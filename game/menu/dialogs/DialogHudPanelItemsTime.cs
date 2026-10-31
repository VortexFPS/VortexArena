namespace VortexArena.Game.Menu;

/// <summary>
/// "Items Time Panel" — <c>XonoticHUDItemsTimeDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_itemstime.qc) on
/// its 16 x 4 grid: the panel's three-way enable as a full-width mixed slider (in place of the shared checkbox),
/// the shared background rows, then the text/icon ratio, icon alignment, status bar and the hide / size options.
/// </summary>
public partial class DialogHudPanelItemsTime : HudPanelDialog
{
    public override bool QcRightCentered => false; // mainwindow.qc: addItemCentered

    public override string QcTitle => Localization.Tr("Items Time Panel");
    public override float QcRows => 16;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "itemstime";
        var align = new QcRadioGroup();

        me.TR();
            me.TD(1, 4, Qc.Mixed("hud_panel_itemstime")
                .Add("Disable", 0)
                .Add("Enable spectating", 1)
                .Add("Enable even in warmup", 2)
                .Done(1.5f));

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Text/icon ratio:"));
            me.TD(1, 2.6f, Qc.Slider(2, 8, 0.5f, "hud_panel_itemstime_ratio"));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Icon alignment:"));
                me.TD(1, 2.6f / 2, Qc.Radio(align, "hud_panel_itemstime_iconalign", "0", "Left"));
                me.TD(1, 2.6f / 2, Qc.Radio(align, "hud_panel_itemstime_iconalign", "1", "Right"));

        me.TR();
            me.TD(1, 2, Qc.Check(0, "hud_panel_itemstime_progressbar", "Enable status bar"));
            me.TD(1, 2, Qc.Check(0, "hud_panel_itemstime_progressbar_reduced", "Reduced"))
                .Dep("hud_panel_itemstime_progressbar", 1, 1);

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_itemstime_hidespawned", "Hide spawned items"));
        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_itemstime_hidebig", "Hide big armor and health"));
        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_itemstime_dynamicsize", "Dynamic size"));
    }
}
