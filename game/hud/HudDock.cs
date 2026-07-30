using Godot;
using VortexArena.Game.Menu;   // MenuState.Cvars — the shared menu/console store

namespace VortexArena.Game.Hud;

/// <summary>
/// The HUD dock background — port of the dock-draw block in Base/.../qcsrc/client/hud/hud.qc
/// (<c>HUD_Main</c>, the <c>if (autocvar_hud_dock != "" &amp;&amp; autocvar_hud_dock != "0")</c> branch,
/// hud.qc:708-748). When <c>hud_dock</c> names a dock art (anything but "" / "0") the engine draws a
/// full-screen background image (<c>gfx/hud/&lt;skin&gt;/&lt;hud_dock&gt;</c>, falling back to
/// <c>dock_medium</c> then the default skin) tinted by <c>hud_dock_color</c> (or the team color when
/// <c>hud_dock_color_team</c> and teamplay), at <c>hud_dock_alpha * hud_fade_alpha</c>, with NO aspect
/// forcing — it fills the whole viewport.
///
/// This is a standalone <see cref="Control"/> added FIRST (lowest z-order) in <see cref="Hud"/> so it
/// composites behind every panel, exactly as the QC dock is drawn before the panel walk. It self-blanks
/// when the dock is off (the common default <c>hud_dock 0</c>), so it is inert unless the player enables it
/// via the Dock slider / console.
/// </summary>
public partial class HudDock : Control
{
    /// <summary>QC <c>hud_fade_alpha</c>: the global HUD fade the host may dim the whole HUD by. Fed each
    /// frame from <see cref="Hud.HudFadeAlpha"/> so the dock fades with the rest of the HUD.</summary>
    public float HudFadeAlpha { get; set; } = 1f;

    /// <summary>QC <c>myteamcolors</c> (teamplay): the local player's team color, or null when not teamplay /
    /// not on a team. When set and <c>hud_dock_color_team</c> is on, the dock is tinted by it.</summary>
    public Color? TeamColor { get; set; }

    /// <summary>QC <c>Team_ColorRGB</c> / <c>myteamcolors</c>: the standard team tint (1=red, 2=blue, 3=yellow,
    /// 4=pink), matching the values the menu/scoreboard use. Anything else falls back to white (no tint).</summary>
    public static Color TeamRgb(int team) => team switch
    {
        1 => new Color(1f, 0.0625f, 0.0625f),
        2 => new Color(0.0625f, 0.0625f, 1f),
        3 => new Color(1f, 1f, 0.0625f),
        4 => new Color(1f, 0.0625f, 1f),
        _ => new Color(1f, 1f, 1f),
    };

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore; // the dock never eats input
        // Always cover the full viewport (QC draws at '0 0 0' for vid_conwidth × vid_conheight).
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    private bool _wasOn;

    public override void _Process(double delta)
    {
        // The dock is cvar-driven; redraw every frame WHILE enabled so a live console/menu edit (or the team
        // color changing) takes effect. When off, skip the per-frame re-record entirely (one final redraw on the
        // on→off edge clears the last frame), so the disabled default costs nothing.
        string dock = MenuState.Cvars.GetString("hud_dock");
        bool on = !string.IsNullOrWhiteSpace(dock) && dock != "0";
        if (on || _wasOn)
            QueueRedraw();
        _wasOn = on;
    }

    public override void _Draw()
    {
        string dock = MenuState.Cvars.GetString("hud_dock");
        // QC: hud_dock != "" && hud_dock != "0" — off by default.
        if (string.IsNullOrWhiteSpace(dock) || dock == "0")
            return;

        Vector2 vp = GetViewportRect().Size;
        if (!(vp.X > 0f) || !(vp.Y > 0f))
            return;

        // QC color resolution: team color when teamplay + hud_dock_color_team, else hud_dock_color rgb.
        float teamFactor = MenuState.Cvars.GetFloat("hud_dock_color_team");
        Color tint;
        if (TeamColor is { } tc && teamFactor != 0f)
            tint = new Color(tc.R * teamFactor, tc.G * teamFactor, tc.B * teamFactor, 1f);
        else
        {
            // QC hud_dock_color literals: "team" tints the dock by the local team color (fed by the manager via
            // TeamColor), else parse an rgb triple. Fall back to black when unresolved. (The QC "shirt"/"pants"
            // colormap literals are NOT honoured here — the local-player colormap is not yet plumbed to the HUD;
            // the panels punt on them too, see HudPanel.Resolve. Tracked by the cl-hud.engine.dock shirt/pants gap.)
            string colStr = MenuState.Cvars.GetString("hud_dock_color");
            if (colStr == "team" && TeamColor is { } team) tint = team;
            else tint = HudPanel.TryParseRgbColor(colStr, out Color c) ? c : new Color(0f, 0f, 0f);
        }

        float dockAlpha = MenuState.Cvars.GetFloat("hud_dock_alpha");
        if (string.IsNullOrWhiteSpace(MenuState.Cvars.GetString("hud_dock_alpha")))
            dockAlpha = 1f; // unset → QC default 1
        float a = Mathf.Clamp(dockAlpha, 0f, 1f) * Mathf.Clamp(HudFadeAlpha, 0f, 1f);
        if (!(a > 0f) || !float.IsFinite(a))
            return;
        tint.A = a;

        // QC: pic = "gfx/hud/<skin>/<hud_dock>" → fallback "gfx/hud/<skin>/dock_medium" → "gfx/hud/default/dock_medium".
        Texture2D? tex = TextureCache.GetFirst(
            $"gfx/hud/{HudSkin.SkinName}/{dock}",
            $"gfx/hud/{HudSkin.SkinName}/dock_medium",
            "gfx/hud/default/dock_medium");

        var full = new Rect2(Vector2.Zero, vp);
        if (tex is not null)
            DrawTextureRect(tex, full, false, tint); // QC drawpic: full viewport, no aspect forcing
        else
            DrawRect(full, tint); // art missing: a flat tinted backdrop so the dock still reads
    }
}
