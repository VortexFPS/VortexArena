using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// The gameplay-level confirmation of the 2026-07-27 <see cref="CollisionWorld.QuerySwept"/> fix, on REAL
/// shipped map geometry rather than a synthetic lattice.
///
/// <para>The symptom this guards is "Vortex shots go through walls" — a rail fired across a height change
/// whose beam and impact land somewhere past the surface it should have stopped on. Two independent causes
/// were found: the <c>MOVE_WORLDONLY</c> filter (fixed in b21ec91) and the swept broadphase silently
/// dropping candidate brushes (fixed 2026-07-27). This pins the second at the scale it actually bites.</para>
///
/// <para>What is asserted is CONSERVATISM of the broadphase, which is what makes a trace correct: every
/// brush the shot's swept volume genuinely touches must be handed to the narrowphase. Ground truth is an
/// exact segment/AABB slab test over EVERY brush in the map — no sampling, no reliance on the structure
/// being tested. A brush in the ground-truth set but missing from the broadphase result is precisely a
/// surface the shot would pass through.</para>
///
/// <para>Trajectories are deliberately rail-shaped: long horizontal reach plus real vertical travel (up or
/// down a storey), which is the case the dropped-brush bug needed. Grid cells are XY buckets spanning all
/// Z, so a segment that changes height moves through cells an earlier part of the sweep already visited at
/// a different altitude — the exact condition under which the old mark-on-sight logic pinned a brush out of
/// the candidate set before the segment that hits it ever got to test it.</para>
///
/// <para>Self-skips when the map data isn't mounted (same convention as the other real-data suites).</para>
/// </summary>
public class HitscanHeightChangeTests
{
    private static readonly string DataDir = TestPaths.Data;

    /// <summary>Exact segment vs AABB (slab method), returning the ENTRY fraction. No sampling — a thin
    /// brush cannot slip between steps. <paramref name="entry"/> orders hits along the ray, which is what
    /// separates "dropped a surface the shot stops on" from "dropped one behind the impact".</summary>
    private static bool SegmentHitsAabb(Vector3 a, Vector3 b, Vector3 mn, Vector3 mx, out float entry)
    {
        entry = 0f;
        float tmin = 0f, tmax = 1f;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? a.X : axis == 1 ? a.Y : a.Z;
            float d = (axis == 0 ? b.X : axis == 1 ? b.Y : b.Z) - o;
            float lo = axis == 0 ? mn.X : axis == 1 ? mn.Y : mn.Z;
            float hi = axis == 0 ? mx.X : axis == 1 ? mx.Y : mx.Z;
            if (System.MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi) return false;    // parallel and outside the slab
                continue;
            }
            float t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = System.MathF.Max(tmin, t1);
            tmax = System.MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        entry = tmin;
        return true;
    }

    private static CollisionWorld? LoadShippedMap(string wanted, out string mapName)
    {
        mapName = "";
        if (!Directory.Exists(DataDir))
            return null;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountContentRoot(DataDir))
            return null;
        string? pick = vfs.Find("maps/", "bsp")
            .FirstOrDefault(m => Path.GetFileNameWithoutExtension(m)
                .Equals(wanted, System.StringComparison.OrdinalIgnoreCase));
        if (pick is null)
            return null;                  // this map isn't in the mounted set — self-skip
        mapName = Path.GetFileName(pick);
        BspData bsp = BspReader.Read(vfs.ReadBytes(pick));
        CollisionWorld world = BspCollisionBuilder.Build(bsp).World;
        world.BuildGrid();
        return world;
    }

    /// <summary>
    /// Run across several shipped maps with different vertical vocabularies, because the bug's trigger is
    /// geometric: stormkeep (towers + courtyards), implosion (the stacked-arena layout the 10-bot soaks
    /// use), catharsis (the long sightlines that made it the perf-campaign's worst case). A broadphase
    /// defect that shows on one layout and not another is exactly what a single-map test would miss.
    /// </summary>
    [Theory]
    [InlineData("stormkeep")]
    [InlineData("implosion")]
    [InlineData("catharsis")]
    [InlineData("darkzone")]
    [InlineData("space-elevator")]
    [InlineData("solarium")]
    public void RailShotsAcrossHeightChanges_NeverSkipASurfaceTheyPassThrough(string map)
    {
        CollisionWorld? maybe = LoadShippedMap(map, out string mapName);
        if (maybe is null)
            return;                       // map data not mounted — self-skip
        CollisionWorld world = maybe;

        // Every brush in the map, for the ground-truth pass.
        var all = new List<Brush>();
        world.Query(new Vector3(-1e6f), new Vector3(1e6f), all);
        Assert.NotEmpty(all);

        // World bounds from the brush set, so shots stay inside the playable volume.
        Vector3 wmin = all[0].Mins, wmax = all[0].Maxs;
        foreach (Brush b in all)
        {
            wmin = Vector3.Min(wmin, b.Mins);
            wmax = Vector3.Max(wmax, b.Maxs);
        }

        // Deterministic LCG — a fixed trajectory set, so a failure is reproducible.
        uint seed = 0x5EED1234;
        float Next()
        {
            seed = unchecked(seed * 1664525u + 1013904223u);
            return (seed >> 8) / (float)(1 << 24);
        }
        Vector3 PointInWorld() => new(
            wmin.X + Next() * (wmax.X - wmin.X),
            wmin.Y + Next() * (wmax.Y - wmin.Y),
            wmin.Z + Next() * (wmax.Z - wmin.Z));

        int shots = 0, checkedBrushes = 0, blockingMissed = 0;
        var missed = new List<string>();

        // Rail geometry: a point trace (mins == maxs == 0), long reach, real vertical travel.
        for (int i = 0; i < 4000 && shots < 400; i++)
        {
            Vector3 from = PointInWorld(), to = PointInWorld();
            Vector3 d = to - from;
            float flat = new Vector2(d.X, d.Y).Length();
            if (flat < 600f || System.MathF.Abs(d.Z) < 150f)
                continue;                 // not a cross-the-room shot with a height change
            shots++;

            var swept = new List<Brush>();
            world.QuerySwept(from, to, Vector3.Zero, Vector3.Zero, swept);
            var offered = new HashSet<Brush>(swept);

            Brush? nearest = null;
            float nearestT = float.MaxValue;
            foreach (Brush b in all)
            {
                // Shrink by an epsilon so a knife-edge tangency (where a broadphase is entitled to differ)
                // never fails the assert; a genuine drop is gross, not marginal.
                var mn = new Vector3(b.Mins.X + 0.05f, b.Mins.Y + 0.05f, b.Mins.Z + 0.05f);
                var mx = new Vector3(b.Maxs.X - 0.05f, b.Maxs.Y - 0.05f, b.Maxs.Z - 0.05f);
                if (mn.X > mx.X || mn.Y > mx.Y || mn.Z > mx.Z)
                    continue;             // degenerate/thin brush — skip rather than guess
                if (!SegmentHitsAabb(from, to, mn, mx, out float entry))
                    continue;
                checkedBrushes++;
                if (entry < nearestT) { nearestT = entry; nearest = b; }
                if (!offered.Contains(b))
                    missed.Add($"shot {from} -> {to} passes through brush [{b.Mins} .. {b.Maxs}]");
            }

            // The gameplay-visible case: the surface the shot actually STOPS on. Dropping a brush behind
            // the impact point changes nothing a player can see; dropping the nearest one is the shot
            // visibly travelling through a wall.
            if (nearest is not null && !offered.Contains(nearest))
                blockingMissed++;
        }

        Assert.True(shots > 50, $"'{mapName}': only {shots} qualifying trajectories — widen the generator");
        Assert.True(checkedBrushes > 0, $"'{mapName}': no shot intersected any brush — generator is not aimed at geometry");
        Assert.True(missed.Count == 0,
            $"'{mapName}': {missed.Count} surface(s) dropped by the swept broadphase across {shots} rail shots "
            + $"({checkedBrushes} genuine intersections); {blockingMissed} of those shots "
            + $"({100.0 * blockingMissed / shots:0.0}%) lost the surface they STOP on — a beam and impact "
            + $"that visibly travel through a wall.\n"
            + string.Join("\n", missed.Take(5)));
    }
}
