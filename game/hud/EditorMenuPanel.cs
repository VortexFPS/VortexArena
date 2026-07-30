using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vmap;
using VortexArena.Game.Vmap;

namespace VortexArena.Game.Hud;

/// <summary>
/// The map editor's context menu (design doc §11.9): right-click and release pops a list beside the crosshair.
///
/// Three decisions shape this file.
///
/// <b>Submenus REPLACE rather than cascade.</b> Descending swaps the row list in place and puts a back row on
/// top. Cascading panels need hover-timing and somewhere to put a second column, and neither survives being
/// driven by the number keys — which is the input path that has to keep working, because the 3D view is a
/// crosshair view and reaching for a pointer is the thing the menu exists to avoid.
///
/// <b>Rows are built LIVE, every time a menu is entered.</b> The mode list depends on the current tool, the
/// undo row names the step it would undo, and the toggles show their real state. A tree built once at open
/// would be stale by the second click.
///
/// <b>Every action is a console command.</b> Rows carry a command string rather than a delegate, so the menu,
/// the keybinds and the console all drive the editor through one vocabulary. That is what stops the menu from
/// growing its own private half of the editor's API, and it makes every row scriptable for free.
/// </summary>
public partial class EditorMenuPanel : HudPanel
{
    // No RegisterDefaults here: `hud_panel_editormenu` and its pos/size/bg family are already seeded from the
    // HudLayoutDefaults table (HudConfig walks Ids), so declaring them again would be a second source of truth
    // for the same names.

    /// <summary>Rows shown before paging. Ten keeps every row reachable on a number key.</summary>
    public const int MaxRows = 10;

    /// <summary>One row of the menu.</summary>
    private sealed class Row
    {
        /// <summary>Left-hand text.</summary>
        public string Label = "";

        /// <summary>Right-hand text: the current value, a bind, or why the row is unavailable.</summary>
        public string Detail = "";

        /// <summary>Console command to run when picked. Empty for a submenu or the back row.</summary>
        public string Command = "";

        /// <summary>Builds this row's submenu when picked. Null for a leaf.</summary>
        public Func<List<Row>>? Submenu;

        /// <summary>Title shown while inside the submenu.</summary>
        public string SubmenuTitle = "";

        /// <summary>False draws the row dimmed and refuses the pick.</summary>
        public bool Enabled = true;

        /// <summary>Draws a checkbox reflecting this state. Null for rows that are not toggles.</summary>
        public bool? Checked;

        /// <summary>The back row, which pops the nav stack.</summary>
        public bool IsBack;

        /// <summary>Keep the menu open after picking (toggles and anything you would repeat).</summary>
        public bool KeepOpen;
    }

    /// <summary>One level of the navigation stack.</summary>
    private readonly record struct Level(string Title, Func<List<Row>> Build);

    private readonly List<Level> _stack = new();
    private readonly List<Row> _rows = new();
    private bool _open;
    private int _hover = -1;
    private Vector2 _anchor;
    private int _page;

    // ---- host-supplied context ----

    /// <summary>The live controller, for the state the rows display. Null outside a session.</summary>
    public EditorController? Controller { get; set; }

    /// <summary>Where picked commands go — the shared console interpreter.</summary>
    public Action<string>? CommandSink { get; set; }

    /// <summary>True when the orthographic view is open (changes the view rows and the anchor).</summary>
    public bool OrthoOpen { get; set; }

    /// <summary>
    /// Fly-speed multiplier, fed by the host. Read from the host rather than the controller because it lives
    /// on the SERVER's player entity (QC sys_phys_spectator_control drives it off impulses), so the client-side
    /// controller has no authority over it and should not pretend to.
    /// </summary>
    public float FlySpeed { get; set; } = 1f;

    /// <summary>True once the host has confirmed this is an editor session; the panel is silent until then.</summary>
    public bool IsEditorSession { get; set; }

    /// <summary>QC-style open state, read by the host's cursor-ownership gate.</summary>
    public bool IsOpen => _open && _rows.Count > 0;

    public override bool IsDynamic => true;

    // =====================================================================================
    //  Lifecycle
    // =====================================================================================

    public override void _Process(double delta)
    {
        bool show = IsEditorSession && _open && ShowModeCvar() != 0;
        if (show != Visible)
            Visible = show;
        if (!show)
            return;

        UpdateHover();
        QueueRedraw();
    }

    /// <summary>
    /// Open at <paramref name="anchor"/> (viewport pixels). In the 3D view the host passes the crosshair; in
    /// ortho it passes the pointer, which is already where the mapper is looking.
    /// </summary>
    public void Open(Vector2 anchor)
    {
        _anchor = anchor;
        _stack.Clear();
        _stack.Add(new Level("", BuildRoot));
        _page = 0;
        Rebuild();
        if (_rows.Count == 0)
            return;

        _open = true;
        _hover = -1;

        // Take the cursor and the keyboard the same way the quickmenu and the maximized radar do. Without the
        // focus grab a Control receives mouse events by rect but never key events, so the digits would fall
        // through to the editor's own bind layer and pick a tool instead of a row.
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        GrabFocus();
        QueueRedraw();
    }

    /// <summary>Close and hand input back to the editor.</summary>
    public void Close()
    {
        _open = false;
        _rows.Clear();
        _stack.Clear();
        _hover = -1;
        _page = 0;
        MouseFilter = MouseFilterEnum.Ignore;
        if (HasFocus())
            ReleaseFocus();
        FocusMode = FocusModeEnum.None;
        QueueRedraw();
    }

    /// <summary>Toggle, for the bind.</summary>
    public void Toggle(Vector2 anchor)
    {
        if (_open) Close();
        else Open(anchor);
    }

    /// <summary>Rebuild the current level's rows from live editor state.</summary>
    private void Rebuild()
    {
        _rows.Clear();
        if (_stack.Count == 0)
            return;

        Level level = _stack[^1];
        if (_stack.Count > 1)
            _rows.Add(new Row { Label = "back", Detail = level.Title, IsBack = true });

        _rows.AddRange(level.Build());
    }

    private void Descend(Row row)
    {
        if (row.Submenu is null)
            return;
        _stack.Add(new Level(row.SubmenuTitle.Length > 0 ? row.SubmenuTitle : row.Label, row.Submenu));
        _page = 0;
        _hover = -1;
        Rebuild();
    }

    private void Ascend()
    {
        if (_stack.Count <= 1)
        {
            Close();
            return;
        }
        _stack.RemoveAt(_stack.Count - 1);
        _page = 0;
        _hover = -1;
        Rebuild();
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _rows.Count)
            return;

        Row row = _rows[index];
        if (row.IsBack)
        {
            Ascend();
            return;
        }
        if (!row.Enabled)
            return;

        if (row.Submenu is not null)
        {
            Descend(row);
            return;
        }

        if (row.Command.Length > 0)
            CommandSink?.Invoke(row.Command);

        if (row.KeepOpen)
            Rebuild();      // the row's state just changed; redraw it with the new value
        else
            Close();
    }

    // =====================================================================================
    //  The tree
    // =====================================================================================

    private List<Row> BuildRoot()
    {
        EditorController? c = Controller;
        var rows = new List<Row>(10)
        {
            new()
            {
                Label = "Tools",
                Detail = c is null ? "" : EditorTools.Label(c.Tool),
                SubmenuTitle = "Tools",
                Submenu = BuildTools,
            },
        };

        // The Mode row hides entirely when the tool has no modes, rather than opening an empty submenu.
        if (c is not null && EditorTools.ModesFor(c.Tool).Count > 0)
            rows.Add(new Row
            {
                Label = "Mode",
                Detail = EditorTools.Label(c.Mode),
                SubmenuTitle = $"{EditorTools.Label(c.Tool)} mode",
                Submenu = BuildModes,
            });

        rows.Add(new Row { Label = "Selection", SubmenuTitle = "Selection", Submenu = BuildSelection });
        rows.Add(new Row { Label = "CSG", SubmenuTitle = "CSG", Submenu = BuildCsg });
        rows.Add(new Row { Label = "Hide / Region", SubmenuTitle = "Hide / Region", Submenu = BuildRegion });

        // The clip tool's keep-half choice is a separate axis from its placement mode, so it gets its own row
        // rather than being folded into the mode list where picking "keep front" would deselect "two-point".
        if (c is { Tool: EditorTool.Clip })
            rows.Add(new Row
            {
                Label = "Clip keeps",
                Detail = c.ClipKeep.ToString().ToLowerInvariant(),
                Command = "editor_clip keep",
                KeepOpen = true,
            });

        string undoLabel = c?.Session?.UndoLabel ?? "";
        rows.Add(new Row
        {
            Label = "Undo",
            Detail = undoLabel.Length > 0 ? undoLabel : "nothing to undo",
            Command = "editor_undo",
            Enabled = undoLabel.Length > 0,
        });

        rows.Add(new Row
        {
            Label = "History...",
            Detail = Pending("E8"),
            Command = "editor_history",
            Enabled = false,
        });

        rows.Add(new Row { Label = "View", SubmenuTitle = "View", Submenu = BuildView });
        rows.Add(new Row { Label = "Lighting", SubmenuTitle = "Lighting", Submenu = BuildLighting });

        rows.Add(new Row
        {
            Label = "Map info...",
            Detail = Pending("E8"),
            Command = "editor_mapinfo",
            Enabled = false,
        });

        rows.Add(new Row { Label = "Prefabs", SubmenuTitle = "Prefabs", Submenu = BuildPrefabs });

        rows.Add(new Row
        {
            Label = "Bots in playtest",
            // Flow and item timing are not things you can judge flying around an empty room.
            Detail = "2 bots",
            Command = "editor_bots 2",
        });

        rows.Add(new Row
        {
            Label = "Save map",
            // Unsaved work is the one state a mapper must never be unsure about, so the row says it outright
            // rather than leaving them to guess from a title bar.
            Detail = c?.Session is { IsDirty: true } ? "UNSAVED CHANGES" : "saved",
            Command = "editor_save",
        });

        rows.Add(new Row
        {
            Label = "Playtest",
            Detail = BoundKey("editor_playtest"),
            Command = "editor_playtest",
        });

        return rows;
    }

    private List<Row> BuildTools()
    {
        var rows = new List<Row>(EditorTools.All.Count);
        EditorTool current = Controller?.Tool ?? EditorTool.None;

        foreach (EditorTool tool in EditorTools.All)
        {
            bool built = EditorTools.IsImplemented(tool);
            rows.Add(new Row
            {
                Label = EditorTools.Label(tool),
                // Unimplemented tools are SHOWN, marked, rather than hidden: the roster is the plan, and a
                // mapper who cannot see that the entity tool is coming will assume it does not exist.
                Detail = built ? (tool == current ? "current" : "") : Pending("E8"),
                Command = $"editor_tool {tool}",
                Enabled = built,
                Checked = built ? tool == current : null,
            });
        }
        return rows;
    }

    private List<Row> BuildModes()
    {
        var rows = new List<Row>();
        if (Controller is not { } c)
            return rows;

        foreach (ToolMode mode in EditorTools.ModesFor(c.Tool))
        {
            bool built = IsModeBuilt(c.Tool, mode);
            rows.Add(new Row
            {
                Label = EditorTools.Label(mode),
                Detail = built ? (mode == c.Mode ? "current" : "") : Pending("E8"),
                Command = EntityConsoleCommand(Controller?.Tool ?? EditorTool.None, mode)
                          ?? LightConsoleCommand(Controller?.Tool ?? EditorTool.None, mode)
                          ?? PaintConsoleCommand(Controller?.Tool ?? EditorTool.None, mode)
                          ?? ShaderConsoleCommand(Controller?.Tool ?? EditorTool.None, mode)
                          ?? WaypointConsoleCommand(Controller?.Tool ?? EditorTool.None, mode)
                          ?? PatchConsoleCommand(Controller?.Tool ?? EditorTool.None, mode)
                          ?? GeometryConsoleCommand(Controller?.Tool ?? EditorTool.None, mode)
                          ?? $"editor_mode {mode}",
                Enabled = built,
                Checked = built ? mode == c.Mode : null,
            });
        }
        return rows;
    }

    /// <summary>
    /// Entity create and the key inspector have no dialog yet, so their menu rows drive the console command
    /// that does exist. Naming that here rather than silently entering a mode with no UI behind it.
    /// </summary>
    private static string? EntityConsoleCommand(EditorTool tool, ToolMode mode)
    {
        if (tool != EditorTool.Entity)
            return null;
        return mode switch
        {
            ToolMode.Create => "editor_entity palette",
            ToolMode.Properties => "editor_entity keys",
            _ => null,
        };
    }

    /// <summary>
    /// The paint tool's Browse row opens the same texture grid the shader tool uses — picking a material is
    /// picking a material, whichever tool asked for it (backlog F3).
    /// </summary>
    private static string? PaintConsoleCommand(EditorTool tool, ToolMode mode)
        => tool == EditorTool.Paint && mode == ToolMode.Browse ? "editor_shader browse" : null;

    /// <summary>
    /// The light tool's rows (backlog T2). Create places one straight away rather than opening a palette —
    /// there is exactly one light class, so a list of one would be a dialog that only wastes a keypress.
    /// </summary>
    private static string? LightConsoleCommand(EditorTool tool, ToolMode mode)
    {
        if (tool != EditorTool.Light)
            return null;
        return mode switch
        {
            ToolMode.Create => "editor_light create",
            ToolMode.Properties => "editor_light dialog",
            _ => null,
        };
    }

    /// <summary>
    /// Modes whose gesture is a one-shot verb rather than a state: extrude, bevel, snap and brush-create all
    /// act immediately on what you are aiming at.
    /// </summary>
    private static string? GeometryConsoleCommand(EditorTool tool, ToolMode mode) => (tool, mode) switch
    {
        (EditorTool.Face, ToolMode.Extrude) => "editor_extrude",
        (EditorTool.Edge, ToolMode.Bevel) => "editor_bevel",
        (EditorTool.Vertex, ToolMode.SnapToGrid) => "editor_snap_grid",
        (EditorTool.Brush, ToolMode.Create) => "editor_brush_create",
        (EditorTool.Face, ToolMode.Create) => "editor_brush_create",
        (EditorTool.Shader, ToolMode.Flags) => "editor_shader flags",
        _ => null,
    };

    /// <summary>Patch create and modify open their dialogs.</summary>
    private static string? PatchConsoleCommand(EditorTool tool, ToolMode mode)
    {
        if (tool != EditorTool.Patch)
            return null;
        return mode switch
        {
            ToolMode.Create => "editor_patch palette",
            ToolMode.Modify => "editor_patch modify",
            _ => null,
        };
    }

    /// <summary>
    /// Waypoint modes are one-shot verbs against the server's live graph rather than states to sit in, so the
    /// rows fire their command directly.
    /// </summary>
    private static string? WaypointConsoleCommand(EditorTool tool, ToolMode mode)
    {
        if (tool != EditorTool.Waypoint)
            return null;
        return mode switch
        {
            ToolMode.Place => "editor_waypoint place",
            ToolMode.PlaceJump => "editor_waypoint place jump",
            ToolMode.PlaceCrouch => "editor_waypoint place crouch",
            ToolMode.PlaceSupport => "editor_waypoint place support",
            ToolMode.Remove => "editor_waypoint remove",
            ToolMode.Hardwire => "editor_waypoint hardwire",
            ToolMode.Unreachable => "editor_waypoint unreachable",
            ToolMode.RelinkAll => "editor_waypoint relinkall",
            ToolMode.Lock => "editor_waypoint lock",
            ToolMode.Symmetry => "editor_waypoint symmetry",
            _ => null,
        };
    }

    /// <summary>
    /// Shader modes drive the surface commands directly; there is no inspector dialog to enter yet, and a row
    /// that switched into a mode with no UI behind it would be the silent no-op the menu exists to avoid.
    /// </summary>
    private static string? ShaderConsoleCommand(EditorTool tool, ToolMode mode)
    {
        if (tool != EditorTool.Shader)
            return null;
        return mode switch
        {
            ToolMode.Browse => "editor_shader browse",
            ToolMode.PickShader => "editor_shader pick",
            ToolMode.ApplyShader => "editor_shader apply",
            ToolMode.FitProjection => "editor_shader fit",
            ToolMode.NaturalProjection => "editor_shader natural",
            ToolMode.AxialProjection => "editor_shader axial",
            ToolMode.ShiftUv => "editor_shader shift 0.25 0",
            ToolMode.ScaleUv => "editor_shader scale 2 2",
            ToolMode.RotateUv => "editor_shader rotate 15",
            _ => null,
        };
    }

    /// <summary>
    /// Which modes actually do something today. E7 shipped the rails plus move/rotate/scale; the rest land
    /// with their tools in E8. Marking them is the honest alternative to a row that silently no-ops.
    /// </summary>
    private static bool IsModeBuilt(EditorTool tool, ToolMode mode) => mode switch
    {
        ToolMode.Move or ToolMode.Rotate or ToolMode.Scale or ToolMode.Paste => true,
        ToolMode.Object or ToolMode.Face => tool == EditorTool.Select,
        ToolMode.TwoPoint or ToolMode.ThreePoint or ToolMode.ViewPlane => tool == EditorTool.Clip,
        // Entity create and the key inspector run from the console for now (editor_entity); the dialogs are
        // still to come, so the rows point at what exists rather than claiming a UI that does not.
        ToolMode.Create => tool is EditorTool.Entity or EditorTool.Light or EditorTool.Patch
            or EditorTool.Brush or EditorTool.Face,
        ToolMode.PaintWeight or ToolMode.EraseWeight or ToolMode.SmoothWeight => tool == EditorTool.Paint,
        ToolMode.Extrude => tool == EditorTool.Face,
        ToolMode.Bevel => tool == EditorTool.Edge,
        ToolMode.SnapToGrid => tool == EditorTool.Vertex,
        ToolMode.Flags => tool == EditorTool.Shader,
        ToolMode.Properties => tool is EditorTool.Entity or EditorTool.Light,
        ToolMode.Modify => tool == EditorTool.Patch,
        ToolMode.ControlPoints => tool == EditorTool.Patch,
        ToolMode.PickShader or ToolMode.ApplyShader or ToolMode.FitProjection
            or ToolMode.NaturalProjection or ToolMode.AxialProjection or ToolMode.ShiftUv
            or ToolMode.ScaleUv or ToolMode.RotateUv => tool == EditorTool.Shader,
        ToolMode.Browse => tool is EditorTool.Shader or EditorTool.Paint,
        ToolMode.Distance or ToolMode.Angle or ToolMode.Reachability => tool == EditorTool.Measure,
        ToolMode.Place or ToolMode.PlaceJump or ToolMode.PlaceCrouch or ToolMode.PlaceSupport
            or ToolMode.Remove or ToolMode.Hardwire or ToolMode.Unreachable
            or ToolMode.RelinkAll or ToolMode.Lock or ToolMode.Symmetry => tool == EditorTool.Waypoint,
        _ => false,
    };

    /// <summary>
    /// Constructive solid geometry (backlog F5, F6): one-shot verbs on the brush selection.
    ///
    /// Its own submenu rather than modes under the Brush tool, because a mode is a state you sit in and these
    /// fire once — the same reasoning that keeps copy and delete off the mode list. Every row states its
    /// requirement in the Detail when it is disabled, so a greyed row explains itself.
    /// </summary>
    private List<Row> BuildCsg()
    {
        int brushes = Controller?.Session?.SelectedBrushIds().Count ?? 0;
        float grid = MathF.Max(1f, Controller?.GridSnapSize ?? 16f);

        return new List<Row>
        {
            new()
            {
                Label = "Subtract",
                Detail = brushes > 0
                    ? "carves the first selected brush out of everything it overlaps; the cutter survives"
                    : "select the brush to cut OUT",
                Command = "editor_csg subtract",
                Enabled = brushes > 0,
            },
            new()
            {
                Label = "Hollow",
                Detail = brushes > 0 ? $"walls inside, {grid:0.##}u thick" : "select a brush",
                Command = "editor_csg hollow",
                Enabled = brushes > 0,
            },
            new()
            {
                Label = "Make room",
                Detail = brushes > 0
                    ? $"walls outside, {grid:0.##}u thick — the void is the volume you drew"
                    : "select a brush",
                Command = "editor_csg room",
                Enabled = brushes > 0,
            },
            new()
            {
                Label = "Merge",
                Detail = brushes >= 2
                    ? "fuses them into one, when the union is convex"
                    : "select at least two brushes",
                Command = "editor_csg merge",
                Enabled = brushes >= 2,
            },
        };
    }

    /// <summary>
    /// Narrowing the view (backlog F9) and grouping (backlog F8), which share a menu because they are the same
    /// question from a mapper's side: what am I working on right now.
    ///
    /// The "show everything" row stays enabled and states the count, because the whole hazard here is
    /// forgetting that something is hidden.
    /// </summary>
    private List<Row> BuildRegion()
    {
        VmapVisibility? vis = Controller?.Visibility;
        int selected = Controller?.Session?.Selection.Count ?? 0;
        int hidden = vis?.ExplicitHiddenCount ?? 0;

        return new List<Row>
        {
            new()
            {
                Label = "Hide selection",
                Detail = selected > 0 ? $"{selected} selected" : "nothing selected",
                Command = "editor_hide",
                Enabled = selected > 0,
            },
            new()
            {
                Label = "Isolate selection",
                Detail = selected > 0 ? "hide everything else" : "nothing selected",
                Command = "editor_hide unselected",
                Enabled = selected > 0,
            },
            new()
            {
                Label = "Show all hidden",
                Detail = hidden > 0 ? $"{hidden} hidden" : "nothing is hidden",
                Command = "editor_hide show",
                Enabled = hidden > 0,
                KeepOpen = true,
            },
            new()
            {
                Label = "Region to selection",
                Detail = selected > 0 ? "clip the view to its bounds" : "nothing selected",
                Command = "editor_region",
                Enabled = selected > 0,
            },
            new()
            {
                Label = "Region off",
                Detail = vis is { HasRegion: true } ? "show the whole map" : "no region set",
                Command = "editor_region off",
                Enabled = vis is { HasRegion: true },
                KeepOpen = true,
            },
            new()
            {
                Label = "Ungroup selection",
                Detail = selected > 0 ? "dissolve the groups it belongs to" : "nothing selected",
                Command = "editor_group off",
                Enabled = selected > 0,
            },
            new()
            {
                Label = "List groups",
                Detail = "to the console",
                Command = "editor_group list",
                KeepOpen = true,
            },
        };
    }

    private List<Row> BuildSelection()
    {
        VmapEditSession? s = Controller?.Session;
        int count = s?.Selection.Count ?? 0;
        bool any = count > 0;
        bool clip = Controller is { Clipboard.IsEmpty: false };

        return new List<Row>
        {
            new()
            {
                Label = "Deselect",
                Detail = any ? $"{count} selected" : "nothing selected",
                Command = "editor_select deselect",
                Enabled = any,
            },
            new()
            {
                Label = "Copy",
                Detail = any ? "" : "nothing selected",
                Command = "editor_select copy",
                Enabled = any,
            },
            new()
            {
                Label = "Paste",
                Detail = clip ? Controller!.Clipboard.Describe() : "clipboard empty",
                Command = "editor_select paste",
                Enabled = clip,
            },
            new()
            {
                Label = "Delete",
                Detail = any ? "" : "nothing selected",
                Command = "editor_select delete",
                Enabled = any,
            },
            new()
            {
                Label = "Invert selection",
                Detail = Pending("E8"),
                Command = "editor_select invert",
                Enabled = false,
            },
            new()
            {
                Label = "Select all of this shader",
                Detail = Pending("E8"),
                Command = "editor_select all_shader",
                Enabled = false,
            },
        };
    }

    private List<Row> BuildView()
    {
        var rows = new List<Row>
        {
            new()
            {
                Label = OrthoOpen ? "Switch to free-fly" : "Switch to ortho",
                Detail = BoundKey("editor_ortho"),
                Command = "editor_ortho",
            },
            new() { Label = "Grid", SubmenuTitle = "Grid", Submenu = BuildGrid },
            new() { Label = "Free-fly", SubmenuTitle = "Free-fly", Submenu = BuildFreeFly },
        };

        if (OrthoOpen)
            rows.Add(new Row
            {
                Label = "Ortho axis",
                Detail = BoundKey("editor_ortho_axis"),
                Command = "editor_ortho_axis",
                KeepOpen = true,
            });

        if (OrthoOpen)
            rows.Add(new Row
            {
                // The handoff (§11.5): pick a spot in the elevation view and stand in it.
                Label = "Fly camera to pointer",
                Command = "editor_camera here",
            });

        rows.Add(new Row { Label = "Overlays", SubmenuTitle = "Overlays", Submenu = BuildOverlays });
        rows.Add(new Row { Label = "Camera", SubmenuTitle = "Camera", Submenu = BuildCamera });

        rows.Add(new Row
        {
            Label = "Ortho layout...",
            // Honest about why: the multi-pane layout has nowhere to go until §11.5 grows more than one view.
            Detail = Pending("one view only"),
            Enabled = false,
        });

        return rows;
    }

    /// <summary>Save the selection as a prefab, or place one (§11.8).</summary>
    private List<Row> BuildPrefabs()
    {
        bool any = Controller?.Session is { Selection.Count: > 0 };
        return new List<Row>
        {
            new()
            {
                Label = "Save selection as prefab...",
                Detail = any ? "" : "nothing selected",
                Enabled = any,
                Command = "editor_prefab save prefab1",
            },
            new() { Label = "Place prefab...", Command = "editor_prefab place prefab1" },
            new() { Label = "List prefabs", Command = "editor_prefab list", KeepOpen = true },
        };
    }

    /// <summary>§11.5’s render overlays.</summary>
    private List<Row> BuildOverlays() => new()
    {
        new()
        {
            Label = "Vertices",
            Detail = "brush corners",
            Command = $"toggle {EditorController.CvarShowVertices}",
            Checked = GlobalF(EditorController.CvarShowVertices, 0f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Collision",
            // The one that earns its place: a volume you can walk into but never see is invisible by
            // definition until something draws it.
            Detail = "volumes that render nothing",
            Command = $"toggle {EditorController.CvarShowCollision}",
            Checked = GlobalF(EditorController.CvarShowCollision, 0f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Wireframe",
            Detail = BoundKey("editor_wire"),
            Command = "editor_wire",
            KeepOpen = true,
        },
        new()
        {
            Label = "Overlay range",
            Detail = $"{GlobalF(EditorController.CvarOverlayRange, 1024f):0}u",
            Command = $"toggle {EditorController.CvarOverlayRange} 512 1024 2048 4096",
            KeepOpen = true,
        },
    };

    /// <summary>Camera bookmarks and jumps (§11.8).</summary>
    private List<Row> BuildCamera() => new()
    {
        new() { Label = "Frame selection", Command = "editor_camera frame" },
        new() { Label = "Save to slot 1", Command = "editor_camera save 1", KeepOpen = true },
        new() { Label = "Save to slot 2", Command = "editor_camera save 2", KeepOpen = true },
        new() { Label = "Go to slot 1", Command = "editor_camera go 1" },
        new() { Label = "Go to slot 2", Command = "editor_camera go 2" },
    };

    private List<Row> BuildGrid()
    {
        bool drawn = GlobalF(EditorGrid.CvarEnabled, 1f) != 0f;
        float size = GlobalF(EditorGrid.CvarSize, 64f);
        bool align = GlobalF(EditorGrid.CvarSnapEnabled, 1f) != 0f;
        float alignSize = GlobalF(EditorGrid.CvarSnapSize, 16f);

        // Two grids, listed as two groups, because they are two decisions. What is drawn is a reference you
        // coarsen to see the room; what edits align to is a constraint you tighten to do precise work.
        return new List<Row>
        {
            new()
            {
                Label = "Show grid",
                Detail = BoundKey("editor_grid"),
                Command = "editor_grid",
                Checked = drawn,
                KeepOpen = true,
            },
            new()
            {
                Label = "Drawn size up",
                Detail = $"{size:0.###}u",
                Command = "editor_grid_size +",
                KeepOpen = true,
            },
            new()
            {
                Label = "Drawn size down",
                Detail = $"{size:0.###}u",
                Command = "editor_grid_size -",
                KeepOpen = true,
            },
            new()
            {
                Label = "Align to grid",
                Detail = BoundKey("editor_grid_snap"),
                Command = "editor_grid_snap",
                Checked = align,
                KeepOpen = true,
            },
            new()
            {
                Label = "Align size up",
                Detail = $"{alignSize:0.###}u  (hold G + wheel)",
                Command = "editor_grid_snap_size +",
                KeepOpen = true,
            },
            new()
            {
                Label = "Align size down",
                Detail = $"{alignSize:0.###}u",
                Command = "editor_grid_snap_size -",
                KeepOpen = true,
            },
            new()
            {
                // Snapping to nearby GEOMETRY — a different thing from aligning to the grid above, which is
                // why it keeps its own word in the menu and on the HUD.
                Label = "Geometry snap",
                Detail = $"vertices · edges · faces",
                Command = "editor_snap",
                Checked = GlobalF(EditorController.CvarSnapEnabled, 1f) != 0f,
                KeepOpen = true,
            },
            new()
            {
                Label = "Snap distance up",
                Detail = $"{GlobalF(EditorController.CvarSnapRadius, 16f):0.#}u",
                Command = "editor_snap_dist +",
                KeepOpen = true,
            },
            new()
            {
                Label = "Snap distance down",
                Detail = $"{GlobalF(EditorController.CvarSnapRadius, 16f):0.#}u",
                Command = "editor_snap_dist -",
                KeepOpen = true,
            },
        };
    }

    private List<Row> BuildFreeFly() => new()
    {
        new()
        {
            Label = "Camera speed up",
            Detail = $"x{FlySpeed:0.#}",
            Command = "editor_flyspeed +",
            KeepOpen = true,
        },
        new()
        {
            Label = "Camera speed down",
            Detail = $"x{FlySpeed:0.#}",
            Command = "editor_flyspeed -",
            KeepOpen = true,
        },
        new()
        {
            Label = "Show tool brushes",
            Detail = "hint/clip/caulk",
            Command = $"toggle {EditorController.CvarShowToolBrushes}",
            Checked = GlobalF(EditorController.CvarShowToolBrushes, 0f) != 0f,
            KeepOpen = true,
        },
    };

    private List<Row> BuildLighting() => new()
    {
        new()
        {
            Label = "Recompute lightmaps",
            Detail = BoundKey("editor_rebake"),
            Command = "editor_rebake",
        },
        new()
        {
            Label = "Lighting",
            Command = $"toggle {EditorLighting.CvarEnabled}",
            Checked = GlobalF(EditorLighting.CvarEnabled, 1f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Traced shadows in bake",
            Command = $"toggle {EditorLighting.CvarBakeShadows}",
            Checked = GlobalF(EditorLighting.CvarBakeShadows, 1f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Bounce light",
            Detail = $"{GlobalF(EditorLighting.CvarBakeBounces, 8f):0} bounces",
            Command = $"toggle {EditorLighting.CvarBakeBounce}",
            Checked = GlobalF(EditorLighting.CvarBakeBounce, 1f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Ambient occlusion",
            Command = $"toggle {EditorLighting.CvarSsao}",
            Checked = GlobalF(EditorLighting.CvarSsao, 1f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Compare original BSP",
            Detail = BoundKey("editor_show_bsp"),
            Command = "editor_show_bsp",
            Checked = GlobalF(EditorLighting.CvarShowBsp, 0f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Hide entities behind walls",
            Detail = "the overlay draws through geometry; this culls the boxes that are behind it",
            Command = $"toggle {EditorController.CvarEntityOcclusion}",
            Checked = GlobalF(EditorController.CvarEntityOcclusion, 1f) != 0f,
            KeepOpen = true,
        },
        new()
        {
            Label = "Texture browser thumbnails",
            Detail = "off falls back to the name list, which is what you want for reading whole paths",
            Command = $"toggle {EditorController.CvarThumbnails}",
            Checked = GlobalF(EditorController.CvarThumbnails, 1f) != 0f,
            KeepOpen = true,
        },
    };

    /// <summary>Mark a row that exists in the design but not yet in the build.</summary>
    private static string Pending(string why) => $"[{why}]";

    /// <summary>
    /// The key currently bound to a command, or empty when nothing is. Resolved live through the bind table
    /// (the same contract <see cref="EditorPanel"/> is built on) so rebinding updates the menu instead of
    /// leaving it confidently wrong.
    /// </summary>
    private static string BoundKey(string command)
    {
        string key = BindTable.CommandKey("", command);
        return string.IsNullOrEmpty(key) ? "" : $"[{key}]";
    }

    // =====================================================================================
    //  Input
    // =====================================================================================

    public override void _GuiInput(InputEvent @event)
    {
        if (!IsOpen)
            return;

        if (@event is InputEventMouseButton { Pressed: true } mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.Left:
                    int idx = RowAt(mb.Position);
                    if (idx >= 0)
                        Activate(idx);
                    AcceptEvent();
                    return;

                case MouseButton.Right:
                    // Right-click inside the menu goes back a level, which is the gesture that opened it and
                    // therefore the one already under the mapper's finger.
                    Ascend();
                    AcceptEvent();
                    return;

                case MouseButton.WheelDown when _rows.Count > MaxRows:
                    _page = Math.Min(_page + 1, (_rows.Count - 1) / MaxRows);
                    AcceptEvent();
                    return;

                case MouseButton.WheelUp when _rows.Count > MaxRows:
                    _page = Math.Max(0, _page - 1);
                    AcceptEvent();
                    return;
            }
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } k)
        {
            switch (k.Keycode)
            {
                case Key.Escape:
                    Close();
                    AcceptEvent();
                    return;
                case Key.Backspace:
                    Ascend();
                    AcceptEvent();
                    return;
            }

            int digit = DigitOf(k.Keycode);
            if (digit < 0)
                return;

            // 1..9 pick rows 1..9 of the page; 0 pages forward when there is more, else picks row 10.
            int visible = VisibleCount();
            if (digit == 0)
            {
                if (_rows.Count > MaxRows)
                    _page = (_page + 1) * MaxRows >= _rows.Count ? 0 : _page + 1;
                else if (visible >= MaxRows)
                    Activate(_page * MaxRows + MaxRows - 1);
                AcceptEvent();
                return;
            }

            if (digit <= visible)
                Activate(_page * MaxRows + digit - 1);
            AcceptEvent();
        }
    }

    private static int DigitOf(Key key) => key switch
    {
        >= Key.Key0 and <= Key.Key9 => (int)(key - Key.Key0),
        >= Key.Kp0 and <= Key.Kp9 => (int)(key - Key.Kp0),
        _ => -1,
    };

    private void UpdateHover()
    {
        int idx = RowAt(GetLocalMousePosition());
        if (idx != _hover)
        {
            _hover = idx;
            QueueRedraw();
        }
    }

    // =====================================================================================
    //  Layout + draw
    // =====================================================================================

    private int VisibleCount() => Math.Min(MaxRows, Math.Max(0, _rows.Count - _page * MaxRows));

    private int FontPx => (int)Mathf.Clamp(Size2.Y * 0.019f, 11f, 24f);

    private float RowH => FontPx + 8f;

    /// <summary>
    /// Menu width: the widest row plus room for the detail column, clamped so a long shader name cannot push
    /// the box off screen.
    /// </summary>
    private float MenuWidth()
    {
        float widest = 120f;
        int from = _page * MaxRows;
        int to = Math.Min(_rows.Count, from + MaxRows);
        for (int i = from; i < to; i++)
        {
            Row r = _rows[i];
            float w = MeasureText($"9. {r.Label}", FontPx) + 24f;
            if (r.Detail.Length > 0)
                w += MeasureText(r.Detail, FontPx) + 16f;
            widest = MathF.Max(widest, w);
        }
        return MathF.Min(widest, Size2.X * 0.42f);
    }

    /// <summary>
    /// Top-left of the box. Sits to the RIGHT of the anchor (the crosshair), and flips to the left or shifts
    /// up when that would run it off screen — a menu that opens partly outside the viewport is a menu whose
    /// bottom rows cannot be clicked.
    /// </summary>
    private Vector2 BoxOrigin(float w, float h)
    {
        float x = _anchor.X + 18f;
        float y = _anchor.Y - RowH;

        if (x + w > Size2.X - 8f)
            x = _anchor.X - 18f - w;
        x = Mathf.Clamp(x, 8f, MathF.Max(8f, Size2.X - w - 8f));
        y = Mathf.Clamp(y, 8f, MathF.Max(8f, Size2.Y - h - 8f));
        return new Vector2(x, y);
    }

    private int RowAt(Vector2 pos)
    {
        if (!IsOpen)
            return -1;

        int visible = VisibleCount();
        float w = MenuWidth();
        float h = HeaderH + visible * RowH + 6f;
        Vector2 origin = BoxOrigin(w, h);

        if (pos.X < origin.X || pos.X > origin.X + w)
            return -1;

        float top = origin.Y + HeaderH;
        if (pos.Y < top)
            return -1;

        int row = (int)((pos.Y - top) / RowH);
        if (row < 0 || row >= visible)
            return -1;
        return _page * MaxRows + row;
    }

    private float HeaderH => _stack.Count > 1 || _rows.Count > MaxRows ? RowH * 0.9f : 0f;

    private static readonly Color MenuBg = new(0.04f, 0.05f, 0.07f, 0.88f);
    private static readonly Color EdgeColor = new(0.45f, 0.85f, 1f, 0.55f);
    private static readonly Color LabelColor = new(0.88f, 0.92f, 0.96f);
    private static readonly Color SubmenuColor = new(0.55f, 0.82f, 1f);
    private static readonly Color DetailColor = new(0.55f, 0.6f, 0.66f);
    private static readonly Color DisabledColor = new(0.42f, 0.44f, 0.48f);
    private static readonly Color HoverColor = new(1f, 1f, 1f, 0.14f);
    private static readonly Color CheckColor = new(0.45f, 1f, 0.6f);
    private static readonly Color HeaderColor = new(1f, 0.85f, 0.45f);

    protected override void DrawPanel()
    {
        if (!IsOpen)
            return;

        int visible = VisibleCount();
        float w = MenuWidth();
        float rh = RowH;
        float headerH = HeaderH;
        float h = headerH + visible * rh + 6f;
        Vector2 origin = BoxOrigin(w, h);

        DrawRect(new Rect2(origin.X, origin.Y, w, h), MenuBg);
        // A one-pixel edge, drawn as four thin rects: the panel API has no stroke, and an unbordered dark box
        // over a dark level is invisible.
        DrawRect(new Rect2(origin.X, origin.Y, w, 1f), EdgeColor);
        DrawRect(new Rect2(origin.X, origin.Y + h - 1f, w, 1f), EdgeColor);
        DrawRect(new Rect2(origin.X, origin.Y, 1f, h), EdgeColor);
        DrawRect(new Rect2(origin.X + w - 1f, origin.Y, 1f, h), EdgeColor);

        if (headerH > 0f)
        {
            string title = _stack.Count > 1 ? _stack[^1].Title : "Editor";
            if (_rows.Count > MaxRows)
                title += $"   {_page + 1}/{(_rows.Count + MaxRows - 1) / MaxRows}";
            DrawText(new Vector2(origin.X + 10f, origin.Y + 3f), title, HeaderColor, FontPx);
        }

        for (int i = 0; i < visible; i++)
        {
            int index = _page * MaxRows + i;
            Row r = _rows[index];
            float y = origin.Y + headerH + i * rh;

            if (_hover == index && r.Enabled)
                DrawRect(new Rect2(origin.X + 1f, y, w - 2f, rh), HoverColor);

            Color color = !r.Enabled ? DisabledColor
                : r.IsBack ? HeaderColor
                : r.Submenu is not null ? SubmenuColor
                : LabelColor;

            string number = r.IsBack ? "<-" : $"{(i + 1) % 10}.";
            string label = r.IsBack ? "back" : r.Label;
            if (r.Submenu is not null)
                label += "  >";

            DrawText(new Vector2(origin.X + 10f, y + 3f), $"{number} {label}", color, FontPx);

            if (r.Checked is { } ticked)
            {
                float cx = origin.X + w - 18f;
                DrawText(new Vector2(cx, y + 3f), ticked ? "x" : "-",
                    ticked ? CheckColor : DisabledColor, FontPx);
            }

            if (r.Detail.Length > 0)
            {
                float dx = origin.X + w - 10f - MeasureText(r.Detail, FontPx) - (r.Checked is null ? 0f : 16f);
                DrawText(new Vector2(dx, y + 3f), r.Detail, r.Enabled ? DetailColor : DisabledColor, FontPx);
            }
        }
    }
}
