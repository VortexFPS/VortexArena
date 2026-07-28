using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

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

    /// <summary>
    /// Active sub-object tool. Starts at <see cref="EditorTool.None"/>: a session opens by LOOKING at the
    /// map, and a hover outline drawn over whatever you aim at is in the way of judging it.
    /// </summary>
    public EditorTool Tool { get; private set; } = EditorTool.None;

    /// <summary>
    /// What a handle drag does with the current tool. Always a mode the tool actually offers — every path that
    /// writes it goes through <see cref="EditorTools.Supports"/> or <see cref="EditorTools.CarryMode"/>, so the
    /// pair can never drift into a combination the menu would not show.
    /// </summary>
    public ToolMode Mode { get; private set; } = ToolMode.None;

    /// <summary>Which manipulator handles the current mode draws. Derived, never stored.</summary>
    public HandleSet Handles => EditorTools.HandlesFor(Mode);

    /// <summary>The HUD action line: <c>Tool &gt; Mode: subject</c> (design doc §11.9).</summary>
    public string ActionLine => EditorTools.ActionLine(Tool, Mode, ActionSubject());

    /// <summary>
    /// What the current tool+mode is about to act ON, in the mapper's words. Paste names the clipboard because
    /// that is the thing about to land in the world; everything else names the selection, falling back to what
    /// the crosshair is over so the line is never blank while you are aiming at something.
    /// </summary>
    private string ActionSubject()
    {
        if (Mode == ToolMode.Paste)
            return Clipboard.IsEmpty ? "(clipboard empty)" : Clipboard.Describe();

        if (_session is { } s && s.Selection.Count > 0)
            return s.Selection.Count == 1 && s.Selection[0].Kind == VmapSelectionKind.Entity
                ? DescribeEntity(s.Selection[0].EntityId)
                : DescribeSelection(s.Selection);

        if (Hover.Hit && !Hover.Selection.IsEmpty)
            return Hover.Selection.Kind == VmapSelectionKind.Entity
                ? DescribeEntity(Hover.Selection.EntityId)
                : DescribeOne(Hover.Selection);

        return "";
    }

    /// <summary>Name a selection the way the HUD should read it: one item spelled out, many summarised.</summary>
    private static string DescribeSelection(IReadOnlyList<VmapSelection> sel)
    {
        if (sel.Count == 1)
            return DescribeOne(sel[0]);

        // Mixed selections are possible (shift-click a brush then a patch), so only claim a kind when they agree.
        VmapSelectionKind kind = sel[0].Kind;
        for (int i = 1; i < sel.Count; i++)
            if (sel[i].Kind != kind)
                return $"{sel.Count} items";
        return $"{sel.Count} {kind.ToString().ToLowerInvariant()}s";
    }

    private static string DescribeOne(VmapSelection s) => s.Kind switch
    {
        VmapSelectionKind.Brush => $"Brush #{s.BrushId}",
        VmapSelectionKind.Face => $"Face {s.FaceIndex} of brush #{s.BrushId}",
        VmapSelectionKind.Edge => $"Edge of brush #{s.BrushId}",
        VmapSelectionKind.Vertex => $"Vertex of brush #{s.BrushId}",
        VmapSelectionKind.Patch => $"Patch #{s.PatchId}",
        VmapSelectionKind.Entity => $"entity #{s.EntityId}",
        _ => "",
    };

    /// <summary>
    /// Name an entity the way a mapper recognises it: by classname, with its targetname when it has one, since
    /// a map with nine info_player_deathmatch entities needs something to tell them apart.
    /// </summary>
    private string DescribeEntity(int id)
    {
        if (_document?.FindEntity(id) is not { } e)
            return $"entity #{id}";
        string name = e.Fields.TryGetValue("targetname", out string? t) && t.Length > 0 ? $" \"{t}\"" : "";
        return string.IsNullOrEmpty(e.ClassName) ? $"entity #{id}{name}" : $"{e.ClassName}{name}";
    }

    /// <summary>
    /// The editor clipboard. Lives on the controller rather than the session because it deliberately SURVIVES
    /// closing a map: copying a light rig out of one level and pasting it into another is a thing mappers do,
    /// and there is no reason the document boundary should eat it.
    /// </summary>
    public VmapClipboard Clipboard { get; } = new();

    /// <summary>
    /// Cycle the mode within the current tool. The menu is the discoverable path; this is the keyboard one, and
    /// it wraps within the tool so it can never land on a mode the tool does not offer.
    /// </summary>
    public void CycleMode()
    {
        IReadOnlyList<ToolMode> modes = EditorTools.ModesFor(Tool);
        if (modes.Count == 0)
            return;

        int at = 0;
        for (int i = 0; i < modes.Count; i++)
            if (modes[i] == Mode)
            {
                at = i;
                break;
            }
        SetMode(modes[(at + 1) % modes.Count]);
    }

    /// <summary>Set the mode directly. Refused (and logged) when the current tool does not offer it.</summary>
    public bool SetMode(ToolMode mode)
    {
        if (mode != ToolMode.None && !EditorTools.Supports(Tool, mode))
        {
            Log.Warn($"editor: {EditorTools.Label(Tool)} has no {EditorTools.Label(mode)} mode");
            return false;
        }
        Mode = mode;
        CancelDrag();
        Log.Info($"editor mode: {EditorTools.ActionLine(Tool, Mode)}");
        return true;
    }

    /// <summary>
    /// World position the manipulator handles sit at: the centre of the current SELECTION.
    ///
    /// Deliberately no hover fallback. Under the two-phase model (§11.9) handles are click targets, and
    /// handles that follow whatever you happen to be aiming at would put a grabbable arrow in front of every
    /// surface in the level — you could never click the geometry to select it in the first place.
    /// </summary>
    public bool TryGetManipulatorOrigin(out NVec3 origin)
    {
        origin = NVec3.Zero;
        if (_session is null || _document is null || _session.Selection.Count == 0)
            return false;

        List<int> ids = _session.SelectedBrushIds();
        if (ids.Count > 0 && VmapEdit.TryGetSelectionCenter(_document, ids, out origin))
            return true;

        // A patch- or entity-only selection has no brush ids; fall back to their own centres.
        return TryGetPatchSelectionCenter(out origin) || TryGetEntitySelectionCenter(out origin);
    }

    private bool TryGetEntitySelectionCenter(out NVec3 origin)
    {
        origin = NVec3.Zero;
        if (_document is null)
            return false;

        NVec3 sum = NVec3.Zero;
        int n = 0;
        foreach (int id in SelectedEntityIds())
        {
            if (_document.FindEntity(id) is not { } e)
                continue;
            sum += e.Origin();
            n++;
        }
        if (n == 0)
            return false;
        origin = sum / n;
        return true;
    }

    private bool TryGetPatchSelectionCenter(out NVec3 origin)
    {
        origin = NVec3.Zero;
        if (_session is null || _document is null)
            return false;

        NVec3 sum = NVec3.Zero;
        int n = 0;
        foreach (VmapSelection s in _session.Selection)
        {
            if (s.Kind != VmapSelectionKind.Patch || _document.FindPatch(s.PatchId) is not { } p)
                continue;
            foreach (NVec3 c in p.Controls)
            {
                sum += c;
                n++;
            }
        }
        if (n == 0)
            return false;
        origin = sum / n;
        return true;
    }

    // =============================================================================================
    //  Manipulator handles (§11.9) — the click targets that make a transform pick ONE axis
    // =============================================================================================

    private readonly List<EditorHandle> _handles = new();
    private EditorHandle? _hoverHandle;
    private EditorHandle? _grabbedHandle;

    /// <summary>The live handle set, for the gizmo to draw. Empty when nothing is selected.</summary>
    public IReadOnlyList<EditorHandle> HandleList => _handles;

    /// <summary>The handle the crosshair is over, for highlighting. Null when aiming elsewhere.</summary>
    public EditorHandle? HoverHandle => _hoverHandle;

    /// <summary>The handle a live drag is riding, or null when idle.</summary>
    public EditorHandle? GrabbedHandle => _grabbedHandle;

    /// <summary>
    /// Rebuild the handle set. Sized so it stays a constant fraction of the viewport regardless of how far
    /// away the selection is: a fixed world size is a thumbnail across a hall and fills the screen up close,
    /// and either way it stops being clickable.
    /// </summary>
    private void UpdateHandles()
    {
        HandleSet set = Handles;
        if (set == HandleSet.None || _camera is null || !TryGetManipulatorOrigin(out NVec3 centre))
        {
            _handles.Clear();
            _hoverHandle = null;
            return;
        }

        NVec3 eye = Coords.ToQuake(_camera.GlobalTransform.Origin);
        float distance = (centre - eye).Length();
        float tanHalfFov = MathF.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f);
        VmapHandles.Build(_handles, set, centre, VmapHandles.ScreenScale(distance, tanHalfFov));

        // A live drag keeps its highlight on the handle it grabbed; re-picking would let it flicker onto a
        // neighbour as the geometry moves under the crosshair.
        if (_dragging)
        {
            _hoverHandle = _grabbedHandle;
            return;
        }

        (NVec3 rayOrigin, NVec3 rayDir) = CameraRay();
        _hoverHandle = VmapHandles.TryPick(_handles, rayOrigin, rayDir, out EditorHandle hit, out _)
            ? hit
            : null;
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
    /// Mark derived geometry dirty from outside the controller — the console entity commands apply their own
    /// ops and still need the pick index and the world rebuild to follow.
    /// </summary>
    public void BumpGeometryVersion() => GeometryVersion++;

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
        c.Register(EditorLighting.CvarAmbient, "0.004", CvarFlags.Save);
        c.Register(EditorLighting.CvarGlobalIllumination, "0", CvarFlags.Save);
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
        c.Register(EditorLighting.CvarSunScale, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarRangeScale, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarSurfaceLights, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarBakeLights, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarBakeScale, "0.0043", CvarFlags.Save);
        c.Register(EditorLighting.CvarBakeShadows, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarBakeBounce, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarBakeBounces, "8", CvarFlags.Save);
        c.Register(EditorLighting.CvarBakeGamma, "1.05", CvarFlags.Save);
        c.Register(EditorLighting.CvarDeluxe, "1", CvarFlags.Save);
        c.Register(EditorLighting.CvarShowBsp, "0", CvarFlags.None);
        c.Register(EditorLighting.CvarBakeCpu, "0.75", CvarFlags.Save);
        c.Register(EditorLighting.CvarPatchSubdiv, "8", CvarFlags.Save);
        c.Register(EditorLighting.CvarGlow, "0", CvarFlags.Save);
        c.Register(EditorLighting.CvarLuxel, "24", CvarFlags.Save);
        c.Register(EditorLighting.CvarDirt, "0.8", CvarFlags.Save);
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

        UpdateHandles();
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

        if (Tool == EditorTool.None)
        {
            Hover = default;   // nothing picked, nothing outlined, and no pick cost either
            return;
        }

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

    private VmapSelectionKind PickMode()
    {
        // Select resolves at object granularity by default, but its Face mode is exactly "pick the face
        // instead" — the one place the tool alone does not determine the pick kind.
        if (Tool == EditorTool.Select && Mode == ToolMode.Face)
            return VmapSelectionKind.Face;
        return EditorTools.PickKind(Tool);
    }

    // =============================================================================================
    //  Drag lifecycle
    // =============================================================================================

    /// <summary>
    /// A left-click in the 3D view. TWO-PHASE (§11.9): if the crosshair is on a manipulator handle this starts
    /// a transform on that handle's axis; otherwise it only SELECTS.
    ///
    /// Dragging the object body no longer transforms anything, and that is the point. The old behaviour moved
    /// the selection along both screen axes at once with no way to say "only Z", which is fine for shoving a
    /// prop roughly into place and useless for the alignment work an editor exists to do. Making the axis
    /// something you aim at turns it into a choice instead of an inference.
    ///
    /// Returns true when a drag actually began.
    /// </summary>
    public bool BeginDrag(bool addToSelection)
    {
        if (_session is null || _dragging)
            return false;

        // Paste mode owns the click outright: the ghost is under the crosshair and clicking puts it down.
        // Handled before the handle test because a fresh paste has no selection yet, so there are no handles
        // to compete with — and after a paste there ARE, which is exactly when you want them.
        if (Mode == ToolMode.Paste)
        {
            PasteAtCrosshair();
            return false;
        }

        // The Clip tool's click places a plane point, except with nothing selected yet — you have to be able
        // to pick the brushes to cut before you can aim a cut at them.
        if (Tool == EditorTool.Clip && _session.Selection.Count > 0)
        {
            AddClipPoint();
            return false;
        }

        // Measure never selects: every click is a point of the measurement.
        if (Tool == EditorTool.Measure)
        {
            AddMeasurePoint();
            return false;
        }

        // --- phase two: a handle is under the crosshair, so transform along it ---
        if (_hoverHandle is { } handle && _session.Selection.Count > 0)
        {
            _grabbedHandle = handle;
            _dragSelection = _session.Selection[0];
            _dragStartPoint = handle.Tip;
            _dragDistance = HandleDistance(handle);
            _dragDelta = NVec3.Zero;
            _dragRaw = NVec3.Zero;
            _dragAngle = 0f;
            _dragScale = NVec3.One;
            _dragAxis = handle.Kind == HandleKind.ScaleUniform ? NVec3.Zero : handle.Axis;
            _dragSnap = default;
            _dragging = true;
            return true;
        }

        // --- phase one: nothing grabbed, so this is a selection click ---
        if (!Hover.Hit)
        {
            // Clicking empty space clears the selection, which is also how you put the handles away.
            if (!addToSelection)
                _session.Selection.Clear();
            return false;
        }

        if (addToSelection)
            _session.ToggleSelect(Hover.Selection);
        else
            _session.Select(Hover.Selection);

        // Rebuild immediately rather than waiting for the next frame, so the handles are already grabbable by
        // the time the mapper's second click lands. At speed those two clicks are ~80ms apart.
        UpdateHandles();
        return false;
    }

    /// <summary>Distance from the eye to a handle, for the units-per-pixel conversion during its drag.</summary>
    private float HandleDistance(EditorHandle handle)
    {
        if (_camera is null)
            return 256f;
        NVec3 eye = Coords.ToQuake(_camera.GlobalTransform.Origin);
        return MathF.Max(1f, (handle.Tip - eye).Length());
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
        // selection about the grabbed ring's axis. Degrees-per-pixel is fixed rather than depth-scaled,
        // because a rotation has no depth for the drag to track against.
        if (Mode == ToolMode.Rotate)
        {
            _dragAngle += mouseDelta.X * 0.5f;
            return;
        }

        _dragRaw += screenRight * (mouseDelta.X * unitsPerPixel) - screenUp * (mouseDelta.Y * unitsPerPixel);

        if (Mode == ToolMode.Scale)
        {
            UpdateScaleFromRaw();
            return;
        }

        UpdateDragFromRaw();
    }

    /// <summary>
    /// Turn the accumulated pointer travel into per-axis scale factors. The reach (pivot to handle) is the
    /// denominator, so dragging a handle to twice its distance from the pivot doubles the selection — which is
    /// what makes the gesture feel proportional on a small brush and a large one alike.
    /// </summary>
    private void UpdateScaleFromRaw()
    {
        if (_grabbedHandle is not { } handle || !TryGetManipulatorOrigin(out NVec3 pivot))
        {
            _dragScale = NVec3.One;
            return;
        }

        float along;
        float reach;

        if (handle.Kind == HandleKind.ScaleUniform)
        {
            // The centre handle sits ON the pivot, so it has no axis of its own to project onto. Drag right to
            // grow: a horizontal gesture is the one that reads as "bigger" without an axis to follow.
            NVec3 right = Coords.ToQuake(_camera!.GlobalTransform.Basis.X);
            along = NVec3.Dot(_dragRaw, right);

            // Scale against the selection's own size, so the same pointer travel is proportionally the same
            // change on a 16-unit crate and a 1024-unit hall.
            reach = SelectionReach();
        }
        else
        {
            along = NVec3.Dot(_dragRaw, handle.Axis * handle.Sign);
            reach = MathF.Max(1f, (handle.Tip - pivot).Length());
        }

        _dragScale = VmapHandles.ScaleFactors(handle, along, reach, ScaleSelectionOp.MinFactor);
    }

    /// <summary>Half the selection's largest extent, floored so a degenerate selection cannot divide by zero.</summary>
    private float SelectionReach()
    {
        if (_document is null || _session is null)
            return 64f;

        float best = 0f;
        foreach (int id in _session.SelectedBrushIds())
        {
            if (_document.FindBrush(id) is not { } b || !VmapWinding.TryGetBounds(b, out NVec3 mn, out NVec3 mx))
                continue;
            best = MathF.Max(best, (mx - mn).Length() * 0.5f);
        }
        return MathF.Max(16f, best);
    }

    /// <summary>Accumulated, unsnapped drag offset in world space.</summary>
    private NVec3 _dragRaw;

    /// <summary>Per-axis scale factors the current drag would apply (one outside a scale drag).</summary>
    private NVec3 _dragScale = NVec3.One;

    /// <summary>The pending scale, for the ghost preview and the HUD readout.</summary>
    public NVec3 DragScale => _dragging && Mode == ToolMode.Scale ? _dragScale : NVec3.One;

    /// <summary>Accumulated rotation in degrees while dragging in <see cref="ToolMode.Rotate"/>.</summary>
    private float _dragAngle;

    /// <summary>Snapped rotation the current drag would apply, in degrees (0 outside a rotate drag).</summary>
    public float DragAngle => Mode == ToolMode.Rotate && _dragging
        ? VmapEdit.SnapToGrid(_dragAngle, AngleSnapDegrees)
        : 0f;

    /// <summary>Rotation snap step. 15 degrees matches Radiant's default and keeps geometry on nice angles.</summary>
    public const float AngleSnapDegrees = 15f;

    /// <summary>The axis a face push is constrained to (its normal); zero for a free 3D drag.</summary>
    private NVec3 _dragAxis;

    /// <summary>The constrained drag axis, for the HUD/gizmo axis readout. Zero when the drag is free.</summary>
    public NVec3 DragAxis => _dragAxis;

    /// <summary>
    /// The axis a rotate drag turns about: the ring the mapper actually grabbed. Falls back to the world axis
    /// the camera is most nearly looking along (so the ring you can see face-on is the one that turns) only
    /// when a rotation is driven from a key rather than a handle.
    /// </summary>
    private NVec3 RotationAxis()
    {
        if (_grabbedHandle is { Kind: HandleKind.RotateRing } ring)
            return ring.Axis;

        if (_camera is null)
            return new NVec3(0f, 0f, 1f);

        NVec3 fwd = Coords.ToQuake(-_camera.GlobalTransform.Basis.Z);
        float ax = MathF.Abs(fwd.X), ay = MathF.Abs(fwd.Y), az = MathF.Abs(fwd.Z);
        if (az >= ax && az >= ay) return new NVec3(0f, 0f, 1f);
        return ax >= ay ? new NVec3(1f, 0f, 0f) : new NVec3(0f, 1f, 0f);
    }

    /// <summary>
    /// Resolve the raw drag into the committed delta: constrain to what the grabbed handle permits, then snap.
    ///
    /// The handle is the authority now. A face push is still one-dimensional along its own normal, but for
    /// everything else the axis comes from the arrow or pad the mapper aimed at rather than being inferred
    /// from the direction the mouse happened to travel.
    /// </summary>
    private void UpdateDragFromRaw()
    {
        NVec3 raw = _dragRaw;

        // Constrain FIRST, so snapping quantizes the motion that will actually be applied rather than a
        // free 3D position that then gets projected (which lands off-grid).
        if (_grabbedHandle is { } handle)
            raw = VmapHandles.ConstrainDrag(handle, raw);

        // A face push is one-dimensional: only motion along the face normal means anything, and allowing the
        // other two axes would let a careless drag shear the wall sideways.
        if (_dragSelection.Kind == VmapSelectionKind.Face && _dragAxis != NVec3.Zero)
        {
            float along = SnapDistance(NVec3.Dot(raw, _dragAxis));
            _dragDelta = _dragAxis * along;
            _dragSnap = default;
            return;
        }

        // An axis-constrained drag snaps per-component and skips geometry snapping: pulling the Z arrow to a
        // vertex 300 units off in X is not what the mapper asked for, and it is exactly what the free-3D
        // resolver would do.
        if (_grabbedHandle is { Kind: HandleKind.MoveAxis or HandleKind.MovePlane })
        {
            _dragDelta = new NVec3(SnapDistance(raw.X), SnapDistance(raw.Y), SnapDistance(raw.Z));
            _dragSnap = default;
            return;
        }

        PickIndex.EnsureBuilt(_document!, GeometryVersion, IncludeToolBrushes);
        NVec3 resolved = VmapPicking.ResolveDragPosition(
            PickIndex, _dragStartPoint + raw, GridSize, SnapRadius,
            _session!.SelectedBrushIds(), out _dragSnap);
        _dragDelta = resolved - _dragStartPoint;
    }

    /// <summary>
    /// Quantize a distance to the grid, honouring the held-Ctrl inversion (§11.9): Ctrl flips whichever way
    /// the grid toggle is currently set, so you can drop off-grid for one drag without changing the setting,
    /// and equally snap for one drag while working freehand.
    /// </summary>
    private float SnapDistance(float value) => VmapEdit.SnapToGrid(value, EffectiveGridSnap);

    /// <summary>
    /// The grid step a drag actually quantizes to: the grid size when the grid is on, zero (no snapping) when
    /// it is off, and inverted while Ctrl is held.
    ///
    /// Tying this to the grid TOGGLE rather than snapping unconditionally is a correctness fix as much as a
    /// feature: the HUD has always been able to say "Grid: OFF" while every drag still quantized to 64 units,
    /// which is a readout that contradicts what the editor does.
    /// </summary>
    public float EffectiveGridSnap
    {
        get
        {
            bool on = Cvar(EditorGrid.CvarEnabled, 1f) != 0f;
            if (SnapInverted)
                on = !on;
            return on ? GridSize : 0f;
        }
    }

    /// <summary>
    /// True while the grid snap is being temporarily inverted. Set by the host from the Ctrl key; read here so
    /// the drag maths and the HUD tip agree on one piece of state.
    /// </summary>
    public bool SnapInverted { get; set; }

    /// <summary>Commit the drag as a single op. Returns true when geometry actually changed.</summary>
    public bool EndDrag()
    {
        if (!_dragging || _session is null)
            return false;

        VmapSelection sel = _dragSelection;
        NVec3 delta = _dragDelta;
        NVec3 scale = _dragScale;
        float angle = DragAngle;
        ToolMode mode = Mode;

        // Resolve everything that depends on the grabbed handle BEFORE clearing it: RotationAxis() reads the
        // grabbed ring, and the manipulator origin is the scale pivot.
        NVec3 rotAxis = RotationAxis();
        bool havePivot = TryGetManipulatorOrigin(out NVec3 pivot);
        CancelDrag();

        if (mode == ToolMode.Rotate)
        {
            // Rotate the whole selection about its own centre, on the axis of the ring that was grabbed.
            List<int> rotIds = _session.SelectedBrushIds();
            List<int> rotPatches = SelectedPatchIds();
            if (angle == 0f || !havePivot || (rotIds.Count == 0 && rotPatches.Count == 0))
                return false;

            // Entities turn about the vertical axis with their facing keys, which is a different op from the
            // geometry rotate — a spawn's direction lives in a key, not in its shape.
            List<int> rotEntities = SelectedEntityIds();
            if (rotEntities.Count > 0)
            {
                bool turned = _session.Apply(new RotateEntitiesOp(rotEntities, pivot, angle));
                if (rotIds.Count == 0 && rotPatches.Count == 0)
                {
                    if (turned)
                        GeometryVersion++;
                    return turned;
                }
            }

            // ONE op for the whole selection: a mixed brush+patch rotate about a shared pivot has to be a
            // single undo step, because a single drag produced it.
            if (!_session.Apply(new RotateSelectionOp(rotIds, rotPatches, pivot, rotAxis, angle)))
            {
                Log.Info("editor: rotation refused — that would break the brush");
                return false;
            }

            GeometryVersion++;
            return true;
        }

        if (mode == ToolMode.Scale)
        {
            if (scale == NVec3.One || !havePivot)
                return false;

            List<int> scaleBrushes = _session.SelectedBrushIds();
            List<int> scalePatches = SelectedPatchIds();
            if (scaleBrushes.Count == 0 && scalePatches.Count == 0)
                return false;

            if (!_session.Apply(new ScaleSelectionOp(scaleBrushes, scalePatches, pivot, scale)))
            {
                Log.Info("editor: scale refused — that would break the brush");
                return false;
            }
            GeometryVersion++;
            return true;
        }

        if (delta == NVec3.Zero)
            return false;   // a click, not a drag

        // An entity-only selection moves through the entity op: a point entity has no geometry to translate,
        // and a brush entity's move has to travel to the brushes it owns.
        List<int> moveEntities = SelectedEntityIds();
        if (moveEntities.Count > 0 && _session.SelectedBrushIds().Count == 0 && SelectedPatchIds().Count == 0)
        {
            if (!_session.Apply(new MoveEntitiesOp(moveEntities, delta, _document)))
                return false;
            GeometryVersion++;
            return true;
        }

        IVmapOp? op = sel.Kind switch
        {
            // A patch moves as a whole object — it has no plane set to push or corner to refit.
            VmapSelectionKind.Patch => new TranslatePatchesOp(SelectedPatchIds(), delta),
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

    // =============================================================================================
    //  Measure tool (§11.8, §11.9) — distance, angle, and the reachability a desktop editor cannot answer
    // =============================================================================================

    private readonly List<NVec3> _measurePoints = new();

    /// <summary>Points clicked for the current measurement, for the gizmo to draw.</summary>
    public IReadOnlyList<NVec3> MeasurePoints => _measurePoints;

    /// <summary>How many points the current measure mode wants.</summary>
    public int MeasurePointsNeeded => Mode == ToolMode.Angle ? 3 : 2;

    /// <summary>Add a measure point at the crosshair, starting over once the previous measurement is complete.</summary>
    public bool AddMeasurePoint()
    {
        if (Tool != EditorTool.Measure || !TryGetPastePoint(out NVec3 p))
            return false;

        if (_measurePoints.Count >= MeasurePointsNeeded)
            _measurePoints.Clear();
        _measurePoints.Add(p);
        return true;
    }

    /// <summary>Drop the current measurement.</summary>
    public void ClearMeasurePoints() => _measurePoints.Clear();

    /// <summary>
    /// The measurement as a line of text, or empty when not enough points are down.
    ///
    /// Reachability is the mode worth having: it answers "can a player cross this" out of the same
    /// <c>sv_jumpvelocity</c> and <c>sv_gravity</c> the movement code runs on, which is the one question a
    /// desktop editor structurally cannot answer.
    /// </summary>
    public string MeasureReadout()
    {
        if (_measurePoints.Count < 2)
            return _measurePoints.Count == 1
                ? "click a second point"
                : "click two points";

        if (Mode == ToolMode.Angle)
        {
            if (_measurePoints.Count < 3)
                return "click a third point";
            float deg = VmapMeasure.Angle(_measurePoints[0], _measurePoints[1], _measurePoints[2]);
            return $"{deg:0.##}° at the first point";
        }

        if (Mode == ToolMode.Reachability)
            return VmapMeasure.Describe(_measurePoints[0], _measurePoints[1], ReachParams.Default);

        NVec3 a = _measurePoints[0], b = _measurePoints[1];
        return $"{VmapMeasure.Distance(a, b):0.#}u   "
            + $"run {VmapMeasure.HorizontalDistance(a, b):0.#}   "
            + $"rise {VmapMeasure.Rise(a, b):+0.#;-0.#;0}";
    }

    // =============================================================================================
    //  Shader tool (§11.9) — the Surface Inspector
    // =============================================================================================

    /// <summary>
    /// The shader clipboard: the material and alignment lifted by the eyedropper. Separate from the geometry
    /// clipboard because they are used together — copy a wall's shader, then paste geometry somewhere else and
    /// apply the shader to it, without either operation clobbering the other.
    /// </summary>
    public string PickedMaterial { get; private set; } = "";

    /// <summary>Alignment captured alongside <see cref="PickedMaterial"/>.</summary>
    public VmapTexProjection PickedProjection { get; private set; }

    /// <summary>Surface/content flags captured alongside the material.</summary>
    public int PickedSurfaceFlags { get; private set; }

    public int PickedContentFlags { get; private set; }

    /// <summary>True once something has been picked up.</summary>
    public bool HasPickedShader => PickedMaterial.Length > 0;

    /// <summary>The face the crosshair is over, resolved for shader work. Null when aiming at nothing.</summary>
    public (VmapBrush Brush, int FaceIndex)? HoveredFace()
    {
        if (_document is null || !Hover.Hit)
            return null;
        VmapSelection sel = Hover.Selection;
        if (sel.Kind != VmapSelectionKind.Face || sel.FaceIndex < 0)
            return null;
        if (_document.FindBrush(sel.BrushId) is not { } brush || sel.FaceIndex >= brush.Faces.Count)
            return null;
        return (brush, sel.FaceIndex);
    }

    /// <summary>Eyedropper: lift the hovered face's material, alignment and flags.</summary>
    public bool PickShaderAtCrosshair()
    {
        if (HoveredFace() is not { } hit)
            return false;

        VmapFace f = hit.Brush.Faces[hit.FaceIndex];
        PickedMaterial = f.Material;
        PickedProjection = f.Projection;
        PickedSurfaceFlags = f.SurfaceFlags;
        PickedContentFlags = f.ContentFlags;
        Log.Info($"editor: picked {PickedMaterial}");
        return true;
    }

    /// <summary>
    /// The faces a surface edit applies to: the selection when there is one, else whatever the crosshair is
    /// over. Aiming is the fast path for retexturing a wall at a time; selecting is how you do twenty at once.
    /// </summary>
    public List<(int BrushId, int FaceIndex)> ShaderTargets()
    {
        var targets = new List<(int, int)>();
        if (_session is not null)
            foreach (VmapSelection sel in _session.Selection)
                if (sel.Kind == VmapSelectionKind.Face && sel.FaceIndex >= 0)
                    targets.Add((sel.BrushId, sel.FaceIndex));

        if (targets.Count == 0 && HoveredFace() is { } hit)
            targets.Add((hit.Brush.Id, hit.FaceIndex));
        return targets;
    }

    /// <summary>Apply the picked material (and its flags) to the target faces.</summary>
    public bool ApplyShader()
    {
        if (_session is null || !HasPickedShader)
        {
            Log.Info("editor: nothing picked — aim at a face and pick first");
            return false;
        }

        int changed = 0;
        foreach ((int brushId, int faceIndex) in ShaderTargets())
        {
            if (_session.Apply(new SetFaceMaterialOp(brushId, faceIndex, PickedMaterial)))
                changed++;
            _session.Apply(new SetFaceFlagsOp(brushId, faceIndex, PickedSurfaceFlags, PickedContentFlags));
        }

        if (changed == 0)
            return false;
        GeometryVersion++;
        Log.Info($"editor: applied {PickedMaterial} to {changed} face(s)");
        return true;
    }

    /// <summary>
    /// Run one alignment operation over the target faces. Returns how many faces changed.
    ///
    /// Each face is transformed against its OWN winding and normal rather than against a shared frame: fitting
    /// a texture means fitting it to that face, and two faces of different sizes must end up with different
    /// projections even though the mapper asked once.
    /// </summary>
    public int AlignShader(Func<VmapTexProjection, VmapFace, IReadOnlyList<NVec3>, VmapTexProjection> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (_session is null || _document is null)
            return 0;

        int changed = 0;
        foreach ((int brushId, int faceIndex) in ShaderTargets())
        {
            if (_document.FindBrush(brushId) is not { } brush || faceIndex >= brush.Faces.Count)
                continue;

            VmapFace face = brush.Faces[faceIndex];
            NVec3[] winding = VmapWinding.BuildFaceWinding(brush, faceIndex);
            VmapTexProjection next = transform(face.Projection, face, winding);
            if (_session.Apply(new SetFaceProjectionOp(brushId, faceIndex, next)))
                changed++;
        }

        if (changed > 0)
            GeometryVersion++;
        return changed;
    }

    /// <summary>Centre of a face's winding — the anchor a scale or rotate turns about.</summary>
    public static NVec3 FaceCenter(IReadOnlyList<NVec3> winding)
    {
        if (winding is null || winding.Count == 0)
            return NVec3.Zero;
        NVec3 sum = NVec3.Zero;
        foreach (NVec3 v in winding)
            sum += v;
        return sum / winding.Count;
    }

    // =============================================================================================
    //  Clip tool (§11.9) — click points to place a cutting plane, Enter to cut
    // =============================================================================================

    private readonly List<NVec3> _clipPoints = new();

    /// <summary>Points clicked so far for the pending cut, for the gizmo to draw.</summary>
    public IReadOnlyList<NVec3> ClipPoints => _clipPoints;

    /// <summary>Which half a clip keeps. Cycled from the menu; Back is Radiant's default sense.</summary>
    public ClipKeep ClipKeep { get; private set; } = ClipKeep.Back;

    /// <summary>Cycle keep-back → keep-front → keep-both.</summary>
    public void CycleClipKeep()
    {
        ClipKeep = ClipKeep switch
        {
            ClipKeep.Back => ClipKeep.Front,
            ClipKeep.Front => ClipKeep.Both,
            _ => ClipKeep.Back,
        };
        Log.Info($"editor: clip keeps {ClipKeep}");
    }

    /// <summary>How many points the current clip mode needs before it defines a plane.</summary>
    public int ClipPointsNeeded => Mode switch
    {
        ToolMode.ThreePoint => 3,
        ToolMode.TwoPoint => 2,
        _ => 0,                     // ViewPlane needs none — the camera IS the plane
    };

    /// <summary>Add a clip point at the crosshair. Returns true when the point was taken.</summary>
    public bool AddClipPoint()
    {
        if (Tool != EditorTool.Clip || !TryGetPastePoint(out NVec3 p))
            return false;

        // Starting a new cut after one completed clears the old points rather than accumulating: the previous
        // plane is already applied or abandoned, and leaving its points around would silently change what the
        // next click means.
        if (_clipPoints.Count >= ClipPointsNeeded && ClipPointsNeeded > 0)
            _clipPoints.Clear();

        _clipPoints.Add(p);
        return true;
    }

    /// <summary>Discard the pending cut.</summary>
    public void ClearClipPoints() => _clipPoints.Clear();

    /// <summary>
    /// The cutting plane the current mode and points define.
    ///
    /// Two-point is the interesting one: two clicked points fix a LINE, not a plane, so the third constraint
    /// has to come from somewhere. It comes from the view direction — the cut runs along the line you drew and
    /// extends away from you, which is the gesture a mapper means by "slice it here" in a first-person view.
    /// </summary>
    public bool TryGetClipPlane(out VmapPlane plane)
    {
        plane = default;
        if (_camera is null)
            return false;

        NVec3 forward = Coords.ToQuake(-_camera.GlobalTransform.Basis.Z);

        switch (Mode)
        {
            case ToolMode.ViewPlane:
            {
                if (!TryGetPastePoint(out NVec3 at))
                    return false;
                plane = new VmapPlane(NVec3.Normalize(forward), NVec3.Dot(at, forward));
                return true;
            }

            case ToolMode.TwoPoint:
            {
                if (_clipPoints.Count < 2)
                    return false;
                NVec3 along = _clipPoints[1] - _clipPoints[0];
                if (along.LengthSquared() < 1e-6f)
                    return false;
                NVec3 n = NVec3.Cross(along, forward);
                if (n.LengthSquared() < 1e-6f)
                    return false;       // looking straight down the line: no plane is determined
                n = NVec3.Normalize(n);
                plane = new VmapPlane(n, NVec3.Dot(_clipPoints[0], n));
                return true;
            }

            case ToolMode.ThreePoint:
            {
                if (_clipPoints.Count < 3)
                    return false;
                if (!VmapPlane.TryFromPoints(_clipPoints[0], _clipPoints[1], _clipPoints[2], out plane))
                    return false;
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Apply the pending cut to the selection. Returns true when at least one brush was actually crossed.
    /// </summary>
    public bool ApplyClip()
    {
        if (_session is null || !TryGetClipPlane(out VmapPlane plane))
            return false;

        List<int> ids = _session.SelectedBrushIds();
        if (ids.Count == 0)
        {
            Log.Info("editor: clip needs a selection — click the brushes to cut first");
            return false;
        }

        var op = new ClipSelectionOp(ids, plane, ClipKeep);
        if (!_session.Apply(op))
        {
            Log.Info("editor: the cutting plane missed every selected brush");
            return false;
        }

        // The off-cuts join the selection when both halves are kept, so a split can be immediately dragged
        // apart without re-picking the piece that did not exist a moment ago.
        foreach (int id in op.CreatedBrushIds)
            _session.Selection.Add(VmapSelection.OfBrush(id));

        _clipPoints.Clear();
        GeometryVersion++;
        Log.Info($"editor: {op.Describe()}");
        return true;
    }

    // =============================================================================================
    //  Paste placement (§11.9) — the ghost follows the crosshair, a click puts it down
    // =============================================================================================

    /// <summary>
    /// Where the clipboard would land right now: the crosshair's surface hit, snapped to the grid, or a point
    /// out in front of the camera when the crosshair is aimed at nothing.
    ///
    /// Aiming at a surface rather than free-floating is what makes paste useful for the common case — dropping
    /// a copied light fixture onto a wall lands it ON the wall instead of somewhere near it.
    /// </summary>
    public bool TryGetPastePoint(out NVec3 point)
    {
        point = NVec3.Zero;
        if (_camera is null)
            return false;

        (NVec3 origin, NVec3 dir) = CameraRay();

        if (_document is not null)
        {
            PickIndex.EnsureBuilt(_document, GeometryVersion, IncludeToolBrushes);
            VmapPickResult hit = VmapPicking.Pick(
                PickIndex, origin, dir, VmapSelectionKind.Brush, GrabRadius, PickRange);
            if (hit.Hit)
            {
                point = SnapPoint(hit.Point);
                return true;
            }
        }

        // Nothing under the crosshair: park it a fixed distance out so the ghost is still visible and placeable
        // in open space rather than vanishing.
        point = SnapPoint(origin + dir * PasteFallbackDistance);
        return true;
    }

    /// <summary>How far in front of the camera a paste lands when the crosshair is aimed at open space.</summary>
    private const float PasteFallbackDistance = 256f;

    private NVec3 SnapPoint(NVec3 p)
    {
        float g = EffectiveGridSnap;
        return g <= 0f ? p : VmapEdit.SnapToGrid(p, g);
    }

    /// <summary>
    /// Put the clipboard down at the crosshair. Returns true when something was placed.
    ///
    /// The paste becomes the new SELECTION, which is what lets you immediately grab a handle and nudge it —
    /// the alternative leaves you having to find and click the thing you just created.
    /// </summary>
    public bool PasteAtCrosshair()
    {
        if (_session is null || Clipboard.IsEmpty || !TryGetPastePoint(out NVec3 at))
            return false;

        var op = new PasteOp(Clipboard, at);
        if (!_session.Apply(op))
        {
            Log.Info("editor: paste refused — the pasted geometry would be invalid here");
            return false;
        }

        _session.Selection.Clear();
        foreach (int id in op.CreatedBrushIds)
            _session.Selection.Add(VmapSelection.OfBrush(id));
        foreach (int id in op.CreatedPatchIds)
            _session.Selection.Add(VmapSelection.OfPatch(id));

        GeometryVersion++;
        Log.Info($"editor: pasted {op.CreatedBrushIds.Count} brushes, {op.CreatedPatchIds.Count} patches");
        return true;
    }

    /// <summary>
    /// Entity class descriptors, loaded from the game's own scripts/entities.ent. Fed by the host, because
    /// reading it needs the VFS. Null until then, and everything degrades to plain boxes rather than failing.
    /// </summary>
    public EntityDefs? Defs
    {
        get => PickIndex.Defs;
        set
        {
            PickIndex.Defs = value;
            PickIndex.Invalidate();
        }
    }

    /// <summary>Entity ids in the current selection, deduplicated.</summary>
    public List<int> SelectedEntityIds()
    {
        var ids = new List<int>();
        if (_session is null)
            return ids;
        foreach (VmapSelection s in _session.Selection)
            if (s.Kind == VmapSelectionKind.Entity && !ids.Contains(s.EntityId))
                ids.Add(s.EntityId);
        return ids;
    }

    /// <summary>Patch ids in the current selection, deduplicated — the patch counterpart of SelectedBrushIds.</summary>
    public List<int> SelectedPatchIds()
    {
        var ids = new List<int>();
        if (_session is null)
            return ids;
        foreach (VmapSelection s in _session.Selection)
            if (s.Kind == VmapSelectionKind.Patch && !ids.Contains(s.PatchId))
                ids.Add(s.PatchId);
        return ids;
    }

    /// <summary>Abandon an in-flight drag without applying anything.</summary>
    public void CancelDrag()
    {
        _dragging = false;
        _grabbedHandle = null;
        _dragScale = NVec3.One;
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

    /// <summary>
    /// Cycle to the next tool. Skips tools that are not implemented yet: the context menu shows them (so the
    /// roster reads as a plan) but a keyboard cycle that can strand you in a tool which does nothing is a
    /// different thing entirely, and there would be no feedback saying why the editor stopped responding.
    /// </summary>
    public void CycleTool()
    {
        IReadOnlyList<EditorTool> all = EditorTools.All;
        int at = 0;
        for (int i = 0; i < all.Count; i++)
            if (all[i] == Tool)
            {
                at = i;
                break;
            }

        for (int step = 1; step <= all.Count; step++)
        {
            EditorTool next = all[(at + step) % all.Count];
            if (!EditorTools.IsImplemented(next))
                continue;
            SetTool(next);
            return;
        }
    }

    /// <summary>
    /// Set the tool directly, carrying the current mode across when the new tool also offers it (Brush→Patch
    /// while rotating stays in Rotate) and falling back to the new tool's default when it does not.
    /// </summary>
    public void SetTool(EditorTool tool)
    {
        Tool = tool;
        Mode = EditorTools.CarryMode(tool, Mode);
        CancelDrag();
        Log.Info($"editor tool: {EditorTools.ActionLine(Tool, Mode)}");
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

        List<int> entityIds = SelectedEntityIds();
        List<int> ids = _session.SelectedBrushIds();
        if (ids.Count == 0 && entityIds.Count == 0)
            return false;

        bool any = false;

        // Entities first. Deleting a brush entity takes its geometry with it, so doing brushes first would
        // leave the entity op with nothing to find and the ownership links already half-unhooked.
        if (entityIds.Count > 0 && _session.Apply(new DeleteEntitiesOp(entityIds, _document)))
            any = true;

        // Re-read: an entity delete may have removed brushes that were also selected directly.
        List<int> remaining = ids.FindAll(id => _document?.FindBrush(id) is not null);
        if (remaining.Count > 0 && _session.Apply(new DeleteBrushesOp(remaining)))
            any = true;

        if (!any)
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
