using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The in-game Team Selection dialog — a faithful C# port of <c>XonoticTeamSelectDialog</c>
/// (qcsrc/menu/xonotic/dialog_teamselect.qc / .qh). Shown while connected to a team match: it offers an
/// auto-select button, one button per available team (Red/Blue/Yellow/Pink), and a Spectate button. Each
/// button issues the same engine command the QC issues, routed through <see cref="Widgets.CommandButton"/> →
/// <see cref="MenuCommand"/>.
///
/// FAITHFUL UI NOW: the join actions ("cmd selectteam red; cmd join", "cmd spectate", …) drive the server
/// team backend, which VortexArena does NOT have yet — <see cref="MenuCommand"/> logs them inert (no live match
/// join). The team-availability gating is reproduced from the QC <c>showNotify</c>: it reads the
/// <c>_teams_available</c> bitmask (1=red, 2=blue, 4=yellow, 8=pink) and disables teams whose bit is clear.
/// With no live match that cvar is 0, so all four team buttons start disabled (exactly as QC would do
/// out-of-match) and an honest note explains why.
///
/// Layout mirrors the QC table (intendedWidth 0.4, 5 rows × 4 columns): the auto button spans the full
/// width, the four team buttons share one row, then Spectate spans the full width.
/// </summary>
public partial class DialogTeamSelect : MenuScreen
{
    // QC team bitmask bits in _teams_available (see showNotify: team1..team4 ↔ bits 1,2,4,8).
    private const int TeamRedBit = 1;
    private const int TeamBlueBit = 2;
    private const int TeamYellowBit = 4;
    private const int TeamPinkBit = 8;

    // QC big-button tint colors ('r g b' vectors from dialog_teamselect.qc), applied as the button text color
    // to stay faithful to the colored team buttons.
    private static readonly Color RedColor = new(1f, 0.5f, 0.5f);
    private static readonly Color BlueColor = new(0.5f, 0.5f, 1f);
    private static readonly Color YellowColor = new(1f, 1f, 0.5f);
    private static readonly Color PinkColor = new(1f, 0.5f, 1f);

    protected override void BuildUi()
    {
        Name = "TeamSelect"; // QC ATTRIB name "TeamSelect"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        // QC ATTRIB title "Team Selection".
        root.AddChild(MakeTitle("Team Selection"));

        // Read the team-availability bitmask exactly as QC showNotify does (cvar("_teams_available")).
        // Out of a live match this is 0, which disables every team button (faithful to QC behaviour).
        int teamsAvailable = (int)MenuState.Cvars.GetFloat("_teams_available");

        // Row 1 (QC: me.TD(me, 2, 4, ...)) — auto-select spans the full width; it is the preferred focus.
        // command "cmd selectteam auto; cmd join", tooltip "Auto-select team (recommended)".
        var auto = MakeBigCommandButton("join 'best' team (auto-select)", Colors.White,
            "cmd selectteam auto; cmd join", "Auto-select team (recommended)");
        auto.GrabFocus(); // QC e.preferredFocusPriority = 1
        root.AddChild(auto);

        root.AddChild(Ui.Spacer()); // QC blank TR between auto and the team row

        // Row 3 (QC: four me.TD(me, 2, 1, ...)) — the four team buttons side by side.
        // Each: command "cmd selectteam <color>; cmd join". Disabled when its _teams_available bit is clear.
        var teamRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        teamRow.AddThemeConstantOverride("separation", 12);

        // QC drives these from _teams_available; out of a live match that bitmask is 0. Rather than render the
        // row as four blank, indistinguishable pills, always preview the four standard Xonotic teams with their
        // names and colors so the row reads as team choices. Each is enabled only when its availability bit is set.
        teamRow.AddChild(MakeTeamButton("Red", RedColor, "cmd selectteam red; cmd join",
            (teamsAvailable & TeamRedBit) != 0));
        teamRow.AddChild(MakeTeamButton("Blue", BlueColor, "cmd selectteam blue; cmd join",
            (teamsAvailable & TeamBlueBit) != 0));
        teamRow.AddChild(MakeTeamButton("Yellow", YellowColor, "cmd selectteam yellow; cmd join",
            (teamsAvailable & TeamYellowBit) != 0));
        teamRow.AddChild(MakeTeamButton("Pink", PinkColor, "cmd selectteam pink; cmd join",
            (teamsAvailable & TeamPinkBit) != 0));

        root.AddChild(teamRow);

        // Honest note when no live match supplies _teams_available (team buttons are then all disabled).
        if (teamsAvailable == 0)
            root.AddChild(Ui.Label("(team list — no active team match; team availability backend pending)"));

        root.AddChild(Ui.Spacer()); // QC blank TR between the team row and spectate

        // Row 5 (QC: me.TD(me, 1, 4, makeXonoticCommandButton("spectate", '0 0 0', "cmd spectate", …))).
        root.AddChild(MakeBigCommandButton("spectate", Colors.White, "cmd spectate"));

        // Not in the QC dialog itself (it auto-closes via COMMANDBUTTON_CLOSE), but every VortexArena full-screen
        // dialog needs a way back to the host.
        root.AddChild(Ui.Spacer());
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    /// <summary>
    /// A full-width "big" command button (QC <c>makeXonoticBigCommandButton</c>): runs the same command via
    /// <see cref="Widgets.CommandButton"/>, tinted with the QC button color. The QC <c>COMMANDBUTTON_CLOSE</c>
    /// flag (close the dialog on press) is noted inert — there is no live match to leave yet.
    /// </summary>
    private static CommandButton MakeBigCommandButton(string label, Color color, string command, string tooltip = "")
    {
        var b = Widgets.CommandButton(label, command, tooltip);
        b.CustomMinimumSize = new Vector2(0, 48); // "big" button
        if (color != Colors.White)
            b.AddThemeColorOverride("font_color", color);
        return b;
    }

    /// <summary>
    /// One team button (QC <c>makeTeamButton</c>): a big command button tinted with the team color, enabled
    /// only when the team is available (QC <c>showNotify</c> sets <c>disabled</c> from <c>_teams_available</c>).
    /// The whole button is modulated toward the team color (not just the text) so the row reads as four distinct,
    /// labelled team choices even when no live match supplies <c>_teams_available</c> and the buttons preview as
    /// disabled.
    /// </summary>
    private static CommandButton MakeTeamButton(string label, Color color, string command, bool available)
    {
        var b = MakeBigCommandButton(label, color, command);
        // Tint the button itself toward the team color so each pill reads as red/blue/yellow/pink, not a blank
        // widget. Keep the label bright (white) for legibility on top of the colored pill.
        b.AddThemeColorOverride("font_color", Colors.White);
        b.Modulate = available ? color : color * new Color(1, 1, 1, 0.55f); // dim the preview-only (disabled) state
        return b;
    }
}
