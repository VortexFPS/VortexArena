using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Differential test for the entity area-grid broadphase (D1, <see cref="XonoticGodot.Engine.Collision.EntityAreaGrid"/>):
/// the grid must return the SAME entities as the old flat O(n) linear scan, only with fewer candidates. Builds
/// randomized entity sets (positions, mixed sizes including the oversized/<c>areagrid_outside</c> path, plus
/// post-link MOVES and REMOVALS to exercise relink/unlink) and asserts:
/// <list type="number">
///   <item><b>EntitiesInBox is a conservative superset</b> — it never MISSES an entity whose AABB overlaps the
///         query box, and never returns a freed entity or a duplicate. (The grid partitions only XY, like DP, so
///         it may return extra XY-overlapping-but-Z-disjoint candidates — those the precise per-call test trims.)</item>
///   <item><b>FindInRadius matches a brute-force nearest-point scan</b> EXACTLY — the end-to-end result the
///         splash-damage path depends on is unchanged.</item>
///   <item><b>FindInBox matches a brute-force AbsMin/AbsMax overlap scan</b> EXACTLY — the precise AABB test
///         lives inside the query (DP <c>findbox</c>), which the telefrag/volume/fire-transfer callers that
///         dropped their own <c>boxesoverlap</c> depend on.</item>
/// </list>
/// </summary>
[Collection("GlobalState")]
public class EntityAreaGridDifferentialTests
{
    private static readonly Solid[] Solids =
        { Solid.Not, Solid.Trigger, Solid.BBox, Solid.SlideBox, Solid.Bsp, Solid.Corpse };

    private static Vector3 Rand(Random r, float span) => new(
        (float)(r.NextDouble() * 2 - 1) * span,
        (float)(r.NextDouble() * 2 - 1) * span,
        (float)(r.NextDouble() * 2 - 1) * span);

    private static bool Overlap(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X <= bMax.X && aMax.X >= bMin.X &&
        aMin.Y <= bMax.Y && aMax.Y >= bMin.Y &&
        aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

    private static EntityService BuildWorld(int n, int seed, out List<Entity> spawned)
    {
        var es = new EntityService();
        var r = new Random(seed);
        spawned = new List<Entity>(n);
        for (int i = 0; i < n; i++)
        {
            Entity e = es.Spawn();
            e.Solid = Solids[r.Next(Solids.Length)];
            // Mostly small entities; ~10% are large enough to exercise the oversized (areagrid_outside) path.
            float s = r.NextDouble() < 0.1 ? (float)(r.NextDouble() * 3000 + 600) : (float)(r.NextDouble() * 60 + 1);
            es.SetSize(e, new Vector3(-s, -s, -s * 0.5f), new Vector3(s, s, s * 1.5f));
            es.SetOrigin(e, Rand(r, 4000f));
            spawned.Add(e);
        }
        return es;
    }

    [Fact]
    public void EntitiesInBox_neverMissesAnAabbOverlap_andHasNoDupesOrFreed()
    {
        EntityService es = BuildWorld(400, 1234, out _);
        var r = new Random(99);
        var grid = new List<Entity>();

        for (int q = 0; q < 400; q++)
        {
            Vector3 c = Rand(r, 4200f);
            float h = (float)(r.NextDouble() * 400 + 1);
            Vector3 mins = c - new Vector3(h, h, h), maxs = c + new Vector3(h, h, h);
            es.EntitiesInBox(mins, maxs, grid);

            Assert.Equal(grid.Count, grid.Distinct().Count());   // no duplicates
            Assert.DoesNotContain(grid, e => e.IsFreed);         // no freed entities

            // Conservative: every entity whose AABB overlaps the box must be present (zero misses).
            var gridSet = new HashSet<Entity>(grid);
            foreach (Entity e in es.All)
                if (!e.IsFreed && Overlap(mins, maxs, e.Origin + e.Mins, e.Origin + e.Maxs))
                    Assert.Contains(e, gridSet);
        }
    }

    [Fact]
    public void FindInRadius_matchesBruteForce_acrossMovesAndRemovals()
    {
        EntityService es = BuildWorld(300, 777, out List<Entity> spawned);
        var r = new Random(42);

        // Mutate the live set so the grid must relink + unlink: move every 3rd, remove every 5th.
        for (int i = 0; i < spawned.Count; i++)
        {
            if (i % 3 == 0) es.SetOrigin(spawned[i], Rand(r, 4000f));
            if (i % 5 == 0) es.Remove(spawned[i]);
        }

        var got = new List<Entity>();
        for (int q = 0; q < 400; q++)
        {
            Vector3 origin = Rand(r, 4200f);
            float radius = (float)(r.NextDouble() * 500 + 1);
            es.FindInRadius(origin, radius, got);

            // Brute force: nearest point on each bbox within radius (the same metric the grid path applies).
            var brute = new List<Entity>();
            float r2 = radius * radius;
            foreach (Entity e in es.All)
            {
                if (e.IsFreed) continue;
                Vector3 nearest = Vector3.Clamp(origin, e.Origin + e.Mins, e.Origin + e.Maxs);
                if ((nearest - origin).LengthSquared() <= r2)
                    brute.Add(e);
            }
            Assert.Equal(new HashSet<Entity>(brute), new HashSet<Entity>(got));
        }
    }

    [Fact]
    public void EntitiesInBox_dedupsNegativeIndexClientEdicts()
    {
        // ClientManager gives client edicts NEGATIVE indices (they live outside the engine table's dense
        // slots) and the sim links them into the grid every tick (SimulationLoop: LinkEdict(c) after
        // ClientMove). A player straddling a cell boundary must still be returned ONCE: the original dedup
        // indexed the tag array by the raw Entity.Index, whose bounds check silently skipped negatives — so
        // every splash blast (RadiusDamage iterates FindInRadius) damaged + pushed a player once per
        // overlapped 256-unit cell, multiplying damage/knockback 2-5x depending on where they stood.
        var es = new EntityService();
        var p = new Entity { Index = -1, ClassName = "player" };
        p.Mins = new Vector3(-16, -16, -24);
        p.Maxs = new Vector3(16, 16, 45);
        p.Origin = new Vector3(0f, 0f, 25f);   // AABB straddles the X=0 and Y=0 cell lines -> 4 grid cells
        es.LinkEdict(p);

        var box = new List<Entity>();
        es.EntitiesInBox(new Vector3(-300, -300, -300), new Vector3(300, 300, 300), box);
        Assert.Single(box, e => ReferenceEquals(e, p));

        var radius = new List<Entity>();
        es.FindInRadius(new Vector3(0, 0, 25), 200f, radius);
        Assert.Single(radius, e => ReferenceEquals(e, p));
    }

    [Fact]
    public void FindInBox_matchesBruteForce_acrossMovesAndRemovals()
    {
        // FindInBox = grid broadphase + precise AbsMin/AbsMax overlap INSIDE the query (DP findbox /
        // World_EntitiesInBox — upstream b6e02fe3's telefrag findbox). Callers that replaced the old
        // findradius + boxesoverlap pair depend on this being exactly the linked-AABB overlap set.
        EntityService es = BuildWorld(300, 555, out List<Entity> spawned);
        var r = new Random(7);
        for (int i = 0; i < spawned.Count; i++)
        {
            if (i % 3 == 0) es.SetOrigin(spawned[i], Rand(r, 4000f));
            if (i % 5 == 0) es.Remove(spawned[i]);
        }

        var got = new List<Entity>();
        for (int q = 0; q < 400; q++)
        {
            Vector3 c = Rand(r, 4200f);
            Vector3 h = new(
                (float)(r.NextDouble() * 400 + 1),
                (float)(r.NextDouble() * 400 + 1),
                (float)(r.NextDouble() * 400 + 1));
            Vector3 mins = c - h, maxs = c + h;
            es.FindInBox(mins, maxs, got);

            Assert.Equal(got.Count, got.Distinct().Count());     // no duplicates
            Assert.DoesNotContain(got, e => e.IsFreed);          // no freed entities

            var brute = new List<Entity>();
            foreach (Entity e in es.All)
                if (!e.IsFreed && Overlap(mins, maxs, e.AbsMin, e.AbsMax))
                    brute.Add(e);
            Assert.Equal(new HashSet<Entity>(brute), new HashSet<Entity>(got));
        }
    }

    [Fact]
    public void ServerFindInBox_dedupsLinkedPlayer_andMergesUnlinkedMove()
    {
        // ServerEntityService.FindInBox must mirror the FindInRadius player merge: a grid-linked player is
        // returned ONCE (not grid + merge), and a player whose Origin moved without a relink (the fresh-player
        // window) is still found via the live Origin+Mins/Maxs merge — telefrag targets are mostly players.
        var inner = new EntityService();
        var server = new ServerEntityService(inner);

        Entity inside = inner.Spawn();
        inner.SetSize(inside, new Vector3(-8, -8, -8), new Vector3(8, 8, 8));
        inner.SetOrigin(inside, new Vector3(100, 0, 0));
        Entity outside = inner.Spawn();
        inner.SetSize(outside, new Vector3(-8, -8, -8), new Vector3(8, 8, 8));
        inner.SetOrigin(outside, new Vector3(1000, 0, 0));

        var p = new Player { Index = -1 };                       // client edicts use the negative index space
        p.Mins = new Vector3(-16, -16, -24);
        p.Maxs = new Vector3(16, 16, 45);
        p.Origin = new Vector3(120, 0, 0);
        server.RegisterPlayer(p);                                // links: found via grid AND merge -> dedup

        var got = new List<Entity>();
        server.FindInBox(new Vector3(64, -64, -64), new Vector3(160, 64, 64), got);
        Assert.Contains(inside, got);
        Assert.DoesNotContain(outside, got);
        Assert.Single(got, e => ReferenceEquals(e, p));          // linked player exactly once

        // Raw origin write (no relink): grid/AbsMin still hold the OLD cells — the live-box merge must find it.
        p.Origin = new Vector3(-500, 0, 0);
        server.FindInBox(new Vector3(-560, -64, -64), new Vector3(-440, 64, 64), got);
        Assert.Single(got, e => ReferenceEquals(e, p));
    }

    [Fact]
    public void Remove_thenReSpawn_reusesSlotWithoutStaleGridEntries()
    {
        var es = new EntityService();
        // Fill a slot, position it, free it — then re-spawn (reuses the slot) and confirm the OLD entity is gone
        // from queries and the NEW one is found where it actually is (no stale cell membership for the slot).
        Entity a = es.Spawn();
        es.SetSize(a, new Vector3(-8, -8, -8), new Vector3(8, 8, 8));
        es.SetOrigin(a, new Vector3(1000, 0, 0));
        es.Remove(a);

        var near1000 = new List<Entity>();
        es.EntitiesInBox(new Vector3(990, -10, -10), new Vector3(1010, 10, 10), near1000);
        Assert.DoesNotContain(a, near1000);

        Entity b = es.Spawn();
        Assert.Equal(a.Index, b.Index);          // same slot reused
        es.SetSize(b, new Vector3(-8, -8, -8), new Vector3(8, 8, 8));
        es.SetOrigin(b, new Vector3(-2000, 0, 0));

        es.EntitiesInBox(new Vector3(990, -10, -10), new Vector3(1010, 10, 10), near1000);
        Assert.DoesNotContain(b, near1000);      // b is at -2000, not 1000

        var nearMinus2000 = new List<Entity>();
        es.EntitiesInBox(new Vector3(-2010, -10, -10), new Vector3(-1990, 10, 10), nearMinus2000);
        Assert.Contains(b, nearMinus2000);
    }
}
