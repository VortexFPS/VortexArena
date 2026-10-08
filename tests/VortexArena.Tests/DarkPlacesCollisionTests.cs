using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The DarkPlaces-exact collision the shared library offers as options (and legacy mode always uses):
/// patch triangles (<see cref="PatchCollisionMode.DarkPlacesTriangles"/>), the bounding interval hierarchy
/// (<see cref="CollisionBih"/>), triangle meshes for model entities (<see cref="CollisionMesh"/>), the
/// lengthened trace (<see cref="TraceExtension"/>), the monster-only MOVE_MISSILE margin, and DarkPlaces'
/// own arithmetic. None of it needs game data; what a DarkPlaces server actually answers is checked against
/// recorded traces in <see cref="DarkPlacesTraceParityTests"/>.
/// </summary>
public class DarkPlacesCollisionTests
{
    private const int SolidOpaque = 0x20000001 ^ 0x20000000;   // CONTENTS_SOLID, not translucent

    // A flat 3x3 patch over [0,128]^2 at z, in a map with no brushes.
    private static BspData FlatPatchBsp(float z = 0f, bool reversed = false)
    {
        var verts = new List<BspVertex>();
        float[] coords = { 0f, 64f, 128f };
        foreach (float y in coords)
            foreach (float x in reversed ? coords.Reverse() : coords)
                verts.Add(new BspVertex(new Vector3(x, y, z), Vector2.Zero, Vector2.Zero, new Vector3(0, 0, 1), new BspColor(255, 255, 255, 255)));
        return new BspData
        {
            Vertices = verts.ToArray(),
            Faces = new[] { new BspFace(0, -1, BspFaceType.Patch, 0, 9, 0, 0, -1, 3, 3) },
            Textures = new[] { new BspTexture("textures/test/floor", 0, SolidOpaque) },
        };
    }

    private static readonly BspCollisionOptions Triangles = new() { PatchCollision = PatchCollisionMode.DarkPlacesTriangles };

    [Fact]
    public void The_default_build_has_no_triangles_no_hierarchy_and_no_flags()
    {
        // (the defaults proper: BspCollisionOptions.Default can be redirected for a trial run, see its TrialVariable)
        BspCollisionBuilder.Result plain = BspCollisionBuilder.Build(FlatPatchBsp(), null, new BspCollisionOptions());
        BspCollisionBuilder.Result withDefaults = BspCollisionBuilder.Build(FlatPatchBsp());
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BspCollisionOptions.TrialVariable)))
            Assert.Equal(PatchCollisionMode.Slabs, BspCollisionOptions.Default.PatchCollision);
        else return;   // a trial run: the rest of this test is about the untouched default
        Assert.Equal(PatchCollisionMode.Slabs, plain.PatchCollision);
        Assert.Equal(0, plain.PatchTriangles);
        Assert.Equal(plain.World.Brushes.Count, withDefaults.World.Brushes.Count);
        Assert.NotEmpty(plain.World.Brushes);
        Assert.All(plain.World.Brushes, b => { Assert.False(b.IsTriangle); Assert.False(b.HasAabbPlanes); });
        Assert.False(plain.World.UseBih);
        Assert.Null(plain.World.Bih);
        for (int i = 0; i < plain.World.Brushes.Count; i++)
        {
            Assert.Equal(plain.World.Brushes[i].Mins, withDefaults.World.Brushes[i].Mins);
            Assert.Equal(plain.World.Brushes[i].Maxs, withDefaults.World.Brushes[i].Maxs);
            Assert.Equal(plain.World.Brushes[i].Sides.Length, withDefaults.World.Brushes[i].Sides.Length);
        }
    }

    [Fact]
    public void The_trial_variables_are_read_when_set_and_only_then()
    {
        string? patches = Environment.GetEnvironmentVariable(BspCollisionOptions.TrialVariable)?.Trim().ToLowerInvariant();
        Assert.Equal(patches is "triangles" or "triangles+bih" ? PatchCollisionMode.DarkPlacesTriangles : PatchCollisionMode.Slabs, BspCollisionOptions.Default.PatchCollision);
        Assert.Equal(patches == "triangles+bih" ? CollisionBroadphase.Bih : CollisionBroadphase.Grid, BspCollisionOptions.Default.Broadphase);
        Assert.False(BspCollisionOptions.Default.CompiledBrushesHaveAabbPlanes);
        Assert.False(BspCollisionOptions.Default.DarkPlacesBrushPoints);
        bool monstersOnly = string.Equals(Environment.GetEnvironmentVariable(TraceService.MissileTrialVariable)?.Trim(), "monsters", StringComparison.OrdinalIgnoreCase);
        var service = new TraceService(EmptyWorld());
        Assert.Equal(monstersOnly, service.MissileGrowsOnlyAgainstMonsters);
        Assert.False(service.DarkPlacesArithmetic);
    }

    [Fact]
    public void A_patch_built_as_DarkPlaces_triangles_is_a_surface_not_a_slab()
    {
        BspCollisionBuilder.Result built = BspCollisionBuilder.Build(FlatPatchBsp(), null, Triangles);
        Assert.Equal(PatchCollisionMode.DarkPlacesTriangles, built.PatchCollision);
        // a flat patch tessellates at level 0: its four corners, two triangles
        Assert.Equal(2, built.PatchTriangles);
        Assert.All(built.World.Brushes, b => Assert.True(b.IsTriangle));
        var trace = new TraceService(built.World);
        var mover = new Entity { Solid = Solid.SlideBox };

        // A box dropped onto it stops with its underside the impact nudge (1/32) above the surface.
        TraceResult drop = trace.Trace(new Vector3(64, 64, 64), new Vector3(-8), new Vector3(8), new Vector3(64, 64, -64), MoveFilter.Normal, mover);
        Assert.Equal(8 + Collision.ImpactNudge, drop.EndPos.Z, 4);
        Assert.Equal("textures/test/floor", drop.DpHitTextureName);

        // A box wholly below the surface is in the open: there is no skirt under a triangle.
        TraceResult below = trace.Trace(new Vector3(64, 64, -20), new Vector3(-8), new Vector3(8), new Vector3(64, 64, -20), MoveFilter.Normal, mover);
        Assert.False(below.StartSolid);
        // One that straddles it starts solid.
        TraceResult across = trace.Trace(new Vector3(64, 64, 2), new Vector3(-8), new Vector3(8), new Vector3(64, 64, 2), MoveFilter.Normal, mover);
        Assert.True(across.StartSolid);

        // A point is never inside a triangle, even on it.
        Assert.Equal(0, trace.PointContents(new Vector3(64, 64, 0)));
        TraceResult pointAtRest = trace.Trace(new Vector3(64, 64, 0), Vector3.Zero, Vector3.Zero, new Vector3(64, 64, 0), MoveFilter.Normal, mover);
        Assert.False(pointAtRest.StartSolid);
    }

    [Fact]
    public void A_line_is_stopped_by_the_front_of_a_patch_triangle_and_passes_through_its_back()
    {
        var mover = new Entity { Solid = Solid.SlideBox };
        int stoppedFromAbove = 0, stoppedFromBelow = 0;
        foreach (bool reversed in new[] { false, true })
        {
            var trace = new TraceService(BspCollisionBuilder.Build(FlatPatchBsp(0, reversed), null, Triangles).World);
            TraceResult down = trace.Trace(new Vector3(40, 50, 64), Vector3.Zero, Vector3.Zero, new Vector3(40, 50, -64), MoveFilter.Normal, mover);
            TraceResult up = trace.Trace(new Vector3(40, 50, -64), Vector3.Zero, Vector3.Zero, new Vector3(40, 50, 64), MoveFilter.Normal, mover);
            // exactly one side of a patch stops a line, and which one follows the winding of its control points
            Assert.True(down.Fraction < 1 ^ up.Fraction < 1, $"reversed {reversed}: down {down.Fraction} up {up.Fraction}");
            if (down.Fraction < 1)
            {
                stoppedFromAbove++;
                Assert.Equal(1, down.PlaneNormal.Z, 4);
                Assert.Equal("textures/test/floor", down.DpHitTextureName);
            }
            else
            {
                stoppedFromBelow++;
                Assert.Equal(-1, up.PlaneNormal.Z, 4);
            }
            // a box is stopped from either side
            TraceResult boxUp = trace.Trace(new Vector3(40, 50, -64), new Vector3(-4), new Vector3(4), new Vector3(40, 50, 64), MoveFilter.Normal, mover);
            Assert.True(boxUp.Fraction < 1);
        }
        Assert.Equal(1, stoppedFromAbove);
        Assert.Equal(1, stoppedFromBelow);
    }

    // ---- the hierarchy ---------------------------------------------------------------------------------

    private static CollisionWorld RandomBoxWorld(int seed, int count, out List<(Vector3 Mins, Vector3 Maxs)> boxes)
    {
        Random random = new(seed);
        var world = new CollisionWorld();
        boxes = new();
        for (int i = 0; i < count; i++)
        {
            Vector3 centre = new(random.Next(-2000, 2000), random.Next(-2000, 2000), random.Next(-500, 500));
            Vector3 half = new(random.Next(4, 120), random.Next(4, 120), random.Next(4, 60));
            boxes.Add((centre - half, centre + half));
            world.AddBrush(Brush.FromBox(centre - half, centre + half, SuperContents.Solid));
        }
        world.BuildGrid();
        return world;
    }

    [Fact]
    public void The_hierarchy_holds_every_leaf_once_and_a_walk_misses_none_the_box_touches()
    {
        CollisionWorld world = RandomBoxWorld(7, 600, out List<(Vector3 Mins, Vector3 Maxs)> boxes);
        world.UseBih = true;
        CollisionBih bih = world.Bih!;
        Assert.Equal(600, bih.LeafCount);
        Assert.Equal(Enumerable.Range(0, 600), bih.LeafOrder.ToArray().OrderBy(i => i));

        Random random = new(11);
        var found = new List<Brush>();
        var walker = new CollisionBih.Walker();
        var leaves = new List<int>();
        int[] rank = new int[600];
        for (int i = 0; i < 600; i++) rank[bih.LeafOrder[i]] = i;
        for (int n = 0; n < 120; n++)
        {
            Vector3 start = new(random.Next(-2200, 2200), random.Next(-2200, 2200), random.Next(-600, 600));
            Vector3 end = n % 3 == 0 ? start : start + new Vector3(random.Next(-900, 900), random.Next(-900, 900), random.Next(-300, 300));
            Vector3 mins = new(-16, -16, -24), maxs = new(16, 16, 45);
            found.Clear();
            world.QuerySwept(start, end, mins, maxs, found);
            // every brush the swept box really passes through is a candidate (an exact sweep finds a hit or a solid start)
            var mover = new Entity { Solid = Solid.SlideBox };
            for (int b = 0; b < boxes.Count; b++)
            {
                var lone = new CollisionWorld();
                lone.AddBrush(world.Brushes[b]);
                TraceResult alone = new TraceService(lone).Trace(start, mins, maxs, end, MoveFilter.WorldOnly, mover);
                if (alone.Fraction < 1 || alone.StartSolid) Assert.Contains(world.Brushes[b], found);
            }
            // and the candidates come in the hierarchy's order
            leaves.Clear();
            Vector3 centre = (mins + maxs) * 0.5f;
            bih.QuerySwept(walker, start + centre, end + centre, mins - centre, maxs - centre, leaves);
            for (int i = 1; i < leaves.Count; i++) Assert.True(rank[leaves[i - 1]] < rank[leaves[i]]);
            Assert.Equal(leaves.Count, leaves.Distinct().Count());
        }
    }

    [Fact]
    public void A_trace_answers_the_same_through_the_grid_and_through_the_hierarchy()
    {
        CollisionWorld grid = RandomBoxWorld(21, 500, out _), tree = RandomBoxWorld(21, 500, out _);
        tree.UseBih = true;
        var a = new TraceService(grid);
        var b = new TraceService(tree);
        var mover = new Entity { Solid = Solid.SlideBox };
        Random random = new(5);
        int hits = 0;
        for (int n = 0; n < 3000; n++)
        {
            Vector3 start = new(random.Next(-2200, 2200), random.Next(-2200, 2200), random.Next(-600, 600));
            Vector3 end = start + new Vector3(random.Next(-1500, 1500), random.Next(-1500, 1500), random.Next(-400, 400));
            bool point = n % 2 == 0;
            Vector3 mins = point ? Vector3.Zero : new Vector3(-16, -16, -24), maxs = point ? Vector3.Zero : new Vector3(16, 16, 45);
            TraceResult x = a.Trace(start, mins, maxs, end, MoveFilter.WorldOnly, mover), y = b.Trace(start, mins, maxs, end, MoveFilter.WorldOnly, mover);
            Assert.Equal(x.Fraction, y.Fraction);
            Assert.Equal(x.StartSolid, y.StartSolid);
            Assert.Equal(x.EndPos, y.EndPos);
            if (x.Fraction < 1) hits++;
            Assert.Equal(a.PointContents(start), b.PointContents(start));
        }
        Assert.True(hits > 300, $"only {hits} of the traces hit anything");
    }

    // ---- model meshes ----------------------------------------------------------------------------------

    // A closed box of twelve triangles, wound to face outward.
    private static CollisionMesh BoxMesh(Vector3 lo, Vector3 hi, int maxTriangles = 64)
    {
        Vector3[] v =
        {
            new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(lo.X, hi.Y, lo.Z),
            new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z),
        };
        // TriangleNormal(a, b, c) is (a - b) x (c - b): the front of a triangle a, b, c is where its
        // corners run clockwise.
        int[] quads = { 0, 1, 2, 3, /* bottom */ 7, 6, 5, 4, /* top */ 4, 5, 1, 0, /* -y */ 6, 7, 3, 2, /* +y */ 7, 4, 0, 3, /* -x */ 5, 6, 2, 1 /* +x */ };
        var elements = new List<int>();
        for (int q = 0; q < 24; q += 4)
        {
            elements.AddRange(new[] { quads[q], quads[q + 1], quads[q + 2] });
            elements.AddRange(new[] { quads[q], quads[q + 2], quads[q + 3] });
        }
        return CollisionMesh.Create(v, elements.ToArray(), new int[12], new[] { new CollisionMesh.Surface(SuperContents.Solid | SuperContents.Opaque, 0, "models/test/crate") }, maxTriangles)!;
    }

    private sealed class OneEntity : TraceService.IEntityProvider
    {
        public Entity Entity = new();
        public CollisionMesh? Mesh;
        public float PitchSign = -1;
        public IReadOnlyList<Entity> SolidEntities => new[] { Entity };
        public void EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results) { results.Clear(); results.Add(Entity); }
        public bool TryGetEntityBrushModel(Entity e, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld)
        {
            localBrushes = Array.Empty<Brush>();
            toWorld = EntityMatrix.Identity;
            return false;
        }
        public bool TryGetEntityMeshModel(Entity e, MoveFilter filter, out CollisionMesh? mesh, out EntityMatrix toWorld)
        {
            mesh = Mesh;
            toWorld = EntityMatrix.FromQuakeEntity(e.Origin, new Vector3(PitchSign * e.Angles.X, e.Angles.Y, e.Angles.Z));
            return Mesh is not null;
        }
    }

    private static CollisionWorld EmptyWorld()
    {
        var world = new CollisionWorld();
        world.BuildGrid();
        return world;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_turned_crate_is_hit_where_its_mesh_is_and_not_where_only_its_box_is(bool darkPlacesArithmetic)
    {
        // A 64-unit crate turned 45 degrees about the vertical. Its entity box is what setmodel gives a
        // turned SOLID_BSP model - the box that holds it at any yaw, 45.25 units to a side.
        float yawBox = MathF.Sqrt(32 * 32 * 2);
        var provider = new OneEntity
        {
            Mesh = BoxMesh(new Vector3(-32, -32, 0), new Vector3(32, 32, 64)),
            Entity = new Entity { Index = 1, Solid = Solid.Bsp, Origin = new Vector3(1000, 2000, 0), Angles = new Vector3(0, 45, 0), Mins = new Vector3(-yawBox, -yawBox, 0), Maxs = new Vector3(yawBox, yawBox, 64) },
        };
        var withMesh = new TraceService(EmptyWorld(), provider) { DarkPlacesArithmetic = darkPlacesArithmetic };
        var asBox = new TraceService(EmptyWorld(), new OneEntity { Entity = provider.Entity }) { DarkPlacesArithmetic = darkPlacesArithmetic };
        var mover = new Entity { Solid = Solid.BBox };

        // Along +x through the crate's centre the turned crate's corner comes first: 45.25 units out, where its box also ends.
        Vector3 from = new(900, 2000, 32), to = new(1100, 2000, 32);
        TraceResult corner = withMesh.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Normal, mover);
        Assert.Equal(1000 - yawBox, corner.EndPos.X, 1);
        Assert.Same(provider.Entity, corner.Ent);
        Assert.Equal("models/test/crate", corner.DpHitTextureName);
        // the face it met is one of the two that make that corner
        Assert.Equal(-MathF.Sqrt(0.5f), corner.PlaneNormal.X, 3);
        Assert.Equal(MathF.Sqrt(0.5f), MathF.Abs(corner.PlaneNormal.Y), 3);

        // 30 units to the side the same line meets the mesh 30 units further in (the face runs at 45 degrees),
        // where the box is still at its edge: the mesh is hit where the box would long have been.
        from = new(900, 2030, 32);
        to = new(1100, 2030, 32);
        TraceResult face = withMesh.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Normal, mover);
        TraceResult box = asBox.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Normal, mover);
        Assert.Equal(1000 - yawBox + 30, face.EndPos.X, 1);
        Assert.Equal(1000 - yawBox, box.EndPos.X, 1);

        // Past the corner of the mesh but inside the box (x and y both 40 off centre: the mesh reaches
        // 45.25 along the axes only): the box stops the line, the crate does not.
        from = new(1040, 2040, 100);
        to = new(1040, 2040, -20);
        Assert.True(asBox.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Normal, mover).Fraction < 1);
        Assert.Equal(1, withMesh.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Normal, mover).Fraction);

        // A small box dropped onto the lid rests on it; dropped beside the mesh, inside the entity's box, it falls through.
        Vector3 lo = new(-4, -4, 0), hi = new(4, 4, 8);
        TraceResult onLid = withMesh.Trace(new Vector3(1000, 2000, 120), lo, hi, new Vector3(1000, 2000, -20), MoveFilter.Normal, mover);
        Assert.Equal(64 + Collision.ImpactNudge, onLid.EndPos.Z, 3);
        Assert.Equal(1, onLid.PlaneNormal.Z, 3);
        Assert.Equal(1, withMesh.Trace(new Vector3(1040, 2040, 120), lo, hi, new Vector3(1040, 2040, -20), MoveFilter.Normal, mover).Fraction);
        Assert.True(asBox.Trace(new Vector3(1040, 2040, 120), lo, hi, new Vector3(1040, 2040, -20), MoveFilter.Normal, mover).Fraction < 1);

        // A mesh is a surface: a box wholly inside the crate is in the open, one across its lid is not,
        // and a point at rest is never in it.
        Assert.False(withMesh.Trace(new Vector3(1000, 2000, 30), lo, hi, new Vector3(1000, 2000, 30), MoveFilter.Normal, mover).StartSolid);
        Assert.True(withMesh.Trace(new Vector3(1000, 2000, 60), lo, hi, new Vector3(1000, 2000, 60), MoveFilter.Normal, mover).StartSolid);
        Assert.False(withMesh.Trace(new Vector3(1000, 2000, 30), Vector3.Zero, Vector3.Zero, new Vector3(1000, 2000, 30), MoveFilter.Normal, mover).StartSolid);
        // and a line from inside it goes out through the back of its faces
        Assert.Equal(1, withMesh.Trace(new Vector3(1000, 2000, 30), Vector3.Zero, Vector3.Zero, new Vector3(1000, 2000, 300), MoveFilter.Normal, mover).Fraction);
    }

    [Fact]
    public void Only_a_solid_bsp_entity_or_a_hitmodel_move_is_clipped_by_its_mesh()
    {
        var provider = new OneEntity
        {
            Mesh = BoxMesh(new Vector3(-8, -8, 0), new Vector3(8, 8, 16)),
            Entity = new Entity { Index = 1, Solid = Solid.BBox, Origin = Vector3.Zero, Mins = new Vector3(-32, -32, 0), Maxs = new Vector3(32, 32, 64) },
        };
        var trace = new TraceService(EmptyWorld(), provider);
        var mover = new Entity { Solid = Solid.BBox };
        Vector3 from = new(-100, 20, 8), to = new(100, 20, 8);   // through the box, past the mesh
        Assert.True(trace.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Normal, mover).Fraction < 1);
        Assert.Equal(1, trace.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.HitModel, mover).Fraction);
        Assert.True(trace.Trace(new Vector3(-100, 0, 8), Vector3.Zero, Vector3.Zero, new Vector3(100, 0, 8), MoveFilter.HitModel, mover).Fraction < 1);
    }

    [Fact]
    public void A_mesh_is_made_only_from_what_is_sound_and_refused_when_too_large()
    {
        Vector3[] vertices = { new(0, 0, 0), new(8, 0, 0), new(0, 8, 0), new(float.NaN, 0, 0) };
        var surfaces = new[] { new CollisionMesh.Surface(SuperContents.Solid, 0, null) };
        // triangle 1 names a vertex that does not exist, 2 a coordinate that is not a number, 3 a surface that does not exist
        int[] elements = { 0, 1, 2, 0, 1, 9, 0, 1, 3, 0, 2, 1 };
        int[] triangleSurface = { 0, 0, 0, 5 };
        CollisionMesh mesh = CollisionMesh.Create(vertices, elements, triangleSurface, surfaces, 100)!;
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Null(CollisionMesh.Create(vertices, elements, triangleSurface, surfaces, maxTriangles: 3));
        Assert.Null(CollisionMesh.Create(vertices, new[] { 0, 1, 9 }, new[] { 0 }, surfaces, 100));
        Assert.Null(CollisionMesh.Create(vertices, Array.Empty<int>(), Array.Empty<int>(), surfaces, 100));
    }

    // ---- the smaller options ---------------------------------------------------------------------------

    [Fact]
    public void A_missile_move_is_fattened_against_everything_by_default_and_against_monsters_only_on_request()
    {
        var player = new OneEntity { Entity = new Entity { Index = 1, Solid = Solid.SlideBox, Origin = Vector3.Zero, Mins = new Vector3(-16, -16, -24), Maxs = new Vector3(16, 16, 45) } };
        var monster = new OneEntity { Entity = new Entity { Index = 2, Solid = Solid.SlideBox, Flags = EntFlags.Monster, Origin = Vector3.Zero, Mins = new Vector3(-16, -16, -24), Maxs = new Vector3(16, 16, 45) } };
        var rocket = new Entity { Solid = Solid.BBox };
        Vector3 from = new(-200, 0, 0), to = new(200, 0, 0);   // straight at the box, whose face is at x = -16

        // As this port has always had it: every entity is 15 units fatter to a MOVE_MISSILE move.
        // (Set explicitly: the initial value can be redirected for a trial run, see TraceService.MissileTrialVariable.)
        TraceService Default(OneEntity entity) => new(EmptyWorld(), entity) { MissileGrowsOnlyAgainstMonsters = false };
        Assert.Equal(-31 - Collision.ImpactNudge, Default(player).Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Missile, rocket).EndPos.X, 2);
        Assert.Equal(-16 - Collision.ImpactNudge, Default(player).Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Normal, rocket).EndPos.X, 2);

        // As DarkPlaces has it: a player is its own size, a monster 15 units fatter.
        var atPlayer = new TraceService(EmptyWorld(), player) { MissileGrowsOnlyAgainstMonsters = true };
        Assert.Equal(-16 - Collision.ImpactNudge, atPlayer.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Missile, rocket).EndPos.X, 2);
        var atMonster = new TraceService(EmptyWorld(), monster) { MissileGrowsOnlyAgainstMonsters = true };
        Assert.Equal(-31 - Collision.ImpactNudge, atMonster.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Missile, rocket).EndPos.X, 2);

        // A line that passes 10 units beside the box. DarkPlaces gathers its candidates over the fattened
        // box, so the monster is met; the player is not. (By default the candidates are gathered over the
        // move as given, so whether such a near miss counts depends on the direction of the line: an
        // axis-aligned one like this misses, a diagonal one, whose bounds are wide, hits.)
        from = new Vector3(-200, 26, 0);
        to = new Vector3(200, 26, 0);
        Assert.True(atMonster.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Missile, rocket).Fraction < 1);
        Assert.Equal(1, atPlayer.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Missile, rocket).Fraction);
        Assert.Equal(1, Default(player).Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.Missile, rocket).Fraction);
        Assert.True(Default(player).Trace(new Vector3(-200, -174, 0), Vector3.Zero, Vector3.Zero, new Vector3(200, 226, 0), MoveFilter.Missile, rocket).Fraction < 1);
    }

    [Fact]
    public void An_extended_trace_finds_a_surface_it_stops_just_short_of_and_forgets_one_beyond_its_end()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-100, -100, -16), new Vector3(100, 100, 0), SuperContents.Solid));
        world.BuildGrid();
        var trace = new TraceService(world);
        var mover = new Entity { Solid = Solid.BBox };
        Vector3 lo = new(-1), hi = new(1), start = new(0, 0, 25);

        // to within a hundredth of the floor: unextended there is no impact at all
        Vector3 end = new(0, 0, 1.01f);
        Assert.Equal(1, trace.Trace(start, lo, hi, end, MoveFilter.Normal, mover).Fraction);
        TraceResult extended = trace.TraceExtended(start, lo, hi, end, MoveFilter.Normal, mover, 1, out bool cleared);
        Assert.False(cleared);
        Assert.InRange(extended.Fraction, 0.998f, 0.9999f);
        Assert.Equal(1 + Collision.ImpactNudge, extended.EndPos.Z, 4);
        Assert.Equal(1, extended.PlaneNormal.Z);

        // an impact that lies only in the extra length is no impact
        end = new Vector3(0, 0, 1.5f);
        TraceResult shortOfIt = trace.TraceExtended(start, lo, hi, end, MoveFilter.Normal, mover, 1, out cleared);
        Assert.True(cleared);
        Assert.Equal(1, shortOfIt.Fraction);
        Assert.Equal(end, shortOfIt.EndPos);
        Assert.Equal(Vector3.Zero, shortOfIt.PlaneNormal);

        // a move of no length is not extended
        TraceExtension none = TraceExtension.Prepare(start, start, 16);
        Assert.Equal(1, none.ScaleToExtend);
        Assert.Equal(start, none.ExtendEnd);
    }

    [Fact]
    public void Start_contents_are_those_of_everything_the_box_starts_in_not_of_its_origin()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(10, -100, -100), new Vector3(100, 100, 100), SuperContents.Solid));
        world.AddBrush(Brush.FromBox(new Vector3(-100, -100, -100), new Vector3(-10, 100, 100), SuperContents.Water));
        world.BuildGrid();
        foreach (bool exact in new[] { false, true })
        {
            var trace = new TraceService(world) { DarkPlacesArithmetic = exact };
            var mover = new Entity { Solid = Solid.BBox };   // stops on solid, body and corpse: not on water
            TraceResult rest = trace.Trace(Vector3.Zero, new Vector3(-16), new Vector3(16), Vector3.Zero, MoveFilter.Normal, mover);
            Assert.True(rest.StartSolid);
            Assert.Equal(SuperContents.Solid | SuperContents.Water, trace.LastStartContents);
            Assert.Equal(0, trace.PointContents(Vector3.Zero));
            trace.Trace(Vector3.Zero, new Vector3(-4), new Vector3(4), Vector3.Zero, MoveFilter.Normal, mover);
            Assert.Equal(0, trace.LastStartContents);
        }
    }

    [Fact]
    public void A_point_at_rest_in_a_brush_is_all_solid_in_DarkPlaces_arithmetic()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-10), new Vector3(10), SuperContents.Solid));
        world.BuildGrid();
        var mover = new Entity { Solid = Solid.BBox };
        TraceResult exact = new TraceService(world) { DarkPlacesArithmetic = true }.Trace(Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, MoveFilter.Normal, mover);
        Assert.True(exact.StartSolid);
        Assert.True(exact.AllSolid);   // Collision_TracePointBrushFloat sets both
    }

    [Fact]
    public void DarkPlaces_arithmetic_changes_no_answer_by_more_than_rounding()
    {
        CollisionWorld world = RandomBoxWorld(33, 400, out _);
        var plain = new TraceService(world);
        var exact = new TraceService(world) { DarkPlacesArithmetic = true };
        var mover = new Entity { Solid = Solid.SlideBox };
        Random random = new(9);
        for (int n = 0; n < 2000; n++)
        {
            Vector3 start = new(random.Next(-2200, 2200) + 0.37f, random.Next(-2200, 2200) + 0.37f, random.Next(-600, 600) + 0.37f);
            Vector3 end = start + new Vector3(random.Next(-300, 300), random.Next(-300, 300), random.Next(-200, 200));
            Vector3 mins = n % 2 == 0 ? Vector3.Zero : new Vector3(-16, -16, -24), maxs = n % 2 == 0 ? Vector3.Zero : new Vector3(16, 16, 45);
            TraceResult a = plain.Trace(start, mins, maxs, end, MoveFilter.WorldOnly, mover), b = exact.Trace(start, mins, maxs, end, MoveFilter.WorldOnly, mover);
            Assert.Equal(a.StartSolid, b.StartSolid);
            Assert.True(Vector3.Distance(a.EndPos, b.EndPos) < 0.01f, $"{a.EndPos} against {b.EndPos}");
        }
    }
}
