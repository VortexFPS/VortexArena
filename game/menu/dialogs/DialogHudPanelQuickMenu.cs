using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Quick Menu" panel config dialog — a faithful C# port of <c>XonoticHUDQuickMenuDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_quickmenu.qc). This panel has NO main enable cvar (the QC comments out
/// <c>dialog_hudpanel_main_checkbox</c>: "this panel has no main cvar"), so the common block is built with its
/// Enable row suppressed — only the Background group. Then the quickmenu-specific rows: text alignment radio
/// set and the translate-commands / use-server-quickmenu checkboxes.
///
/// FAITHFUL UI NOW: the quick-menu panel is drawn by the HUD backend VortexArena hasn't wired up; all cvar
/// bindings are real (they write the shared store the game reads).
/// </summary>
public partial class DialogHudPanelQuickMenu : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelQuickMenu";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Quick Menu Panel"));

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

        // QC: dialog_hudpanel_main_checkbox is commented out (no main cvar). Only dialog_hudpanel_main_settings.
        HudPanelCommon.BuildCommon(box, "quickmenu", includeEnable: false);

        // QC: "Text alignment:" label + makeXonoticRadioButton(3, "hud_panel_quickmenu_align", "0"/"0.5"/"1",
        // Left/Center/Right).
        box.AddChild(Ui.Label("Text alignment:"));
        var alignGroup = new ButtonGroup();
        var alignRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        alignRow.AddThemeConstantOverride("separation", 8);
        alignRow.AddChild(Widgets.RadioButton("hud_panel_quickmenu_align", "0", "Left", alignGroup));
        alignRow.AddChild(Widgets.RadioButton("hud_panel_quickmenu_align", "0.5", "Center", alignGroup));
        alignRow.AddChild(Widgets.RadioButton("hud_panel_quickmenu_align", "1", "Right", alignGroup));
        box.AddChild(alignRow);

        // QC: two independent checkboxes.
        box.AddChild(Widgets.CheckBox("hud_panel_quickmenu_translatecommands", "Translate commands"));
        box.AddChild(Widgets.CheckBox("hud_panel_quickmenu_server_is_default", "Use the server's quickmenu"));

        // QC HUD-panel dialogs close with Dialog_Close → universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
