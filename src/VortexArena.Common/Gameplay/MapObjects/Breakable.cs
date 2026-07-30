// Port of qcsrc/common/mapobjects/func/breakable.qc (func_breakable, misc_breakablemodel).
//
// "func_breakable - basically func_assault_destructible for general gameplay use." A breakable is a solid
// BSP model with health that, when its health is depleted, breaks: it stops being solid (or swaps to a
// `mdl_dead` wreck model), throws debris, deals optional radius damage, fires its targets, and — if
// `.respawntime` is set — restores itself later.
//
// Now ported in full: takes damage through the real damage pipeline and breaks at <= 0 health, throws
// debris (LaunchDebris) with jitter + fade, deals the RadiusDamage blast (dmg/dmg_edge/dmg_radius/dmg_force),
// fires targets, and respawns after `.respawntime` with the floor-clear trace retry. As with doors/buttons,
// the per-entity event_damage hook is realized by subscribing to the pipeline's <see cref="Combat.Death"/>
// obituary hook. Genuinely client-only: colormod damage indication, waypoint sprites, CSQC model networking.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>func_breakable</c> / <c>misc_breakablemodel</c> — destructible BSP geometry. Setup is a spawnfunc.</summary>
public static class Breakable
{
    // breakable.qh spawnflag bits.
    public const int StartDisabled = 1 << 0;       // START_DISABLED — spawn already broken, trigger to restore
    public const int IndicateDamage = 1 << 1;      // BREAKABLE_INDICATE_DAMAGE
    public const int NoDamage = 1 << 2;            // BREAKABLE_NODAMAGE — only breaks when triggered

    // breakable.qh states.
    public const int StateAlive = 0;   // STATE_ALIVE  (reuses MoverState slot)
    public const int StateBroken = 1;  // STATE_BROKEN

    private static bool _hooked;

    /// <summary><c>spawnfunc(func_breakable)</c> / <c>spawnfunc(misc_breakablemodel)</c>.</summary>
    public static void BreakableSetup(Entity this_)
    {
        EnsureHooked();

        if (this_.Health == 0f)
            this_.Health = 100f;
        this_.MaxHealthMover = this_.Health;

        // QC debris defaults (func_breakable_setup).
        if (this_.DebrisMoveType == MoveType.None) this_.DebrisMoveType = MoveType.Bounce;
        // QC: if(!this.debrissolid) this.debrissolid = SOLID_NOT; — SOLID_NOT is the default (0) already.
        if (this_.DebrisVelocity == Vector3.Zero) this_.DebrisVelocity = new Vector3(0f, 0f, 140f);
        if (this_.DebrisVelocityJitter == Vector3.Zero) this_.DebrisVelocityJitter = new Vector3(70f, 70f, 70f);
        if (this_.DebrisAVelocityJitter == Vector3.Zero) this_.DebrisAVelocityJitter = new Vector3(600f, 600f, 600f);
        if (this_.DebrisTime == 0f) this_.DebrisTime = 3.5f;
        // QC bug (breakable.qc:331): `if(!this.debristimejitter) this.debristime = 2.5;` — an unset
        // debristimejitter CLOBBERS debristime to 2.5 and leaves the jitter at 0. Reproduced faithfully.
        if (this_.DebrisTimeJitter == 0f) this_.DebrisTime = 2.5f;

        if (string.IsNullOrEmpty(this_.Message))
            this_.Message = "got too close to an explosion";
        if (this_.DmgRadius == 0f) this_.DmgRadius = 150f;
        if (this_.DmgForce == 0f) this_.DmgForce = 200f;

        if (string.IsNullOrEmpty(this_.ClassName))
            this_.ClassName = "func_breakable";

        if (Api.Services is not null && !string.IsNullOrEmpty(this_.Model))
            Api.Entities.SetModel(this_, this_.Model);

        if ((this_.SpawnFlags & NoDamage) != 0)
        {
            // NODAMAGE: can't be shot; a trigger toggles it broken/restored.
            this_.TakeDamage = DamageMode.No;
            this_.Use = BreakableDestroyUse;
        }
        else
        {
            this_.TakeDamage = DamageMode.Aim;
            this_.Use = BreakableRestoreUse; // a trigger restores it after it's broken
        }

        MapMover.IndexRegister(this_);

        // QC: this.reset = func_breakable_reset — re-arm on a round/match restart (fired by ResetMapObjects).
        this_.Reset = BreakableReset;

        // reset to the starting look/behavior.
        BreakableReset(this_);
    }

    /// <summary>
    /// QC <c>func_breakable_reset</c> (breakable.qc:307): restore the spawn behaviour on a round restart —
    /// a START_DISABLED breakable goes (back) to broken/non-solid, everything else is restored to a live solid.
    /// (QC also restores team_saved + func_breakable_look_restore — the team field isn't carried at this layer
    /// and the colormod look is presentation-only, so only the behave state is reproduced.)
    /// </summary>
    public static void BreakableReset(Entity this_)
    {
        if ((this_.SpawnFlags & StartDisabled) != 0)
            BehaveDestroyed(this_);
        else
            BehaveRestore(this_);
    }

    /// <summary>QC <c>func_breakable_behave_restore</c>: re-arm as a live, damageable solid.</summary>
    private static void BehaveRestore(Entity this_)
    {
        this_.Health = this_.MaxHealthMover;
        this_.MoverState = StateAlive;
        this_.DeadState = DeadFlag.No;
        this_.Solid = Solid.Bsp;
        if ((this_.SpawnFlags & NoDamage) == 0)
            this_.TakeDamage = DamageMode.Aim;
        MapMover.SetOrigin(this_, this_.Origin); // relink
    }

    /// <summary>QC <c>func_breakable_behave_destroyed</c>: mark broken, non-solid, not damageable.</summary>
    private static void BehaveDestroyed(Entity this_)
    {
        this_.Health = this_.MaxHealthMover;
        this_.TakeDamage = DamageMode.No;
        this_.MoverState = StateBroken;
        this_.Solid = Solid.Not;
        MapMover.SetOrigin(this_, this_.Origin); // unlink as a solid
    }

    /// <summary>
    /// QC <c>func_breakable_destroy</c>: throw debris, blast, fire targets, then either remove (no respawn)
    /// or schedule a restore. <paramref name="actor"/> is the credited attacker; <paramref name="force"/> is
    /// the killing hit's knockback (QC global <c>debrisforce</c>, used to fling the debris).
    /// </summary>
    public static void BreakableDestroy(Entity this_, Entity? actor, Vector3 force)
    {
        if (this_.MoverState == StateBroken)
            return; // already broken

        // throw the debris pieces (QC tokenizes .debris and launches each model).
        if (!string.IsNullOrEmpty(this_.Debris))
            foreach (string model in this_.Debris.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                LaunchDebris(this_, model, force);

        BehaveDestroyed(this_);

        MapMover.Sound(this_, SoundChannel.Auto, this_.Noise);

        // radius-damage blast on break (dmg/dmg_edge/dmg_radius/dmg_force).
        if (this_.Dmg != 0f)
            WeaponSplash.RadiusDamage(this_, (this_.AbsMin + this_.AbsMax) * 0.5f,
                this_.Dmg, this_.DmgEdge, this_.DmgRadius, actor, 0, this_.DmgForce);

        // fire targets (QC blanks .message so the generic squish message isn't printed here).
        string oldMsg = this_.Message;
        this_.Message = "";
        MapMover.UseTargets(this_, actor, null);
        this_.Message = oldMsg;

        if (this_.RespawnTimeMover != 0f)
        {
            this_.Think = RestoreSelf;
            this_.NextThink = MapMover.Now() + this_.RespawnTimeMover + Prandom.Signed() * this_.RespawnTimeJitter;
        }
    }

    /// <summary>
    /// Port of <c>LaunchDebris</c> (breakable.qc): spawn one debris entity at a random point inside the
    /// breakable's bbox, fling it with the base velocity + per-axis jitter + the killing force, give it a
    /// random spin, and schedule its fade-out (SUB_SetFade).
    /// </summary>
    private static void LaunchDebris(Entity this_, string debrisName, Vector3 force)
    {
        if (Api.Services is null)
            return;

        Entity dbr = Api.Entities.Spawn();
        dbr.ClassName = "debris";
        Vector3 org = this_.AbsMin + new Vector3(
            Prandom.Float() * (this_.AbsMax.X - this_.AbsMin.X),
            Prandom.Float() * (this_.AbsMax.Y - this_.AbsMin.Y),
            Prandom.Float() * (this_.AbsMax.Z - this_.AbsMin.Z));
        MapMover.SetOrigin(dbr, org);
        if (!string.IsNullOrEmpty(debrisName))
            Api.Entities.SetModel(dbr, debrisName);
        dbr.Owner = this_; // not affected by our own explosion
        dbr.MoveType = this_.DebrisMoveType;
        dbr.Solid = this_.DebrisSolid;
        if (dbr.Solid != Solid.Bsp)
            MapMover.SetSize(dbr, Vector3.Zero, Vector3.Zero); // perf: point-sized non-BSP debris

        dbr.Velocity = new Vector3(
            this_.DebrisVelocity.X + this_.DebrisVelocityJitter.X * Prandom.Signed(),
            this_.DebrisVelocity.Y + this_.DebrisVelocityJitter.Y * Prandom.Signed(),
            this_.DebrisVelocity.Z + this_.DebrisVelocityJitter.Z * Prandom.Signed());
        dbr.Velocity += force; // QC scales by .debrisdamageforcescale; default 0 => no force kick, but force is set
        dbr.Angles = this_.Angles;
        dbr.AVelocity = new Vector3(
            Prandom.Float() * this_.DebrisAVelocityJitter.X,
            Prandom.Float() * this_.DebrisAVelocityJitter.Y,
            Prandom.Float() * this_.DebrisAVelocityJitter.Z);

        MapMover.SubSetFade(dbr,
            MapMover.Now() + this_.DebrisTime + Prandom.Signed() * this_.DebrisTimeJitter,
            this_.DebrisFadeTime);
    }

    /// <summary>
    /// QC <c>func_breakable_restore_self</c>: only rebuild when no creature is standing inside; otherwise
    /// retry every 5 seconds (the volume is traced with a body-only contents mask).
    /// </summary>
    private static void RestoreSelf(Entity this_)
    {
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(this_.Origin, this_.Mins, this_.Maxs, this_.Origin, MoveFilter.Normal, this_);
            if (tr.StartSolid || tr.Fraction < 1f)
            {
                this_.NextThink = MapMover.Now() + 5f; // area still occupied — retry
                return;
            }
        }
        BehaveRestore(this_);
        this_.Think = null;
        this_.NextThink = 0f;
    }

    /// <summary>QC <c>func_breakable_destroy</c> as a .use (BREAKABLE_NODAMAGE trigger-to-break, toggles).</summary>
    private static void BreakableDestroyUse(Entity self, Entity actor)
    {
        if (self.MoverState == StateBroken)
        {
            // a second trigger restores it (QC toggles).
            BehaveRestore(self);
            return;
        }
        BreakableDestroy(self, actor, Vector3.Zero);
    }

    /// <summary>QC <c>func_breakable_restore</c> as a .use: a trigger rebuilds a broken breakable.</summary>
    private static void BreakableRestoreUse(Entity self, Entity actor)
    {
        BehaveRestore(self);
    }

    /// <summary>
    /// Subscribe to the damage pipeline's death hook once, so a breakable that the generic pipeline kills
    /// (health &lt; 1) actually breaks. This stands in for the un-ported per-entity <c>event_damage</c> hook.
    /// </summary>
    private static void EnsureHooked()
    {
        if (_hooked)
            return;
        _hooked = true;
        Combat.Death.Add(OnDeath);
    }

    /// <summary>
    /// <see cref="Combat.Death"/> handler: when a func_breakable / misc_breakablemodel is "killed" by the
    /// damage pipeline, run its break. Returns false (non-exclusive) so other death subscribers still run.
    /// </summary>
    private static bool OnDeath(ref DeathEvent ev)
    {
        Entity v = ev.Victim;
        if (v.ClassName is "func_breakable" or "misc_breakablemodel")
        {
            // QC func_breakable_damage NOSPLASH guard: a NOSPLASH breakable ignores indirect (splash) blast
            // unless the deathtype is special (non-weapon). DEATH_ISSPECIAL(dt) && HITTYPE_SPLASH -> return.
            if ((v.SpawnFlags & MapMover.SpawnNoSplash) != 0
                && DeathTypes.HasHitType(ev.DeathType, DeathTypes.Splash)
                && !DeathTypes.IsSpecial(ev.DeathType))
            {
                // restore HP so the resuscitate-to-HP>=1 early-out keeps the brush alive (splash bounced off).
                v.Health = v.MaxHealthMover;
                return false;
            }

            // QC func_breakable_damage team friendly-fire guard: a teamed breakable can't be broken by an
            // attacker on the same team (this.team && attacker.team == this.team -> return).
            if (v.Team != 0f && ev.Attacker is { } atk && atk.Team == v.Team)
            {
                v.Health = v.MaxHealthMover;
                return false;
            }

            // QC keeps the entity alive (it respawns / can be restored) — restore HP so the kill path's
            // "resuscitated to HP>=1, don't die" early-out leaves the brush intact, then break it.
            v.Health = v.MaxHealthMover;
            // QC sets the debris force from the killing hit's `force`; the Death hook doesn't carry it, and
            // .debrisdamageforcescale defaults to 0 (so QC's force contribution is normally nil anyway).
            BreakableDestroy(v, ev.Attacker, Vector3.Zero);
        }
        return false;
    }
}
