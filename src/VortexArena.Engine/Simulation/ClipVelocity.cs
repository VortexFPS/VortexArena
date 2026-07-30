using System.Numerics;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// Velocity clipping against an impact plane — a faithful port of Darkplaces' <c>ClipVelocity</c>
/// (Base/darkplaces/sv_phys.c:1119). Removes the component of velocity heading into the plane,
/// optionally with overbounce (bounce restitution), and snaps near-zero components to zero via
/// STOP_EPSILON to avoid jitter. Part of the slide-and-step fidelity contract
/// (planning/specs/determinism-and-physics.md §5).
/// </summary>
public static class Clip
{
    /// <summary>DP STOP_EPSILON (sv_phys.c:1119).</summary>
    public const float StopEpsilon = 0.1f;

    /// <summary>
    /// Slide <paramref name="velocity"/> off <paramref name="normal"/>.
    /// <paramref name="overbounce"/> = 1 → pure slide (no bounce); &gt;1 adds restitution
    /// (e.g. grenades use 1 + bouncefactor). Returns the clipped velocity.
    /// </summary>
    public static Vector3 ClipVelocity(Vector3 velocity, Vector3 normal, float overbounce = 1f)
    {
        float backoff = -Vector3.Dot(velocity, normal) * overbounce;
        Vector3 outv = velocity + normal * backoff;

        // snap small components to zero (per-axis, exactly as DP)
        if (outv.X > -StopEpsilon && outv.X < StopEpsilon) outv.X = 0f;
        if (outv.Y > -StopEpsilon && outv.Y < StopEpsilon) outv.Y = 0f;
        if (outv.Z > -StopEpsilon && outv.Z < StopEpsilon) outv.Z = 0f;

        return outv;
    }
}
