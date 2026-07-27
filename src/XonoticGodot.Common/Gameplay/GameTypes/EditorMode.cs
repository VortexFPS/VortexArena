using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The in-game map editor, modelled as a gametype (design doc §11.3, phase E2).
///
/// Making the editor a MODE rather than a separate application is the load-bearing decision: it runs inside
/// the normal server/client loop, so it inherits the real renderer, the real movement physics and the real
/// netcode for free. Three things fall out of that with no extra machinery —
/// <list type="bullet">
///   <item><b>Honest playtesting</b>: PLAYTEST is a real player in the real world, not a preview.</item>
///   <item><b>Co-editing</b>: it is already a server, so more than one client can be in the session.</item>
///   <item><b>No editor-only render path</b> to keep in sync with the game's.</item>
/// </list>
///
/// Each connected client is in one of two states, swapped by <c>editor_playtest</c>:
/// <b>EDIT</b> (free-fly, an observer — Space rises, Crouch descends) and <b>PLAYTEST</b> (a spawned player
/// with full movement). The transition preserves position and view angles in both directions, so toggling
/// never teleports you away from the geometry you are working on.
///
/// The mode itself is deliberately inert: no scoring, no frag/time limit, no round structure. It exists so
/// the map-vote/rotation machinery can select it and so the host can gate editor behaviour on
/// <c>GameType is EditorMode</c>.
/// </summary>
[GameType]
public sealed class EditorMode : GameType
{
    public EditorMode()
    {
        NetName = "editor";
        DisplayName = "Map Editor";
        TeamGame = false;
    }

    /// <summary>
    /// QC MENUQC gametype description — shown under the gametype icon in the vote picker.
    /// </summary>
    public override string? MenuDescription =>
        "Edit the map from inside the game. In EDIT mode you fly freely through the level and manipulate its " +
        "geometry directly — grab vertices, edges and faces, move and rotate them, with grid and " +
        "geometry snapping.\n\n" +
        "Switch to PLAYTEST at any time to drop in as a real player and run, jump and bunnyhop through what " +
        "you just built, then switch back and keep working. Your position and view are preserved both ways.\n\n" +
        "There is no scoring and no time limit.";

    /// <summary>
    /// Never report a tie: the editor has no scores, so the overtime cascade must not consider it decidable
    /// one way or the other.
    /// </summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster) => false;
}
