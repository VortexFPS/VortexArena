using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

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

        UpdateCrosshairHover();
        if (_dragging)
            UpdateDrag();
    }

    // =============================================================================================
    //  Picking + hover
    // =============================================================================================

    private void UpdateCrosshairHover()
    {
        (NVec3 origin, NVec3 dir) = CameraRay();
        Hover = VmapPicking.Pick(_document!, origin, dir, PickMode(), GrabRadius, PickRange);
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
        _dragSnap = default;
        _dragging = true;
    }

    /// <summary>
    /// Track the grab. The grabbed point rides at its original camera distance, so looking around moves it in
    /// a sphere about the eye; the result is then constrained by the tool and resolved through the snapping
    /// policy (geometry inside its radius, else grid).
    /// </summary>
    private void UpdateDrag()
    {
        (NVec3 origin, NVec3 dir) = CameraRay();
        NVec3 target = origin + dir * _dragDistance;

        // A face push is one-dimensional: only motion along the face normal means anything, and allowing the
        // other two axes would let a careless look-around shear the wall sideways.
        if (_dragSelection.Kind == VmapSelectionKind.Face)
        {
            NVec3 normal = Hover.Hit ? Hover.Normal : FaceNormal(_dragSelection);
            float along = NVec3.Dot(target - _dragStartPoint, normal);
            along = VmapEdit.SnapToGrid(along, GridSize);
            _dragDelta = normal * along;
            _dragSnap = default;
            return;
        }

        NVec3 resolved = VmapPicking.ResolveDragPosition(
            _document!, target, GridSize, SnapRadius, _session!.SelectedBrushIds(), out _dragSnap);
        _dragDelta = resolved - _dragStartPoint;
    }

    /// <summary>Commit the drag as a single op. Returns true when geometry actually changed.</summary>
    public bool EndDrag()
    {
        if (!_dragging || _session is null)
            return false;

        VmapSelection sel = _dragSelection;
        NVec3 delta = _dragDelta;
        CancelDrag();

        if (delta == NVec3.Zero)
            return false;   // a click, not a drag

        IVmapOp? op = sel.Kind switch
        {
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

    private static float Cvar(string name, float fallback)
    {
        if (Menu.MenuState.Cvars is not { } cvars)
            return fallback;
        string s = cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : cvars.GetFloat(name);
    }
}
