namespace VortexArena.Game.Menu;

/// <summary>
/// "Quick Menu Panel" — <c>XonoticHUDQuickMenuDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_quickmenu.qc) on
/// its 15.5 x 4 grid. The panel has no main cvar, so there is no enable row: the shared background rows start half
/// a row down, then text alignment, command translation and the server's quickmenu.
/// </summary>
public partial class DialogHudPanelQuickMenu : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Quick Menu Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "quickmenu";
        var align = new QcRadioGroup();

        // this panel has no main cvar
        //HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 4, Qc.Label("Text alignment:"));
        me.TR();
            me.TDempty(0.2f);
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_quickmenu_align", "0", "Left"));
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_quickmenu_align", "0.5", "Center"));
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_quickmenu_align", "1", "Right"));

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_quickmenu_translatecommands", "Translate commands"));
        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_quickmenu_server_is_default", "Use the server's quickmenu"));
    }
}
