using System.Globalization;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Weapons" panel configuration dialog — a faithful C# port of <c>XonoticHUDWeaponsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_weapons.qc). It opens with the common HUD-panel block (Enable mode +
/// Background group from <c>dialog_hudpanel_main_*</c>) then declares the panel-specific rows: fade-out timeout
/// + fade effect, show-only-owned with its noncurrent opacity/scale, the weapon label mode (checkbox + radio)
/// and label scale, the ammo/accuracy checkboxes, and the ammo bar opacity + color.
///
/// FAITHFUL UI NOW: drives the real <c>hud_panel_weapons_*</c> cvars (the shared store the in-game HUD reads).
/// Two QC widgets have no direct toolkit factory:
///   * <c>makeXonoticMixedSlider</c> (timeout / timeout_effect) — a mixed text+range chooser. The effect one is
///     purely discrete (4 named values) so it maps to a <see cref="Widgets.TextSlider"/>; the timeout one mixes
///     "Never" with the 1..10 range, reproduced as a TextSlider over Never + each integer second (noted).
///   * <c>makeXonoticColorpickerString</c> (ammo bar color) — a string-RGB picker, built via the shared
///     <see cref="Widgets.ColorButton"/> (<see cref="CvarColorButton"/>) bound to the same "r g b" string cvar.
/// </summary>
public partial class DialogHudPanelWeapons : MenuScreen
{
    private const string Panel = "weapons";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelWeapons"; // QC ATTRIB name "HUDweapons"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Weapons Panel"));

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

        // --- Common HUD-panel block (dialog_hudpanel_main_checkbox + dialog_hudpanel_main_settings) ---------
        HudPanelCommon.BuildCommon(box, Panel);

        // --- Panel-specific rows (XonoticHUDWeaponsDialog_fill) ----------------------------------------------
        box.AddChild(Ui.Spacer());

        // QC: "Fade out after:" mixedslider hud_panel_weapons_timeout, formatString "s", addText("Never",0) +
        // addRange(1,10,1). Mixed range -> a TextSlider over Never + each integer second.
        var timeout = Widgets.TextSlider("hud_panel_weapons_timeout", "Time after which the panel fades out");
        timeout.Add("Never", 0);
        for (int s = 1; s <= 10; s++) timeout.Add($"{s}s", s);
        box.AddChild(Ui.Row("Fade out after:", timeout));

        // QC: "Fade effect:" mixedslider hud_panel_weapons_timeout_effect (None=0/Opacity=1/Slide=2/Both=3),
        // setDependentNOT(hud_panel_weapons_timeout, 0) -> enabled while a timeout is set.
        var effect = Widgets.TextSlider("hud_panel_weapons_timeout_effect", "Effect used when the panel fades out")
            .Add("None", 0).Add("Opacity", 1).Add("Slide", 2).Add("Both", 3);
        var effectRow = Ui.Row("Fade effect:", effect);
        box.AddChild(effectRow);
        Dependent.BindNot(effectRow, "hud_panel_weapons_timeout", 0); // setDependentNOT(...,0)

        box.AddChild(Ui.Spacer());

        // QC: checkbox hud_panel_weapons_onlyowned "Show only owned weapons".
        box.AddChild(Widgets.CheckBox("hud_panel_weapons_onlyowned", "Show only owned weapons"));

        // QC: "Noncurrent opacity:" slider hud_panel_weapons_noncurrent_alpha (0.1..1 step 0.1, "%"),
        // setDependent(hud_panel_weapons_onlyowned, 0, 1) -> always enabled (0 or 1) in practice.
        var ncAlpha = Widgets.Slider("hud_panel_weapons_noncurrent_alpha", 0.1f, 1f, 0.1f, format: HudPanelCommon.Percent);
        var ncAlphaRow = Ui.Row("Noncurrent opacity:", ncAlpha);
        box.AddChild(ncAlphaRow);
        Dependent.Bind(ncAlphaRow, "hud_panel_weapons_onlyowned", 0, 1); // setDependent(...,0,1)

        // QC: "Noncurrent scale:" slider hud_panel_weapons_noncurrent_scale, setDependentNOT(noncurrent_alpha,0)
        // AND setDependentAND(onlyowned, 0, 1). Single-test framework -> keep the dominant noncurrent_alpha != 0
        // gate (the onlyowned [0,1] half is always satisfied); the AND pair is approximated (noted).
        var ncScale = Widgets.Slider("hud_panel_weapons_noncurrent_scale", 0.1f, 1f, 0.1f, format: HudPanelCommon.Percent);
        var ncScaleRow = Ui.Row("Noncurrent scale:", ncScale);
        box.AddChild(ncScaleRow);
        Dependent.BindNot(ncScaleRow, "hud_panel_weapons_noncurrent_alpha", 0); // dominant setDependentNOT

        box.AddChild(Ui.Spacer());

        // QC: checkbox hud_panel_weapons_label "Show label:" + radio group 2 over the SAME cvar
        // (Number=1/Bind=2/Name=3), the radios setDependent(hud_panel_weapons_label, 1, 3). The checkbox toggles
        // the cvar between 0 (off) and its nonzero label mode — same dual-use pattern as cl_hitsound in Audio.
        box.AddChild(Widgets.CheckBox("hud_panel_weapons_label", "Show label:"));
        var labelGroup = new ButtonGroup();
        var labelRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        labelRow.AddThemeConstantOverride("separation", 8);
        labelRow.AddChild(Widgets.RadioButton("hud_panel_weapons_label", "1", "Number", labelGroup));
        labelRow.AddChild(Widgets.RadioButton("hud_panel_weapons_label", "2", "Bind", labelGroup));
        labelRow.AddChild(Widgets.RadioButton("hud_panel_weapons_label", "3", "Name", labelGroup));
        box.AddChild(labelRow);
        Dependent.Bind(labelRow, "hud_panel_weapons_label", 1, 3); // setDependent(...,1,3)

        // QC: "Scale:" slider hud_panel_weapons_label_scale (0.1..1 step 0.05, formatString "%sx"),
        // setDependent(hud_panel_weapons_label, 1, 3).
        var labelScale = Widgets.Slider("hud_panel_weapons_label_scale", 0.1f, 1f, 0.05f,
            format: v => $"{v.ToString("0.##", CultureInfo.InvariantCulture)}x"); // QC "%sx"
        var labelScaleRow = Ui.Row("Scale:", labelScale);
        box.AddChild(labelScaleRow);
        Dependent.Bind(labelScaleRow, "hud_panel_weapons_label", 1, 3); // setDependent(...,1,3)

        box.AddChild(Ui.Spacer());

        // QC: two checkboxes on one row — hud_panel_weapons_ammo "Show Ammo" / hud_panel_weapons_accuracy
        // "Show Accuracy".
        var ammoAccRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ammoAccRow.AddThemeConstantOverride("separation", 8);
        ammoAccRow.AddChild(Widgets.CheckBox("hud_panel_weapons_ammo", "Show Ammo"));
        ammoAccRow.AddChild(Widgets.CheckBox("hud_panel_weapons_accuracy", "Show Accuracy"));
        box.AddChild(ammoAccRow);

        // QC: "Ammo bar opacity:" slider hud_panel_weapons_ammo_alpha (0.1..1 step 0.1, "%"),
        // setDependent(hud_panel_weapons_ammo, 1, 1).
        var ammoAlpha = Widgets.Slider("hud_panel_weapons_ammo_alpha", 0.1f, 1f, 0.1f, format: HudPanelCommon.Percent);
        var ammoAlphaRow = Ui.Row("Ammo bar opacity:", ammoAlpha);
        box.AddChild(ammoAlphaRow);
        Dependent.Bind(ammoAlphaRow, "hud_panel_weapons_ammo", 1, 1); // setDependent(...,1,1)

        // QC: "Ammo bar color:" colorpickerString hud_panel_weapons_ammo_color, setDependentNOT(ammo_alpha, 0)
        // AND setDependentAND(ammo, 1, 1). Keep the dominant ammo==1 gate (matching the opacity row); the
        // AND/NOT pair is approximated (noted). No toolkit factory -> ColorPickerButton on the same string cvar.
        var ammoColor = Widgets.ColorButton("hud_panel_weapons_ammo_color");
        var ammoColorRow = Ui.Row("Ammo bar color:", ammoColor);
        box.AddChild(ammoColorRow);
        Dependent.Bind(ammoColorRow, "hud_panel_weapons_ammo", 1, 1); // dominant gate of setDependentAND

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
