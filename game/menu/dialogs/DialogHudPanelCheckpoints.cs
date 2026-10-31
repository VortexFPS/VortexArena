namespace VortexArena.Game.Menu;

/// <summary>
/// "Checkpoints Panel" — <c>XonoticHUDCheckpointsDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_checkpoints.qc)
/// on its 15.5 x 4 grid: the shared rows, then font scale, flip and text alignment.
/// </summary>
public partial class DialogHudPanelCheckpoints : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Checkpoints Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "checkpoints";
        var align = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Font scale:"));
            me.TD(1, 2.6f, Qc.Slider(0.8f, 2, 0.1f, "hud_panel_checkpoints_fontscale"));
        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_checkpoints_flip", "Flip checkpoint order"));

        me.TR();
            me.TD(1, 4, Qc.Label("Text alignment:"));
        me.TR();
            me.TDempty(0.2f);
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_checkpoints_align", "0", "Left"));
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_checkpoints_align", "0.5", "Center"));
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_checkpoints_align", "1", "Right"));
    }
}
