namespace VortexArena.Game.Menu;

/// <summary>
/// "Timer Panel" — <c>XonoticHUDTimerDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_timer.qc) on its 15.5 x 4
/// grid: the shared rows, then elapsed time and the secondary timer.
/// </summary>
public partial class DialogHudPanelTimer : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Timer Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "timer";
        var secondary = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_timer_increment", "Show elapsed time"));

        me.TR();
            me.TD(1, 4, Qc.Label("Secondary timer:"));
        me.TR();
            me.TDempty(0.2f);
                me.TD(1, 3.8f / 3, Qc.Radio(secondary, "hud_panel_timer_secondary", "0", "Disable"));
                me.TD(1, 3.8f / 3, Qc.Radio(secondary, "hud_panel_timer_secondary", "1", "Enable"));
                me.TD(1, 3.8f / 3, Qc.Radio(secondary, "hud_panel_timer_secondary", "2", "Swapped"));
    }
}
