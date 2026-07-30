using System.Text;

namespace XonoticGodot.Formats.Materials;

/// <summary>
/// Generates the GDShader source for a Q3 <c>deformVertexes autosprite</c>/<c>autosprite2</c> surface —
/// Base's bolt projectiles (<c>laser_projectile_core/long</c>, <c>electro_projectile_core/long</c>,
/// HLAC). The companion bake (<c>AutospriteQuads</c>) stores each quad's view-independent frame in custom
/// vertex attributes; this vertex shader rebuilds every corner on the live view axes each frame, on the
/// GPU (DP does the same math on the CPU, <c>gl_rmain.c</c> Q3DEFORM_AUTOSPRITE/AUTOSPRITE2):
///
/// <list type="bullet">
///   <item><c>CUSTOM0</c> = (quad center xyz, s) — s is the corner's width offset.</item>
///   <item><c>CUSTOM1</c> = (long axis xyz, t) — t is the corner's offset along the axis (autosprite2)
///     or the screen-up offset (autosprite; the axis is unused).</item>
/// </list>
///
/// <para>Shading is DP's bolt model, NOT the unshaded look: <c>blendfunc add</c> +
/// <c>rgbGen lightingDiffuse</c> means the base texture is <b>lit</b> (Godot lights <c>ALBEDO</c>), and
/// the fullbright <c>_glow</c> companion rides <c>EMISSION</c> (DP adds it unlit on top), the whole thing
/// blended additively. The fragment reuses the shared <see cref="Q3StageGlsl"/> emitters, so the stage's
/// tcMod stack (including electro's <c>tcMod page</c> crackle flipbook) and rgbGen keep working.</para>
///
/// <para>Per-quad data rides vertex attributes, not instance uniforms — instance uniforms are inert for
/// vertex-stage values in this codebase. The per-entity <c>colormod</c>/<c>glowmod</c> instance uniforms
/// below are fragment-stage, the safe case.</para>
/// </summary>
public static class AutospriteShaderGen
{
    /// <summary>
    /// Emit the shader source. <paramref name="axial"/> selects the deform: false = <c>autosprite</c>
    /// (full view-plane billboard), true = <c>autosprite2</c> (axial — the streak stays along its flight
    /// axis and only rolls toward the viewer).
    /// </summary>
    public static string Generate(ShaderDef def, ShaderStage stage, bool axial)
    {
        var sb = new StringBuilder(1536);
        sb.Append("// XonoticGodot autosprite").Append(axial ? "2" : "").Append(" deform shader for '")
          .Append(Sanitize(def.Name)).Append("'. Generated in C#.\n");
        sb.Append("shader_type spatial;\n");
        // skip_vertex_transform: VERTEX/NORMAL are assigned in VIEW space by our vertex() below.
        // cull_disabled: the re-aimed quad's winding depends on the view; both faces must draw.
        // blend_add: Q3 `blendfunc add` — and deliberately NOT `unshaded`: DP lights the base texture
        // (rgbGen lightingDiffuse) and only the _glow companion is fullbright (EMISSION).
        // fog_disabled: Godot fogs transparent surfaces toward the FOG COLOR, which on an additive
        // quad paints the black region as a haze-colored box; DP instead fades additive surfaces
        // toward black in fog. Staying fog-free is the lesser divergence for a small short-lived bolt.
        sb.Append("render_mode skip_vertex_transform, cull_disabled, blend_add, fog_disabled;\n\n");

        sb.Append("uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;\n");
        sb.Append("uniform sampler2D glow_tex : source_color, filter_linear_mipmap_anisotropic;\n");
        // Per-entity DP colormod/glowmod (folded into lightingDiffuse / the glow add). Fragment-stage
        // instance uniforms — the working kind. Identity by default.
        sb.Append("instance uniform vec3 colormod : source_color = vec3(1.0, 1.0, 1.0);\n");
        sb.Append("instance uniform vec3 glowmod : source_color = vec3(1.0, 1.0, 1.0);\n\n");

        // ---- vertex(): rebuild each corner from its baked quad frame, in view space ----
        sb.Append("void vertex() {\n");
        sb.Append("    vec3 c = (MODELVIEW_MATRIX * vec4(CUSTOM0.xyz, 1.0)).xyz;   // quad center, view space\n");
        sb.Append("    float ms = length(MODEL_MATRIX[0].xyz);                     // uniform node scale\n");
        if (axial)
        {
            // autosprite2: t stays along the flight axis; s is re-aimed at the camera.
            // DP: forward = center - vieworigin (in view space just c), newright = cross(axis, forward).
            sb.Append("    vec3 ax = normalize(mat3(MODELVIEW_MATRIX) * CUSTOM1.xyz); // long/flight axis\n");
            sb.Append("    vec3 fw = normalize(c);                                    // viewer -> quad\n");
            sb.Append("    vec3 rt = cross(ax, fw);\n");
            sb.Append("    float rl = length(rt);\n");
            sb.Append("    rt = rl > 0.00001 ? rt / rl : vec3(1.0, 0.0, 0.0);         // degenerate: axis at the camera\n");
            sb.Append("    VERTEX = c + ax * (CUSTOM1.w * ms) + rt * (CUSTOM0.w * ms);\n");
        }
        else
        {
            // autosprite: the whole quad re-aims at the view plane — s along view-right, t along view-up.
            sb.Append("    VERTEX = c + vec3(CUSTOM0.w, CUSTOM1.w, 0.0) * ms;\n");
        }
        sb.Append("    NORMAL = vec3(0.0, 0.0, 1.0);   // camera-facing, for the lit base (view space)\n");
        sb.Append("}\n\n");

        // ---- fragment(): tcMod stack -> lit base * colormod + fullbright glow ----
        sb.Append("void fragment() {\n");
        sb.Append("    vec2 uv = UV;\n");
        foreach (TcMod m in stage.TcMods)
            Q3StageGlsl.EmitTcMod(sb, m);
        sb.Append("    vec4 c = texture(albedo_tex, uv);\n");
        Q3StageGlsl.EmitRgbGen(sb, stage.RgbGen);
        sb.Append("    ALBEDO = c.rgb * colormod;                       // lit — rgbGen lightingDiffuse\n");
        // DP's lightingDiffuse is DIFFUSE-ONLY. Godot's lit pipeline would add a specular lobe over the
        // whole quad INDEPENDENT of the black albedo — a view/light-angle-dependent white sheen that
        // makes the quad's full square flash into view (the "white box around the bolt" artifact).
        sb.Append("    SPECULAR = 0.0;\n");
        sb.Append("    ROUGHNESS = 1.0;\n");
        sb.Append("    EMISSION = texture(glow_tex, uv).rgb * glowmod;  // _glow companion, fullbright add\n");
        // Q3 add is GL_ONE GL_ONE; Godot blend_add is srcAlpha*ONE, so alpha must be 1 — black texels
        // self-erase under the add, exactly like DP.
        sb.Append("    ALPHA = 1.0;\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string Sanitize(string s) => s.Replace("*/", "* /").Replace("\n", " ").Replace("\r", " ");
}
