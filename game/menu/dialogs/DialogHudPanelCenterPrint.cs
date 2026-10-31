namespace VortexArena.Game.Menu;

/// <summary>
/// "Centerprint Panel" — <c>XonoticHUDCenterprintDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_centerprint.qc)
/// on its 18 x 4 grid: the shared rows, then message duration, fade time, the three font scales, the flip and the
/// text alignment — everything after the duration greyed while it is 0.
/// </summary>
public partial class DialogHudPanelCenterPrint : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Centerprint Panel");
    public override float QcRows => 18;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "centerprint";
        const string time = "hud_panel_centerprint_time";
        float vs = 1.25f * QcLook.DefaultValueSpace;
        var align = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Message duration:"));
            me.TD(1, 2.6f, Qc.Slider(1, 10, 1, time, "", "s", vs));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Fade time:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Mixed("hud_panel_centerprint_fade_out", "", "S")
                .Add(HudPanelCommon.Z("FADESPEED^Instant"), 0)
                .AddRange(0.05f, 1, 0.05f)
                .Done(1.25f)).DepNot(time, 0);
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Font scale:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Slider(0.5f, 2, 0.1f, "hud_panel_centerprint_fontscale")).DepNot(time, 0);
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Bold font scale:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Slider(0.5f, 3, 0.1f, "hud_panel_centerprint_fontscale_bold")).DepNot(time, 0);
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Title font scale:")).DepNot(time, 0);
            me.TD(1, 2.6f, Qc.Slider(0.5f, 4, 0.1f, "hud_panel_centerprint_fontscale_title")).DepNot(time, 0);
        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_centerprint_flip", "Flip messages order")).DepNot(time, 0);

        me.TR();
            me.TD(1, 4, Qc.Label("Text alignment:")).DepNot(time, 0);
        me.TR();
            me.TDempty(0.2f);
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_centerprint_align", "0", "Left")).DepNot(time, 0);
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_centerprint_align", "0.5", "Center")).DepNot(time, 0);
                me.TD(1, 3.8f / 3, Qc.Radio(align, "hud_panel_centerprint_align", "1", "Right")).DepNot(time, 0);
    }
}
