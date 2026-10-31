namespace VortexArena.Game.Menu;

/// <summary>
/// "Ammo Panel" — <c>XonoticHUDAmmoDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_ammo.qc) on its 16 x 4 grid:
/// the shared enable and background rows, then only-current, the noncurrent opacity / scale sliders, the icon
/// alignment and the status bar.
/// </summary>
public partial class DialogHudPanelAmmo : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Ammo Panel");
    public override float QcRows => 16;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "ammo";
        var align = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_ammo_onlycurrent", "Show only current ammo type"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.4f, Qc.Label("Noncurrent opacity:")).Dep("hud_panel_ammo_onlycurrent", 0, 0);
            me.TD(1, 2.4f, Qc.Slider(0.1f, 1, 0.1f, "hud_panel_ammo_noncurrent_alpha", "", "%"))
                .Dep("hud_panel_ammo_onlycurrent", 0, 0);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.4f, Qc.Label("Noncurrent scale:"))
                .DepNot("hud_panel_ammo_noncurrent_alpha", 0).DepAnd("hud_panel_ammo_onlycurrent", 0, 0);
            me.TD(1, 2.4f, Qc.Slider(0.1f, 1, 0.1f, "hud_panel_ammo_noncurrent_scale", "", "%"))
                .DepNot("hud_panel_ammo_noncurrent_alpha", 0).DepAnd("hud_panel_ammo_onlycurrent", 0, 0);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Align icon:"));
                me.TD(1, 2.6f / 2, Qc.Radio(align, "hud_panel_ammo_iconalign", "0", "Left"));
                me.TD(1, 2.6f / 2, Qc.Radio(align, "hud_panel_ammo_iconalign", "1", "Right"));

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_ammo_progressbar", "Enable status bar"));
    }
}
