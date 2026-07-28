using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// What the manipulator handles do — the Radiant/Blender-style mode cycle. Independent of
/// <see cref="EditorTool"/>: the tool decides WHAT is selected, the manipulator decides HOW it transforms.
/// </summary>
public enum ManipulatorMode
{
    /// <summary>Axis arrows: drag to translate along X, Y or Z.</summary>
    Translate,

    /// <summary>Curved arcs: drag to rotate about X, Y or Z.</summary>
    Rotate,

    /// <summary>Axis boxes: drag to scale.</summary>
    Scale,
}

/// <summary>Which sub-object the pick resolves to, cycled by the tool key.</summary>
public enum EditorTool
{
    /// <summary>Select and move whole brushes.</summary>
    Brush,

    /// <summary>Select faces; dragging pushes the face along its own normal.</summary>
    Face,

    /// <summary>Select edges; dragging moves both endpoints and refits the meeting planes.</summary>
    Edge,

    /// <summary>Select vertices; dragging moves one corner and refits the meeting planes.</summary>
    Vertex,
}

/// <summary>
/// Drives in-game geometry editing (design doc §11.4): owns the <see cref="VmapEditSession"/>, turns the
/// camera into picks, and turns mouse drags into ops.
///
/// Interaction is CROSSHAIR-BASED in the 3D view rather than free-cursor. That is the right fit for a game
/// whose mouse is captured for looking: you aim at geometry the same way you aim at a player, click to grab,
/// and the grabbed feature follows your view at the distance you grabbed it. Freeing the cursor is reserved
/// for the orthographic view (§11.5), where pan/zoom/marquee genuinely need a pointer — and there the editor
/// takes cursor ownership the same way the maximized radar does.
///
/// A drag never touches the document until it is released. The preview is drawn as a ghost by
/// <see cref="EditorGizmos"/> and a single op is applied on release, so one drag is exactly one undo step and
/// the expensive plane refit runs once instead of per frame.
/// </summary>
public sealed partial class EditorController : Node3D
{
    /// <summary>Cvar: grab radius in world units for resolving a vertex/edge instead of the face.</summary>
    public const string CvarGrabRadius = "cl_editor_grab_radius";

    /// <summary>Cvar: geometry-snap radius in world units (0 disables geometry snapping).</summary>
    public const string CvarSnapRadius = "cl_editor_snap_radius";

    /// <summary>Cvar: whether geometry-to-geometry snapping is active.</summary>
    public const string CvarSnapEnabled = "cl_editor_snap";

    /// <summary>Cvar: show q3map2 TOOL brushes (hint/skip/clip/trigger/caulk) as editable geometry.</summary>
    public const string CvarShowToolBrushes = "cl_editor_show_tool_brushes";

    /// <summary>
    /// Drop face area buried inside other solids when building the editor world. On by default — without it
    /// the view shows the mapper's overlapping solids rather than the level's visible skin. Off is a debugging
    /// aid: it answers "is that hole real geometry, or did the culler eat it?" without a rebuild.
    /// </summary>
    public const string CvarCullOccluded = "cl_editor_cull_occluded";

    /// <summary>Maximum pick range in world units.</summary>
    private const float PickRange = 8192f;

    private Camera3D? _camera;
    private VmapEditSession? _session;
    private VmapDocument? _document;

    // ---- drag state ----
    private bool _dragging;
    private VmapSelection _dragSelection;
    private NVec3 _dragStartPoint;      // where on the surface the grab began
    private float _dragDistance;        // distance from camera at grab time; the drag rides this depth
    private NVec3 _dragDelta;           // current (snapped) offset, previewed but not yet applied
    private VmapPicking.SnapResult _dragSnap;

    /// <summary>The live edit session, or null until a map has been opened for editing.</summary>
    public VmapEditSession? Session => _session;

    /// <summary>The document being edited, or null when no session is open.</summary>
    public VmapDocument? Document => _document;

    /// <summary>Active sub-object tool.</summary>
    public EditorTool Tool { get; private set; } = EditorTool.Face;

    /// <summary>Active manipulator mode (translate / rotate / scale).</summary>
    public ManipulatorMode Manipulator { get; private set; } = ManipulatorMode.Translate;

    /// <summary>Cycle the manipulator: translate → rotate → scale.</summary>
    public void CycleManipulator()
    {
        Manipulator = Manipulator switch
        {
            ManipulatorMode.Translate => ManipulatorMode.Rotate,
            ManipulatorMode.Rotate => ManipulatorMode.Scale,
            _ => ManipulatorMode.Translate,
        };
        Log.Info($"editor manipulator: {Manipulator}");
    }

    /// <summary>
    /// World position the manipulator handles are drawn at — the centre of the current selection, or the
    /// hovered feature when nothing is selected yet, so the handles always have somewhere meaningful to sit.
    /// </summary>
    public bool TryGetManipulatorOrigin(out NVec3 origin)
    {
        origin = NVec3.Zero;
        if (_session is null || _document is null)
            return false;

        List<int> ids = _session.SelectedBrushIds();
        if (ids.Count > 0 && VmapEdit.TryGetSelectionCenter(_document, ids, out origin))
            return true;

        if (Hover.Hit)
        {
            origin = Hover.Point;
            return true;
        }
        return false;
    }

    /// <summary>What the crosshair is currently over (drives the hover highlight).</summary>
    public VmapPickResult Hover { get; private set; } = VmapPickResult.Miss;

    /// <summary>True while a grab is in progress.</summary>
    public bool IsDragging => _dragging;

    /// <summary>The pending, un-applied drag offset — what the ghost preview is drawn at.</summary>
    public NVec3 DragDelta => _dragDelta;

    /// <summary>The selection being dragged (empty when idle).</summary>
    public VmapSelection DragSelection => _dragSelection;

    /// <summary>The snap currently resolved for the drag, for drawing the snap hint.</summary>
    public VmapPicking.SnapResult DragSnap => _dragSnap;

    /// <summary>Bumped whenever geometry changes, so cached wireframe/render meshes know to rebuild.</summary>
    public int GeometryVersion { get; private set; }

    /// <summary>
    /// The shared broadphase cache backing picking, snapping and the ortho wireframe. Rebuilt only when
    /// <see cref="GeometryVersion"/> moves, which is what makes a per-frame crosshair query affordable on a
    /// real map instead of re-deriving every brush's geometry each frame.
    /// </summary>
    public VmapPickIndex PickIndex { get; } = new();

    /// <summary>True when this client is in the editor gametype and free-flying (set by the host each frame).</summary>
    public bool Active { get; set; }

    /// <summary>The orthographic view, when one is open.</summary>
    public EditorOrthoView? Ortho { get; set; }

    /// <summary>Register the interaction cvars. All are client-side tool preferences, so all are saved.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Register(CvarGrabRadius, "12", CvarFlags.Save);
        c.Register(CvarSnapRadius, "16", CvarFlags.Save);
        c.Register(CvarSnapEnabled, "1", CvarFlags.Save);
        c.Register(CvarShowToolBrushes, "0", CvarFlags.Save);
        c.Register(CvarCullOccluded, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarEnabled, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarShadowBudget, "6", CvarFlags.Save);
        c.Register(EditorLighting.CvarBrightness, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarAmbient, "0.18", CvarFlags.Save);
        c.Register(EditorLighting.CvarGlobalIllumination, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarGiCellSize, "8", CvarFlags.Save);
        c.Register(EditorLighting.CvarGiCascades, "4", CvarFlags.Save);
        c.Register(EditorLighting.CvarGiEnergy, "4", CvarFlags.Save);
        c.Register(EditorLighting.CvarLightBakeMode, "2", CvarFlags.Save);
        c.Register(EditorLighting.CvarSsao, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarSsaoIntensity, "4", CvarFlags.Save);
        c.Register(EditorLighting.CvarSsaoRadius, "48", CvarFlags.Save);
        c.Register(EditorLighting.CvarFalloff, "2", CvarFlags.Save);
        c.Register(EditorLighting.CvarSkyLight, "0", CvarFlags.Save);
        c.Register(EditorLighting.CvarSunShadowDistance, "6000", CvarFlags.Save);
    }

    /// <summary>
    /// The gametype whose geometry is currently shown ("" = show everything). Xonotic maps carry
    /// gametype-conditional brush entities — a CTF-only wall, a Race-only barrier — and the compiled map
    /// contains all of them at once. NetRadiant has no notion of this: it edits the .map source, where those
    /// are ordinary entities with filter keys, and it hides categories through its View > Filter toggles
    /// rather than by gametype. Since we edit the COMPILED result, we can do better and show exactly the map a
    /// given mode would produce — while never discarding the rest.
    /// </summary>
    public string GametypeFilter { get; private set; } = "";

    /// <summary>Human-readable filter state for the HUD.</summary>
    public string GametypeFilterLabel => string.IsNullOrEmpty(GametypeFilter) ? "all" : GametypeFilter;

    /// <summary>
    /// Show only geometry present in <paramref name="gametype"/>, or everything when it is empty/"all".
    /// <paramref name="hiddenSubmodels"/> is the inline-model set that gametype filters out, resolved by the
    /// host (which owns the BSP and the entity-filter rules).
    /// </summary>
    public void SetGametypeFilter(string gametype, IReadOnlySet<int>? hiddenSubmodels)
    {
        GametypeFilter = string.Equals(gametype, "all", StringComparison.OrdinalIgnoreCase) ? "" : gametype ?? "";

        PickIndex.HiddenSubmodels.Clear();
        if (hiddenSubmodels is not null)
            foreach (int i in hiddenSubmodels)
                PickIndex.HiddenSubmodels.Add(i);
        PickIndex.Invalidate();

        // The wireframe and any cached derived geometry must rebuild against the new visible set.
        GeometryVersion++;
        Log.Info($"editor: showing geometry for '{GametypeFilterLabel}' ({PickIndex.HiddenSubmodels.Count} models hidden)");
    }

    /// <summary>Point the controller at the scene camera it should pick along.</summary>
    public void Attach(Camera3D camera) => _camera = camera;

    /// <summary>
    /// Begin editing a document. Called by the host when an editor session opens a map.
    /// </summary>
    public void OpenSession(VmapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _session = new VmapEditSession(document);
        CancelDrag();
        GeometryVersion++;
    }

    /// <summary>Close the session (leaving the editor gametype), discarding selection and any in-flight drag.</summary>
    public void CloseSession()
    {
        CancelDrag();
        _session = null;
        _document = null;
        Hover = VmapPickResult.Miss;
    }

    public override void _Process(double delta)
    {
        using var _scope = Client.FrameProfiler.Scope("editor.ctrl");

        if (!Active || _session is null || _document is null || _camera is null)
        {
            Hover = VmapPickResult.Miss;
            return;
        }

        // The ortho view drives its own picking through the cursor, so the crosshair path stands down while
        // it owns interaction.
        if (Ortho is { IsOpen: true })
        {
            Hover = VmapPickResult.Miss;
            return;
        }

        // The hover is frozen mid-drag: the crosshair is parked on the grabbed feature, and re-picking would
        // let the highlight wander onto whatever the ghost happens to overlap.
        if (!_dragging)
            UpdateCrosshairHover();
    }

    // =============================================================================================
    //  Picking + hover
    // =============================================================================================

    // Last ray the hover was solved for, so a still camera does not re-pick.
    private NVec3 _lastPickOrigin;
    private NVec3 _lastPickDir;
    private int _lastPickVersion = -1;
    private EditorTool _lastPickTool = (EditorTool)(-1);

    /// <summary>
    /// Re-solve the crosshair hover, but only when the answer can have changed.
    ///
    /// A pick evaluates brush windings across the whole document, and a real map is thousands of brushes — on
    /// stormkeep (5400) doing that every frame cost tens of milliseconds and showed up as a CPU-LOGIC hitch.
    /// The hover can only change when the view moves, the geometry changes, or the tool changes, so gate on
    /// exactly those. Standing still is the common case while a mapper reads the HUD or lines up a shot.
    /// </summary>
    private void UpdateCrosshairHover()
    {
        (NVec3 origin, NVec3 dir) = CameraRay();

        // ~0.02 units of movement and ~0.1 degrees of rotation are well below what could select a different
        // feature, so treating them as "unchanged" costs nothing visible.
        bool sameRay = (origin - _lastPickOrigin).LengthSquared() < 4e-4f
                       && NVec3.Dot(dir, _lastPickDir) > 0.9999985f;

        if (sameRay && _lastPickVersion == GeometryVersion && _lastPickTool == Tool)
            return;

        _lastPickOrigin = origin;
        _lastPickDir = dir;
        _lastPickVersion = GeometryVersion;
        _lastPickTool = Tool;

        PickIndex.EnsureBuilt(_document!, GeometryVersion, IncludeToolBrushes);
        Hover = VmapPicking.Pick(PickIndex, origin, dir, PickMode(), GrabRadius, PickRange);
    }

    /// <summary>The camera ray in Quake space — the crosshair is the view centre, so this is simply forward.</summary>
    private (NVec3 Origin, NVec3 Direction) CameraRay()
    {
        Transform3D t = _camera!.GlobalTransform;
        NVec3 origin = Coords.ToQuake(t.Origin);
        NVec3 dir = Coords.ToQuake(-t.Basis.Z);   // Godot cameras look down -Z
        return (origin, dir);
    }

    private VmapSelectionKind PickMode() => Tool switch
    {
        EditorTool.Brush => VmapSelectionKind.Brush,
        EditorTool.Edge => VmapSelectionKind.Edge,
        EditorTool.Vertex => VmapSelectionKind.Vertex,
        _ => VmapSelectionKind.Face,
    };

    // =============================================================================================
    //  Drag lifecycle
    // =============================================================================================

    /// <summary>Begin a grab on whatever the crosshair is over. No-op when nothing is hovered.</summary>
    public void BeginDrag(bool addToSelection)
    {
        if (_session is null || !Hover.Hit || _dragging)
            return;

        if (addToSelection)
            _session.ToggleSelect(Hover.Selection);
        else
            _session.Select(Hover.Selection);

        _dragSelection = Hover.Selection;
        _dragStartPoint = Hover.Point;
        _dragDistance = Hover.Distance;
        _dragDelta = NVec3.Zero;
        _dragRaw = NVec3.Zero;
        _dragAngle = 0f;
        _dragAxis = Manipulator == ManipulatorMode.Rotate
            ? RotationAxis()
            : Hover.Selection.Kind == VmapSelectionKind.Face ? Hover.Normal : NVec3.Zero;
        if (Hover.Selection.Kind == VmapSelectionKind.Patch)
            _dragAxis = NVec3.Zero;   // free 3D: a curved surface has no single axis to push along
        _dragSnap = default;
        _dragging = true;
    }

    /// <summary>
    /// Feed a mouse motion into the active drag. The CAMERA IS FROZEN while dragging (the host stops feeding
    /// mouse-look), so the pointer moves the geometry instead of the view.
    ///
    /// Dragging by turning your head was the first design and it felt awkward for exactly the reason you would
    /// expect: the thing being placed and the frame you judge it against move together. Fixing the view and
    /// moving the object against it is how editors do this, and it is what makes fine adjustment possible.
    /// </summary>
    public void ApplyDragMouse(Godot.Vector2 mouseDelta)
    {
        if (!_dragging || _camera is null)
            return;

        // World units per pixel at the grab depth, so the drag tracks the pointer 1:1 on screen.
        float viewportH = MathF.Max(1f, _camera.GetViewport().GetVisibleRect().Size.Y);
        float unitsPerPixel = _camera.Projection == Camera3D.ProjectionType.Orthogonal
            ? _camera.Size / viewportH
            : 2f * MathF.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f) * MathF.Max(1f, _dragDistance) / viewportH;

        Transform3D t = _camera.GlobalTransform;
        NVec3 screenRight = Coords.ToQuake(t.Basis.X);
        NVec3 screenUp = Coords.ToQuake(t.Basis.Y);

        // Screen Y grows downward, so an upward drag is -Y.
        // In ROTATE mode the pointer drives an ANGLE, not a displacement: horizontal travel turns the
        // selection about the chosen axis. Degrees-per-pixel is fixed rather than depth-scaled, because a
        // rotation has no depth for the drag to track against.
        if (Manipulator == ManipulatorMode.Rotate)
        {
            _dragAngle += mouseDelta.X * 0.5f;
            return;
        }

        _dragRaw += screenRight * (mouseDelta.X * unitsPerPixel) - screenUp * (mouseDelta.Y * unitsPerPixel);
        UpdateDragFromRaw();
    }

    /// <summary>Accumulated, unsnapped drag offset in world space.</summary>
    private NVec3 _dragRaw;

    /// <summary>Accumulated rotation in degrees while dragging in <see cref="ManipulatorMode.Rotate"/>.</summary>
    private float _dragAngle;

    /// <summary>Snapped rotation the current drag would apply, in degrees (0 outside a rotate drag).</summary>
    public float DragAngle => Manipulator == ManipulatorMode.Rotate && _dragging
        ? VmapEdit.SnapToGrid(_dragAngle, AngleSnapDegrees)
        : 0f;

    /// <summary>Rotation snap step. 15 degrees matches Radiant's default and keeps geometry on nice angles.</summary>
    public const float AngleSnapDegrees = 15f;

    /// <summary>The axis a face push is constrained to (its normal); zero for a free 3D drag.</summary>
    private NVec3 _dragAxis;

    /// <summary>The constrained drag axis, for the HUD/gizmo axis readout. Zero when the drag is free.</summary>
    public NVec3 DragAxis => _dragAxis;

    /// <summary>
    /// The axis a rotate drag turns about: whichever world axis the camera is most nearly looking ALONG, so
    /// the ring you can see face-on is the one that turns. Falls back to Z (yaw), the common case.
    /// </summary>
    private NVec3 RotationAxis()
    {
        if (_camera is null)
            return new NVec3(0f, 0f, 1f);

        NVec3 fwd = Coords.ToQuake(-_camera.GlobalTransform.Basis.Z);
        float ax = MathF.Abs(fwd.X), ay = MathF.Abs(fwd.Y), az = MathF.Abs(fwd.Z);
        if (az >= ax && az >= ay) return new NVec3(0f, 0f, 1f);
        return ax >= ay ? new NVec3(1f, 0f, 0f) : new NVec3(0f, 1f, 0f);
    }

    /// <summary>Resolve the raw drag into the committed delta: constrain to the axis, then snap.</summary>
    private void UpdateDragFromRaw()
    {
        // A face push is one-dimensional: only motion along the face normal means anything, and allowing the
        // other two axes would let a careless drag shear the wall sideways.
        if (_dragSelection.Kind == VmapSelectionKind.Face && _dragAxis != NVec3.Zero)
        {
            float along = VmapEdit.SnapToGrid(NVec3.Dot(_dragRaw, _dragAxis), GridSize);
            _dragDelta = _dragAxis * along;
            _dragSnap = default;
            return;
        }

        PickIndex.EnsureBuilt(_document!, GeometryVersion, IncludeToolBrushes);
        NVec3 resolved = VmapPicking.ResolveDragPosition(
            PickIndex, _dragStartPoint + _dragRaw, GridSize, SnapRadius,
            _session!.SelectedBrushIds(), out _dragSnap);
        _dragDelta = resolved - _dragStartPoint;
    }

    /// <summary>Commit the drag as a single op. Returns true when geometry actually changed.</summary>
    public bool EndDrag()
    {
        if (!_dragging || _session is null)
            return false;

        VmapSelection sel = _dragSelection;
        NVec3 delta = _dragDelta;
        float angle = DragAngle;
        ManipulatorMode mode = Manipulator;
        CancelDrag();

        if (mode == ManipulatorMode.Rotate)
        {
            // Rotate the whole selection about its own centre, on the axis nearest the view direction — the
            // axis whose handle ring is facing you is the one you are turning.
            List<int> rotIds = _session.SelectedBrushIds();
            if (angle == 0f || rotIds.Count == 0
                || !VmapEdit.TryGetSelectionCenter(_document!, rotIds, out NVec3 pivot))
                return false;

            if (!_session.Apply(new RotateBrushesOp(rotIds, pivot, RotationAxis(), angle)))
            {
                Log.Info("editor: rotation refused — that would break the brush");
                return false;
            }
            GeometryVersion++;
            return true;
        }

        if (delta == NVec3.Zero)
            return false;   // a click, not a drag

        IVmapOp? op = sel.Kind switch
        {
            // A patch moves as a whole object — it has no plane set to push or corner to refit.
            VmapSelectionKind.Patch => new TranslatePatchesOp(new[] { sel.PatchId }, delta),
            VmapSelectionKind.Face => BuildFaceOp(sel, delta),
            VmapSelectionKind.Vertex or VmapSelectionKind.Edge => new MoveVerticesOp(sel.BrushId, sel.Vertices, delta),
            VmapSelectionKind.Brush => new TranslateBrushesOp(_session.SelectedBrushIds(), delta),
            _ => null,
        };
        if (op is null)
            return false;

        if (!_session.Apply(op))
        {
            // Refused: the drag would have produced invalid geometry (§11.4). Say so rather than leaving the
            // mapper wondering why the wall snapped back.
            Log.Info("editor: edit refused — that would break the brush");
            return false;
        }

        GeometryVersion++;
        return true;
    }

    /// <summary>Abandon an in-flight drag without applying anything.</summary>
    public void CancelDrag()
    {
        _dragging = false;
        _dragSelection = VmapSelection.None;
        _dragDelta = NVec3.Zero;
        _dragRaw = NVec3.Zero;
        _dragAngle = 0f;
        _dragAxis = NVec3.Zero;
        _dragSnap = default;
    }

    private IVmapOp? BuildFaceOp(VmapSelection sel, NVec3 delta)
    {
        NVec3 normal = FaceNormal(sel);
        if (normal == NVec3.Zero)
            return null;
        return new MoveFaceOp(sel.BrushId, sel.FaceIndex, NVec3.Dot(delta, normal));
    }

    private NVec3 FaceNormal(VmapSelection sel)
    {
        if (_document?.FindBrush(sel.BrushId) is not { } brush)
            return NVec3.Zero;
        if (sel.FaceIndex < 0 || sel.FaceIndex >= brush.Faces.Count)
            return NVec3.Zero;
        return brush.Faces[sel.FaceIndex].Plane.Normal;
    }

    // =============================================================================================
    //  Commands issued by binds
    // =============================================================================================

    /// <summary>Cycle the sub-object tool (brush → face → edge → vertex).</summary>
    public void CycleTool()
    {
        Tool = Tool switch
        {
            EditorTool.Brush => EditorTool.Face,
            EditorTool.Face => EditorTool.Edge,
            EditorTool.Edge => EditorTool.Vertex,
            _ => EditorTool.Brush,
        };
        CancelDrag();
        Log.Info($"editor tool: {Tool}");
    }

    /// <summary>Set the tool directly.</summary>
    public void SetTool(EditorTool tool)
    {
        Tool = tool;
        CancelDrag();
    }

    /// <summary>Undo one step and refresh derived geometry.</summary>
    public bool Undo()
    {
        if (_session is null || !_session.Undo())
            return false;
        GeometryVersion++;
        Log.Info($"undo: {_session.RedoLabel ?? "step"}");
        return true;
    }

    /// <summary>Redo one step.</summary>
    public bool Redo()
    {
        if (_session is null || !_session.Redo())
            return false;
        GeometryVersion++;
        Log.Info($"redo: {_session.UndoLabel ?? "step"}");
        return true;
    }

    /// <summary>Delete the selected brushes.</summary>
    public bool DeleteSelection()
    {
        if (_session is null)
            return false;
        List<int> ids = _session.SelectedBrushIds();
        if (ids.Count == 0)
            return false;
        if (!_session.Apply(new DeleteBrushesOp(ids)))
            return false;
        _session.Selection.Clear();
        GeometryVersion++;
        return true;
    }

    /// <summary>
    /// Rotate the selection about the vertical axis through its own centre, by the angle step. Bound to a key
    /// rather than a drag for now: a rotation ring gizmo needs the cursor, which the 3D view does not own.
    /// </summary>
    public bool RotateSelection(float degrees)
    {
        if (_session is null)
            return false;
        List<int> ids = _session.SelectedBrushIds();
        if (ids.Count == 0 || !VmapEdit.TryGetSelectionCenter(_document!, ids, out NVec3 center))
            return false;
        if (!_session.Apply(new RotateBrushesOp(ids, center, new NVec3(0f, 0f, 1f), degrees)))
            return false;
        GeometryVersion++;
        return true;
    }

    /// <summary>Save the session back to its package.</summary>
    public bool Save(string path)
    {
        if (_session is null)
            return false;
        _session.Save(path);
        Log.Info($"editor: saved {path}");
        return true;
    }

    // =============================================================================================
    //  Cvar reads
    // =============================================================================================

    /// <summary>Current grid size, shared with the world grid so what you see is what you snap to.</summary>
    public float GridSize => Cvar(EditorGrid.CvarSize, 64f);

    private float GrabRadius => Cvar(CvarGrabRadius, 12f);

    private float SnapRadius => Cvar(CvarSnapEnabled, 1f) != 0f ? Cvar(CvarSnapRadius, 16f) : 0f;

    /// <summary>True when geometry snapping is enabled (shown in the HUD).</summary>
    public bool SnapEnabled => Cvar(CvarSnapEnabled, 1f) != 0f;

    /// <summary>Geometry-snap radius currently in force (shown in the HUD).</summary>
    public float SnapRadiusDisplay => Cvar(CvarSnapRadius, 16f);

    /// <summary>
    /// Whether q3map2 TOOL brushes take part in picking. Off by default: hint/skip/clip/trigger/caulk brushes
    /// are compiler and gameplay scaffolding, not level architecture, and on a real map they vastly outnumber
    /// the visible geometry and sit in front of it — so with them pickable, the crosshair mostly grabs invisible
    /// volumes instead of the wall behind them.
    /// </summary>
    public bool IncludeToolBrushes => Cvar(CvarShowToolBrushes, 0f) != 0f;

    /// <summary>Whether the world build removes faces buried inside other solids.</summary>
    public bool CullOccludedFaces => Cvar(CvarCullOccluded, 1f) != 0f;

    private static float Cvar(string name, float fallback)
    {
        if (Menu.MenuState.Cvars is not { } cvars)
            return fallback;
        string s = cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : cvars.GetFloat(name);
    }
}
