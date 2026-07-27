using System;
using System.Collections.Generic;
using Godot;

namespace XonoticGodot.Game.Hud;

/// <summary>QC <c>PANEL_SHOW_*</c> — the game contexts a panel is allowed to draw in.</summary>
[Flags]
public enum PanelShow
{
    None = 0,
    MainGame = 1,        // PANEL_SHOW_MAINGAME — normal play
    Minigame = 2,        // PANEL_SHOW_MINIGAME — while a board minigame is active
    Mapvote = 4,         // PANEL_SHOW_MAPVOTE  — intermission map vote
    WithScoreboard = 8,  // PANEL_SHOW_WITH_SB  — keep drawing while the scoreboard is up
}

/// <summary>QC <c>PANEL_CONFIG_*</c> — editor/toggle capability of a panel.</summary>
[Flags]
public enum PanelConfig
{
    No = 0,
    Main = 1,       // PANEL_CONFIG_MAIN — movable in the (unported) HUD editor
    CanBeOff = 2,   // PANEL_CONFIG_CANBEOFF — user may disable via hud_panel_<id> 0
}

/// <summary>Normalized 0..1 default position + size for a panel (QC <c>hud_panel_&lt;id&gt;_pos/_size</c>).</summary>
public readonly record struct PanelLayoutDefault(Vector2 PosFraction, Vector2 SizeFraction);

/// <summary>
/// The luma HUD-skin reference defaults, baked as a table keyed by panel id. This is what lets every existing
/// <see cref="HudPanel"/> jump to the correct Xonotic layout + skin frame + show-context with zero per-panel
/// edits: <see cref="HudPanel"/>'s virtuals (<c>PanelId</c>, <c>DefaultLayout</c>, <c>ShowFlags</c>,
/// <c>DefaultBg</c>…) all default to looking themselves up here. <see cref="HudConfig"/> also reads this table
/// to register the per-panel cvar defaults. Numbers are from <c>hud_luma.cfg</c> / the reference
/// <c>REGISTER_HUD_PANEL</c> show-flag list (see planning/HUD_PARITY_CONTRACT.md §3.3).
/// </summary>
public static class HudLayoutDefaults
{
    /// <summary>One panel's full default set.</summary>
    public readonly record struct Entry(
        Vector2 Pos, Vector2 Size,
        string Bg, string Border, string Enable,
        string BgColor, string BgAlpha,
        PanelShow Show, PanelConfig Config);

    private const PanelShow Main = PanelShow.MainGame;
    private const PanelShow MainSb = PanelShow.MainGame | PanelShow.WithScoreboard;
    private const PanelShow MainMiniSb = PanelShow.MainGame | PanelShow.Minigame | PanelShow.WithScoreboard;
    private const PanelShow All = PanelShow.MainGame | PanelShow.Minigame | PanelShow.Mapvote | PanelShow.WithScoreboard;
    private const PanelConfig Cfg = PanelConfig.Main | PanelConfig.CanBeOff;
    private const PanelConfig CfgNo = PanelConfig.No;

    private static Entry E(float px, float py, float sx, float sy, string bg, string border, string enable,
                           PanelShow show, PanelConfig config = PanelConfig.Main | PanelConfig.CanBeOff,
                           string bgColor = "", string bgAlpha = "")
        => new(new Vector2(px, py), new Vector2(sx, sy), bg, border, enable, bgColor, bgAlpha, show, config);

    // id → luma defaults. Ids match HudPanel.PanelId (class name minus Panel/Hud, lowercased).
    private static readonly Dictionary<string, Entry> Table = new()
    {
        ["healtharmor"]  = E(0.30f, 0.925f, 0.40f, 0.07f,  "border_default_south",    "4", "1", Main),
        ["ammo"]         = E(0.315f, 0.865f, 0.37f, 0.06f, "border_tab_south",        "",  "1", Main),
        ["powerups"]     = E(0.325f, 0.815f, 0.35f, 0.055f,"border_shadow_south",     "",  "1", Main),
        ["weapons"]      = E(0.965f, 0.125f, 0.035f, 0.77f,"border_default_east",     "",  "1", Main),
        ["notify"]       = E(0.73f, 0.80f, 0.265f, 0.20f,  "0",                       "",  "1", MainMiniSb),
        ["timer"]        = E(0.45f, 0.00f, 0.10f, 0.05f,   "border_plain_north",      "",  "1", MainMiniSb),
        ["radar"]        = E(0.00f, 0.00f, 0.20f, 0.25f,   "border_corner_northwest", "",  "1", Main),
        ["score"]        = E(0.88f, 0.00f, 0.12f, 0.08f,   "border_corner_northeast", "",  "1", PanelShow.MainGame | PanelShow.Minigame),
        ["racetimer"]    = E(0.36f, 0.11f, 0.28f, 0.09f,   "0",                       "",  "1", Main),
        ["vote"]         = E(0.74f, 0.69f, 0.19f, 0.09f,   "border_default",          "",  "1", All),
        ["modicons"]     = E(0.37f, 0.03f, 0.26f, 0.07f,   "border_fading_north",     "4", "1", MainSb),
        ["pressedkeys"]  = E(0.445f, 0.71f, 0.11f, 0.09f,  "border_default",          "",  "1", Main),
        ["chat"]         = E(0.01f, 0.70f, 0.46f, 0.19f,   "0",                       "",  "1", All),
        ["engineinfo"]   = E(0.93f, 0.97f, 0.07f, 0.03f,   "0",                       "",  "0", All),
        ["infomessages"] = E(0.68f, 0.10f, 0.28f, 0.08f,   "0",                       "",  "1", Main),
        ["physics"]      = E(0.41f, 0.625f, 0.18f, 0.08f,  "0",                       "",  "3", Main),
        ["centerprint"]  = E(0.175f, 0.22f, 0.65f, 0.22f,  "0",                       "",  "1", MainSb),
        ["itemstime"]    = E(0.03f, 0.26f, 0.07f, 0.23f,   "border_default",          "",  "2", Main),
        ["quickmenu"]    = E(0.60f, 0.445f, 0.22f, 0.24f,  "",                        "",  "1", PanelShow.MainGame | PanelShow.Minigame, PanelConfig.Main),
        ["scoreboard"]   = E(0.15f, 0.15f, 0.70f, 0.70f,   "border_default",          "",  "1", All, CfgNo, "0 0.3 0.5", "0.7"),
        ["strafehud"]    = E(0.32f, 0.57f, 0.36f, 0.02f,   "0",                       "",  "3", Main, Cfg, "", "0.7"),
        ["pickup"]       = E(0.01f, 0.945f, 0.26f, 0.035f, "0",                       "",  "1", Main),
        ["checkpoints"]  = E(0.70f, 0.19f, 0.25f, 0.17f,   "",                        "",  "1", Main),
        ["minigamehelp"] = E(0.22f, 0.78f, 0.50f, 0.20f,   "",                        "",  "1", PanelShow.Minigame | PanelShow.WithScoreboard),
        ["mapvote"]      = E(0.00f, 0.00f, 1.00f, 1.00f,   "border_default",          "",  "1", PanelShow.Mapvote, CfgNo),
        ["minigamemenu"] = E(0.60f, 0.445f, 0.22f, 0.24f,  "",                        "",  "1", MainMiniSb, CfgNo),

        // ---- port extras (NOT stock panels) — self-positioning, full-viewport, no frame ----
        ["crosshair"]    = E(0.00f, 0.00f, 1.00f, 1.00f, "0", "", "1", Main, CfgNo),
        ["fps"]          = E(0.00f, 0.00f, 1.00f, 1.00f, "0", "", "1", All, CfgNo),
        ["ping"]         = E(0.00f, 0.00f, 1.00f, 1.00f, "0", "", "1", All, CfgNo),
        ["position"]     = E(0.00f, 0.00f, 1.00f, 1.00f, "0", "", "1", All, CfgNo),
        ["vehicle"]      = E(0.29f, 0.84f, 0.42f, 0.16f, "0", "", "1", Main, CfgNo),
        // The map editor's status readout. Full-viewport and self-positioning like the other port extras:
        // this table is what the generic per-panel cvars (pos/size/bg/enable) are seeded from, and a panel
        // missing from it gets no size cvar at all and resolves to a zero rect — i.e. draws nothing.
        ["editor"]       = E(0.00f, 0.00f, 1.00f, 1.00f, "0", "", "1", All, CfgNo),
    };

    /// <summary>All ids in the table (for cvar registration).</summary>
    public static IEnumerable<string> Ids => Table.Keys;

    /// <summary>Look up a panel's defaults, or a sane full-viewport fallback for an unknown id.</summary>
    public static Entry For(string id)
        => Table.TryGetValue(id, out Entry e)
            ? e
            : E(0f, 0f, 1f, 1f, "0", "", "1", PanelShow.MainGame, PanelConfig.Main);

    /// <summary>Derive the panel id from a type name (HealthArmorPanel → "healtharmor", VehicleHud → "vehicle").</summary>
    public static string DeriveId(Type t)
    {
        string n = t.Name;
        if (n.EndsWith("Panel", StringComparison.Ordinal)) n = n[..^5];
        else if (n.EndsWith("Hud", StringComparison.Ordinal)) n = n[..^3];
        return n.ToLowerInvariant();
    }
}
