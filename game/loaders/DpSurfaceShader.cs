using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Formats.Materials;
using VortexArena.Formats.Vfs;

namespace VortexArena.Game.Loaders;

/// <summary>
/// A Quake 3 shader drawn the way DarkPlaces draws it, for a legacy session (an <see cref="AssetSystem"/> with
/// <see cref="AssetSystem.DarkPlacesRules"/> set): one material stage, the first stage's blend function, lit or
/// full-bright as a whole (<see cref="DpMaterialRules.Plan"/>), and DarkPlaces' own arithmetic on the stored
/// texel values (<see cref="DpColour"/>).
///
/// <para>The native compiler (<see cref="ShaderCompiler"/>) instead turns every stage into a pass and lights an
/// unblended stage with the scene's sun and sky. That is a different picture from the same data - lava that is
/// one additive full-bright stage in DarkPlaces came out alpha-blended and sunlit - so a legacy session does not
/// use it for the surfaces this class can express.</para>
///
/// <para>Texels are sampled without sRGB decoding and combined as they are; the result is decoded once at the
/// end so that the engine's output transform shows the computed value (<c>dp_out</c>). Time is the session's
/// clock (<see cref="TimeUniform"/>: cl.time, DarkPlaces' rsurface.shadertime for the world), not the engine's.</para>
/// </summary>
public static class DpSurfaceShader
{
    /// <summary>Global shader parameter: the legacy session's cl.time, for tcMod / deform / animMap.</summary>
    public static readonly StringName TimeUniform = "dp_time";

    /// <summary>
    /// The material DarkPlaces would draw for <paramref name="def"/>, or null when the shader is one this
    /// class leaves to the caller (sky, nodraw, a stage with no image).
    /// </summary>
    /// <param name="forModel">The surface belongs to an entity's model (lit from the light grid, tinted by
    /// colormod) rather than to the level (lit by its vertex colours).</param>
    public static Material? Compile(ShaderDef def, AssetSystem ctx, bool forModel) => Compile(def, ctx, forModel, null);

    /// <summary>
    /// The material for a face of the level that has a lightmap page: null when the plain lightmap shader
    /// (<see cref="LightmapShader"/>) already draws what DarkPlaces would - an opaque or alpha-tested, unanimated,
    /// lit texture - and otherwise this generator's material, lit by <paramref name="lightmap"/> through the
    /// mesh's second texture coordinates (MODE_LIGHTMAP: texel * lightmap * 2), blended and animated as the
    /// shader says. A shader DarkPlaces draws full-bright never gets here (MapLoader gives its faces no page).
    /// </summary>
    public static Material? CompileLightmapped(ShaderDef def, AssetSystem ctx, Texture2D lightmap)
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(def);
        if (plan.Sky || plan.NoDraw || plan.MaterialStage < 0 || plan.FullBright) return null;
        ShaderStage stage = def.Stages[plan.MaterialStage];
        // (A lone static tcMod scale is the lightmap shader's albedo_uv_scale.)
        if (!plan.Blended && plan.BackgroundStage < 0 && !Animated(def, stage, staticScaleIsPlain: true)) return null;
        return Compile(def, ctx, forModel: false, lightmap);
    }

    private static Material? Compile(ShaderDef def, AssetSystem ctx, bool forModel, Texture2D? lightmap)
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(def);
        if (plan.Sky || plan.NoDraw || plan.MaterialStage < 0) return null;
        ShaderStage stage = def.Stages[plan.MaterialStage];
        if (stage.IsLightmap) return null;

        Texture2D? albedo = StageTexture(stage, ctx, out string imageName);
        if (albedo is null) return null;

        bool animated = Animated(def, stage);
        // An ordinary lit, unblended, unanimated model skin is what PlayerSkinShader already draws with
        // DarkPlaces' arithmetic (and it has the normal, gloss and team-colour maps this generator leaves out).
        if (forModel && !plan.FullBright && !plan.Blended && !plan.AlphaTest && !animated && plan.BackgroundStage < 0 && !stage.IsWhiteImage)
        {
            ShaderMaterial? skin = ctx.TryBuildSkinMaterial(imageName, albedo, alwaysBuild: true);
            if (skin is not null)
            {
                skin.ResourceName = def.Name + "/skin";
                if (def.Dp.GlossExponentMod is { } exponent) skin.SetShaderParameter("dp_gloss_exponent_mod", exponent);
                if (def.Dp.GlossIntensityMod is { } intensity) skin.SetShaderParameter("dp_gloss_intensity_mod", intensity);
                if (plan.TwoSided) skin.ResourceName += " twosided";
                return skin;
            }
        }

        ShaderStage? background = plan.BackgroundStage >= 0 ? def.Stages[plan.BackgroundStage] : null;
        Texture2D? backgroundTexture = background is null ? null : StageTexture(background, ctx, out _);
        if (background is not null && backgroundTexture is null) background = null;

        Texture2D? glow = stage.IsWhiteImage ? null : ctx.LoadTexture(AssetPaths.StripImageExtension(imageName) + "_glow");
        int frames = stage.AnimMap is { Frames.Length: > 1 } ? Math.Min(stage.AnimMap.Frames.Length, 8) : 1;

        // dpreflectcube: the stage texture's _reflect mask times the named cube map (surfaces of the level; a
        // model's ordinary skin has it in PlayerSkinShader).
        (Texture2D? reflectMask, Cubemap? reflectCube) = forModel || stage.IsWhiteImage ? (null, null) : ctx.ResolveReflect(def, imageName);
        bool reflect = reflectMask is not null && reflectCube is not null;

        string code = DpSurfaceShaderGen.Generate(def, plan, stage, background, forModel, glow is not null, frames, lightmap is not null, reflect);
        ShaderMaterial material = new() { Shader = ShaderCompiler.SharedShader(code), ResourceName = def.Name + "/dp" };
        if (!forModel && def.Dp.Water is { } water && plan.Blended)
        {
            // r_water: the reflective and refractive variant of the same material; WaterRenderer switches a
            // surface to it while the pass is on and supplies the reflection. Off, this is the plain material
            // DarkPlaces draws with r_water 0.
            string waterCode = DpSurfaceShaderGen.Generate(def, plan, stage, background, forModel, glow is not null, frames, lightmap is not null, reflect, water);
            Texture2D? waterNormal = ctx.LoadTexture(AssetPaths.StripImageExtension(imageName) + "_norm");
            if (waterNormal is not null)
            {
                material.SetShaderParameter("water_normal_tex", waterNormal);
                if (AssetSystem.IsRgTexture(waterNormal)) material.SetShaderParameter("water_norm_rg", true);
            }
            VortexArena.Game.Client.WaterRenderer.Register(material, material.Shader, ShaderCompiler.SharedShader(waterCode), water);
        }
        material.SetShaderParameter("albedo_tex", albedo);
        if (lightmap is not null && !plan.FullBright) material.SetShaderParameter("lightmap_tex", lightmap);
        if (background is not null) material.SetShaderParameter("background_tex", backgroundTexture!);
        if (glow is not null) material.SetShaderParameter("glow_tex", glow);
        if (reflect)
        {
            material.SetShaderParameter("reflect_mask", reflectMask!);
            material.SetShaderParameter("reflect_cube", reflectCube!);
        }
        for (int i = 1; i < frames; i++)
            material.SetShaderParameter("anim_tex_" + i, ctx.LoadTexture(AssetPaths.StripImageExtension(stage.AnimMap!.Frames[i])) ?? albedo);
        return material;
    }

    private static Texture2D? StageTexture(ShaderStage stage, AssetSystem ctx, out string imageName)
    {
        imageName = !string.IsNullOrEmpty(stage.MapTexture) ? AssetPaths.StripImageExtension(stage.MapTexture)
            : stage.AnimMap is { Frames.Length: > 0 } ? AssetPaths.StripImageExtension(stage.AnimMap.Frames[0]) : string.Empty;
        if (stage.IsWhiteImage) return ctx.WhiteTexture();
        if (string.IsNullOrEmpty(imageName) || imageName == "-" || imageName.StartsWith('$')) return null;
        return ctx.LoadTexture(imageName);
    }

    private static bool Animated(ShaderDef def, ShaderStage stage, bool staticScaleIsPlain = false)
    {
        if (stage.AnimMap is { Frames.Length: > 1 }) return true;
        if (stage.TcGen is { Type: TcGenType.Environment or TcGenType.Vector }) return true;
        foreach (TcMod m in stage.TcMods)
            if (!(staticScaleIsPlain && m.Type == TcModType.Scale && stage.TcMods.Count == 1)) return true;
        foreach (DeformVertexes d in def.Deforms)
            if (d.Type is DeformType.Wave or DeformType.Move or DeformType.Bulge or DeformType.Autosprite or DeformType.Autosprite2) return true;
        return false;
    }
}
