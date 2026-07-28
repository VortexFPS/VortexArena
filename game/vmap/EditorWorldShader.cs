using Godot;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// The editor world's surface shader when lighting is BAKED (design doc §10.1, the "compute it once" step).
///
/// Structure is DarkPlaces'/q3map2's, and the same split the shipped <see cref="Loaders.LightmapShader"/>
/// uses: the precomputed light rides in <c>EMISSION</c>, where no light source touches it, while
/// <c>ALBEDO</c> stays available for the handful of REAL-TIME lights worth keeping (the sun, with its
/// shadows). That is what lets one shadowed directional light coexist with hundreds of baked fixtures at no
/// per-pixel cost per fixture.
///
/// The baked term arrives in the mesh's COLOR channel — a vertex lightmap. Vertex granularity rather than a
/// chart atlas is the deliberate first rung: it needs no UV2, no packing and no atlas residency, and the
/// geometry is subdivided at bake time so the gradients land where a lightmap's luxels would.
/// </summary>
public static class EditorWorldShader
{
    /// <summary>
    /// Fallback HDR range for the baked vertex colours, used only until a bake measures its own.
    ///
    /// The light is stored as <c>sqrt(value / range)</c> and squared back in the shader. The square root is
    /// not decoration: storing linearly at a range wide enough for the peaks would leave the median at ~2 of
    /// 255 levels and band every dark surface. The RANGE itself is measured per bake
    /// (<see cref="EditorLightBake.EncodeRange"/>) because a fixed one silently clips the top of the
    /// distribution, and a clipped bake looks exactly like a flat one.
    /// </summary>
    public const float BakedColorRange = 48f;


    private static Shader? _shader;

    public static Shader Instance => _shader ??= new Shader { Code = Code };

    public const string Code = @"// Vortex Arena editor world (baked vertex lighting). Generated in C#.
shader_type spatial;
// ambient_light_disabled: the baked term already accounts for fill; letting the environment add its own on
// top washes out exactly the contrast the bake exists to provide.
render_mode cull_back, depth_draw_opaque, ambient_light_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform vec3 albedo_tint = vec3(1.0);
uniform vec2 uv_scale = vec2(1.0, 1.0);
uniform float alpha_cutoff = 0.0;

uniform sampler2D glow_tex : source_color, hint_default_black;  // fixture self-illumination page
uniform float glow_energy = 0.0;

uniform sampler2D normal_tex : hint_normal;   // the shader's _norm companion
uniform float normal_strength = 0.0;          // 0 when the material has no normal map

// The deluxemap: the direction the baked light arrived from, per vertex, in world space.
varying vec3 v_deluxe;

// LIVE controls, global on purpose: per-material uniforms are frozen into the material cache at build time,
// which is exactly how the first version of these knobs came to do nothing at all. A global is one
// RenderingServer set away from every surface, every frame, no rebuild, no rebake.
global uniform float editor_bake_scale;    // overall strength of the baked light
global uniform float editor_bake_ambient;  // flat floor added in-shader (ambient_light_disabled blocks the scene's)
global uniform float editor_bake_gamma;    // response curve on the baked light: >1 = punchier, more contrast
global uniform float editor_bake_range;    // HDR decode range, measured from the bake itself
global uniform float editor_deluxe;        // 0..1 blend of the per-pixel deluxe term

void vertex() {
    // CUSTOM0 carries the baked light direction. It has to be forwarded through a varying: a custom vertex
    // attribute is not visible to the fragment stage on its own.
    v_deluxe = CUSTOM0.xyz;
}

void fragment() {
    vec4 t = texture(albedo_tex, UV * uv_scale);
    if (alpha_cutoff > 0.0 && t.a < alpha_cutoff) {
        discard;
    }

    vec3 base = t.rgb * albedo_tint;

    // ALBEDO feeds the real-time lights (the sun): its shadows and direction stay live and correct.
    ALBEDO = base;
    ROUGHNESS = 0.9;
    METALLIC = 0.0;

    // EMISSION carries what was computed offline: the baked fixture light modulating this surface's own
    // colour, plus the fixture's glow page for the emitting faces themselves. The gamma curve is applied to
    // the LIGHT, not the albedo — the same place a lightmap's own storage response lives, and the knob that
    // separates physically-averaged-and-flat from the punchy compiled look.
    vec3 stored = max(COLOR.rgb, vec3(0.0));
    vec3 baked = pow(stored * stored * editor_bake_range, vec3(editor_bake_gamma)) * editor_bake_scale;

    // DELUXE: re-shade the baked light against this PIXEL's normal instead of the vertex's.
    //
    // The bake already applied N.L using the vertex normal, so the correction is the ratio between the
    // per-pixel and per-vertex terms — that is what makes a normal-mapped brick react to where the light
    // actually is. Irradiance alone cannot do this: it records how much light arrived, never from where,
    // which is why every pixel of a face shades identically without it.
    if (normal_strength > 0.0 && editor_deluxe > 0.0) {
        vec3 nm = texture(normal_tex, UV * uv_scale).xyz * 2.0 - 1.0;
        nm.xy *= normal_strength;
        vec3 n_view = normalize(TANGENT * nm.x + BINORMAL * nm.y + NORMAL * nm.z);
        vec3 n_world = normalize((INV_VIEW_MATRIX * vec4(n_view, 0.0)).xyz);
        vec3 flat_world = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
        vec3 ldir = normalize(v_deluxe);

        // Floor on the denominator: at grazing incidence the vertex term tends to zero and the ratio would
        // explode into a bright rim exactly where the bake is least certain.
        float flat_ndl = max(dot(flat_world, ldir), 0.25);
        float px_ndl = max(dot(n_world, ldir), 0.0);
        float k = clamp(px_ndl / flat_ndl, 0.0, 2.0);
        baked *= mix(1.0, k, editor_deluxe);
    }
    EMISSION = base * (baked + vec3(editor_bake_ambient))
        + texture(glow_tex, UV * uv_scale).rgb * glow_energy;
}
";
}
