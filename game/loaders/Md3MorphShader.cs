using Godot;

namespace XonoticGodot.Game.Loaders;

/// <summary>
/// The GPU vertex-morph spatial shader for MD3 frame interpolation (item 3.3 Tier-3). It reproduces, on the
/// GPU, the per-vertex lerp DarkPlaces' <c>R_AliasLerpVerts</c> (and our CPU path in
/// <c>ModelAnimator.ApplyFrame</c>) does on the CPU: a smoothly-playing morph model collapses to "set frameA
/// streams once when the A/B bracket changes + set one <c>morph_amount</c> float per render frame".
///
/// <para>The mesh carries frameA in the standard <c>ARRAY_VERTEX</c>/<c>ARRAY_NORMAL</c> streams and frameB in
/// two custom vertex channels — <c>CUSTOM0.xyz</c> = frameB position, <c>CUSTOM1.xyz</c> = frameB normal — both
/// already converted Quake→Godot at bake time (so the shader does NO coordinate conversion; it just lerps two
/// Godot-space values, exactly equivalent to lerping the Quake values then converting). <c>vertex()</c> mixes
/// the two by <c>morph_amount</c> and re-normalizes the morphed normal. The C# side packs CUSTOM0/CUSTOM1 with
/// <see cref="Godot.Mesh.ArrayCustomFormat.RgbaFloat"/> (full-float vec4, .w unused).</para>
///
/// <para>It is used ONLY when <c>cl_gpu_morph</c> is on AND every visible surface resolved to a
/// <see cref="StandardMaterial3D"/> (or null) — the common gib / casing / turret / vehicle-body / pickup case.
/// Surfaces backed by a custom <see cref="ShaderMaterial"/> (the <see cref="PlayerSkinShader"/> or a generated
/// animated stage) cannot have a <c>vertex()</c> bolted on, so such a model transparently stays on the CPU
/// morph path; this keeps parity exact for skins/animated stages. The default (<c>cl_gpu_morph 0</c>) is the
/// byte-identical CPU path.</para>
///
/// <para><see cref="Code"/>'s <c>fragment()</c> replicates the look <c>AssetSystem.BuildPlainMaterial</c> /
/// <c>WireCompanions</c> produce on a StandardMaterial3D: albedo (UV), optional normal map, gloss→roughness
/// (inverse, matching <see cref="PlayerSkinShader"/>), glow→emission. As with the CPU StandardMaterial3D path,
/// no tangents are generated for morph meshes — see the <c>has_normal</c> note: it defaults false so the look
/// matches today (the StandardMaterial3D path already lacks tangents). The global <c>entity_tint</c> grade is
/// folded in for consistency with the other model materials (<see cref="PlayerSkinShader"/>).</para>
/// </summary>
public static class Md3MorphShader
{
    // C2 STANDING RULE (godot#105750 / planning/PERFORMANCE_REPORT.md C2): the uniform names below are
    // `static readonly StringName`, not `const string`. Godot's *ShaderParameter / GlobalShaderParameter APIs
    // take a StringName, so a string literal there mints a StringName allocation per call — a GC treadmill in a
    // per-frame path (morph_amount is set every render frame). NEVER pass a string literal to a StringName/
    // NodePath Godot API from a hot path; cache it here. The XG0002 analyzer flags new violations.

    /// <summary>Uniform: the frameA→frameB interpolation weight (0 = frameA, 1 = frameB). Set every render frame.</summary>
    public static readonly StringName MorphAmountUniform = "morph_amount";

    /// <summary>Uniform: the diffuse/albedo texture (sampled with UV).</summary>
    public static readonly StringName AlbedoUniform = "albedo_tex";

    /// <summary>Uniform: the normal map (only honored when <see cref="HasNormalUniform"/> is true).</summary>
    public static readonly StringName NormalUniform = "normal_tex";

    /// <summary>Uniform: the gloss map (sampled .g, inverted into roughness; only when <see cref="HasGlossUniform"/>).</summary>
    public static readonly StringName GlossUniform = "gloss_tex";

    /// <summary>Uniform: the glow/emission map (added unlit; only when <see cref="HasGlowUniform"/>).</summary>
    public static readonly StringName GlowUniform = "glow_tex";

    /// <summary>Uniform: whether a normal map is bound.</summary>
    public static readonly StringName HasNormalUniform = "has_normal";

    /// <summary>Uniform: whether a gloss map is bound (drives roughness).</summary>
    public static readonly StringName HasGlossUniform = "has_gloss";

    /// <summary>Uniform: whether a glow map is bound (drives emission).</summary>
    public static readonly StringName HasGlowUniform = "has_glow";

    /// <summary>
    /// The GDShader source. Lit spatial surface; <c>vertex()</c> morphs frameA (ARRAY_VERTEX/ARRAY_NORMAL) toward
    /// frameB (CUSTOM0/CUSTOM1) by <c>morph_amount</c>; <c>fragment()</c> replicates the StandardMaterial3D look
    /// used by the CPU path (albedo + optional normal/gloss/glow). Custom channels CUSTOM0/CUSTOM1 are read as
    /// the GDShader built-ins of the same name — no uniform declaration; the mesh surface must declare the
    /// matching ARRAY_CUSTOM0/ARRAY_CUSTOM1 RgbaFloat format (done C#-side in ModelAnimator).
    /// </summary>
    public const string Code = @"// XonoticGodot MD3 GPU vertex-morph shader (R_AliasLerpVerts on the GPU). Generated in C#.
shader_type spatial;
// OPAQUE, cull_back to match the StandardMaterial3D default this wraps (and PlayerSkinShader). We deliberately
// do NOT write ALPHA: any assignment to ALPHA sets the shader's uses_alpha flag and moves the material into the
// transparent (no-depth) pass — the see-through self-overlap artifact. These MD3 morph props are solid.
render_mode cull_back;

uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D normal_tex : hint_normal;
uniform sampler2D gloss_tex : hint_default_white;
uniform sampler2D glow_tex : hint_default_black;
uniform bool has_normal = false;
uniform bool has_gloss = false;
uniform bool has_glow = false;
// frameA->frameB blend weight. Set every render frame from C# (the cheap ""one float"" steady-state cost).
uniform float morph_amount = 0.0;
// DP first-person viewmodel depth hack (MATERIALFLAG_SHORTDEPTHRANGE = GL_DepthRange(0, 0.0625)) — the same
// plain uniform as PlayerSkinShader: 1.0 (default) = full depth range, an identity remap for every world
// morph prop; a first-person muzzle-flash/weapon morph material gets 0.0625 (morph materials are built
// per-animator, so ViewModelRenderFx can set it directly) and compresses into the nearest 1/16 slice so it
// never clips into world geometry.
uniform float viewmodel_depth_range = 1.0;
// Dynamic scene tint (XonoticGodot.Game.WorldTint) — a GLOBAL shader parameter applied to every model material
// (players, weapon viewmodels, pickups, and now GPU-morph props) so the look matches the rest of the pipeline.
// Strength is folded in on the C# side; default (1,1,1) is identity.
global uniform vec3 entity_tint;

void vertex() {
    // ARRAY_VERTEX / ARRAY_NORMAL hold frameA; CUSTOM0.xyz / CUSTOM1.xyz hold frameB — both already converted
    // Quake->Godot at bake time. Mixing two Godot-space values by morph_amount is exactly equivalent to lerping
    // the Quake-space values then converting (the CPU path's R_AliasLerpVerts), so the geometry is identical.
    VERTEX = mix(VERTEX, CUSTOM0.xyz, morph_amount);
    NORMAL = normalize(mix(NORMAL, CUSTOM1.xyz, morph_amount));
    // DP SHORTDEPTHRANGE viewmodel remap (see viewmodel_depth_range above), from the MORPHED vertex. Godot
    // 4.3+ reversed-Z: GL's [0, frac] window slice mirrors to [1-frac, 1] => z' = (1-frac)*w + frac*z. At the
    // default 1.0 this is mix(z, w, 0.0) == z — bit-identical to the fixed-function transform.
    POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POSITION.z = mix(POSITION.z, POSITION.w, 1.0 - viewmodel_depth_range);
}

void fragment() {
    vec4 base = texture(albedo_tex, UV);
    ALBEDO = base.rgb * entity_tint;
    // NOTE: deliberately NOT writing ALPHA — see the opaque note in the render_mode header.

    if (has_normal) {
        NORMAL_MAP = texture(normal_tex, UV).rgb;
    }

    // gloss is the inverse of roughness; sample green channel (matches PlayerSkinShader + WireCompanions).
    ROUGHNESS = has_gloss ? (1.0 - texture(gloss_tex, UV).g) : 1.0;

    if (has_glow) {
        // _glow is added FULLBRIGHT (a mostly-black mask); base emission black so only the bright bits light up.
        EMISSION = texture(glow_tex, UV).rgb;
    }
}
";

    private static Shader? _shared;

    /// <summary>The shared <see cref="Shader"/> instance compiled from <see cref="Code"/>.</summary>
    public static Shader Shader => _shared ??= new Shader { Code = Code };
}
