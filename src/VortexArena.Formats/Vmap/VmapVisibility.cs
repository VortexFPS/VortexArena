using System.Numerics;

namespace VortexArena.Formats.Vmap;

/// <summary>
/// What the editor is currently SHOWING: one object, read by everything that has to agree about it (backlog
/// F9, and the per-group half of F8).
///
/// One object rather than a field per consumer, because the failure this must not have is a brush you can
/// click but cannot see — or worse, one you can see and cannot click. The renderer and the picker each used
/// to carry their own copy of the filter, hand-synced at the call site; adding region, ad-hoc hide and group
/// visibility to both of those is how they drift apart.
///
/// This is VIEW state, not document state. It is not saved, not replicated and not undoable: what one mapper
/// has narrowed their view to is their business, and a co-editing peer's region has nothing to do with yours.
/// The one exception is a group's own <see cref="VmapGroup.Hidden"/> flag, which IS document state — that is
/// a property of the map, and it lands here through <see cref="HiddenGroups"/>.
///
/// Two places this must deliberately NOT reach, and both would read as bugs if it did:
/// the shadow trace (narrowing your view would change the map's lighting) and the playtest collision build
/// (you would walk through everything you had hidden). Both take the gametype filter alone.
/// </summary>
public sealed class VmapVisibility
{
    /// <summary>Show q3map2 tool brushes (hint/skip/clip/trigger/caulk) as ordinary geometry.</summary>
    public bool IncludeToolBrushes { get; set; }

    /// <summary>
    /// Inline-model indices the GAMETYPE filter hides. The oldest of these axes, and the only one that also
    /// reaches collision and lighting.
    /// </summary>
    public HashSet<int> HiddenSubmodels { get; } = new();

    /// <summary>Groups hidden as a unit (backlog F8). Mirrors each group's own persisted flag.</summary>
    public HashSet<int> HiddenGroups { get; } = new();

    /// <summary>Objects hidden individually — the ad-hoc hide and isolate gestures (backlog F9).</summary>
    public HashSet<int> HiddenBrushIds { get; } = new();

    public HashSet<int> HiddenPatchIds { get; } = new();

    public HashSet<int> HiddenEntityIds { get; } = new();

    /// <summary>The region box, or null when the whole map is in view.</summary>
    public Vector3? RegionMins { get; private set; }

    public Vector3? RegionMaxs { get; private set; }

    /// <summary>True when a region is narrowing the view.</summary>
    public bool HasRegion => RegionMins is not null && RegionMaxs is not null;

    /// <summary>How many objects are hidden one at a time — for the HUD, which has to say so.</summary>
    public int ExplicitHiddenCount => HiddenBrushIds.Count + HiddenPatchIds.Count + HiddenEntityIds.Count;

    /// <summary>
    /// Bumped by every mutation, so a cache can compare one int instead of hashing five sets.
    ///
    /// The sets are public and mutable — callers add to them directly — so this is not automatic. Every
    /// mutator calls <see cref="Bump"/>; the alternative (wrapping each set behind methods) buys nothing that
    /// the one call site per gesture does not.
    /// </summary>
    public int Version { get; private set; }

    public void Bump() => Version++;

    /// <summary>Narrow the view to a box. Anything TOUCHING it stays visible — Radiant's rule.</summary>
    public void SetRegion(Vector3 mins, Vector3 maxs)
    {
        RegionMins = Vector3.Min(mins, maxs);
        RegionMaxs = Vector3.Max(mins, maxs);
        Bump();
    }

    public void ClearRegion()
    {
        if (RegionMins is null && RegionMaxs is null)
            return;
        RegionMins = null;
        RegionMaxs = null;
        Bump();
    }

    /// <summary>Forget every individually hidden object. Groups and the gametype filter are left alone.</summary>
    public void ShowAllHidden()
    {
        if (ExplicitHiddenCount == 0)
            return;
        HiddenBrushIds.Clear();
        HiddenPatchIds.Clear();
        HiddenEntityIds.Clear();
        Bump();
    }

    public bool IsBrushVisible(VmapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (brush.IsToolBrush && !IncludeToolBrushes)
            return false;
        if (brush.SubmodelIndex != 0 && HiddenSubmodels.Contains(brush.SubmodelIndex))
            return false;
        if (brush.GroupId != 0 && HiddenGroups.Contains(brush.GroupId))
            return false;
        if (HiddenBrushIds.Contains(brush.Id))
            return false;
        if (!HasRegion)
            return true;
        return VmapWinding.TryGetBounds(brush, out Vector3 lo, out Vector3 hi) && OverlapsRegion(lo, hi);
    }

    public bool IsPatchVisible(VmapPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.GroupId != 0 && HiddenGroups.Contains(patch.GroupId))
            return false;
        if (HiddenPatchIds.Contains(patch.Id))
            return false;
        if (!HasRegion)
            return true;
        if (patch.Controls.Count == 0)
            return true;

        // A bezier lies inside the hull of its control points, so control bounds are conservative — which is
        // the right direction for a region: it shows things that touch the box.
        Vector3 lo = patch.Controls[0], hi = patch.Controls[0];
        foreach (Vector3 c in patch.Controls)
        {
            lo = Vector3.Min(lo, c);
            hi = Vector3.Max(hi, c);
        }
        return OverlapsRegion(lo, hi);
    }

    public bool IsEntityVisible(VmapEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.GroupId != 0 && HiddenGroups.Contains(entity.GroupId))
            return false;
        if (HiddenEntityIds.Contains(entity.Id))
            return false;
        if (!HasRegion)
            return true;

        // A brush entity has no origin of its own; it is in view when any of its geometry is, and the caller
        // filters that geometry separately.
        if (entity.IsBrushEntity)
            return true;

        Vector3 origin = entity.Origin();
        return OverlapsRegion(origin, origin);
    }

    private bool OverlapsRegion(Vector3 lo, Vector3 hi)
    {
        Vector3 rMin = RegionMins!.Value, rMax = RegionMaxs!.Value;
        return lo.X <= rMax.X && hi.X >= rMin.X
            && lo.Y <= rMax.Y && hi.Y >= rMin.Y
            && lo.Z <= rMax.Z && hi.Z >= rMin.Z;
    }
}
