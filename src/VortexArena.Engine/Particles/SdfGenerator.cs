using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XonoticGodot.Engine.Collision;

namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  Chunked SDF generator — planning/particles-dual-system.md §A.3.
//
//  Produces a chunked signed-distance field (SdfField) over the map's collision geometry. Each occupied
//  1024qu chunk is a 128³ (chunk/voxel) slab of world-unit signed distances (negative = inside solid),
//  later uploaded into a GPUParticlesCollisionSDF3D node by the runtime service (§A.5). Distances beyond
//  the band are clamped (Godot only needs accuracy near surfaces).
//
//  We work directly off the engine collision Brushes (the static CollisionWorld): worldspawn brushes plus
//  the tessellated patch slabs that BspCollisionBuilder already emits as thin convex brushes. So §A.3's
//  "brushes + collision triangles" both arrive here as convex Brushes — we treat every SOLID brush as a
//  convex polytope and compute the exact point-to-polytope signed distance, taking the min over all gathered
//  brushes. Patches are thin slabs (open shells made solid via thickness dilation, §A.3 step 4), so a grate
//  reads ≥1 voxel thick to particles, exactly the accepted tradeoff in the spec.
//
//  Port note: this replaces §A.3's unsigned-distance-via-spatial-hash + separate inside test (steps 2-3) with
//  a single exact convex point-to-polytope distance per brush (sign included). It yields the same field — a
//  band-clamped signed distance to the union of solid surfaces — without the triangle spatial hash, because
//  the collision representation is already convex brushes rather than loose triangles.
// =====================================================================================================

/// <summary>Builds a chunked <see cref="SdfField"/> from a <see cref="CollisionWorld"/> (§A.3).</summary>
public sealed class SdfGenerator
{
    private readonly SdfGenParams _p;

    public SdfGenerator(SdfGenParams p)
    {
        _p = p ?? throw new ArgumentNullException(nameof(p));
    }

    /// <summary>
    /// Generate the chunked SDF over <paramref name="world"/>. Only chunks that gather at least one solid
    /// brush are recorded (empty chunks are skipped, §A.3 step 1). <paramref name="onChunk"/> is invoked once
    /// per occupied chunk as it completes (thread-safe — callers append into their own collections). The
    /// returned field has BspHash/ParamsHash left for the caller to fill (§A.2).
    /// </summary>
    public SdfField Generate(CollisionWorld world, Action<SdfChunk>? onChunk = null, CancellationToken ct = default)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        float chunkSize = _p.ChunkSize;
        float voxel = _p.VoxelSize;
        int res = _p.Resolution;
        if (res < 1) res = 1;
        float skirt = _p.Skirt;
        float band = _p.Band;
        // §A.3 step 4 thickness dilation: shrink the field by half a voxel + extra Thickness so sub-voxel
        // walls/grates read ≥1 voxel thick and fast particles can't tunnel.
        float dilate = 0.5f * voxel + _p.Thickness;

        Vector3 worldMins = world.WorldMins;
        Vector3 worldMaxs = world.WorldMaxs;

        // Floor-aligned grid origin so chunk cells tile from a stable lattice (a different worldMins by a few
        // qu must not shift every voxel). GridMins = floor(worldMins / chunkSize) * chunkSize.
        Vector3 gridMins = new(
            MathF.Floor(worldMins.X / chunkSize) * chunkSize,
            MathF.Floor(worldMins.Y / chunkSize) * chunkSize,
            MathF.Floor(worldMins.Z / chunkSize) * chunkSize);

        int dimsX = Math.Max(1, (int)MathF.Ceiling((worldMaxs.X - gridMins.X) / chunkSize));
        int dimsY = Math.Max(1, (int)MathF.Ceiling((worldMaxs.Y - gridMins.Y) / chunkSize));
        int dimsZ = Math.Max(1, (int)MathF.Ceiling((worldMaxs.Z - gridMins.Z) / chunkSize));

        var field = new SdfField
        {
            VoxelSize = voxel,
            ChunkSize = chunkSize,
            Skirt = skirt,
            Thickness = _p.Thickness,
            GeneratorVersion = PsdfFile.GeneratorVersion,
            GridMins = gridMins,
            GridDimsX = dimsX,
            GridDimsY = dimsY,
            GridDimsZ = dimsZ,
        };

        // ---- pass 1 (serial): find occupied chunks ------------------------------------------------------
        // CollisionWorld.Query is NOT thread-safe (it mutates a shared epoch-mark array for broadphase dedup),
        // so we do the cheap gather single-threaded here, then parallelize only the expensive voxel sweep
        // (pass 2) — the same split MapLoader uses (decide on the main thread, run the math on workers). A cell
        // whose ±skirt gather has no SOLID brush is unoccupied → no record / no collider node (§A.3 step 1).
        var jobs = new List<(int Cx, int Cy, int Cz, Vector3 CellMins, Brush[] Solids)>();
        var gathered = new List<Brush>();
        for (int cz = 0; cz < dimsZ; cz++)
        for (int cy = 0; cy < dimsY; cy++)
        for (int cx = 0; cx < dimsX; cx++)
        {
            ct.ThrowIfCancellationRequested();

            Vector3 cellMins = gridMins + new Vector3(cx * chunkSize, cy * chunkSize, cz * chunkSize);
            Vector3 cellMaxs = cellMins + new Vector3(chunkSize, chunkSize, chunkSize);

            gathered.Clear();
            Vector3 gMins = cellMins - new Vector3(skirt);
            Vector3 gMaxs = cellMaxs + new Vector3(skirt);
            world.Query(gMins, gMaxs, gathered);

            // Keep only SOLID-bearing brushes (water/lava/hint don't block particles).
            int solidCount = 0;
            for (int k = 0; k < gathered.Count; k++)
                if ((gathered[k].Contents & SuperContents.Solid) != 0)
                    solidCount++;
            if (solidCount == 0)
                continue; // unoccupied chunk — no record (§A.3 step 1)

            var solids = new Brush[solidCount];
            int si = 0;
            for (int k = 0; k < gathered.Count; k++)
                if ((gathered[k].Contents & SuperContents.Solid) != 0)
                    solids[si++] = gathered[k];

            jobs.Add((cx, cy, cz, cellMins, solids));
        }

        // ---- pass 2 (parallel): voxel sweep per occupied chunk ------------------------------------------
        var results = new SdfChunk[jobs.Count];
        var sync = new object();
        var pOptions = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, jobs.Count, pOptions, i =>
        {
            ct.ThrowIfCancellationRequested();
            var (cx, cy, cz, cellMins, solids) = jobs[i];
            SdfChunk chunk = GenerateChunk(cx, cy, cz, cellMins, res, voxel, band, dilate, solids);
            results[i] = chunk;
            if (onChunk != null)
                lock (sync) { onChunk(chunk); }
        });

        for (int i = 0; i < results.Length; i++)
            field.Chunks.Add(results[i]);

        return field;
    }

    /// <summary>
    /// Voxel sweep for one chunk: Res³ signed distances (X fastest, then Y, then Z) to the union of
    /// <paramref name="solids"/>, dilated and band-clamped (§A.3 steps 3-5). Voxel centers are at
    /// cellMins + (i+0.5)*voxel so samples sit in the middle of each voxel cell.
    /// </summary>
    private static SdfChunk GenerateChunk(int cx, int cy, int cz, Vector3 cellMins, int res, float voxel,
        float band, float dilate, Brush[] solids)
    {
        var dist = new float[res * res * res];

        for (int z = 0; z < res; z++)
        {
            float wz = cellMins.Z + (z + 0.5f) * voxel;
            int zBase = z * res * res;
            for (int y = 0; y < res; y++)
            {
                float wy = cellMins.Y + (y + 0.5f) * voxel;
                int yBase = zBase + y * res;
                for (int x = 0; x < res; x++)
                {
                    float wx = cellMins.X + (x + 0.5f) * voxel;
                    var p = new Vector3(wx, wy, wz);

                    // min signed distance over the gathered solid brushes (union of convex polytopes).
                    float best = band;
                    for (int b = 0; b < solids.Length; b++)
                    {
                        float d = SignedDistanceToBrush(p, solids[b]);
                        if (d < best) best = d;
                    }

                    // §A.3 step 4 thickness dilation: subtract dilate (= 0.5*voxel + Thickness). Then clamp the
                    // interior of any hit surface to ≤ -voxel so a sub-voxel wall/grate reads ≥1 voxel thick.
                    best -= dilate;
                    if (best < 0f && best > -voxel)
                        best = -voxel;

                    // §A.3 step 2/5: clamp to ±band (Godot only needs accuracy near surfaces).
                    if (best > band) best = band;
                    else if (best < -band) best = -band;

                    dist[yBase + x] = best;
                }
            }
        }

        return new SdfChunk
        {
            Cx = cx, Cy = cy, Cz = cz,
            Res = res,
            Flags = 0,
            Distances = dist,
            CellMins = cellMins,
        };
    }

    // =================================================================================================
    //  Exact signed distance from a point to a convex brush (intersection of its plane half-spaces).
    //
    //  Inside (behind every plane): the signed distance is the largest (closest-to-zero, i.e. least
    //  negative) plane signed distance — that is the negative distance to the nearest face. Convexity
    //  guarantees this is the true negative distance to the boundary.
    //
    //  Outside (in front of ≥1 plane): the closest point lies on the boundary. We take the min over all
    //  faces of the distance to that face's polygon (point clamped into the convex face polygon). The face
    //  polygon is the subset of the brush's corner Points lying on that plane, ordered around the face
    //  normal. This also covers edge/vertex closest features (a clamp to the polygon boundary lands on the
    //  shared edge/vertex). Mirrors DP's convex-brush nearest-feature reasoning (collision.c brush tests).
    // =================================================================================================
    private static float SignedDistanceToBrush(Vector3 p, Brush brush)
    {
        BrushPlane[] sides = brush.Sides;
        if (sides.Length == 0)
        {
            // Degenerate point brush (FromBox mins==maxs). Distance to that single point.
            return brush.Points.Length > 0 ? Vector3.Distance(p, brush.Points[0]) : float.MaxValue;
        }

        // inside-all-planes test → negative signed distance = max plane signed distance.
        float maxPlane = float.NegativeInfinity;
        bool inside = true;
        for (int i = 0; i < sides.Length; i++)
        {
            float s = Vector3.Dot(sides[i].Normal, p) - sides[i].Dist;
            if (s > maxPlane) maxPlane = s;
            if (s > 0f) inside = false;
        }
        if (inside)
            return maxPlane; // ≤ 0; the nearest face is at -maxPlane below

        // Outside: nearest point on the brush surface. Min over faces of point-to-face-polygon distance.
        Vector3[] points = brush.Points;
        float best = float.MaxValue;
        for (int i = 0; i < sides.Length; i++)
        {
            float d = DistanceToFacePolygon(p, sides[i], points);
            if (d < best) best = d;
        }
        return best;
    }

    private const float OnPlaneEps = 0.1f;

    /// <summary>
    /// Distance from <paramref name="p"/> to the convex face polygon on <paramref name="plane"/> — the subset
    /// of brush <paramref name="points"/> lying on the plane. Projects p onto the plane; if the projection is
    /// inside the polygon, the distance is just the perpendicular distance; otherwise it's the distance to the
    /// nearest polygon edge (segment). A face with &lt;3 on-plane points falls back to nearest-vertex distance.
    /// </summary>
    private static float DistanceToFacePolygon(Vector3 p, BrushPlane plane, Vector3[] points)
    {
        Vector3 n = plane.Normal;

        // Collect on-plane vertices (the face's corners) and their centroid. A convex brush face has very few
        // corners; a fixed stack buffer covers every real case, with a heap fallback for pathological brushes.
        // (Separate statements — a stackalloc inside a conditional expression isn't a valid span source.)
        const int StackCap = 64;
        Span<Vector3> polyStack = stackalloc Vector3[StackCap];
        Span<Vector3> poly = points.Length <= StackCap ? polyStack : new Vector3[points.Length];
        int count = 0;
        Vector3 centroid = Vector3.Zero;
        for (int i = 0; i < points.Length; i++)
        {
            if (MathF.Abs(Vector3.Dot(n, points[i]) - plane.Dist) <= OnPlaneEps)
            {
                poly[count] = points[i];
                centroid += points[i];
                count++;
            }
        }

        if (count == 0)
            return float.MaxValue; // shouldn't happen for a closed brush; caller mins it away
        if (count < 3)
        {
            // Degenerate face (edge or vertex): nearest of those points.
            float dv = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float d = Vector3.Distance(p, poly[i]);
                if (d < dv) dv = d;
            }
            return dv;
        }
        centroid /= count;

        // Order the face corners CCW around the plane normal so the inside-edge test is consistent.
        // Build an in-plane basis (u,v) and sort by angle about the centroid.
        Vector3 u = Vector3.Cross(n, Math.Abs(n.Z) < 0.9f ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0));
        float ulen2 = u.LengthSquared();
        if (ulen2 < 1e-12f) u = new Vector3(1, 0, 0); else u *= 1f / MathF.Sqrt(ulen2);
        Vector3 v = Vector3.Cross(n, u);

        Span<float> angStack = stackalloc float[StackCap];
        Span<float> ang = count <= StackCap ? angStack : new float[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 r = poly[i] - centroid;
            ang[i] = MathF.Atan2(Vector3.Dot(r, v), Vector3.Dot(r, u));
        }
        // simple insertion sort (faces have very few corners)
        for (int i = 1; i < count; i++)
        {
            float a = ang[i];
            Vector3 pt = poly[i];
            int j = i - 1;
            while (j >= 0 && ang[j] > a)
            {
                ang[j + 1] = ang[j];
                poly[j + 1] = poly[j];
                j--;
            }
            ang[j + 1] = a;
            poly[j + 1] = pt;
        }

        // Project p onto the plane.
        float signed = Vector3.Dot(n, p) - plane.Dist;
        Vector3 proj = p - n * signed;

        // Is the projection inside the convex polygon? (all cross products same sign about n)
        bool insidePoly = true;
        for (int i = 0; i < count; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % count];
            Vector3 edge = b - a;
            Vector3 toP = proj - a;
            // outward edge normal in-plane = cross(edge, n); proj inside if Dot(toP, outwardN) <= 0
            Vector3 outward = Vector3.Cross(edge, n);
            if (Vector3.Dot(toP, outward) > 1e-4f)
            {
                insidePoly = false;
                break;
            }
        }

        if (insidePoly)
            return MathF.Abs(signed); // perpendicular distance to the face

        // Otherwise nearest point on the polygon boundary (min over edge segments).
        float best = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % count];
            float d = DistanceToSegment(p, a, b);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Distance from <paramref name="p"/> to segment [<paramref name="a"/>,<paramref name="b"/>].</summary>
    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f)
            return Vector3.Distance(p, a);
        float t = Vector3.Dot(p - a, ab) / len2;
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        Vector3 closest = a + ab * t;
        return Vector3.Distance(p, closest);
    }
}
