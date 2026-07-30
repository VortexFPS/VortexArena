using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Radar" panel config dialog — a faithful C# port of <c>XonoticHUDRadarDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_radar.qc).
///
/// The radar file does NOT call <c>dialog_hudpanel_main_checkbox</c>; instead it opens with its own
/// 3-state enable mixedslider on <c>hud_panel_radar</c> (Disable / Enable in team games / Always enable),
/// then the common <c>dialog_hudpanel_main_settings</c> "Background" group, then the radar-specific rows.
/// Every radar-specific row is gated on <c>hud_panel_radar_foreground_alpha != 0</c> (QC setDependentNOT),
/// i.e. they grey out when the radar is fully transparent.
///
/// FAITHFUL UI NOW: every binding writes the real shared <c>hud_panel_radar*</c> cvars the in-game radar
/// reads. VortexArena has no live HUD editor/preview yet, so nothing previews here; the controls just edit cvars.
/// </summary>
public partial class DialogHudPanelRadar : MenuScreen
{
    private const string Panel = "radar";
    // QC gates the whole panel-specific block on this cvar being nonzero.
    private const string AlphaCvar = "hud_panel_radar_foreground_alpha";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelRadar";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Radar Panel"));

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

        // QC: makeXonoticMixedSlider("hud_panel_radar") with 0/1/2 — this panel's own enable mode (NOT the
        // generic dialog_hudpanel_main_checkbox). Same cvar the common enable would bind.
        var enable = Widgets.TextSlider("hud_panel_radar")
            .Add("Disable", 0)
            .Add("Enable in team games", 1)
            .Add("Always enable", 2);
        box.AddChild(Ui.Row("Enable:", enable));

        // Common Background group (dialog_hudpanel_main_settings) — hud_panel_radar_bg*.
        HudPanelCommon.BuildCommon(box, Panel, includeEnable: false);

        box.AddChild(Ui.Spacer());

        // --- Panel-specific rows (dialog_hudpanel_radar.qc) ---------------------------------------------

        // QC: makeXonoticSlider(0.1, 1, 0.1, "hud_panel_radar_foreground_alpha"); formatString "%".
        box.AddChild(Ui.Row("Opacity:",
            Widgets.Slider(AlphaCvar, 0.1f, 1f, 0.1f, format: Percent)));

        // Everything below is setDependentNOT(e, hud_panel_radar_foreground_alpha, 0): greyed when alpha == 0.

        // QC: mixedslider "hud_panel_radar_rotation" — Forward/West/South/East/North.
        var rotation = Widgets.TextSlider("hud_panel_radar_rotation")
            .Add("Forward", 0).Add("West", 1).Add("South", 2).Add("East", 3).Add("North", 4);
        var rotationRow = Ui.Row("Rotation:", rotation);
        box.AddChild(rotationRow);
        Dependent.BindNot(rotationRow, AlphaCvar, 0);

        // QC: makeXonoticSlider(1024, 8192, 512, "hud_panel_radar_scale"); formatString "%s qu".
        var scale = Widgets.Slider("hud_panel_radar_scale", 1024f, 8192f, 512f, format: Qu);
        var scaleRow = Ui.Row("Scale:", scale);
        box.AddChild(scaleRow);
        Dependent.BindNot(scaleRow, AlphaCvar, 0);

        // QC: mixedslider "hud_panel_radar_zoommode" — Zoomed in/out/Always/Never.
        var zoom = Widgets.TextSlider("hud_panel_radar_zoommode")
            .Add("Zoomed in", 0).Add("Zoomed out", 1).Add("Always zoomed", 2).Add("Never zoomed", 3);
        var zoomRow = Ui.Row("Zoom mode:", zoom);
        box.AddChild(zoomRow);
        Dependent.BindNot(zoomRow, AlphaCvar, 0);

        // QC: a "Maximized radar:" header label (itself dependent on alpha), then maximized rotation/scale/zoom.
        var maxLabel = Ui.Header("Maximized radar:");
        box.AddChild(maxLabel);
        Dependent.BindNot(maxLabel, AlphaCvar, 0);

        // QC: mixedslider "hud_panel_radar_maximized_rotation" — Forward/West/South/East/North.
        var maxRotation = Widgets.TextSlider("hud_panel_radar_maximized_rotation")
            .Add("Forward", 0).Add("West", 1).Add("South", 2).Add("East", 3).Add("North", 4);
        var maxRotationRow = Ui.Row("Rotation:", maxRotation);
        box.AddChild(maxRotationRow);
        Dependent.BindNot(maxRotationRow, AlphaCvar, 0);

        // QC: makeXonoticSlider(1024, 8192, 512, "hud_panel_radar_maximized_scale"); formatString "%s qu".
        var maxScale = Widgets.Slider("hud_panel_radar_maximized_scale", 1024f, 8192f, 512f, format: Qu);
        var maxScaleRow = Ui.Row("Scale:", maxScale);
        box.AddChild(maxScaleRow);
        Dependent.BindNot(maxScaleRow, AlphaCvar, 0);

        // QC: mixedslider "hud_panel_radar_maximized_zoommode" — Zoomed in/out/Always/Never.
        var maxZoom = Widgets.TextSlider("hud_panel_radar_maximized_zoommode")
            .Add("Zoomed in", 0).Add("Zoomed out", 1).Add("Always zoomed", 2).Add("Never zoomed", 3);
        var maxZoomRow = Ui.Row("Zoom mode:", maxZoom);
        box.AddChild(maxZoomRow);
        Dependent.BindNot(maxZoomRow, AlphaCvar, 0);

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    /// <summary>QC slider formatString "%": show a 0..1 alpha as a percentage.</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";

    /// <summary>QC slider formatString "%s qu": show the raw value followed by the Quake-unit suffix.</summary>
    private static string Qu(float v) => $"{Mathf.RoundToInt(v)} qu";
}
