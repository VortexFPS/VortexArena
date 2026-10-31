namespace VortexArena.Game.Menu;

/// <summary>
/// "Health/Armor Panel" — <c>XonoticHUDHealthArmorDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_healtharmor.qc)
/// on its 18 x 4 grid: the shared rows, then combine / flip, the status bar and its alignment, and the icon
/// alignment.
/// </summary>
public partial class DialogHudPanelHealthArmor : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Health/Armor Panel");
    public override float QcRows => 18;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "healtharmor";
        const string bar = "hud_panel_healtharmor_progressbar";
        var barAlign = new QcRadioGroup();
        var iconAlign = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_healtharmor_combined", "Combine health and armor"));
        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_healtharmor_flip", "Flip health and armor positions"))
                .Dep("hud_panel_healtharmor_combined", 0, 0);

        me.TR();
            me.TD(1, 4, Qc.Check(0, bar, "Enable status bar"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f, Qc.Label("Status bar alignment:")).Dep(bar, 1, 1);
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_healtharmor_baralign", "0", "Left")).Dep(bar, 1, 1);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_healtharmor_baralign", "1", "Right")).Dep(bar, 1, 1);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_healtharmor_baralign", "2", "Inward")).Dep(bar, 1, 1);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_healtharmor_baralign", "3", "Outward")).Dep(bar, 1, 1);

        me.TR();
            me.TD(1, 4, Qc.Label("Icon alignment:"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 0.95f, Qc.Radio(iconAlign, "hud_panel_healtharmor_iconalign", "0", "Left"));
            me.TD(1, 0.95f, Qc.Radio(iconAlign, "hud_panel_healtharmor_iconalign", "1", "Right"));
            me.TD(1, 0.95f, Qc.Radio(iconAlign, "hud_panel_healtharmor_iconalign", "2", "Inward"));
            me.TD(1, 0.95f, Qc.Radio(iconAlign, "hud_panel_healtharmor_iconalign", "3", "Outward"));
    }
}
