using System.Numerics;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Formats.Vmap;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// The <c>.vmap</c> text format: what a map survives, what it refuses, and how big it is.
///
/// A map format has one obligation above all others — give back exactly what it was handed — and the way it
/// fails is by dropping a field nobody thought to check. So the round trip here is written against a document
/// that uses EVERY feature at once (layers, groups, patches, brush entities, paint, awkward strings), and the
/// real-data case at the bottom does the same against the shipped maps, where the inputs are not ones anybody
/// chose.
/// </summary>
public class VmapTextFormatTests
{
    private static readonly string DataDir = TestPaths.Data;

    private readonly ITestOutputHelper _out;
    public VmapTextFormatTests(ITestOutputHelper output) => _out = output;

    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id, string material = "textures/test/wall")
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = material,
            Projection = VmapTexProjection.AxialFor(n),
        });
        Face(new Vector3(1, 0, 0), maxs.X);
        Face(new Vector3(-1, 0, 0), -mins.X);
        Face(new Vector3(0, 1, 0), maxs.Y);
        Face(new Vector3(0, -1, 0), -mins.Y);
        Face(new Vector3(0, 0, 1), maxs.Z);
        Face(new Vector3(0, 0, -1), -mins.Z);
        return b;
    }

    /// <summary>A document that uses every feature the format carries, so nothing can be quietly dropped.</summary>
    private static VmapDocument Everything()
    {
        var doc = new VmapDocument
        {
            Manifest =
            {
                Name = "kitchen sink",
                Title = "The \"Kitchen\" Sink",
                SourceKind = "map",
                SourcePath = "maps/my map/sink.map",
                SourceHash = "deadbeefcafef00d",
            },
        };

        VmapBrush a = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        a.IsDetail = true;
        a.SubmodelIndex = 3;
        a.GroupId = 1;
        a.Faces[0].SurfaceFlags = 0x80;
        a.Faces[0].ContentFlags = 0x2000;
        a.Faces[0].Layers.Add(new VmapFaceLayer
        {
            Material = "textures/exx/rust overlay",
            Blend = VmapBlend.Vertex,
            WeightChannel = 2,
            Projection = new VmapTexProjection(
                new Vector3(0.0078125f, 0f, 0f), new Vector3(0f, -0.0078125f, 0f), 1.5f, -2.25f),
        });
        doc.Brushes.Add(a);

        VmapBrush b = Box(new Vector3(128, 0, 0), new Vector3(192, 64, 64), 2, "textures/common/caulk");
        b.IsToolBrush = true;
        doc.Brushes.Add(b);

        var patch = new VmapPatch
        {
            Id = 1, Width = 3, Height = 3, Material = "textures/test/curve",
            SurfaceFlags = 4, ContentFlags = 1, GroupId = 1,
        };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                patch.Controls.Add(new Vector3((col - 1) * 48.5f, (row - 1) * 48.5f, 12.25f));
                patch.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        doc.Patches.Add(patch);

        var world = new VmapEntity { Id = 1, ClassName = "worldspawn" };
        world.Fields["classname"] = "worldspawn";
        world.Fields["message"] = "a map with spaces in its \"name\"";
        doc.Entities.Add(world);

        var door = new VmapEntity { Id = 2, ClassName = "func_door", GroupId = 1 };
        door.Fields["classname"] = "func_door";
        door.Fields["speed"] = "100";
        door.BrushIds.Add(1);
        door.PatchIds.Add(1);
        doc.Entities.Add(door);

        var spawn = new VmapEntity { Id = 3, ClassName = "info_player_deathmatch" };
        spawn.Fields["classname"] = "info_player_deathmatch";
        spawn.SetOrigin(new Vector3(16.5f, -32.25f, 128f));
        doc.Entities.Add(spawn);

        doc.Groups.Add(new VmapGroup { Id = 1, Name = "north wing", Hidden = true });

        var make = new CreateBlendMapOp(1, 4, unitsPerTexel: 8f);
        Assert.True(make.Apply(doc));
        Assert.True(new PaintBlendOp(make.BlendMapId, 2, VmapPaintMode.Add,
            new[] { new Vector2(0.4f, 0.45f), new Vector2(0.6f, 0.55f) }, 0.2f, 0.8f, 0.3f, doc).Apply(doc));

        return doc;
    }

    private static void AssertSame(VmapDocument a, VmapDocument b)
    {
        Assert.Equal(a.Manifest.Name, b.Manifest.Name);
        Assert.Equal(a.Manifest.Title, b.Manifest.Title);
        Assert.Equal(a.Manifest.SourceKind, b.Manifest.SourceKind);
        Assert.Equal(a.Manifest.SourcePath, b.Manifest.SourcePath);
        Assert.Equal(a.Manifest.SourceHash, b.Manifest.SourceHash);

        Assert.Equal(a.Brushes.Count, b.Brushes.Count);
        for (int i = 0; i < a.Brushes.Count; i++)
        {
            VmapBrush x = a.Brushes[i], y = b.Brushes[i];
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.IsDetail, y.IsDetail);
            Assert.Equal(x.ContentFlags, y.ContentFlags);
            Assert.Equal(x.SubmodelIndex, y.SubmodelIndex);
            Assert.Equal(x.IsToolBrush, y.IsToolBrush);
            Assert.Equal(x.GroupId, y.GroupId);
            Assert.Equal(x.Faces.Count, y.Faces.Count);

            for (int f = 0; f < x.Faces.Count; f++)
            {
                VmapFace p = x.Faces[f], q = y.Faces[f];
                Assert.Equal(p.Plane.Normal, q.Plane.Normal);
                Assert.Equal(p.Plane.Dist, q.Plane.Dist);
                Assert.Equal(p.SurfaceFlags, q.SurfaceFlags);
                Assert.Equal(p.ContentFlags, q.ContentFlags);
                Assert.Equal(p.BlendMapId, q.BlendMapId);
                Assert.Equal(p.Layers.Count, q.Layers.Count);

                for (int l = 0; l < p.Layers.Count; l++)
                {
                    Assert.Equal(p.Layers[l].Material, q.Layers[l].Material);
                    Assert.Equal(p.Layers[l].Blend, q.Layers[l].Blend);
                    Assert.Equal(p.Layers[l].WeightChannel, q.Layers[l].WeightChannel);
                    Assert.Equal(p.Layers[l].Projection.AxisU, q.Layers[l].Projection.AxisU);
                    Assert.Equal(p.Layers[l].Projection.AxisV, q.Layers[l].Projection.AxisV);
                    Assert.Equal(p.Layers[l].Projection.OffsetU, q.Layers[l].Projection.OffsetU);
                    Assert.Equal(p.Layers[l].Projection.OffsetV, q.Layers[l].Projection.OffsetV);
                }
            }
        }

        Assert.Equal(a.Patches.Count, b.Patches.Count);
        for (int i = 0; i < a.Patches.Count; i++)
        {
            VmapPatch x = a.Patches[i], y = b.Patches[i];
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.Material, y.Material);
            Assert.Equal(x.Width, y.Width);
            Assert.Equal(x.Height, y.Height);
            Assert.Equal(x.SurfaceFlags, y.SurfaceFlags);
            Assert.Equal(x.ContentFlags, y.ContentFlags);
            Assert.Equal(x.GroupId, y.GroupId);
            Assert.Equal(x.Controls, y.Controls);
            Assert.Equal(x.ControlUvs, y.ControlUvs);
        }

        Assert.Equal(a.Entities.Count, b.Entities.Count);
        for (int i = 0; i < a.Entities.Count; i++)
        {
            VmapEntity x = a.Entities[i], y = b.Entities[i];
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.ClassName, y.ClassName);
            Assert.Equal(x.GroupId, y.GroupId);
            Assert.Equal(x.BrushIds, y.BrushIds);
            Assert.Equal(x.PatchIds, y.PatchIds);
            Assert.Equal(x.Fields.Count, y.Fields.Count);
            foreach (KeyValuePair<string, string> kv in x.Fields)
                Assert.Equal(kv.Value, y.Fields[kv.Key]);
        }

        Assert.Equal(a.Groups.Count, b.Groups.Count);
        for (int i = 0; i < a.Groups.Count; i++)
        {
            Assert.Equal(a.Groups[i].Id, b.Groups[i].Id);
            Assert.Equal(a.Groups[i].Name, b.Groups[i].Name);
            Assert.Equal(a.Groups[i].Hidden, b.Groups[i].Hidden);
        }

        Assert.Equal(a.BlendMaps.Count, b.BlendMaps.Count);
        for (int i = 0; i < a.BlendMaps.Count; i++)
        {
            VmapBlendMap x = a.BlendMaps[i], y = b.BlendMaps[i];
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.Width, y.Width);
            Assert.Equal(x.Height, y.Height);
            Assert.Equal(x.UnitsPerTexel, y.UnitsPerTexel);
            Assert.Equal(x.Projection.AxisU, y.Projection.AxisU);
            Assert.Equal(x.Texels, y.Texels);
        }
    }

    // ---------------------------------------------------------------- the round trip

    [Fact]
    public void EveryFeatureSurvivesTheRoundTrip()
    {
        VmapDocument doc = Everything();
        AssertSame(doc, VmapText.Read(VmapText.Write(doc)));
    }

    /// <summary>
    /// Deterministic bytes are what let a <c>.vmap</c> diff and merge. Writing, reading and writing again has
    /// to give the identical file, or every save shows spurious changes.
    /// </summary>
    [Fact]
    public void WritingIsDeterministic()
    {
        VmapDocument doc = Everything();
        string once = VmapText.Write(doc);
        string twice = VmapText.Write(VmapText.Read(once));

        Assert.Equal(once, twice);
        Assert.Equal(once, VmapText.Write(doc));
    }

    /// <summary>Floats are written round-trip, or a plane drifts a little every time the map is saved.</summary>
    [Fact]
    public void FloatsAreLossless()
    {
        var doc = new VmapDocument();
        VmapBrush b = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        b.Faces[0].Plane = new VmapPlane(
            Vector3.Normalize(new Vector3(0.3333333f, 0.7777777f, -0.1234567f)), 478.76584f);
        b.Faces[0].Projection = new VmapTexProjection(
            new Vector3(1e-7f, 0.015624999f, 0f), new Vector3(0f, -1234.5678f, 0f), 0.1f, -0.0000001f);
        doc.Brushes.Add(b);

        VmapDocument back = VmapText.Read(VmapText.Write(doc));
        Assert.Equal(b.Faces[0].Plane.Normal, back.Brushes[0].Faces[0].Plane.Normal);
        Assert.Equal(b.Faces[0].Plane.Dist, back.Brushes[0].Faces[0].Plane.Dist);
        Assert.Equal(b.Faces[0].Projection.AxisU, back.Brushes[0].Faces[0].Projection.AxisU);
        Assert.Equal(b.Faces[0].Projection.AxisV, back.Brushes[0].Faces[0].Projection.AxisV);
        Assert.Equal(b.Faces[0].Projection.OffsetV, back.Brushes[0].Faces[0].Projection.OffsetV);
    }

    /// <summary>
    /// Materials and spawn values are user text. A space would split into two tokens and shift every field
    /// after it; a quote would end the string early.
    /// </summary>
    [Theory]
    [InlineData("textures/my map/wall 01")]
    [InlineData("a \"quoted\" name")]
    [InlineData("back\\slash")]
    [InlineData("")]
    public void AwkwardStringsSurvive(string value)
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), 1, value));
        var e = new VmapEntity { Id = 1, ClassName = "target_speaker" };
        e.Fields["classname"] = "target_speaker";
        e.Fields["message"] = value;
        doc.Entities.Add(e);

        VmapDocument back = VmapText.Read(VmapText.Write(doc));
        Assert.Equal(value, back.Brushes[0].Faces[0].Material);
        Assert.Equal(value, back.Entities[0].Fields["message"]);
    }

    [Fact]
    public void AnEmptyDocumentRoundTrips()
        => AssertSame(new VmapDocument(), VmapText.Read(VmapText.Write(new VmapDocument())));

    // ---------------------------------------------------------------- refusals

    /// <summary>
    /// A map that will not load has to say WHERE. Without a line number a mapper is reading a two-megabyte
    /// file by eye.
    /// </summary>
    [Theory]
    [InlineData("nothing here at all", "expected")]
    [InlineData("// vmap 999\n", "newer than this build")]
    [InlineData("// vmap 3\nwobble 1 2 3\n", "unknown record")]
    [InlineData("// vmap 3\nb 1 0 1 0 0\n", "needs")]
    [InlineData("// vmap 3\nf 0 0 1 64 0 0 0 0 0 0 0 0 0 0 0 0\n", "outside a brush")]
    [InlineData("// vmap 3\nl 0 0 0 0 0 0 0 0 0 0 0\n", "outside a face")]
    [InlineData("// vmap 3\nc 0 0 0 0 0\n", "outside a patch")]
    [InlineData("// vmap 3\nk \"a\" \"b\"\n", "outside an entity")]
    [InlineData("// vmap 3\nd AAAA\n", "outside a blend map")]
    [InlineData("// vmap 3\nmat 1 \"late\"\n", "out of order")]
    [InlineData("// vmap 3\nb 1 0 1 0 0 0\nf 0 0 1 64 9 0 0 0 0 0 0 0 0 0 0 0\n", "not in the table")]
    [InlineData("// vmap 3\nmap \"name\" \"unterminated\n", "unterminated")]
    [InlineData("// vmap 3\nb x 0 1 0 0 0\n", "not a whole number")]
    public void MalformedInputIsRefusedWithAReason(string text, string expected)
    {
        var ex = Assert.Throws<AssetParseException>(() => VmapText.Read(text));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AParseErrorNamesTheLine()
    {
        var ex = Assert.Throws<AssetParseException>(
            () => VmapText.Read("// vmap 3\n\n\nb 1 0 1 0 0 0\nwobble\n"));
        Assert.Contains("line 5", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unknown manifest key is ignored: losing a field a newer build wrote beats refusing the map.</summary>
    [Fact]
    public void AnUnknownManifestKeyIsIgnored()
    {
        VmapDocument doc = VmapText.Read("// vmap 3\nmap \"name\" \"x\"\nmap \"whatIsThis\" \"y\"\n");
        Assert.Equal("x", doc.Manifest.Name);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        VmapDocument doc = VmapText.Read(
            "// vmap 3\n\n// a comment\nmap \"name\" \"x\"\n\n   \n// another\n");
        Assert.Equal("x", doc.Manifest.Name);
    }

    // ---------------------------------------------------------------- on disk

    [Fact]
    public void AVmapIsAFileNotADirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "vmaptext_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "sink.vmap");
            VmapPackage.Write(Everything(), path);

            Assert.True(File.Exists(path));
            Assert.False(Directory.Exists(path));
            Assert.StartsWith(VmapText.Magic, File.ReadAllText(path), StringComparison.Ordinal);
            AssertSame(Everything(), VmapPackage.Read(path));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The two layouts that came before are still read. A mapper with saves on disk should not lose them to a
    /// format change, and the reader tells all three apart by CONTENT — they share an extension.
    /// </summary>
    [Fact]
    public void ALegacyJsonDirectoryStillLoads()
    {
        string root = Path.Combine(Path.GetTempPath(), "vmaplegacy_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, VmapPackage.ManifestSection),
                """{"formatVersion":1,"name":"old"}""");
            File.WriteAllText(Path.Combine(root, VmapPackage.GeometrySection),
                """
                {"brushes":[{"id":7,"detail":false,"contents":1,"faces":[
                  {"normal":[0,0,1],"dist":64,"material":"textures/old/floor",
                   "axisU":[0.0156,0,0],"axisV":[0,-0.0156,0],"offsetU":0,"offsetV":0,
                   "surface":0,"contents":1}]}]}
                """);

            VmapDocument doc = VmapPackage.Read(root);
            Assert.Equal("old", doc.Manifest.Name);
            Assert.Equal(7, Assert.Single(doc.Brushes).Id);
            Assert.Equal("textures/old/floor", doc.Brushes[0].Faces[0].Material);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- real maps

    /// <summary>
    /// The shipped maps through the format, which is the only place the inputs are not ones I chose — and the
    /// only place the SIZE claim can be checked. The JSON form this replaced was 22 MB for stormkeep and
    /// 476 MB for catharsis, the latter past what GitHub accepts in one file at all.
    /// </summary>
    [Fact]
    public void RealMapsRoundTripAndStaySmall()
    {
        if (!Directory.Exists(DataDir))
        {
            _out.WriteLine($"content dir '{DataDir}' missing — skipped");
            return;
        }

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountGameDir(DataDir));

        int checkedMaps = 0;
        foreach (string map in new[] { "stormkeep", "fuse", "afterslime" })
        {
            if (!vfs.Exists($"maps/{map}.bsp"))
                continue;

            VmapDocument doc = BspToVmap.Import(
                BspReader.Read(vfs.ReadBytes($"maps/{map}.bsp")), map, $"maps/{map}.bsp", "");

            string text = VmapText.Write(doc);
            AssertSame(doc, VmapText.Read(text));
            Assert.Equal(text, VmapText.Write(VmapText.Read(text)));

            int faces = 0;
            foreach (VmapBrush b in doc.Brushes)
                faces += b.Faces.Count;

            double mb = System.Text.Encoding.UTF8.GetByteCount(text) / 1024.0 / 1024.0;
            double perFace = System.Text.Encoding.UTF8.GetByteCount(text) / (double)Math.Max(1, faces);
            _out.WriteLine($"{map,-11} {doc.Brushes.Count,6} brushes  {faces,7} faces  "
                + $"{mb,6:0.00} MB  {perFace,5:0} B/face");

            // The JSON form was 454 bytes a face. Anything near that means the compaction regressed.
            Assert.True(perFace < 120, $"{map}: {perFace:0} bytes per face is not compact");
            checkedMaps++;
        }

        Assert.True(checkedMaps > 0, "no stock maps found to check");
    }
}
