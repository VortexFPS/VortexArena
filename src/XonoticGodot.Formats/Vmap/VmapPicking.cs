using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>What a camera ray hit, and where.</summary>
public readonly struct VmapPickResult
{
    public bool Hit { get; init; }
    public VmapSelection Selection { get; init; }

    /// <summary>World position of the hit point on the surface.</summary>
    public Vector3 Point { get; init; }

    /// <summary>Distance along the ray to <see cref="Point"/>.</summary>
    public float Distance { get; init; }

    /// <summary>Outward normal of the face that was hit (useful for the drag axis of a face push).</summary>
    public Vector3 Normal { get; init; }

    public static VmapPickResult Miss => new() { Hit = false, Selection = VmapSelection.None };
}

/// <summary>
/// Ray picking against truth geometry, and geometry-to-geometry snapping (design doc §11.4).
///
/// Picking runs against the brush planes, NOT the render mesh: the render mesh is derived and may be
/// spatially re-chunked, merged or (later) replaced by amplified decoration, so picking it would select
/// something that has no stable identity to edit. Going straight to the truth also means a pick can resolve
/// sub-objects — the face you hit, or the edge/vertex near where you hit it — which is what the vertex and
/// edge drags need.
/// </summary>
public static class VmapPicking
{
    /// <summary>
    /// Pick the nearest brush along a ray, resolving to a vertex, an edge or a face.
    ///
    /// Resolution is by SCREEN-SPACE proximity, approximated here as a world-space radius that the caller
    /// scales with distance (<paramref name="grabRadius"/>): a vertex within the radius of the hit point wins,
    /// then an edge, then the face itself. Without that, distant vertices would be unclickably small while
    /// nearby ones would swallow the whole face.
    /// </summary>
    /// <param name="doc">Document to pick against.</param>
    /// <param name="origin">Ray origin (world/Quake space).</param>
    /// <param name="direction">Ray direction; need not be normalized.</param>
    /// <param name="mode">Which sub-object kinds may be returned.</param>
    /// <param name="grabRadius">World-space radius within which a vertex/edge beats the face.</param>
    /// <param name="maxDistance">Ignore hits beyond this range.</param>
    public static VmapPickResult Pick(
        VmapDocument doc,
        Vector3 origin,
        Vector3 direction,
        VmapSelectionKind mode = VmapSelectionKind.Face,
        float grabRadius = 8f,
        float maxDistance = 8192f)
    {
        ArgumentNullException.ThrowIfNull(doc);

        float dirLen = direction.Length();
        if (dirLen < 1e-6f)
            return VmapPickResult.Miss;
        Vector3 dir = direction / dirLen;

        VmapPickResult best = VmapPickResult.Miss;
        float bestDistance = maxDistance;

        foreach (VmapBrush brush in doc.Brushes)
        {
            Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);
            for (int f = 0; f < windings.Length; f++)
            {
                Vector3[] w = windings[f];
                if (w.Length < 3)
                    continue;

                VmapPlane plane = brush.Faces[f].Plane;

                // Only front faces: a ray leaving the camera should hit the outside of a solid, and skipping
                // back faces stops a click from selecting the far wall of the room you are standing in.
                float denom = Vector3.Dot(plane.Normal, dir);
                if (denom >= -1e-6f)
                    continue;

                float t = (plane.Dist - Vector3.Dot(plane.Normal, origin)) / denom;
                if (t < 0f || t >= bestDistance)
                    continue;

                Vector3 point = origin + dir * t;
                if (!PointInPolygon(w, plane.Normal, point))
                    continue;

                bestDistance = t;
                best = new VmapPickResult
                {
                    Hit = true,
                    Point = point,
                    Distance = t,
                    Normal = plane.Normal,
                    Selection = Resolve(brush, f, w, point, mode, grabRadius),
                };
            }
        }

        return best;
    }

    /// <summary>Choose the sub-object the hit point is closest to, honouring the requested mode.</summary>
    private static VmapSelection Resolve(
        VmapBrush brush, int faceIndex, Vector3[] winding, Vector3 point, VmapSelectionKind mode, float grabRadius)
    {
        if (mode == VmapSelectionKind.Brush)
            return VmapSelection.OfBrush(brush.Id);

        if (mode is VmapSelectionKind.Vertex or VmapSelectionKind.Edge)
        {
            // Vertices beat edges: a corner is harder to hit than an edge, so it gets first refusal.
            if (mode == VmapSelectionKind.Vertex)
            {
                Vector3 nearestVertex = winding[0];
                float bestSq = float.MaxValue;
                foreach (Vector3 v in winding)
                {
                    float d = (v - point).LengthSquared();
                    if (d < bestSq)
                    {
                        bestSq = d;
                        nearestVertex = v;
                    }
                }
                if (bestSq <= grabRadius * grabRadius)
                    return VmapSelection.OfVertex(brush.Id, nearestVertex);
            }
            else
            {
                Vector3 ea = Vector3.Zero, eb = Vector3.Zero;
                float bestDist = float.MaxValue;
                for (int i = 0; i < winding.Length; i++)
                {
                    Vector3 a = winding[i], b = winding[(i + 1) % winding.Length];
                    float d = DistancePointSegment(point, a, b);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        ea = a;
                        eb = b;
                    }
                }
                if (bestDist <= grabRadius)
                    return VmapSelection.OfEdge(brush.Id, ea, eb);
            }
        }

        return VmapSelection.OfFace(brush.Id, faceIndex);
    }

    /// <summary>Is a coplanar point inside a convex polygon? (Consistent sign of the edge cross products.)</summary>
    private static bool PointInPolygon(Vector3[] w, Vector3 normal, Vector3 p)
    {
        for (int i = 0; i < w.Length; i++)
        {
            Vector3 a = w[i], b = w[(i + 1) % w.Length];
            // Winding is counter-clockwise seen from outside, so an interior point stays left of every edge.
            if (Vector3.Dot(Vector3.Cross(b - a, p - a), normal) < -VmapWinding.OnEpsilon)
                return false;
        }
        return true;
    }

    /// <summary>Shortest distance from a point to a line segment.</summary>
    public static float DistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-12f)
            return (p - a).Length();
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / lenSq, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }

    // =============================================================================================
    //  Geometry-to-geometry snapping (E4)
    // =============================================================================================

    /// <summary>A resolved snap: where the dragged point should land, and what it snapped to (for the HUD).</summary>
    public readonly struct SnapResult
    {
        public bool Snapped { get; init; }
        public Vector3 Position { get; init; }
        public VmapSelectionKind TargetKind { get; init; }
        public int TargetBrushId { get; init; }

        /// <summary>The snapped-to feature's endpoints (one point for a vertex, two for an edge) — for drawing the hint.</summary>
        public IReadOnlyList<Vector3> TargetPoints { get; init; }
    }

    /// <summary>
    /// Pull a dragged position onto nearby geometry: vertex first, then edge, then face plane.
    ///
    /// This is what makes brushes actually meet instead of nearly meeting — a hairline gap between two walls
    /// leaks light and shows a seam, and it is invisible at editing zoom. Geometry snapping wins inside its
    /// radius; outside it, the caller falls back to the grid, so the two never fight.
    /// </summary>
    /// <param name="doc">Document to snap against.</param>
    /// <param name="position">The dragged position.</param>
    /// <param name="radius">Snap threshold in world units.</param>
    /// <param name="excludeBrushIds">Brushes being dragged — never snap geometry to itself.</param>
    public static SnapResult SnapToGeometry(
        VmapDocument doc, Vector3 position, float radius, IReadOnlyList<int>? excludeBrushIds = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (radius <= 0f)
            return default;

        float radiusSq = radius * radius;
        SnapResult best = default;
        float bestDist = float.MaxValue;

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (excludeBrushIds is not null && excludeBrushIds.Contains(brush.Id))
                continue;

            Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);
            foreach (Vector3[] w in windings)
            {
                if (w.Length < 3)
                    continue;

                // --- vertex ---
                foreach (Vector3 v in w)
                {
                    float d = (v - position).LengthSquared();
                    if (d <= radiusSq && d < bestDist)
                    {
                        bestDist = d;
                        best = new SnapResult
                        {
                            Snapped = true,
                            Position = v,
                            TargetKind = VmapSelectionKind.Vertex,
                            TargetBrushId = brush.Id,
                            TargetPoints = new[] { v },
                        };
                    }
                }
            }
        }

        // A vertex snap always wins — it is the most specific target, and settling for the edge through it
        // would leave the dragged point sliding along that edge instead of landing on the corner.
        if (best.Snapped)
            return best;

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (excludeBrushIds is not null && excludeBrushIds.Contains(brush.Id))
                continue;

            Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);
            foreach (Vector3[] w in windings)
            {
                if (w.Length < 3)
                    continue;

                for (int i = 0; i < w.Length; i++)
                {
                    Vector3 a = w[i], b = w[(i + 1) % w.Length];
                    float d = DistancePointSegment(position, a, b);
                    if (d > radius || d * d >= bestDist)
                        continue;

                    Vector3 ab = b - a;
                    float t = Math.Clamp(Vector3.Dot(position - a, ab) / MathF.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
                    bestDist = d * d;
                    best = new SnapResult
                    {
                        Snapped = true,
                        Position = a + ab * t,
                        TargetKind = VmapSelectionKind.Edge,
                        TargetBrushId = brush.Id,
                        TargetPoints = new[] { a, b },
                    };
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Resolve a dragged position through the full snapping policy: geometry snap if anything is in range,
    /// otherwise the grid. This is the single place the precedence rule lives, so the 3D view, the ortho views
    /// and any scripted edit all behave identically.
    /// </summary>
    public static Vector3 ResolveDragPosition(
        VmapDocument doc,
        Vector3 position,
        float gridSize,
        float snapRadius,
        IReadOnlyList<int>? excludeBrushIds,
        out SnapResult snap)
    {
        snap = snapRadius > 0f
            ? SnapToGeometry(doc, position, snapRadius, excludeBrushIds)
            : default;

        return snap.Snapped ? snap.Position : VmapEdit.SnapToGrid(position, gridSize);
    }
}
