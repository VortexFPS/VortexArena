using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Allow nickname in statistics?" — <c>XonoticUid2NameDialog_fill</c> (qcsrc/menu/xonotic/dialog_uid2name.qc) on
/// its 4 x 2 grid, 0.5 of the menu wide, with an empty title: two centred lines, a blank row, then the "Yes" /
/// "No" command buttons (<c>vyes; setreport cl_allow_uid2name 1</c> / <c>vno; setreport cl_allow_uid2name 0</c>,
/// COMMANDBUTTON_CLOSE). "Yes" is the preferred focus.
///
/// The <c>cl_allow_uid2name</c> write is the real effect, so each button sets that cvar directly on the shared
/// store (engine <c>setreport</c> = set + report; MenuCommand has no <c>setreport</c> handler) and runs the vote
/// command, then closes. The QC dialog is not closable (<c>closable false</c>); the port's frame still draws its
/// close button.
/// </summary>
public partial class DialogUid2Name : MenuScreen, IQcDialog
{
    private const string Cvar = "cl_allow_uid2name";

    // dialog_uid2name.qh
    public string QcTitle => "";
    public float QcIntendedWidth => 0.5f;
    public float QcRows => 4;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_TEAMSELECT", 1f, 1f, 1f);

    protected override void BuildUi()
    {
        Name = "DialogUid2Name"; // QC ATTRIB name "Uid2Name"

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 2, Qc.Label("Allow player statistics to use your nickname?", 0.5f));
        me.TR();
            me.TD(1, 2, Qc.Label("Answering \"No\" you will appear as \"Anonymous player\"", 0.5f));
        me.TR();
        me.TR();
            var yes = me.TD(1, 1, Qc.Button("Yes", () => Answer(true)));
            me.TD(1, 1, Qc.Button("No", () => Answer(false)));
        yes.CallDeferred(Control.MethodName.GrabFocus); // preferredFocusPriority 1
    }

    /// <summary>Cast the vote, record the consent cvar (engine <c>setreport</c>), and close (COMMANDBUTTON_CLOSE).</summary>
    private void Answer(bool allow)
    {
        CvarService cvars = MenuState.Cvars;
        cvars.Set(Cvar, allow ? "1" : "0");
        cvars.MarkArchived(Cvar);
        MenuCommand.Run(allow ? "vyes" : "vno");
        GoBack();
    }
}
