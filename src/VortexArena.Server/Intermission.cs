using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// End-of-match intermission — the Godot-free essence of <c>NextLevel</c> (server/world.qc) +
/// <c>IntermissionThink</c> / <c>GotoNextMap</c> (server/intermission.qc). When a gametype's win condition
/// trips, the host calls <see cref="Begin"/>: that latches the winner, freezes the world (scoreboard stays
/// up, no more scoring/respawns — QC sets <c>game_stopped = intermission_running = true</c>), and arms a
/// map-change timer (QC <c>intermission_exittime = time + sv_mapchange_delay</c>, or −1 with no players).
///
/// <see cref="Think"/> is polled each frame by <see cref="GameWorld"/>; once the exit time passes (or a
/// player presses fire/jump after a short grace, QC IntermissionThink), <see cref="ReadyToChangeLevel"/>
/// flips and the host proceeds to map voting / the next map.
///
/// The per-player input-driven early exit is modeled here by <see cref="RequestExit"/>. The remaining QC
/// pieces are client/network-only and so have no home in this Godot-free server core: the scoreboard
/// SVC_FINALE/SVC_INTERMISSION broadcast, the autoscreenshot stuffcmd dance, the playerstats game-report
/// upload, the gamelog ":gameover" line, and FixIntermissionClient's view setup. A host that needs them
/// subscribes to <see cref="Running"/> / <see cref="ReadyToChangeLevel"/> and emits them itself.
/// </summary>
public sealed class Intermission
{
    /// <summary>QC <c>sv_mapchange_delay</c> default (xonotic-server.cfg): seconds to hold the scoreboard before switching.</summary>
    public const float DefaultMapChangeDelay = 5f;

    /// <summary>
    /// QC IntermissionThink: after <c>intermission_exittime</c> the server waits up to this many seconds for a
    /// player to press fire/jump/atck2/hook/use before auto-advancing the map (<c>time &lt; intermission_exittime + 10</c>).
    /// </summary>
    public const float InputGracePeriod = 10f;

    /// <summary>QC <c>intermission_running</c>: the match is over and the scoreboard is frozen on screen.</summary>
    public bool Running { get; private set; }

    /// <summary>
    /// The winning player (FFA), latched at <see cref="Begin"/>. Null for a team game or a draw — read
    /// <see cref="WinnerTeam"/> in those cases.
    /// </summary>
    public Player? Winner { get; private set; }

    /// <summary>The winning team color code (team games), or <see cref="Teams.None"/> for FFA / a draw.</summary>
    public int WinnerTeam { get; private set; } = Teams.None;

    /// <summary>QC <c>intermission_exittime</c>: absolute sim time the map may change (−1 = no players, wait for input only).</summary>
    public float ExitTime { get; private set; }

    /// <summary>Set true once the map-change timer (or a player request) elapses; the host then advances the map.</summary>
    public bool ReadyToChangeLevel { get; private set; }

    private bool _exitRequested;

    /// <summary>
    /// Begin intermission for an FFA winner (QC NextLevel + the FFA winner latch). <paramref name="playerCount"/>
    /// gates the exit timer exactly like QC: with players present the timer is <paramref name="mapChangeDelay"/>
    /// seconds out; with none it is −1 (the map changes only once someone is around / requests it).
    /// </summary>
    public void Begin(Player? winner, int playerCount, float? mapChangeDelay = null)
    {
        BeginCommon(playerCount, mapChangeDelay);
        Winner = winner;
        WinnerTeam = Teams.None;
    }

    /// <summary>Begin intermission for a team winner (QC NextLevel + the winner_team latch).</summary>
    public void BeginTeam(int winnerTeam, int playerCount, float? mapChangeDelay = null)
    {
        BeginCommon(playerCount, mapChangeDelay);
        Winner = null;
        WinnerTeam = winnerTeam;
    }

    private void BeginCommon(int playerCount, float? mapChangeDelay)
    {
        if (Running)
            return; // QC: NextLevel is idempotent once intermission_running is set
        Running = true;
        ReadyToChangeLevel = false;
        _exitRequested = false;

        float delay = mapChangeDelay ?? MapChangeDelayCvar();
        float now = Now;
        ExitTime = playerCount > 0 ? now + delay : -1f;
    }

    /// <summary>
    /// A player asked to skip the scoreboard (QC IntermissionThink: pressing fire/jump after the grace
    /// period). Honored by <see cref="Think"/> once the exit time has passed (or immediately when there are
    /// no players and ExitTime is −1).
    /// </summary>
    public void RequestExit() => _exitRequested = true;

    /// <summary>
    /// Advance intermission one frame (QC IntermissionThink, world-side). After the exit time elapses the map
    /// auto-advances once the <see cref="InputGracePeriod"/> (+10s, QC) passes, or immediately when a player has
    /// <see cref="RequestExit"/>ed (pressed fire/jump/atck2/hook/use). <see cref="ReadyToChangeLevel"/> then
    /// flips true. No-op until <see cref="Begin"/> has run.
    /// </summary>
    public void Think()
    {
        if (!Running || ReadyToChangeLevel)
            return;

        float now = Now;

        if (ExitTime < 0f)
        {
            // no players: only an explicit request advances the map (QC -1 exittime).
            if (_exitRequested)
                ReadyToChangeLevel = true;
            return;
        }

        if (now < ExitTime)
            return; // QC: if (time < intermission_exittime) return;

        // Past the hold time (QC IntermissionThink): wait up to +10s for a fire/jump/atck2/hook/use before
        // auto-advancing. An explicit RequestExit (a button press) short-circuits the grace and advances now;
        // otherwise the map auto-advances once the grace window elapses.
        if (_exitRequested || now >= ExitTime + InputGracePeriod)
            ReadyToChangeLevel = true;
    }

    /// <summary>Clear intermission state (QC reset on map change / restart) so a fresh match can run.</summary>
    public void Reset()
    {
        Running = false;
        ReadyToChangeLevel = false;
        _exitRequested = false;
        Winner = null;
        WinnerTeam = Teams.None;
        ExitTime = 0f;
    }

    private static float MapChangeDelayCvar()
    {
        if (Api.Services is null)
            return DefaultMapChangeDelay;
        string s = Api.Cvars.GetString("sv_mapchange_delay");
        if (string.IsNullOrEmpty(s))
            return DefaultMapChangeDelay;
        return Api.Cvars.GetFloat("sv_mapchange_delay");
    }

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
