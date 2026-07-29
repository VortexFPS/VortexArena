using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Face layer stacks (design doc §11.x): a face is a stack of textured layers rather than a single material.
///
/// The point of the format is to stop being bounded by what a BSP drawvert can carry — one shader per face,
/// one RGBA, two UV sets. What the tests hold down is that adding the stack did not cost anything that
/// already worked: a plain face still behaves exactly as it did, still writes the same bytes, and still
/// survives every path a face travels (clone, undo, package, wire).
/// </summary>
public class VmapFaceLayerTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id = 1)
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = "textures/test/wall",
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

    private static VmapFaceLayer Rust(VmapBlend blend = VmapBlend.Vertex, int channel = 0) => new()
    {
        Material = "textures/exx/rust overlay",     // a space, so escaping is exercised too
        Projection = new VmapTexProjection(
            new Vector3(0.03125f, 0f, 0f), new Vector3(0f, -0.03125f, 0f), 1.5f, -2.25f),
        Blend = blend,
        WeightChannel = channel,
    };

    // ---------------------------------------------------------------- the compatibility contract

    [Fact]
    public void APlainFace_HasExactlyOneLayerAndReadsThroughTheOldProperties()
    {
        var face = new VmapFace
        {
            Plane = new VmapPlane(new Vector3(0, 0, 1), 64f),
            Material = "textures/exx/floor01",
            Projection = VmapTexProjection.AxialFor(new Vector3(0, 0, 1)),
        };

        Assert.Single(face.Layers);
        Assert.False(face.IsLayered);
        Assert.Equal("textures/exx/floor01", face.Layers[0].Material);

        // The old properties are the base layer, both ways round.
        face.Layers[0].Material = "textures/exx/floor02";
        Assert.Equal("textures/exx/floor02", face.Material);
    }

    [Fact]
    public void CloningAFace_CopiesTheStackRatherThanSharingIt()
    {
        var face = new VmapFace { Material = "base" };
        face.Layers.Add(Rust());

        VmapFace copy = face.Clone();
        copy.Layers[1].Material = "changed";

        Assert.Equal("textures/exx/rust overlay", face.Layers[1].Material);
        Assert.Equal(2, copy.Layers.Count);
    }

    [Fact]
    public void CloningABrush_KeepsEveryFaceStack()
    {
        // VmapBrush.Clone used to copy Material/Projection by hand. Doing that now would silently flatten a
        // layered face to its base on every undo snapshot — the face would still look valid, just wrong.
        VmapBrush brush = Box(Vector3.Zero, new Vector3(64, 64, 64));
        brush.Faces[0].Layers.Add(Rust());

        VmapBrush copy = brush.Clone();
        Assert.Equal(2, copy.Faces[0].Layers.Count);
        Assert.Equal(VmapBlend.Vertex, copy.Faces[0].Layers[1].Blend);
        Assert.Equal(0, copy.Faces[0].Layers[1].WeightChannel);
    }

    [Fact]
    public void UndoRestoresAStackTheOpReplaced()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64)));
        var session = new VmapEditSession(doc);

        var stack = new List<VmapFaceLayer> { doc.Brushes[0].Faces[0].Base.Clone(), Rust() };
        Assert.True(session.Apply(new SetFaceLayersOp(1, 0, stack)));
        Assert.Equal(2, doc.Brushes[0].Faces[0].Layers.Count);

        Assert.True(session.Undo());
        Assert.Single(doc.Brushes[0].Faces[0].Layers);
        Assert.Equal("textures/test/wall", doc.Brushes[0].Faces[0].Material);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void ALayeredFace_SurvivesAPackageRoundTrip()
    {
        var doc = new VmapDocument { Manifest = { Name = "layers" } };
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64)));
        doc.Brushes[0].Faces[0].Layers.Add(Rust(VmapBlend.Add, channel: 2));

        string dir = Path.Combine(Path.GetTempPath(), "vmap-layers-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "layers.vmap");
            VmapPackage.Write(doc, path);
            VmapDocument back = VmapPackage.Read(path);

            VmapFace face = back.Brushes[0].Faces[0];
            Assert.Equal(2, face.Layers.Count);
            Assert.Equal("textures/exx/rust overlay", face.Layers[1].Material);
            Assert.Equal(VmapBlend.Add, face.Layers[1].Blend);
            Assert.Equal(2, face.Layers[1].WeightChannel);
            Assert.Equal(1.5f, face.Layers[1].Projection.OffsetU, 4);

            // And the faces that were never layered stayed single.
            Assert.All(back.Brushes[0].Faces.Skip(1), f => Assert.Single(f.Layers));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void APlainFace_CostsNothingExtraInTheFile()
    {
        // A face with one layer writes one line. The stack is an addition to the format, not a tax on every
        // map that does not use it.
        var doc = new VmapDocument { Manifest = { Name = "plain" } };
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64)));

        string text = VmapText.Write(doc);
        Assert.DoesNotContain("\nl ", text, StringComparison.Ordinal);

        doc.Brushes[0].Faces[0].Layers.Add(Rust(VmapBlend.Add, channel: 2));
        Assert.Contains("\nl ", VmapText.Write(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void APackageWrittenBeforeLayers_ReadsAsSingleLayerFaces()
    {
        // The forward half of the same contract: no extraLayers key at all is a one-layer face, no special case.
        const string manifest = """{"formatVersion":1,"name":"old"}""";
        const string geometry = """
        {"brushes":[{"id":1,"detail":false,"contents":1,"faces":[
          {"normal":[0,0,1],"dist":64,"material":"textures/old/floor",
           "axisU":[0.015625,0,0],"axisV":[0,-0.015625,0],"offsetU":0,"offsetV":0,
           "surface":0,"contents":1}]}],"patches":[]}
        """;

        string dir = Path.Combine(Path.GetTempPath(), "vmap-old-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, VmapPackage.ManifestSection), manifest);
            File.WriteAllText(Path.Combine(dir, VmapPackage.GeometrySection), geometry);

            VmapDocument back = VmapPackage.ReadFromDirectory(dir);
            VmapFace face = back.Brushes[0].Faces[0];
            Assert.Single(face.Layers);
            Assert.Equal("textures/old/floor", face.Material);
            Assert.False(face.IsLayered);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- replication

    [Fact]
    public void SetFaceLayersOp_RoundTripsThroughTheWire()
    {
        var op = new SetFaceLayersOp(3, 2, new List<VmapFaceLayer>
        {
            new() { Material = "textures/exx/base", Projection = VmapTexProjection.AxialFor(Vector3.UnitZ) },
            Rust(VmapBlend.Multiply, channel: 3),
        });

        string line = VmapOpWire.Serialize(op)!;
        var decoded = (SetFaceLayersOp)VmapOpWire.Deserialize(line)!;

        Assert.Equal(line, VmapOpWire.Serialize(decoded));
        Assert.Equal(2, decoded.Layers.Count);
        Assert.Equal(VmapBlend.Multiply, decoded.Layers[1].Blend);
        Assert.Equal(3, decoded.Layers[1].WeightChannel);
        Assert.Equal("textures/exx/rust overlay", decoded.Layers[1].Material);
    }

    [Fact]
    public void ALayeredBrush_ReplicatesItsStackRatherThanArrivingFlattened()
    {
        // A receiver that decoded only the base would render a plausible-looking wall with the blend missing,
        // which is exactly the kind of divergence nobody notices until the map ships.
        var doc = new VmapDocument();
        VmapBrush brush = Box(Vector3.Zero, new Vector3(64, 64, 64), id: 7);
        brush.Faces[0].Layers.Add(Rust());
        brush.Faces[2].Layers.Add(Rust(VmapBlend.Alpha, channel: -1));
        doc.Brushes.Add(brush);

        var add = AddObjectsOp.Capture(doc, new[] { 7 }, Array.Empty<int>(), Array.Empty<int>());
        var decoded = (AddObjectsOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(add)!)!;

        Assert.Equal(VmapOpWire.Serialize(add), VmapOpWire.Serialize(decoded));
        Assert.Equal(2, decoded.Brushes[0].Faces[0].Layers.Count);
        Assert.Equal(2, decoded.Brushes[0].Faces[2].Layers.Count);
        Assert.Single(decoded.Brushes[0].Faces[1].Layers);
        Assert.Equal(VmapBlend.Alpha, decoded.Brushes[0].Faces[2].Layers[1].Blend);
        Assert.Equal(-1, decoded.Brushes[0].Faces[2].Layers[1].WeightChannel);
    }

    [Fact]
    public void AHugeLayerCount_IsRejectedRatherThanAllocated()
    {
        // Same class as every other length on the wire: the count must not be trusted past what the line holds.
        Assert.Null(VmapOpWire.Deserialize("layers 1 0 2147483647"));
        Assert.Null(VmapOpWire.Deserialize("layers 1 0 0"));            // a face always has a base layer
        Assert.Null(VmapOpWire.Deserialize("add 1 1 1 0 0 1 64 1 0 0 0 -1 0 0 0 0 0 t 2147483647"));
    }

    // ---------------------------------------------------------------- batching

    [Fact]
    public void FacesBatchByTheirWholeStack_NotJustTheBaseMaterial()
    {
        // Two faces sharing a base but differing above it need different GPU materials, so they cannot share a
        // mesh surface. Keying on the base alone would merge them and the first one seen would decide how both
        // looked.
        var doc = new VmapDocument();
        VmapBrush plain = Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1);
        VmapBrush layered = Box(new Vector3(128, 0, 0), new Vector3(192, 64, 64), id: 2);
        layered.Faces[0].Layers.Add(Rust());
        doc.Brushes.Add(plain);
        doc.Brushes.Add(layered);

        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc, new VmapSurfaceOptions());

        Assert.Contains(surfaces, s => s.ExtraLayers.Count == 1);
        Assert.Contains(surfaces, s => s.Material == "textures/test/wall" && s.ExtraLayers.Count == 0);
    }
}
