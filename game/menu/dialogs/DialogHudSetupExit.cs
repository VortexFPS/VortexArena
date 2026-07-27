using System.Globalization;
using Godot;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// The "Exit HUD setup" dialog — a faithful C# port of <c>XonoticHUDExitDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudsetup_exit.qc). This is the panel that overlays the live HUD editor: a HUD
/// skin list (filter + Refresh / Set skin / Save current skin), the panel-background defaults applied to every
/// HUD panel (<c>hud_panel_bg*</c>), the HUD Dock settings (<c>hud_dock*</c>), the configure-mode grid options
/// (<c>hud_configure_grid*</c> + the centerline checkbox), and the "Exit setup" button that leaves edit mode.
/// Every cvar control binds the same engine cvar the QC binds, with the same <c>setDependent</c> grey-outs.
///
/// FAITHFUL UI NOW: the cvar bindings are real (they write the shared <see cref="MenuState.Cvars"/> store the
/// in-game HUD reads), and "Save current skin" runs the real exporter (the <c>hud save</c> backend,
/// <see cref="Game.Hud.HudConfigEditor.ExportCfg"/>). Still honest stubs:
///   * the HUD skin <b>list</b> (QC <c>makeXonoticHUDSkinList</c>, which scans data/data/*.cfg) has no file-scan
///     backend, so the list area is an honest short note; the Filter input is a real cvar-less field and the
///     Refresh / Set skin buttons are logged inert until the list backend lands;
///   * the two QC <c>makeXonoticColorpickerString</c> widgets are built via the shared
///     <see cref="Widgets.ColorButton"/> (<see cref="CvarColorButton"/>, a Godot color picker on the same cvar);
///   * "Exit setup" runs the QC command <c>_hud_configure 0</c> through <see cref="Widgets.CommandButton"/>.
/// The QC <c>makeXonoticMixedSlider</c> rows (border size, the two team-color sliders) are reproduced as
/// <see cref="Widgets.TextSlider"/>s carrying the same "Disable"=0 entry + the same discrete value range.
/// </summary>
public partial class DialogHudSetupExit : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudSetupExit"; // QC ATTRIB title "Panel HUD setup"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Panel HUD setup"));

        // The QC dialog is a wide two-column table: the left column is the HUD skin list, the right column the
        // panel/dock/grid default settings. Reproduce that with two side-by-side scrolling columns.
        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        root.AddChild(columns);

        columns.AddChild(MakeColumn(BuildSkinColumn));
        columns.AddChild(MakeColumn(BuildDefaultsColumn));

        // QC bottom row: a single full-width "Exit setup" command button (runs "_hud_configure 0").
        // It both leaves the HUD editor and closes the dialog (COMMANDBUTTON_CLOSE); Back also closes here.
        var exit = Widgets.CommandButton("Exit setup", "_hud_configure 0",
            "Exit the HUD editor and return to the menu");
        root.AddChild(MakeButtonBar(exit, MakeButton("Back", GoBack)));
    }

    /// <summary>One scrolling column holding a vertical list filled by <paramref name="fill"/>.</summary>
    private static ScrollContainer MakeColumn(System.Action<VBoxContainer> fill)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);
        fill(box);
        return scroll;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Left column — "HUD skins" list (filter + the list + Refresh / Set skin / Save).
    // -----------------------------------------------------------------------------------------------------

    private void BuildSkinColumn(VBoxContainer box)
    {
        box.AddChild(Ui.Header("HUD skins")); // QC makeXonoticHeaderLabel(_("HUD skins"))

        // QC: a "Filter:" label + an inputbox whose onChange filters the list (HUDSkinList_Filter_Change).
        // The filter field is not cvar-bound in the QC; with no list backend it is an honest, inert field.
        box.AddChild(Ui.Row("Filter:", new LineEdit
        {
            PlaceholderText = "Filter skins…",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        }, labelMinWidth: 80f));

        // QC: makeXonoticHUDSkinList — a scrolling list of HUD skin .cfg files under data/data/. There is no
        // file-scan / skin-list backend yet, so render the list area as an honest short note rather than fake
        // entries. Set skin / Save below still route the real QC commands.
        var listNote = MakeLabel("(HUD skin list — scans data/data/*.cfg; skin-list backend pending)");
        listNote.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        listNote.CustomMinimumSize = new Vector2(0, 120);
        listNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(listNote);

        // QC: Refresh (HUDSkinList_Refresh_Click) + Set skin (opens the reset-confirm dialog, then applies the
        // selected skin). No list backend → these route through MenuCommand and are logged inert.
        var listBtns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        listBtns.AddThemeConstantOverride("separation", 8);
        listBtns.AddChild(MakeButton("Refresh", () => OnInert("HUD skin list refresh")));
        listBtns.AddChild(MakeButton("Set skin", () => OnInert("apply selected HUD skin (menu_loadhud)")));
        box.AddChild(listBtns);

        box.AddChild(Ui.Spacer());

        // QC: makeXonoticButton_T(_("Save current skin"), …) + an inputbox for the name. Saving routes the QC
        // `hud save <name>` effect — the HUD_Panel_ExportCfg port (HudConfigEditor.ExportCfg) — writing
        // hud_<skin>_<name>.cfg with every hud_* cvar, same as the editor's Ctrl+S.
        var saveName = new LineEdit
        {
            PlaceholderText = "New skin name…",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        var save = MakeButton("Save current skin", () =>
        {
            string name = saveName.Text.Trim();
            Game.Hud.HudConfigEditor.ExportCfg(string.IsNullOrEmpty(name) ? "myconfig" : name);
        });
        save.TooltipText =
            "Note: HUD skins are saved in data/data/ directory and can be manually renamed/deleted from there";
        box.AddChild(save);
        box.AddChild(saveName);
    }

    // -----------------------------------------------------------------------------------------------------
    //  Right column — "Panel background defaults", "HUD Dock", "Grid settings".
    // -----------------------------------------------------------------------------------------------------

    private void BuildDefaultsColumn(VBoxContainer box)
    {
        // ---- Panel background defaults: -----------------------------------------------------------------
        box.AddChild(Ui.Header("Panel background defaults:")); // QC makeXonoticTextLabel header

        // Background — QC makeXonoticTextSlider("hud_panel_bg"): "Disable"=0 + "border_default" (a file name).
        // The QC also pulls the available skin border files in via configureXonoticTextSliderValues; without
        // that scan we expose the two literal entries the QC adds explicitly.
        box.AddChild(Ui.Row("Background:", Widgets.TextSlider("hud_panel_bg")
            .Add("Disable", "0")
            .Add("border_default", "border_default")));

        // Color — QC makeXonoticColorpickerString("hud_panel_bg_color", …).
        box.AddChild(Ui.Row("Color:", new CvarColorButton("hud_panel_bg_color")));

        // Border size — QC makeXonoticMixedSlider("hud_panel_bg_border"): "Disable"=0 + addRange(2,20,2).
        box.AddChild(Ui.Row("Border size:", MakeMixedSlider("hud_panel_bg_border", 2f, 20f, 2f)));

        // Opacity — QC makeXonoticSlider(0.05, 1, 0.05, "hud_panel_bg_alpha") with formatString "%".
        box.AddChild(Ui.Row("Opacity:", Widgets.Slider("hud_panel_bg_alpha", 0.05f, 1f, 0.05f, format: Percent)));

        // Team color — QC makeXonoticMixedSlider("hud_panel_bg_color_team"): "Disable"=0 + addRange(0.1,1,0.1),
        // formatString "%".
        box.AddChild(Ui.Row("Team color:", MakeMixedSlider("hud_panel_bg_color_team", 0.1f, 1f, 0.1f, Percent)));

        // QC makeXonoticCheckBox(0, "hud_configure_teamcolorforced", _("Test team color in configure mode")).
        box.AddChild(Widgets.CheckBox("hud_configure_teamcolorforced", "Test team color in configure mode"));

        // Padding — QC makeXonoticSlider(-5, 5, 1, "hud_panel_bg_padding").
        box.AddChild(Ui.Row("Padding:", Widgets.Slider("hud_panel_bg_padding", -5f, 5f, 1f)));

        box.AddChild(Ui.Spacer());

        // ---- HUD Dock: ----------------------------------------------------------------------------------
        box.AddChild(Ui.Header("HUD Dock:")); // QC "HUD Dock:" label row leads this group

        // QC makeXonoticTextSlider("hud_dock"): Disabled=0 / Small / Medium / Large (the dock background image).
        box.AddChild(Ui.Row("Dock:", Widgets.TextSlider("hud_dock")
            .Add("Disabled", "0")
            .Add("Small", "dock_small")
            .Add("Medium", "dock_medium")
            .Add("Large", "dock_large")));

        // Color — QC makeXonoticColorpickerString("hud_dock_color", …).
        box.AddChild(Ui.Row("Color:", new CvarColorButton("hud_dock_color")));

        // Opacity — QC makeXonoticSlider(0.05, 1, 0.05, "hud_dock_alpha") with formatString "%".
        box.AddChild(Ui.Row("Opacity:", Widgets.Slider("hud_dock_alpha", 0.05f, 1f, 0.05f, format: Percent)));

        // Team color — QC makeXonoticMixedSlider("hud_dock_color_team"): "Disable"=0 + addRange(0.1,1,0.1), "%".
        box.AddChild(Ui.Row("Team color:", MakeMixedSlider("hud_dock_color_team", 0.1f, 1f, 0.1f, Percent)));

        box.AddChild(Ui.Spacer());

        // ---- Grid settings: -----------------------------------------------------------------------------
        box.AddChild(Ui.Header("Grid settings:")); // QC "Grid settings:" label row leads this group

        // QC makeXonoticCheckBox(0, "hud_configure_grid", _("Snap panels to grid")).
        box.AddChild(Widgets.CheckBox("hud_configure_grid", "Snap panels to grid"));

        // Grid size X / Y — QC two makeXonoticSlider(0.005, 0.07, 0.005, …) with formatString "%", valueDigits 3,
        // each setDependent(e, "hud_configure_grid", 1, 1).
        var xRow = Ui.Row("Grid size X:",
            Widgets.Slider("hud_configure_grid_xsize", 0.005f, 0.07f, 0.005f, format: PercentFine));
        box.AddChild(xRow);
        Dependent.Bind(xRow, "hud_configure_grid", 1, 1); // QC setDependent(e,"hud_configure_grid",1,1)

        var yRow = Ui.Row("Grid size Y:",
            Widgets.Slider("hud_configure_grid_ysize", 0.005f, 0.07f, 0.005f, format: PercentFine));
        box.AddChild(yRow);
        Dependent.Bind(yRow, "hud_configure_grid", 1, 1); // QC setDependent(e,"hud_configure_grid",1,1)

        // QC makeXonoticCheckBoxEx_T(0.5, 0, "hud_configure_vertical_lines", _("Center line"), <tooltip>):
        // the cvar holds a count of vertical lines; the checkbox toggles between 0 and 0.5 (one centerline).
        box.AddChild(Widgets.CheckBox("hud_configure_vertical_lines", "Center line",
            "Show a vertical centerline to help align panels. It's possible to show more vertical lines by " +
            "editing hud_configure_vertical_lines in the console",
            on: "0.5", off: "0"));
    }

    // -----------------------------------------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The C# stand-in for one QC <c>makeXonoticMixedSlider</c>: a "Disable"=0 entry followed by a discrete
    /// range of values (QC <c>addText(_("Disable"),0)</c> + <c>addRange(min,max,step)</c>). Rendered as a
    /// <see cref="Widgets.TextSlider"/> on the same cvar; <paramref name="percent"/> formats the labels as a
    /// percentage when the QC set <c>formatString "%"</c>.
    /// </summary>
    private static CvarTextSlider MakeMixedSlider(string cvar, float min, float max, float step,
        System.Func<float, string>? percent = null)
    {
        var slider = Widgets.TextSlider(cvar).Add("Disable", "0");
        for (float v = min; v <= max + step * 0.5f; v += step)
        {
            float val = Mathf.Snapped(v, step);
            string label = percent != null ? percent(val) : CleanNumber(val);
            slider.Add(label, val.ToString(CultureInfo.InvariantCulture));
        }
        return slider;
    }

    /// <summary>Run an action the QC backed with a HUD-editor/skin command XonoticGodot lacks; log it honestly.</summary>
    private static void OnInert(string what)
        => GD.Print($"[DialogHudSetupExit] {what}: HUD editor / skin-list backend pending — inert.");

    /// <summary>Render a 0..1 value as a whole percentage (QC slider formatString "%").</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";

    /// <summary>Render a small fractional grid size as a percentage with one decimal (QC valueDigits 3, "%").</summary>
    private static string PercentFine(float v) => $"{(v * 100f).ToString("0.0", CultureInfo.InvariantCulture)}%";

    /// <summary>A tidy number with no trailing zeros (the plain mixed-slider entry labels).</summary>
    private static string CleanNumber(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
