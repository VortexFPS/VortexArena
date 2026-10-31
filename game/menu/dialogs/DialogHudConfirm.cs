using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Enter HUD editor" — <c>XonoticHUDConfirmDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings_game_hudconfirm.qc)
/// on its 4 x 2 grid, 0.5 of the menu wide, in SKINCOLOR_DIALOG_HUDCONFIRM: two centred lines, then a red "Yes"
/// (<c>HUDSetup_Start</c>) and a green "No" that closes it.
///
/// <para><c>HUDSetup_Start</c>: outside a game (<c>!(gamestatus &amp; (GAME_CONNECTED | GAME_ISSERVER))</c>,
/// <see cref="MenuCommand.InMatch"/>) it runs <c>map _hudsetup</c>, inside one <c>togglemenu 0</c>; either way then
/// <c>_hud_configure 1</c>, which the in-game editor (<see cref="Game.Hud.HudConfigEditor"/>) follows.</para>
/// </summary>
public partial class DialogHudConfirm : MenuScreen, IQcDialog
{
    public string QcTitle => Localization.Tr("Enter HUD editor");
    public float QcIntendedWidth => 0.5f;
    public float QcRows => 4;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_HUDCONFIRM", 1f, 0.2f, 0.15f);

    protected override void BuildUi()
    {
        Name = "DialogHudConfirm"; // QC XonoticHUDConfirmDialog

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 2, Qc.Label("In order for the HUD editor to show, you must first be in game.", 0.5f));
        me.TR();
            me.TD(1, 2, Qc.Label("Do you wish to start a local game to set up the HUD?", 0.5f));
        me.TR();
        me.TR();
            me.TD(1, 1, Qc.Button("Yes", HudSetupStart, tint: new Color(1, 0, 0)));
            me.TD(1, 1, Qc.Button("No", GoBack, tint: new Color(0, 1, 0)));
    }

    /// <summary>QC <c>HUDSetup_Start</c>, then closing the dialog (the port's menu stays up otherwise).</summary>
    private void HudSetupStart()
    {
        if (MenuCommand.InMatch?.Invoke() != true)
            MenuCommand.Run("map _hudsetup");
        else
            MenuCommand.Run("togglemenu 0");

        MenuCommand.Run("_hud_configure 1");
        GoBack();
    }
}
