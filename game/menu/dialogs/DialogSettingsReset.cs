using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Factory reset" — <c>XonoticResetDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings_misc_reset.qc) on its 4 x 2
/// grid, 0.5 of the menu wide: two centred lines, then a red "Yes" (<c>saveconfig backup.cfg; exec default.cfg</c>)
/// and a green "No" that closes it.
/// </summary>
public partial class DialogSettingsReset : MenuScreen, IQcDialog
{
    public string QcTitle => Localization.Tr("Factory reset");
    public float QcIntendedWidth => 0.5f;
    public float QcRows => 4;

    protected override void BuildUi()
    {
        Name = "DialogSettingsReset"; // QC XonoticResetDialog

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 2, Qc.Label("Are you sure you want to reset all settings?", 0.5f));
        me.TR();
            me.TD(1, 2, Qc.Label("This will create a backup config in your data directory", 0.5f));
        me.TR();
        me.TR();
            var yes = QcLook.Button(Widgets.CommandButton("Yes", "saveconfig backup.cfg; exec default.cfg"), new Color(1, 0, 0));
            me.TD(1, 1, yes);
            me.TD(1, 1, Qc.Button("No", GoBack, tint: new Color(0, 1, 0)));
    }
}
