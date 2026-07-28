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
    /// HDR range of the baked vertex colours. The mesh COLOR channel is 8-bit and clamps at 1, while the
    /// bake spans a measured 0..42 (median 0.47, p99 13.3) — so the light is stored as
    /// <c>sqrt(value / range)</c> and squared back in the shader.
    ///
    /// The square root is not decoration. Storing linearly at a range wide enough for the peaks would leave
    /// the median at ~2 of 255 levels and band every dark surface in the map; the sqrt spends its precision
    /// where the eye is sensitive, which is exactly what real HDR lightmap encodings do. Clipping the peaks
    /// instead — the previous range of 8 — flattened the brightest 4% of vertices, and that 4% is precisely
    /// the pool of light directly under each fixture, which is why fixtures read as glowing decals with no
    /// light around them.
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

// LIVE controls, global on purpose: per-material uniforms are frozen into the material cache at build time,
// which is exactly how the first version of these knobs came to do nothing at all. A global is one
// RenderingServer set away from every surface, every frame, no rebuild, no rebake.
global uniform float editor_bake_scale;    // overall strength of the baked light
global uniform float editor_bake_ambient;  // flat floor added in-shader (ambient_light_disabled blocks the scene's)
global uniform float editor_bake_gamma;    // response curve on the baked light: >1 = punchier, more contrast

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
    vec3 baked = pow(stored * stored * 48.0, vec3(editor_bake_gamma)) * editor_bake_scale;
    EMISSION = base * (baked + vec3(editor_bake_ambient))
        + texture(glow_tex, UV * uv_scale).rgb * glow_energy;
}
";
}
