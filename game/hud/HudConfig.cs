using System;
using System.Reflection;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Registers the HUD cvar defaults into the shared menu/console store — the C# successor to the
/// <c>_hud_common.cfg</c> / <c>hud_luma.cfg</c> <c>seta hud_*</c> blocks that seeded the HUD's tunables.
/// Called once at boot from <see cref="ClientSettings.ApplyAll"/> (alongside the vignette/tint defaults) so the
/// values are visible/bindable in the menu and console, and so panels reading them get the luma defaults until
/// a config overrides them. <c>Register</c> keeps any value an exec'd cfg / user seta already set (idempotent).
///
/// Two layers are registered:
/// <list type="bullet">
///   <item>The GLOBAL skin cvars (<c>hud_skin</c>, <c>hud_fontsize</c>, <c>hud_panel_bg*</c>,
///         <c>hud_progressbar_*</c>, …).</item>
///   <item>The GENERIC per-panel cvars for every id in <see cref="HudLayoutDefaults"/>
///         (<c>hud_panel_&lt;id&gt;</c>, <c>_pos</c>, <c>_size</c>, <c>_bg</c>, …), seeded from the luma table.</item>
/// </list>
/// Per-panel BEHAVIOUR cvars (e.g. <c>hud_panel_weapons_accuracy</c>) are registered by each panel's own
/// optional <c>public static void RegisterDefaults(CvarService)</c>, which this class invokes by reflection —
/// so a panel owns its own tunables in its own file without a central list.
/// </summary>
public static class HudConfig
{
    private const CvarFlags Save = CvarFlags.Save;

    public static void RegisterDefaults(CvarService c)
    {
        // ---- global / skin cvars (luma) ----
        c.Register("hud_skin", "luma", Save);
        c.Register("hud_fontsize", "11", Save);
        c.Register("hud_width", "0", Save);
        c.Register("hud_panel_update_interval", "2", Save);
        c.Register("hud_panel_bg", "0", Save);
        c.Register("hud_panel_bg_color", "0 0.14 0.25", Save);
        c.Register("hud_panel_bg_color_team", "1", Save);
        c.Register("hud_panel_bg_alpha", "1", Save);
        c.Register("hud_panel_bg_border", "2", Save);
        c.Register("hud_panel_bg_padding", "3", Save);
        c.Register("hud_panel_fg_alpha", "1", Save);
        c.Register("hud_dock", "0", Save);
        c.Register("hud_dock_color", "0 0 0", Save);
        c.Register("hud_dock_color_team", "1", Save);
        c.Register("hud_dock_alpha", "1", Save);
        c.Register("hud_progressbar_alpha", "0.6", Save);
        c.Register("hud_progressbar_health_color", "0.83 0.12 0", Save);
        c.Register("hud_progressbar_armor_color", "0.28 0.8 0", Save);
        c.Register("hud_progressbar_fuel_color", "0.77 0.67 0", Save);
        c.Register("hud_progressbar_oxygen_color", "0.1 1 1", Save);
        c.Register("hud_progressbar_strength_color", "1 0.39 0", Save);
        c.Register("hud_progressbar_shield_color", "0.36 1 0.07", Save);
        c.Register("hud_progressbar_speed_color", "0.77 0.67 0", Save);
        c.Register("hud_progressbar_acceleration_color", "0.2 0.65 0.93", Save);
        c.Register("hud_progressbar_acceleration_neg_color", "0.86 0.35 0", Save);
        c.Register("hud_speed_unit", "1", Save);
        // Named color-macro palette (QC hud_colorset_* → CCR()/HudText.Expand): which ^digit each ^F*/^K*/^BG maps
        // to. Defaults are the hud_luma.cfg values; HudText reads them live so notification text colors stay faithful.
        c.Register("hud_colorset_foreground_1", "2", Save);
        c.Register("hud_colorset_foreground_2", "3", Save);
        c.Register("hud_colorset_foreground_3", "4", Save);
        c.Register("hud_colorset_foreground_4", "1", Save);
        c.Register("hud_colorset_kill_1", "1", Save);
        c.Register("hud_colorset_kill_2", "3", Save);
        c.Register("hud_colorset_kill_3", "4", Save);
        c.Register("hud_colorset_background", "7", Save);

        // ---- HUD configure-mode (editor) cvars — the C# successor to the `_hud_common.cfg` block (Base
        // /.../_hud_common.cfg:2-7) plus the grid cvars whose numeric defaults the loaded skin (luma) seeds.
        // T27 ports the configure-mode editor (HudConfigEditor) over these; registering them here guarantees they
        // exist (and are menu-bindable) even on a headless/test run that never exec'd the HUD cfg tree.
        // NOTE: `_hud_configure` is a `set` (non-archived) in Base — a transient toggle, not a saved pref — so
        // it is registered WITHOUT Save; everything else is `seta` (archived) and keeps the Save flag.
        c.Register("_hud_configure", "0");                                   // set _hud_configure 0
        c.Register("hud_configure_teamcolorforced", "0", Save);             // seta … 0
        // Base ships `hud_configure_checkcollisions 0` (collision snapping OFF by default — _hud_common.cfg:4).
        // The recon brief said "=1"; the SHIPPED Base default is 0 and parity wins, so 0 it is.
        c.Register("hud_configure_checkcollisions", "0", Save);             // seta … 0
        c.Register("hud_configure_bg_minalpha", "0.25", Save);             // seta … 0.25
        c.Register("hud_configure_grid_alpha", "0.15", Save);             // seta … 0.15
        c.Register("hud_configure_vertical_lines", "0.5", Save);         // seta … "0.5"
        // Grid snap defaults come from the loaded skin in Base (hud_luma.cfg: grid 1, xsize/ysize 0.005;
        // _hud_descriptions.cfg leaves them ""). We seed the luma values so the grid works out of the box.
        c.Register("hud_configure_grid", "1", Save);                     // hud_luma.cfg: seta … 1
        c.Register("hud_configure_grid_xsize", "0.005", Save);         // hud_luma.cfg: seta … 0.005
        c.Register("hud_configure_grid_ysize", "0.005", Save);         // hud_luma.cfg: seta … 0.005

        // ---- generic per-panel cvars, seeded from the luma table ----
        foreach (string id in HudLayoutDefaults.Ids)
        {
            HudLayoutDefaults.Entry e = HudLayoutDefaults.For(id);
            string p = "hud_panel_" + id;
            c.Register(p, e.Enable, Save);
            c.Register(p + "_pos", $"{F(e.Pos.X)} {F(e.Pos.Y)}", Save);
            c.Register(p + "_size", $"{F(e.Size.X)} {F(e.Size.Y)}", Save);
            c.Register(p + "_bg", e.Bg, Save);
            c.Register(p + "_bg_color", e.BgColor, Save);
            c.Register(p + "_bg_color_team", "", Save);
            c.Register(p + "_bg_alpha", e.BgAlpha, Save);
            c.Register(p + "_bg_border", e.Border, Save);
            c.Register(p + "_bg_padding", "", Save);
            c.Register(p + "_fg_alpha", "", Save);
            c.Register(p + "_fontsize", "", Save);
        }

        // ---- per-panel behaviour cvars (each panel registers its own) ----
        foreach (Type t in HudRegistry.PanelTypes)
        {
            MethodInfo? m = t.GetMethod("RegisterDefaults",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(CvarService) }, null);
            try { m?.Invoke(null, new object[] { c }); }
            catch { /* a panel's optional registrar must never break boot */ }
        }
    }

    // Compact invariant float for cvar default strings (no trailing zeros, '.' decimal).
    private static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
