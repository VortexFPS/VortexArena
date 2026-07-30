using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Notification HUD panel config — a faithful C# port of <c>XonoticHUDNotificationDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_notification.qc). Configures the notification panel (the running list
/// of kill/event messages). NOTE the panel cvar prefix is <c>hud_panel_notify</c> (the QC panelname is
/// <c>"notify"</c>, not "notification").
///
/// Layout follows the QC exactly: first the common HUD-panel block
/// (<c>dialog_hudpanel_main_checkbox</c> + <c>dialog_hudpanel_main_settings</c>, via the shared
/// <see cref="HudPanelCommon.BuildCommon"/>), then the panel-specific rows: entry lifetime, entry fadetime, and a
/// "Flip notify order" checkbox. The panel-specific rows depend on <c>hud_panel_notify_time != 0</c>
/// (QC <c>setDependentNOT</c>).
///
/// FAITHFUL UI NOW: the <c>hud_panel_notify_*</c> cvars drive the in-game HUD renderer/editor VortexArena has not
/// wired yet, but the bindings are REAL (they write the shared <see cref="MenuState.Cvars"/> store). No
/// apply/command button exists (just Back). The QC "Entry fadetime" widget is a <c>makeXonoticMixedSlider</c>
/// ("Instant"=0 + numeric range 0.5..5 step 0.5), reproduced with a <see cref="Widgets.TextSlider"/> over the
/// same stored values.
/// </summary>
public partial class DialogHudPanelNotification : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelNotification"; // QC ATTRIB name "HUDnotify"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Notifications"));

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

        // --- common HUD-panel block: enable mode + Background group (panelname "notify") ---
        HudPanelCommon.BuildCommon(box, "notify");

        // --- panel-specific rows ---

        // Entry lifetime: makeXonoticSlider(3,15,1, hud_panel_notify_time), formatString "s".
        box.AddChild(Ui.Row("Entry lifetime:",
            Widgets.Slider("hud_panel_notify_time", 3f, 15f, 1f, format: v => $"{Mathf.RoundToInt(v)}s")));

        // Entry fadetime: makeXonoticMixedSlider(hud_panel_notify_fadetime) — "Instant"=0, addRange(0.5,5,0.5).
        var fade = Widgets.TextSlider("hud_panel_notify_fadetime").Add("Instant", 0f);
        for (float v = 0.5f; v <= 5.0001f; v += 0.5f)
            fade.Add($"{v:0.##}s", Mathf.Snapped(v, 0.5f));
        var fadeRow = Ui.Row("Entry fadetime:", fade);
        box.AddChild(fadeRow);
        Dependent.BindNot(fadeRow, "hud_panel_notify_time", 0);

        // Flip notify order: makeXonoticCheckBox(0, hud_panel_notify_flip, ...).
        var flip = Widgets.CheckBox("hud_panel_notify_flip", "Flip notify order");
        box.AddChild(flip);
        Dependent.BindNot(flip, "hud_panel_notify_time", 0);

        // QC dialog has no apply/command button — only the universal Back closes it.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

}
