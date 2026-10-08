// Port of the part of Base/darkplaces/model_shared.c Mod_LoadTextureFromQ3Shader that turns a Quake 3
// shader into what DarkPlaces actually draws, and of the arithmetic in shader_glsl.h / gl_rmain.c that a
// legacy session's shaders reproduce. No Godot types: the rules and the numbers are testable on their own.
//
// DarkPlaces does not run a Quake 3 shader's stages one after another. It picks ONE stage as the material
// (plus, for terrain, one background stage it blends by vertex alpha), takes the blend function of the FIRST
// stage, and decides once whether the surface is lit. The other stages are parsed and never drawn
// ("shaderpasses" are created for them and nothing reads them: gl_rmain.c only ever uses materialshaderpass
// and backgroundshaderpass). rgbGen and alphaGen are read for one purpose: to decide whether the surface is lit.
using System;

namespace VortexArena.Formats.Materials;

/// <summary>How DarkPlaces blends a surface with what is behind it (texture_t.currentblendfunc).</summary>
public enum DpBlend
{
    /// <summary>GL_ONE GL_ZERO.</summary>
    Opaque = 0,
    /// <summary>MATERIALFLAG_ADD: drawn GL_SRC_ALPHA GL_ONE (for a first stage of GL_ONE GL_ONE or GL_SRC_ALPHA GL_ONE).</summary>
    Add,
    /// <summary>MATERIALFLAG_ALPHA: GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA.</summary>
    Alpha,
    /// <summary>MATERIALFLAG_CUSTOMBLEND: the first stage's own factor pair; always full-bright.</summary>
    Custom,
}

/// <summary>What DarkPlaces draws for one Quake 3 shader.</summary>
public readonly struct DpMaterialPlan
{
    /// <summary>MATERIALFLAG_NODRAW: nothing is drawn (surfaceparm nodraw, or a shader without stages).</summary>
    public bool NoDraw { get; init; }
    /// <summary>MATERIALFLAG_SKY.</summary>
    public bool Sky { get; init; }
    /// <summary>Index into <see cref="ShaderDef.Stages"/> of the stage that is the material, or -1.</summary>
    public int MaterialStage { get; init; }
    /// <summary>The terrain background stage (blended with the material by vertex alpha), or -1.</summary>
    public int BackgroundStage { get; init; }
    public DpBlend Blend { get; init; }
    /// <summary>The first stage's factors, for <see cref="DpBlend.Custom"/>.</summary>
    public BlendFactor CustomSrc { get; init; }
    public BlendFactor CustomDst { get; init; }
    /// <summary>MATERIALFLAG_ALPHATEST (the first stage has an alphaFunc): texels under one half are not drawn.</summary>
    public bool AlphaTest { get; init; }
    /// <summary>MATERIALFLAG_FULLBRIGHT: no stage asked for light ($lightmap, rgbGen lightingDiffuse, rgbGen vertex), or the blend is custom.</summary>
    public bool FullBright { get; init; }
    /// <summary>MATERIALFLAG_ALPHAGEN_VERTEX: the first stage has alphaGen vertex; the vertex alpha multiplies the texel's.</summary>
    public bool VertexAlpha { get; init; }
    /// <summary>MATERIALFLAG_NOCULLFACE (cull none / disable / twosided).</summary>
    public bool TwoSided { get; init; }
    /// <summary>
    /// TEXF_ALPHA on the material stage's texture: DarkPlaces keeps a texture's alpha channel only for a stage
    /// that tests alpha or whose own blend function names the source alpha; every other stage's texture is
    /// loaded opaque (alpha one), whatever the file holds. A "blendfunc add" water texture with a low alpha
    /// channel is therefore added at full strength.
    /// </summary>
    public bool MaterialAlpha { get; init; }
    /// <summary>The same for the terrain background stage (mod_q3shader_force_terrain_alphaflag 1, Xonotic's
    /// setting, gives the first stage of a terrain blend its alpha).</summary>
    public bool BackgroundAlpha { get; init; }

    public bool Blended => Blend != DpBlend.Opaque;
}

public static class DpMaterialRules
{
    /// <summary>
    /// Mod_LoadTextureFromQ3Shader, "here be dragons: convert quake3 shaders to material".
    /// </summary>
    public static DpMaterialPlan Plan(ShaderDef shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        int count = shader.Stages.Count;
        if (shader.IsSky) return new DpMaterialPlan { Sky = true, MaterialStage = -1, BackgroundStage = -1 };
        if (shader.IsNoDraw || count == 0) return new DpMaterialPlan { NoDraw = true, MaterialStage = -1, BackgroundStage = -1 };

        // "shader.lighting": any stage with map $lightmap, rgbGen lightingDiffuse or rgbGen vertex.
        bool lighting = false;
        int lightmapLayer = -1, vertexLayer = -1, diffuseLayer = -1;
        for (int i = 0; i < count; i++)
        {
            ShaderStage s = shader.Stages[i];
            if (s.IsLightmap) { lightmapLayer = i; lighting = true; }
            if (s.RgbGen?.Type == ColorGenType.Vertex) { vertexLayer = i; lighting = true; }
            if (s.RgbGen?.Type == ColorGenType.LightingDiffuse) { diffuseLayer = i; lighting = true; }
        }

        ShaderStage first = shader.Stages[0];
        (BlendFactor src, BlendFactor dst) = Factors(first);
        DpBlend blend = DpBlend.Opaque;
        bool fullBright = !lighting;
        if (src != BlendFactor.One || dst != BlendFactor.Zero)
        {
            if ((src == BlendFactor.One || src == BlendFactor.SrcAlpha) && dst == BlendFactor.One) blend = DpBlend.Add;
            else if (src == BlendFactor.SrcAlpha && dst == BlendFactor.OneMinusSrcAlpha) blend = DpBlend.Alpha;
            else
            {
                blend = DpBlend.Custom;
                fullBright = true;
            }
        }

        int material, background = -1;
        if (count >= 2
            && shader.Stages[1].AlphaGen?.Type == ColorGenType.Vertex
            && src == BlendFactor.One && dst == BlendFactor.Zero && first.AlphaFunc is null
            && IsTerrainTop(shader.Stages[1]))
        {
            // "terrain blend or certain other effects involving alphatest over a regular layer"
            background = 0;
            material = 1;
        }
        else if (lightmapLayer == 0) material = 1;               // "$lightmap before diffuse"
        else if (lightmapLayer >= 1) material = lightmapLayer - 1; // "ordinary texture"
        else if (vertexLayer >= 0) material = vertexLayer;        // "map models with baked lighting"
        else if (diffuseLayer >= 0) material = diffuseLayer;      // "entity models with dynamic lighting"
        else material = 0;                                        // "special effects shaders"
        if (material >= count) material = -1;

        return new DpMaterialPlan
        {
            MaterialStage = material,
            BackgroundStage = background,
            Blend = blend,
            CustomSrc = src,
            CustomDst = dst,
            AlphaTest = first.AlphaFunc is not null,
            FullBright = fullBright,
            VertexAlpha = first.AlphaGen?.Type == ColorGenType.Vertex,
            TwoSided = shader.Cull == CullMode.None,
            MaterialAlpha = material >= 0 && (KeepsAlpha(shader.Stages[material]) || (material == 0 && TerrainAlphaOnFirst(shader))),
            BackgroundAlpha = background >= 0 && (KeepsAlpha(shader.Stages[background]) || TerrainAlphaOnFirst(shader)),
        };

        // "multilayer terrain shader or similar": a later stage with alphaGen vertex puts TEXF_ALPHA on stage 0.
        static bool TerrainAlphaOnFirst(ShaderDef def)
        {
            for (int i = 1; i < def.Stages.Count; i++)
                if (def.Stages[i].AlphaGen?.Type == ColorGenType.Vertex) return true;
            return false;
        }

        static bool IsTerrainTop(ShaderStage second)
        {
            (BlendFactor s, BlendFactor d) = Factors(second);
            return (s == BlendFactor.SrcAlpha && d == BlendFactor.OneMinusSrcAlpha)
                || (s == BlendFactor.One && d == BlendFactor.Zero && second.AlphaFunc is not null);
        }
    }

    /// <summary>layer->dptexflags & TEXF_ALPHA: an alphaFunc, or a blend function naming GL_SRC_ALPHA or
    /// GL_ONE_MINUS_SRC_ALPHA on either side.</summary>
    public static bool KeepsAlpha(ShaderStage stage)
    {
        if (stage.AlphaFunc is not null) return true;
        (BlendFactor src, BlendFactor dst) = Factors(stage);
        return src is BlendFactor.SrcAlpha or BlendFactor.OneMinusSrcAlpha || dst is BlendFactor.SrcAlpha or BlendFactor.OneMinusSrcAlpha;
    }

    /// <summary>A stage's blend factors with DarkPlaces' default (GL_ONE GL_ZERO) for a stage that names none.</summary>
    public static (BlendFactor Src, BlendFactor Dst) Factors(ShaderStage stage)
    {
        BlendFactor src = stage.BlendSrc == BlendFactor.None ? BlendFactor.One : stage.BlendSrc;
        BlendFactor dst = stage.BlendDst == BlendFactor.None ? BlendFactor.Zero : stage.BlendDst;
        return (src, dst);
    }
}

/// <summary>
/// The arithmetic of DarkPlaces' surface shader with Xonotic's default configuration (vid_sRGB 0,
/// r_glsl_deluxemapping 1, r_shadow_gloss 1, r_shadow_glossexact 1, r_shadow_glossexponent 32,
/// r_hdr_scenebrightness 1, r_ambient 0), on display values in 0..1 - the reference the shaders of a legacy
/// session are written against (LightmapShader "dp_exact", PlayerSkinShader "model_light_gamma > 1.5").
/// </summary>
public static class DpColour
{
    /// <summary>Image_LinearFloatFromsRGBFloat: the sRGB decoding curve (what Godot's output transform inverts).</summary>
    public static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>Image_sRGBFloatFromLinearFloat.</summary>
    public static float ToDisplay(float c) => c < 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;

    /// <summary>
    /// r_refdef.scene.rtlightstylevalue[0], which DarkPlaces multiplies the lightmap intensity of a Quake 3 level
    /// by ("Apply the default lightstyle to the lightmap even on q3bsp", gl_rmain.c R_UpdateVariables): the
    /// style string is stepped ten times a second, each letter worth (letter - 'a') * 22 / 256 and interpolated
    /// with the one before (cl_main.c CL_RelinkLightFlashes). The usual "m" is therefore 1.03125, not 1: every
    /// lit surface and every grid-lit model of a stock level is 3 % brighter than texel * lightmap * 2.
    /// An empty style is 1; "=1.5" is that number.
    /// </summary>
    public static float LightStyleValue(string? style, double time)
    {
        if (string.IsNullOrEmpty(style)) return 1f;
        if (style[0] == '=')
            return float.TryParse(style.AsSpan(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fixedValue) ? fixedValue : 0f;
        double f = time * 10;
        int i = (int)Math.Floor(f);
        float frac = (float)(f - i);
        int k = style[Modulo(i, style.Length)] - 'a';
        int l = style[Modulo(i - 1, style.Length)] - 'a';
        return (k * frac + l * (1 - frac)) * (22 / 256.0f);

        static int Modulo(int value, int length) => ((value % length) + length) % length;
    }

    /// <summary>What a frame buffer of 8 bits a channel does to a colour: clamps it.</summary>
    public static float Saturate(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>
    /// MODE_LIGHTMAP / MODE_VERTEXCOLOR: <c>diffusetex * (Color_Ambient + lightmap * Color_Diffuse) + glow *
    /// Color_Glow</c> with Color_Ambient = r_ambient / 64 (0), Color_Diffuse = 2 ("2x diffuse and specular
    /// brightness because bsp files have 0-2 colors as 0-1", gl_rmain.c R_UpdateCurrentTexture) and
    /// Color_Glow = r_hdr_glowintensity (1). All three arguments are the stored texel values.
    /// </summary>
    /// <param name="intensity">r_refdef.scene.lightmapintensity: <see cref="LightStyleValue"/> of style 0.</param>
    public static float Lightmap(float texel, float lightmap, float glow = 0, float intensity = 1) => texel * (lightmap * 2f * intensity) + glow;

    /// <summary>
    /// MODE_LIGHTDIRECTIONMAP_MODELSPACE (a deluxemapped level): the light colour is first divided by
    /// max(0.25, lightnormal.z) - undoing the angle attenuation q3map2 baked in - and then multiplied by
    /// the diffuse term dot(surfacenormal, lightnormal), so a surface without a normal map and with the
    /// light at least 14.5 degrees off its plane shows exactly <see cref="Lightmap"/>.
    /// </summary>
    /// <param name="lightNormalZ">The deluxemap direction's component along the surface normal, after normalising.</param>
    /// <param name="diffuse">sat(dot(surfacenormal, lightnormal)); equal to <paramref name="lightNormalZ"/> without a normal map.</param>
    /// <param name="gloss">The gloss texel (0 without a gloss texture).</param>
    /// <param name="specular">The specular term, <see cref="Specular"/>.</param>
    public static float Deluxe(float texel, float lightmap, float lightNormalZ, float diffuse, float gloss = 0, float specular = 0, float glow = 0)
    {
        float lightColour = lightmap / MathF.Max(0.25f, lightNormalZ);
        return (texel * 2f * Saturate(diffuse) + gloss * 2f * specular) * lightColour + glow;
    }

    /// <summary>
    /// SHADESPECULAR with USEEXACTSPECULARMATH: <c>pow(sat(dot(reflect(lightnormal, surfacenormal), -eyenormal)),
    /// 1 + SpecularPower * gloss.a)</c> where SpecularPower = r_shadow_glossexponent * dpglossexponentmod * 0.25 - 1
    /// (gl_rmain.c:1930; "t->specularpower *= t->specularpowermod"). Most of Xonotic's wall shaders say
    /// dpglossexponentmod 4, so their highlight has the exponent 1 + 31 * gloss.a, not 1 + 7 * gloss.a.
    /// </summary>
    /// <param name="reflectDotEye">dot(reflect(L, N), -E), which is dot(2 N (N.L) - L, E).</param>
    public static float Specular(float reflectDotEye, float glossAlpha, float glossExponent = 32f, float exponentMod = 1f) =>
        MathF.Pow(Saturate(reflectDotEye), 1f + (glossExponent * exponentMod * 0.25f - 1f) * glossAlpha);

    /// <summary>
    /// MODE_LIGHTGRID (every model on a level with a light grid): <c>diffusetex * Color_Diffuse * (ambient +
    /// diffuse * directed) + gloss * Color_Specular * specular * directed + glow * Color_Glow</c>, with
    /// Color_Diffuse = colormod * 2 and Color_Specular = 2; ambient and directed are the light grid's bytes / 255.
    /// </summary>
    public static float LightGrid(float texel, float colorMod, float ambient, float directed, float diffuse,
        float gloss = 0, float specular = 0, float glow = 0, float glowMod = 1) =>
        texel * colorMod * 2f * (ambient + Saturate(diffuse) * directed) + gloss * 2f * specular * directed + glow * glowMod;

    /// <summary>MODE_FLATCOLOR (a full-bright surface or entity): <c>diffusetex * colormod + glow * glowmod</c>.</summary>
    public static float FullBright(float texel, float colorMod = 1, float glow = 0, float glowMod = 1) => texel * colorMod + glow * glowMod;

    /// <summary>The colour of a skin texel with the team colours: <c>texel + pants * Color_Pants + shirt * Color_Shirt</c>.</summary>
    public static float ColorMapped(float texel, float pantsMask, float pantsColour, float shirtMask, float shirtColour) =>
        texel + pantsMask * pantsColour + shirtMask * shirtColour;

    /// <summary>
    /// What the screen shows for a colour a shader of a legacy session computed: the session's shaders hand
    /// the engine <see cref="ToLinear"/> of the clamped value and the engine's output transform applies
    /// <see cref="ToDisplay"/>, so the two cancel and the value itself is displayed.
    /// </summary>
    public static float Shown(float display) => ToDisplay(ToLinear(Saturate(display)));

    /// <summary>
    /// The GDShader helpers every shader of a legacy session ends with. <c>dp_fb(display)</c> is what to write
    /// for a display colour: the colour itself while the session's 3D buffer holds display values (the global
    /// shader parameter dp_framebuffer, see the game's DisplayFramebuffer), its decoding when the buffer holds
    /// linear light. <c>dp_fb_factor</c> is the same for the factor of a multiplying blend (not clamped at one).
    /// </summary>
    public const string ShaderFunctions = @"global uniform float dp_framebuffer;
vec3 dp_linear(vec3 c) {
    return mix(c * (1.0 / 12.92), pow((max(c, vec3(0.0)) + 0.055) * (1.0 / 1.055), vec3(2.4)), step(vec3(0.04045), c));
}
vec3 dp_display(vec3 c) {
    return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}
vec3 dp_fb(vec3 display) {
    vec3 clamped = clamp(display, vec3(0.0), vec3(1.0));   // an 8-bit frame buffer clamps what is written
    return dp_framebuffer > 0.5 ? clamped : dp_linear(clamped);
}
vec3 dp_fb_factor(vec3 display) {
    return dp_framebuffer > 0.5 ? max(display, vec3(0.0)) : dp_linear(display);
}
";

    /// <summary>
    /// The same data combined the way the native game combines it (decode, multiply in linear light, encode):
    /// what a legacy session showed before it had its own arithmetic. Kept so the difference can be stated.
    /// </summary>
    public static float LightmapInLinearLight(float texel, float lightmap) => ToDisplay(Saturate(ToLinear(texel) * ToLinear(lightmap) * 2f));
}
