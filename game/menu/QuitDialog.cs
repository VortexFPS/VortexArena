using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Quit" — <c>XonoticQuitDialog_fill</c> (qcsrc/menu/xonotic/dialog_quit.qc) on its 3 x 2 grid, 0.5 of the menu
/// wide: the centred question, then a red "Yes" (<c>echo ]quit; quit</c>) and a green "No" (Dialog_Close).
/// It is the main menu's Quit panel and, pushed on its own (<c>menu_showquitdialog</c>), the same dialog in Xonotic's
/// frame.
/// </summary>
public partial class QuitDialog : MenuScreen, IQcDialog
{
    public string QcTitle => Localization.Tr("Quit");
    public float QcIntendedWidth => 0.5f;
    public float QcRows => 3;

    protected override void BuildUi()
    {
        Name = "QuitDialog"; // QC XonoticQuitDialog

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 2, Qc.Label("Are you sure you want to quit?", 0.5f));
        me.TR();
        me.TR();
            me.TD(1, 1, Qc.Button("Yes", () => GetTree().Quit(), tint: new Color(1, 0, 0)));
            me.TD(1, 1, Qc.Button("No", GoBack, tint: new Color(0, 1, 0)));
    }
}
