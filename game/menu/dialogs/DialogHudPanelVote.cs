namespace VortexArena.Game.Menu;

/// <summary>
/// "Vote Panel" — <c>XonoticHUDVoteDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_vote.qc) on its 15.5 x 4 grid:
/// the shared rows, then the opacity after voting.
/// </summary>
public partial class DialogHudPanelVote : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Vote Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "vote";

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Opacity after voting:"));
            me.TD(1, 2.6f, Qc.Slider(0.1f, 1, 0.1f, "hud_panel_vote_alreadyvoted_alpha", "", "%"));
    }
}
