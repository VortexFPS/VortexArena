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
    /// <summary>
    /// How far a shadow ray starts off the surface, Quake units.
    ///
    /// It exists to stop a ray hitting the very surface it leaves, so it wants to be as SMALL as precision
    /// allows — not as large as seems safe. At 2 units it was lifting samples clear of the geometry they
    /// belong to: out of a narrow panel recess, or off a trim strip only a few units wide. Those samples
    /// then saw open space and took full light, so grooves that should read as dark lines came out as
    /// bright bands along the seam.
    ///
    /// 0.5 is still a thousand times the float32 epsilon at this map's far corners (~4000 units), so it
    /// keeps its actual job while no longer teleporting samples out of the features they describe.
    /// </summary>
    public const float SurfaceBias = 0.5f;

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

            Insert(new Occluder(planes, min, max));
        }

        AddPatchOccluders(doc);
    }

    /// <summary>Index one occluder into every grid cell its bounds touch.</summary>
    private void Insert(Occluder occluder)
    {
        int index = _occluders.Count;
        _occluders.Add(occluder);

        int x0 = (int)MathF.Floor(occluder.Min.X / CellSize), x1 = (int)MathF.Floor(occluder.Max.X / CellSize);
        int y0 = (int)MathF.Floor(occluder.Min.Y / CellSize), y1 = (int)MathF.Floor(occluder.Max.Y / CellSize);
        int z0 = (int)MathF.Floor(occluder.Min.Z / CellSize), z1 = (int)MathF.Floor(occluder.Max.Z / CellSize);
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

    /// <summary>
    /// Curved surfaces cast shadows too — q3map2's <c>-patchshadows</c>, which the Xonotic game profile
    /// turns on. Without this every arch, pipe and curved wall in the map is transparent to light, and the
    /// shadows that ought to sit under them simply are not there.
    ///
    /// Each tessellated triangle becomes a THIN PRISM: the triangle's own plane, a parallel one just behind
    /// it, and three edge planes. That keeps every occluder a convex plane set, so the existing slab clip
    /// handles patches and brushes with the same code and no special case in the hot loop.
    /// </summary>
    private void AddPatchOccluders(VmapDocument doc)
    {
        if (doc.Patches.Count == 0)
            return;

        var patchDoc = new VmapDocument();
        foreach (VmapPatch patch in doc.Patches)
        {
            if (!patch.IsValid || (patch.SurfaceFlags & SurfaceNonSolid) != 0)
                continue;
            if ((patch.SurfaceFlags & VmapGeometryBuilder.SurfaceSky) != 0)
                continue;
            patchDoc.Patches.Add(patch);
        }
        if (patchDoc.Patches.Count == 0)
            return;

        IReadOnlyList<VmapSurface> surfaces;
        try
        {
            surfaces = VmapGeometryBuilder.BuildSurfaces(patchDoc, includeSky: false);
        }
        catch (Exception)
        {
            return;   // a malformed patch must not take the whole bake down with it
        }

        foreach (VmapSurface surface in surfaces)
        {
            for (int t = 0; t + 2 < surface.Indices.Count; t += 3)
            {
                NVec3 a0 = surface.Positions[surface.Indices[t]];
                NVec3 b0 = surface.Positions[surface.Indices[t + 1]];
                NVec3 c0 = surface.Positions[surface.Indices[t + 2]];

                NVec3 edge1 = b0 - a0, edge2 = c0 - a0;
                NVec3 n = NVec3.Cross(edge1, edge2);
                if (n.LengthSquared() < 1e-8f)
                    continue;   // degenerate triangle: no area, no shadow
                n = NVec3.Normalize(n);

                var planes = new VmapPlane[5];
                planes[0] = new VmapPlane(n, NVec3.Dot(n, a0) + PatchThickness * 0.5f);
                planes[1] = new VmapPlane(-n, -(NVec3.Dot(n, a0) - PatchThickness * 0.5f));
                planes[2] = EdgePlane(a0, b0, n);
                planes[3] = EdgePlane(b0, c0, n);
                planes[4] = EdgePlane(c0, a0, n);

                NVec3 min = NVec3.Min(a0, NVec3.Min(b0, c0)) - new NVec3(PatchThickness);
                NVec3 max = NVec3.Max(a0, NVec3.Max(b0, c0)) + new NVec3(PatchThickness);
                Insert(new Occluder(planes, min, max));
            }
        }
    }

    /// <summary>The outward plane through an edge, perpendicular to the triangle's own plane.</summary>
    private static VmapPlane EdgePlane(NVec3 from, NVec3 to, NVec3 faceNormal)
    {
        NVec3 outward = NVec3.Cross(to - from, faceNormal);
        float len = outward.Length();
        if (len < 1e-8f)
            return new VmapPlane(faceNormal, NVec3.Dot(faceNormal, from));
        outward /= len;
        return new VmapPlane(outward, NVec3.Dot(outward, from));
    }

    /// <summary>Thickness given to a tessellated patch triangle, Quake units.</summary>
    private const float PatchThickness = 2f;

    /// <summary>Q3 <c>surfaceparm nonsolid</c>.</summary>
    private const int SurfaceNonSolid = 0x4000;

    /// <summary>
    /// True when <paramref name="point"/> sits inside a solid occluder. A bake sample can land there — a
    /// face partly covered by an overlapping trim brush keeps its own plane, and the 2-unit lift does not
    /// clear a brush that overlaps by more. Such a sample sees no light from anywhere and must be repaired
    /// from its neighbours, not trusted.
    /// </summary>
    public bool IsInsideSolid(NVec3 point)
    {
        var key = ((int)MathF.Floor(point.X / CellSize),
                   (int)MathF.Floor(point.Y / CellSize),
                   (int)MathF.Floor(point.Z / CellSize));
        if (!_grid.TryGetValue(key, out List<int>? bucket))
            return false;

        foreach (int i in bucket)
        {
            Occluder o = _occluders[i];
            if (point.X < o.Min.X || point.X > o.Max.X
                || point.Y < o.Min.Y || point.Y > o.Max.Y
                || point.Z < o.Min.Z || point.Z > o.Max.Z)
                continue;

            bool inside = true;
            foreach (VmapPlane plane in o.Planes)
                if (plane.Distance(point) > 0f)
                {
                    inside = false;
                    break;
                }
            if (inside)
                return true;
        }
        return false;
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
