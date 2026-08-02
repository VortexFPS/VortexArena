using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Materials;
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
    public Material ResolveMaterial(string nameOrTexture)
    {
        if (string.IsNullOrEmpty(nameOrTexture))
            return FallbackMaterial();

        string key = StripShaderExtension(nameOrTexture);
        lock (_materialCacheGate)
            if (_materialCache.TryGetValue(key, out Material? cached))
                return cached;

        Material result;
        try
        {
            // Compile OUTSIDE the lock so a slow shader build never blocks another material's lookup.
            if (_shaders.TryGetValue(key, out ShaderDef? def))
            {
                result = ShaderCompiler.Compile(def, this) ?? BuildPlainMaterial(key);
            }
            else
            {
                result = BuildPlainMaterial(key);
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
            if (_materialCache.TryGetValue(key, out Material? raced))
                return raced;
            _materialCache[key] = result;
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
    private Material BuildPlainMaterial(string textureBase)
    {
        Texture2D? albedo = LoadTexture(textureBase);
        if (albedo == null)
            return FallbackMaterial();

        // A texture with team-colorable (_shirt/_pants) or reflective (_reflect) masks must compile to the
        // dedicated skin shader — StandardMaterial3D cannot express the tinted additive masks. This covers
        // the (extensionless, shaderless) model skins Xonotic loads straight by texture name.
        ShaderMaterial? skin = TryBuildSkinMaterial(textureBase, albedo);
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
    internal ShaderMaterial? TryBuildSkinMaterial(string baseName, Texture2D? albedo)
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
        if (shirt == null && pants == null && reflect == null)
            return null; // not a team-colorable / reflective skin → ordinary material

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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Image> _predecodedImages =
        new(StringComparer.Ordinal);

    /// <summary>
    /// OFF-THREAD-SAFE: resolve + decode one texture into the handoff so the next main-thread
    /// <see cref="LoadTexture"/> of the same name skips the read+decode. Idempotent; a miss is a no-op.
    /// (Worst case — the texture was already GPU-cached — the entry sits unused until consumed or
    /// <see cref="ClearPredecodedImages"/>.)
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
        Image? img = LoadImageFromVpath(vpath);
        if (img is not null)
        {
            EnsureMipmaps(vpath, img);   // on the WORKER — the main-thread upload then includes mips for free
            _predecodedImages.TryAdd(vpath, img);
        }
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
    private static void MaybeCompress(string vpath, Image image)
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
        try
        {
            // Scoped so the cost of this setting is attributable rather than folded into whatever frame the
            // texture happened to load on — the whole question of "is CPU compression too slow" is answerable
            // from a capture only if it has its own line.
            using var _ = VortexArena.Common.Diagnostics.Prof.Sample("tex.compress");
            if (image.Compress(target, src) != Error.Ok)
                GD.Print($"[AssetSystem] texture compression skipped for '{vpath}' (unsupported source format).");
        }
        catch (Exception ex)
        {
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
        if (!_predecodedImages.TryRemove(vpath, out Image? image))
            image = LoadImageFromVpath(vpath);
        if (image == null)
            return null;
        EnsureMipmaps(vpath, image);   // no-op when the worker predecode already generated them
        MaybeCompress(vpath, image);   // gl_texturecompression: shrink RGBA8 to BC before it reaches VRAM
        return image;
    }

    /// <summary>The GPU half, and the only part that belongs inside the upload gate.</summary>
    private static Texture2D? UploadImage(string vpath, Image image)
    {
        try
        {
            return ImageTexture.CreateFromImage(image);
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
