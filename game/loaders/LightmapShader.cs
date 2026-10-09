using Godot;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The lightmap-modulate spatial shader used by IBSP world geometry.
/// (It is no longer <c>unshaded</c>: the baked result is written to EMISSION and realtime lights add to it
/// through light(); the paragraph below describes the baked term.)
///
/// Quake 3 maps ship precomputed per-surface lightmaps and feed each face's lightmap UVs as a second UV
/// channel. Godot's <see cref="LightmapGI"/> cannot ingest these precomputed pages, so the BSP path bypasses
/// it: every world surface samples its albedo with the regular UV and multiplies by the lightmap sampled with
/// <c>UV2</c>. The result is rendered <c>unshaded</c> (the baked lightmap already contains all the lighting) so
/// realtime lights do not double-light the surface — matching Darkplaces' default lightmap path for parity
/// (dynamic relighting is intentionally lost; see asset-pipeline.md §"Lightmaps").
///
/// <para><b>Color space.</b> Xonotic exposes two modes (<c>vid_sRGB</c>/<c>mod_q3bsp_sRGBlightmaps</c>): the
/// "recommended" <c>sRGB-enable</c> path decodes diffuse + lightmap to linear and multiplies in linear space,
/// while the literal stock default (<c>sRGB-disable.cfg</c>) multiplies in gamma space and displays the product
/// directly. Godot always renders linear and re-encodes linear→sRGB on output, so this shader samples
/// albedo/lightmap <i>raw</i> (no <c>source_color</c>) and does the color management explicitly via the
/// <c>srgb_color</c> uniform: when set it decodes both inputs and lets Godot encode the linear product; when
/// clear it multiplies raw and pre-encodes the product with <c>srgb_to_linear</c> to cancel Godot's output
/// transform. <b>What is actually drawn since October 2026</b> is neither: the global
/// <c>world_gamma_space</c> (set by <c>NativeColour</c> for the native game and by a legacy session) selects
/// DarkPlaces' own arithmetic - the stored texel times the stored lightmap texel times two, written to a 3D
/// buffer that holds display values - which is what DarkPlaces shows with Xonotic's shipped configuration
/// (<c>vid_sRGB 0</c>: <c>sRGB-disable.cfg</c> is what <c>xonotic-client.cfg</c> executes). The per-material
/// <c>srgb_color</c> path is the earlier native look, reached with <c>r_darkplaces_colour 0</c>; an earlier
/// version of this comment said that look matched Xonotic's "sRGB-enable mode" - measured against
/// DarkPlaces it was 0.47 to 0.60 of its luminance and redder, whichever of Xonotic's two modes is meant
/// (planning/specs/legacy-compat.md sections 15 and 16).
/// Either way the x2 overbright matches DP's <c>render_lightmap_diffuse</c> (<c>gl_rmain.c</c>).</para>
///
/// <para><b>Deluxemaps.</b> On a deluxemapped map (q3map2 <c>-light -deluxe</c>) the lump also carries a
/// per-texel light-<i>direction</i> ("deluxe") page. We reproduce Darkplaces'
/// MODE_LIGHTDIRECTIONMAP_MODELSPACE combine (<c>shader_glsl.h</c> ~1605-1622, plus the SHADING combine
/// ~1657-1664): decode <c>lightnormal_modelspace = deluxe*2-1</c>, rotate it into the surface's tangent frame
/// via the per-surface basis (<c>dot(·, VectorS/T/R)</c>) and normalize, then apply both the angle-attenuation
/// undo <c>lightcolor *= 1/max(0.25, lightnormal.z)</c> AND the directional diffuse
/// <c>diffuse = possatdot(surfacenormal, lightnormal)</c>. With no per-surface normalmap the surface normal is
/// the flat tangentspace <c>(0,0,1)</c>, so the final color is <c>albedo * diffuse * lightcolor</c>. The tangent
/// frame is threaded through the BSP path: <see cref="VortexArena.Game.MapLoader"/> generates per-vertex tangents
/// for deluxemapped lightmap surfaces.</para>
///
/// <para><b>Vertex lighting.</b> A face with a negative lightmap index (q3map2 vertex-lit, e.g. <c>-3</c>) has no
/// lightmap page; DP renders it with the per-vertex RGB (MODE_VERTEXCOLOR). When <c>use_vertex_color</c> is set
/// the modulation comes from the interpolated mesh <c>COLOR</c> instead of the lightmap texture, sharing the same
/// overbright + color-space handling.</para>
///
/// The shader is a string constant (Godot builds <c>.gdshader</c> from source text at runtime via
/// <see cref="Shader.Code"/>); the factory methods wrap it in a ready-to-use <see cref="ShaderMaterial"/>.
/// </summary>
public static class LightmapShader
{
    /// <summary>Uniform name for the surface albedo (diffuse) texture, sampled with <c>UV</c>.</summary>
    public static readonly StringName AlbedoUniform = "albedo_tex";

    /// <summary>Uniform name for the lightmap page texture, sampled with <c>UV2</c>.</summary>
    public static readonly StringName LightmapUniform = "lightmap_tex";

    /// <summary>Uniform name for the deluxemap (light-direction) page texture, sampled with <c>UV2</c>.</summary>
    public static readonly StringName DeluxemapUniform = "deluxemap_tex";

    /// <summary>Uniform name for the flag that enables deluxemap directional re-modulation.</summary>
    public static readonly StringName UseDeluxemapUniform = "use_deluxemap";

    /// <summary>Uniform name for the flag that modulates by the per-vertex <c>COLOR</c> instead of a lightmap page.</summary>
    public static readonly StringName UseVertexColorUniform = "use_vertex_color";

    /// <summary>Uniform name for the sRGB color-space flag (Xonotic <c>vid_sRGB</c>/<c>mod_q3bsp_sRGBlightmaps</c>).</summary>
    public static readonly StringName SrgbColorUniform = "srgb_color";

    /// <summary>Global shader parameter: 0 (the native game) leaves every material on its own
    /// <c>srgb_color</c> setting; 1 (a legacy session) makes every world surface combine as DarkPlaces does with
    /// Xonotic's default <c>vid_sRGB 0</c> - texel times lightmap texel times two on the stored 8-bit values, the
    /// product shown as it is - with DarkPlaces' own specular term (see <c>dp_exact</c> in the source).
    /// 2 and 3 are a developer aid: 2 is DarkPlaces' <c>gl_lightmaps 1</c> (the lighting on a mid-grey texture,
    /// no glow, no gloss), 3 is <c>r_shadow_gloss 0</c>.</summary>
    public static readonly StringName GammaSpaceUniform = "world_gamma_space";

    /// <summary>Uniform name for a scalar lightmap brightness multiplier (Q3 overbright ≈ 2).</summary>
    public static readonly StringName LightmapScaleUniform = "lightmap_scale";

    /// <summary>Uniform name for the alpha-test cutoff (0 disables the test).</summary>
    public static readonly StringName AlphaCutoffUniform = "alpha_cutoff";

    /// <summary>Uniform name for the albedo-UV scale (Q3 <c>tcMod scale</c>; lightmap UV2 stays unscaled).</summary>
    public static readonly StringName AlbedoUvScaleUniform = "albedo_uv_scale";

    /// <summary>Uniform name for the self-illumination (<c>_glow</c>) texture, sampled with the albedo UV.</summary>
    public static readonly StringName GlowUniform = "glow_tex";

    /// <summary>Uniform name for the flag that enables the fullbright glow add.</summary>
    public static readonly StringName UseGlowUniform = "use_glow";

    /// <summary>Uniform name for the additive glow scale (DP <c>Color_Glow</c>; ~1).</summary>
    public static readonly StringName GlowScaleUniform = "glow_scale";

    /// <summary>Uniform name for the per-pixel surface-normal (<c>_norm</c>) companion texture.</summary>
    public static readonly StringName NormalUniform = "normal_tex";

    /// <summary>Uniform name for the flag that enables <c>_norm</c> per-pixel normal perturbation.</summary>
    public static readonly StringName UseNormalUniform = "use_normal";
    public static readonly StringName NormRgUniform = "norm_rg";

    /// <summary>Uniform name for the specular (<c>_gloss</c>) companion texture.</summary>
    public static readonly StringName GlossUniform = "gloss_tex";

    /// <summary>Uniform names for a shader's <c>dpglossexponentmod</c> / <c>dpglossintensitymod</c> (a legacy
    /// session's gloss; the native highlight does not read them).</summary>
    public static readonly StringName DpGlossExponentModUniform = "dp_gloss_exponent_mod";
    public static readonly StringName DpGlossIntensityModUniform = "dp_gloss_intensity_mod";

    /// <summary>Uniform name for the flag that enables the <c>_gloss</c> deluxe specular highlight.</summary>
    public static readonly StringName UseGlossUniform = "use_gloss";

    /// <summary>
    /// The GDShader source. Unshaded so the baked lightmap is the only lighting term; the albedo is
    /// modulated by <c>lightmap * scale</c> (or the per-vertex color when <c>use_vertex_color</c>). A 1×1
    /// white fallback is used when a texture is unbound. See the type doc for the color-space, deluxemap, and
    /// vertex-lighting details.
    /// </summary>
    public const string Code = @"// VortexArena lightmap-modulate shader (Q3 BSP world surfaces). Generated in C#.
shader_type spatial;
// NOT `unshaded`, so real-time point lights can reach the world (DP r_shadow_realtime_dlight): a muzzle
// flash or an explosion should light the wall next to it. The BAKED result is written to EMISSION, which no
// light touches, so a surface with no dynamic light near it renders byte-identically to the unshaded version;
// ALBEDO carries the plain diffuse purely so the light() below has something to modulate.
// `ambient_light_disabled` is essential: the scene's ambient/sky would otherwise be added on top of a
// lightmap that already accounts for all of it, washing every map out.
// (F3-A) `shadows_disabled` is GONE from render_mode. It was retired on 2026-08-02 because nothing cast:
// the light() below discards directional light (the lightmap already owns it) and no omni had shadows on,
// so the per-pixel PCF tap chain at 4x MSAA bought nothing. With the light budget able to grant shadows to
// dynamic lights that reasoning no longer holds - a muzzle flash SHOULD be occluded by the pillar between
// you and it. Godot only samples a shadow map for lights that actually have shadows enabled and reach the
// cluster, so with the default r_shadow_realtime_dlight_shadows 0 this costs nothing measurable; the cost
// arrives with the cvar, which is where it belongs. ATTENUATION in light() carries the shadow term, so the
// dynamic contribution below is shadowed for free once a light casts.
// (historical) the old note read: the world RECEIVES no shadow map — the light() below discards
// directional light entirely (the lightmap already owns it) and no omni here casts — so paying the
// per-pixel PCF tap chain at 4x MSAA bought nothing. If a future feature wants the world shadowed,
// remove this together with the r_sun_shadow default (they were retired as a pair).
render_mode cull_back, depth_draw_opaque, ambient_light_disabled;

// NOTE: albedo/lightmap are sampled RAW (no source_color) — the stock Xonotic config renders in gamma space.
// The srgb_color path decodes them explicitly. See LightmapShader's type doc.
uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D lightmap_tex : hint_default_white;
uniform sampler2D deluxemap_tex : hint_default_black;   // light-direction page (deluxemapped maps only).
uniform bool use_deluxemap = false;     // enable the deluxemap directional re-modulation.
uniform bool use_vertex_color = false;  // modulate by per-vertex COLOR (q3map2 vertex-lit faces) not a page.
uniform bool srgb_color = true;         // true = decode diffuse+lightmap from sRGB then multiply in linear
                                        // (Xonotic's recommended sRGB-enable mode); false = literal stock
                                        // sRGB-disable gamma-space combine (brighter, clips highlights).
uniform float lightmap_scale = 2.0;     // Q3 overbright: lightmaps are stored at half intensity.
uniform float alpha_cutoff = 0.0;       // >0 enables alpha test (masked surfaces).
uniform vec2 albedo_uv_scale = vec2(1.0, 1.0);   // Q3 tcMod scale on the albedo UV (lightmap UV2 stays raw).
uniform sampler2D glow_tex : hint_default_black; // self-illumination (_glow companion); added fullbright.
uniform bool use_glow = false;          // enable the glow add (a _glow page was found for this surface).
uniform float glow_scale = 1.0;         // DP Color_Glow (straight additive scale; ~1).
// Per-pixel normal/specular companions. Only applied on DELUXEMAPPED surfaces (they need a per-texel light
// DIRECTION to shade against); on a flat lightmap the surface normal has no light to dot with, so they go
// unused — matching DP, which only normal/gloss-maps the lightdirectionmap modes.
uniform sampler2D normal_tex : filter_linear_mipmap_anisotropic; // tangentspace _norm companion (raw; decoded *2-1).
uniform bool use_normal = false;        // a _norm page was found for this surface.
uniform bool norm_rg = false;           // BC5/RGTC two-channel normal: reconstruct z = sqrt(1 - x^2 - y^2).
uniform sampler2D gloss_tex : hint_default_black;               // _gloss specular map (DP Texture_Gloss).
uniform bool use_gloss = false;         // a _gloss page was found for this surface.
uniform float specular_power = 32.0;    // DP r_shadow_glossexponent (× glosstex.a per-texel in the shader).
uniform float specular_scale = 0.15;    // DP Color_Specular (gloss intensity) — a subtle glint; 0 disables.
// A legacy session's gloss (dp_exact below): the shader's dpglossexponentmod and dpglossintensitymod, which
// DarkPlaces multiplies into r_shadow_glossexponent and r_shadow_glossintensity (gl_rmain.c: specularpower *=
// specularpowermod, specularscale *= specularscalemod). Most of Xonotic's wall shaders say 4 and 1.5.
uniform float dp_gloss_exponent_mod = 1.0;
uniform float dp_gloss_intensity_mod = 1.0;
// dpreflectcube (shader_glsl.h USEREFLECTCUBE): the texture's _reflect mask times a cube map, sampled along the
// reflected view vector, ADDED to the texel before it is lit. DarkPlaces' arithmetic only (dp_exact below).
uniform sampler2D reflect_mask : hint_default_black, filter_linear_mipmap_anisotropic;
uniform samplerCube reflect_cube : filter_linear_mipmap;
uniform bool use_reflect = false;

// Dynamic whole-map colour tint (VortexArena.Game.WorldTint). A GLOBAL shader parameter so one
// RenderingServer.GlobalShaderParameterSet re-tints every world surface at once; the strength is folded into the
// multiplier on the C# side, so this is a trivial final multiply and the registered default (1,1,1) is identity.
global uniform vec3 map_tint;

// Real-time dynamic-light strength on world surfaces (DP r_shadow_realtime_dlight). Global so one
// RenderingServer.GlobalShaderParameterSet reaches every world surface; 0 restores the pre-dlight look
// exactly, since the baked term lives in EMISSION and is unaffected either way.
global uniform float world_dlight;
// (F4) DP r_shadow_realtime_world_lightmaps: while realtime WORLD lighting is on, the baked lightmaps are
// re-admitted at this brightness (DP default 0 = fully replaced by the authored lights; its own help text
// suggests 0.5 for a tenebrae-like look). 1 when the mode is off, so the normal path is untouched.
global uniform float world_lightmap_scale;
// r_glsl_deluxemapping (F9 wiring): the live gate over the per-surface use_deluxemap flag. The flag says
// ""this surface HAS a deluxe page""; this global says ""the player wants the directional re-modulation"".
// Both must hold - so flipping the cvar takes effect instantly on every deluxemapped surface, with no
// material rebuild, exactly like the tints.
global uniform float deluxe_enabled;
// mod_q3bsp_nolightmaps (F9 wiring): 1 = render the world FULLBRIGHT (DP loads white lightmaps; here the
// lightmap term collapses to 1 after all scaling, which displays the plain albedo). The menu's
// ""Use lightmaps"" checkbox is this cvar INVERTED - see the dialog polarity note.
global uniform float world_nolightmaps;
// 0 = the native game (each material's own srgb_color). 1 = a legacy session: DarkPlaces' arithmetic with
// Xonotic's default vid_sRGB 0 - nothing is converted, the stored values are multiplied and the product is the
// pixel (gl_rmain.c R_UpdateCurrentTexture: render_lightmap_diffuse = colormod * lightmapintensity * 2;
// shader_glsl.h MODE_LIGHTMAP / MODE_LIGHTDIRECTIONMAP_MODELSPACE). 2, 3: developer aid, see GammaSpaceUniform.
global uniform float world_gamma_space;
// 1 while a legacy session keeps DISPLAY values in the 3D buffer (VortexArena.Game.Client.DisplayFramebuffer):
// the combine below is then written as it is, and blending, fog and the dynamic lights act on it as they do
// in DarkPlaces' frame buffer. 0 = linear light, the native game.
global uniform float dp_framebuffer;
// r_shadow_usenormalmap (DarkPlaces' arithmetic): 1 = a realtime light is shaded by N.L on the normal-mapped
// normal and has a specular term; 0 = its diffuse scale joins the ambient one and nothing depends on N.L
// (r_shadow.c R_Shadow_RenderLighting).
global uniform float dp_usenormalmap;
// r_shadows (DarkPlaces' model shadows): r_shadows_darken while the pass is on, 0 while it is off. The scene's
// directional light is then the shadow map of the models thrown along r_shadows_throwdirection, and a
// shadowed pixel keeps (1 - darken) of its lit colour (shader_glsl.h USESHADOWMAPORTHO).
global uniform float dp_model_shadow;

varying vec4 v_light_gloss;   // gloss.rgb * intensity, exponent: the specular term of a realtime light
varying vec3 v_shadowable;    // the lit colour before glow: what a model shadow darkens

// Per-surface tangent frame (DP VectorS/T/R = tangent/binormal/normal), captured in modelspace so the
// modelspace deluxe light direction can be rotated into it without a view-space mismatch.
varying vec3 v_tangent;
varying vec3 v_binormal;
varying vec3 v_normal;
varying vec4 v_color;   // per-vertex color (vertex-lit faces); white when the mesh has no COLOR array.
varying vec3 v_eye_model; // camera minus vertex in modelspace (deluxe specular half-angle; normalized in frag).

// Accurate piecewise sRGB transfer functions — the same curve Godot uses for its framebuffer encode, so
// srgb_to_linear here exactly cancels Godot's linear->sRGB output transform in the default gamma-space mode.
vec3 srgb_to_linear(vec3 c) {
    return mix(c * (1.0 / 12.92), pow((c + 0.055) * (1.0 / 1.055), vec3(2.4)), step(vec3(0.04045), c));
}
@@DPLIGHT@@
void vertex() {
    // In vertex() the basis is modelspace (pre view transform) — the same space the deluxemap encodes the
    // light direction in. Every lightmapped surface carries a TANGENT array (MapLoader generates one).
    v_tangent = TANGENT;
    v_binormal = BINORMAL;
    v_normal = NORMAL;
    v_color = COLOR;
    // Eye vector in modelspace for the deluxe specular half-angle. Per-vertex via the model inverse (the BSP
    // world matrix is identity, but stay correct under any transform); normalized in fragment.
    v_eye_model = (inverse(MODEL_MATRIX) * INV_VIEW_MATRIX[3]).xyz - VERTEX;
}

void fragment() {
    vec4 base = texture(albedo_tex, UV * albedo_uv_scale);
    vec3 albedo = base.rgb;
    // Lighting term: the per-vertex color for q3map2 vertex-lit faces, otherwise the baked lightmap page.
    vec3 lm = use_vertex_color ? v_color.rgb : texture(lightmap_tex, UV2).rgb;
    // Self-illumination map (aligned with the diffuse UV); black/zero when this surface has no _glow page.
    vec3 glow = use_glow ? texture(glow_tex, UV * albedo_uv_scale).rgb : vec3(0.0);

    bool dp_exact = world_gamma_space > 0.5;
    bool linear_combine = srgb_color && !dp_exact;
    if (world_gamma_space > 1.5 && world_gamma_space < 2.5) {
        // gl_lightmaps 1 (gl_rmain.c: basetexture = r_texture_grey128, no glow, no gloss, a flat normal map).
        albedo = vec3(0.5);
        glow = vec3(0.0);
    }
    if (linear_combine) {
        // sRGB-enable mode: decode diffuse + lightmap (and vertex colors) + glow to linear before combining.
        albedo = srgb_to_linear(albedo);
        lm = srgb_to_linear(lm);
        glow = srgb_to_linear(glow);
    }
    // The normal-mapped normal in view space (the normal map's T runs along decreasing v), for the reflection
    // cube and the realtime lights; and the gloss texel, for the lightmap's and the realtime lights' specular.
    // (One sample of the normal map serves the lightmap's directional term below as well.)
    vec3 rn = normalize(NORMAL);
    vec3 rts = vec3(0.0, 0.0, 1.0);   // the normal map's texel, tangent space, unit length
    if (use_normal) {
        vec3 rt = texture(normal_tex, UV * albedo_uv_scale).xyz * 2.0 - 1.0;
        if (norm_rg) { rt.z = sqrt(max(0.0, 1.0 - dot(rt.xy, rt.xy))); }
        rts = normalize(rt);
        if (dp_exact) { rn = normalize(TANGENT * rt.x - BINORMAL * rt.y + NORMAL * rt.z); }
    }
    vec4 gtex = use_gloss ? texture(gloss_tex, UV * albedo_uv_scale) : vec4(0.0);
    v_light_gloss = (use_gloss && dp_exact && world_gamma_space < 1.5)
        ? vec4(gtex.rgb * dp_gloss_intensity_mod, 1.0 + (32.0 * dp_gloss_exponent_mod * 0.25 - 1.0) * gtex.a) : vec4(0.0, 0.0, 0.0, 1.0);
    if (use_reflect && dp_exact) {
        //   TangentReflectVector = reflect(-EyeVector, surfacenormal); diffusetex += reflectmask * reflectcube
        // The cube map is in Quake axes (x, y, z) = Godot (x, -z, y).
        vec3 rw = (INV_VIEW_MATRIX * vec4(reflect(-VIEW, rn), 0.0)).xyz;
        albedo += texture(reflect_mask, UV * albedo_uv_scale).rgb * texture(reflect_cube, vec3(rw.x, -rw.z, rw.y)).rgb;
    }

    vec3 spec_accum = vec3(0.0);   // deluxe specular highlight; added (overbright-scaled) into combined below.
    if (use_deluxemap && deluxe_enabled > 0.5) {
        // DP shader_glsl.h MODE_LIGHTDIRECTIONMAP_MODELSPACE (~1605-1622) + the SHADING combine (~1657-1664).
        // Decode the modelspace light direction. q3map2 stores it in Quake space; rotate to Godot space so it
        // matches the Godot-space tangent frame (the rotation is orthogonal, so it preserves the dots below).
        vec3 d = texture(deluxemap_tex, UV2).rgb * 2.0 - 1.0;
        vec3 lightnormal_modelspace = vec3(d.x, d.z, -d.y);   // Coords.ToGodot
        vec3 vs = normalize(v_tangent);
        vec3 vt = normalize(v_binormal);
        // DarkPlaces' tangent frame has T along DECREASING v (model_shared.c
        // Mod_BuildTextureVectorsFromNormals: sdir = +dP/du and tdir = -dP/dv for every winding), which is the
        // frame Xonotic's normal maps are drawn in; this mesh's BINORMAL runs along increasing v. A legacy
        // session takes DarkPlaces' sign, so a bump is lit from the side the light is on. (Only a surface with
        // a normal map can tell: without one the term below is the same for either sign.)
        if (dp_exact) { vt = -vt; }
        vec3 vr = normalize(v_normal);
        vec3 lightnormal;
        lightnormal.x = dot(lightnormal_modelspace, vs);
        lightnormal.y = dot(lightnormal_modelspace, vt);
        lightnormal.z = dot(lightnormal_modelspace, vr);
        lightnormal = normalize(lightnormal);
        // Per-pixel surface normal from the _norm companion (tangentspace), else the flat face normal (0,0,1).
        // DP MODE_LIGHTDIRECTIONMAP_TANGENTSPACE: the directional diffuse is dot(surfacenormal, lightnormal),
        // which reduces to clamp(lightnormal.z,0,1) when there is no normalmap (sn = (0,0,1)) — the old path.
        // BC5/RGTC normals carry only X/Y (industry-standard normal compression — blue samples as 0);
        // reconstruct Z on the unit hemisphere. Full-channel textures keep the direct decode.
        vec3 sn = vec3(0.0, 0.0, 1.0);
        if (use_normal && !(world_gamma_space > 1.5 && world_gamma_space < 2.5)) {
            sn = rts;
        }
        float diffuse = clamp(dot(sn, lightnormal), 0.0, 1.0);
        // lightcolor = lightmap / max(0.25, lightnormal.z)  (angle-attenuation undo); reused by the specular.
        lm *= 1.0 / max(0.25, lightnormal.z);
        // Specular: Blinn half-vector between the light dir and the tangentspace eye (DP shader_glsl.h ~1660:
        // specular = pow(dot(N,H), SpecularPower * glosstex.a); added as glosstex.rgb * Color_Specular * specular
        // * lightcolor). The per-texel ALPHA modulates the exponent (highlight tightness) and Color_Specular
        // (specular_scale, low) keeps it a subtle glint rather than the broad sheen a fixed low exponent gives.
        if (use_gloss && dp_exact) {
            // DarkPlaces as Xonotic configures it (r_shadow_gloss 1, r_shadow_glossintensity 1,
            // r_shadow_glossexponent 32, r_shadow_glossexact 1): SHADESPECULAR with USEEXACTSPECULARMATH,
            //   specular = pow(sat(dot(reflect(lightnormal, surfacenormal), -eyenormal)), 1 + SpecularPower * gloss.a)
            // where SpecularPower = 32 * dpglossexponentmod * 0.25 - 1 (gl_rmain.c:1930), added as gloss.rgb *
            // Color_Specular * specular * lightcolor with Color_Specular = 2 * dpglossintensitymod (the same
            // overbright two as the diffuse).
            if (world_gamma_space < 1.5) {
                vec3 eye_ts = normalize(vec3(dot(v_eye_model, vs), dot(v_eye_model, vt), dot(v_eye_model, vr)));
                float spec = pow(clamp(dot(reflect(lightnormal, sn), -eye_ts), 0.0, 1.0), 1.0 + (32.0 * dp_gloss_exponent_mod * 0.25 - 1.0) * gtex.a);
                spec_accum = lm * spec * gtex.rgb * dp_gloss_intensity_mod;
            }
        } else if (use_gloss) {
            vec3 eye_ts = normalize(vec3(dot(v_eye_model, vs), dot(v_eye_model, vt), dot(v_eye_model, vr)));
            vec3 halfdir = normalize(lightnormal + eye_ts);
            float spec = pow(clamp(dot(sn, halfdir), 0.0, 1.0), specular_power * gtex.a);
            spec_accum = lm * spec * gtex.rgb * specular_scale;
        }
        lm *= diffuse;   // SHADEDIFFUSE: re-modulate the light by the (possibly normal-mapped) diffuse term.
    }

    lm *= lightmap_scale * world_lightmap_scale;
    // mod_q3bsp_nolightmaps: collapse the whole lighting term to 1 -> the surface displays its raw albedo.
    lm = mix(lm, vec3(1.0), world_nolightmaps);
    if (dp_exact) { spec_accum *= 1.0 - world_nolightmaps; }   // DarkPlaces' fullbright has no specular term
    // Self-illumination (DP shader_glsl.h: color.rgb += Texture_Glow * Color_Glow): added on top of the lit
    // diffuse and NOT modulated by the lightmap, so light fixtures glow at full intensity regardless of how
    // lit their own luxels are. Without this, lightmapped lights render as a dim diffuse×lightmap and look dark.
    vec3 combined = albedo * lm + spec_accum * lightmap_scale * world_lightmap_scale + glow * glow_scale;
    combined *= map_tint;   // dynamic whole-map tint (identity (1,1,1) when no tint is active).
    v_shadowable = clamp(combined - glow * glow_scale * map_tint, vec3(0.0), vec3(1.0));
    // A realtime light is shaded on the normal-mapped normal, as DarkPlaces' light pass is. (The baked term
    // above is already computed; the engine's ambient light is off for this shader.)
    if (dp_framebuffer > 0.5 && dp_usenormalmap > 0.5) { NORMAL = rn; }
    // In sRGB mode combined is linear (let Godot encode it). In the default gamma-space mode it's the
    // display-ready value, so pre-encode it to linear to cancel Godot's linear->sRGB output transform.
    // The baked result goes to EMISSION, not ALBEDO: emission is not affected by lighting, so the static
    // lightmap look survives exactly as-is and dynamic lights ADD to it rather than replacing it.
    EMISSION = linear_combine ? combined
        : (dp_framebuffer > 0.5 ? clamp(combined, vec3(0.0), vec3(1.0)) : srgb_to_linear(combined));
    // Linear diffuse for the dynamic-light term below. Tinted to match, so a dlight on a tinted map picks up
    // the tint too.
    ALBEDO = (linear_combine || dp_framebuffer > 0.5 ? albedo : srgb_to_linear(albedo)) * map_tint;
    // World surfaces are OPAQUE: do NOT write ALPHA. Writing it pushes the material into Godot's transparent
    // pass, which is depth-sorted per-object and doesn't occlude — i.e. you'd see through walls. Masked
    // surfaces (grates/foliage) instead alpha-TEST via discard below, which stays in the opaque pass.
    if (alpha_cutoff > 0.0 && base.a < alpha_cutoff) {
        discard;
    }
}

// DP's realtime-dlight model: the baked lightmap is the static term, and dynamic lights add a simple
// Lambert diffuse on top. DIRECTIONAL lights are deliberately ignored — the scene carries a generic sun for
// lighting player models, and letting it reach the world would add a second, constant light term to surfaces
// whose lighting is already fully baked, flattening every map.
void light() {
    // Guarded rather than early-returned: Godot rejects `return` inside light().
    if (!LIGHT_IS_DIRECTIONAL) {
        float ndotl = clamp(dot(normalize(NORMAL), normalize(LIGHT)), 0.0, 1.0);
        if (dp_framebuffer > 0.5) {
            // DarkPlaces' dynamic light pass (shader_glsl.h MODE_LIGHTSOURCE, drawn GL_ONE GL_ONE):
            //   fb += diffusetex * lightcolor * attenuation * sat(dot(N, L))
            // The engine multiplies what is gathered here by ALBEDO (the texel) and adds it to the buffer,
            // which holds display values - so this is that sum. A legacy session's lights have no distance
            // falloff of the engine's own beyond its range window (1 - (d/r)^4)^2, from which d/r is taken
            // back and DarkPlaces' table is evaluated: (1 - d/r) * 2 / (1 + (d/r)^2), at most 1
            // (r_shadow.c R_Shadow_MakeTextures_SamplePoint). LIGHT_COLOR carries the engine's factor of pi.
            // The light's ambient, diffuse and specular scales (an .rtlights line's last fields) come packed
            // in its specular parameter; with r_shadow_usenormalmap 0 the diffuse joins the ambient.
            float att = dp_light_att(ATTENUATION);
            vec3 scales = dp_light_scales(SPECULAR_AMOUNT);
            if (dp_usenormalmap < 0.5) { scales = vec3(scales.x + scales.y, 0.0, 0.0); }
            DIFFUSE_LIGHT += LIGHT_COLOR * (att * (scales.x + scales.y * ndotl) * world_dlight * 0.31830989);
            if (scales.z > 0.0 && v_light_gloss.a > 1.0) {
                //   specular = pow(sat(dot(reflect(lightnormal, surfacenormal), -eyenormal)), 1 + SpecularPower * gloss.a)
                float spec = pow(clamp(dot(reflect(-normalize(LIGHT), normalize(NORMAL)), normalize(VIEW)), 0.0, 1.0), v_light_gloss.a);
                SPECULAR_LIGHT += LIGHT_COLOR * v_light_gloss.rgb * (att * scales.z * spec * world_dlight * 0.31830989);
            }
        } else {
            DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * ATTENUATION * ndotl * world_dlight;
        }
    } else if (dp_framebuffer > 0.5 && dp_model_shadow > 0.0) {
        // r_shadows: the directional light is only a shadow map of the models. ATTENUATION is the fraction
        // of unshadowed samples; a shadowed pixel keeps (1 - r_shadows_darken) of its lit colour.
        SPECULAR_LIGHT -= v_shadowable * (dp_model_shadow * (1.0 - clamp(ATTENUATION, 0.0, 1.0)));
    }
}
";

    // The shader resource is immutable text, so a single shared instance is reused across every
    // lightmap material (the per-surface textures live on the ShaderMaterial, not the Shader).
    private static Shader? _shared;
    private static Shader? _sharedMasked;
    private static Shader? _sharedTranslucent;

    /// <summary>The shared opaque <see cref="Shader"/> instance compiled from <see cref="Code"/>.</summary>
    private static readonly object _sharedGate = new();

    /// <summary>Shared OPAQUE instance — <see cref="Code"/> with the alpha-test block textually removed
    /// (2026-08-02): a shader that *contains* `discard` is classified alpha-discard by Godot even when the
    /// uniform keeps it dead, which forces the depth prepass to run the fragment shader for ALL world
    /// geometry and weakens early-Z in the main pass. The overwhelming majority of world surfaces have
    /// `alpha_cutoff == 0`, so they now compile a discard-free program; masked surfaces (grates/foliage)
    /// keep it via <see cref="MaskedShader"/>. Locked for the reason spelled out in
    /// <c>PlayerSkinShader.Shader</c>.</summary>
    public static Shader Shader
    {
        get
        {
            lock (_sharedGate)
                return _shared ??= new Shader { Code = Final(OpaqueCode) };
        }
    }

    /// <summary>The masked (alpha-tested) variant — the original <see cref="Code"/>, whose discard block
    /// keeps grates/foliage in the opaque pass. Chosen by <see cref="MakeMaterial"/> when
    /// <c>alphaCutoff &gt; 0</c>.</summary>
    public static Shader MaskedShader
    {
        get
        {
            lock (_sharedGate)
                return _sharedMasked ??= new Shader { Code = Final(Code) };
        }
    }

    /// <summary>The opaque source: <see cref="Code"/> minus the alpha-test block (see <see cref="Shader"/>).
    /// Derived textually, like <see cref="TranslucentCode"/>, so the color math can never drift.</summary>
    private static readonly string OpaqueCode = Code.Replace(
        "    if (alpha_cutoff > 0.0 && base.a < alpha_cutoff) {\n        discard;\n    }\n", "");

    /// <summary>
    /// The translucent variant, for alpha-blended world surfaces (Q3 <c>blendFunc blend</c> over a lightmap —
    /// e.g. <c>trak5x/misc-glass</c>). Identical albedo×lightmap colour math to the opaque <see cref="Shader"/>,
    /// but it writes <c>ALPHA = base.a</c> so Godot renders it in the transparent pass and the diffuse texture's
    /// alpha channel drives the see-through. Built once by injecting the alpha write into <see cref="Code"/> so
    /// the colour-space / deluxe / glow math can never drift between the two variants.
    /// </summary>
    public static Shader TranslucentShader
    {
        get
        {
            lock (_sharedGate)
                return _sharedTranslucent ??= new Shader { Code = Final(TranslucentCode) };
        }
    }

    /// <summary>The translucent source: the opaque <see cref="Code"/> with a single <c>ALPHA = base.a</c> write
    /// added (the opaque variant deliberately leaves ALPHA unwritten to stay in the opaque pass).</summary>
    private static readonly string TranslucentCode = MakeTranslucent(Code);

    /// <summary>The source as compiled: <see cref="Code"/> with the DarkPlaces light helpers
    /// (<c>DpLightModel.ShaderFunctions</c>) in place of their marker.</summary>
    internal static string Final(string code) => code.Replace("@@DPLIGHT@@", VortexArena.Formats.Lighting.DpLightModel.ShaderFunctions);

    /// <summary>The line of <see cref="Code"/> after which the translucent variant writes ALPHA. A constant, and
    /// checked: the replacement used to name a line that a later edit had rewritten, so the variant silently
    /// stayed opaque and lightmapped glass was a solid wall.</summary>
    internal const string TranslucentAnchor = "    ALBEDO = (linear_combine || dp_framebuffer > 0.5 ? albedo : srgb_to_linear(albedo)) * map_tint;\n";

    internal static string MakeTranslucent(string code)
    {
        if (!code.Contains(TranslucentAnchor, System.StringComparison.Ordinal))
            throw new System.InvalidOperationException("LightmapShader: the translucent variant's anchor line is gone from Code.");
        // A blended surface is not alpha-tested: the discard block goes.
        return code.Replace(TranslucentAnchor, TranslucentAnchor +
            "    ALPHA = base.a; // translucent variant (Q3 blendFunc blend): diffuse alpha drives the see-through.\n")
            .Replace("    if (alpha_cutoff > 0.0 && base.a < alpha_cutoff) {\n        discard;\n    }\n", "");
    }

    /// <summary>True if <paramref name="shader"/> is one of the lightmap shader instances (opaque or
    /// translucent). The BSP load tally uses this to recognise a surface that came back on the lightmap path
    /// regardless of transparency variant, so a translucent glass surface is not mistaken for a lightmap-bind
    /// miss (the regression signature the tally guards).</summary>
    public static bool IsLightmapShader(Shader? shader)
        => shader != null && (shader == _shared || shader == _sharedMasked || shader == _sharedTranslucent);

    /// <summary>
    /// Build a <see cref="ShaderMaterial"/> that modulates <paramref name="albedo"/> by
    /// <paramref name="lightmap"/> (sampled via UV2). Either texture may be null; the shader falls back
    /// to white for an unbound sampler. <paramref name="lightmapScale"/> defaults to the Q3 overbright
    /// factor of 2.
    ///
    /// <paramref name="deluxemap"/> (optional) is the matching light-direction page on a deluxemapped map;
    /// when supplied, the lightmap is re-modulated by <c>1/max(0.25, lightnormal.z)</c> AND the directional
    /// diffuse <c>clamp(lightnormal.z, 0, 1)</c> (DP MODE_LIGHTDIRECTIONMAP_MODELSPACE + SHADEDIFFUSE). This
    /// path needs the mesh to carry a TANGENT array — <see cref="VortexArena.Game.MapLoader"/> generates one for
    /// deluxemapped lightmap surfaces. <paramref name="albedoUvScale"/> applies a static Q3 <c>tcMod scale</c>
    /// to the albedo UV only (default <c>(1,1)</c> = no scale).
    ///
    /// <paramref name="translucent"/> selects the <see cref="TranslucentShader"/> variant for alpha-blended
    /// surfaces (Q3 <c>blendFunc blend</c>, e.g. glass): the diffuse alpha channel drives the see-through and
    /// the surface renders in the transparent pass. Default <c>false</c> (opaque world surface).
    /// </summary>
    public static ShaderMaterial MakeMaterial(
        Texture2D? albedo, Texture2D? lightmap, float lightmapScale = 2.0f,
        Texture2D? deluxemap = null, Vector2? albedoUvScale = null, float alphaCutoff = 0.0f,
        Texture2D? glow = null, float glowScale = 1.0f, bool translucent = false,
        Texture2D? normal = null, Texture2D? gloss = null, Texture2D? reflectMask = null, Cubemap? reflectCube = null)
    {
        // Three-way program pick (2026-08-02): translucent → alpha-blend variant; alpha-tested (grates,
        // foliage) → the masked variant that carries `discard`; everything else → the discard-free opaque
        // program, which is what lets Godot run a position-only depth prepass for the world.
        var mat = new ShaderMaterial
        {
            Shader = translucent ? TranslucentShader : alphaCutoff > 0f ? MaskedShader : Shader,
        };
        if (albedo != null)
            mat.SetShaderParameter(AlbedoUniform, albedo);
        if (lightmap != null)
            mat.SetShaderParameter(LightmapUniform, lightmap);
        if (deluxemap != null)
        {
            mat.SetShaderParameter(DeluxemapUniform, deluxemap);
            mat.SetShaderParameter(UseDeluxemapUniform, true);
        }
        if (glow != null)
        {
            mat.SetShaderParameter(GlowUniform, glow);
            mat.SetShaderParameter(UseGlowUniform, true);
            mat.SetShaderParameter(GlowScaleUniform, glowScale);
        }
        // _norm/_gloss only shade on a deluxemapped surface (the shader gates them on use_deluxemap), but bind
        // them whenever present — a non-deluxe map just leaves them dormant, mirroring the always-present tangent
        // frame. The per-pixel normal perturbs the directional diffuse; the gloss masks a Blinn specular highlight.
        if (normal != null)
        {
            mat.SetShaderParameter(NormalUniform, normal);
            mat.SetShaderParameter(UseNormalUniform, true);
            if (AssetSystem.IsRgTexture(normal))
                mat.SetShaderParameter(NormRgUniform, true); // BC5 two-channel — shader reconstructs Z
        }
        if (gloss != null)
        {
            mat.SetShaderParameter(GlossUniform, gloss);
            mat.SetShaderParameter(UseGlossUniform, true);
        }
        if (reflectMask != null && reflectCube != null)
        {
            mat.SetShaderParameter("reflect_mask", reflectMask);
            mat.SetShaderParameter("reflect_cube", reflectCube);
            mat.SetShaderParameter("use_reflect", true);
        }
        mat.SetShaderParameter(LightmapScaleUniform, lightmapScale);
        mat.SetShaderParameter(AlphaCutoffUniform, alphaCutoff);
        mat.SetShaderParameter(AlbedoUvScaleUniform, albedoUvScale ?? Vector2.One);
        return mat;
    }

    /// <summary>
    /// Build a vertex-lit material: <paramref name="albedo"/> modulated by the interpolated mesh
    /// <c>COLOR</c> (the per-vertex RGB q3map2 bakes for surfaces with a negative lightmap index). Mirrors
    /// DP's MODE_VERTEXCOLOR — unshaded, with the same ×<paramref name="lightmapScale"/> overbright and
    /// color-space handling as the lightmap path. The mesh must carry a <c>Color</c> array (white otherwise).
    /// </summary>
    public static ShaderMaterial MakeVertexLitMaterial(Texture2D? albedo, float lightmapScale = 2.0f)
    {
        var mat = new ShaderMaterial { Shader = Shader };
        if (albedo != null)
            mat.SetShaderParameter(AlbedoUniform, albedo);
        mat.SetShaderParameter(UseVertexColorUniform, true);
        mat.SetShaderParameter(LightmapScaleUniform, lightmapScale);
        mat.SetShaderParameter(AlphaCutoffUniform, 0.0f);
        return mat;
    }
}
