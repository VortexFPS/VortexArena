namespace VortexArena.Game.Menu;

/// <summary>
/// "Powerups Panel" — <c>XonoticHUDPowerupsDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_powerups.qc) on its
/// 16 x 4 grid: the shared rows, then the status bar and its alignment, and the icon alignment.
/// </summary>
public partial class DialogHudPanelPowerups : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Powerups Panel");
    public override float QcRows => 16;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "powerups";
        const string bar = "hud_panel_powerups_progressbar";
        var barAlign = new QcRadioGroup();
        var iconAlign = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 4, Qc.Check(0, bar, "Enable status bar"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f, Qc.Label("Status bar alignment:")).Dep(bar, 1, 1);
        me.TR();
            me.TDempty(0.4f);
                me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_powerups_baralign", "0", "Left")).Dep(bar, 1, 1);
                me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_powerups_baralign", "1", "Right")).Dep(bar, 1, 1);
                me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_powerups_baralign", "2", "Inward")).Dep(bar, 1, 1);
                me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_powerups_baralign", "3", "Outward")).Dep(bar, 1, 1);

        me.TR();
            me.TD(1, 4, Qc.Label("Icon alignment:"));
        me.TR();
            me.TDempty(0.2f);
                me.TD(1, 3.8f / 4, Qc.Radio(iconAlign, "hud_panel_powerups_iconalign", "0", "Left"));
                me.TD(1, 3.8f / 4, Qc.Radio(iconAlign, "hud_panel_powerups_iconalign", "1", "Right"));
                me.TD(1, 3.8f / 4, Qc.Radio(iconAlign, "hud_panel_powerups_iconalign", "2", "Inward"));
                me.TD(1, 3.8f / 4, Qc.Radio(iconAlign, "hud_panel_powerups_iconalign", "3", "Outward"));
    }
}
