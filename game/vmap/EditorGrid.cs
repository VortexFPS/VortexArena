using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// The world-space alignment grid (design doc §11.4): a grid fixed in WORLD space that geometry receives —
/// every surface shows the lines where the global grid planes cut through it, so a wall, a slope and a stair
/// all display the same lattice and you can line things up across the whole map.
///
/// It is deliberately NOT a texture and NOT a screen overlay. A projected texture would move with the surface
/// and scale with its UVs; a screen overlay would float in front of the level instead of lying on it. Instead
/// this is a single full-screen pass that reads the depth buffer, reconstructs each pixel's WORLD position,
/// and lights the pixel when that position is close to a grid plane. Consequences worth knowing:
/// <list type="bullet">
///   <item>Every visible surface is covered automatically — brushes, patches, props, player models — with no
///         per-material work and no second copy of the geometry.</item>
///   <item>Line thickness is derivative-based (<c>fwidth</c>), so lines stay about one pixel wide however far
///         away or however oblique the surface is, instead of aliasing into a shimmering mess.</item>
///   <item>It costs one full-screen pass, and only while enabled.</item>
/// </list>
///
/// Grid spacing is in Quake units. The Quake→Godot axis map (<see cref="Coords.ToGodot"/>) only permutes and
/// negates axes at 1:1 scale, and the grid test is symmetric about the origin, so evaluating it directly in
/// Godot world space yields exactly the Quake-aligned grid a mapper expects.
/// </summary>
public sealed partial class EditorGrid : MeshInstance3D
{
    /// <summary>Cvar: master on/off for the world grid (toggled by the <c>editor_grid</c> bind).</summary>
    public const string CvarEnabled = "cl_editor_grid";

    /// <summary>Cvar: grid spacing in Quake units (Radiant's power-of-two ladder, 1..1024).</summary>
    public const string CvarSize = "cl_editor_grid_size";

    /// <summary>Cvar: how many minor lines make one brighter major line (Radiant shows a heavier line every 8).</summary>
    public const string CvarMajorEvery = "cl_editor_grid_major";

    /// <summary>Cvar: distance in Quake units at which the grid starts fading out.</summary>
    public const string CvarFadeStart = "cl_editor_grid_fade_start";

    /// <summary>Cvar: distance in Quake units at which the grid has fully faded.</summary>
    public const string CvarFadeEnd = "cl_editor_grid_fade_end";

    /// <summary>Smallest and largest spacing the size ladder will step to.</summary>
    public const float MinSize = 1f;
    public const float MaxSize = 1024f;

    private ShaderMaterial? _material;

    /// <summary>Register the grid's client-side cvar defaults. All are user preferences, so all are saved.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Register(CvarEnabled, "0", CvarFlags.Save);
        c.Register(CvarSize, "64", CvarFlags.Save);
        c.Register(CvarMajorEvery, "8", CvarFlags.Save);
        c.Register(CvarFadeStart, "1024", CvarFlags.Save);
        c.Register(CvarFadeEnd, "6144", CvarFlags.Save);
    }

    public override void _Ready()
    {
        Name = "EditorGrid";

        // A unit quad whose vertex shader rewrites POSITION into clip space, covering the screen. The custom
        // AABB is enormous so the quad is never frustum-culled out of existence when the camera moves.
        Mesh = new QuadMesh { Size = new Vector2(2f, 2f) };
        CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f));
        CastShadow = ShadowCastingSetting.Off;
        GIMode = GIModeEnum.Disabled;

        _material = new ShaderMaterial { Shader = BuildShader() };
        MaterialOverride = _material;

        Visible = false;
        ProcessPriority = 100; // parameters refresh after gameplay has settled for the frame
    }

    public override void _Process(double delta)
    {
        using var _scope = Client.FrameProfiler.Scope("editorgrid");

        if (_material is null)
            return;

        bool enabled = Cvar(CvarEnabled, 0f) != 0f;
        Visible = enabled;
        if (!enabled)
            return;

        float size = Mathf.Clamp(Cvar(CvarSize, 64f), MinSize, MaxSize);
        _material.SetShaderParameter("grid_size", size);
        _material.SetShaderParameter("major_every", MathF.Max(1f, Cvar(CvarMajorEvery, 8f)));
        _material.SetShaderParameter("fade_start", MathF.Max(0f, Cvar(CvarFadeStart, 1024f)));
        _material.SetShaderParameter("fade_end", MathF.Max(1f, Cvar(CvarFadeEnd, 6144f)));
    }

    private static float Cvar(string name, float fallback)
    {
        if (Menu.MenuState.Cvars is not { } cvars)
            return fallback;
        string s = cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : cvars.GetFloat(name);
    }

    // =============================================================================================
    //  Console commands
    // =============================================================================================

    /// <summary>
    /// Register <c>editor_grid</c> (toggle) and <c>editor_grid_size</c> (step or set). Both are client-side:
    /// the grid is a viewing aid with no server-visible effect.
    /// </summary>
    public static void RegisterCommands(Common.Config.ConfigInterpreter interp, CvarService cvars)
    {
        ArgumentNullException.ThrowIfNull(interp);
        ArgumentNullException.ThrowIfNull(cvars);

        interp.RegisterCommand("editor_grid", argv =>
        {
            bool on = cvars.GetFloat(CvarEnabled) == 0f;
            if (argv.Count >= 2 && float.TryParse(argv[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float explicitValue))
                on = explicitValue != 0f;
            cvars.Set(CvarEnabled, on ? "1" : "0");
            Log.Info($"world grid {(on ? "ON" : "OFF")} ({Fmt(cvars.GetFloat(CvarSize))}u)");
        });

        interp.RegisterCommand("editor_grid_size", argv =>
        {
            float current = Mathf.Clamp(cvars.GetFloat(CvarSize), MinSize, MaxSize);
            float next = current;

            string arg = argv.Count >= 2 ? argv[1] : "";
            if (arg is "+" or "up")
                next = MathF.Min(MaxSize, current * 2f);
            else if (arg is "-" or "down")
                next = MathF.Max(MinSize, current / 2f);
            else if (float.TryParse(arg, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out float exact))
                next = Mathf.Clamp(exact, MinSize, MaxSize);
            else if (arg.Length > 0)
            {
                Log.Help("usage: editor_grid_size [ + | - | <units> ]");
                return;
            }

            cvars.Set(CvarSize, Fmt(next));
            Log.Info($"grid size {Fmt(next)}u");
        });
    }

    private static string Fmt(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // =============================================================================================
    //  Shader
    // =============================================================================================

    private static Shader BuildShader() => new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, depth_test_disabled, fog_disabled;

            // The scene depth buffer, from which each pixel's world position is reconstructed.
            uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

            uniform float grid_size    = 64.0;   // spacing in world (Quake) units
            uniform float major_every  = 8.0;    // a heavier line every N minor lines
            uniform float line_px      = 1.1;    // line half-width in pixels
            uniform float fade_start   = 1024.0; // distance where the grid begins to fade
            uniform float fade_end     = 6144.0; // distance where it is gone

            uniform vec3 minor_color : source_color = vec3(0.38, 0.78, 1.0);
            uniform vec3 major_color : source_color = vec3(0.75, 0.95, 1.0);
            uniform float minor_alpha = 0.20;
            uniform float major_alpha = 0.42;

            void vertex() {
                // Full-screen: ignore the quad's transform and emit clip-space coordinates directly.
                POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
            }

            // Coverage of the nearest grid plane along any axis, anti-aliased to a constant pixel width via
            // screen-space derivatives of the world position.
            float grid_coverage(vec3 w, vec3 dw, float spacing) {
                vec3 dist  = abs(fract(w / spacing + 0.5) - 0.5) * spacing;
                vec3 pixels = dist / max(dw, vec3(1e-6));
                float nearest = min(min(pixels.x, pixels.y), pixels.z);
                return 1.0 - smoothstep(0.0, line_px, nearest);
            }

            void fragment() {
                float depth = texture(depth_tex, SCREEN_UV).x;

                // Reverse-Z: the far plane reads 0, i.e. sky / nothing drawn. The grid only lives on surfaces.
                if (depth <= 0.0) {
                    discard;
                }

                vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, depth);
                vec4 view = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
                view.xyz /= view.w;
                vec3 world = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;

                // Derivatives of the reconstructed position: how much world space one pixel spans here. This
                // is what keeps lines one pixel wide on a distant floor and on a wall seen edge-on alike.
                vec3 dw = fwidth(world);

                float minor = grid_coverage(world, dw, grid_size);
                float major = grid_coverage(world, dw, grid_size * major_every);

                // Distance fade, so far geometry does not turn into a solid wash of lines.
                float dist = length(view.xyz);
                float fade = 1.0 - smoothstep(fade_start, fade_end, dist);
                if (fade <= 0.0) {
                    discard;
                }

                // A major line wins where the two coincide.
                vec3  color = mix(minor_color, major_color, major);
                float alpha = max(minor * minor_alpha, major * major_alpha) * fade;
                if (alpha <= 0.001) {
                    discard;
                }

                ALBEDO = color;
                ALPHA = alpha;
            }
            """,
    };
}
