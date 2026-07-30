using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Center-print HUD panel config — a faithful C# port of <c>XonoticHUDCenterprintDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_centerprint.qc). Configures the center-print panel of the in-game HUD
/// (the big middle-screen messages: round announcements, hints, etc.).
///
/// Layout follows the QC exactly: first the common HUD-panel block
/// (<c>dialog_hudpanel_main_checkbox</c> + <c>dialog_hudpanel_main_settings</c>, via the shared
/// <see cref="HudPanelCommon.BuildCommon"/>), then the panel-specific rows the .qc
/// declares (message duration, fade time, the three font scales, flip order, and the 3-way text alignment).
/// Every control binds the same <c>hud_panel_centerprint_*</c> cvar the QC binds, with the same
/// <c>setDependentNOT(..., "hud_panel_centerprint_time", 0)</c> grey-out (every panel-specific row greys out
/// while message duration is 0, i.e. the panel disabled).
///
/// FAITHFUL UI NOW: these cvars drive the in-game HUD editor / HUD renderer VortexArena does not have wired yet, but
/// the bindings are REAL — they write the shared <see cref="MenuState.Cvars"/> store the HUD will read. No
/// command/apply buttons exist in this dialog (just the universal Back). The QC "Fade time" widget is a
/// <c>makeXonoticMixedSlider</c> (an "Instant"=0 text entry plus a numeric range); reproduced with a
/// <see cref="Widgets.TextSlider"/> carrying the exact same stored values (noted).
/// </summary>
public partial class DialogHudPanelCenterPrint : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelCenterPrint"; // QC ATTRIB name "HUDcenterprint"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Center Print"));

        // The form is long; wrap it in a ScrollContainer > VBox so it scrolls. NEVER nest a TabContainer here.
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

        // --- common HUD-panel block: enable mode + Background group (QC dialog_hudpanel_main_*) ---
        HudPanelCommon.BuildCommon(box, "centerprint");

        // --- panel-specific rows (QC after dialog_hudpanel_main_settings) ---

        // Message duration: makeXonoticSlider(1,10,1, hud_panel_centerprint_time), formatString "s".
        box.AddChild(Ui.Row("Message duration:",
            Widgets.Slider("hud_panel_centerprint_time", 1f, 10f, 1f, format: v => $"{Mathf.RoundToInt(v)}s")));

        // Fade time: makeXonoticMixedSlider(hud_panel_centerprint_fade_out) — "Instant"=0 then addRange(0.05,1,0.05).
        // Reproduced as a TextSlider with the identical stored values (mixedslider has no toolkit factory).
        var fade = Widgets.TextSlider("hud_panel_centerprint_fade_out").Add("Instant", 0f);
        for (float v = 0.05f; v <= 1.0001f; v += 0.05f)
            fade.Add($"{v:0.##}s", Mathf.Snapped(v, 0.05f));
        var fadeRow = Ui.Row("Fade time:", fade);
        box.AddChild(fadeRow);
        Dependent.BindNot(fadeRow, "hud_panel_centerprint_time", 0);

        // Font scale: makeXonoticSlider(0.5, 2, 0.1, hud_panel_centerprint_fontscale).
        var fontRow = Ui.Row("Font scale:",
            Widgets.Slider("hud_panel_centerprint_fontscale", 0.5f, 2f, 0.1f));
        box.AddChild(fontRow);
        Dependent.BindNot(fontRow, "hud_panel_centerprint_time", 0);

        // Bold font scale: makeXonoticSlider(0.5, 3, 0.1, hud_panel_centerprint_fontscale_bold).
        var boldRow = Ui.Row("Bold font scale:",
            Widgets.Slider("hud_panel_centerprint_fontscale_bold", 0.5f, 3f, 0.1f));
        box.AddChild(boldRow);
        Dependent.BindNot(boldRow, "hud_panel_centerprint_time", 0);

        // Title font scale: makeXonoticSlider(0.5, 4, 0.1, hud_panel_centerprint_fontscale_title).
        var titleRow = Ui.Row("Title font scale:",
            Widgets.Slider("hud_panel_centerprint_fontscale_title", 0.5f, 4f, 0.1f));
        box.AddChild(titleRow);
        Dependent.BindNot(titleRow, "hud_panel_centerprint_time", 0);

        // Flip messages order: makeXonoticCheckBox(0, hud_panel_centerprint_flip, ...).
        var flip = Widgets.CheckBox("hud_panel_centerprint_flip", "Flip messages order");
        box.AddChild(flip);
        Dependent.BindNot(flip, "hud_panel_centerprint_time", 0);

        // Text alignment: a 3-way radio set (Left=0 / Center=0.5 / Right=1) on hud_panel_centerprint_align.
        var alignLabel = Ui.Label("Text alignment:");
        box.AddChild(alignLabel);
        Dependent.BindNot(alignLabel, "hud_panel_centerprint_time", 0);

        var alignGroup = new ButtonGroup();
        var alignRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        alignRow.AddThemeConstantOverride("separation", 8);
        alignRow.AddChild(Widgets.RadioButton("hud_panel_centerprint_align", "0", "Left", alignGroup));
        alignRow.AddChild(Widgets.RadioButton("hud_panel_centerprint_align", "0.5", "Center", alignGroup));
        alignRow.AddChild(Widgets.RadioButton("hud_panel_centerprint_align", "1", "Right", alignGroup));
        box.AddChild(alignRow);
        Dependent.BindNot(alignRow, "hud_panel_centerprint_time", 0);

        // QC dialog has no apply/command button — only the universal Back closes it.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

}
