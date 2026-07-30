using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  Faithful decal SPLATS — the C# mirror of Darkplaces' R_DecalSystem (gl_rmain.c:9134-9710), the "new
//  decal system" every particle decal routes through (CL_SpawnDecalParticleForSurface/ForPoint and
//  CL_ImmediateBloodStain all call R_DecalSystem_SplatEntities).
//
//  DP decals are NOT projected boxes: the decal quad is CLIPPED against the real surface triangles
//  around the impact, producing geometry that CONFORMS to the wall — it wraps across edges and short
//  ledges, and has no projection streaking. The clipped triangles draw with
//  GL_ZERO / GL_ONE_MINUS_SRC_COLOR (gl_rmain.c:9705):
//
//      wall' = wall · (1 − tex·color)
//
//  i.e. the splat color is the amount of light REMOVED — multiplicative darkening that can never
//  brighten the surface (a teal stain on a dark wall reads as a subtle teal-tinted burn, not a bright
//  cyan patch), with GL_PolygonOffset for the surface bias (:9702) and double-sided, depth-tested,
//  no-depth-write state. This node reproduces all of that:
//
//   * Geometry: brush faces from the static CollisionWorld overlapping the splat box are reconstructed
//     (the corner points lying on each side plane), clipped Sutherland–Hodgman against the splat's
//     6 box planes, fanned into triangles, and biased slightly off the surface (no polygon-offset in
//     Godot shaders — a small geometric push reads the same).
//   * Blend: an inline blend_mul shader outputs (1 − tex·color·fade) == DP's exact blendfunc.
//   * Color: callers pass the INVMOD removal color exactly as DP feeds SplatEntities — staintex stains
//     pre-complemented (1 − staincolor), blood/decal-block splats the raw particle color.
//   * Lifecycle: full strength for DecalTime, fading over FadeTime (cl_decals_time/_fadetime), capped.
//
//  When no CollisionWorld is wired (pure --connect client) the splat falls back to a single flat quad
//  perpendicular to the hit normal — still multiplicative, still streak-free, just not conforming.
// =====================================================================================================

/// <summary>Surface-conforming multiplicative decal splats (DP R_DecalSystem). One node per map session;
/// the faithful particle backend routes every pt_decal / stain / blood mark here.</summary>
public sealed partial class DecalSplats : Node3D
{
    /// <summary>Full-strength hold before fading (DP cl_decals_time 20, trimmed like the legacy Decals).</summary>
    [Export] public float DecalTime { get; set; } = 12f;

    /// <summary>Fade-out duration (DP cl_decals_fadetime).</summary>
    [Export] public float FadeTime { get; set; } = 2f;

    /// <summary>Hard cap on live splats; oldest culled past this (DP cl_decals_max, scaled down).</summary>
    [Export] public int MaxSplats { get; set; } = 256;

    /// <summary>DP cl_decals_newsystem_intensitymultiplier (default 2): boosts splat intensity so the
    /// multiplicative marks read at gameplay brightness.</summary>
    [Export] public float IntensityMultiplier { get; set; } = 2f;

    /// <summary>The static collision world supplying brush faces to conform to (wired at map load via
    /// <see cref="EffectSystem.SetCollisionWorld"/>). Secondary geometry source — the render-triangle soup
    /// from <see cref="SetGeometry"/> is preferred (DP splats the RENDER surfaces; collision brushes diverge
    /// on bevelled trim/patches/detail and the mark then stops at the wrong edge).</summary>
    public CollisionWorld? World { get; set; }

    // ---------------------------------------------------------------------------------------------
    //  Render-triangle soup — DP's actual splat target (R_DecalSystem_SplatEntities iterates the model's
    //  render surfaces). Built once per map from the BSP's worldspawn faces (incl. tessellated patches),
    //  indexed by a uniform grid for the per-splat AABB query.
    // ---------------------------------------------------------------------------------------------

    private const float GridCell = 256f;          // qu per grid cell
    private float[]? _tris;                       // 9 floats per triangle (Quake space)
    private readonly Dictionary<long, List<int>> _grid = new();
    private int[] _triStamp = Array.Empty<int>(); // per-tri visited stamp (dedup across cells)
    private int _stamp;

    /// <summary>
    /// Build the splat geometry from the map's RENDER surfaces (worldspawn model 0: flat/mesh faces +
    /// tessellated bezier patches) — the same triangles DP's decal system clips against. Faces whose
    /// texture is NOMARKS / sky / nodraw never take marks. Call once at map load.
    /// </summary>
    public void SetGeometry(BspData bsp)
    {
        var tris = new List<float>(65536);
        if (bsp.Models.Length > 0)
        {
            BspModel world = bsp.Models[0];
            int faceEnd = world.FirstFace + world.FaceCount;
            for (int fi = world.FirstFace; fi < faceEnd && fi < bsp.Faces.Length; fi++)
            {
                BspFace face = bsp.Faces[fi];
                if (SkipMarks(bsp, face.TextureIndex))
                    continue;

                if (face.Type is BspFaceType.Flat or BspFaceType.Mesh)
                {
                    int end = face.FirstIndex + face.IndexCount;
                    for (int e = face.FirstIndex; e + 2 < end + 0 && e + 2 < bsp.Triangles.Length; e += 3)
                    {
                        AddTri(tris, bsp, face, e);
                    }
                }
                else if (face.Type == BspFaceType.Patch)
                {
                    BezierPatch.Tessellation? tess = BezierPatch.Tessellate(face, bsp.Vertices);
                    if (tess is null)
                        continue;
                    for (int i = 0; i + 2 < tess.Indices.Count; i += 3)
                    {
                        AddVec(tris, tess.Vertices[tess.Indices[i]].Position);
                        AddVec(tris, tess.Vertices[tess.Indices[i + 1]].Position);
                        AddVec(tris, tess.Vertices[tess.Indices[i + 2]].Position);
                    }
                }
            }
        }

        _tris = tris.ToArray();
        int triCount = _tris.Length / 9;
        _triStamp = new int[triCount];
        _stamp = 0;
        _grid.Clear();
        for (int t = 0; t < triCount; t++)
        {
            int o = t * 9;
            float minX = MathF.Min(_tris[o], MathF.Min(_tris[o + 3], _tris[o + 6]));
            float maxX = MathF.Max(_tris[o], MathF.Max(_tris[o + 3], _tris[o + 6]));
            float minY = MathF.Min(_tris[o + 1], MathF.Min(_tris[o + 4], _tris[o + 7]));
            float maxY = MathF.Max(_tris[o + 1], MathF.Max(_tris[o + 4], _tris[o + 7]));
            float minZ = MathF.Min(_tris[o + 2], MathF.Min(_tris[o + 5], _tris[o + 8]));
            float maxZ = MathF.Max(_tris[o + 2], MathF.Max(_tris[o + 5], _tris[o + 8]));
            ForEachCell(minX, minY, minZ, maxX, maxY, maxZ, key =>
            {
                if (!_grid.TryGetValue(key, out List<int>? list))
                    _grid[key] = list = new List<int>(8);
                list.Add(t);
            });
        }
        GD.Print($"[DecalSplats] geometry: {triCount} render triangles, {_grid.Count} grid cells");
    }

    private static void AddTri(List<float> tris, BspData bsp, BspFace face, int e)
    {
        for (int k = 0; k < 3; k++)
        {
            int idx = e + k;
            int local = idx >= 0 && idx < bsp.Triangles.Length ? bsp.Triangles[idx] : 0;
            int src = face.FirstVertex + local;
            if (src < 0 || src >= bsp.Vertices.Length) src = 0;
            AddVec(tris, bsp.Vertices[src].Position);
        }
    }

    private static void AddVec(List<float> tris, System.Numerics.Vector3 v)
    {
        tris.Add(v.X);
        tris.Add(v.Y);
        tris.Add(v.Z);
    }

    /// <summary>Faces that never take decals: NOMARKS, sky, nodraw (DP filters hit surfaceflags).</summary>
    private static bool SkipMarks(BspData bsp, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;
        int sf = bsp.Textures[textureIndex].SurfaceFlags;
        return (sf & (Q3SurfaceFlags.NoMarks | Q3SurfaceFlags.Sky | Q3SurfaceFlags.NoDraw)) != 0;
    }

    private static void ForEachCell(float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        Action<long> visit)
    {
        int x0 = (int)MathF.Floor(minX / GridCell), x1 = (int)MathF.Floor(maxX / GridCell);
        int y0 = (int)MathF.Floor(minY / GridCell), y1 = (int)MathF.Floor(maxY / GridCell);
        int z0 = (int)MathF.Floor(minZ / GridCell), z1 = (int)MathF.Floor(maxZ / GridCell);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    visit(CellKey(x, y, z));
    }

    private static long CellKey(int x, int y, int z)
        => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    /// <summary>The particlefont atlas — splat textures are its raw cells (DP samples the same cells; the
    /// black background contributes zero removal under the multiplicative blend, so no alpha is needed).</summary>
    public ParticleFont? Font { get; set; }

    private Shader? _shader;

    // Scratch buffers reused per splat (single-threaded scene calls).
    private readonly List<Brush> _brushScratch = new(32);
    private readonly List<NVec3> _polyA = new(16);
    private readonly List<NVec3> _polyB = new(16);

    // ---------------------------------------------------------------------------------------------
    //  Public API
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Splat a mark onto the geometry around <paramref name="org"/> (Quake space), facing along
    /// <paramref name="dir"/> (the surface normal, or the impact velocity for blood smears — DP accepts
    /// either, cl_particles.c:3007). <paramref name="halfSize"/> is the mark half-extent (effectinfo
    /// size/stainsize); <paramref name="removal"/> is the INVMOD removal color (0..1, what the mark
    /// subtracts); <paramref name="alpha"/> 0..1 scales intensity; <paramref name="texnum"/> selects the
    /// particlefont cell.
    /// </summary>
    public void Splat(NVec3 org, NVec3 dir, float halfSize, Color removal, float alpha, int texnum)
    {
        if (alpha <= 0.005f)
            return;
        // A near-zero removal color subtracts nothing — invisible in DP, skip the geometry work.
        if (removal.R + removal.G + removal.B < 0.02f)
            return;
        halfSize = Math.Clamp(halfSize <= 0f ? 8f : halfSize, 1f, 256f);

        NVec3 n = Normalize(dir, new NVec3(0f, 0f, 1f));
        // DP VectorVectors: an arbitrary orthonormal right/up pair around the splat axis.
        NVec3 right = Normalize(MathF.Abs(n.Z) > 0.95f
            ? NVec3.Cross(new NVec3(1f, 0f, 0f), n)
            : NVec3.Cross(new NVec3(0f, 0f, 1f), n), new NVec3(1f, 0f, 0f));
        NVec3 up = NVec3.Cross(n, right);

        var verts = new List<Vector3>(24);
        var uvs = new List<Vector2>(24);
        var cols = new List<Color>(24);

        // Per-vertex intensity (R_DecalSystem_SplatTriangle, gl_rmain.c:9292-9297): the mark fades with
        // the vertex's distance from the impact plane ALONG the projection axis —
        // f = clamp(alpha · (1 − |axial|) · intensitymultiplier). This both softens the splat-box clip
        // boundary (no hard cutoff lines) and keeps far geometry inside the box from taking full marks.
        var ctx = new SplatContext(org, n, right, up, halfSize, removal, alpha * IntensityMultiplier);

        // Preferred geometry: the map's RENDER triangles (what DP clips against). The brush-face and flat-
        // quad fallbacks only engage when a geometry SOURCE is missing entirely — never on a per-splat clip
        // miss: when the soup exists but nothing clipped, the impact simply isn't on world geometry (an
        // entity hit, or off-surface), and DP emits NOTHING there (R_DecalSystem_SplatEntity marks surfaces
        // only). The old per-miss flat quad was exactly the floating through-wall mark of playtest #37 —
        // an unclipped plane hanging in space, poking through the corner it failed to conform to.
        if (_tris is not null && _tris.Length > 0)
            ClipSoupTriangles(in ctx, verts, uvs, cols);
        else if (World is not null)
            ClipBrushFaces(in ctx, verts, uvs, cols);
        else
            EmitQuad(in ctx, verts, uvs, cols); // no geometry at all (bare client) — soft legacy fallback

        if (verts.Count == 0)
            return; // nothing conformed → no mark (DP)

        AddSplatMesh(verts, uvs, cols, texnum);
    }

    /// <summary>Per-splat parameters threaded through the clip/emit helpers.</summary>
    private readonly struct SplatContext
    {
        public readonly NVec3 Org, N, Right, Up;
        public readonly float HalfSize;
        public readonly Color Removal;
        public readonly float Intensity;   // alpha · intensitymultiplier, pre-clamp (DP folds it into f)

        public SplatContext(NVec3 org, NVec3 n, NVec3 right, NVec3 up, float halfSize, Color removal, float intensity)
        {
            Org = org; N = n; Right = right; Up = up; HalfSize = halfSize; Removal = removal; Intensity = intensity;
        }
    }

    /// <summary>
    /// DP CL_SpawnDecalParticleForPoint (cl_particles.c:981): probe 32 random rays out to
    /// <paramref name="maxDist"/>, keep the nearest non-NOMARKS hit, and splat on that surface. Used by
    /// effectinfo <c>type decal</c> blocks (originjitter[0] is the reach). No surface → no mark.
    /// </summary>
    public void SplatPoint(NVec3 org, float maxDist, float halfSize, Color removal, float alpha, int texnum)
    {
        if (Api.Services is null)
            return;
        float dist = MathF.Max(maxDist, 4f);

        bool found = false;
        float bestFrac = 1f;
        NVec3 bestPos = org, bestNormal = new(0f, 0f, 1f);
        for (int i = 0; i < 32; i++)
        {
            NVec3 d = RandomUnitVector();
            TraceResult tr = Api.Trace.Trace(org, NVec3.Zero, NVec3.Zero, org + d * dist, MoveFilter.WorldOnly, null);
            if (tr.Fraction >= bestFrac)
                continue;
            if ((tr.DpHitContents & SuperContents.Sky) != 0 ||
                (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlags.NoMarks) != 0)
                continue;
            bestFrac = tr.Fraction;
            bestPos = tr.EndPos;
            bestNormal = tr.PlaneNormal;
            found = true;
        }
        if (found)
            Splat(bestPos, bestNormal, halfSize, removal, alpha, texnum);
    }

    /// <summary>Remove every live splat (map change). Pooled nodes survive for the next map.</summary>
    public void Clear()
    {
        for (int i = 0; i < _liveSlots.Count; i++)
            ReleaseSlot(_liveSlots[i]);
        _liveSlots.Clear();
    }

    // ---------------------------------------------------------------------------------------------
    //  Geometry — brush-face reconstruction + box clip (DP R_DecalSystem_SplatTriangle equivalent).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Clip the RENDER triangles overlapping the splat box (DP R_DecalSystem_SplatTriangle,
    /// gl_rmain.c:9240-9300: clip each surface triangle by the 6 box planes, emit what survives). No
    /// edge-on cull — DP keeps every surviving sliver; the axial falloff fades them naturally.</summary>
    private void ClipSoupTriangles(in SplatContext ctx, List<Vector3> verts, List<Vector2> uvs, List<Color> cols)
    {
        float h = ctx.HalfSize;
        NVec3 org = ctx.Org;
        _stamp++;
        int stamp = _stamp;
        float[] tris = _tris!;

        int x0 = (int)MathF.Floor((org.X - h) / GridCell), x1 = (int)MathF.Floor((org.X + h) / GridCell);
        int y0 = (int)MathF.Floor((org.Y - h) / GridCell), y1 = (int)MathF.Floor((org.Y + h) / GridCell);
        int z0 = (int)MathF.Floor((org.Z - h) / GridCell), z1 = (int)MathF.Floor((org.Z + h) / GridCell);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_grid.TryGetValue(CellKey(x, y, z), out List<int>? cell))
                        continue;
                    foreach (int t in cell)
                    {
                        if (_triStamp[t] == stamp)
                            continue;       // already processed via another overlapped cell
                        _triStamp[t] = stamp;

                        int o = t * 9;
                        _polyA.Clear();
                        _polyA.Add(new NVec3(tris[o], tris[o + 1], tris[o + 2]));
                        _polyA.Add(new NVec3(tris[o + 3], tris[o + 4], tris[o + 5]));
                        _polyA.Add(new NVec3(tris[o + 6], tris[o + 7], tris[o + 8]));
                        if (!ClipPolyAgainstBox(org, ctx.N, ctx.Right, ctx.Up, h))
                            continue;
                        for (int i = 2; i < _polyA.Count; i++)
                        {
                            EmitVertex(_polyA[0], in ctx, verts, uvs, cols);
                            EmitVertex(_polyA[i - 1], in ctx, verts, uvs, cols);
                            EmitVertex(_polyA[i], in ctx, verts, uvs, cols);
                        }
                    }
                }
    }

    private void ClipBrushFaces(in SplatContext ctx, List<Vector3> verts, List<Vector2> uvs, List<Color> cols)
    {
        float halfSize = ctx.HalfSize;
        NVec3 org = ctx.Org, n = ctx.N;
        var mins = new System.Numerics.Vector3(org.X - halfSize, org.Y - halfSize, org.Z - halfSize);
        var maxs = new System.Numerics.Vector3(org.X + halfSize, org.Y + halfSize, org.Z + halfSize);
        _brushScratch.Clear();
        World!.Query(mins, maxs, _brushScratch);

        foreach (Brush brush in _brushScratch)
        {
            if ((brush.Contents & SuperContents.Solid) == 0)
                continue;   // clip/trigger volumes leave no marks

            foreach (BrushPlane side in brush.Sides)
            {
                // Sky / NOMARKS faces never take marks (DP filters hitq3surfaceflags).
                if ((side.SurfaceFlags & (Q3SurfaceFlags.NoMarks | Q3SurfaceFlags.Sky)) != 0)
                    continue;
                // Skip faces nearly edge-on to the splat axis: they'd receive a sliver-stretched smear
                // (the streak artifact). |dot| accepts both normal- and velocity-direction conventions.
                if (MathF.Abs(NVec3.Dot(side.Normal, n)) < 0.06f)
                    continue;
                // Face plane must pass within the splat box at all.
                float planeDist = NVec3.Dot(side.Normal, org) - side.Dist;
                if (MathF.Abs(planeDist) > halfSize)
                    continue;

                // Reconstruct the face polygon: the brush corner points lying on this side's plane.
                _polyA.Clear();
                foreach (NVec3 p in brush.Points)
                    if (MathF.Abs(NVec3.Dot(side.Normal, p) - side.Dist) < 0.1f)
                        _polyA.Add(p);
                if (_polyA.Count < 3)
                    continue;
                WindAroundCentroid(_polyA, side.Normal);

                // Clip against the splat's 6 box planes (DP clips each surface triangle the same way).
                if (!ClipPolyAgainstBox(org, n, ctx.Right, ctx.Up, halfSize))
                    continue;

                // Fan-triangulate ON the surface — NO geometric displacement. DP separates decals from the
                // wall with GL_PolygonOffset only (gl_rmain.c:9702); a per-face push opens visible gaps at
                // shared edges (adjacent faces displaced apart). The shader applies the depth bias instead.
                for (int i = 2; i < _polyA.Count; i++)
                {
                    EmitVertex(_polyA[0], in ctx, verts, uvs, cols);
                    EmitVertex(_polyA[i - 1], in ctx, verts, uvs, cols);
                    EmitVertex(_polyA[i], in ctx, verts, uvs, cols);
                }
            }
        }
    }

    /// <summary>Clip the polygon in <see cref="_polyA"/> against the splat box (4 side planes + front/
    /// back along the axis). Result back in <see cref="_polyA"/>; false when fully clipped away.</summary>
    private bool ClipPolyAgainstBox(NVec3 org, NVec3 n, NVec3 right, NVec3 up, float halfSize)
    {
        // Each plane keeps the half-space dot(p, axis) <= dot(org, axis) + halfSize — its mirrored axis
        // covers the opposite side, so together the six bound the splat box.
        Span<(NVec3 N, float D)> planes = stackalloc (NVec3, float)[6]
        {
            (right, NVec3.Dot(right, org) + halfSize),
            (-right, -NVec3.Dot(right, org) + halfSize),
            (up, NVec3.Dot(up, org) + halfSize),
            (-up, -NVec3.Dot(up, org) + halfSize),
            (n, NVec3.Dot(n, org) + halfSize),
            (-n, -NVec3.Dot(n, org) + halfSize),
        };
        foreach ((NVec3 pn, float pd) in planes)
        {
            _polyB.Clear();
            int count = _polyA.Count;
            for (int i = 0; i < count; i++)
            {
                NVec3 a = _polyA[i];
                NVec3 b = _polyA[(i + 1) % count];
                float da = NVec3.Dot(pn, a) - pd;
                float db = NVec3.Dot(pn, b) - pd;
                bool ia = da <= 0f, ib = db <= 0f;
                if (ia)
                    _polyB.Add(a);
                if (ia != ib)
                {
                    float t = da / (da - db);
                    _polyB.Add(a + (b - a) * t);
                }
            }
            _polyA.Clear();
            _polyA.AddRange(_polyB);
            if (_polyA.Count < 3)
                return false;
        }
        return true;
    }

    /// <summary>Sort the on-plane corner points into a convex winding around their centroid.</summary>
    private static void WindAroundCentroid(List<NVec3> poly, NVec3 normal)
    {
        NVec3 c = default;
        foreach (NVec3 p in poly) c += p;
        c /= poly.Count;
        NVec3 axisA = Normalize(poly[0] - c, new NVec3(1f, 0f, 0f));
        NVec3 axisB = NVec3.Cross(normal, axisA);
        poly.Sort((p, q) =>
        {
            float ap = MathF.Atan2(NVec3.Dot(p - c, axisB), NVec3.Dot(p - c, axisA));
            float aq = MathF.Atan2(NVec3.Dot(q - c, axisB), NVec3.Dot(q - c, axisA));
            return ap.CompareTo(aq);
        });
    }

    private static void EmitVertex(NVec3 p, in SplatContext ctx, List<Vector3> verts, List<Vector2> uvs, List<Color> cols)
    {
        verts.Add(Coords.ToGodot(p));
        // Project into the splat plane: [-halfSize, +halfSize] → [0,1] (DP texcoord projection :9290).
        NVec3 d = p - ctx.Org;
        float u = NVec3.Dot(d, ctx.Right) / (2f * ctx.HalfSize) + 0.5f;
        float v = NVec3.Dot(d, ctx.Up) / (2f * ctx.HalfSize) + 0.5f;
        uvs.Add(new Vector2(u, v));
        // DP per-vertex falloff (:9293): f = clamp(alpha · (1 − |axial|) · intensitymultiplier) — fades the
        // mark with distance from the impact plane, so the splat-box clip boundary never shows a hard line.
        float axial = MathF.Abs(NVec3.Dot(d, ctx.N)) / ctx.HalfSize;
        float f = Math.Clamp(ctx.Intensity * (1f - axial), 0f, 1f);
        cols.Add(new Color(ctx.Removal.R * f, ctx.Removal.G * f, ctx.Removal.B * f));
    }

    private static void EmitQuad(in SplatContext ctx, List<Vector3> verts, List<Vector2> uvs, List<Color> cols)
    {
        NVec3 r = ctx.Right * ctx.HalfSize, u = ctx.Up * ctx.HalfSize;
        NVec3 center = ctx.Org;
        Span<NVec3> c = stackalloc NVec3[4] { center - r - u, center - r + u, center + r + u, center + r - u };
        int[] fan = { 0, 1, 2, 0, 2, 3 };
        foreach (int i in fan)
            EmitVertex(c[i], in ctx, verts, uvs, cols);
    }

    // ---------------------------------------------------------------------------------------------
    //  Mesh + material + lifecycle — pooled (PERFORMANCE_REPORT §11 R2). A splat acquires a pooled
    //  MeshInstance3D + ArrayMesh + ShaderMaterial slot (the mesh is rebuilt in place via ClearSurfaces +
    //  AddSurfaceFromArrays) and ONE _Process ager drives every live splat's hold/fade/release. Replaces
    //  the per-splat ArrayMesh/ShaderMaterial/MeshInstance3D/Tween allocation + QueueFree churn that made
    //  a rocket volley a node-churn spike (each impact splats several marks in one frame).
    // ---------------------------------------------------------------------------------------------

    // Shader uniform names cached per the §3.4 standing rule: never pass a string literal to a
    // StringName-typed Godot API from a per-frame path (_Process below pushes "fade" while splats decay).
    private static readonly StringName FadeParam = "fade";
    private static readonly StringName SplatTexParam = "splat_tex";
    private static readonly StringName HasTexParam = "has_tex";

    /// <summary>One pooled splat: the node plus its permanently-owned mesh/material, and the live age.</summary>
    private sealed class Slot
    {
        public MeshInstance3D Node = null!;
        public ArrayMesh Mesh = null!;
        public ShaderMaterial Mat = null!;
        public float Age;
    }

    private readonly List<Slot> _liveSlots = new();   // append order == age order (oldest first)
    private readonly Queue<Slot> _freeSlots = new();

    private Slot AcquireSlot()
    {
        if (_freeSlots.TryDequeue(out Slot? pooled))
            return pooled;
        var mesh = new ArrayMesh();
        // Draw BEFORE the particle batches (priority 0/1): DP renders decals during the per-surface pass,
        // ahead of the sorted transparent particles — smoke and fire composite OVER the marks, never under.
        var mat = new ShaderMaterial { Shader = _shader ??= SplatShader(), RenderPriority = -1 };
        var node = new MeshInstance3D
        {
            Name = "splat",
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false,
        };
        AddChild(node);
        return new Slot { Node = node, Mesh = mesh, Mat = mat };
    }

    private void ReleaseSlot(Slot s)
    {
        s.Node.Visible = false;
        s.Mesh.ClearSurfaces();   // drop the GPU vertex buffer while parked
        _freeSlots.Enqueue(s);
    }

    private void AddSplatMesh(List<Vector3> verts, List<Vector2> uvs, List<Color> cols, int texnum)
    {
        if (verts.Count < 3)
            return;

        Slot s = AcquireSlot();

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();   // removal · per-vertex falloff (DP c4f)
        s.Mesh.ClearSurfaces();
        s.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // The raw particlefont cell — under (1 − tex·color) its black background removes nothing, exactly
        // DP's "the particlefont does not need alpha on most textures". Solid fallback keeps marks alive
        // without the atlas.
        Texture2D? cell = Font?.Cell(texnum);
        s.Mat.SetShaderParameter(FadeParam, 1f);
        if (cell is not null)
            s.Mat.SetShaderParameter(SplatTexParam, cell);
        s.Mat.SetShaderParameter(HasTexParam, cell is not null);

        s.Age = 0f;
        s.Node.Visible = true;
        _liveSlots.Add(s);

        // Hard cap (DP cl_decals_max): retire the oldest. RemoveAt(0) is O(n) but n ≤ MaxSplats and this
        // only runs while saturated.
        while (_liveSlots.Count > MaxSplats)
        {
            ReleaseSlot(_liveSlots[0]);
            _liveSlots.RemoveAt(0);
        }
    }

    /// <summary>The one splat ager — hold for <see cref="DecalTime"/>, fade the multiplicative factor over
    /// <see cref="FadeTime"/>, release to the pool (cl_decals_time/_fadetime). During the hold nothing is
    /// pushed to the GPU (fade stays 1), so a screen full of fresh marks costs zero uniform traffic.</summary>
    public override void _Process(double delta)
    {
        int count = _liveSlots.Count;
        if (count == 0)
            return;
        using var _scope = VortexArena.Game.Client.FrameProfiler.Scope("decals.splat"); // [profiling] §18: out of proc:other
        float dt = (float)delta;
        for (int i = count - 1; i >= 0; i--)
        {
            Slot s = _liveSlots[i];
            s.Age += dt;
            if (s.Age <= DecalTime)
                continue;   // full strength — fade uniform already 1
            float fade = FadeTime > 0f ? 1f - (s.Age - DecalTime) / FadeTime : 0f;
            if (fade <= 0f)
            {
                ReleaseSlot(s);
                _liveSlots.RemoveAt(i);
                continue;
            }
            s.Mat.SetShaderParameter(FadeParam, fade);
        }
    }

    /// <summary>A standalone one-triangle splat for the offscreen GPU warm pass (§11 R1) sharing the SAME
    /// splat <see cref="Shader"/> the live pool uses, so the blend_mul splat pipeline compiles at map load
    /// instead of on the first impact mark. The warm pass parents, renders, and frees it.</summary>
    public MeshInstance3D BuildWarmupInstance()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(0f, 0f, 0f), new(8f, 0f, 0f), new(0f, 8f, 0f) };
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[] { new(0f, 0f), new(1f, 0f), new(0f, 1f) };
        arrays[(int)Mesh.ArrayType.Color] = new Color[] { new(0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f) };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var mat = new ShaderMaterial { Shader = _shader ??= SplatShader(), RenderPriority = -1 };
        mat.SetShaderParameter(HasTexParam, false);
        return new MeshInstance3D
        {
            Name = "warm_splat",
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
    }

    /// <summary>DP's decal draw state (gl_rmain.c:9699-9706): GL_ZERO/ONE_MINUS_SRC_COLOR == blend_mul of
    /// (1 − tex·color·fade), depth test on / write off, double-sided, unshaded, GL_PolygonOffset. Godot
    /// shaders have no polygon offset, so the vertex stage writes POSITION with a small constant NDC depth
    /// nudge toward the viewer (Godot 4 uses reversed-Z: nearer = larger depth) — the splat triangles lie
    /// EXACTLY on the surface geometrically (no per-face displacement → no gaps at shared edges).</summary>
    private static Shader SplatShader() => new()
    {
        Code =
            "shader_type spatial;\n" +
            "render_mode blend_mul, unshaded, cull_disabled, shadows_disabled, depth_draw_opaque;\n" +
            "uniform sampler2D splat_tex : source_color, filter_linear;\n" +
            "uniform bool has_tex = true;\n" +
            "uniform float fade = 1.0;\n" +
            "void vertex() {\n" +
            "    POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);\n" +
            "    POSITION.z += 0.0004 * POSITION.w;   // polygon-offset stand-in (reversed-Z: toward viewer)\n" +
            "}\n" +
            "void fragment() {\n" +
            "    vec3 t = has_tex ? texture(splat_tex, UV).rgb : vec3(1.0 - smoothstep(0.3, 0.5, distance(UV, vec2(0.5))));\n" +
            "    ALBEDO = vec3(1.0) - t * COLOR.rgb * fade;\n" +
            "    ALPHA = 1.0;\n" +
            "}\n",
    };

    private static NVec3 RandomUnitVector()
    {
        for (int tries = 0; tries < 16; tries++)
        {
            float x = (float)GD.RandRange(-1.0, 1.0);
            float y = (float)GD.RandRange(-1.0, 1.0);
            float z = (float)GD.RandRange(-1.0, 1.0);
            float l2 = x * x + y * y + z * z;
            if (l2 > 0.0001f && l2 <= 1f)
                return new NVec3(x, y, z) / MathF.Sqrt(l2);
        }
        return new NVec3(0f, 0f, 1f);
    }

    private static NVec3 Normalize(NVec3 v, NVec3 fallback)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : fallback;
    }
}
