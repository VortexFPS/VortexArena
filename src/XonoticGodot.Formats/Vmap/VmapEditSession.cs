using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>What kind of sub-object a selection refers to.</summary>
public enum VmapSelectionKind
{
    None,
    Brush,
    Face,
    Edge,
    Vertex,

    /// <summary>
    /// A whole bezier patch (curved surface / terrain mesh). Patches are NOT plane sets, so face, edge and
    /// vertex modes are meaningless for them — Radiant treats them the same way: you select the OBJECT and
    /// edit its control grid, rather than picking faces off a hull it does not have.
    /// </summary>
    Patch,
}

/// <summary>
/// A reference to something selectable, by STABLE ids rather than list positions: a brush id plus, for a
/// sub-object, the face index and/or the world positions of the vertices involved.
///
/// Positions rather than vertex indices because a brush has no stored vertex list — its corners are derived
/// from the plane set, so any "index" would be an artefact of the derivation order and would silently point
/// at a different corner the moment a neighbouring plane moved.
/// </summary>
public readonly struct VmapSelection
{
    public VmapSelectionKind Kind { get; init; }
    public int BrushId { get; init; }

    /// <summary>Face index within the brush, for <see cref="VmapSelectionKind.Face"/>; -1 otherwise.</summary>
    public int FaceIndex { get; init; }

    /// <summary>Patch id for <see cref="VmapSelectionKind.Patch"/>; 0 otherwise. Kept separate from
    /// <see cref="BrushId"/> because brush and patch ids are independent sequences.</summary>
    public int PatchId { get; init; }

    /// <summary>World positions of the referenced vertices (one for a vertex, two for an edge).</summary>
    public IReadOnlyList<Vector3> Vertices { get; init; }

    public static VmapSelection None => new() { Kind = VmapSelectionKind.None, FaceIndex = -1, Vertices = Array.Empty<Vector3>() };

    public static VmapSelection OfBrush(int brushId) => new()
    { Kind = VmapSelectionKind.Brush, BrushId = brushId, FaceIndex = -1, Vertices = Array.Empty<Vector3>() };

    public static VmapSelection OfFace(int brushId, int faceIndex) => new()
    { Kind = VmapSelectionKind.Face, BrushId = brushId, FaceIndex = faceIndex, Vertices = Array.Empty<Vector3>() };

    public static VmapSelection OfVertex(int brushId, Vector3 vertex) => new()
    { Kind = VmapSelectionKind.Vertex, BrushId = brushId, FaceIndex = -1, Vertices = new[] { vertex } };

    /// <summary>Select a whole bezier patch.</summary>
    public static VmapSelection OfPatch(int patchId) => new()
    { Kind = VmapSelectionKind.Patch, PatchId = patchId, FaceIndex = -1, Vertices = Array.Empty<Vector3>() };

    public static VmapSelection OfEdge(int brushId, Vector3 a, Vector3 b) => new()
    { Kind = VmapSelectionKind.Edge, BrushId = brushId, FaceIndex = -1, Vertices = new[] { a, b } };

    public bool IsEmpty => Kind == VmapSelectionKind.None;
}

/// <summary>
/// An editing session over one document: the op journal with undo/redo, and the current selection.
///
/// Undo is by SNAPSHOT, not by inverse op. A vertex drag re-derives planes through a least-squares fit, so
/// "apply the reverse drag" is not an exact inverse and geometry would drift a little with every undo/redo
/// cycle. Snapshotting the touched brushes instead makes undo exact by construction, and it costs almost
/// nothing because ops declare precisely which brushes they touch.
///
/// The same journal is what a co-editing server replays and what an autosave replays after a crash
/// (design doc §11.7, §11.8) — one mechanism, three uses.
/// </summary>
public sealed class VmapEditSession
{
    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();

    /// <summary>Maximum retained undo steps; older entries are dropped from the bottom.</summary>
    public int UndoLimit { get; set; } = 256;

    public VmapEditSession(VmapDocument document)
        => Document = document ?? throw new ArgumentNullException(nameof(document));

    public VmapDocument Document { get; }

    /// <summary>Currently selected sub-objects. Multi-select is a list; most edits act on all of them.</summary>
    public List<VmapSelection> Selection { get; } = new();

    /// <summary>True when the document has changes not yet written to disk.</summary>
    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Label of the next undo step, for the HUD ("Undo: Push face...").</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>Label of the next redo step.</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>
    /// Apply an op. On success it is journalled and the redo stack is cleared (the standard linear-history
    /// rule: editing after undoing abandons the redone future). On failure the document is untouched and
    /// nothing is journalled — a rejected drag must not leave an empty step in the undo list.
    /// </summary>
    public bool Apply(IVmapOp op)
    {
        ArgumentNullException.ThrowIfNull(op);

        // Snapshot BEFORE applying. An op that creates a brush reports no touched ids yet, so also capture the
        // brush-id set to detect additions.
        var before = Snapshot(op.TouchedBrushIds);
        var patchBefore = SnapshotPatches(op.TouchedPatchIds);
        var entityBefore = SnapshotEntities(op.TouchedEntityIds);

        // An entity that OWNS a touched brush or patch is itself touched, whether or not the op said so. Geometry
        // ops mutate ownership links as a side effect — a clip hands the off-cut to the same brush entity, a
        // delete unhooks the brush from it — and an op cannot name those entities up front because it has no
        // document until Apply. Deriving them here is what stops undo leaving an entity pointing at a brush that
        // no longer exists.
        foreach (VmapEntity owner in Document.Entities)
        {
            if (entityBefore.ContainsKey(owner.Id))
                continue;
            if (owner.BrushIds.Exists(op.TouchedBrushIds.Contains)
                || owner.PatchIds.Exists(op.TouchedPatchIds.Contains))
                entityBefore[owner.Id] = owner.Clone();
        }

        var idsBefore = new HashSet<int>(Document.Brushes.Select(b => b.Id));
        var patchIdsBefore = new HashSet<int>(Document.Patches.Select(p => p.Id));
        var entityIdsBefore = new HashSet<int>(Document.Entities.Select(e => e.Id));

        if (!op.Apply(Document))
            return false;

        // Fold in anything the op created or removed that its declared set did not cover.
        foreach (VmapBrush b in Document.Brushes)
            if (!idsBefore.Contains(b.Id))
                before[b.Id] = null;                       // created: undo removes it
        foreach (int id in idsBefore)
            if (Document.FindBrush(id) is null && !before.ContainsKey(id))
                before[id] = null;                          // removed but unsnapshotted: cannot restore

        foreach (VmapPatch p in Document.Patches)
            if (!patchIdsBefore.Contains(p.Id))
                patchBefore[p.Id] = null;
        foreach (int id in patchIdsBefore)
            if (Document.FindPatch(id) is null && !patchBefore.ContainsKey(id))
                patchBefore[id] = null;

        foreach (VmapEntity e in Document.Entities)
            if (!entityIdsBefore.Contains(e.Id))
                entityBefore[e.Id] = null;
        foreach (int id in entityIdsBefore)
            if (Document.FindEntity(id) is null && !entityBefore.ContainsKey(id))
                entityBefore[id] = null;

        _undo.Add(new Entry(op.Describe(), before, Snapshot(before.Keys.ToList()),
            patchBefore, SnapshotPatches(patchBefore.Keys.ToList()),
            entityBefore, SnapshotEntities(entityBefore.Keys.ToList())));
        if (_undo.Count > UndoLimit)
            _undo.RemoveAt(0);
        _redo.Clear();
        IsDirty = true;
        return true;
    }

    /// <summary>Roll back the most recent op.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        Entry e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(e.Before);
        RestorePatches(e.PatchesBefore);
        RestoreEntities(e.EntitiesBefore);
        _redo.Add(e);
        IsDirty = true;
        return true;
    }

    /// <summary>Re-apply the most recently undone op.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        Entry e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(e.After);
        RestorePatches(e.PatchesAfter);
        RestoreEntities(e.EntitiesAfter);
        _undo.Add(e);
        IsDirty = true;
        return true;
    }

    /// <summary>Write the document out and clear the dirty flag.</summary>
    public void Save(string path, bool zip = false)
    {
        if (zip)
            VmapPackage.WriteToZip(Document, path);
        else
            VmapPackage.WriteToDirectory(Document, path);
        IsDirty = false;
    }

    /// <summary>Brush ids in the current selection, deduplicated.</summary>
    public List<int> SelectedBrushIds()
    {
        var ids = new List<int>();
        foreach (VmapSelection s in Selection)
            if (!s.IsEmpty && !ids.Contains(s.BrushId))
                ids.Add(s.BrushId);
        return ids;
    }

    /// <summary>Replace the selection with a single item (a plain click).</summary>
    public void Select(VmapSelection selection)
    {
        Selection.Clear();
        if (!selection.IsEmpty)
            Selection.Add(selection);
    }

    /// <summary>Add to / remove from the selection (a shift-click).</summary>
    public void ToggleSelect(VmapSelection selection)
    {
        if (selection.IsEmpty)
            return;
        int existing = Selection.FindIndex(s =>
            s.Kind == selection.Kind && s.BrushId == selection.BrushId && s.FaceIndex == selection.FaceIndex);
        if (existing >= 0)
            Selection.RemoveAt(existing);
        else
            Selection.Add(selection);
    }

    // ---- snapshot plumbing -------------------------------------------------------------------

    /// <summary>A journal entry: the label plus the touched brushes and patches before and after the op.</summary>
    private readonly record struct Entry(
        string Label,
        Dictionary<int, VmapBrush?> Before, Dictionary<int, VmapBrush?> After,
        Dictionary<int, VmapPatch?> PatchesBefore, Dictionary<int, VmapPatch?> PatchesAfter,
        Dictionary<int, VmapEntity?> EntitiesBefore, Dictionary<int, VmapEntity?> EntitiesAfter);

    /// <summary>Clone the given brushes; a null value records "did not exist".</summary>
    private Dictionary<int, VmapBrush?> Snapshot(IReadOnlyList<int> ids)
    {
        var map = new Dictionary<int, VmapBrush?>();
        foreach (int id in ids)
            map[id] = Document.FindBrush(id)?.Clone();
        return map;
    }

    /// <summary>Clone the given patches; a null value records "did not exist".</summary>
    private Dictionary<int, VmapPatch?> SnapshotPatches(IReadOnlyList<int> ids)
    {
        var map = new Dictionary<int, VmapPatch?>();
        foreach (int id in ids)
            map[id] = Document.FindPatch(id)?.Clone();
        return map;
    }

    /// <summary>Clone the given entities; a null value records "did not exist".</summary>
    private Dictionary<int, VmapEntity?> SnapshotEntities(IReadOnlyList<int> ids)
    {
        var map = new Dictionary<int, VmapEntity?>();
        foreach (int id in ids)
            map[id] = Document.FindEntity(id)?.Clone();
        return map;
    }

    /// <summary>
    /// Restore an entity snapshot. Without this, undoing a paste that brought a brush entity along removes the
    /// geometry and leaves the entity behind, owning nothing.
    /// </summary>
    private void RestoreEntities(Dictionary<int, VmapEntity?> snapshot)
    {
        foreach ((int id, VmapEntity? saved) in snapshot)
        {
            VmapEntity? live = Document.FindEntity(id);
            if (saved is null)
            {
                if (live is not null)
                    Document.Entities.Remove(live);
                continue;
            }

            if (live is null)
            {
                Document.Entities.Add(saved.Clone());
                continue;
            }

            live.ClassName = saved.ClassName;
            live.Fields.Clear();
            foreach (KeyValuePair<string, string> kv in saved.Fields)
                live.Fields[kv.Key] = kv.Value;
            live.BrushIds.Clear();
            live.BrushIds.AddRange(saved.BrushIds);
            live.PatchIds.Clear();
            live.PatchIds.AddRange(saved.PatchIds);
        }
    }

    /// <summary>Restore a patch snapshot, mirroring <see cref="Restore"/>'s replace / re-add / remove cases.</summary>
    private void RestorePatches(Dictionary<int, VmapPatch?> snapshot)
    {
        foreach ((int id, VmapPatch? saved) in snapshot)
        {
            VmapPatch? live = Document.FindPatch(id);
            if (saved is null)
            {
                if (live is not null)
                    Document.Patches.Remove(live);
                continue;
            }

            if (live is null)
            {
                Document.Patches.Add(saved.Clone());
                continue;
            }

            // Mutate in place rather than swapping the list entry: the pick index and any other cache keyed on
            // the patch OBJECT would otherwise keep pointing at the replaced instance.
            live.Material = saved.Material;
            live.Width = saved.Width;
            live.Height = saved.Height;
            live.SurfaceFlags = saved.SurfaceFlags;
            live.ContentFlags = saved.ContentFlags;
            live.Controls.Clear();
            live.Controls.AddRange(saved.Controls);
            live.ControlUvs.Clear();
            live.ControlUvs.AddRange(saved.ControlUvs);
        }
    }

    /// <summary>Restore a snapshot: replace present brushes, re-add missing ones, remove ones that should not exist.</summary>
    private void Restore(Dictionary<int, VmapBrush?> snapshot)
    {
        foreach ((int id, VmapBrush? saved) in snapshot)
        {
            VmapBrush? live = Document.FindBrush(id);
            if (saved is null)
            {
                if (live is not null)
                    Document.Brushes.Remove(live);
                continue;
            }

            if (live is null)
            {
                Document.Brushes.Add(saved.Clone());
                continue;
            }

            live.Faces.Clear();
            foreach (VmapFace f in saved.Clone().Faces)
                live.Faces.Add(f);
            live.IsDetail = saved.IsDetail;
            live.ContentFlags = saved.ContentFlags;
            // Restore the CLASSIFICATION too, not just the geometry. These two decide which gametype the brush
            // belongs to and whether it is pickable at all, so leaving them out makes undo a lossy operation
            // for anything an op reclassified.
            live.SubmodelIndex = saved.SubmodelIndex;
            live.IsToolBrush = saved.IsToolBrush;
        }
    }
}
