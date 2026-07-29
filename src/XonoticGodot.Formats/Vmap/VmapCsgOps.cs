using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// Constructive solid geometry over brushes: subtract, shell (hollow / room) and merge (backlog F5, F6).
///
/// Everything here is expressed in one primitive — <see cref="VmapEdit.WithExtraPlane"/> plus
/// <see cref="VmapWinding.IsClosedConvex"/> — because that is what a Q3 brush IS: an intersection of
/// half-spaces. Adding a plane narrows the solid; validating the result says whether anything is left. No
/// polygon-soup boolean library is involved, no triangulation, and the pieces that come out are ordinary
/// convex brushes the rest of the editor already understands.
///
/// Nothing in this class touches a document. The ops below plan against clones and only commit once every
/// piece has been validated, so a refusal leaves the map exactly as it was rather than half-carved.
/// </summary>
public static class VmapCsg
{
    /// <summary>
    /// Below this, a piece is float noise rather than a solid. <see cref="VmapWinding.ChopWinding"/> already
    /// treats sub-0.1u overlaps as coincident, so a thinner sliver that survives is an artefact of the
    /// arithmetic, not geometry a mapper asked for.
    /// </summary>
    public const float MinPieceExtent = 0.1f;

    /// <summary>
    /// Ceiling on the pieces one gesture may produce. A six-faced cutter over a few hundred overlapping
    /// brushes is thousands of new solids in ONE undo step and ONE wire line — and the submit channel has a
    /// length cap, so past a point the op simply cannot replicate. Refuse rather than truncate: a silently
    /// partial carve is a map two mappers no longer agree on.
    /// </summary>
    public const int MaxPieces = 256;

    /// <summary>What a subtract did, which is not a boolean — "missed" and "failed" need different answers.</summary>
    public enum SubtractOutcome
    {
        /// <summary>A was cut into <c>pieces</c>.</summary>
        Carved,

        /// <summary>A was entirely inside B: nothing is left of it.</summary>
        Swallowed,

        /// <summary>The two solids do not intersect. A is untouched, and this is not an error.</summary>
        Disjoint,

        /// <summary>One of the inputs was not a valid solid, or the result blew the piece cap.</summary>
        Refused,
    }

    /// <summary>
    /// A \ B, as detached convex pieces. Neither input is mutated and <paramref name="pieces"/> is cleared
    /// first.
    ///
    /// This is Radiant's own carve: walk B's planes; at each one, split what is left of A into the part
    /// OUTSIDE that plane (which can never be inside B, so it is finished) and the part inside (which
    /// continues). What remains after the last plane is A∩B and is discarded.
    ///
    /// The subtlety is reading an empty inside-half as DISJOINT rather than as a failure. It is sound only
    /// because A is validated as a closed convex solid up front: if some plane of B leaves nothing of A
    /// behind it, A lies wholly on B's outside.
    /// </summary>
    public static SubtractOutcome Subtract(VmapBrush a, VmapBrush b, List<VmapBrush> pieces)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(pieces);

        pieces.Clear();
        if (!VmapWinding.IsClosedConvex(a) || !VmapWinding.IsClosedConvex(b))
            return SubtractOutcome.Refused;
        if (!Overlaps(a, b))
            return SubtractOutcome.Disjoint;   // fast reject only; the real answer is the empty half below

        VmapBrush remainder = a.Clone();
        foreach (VmapFace bf in b.Faces)
        {
            if (!TryNormalize(bf.Plane, out VmapPlane p))
                return SubtractOutcome.Refused;

            // Interiors are Dot(n, x) <= d, so adding (n, d) keeps the half BEHIND the plane and (-n, -d)
            // keeps the half in FRONT of it.
            VmapBrush inside = VmapEdit.WithExtraPlane(remainder, p);
            VmapBrush outside = VmapEdit.WithExtraPlane(remainder, new VmapPlane(-p.Normal, -p.Dist));

            if (!VmapWinding.IsClosedConvex(inside))
            {
                pieces.Clear();
                return SubtractOutcome.Disjoint;
            }

            if (VmapWinding.IsClosedConvex(outside) && IsFat(outside))
                pieces.Add(outside);
            if (pieces.Count > MaxPieces)
            {
                pieces.Clear();
                return SubtractOutcome.Refused;
            }
            remainder = inside;
        }

        return pieces.Count == 0 ? SubtractOutcome.Swallowed : SubtractOutcome.Carved;
    }

    /// <summary>
    /// Turn a solid into wall slabs of <paramref name="thickness"/>, cleared into
    /// <paramref name="walls"/>.
    ///
    /// <paramref name="outward"/> false is HOLLOW — the walls come out of the brush, so the void is the brush
    /// shrunk. True is ROOM — the void is exactly the brush you drew, and the walls grow outside it.
    ///
    /// Room is expand-then-hollow rather than the obvious "push each face out and keep the outer slab",
    /// because the obvious one leaves a thickness-square gap along every edge and the room LEAKS. Offsetting
    /// every plane first makes the corner slabs overlap, which seals it. Overlapping wall brushes are
    /// ordinary Q3 geometry and are exactly what Radiant's hollow produces.
    /// </summary>
    public static bool Shell(VmapBrush a, float thickness, bool outward, List<VmapBrush> walls)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(walls);

        walls.Clear();
        if (thickness <= 0f || !VmapWinding.IsClosedConvex(a))
            return false;

        VmapBrush outer = outward ? Offset(a, +thickness) : a.Clone();
        if (!VmapWinding.IsClosedConvex(outer))
            return false;

        // The void has to be a real solid. Without this check, a thickness at or past half the brush produces
        // N slabs that are each the whole brush, stacked on each other — geometry that looks right in the
        // wireframe and is solid all the way through.
        if (!VmapWinding.IsClosedConvex(Offset(outer, -thickness)))
            return false;

        Vector3[][] windings = VmapWinding.BuildBrushWindings(outer);
        for (int i = 0; i < outer.Faces.Count && i < windings.Length; i++)
        {
            if (windings[i].Length < 3)
                continue;   // a redundant bevel plane contributes no surface, so it gets no wall

            VmapPlane p = outer.Faces[i].Plane;
            VmapBrush slab = VmapEdit.WithExtraPlane(
                outer, new VmapPlane(-p.Normal, -(p.Dist - thickness)));
            if (VmapWinding.IsClosedConvex(slab) && IsFat(slab))
                walls.Add(slab);
        }

        // Fewer than four walls is not a shell — it is a degenerate result that would leave the void open.
        return walls.Count >= 4;
    }

    /// <summary>
    /// A ∪ B when that union is genuinely one convex solid; null when it is not.
    ///
    /// Built from the planes that survive: a face of A is on the boundary of the union only if all of B is
    /// behind it. That single test drops both interior planes of an abutting pair automatically — the shared
    /// plane and its mirror each fail it — so nothing needs to special-case opposed faces.
    /// </summary>
    public static VmapBrush? Union(VmapBrush a, VmapBrush b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!VmapWinding.IsClosedConvex(a) || !VmapWinding.IsClosedConvex(b))
            return null;

        Vector3[] pointsA = VmapWinding.BrushPoints(a);
        Vector3[] pointsB = VmapWinding.BrushPoints(b);
        if (pointsA.Length < 4 || pointsB.Length < 4)
            return null;

        var merged = new VmapBrush
        {
            Id = a.Id,
            IsDetail = a.IsDetail,
            ContentFlags = a.ContentFlags,
            SubmodelIndex = a.SubmodelIndex,
            IsToolBrush = a.IsToolBrush,
        };

        void Take(VmapBrush from, Vector3[] otherPoints)
        {
            foreach (VmapFace f in from.Faces)
            {
                if (!BoundsAll(f.Plane, otherPoints))
                    continue;                        // the other brush pokes through: an interior plane
                if (AlreadyHave(merged, f.Plane))
                    continue;                        // two brushes sharing a plane would emit it twice
                merged.Faces.Add(f.Clone());         // Clone, so a layered face keeps its stack
            }
        }

        Take(a, pointsB);
        Take(b, pointsA);

        if (!VmapWinding.IsClosedConvex(merged))
            return null;

        // The decisive test. A ∪ B is contained in `merged` by construction, so the only way it can be wrong
        // is by being BIGGER — which is exactly what an L, a cross or a gap between the two looks like.
        float volumeA = VmapWinding.Volume(a);
        float volumeB = VmapWinding.Volume(b);
        float volumeMerged = VmapWinding.Volume(merged);

        var intersection = new VmapBrush();
        foreach (VmapFace f in a.Faces)
            intersection.Faces.Add(f.Clone());
        foreach (VmapFace f in b.Faces)
            intersection.Faces.Add(f.Clone());
        float volumeShared = VmapWinding.IsClosedConvex(intersection)
            ? VmapWinding.Volume(intersection)
            : 0f;

        float expected = volumeA + volumeB - volumeShared;
        return MathF.Abs(volumeMerged - expected) > 1e-3f * MathF.Max(volumeMerged, 1f)
            ? null
            : merged;
    }

    /// <summary>Every plane pushed out along its own normal; a negative distance shrinks the solid.</summary>
    public static VmapBrush Offset(VmapBrush a, float distance)
    {
        ArgumentNullException.ThrowIfNull(a);
        VmapBrush copy = a.Clone();
        for (int i = 0; i < copy.Faces.Count; i++)
        {
            // Normalized first: "+t units" is only true when the normal is a unit vector, and an imported or
            // hand-built plane need not be.
            if (!TryNormalize(copy.Faces[i].Plane, out VmapPlane p))
                continue;
            copy.Faces[i].Plane = new VmapPlane(p.Normal, p.Dist + distance);
        }
        return copy;
    }

    /// <summary>Do the two brushes' bounds overlap? A fast reject, never the final answer.</summary>
    public static bool Overlaps(VmapBrush a, VmapBrush b)
    {
        if (!VmapWinding.TryGetBounds(a, out Vector3 aMin, out Vector3 aMax)
            || !VmapWinding.TryGetBounds(b, out Vector3 bMin, out Vector3 bMax))
            return false;

        return aMin.X <= bMax.X && aMax.X >= bMin.X
            && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
            && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    /// <summary>True when the brush has real extent on all three axes rather than being a numerical sliver.</summary>
    public static bool IsFat(VmapBrush b)
        => VmapWinding.TryGetBounds(b, out Vector3 lo, out Vector3 hi)
           && hi.X - lo.X >= MinPieceExtent
           && hi.Y - lo.Y >= MinPieceExtent
           && hi.Z - lo.Z >= MinPieceExtent;

    private static bool TryNormalize(VmapPlane plane, out VmapPlane normalized)
    {
        float length = plane.Normal.Length();
        if (length < 1e-6f)
        {
            normalized = plane;
            return false;
        }
        normalized = new VmapPlane(plane.Normal / length, plane.Dist / length);
        return true;
    }

    private static bool BoundsAll(VmapPlane plane, Vector3[] points)
    {
        if (!TryNormalize(plane, out VmapPlane p))
            return false;
        foreach (Vector3 v in points)
            if (Vector3.Dot(p.Normal, v) - p.Dist > VmapEdit.OnPlaneEpsilon)
                return false;
        return true;
    }

    private static bool AlreadyHave(VmapBrush brush, VmapPlane plane)
    {
        if (!TryNormalize(plane, out VmapPlane p))
            return true;
        foreach (VmapFace f in brush.Faces)
        {
            if (!TryNormalize(f.Plane, out VmapPlane q))
                continue;
            if (Vector3.Dot(p.Normal, q.Normal) > 1f - 1e-4f
                && MathF.Abs(p.Dist - q.Dist) < VmapWinding.WeldEpsilon)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Carve one brush out of others (backlog F5) — Radiant's subtract, and the doorway workflow there was no
/// substitute for: rough a hole in with a brush, take it out of the wall, delete the cutter.
///
/// The CUTTER SURVIVES, which is Radiant's behaviour and the useful one: the same block cuts the next
/// doorway. It is included in <see cref="TouchedBrushIds"/> even though it does not change, because the carve
/// is recomputed from its geometry on every peer — so it has to be in the co-editing lock set, or a
/// concurrent drag on it makes two machines carve differently.
/// </summary>
public sealed class SubtractBrushesOp : IVmapOp
{
    private readonly int _cutterId;
    private readonly int[] _targets;
    private readonly int[] _forcedIds;
    private readonly int[] _touched;
    private readonly List<int> _createdIds = new();

    /// <param name="forcedIds">
    /// Ids for the extra pieces, consumed in order, when replaying a server-applied op. A replaying peer holds
    /// the same document and carves the same brushes in the same order, so position is identity.
    /// </param>
    public SubtractBrushesOp(int cutterBrushId, IReadOnlyList<int> targetBrushIds,
        IReadOnlyList<int>? forcedIds = null)
    {
        ArgumentNullException.ThrowIfNull(targetBrushIds);
        _cutterId = cutterBrushId;

        var targets = new List<int>(targetBrushIds.Count);
        foreach (int id in targetBrushIds)
            if (id != cutterBrushId && !targets.Contains(id))
                targets.Add(id);
        _targets = targets.ToArray();

        _forcedIds = forcedIds?.ToArray() ?? Array.Empty<int>();

        var touched = new List<int>(_targets.Length + 1) { _cutterId };
        touched.AddRange(_targets);
        _touched = touched.ToArray();
    }

    /// <summary>The brush being cut OUT. Read by the wire codec.</summary>
    public int CutterBrushId => _cutterId;

    /// <summary>The brushes being cut. Read by the wire codec.</summary>
    public IReadOnlyList<int> TargetBrushIds => _targets;

    /// <summary>Extra pieces produced; valid after a successful <see cref="Apply"/>.</summary>
    public IReadOnlyList<int> CreatedBrushIds => _createdIds;

    /// <summary>The ids this op carries on the wire — assigned once it has run, requested before that.</summary>
    public IReadOnlyList<int> WireIds => _createdIds.Count > 0 ? _createdIds : _forcedIds;

    public IReadOnlyList<int> TouchedBrushIds => _touched;

    public string Describe()
        => $"Subtract brush {_cutterId} from {_targets.Length} brush{(_targets.Length == 1 ? "" : "es")}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_targets.Length == 0)
            return false;
        if (doc.FindBrush(_cutterId) is not { } cutter || !VmapWinding.IsClosedConvex(cutter))
            return false;

        // Plan everything first. A carve that fails halfway would leave a document no undo step describes.
        var plan = new List<(VmapBrush Live, List<VmapBrush> Pieces)>();
        int newCount = 0;
        foreach (int id in _targets)
        {
            if (doc.FindBrush(id) is not { } live)
                continue;   // vanished since the gesture; the same tolerance a clip shows

            var pieces = new List<VmapBrush>();
            switch (VmapCsg.Subtract(live, cutter, pieces))
            {
                case VmapCsg.SubtractOutcome.Disjoint:
                    continue;   // the cut missed it — not a failure, just nothing to do
                case VmapCsg.SubtractOutcome.Refused:
                    return false;
                default:
                    plan.Add((live, pieces));
                    newCount += Math.Max(pieces.Count - 1, 0);
                    break;
            }
        }

        if (plan.Count == 0)
            return false;             // nothing intersected: no empty journal step
        if (newCount > VmapCsg.MaxPieces)
            return false;

        // Either no forced ids or exactly as many as there are pieces. A SHORT list would leave the tail
        // minted from NextBrushId, which can collide with a forced id further along and hand two brushes the
        // same id on one peer.
        if (_forcedIds.Length != 0 && _forcedIds.Length != newCount)
            return false;

        int next = doc.NextBrushId();
        int forced = 0;
        _createdIds.Clear();

        foreach ((VmapBrush live, List<VmapBrush> pieces) in plan)
        {
            if (pieces.Count == 0)
            {
                // Swallowed whole. Unhook it from any entity too, or the next save writes a dangling id.
                doc.Brushes.Remove(live);
                foreach (VmapEntity e in doc.Entities)
                    e.BrushIds.Remove(live.Id);
                continue;
            }

            // The first piece takes over the original IN PLACE. Deleting the source and adding N fresh
            // brushes would invalidate every live selection and drop the entity ownership links.
            live.Faces.Clear();
            foreach (VmapFace face in pieces[0].Faces)
                live.Faces.Add(face);

            for (int i = 1; i < pieces.Count; i++)
            {
                VmapBrush piece = pieces[i];
                piece.Id = _forcedIds.Length > 0 ? _forcedIds[forced++] : next++;
                doc.Brushes.Add(piece);
                _createdIds.Add(piece.Id);

                // A carved func_door must not lose limbs into worldspawn.
                foreach (VmapEntity e in doc.Entities)
                    if (e.BrushIds.Contains(live.Id))
                        e.BrushIds.Add(piece.Id);
            }
        }

        return true;
    }

    /// <summary>
    /// Which brushes a cutter should carve, resolved against the document.
    ///
    /// Deliberately NOT part of <see cref="Apply"/>: the op has to be deterministic from the wire list alone,
    /// so the target set is decided once, by the mapper's own machine, and travels explicitly.
    ///
    /// Scoped to the cutter's OWN entity owner (worldspawn when it has none). Carving a func_door with a
    /// world cutter is never what a mapper means, and Radiant's whole-map carve is only safe because it has
    /// no concept of geometry that belongs to something.
    /// </summary>
    public static List<int> ResolveTargets(VmapDocument doc, int cutterId, bool includeToolBrushes)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var targets = new List<int>();
        if (doc.FindBrush(cutterId) is not { } cutter)
            return targets;

        int cutterOwner = doc.OwnerOfBrush(cutterId)?.Id ?? 0;
        foreach (VmapBrush b in doc.Brushes)
        {
            if (b.Id == cutterId)
                continue;
            if (b.IsToolBrush && !includeToolBrushes)
                continue;
            if ((doc.OwnerOfBrush(b.Id)?.Id ?? 0) != cutterOwner)
                continue;
            if (!VmapCsg.Overlaps(cutter, b))
                continue;
            targets.Add(b.Id);
        }
        return targets;
    }
}

/// <summary>
/// Turn solid brushes into hollow shells, or into rooms around themselves (backlog F6).
///
/// Hollow takes the walls OUT of the brush; room grows them outside it, so the void is exactly the volume you
/// drew. Both are conveniences over six clipped brushes, and both are what a mapper reaches for to block out
/// a space in one gesture.
/// </summary>
public sealed class HollowBrushesOp : IVmapOp
{
    private readonly int[] _ids;
    private readonly float _thickness;
    private readonly bool _outward;
    private readonly int[] _forcedIds;
    private readonly List<int> _createdIds = new();

    public HollowBrushesOp(IReadOnlyList<int> brushIds, float thickness, bool outward = false,
        IReadOnlyList<int>? forcedIds = null)
    {
        ArgumentNullException.ThrowIfNull(brushIds);
        var ids = new List<int>(brushIds.Count);
        foreach (int id in brushIds)
            if (!ids.Contains(id))
                ids.Add(id);
        _ids = ids.ToArray();
        _thickness = thickness;
        _outward = outward;
        _forcedIds = forcedIds?.ToArray() ?? Array.Empty<int>();
    }

    /// <summary>Wall thickness in world units. Read by the wire codec.</summary>
    public float Thickness => _thickness;

    /// <summary>True for a ROOM (walls outside the brush), false for a HOLLOW. Read by the wire codec.</summary>
    public bool Outward => _outward;

    public IReadOnlyList<int> TouchedBrushIds => _ids;

    /// <summary>Extra walls produced; valid after a successful <see cref="Apply"/>.</summary>
    public IReadOnlyList<int> CreatedBrushIds => _createdIds;

    /// <summary>The ids this op carries on the wire.</summary>
    public IReadOnlyList<int> WireIds => _createdIds.Count > 0 ? _createdIds : _forcedIds;

    public string Describe()
        => _outward
            ? $"Room {_ids.Length} brush{(_ids.Length == 1 ? "" : "es")} at {_thickness:0.##}u"
            : $"Hollow {_ids.Length} brush{(_ids.Length == 1 ? "" : "es")} at {_thickness:0.##}u";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_ids.Length == 0 || _thickness <= 0f)
            return false;

        var plan = new List<(VmapBrush Live, List<VmapBrush> Walls)>();
        int newCount = 0;
        foreach (int id in _ids)
        {
            if (doc.FindBrush(id) is not { } live)
                return false;

            var walls = new List<VmapBrush>();
            // Unlike a subtract there is no meaningful partial answer: "hollow this" either works on the
            // brush the mapper pointed at or it does not.
            if (!VmapCsg.Shell(live, _thickness, _outward, walls))
                return false;

            plan.Add((live, walls));
            newCount += walls.Count - 1;
        }

        if (newCount > VmapCsg.MaxPieces)
            return false;
        if (_forcedIds.Length != 0 && _forcedIds.Length != newCount)
            return false;

        int next = doc.NextBrushId();
        int forced = 0;
        _createdIds.Clear();

        foreach ((VmapBrush live, List<VmapBrush> walls) in plan)
        {
            live.Faces.Clear();
            foreach (VmapFace face in walls[0].Faces)
                live.Faces.Add(face);

            for (int i = 1; i < walls.Count; i++)
            {
                VmapBrush wall = walls[i];
                wall.Id = _forcedIds.Length > 0 ? _forcedIds[forced++] : next++;
                doc.Brushes.Add(wall);
                _createdIds.Add(wall.Id);

                foreach (VmapEntity e in doc.Entities)
                    if (e.BrushIds.Contains(live.Id))
                        e.BrushIds.Add(wall.Id);
            }
        }

        return true;
    }
}

/// <summary>
/// Fuse brushes into one, when their union is genuinely convex (backlog F6).
///
/// The survivor keeps the FIRST id, so a live selection and any entity ownership stay valid, and no ids are
/// created — which is why this op needs no id handshake and its wire line is a bare list.
///
/// Refuses across differing detail/content/submodel classification, or across entity owners. Merging a detail
/// brush into a structural one silently changes how the map vises, and merging across owners makes the
/// survivor's ownership a coin flip; both are the kind of loss a mapper discovers much later.
/// </summary>
public sealed class MergeBrushesOp : IVmapOp
{
    private readonly int[] _ids;

    public MergeBrushesOp(IReadOnlyList<int> brushIds)
    {
        ArgumentNullException.ThrowIfNull(brushIds);
        var ids = new List<int>(brushIds.Count);
        foreach (int id in brushIds)
            if (!ids.Contains(id))
                ids.Add(id);
        _ids = ids.ToArray();
    }

    public IReadOnlyList<int> TouchedBrushIds => _ids;

    public string Describe() => $"Merge {_ids.Length} brushes";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_ids.Length < 2)
            return false;

        var live = new List<VmapBrush>(_ids.Length);
        foreach (int id in _ids)
        {
            if (doc.FindBrush(id) is not { } b)
                return false;
            live.Add(b);
        }

        VmapBrush first = live[0];
        int firstOwner = doc.OwnerOfBrush(first.Id)?.Id ?? 0;
        for (int i = 1; i < live.Count; i++)
        {
            if (live[i].IsDetail != first.IsDetail
                || live[i].ContentFlags != first.ContentFlags
                || live[i].SubmodelIndex != first.SubmodelIndex
                || live[i].IsToolBrush != first.IsToolBrush)
                return false;
            if ((doc.OwnerOfBrush(live[i].Id)?.Id ?? 0) != firstOwner)
                return false;
        }

        VmapBrush merged = first.Clone();
        for (int i = 1; i < live.Count; i++)
        {
            VmapBrush? next = VmapCsg.Union(merged, live[i]);
            if (next is null)
                return false;
            merged = next;
        }

        first.Faces.Clear();
        foreach (VmapFace face in merged.Faces)
            first.Faces.Add(face);

        for (int i = 1; i < live.Count; i++)
        {
            doc.Brushes.Remove(live[i]);
            foreach (VmapEntity e in doc.Entities)
                e.BrushIds.Remove(live[i].Id);
        }

        return true;
    }
}
