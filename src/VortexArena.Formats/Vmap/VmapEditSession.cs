using System.Numerics;

namespace VortexArena.Formats.Vmap;

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

    /// <summary>
    /// A map entity. A POINT entity is its origin key plus a descriptor box; a BRUSH entity is the geometry it
    /// owns, so selecting one selects the solid rather than a bounding volume that does not exist.
    /// </summary>
    Entity,
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

    /// <summary>Entity id for <see cref="VmapSelectionKind.Entity"/>; 0 otherwise. A third independent id
    /// sequence, for the same reason patches have their own.</summary>
    public int EntityId { get; init; }

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

    /// <summary>Select a whole map entity.</summary>
    public static VmapSelection OfEntity(int entityId) => new()
    { Kind = VmapSelectionKind.Entity, EntityId = entityId, FaceIndex = -1, Vertices = Array.Empty<Vector3>() };

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
        var blendBefore = SnapshotBlend(op.TouchedBlendRegions);

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
            entityBefore, SnapshotEntities(entityBefore.Keys.ToList()),
            blendBefore, SnapshotBlend(op.TouchedBlendRegions)));
        if (_undo.Count > UndoLimit)
            _undo.RemoveAt(0);

        // Editing after undoing normally DISCARDS the redone future. Filing it as a branch instead costs the
        // memory that was already allocated and removes the whole class of "I undid four steps, touched one
        // thing, and lost the rest" — which the design doc calls out as strictly better than warning about it
        // (§11.9), because a warning still ends with the work gone.
        if (_redo.Count > 0)
        {
            _branches.Add(new Branch(_redo[^1].Label, _redo.ToList()));
            if (_branches.Count > BranchLimit)
                _branches.RemoveAt(0);
            _redo.Clear();
        }

        IsDirty = true;
        Applied?.Invoke(op);
        return true;
    }

    /// <summary>
    /// Raised after an op has successfully changed the document (phase E6).
    ///
    /// One choke point for replication. Every tool in the editor goes through <see cref="Apply"/>, so a host
    /// that broadcasts from here cannot forget to replicate a gesture the way it could if each of the twenty
    /// call sites had to remember. Fires AFTER the apply, so a create's assigned id is already in the op.
    /// </summary>
    public event Action<IVmapOp>? Applied;

    // =============================================================================================
    //  History (design doc §11.9) — the journal as something you can look at and travel through
    // =============================================================================================

    /// <summary>How many abandoned branches to keep before the oldest is dropped.</summary>
    public const int BranchLimit = 8;

    /// <summary>An abandoned redo stack, kept so an undo-then-edit does not destroy the work it skipped.</summary>
    private readonly record struct Branch(string Label, List<Entry> Entries);

    private readonly List<Branch> _branches = new();

    /// <summary>One row of the history list.</summary>
    /// <param name="Label">What the step did.</param>
    /// <param name="IsCurrent">True for the step the document is currently sitting at.</param>
    /// <param name="IsUndone">True for steps that have been undone and could be redone.</param>
    public readonly record struct HistoryStep(string Label, bool IsCurrent, bool IsUndone);

    /// <summary>
    /// The journal as a list, oldest first: applied steps then undone ones. Index 0 is the oldest APPLIED step,
    /// so travelling to index i means "leave i+1 steps applied".
    /// </summary>
    public IReadOnlyList<HistoryStep> History()
    {
        var rows = new List<HistoryStep>(_undo.Count + _redo.Count);
        for (int i = 0; i < _undo.Count; i++)
            rows.Add(new HistoryStep(_undo[i].Label, i == _undo.Count - 1, false));

        // _redo is a stack: its LAST element is the next step to redo, so it reads oldest-first reversed.
        for (int i = _redo.Count - 1; i >= 0; i--)
            rows.Add(new HistoryStep(_redo[i].Label, false, true));
        return rows;
    }

    /// <summary>How many steps are currently applied. 0 means the document is at its opened state.</summary>
    public int HistoryPosition => _undo.Count;

    /// <summary>Total steps known, applied plus undone.</summary>
    public int HistoryLength => _undo.Count + _redo.Count;

    /// <summary>
    /// Travel to a point in the history: leave exactly <paramref name="appliedSteps"/> steps applied, undoing
    /// or redoing as needed. Returns true when the position actually changed.
    ///
    /// Walks one step at a time through the same Undo/Redo path rather than trying to compose a jump. Each
    /// entry's snapshot describes one transition, so composing would mean merging snapshots — and a merge that
    /// is subtly wrong produces geometry that never existed, which is far worse than a loop that is O(steps).
    /// </summary>
    public bool TravelTo(int appliedSteps)
    {
        int target = Math.Clamp(appliedSteps, 0, HistoryLength);
        if (target == _undo.Count)
            return false;

        while (_undo.Count > target && Undo()) { }
        while (_undo.Count < target && Redo()) { }
        return true;
    }

    /// <summary>Abandoned branches, newest first, for the history dialog to offer.</summary>
    public IReadOnlyList<string> Branches()
    {
        var names = new List<string>(_branches.Count);
        for (int i = _branches.Count - 1; i >= 0; i--)
            names.Add($"{_branches[i].Entries.Count} step(s) ending in \"{_branches[i].Label}\"");
        return names;
    }

    /// <summary>
    /// Put an abandoned branch back on the redo stack so it can be replayed. Indexed the way
    /// <see cref="Branches"/> lists them, newest first.
    ///
    /// Only restores the ABILITY to redo; it does not replay anything. The branch was abandoned from some
    /// earlier point in the history, so replaying blind could apply it on top of a document it was never
    /// authored against — the mapper travels back first, then redoes.
    /// </summary>
    public bool RestoreBranch(int index)
    {
        if (index < 0 || index >= _branches.Count)
            return false;

        Branch b = _branches[_branches.Count - 1 - index];
        _branches.RemoveAt(_branches.Count - 1 - index);
        _redo.Clear();
        _redo.AddRange(b.Entries);
        return true;
    }

    /// <summary>
    /// Raised after an undo, redo or history jump, with the brush, patch and entity ids whose state was put
    /// back (phase E6).
    ///
    /// Undo does not replay an op — it restores a snapshot — so it has nothing an op wire could carry. What
    /// replicates instead is the RESULT: these objects now look like this. Without this hook an undo on one
    /// machine is invisible on every other one, and a co-editing session diverges the first time anyone
    /// presses Ctrl+Z, which in a map editor is immediately.
    /// </summary>
    public event Action<IReadOnlyList<int>, IReadOnlyList<int>, IReadOnlyList<int>,
        IReadOnlyList<VmapBlendRegion>>? Restored;

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
        RestoreBlend(e.BlendBefore);
        _redo.Add(e);
        IsDirty = true;
        RaiseRestored(e);
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
        RestoreBlend(e.BlendAfter);
        _undo.Add(e);
        IsDirty = true;
        RaiseRestored(e);
        return true;
    }

    /// <summary>The journal entry names every id it snapshotted, which is exactly the set a restore rewrote.</summary>
    private void RaiseRestored(Entry e)
    {
        if (Restored is null)
            return;
        var blend = new List<VmapBlendRegion>(e.BlendBefore.Count);
        foreach (BlendPatch b in e.BlendBefore)
            if (b.W > 0 && b.H > 0)
                blend.Add(new VmapBlendRegion(b.MapId, b.X, b.Y, b.W, b.H));

        Restored(e.Before.Keys.ToList(), e.PatchesBefore.Keys.ToList(), e.EntitiesBefore.Keys.ToList(), blend);
    }

    /// <summary>Write the document out and clear the dirty flag.</summary>
    public void Save(string path)
    {
        VmapPackage.Write(Document, path);
        IsDirty = false;
    }

    /// <summary>
    /// Brush ids in the current selection, deduplicated.
    ///
    /// Only the kinds that actually reference a brush are read. A patch or entity selection leaves
    /// <see cref="VmapSelection.BrushId"/> at zero — they carry their own id fields, because the three are
    /// independent sequences — and taking it anyway puts a brush id of 0 into a list that every caller treats
    /// as real geometry. Ops respond to that differently and all of them badly: a snap refuses outright
    /// because one of "its" brushes cannot be found, while a translate quietly works on fewer objects than
    /// were selected.
    /// </summary>
    public List<int> SelectedBrushIds()
    {
        var ids = new List<int>();
        foreach (VmapSelection s in Selection)
        {
            if (s.Kind is not (VmapSelectionKind.Brush or VmapSelectionKind.Face
                               or VmapSelectionKind.Vertex or VmapSelectionKind.Edge))
                continue;
            if (s.BrushId != 0 && !ids.Contains(s.BrushId))
                ids.Add(s.BrushId);
        }
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
        int existing = Selection.FindIndex(s => SameTarget(s, selection));
        if (existing >= 0)
            Selection.RemoveAt(existing);
        else
            Selection.Add(selection);
    }

    /// <summary>
    /// Whether two selections point at the same thing — what shift-click uses to decide add versus remove.
    ///
    /// Every distinguishing field has to be compared, and which ones those are depends on the kind. Comparing
    /// only kind/brush/face reads two patches as identical (both leave BrushId at zero and FaceIndex at -1),
    /// so shift-clicking a second patch DESELECTS the first instead of adding it. The same goes for two
    /// entities, and for two vertices of one brush, which differ only in the position they carry.
    /// </summary>
    private static bool SameTarget(VmapSelection a, VmapSelection b)
    {
        if (a.Kind != b.Kind)
            return false;

        return a.Kind switch
        {
            VmapSelectionKind.Patch => a.PatchId == b.PatchId,
            VmapSelectionKind.Entity => a.EntityId == b.EntityId,
            VmapSelectionKind.Vertex or VmapSelectionKind.Edge =>
                a.BrushId == b.BrushId && SameVertices(a.Vertices, b.Vertices),
            _ => a.BrushId == b.BrushId && a.FaceIndex == b.FaceIndex,
        };
    }

    /// <summary>
    /// Positional identity for vertex and edge selections. Compared with a tolerance rather than exactly: a
    /// vertex position is re-derived from plane intersections each time it is picked, so the same corner can
    /// come back a few ulps apart between one click and the next.
    /// </summary>
    private static bool SameVertices(IReadOnlyList<Vector3> a, IReadOnlyList<Vector3> b)
    {
        if (a is null || b is null || a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if ((a[i] - b[i]).LengthSquared() > 1e-6f)
                return false;
        return true;
    }

    // ---- snapshot plumbing -------------------------------------------------------------------

    /// <summary>A journal entry: the label plus the touched brushes and patches before and after the op.</summary>
    private readonly record struct Entry(
        string Label,
        Dictionary<int, VmapBrush?> Before, Dictionary<int, VmapBrush?> After,
        Dictionary<int, VmapPatch?> PatchesBefore, Dictionary<int, VmapPatch?> PatchesAfter,
        Dictionary<int, VmapEntity?> EntitiesBefore, Dictionary<int, VmapEntity?> EntitiesAfter,
        List<BlendPatch> BlendBefore, List<BlendPatch> BlendAfter);

    /// <summary>
    /// A rectangle of one blend map's texels, before or after an op (backlog F2).
    ///
    /// A RECTANGLE rather than the whole map, and that is not a nicety: the journal keeps 256 entries with a
    /// before and an after, so whole-map snapshots of one painted wall would cost 128 MB.
    /// </summary>
    private readonly record struct BlendPatch(int MapId, int X, int Y, int W, int H, byte[]? Texels);

    /// <summary>Copy the declared rectangles out of the live maps; a null block records "did not exist".</summary>
    private List<BlendPatch> SnapshotBlend(IReadOnlyList<VmapBlendRegion> regions)
    {
        var patches = new List<BlendPatch>(regions.Count);
        foreach (VmapBlendRegion r in regions)
        {
            if (Document.FindBlendMap(r.BlendMapId) is not { } map || !map.IsValid)
            {
                patches.Add(new BlendPatch(r.BlendMapId, 0, 0, 0, 0, null));
                continue;
            }
            VmapBlendRegion c = SetBlendRegionOp.Clamp(map, r);
            patches.Add(c.Width <= 0 || c.Height <= 0
                ? new BlendPatch(r.BlendMapId, 0, 0, 0, 0, null)
                : new BlendPatch(r.BlendMapId, c.X, c.Y, c.Width, c.Height,
                    map.CopyRegion(c.X, c.Y, c.Width, c.Height)));
        }
        return patches;
    }

    /// <summary>Put snapshotted rectangles back.</summary>
    private void RestoreBlend(List<BlendPatch> patches)
    {
        foreach (BlendPatch b in patches)
        {
            if (b.Texels is null || b.W <= 0 || b.H <= 0)
                continue;
            Document.FindBlendMap(b.MapId)?.PasteRegion(b.X, b.Y, b.W, b.H, b.Texels);
        }
    }

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
            live.GroupId = saved.GroupId;
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
            live.GroupId = saved.GroupId;
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
            live.GroupId = saved.GroupId;
        }
    }
}
