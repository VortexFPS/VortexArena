using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Vmap;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>Which axis the orthographic view looks down.</summary>
public enum OrthoAxis
{
    /// <summary>Looking down -Z: the map's plan view (X right, Y up on screen).</summary>
    Top,

    /// <summary>Looking along +Y: the front elevation (X right, Z up on screen).</summary>
    Front,

    /// <summary>Looking along +X: the side elevation (Y right, Z up on screen).</summary>
    Side,
}

/// <summary>
/// The orthographic top/front/side view (design doc §11.5) — the precision half of the editor.
///
/// A single pane that cycles axes rather than a docked quad layout: this is a tool inside a game, not a
/// desktop application, and one big view with a key to cycle it keeps the screen readable at gameplay
/// resolutions. It renders the world as a TRUTH wireframe (<see cref="EditorGizmos.ShowWorldWireframe"/>)
/// rather than the textured render mesh, which is what makes its lines exact and its geometry unambiguous.
///
/// Two properties make this worth having alongside the 3D view:
/// <list type="bullet">
///   <item><b>Axis-locked by construction</b> — the projection axis is fixed, so a drag is exactly planar and
///         cannot drift in depth. That is why 2D views remain unbeatable for alignment work.</item>
///   <item><b>A floor filter</b> — a scrollable near/far slab through the map, so stacked floors do not draw
///         on top of each other. The classic tall-map ortho problem, solved the classic way.</item>
/// </list>
///
/// It reuses the scene camera rather than a second viewport: one camera, switched to
/// <see cref="Camera3D.ProjectionType.Orthogonal"/> and parked on an axis. That keeps every existing render
/// path (the world grid, the gizmos, PVS culling) working unchanged instead of needing an editor-only
/// duplicate of each.
/// </summary>
public sealed partial class EditorOrthoView : Node
{
    /// <summary>Cvar: vertical extent of the ortho view in world units (the zoom level).</summary>
    public const string CvarZoom = "cl_editor_ortho_zoom";

    /// <summary>Cvar: half-thickness of the floor-filter slab in world units.</summary>
    public const string CvarSlab = "cl_editor_ortho_slab";

    private const float MinZoom = 64f;
    private const float MaxZoom = 16384f;

    /// <summary>How far outside the slab the camera sits, so near-plane clipping does the filtering.</summary>
    private const float CameraStandoff = 16f;

    private Camera3D? _camera;
    private EditorController? _controller;
    private EditorGizmos? _gizmos;

    // Saved perspective state, restored on close.
    private bool _savedIsPerspective = true;
    private float _savedFov;
    private float _savedNear;
    private float _savedFar;
    private Transform3D _savedTransform;

    /// <summary>True while the ortho view is showing.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Which axis is being looked down.</summary>
    public OrthoAxis Axis { get; private set; } = OrthoAxis.Top;

    /// <summary>Centre of the view in world (Quake) space — what panning moves.</summary>
    public NVec3 Center { get; private set; }

    /// <summary>Vertical extent of the view in world units.</summary>
    public float Zoom => Mathf.Clamp(Cvar(CvarZoom, 2048f), MinZoom, MaxZoom);

    /// <summary>Half-thickness of the visible slab.</summary>
    public float SlabHalfThickness => MathF.Max(16f, Cvar(CvarSlab, 4096f));

    public static void RegisterDefaults(CvarService c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Register(CvarZoom, "2048", CvarFlags.Save);
        c.Register(CvarSlab, "4096", CvarFlags.Save);
    }

    public void Attach(Camera3D camera, EditorController controller, EditorGizmos gizmos)
    {
        _camera = camera;
        _controller = controller;
        _gizmos = gizmos;
    }

    /// <summary>
    /// Open the view centred on <paramref name="center"/> (normally the fly camera's position, so the mapper
    /// lands looking at where they already were rather than at the map origin).
    /// </summary>
    public void Open(NVec3 center)
    {
        if (_camera is null || IsOpen)
            return;

        _savedIsPerspective = _camera.Projection == Camera3D.ProjectionType.Perspective;
        _savedFov = _camera.Fov;
        _savedNear = _camera.Near;
        _savedFar = _camera.Far;
        _savedTransform = _camera.GlobalTransform;

        Center = center;
        IsOpen = true;
        if (_gizmos is not null)
            _gizmos.ShowWorldWireframe = true;
        Apply();
        Log.Info($"ortho: {Axis} view (zoom {Zoom:0}u)");
    }

    /// <summary>Close the view and restore the perspective camera exactly as it was.</summary>
    public void Close()
    {
        if (!IsOpen || _camera is null)
            return;

        IsOpen = false;
        if (_gizmos is not null)
            _gizmos.ShowWorldWireframe = false;
        SetSkyVisible(true);

        _camera.Projection = _savedIsPerspective
            ? Camera3D.ProjectionType.Perspective
            : Camera3D.ProjectionType.Orthogonal;
        _camera.Fov = _savedFov;
        _camera.Near = _savedNear;
        _camera.Far = _savedFar;
        _camera.GlobalTransform = _savedTransform;
    }

    /// <summary>Open at the given centre, or close if already open.</summary>
    public void Toggle(NVec3 center)
    {
        if (IsOpen)
            Close();
        else
            Open(center);
    }

    /// <summary>Cycle top → front → side, keeping the centre so you stay over the same part of the map.</summary>
    public void CycleAxis()
    {
        Axis = Axis switch
        {
            OrthoAxis.Top => OrthoAxis.Front,
            OrthoAxis.Front => OrthoAxis.Side,
            _ => OrthoAxis.Top,
        };
        if (IsOpen)
        {
            Apply();
            Log.Info($"ortho: {Axis} view");
        }
    }

    /// <summary>Zoom by a multiplicative step (wheel), clamped to the usable range.</summary>
    public void ZoomBy(float factor)
    {
        if (Menu.MenuState.Cvars is not { } cvars || factor <= 0f)
            return;
        float next = Mathf.Clamp(Zoom * factor, MinZoom, MaxZoom);
        cvars.Set(CvarZoom, next.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (IsOpen)
            Apply();
    }

    /// <summary>
    /// Pan by a screen-space delta in pixels, converted through the current zoom so a drag tracks the cursor
    /// one-to-one regardless of zoom level.
    /// </summary>
    public void PanByPixels(Vector2 pixels, float viewportHeight)
    {
        if (!IsOpen || viewportHeight <= 0f)
            return;
        float unitsPerPixel = Zoom / viewportHeight;
        (NVec3 right, NVec3 up) = ScreenAxes();
        Center -= right * (pixels.X * unitsPerPixel);
        Center += up * (pixels.Y * unitsPerPixel);
        Apply();
    }

    /// <summary>
    /// Pan using the movement axes, so the normal fly keys pan the view. Mouse-button panning depends on the
    /// middle button reaching the handler; the movement keys always do, and while the ortho view owns the
    /// cursor those keys are otherwise unused.
    /// </summary>
    /// <param name="forward">Forward axis in [-1,1] (screen up/down).</param>
    /// <param name="side">Side axis in [-1,1] (screen left/right).</param>
    /// <param name="dt">Frame time.</param>
    public void PanByAxes(float forward, float side, float dt)
    {
        if (!IsOpen || (forward == 0f && side == 0f))
            return;
        // Pan speed scales with zoom so a keypress crosses the same FRACTION of the view at any zoom level.
        float speed = Zoom * 0.9f * dt;
        (NVec3 right, NVec3 up) = ScreenAxes();
        Center += right * (side * speed) + up * (forward * speed);
        Apply();
    }

    /// <summary>Move the slab along the view axis — the floor filter.</summary>
    public void MoveSlab(float units)
    {
        if (!IsOpen)
            return;
        Center += ViewForward() * units;
        Apply();
    }

    /// <summary>
    /// The world-space ray for a screen position. In an orthographic projection the ray direction is constant
    /// (the view axis) and only the origin varies, which is exactly what makes in-view edits planar.
    /// </summary>
    public (NVec3 Origin, NVec3 Direction) RayAt(Vector2 screenPosition, Vector2 viewportSize)
    {
        (NVec3 right, NVec3 up) = ScreenAxes();
        float unitsPerPixel = viewportSize.Y > 0f ? Zoom / viewportSize.Y : 1f;

        Vector2 fromCenter = screenPosition - viewportSize * 0.5f;
        NVec3 origin = Center
                       + right * (fromCenter.X * unitsPerPixel)
                       - up * (fromCenter.Y * unitsPerPixel)     // screen Y grows downward
                       - ViewForward() * (SlabHalfThickness + CameraStandoff);

        return (origin, ViewForward());
    }

    /// <summary>
    /// Re-assert the ortho camera. Called every frame by the host while the view is open, because the normal
    /// first-person camera update would otherwise reclaim the camera between frames.
    /// </summary>
    public void Reapply()
    {
        if (IsOpen)
            Apply();
    }

    /// <summary>Push the current axis/zoom/centre onto the camera.</summary>
    private void Apply()
    {
        if (_camera is null)
            return;

        NVec3 forward = ViewForward();
        NVec3 eye = Center - forward * (SlabHalfThickness + CameraStandoff);

        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        _camera.Size = Zoom;

        // The skybox is drawn at effectively infinite distance for a PERSPECTIVE eye; under an orthographic
        // projection that assumption breaks and it swims wildly across the frame. An elevation view wants a
        // flat backdrop anyway, so replace it with one while the view is open.
        SetSkyVisible(false);

        // The near/far pair IS the floor filter: geometry outside the slab is clipped away by the projection,
        // which costs nothing and is exactly what stops upper floors from drawing over the one being edited.
        _camera.Near = CameraStandoff * 0.5f;
        _camera.Far = CameraStandoff + SlabHalfThickness * 2f;

        Vector3 godotEye = Coords.ToGodot(eye);
        Vector3 godotTarget = Coords.ToGodot(Center);

        // Looking straight down needs a non-parallel up vector or the basis is singular.
        Vector3 up = Axis == OrthoAxis.Top ? new Vector3(0, 0, -1) : new Vector3(0, 1, 0);
        _camera.GlobalTransform = new Transform3D(Basis.Identity, godotEye).LookingAt(godotTarget, up);
    }

    /// <summary>
    /// Swap the world environment's sky for a flat colour (and back). Kept as a saved/restored background mode
    /// rather than hiding a node, so any sky source the map used comes back exactly as it was.
    /// </summary>
    private void SetSkyVisible(bool visible)
    {
        if (_camera?.GetViewport() is not Viewport vp || vp.World3D?.Environment is not Godot.Environment env)
            return;

        if (!visible)
        {
            if (_savedBg is null)
            {
                _savedBg = env.BackgroundMode;
                _savedBgColor = env.BackgroundColor;
            }
            env.BackgroundMode = Godot.Environment.BGMode.Color;
            env.BackgroundColor = new Color(0.07f, 0.08f, 0.10f);
        }
        else if (_savedBg is { } mode)
        {
            env.BackgroundMode = mode;
            env.BackgroundColor = _savedBgColor;
            _savedBg = null;
        }
    }

    private Godot.Environment.BGMode? _savedBg;
    private Color _savedBgColor;

    /// <summary>
    /// How much to dim the world grid while the ortho view is up. The wireframe already carries the geometry
    /// there, so a full-strength grid competes with it instead of supporting it.
    /// </summary>
    public const float GridAlphaScale = 0.45f;

    /// <summary>The direction the view looks, in Quake space.</summary>
    public NVec3 ViewForward() => Axis switch
    {
        OrthoAxis.Top => new NVec3(0f, 0f, -1f),
        OrthoAxis.Front => new NVec3(0f, 1f, 0f),
        _ => new NVec3(1f, 0f, 0f),
    };

    /// <summary>The world axes mapped to screen right and screen up for the current view.</summary>
    public (NVec3 Right, NVec3 Up) ScreenAxes() => Axis switch
    {
        OrthoAxis.Top => (new NVec3(1f, 0f, 0f), new NVec3(0f, 1f, 0f)),
        OrthoAxis.Front => (new NVec3(1f, 0f, 0f), new NVec3(0f, 0f, 1f)),
        _ => (new NVec3(0f, 1f, 0f), new NVec3(0f, 0f, 1f)),
    };

    /// <summary>Short label for the HUD.</summary>
    public string AxisLabel => Axis switch
    {
        OrthoAxis.Top => "TOP",
        OrthoAxis.Front => "FRONT",
        _ => "SIDE",
    };

    private static float Cvar(string name, float fallback)
    {
        if (Menu.MenuState.Cvars is not { } cvars)
            return fallback;
        string s = cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : cvars.GetFloat(name);
    }
}
