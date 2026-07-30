using System.Globalization;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Pickup" panel config dialog — a faithful C# port of <c>XonoticHUDPickupDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_pickup.qc). The common HUD-panel block (Enable mode + Background
/// group, via <see cref="HudPanelCommon"/>) then the pickup-specific rows: message duration, fade time,
/// icon size scale, and the "Show timer" radio set. Every pickup row below the duration is greyed out while
/// <c>hud_panel_pickup_time</c> is 0 (QC setDependentNOT(e, "hud_panel_pickup_time", 0)).
///
/// FAITHFUL UI NOW: the pickup notification panel is drawn by the HUD backend VortexArena hasn't wired up; all
/// cvar bindings are real (they write the shared store the game reads).
/// </summary>
public partial class DialogHudPanelPickup : MenuScreen
{
    private const string TimeCvar = "hud_panel_pickup_time";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelPickup";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Pickup Panel"));

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

        // QC: dialog_hudpanel_main_checkbox(me, "pickup") + dialog_hudpanel_main_settings(me, "pickup").
        HudPanelCommon.BuildCommon(box, "pickup");

        // QC: makeXonoticSlider(1, 5, 1, "hud_panel_pickup_time") with "s" suffix.
        box.AddChild(Ui.Row("Message duration:",
            Widgets.Slider(TimeCvar, 1f, 5f, 1f, format: v => $"{CvarTidy(v)}s")));

        // QC: makeXonoticMixedSlider("hud_panel_pickup_fade_out") — "S" suffix, Instant(0) (FADESPEED^Instant)
        // + range 0.05..1 step 0.05; dependent on hud_panel_pickup_time != 0.
        var fade = Widgets.TextSlider("hud_panel_pickup_fade_out").Add("Instant", 0);
        for (int i = 1; i <= 20; i++)
        {
            float v = i * 0.05f;
            fade.Add($"{v.ToString("0.##", CultureInfo.InvariantCulture)}s", v.ToString("0.###", CultureInfo.InvariantCulture));
        }
        var fadeRow = Ui.Row("Fade time:", fade);
        box.AddChild(fadeRow);
        Dependent.BindNot(fadeRow, TimeCvar, 0);

        // QC: makeXonoticSlider(1, 3, 0.1, "hud_panel_pickup_iconsize") with "%sx" format; dependent on time.
        var iconRow = Ui.Row("Icon size scale:",
            Widgets.Slider("hud_panel_pickup_iconsize", 1f, 3f, 0.1f, format: v => $"{CvarTidy(v)}x"));
        box.AddChild(iconRow);
        Dependent.BindNot(iconRow, TimeCvar, 0);

        // QC: "Show timer:" label + makeXonoticRadioButton(2, "hud_panel_pickup_showtimer", "0"/"2"/"1",
        // Never/Spectating/Always); the whole group is dependent on hud_panel_pickup_time != 0.
        var timerLabel = Ui.Label("Show timer:");
        box.AddChild(timerLabel);
        Dependent.BindNot(timerLabel, TimeCvar, 0);

        var timerGroup = new ButtonGroup();
        var timerRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        timerRow.AddThemeConstantOverride("separation", 8);
        timerRow.AddChild(Widgets.RadioButton("hud_panel_pickup_showtimer", "0", "Never", timerGroup));
        timerRow.AddChild(Widgets.RadioButton("hud_panel_pickup_showtimer", "2", "Spectating", timerGroup));
        timerRow.AddChild(Widgets.RadioButton("hud_panel_pickup_showtimer", "1", "Always", timerGroup));
        box.AddChild(timerRow);
        Dependent.BindNot(timerRow, TimeCvar, 0);

        // QC HUD-panel dialogs close with Dialog_Close → universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    /// <summary>Trim a float to a tidy string (no trailing zeros), matching the QC slider's number display.</summary>
    private static string CvarTidy(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
