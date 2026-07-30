using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Score" panel configuration dialog — a faithful C# port of <c>XonoticHUDScoreDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_score.qc). It opens with the common HUD-panel block (Enable mode +
/// Background group from <c>dialog_hudpanel_main_*</c>) then declares the single panel-specific row: the
/// rankings mode radio (Off / And me / Pure).
///
/// FAITHFUL UI NOW: drives the real <c>hud_panel_score_*</c> cvars (the shared store the in-game HUD reads).
/// The Background color uses a string-RGB color picker, built via the shared
/// <see cref="Widgets.ColorButton"/> (<see cref="CvarColorButton"/>) bound to the same "r g b" string cvar.
/// </summary>
public partial class DialogHudPanelScore : MenuScreen
{
    private const string Panel = "score";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelScore"; // QC ATTRIB name "HUDscore"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Score Panel"));

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

        // --- Common HUD-panel block (dialog_hudpanel_main_checkbox + dialog_hudpanel_main_settings) ---------
        HudPanelCommon.BuildCommon(box, Panel);

        // --- Panel-specific rows (XonoticHUDScoreDialog_fill) ------------------------------------------------
        box.AddChild(Ui.Spacer());

        // QC: "Rankings:" label + radio group 1 over hud_panel_score_rankings (Off=0/And me=1/Pure=2).
        box.AddChild(Ui.Label("Rankings:"));
        var rankGroup = new ButtonGroup();
        var rankRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rankRow.AddThemeConstantOverride("separation", 8);
        rankRow.AddChild(Widgets.RadioButton("hud_panel_score_rankings", "0", "Off", rankGroup));
        rankRow.AddChild(Widgets.RadioButton("hud_panel_score_rankings", "1", "And me", rankGroup));
        rankRow.AddChild(Widgets.RadioButton("hud_panel_score_rankings", "2", "Pure", rankGroup));
        box.AddChild(rankRow);

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
