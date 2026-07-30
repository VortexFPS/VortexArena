using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Pressed Keys" panel config dialog — a faithful C# port of <c>XonoticHUDPressedKeysDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_pressedkeys.qc).
///
/// Like the radar panel, the pressedkeys file does NOT call <c>dialog_hudpanel_main_checkbox</c>; it opens
/// with its own 3-state enable mixedslider on <c>hud_panel_pressedkeys</c> (Disable / Enable spectating /
/// Always enable), then the common <c>dialog_hudpanel_main_settings</c> "Background" group on
/// <c>hud_panel_pressedkeys_bg*</c>, then the panel-specific "Forced aspect" slider and "Show attack keys"
/// checkbox.
///
/// FAITHFUL UI NOW: every binding writes the real <c>hud_panel_pressedkeys*</c> cvars the in-game pressed-keys
/// panel reads. There is no live HUD editor/preview in VortexArena yet, so nothing previews here.
/// </summary>
public partial class DialogHudPanelPressedKeys : MenuScreen
{
    private const string Panel = "pressedkeys";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelPressedKeys";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Pressed Keys Panel"));

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

        // QC: makeXonoticMixedSlider("hud_panel_pressedkeys") with 0/1/2 — this panel's own enable mode (NOT
        // the generic dialog_hudpanel_main_checkbox). Same cvar the common enable would bind.
        var enable = Widgets.TextSlider("hud_panel_pressedkeys")
            .Add("Disable", 0)
            .Add("Enable spectating", 1)
            .Add("Always enable", 2);
        box.AddChild(Ui.Row("Enable:", enable));

        // Common Background group (dialog_hudpanel_main_settings) — hud_panel_pressedkeys_bg*.
        HudPanelCommon.BuildCommon(box, Panel, includeEnable: false);

        box.AddChild(Ui.Spacer());

        // --- Panel-specific rows (dialog_hudpanel_pressedkeys.qc) ---------------------------------------

        // QC: makeXonoticSlider(0.2, 4, 0.1, "hud_panel_pressedkeys_aspect") — no formatString (raw value).
        box.AddChild(Ui.Row("Forced aspect:",
            Widgets.Slider("hud_panel_pressedkeys_aspect", 0.2f, 4f, 0.1f)));

        // QC: makeXonoticCheckBox(0, "hud_panel_pressedkeys_attack", _("Show attack keys")).
        box.AddChild(Widgets.CheckBox("hud_panel_pressedkeys_attack", "Show attack keys"));

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
