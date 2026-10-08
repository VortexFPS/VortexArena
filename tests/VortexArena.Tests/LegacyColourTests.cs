using System;
using System.Linq;
using VortexArena.Formats.Materials;
using VortexArena.Legacy.Protocol;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The colour of legacy compatibility mode (planning/specs/legacy-compat.md §15): the rules by which
/// DarkPlaces turns a Quake 3 shader into what it draws (<see cref="DpMaterialRules"/>, a port of
/// model_shared.c Mod_LoadTextureFromQ3Shader), its arithmetic with Xonotic's default vid_sRGB 0
/// (<see cref="DpColour"/>, shader_glsl.h / gl_rmain.c), and the shader text built from both
/// (<see cref="DpSurfaceShaderGen"/>). The shader scripts below are Xonotic's own, copied from
/// scripts/liquids_lava.shader, map_solarium.shader and trak5x.shader; the expected numbers are
/// computed here from DarkPlaces' formulas, not read back from the code under test.
/// </summary>
public class LegacyColourTests
{
    private static ShaderDef Parse(string text) => Q3ShaderParser.Parse(text).Values.Single();

    private const string Lava = @"textures/liquids_lava/lava0
{
	surfaceparm lava
	surfaceparm trans
	cull disable
	deformVertexes wave 150.0 sin 2 5 0.25 0.1
	q3map_surfacelight 1000
	{
		map textures/liquids_lava/lava0.tga
		blendfunc GL_SRC_ALPHA GL_ONE
	}
}";

    private const string Water = @"textures/map_solarium/water4
{
	surfaceparm trans
	surfaceparm water
	cull none
	{
		map textures/map_solarium/water4/water4.tga
		tcmod scale 0.3 0.4
		tcMod scroll 0.05 0.05
		blendfunc add
		alphaGen vertex
	}
	{
		map $lightmap
		blendfunc add
		tcGen lightmap
	}
}";

    private const string EnvironmentGlass = @"textures/map_solarium/cubemap_glass
{
	surfaceparm trans
	cull disable
	{
		map textures/map_solarium/cubemap_glass/env2.tga
		blendfunc add
		tcgen environment
	}
}";

    private const string Glass = @"textures/trak5x/misc-glass
{
	surfaceparm trans
	{
		map textures/trak5x/misc/misc_glass.tga
		blendFunc blend
	}
	{
		map $lightmap
		rgbGen identity
		tcGen lightmap
		blendfunc filter
	}
}";

    private const string Wall = @"textures/trak6x/base-base1c
{
	{
		map textures/trak6x/base/base_base1c.tga
	}
	{
		map $lightmap
		rgbGen identity
		tcGen lightmap
		blendfunc filter
	}
}";

    private const string Grass = @"models/desertfactory/textures/shaders/grass01
{
	surfaceparm trans
	cull none
	{
		map models/desertfactory/textures/misc/grass01.tga
		alphaFunc GE128
		rgbGen vertex
	}
}";

    private const string LightmapFirst = @"textures/test/lightmapfirst
{
	{
		map $lightmap
	}
	{
		map textures/test/diffuse.tga
		blendfunc filter
	}
	{
		map textures/test/glowpass.tga
		blendfunc add
	}
}";

    private const string Terrain = @"textures/test/terrain
{
	{
		map textures/test/rock.tga
	}
	{
		map textures/test/grass.tga
		blendFunc blend
		alphaGen vertex
	}
	{
		map $lightmap
		blendfunc filter
	}
}";

    private const string Decal = @"textures/test/decal
{
	{
		map textures/test/stain.tga
		blendfunc GL_ZERO GL_ONE_MINUS_SRC_COLOR
		rgbGen wave sin 0.5 0.5 0 1
	}
}";

    // ---- which stage is drawn, how it is blended, whether it is lit ----------------------------------

    [Fact]
    public void Lava_IsOneAdditiveFullBrightStage()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(Lava));
        Assert.Equal(0, plan.MaterialStage);
        Assert.Equal(DpBlend.Add, plan.Blend);            // GL_SRC_ALPHA GL_ONE -> MATERIALFLAG_ADD
        Assert.True(plan.FullBright);                     // no $lightmap, no rgbGen lightingDiffuse / vertex
        Assert.True(plan.MaterialAlpha);                  // the blend names the source alpha: TEXF_ALPHA
        Assert.True(plan.TwoSided);
        Assert.False(plan.AlphaTest);
    }

    [Fact]
    public void Water_IsAddedAtFullStrength_LitByItsVertexColours()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(Water));
        Assert.Equal(0, plan.MaterialStage);              // "ordinary texture": the stage before $lightmap
        Assert.Equal(DpBlend.Add, plan.Blend);            // GL_ONE GL_ONE -> MATERIALFLAG_ADD
        Assert.False(plan.FullBright);                    // a $lightmap stage makes the shader lit
        Assert.True(plan.VertexAlpha);                    // alphaGen vertex on the first stage
        // "blendfunc add" names no alpha: the texture is loaded without TEXF_ALPHA, so its alpha channel
        // (0.15 to 0.44 in water4.tga) does not weaken the add. This is what made the pool visible.
        Assert.False(plan.MaterialAlpha);
    }

    [Fact]
    public void EnvironmentGlass_IgnoresTheLightmapItsFacesWereGiven()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(EnvironmentGlass));
        Assert.Equal(DpBlend.Add, plan.Blend);
        Assert.True(plan.FullBright);                     // no stage asks for light: MATERIALFLAG_FULLBRIGHT
        Assert.False(plan.MaterialAlpha);
    }

    [Fact]
    public void Glass_IsAlphaBlended_AndLit()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(Glass));
        Assert.Equal(0, plan.MaterialStage);
        Assert.Equal(DpBlend.Alpha, plan.Blend);
        Assert.False(plan.FullBright);
        Assert.True(plan.MaterialAlpha);
    }

    [Fact]
    public void OrdinaryWall_IsOpaqueAndLit_FromTheStageBeforeTheLightmap()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(Wall));
        Assert.Equal(0, plan.MaterialStage);
        Assert.Equal(-1, plan.BackgroundStage);
        Assert.Equal(DpBlend.Opaque, plan.Blend);
        Assert.False(plan.FullBright);
        Assert.False(plan.Blended);
    }

    [Fact]
    public void VertexLitFoliage_IsAlphaTested_AndLit()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(Grass));
        Assert.True(plan.AlphaTest);
        Assert.True(plan.MaterialAlpha);
        Assert.False(plan.FullBright);                    // rgbGen vertex
        Assert.Equal(DpBlend.Opaque, plan.Blend);
    }

    [Fact]
    public void LightmapFirst_TakesTheSecondStage_AndTheFirstStagesBlend()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(LightmapFirst));
        Assert.Equal(1, plan.MaterialStage);              // "$lightmap before diffuse"
        Assert.Equal(DpBlend.Opaque, plan.Blend);         // the blend is always the FIRST stage's
        Assert.False(plan.FullBright);
    }

    [Fact]
    public void Terrain_BlendsTwoStagesByVertexAlpha()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(Terrain));
        Assert.Equal(1, plan.MaterialStage);
        Assert.Equal(0, plan.BackgroundStage);
        Assert.Equal(DpBlend.Opaque, plan.Blend);
        Assert.True(plan.BackgroundAlpha);                // mod_q3shader_force_terrain_alphaflag 1
        Assert.True(plan.MaterialAlpha);
    }

    [Fact]
    public void AnUnusualBlend_IsCustom_AndAlwaysFullBright()
    {
        DpMaterialPlan plan = DpMaterialRules.Plan(Parse(Decal));
        Assert.Equal(DpBlend.Custom, plan.Blend);
        Assert.Equal(BlendFactor.Zero, plan.CustomSrc);
        Assert.Equal(BlendFactor.OneMinusSrcColor, plan.CustomDst);
        Assert.True(plan.FullBright);
    }

    [Fact]
    public void SkyAndNodraw_DrawNothing()
    {
        Assert.True(DpMaterialRules.Plan(Parse("textures/x/sky\n{\n\tsurfaceparm sky\n\t{\n\t\tmap textures/x/a.tga\n\t}\n}")).Sky);
        Assert.True(DpMaterialRules.Plan(Parse("textures/x/caulk\n{\n\tsurfaceparm nodraw\n}")).NoDraw);
        Assert.True(DpMaterialRules.Plan(Parse("textures/x/empty\n{\n\tsurfaceparm trans\n}")).NoDraw);   // no stages
    }

    // ---- the arithmetic ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.50f, 0.40f)]
    [InlineData(0.30f, 0.25f)]
    [InlineData(0.80f, 0.50f)]
    [InlineData(0.10f, 0.10f)]
    public void Lightmap_IsTexelTimesLightmapTimesTwo_OnTheStoredValues(float texel, float lightmap)
    {
        // shader_glsl.h MODE_LIGHTMAP with Color_Ambient 0 and Color_Diffuse 2 (gl_rmain.c: "lightmap - 2x
        // diffuse and specular brightness because bsp files have 0-2 colors as 0-1").
        float expected = texel * lightmap * 2f;
        Assert.Equal(expected, DpColour.Lightmap(texel, lightmap), 6);
        // What the session's shader writes into a linear buffer is shown as that value by the output transform...
        Assert.Equal(Math.Min(expected, 1f), DpColour.Shown(DpColour.Lightmap(texel, lightmap)), 4);
        // ...and the native combine of the same two texels (decode, multiply in linear light, encode) is darker.
        Assert.True(DpColour.LightmapInLinearLight(texel, lightmap) < expected);
    }

    [Fact]
    public void LinearCombine_IsTheDefectThatWasSeen_DarkerAndMoreSaturated()
    {
        // A brick texel (R 0.60, G 0.40, B 0.30) under a lightmap texel of 0.40. DarkPlaces shows 0.48 0.32 0.24.
        float r = DpColour.Lightmap(0.60f, 0.40f), g = DpColour.Lightmap(0.40f, 0.40f), b = DpColour.Lightmap(0.30f, 0.40f);
        Assert.Equal(0.48f, r, 5);
        Assert.Equal(0.32f, g, 5);
        Assert.Equal(0.24f, b, 5);
        float lr = DpColour.LightmapInLinearLight(0.60f, 0.40f), lg = DpColour.LightmapInLinearLight(0.40f, 0.40f), lb = DpColour.LightmapInLinearLight(0.30f, 0.40f);
        // Darker: about 0.33 0.21 0.15 - two thirds of the brightness and less...
        Assert.InRange(lr / r, 0.60f, 0.72f);
        Assert.InRange(lb / b, 0.55f, 0.68f);
        // ...and redder: the weak channels lose more than the strong one, so red's share of the colour grows.
        Assert.True(lr / lg > r / g);
        Assert.True(lr / lb > r / b);
    }

    [Fact]
    public void Overbright_ClampsAtWhite_AsAnEightBitFrameBufferDoes()
    {
        Assert.Equal(1f, DpColour.Shown(DpColour.Lightmap(0.9f, 0.8f)), 5);   // 1.44 -> 1
        Assert.Equal(0f, DpColour.Shown(-0.2f), 5);
    }

    [Fact]
    public void Glow_IsAddedUnlit()
    {
        Assert.Equal(0.5f * 0.2f * 2f + 0.7f, DpColour.Lightmap(0.5f, 0.2f, glow: 0.7f), 6);
    }

    [Theory]
    [InlineData(1.00f)]
    [InlineData(0.70f)]
    [InlineData(0.25f)]
    public void Deluxe_WithoutANormalMap_IsThePlainLightmap_DownToAQuarter(float lightNormalZ)
    {
        // lightcolor = lightmap / max(0.25, z); diffuse = z; product = lightmap for z >= 0.25.
        Assert.Equal(DpColour.Lightmap(0.6f, 0.4f), DpColour.Deluxe(0.6f, 0.4f, lightNormalZ, lightNormalZ), 5);
    }

    [Fact]
    public void Deluxe_GrazingLight_IsDarkened()
    {
        // z = 0.1: lightcolor = lightmap / 0.25, diffuse = 0.1 -> 0.4 of the plain lightmap.
        Assert.Equal(DpColour.Lightmap(0.6f, 0.4f) * 0.4f, DpColour.Deluxe(0.6f, 0.4f, 0.1f, 0.1f), 5);
    }

    [Fact]
    public void Deluxe_Specular_UsesTheExactExponent_AndTheOverbrightTwo()
    {
        // r_shadow_glossexponent 32 with r_shadow_glossexact 1: SpecularPower = 32 * 0.25 - 1 = 7, so the
        // exponent is 1 + 7 * gloss.a.
        Assert.Equal(MathF.Pow(0.9f, 8f), DpColour.Specular(0.9f, 1f), 6);
        Assert.Equal(0.9f, DpColour.Specular(0.9f, 0f), 6);
        Assert.Equal(0f, DpColour.Specular(-0.3f, 1f), 6);
        float specular = DpColour.Specular(0.9f, 0.5f);
        // (diffusetex * 2 * diffuse + gloss * 2 * specular) * lightcolor
        float expected = (0.5f * 2f * 1f + 0.8f * 2f * specular) * 0.3f;
        Assert.Equal(expected, DpColour.Deluxe(0.5f, 0.3f, 1f, 1f, gloss: 0.8f, specular: specular), 5);
    }

    [Fact]
    public void LightStyleZero_ScalesTheLightmap_AndTheUsualStyleIsNotOne()
    {
        // cl_main.c CL_RelinkLightFlashes: (letter - 'a') * 22 / 256; gl_rmain.c R_UpdateVariables multiplies
        // the lightmap intensity of a Quake 3 level by the value of style 0.
        Assert.Equal(12 * 22 / 256f, DpColour.LightStyleValue("m", 0), 6);       // 1.03125
        Assert.Equal(1.03125f, DpColour.LightStyleValue("m", 123.456), 6);
        Assert.Equal(1f, DpColour.LightStyleValue(null, 5), 6);
        Assert.Equal(1f, DpColour.LightStyleValue("", 5), 6);
        Assert.Equal(1.5f, DpColour.LightStyleValue("=1.5", 5), 6);
        Assert.Equal(0f, DpColour.LightStyleValue("a", 5), 6);
        Assert.Equal(25 * 22 / 256f, DpColour.LightStyleValue("z", 5), 6);
        // "az" at t = 0.05: step 0 is 'a', the step before it (index -1) is 'z'; half way between them.
        Assert.Equal((0 * 0.5f + 25 * 0.5f) * 22 / 256f, DpColour.LightStyleValue("az", 0.05), 4);
        // The wall of the first test, as DarkPlaces really shows it on a stock level.
        Assert.Equal(0.5f * 0.4f * 2f * 1.03125f, DpColour.Lightmap(0.5f, 0.4f, intensity: DpColour.LightStyleValue("m", 0)), 6);
    }

    [Fact]
    public void GlossExponentMod_TightensTheHighlight()
    {
        // dpglossexponentmod 4 (most of Xonotic's wall shaders): SpecularPower = 32 * 4 * 0.25 - 1 = 31.
        Assert.Equal(MathF.Pow(0.9f, 32f), DpColour.Specular(0.9f, 1f, exponentMod: 4f), 6);
        Assert.True(DpColour.Specular(0.9f, 1f, exponentMod: 4f) < DpColour.Specular(0.9f, 1f) * 0.1f);
    }

    [Fact]
    public void LitShaders_TakeTheLightmapIntensity_FullBrightOnesDoNot()
    {
        Assert.Contains("world_lightmap_scale", Generate(Water));
        Assert.Contains("world_lightmap_scale", Generate(Glass, lightmapped: true));
        Assert.Contains("world_lightmap_scale", Generate(Grass, forModel: true));
        Assert.DoesNotContain("world_lightmap_scale", Generate(Lava));
    }

    [Fact]
    public void LightGrid_IsColormodTimesTwoTimesAmbientPlusDirected()
    {
        // shader_glsl.h MODE_LIGHTGRID: diffusetex * Color_Diffuse * (ambient + diffuse * directed), Color_Diffuse = colormod * 2.
        float expected = 0.5f * 1f * 2f * (0.2f + 0.6f * 0.3f);
        Assert.Equal(expected, DpColour.LightGrid(0.5f, 1f, ambient: 0.2f, directed: 0.3f, diffuse: 0.6f), 6);
        // A surface facing away from the light keeps the ambient term only.
        Assert.Equal(0.5f * 2f * 0.2f, DpColour.LightGrid(0.5f, 1f, 0.2f, 0.3f, diffuse: -0.4f), 6);
        // Gloss and glow: + gloss * 2 * specular * directed + glow * glowmod.
        Assert.Equal(expected + 0.9f * 2f * 0.5f * 0.3f + 0.4f * 0.5f,
            DpColour.LightGrid(0.5f, 1f, 0.2f, 0.3f, 0.6f, gloss: 0.9f, specular: 0.5f, glow: 0.4f, glowMod: 0.5f), 5);
    }

    [Fact]
    public void FullBright_AndTeamColours()
    {
        Assert.Equal(0.6f * 0.5f + 0.2f, DpColour.FullBright(0.6f, colorMod: 0.5f, glow: 0.2f), 6);
        Assert.Equal(0.3f + 0.5f * 0.8f + 0.25f * 0.4f, DpColour.ColorMapped(0.3f, 0.5f, 0.8f, 0.25f, 0.4f), 6);
    }

    [Fact]
    public void TheCurve_IsTheSrgbCurve_AndItsInverse()
    {
        Assert.Equal(0.21404f, DpColour.ToLinear(0.5f), 4);
        Assert.Equal(0.5f, DpColour.ToDisplay(0.21404f), 4);
        Assert.Equal(0.02f / 12.92f, DpColour.ToLinear(0.02f), 7);          // the linear toe
        for (int i = 0; i <= 255; i++)
        {
            float v = i / 255f;
            Assert.Equal(v, DpColour.ToDisplay(DpColour.ToLinear(v)), 4);   // every 8-bit value survives the round trip
        }
        // Above one as well (a colormod of 2 reaches the shader decoded and is encoded back).
        Assert.Equal(2f, DpColour.ToDisplay(DpColour.ToLinear(2f)), 3);
    }

    [Fact]
    public void AddingInLinearLight_IsWeakerThanAddingDisplayValues()
    {
        // Why the session's 3D buffer holds display values: an additive surface of 0.25 over pale sand (0.80).
        // DarkPlaces shows 0.80 + 0.25 = 1.0 (clamped); a linear buffer shows the encoding of the linear sum.
        float display = Math.Min(1f, 0.80f + 0.25f);
        float linear = DpColour.ToDisplay(DpColour.ToLinear(0.80f) + DpColour.ToLinear(0.25f));
        Assert.Equal(1f, display, 5);
        Assert.InRange(linear, 0.82f, 0.85f);              // the water all but vanished
        // Over black the two agree, which is why a dark level hid the difference.
        Assert.Equal(0.25f, DpColour.ToDisplay(DpColour.ToLinear(0f) + DpColour.ToLinear(0.25f)), 4);
    }

    // ---- the shader text ----------------------------------------------------------------------------------

    private static string Generate(string script, bool forModel = false, bool lightmapped = false, bool glow = false)
    {
        ShaderDef def = Parse(script);
        DpMaterialPlan plan = DpMaterialRules.Plan(def);
        ShaderStage? background = plan.BackgroundStage >= 0 ? def.Stages[plan.BackgroundStage] : null;
        return DpSurfaceShaderGen.Generate(def, plan, def.Stages[plan.MaterialStage], background, forModel, glow, 1, lightmapped);
    }

    [Fact]
    public void Shader_Lava_IsUnshadedAdditive_WithItsAlpha_OnTheSessionsClock()
    {
        string code = Generate(Lava);
        Assert.StartsWith(DpSurfaceShaderGen.Banner, code);
        Assert.Contains("unshaded", code);
        Assert.Contains("blend_add", code);
        Assert.Contains("depth_draw_never", code);
        Assert.Contains("cull_disabled", code);
        Assert.Contains("ALBEDO = dp_fb(rgb * c.a);", code);           // GL_SRC_ALPHA GL_ONE
        Assert.DoesNotContain("c.a = 1.0;", code);                      // the texture keeps its alpha
        Assert.DoesNotContain("COLOR.rgb *", code);                   // full-bright: no vertex light
        Assert.Contains("dp_time", code);                               // deformVertexes wave on cl.time
        Assert.DoesNotContain("TIME", code.Replace("dp_time", ""));     // never the engine's clock
        Assert.DoesNotContain("source_color", code);                    // the stored texel, undecoded
    }

    [Fact]
    public void Shader_Water_DropsTheTexturesAlpha_AndIsLitByVertexColour()
    {
        string code = Generate(Water);
        Assert.Contains("blend_add", code);
        Assert.Contains("c.a = 1.0;", code);                            // no TEXF_ALPHA
        Assert.Contains("c.a *= COLOR.a;", code);                       // alphaGen vertex
        Assert.Contains("rgb *= COLOR.rgb * (2.0 * world_lightmap_scale);", code);   // MODE_VERTEXCOLOR
        Assert.Contains("uv *= vec2(0.3, 0.4);", code);                 // tcMod scale
        Assert.Contains("* dp_time;", code);                            // tcMod scroll on cl.time
    }

    [Fact]
    public void Shader_LightmappedGlass_MultipliesByThePage_AndMixesByAlpha()
    {
        string code = Generate(Glass, lightmapped: true);
        Assert.Contains("blend_mix", code);
        Assert.Contains("rgb *= texture(lightmap_tex, UV2).rgb * (2.0 * world_lightmap_scale);", code);   // MODE_LIGHTMAP
        Assert.Contains("ALBEDO = dp_fb(rgb);", code);
        Assert.Contains("ALPHA = c.a;", code);
    }

    [Fact]
    public void Shader_EnvironmentGlass_UsesTheSphereMap()
    {
        string code = Generate(EnvironmentGlass);
        Assert.Contains("varying vec2 dp_uv;", code);                   // Q3TCGEN_ENVIRONMENT
        Assert.Contains("vec2 uv = dp_uv;", code);
        Assert.DoesNotContain("lightmap_tex", code);
    }

    [Fact]
    public void Shader_Foliage_TestsAlphaAtOneHalf_AndStaysOpaque()
    {
        string code = Generate(Grass);
        Assert.Contains("if (c.a < 0.5) discard;", code);
        Assert.DoesNotContain("blend_", code);
        Assert.DoesNotContain("ALPHA =", code);                         // writing ALPHA would leave the opaque pass
        Assert.Contains("ALBEDO = dp_fb(rgb);", code);
    }

    [Fact]
    public void Shader_Terrain_BlendsAsDarkPlacesWithBothAlphas()
    {
        string code = Generate(Terrain, lightmapped: true);
        Assert.Contains("float terrainblend = max(clamp(COLOR.a * c.a, 0.0, 1.0), 1.0 - c2.a);", code);
        Assert.Contains("c = vec4(mix(c2.rgb, c.rgb, terrainblend), 1.0);", code);
    }

    [Fact]
    public void Shader_InverseModulate_WritesOneMinusTheTexel_AsAFactor()
    {
        string code = Generate(Decal);
        Assert.Contains("blend_mul", code);
        Assert.Contains("ALBEDO = dp_fb_factor(vec3(1.0) - rgb);", code);
        Assert.DoesNotContain("rgbGen", code);                          // DarkPlaces never evaluates rgbGen wave
        Assert.DoesNotContain("sin(wx", code);
    }

    [Fact]
    public void Shader_OnAModel_IsLitFromTheGrid_AndSharesTheSkinShadersInstanceSlots()
    {
        string code = Generate(Grass, forModel: true);
        Assert.Contains("instance uniform vec3 colormod : source_color, instance_index(2)", code);
        Assert.Contains("instance uniform vec3 glowmod : source_color, instance_index(3)", code);
        Assert.Contains("instance uniform float grid_lit : instance_index(4)", code);
        Assert.Contains("lightgrid_tex", code);
        Assert.Contains("rgb *= d_mod * (amb + dif * ndl) * world_lightmap_scale;", code);     // MODE_LIGHTGRID, the twos in lightgrid_params.z
        Assert.Contains("grid_lit > 2.5", code);                        // EF_FULLBRIGHT
        Assert.Contains("morph_amount", code);                          // usable on a vertex-animated model
        Assert.Contains("viewmodel_depth_range", code);                 // and on the view model
    }

    [Fact]
    public void ShaderFunctions_WriteDisplayValuesOrTheirDecoding()
    {
        Assert.Contains("global uniform float dp_framebuffer;", DpColour.ShaderFunctions);
        Assert.Contains("return dp_framebuffer > 0.5 ? clamped : dp_linear(clamped);", DpColour.ShaderFunctions);
    }

    // ---- the comparison harness: a paused recording stands on its newest server time --------------------

    [Fact]
    public void Clock_HeldAtServerTime_HasNothingLeftToInterpolate()
    {
        DpClientClock clock = new() { Demo = true };
        clock.NetworkTimeReceived(10.0, signedOn: false);
        clock.NetworkTimeReceived(10.05, signedOn: true);
        clock.Advance(0.01);
        clock.HoldAtServerTime();
        Assert.Equal(10.05, clock.Time, 9);
        Assert.Equal(clock.Time, clock.OldTime, 9);
        clock.Advance(0.5, paused: true);
        Assert.Equal(10.05, clock.Time, 9);
    }
}
