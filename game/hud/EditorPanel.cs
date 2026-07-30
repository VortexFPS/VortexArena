using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vmap;
using VortexArena.Game.Vmap;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Hud;

/// <summary>
/// The map editor's status readout (design doc §11.6): current state, world-grid setting, and — the part that
/// matters — the LIVE keybind for every toggle it lists.
///
/// Binds are resolved through <see cref="BindTable.CommandKey"/> (DP's <c>getcommandkey</c>) on every redraw
/// rather than being written into the strings. Hardcoding "[G]" would go stale the moment a mapper rebinds
/// anything, and a HUD that confidently displays the wrong key is worse than one that displays none: this way
/// rebinding <c>editor_grid</c> to any key immediately updates the panel, and an UNBOUND action renders as
/// <c>--</c> so it reads as "no key" instead of silently lying.
///
/// Discovered and instantiated automatically by <c>HudRegistry</c> like any other <see cref="HudPanel"/>, so
/// it inherits cvar-driven placement, the HUD editor's drag/resize, and show-mode gating for free. Per the
/// self-blank contract, it draws NOTHING until the host feeds it state — every non-editor session has this
/// panel present but silent.
/// </summary>
public partial class EditorPanel : HudPanel
{
    /// <summary>Cvar: master on/off for this panel (defaults on — it only draws in an editor session anyway).</summary>
    public const string CvarShow = "hud_panel_editor";

    /// <summary>
    /// True once the host has confirmed this is an editor session. Until then the panel is silent, so the
    /// auto-registered panel never draws over a normal match.
    /// </summary>
    public bool IsEditorSession { get; set; }

    /// <summary>True while the local player is free-flying (EDIT); false while playtesting.</summary>
    public bool IsEditing { get; set; } = true;

    /// <summary>Fly-speed multiplier from the spectator speed ladder, shown so the mapper knows why they are fast.</summary>
    public float FlySpeed { get; set; } = 1f;

    /// <summary>The live editor controller, for tool/selection/coordinate readouts. Null outside a session.</summary>
    public EditorController? Controller { get; set; }

    /// <summary>Point lights the live rig built; -1 when there is no rig.</summary>
    public int Lights { get; set; } = -1;

    /// <summary>True when the sun came from the map's own sky shader rather than the fallback.</summary>
    public bool HasMapSun { get; set; }

    /// <summary>True when fixture light is precomputed into the mesh rather than rendered live.</summary>
    public bool Baked { get; set; }

    /// <summary>True when the baked lighting was rebuilt without tracing, so its shadows are out of date.</summary>
    public bool ShadowsStale { get; set; }

    /// <summary>True while the background bake is running.</summary>
    public bool BakeRunning { get; set; }

    /// <summary>True while the session is showing the ORIGINAL compiled BSP for comparison.</summary>
    public bool ShowingBsp { get; set; }

    /// <summary>The running bake's phase: "direct", "bounce", "finalize".</summary>
    public string BakePhase { get; set; } = "";

    /// <summary>Cell meshes still waiting for the finished bake to be applied.</summary>
    public int ApplyRemaining { get; set; }

    /// <summary>Cell meshes the current apply started with.</summary>
    public int ApplyTotal { get; set; }

    /// <summary>Fraction of the running bake that is done, 0..1.</summary>
    public float BakeProgress { get; set; }

    /// <summary>The orthographic view, for its state line. Null outside a session.</summary>
    public EditorOrthoView? Ortho { get; set; }

    public override bool IsDynamic => true;

    public static void RegisterDefaults(CvarService c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Register(CvarShow, "1", CvarFlags.Save);
    }

    public override void _Process(double delta)
    {
        bool show = IsEditorSession && ShowMode() != 0;
        if (show != Visible)
            Visible = show;
        if (show)
            QueueRedraw();
    }

    /// <summary>
    /// The panel's on/off mode. Read through the base's <see cref="HudPanel.ShowModeCvar"/> rather than a
    /// direct cvar lookup: that is the accessor which knows which store the HUD's per-panel cvars live in.
    /// </summary>
    private int ShowMode() => ShowModeCvar();

    /// <summary>
    /// The work-in-progress banner's colour: a saturated warning yellow, deliberately brighter than the
    /// amber this panel uses for a held mode. A caution about the tool itself should not read as one more
    /// piece of editor state.
    /// </summary>
    private static readonly Color WipColor = new(1f, 0.85f, 0.15f);

    protected override void DrawPanel()
    {
        if (!IsEditorSession || ShowModeCvar() == 0)
            return;

        int size = (int)Mathf.Clamp(Size2.Y * 0.017f, 11f, 24f);
        float lineH = size + 5f;
        // TOP RIGHT, right-aligned: this panel takes over the corner the scoreboard and the spectator
        // prompt occupy in a match, both of which an editing session hides. Anchored to the right edge so
        // the readout stays put as lines change length instead of shuffling sideways.
        float right = Size2.X - 12f;
        float y = 12f;

        bool gridOn = CvarFloat(EditorGrid.CvarEnabled) != 0f;
        float gridSize = CvarFloat(EditorGrid.CvarSize, 64f);
        bool alignOn = CvarFloat(EditorGrid.CvarSnapEnabled, 1f) != 0f;
        float alignSize = CvarFloat(EditorGrid.CvarSnapSize, 16f);

        var lines = new List<(string Text, Color Color)>
        {
            // First line, and it stays first. The editor is reachable like any other gametype, so nothing
            // else tells a mapper that what they are about to use is unfinished — and finding that out by
            // losing an evening's work is the worst way to learn it. Drawn in PLAYTEST too: the state you
            // are in changes, the maturity of the tool does not.
            ("WORK IN PROGRESS — the map editor is under development", WipColor),

            IsEditing
                ? ($"EDIT   {Key(BindPlaytest)} playtest", new Color(0.45f, 0.85f, 1f))
                : ($"PLAYTEST   {Key(BindPlaytest)} edit", new Color(1f, 0.75f, 0.3f)),
        };

        if (IsEditing)
        {
            var dim = new Color(0.6f, 0.65f, 0.7f);
            var bright = new Color(0.8f, 0.9f, 0.95f);

            if (Controller is { } c)
            {
                lines.Add(($"{c.ActionLine}", new Color(1f, 0.9f, 0.55f)));
                lines.Add(($"Showing: {c.GametypeFilterLabel}   (editor_gametype <name|all>)", dim));

                // A narrowed view has to SAY it is narrowed. A mapper who forgets a region is set concludes
                // that half their map got deleted, and there is nothing anywhere to tell them otherwise —
                // which makes this the cheapest line in the panel and one of the most valuable.
                int hidden = c.Visibility.ExplicitHiddenCount;
                if (c.Visibility.HasRegion || hidden > 0 || c.Visibility.HiddenGroups.Count > 0)
                {
                    var parts = new List<string>(3);
                    if (c.Visibility.HasRegion)
                        parts.Add("REGION on (editor_region off)");
                    if (hidden > 0)
                        parts.Add($"{hidden} hidden (editor_hide show)");
                    if (c.Visibility.HiddenGroups.Count > 0)
                        parts.Add($"{c.Visibility.HiddenGroups.Count} group(s) hidden");
                    lines.Add((string.Join("  ·  ", parts), new Color(1f, 0.75f, 0.3f)));
                }

                // The comparison view is a MODE, and a mode you can be in silently is a mode you will
                // forget you are in — edits do not draw while the BSP is up, which would read as a broken
                // editor rather than as a held toggle.
                if (ShowingBsp)
                    lines.Add(($"VIEWING ORIGINAL BSP — {EKey(CmdBspCompare)} back to editor world",
                        new Color(1f, 0.75f, 0.3f)));
                else
                    lines.Add(($"{EKey(CmdBspCompare)} compare original BSP", dim));

                // Lighting state, because it changes what every surface looks like and is otherwise a cvar
                // nobody would guess at. Reports what the rig actually built, not just the toggle: a map
                // compiled without -keeplights yields no point lights, and the mapper should be told that
                // rather than left wondering why "lit" looks flat.
                bool lit = VortexArena.Game.Vmap.EditorLighting.Enabled(VortexArena.Game.Menu.MenuState.Cvars);
                string lighting = lit
                    ? $"Light: ON  {(Lights >= 0 ? $"{Lights} lights" : "")}{(Baked ? " BAKED" : "")}{(HasMapSun ? " + map sun" : " + default sun")}"
                    : "Light: OFF (fullbright)";
                lines.Add(($"{lighting}   (cl_editor_lighting)", lit ? bright : dim));
                // Shadow tracing costs seconds, so edits skip it and say so rather than quietly showing
                // lighting that no longer matches the geometry.
                if (lit && Baked && BakeRunning)
                {
                    // A bar, not just a number: a bake runs for minutes and the mapper needs to see it move
                    // to tell "working" from "hung" — which is exactly the distinction that was missing.
                    // The phase label carries the rest of the honesty: 100% of one pass is not done.
                    int filled = (int)(BakeProgress * 24f);
                    lines.Add(($"  BAKING [{new string('#', filled)}{new string('.', 24 - filled)}] "
                        + $"{BakeProgress * 100f:F0}% {BakePhase}", new Color(0.5f, 0.85f, 1f)));
                }
                else if (lit && Baked && ApplyRemaining > 0 && ApplyTotal > 0)
                {
                    // The finished bake streams onto the world a few milliseconds a frame; SAY so, rather
                    // than leaving a silent gap between "BAKING 100%" and the lighting visibly changing.
                    float f = 1f - (float)ApplyRemaining / ApplyTotal;
                    int filled = (int)(f * 24f);
                    lines.Add(($"  APPLYING [{new string('#', filled)}{new string('.', 24 - filled)}] "
                        + $"{f * 100f:F0}%", new Color(0.5f, 0.85f, 1f)));
                }
                else if (lit && Baked && ShadowsStale)
                    lines.Add(($"  LIGHTING STALE — {Key(BindRebake)} to rebake", new Color(1f, 0.75f, 0.3f)));

                // Three separate things, so three separate words. "Grid" is what is DRAWN, "Align" is what
                // edits quantize to, and "Snap" has always meant snapping to nearby GEOMETRY — reusing that
                // label for the alignment grid would have made the one readout that already existed wrong.
                lines.Add((
                    $"Grid: {(gridOn ? "ON" : "OFF")} {Fmt(gridSize)}u  {EKey(CmdGrid)}   " +
                    $"Align: {(alignOn ? "ON" : "OFF")} {Fmt(alignSize)}u  hold {EKey(CmdGrid)}+wheel   " +
                    $"Snap: {(c.SnapEnabled ? "ON" : "OFF")} {Fmt(c.SnapRadiusDisplay)}u",
                    alignOn ? bright : dim));

                // Selection + live coordinates. During a drag the delta is what the mapper is actually steering,
                // so it takes the line; otherwise the selection centre anchors where they are working.
                if (c.Session is { } session && session.Selection.Count > 0)
                {
                    VmapSelection first = session.Selection[0];
                    string what = session.Selection.Count > 1
                        ? $"{session.Selection.Count} {first.Kind}s"
                        : $"{first.Kind} of brush #{first.BrushId}";
                    lines.Add(($"Sel: {what}", new Color(0.45f, 0.85f, 1f)));

                    if (c.Document is { } doc
                        && VmapEdit.TryGetSelectionCenter(doc, session.SelectedBrushIds(), out NVec3 center))
                        lines.Add(($"ctr  {Coord(center)}", dim));
                }

                // What the crosshair is over: the shader/texture, its flags, and whether it is real architecture
                // or compiler scaffolding. This is the information a mapper actually needs before touching a
                // face, and it is otherwise invisible in-game.
                VmapSelection info = c.IsDragging ? c.DragSelection : c.Hover.Selection;
                if (!info.IsEmpty && c.Document?.FindBrush(info.BrushId) is { } infoBrush)
                {
                    if (info.FaceIndex >= 0 && info.FaceIndex < infoBrush.Faces.Count)
                    {
                        VmapFace face = infoBrush.Faces[info.FaceIndex];
                        lines.Add(($"shader {Shorten(face.Material)}", new Color(0.85f, 0.8f, 0.6f)));

                        string flags = DescribeFlags(face.SurfaceFlags, face.ContentFlags);
                        if (flags.Length > 0)
                            lines.Add(($"  {flags}", dim));
                    }

                    string kind = infoBrush.IsToolBrush ? "tool" : infoBrush.IsDetail ? "detail" : "structural";
                    lines.Add(($"brush #{infoBrush.Id}  {infoBrush.Faces.Count} faces  {kind}", dim));
                }

                if (c.IsDragging && c.Mode == ToolMode.Rotate)
                {
                    lines.Add(($"rotate {c.DragAngle:0.#}°  axis {AxisName(c.DragAxis)}", new Color(0.4f, 1f, 0.55f)));
                }
                else if (c.IsDragging)
                {
                    NVec3 d = c.DragDelta;
                    string axis = c.DragAxis != NVec3.Zero ? $"  axis {AxisName(c.DragAxis)}" : "";
                    string snapNote = c.DragSnap.Snapped ? $"  snap→{c.DragSnap.TargetKind}" : "";
                    lines.Add(($"drag {Coord(d)}  |{d.Length():0.#}|{axis}{snapNote}", new Color(0.4f, 1f, 0.55f)));
                }
                else if (c.Hover.Hit)
                {
                    lines.Add(($"cur  {Coord(c.Hover.Point)}", dim));
                }

                // Unsaved work gets its own line, in warning colour. It is the one piece of editor state whose
                // cost is unrecoverable, so it does not share a line with anything.
                if (c.Session is { IsDirty: true })
                    lines.Add(("UNSAVED CHANGES   (editor_save)", new Color(1f, 0.75f, 0.3f)));

                if (c.Session is { CanUndo: true } undoable)
                    lines.Add(($"[Ctrl+Z] undo: {undoable.UndoLabel}", dim));
            }
            else
            {
                lines.Add((
                    $"Grid: {(gridOn ? "ON" : "OFF")}  {Fmt(gridSize)}u   {EKey(CmdGrid)}",
                    gridOn ? bright : dim));
            }

            if (Ortho is { IsOpen: true } ortho)
            {
                lines.Add(($"ORTHO {ortho.AxisLabel}   {EKey(CmdOrtho)} close · {Key(BindOrthoAxis)} axis",
                    new Color(1f, 0.85f, 0.4f)));
                // Panning is not discoverable: the view owns the cursor, so the usual fly keys pan instead.
                lines.Add(($"  WASD pan · wheel zoom · Alt+wheel floor · edges {ortho.WireAlpha * 100f:0}% {Key(BindWire)}",
                    new Color(1f, 0.85f, 0.4f)));
            }
            else
                lines.Add(($"{EKey(CmdOrtho)} ortho view", dim));

            if (FlySpeed > 0f)
                lines.Add(($"Fly x{FlySpeed:0.#}", dim));

            AppendTips(lines);
        }

        float widest = 0f;
        foreach ((string text, _) in lines)
            widest = MathF.Max(widest, MeasureText(text, size));

        DrawRect(new Rect2(right - widest - 6f, y - 4f, widest + 12f, lines.Count * lineH + 8f),
            new Color(0f, 0f, 0f, 0.45f));

        for (int i = 0; i < lines.Count; i++)
        {
            (string text, Color color) = lines[i];
            DrawText(new Vector2(right - MeasureText(text, size), y + i * lineH), text, color, size);
        }
    }

    // (E7) The editor owns keys 0-9 outright while free-flying, through its OWN bind layer (EditorBinds), so
    // the player's number-row binds survive untouched and come back the moment they drop into PLAYTEST. The
    // panel resolves those keys from that layer, and everything else from the game's shared bind table — both
    // live, so rebinding either updates the readout instead of leaving it confidently wrong.
    //
    // The EDIT/PLAYTEST toggle rides F9 (Base's minigame-HUD bind) rather than a digit: it has to work from
    // PLAYTEST too, where the editor's layer is not active.
    private const string BindPlaytest = "cl_cmd hud minigame";
    private const string CmdGrid = "editor_grid";
    private const string CmdMode = "editor_mode";
    private const string CmdOrtho = "editor_ortho";
    private const string CmdBspCompare = "editor_show_bsp";
    private const string BindWire = "weapon_group_8";
    private const string BindRebake = "weapon_group_9";
    private const string BindOrthoAxis = "weapon_group_5";

    /// <summary>Trim a long shader path to its last two components, which is the part that identifies it.</summary>
    private static string Shorten(string material)
    {
        if (string.IsNullOrEmpty(material))
            return "(none)";
        string[] parts = material.Split('/');
        return parts.Length <= 2 ? material : string.Join('/', parts[^2], parts[^1]);
    }

    /// <summary>Name the dominant axis of a drag constraint, so the readout says WHICH way it is moving.</summary>
    private static string AxisName(NVec3 a)
    {
        float ax = MathF.Abs(a.X), ay = MathF.Abs(a.Y), az = MathF.Abs(a.Z);
        if (ax >= ay && ax >= az) return a.X >= 0 ? "+X" : "-X";
        if (ay >= az) return a.Y >= 0 ? "+Y" : "-Y";
        return a.Z >= 0 ? "+Z" : "-Z";
    }

    /// <summary>Human-readable Q3 surface/content flags — the ones a mapper cares about when picking a face.</summary>
    private static string DescribeFlags(int surface, int contents)
    {
        var parts = new List<string>(4);
        if ((surface & 0x0080) != 0) parts.Add("nodraw");
        if ((surface & 0x0004) != 0) parts.Add("sky");
        if ((surface & 0x0002) != 0) parts.Add("slick");
        if ((surface & 0x0008) != 0) parts.Add("ladder");
        if ((surface & 0x4000) != 0) parts.Add("nonsolid");
        if ((contents & 0x08000000) != 0) parts.Add("detail");
        if ((contents & 0x00010000) != 0) parts.Add("playerclip");
        if ((contents & 0x40000000) != 0) parts.Add("trigger");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Format a world position the way a mapper reads one: Quake units, integral, in the map's own coordinate
    /// system (COORDINATE_CONVENTIONS.md — HUD coordinates are Quake, never the renderer's Y-up space).
    /// </summary>
    private static string Coord(NVec3 v) => $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})";

    /// <summary>
    /// The key currently bound to <paramref name="command"/>, in brackets, or <c>--</c> when nothing is bound.
    /// Matching is exact on the bound command string, so the text passed here must be exactly what the mapper
    /// would <c>bind</c>.
    /// </summary>
    /// <summary>
    /// The tips block (design doc §11.9): what the modifiers would do RIGHT NOW.
    ///
    /// Contextual rather than a static legend, and that is the whole value. "Hold Shift to multi-select" is
    /// noise when nothing is selectable and exactly the thing you needed to know when the crosshair is on a
    /// brush. A fixed legend gets read once and then becomes wallpaper; a list that changes gets read.
    ///
    /// No visible header — the tips just sit at the bottom of the panel in their own colour, because a header
    /// would cost a line to say something the content already says.
    /// </summary>
    private void AppendTips(List<(string Text, Color Color)> lines)
    {
        if (Controller is not { } c)
            return;

        var tip = new Color(0.55f, 0.72f, 0.62f);
        var live = new Color(0.5f, 1f, 0.7f);

        // Held modifiers report their CURRENT state, so the line doubles as an indicator: while Ctrl is down
        // it says what is happening, not what would happen.
        if (c.SnapInverted)
            lines.Add((c.EffectiveGridSnap > 0f
                ? $"CTRL — snapping to {Fmt(c.EffectiveGridSnap)}u grid"
                : "CTRL — grid snap off", live));
        else
            lines.Add((c.EffectiveGridSnap > 0f
                ? "hold Ctrl to drop off-grid"
                : "hold Ctrl to snap to grid", tip));

        if (c.IsDragging)
        {
            lines.Add(("Esc or right-click cancels the drag", tip));
            return;
        }

        // Measure's whole output is a line of text, so it belongs here rather than anywhere else.
        if (c.Tool == EditorTool.Measure)
        {
            lines.Add((c.MeasureReadout(), live));
            return;
        }

        // The clip tool is the one place where clicking is not the commit — Enter is — so it needs saying.
        if (c.Tool == EditorTool.Clip)
        {
            if (c.Session is not { Selection.Count: > 0 })
                lines.Add(("click the brushes to cut first", tip));
            else if (c.ClipPoints.Count < c.ClipPointsNeeded)
                lines.Add(($"click {c.ClipPointsNeeded - c.ClipPoints.Count} more point(s) "
                    + $"· keeps {c.ClipKeep.ToString().ToLowerInvariant()}", tip));
            else
                lines.Add(($"[Enter] cut · keeps {c.ClipKeep.ToString().ToLowerInvariant()} · Esc clears", live));
            return;
        }

        if (c.Mode == ToolMode.Paste)
        {
            lines.Add((c.Clipboard.IsEmpty
                ? "nothing copied yet"
                : "click to place · Esc to stop pasting", live));
            return;
        }

        if (c.HoverHandle is { } h)
        {
            lines.Add(($"click to {HandleVerb(h)}", live));
            return;
        }

        // The two-phase model is the thing most likely to read as a broken editor: a mapper who drags the
        // object body and sees nothing move needs to be told that the handle is the target, not the brush.
        if (c.Session is { Selection.Count: > 0 })
            lines.Add(("grab an axis handle to transform", tip));
        else if (c.Hover.Hit)
            lines.Add(("click to select · hold Shift to multi-select", tip));

        lines.Add(("right-click for the editor menu", tip));
    }

    private static string HandleVerb(EditorHandle h) => h.Kind switch
    {
        HandleKind.MoveAxis => $"move along {Axis(h.Axis)}",
        HandleKind.MovePlane => $"move in {Axis(h.Axis)}{Axis(h.Axis2)}",
        HandleKind.RotateRing => $"rotate about {Axis(h.Axis)}",
        HandleKind.ScaleUniform => "scale uniformly",
        _ => $"scale along {Axis(h.Axis)}",
    };

    private static string Axis(NVec3 a)
    {
        float ax = MathF.Abs(a.X), ay = MathF.Abs(a.Y), az = MathF.Abs(a.Z);
        if (ax >= ay && ax >= az) return "X";
        return ay >= az ? "Y" : "Z";
    }

    private static string EKey(string command) => VortexArena.Game.Vmap.EditorBinds.KeyLabel(command);

    private static string Key(string command)
    {
        string key = BindTable.CommandKey("", command);
        return string.IsNullOrEmpty(key) ? "[--]" : $"[{key}]";
    }

    /// <summary>
    /// Read a client cvar through the base's global accessor. Going direct to <c>Api.Cvars</c> reads a
    /// different store than the one the client's own cvars live in, which silently reports every editor
    /// setting as its fallback — the panel then claims "Grid: OFF" while the grid is plainly on screen.
    /// </summary>
    private float CvarFloat(string name, float fallback = 0f) => GlobalF(name, fallback);

    private static string Fmt(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
