namespace VortexArena.Game.Menu;

/// <summary>
/// "Pickup Panel" — <c>XonoticHUDPickupDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_pickup.qc) on its
/// 15.5 x 4 grid: the shared rows, then message duration, fade time, icon size and when to show the timer — all
/// after the duration greyed while it is 0.
/// </summary>
public partial class DialogHudPanelPickup : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Pickup Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "pickup";
        const string time = "hud_panel_pickup_time";
        var timer = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Message duration:"));
            me.TD(1, 2.6f, Qc.Slider(1, 5, 1, time, "", "s", 1.25f * QcLook.DefaultValueSpace));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Fade time:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Mixed("hud_panel_pickup_fade_out", "", "S")
                .Add(HudPanelCommon.Z("FADESPEED^Instant"), 0)
                .AddRange(0.05f, 1, 0.05f)
                .Done(1.25f)).DepNot(time, 0);
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Icon size scale:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Slider(1, 3, 0.1f, "hud_panel_pickup_iconsize", "", "%sx")).DepNot(time, 0);

        me.TR();
            me.TD(1, 4, Qc.Label("Show timer:")).DepNot(time, 0);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f / 3, Qc.Radio(timer, "hud_panel_pickup_showtimer", "0", "Never")).DepNot(time, 0);
            me.TD(1, 3.8f / 3, Qc.Radio(timer, "hud_panel_pickup_showtimer", "2", "Spectating")).DepNot(time, 0);
            me.TD(1, 3.8f / 3, Qc.Radio(timer, "hud_panel_pickup_showtimer", "1", "Always")).DepNot(time, 0);
    }
}
