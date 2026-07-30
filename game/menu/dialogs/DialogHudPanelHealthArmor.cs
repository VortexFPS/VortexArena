using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Health/Armor" panel configuration dialog — a faithful C# port of
/// <c>XonoticHUDHealthArmorDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_healtharmor.qc). It opens with
/// the common HUD-panel block (Enable mode + Background group from <c>dialog_hudpanel_main_*</c>) then declares
/// the panel-specific rows: combine health/armor, flip positions, the status bar checkbox and its 4-way
/// alignment radio, and the icon-alignment radio.
///
/// FAITHFUL UI NOW: drives the real <c>hud_panel_healtharmor_*</c> cvars (the shared store the in-game HUD
/// reads). The Background color uses a string-RGB color picker, built via the shared
/// <see cref="Widgets.ColorButton"/> (<see cref="CvarColorButton"/>) bound to the same "r g b" string cvar.
/// </summary>
public partial class DialogHudPanelHealthArmor : MenuScreen
{
    private const string Panel = "healtharmor";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelHealthArmor"; // QC ATTRIB name "HUDhealtharmor"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Health/Armor Panel"));

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

        // --- Panel-specific rows (XonoticHUDHealthArmorDialog_fill) ------------------------------------------
        box.AddChild(Ui.Spacer());

        // QC: checkbox hud_panel_healtharmor_combined "Combine health and armor".
        box.AddChild(Widgets.CheckBox("hud_panel_healtharmor_combined", "Combine health and armor"));

        // QC: checkbox hud_panel_healtharmor_flip "Flip health and armor positions",
        // setDependent(hud_panel_healtharmor_combined, 0, 0) -> only when not combined.
        var flip = Widgets.CheckBox("hud_panel_healtharmor_flip", "Flip health and armor positions");
        box.AddChild(flip);
        Dependent.Bind(flip, "hud_panel_healtharmor_combined", 0, 0); // setDependent(...,0,0)

        box.AddChild(Ui.Spacer());

        // QC: checkbox hud_panel_healtharmor_progressbar "Enable status bar".
        box.AddChild(Widgets.CheckBox("hud_panel_healtharmor_progressbar", "Enable status bar"));

        // QC: "Status bar alignment:" label + radio group 2 over hud_panel_healtharmor_baralign
        // (Left=0/Right=1/Inward=2/Outward=3), every widget setDependent(progressbar, 1, 1).
        var barLabel = Ui.Label("Status bar alignment:");
        box.AddChild(barLabel);
        Dependent.Bind(barLabel, "hud_panel_healtharmor_progressbar", 1, 1);
        var barGroup = new ButtonGroup();
        var barRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        barRow.AddThemeConstantOverride("separation", 8);
        barRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_baralign", "0", "Left", barGroup));
        barRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_baralign", "1", "Right", barGroup));
        barRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_baralign", "2", "Inward", barGroup));
        barRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_baralign", "3", "Outward", barGroup));
        box.AddChild(barRow);
        Dependent.Bind(barRow, "hud_panel_healtharmor_progressbar", 1, 1); // setDependent(...,1,1)

        box.AddChild(Ui.Spacer());

        // QC: "Icon alignment:" label + radio group 3 over hud_panel_healtharmor_iconalign
        // (Left=0/Right=1/Inward=2/Outward=3) — no dependency.
        box.AddChild(Ui.Label("Icon alignment:"));
        var iconGroup = new ButtonGroup();
        var iconRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        iconRow.AddThemeConstantOverride("separation", 8);
        iconRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_iconalign", "0", "Left", iconGroup));
        iconRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_iconalign", "1", "Right", iconGroup));
        iconRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_iconalign", "2", "Inward", iconGroup));
        iconRow.AddChild(Widgets.RadioButton("hud_panel_healtharmor_iconalign", "3", "Outward", iconGroup));
        box.AddChild(iconRow);

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
