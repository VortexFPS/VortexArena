using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers the editor's geometry edits (phases E3–E5): the op journal with undo/redo, ray picking, the convex
/// plane-refit behind vertex and edge drags, rotation, snapping, brush creation and the clipper.
///
/// The through-line is the invariant from the design doc (§11.4): an edit either produces a valid closed
/// convex solid or it does nothing at all. Half-applied geometry is worse than a refused drag, because the
/// mapper cannot see that a brush has quietly stopped being solid until they playtest it.
/// </summary>
public class VmapEditTests
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

    private static VmapDocument DocWithBox(Vector3 mins, Vector3 maxs, int id = 1)
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(mins, maxs, id));
        return doc;
    }

    // ---------------------------------------------------------------- E3: translate / face push

    [Fact]
    public void Translate_MovesTheWholeSolid_Exactly()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new TranslateBrushesOp(new[] { 1 }, new Vector3(64, 0, 32))));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(48, -16, 16), mins);
        Assert.Equal(new Vector3(80, 16, 48), maxs);
    }

    [Fact]
    public void PushFace_MovesOnlyThatWall()
    {
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        // Face 0 is +X. Push it out 48 units.
        Assert.True(session.Apply(new MoveFaceOp(1, 0, 48f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(64f, maxs.X, 3);
        Assert.Equal(-16f, mins.X, 3);   // the opposite wall did not move
        Assert.Equal(16f, maxs.Y, 3);
    }

    [Fact]
    public void PushFace_ThroughTheOppositeWall_IsRefusedAndChangesNothing()
    {
        // The failure mode that matters: pushing a wall past the far side collapses the solid. Committing that
        // would leave a brush with no interior that still renders, so the op must refuse and roll back.
        VmapDocument doc = DocWithBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new MoveFaceOp(1, 0, -64f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(16f, maxs.X, 3);     // untouched
        Assert.Equal(-16f, mins.X, 3);
        Assert.False(session.CanUndo);    // and a refused op must not leave an empty undo step
    }

    // ---------------------------------------------------------------- E3: vertex / edge drag

    [Fact]
    public void VertexDrag_LandsTheGrabbedCornerExactlyOnTarget()
    {
        // Worth being precise about what a vertex drag on a convex brush actually means. A brush stores
        // PLANES, so dragging one corner of a box has to tilt the quad faces that meet there — a quad with one
        // corner pulled out of plane is not representable otherwise. The consequence is that the neighbouring
        // corners on those faces move too. That is correct, and it is also how Radiant behaves; what must NOT
        // happen is the grabbed corner landing somewhere other than where the mapper dropped it.
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);
        var corner = new Vector3(64, 64, 64);

        Assert.True(session.Apply(new MoveVerticesOp(1, new[] { corner }, new Vector3(0, 0, -32))));

        VmapBrush brush = doc.Brushes[0];
        Assert.True(VmapWinding.IsClosedConvex(brush));

        Vector3[] pts = VmapWinding.BrushPoints(brush);
        Assert.Contains(pts, p => (p - new Vector3(64, 64, 32)).Length() < 0.01f);  // exactly on target
        Assert.DoesNotContain(pts, p => (p - corner).Length() < 0.5f);              // old corner is gone
        Assert.Contains(pts, p => (p - Vector3.Zero).Length() < 0.01f);             // the base is untouched
    }

    [Fact]
    public void VertexDrag_WithNoMatchingVertex_IsRefused()
    {
        // A grab that resolved to a stale position (the brush moved under it, or another client edited it)
        // must be a no-op rather than silently dragging nothing and journalling an empty step.
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new MoveVerticesOp(1, new[] { new Vector3(500, 500, 500) }, new Vector3(0, 0, -8))));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void EdgeDrag_MovesBothEndpointsTogether()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        // The top edge along +X at y=64.
        var a = new Vector3(0, 64, 64);
        var b = new Vector3(64, 64, 64);
        Assert.True(session.Apply(new MoveVerticesOp(1, new[] { a, b }, new Vector3(0, 0, -24))));

        VmapBrush brush = doc.Brushes[0];
        Assert.True(VmapWinding.IsClosedConvex(brush));
        Vector3[] pts = VmapWinding.BrushPoints(brush);
        Assert.Contains(pts, p => (p - new Vector3(0, 64, 40)).Length() < 0.5f);
        Assert.Contains(pts, p => (p - new Vector3(64, 64, 40)).Length() < 0.5f);
    }

    [Fact]
    public void VertexDrag_ThatFlattensTheSolid_IsRefused()
    {
        // The genuine invalid case, and the one a mapper hits by accident: collapsing a solid to zero volume.
        // A tetrahedron's apex dragged down onto its own base plane leaves four coplanar points — no interior,
        // no valid brush. Committing it would leave geometry that still draws but has nothing to collide with.
        var tet = new VmapBrush { Id = 1, ContentFlags = 1 };
        void Face(Vector3 a, Vector3 b, Vector3 c)
        {
            Assert.True(VmapPlane.TryFromPoints(a, b, c, out VmapPlane plane));
            tet.Faces.Add(new VmapFace { Plane = plane, Material = "t", Projection = VmapTexProjection.AxialFor(plane.Normal) });
        }

        var p0 = new Vector3(0, 0, 0);
        var p1 = new Vector3(64, 0, 0);
        var p2 = new Vector3(0, 64, 0);
        var apex = new Vector3(0, 0, 64);

        Face(p0, p1, p2);      // base (outward -Z)
        Face(p0, apex, p1);
        Face(p1, apex, p2);
        Face(p2, apex, p0);

        Assert.True(VmapWinding.IsClosedConvex(tet));

        var doc = new VmapDocument();
        doc.Brushes.Add(tet);
        var session = new VmapEditSession(doc);
        VmapPlane baseBefore = tet.Faces[0].Plane;

        // Drop the apex exactly onto the base plane: zero volume.
        Assert.False(session.Apply(new MoveVerticesOp(1, new[] { apex }, new Vector3(0, 0, -64))));

        Assert.Equal(baseBefore.Dist, doc.Brushes[0].Faces[0].Plane.Dist, 4);
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[0]));   // still the original solid
        Assert.False(session.CanUndo);
    }

    // ---------------------------------------------------------------- E3: undo / redo

    [Fact]
    public void UndoRedo_RoundTripsGeometryExactly()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        Vector3[] original = VmapWinding.BrushPoints(doc.Brushes[0]).OrderBy(Key).ToArray();

        Assert.True(session.Apply(new MoveVerticesOp(1, new[] { new Vector3(64, 64, 64) }, new Vector3(0, 0, -32))));
        Vector3[] edited = VmapWinding.BrushPoints(doc.Brushes[0]).OrderBy(Key).ToArray();

        Assert.True(session.CanUndo);
        Assert.True(session.Undo());
        Vector3[] undone = VmapWinding.BrushPoints(doc.Brushes[0]).OrderBy(Key).ToArray();

        // Exactness is the point of snapshot-based undo: a fitted plane is not exactly recoverable by
        // re-dragging, so a naive inverse-op undo would drift a little on every cycle.
        Assert.Equal(original.Length, undone.Length);
        for (int i = 0; i < original.Length; i++)
            Assert.True((original[i] - undone[i]).Length() < 1e-4f, $"undo drifted at {i}");

        Assert.True(session.CanRedo);
        Assert.True(session.Redo());
        Vector3[] redone = VmapWinding.BrushPoints(doc.Brushes[0]).OrderBy(Key).ToArray();
        for (int i = 0; i < edited.Length; i++)
            Assert.True((edited[i] - redone[i]).Length() < 1e-4f, $"redo drifted at {i}");

        static float Key(Vector3 v) => v.X * 1e6f + v.Y * 1e3f + v.Z;
    }

    [Fact]
    public void EditingAfterUndo_DiscardsTheRedoFuture()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new MoveFaceOp(1, 0, 16f)));
        Assert.True(session.Undo());
        Assert.True(session.CanRedo);

        Assert.True(session.Apply(new MoveFaceOp(1, 4, 16f)));
        Assert.False(session.CanRedo);   // linear history: the abandoned branch is gone
    }

    [Fact]
    public void Undo_RemovesACreatedBrush_AndRedoBringsItBack()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);

        var create = new CreateBoxBrushOp(new Vector3(0, 0, 0), new Vector3(64, 64, 64), "textures/test/wall");
        Assert.True(session.Apply(create));
        Assert.Single(doc.Brushes);

        Assert.True(session.Undo());
        Assert.Empty(doc.Brushes);

        Assert.True(session.Redo());
        Assert.Single(doc.Brushes);
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[0]));
    }

    // ---------------------------------------------------------------- E3: picking

    [Fact]
    public void Pick_HitsTheNearFace_NotTheFarOne()
    {
        VmapDocument doc = DocWithBox(new Vector3(-32, -32, -32), new Vector3(32, 32, 32));

        // Looking from -X toward +X: must hit the -X wall at x=-32, not the far +X wall.
        VmapPickResult hit = VmapPicking.Pick(doc, new Vector3(-256, 0, 0), new Vector3(1, 0, 0));

        Assert.True(hit.Hit);
        Assert.Equal(-32f, hit.Point.X, 3);
        Assert.Equal(new Vector3(-1, 0, 0), hit.Normal);
        Assert.Equal(VmapSelectionKind.Face, hit.Selection.Kind);
        Assert.Equal(1, hit.Selection.BrushId);
    }

    [Fact]
    public void Pick_ResolvesAVertexWhenTheRayLandsNearACorner()
    {
        VmapDocument doc = DocWithBox(new Vector3(-32, -32, -32), new Vector3(32, 32, 32));

        // Aim at the -X wall right next to the (-32, 32, 32) corner.
        VmapPickResult hit = VmapPicking.Pick(
            doc, new Vector3(-256, 30f, 30f), new Vector3(1, 0, 0), VmapSelectionKind.Vertex, grabRadius: 8f);

        Assert.True(hit.Hit);
        Assert.Equal(VmapSelectionKind.Vertex, hit.Selection.Kind);
        Assert.Single(hit.Selection.Vertices);
        Assert.True((hit.Selection.Vertices[0] - new Vector3(-32, 32, 32)).Length() < 0.5f);
    }

    [Fact]
    public void Pick_FallsBackToTheFaceWhenNoCornerIsNear()
    {
        VmapDocument doc = DocWithBox(new Vector3(-32, -32, -32), new Vector3(32, 32, 32));

        VmapPickResult hit = VmapPicking.Pick(
            doc, new Vector3(-256, 0, 0), new Vector3(1, 0, 0), VmapSelectionKind.Vertex, grabRadius: 8f);

        Assert.True(hit.Hit);
        Assert.Equal(VmapSelectionKind.Face, hit.Selection.Kind);
    }

    [Fact]
    public void Pick_MissesWhenTheRayPointsAway()
    {
        VmapDocument doc = DocWithBox(new Vector3(-32, -32, -32), new Vector3(32, 32, 32));
        Assert.False(VmapPicking.Pick(doc, new Vector3(-256, 0, 0), new Vector3(-1, 0, 0)).Hit);
    }

    // ---------------------------------------------------------------- E4: rotation + snapping

    [Fact]
    public void Rotate90AboutZ_MapsTheBoxOntoItsSwappedExtents()
    {
        VmapDocument doc = DocWithBox(new Vector3(-64, -16, 0), new Vector3(64, 16, 32));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateBrushesOp(new[] { 1 }, Vector3.Zero, new Vector3(0, 0, 1), 90f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(-16f, mins.X, 2);
        Assert.Equal(16f, maxs.X, 2);
        Assert.Equal(-64f, mins.Y, 2);
        Assert.Equal(64f, maxs.Y, 2);
        Assert.Equal(0f, mins.Z, 2);      // the rotation axis is untouched
        Assert.Equal(32f, maxs.Z, 2);
    }

    [Fact]
    public void Rotate_IsReversible_ByRotatingBack()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(32, 64, 16));
        var session = new VmapEditSession(doc);
        var pivot = new Vector3(16, 32, 8);

        Assert.True(session.Apply(new RotateBrushesOp(new[] { 1 }, pivot, new Vector3(0, 0, 1), 37f)));
        Assert.True(session.Apply(new RotateBrushesOp(new[] { 1 }, pivot, new Vector3(0, 0, 1), -37f)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(0f, mins.X, 2);
        Assert.Equal(32f, maxs.X, 2);
        Assert.Equal(64f, maxs.Y, 2);
    }

    [Fact]
    public void GeometrySnap_PullsOntoAVertexOfANeighbouringBrush()
    {
        // The gap this prevents: two walls that nearly meet leak light and show a seam, and the error is
        // invisible at editing zoom.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 64, 64), id: 1));
        doc.Brushes.Add(Box(new Vector3(128, 0, 0), new Vector3(192, 64, 64), id: 2));

        var dragged = new Vector3(125f, 2f, 62f);   // near brush 2's (128, 0, 64) corner
        VmapPicking.SnapResult snap = VmapPicking.SnapToGeometry(doc, dragged, radius: 8f, excludeBrushIds: new[] { 1 });

        Assert.True(snap.Snapped);
        Assert.Equal(VmapSelectionKind.Vertex, snap.TargetKind);
        Assert.Equal(2, snap.TargetBrushId);
        Assert.True((snap.Position - new Vector3(128, 0, 64)).Length() < 1e-3f);
    }

    [Fact]
    public void GeometrySnap_IgnoresTheBrushBeingDragged()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        VmapPicking.SnapResult snap = VmapPicking.SnapToGeometry(
            doc, new Vector3(2, 2, 2), radius: 8f, excludeBrushIds: new[] { 1 });

        Assert.False(snap.Snapped);   // snapping a brush to its own corners would freeze every drag
    }

    [Fact]
    public void DragResolution_PrefersGeometryOverGrid_ThenFallsBackToGrid()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 64, 64), id: 1));
        doc.Brushes.Add(Box(new Vector3(100, 0, 0), new Vector3(164, 64, 64), id: 2));

        // Inside the snap radius of brush 2's corner at (100, 0, 0): geometry wins even though the grid would
        // have rounded to 96.
        Vector3 snapped = VmapPicking.ResolveDragPosition(
            doc, new Vector3(98f, 1f, 1f), gridSize: 32f, snapRadius: 8f, excludeBrushIds: new[] { 1 }, out var s1);
        Assert.True(s1.Snapped);
        Assert.True((snapped - new Vector3(100, 0, 0)).Length() < 1e-3f);

        // Far from any geometry: the grid takes over.
        Vector3 gridded = VmapPicking.ResolveDragPosition(
            doc, new Vector3(401f, 30f, 17f), gridSize: 32f, snapRadius: 8f, excludeBrushIds: new[] { 1 }, out var s2);
        Assert.False(s2.Snapped);
        Assert.Equal(new Vector3(416, 32, 32), gridded);
    }

    // ---------------------------------------------------------------- E5: creation, clipper, delete

    [Fact]
    public void CreateBox_ProducesAValidSolidWithSixFaces()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);

        var op = new CreateBoxBrushOp(new Vector3(0, 0, 0), new Vector3(128, 64, 32), "textures/test/floor");
        Assert.True(session.Apply(op));

        VmapBrush brush = doc.Brushes[0];
        Assert.Equal(op.CreatedBrushId, brush.Id);
        Assert.Equal(6, brush.Faces.Count);
        Assert.True(VmapWinding.IsClosedConvex(brush));
        Assert.All(brush.Faces, f => Assert.Equal("textures/test/floor", f.Material));
    }

    [Fact]
    public void CreateBox_WithZeroThickness_IsRefused()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);

        // A drag that never left the plane it started on — common when a click is mistaken for a drag.
        Assert.False(session.Apply(new CreateBoxBrushOp(new Vector3(0, 0, 0), new Vector3(64, 64, 0), "t")));
        Assert.Empty(doc.Brushes);
    }

    [Fact]
    public void Clipper_KeepsOneHalfAndBothHalvesStayConvex()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        // Cut at x = 32, keeping the half behind +X (i.e. x <= 32).
        Assert.True(session.Apply(new ClipBrushOp(1, new VmapPlane(new Vector3(1, 0, 0), 32f))));

        Assert.Single(doc.Brushes);
        Assert.True(VmapWinding.IsClosedConvex(doc.Brushes[0]));
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(0f, mins.X, 3);
        Assert.Equal(32f, maxs.X, 3);
    }

    [Fact]
    public void Clipper_CanKeepBothHalves_AndTheOffcutInheritsEntityOwnership()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.BrushIds.Add(1);
        doc.Entities.Add(door);
        var session = new VmapEditSession(doc);

        var op = new ClipBrushOp(1, new VmapPlane(new Vector3(1, 0, 0), 32f), keepBothHalves: true);
        Assert.True(session.Apply(op));

        Assert.Equal(2, doc.Brushes.Count);
        Assert.All(doc.Brushes, b => Assert.True(VmapWinding.IsClosedConvex(b)));

        // Splitting a door leaf must leave both halves part of the door, not drop one into the world.
        Assert.Contains(op.CreatedBrushId, door.BrushIds);

        VmapBrush offcut = doc.FindBrush(op.CreatedBrushId)!;
        Assert.True(VmapWinding.TryGetBounds(offcut, out Vector3 mins, out Vector3 maxs));
        Assert.Equal(32f, mins.X, 3);
        Assert.Equal(64f, maxs.X, 3);
    }

    [Fact]
    public void Clipper_WithAPlaneThatMissesTheBrush_IsRefused()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new ClipBrushOp(1, new VmapPlane(new Vector3(1, 0, 0), 512f))));
        Assert.Single(doc.Brushes);
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(64f, maxs.X, 3);   // unchanged
    }

    [Fact]
    public void Delete_RemovesTheBrushAndUnhooksItFromItsEntity()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.BrushIds.Add(1);
        doc.Entities.Add(door);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new DeleteBrushesOp(new[] { 1 })));

        Assert.Empty(doc.Brushes);
        Assert.Empty(door.BrushIds);   // a dangling id would make the collision builder look up a ghost

        Assert.True(session.Undo());
        Assert.Single(doc.Brushes);
    }

    // ---------------------------------------------------------------- selection + session state

    [Fact]
    public void Selection_TogglesAndDeduplicatesBrushIds()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        session.Select(VmapSelection.OfFace(1, 0));
        session.ToggleSelect(VmapSelection.OfFace(1, 2));
        Assert.Equal(2, session.Selection.Count);
        Assert.Single(session.SelectedBrushIds());     // both faces belong to the same brush

        session.ToggleSelect(VmapSelection.OfFace(1, 2));
        Assert.Single(session.Selection);

        session.Select(VmapSelection.None);
        Assert.Empty(session.Selection);
    }

    [Fact]
    public void Session_TracksDirtyStateAndUndoLabels()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var session = new VmapEditSession(doc);

        Assert.False(session.IsDirty);
        Assert.Null(session.UndoLabel);

        Assert.True(session.Apply(new MoveFaceOp(1, 0, 8f)));
        Assert.True(session.IsDirty);
        Assert.Contains("Push face", session.UndoLabel!);
    }

    [Fact]
    public void SelectionCenter_IsTheBoundsCentreOfEverySelectedBrush()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(32, 32, 32), id: 1));
        doc.Brushes.Add(Box(new Vector3(64, 0, 0), new Vector3(96, 32, 32), id: 2));

        Assert.True(VmapEdit.TryGetSelectionCenter(doc, new[] { 1, 2 }, out Vector3 center));
        Assert.Equal(new Vector3(48, 16, 16), center);
    }
}
