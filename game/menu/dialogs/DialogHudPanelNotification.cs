namespace VortexArena.Game.Menu;

/// <summary>
/// "Notification Panel" — <c>XonoticHUDNotificationDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_notification.qc)
/// on its 15.5 x 4 grid (the panel's cvars are <c>hud_panel_notify*</c>): the shared rows, then entry lifetime,
/// fade time and flip.
/// </summary>
public partial class DialogHudPanelNotification : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Notification Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "notify";
        const string time = "hud_panel_notify_time";

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Entry lifetime:"));
            me.TD(1, 2.6f, Qc.Slider(3, 15, 1, time, "", "s", 1.25f * QcLook.DefaultValueSpace));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Entry fadetime:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Mixed("hud_panel_notify_fadetime", "", "S")
                .Add(HudPanelCommon.Z("FADESPEED^Instant"), 0)
                .AddRange(0.5f, 5, 0.5f)
                .Done(1.25f)).DepNot(time, 0);

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_notify_flip", "Flip notify order")).DepNot(time, 0);
    }
}
