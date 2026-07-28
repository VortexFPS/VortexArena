using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// One atomic geometry edit. Ops are the single currency of the editor: they are the undo journal, the
/// network message a co-editing client sends, and the diff a save writes (design doc §11.7). Building them
/// once means undo, replication and autosave are the same mechanism rather than three parallel ones.
///
/// An op must be ALL-OR-NOTHING: <see cref="Apply"/> returns false and leaves the document untouched when the
/// edit would produce invalid geometry. That is what lets the editor clamp a bad drag instead of corrupting a
/// brush (design doc §11.4, "the hard part: convexity").
/// </summary>
public interface IVmapOp
{
    /// <summary>Short human-readable label, shown in the undo list.</summary>
    string Describe();

    /// <summary>
    /// Brushes this op reads or writes. The session snapshots exactly these for undo, and a co-editing server
    /// uses the same set as the lock set so two mappers cannot fight over one brush.
    /// </summary>
    IReadOnlyList<int> TouchedBrushIds { get; }

    /// <summary>
    /// Patches this op reads or writes, on the same contract as <see cref="TouchedBrushIds"/>. Defaulted to
    /// empty because most ops are brush-only, but an op that moves a patch and does NOT declare it here is
    /// un-undoable: the journal snapshots exactly what is declared, so an undeclared patch edit rolls back to
    /// nothing at all and the mapper's undo silently does nothing.
    /// </summary>
    IReadOnlyList<int> TouchedPatchIds => Array.Empty<int>();

    /// <summary>
    /// Entities this op reads or writes, on the same contract as <see cref="TouchedBrushIds"/>. An op that
    /// CREATES entities need not declare anything (the session detects additions and undo removes them); an op
    /// that MUTATES an existing entity must, or its change cannot be rolled back.
    /// </summary>
    IReadOnlyList<int> TouchedEntityIds => Array.Empty<int>();

    /// <summary>Mutate the document. Returns false (having changed nothing) when the edit is invalid.</summary>
    bool Apply(VmapDocument doc);
}

/// <summary>
/// Shared geometry helpers for the edit ops: plane fitting, validation, and the convex refit that vertex and
/// edge drags are built on.
/// </summary>
public static class VmapEdit
{
    /// <summary>A point within this distance of a plane counts as lying on it.</summary>
    public const float OnPlaneEpsilon = 0.05f;

    /// <summary>Two vertices closer than this are the same vertex.</summary>
    public const float VertexEpsilon = 0.05f;

    /// <summary>
    /// Fit a plane through a polygon's points using Newell's method — robust for near-degenerate and slightly
    /// non-planar point sets, where a naive cross product of the first three points collapses.
    /// </summary>
    public static bool TryFitPlane(IReadOnlyList<Vector3> points, out VmapPlane plane)
    {
        plane = default;
        if (points is null || points.Count < 3)
            return false;

        Vector3 normal = Vector3.Zero;
        Vector3 centroid = Vector3.Zero;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[(i + 1) % points.Count];
            normal.X += (a.Y - b.Y) * (a.Z + b.Z);
            normal.Y += (a.Z - b.Z) * (a.X + b.X);
            normal.Z += (a.X - b.X) * (a.Y + b.Y);
            centroid += a;
        }

        float len = normal.Length();
        if (len < 1e-6f)
            return false; // collinear / zero-area

        normal /= len;
        centroid /= points.Count;
        plane = new VmapPlane(normal, Vector3.Dot(centroid, normal));
        return true;
    }

    /// <summary>
    /// Move a set of the brush's corner vertices and re-derive the planes of every face that touched them —
    /// Radiant's vertex-mode semantics.
    ///
    /// A brush stores planes, not points, so "move this vertex" is really "move the planes that meet here",
    /// which is why this cannot be a simple position write. Each affected face's polygon is rebuilt with the
    /// moved points substituted in, and a new plane is fitted through the result. The whole thing is validated
    /// afterwards and rejected as a unit if the brush stopped being a closed convex solid — the drag then gets
    /// clamped by the caller rather than committing broken geometry.
    /// </summary>
    /// <param name="brush">Brush to modify (mutated only on success).</param>
    /// <param name="targets">World positions of the vertices to move (matched within <see cref="VertexEpsilon"/>).</param>
    /// <param name="delta">Translation applied to each matched vertex.</param>
    public static bool TryMoveVertices(VmapBrush brush, IReadOnlyList<Vector3> targets, Vector3 delta)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || delta == Vector3.Zero)
            return false;

        Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);

        // Work on a copy so a rejected drag leaves the original untouched.
        var candidate = brush.Clone();
        bool anyFaceChanged = false;

        for (int f = 0; f < windings.Length; f++)
        {
            Vector3[] w = windings[f];
            if (w.Length < 3)
                continue;

            var moved = new List<Vector3>(w.Length);
            Vector3 anchorSum = Vector3.Zero;
            int anchorCount = 0;
            foreach (Vector3 p in w)
            {
                if (MatchesAny(p, targets))
                {
                    Vector3 newPoint = p + delta;
                    moved.Add(newPoint);
                    anchorSum += newPoint;
                    anchorCount++;
                }
                else
                {
                    moved.Add(p);
                }
            }

            if (anchorCount == 0)
                continue;

            if (!TryFitPlane(moved, out VmapPlane fitted))
                return false; // the face collapsed to a line — reject the whole drag

            // Keep the plane's original facing: Newell's normal follows the winding order, and a face that got
            // dragged past its own plane could otherwise come back inverted, quietly turning the brush inside out.
            if (Vector3.Dot(fitted.Normal, candidate.Faces[f].Plane.Normal) < 0f)
                fitted = new VmapPlane(-fitted.Normal, -fitted.Dist);

            // Re-anchor the plane through the moved point(s). Dragging one corner of a QUAD face makes that
            // face non-planar, and a convex brush cannot represent that — so the face has to tilt. Newell gives
            // the well-conditioned normal for the tilt, but its centroid-based distance would leave the plane
            // splitting the difference and the dragged vertex landing short of where the mapper put it. Taking
            // the distance from the moved points instead puts the grabbed corner exactly on target; the face's
            // other corners then shift along, which is the unavoidable, correct consequence of the tilt.
            Vector3 anchor = anchorSum / anchorCount;
            fitted = new VmapPlane(fitted.Normal, Vector3.Dot(anchor, fitted.Normal));

            candidate.Faces[f].Plane = fitted;
            anyFaceChanged = true;
        }

        if (!anyFaceChanged || !VmapWinding.IsClosedConvex(candidate))
            return false;

        CopyPlanesInto(candidate, brush);
        return true;
    }

    private static bool MatchesAny(Vector3 p, IReadOnlyList<Vector3> targets)
    {
        for (int i = 0; i < targets.Count; i++)
            if ((p - targets[i]).LengthSquared() < VertexEpsilon * VertexEpsilon)
                return true;
        return false;
    }

    /// <summary>Copy face planes from <paramref name="from"/> onto <paramref name="to"/> (same face count).</summary>
    internal static void CopyPlanesInto(VmapBrush from, VmapBrush to)
    {
        for (int i = 0; i < to.Faces.Count && i < from.Faces.Count; i++)
            to.Faces[i].Plane = from.Faces[i].Plane;
    }

    /// <summary>Quantize a value to the nearest multiple of <paramref name="grid"/> (grid &lt;= 0 = no snap).</summary>
    public static float SnapToGrid(float value, float grid)
        => grid <= 0f ? value : MathF.Round(value / grid) * grid;

    /// <summary>Quantize each component of a position to the grid.</summary>
    public static Vector3 SnapToGrid(Vector3 v, float grid)
        => grid <= 0f ? v : new Vector3(SnapToGrid(v.X, grid), SnapToGrid(v.Y, grid), SnapToGrid(v.Z, grid));

    /// <summary>The corner vertices of every brush in <paramref name="ids"/>, for snapping and gizmo placement.</summary>
    public static List<Vector3> CollectVertices(VmapDocument doc, IReadOnlyList<int> ids)
    {
        var pts = new List<Vector3>();
        foreach (int id in ids)
            if (doc.FindBrush(id) is { } b)
                pts.AddRange(VmapWinding.BrushPoints(b));
        return pts;
    }

    /// <summary>Centre of the axis-aligned bounds of a set of brushes — the pivot gizmos anchor to.</summary>
    public static bool TryGetSelectionCenter(VmapDocument doc, IReadOnlyList<int> ids, out Vector3 center)
    {
        center = Vector3.Zero;
        Vector3 mins = new(float.MaxValue), maxs = new(float.MinValue);
        bool any = false;
        foreach (int id in ids)
        {
            if (doc.FindBrush(id) is not { } b || !VmapWinding.TryGetBounds(b, out Vector3 lo, out Vector3 hi))
                continue;
            mins = Vector3.Min(mins, lo);
            maxs = Vector3.Max(maxs, hi);
            any = true;
        }
        if (!any)
            return false;
        center = (mins + maxs) * 0.5f;
        return true;
    }
}

// =================================================================================================
//  E3 — translate / face push / vertex drag / material
// =================================================================================================

/// <summary>
/// Translate whole brushes. Exact and always valid: shifting a convex solid just shifts every bounding
/// plane's distance by the projection of the delta onto its normal, so convexity cannot be broken.
/// </summary>
public sealed class TranslateBrushesOp : IVmapOp
{
    private readonly int[] _ids;
    private readonly Vector3 _delta;

    public TranslateBrushesOp(IReadOnlyList<int> brushIds, Vector3 delta)
    {
        _ids = brushIds?.ToArray() ?? throw new ArgumentNullException(nameof(brushIds));
        _delta = delta;
    }

    public IReadOnlyList<int> TouchedBrushIds => _ids;

    /// <summary>Translation applied to every selected brush. Read by the wire codec.</summary>
    public Vector3 Delta => _delta;

    public string Describe() => $"Move {_ids.Length} brush{(_ids.Length == 1 ? "" : "es")}";

    public bool Apply(VmapDocument doc)
    {
        if (_delta == Vector3.Zero || _ids.Length == 0)
            return false;

        var brushes = new List<VmapBrush>(_ids.Length);
        foreach (int id in _ids)
        {
            if (doc.FindBrush(id) is not { } b)
                return false;
            brushes.Add(b);
        }

        foreach (VmapBrush b in brushes)
            foreach (VmapFace f in b.Faces)
                f.Plane = new VmapPlane(f.Plane.Normal, f.Plane.Dist + Vector3.Dot(f.Plane.Normal, _delta));

        return true;
    }
}

/// <summary>
/// Push a single face along its own normal — the "drag the wall out" gesture, and the most common edit there
/// is. Only that face's plane distance changes, so the brush stays convex by construction; the only failure
/// mode is pushing the face past the opposite side and collapsing the solid, which the validity check catches.
/// </summary>
public sealed class MoveFaceOp : IVmapOp
{
    private readonly int _brushId;
    private readonly int _faceIndex;
    private readonly float _distance;

    public MoveFaceOp(int brushId, int faceIndex, float distance)
    {
        _brushId = brushId;
        _faceIndex = faceIndex;
        _distance = distance;
    }

    public IReadOnlyList<int> TouchedBrushIds => new[] { _brushId };

    /// <summary>Index of the pushed face within its brush. Read by the wire codec.</summary>
    public int FaceIndex => _faceIndex;

    /// <summary>Signed push distance along the face normal. Read by the wire codec.</summary>
    public float Distance => _distance;

    public string Describe() => $"Push face {_faceIndex} of brush {_brushId} by {_distance:0.##}u";

    public bool Apply(VmapDocument doc)
    {
        if (_distance == 0f)
            return false;
        if (doc.FindBrush(_brushId) is not { } brush)
            return false;
        if (_faceIndex < 0 || _faceIndex >= brush.Faces.Count)
            return false;

        VmapFace face = brush.Faces[_faceIndex];
        VmapPlane original = face.Plane;
        face.Plane = new VmapPlane(original.Normal, original.Dist + _distance);

        if (!VmapWinding.IsClosedConvex(brush))
        {
            face.Plane = original;   // collapsed the solid — roll back
            return false;
        }
        return true;
    }
}

/// <summary>
/// Drag brush vertices (one for a vertex grab, two for an edge grab), re-deriving the planes of every face
/// that meets them. See <see cref="VmapEdit.TryMoveVertices"/> for why this is a plane refit rather than a
/// position write.
/// </summary>
public sealed class MoveVerticesOp : IVmapOp
{
    private readonly int _brushId;
    private readonly Vector3[] _targets;
    private readonly Vector3 _delta;

    public MoveVerticesOp(int brushId, IReadOnlyList<Vector3> vertexPositions, Vector3 delta)
    {
        _brushId = brushId;
        _targets = vertexPositions?.ToArray() ?? throw new ArgumentNullException(nameof(vertexPositions));
        _delta = delta;
    }

    public IReadOnlyList<int> TouchedBrushIds => new[] { _brushId };

    /// <summary>World positions of the grabbed vertices. Read by the wire codec.</summary>
    public IReadOnlyList<Vector3> Targets => _targets;

    /// <summary>Translation applied to each grabbed vertex. Read by the wire codec.</summary>
    public Vector3 Delta => _delta;

    public string Describe() => _targets.Length == 2
        ? $"Drag edge of brush {_brushId}"
        : $"Drag {_targets.Length} vertex/vertices of brush {_brushId}";

    public bool Apply(VmapDocument doc)
        => doc.FindBrush(_brushId) is { } brush && VmapEdit.TryMoveVertices(brush, _targets, _delta);
}

/// <summary>
/// Move whole bezier patches by translating every control point. Patches have no plane set, so this is a
/// direct point translation rather than a plane-distance shift — and it is always valid: a translated bezier
/// is still a bezier, so unlike a brush drag there is no convexity to break.
/// </summary>
public sealed class TranslatePatchesOp : IVmapOp
{
    private readonly int[] _patchIds;
    private readonly Vector3 _delta;

    public TranslatePatchesOp(IReadOnlyList<int> patchIds, Vector3 delta)
    {
        _patchIds = patchIds?.ToArray() ?? throw new ArgumentNullException(nameof(patchIds));
        _delta = delta;
    }

    /// <summary>Patches are not brushes; there is nothing brush-shaped to capture here.</summary>
    public IReadOnlyList<int> TouchedBrushIds => Array.Empty<int>();

    /// <summary>Declared so the journal snapshots these patches and undo can put them back.</summary>
    public IReadOnlyList<int> TouchedPatchIds => _patchIds;

    /// <summary>Patch ids this op moves.</summary>
    public IReadOnlyList<int> PatchIds => _patchIds;

    /// <summary>Translation applied to every control point.</summary>
    public Vector3 Delta => _delta;

    public string Describe() => $"Move {_patchIds.Length} patch{(_patchIds.Length == 1 ? "" : "es")}";

    public bool Apply(VmapDocument doc)
    {
        if (_delta == Vector3.Zero || _patchIds.Length == 0)
            return false;

        bool moved = false;
        foreach (int id in _patchIds)
        {
            VmapPatch? patch = doc.Patches.FirstOrDefault(p => p.Id == id);
            if (patch is null)
                continue;
            for (int i = 0; i < patch.Controls.Count; i++)
                patch.Controls[i] += _delta;
            moved = true;
        }
        return moved;
    }
}

/// <summary>Retexture one face. Trivial, but it goes through the op journal so it undoes and replicates.</summary>
public sealed class SetFaceMaterialOp : IVmapOp
{
    private readonly int _brushId;
    private readonly int _faceIndex;
    private readonly string _material;

    public SetFaceMaterialOp(int brushId, int faceIndex, string material)
    {
        _brushId = brushId;
        _faceIndex = faceIndex;
        _material = material ?? string.Empty;
    }

    public IReadOnlyList<int> TouchedBrushIds => new[] { _brushId };

    public string Describe() => $"Set material of brush {_brushId} face {_faceIndex} to {_material}";

    public bool Apply(VmapDocument doc)
    {
        if (doc.FindBrush(_brushId) is not { } brush)
            return false;
        if (_faceIndex < 0 || _faceIndex >= brush.Faces.Count)
            return false;
        if (brush.Faces[_faceIndex].Material == _material)
            return false;
        brush.Faces[_faceIndex].Material = _material;
        return true;
    }
}

// =================================================================================================
//  E4 — rotation
// =================================================================================================

/// <summary>
/// Rotate brushes about an arbitrary axis through a pivot. Like translation this is exact on the plane
/// representation: rotate each normal, then re-anchor the distance through a rotated point on the plane.
/// </summary>
public sealed class RotateBrushesOp : IVmapOp
{
    private readonly int[] _ids;
    private readonly Vector3 _pivot;
    private readonly Vector3 _axis;
    private readonly float _degrees;

    public RotateBrushesOp(IReadOnlyList<int> brushIds, Vector3 pivot, Vector3 axis, float degrees)
    {
        _ids = brushIds?.ToArray() ?? throw new ArgumentNullException(nameof(brushIds));
        _pivot = pivot;
        _axis = axis;
        _degrees = degrees;
    }

    public IReadOnlyList<int> TouchedBrushIds => _ids;

    /// <summary>Point the rotation turns about. Read by the wire codec.</summary>
    public Vector3 Pivot => _pivot;

    /// <summary>Rotation axis (need not be normalized). Read by the wire codec.</summary>
    public Vector3 Axis => _axis;

    /// <summary>Rotation angle in degrees. Read by the wire codec.</summary>
    public float Degrees => _degrees;

    public string Describe() => $"Rotate {_ids.Length} brush{(_ids.Length == 1 ? "" : "es")} by {_degrees:0.#}°";

    public bool Apply(VmapDocument doc)
    {
        if (_degrees == 0f || _ids.Length == 0)
            return false;

        float axisLen = _axis.Length();
        if (axisLen < 1e-6f)
            return false;
        Quaternion q = Quaternion.CreateFromAxisAngle(_axis / axisLen, _degrees * MathF.PI / 180f);

        var brushes = new List<VmapBrush>(_ids.Length);
        foreach (int id in _ids)
        {
            if (doc.FindBrush(id) is not { } b)
                return false;
            brushes.Add(b);
        }

        foreach (VmapBrush b in brushes)
        {
            foreach (VmapFace f in b.Faces)
            {
                VmapPlane p = f.Plane;
                Vector3 onPlane = p.Normal * p.Dist;                         // a point on the original plane
                Vector3 newNormal = Vector3.Transform(p.Normal, q);
                Vector3 newPoint = _pivot + Vector3.Transform(onPlane - _pivot, q);
                f.Plane = new VmapPlane(newNormal, Vector3.Dot(newPoint, newNormal));
            }
        }
        return true;
    }
}

// =================================================================================================
//  E5 — creation, deletion, clipper
// =================================================================================================

/// <summary>
/// Create an axis-aligned box brush from two opposite corners — the drag-out gesture that lets a mapper build
/// from nothing rather than only remix imported geometry.
/// </summary>
public sealed class CreateBoxBrushOp : IVmapOp
{
    private readonly Vector3 _mins;
    private readonly Vector3 _maxs;
    private readonly string _material;
    private int _assignedId;

    public CreateBoxBrushOp(Vector3 cornerA, Vector3 cornerB, string material)
    {
        _mins = Vector3.Min(cornerA, cornerB);
        _maxs = Vector3.Max(cornerA, cornerB);
        _material = material ?? string.Empty;
    }

    /// <summary>Id given to the created brush; valid after a successful <see cref="Apply"/>.</summary>
    public int CreatedBrushId => _assignedId;

    // The id is not known until Apply allocates it, so before that this reports nothing to snapshot — which is
    // correct: there is no prior state for a brush that does not exist yet.
    public IReadOnlyList<int> TouchedBrushIds => _assignedId == 0 ? Array.Empty<int>() : new[] { _assignedId };

    public string Describe() => "Create brush";

    public bool Apply(VmapDocument doc)
    {
        Vector3 size = _maxs - _mins;
        if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f)
            return false; // zero-thickness drag

        _assignedId = doc.NextBrushId();
        var brush = new VmapBrush { Id = _assignedId, ContentFlags = 1 /* Q3 solid */ };

        void Face(Vector3 n, float d) => brush.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = _material,
            Projection = VmapTexProjection.AxialFor(n),
        });

        Face(new Vector3(1, 0, 0), _maxs.X);
        Face(new Vector3(-1, 0, 0), -_mins.X);
        Face(new Vector3(0, 1, 0), _maxs.Y);
        Face(new Vector3(0, -1, 0), -_mins.Y);
        Face(new Vector3(0, 0, 1), _maxs.Z);
        Face(new Vector3(0, 0, -1), -_mins.Z);

        if (!VmapWinding.IsClosedConvex(brush))
            return false;

        doc.Brushes.Add(brush);
        return true;
    }
}

/// <summary>Delete brushes, also unhooking them from any brush entity that owned them.</summary>
public sealed class DeleteBrushesOp : IVmapOp
{
    private readonly int[] _ids;

    public DeleteBrushesOp(IReadOnlyList<int> brushIds)
        => _ids = brushIds?.ToArray() ?? throw new ArgumentNullException(nameof(brushIds));

    public IReadOnlyList<int> TouchedBrushIds => _ids;

    public string Describe() => $"Delete {_ids.Length} brush{(_ids.Length == 1 ? "" : "es")}";

    public bool Apply(VmapDocument doc)
    {
        bool removed = false;
        foreach (int id in _ids)
        {
            VmapBrush? b = doc.FindBrush(id);
            if (b is null)
                continue;
            doc.Brushes.Remove(b);
            foreach (VmapEntity e in doc.Entities)
                e.BrushIds.Remove(id);
            removed = true;
        }
        return removed;
    }
}

/// <summary>
/// Split a brush with a plane — the clipper, the most-used tool in Radiant and the one edit that is
/// <b>convexity-safe by construction</b>: intersecting a convex solid with a half-space always yields a
/// convex solid, so unlike a vertex drag it can never produce invalid geometry.
/// </summary>
public sealed class ClipBrushOp : IVmapOp
{
    private readonly int _brushId;
    private readonly VmapPlane _plane;
    private readonly bool _keepBothHalves;
    private int _createdId;

    /// <param name="brushId">Brush to split.</param>
    /// <param name="plane">Cutting plane; the brush keeps the half BEHIND the normal.</param>
    /// <param name="keepBothHalves">When true the discarded half becomes a second brush instead.</param>
    public ClipBrushOp(int brushId, VmapPlane plane, bool keepBothHalves = false)
    {
        _brushId = brushId;
        _plane = plane;
        _keepBothHalves = keepBothHalves;
    }

    /// <summary>Id of the off-cut brush when <c>keepBothHalves</c> was set; 0 otherwise.</summary>
    public int CreatedBrushId => _createdId;

    public IReadOnlyList<int> TouchedBrushIds
        => _createdId == 0 ? new[] { _brushId } : new[] { _brushId, _createdId };

    public string Describe() => _keepBothHalves ? $"Split brush {_brushId}" : $"Clip brush {_brushId}";

    public bool Apply(VmapDocument doc)
    {
        if (doc.FindBrush(_brushId) is not { } brush)
            return false;

        float len = _plane.Normal.Length();
        if (len < 1e-6f)
            return false;
        var cut = new VmapPlane(_plane.Normal / len, _plane.Dist / len);

        // Both halves must be real solids, or the plane missed the brush entirely (or only grazed it).
        VmapBrush keep = WithExtraPlane(brush, cut);
        VmapBrush other = WithExtraPlane(brush, new VmapPlane(-cut.Normal, -cut.Dist));
        if (!VmapWinding.IsClosedConvex(keep) || !VmapWinding.IsClosedConvex(other))
            return false;

        brush.Faces.Clear();
        foreach (VmapFace f in keep.Faces)
            brush.Faces.Add(f);

        if (_keepBothHalves)
        {
            _createdId = doc.NextBrushId();
            other.Id = _createdId;
            doc.Brushes.Add(other);

            // The off-cut inherits the original's entity ownership, so clipping a door leaf keeps both halves
            // part of the door rather than silently dropping one into the world.
            foreach (VmapEntity e in doc.Entities)
                if (e.BrushIds.Contains(_brushId))
                    e.BrushIds.Add(_createdId);
        }
        return true;
    }

    /// <summary>A copy of the brush with one more bounding plane, textured like the face it most resembles.</summary>
    private static VmapBrush WithExtraPlane(VmapBrush brush, VmapPlane plane)
    {
        VmapBrush copy = brush.Clone();

        // Give the new face the material of whichever existing face points most like it, so a clipped brush
        // does not come back with an untextured cut surface.
        string material = string.Empty;
        int surfaceFlags = 0, contents = 0;
        float best = float.MinValue;
        foreach (VmapFace f in brush.Faces)
        {
            float dot = Vector3.Dot(f.Plane.Normal, plane.Normal);
            if (dot > best)
            {
                best = dot;
                material = f.Material;
                surfaceFlags = f.SurfaceFlags;
                contents = f.ContentFlags;
            }
        }

        copy.Faces.Add(new VmapFace
        {
            Plane = plane,
            Material = material,
            Projection = VmapTexProjection.AxialFor(plane.Normal),
            SurfaceFlags = surfaceFlags,
            ContentFlags = contents,
        });
        return copy;
    }
}

// =================================================================================================
//  E7 — scale
// =================================================================================================

/// <summary>
/// Scale a selection about a pivot, per-axis or uniformly (design doc §11.9). Brushes and patches move in ONE
/// op rather than two, because a mixed selection scaled about a shared pivot has to move together: two ops
/// would be two undo steps, and a failure between them would leave the selection half-scaled.
///
/// The brush maths is the part worth stating, because the obvious version is wrong. A brush face is a PLANE,
/// not a polygon, so it cannot simply have its points multiplied. Under the affine map
/// <c>p → pivot + S·(p − pivot)</c> with <c>S = diag(sx,sy,sz)</c>, a plane's normal transforms by the
/// INVERSE TRANSPOSE of S (here <c>diag(1/sx,1/sy,1/sz)</c>, since S is diagonal), not by S. Scaling the
/// normal directly is the classic bug: it looks right for uniform scales, where the two agree up to a
/// normalization, and skews every face the moment the axes differ.
/// </summary>
public sealed class ScaleSelectionOp : IVmapOp
{
    /// <summary>
    /// Smallest scale factor that still yields geometry. Below this a brush is thin enough that the plane
    /// intersections stop being numerically meaningful, and what comes back is a sliver rather than a solid.
    /// </summary>
    public const float MinFactor = 1e-3f;

    private readonly int[] _brushIds;
    private readonly int[] _patchIds;
    private readonly Vector3 _pivot;
    private readonly Vector3 _scale;

    public ScaleSelectionOp(IReadOnlyList<int> brushIds, IReadOnlyList<int> patchIds, Vector3 pivot, Vector3 scale)
    {
        _brushIds = brushIds?.ToArray() ?? throw new ArgumentNullException(nameof(brushIds));
        _patchIds = patchIds?.ToArray() ?? throw new ArgumentNullException(nameof(patchIds));
        _pivot = pivot;
        _scale = scale;
    }

    /// <summary>Uniform-scale convenience: the same factor on all three axes.</summary>
    public ScaleSelectionOp(IReadOnlyList<int> brushIds, IReadOnlyList<int> patchIds, Vector3 pivot, float factor)
        : this(brushIds, patchIds, pivot, new Vector3(factor, factor, factor)) { }

    public IReadOnlyList<int> TouchedBrushIds => _brushIds;

    public IReadOnlyList<int> TouchedPatchIds => _patchIds;

    /// <summary>Point the scale expands from. Read by the wire codec.</summary>
    public Vector3 Pivot => _pivot;

    /// <summary>Per-axis scale factors. Read by the wire codec.</summary>
    public Vector3 Scale => _scale;

    /// <summary>True when all three factors agree, which is what the centre handle produces.</summary>
    public bool IsUniform =>
        MathF.Abs(_scale.X - _scale.Y) < 1e-6f && MathF.Abs(_scale.Y - _scale.Z) < 1e-6f;

    public string Describe()
    {
        int n = _brushIds.Length + _patchIds.Length;
        string what = n == 1 ? "selection" : $"{n} objects";
        return IsUniform
            ? $"Scale {what} by {_scale.X:0.###}x"
            : $"Scale {what} by ({_scale.X:0.###}, {_scale.Y:0.###}, {_scale.Z:0.###})";
    }

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (_brushIds.Length == 0 && _patchIds.Length == 0)
            return false;
        if (_scale == Vector3.One)
            return false;

        // NEGATIVE factors are refused rather than silently mirroring. A mirror inverts every plane normal,
        // and a brush whose outward normals point inward is not a solid at all under the Quake convention
        // (interior is dot(n,p) <= d) — it is the unbounded complement. Mirroring is a real feature, but it
        // has to re-derive the plane set, which is a different op.
        if (_scale.X < MinFactor || _scale.Y < MinFactor || _scale.Z < MinFactor)
            return false;

        var brushes = new List<VmapBrush>(_brushIds.Length);
        foreach (int id in _brushIds)
        {
            if (doc.FindBrush(id) is not { } b)
                return false;
            brushes.Add(b);
        }

        var patches = new List<VmapPatch>(_patchIds.Length);
        foreach (int id in _patchIds)
        {
            if (doc.FindPatch(id) is not { } p)
                return false;
            patches.Add(p);
        }

        // Build every scaled brush on a COPY first, so a selection where one brush degenerates leaves the
        // whole document untouched instead of applying to the others and failing halfway.
        var scaled = new List<VmapBrush>(brushes.Count);
        var normalScale = new Vector3(1f / _scale.X, 1f / _scale.Y, 1f / _scale.Z);

        foreach (VmapBrush b in brushes)
        {
            VmapBrush candidate = b.Clone();
            foreach (VmapFace f in candidate.Faces)
            {
                VmapPlane p = f.Plane;

                Vector3 n = p.Normal * normalScale;      // inverse-transpose, not the scale itself
                float len = n.Length();
                if (len < 1e-9f)
                    return false;
                n /= len;

                // Re-anchor through a point known to be on the plane, transformed by the FORWARD map.
                Vector3 onPlane = p.Normal * p.Dist;
                Vector3 moved = _pivot + (onPlane - _pivot) * _scale;
                f.Plane = new VmapPlane(n, Vector3.Dot(moved, n));
            }

            // A positive-definite scale maps convex sets to convex sets, so this should always hold; it is
            // checked anyway because "should" and "does" diverge at the precision limit, and a silently
            // degenerate brush is far more expensive to debug later than a refused drag is now.
            if (!VmapWinding.IsClosedConvex(candidate))
                return false;

            scaled.Add(candidate);
        }

        for (int i = 0; i < brushes.Count; i++)
            VmapEdit.CopyPlanesInto(scaled[i], brushes[i]);

        // Patches have no convexity constraint — a control point is just a point, so the forward map applies
        // directly and can never produce something invalid.
        foreach (VmapPatch patch in patches)
            for (int i = 0; i < patch.Controls.Count; i++)
                patch.Controls[i] = _pivot + (patch.Controls[i] - _pivot) * _scale;

        return true;
    }
}

// =================================================================================================
//  E8 — paste
// =================================================================================================

/// <summary>
/// Drop the clipboard into the document at a chosen point (design doc §11.9).
///
/// Two things make this more than a copy loop.
///
/// <b>Ids are minted fresh, and references are REMAPPED.</b> A copied <c>func_door</c> carries the brush ids
/// it owned in the source document; pasting it verbatim would leave the new entity pointing at the ORIGINAL
/// brushes, so moving the pasted door would move the one it was copied from. The op therefore builds an
/// old-id → new-id map as it goes and rewrites every owning reference through it.
///
/// <b>The whole paste is one op.</b> A group of brushes, its patches and the entity that owns them arrive
/// together or not at all, which is what makes a paste exactly one undo step rather than a pile of them.
///
/// The clipboard is snapshotted at CONSTRUCTION rather than read at apply time, so the op stays replayable:
/// undo, copy something else, then redo, and the redo still puts back what it originally placed.
/// </summary>
public sealed class PasteOp : IVmapOp
{
    private readonly VmapBrush[] _brushes;
    private readonly VmapPatch[] _patches;
    private readonly VmapEntity[] _entities;
    private readonly Vector3 _offset;
    private readonly List<int> _createdBrushIds = new();
    private readonly List<int> _createdPatchIds = new();

    /// <summary>
    /// Snapshot <paramref name="clipboard"/> and place its pivot at <paramref name="at"/>.
    /// </summary>
    public PasteOp(VmapClipboard clipboard, Vector3 at)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        // Clone again on the way in: the clipboard is mutable and long-lived (it deliberately outlives the
        // session), so an op holding its live lists would paste whatever was copied MOST RECENTLY on redo.
        _brushes = clipboard.Brushes.Select(b => b.Clone()).ToArray();
        _patches = clipboard.Patches.Select(p => p.Clone()).ToArray();
        _entities = clipboard.Entities.Select(e => e.Clone()).ToArray();
        _offset = at - clipboard.Pivot;
    }

    /// <summary>Brush ids created by the paste; valid after a successful <see cref="Apply"/>.</summary>
    public IReadOnlyList<int> CreatedBrushIds => _createdBrushIds;

    /// <summary>Patch ids created by the paste; valid after a successful <see cref="Apply"/>.</summary>
    public IReadOnlyList<int> CreatedPatchIds => _createdPatchIds;

    // Nothing pre-exists to snapshot: the session detects the additions itself and undo removes them.
    public IReadOnlyList<int> TouchedBrushIds => Array.Empty<int>();

    public IReadOnlyList<int> TouchedPatchIds => Array.Empty<int>();

    public string Describe()
    {
        int n = _brushes.Length + _patches.Length + _entities.Length;
        return n == 1 ? "Paste 1 object" : $"Paste {n} objects";
    }

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_brushes.Length == 0 && _patches.Length == 0 && _entities.Length == 0)
            return false;

        _createdBrushIds.Clear();
        _createdPatchIds.Clear();

        var brushRemap = new Dictionary<int, int>();
        var patchRemap = new Dictionary<int, int>();

        // Allocate off a running counter rather than re-querying NextBrushId per item: the document is only
        // appended to below, so re-querying would be O(n^2) on a big paste for the same answer.
        int nextBrush = doc.NextBrushId();
        int nextPatch = doc.NextPatchId();

        var addedBrushes = new List<VmapBrush>(_brushes.Length);
        foreach (VmapBrush source in _brushes)
        {
            VmapBrush copy = source.Clone();
            brushRemap[source.Id] = copy.Id = nextBrush++;

            foreach (VmapFace f in copy.Faces)
            {
                VmapPlane p = f.Plane;
                // Translating a plane moves its distance along its own normal; the normal itself is unchanged.
                f.Plane = new VmapPlane(p.Normal, p.Dist + Vector3.Dot(_offset, p.Normal));

                // The texture projection is a world-space map, so it has to travel with the geometry or the
                // pasted copy comes out with its texture sliding across the surface.
                VmapTexProjection t = f.Projection;
                f.Projection = new VmapTexProjection(
                    t.AxisU, t.AxisV,
                    t.OffsetU - Vector3.Dot(_offset, t.AxisU),
                    t.OffsetV - Vector3.Dot(_offset, t.AxisV));
            }

            if (!VmapWinding.IsClosedConvex(copy))
                return false;   // refuse the whole paste rather than landing a broken solid
            addedBrushes.Add(copy);
        }

        var addedPatches = new List<VmapPatch>(_patches.Length);
        foreach (VmapPatch source in _patches)
        {
            VmapPatch copy = source.Clone();
            patchRemap[source.Id] = copy.Id = nextPatch++;
            for (int i = 0; i < copy.Controls.Count; i++)
                copy.Controls[i] += _offset;
            addedPatches.Add(copy);
        }

        var addedEntities = new List<VmapEntity>(_entities.Length);
        int nextEntity = doc.NextEntityId();
        foreach (VmapEntity source in _entities)
        {
            VmapEntity copy = source.Clone();
            copy.Id = nextEntity++;

            // Point entities carry their position in a key rather than in geometry.
            if (!copy.IsBrushEntity)
                copy.SetOrigin(copy.Origin() + _offset);

            // Repoint ownership at the brushes and patches THIS paste created. An id with no mapping belonged
            // to something that was not copied, so it is dropped rather than left dangling at a stranger.
            Remap(copy.BrushIds, brushRemap);
            Remap(copy.PatchIds, patchRemap);
            addedEntities.Add(copy);
        }

        // Commit only once every piece has been validated.
        foreach (VmapBrush b in addedBrushes)
        {
            doc.Brushes.Add(b);
            _createdBrushIds.Add(b.Id);
        }
        foreach (VmapPatch p in addedPatches)
        {
            doc.Patches.Add(p);
            _createdPatchIds.Add(p.Id);
        }
        foreach (VmapEntity e in addedEntities)
            doc.Entities.Add(e);

        return true;
    }

    private static void Remap(List<int> ids, Dictionary<int, int> map)
    {
        int write = 0;
        for (int read = 0; read < ids.Count; read++)
            if (map.TryGetValue(ids[read], out int mapped))
                ids[write++] = mapped;
        ids.RemoveRange(write, ids.Count - write);
    }
}

/// <summary>
/// Rotate a selection about a pivot (design doc §11.9) — brushes and patches in ONE op.
///
/// Combined for the same reason <see cref="ScaleSelectionOp"/> is: a mixed selection turned about a shared
/// pivot has to move together, and two ops would be two undo steps that a single drag produced. It DELEGATES
/// the brush half to <see cref="RotateBrushesOp"/> rather than restating the plane maths, and resolves the
/// patches up front so a bad id fails before anything has been mutated.
///
/// The patch half is the easy one: a patch is a list of control points with no convexity constraint, so the
/// rotation applies directly and cannot produce something invalid.
/// </summary>
public sealed class RotateSelectionOp : IVmapOp
{
    private readonly int[] _brushIds;
    private readonly int[] _patchIds;
    private readonly Vector3 _pivot;
    private readonly Vector3 _axis;
    private readonly float _degrees;

    public RotateSelectionOp(
        IReadOnlyList<int> brushIds, IReadOnlyList<int> patchIds, Vector3 pivot, Vector3 axis, float degrees)
    {
        _brushIds = brushIds?.ToArray() ?? throw new ArgumentNullException(nameof(brushIds));
        _patchIds = patchIds?.ToArray() ?? throw new ArgumentNullException(nameof(patchIds));
        _pivot = pivot;
        _axis = axis;
        _degrees = degrees;
    }

    public IReadOnlyList<int> TouchedBrushIds => _brushIds;

    public IReadOnlyList<int> TouchedPatchIds => _patchIds;

    /// <summary>Point the rotation turns about. Read by the wire codec.</summary>
    public Vector3 Pivot => _pivot;

    /// <summary>Rotation axis (need not be normalized). Read by the wire codec.</summary>
    public Vector3 Axis => _axis;

    /// <summary>Rotation angle in degrees. Read by the wire codec.</summary>
    public float Degrees => _degrees;

    public string Describe()
    {
        int n = _brushIds.Length + _patchIds.Length;
        string what = n == 1 ? "selection" : $"{n} objects";
        return $"Rotate {what} by {_degrees:0.#}°";
    }

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_degrees == 0f || (_brushIds.Length == 0 && _patchIds.Length == 0))
            return false;

        float axisLen = _axis.Length();
        if (axisLen < 1e-6f)
            return false;

        // Resolve the patches BEFORE mutating anything, so a bad id cannot leave the brushes turned and the
        // patches not.
        var patches = new List<VmapPatch>(_patchIds.Length);
        foreach (int id in _patchIds)
        {
            if (doc.FindPatch(id) is not { } p)
                return false;
            patches.Add(p);
        }

        if (_brushIds.Length > 0 && !new RotateBrushesOp(_brushIds, _pivot, _axis, _degrees).Apply(doc))
            return false;

        Quaternion q = Quaternion.CreateFromAxisAngle(_axis / axisLen, _degrees * MathF.PI / 180f);
        foreach (VmapPatch patch in patches)
            for (int i = 0; i < patch.Controls.Count; i++)
                patch.Controls[i] = _pivot + Vector3.Transform(patch.Controls[i] - _pivot, q);

        return true;
    }
}

/// <summary>Which side of the cutting plane a clip keeps.</summary>
public enum ClipKeep
{
    /// <summary>Keep the half BEHIND the plane normal — <see cref="ClipBrushOp"/>'s own default.</summary>
    Back,

    /// <summary>Keep the half in FRONT of the normal (the plane is flipped before cutting).</summary>
    Front,

    /// <summary>Keep both halves, the off-cut becoming a second brush.</summary>
    Both,
}

/// <summary>
/// Clip every brush in a selection with one plane (design doc §11.9) — the Clip tool's op.
///
/// One op rather than one per brush, on the same rule as scale and rotate: a single gesture is a single undo
/// step. It is a thin loop over <see cref="ClipBrushOp"/> rather than a reimplementation, so the plane maths
/// and the material inheritance stay in one place.
///
/// Brushes the plane MISSES are skipped rather than failing the operation. That is what makes a marquee-style
/// clip usable: you select a row of pillars, draw one cut across them, and the ones the plane happens not to
/// cross are simply left alone — the alternative refuses the whole gesture because of a brush you were not
/// thinking about.
/// </summary>
public sealed class ClipSelectionOp : IVmapOp
{
    private readonly int[] _brushIds;
    private readonly VmapPlane _plane;
    private readonly ClipKeep _keep;
    private readonly List<int> _createdIds = new();
    private int _clipped;

    public ClipSelectionOp(IReadOnlyList<int> brushIds, VmapPlane plane, ClipKeep keep)
    {
        _brushIds = brushIds?.ToArray() ?? throw new ArgumentNullException(nameof(brushIds));
        _plane = plane;
        _keep = keep;
    }

    public IReadOnlyList<int> TouchedBrushIds => _brushIds;

    /// <summary>Off-cut brushes produced when keeping both halves; valid after a successful <see cref="Apply"/>.</summary>
    public IReadOnlyList<int> CreatedBrushIds => _createdIds;

    /// <summary>How many brushes the plane actually crossed.</summary>
    public int ClippedCount => _clipped;

    public string Describe()
    {
        string verb = _keep == ClipKeep.Both ? "Split" : "Clip";
        return _clipped == 1 ? $"{verb} 1 brush" : $"{verb} {_clipped} brushes";
    }

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_brushIds.Length == 0)
            return false;

        float len = _plane.Normal.Length();
        if (len < 1e-6f)
            return false;

        // "Keep front" is the same cut seen from the other side, so it is expressed by flipping the plane
        // rather than by a second code path through the split.
        VmapPlane cut = _keep == ClipKeep.Front
            ? new VmapPlane(-_plane.Normal, -_plane.Dist)
            : _plane;

        _createdIds.Clear();
        _clipped = 0;

        foreach (int id in _brushIds)
        {
            var one = new ClipBrushOp(id, cut, _keep == ClipKeep.Both);
            if (!one.Apply(doc))
                continue;               // the plane missed this brush — leave it alone

            _clipped++;
            if (one.CreatedBrushId != 0)
                _createdIds.Add(one.CreatedBrushId);
        }

        // Nothing was crossed: report failure so the session does not journal an empty step, and so the caller
        // can tell the mapper the cut went nowhere instead of leaving them wondering.
        return _clipped > 0;
    }
}

// =================================================================================================
//  E8 — entities
// =================================================================================================

/// <summary>
/// Create a point entity of a given class at a position (design doc §11.9).
///
/// Point only. A BRUSH entity has no origin — it is defined by the geometry it owns — so creating one means
/// assigning existing brushes to a new entity, which is a different gesture and a different op.
/// </summary>
public sealed class CreateEntityOp : IVmapOp
{
    private readonly string _className;
    private readonly Vector3 _origin;
    private readonly Dictionary<string, string> _fields;
    private int _assignedId;

    public CreateEntityOp(string className, Vector3 origin, IReadOnlyDictionary<string, string>? fields = null)
    {
        _className = className ?? throw new ArgumentNullException(nameof(className));
        _origin = origin;
        _fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fields is not null)
            foreach (KeyValuePair<string, string> kv in fields)
                _fields[kv.Key] = kv.Value;
    }

    /// <summary>Id given to the created entity; valid after a successful <see cref="Apply"/>.</summary>
    public int CreatedEntityId => _assignedId;

    public IReadOnlyList<int> TouchedBrushIds => Array.Empty<int>();

    // Nothing pre-exists: the session detects the addition and undo removes it.
    public IReadOnlyList<int> TouchedEntityIds => Array.Empty<int>();

    public string Describe() => $"Create {_className}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_className.Length == 0)
            return false;

        _assignedId = doc.NextEntityId();
        var e = new VmapEntity { Id = _assignedId, ClassName = _className };
        foreach (KeyValuePair<string, string> kv in _fields)
            e.Fields[kv.Key] = kv.Value;

        // classname is mirrored into the field bag because that is where the writers read it from; keeping the
        // hoisted property and the key in step is the whole contract of VmapEntity.
        e.Fields["classname"] = _className;
        e.SetOrigin(_origin);

        doc.Entities.Add(e);
        return true;
    }
}

/// <summary>
/// Move entities by a delta.
///
/// A POINT entity moves by rewriting its origin key. A BRUSH entity has no origin to rewrite, so it moves the
/// geometry it owns instead — which is what a mapper means by "move the door": the door IS its brushes.
/// </summary>
public sealed class MoveEntitiesOp : IVmapOp
{
    private readonly int[] _entityIds;
    private readonly Vector3 _delta;
    private int[] _movedBrushes = Array.Empty<int>();
    private int[] _movedPatches = Array.Empty<int>();

    public MoveEntitiesOp(IReadOnlyList<int> entityIds, Vector3 delta, VmapDocument? doc = null)
    {
        _entityIds = entityIds?.ToArray() ?? throw new ArgumentNullException(nameof(entityIds));
        _delta = delta;

        // The touched-geometry set has to be known BEFORE Apply so the journal can snapshot it, and only the
        // document knows which brushes an entity owns. Passing it in at construction is the honest way to get
        // that; without it a brush entity's geometry moves un-undoably.
        if (doc is not null)
            ResolveOwned(doc);
    }

    private void ResolveOwned(VmapDocument doc)
    {
        var brushes = new List<int>();
        var patches = new List<int>();
        foreach (int id in _entityIds)
        {
            if (doc.FindEntity(id) is not { } e)
                continue;
            brushes.AddRange(e.BrushIds);
            patches.AddRange(e.PatchIds);
        }
        _movedBrushes = brushes.ToArray();
        _movedPatches = patches.ToArray();
    }

    public IReadOnlyList<int> TouchedBrushIds => _movedBrushes;

    public IReadOnlyList<int> TouchedPatchIds => _movedPatches;

    public IReadOnlyList<int> TouchedEntityIds => _entityIds;

    /// <summary>Translation applied. Read by the wire codec.</summary>
    public Vector3 Delta => _delta;

    public string Describe() => $"Move {_entityIds.Length} entit{(_entityIds.Length == 1 ? "y" : "ies")}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_delta == Vector3.Zero || _entityIds.Length == 0)
            return false;

        var entities = new List<VmapEntity>(_entityIds.Length);
        foreach (int id in _entityIds)
        {
            if (doc.FindEntity(id) is not { } e)
                return false;
            entities.Add(e);
        }

        foreach (VmapEntity e in entities)
        {
            if (e.IsBrushEntity)
            {
                new TranslateBrushesOp(e.BrushIds, _delta).Apply(doc);
                if (e.PatchIds.Count > 0)
                    new TranslatePatchesOp(e.PatchIds, _delta).Apply(doc);
                continue;
            }
            e.SetOrigin(e.Origin() + _delta);
        }
        return true;
    }
}

/// <summary>
/// Rotate point entities about a pivot, turning both their POSITION and their FACING.
///
/// The facing half is what makes this more than a move. A spawn point or a jumppad target carries its
/// direction in an <c>angle</c> or <c>angles</c> key, and rotating the position while leaving the key alone
/// produces a spawn that is in the right place looking the wrong way — a bug that only shows up when someone
/// spawns there.
/// </summary>
public sealed class RotateEntitiesOp : IVmapOp
{
    private readonly int[] _entityIds;
    private readonly Vector3 _pivot;
    private readonly float _degrees;

    /// <param name="entityIds">Entities to turn; brush entities are skipped (their geometry is rotated instead).</param>
    /// <param name="pivot">Point to turn about.</param>
    /// <param name="degrees">Yaw, in degrees. Rotation is about the vertical axis only: that is what the
    /// single-value <c>angle</c> key can express, and it is the rotation a mapper actually wants on a spawn.</param>
    public RotateEntitiesOp(IReadOnlyList<int> entityIds, Vector3 pivot, float degrees)
    {
        _entityIds = entityIds?.ToArray() ?? throw new ArgumentNullException(nameof(entityIds));
        _pivot = pivot;
        _degrees = degrees;
    }

    public IReadOnlyList<int> TouchedBrushIds => Array.Empty<int>();

    public IReadOnlyList<int> TouchedEntityIds => _entityIds;

    /// <summary>Point the rotation turns about. Read by the wire codec.</summary>
    public Vector3 Pivot => _pivot;

    /// <summary>Yaw in degrees. Read by the wire codec.</summary>
    public float Degrees => _degrees;

    public string Describe()
        => $"Rotate {_entityIds.Length} entit{(_entityIds.Length == 1 ? "y" : "ies")} by {_degrees:0.#} deg";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_degrees == 0f || _entityIds.Length == 0)
            return false;

        var entities = new List<VmapEntity>(_entityIds.Length);
        foreach (int id in _entityIds)
        {
            if (doc.FindEntity(id) is not { } e)
                return false;
            if (!e.IsBrushEntity)
                entities.Add(e);
        }
        if (entities.Count == 0)
            return false;

        float rad = _degrees * MathF.PI / 180f;
        float cos = MathF.Cos(rad), sin = MathF.Sin(rad);

        foreach (VmapEntity e in entities)
        {
            Vector3 rel = e.Origin() - _pivot;
            e.SetOrigin(_pivot + new Vector3(
                rel.X * cos - rel.Y * sin,
                rel.X * sin + rel.Y * cos,
                rel.Z));

            RotateFacing(e, _degrees);
        }
        return true;
    }

    /// <summary>
    /// Add the yaw to whichever facing key the entity actually uses. <c>angles</c> ("pitch yaw roll") wins
    /// when present because it is the more expressive of the two; otherwise the scalar <c>angle</c>.
    /// </summary>
    private static void RotateFacing(VmapEntity e, float degrees)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        if (e.Fields.TryGetValue("angles", out string? angles)
            && VmapEntity.TryParseVector(angles, out Vector3 pyr))
        {
            e.Fields["angles"] = string.Create(inv, $"{pyr.X:0.###} {Wrap(pyr.Y + degrees):0.###} {pyr.Z:0.###}");
            return;
        }

        if (e.Fields.TryGetValue("angle", out string? angle)
            && float.TryParse(angle, System.Globalization.NumberStyles.Float, inv, out float yaw))
        {
            e.Fields["angle"] = Wrap(yaw + degrees).ToString("0.###", inv);
        }
    }

    private static float Wrap(float degrees)
    {
        float d = degrees % 360f;
        return d < 0f ? d + 360f : d;
    }
}

/// <summary>Delete entities, and the geometry a brush entity owns along with them.</summary>
public sealed class DeleteEntitiesOp : IVmapOp
{
    private readonly int[] _entityIds;
    private int[] _ownedBrushes = Array.Empty<int>();
    private int[] _ownedPatches = Array.Empty<int>();

    public DeleteEntitiesOp(IReadOnlyList<int> entityIds, VmapDocument? doc = null)
    {
        _entityIds = entityIds?.ToArray() ?? throw new ArgumentNullException(nameof(entityIds));
        if (doc is null)
            return;

        var brushes = new List<int>();
        var patches = new List<int>();
        foreach (int id in _entityIds)
        {
            if (doc.FindEntity(id) is not { } e)
                continue;
            brushes.AddRange(e.BrushIds);
            patches.AddRange(e.PatchIds);
        }
        _ownedBrushes = brushes.ToArray();
        _ownedPatches = patches.ToArray();
    }

    public IReadOnlyList<int> TouchedBrushIds => _ownedBrushes;

    public IReadOnlyList<int> TouchedPatchIds => _ownedPatches;

    public IReadOnlyList<int> TouchedEntityIds => _entityIds;

    public string Describe() => $"Delete {_entityIds.Length} entit{(_entityIds.Length == 1 ? "y" : "ies")}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_entityIds.Length == 0)
            return false;

        bool removed = false;
        foreach (int id in _entityIds)
        {
            if (doc.FindEntity(id) is not { } e)
                continue;

            // A brush entity's geometry goes with it. Leaving the brushes behind would silently promote a
            // deleted door's leaf into a solid wall in worldspawn, which is neither outcome the mapper meant.
            foreach (int brushId in e.BrushIds)
                if (doc.FindBrush(brushId) is { } b)
                    doc.Brushes.Remove(b);
            foreach (int patchId in e.PatchIds)
                if (doc.FindPatch(patchId) is { } p)
                    doc.Patches.Remove(p);

            doc.Entities.Remove(e);
            removed = true;
        }
        return removed;
    }
}

/// <summary>Set (or clear) one spawn key on one entity — the inspector's edit.</summary>
public sealed class SetEntityKeyOp : IVmapOp
{
    private readonly int _entityId;
    private readonly string _key;
    private readonly string _value;

    /// <param name="entityId">Entity to edit.</param>
    /// <param name="key">Spawn key name.</param>
    /// <param name="value">Empty removes the key, which is how a mapper clears one back to its default.</param>
    public SetEntityKeyOp(int entityId, string key, string value)
    {
        _entityId = entityId;
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _value = value ?? "";
    }

    public IReadOnlyList<int> TouchedBrushIds => Array.Empty<int>();

    public IReadOnlyList<int> TouchedEntityIds => new[] { _entityId };

    public string Describe() => _value.Length == 0 ? $"Clear {_key}" : $"Set {_key} to {_value}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_key.Length == 0 || doc.FindEntity(_entityId) is not { } e)
            return false;

        if (_value.Length == 0)
        {
            // classname is not optional: an entity without one is not spawnable and would be dropped on the
            // next load, so clearing it is refused rather than quietly breaking the entity.
            if (_key.Equals("classname", StringComparison.OrdinalIgnoreCase))
                return false;
            return e.Fields.Remove(_key);
        }

        if (e.Fields.TryGetValue(_key, out string? existing) && existing == _value)
            return false;   // no change: do not journal an empty step

        e.Fields[_key] = _value;
        if (_key.Equals("classname", StringComparison.OrdinalIgnoreCase))
            e.ClassName = _value;
        return true;
    }
}
