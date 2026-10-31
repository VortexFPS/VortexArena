namespace VortexArena.Game.Menu;

/// <summary>
/// "Info Messages Panel" — <c>XonoticHUDInfoMessagesDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_infomessages.qc) on its 15.5 x 4 grid: the shared rows, then message
/// duration, fade time and text alignment.
/// </summary>
public partial class DialogHudPanelInfoMessages : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Info Messages Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "infomessages";
        const string time = "hud_panel_infomessages_group_time";
        var flip = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Message duration:"));
            me.TD(1, 2.6f, Qc.Slider(1, 10, 1, time, "", "s", 1.25f * QcLook.DefaultValueSpace));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Fade time:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Mixed("hud_panel_infomessages_group_fadetime", "", "S")
                .Add(HudPanelCommon.Z("FADESPEED^Instant"), 0)
                .AddRange(0.05f, 1, 0.05f)
                .Done(1.25f)).DepNot(time, 0);
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Text alignment:")).DepNot(time, 0);
                me.TD(1, 2.6f / 2, Qc.Radio(flip, "hud_panel_infomessages_flip", "0", "Left")).DepNot(time, 0);
                me.TD(1, 2.6f / 2, Qc.Radio(flip, "hud_panel_infomessages_flip", "1", "Right")).DepNot(time, 0);
    }
}
