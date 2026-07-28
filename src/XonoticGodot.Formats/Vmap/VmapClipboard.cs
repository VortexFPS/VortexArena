using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// The editor clipboard (design doc §11.9): a detached deep copy of whatever was selected, plus the pivot the
/// paste ghost is positioned against.
///
/// DETACHED is the load-bearing word. It holds clones, not references into the document, for two reasons that
/// both bite in practice: the source geometry can be deleted (or undone away) between copy and paste, and the
/// clipboard deliberately outlives the session so a light rig can be lifted out of one map and dropped into
/// another. Holding live objects would give you a paste that silently reproduces later edits to the original,
/// or a null reference.
///
/// Ids are NOT preserved. A paste mints fresh ids in the destination document, so copy/paste within one map
/// produces a genuine second object rather than a second reference to the first.
/// </summary>
public sealed class VmapClipboard
{
    private readonly List<VmapBrush> _brushes = new();
    private readonly List<VmapPatch> _patches = new();
    private readonly List<VmapEntity> _entities = new();

    /// <summary>Brushes on the clipboard (clones; their <see cref="VmapBrush.Id"/>s are the SOURCE ids).</summary>
    public IReadOnlyList<VmapBrush> Brushes => _brushes;

    /// <summary>Patches on the clipboard (clones).</summary>
    public IReadOnlyList<VmapPatch> Patches => _patches;

    /// <summary>Entities on the clipboard (clones).</summary>
    public IReadOnlyList<VmapEntity> Entities => _entities;

    /// <summary>
    /// Centre of the copied content's bounds, in Quake units. The paste ghost is drawn with this point under
    /// the crosshair, so a copied group lands centred on where you are aiming rather than offset by wherever
    /// the map's origin happened to be.
    /// </summary>
    public Vector3 Pivot { get; private set; }

    /// <summary>True when there is nothing to paste.</summary>
    public bool IsEmpty => _brushes.Count == 0 && _patches.Count == 0 && _entities.Count == 0;

    /// <summary>Total item count across all three kinds.</summary>
    public int Count => _brushes.Count + _patches.Count + _entities.Count;

    /// <summary>Drop everything.</summary>
    public void Clear()
    {
        _brushes.Clear();
        _patches.Clear();
        _entities.Clear();
        Pivot = Vector3.Zero;
    }

    /// <summary>
    /// Replace the clipboard with deep copies of everything <paramref name="selection"/> refers to. A face,
    /// edge or vertex selection copies its whole OWNING BRUSH: there is no such thing as a free-floating face
    /// in a plane-set model, so copying one and pasting it could only ever mean copying the solid it bounds.
    /// Returns the number of items captured (0 leaves the previous clipboard untouched, so a copy with nothing
    /// selected does not silently destroy what you copied a minute ago).
    /// </summary>
    public int CopyFrom(VmapDocument document, IReadOnlyList<VmapSelection> selection)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);

        var brushes = new List<VmapBrush>();
        var patches = new List<VmapPatch>();
        var seenBrushes = new HashSet<int>();
        var seenPatches = new HashSet<int>();

        foreach (VmapSelection s in selection)
        {
            if (s.IsEmpty)
                continue;

            if (s.Kind == VmapSelectionKind.Patch)
            {
                if (!seenPatches.Add(s.PatchId))
                    continue;
                VmapPatch? p = document.FindPatch(s.PatchId);
                if (p is not null)
                    patches.Add(p.Clone());
                continue;
            }

            if (!seenBrushes.Add(s.BrushId))
                continue;
            VmapBrush? b = document.FindBrush(s.BrushId);
            if (b is not null)
                brushes.Add(b.Clone());
        }

        // A brush entity's geometry is meaningless without the entity that gives it behaviour, so copying any
        // of a func_door's brushes brings the func_door along with it.
        var entities = new List<VmapEntity>();
        foreach (VmapEntity e in document.Entities)
        {
            if (e.BrushIds.Count == 0 && e.PatchIds.Count == 0)
                continue;
            bool owns = e.BrushIds.Exists(seenBrushes.Contains) || e.PatchIds.Exists(seenPatches.Contains);
            if (owns)
                entities.Add(e.Clone());
        }

        if (brushes.Count == 0 && patches.Count == 0 && entities.Count == 0)
            return 0;

        _brushes.Clear();
        _patches.Clear();
        _entities.Clear();
        _brushes.AddRange(brushes);
        _patches.AddRange(patches);
        _entities.AddRange(entities);
        Pivot = ComputePivot();
        return Count;
    }

    /// <summary>Copy a set of point entities (no geometry) — the entity tool's copy path.</summary>
    public int CopyEntities(IReadOnlyList<VmapEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return 0;

        _brushes.Clear();
        _patches.Clear();
        _entities.Clear();
        foreach (VmapEntity e in entities)
            _entities.Add(e.Clone());
        Pivot = ComputePivot();
        return Count;
    }

    /// <summary>
    /// What the HUD action line says the clipboard holds, e.g. <c>Brush #412, weapon_devastator, +3 more</c>.
    /// Names the first few rather than only counting them: "3 items" does not tell you whether you are about to
    /// paste the thing you meant to.
    /// </summary>
    public string Describe(int maxNamed = 3)
    {
        if (IsEmpty)
            return "";

        var parts = new List<string>(maxNamed + 1);
        int named = 0;

        foreach (VmapBrush b in _brushes)
        {
            if (named >= maxNamed) break;
            parts.Add($"Brush #{b.Id}");
            named++;
        }
        foreach (VmapPatch p in _patches)
        {
            if (named >= maxNamed) break;
            parts.Add($"Patch #{p.Id}");
            named++;
        }
        foreach (VmapEntity e in _entities)
        {
            if (named >= maxNamed) break;
            parts.Add(string.IsNullOrEmpty(e.ClassName) ? $"Entity #{e.Id}" : e.ClassName);
            named++;
        }

        int rest = Count - named;
        if (rest > 0)
            parts.Add($"+{rest} more");
        return string.Join(", ", parts);
    }

    /// <summary>Centre of the bounds of everything held. Zero when the clipboard has no positional content.</summary>
    private Vector3 ComputePivot()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        foreach (VmapBrush b in _brushes)
        {
            foreach (Vector3[] winding in VmapWinding.BuildBrushWindings(b))
                foreach (Vector3 v in winding)
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                    any = true;
                }
        }

        foreach (VmapPatch p in _patches)
            foreach (Vector3 c in p.Controls)
            {
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
                any = true;
            }

        // Point entities only contribute their origin; a brush entity's extent already came from its brushes.
        foreach (VmapEntity e in _entities)
        {
            if (e.IsBrushEntity)
                continue;
            Vector3 o = e.Origin();
            min = Vector3.Min(min, o);
            max = Vector3.Max(max, o);
            any = true;
        }

        return any ? (min + max) * 0.5f : Vector3.Zero;
    }
}
