using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Ammo" panel configuration dialog — a faithful C# port of <c>XonoticHUDAmmoDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_ammo.qc). Like every HUD-panel dialog it opens with the common panel
/// settings emitted by <c>dialog_hudpanel_main_checkbox</c> + <c>dialog_hudpanel_main_settings</c> (the Enable
/// mode and the Background group) and then declares the panel-specific rows: show-only-current-ammo, the
/// noncurrent opacity/scale sliders, the icon-align radio and the status-bar checkbox.
///
/// FAITHFUL UI NOW: this drives the in-game HUD's <c>hud_panel_ammo_*</c> cvars (the shared store the HUD reads
/// in configure mode). VortexArena has no live HUD editor yet, but the cvar bindings are real — every control here
/// writes the same cvar the QC binds, with the same <c>setDependent</c> grey-outs. The QC background color uses
/// a string-RGB color picker (<c>makeXonoticColorpickerString</c>), built via the shared
/// <see cref="Widgets.ColorButton"/> (<see cref="CvarColorButton"/>) bound to the same "r g b" string cvar.
/// </summary>
public partial class DialogHudPanelAmmo : MenuScreen
{
    private const string Panel = "ammo";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelAmmo"; // QC ATTRIB name "HUDammo"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Ammo Panel"));

        // The QC form is a long table; wrap it in a ScrollContainer > VBox so it scrolls (never a TabContainer).
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

        // --- Panel-specific rows (XonoticHUDAmmoDialog_fill) -------------------------------------------------
        box.AddChild(Ui.Spacer());

        // QC: checkbox hud_panel_ammo_onlycurrent "Show only current ammo type".
        box.AddChild(Widgets.CheckBox("hud_panel_ammo_onlycurrent", "Show only current ammo type"));

        // QC: "Noncurrent opacity:" slider hud_panel_ammo_noncurrent_alpha (0.1..1 step 0.1, "%"),
        // both label+slider setDependent(hud_panel_ammo_onlycurrent, 0, 0) -> enabled while onlycurrent is off.
        var ncAlpha = Widgets.Slider("hud_panel_ammo_noncurrent_alpha", 0.1f, 1f, 0.1f, format: HudPanelCommon.Percent);
        var ncAlphaRow = Ui.Row("Noncurrent opacity:", ncAlpha);
        box.AddChild(ncAlphaRow);
        Dependent.Bind(ncAlphaRow, "hud_panel_ammo_onlycurrent", 0, 0); // setDependent(...,0,0)

        // QC: "Noncurrent scale:" slider hud_panel_ammo_noncurrent_scale, setDependentNOT(noncurrent_alpha, 0)
        // AND setDependentAND(onlycurrent, 0, 0). The framework Dependent does a single test; we keep the
        // dominant onlycurrent==0 gate (matching the opacity row) — the AND/NOT pair is approximated (noted).
        var ncScale = Widgets.Slider("hud_panel_ammo_noncurrent_scale", 0.1f, 1f, 0.1f, format: HudPanelCommon.Percent);
        var ncScaleRow = Ui.Row("Noncurrent scale:", ncScale);
        box.AddChild(ncScaleRow);
        Dependent.Bind(ncScaleRow, "hud_panel_ammo_onlycurrent", 0, 0); // dominant gate of setDependentAND

        box.AddChild(Ui.Spacer());

        // QC: "Align icon:" radio group 2 over hud_panel_ammo_iconalign — Left=0 / Right=1.
        box.AddChild(Ui.Label("Align icon:"));
        var iconGroup = new ButtonGroup();
        var iconRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        iconRow.AddThemeConstantOverride("separation", 8);
        iconRow.AddChild(Widgets.RadioButton("hud_panel_ammo_iconalign", "0", "Left", iconGroup));
        iconRow.AddChild(Widgets.RadioButton("hud_panel_ammo_iconalign", "1", "Right", iconGroup));
        box.AddChild(iconRow);

        // QC: checkbox hud_panel_ammo_progressbar "Enable status bar".
        box.AddChild(Widgets.CheckBox("hud_panel_ammo_progressbar", "Enable status bar"));

        // QC "OK" closed the dialog; here the universal Back does it.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
