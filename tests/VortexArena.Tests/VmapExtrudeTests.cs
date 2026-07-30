using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers the remaining geometry ops: face extrude, edge bevel, and snap-to-grid.
///
/// The invariant every one of them shares is §11.4's: an edit either produces a valid closed convex solid or
/// it does nothing. Extrude is the one with the most ways to go wrong, because it derives a whole brush from
/// a polygon rather than moving planes that already bound one.
/// </summary>
public class VmapExtrudeTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id = 1, string material = "textures/test/wall")
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

    private static VmapDocument DocWithBox()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-32, -32, -32), new Vector3(32, 32, 32)));
        return doc;
    }

    // ---------------------------------------------------------------- extrude

    /// <summary>Face 4 is +Z at z=32; extruding it 64 gives a solid from z=32 to z=96 over the same footprint.</summary>
    [Fact]
    public void ExtrudeGrowsASolidOutOfTheFace()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);

        var op = new ExtrudeFaceOp(1, 4, 64f);
        Assert.True(session.Apply(op));
        Assert.Equal(2, doc.Brushes.Count);

        VmapBrush made = doc.Brushes[1];
        Assert.True(VmapWinding.TryGetBounds(made, out Vector3 mins, out Vector3 maxs));
        Assert.Equal(32f, mins.Z, 2);
        Assert.Equal(96f, maxs.Z, 2);
        Assert.Equal(-32f, mins.X, 2);
        Assert.Equal(32f, maxs.X, 2);
    }

    [Fact]
    public void TheExtrudedSolidIsClosedAndConvex()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new ExtrudeFaceOp(1, 0, 48f)));
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[1]));
    }

    [Fact]
    public void ExtrudeLeavesTheSourceBrushAlone()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new ExtrudeFaceOp(1, 4, 64f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(-32, -32, -32), mins);
        Assert.Equal(new Vector3(32, 32, 32), maxs);
    }

    /// <summary>An extruded wall must continue the texture of the wall it grew from, not arrive untextured.</summary>
    [Fact]
    public void TheExtrusionInheritsMaterialAndAlignment()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new ExtrudeFaceOp(1, 4, 64f)));

        VmapFace src = doc.Brushes[0].Faces[4];
        foreach (VmapFace f in doc.Brushes[1].Faces)
        {
            Assert.Equal(src.Material, f.Material);
            Assert.Equal(src.Projection.OffsetU, f.Projection.OffsetU, 4);
        }
    }

    [Fact]
    public void UndoingAnExtrudeRemovesTheNewBrush()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new ExtrudeFaceOp(1, 4, 64f)));
        Assert.Equal(2, doc.Brushes.Count);

        Assert.True(session.Undo());
        Assert.Single(doc.Brushes);

        Assert.True(session.Redo());
        Assert.Equal(2, doc.Brushes.Count);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.0001f)]
    public void AZeroDistanceExtrudeIsRefused(float distance)
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new ExtrudeFaceOp(1, 4, distance)));
        Assert.Single(doc.Brushes);
    }

    [Fact]
    public void ExtrudingAMissingFaceOrBrushIsRefused()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new ExtrudeFaceOp(1, 99, 64f)));
        Assert.False(session.Apply(new ExtrudeFaceOp(99, 0, 64f)));
    }

    /// <summary>
    /// A negative distance would sweep into the source solid and produce a brush occupying space that is
    /// already filled, so the magnitude is used and the extrusion always grows outward.
    /// </summary>
    [Fact]
    public void ANegativeDistanceStillGrowsOutward()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new ExtrudeFaceOp(1, 4, -64f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[1], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(32f, mins.Z, 2);
        Assert.Equal(96f, maxs.Z, 2);
    }

    /// <summary>Sides come from the WINDING, so a face produced by a clip extrudes as happily as a box face.</summary>
    [Fact]
    public void AClippedFaceCanBeExtruded()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);

        var cut = new VmapPlane(Vector3.Normalize(new Vector3(1, 1, 0)), 0f);
        Assert.True(session.Apply(new ClipSelectionOp(new[] { 1 }, cut, ClipKeep.Back)));

        // The cut created a new face; extruding it must still make a valid solid.
        int newFace = doc.Brushes[0].Faces.Count - 1;
        Assert.True(session.Apply(new ExtrudeFaceOp(1, newFace, 32f)));
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[^1]));
    }

    // ---------------------------------------------------------------- bevel

    [Fact]
    public void BevelChamfersTheCorner()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);

        // The +X/+Y vertical edge of the box.
        var a = new Vector3(32, 32, -32);
        var b = new Vector3(32, 32, 32);

        int facesBefore = doc.Brushes[0].Faces.Count;
        Assert.True(session.Apply(new BevelEdgeOp(1, a, b, 8f)));

        Assert.True(doc.Brushes[0].Faces.Count > facesBefore, "no chamfer face was added");
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[0]));
    }

    [Fact]
    public void BevellingRemovesVolumeFromTheCorner()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new BevelEdgeOp(
            1, new Vector3(32, 32, -32), new Vector3(32, 32, 32), 8f)));

        // The far corner is gone: no winding point should still sit at (32,32).
        foreach (Vector3[] w in VmapWinding.BuildBrushWindings(doc.Brushes[0]))
            foreach (Vector3 v in w)
                Assert.False(v.X > 31.9f && v.Y > 31.9f, $"corner survived at {v}");
    }

    [Fact]
    public void UndoingABevelRestoresTheCorner()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        int before = doc.Brushes[0].Faces.Count;

        Assert.True(session.Apply(new BevelEdgeOp(
            1, new Vector3(32, 32, -32), new Vector3(32, 32, 32), 8f)));
        Assert.True(session.Undo());

        Assert.Equal(before, doc.Brushes[0].Faces.Count);
    }

    [Fact]
    public void BevellingSomethingThatIsNotAnEdgeIsRefused()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);

        // Two points that do not share two faces of this brush.
        Assert.False(session.Apply(new BevelEdgeOp(
            1, new Vector3(999, 999, 0), new Vector3(998, 998, 0), 8f)));
        Assert.False(session.Apply(new BevelEdgeOp(
            1, new Vector3(32, 32, 0), new Vector3(32, 32, 0), 8f)));   // degenerate
    }

    [Fact]
    public void AZeroSizeBevelIsRefused()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new BevelEdgeOp(
            1, new Vector3(32, 32, -32), new Vector3(32, 32, 32), 0f)));
    }

    // ---------------------------------------------------------------- snap to grid

    [Fact]
    public void SnapMovesCornersOntoTheGrid()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-31.4f, -30.2f, -33.7f), new Vector3(33.1f, 29.6f, 30.9f)));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SnapBrushToGridOp(new[] { 1 }, 16f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        foreach (float v in new[] { mins.X, mins.Y, mins.Z, maxs.X, maxs.Y, maxs.Z })
            Assert.Equal(0f, v % 16f, 2);
    }

    [Fact]
    public void SnapKeepsTheBrushValid()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-31.4f, -30.2f, -33.7f), new Vector3(33.1f, 29.6f, 30.9f)));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SnapBrushToGridOp(new[] { 1 }, 16f)));
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[0]));
    }

    /// <summary>A brush already on the grid has nothing to change, so it must not journal an empty step.</summary>
    [Fact]
    public void SnappingAnAlreadyAlignedBrushIsRefused()
    {
        VmapDocument doc = DocWithBox();       // -32..32, already on a 16 grid
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new SnapBrushToGridOp(new[] { 1 }, 16f)));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void UndoingASnapRestoresTheOriginalCorners()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-31.4f, -30.2f, -33.7f), new Vector3(33.1f, 29.6f, 30.9f)));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SnapBrushToGridOp(new[] { 1 }, 16f)));
        Assert.True(session.Undo());

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out _));
        Assert.Equal(-31.4f, mins.X, 2);
    }

    [Fact]
    public void AZeroGridIsRefused()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new SnapBrushToGridOp(new[] { 1 }, 0f)));
    }

    /// <summary>
    /// Snapping to a grid coarser than the brush would collapse it, and a collapsed solid is worse than a
    /// refused edit.
    /// </summary>
    [Fact]
    public void SnappingToAGridCoarserThanTheBrushIsRefused()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-2, -2, -2), new Vector3(2, 2, 2)));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new SnapBrushToGridOp(new[] { 1 }, 256f)));
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[0]));
    }
}
