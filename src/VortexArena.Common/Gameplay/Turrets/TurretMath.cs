using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared Godot-free math + steering + movement primitives the turret AI, the mobile turrets (walker/ewheel)
/// and the guided projectiles (hellion/hk/walker rockets) reuse. These are faithful ports of the QuakeC
/// helpers the turret code leans on that did not yet exist in <see cref="QMath"/>:
///
///  - angle math: <c>anglemods</c>, <c>shortangle_f</c>, <c>shortangle_vxy</c>, <c>angleofs3</c> (lib/angle.qc);
///  - steering: <c>steerlib_pull/arrive/attract2</c> (server/steerlib.qc);
///  - movement: <c>movelib_move_simple</c>, <c>movelib_brake_simple</c> (common/physics/movelib.qc).
///
/// Kept in the Gameplay namespace (alongside the turret files) but deliberately self-contained so nothing
/// outside the turret folder is touched. Operates only on <see cref="Entity"/> + <see cref="Api"/>.
/// </summary>
public static class TurretMath
{
    // ---- angle helpers (qcsrc/lib/angle.qc) ----

    /// <summary>QC anglemods: wrap an angle into (-180, 180].</summary>
    public static float AngleMods(float v)
    {
        v -= 360f * MathF.Floor(v / 360f);
        if (v >= 180f) return v - 360f;
        if (v <= -180f) return v + 360f;
        return v;
    }

    /// <summary>
    /// QC shortangle_f(ang1, ang2): pick the representation of <paramref name="ang1"/> that is the short way
    /// around relative to <paramref name="ang2"/>. (A direct transcription of the QC branch logic.)
    /// </summary>
    public static float ShortAngle(float ang1, float ang2)
    {
        if (ang1 > ang2)
        {
            if (ang1 > 180f) return ang1 - 360f;
        }
        else
        {
            if (ang1 < -180f) return ang1 + 360f;
        }
        return ang1;
    }

    /// <summary>QC shortangle_vxy: per-component <see cref="ShortAngle"/> on x and y, z forced to 0.</summary>
    public static Vector3 ShortAngleVxy(Vector3 ang1, Vector3 ang2)
        => new(ShortAngle(ang1.X, ang2.X), ShortAngle(ang1.Y, ang2.Y), 0f);

    /// <summary>
    /// QC angleofs3(from, ang, to): the angular offset (pitch, yaw, 0) between the facing <paramref name="ang"/>
    /// and the direction from <paramref name="from"/> to <paramref name="to"/>, each component wrapped to
    /// (-180, 180].
    /// </summary>
    public static Vector3 AngleOfs(Vector3 from, Vector3 ang, Vector3 to)
    {
        Vector3 res = QMath.VecToAngles(QMath.Normalize(to - from)) - ang;
        if (res.X < 0f) res.X += 360f;
        if (res.X > 180f) res.X -= 360f;
        if (res.Y < 0f) res.Y += 360f;
        if (res.Y > 180f) res.Y -= 360f;
        return res;
    }

    // ---- steering (qcsrc/server/steerlib.qc) ----

    /// <summary>QC steerlib_pull(ent, point): a unit pull toward <paramref name="point"/>.</summary>
    public static Vector3 SteerPull(Entity e, Vector3 point) => QMath.Normalize(point - e.Origin);

    /// <summary>
    /// QC steerlib_arrive(ent, point, maxDist): pull toward <paramref name="point"/>, strength growing with
    /// distance (so it eases in as it approaches the goal).
    /// </summary>
    public static Vector3 SteerArrive(Entity e, Vector3 point, float maxDist)
    {
        float dist = QMath.Bound(0.001f, (e.Origin - point).Length(), maxDist);
        Vector3 dir = QMath.Normalize(point - e.Origin);
        return dir * (dist / maxDist);
    }

    /// <summary>
    /// QC steerlib_attract2(ent, point, minInfl, maxDist, maxInfl): pull toward <paramref name="point"/> with
    /// influence rising the CLOSER it gets, lerped between <paramref name="minInfl"/> and
    /// <paramref name="maxInfl"/>.
    /// </summary>
    public static Vector3 SteerAttract2(Entity e, Vector3 point, float minInfl, float maxDist, float maxInfl)
    {
        float dist = QMath.Bound(0.00001f, (e.Origin - point).Length(), maxDist);
        Vector3 dir = QMath.Normalize(point - e.Origin);
        float infl = 1f - (dist / maxDist);
        infl = minInfl + (infl * (maxInfl - minInfl));
        return dir * infl;
    }

    // ---- movement (qcsrc/common/physics/movelib.qc) ----

    /// <summary>
    /// QC movelib_move_simple(ent, dir, speed, inertia): blend the current velocity toward
    /// <paramref name="dir"/>*<paramref name="speed"/> by (1-inertia). Preserves the QC convention used by the
    /// turrets (where "inertia" is the weight of the NEW target velocity).
    /// </summary>
    public static void MoveSimple(Entity e, Vector3 dir, float speed, float inertia)
    {
        Vector3 target = dir * speed;
        e.Velocity = e.Velocity * (1f - inertia) + target * inertia;
    }

    /// <summary>
    /// QC movelib_brake_simple(ent, force): bleed <paramref name="force"/> off the horizontal speed each call,
    /// preserving the vertical component (so gravity/jumps are unaffected).
    /// </summary>
    public static void BrakeSimple(Entity e, float force)
    {
        float mspeed = MathF.Max(0f, e.Velocity.Length() - force);
        Vector3 mdir = QMath.Normalize(e.Velocity);
        float vz = e.Velocity.Z;
        e.Velocity = mdir * mspeed;
        e.Velocity = new Vector3(e.Velocity.X, e.Velocity.Y, vz);
    }
}
