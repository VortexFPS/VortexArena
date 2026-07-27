using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers the two importers that feed the editable map format — the <c>.map</c> source parser
/// (<see cref="MapSourceReader"/>, phase E1) and the <c>.vmap</c> container round-trip
/// (<see cref="VmapPackage"/>, phase E0). Between them these are the only ways geometry enters the editor,
/// so a silent regression here corrupts every map that passes through.
/// </summary>
public class VmapImportTests
{
    /// <summary>A minimal but realistic classic-Q3 .map: worldspawn with one box brush, plus a point entity.</summary>
    private const string SimpleMap = """
        // entity 0
        {
        "classname" "worldspawn"
        "message" "Test Map"
        // brush 0
        {
        ( -64 -64 16 ) ( -64 -63 16 ) ( -63 -64 16 ) textures/test/floor 0 0 0 0.500000 0.500000 0 0 0
        ( -64 -64 0 ) ( -63 -64 0 ) ( -64 -63 0 ) textures/test/floor 0 0 0 0.500000 0.500000 0 0 0
        ( -64 -64 0 ) ( -64 -63 0 ) ( -64 -64 1 ) textures/test/wall 0 0 0 0.500000 0.500000 0 0 0
        ( 64 64 0 ) ( 64 63 0 ) ( 64 64 1 ) textures/test/wall 0 0 0 0.500000 0.500000 0 0 0
        ( -64 -64 0 ) ( -64 -64 1 ) ( -63 -64 0 ) textures/test/wall 0 0 0 0.500000 0.500000 0 0 0
        ( 64 64 0 ) ( 64 64 1 ) ( 63 64 0 ) textures/test/wall 0 0 0 0.500000 0.500000 0 0 0
        }
        }
        // entity 1
        {
        "classname" "info_player_deathmatch"
        "origin" "0 0 32"
        "angle" "90"
        }
        """;

    [Fact]
    public void MapSource_ParsesWorldspawnBrushAndPointEntity()
    {
        var warnings = new List<string>();
        VmapDocument doc = MapSourceReader.Read(SimpleMap, "testmap", warnings: warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, doc.Entities.Count);
        Assert.Single(doc.Brushes);

        VmapEntity world = doc.Worldspawn()!;
        Assert.Equal("Test Map", world.Fields["message"]);
        Assert.False(world.IsBrushEntity);   // unclaimed geometry belongs to worldspawn implicitly

        VmapEntity spawn = doc.Entities[1];
        Assert.Equal("info_player_deathmatch", spawn.ClassName);
        Assert.Equal(new Vector3(0, 0, 32), spawn.Origin());
    }

    [Fact]
    public void MapSource_BrushPlanesBoundTheAuthoredBox()
    {
        VmapDocument doc = MapSourceReader.Read(SimpleMap, "testmap");
        VmapBrush brush = doc.Brushes[0];

        Assert.Equal(6, brush.Faces.Count);
        Assert.True(VmapWinding.IsClosedConvex(brush));
        Assert.True(VmapWinding.TryGetBounds(brush, out Vector3 mins, out Vector3 maxs));

        // The authored brush spans x,y in [-64,64] and z in [0,16].
        Assert.Equal(new Vector3(-64, -64, 0), mins);
        Assert.Equal(new Vector3(64, 64, 16), maxs);
    }

    [Fact]
    public void MapSource_ClassicTexdefScaleMatchesRadiant()
    {
        // Radiant's default 0.5 scale means the texture repeats every texW/2 units. With the 64x64 fallback
        // size that is one repeat per 32 units, so two points 32 units apart differ by exactly 1 in U.
        VmapDocument doc = MapSourceReader.Read(SimpleMap, "testmap");
        VmapFace floor = doc.Brushes[0].Faces[0];

        Assert.True(floor.Projection.IsValid);
        Vector2 a = floor.Projection.Evaluate(new Vector3(0, 0, 16));
        Vector2 b = floor.Projection.Evaluate(new Vector3(32, 0, 16));
        Assert.Equal(1f, MathF.Abs(b.X - a.X), 3);
    }

    [Fact]
    public void MapSource_ValveTexdefUsesExplicitAxes()
    {
        const string valve = """
            {
            "classname" "worldspawn"
            {
            ( 0 0 64 ) ( 0 1 64 ) ( 1 0 64 ) textures/t/a [ 1 0 0 0 ] [ 0 -1 0 0 ] 0 1 1
            ( 0 0 0 ) ( 1 0 0 ) ( 0 1 0 ) textures/t/a [ 1 0 0 0 ] [ 0 -1 0 0 ] 0 1 1
            ( 0 0 0 ) ( 0 1 0 ) ( 0 0 1 ) textures/t/a [ 0 1 0 0 ] [ 0 0 -1 0 ] 0 1 1
            ( 64 64 0 ) ( 64 63 0 ) ( 64 64 1 ) textures/t/a [ 0 1 0 0 ] [ 0 0 -1 0 ] 0 1 1
            ( 0 0 0 ) ( 0 0 1 ) ( 1 0 0 ) textures/t/a [ 1 0 0 0 ] [ 0 0 -1 0 ] 0 1 1
            ( 64 64 0 ) ( 64 64 1 ) ( 63 64 0 ) textures/t/a [ 1 0 0 0 ] [ 0 0 -1 0 ] 0 1 1
            }
            }
            """;

        var warnings = new List<string>();
        VmapDocument doc = MapSourceReader.Read(valve, "valve", warnings: warnings);

        Assert.Single(doc.Brushes);
        Assert.Equal(6, doc.Brushes[0].Faces.Count);
        Assert.Empty(warnings);

        // Scale 1 with the 64x64 fallback: one repeat per 64 units along the explicit U axis.
        VmapTexProjection p = doc.Brushes[0].Faces[0].Projection;
        Assert.Equal(1f / 64f, p.AxisU.X, 5);
        Assert.Equal(-1f / 64f, p.AxisV.Y, 5);
    }

    [Fact]
    public void MapSource_FuncGroupIsDissolvedIntoWorldGeometry()
    {
        // q3map2 treats func_group as an editor-only grouping node. Keeping it as a brush entity would turn
        // level architecture into a separate solid model that never seals the world.
        const string grouped = """
            {
            "classname" "worldspawn"
            }
            {
            "classname" "func_group"
            {
            ( 0 0 16 ) ( 0 1 16 ) ( 1 0 16 ) t/a 0 0 0 1 1 0 0 0
            ( 0 0 0 ) ( 1 0 0 ) ( 0 1 0 ) t/a 0 0 0 1 1 0 0 0
            ( 0 0 0 ) ( 0 1 0 ) ( 0 0 1 ) t/a 0 0 0 1 1 0 0 0
            ( 32 32 0 ) ( 32 31 0 ) ( 32 32 1 ) t/a 0 0 0 1 1 0 0 0
            ( 0 0 0 ) ( 0 0 1 ) ( 1 0 0 ) t/a 0 0 0 1 1 0 0 0
            ( 32 32 0 ) ( 32 32 1 ) ( 31 32 0 ) t/a 0 0 0 1 1 0 0 0
            }
            }
            """;

        VmapDocument doc = MapSourceReader.Read(grouped, "grouped");

        Assert.Single(doc.Brushes);
        Assert.Single(doc.Entities);                            // only worldspawn survives
        Assert.DoesNotContain(doc.Entities, e => e.ClassName == "func_group");
        Assert.All(doc.Entities, e => Assert.False(e.IsBrushEntity));  // brush is world geometry now
    }

    [Fact]
    public void MapSource_BrushEntityOwnsItsBrushes()
    {
        const string door = """
            {
            "classname" "worldspawn"
            }
            {
            "classname" "func_door"
            "angle" "-1"
            {
            ( 0 0 16 ) ( 0 1 16 ) ( 1 0 16 ) t/a 0 0 0 1 1 0 0 0
            ( 0 0 0 ) ( 1 0 0 ) ( 0 1 0 ) t/a 0 0 0 1 1 0 0 0
            ( 0 0 0 ) ( 0 1 0 ) ( 0 0 1 ) t/a 0 0 0 1 1 0 0 0
            ( 32 32 0 ) ( 32 31 0 ) ( 32 32 1 ) t/a 0 0 0 1 1 0 0 0
            ( 0 0 0 ) ( 0 0 1 ) ( 1 0 0 ) t/a 0 0 0 1 1 0 0 0
            ( 32 32 0 ) ( 32 32 1 ) ( 31 32 0 ) t/a 0 0 0 1 1 0 0 0
            }
            }
            """;

        VmapDocument doc = MapSourceReader.Read(door, "door");
        VmapEntity ent = doc.Entities.Single(e => e.ClassName == "func_door");

        Assert.True(ent.IsBrushEntity);
        Assert.Single(ent.BrushIds);
        Assert.NotNull(doc.FindBrush(ent.BrushIds[0]));
    }

    [Fact]
    public void MapSource_ParsesPatchDef2Grid()
    {
        const string patchMap = """
            {
            "classname" "worldspawn"
            {
            patchDef2
            {
            textures/test/curve
            ( 3 3 0 0 0 )
            (
            ( ( 0 0 0 0 0 ) ( 0 32 0 0 0.5 ) ( 0 64 0 0 1 ) )
            ( ( 32 0 16 0.5 0 ) ( 32 32 16 0.5 0.5 ) ( 32 64 16 0.5 1 ) )
            ( ( 64 0 0 1 0 ) ( 64 32 0 1 0.5 ) ( 64 64 0 1 1 ) )
            )
            }
            }
            }
            """;

        var warnings = new List<string>();
        VmapDocument doc = MapSourceReader.Read(patchMap, "patch", warnings: warnings);

        Assert.Empty(warnings);
        Assert.Single(doc.Patches);
        VmapPatch p = doc.Patches[0];
        Assert.Equal(3, p.Width);
        Assert.Equal(3, p.Height);
        Assert.True(p.IsValid);
        Assert.Equal("textures/test/curve", p.Material);

        // The patch tessellates into drawable geometry (an arch, not an empty surface).
        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc);
        Assert.Single(surfaces);
        Assert.True(surfaces[0].TriangleCount > 0);
    }

    [Fact]
    public void MapSource_DetailBrushIsFlagged()
    {
        // The detail content bit (0x8000000) is the strongest ornament-detection signal for later phases,
        // so it has to survive import rather than being collapsed into a generic solid.
        const string detail = """
            {
            "classname" "worldspawn"
            {
            ( 0 0 16 ) ( 0 1 16 ) ( 1 0 16 ) t/a 0 0 0 1 1 134217728 0 0
            ( 0 0 0 ) ( 1 0 0 ) ( 0 1 0 ) t/a 0 0 0 1 1 134217728 0 0
            ( 0 0 0 ) ( 0 1 0 ) ( 0 0 1 ) t/a 0 0 0 1 1 134217728 0 0
            ( 32 32 0 ) ( 32 31 0 ) ( 32 32 1 ) t/a 0 0 0 1 1 134217728 0 0
            ( 0 0 0 ) ( 0 0 1 ) ( 1 0 0 ) t/a 0 0 0 1 1 134217728 0 0
            ( 32 32 0 ) ( 32 32 1 ) ( 31 32 0 ) t/a 0 0 0 1 1 134217728 0 0
            }
            }
            """;

        VmapDocument doc = MapSourceReader.Read(detail, "detail");
        Assert.True(doc.Brushes[0].IsDetail);
    }

    [Fact]
    public void Package_RoundTripsThroughDirectoryAndZip()
    {
        VmapDocument original = MapSourceReader.Read(SimpleMap, "testmap", sourcePath: "maps/testmap.map");
        string root = Path.Combine(Path.GetTempPath(), "vmap-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            string dir = Path.Combine(root, "testmap.vmap");
            VmapPackage.WriteToDirectory(original, dir);
            AssertSameDocument(original, VmapPackage.Read(dir));

            string zip = Path.Combine(root, "packed.vmap");
            VmapPackage.WriteToZip(original, zip);
            AssertSameDocument(original, VmapPackage.Read(zip));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Package_WritesByteIdenticalOutputForUnchangedData()
    {
        // Deterministic serialization is what lets .vmap files diff and merge in git (design doc §11.8).
        VmapDocument doc = MapSourceReader.Read(SimpleMap, "testmap");
        string root = Path.Combine(Path.GetTempPath(), "vmap-det-" + Guid.NewGuid().ToString("N"));

        try
        {
            string a = Path.Combine(root, "a.vmap");
            string b = Path.Combine(root, "b.vmap");
            VmapPackage.WriteToDirectory(doc, a);
            VmapPackage.WriteToDirectory(VmapPackage.Read(a), b);

            foreach (string section in new[] { "map.json", "geometry.json", "entities.json" })
                Assert.Equal(File.ReadAllText(Path.Combine(a, section)), File.ReadAllText(Path.Combine(b, section)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Package_RejectsAFutureFormatVersion()
    {
        // A newer editor's package must fail loudly rather than silently importing partial geometry.
        string root = Path.Combine(Path.GetTempPath(), "vmap-ver-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "map.json"),
                $$"""{"formatVersion": {{VmapDocument.CurrentFormatVersion + 1}}, "name": "future"}""");

            var ex = Assert.Throws<XonoticGodot.Formats.AssetParseException>(() => VmapPackage.Read(root));
            Assert.Contains("newer than this build", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertSameDocument(VmapDocument expected, VmapDocument actual)
    {
        Assert.Equal(expected.Manifest.Name, actual.Manifest.Name);
        Assert.Equal(expected.Manifest.SourcePath, actual.Manifest.SourcePath);
        Assert.Equal(expected.Brushes.Count, actual.Brushes.Count);
        Assert.Equal(expected.Entities.Count, actual.Entities.Count);
        Assert.Equal(expected.Patches.Count, actual.Patches.Count);

        for (int i = 0; i < expected.Brushes.Count; i++)
        {
            VmapBrush e = expected.Brushes[i], a = actual.Brushes[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.IsDetail, a.IsDetail);
            Assert.Equal(e.Faces.Count, a.Faces.Count);
            for (int f = 0; f < e.Faces.Count; f++)
            {
                Assert.Equal(e.Faces[f].Material, a.Faces[f].Material);
                Assert.Equal(e.Faces[f].Plane.Normal, a.Faces[f].Plane.Normal);
                Assert.Equal(e.Faces[f].Plane.Dist, a.Faces[f].Plane.Dist, 4);
                Assert.Equal(e.Faces[f].Projection.AxisU, a.Faces[f].Projection.AxisU);
                Assert.Equal(e.Faces[f].Projection.OffsetV, a.Faces[f].Projection.OffsetV, 4);
            }
        }

        for (int i = 0; i < expected.Entities.Count; i++)
        {
            Assert.Equal(expected.Entities[i].ClassName, actual.Entities[i].ClassName);
            Assert.Equal(expected.Entities[i].BrushIds, actual.Entities[i].BrushIds);
        }
    }
}
