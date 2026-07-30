using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Physics HUD panel config — a faithful C# port of <c>XonoticHUDPhysicsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_physics.qc). Configures the physics/speedometer panel (speed +
/// acceleration readout). Panel cvar prefix is <c>hud_panel_physics</c>.
///
/// This panel is the ONE exception to the standard HUD-panel layout: instead of the common
/// <c>dialog_hudpanel_main_checkbox</c> Enable checkbox it declares its OWN enable
/// <c>makeXonoticMixedSlider("hud_panel_physics")</c> with four specific modes (Disable=0 / Enable=1 /
/// "Enable even observing"=2 / "Enable only in Race/CTS"=3) FIRST, then calls
/// <c>dialog_hudpanel_main_settings</c> for the Background group, then its panel-specific rows. This port
/// reproduces that order exactly: the custom enable TextSlider, then <see cref="HudPanelCommon.BuildCommon"/>
/// (Background group only — no extra Enable row), then the physics-specific rows (flip positions, status bar
/// mode + alignment, the speed group, the acceleration group). Every control binds the same
/// <c>hud_panel_physics_*</c> cvar; <c>setDependent</c> grey-outs are reproduced with <see cref="Dependent"/>.
///
/// FAITHFUL UI NOW: the <c>hud_panel_physics_*</c> cvars drive the in-game HUD renderer/editor VortexArena has not
/// wired yet, but the bindings are REAL (they write the shared <see cref="MenuState.Cvars"/> store). No
/// apply/command button exists (just Back). Two approximations are noted inline: the "Show speed unit"
/// checkbox uses QC <c>setDependent</c>+<c>setDependentOR</c> (enabled if topspeed==0 OR jumpspeed==0); the
/// framework <see cref="Dependent"/> tests a single cvar, so only the dominant first branch is bound. The QC
/// "Full status bar at:" input rows are commented out in the source and so are absent here too.
/// </summary>
public partial class DialogHudPanelPhysics : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelPhysics"; // QC ATTRIB name "HUDphysics"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Physics"));

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

        // --- panel enable: this panel's OWN mixedslider (NOT the common Enable checkbox), with 4 modes ---
        // makeXonoticMixedSlider("hud_panel_physics"): Disable=0/Enable=1/Enable even observing=2/Race/CTS=3.
        box.AddChild(Ui.Row("Enable:",
            Widgets.TextSlider("hud_panel_physics", "Enable this panel")
                .Add("Disable", 0).Add("Enable", 1)
                .Add("Enable even observing", 2).Add("Enable only in Race/CTS", 3)));

        // --- common HUD-panel block: Background group only (QC then calls dialog_hudpanel_main_settings) ---
        HudPanelCommon.BuildCommon(box, "physics", includeEnable: false);

        // --- panel-specific rows ---

        // Flip speed/acceleration positions: makeXonoticCheckBox(0, hud_panel_physics_flip, ...).
        box.AddChild(Widgets.CheckBox("hud_panel_physics_flip", "Flip speed/acceleration positions"));

        // Status bar: makeXonoticMixedSlider(hud_panel_physics_progressbar) — None=0/Speed=2/Acceleration=3/Both=1.
        box.AddChild(Ui.Row("Status bar:",
            Widgets.TextSlider("hud_panel_physics_progressbar")
                .Add("None", 0).Add("Speed", 2).Add("Acceleration", 3).Add("Both", 1)));

        // Status bar alignment: a 4-way radio (Left=0/Right=1/Inward=2/Outward=3) on hud_panel_physics_baralign,
        // under a label, all dependent on progressbar in [1,3] (QC setDependent(progressbar,1,3)).
        var barLabel = Ui.Label("Status bar alignment:");
        box.AddChild(barLabel);
        Dependent.Bind(barLabel, "hud_panel_physics_progressbar", 1, 3);

        var barGroup = new ButtonGroup();
        var barRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        barRow.AddThemeConstantOverride("separation", 8);
        barRow.AddChild(Widgets.RadioButton("hud_panel_physics_baralign", "0", "Left", barGroup));
        barRow.AddChild(Widgets.RadioButton("hud_panel_physics_baralign", "1", "Right", barGroup));
        barRow.AddChild(Widgets.RadioButton("hud_panel_physics_baralign", "2", "Inward", barGroup));
        barRow.AddChild(Widgets.RadioButton("hud_panel_physics_baralign", "3", "Outward", barGroup));
        box.AddChild(barRow);
        Dependent.Bind(barRow, "hud_panel_physics_progressbar", 1, 3);

        box.AddChild(Ui.Spacer(6));

        // Speed group.
        box.AddChild(Ui.Header("Speed"));

        // Include vertical speed: makeXonoticCheckBox(0, hud_panel_physics_speed_vertical, ...).
        box.AddChild(Widgets.CheckBox("hud_panel_physics_speed_vertical", "Include vertical speed"));

        // Top speed (checkbox) + its hold time slider (0.5..5 step 0.5), the slider dependent on the checkbox.
        box.AddChild(Widgets.CheckBox("hud_panel_physics_topspeed", "Top speed"));
        var topRow = Ui.Row("Top speed time:",
            Widgets.Slider("hud_panel_physics_topspeed_time", 0.5f, 5f, 0.5f, format: v => $"{v:0.#}s"));
        box.AddChild(topRow);
        Dependent.Bind(topRow, "hud_panel_physics_topspeed", 1, 1);

        // Jump speed (checkbox) + its hold time slider (0.5..5 step 0.5), the slider dependent on the checkbox.
        box.AddChild(Widgets.CheckBox("hud_panel_physics_jumpspeed", "Jump speed"));
        var jumpRow = Ui.Row("Jump speed time:",
            Widgets.Slider("hud_panel_physics_jumpspeed_time", 0.5f, 5f, 0.5f, format: v => $"{v:0.#}s"));
        box.AddChild(jumpRow);
        Dependent.Bind(jumpRow, "hud_panel_physics_jumpspeed", 1, 1);

        // Show speed unit: QC setDependent(topspeed,0,0) + setDependentOR(jumpspeed,0,0) — enabled while
        // (topspeed == 0) OR (jumpspeed == 0). The framework Dependent tests a single cvar, so we bind the
        // dominant first branch (topspeed==0); the OR jumpspeed==0 branch is approximated (noted).
        var speedUnit = Widgets.CheckBox("hud_panel_physics_speed_unit_show", "Show speed unit");
        box.AddChild(speedUnit);
        Dependent.Bind(speedUnit, "hud_panel_physics_topspeed", 0, 0);

        box.AddChild(Ui.Spacer(6));

        // Acceleration group.
        box.AddChild(Ui.Header("Acceleration"));

        // Include vertical acceleration: makeXonoticCheckBox(0, hud_panel_physics_acceleration_vertical, ...).
        box.AddChild(Widgets.CheckBox("hud_panel_physics_acceleration_vertical", "Include vertical acceleration"));

        // (QC "Full status bar at:" input rows for speed_max/acceleration_max are commented out in the source.)

        // QC dialog has no apply/command button — only the universal Back closes it.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

}
