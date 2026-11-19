using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Game.Console;
using VortexArena.Server;

namespace VortexArena.Game.Menu;

/// <summary>
/// The server's welcome dialog — <c>XonoticWelcomeDialog_fill</c> (qcsrc/menu/xonotic/dialog_welcome.qc) on its 12 x 4
/// grid, 0.8 of the menu wide: the server's message of the day in a text box (<see cref="QcTextBox"/>: centred,
/// colour codes drawn, <c>\n</c> as a line break) over every row but the last, then a green "Join!"
/// (<c>cmd join</c>) and "Spectate" (<c>cmd spectate</c>), each half the width, both closing the dialog. The title is
/// the server's name (welcomeDialog_resetStrings: "Welcome" until a server sends one).
///
/// </summary>
public partial class DialogWelcome : MenuScreen, IQcDialog
{
    private WelcomeDialogData _data;
    private QcTextBox? _message;

    public DialogWelcome() : this(null, "") { }

    public DialogWelcome(string? serverName, string motd) => SetData(new(serverName, motd));

    public string QcTitle => _data.Title ?? Localization.Tr("Welcome");
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 12;
    public bool QcTitleAllowsColors => true;
    public bool QcCloseOnOutsideRelease => true;
    internal bool Demo { get; set; }

    internal void SetData(WelcomeDialogData data)
    {
        _data = data;
        if (_message is not null)
            _message.Text = MessageText;
    }

    // ConfigInterpreter decodes escaped newlines in quotes; callers can also pass stock's literal \n.
    private string MessageText => (_data.Message ?? "").Replace("\\n", "\n").Replace("\r\n", "\n").Replace('\r', '\n');

    protected override void BuildUi()
    {
        Name = "DialogWelcome"; // QC XonoticWelcomeDialog

        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            _message = me.TD(me.Rows - 1, me.Columns, new QcTextBox { Align = 0.5f, AllowColors = true, Text = MessageText });
        me.GotoRC(me.Rows - 1, 0);
            var join = me.TD(1, me.Columns / 2, Qc.Button("Join!", () => RunAndClose("cmd join"), tint: new Color(0, 1, 0)));
            var spectate = me.TD(1, me.Columns / 2, Qc.Button("Spectate", () => RunAndClose("cmd spectate")));
        join.Disabled = spectate.Disabled = Demo;
        if (!Demo && !ConsoleState.IsOpen)
            join.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>XonoticWelcomeDialog_keyDown: Enter and Space press "Join!".</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (!Demo && IsVisibleInTree() && Menu?.Current == this
            && e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter or Key.Space })
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

/// <summary>Payload for the named Welcome dialog (dialog_welcome.qc readInputArgs).</summary>
internal readonly record struct WelcomeDialogData(string? Title, string? Message)
{
    public static bool TryParse(IReadOnlyList<string> args, string campaign, out WelcomeDialogData data)
    {
        data = default;
        string? title = null;
        string? message = null;
        bool recognized = false;
        for (int i = 0; i < args.Count && args[i].Length != 0; i++)
        {
            switch (args[i])
            {
                case "HOSTNAME":
                    if (++i == args.Count) return false;
                    title = args[i];
                    recognized = true;
                    break;
                case "WELCOME":
                    if (++i == args.Count) return false;
                    message = args[i];
                    recognized = true;
                    break;
                case "RESET":
                    return true;
                case "CAMPAIGN":
                    if (args.Count - i < 3 ||
                        !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level))
                        return false;
                    data = CampaignBriefing(campaign, level, args[i + 2]);
                    return true;
            }
        }
        data = new(title, message);
        return recognized;
    }

    private static WelcomeDialogData CampaignBriefing(string campaign, int level, string template)
    {
        string path = CampaignCatalog.FileName(string.IsNullOrWhiteSpace(campaign) ? CampaignCatalog.DefaultName : campaign);
        string? text = MenuState.Vfs is { } vfs && vfs.Exists(path) ? vfs.ReadText(path) : null;
        var levels = CampaignCatalog.Parse(text, out string title);
        var entry = level > 0 ? levels.Find(item => item.Index == level - 1) : null;
        string message = entry is null
            ? $"^1Error: invalid level number {level}"
            : template.Replace("_LEVEL_DESC", entry.ShortDesc + "\n\n" + entry.LongDesc, StringComparison.Ordinal);
        return new(string.IsNullOrWhiteSpace(title) ? Localization.Tr("Campaign") : title, message);
    }
}
