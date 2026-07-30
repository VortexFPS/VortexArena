using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Items Time" panel config dialog — a faithful C# port of <c>XonoticHUDItemsTimeDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_itemstime.qc). This panel has no plain Enable checkbox; instead its
/// <c>hud_panel_itemstime</c> cvar is a 3-way mixed slider (Disable/Enable spectating/Enable even in warmup)
/// declared BEFORE the Background group, so we build that enable slider ourselves and call the common block
/// with its own Enable row suppressed. Then the items-time-specific rows: text/icon ratio, icon alignment,
/// status bar (+reduced), and the hide-spawned / hide-big / dynamic-size checkboxes.
///
/// FAITHFUL UI NOW: the items-time panel is drawn by the HUD backend VortexArena hasn't wired up; all cvar
/// bindings are real (they write the shared store the game reads).
/// </summary>
public partial class DialogHudPanelItemsTime : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelItemsTime";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Items Time Panel"));

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

        // QC: makeXonoticMixedSlider("hud_panel_itemstime") with Disable(0)/Enable spectating(1)/
        // Enable even in warmup(2) — this IS the panel's enable cvar (replaces the common Enable checkbox).
        var enable = Widgets.TextSlider("hud_panel_itemstime")
            .Add("Disable", 0)
            .Add("Enable spectating", 1)
            .Add("Enable even in warmup", 2);
        box.AddChild(Ui.Row("Enable:", enable));

        // QC: dialog_hudpanel_main_settings(me, "itemstime") — the Background group only (no Enable checkbox).
        HudPanelCommon.BuildCommon(box, "itemstime", includeEnable: false);

        // QC: makeXonoticSlider(2, 8, 0.5, "hud_panel_itemstime_ratio").
        box.AddChild(Ui.Row("Text/icon ratio:", Widgets.Slider("hud_panel_itemstime_ratio", 2f, 8f, 0.5f)));

        // QC: makeXonoticRadioButton(2, "hud_panel_itemstime_iconalign", "0"/"1", Left/Right).
        var alignGroup = new ButtonGroup();
        var alignRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        alignRow.AddThemeConstantOverride("separation", 8);
        alignRow.AddChild(Widgets.RadioButton("hud_panel_itemstime_iconalign", "0", "Left", alignGroup));
        alignRow.AddChild(Widgets.RadioButton("hud_panel_itemstime_iconalign", "1", "Right", alignGroup));
        box.AddChild(Ui.Row("Icon alignment:", alignRow));

        // QC: makeXonoticCheckBox(0, "hud_panel_itemstime_progressbar", _("Enable status bar")) + a "Reduced"
        // checkbox dependent on the status bar being on (setDependent(e, "...progressbar", 1, 1)).
        var statusRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        statusRow.AddThemeConstantOverride("separation", 12);
        statusRow.AddChild(Widgets.CheckBox("hud_panel_itemstime_progressbar", "Enable status bar"));
        var reduced = Widgets.CheckBox("hud_panel_itemstime_progressbar_reduced", "Reduced");
        statusRow.AddChild(reduced);
        box.AddChild(statusRow);
        Dependent.Bind(reduced, "hud_panel_itemstime_progressbar", 1, 1);

        // QC: three independent checkboxes.
        box.AddChild(Widgets.CheckBox("hud_panel_itemstime_hidespawned", "Hide spawned items"));
        box.AddChild(Widgets.CheckBox("hud_panel_itemstime_hidebig", "Hide big armor and health"));
        box.AddChild(Widgets.CheckBox("hud_panel_itemstime_dynamicsize", "Dynamic size"));

        // QC HUD-panel dialogs close with Dialog_Close → universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
