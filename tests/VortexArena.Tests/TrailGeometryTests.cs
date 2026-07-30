using System;
using Xunit;
using XonoticGodot.Engine.Effects;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers the distance-stepped trail point generation (T1 — the vortex-beam-line fix). A trail must lay
/// exactly N points evenly ALONG the segment (each within the originjitter envelope), not scatter them
/// through the segment's bounding box.
/// </summary>
public class TrailGeometryTests
{
    // A deterministic "rand in [-1,1]" that cycles a fixed sequence, so tests don't depend on a global RNG.
    private static Func<float> SeqRand(params float[] seq)
    {
        int i = 0;
        return () => seq.Length == 0 ? 0f : seq[i++ % seq.Length];
    }

    [Fact]
    public void Produces_exactly_n_points()
    {
        NVec3[] pts = TrailGeometry.PointsAlongSegment(
            new NVec3(0, 0, 0), new NVec3(100, 0, 0), 12, NVec3.Zero, SeqRand(0f));
        Assert.Equal(12, pts.Length);
    }

    [Fact]
    public void Clamps_count_below_one_to_one()
    {
        NVec3[] pts = TrailGeometry.PointsAlongSegment(
            new NVec3(0, 0, 0), new NVec3(10, 0, 0), 0, NVec3.Zero, SeqRand(0f));
        Assert.Single(pts);
    }

    [Fact]
    public void Zero_jitter_points_lie_on_the_segment_and_advance_monotonically()
    {
        var start = new NVec3(10, 20, 30);
        var end = new NVec3(110, 20, 30); // along +X only, so X is the parameter
        NVec3[] pts = TrailGeometry.PointsAlongSegment(start, end, 10, NVec3.Zero, SeqRand(0f));

        float prevX = float.NegativeInfinity;
        foreach (NVec3 p in pts)
        {
            // On the line: Y and Z unchanged; X strictly within (start,end) and increasing.
            Assert.Equal(20f, p.Y, 3);
            Assert.Equal(30f, p.Z, 3);
            Assert.InRange(p.X, 10f, 110f);
            Assert.True(p.X > prevX, "points must advance monotonically along the segment");
            prevX = p.X;
        }
        // First point centered in its step: (0.5/10)*100 + 10 = 15.
        Assert.Equal(15f, pts[0].X, 3);
        // Last point: (9.5/10)*100 + 10 = 105.
        Assert.Equal(105f, pts[^1].X, 3);
    }

    [Fact]
    public void Jitter_stays_within_the_declared_envelope_per_axis()
    {
        var start = new NVec3(0, 0, 0);
        var end = new NVec3(120, 0, 0);
        var jitter = new NVec3(2f, 4f, 8f);
        // rand returns +1 then -1 alternating → max positive/negative excursion per axis.
        NVec3[] pts = TrailGeometry.PointsAlongSegment(start, end, 30, jitter, SeqRand(1f, -1f));

        for (int i = 0; i < pts.Length; i++)
        {
            float baseX = (i + 0.5f) / 30f * 120f;
            Assert.True(MathF.Abs(pts[i].X - baseX) <= jitter.X + 1e-3f);
            Assert.True(MathF.Abs(pts[i].Y) <= jitter.Y + 1e-3f);
            Assert.True(MathF.Abs(pts[i].Z) <= jitter.Z + 1e-3f);
        }
    }
}
