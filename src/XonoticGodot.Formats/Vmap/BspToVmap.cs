using System.Numerics;
using XonoticGodot.Formats.Bsp;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// Imports a compiled <see cref="BspData"/> into the editable <see cref="VmapDocument"/> truth model — the
/// path that makes EVERY existing map editable (design doc §11.1), since a shipped Xonotic map only exists as
/// a <c>.bsp</c>.
///
/// A BSP keeps the brush plane sets that q3map2 compiled from, so brush geometry imports losslessly. What it
/// does NOT keep is the per-face texture ALIGNMENT: Radiant's texdef is consumed at compile time and baked
/// into each render vertex's UV. Rather than lose alignment (which would re-texture every wall at the wrong
/// scale and rotation), this importer RECOVERS the projection by least-squares fitting an affine
/// position→UV map from the compiled face's own vertices — see <see cref="TryFitProjection"/>. Faces with no
/// matching render surface (caulk, nodraw, fully-occluded sides) fall back to the axial box projection.
/// </summary>
public static class BspToVmap
{
    /// <summary>Plane-normal agreement required to consider a render face to lie on a brush side (cos of ~2.5°).</summary>
    private const float NormalMatchDot = 0.999f;

    /// <summary>Plane-distance tolerance, in Quake units, when matching a render face to a brush side.</summary>
    private const float DistMatchEpsilon = 0.25f;

    /// <summary>
    /// Convert <paramref name="bsp"/> into an editable document.
    /// </summary>
    /// <param name="bsp">The parsed BSP.</param>
    /// <param name="mapName">Short map name recorded in the manifest (e.g. "catharsis").</param>
    /// <param name="sourcePath">Virtual path of the source BSP, recorded for provenance.</param>
    /// <param name="sourceHash">Content hash of the source bytes (see <see cref="VmapPackage.HashBytes"/>).</param>
    /// <param name="droppedSubmodels">
    /// Inline model indices the active gametype filtered out (the same set the collision and render builders
    /// receive). Their brushes are SKIPPED: a gametype-conditional <c>func_wall</c> is not part of this
    /// session's map, so importing it drops textured, solid-looking brushes into the editor that float in
    /// mid-air with nothing to line up against — geometry belonging to a mode that is not running.
    /// </param>
    public static VmapDocument Import(BspData bsp, string mapName = "", string sourcePath = "",
        string sourceHash = "", IReadOnlySet<int>? droppedSubmodels = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        // Brush index -> owning inline model. Recorded rather than used to DROP anything: geometry that belongs
        // to another gametype is still real map data, and discarding it at import would silently destroy it on
        // the next save. The editor filters on this instead (see VmapBrush.SubmodelIndex).
        var brushSubmodel = new Dictionary<int, int>();
        for (int mi = 1; mi < bsp.Models.Length; mi++)
        {
            BspModel dm = bsp.Models[mi];
            for (int i = 0; i < dm.BrushCount; i++)
                brushSubmodel[dm.FirstBrush + i] = mi;
        }
        _ = droppedSubmodels;

        var doc = new VmapDocument
        {
            Manifest = new VmapManifest
            {
                Name = mapName,
                Title = mapName,
                SourceKind = "bsp",
                SourcePath = sourcePath,
                SourceHash = sourceHash,
            },
        };

        // A plane-indexed view of the compiled render faces, so each brush side can find the surface that was
        // generated from it and inherit its texture alignment.
        var faceIndex = new RenderFaceIndex(bsp);

        // ----- brushes: one VmapBrush per BSP brush, faces from its brush sides -----
        for (int bi = 0; bi < bsp.Brushes.Length; bi++)
        {
            VmapBrush? brush = ImportBrush(bsp, bi, faceIndex);
            if (brush is not null)
            {
                brush.SubmodelIndex = brushSubmodel.TryGetValue(bi, out int mi2) ? mi2 : 0;
                doc.Brushes.Add(brush);
            }
        }

        // ----- patches: one VmapPatch per BSP patch face (control points live in the vertex lump) -----
        int nextPatchId = 1;
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Patch)
                continue;
            VmapPatch? patch = ImportPatch(bsp, face, nextPatchId);
            if (patch is not null)
            {
                doc.Patches.Add(patch);
                nextPatchId++;
            }
        }

        ImportEntities(bsp, doc);
        return doc;
    }

    // =============================================================================================
    //  Brushes
    // =============================================================================================

    private static VmapBrush? ImportBrush(BspData bsp, int brushIndex, RenderFaceIndex faceIndex)
    {
        BspBrush b = bsp.Brushes[brushIndex];
        if (b.SideCount < 4)
            return null; // cannot bound a volume

        bool haveBrushTex = b.TextureIndex >= 0 && b.TextureIndex < bsp.Textures.Length;
        int brushContents = haveBrushTex ? bsp.Textures[b.TextureIndex].ContentFlags : Q3ContentsSolid;

        var brush = new VmapBrush
        {
            Id = brushIndex + 1, // 1-based; id 0 is reserved as "none"
            ContentFlags = brushContents,
            IsDetail = (brushContents & Q3ContentsDetail) != 0,
        };

        int end = b.FirstSide + b.SideCount;
        for (int s = b.FirstSide; s < end; s++)
        {
            if (s < 0 || s >= bsp.BrushSides.Length)
                continue;
            BspBrushSide side = bsp.BrushSides[s];
            if (side.PlaneIndex < 0 || side.PlaneIndex >= bsp.Planes.Length)
                continue;

            BspPlane p = bsp.Planes[side.PlaneIndex];
            bool haveSideTex = side.TextureIndex >= 0 && side.TextureIndex < bsp.Textures.Length;

            // v48/IG BSPs carry real per-side surface flags; older ones store -1 and inherit the texture's.
            int surfaceFlags = side.SurfaceFlags >= 0
                ? side.SurfaceFlags
                : (haveSideTex ? bsp.Textures[side.TextureIndex].SurfaceFlags : 0);
            string material = haveSideTex
                ? bsp.Textures[side.TextureIndex].ShaderName
                : (haveBrushTex ? bsp.Textures[b.TextureIndex].ShaderName : string.Empty);
            int contents = haveSideTex ? bsp.Textures[side.TextureIndex].ContentFlags : brushContents;

            var plane = new VmapPlane(p.Normal, p.Distance);

            // Recover the alignment from the compiled surface this side produced; fall back to axial mapping.
            VmapTexProjection projection =
                faceIndex.TryFindProjection(plane, side.TextureIndex, out VmapTexProjection fitted)
                    ? fitted
                    : VmapTexProjection.AxialFor(plane.Normal);

            brush.Faces.Add(new VmapFace
            {
                Plane = plane,
                Material = material,
                Projection = projection,
                SurfaceFlags = surfaceFlags,
                ContentFlags = contents,
            });
        }

        if (brush.Faces.Count < 4)
            return null;
        brush.IsToolBrush = brush.ClassifyToolBrush();
        return brush;
    }

    // =============================================================================================
    //  Patches
    // =============================================================================================

    private static VmapPatch? ImportPatch(BspData bsp, BspFace face, int id)
    {
        int w = face.PatchWidth, h = face.PatchHeight;
        if (w < 3 || h < 3 || (w & 1) == 0 || (h & 1) == 0)
            return null;
        if (face.FirstVertex < 0 || face.FirstVertex + w * h > bsp.Vertices.Length)
            return null;

        bool haveTex = face.TextureIndex >= 0 && face.TextureIndex < bsp.Textures.Length;
        var patch = new VmapPatch
        {
            Id = id,
            Width = w,
            Height = h,
            Material = haveTex ? bsp.Textures[face.TextureIndex].ShaderName : string.Empty,
            SurfaceFlags = haveTex ? bsp.Textures[face.TextureIndex].SurfaceFlags : 0,
            ContentFlags = haveTex ? bsp.Textures[face.TextureIndex].ContentFlags : Q3ContentsSolid,
        };

        for (int i = 0; i < w * h; i++)
        {
            BspVertex v = bsp.Vertices[face.FirstVertex + i];
            patch.Controls.Add(v.Position);
            patch.ControlUvs.Add(v.TexCoord);
        }
        return patch.IsValid ? patch : null;
    }

    // =============================================================================================
    //  Entities
    // =============================================================================================

    private static void ImportEntities(BspData bsp, VmapDocument doc)
    {
        int nextId = 1;
        foreach (IReadOnlyDictionary<string, string> raw in bsp.Entities)
        {
            var ent = new VmapEntity { Id = nextId++ };
            foreach (KeyValuePair<string, string> kv in raw)
                ent.Fields[kv.Key] = kv.Value;
            ent.Fields.TryGetValue("classname", out string? cls);
            ent.ClassName = cls ?? string.Empty;

            // A brush entity references an inline submodel as "*N"; that submodel owns a contiguous brush range.
            if (ent.Fields.TryGetValue("model", out string? model) && model.StartsWith('*')
                && int.TryParse(model.AsSpan(1), out int submodel)
                && submodel > 0 && submodel < bsp.Models.Length)
            {
                BspModel m = bsp.Models[submodel];
                for (int i = 0; i < m.BrushCount; i++)
                {
                    int brushIndex = m.FirstBrush + i;
                    if (brushIndex >= 0 && brushIndex < bsp.Brushes.Length)
                        ent.BrushIds.Add(brushIndex + 1); // match ImportBrush's 1-based ids
                }
            }

            doc.Entities.Add(ent);
        }
    }

    // =============================================================================================
    //  Texture-projection recovery
    // =============================================================================================

    /// <summary>
    /// Compiled render faces indexed by plane distance, so a brush side can locate the surface generated from
    /// it. Sorted by distance and binary-searched: BSP planes are deduplicated, so faces sharing a plane share
    /// an exact distance and cluster tightly.
    /// </summary>
    private sealed class RenderFaceIndex
    {
        private readonly BspData _bsp;
        private readonly Entry[] _entries;

        private readonly record struct Entry(float Dist, Vector3 Normal, int TextureIndex, int FaceIndex);

        public RenderFaceIndex(BspData bsp)
        {
            _bsp = bsp;
            var list = new List<Entry>(bsp.Faces.Length);
            for (int fi = 0; fi < bsp.Faces.Length; fi++)
            {
                BspFace face = bsp.Faces[fi];
                // Only planar surfaces carry a usable single-plane projection (patches curve; flares are sprites).
                if (face.Type is not (BspFaceType.Flat or BspFaceType.Mesh))
                    continue;
                if (face.VertexCount < 3 || face.FirstVertex < 0 || face.FirstVertex + face.VertexCount > bsp.Vertices.Length)
                    continue;

                if (!TryFacePlane(bsp, face, out Vector3 n, out float d))
                    continue;
                list.Add(new Entry(d, n, face.TextureIndex, fi));
            }
            list.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
            _entries = list.ToArray();
        }

        /// <summary>
        /// Find a render face lying on <paramref name="plane"/> (preferring one with the same texture) and fit
        /// its texture projection.
        /// </summary>
        public bool TryFindProjection(VmapPlane plane, int textureIndex, out VmapTexProjection projection)
        {
            projection = default;
            if (_entries.Length == 0)
                return false;

            int lo = LowerBound(plane.Dist - DistMatchEpsilon);
            float hi = plane.Dist + DistMatchEpsilon;

            int best = -1;
            bool bestTextureMatch = false;
            for (int i = lo; i < _entries.Length && _entries[i].Dist <= hi; i++)
            {
                Entry e = _entries[i];
                if (Vector3.Dot(e.Normal, plane.Normal) < NormalMatchDot)
                    continue;

                bool textureMatch = e.TextureIndex == textureIndex;
                // A same-texture surface is the surface this side actually generated; prefer it, but accept a
                // co-planar different-texture face as a fallback rather than dropping to axial mapping.
                if (best < 0 || (textureMatch && !bestTextureMatch))
                {
                    best = e.FaceIndex;
                    bestTextureMatch = textureMatch;
                    if (textureMatch)
                        break;
                }
            }

            if (best < 0)
                return false;

            BspFace face = _bsp.Faces[best];
            return TryFitProjection(_bsp, face, plane.Normal, out projection);
        }

        /// <summary>Index of the first entry whose distance is >= <paramref name="dist"/>.</summary>
        private int LowerBound(float dist)
        {
            int lo = 0, hi = _entries.Length;
            while (lo < hi)
            {
                int mid = (int)(((uint)lo + (uint)hi) >> 1);
                if (_entries[mid].Dist < dist)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }
    }

    /// <summary>
    /// The plane of a planar render face, taken from its vertex normal (all vertices of a q3map2 planar surface
    /// share it) with the distance from the first vertex.
    /// </summary>
    private static bool TryFacePlane(BspData bsp, BspFace face, out Vector3 normal, out float dist)
    {
        normal = bsp.Vertices[face.FirstVertex].Normal;
        float len = normal.Length();
        if (len < 1e-6f)
        {
            // Degenerate stored normal: derive one from the first non-collinear vertex triple.
            dist = 0f;
            return TryDeriveNormal(bsp, face, out normal, out dist);
        }
        normal /= len;
        dist = Vector3.Dot(bsp.Vertices[face.FirstVertex].Position, normal);
        return true;
    }

    private static bool TryDeriveNormal(BspData bsp, BspFace face, out Vector3 normal, out float dist)
    {
        normal = Vector3.Zero;
        dist = 0f;
        Vector3 a = bsp.Vertices[face.FirstVertex].Position;
        for (int i = 1; i < face.VertexCount - 1; i++)
        {
            Vector3 b = bsp.Vertices[face.FirstVertex + i].Position;
            Vector3 c = bsp.Vertices[face.FirstVertex + i + 1].Position;
            Vector3 n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            if (len < 1e-4f)
                continue;
            normal = n / len;
            dist = Vector3.Dot(a, normal);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Least-squares fit of an affine position→UV map over a face's vertices, expressed in the canonical
    /// <c>u = Dot(p, AxisU) + OffsetU</c> form.
    ///
    /// The fit runs in a 2D basis on the face plane (so the system is well-conditioned and the component of
    /// the axis along the normal — which is unobservable on a plane — comes out zero), solving
    /// <c>u = A*s + B*t + C</c> by 3x3 normal equations, then lifting (A,B,C) back to world space.
    /// </summary>
    public static bool TryFitProjection(BspData bsp, BspFace face, Vector3 normal, out VmapTexProjection projection)
    {
        projection = default;
        if (face.VertexCount < 3)
            return false;

        // Orthonormal basis on the plane.
        Vector3 seed = MathF.Abs(normal.Z) < 0.9f ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f);
        Vector3 t1 = Vector3.Cross(seed, normal);
        float t1Len = t1.Length();
        if (t1Len < 1e-6f)
            return false;
        t1 /= t1Len;
        Vector3 t2 = Vector3.Cross(normal, t1);

        Vector3 origin = bsp.Vertices[face.FirstVertex].Position;

        // Accumulate the normal-equation matrix for [s t 1] and both right-hand sides (u and v).
        double sss = 0, sst = 0, ss1 = 0, stt = 0, st1 = 0, s11 = 0;
        double su = 0, tu = 0, ou = 0, sv = 0, tv = 0, ov = 0;
        int n = 0;

        for (int i = 0; i < face.VertexCount; i++)
        {
            BspVertex vert = bsp.Vertices[face.FirstVertex + i];
            Vector3 rel = vert.Position - origin;
            double s = Vector3.Dot(rel, t1);
            double t = Vector3.Dot(rel, t2);
            double u = vert.TexCoord.X;
            double v = vert.TexCoord.Y;

            sss += s * s; sst += s * t; ss1 += s;
            stt += t * t; st1 += t; s11 += 1;
            su += s * u; tu += t * u; ou += u;
            sv += s * v; tv += t * v; ov += v;
            n++;
        }
        if (n < 3)
            return false;

        // Symmetric 3x3: [ sss sst ss1 ; sst stt st1 ; ss1 st1 s11 ]
        double det =
            sss * (stt * s11 - st1 * st1)
            - sst * (sst * s11 - st1 * ss1)
            + ss1 * (sst * st1 - stt * ss1);

        // A degenerate determinant means the vertices are collinear (or all coincident) — no unique fit.
        if (Math.Abs(det) < 1e-9)
            return false;

        if (!Solve3(sss, sst, ss1, stt, st1, s11, su, tu, ou, det, out double au, out double bu, out double cu))
            return false;
        if (!Solve3(sss, sst, ss1, stt, st1, s11, sv, tv, ov, det, out double av, out double bv, out double cv))
            return false;

        Vector3 axisU = t1 * (float)au + t2 * (float)bu;
        Vector3 axisV = t1 * (float)av + t2 * (float)bv;
        float offsetU = (float)cu - Vector3.Dot(origin, axisU);
        float offsetV = (float)cv - Vector3.Dot(origin, axisV);

        var fitted = new VmapTexProjection(axisU, axisV, offsetU, offsetV);
        if (!fitted.IsValid)
            return false;

        projection = fitted;
        return true;
    }

    /// <summary>Cramer solve of the symmetric normal-equation system with the shared determinant precomputed.</summary>
    private static bool Solve3(
        double m00, double m01, double m02, double m11, double m12, double m22,
        double r0, double r1, double r2, double det,
        out double x, out double y, out double z)
    {
        // Matrix is [ m00 m01 m02 ; m01 m11 m12 ; m02 m12 m22 ], right-hand side [r0 r1 r2].
        double dx = r0 * (m11 * m22 - m12 * m12)
                  - m01 * (r1 * m22 - m12 * r2)
                  + m02 * (r1 * m12 - m11 * r2);
        double dy = m00 * (r1 * m22 - m12 * r2)
                  - r0 * (m01 * m22 - m12 * m02)
                  + m02 * (m01 * r2 - r1 * m02);
        double dz = m00 * (m11 * r2 - r1 * m12)
                  - m01 * (m01 * r2 - r1 * m02)
                  + r0 * (m01 * m12 - m11 * m02);

        x = dx / det;
        y = dy / det;
        z = dz / det;
        return !(double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z));
    }

    // Q3 native content bits used here (mirrors of Engine's Q3Contents, kept local so Formats stays dependency-free).
    private const int Q3ContentsSolid = 1;
    private const int Q3ContentsDetail = 0x08000000;
}
