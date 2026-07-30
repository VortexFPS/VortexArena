using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Timer" panel config dialog — a faithful C# port of <c>XonoticHUDTimerDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_timer.qc).
///
/// As every HUD-panel dialog does, it first emits the common panel block:
///   * <c>dialog_hudpanel_main_checkbox</c> — the "Enable" control (here the QC uses a plain checkbox on
///     <c>hud_panel_timer</c>; we render it as the standard 0..3 enable mode <see cref="Widgets.TextSlider"/>
///     so all the hud-b panels are uniform — same cvar, same effect), and
///   * <c>dialog_hudpanel_main_settings</c> — the "Background" group (bg / color / border / opacity /
///     team-color / padding) bound to <c>hud_panel_timer_bg*</c>.
/// THEN the panel-specific rows the .qc declares: "Show elapsed time" and the 3-way "Secondary timer" radio.
///
/// FAITHFUL UI NOW: these cvar bindings are real (they write the shared store the in-game HUD reads). There is
/// no in-game HUD editor/preview wired into VortexArena yet, so nothing here previews live; the controls simply
/// edit the same <c>hud_panel_*</c> cvars the HUD will consume.
/// </summary>
public partial class DialogHudPanelTimer : MenuScreen
{
    private const string Panel = "timer";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelTimer";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Timer Panel"));

        // The dense form scrolls (NEVER a TabContainer inside the scroll — just a vertical list).
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // Common HUD-panel block: enable mode + Background group (dialog_hudpanel_main_checkbox/_settings).
        HudPanelCommon.BuildCommon(box, Panel);

        box.AddChild(Ui.Spacer());

        // --- Panel-specific rows (dialog_hudpanel_timer.qc) ---------------------------------------------

        // QC: makeXonoticCheckBox(0, "hud_panel_timer_increment", _("Show elapsed time"))
        box.AddChild(Widgets.CheckBox("hud_panel_timer_increment", "Show elapsed time"));

        // QC: a "Secondary timer:" label then a 3-way radio (group 2) on hud_panel_timer_secondary.
        box.AddChild(Ui.Label("Secondary timer:"));
        var secGroup = new ButtonGroup();
        var secRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        secRow.AddThemeConstantOverride("separation", 8);
        secRow.AddChild(Widgets.RadioButton("hud_panel_timer_secondary", "0", "Disable", secGroup));
        secRow.AddChild(Widgets.RadioButton("hud_panel_timer_secondary", "1", "Enable", secGroup));
        secRow.AddChild(Widgets.RadioButton("hud_panel_timer_secondary", "2", "Swapped", secGroup));
        box.AddChild(secRow);

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
