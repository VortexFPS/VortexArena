using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The selection list itself (design doc §11.9): what "the selected brushes" means when the selection also
/// holds patches and entities, and what shift-click does when two selections differ in a field the comparison
/// forgot to look at.
///
/// <see cref="VmapSelection"/> keeps brush, patch and entity ids in SEPARATE fields, because the three are
/// independent id sequences. Anything that reads one field for every kind of selection is reading zero most
/// of the time, and zero is a brush id that no document contains.
/// </summary>
public class VmapSelectionTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id)
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

    private static VmapPatch FlatPatch(int id)
    {
        var p = new VmapPatch { Id = id, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(new Vector3((col - 1) * 32f, (row - 1) * 32f, 0f));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        return p;
    }

    [Fact]
    public void SelectedBrushIds_IgnoresPatchAndEntitySelections()
    {
        // A patch or entity selection leaves BrushId at its default. Returning it anyway puts brush id 0 into
        // a list that seventeen call sites treat as real geometry.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        doc.Patches.Add(FlatPatch(id: 1));

        var session = new VmapEditSession(doc);
        session.Selection.Add(VmapSelection.OfBrush(1));
        session.Selection.Add(VmapSelection.OfPatch(1));
        session.Selection.Add(VmapSelection.OfEntity(1));

        Assert.Equal(new[] { 1 }, session.SelectedBrushIds());
    }

    [Fact]
    public void SnapToGrid_StillWorksWhenAPatchIsAlsoSelected()
    {
        // The concrete cost of the bug above: SnapBrushToGridOp refuses outright when an id in its list has no
        // brush behind it, so a phantom 0 makes "snap to grid" silently do nothing.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(1.5f, 2.5f, 3.5f), new Vector3(63.5f, 62.5f, 61.5f), id: 1));
        doc.Patches.Add(FlatPatch(id: 1));

        var session = new VmapEditSession(doc);
        session.Selection.Add(VmapSelection.OfBrush(1));
        session.Selection.Add(VmapSelection.OfPatch(1));

        Assert.True(session.Apply(new SnapBrushToGridOp(session.SelectedBrushIds(), 16f)));
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out _));
        Assert.Equal(0f, mins.X, 3);
    }

    [Fact]
    public void AnEntityOnlySelection_TakesTheEntityMovePath()
    {
        // The reported "entities won't move" bug, from the other end. EditorController routes an entity-only
        // selection to MoveEntitiesOp, gated on `SelectedBrushIds().Count == 0`. While that returned a phantom
        // 0 for an entity selection the gate never opened, the drag fell through to a switch with no Entity
        // case, and the entity sat still with nothing logged.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        var spawn = new VmapEntity { Id = 5, ClassName = "info_player_deathmatch" };
        spawn.Fields["classname"] = "info_player_deathmatch";
        spawn.SetOrigin(new Vector3(100, 0, 24));
        doc.Entities.Add(spawn);

        var session = new VmapEditSession(doc);
        session.Select(VmapSelection.OfEntity(5));

        Assert.Empty(session.SelectedBrushIds());   // the gate the controller reads

        Assert.True(session.Apply(new MoveEntitiesOp(new[] { 5 }, new Vector3(0, 0, 64f), doc)));
        Assert.Equal(new Vector3(100, 0, 88), doc.Entities[0].Origin());
    }

    [Fact]
    public void DeletingAPatch_RemovesItAndUnhooksItsOwner()
    {
        // Patches are their own id space and their own list, so neither the brush delete nor the entity delete
        // can reach them. Without an op of their own a mapper can create a cylinder and never remove it.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        doc.Patches.Add(FlatPatch(id: 4));
        var owner = new VmapEntity { Id = 1, ClassName = "func_group" };
        owner.Fields["classname"] = "func_group";
        owner.BrushIds.Add(1);
        owner.PatchIds.Add(4);
        doc.Entities.Add(owner);

        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new DeletePatchesOp(new[] { 4 })));

        Assert.Empty(doc.Patches);
        Assert.Empty(doc.Entities[0].PatchIds);            // no dangling reference left to write out on save
        Assert.Equal(new[] { 1 }, doc.Entities[0].BrushIds);

        // And it is undoable, ownership link included.
        Assert.True(session.Undo());
        Assert.Single(doc.Patches);
        Assert.Equal(new[] { 4 }, doc.Entities[0].PatchIds);
    }

    [Fact]
    public void ShiftClickingASecondPatch_AddsItRatherThanRemovingTheFirst()
    {
        // Two patch selections agree on Kind, BrushId (0) and FaceIndex (-1) and differ only in PatchId. A
        // comparison that does not look at PatchId reads them as the same object, so shift-clicking a second
        // patch deselects the first.
        var session = new VmapEditSession(new VmapDocument());

        session.ToggleSelect(VmapSelection.OfPatch(3));
        session.ToggleSelect(VmapSelection.OfPatch(7));

        Assert.Equal(2, session.Selection.Count);
        Assert.Contains(session.Selection, s => s.PatchId == 3);
        Assert.Contains(session.Selection, s => s.PatchId == 7);

        // And toggling one of them off still removes the right one.
        session.ToggleSelect(VmapSelection.OfPatch(3));
        Assert.Single(session.Selection);
        Assert.Equal(7, session.Selection[0].PatchId);
    }

    [Fact]
    public void ShiftClickingASecondEntity_AddsItRatherThanRemovingTheFirst()
    {
        var session = new VmapEditSession(new VmapDocument());

        session.ToggleSelect(VmapSelection.OfEntity(11));
        session.ToggleSelect(VmapSelection.OfEntity(12));

        Assert.Equal(2, session.Selection.Count);
        Assert.Contains(session.Selection, s => s.EntityId == 11);
        Assert.Contains(session.Selection, s => s.EntityId == 12);
    }

    [Fact]
    public void ShiftClickingTwoVerticesOfOneBrush_KeepsBoth()
    {
        // Vertex selections of the same brush agree on every scalar field and differ only in the position they
        // carry — which is the entire point of selecting two of them.
        var session = new VmapEditSession(new VmapDocument());

        session.ToggleSelect(VmapSelection.OfVertex(1, new Vector3(0, 0, 0)));
        session.ToggleSelect(VmapSelection.OfVertex(1, new Vector3(64, 0, 0)));

        Assert.Equal(2, session.Selection.Count);

        // Clicking the first one again removes exactly it.
        session.ToggleSelect(VmapSelection.OfVertex(1, new Vector3(0, 0, 0)));
        Assert.Single(session.Selection);
        Assert.Equal(new Vector3(64, 0, 0), session.Selection[0].Vertices[0]);
    }

    [Fact]
    public void ShiftClickingTwoFacesOfOneBrush_KeepsBoth()
    {
        // The case that already worked, pinned so the fix for the others does not break it.
        var session = new VmapEditSession(new VmapDocument());

        session.ToggleSelect(VmapSelection.OfFace(1, 0));
        session.ToggleSelect(VmapSelection.OfFace(1, 3));
        Assert.Equal(2, session.Selection.Count);

        session.ToggleSelect(VmapSelection.OfFace(1, 0));
        Assert.Single(session.Selection);
        Assert.Equal(3, session.Selection[0].FaceIndex);
    }
}
