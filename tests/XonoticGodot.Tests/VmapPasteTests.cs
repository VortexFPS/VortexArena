using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers the clipboard and <see cref="PasteOp"/> (phase E8).
///
/// The case worth the most attention is entity REMAPPING. A copied brush entity carries the brush ids it owned
/// in the source document, and pasting those verbatim leaves the new entity owning the ORIGINAL brushes — so
/// moving the pasted door moves the one it was copied from, and deleting one deletes the other's geometry.
/// Nothing about that is visible until a mapper hits it, which is exactly the kind of corruption a test should
/// be holding down.
/// </summary>
public class VmapPasteTests
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

    private static VmapPatch FlatPatch(int id = 1, float half = 32f)
    {
        var p = new VmapPatch { Id = id, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(new Vector3((col - 1) * half, (row - 1) * half, 0f));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        return p;
    }

    // ---------------------------------------------------------------- clipboard

    [Fact]
    public void CopyingABrush_CapturesItAndItsCentre()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-16, -16, -16), new Vector3(16, 16, 16)));
        var clip = new VmapClipboard();

        Assert.Equal(1, clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) }));
        Assert.Equal(Vector3.Zero, clip.Pivot);
        Assert.Equal("Brush #1", clip.Describe());
    }

    /// <summary>
    /// The clipboard holds CLONES. Copying then deleting the source has to leave the clipboard intact, because
    /// cut-and-paste is exactly that sequence and it must not paste a hole.
    /// </summary>
    [Fact]
    public void ClipboardSurvivesDeletingWhatWasCopied()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(32, 32, 32)));
        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();

        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });
        Assert.True(session.Apply(new DeleteBrushesOp(new[] { 1 })));
        Assert.Empty(doc.Brushes);

        Assert.True(session.Apply(new PasteOp(clip, new Vector3(256, 0, 0))));
        Assert.Single(doc.Brushes);
    }

    [Fact]
    public void CopyingAFace_TakesItsWholeBrush()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-8, -8, -8), new Vector3(8, 8, 8)));
        var clip = new VmapClipboard();

        // There is no free-floating face in a plane-set model, so copying one can only mean its solid.
        Assert.Equal(1, clip.CopyFrom(doc, new[] { VmapSelection.OfFace(1, 0) }));
        Assert.Single(clip.Brushes);
    }

    [Fact]
    public void CopyingNothing_LeavesThePreviousClipboardAlone()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(16, 16, 16)));
        var clip = new VmapClipboard();

        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });
        Assert.Equal(0, clip.CopyFrom(doc, System.Array.Empty<VmapSelection>()));
        Assert.False(clip.IsEmpty);
    }

    // ---------------------------------------------------------------- placement

    [Fact]
    public void PasteLandsThePivotWhereItWasAsked()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-16, -16, -16), new Vector3(16, 16, 16)));
        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });

        Assert.True(session.Apply(new PasteOp(clip, new Vector3(512, 128, 64))));

        Assert.Equal(2, doc.Brushes.Count);
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[1], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(496, 112, 48), mins);
        Assert.Equal(new Vector3(528, 144, 80), maxs);
    }

    [Fact]
    public void PastedBrushGetsAFreshId_NotTheSourceId()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(16, 16, 16), id: 7));
        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(7) });

        var op = new PasteOp(clip, new Vector3(128, 0, 0));
        Assert.True(session.Apply(op));

        Assert.Single(op.CreatedBrushIds);
        Assert.NotEqual(7, op.CreatedBrushIds[0]);
        Assert.Equal(2, doc.Brushes.Count);
    }

    [Fact]
    public void PastingTwice_ProducesTwoIndependentCopies()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(16, 16, 16)));
        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });

        Assert.True(session.Apply(new PasteOp(clip, new Vector3(128, 0, 0))));
        Assert.True(session.Apply(new PasteOp(clip, new Vector3(256, 0, 0))));

        Assert.Equal(3, doc.Brushes.Count);
        Assert.Equal(3, new HashSet<int>(doc.Brushes.Select(b => b.Id)).Count);
    }

    [Fact]
    public void PastedTextureAlignmentTravelsWithTheGeometry()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 64, 64)));
        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });

        var offset = new Vector3(128, 0, 0);
        Assert.True(session.Apply(new PasteOp(clip, clip.Pivot + offset)));

        // A point on the source and the SAME point on the copy must sample the same texel, or the pasted wall
        // comes out with its texture slid across it.
        VmapFace src = doc.Brushes[0].Faces[4];   // +Z
        VmapFace dst = doc.Brushes[1].Faces[4];
        var probe = new Vector3(16, 16, 64);
        Assert.Equal(src.Projection.Evaluate(probe).X, dst.Projection.Evaluate(probe + offset).X, 3);
        Assert.Equal(src.Projection.Evaluate(probe).Y, dst.Projection.Evaluate(probe + offset).Y, 3);
    }

    [Fact]
    public void PastedPatchControlPointsMove()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfPatch(1) });

        Assert.True(session.Apply(new PasteOp(clip, new Vector3(0, 0, 256))));

        Assert.Equal(2, doc.Patches.Count);
        Assert.Equal(new Vector3(32, 32, 256), doc.Patches[1].Controls[8]);
    }

    // ---------------------------------------------------------------- entity remapping

    /// <summary>
    /// The one that matters. A pasted brush entity must own the brushes the paste created, never the ones it
    /// was copied from.
    /// </summary>
    [Fact]
    public void PastedBrushEntity_OwnsTheNewBrushes_NotTheOriginals()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 16, 96), id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();

        // Copying the door's brush must bring the door along — geometry without its behaviour is not the thing
        // that was copied.
        Assert.Equal(2, clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) }));
        Assert.Single(clip.Entities);

        var op = new PasteOp(clip, new Vector3(512, 0, 0));
        Assert.True(session.Apply(op));

        VmapEntity pasted = doc.Entities[^1];
        Assert.Equal("func_door", pasted.ClassName);
        Assert.Single(pasted.BrushIds);
        Assert.Equal(op.CreatedBrushIds[0], pasted.BrushIds[0]);
        Assert.NotEqual(1, pasted.BrushIds[0]);

        // And the original is untouched.
        Assert.Single(doc.Entities[0].BrushIds);
        Assert.Equal(1, doc.Entities[0].BrushIds[0]);
    }

    [Fact]
    public void PastedPointEntity_GetsTheOffsetOrigin()
    {
        var doc = new VmapDocument();
        var weapon = new VmapEntity { Id = 1, ClassName = "weapon_devastator" };
        weapon.Fields["classname"] = "weapon_devastator";
        weapon.SetOrigin(new Vector3(10, 20, 30));
        doc.Entities.Add(weapon);

        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        Assert.Equal(1, clip.CopyEntities(new[] { weapon }));

        // Pivot is the entity's own origin, so pasting at P puts it exactly at P.
        Assert.True(session.Apply(new PasteOp(clip, new Vector3(100, 200, 300))));

        Assert.Equal(2, doc.Entities.Count);
        Assert.Equal(new Vector3(100, 200, 300), doc.Entities[1].Origin());
        Assert.Equal(new Vector3(10, 20, 30), doc.Entities[0].Origin());
    }

    /// <summary>
    /// An id with no mapping belonged to something that was not copied. Keeping it would leave the pasted
    /// entity owning a stranger's brush; the reference is dropped instead.
    /// </summary>
    [Fact]
    public void UnmappedOwnershipReferences_AreDropped_NotLeftDangling()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(16, 16, 16), id: 1));
        doc.Brushes.Add(Box(new Vector3(64, 0, 0), new Vector3(80, 16, 16), id: 2));
        var ent = new VmapEntity { Id = 1, ClassName = "func_group" };
        ent.BrushIds.Add(1);
        ent.BrushIds.Add(2);
        doc.Entities.Add(ent);

        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();

        // Copy only brush 1; the entity comes along but its reference to brush 2 has nowhere to point.
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });
        var op = new PasteOp(clip, new Vector3(0, 256, 0));
        Assert.True(session.Apply(op));

        VmapEntity pasted = doc.Entities[^1];
        Assert.Single(pasted.BrushIds);
        Assert.Equal(op.CreatedBrushIds[0], pasted.BrushIds[0]);
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public void UndoingAPaste_RemovesEverythingItAdded()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(16, 16, 16)));
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1), VmapSelection.OfPatch(1) });

        Assert.True(session.Apply(new PasteOp(clip, new Vector3(400, 0, 0))));
        Assert.Equal(2, doc.Brushes.Count);
        Assert.Equal(2, doc.Patches.Count);

        Assert.True(session.Undo());
        Assert.Single(doc.Brushes);
        Assert.Single(doc.Patches);

        Assert.True(session.Redo());
        Assert.Equal(2, doc.Brushes.Count);
        Assert.Equal(2, doc.Patches.Count);
    }

    /// <summary>
    /// The entity half of the same story: before E8 the journal tracked brushes and patches only, so undoing a
    /// paste stripped the geometry and left the entity behind owning nothing.
    /// </summary>
    [Fact]
    public void UndoingAPaste_AlsoRemovesThePastedEntity()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 16, 96), id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        var session = new VmapEditSession(doc);
        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });

        Assert.True(session.Apply(new PasteOp(clip, new Vector3(512, 0, 0))));
        Assert.Equal(2, doc.Entities.Count);

        Assert.True(session.Undo());
        Assert.Single(doc.Entities);
        Assert.Single(doc.Brushes);
    }

    /// <summary>
    /// The op snapshots the clipboard when it is CONSTRUCTED, so a redo replaces what it originally placed
    /// rather than whatever happens to be on the clipboard by then.
    /// </summary>
    [Fact]
    public void RedoPastesWhatWasOriginallyPasted_NotTheLatestClipboard()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(16, 16, 16), id: 1));
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(128, 128, 128), id: 2));
        var session = new VmapEditSession(doc);

        var clip = new VmapClipboard();
        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(1) });     // the small one
        var op = new PasteOp(clip, new Vector3(400, 0, 0));
        Assert.True(session.Apply(op));
        Assert.True(session.Undo());

        clip.CopyFrom(doc, new[] { VmapSelection.OfBrush(2) });     // clipboard now holds the big one
        Assert.True(session.Redo());

        // The redone paste must still be the 32-unit box.
        VmapBrush redone = doc.Brushes[^1];
        Assert.True(VmapWinding.TryGetBounds(redone, out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(16, 16, 16), maxs - mins);
    }

    [Fact]
    public void PastingAnEmptyClipboard_IsRefused()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new PasteOp(new VmapClipboard(), Vector3.Zero)));
        Assert.False(session.CanUndo);
    }
}

/// <summary>Covers <see cref="RotateSelectionOp"/> (phase E8) — the patch half of the rotate vocabulary.</summary>
public class VmapRotateSelectionTests
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

    private static VmapPatch FlatPatch(int id = 1, float half = 32f)
    {
        var p = new VmapPatch { Id = id, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(new Vector3((col - 1) * half, (row - 1) * half, 0f));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        return p;
    }

    private static readonly Vector3 Zaxis = new(0, 0, 1);

    [Fact]
    public void RotatingAPatch_TurnsItsControlPoints()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateSelectionOp(
            System.Array.Empty<int>(), new[] { 1 }, Vector3.Zero, Zaxis, 90f)));

        // (32, 32, 0) rotated 90 degrees about +Z lands at (-32, 32, 0).
        Vector3 c = doc.Patches[0].Controls[8];
        Assert.Equal(-32f, c.X, 3);
        Assert.Equal(32f, c.Y, 3);
        Assert.Equal(0f, c.Z, 3);
    }

    [Fact]
    public void UndoingAPatchRotate_PutsItBack()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateSelectionOp(
            System.Array.Empty<int>(), new[] { 1 }, Vector3.Zero, Zaxis, 45f)));
        Assert.True(session.Undo());

        Assert.Equal(new Vector3(32, 32, 0), doc.Patches[0].Controls[8]);
    }

    [Fact]
    public void MixedSelection_TurnsTogether_AsOneUndoStep()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, -8, -8), new Vector3(64, 8, 8)));
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateSelectionOp(
            new[] { 1 }, new[] { 1 }, Vector3.Zero, Zaxis, 90f)));

        // The long brush ran along +X; after a quarter turn about Z it runs along +Y.
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(64f, maxs.Y - mins.Y, 2);
        Assert.Equal(16f, maxs.X - mins.X, 2);

        // ONE step, not two.
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal(new Vector3(32, 32, 0), doc.Patches[0].Controls[8]);
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 backMins, out Vector3 backMaxs));
        Assert.Equal(64f, backMaxs.X - backMins.X, 2);
    }

    [Fact]
    public void AMissingPatchId_RefusesBeforeTurningTheBrushes()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, -8, -8), new Vector3(64, 8, 8)));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new RotateSelectionOp(
            new[] { 1 }, new[] { 99 }, Vector3.Zero, Zaxis, 90f)));

        // The brush must be exactly where it started.
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(64f, maxs.X - mins.X, 2);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ZeroAngle_IsRefused()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch());
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new RotateSelectionOp(
            System.Array.Empty<int>(), new[] { 1 }, Vector3.Zero, Zaxis, 0f)));
    }
}
