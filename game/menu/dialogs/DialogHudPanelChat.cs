namespace VortexArena.Game.Menu;

/// <summary>
/// "Chat Panel" — <c>XonoticHUDChatDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_chat.qc) on its 15.5 x 4
/// grid: the shared rows, then the console chat size, chat lifetime and chat beep.
/// </summary>
public partial class DialogHudPanelChat : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Chat Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "chat";

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Chat size:"));
            me.TD(1, 2.6f, Qc.Slider(6, 20, 1, "con_chatsize"));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Chat lifetime:"));
            me.TD(1, 2.6f, Qc.Mixed("con_chattime", "", "s")
                .AddRange(5, 60, 5)
                .AddRange(120, 300, 60)
                .Add(HudPanelCommon.Z("LIFETIME^Infinite"), 0)
                .Done(1.25f));
        me.TR();
            me.TD(1, 4, Qc.Check(0, "con_chatsound", "Chat beep sound"));
    }
}
