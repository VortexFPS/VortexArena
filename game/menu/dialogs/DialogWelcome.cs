using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The server's welcome dialog — <c>XonoticWelcomeDialog_fill</c> (qcsrc/menu/xonotic/dialog_welcome.qc) on its 12 x 4
/// grid, 0.8 of the menu wide: the server's message of the day in a text box (<see cref="QcTextBox"/>: centred,
/// colour codes drawn, <c>\n</c> as a line break) over every row but the last, then a green "Join!"
/// (<c>cmd join</c>) and "Spectate" (<c>cmd spectate</c>), each half the width, both closing the dialog. The title is
/// the server's name (welcomeDialog_resetStrings: "Welcome" until a server sends one).
///
/// The port has no server that sends the HOSTNAME / WELCOME arguments yet, so the dialog shows what the constructor
/// is given: by default Xonotic's reset strings.
/// </summary>
public partial class DialogWelcome : MenuScreen, IQcDialog
{
    private readonly string _name;
    private readonly string _motd;

    public DialogWelcome() : this(null, "") { }

    public DialogWelcome(string? serverName, string motd)
    {
        _name = serverName ?? Localization.Tr("Welcome");
        _motd = motd;
    }

    public string QcTitle => _name;
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 12;

    protected override void BuildUi()
    {
        Name = "DialogWelcome"; // QC XonoticWelcomeDialog

        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(me.Rows - 1, me.Columns, new QcTextBox { Align = 0.5f, AllowColors = true, EscapedNewLines = true, Text = _motd });
        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns / 2, Qc.Button("Join!", () => RunAndClose("cmd join"), tint: new Color(0, 1, 0)));
            me.TD(1, me.Columns / 2, Qc.Button("Spectate", () => RunAndClose("cmd spectate")));
    }

    /// <summary>XonoticWelcomeDialog_keyDown: Enter and Space press "Join!".</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter or Key.Space } && IsVisibleInTree())
        {
            RunAndClose("cmd join");
            AcceptEvent();
        }
    }

    // COMMANDBUTTON_CLOSE: issue the command, then close the dialog.
    private void RunAndClose(string command)
    {
        MenuCommand.Run(command);
        GoBack();
    }
}
