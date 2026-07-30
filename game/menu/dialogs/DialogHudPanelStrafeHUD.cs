using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "StrafeHUD" panel config dialog — a faithful C# port of <c>XonoticHUDStrafeHUDDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_strafehud.qc). This panel has no plain Enable checkbox; its
/// <c>hud_panel_strafehud</c> cvar is a 4-way mixed slider declared BEFORE the Background group, so we build
/// that enable slider ourselves and call the common block with its Enable row suppressed. Then the dense
/// strafe-specific form: centering mode, style, range, the "Strafe bar" colour+opacity triplet
/// (Neutral/Good/Overturn), the "Angle indicator" colour triplet + single opacity, the Switch / W-turn /
/// Best-angle indicator checkboxes with their colours+opacities, a Demo-mode checkbox, and a "Reset colors"
/// button.
///
/// QC <c>setDependentAND(e, cvar, lo, hi)</c> is the same range test as <c>setDependent</c> (the framework's
/// <see cref="Dependent.Bind"/>), so it is reproduced with Bind. The colour swatches are
/// <see cref="CvarColorButton"/> (no toolkit colour-picker factory; bound to the same <c>"R G B"</c>
/// cvar string). The "Reset colors" button reproduces QC <c>StrafeHUD_ColorReset</c> directly — it restores
/// the eight colour cvars to their defaults via the store's GetDefault (the C# <c>cvar_defstring</c>); it is
/// a real local state change, not routed through a console command.
///
/// FAITHFUL UI NOW: the strafe HUD is drawn by the HUD backend VortexArena hasn't wired up; all cvar bindings are
/// real (they write the shared store the game reads). No live strafe preview is fabricated.
/// </summary>
public partial class DialogHudPanelStrafeHUD : MenuScreen
{
    // QC StrafeHUD_ColorReset resets exactly these eight colour cvars to their default strings. (Note: the QC
    // does NOT reset hud_panel_strafehud_wturn_color — faithfully omitted here too.)
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

    protected override void BuildUi()
    {
        Name = "DialogHudPanelStrafeHUD";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("StrafeHUD Panel"));

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // QC: makeXonoticMixedSlider("hud_panel_strafehud") — Disable(0)/Enable(1)/Enable even observing(2)/
        // Enable only in Race/CTS(3). This IS the panel's enable cvar (replaces the common Enable checkbox).
        var enable = Widgets.TextSlider("hud_panel_strafehud")
            .Add("Disable", 0)
            .Add("Enable", 1)
            .Add("Enable even observing", 2)
            .Add("Enable only in Race/CTS", 3);
        box.AddChild(Ui.Row("Enable:", enable));

        // QC: dialog_hudpanel_main_settings(me, "strafehud") — Background group only (no Enable checkbox).
        HudPanelCommon.BuildCommon(box, "strafehud", includeEnable: false);

        // QC: "Centered on:" + makeXonoticRadioButton(2, "hud_panel_strafehud_mode", "0"/"1", View/Velocity).
        var modeGroup = new ButtonGroup();
        var modeRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        modeRow.AddThemeConstantOverride("separation", 8);
        modeRow.AddChild(Widgets.RadioButton("hud_panel_strafehud_mode", "0", "View angle", modeGroup));
        modeRow.AddChild(Widgets.RadioButton("hud_panel_strafehud_mode", "1", "Velocity angle", modeGroup));
        box.AddChild(Ui.Row("Centered on:", modeRow));

        // QC: makeXonoticMixedSlider("hud_panel_strafehud_style") — Basic(0)/Status bar(1)/Gradient(2).
        var style = Widgets.TextSlider("hud_panel_strafehud_style")
            .Add("Basic", 0).Add("Status bar", 1).Add("Gradient", 2);
        box.AddChild(Ui.Row("Style:", style));

        // QC: makeXonoticMixedSlider("hud_panel_strafehud_range") — "%s°", Dynamic(0) + range 10..360 step 10.
        var range = Widgets.TextSlider("hud_panel_strafehud_range").Add("Dynamic", 0);
        for (int d = 10; d <= 360; d += 10) range.Add($"{d}°", d);
        box.AddChild(Ui.Row("Range:", range));

        box.AddChild(Ui.Spacer(8));

        // ---- Strafe bar: Neutral / Good / Overturn (colour swatch + opacity each) ----
        // QC: makeXonoticColorpickerString per slot, each setDependentNOT(e, "<slot>_alpha", 0); paired
        // makeXonoticSlider(0, 1, 0.1, "<slot>_alpha") with "%" format.
        box.AddChild(Ui.Header("Strafe bar"));
        box.AddChild(ColorAlphaTriplet(
            ("Neutral:", "hud_panel_strafehud_bar_neutral_color", "hud_panel_strafehud_bar_neutral_alpha"),
            ("Good:", "hud_panel_strafehud_bar_accel_color", "hud_panel_strafehud_bar_accel_alpha"),
            ("Overturn:", "hud_panel_strafehud_bar_overturn_color", "hud_panel_strafehud_bar_overturn_alpha")));

        box.AddChild(Ui.Spacer(8));

        // ---- Angle indicator: Neutral / Good / Overturn share ONE opacity (hud_panel_strafehud_angle_alpha) ----
        // QC: three colour pickers each setDependentNOT(e, "hud_panel_strafehud_angle_alpha", 0); a single
        // makeXonoticSlider(0, 1, 0.1, "hud_panel_strafehud_angle_alpha") spans the row.
        box.AddChild(Ui.Header("Angle indicator"));
        box.AddChild(AngleColorTriplet());
        box.AddChild(Ui.Row("Opacity:",
            Widgets.Slider("hud_panel_strafehud_angle_alpha", 0f, 1f, 0.1f, format: Percent)));

        box.AddChild(Ui.Spacer(8));

        // ---- Switch / W-turn / Best angle indicators ----
        // QC: makeXonoticCheckBox_T per indicator (with tooltip); a colour picker setDependentNOT(...,_alpha,0)
        // AND setDependentAND(...,<checkbox>,1,..); an opacity slider setDependent(...,<checkbox>,1,..).
        box.AddChild(IndicatorBlock(
            "hud_panel_strafehud_switch", "Switch:",
            "Indicator on the angle to aim at when switching strafing direction",
            "hud_panel_strafehud_switch_color", "hud_panel_strafehud_switch_alpha", checkboxMax: 3));
        box.AddChild(IndicatorBlock(
            "hud_panel_strafehud_wturn", "W-turn:",
            "Indicator on the angle to aim at when W-turning to rotate as fast as possible",
            "hud_panel_strafehud_wturn_color", "hud_panel_strafehud_wturn_alpha", checkboxMax: 3));
        box.AddChild(IndicatorBlock(
            "hud_panel_strafehud_bestangle", "Best angle:",
            "Indicator on the angle to aim at for maximal acceleration",
            "hud_panel_strafehud_bestangle_color", "hud_panel_strafehud_bestangle_alpha", checkboxMax: 1));

        box.AddChild(Ui.Spacer(8));

        // QC: makeXonoticCheckBox(0, "_hud_panel_strafehud_demo", _("Demo mode")) + a "Reset colors" button
        // whose onClick is StrafeHUD_ColorReset.
        var bottomRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bottomRow.AddThemeConstantOverride("separation", 12);
        bottomRow.AddChild(Widgets.CheckBox("_hud_panel_strafehud_demo", "Demo mode"));
        bottomRow.AddChild(MakeButton("Reset colors", ResetColors));
        box.AddChild(bottomRow);

        // QC HUD-panel dialogs close with Dialog_Close → universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    /// <summary>
    /// One "label: swatch / opacity" trio for the Strafe bar group: three slots, each a colour picker (greyed
    /// out while its own alpha is 0) above its own opacity slider, laid out as a small grid.
    /// </summary>
    private static Control ColorAlphaTriplet(
        (string Label, string Color, string Alpha) a,
        (string Label, string Color, string Alpha) b,
        (string Label, string Color, string Alpha) c)
    {
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 4);

        foreach (var slot in new[] { a, b, c })
            grid.AddChild(Ui.Label(slot.Label));

        foreach (var slot in new[] { a, b, c })
        {
            var picker = new CvarColorButton(slot.Color);
            grid.AddChild(picker);
            Dependent.BindNot(picker, slot.Alpha, 0); // QC setDependentNOT(e, "<slot>_alpha", 0)
        }

        foreach (var slot in new[] { a, b, c })
        {
            // QC: makeXonoticSlider(0, 1, 0.1, "<slot>_alpha"), formatString "%".
            grid.AddChild(Widgets.Slider(slot.Alpha, 0f, 1f, 0.1f, format: Percent));
        }

        return grid;
    }

    /// <summary>
    /// The Angle-indicator colour row: Neutral / Good / Overturn swatches that all share the single opacity
    /// cvar <c>hud_panel_strafehud_angle_alpha</c>; each swatch is greyed out while that shared alpha is 0
    /// (QC setDependentNOT(e, "hud_panel_strafehud_angle_alpha", 0)).
    /// </summary>
    private static Control AngleColorTriplet()
    {
        const string alpha = "hud_panel_strafehud_angle_alpha";
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 4);

        grid.AddChild(Ui.Label("Neutral:"));
        grid.AddChild(Ui.Label("Good:"));
        grid.AddChild(Ui.Label("Overturn:"));

        foreach (string color in new[]
                 {
                     "hud_panel_strafehud_angle_neutral_color",
                     "hud_panel_strafehud_angle_accel_color",
                     "hud_panel_strafehud_angle_overturn_color",
                 })
        {
            var picker = new CvarColorButton(color);
            grid.AddChild(picker);
            Dependent.BindNot(picker, alpha, 0);
        }

        return grid;
    }

    /// <summary>
    /// One Switch/W-turn/Best-angle indicator: an enable checkbox (with the QC tooltip) plus a colour picker
    /// and opacity slider that are active only while the checkbox is on (QC setDependent/setDependentAND on the
    /// checkbox in [1, <paramref name="checkboxMax"/>]) and the colour is additionally greyed while its alpha
    /// is 0 (QC setDependentNOT on the alpha).
    /// </summary>
    private static Control IndicatorBlock(
        string checkboxCvar, string label, string tooltip, string colorCvar, string alphaCvar, int checkboxMax)
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 4);

        // QC: makeXonoticCheckBox_T(0, "<checkbox>", _("<label>"), _("<tooltip>")).
        col.AddChild(Widgets.CheckBox(checkboxCvar, label, tooltip));

        var picker = new CvarColorButton(colorCvar);
        col.AddChild(picker);
        Dependent.BindNot(picker, alphaCvar, 0);                 // QC setDependentNOT(e, "<...>_alpha", 0)
        Dependent.Bind(picker, checkboxCvar, 1, checkboxMax);    // QC setDependentAND(e, "<checkbox>", 1, max)

        // QC: makeXonoticSlider(0, 1, 0.1, "<...>_alpha"), formatString "%"; setDependent on the checkbox.
        var slider = Widgets.Slider(alphaCvar, 0f, 1f, 0.1f, format: Percent);
        col.AddChild(slider);
        Dependent.Bind(slider, checkboxCvar, 1, checkboxMax);

        return col;
    }

    /// <summary>QC <c>StrafeHUD_ColorReset</c>: restore the eight strafe colour cvars to their defaults.</summary>
    private void ResetColors()
    {
        CvarService cvars = MenuState.Cvars;
        foreach (string name in ResetColorCvars)
        {
            // QC: cvar_set(name, cvar_defstring(name)) — GetDefault is the C# cvar_defstring.
            cvars.Set(name, cvars.GetDefault(name));
            cvars.MarkArchived(name);
        }
    }

    /// <summary>Render a 0..1 alpha as a percent (the QC slider's "%" formatString readout).</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}
