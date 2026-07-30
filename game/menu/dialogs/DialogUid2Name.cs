using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Allow nickname in statistics?" popup — a faithful C# port of <c>XonoticUid2NameDialog</c>
/// (qcsrc/menu/xonotic/dialog_uid2name.qc). Two message lines then a Yes/No row of command buttons.
///   * "Yes" — QC <c>makeXonoticCommandButton(_("Yes"), '0 0 0', "vyes; setreport cl_allow_uid2name 1",
///     COMMANDBUTTON_CLOSE)</c>: casts a yes vote and sets <c>cl_allow_uid2name 1</c>, then closes.
///   * "No" — same with <c>"vno; setreport cl_allow_uid2name 0"</c>.
/// The <c>cl_allow_uid2name</c> write is the REAL effect of this dialog, so each button sets that cvar directly
/// on the shared store (engine <c>setreport</c> = set + report; MenuCommand has no <c>setreport</c>/<c>vyes</c>/
/// <c>vno</c> handler, those log inert) and then closes, mirroring the QC COMMANDBUTTON_CLOSE.
/// QC has an empty title and is non-closable (you must pick Yes or No); we render no title bar text and provide
/// only the two choices (no Back). The QC marks "Yes" as the preferred-focus button.
/// </summary>
public partial class DialogUid2Name : MenuScreen
{
    private const string Cvar = "cl_allow_uid2name";

    protected override void BuildUi()
    {
        Name = "DialogUid2Name"; // QC ATTRIB name "Uid2Name"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        // QC title is "" (empty); the two centered message labels carry the prompt.
        var line1 = MakeLabel("Allow player statistics to use your nickname?");
        line1.HorizontalAlignment = HorizontalAlignment.Center;
        var line2 = MakeLabel("Answering \"No\" you will appear as \"Anonymous player\"");
        line2.HorizontalAlignment = HorizontalAlignment.Center;
        root.AddChild(line1);
        root.AddChild(line2);

        root.AddChild(Ui.Spacer());

        // QC: Yes -> vyes; setreport cl_allow_uid2name 1   (preferredFocusPriority 1)
        //     No  -> vno;  setreport cl_allow_uid2name 0   — both COMMANDBUTTON_CLOSE.
        var yes = MakeButton("Yes", () => Answer(true));
        var no = MakeButton("No", () => Answer(false));
        root.AddChild(MakeButtonBar(yes, no));
        yes.GrabFocus(); // QC preferredFocusPriority on "Yes".
    }

    /// <summary>Cast the vote, record the consent cvar (engine <c>setreport</c>), and close (COMMANDBUTTON_CLOSE).</summary>
    private void Answer(bool allow)
    {
        // setreport = set the cvar AND report it to the server; the cvar write is the real outcome here.
        CvarService cvars = MenuState.Cvars;
        cvars.Set(Cvar, allow ? "1" : "0");
        cvars.MarkArchived(Cvar);
        // The vote command (vyes/vno) has no client backend yet — logged inert by MenuCommand.
        MenuCommand.Run(allow ? "vyes" : "vno");
        GoBack(); // QC COMMANDBUTTON_CLOSE.
    }
}
