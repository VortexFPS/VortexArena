namespace VortexArena.Game.Menu;

/// <summary>
/// "Radar Panel" — <c>XonoticHUDRadarDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_radar.qc) on its 18 x 4
/// grid: the three-way enable as a full-width mixed slider, the shared background rows, then opacity, rotation,
/// scale and zoom mode, and the same three for the maximized radar — all greyed while the opacity is 0.
/// </summary>
public partial class DialogHudPanelRadar : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Radar Panel");
    public override float QcRows => 18;

    private static CvarMixedSlider RotationSlider(string cvar) => Qc.Mixed(cvar)
        .Add("Forward", 0)
        .Add("West", 1)
        .Add("South", 2)
        .Add("East", 3)
        .Add("North", 4)
        .Done();

    private static CvarMixedSlider ZoomModeSlider(string cvar) => Qc.Mixed(cvar)
        .Add("Zoomed in", 0)
        .Add("Zoomed out", 1)
        .Add("Always zoomed", 2)
        .Add("Never zoomed", 3)
        .Done(1.25f);

    protected override void Fill(QcGrid me)
    {
        const string panelname = "radar";
        const string alpha = "hud_panel_radar_foreground_alpha";

        me.TR();
            me.TD(1, 4, Qc.Mixed("hud_panel_radar")
                .Add("Disable", 0)
                .Add("Enable in team games", 1)
                .Add("Always enable", 2)
                .Done(1.5f));

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Opacity:"));
            me.TD(1, 2.6f, Qc.Slider(0.1f, 1, 0.1f, alpha, "", "%"));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Rotation:")).DepNot(alpha, 0);
                me.TD(1, 2.6f, RotationSlider("hud_panel_radar_rotation")).DepNot(alpha, 0);
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Scale:")).DepNot(alpha, 0);
            me.TD(1, 2.6f, Qc.Slider(1024, 8192, 512, "hud_panel_radar_scale", "", "%s qu")).DepNot(alpha, 0);
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Zoom mode:")).DepNot(alpha, 0);
                me.TD(1, 2.6f, ZoomModeSlider("hud_panel_radar_zoommode")).DepNot(alpha, 0);

        me.TR();
            me.TD(1, 4, Qc.Label("Maximized radar:")).DepNot(alpha, 0);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Rotation:")).DepNot(alpha, 0);
                me.TD(1, 2.6f, RotationSlider("hud_panel_radar_maximized_rotation")).DepNot(alpha, 0);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Scale:")).DepNot(alpha, 0);
            me.TD(1, 2.6f, Qc.Slider(1024, 8192, 512, "hud_panel_radar_maximized_scale", "", "%s qu")).DepNot(alpha, 0);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Zoom mode:")).DepNot(alpha, 0);
                me.TD(1, 2.6f, ZoomModeSlider("hud_panel_radar_maximized_zoommode")).DepNot(alpha, 0);
    }
}
