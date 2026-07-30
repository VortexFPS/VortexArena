using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The guided-missile think chains the homing turrets share — faithful ports of
/// <c>turret_hellion_missile_think</c> (hellion_weapon.qc), <c>turret_hk_missile_think</c> (hk_weapon.qc) and
/// <c>walker_rocket_think</c> (walker_weapon.qc). Each is a per-frame steering+acceleration step run off the
/// projectile's <see cref="Entity.Think"/>; on intercept (or fuel-out) it detonates via the explode action
/// the spawner installed (the radius-damage closure from <see cref="TurretSpawn.Projectile"/> /
/// <see cref="TurretMissiles"/>).
///
/// Per-projectile guidance scratch (QC flat fields on the missile edict) lives on
/// <see cref="GuidanceState"/>, attached lazily. Godot-free; operates only on <see cref="Entity"/> +
/// <see cref="Api"/>. Only the CSQC trail/smoke is left to the client.
/// </summary>
public static class GuidedProjectile
{
    /// <summary>Per-missile guidance scratch (QC .tur_aimpos jitter, .cnt fuel/guide timers, .max_health TTL, .shot_* speed band).</summary>
    public sealed class GuidanceState
    {
        public float ExpireTime;     // QC .max_health / .cnt — absolute time the missile self-destructs
        public float GuideJitterAt;  // QC .cnt for walker — next time to re-roll the aim jitter
        public Vector3 AimJitter;    // QC .tur_shotorg / .tur_aimpos — random offset added to the enemy aim point
        public float Speed;          // launch speed (for the accel band)
        public float SpeedMax;       // QC shot_speed_max
        public float SpeedGain;      // QC shot_speed_gain (hellion accel multiplier)
        public float TurnRate;       // QC shot_speed_turnrate / rocket_turnrate
        public float Radius;         // QC owner.shot_radius (proximity-detonate distance basis)
        public float FuelTime;       // QC .cnt for hk — time the boosted accel band lasts
        public float Accel = 1.025f; // QC shot_speed_accel  (decel-trigger climb / near-wall accel)
        public float Accel2 = 1.05f; // QC shot_speed_accel2 (clear-path + boosted-fuel accel)
        public float Decel = 0.9f;   // QC shot_speed_decel  (near-wall / sharp-turn brake)
        public bool Inert;           // QC hk fuel-out: rocket went MOVETYPE_BOUNCE + stopped thinking (a dud)
        public bool InLoop;          // QC walker .shot_dmg == 1337 sentinel: rocket is mid up-and-over loop (skips re-entry roll)
        public Vector3 LoopTarget;   // QC walker .tur_shotorg overload while looping: the loop waypoint to steer to
        public float WalkerSpeed;    // launch speed captured for the walker loop steps (QC rocket_speed)
        public float WalkerTurnRate; // turn rate captured for the walker loop steps (QC rocket_turnrate)
        public Action<Entity>? Explode;  // the detonation closure (radius damage + remove)
    }

    private static readonly Dictionary<Entity, GuidanceState> _g = new();

    public static GuidanceState Guidance(Entity e)
    {
        if (!_g.TryGetValue(e, out var s)) { s = new GuidanceState(); _g[e] = s; }
        return s;
    }

    public static void Forget(Entity e) => _g.Remove(e);

    /// <summary>How a guided missile steers (selects the per-frame think).</summary>
    public enum Mode { Hellion, Hk, WalkerRocket }

    /// <summary>
    /// Spawn a guided turret missile: a projectile that flies under one of the guidance models toward
    /// <paramref name="enemy"/> and detonates (radius damage) on intercept, touch, or fuel-out. Wires the
    /// <see cref="GuidanceState"/> speed band + TTL and the detonation closure. Returns the missile.
    /// </summary>
    public static Entity Launch(Entity turret, Entity? enemy, Vector3 origin, Vector3 dir, Mode mode,
        float launchSpeed, float speedMax, float speedGain, float turnRate, float size, float health,
        float damage, float radius, float force, string deathType, float ttl,
        float accel = 1.025f, float accel2 = 1.05f, float decel = 0.9f, float fuelTime = 30f)
    {
        Entity m = Api.Entities.Spawn();
        m.ClassName = "turret_guided";
        // QC hellion/hk/walker fire turret_projectile(..., PROJECTILE_ROCKET, ...) (and walker.qc:244
        // CSQCProjectile(..., PROJECTILE_ROCKET, ...)). The port has no CSQC turret edict, but the missile IS
        // networked + classified by the shared ProjectileCatalog (keyed on the networked classname+netname).
        // Stamp the netname so the client gives the homing missile the rocket smoke trail + rocket-fly loop
        // instead of the Generic fallback. (All three guided turret rockets use PROJECTILE_ROCKET.)
        m.NetName = "rocket";
        m.Owner = turret;
        m.Enemy = enemy;
        m.Team = turret.Team;
        m.MoveType = mode == Mode.Hk ? MoveType.BounceMissile : MoveType.FlyMissile;
        // QC turret_projectile PROJECTILE_MAKETRIGGER (SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE): the
        // HK missile spawns via turret_projectile (hk_weapon.qc:29) so it inherits the maketrigger — transparent
        // to the firing turret's bbox so it can't collide with / detonate on its launcher.
        Projectiles.MakeTrigger(m);
        m.Flags = EntFlags.Item;
        m.Velocity = QMath.Normalize(dir) * launchSpeed;
        m.Angles = QMath.VecToAngles(m.Velocity);

        Vector3 half = new Vector3(0.5f, 0.5f, 0.5f) * size;
        Api.Entities.SetSize(m, -half, half);
        Api.Entities.SetOrigin(m, origin);

        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // Detonation: radius damage + remove (QC turret_projectile_explode / *_rocket_explode).
        void Explode(Entity self)
        {
            self.Touch = null;
            self.Think = null;
            self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, damage, 0f, radius, self.Owner, 0, force, deathTag: deathType);
            Forget(self);
            Api.Entities.Remove(self);
        }

        if (health > 0f)
        {
            // Shootable hull (FLAC shoots these down). The headless DamageSystem has no per-entity
            // event_damage hook, so detonation-when-destroyed flows through the shared death hook below.
            m.TakeDamage = DamageMode.Yes;
            m.Health = health;
            m.SetResourceExplicit(ResourceType.Health, health);
            EnsureDeathHook();
        }
        else
        {
            m.TakeDamage = DamageMode.No;
            m.Flags |= EntFlags.NoTarget;
        }

        GuidanceState g = Guidance(m);
        g.Speed = launchSpeed;
        g.SpeedMax = speedMax;
        g.SpeedGain = speedGain;
        g.TurnRate = turnRate;
        g.Radius = radius;
        g.Accel = accel;
        g.Accel2 = accel2;
        g.Decel = decel;
        g.Inert = false;
        g.ExpireTime = now + ttl;
        g.FuelTime = now + fuelTime;       // QC hk: cnt = time + 30 (boosted-accel window AND fuel-out clock)
        g.GuideJitterAt = now + 1f;        // QC walker: cnt = time + 1
        g.AimJitter = Prandom.Vec() * 512f;
        g.Explode = Explode;

        m.Touch = (self, _) => Explode(self);
        EntityThink think = mode switch
        {
            Mode.Hellion => HellionThink,
            Mode.Hk => HkThink,
            _ => self => WalkerRocketThink(self, launchSpeed, turnRate),
        };
        // QC walker_fire_rocket: 1% chance the rocket launches straight into the up-and-over loop maneuver.
        if (mode == Mode.WalkerRocket)
        {
            g.WalkerSpeed = launchSpeed;
            g.WalkerTurnRate = turnRate;
            if (Prandom.Float() < 0.01f)
                think = WalkerRocketLoop;
        }
        m.Think = think;
        m.NextThink = now;
        return m;
    }

    // ------------------------------------------------------------------------------------------------
    // Hellion: heat-seeking missile that predicts the lead point and accelerates toward it each frame.
    // ------------------------------------------------------------------------------------------------
    public static void HellionThink(Entity missile)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float dt = Api.Services is not null ? Api.Clock.FrameTime : 0.05f;
        GuidanceState g = Guidance(missile);

        missile.NextThink = now + 0.05f;
        Vector3 olddir = QMath.Normalize(missile.Velocity);
        float speed = missile.Velocity.Length();

        if (g.ExpireTime < now) { Detonate(missile); return; }

        // Enemy dead/gone? keep current heading and accelerate; explode if we stray too far from the owner.
        if (missile.Enemy is null || missile.Enemy.Health <= 0f || missile.Enemy.DeadState != DeadFlag.No)
        {
            missile.Enemy = null;
            missile.Angles = QMath.VecToAngles(missile.Velocity);
            if (missile.Owner is not null && (missile.Origin - missile.Owner.Origin).Length() > g.Radius * 5f)
            { Detonate(missile); return; }
            missile.Velocity = olddir * System.Math.Min(speed * g.SpeedGain, g.SpeedMax);
            return;
        }

        // Close enough to deal good damage?
        if ((missile.Origin - missile.Enemy.Origin).Length() < g.Radius * 0.2f) { Detonate(missile); return; }

        // Predict enemy position by traveltime, average it with the current pos (QC smoothing).
        float itime = speed > 0f ? (missile.Enemy.Origin - missile.Origin).Length() / speed : 0f;
        Vector3 prePos = missile.Enemy.Origin + missile.Enemy.Velocity * itime;
        prePos = (prePos + missile.Enemy.Origin) * 0.5f;

        Vector3 newdir = QMath.Normalize(prePos - missile.Origin);
        newdir = QMath.Normalize(olddir + newdir * 0.35f);          // turn (limited blend)
        missile.Velocity = newdir * System.Math.Min(speed * g.SpeedGain, g.SpeedMax);  // accelerate
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        if (itime < 0.05f) Detonate(missile);
    }

    // ------------------------------------------------------------------------------------------------
    // Hunter-Killer: obstacle-avoiding guided rocket. Funnel-traces forward/L/R/U/D, biases toward the
    // clearest direction, accels in the open and decels near walls/sharp turns, with a panic turn.
    // ------------------------------------------------------------------------------------------------
    public static void HkThink(Entity missile)
    {
        if (Api.Services is null) { Detonate(missile); return; }
        float now = Api.Clock.Time;
        GuidanceState g = Guidance(missile);
        missile.NextThink = now;

        // Drop a dead/invalid target (QC: IS_DEAD || IS_SPEC || IS_OBSERVER) so we can re-seek.
        if (missile.Enemy is not null && (missile.Enemy.Health <= 0f || missile.Enemy.DeadState != DeadFlag.No))
            missile.Enemy = null;

        // No target? re-seek the closest valid target within 5000u (QC: IL_EACH(g_damagedbycontents,
        // hk_is_valid_target, ...) — port scans live entities in radius and applies the same validity gate).
        if (missile.Enemy is null)
        {
            Entity? best = null;
            float bestD2 = float.MaxValue;
            foreach (Entity it in Api.Entities.FindInRadius(missile.Origin, 5000f))
            {
                if (!HkIsValidTarget(missile.Owner, it)) continue;
                float d2 = (missile.Origin - it.Origin).LengthSquared();
                if (best is null || d2 < bestD2) { best = it; bestD2 = d2; }
            }
            missile.Enemy = best;
        }

        // Build the missile's facing basis (QC negates pitch around makevectors == fixedvectoangles).
        Vector3 ang = QMath.FixedVecToAngles(missile.Velocity);
        QMath.AngleVectors(ang, out Vector3 vfwd, out Vector3 vright, out Vector3 vup);

        Vector3 ve = Vector3.Zero; float fe = 0f;
        if (missile.Enemy is not null)
        {
            if ((missile.Origin - missile.Enemy.Origin).Length() <= g.Radius * 0.25f) { Detonate(missile); return; }
            float lead = System.Math.Min(missile.Enemy.Velocity.Length() > 0f
                ? (missile.Enemy.Origin - missile.Origin).Length() / System.Math.Max(missile.Velocity.Length(), 1f)
                : 0f, 0.5f);
            Vector3 prePos = missile.Enemy.Origin + missile.Enemy.Velocity * lead;
            TraceResult t = Api.Trace.Trace(missile.Origin, Vector3.Zero, Vector3.Zero, prePos, MoveFilter.NoMonsters, missile.Enemy);
            ve = QMath.Normalize(prePos - missile.Origin);
            fe = t.Fraction;
        }

        float myspeed = missile.Velocity.Length();
        Vector3 wishdir;

        bool clearToTarget = missile.Enemy is not null && fe == 1f
                             && (missile.Origin - missile.Enemy!.Origin).Length() <= 1000f;

        if (!clearToTarget)
        {
            float ltFor = myspeed * 3f;
            float ltSeek = myspeed * 2.95f;

            float ff = Ray(missile, missile.Origin, missile.Origin + vfwd * ltFor);

            float ad = missile.Enemy is not null
                ? (QMath.VecToAngles(QMath.Normalize(missile.Enemy.Origin - missile.Origin)) - ang).Length()
                : 0f;

            // Too close to a wall or a sharp turn? slow down. Fairly clear? speed up.
            if ((ff < 0.7f || ad > 4f) && myspeed > g.Speed)
                myspeed = System.Math.Max(myspeed * g.Decel, g.Speed);    // decel (shot_speed_decel)
            if (ff > 0.7f && myspeed < g.SpeedMax)
                myspeed = System.Math.Min(myspeed * g.Accel, g.SpeedMax); // accel (shot_speed_accel)

            float ptSeek = QMath.Bound(0.15f, 1f - ff, 0.8f);
            if (ff < 0.5f) ptSeek = 1f;

            float fl = Ray(missile, missile.Origin, missile.Origin + (-(vright * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vl);
            float fr = Ray(missile, missile.Origin, missile.Origin + ((vright * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vr);
            float fu = Ray(missile, missile.Origin, missile.Origin + ((vup * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vu2);
            float fd = Ray(missile, missile.Origin, missile.Origin + (-(vup * ptSeek) + vfwd * ff) * ltSeek, out Vector3 vd);

            vl = QMath.Normalize(vl - missile.Origin);
            vr = QMath.Normalize(vr - missile.Origin);
            vu2 = QMath.Normalize(vu2 - missile.Origin);
            vd = QMath.Normalize(vd - missile.Origin);

            if (ptSeek == 1f)
            {
                // Panic: pick a single clearest cardinal and turn hard.
                wishdir = vright;
                if (fl > fr) wishdir = -vright;
                if (fu > fl) wishdir = vup;
                if (fd > fu) wishdir = -vup;
            }
            else
            {
                wishdir = QMath.Normalize(vl * fl + vr * fr + vu2 * fu + vd * fd);
            }

            if (missile.Enemy is not null)
            {
                if (fe < 0.1f) fe = 0.1f;   // always bias slightly toward the target
                wishdir = wishdir * (1f - fe) + ve * fe;
            }
        }
        else
        {
            // Clear path: boost toward full speed and go straight at the target.
            if (myspeed < g.SpeedMax) myspeed = System.Math.Min(myspeed * g.Accel2, g.SpeedMax);
            wishdir = ve;
        }

        // Boosted accel while fuel lasts (QC: accel2 while cnt > time).
        if (myspeed > g.Speed && g.FuelTime > now)
            myspeed = System.Math.Min(myspeed * g.Accel2, g.SpeedMax);

        // Ranoutagazfish? (QC: cnt < time) — go inert (MOVETYPE_BOUNCE) and stop thinking. The rocket drops
        // as a dud and bounces; it does NOT detonate here. Touch/FLAC death can still blow it later.
        if (g.FuelTime < now)
        {
            g.FuelTime = now + 0.25f;
            g.Inert = true;
            missile.MoveType = MoveType.Bounce;
            missile.Think = null;
            missile.NextThink = 0f;
            return;
        }

        Vector3 olddir = QMath.Normalize(missile.Velocity);
        Vector3 newdir = QMath.Normalize(olddir + wishdir * g.TurnRate);
        missile.Velocity = newdir * myspeed;
        missile.Angles = QMath.VecToAngles(missile.Velocity);
    }

    // ------------------------------------------------------------------------------------------------
    // Walker rocket: crude steerlib_pull guidance toward the enemy + jitter, movelib-driven turn.
    // ------------------------------------------------------------------------------------------------
    public static void WalkerRocketThink(Entity rocket, float speed, float turnRate)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        GuidanceState g = Guidance(rocket);
        rocket.NextThink = now;

        float edist = rocket.Enemy is not null ? (rocket.Enemy.Origin - rocket.Origin).Length() : 0f;

        // Re-roll the aim jitter periodically to "simulate crude guidance".
        if (g.GuideJitterAt < now)
        {
            g.AimJitter = Prandom.Vec() * System.Math.Min(edist, edist < 1000f ? 64f : 256f);
            g.GuideJitterAt = now + 0.5f;
        }
        if (edist < 128f) g.AimJitter = Vector3.Zero;

        if (g.ExpireTime < now) { Detonate(rocket); return; }

        // QC: if (shot_dmg != 1337 && random() < 0.01) walker_rocket_loop(this). The 1337 sentinel (g.InLoop)
        // means we're already mid-loop and must not re-enter; otherwise a 1%/think chance to start the loop.
        if (!g.InLoop && Prandom.Float() < 0.01f) { WalkerRocketLoop(rocket); return; }

        if (rocket.Enemy is not null && (rocket.Enemy.Health <= 0f || rocket.Enemy.DeadState != DeadFlag.No))
            rocket.Enemy = null;

        Vector3 newdir = rocket.Enemy is not null
            ? TurretMath.SteerPull(rocket, rocket.Enemy.Origin + g.AimJitter)
            : QMath.Normalize(rocket.Velocity);

        // movelib_move_simple((rocket), newdir, speed, turnrate).
        TurretMath.MoveSimple(rocket, newdir, speed, turnRate);
        rocket.Angles = QMath.VecToAngles(rocket.Velocity);
    }

    // ------------------------------------------------------------------------------------------------
    // walker_rocket_loop / loop2 / loop3 (walker_weapon.qc:153-202): the up-and-over "loop" maneuver. The
    // rocket steers up 300u (loop), then back down to its start (loop2), then 200u below that (loop3), then
    // hands control back to the straight steer-pull. The QC overloads .tur_shotorg as the loop waypoint and
    // sets .shot_dmg = 1337 as the "in loop, don't re-roll" sentinel — here LoopTarget + InLoop.
    // ------------------------------------------------------------------------------------------------
    private static void WalkerRocketLoop(Entity rocket)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        GuidanceState g = Guidance(rocket);
        rocket.NextThink = now;
        g.LoopTarget = rocket.Origin + new Vector3(0f, 0f, 300f);
        g.InLoop = true;                          // QC: this.shot_dmg = 1337
        rocket.Think = WalkerRocketLoop2;
    }

    private static void WalkerRocketLoop2(Entity rocket)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        GuidanceState g = Guidance(rocket);
        rocket.NextThink = now;

        if (g.ExpireTime < now) { Detonate(rocket); return; }

        if ((rocket.Origin - g.LoopTarget).Length() < 100f)
        {
            g.LoopTarget = rocket.Origin - new Vector3(0f, 0f, 200f);
            rocket.Think = WalkerRocketLoop3;
            return;
        }

        Vector3 newdir = TurretMath.SteerPull(rocket, g.LoopTarget);
        TurretMath.MoveSimple(rocket, newdir, g.WalkerSpeed, g.WalkerTurnRate);
        rocket.Angles = QMath.VecToAngles(rocket.Velocity);
    }

    private static void WalkerRocketLoop3(Entity rocket)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        GuidanceState g = Guidance(rocket);
        rocket.NextThink = now;

        if (g.ExpireTime < now) { Detonate(rocket); return; }

        if ((rocket.Origin - g.LoopTarget).Length() < 100f)
        {
            // Loop complete: hand back to the straight steer-pull. (QC clears nothing but resets the think;
            // shot_dmg stays 1337 in Base so the rocket never loops again — InLoop stays true to match.)
            rocket.Think = self => WalkerRocketThink(self, g.WalkerSpeed, g.WalkerTurnRate);
            return;
        }

        Vector3 newdir = TurretMath.SteerPull(rocket, g.LoopTarget);
        TurretMath.MoveSimple(rocket, newdir, g.WalkerSpeed, g.WalkerTurnRate);
        rocket.Angles = QMath.VecToAngles(rocket.Velocity);
    }

    private static void Detonate(Entity missile)
    {
        GuidanceState g = Guidance(missile);
        Action<Entity>? boom = g.Explode;
        Forget(missile);
        if (boom is not null) boom(missile);
        else if (missile.Think is not null) { var t = missile.Think; missile.Think = null; t(missile); }
    }

    private static bool _deathHooked;

    /// <summary>Detonate a guided missile the damage pipeline shoots down (FLAC vs hellion/hk rockets).</summary>
    public static void EnsureDeathHook()
    {
        if (_deathHooked) return;
        _deathHooked = true;
        Damage.Combat.Death.Add(static (ref Damage.DeathEvent ev) =>
        {
            Entity v = ev.Victim;
            if (v.ClassName == "turret_guided" && _g.TryGetValue(v, out var g) && g.Explode is not null)
                g.Explode(v);
            return false;
        });
    }

    /// <summary>
    /// Port of <c>hk_is_valid_target</c> (hk_weapon.qc:241): the re-seek validity gate the HK rocket runs over
    /// nearby entities when it has lost its target. Rejects null/freed, no-target-flagged, undamageable or
    /// dead-health entities, dead players, and same-team (relative to self or the missile's owner). HK's
    /// playerbias (1) and missilebias (0) are both non-negative, so players and projectiles pass those gates.
    /// </summary>
    private static bool HkIsValidTarget(Entity? owner, Entity targ)
    {
        if (targ is null || targ.IsFreed) return false;
        if ((targ.Flags & EntFlags.NoTarget) != 0) return false;
        if (targ.TakeDamage == DamageMode.No || targ.Health < 0f) return false;

        if (TurretAI.IsPlayer(targ))
        {
            // HK target_select_playerbias = 1 (>= 0) -> players are allowed; only reject dead ones.
            if (targ.Health <= 0f || targ.DeadState != DeadFlag.No) return false;
        }
        // HK target_select_missilebias = 0 (>= 0) -> projectiles are NOT rejected here.

        // Team check: reject same team as the missile owner (the launching turret).
        if (owner is not null && (TurretAI.SameTeam(owner, targ)
            || (targ.Owner is not null && TurretAI.SameTeam(owner, targ.Owner))))
            return false;

        return true;
    }

    /// <summary>Trace fraction toward <paramref name="to"/> (QC traceline, no monsters), discarding the endpoint.</summary>
    private static float Ray(Entity self, Vector3 from, Vector3 to)
    {
        TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.NoMonsters, self);
        return tr.Fraction;
    }

    /// <summary>Trace fraction toward <paramref name="to"/>, also returning the trace endpoint.</summary>
    private static float Ray(Entity self, Vector3 from, Vector3 to, out Vector3 endPos)
    {
        TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.NoMonsters, self);
        endPos = tr.EndPos;
        return tr.Fraction;
    }
}
