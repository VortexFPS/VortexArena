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
        // One MultiMesh per capacity tier (TierCaps), all drawn by the same material from the same place; only
        // the smallest tier that holds this frame's instances is shown and uploaded. See TierCaps.
        public MultiMeshInstance3D[] Nodes = Array.Empty<MultiMeshInstance3D>();
        public Rid[] Meshes = Array.Empty<Rid>();
        public string Name = "";
        public ShaderMaterial Material = null!;
        public float[] Buffer = Array.Empty<float>();
        public int Count;
        // What the engine was last told, so that a frame that changes neither makes no call for it.
        public int ShownTier = -1, ShownCount = -1;
        // Scratch indices into the pool for this batch (filled each Sync, sorted, then packed).
        public int[] Indices = new int[256];
        public int IndexCount;

        public void Add(int i)
        {
            if (IndexCount == Indices.Length) Array.Resize(ref Indices, Indices.Length * 2);
            Indices[IndexCount++] = i;
        }
    }

    // (hitch fix 2026-08-03) Fixed instance capacity — see the note in the pack loop. Sized to cover a
    // heavy firefight in one allocation so MultiMesh.InstanceCount is never written after creation (that
    // write is a GPU buffer realloc, and a render-thread rendezvous once thread_model=Separate is on).
    // 8192 is 2x the largest capacity the old grow path was observed reaching (4096) on stormkeep.
    private const int MaxInstances = 8192;

    // (2026-10-08) The instance buffer of a MultiMesh can only be replaced whole (MultimeshSetBuffer takes
    // InstanceCount x stride floats), so with one MultiMesh of 8,192 instances every frame with a single live
    // particle copied 640 KB to the engine, which copied it to the render thread, which sent it to the GPU:
    // most of what a frame of particles cost. Each batch therefore keeps one MultiMesh per capacity below, made
    // once and never resized (the 2026-08-03 rule stands), and a frame uploads to the smallest that holds it -
    // 10 KB for a hundred particles. The instances, their order, the material and the node's place and bounds
    // are the same whichever tier draws them, so the picture is the same.
    private static readonly int[] TierCaps = { 128, 512, 2048, MaxInstances };

    // sRGB byte / 256 -> linear, exactly SrgbToLinear(b * (1f / 256f)) for each of the 256 inputs.
    private static readonly float[] s_srgbByteToLinear = BuildSrgbTable();

    private static float[] BuildSrgbTable()
    {
        float[] t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = SrgbToLinear(i * (1f / 256f));
        return t;
    }

    // DP texnum (a byte) -> shader slot; 0 for a cell the atlas does not hold (what the dictionary miss gave).
    private readonly int[] _slotTable = new int[256];

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
    // (hitch fix 2026-08-03) Depth sort WITHOUT a comparison delegate. The old shape —
    // List<int>.Sort(CompareDepthFarthestFirst) — recomputed BOTH particles' view distances on every one of
    // the ~n·log n comparisons, each a random ~100-byte-stride struct access (a cache miss), through a
    // delegate call. At a burst-filled pool that is over a million delegate comparisons and two million
    // distance computes in one frame: measured as the residual 400-840 ms particles.cpu freezes on stormkeep
    // AFTER the sim's trace/content budgets landed (watchdog 3656/3664 samples in particles.cpu with both
    // budgets capped). Now each particle's distance is computed ONCE into a sortable ulong key —
    //   [ ~bits(distSq) : 32 ][ pool index : 32 ]
    // — and Array.Sort(keys, indices) runs the tight primitive-key introsort. distSq is non-negative, and
    // IEEE bit patterns of non-negative floats are order-isomorphic to their values, so ~bits gives
    // ascending = FARTHEST FIRST; the embedded pool index makes equal distances resolve in pool order
    // (spawn/block order within a burst) exactly like the old explicit tie-break, with no stability
    // requirement on the sort itself.
    private ulong[] _sortKeys = Array.Empty<ulong>();
    private int[] _sortIdx = Array.Empty<int>();

    /// <summary>Sort a cull stream's indices farthest-first (ties in pool order) via the key arrays.</summary>
    private void SortDepth(int[] indices, int n, Particle[] pool, NVec3 viewOrigin)
    {
        if (n <= 1) return;
        if (_sortKeys.Length < n)
        {
            int cap = System.Numerics.BitOperations.RoundUpToPowerOf2((uint)n) is var c && c > 0 ? (int)c : n;
            _sortKeys = new ulong[cap];
            _sortIdx = new int[cap];
        }
        for (int k = 0; k < n; k++)
        {
            int i = indices[k];
            NVec3 d = pool[i].SortOrg - viewOrigin;
            uint bits = System.BitConverter.SingleToUInt32Bits(NVec3.Dot(d, d));
            _sortKeys[k] = ((ulong)~bits << 32) | (uint)i;
            _sortIdx[k] = i;
        }
        Array.Sort(_sortKeys, _sortIdx, 0, n);
        for (int k = 0; k < n; k++)
            indices[k] = _sortIdx[k];
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
        Array.Clear(_slotTable);
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
            if ((uint)index < 256u) _slotTable[index] = slot;
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
                if ((uint)i < 256u) _slotTable[i] = rects.Count;
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
                Name = "warm_fp_" + b.Name,
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

        var mat = new ShaderMaterial { Shader = ParticleShader(invmod) };
        if (invmod)
            // Multiplicative darkening composites over the premul stream (DP interleaves them in one
            // queue; a separate later batch is the accepted approximation — blood marks want to darken
            // what's under them).
            mat.RenderPriority = 1;

        var nodes = new MultiMeshInstance3D[TierCaps.Length];
        var meshes = new Rid[TierCaps.Length];
        for (int tier = 0; tier < TierCaps.Length; tier++)
        {
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                UseCustomData = true,
                Mesh = quad,
                // Pre-size so the GPU buffer exists before the first burst and is NEVER resized afterwards —
                // VisibleInstanceCount 0 keeps the uninitialized instances from drawing until the first Sync.
                InstanceCount = TierCaps[tier],
                VisibleInstanceCount = 0,
            };
            var node = new MultiMeshInstance3D
            {
                // The largest tier keeps the name the single MultiMesh had.
                Name = tier == TierCaps.Length - 1 ? "fp_" + name : "fp_" + name + "_" + TierCaps[tier],
                Multimesh = mm,
                MaterialOverride = mat,
                // Particles are emissive sprites: never cast/receive shadows, never affected by GI.
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                // A generous custom AABB so the renderer doesn't cull the whole batch when instances are far
                // from the node origin (we never recompute a tight AABB per frame).
                CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
                Visible = false,
            };
            AddChild(node);
            nodes[tier] = node;
            meshes[tier] = mm.GetRid();
        }

        // One CPU buffer for all tiers, sized once for the largest — the pack loop never reallocates it, and a
        // frame hands the engine only the stretch its tier holds.
        return new Batch
        {
            Nodes = nodes, Meshes = meshes, Name = name, Material = mat,
            Buffer = new float[MaxInstances * FloatsPerInstance],
        };
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
                // A legacy session on display values (DisplayFramebuffer) composes the stored texel and the
                // stored colour, as DarkPlaces does; otherwise the atlas is decoded to linear light.
                (DisplayFramebuffer.Active
                    ? "uniform sampler2D albedo_tex : filter_linear;\n"
                    : "uniform sampler2D albedo_tex : source_color, filter_linear;\n") +
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
        Pack(pool, highWater, viewOrigin, viewForward, time, ReadSyncSettings());
        Upload();
    }

    /// <summary>What one <see cref="Pack"/> reads from outside the pool: the draw cvars and the colour space.</summary>
    public readonly struct SyncSettings
    {
        public readonly float SizeScale, AlphaScale, NearMin, NearMax, DrawDistance;
        public readonly bool Display;

        public SyncSettings(float sizeScale, float alphaScale, float nearMin, float nearMax, float drawDistance, bool display)
        {
            SizeScale = sizeScale; AlphaScale = alphaScale; NearMin = nearMin; NearMax = nearMax; DrawDistance = drawDistance; Display = display;
        }
    }

    /// <summary>Reads the cvars a pack needs. Main thread (the cvar store's owner).</summary>
    public SyncSettings ReadSyncSettings() => new(
        ReadCvar(ParticleCvars.Size, 1f), ReadCvar(ParticleCvars.Alpha, 1f),
        ReadCvar(ParticleCvars.NearClipMin, 4f), ReadCvar(ParticleCvars.NearClipMax, 4f),
        ReadCvar(ParticleCvars.DrawDistance, 2000f), DisplayFramebuffer.Active);

    /// <summary>
    /// The first half of <see cref="Sync"/>: cull, sort and write the instances into this renderer's own
    /// buffers. It calls nothing in the engine and reads only its arguments and what <see cref="BuildAtlas"/>
    /// set up, so the particle worker runs it (FaithfulParticleBackend); <see cref="Upload"/> then hands the
    /// result to the engine on the main thread. One Pack at a time, and no Upload during one.
    /// </summary>
    public void Pack(Particle[] pool, int highWater, NVec3 viewOrigin, NVec3 viewForward, float time, in SyncSettings settings)
    {
        Batch? premul = _premul, invmod = _invmod;
        if (premul is null || invmod is null) return;
        if (!_built || pool is null || highWater <= 0)
        {
            premul.Count = 0;
            invmod.Count = 0;
            return;
        }

        // cl_particles_size scales every particle's drawn size (cl_particles.c:2732).
        float sizeScale = settings.SizeScale;
        if (sizeScale <= 0f) sizeScale = 1f;
        float alphaScale = MathF.Max(0f, settings.AlphaScale);

        // Near-clip band + size-scaled drawdistance (cl_particles.c:2655-2656, 3158).
        float nearMin = settings.NearMin;
        float nearMax = settings.NearMax;
        float drawDist = settings.DrawDistance;
        float drawDistSq = drawDist > 0f ? drawDist * drawDist : 0f;   // 0 keeps "disabled" semantics
        NVec3 fwd = Normalize(viewForward);
        float planeStart = NVec3.Dot(viewOrigin, fwd) + nearMin;       // minparticledist_start
        float planeEnd = NVec3.Dot(viewOrigin, fwd) + nearMax;         // minparticledist_end
        bool doFade = planeStart < planeEnd;

        premul.IndexCount = 0;
        invmod.IndexCount = 0;

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
                invmod.Add(i);
            else
                premul.Add(i);
        }

        // 2) Sort both streams the way DP's transparent queue does: farthest SortOrg (the effect center)
        //    first, ties in pool order — a burst composites in its spawn/block order.
        SortDepth(premul.Indices, premul.IndexCount, pool, viewOrigin);
        SortDepth(invmod.Indices, invmod.IndexCount, pool, viewOrigin);

        // 3) Pack.
        PackBatch(premul, pool, sizeScale, alphaScale, viewOrigin, fwd, time, planeStart, planeEnd, doFade, invmod: false, settings.Display);
        PackBatch(invmod, pool, sizeScale, alphaScale, viewOrigin, fwd, time, planeStart, planeEnd, doFade, invmod: true, settings.Display);
    }

    /// <summary>The second half of <see cref="Sync"/>: shows what the last <see cref="Pack"/> wrote. Main thread.</summary>
    public void Upload()
    {
        UploadBatch(_premul);
        UploadBatch(_invmod);
    }

    private void PackBatch(Batch b, Particle[] pool, float sizeScale, float alphaScale, NVec3 viewOrigin, NVec3 viewFwd,
        float time, float planeStart, float planeEnd, bool doFade, bool invmod, bool display)
    {
        int n = b.IndexCount;
        if (n == 0)
        {
            b.Count = 0;
            return;
        }

        // Reuse the buffer; InstanceCount tracks the buffer capacity and VisibleInstanceCount limits
        // what's drawn, so the per-frame path allocates NOTHING (only a native marshal copy in the upload).
        // (hitch fix 2026-08-03) CAPACITY IS FIXED. Writing MultiMesh.InstanceCount frees and re-creates the
        // GPU instance buffer, and under the separate render thread that realloc has to rendezvous with the
        // render thread — measured as 678-836 ms stalls MID-MATCH on stormkeep, watchdog 6869/6973 samples in
        // particles.cpu, with the frame's own events reading "fp_premul capacity -> 2048 (GPU realloc);
        // -> 4096 (GPU realloc)". The old grow-then-decay policy made that permanent rather than one-off: a
        // firefight grew the buffer, DecaySeconds later it shrank back, and the next firefight paid again.
        // Now the buffer is sized once at MaxInstances and never resized; VisibleInstanceCount (set by the
        // caller) is what limits drawing, which is the cheap per-frame knob the MultiMesh contract intends.
        // Cost is memory, and it is small: MaxInstances x 20 floats x 4 B ~= 0.65 MB per batch.
        // Overflow beyond MaxInstances is CLAMPED rather than reallocated - dropping part of one
        // extreme burst is invisible next to an 800 ms freeze, and the sim's own pool ceiling bounds n.
        // (2026-08-03) The clamp keeps the NEAREST MaxInstances: the stream is sorted farthest-first, so
        // taking the FIRST n kept the far particles and dropped the ones in the player's face. Packing the
        // TAIL window keeps the near subset, still in farthest-first order within itself (correct
        // transparent compositing).
        int first = 0;
        if (n > MaxInstances)
        {
            first = n - MaxInstances;
            n = MaxInstances;
        }
        float[] buf = b.Buffer;
        int[] indices = b.Indices;
        int[] slotTable = _slotTable;
        float[] toLinear = s_srgbByteToLinear;
        // (display: a session on display values takes the bytes as they are - DisplayFramebuffer.)

        for (int k = 0; k < n; k++)
        {
            ref Particle p = ref pool[indices[first + k]];
            int o = k * FloatsPerInstance;

            int slot = slotTable[p.TexNum];
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

            float lr = display ? p.ColorR * (1f / 256f) : toLinear[p.ColorR];
            float lg = display ? p.ColorG * (1f / 256f) : toLinear[p.ColorG];
            float lb = display ? p.ColorB * (1f / 256f) : toLinear[p.ColorB];

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
                // Velocity-stretched spark (cl_particles.c:2817-2825 + R_CalcBeam_Vertex3f): half-length
                // along the CURRENT velocity = max(stretch · 0.04 · |vel|, size · 0.5); half-width = size,
                // across the streak and across the line from the spark to the EYE (not the camera's forward
                // axis: that made a spark away from the screen centre thinner, and one flying along the view
                // direction vanish). The maths is ParticleGeometry.SparkAxes, held to DarkPlaces by
                // ParticleDarkPlacesTests. The shader draws the basis verbatim (sparkFlag = 1).
                //
                // The texture runs ALONG the streak: DarkPlaces gives the tail (org − along) the cell's
                // left edge s1 and the head s2, with t across (:2826-2829) — so the 1x1 quad's X (its U
                // axis) carries the full length, tail to head, and its Y the full width, with the top row
                // (V = 0, t1) on the −across side. Cell 41 (the bar the laser/electro sparks use) is drawn
                // lengthwise; with the axes the other way round it lay across the streak.
                Vector3 xAxis, yAxis, zAxis;
                if (ParticleGeometry.SparkAxes(p.Org, p.Vel, p.Size * sizeScale, p.Stretch, viewOrigin,
                        out NVec3 along, out NVec3 across))
                {
                    xAxis = Coords.ToGodot(along) * 2f;
                    yAxis = Coords.ToGodot(across) * -2f;
                    zAxis = xAxis.Cross(yAxis).Normalized();
                }
                else
                {
                    xAxis = yAxis = Vector3.Zero;               // no velocity / end-on: nothing to draw
                    zAxis = Vector3.Back;
                }
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

        b.Count = n;
    }

    private void UploadBatch(Batch? b)
    {
        if (b is null) return;
        int n = b.Count;
        if (n == 0)
        {
            ClearBatch(b);
            return;
        }
        float[] buf = b.Buffer;

        // The smallest tier that holds the n instances draws them; the one that drew the last frame's is hidden.
        int tier = 0;
        while (TierCaps[tier] < n) tier++;
        if (tier != b.ShownTier)
        {
            if (b.ShownTier >= 0) b.Nodes[b.ShownTier].Visible = false;
            b.Nodes[tier].Visible = true;
            b.ShownTier = tier;
            b.ShownCount = -1;
        }
        if (n != b.ShownCount)
        {
            RenderingServer.MultimeshSetVisibleInstances(b.Meshes[tier], n);   // draw only the n filled instances
            b.ShownCount = n;
        }

        // One upload of the reused buffer's first InstanceCount*stride floats (the length MultimeshSetBuffer
        // requires of this tier). No managed allocation here — only the native marshal copy.
        long began = VortexArena.Game.Legacy.LegacyPerfLog.Stamp();
        RenderingServer.MultimeshSetBuffer(b.Meshes[tier], new ReadOnlySpan<float>(buf, 0, TierCaps[tier] * FloatsPerInstance));
        VortexArena.Game.Legacy.LegacyPerfLog.Extra(VortexArena.Game.Legacy.LegacyPerfLog.XParticleUpload, began);
    }

    private static void ClearBatch(Batch? b)
    {
        if (b is null) return;
        if (b.ShownTier < 0) return;
        b.Nodes[b.ShownTier].Visible = false;
        b.ShownTier = -1;
        b.ShownCount = -1;
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
