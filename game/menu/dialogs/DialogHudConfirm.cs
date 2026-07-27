using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// "Enter HUD editor" confirm popup — a faithful C# port of <c>XonoticHUDConfirmDialog</c>
/// (qcsrc/menu/xonotic/dialog_settings_game_hudconfirm.qc). Two message lines then a Yes/No row.
///
/// In the QC, "Yes" runs <c>HUDSetup_Start</c>: if you're not already in a game
/// (<c>!(gamestatus &amp; (GAME_CONNECTED | GAME_ISSERVER))</c>) it execs <c>map _hudsetup</c>, otherwise it
/// runs <c>togglemenu 0</c> to drop back into the running match; either way it then sets <c>_hud_configure 1</c>
/// to open the in-game HUD editor. Reached from the menu (not in a match) the not-connected branch is taken, so
/// we issue <c>map _hudsetup</c> + <c>_hud_configure 1</c> and close.
///
/// The flow is LIVE end-to-end: <c>map _hudsetup</c> routes through <see cref="MenuCommand"/> (StartMap hook;
/// the map ships in the maps pk3), <c>_hud_configure 1</c> lands in the shared cvar store via the bare-cvar
/// fallback, and the in-game editor it enables is <see cref="Game.Hud.HudConfigEditor"/> (the hud_config.qc
/// port). "No" was QC <c>Dialog_Close</c> — here the universal Back. Binds no persistent settings cvars of
/// its own. QC title "Enter HUD editor".
/// </summary>
public partial class DialogHudConfirm : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudConfirm"; // QC XonoticHUDConfirmDialog

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Enter HUD editor"));

        // The two QC message rows (makeXonoticTextLabel, centered align 0.5).
        var line1 = MakeLabel("In order for the HUD editor to show, you must first be in game.");
        line1.HorizontalAlignment = HorizontalAlignment.Center;
        var line2 = MakeLabel("Do you wish to start a local game to set up the HUD?");
        line2.HorizontalAlignment = HorizontalAlignment.Center;
        root.AddChild(line1);
        root.AddChild(line2);

        root.AddChild(Ui.Spacer());

        // QC HUDSetup_Start (not-connected branch, taken from the menu): map _hudsetup; _hud_configure 1.
        var yes = MakeButton("Yes", OnYes);
        yes.TooltipText = "Start a local game and open the HUD editor";
        root.AddChild(MakeButtonBar(yes, MakeButton("No", GoBack)));
    }

    /// <summary>QC <c>HUDSetup_Start</c>: load the HUD-setup map, enable the HUD editor, then close.</summary>
    private void OnYes()
    {
        // From the menu we are not connected, so this mirrors the QC `localcmd("map _hudsetup\n")` branch,
        // followed by `_hud_configure 1`. (The in-match branch would instead toggle the menu off.)
        MenuCommand.Run("map _hudsetup; _hud_configure 1");
        GoBack();
    }
}
