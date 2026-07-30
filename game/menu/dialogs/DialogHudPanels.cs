using System;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// A directory of the HUD panel configuration dialogs — the menu-reachable entry point to the per-panel
/// editors that Xonotic normally opens by clicking a panel in the in-game HUD editor (hud_configure). Each
/// button opens the corresponding <c>DialogHudPanel*</c> (a faithful port of one <c>dialog_hudpanel_*.qc</c>),
/// plus the shared background defaults (<see cref="DialogHudSetupExit"/>). The panel dialogs bind the real
/// <c>hud_panel_*</c> cvars in the shared store; the live HUD that consumes them is a separate bring-up.
/// </summary>
public partial class DialogHudPanels : MenuScreen
{
    // (button label, factory) for every ported HUD panel dialog, in the HUD's panel order.
    private static readonly (string Label, Func<MenuScreen> Open)[] Panels =
    {
        ("Weapons",        () => new DialogHudPanelWeapons()),
        ("Ammo",           () => new DialogHudPanelAmmo()),
        ("Powerups",       () => new DialogHudPanelPowerups()),
        ("Health / Armor", () => new DialogHudPanelHealthArmor()),
        ("Notifications",  () => new DialogHudPanelNotification()),
        ("Timer",          () => new DialogHudPanelTimer()),
        ("Radar",          () => new DialogHudPanelRadar()),
        ("Score",          () => new DialogHudPanelScore()),
        ("Race timer",     () => new DialogHudPanelRaceTimer()),
        ("Vote",           () => new DialogHudPanelVote()),
        ("Mod icons",      () => new DialogHudPanelModIcons()),
        ("Pressed keys",   () => new DialogHudPanelPressedKeys()),
        ("Chat",           () => new DialogHudPanelChat()),
        ("Engine info",    () => new DialogHudPanelEngineInfo()),
        ("Info messages",  () => new DialogHudPanelInfoMessages()),
        ("Physics",        () => new DialogHudPanelPhysics()),
        ("Centerprint",    () => new DialogHudPanelCenterPrint()),
        ("Items time",     () => new DialogHudPanelItemsTime()),
        ("Pickup",         () => new DialogHudPanelPickup()),
        ("Quick menu",     () => new DialogHudPanelQuickMenu()),
        ("Checkpoints",    () => new DialogHudPanelCheckpoints()),
        ("Strafe HUD",     () => new DialogHudPanelStrafeHUD()),
    };

    protected override void BuildUi()
    {
        Name = "DialogHudPanels";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("HUD Panels"));
        root.AddChild(MakeHeader("Configure individual HUD panels"));

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        root.AddChild(scroll);

        // Two-column grid of panel buttons so the 22 panels fit without a long scroll.
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 8);
        scroll.AddChild(grid);

        foreach (var (label, open) in Panels)
        {
            Func<MenuScreen> factory = open; // capture per-iteration
            grid.AddChild(MakeButton(label, () => Menu?.Push(factory())));
        }

        // The shared background/dock/grid defaults (the QC HUD-setup exit dialog) + Back.
        root.AddChild(MakeButtonBar(
            MakeButton("Background defaults...", () => Menu?.Push(new DialogHudSetupExit())),
            MakeButton("Back", GoBack)));
    }
}
