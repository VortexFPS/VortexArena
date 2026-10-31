namespace VortexArena.Game.Menu;

/// <summary>
/// "Physics Panel" — <c>XonoticHUDPhysicsDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_physics.qc) on its
/// 19 x 4 grid: the four-way enable as a full-width mixed slider, the shared background rows, then flip, the status
/// bar and its alignment, the speed options (vertical, top / jump speed with their times, unit) and vertical
/// acceleration. The QC's commented-out "Full status bar at:" rows stay out.
/// </summary>
public partial class DialogHudPanelPhysics : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Physics Panel");
    public override float QcRows => 19;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "physics";
        const string bar = "hud_panel_physics_progressbar";
        float vs = 1.5f * QcLook.DefaultValueSpace;
        var barAlign = new QcRadioGroup();

        me.TR();
            me.TD(1, 4, Qc.Mixed("hud_panel_physics")
                .Add("Disable", 0)
                .Add("Enable", 1)
                .Add("Enable even observing", 2)
                .Add("Enable only in Race/CTS", 3)
                .Done(1.5f));

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_physics_flip", "Flip speed/acceleration positions"));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Status bar"));
            me.TD(1, 2.6f, Qc.Mixed(bar)
                .Add("None", 0)
                .Add("Speed", 2)
                .Add("Acceleration", 3)
                .Add("Both", 1)
                .Done());
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f, Qc.Label("Status bar alignment:")).Dep(bar, 1, 3);
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_physics_baralign", "0", "Left")).Dep(bar, 1, 3);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_physics_baralign", "1", "Right")).Dep(bar, 1, 3);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_physics_baralign", "2", "Inward")).Dep(bar, 1, 3);
            me.TD(1, 3.6f / 4, Qc.Radio(barAlign, "hud_panel_physics_baralign", "3", "Outward")).Dep(bar, 1, 3);

        // speed
        me.TR();
            me.TD(1, 1, Qc.Label("Speed:"));
            me.TD(1, 3, Qc.Check(0, "hud_panel_physics_speed_vertical", "Include vertical speed"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f / 2, Qc.Check(0, "hud_panel_physics_topspeed", "Top speed"));
            me.TD(1, 3.8f / 2, Qc.Slider(0.5f, 5, 0.5f, "hud_panel_physics_topspeed_time", "", "S", vs))
                .Dep("hud_panel_physics_topspeed", 1, 1);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f / 2, Qc.Check(0, "hud_panel_physics_jumpspeed", "Jump speed"));
            me.TD(1, 3.8f / 2, Qc.Slider(0.5f, 5, 0.5f, "hud_panel_physics_jumpspeed_time", "", "S", vs))
                .Dep("hud_panel_physics_jumpspeed", 1, 1);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 3.8f, Qc.Check(0, "hud_panel_physics_speed_unit_show", "Show speed unit"))
                .Dep("hud_panel_physics_topspeed", 0, 0).DepOr("hud_panel_physics_jumpspeed", 0, 0);

        // acceleration
        me.TR();
            me.TD(1, 1, Qc.Label("Acceleration:"));
            me.TD(1, 3, Qc.Check(0, "hud_panel_physics_acceleration_vertical", "Include vertical acceleration"));
    }
}
