namespace VortexArena.Game.Menu;

/// <summary>
/// "Weapons Panel" — <c>XonoticHUDWeaponsDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_weapons.qc) on its
/// 22 x 4 grid: the shared rows, then the fade-out timeout and effect, only-owned with the noncurrent opacity /
/// scale, the label kind and scale, ammo and accuracy, and the ammo bar's opacity and colour.
/// </summary>
public partial class DialogHudPanelWeapons : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Weapons Panel");
    public override float QcRows => 22;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "weapons";
        const string timeout = "hud_panel_" + panelname + "_timeout";
        const string label = "hud_panel_weapons_label";
        var labels = new QcRadioGroup();

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Fade out after:"));
                me.TD(1, 2.6f, Qc.Mixed(timeout, "", "s")
                    .Add("Never", 0)
                    .AddRange(1, 10, 1)
                    .Done(1.2f));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Fade effect:")).DepNot(timeout, 0);
                me.TD(1, 2.6f, Qc.Mixed("hud_panel_" + panelname + "_timeout_effect")
                    .Add(HudPanelCommon.Z("EF^None"), 0)
                    .Add("Opacity", 1)
                    .Add("Slide", 2)
                    .Add(HudPanelCommon.Z("EF^Both"), 3)
                    .Done()).DepNot(timeout, 0);

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_weapons_onlyowned", "Show only owned weapons"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.4f, Qc.Label("Noncurrent opacity:")).Dep("hud_panel_weapons_onlyowned", 0, 1);
            me.TD(1, 2.4f, Qc.Slider(0.1f, 1, 0.1f, "hud_panel_weapons_noncurrent_alpha", "", "%"))
                .Dep("hud_panel_weapons_onlyowned", 0, 1);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.4f, Qc.Label("Noncurrent scale:"))
                .DepNot("hud_panel_weapons_noncurrent_alpha", 0).DepAnd("hud_panel_weapons_onlyowned", 0, 1);
            me.TD(1, 2.4f, Qc.Slider(0.1f, 1, 0.1f, "hud_panel_weapons_noncurrent_scale", "", "%"))
                .DepNot("hud_panel_weapons_noncurrent_alpha", 0).DepAnd("hud_panel_weapons_onlyowned", 0, 1);

        me.TR();
            me.TD(1, 1.4f, Qc.Check(0, label, "Show label:"));
                me.TD(1, 2.6f / 3, Qc.Radio(labels, label, "1", "Number")).Dep(label, 1, 3);
                me.TD(1, 2.6f / 3, Qc.Radio(labels, label, "2", "Bind")).Dep(label, 1, 3);
                me.TD(1, 2.6f / 3, Qc.Radio(labels, label, "3", "Name")).Dep(label, 1, 3);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Scale:")).Dep(label, 1, 3);
            me.TD(1, 2.6f, Qc.Slider(0.1f, 1, 0.05f, "hud_panel_weapons_label_scale", "", "%sx")).Dep(label, 1, 3);

        me.TR();
            me.TD(1, 2, Qc.Check(0, "hud_panel_weapons_ammo", "Show Ammo"));
            me.TD(1, 2, Qc.Check(0, "hud_panel_weapons_accuracy", "Show Accuracy"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.4f, Qc.Label("Ammo bar opacity:")).Dep("hud_panel_weapons_ammo", 1, 1);
            me.TD(1, 2.4f, Qc.Slider(0.1f, 1, 0.1f, "hud_panel_weapons_ammo_alpha", "", "%"))
                .Dep("hud_panel_weapons_ammo", 1, 1);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Ammo bar color:"))
                .DepNot("hud_panel_weapons_ammo_alpha", 0).DepAnd("hud_panel_weapons_ammo", 1, 1);
            me.TD(2, 2.4f, HudPanelCommon.ColorPicker("hud_panel_weapons_ammo_color"))
                .DepNot("hud_panel_weapons_ammo_alpha", 0).DepAnd("hud_panel_weapons_ammo", 1, 1);
            me.TR();
    }
}
