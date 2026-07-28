namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// Which sub-object a pick resolves to (design doc §11.9, the WHAT layer). A tool decides what a click
/// SELECTS and nothing else; what a drag then DOES is <see cref="ToolMode"/>.
///
/// Keeping the two apart is what removes the repetition the first cut of the editor menu had: "copy" and
/// "paste" are not properties of being in brush mode, they act on whatever the current tool selected, so they
/// live once on the selection menu instead of being repeated under all seven tools.
/// </summary>
public enum EditorTool
{
    /// <summary>
    /// No tool: nothing is picked and nothing is highlighted. Kept because it is the only way to LOOK at the
    /// map — the hover outline is drawn over the very surfaces whose lighting you are trying to judge, and it
    /// also skips the pick cost entirely.
    /// </summary>
    None,

    /// <summary>
    /// General selection at object granularity; transforms nothing. The tool a session opens in: safe to click
    /// anything, which <see cref="None"/> cannot be because it does not pick at all.
    /// </summary>
    Select,

    /// <summary>Select and transform whole brushes.</summary>
    Brush,

    /// <summary>Select faces; a face push slides the plane along its own normal.</summary>
    Face,

    /// <summary>Select edges; a drag moves both endpoints and refits the meeting planes.</summary>
    Edge,

    /// <summary>
    /// Select vertices; a drag moves one corner and refits every plane meeting there. NOT a variant of
    /// <see cref="Face"/>: a face push preserves the brush's topology (a box stays a box), while a vertex drag
    /// re-derives the containing planes, which is the only way to turn a box into a wedge.
    /// </summary>
    Vertex,

    /// <summary>Select and transform whole bezier patches, or edit their control grid.</summary>
    Patch,

    /// <summary>Material and texture-projection work — the Surface Inspector.</summary>
    Shader,

    /// <summary>Point and brush entities: items, spawns, objectives, triggers, lights.</summary>
    Entity,

    /// <summary>Bot navigation nodes and their links.</summary>
    Waypoint,

    /// <summary>
    /// Plane split. Its own tool rather than a mode under <see cref="Brush"/> because it is the most-used
    /// Radiant power tool and because it is convexity-safe by construction: both halves of a convex solid are
    /// convex, so it sidesteps the plane-refit validity problem the drag tools have.
    /// </summary>
    Clip,

    /// <summary>Distance and angle between points, plus reachability under the real movement physics.</summary>
    Measure,
}

/// <summary>
/// What a handle drag does (design doc §11.9, the HOW layer). Per-tool: <see cref="EditorTools.ModesFor"/> is
/// the validity matrix, so the mode menu only ever lists modes the current tool can actually perform and there
/// are no rows that do nothing.
///
/// Deliberately does NOT contain copy, delete or deselect. Those fire once and have no ongoing state, so a
/// "copy mode" would be a state you can sit in that does nothing; they are actions on the selection menu.
/// <see cref="Paste"/> is the one exception and it is genuinely a mode: a ghost follows the crosshair until you
/// click to place it.
/// </summary>
public enum ToolMode
{
    /// <summary>No mode (the tool is <see cref="EditorTool.None"/>); the mode row hides.</summary>
    None,

    // ---- shared manipulation ----

    /// <summary>Translate along a grabbed axis or plane pad.</summary>
    Move,

    /// <summary>Rotate about a grabbed ring's axis.</summary>
    Rotate,

    /// <summary>Stretch along a grabbed box handle's axis, or uniformly from the centre handle.</summary>
    Scale,

    /// <summary>Drag out new geometry, or open the tool's creation dialog.</summary>
    Create,

    /// <summary>Ghost of the clipboard follows the crosshair; left-click places it.</summary>
    Paste,

    // ---- Select ----

    /// <summary>Whole brush / patch / entity.</summary>
    Object,

    /// <summary>A single face of whatever was hit.</summary>
    Face,

    // ---- Face ----

    /// <summary>Sweep the face winding along its normal into a new brush.</summary>
    Extrude,

    // ---- Edge ----

    /// <summary>Insert a bevel plane along the edge (the clipper, aimed at an edge).</summary>
    Bevel,

    // ---- Vertex ----

    /// <summary>Snap the selected corner(s) to the nearest grid intersection.</summary>
    SnapToGrid,

    // ---- Patch ----

    /// <summary>Drag individual control points of the patch grid.</summary>
    ControlPoints,

    /// <summary>Open the patch properties dialog (matrix, rows/columns, thicken, cap, texture).</summary>
    Modify,

    // ---- Shader ----

    /// <summary>Eyedropper: copy the hovered face's material and projection into the shader clipboard.</summary>
    PickShader,

    /// <summary>Apply the shader clipboard to the hovered or selected faces.</summary>
    ApplyShader,

    /// <summary>Open the shader browser.</summary>
    Browse,

    /// <summary>Refit the projection so one texture tile spans the face.</summary>
    FitProjection,

    /// <summary>Reset to the shader's natural scale, keeping the rotation.</summary>
    NaturalProjection,

    /// <summary>Reset to the axis-aligned box projection for the face normal.</summary>
    AxialProjection,

    /// <summary>Drag to slide the texture across the face.</summary>
    ShiftUv,

    /// <summary>Drag to scale the texture on the face.</summary>
    ScaleUv,

    /// <summary>Drag to rotate the texture on the face.</summary>
    RotateUv,

    /// <summary>Edit the face's surface and content flags.</summary>
    Flags,

    // ---- Entity ----

    /// <summary>Open the entity key/value inspector.</summary>
    Properties,

    // ---- Waypoint ----

    /// <summary>Place an ordinary waypoint where the crosshair meets the floor.</summary>
    Place,

    /// <summary>Place a jump waypoint (place it at least 60qu before the jump start; the next one is its destination).</summary>
    PlaceJump,

    /// <summary>Place a crouch waypoint (links only to very close waypoints).</summary>
    PlaceCrouch,

    /// <summary>Place a support waypoint (the next one is the destination whose incoming links are removed).</summary>
    PlaceSupport,

    /// <summary>Delete the aimed waypoint.</summary>
    Remove,

    /// <summary>Mark the aimed waypoint as the origin of a new hardwired link.</summary>
    Hardwire,

    /// <summary>Lock link display to the aimed waypoint (aim at nothing to unlock).</summary>
    Lock,

    /// <summary>Reveal waypoints and items unreachable from here, and spawns with no nearest waypoint.</summary>
    Unreachable,

    /// <summary>Relink every waypoint as if it had just been respawned.</summary>
    RelinkAll,

    /// <summary>Get or set the map's symmetry origin / axis.</summary>
    Symmetry,

    // ---- Clip ----

    /// <summary>Two clicked points plus the view direction define the cutting plane.</summary>
    TwoPoint,

    /// <summary>Three clicked points define the cutting plane exactly.</summary>
    ThreePoint,

    /// <summary>Cut along the current view plane through the selection.</summary>
    ViewPlane,

    // ---- Measure ----

    /// <summary>Distance between two clicked points.</summary>
    Distance,

    /// <summary>Angle at a clicked vertex between two clicked arms.</summary>
    Angle,

    /// <summary>
    /// Colour the measured gap by whether a walk / jump / crouch-jump / bhop actually clears it, run through
    /// the game's own movement physics. The measurement Radiant cannot make.
    /// </summary>
    Reachability,
}

/// <summary>Which manipulator handles to draw, derived from the mode rather than stored separately.</summary>
public enum HandleSet
{
    /// <summary>No handles — the mode is not a spatial transform.</summary>
    None,

    /// <summary>Three axis arrows plus three plane pads.</summary>
    Move,

    /// <summary>Three rotation rings.</summary>
    Rotate,

    /// <summary>Six box handles on the selection bounds plus a uniform centre handle.</summary>
    Scale,
}

/// <summary>
/// The tool/mode vocabulary: which modes each tool offers, what each is called, and what a tool picks.
///
/// Godot-free on purpose. It lives here rather than next to <c>EditorController</c> so it can be unit-tested
/// (the test project references <c>src/</c> only), and because the same tables drive three consumers that must
/// not disagree: the context menu's rows, the HUD's action line, and the controller's own dispatch.
/// </summary>
public static class EditorTools
{
    /// <summary>Every tool, in menu order.</summary>
    public static readonly IReadOnlyList<EditorTool> All = new[]
    {
        EditorTool.None, EditorTool.Select, EditorTool.Brush, EditorTool.Face, EditorTool.Edge,
        EditorTool.Vertex, EditorTool.Patch, EditorTool.Shader, EditorTool.Entity, EditorTool.Waypoint,
        EditorTool.Clip, EditorTool.Measure,
    };

    private static readonly ToolMode[] NoModes = Array.Empty<ToolMode>();

    /// <summary>
    /// The modes a tool offers, in menu order. Empty for <see cref="EditorTool.None"/>, which is what makes the
    /// menu's Mode row hide rather than show an empty submenu.
    /// </summary>
    public static IReadOnlyList<ToolMode> ModesFor(EditorTool tool) => tool switch
    {
        EditorTool.None => NoModes,

        EditorTool.Select => new[] { ToolMode.Object, ToolMode.Face },

        EditorTool.Brush => new[]
        {
            ToolMode.Move, ToolMode.Rotate, ToolMode.Scale, ToolMode.Create, ToolMode.Paste,
        },

        // No Scale: a face has no size of its own, its outline is wherever the neighbouring planes cut it.
        // What "scale a face" would mean is a brush scale about the face centroid, which lives under Brush.
        // No Split either: splitting a face is adding a plane through the brush, which is the Clip tool.
        EditorTool.Face => new[]
        {
            ToolMode.Move, ToolMode.Rotate, ToolMode.Extrude, ToolMode.Create, ToolMode.Paste,
        },

        // Rotate and scale on a SINGLE edge are ill-defined (an edge has no facing and no extent to scale
        // against), so the edge tool is a move plus the bevel that "split an edge" actually means.
        EditorTool.Edge => new[] { ToolMode.Move, ToolMode.Bevel },

        EditorTool.Vertex => new[] { ToolMode.Move, ToolMode.SnapToGrid },

        EditorTool.Patch => new[]
        {
            ToolMode.Move, ToolMode.Rotate, ToolMode.Scale, ToolMode.ControlPoints,
            ToolMode.Modify, ToolMode.Create, ToolMode.Paste,
        },

        EditorTool.Shader => new[]
        {
            ToolMode.PickShader, ToolMode.ApplyShader, ToolMode.Browse,
            ToolMode.FitProjection, ToolMode.NaturalProjection, ToolMode.AxialProjection,
            ToolMode.ShiftUv, ToolMode.ScaleUv, ToolMode.RotateUv, ToolMode.Flags,
        },

        EditorTool.Entity => new[]
        {
            ToolMode.Move, ToolMode.Rotate, ToolMode.Scale, ToolMode.Create,
            ToolMode.Properties, ToolMode.Paste,
        },

        EditorTool.Waypoint => new[]
        {
            ToolMode.Place, ToolMode.PlaceJump, ToolMode.PlaceCrouch, ToolMode.PlaceSupport,
            ToolMode.Remove, ToolMode.Hardwire, ToolMode.Lock, ToolMode.Unreachable,
            ToolMode.RelinkAll, ToolMode.Symmetry,
        },

        EditorTool.Clip => new[] { ToolMode.TwoPoint, ToolMode.ThreePoint, ToolMode.ViewPlane },

        EditorTool.Measure => new[] { ToolMode.Distance, ToolMode.Angle, ToolMode.Reachability },

        _ => NoModes,
    };

    /// <summary>True when <paramref name="mode"/> is one <paramref name="tool"/> offers.</summary>
    public static bool Supports(EditorTool tool, ToolMode mode)
    {
        IReadOnlyList<ToolMode> modes = ModesFor(tool);
        for (int i = 0; i < modes.Count; i++)
            if (modes[i] == mode)
                return true;
        return false;
    }

    /// <summary>
    /// The mode a tool starts in — its first, which is <see cref="ToolMode.Move"/> or the nearest thing to it
    /// for every tool that has one. Switching tools must never leave you in a mode the new tool cannot do.
    /// </summary>
    public static ToolMode DefaultMode(EditorTool tool)
    {
        IReadOnlyList<ToolMode> modes = ModesFor(tool);
        return modes.Count > 0 ? modes[0] : ToolMode.None;
    }

    /// <summary>
    /// Carry the current mode across a tool switch when the new tool also offers it, else fall back to the new
    /// tool's default. Going Brush→Patch while in Rotate should stay in Rotate; going Brush→Waypoint cannot.
    /// </summary>
    public static ToolMode CarryMode(EditorTool newTool, ToolMode current)
        => Supports(newTool, current) ? current : DefaultMode(newTool);

    /// <summary>What a pick resolves to for this tool.</summary>
    public static VmapSelectionKind PickKind(EditorTool tool) => tool switch
    {
        EditorTool.Brush or EditorTool.Select => VmapSelectionKind.Brush,
        EditorTool.Edge => VmapSelectionKind.Edge,
        EditorTool.Vertex => VmapSelectionKind.Vertex,
        EditorTool.Patch => VmapSelectionKind.Patch,
        _ => VmapSelectionKind.Face,
    };

    /// <summary>Which handles a mode draws. Modes that open a dialog or fire once draw none.</summary>
    public static HandleSet HandlesFor(ToolMode mode) => mode switch
    {
        ToolMode.Move or ToolMode.ControlPoints or ToolMode.ShiftUv => HandleSet.Move,
        ToolMode.Rotate or ToolMode.RotateUv => HandleSet.Rotate,
        ToolMode.Scale or ToolMode.ScaleUv => HandleSet.Scale,
        _ => HandleSet.None,
    };

    /// <summary>
    /// Whether the tool actually does anything yet (roadmap E7 ships the rails, E8 the tools). The menu shows
    /// unimplemented tools rather than hiding them, marked, so the roster reads as a plan instead of leaving a
    /// mapper clicking a row that silently does nothing.
    /// </summary>
    public static bool IsImplemented(EditorTool tool) => tool switch
    {
        EditorTool.None or EditorTool.Select or EditorTool.Brush or EditorTool.Face
            or EditorTool.Edge or EditorTool.Vertex or EditorTool.Patch or EditorTool.Clip => true,
        _ => false,
    };

    /// <summary>Display name of a tool.</summary>
    public static string Label(EditorTool tool) => tool switch
    {
        EditorTool.None => "None",
        EditorTool.Select => "Select",
        EditorTool.Brush => "Brush",
        EditorTool.Face => "Face",
        EditorTool.Edge => "Edge",
        EditorTool.Vertex => "Vertex",
        EditorTool.Patch => "Patch",
        EditorTool.Shader => "Shader",
        EditorTool.Entity => "Entity",
        EditorTool.Waypoint => "Waypoint",
        EditorTool.Clip => "Clip",
        EditorTool.Measure => "Measure",
        _ => tool.ToString(),
    };

    /// <summary>
    /// Display name of a mode. Spelled out rather than derived from the enum name: "ShiftUv" would render as
    /// "Shift Uv" and "PlaceJump" as "Place Jump", and a menu is read far more often than it is written.
    /// </summary>
    public static string Label(ToolMode mode) => mode switch
    {
        ToolMode.None => "None",
        ToolMode.Move => "Move",
        ToolMode.Rotate => "Rotate",
        ToolMode.Scale => "Scale",
        ToolMode.Create => "Create",
        ToolMode.Paste => "Paste",
        ToolMode.Object => "Object",
        ToolMode.Face => "Face",
        ToolMode.Extrude => "Extrude",
        ToolMode.Bevel => "Bevel",
        ToolMode.SnapToGrid => "Snap to grid",
        ToolMode.ControlPoints => "Control points",
        ToolMode.Modify => "Modify...",
        ToolMode.PickShader => "Pick shader",
        ToolMode.ApplyShader => "Apply shader",
        ToolMode.Browse => "Browse...",
        ToolMode.FitProjection => "Fit texture",
        ToolMode.NaturalProjection => "Natural scale",
        ToolMode.AxialProjection => "Axial projection",
        ToolMode.ShiftUv => "Shift texture",
        ToolMode.ScaleUv => "Scale texture",
        ToolMode.RotateUv => "Rotate texture",
        ToolMode.Flags => "Surface flags...",
        ToolMode.Properties => "Properties...",
        ToolMode.Place => "Place waypoint",
        ToolMode.PlaceJump => "Place jump",
        ToolMode.PlaceCrouch => "Place crouch",
        ToolMode.PlaceSupport => "Place support",
        ToolMode.Remove => "Remove waypoint",
        ToolMode.Hardwire => "Hardwire link",
        ToolMode.Lock => "Lock link display",
        ToolMode.Unreachable => "Show unreachable",
        ToolMode.RelinkAll => "Relink all",
        ToolMode.Symmetry => "Symmetry...",
        ToolMode.TwoPoint => "Two-point clip",
        ToolMode.ThreePoint => "Three-point clip",
        ToolMode.ViewPlane => "Clip on view plane",
        ToolMode.Distance => "Distance",
        ToolMode.Angle => "Angle",
        ToolMode.Reachability => "Reachability",
        _ => mode.ToString(),
    };

    /// <summary>
    /// The HUD action line (design doc §11.9): <c>Tool &gt; Mode: subject</c>, e.g.
    /// <c>Select &gt; Copy: Brush #412, Devastator (#88)</c> or <c>Entity &gt; Create: Devastator</c>.
    /// <paramref name="subject"/> is what the verb is about to act on; omit it and the line is just the state.
    /// </summary>
    public static string ActionLine(EditorTool tool, ToolMode mode, string subject = "")
    {
        string head = mode == ToolMode.None
            ? Label(tool)
            : $"{Label(tool)} > {Label(mode)}";
        return string.IsNullOrEmpty(subject) ? head : $"{head}: {subject}";
    }
}
