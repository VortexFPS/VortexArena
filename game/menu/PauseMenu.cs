// Port of qcsrc/menu/xonotic/dialog_gamemenu.qc (XonoticGameMenuDialog) + leavematchbutton.qc.
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The in-game menu shown over a paused match (Escape) — a faithful C# port of <c>XonoticGameMenuDialog</c>
/// (qcsrc/menu/xonotic/dialog_gamemenu.qc). Fill order mirrors the QC exactly:
///   Main menu (menu_cmd nexposee) · Servers · Profile · Settings · Input · Guide (the four indented, via
///   <c>menu_cmd directmenu</c> so closing them drops back into the match) · Quick menu · [gap] · Join! /
///   Restart level (swapped on g_campaign) · Spectate · [gap] · Leave-match · [gap] · Quit.
///
/// The Join!/Spectate/Leave-match buttons carry QC <c>COMMANDBUTTON_CLOSE</c>: after issuing the command the
/// engine closes the menu and returns to the match — reproduced here by resuming after the command runs. The
/// gameplay commands reach the live match through <see cref="MenuCommand.SendGameCommand"/> (wired in
/// <see cref="Shell"/>); without that channel they'd be inert, like the team-select buttons used to be.
/// </summary>
public partial class PauseMenu : MenuScreen
{
    private Button _joinButton = null!;
    private LeaveMatchButton _leaveButton = null!;

    protected override void BuildUi()
    {
        Name = "PauseMenu";

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(340, 0) };
        column.AddThemeConstantOverride("separation", 8);
        center.AddChild(column);

        column.AddChild(MakeTitle("Game Menu"));
        column.AddChild(Gap(10));

        // "Main menu" → menu_cmd nexposee (m_goto false: shows the fan over the match, stays connected).
        column.AddChild(NavButton("Main menu", "menu_cmd nexposee"));

        // The four indented buttons use `directmenu` (m_goto true) so closing the dialog returns to the match.
        column.AddChild(Indent(NavButton("Servers", "menu_cmd directmenu servers")));
        column.AddChild(Indent(NavButton("Profile", "menu_cmd directmenu profile")));
        column.AddChild(Indent(NavButton("Settings", "menu_cmd directmenu settings")));
        column.AddChild(Indent(NavButton("Input", "menu_cmd directmenu inputsettings")));
        column.AddChild(Indent(NavButton("Guide", "menu_cmd directmenu guide")));

        // QC: "Quick menu" with COMMANDBUTTON_CLOSE. No quick-menu backend here; route the command (inert/logged)
        // and close, matching the QC "issue command, return to match" behavior.
        column.AddChild(CloseAfter(NavButton("Quick menu", "quickmenu")));

        column.AddChild(Gap(14)); // QC two blank rows

        // Join! / Restart level — onClickCommand "join" (or "resetmatch" under g_campaign, swapped in _Process).
        _joinButton = CloseAfter(NavButton("Join!", "join"));
        column.AddChild(_joinButton);

        // Spectate — onClickCommand "spec", COMMANDBUTTON_CLOSE.
        column.AddChild(CloseAfter(NavButton("Spectate", "spec")));

        column.AddChild(Gap(14));

        // Leave-match — the dedicated button: disabled when not in a match, dynamic label, disconnects on press.
        _leaveButton = new LeaveMatchButton(() => { Menu?.RequestDisconnect(); });
        column.AddChild(_leaveButton);

        column.AddChild(Gap(14));

        // Quit — red, opens the quit confirmation (QC menu_showquitdialog). NOT a close button.
        var quit = NavButton("Quit", "menu_showquitdialog");
        quit.AddThemeColorOverride("font_color", new Color(1f, 0.35f, 0.35f));
        column.AddChild(quit);

        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        // QC XonoticGameMenuDialog_draw: under g_campaign the Join! button becomes "Restart level"/"resetmatch".
        bool campaign = MenuState.Cvars.GetFloat("g_campaign") != 0f;
        if (campaign && _joinButton.Text == "Join!")
        {
            _joinButton.Text = "Restart level";
            _joinButton.SetMeta(CmdMeta, "resetmatch");
        }
        else if (!campaign && _joinButton.Text == "Restart level")
        {
            _joinButton.Text = "Join!";
            _joinButton.SetMeta(CmdMeta, "join");
        }
    }

    // C2/C3: the SetMeta key is cached as a StringName so this _Process path mints no per-frame allocation
    // (godot#105750) — the XG0002 analyzer flagged the former string literal.
    private static readonly StringName CmdMeta = "cmd";

    // ---- button helpers (the QC makeXonoticCommandButton + COMMANDBUTTON_CLOSE flag) ----------------------

    /// <summary>A command button that runs its command through <see cref="MenuCommand"/> (QC makeXonoticCommandButton).</summary>
    private static Button NavButton(string label, string command)
    {
        var b = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        b.SetMeta("cmd", command);
        b.Pressed += () => MenuCommand.Run(b.GetMeta("cmd").AsString());
        return b;
    }

    /// <summary>Mark a button as QC COMMANDBUTTON_CLOSE: after the command runs, close the menu (resume the match).</summary>
    private T CloseAfter<T>(T b) where T : Button
    {
        b.Pressed += () => Menu?.RequestResume();
        return b;
    }

    /// <summary>QC me.TDempty(0.1): indent a button slightly (the Servers/Profile/Settings/Input/Guide rows).</summary>
    private static Control Indent(Control inner)
    {
        var row = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("margin_left", 28);
        inner.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(inner);
        return row;
    }

    private static Control Gap(float h) => new Control { CustomMinimumSize = new Vector2(0, h) };
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
        bool inMatch = MenuCommand.InMatch?.Invoke() ?? false;
        Disabled = !inMatch;
        // leaveMatchButton_getText: disabled → "Leave current match"; else (single network path) "Leave multiplayer".
        Text = inMatch ? "Leave multiplayer" : "Leave current match";
    }
}
