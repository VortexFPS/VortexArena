// Port of qcsrc/common/mapobjects/misc/laser.qc (misc_laser) — SVQC half.
//
// A laser beams from its origin toward .target (a target_position) or along its angles, hurting whatever
// the beam hits (.dmg per second; -1 = instant kill) and/or acting as a DETECTOR (when its target_position
// itself has a target, SUB_UseTargets fires on BOTH the enter and exit edges of a creature crossing the
// beam — toggle semantics, laser.qc:90-111).
//
// Port notes:
//  * QC resolves the end effect to a particle-effect NUMBER (.cnt); the port resolves effects BY NAME, so
//    the resolved name rides Entity.LaserEndEffect ("" = none, QC cnt -1). A raw numeric `cnt` map key
//    (no shipped map uses one) is not honored.
//  * QC's detector latch reuses .count; `count` is a plumbed map key on this Entity, so the latch is the
//    dedicated Entity.LaserHitLatch bool (deviation: a mapper-set `count` no longer pre-latches — obscure).
//  * INITPRIO_FINDTARGET (deferred enemy lookup) runs lazily on the first think instead.
//  * Net_LinkEntity/laser_SendEntity are unnecessary: the client renderer (game/client/LaserRenderer.cs)
//    reads the shared entity off the ambient facade (the listen-server/demo seam every client fx uses).
//  * The beam VISUAL (Draw_Laser) is client-side — see game/client/LaserRenderer.cs.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>misc_laser</c> — the hazard/detector laser beam. Registered by <see cref="MapObjectsRegistry"/>.</summary>
public static class Laser
{
    // ---- spawnflags (laser.qh:16-18 + defs.qh START_ENABLED) ----
    public const int StartOn = 1 << 0;        // START_ON / START_ENABLED
    public const int Finite = 1 << 1;         // LASER_FINITE (DEST_IS_FIXED) — endpoint is exactly enemy.origin
    public const int NoTrace = 1 << 2;        // LASER_NOTRACE — client skips the beam trace
    public const int InvertTeam = 1 << 3;     // LASER_INVERT_TEAM — flips the team damage gate

    /// <summary>QC LASER_BEAM_MAXLENGTH (laser.qh:32) — the server/clipped beam trace length.</summary>
    public const float BeamMaxLength = 32768f;
    /// <summary>QC LASER_BEAM_MAXWORLDSIZE (laser.qh:34) — the client sky-hit beam extension.</summary>
    public const float BeamMaxWorldSize = 1048576f;

    /// <summary><c>spawnfunc(misc_laser)</c> (laser.qc:215-288).</summary>
    public static void LaserSetup(Entity this_)
    {
        this_.ClassName = "misc_laser";

        // --- end-effect resolution (laser.qc:217-236), by NAME port-side ---
        if (!string.IsNullOrEmpty(this_.Mdl))
        {
            if (this_.Mdl == "none")
            {
                this_.LaserEndEffect = ""; // QC cnt = -1
            }
            else
            {
                this_.LaserEndEffect = this_.Mdl;
                // QC: _particleeffectnum(mdl) < 0 && dmg => EFFECT_LASER_DEADLY. The server-side stand-in for
                // "unresolvable" is the effect registry (the client additionally knows raw effectinfo names and
                // renders them fine, so this only matters for genuinely unknown names on a damaging laser).
                if (this_.Dmg != 0f
                    && Effects.ByName(this_.Mdl) is null
                    && Effects.ByEffectInfoName(this_.Mdl) is null)
                {
                    this_.LaserEndEffect = "laser_deadly";
                }
            }
        }
        else
        {
            // QC `else if(!this.cnt)`: dmg lasers default to EFFECT_LASER_DEADLY, harmless ones to none.
            this_.LaserEndEffect = this_.Dmg != 0f ? "laser_deadly" : "";
        }

        // --- legacy 'colormod' aliases beam_color (laser.qc:238-242) ---
        if (this_.BeamColor == Vector3.Zero && this_.ColorModKey != Vector3.Zero)
            this_.BeamColor = this_.ColorModKey;

        // --- beam_color defaults red ONLY when both color and alpha are unset (laser.qc:244-248) ---
        if (this_.BeamColor == Vector3.Zero && this_.AlphaKey == 0f)
            this_.BeamColor = new Vector3(1f, 0f, 0f);

        // --- obituary messages (laser.qc:250-257) ---
        if (string.IsNullOrEmpty(this_.Message))
            this_.Message = "saw the light";
        if (string.IsNullOrEmpty(this_.Message2))
            this_.Message2 = "was pushed into a laser by";

        // --- scale (beam radius) / modelscale (dlight radius) defaults (laser.qc:258-269) ---
        if (this_.ScaleFactor == 0f)
            this_.ScaleFactor = 1f;
        if (this_.ModelScale == 0f)
            this_.ModelScale = 1f;
        else if (this_.ModelScale < 0f)
            this_.ModelScale = 0f;

        this_.MoveType = MoveType.None;
        this_.Think = LaserThink;
        this_.NextThink = MapMover.Now();

        this_.MAngle = this_.Angles; // laser.qc:274

        this_.SetActive = LaserSetActive;

        if (!string.IsNullOrEmpty(this_.TargetName))
            this_.Use = LaserUse; // backwards compatibility (laser.qc:280-284)

        // generic_netlinked_reset (triggers.qc:76-95): targetname set => active iff START_ON; else always on.
        if (!string.IsNullOrEmpty(this_.TargetName))
            this_.Active = (this_.SpawnFlags & StartOn) != 0 ? MapMover.ActiveActive : MapMover.ActiveNot;
        else
            this_.Active = MapMover.ActiveActive;

        MapMover.IndexRegister(this_);
    }

    /// <summary>Port of <c>misc_laser_aim</c> (laser.qc:12-49) — recompute the stored mangle.</summary>
    internal static void LaserAim(Entity this_)
    {
        if (this_.Enemy is { } enemy)
        {
            if ((this_.SpawnFlags & Finite) != 0)
            {
                this_.MAngle = enemy.Origin; // FINITE: mangle holds the endpoint POSITION (QC quirk)
            }
            else
            {
                // QC: a = vectoangles(enemy.origin - origin); a_x = -a_x. The port's VecToAngles keeps DP's
                // inverted pitch (see the QMath pitch-convention note), so the explicit negation here makes the
                // pair round-trip through QMath.Forward exactly as makevectors(mangle) does in QC.
                Vector3 a = QMath.VecToAngles(enemy.Origin - this_.Origin);
                a.X = -a.X;
                this_.MAngle = a;
            }
        }
        else
        {
            this_.MAngle = this_.Angles;
        }
    }

    /// <summary>Port of <c>misc_laser_think</c> (laser.qc:58-121) — runs every server frame.</summary>
    public static void LaserThink(Entity this_)
    {
        this_.NextThink = MapMover.Now();

        if (this_.Active == MapMover.ActiveNot)
            return;

        // INITPRIO_FINDTARGET (laser.qc:51-55, 272), run lazily on the first active think.
        if (!this_.LaserTargetSearched)
        {
            this_.LaserTargetSearched = true;
            if (!string.IsNullOrEmpty(this_.Target))
                this_.Enemy = MapMover.FindFirstByTargetName(this_.Target);
        }

        LaserAim(this_);

        // --- endpoint (laser.qc:71-81) ---
        Vector3 o;
        Entity? enemy = this_.Enemy;
        if (enemy is not null)
        {
            o = enemy.Origin;
            if ((this_.SpawnFlags & Finite) == 0)
                o = this_.Origin + QMath.Normalize(o - this_.Origin) * BeamMaxLength;
        }
        else
        {
            o = this_.Origin + QMath.Forward(this_.MAngle) * BeamMaxLength;
        }

        // --- trace, gated exactly as QC: only when damaging or a detector (laser.qc:83-88) ---
        bool detector = enemy is not null && !string.IsNullOrEmpty(enemy.Target);
        Entity? hitent = null;
        Vector3 hitloc = o;
        if ((this_.Dmg != 0f || detector) && Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(this_.Origin, Vector3.Zero, Vector3.Zero, o, MoveFilter.Normal, this_);
            hitent = tr.Ent;
            hitloc = tr.EndPos;
        }

        // --- DETECTOR laser (laser.qc:90-111): SUB_UseTargets on BOTH the enter and exit edge ---
        if (detector)
        {
            if (hitent is not null && MapMover.IsCreature(hitent))
            {
                this_.Pusher = hitent;
                if (!this_.LaserHitLatch)
                {
                    this_.LaserHitLatch = true;
                    // QC passes this.enemy.pusher as the activator (laser.qc:99 — the target_position's
                    // .pusher, normally NULL). Mirrored verbatim, quirk included.
                    MapMover.UseTargets(enemy!, enemy!.Pusher, null);
                }
            }
            else if (this_.LaserHitLatch)
            {
                this_.LaserHitLatch = false;
                MapMover.UseTargets(enemy!, enemy!.Pusher, null);
            }
        }

        // --- damage (laser.qc:113-120) ---
        if (this_.Dmg != 0f && hitent is not null)
        {
            // EXACT QC truth table (laser.qc:115-117): no INVERT => return when teams DIFFER (a team-stamped
            // laser damages only SAME-team touchers); INVERT => return when teams MATCH. Counterintuitive but
            // verbatim — do not "fix".
            if (this_.Team != 0f)
                if (((this_.SpawnFlags & InvertTeam) == 0) == (this_.Team != hitent.Team))
                    return;

            if (hitent.TakeDamage != DamageMode.No)
            {
                float amount = this_.Dmg < 0f ? 100000f : this_.Dmg * MapMover.FrameTime();
                // DEATH_HURTTRIGGER → DeathTypes.Void, the same mapping trigger_hurt uses.
                Combat.Damage(hitent, this_, this_, amount, DeathTypes.Void, hitloc, Vector3.Zero);
            }
        }
    }

    /// <summary>Port of <c>laser_setactive</c> (laser.qc:184-208).</summary>
    private static void LaserSetActive(Entity self, int act)
    {
        int old = self.Active;
        if (act == MapMover.ActiveToggle)
            self.Active = self.Active == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
        else
            self.Active = act;

        if (self.Active != old)
            LaserAim(self); // QC re-aims (and nets the update — implicit here via the shared entity)
    }

    /// <summary>Port of <c>laser_use</c> (laser.qc:210-213).</summary>
    private static void LaserUse(Entity self, Entity actor)
    {
        if (self.SetActive is { } sa)
            sa(self, MapMover.ActiveToggle);
        else
            LaserSetActive(self, MapMover.ActiveToggle);
    }
}
