using System;
using Godot;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Materials;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The surfaces of a Quake 1 format map (BSP 29, BSP2, 2PSB, Half-Life 30), drawn as DarkPlaces draws them
/// with Xonotic's configuration (<c>vid_sRGB 0</c>): the stored texel times the lightmap times two, the sum
/// shown as it is (shader_glsl.h MODE_LIGHTMAP; gl_rmain.c "2x diffuse and specular brightness because bsp
/// files have 0-2 colors as 0-1").
///
/// <para><b>The lightmap is not one image.</b> A face has up to four layers of samples, each tied to a light
/// style, and the lightmap DarkPlaces draws is their sum weighted by the styles' current values
/// (gl_rsurf.c R_BuildLightMap): <c>min(255, (layer0 * value0 + layer1 * value1 + ...) >> 8)</c>, with 256
/// meaning one. DarkPlaces rebuilds and uploads a face's block whenever one of its styles changes. Here the
/// layers are the layers of a texture array (<see cref="Q1LightmapAtlas"/>), the four style numbers of a face
/// ride in its vertex colour, and the vertex stage reads their current values from a 256 x 1 texture
/// (<see cref="Q1LightStyleTexture"/>): a flickering or switched light costs one tiny texture update, not a
/// lightmap upload.</para>
///
/// <para>Liquids ("*name") use the same arithmetic with DarkPlaces' <c>r_waterscroll</c> texture matrix (a
/// translation of the texture coordinates by <c>sin(t) * 0.025</c> and <c>sin(0.8 t) * 0.025</c>; DarkPlaces
/// does not warp vertices or texels as software Quake did) and, on a map whose visibility data allows it,
/// <c>r_wateralpha</c>. A sky surface shows the sky dome or box in place of its texture (r_sky.c).</para>
///
/// <para>Dynamic lights reach these surfaces exactly as they reach a Quake 3 level's
/// (<see cref="LightmapShader"/>): the baked result is EMISSION, the texel is ALBEDO, and <c>light()</c> adds
/// DarkPlaces' own falloff.</para>
/// </summary>
public static class Q1SurfaceShader
{
    public static readonly StringName AlbedoUniform = "albedo_tex";
    public static readonly StringName GlowUniform = "glow_tex";
    public static readonly StringName UseGlowUniform = "use_glow";
    public static readonly StringName LightmapUniform = "lightmap_tex";
    public static readonly StringName LightmapLayersUniform = "lightmap_layers";
    public static readonly StringName StyleUniform = "style_tex";
    public static readonly StringName FixedLightUniform = "lm_fixed";
    public static readonly StringName WaterScrollUniform = "water_scroll";
    public static readonly StringName SurfaceAlphaUniform = "surface_alpha";

    private const string Header = @"// VortexArena Quake 1 map surface shader. Generated in C# (Q1SurfaceShader).
shader_type spatial;
";

    // {MODE}: extra render modes. {DISCARD}: the alpha test. {ALPHA}: the alpha write of a blended surface.
    private const string Body = @"render_mode cull_back, depth_draw_opaque, ambient_light_disabled{MODE};

uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D glow_tex : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_glow = false;
// The light of the map: one layer per style layer of the faces on this page.
uniform sampler2DArray lightmap_tex : filter_linear, repeat_disable;
uniform int lightmap_layers = 1;
// 256 x 1: the current value of each light style, 1.0 = lightstylevalue 256. Entry 255 is 0 (no layer).
uniform sampler2D style_tex : filter_nearest, repeat_disable;
// A map without light data: every sample is 128 and no style applies.
uniform bool lm_fixed = false;
// r_waterscroll on a liquid, 0 on anything else.
uniform float water_scroll = 0.0;
// r_wateralpha on a blended liquid.
uniform float surface_alpha = 1.0;

global uniform vec3 map_tint;
global uniform float world_dlight;
global uniform float world_nolightmaps;
global uniform float world_gamma_space;
global uniform float dp_time;
" + DpColour.ShaderFunctions + @"
varying vec4 v_scale;

void vertex() {
    // The four style numbers of the face, as bytes in the vertex colour.
    ivec4 style = ivec4(COLOR * 255.0 + vec4(0.5));
    v_scale = lm_fixed ? vec4(1.0, 0.0, 0.0, 0.0) : vec4(
        texelFetch(style_tex, ivec2(style.r, 0), 0).r,
        texelFetch(style_tex, ivec2(style.g, 0), 0).r,
        texelFetch(style_tex, ivec2(style.b, 0), 0).r,
        texelFetch(style_tex, ivec2(style.a, 0), 0).r);
    // gl_rmain.c: Matrix4x4_CreateTranslate(&r_waterscrollmatrix, sin(time) * 0.025 * r_waterscroll,
    // sin(time * 0.8) * 0.025 * r_waterscroll, 0), applied to the texture coordinates.
    if (water_scroll != 0.0) {
        UV += vec2(sin(dp_time), sin(dp_time * 0.8)) * (0.025 * water_scroll);
    }
}

void fragment() {
    vec4 base = texture(albedo_tex, UV);
    vec3 albedo = base.rgb;
    vec3 glow = use_glow ? texture(glow_tex, UV).rgb : vec3(0.0);

    // gl_rsurf.c R_BuildLightMap: the layers times their styles' values, summed, shifted to 8 bits and
    // clamped at 255. (The shift drops the fraction of each stored texel: half a unit on average.)
    vec3 lm = texture(lightmap_tex, vec3(UV2, 0.0)).rgb * v_scale.x;
    if (lightmap_layers > 1) { lm += texture(lightmap_tex, vec3(UV2, 1.0)).rgb * v_scale.y; }
    if (lightmap_layers > 2) { lm += texture(lightmap_tex, vec3(UV2, 2.0)).rgb * v_scale.z; }
    if (lightmap_layers > 3) { lm += texture(lightmap_tex, vec3(UV2, 3.0)).rgb * v_scale.w; }
    if (!lm_fixed) { lm = max(lm - vec3(0.5 / 255.0), vec3(0.0)); }
    lm = min(lm, vec3(1.0)) * 2.0;

    if (world_gamma_space > 1.5 && world_gamma_space < 2.5) {
        // developer aid: DarkPlaces' gl_lightmaps 1 (a mid-grey texture, no glow)
        albedo = vec3(0.5);
        glow = vec3(0.0);
    }
    // developer aid: r_fullbright
    lm = mix(lm, vec3(1.0), world_nolightmaps);

    vec3 combined = (albedo * lm + glow) * map_tint;
    // The baked result is EMISSION, which no light touches; ALBEDO is what a dynamic light multiplies.
    EMISSION = dp_fb(combined);
    ALBEDO = dp_fb_factor(albedo) * map_tint;
{DISCARD}{ALPHA}}

void light() {
    // As LightmapShader: DarkPlaces' dynamic light pass on top of the baked light; no directional light.
    if (!LIGHT_IS_DIRECTIONAL) {
        float ndotl = clamp(dot(normalize(NORMAL), normalize(LIGHT)), 0.0, 1.0);
        if (dp_framebuffer > 0.5) {
            float q = pow(max(1.0 - sqrt(clamp(ATTENUATION, 0.0, 1.0)), 0.0), 0.25);
            float att = clamp((1.0 - q) * 2.0 / (1.0 + q * q), 0.0, 1.0);
            DIFFUSE_LIGHT += LIGHT_COLOR * (att * ndotl * world_dlight * 0.31830989);
        } else {
            DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * ATTENUATION * ndotl * world_dlight;
        }
    }
}
";

    /// <summary>The opaque program (no discard, so the depth prepass stays position-only).</summary>
    public static readonly string OpaqueCode = Header + Body.Replace("{MODE}", "").Replace("{DISCARD}", "").Replace("{ALPHA}", "");
    /// <summary>A "{" fence texture: MATERIALFLAG_ALPHATEST, alpha below one half is not drawn.</summary>
    public static readonly string MaskedCode = Header + Body.Replace("{MODE}", "")
        .Replace("{DISCARD}", "    if (base.a < 0.5) { discard; }\n").Replace("{ALPHA}", "");
    /// <summary>A blended liquid (r_wateralpha below 1 on a map that supports it).</summary>
    public static readonly string BlendedCode = Header + Body.Replace("{MODE}", ", blend_mix")
        .Replace("{DISCARD}", "").Replace("{ALPHA}", "    ALPHA = base.a * surface_alpha;\n");

    /// <summary>
    /// The Quake sky on a sky surface (r_sky.c R_SkySphere): two layers of one 128 x 128 cloud texture on a
    /// flattened sphere around the eye, the back one solid, the front one keyed, scrolling at
    /// <c>r_skyscroll1</c> and <c>r_skyscroll2</c> (1 and 2) times 8 / 128 of the texture a second. The sphere's
    /// texture coordinate for a view direction d is 3 * normalize(d.x, d.y, 3 d.z).xy.
    /// </summary>
    public static readonly string SkySphereCode = @"// VortexArena Quake sky (r_sky.c R_SkySphere). Generated in C# (Q1SurfaceShader).
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque, ambient_light_disabled, shadows_disabled, fog_disabled;
uniform sampler2D sky_solid : hint_default_black, filter_linear, repeat_enable;
uniform sampler2D sky_alpha : hint_default_transparent, filter_linear, repeat_enable;
uniform float sky_scroll1 = 1.0;
uniform float sky_scroll2 = 2.0;
// The level's fog on the sky: DarkPlaces draws the dome at half the far clip distance, where fog has reached its limit.
uniform vec3 sky_fog_colour = vec3(0.0);
uniform float sky_fog_amount = 0.0;
global uniform float dp_time;
" + DpColour.ShaderFunctions + @"
varying vec3 v_dir;
void vertex() {
    v_dir = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - INV_VIEW_MATRIX[3].xyz;
}
void fragment() {
    vec3 q = vec3(v_dir.x, -v_dir.z, v_dir.y);          // Godot (Y up) to Quake (Z up)
    vec2 tc = normalize(vec3(q.x, q.y, q.z * 3.0)).xy * 3.0;
    float s1 = fract(dp_time * sky_scroll1 * (8.0 / 128.0));
    float s2 = fract(dp_time * sky_scroll2 * (8.0 / 128.0));
    vec3 solid = texture(sky_solid, tc + vec2(s1)).rgb;
    vec4 front = texture(sky_alpha, tc + vec2(s2));
    ALBEDO = dp_fb(mix(mix(solid, front.rgb, front.a), sky_fog_colour, sky_fog_amount));
}
";

    /// <summary>A sky box on a sky surface (r_sky.c R_SkyBox), with the faces SkyboxLoader has oriented.</summary>
    public static readonly string SkyBoxCode = @"// VortexArena sky box on a Quake 1 map's sky surfaces. Generated in C# (Q1SurfaceShader).
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque, ambient_light_disabled, shadows_disabled, fog_disabled;
uniform sampler2D face_px : filter_linear_mipmap, repeat_disable;
uniform sampler2D face_nx : filter_linear_mipmap, repeat_disable;
uniform sampler2D face_py : filter_linear_mipmap, repeat_disable;
uniform sampler2D face_ny : filter_linear_mipmap, repeat_disable;
uniform sampler2D face_pz : filter_linear_mipmap, repeat_disable;
uniform sampler2D face_nz : filter_linear_mipmap, repeat_disable;
uniform vec3 sky_fog_colour = vec3(0.0);
uniform float sky_fog_amount = 0.0;
" + DpColour.ShaderFunctions + @"
varying vec3 v_dir;
void vertex() {
    v_dir = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - INV_VIEW_MATRIX[3].xyz;
}
void fragment() {
    vec3 q = vec3(v_dir.x, -v_dir.z, v_dir.y);
    vec3 a = abs(q);
    float m = max(a.x, max(a.y, a.z));
    vec3 n = q / m;
    vec3 c;
    if (a.x >= a.y && a.x >= a.z) {
        if (n.x > 0.0) c = textureLod(face_px, vec2((1.0 - n.z) * 0.5, (1.0 - n.y) * 0.5), 0.0).rgb;
        else           c = textureLod(face_nx, vec2((1.0 + n.z) * 0.5, (1.0 - n.y) * 0.5), 0.0).rgb;
    } else if (a.y >= a.z) {
        if (n.y > 0.0) c = textureLod(face_py, vec2((1.0 + n.x) * 0.5, (1.0 + n.z) * 0.5), 0.0).rgb;
        else           c = textureLod(face_ny, vec2((1.0 + n.x) * 0.5, (1.0 - n.z) * 0.5), 0.0).rgb;
    } else {
        if (n.z > 0.0) c = textureLod(face_pz, vec2((1.0 + n.x) * 0.5, (1.0 - n.y) * 0.5), 0.0).rgb;
        else           c = textureLod(face_nz, vec2((1.0 - n.x) * 0.5, (1.0 - n.y) * 0.5), 0.0).rgb;
    }
    ALBEDO = dp_fb(mix(c, sky_fog_colour, sky_fog_amount));
}
";

    public static readonly string[] SkyBoxFaceUniforms = { "face_px", "face_nx", "face_py", "face_ny", "face_pz", "face_nz" };

    private static readonly object s_gate = new();
    private static Shader? s_opaque, s_masked, s_blended, s_skySphere, s_skyBox;

    public static Shader Opaque { get { lock (s_gate) return s_opaque ??= new Shader { Code = OpaqueCode }; } }
    public static Shader Masked { get { lock (s_gate) return s_masked ??= new Shader { Code = MaskedCode }; } }
    public static Shader Blended { get { lock (s_gate) return s_blended ??= new Shader { Code = BlendedCode }; } }
    public static Shader SkySphere { get { lock (s_gate) return s_skySphere ??= new Shader { Code = SkySphereCode }; } }
    public static Shader SkyBox { get { lock (s_gate) return s_skyBox ??= new Shader { Code = SkyBoxCode }; } }

    public static bool IsQ1Shader(Shader? shader) =>
        shader is not null && (shader == s_opaque || shader == s_masked || shader == s_blended || shader == s_skySphere || shader == s_skyBox);
}

/// <summary>
/// The current value of every light style as a 256 x 1 float texture, shared by every Quake 1 surface
/// material of the process. Written only when a value changes (a style string steps ten times a second,
/// and most maps' styles are constant).
/// </summary>
public static class Q1LightStyleTexture
{
    private static ImageTexture? s_texture;
    private static Image? s_image;
    private static readonly float[] s_scales = new float[Q1LightStyles.Count];
    private static readonly byte[] s_bytes = new byte[Q1LightStyles.Count * sizeof(float)];
    private static bool s_filled;

    /// <summary>The value of each style, value / 256 (1.03125 for "m"), as last set.</summary>
    public static ReadOnlySpan<float> Scales => s_scales;

    public static ImageTexture Texture
    {
        get
        {
            if (s_texture is null || !GodotObject.IsInstanceValid(s_texture))
            {
                if (!s_filled) Reset();
                Buffer.BlockCopy(s_scales, 0, s_bytes, 0, s_bytes.Length);
                s_image = Image.CreateFromData(Q1LightStyles.Count, 1, false, Image.Format.Rf, s_bytes);
                s_texture = ImageTexture.CreateFromImage(s_image);
            }
            return s_texture;
        }
    }

    /// <summary>Every style at 1 (an unset style), the "no layer" entry at 0.</summary>
    public static void Reset()
    {
        s_filled = true;
        Array.Fill(s_scales, 1f);
        s_scales[Q1LightStyles.Unused] = 0f;
        Upload();
    }

    /// <summary>The styles at <paramref name="time"/> (cl.time). Returns true if the texture was written.</summary>
    public static bool Update(System.Collections.Generic.IReadOnlyList<string?> styles, double time, bool lerp)
    {
        s_filled = true;
        if (!Q1LightStyles.Evaluate(styles, time, lerp, s_scales)) return false;
        Upload();
        return true;
    }

    private static void Upload()
    {
        if (s_texture is null || s_image is null || !GodotObject.IsInstanceValid(s_texture)) return;
        Buffer.BlockCopy(s_scales, 0, s_bytes, 0, s_bytes.Length);
        s_image.SetData(Q1LightStyles.Count, 1, false, Image.Format.Rf, s_bytes);
        s_texture.Update(s_image);
    }
}
