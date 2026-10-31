using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Reset key bindings" — <c>XonoticBindingsResetDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings_bindings_reset.qc)
/// on its 3 x 2 grid, 0.5 of the menu wide: one centred line, then a red "Yes" and a green "No".
///
/// In the QC, "Yes" runs <c>KeyBinder_Bind_Reset_All</c> (keybinder.qc): <c>unbindall; exec binds-xonotic.cfg;
/// -zoom</c>, sets <c>_hud_showbinds_reload 1</c>, then closes the dialog. Here the first two are
/// <see cref="MenuState.ReloadDefaultBinds"/> on the client's bind table (saved with the config), the rest goes
/// through <see cref="MenuCommand"/>. "No" is QC <c>Dialog_Close</c>.
/// </summary>
public partial class DialogBindingsReset : MenuScreen, IQcDialog
{
    public string QcTitle => Localization.Tr("Reset key bindings");
    public float QcIntendedWidth => 0.5f;
    public float QcRows => 3;

    protected override void BuildUi()
    {
        Name = "DialogBindingsReset"; // QC XonoticBindingsResetDialog

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 2, Qc.Label("Are you sure you want to reset all key bindings?", 0.5f));
        me.TR();
        me.TR();
            me.TD(1, 1, Qc.Button("Yes", () =>
            {
                MenuState.ReloadDefaultBinds();
                MenuState.SaveUserConfig();
                MenuCommand.Run("-zoom; set _hud_showbinds_reload 1");
                GoBack();
            }, tint: new Color(1, 0, 0)));
            me.TD(1, 1, Qc.Button("No", GoBack, tint: new Color(0, 1, 0)));
    }
}
