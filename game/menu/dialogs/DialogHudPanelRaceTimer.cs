using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Race-timer HUD panel config — a faithful C# port of <c>XonoticHUDRaceTimerDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_racetimer.qc). Configures the race-timer panel (the lap/checkpoint
/// timer shown in Race/CTS). Panel cvar prefix is <c>hud_panel_racetimer</c>.
///
/// The QC fill is the shortest of the family: it is JUST the common HUD-panel block —
/// <c>dialog_hudpanel_main_checkbox</c> (Enable) + <c>dialog_hudpanel_main_settings</c> (the Background
/// group) — with NO panel-specific rows. This port reproduces exactly that: <see cref="HudPanelCommon.BuildCommon"/>
/// then the universal Back. Every control binds the same <c>hud_panel_racetimer</c>/<c>_bg*</c> cvar the QC
/// binds.
///
/// FAITHFUL UI NOW: the <c>hud_panel_racetimer*</c> cvars drive the in-game HUD renderer/editor VortexArena has not
/// wired yet, but the bindings are REAL (they write the shared <see cref="MenuState.Cvars"/> store). No
/// apply/command button exists (just Back). The Color row is a string color picker (no toolkit factory → a
/// Godot <see cref="ColorPickerButton"/> bound to the same cvar, noted).
/// </summary>
public partial class DialogHudPanelRaceTimer : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelRaceTimer"; // QC ATTRIB name "HUDracetimer"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Race Timer"));

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

        // --- common HUD-panel block: enable mode + Background group (the entire QC fill) ---
        HudPanelCommon.BuildCommon(box, "racetimer");

        // QC dialog has no panel-specific rows and no apply/command button — only the universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

}
