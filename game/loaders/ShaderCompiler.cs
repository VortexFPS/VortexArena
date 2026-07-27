using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Formats.Vfs;

namespace XonoticGodot.Game.Loaders;

/// <summary>
/// Compiles a parsed <see cref="ShaderDef"/> into a Godot <see cref="Material"/>.
///
/// The translation, in order of increasing complexity (asset-pipeline.md §4):
/// <list type="number">
///   <item><b>Nodraw / textureless</b> — a 0-alpha material flagged <c>__nodraw__</c> so the mesh
///   builder can skip the surface; never null.</item>
///   <item><b>Single opaque stage</b> — a <see cref="StandardMaterial3D"/>: albedo from the stage map,
///   with the <c>_norm</c>/<c>_gloss</c>/<c>_glow</c> channel-suffix companions wired in.</item>
///   <item><b>Multi-stage</b> — a base material plus a <see cref="Material.NextPass"/> chain, one extra
///   material per additional render stage, each with the transparency/blend its <see cref="BlendMode"/>
///   implies (Add→<c>BlendMode.Add</c>, Blend→<c>Alpha</c>/<c>Mix</c>, Filter→<c>Mul</c>).</item>
///   <item><b>Animated (tcMod / deformVertexes)</b> — any stage that scrolls/scales/rotates its UVs or
///   deforms vertices is emitted as a <see cref="ShaderMaterial"/> whose <c>.gdshader</c> source (built
///   here as a C# string) drives <c>UV</c>/<c>VERTEX</c> from <c>TIME</c>, preserving Q3 op order.</item>
///   <item><b>$lightmap</b> — a stage referencing <c>$lightmap</c> is marked so the BSP path can route
///   it through <see cref="LightmapShader"/> with the real lightmap page (the compiler can't see UV2 /
///   the page here).</item>
/// </list>
/// <c>cull none</c>→two-sided; a masked stage (<c>alphaFunc</c>) → alpha-scissor; deform billboards
/// (<c>autosprite</c>) set the material's billboard mode.
/// </summary>
public static class ShaderCompiler
{
    /// <summary>A resource-name marker the mesh builder checks to skip a non-drawn surface.</summary>
    public const string NoDrawMarker = "__nodraw__";

    /// <summary>A resource-name marker the BSP path checks to route a surface through the lightmap shader.</summary>
    public const string LightmapMarker = "__lightmap__";

    /// <summary>
    /// Compile <paramref name="def"/> into a never-null Godot material, resolving textures through
    /// <paramref name="ctx"/>.
    /// </summary>
    public static Material Compile(ShaderDef def, AssetSystem ctx)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(ctx);

        // 1) Nodraw: emit a transparent, flagged material the mesh builder can drop.
        if (def.IsNoDraw)
            return MakeNoDraw(def);

        // Sky surfaces draw via the skybox, not a per-face material; give the builder a flagged
        // placeholder (the env/sky setup lives in the map/world builder, not here).
        if (def.IsSky)
            return MakeSky(def);

        // Hero-material override: water / portal-mirror-camera / force-field shaders whose visual intent the
        // generic stage path can't reproduce (they would degrade to a plain/white surface). Recognized up
        // front so the purpose-built material wins over the fallback chain below.
        Material? hero = HeroMaterials.TryOverride(def, ctx);
        if (hero != null)
            return hero;

        // Collect the stages that actually paint pixels (skip pure lightmap-only? no — lightmap is a
        // real visual stage, but it needs the page from the BSP, so it's marked, not textured here).
        var stages = SelectRenderStages(def);

        if (stages.Count == 0)
        {
            // A global-only shader (e.g. just surfaceparms, or a texture-named shader with no stages):
            // fall back to a plain material from the shader name (which is usually also a texture path).
            return BuildSingleStandard(def, null, ctx) ?? ctx.FallbackMaterial();
        }

        // If any selected stage references $lightmap, flag the whole material for the lightmap path.
        bool usesLightmap = false;
        foreach (var s in stages)
            if (s.IsLightmap) usesLightmap = true;

        // 2) Single stage.
        if (stages.Count == 1)
        {
            ShaderStage only = stages[0];
            // A team-colorable / reflective skin (_shirt/_pants/_reflect siblings) needs the dedicated skin
            // shader, which BuildSingleStandard (StandardMaterial3D-only) can't return. Try it first for a
            // static, real-image stage (the player/model skin case); animated stages keep their own path.
            Material? skin = TrySkinMaterial(def, only, ctx);
            Material mat = skin
                ?? (NeedsAnimatedShader(def, only)
                    ? BuildAnimatedStage(def, only, ctx, isBasePass: true)
                    : (Material?)BuildSingleStandard(def, only, ctx) ?? ctx.FallbackMaterial());
            if (usesLightmap)
                mat.ResourceName = Combine(mat.ResourceName, LightmapMarker);
            return mat;
        }

        // 3) Multi-stage → base + next_pass chain. The first stage is the base; each subsequent stage
        // becomes a pass appended to the chain. Animated stages within the chain use a ShaderMaterial.
        Material baseMat = BuildChainStage(def, stages[0], ctx, isBasePass: true);
        Material tail = baseMat;
        for (int i = 1; i < stages.Count; i++)
        {
            Material pass = BuildChainStage(def, stages[i], ctx, isBasePass: false);
            tail.NextPass = pass;
            tail = pass;
        }

        if (usesLightmap)
            baseMat.ResourceName = Combine(baseMat.ResourceName, LightmapMarker);
        return baseMat;
    }

    // -------------------------------------------------------------------------------------------------
    //  Autosprite deform (opt-in — the MD3 model path; BSP keeps the billboard approximation)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The autosprite deform on <paramref name="def"/>: null when none, else whether it is the axial
    /// variant (<c>autosprite2</c>). First autosprite-family deform wins (Q3 shaders carry at most one).
    /// </summary>
    public static bool? AutospriteAxial(ShaderDef def)
    {
        foreach (DeformVertexes d in def.Deforms)
        {
            if (d.Type == DeformType.Autosprite) return false;
            if (d.Type == DeformType.Autosprite2) return true;
        }
        return null;
    }

    /// <summary>
    /// Compile the faithful autosprite/autosprite2 deform material for <paramref name="def"/> — a
    /// <see cref="ShaderMaterial"/> whose vertex stage rebuilds each quad on the view axes from the
    /// <c>CUSTOM0/1</c> frames the MD3 builder bakes (<c>AutospriteQuads</c>), and whose fragment is DP's
    /// bolt shading: lit base (<c>rgbGen lightingDiffuse</c>) + fullbright <c>_glow</c> companion on
    /// EMISSION, additive (NOT unshaded — the corrected playtest-#38 model). Returns null when the def has
    /// no autosprite deform or no usable image stage; callers fall back to <see cref="Compile"/>.
    /// </summary>
    public static ShaderMaterial? CompileAutosprite(ShaderDef def, AssetSystem ctx)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(ctx);

        bool? axial = AutospriteAxial(def);
        if (axial == null || def.IsNoDraw || def.IsSky)
            return null;

        var stages = SelectRenderStages(def);
        if (stages.Count == 0)
            return null;
        ShaderStage stage = stages[0]; // the bolt shaders are single-stage; a chain's base stage drives
        if (stage.IsLightmap)
            return null;

        string albedoName = StageImageName(stage);
        Texture2D? albedo = ResolveStageTexture(stage, albedoName, ctx);
        if (albedo == null)
            return null;

        string code = AutospriteShaderGen.Generate(def, stage, axial.Value);
        var mat = new ShaderMaterial
        {
            Shader = new Shader { Code = code },
            ResourceName = def.Name + "/autosprite",
        };
        mat.SetShaderParameter("albedo_tex", albedo);

        // The fullbright companion (laser.tga -> laser_glow.tga, shipped next to every bolt texture).
        // Missing companion -> 1×1 black: the EMISSION term contributes nothing.
        Texture2D? glow = ctx.LoadTexture(AssetPaths.StripImageExtension(albedoName) + "_glow");
        mat.SetShaderParameter("glow_tex", glow ?? ctx.BlackTexture());
        return mat;
    }

    // -------------------------------------------------------------------------------------------------
    //  Stage selection
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Pick the stages worth turning into render passes. Drops pure <c>detail</c> passes (they only
    /// sharpen up close and have no correctness value) and stages with no usable image (no map and not
    /// $lightmap/$white). Caps at a small number of passes so a pathological shader can't build an
    /// unbounded next_pass chain.
    /// </summary>
    private static List<ShaderStage> SelectRenderStages(ShaderDef def)
    {
        const int maxPasses = 4;
        var result = new List<ShaderStage>();
        foreach (ShaderStage s in def.Stages)
        {
            if (s.Detail)
                continue;
            bool hasImage = s.IsLightmap || s.IsWhiteImage
                            || !string.IsNullOrEmpty(s.MapTexture)
                            || s.AnimMap is { Frames.Length: > 0 };
            if (!hasImage)
                continue;
            result.Add(s);
            if (result.Count >= maxPasses)
                break;
        }
        return result;
    }

    // -------------------------------------------------------------------------------------------------
    //  Single StandardMaterial3D (opaque stage)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="StandardMaterial3D"/> for a single opaque stage (or, when
    /// <paramref name="stage"/> is null, straight from the shader name as a texture). Wires the
    /// channel-suffix companions and applies cull/alpha-test.
    /// </summary>
    private static StandardMaterial3D? BuildSingleStandard(ShaderDef def, ShaderStage? stage, AssetSystem ctx)
    {
        string albedoName = stage != null ? StageImageName(stage) : def.Name;
        Texture2D? albedo = ResolveStageTexture(stage, albedoName, ctx);
        if (albedo == null && stage is { IsLightmap: true })
            albedo = ctx.WhiteTexture(); // lightmap stage with no albedo: white base, page comes later
        if (albedo == null)
            return null;

        var mat = new StandardMaterial3D
        {
            ResourceName = def.Name,
            AlbedoTexture = albedo,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };

        // Companions only make sense for a real texture base (not $lightmap/$white). The team-colorable
        // _shirt/_pants skin path is taken earlier in Compile (TrySkinMaterial); here _norm/_gloss/_glow/
        // _reflect are wired onto the StandardMaterial3D.
        string? companionBase = CompanionBase(stage, albedoName);
        if (companionBase != null)
            ctx.WireCompanions(mat, companionBase);

        // A single-stage surface whose stage has a non-opaque blendFunc (e.g. glass' blendFunc blend) is
        // transparent: honor it so the base material blends instead of painting a solid wall.
        ApplyBaseBlend(mat, stage);
        ApplyRgbGen(mat, stage);
        ApplyCull(mat, def);
        ApplyClamp(mat, stage);
        ApplyAlphaTest(mat, stage);
        ApplyStaticTcModScale(mat, stage);
        ApplyDeformBillboard(mat, def);
        return mat;
    }

    /// <summary>
    /// If <paramref name="stage"/> paints a real image whose base name has the team-colorable
    /// (<c>_shirt</c>/<c>_pants</c>) or reflective (<c>_reflect</c>) companion masks, build the dedicated
    /// Darkplaces skin material (<see cref="PlayerSkinShader"/>) — the generic StandardMaterial3D cannot
    /// express the tinted additive masks. Returns null when the stage has no real image or no such siblings,
    /// in which case the normal single-stage path applies. Faithful to DP's <c>_pants</c>/<c>_shirt</c>/
    /// <c>_reflect</c> skin-frame suffixes (<c>darkplaces.txt</c>, <c>gl_rmain.c</c>).
    /// </summary>
    private static Material? TrySkinMaterial(ShaderDef def, ShaderStage stage, AssetSystem ctx)
    {
        string albedoName = StageImageName(stage);
        string? companionBase = CompanionBase(stage, albedoName);
        if (companionBase == null)
            return null;

        Texture2D? albedo = ResolveStageTexture(stage, albedoName, ctx);
        if (albedo == null)
            return null;

        ShaderMaterial? skin = ctx.TryBuildSkinMaterial(companionBase, albedo);
        if (skin == null)
            return null;

        skin.ResourceName = def.Name + "/skin";
        return skin;
    }

    // -------------------------------------------------------------------------------------------------
    //  Chain pass (base or next_pass)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Build one material in a multi-stage chain. Picks a <see cref="ShaderMaterial"/> when the stage is
    /// animated, else a <see cref="StandardMaterial3D"/>; applies the blend mode for a non-base pass.
    /// </summary>
    private static Material BuildChainStage(ShaderDef def, ShaderStage stage, AssetSystem ctx, bool isBasePass)
    {
        if (NeedsAnimatedShader(def, stage))
            return BuildAnimatedStage(def, stage, ctx, isBasePass);

        string albedoName = StageImageName(stage);
        Texture2D? albedo = ResolveStageTexture(stage, albedoName, ctx) ?? ctx.WhiteTexture();

        var mat = new StandardMaterial3D
        {
            ResourceName = def.Name + (isBasePass ? "" : "/pass"),
            AlbedoTexture = albedo,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };

        if (isBasePass)
        {
            string? companionBase = CompanionBase(stage, albedoName);
            if (companionBase != null)
                ctx.WireCompanions(mat, companionBase);
            // Honor a non-opaque blendFunc on the BASE stage (e.g. glass' blendFunc blend); otherwise the
            // first stage is treated as opaque and a transparent surface paints as a solid wall.
            ApplyBaseBlend(mat, stage);
        }
        else
        {
            ApplyBlend(mat, stage);
        }

        ApplyRgbGen(mat, stage);
        ApplyCull(mat, def);
        ApplyClamp(mat, stage);
        ApplyAlphaTest(mat, stage);
        ApplyStaticTcModScale(mat, stage);
        ApplyDeformBillboard(mat, def);
        return mat;
    }

    /// <summary>Map a stage's <see cref="BlendMode"/> onto a StandardMaterial3D's transparency + blend.</summary>
    private static void ApplyBlend(StandardMaterial3D mat, ShaderStage stage)
    {
        switch (stage.BlendMode)
        {
            case BlendMode.Add:
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                // Q3 `blendfunc add` stages are UNLIT self-luminous adds (GL_ONE GL_ONE over whatever is
                // already there — no lightmap/scene-light modulation). A Godot Add material left PerPixel-
                // shaded multiplies the texture by scene lighting first, so in a dim room the addition is
                // near-black — the "totally flat/dark" energy bolts of playtest #38 (laser/electro
                // projectile cores) and dim additive map FX. Unshaded = the faithful glow-in-the-dark look.
                mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                break;
            case BlendMode.Filter:
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.BlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                // Q3 `blendfunc filter` multiplies the framebuffer — lighting the multiplier would double-
                // count the scene light (the surface below is already lit). Unshaded, like Add.
                mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                break;
            case BlendMode.Blend:
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                break;
            case BlendMode.Custom:
                // Best-effort: treat an unclassified blend as alpha mix.
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                break;
            case BlendMode.Opaque:
            default:
                break; // opaque overwrite — leave defaults
        }
        // Additional passes never write depth (they layer over the base).
        mat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
    }

    /// <summary>
    /// Apply transparency to a <b>base</b>-pass material whose own stage carries a non-opaque
    /// <c>blendFunc</c> — e.g. a glass surface whose first (and only diffuse) stage is <c>blendFunc blend</c>.
    /// The compile path otherwise treats the first stage as an opaque base and drops its blend, painting a
    /// transparent surface as a solid wall. Skipped for an opaque stage (the common world case, left untouched)
    /// and for an alpha-TEST stage (masked via <see cref="ApplyAlphaTest"/>'s scissor, not blended). Unlike
    /// <see cref="ApplyBlend"/> (layered passes) the base keeps its default depth-draw so it still reads as a
    /// real surface. Mirrors the lightmap path's translucency routing (<c>AssetSystem.LightmapDiffuse</c>).
    /// </summary>
    private static void ApplyBaseBlend(StandardMaterial3D mat, ShaderStage? stage)
    {
        if (stage == null || !string.IsNullOrEmpty(stage.AlphaFunc))
            return;
        switch (stage.BlendMode)
        {
            case BlendMode.Add:
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                // Q3 additive stages are UNLIT self-luminous adds — a PerPixel-shaded Add material goes
                // near-black in dim rooms (scene-light × texture before the add). This was the flat/dark
                // laser/electro projectile bolt (playtest #38: laser_projectile_core & co. compile here).
                mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                break;
            case BlendMode.Filter:
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.BlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; // multiplies the already-lit framebuffer
                break;
            case BlendMode.Blend:
            case BlendMode.Custom:
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                break;
            case BlendMode.Opaque:
            default:
                break; // opaque base — leave the StandardMaterial3D fully opaque
        }
    }

    /// <summary>
    /// Apply a stage's <c>rgbGen</c> to a StandardMaterial3D. DP's default on model stages is
    /// <c>lightingDiffuse</c> (the surface is lit) — Godot's lit pipeline is the analogue, so
    /// identity/lighting gens leave the albedo white. <c>const</c> tints the albedo by a fixed color;
    /// <c>vertex</c>/<c>exactVertex</c> route the mesh's vertex colors into the albedo. (The per-entity
    /// colormod multiply DP folds into lightingDiffuse is applied per-instance on the skin path — see
    /// <see cref="PlayerSkinShader"/>'s <c>colormod</c>.)
    /// </summary>
    private static void ApplyRgbGen(StandardMaterial3D mat, ShaderStage? stage)
    {
        ColorGen? gen = stage?.RgbGen;
        if (gen == null)
            return;
        switch (gen.Type)
        {
            case ColorGenType.Const:
                if (gen.Parms.Length >= 3)
                    mat.AlbedoColor = new Color(gen.Parms[0], gen.Parms[1], gen.Parms[2]);
                break;
            case ColorGenType.Vertex:
            case ColorGenType.ExactVertex:
            case ColorGenType.OneMinusVertex:
                mat.VertexColorUseAsAlbedo = true;
                break;
            default:
                // Identity / IdentityLighting / LightingDiffuse / Entity / Wave: leave the lit albedo white.
                break;
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Animated stage (tcMod / deformVertexes) → ShaderMaterial with generated .gdshader
    // -------------------------------------------------------------------------------------------------

    /// <summary>True if the stage's UVs are time-animated (scroll/rotate/stretch/turb) or the shader deforms vertices.</summary>
    private static bool NeedsAnimatedShader(ShaderDef def, ShaderStage stage)
    {
        if (def.Deforms.Count > 0 && HasWaveDeform(def))
            return true;
        // A multi-frame animMap must CYCLE (the static path binds only frame 0 — playtest r14: the mortar
        // sight sat frozen on its first frame), and an rgbGen wave must pulse (same stage: the sight blink).
        if (stage.AnimMap is { Frames.Length: > 1 })
            return true;
        if (stage.RgbGen is { Type: ColorGenType.Wave, Wave: not null })
            return true;
        foreach (TcMod m in stage.TcMods)
        {
            switch (m.Type)
            {
                case TcModType.Scroll:
                case TcModType.Rotate:
                case TcModType.Stretch:
                case TcModType.Turb:
                case TcModType.Page: // flipbook atlas cycles with TIME (electro crackle) — was a silent no-op
                    return true;
                case TcModType.Scale:
                    // A static scale alone doesn't need a custom shader — it is baked onto the
                    // StandardMaterial3D's Uv1Scale by ApplyStaticTcModScale. (If it co-occurs with an
                    // animated mod, an earlier case above already returned true and the generated shader
                    // takes the whole stack.)
                    break;
            }
        }
        return false;
    }

    private static bool HasWaveDeform(ShaderDef def)
    {
        foreach (DeformVertexes d in def.Deforms)
            if (d.Type is DeformType.Wave or DeformType.Move or DeformType.Bulge)
                return true;
        return false;
    }

    /// <summary>
    /// Build a <see cref="ShaderMaterial"/> from a generated spatial shader that applies the stage's
    /// tcMod stack to <c>UV</c> and the shader's wave deforms to <c>VERTEX</c>, both driven by
    /// <c>TIME</c>. The albedo texture is bound as a uniform; blend/cull/alpha-test are baked into the
    /// shader's <c>render_mode</c>.
    /// </summary>
    private static ShaderMaterial BuildAnimatedStage(ShaderDef def, ShaderStage stage, AssetSystem ctx, bool isBasePass)
    {
        string code = GenerateStageShader(def, stage, isBasePass);
        var shader = new Shader { Code = code };
        var mat = new ShaderMaterial { Shader = shader, ResourceName = def.Name + "/anim" };

        string albedoName = StageImageName(stage);
        Texture2D? albedo = ResolveStageTexture(stage, albedoName, ctx) ?? ctx.WhiteTexture();
        mat.SetShaderParameter("albedo_tex", albedo);

        // animMap frames 1..N-1 (frame 0 IS albedo_tex — StageImageName resolves it). A frame that fails
        // to load falls back to frame 0 so the cycle degrades to a shorter blink, not a white flash.
        // Load-time only (material build), so the string-built uniform names are fine here.
        if (stage.AnimMap is { Frames.Length: > 1 } am)
        {
            int frames = Math.Min(am.Frames.Length, 8); // Q3 MAX_IMAGE_ANIMATIONS
            for (int i = 1; i < frames; i++)
            {
                Texture2D? frameTex = ctx.LoadTexture(AssetPaths.StripImageExtension(am.Frames[i]));
                mat.SetShaderParameter("anim_tex_" + i, frameTex ?? albedo);
            }
        }
        return mat;
    }

    /// <summary>
    /// Emit the GDShader source for an animated stage. The fragment stage walks the tcMod list in source
    /// order (Q3 applies them in order); the vertex stage applies wave/move deforms. Comments name the Q3
    /// directive each block came from so the output is debuggable.
    /// </summary>
    private static string GenerateStageShader(ShaderDef def, ShaderStage stage, bool isBasePass)
    {
        var sb = new StringBuilder(1024);
        sb.Append("// XonoticGodot animated Q3 stage shader for '").Append(Sanitize(def.Name)).Append("'. Generated in C#.\n");
        sb.Append("shader_type spatial;\n");

        // render_mode: cull + blend + (unshaded for additive/colored passes so blending reads true).
        var modes = new List<string>();
        modes.Add(def.Cull == CullMode.None ? "cull_disabled" : "cull_back");
        bool transparent = !isBasePass && stage.BlendMode != BlendMode.Opaque;
        switch (stage.BlendMode)
        {
            case BlendMode.Add: modes.Add("blend_add"); modes.Add("unshaded"); break;
            case BlendMode.Filter: modes.Add("blend_mul"); break;
            case BlendMode.Blend: modes.Add("blend_mix"); break;
            case BlendMode.Custom: modes.Add("blend_mix"); break;
            default: break;
        }
        if (transparent)
            modes.Add("depth_draw_opaque");
        sb.Append("render_mode ").Append(string.Join(", ", modes)).Append(";\n\n");

        sb.Append("uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;\n");
        // GPU MD3 vertex-morph hook (cl_gpu_morph — ModelAnimator): frameB streams ride CUSTOM0/1 and
        // morph_amount blends toward them, so an ANIMATED-STAGE surface on a morphing model (the CTF flag's
        // scrolling-energy surface — the r16 case that pinned the whole flag to the CPU rebuild path) morphs
        // GPU-side too. Default 0.0 = exact no-op for every static/world use of this material. Per-material
        // uniform (vertex-stage instance uniforms are inert); ModelAnimator drives a per-surface Duplicate().
        sb.Append("uniform float morph_amount = 0.0;\n");
        // animMap frame cycling (playtest r14 D): one sampler per extra frame, selected below by
        // int(TIME*fps) % N (Q3 tr_shade.c R_BindAnimatedImage). Frame 0 stays albedo_tex so a map that
        // fails to resolve degrades to the old static look. Q3 caps animations at 8 frames.
        int animFrames = stage.AnimMap is { Frames.Length: > 1 } ? Math.Min(stage.AnimMap.Frames.Length, 8) : 1;
        for (int i = 1; i < animFrames; i++)
            sb.Append("uniform sampler2D anim_tex_").Append(i)
              .Append(" : source_color, filter_linear_mipmap_anisotropic;\n");
        // Dynamic whole-map colour tint (XonoticGodot.Game.WorldTint) — a global shader parameter so animated
        // world surfaces (scrolling textures, lava) re-tint with the rest of the map. Identity (1,1,1) default.
        sb.Append("global uniform vec3 map_tint;\n");
        bool alphaTest = !string.IsNullOrEmpty(stage.AlphaFunc);
        if (alphaTest)
            sb.Append("const float ALPHA_CUTOFF = ").Append(Flt(AlphaCutoff(stage.AlphaFunc!))).Append(";\n");
        sb.Append('\n');

        // ---- vertex() : MD3 morph prelude + deformVertexes ----
        // The morph mix runs FIRST (DP lerps the frames, THEN applies deforms — R_AliasLerpVerts before
        // RSurf_PrepareVerticesForBatch); at the default morph_amount 0 the branch is an exact no-op.
        sb.Append("void vertex() {\n");
        sb.Append("    if (morph_amount > 0.0) {   // GPU MD3 vertex-morph (see uniform above)\n");
        sb.Append("        VERTEX = mix(VERTEX, CUSTOM0.xyz, morph_amount);\n");
        sb.Append("        NORMAL = normalize(mix(NORMAL, CUSTOM1.xyz, morph_amount));\n");
        sb.Append("    }\n");
        if (def.Deforms.Count > 0 && HasWaveDeform(def))
        {
            foreach (DeformVertexes d in def.Deforms)
                EmitDeform(sb, d);
        }
        sb.Append("}\n\n");

        // ---- fragment() : tcMod stack on UV, then sample ----
        sb.Append("void fragment() {\n");
        sb.Append("    vec2 uv = UV;\n");
        foreach (TcMod m in stage.TcMods)
            EmitTcMod(sb, m);
        if (animFrames > 1)
        {
            // animMap: pick this frame's sampler. A dynamically-uniform if-chain (GDShader has no sampler
            // arrays); fps clamped so a malformed 0 doesn't divide the cycle away.
            float fps = stage.AnimMap!.Fps > 0f ? stage.AnimMap.Fps : 2f;
            sb.Append("    int fr = int(TIME * ").Append(Flt(fps)).Append(") % ").Append(animFrames)
              .Append(";                // animMap cycle\n");
            sb.Append("    vec4 c = texture(albedo_tex, uv);\n");
            for (int i = 1; i < animFrames; i++)
                sb.Append("    if (fr == ").Append(i).Append(") c = texture(anim_tex_").Append(i).Append(", uv);\n");
        }
        else
        {
            sb.Append("    vec4 c = texture(albedo_tex, uv);\n");
        }
        EmitRgbGen(sb, stage.RgbGen);
        sb.Append("    ALBEDO = c.rgb * map_tint;\n");
        sb.Append("    ALPHA = c.a;\n");
        if (alphaTest)
            sb.Append("    if (c.a < ALPHA_CUTOFF) discard;\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    // EmitRgbGen / EmitTcMod / the waveform expressions moved to the Godot-free
    // XonoticGodot.Formats.Materials.Q3StageGlsl so the autosprite deform shader generator
    // (AutospriteShaderGen) emits the identical, unit-tested GLSL. Thin local aliases keep call sites terse.
    private static void EmitRgbGen(StringBuilder sb, ColorGen? cg) => Q3StageGlsl.EmitRgbGen(sb, cg);

    private static void EmitTcMod(StringBuilder sb, TcMod m) => Q3StageGlsl.EmitTcMod(sb, m);

    /// <summary>Emit vertex displacement GLSL for one deformVertexes directive.</summary>
    private static void EmitDeform(StringBuilder sb, DeformVertexes d)
    {
        switch (d.Type)
        {
            case DeformType.Wave:
            {
                WaveForm w = d.Wave ?? new WaveForm();
                // Q3 "deformVertexes wave <spread> <wave>": the spread is a divisor — each vertex's
                // phase is offset by (x+y+z)/spread so the surface ripples instead of pulsing as one.
                // DP precomputes 1/spread (and uses 0 when spread is 0, i.e. a uniform pulse).
                float spread = d.Parms.Length > 0 ? d.Parms[0] : 0f;
                float invSpread = MathF.Abs(spread) > 1e-6f ? 1f / spread : 0f;
                sb.Append("    {\n");
                sb.Append("        float off = dot(VERTEX, vec3(1.0)) * ").Append(Flt(invSpread)).Append(";  // deformVertexes wave (1/spread)\n");
                sb.Append("        float disp = ").Append(WaveExprPhased(w, "off")).Append(";\n");
                sb.Append("        VERTEX += NORMAL * disp;\n");
                sb.Append("    }\n");
                break;
            }
            case DeformType.Move:
            {
                WaveForm w = d.Wave ?? new WaveForm();
                float x = d.Parms.Length > 0 ? d.Parms[0] : 0f;
                float y = d.Parms.Length > 1 ? d.Parms[1] : 0f;
                float z = d.Parms.Length > 2 ? d.Parms[2] : 0f;
                sb.Append("    {\n");
                sb.Append("        float disp = ").Append(WaveExpr(w)).Append(";   // deformVertexes move\n");
                sb.Append("        VERTEX += vec3(").Append(Flt(x)).Append(", ").Append(Flt(y)).Append(", ")
                  .Append(Flt(z)).Append(") * disp;\n");
                sb.Append("    }\n");
                break;
            }
            case DeformType.Bulge:
            {
                // bulge width height speed → sine along UV.x over time.
                float width = d.Parms.Length > 0 ? d.Parms[0] : 0f;
                float height = d.Parms.Length > 1 ? d.Parms[1] : 0f;
                float speed = d.Parms.Length > 2 ? d.Parms[2] : 0f;
                sb.Append("    {\n");
                sb.Append("        float disp = sin(UV.x * ").Append(Flt(width)).Append(" + TIME * ")
                  .Append(Flt(speed)).Append(") * ").Append(Flt(height)).Append("; // deformVertexes bulge\n");
                sb.Append("        VERTEX += NORMAL * disp;\n");
                sb.Append("    }\n");
                break;
            }
            default:
                break; // normal/autosprite handled elsewhere (billboard) or ignored
        }
    }

    /// <summary>A Q3 waveform evaluated at TIME, as a GLSL expression: base + amp * wave(phase + freq*TIME).</summary>
    private static string WaveExpr(WaveForm w) => Q3StageGlsl.WaveExpr(w);

    /// <summary>Waveform expression with an extra spatial phase term added (for surface-varying deforms).</summary>
    private static string WaveExprPhased(WaveForm w, string extraPhase) => Q3StageGlsl.WaveExprPhased(w, extraPhase);

    // -------------------------------------------------------------------------------------------------
    //  Special materials
    // -------------------------------------------------------------------------------------------------

    private static Material MakeNoDraw(ShaderDef def)
    {
        return new StandardMaterial3D
        {
            ResourceName = Combine(def.Name, NoDrawMarker),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0, 0, 0, 0),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    private static Material MakeSky(ShaderDef def)
    {
        // The skybox itself is set up by the world builder from def.SkyParms. A face using a sky shader
        // should not paint anything in the world pass, so it behaves like nodraw but is flagged sky.
        return new StandardMaterial3D
        {
            ResourceName = Combine(def.Name, "__sky__"),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0, 0, 0, 0),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
    }

    // -------------------------------------------------------------------------------------------------
    //  Shared appliers
    // -------------------------------------------------------------------------------------------------

    private static void ApplyCull(StandardMaterial3D mat, ShaderDef def)
    {
        mat.CullMode = def.Cull == CullMode.None
            ? BaseMaterial3D.CullModeEnum.Disabled
            : (def.Cull == CullMode.Back
                ? BaseMaterial3D.CullModeEnum.Front   // Q3 "cull back" culls front faces
                : BaseMaterial3D.CullModeEnum.Back);
    }

    private static void ApplyClamp(StandardMaterial3D mat, ShaderStage? stage)
    {
        if (stage is { ClampMap: true })
        {
            // Disable repeat so UVs clamp to the edge (Godot uses the texture-repeat flag).
            mat.SetFlag(BaseMaterial3D.Flags.UseTextureRepeat, false);
        }
    }

    private static void ApplyAlphaTest(StandardMaterial3D mat, ShaderStage? stage)
    {
        if (stage == null || string.IsNullOrEmpty(stage.AlphaFunc))
            return;
        mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
        mat.AlphaScissorThreshold = AlphaCutoff(stage.AlphaFunc);
    }

    /// <summary>
    /// Bake a lone static <c>tcMod scale</c> onto the material's <see cref="BaseMaterial3D.Uv1Scale"/>.
    /// DP applies Q3TCMOD_SCALE as a per-frame UV scale matrix (<c>gl_rmain.c</c>:
    /// <c>Matrix4x4_CreateScale3(&amp;matrix, parms[0], parms[1], 1)</c>); for a static stage that is just a
    /// constant UV multiply, which Godot expresses as Uv1Scale. Only applied when the scale is the sole
    /// UV-animating mod on the stage — a scale that co-occurs with scroll/rotate/stretch/turb is handled by
    /// the generated animated shader (<see cref="NeedsAnimatedShader"/>), so it must not also be baked here.
    /// (Concrete case: <c>textures/map_catharsis/chain</c> — <c>tcMod scale 2 2</c>.)
    /// </summary>
    private static void ApplyStaticTcModScale(StandardMaterial3D mat, ShaderStage? stage)
    {
        if (stage is null)
            return;

        Vector3 scale = Vector3.One;
        bool hasScale = false, hasAnimated = false;
        foreach (TcMod m in stage.TcMods)
        {
            switch (m.Type)
            {
                case TcModType.Scale:
                    scale = new Vector3(m.P(0), m.P(1), 1f);
                    hasScale = true;
                    break;
                case TcModType.Scroll:
                case TcModType.Rotate:
                case TcModType.Stretch:
                case TcModType.Turb:
                    hasAnimated = true;
                    break;
            }
        }

        if (hasScale && !hasAnimated)
            mat.Uv1Scale = scale;
    }

    private static void ApplyDeformBillboard(StandardMaterial3D mat, ShaderDef def)
    {
        foreach (DeformVertexes d in def.Deforms)
        {
            if (d.Type is DeformType.Autosprite or DeformType.Autosprite2)
            {
                mat.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
                mat.SetFlag(BaseMaterial3D.Flags.BillboardKeepScale, true);
                return;
            }
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>The Godot alpha-scissor cutoff for a Q3 <c>alphaFunc</c> comparator (GE128/GT0/LT128).</summary>
    private static float AlphaCutoff(string alphaFunc) => Q3StageGlsl.AlphaCutoff(alphaFunc);

    /// <summary>
    /// The image name a stage paints with (its <c>map</c>, or the first <c>animMap</c> frame), with the
    /// image extension stripped. The bare name resolves the albedo AND is the base the
    /// <c>_norm</c>/<c>_gloss</c>/<c>_glow</c>/<c>_shirt</c>/<c>_pants</c>/<c>_reflect</c> siblings are
    /// appended to — exactly as DP appends those suffixes to the extension-stripped skinframe basename
    /// (<c>gl_rmain.c</c> <c>R_SkinFrame_LoadExternal</c>). Keeping a <c>.tga</c> here would make every
    /// companion probe (<c>"textures/foo.tga_glow"</c>) miss. See <see cref="AssetPaths.StripImageExtension"/>.
    /// </summary>
    private static string StageImageName(ShaderStage stage)
    {
        if (!string.IsNullOrEmpty(stage.MapTexture))
            return AssetPaths.StripImageExtension(stage.MapTexture);
        if (stage.AnimMap is { Frames.Length: > 0 })
            return AssetPaths.StripImageExtension(stage.AnimMap.Frames[0]);
        return string.Empty;
    }

    /// <summary>
    /// Resolve a stage's texture, honoring the <c>$whiteimage</c>/<c>$lightmap</c> special tokens (a
    /// lightmap stage resolves to null here — its page is supplied later by the BSP path).
    /// </summary>
    private static Texture2D? ResolveStageTexture(ShaderStage? stage, string imageName, AssetSystem ctx)
    {
        if (stage != null)
        {
            if (stage.IsLightmap)
                return null;
            if (stage.IsWhiteImage)
                return ctx.WhiteTexture();
        }
        if (string.IsNullOrEmpty(imageName) || imageName == "-")
            return null;
        return ctx.LoadTexture(imageName);
    }

    /// <summary>The base name to look for <c>_norm</c>/<c>_gloss</c>/<c>_glow</c> siblings against, or null for special images.</summary>
    private static string? CompanionBase(ShaderStage? stage, string imageName)
    {
        if (stage is { IsLightmap: true } or { IsWhiteImage: true })
            return null;
        if (string.IsNullOrEmpty(imageName) || imageName == "-" || imageName.StartsWith('$'))
            return null;
        return imageName;
    }

    private static string Combine(string a, string marker)
        => string.IsNullOrEmpty(a) ? marker : a + " " + marker;

    /// <summary>Format a float for GLSL source: invariant culture, always with a decimal point.</summary>
    private static string Flt(float v) => Q3StageGlsl.Flt(v);

    private static string Sanitize(string s) => s.Replace("*/", "* /").Replace("\n", " ").Replace("\r", " ");
}
