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
