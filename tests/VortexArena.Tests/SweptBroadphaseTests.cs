using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Engine.Collision;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Regression cover for <see cref="CollisionWorld.QuerySwept"/> — the swept-corridor broadphase that perf 2.1
/// put under every long trace (hitscan, bot line-of-sight, crosshair true-aim).
///
/// The contract that matters is CONSERVATISM: the candidate set it returns must never omit a brush the plain
/// rectangle <see cref="CollisionWorld.Query"/> would have handed the narrowphase. It may return fewer
/// candidates overall (that is the whole point — the enclosing rectangle of a long diagonal covers O(length²)
/// cells), but never fewer than the ones the sweep can actually touch: a brush dropped here is a brush the
/// narrowphase never clips, i.e. a shot that travels through solid geometry.
///
/// The 2026-07-27 bug: the per-cell loop marked a brush as seen BEFORE testing it, so each brush was judged
/// against the box of whichever SEGMENT first reached its cell. Grid cells are XY buckets spanning all Z and a
/// segment's box straddles up to four of them, so an early segment routinely reaches a cell it is still far
/// from in Z, fails the overlap test, and pins the brush out of the candidate set for every later segment —
/// including the one that genuinely hits it.
/// </summary>
public class SweptBroadphaseTests
{
    private const int Solid = SuperContents.Solid;

    private static CollisionWorld WorldWith(params (Vector3 Min, Vector3 Max)[] boxes)
    {
        var w = new CollisionWorld();
        // A large floor establishes world bounds so the grid is built over a realistic extent (the swept path
        // is only taken once XY travel exceeds ~3 cells, which depends on the grid scale).
        w.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), Solid));
        foreach ((Vector3 mn, Vector3 mx) in boxes)
            w.AddBrush(Brush.FromBox(mn, mx, Solid));
        w.BuildGrid();
        return w;
    }

    /// <summary>The exact geometry that reproduced the drop: a steep-Z sweep whose brush sits in a cell an
    /// earlier segment reaches (and rejects on Z) before the segment that actually hits it gets there.</summary>
    [Fact]
    public void SweptQuery_KeepsBrushHitLateInASteepZSweep()
    {
        var start = new Vector3(0f, 0f, 0f);
        var end = new Vector3(200f, 0f, 2000f);
        var target = (Min: new Vector3(100f, -16f, 1200f), Max: new Vector3(128f, 16f, 1300f));
        CollisionWorld w = WorldWith(target);

        var swept = new List<Brush>();
        w.QuerySwept(start, end, Vector3.Zero, Vector3.Zero, swept);

        // The ray passes through the box (at x=120 it is at z=1200, inside it), so the broadphase MUST offer it.
        Assert.Contains(swept, b => b.Mins.Z >= 1000f);
    }

    /// <summary>General conservatism: for a spread of long sweeps, QuerySwept never omits a brush that the
    /// plain rectangle Query returns AND that overlaps the sweep's own bounds.</summary>
    [Theory]
    [InlineData(200f, 0f, 2000f)]
    [InlineData(2000f, 2000f, 1500f)]
    [InlineData(-1800f, 900f, -1200f)]
    [InlineData(3000f, -2500f, 400f)]
    public void SweptQuery_NeverDropsACandidateTheRectangleQueryFinds(float ex, float ey, float ez)
    {
        // A lattice of small boxes spread across the volume the sweeps cross.
        var boxes = new List<(Vector3, Vector3)>();
        for (int x = -2000; x <= 3000; x += 250)
            for (int z = -1200; z <= 2000; z += 200)
                boxes.Add((new Vector3(x, -24f, z), new Vector3(x + 40f, 24f, z + 60f)));
        CollisionWorld w = WorldWith(boxes.ToArray());

        var start = new Vector3(0f, 0f, 0f);
        var end = new Vector3(ex, ey, ez);
        var mins = new Vector3(-8f, -8f, -8f);
        var maxs = new Vector3(8f, 8f, 8f);

        var swept = new List<Brush>();
        w.QuerySwept(start, end, mins, maxs, swept);

        // The exact requirement: every brush the moving BOX actually overlaps somewhere along the sweep must be
        // offered to the narrowphase. Brushes merely inside the enclosing rectangle are legitimately culled —
        // that is the entire point of the corridor march — so the rectangle Query is NOT the oracle here.
        var sweptSet = new HashSet<Brush>(swept);
        foreach (Brush b in AllBrushesTouchedBySweep(w, start, end, mins, maxs))
            Assert.Contains(b, sweptSet);
    }

    /// <summary>Ground truth by brute force: every brush in the world whose AABB the swept box overlaps.</summary>
    private static List<Brush> AllBrushesTouchedBySweep(
        CollisionWorld w, Vector3 a, Vector3 b, Vector3 mins, Vector3 maxs)
    {
        // Pull the whole world via a rectangle that cannot miss anything, then filter exactly.
        var all = new List<Brush>();
        w.Query(new Vector3(-1e6f), new Vector3(1e6f), all);

        var hit = new List<Brush>();
        foreach (Brush br in all)
            if (SweptBoxOverlaps(a, b, mins, maxs, br))
                hit.Add(br);
        return hit;
    }

    /// <summary>Does the box [mins,maxs] swept from <paramref name="a"/> to <paramref name="b"/> overlap the
    /// brush's AABB at any point? Dense sampling — the test brushes are far larger than the sample spacing.</summary>
    private static bool SweptBoxOverlaps(Vector3 a, Vector3 b, Vector3 mins, Vector3 maxs, Brush brush)
    {
        const int steps = 4096;
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = Vector3.Lerp(a, b, i / (float)steps);
            if (CollisionWorld.BoxesOverlap(p + mins, p + maxs, brush.Mins, brush.Maxs))
                return true;
        }
        return false;
    }
}
