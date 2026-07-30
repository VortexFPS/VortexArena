using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers <see cref="ScaleSelectionOp"/> (phase E7) and the patch half of the undo journal.
///
/// The interesting case is NON-UNIFORM scale. A brush face is a plane, not a polygon, and a plane's normal
/// transforms by the inverse transpose of the scale rather than by the scale — the two agree for uniform
/// scales, which is exactly why scaling the normal directly is a bug that passes every uniform test and skews
/// every wall the moment the axes differ. The oblique-face tests below are the ones that would catch it.
/// </summary>
public class VmapScaleTests
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

    /// <summary>A unit-ish patch: a flat 3x3 control grid in the XY plane centred on the origin.</summary>
    private static VmapPatch FlatPatch(int id = 1, float half = 32f, float z = 0f)
    {
        var p = new VmapPatch { Id = id, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(new Vector3((col - 1) * half, (row - 1) * half, z));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        return p;
    }

    private static VmapDocument DocWithBox(Vector3 mins, Vector3 maxs, int id = 1)
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(mins, maxs, id));
        return doc;
    }

    // ---------------------------------------------------------------- uniform

    [Fact]
    public void UniformScale_AboutTheCentre_GrowsTheBoxEvenly()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(new[] { 1 }, Array.Empty<int>(), Vector3.Zero, 2f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(-32, -32, -32), mins);
        Assert.Equal(new Vector3(32, 32, 32), maxs);
    }

    [Fact]
    public void ScaleAboutAnOffPivot_MovesTheSolidAsWellAsResizingIt()
    {
        // Pivot at the box's own -X face: that face must stay put while the far one travels.
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(32, 32, 32));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(new[] { 1 }, Array.Empty<int>(), Vector3.Zero, 2f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(Vector3.Zero, mins);
        Assert.Equal(new Vector3(64, 64, 64), maxs);
    }

    // ---------------------------------------------------------------- per-axis

    [Fact]
    public void PerAxisScale_StretchesOnlyTheNamedAxis()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(
            new[] { 1 }, Array.Empty<int>(), Vector3.Zero, new Vector3(4f, 1f, 1f))));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(-64, -16, -16), mins);
        Assert.Equal(new Vector3(64, 16, 16), maxs);
    }

    /// <summary>
    /// The inverse-transpose test. A 45° wedge face scaled 4x on X only must come back at the angle the
    /// STRETCHED SOLID has, not at 45°. Scaling the normal directly leaves it at 45° and the face no longer
    /// touches the corners it is supposed to join.
    /// </summary>
    [Fact]
    public void PerAxisScale_TiltsAnObliqueFaceCorrectly()
    {
        // A wedge: the box with its +X/+Z corner cut off by a 45° plane through (16,*,0) and (0,*,16).
        VmapBrush wedge = Box(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var n = Vector3.Normalize(new Vector3(1, 0, 1));
        wedge.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, Vector3.Dot(new Vector3(16, 0, 0), n)),
            Material = "textures/test/wall",
            Projection = VmapTexProjection.AxialFor(n),
        });

        var doc = new VmapDocument();
        doc.Brushes.Add(wedge);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(
            new[] { 1 }, Array.Empty<int>(), Vector3.Zero, new Vector3(4f, 1f, 1f))));

        // After a 4x stretch on X, the plane that ran (16,0,0)→(0,0,16) runs (64,0,0)→(0,0,16), so its normal
        // is proportional to (1, 0, 4) — the inverse-transpose result. The naive version keeps (1,0,1).
        VmapPlane oblique = doc.Brushes[0].Faces[6].Plane;
        Vector3 expected = Vector3.Normalize(new Vector3(1, 0, 4));
        Assert.True(Vector3.Dot(oblique.Normal, expected) > 0.9999f,
            $"normal was {oblique.Normal}, expected {expected}");

        // And the plane must still pass through the stretched corner.
        Assert.Equal(Vector3.Dot(new Vector3(64, 0, 0), oblique.Normal), oblique.Dist, 3);
    }

    [Fact]
    public void ScaledBrushStaysAClosedConvexSolid()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(
            new[] { 1 }, Array.Empty<int>(), new Vector3(100, 0, 0), new Vector3(0.25f, 3f, 1.5f))));

        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[0]));
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(1f, -1f, 1f)]      // a mirror: would invert the plane normals, so it is not a scale
    [InlineData(1f, 1f, -2f)]
    public void NonPositiveScale_IsRefused_AndChangesNothing(float sx, float sy, float sz)
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new ScaleSelectionOp(
            new[] { 1 }, Array.Empty<int>(), Vector3.Zero, new Vector3(sx, sy, sz))));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(-16, -16, -16), mins);
        Assert.Equal(new Vector3(16, 16, 16), maxs);
        Assert.False(session.CanUndo);   // a refused op must not journal a step
    }

    [Fact]
    public void IdentityScale_IsRefused()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new ScaleSelectionOp(new[] { 1 }, Array.Empty<int>(), Vector3.Zero, 1f)));
    }

    [Fact]
    public void MissingBrush_IsRefused_BeforeTouchingTheOthers()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        doc.Brushes.Add(Box(new Vector3(64, 64, 64), new Vector3(96, 96, 96), id: 2));
        var session = new VmapEditSession(doc);

        // Id 99 does not exist: the whole op must refuse rather than scaling 1 and 2 and then failing.
        Assert.False(session.Apply(new ScaleSelectionOp(
            new[] { 1, 2, 99 }, Array.Empty<int>(), Vector3.Zero, 2f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out _));
        Assert.Equal(new Vector3(-16, -16, -16), mins);
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public void Undo_RestoresTheOriginalBoxExactly()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(
            new[] { 1 }, Array.Empty<int>(), new Vector3(8, -4, 2), new Vector3(3f, 0.5f, 1.75f))));
        Assert.True(session.Undo());

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(-16, -16, -16), mins);
        Assert.Equal(new Vector3(16, 16, 16), maxs);
    }

    // ---------------------------------------------------------------- patches

    [Fact]
    public void ScalingAPatch_MovesItsControlPoints()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(
            Array.Empty<int>(), new[] { 1 }, Vector3.Zero, new Vector3(2f, 4f, 1f))));

        // Corner control point (32, 32, 0) -> (64, 128, 0).
        Assert.Equal(new Vector3(64, 128, 0), doc.Patches[0].Controls[8]);
        // Centre control point sits on the pivot and must not move.
        Assert.Equal(Vector3.Zero, doc.Patches[0].Controls[4]);
    }

    /// <summary>
    /// The patch journal. Before E7 an op could move a patch without declaring it, and the journal snapshotted
    /// brushes only — so undo silently did nothing and the mapper had no way back.
    /// </summary>
    [Fact]
    public void UndoingAPatchScale_PutsTheControlPointsBack()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(
            Array.Empty<int>(), new[] { 1 }, Vector3.Zero, 3f)));
        Assert.Equal(new Vector3(96, 96, 0), doc.Patches[0].Controls[8]);

        Assert.True(session.Undo());
        Assert.Equal(new Vector3(32, 32, 0), doc.Patches[0].Controls[8]);

        Assert.True(session.Redo());
        Assert.Equal(new Vector3(96, 96, 0), doc.Patches[0].Controls[8]);
    }

    [Fact]
    public void UndoingAPatchMove_PutsItBack()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new TranslatePatchesOp(new[] { 1 }, new Vector3(0, 0, 128))));
        Assert.Equal(new Vector3(32, 32, 128), doc.Patches[0].Controls[8]);

        Assert.True(session.Undo());
        Assert.Equal(new Vector3(32, 32, 0), doc.Patches[0].Controls[8]);
    }

    [Fact]
    public void UndoRestoresPatchesInPlace_SoCachesKeyedOnTheObjectStayValid()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        VmapPatch live = doc.Patches[0];
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new TranslatePatchesOp(new[] { 1 }, new Vector3(64, 0, 0))));
        Assert.True(session.Undo());

        Assert.Same(live, doc.Patches[0]);
    }

    // ---------------------------------------------------------------- mixed selection

    [Fact]
    public void OneOp_ScalesBrushesAndPatchesTogether_AsASingleUndoStep()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new ScaleSelectionOp(new[] { 1 }, new[] { 1 }, Vector3.Zero, 2f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out _, out Vector3 maxs));
        Assert.Equal(new Vector3(32, 32, 32), maxs);
        Assert.Equal(new Vector3(64, 64, 0), doc.Patches[0].Controls[8]);

        // ONE step, not two: a mixed selection scaled about a shared pivot has to come back together.
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out _, out Vector3 backMaxs));
        Assert.Equal(new Vector3(16, 16, 16), backMaxs);
        Assert.Equal(new Vector3(32, 32, 0), doc.Patches[0].Controls[8]);
    }

    // ---------------------------------------------------------------- clone completeness

    /// <summary>
    /// Undo restores from a clone, so anything Clone drops is silently lost by every undo. SubmodelIndex and
    /// IsToolBrush are classification rather than geometry, which is what made them easy to miss: losing the
    /// submodel moves a gametype-conditional brush into worldspawn, and losing the tool flag makes a caulk
    /// volume pickable.
    /// </summary>
    [Fact]
    public void BrushClone_CarriesClassification_NotJustGeometry()
    {
        VmapBrush b = Box(new Vector3(-8, -8, -8), new Vector3(8, 8, 8));
        b.SubmodelIndex = 7;
        b.IsToolBrush = true;
        b.IsDetail = true;

        VmapBrush copy = b.Clone();

        Assert.Equal(7, copy.SubmodelIndex);
        Assert.True(copy.IsToolBrush);
        Assert.True(copy.IsDetail);
    }

    [Fact]
    public void UndoingADelete_BringsTheBrushBackWithItsClassificationIntact()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        doc.Brushes[0].SubmodelIndex = 3;
        doc.Brushes[0].IsToolBrush = true;
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new DeleteBrushesOp(new[] { 1 })));
        Assert.Empty(doc.Brushes);

        Assert.True(session.Undo());
        Assert.Single(doc.Brushes);
        Assert.Equal(3, doc.Brushes[0].SubmodelIndex);
        Assert.True(doc.Brushes[0].IsToolBrush);
    }

    [Fact]
    public void PatchClone_IsIndependentOfItsSource()
    {
        VmapPatch p = FlatPatch();
        VmapPatch copy = p.Clone();

        p.Controls[0] = new Vector3(999, 999, 999);
        p.Material = "changed";

        Assert.Equal(new Vector3(-32, -32, 0), copy.Controls[0]);
        Assert.Equal("textures/test/curve", copy.Material);
    }

    [Fact]
    public void EntityClone_DoesNotShareItsFieldDictionary()
    {
        var e = new VmapEntity { Id = 5, ClassName = "weapon_devastator" };
        e.Fields["classname"] = "weapon_devastator";
        e.Fields["origin"] = "16 32 64";
        e.BrushIds.Add(9);

        VmapEntity copy = e.Clone();
        e.Fields["origin"] = "0 0 0";
        e.BrushIds.Add(10);

        Assert.Equal("16 32 64", copy.Fields["origin"]);
        Assert.Single(copy.BrushIds);
    }
}
