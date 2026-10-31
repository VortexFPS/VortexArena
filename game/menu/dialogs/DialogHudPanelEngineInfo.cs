namespace VortexArena.Game.Menu;

/// <summary>
/// "Engine Info Panel" — <c>XonoticHUDEngineInfoDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_engineinfo.qc)
/// on its 15.5 x 4 grid: the shared rows, then the FPS averaging and decimal places.
/// </summary>
public partial class DialogHudPanelEngineInfo : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Engine Info Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "engineinfo";

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 4, Qc.Label("FPS:"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f, Qc.Check(0, "hud_panel_engineinfo_fps_movingaverage", "Use an averaging algorithm"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Decimal places:"));
            me.TD(1, 2.6f, Qc.Slider(0, 2, 1, "hud_panel_engineinfo_fps_decimals"));
    }
}
