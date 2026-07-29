using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers the patch primitives and the Modify operations (phase E8).
///
/// The claim worth testing hardest is the one that looks like a bug: these curves are NOT circles. A Q3 patch
/// is biquadratic and a quadratic bezier cannot represent a circular arc, so Radiant builds round primitives
/// from a bounding box and the surface bulges to about 1.06r at the diagonals. Every stock map is built out of
/// that shape, so a patch authored here has to bulge the same way or it will not sit flush against one
/// authored in Radiant. The arithmetic is pinned below so nobody later "fixes" it.
/// </summary>
public class VmapPatchTests
{
    private static readonly Vector3 Lo = new(-64, -64, 0);
    private static readonly Vector3 Hi = new(64, 64, 128);

    private static VmapPatch Build(PatchPrimitive kind, int w = 3, int h = 3)
        => VmapPatchPrimitives.Build(kind, Lo, Hi, "textures/test/curve", w, h);

    /// <summary>Evaluate a quadratic bezier at t.</summary>
    private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
    {
        float it = 1f - t;
        return a * (it * it) + b * (2f * it * t) + c * (t * t);
    }

    private static Vector3 At(VmapPatch p, int row, int col) => p.Controls[row * p.Width + col];

    // ---------------------------------------------------------------- shape and validity

    [Theory]
    [InlineData(PatchPrimitive.SimpleMesh, 3, 3)]
    [InlineData(PatchPrimitive.Bevel, 3, 3)]
    [InlineData(PatchPrimitive.EndCap, 5, 3)]
    [InlineData(PatchPrimitive.Cylinder, 9, 3)]
    [InlineData(PatchPrimitive.DenseCylinder, 9, 5)]
    [InlineData(PatchPrimitive.Cone, 9, 3)]
    [InlineData(PatchPrimitive.Sphere, 9, 5)]
    public void EveryPrimitiveHasItsRadiantDimensions(PatchPrimitive kind, int w, int h)
    {
        VmapPatch p = Build(kind);
        Assert.Equal(w, p.Width);
        Assert.Equal(h, p.Height);
        Assert.Equal((w, h), VmapPatchPrimitives.DimensionsOf(kind));
    }

    [Theory]
    [InlineData(PatchPrimitive.SimpleMesh)]
    [InlineData(PatchPrimitive.Bevel)]
    [InlineData(PatchPrimitive.EndCap)]
    [InlineData(PatchPrimitive.Cylinder)]
    [InlineData(PatchPrimitive.DenseCylinder)]
    [InlineData(PatchPrimitive.Cone)]
    [InlineData(PatchPrimitive.Sphere)]
    public void EveryPrimitiveIsAValidPatch(PatchPrimitive kind)
    {
        VmapPatch p = Build(kind);
        Assert.True(p.IsValid, $"{kind} produced an invalid grid");
        Assert.Equal(p.Width * p.Height, p.Controls.Count);
        Assert.Equal(p.Controls.Count, p.ControlUvs.Count);
        Assert.Equal(1, p.Width & 1);
        Assert.Equal(1, p.Height & 1);
    }

    [Theory]
    [InlineData(PatchPrimitive.Cylinder)]
    [InlineData(PatchPrimitive.Cone)]
    [InlineData(PatchPrimitive.Sphere)]
    public void ClosedPrimitivesActuallyClose(PatchPrimitive kind)
    {
        VmapPatch p = Build(kind);
        for (int row = 0; row < p.Height; row++)
            Assert.Equal(At(p, row, 0), At(p, row, p.Width - 1));
    }

    [Fact]
    public void APrimitiveStaysInsideTheBoxItWasGiven()
    {
        VmapPatch p = Build(PatchPrimitive.Cylinder);
        foreach (Vector3 c in p.Controls)
        {
            Assert.InRange(c.X, Lo.X, Hi.X);
            Assert.InRange(c.Y, Lo.Y, Hi.Y);
            Assert.InRange(c.Z, Lo.Z, Hi.Z);
        }
    }

    // ---------------------------------------------------------------- the bezier facts

    /// <summary>
    /// The bulge, stated exactly. A quarter arc from (r,0) to (0,r) with its control point at the box corner
    /// (r,r) passes through 0.75r,0.75r at the midpoint — a radius of 0.75*sqrt(2)*r, about 1.0607r. This is
    /// Radiant's shape, and matching it is why a cylinder built here meets one built there.
    /// </summary>
    [Fact]
    public void AQuarterArcBulgesToAboutOnePointZeroSixRadius()
    {
        const float r = 64f;
        Vector3 mid = Bezier(new Vector3(r, 0, 0), new Vector3(r, r, 0), new Vector3(0, r, 0), 0.5f);

        Assert.Equal(0.75f * r, mid.X, 3);
        Assert.Equal(0.75f * r, mid.Y, 3);
        Assert.Equal(1.0606601f * r, new Vector2(mid.X, mid.Y).Length(), 2);
    }

    /// <summary>The cylinder's own control points are laid out to produce exactly that arc.</summary>
    [Fact]
    public void TheCylinderRingAlternatesEdgeMidpointAndCorner()
    {
        VmapPatch p = VmapPatchPrimitives.Build(
            PatchPrimitive.Cylinder, new Vector3(-64, -64, 0), new Vector3(64, 64, 128), "m");

        // Column 0 is an edge midpoint (+X, mid Y); column 1 is the +X/+Y corner.
        Assert.Equal(new Vector3(64, 0, 0), At(p, 0, 0));
        Assert.Equal(new Vector3(64, 64, 0), At(p, 0, 1));
        Assert.Equal(new Vector3(0, 64, 0), At(p, 0, 2));

        // And the arc those three describe passes through the documented bulge.
        Vector3 mid = Bezier(At(p, 0, 0), At(p, 0, 1), At(p, 0, 2), 0.5f);
        Assert.Equal(1.0606601f * 64f, new Vector2(mid.X, mid.Y).Length(), 2);
    }

    /// <summary>
    /// A sphere's intermediate rows sit at FULL radius but at the pole's height, because that is where the
    /// tangents meet. Putting them half way up instead gives a lemon.
    /// </summary>
    [Fact]
    public void TheSphereTangentRowsAreAtFullRadiusAndPoleHeight()
    {
        VmapPatch p = Build(PatchPrimitive.Sphere);

        // Row 0 is the south pole: every column collapsed to the centre.
        for (int col = 0; col < p.Width; col++)
        {
            Assert.Equal(0f, At(p, 0, col).X, 3);
            Assert.Equal(0f, At(p, 0, col).Y, 3);
            Assert.Equal(Lo.Z, At(p, 0, col).Z, 3);
        }

        // Row 1 is at the pole's HEIGHT but the equator's radius.
        Assert.Equal(Lo.Z, At(p, 1, 0).Z, 3);
        Assert.Equal(64f, At(p, 1, 0).X, 3);

        // Row 2 is the equator.
        Assert.Equal((Lo.Z + Hi.Z) * 0.5f, At(p, 2, 0).Z, 3);
    }

    /// <summary>A pole-to-equator arc built that way reaches the equator radius at its midpoint height.</summary>
    [Fact]
    public void TheSphereIsRoundedNotPointed()
    {
        VmapPatch p = Build(PatchPrimitive.Sphere);
        Vector3 quarter = Bezier(At(p, 0, 0), At(p, 1, 0), At(p, 2, 0), 0.5f);

        // Half way from pole to equator the surface must already be well out from the axis — a lemon would
        // still be hugging it.
        Assert.True(quarter.X > 32f, $"sphere collapsed toward the axis (x={quarter.X})");
        Assert.True(quarter.Z < (Lo.Z + Hi.Z) * 0.5f, "the arc overshot the equator");
    }

    /// <summary>A cone has FLAT sides, so its middle row is the straight midpoint, not a curve.</summary>
    [Fact]
    public void TheConeHasStraightSides()
    {
        VmapPatch p = Build(PatchPrimitive.Cone);

        Vector3 baseP = At(p, 0, 0);
        Vector3 midP = At(p, 1, 0);
        Vector3 apex = At(p, 2, 0);

        Assert.Equal((baseP.X + apex.X) * 0.5f, midP.X, 3);
        Assert.Equal((baseP.Z + apex.Z) * 0.5f, midP.Z, 3);

        // Every column of the top row is the same point.
        for (int col = 0; col < p.Width; col++)
            Assert.Equal(apex, At(p, 2, col));
    }

    [Fact]
    public void ABevelSweepsExactlyOneCorner()
    {
        VmapPatch p = Build(PatchPrimitive.Bevel);
        Assert.Equal(new Vector3(64, -64, 0), At(p, 0, 0));
        Assert.Equal(new Vector3(64, 64, 0), At(p, 0, 1));    // the corner
        Assert.Equal(new Vector3(-64, 64, 0), At(p, 0, 2));
    }

    // ---------------------------------------------------------------- texturing

    /// <summary>
    /// UVs scale with SIZE. A normalized 0..1 mapping would smear one texture copy over a 1024-unit cylinder,
    /// and the mapper's first job would be undoing it.
    /// </summary>
    [Fact]
    public void UvsScaleWithThePatchSize()
    {
        VmapPatch small = VmapPatchPrimitives.Build(
            PatchPrimitive.Cylinder, new Vector3(-32, -32, 0), new Vector3(32, 32, 64), "m");
        VmapPatch big = VmapPatchPrimitives.Build(
            PatchPrimitive.Cylinder, new Vector3(-512, -512, 0), new Vector3(512, 512, 1024), "m");

        float smallV = small.ControlUvs[^1].Y;
        float bigV = big.ControlUvs[^1].Y;
        Assert.True(bigV > smallV * 8f, $"small={smallV} big={bigV}");
    }

    [Fact]
    public void TheMaterialIsCarried()
        => Assert.Equal("textures/test/curve", Build(PatchPrimitive.Cylinder).Material);

    // ---------------------------------------------------------------- simple mesh sizing

    [Theory]
    [InlineData(3, 3, 3, 3)]
    [InlineData(5, 7, 5, 7)]
    [InlineData(4, 6, 5, 7)]     // even requests round UP to odd
    [InlineData(1, 0, 3, 3)]     // and are floored at the 3x3 minimum
    public void SimpleMeshHonoursTheRequestedSize_KeptOdd(int w, int h, int wantW, int wantH)
    {
        VmapPatch p = Build(PatchPrimitive.SimpleMesh, w, h);
        Assert.Equal(wantW, p.Width);
        Assert.Equal(wantH, p.Height);
        Assert.True(p.IsValid);
    }

    // ---------------------------------------------------------------- modify operations

    [Fact]
    public void InvertReversesEachRow()
    {
        VmapPatch p = Build(PatchPrimitive.EndCap);
        Vector3 firstBefore = At(p, 0, 0);
        Vector3 lastBefore = At(p, 0, p.Width - 1);

        VmapPatch? inv = VmapPatchEdit.Apply(p, PatchOperation.Invert);
        Assert.NotNull(inv);
        Assert.Equal(lastBefore, At(inv!, 0, 0));
        Assert.Equal(firstBefore, At(inv, 0, inv.Width - 1));
    }

    [Fact]
    public void InvertIsItsOwnInverse()
    {
        VmapPatch p = Build(PatchPrimitive.EndCap);
        VmapPatch? twice = VmapPatchEdit.Apply(VmapPatchEdit.Apply(p, PatchOperation.Invert)!, PatchOperation.Invert);
        Assert.NotNull(twice);
        for (int i = 0; i < p.Controls.Count; i++)
            Assert.Equal(p.Controls[i], twice!.Controls[i]);
    }

    [Fact]
    public void TransposeSwapsTheDimensionsWithoutMovingAnyPoint()
    {
        VmapPatch p = Build(PatchPrimitive.EndCap);      // 5 x 3
        VmapPatch? t = VmapPatchEdit.Apply(p, PatchOperation.Transpose);

        Assert.NotNull(t);
        Assert.Equal(3, t!.Width);
        Assert.Equal(5, t.Height);

        // The same set of points, just addressed the other way.
        for (int row = 0; row < p.Height; row++)
            for (int col = 0; col < p.Width; col++)
                Assert.Equal(At(p, row, col), At(t, col, row));
    }

    [Fact]
    public void TransposeTwiceIsTheOriginal()
    {
        VmapPatch p = Build(PatchPrimitive.EndCap);
        VmapPatch? twice = VmapPatchEdit.Apply(VmapPatchEdit.Apply(p, PatchOperation.Transpose)!,
            PatchOperation.Transpose);

        Assert.NotNull(twice);
        Assert.Equal(p.Width, twice!.Width);
        for (int i = 0; i < p.Controls.Count; i++)
            Assert.Equal(p.Controls[i], twice.Controls[i]);
    }

    [Fact]
    public void RedisperseEvenlySpacesTheInteriorPoints()
    {
        VmapPatch p = Build(PatchPrimitive.SimpleMesh, 5, 3);
        // Drag an interior point badly out of line.
        p.Controls[1] = new Vector3(-60, p.Controls[1].Y, p.Controls[1].Z);

        VmapPatch? r = VmapPatchEdit.Apply(p, PatchOperation.RedisperseRows);
        Assert.NotNull(r);

        Vector3 a = At(r!, 0, 0);
        Vector3 b = At(r, 0, r.Width - 1);
        for (int col = 1; col < r.Width - 1; col++)
        {
            Vector3 want = a + (b - a) * (col / (float)(r.Width - 1));
            Assert.Equal(want.X, At(r, 0, col).X, 3);
        }
    }

    [Fact]
    public void RedisperseLeavesTheEndsAlone()
    {
        VmapPatch p = Build(PatchPrimitive.SimpleMesh, 5, 3);
        Vector3 first = At(p, 0, 0);
        Vector3 last = At(p, 0, 4);

        VmapPatch? r = VmapPatchEdit.Apply(p, PatchOperation.RedisperseRows);
        Assert.Equal(first, At(r!, 0, 0));
        Assert.Equal(last, At(r, 0, 4));
    }

    [Fact]
    public void InsertGrowsTheGridByTwo_AndKeepsItOdd()
    {
        VmapPatch p = Build(PatchPrimitive.SimpleMesh, 3, 3);

        VmapPatch? wider = VmapPatchEdit.Apply(p, PatchOperation.InsertColumns);
        Assert.NotNull(wider);
        Assert.Equal(5, wider!.Width);
        Assert.Equal(3, wider.Height);
        Assert.True(wider.IsValid);

        VmapPatch? taller = VmapPatchEdit.Apply(p, PatchOperation.InsertRows);
        Assert.Equal(5, taller!.Height);
        Assert.True(taller.IsValid);
    }

    /// <summary>
    /// Inserting must SUBDIVIDE, not extend: the surface has to stay exactly where it was, with more points to
    /// sculpt it by. Appending rows on the end would grow the patch into space it did not occupy.
    /// </summary>
    [Fact]
    public void InsertKeepsTheCornersWhereTheyWere()
    {
        VmapPatch p = Build(PatchPrimitive.SimpleMesh, 3, 3);
        Vector3 c00 = At(p, 0, 0);
        Vector3 c02 = At(p, 0, 2);
        Vector3 c20 = At(p, 2, 0);

        VmapPatch? bigger = VmapPatchEdit.Apply(p, PatchOperation.InsertColumns);
        Assert.NotNull(bigger);
        Assert.Equal(c00, At(bigger!, 0, 0));
        Assert.Equal(c02, At(bigger, 0, bigger.Width - 1));
        Assert.Equal(c20, At(bigger, 2, 0));
    }

    [Fact]
    public void RemoveShrinksByTwo()
    {
        VmapPatch p = Build(PatchPrimitive.SimpleMesh, 7, 5);

        VmapPatch? narrower = VmapPatchEdit.Apply(p, PatchOperation.RemoveColumns);
        Assert.NotNull(narrower);
        Assert.Equal(5, narrower!.Width);
        Assert.True(narrower.IsValid);
    }

    /// <summary>3x3 is what a biquadratic patch IS; there is nothing below it to remove down to.</summary>
    [Fact]
    public void RemoveIsRefusedAtTheMinimum()
    {
        VmapPatch p = Build(PatchPrimitive.SimpleMesh, 3, 3);
        Assert.Null(VmapPatchEdit.Apply(p, PatchOperation.RemoveColumns));
        Assert.Null(VmapPatchEdit.Apply(p, PatchOperation.RemoveRows));
    }

    [Fact]
    public void EveryOperationHasALabelAndADescription()
    {
        foreach (PatchOperation op in System.Enum.GetValues<PatchOperation>())
        {
            Assert.False(string.IsNullOrWhiteSpace(VmapPatchEdit.Label(op)));
            Assert.False(string.IsNullOrWhiteSpace(VmapPatchEdit.Describe(op)));
        }
    }

    [Fact]
    public void EveryPrimitiveHasADescription()
    {
        foreach (PatchPrimitive kind in System.Enum.GetValues<PatchPrimitive>())
            Assert.False(string.IsNullOrWhiteSpace(VmapPatchPrimitives.Describe(kind)));
    }

    // ---------------------------------------------------------------- the ops

    [Fact]
    public void CreatePatchAddsItToTheDocument_AndUndoRemovesIt()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);

        var op = new CreatePatchOp(PatchPrimitive.Cylinder, Lo, Hi, "textures/test/curve");
        Assert.True(session.Apply(op));
        Assert.Single(doc.Patches);
        Assert.Equal(doc.Patches[0].Id, op.CreatedPatchId);

        Assert.True(session.Undo());
        Assert.Empty(doc.Patches);

        Assert.True(session.Redo());
        Assert.Single(doc.Patches);
    }

    [Fact]
    public void CreatingInAZeroBoxIsRefused()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new CreatePatchOp(PatchPrimitive.Cylinder, Lo, Lo, "m")));
    }

    [Fact]
    public void ModifyReshapesInPlace_AndUndoRestoresTheGrid()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new CreatePatchOp(PatchPrimitive.SimpleMesh, Lo, Hi, "m")));

        VmapPatch live = doc.Patches[0];
        Assert.Equal(3, live.Width);

        Assert.True(session.Apply(new ModifyPatchOp(live.Id, PatchOperation.InsertColumns)));
        Assert.Equal(5, doc.Patches[0].Width);
        Assert.Same(live, doc.Patches[0]);      // same instance: caches keyed on it stay valid

        Assert.True(session.Undo());
        Assert.Equal(3, doc.Patches[0].Width);
        Assert.True(doc.Patches[0].IsValid);
    }

    [Fact]
    public void ARefusedModifyJournalsNothing()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new CreatePatchOp(PatchPrimitive.SimpleMesh, Lo, Hi, "m")));

        Assert.False(session.Apply(new ModifyPatchOp(doc.Patches[0].Id, PatchOperation.RemoveColumns)));
        Assert.Equal("Create SimpleMesh patch", session.UndoLabel);
    }

    [Fact]
    public void ModifyingAMissingPatchIsRefused()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new ModifyPatchOp(99, PatchOperation.Invert)));
    }
}
