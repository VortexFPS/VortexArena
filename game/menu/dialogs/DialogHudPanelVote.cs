using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Vote" panel config dialog — a faithful C# port of <c>XonoticHUDVoteDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_vote.qc).
///
/// Emits the common panel block first (<c>dialog_hudpanel_main_checkbox</c> "Enable" on
/// <c>hud_panel_vote</c>, then the <c>dialog_hudpanel_main_settings</c> "Background" group on
/// <c>hud_panel_vote_bg*</c>), then the single panel-specific row: "Opacity after voting".
///
/// FAITHFUL UI NOW: the binding writes the real <c>hud_panel_vote*</c> cvars the in-game vote panel reads;
/// there is no live HUD editor/preview in VortexArena yet, so nothing previews here.
/// </summary>
public partial class DialogHudPanelVote : MenuScreen
{
    private const string Panel = "vote";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelVote";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Vote Panel"));

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

        // Common HUD-panel block: enable mode + Background group.
        HudPanelCommon.BuildCommon(box, Panel);

        box.AddChild(Ui.Spacer());

        // --- Panel-specific rows (dialog_hudpanel_vote.qc) ----------------------------------------------

        // QC: makeXonoticSlider(0.1, 1, 0.1, "hud_panel_vote_alreadyvoted_alpha"); formatString "%".
        box.AddChild(Ui.Row("Opacity after voting:",
            Widgets.Slider("hud_panel_vote_alreadyvoted_alpha", 0.1f, 1f, 0.1f, format: Percent)));

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    /// <summary>QC slider formatString "%": show a 0..1 alpha as a percentage.</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}
