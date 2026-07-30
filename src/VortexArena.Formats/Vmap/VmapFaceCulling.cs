using System.Numerics;

namespace VortexArena.Formats.Vmap;

/// <summary>
/// Removes the parts of a brush face that are buried inside another solid brush — the step a compiler does and
/// a raw plane-set evaluation does not.
///
/// Why this is not optional. A Quake map is authored as heavily OVERLAPPING solids: a wall brush runs straight
/// through the floor brush, a pillar is sunk into both, trim is embedded in the wall behind it. q3map2 chops
/// every brush side against the other brushes and emits only the fragments with empty space in front of them,
/// so the compiled level contains just its visible skin. Evaluating the plane sets directly — which is what
/// makes an edited map viewable with no compile step — reproduces the mapper's overlapping solids instead, and
/// the buried faces are then painted over the rooms: measured on stormkeep, that is an extra ~38% of drawn
/// surface area, and from inside a corridor you end up looking at the far side of the masonry behind you
/// rather than at the corridor.
///
/// The rule is q3map2's, and it is exact rather than a heuristic: subtract each overlapping opaque solid from
/// the face polygon and keep whatever survives. Faces that merely TOUCH another brush (two floor brushes
/// sharing a seam) keep their full area, because a winding lying on a clip plane counts as inside that plane
/// but is still separated by the brush's other planes — the same epsilon convention as
/// <c>ClipWindingEpsilon</c>. A face lying flush against a solid's surface, with nothing but that solid in
/// front of it, ends up inside every plane and is dropped, which is exactly right: nobody can see it.
/// </summary>
public sealed class VmapFaceCulling
{
    /// <summary>Q3 CONTENTS_SOLID.</summary>
    private const int ContentsSolid = 0x1;

    /// <summary>Q3 CONTENTS_TRANSLUCENT — you can see through it, so it must not erase what is behind it.</summary>
    private const int ContentsTranslucent = 0x2000_0000;

    /// <summary>Grid cell edge in Quake units for the occluder broadphase.</summary>
    private const float CellSize = 128f;

    /// <summary>Bounds slack when gathering candidates, so a face flush against a brush still finds it.</summary>
    private const float BoundsSlack = 1f;

    /// <summary>On-plane tolerance, in Quake units. Matches q3map2's winding epsilon.</summary>
    private const float SplitEpsilon = 0.01f;

    /// <summary>Normal agreement (cos ~2.5°) for calling two planes the same surface.</summary>
    private const float CoplanarDot = 0.999f;

    /// <summary>Plane-distance tolerance, in Quake units, for calling two planes the same surface.</summary>
    private const float CoplanarDistance = 0.1f;

    private readonly List<Occluder> _occluders = new();
    private readonly Dictionary<(int X, int Y, int Z), List<int>> _grid = new();

    /// <summary>An opaque solid that can hide other faces, with its planes and bounds precomputed.</summary>
    private readonly struct Occluder
    {
        public Occluder(VmapBrush brush, VmapPlane[] planes, Vector3 min, Vector3 max)
        {
            Brush = brush;
            Planes = planes;
            Min = min;
            Max = max;
            Center = (min + max) * 0.5f;
            Extent = (max - min) * 0.5f;
        }

        public VmapBrush Brush { get; }
        public VmapPlane[] Planes { get; }
        public Vector3 Min { get; }
        public Vector3 Max { get; }
        public Vector3 Center { get; }
        public Vector3 Extent { get; }
    }

    /// <summary>
    /// Index every brush in <paramref name="doc"/> that can hide geometry.
    /// </summary>
    /// <param name="doc">The document being drawn.</param>
    /// <param name="isVisible">
    /// Predicate for "this brush is part of the world being shown". Hidden brushes must not occlude: filtering
    /// another gametype's <c>func_wall</c> out of the view and still letting it carve holes in the walls
    /// behind it would leave the mapper looking at gaps with no visible cause.
    /// </param>
    public VmapFaceCulling(VmapDocument doc, Func<VmapBrush, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (brush.IsToolBrush || !IsOpaqueSolid(brush))
                continue;
            if (isVisible is not null && !isVisible(brush))
                continue;
            if (!VmapWinding.TryGetBounds(brush, out Vector3 min, out Vector3 max))
                continue;

            var planes = new VmapPlane[brush.Faces.Count];
            for (int i = 0; i < brush.Faces.Count; i++)
                planes[i] = brush.Faces[i].Plane;

            int index = _occluders.Count;
            _occluders.Add(new Occluder(brush, planes, min, max));

            foreach ((int, int, int) cell in CellsFor(min, max))
            {
                if (!_grid.TryGetValue(cell, out List<int>? bucket))
                {
                    bucket = new List<int>();
                    _grid[cell] = bucket;
                }
                bucket.Add(index);
            }
        }
    }

    /// <summary>Number of indexed occluders (diagnostics).</summary>
    public int OccluderCount => _occluders.Count;

    /// <summary>Per-occluder visit stamps, so a brush spanning several grid cells is only subtracted once.</summary>
    private int[] _stamp = Array.Empty<int>();
    private int _stampEpoch;

    private List<List<Vector3>> _fragments = new();
    private List<List<Vector3>> _scratch = new();

    /// <summary>
    /// Subtract every overlapping opaque solid from <paramref name="winding"/>, returning the visible
    /// fragments. Returns an empty list when the face is completely buried.
    ///
    /// The returned list is REUSED between calls — copy anything you need to keep. It is called once per
    /// drawable face of every rebuild, so the alternative is thousands of throwaway lists per edit.
    /// </summary>
    /// <param name="owner">The brush the face belongs to — it never occludes its own faces.</param>
    /// <param name="facePlane">The face's own plane, used to reject occluders that cannot reach it.</param>
    /// <param name="winding">The face polygon, in Quake space.</param>
    public List<List<Vector3>> Subtract(VmapBrush owner, VmapPlane facePlane, IReadOnlyList<Vector3> winding)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(winding);

        _fragments.Clear();
        _fragments.Add(new List<Vector3>(winding));
        if (winding.Count < 3 || _occluders.Count == 0)
            return _fragments;

        Vector3 fMin = winding[0], fMax = winding[0];
        for (int i = 1; i < winding.Count; i++)
        {
            fMin = Vector3.Min(fMin, winding[i]);
            fMax = Vector3.Max(fMax, winding[i]);
        }
        fMin -= new Vector3(BoundsSlack);
        fMax += new Vector3(BoundsSlack);

        Vector3 center = (fMin + fMax) * 0.5f, extent = (fMax - fMin) * 0.5f;

        if (_stamp.Length < _occluders.Count)
            _stamp = new int[_occluders.Count];
        _stampEpoch++;

        int x0 = (int)MathF.Floor(fMin.X / CellSize), x1 = (int)MathF.Floor(fMax.X / CellSize);
        int y0 = (int)MathF.Floor(fMin.Y / CellSize), y1 = (int)MathF.Floor(fMax.Y / CellSize);
        int z0 = (int)MathF.Floor(fMin.Z / CellSize), z1 = (int)MathF.Floor(fMax.Z / CellSize);

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            if (!_grid.TryGetValue((x, y, z), out List<int>? bucket))
                continue;

            foreach (int oi in bucket)
            {
                if (_stamp[oi] == _stampEpoch)
                    continue;
                _stamp[oi] = _stampEpoch;

                Occluder o = _occluders[oi];
                if (ReferenceEquals(o.Brush, owner))
                    continue;
                if (o.Max.X < fMin.X || o.Min.X > fMax.X || o.Max.Y < fMin.Y || o.Min.Y > fMax.Y
                    || o.Max.Z < fMin.Z || o.Min.Z > fMax.Z)
                    continue;
                // The polygon lies in its own plane, so only a solid that CROSSES that plane can contain any
                // part of it. Rejecting the rest here is what keeps this loop off the frame budget: a face
                // typically shares its bounding box with many brushes that sit entirely above or below it.
                if (BoxMissesPlane(facePlane, o.Center, o.Extent))
                    continue;
                // A face lying on this solid's own OUTWARD surface, pointing the same way, is not buried by
                // it — the two brushes share a skin, and open space is still in front. Exactly one of the
                // pair may draw or they z-fight, so the loser is chopped by the winner instead, which is
                // what q3map2's brush CSG does for the same overlap.
                if (KeepsSharedSurface(owner, o.Brush, facePlane))
                    continue;
                // Boxes overlapping is not volumes overlapping. Most survivors of the bounds test are still
                // wholly outside one of the occluder's planes, and answering that from the face's box costs a
                // handful of dot products against no allocation at all.
                if (BoxIsOutside(o.Planes, center, extent))
                    continue;

                _scratch.Clear();
                foreach (List<Vector3> fragment in _fragments)
                    SubtractOne(fragment, o.Planes, _scratch);

                (_fragments, _scratch) = (_scratch, _fragments);
                if (_fragments.Count == 0)
                    return _fragments;
            }
        }

        return _fragments;
    }

    /// <summary>
    /// Subtract one convex volume from one polygon, appending the surviving pieces to <paramref name="output"/>.
    ///
    /// The decomposition is the standard one: piece <c>i</c> is the part of the polygon outside plane <c>i</c>
    /// but inside planes <c>0..i-1</c>. Those pieces are disjoint, and whatever is still inside after the last
    /// plane is inside the volume and therefore invisible.
    ///
    /// The first plane the polygon lies wholly in front of separates it from the volume entirely: Split hands
    /// the whole polygon back as <c>outside</c> with nothing inside, which ends the loop immediately. That is
    /// the early-out for the large majority of pairs that clear the bounds test without really overlapping.
    /// </summary>
    private static void SubtractOne(List<Vector3> polygon, VmapPlane[] planes, List<List<Vector3>> output)
    {
        List<Vector3> remaining = polygon;
        foreach (VmapPlane plane in planes)
        {
            Split(remaining, plane, out List<Vector3>? outside, out List<Vector3>? inside);
            if (outside is not null)
                output.Add(outside);
            if (inside is null)
                return;
            remaining = inside;
        }
        // `remaining` is inside every plane — buried, so it is deliberately not emitted.
    }

    /// <summary>
    /// Split a polygon by a plane into the part in FRONT (outside the brush) and the part BEHIND (inside it).
    ///
    /// Follows q3map2's <c>ClipWindingEpsilon</c> convention for the degenerate case: a polygon lying ON the
    /// plane goes wholly to the inside. That single choice is what makes touching brushes behave — a floor
    /// brush's top face is coplanar with its neighbour's top plane, so it survives on the neighbour's SIDE
    /// planes instead of being eaten at the seam.
    /// </summary>
    private static void Split(List<Vector3> polygon, VmapPlane plane, out List<Vector3>? front, out List<Vector3>? back)
    {
        int count = polygon.Count;

        Span<float> dists = count <= 64 ? stackalloc float[count + 1] : new float[count + 1];
        Span<int> sides = count <= 64 ? stackalloc int[count + 1] : new int[count + 1];
        int frontCount = 0, backCount = 0;

        for (int i = 0; i < count; i++)
        {
            float d = Vector3.Dot(plane.Normal, polygon[i]) - plane.Dist;
            dists[i] = d;
            if (d > SplitEpsilon) { sides[i] = 1; frontCount++; }
            else if (d < -SplitEpsilon) { sides[i] = -1; backCount++; }
            else { sides[i] = 0; }
        }
        sides[count] = sides[0];
        dists[count] = dists[0];

        if (frontCount == 0)
        {
            // Nothing in front: either wholly behind, or wholly ON the plane. Both count as inside.
            front = null;
            back = polygon;
            return;
        }
        if (backCount == 0)
        {
            front = polygon;
            back = null;
            return;
        }

        var f = new List<Vector3>(count + 4);
        var b = new List<Vector3>(count + 4);

        for (int i = 0; i < count; i++)
        {
            Vector3 p1 = polygon[i];

            if (sides[i] == 0)
            {
                f.Add(p1);
                b.Add(p1);
                continue;
            }
            if (sides[i] == 1)
                f.Add(p1);
            else
                b.Add(p1);

            if (sides[i + 1] == 0 || sides[i + 1] == sides[i])
                continue;

            Vector3 p2 = polygon[(i + 1) % count];
            float denom = dists[i] - dists[i + 1];
            if (MathF.Abs(denom) < 1e-12f)
                continue;
            Vector3 mid = p1 + (p2 - p1) * (dists[i] / denom);
            f.Add(mid);
            b.Add(mid);
        }

        front = f.Count >= 3 ? f : null;
        back = b.Count >= 3 ? b : null;
    }

    /// <summary>
    /// Whether <paramref name="owner"/> keeps a face lying in the same plane, facing the same way, as one of
    /// <paramref name="other"/>'s own outward faces.
    ///
    /// The brushes present one shared surface there, so only one of them may draw it. The winner must be a
    /// face that is actually DRAWN: Q3 maps routinely build a wall as a structural shell caulked on the
    /// visible side with a textured skin brush laid flush on top of it, and handing the surface to the caulk
    /// face would filter it away and leave a hole where the wall should be. When both are drawable the choice
    /// is arbitrary but must be stable across rebuilds, so it breaks on brush id.
    /// </summary>
    private static bool KeepsSharedSurface(VmapBrush owner, VmapBrush other, VmapPlane facePlane)
    {
        foreach (VmapFace f in other.Faces)
        {
            if (Vector3.Dot(f.Plane.Normal, facePlane.Normal) <= CoplanarDot
                || MathF.Abs(f.Plane.Dist - facePlane.Dist) >= CoplanarDistance)
                continue;

            bool otherDraws = !VmapBrush.IsToolMaterial(f.Material)
                && (f.SurfaceFlags & VmapGeometryBuilder.SurfaceNoDraw) == 0;
            return !otherDraws || owner.Id <= other.Id;
        }
        return false;   // no shared surface: the normal burial rules apply
    }

    /// <summary>True when the box lies wholly to one side of the plane, so the plane does not cut through it.</summary>
    private static bool BoxMissesPlane(VmapPlane plane, Vector3 center, Vector3 extent)
    {
        Vector3 n = plane.Normal;
        float d = Vector3.Dot(n, center) - plane.Dist;
        float reach = MathF.Abs(n.X) * extent.X + MathF.Abs(n.Y) * extent.Y + MathF.Abs(n.Z) * extent.Z;
        return d - reach > SplitEpsilon || d + reach < -SplitEpsilon;
    }

    /// <summary>
    /// True when the box lies wholly in front of at least one of the volume's planes, which proves the two do
    /// not intersect. The converse does not hold — a box can straddle every plane and still miss the volume —
    /// so this is a conservative reject, never an accept.
    /// </summary>
    private static bool BoxIsOutside(VmapPlane[] planes, Vector3 center, Vector3 extent)
    {
        foreach (VmapPlane plane in planes)
        {
            Vector3 n = plane.Normal;
            float reach = MathF.Abs(n.X) * extent.X + MathF.Abs(n.Y) * extent.Y + MathF.Abs(n.Z) * extent.Z;
            if (Vector3.Dot(n, center) - reach - plane.Dist > SplitEpsilon)
                return true;
        }
        return false;
    }

    /// <summary>Only opaque solids hide anything: water, fog, clip volumes and glass all leave what is behind them visible.</summary>
    private static bool IsOpaqueSolid(VmapBrush brush)
        => (brush.ContentFlags & ContentsSolid) != 0 && (brush.ContentFlags & ContentsTranslucent) == 0;

    private static IEnumerable<(int X, int Y, int Z)> CellsFor(Vector3 min, Vector3 max)
    {
        int x0 = (int)MathF.Floor(min.X / CellSize), x1 = (int)MathF.Floor(max.X / CellSize);
        int y0 = (int)MathF.Floor(min.Y / CellSize), y1 = (int)MathF.Floor(max.Y / CellSize);
        int z0 = (int)MathF.Floor(min.Z / CellSize), z1 = (int)MathF.Floor(max.Z / CellSize);

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            yield return (x, y, z);
    }
}
