using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Team Selection" — <c>XonoticTeamSelectDialog_fill</c> (qcsrc/menu/xonotic/dialog_teamselect.qc) on its 5 x 4
/// grid, 0.4 of the menu wide: the auto-select big button over rows 0..1 (the preferred focus), the four team big
/// buttons over rows 2..3 tinted red '1 0.5 0.5', blue '0.5 0.5 1', yellow '1 1 0.5' and pink '1 0.5 1' (the
/// <c>bigbuttongray</c> art), and a plain "spectate" command button on the last row. Every button closes the
/// dialog after its command (COMMANDBUTTON_CLOSE).
///
/// <c>showNotify</c>: a team's button is disabled while its bit of <c>_teams_available</c> (1 red, 2 blue,
/// 4 yellow, 8 pink) is clear. The commands are the expansions of the QC's <c>team_auto</c> / <c>team_red</c> …
/// aliases (commands.cfg), routed through <see cref="MenuCommand"/>.
/// </summary>
public partial class DialogTeamSelect : MenuScreen, IQcDialog
{
    // dialog_teamselect.qh (name "TeamSelect")
    public string QcTitle => Localization.Tr("Team Selection");
    public float QcIntendedWidth => 0.4f;
    public float QcRows => 5;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_TEAMSELECT", 1f, 1f, 1f);

    protected override void BuildUi()
    {
        Name = "TeamSelect"; // QC ATTRIB name "TeamSelect"

        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            var auto = me.TD(2, 4, TeamButton("join 'best' team (auto-select)", null, "cmd selectteam auto; cmd join",
                "Auto-select team (recommended)"));
        me.TR();
        me.TR();
            var team1 = me.TD(2, 1, TeamButton("red", new Color(1, 0.5f, 0.5f), "cmd selectteam red; cmd join"));
            var team2 = me.TD(2, 1, TeamButton("blue", new Color(0.5f, 0.5f, 1), "cmd selectteam blue; cmd join"));
            var team3 = me.TD(2, 1, TeamButton("yellow", new Color(1, 1, 0.5f), "cmd selectteam yellow; cmd join"));
            var team4 = me.TD(2, 1, TeamButton("pink", new Color(1, 0.5f, 1), "cmd selectteam pink; cmd join"));
        me.TR();
        me.TR();
            var spectate = me.TD(1, 4, Qc.Command("spectate", "cmd spectate"));
            spectate.Pressed += GoBack;

        // XonoticTeamSelectDialog_showNotify.
        int teams = (int)MenuState.Cvars.GetFloat("_teams_available");
        team1.Disabled = (teams & 1) == 0;
        team2.Disabled = (teams & 2) == 0;
        team3.Disabled = (teams & 4) == 0;
        team4.Disabled = (teams & 8) == 0;

        auto.CallDeferred(Control.MethodName.GrabFocus); // preferredFocusPriority 1
    }

    /// <summary>makeTeamButton_T: makeXonoticBigCommandButton_T(name, color, command, COMMANDBUTTON_CLOSE, tooltip) —
    /// the <c>bigbutton</c> picture, or <c>bigbuttongray</c> tinted with the team colour.</summary>
    private CommandButton TeamButton(string text, Color? tint, string command, string tooltip = "")
    {
        CommandButton b = QcLook.Button(Widgets.CommandButton(text, command, tooltip), tint, image: "bigbutton");
        b.Pressed += GoBack;
        return b;
    }
}
