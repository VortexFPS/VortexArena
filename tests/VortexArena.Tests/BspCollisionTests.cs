using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the BSP → collision split: worldspawn (<c>Models[0]</c>) goes to the static collision world, while
/// the inline <c>"*N"</c> brush models become per-entity collision geometry registered on the model catalog —
/// so moving SOLID_BSP entities (func_door/plat) clip against real brushes instead of being baked into the
/// static world (the prior bug) or falling back to an AABB.
/// </summary>
public class BspCollisionTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";
    private const int Solid = SuperContents.Solid;

    // ---- synthetic two-box BSP: one worldspawn brush + one inline-model brush -----------------------

    /// <summary>Append an axis-aligned box (6 planes/sides + 1 brush) to the lists; returns the brush index.</summary>
    private static int AddBox(List<BspPlane> planes, List<BspBrushSide> sides, List<BspBrush> brushes,
                             Vector3 mn, Vector3 mx)
    {
        int firstSide = sides.Count;
        (Vector3 n, float d)[] faces =
        {
            (new Vector3(1, 0, 0), mx.X), (new Vector3(-1, 0, 0), -mn.X),
            (new Vector3(0, 1, 0), mx.Y), (new Vector3(0, -1, 0), -mn.Y),
            (new Vector3(0, 0, 1), mx.Z), (new Vector3(0, 0, -1), -mn.Z),
        };
        foreach ((Vector3 n, float d) in faces)
        {
            sides.Add(new BspBrushSide(planes.Count, 0, -1));
            planes.Add(new BspPlane(n, d));
        }
        brushes.Add(new BspBrush(firstSide, 6, 0));
        return brushes.Count - 1;
    }

    private static BspData TwoBoxBsp()
    {
        var planes = new List<BspPlane>();
        var sides = new List<BspBrushSide>();
        var brushes = new List<BspBrush>();

        AddBox(planes, sides, brushes, new Vector3(0, 0, 0), new Vector3(64, 64, 64));      // brush 0 — world
        AddBox(planes, sides, brushes, new Vector3(100, 0, 0), new Vector3(164, 64, 64));   // brush 1 — "*1"

        return new BspData
        {
            Planes = planes.ToArray(),
            BrushSides = sides.ToArray(),
            Brushes = brushes.ToArray(),
            Textures = new[] { new BspTexture("common/solid", 0, Solid) },
            Models = new[]
            {
                new BspModel(new Vector3(0, 0, 0), new Vector3(64, 64, 64), 0, 0, 0, 1),       // model 0: brush 0
                new BspModel(new Vector3(100, 0, 0), new Vector3(164, 64, 64), 0, 0, 1, 1),    // model 1 "*1": brush 1
            },
        };
    }

    [Fact]
    public void Build_Puts_Only_Worldspawn_In_The_Static_World()
    {
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(TwoBoxBsp());

        Assert.Single(r.World.Brushes);                  // only the worldspawn brush, NOT the submodel brush
        Assert.Equal(new Vector3(0, 0, 0), r.World.Brushes[0].Mins);
        Assert.Equal(new Vector3(64, 64, 64), r.World.Brushes[0].Maxs);

        Assert.Single(r.Submodels);
        Assert.Equal("*1", r.Submodels[0].Name);
        Assert.Single(r.Submodels[0].Brushes);           // the inline model carries its own real brush
        Assert.False(r.Submodels[0].Brushes[0].IsAabb);  // a real convex brush, not an AABB box
    }

    [Fact]
    public void Submodel_Brush_Region_Is_Not_Solid_In_The_Static_World()
    {
        // A point inside the worldspawn box is solid; a point inside the submodel box's region is NOT solid in
        // the static world (the brush moved to the inline model), so a closed door isn't permanently baked in.
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(TwoBoxBsp());
        var services = new EngineServices(r.World);

        Assert.NotEqual(0, services.Trace.PointContents(new Vector3(32, 32, 32)) & Solid);  // inside world box
        Assert.Equal(0, services.Trace.PointContents(new Vector3(132, 32, 32)) & Solid);    // inside "*1" region
    }

    [Fact]
    public void Registered_Submodel_Resolves_Real_Brushes_For_A_SolidBsp_Entity()
    {
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(TwoBoxBsp());
        var services = new EngineServices(r.World);
        BspCollisionBuilder.RegisterSubmodels(r.Submodels, services.ModelsImpl);

        var door = services.Entities.Spawn();
        services.Entities.SetModel(door, "*1");
        // setmodel resolved the inline model's bounds onto the entity hull (not zero).
        Assert.Equal(new Vector3(100, 0, 0), door.Mins);
        Assert.Equal(new Vector3(164, 64, 64), door.Maxs);

        bool ok = services.EntityTable.TryGetEntityBrushModel(door, out IReadOnlyList<Brush> brushes, out _);
        Assert.True(ok);
        Assert.Single(brushes);
        // It's the REAL registered brush (reference-identical), not a freshly-built AABB fallback box.
        Assert.Same(r.Submodels[0].Brushes[0], brushes[0]);
    }

    // ---- trace_dphittexturename (TraceResult.DpHitTextureName) ---------------------------------------
    //
    // Port of DP's trace_dphittexturename (prvm_cmds.c:5242 = trace->hittexture->name; set in the SAT sweep
    // Collision_TraceBrushBrushFloat at collision.c:681/688/693 → 732). The faithful consumer is
    // world.qc:1158-1170 MoveToRandomLocationWithinBounds, which rejects a candidate whose sightline hits
    // "common/caulk". These tests prove the field is now truthfully populated from the BSP texture lump's
    // ShaderName, threaded Brush/BrushPlane → SAT sweep → TraceResult.

    /// <summary>Build a one-box static world whose brush carries the named shader (TextureIndex 0).</summary>
    private static BspData OneBoxNamed(string shader, Vector3 mn, Vector3 mx)
    {
        var planes = new List<BspPlane>();
        var sides = new List<BspBrushSide>();
        var brushes = new List<BspBrush>();
        AddBox(planes, sides, brushes, mn, mx);
        return new BspData
        {
            Planes = planes.ToArray(),
            BrushSides = sides.ToArray(),
            Brushes = brushes.ToArray(),
            Textures = new[] { new BspTexture(shader, 0, Solid) },
            Models = new[] { new BspModel(mn, mx, 0, 0, 0, 1) },
        };
    }

    [Fact]
    public void Trace_Reports_HitTextureName_Of_World_Brush()
    {
        // A box brush named "common/caulk"; trace a box from -X into its -X face.
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(OneBoxNamed("common/caulk",
            new Vector3(0, 0, 0), new Vector3(64, 64, 64)));
        var services = new EngineServices(r.World);

        Vector3 mins = new(-4, -4, -4), maxs = new(4, 4, 4);
        TraceResult tr = services.Trace.Trace(
            new Vector3(-32, 32, 32), mins, maxs, new Vector3(32, 32, 32), MoveFilter.WorldOnly, null);

        Assert.True(tr.Fraction < 1f, "the box trace must hit the wall");
        Assert.False(tr.StartSolid);
        Assert.Equal("common/caulk", tr.DpHitTextureName);
    }

    [Fact]
    public void Trace_Reports_HitTextureName_Differs_Per_Brush()
    {
        // Two independent one-box worlds with distinct shader names; trace into each, assert the correct
        // name on each impact (proves the name is per-brush, not a constant). Two separate BspData keep each
        // box's brushsides pointing at their own (index-0) texture, so the per-side path reports each name.
        var sA = new EngineServices(BspCollisionBuilder.Build(
            OneBoxNamed("a/floor", new Vector3(0, 0, 0), new Vector3(64, 64, 64))).World);
        var sB = new EngineServices(BspCollisionBuilder.Build(
            OneBoxNamed("b/wall", new Vector3(0, 0, 0), new Vector3(64, 64, 64))).World);

        Vector3 mins = new(-4, -4, -4), maxs = new(4, 4, 4);
        TraceResult tA = sA.Trace.Trace(
            new Vector3(-32, 32, 32), mins, maxs, new Vector3(32, 32, 32), MoveFilter.WorldOnly, null);
        TraceResult tB = sB.Trace.Trace(
            new Vector3(-32, 32, 32), mins, maxs, new Vector3(32, 32, 32), MoveFilter.WorldOnly, null);

        Assert.True(tA.Fraction < 1f && tB.Fraction < 1f);
        Assert.Equal("a/floor", tA.DpHitTextureName);
        Assert.Equal("b/wall", tB.DpHitTextureName);
    }

    [Fact]
    public void Trace_Miss_Leaves_HitTextureName_Null()
    {
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(OneBoxNamed("common/caulk",
            new Vector3(0, 0, 0), new Vector3(64, 64, 64)));
        var services = new EngineServices(r.World);

        // Trace well above the box — no contact.
        Vector3 mins = new(-4, -4, -4), maxs = new(4, 4, 4);
        TraceResult tr = services.Trace.Trace(
            new Vector3(-32, 32, 300), mins, maxs, new Vector3(32, 32, 300), MoveFilter.WorldOnly, null);

        Assert.Equal(1f, tr.Fraction);
        Assert.False(tr.StartSolid);
        Assert.Null(tr.DpHitTextureName);   // DP sets trace_dphittexturename = 0/string_null on a miss
    }

    [Fact]
    public void Trace_PureStartSolid_Leaves_HitTextureName_Null()
    {
        // FAITHFUL CORRECTION (vs recon §A.5 test #4): trace_dphittexturename reads trace->hittexture ONLY
        // (prvm_cmds.c:5242), which is set exclusively on an ENTER event (collision.c:732). A pure startsolid
        // (started inside, no enter event) stores the texture in the SEPARATE trace->starttexture field
        // (collision.c:753) that has NO QC accessor — so trace_dphittexturename stays NULL. We mirror that:
        // a started-inside trace reports DpHitTextureName == null even though StartSolid is true.
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(OneBoxNamed("common/caulk",
            new Vector3(0, 0, 0), new Vector3(64, 64, 64)));
        var services = new EngineServices(r.World);

        Vector3 mins = new(-4, -4, -4), maxs = new(4, 4, 4);
        // Start AND end inside the box → startsolid, no enter event.
        TraceResult tr = services.Trace.Trace(
            new Vector3(32, 32, 32), mins, maxs, new Vector3(40, 32, 32), MoveFilter.WorldOnly, null);

        Assert.True(tr.StartSolid);
        Assert.Null(tr.DpHitTextureName);
    }

    [Fact]
    public void Trace_Reports_HitTextureName_Of_Patch_Slab()
    {
        // A single flat bezier patch (3x3 control grid, all coplanar at z=0) named "mapname/grate" with SOLID
        // contents → the builder tessellates it into walkable floor slabs that must carry the patch shader, so a
        // box dropped onto it reports DpHitTextureName == that shader (DP's BIH_COLLISIONTRIANGLE → texture->name).
        BspData bsp = FlatPatchNamed("mapname/grate");
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(bsp);
        var services = new EngineServices(r.World);
        Assert.NotEmpty(r.World.Brushes);   // the patch tessellated into collision slabs

        Vector3 mins = new(-4, -4, -4), maxs = new(4, 4, 4);
        // Drop straight down onto the patch plane (z=0) from above.
        TraceResult tr = services.Trace.Trace(
            new Vector3(32, 32, 40), mins, maxs, new Vector3(32, 32, -8), MoveFilter.WorldOnly, null);

        Assert.True(tr.Fraction < 1f, "the box must land on the patch");
        Assert.Equal("mapname/grate", tr.DpHitTextureName);
    }

    /// <summary>A worldspawn BSP with one flat PATCH face (3x3 coplanar grid at z=0) carrying the named shader.</summary>
    private static BspData FlatPatchNamed(string shader)
    {
        // 3x3 control grid spanning [0,64]x[0,64] at z=0. A flat patch tessellates to coplanar triangles.
        var verts = new List<BspVertex>();
        for (int gy = 0; gy < 3; gy++)
        for (int gx = 0; gx < 3; gx++)
        {
            verts.Add(new BspVertex(
                new Vector3(gx * 32f, gy * 32f, 0f),
                new Vector2(gx * 0.5f, gy * 0.5f),
                new Vector2(gx * 0.5f, gy * 0.5f),
                new Vector3(0, 0, 1),
                new BspColor(255, 255, 255, 255)));
        }

        var face = new BspFace(
            TextureIndex: 0, EffectIndex: -1, Type: BspFaceType.Patch,
            FirstVertex: 0, VertexCount: 9, FirstIndex: 0, IndexCount: 0,
            LightmapIndex: -1, PatchWidth: 3, PatchHeight: 3);

        return new BspData
        {
            Vertices = verts.ToArray(),
            Faces = new[] { face },
            Textures = new[] { new BspTexture(shader, 0, Solid) },
            // Worldspawn model owns the one patch face (and no brushes).
            Models = new[] { new BspModel(new Vector3(0, 0, -32), new Vector3(64, 64, 8), 0, 1, 0, 0) },
        };
    }

    // ---- real data: parse the Models lump + build collision from a shipped map ----------------------

    [Fact]
    public void Real_Bsp_Models_Lump_Parses_And_Splits_Worldspawn()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? bspPath = vfs.Find("maps/", "bsp").FirstOrDefault();
        if (bspPath is null) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        Assert.True(bsp.Models.Length >= 1, "every IBSP has at least the worldspawn model");

        // Model 0 owns a contiguous brush range starting at 0 (the standard Q3 layout).
        Assert.Equal(0, bsp.Models[0].FirstBrush);
        Assert.True(bsp.Models[0].BrushCount <= bsp.Brushes.Length);

        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(bsp);
        Assert.Equal(bsp.Models.Length - 1, r.Submodels.Count);             // one submodel per inline model

        // The static world holds worldspawn brushes PLUS the tessellated slabs of worldspawn bezier patches
        // (patches carry no brushes in the BSP, so the builder tessellates them — see BspPatchCollisionTests),
        // and so it can exceed the worldspawn BRUSH count. The precise "no submodel brush leaks into the world"
        // guarantee is covered by the synthetic TwoBoxBsp tests above; here we just confirm the split produced a
        // non-empty world alongside the right number of submodels.
        Assert.NotEmpty(r.World.Brushes);
    }

    // ---- real data: the lightgrid lump (playtest r12 #41 — DP's per-position model lighting source) -----

    [Fact]
    public void Real_Bsp_LightGrid_Parses_And_Samples_NonBlack()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? bspPath = vfs.Find("maps/", "bsp").FirstOrDefault(p => p.Contains("stormkeep"));
        if (bspPath is null) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        Assert.NotNull(bsp.LightGrid); // stock q3map2-compiled maps always ship lump 15
        LightGridData grid = bsp.LightGrid!;
        Assert.True(grid.Nx > 1 && grid.Ny > 1 && grid.Nz > 1, $"degenerate grid {grid.Nx}x{grid.Ny}x{grid.Nz}");

        // A sample at the world model's center must return SOME light (0..255 scale) — a fully black grid
        // would mean the dims/indexing are off (every playable spot on stormkeep is lit).
        var mid = (bsp.Models[0].Mins + bsp.Models[0].Maxs) * 0.5f;
        grid.Sample(mid, out var ambient, out var directed, out _);
        Assert.True(ambient.X + ambient.Y + ambient.Z + directed.X + directed.Y + directed.Z > 0f,
            $"black sample at world center {mid} (ambient={ambient}, directed={directed})");
    }
}
