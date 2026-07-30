using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Particles;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  Faithful particle RENDERER — the draw-side mirror of DarkPlaces' R_DrawParticles /
//  R_DrawParticle_TransparentCallback (Base/darkplaces/cl_particles.c:2624-3162). The CPU pool
//  (ParticleSim.Pool) is drawn exactly the way DP draws it:
//
//  * ONE PREMULTIPLIED STREAM for alpha + additive particles. DP renders both through a single
//    GL_ONE / GL_ONE_MINUS_SRC_ALPHA blend ("we can group these because we premultiplied the texture
//    alpha", :2891): an alpha particle writes (rgb·a, a), an additive one writes (rgb·a, 0) — so one
//    depth-sorted instance stream composites fire and smoke per particle, which is what gives a rocket
//    explosion its dark-smoke-over-fire body. We replicate with one MultiMesh + blend_premul_alpha,
//    premultiplying the vertex color CPU-side and the texture alpha in the fragment stage.
//  * INVMOD exactly: GL_ZERO / GL_ONE_MINUS_SRC_COLOR == dst·(1−src) (:2877). Godot's blend_mul gives
//    dst·src, so the shader outputs (1 − tex·color) — per-channel exact. Drawn as a second batch.
//  * SORT KEY = the EFFECT's spawn center (particle_t.sortorigin, :3145 TRANSPARENTSORT_DISTANCE):
//    every particle of one burst carries the same key, so the burst sorts as a group and its particles
//    composite in POOL ORDER within it — i.e. effectinfo block order (fire first, black smoke on top,
//    sparks last). Ties broken by pool index, exactly DP's queue-insertion order.
//  * DP color math (:2643-2727): rgb = byte/256, alpha = min(1, alpha·cl_particles_alpha/256), the
//    near-clip FADE band between r_drawparticles_nearclip_min..max, and draw-time spin
//    angle + spin·(time − delayedspawn) (:2740) with stretch scaling the billboard X axis (:2752).
//    Colors are sRGB bytes (DP draws into a gamma framebuffer); Godot's pipeline is linear, and the
//    atlas is already sampled with source_color (sRGB→linear), so the vertex color is converted
//    sRGB→linear too — otherwise the weak channels render 2-3× too bright and fire washes to white.
//  * DP cull gates (:3134-3159): skip delayedspawn > time; hard near-clip at nearclip_min (fading up to
//    nearclip_max when max > min); drawdistance² · size² (big particles visible farther).
//
//  Per-instance data is uploaded with one RenderingServer.MultimeshSetBuffer per batch:
//  transform(12) + COLOR(4, premultiplied) + CUSTOM(4) = (cellSlot, angleRadians, sparkFlag, 0),
//  where cellSlot indexes a uniform array of atlas UV-rects and sparkFlag means "the transform already
//  carries a CPU-built basis (spark streak / oriented decal) — do NOT billboard".
//
//  Coordinate conversion to Godot happens here at the render boundary — the sim stays in Quake space.
// =====================================================================================================

/// <summary>Renders the faithful CPU particle pool the way DP does: one sorted premultiplied stream
/// (alpha+add) plus an exact-INVMOD multiply batch, over the particlefont atlas.</summary>
public sealed partial class FaithfulParticleRenderer : Node3D
{
    // 20 floats per instance: 12 transform + 4 color + 4 custom (Godot MultiMesh buffer layout when
    // TransformFormat=Transform3D, UseColors and UseCustomData are both enabled).
    private const int FloatsPerInstance = 20;

    // The DP particlefont index ranges packed into the render atlas: ALL sprite cells 0-95 (DP
    // MAX_PARTICLETEXTURES=96 — effects use up to the 90s: nex 65, electro bolts 70-74, debris 66-68).
    // The beam strips (200-205) are deliberately EXCLUDED: beams draw through the dedicated beam path
    // (this renderer skips Orientation.Beam), and one ~2048px-wide strip in the uniform slot grid blew
    // the atlas up to 32768px — past common GPU texture limits, corrupting every sprite sample.
    private static readonly (int Lo, int Hi)[] AtlasRanges = { (0, 96) };

    private sealed class Batch
    {
        public MultiMeshInstance3D Node = null!;
        public MultiMesh Mesh = null!;
        public ShaderMaterial Material = null!;
        public float[] Buffer = Array.Empty<float>();
        public int Count;
        // Scratch indices into the pool for this batch (filled each Sync, sorted, then packed).
        public readonly List<int> Indices = new();
        // (§11 R6) Capacity-decay bookkeeping: the upload is always the FULL capacity buffer (the
        // MultimeshSetBuffer contract), so capacity must track the RECENT peak, not the all-session peak.
        public int PeakSinceCheck;
        public float NextDecayAt;
    }

    // (§11 R6) Batch capacity policy: pre-size so a typical first burst never grows mid-burst, grow in
    // power-of-two steps (one realloc per doubling instead of one per new highwater), and every
    // DecaySeconds shrink back toward the recent peak so the steady-state per-frame upload (capacity ×
    // 80 B) tracks actual usage after a heavy firefight instead of staying at the session maximum.
    private const int InitialInstances = 512;
    private const float DecaySeconds = 10f;

    private Batch? _premul;   // DP GL_ONE / GL_ONE_MINUS_SRC_ALPHA — alpha AND additive particles
    private Batch? _invmod;   // DP GL_ZERO / GL_ONE_MINUS_SRC_COLOR — dst·(1−src) via blend_mul

    // The packed render atlas + per-cell normalized UV rects (slot index -> rect). texnum -> slot via _slotOf.
    private Texture2D? _atlasTex;
    private readonly Dictionary<int, int> _slotOf = new();   // DP texnum -> contiguous shader slot
    private Vector4[] _cellRects = Array.Empty<Vector4>();    // slot -> (u0, v0, du, dv) in atlas UV space
    private bool _built;

    /// <summary>The CLIENT cvar store for cl_particles_size/_alpha/draw-distance (set by the backend to
    /// MenuState.Cvars). Null falls back to Api.Cvars.</summary>
    public ICvarService? Cvars { get; set; }

    // Cached depth-sort state — the comparator reads these instead of capturing a closure, so Sync
    // allocates nothing per frame. Key: squared distance of the particle's SortOrg (the EFFECT center)
    // from the view origin, farthest first; ties by pool index ascending (DP queue-insertion order).
    private Particle[] _sortPool = Array.Empty<Particle>();
    private NVec3 _sortVo;
    private Comparison<int>? _depthCmp;

    private int CompareDepthFarthestFirst(int ia, int ib)
    {
        NVec3 da = _sortPool[ia].SortOrg - _sortVo, db = _sortPool[ib].SortOrg - _sortVo;
        float fa = NVec3.Dot(da, da), fb = NVec3.Dot(db, db);
        int c = fb.CompareTo(fa);                 // farthest first
        return c != 0 ? c : ia.CompareTo(ib);     // tie: pool order (spawn/block order within a burst)
    }

    public override void _Ready()
    {
        _premul = MakeBatch("premul", invmod: false);
        _invmod = MakeBatch("invmod", invmod: true);
        // BuildAtlas may have run before _Ready (the backend builds the atlas as soon as it has the font,
        // which can precede this node entering the tree). If an atlas is already packed, apply it now that
        // the batch materials exist.
        if (_atlasTex is not null)
        {
            ApplyAtlas(_premul);
            ApplyAtlas(_invmod);
            _built = true;
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  Atlas build — point the shader at one texture + a UV-rect table it indexes by slot.
    //  PREFERRED: ParticleFont surfaces its OWN source atlas (AtlasTexture) plus per-cell normalized rects
    //  (TryGetCellUv), so we sample the font's atlas directly — one shared texture, no per-cell crops or
    //  blits, exactly how DP keeps particlefont resident and samples cells by texcoord. We only build the
    //  texnum->slot table. LEGACY FALLBACK (a font predating that API, AtlasTexture null): re-pack the cells
    //  we need into our own grid atlas (~100 small blits) and record each cell's normalized rect.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Build the render atlas + UV table from the loaded <paramref name="font"/>. Safe to call
    /// repeatedly (rebuilds on a new font); no-op when the font isn't loaded (renderer draws nothing).</summary>
    public void BuildAtlas(ParticleFont? font)
    {
        _built = false;
        _slotOf.Clear();
        _atlasTex = null;
        _cellRects = Array.Empty<Vector4>();
        if (font is null || !font.Loaded)
            return;

        // Preferred path: sample the font's own atlas directly via its new AtlasTexture/TryGetCellUv API,
        // skipping the per-cell crops + blits below. Falls through to the re-pack only when that API yields
        // nothing (older font, or no resolvable cells in our ranges).
        if (TryBuildFromSharedAtlas(font))
            return;

        // Gather the cell images we can resolve, in a stable slot order.
        var cells = new List<(int Index, Image Img)>();
        foreach ((int lo, int hi) in AtlasRanges)
            for (int i = lo; i < hi; i++)
            {
                ImageTexture? t = font.Cell(i);
                Image? img = t?.GetImage();
                if (img is null || img.GetWidth() <= 0 || img.GetHeight() <= 0)
                    continue;
                if (img.IsCompressed())
                    img.Decompress();
                if (img.GetFormat() != Image.Format.Rgba8)
                    img.Convert(Image.Format.Rgba8);
                cells.Add((i, img));
            }
        if (cells.Count == 0)
            return;

        // Uniform grid sized to the largest cell, padded by 1px to stop bilinear bleed between slots.
        int cellW = 0, cellH = 0;
        foreach ((_, Image img) in cells)
        {
            cellW = Math.Max(cellW, img.GetWidth());
            cellH = Math.Max(cellH, img.GetHeight());
        }
        const int pad = 1;
        int slotW = cellW + pad * 2, slotH = cellH + pad * 2;
        int cols = Mathf.CeilToInt(MathF.Sqrt(cells.Count));
        int rows = Mathf.CeilToInt(cells.Count / (float)cols);
        int atlasW = NextPow2(cols * slotW), atlasH = NextPow2(rows * slotH);

        var atlas = Image.CreateEmpty(atlasW, atlasH, false, Image.Format.Rgba8);
        atlas.Fill(new Color(0, 0, 0, 0));
        _cellRects = new Vector4[cells.Count];

        for (int slot = 0; slot < cells.Count; slot++)
        {
            (int index, Image img) = cells[slot];
            int cx = (slot % cols) * slotW + pad;
            int cy = (slot / cols) * slotH + pad;
            int w = img.GetWidth(), h = img.GetHeight();
            atlas.BlitRect(img, new Rect2I(0, 0, w, h), new Vector2I(cx, cy));
            // Inset the sampled rect by a half-texel so bilinear never reaches the padding.
            float u0 = (cx + 0.5f) / atlasW;
            float v0 = (cy + 0.5f) / atlasH;
            float du = (w - 1f) / atlasW;
            float dv = (h - 1f) / atlasH;
            _cellRects[slot] = new Vector4(u0, v0, du, dv);
            _slotOf[index] = slot;
        }

        _atlasTex = ImageTexture.CreateFromImage(atlas);

        // Push the atlas + UV table into every batch material. If _Ready hasn't built the batches yet
        // (BuildAtlas can run before this node enters the tree), defer: _Ready re-applies once they exist.
        if (_premul is not null && _invmod is not null)
        {
            ApplyAtlas(_premul);
            ApplyAtlas(_invmod);
            _built = true;
        }
    }

    /// <summary>
    /// Preferred atlas build: bind the shader to the font's OWN atlas (<see cref="ParticleFont.AtlasTexture"/>)
    /// and fill the texnum->slot table from <see cref="ParticleFont.TryGetCellUv"/>, so the renderer samples
    /// cells straight out of the shared atlas — no per-cell crop/blit re-pack. Returns false (caller falls back
    /// to the re-pack) when the font predates this API or resolves no cells in <see cref="AtlasRanges"/>.
    /// </summary>
    private bool TryBuildFromSharedAtlas(ParticleFont font)
    {
        Texture2D? shared = font.AtlasTexture;
        if (shared is null)
            return false;
        float aw = shared.GetWidth(), ah = shared.GetHeight();
        if (aw <= 0f || ah <= 0f)
            return false;

        var rects = new List<Vector4>();
        foreach ((int lo, int hi) in AtlasRanges)
            for (int i = lo; i < hi; i++)
            {
                if (!font.TryGetCellUv(i, out Rect2 uv) || uv.Size.X <= 0f || uv.Size.Y <= 0f)
                    continue;
                // Inset the sampled rect by a half texel so bilinear never reaches a neighbouring cell: the
                // shared atlas packs cells edge-to-edge (no 1px padding gutter the re-pack inserts). Same
                // half-texel inset the re-pack path bakes into its rects.
                float u0 = uv.Position.X + 0.5f / aw;
                float v0 = uv.Position.Y + 0.5f / ah;
                float du = uv.Size.X - 1f / aw;
                float dv = uv.Size.Y - 1f / ah;
                _slotOf[i] = rects.Count;
                rects.Add(new Vector4(u0, v0, du, dv));
            }
        if (rects.Count == 0)
            return false;

        _atlasTex = shared;
        _cellRects = rects.ToArray();

        // Push into the batch materials if _Ready has built them; otherwise _Ready re-applies (same deferral
        // the re-pack path relies on when BuildAtlas runs before this node enters the tree).
        if (_premul is not null && _invmod is not null)
        {
            ApplyAtlas(_premul);
            ApplyAtlas(_invmod);
            _built = true;
        }
        return true;
    }

    private void ApplyAtlas(Batch b)
    {
        if (_atlasTex is null || b?.Material is null)
            return;
        b.Material.SetShaderParameter("albedo_tex", _atlasTex);
        // Pass the UV rects as a flat vec4 array uniform; the fragment stage indexes it by CUSTOM.x.
        var arr = new Godot.Collections.Array();
        foreach (Vector4 r in _cellRects)
            arr.Add(r);
        b.Material.SetShaderParameter("cell_rects", arr);
        b.Material.SetShaderParameter("cell_count", _cellRects.Length);
    }

    private static int NextPow2(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return Math.Max(2, p);
    }

    // ---------------------------------------------------------------------------------------------
    //  Batch + shader construction
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Standalone MultiMesh instances for the offscreen GPU warm pass (§11 R1): one node per live batch
    /// (premul + invmod) sharing that batch's exact <see cref="ShaderMaterial"/>, each carrying one
    /// billboard and one spark-flagged instance so both vertex-stage paths render. Drawing them in the
    /// warm viewport compiles the same pipelines the first real explosion needs — without this, faithful
    /// mode (the default) paid a first-use pipeline compile mid-play. Empty before <see cref="_Ready"/>.
    /// The warm pass parents, renders, and frees the returned nodes; the shared materials survive.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        var list = new List<Node3D>(2);
        foreach (Batch? b in new[] { _premul, _invmod })
        {
            if (b is null)
                continue;
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                UseCustomData = true,
                Mesh = new QuadMesh { Size = new Vector2(1f, 1f) },
                InstanceCount = 2,
            };
            var buf = new float[2 * FloatsPerInstance];
            // Instance 0: billboard (sparkFlag 0); instance 1: spark basis drawn verbatim (sparkFlag 1).
            WriteTransform(buf, 0, new Vector3(4f, 0, 0), new Vector3(0, 4f, 0), new Vector3(0, 0, 1f), Vector3.Zero);
            WriteColor(buf, 0, new Color(0.5f, 0.5f, 0.5f, 0.5f));
            WriteCustom(buf, 0, slot: 0, angle: 0.3f, sparkFlag: 0f);
            WriteTransform(buf, FloatsPerInstance, new Vector3(1f, 0, 0), new Vector3(0, 8f, 0), new Vector3(0, 0, 1f),
                new Vector3(6f, 0f, 0f));
            WriteColor(buf, FloatsPerInstance, new Color(0.5f, 0.5f, 0.5f, 0f));   // additive-style (alpha 0)
            WriteCustom(buf, FloatsPerInstance, slot: 0, angle: 0f, sparkFlag: 1f);
            RenderingServer.MultimeshSetBuffer(mm.GetRid(), buf);

            list.Add(new MultiMeshInstance3D
            {
                Name = "warm_" + b.Node.Name,
                Multimesh = mm,
                MaterialOverride = b.Material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                CustomAabb = new Aabb(new Vector3(-100f, -100f, -100f), new Vector3(200f, 200f, 200f)),
            });
        }
        return list;
    }

    private Batch MakeBatch(string name, bool invmod)
    {
        // 1x1 quad centered on origin; the vertex shader scales/billboards it. Two-sided so back-facing
        // billboards (and oriented sparks) still draw.
        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = quad,
            // (§11 R6) Pre-size so the GPU buffer exists before the first burst (no mid-burst realloc) —
            // VisibleInstanceCount 0 keeps the uninitialized instances from drawing until the first Sync.
            InstanceCount = InitialInstances,
            VisibleInstanceCount = 0,
        };

        var mat = new ShaderMaterial { Shader = ParticleShader(invmod) };
        if (invmod)
            // Multiplicative darkening composites over the premul stream (DP interleaves them in one
            // queue; a separate later batch is the accepted approximation — blood marks want to darken
            // what's under them).
            mat.RenderPriority = 1;

        var node = new MultiMeshInstance3D
        {
            Name = "fp_" + name,
            Multimesh = mm,
            MaterialOverride = mat,
            // Particles are emissive sprites: never cast/receive shadows, never affected by GI.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // A generous custom AABB so the renderer doesn't cull the whole batch when instances are far
            // from the node origin (we never recompute a tight AABB per frame).
            CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
        };
        AddChild(node);

        return new Batch { Node = node, Mesh = mm, Material = mat };
    }

    /// <summary>
    /// The inline draw shader. Vertex: when CUSTOM.z (sparkFlag) is 0 it builds a camera-facing quad from
    /// the instance origin + the X/Y basis lengths (X carries DP's stretch), rolled by CUSTOM.y (the
    /// draw-time angle incl. spin); when sparkFlag != 0 it draws the instance transform verbatim (the CPU
    /// baked the spark/oriented basis). Fragment replicates DP's PREMULTIPLIED particlefont
    /// (cl_particles.c:2891 "we premultiplied the texture alpha"): tex.rgb·tex.a × COLOR, where COLOR was
    /// premultiplied by particle alpha CPU-side (additive instances carry COLOR.a = 0).
    ///   premul batch: blend_premul_alpha == GL_ONE / GL_ONE_MINUS_SRC_ALPHA (DP :2891).
    ///   invmod batch: blend_mul outputs (1 − tex·COLOR) == GL_ZERO / GL_ONE_MINUS_SRC_COLOR (DP :2877).
    /// </summary>
    private static Shader ParticleShader(bool invmod)
    {
        string blendMode = invmod ? "blend_mul" : "blend_premul_alpha";
        string fragment = invmod
            // dst·(1−src): blend_mul gives dst·ALBEDO, so output the inverse-modulate factor directly.
            ? "    ALBEDO = vec3(1.0) - t.rgb * t.a * COLOR.rgb;\n" +
              "    ALPHA = 1.0;\n"
            // Premultiplied compositing: rgb is the full (already alpha-weighted) contribution; ALPHA only
            // controls how much of the destination is occluded (0 for additive).
            : "    ALBEDO = t.rgb * t.a * COLOR.rgb;\n" +
              "    ALPHA = t.a * COLOR.a;\n";
        return new Shader
        {
            Code =
                "shader_type spatial;\n" +
                // Depth TEST on (walls occlude), depth WRITE off for transparency (matches the growth
                // shader, EffectSystem.cs:1455-1458). Unshaded emissive sprites, two-sided.
                "render_mode " + blendMode + ", unshaded, cull_disabled, shadows_disabled, depth_draw_opaque;\n" +
                "uniform sampler2D albedo_tex : source_color, filter_linear;\n" +
                "uniform vec4 cell_rects[256];\n" +   // slot -> (u0, v0, du, dv); covers 0-95 + 200-205
                "uniform int cell_count = 0;\n" +
                "varying flat int v_slot;\n" +
                "void vertex() {\n" +
                "    int slot = int(INSTANCE_CUSTOM.x + 0.5);\n" +
                "    v_slot = clamp(slot, 0, max(cell_count - 1, 0));\n" +
                "    float angle = INSTANCE_CUSTOM.y;\n" +
                "    float spark = INSTANCE_CUSTOM.z;\n" +
                "    if (spark > 0.5) {\n" +
                // Spark/oriented: the CPU baked the basis into MODEL_MATRIX; draw it directly.
                "        MODELVIEW_MATRIX = VIEW_MATRIX * MODEL_MATRIX;\n" +
                "    } else {\n" +
                // Billboard: camera-facing axes scaled by the per-instance X/Y edge lengths (X carries
                // DP's stretch factor), then rolled by the draw-time angle (DP cl_particles.c:2740-2754).
                "        float sx = length(MODEL_MATRIX[0].xyz);\n" +
                "        float sy = length(MODEL_MATRIX[1].xyz);\n" +
                "        vec3 r = normalize(INV_VIEW_MATRIX[0].xyz) * sx;\n" +
                "        vec3 u = normalize(INV_VIEW_MATRIX[1].xyz) * sy;\n" +
                "        vec3 f = normalize(INV_VIEW_MATRIX[2].xyz);\n" +
                "        mat4 bb = mat4(vec4(r, 0.0), vec4(u, 0.0), vec4(f, 0.0), MODEL_MATRIX[3]);\n" +
                "        mat4 rot = mat4(vec4(cos(angle), -sin(angle), 0.0, 0.0), vec4(sin(angle), cos(angle), 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));\n" +
                "        MODELVIEW_MATRIX = VIEW_MATRIX * (bb * rot);\n" +
                "    }\n" +
                "}\n" +
                "void fragment() {\n" +
                "    vec4 rect = cell_rects[v_slot];\n" +     // (u0, v0, du, dv)
                "    vec2 uv = rect.xy + UV * rect.zw;\n" +
                "    vec4 t = texture(albedo_tex, uv);\n" +
                fragment +
                "}\n",
        };
    }

    // ---------------------------------------------------------------------------------------------
    //  Per-frame sync — cull, sort (DP transparent-queue semantics), pack, upload.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuild the batches from the live particles in <paramref name="pool"/> (scan
    /// [0, <paramref name="highWater"/>)). Replicates DP's queue gates (cl_particles.c:3134-3159):
    /// skip delayed spawns, hard near-clip at nearclip_min (with the nearclip_max fade band), and the
    /// size-scaled drawdistance² cull; sorts by effect center farthest-first with pool-order ties; packs
    /// DP's premultiplied vertex colors. <paramref name="viewOrigin"/>/<paramref name="viewForward"/> are
    /// QUAKE space; <paramref name="time"/> is the SIM clock (drives draw-time spin + the delayed gate).
    /// </summary>
    public void Sync(Particle[] pool, int highWater, NVec3 viewOrigin, NVec3 viewForward, float time)
    {
        Batch? premul = _premul, invmod = _invmod;
        if (!_built || premul is null || invmod is null || pool is null || highWater <= 0)
        {
            ClearBatch(premul);
            ClearBatch(invmod);
            return;
        }

        // cl_particles_size scales every particle's drawn size (cl_particles.c:2732).
        float sizeScale = ReadCvar(ParticleCvars.Size, 1f);
        if (sizeScale <= 0f) sizeScale = 1f;
        float alphaScale = MathF.Max(0f, ReadCvar(ParticleCvars.Alpha, 1f));

        // Near-clip band + size-scaled drawdistance (cl_particles.c:2655-2656, 3158).
        float nearMin = ReadCvar(ParticleCvars.NearClipMin, 4f);
        float nearMax = ReadCvar(ParticleCvars.NearClipMax, 4f);
        float drawDist = ReadCvar(ParticleCvars.DrawDistance, 2000f);
        float drawDistSq = drawDist > 0f ? drawDist * drawDist : 0f;   // 0 keeps "disabled" semantics
        NVec3 fwd = Normalize(viewForward);
        float planeStart = NVec3.Dot(viewOrigin, fwd) + nearMin;       // minparticledist_start
        float planeEnd = NVec3.Dot(viewOrigin, fwd) + nearMax;         // minparticledist_end
        bool doFade = planeStart < planeEnd;

        premul.Indices.Clear();
        invmod.Indices.Clear();

        // 1) Cull + bucket by blend state (DP groups INVMOD apart from the shared premultiplied stream).
        for (int i = 0; i < highWater; i++)
        {
            ref Particle p = ref pool[i];
            if (!p.Active || p.Alpha <= 0f)
                continue;
            if (p.DelayedSpawn > time)          // not yet spawned visually (:3134)
                continue;
            // Beams are drawn by the dedicated beam path; the faithful renderer handles billboard/spark/
            // oriented. Skip pure beams here (orientation == Beam) — they have no billboard form.
            if (p.Orientation == ParticleOrientation.Beam)
                continue;

            float along = NVec3.Dot(p.Org, fwd);
            if (along < planeStart)             // hard near cull (:3158, dot(org,fwd) >= start)
                continue;
            if (drawDistSq > 0f)
            {
                // DP: VectorDistance2(org, vieworg) < drawdist² · size² — big sprites stay visible farther.
                NVec3 d = p.Org - viewOrigin;
                float size = MathF.Max(p.Size * sizeScale, 0.0001f);
                if (NVec3.Dot(d, d) >= drawDistSq * size * size)
                    continue;
            }

            if (p.BlendMode == ParticleBlend.InvMod)
                invmod.Indices.Add(i);
            else
                premul.Indices.Add(i);
        }

        // 2) Sort both streams the way DP's transparent queue does: farthest SortOrg (the effect center)
        //    first, ties in pool order — a burst composites in its spawn/block order.
        _sortPool = pool;
        _sortVo = viewOrigin;
        _depthCmp ??= CompareDepthFarthestFirst;
        if (premul.Indices.Count > 1) premul.Indices.Sort(_depthCmp);
        if (invmod.Indices.Count > 1) invmod.Indices.Sort(_depthCmp);

        // 3) Pack + upload.
        PackAndUpload(premul, pool, sizeScale, alphaScale, fwd, time, planeStart, planeEnd, doFade, invmod: false);
        PackAndUpload(invmod, pool, sizeScale, alphaScale, fwd, time, planeStart, planeEnd, doFade, invmod: true);
    }

    private void PackAndUpload(Batch b, Particle[] pool, float sizeScale, float alphaScale, NVec3 viewFwd,
        float time, float planeStart, float planeEnd, bool doFade, bool invmod)
    {
        int n = b.Indices.Count;
        if (n == 0)
        {
            ClearBatch(b);
            return;
        }

        // Reuse the buffer; InstanceCount tracks the buffer capacity and VisibleInstanceCount limits
        // what's drawn, so the per-frame path allocates NOTHING (only a native marshal copy in the upload).
        // Capacity grows in power-of-two steps and decays toward the recent peak (§11 R6, fields above).
        int need = n * FloatsPerInstance;
        if (b.Buffer.Length < need)
        {
            int cap = Math.Max(InitialInstances, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)n));
            b.Buffer = new float[cap * FloatsPerInstance];
            b.Mesh.InstanceCount = cap;
            VortexArena.Common.Diagnostics.Prof.Event($"particles: {b.Node.Name} capacity -> {cap} (GPU realloc)");
        }
        b.PeakSinceCheck = Math.Max(b.PeakSinceCheck, n);
        if (time + DecaySeconds < b.NextDecayAt)
            b.NextDecayAt = time + DecaySeconds;   // sim clock went backwards (map change) — re-arm
        if (time >= b.NextDecayAt)
        {
            int capInst = b.Buffer.Length / FloatsPerInstance;
            int target = Math.Max(InitialInstances,
                (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, b.PeakSinceCheck)));
            if (target < capInst)
            {
                // One quiet realloc back to the recent peak's tier; PeakSinceCheck ≥ n, so target fits n.
                b.Buffer = new float[target * FloatsPerInstance];
                b.Mesh.InstanceCount = target;
                VortexArena.Common.Diagnostics.Prof.Event($"particles: {b.Node.Name} capacity decay -> {target}");
            }
            b.PeakSinceCheck = 0;
            b.NextDecayAt = time + DecaySeconds;
        }
        float[] buf = b.Buffer;

        for (int k = 0; k < n; k++)
        {
            ref Particle p = ref pool[b.Indices[k]];
            int o = k * FloatsPerInstance;

            int slot = _slotOf.TryGetValue(p.TexNum, out int s) ? s : 0;
            // Draw-time spin (cl_particles.c:2740): angle + spin·(time − delayedspawn), degrees → radians.
            float angle = (p.Angle + p.Spin * (time - p.DelayedSpawn)) * (MathF.PI / 180f);

            // DP vertex color (:2643-2727): alpha = min(1, alpha·cl_particles_alpha/256) with the near
            // fade band; rgb = byte/256, premultiplied by alpha. Colors are sRGB bytes (DP's gamma
            // framebuffer) → convert to linear for Godot's pipeline (the atlas is converted by
            // source_color already, so this keeps texture × color consistent).
            float alphaNorm = p.Alpha * alphaScale * (1f / 256f);
            if (doFade)
            {
                float along = NVec3.Dot(p.Org, viewFwd);
                alphaNorm *= MathF.Min(1f, (along - planeStart) / (planeEnd - planeStart));
            }
            if (alphaNorm > 1f) alphaNorm = 1f;

            float lr = SrgbToLinear(p.ColorR * (1f / 256f));
            float lg = SrgbToLinear(p.ColorG * (1f / 256f));
            float lb = SrgbToLinear(p.ColorB * (1f / 256f));

            // Premultiply (DP :2683 ADD, :2727 ALPHA, :2680 INVMOD). Additive carries vertex alpha 0 so
            // the premultiplied blend leaves the destination intact (pure add); invmod's COLOR is the
            // darkening factor.
            Color col;
            if (invmod)
                col = new Color(lr * alphaNorm, lg * alphaNorm, lb * alphaNorm, 1f);
            else if (p.BlendMode == ParticleBlend.Add)
                col = new Color(lr * alphaNorm, lg * alphaNorm, lb * alphaNorm, 0f);
            else
                col = new Color(lr * alphaNorm, lg * alphaNorm, lb * alphaNorm, alphaNorm);

            Vector3 gpos = Coords.ToGodot(p.Org);

            if (p.Orientation == ParticleOrientation.Spark)
            {
                // Velocity-stretched spark (cl_particles.c:2812-2825): half-length along the CURRENT
                // velocity = max(stretch · 0.04 · |vel|, size · 0.5); cross HALF-width = size —
                // R_CalcBeam_Vertex3f places corners at org ± size·right (gl_rmain.c:6269-6280), so the
                // full width is 2·size, same ± convention as DP's billboard corners. Build the basis
                // CPU-side so the shader draws it verbatim (sparkFlag = 1). The quad is 1x1 centered, so
                // the X axis becomes the full width and Y the full length.
                float size = p.Size * sizeScale;
                float speed = p.Vel.Length();
                float stretch = p.Stretch > 0f ? p.Stretch : 1f;
                float halfLen = MathF.Max(stretch * 0.04f * speed, size * 0.5f);
                float width = MathF.Max(size * 2f, 0.001f);

                Vector3 gvel = speed > 1e-4f ? Coords.ToGodot(p.Vel).Normalized() : Vector3.Up;
                Vector3 gfwd = Coords.ToGodot(viewFwd);
                Vector3 widthAxis = gvel.Cross(gfwd);
                if (widthAxis.LengthSquared() < 1e-6f)
                    widthAxis = gvel.Cross(Vector3.Right);
                widthAxis = widthAxis.Normalized();
                Vector3 faceAxis = widthAxis.Cross(gvel).Normalized();

                Vector3 xAxis = widthAxis * width;          // full width across the streak
                Vector3 yAxis = gvel * (halfLen * 2f);      // full length along velocity
                Vector3 zAxis = faceAxis;                   // unit normal (no scale needed)
                WriteTransform(buf, o, xAxis, yAxis, zAxis, gpos);
                WriteColor(buf, o, col);
                WriteCustom(buf, o, slot, angle, sparkFlag: 1f);
            }
            else if (p.Orientation == ParticleOrientation.Oriented)
            {
                // Oriented (double-sided) billboard fixed to a surface: orient by the velocity/normal the
                // sim carries in Vel (decal-style). Treat like a spark-flagged instance (no billboarding):
                // build a basis whose Z is the orientation normal. DP spans corners org ± right ± up with
                // right = baseright·size·stretch, up = baseup·size (cl_particles.c:2792-2794) — full edges
                // are 2·size·stretch and 2·size, like every other DP orientation.
                float size2 = p.Size * sizeScale;
                float stretch2 = p.Stretch != 0f ? MathF.Abs(p.Stretch) : 1f;
                Vector3 gnrm = p.Vel.LengthSquared() > 1e-6f ? Coords.ToGodot(p.Vel).Normalized() : Vector3.Up;
                Vector3 refv = MathF.Abs(gnrm.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
                Vector3 xa = refv.Cross(gnrm).Normalized() * (size2 * 2f * stretch2);
                Vector3 ya = gnrm.Cross(xa.Normalized()).Normalized() * (size2 * 2f);
                WriteTransform(buf, o, xa, ya, gnrm, gpos);
                WriteColor(buf, o, col);
                WriteCustom(buf, o, slot, angle, sparkFlag: 1f);
            }
            else
            {
                // Billboard: upload origin + the X/Y edge lengths. DP scales the X (right) axis by the
                // particle's stretch factor (:2752 right = left · size · stretch) — elliptical sprites.
                float size = p.Size * sizeScale;
                float stretch = p.Stretch != 0f ? MathF.Abs(p.Stretch) : 1f;
                float xEdge = MathF.Max(size * 2f * stretch, 0.001f);
                float yEdge = MathF.Max(size * 2f, 0.001f);
                WriteTransform(buf, o,
                    new Vector3(xEdge, 0, 0), new Vector3(0, yEdge, 0), new Vector3(0, 0, 1f), gpos);
                WriteColor(buf, o, col);
                WriteCustom(buf, o, slot, angle, sparkFlag: 0f);
            }
        }

        b.Mesh.VisibleInstanceCount = n;       // draw only the n filled instances (buffer may be larger)
        b.Count = n;

        // One upload of the reused buffer (length == InstanceCount*stride, the contract MultimeshSetBuffer
        // requires). No managed allocation here — only the native marshal copy.
        RenderingServer.MultimeshSetBuffer(b.Mesh.GetRid(), buf);
        b.Node.Visible = true;
    }

    private static void ClearBatch(Batch? b)
    {
        if (b is null) return;
        b.Count = 0;
        if (b.Mesh is not null)
            b.Mesh.VisibleInstanceCount = 0;
        if (b.Node is not null)
            b.Node.Visible = false;
    }

    // --- MultiMesh buffer writers (row-major Transform3D: 3 rows of [basisRow.x, .y, .z, origin]) -----

    private static void WriteTransform(float[] buf, int o, Vector3 xAxis, Vector3 yAxis, Vector3 zAxis, Vector3 origin)
    {
        // Godot MultiMesh stores the Transform3D as 3 rows; row r = (basis.x[r], basis.y[r], basis.z[r], origin[r]).
        buf[o + 0] = xAxis.X; buf[o + 1] = yAxis.X; buf[o + 2] = zAxis.X; buf[o + 3] = origin.X;
        buf[o + 4] = xAxis.Y; buf[o + 5] = yAxis.Y; buf[o + 6] = zAxis.Y; buf[o + 7] = origin.Y;
        buf[o + 8] = xAxis.Z; buf[o + 9] = yAxis.Z; buf[o + 10] = zAxis.Z; buf[o + 11] = origin.Z;
    }

    private static void WriteColor(float[] buf, int o, Color c)
    {
        buf[o + 12] = c.R; buf[o + 13] = c.G; buf[o + 14] = c.B; buf[o + 15] = c.A;
    }

    private static void WriteCustom(float[] buf, int o, int slot, float angle, float sparkFlag)
    {
        buf[o + 16] = slot; buf[o + 17] = angle; buf[o + 18] = sparkFlag; buf[o + 19] = 0f;
    }

    /// <summary>Standard sRGB → linear (the inverse of Godot's output transfer). DP composes raw sRGB
    /// bytes into a gamma framebuffer; converting here makes one particle on a dark background land on
    /// the same displayed value through Godot's linear pipeline (the atlas already converts via
    /// source_color, so texture × color stays consistent).</summary>
    private static float SrgbToLinear(float c)
        => c <= 0.04045f ? c * (1f / 12.92f) : MathF.Pow((c + 0.055f) * (1f / 1.055f), 2.4f);

    private float ReadCvar(string name, float fallback)
    {
        ICvarService? c = Cvars ?? (Api.Services is not null ? Api.Cvars : null);
        return c is null ? fallback : c.GetFloat(name);
    }

    private static NVec3 Normalize(NVec3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : new NVec3(1f, 0f, 0f);
    }
}
