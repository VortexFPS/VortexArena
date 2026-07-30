using System.Numerics;
using VortexArena.Formats.Bsp;

namespace VortexArena.Formats.Vmap;

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
    /// Largest UV error, in texture widths, for a fitted projection to count as a genuine recovery of its
    /// source face's alignment. Tight on purpose: an eighth of a texture is already a visible seam, and a
    /// wrong-but-accepted fit is worse than no fit, because the axial fallback is at least predictable.
    /// </summary>
    private const float FitResidualEpsilon = 0.01f;

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

        // Parallel to brush.Faces: the BSP texture index each side was built from. Kept local rather than on
        // VmapFace because it is an artefact of THIS importer — the truth model refers to shaders by name, and
        // a persisted lump index would be meaningless the moment the document is edited or saved.
        var sideTextures = new List<int>(b.SideCount);

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

            brush.Faces.Add(new VmapFace
            {
                Plane = plane,
                Material = material,
                // Left INVALID on purpose. RecoverProjections fills in the real alignment below; a side it
                // cannot match keeps "unknown" rather than a plausible-looking axial guess, so the document
                // records what it actually knows and the geometry builder applies the axial fallback at draw
                // time. Baking the guess in here would make an unrecovered side indistinguishable from a
                // recovered one, both in the file and to anyone trying to measure the recovery rate.
                Projection = default,
                SurfaceFlags = surfaceFlags,
                ContentFlags = contents,
            });
            sideTextures.Add(side.TextureIndex);
        }

        if (brush.Faces.Count < 4)
            return null;
        brush.IsToolBrush = brush.ClassifyToolBrush();
        RecoverProjections(brush, sideTextures, faceIndex);
        return brush;
    }

    /// <summary>
    /// Replace each side's placeholder axial mapping with the alignment recovered from the compiled surface it
    /// generated.
    ///
    /// Deliberately a second pass: identifying the right source face needs the side's WINDING centre (see
    /// <see cref="RenderFaceIndex.TryFindProjection"/>), and a winding cannot be evaluated until every plane of
    /// the brush is known. Sides whose winding is empty — bevel planes, fully clipped sides — keep the axial
    /// fallback, which is all they can have and all they need, since they generate no surface.
    /// </summary>
    private static void RecoverProjections(VmapBrush brush, List<int> sideTextures, RenderFaceIndex faceIndex)
    {
        Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);

        for (int i = 0; i < brush.Faces.Count && i < windings.Length; i++)
        {
            Vector3[] w = windings[i];
            VmapFace face = brush.Faces[i];
            if (faceIndex.TryFindProjection(face.Plane, sideTextures[i], w.Length >= 3 ? w : null,
                    out VmapTexProjection fitted))
                face.Projection = fitted;
        }
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

        /// <summary>Reused candidate buffer — this runs once per brush side of the whole map.</summary>
        private readonly List<(int Rank, float Distance, int FaceIndex)> _candidates = new();

        private readonly record struct Entry(float Dist, Vector3 Normal, int TextureIndex, int FaceIndex, Vector3 Centre);

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

                Vector3 centre = Vector3.Zero;
                for (int v = 0; v < face.VertexCount; v++)
                    centre += bsp.Vertices[face.FirstVertex + v].Position;
                centre /= face.VertexCount;

                list.Add(new Entry(d, n, face.TextureIndex, fi, centre));
            }
            list.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
            _entries = list.ToArray();
        }

        /// <summary>
        /// Find the render face this brush side generated and fit its texture projection.
        /// </summary>
        /// <param name="plane">The brush side's plane.</param>
        /// <param name="textureIndex">The side's texture, preferred over a co-planar different-texture face.</param>
        /// <param name="near">
        /// A point on the side (its winding centre) when one is known. Plane and texture ALONE do not identify
        /// the source face: a long wall is normally several brushes sharing one plane and one texture, each with
        /// its own texdef, so taking the first match hands the side a neighbour's alignment and the texture
        /// lands shifted. Measured on stormkeep, that was 703 of 4263 matched faces. Pass <c>null</c> only when
        /// no winding is available yet.
        /// </param>
        public bool TryFindProjection(VmapPlane plane, int textureIndex, IReadOnlyList<Vector3>? winding,
            out VmapTexProjection projection)
        {
            Vector3? near = null;
            if (winding is { Count: >= 3 })
            {
                Vector3 sum = Vector3.Zero;
                foreach (Vector3 v in winding)
                    sum += v;
                near = sum / winding.Count;
            }

            projection = default;
            if (_entries.Length == 0)
                return false;

            int lo = LowerBound(plane.Dist - DistMatchEpsilon);
            float hi = plane.Dist + DistMatchEpsilon;

            // Rank every co-planar candidate, then take the first whose fit VALIDATES. Committing to a single
            // best candidate loses the side's alignment entirely when that one happens to be an unfittable
            // triangle-soup surface, even though a perfectly good planar face sits on the same plane.
            _candidates.Clear();
            for (int i = lo; i < _entries.Length && _entries[i].Dist <= hi; i++)
            {
                Entry e = _entries[i];
                if (Vector3.Dot(e.Normal, plane.Normal) < NormalMatchDot)
                    continue;
                // Rank on COVERAGE first: the face that generated this side is the one whose triangles the
                // side's centre actually lands on. Centroid distance alone picks the wrong neighbour whenever
                // the true source is long or L-shaped — its centroid can sit further away than a smaller
                // unrelated face's — and the side then inherits a valid alignment belonging to someone else.
                // Only the side's OWN shader. A co-planar face of a different shader was previously accepted as
                // a fallback, on the theory that any real alignment beats the axial guess; it does not. Its
                // texdef belongs to a different surface, so it lands the texture somewhere arbitrary, and
                // unlike the axial fallback it looks deliberate.
                if (e.TextureIndex != textureIndex)
                    continue;
                bool covers = near is { } q && FaceCovers(_bsp, _bsp.Faces[e.FaceIndex], q);
                _candidates.Add((
                    covers ? 0 : 1,
                    near is { } p ? Vector3.DistanceSquared(e.Centre, p) : 0f,
                    e.FaceIndex));
            }
            if (_candidates.Count == 0)
                return false;

            _candidates.Sort(static (a, b) =>
            {
                int byRank = a.Rank.CompareTo(b.Rank);
                return byRank != 0 ? byRank : a.Distance.CompareTo(b.Distance);
            });

            foreach ((int _, float _, int faceIndex) in _candidates)
                if (TryFitProjection(_bsp, _bsp.Faces[faceIndex], plane.Normal, winding, out projection))
                    return true;

            return false;
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
    /// <summary>
    /// Mark the face vertices belonging to triangles that lie inside <paramref name="winding"/> — the part of a
    /// merged draw surface that this brush side is responsible for. Returns null when no winding was given or
    /// when fewer than three vertices qualify, meaning "use the whole face".
    /// </summary>
    private static bool[]? SelectCoveredVertices(BspData bsp, BspFace face, IReadOnlyList<Vector3>? winding)
    {
        if (winding is null || winding.Count < 3)
            return null;

        var use = new bool[face.VertexCount];
        int count = 0;

        for (int i = 0; i + 2 < face.IndexCount; i += 3)
        {
            int i0 = face.FirstIndex + i;
            if (i0 + 2 >= bsp.Triangles.Length)
                break;
            int a = bsp.Triangles[i0], b = bsp.Triangles[i0 + 1], c = bsp.Triangles[i0 + 2];
            if (a >= face.VertexCount || b >= face.VertexCount || c >= face.VertexCount)
                continue;

            Vector3 centre = (bsp.Vertices[face.FirstVertex + a].Position
                + bsp.Vertices[face.FirstVertex + b].Position
                + bsp.Vertices[face.FirstVertex + c].Position) / 3f;
            if (!WindingContains(winding, centre))
                continue;

            if (!use[a]) { use[a] = true; count++; }
            if (!use[b]) { use[b] = true; count++; }
            if (!use[c]) { use[c] = true; count++; }
        }

        return count >= 3 ? use : null;
    }

    /// <summary>Point-in-convex-polygon test for a brush-side winding, both already on the same plane.</summary>
    private static bool WindingContains(IReadOnlyList<Vector3> winding, Vector3 point)
    {
        // The winding's own plane normal, from the first non-degenerate corner.
        Vector3 n = Vector3.Zero;
        for (int i = 1; i + 1 < winding.Count && n.LengthSquared() < 1e-12f; i++)
            n = Vector3.Cross(winding[i] - winding[0], winding[i + 1] - winding[0]);
        if (n.LengthSquared() < 1e-12f)
            return false;
        n = Vector3.Normalize(n);

        for (int i = 0; i < winding.Count; i++)
        {
            Vector3 a = winding[i], b = winding[(i + 1) % winding.Count];
            // Slack of half a unit: a triangle centre can sit exactly on a shared edge.
            if (Vector3.Dot(Vector3.Cross(b - a, point - a), n) < -0.5f * (b - a).Length())
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies on one of the face's triangles (both are already known to share a
    /// plane, so this is a 2D containment test done with 3D barycentrics).
    /// </summary>
    private static bool FaceCovers(BspData bsp, BspFace face, Vector3 point)
    {
        const float Slack = 0.5f;   // a hair outside still counts: the side's centre can land on a shared edge

        for (int i = 0; i + 2 < face.IndexCount; i += 3)
        {
            int i0 = face.FirstIndex + i;
            if (i0 + 2 >= bsp.Triangles.Length)
                break;
            Vector3 a = bsp.Vertices[face.FirstVertex + bsp.Triangles[i0]].Position;
            Vector3 b = bsp.Vertices[face.FirstVertex + bsp.Triangles[i0 + 1]].Position;
            Vector3 c = bsp.Vertices[face.FirstVertex + bsp.Triangles[i0 + 2]].Position;

            Vector3 n = Vector3.Cross(b - a, c - a);
            float area2 = n.Length();
            if (area2 < 1e-6f)
                continue;
            n /= area2;

            // Inside when the point is on the inner side of all three edges, with a little slack.
            float e0 = Vector3.Dot(Vector3.Cross(b - a, point - a), n);
            float e1 = Vector3.Dot(Vector3.Cross(c - b, point - b), n);
            float e2 = Vector3.Dot(Vector3.Cross(a - c, point - c), n);
            float tolerance = -Slack * area2;
            if (e0 >= tolerance && e1 >= tolerance && e2 >= tolerance)
                return true;
        }
        return false;
    }

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
        => TryFitProjection(bsp, face, normal, null, out projection);

    /// <summary>
    /// As above, but fitting only the part of the face that lies within <paramref name="winding"/>.
    ///
    /// A compiled face is NOT necessarily one texdef. q3map2's meta pass merges coplanar surfaces that share a
    /// shader into a single draw surface, so one face can carry several alignments — stormkeep has a 12-vertex
    /// trim face whose UVs no single affine map fits within 3.2 texture widths. Fitting the whole thing either
    /// yields a wrong map or (with validation) yields none at all, and the side falls back to a box projection
    /// or, worse, to a co-planar face of a different shader. Restricting the fit to the triangles the brush
    /// side actually covers picks out the one alignment that belongs to it.
    /// </summary>
    public static bool TryFitProjection(
        BspData bsp, BspFace face, Vector3 normal, IReadOnlyList<Vector3>? winding, out VmapTexProjection projection)
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
        bool[]? originUse = SelectCoveredVertices(bsp, face, winding);
        if (originUse is not null)
            for (int i = 0; i < face.VertexCount; i++)
                if (originUse[i]) { origin = bsp.Vertices[face.FirstVertex + i].Position; break; }

        // Accumulate the normal-equation matrix for [s t 1] and both right-hand sides (u and v).
        double sss = 0, sst = 0, ss1 = 0, stt = 0, st1 = 0, s11 = 0;
        double su = 0, tu = 0, ou = 0, sv = 0, tv = 0, ov = 0;
        int n = 0;

        // Which of the face's vertices take part: those of triangles the brush side covers, or all of them
        // when no winding was supplied (or none of the triangles land inside it).
        bool[]? use = originUse;

        for (int i = 0; i < face.VertexCount; i++)
        {
            if (use is not null && !use[i])
                continue;
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

        // VERIFY the fit against the data it came from. A least-squares solve always returns something, and an
        // affine position→UV map only exists if the source really is one planar surface with one texdef. A
        // q3map2 -meta surface is a triangle SOUP — several coplanar-ish faces welded together, sometimes with
        // different alignments — and fitting one yields a plausible-looking projection whose axes are skewed
        // out of the plane. That is silent: the geometry is right, the texture is simply wrong, and the only
        // way to tell is to ask whether the projection reproduces the UVs it was derived from.
        for (int i = 0; i < face.VertexCount; i++)
        {
            if (use is not null && !use[i])
                continue;
            BspVertex v = bsp.Vertices[face.FirstVertex + i];
            if ((fitted.Evaluate(v.Position) - v.TexCoord).Length() > FitResidualEpsilon)
                return false;
        }

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
