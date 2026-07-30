using System;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Engine.Effects;

/// <summary>
/// Pure geometry for distance-stepped particle TRAILS — the C# port of Darkplaces' trail spawn loop
/// (Base/darkplaces/cl_particles.c:1763-1780). A trail (a beam, a projectile exhaust) spawns its particles
/// stepped evenly ALONG the segment from start to end, each jittered by <c>originjitter</c> per axis, with
/// ALL of them placed in the same frame — NOT scattered through the segment's bounding box over time (the
/// old port behaviour, which made the vortex beam read as a slowly-appearing cloud instead of a line).
///
/// This is engine-side and Godot-free so it can be unit-tested; the renderer (game/client/EffectSystem) calls
/// it to build the point set, then encodes it into a GpuParticles3D emission-point texture.
/// </summary>
public static class TrailGeometry
{
    /// <summary>
    /// Step <paramref name="count"/> points evenly along <c>[start, end]</c>, each offset by ±<paramref name="jitter"/>
    /// per axis (DP <c>trailpos += trailstep*traildir</c> with <c>originjitter[axis]*rand(-1,1)</c>). Points are at
    /// the segment fractions <c>(i+0.5)/count</c> so they're centered in their step (no clumping at the endpoints).
    /// <paramref name="rand11"/> returns a uniform value in [-1, 1] (one call per axis per point); pass a seeded
    /// generator in tests for determinism. Always returns exactly <c>max(1, count)</c> points in Quake space.
    /// </summary>
    public static NVec3[] PointsAlongSegment(NVec3 start, NVec3 end, int count, NVec3 jitter, Func<float> rand11)
    {
        if (count < 1)
            count = 1;
        var pts = new NVec3[count];
        NVec3 step = (end - start) / count;
        for (int i = 0; i < count; i++)
        {
            NVec3 at = start + step * (i + 0.5f);
            NVec3 j = new NVec3(
                jitter.X * rand11(),
                jitter.Y * rand11(),
                jitter.Z * rand11());
            pts[i] = at + j;
        }
        return pts;
    }
}
