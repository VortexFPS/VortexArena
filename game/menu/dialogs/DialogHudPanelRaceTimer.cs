namespace VortexArena.Game.Menu;

/// <summary>
/// "Race Timer Panel" — <c>XonoticHUDRaceTimerDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_racetimer.qc) on its
/// 15.5 x 4 grid: only the shared enable and background rows.
/// </summary>
public partial class DialogHudPanelRaceTimer : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Race Timer Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "racetimer";

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);
    }
}
