// The colour space of a legacy session's picture, and the developer aid that was used to compare it with
// DarkPlaces frame for frame (planning/specs/legacy-compat.md, "Colour").
//
// DarkPlaces with Xonotic's default configuration (vid_sRGB 0, mod_q3bsp_sRGBlightmaps 0: sRGB-disable.cfg,
// executed by xonotic-client.cfg) never converts a texel: the 8-bit values of textures, lightmaps and the light
// grid are multiplied as they are, and the product is what the screen shows. The native game multiplies in
// linear light instead, which is darker and more saturated for the same data. A legacy session draws
// Xonotic's data, so it asks the shared shaders for DarkPlaces' arithmetic while it runs and gives the
// native setting back when it ends.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    private bool _colourApplied;

    /// <summary>Switches the shared world and model shaders to DarkPlaces' gamma-space arithmetic (see the top
    /// of this file). Called when the session's scene is set up; <see cref="RestoreNativeColour"/> undoes it.</summary>
    private void ApplyLegacyColour()
    {
        if (_colourApplied) return;
        _colourApplied = true;
        LegacyColour.Enter();
    }

    /// <summary>
    /// The environment of a session whose 3D buffer holds display values (DisplayFramebuffer): the colour
    /// correction table that cancels the output transform's encoding, and none of the engine's own ambient
    /// light or sun - every model is lit from the level's light grid by its shader, as in DarkPlaces, and
    /// would otherwise get both on top (the model shaders hand the engine an albedo for the dynamic lights).
    /// </summary>
    private void ApplyDisplayBuffer(Godot.Environment env, bool levelHasLightGrid)
    {
        if (!DisplayFramebuffer.Active) return;
        env.AdjustmentEnabled = true;
        env.AdjustmentBrightness = 1f;
        env.AdjustmentContrast = 1f;
        env.AdjustmentSaturation = 1f;
        env.AdjustmentColorCorrection = DisplayFramebuffer.InverseOutputTable();
        env.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
        env.AmbientLightEnergy = 0f;
        if (env.FogEnabled) env.FogLightColor = DisplayFramebuffer.ForEngine(env.FogLightColor);
        if (_sun is not null) _sun.Visible = !levelHasLightGrid;
    }

    // R_BeginPolygon's material: the picture's stored texel times the vertex colour, DRAWFLAG_ADDITIVE as
    // GL_SRC_ALPHA GL_ONE and everything else as GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA, written for the
    // session's buffer convention (dp_fb).
    private static Shader? s_polygonMix, s_polygonAdd;
    private static Shader PolygonShader(bool additive)
    {
        ref Shader? slot = ref additive ? ref s_polygonAdd : ref s_polygonMix;
        return slot ??= new Shader
        {
            Code = "// VortexArena legacy polygon shader (R_BeginPolygon). Generated in C#.\n" +
                "shader_type spatial;\n" +
                "render_mode unshaded, cull_disabled, shadows_disabled, depth_draw_never, " + (additive ? "blend_add" : "blend_mix") + ";\n" +
                "uniform sampler2D picture : hint_default_white, filter_linear_mipmap, repeat_enable;\n" +
                DisplayFramebuffer.ShaderFunctions +
                "void fragment() {\n" +
                "    vec4 c = texture(picture, UV) * COLOR;\n" +
                (additive
                    ? "    ALBEDO = dp_fb(c.rgb * c.a);\n    ALPHA = 1.0;\n"
                    : "    ALBEDO = dp_fb(c.rgb);\n    ALPHA = c.a;\n") +
                "}\n",
        };
    }

    private float _lightStyleApplied = 1f;

    // r_refdef.scene.lightmapintensity for a Quake 3 level: the value of light style 0 at the session's time
    // (DpColour.LightStyleValue; 1.03125 for the "m" a stock server sends). It scales every lightmapped and
    // vertex-lit surface and every grid-lit model through the world shaders' lightmap-intensity global.
    private void ApplyLightStyle(double time)
    {
        float value = VortexArena.Formats.Materials.DpColour.LightStyleValue(_lightStyles[0], time);
        if (!float.IsFinite(value)) value = 1f;
        value = Math.Clamp(value, 0f, 4f);
        if (value == _lightStyleApplied) return;
        _lightStyleApplied = value;
        RenderingServer.GlobalShaderParameterSet(WorldLightRenderer.LightmapScaleUniform, value);
    }

    private void RestoreNativeColour()
    {
        if (_lightStyleApplied != 1f)
        {
            _lightStyleApplied = 1f;
            RenderingServer.GlobalShaderParameterSet(WorldLightRenderer.LightmapScaleUniform, 1f);
        }
        if (!_colourApplied) return;
        _colourApplied = false;
        LegacyColour.Leave();
    }

    private bool _effectsFrozen;

    /// <summary>A paused recording (cls.demopaused): the particles and the lights of effects stop where they
    /// are, as DarkPlaces' do when cl.time stands still, so that a held frame can be photographed.</summary>
    public void FreezeEffects(bool frozen)
    {
        if (frozen == _effectsFrozen) return;
        _effectsFrozen = frozen;
        _effects.ProcessMode = frozen ? Node.ProcessModeEnum.Disabled : Node.ProcessModeEnum.Inherit;
    }

    /// <summary>
    /// The frame's particle step, started as soon as the client program has run instead of when the effect
    /// system's node gets its turn later in the frame. Nothing spawns a particle in between (spawns come from
    /// the server's messages and from the program, both done by now), so the particles are the same; what
    /// changes is that their update, which runs on its own thread, has the rest of the frame to finish in.
    /// </summary>
    public void AdvanceParticles(double delta)
    {
        if (_effectsFrozen || _effects.FaithfulParticles is not { } particles || !particles.IsInsideTree() || !particles.CanProcess()) return;
        particles.Advance(delta);
    }

    // ---- developer aid: only reachable from a review script (VORTEX_LEGACY_SCRIPT) -----------------------

    /// <summary>"colourdbg &lt;what&gt; [value]" in a review script: one switch of the picture, or "dump" for the
    /// materials in view. Used to take the picture apart beside DarkPlaces' r_fullbright / gl_lightmaps.</summary>
    public void ColourDebug(string arguments)
    {
        string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        float value = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 1;
        bool on = value != 0;
        switch (parts[0])
        {
            case "dump": DumpMaterials(); break;
            case "cvar" when parts.Length >= 3: _cvars.Set(parts[1], parts[2]); break;   // a session cvar, e.g. r_water
            case "fullbright": RenderingServer.GlobalShaderParameterSet("world_nolightmaps", on ? 1f : 0f); LegacyColour.DebugFullbright(on); break;
            case "lightmaponly": LegacyColour.DebugLightmapOnly(on); break;
            case "gamma": LegacyColour.DebugForce(on); break;
            case "deluxe": RenderingServer.GlobalShaderParameterSet("deluxe_enabled", on ? 1f : 0f); break;
            case "gloss": LegacyColour.DebugGloss(on); break;
            case "dlight": WorldTint.SetWorldDlight(on ? 1f : 0f); break;
            case "hud": s_noHud = !on; _drawLayer.Visible = on; break;
            case "particles": s_noEffects = !on; _effects.Visible = on; break;
            case "ents": s_noEntities = !on; break;
            case "world": s_noWorld = !on; break;
            case "msaa": if (_sceneRoot.GetViewport() is { } vp) vp.Msaa3D = on ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Disabled; break;
            case "sun": if (_sun is not null) _sun.Visible = on; break;
            case "ambient": if (_environment?.Environment is { } env) env.AmbientLightEnergy = value; break;
            default: _note("colourdbg: unknown switch " + parts[0]); break;
        }
        _note("colourdbg " + arguments);
    }

    private void DumpMaterials()
    {
        Dictionary<string, (int Count, string Example)> rows = new();
        Walk(_sceneRoot);
        List<KeyValuePair<string, (int Count, string Example)>> sorted = new(rows);
        sorted.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
        _note($"colourdbg dump: {sorted.Count} kinds of material on visible geometry");
        foreach ((string kind, (int count, string example)) in sorted) _note($"  x{count,4} {kind}   e.g. {example}");

        void Walk(Node node)
        {
            if (node is Node3D { Visible: false }) return;
            if (node is MeshInstance3D { Mesh: { } mesh } instance)
            {
                for (int s = 0, n = mesh.GetSurfaceCount(); s < n; s++)
                    Add(instance.MaterialOverride ?? instance.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s), instance);
            }
            else if (node is MultiMeshInstance3D { Multimesh.Mesh: { } multi } many)
            {
                for (int s = 0, n = multi.GetSurfaceCount(); s < n; s++) Add(many.MaterialOverride ?? multi.SurfaceGetMaterial(s), many);
            }
            else if (node is GeometryInstance3D geometry) Add(geometry.MaterialOverride, geometry);
            foreach (Node child in node.GetChildren()) Walk(child);
        }

        void Add(Material? material, Node3D owner)
        {
            string kind = Describe(material) + " on " + owner.GetType().Name;
            string where = PathTail(owner);
            rows[kind] = rows.TryGetValue(kind, out (int Count, string Example) row) ? (row.Count + 1, row.Example) : (1, where);
        }

        static string PathTail(Node node)
        {
            StringBuilder path = new();
            Node? at = node;
            for (int i = 0; i < 4 && at is not null; i++, at = at.GetParent()) path.Insert(0, "/" + at.Name);
            return path.ToString();
        }

        static string Describe(Material? material)
        {
            switch (material)
            {
                case null: return "(no material)";
                case ShaderMaterial shaded:
                    Shader? shader = shaded.Shader;
                    if (shader is null) return "ShaderMaterial(no shader)";
                    if (LightmapShader.IsLightmapShader(shader)) return "LightmapShader";
                    if (shader == PlayerSkinShader.Shader) return "PlayerSkinShader";
                    if (shader == Md3MorphShader.Shader) return "Md3MorphShader";
                    string code = shader.Code;
                    int end = code.IndexOf('\n');
                    string first = end > 0 ? code[..Math.Min(end, 90)] : "?";
                    int mode = code.IndexOf("render_mode", StringComparison.Ordinal);
                    string modeLine = mode >= 0 ? code[mode..Math.Min(code.Length, code.IndexOf(';', mode) + 1)] : "";
                    return $"ShaderMaterial[{first.Trim()} | {modeLine}]";
                case BaseMaterial3D standard:
                    return $"{standard.GetClass()}[{standard.ShadingMode}, blend {standard.BlendMode}, transparency {standard.Transparency}, vertexcolor {standard.VertexColorUseAsAlbedo}, emission {standard.EmissionEnabled}, albedo {standard.AlbedoColor}, texture {(standard.AlbedoTexture is null ? "none" : "yes")}]";
                default: return material.GetClass();
            }
        }
    }
}

/// <summary>
/// The process-wide switches behind a legacy session's colour: global shader parameters, so one call reaches
/// every material of the shared shaders. All of them are at the native value outside a session.
/// </summary>
public static class LegacyColour
{
    private static int s_sessions;

    /// <summary>True while a legacy session has the shared shaders on DarkPlaces' arithmetic.</summary>
    public static bool Active => s_sessions > 0;

    public static void Enter()
    {
        if (s_sessions++ > 0) return;
        Push(true);
    }

    public static void Leave()
    {
        if (s_sessions <= 0 || --s_sessions > 0) return;
        // Back to what the native game asked for (NativeColour: DarkPlaces' arithmetic by default).
        if (NativeColour.Enabled) NativeColour.Push(true); else Push(false);
    }

    // Developer aid, as the other VORTEX_LEGACY_* variables: VORTEX_LEGACY_LINEARFB=1 leaves the 3D buffer in
    // linear light (the session's shaders then decode what they computed): the other arm of a comparison of
    // the blended surfaces, the fog and the dynamic lights. An environment variable, so no server can set it.
    private static readonly bool s_linearBuffer = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_LINEARFB"));

    private static void Push(bool legacy)
    {
        WorldTint.EnsureRegistered();
        RenderingServer.GlobalShaderParameterSet(LightmapShader.GammaSpaceUniform, legacy ? 1f : 0f);
        WorldTint.SetLegacyModelLight(legacy);
        ModelLighting.SetDarkPlacesScale(legacy);
        DisplayFramebuffer.Set(legacy && !s_linearBuffer);
    }

    // ---- developer aid (review scripts only) ----
    public static void DebugForce(bool legacy) => Push(legacy);
    public static void DebugLightmapOnly(bool on) => RenderingServer.GlobalShaderParameterSet(LightmapShader.GammaSpaceUniform, on ? 2f : 1f);
    public static void DebugFullbright(bool on) => WorldTint.SetLegacyModelLight(true, on);
    public static void DebugGloss(bool on) => RenderingServer.GlobalShaderParameterSet(LightmapShader.GammaSpaceUniform, on ? 1f : 3f);
}
