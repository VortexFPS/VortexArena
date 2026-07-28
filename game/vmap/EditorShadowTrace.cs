using XonoticGodot.Formats.Vmap;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// Ray-vs-world occlusion for the light bake — what turns flat direct light into light with shadows.
///
/// Brushes, not triangles. The document's convex plane sets are exactly what a ray wants: testing a ray
/// against a convex volume is a slab clip over its planes, with no BVH to build and no triangle soup to walk,
/// and the brush set is an order of magnitude smaller than the subdivided render mesh the bake runs over.
/// Occluders are indexed into a uniform grid and rays walk it with a 3D DDA, so a trace only tests brushes it
/// could actually reach.
///
/// This is the step q3map2 spends most of its minutes on. It is affordable here because it runs across every
/// core (see <see cref="EditorLightBake"/>) and because a shadow ray is a BOOLEAN — the first blocker ends the
/// trace, unlike a bounce solver that has to keep going.
/// </summary>
public sealed class EditorShadowTrace
{
    /// <summary>Q3 CONTENTS_SOLID.</summary>
    private const int ContentsSolid = 0x1;

    /// <summary>Q3 CONTENTS_TRANSLUCENT — glass and water do not cast a hard shadow.</summary>
    private const int ContentsTranslucent = 0x2000_0000;

    /// <summary>True when any face of the brush is sky — the light comes through, not off, these.</summary>
    private static bool IsSky(VmapBrush brush)
    {
        foreach (VmapFace f in brush.Faces)
            if ((f.SurfaceFlags & VmapGeometryBuilder.SurfaceSky) != 0)
                return true;
        return false;
    }

    private const float CellSize = 256f;

    /// <summary>How far off the surface a shadow ray starts, in Quake units. Stops a face shadowing itself.</summary>
    public const float SurfaceBias = 2f;

    private readonly struct Occluder
    {
        public Occluder(VmapPlane[] planes, NVec3 min, NVec3 max)
        {
            Planes = planes;
            Min = min;
            Max = max;
        }

        public VmapPlane[] Planes { get; }
        public NVec3 Min { get; }
        public NVec3 Max { get; }
    }

    private readonly List<Occluder> _occluders = new();
    private readonly Dictionary<(int, int, int), List<int>> _grid = new();

    /// <summary>Number of indexed occluders (diagnostics).</summary>
    public int OccluderCount => _occluders.Count;

    public EditorShadowTrace(VmapDocument doc, Func<VmapBrush, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (brush.IsToolBrush)
                continue;
            if ((brush.ContentFlags & ContentsSolid) == 0 || (brush.ContentFlags & ContentsTranslucent) != 0)
                continue;
            // SKY IS NOT AN OCCLUDER. In q3map2 a sky surface is where the sun and the sky dome come FROM;
            // treating it as solid seals the map against its own light source, which is why the compiled
            // map's sunlit floor had no counterpart here at all.
            if (IsSky(brush))
                continue;
            if (isVisible is not null && !isVisible(brush))
                continue;
            if (!VmapWinding.TryGetBounds(brush, out NVec3 min, out NVec3 max))
                continue;

            var planes = new VmapPlane[brush.Faces.Count];
            for (int i = 0; i < brush.Faces.Count; i++)
                planes[i] = brush.Faces[i].Plane;

            int index = _occluders.Count;
            _occluders.Add(new Occluder(planes, min, max));

            int x0 = (int)MathF.Floor(min.X / CellSize), x1 = (int)MathF.Floor(max.X / CellSize);
            int y0 = (int)MathF.Floor(min.Y / CellSize), y1 = (int)MathF.Floor(max.Y / CellSize);
            int z0 = (int)MathF.Floor(min.Z / CellSize), z1 = (int)MathF.Floor(max.Z / CellSize);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                var key = (x, y, z);
                if (!_grid.TryGetValue(key, out List<int>? bucket))
                    _grid[key] = bucket = new List<int>();
                bucket.Add(index);
            }
        }
    }

    /// <summary>
    /// True when something solid lies between the two points.
    ///
    /// Read-only and allocation-free, so every core can trace at once against one shared index.
    /// </summary>
    public bool IsOccluded(NVec3 from, NVec3 to)
    {
        NVec3 delta = to - from;
        float length = delta.Length();
        if (length < 1e-3f)
            return false;
        NVec3 dir = delta / length;

        // 3D DDA over the occluder grid: step cell by cell along the ray rather than testing its whole
        // bounding box, which for a long diagonal ray would pull in most of the map.
        int cx = (int)MathF.Floor(from.X / CellSize);
        int cy = (int)MathF.Floor(from.Y / CellSize);
        int cz = (int)MathF.Floor(from.Z / CellSize);
        int ex = (int)MathF.Floor(to.X / CellSize);
        int ey = (int)MathF.Floor(to.Y / CellSize);
        int ez = (int)MathF.Floor(to.Z / CellSize);

        int stepX = dir.X > 0f ? 1 : -1, stepY = dir.Y > 0f ? 1 : -1, stepZ = dir.Z > 0f ? 1 : -1;
        float tDeltaX = MathF.Abs(dir.X) > 1e-6f ? MathF.Abs(CellSize / dir.X) : float.MaxValue;
        float tDeltaY = MathF.Abs(dir.Y) > 1e-6f ? MathF.Abs(CellSize / dir.Y) : float.MaxValue;
        float tDeltaZ = MathF.Abs(dir.Z) > 1e-6f ? MathF.Abs(CellSize / dir.Z) : float.MaxValue;

        float nextX = NextBoundary(from.X, dir.X, cx, stepX);
        float nextY = NextBoundary(from.Y, dir.Y, cy, stepY);
        float nextZ = NextBoundary(from.Z, dir.Z, cz, stepZ);

        // Bounded so a degenerate direction cannot spin forever.
        for (int guard = 0; guard < 512; guard++)
        {
            if (_grid.TryGetValue((cx, cy, cz), out List<int>? bucket))
                foreach (int i in bucket)
                    if (RayHitsBrush(_occluders[i], from, dir, length))
                        return true;

            if (cx == ex && cy == ey && cz == ez)
                return false;

            if (nextX < nextY && nextX < nextZ)
            {
                if (nextX > length) return false;
                cx += stepX; nextX += tDeltaX;
            }
            else if (nextY < nextZ)
            {
                if (nextY > length) return false;
                cy += stepY; nextY += tDeltaY;
            }
            else
            {
                if (nextZ > length) return false;
                cz += stepZ; nextZ += tDeltaZ;
            }
        }
        return false;
    }

    private static float NextBoundary(float origin, float dir, int cell, int step)
    {
        if (MathF.Abs(dir) < 1e-6f)
            return float.MaxValue;
        float boundary = (cell + (step > 0 ? 1 : 0)) * CellSize;
        return (boundary - origin) / dir;
    }

    /// <summary>Slab clip of the ray against the brush half-spaces — the standard convex-volume test.</summary>
    private static bool RayHitsBrush(in Occluder o, NVec3 origin, NVec3 dir, float length)
    {
        // Cheap reject: a ray heading away from the brush box can never reach it.
        if (origin.X > o.Max.X && dir.X >= 0f) return false;
        if (origin.X < o.Min.X && dir.X <= 0f) return false;
        if (origin.Y > o.Max.Y && dir.Y >= 0f) return false;
        if (origin.Y < o.Min.Y && dir.Y <= 0f) return false;
        if (origin.Z > o.Max.Z && dir.Z >= 0f) return false;
        if (origin.Z < o.Min.Z && dir.Z <= 0f) return false;

        float near = 0f, far = length;
        foreach (VmapPlane p in o.Planes)
        {
            float denom = NVec3.Dot(p.Normal, dir);
            float dist = NVec3.Dot(p.Normal, origin) - p.Dist;

            if (MathF.Abs(denom) < 1e-6f)
            {
                if (dist > 0f)
                    return false;   // parallel to this plane and outside it
                continue;
            }

            float t = -dist / denom;
            if (denom < 0f)
            {
                if (t > near) near = t;   // entering the half-space
            }
            else
            {
                if (t < far) far = t;     // leaving it
            }
            if (near > far)
                return false;
        }
        return far > 0f && near < length;
    }
}
