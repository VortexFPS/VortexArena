using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Engine Info" panel config dialog — a faithful C# port of <c>XonoticHUDEngineInfoDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_engineinfo.qc). The common HUD-panel block (Enable mode + Background
/// group, via <see cref="HudPanelCommon"/>) then the engine-info-specific rows: an "FPS:" header label, the
/// FPS averaging checkbox, and the FPS decimal-places slider.
///
/// FAITHFUL UI NOW: the engine-info panel is rendered in-game by the HUD backend VortexArena hasn't wired up;
/// the cvar bindings are real (they write the shared store the game reads). No live FPS preview is fabricated.
/// </summary>
public partial class DialogHudPanelEngineInfo : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelEngineInfo";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Engine Info Panel"));

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

        // QC: dialog_hudpanel_main_checkbox(me, "engineinfo") + dialog_hudpanel_main_settings(me, "engineinfo").
        HudPanelCommon.BuildCommon(box, "engineinfo");

        // QC: a "FPS:" text-label header, then an indented checkbox + slider group.
        box.AddChild(Ui.Header("FPS"));

        // QC: makeXonoticCheckBox(0, "hud_panel_engineinfo_fps_movingaverage", _("Use an averaging algorithm")).
        box.AddChild(Widgets.CheckBox("hud_panel_engineinfo_fps_movingaverage", "Use an averaging algorithm"));

        // QC: makeXonoticSlider(0, 2, 1, "hud_panel_engineinfo_fps_decimals").
        box.AddChild(Ui.Row("Decimal places:", Widgets.Slider("hud_panel_engineinfo_fps_decimals", 0f, 2f, 1f)));

        // QC HUD-panel dialogs close with Dialog_Close → universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
