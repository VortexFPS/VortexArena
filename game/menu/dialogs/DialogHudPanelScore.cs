namespace VortexArena.Game.Menu;

/// <summary>
/// "Score Panel" — <c>XonoticHUDScoreDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_score.qc) on its 15.5 x 4
/// grid: the shared rows, then the rankings choice.
/// </summary>
public partial class DialogHudPanelScore : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Score Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "score";
        var rankings = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Rankings:"));
            me.TD(1, 2.6f / 3, Qc.Radio(rankings, "hud_panel_score_rankings", "0", "Off"));
            me.TD(1, 2.6f / 3, Qc.Radio(rankings, "hud_panel_score_rankings", "1", "And me"));
            me.TD(1, 2.6f / 3, Qc.Radio(rankings, "hud_panel_score_rankings", "2", "Pure"));
    }
}
