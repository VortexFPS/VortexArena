// Port of qcsrc/common/mapobjects/func/plat.qc (+ the plat half of platforms.qc).
//
// A func_plat is a MOVETYPE_PUSH brush that rides between a raised position (pos1 = spawn origin) and a
// lowered position (pos2 = pos1 - height on Z). It starts lowered (STATE_BOTTOM); a creature stepping onto
// its center trigger raises it (plat_go_up); at the top it waits, then lowers (plat_go_down). Blocking it
// crushes (CRUSH spawnflag) or bites for `.dmg`, reversing.
//
// Core behavior ported: the up/down state machine via SUB_CalcMove, the spawned center touch trigger, the
// crush/bite-on-block handler, and the reset — including the targetname "start raised" variant (a targeted
// plat spawns at the top in STATE_UP and a trigger sends it down via plat_use). Genuinely out of scope:
// platmovetype easing details beyond the shared CalcMove bezier, and CSQC networking.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>func_plat</c> — a vertically-riding platform. Registered as a spawnfunc (<see cref="PlatSetup"/>).</summary>
public static class Platforms
{
    // platforms.qh / defs.qh spawnflag bits used by plats.
    public const int Crush = 1 << 2;        // CRUSH — instakill blockers
    public const int LowTrigger = 1 << 0;   // PLAT_LOW_TRIGGER

    /// <summary><c>spawnfunc(func_plat)</c>.</summary>
    public static void PlatSetup(Entity this_)
    {
        // QC's q3compat branch (spawnflags=0, dmg default 2, medplat sounds). q3compat is the live per-map flag
        // (CompatRemaps.IsQ3Compat), set from the .arena/.defi map-pack probe at boot — true on a Q3/Q3DF import.
        // (The CPMA sound_start/sound_end + pt1_strt/pt1_end map-pack file probe stays a documented gap — it needs
        // GetField_fullspawndata / FindFileInMapPack, unavailable headless; the medplat default still applies.)
        bool q3compat = CompatRemaps.IsQ3Compat;

        if (q3compat)
        {
            this_.SpawnFlags = 0;           // Q3 plats have no spawnflags
            if (this_.Dmg == 0f) this_.Dmg = 2f;
        }
        else if ((this_.SpawnFlags & Crush) != 0)
        {
            this_.Dmg = 10000f;
        }

        if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message))
            this_.Message = "was squished";
        if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message2))
            this_.Message2 = "was squished by";

        // Sounds: Base plat.qc sets .noise/.noise1 ONLY for sounds==1 (plat1/2), sounds==2 or q3compat (medplat),
        // then legacy sound1/sound2 overrides — a func_plat with .sounds unset (0) stays SILENT in Base. Do NOT
        // force a plat1/plat2 default here (old-Quake behavior Xonotic dropped); ApplyPlatSounds reproduces Base.
        MapMover.ApplyPlatSounds(this_, q3compat);  // sounds==1 -> plat1/2, ==2/q3 -> medplat, then sound1/2 overrides

        // QC stores angles into mangle then clears angles (plats don't rotate via .angles).
        this_.MAngle = this_.Angles;
        this_.Angles = Vector3.Zero;

        this_.ClassName = "plat";
        if (!MapMover.InitMovingBrushTrigger(this_))
            return;

        // QC parses the .platmovetype spawn-key into platmovetype_start/_end here (movers default linear).
        // Base set_platmovetype objerrors (rejects the mover) on a reverse/insane curve; the headless port returns
        // false. Mirror the train path (MovingBrushes.cs:332) and bail out so a malformed func_plat is inert
        // rather than silently keeping the bad curve.
        if (!MapMover.SetPlatMoveType(this_, this_.Platmovetype))
            return;

        this_.Blocked = PlatCrush;

        if (this_.Speed == 0f) this_.Speed = q3compat ? 200f : 150f;
        if (this_.Lip == 0f) this_.Lip = q3compat ? 8f : 16f;
        if (this_.Height == 0f) this_.Height = this_.Size.Z - this_.Lip;

        // pos1 = top (spawn), pos2 = pos1 lowered by height.
        this_.Pos1 = this_.Origin;
        this_.Pos2 = this_.Origin;
        this_.Pos2.Z = this_.Origin.Z - this_.Height;

        MapMover.IndexRegister(this_);

        this_.Reset = PlatReset; // QC plat.qc: this.reset = plat_reset (round-restart re-arm)
        PlatReset(this_);

        // The "start moving" trigger that detects a creature standing on the plat (QC plat_delayedinit).
        // QC: Q3 uses only a .targetname truth check to decide whether to spawn the inside trigger — a TARGETED
        // Q3/Q3DF plat is driven by its trigger (plat_target_use) and gets no ride-detect volume (plat.qc:10-12).
        if (!q3compat || string.IsNullOrEmpty(this_.TargetName))
            SpawnInsideTrigger(this_);
    }

    /// <summary>
    /// QC <c>plat_reset</c>: a TARGETED plat starts RAISED (at pos1, STATE_UP) and is sent down by a trigger
    /// (<see cref="PlatUse"/>); an untargeted plat starts at the bottom (STATE_BOTTOM) to be ridden up.
    /// </summary>
    public static void PlatReset(Entity this_)
    {
        // q3compat = the live per-map flag (set from the .arena/.defi probe). On a Q3/Q3DF import a TARGETED plat
        // takes the ground-plat fork (start at the bottom, PlatTargetUse re-raises) instead of starting raised.
        bool q3compat = CompatRemaps.IsQ3Compat;

        if (!string.IsNullOrEmpty(this_.TargetName) && !q3compat)
        {
            MapMover.SetOrigin(this_, this_.Pos1);
            this_.MoverState = MapMover.StateUp;   // QC sets STATE_UP for a start-raised plat
            this_.Use = PlatUse;
        }
        else
        {
            MapMover.SetOrigin(this_, this_.Pos2);
            this_.MoverState = MapMover.StateBottom;
            // QC: a targeted Q3COMPAT ground plat uses plat_target_use; everything else plat_trigger_use.
            this_.Use = (!string.IsNullOrEmpty(this_.TargetName) && q3compat)
                ? PlatTargetUse
                : PlatTriggerUse;
        }
    }

    /// <summary>QC <c>plat_use</c>: a trigger sends a start-raised plat down (one-shot).</summary>
    public static void PlatUse(Entity self, Entity actor)
    {
        self.Use = null;
        if (self.MoverState != MapMover.StateUp)
            return; // QC objerrors "plat_use: not in up state"; headless: ignore
        PlatGoDown(self);
    }

    /// <summary>
    /// QC <c>plat_target_use</c>: a Q3COMPAT targeted ground plat's re-raise/refresh handler — a topped plat
    /// refreshes its dwell, otherwise (any non-up state) it raises. Installed by <see cref="PlatReset"/> on the
    /// (targetname &amp;&amp; q3compat) fork; reachable on a Q3/Q3DF targeted plat (q3compat is the live flag).
    /// </summary>
    public static void PlatTargetUse(Entity self, Entity actor)
    {
        if (self.MoverState == MapMover.StateTop)
            self.NextThink = self.LTime + 1f;
        else if (self.MoverState != MapMover.StateUp)
            PlatGoUp(self);
    }

    /// <summary>
    /// QC <c>plat_outside_touch</c>: a mapper-wired outside trigger sends a topped plat down. Same live-creature +
    /// health guard as the center touch; stock func_plat never spawns this trigger, so it is custom-wiring only.
    /// </summary>
    public static void PlatOutsideTouch(Entity self, Entity toucher)
    {
        if (!MapMover.IsCreature(toucher))
            return;
        if (toucher.GetResource(ResourceType.Health) <= 0f)
            return;

        Entity plat = self.Enemy!;
        if (plat.MoverState == MapMover.StateTop)
            PlatGoDown(plat);
    }

    /// <summary>
    /// QC <c>plat_spawn_inside_trigger</c>: spawn a SOLID_TRIGGER volume over the plat whose touch
    /// (<see cref="PlatCenterTouch"/>) raises it. We size it from the plat's absmin/absmax like QC.
    /// </summary>
    private static void SpawnInsideTrigger(Entity this_)
    {
        if (Api.Services is null)
            return;

        Entity trigger = Api.Entities.Spawn();
        trigger.ClassName = "plat_trigger";
        trigger.Touch = PlatCenterTouch;
        trigger.MoveType = MoveType.None;
        trigger.Solid = Solid.Trigger;
        trigger.Enemy = this_; // points back at the plat

        Vector3 tmin = this_.AbsMin + new Vector3(25f, 25f, 0f);
        Vector3 tmax = this_.AbsMax - new Vector3(25f, 25f, -8f);
        tmin.Z = tmax.Z - (this_.Pos1.Z - this_.Pos2.Z + 8f);
        if ((this_.SpawnFlags & LowTrigger) != 0)
            tmax.Z = tmin.Z + 8f;

        if (this_.Size.X <= 50f)
        {
            tmin.X = (this_.Mins.X + this_.Maxs.X) / 2f;
            tmax.X = tmin.X + 1f;
        }
        if (this_.Size.Y <= 50f)
        {
            tmin.Y = (this_.Mins.Y + this_.Maxs.Y) / 2f;
            tmax.Y = tmin.Y + 1f;
        }

        // QC rejects a degenerate volume (delete + objerror "plat_spawn_inside_trigger: platform has too small
        // a height"). Headless: drop the trigger rather than register an inverted box.
        if (tmin.X >= tmax.X || tmin.Y >= tmax.Y || tmin.Z >= tmax.Z)
        {
            MapMover.RemoveEntity(trigger);
            return;
        }

        Api.Entities.SetSize(trigger, tmin, tmax);
    }

    // ================= state machine =================

    /// <summary>QC <c>plat_go_down</c>: lower toward pos2.</summary>
    public static void PlatGoDown(Entity this_)
    {
        // Base plat_go_down uses CH_TRIGGER_SINGLE (=3); the port's Item slot = 3 is that channel.
        MapMover.Sound(this_, SoundChannel.Item, this_.Noise);
        this_.MoverState = MapMover.StateDown;
        MapMover.CalcMove(this_, this_.Pos2, MapMover.SpeedType.Linear, this_.Speed, PlatHitBottom);
    }

    /// <summary>QC <c>plat_go_up</c>: raise toward pos1.</summary>
    public static void PlatGoUp(Entity this_)
    {
        // Base plat_go_up uses CH_TRIGGER_SINGLE (=3); the port's Item slot = 3 is that channel.
        MapMover.Sound(this_, SoundChannel.Item, this_.Noise);
        this_.MoverState = MapMover.StateUp;
        MapMover.CalcMove(this_, this_.Pos1, MapMover.SpeedType.Linear, this_.Speed, PlatHitTop);
    }

    /// <summary>QC <c>plat_hit_top</c>: reached the top; wait 3s then go down.</summary>
    private static void PlatHitTop(Entity this_)
    {
        // Base plat_hit_top uses CH_TRIGGER_SINGLE (=3); the port's Item slot = 3 is that channel.
        MapMover.Sound(this_, SoundChannel.Item, this_.Noise1);
        this_.MoverState = MapMover.StateTop;
        this_.Think = PlatGoDown;
        this_.NextThink = this_.LTime + 3f;
    }

    /// <summary>QC <c>plat_hit_bottom</c>: reached the bottom and rests.</summary>
    private static void PlatHitBottom(Entity this_)
    {
        // Base plat_hit_bottom uses CH_TRIGGER_SINGLE (=3); the port's Item slot = 3 is that channel.
        MapMover.Sound(this_, SoundChannel.Item, this_.Noise1);
        this_.MoverState = MapMover.StateBottom;
    }

    /// <summary>QC <c>plat_center_touch</c>: a live creature on the plat raises it (or refreshes its top wait).</summary>
    public static void PlatCenterTouch(Entity self, Entity toucher)
    {
        if (!MapMover.IsCreature(toucher))
            return;
        if (toucher.GetResource(ResourceType.Health) <= 0f)
            return;

        Entity plat = self.Enemy!;
        if (plat.MoverState == MapMover.StateBottom)
            PlatGoUp(plat);
        else if (plat.MoverState == MapMover.StateTop)
            plat.NextThink = plat.LTime + 1f; // refresh the top dwell
    }

    /// <summary>QC <c>plat_trigger_use</c>: external trigger sends the plat down (if idle).</summary>
    public static void PlatTriggerUse(Entity self, Entity actor)
    {
        if (self.Think is not null)
            return; // already activated/moving
        PlatGoDown(self);
    }

    /// <summary>QC <c>plat_crush</c>: crush (CRUSH) or bite (`.dmg`) a blocker, then reverse.</summary>
    public static void PlatCrush(Entity self, Entity blocker)
    {
        if ((self.SpawnFlags & Crush) != 0 && blocker.TakeDamage != DamageMode.No)
        {
            Combat.Damage(blocker, self, self, 10000f, DeathTypes.Void, blocker.Origin, Vector3.Zero);
        }
        else
        {
            if (self.Dmg != 0f && blocker.TakeDamage != DamageMode.No)
            {
                Combat.Damage(blocker, self, self, self.Dmg, DeathTypes.Void, blocker.Origin, Vector3.Zero);
                if (MapMover.IsDead(blocker))
                    Combat.Damage(blocker, self, self, 10000f, DeathTypes.Void, blocker.Origin, Vector3.Zero);
            }

            if (self.MoverState == MapMover.StateUp)
                PlatGoDown(self);
            else if (self.MoverState == MapMover.StateDown)
                PlatGoUp(self);
        }
    }
}
