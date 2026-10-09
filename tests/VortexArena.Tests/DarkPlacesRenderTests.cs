using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Formats.Lighting;
using VortexArena.Formats.Materials;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The arithmetic behind the DarkPlaces picture both the native game and legacy mode draw: realtime lights and
/// shadows (<see cref="DpLightModel"/>, from r_shadow.c and shader_glsl.h MODE_LIGHTSOURCE), reflective water
/// (<see cref="DpWaterModel"/>, gl_rmain.c R_Water_* and MODE_WATER), and the shader text generated for a
/// dp_water or dpreflectcube surface. Expected numbers are worked out here from DarkPlaces' formulas.
/// </summary>
public class DarkPlacesRenderTests
{
    private static ShaderDef Parse(string text) => Q3ShaderParser.Parse(text).Values.Single();

    // Xonotic's own script (scripts/map_solarium.shader).
    private const string Water4 = @"textures/map_solarium/water4
{
	surfaceparm trans
	surfaceparm water
	surfaceparm nolightmap
	cull none
	{
		map textures/map_solarium/water4/water4.tga
		tcmod scale 0.3 0.4
		tcMod scroll 0.05 0.05
		blendfunc add
		alphaGen vertex
	}
	dpreflectcube cubemaps/default/sky
	{
		map $lightmap
		blendfunc add
		tcGen lightmap
	}
	dp_water 0.1 1.2  1.4 0.7  1 1 1  1 1 1  0.1
}";

    // ---- realtime lights ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 1f)]            // (1 - 0) * 2 / 1 = 2, clamped to 1
    [InlineData(0.25f, 1f)]         // 0.75 * 2 / 1.0625 = 1.41, clamped
    [InlineData(0.5f, 0.8f)]        // 0.5 * 2 / 1.25
    [InlineData(0.75f, 0.32f)]      // 0.25 * 2 / 1.5625
    [InlineData(1f, 0f)]
    [InlineData(1.5f, 0f)]
    public void Light_falloff_is_darkplaces_attenuation_table(float d, float expected)
        => Assert.Equal(expected, DpLightModel.Attenuation(d), 4);

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.37f)]
    [InlineData(0.6f)]
    [InlineData(0.93f)]
    public void The_engine_window_gives_the_distance_back(float d)
    {
        // The light shader is handed (1 - d^4)^2 and has to arrive at DarkPlaces' falloff of d.
        float window = DpLightModel.EngineWindow(d);
        Assert.Equal(d, DpLightModel.DistanceFromWindow(window), 3);
        Assert.Equal(DpLightModel.Attenuation(d), DpLightModel.Attenuation(DpLightModel.DistanceFromWindow(window)), 3);
    }

    [Theory]
    [InlineData(0f, 1f, 1f)]        // a plain dynamic light, and an 8-field .rtlights line
    [InlineData(0.3f, 0.7f, 0f)]
    [InlineData(1f, 2f, 1.5f)]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0.25f, 1f, 0.125f)]
    public void Light_scales_survive_packing(float ambient, float diffuse, float specular)
    {
        float packed = DpLightModel.PackScales(ambient, diffuse, specular);
        Assert.True(packed >= DpLightModel.PackBase);
        (float a, float d, float s) = DpLightModel.UnpackScales(packed);
        Assert.Equal(ambient, a, 3);
        Assert.Equal(diffuse, d, 3);
        Assert.Equal(specular, s, 3);
    }

    [Fact]
    public void An_unpacked_light_is_ambient_0_diffuse_1()
    {
        // A light nobody packed carries the engine's own specular value (0.5 by default): it must still light.
        Assert.Equal((0f, 1f, 0.5f), DpLightModel.UnpackScales(0.5f));
    }

    [Fact]
    public void Shader_helper_unpacks_the_same_layout()
    {
        // The GLSL divides SPECULAR_AMOUNT (twice the property) by two and uses the same 8 and 101.
        Assert.Contains("specular_amount * 0.5", DpLightModel.ShaderFunctions);
        Assert.Contains("floor(packed / 8.0)", DpLightModel.ShaderFunctions);
        Assert.Contains("floor(whole / 101.0)", DpLightModel.ShaderFunctions);
        Assert.Equal(8f, DpLightModel.PackBase);
        Assert.Equal(101f, DpLightModel.DiffuseSteps);
        Assert.Contains("(1.0 - q) * 2.0 / (1.0 + q * q)", DpLightModel.ShaderFunctions);
    }

    [Fact]
    public void Without_normal_maps_a_light_has_no_n_dot_l()
    {
        // r_shadow.c: if (!r_shadow_usenormalmap) ambientcolor += diffusecolor, diffusecolor = 0
        Assert.Equal(0.8f * 0.25f, DpLightModel.DiffuseFactor(0f, 1f, 0.25f, 0.8f), 5);
        Assert.Equal(0.8f, DpLightModel.DiffuseFactor(0f, 1f, 0.25f, 0.8f, useNormalMap: false), 5);
        Assert.Equal((0.3f + 0.7f * 0.5f) * 0.5f, DpLightModel.DiffuseFactor(0.3f, 0.7f, 0.5f, 0.5f), 5);
    }

    [Theory]
    //          shadowmapping, light casts, world light, world shadows, dlight shadows, expected
    [InlineData(true, true, true, true, false, true)]
    [InlineData(true, true, true, false, true, false)]     // a level's light follows r_shadow_realtime_world_shadows only
    [InlineData(true, true, false, true, false, false)]    // a dynamic light follows r_shadow_realtime_dlight_shadows only
    [InlineData(true, true, false, false, true, true)]
    [InlineData(false, true, false, true, true, false)]    // no shadow mapping, no light shadows at all
    [InlineData(true, false, true, true, true, false)]     // the '!' of an .rtlights line
    public void A_light_casts_by_darkplaces_rule(bool mapping, bool lightCasts, bool world, bool worldShadows, bool dlightShadows, bool expected)
        => Assert.Equal(expected, DpLightModel.CastsShadow(mapping, lightCasts, world, worldShadows, dlightShadows));

    [Fact]
    public void A_world_light_is_drawn_by_its_mode_flag()
    {
        RtLightsFile.Light plain = RtLightsFile.ParseLine("784 216 -436 350 0.3 0.54 0.74 0")!;
        Assert.Equal(RtLightsFile.FlagRealtimeMode, plain.Flags);
        Assert.True(DpLightModel.WorldLightDrawn(plain.Flags, realtimeWorld: true));
        Assert.False(DpLightModel.WorldLightDrawn(plain.Flags, realtimeWorld: false));

        RtLightsFile.Light both = RtLightsFile.ParseLine("-250 380 80 350 1 0.85 0.6 0 \"\" 1 0 0 0 0.25 0 1 1 3")!;
        Assert.True(DpLightModel.WorldLightDrawn(both.Flags, true));
        Assert.True(DpLightModel.WorldLightDrawn(both.Flags, false));
        Assert.Equal(1f, both.Corona);

        RtLightsFile.Light quiet = RtLightsFile.ParseLine("!-100 150 60 300 0.3 0.5 1.0 0 \"\" 0 0 0 0 0.25 0.3 0.7 0 1")!;
        Assert.False(quiet.Shadow);
        Assert.Equal(0.3f, quiet.AmbientScale, 4);
        Assert.Equal(0.7f, quiet.DiffuseScale, 4);
        Assert.Equal(0f, quiet.SpecularScale, 4);
        Assert.False(DpLightModel.WorldLightDrawn(quiet.Flags, true));
        Assert.True(DpLightModel.WorldLightDrawn(quiet.Flags, false));
    }

    [Theory]
    [InlineData(-1, DpLightModel.ShadowFilter.SoftLow)]
    [InlineData(0, DpLightModel.ShadowFilter.Hard)]
    [InlineData(1, DpLightModel.ShadowFilter.Hard)]
    [InlineData(2, DpLightModel.ShadowFilter.SoftLow)]
    [InlineData(3, DpLightModel.ShadowFilter.SoftLow)]
    [InlineData(4, DpLightModel.ShadowFilter.SoftMedium)]
    public void Filter_quality_maps_to_a_filter(int quality, DpLightModel.ShadowFilter expected)
        => Assert.Equal(expected, DpLightModel.FilterFor(quality));

    [Fact]
    public void Corona_and_model_shadow_numbers()
    {
        // R_DrawCorona(rtlight, corona * r_coronas * 0.25, radius * coronasizescale)
        Assert.Equal((0.25f, 87.5f), DpLightModel.Corona(1f, 0.25f, 350f, 1f));
        // mix(1 - r_shadows_darken, 1, lit)
        Assert.Equal(0.5f, DpLightModel.ModelShadowFactor(0.5f, 0f), 5);
        Assert.Equal(1f, DpLightModel.ModelShadowFactor(0.5f, 1f), 5);
        Assert.Equal(0.75f, DpLightModel.ModelShadowFactor(0.5f, 0.5f), 5);
        Assert.Equal(0f, DpLightModel.ModelShadowFactor(2f, 0f), 5);
    }

    // ---- water ----------------------------------------------------------------------------------------------

    [Fact]
    public void Dp_water_line_is_parsed()
    {
        DpWater water = Parse(Water4).Dp.Water!;
        Assert.Equal(0.1f, water.ReflectMin, 4);
        Assert.Equal(1.2f, water.ReflectMax, 4);
        Assert.Equal(1.4f, water.RefractFactor, 4);
        Assert.Equal(0.7f, water.ReflectFactor, 4);
        Assert.Equal(0.1f, water.WaterAlpha, 4);
        Assert.Equal("cubemaps/default/sky", Parse(Water4).Dp.ReflectCube);
    }

    [Theory]
    [InlineData(1f, 0.1f)]                       // straight down: reflectmin
    [InlineData(0f, 1.2f)]                       // grazing: reflectmax
    [InlineData(0.5f, 0.25f * 1.1f + 0.1f)]      // (1 - 0.5)^2 * (1.2 - 0.1) + 0.1
    public void Fresnel_is_mode_water(float cosine, float expected)
        => Assert.Equal(expected, DpWaterModel.Fresnel(cosine, 0.1f, 1.2f), 5);

    [Fact]
    public void Vertex_alpha_scales_the_water()
    {
        // USEALPHAGENVERTEX: reflectoffset and reflectfactor times the vertex alpha, refractcolor towards white
        Assert.Equal((0.25f * 1.1f + 0.1f) * 0.5f, DpWaterModel.Fresnel(0.5f, 0.1f, 1.2f, vertexAlpha: 0.5f), 5);
        Vector3 result = DpWaterModel.Background(new Vector3(0.4f), new Vector3(0.8f), new Vector3(0.5f, 1f, 0.5f), Vector3.One, 0f, vertexAlpha: 0.5f);
        Assert.Equal(0.4f * 0.75f, result.X, 5);
        Assert.Equal(0.4f, result.Y, 5);
        Vector3 mixed = DpWaterModel.Background(new Vector3(0.2f), new Vector3(0.6f), Vector3.One, new Vector3(1f, 0.5f, 1f), 0.25f);
        Assert.Equal(0.2f * 0.75f + 0.6f * 0.25f, mixed.X, 5);
        Assert.Equal(0.2f * 0.75f + 0.3f * 0.25f, mixed.Y, 5);
    }

    [Fact]
    public void Water_numbers_follow_the_cvars()
    {
        // Xonotic: r_water_refractdistort 0.003, r_water_reflectdistort 0.01; water4 says 1.4 and 0.7
        (float refract, float reflect) = DpWaterModel.Distort(DpWaterModel.XonoticRefractDistort, DpWaterModel.DefaultReflectDistort, 1.4f, 0.7f);
        Assert.Equal(0.0042f, refract, 5);
        Assert.Equal(0.007f, reflect, 5);
        Assert.Equal((320, 180), DpWaterModel.TextureSize(1280, 720, 0.25f));
        Assert.Equal((640, 360), DpWaterModel.TextureSize(1280, 720, 0.5f));
        Assert.Equal((1280, 720), DpWaterModel.TextureSize(1280, 720, 4f));
        Assert.Equal((16, 16), DpWaterModel.TextureSize(1280, 720, 0.001f));
        Assert.Equal(0.1f * 0.4f, DpWaterModel.SurfaceAlpha(0.4f, 0.1f), 6);
    }

    [Fact]
    public void Planes_merge_and_clip_as_in_darkplaces()
    {
        Vector3 up = Vector3.UnitZ;
        Assert.True(DpWaterModel.SamePlane(up, 16f, up, 16.5f));        // 0.5 * 0.001 = 0.0005
        Assert.False(DpWaterModel.SamePlane(up, 16f, up, 18f));         // 0.002
        Assert.False(DpWaterModel.SamePlane(up, 16f, Vector3.Normalize(new Vector3(0.1f, 0, 1f)), 16f));
        // the clip plane is the water plane moved back by the bias unless the eye is within it
        Assert.Equal(15f, DpWaterModel.ClipPlaneDist(16f, eyeDist: 100f));
        Assert.Equal(16f, DpWaterModel.ClipPlaneDist(16f, eyeDist: 16.5f));
        Assert.Equal(new Vector3(5f, 7f, 16f - 84f), DpWaterModel.MirrorPoint(new Vector3(5f, 7f, 100f), up, 16f));
    }

    private static string Gen(string script, bool reflect = false, bool water = false)
    {
        ShaderDef def = Parse(script);
        DpMaterialPlan plan = DpMaterialRules.Plan(def);
        return DpSurfaceShaderGen.Generate(def, plan, def.Stages[plan.MaterialStage], null, forModel: false, glow: false, frames: 1,
            lightmapped: false, reflect: reflect, water: water ? def.Dp.Water : null);
    }

    [Fact]
    public void Water_variant_composes_refraction_and_reflection()
    {
        string plain = Gen(Water4);
        string water = Gen(Water4, water: true);
        // r_water 0: the plain additive material, no screen read
        Assert.Contains("blend_add", plain);
        Assert.DoesNotContain("hint_screen_texture", plain);
        Assert.DoesNotContain("reflection_tex", plain);
        // r_water 1: the scene behind, the reflection, the Fresnel mix, the material over it at the water alpha
        Assert.Contains("blend_mix", water);
        Assert.DoesNotContain("blend_add", water);
        Assert.Contains("hint_screen_texture", water);
        Assert.Contains("reflection_vp * vec4(dp_world, 1.0)", water);
        Assert.Contains("* 1.1 * va + 0.1 * va", water);                          // reflectmax - reflectmin, reflectmin
        Assert.Contains("refractcolor = mix(refractcolor, vec3(1.0), va)", water); // alphaGen vertex
        Assert.Contains("over = 0.1;", water);
        Assert.Contains("ALBEDO = clamp(bg + rgb * (c.a * over)", water);          // blendfunc add, over the water
        Assert.Contains("CAMERA_VISIBLE_LAYERS & uint(" + DpSurfaceShaderGen.WaterRenderSkipBit + ")", water);
        Assert.Contains("float va = COLOR.a;", water);
    }

    [Fact]
    public void Reflect_cube_is_added_to_the_texel_before_lighting()
    {
        string with = Gen(Water4, reflect: true);
        string without = Gen(Water4);
        Assert.Contains("uniform samplerCube reflect_cube", with);
        Assert.Contains("rgb += texture(reflect_mask, uv).rgb * texture(reflect_cube, vec3(rw.x, -rw.z, rw.y)).rgb", with);
        Assert.DoesNotContain("reflect_cube", without);
        // before the vertex light is multiplied in
        Assert.True(with.IndexOf("USEREFLECTCUBE", StringComparison.Ordinal) < with.IndexOf("MODE_VERTEXCOLOR", StringComparison.Ordinal));
    }

    // ---- the lightmap shader's translucent variant (lightmapped glass) ---------------------------------------

    [Fact]
    public void Translucent_lightmap_variant_has_its_anchor_line()
    {
        // The variant is made by adding an ALPHA write after one line of the shader; when that line was reworded
        // the replacement silently did nothing and glass was a solid wall. The line must exist in the source.
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "VortexArena.sln"))) root = Path.GetDirectoryName(root);
        if (root is null) return;   // not run from a checkout
        string source = File.ReadAllText(Path.Combine(root, "game", "loaders", "LightmapShader.cs"));
        const string marker = "internal const string TranslucentAnchor = \"";
        int at = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0, "TranslucentAnchor constant not found");
        int end = source.IndexOf("\\n\";", at, StringComparison.Ordinal);
        string anchor = source[(at + marker.Length)..end];
        int first = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(first >= 0 && first < at, "the anchor line is not in the shader source above the constant");
        Assert.Contains("ALPHA = base.a;", source);
    }
}
