using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Warning" — <c>XonoticLanguageWarningDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings_user_languagewarning.qc)
/// on its 4 x 4 grid, 0.6 of the menu wide: the wrapped warning over two rows, then "Disconnect now"
/// (<c>disconnect</c>) and "Switch language" (<c>prvm_language "$_menu_prvm_language"; menu_restart; menu_cmd
/// languageselect</c>), two columns each. It has no close button of its own; the frame's closes it.
/// </summary>
public partial class DialogLanguageWarning : MenuScreen, IQcDialog
{
    public string QcTitle => Localization.Tr("Warning");
    public float QcIntendedWidth => 0.6f;
    public float QcRows => 4;

    protected override void BuildUi()
    {
        Name = "DialogLanguageWarning"; // QC XonoticLanguageWarningDialog

        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(2, 4, new QcTextLabel("While connected language changes will be applied only to the menu, full language changes will take effect starting from the next game")
            {
                AllowWrap = true, WrapSpacing = 0.5f,
            });
        me.TR();
        me.TR();
        me.TR();
            // reconnect command doesn't work properly, otherwise it would replace disconnect
            me.TD(1, 2, Qc.Command("Disconnect now", "disconnect"));
            me.TD(1, 2, Qc.Command("Switch language", "prvm_language \"$_menu_prvm_language\"; menu_restart; menu_cmd languageselect"));
    }
}
