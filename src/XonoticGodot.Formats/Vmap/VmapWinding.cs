using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// The brush CSG kernel: turns a convex brush's plane set into the polygon ("winding") of each face.
/// This is the port of q3map2's <c>polylib.c</c> (<c>BaseWindingForPlane</c> / <c>ChopWindingInPlace</c>)
/// reduced to what map geometry needs, and it is the foundation of the whole editable-geometry pipeline:
/// both importers (<see cref="BspToVmap"/>, <see cref="MapSourceReader"/>) and the render/collision builders
/// evaluate geometry through it, because a <c>.map</c> file — and an edited brush — carry ONLY planes.
///
/// Method: start each face with a huge quad lying in its own plane, then clip that quad against every OTHER
/// face's half-space. What survives is exactly the face's polygon. Vertices come out in counter-clockwise
/// order seen from OUTSIDE the brush (the face normal's side).
/// </summary>
public static class VmapWinding
{
    /// <summary>
    /// Half-extent of the initial quad, matching q3map2's <c>MAX_WORLD_COORD</c> (128k). Comfortably larger
    /// than any legal map (±32k), and the quad is immediately clipped down, so the coarse float precision at
    /// this magnitude never reaches the final vertices.
    /// </summary>
    public const float BaseExtent = 128f * 1024f;

    /// <summary>Points closer than this to a clip plane count as ON it (q3map2 <c>ON_EPSILON</c>).</summary>
    public const float OnEpsilon = 0.1f;

    /// <summary>Vertices closer together than this are merged when a winding is finalized.</summary>
    public const float WeldEpsilon = 0.01f;

    /// <summary>
    /// The polygons of every face of <paramref name="brush"/>, in the same order as
    /// <see cref="VmapBrush.Faces"/>. A face that is entirely clipped away by the other planes (a "bevel"
    /// plane that contributes no surface) yields an EMPTY array at its index rather than being dropped, so
    /// indices stay aligned with the face list.
    /// </summary>
    public static Vector3[][] BuildBrushWindings(VmapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);
        int n = brush.Faces.Count;
        var result = new Vector3[n][];
        for (int i = 0; i < n; i++)
            result[i] = BuildFaceWinding(brush, i);
        return result;
    }

    /// <summary>
    /// The polygon of a single face: the face's own plane quad clipped by every other face's half-space.
    /// Returns an empty array when the face contributes no surface (degenerate or fully clipped).
    /// </summary>
    public static Vector3[] BuildFaceWinding(VmapBrush brush, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (faceIndex < 0 || faceIndex >= brush.Faces.Count)
            return Array.Empty<Vector3>();

        VmapPlane plane = brush.Faces[faceIndex].Plane;
        List<Vector3>? w = BaseWindingForPlane(plane);
        if (w is null)
            return Array.Empty<Vector3>();

        for (int i = 0; i < brush.Faces.Count && w is not null; i++)
        {
            if (i == faceIndex)
                continue;
            VmapPlane clip = brush.Faces[i].Plane;

            // Keep the half-space BEHIND the other face's outward normal (the brush interior side). Flipping
            // the plane turns "keep behind" into ChopInPlace's "keep in front", which is what it implements.
            w = ChopWinding(w, new VmapPlane(-clip.Normal, -clip.Dist));
        }

        if (w is null || w.Count < 3)
            return Array.Empty<Vector3>();

        RemoveDuplicatePoints(w);
        return w.Count < 3 ? Array.Empty<Vector3>() : w.ToArray();
    }

    /// <summary>
    /// A large quad lying in <paramref name="plane"/>, counter-clockwise seen from the front (normal side).
    /// Null when the plane's normal is degenerate.
    /// </summary>
    public static List<Vector3>? BaseWindingForPlane(VmapPlane plane)
    {
        Vector3 n = plane.Normal;
        float len = n.Length();
        if (len < 1e-6f)
            return null;
        n /= len;
        float dist = plane.Dist / len;

        // Pick the axis LEAST aligned with the normal as the seed "up", so the cross products stay well conditioned.
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        Vector3 up = az >= ax && az >= ay
            ? new Vector3(1f, 0f, 0f)
            : new Vector3(0f, 0f, 1f);

        // Gram-Schmidt: remove the normal component, then form the tangent frame.
        up -= n * Vector3.Dot(up, n);
        float upLen = up.Length();
        if (upLen < 1e-6f)
            return null;
        up /= upLen;

        // right = n x up (NOT up x n): this handedness is what makes the quad below run counter-clockwise
        // seen from the +normal side, which is the orientation every consumer of a winding relies on.
        Vector3 right = Vector3.Cross(n, up);
        Vector3 origin = n * dist;

        up *= BaseExtent;
        right *= BaseExtent;

        // Counter-clockwise seen from the +normal side.
        return new List<Vector3>(4)
        {
            origin - right + up,
            origin + right + up,
            origin + right - up,
            origin - right - up,
        };
    }

    /// <summary>
    /// Clip <paramref name="winding"/> to the half-space IN FRONT of <paramref name="plane"/>
    /// (<c>Dot(Normal, p) &gt;= Dist</c>), splitting edges that cross it. Returns null when nothing survives.
    /// Port of q3map2 <c>ChopWindingInPlace</c>.
    /// </summary>
    public static List<Vector3>? ChopWinding(List<Vector3> winding, VmapPlane plane)
    {
        ArgumentNullException.ThrowIfNull(winding);
        int count = winding.Count;
        if (count == 0)
            return null;

        Span<float> dists = count <= 64 ? stackalloc float[count + 1] : new float[count + 1];
        Span<int> sides = count <= 64 ? stackalloc int[count + 1] : new int[count + 1];
        int front = 0, back = 0;

        for (int i = 0; i < count; i++)
        {
            float d = Vector3.Dot(plane.Normal, winding[i]) - plane.Dist;
            dists[i] = d;
            if (d > OnEpsilon)       { sides[i] = 1;  front++; }
            else if (d < -OnEpsilon) { sides[i] = -1; back++; }
            else                     { sides[i] = 0; }
        }
        // Wrap-around sentinel so the edge loop can read [i+1] without a modulo.
        sides[count] = sides[0];
        dists[count] = dists[0];

        if (front == 0)
            return null;      // everything is behind (or on) the plane — fully clipped away
        if (back == 0)
            return winding;   // nothing crosses — unchanged

        var outPts = new List<Vector3>(count + 4);
        for (int i = 0; i < count; i++)
        {
            Vector3 p1 = winding[i];

            if (sides[i] == 0)
            {
                // On the plane: keep it once and never split this edge.
                outPts.Add(p1);
                continue;
            }
            if (sides[i] == 1)
                outPts.Add(p1);

            if (sides[i + 1] == 0 || sides[i + 1] == sides[i])
                continue;

            // The edge crosses the plane — emit the intersection point.
            Vector3 p2 = winding[(i + 1) % count];
            float denom = dists[i] - dists[i + 1];
            if (MathF.Abs(denom) < 1e-12f)
                continue;
            float frac = dists[i] / denom;
            outPts.Add(SnapNearAxis(p1 + (p2 - p1) * frac, plane));
        }

        return outPts.Count < 3 ? null : outPts;
    }

    /// <summary>
    /// Pull an intersection point exactly onto the clip plane when the plane is axis-aligned. Q3 brushes are
    /// overwhelmingly axis-aligned, and this removes the float drift that would otherwise leave hairline
    /// cracks between neighbouring brushes (q3map2 does the same in <c>ChopWindingInPlace</c>).
    /// </summary>
    private static Vector3 SnapNearAxis(Vector3 p, VmapPlane plane)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            float nAxis = axis == 0 ? plane.Normal.X : axis == 1 ? plane.Normal.Y : plane.Normal.Z;
            if (MathF.Abs(nAxis - 1f) < 1e-6f)
            {
                if (axis == 0) p.X = plane.Dist; else if (axis == 1) p.Y = plane.Dist; else p.Z = plane.Dist;
                return p;
            }
            if (MathF.Abs(nAxis + 1f) < 1e-6f)
            {
                if (axis == 0) p.X = -plane.Dist; else if (axis == 1) p.Y = -plane.Dist; else p.Z = -plane.Dist;
                return p;
            }
        }
        return p;
    }

    /// <summary>Drop consecutive (and wrap-around) vertices closer together than <see cref="WeldEpsilon"/>.</summary>
    private static void RemoveDuplicatePoints(List<Vector3> w)
    {
        for (int i = 0; i < w.Count; i++)
        {
            int j = (i + 1) % w.Count;
            if ((w[i] - w[j]).LengthSquared() < WeldEpsilon * WeldEpsilon)
            {
                w.RemoveAt(j);
                i--;
                if (w.Count < 3)
                    return;
            }
        }
    }

    /// <summary>
    /// Corner points of the whole brush (the union of its face windings, deduplicated) — what the collision
    /// builder needs for SAT projection, and what an editor vertex-drag manipulates.
    /// </summary>
    public static Vector3[] BrushPoints(VmapBrush brush)
    {
        Vector3[][] windings = BuildBrushWindings(brush);
        var pts = new List<Vector3>(32);
        foreach (Vector3[] w in windings)
        {
            foreach (Vector3 p in w)
            {
                bool dup = false;
                for (int q = 0; q < pts.Count; q++)
                {
                    if ((pts[q] - p).LengthSquared() < WeldEpsilon * WeldEpsilon)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                    pts.Add(p);
            }
        }
        return pts.ToArray();
    }

    /// <summary>
    /// Axis-aligned bounds of the brush, derived from its windings. Returns false for a brush that bounds no
    /// volume (fewer than 4 faces, or an open/degenerate plane set).
    /// </summary>
    public static bool TryGetBounds(VmapBrush brush, out Vector3 mins, out Vector3 maxs)
    {
        mins = maxs = Vector3.Zero;
        Vector3[] pts = BrushPoints(brush);
        if (pts.Length < 4)
            return false;
        mins = maxs = pts[0];
        for (int i = 1; i < pts.Length; i++)
        {
            mins = Vector3.Min(mins, pts[i]);
            maxs = Vector3.Max(maxs, pts[i]);
        }
        return true;
    }

    /// <summary>
    /// Volume enclosed by the plane set, in cubic world units; zero for anything that does not bound one.
    ///
    /// A tetrahedron fan from one vertex over every face polygon — the divergence-theorem sum, exact for a
    /// convex solid. It exists because it is the decisive test that a CSG merge invented no volume: the union
    /// of two convex solids is contained in the brush built from their outer planes, so the only way that
    /// brush can be wrong is by being BIGGER — which is precisely what a non-convex union (an L, a cross, a
    /// gap between them) looks like.
    /// </summary>
    public static float Volume(VmapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        Vector3[][] windings = BuildBrushWindings(brush);
        Vector3 apex = Vector3.Zero;
        bool haveApex = false;
        foreach (Vector3[] w in windings)
        {
            if (w.Length < 3)
                continue;
            apex = w[0];
            haveApex = true;
            break;
        }
        if (!haveApex)
            return 0f;

        double sum = 0.0;
        int contributing = 0;
        foreach (Vector3[] w in windings)
        {
            if (w.Length < 3)
                continue;
            contributing++;
            for (int i = 1; i + 1 < w.Length; i++)
            {
                Vector3 a = w[0] - apex, b = w[i] - apex, c = w[i + 1] - apex;
                sum += Vector3.Dot(a, Vector3.Cross(b, c));
            }
        }
        return contributing < 4 ? 0f : (float)(Math.Abs(sum) / 6.0);
    }

    /// <summary>
    /// Largest legal absolute world coordinate. A vertex beyond this can only be a surviving remnant of the
    /// huge base quad, which means the plane set never closed on that side.
    /// </summary>
    public const float MaxWorldCoord = 65536f;

    /// <summary>
    /// True when the plane set bounds a closed, non-degenerate convex volume — the validity test an editor
    /// geometry op must pass before it is allowed to commit (design doc §11.4, "the hard part: convexity").
    ///
    /// Two distinct failures have to be caught: too few contributing faces (degenerate/flat), and an OPEN
    /// plane set — half-spaces that never close, e.g. two planes both facing the same way. An open set still
    /// yields finite-looking polygons (the base quad gets clipped by the other planes), so counting faces
    /// alone is not enough; the giveaway is a vertex out at base-quad scale.
    /// </summary>
    public static bool IsClosedConvex(VmapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (brush.Faces.Count < 4)
            return false;

        Vector3[][] windings = BuildBrushWindings(brush);
        int contributing = 0;
        foreach (Vector3[] w in windings)
        {
            if (w.Length < 3)
                continue;
            contributing++;

            foreach (Vector3 p in w)
            {
                if (MathF.Abs(p.X) > MaxWorldCoord || MathF.Abs(p.Y) > MaxWorldCoord || MathF.Abs(p.Z) > MaxWorldCoord)
                    return false; // unbounded in at least one direction
            }
        }

        // Every face of a closed convex solid that is not a redundant bevel contributes a polygon, and a
        // volume needs at least 4 of them (a tetrahedron).
        return contributing >= 4;
    }
}
