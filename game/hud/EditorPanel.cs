using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Vmap;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

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

    protected override void DrawPanel()
    {
        if (!IsEditorSession || ShowModeCvar() == 0)
            return;

        int size = (int)Mathf.Clamp(Size2.Y * 0.017f, 11f, 24f);
        float lineH = size + 5f;
        float x = 12f;
        float y = Size2.Y * 0.30f;

        bool gridOn = CvarFloat(EditorGrid.CvarEnabled) != 0f;
        float gridSize = CvarFloat(EditorGrid.CvarSize, 64f);

        var lines = new List<(string Text, Color Color)>
        {
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
                lines.Add(($"Tool: {c.Tool}  {Key(BindTool)}   Manip: {c.Manipulator}  {Key(BindManip)}", bright));
                lines.Add(($"Showing: {c.GametypeFilterLabel}   (editor_gametype <name|all>)", dim));

                // Lighting state, because it changes what every surface looks like and is otherwise a cvar
                // nobody would guess at. Reports what the rig actually built, not just the toggle: a map
                // compiled without -keeplights yields no point lights, and the mapper should be told that
                // rather than left wondering why "lit" looks flat.
                bool lit = XonoticGodot.Game.Vmap.EditorLighting.Enabled(XonoticGodot.Game.Menu.MenuState.Cvars);
                string lighting = lit
                    ? $"Light: ON  {(Lights >= 0 ? $"{Lights} lights" : "")}{(HasMapSun ? " + map sun" : " + default sun")}"
                    : "Light: OFF (fullbright)";
                lines.Add(($"{lighting}   (cl_editor_lighting)", lit ? bright : dim));

                lines.Add((
                    $"Grid: {(gridOn ? "ON" : "OFF")} {Fmt(gridSize)}u  {Key(BindGrid)} · {Key(BindGridUp)}/{Key(BindGridDown)}   " +
                    $"Snap: {(c.SnapEnabled ? "ON" : "OFF")} {Fmt(c.SnapRadiusDisplay)}u",
                    gridOn ? bright : dim));

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

                if (c.IsDragging && c.Manipulator == ManipulatorMode.Rotate)
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

                if (c.Session is { CanUndo: true } undoable)
                    lines.Add(($"[Ctrl+Z] undo: {undoable.UndoLabel}", dim));
            }
            else
            {
                lines.Add((
                    $"Grid: {(gridOn ? "ON" : "OFF")}  {Fmt(gridSize)}u   {Key(BindGrid)} · {Key(BindGridUp)}/{Key(BindGridDown)}",
                    gridOn ? bright : dim));
            }

            if (Ortho is { IsOpen: true } ortho)
            {
                lines.Add(($"ORTHO {ortho.AxisLabel}   {Key(BindOrtho)} close · {Key(BindOrthoAxis)} axis",
                    new Color(1f, 0.85f, 0.4f)));
                // Panning is not discoverable: the view owns the cursor, so the usual fly keys pan instead.
                lines.Add(($"  WASD pan · wheel zoom · Ctrl+wheel floor · edges {ortho.WireAlpha * 100f:0}% {Key(BindWire)}",
                    new Color(1f, 0.85f, 0.4f)));
            }
            else
                lines.Add(($"{Key(BindOrtho)} ortho view", dim));

            if (FlySpeed > 0f)
                lines.Add(($"Fly x{FlySpeed:0.#}", dim));
        }

        float widest = 0f;
        foreach ((string text, _) in lines)
            widest = MathF.Max(widest, MeasureText(text, size));

        DrawRect(new Rect2(x - 6f, y - 4f, widest + 12f, lines.Count * lineH + 8f), new Color(0f, 0f, 0f, 0.45f));

        for (int i = 0; i < lines.Count; i++)
            DrawText(new Vector2(x, y + i * lineH), lines[i].Text, lines[i].Color, size);
    }

    // The editor reuses the WEAPON binds while free-flying (NetGame.TryRunEditorBind): you cannot shoot and
    // edit at the same time, so the weapon keys are free, and reusing them means the editor inherits whatever
    // keys the player already has in muscle memory instead of needing its own bind set. The HUD therefore
    // reverse-looks-up the weapon command, which is what is actually bound to a key.
    // The EDIT/PLAYTEST toggle rides F9 (Base's minigame-HUD bind), NOT a weapon key: in PLAYTEST the weapon
    // binds return to selecting weapons, so a weapon-key toggle would strand the mapper in playtest.
    private const string BindPlaytest = "cl_cmd hud minigame";
    private const string BindGrid = "weapon_group_1";
    private const string BindTool = "weapon_group_3";
    private const string BindManip = "weapon_group_7";
    private const string BindWire = "weapon_group_8";
    private const string BindOrtho = "weapon_group_4";
    private const string BindOrthoAxis = "weapon_group_5";
    private const string BindGridUp = "weapnext";
    private const string BindGridDown = "weapprev";

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
