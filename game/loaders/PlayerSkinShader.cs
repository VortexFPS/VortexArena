using Godot;

namespace XonoticGodot.Game.Loaders;

/// <summary>
/// The Darkplaces "skin" material shader: a diffuse surface plus the team-colorable <c>_shirt</c>/<c>_pants</c>
/// masks and the <c>_reflect</c> reflection mask that the channel-suffix companions carry.
///
/// <para>Darkplaces loads, alongside the diffuse, a family of sibling images by filename suffix
/// (<c>darkplaces.txt</c>): <c>_pants</c>/<c>_shirt</c> are <b>greyscale masks additively ("Screen") blended
/// over the diffuse, tinted by the player's pants/shirt color</b> (DP <c>gl_rmain.c</c>
/// <c>Color_Pants</c>/<c>Color_Shirt</c> uniforms over <c>Texture_Pants</c>/<c>Texture_Shirt</c>), and
/// <c>_reflect</c> is a reflection mask that modulates an environment cubemap added to the diffuse
/// (<c>Texture_ReflectMask</c> × <c>Texture_ReflectCube</c> in DP's GLSL). The stock
/// <see cref="StandardMaterial3D"/> can express none of these — two independent tinted additive masks have
/// no slot — so a model that uses them must compile to this dedicated shader.</para>
///
/// <para>The shirt/pants colors are bound as uniforms (<see cref="ShirtColorUniform"/>/
/// <see cref="PantsColorUniform"/>), defaulting to black exactly as DP does when an entity has no colormap
/// (so the masks contribute nothing until a team color is applied). The player renderer can set them per
/// entity from the colormap to drive team coloring; the masks are wired regardless, so they are no longer
/// dropped. The reflection mask drives a metallic-style environment reflection (a true cubemap reflection
/// from <c>dpreflectcube</c> would bind <see cref="ReflectCubeUniform"/>, otherwise the surface reflects the
/// world environment where the mask is bright).</para>
/// </summary>
public static class PlayerSkinShader
{
    // C2 STANDING RULE (godot#105750 / planning/PERFORMANCE_REPORT.md C2): the uniform names below are
    // `static readonly StringName`, not `const string`. Godot's *ShaderParameter / GlobalShaderParameter APIs
    // take a StringName, so a string literal there mints a StringName allocation per call — a GC treadmill if it
    // ever lands in a per-frame path. NEVER pass a string literal to a StringName/NodePath Godot API from a hot
    // path; cache it here. The XG0002 analyzer flags new violations inside _Process/_PhysicsProcess/_Draw.

    /// <summary>Uniform: the diffuse/albedo texture (sampled with UV).</summary>
    public static readonly StringName AlbedoUniform = "albedo_tex";

    /// <summary>Uniform: the <c>_shirt</c> greyscale mask.</summary>
    public static readonly StringName ShirtMaskUniform = "shirt_mask";

    /// <summary>Uniform: the <c>_pants</c> greyscale mask.</summary>
    public static readonly StringName PantsMaskUniform = "pants_mask";

    /// <summary>Uniform: the team shirt color (RGB). Default black = no contribution (DP no-colormap default).</summary>
    public static readonly StringName ShirtColorUniform = "shirt_color";

    /// <summary>Uniform: the team pants color (RGB). Default black = no contribution.</summary>
    public static readonly StringName PantsColorUniform = "pants_color";

    /// <summary>Uniform: the <c>_reflect</c> reflection mask (greyscale, modulates the environment reflection).</summary>
    public static readonly StringName ReflectMaskUniform = "reflect_mask";

    /// <summary>Uniform: optional explicit reflection cubemap (<c>dpreflectcube</c>); unbound → world environment.</summary>
    public static readonly StringName ReflectCubeUniform = "reflect_cube";

    /// <summary>Uniform: scalar reflection strength applied through the mask (0..1).</summary>
    public static readonly StringName ReflectStrengthUniform = "reflect_strength";

    /// <summary>Uniform: the <c>_glow</c>/<c>_luma</c> emission map (added unlit).</summary>
    public static readonly StringName GlowUniform = "glow_tex";

    /// <summary>Instance uniform: per-entity albedo tint (DP <c>colormod</c>); default white = identity.</summary>
    public static readonly StringName ColormodUniform = "colormod";

    /// <summary>Instance uniform: per-entity glow/emission tint (DP <c>glowmod</c>); default white.</summary>
    public static readonly StringName GlowmodUniform = "glowmod";

    /// <summary>Instance uniform: 1 = light this instance from the lightgrid sample (DP model lighting)
    /// instead of Godot's scene lights. Default 0 = the PBR path (also the no-grid-map fallback).</summary>
    public static readonly StringName GridLitUniform = "grid_lit";

    /// <summary>Instance uniform: lightgrid ambient RGB at the entity origin (DP scale: 1.0 ≈ grid byte 128).</summary>
    public static readonly StringName GridAmbientUniform = "grid_ambient";

    /// <summary>Instance uniform: lightgrid directed RGB (drives the N·L lobe AND the colored specular).</summary>
    public static readonly StringName GridDiffuseUniform = "grid_diffuse";

    /// <summary>Instance uniform: lightgrid light direction, normalized, GODOT world axes, pointing AT the light.</summary>
    public static readonly StringName GridDirUniform = "grid_dir";

    /// <summary>Global shader param behind <c>r_model_light_gamma</c>: 1 = DP-faithful gamma-space light
    /// response on grid-lit models, 0 = plain linear multiply. Registered/polled by <c>WorldTint</c>.</summary>
    public static readonly StringName ModelLightGammaUniform = "model_light_gamma";

    /// <summary>
    /// Uniform: the DP viewmodel depth-range max (<c>MATERIALFLAG_SHORTDEPTHRANGE</c> /
    /// <c>GL_DepthRange(0, 0.0625)</c>, gl_rmain.c:8581). 1 (default) = full depth range — identity for every
    /// world/player material; <c>ViewModelRenderFx</c> sets 0.0625 on the first-person weapon's own material
    /// duplicates so the gun's depth is compressed into the nearest 1/16 of the depth buffer and it never
    /// clips into world geometry. Shared name with <see cref="Md3MorphShader"/>.
    /// </summary>
    public static readonly StringName ViewmodelDepthRangeUniform = "viewmodel_depth_range";

    /// <summary>
    /// The GDShader source. Lit (the skin reacts to scene lighting like a normal model surface), with the
    /// shirt/pants masks added as tinted terms over the albedo and the reflect mask driving the metallic
    /// channel so Godot's environment reflection shows through where the mask is bright. Optional
    /// normal/roughness(gloss)/glow are honored when their samplers are bound.
    /// </summary>
    public const string Code = @"// XonoticGodot player-skin shader (Darkplaces _shirt/_pants/_reflect masks). Generated in C#.
shader_type spatial;
// OPAQUE skin. These DP alias-model skins (players + weapon viewmodels) are solid: real translucency comes
// from a shader blendFunc (which compiles elsewhere), never from the diffuse alpha — in DP a model skin's
// alpha channel is a spec/gloss mask, not transparency. We must NOT write ALPHA below: in Godot 4 any
// assignment to ALPHA sets the shader's `uses_alpha` flag, which moves the material into the transparent
// pass (alpha-blended, and with the default depth_draw_opaque it stops writing depth). Depth-write off on a
// self-overlapping mesh lets its own back faces draw over its front faces in submission order — the
// ""I can see vertices through other vertices"" see-through artifact on every weapon model. Leaving ALPHA at
// its default (1.0) keeps the skin opaque with normal depth testing.
render_mode cull_back;

uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D shirt_mask : hint_default_black;
uniform sampler2D pants_mask : hint_default_black;
uniform sampler2D reflect_mask : hint_default_black;
uniform sampler2D normal_tex : hint_normal;
uniform sampler2D gloss_tex : hint_default_white;
uniform sampler2D glow_tex : hint_default_black;
// dpreflectcube (playtest #36): the environment cubemap the _reflect mask gates. DP ADDS mask x cubemap on
// top of the lit diffuse (USEREFLECTCUBE in its GLSL) - the diffuse color always survives. Faces are loaded
// in DP box order (+X -X +Y -Y +Z -Z, QUAKE axes), so the sample direction below converts Godot->Quake.
uniform samplerCube reflect_cube : source_color, hint_default_black;
uniform bool has_normal = false;
uniform bool has_gloss = false;
uniform bool has_glow = false;
uniform bool has_reflect = false;
uniform bool has_reflect_cube = false;
// Per-entity tints are instance uniforms so the shared (cached) skin material can still be reused while
// each model instance carries its own colors (set via MeshInstance3D.set_instance_shader_parameter).
instance uniform vec3 shirt_color : source_color = vec3(0.0);
instance uniform vec3 pants_color : source_color = vec3(0.0);
instance uniform vec3 colormod : source_color = vec3(1.0);
instance uniform vec3 glowmod : source_color = vec3(1.0);
// Lightgrid model lighting (playtest r14 experiments B/C): DP lights every model from ONE source — the BSP
// lightgrid sample at the entity origin (Mod_Q3BSP_LightPoint -> MODE_LIGHTDIRECTION). When grid_lit is on,
// this branch reproduces that formula and bypasses Godot's scene lights entirely (EMISSION-only output):
//    tex x ambient  +  tex x diffuse x max(0,N.L)  +  gloss.rgb x diffuse x pow(max(0,N.H), 1+32*gloss.a)
//    + glow                                            (DP gl_rmain.c R_SetupShader_Surface, glsl default.glsl)
// Values are DP scale (1.0 = grid byte 128) and may exceed 1 — q3map overbright is what turns the near-black
// weapon albedos into readable gunmetal. NOT source_color: the C# side passes raw floats, no sRGB decode.
instance uniform float grid_lit = 0.0;
instance uniform vec3 grid_ambient = vec3(1.0);
instance uniform vec3 grid_diffuse = vec3(0.0);
instance uniform vec3 grid_dir = vec3(0.0, 1.0, 0.0); // Godot WORLD axes, points AT the light
// DP first-person viewmodel depth hack (MATERIALFLAG_SHORTDEPTHRANGE = GL_DepthRange(0, 0.0625),
// gl_rmain.c:6214/8581): RENDER_VIEWMODEL entities compress their depth into the nearest 1/16 of the depth
// buffer with depth TESTING still on — the gun keeps its own self-occlusion but always beats world geometry,
// so it never clips into walls. This is that depth-range max: 1.0 (default) = full range, an IDENTITY remap
// for every world/player material (mix(z, w, 0.0) == z bit-exact). A PLAIN uniform, not an instance uniform:
// ViewModelRenderFx gives the gun its own duplicates of the shared skin materials and sets 0.0625 on those
// (per-instance values proved unreliable for a vertex-stage uniform here; a per-material value is exact and
// the duplicates share this same Shader object, so no new pipelines are introduced).
uniform float viewmodel_depth_range = 1.0;
uniform float reflect_strength = 1.0;
// GPU MD3 vertex-morph (cl_gpu_morph — ModelAnimator.ApplyFrameGpu): frameB position/normal streams ride
// CUSTOM0/CUSTOM1 and morph_amount blends toward them, so a skin-shaded morph model (the CTF flag's team-
// tintable _shirt skin — the r16 4.4ms/frame CPU-rebuild case) animates GPU-side like the Md3MorphShader
// path. Default 0.0 keeps the branch off — every existing shared-skin use renders bit-identically, incl.
// meshes with no CUSTOM arrays. Like viewmodel_depth_range this is a PLAIN per-material uniform (vertex-
// stage instance uniforms are unreliable); ModelAnimator gives each morphing surface its own Duplicate()
// of the resolved skin material (same Shader object — no new pipeline family) and drives the value there.
uniform float morph_amount = 0.0;
// Dynamic scene tint (XonoticGodot.Game.WorldTint) — a GLOBAL shader parameter applied to every model/skin
// (players, weapon viewmodels, pickups) so the ""everything else"" grade can differ from the whole-map tint.
// Strength is folded in on the C# side; default (1,1,1) is identity. Distinct from the per-entity colormod.
global uniform vec3 entity_tint;
// r_model_light_gamma (experiment A, registered/polled by WorldTint): DP renders models in GAMMA space
// (vid_sRGB 0 — light multiplies the gamma-encoded texel, no tonemap, so displayed brightness scales
// LINEARLY with light). The port is linear end-to-end, which compresses the same math perceptually
// (~L^0.45). 1 = emulate DP: redo the multiply on gamma-encoded values and pre-decode the result so
// Linear tonemap + sRGB encode displays it verbatim. 0 (default — preferred in the r15 playtest A/B)
// = plain linear multiply; the grid shading structure is identical either way.
global uniform float model_light_gamma;

void vertex() {
    // GPU MD3 vertex-morph (see morph_amount above): frameA is the mesh's own streams, frameB rides
    // CUSTOM0/1 — identical math to Md3MorphShader.vertex(). Branch-gated on the uniform (dynamically
    // uniform, no divergence cost) so the default-0 case is EXACTLY the pre-morph shader: VERTEX/NORMAL
    // untouched.
    if (morph_amount > 0.0) {
        VERTEX = mix(VERTEX, CUSTOM0.xyz, morph_amount);
        NORMAL = normalize(mix(NORMAL, CUSTOM1.xyz, morph_amount));
    }
    // Custom projection ONLY to remap depth (the DP SHORTDEPTHRANGE viewmodel hack — see
    // viewmodel_depth_range above). Godot 4.3+ uses REVERSED-Z (NDC near = 1, far = 0), so GL's
    // glDepthRange(0, frac) window slice [0, frac] mirrors to [1-frac, 1]: z' = (1-frac)*w + frac*z.
    // At the default frac = 1.0 this is mix(z, w, 0.0) == z — bit-identical to the fixed-function
    // transform, so world/player instances render exactly as before. VERTEX is untouched by the depth
    // remap (morph aside), so view-space lighting in fragment() is unaffected. Viewmodel meshes cast no
    // shadows (the C# side sets CastShadow=Off), so the light-projection shadow pass never sees a
    // compressed depth. Computed from the (possibly morphed) VERTEX.
    POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POSITION.z = mix(POSITION.z, POSITION.w, 1.0 - viewmodel_depth_range);
}

void fragment() {
    vec4 base = texture(albedo_tex, UV);
    // DP _shirt/_pants: greyscale mask, additively (Screen-ish) blended with the team color tint.
    float shirt = texture(shirt_mask, UV).r;
    float pants = texture(pants_mask, UV).r;
    vec3 col = base.rgb + shirt * shirt_color + pants * pants_color;
    // colormod: the per-entity tint DP folds into the lighting term (default white = identity).
    // entity_tint: the global, dynamic scene grade (default white = identity), applied on top.
    vec3 tinted = col * colormod * entity_tint;
    // NOTE: deliberately NOT writing ALPHA — see the opaque-skin note in the render_mode header. base.a is a
    // spec/mask channel here, not transparency, and writing it would force the transparent (no-depth) pass.

    if (grid_lit > 0.5) {
        // ---- DP lightgrid model lighting (playtest r14 B/C/A) ----
        // Normal-mapping applied manually: NORMAL_MAP feeds Godot's scene-light path, which this branch
        // bypasses (missing/zero tangents degrade toward the vertex normal — same binding contract as
        // the PBR path's NORMAL_MAP).
        vec3 nrm = normalize(NORMAL);
        if (has_normal) {
            vec3 nm = texture(normal_tex, UV).rgb * 2.0 - 1.0;
            nrm = normalize(TANGENT * nm.x + BINORMAL * nm.y + NORMAL * nm.z);
        }
        vec3 ldir = normalize((VIEW_MATRIX * vec4(grid_dir, 0.0)).xyz);
        float ndl = max(dot(nrm, ldir), 0.0);
        // DP specular: Blinn half-vector against the BAKED light direction, exponent
        // 1 + r_shadow_glossexponent(32) * gloss.a, colored by gloss.rgb x the directed grid term
        // (default.glsl MODE_LIGHTDIRECTION; no N.L gate — DP has none, the high exponent handles it).
        vec4 gloss_px = has_gloss ? texture(gloss_tex, UV) : vec4(0.0);
        vec3 half_v = normalize(ldir + VIEW);
        float spec = pow(max(dot(nrm, half_v), 0.0), 1.0 + 32.0 * gloss_px.a);
        vec3 glow = has_glow ? texture(glow_tex, UV).rgb * glowmod : vec3(0.0);
        vec3 lit;
        if (model_light_gamma > 0.5) {
            // Gamma-faithful (A): rebuild the authored gamma texels ((a*b)^(1/g) == a^(1/g)*b^(1/g), so the
            // tint factors survive re-encoding), run DP's multiply there, clamp to displayable range (DP's
            // framebuffer saturates — overbright clips to white, no bloom), then pre-decode so the Linear
            // tonemap + output sRGB encode round-trips back to exactly this value on screen.
            vec3 g_tex = pow(max(tinted, vec3(0.0)), vec3(1.0 / 2.2));
            vec3 g_gloss = pow(max(gloss_px.rgb, vec3(0.0)), vec3(1.0 / 2.2));
            vec3 g_glow = pow(max(glow, vec3(0.0)), vec3(1.0 / 2.2));
            vec3 res_g = g_tex * grid_ambient + g_tex * grid_diffuse * ndl
                       + g_gloss * grid_diffuse * spec + g_glow;
            lit = pow(clamp(res_g, vec3(0.0), vec3(1.0)), vec3(2.2));
        } else {
            // Linear variant (r_model_light_gamma 0): same structure, straight multiply in linear space.
            lit = tinted * grid_ambient + tinted * grid_diffuse * ndl
                + gloss_px.rgb * grid_diffuse * spec + glow;
        }
        // EMISSION-only output: scene lights must not double-light the grid-lit surface.
        ALBEDO = vec3(0.0);
        ROUGHNESS = 1.0;
        METALLIC = 0.0;
        SPECULAR = 0.0;
        EMISSION = lit;
    } else {
        // ---- PBR path (grid off / no lightgrid on the map): the pre-r14 look. ----
        ALBEDO = tinted;

        if (has_normal) {
            NORMAL_MAP = texture(normal_tex, UV).rgb;
        }

        // Resolve roughness/metallic locally (avoid reading write-only builtins), then assign once.
        float rough = has_gloss ? (1.0 - texture(gloss_tex, UV).g) : 1.0; // gloss is the inverse of roughness
        float metal = 0.0;
        vec3 reflect_add = vec3(0.0);
        if (has_reflect) {
            float m = texture(reflect_mask, UV).r * reflect_strength;
            if (has_reflect_cube) {
                // DP USEREFLECTCUBE (playtest #36): reflection = ReflectMask x ReflectCube(reflected view dir),
                // ADDED over the lit diffuse - it never replaces the base color. (The old METALLIC=m routing
                // ZEROED the diffuse albedo where the mask was bright - PBR metals have no diffuse - which
                // desaturated every weapon: the ""lacking color"" report.) Reflect in view space, take the
                // direction to Godot world space, then convert Godot(Y-up) -> Quake(Z-up) axes because the cube
                // faces are authored/loaded in DP box order.
                vec3 vdir = reflect(-VIEW, NORMAL);
                vec3 wdir = normalize((INV_VIEW_MATRIX * vec4(vdir, 0.0)).xyz);
                vec3 qdir = vec3(wdir.x, -wdir.z, wdir.y);
                reflect_add = texture(reflect_cube, qdir).rgb * m;
                rough = mix(rough, 0.3, m * 0.5); // a mild sheen; the diffuse keeps its full contribution
            } else {
                // No cubemap resolved: a restrained metallic sheen approximation. Kept well below 1 so the
                // albedo still contributes (full metalness was the color-killer).
                metal = m * 0.35;
                rough = mix(rough, 0.2, m);
                SPECULAR = mix(0.5, 1.0, m);
            }
        }
        ROUGHNESS = rough;
        METALLIC = metal;

        // Emission composes the additive cubemap reflection with the _glow map (DP adds both over the lit surface).
        vec3 emis = reflect_add;
        if (has_glow) {
            // glowmod: per-entity glow tint (DP render_glowmod); viewmodels/players use the pants color.
            emis += texture(glow_tex, UV).rgb * glowmod;
        }
        EMISSION = emis;
    }
}
";

    private static Shader? _shared;

    /// <summary>The shared <see cref="Shader"/> instance compiled from <see cref="Code"/>.</summary>
    public static Shader Shader => _shared ??= new Shader { Code = Code };
}
