using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Vmap;

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

    private static int ShowMode() => Api.Services is null ? 1 : (int)Api.Cvars.GetFloat(CvarShow);

    protected override void DrawPanel()
    {
        if (!IsEditorSession || ShowMode() == 0)
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
            lines.Add((
                $"Grid: {(gridOn ? "ON" : "OFF")}  {Fmt(gridSize)}u   {Key(BindGrid)} toggle · {Key(BindGridUp)}/{Key(BindGridDown)} size",
                gridOn ? new Color(0.8f, 0.9f, 0.95f) : new Color(0.55f, 0.6f, 0.65f)));

            if (FlySpeed > 0f)
                lines.Add(($"Fly x{FlySpeed:0.#}", new Color(0.6f, 0.65f, 0.7f)));
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
    private const string BindPlaytest = "weapon_group_2";
    private const string BindGrid = "weapon_group_1";
    private const string BindGridUp = "weapnext";
    private const string BindGridDown = "weapprev";

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

    private static float CvarFloat(string name, float fallback = 0f)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    private static string Fmt(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
