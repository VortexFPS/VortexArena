using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vmap;

namespace VortexArena.Game.Vmap;

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

    /// <summary>Cvar: spacing of the DRAWN grid in Quake units (Radiant's power-of-two ladder, 1..1024).</summary>
    public const string CvarSize = "cl_editor_grid_size";

    /// <summary>
    /// Cvar: spacing of the ALIGNMENT grid — what an edit snaps to (backlog T3).
    ///
    /// Split from the drawn size because they answer different questions. The drawn grid is a reference you
    /// want readable, so on a big room you coarsen it until the lines stop being noise; the alignment grid is
    /// a constraint you want tight, and coarsening it to see better silently starts rounding your work. One
    /// value forced every mapper to trade one against the other.
    /// </summary>
    public const string CvarSnapSize = "cl_editor_grid_snap_size";

    /// <summary>Cvar: whether edits snap to the alignment grid at all. Independent of whether it is DRAWN.</summary>
    public const string CvarSnapEnabled = "cl_editor_grid_snap";

    /// <summary>Cvar: how many minor lines make one brighter major line (Radiant shows a heavier line every 8).</summary>
    public const string CvarMajorEvery = "cl_editor_grid_major";

    /// <summary>Cvar: distance in Quake units at which the grid starts fading out.</summary>
    public const string CvarFadeStart = "cl_editor_grid_fade_start";

    /// <summary>Cvar: distance in Quake units at which the grid has fully faded.</summary>
    public const string CvarFadeEnd = "cl_editor_grid_fade_end";

    /// <summary>Smallest and largest spacing the size ladder will step to.</summary>
    public const float MinSize = 1f;
    public const float MaxSize = 1024f;

    // C2 STANDING RULE (godot#105750 / planning/PERFORMANCE_REPORT.md C2), same as PlayerSkinShader: these are
    // `static readonly StringName`, not `const string`. SetShaderParameter takes a StringName, so a string
    // literal there mints an allocation per call — and _Process pushes all nine every frame the grid is on.
    // The XG0002 analyzer flags a literal reaching a StringName API from _Process/_PhysicsProcess/_Draw.

    /// <summary>Uniforms: grid spacing, major-line interval, and the two fade distances.</summary>
    private static readonly StringName GridSizeUniform = "grid_size";
    private static readonly StringName MajorEveryUniform = "major_every";
    private static readonly StringName FadeStartUniform = "fade_start";
    private static readonly StringName FadeEndUniform = "fade_end";

    /// <summary>Uniforms: per-line-class opacity, scaled down while the ortho view carries the wireframe.</summary>
    private static readonly StringName MinorAlphaUniform = "minor_alpha";
    private static readonly StringName MajorAlphaUniform = "major_alpha";

    /// <summary>Uniforms: the highlighted face's plane and tint (the plane being hovered or dragged).</summary>
    private static readonly StringName HlActiveUniform = "hl_active";
    private static readonly StringName HlPlaneUniform = "hl_plane";
    private static readonly StringName HlColorUniform = "hl_color";

    private ShaderMaterial? _material;
    private EditorController? _controller;

    /// <summary>Point the grid at the editor controller so it can tint the plane under the crosshair.</summary>
    public void Attach(EditorController controller) => _controller = controller;

    /// <summary>Register the grid's client-side cvar defaults. All are user preferences, so all are saved.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Register(CvarEnabled, "0", CvarFlags.Save);
        c.Register(CvarSize, "64", CvarFlags.Save);
        c.Register(CvarSnapSize, "16", CvarFlags.Save);
        c.Register(CvarSnapEnabled, "1", CvarFlags.Save);
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
        _material.SetShaderParameter(GridSizeUniform, size);
        _material.SetShaderParameter(MajorEveryUniform, MathF.Max(1f, Cvar(CvarMajorEvery, 8f)));
        _material.SetShaderParameter(FadeStartUniform, MathF.Max(0f, Cvar(CvarFadeStart, 1024f)));
        _material.SetShaderParameter(FadeEndUniform, MathF.Max(1f, Cvar(CvarFadeEnd, 6144f)));

        // Tint the grid ON the face being worked with, so the plane you are about to move reads distinctly from
        // the rest of the world grid. Uses the DRAG selection while dragging (that is the plane actually moving)
        // and the hover otherwise.
        bool active = false;
        var plane = new Vector4(0f, 0f, 1f, 0f);
        var tint = new Vector3(1f, 0.85f, 0.3f);   // amber, matching the hover outline

        if (_controller is { Active: true, Document: not null } c)
        {
            VmapSelection sel = c.IsDragging ? c.DragSelection : c.Hover.Selection;
            if (sel.Kind == VmapSelectionKind.Face && c.Document.FindBrush(sel.BrushId) is { } brush
                && sel.FaceIndex >= 0 && sel.FaceIndex < brush.Faces.Count)
            {
                VmapPlane p = brush.Faces[sel.FaceIndex].Plane;
                // The shader works in Godot space; convert the plane's normal and re-anchor its distance
                // through a point on it, because ToGodot permutes axes and a raw distance would not survive.
                Vector3 n = Coords.ToGodot(p.Normal);
                Vector3 onPlane = Coords.ToGodot(p.Normal * p.Dist);
                plane = new Vector4(n.X, n.Y, n.Z, n.Dot(onPlane));
                active = true;
                if (c.IsDragging)
                    tint = new Vector3(0.4f, 1f, 0.55f);   // green while actually moving it
            }
        }

        // The ortho view carries the geometry as a wireframe, so a full-strength grid competes with it there.
        float alphaScale = _controller?.Ortho is { IsOpen: true } ? EditorOrthoView.GridAlphaScale : 1f;
        _material.SetShaderParameter(MinorAlphaUniform, 0.20f * alphaScale);
        _material.SetShaderParameter(MajorAlphaUniform, 0.42f * alphaScale);

        _material.SetShaderParameter(HlActiveUniform, active ? 1f : 0f);
        _material.SetShaderParameter(HlPlaneUniform, plane);
        _material.SetShaderParameter(HlColorUniform, tint);
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
            StepSizeCommand(cvars, CvarSize, argv, "grid size", "editor_grid_size"));

        interp.RegisterCommand("editor_grid_snap_size", argv =>
            StepSizeCommand(cvars, CvarSnapSize, argv, "alignment grid", "editor_grid_snap_size"));

        interp.RegisterCommand("editor_grid_snap", argv =>
        {
            bool on = cvars.GetFloat(CvarSnapEnabled) == 0f;
            if (argv.Count >= 2 && float.TryParse(argv[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float explicitValue))
                on = explicitValue != 0f;
            cvars.Set(CvarSnapEnabled, on ? "1" : "0");
            Log.Info($"grid snapping {(on ? "ON" : "OFF")} ({Fmt(cvars.GetFloat(CvarSnapSize))}u)");
        });
    }

    /// <summary>
    /// The shared <c>[ + | - | &lt;units&gt; ]</c> handler behind both size commands. One implementation so the
    /// drawn grid and the alignment grid step the same ladder — two copies would drift the moment either
    /// gained a bound or a rounding rule.
    /// </summary>
    private static void StepSizeCommand(
        CvarService cvars, string cvar, IReadOnlyList<string> argv, string label, string usage)
    {
        float current = Mathf.Clamp(cvars.GetFloat(cvar), MinSize, MaxSize);
        string arg = argv.Count >= 2 ? argv[1] : "";

        float next;
        if (arg is "+" or "up")
            next = VmapEdit.StepGridSize(current, +1, MinSize, MaxSize);
        else if (arg is "-" or "down")
            next = VmapEdit.StepGridSize(current, -1, MinSize, MaxSize);
        else if (float.TryParse(arg, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out float exact))
            next = Mathf.Clamp(exact, MinSize, MaxSize);
        else if (arg.Length > 0)
        {
            Log.Help($"usage: {usage} [ + | - | <units> ]");
            return;
        }
        else
        {
            next = current;
        }

        cvars.Set(cvar, Fmt(next));
        Log.Info($"{label} {Fmt(next)}u");
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

            uniform float hl_active = 0.0;         // 1 while a face is hovered/dragged
            uniform vec4  hl_plane = vec4(0.0, 0.0, 1.0, 0.0);  // xyz = normal, w = distance (Godot space)
            uniform vec3  hl_color : source_color = vec3(1.0, 0.85, 0.3);
            uniform float hl_band = 1.5;           // how close to the plane counts as "on" it, in world units

            uniform vec3 minor_color : source_color = vec3(0.38, 0.78, 1.0);
            uniform vec3 major_color : source_color = vec3(0.75, 0.95, 1.0);
            uniform float minor_alpha = 0.20;
            uniform float major_alpha = 0.42;

            void vertex() {
                // Full-screen: ignore the quad's transform and emit clip-space coordinates directly.
                POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
            }

            // Coverage of the nearest grid plane, measured in PIXELS via screen-space derivatives of the world
            // position so a line is the same width at any distance or angle. Two guards matter here:
            //
            //  * An axis that does not VARY across this surface is excluded. A floor's world Z is constant, and
            //    level geometry sits exactly on grid multiples by construction — so without this the Z term is
            //    zero-distance-to-a-grid-plane over the floor's entire area and lights the whole surface, which
            //    then flickers with depth-reconstruction noise and reads exactly like z-fighting.
            //
            //  * Cells that project to less than a few pixels fade out rather than aliasing into moire, which
            //    is what makes a receding floor shimmer.
            float grid_coverage(vec3 w, vec3 dw, float spacing) {
                vec3 uv  = w / spacing;
                vec3 duv = dw / spacing;          // grid cells spanned by one pixel, per axis

                // Relative threshold, so it holds at any grid size: an axis varying far less than the dominant
                // one is constant across this surface.
                float dmax = max(max(duv.x, duv.y), duv.z);
                float thresh = dmax * 0.02 + 1e-8;

                vec3 pixels = vec3(1e9);
                if (duv.x > thresh) pixels.x = abs(fract(uv.x + 0.5) - 0.5) / duv.x;
                if (duv.y > thresh) pixels.y = abs(fract(uv.y + 0.5) - 0.5) / duv.y;
                if (duv.z > thresh) pixels.z = abs(fract(uv.z + 0.5) - 0.5) / duv.z;

                float nearest = min(min(pixels.x, pixels.y), pixels.z);
                float cov = 1.0 - smoothstep(0.0, line_px, nearest);

                // Fade as cells approach pixel size (dmax -> 1 means one cell per pixel).
                return cov * (1.0 - smoothstep(0.15, 0.6, dmax));
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

                // On the highlighted face, swap the grid to the highlight colour and brighten it, so the plane
                // being manipulated carries its own grid rather than blending into the global one.
                if (hl_active > 0.5) {
                    float onPlane = 1.0 - smoothstep(0.0, hl_band, abs(dot(world, hl_plane.xyz) - hl_plane.w));
                    color = mix(color, hl_color, onPlane);
                    alpha = mix(alpha, min(1.0, alpha * 2.5 + 0.10), onPlane);
                }
                if (alpha <= 0.001) {
                    discard;
                }

                ALBEDO = color;
                ALPHA = alpha;
            }
            """,
    };
}
