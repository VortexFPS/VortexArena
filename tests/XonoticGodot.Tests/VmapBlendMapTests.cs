using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers painted layer weights: the format, the packer, the rasterizer and the ops (backlog F2, F3).
///
/// The two properties everything else rests on are here. A stroke has to be DETERMINISTIC, because what
/// replicates is the stroke rather than its pixels — a peer replays it and must land on the same bytes. And
/// the rectangle an op declares has to be a SUPERSET of what it writes, because that rectangle is exactly
/// what undo restores; too small and half a stroke survives a Ctrl+Z with no step describing it.
/// </summary>
public class VmapBlendMapTests
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

    private static VmapBlendMap Map(int id, int w, int h)
        => new()
        {
            Id = id,
            Width = w,
            Height = h,
            UnitsPerTexel = 4f,
            Projection = VmapTexProjection.AxialFor(new Vector3(0, 0, 1), 1f),
            Texels = new byte[w * h * 4],
        };

    // ---------------------------------------------------------------- the type

    [Fact]
    public void CloneDeepCopiesTheTexels()
    {
        VmapBlendMap a = Map(1, 8, 8);
        a.Texels[0] = 200;

        VmapBlendMap b = a.Clone();
        b.Texels[0] = 7;

        Assert.Equal(200, a.Texels[0]);
        Assert.Equal(7, b.Texels[0]);
    }

    /// <summary>The same omission class the brush Clone comment warns about — and just as invisible.</summary>
    [Fact]
    public void FaceCloneCarriesTheBlendMapId()
    {
        var face = new VmapFace { BlendMapId = 42 };
        Assert.Equal(42, face.Clone().BlendMapId);
    }

    [Fact]
    public void CopyAndPasteRegionRoundTrip()
    {
        VmapBlendMap m = Map(1, 16, 16);
        for (int i = 0; i < m.Texels.Length; i++)
            m.Texels[i] = (byte)(i % 251);

        byte[] block = m.CopyRegion(4, 4, 6, 6);
        Array.Clear(m.Texels);
        Assert.True(m.PasteRegion(4, 4, 6, 6, block));

        Assert.Equal((byte)(((4 * 16 + 4) * 4) % 251), m.Texels[(4 * 16 + 4) * 4]);
        Assert.Equal(0, m.Texels[0]);          // outside the region: still cleared
    }

    [Fact]
    public void RegionsAreClampedToTheMap()
    {
        VmapBlendMap m = Map(1, 8, 8);
        Assert.Empty(m.CopyRegion(-100, -100, 4, 4));
        Assert.True(m.PasteRegion(6, 6, 100, 100, new byte[100 * 100 * 4]));
    }

    // ---------------------------------------------------------------- the rasterizer

    [Fact]
    public void SettingAtFullStrengthFillsOnlyTheTargetChannel()
    {
        VmapBlendMap m = Map(1, 16, 16);

        Assert.True(VmapBlendPaint.Stamp(
            m, new Vector2(0.5f, 0.5f), 4f, 1f, 1f, channel: 1, VmapPaintMode.Set,
            out _, out _, out _, out _));

        for (int i = 0; i < 16 * 16; i++)
        {
            Assert.Equal(0, m.Texels[i * 4 + 0]);
            Assert.Equal(255, m.Texels[i * 4 + 1]);
            Assert.Equal(0, m.Texels[i * 4 + 2]);
            Assert.Equal(0, m.Texels[i * 4 + 3]);
        }
    }

    [Fact]
    public void AStampEntirelyOffTheMapDoesNothing()
    {
        VmapBlendMap m = Map(1, 16, 16);
        Assert.False(VmapBlendPaint.Stamp(
            m, new Vector2(5f, 5f), 0.05f, 1f, 1f, 0, VmapPaintMode.Set,
            out _, out _, out _, out _));
        Assert.All(m.Texels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void SubtractUndoesAnEqualAdd()
    {
        VmapBlendMap m = Map(1, 32, 32);
        var at = new Vector2(0.5f, 0.5f);

        VmapBlendPaint.Stamp(m, at, 0.3f, 0.5f, 0.5f, 0, VmapPaintMode.Add, out _, out _, out _, out _);
        VmapBlendPaint.Stamp(m, at, 0.3f, 0.5f, 0.5f, 0, VmapPaintMode.Subtract, out _, out _, out _, out _);

        // Byte quantisation permits one unit of drift per operation, and no more.
        foreach (byte b in m.Texels)
            Assert.True(b <= 1, $"expected back to zero, got {b}");
    }

    /// <summary>
    /// The property that lets a stroke replicate as a polyline instead of a bitmap: two machines given the
    /// same floats must produce the same bytes. It is why the falloff is a polynomial and not a Pow.
    /// </summary>
    [Fact]
    public void TheSameStampProducesTheSameBytes()
    {
        VmapBlendMap a = Map(1, 37, 23);
        VmapBlendMap b = Map(1, 37, 23);
        var at = new Vector2(0.37f, 0.61f);

        VmapBlendPaint.Stamp(a, at, 0.25f, 0.8f, 0.4f, 2, VmapPaintMode.Add, out _, out _, out _, out _);
        VmapBlendPaint.Stamp(b, at, 0.25f, 0.8f, 0.4f, 2, VmapPaintMode.Add, out _, out _, out _, out _);

        Assert.Equal(a.Texels, b.Texels);
    }

    /// <summary>
    /// The undo-correctness property, swept over radii and off-map centres: the declared rectangle must cover
    /// every texel the disc reaches.
    ///
    /// The expected set is computed HERE from the disc's own geometry rather than from
    /// <see cref="VmapBlendPaint.RegionOf"/>, because the stamp loop walks the rectangle RegionOf returns —
    /// so checking one against the other would be checking a function against itself and could not fail.
    /// </summary>
    [Fact]
    public void TheDeclaredRegionCoversEveryTexelTheDiscReaches()
    {
        const int width = 29, height = 17;

        foreach (float radius in new[] { 0.02f, 0.1f, 0.35f, 0.9f, 2f })
            foreach (float cx in new[] { -0.3f, 0f, 0.13f, 0.5f, 0.97f, 1.4f })
                foreach (float cy in new[] { -0.2f, 0.04f, 0.5f, 1.1f })
                {
                    var at = new Vector2(cx, cy);
                    VmapBlendPaint.RegionOf(width, height, at, radius,
                        out int rx, out int ry, out int rw, out int rh);

                    // Independently: which texel centres lie inside the ellipse the stamp draws.
                    float radiusX = radius * width, radiusY = radius * height;
                    float centerX = cx * width - 0.5f, centerY = cy * height - 0.5f;

                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            float dx = (x - centerX) / radiusX;
                            float dy = (y - centerY) / radiusY;
                            if (dx * dx + dy * dy >= 1f)
                                continue;
                            Assert.True(
                                x >= rx && x < rx + rw && y >= ry && y < ry + rh,
                                $"texel ({x},{y}) is inside the disc but outside the declared "
                                + $"({rx},{ry},{rw},{rh}) for r={radius} at ({cx},{cy})");
                        }
                }
    }

    /// <summary>
    /// And the same property one level up, where it actually bit: the OP declares its rectangle before it has
    /// touched anything, so it has to derive the size from the real map rather than from a constant. Two map
    /// sizes, neither of them a number that appears in the op.
    /// </summary>
    [Theory]
    [InlineData(23, 41)]
    [InlineData(128, 64)]
    public void ThePaintOpDeclaresARegionThatCoversWhatItWrites(int width, int height)
    {
        var doc = new VmapDocument();
        doc.BlendMaps.Add(Map(1, width, height));

        var samples = new[] { new Vector2(0.3f, 0.3f), new Vector2(0.55f, 0.6f) };
        var op = new PaintBlendOp(1, 0, VmapPaintMode.Set, samples, 0.12f, 1f, 1f, doc);

        VmapBlendRegion declared = Assert.Single(op.TouchedBlendRegions);
        Assert.True(op.Apply(doc));

        VmapBlendMap m = doc.BlendMaps[0];
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
            {
                if (m.Texels[(y * m.Width + x) * 4] == 0)
                    continue;
                Assert.True(
                    x >= declared.X && x < declared.X + declared.Width
                    && y >= declared.Y && y < declared.Y + declared.Height,
                    $"({x},{y}) written outside the declared region on a {width}x{height} map");
            }
    }

    // ---------------------------------------------------------------- the atlas

    private static VmapDocument DocWithMaps(params (int Id, int W, int H)[] maps)
    {
        var doc = new VmapDocument();
        foreach ((int id, int w, int h) in maps)
            doc.BlendMaps.Add(Map(id, w, h));
        return doc;
    }

    [Fact]
    public void TheAtlasLaysOutIdenticallyEveryTime()
    {
        VmapDocument doc = DocWithMaps((1, 64, 64), (2, 128, 32), (3, 32, 128), (4, 64, 64));

        VmapBlendAtlas a = VmapBlendAtlas.Build(doc);
        VmapBlendAtlas b = VmapBlendAtlas.Build(doc);

        Assert.Equal(a.Slots.Count, b.Slots.Count);
        foreach ((int id, VmapBlendAtlas.Slot slot) in a.Slots)
            Assert.Equal(slot, b.Slots[id]);
    }

    [Fact]
    public void NoTwoSlotsOverlapIncludingTheirGutters()
    {
        var maps = new List<(int, int, int)>();
        for (int i = 1; i <= 40; i++)
            maps.Add((i, 16 + (i % 7) * 24, 16 + (i % 5) * 32));
        VmapBlendAtlas atlas = VmapBlendAtlas.Build(DocWithMaps(maps.ToArray()));

        var slots = new List<VmapBlendAtlas.Slot>(atlas.Slots.Values);
        for (int i = 0; i < slots.Count; i++)
        {
            VmapBlendAtlas.Slot s = slots[i];
            Assert.True(s.X >= 0 && s.Y >= 0);
            Assert.True(s.X + s.Width <= atlas.PageSize, "slot runs off its page in X");
            Assert.True(s.Y + s.Height <= atlas.PageSize, "slot runs off its page in Y");

            for (int j = i + 1; j < slots.Count; j++)
            {
                VmapBlendAtlas.Slot o = slots[j];
                if (o.Page != s.Page)
                    continue;
                bool apart =
                    s.X + s.Width + VmapBlendAtlas.Gutter <= o.X
                    || o.X + o.Width + VmapBlendAtlas.Gutter <= s.X
                    || s.Y + s.Height + VmapBlendAtlas.Gutter <= o.Y
                    || o.Y + o.Height + VmapBlendAtlas.Gutter <= s.Y;
                Assert.True(apart, $"slots overlap on page {s.Page}");
            }
        }
    }

    /// <summary>Bigger than a page gets one to itself. Clipping would look like a painting bug.</summary>
    [Fact]
    public void AnOversizedMapGetsItsOwnPage()
    {
        VmapBlendAtlas atlas = VmapBlendAtlas.Build(DocWithMaps((1, 64, 64), (2, 900, 900)), pageSize: 256);

        Assert.NotEqual(atlas.Slots[1].Page, atlas.Slots[2].Page);
        Assert.Equal(900, atlas.Slots[2].Width);
    }

    /// <summary>
    /// UV 0 and 1 land on texel CENTRES, half a texel in from the slot's edge — without the inset, every
    /// painted face gets a half-strength rim where filtering samples into the gutter.
    /// </summary>
    [Fact]
    public void AtlasUvsAreInsetToTexelCentres()
    {
        VmapBlendAtlas atlas = VmapBlendAtlas.Build(DocWithMaps((1, 64, 64)));

        Assert.True(atlas.TryToAtlasUv(1, Vector2.Zero, out int page, out Vector2 lo));
        Assert.True(atlas.TryToAtlasUv(1, Vector2.One, out _, out Vector2 hi));
        Assert.Equal(0, page);

        VmapBlendAtlas.Slot slot = atlas.Slots[1];
        Assert.Equal((slot.X + 0.5f) / atlas.PageSize, lo.X, 5);
        Assert.Equal((slot.X + slot.Width - 0.5f) / atlas.PageSize, hi.X, 5);
    }

    [Fact]
    public void AnUnknownIdHasNoAtlasUv()
        => Assert.False(VmapBlendAtlas.Build(new VmapDocument()).TryToAtlasUv(1, Vector2.Zero, out _, out _));

    // ---------------------------------------------------------------- the ops

    private static VmapDocument DocWithFace()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(512, 512, 64), 1));
        return doc;
    }

    [Fact]
    public void CreatingABlendMapSizesItFromTheFace()
    {
        VmapDocument doc = DocWithFace();
        // Face 4 is +Z, spanning 512 x 512 units.
        var op = new CreateBlendMapOp(1, 4, unitsPerTexel: 4f);
        Assert.True(op.Apply(doc));

        VmapBlendMap m = Assert.Single(doc.BlendMaps);
        Assert.Equal(128, m.Width);
        Assert.Equal(128, m.Height);
        Assert.Equal(op.BlendMapId, doc.FindBrush(1)!.Faces[4].BlendMapId);
        Assert.True(m.IsValid);
    }

    /// <summary>The projection has to put the whole face inside [0,1], or paint lands off the map.</summary>
    [Fact]
    public void TheBlendProjectionCoversTheWholeFace()
    {
        VmapDocument doc = DocWithFace();
        Assert.True(new CreateBlendMapOp(1, 4, 4f).Apply(doc));

        VmapBlendMap m = doc.BlendMaps[0];
        foreach (Vector3 corner in VmapWinding.BuildFaceWinding(doc.FindBrush(1)!, 4))
        {
            Vector2 uv = m.Projection.Evaluate(corner);
            Assert.InRange(uv.X, -1e-3f, 1f + 1e-3f);
            Assert.InRange(uv.Y, -1e-3f, 1f + 1e-3f);
        }
    }

    [Fact]
    public void CreatingASecondBlendMapOnOneFaceIsRefused()
    {
        VmapDocument doc = DocWithFace();
        Assert.True(new CreateBlendMapOp(1, 4, 4f).Apply(doc));
        Assert.False(new CreateBlendMapOp(1, 4, 4f).Apply(doc));
        Assert.Single(doc.BlendMaps);
    }

    [Fact]
    public void CreatingOnAMissingFaceIsRefused()
    {
        VmapDocument doc = DocWithFace();
        Assert.False(new CreateBlendMapOp(1, 99, 4f).Apply(doc));
        Assert.False(new CreateBlendMapOp(99, 0, 4f).Apply(doc));
        Assert.False(new CreateBlendMapOp(1, 4, 0f).Apply(doc));
    }

    /// <summary>
    /// The test that says the declared region is right: assert on the WHOLE buffer, so a rectangle that is a
    /// texel too small leaves a difference and fails here rather than in play.
    /// </summary>
    [Fact]
    public void UndoingAStrokeRestoresEveryByte()
    {
        VmapDocument doc = DocWithFace();
        var session = new VmapEditSession(doc);
        var make = new CreateBlendMapOp(1, 4, 4f);
        Assert.True(session.Apply(make));

        byte[] before = (byte[])doc.BlendMaps[0].Texels.Clone();

        Assert.True(session.Apply(new PaintBlendOp(
            make.BlendMapId, 0, VmapPaintMode.Add,
            new[] { new Vector2(0.3f, 0.3f), new Vector2(0.5f, 0.55f), new Vector2(0.7f, 0.4f) },
            radiusUv: 0.12f, strength: 0.9f, hardness: 0.3f, doc)));
        Assert.NotEqual(before, doc.BlendMaps[0].Texels);

        Assert.True(session.Undo());
        Assert.Equal(before, doc.BlendMaps[0].Texels);
    }

    [Fact]
    public void RedoingAStrokeReappliesItExactly()
    {
        VmapDocument doc = DocWithFace();
        var session = new VmapEditSession(doc);
        var make = new CreateBlendMapOp(1, 4, 4f);
        Assert.True(session.Apply(make));

        Assert.True(session.Apply(new PaintBlendOp(
            make.BlendMapId, 3, VmapPaintMode.Set, new[] { new Vector2(0.5f, 0.5f) },
            0.2f, 1f, 0.5f, doc)));
        byte[] painted = (byte[])doc.BlendMaps[0].Texels.Clone();

        Assert.True(session.Undo());
        Assert.True(session.Redo());
        Assert.Equal(painted, doc.BlendMaps[0].Texels);
    }

    [Fact]
    public void PaintingAMissingMapIsRefused()
        => Assert.False(new PaintBlendOp(99, 0, VmapPaintMode.Add, new[] { Vector2.One * 0.5f }, 0.2f, 1f, 1f)
            .Apply(new VmapDocument()));

    [Fact]
    public void SetBlendRegionRestoresCapturedTexels()
    {
        VmapDocument doc = DocWithFace();
        var make = new CreateBlendMapOp(1, 4, 4f);
        Assert.True(make.Apply(doc));

        var region = new[] { VmapBlendRegion.Whole(make.BlendMapId) };
        Assert.True(new PaintBlendOp(make.BlendMapId, 0, VmapPaintMode.Set,
            new[] { new Vector2(0.5f, 0.5f) }, 0.3f, 1f, 1f).Apply(doc));

        SetBlendRegionOp captured = SetBlendRegionOp.Capture(doc, region);
        Assert.False(captured.IsEmpty);

        Array.Clear(doc.BlendMaps[0].Texels);
        Assert.True(captured.Apply(doc));
        Assert.Contains(doc.BlendMaps[0].Texels, b => b != 0);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void AnUnpaintedMapWritesNoBlendRecords()
    {
        string text = VmapText.Write(DocWithFace());
        Assert.DoesNotContain("\nx ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nd ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APaintedMapRoundTripsThroughTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "vmapblend_" + Guid.NewGuid().ToString("N") + ".vmap");
        try
        {
            VmapDocument doc = Painted();
            VmapPackage.Write(doc, path);
            AssertSamePaint(doc, VmapPackage.Read(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Texels are deflated before they are base64'd, and the reason is legibility as much as size: a painted
    /// face has to stay a kilobyte of the file, not eighty-five.
    /// </summary>
    [Fact]
    public void PaintIsCompressedBeforeItIsEncoded()
    {
        VmapDocument doc = Painted();
        VmapBlendMap m = doc.BlendMaps[0];
        int rawBase64 = (m.Texels.Length + 2) / 3 * 4;

        int encoded = 0;
        foreach (string line in VmapText.Write(doc).Split('\n'))
            if (line.StartsWith("d ", StringComparison.Ordinal))
                encoded += line.Length - 2;

        Assert.True(encoded < rawBase64 / 8,
            $"{encoded} encoded chars against {rawBase64} raw — the deflate is not doing its job");
    }

    private static VmapDocument Painted()
    {
        VmapDocument doc = DocWithFace();
        var make = new CreateBlendMapOp(1, 4, 8f);
        Assert.True(make.Apply(doc));
        Assert.True(new PaintBlendOp(make.BlendMapId, 2, VmapPaintMode.Add,
            new[] { new Vector2(0.4f, 0.4f), new Vector2(0.6f, 0.6f) }, 0.15f, 0.7f, 0.25f).Apply(doc));
        return doc;
    }

    private static void AssertSamePaint(VmapDocument a, VmapDocument b)
    {
        Assert.Equal(VmapText.Version, b.FormatVersion);
        Assert.Equal(a.BlendMaps.Count, b.BlendMaps.Count);

        VmapBlendMap wrote = a.BlendMaps[0], read = b.BlendMaps[0];
        Assert.Equal(wrote.Id, read.Id);
        Assert.Equal(wrote.Width, read.Width);
        Assert.Equal(wrote.Height, read.Height);
        Assert.Equal(wrote.UnitsPerTexel, read.UnitsPerTexel);
        Assert.Equal(wrote.Projection.AxisU, read.Projection.AxisU);
        Assert.Equal(wrote.Texels, read.Texels);
        Assert.Equal(wrote.Id, b.FindBrush(1)!.Faces[4].BlendMapId);
    }
}
