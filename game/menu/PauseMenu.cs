// Port of qcsrc/menu/xonotic/dialog_gamemenu.qc (XonoticGameMenuDialog) + leavematchbutton.qc.
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Game menu", the in-game menu shown over a match (Escape) — <c>XonoticGameMenuDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_gamemenu.qc) on its 14 x 1 grid, 0.3 of the menu wide, TD for TD: Main menu
/// (<c>menu_cmd nexposee</c>); Servers, Profile, Settings, Input and Guide indented 0.1 (<c>menu_cmd directmenu</c>, so
/// closing them drops back into the match); Quick menu; Join! (green; "Restart level" / <c>resetmatch</c> under
/// g_campaign, as XonoticGameMenuDialog_draw swaps it); Spectate; the leave-match button; Quit (red,
/// <c>menu_showquitdialog</c>).
///
/// Join!, Spectate, Quick menu and the leave-match button carry COMMANDBUTTON_CLOSE: after the command the menu
/// closes and the match resumes. The gameplay commands reach the live match through
/// <see cref="MenuCommand.SendGameCommand"/> (wired in <see cref="Shell"/>).
/// </summary>
public partial class PauseMenu : MenuScreen, IQcDialog
{
    public string QcTitle => Localization.Tr("Game menu");
    public float QcIntendedWidth => 0.3f;
    public float QcRows => 14;

    private Button _joinButton = null!;
    private string _joinCommand = "join";

    protected override void BuildUi()
    {
        Name = "PauseMenu"; // QC XonoticGameMenuDialog

        var me = new QcGrid(QcRows, 1);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 1, Command("Main menu", "menu_cmd nexposee"));
        me.TR();
            me.TDempty(0.1f);
            me.TD(1, 0.8f, Command("Servers", "menu_cmd directmenu servers"));
        me.TR();
            me.TDempty(0.1f);
            me.TD(1, 0.8f, Command("Profile", "menu_cmd directmenu profile"));
        me.TR();
            me.TDempty(0.1f);
            me.TD(1, 0.8f, Command("Settings", "menu_cmd directmenu settings"));
        me.TR();
            me.TDempty(0.1f);
            me.TD(1, 0.8f, Command("Input", "menu_cmd directmenu inputsettings"));
        me.TR();
            me.TDempty(0.1f);
            me.TD(1, 0.8f, Command("Guide", "menu_cmd directmenu guide"));
        me.TR();
            me.TD(1, 1, CloseAfter(Command("Quick menu", "quickmenu")));
        me.TR();
        me.TR();
            _joinButton = me.TD(1, 1, CloseAfter(Qc.Button("Join!", () => MenuCommand.Run(_joinCommand), tint: new Color(0, 1, 0))));
        me.TR();
            me.TD(1, 1, CloseAfter(Command("Spectate", "spec")));
        me.TR();
        me.TR();
            me.TD(1, 1, QcLook.Button(new LeaveMatchButton(() => Menu?.RequestDisconnect()) { CustomMinimumSize = Vector2.Zero }));
        me.TR();
        me.TR();
            me.TD(1, 1, Qc.Button("Quit", () => MenuCommand.Run("menu_showquitdialog"), tint: new Color(1, 0, 0)));

        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        // (perf 2026-08-03) Once opened, this screen stays alive-but-hidden for the rest of the match, and Shell
        // processes Always — the button text only matters while visible.
        if (!IsVisibleInTree())
            return;
        // XonoticGameMenuDialog_draw: under g_campaign the Join! button becomes "Restart level" / resetmatch.
        string command = MenuState.Cvars.GetFloat("g_campaign") != 0f ? "resetmatch" : "join";
        if (command != _joinCommand)
        {
            _joinCommand = command;
            _joinButton.Text = Localization.Tr(command == "join" ? "Join!" : "Restart level");
        }
    }

    /// <summary>makeXonoticCommandButton(text, '0 0 0', command, 0).</summary>
    private static Button Command(string text, string command) => Qc.Button(text, () => MenuCommand.Run(command));

    /// <summary>COMMANDBUTTON_CLOSE: after the command runs, close the menu (resume the match).</summary>
    private T CloseAfter<T>(T b) where T : BaseButton
    {
        b.Pressed += () => Menu?.RequestResume();
        return b;
    }
}

/// <summary>
/// The Leave-match button — a faithful C# port of <c>XonoticLeaveMatchButton</c> (leavematchbutton.qc). Its
/// disabled state and label track the match status every frame (QC <c>draw</c>): disabled when not in a match
/// ("Leave current match"); else "Leave multiplayer" (the port has one network path, so the campaign/demo/
/// singleplayer label variants collapse to this). On press it disconnects — the QC <c>LEAVEMATCH_CMD</c> is a
/// deferred <c>disconnect; …; g_campaign 0; menu_sync</c> chain whose 0.4s delay only exists to let the click
/// sound finish before disconnect stops all sounds; the port has no such race, so it disconnects directly.
/// </summary>
public partial class LeaveMatchButton : Button
{
    private readonly System.Action _onLeave;

    public LeaveMatchButton(System.Action onLeave)
    {
        _onLeave = onLeave;
        CustomMinimumSize = new Vector2(0, 34);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Pressed += () => _onLeave();
    }

    public override void _Process(double delta)
    {
        // (perf 2026-08-03) Hidden for the whole match otherwise, and this wrote a marshalled STRING
        // property (Text) plus Disabled every single frame. Both are display-only.
        if (!IsVisibleInTree())
            return;
        bool inMatch = MenuCommand.InMatch?.Invoke() ?? false;
        Disabled = !inMatch;
        // leaveMatchButton_getText: disabled → "Leave current match"; "Leave campaign" under g_campaign; else (the
        // port has one network path) "Leave multiplayer".
        string text = Localization.Tr(!inMatch ? "Leave current match"
            : MenuState.Cvars.GetFloat("g_campaign") != 0f ? "Leave campaign" : "Leave multiplayer");
        if (Text != text) Text = text;
    }
}
