using Godot;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The lightmap-modulate spatial shader used by IBSP world geometry.
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
/// <c>srgb_color</c> uniform: when set (the default) it decodes both inputs and lets Godot encode the linear
/// product (sRGB-enable); when clear it multiplies raw and pre-encodes the product with <c>srgb_to_linear</c> to
/// cancel Godot's output transform, reproducing DP's gamma-space displayed pixel. The default matches Xonotic's
/// recommended mode (and looks balanced; the gamma-space mode clips highlights through Godot's linear pipeline).
/// Either way the ×2 overbright matches DP's <c>render_lightmap_diffuse</c> (<c>gl_rmain.c</c>).</para>
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

    /// <summary>Uniform name for the specular (<c>_gloss</c>) companion texture.</summary>
    public static readonly StringName GlossUniform = "gloss_tex";

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
uniform sampler2D gloss_tex : hint_default_black;               // _gloss specular map (DP Texture_Gloss).
uniform bool use_gloss = false;         // a _gloss page was found for this surface.
uniform float specular_power = 32.0;    // DP r_shadow_glossexponent (× glosstex.a per-texel in the shader).
uniform float specular_scale = 0.15;    // DP Color_Specular (gloss intensity) — a subtle glint; 0 disables.

// Dynamic whole-map colour tint (VortexArena.Game.WorldTint). A GLOBAL shader parameter so one
// RenderingServer.GlobalShaderParameterSet re-tints every world surface at once; the strength is folded into the
// multiplier on the C# side, so this is a trivial final multiply and the registered default (1,1,1) is identity.
global uniform vec3 map_tint;

// Real-time dynamic-light strength on world surfaces (DP r_shadow_realtime_dlight). Global so one
// RenderingServer.GlobalShaderParameterSet reaches every world surface; 0 restores the pre-dlight look
// exactly, since the baked term lives in EMISSION and is unaffected either way.
global uniform float world_dlight;

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

    if (srgb_color) {
        // sRGB-enable mode: decode diffuse + lightmap (and vertex colors) + glow to linear before combining.
        albedo = srgb_to_linear(albedo);
        lm = srgb_to_linear(lm);
        glow = srgb_to_linear(glow);
    }

    vec3 spec_accum = vec3(0.0);   // deluxe specular highlight; added (overbright-scaled) into combined below.
    if (use_deluxemap) {
        // DP shader_glsl.h MODE_LIGHTDIRECTIONMAP_MODELSPACE (~1605-1622) + the SHADING combine (~1657-1664).
        // Decode the modelspace light direction. q3map2 stores it in Quake space; rotate to Godot space so it
        // matches the Godot-space tangent frame (the rotation is orthogonal, so it preserves the dots below).
        vec3 d = texture(deluxemap_tex, UV2).rgb * 2.0 - 1.0;
        vec3 lightnormal_modelspace = vec3(d.x, d.z, -d.y);   // Coords.ToGodot
        vec3 vs = normalize(v_tangent);
        vec3 vt = normalize(v_binormal);
        vec3 vr = normalize(v_normal);
        vec3 lightnormal;
        lightnormal.x = dot(lightnormal_modelspace, vs);
        lightnormal.y = dot(lightnormal_modelspace, vt);
        lightnormal.z = dot(lightnormal_modelspace, vr);
        lightnormal = normalize(lightnormal);
        // Per-pixel surface normal from the _norm companion (tangentspace), else the flat face normal (0,0,1).
        // DP MODE_LIGHTDIRECTIONMAP_TANGENTSPACE: the directional diffuse is dot(surfacenormal, lightnormal),
        // which reduces to clamp(lightnormal.z,0,1) when there is no normalmap (sn = (0,0,1)) — the old path.
        vec3 sn = use_normal
            ? normalize(texture(normal_tex, UV * albedo_uv_scale).xyz * 2.0 - 1.0)
            : vec3(0.0, 0.0, 1.0);
        float diffuse = clamp(dot(sn, lightnormal), 0.0, 1.0);
        // lightcolor = lightmap / max(0.25, lightnormal.z)  (angle-attenuation undo); reused by the specular.
        lm *= 1.0 / max(0.25, lightnormal.z);
        // Specular: Blinn half-vector between the light dir and the tangentspace eye (DP shader_glsl.h ~1660:
        // specular = pow(dot(N,H), SpecularPower * glosstex.a); added as glosstex.rgb * Color_Specular * specular
        // * lightcolor). The per-texel ALPHA modulates the exponent (highlight tightness) and Color_Specular
        // (specular_scale, low) keeps it a subtle glint rather than the broad sheen a fixed low exponent gives.
        if (use_gloss) {
            vec3 eye_ts = normalize(vec3(dot(v_eye_model, vs), dot(v_eye_model, vt), dot(v_eye_model, vr)));
            vec3 halfdir = normalize(lightnormal + eye_ts);
            vec4 gtex = texture(gloss_tex, UV * albedo_uv_scale);
            float spec = pow(clamp(dot(sn, halfdir), 0.0, 1.0), specular_power * gtex.a);
            spec_accum = lm * spec * gtex.rgb * specular_scale;
        }
        lm *= diffuse;   // SHADEDIFFUSE: re-modulate the light by the (possibly normal-mapped) diffuse term.
    }

    lm *= lightmap_scale;
    // Self-illumination (DP shader_glsl.h: color.rgb += Texture_Glow * Color_Glow): added on top of the lit
    // diffuse and NOT modulated by the lightmap, so light fixtures glow at full intensity regardless of how
    // lit their own luxels are. Without this, lightmapped lights render as a dim diffuse×lightmap and look dark.
    vec3 combined = albedo * lm + spec_accum * lightmap_scale + glow * glow_scale;
    combined *= map_tint;   // dynamic whole-map tint (identity (1,1,1) when no tint is active).
    // In sRGB mode combined is linear (let Godot encode it). In the default gamma-space mode it's the
    // display-ready value, so pre-encode it to linear to cancel Godot's linear->sRGB output transform.
    // The baked result goes to EMISSION, not ALBEDO: emission is not affected by lighting, so the static
    // lightmap look survives exactly as-is and dynamic lights ADD to it rather than replacing it.
    EMISSION = srgb_color ? combined : srgb_to_linear(combined);
    // Linear diffuse for the dynamic-light term below. Tinted to match, so a dlight on a tinted map picks up
    // the tint too.
    ALBEDO = (srgb_color ? albedo : srgb_to_linear(albedo)) * map_tint;
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
        DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * ATTENUATION * ndotl * world_dlight;
    }
}
";

    // The shader resource is immutable text, so a single shared instance is reused across every
    // lightmap material (the per-surface textures live on the ShaderMaterial, not the Shader).
    private static Shader? _shared;
    private static Shader? _sharedTranslucent;

    /// <summary>The shared opaque <see cref="Shader"/> instance compiled from <see cref="Code"/>.</summary>
    public static Shader Shader => _shared ??= new Shader { Code = Code };

    /// <summary>
    /// The translucent variant, for alpha-blended world surfaces (Q3 <c>blendFunc blend</c> over a lightmap —
    /// e.g. <c>trak5x/misc-glass</c>). Identical albedo×lightmap colour math to the opaque <see cref="Shader"/>,
    /// but it writes <c>ALPHA = base.a</c> so Godot renders it in the transparent pass and the diffuse texture's
    /// alpha channel drives the see-through. Built once by injecting the alpha write into <see cref="Code"/> so
    /// the colour-space / deluxe / glow math can never drift between the two variants.
    /// </summary>
    public static Shader TranslucentShader => _sharedTranslucent ??= new Shader { Code = TranslucentCode };

    /// <summary>The translucent source: the opaque <see cref="Code"/> with a single <c>ALPHA = base.a</c> write
    /// added (the opaque variant deliberately leaves ALPHA unwritten to stay in the opaque pass).</summary>
    private static readonly string TranslucentCode = Code.Replace(
        "ALBEDO = srgb_color ? combined : srgb_to_linear(combined);",
        "ALBEDO = srgb_color ? combined : srgb_to_linear(combined);\n" +
        "    ALPHA = base.a; // translucent variant (Q3 blendFunc blend): diffuse alpha drives the see-through.");

    /// <summary>True if <paramref name="shader"/> is one of the lightmap shader instances (opaque or
    /// translucent). The BSP load tally uses this to recognise a surface that came back on the lightmap path
    /// regardless of transparency variant, so a translucent glass surface is not mistaken for a lightmap-bind
    /// miss (the regression signature the tally guards).</summary>
    public static bool IsLightmapShader(Shader? shader)
        => shader != null && (shader == _shared || shader == _sharedTranslucent);

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
        Texture2D? normal = null, Texture2D? gloss = null)
    {
        var mat = new ShaderMaterial { Shader = translucent ? TranslucentShader : Shader };
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
        }
        if (gloss != null)
        {
            mat.SetShaderParameter(GlossUniform, gloss);
            mat.SetShaderParameter(UseGlossUniform, true);
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
