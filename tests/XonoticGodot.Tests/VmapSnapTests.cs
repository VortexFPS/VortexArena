using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Snapping a drag onto nearby geometry (backlog T5).
///
/// What this is for: a hairline gap between two walls leaks light and shows a seam, and at editing zoom it is
/// invisible. The tiers go vertex, then edge, then face plane, most specific first — settling for the edge
/// through a corner would leave the dragged point sliding along it instead of landing ON the corner, and
/// settling for the plane would do the same in two dimensions.
/// </summary>
public class VmapSnapTests
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

    private static VmapDocument DocWithBox()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        return doc;
    }

    [Fact]
    public void AVertexWinsOverTheEdgesAndFacesThroughIt()
    {
        VmapDocument doc = DocWithBox();

        // Just off the (64,64,64) corner — which is also on three edges and three faces.
        var result = VmapPicking.SnapToGeometry(doc, new Vector3(67f, 66f, 65f), radius: 16f);

        Assert.True(result.Snapped);
        Assert.Equal(VmapSelectionKind.Vertex, result.TargetKind);
        Assert.Equal(new Vector3(64, 64, 64), result.Position);
    }

    [Fact]
    public void AnEdgeWinsOverTheFacesThroughIt()
    {
        VmapDocument doc = DocWithBox();

        // Near the middle of the top-front edge (y=64, z=64), far from either corner.
        var result = VmapPicking.SnapToGeometry(doc, new Vector3(32f, 68f, 67f), radius: 16f);

        Assert.True(result.Snapped);
        Assert.Equal(VmapSelectionKind.Edge, result.TargetKind);
        Assert.Equal(64f, result.Position.Y, 3);
        Assert.Equal(64f, result.Position.Z, 3);
        Assert.Equal(32f, result.Position.X, 3);      // slid along the edge, not pulled to a corner
    }

    [Fact]
    public void AFacePlaneCatchesWhatNoVertexOrEdgeIsNear()
    {
        // The tier the docstring promised and nobody wrote: landing a brush FLUSH against a wall it is
        // nowhere near a corner of.
        VmapDocument doc = DocWithBox();

        var result = VmapPicking.SnapToGeometry(doc, new Vector3(32f, 32f, 70f), radius: 16f);

        Assert.True(result.Snapped);
        Assert.Equal(VmapSelectionKind.Face, result.TargetKind);
        Assert.Equal(64f, result.Position.Z, 3);       // pulled onto the top face
        Assert.Equal(32f, result.Position.X, 3);       // and nowhere else
        Assert.Equal(32f, result.Position.Y, 3);
    }

    [Fact]
    public void AFacePlaneDoesNotReachBeyondItsOwnWinding()
    {
        // An infinite plane would drag things onto surfaces they are not over, from anywhere on the map. The
        // point here is level with the top face and within the radius of its PLANE, but well outside the brush.
        VmapDocument doc = DocWithBox();

        var result = VmapPicking.SnapToGeometry(doc, new Vector3(500f, 500f, 70f), radius: 16f);

        Assert.False(result.Snapped);
    }

    [Fact]
    public void TheDraggedBrushIsNeverSnappedToItself()
    {
        VmapDocument doc = DocWithBox();

        var result = VmapPicking.SnapToGeometry(
            doc, new Vector3(67f, 66f, 65f), radius: 16f, excludeBrushIds: new[] { 1 });

        Assert.False(result.Snapped);
    }

    [Fact]
    public void NothingSnapsOutsideTheRadius()
    {
        VmapDocument doc = DocWithBox();
        Assert.False(VmapPicking.SnapToGeometry(doc, new Vector3(200f, 200f, 200f), radius: 16f).Snapped);

        // And a zero radius disables it outright — how the cvar turns the whole feature off.
        Assert.False(VmapPicking.SnapToGeometry(doc, new Vector3(65f, 65f, 65f), radius: 0f).Snapped);
    }

    [Fact]
    public void PatchControlPointsSnapToo()
    {
        // A wall meeting the lip of a curved platform is the same job as a wall meeting another wall, and
        // leaving patches out stopped the snap working near exactly the geometry most likely to need it.
        var doc = new VmapDocument();
        var patch = new VmapPatch { Id = 1, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                patch.Controls.Add(new Vector3(col * 64f, row * 64f, 0f));
                patch.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        doc.Patches.Add(patch);

        var result = VmapPicking.SnapToGeometry(doc, new Vector3(130f, 126f, 4f), radius: 16f);

        Assert.True(result.Snapped);
        Assert.Equal(new Vector3(128, 128, 0), result.Position);
    }

    [Fact]
    public void TheClosestOfTwoCandidatesWins()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        doc.Brushes.Add(Box(new Vector3(80, 0, 0), new Vector3(144, 64, 64), id: 2));

        // Between the two brushes, nearer the second's left face corner at x=80.
        var result = VmapPicking.SnapToGeometry(doc, new Vector3(76f, 64f, 64f), radius: 16f);

        Assert.True(result.Snapped);
        Assert.Equal(2, result.TargetBrushId);
        Assert.Equal(80f, result.Position.X, 3);
    }
}
