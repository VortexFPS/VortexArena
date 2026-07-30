// Port: the shared vehicle-physics helpers QuakeC kept in common/physics/movelib.qc,
// common/vehicles/vehicles.qc and common/vehicles/sv_vehicles.qc — the bits every concrete vehicle's
// per-frame controller leans on:
//   * movelib_groundalign4point / movelib_move_simple / movelib_brake_simple (movelib.qc)
//   * vehicles_force_fromtag_hover / _maglev + vehicle_altitude (vehicles.qc)
//   * vehicle_aimturret + vehicles_locktarget (sv_vehicles.qc)
//   * the angle helpers (shortangle_f / anglemods / AnglesTransform_*) the controllers use
//   * the homing/guided/ground-hugging projectile guidance the vehicle rockets share
//
// This is a NEW, prefixed (`VehiclePhysics`) helper type — it deliberately does NOT touch VehicleCommon.cs
// so the two never collide. It is Godot-free: everything goes through the engine-services facade (Api).
//
// Tag handling: QC reads sub-entity bone positions with gettaginfo(); the facade exposes that as
// Api.Models.TryGetTag. When a tag is missing (headless model service), the helpers fall back to a
// faithful origin/forward computed from the vehicle's own angles so behavior is preserved.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared, Godot-free vehicle physics + aiming + guidance helpers — the headless core of QuakeC's
/// movelib / vehicles_force_fromtag / vehicle_aimturret / vehicles_locktarget / vehicle-rocket guidance.
/// Concrete vehicles (Racer/Raptor/Spiderbot/Bumblebee) call into these from their per-frame controllers
/// so the deep physics lives in one place, exactly as QC kept it shared.
/// </summary>
public static class VehiclePhysics
{
    private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;

    // QC DPCONTENTS_LIQUIDSMASK — the SUPERCONTENTS bits for water/slime/lava (matches PlayerPhysics).
    private const int SuperContentsLiquidsMask = 0x00000010 | 0x00000020 | 0x00000040;

    /// <summary>QC <c>pointcontents(p) &amp; DPCONTENTS_LIQUIDSMASK</c>: is the point inside water/slime/lava?</summary>
    public static bool InLiquid(Vector3 point)
        => Api.Services is not null && (Api.Trace.PointContents(point) & SuperContentsLiquidsMask) != 0;

    // =====================================================================================
    // Angle helpers (QC mathlib / vehicles).
    // =====================================================================================

    /// <summary>QC anglemods(v): wrap an angle into [0, 360).</summary>
    public static float AngleMods(float v)
    {
        v %= 360f;
        if (v < 0f) v += 360f;
        return v;
    }

    /// <summary>QC shortangle_f(precision, self): wrap an angle delta into [-180, 180].</summary>
    public static float ShortAngle(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        else if (a < -180f) a += 360f;
        return a;
    }

    /// <summary>Per-component <see cref="ShortAngle"/> for a vector of Euler deltas (QC AnglesTransform_Normalize true).</summary>
    public static Vector3 ShortAngles(Vector3 a) => new(ShortAngle(a.X), ShortAngle(a.Y), ShortAngle(a.Z));

    // =====================================================================================
    // Tag origin/forward (QC gettaginfo) with a faithful fallback.
    // =====================================================================================

    /// <summary>
    /// World origin of a model tag (QC <c>gettaginfo(e, gettagindex(e, tag))</c>). Falls back to the
    /// entity origin offset forward/up by a small amount when the model service has no tag (headless).
    /// </summary>
    public static Vector3 TagOrigin(Entity e, string tag, Vector3 fallbackForwardOffset = default)
    {
        if (Api.Services is not null && Api.Models.TryGetTag(e, tag, out Vector3 org, out _, out _, out _))
            return org;
        if (fallbackForwardOffset == default)
            return e.Origin;
        QMath.AngleVectors(e.Angles, out Vector3 fwd, out Vector3 right, out Vector3 up);
        return e.Origin + fwd * fallbackForwardOffset.X + right * fallbackForwardOffset.Y + up * fallbackForwardOffset.Z;
    }

    /// <summary>Tag origin AND forward (QC gettaginfo sets v_forward too). Fallback uses the entity angles.</summary>
    public static (Vector3 origin, Vector3 forward) TagOriginForward(Entity e, string tag)
    {
        if (Api.Services is not null && Api.Models.TryGetTag(e, tag, out Vector3 org, out Vector3 fwd, out _, out _))
            return (org, fwd);
        return (e.Origin, QMath.Forward(e.Angles));
    }

    // =====================================================================================
    // movelib (QC common/physics/movelib.qc).
    // =====================================================================================

    /// <summary>
    /// Port of <c>movelib_brake_simple</c>: bleed the horizontal velocity toward zero by
    /// <paramref name="force"/> per call, preserving the vertical component.
    /// </summary>
    public static void BrakeSimple(Entity e, float force)
    {
        float mspeed = MathF.Max(0f, QMath.VLen(e.Velocity) - force);
        Vector3 mdir = QMath.Normalize(e.Velocity);
        float vz = e.Velocity.Z;
        e.Velocity = mdir * mspeed;
        Vector3 v = e.Velocity; v.Z = vz; e.Velocity = v;
    }

    /// <summary>
    /// Port of <c>movelib_move_simple</c> (movelib.qh inline): blend the current velocity toward
    /// <paramref name="dir"/>*<paramref name="vel"/> by <paramref name="blend"/> (the QC inertia lerp).
    /// </summary>
    public static void MoveSimple(Entity e, Vector3 dir, float vel, float blend)
    {
        Vector3 target = dir * vel;
        e.Velocity = target * blend + e.Velocity * (1f - blend);
    }

    /// <summary>
    /// Port of <c>vehicle_altitude</c> (vehicles.qc): height of the vehicle bbox above the world below it.
    /// </summary>
    public static float Altitude(Entity e, float amax)
    {
        if (Api.Services is null) return amax;
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs, e.Origin - new Vector3(0, 0, amax),
            MoveFilter.WorldOnly, e);
        return QMath.VLen(e.Origin - tr.EndPos);
    }

    /// <summary>
    /// Port of <c>movelib_groundalign4point</c> (movelib.qc): trace four springs at the bbox corners and
    /// pitch/roll the chassis to follow the ground under it, blending toward the computed push angles.
    /// Requires the caller to have set the (forward,right,up) basis (we pass it in explicitly).
    /// Returns nothing; it mutates <c>e.Angles</c> (X=pitch, Z=roll) and re-plants <c>e.Origin</c> like QC.
    /// </summary>
    public static void GroundAlign4Point(Entity e, Vector3 forward, Vector3 right, Vector3 up,
        float springLength, float springUp, float blendRate, float max)
    {
        if (Api.Services is null) return;

        Vector3 baseCenter = (e.AbsMax + e.AbsMin) * 0.5f + up * springUp;
        Vector3 ev = up * springLength;

        // Put the springs slightly inside the bbox (QC maxs.x/y * 0.8).
        Vector3 ahead = forward * (e.Maxs.X * 0.8f);
        Vector3 side = right * (e.Maxs.Y * 0.8f);

        Vector3 a = baseCenter + ahead + side;
        Vector3 b = baseCenter + ahead - side;
        Vector3 c = baseCenter - ahead + side;
        Vector3 d = baseCenter - ahead - side;

        float az = SpringHit(e, a, ev);
        float bz = SpringHit(e, b, ev);
        float cz = SpringHit(e, c, ev);
        float dz = SpringHit(e, d, ev);

        float pushX = (az - cz) * max + (bz - dz) * max;
        float pushZ = (bz - az) * max + (dz - cz) * max;

        Vector3 ang = e.Angles;
        ang.X = (1f - blendRate) * ang.X + pushX * blendRate;
        ang.Z = (1f - blendRate) * ang.Z + pushZ * blendRate;
        e.Angles = ang;
        // QC re-anchors origin to itself via setorigin(this, r) to relink the area grid; harmless headless.
    }

    private static float SpringHit(Entity e, Vector3 start, Vector3 down)
    {
        TraceResult tr = TraceLine(e, start, start - down);
        return 1f - tr.Fraction;
    }

    // Convenience overload: traceline (point trace) used by the spring + force-from-tag helpers.
    private static TraceResult TraceLine(Entity e, Vector3 a, Vector3 b) =>
        Api.Trace.Trace(a, Vector3.Zero, Vector3.Zero, b, MoveFilter.Normal, e);

    // =====================================================================================
    // vehicles_force_fromtag (QC vehicles.qc) — the per-engine spring for the racer 4-point hover.
    // Returns (force vector to add, normalized power 0..1 used for roll/pitch differential).
    // =====================================================================================

    /// <summary>
    /// Port of <c>vehicles_force_fromtag_hover</c>: a downward spring from the engine tag; the closer the
    /// ground the stronger the upward push. <paramref name="maglev"/> switches to the maglev variant
    /// (<c>vehicles_force_fromtag_maglev</c>) which also repels from ceilings.
    /// </summary>
    public static (Vector3 force, float normPower) ForceFromTag(Entity e, string tag,
        float springLength, float maxPower, bool maglev)
    {
        Vector3 origin;
        Vector3 up;
        if (Api.Services is not null && Api.Models.TryGetTag(e, tag, out Vector3 tOrg, out _, out _, out Vector3 tUp))
        {
            origin = tOrg;
            up = QMath.Normalize(tUp); // QC uses -normalize(v_forward); the tag's up is the spring axis here
        }
        else
        {
            // Fallback: four virtual engines around the chassis center, spring straight down.
            QMath.AngleVectors(e.Angles, out _, out _, out Vector3 eu);
            origin = e.Origin;
            up = QMath.Normalize(eu);
        }

        Vector3 dir = up; // spring pushes "up" out of the ground
        TraceResult tr = TraceLine(e, origin, origin - dir * springLength);

        if (!maglev)
        {
            float power = (1f - tr.Fraction) * maxPower;
            return (dir * power, power / maxPower);
        }

        // maglev: repels from both floor and ceiling; if nothing under it, sink.
        if (tr.Fraction == 1f)
            return (new Vector3(0, 0, -200f), -0.25f);
        float p = ((1f - tr.Fraction) - tr.Fraction) * maxPower;
        return (dir * p, p / maxPower);
    }

    // =====================================================================================
    // vehicle_aimturret (QC sv_vehicles.qc) — slew a turret/head toward a world target within limits.
    // =====================================================================================

    /// <summary>
    /// Port of <c>vehicle_aimturret</c>: turn the turret head <paramref name="turret"/> toward
    /// <paramref name="target"/> (in world space), clamped to per-axis pitch/yaw limits, at
    /// <paramref name="aimSpeed"/> deg/sec. Operates on <paramref name="turret"/>.Angles relative to the
    /// body <paramref name="vehic"/>. Returns the muzzle tag origin (QC returns vtag).
    /// </summary>
    public static Vector3 AimTurret(Entity vehic, Vector3 target, Entity turret, string tag,
        float pitchMin, float pitchMax, float rotMin, float rotMax, float aimSpeed, float dt)
    {
        Vector3 vtag = TagOrigin(turret, tag, new Vector3(40f, 0f, 0f));

        // Direction to the target in world angles, brought into the body's frame, minus the head's
        // current angles -> the remaining delta to slew. (QC AnglesTransform left-divide by body angles.)
        Vector3 worldAng = QMath.VecToAngles(QMath.Normalize(target - vtag));
        Vector3 delta = ShortAngles(new Vector3(
            worldAng.X - vehic.Angles.X - turret.Angles.X,
            worldAng.Y - vehic.Angles.Y - turret.Angles.Y,
            0f));

        float ftmp = aimSpeed * dt;
        float dy = QMath.Bound(-ftmp, delta.Y, ftmp);
        float dx = QMath.Bound(-ftmp, delta.X, ftmp);

        Vector3 a = turret.Angles;
        a.Y = QMath.Bound(rotMin, a.Y + dy, rotMax);
        a.X = QMath.Bound(pitchMin, a.X + dx, pitchMax);
        turret.Angles = a;
        return vtag;
    }

    // =====================================================================================
    // vehicles_locktarget (QC sv_vehicles.qc) — build up / decay a homing lock on a traced target.
    // =====================================================================================

    /// <summary>
    /// Port of <c>vehicles_locktarget</c>: maintain the vehicle's lock-on state from the crosshair trace
    /// result <paramref name="tracedTarget"/>. <paramref name="incr"/>/<paramref name="decr"/> are the
    /// per-tick build/decay rates; <paramref name="lockTime"/> is how long a full lock holds.
    /// </summary>
    public static void LockTarget(Entity vehic, Entity? tracedTarget, float incr, float decr, float lockTime)
    {
        if (vehic.VehLockTarget is not null && VehicleCommon.IsDead(vehic.VehLockTarget))
        {
            vehic.VehLockTarget = null;
            vehic.VehLockStrength = 0f;
            vehic.VehLockTime = 0f;
        }

        if (vehic.VehLockTime > Time)
        {
            if (vehic.VehLockTarget is not null && vehic.VehLockSoundTime < Time && Api.Services is not null)
            {
                vehic.VehLockSoundTime = Time + 0.5f;
                // QC sv_vehicles.qc:114 play2(this.owner, "vehicles/locked.wav") — a per-recipient 2D send
                // (CH_INFO / VOL_BASE 0.7 / ATTEN_NONE 0), not a positional ATTEN_LARGE emit. Route through
                // SoundSystem.Play2Raw so the volume/attenuation match Base (was Api.Sound.Play default vol=1/atten=1).
                if (vehic.Owner is not null) SoundSystem.Play2Raw(vehic.Owner, "vehicles/locked.wav");
            }
            return;
        }

        Entity? t = tracedTarget;
        // QC (sv_vehicles.qc:120-125) rejects same-team / dead / non-(vehicle||turret) / near-invisible
        // targets — the homing lock only ever acquires another vehicle or a turret, never a player or any
        // other live entity. `alpha <= 0.5 && alpha != 0` means a deliberately faded entity (0 == default
        // opaque) cannot be locked.
        if (t is not null && (SameTeam(t, vehic) || VehicleCommon.IsDead(t)
            || !IsVehicleOrTurret(t)
            || (t.Alpha <= 0.5f && t.Alpha != 0f)))
            t = null;

        if (vehic.VehLockTarget is null && t is not null)
            vehic.VehLockTarget = t;

        if (vehic.VehLockTarget is not null && t == vehic.VehLockTarget && Api.Services is not null && vehic.Owner is not null)
        {
            if (vehic.VehLockStrength != 1f && vehic.VehLockStrength + incr >= 1f)
            {
                // QC sv_vehicles.qc:134 play2(this.owner, "vehicles/lock.wav") — per-recipient 2D (VOL_BASE/ATTEN_NONE).
                SoundSystem.Play2Raw(vehic.Owner, "vehicles/lock.wav");
                vehic.VehLockSoundTime = Time + 0.8f;
            }
            else if (vehic.VehLockStrength != 1f && vehic.VehLockSoundTime < Time)
            {
                // QC sv_vehicles.qc:139 play2(this.owner, "vehicles/locking.wav") — per-recipient 2D (VOL_BASE/ATTEN_NONE).
                SoundSystem.Play2Raw(vehic.Owner, "vehicles/locking.wav");
                vehic.VehLockSoundTime = Time + 0.3f;
            }
        }

        if (t == vehic.VehLockTarget && t is not null)
        {
            vehic.VehLockStrength = MathF.Min(vehic.VehLockStrength + incr, 1f);
            if (vehic.VehLockStrength == 1f)
                vehic.VehLockTime = Time + lockTime;
        }
        else
        {
            vehic.VehLockStrength = MathF.Max(vehic.VehLockStrength - (t is not null ? decr * 2f : decr), 0f);
            if (vehic.VehLockStrength == 0f)
                vehic.VehLockTarget = null;
        }
    }

    // =====================================================================================
    // Crosshair trace (QC crosshair_trace) — what the seated player is aiming at.
    // =====================================================================================

    /// <summary>
    /// Port of <c>crosshair_trace</c>: trace from the player's eye along its view angles to find the world
    /// point + entity under the crosshair. Returns the trace; vehicles use EndPos as the aim point.
    /// </summary>
    public static TraceResult CrosshairTrace(Entity vehic, Vector3 viewAngles, Entity ignore)
    {
        if (Api.Services is null) return TraceResult.Miss(vehic.Origin + QMath.Forward(viewAngles) * 1000f);
        Vector3 start = vehic.Origin;
        Vector3 fwd = QMath.Forward(viewAngles);
        return Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, start + fwd * WeaponFiring.CurrentMaxShotDistance,
            MoveFilter.Normal, ignore);
    }

    // =====================================================================================
    // Bomb dropmark prediction (QC raptor vr_crosshair tracetoss) — the bomb-impact predictor.
    // =====================================================================================

    /// <summary>
    /// Port of the raptor <c>vr_crosshair</c> dropmark <c>tracetoss</c> (raptor.qc:792-799): predict where a
    /// cluster bomb dropped right now would land. QC seeds a point-sized MOVETYPE_BOUNCE body (gravity 1,
    /// dphitcontentsmask DPCONTENTS_SOLID) at the raptor origin with the raptor velocity, then runs the engine
    /// <c>tracetoss</c> ballistic sweep to its first solid contact and marks <c>trace_endpos</c>. Reproduced
    /// here as a stepped gravity integration (the same model <see cref="Monsters.MonsterAI"/> uses), returning
    /// the predicted impact point in Quake space. Pure presentation: drives only the HUD dropmark crosshair.
    /// </summary>
    public static Vector3 BombDropPredict(Entity vehic)
    {
        if (Api.Services is null) return vehic.Origin;
        float g = Api.Cvars.GetFloat("sv_gravity"); // bomb gravity = 1 (full), so sv_gravity unscaled
        if (g == 0f) g = 800f;
        Vector3 pos = vehic.Origin;
        Vector3 v = vehic.Velocity;
        const float step = 0.05f;
        for (int i = 0; i < 200; ++i) // up to ~10s of fall, plenty for any drop height
        {
            Vector3 next = pos + v * step;
            TraceResult tr = Api.Trace.Trace(pos, Vector3.Zero, Vector3.Zero, next, MoveFilter.Normal, vehic);
            if (tr.Fraction < 1f) return tr.EndPos;
            pos = next;
            v.Z -= g * step;
        }
        return pos;
    }

    // =====================================================================================
    // Team helpers (QC SAME_TEAM / DIFF_TEAM).
    // =====================================================================================

    public static bool SameTeam(Entity a, Entity b) => a.Team != 0f && a.Team == b.Team;
    public static bool DiffTeam(Entity a, Entity b) => a.Team != b.Team;

    /// <summary>
    /// QC <c>IS_VEHICLE(e) || IS_TURRET(e)</c>: only these two classes are valid homing-lock targets. A
    /// vehicle is flagged with <see cref="VehicleCommon.VehicleFlags.IsVehicle"/> (set in
    /// <c>SpawnVehicle</c>); a turret carries the <c>"turret_"</c> classname prefix (set in
    /// <c>TurretSpawn.Init</c>). Anything else (players, items, projectiles) is rejected.
    /// </summary>
    public static bool IsVehicleOrTurret(Entity e)
        => (e.VehicleFlags & VehicleFlags.IsVehicle) != 0
        || e.ClassName.StartsWith("turret_", System.StringComparison.Ordinal);

    // =====================================================================================
    // Vehicle projectile guidance — the per-tick think for the homing rockets (racer/spiderbot).
    // Shared so VehicleCommon's projectile guidance helper and the vehicle weapons reuse one model.
    // =====================================================================================

    /// <summary>Guidance modes a vehicle projectile can fly under (QC distinct setthink targets).</summary>
    public enum GuideMode
    {
        Dumb = -1,
        RacerHoming = 0,     // racer_rocket_tracker (predicts + chases a locked target)
        RacerGroundHug = 1,  // racer_rocket_groundhugger (terrain-following)
        SpiderGuided = 2,    // spiderbot_rocket_guided (steers toward the pilot crosshair)
        SpiderUnguided = 3,  // spiderbot_rocket_unguided (steers toward a fixed pos1)
    }

    /// <summary>
    /// Advance one tick of a guided vehicle rocket (the union of racer_rocket_tracker/groundhugger and
    /// spiderbot_rocket_guided/unguided). <paramref name="frametime"/> is the engine frame length.
    /// <paramref name="crosshair"/> is the live aim point for pilot-guided modes (null otherwise).
    /// Returns false when the rocket should detonate (caller explodes it).
    /// </summary>
    public static bool GuideRocket(Entity rocket, GuideMode mode, float frametime, Vector3? crosshair)
    {
        if (Api.Services is null) return true;

        // QC: detonate if the owner died or the lifetime elapsed.
        Entity? owner = rocket.Owner;
        if ((owner is not null && VehicleCommon.IsDead(owner)) || rocket.VehProjExpire < Time)
            return false;

        Vector3 oldDir = QMath.Normalize(rocket.Velocity);
        float oldVel = QMath.VLen(rocket.Velocity);
        float speed = oldVel + rocket.VehProjAccel;
        float turn = rocket.VehProjTurnRate;

        switch (mode)
        {
            case GuideMode.RacerHoming:
            {
                Entity? targ = rocket.Enemy;
                if (targ is null) { rocket.VehGuideMode = (int)GuideMode.RacerGroundHug; return true; }

                float tti = MathF.Min(QMath.VLen(targ.Origin - rocket.Origin) / MathF.Max(oldVel, 1f), 1f);
                Vector3 predicted = targ.Origin + targ.Velocity * tti;

                // QC racer_rocket_tracker: trace a line ahead of the rocket (origin -> origin + fwd*64 - '0 0 32')
                // so an obstacle between it and the target can be detected and climbed over.
                QMath.AngleVectors(QMath.VecToAngles(oldDir), out Vector3 fwd, out _, out _);
                TraceResult ahead = Api.Trace.Trace(rocket.Origin, Vector3.Zero, Vector3.Zero,
                    rocket.Origin + fwd * 64f - new Vector3(0, 0, 32f), MoveFilter.Normal, rocket);

                Vector3 newDir = QMath.Normalize(predicted - rocket.Origin);
                float heightDiff = predicted.Z - rocket.Origin.Z;

                // QC: lose the lock if the target leaves the cone (locked_maxangle 1.8 rad of |newdir-fwd|).
                if (QMath.VLen(newDir - fwd) > 1.8f) { rocket.VehGuideMode = (int)GuideMode.RacerGroundHug; return true; }

                // QC: if the ahead-trace hit something that ISN'T the locked target, lift the steering vector to
                // climb over the obstacle between the rocket and its prey (newdir.z += 16 * sys_frametime).
                if (ahead.Fraction != 1f && ahead.Ent != targ)
                    newDir.Z += 16f * frametime;

                Vector3 v = QMath.Normalize(oldDir + newDir * turn) * speed;
                v.Z -= 800f * frametime;
                v.Z += MathF.Max(heightDiff, 1600f) * frametime; // climbspeed 1600
                rocket.Velocity = v;
                return true;
            }
            case GuideMode.RacerGroundHug:
            {
                TraceResult ahead = Api.Trace.Trace(rocket.Origin, rocket.Mins, rocket.Maxs,
                    rocket.Origin + oldDir * 64f, MoveFilter.WorldOnly, rocket);
                if (ahead.Fraction <= 0.5f) { rocket.Velocity = oldDir * speed; return true; }

                TraceResult floor = TraceLine(rocket, ahead.EndPos, ahead.EndPos - new Vector3(0, 0, 64f));
                if (floor.Fraction != 1f)
                {
                    Vector3 newDir = QMath.Normalize(floor.EndPos + new Vector3(0, 0, 64f) - rocket.Origin) * turn;
                    rocket.Velocity = QMath.Normalize(oldDir + newDir) * speed;
                }
                else
                {
                    Vector3 v = oldDir * speed; v.Z -= 1600f * frametime; rocket.Velocity = v;
                }
                return true;
            }
            case GuideMode.SpiderGuided:
            {
                if (owner is not null && owner.Vehicle is null) { rocket.VehGuideMode = (int)GuideMode.SpiderUnguided; }
                Vector3 aim = crosshair ?? rocket.VehGuideTarget;
                Vector3 newDir = QMath.Normalize(aim - rocket.Origin) + Prandom.Vec() * 0.2f; // rocket_noise
                rocket.Velocity = QMath.Normalize(oldDir + newDir * turn) * speed;
                return true;
            }
            case GuideMode.SpiderUnguided:
            {
                Vector3 newDir = QMath.Normalize(rocket.VehGuideTarget - rocket.Origin) + Prandom.Vec() * 0.2f;
                rocket.Velocity = QMath.Normalize(oldDir + newDir * turn) * speed;
                if (QMath.VLen(rocket.VehGuideTarget - rocket.Origin) < 16f) return false;
                return true;
            }
            default:
                return true;
        }
    }
}
