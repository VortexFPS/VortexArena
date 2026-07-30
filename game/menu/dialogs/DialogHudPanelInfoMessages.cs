using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Info-messages HUD panel config — a faithful C# port of <c>XonoticHUDInfoMessagesDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_infomessages.qc). Configures the info-messages panel (the small
/// status lines like "Press jump to spawn", spectator hints, etc.). Panel cvar prefix is
/// <c>hud_panel_infomessages</c>.
///
/// Layout follows the QC exactly: first the common HUD-panel block
/// (<c>dialog_hudpanel_main_checkbox</c> + <c>dialog_hudpanel_main_settings</c>, via the shared
/// <see cref="HudPanelCommon.BuildCommon"/>), then the panel-specific rows: message duration, fade time and a
/// 2-way text alignment radio (Left=0 / Right=1). The panel-specific rows depend on
/// <c>hud_panel_infomessages_group_time != 0</c> (QC <c>setDependentNOT</c>).
///
/// FAITHFUL UI NOW: the <c>hud_panel_infomessages_*</c> cvars drive the in-game HUD renderer/editor VortexArena has
/// not wired yet, but the bindings are REAL (they write the shared <see cref="MenuState.Cvars"/> store). No
/// apply/command button exists (just Back). The QC "Fade time" widget is a <c>makeXonoticMixedSlider</c>
/// ("Instant"=0 + numeric range), reproduced with a <see cref="Widgets.TextSlider"/> over the same values.
/// </summary>
public partial class DialogHudPanelInfoMessages : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelInfoMessages"; // QC ATTRIB name "HUDinfomessages"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Info Messages"));

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

        // --- common HUD-panel block: enable mode + Background group ---
        HudPanelCommon.BuildCommon(box, "infomessages");

        // --- panel-specific rows ---

        // Message duration: makeXonoticSlider(1,10,1, hud_panel_infomessages_group_time), formatString "s".
        box.AddChild(Ui.Row("Message duration:",
            Widgets.Slider("hud_panel_infomessages_group_time", 1f, 10f, 1f, format: v => $"{Mathf.RoundToInt(v)}s")));

        // Fade time: makeXonoticMixedSlider(hud_panel_infomessages_group_fadetime) — "Instant"=0, addRange(0.05,1,0.05).
        var fade = Widgets.TextSlider("hud_panel_infomessages_group_fadetime").Add("Instant", 0f);
        for (float v = 0.05f; v <= 1.0001f; v += 0.05f)
            fade.Add($"{v:0.##}s", Mathf.Snapped(v, 0.05f));
        var fadeRow = Ui.Row("Fade time:", fade);
        box.AddChild(fadeRow);
        Dependent.BindNot(fadeRow, "hud_panel_infomessages_group_time", 0);

        // Text alignment: a 2-way radio (Left=0 / Right=1) on hud_panel_infomessages_flip, beside its label.
        // QC puts the "Text alignment:" label and the two radios on one row; Ui.Row supplies the label column.
        var alignGroup = new ButtonGroup();
        var alignControls = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        alignControls.AddThemeConstantOverride("separation", 8);
        alignControls.AddChild(Widgets.RadioButton("hud_panel_infomessages_flip", "0", "Left", alignGroup));
        alignControls.AddChild(Widgets.RadioButton("hud_panel_infomessages_flip", "1", "Right", alignGroup));
        var alignRow = Ui.Row("Text alignment:", alignControls);
        box.AddChild(alignRow);
        Dependent.BindNot(alignRow, "hud_panel_infomessages_group_time", 0);

        // QC dialog has no apply/command button — only the universal Back closes it.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

}
