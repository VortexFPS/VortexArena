using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using VortexArena.Formats.Materials;
using VortexArena.Formats.Images;
using VortexArena.Formats.Vfs;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The central asset facade: it turns Quake 3 shader names and texture base names into ready-to-use
/// Godot <see cref="Material"/>s and <see cref="Texture2D"/>s, backed by the <see cref="VirtualFileSystem"/>.
///
/// <para>On construction it parses every <c>scripts/*.shader</c> in the mounted gamedirs into a
/// case-insensitive shader dictionary (via <see cref="Q3ShaderParser.ParseFiles"/>); thereafter the BSP
/// loader, model importers, and the HUD all resolve materials through the same instance so a name is
/// compiled once and cached. The public surface is intentionally small and stable — other builders call
/// it and must keep working:</para>
/// <list type="bullet">
///   <item><see cref="ResolveMaterial"/> — name/texture → a never-null Godot material.</item>
///   <item><see cref="LoadTexture"/> — base name → a cached <see cref="Texture2D"/> (TGA/PNG/JPG).</item>
///   <item><see cref="MakeLightmapMaterial"/> — albedo + lightmap → the lightmap-modulate material.</item>
///   <item><see cref="GetShader"/> — name → the parsed <see cref="ShaderDef"/> (for surfaceparm queries).</item>
/// </list>
///
/// <para>Everything here lives on the Godot/render side; the parsed POCOs and the VFS come from the
/// Godot-free <c>VortexArena.Formats</c> library. Conversions between the two are explicit. Materials and
/// textures are cached and shared, so callers must treat returned resources as read-only.</para>
/// </summary>
public sealed class AssetSystem
{
    private readonly VirtualFileSystem _vfs;

    // name (extension-stripped, lower-cased) -> parsed shader. Case-insensitive lookups.
    //
    // Read lock-free from the streamer's worker lanes, which is safe because the dictionary is never MUTATED:
    // ReloadShaders (fs_rescan) parses a whole new one and swaps the reference. `volatile` is what makes that
    // swap publish safely — a worker mid-flight keeps reading the old snapshot, which is still a complete and
    // internally consistent table. Same immutable-snapshot discipline VirtualFileSystem uses for its mounts.
    private volatile IReadOnlyDictionary<string, ShaderDef> _shaders;

    // Caches. Materials are keyed by the *requested* name (so a shader and a bare texture of the same
    // stem share a slot, matching Q3 where the shader shadows the texture). Textures are keyed by the
    // resolved vpath so two names that resolve to the same file share one GPU texture.
    private readonly Dictionary<string, Material> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D?> _textureCache = new(StringComparer.Ordinal);
    // The texture cache is also written from the streamer's worker lane (WarmTextureOffThread — the menu
    // warm's upload half), so every read/write goes through this gate. Uncontended in the common case; a
    // double-upload race is harmless (last writer wins, the loser is collected), the same reasoning the
    // model/skeletal parse caches already document.
    private readonly object _textureCacheGate = new();
    // Same story for the material cache: the menu warm resolves materials on the streamer's worker lane so the
    // generated-shader compile never lands on a menu frame. `_shaders` itself is immutable after construction,
    // so reading a ShaderDef from a worker needs no gate — only this cache does.
    private readonly object _materialCacheGate = new();

    private Texture2D? _fallbackTexture;       // magenta/black checkerboard
    private Material? _fallbackMaterial;        // unlit material wrapping the checkerboard
    private Texture2D? _whiteTexture;           // 1×1 white ($whiteimage)
    private Texture2D? _blackTexture;           // 1×1 black (missing _glow companion)

    // Autosprite deform materials, cached separately from _materialCache: the same shader NAME compiles
    // to a ShaderMaterial with baked CUSTOM0/1 semantics on the model path but keeps the plain
    // billboard fallback on the ordinary (BSP) path. A null entry caches "not an autosprite shader".
    private readonly Dictionary<string, ShaderMaterial?> _autospriteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _autospriteCacheGate = new();

    /// <summary>
    /// Build the facade over <paramref name="vfs"/>: load and parse every <c>scripts/*.shader</c> into
    /// the shader dictionary. The VFS must already have its gamedirs mounted.
    /// </summary>
    public AssetSystem(VirtualFileSystem vfs)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        _shaders = LoadShaders(vfs);
    }

    /// <summary>The virtual filesystem this facade reads assets from.</summary>
    public VirtualFileSystem Vfs => _vfs;

    /// <summary>Number of shaders parsed at construction (diagnostics).</summary>
    public int ShaderCount => _shaders.Count;

    /// <summary>
    /// Every parsed shader name, for the editor's shader browser. The keys are the shader paths as the
    /// <c>.shader</c> files declare them, which is exactly what a face's <c>Material</c> holds.
    /// </summary>
    public IEnumerable<string> ShaderNames() => _shaders.Keys;

    /// <summary>
    /// Re-parse every <c>scripts/*.shader</c> off the CURRENT search path and swap the table in — the
    /// <c>fs_rescan</c> half that makes a newly mounted pack's shaders take effect (most map packs ship one).
    /// The derived caches go with it: a material or autosprite compiled from the old table has to be rebuilt,
    /// and a texture that resolved to nothing may now resolve.
    ///
    /// <para>Already-built materials held by the live scene are NOT reached by this and keep rendering as they
    /// are — clearing a cache only decides what the NEXT request builds. That is the same line
    /// <see cref="VirtualFileSystem.Rescan"/> draws, and the reason a rescan is not a substitute for a map
    /// reload when the goal is to restyle what is already on screen.</para>
    /// </summary>
    public void ReloadShaders()
    {
        _shaders = LoadShaders(_vfs);

        lock (_materialCacheGate)
            _materialCache.Clear();
        lock (_autospriteCacheGate)
            _autospriteCache.Clear();

        // Textures are dropped SELECTIVELY: a null entry is a cached "this file does not exist", which a new
        // pack may have just made false. A live entry is a GPU texture the running scene is sharing — evicting
        // it would re-decode and re-upload the whole working set on the next frame that touches it, for no
        // gain, since the bytes behind a texture that still resolves the same way have not changed.
        lock (_textureCacheGate)
        {
            var misses = new List<string>();
            foreach (var kv in _textureCache)
            {
                if (kv.Value is null)
                    misses.Add(kv.Key);
            }
            foreach (string key in misses)
                _textureCache.Remove(key);
        }

        ClearPredecodedImages();
    }

    // -------------------------------------------------------------------------------------------------
    //  VRAM census (`r_vram_census`, perf 2026-08-02)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Inventory the resident texture cache: estimated GPU bytes per <see cref="TexCategory"/>
    /// plus the top offenders by size. Built to answer "where do 3.4 GB go?" with names instead of guesses —
    /// the 2026-08-02 re-measure showed <c>gl_texturecompression 1</c> moving total VRAM by ~0 (3459→3491 MB)
    /// even though the compressor demonstrably ran, so the bulk must live outside the compressible classes.
    /// Estimation: format bits-per-pixel × W×H, ×4/3 when mipmapped — close enough to rank, not exact
    /// (the driver pads). Godot's own <c>vram</c> counter additionally includes mesh/render-target buffers,
    /// so census-total &lt; monitor-total is expected; the DELTA is the non-texture share.
    /// </summary>
    public string VramCensus(int top = 25)
    {
        var perCat = new Dictionary<TexCategory, (long Bytes, int Count)>();
        var rows = new List<(string Path, long Bytes, string Fmt)>();
        long total = 0;
        lock (_textureCacheGate)
        {
            foreach (var kv in _textureCache)
            {
                if (kv.Value is not Texture2D t || !GodotObject.IsInstanceValid(t))
                    continue;
                long bytes = EstimateTextureBytes(t, out string fmt);
                total += bytes;
                var cat = TextureCategories.Classify(kv.Key);
                perCat.TryGetValue(cat, out (long Bytes, int Count) agg);
                perCat[cat] = (agg.Bytes + bytes, agg.Count + 1);
                rows.Add((kv.Key, bytes, fmt));
            }
        }

        rows.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[vram census] {rows.Count} resident textures, est {total / (1024.0 * 1024.0):F0} MB "
            + "(textures only; Godot's vram monitor adds mesh/render-target buffers)");
        sb.AppendLine($"  {CompressionStats()}");
        foreach (var kv in perCat.OrderByDescending(k => k.Value.Bytes))
            sb.AppendLine($"  {kv.Key,-16} {kv.Value.Bytes / (1024.0 * 1024.0),8:F1} MB in {kv.Value.Count} textures");
        sb.AppendLine($"  top {Math.Min(top, rows.Count)}:");
        foreach ((string path, long bytes, string fmt) in rows.Take(top))
            sb.AppendLine($"    {bytes / (1024.0 * 1024.0),7:F1} MB  {fmt,-12} {path}");
        return sb.ToString();
    }

    /// <summary>
    /// Estimated GPU bytes for a cached texture (format bpp × pixels, ×4/3 for mips).
    ///
    /// <para>Reads format and mip-ness from <see cref="_texMeta"/> — recorded at upload — rather than from the
    /// texture itself. <c>Texture2D.GetImage()</c> would answer both questions directly, but it is a
    /// <c>texture_2d_get</c> on the RenderingServer: under a THREADED renderer that blocks the main thread
    /// until the render thread drains, and Godot logs "causing RenderingServer synchronizations on every
    /// frame" once per texture, so a 25-row census printed 25 warnings and stalled as many times.
    /// <c>GetWidth</c>/<c>GetHeight</c> stay — those are cached on the Texture2D and never reach the server.</para>
    /// </summary>
    private static long EstimateTextureBytes(Texture2D t, out string fmt)
    {
        long w = t.GetWidth(), h = t.GetHeight();
        TexMeta meta;
        bool known;
        lock (_texMetaGate)
            known = _texMeta.TryGetValue(t.GetInstanceId(), out meta);

        // Unknown means a texture that did not come from UploadImage (an engine singleton, a Godot-side
        // resource). Assume the uncompressed worst case exactly as this did before, and mark the row `?` so a
        // guessed size is never read as a measured one.
        fmt = known ? meta.Format.ToString().ToLowerInvariant() : "rgba8?";
        bool mips = !known || meta.Mipmaps;
        double bpp = known ? BitsPerPixel(meta.Format) : 32;

        long bytes = (long)(w * h * bpp / 8.0);
        return mips ? bytes * 4 / 3 : bytes;
    }

    /// <summary>Bits per pixel of a GPU image format; the BC classes are their block size spread over the
    /// block's pixels. Close enough to rank, not exact — the driver pads.</summary>
    private static double BitsPerPixel(Image.Format f) => f switch
    {
        Image.Format.Dxt1 => 4,                            // BC1
        Image.Format.Dxt3 or Image.Format.Dxt5 => 8,       // BC2/BC3
        Image.Format.RgtcR => 4,                           // BC4
        Image.Format.RgtcRg => 8,                          // BC5
        Image.Format.BptcRgba => 8,                        // BC7
        Image.Format.BptcRgbf or Image.Format.BptcRgbfu => 8,
        Image.Format.Rgb8 => 24,
        Image.Format.L8 or Image.Format.R8 => 8,
        Image.Format.La8 or Image.Format.Rg8 => 16,
        Image.Format.Rf => 32,
        Image.Format.Rgbaf => 128,
        Image.Format.Rgbah => 64,
        _ => 32,                                           // uncompressed RGBA8 and anything unlisted
    };

    // -------------------------------------------------------------------------------------------------
    //  Shader dictionary
    // -------------------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, ShaderDef> LoadShaders(VirtualFileSystem vfs)
    {
        var texts = new List<string>();
        // Enumerate every scripts/*.shader, read each as text. Order matters (first definition wins);
        // Find() yields a stable union across mounts. We read defensively — a single unreadable script
        // must not abort startup.
        foreach (string vpath in SortedShaderPaths(vfs))
        {
            try
            {
                texts.Add(vfs.ReadText(vpath));
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AssetSystem] failed to read shader script '{vpath}': {ex.Message}");
            }
        }

        IReadOnlyDictionary<string, ShaderDef> dict;
        try
        {
            dict = Q3ShaderParser.ParseFiles(texts);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetSystem] shader parse failed: {ex.Message}");
            dict = new Dictionary<string, ShaderDef>(StringComparer.OrdinalIgnoreCase);
        }

        GD.Print($"[AssetSystem] loaded {dict.Count} shaders from {texts.Count} scripts.");
        return dict;
    }

    private static IEnumerable<string> SortedShaderPaths(VirtualFileSystem vfs)
    {
        // Sort by name so precedence is deterministic across runs (Find()'s mount-union order is stable
        // but name-sorting matches how a player would expect scripts/ to be read).
        var list = new List<string>();
        foreach (string p in vfs.Find("scripts/", "shader"))
            list.Add(p);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>
    /// Look up the parsed <see cref="ShaderDef"/> for <paramref name="name"/> (extension stripped,
    /// case-insensitive), or null if there is no shader by that name. Builders use this to read a
    /// surface's <c>surfaceparm</c>s without compiling a material.
    /// </summary>
    public ShaderDef? GetShader(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        string key = StripShaderExtension(name);
        return _shaders.TryGetValue(key, out ShaderDef? def) ? def : null;
    }

    /// <summary>Resolve a shader name straight to its <see cref="SurfaceFlags.SurfaceInfo"/> (solid if unknown).</summary>
    public SurfaceFlags.SurfaceInfo GetSurfaceInfo(string name) => SurfaceFlags.Resolve(GetShader(name));

    // -------------------------------------------------------------------------------------------------
    //  Material resolution
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolve <paramref name="nameOrTexture"/> to a Godot <see cref="Material"/>. If a shader of that
    /// name exists it is compiled (<see cref="ShaderCompiler"/>); otherwise a plain
    /// <see cref="StandardMaterial3D"/> is built from the texture of that name, wiring the
    /// <c>_norm</c>/<c>_gloss</c>/<c>_glow</c> channel-suffix companions when present. The result is
    /// cached by name and is <b>never null</b> — if nothing resolves, a magenta checkerboard fallback is
    /// returned so a missing asset is loud but non-fatal.
    /// </summary>
    public Material ResolveMaterial(string nameOrTexture) => ResolveMaterial(nameOrTexture, forModel: false);

    /// <summary>
    /// Resolve a material for MODEL geometry (players, weapons, items, gibs, props) rather than world
    /// surfaces. Identical to <see cref="ResolveMaterial(string)"/> except that the plain-texture fallback
    /// compiles to <see cref="PlayerSkinShader"/> instead of a <see cref="StandardMaterial3D"/>.
    ///
    /// <para><b>Why models need their own entry point (F1-B).</b> DarkPlaces lights every model from the BSP
    /// light grid. In this port only the skin shader can sample that grid, and the skin shader was only built
    /// for skins carrying <c>_shirt</c>/<c>_pants</c>/<c>_reflect</c> companions — which player models have and
    /// a health pickup does not. So before this, an item on the floor could not be grid-lit no matter what the
    /// renderer pushed at it: its material had nowhere to put the light. Routing model geometry here gives
    /// every model a material that <i>can</i> be grid-lit; whether it <i>is</i> stays a per-instance decision
    /// (<c>grid_lit</c>).</para>
    ///
    /// <para>World surfaces deliberately keep the old path: they are lit by their baked lightmap through
    /// <see cref="LightmapShader"/>, and a non-lightmapped world face has no reason to grow a model shader.
    /// Q3-shader-driven materials (blendFunc, tcMod, animmap) also still compile through
    /// <see cref="ShaderCompiler"/> in both modes — those carry render state a skin material cannot express.</para>
    /// </summary>
    public Material ResolveModelMaterial(string nameOrTexture) => ResolveMaterial(nameOrTexture, forModel: true);

    private Material ResolveMaterial(string nameOrTexture, bool forModel)
    {
        if (string.IsNullOrEmpty(nameOrTexture))
            return FallbackMaterial();

        string key = StripShaderExtension(nameOrTexture);
        // Model and world resolutions of the same name can differ (see ResolveModelMaterial), so they cannot
        // share a cache slot. The suffix keeps one dictionary rather than two parallel ones + two gates; the
        // leading space makes it unambiguous against a real asset name. Only the CACHE key carries it - every
        // lookup below (the shader table, the texture loads) uses the clean name.
        string cacheKey = forModel ? key + " model" : key;
        lock (_materialCacheGate)
            if (_materialCache.TryGetValue(cacheKey, out Material? cached))
                return cached;

        Material result;
        try
        {
            // Compile OUTSIDE the lock so a slow shader build never blocks another material's lookup.
            if (_shaders.TryGetValue(key, out ShaderDef? def))
            {
                result = ShaderCompiler.Compile(def, this) ?? BuildPlainMaterial(key, forModel);
            }
            else
            {
                result = BuildPlainMaterial(key, forModel);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetSystem] material '{key}' failed to compile: {ex.Message}");
            result = FallbackMaterial();
        }

        lock (_materialCacheGate)
        {
            // Re-check before publishing, the way WarmTextureOffThread does under the upload gate. Without
            // it, two threads resolving one key both compile and both publish, and the loser's Material has
            // already been RETURNED to a caller — so the cache and a live mesh hold different ShaderMaterial
            // instances for one name, which is a duplicate pipeline compile on first draw and a break of the
            // documented "materials and textures are cached and shared" contract. Reachable since the menu
            // warm began resolving materials on a worker while a match resolves the same names on the main
            // thread, over the same AssetSystem.
            if (_materialCache.TryGetValue(cacheKey, out Material? raced))
                return raced;
            _materialCache[cacheKey] = result;
        }
        return result;
    }

    /// <summary>
    /// MAIN-THREAD, call once before any worker touches <see cref="ResolveMaterial"/> or
    /// <see cref="WarmTextureOffThread"/>: force the lazily-built shared singletons (white/black/checkerboard
    /// textures and the fallback material) into existence.
    ///
    /// <para>Those four are plain <c>??=</c> lazy fields, so two workers hitting a missing texture at the same
    /// moment would each build one and the loser would leak a GPU resource. Rather than lock every accessor on
    /// a path that is otherwise single-threaded, the menu warm simply constructs them up front — after which
    /// the accessors only ever READ an already-set field, which is safe from any thread.</para>
    /// </summary>
    public void PrimeSharedSingletons()
    {
        WhiteTexture();
        BlackTexture();
        FallbackTexture();
        FallbackMaterial();
        // The generated shaders reachable from ResolveMaterial on a worker (TryBuildSkinMaterial finds the
        // _shirt/_pants masks on any stock player model). Their accessors are individually locked now, which
        // is what actually makes them safe; priming here is belt-and-braces so the FIRST construction — the
        // expensive one, and the one that compiles GLSL — still lands on the main thread where the renderer
        // is unquestioned. Priming ALONE was not enough and is the reason this list was wrong before: it
        // covered the four singletons AssetSystem owns and silently missed the three it merely reaches.
        _ = PlayerSkinShader.Shader;
        _ = LightmapShader.Shader;
        _ = LightmapShader.TranslucentShader;
        _ = Md3MorphShader.Shader;
    }

    /// <summary>
    /// Resolve the dedicated autosprite-deform <see cref="ShaderMaterial"/> for a shader name whose def
    /// carries <c>deformVertexes autosprite</c>/<c>autosprite2</c> — the faithful GPU deform the MD3
    /// builder pairs with baked <c>CUSTOM0/1</c> quad frames (<c>AutospriteQuads</c>). Opt-in and cached
    /// separately from <see cref="ResolveMaterial"/>: only the model path calls this, so BSP surfaces keep
    /// the old billboard approximation untouched. Returns null (cached) when the name has no shader, no
    /// autosprite deform, or no usable image — callers fall back to <see cref="ResolveMaterial"/>.
    /// </summary>
    public ShaderMaterial? ResolveAutospriteMaterial(string nameOrTexture)
    {
        if (string.IsNullOrEmpty(nameOrTexture))
            return null;

        string key = StripShaderExtension(nameOrTexture);
        // Gated like its two siblings. This one had NO lock at all while _materialCache and _textureCache
        // gained theirs, which was survivable only for as long as nothing off-thread reached it — and the
        // menu warm now resolves materials on a worker. A plain Dictionary read concurrent with a resize is
        // undefined, so the failure would be a corrupted lookup rather than a clean race.
        lock (_autospriteCacheGate)
            if (_autospriteCache.TryGetValue(key, out ShaderMaterial? cached))
                return cached;

        ShaderMaterial? result = null;
        try
        {
            // Compiled OUTSIDE the lock, matching ResolveMaterial: a slow shader build must not block
            // another name's lookup.
            if (_shaders.TryGetValue(key, out ShaderDef? def))
                result = ShaderCompiler.CompileAutosprite(def, this);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetSystem] autosprite material '{key}' failed to compile: {ex.Message}");
        }

        lock (_autospriteCacheGate)
        {
            if (_autospriteCache.TryGetValue(key, out ShaderMaterial? raced))
                return raced;
            _autospriteCache[key] = result;
        }
        return result;
    }

    /// <summary>
    /// Build a <see cref="StandardMaterial3D"/> directly from a texture base name (no shader). Wires the
    /// standard Xonotic channel-suffix companions: <c>_norm</c>→normal map, <c>_gloss</c>→roughness
    /// (inverted: gloss is the opposite of roughness), <c>_glow</c>→emission. Falls back to the magenta
    /// material if even the base albedo is missing.
    /// </summary>
    private Material BuildPlainMaterial(string textureBase, bool forModel = false)
    {
        Texture2D? albedo = LoadTexture(textureBase);
        if (albedo == null)
            return FallbackMaterial();

        // A texture with team-colorable (_shirt/_pants) or reflective (_reflect) masks must compile to the
        // dedicated skin shader — StandardMaterial3D cannot express the tinted additive masks. This covers
        // the (extensionless, shaderless) model skins Xonotic loads straight by texture name.
        //
        // MODEL geometry takes that shader unconditionally (forModel, F1-B): it is the only material here that
        // can sample the map's baked light grid, and DarkPlaces lights every model from the grid. A plain
        // pickup skin has no masks, so without this it would be stuck on StandardMaterial3D and could not be
        // grid-lit however hard the renderer tried. With no masks bound they contribute nothing (the shader
        // defaults them to black), so the rendered result is the same material minus that limitation.
        ShaderMaterial? skin = TryBuildSkinMaterial(textureBase, albedo, alwaysBuild: forModel);
        if (skin != null)
            return skin;

        var mat = new StandardMaterial3D
        {
            ResourceName = textureBase,
            AlbedoTexture = albedo,
            // Q3 content is authored for nearest-ish but Godot's default trilinear looks right with mips.
            // Anisotropic keeps it crisp at grazing angles (floors/ramps) — cap set in project.godot.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };

        WireCompanions(mat, textureBase);
        return mat;
    }

    /// <summary>
    /// Attach the <c>_norm</c>/<c>_gloss</c>/<c>_glow</c>/<c>_reflect</c> companion textures to a
    /// StandardMaterial3D built from <paramref name="baseName"/>, if those sibling images exist. Shared
    /// by the plain-texture path and the compiler's single-stage path.
    /// </summary>
    internal void WireCompanions(StandardMaterial3D mat, string baseName)
    {
        // A Q3 shader stage often names its map WITH an extension (`map textures/foo/bar.tga`), and the
        // compiler passes that through verbatim (ShaderCompiler.CompanionBase). Naively appending a suffix
        // then yields `bar.tga_norm`, which never resolves — so strip the extension first (mirrors LoadGlow /
        // DP's Image_StripImageExtension). Without this the _norm/_gloss/_glow/_reflect companions silently
        // fail to load for any shadered surface or model whose stage map name carries an extension.
        baseName = AssetPaths.StripImageExtension(baseName);

        Texture2D? norm = LoadTexture(baseName + "_norm");
        if (norm != null)
        {
            mat.NormalEnabled = true;
            mat.NormalTexture = norm;
        }

        Texture2D? gloss = LoadTexture(baseName + "_gloss");
        if (gloss != null)
        {
            // A gloss map is the inverse of roughness. Feed it as the roughness texture but invert the
            // sense by pulling roughness toward 0 where gloss is high — Godot multiplies the texture by
            // the scalar Roughness, so we sample the (grayscale) gloss and let the scalar bias it. Using
            // the green channel matches DP's gloss convention; a low base roughness keeps speculars tight.
            mat.RoughnessTexture = gloss;
            mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Grayscale;
            mat.Roughness = 1.0f;
        }

        Texture2D? glow = LoadTexture(baseName + "_glow");
        if (glow != null)
        {
            // DP adds the _glow companion FULLBRIGHT on top of the lit surface (shader_glsl.h
            // `color.rgb += Texture_Glow * Color_Glow`, Color_Glow≈1). The glow image is a MASK — mostly
            // black, only the emissive bits bright — so only those bits light up. Godot's default emission
            // operator is ADD: EMISSION = (Emission + glowTex) * EmissionEnergyMultiplier. The base Emission
            // color must therefore be BLACK, not White — White adds (1,1,1) over the WHOLE surface and blows
            // the model out solid white (the weapon-viewmodel regression). Black yields EMISSION = glowTex,
            // matching DP (and PlayerSkinShader's `EMISSION = texture(glow_tex, UV).rgb`).
            mat.EmissionEnabled = true;
            mat.EmissionTexture = glow;
            mat.Emission = Colors.Black;
            mat.EmissionEnergyMultiplier = 1.0f;
        }

        // _reflect: a reflection mask (DP Texture_ReflectMask). StandardMaterial3D can't add a masked
        // cubemap, so map it onto the metallic channel — Godot reflects the scene environment/probes off
        // the bright areas, which is the closest StandardMaterial3D analogue (the full masked-cubemap term
        // is in PlayerSkinShader for the shirt/pants skin path). Roughness is biased low so the reflection
        // reads (gloss above may already have set the roughness texture; the scalar keeps speculars tight).
        Texture2D? reflect = LoadTexture(baseName + "_reflect");
        if (reflect != null)
        {
            mat.MetallicTexture = reflect;
            mat.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Grayscale;
            mat.Metallic = 1.0f;
            mat.MetallicSpecular = 0.8f;
            if (gloss == null)
                mat.Roughness = 0.25f;
        }
    }

    /// <summary>
    /// Build a Darkplaces "skin" material (<see cref="PlayerSkinShader"/>) when <paramref name="baseName"/>
    /// has any of the team-colorable / reflective companion masks — <c>_shirt</c>, <c>_pants</c>, or
    /// <c>_reflect</c>. Returns null when none of those siblings exist (the caller then builds the ordinary
    /// <see cref="StandardMaterial3D"/>). The diffuse and the <c>_norm</c>/<c>_gloss</c>/<c>_glow</c>/
    /// <c>_reflect</c> companions are bound as uniforms so the skin keeps its normal/gloss/glow; the shirt and
    /// pants colors default to black (no contribution) until a caller drives them from the player colormap.
    /// </summary>
    internal ShaderMaterial? TryBuildSkinMaterial(string baseName, Texture2D? albedo, bool alwaysBuild = false)
    {
        if (string.IsNullOrEmpty(baseName))
            return null;

        // Same extension hazard as WireCompanions: the stage map name may carry an extension, so strip it
        // before appending the _shirt/_pants/_reflect/_norm/_gloss/_glow suffixes (else they never resolve
        // and a team-colorable/reflective skin silently degrades to a plain StandardMaterial3D).
        baseName = AssetPaths.StripImageExtension(baseName);

        Texture2D? shirt = LoadTexture(baseName + "_shirt");
        Texture2D? pants = LoadTexture(baseName + "_pants");
        Texture2D? reflect = LoadTexture(baseName + "_reflect");
        // alwaysBuild (model geometry, F1-B): build the skin material even with no masks, so the model has a
        // shader that CAN be grid-lit. Unbound masks default to black and add nothing, so this costs a branch
        // the GPU takes uniformly, not a look change.
        if (!alwaysBuild && shirt == null && pants == null && reflect == null)
            return null; // not a team-colorable / reflective skin, and not model geometry -> ordinary material

        var mat = new ShaderMaterial { Shader = PlayerSkinShader.Shader, ResourceName = baseName + "/skin" };
        mat.SetShaderParameter(PlayerSkinShader.AlbedoUniform, albedo ?? WhiteTexture());

        if (shirt != null) mat.SetShaderParameter(PlayerSkinShader.ShirtMaskUniform, shirt);
        if (pants != null) mat.SetShaderParameter(PlayerSkinShader.PantsMaskUniform, pants);

        if (reflect != null)
        {
            mat.SetShaderParameter(PlayerSkinShader.ReflectMaskUniform, reflect);
            mat.SetShaderParameter("has_reflect", true);
            mat.SetShaderParameter(PlayerSkinShader.ReflectStrengthUniform, 1.0f);
            // dpreflectcube — DELIBERATELY NOT BOUND (playtest r8): DP applies the reflect cubemap only inside
            // its RTLIGHT shader permutations (USEREFLECTCUBE), and stock Xonotic runs with realtime world
            // lighting OFF — so in practice the term is nearly invisible in Base. The always-on EMISSION add we
            // shipped first mirrored the sky at full strength on every reflect-masked panel: bright sky-colored
            // patches that read as HOLES through the gun + chrome ("too shiny / seeing through geometry",
            // r8 screenshots). The shader's no-cubemap fallback (a restrained metal sheen that never kills the
            // diffuse) is the faithful default look; revisit binding the cube only with a real rtlight pass.
        }

        Texture2D? norm = LoadTexture(baseName + "_norm");
        if (norm != null)
        {
            mat.SetShaderParameter("normal_tex", norm);
            mat.SetShaderParameter("has_normal", true);
            if (IsRgTexture(norm))
                mat.SetShaderParameter("norm_rg", true); // BC5 two-channel — shader reconstructs Z
        }
        Texture2D? gloss = LoadTexture(baseName + "_gloss");
        if (gloss != null)
        {
            mat.SetShaderParameter("gloss_tex", gloss);
            mat.SetShaderParameter("has_gloss", true);
        }
        Texture2D? glow = LoadTexture(baseName + "_glow");
        if (glow != null)
        {
            mat.SetShaderParameter(PlayerSkinShader.GlowUniform, glow);
            mat.SetShaderParameter("has_glow", true);
        }

        // Shirt/pants/colormod/glowmod are per-entity *instance* uniforms (see PlayerSkinShader): the masks
        // are bound here, but the colors are driven per model instance by the player/view renderer
        // (ModelTint). They default to no team tint / white colormod when unset, so there is nothing to set
        // on the shared, cached material.
        return mat;
    }

    // --- dpreflectcube (playtest #36) ----------------------------------------------------------------
    private Cubemap? _defaultReflectCube;
    private bool _defaultReflectCubeTried;

    /// <summary>
    /// The `cubemaps/default/sky` environment cubemap every shipped weapon shader names via
    /// <c>dpreflectcube</c> (scripts/weapons.shader) — six faces loaded in DP box order (+X −X +Y −Y +Z −Z,
    /// r_sky.c's <c>px/nx/py/ny/pz/nz</c> convention, no flips), QUAKE axes; the skin shader converts its
    /// sample direction Godot→Quake to match. Built once and cached (null-cached on a miss so a data set
    /// without the faces never re-probes).
    /// CURRENTLY UNWIRED BY DESIGN (playtest r8): DP evaluates dpreflectcube only in its rtlight shader
    /// permutations and stock Xonotic ships realtime world lighting OFF, so the faithful default look has no
    /// visible cubemap term — the always-on EMISSION add read as sky-mirror holes on the guns. Kept for a
    /// future realtime-lighting pass (bind via <see cref="PlayerSkinShader.ReflectCubeUniform"/> +
    /// <c>has_reflect_cube</c>).
    /// </summary>
    internal Cubemap? DefaultReflectCubemap()
    {
        if (_defaultReflectCubeTried)
            return _defaultReflectCube;
        _defaultReflectCubeTried = true;

        string[] suffixes = { "px", "nx", "py", "ny", "pz", "nz" }; // DP box order = GL cubemap layer order
        var faces = new Godot.Collections.Array<Image>();
        foreach (string s in suffixes)
        {
            Image? img = LoadImage("cubemaps/default/sky" + s);
            if (img is null)
            {
                VortexArena.Common.Diagnostics.Log.Info("[AssetSystem] cubemaps/default/sky*: face missing — weapon reflection falls back to the mild sheen.");
                return null;
            }
            // All layers of a layered texture must share one format/size; the shipped faces are uniform PNGs,
            // but normalize the format defensively (a DDS/TGA override could differ).
            if (img.GetFormat() != Image.Format.Rgba8)
                img.Convert(Image.Format.Rgba8);
            img.GenerateMipmaps();
            faces.Add(img);
        }
        var cube = new Cubemap();
        cube.CreateFromImages(faces);
        _defaultReflectCube = cube;
        return cube;
    }

    /// <summary>
    /// The lightmap-modulate material (see <see cref="LightmapShader"/>): albedo sampled with UV,
    /// multiplied by <paramref name="lightmap"/> sampled with UV2. <paramref name="albedo"/> may be null.
    /// </summary>
    public ShaderMaterial MakeLightmapMaterial(Texture2D? albedo, Texture2D lightmap)
        => LightmapShader.MakeMaterial(albedo, lightmap);

    /// <summary>Diffuse-stage params for a lightmapped surface: base texture (may be null), alpha-test cutoff
    /// (0 = opaque), a static UV scale, the self-illumination (<c>_glow</c>) companion if present (null
    /// otherwise), and whether the diffuse stage alpha-blends (Q3 <c>blendFunc blend</c> → render translucent,
    /// e.g. <c>trak5x/misc-glass</c>). See <see cref="ResolveLightmapDiffuse"/>.</summary>
    public readonly record struct LightmapDiffuse(
        Texture2D? Texture, float AlphaCutoff, Vector2 UvScale, Texture2D? Glow, bool Translucent,
        Texture2D? Normal, Texture2D? Gloss);

    /// <summary>
    /// The render parameters a lightmapped surface needs from its shader's <i>diffuse</i> stage: the base
    /// color texture, its alpha-test cutoff (0 = none), and any static <c>tcMod scale</c> on the UV. The BSP
    /// lightmap path resolves albedo through this rather than a bare <see cref="LoadTexture"/> of the shader
    /// name, because a Q3 shader's diffuse image lives in a <i>stage</i> — e.g. a
    /// <c>{ map $lightmap } { map textures/… }</c> shader's color is the second stage, not a file named after
    /// the shader (loading the name there yields null → an untextured white surface). Resolution:
    /// <list type="bullet">
    ///   <item>No shader by this name → the name IS the texture (plain world brush): load it directly.</item>
    ///   <item>Shader present → the first non-detail, non-<c>$lightmap</c>, non-<c>$white</c> stage with a real
    ///   image is the diffuse; its <c>alphaFunc</c> cutoff and lone static <c>tcMod scale</c> come along.</item>
    ///   <item>Shader with no such stage (global-only / pure <c>$lightmap</c>) → fall back to the name.</item>
    /// </list>
    /// Mirrors <see cref="ShaderCompiler"/>'s stage selection so a lightmapped surface shows the same diffuse
    /// the non-lightmapped (ResolveMaterial) path would.
    /// </summary>
    public LightmapDiffuse ResolveLightmapDiffuse(string shaderName)
    {
        if (string.IsNullOrEmpty(shaderName))
            return new LightmapDiffuse(null, 0f, Vector2.One, null, false, null, null);

        ShaderDef? def = GetShader(shaderName);
        if (def is null)
            return new LightmapDiffuse(LoadTexture(shaderName), 0f, Vector2.One, LoadGlow(shaderName), false,
                LoadNorm(shaderName), LoadGloss(shaderName));

        foreach (ShaderStage stage in def.Stages)
        {
            if (stage.Detail || stage.IsLightmap || stage.IsWhiteImage)
                continue;
            string image = !string.IsNullOrEmpty(stage.MapTexture) ? stage.MapTexture
                : (stage.AnimMap is { Frames.Length: > 0 } ? stage.AnimMap.Frames[0] : string.Empty);
            if (string.IsNullOrEmpty(image) || image == "-" || image.StartsWith('$'))
                continue;
            // DP auto-loads a "<diffuse>_glow" self-illumination companion (the world equivalent of the
            // _norm/_gloss siblings) and adds it fullbright; light fixtures rely on it (e.g.
            // textures/exx/light/light_u201_glow). Match that so lightmapped lights glow instead of reading dark.
            // A diffuse stage with blendFunc blend (GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA) is an alpha-blended
            // surface (glass): flag it translucent so the lightmap path renders it see-through, not opaque.
            return new LightmapDiffuse(LoadTexture(image), DiffuseAlphaCutoff(stage), DiffuseUvScale(stage),
                LoadGlow(image), stage.BlendMode == BlendMode.Blend, LoadNorm(image), LoadGloss(image));
        }

        // Global-only / $lightmap-only shader: best-effort the shader name as a texture (usually null → white).
        return new LightmapDiffuse(LoadTexture(shaderName), 0f, Vector2.One, LoadGlow(shaderName), false,
            LoadNorm(shaderName), LoadGloss(shaderName));
    }

    /// <summary>Load the <c>_glow</c> self-illumination companion for a diffuse image. The extension MUST be
    /// stripped first: a shader stage often names its map WITH an extension (<c>map foo.tga</c>), and naively
    /// appending the suffix yields <c>foo.tga_glow</c>, which never resolves. Mirrors DP's companion lookup.</summary>
    private Texture2D? LoadGlow(string image)
        => LoadTexture(AssetPaths.StripImageExtension(image) + "_glow");

    /// <summary>Load the <c>_norm</c> (tangentspace normal) companion for a diffuse image; the extension is
    /// stripped first (same hazard as <see cref="LoadGlow"/>). Null when the surface ships no normal map.</summary>
    private Texture2D? LoadNorm(string image)
        => LoadTexture(AssetPaths.StripImageExtension(image) + "_norm");

    /// <summary>Load the <c>_gloss</c> (specular) companion for a diffuse image; the extension is stripped
    /// first (see <see cref="LoadNorm"/>). Null when the surface ships no gloss map.</summary>
    private Texture2D? LoadGloss(string image)
        => LoadTexture(AssetPaths.StripImageExtension(image) + "_gloss");

    /// <summary>The Godot alpha-scissor cutoff for a stage's Q3 <c>alphaFunc</c> (GE128→0.5, GT0→~0, else 0.5);
    /// 0 when the stage has no alpha test. Mirrors <see cref="ShaderCompiler"/>'s mapping.</summary>
    private static float DiffuseAlphaCutoff(ShaderStage stage)
    {
        if (string.IsNullOrEmpty(stage.AlphaFunc))
            return 0f;
        string f = stage.AlphaFunc!.ToUpperInvariant();
        if (f.Contains("128")) return 0.5f;
        if (f.Contains("GT0") || f.Contains("GE0")) return 0.004f;
        return 0.5f;
    }

    /// <summary>A lone static <c>tcMod scale</c> on the stage as a UV multiply (DP Q3TCMOD_SCALE), or (1,1).
    /// A scale that co-occurs with an animated tcMod belongs to the animated-shader path, so it is ignored
    /// here to avoid a double-apply (the lightmap path can't animate).</summary>
    private static Vector2 DiffuseUvScale(ShaderStage stage)
    {
        Vector2 scale = Vector2.One;
        bool hasScale = false, hasAnimated = false;
        foreach (TcMod m in stage.TcMods)
        {
            switch (m.Type)
            {
                case TcModType.Scale: scale = new Vector2(m.P(0), m.P(1)); hasScale = true; break;
                case TcModType.Scroll:
                case TcModType.Rotate:
                case TcModType.Stretch:
                case TcModType.Turb: hasAnimated = true; break;
            }
        }
        return (hasScale && !hasAnimated) ? scale : Vector2.One;
    }

    // -------------------------------------------------------------------------------------------------
    //  Texture loading
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolve and load a texture by extension-agnostic base name (e.g.
    /// <c>"textures/exomorph/exo_floor"</c>). Uses <see cref="VirtualFileSystem.ResolveImage"/> for the
    /// DP extension-search/<c>override/</c> precedence, then decodes the bytes: <c>.tga</c> via the
    /// built-in <see cref="TgaDecoder"/> (uncompressed + RLE, 24/32/16/8-bit), <c>.png</c>/<c>.jpg</c>
    /// via Godot's buffer loaders. Returns null if nothing resolves or the bytes fail to decode. Cached
    /// by resolved vpath so repeated requests (and the same image under several names) share one texture.
    /// </summary>
    public Texture2D? LoadTexture(string baseNameNoExt)
    {
        if (string.IsNullOrEmpty(baseNameNoExt))
            return null;

        // Special engine images.
        if (string.Equals(baseNameNoExt, "$whiteimage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(baseNameNoExt, "$white", StringComparison.OrdinalIgnoreCase))
            return WhiteTexture();

        string? vpath = _vfs.ResolveImage(baseNameNoExt);
        if (vpath == null)
            return null;

        lock (_textureCacheGate)
            if (_textureCache.TryGetValue(vpath, out Texture2D? cached))
                return cached;

        // The upload gate is taken HERE too, not only by the off-thread warm. It used to be the warm's
        // alone, which made "one upload in flight" true worker-vs-worker and false against the main thread —
        // and the warm deliberately keeps running into a match (Shell sets ProcessMode.Always) over the same
        // shared AssetSystem, warming exactly the set the match precaches. Both sides would miss the cache,
        // both would upload, and both would publish: one 25-45 ms upload wasted, one Texture2D orphaned
        // while a already-built Material still referenced it, and the driver-ingest burst the gate exists to
        // prevent happening anyway. The decode is still outside the gate, so a slow decode blocks nobody.
        // Decode, mip and compress OUTSIDE the gate — a slow texture must never block another uploader.
        Image? image = PrepareImage(vpath);

        lock (_uploadGate)
        {
            // Re-check under the gate: whoever held it may have been uploading this very vpath.
            lock (_textureCacheGate)
                if (_textureCache.TryGetValue(vpath, out Texture2D? raced))
                    return raced;

            Texture2D? tex = image is null ? null : UploadImage(vpath, image);
            lock (_textureCacheGate)
                _textureCache[vpath] = tex; // cache even null to avoid re-probing a known-bad image
            return tex;
        }
    }

    /// <summary>
    /// The menu warm's upload half, called from a <c>BackgroundAssetStreamer</c> WORKER — resolve, decode and
    /// GPU-upload one texture into the shared cache entirely off the main thread, so a warm costs the menu
    /// frame nothing at all.
    ///
    /// <para><b>Why this is a separate entry point rather than "LoadTexture is now thread-safe".</b> Godot 4's
    /// <see cref="RenderingServer"/> is command-buffered and tolerates resource creation from a thread, but
    /// that is a property of the ENGINE BUILD, not a contract this codebase should assume everywhere: the
    /// live in-match paths keep uploading on the main thread, where the behaviour is unquestioned. This one
    /// caller is safe to experiment through because a failure here is harmless by construction — the menu warm
    /// is best-effort prefetch, so a texture that fails to warm is simply loaded normally by the match that
    /// needs it. Any exception is swallowed with a one-line note rather than propagated.</para>
    ///
    /// <para>Skips <c>$</c>-prefixed engine images (<c>$whiteimage</c> and friends) — those lazily construct
    /// shared singletons and must stay on the main thread.</para>
    /// </summary>
    public void WarmTextureOffThread(string baseNameNoExt)
    {
        if (string.IsNullOrEmpty(baseNameNoExt) || baseNameNoExt[0] == '$')
            return;
        try
        {
            string? vpath = _vfs.ResolveImage(baseNameNoExt);   // ConcurrentDictionary-cached (thread-safe)
            if (vpath is null)
                return;
            lock (_textureCacheGate)
                if (_textureCache.ContainsKey(vpath))
                    return;

            // Decode in PARALLEL (this is CPU work and the lane has several workers). PredecodeTexture opens
            // its own `stream.predecode` scope, so wrapping the call in a second one of the same name
            // double-counted the warm's dominant worker cost — Prof accumulates by NAME, so every capture
            // read ~2x here and the scope became its own parent in the hitch tree.
            PredecodeTexture(baseNameNoExt);

            // Consume the predecode and finish the CPU work — mips and, when enabled, the block compress —
            // still OUTSIDE the gate. Taking the image out of _predecodedImages here also means a lost race
            // below simply drops a local: the parked image can no longer be stranded, which it was when the
            // early-out returned while it was still in the dictionary (nothing drains it after the vpath is
            // cached, so it survived to the next map change — ~21 MB for a 2048² chain, per lost race).
            Image? image = PrepareImage(vpath);
            if (image is null)
                return;

            // ...but upload SERIALLY. There is one GPU, and letting every worker push a 25-45 ms texture
            // create at it concurrently saturated the driver's ingest path: the main thread then blocked in
            // present (frames of 13-25 ms whose cost was ~all `rest`, with proc/rcpu/gpu all near zero) even
            // though it was doing no work of its own. One upload in flight turns that burst into a trickle.
            // Everything above is done by the time we take the gate, so waiting here is cheap — and now the
            // gate really does hold nothing but the upload, which is what its comment always claimed.
            lock (_uploadGate)
            {
                lock (_textureCacheGate)
                    if (_textureCache.ContainsKey(vpath))
                        return;                     // another uploader won; `image` is a local and is collected
                Texture2D? tex;
                using (VortexArena.Common.Diagnostics.Prof.Sample("stream.upload"))
                    tex = UploadImage(vpath, image);
                lock (_textureCacheGate)
                    _textureCache[vpath] = tex;
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[AssetSystem] off-thread warm of '{baseNameNoExt}' failed ({ex.Message}); " +
                     "the match will load it normally.");
        }
    }

    /// <summary>Serializes off-thread GPU uploads — see <see cref="WarmTextureOffThread"/>. Held only around
    /// the upload itself, never around the decode.</summary>
    private readonly object _uploadGate = new();

    /// <summary>
    /// Run <paramref name="build"/> holding the upload gate. For callers that construct Godot GPU resources
    /// OUTSIDE <see cref="LoadTexture"/> — notably the menu warm's material wave, where
    /// <c>ShaderCompiler.Compile</c> builds a <c>Shader</c>/<c>ShaderMaterial</c> and any texture its stage
    /// list did not pre-warm falls through to a fresh upload. Those went through no gate at all, so the
    /// serialisation the warm's texture half is careful about did not cover its material half, and the
    /// driver-ingest saturation the gate exists to prevent could still happen on gameplay frames (the warm
    /// keeps running into a match by design).
    ///
    /// <para>Kept as an explicit method rather than exposing the lock object, so the gate cannot be taken
    /// somewhere that then does slow CPU work under it — the mistake this file has already made once.</para>
    /// </summary>
    public T WithUploadGate<T>(Func<T> build)
    {
        lock (_uploadGate)
            return build();
    }

    /// <summary>
    /// Resolve and decode a texture by extension-agnostic base name to a raw <see cref="Image"/> (no GPU
    /// upload, not cached). Used by callers that need direct pixel access — e.g. the skybox loader, which
    /// reorients each cube face on the CPU before uploading. Returns null if nothing resolves or the bytes
    /// fail to decode.
    /// </summary>
    public Image? LoadImage(string baseNameNoExt)
    {
        if (string.IsNullOrEmpty(baseNameNoExt))
            return null;
        string? vpath = _vfs.ResolveImage(baseNameNoExt);
        return vpath == null ? null : LoadImageFromVpath(vpath);
    }

    /// <summary>
    /// OFF-THREAD-SAFE. A small preview image for a material — the editor's texture browser (backlog T6).
    /// Its <c>qer_editorimage</c>, else its diffuse stage, else its name (see
    /// <see cref="ShaderPreview.ImageName"/>), decoded, decompressed and scaled to
    /// <paramref name="size"/>² RGBA8. NOT cached and NOT uploaded — the caller owns both.
    ///
    /// Deliberately not <see cref="LoadTexture"/>: that is the WORLD's texture cache and it never evicts, so
    /// browsing a ~2000-entry shader list through it would permanently retain every diffuse in the game —
    /// 512²-1024² apiece, gigabytes — plus a multi-second synchronous decode on the frame the dialog opens.
    /// Deliberately not <see cref="PredecodeTexture"/> either: that parks a FULL-resolution image that only a
    /// matching <see cref="LoadTexture"/> drains, so a browse would leak one per thumbnail.
    ///
    /// Off-thread safety is the chain <see cref="PredecodeTexture"/> already runs on a worker: a
    /// concurrent-dictionary VFS resolve, a <c>[ThreadStatic]</c>-scratch decode, and pure-CPU image work.
    /// </summary>
    public Image? LoadThumbnailImage(string materialName, int size)
    {
        string? name = ShaderPreview.ImageName(materialName, GetShader(materialName));
        if (string.IsNullOrEmpty(name) || size < 1)
            return null;

        Image? img = LoadImage(name);
        if (img is null || img.IsEmpty())
            return null;

        try
        {
            // A full-chain DXT .dds passes through this loader still COMPRESSED, and Image.Resize refuses a
            // compressed format — so decompress, and drop the mip chain that came with it, before scaling.
            if (img.IsCompressed() && img.Decompress() != Error.Ok)
                return null;
            if (img.HasMipmaps())
                img.ClearMipmaps();
            if (img.GetWidth() != size || img.GetHeight() != size)
                img.Resize(size, size, Image.Interpolation.Bilinear);
            if (img.GetFormat() != Image.Format.Rgba8)
                img.Convert(Image.Format.Rgba8);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetSystem] thumbnail for '{materialName}' failed: {ex.Message}");
            return null;
        }
        return img;
    }

    // -------------------------------------------------------------------------------------------------
    //  (§12.3-1) Decoded-image handoff — the off-thread half of a texture load. A model build's dominant
    //  cost was the SYNCHRONOUS texture pipeline (VFS read + TGA/DDS decode + GPU upload, ~395 ms of a
    //  ~750 ms player-model build, measured). The read+decode half is pure C# plus thread-tolerant Image
    //  creation (§5: "decode into an Image off-thread is what Godot's own threaded loader does"), so a
    //  worker pre-decodes into this handoff and the main-thread LoadTexture consumes it — leaving only
    //  the ImageTexture.CreateFromImage upload on the main thread.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A parked predecode: the decoded image, plus whether the worker already ran the FULL CPU prep
    /// (<see cref="MaybePicmip"/> → <see cref="EnsureMipmaps"/> → <see cref="MaybeCompress"/>).
    ///
    /// <para>The flag is carried rather than re-derived because <see cref="MaybePicmip"/> is not idempotent —
    /// running it twice halves the image twice — so "has this been prepared?" cannot be read off the image
    /// itself. (Compression can: a compressed image is visibly compressed. Picmip cannot.)</para>
    /// </summary>
    private readonly record struct Parked(Image Image, bool Prepared);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Parked> _predecodedImages =
        new(StringComparer.Ordinal);

    /// <summary>
    /// OFF-THREAD-SAFE: resolve + decode one texture into the handoff so the next main-thread
    /// <see cref="LoadTexture"/> of the same name skips the read+decode. Idempotent; a miss is a no-op.
    /// (Worst case — the texture was already GPU-cached — the entry sits unused until consumed or
    /// <see cref="ClearPredecodedImages"/>.)
    ///
    /// <para>With <see cref="CompressOffThread"/> set this also runs picmip and the block compression here, so
    /// the whole CPU half of a texture load happens on the worker and the main thread is left with the upload
    /// alone. That is the difference between an encode that can use the lane's width and one that cannot: the
    /// main-thread <see cref="LoadTexture"/> is a single thread by definition, so encoding there is serial
    /// however many workers exist and whatever the CPU budget says.</para>
    /// </summary>
    public void PredecodeTexture(string baseNameNoExt)
    {
        if (string.IsNullOrEmpty(baseNameNoExt) || baseNameNoExt[0] == '$')
            return;
        // (perf 2026-07-03) Named scope: the worker-side read+decode was the biggest UNATTRIBUTED allocator in
        // the profiler (a 190 MB join-window frame carried no top-alloc scope) — per-thread Prof accumulators
        // make worker scopes cheap, and the alloc column now names this path in hitch trees.
        using var _ = VortexArena.Common.Diagnostics.Prof.Sample("stream.predecode");
        string? vpath = _vfs.ResolveImage(baseNameNoExt);   // ConcurrentDictionary-cached (thread-safe)
        if (vpath is null || _predecodedImages.ContainsKey(vpath))
            return;

        // Back-pressure for the load pre-pass (see PredecodeParkCap). Workers decode much faster than the main
        // thread consumes, so without this the pre-pass would try to hold a whole map's pixels in RAM at once.
        // Never applied on the main thread: main is the DRAIN, so blocking it here would deadlock the cap.
        int cap = PredecodeParkCap;
        if (cap > 0 && !VortexArena.Common.Diagnostics.Prof.IsMainThread)
        {
            // Bounded rather than a true wait/notify: the consumer side is two call sites plus a Clear(), and a
            // missed wake would strand a worker for the rest of the load. A 20 s ceiling makes the worst case
            // "the pre-pass gave up and the main thread loads it the old way", which is merely slow.
            for (int spins = 0; _predecodedImages.Count >= cap && spins < 20_000; spins++)
                System.Threading.Thread.Sleep(1);
        }

        Image? img = LoadImageFromVpath(vpath);
        if (img is null)
            return;
        bool prepared = EncodeOffThread;
        if (prepared)
            PrepareDecoded(vpath, img);  // picmip + mips + block compress, all on the WORKER
        else
            EnsureMipmaps(vpath, img);   // on the WORKER — the main-thread upload then includes mips for free
        _predecodedImages.TryAdd(vpath, new Parked(img, prepared));
    }

    /// <summary>
    /// (§12.8) Generate mipmaps on a decoded texture image. DP mipmaps every world/model texture (its GL
    /// texture default), but the port uploaded level-0-only images while every material samples with a
    /// <c>*_mipmap_anisotropic</c> filter — so distant/oblique surfaces aliased and shimmered (WORSE than DP)
    /// and minified sampling thrashed the texture cache. Generating mips is therefore BOTH a fidelity fix and
    /// a GPU win. Lightmap pages (<c>lm_NNNN</c>) are excluded — DP samples lightmaps unmipped; keep them
    /// byte-exact. No-op when mips already exist (DDS files can carry them) or the format can't (compressed).
    /// </summary>
    private static void EnsureMipmaps(string vpath, Image image)
    {
        // A decode that failed under memory pressure (Godot's alloc_static returning null on a full disk / OOM)
        // can hand back a non-null Image whose pixel buffer never allocated; Godot's NATIVE GenerateMipmaps then
        // dereferences that empty buffer and hard-SIGSEGVs the whole process (observed: a 100%-full disk → pagefile
        // exhaustion → alloc_static null → segfault here). Skip an empty image — this also covers a truncated /
        // corrupt asset that decoded to nothing. NOTE: this cannot save an OOM that fails INSIDE GenerateMipmaps'
        // own mip-chain allocation; a native segfault is uncatchable from managed code — freeing memory/disk is the
        // only remedy for that.
        if (image.IsEmpty())
        {
            GD.PrintErr($"[AssetSystem] '{vpath}': image has no pixel data (decode likely failed — low memory/disk?); skipping mipmaps.");
            return;
        }
        if (image.HasMipmaps() || image.IsCompressed())
            return;
        int slash = vpath.LastIndexOf('/');
        string file = slash >= 0 ? vpath[(slash + 1)..] : vpath;
        if (file.StartsWith("lm_", StringComparison.OrdinalIgnoreCase))
            return; // lightmap/deluxe page — sampled unmipped, exactly like DP
        if (image.GenerateMipmaps() != Error.Ok)
            GD.Print($"[AssetSystem] mipmap generation skipped for '{vpath}' (unsupported format)");
    }

    /// <summary>
    /// OFF-THREAD-SAFE: pre-decode every texture a material build will probe. For a plain texture material:
    /// the base + the channel-suffix companions (<c>_norm/_gloss/_glow/_reflect</c> + the skin-shader masks
    /// <c>_shirt/_pants</c>). For a Q3 <em>shader</em> material (the path the first staged-build measurement
    /// missed — a 434 ms main-thread decode): every stage's <c>map</c>/<c>animMap</c> frame, each with the
    /// same companion probes (mirroring ShaderCompiler's CompanionBase wiring). Misses are cheap (the VFS
    /// resolve cache short-circuits them); <c>_shaders</c> is immutable after construction, so reading it
    /// from the worker is safe.
    /// </summary>
    public void PredecodeMaterialTextures(string materialName)
    {
        foreach (string name in EnumerateMaterialTextureNames(materialName))
            PredecodeTexture(name);
    }

    /// <summary>
    /// OFF-THREAD-SAFE: every texture base-name a material build will probe (the base/stage maps + the
    /// channel-suffix companions). The single source for both the worker-side predecode and the per-texture
    /// upload staging (§12.6) — names that don't resolve are cheap no-ops downstream.
    /// </summary>
    public List<string> EnumerateMaterialTextureNames(string materialName)
    {
        var names = new List<string>(8);
        if (string.IsNullOrEmpty(materialName))
            return names;
        string key = StripShaderExtension(materialName);

        if (_shaders.TryGetValue(key, out ShaderDef? def))
        {
            foreach (ShaderStage stage in def.Stages)
            {
                if (!stage.IsLightmap && !stage.IsWhiteImage && !string.IsNullOrEmpty(stage.MapTexture))
                    AddWithCompanions(names, stage.MapTexture);
                if (stage.AnimMap is { Frames.Length: > 0 } anim)
                    foreach (string frame in anim.Frames)
                        AddWithCompanions(names, frame);
            }
            return names;
        }

        AddWithCompanions(names, key);
        return names;
    }

    private static void AddWithCompanions(List<string> names, string textureName)
    {
        string baseName = AssetPaths.StripImageExtension(textureName);
        if (names.Contains(baseName))
            return;
        names.Add(baseName);
        names.Add(baseName + "_norm");
        names.Add(baseName + "_gloss");
        names.Add(baseName + "_glow");
        names.Add(baseName + "_reflect");
        names.Add(baseName + "_shirt");
        names.Add(baseName + "_pants");
    }

    /// <summary>
    /// Compress an about-to-be-uploaded image to a block format when <see cref="TextureCompression"/> asks for
    /// it, cutting its VRAM ~4× (RGBA8 → DXT5/BC7) or ~8× (opaque → DXT1). No-op when the setting is off, when
    /// the image already arrived compressed (the DDS pass-through path — recompressing would be lossy for
    /// nothing), or when it has no mip chain: Godot compresses the whole chain at once, and an unmipped
    /// compressed texture would alias badly on a minified world surface.
    ///
    /// <para>Lightmap pages are excluded for the same reason <see cref="EnsureMipmaps"/> excludes them — DP
    /// samples them unmipped and byte-exact, and block artefacts in a lightmap show up as visible blotching
    /// across large flat surfaces.</para>
    ///
    /// <para>Cost: this is real CPU per texture (BPTC especially — it is the "Good" setting because it is the
    /// slow one). On the menu warm that lands on a worker and is free; on a cold in-match load it is on
    /// whichever thread asked. Failures are non-fatal — the uncompressed image simply uploads as before.</para>
    /// </summary>
    // Compression engagement counters (drained into the session census — a build where the feature silently
    // no-ops must name itself). _s3tcAvailable: -1 unprobed, 0 absent (template build), 1 present (editor).
    private static int _compressOk, _compressFellBack, _compressFailed;
    // Wall time spent inside MaybeCompress, summed across every worker that ran one. Interlocked because the
    // asset streamer compresses on several threads at once; the sum is therefore CPU-time-across-threads, not
    // elapsed - which is the honest number to report for a parallel stage, and is labelled as such.
    private static long _compressMicros;
    // ...of which was paid ON THE FRAME THREAD. A thread-time total alone cannot distinguish "eight threads for
    // one second" from "one thread for eight seconds", and those have opposite fixes, so the split is recorded
    // rather than inferred. See CompressionTimeReport.
    private static long _compressMicrosMain;
    // First encode start and last encode end, as Stopwatch ticks, so the report can state the WALL span the
    // encodes occupied. thread-time / wall-span is the parallelism actually achieved: 1.0 means strictly serial
    // no matter how many threads were nominally available.
    private static long _compressWallFirst, _compressWallLast;
    private static readonly object _compressWallGate = new();
    private static int _ddsSaved, _ddsSaveFailed;

    /// <summary>
    /// DarkPlaces <c>r_texture_dds_save</c>: after compressing a texture, write it to
    /// <c>&lt;userdir&gt;/data/dds/&lt;name&gt;.dds</c> so the next launch loads the blocks instead of
    /// re-encoding them. Set by ClientSettings; a plain static for the same worker-thread reason as
    /// <see cref="TextureCompression"/>.
    /// </summary>
    public static bool DdsSave { get; set; } = true;

    /// <summary>(P6) <c>r_texture_dds_debug</c>: name every texture that reaches the encoder and say whether a
    /// cache file for it already exists on disk — the two ways a texture can re-encode (never cached vs cached
    /// but not readable back) are indistinguishable in the summary line.</summary>
    public static bool DdsDebug { get; set; }

    /// <summary>
    /// <c>r_texturecompression_offthread</c>: run the CPU half of a texture load — picmip, mipmaps and the
    /// block compression — on the asset streamer's worker lane rather than on the frame thread.
    ///
    /// <para><b>Why this exists.</b> A map load pre-decodes each texture on a worker and then calls
    /// <c>LoadTexture</c> on the main thread, and <em>that</em> is where compression sat. So the encode was
    /// serial by construction: one thread, one texture at a time, no matter how wide the worker lane was or
    /// what <see cref="CompressCpuBudget"/> allowed. It is also why the budget knob measured as a no-op — you
    /// cannot cap the concurrency of something that has none.</para>
    ///
    /// <para>Off-thread encoding is not new ground: the menu warm has always compressed on a worker
    /// (<see cref="WarmTextureOffThread"/> → <c>PrepareImage</c>), so the thread-safety of
    /// <c>Image.Compress</c> and of the DDS cache write is already exercised in production. This puts the map
    /// load on the same path.</para>
    ///
    /// <para>The frame-thread path remains for anything that reaches <c>LoadTexture</c> without a predecode —
    /// a lazy mid-match load, an editor probe — which is exactly where the old behaviour's hitch lived.</para>
    ///
    /// <para><b>Never applies to BC7</b> (see <see cref="EncodeOffThread"/>): the pre-pass warms every texture
    /// a material COULD probe, ~45% more than the build consumes, and speculative work is only free when it is
    /// free. S3TC costs ~0.01 CPU-seconds per megapixel and eight workers absorb the waste; BC7 costs ~2.3 and
    /// already saturates the machine on its own, so the same waste is ~420 ms of pure added wall-clock each.</para>
    /// </summary>
    public static bool CompressOffThread { get; set; } = true;

    /// <summary>Whether the worker predecode should do the block compression as well as the decode: only when
    /// the caller asked for it AND the cheap codec (S3TC, not BC7) will run it. See
    /// <see cref="UsesBptcEncoder"/>.</summary>
    private static bool EncodeOffThread => CompressOffThread && !UsesBptcEncoder();

    /// <summary>
    /// <c>r_streamer_prepass</c>: how many decoded images the load pre-pass may leave parked in the handoff at
    /// once. 0 disables the pre-pass entirely; a positive value is both the switch and the depth.
    ///
    /// <para>It is a depth rather than a plain on/off because a parked image is a full mip chain in RAM and the
    /// workers decode far faster than the main thread consumes: stormkeep's texture set is ~3 GB uncompressed
    /// (~950 MB compressed) by the VRAM census, so an unbounded pre-pass would try to hold the entire map's
    /// pixels at once. <see cref="PredecodeTexture"/> makes a worker wait here rather than park past the cap —
    /// back-pressure, with the main thread as the drain.</para>
    /// </summary>
    public static int PredecodeParkCap { get; set; }

    /// <summary>How many images are parked in the predecode handoff right now (diagnostics + back-pressure).</summary>
    public int PredecodeParkedCount => _predecodedImages.Count;

    /// <summary>
    /// Share of the machine texture compression may claim, 0..1 (<c>r_texturecompression_cpubudget</c>).
    /// Same contract as the editor bake's <c>EditorLightBake.CpuBudget</c>, and for the same reason: this is
    /// the other job here that can make a desktop unusable while it runs, and "how much of my computer does
    /// this get" belongs to whoever is sitting at it.
    ///
    /// <para><b>What it caps.</b> How many textures encode CONCURRENTLY on the CPU codec (S3TC/etcpak). It is
    /// live now that <c>r_streamer_prepass</c> feeds the encode from the worker lane; before that it measured
    /// as an exact no-op (budget 1.0 vs 0.25: 20,711 ms vs 20,821 ms), because compression ran on the frame
    /// thread and one thread has no concurrency to cap.</para>
    ///
    /// <para>It has little to say about BC7, which never reaches the worker lane in the first place (see
    /// <see cref="EncodeOffThread"/>) and already fans out across ~13 of 24 cores inside CVTT. Throttling
    /// callers of an already-parallel encoder measured SLOWER, not safer — see <see cref="UsesBptcEncoder"/>.</para>
    /// </summary>
    public static float CompressCpuBudget
    {
        get => _compressCpuBudget;
        set
        {
            float v = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(v - _compressCpuBudget) < 0.001f)
                return;
            _compressCpuBudget = v;
            _compressGate?.Dispose();
            _compressGate = new System.Threading.SemaphoreSlim(BudgetWidth, BudgetWidth);
        }
    }

    private static float _compressCpuBudget = 0.75f;

    /// <summary>Concurrent encodes for the current budget: at least one, never more than the machine has.</summary>
    private static int BudgetWidth =>
        Math.Clamp((int)MathF.Round(System.Environment.ProcessorCount * _compressCpuBudget), 1,
            System.Environment.ProcessorCount);

    /// <summary>
    /// Admission gate for encodes. A semaphore rather than a dedicated thread pool because the callers are
    /// already threads we do not own — the asset streamer's workers, which arrive here mid-decode. Throttling
    /// them where they are keeps the budget honest without a second scheduler and without moving the work off
    /// the thread that is holding the decoded image.
    /// </summary>
    private static System.Threading.SemaphoreSlim? _compressGate;

    /// <summary>
    /// True when this texture will be encoded as <b>BPTC/BC7</b> rather than S3TC: either BC7 was asked for, or
    /// S3TC was and this build has no S3TC encoder so it falls back to BC7.
    ///
    /// <para><b>What makes BC7 different, measured.</b> Godot routes BPTC to CVTT — a CPU encoder dispatched
    /// across <c>WorkerThreadPool.get_thread_count()</c> threads — and it costs about <b>2.3 CPU-seconds per
    /// megapixel</b> against S3TC/etcpak's <b>0.01</b>, roughly 190x, for +9.8 dB (<c>texcompress_bench</c>).
    /// During a cold BC7 load this process burns <b>13.4 of 24 cores</b> continuously and drops to ~2 the
    /// instant the encode ends. So BC7 is not slow because it is serialised — it is already using most of the
    /// machine — it is slow because the work is genuinely enormous.</para>
    ///
    /// <para>Which is exactly why it must not be given speculative work. Three cold stormkeep loads:</para>
    /// <list type="bullet">
    ///   <item>encode on the frame thread, 287 textures — <b>120,595 ms</b></item>
    ///   <item>pre-pass, 8 workers, 411 textures — <b>144,351 ms</b> (oversubscription, not parallelism)</item>
    ///   <item>the same with encodes admitted one at a time, 415 textures — <b>180,758 ms</b> (starves it)</item>
    ///   <item>pre-pass with the encode kept on main, 287 textures — <b>115,256 ms</b></item>
    /// </list>
    /// <para>The pre-pass warms every companion and stage a material could probe, ~45% more textures than the
    /// build consumes. An 8-wide etcpak absorbs that; BC7 cannot. See <see cref="CompressOffThread"/>.</para>
    ///
    /// <para><b>Historical note:</b> this was called <c>UsesBetsy</c> and documented as "Godot routes BPTC to
    /// Betsy, one RenderingDevice on one thread". That was wrong — Betsy implements BC1/3/4/5/6H and ETC, not
    /// BC7 — and the CPU trace above is what disproved it. The behaviour it selects was right either way.</para>
    /// </summary>
    private static bool UsesBptcEncoder() => TextureCompression >= 2 || !S3tcEncoderAvailable();

    private static int _s3tcAvailable = -1, _bptcAvailable = -1;
    private static bool _noEncoderWarned;

    /// <summary>
    /// One-line engagement summary for the session census.
    ///
    /// <para>Reports the encoder probe's CACHED result and never forces one. That distinction is load-bearing:
    /// <see cref="ProbeEncoder"/> calls <c>Image.Compress</c>, and in Godot 4.4+ that routes to the Betsy GPU
    /// compressor, which spins up a background thread owning its own <see cref="RenderingDevice"/>. Godot then
    /// finalizes that device off the render thread at exit ("This function (finalize) can only be called from
    /// the render thread", plus two leaked <c>Object</c> instances — Betsy's compressor is a bare <c>Object</c>).
    /// A census that probed eagerly booted all of that in runs that never compressed a single texture, including
    /// every <c>gl_texturecompression 0</c> run. Verified: headless (no RenderingDevice, so no Betsy) never
    /// leaks.</para>
    /// </summary>
    public static string CompressionStats()
    {
        string s3tc = _s3tcAvailable switch
        {
            1 => "yes",
            0 => "NO (template lacks etcpak encoders; BC7 fallback)",
            _ => "unprobed (nothing compressed this session)",
        };
        return $"compression: mode {TextureCompression}, s3tc {s3tc}"
             + $", ok {_compressOk}, bc7-fallback {_compressFellBack}, failed {_compressFailed}";
    }

    /// <summary>Probe once whether this build can encode S3TC (see the fallback note in MaybeCompress).</summary>
    private static bool S3tcEncoderAvailable()
    {
        if (_s3tcAvailable < 0)
            _s3tcAvailable = ProbeEncoder(Image.CompressMode.S3Tc);
        return _s3tcAvailable == 1;
    }

    /// <summary>Probe once whether this build can encode BPTC/BC7 (cvtt is template-excluded unless built
    /// with <c>cvtt_export_templates=yes</c>).</summary>
    private static bool BptcEncoderAvailable()
    {
        if (_bptcAvailable < 0)
            _bptcAvailable = ProbeEncoder(Image.CompressMode.Bptc);
        return _bptcAvailable == 1;
    }

    private static int ProbeEncoder(Image.CompressMode mode)
    {
        var probe = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        probe.Fill(Colors.White);
        int ok = probe.Compress(mode, Image.CompressSource.Generic) == Error.Ok ? 1 : 0;
        probe.Dispose();
        return ok;
    }

    private static void MaybeCompress(string vpath, Image image)
    {
        // Cheap pre-check before taking the gate: the great majority of calls bail immediately (compression
        // off, already compressed, category disabled), and queueing those behind the budget would serialise
        // work that costs nothing.
        if (TextureCompression <= 0 || image.IsCompressed() || image.IsEmpty() || !image.HasMipmaps())
            return;

        System.Threading.SemaphoreSlim gate =
            _compressGate ??= new System.Threading.SemaphoreSlim(BudgetWidth, BudgetWidth);
        gate.Wait();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { MaybeCompressCore(vpath, image); }
        finally
        {
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            long micros = (t1 - t0) * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
            System.Threading.Interlocked.Add(ref _compressMicros, micros);
            if (VortexArena.Common.Diagnostics.Prof.IsMainThread)
                System.Threading.Interlocked.Add(ref _compressMicrosMain, micros);
            // A short lock rather than a CAS pair: two long writes per encode, against an encode that costs
            // milliseconds, is not measurable — and min/max of two independent Interlocked fields is racy.
            lock (_compressWallGate)
            {
                if (_compressWallFirst == 0 || t0 < _compressWallFirst) _compressWallFirst = t0;
                if (t1 > _compressWallLast) _compressWallLast = t1;
            }
            gate.Release();
        }
    }

    /// <summary>
    /// One line naming what texture compression cost this session, for the load timeline.
    ///
    /// <para>It exists because the cost was INVISIBLE: gl_texturecompression turns ~290 textures into a
    /// multi-thread encode on every launch, and nothing in the loading stages said so - the time simply
    /// appeared inside precache.weapons and render.setup, which are named after something else entirely.
    /// Measured on this box: mode 2 (BC7) adds ~100 s to a map load, mode 1 (S3TC) ~4 s.</para>
    ///
    /// <para>The line reports thread-time, the WALL span the encodes occupied, and how much of the thread-time
    /// was paid on the frame thread — because a bare thread-time total cannot distinguish "eight threads for one
    /// second" from "one thread for eight seconds", and those two have opposite fixes. <c>Nx parallel</c> is
    /// thread-time over wall span: 1.0 means strictly serial however many workers existed.</para>
    /// </summary>
    public static string CompressionTimeReport()
    {
        int n = _compressOk + _compressFellBack + _compressFailed;
        if (n == 0)
            return TextureCompression > 0
                ? $"textures.compress: nothing compressed (gl_texturecompression {TextureCompression}, no eligible textures)"
                : "textures.compress: off (gl_texturecompression 0)";
        double ms = System.Threading.Interlocked.Read(ref _compressMicros) / 1000.0;
        double mainMs = System.Threading.Interlocked.Read(ref _compressMicrosMain) / 1000.0;
        long first, last;
        lock (_compressWallGate) { first = _compressWallFirst; last = _compressWallLast; }
        double wallMs = first == 0 ? 0.0
            : (last - first) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        string what = TextureCompression >= 2 ? "BC7/BPTC, high quality" : "S3TC/DXT, fast";
        string cache = _ddsSaved > 0
            ? $"; cached {_ddsSaved} to {Formats.Vfs.VirtualFileSystem.DdsCacheDir}/ - next launch skips this"
            : (DdsSave ? "" : "; dds cache OFF (r_texture_dds_save 0)");
        if (_ddsSaveFailed > 0)
            cache += $" ({_ddsSaveFailed} cache writes failed)";
        string shape = wallMs > 1.0
            ? $" in {wallMs:0} ms wall ({ms / wallMs:0.00}x parallel, {mainMs * 100.0 / Math.Max(ms, 0.001):0}% on the frame thread)"
            : "";
        return $"textures.compress: {ms:0} ms of thread-time over {n} textures{shape} "
             + $"(gl_texturecompression {TextureCompression} - {what}){cache}";
    }

    /// <summary>
    /// Persist a just-compressed image as DDS beside the user gamedir's <c>dds/</c> tree, which the VFS mounts
    /// and (with r_texture_dds_load) prefers on the next run.
    ///
    /// <para>Writes to the USER gamedir, never into mounted content: a cache must not mutate a shipped pk3dir,
    /// and the user dir is already the highest-priority mount, so what we write is what gets found. Failures
    /// are counted and swallowed — a read-only disk or a full one should cost the cache, not the load.</para>
    /// </summary>
    private static void SaveDdsCache(string vpath, Image image)
    {
        string? fourCc = null;
        uint dxgi = 0;
        int blockBytes;
        switch (image.GetFormat())
        {
            case Image.Format.Dxt1: fourCc = DdsWriter.FourCcDxt1; blockBytes = 8; break;
            case Image.Format.Dxt3: fourCc = DdsWriter.FourCcDxt3; blockBytes = 16; break;
            case Image.Format.Dxt5: fourCc = DdsWriter.FourCcDxt5; blockBytes = 16; break;
            case Image.Format.RgtcR: fourCc = DdsWriter.FourCcBc4; blockBytes = 8; break;
            case Image.Format.RgtcRg: fourCc = DdsWriter.FourCcBc5; blockBytes = 16; break;
            case Image.Format.BptcRgba: fourCc = DdsWriter.Dx10; dxgi = DdsWriter.DxgiBc7Unorm; blockBytes = 16; break;
            default: return;   // not a block format we can express; nothing to cache
        }

        // (P6) Never cache a texture that CAME from the cache. The vpath here is where the bytes were found,
        // so a texture loaded from dds/textures/foo.dds stems to "dds/textures/foo" and would be written to
        // dds/dds/textures/foo.dds — a path nothing ever reads. That is not hypothetical: it had produced 39
        // junk files on this machine. With the pass-through fix below such a texture no longer reaches the
        // encoder at all, but the guard is the honest statement of the invariant either way.
        if (vpath.StartsWith("dds/", StringComparison.OrdinalIgnoreCase)
            || vpath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // (P7) Into the MODE-TAGGED directory the resolver probes, not the shared dds/ tree — so a cache
            // banked at gl_texturecompression 1 cannot silently satisfy a player who has since set 2, and so
            // our writes never shadow the game's own shipped dds/ files.
            string cacheDir = Formats.Vfs.VirtualFileSystem.DdsCacheDir;
            if (cacheDir.Length == 0)
                return;
            string stem = AssetPaths.StripImageExtension(AssetPaths.Normalize(vpath));
            string rel = System.IO.Path.Combine(cacheDir, stem.Replace('/', System.IO.Path.DirectorySeparatorChar)) + ".dds";
            string full = System.IO.Path.Combine(UserPaths.GameDir, rel);
            string? dir = System.IO.Path.GetDirectoryName(full);
            if (dir is null)
                return;
            System.IO.Directory.CreateDirectory(dir);

            byte[] dds = DdsWriter.Write(image.GetWidth(), image.GetHeight(),
                Math.Max(1, image.GetMipmapCount() + 1), fourCc, dxgi, image.GetData(), blockBytes);
            // Write-then-move so a crash mid-write cannot leave a truncated file the next run would trust.
            // The temp name carries the thread id because two workers CAN encode the same vpath at once — the
            // handoff's ContainsKey check is best-effort, not a lock — and a shared "<name>.dds.tmp" made that
            // race a lost cache write (observed once per cold load with the pre-pass at 8 workers).
            string tmp = $"{full}.{System.Environment.CurrentManagedThreadId}.tmp";
            System.IO.File.WriteAllBytes(tmp, dds);
            System.IO.File.Move(tmp, full, overwrite: true);
            System.Threading.Interlocked.Increment(ref _ddsSaved);
        }
        catch
        {
            System.Threading.Interlocked.Increment(ref _ddsSaveFailed);
        }
    }

    private static void MaybeCompressCore(string vpath, Image image)
    {
        int mode = TextureCompression;
        if (mode <= 0 || image.IsCompressed() || image.IsEmpty() || !image.HasMipmaps())
            return;
        // Which gl_texturecompression_* gate this texture answers to. Note the lightmap exclusion that used to
        // be hardcoded here is now the Q3BspLightmaps bucket, which DP and Xonotic both default to 0 — so the
        // behaviour is unchanged, but it is a setting rather than a rule. It stays safe even if a player turns
        // it on: EnsureMipmaps skips lm_ pages, and the !HasMipmaps() guard above already refuses those.
        if (!TextureCategories.Enabled(TextureCompressionCategories, TextureCategories.Classify(vpath)))
            return;

        // ALWAYS Generic, including for _norm — see TexCategory.Normal. CompressSource.Normal would be the
        // quality-correct hint (it weights the channels for a tangent-space normal instead of for perceptual
        // colour), but Godot implements it by declaring the image RG-only: image.cpp's
        // detect_used_channels(COMPRESS_SOURCE_NORMAL) returns USED_CHANNELS_RG unconditionally, which routes
        // to a two-channel BC5/RGTC_RG texture whose BLUE SAMPLES AS 0. Every shader here unpacks a normal as
        // `texture(normal_tex, uv).rgb * 2.0 - 1.0` and uses `.z` (LightmapShader, PlayerSkinShader ×2), so
        // that gives z = -1 and inverts the lighting — the exact failure DdsDecoder's remarks cite as the
        // reason BC5 is CPU-decoded to RGBA8 with Z reconstructed rather than passed through. Generic keeps a
        // real blue channel (DXT1 opaque / DXT5 with alpha / BC7), so a compressed _norm stays correct, just
        // lower quality than BC5 would be. Switching to CompressSource.Normal requires teaching those three
        // shaders to reconstruct Z first (the notes' option E).
        Image.CompressSource src = Image.CompressSource.Generic;
        Image.CompressMode target = mode >= 2 ? Image.CompressMode.Bptc : Image.CompressMode.S3Tc;

        // (BC5 normals 2026-08-02) The Normal category now takes the industry path when the S3TC/RGTC
        // encoder exists: CompressSource.Normal routes to two-channel BC5 (each channel independently
        // block-coded — far better normal gradients than any color codec), and the consuming shaders
        // reconstruct Z behind `norm_rg` (set from the IsRgTexture registry at bind time). The July failure
        // mode — BC5 with shaders that read .z directly — is exactly what that flag closes. Without the
        // encoder (unpatched template) normals fall through with everything else to BC7-Generic, which keeps
        // a real blue channel and needs no flag.
        bool normalMap = TextureCategories.Classify(vpath) == TexCategory.Normal
                         && target == Image.CompressMode.S3Tc && S3tcEncoderAvailable();
        if (normalMap)
        {
            src = Image.CompressSource.Normal;
        }

        // (2026-08-02) S3TC availability is a BUILD property, not a given: Godot's etcpak module registers the
        // BC/ETC ENCODERS under `#ifdef TOOLS_ENABLED` (modules/etcpak/register_types.cpp, verified at
        // 4.6.3-stable), so export templates ship decode-only and every S3Tc compress fails — the release runs
        // printed 250 "skipped" lines while the editor compressed the same set fine. cvtt (BPTC) registers
        // UNconditionally, so templates CAN encode BC7. Probe once and route S3Tc→Bptc when the S3TC encoder
        // is absent: on today's template that trades 4 bpp DXT1 for 8 bpp BC7 on opaque color (half the
        // saving, higher quality) and keeps the feature real instead of a silent no-op. The proper fix is the
        // engine patch restoring etcpak's encoders in templates (tools/engine-patches/), after which the probe
        // passes and this fallback goes quiet. Counters feed the session census (see CompressionStats).
        if (target == Image.CompressMode.S3Tc && !S3tcEncoderAvailable())
        {
            target = Image.CompressMode.Bptc;
            _compressFellBack++;
        }
        // No encoder at all (vortex1 template: etcpak encoders are TOOLS_ENABLED-gated AND the cvtt module
        // is excluded from templates unless built with `cvtt_export_templates=yes` — both verified against
        // 4.6.3-stable). One loud line, then uploads stay uncompressed; the census still reports the counts.
        if (target == Image.CompressMode.Bptc && !BptcEncoderAvailable())
        {
            if (!_noEncoderWarned)
            {
                _noEncoderWarned = true;
                GD.PrintErr("[AssetSystem] gl_texturecompression is ON but this build has NO block-compression "
                    + "encoder (template built without etcpak encode + cvtt_export_templates) — textures upload "
                    + "uncompressed. See tools/engine-patches/README.md.");
            }
            _compressFailed++;
            return;
        }
        try
        {
            // TEMP DIAGNOSTIC (P6): name every texture we are about to encode, and say whether a cache file for
            // it already exists. A texture that re-encodes on EVERY launch despite a cached dds is a broken
            // round trip, not a cold cache — and the two look identical in the summary line.
            if (DdsDebug)
            {
                string stem = AssetPaths.StripImageExtension(AssetPaths.Normalize(vpath));
                string cached = System.IO.Path.Combine(UserPaths.GameDir,
                    Formats.Vfs.VirtualFileSystem.DdsCacheDir,
                    stem.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".dds");
                bool onDisk = System.IO.File.Exists(cached);
                GD.Print($"[dds-debug] encoding '{vpath}' {image.GetWidth()}x{image.GetHeight()} "
                       + $"mips={image.GetMipmapCount()} fmt={image.GetFormat()} "
                       + $"cachefile={(onDisk ? $"PRESENT ({new System.IO.FileInfo(cached).Length}b)" : "absent")}");
            }

            // Scoped so the cost of this setting is attributable rather than folded into whatever frame the
            // texture happened to load on — the whole question of "is CPU compression too slow" is answerable
            // from a capture only if it has its own line.
            using var _ = VortexArena.Common.Diagnostics.Prof.Sample("tex.compress");

            // (G3) Declare the channel set instead of letting Godot infer it from the pixels.
            //
            // Image.Compress runs detect_used_channels(), which reports what the CONTENT happens to use — so a
            // greyscale glow or gloss map came back USED_CHANNELS_R and routed to RGTC_R/BC4, a ONE-CHANNEL
            // format. The shaders do not read these as one channel: glow_tex is sampled `.rgb` (PlayerSkinShader
            // 278/396) and BC4 gives (R,0,0), so a grey glow rendered RED; gloss_tex is sampled `.g` (:360) and
            // gives 0, so `rough = 1.0 - 0` made the surface fully matte, with `.a` (:273) pinning specular
            // power at max. Only mode 1 was affected — measured, dds1/ holds 3 BC4 + 39 BC5 files while dds2/ is
            // 293/293 BC7 — and it reached players through effects-low.cfg and effects-omg.cfg, which are the
            // two presets that set gl_texturecompression 1.
            //
            // This is also what DarkPlaces does, and the reason it never had this bug. Its whole compressed
            // vocabulary is RGB or RGBA (gl_textures.c:129-152: DXT1/DXT1A/DXT3/DXT5 plus sRGB variants, with
            // no RGTC/BC4/BC5 anywhere), and the choice is one line — gl_textures.c:283, `TEXF_ALPHA ? DXT5 :
            // DXT1`. Format follows the texture's declared ROLE and flags, never a scan of its pixels.
            //
            // Costs no VRAM: BC4 and DXT1 are both 8 bytes per 4x4 block. The narrower format bought nothing.
            // Normal maps keep the deliberate BC5 path (see above) — that one is read back through `norm_rg`.
            Error rc;
            if (normalMap)
            {
                rc = image.Compress(target, src);
            }
            else
            {
                Image.UsedChannels channels = image.DetectAlpha() == Image.AlphaMode.None
                    ? Image.UsedChannels.Rgb
                    : Image.UsedChannels.Rgba;
                rc = image.CompressFromChannels(target, channels);
            }
            if (rc != Error.Ok)
            {
                _compressFailed++;
                GD.Print($"[AssetSystem] texture compression skipped for '{vpath}' (unsupported source format).");
            }
            else
            {
                _compressOk++;
                // r_texture_dds_save: bank the result so the next launch reads blocks instead of encoding.
                if (DdsSave)
                    SaveDdsCache(vpath, image);
            }
        }
        catch (Exception ex)
        {
            _compressFailed++;
            GD.Print($"[AssetSystem] texture compression failed for '{vpath}': {ex.Message}; uploading uncompressed.");
        }
    }

    /// <summary>
    /// Bitmask of the enabled <see cref="TexCategory"/> buckets, mirrored out of the cvar store by
    /// <c>ClientSettings.ApplyTextureCompression</c> for the same reason <see cref="TextureCompression"/> is:
    /// the texture path runs on the streamer's worker threads, where reading the cvar store concurrently with
    /// a console write is not safe. One <c>int</c> rather than eleven <c>bool</c>s so a worker reads the whole
    /// set in one atomic load and can never observe a half-applied change.
    ///
    /// <para>Initialised to DP's defaults so the field is correct even before <c>ClientSettings</c> pushes it
    /// (a test or tool that builds an <see cref="AssetSystem"/> without the menu stack gets stock behaviour,
    /// not "everything off").</para>
    /// </summary>
    public static int TextureCompressionCategories { get; set; } = TextureCategories.DefaultMask;

    /// <summary>Drop any unconsumed predecoded images (map change — don't hold decoded pixels for a world
    /// that's gone).</summary>
    public void ClearPredecodedImages() => _predecodedImages.Clear();

    /// <summary>
    /// <c>gl_texturecompression</c>, mirrored here as a plain field so the texture path never does a cvar
    /// lookup (it runs on worker threads, where the cvar store is not safe to read concurrently with a console
    /// write). 0 = off, 1 = "Fast" (S3TC — DXT1/DXT5), 2 = "Good" (BPTC — BC7, same size, better quality but a
    /// much slower compress). Pushed by <c>ClientSettings</c> at boot and on change.
    ///
    /// <para>Defaults to 0. Compression is a QUALITY TRADE (it is lossy, on top of whatever the source already
    /// lost) and it costs real CPU per texture, so it stays opt-in rather than becoming a silent default —
    /// exactly what the menu's existing "Texture compression: None / Fast / Good" slider is for. That slider
    /// has been in the UI all along bound to this cvar name, but nothing read it until now.</para>
    /// </summary>
    public static int TextureCompression { get; set; }

    /// <summary>
    /// (F9 wiring) <c>gl_picmip</c>: drop this many mip levels from every CONTENT texture before upload -
    /// halve the resolution N times, DP's classic texture-memory knob. 0 (the default) touches nothing.
    /// Plain static for the same worker-thread reason as <see cref="TextureCompression"/>; pushed by
    /// <c>ClientSettings</c> and clamped there (the menu's "Lowest" writes the Xonotic joke value 1337, and
    /// its "Good"/"Best" write negatives that meant upscale offsets in Base - both clamp into [0,4]).
    /// Applies to textures DECODED AFTER the change, so in practice: next map load.
    /// </summary>
    public static int Picmip { get; set; }

    /// <summary>
    /// Decode and PREPARE the pixels for <paramref name="vpath"/> — everything that is pure CPU work.
    /// Deliberately separate from <see cref="UploadImage"/> so callers can do this OUTSIDE the upload gate:
    /// mipmap generation and especially <see cref="MaybeCompress"/> (BPTC is the "Good" setting because it
    /// is the slow one) used to run inside it, so one worker's compress blocked every other worker's upload
    /// and the gate stopped being what its own comment claims — "held around the upload itself, never around
    /// the decode".
    /// </summary>
    private Image? PrepareImage(string vpath)
    {
        // Consume the off-thread predecode when one is parked for this vpath (removed so memory is freed).
        if (_predecodedImages.TryRemove(vpath, out Parked parked))
        {
            // Already fully prepped on the worker — running the prep again would halve a picmipped image a
            // second time (MaybePicmip is not idempotent), so this early-out is load-bearing, not an optimisation.
            if (parked.Prepared)
                return parked.Image;
            PrepareDecoded(vpath, parked.Image);
            return parked.Image;
        }
        Image? image = LoadImageFromVpath(vpath);
        if (image == null)
            return null;
        PrepareDecoded(vpath, image);
        return image;
    }

    /// <summary>
    /// The CPU half of a texture load, in the order the three steps require: shrink, then build the mip chain
    /// from the shrunk image, then block-compress the whole chain. Runs on the frame thread from
    /// <see cref="PrepareImage"/>, or on a streamer worker from <see cref="PredecodeTexture"/> when
    /// <see cref="CompressOffThread"/> is set. <b>Not idempotent</b> — see <see cref="Parked"/>.
    /// </summary>
    private static void PrepareDecoded(string vpath, Image image)
    {
        MaybePicmip(vpath, image);     // gl_picmip: halve the resolution N times before mips/compression
        EnsureMipmaps(vpath, image);   // no-op when the image already carries them (a DDS can)
        MaybeCompress(vpath, image);   // gl_texturecompression: shrink RGBA8 to BC before it reaches VRAM
    }

    // (BC5 normals 2026-08-02; widened to format+mips 2026-08-03) What each uploaded texture actually became
    // on the GPU, keyed by instance id. Filled at upload — the one place every cached texture passes through —
    // and id-keyed so it never keeps a freed texture alive.
    //
    // Two readers, and the point of the registry is that neither has to ask the RenderingServer:
    //   * bind sites: an RGTC_RG (BC5) normal map samples blue as 0, so the material must set `norm_rg` and
    //     let the shader reconstruct Z (IsRgTexture).
    //   * the VRAM census: bits-per-pixel and the ×4/3 mip factor (EstimateTextureBytes).
    // The census used to read those back with Texture2D.GetImage(), which is a synchronous round trip to the
    // render thread — see EstimateTextureBytes for what that costs under the threaded renderer. Both facts are
    // free here: they are properties of the Image we are holding anyway.
    private readonly record struct TexMeta(Image.Format Format, bool Mipmaps);
    private static readonly Dictionary<ulong, TexMeta> _texMeta = new();
    private static readonly object _texMetaGate = new();

    /// <summary>True when <paramref name="t"/> uploaded as two-channel RGTC_RG (BC5) — the consuming
    /// material must set its <c>norm_rg</c> uniform so the shader reconstructs Z.</summary>
    public static bool IsRgTexture(Texture2D? t)
    {
        if (t is null || !GodotObject.IsInstanceValid(t))
            return false;
        lock (_texMetaGate)
            return _texMeta.TryGetValue(t.GetInstanceId(), out TexMeta m) && m.Format == Image.Format.RgtcRg;
    }

    /// <summary>The GPU half, and the only part that belongs inside the upload gate.</summary>
    /// <summary>
    /// Apply <see cref="Picmip"/>: shrink the decoded image by half per level. Skipped for UI/font pages and
    /// BSP lightmaps (DP's picmip never touches either - a blurred HUD is pure loss and lightmaps are already
    /// tiny), for images that arrived pre-compressed (a BC-block image cannot be resized in place), and once
    /// a dimension would fall under 16 px (past that the texture is noise).
    /// </summary>
    private static void MaybePicmip(string vpath, Image image)
    {
        int levels = Picmip;
        if (levels <= 0 || image.IsCompressed())
            return;
        TexCategory cat = TextureCategories.Classify(vpath);
        if (cat is TexCategory.TwoD or TexCategory.Q3BspLightmaps)
            return;
        int w = image.GetWidth(), h = image.GetHeight();
        while (levels-- > 0 && w >= 32 && h >= 32)
        {
            w >>= 1;
            h >>= 1;
        }
        if (w != image.GetWidth())
            image.Resize(w, h, Image.Interpolation.Bilinear);
    }

    private static Texture2D? UploadImage(string vpath, Image image)
    {
        try
        {
            var tex = ImageTexture.CreateFromImage(image);
            // Record what went up while we still have the Image in hand. Asking the texture later means
            // asking the render thread; see _texMeta.
            var meta = new TexMeta(image.GetFormat(), image.HasMipmaps());
            lock (_texMetaGate)
                _texMeta[tex.GetInstanceId()] = meta;
            return tex;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetSystem] could not create texture from '{vpath}': {ex.Message}");
            return null;
        }
    }

    private Texture2D? LoadTextureFromVpath(string vpath)
    {
        Image? image = PrepareImage(vpath);
        return image is null ? null : UploadImage(vpath, image);
    }

    // (perf 2026-07-03) Grow-only per-thread FILE buffer for the tga/dds read path: `_vfs.ReadBytes`'s fresh
    // `new byte[]` per texture (4-16 MB for an uncompressed TGA / mip-chained DDS, ~11 textures per player
    // model) was the dominant LOH churn behind the 130-430 MB single-frame allocation storms → gen2
    // collections at load/join. One retained buffer per decoding thread (streamer workers + main), bounded by
    // the largest texture file. PNG/JPG keep the exact-array path (Godot's LoadXxxFromBuffer marshals the
    // WHOLE array, and those files are small/rare in Xonotic data).
    [ThreadStatic] private static byte[]? _fileScratch;

    /// <summary>
    /// Decode one resolved vpath. Link following is NOT done here: <see cref="VirtualFileSystem"/> resolves
    /// a link to its target at mount time and <c>ResolveImage</c> hands back the final vpath, so by the time
    /// a name reaches this method it already names the entry that holds the bytes.
    ///
    /// <para>It used to be done here, by reading every image in full before decoding it just to discover it
    /// was not a 20-byte stub — a second read and a full-size unpooled allocation per texture, in front of
    /// the pooled path built to avoid exactly that. Doing it once from the zip's central directory is both
    /// cheaper and, because the mount validates the target against its OWN index, incapable of the
    /// cross-pack redirect the per-load version allowed.</para>
    /// </summary>
    private Image? LoadImageFromVpath(string vpath)
    {
        string ext = AssetPaths.GetExtension(vpath);
        if (ext is "tga" or "dds")
        {
            int length;
            try
            {
                length = _vfs.ReadBytesInto(vpath, ref _fileScratch);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AssetSystem] read failed for texture '{vpath}': {ex.Message}");
                return null;
            }
            byte[] buf = _fileScratch!;
            return ext == "tga" ? DecodeTga(buf, length, vpath) : DecodeDds(buf, length, vpath);
        }

        byte[] bytes;
        try
        {
            bytes = _vfs.ReadBytes(vpath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetSystem] read failed for texture '{vpath}': {ex.Message}");
            return null;
        }

        return ext switch
        {
            "png" => LoadViaGodot(bytes, isPng: true, vpath),
            "jpg" or "jpeg" => LoadViaGodot(bytes, isPng: false, vpath),
            _ => DecodeUnknown(bytes, vpath), // pcx/wal/etc.: unsupported
        };
    }

    private static Image? DecodeTga(byte[] bytes, int length, string vpath)
    {
        // Primary: our own decoder (handles the full Xonotic TGA spread, RLE included).
        Image? img = TgaDecoder.Decode(bytes, length);
        if (img != null)
            return img;

        // Fallback: let Godot try, in case of an exotic header our decoder rejected. Godot's buffer loader
        // marshals the whole array, so the (rare) fallback needs an exact-length copy of the pooled buffer.
        var exact = new byte[length];
        Array.Copy(bytes, exact, length);
        var godot = new Image();
        if (godot.LoadTgaFromBuffer(exact) == Error.Ok)
            return godot;

        GD.PrintErr($"[AssetSystem] failed to decode TGA '{vpath}'.");
        return null;
    }

    private static Image? LoadViaGodot(byte[] bytes, bool isPng, string vpath)
    {
        var img = new Image();
        Error err = isPng ? img.LoadPngFromBuffer(bytes) : img.LoadJpgFromBuffer(bytes);
        if (err == Error.Ok)
            return img;
        GD.PrintErr($"[AssetSystem] failed to decode image '{vpath}' ({err}).");
        return null;
    }

    private static Image? DecodeDds(byte[] bytes, int length, string vpath)
    {
        // Xonotic ships GPU-precompressed S3TC textures under a parallel dds/ tree; for some maps (e.g.
        // stormkeep) the .dds is the only variant present. Full-chain DXT1/3/5 files PASS THROUGH compressed
        // (no CPU decode, mips kept, S3TC on the GPU — see DdsDecoder); the rest decode to RGBA8 as before.
        Image? img = DdsDecoder.Decode(bytes, length);
        if (img != null)
            return img;
        GD.PrintErr($"[AssetSystem] failed to decode DDS '{vpath}'.");
        return null;
    }

    private static Image? DecodeUnknown(byte[] bytes, string vpath)
    {
        // PCX/WAL and other legacy formats aren't shipped by the data we mount; resolve to null so the
        // caller falls back (the resolver already preferred tga/png/jpg/dds).
        _ = bytes;
        string ext = AssetPaths.GetExtension(vpath);
        GD.PrintErr($"[AssetSystem] unsupported image format for '{vpath}' (ext '{ext}').");
        return null;
    }

    // -------------------------------------------------------------------------------------------------
    //  Fallbacks / engine images
    // -------------------------------------------------------------------------------------------------

    /// <summary>A shared 1×1 white texture for <c>$whiteimage</c> stages and missing-albedo lightmaps.</summary>
    internal Texture2D WhiteTexture()
    {
        if (_whiteTexture != null)
            return _whiteTexture;
        var img = Image.CreateFromData(1, 1, false, Image.Format.Rgba8, new byte[] { 255, 255, 255, 255 });
        _whiteTexture = ImageTexture.CreateFromImage(img);
        return _whiteTexture;
    }

    /// <summary>A shared 1×1 black texture — the identity for additive/EMISSION samplers (a bolt shader
    /// whose <c>_glow</c> companion is missing binds this so the emission term contributes nothing).</summary>
    internal Texture2D BlackTexture()
    {
        if (_blackTexture != null)
            return _blackTexture;
        var img = Image.CreateFromData(1, 1, false, Image.Format.Rgba8, new byte[] { 0, 0, 0, 255 });
        _blackTexture = ImageTexture.CreateFromImage(img);
        return _blackTexture;
    }

    /// <summary>The magenta/black checkerboard used when a texture cannot be resolved.</summary>
    internal Texture2D FallbackTexture()
    {
        if (_fallbackTexture != null)
            return _fallbackTexture;

        const int n = 16;
        var data = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            bool magenta = ((x >> 2) + (y >> 2) & 1) == 0;
            int d = (y * n + x) * 4;
            if (magenta) { data[d] = 255; data[d + 1] = 0; data[d + 2] = 255; }
            else         { data[d] = 0;   data[d + 1] = 0; data[d + 2] = 0;   }
            data[d + 3] = 255;
        }
        var img = Image.CreateFromData(n, n, false, Image.Format.Rgba8, data);
        _fallbackTexture = ImageTexture.CreateFromImage(img);
        return _fallbackTexture;
    }

    /// <summary>The shared unlit material wrapping <see cref="FallbackTexture"/> (the never-null result).</summary>
    internal Material FallbackMaterial()
    {
        return _fallbackMaterial ??= new StandardMaterial3D
        {
            ResourceName = "__missing__",
            AlbedoTexture = FallbackTexture(),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
    }

    // -------------------------------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Normalize a shader/texture name to the dictionary key: forward slashes, lower-cased, with any
    /// trailing image/script extension stripped. Mirrors the parser's case-insensitive, extensionless
    /// keys so <c>"textures/foo.tga"</c>, <c>"textures/foo"</c> and <c>"TEXTURES/FOO"</c> all collide.
    /// </summary>
    internal static string StripShaderExtension(string name)
    {
        string norm = AssetPaths.Normalize(name);
        // Strip a known image extension; also strip a ".shader" if a caller passed one.
        string ext = AssetPaths.GetExtension(norm);
        if (ext == "shader")
            return AssetPaths.StripExtension(norm);
        return AssetPaths.StripImageExtension(norm);
    }
}
