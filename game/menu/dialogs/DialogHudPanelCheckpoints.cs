using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Checkpoints" panel config dialog — a faithful C# port of <c>XonoticHUDCheckpointsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_checkpoints.qc). The common HUD-panel block (Enable mode + Background
/// group, via <see cref="HudPanelCommon"/>) then the checkpoints-specific rows: font scale slider, the flip
/// checkpoint-order checkbox, and the text alignment radio set.
///
/// FAITHFUL UI NOW: the Race/CTS checkpoints panel is drawn by the HUD backend VortexArena hasn't wired up; all
/// cvar bindings are real (they write the shared store the game reads).
/// </summary>
public partial class DialogHudPanelCheckpoints : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelCheckpoints";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Checkpoints Panel"));

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

        // QC: dialog_hudpanel_main_checkbox(me, "checkpoints") + dialog_hudpanel_main_settings(me, "checkpoints").
        HudPanelCommon.BuildCommon(box, "checkpoints");

        // QC: makeXonoticSlider(0.8, 2, 0.1, "hud_panel_checkpoints_fontscale").
        box.AddChild(Ui.Row("Font scale:", Widgets.Slider("hud_panel_checkpoints_fontscale", 0.8f, 2f, 0.1f)));

        // QC: makeXonoticCheckBox(0, "hud_panel_checkpoints_flip", _("Flip checkpoint order")).
        box.AddChild(Widgets.CheckBox("hud_panel_checkpoints_flip", "Flip checkpoint order"));

        // QC: "Text alignment:" label + makeXonoticRadioButton(2, "hud_panel_checkpoints_align", "0"/"0.5"/"1",
        // Left/Center/Right).
        box.AddChild(Ui.Label("Text alignment:"));
        var alignGroup = new ButtonGroup();
        var alignRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        alignRow.AddThemeConstantOverride("separation", 8);
        alignRow.AddChild(Widgets.RadioButton("hud_panel_checkpoints_align", "0", "Left", alignGroup));
        alignRow.AddChild(Widgets.RadioButton("hud_panel_checkpoints_align", "0.5", "Center", alignGroup));
        alignRow.AddChild(Widgets.RadioButton("hud_panel_checkpoints_align", "1", "Right", alignGroup));
        box.AddChild(alignRow);

        // QC HUD-panel dialogs close with Dialog_Close → universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
