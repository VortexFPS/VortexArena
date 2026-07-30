using System.Globalization;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The common opening block every HUD-panel config dialog shares — the single C# successor to QC
/// <c>dialog_hudpanel_main_checkbox</c> + <c>dialog_hudpanel_main_settings</c> (qcsrc/menu/xonotic/util.qc).
/// Every <c>dialog_hudpanel_&lt;P&gt;.qc</c> opens with this: the panel "Enable" mode then the "Background"
/// group (background style / color / border / opacity / team color / padding), all bound to the same
/// <c>hud_panel_&lt;P&gt;*</c> cvars the QC binds, with the same <c>setDependent</c> grey-outs.
///
/// <para>
/// This is the one shared builder for ALL HUD-panel dialogs (ammo, healtharmor, weapons, centerprint, chat,
/// timer, radar, …). It replaces the per-family copies that earlier ports duplicated. The QC enable is a plain
/// checkbox on <c>hud_panel_&lt;P&gt;</c>, but the cvar takes the 0..3 show-mode the HUD editor uses
/// (<c>HUD_Panel_GetSettingMaxValue</c>), so the faithful control is the 4-mode <see cref="Widgets.TextSlider"/>.
/// The QC border/alpha/team/padding widgets are <c>makeXonoticTextSlider</c>s with discrete preset strings, so
/// we use <see cref="Widgets.TextSlider"/> carrying the QC's exact preset values (the same cvar strings get
/// written). The color row is a <see cref="CvarColorButton"/> (QC <c>makeXonoticColorpickerString</c>).
/// </para>
/// </summary>
public static class HudPanelCommon
{
    /// <summary>Render a 0..1 alpha/scale value as a percentage (the QC slider formatString "%").</summary>
    public static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";

    /// <summary>
    /// HUD skin-list backend hook (QC <c>makeXonoticHUDSkinList</c> / <c>HUD_Panel_ExportCfg</c>, which scan +
    /// write <c>data/data/hud_*.cfg</c>). The configure-mode editor (T27, <see cref="Game.Hud.HudConfigEditor"/>)
    /// exports the live skin via Ctrl+S → the <c>hud save</c> command; the menu-side skin LIST that enumerates
    /// the saved <c>.cfg</c> files for <see cref="DialogHudSetupExit"/> has no file-scan backend yet. When that
    /// backend lands, point this delegate at it and the dialog's list/Refresh/Set/Save route through it; until
    /// then it stays null and the dialog renders the honest "pending" note. Intentionally deferred (T27 scope is
    /// the in-HUD editor, not the menu skin-list enumeration).
    /// </summary>
    public static System.Func<System.Collections.Generic.IEnumerable<string>>? SkinListProvider { get; set; }

    /// <summary>
    /// Emit the common panel block into <paramref name="box"/> for HUD panel <paramref name="panel"/>: the
    /// Enable mode (when <paramref name="includeEnable"/>) then the Background group. Panels that declare their
    /// own enable control first (physics, radar, pressedkeys, strafehud, itemstime, quickmenu) pass
    /// <paramref name="includeEnable"/> = <c>false</c> so only the Background group is emitted.
    /// </summary>
    public static void BuildCommon(VBoxContainer box, string panel, bool includeEnable = true)
    {
        string p = "hud_panel_" + panel;

        if (includeEnable)
        {
            // QC dialog_hudpanel_main_checkbox: a checkbox on hud_panel_<P>. The cvar is 0..3
            // (HUD_Panel_GetSettingMaxValue), so present the full discrete show-mode range the HUD editor uses.
            box.AddChild(Ui.Row("Enable:",
                Widgets.TextSlider(p, "When this panel is shown")
                    .Add("Disabled", 0)
                    .Add("Enabled", 1)
                    .Add("Also enabled in the editor", 2)
                    .Add("Always", 3)));
        }

        box.AddChild(Ui.Spacer(6));
        box.AddChild(Ui.Header("Background"));

        // QC: makeXonoticTextSlider(hud_panel_<P>_bg) — Default("") / Disable("0") / border_<P> (a preset name).
        box.AddChild(Ui.Row("Background:",
            Widgets.TextSlider(p + "_bg", "Background mode for this panel")
                .Add("Default", "")
                .Add("Disable", "0")
                .Add("border_" + panel, "border_" + panel)));

        // QC: makeXonoticColorpickerString(hud_panel_<P>_bg_color, ...); enabled while the background isn't
        // explicitly disabled (setDependentStringNotEqual(bg, "0") — a string compare, so the "border_<P>" and
        // Default("") presets correctly keep the color row enabled).
        var colorRow = Ui.Row("Color:", Widgets.ColorButton(p + "_bg_color"));
        box.AddChild(colorRow);
        Dependent.BindStringNotEqual(colorRow, p + "_bg", "0");

        // QC: makeXonoticCheckBoxString("", "1 1 1", hud_panel_<P>_bg_color, _("Default")) — checked while the
        // color cvar is "" (use the global default); checking writes "", unchecking writes the explicit "1 1 1".
        box.AddChild(Widgets.CheckBox(p + "_bg_color", "Default", on: "", off: "1 1 1"));

        // QC: makeXonoticTextSlider(hud_panel_<P>_bg_border) — Default("") / Disable("0") / 2..20 step 2.
        var border = Widgets.TextSlider(p + "_bg_border", "Border size of this panel's background")
            .Add("Default", "")
            .Add("Disable", "0");
        for (int i = 1; i <= 10; i++)
            border.Add((i * 2).ToString(CultureInfo.InvariantCulture), (i * 2).ToString(CultureInfo.InvariantCulture));
        box.AddChild(Ui.Row("Border size:", border));

        // QC: makeXonoticTextSlider(hud_panel_<P>_bg_alpha) — Default("") then 5%..100% (i/20, i=1..20).
        var alpha = Widgets.TextSlider(p + "_bg_alpha", "Opacity of this panel's background").Add("Default", "");
        for (int i = 1; i <= 20; i++)
        {
            float v = i / 20f;
            alpha.Add($"{Mathf.RoundToInt(v * 100f)}%", v.ToString("0.###", CultureInfo.InvariantCulture));
        }
        box.AddChild(Ui.Row("Opacity:", alpha));

        // QC: makeXonoticTextSlider(hud_panel_<P>_bg_color_team) — Default("") / Disable("0") / 10%..100%
        // (i/10, i=1..10); enabled only while the background opacity isn't 0 (setDependentNOT(bg_alpha, 0)).
        var team = Widgets.TextSlider(p + "_bg_color_team", "Team color of this panel's background")
            .Add("Default", "")
            .Add("Disable", "0");
        for (int i = 1; i <= 10; i++)
        {
            float v = i / 10f;
            team.Add($"{Mathf.RoundToInt(v * 100f)}%", v.ToString("0.###", CultureInfo.InvariantCulture));
        }
        var teamRow = Ui.Row("Team color:", team);
        box.AddChild(teamRow);
        Dependent.BindNot(teamRow, p + "_bg_alpha", 0);

        // QC: makeXonoticCheckBox(0, "hud_configure_teamcolorforced", _("Test team color in configure mode")).
        box.AddChild(Widgets.CheckBox("hud_configure_teamcolorforced", "Test team color in configure mode"));

        // QC: makeXonoticTextSlider(hud_panel_<P>_bg_padding) — Default("") then -5..5 (i-5, i=0..10).
        var padding = Widgets.TextSlider(p + "_bg_padding", "Padding of this panel's background").Add("Default", "");
        for (int i = 0; i <= 10; i++)
            padding.Add((i - 5).ToString(CultureInfo.InvariantCulture), (i - 5).ToString(CultureInfo.InvariantCulture));
        box.AddChild(Ui.Row("Padding:", padding));

        box.AddChild(Ui.Spacer(6));
    }
}
