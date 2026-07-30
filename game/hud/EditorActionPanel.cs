using Godot;
using VortexArena.Formats.Vmap;
using VortexArena.Game.Vmap;

namespace VortexArena.Game.Hud;

/// <summary>
/// The editor's action readout (design doc §11.9): one line under the crosshair saying what you are about to
/// do and to what — <c>Brush &gt; Move: Brush #412</c>, <c>Brush &gt; Paste: copy of Brush #412</c>.
///
/// Separate from <see cref="EditorPanel"/> on purpose. That panel is a corner status board you consult; this
/// is the one fact you need in peripheral vision while your eyes are on the geometry, and a corner of the
/// screen is the wrong place for it. Keeping it to a single line is the whole design: the moment it grows a
/// second row it stops being glanceable and becomes another thing to read.
///
/// Drawn relative to the viewport centre rather than the panel rect, like the other crosshair-anchored port
/// extras, but it is still a real <see cref="HudPanel"/> so it inherits show-mode gating and can be turned off.
/// </summary>
public partial class EditorActionPanel : HudPanel
{
    /// <summary>True once the host confirms an editor session; silent until then, per the self-blank contract.</summary>
    public bool IsEditorSession { get; set; }

    /// <summary>True while free-flying in EDIT. In PLAYTEST the line hides: there is no pending edit to name.</summary>
    public bool IsEditing { get; set; } = true;

    /// <summary>The live controller. Null outside a session.</summary>
    public EditorController? Controller { get; set; }

    public override bool IsDynamic => true;

    /// <summary>Gap below the crosshair, as a fraction of viewport height.</summary>
    private const float DropFraction = 0.045f;

    private static readonly Color ActionColor = new(1f, 0.9f, 0.55f);
    private static readonly Color GrabColor = new(0.4f, 1f, 0.55f);
    private static readonly Color BackColor = new(0f, 0f, 0f, 0.42f);

    public override void _Process(double delta)
    {
        bool show = IsEditorSession && IsEditing && Controller is not null && ShowModeCvar() != 0;
        if (show != Visible)
            Visible = show;
        if (show)
            QueueRedraw();
    }

    protected override void DrawPanel()
    {
        if (!IsEditorSession || !IsEditing || ShowModeCvar() == 0 || Controller is not { } c)
            return;

        // No tool means no pending action, and the whole point of EditorTool.None is an unobstructed view of
        // the lighting — a caption under the crosshair would be exactly the obstruction it exists to remove.
        if (c.Tool == EditorTool.None)
            return;

        string line = c.ActionLine;
        if (string.IsNullOrEmpty(line))
            return;

        // While a handle is grabbed the line reports the live number instead of the subject: mid-drag, "how
        // far have I moved this" is the only question being asked.
        Color color = ActionColor;
        if (c.IsDragging)
        {
            color = GrabColor;
            line = c.Mode switch
            {
                ToolMode.Rotate => $"{EditorTools.Label(c.Tool)} > Rotate: {c.DragAngle:0.#}° about {AxisName(c.DragAxis)}",
                ToolMode.Scale => $"{EditorTools.Label(c.Tool)} > Scale: {ScaleText(c.DragScale)}",
                _ => $"{EditorTools.Label(c.Tool)} > Move: {Coord(c.DragDelta)}  |{c.DragDelta.Length():0.#}|",
            };
        }

        int size = (int)Mathf.Clamp(Size2.Y * 0.019f, 11f, 24f);
        float w = MeasureText(line, size);
        float x = Size2.X * 0.5f - w * 0.5f;
        float y = Size2.Y * (0.5f + DropFraction);

        DrawRect(new Rect2(x - 8f, y - 3f, w + 16f, size + 8f), BackColor);
        DrawText(new Vector2(x, y), line, color, size);
    }

    private static string ScaleText(System.Numerics.Vector3 s)
        => MathF.Abs(s.X - s.Y) < 1e-4f && MathF.Abs(s.Y - s.Z) < 1e-4f
            ? $"{s.X:0.###}x"
            : $"({s.X:0.###}, {s.Y:0.###}, {s.Z:0.###})";

    private static string Coord(System.Numerics.Vector3 v) => $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})";

    /// <summary>Name the dominant axis of a constraint. Quake axes, per COORDINATE_CONVENTIONS.md.</summary>
    private static string AxisName(System.Numerics.Vector3 a)
    {
        if (a == System.Numerics.Vector3.Zero)
            return "view";
        float ax = MathF.Abs(a.X), ay = MathF.Abs(a.Y), az = MathF.Abs(a.Z);
        if (ax >= ay && ax >= az) return "X";
        return ay >= az ? "Y" : "Z";
    }
}
