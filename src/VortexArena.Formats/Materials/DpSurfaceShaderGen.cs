// The GDShader text of a Quake 3 shader drawn by DarkPlaces' rules (DpMaterialRules.Plan): what the game's
// DpSurfaceShader compiles for a legacy session. Pure text building, no engine types, so that what it emits
// can be tested: gl_rmain.c R_GetCurrentTexture / R_SetupShader_Surface and shader_glsl.h, reduced to the one
// material stage DarkPlaces draws.
using System;
using System.Collections.Generic;
using System.Text;

namespace VortexArena.Formats.Materials;

public static class DpSurfaceShaderGen
{
    /// <summary>Marks a material built from this generator (the first line of its shader source).</summary>
    public const string Banner = "// VortexArena DarkPlaces-rule surface shader";

    /// <summary>The GDShader source for one plan.</summary>
    /// <param name="forModel">An entity's model: lit from the light grid and tinted by colormod, where a
    /// surface of the level is lit by its vertex colours or (<paramref name="lightmapped"/>) its lightmap page.</param>
    /// <param name="glow">The material stage's texture has a _glow companion.</param>
    /// <param name="frames">animMap frames in use (1 without an animMap).</param>
    public static string Generate(ShaderDef def, in DpMaterialPlan plan, ShaderStage stage, ShaderStage? background, bool forModel, bool glow, int frames, bool lightmapped = false, bool reflect = false, DpWater? water = null)
    {
        reflect &= !forModel;
        if (forModel || !plan.Blended) water = null;   // gl_rmain.c: a water shader must be a blended texture
        lightmapped &= !plan.FullBright && !forModel;
        StringBuilder sb = new(4096);
        sb.Append(Banner).Append(" (").Append(plan.FullBright ? "full-bright" : forModel ? "light grid" : lightmapped ? "lightmap" : "vertex light")
          .Append(", ").Append(plan.Blend).Append("). Generated in C#.\n");
        sb.Append("shader_type spatial;\n");

        // ---- render state: gl_rmain.c R_GetCurrentTexture, currentblendfunc and the cull / depth flags ----
        List<string> modes = new() { "unshaded", plan.TwoSided ? "cull_disabled" : "cull_back", "shadows_disabled" };
        // Godot's multiply blend takes the factor from the colour alone, which is what every multiplying pair
        // DarkPlaces accepts needs; the pairs it cannot express fall back to the nearest of add / mix.
        string blend = plan.Blend switch
        {
            DpBlend.Add => "blend_add",
            DpBlend.Alpha => "blend_mix",
            DpBlend.Custom => CustomBlend(plan.CustomSrc, plan.CustomDst),
            _ => "",
        };
        // The water variant (r_water 1) composes its own picture from the scene behind it and a reflection, and
        // replaces what is there: an ordinary mix at alpha 1.
        if (water is not null) blend = "blend_mix";
        if (blend.Length > 0)
        {
            modes.Add(blend);
            modes.Add("depth_draw_never");   // a blended surface does not write depth
        }
        sb.Append("render_mode ").Append(string.Join(", ", modes)).Append(";\n\n");

        // No source_color anywhere: DarkPlaces (vid_sRGB 0) filters and combines the stored values.
        string filter = stage.ClampMap || stage.AnimMap is { Clamp: true } ? "filter_linear_mipmap_anisotropic, repeat_disable" : "filter_linear_mipmap_anisotropic, repeat_enable";
        sb.Append("uniform sampler2D albedo_tex : ").Append(filter).Append(";\n");
        for (int i = 1; i < frames; i++) sb.Append("uniform sampler2D anim_tex_").Append(i).Append(" : ").Append(filter).Append(";\n");
        if (background is not null) sb.Append("uniform sampler2D background_tex : filter_linear_mipmap_anisotropic, repeat_enable;\n");
        if (glow) sb.Append("uniform sampler2D glow_tex : hint_default_black, ").Append(filter).Append(";\n");
        if (lightmapped) sb.Append("uniform sampler2D lightmap_tex : hint_default_white, filter_linear, repeat_disable;\n");
        if (reflect)
        {
            // dpreflectcube (shader_glsl.h USEREFLECTCUBE): diffusetex += reflectmask * reflectcube.
            sb.Append("uniform sampler2D reflect_mask : hint_default_black, ").Append(filter).Append(";\n");
            sb.Append("uniform samplerCube reflect_cube : filter_linear_mipmap;\n");
        }
        sb.Append("uniform float morph_amount = 0.0;            // GPU MD3 vertex-morph (ModelAnimator), as the other model shaders\n");
        sb.Append("uniform float viewmodel_depth_range = 1.0;   // MATERIALFLAG_SHORTDEPTHRANGE, as PlayerSkinShader\n");
        sb.Append("global uniform float dp_time;                // cl.time\n");
        sb.Append("global uniform vec3 map_tint;\n");
        // r_refdef.scene.lightmapintensity: the value of light style 0 (1.03125 for the usual "m"), which scales
        // every lit surface - lightmap, vertex colour or light grid - and no full-bright one.
        if (!plan.FullBright) sb.Append("global uniform float world_lightmap_scale;\n");
        if (forModel)
        {
            // The same per-entity values ModelTint pushes at PlayerSkinShader, declared the same way.
            // At the indices PlayerSkinShader's declaration order gives them: one mesh can carry both shaders,
            // and the engine keeps one slot per name for the whole instance.
            sb.Append("instance uniform vec3 colormod : source_color, instance_index(2) = vec3(1.0);\n");
            sb.Append("instance uniform vec3 glowmod : source_color, instance_index(3) = vec3(1.0);\n");
            sb.Append("instance uniform float grid_lit : instance_index(4) = 0.0;       // 3: EF_FULLBRIGHT / RF_FULLBRIGHT\n");
            if (!plan.FullBright)
            {
                sb.Append("global uniform sampler3D lightgrid_tex : filter_linear, repeat_disable;\n");
                sb.Append("global uniform mat4 lightgrid_matrix;\n");
                sb.Append("global uniform vec4 lightgrid_params;\n");
                sb.Append("varying vec3 lightgrid_tc;\n");
            }
        }
        bool environment = stage.TcGen is { Type: TcGenType.Environment };
        bool turbulent = stage.TcMods.Count > 0 && stage.TcMods[0].Type == TcModType.Turb;
        if (environment || turbulent) sb.Append("varying vec2 dp_uv;\n");
        if (water is not null)
        {
            sb.Append("// r_water (gl_rmain.c R_Water_*, shader_glsl.h MODE_WATER): the scene behind the surface (the refraction),\n");
            sb.Append("// a render from the mirrored eye (the reflection; the game's WaterRenderer supplies it and its matrix), mixed by\n");
            sb.Append("// the Fresnel term and drawn under the ordinary material at the shader's water alpha.\n");
            sb.Append("uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;\n");
            sb.Append("uniform sampler2D reflection_tex : hint_default_black, filter_linear, repeat_disable;\n");
            sb.Append("uniform sampler2D water_normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;\n");
            sb.Append("uniform mat4 reflection_vp;          // world to the reflection render's clip space\n");
            sb.Append("uniform bool water_norm_rg = false;  // a two-channel (BC5) normal map: z is reconstructed\n");
            sb.Append("uniform float water_on = 0.0;        // 1 while this plane's reflection is being rendered\n");
            sb.Append("uniform vec2 water_distort = vec2(0.0);   // r_water_refractdistort * refractfactor, r_water_reflectdistort * reflectfactor\n");
            sb.Append("varying vec3 dp_world;\n");
        }
        sb.Append('\n');
        sb.Append(SharedFunctions);

        // ---- vertex ----
        sb.Append("void vertex() {\n");
        sb.Append("    if (morph_amount > 0.0) {\n");
        sb.Append("        VERTEX = mix(VERTEX, CUSTOM0.xyz, morph_amount);\n");
        sb.Append("        NORMAL = normalize(mix(NORMAL, CUSTOM1.xyz, morph_amount));\n");
        sb.Append("    }\n");
        foreach (DeformVertexes d in def.Deforms) EmitDeform(sb, d);
        if (environment)
        {
            // Q3TCGEN_ENVIRONMENT (gl_rmain.c RSurf_PrepareVerticesForBatch): a sphere map from the world-space
            // reflection of the view vector; "this sphere map only uses world x and z" - Quake's y and z.
            sb.Append("    {\n");
            sb.Append("        vec3 eye_local = (inverse(MODEL_MATRIX) * INV_VIEW_MATRIX[3]).xyz - VERTEX;\n");
            sb.Append("        vec3 refl = NORMAL * 2.0 * dot(NORMAL, eye_local) - eye_local;\n");
            sb.Append("        vec3 wr = normalize(mat3(MODEL_MATRIX) * refl);\n");
            sb.Append("        dp_uv = vec2(0.5 + 0.5 * (-wr.z), 0.5 - 0.5 * wr.y);   // Quake y = -Godot z, Quake z = Godot y\n");
            sb.Append("    }\n");
        }
        else if (turbulent)
        {
            // Q3TCMOD_TURBULENT, "the only tcmod that needs software vertex processing ... and we only support
            // that as the first one": texcoord += amplitude * sin(((x + z) / 1024 + animpos) * 2 pi) and the
            // same of y, on the vertex's own position in Quake axes.
            TcMod turb = stage.TcMods[0];
            sb.Append("    {\n");
            sb.Append("        float animpos = ").Append(F(turb.P(2))).Append(" + dp_time * ").Append(F(turb.P(3))).Append(";\n");
            sb.Append("        dp_uv = UV + ").Append(F(turb.P(1))).Append(" * vec2(sin(((VERTEX.x + VERTEX.y) / 1024.0 + animpos) * 6.2831853), sin(((-VERTEX.z) / 1024.0 + animpos) * 6.2831853));\n");
            sb.Append("    }\n");
        }
        if (water is not null) sb.Append("    dp_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;\n");
        if (forModel && !plan.FullBright)
            sb.Append("    lightgrid_tc = (lightgrid_matrix * vec4((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz, 1.0)).xyz;\n");
        sb.Append("    POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);\n");
        sb.Append("    POSITION.z = mix(POSITION.z, POSITION.w, 1.0 - viewmodel_depth_range);\n");
        sb.Append("}\n\n");

        // ---- fragment ----
        sb.Append("void fragment() {\n");
        sb.Append("    vec2 uv = ").Append(environment || turbulent ? "dp_uv" : "UV").Append(";\n");
        StringBuilder mods = new();
        for (int i = turbulent ? 1 : 0; i < stage.TcMods.Count; i++)
            if (stage.TcMods[i].Type != TcModType.Turb) Q3StageGlsl.EmitTcMod(mods, stage.TcMods[i]);
        sb.Append(mods.Replace("TIME", "dp_time"));
        sb.Append("    vec4 c = texture(albedo_tex, uv);\n");
        if (frames > 1)
        {
            // LoopingFrameNumberFromDouble(shadertime * framerate, numframes)
            float fps = stage.AnimMap!.Fps > 0f ? stage.AnimMap.Fps : 1f;
            sb.Append("    int fr = int(mod(floor(dp_time * ").Append(F(fps)).Append("), ").Append(F(frames)).Append("));\n");
            for (int i = 1; i < frames; i++) sb.Append("    if (fr == ").Append(i).Append(") c = texture(anim_tex_").Append(i).Append(", uv);\n");
        }
        if (!plan.MaterialAlpha) sb.Append("    c.a = 1.0;                             // this stage's texture is loaded without TEXF_ALPHA\n");
        if (background is not null)
        {
            // USEVERTEXTEXTUREBLEND with USEBOTHALPHAS (r_glsl_vertextextureblend_usebothalphas 1, Xonotic's default).
            StringBuilder bg = new();
            foreach (TcMod m in background.TcMods)
                if (m.Type != TcModType.Turb) Q3StageGlsl.EmitTcMod(bg, m);
            sb.Append("    {\n        vec2 keep = uv;\n        uv = UV;\n").Append(bg.Replace("TIME", "dp_time"));
            sb.Append("        vec4 c2 = texture(background_tex, uv);\n        uv = keep;\n");
            if (!plan.BackgroundAlpha) sb.Append("        c2.a = 1.0;\n");
            sb.Append("        float terrainblend = max(clamp(COLOR.a * c.a, 0.0, 1.0), 1.0 - c2.a);\n");
            sb.Append("        c = vec4(mix(c2.rgb, c.rgb, terrainblend), 1.0);\n    }\n");
        }
        if (plan.VertexAlpha) sb.Append("    c.a *= COLOR.a;                       // USEALPHAGENVERTEX\n");
        if (plan.AlphaTest) sb.Append("    if (c.a < 0.5) discard;                // USEALPHAKILL\n");

        sb.Append("    vec3 rgb = c.rgb;\n");
        if (reflect)
        {
            // The reflected view vector in the cube map's axes: Quake (x, y, z) = Godot (x, -z, y).
            sb.Append("    {\n        vec3 rw = (INV_VIEW_MATRIX * vec4(reflect(-VIEW, normalize(NORMAL)), 0.0)).xyz;\n");
            sb.Append("        rgb += texture(reflect_mask, uv).rgb * texture(reflect_cube, vec3(rw.x, -rw.z, rw.y)).rgb;   // USEREFLECTCUBE\n    }\n");
        }
        if (forModel)
        {
            sb.Append("    vec3 d_mod = dp_display(colormod);\n");
            if (plan.FullBright)
                sb.Append("    rgb *= d_mod;                          // MODE_FLATCOLOR: Color_Ambient = colormod\n");
            else
            {
                // MODE_LIGHTGRID without the normal and gloss maps: diffusetex * colormod * 2 * (ambient + diffuse * directed).
                sb.Append("    if (grid_lit > 2.5 || lightgrid_params.w < 0.5) {\n        rgb *= d_mod;\n    } else {\n");
                sb.Append("        vec3 tc = vec3(lightgrid_tc.xy, clamp(lightgrid_tc.z, lightgrid_params.x, lightgrid_params.y));\n");
                sb.Append("        vec3 amb = texture(lightgrid_tex, tc).rgb * lightgrid_params.z;\n");
                sb.Append("        vec3 dif = texture(lightgrid_tex, tc + vec3(0.0, 0.0, 0.3333333)).rgb * lightgrid_params.z;\n");
                sb.Append("        vec3 dq = texture(lightgrid_tex, tc + vec3(0.0, 0.0, 0.6666667)).rgb * 2.0 - 1.0;\n");
                sb.Append("        vec3 dw = vec3(dq.x, dq.z, -dq.y);\n");
                sb.Append("        float ndl = dot(dw, dw) > 1e-6 ? max(dot(normalize(NORMAL), normalize((VIEW_MATRIX * vec4(dw, 0.0)).xyz)), 0.0) : 0.0;\n");
                sb.Append("        rgb *= d_mod * (amb + dif * ndl) * world_lightmap_scale;\n    }\n");
            }
        }
        else if (lightmapped)
            sb.Append("    rgb *= texture(lightmap_tex, UV2).rgb * (2.0 * world_lightmap_scale);   // MODE_LIGHTMAP: Color_Diffuse = 2 * lightmapintensity\n");
        else if (!plan.FullBright)
            sb.Append("    rgb *= COLOR.rgb * (2.0 * world_lightmap_scale);   // MODE_VERTEXCOLOR: Color_Diffuse = 2 * lightmapintensity\n");
        if (glow)
            sb.Append("    rgb += texture(glow_tex, uv).rgb").Append(forModel ? " * dp_display(glowmod)" : "").Append(";   // Color_Glow\n");
        sb.Append("    rgb *= map_tint;\n");

        // ---- what reaches the frame buffer. DarkPlaces blends display values. While the session's buffer holds
        // display values (DpColour.ShaderFunctions) these are DarkPlaces' own blends; when it holds linear light each
        // case hands the engine the linear value whose encoding is DarkPlaces' result where the destination
        // allows it to be known (an opaque surface, any blend over black, the multiplying blends). ----
        if (water is not null)
        {
            EmitWater(sb, plan, water);
            sb.Append("}\n");
            return sb.ToString();
        }
        switch (plan.Blend)
        {
            case DpBlend.Opaque:
                sb.Append("    ALBEDO = dp_fb(rgb);\n");
                break;
            case DpBlend.Add:
                sb.Append("    ALBEDO = dp_fb(rgb * c.a);            // GL_SRC_ALPHA GL_ONE\n    ALPHA = 1.0;\n");
                break;
            case DpBlend.Alpha:
                sb.Append("    ALBEDO = dp_fb(rgb);                  // GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA\n    ALPHA = c.a;\n");
                break;
            default:
                EmitCustom(sb, plan.CustomSrc, plan.CustomDst);
                break;
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>The render layer bit a water render's camera leaves out: a water surface is not drawn into a
    /// water render (gl_rmain.c: no recursion), which its shader does by this bit of CAMERA_VISIBLE_LAYERS.</summary>
    public const uint WaterRenderSkipBit = 1u << 17;

    // shader_glsl.h MODE_WATER, then the base pass over it (gl_rmain.c R_DrawTextureSurfaceList: RSURFPASS_BACKGROUND
    // and the material at currentalpha * r_water_wateralpha). "uv", "c" and "rgb" are the material's own.
    private static void EmitWater(StringBuilder sb, in DpMaterialPlan plan, DpWater water)
    {
        sb.Append("    if ((CAMERA_VISIBLE_LAYERS & uint(").Append(WaterRenderSkipBit).Append(")) == uint(0)) discard;   // not in a water render\n");
        sb.Append("    float va = ").Append(plan.VertexAlpha ? "COLOR.a" : "1.0").Append(";\n");
        sb.Append("    vec3 bg = texture(screen_tex, SCREEN_UV).rgb;\n");
        sb.Append("    float over = 1.0;\n");
        sb.Append("    if (water_on > 0.5) {\n");
        sb.Append("        vec3 wt = texture(water_normal_tex, uv).rgb;\n");
        sb.Append("        if (water_norm_rg) { vec2 w2 = wt.rg * 2.0 - 1.0; wt.b = sqrt(max(0.0, 1.0 - dot(w2, w2))) * 0.5 + 0.5; }\n");
        sb.Append("        vec2 wn = normalize(wt - vec3(0.5)).xy;\n");
        sb.Append("        vec2 distort = water_distort * va;\n");
        // DarkPlaces' screen coordinates have y up; SCREEN_UV has y down.
        sb.Append("        vec3 refraction = texture(screen_tex, SCREEN_UV + vec2(wn.x, -wn.y) * distort.x).rgb;\n");
        sb.Append("        vec4 rc = reflection_vp * vec4(dp_world, 1.0);\n");
        // DarkPlaces shifts the lookup on the SCREEN; the reflection here is a texture spread over the water, so
        // the same shift is taken through the lookup's screen-space derivatives.
        sb.Append("        vec2 rbase = rc.xy / max(rc.w, 1e-5) * vec2(0.5, -0.5) + 0.5;\n");
        sb.Append("        vec2 rshift = dFdx(rbase) * (wn.x * distort.y * VIEWPORT_SIZE.x) - dFdy(rbase) * (wn.y * distort.y * VIEWPORT_SIZE.y);\n");
        sb.Append("        vec2 ruv = clamp(rbase + rshift, vec2(0.002), vec2(0.998));\n");
        sb.Append("        vec3 reflection = texture(reflection_tex, ruv).rgb;\n");
        sb.Append("        float fresnel = pow(min(1.0, 1.0 - abs(dot(normalize(VIEW), normalize(NORMAL)))), 2.0) * ").Append(F(water.ReflectMax - water.ReflectMin))
          .Append(" * va + ").Append(F(water.ReflectMin)).Append(" * va;\n");
        sb.Append("        vec3 refractcolor = vec3(").Append(F(water.RefractR)).Append(", ").Append(F(water.RefractG)).Append(", ").Append(F(water.RefractB)).Append(");\n");
        if (plan.VertexAlpha) sb.Append("        refractcolor = mix(refractcolor, vec3(1.0), va);   // USEALPHAGENVERTEX\n");
        sb.Append("        bg = mix(refraction * refractcolor, reflection * vec3(").Append(F(water.ReflectR)).Append(", ").Append(F(water.ReflectG)).Append(", ").Append(F(water.ReflectB)).Append("), clamp(fresnel, 0.0, 1.0));\n");
        sb.Append("        over = ").Append(F(water.WaterAlpha)).Append(";   // r_water_wateralpha: the dp_water alpha\n");
        sb.Append("    }\n");
        if (plan.Blend == DpBlend.Add || (plan.Blend == DpBlend.Custom && plan.CustomDst == BlendFactor.One))
            sb.Append("    ALBEDO = clamp(bg + rgb * (c.a * over), vec3(0.0), vec3(1.0));   // the material, GL_SRC_ALPHA GL_ONE, over the water\n");
        else
            sb.Append("    ALBEDO = mix(bg, rgb, clamp(c.a * over, 0.0, 1.0));   // the material, GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA, over the water\n");
        sb.Append("    ALPHA = 1.0;\n");
    }

    private static string CustomBlend(BlendFactor src, BlendFactor dst) => (src, dst) switch
    {
        (BlendFactor.DstColor, BlendFactor.Zero) or (BlendFactor.Zero, BlendFactor.SrcColor) => "blend_mul",
        (BlendFactor.DstColor, BlendFactor.SrcColor) => "blend_mul",
        (BlendFactor.Zero, BlendFactor.OneMinusSrcColor) => "blend_mul",
        (BlendFactor.One, BlendFactor.OneMinusSrcAlpha) => "blend_premul_alpha",
        (BlendFactor.DstColor, BlendFactor.One) or (BlendFactor.One, BlendFactor.SrcColor) => "blend_add",
        (_, BlendFactor.One) => "blend_add",
        _ => "blend_mix",
    };

    private static void EmitCustom(StringBuilder sb, BlendFactor src, BlendFactor dst)
    {
        switch (src, dst)
        {
            case (BlendFactor.DstColor, BlendFactor.Zero):
            case (BlendFactor.Zero, BlendFactor.SrcColor):
                sb.Append("    ALBEDO = dp_fb_factor(rgb);            // modulate: dst * src\n    ALPHA = 1.0;\n");
                break;
            case (BlendFactor.DstColor, BlendFactor.SrcColor):
                sb.Append("    ALBEDO = dp_fb_factor(rgb * 2.0);      // modulate x2: dst * src * 2\n    ALPHA = 1.0;\n");
                break;
            case (BlendFactor.Zero, BlendFactor.OneMinusSrcColor):
                sb.Append("    ALBEDO = dp_fb_factor(vec3(1.0) - rgb); // dst * (1 - src)\n    ALPHA = 1.0;\n");
                break;
            case (BlendFactor.One, BlendFactor.OneMinusSrcAlpha):
                sb.Append("    ALBEDO = dp_fb(rgb);                  // src + dst * (1 - alpha)\n    ALPHA = c.a;\n");
                break;
            case (BlendFactor.DstColor, BlendFactor.One):
                sb.Append("    ALBEDO = dp_fb(rgb);                  // brighten (dst * src + dst), taken as an add\n    ALPHA = 1.0;\n");
                break;
            default:
                if (dst == BlendFactor.One) sb.Append("    ALBEDO = dp_fb(rgb);\n    ALPHA = 1.0;\n");
                else sb.Append("    ALBEDO = dp_fb(rgb);\n    ALPHA = c.a;\n");
                break;
        }
    }

    // deformVertexes wave / move / bulge: gl_rmain.c RSurf_PrepareVerticesForBatch (Q3DEFORM_WAVE and friends).
    private static void EmitDeform(StringBuilder sb, DeformVertexes d)
    {
        switch (d.Type)
        {
            case DeformType.Wave:
            {
                WaveForm w = d.Wave ?? new WaveForm();
                // "this is how a divisor of vertex influence on deformation": animpos = 1 / parms[0] (100 for 0),
                // the wave's phase is offset by (x + y + z) * animpos, the vertex moves along its normal.
                float spread = d.Parms.Length > 0 ? d.Parms[0] : 0f;
                float inv = MathF.Abs(spread) > 1e-6f ? 1f / spread : 100f;
                sb.Append("    VERTEX += NORMAL * ").Append(Q3StageGlsl.WaveExprPhased(w, "(dot(VERTEX, vec3(1.0, 1.0, -1.0)) * " + F(inv) + ")").Replace("TIME", "dp_time")).Append(";   // deformVertexes wave\n");
                break;
            }
            case DeformType.Move:
            {
                WaveForm w = d.Wave ?? new WaveForm();
                float x = d.Parms.Length > 0 ? d.Parms[0] : 0f, y = d.Parms.Length > 1 ? d.Parms[1] : 0f, z = d.Parms.Length > 2 ? d.Parms[2] : 0f;
                // The offset is given in Quake axes: Godot = (x, z, -y).
                sb.Append("    VERTEX += vec3(").Append(F(x)).Append(", ").Append(F(z)).Append(", ").Append(F(-y)).Append(") * ")
                  .Append(Q3StageGlsl.WaveExpr(w).Replace("TIME", "dp_time")).Append(";   // deformVertexes move\n");
                break;
            }
            case DeformType.Bulge:
            {
                float width = d.Parms.Length > 0 ? d.Parms[0] : 0f, height = d.Parms.Length > 1 ? d.Parms[1] : 0f, speed = d.Parms.Length > 2 ? d.Parms[2] : 0f;
                sb.Append("    VERTEX += NORMAL * (sin(UV.x * ").Append(F(width)).Append(" + dp_time * ").Append(F(speed)).Append(") * ").Append(F(height)).Append(");   // deformVertexes bulge\n");
                break;
            }
            // Q3DEFORM_AUTOSPRITE needs each quad's centre, which a merged mesh does not carry: a model's
            // autosprite surfaces are built by ShaderCompiler.CompileAutosprite, the level's stay as modelled.
        }
    }

    private static string F(float v) => Q3StageGlsl.Flt(v);

    // dp_fb(display): what to write for a display colour - the colour itself while the session's buffer holds
    // display values, its decoding when the buffer is linear (VortexArena.Game.Client.DisplayFramebuffer).
    private const string SharedFunctions = DpColour.ShaderFunctions + "\n";
}
