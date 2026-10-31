using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// "StrafeHUD Panel" — <c>XonoticHUDStrafeHUDDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_strafehud.qc) on its
/// 23 x 4 grid: the four-way enable as a full-width mixed slider, the shared background rows, then centring, style
/// and range; the strafe bar's three colours and opacities beside the angle indicator's three colours and one
/// opacity; the switch / W-turn / best-angle indicators with their colours and opacities; demo mode and
/// "Reset colors" (<c>StrafeHUD_ColorReset</c>).
/// </summary>
public partial class DialogHudPanelStrafeHUD : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("StrafeHUD Panel");
    public override float QcRows => 23;

    // StrafeHUD_ColorReset resets exactly these eight colour cvars (not hud_panel_strafehud_wturn_color).
    private static readonly string[] ResetColorCvars =
    {
        "hud_panel_strafehud_bar_accel_color",
        "hud_panel_strafehud_bar_neutral_color",
        "hud_panel_strafehud_bar_overturn_color",
        "hud_panel_strafehud_angle_accel_color",
        "hud_panel_strafehud_angle_neutral_color",
        "hud_panel_strafehud_angle_overturn_color",
        "hud_panel_strafehud_switch_color",
        "hud_panel_strafehud_bestangle_color",
    };

    protected override void Fill(QcGrid me)
    {
        const string panelname = "strafehud";
        float vs = 1.25f * QcLook.DefaultValueSpace;
        var mode = new QcRadioGroup();

        me.TR();
            me.TD(1, 4, Qc.Mixed("hud_panel_strafehud")
                .Add("Disable", 0)
                .Add("Enable", 1)
                .Add("Enable even observing", 2)
                .Add("Enable only in Race/CTS", 3)
                .Done(1.5f));

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.2f, Qc.Label("Centered on:"));
                me.TD(1, 2.8f / 2, Qc.Radio(mode, "hud_panel_strafehud_mode", "0", "View angle"));
                me.TD(1, 2.8f / 2, Qc.Radio(mode, "hud_panel_strafehud_mode", "1", "Velocity angle"));
        me.TR();
            me.TD(1, 1.2f, Qc.Label("Style:"));
            me.TD(1, 2.8f, Qc.Mixed("hud_panel_strafehud_style")
                .Add(HudPanelCommon.Z("STYL^Basic"), 0)
                .Add("Status bar", 1)
                .Add("Gradient", 2)
                .Done(1.25f));
        me.TR();
            me.TD(1, 1.2f, Qc.Label("Range:"));
            me.TD(1, 2.8f, Qc.Mixed("hud_panel_strafehud_range", "", "%s°")
                .Add("Dynamic", 0)
                .AddRange(10, 360, 10)
                .Done(13f / 14f));
        me.TR();
            me.TD(1, (4 - 0.05f) / 2, Qc.Label("Strafe bar:"));

            me.TDempty(0.05f);

            me.TD(1, (4 - 0.05f) / 2, Qc.Label("Angle indicator:"));
        me.TR();
            me.TD(1, (4 - 0.25f) / 6, Qc.Label("Neutral:"));
            me.TDempty(0.05f);
            me.TD(1, (4 - 0.25f) / 6, Qc.Label("Good:"));
            me.TDempty(0.05f);
            me.TD(1, (4 - 0.25f) / 6, Qc.Label("Overturn:"));

            me.TDempty(0.05f);

            me.TD(1, (4 - 0.25f) / 6, Qc.Label("Neutral:"));
            me.TDempty(0.05f);
            me.TD(1, (4 - 0.25f) / 6, Qc.Label("Good:"));
            me.TDempty(0.05f);
            me.TD(1, (4 - 0.25f) / 6, Qc.Label("Overturn:"));
        me.TR();
            me.TD(2, (4 - 0.25f) / 6, HudPanelCommon.ColorPicker("hud_panel_strafehud_bar_neutral_color"))
                .DepNot("hud_panel_strafehud_bar_neutral_alpha", 0);
            me.TDempty(0.05f);
            me.TD(2, (4 - 0.25f) / 6, HudPanelCommon.ColorPicker("hud_panel_strafehud_bar_accel_color"))
                .DepNot("hud_panel_strafehud_bar_accel_alpha", 0);
            me.TDempty(0.05f);
            me.TD(2, (4 - 0.25f) / 6, HudPanelCommon.ColorPicker("hud_panel_strafehud_bar_overturn_color"))
                .DepNot("hud_panel_strafehud_bar_overturn_alpha", 0);

            me.TDempty(0.05f);

            me.TD(2, (4 - 0.25f) / 6, HudPanelCommon.ColorPicker("hud_panel_strafehud_angle_neutral_color"))
                .DepNot("hud_panel_strafehud_angle_alpha", 0);
            me.TDempty(0.05f);
            me.TD(2, (4 - 0.25f) / 6, HudPanelCommon.ColorPicker("hud_panel_strafehud_angle_accel_color"))
                .DepNot("hud_panel_strafehud_angle_alpha", 0);
            me.TDempty(0.05f);
            me.TD(2, (4 - 0.25f) / 6, HudPanelCommon.ColorPicker("hud_panel_strafehud_angle_overturn_color"))
                .DepNot("hud_panel_strafehud_angle_alpha", 0);
        me.TR();
        me.TR();
            me.TD(1, (4 - 0.25f) / 6, Qc.Slider(0, 1, 0.1f, "hud_panel_strafehud_bar_neutral_alpha", "", "%", vs));
            me.TDempty(0.05f);
            me.TD(1, (4 - 0.25f) / 6, Qc.Slider(0, 1, 0.1f, "hud_panel_strafehud_bar_accel_alpha", "", "%", vs));
            me.TDempty(0.05f);
            me.TD(1, (4 - 0.25f) / 6, Qc.Slider(0, 1, 0.1f, "hud_panel_strafehud_bar_overturn_alpha", "", "%", vs));

            me.TDempty(0.05f);

            me.TD(1, (4 - 0.05f) / 2, Qc.Slider(0, 1, 0.1f, "hud_panel_strafehud_angle_alpha", "", "%"));
        me.TR();
            me.TD(1, (4 - 0.2f) / 3, Qc.Check(0, "hud_panel_strafehud_switch", "Switch:",
                "Indicator on the angle to aim at when switching strafing direction"));

            me.TDempty(0.1f);

            me.TD(1, (4 - 0.2f) / 3, Qc.Check(0, "hud_panel_strafehud_wturn", "W-turn:",
                "Indicator on the angle to aim at when W-turning to rotate as fast as possible"));

            me.TDempty(0.1f);

            me.TD(1, (4 - 0.2f) / 3, Qc.Check(0, "hud_panel_strafehud_bestangle", "Best angle:",
                "Indicator on the angle to aim at for maximal acceleration"));
        me.TR();
            me.TD(2, (4 - 0.2f) / 3, HudPanelCommon.ColorPicker("hud_panel_strafehud_switch_color"))
                .DepNot("hud_panel_strafehud_switch_alpha", 0).DepAnd("hud_panel_strafehud_switch", 1, 3);

            me.TDempty(0.1f);

            me.TD(2, (4 - 0.2f) / 3, HudPanelCommon.ColorPicker("hud_panel_strafehud_wturn_color"))
                .DepNot("hud_panel_strafehud_wturn_alpha", 0).DepAnd("hud_panel_strafehud_wturn", 1, 3);

            me.TDempty(0.1f);

            me.TD(2, (4 - 0.2f) / 3, HudPanelCommon.ColorPicker("hud_panel_strafehud_bestangle_color"))
                .DepNot("hud_panel_strafehud_bestangle_alpha", 0).DepAnd("hud_panel_strafehud_bestangle", 1, 1);
        me.TR();
        me.TR();
            me.TD(1, (4 - 0.2f) / 3, Qc.Slider(0, 1, 0.1f, "hud_panel_strafehud_switch_alpha", "", "%"))
                .Dep("hud_panel_strafehud_switch", 1, 3);

            me.TDempty(0.1f);

            me.TD(1, (4 - 0.2f) / 3, Qc.Slider(0, 1, 0.1f, "hud_panel_strafehud_wturn_alpha", "", "%"))
                .Dep("hud_panel_strafehud_wturn", 1, 3);

            me.TDempty(0.1f);

            me.TD(1, (4 - 0.2f) / 3, Qc.Slider(0, 1, 0.1f, "hud_panel_strafehud_bestangle_alpha", "", "%"))
                .Dep("hud_panel_strafehud_bestangle", 1, 1);
        me.TR();
            me.TD(1, (4 - 0.1f) / 2, Qc.Check(0, "_hud_panel_strafehud_demo", "Demo mode"));

            me.TDempty(0.1f);

            me.TD(1, (4 - 0.1f) / 2, Qc.Button("Reset colors", ResetColors));
    }

    /// <summary>QC <c>StrafeHUD_ColorReset</c>: cvar_set(name, cvar_defstring(name)) for the eight colour cvars.</summary>
    private static void ResetColors()
    {
        CvarService cvars = MenuState.Cvars;
        foreach (string name in ResetColorCvars)
        {
            cvars.Set(name, cvars.GetDefault(name));
            cvars.MarkArchived(name);
        }
    }
}
