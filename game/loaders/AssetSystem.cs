using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Formats.Vfs;

namespace XonoticGodot.Game.Loaders;

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
/// Godot-free <c>XonoticGodot.Formats</c> library. Conversions between the two are explicit. Materials and
/// textures are cached and shared, so callers must treat returned resources as read-only.</para>
/// </summary>
public sealed class AssetSystem
{
    private readonly VirtualFileSystem _vfs;

    // name (extension-stripped, lower-cased) -> parsed shader. Case-insensitive lookups.
    private readonly IReadOnlyDictionary<string, ShaderDef> _shaders;

    // Caches. Materials are keyed by the *requested* name (so a shader and a bare texture of the same
    // stem share a slot, matching Q3 where the shader shadows the texture). Textures are keyed by the
    // resolved vpath so two names that resolve to the same file share one GPU texture.
    private readonly Dictionary<string, Material> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D?> _textureCache = new(StringComparer.Ordinal);

    private Texture2D? _fallbackTexture;       // magenta/black checkerboard
    private Material? _fallbackMaterial;        // unlit material wrapping the checkerboard
    private Texture2D? _whiteTexture;           // 1×1 white ($whiteimage)
    private Texture2D? _blackTexture;           // 1×1 black (missing _glow companion)

    // Autosprite deform materials, cached separately from _materialCache: the same shader NAME compiles
    // to a ShaderMaterial with baked CUSTOM0/1 semantics on the model path but keeps the plain
    // billboard fallback on the ordinary (BSP) path. A null entry caches "not an autosprite shader".
    private readonly Dictionary<string, ShaderMaterial?> _autospriteCache = new(StringComparer.OrdinalIgnoreCase);

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
        if (_materialCache.TryGetValue(key, out Material? cached))
            return cached;

        Material result;
        try
        {
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

        _materialCache[key] = result;
        return result;
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
        if (_autospriteCache.TryGetValue(key, out ShaderMaterial? cached))
            return cached;

        ShaderMaterial? result = null;
        try
        {
            if (_shaders.TryGetValue(key, out ShaderDef? def))
                result = ShaderCompiler.CompileAutosprite(def, this);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetSystem] autosprite material '{key}' failed to compile: {ex.Message}");
        }

        _autospriteCache[key] = result;
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
                XonoticGodot.Common.Diagnostics.Log.Info("[AssetSystem] cubemaps/default/sky*: face missing — weapon reflection falls back to the mild sheen.");
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

        if (_textureCache.TryGetValue(vpath, out Texture2D? cached))
            return cached;

        Texture2D? tex = LoadTextureFromVpath(vpath);
        _textureCache[vpath] = tex; // cache even null to avoid re-probing a known-bad image
        return tex;
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
        using var _ = XonoticGodot.Common.Diagnostics.Prof.Sample("stream.predecode");
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

    /// <summary>Drop any unconsumed predecoded images (map change — don't hold decoded pixels for a world
    /// that's gone).</summary>
    public void ClearPredecodedImages() => _predecodedImages.Clear();

    private Texture2D? LoadTextureFromVpath(string vpath)
    {
        // Consume the off-thread predecode when one is parked for this vpath (removed so memory is freed).
        if (!_predecodedImages.TryRemove(vpath, out Image? image))
            image = LoadImageFromVpath(vpath);
        if (image == null)
            return null;
        EnsureMipmaps(vpath, image);   // no-op when the worker predecode already generated them

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

    // (perf 2026-07-03) Grow-only per-thread FILE buffer for the tga/dds read path: `_vfs.ReadBytes`'s fresh
    // `new byte[]` per texture (4-16 MB for an uncompressed TGA / mip-chained DDS, ~11 textures per player
    // model) was the dominant LOH churn behind the 130-430 MB single-frame allocation storms → gen2
    // collections at load/join. One retained buffer per decoding thread (streamer workers + main), bounded by
    // the largest texture file. PNG/JPG keep the exact-array path (Godot's LoadXxxFromBuffer marshals the
    // WHOLE array, and those files are small/rare in Xonotic data).
    [ThreadStatic] private static byte[]? _fileScratch;

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
