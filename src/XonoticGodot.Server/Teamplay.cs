using System;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The server-side team manager — the C# successor to the team-assignment slice of
/// server/teamplay.qc (<c>TeamBalance_JoinBestTeam</c> → <c>TeamBalance_FindBestTeam</c>: place a joining
/// player on the active team with the fewest players; ties → the lowest-index such team). It wraps the
/// Godot-free core already ported in <see cref="TeamBalance"/> (XonoticGodot.Common) and adds the server-roster
/// concerns: the configured team count, balance over the live roster, and a notion of "this gametype is a
/// team game".
///
/// QC <c>TeamBalance_FindBestTeam</c> weighs the smallest team first and, on a tie, the team with the lower
/// total score (so a team that is both small and losing is preferred) — both rules are ported here. Also
/// ported now: the inverse-variance skill weighting (<c>g_balance_teams_skill</c>: <see cref="MeasureTeam"/>
/// computes each team's weighted mean skill and <see cref="CompareTeams"/> breaks equal-size ties by strength
/// only when the skill gap is statistically significant (z &gt; <c>..._significance_threshold</c> SDs), so a
/// strong player counts as "more" than a weak one but skill never reorders teams of unequal size), the bot
/// autobalance (<see cref="AutoBalanceBots"/>, QC <c>TeamBalance_AutoBalanceBots</c>: move the lowest-scoring
/// bot from the largest to the smallest team when uneven), and <see cref="KillPlayerForTeamChange"/>
/// (QC the forced death + score clear on a mid-match team move). A <see cref="SkillProvider"/> supplies each
/// player's skill (bots feed their <c>skill</c> level) since TrueSkill mu/variance ratings aren't modeled.
///
/// Also ported now: forced teams (<see cref="DetermineForcedTeam"/> / <see cref="GetForcedTeam"/>, QC
/// <c>Player_DetermineForcedTeam</c> / <c>Player_HasRealForcedTeam</c> — <c>g_forced_team_*</c> id/IP-list
/// parsing, <c>g_campaign_forceteam</c>, and the <c>bot_forced_team</c> pin, all honored on the live
/// <see cref="AssignBestTeam"/> join), the <c>bot_vs_human</c> team partition (QC
/// <c>TeamBalance_CheckAllowedTeams</c>, bots banned to one side / humans to the other), the unbalanced-teams
/// size gap (<see cref="SizeDifference"/> / <see cref="TeamsUnbalancedForNag"/>, QC
/// <c>TeamBalance_SizeDifference</c> + <c>sv_teamnagger</c>, for the nag + warmup-hold), and the mid-match
/// switch gate (<see cref="IsTeamAllowedForSwitch"/>, QC <c>g_balance_teams_prevent_imbalance</c> /
/// cmd.qc:746 — a human can't switch to a team that isn't a best team).
///
/// Deferred (need cross-file plumbing): the warmup join queue (<c>g_balance_teams_queue</c>), excess-player
/// removal (<c>g_balance_teams_remove</c>), the network side of the team-nagger center-print, the
/// Player_ChangeTeam/Player_ChangedTeam mutator hooks (no hook chain exists yet), and the
/// <c>teamplay_lockonrestart</c> auto-lock (the restart hook lives in the vote/warmup controller).
/// </summary>
public sealed class Teamplay
{
    /// <summary>QC <c>TEAM_FORCE_SPECTATOR</c>: the forced-team sentinel meaning "force this player to spectate".</summary>
    public const int TeamForceSpectator = -1;

    /// <summary>QC <c>TEAM_FORCE_DEFAULT</c>: no real forced team — fall through to normal balance.</summary>
    public const int TeamForceDefault = 0;

    /// <summary>QC <c>TEAM_CHANGE_AUTO</c> (server/teamplay.qh:129): the team was selected by autobalance.</summary>
    public const int TeamChangeAuto = 2;

    /// <summary>QC <c>TEAM_CHANGE_MANUAL</c> (server/teamplay.qh:130): the player manually selected their team.</summary>
    public const int TeamChangeManual = 3;

    /// <summary>QC <c>TEAM_CHANGE_SPECTATOR</c> (server/teamplay.qh:131): the player is joining spectators.</summary>
    public const int TeamChangeSpectator = 4;

    /// <summary>QC the admin-move team-change type (the literal <c>6</c> passed to <c>MoveToTeam</c> by the
    /// <c>movetoteam</c>/<c>shuffleteams</c> admin commands, sv_cmd.qc:1139/1374): logged in the <c>:team:</c> line.</summary>
    public const int TeamChangeAdmin = 6;

    /// <summary>Whether the active gametype is a team game (QC <c>teamplay</c>). FFA leaves teams at <see cref="Teams.None"/>.</summary>
    public bool IsTeamGame { get; }

    /// <summary>Number of teams in play (2..4). 0 for a non-team game.</summary>
    public int TeamCount { get; private set; }

    /// <summary>The running per-team score, mirrored from <see cref="Scores"/> so balance can read it without a back-reference.</summary>
    private readonly Scores? _scores;

    /// <summary>
    /// Supplies a player's skill rating (QC <c>m_skill_mu</c>): bots feed their <c>skill</c> level (0..10),
    /// humans default to a mid rating. Used for the skill-weighted team balance when
    /// <c>g_balance_teams_skill</c> is enabled. Defaults to a flat 5 for every player (no skill weighting
    /// effect) until a host wires in real skills (e.g. each bot's <see cref="Bot.BotBrain.Skill"/>).
    /// </summary>
    public Func<Player, float> SkillProvider { get; set; } = static _ => 5f;

    /// <summary>
    /// QC <c>warmup_stage</c> for the team-strength ramp: the score-ratio scaling in
    /// <see cref="CompareTeams"/> only applies once the match has started (<c>!warmup_stage &amp;&amp; time &gt;
    /// game_starttime</c>). Defaults to "in warmup" (the ramp is inert) so a host that hasn't wired a match
    /// clock — and the deterministic tests — keep the plain count/skill comparison. The host wires this to the
    /// live warmup controller.
    /// </summary>
    public Func<bool> IsWarmup { get; set; } = static () => true;

    /// <summary>
    /// QC <c>(time - game_starttime)</c> for the team-strength ramp: seconds elapsed since the match clock
    /// started (≤ 0 while in warmup / before game_starttime). Used with <see cref="TimeLimitSeconds"/> to scale
    /// the score-ratio strength bias by <c>min(1, elapsed/timelimit)^1.5</c>. Defaults to 0 (ramp inert).
    /// </summary>
    public Func<float> SecondsSinceMatchStart { get; set; } = static () => 0f;

    /// <summary>QC the fixed skill variance stand-in (no TrueSkill ratings here): the inverse weight per player.</summary>
    private const float SkillVariance = 1f;

    /// <summary>QC <c>server_skill_average</c>: the inverse-variance weighted mean skill of all rated clients,
    /// recomputed per balance pass in <see cref="MeasureAllTeams"/>. Falls back to 1000 (QC) when nobody is rated.</summary>
    private float _serverSkillAverage = 1000f;

    /// <summary>
    /// QC <c>.team_forced</c>: the per-player forced-team index (1..4 a real team, <see cref="TeamForceDefault"/>,
    /// or <see cref="TeamForceSpectator"/>). Player.cs has no such field, so it is kept here keyed by the player
    /// reference; <see cref="DetermineForcedTeam"/> populates it on connect and <see cref="AssignBestTeam"/>
    /// honors it. Defaults to <see cref="TeamForceDefault"/> for any player not in the table.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, object> _forcedTeam = new();

    /// <summary>
    /// QC <c>.bot_forced_team</c>: a bot pinned to a specific team by its roster config (5th arg of the addbot
    /// command). When set (1..4), <see cref="AssignBestTeam"/> early-outs and the bot keeps that team without
    /// auto-balance, exactly like QC's <c>TeamBalance_JoinBestTeam</c> early-out (teamplay.qc:461). Keyed by the
    /// bot's <see cref="Player"/> reference since Player.cs has no field for it.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, object> _botForcedTeam = new();

    /// <summary>
    /// QC <c>PutObserverInServer(player, true, true)</c>: move a player to spectators (used by the excess-player
    /// removal). The host wires this to <c>ClientManager.PutObserverInServer</c>. A no-op until wired (the
    /// removal then can't kick, matching a host that hasn't enabled the feature).
    /// </summary>
    public Action<Player>? MoveToSpectator { get; set; }

    /// <summary>QC <c>time</c>: the absolute sim clock, for the excess-removal countdown's 1-second cadence.
    /// Defaults to a monotonic stand-in of 0 (the countdown then advances one tick per <see cref="TickRemoveCountdown"/>
    /// call). The host wires this to the live sim time.</summary>
    public Func<float> Now { get; set; } = static () => 0f;

    /// <summary>
    /// QC <c>lockteams</c> read accessor (server/teamplay.qc:333): the live admin team-lock state. Used only by
    /// <see cref="MoveToTeam"/> to back up the lock before a forced move and restore it after — so an admin move
    /// goes through even while teams are locked, exactly like QC <c>MoveToTeam</c>. The host wires this to
    /// <c>GameWorld.TeamsLocked</c>. Defaults to "unlocked" (the backup/restore is then inert).
    /// </summary>
    public Func<bool> LockTeamsGet { get; set; } = static () => false;

    /// <summary>
    /// QC <c>lockteams</c> write accessor (server/teamplay.qc:334): set the live admin team-lock state. Paired with
    /// <see cref="LockTeamsGet"/> for <see cref="MoveToTeam"/>'s temporary-disable-then-restore. The host wires
    /// this to <c>GameWorld.TeamsLocked</c>. A no-op until wired.
    /// </summary>
    public Action<bool> LockTeamsSet { get; set; } = static _ => { };

    /// <summary>
    /// QC <c>LogTeamChange(player.playerid, player.team, type)</c> (server/teamplay.qc:293, the <c>SetPlayerTeam</c>
    /// tail): emit the <c>:team:&lt;playerid&gt;:&lt;team&gt;:&lt;type&gt;</c> event-log line on every team set.
    /// Given (player, teamColorCode, TEAM_CHANGE_* type). The host wires this to <c>GameLog.TeamChange</c> (which
    /// already self-gates on <c>sv_eventlog</c> + a valid player id). A no-op until wired.
    /// </summary>
    public Action<Player, int, int>? OnTeamChangeLog { get; set; }

    /// <summary>
    /// QC <c>if (warmup_stage) ReadyCount();</c> (server/teamplay.qc:308-309, the <c>SetPlayerTeam</c> tail): after a
    /// player joins a real team, re-evaluate the warmup ready count since the teams might now be balanced (the
    /// nagger/badteams hold can release). The host wires this to the warmup controller's ReadyCount. A no-op until wired.
    /// </summary>
    public Action? RequestReadyCount { get; set; }

    /// <summary>QC <c>remove_countdown</c>: the active excess-player removal countdown (null = none running).</summary>
    private RemoveCountdownState? _removeCountdown;

    /// <summary>QC the Nagger think throttle: the next sim time the unbalanced-teams center-print may be (re)sent.
    /// The nag is refreshed at 1 Hz (matching the periodic Nagger SendFlags refresh) while teams stay unbalanced.</summary>
    private float _nextTeamNagTime;

    /// <summary>QC <c>nags &amp; BIT(5)</c> latch (server/client.qc): whether the unbalanced-teams center-print is
    /// currently showing, so it stops being re-sent (and thus fades) exactly once when teams re-balance.</summary>
    private bool _teamNagShowing;

    /// <summary>QC the <c>remove_countdown</c> entity state: the player to be removed, its remaining lifetime
    /// (whole seconds), and the next 1-second think time.</summary>
    private sealed class RemoveCountdownState
    {
        public Player Target = null!;   // QC remove_countdown.enemy
        public int Lifetime;            // QC remove_countdown.lifetime (seconds remaining)
        public float NextThink;         // QC remove_countdown.nextthink
    }

    /// <summary>
    /// Construct the team manager. <paramref name="teamCount"/> is clamped to 2..4 for a team game (QC
    /// gametype team count, e.g. g_tdm_teams). <paramref name="scores"/> (optional) is consulted for the
    /// lowest-score tiebreak; without it, ties fall to the lowest-index team like the base helper.
    /// </summary>
    public Teamplay(bool isTeamGame, int teamCount = 2, Scores? scores = null)
    {
        IsTeamGame = isTeamGame;
        TeamCount = isTeamGame ? System.Math.Clamp(teamCount, 2, 4) : 0;
        _scores = scores;
        if (isTeamGame)
            _scores?.SeedTeams(TeamCount);
    }

    /// <summary>Reconfigure the team count at runtime (QC teams_override cvar change). Clamped to 2..4.</summary>
    public void SetTeamCount(int teamCount)
    {
        if (!IsTeamGame)
            return;
        TeamCount = System.Math.Clamp(teamCount, 2, 4);
        _scores?.SeedTeams(TeamCount);
    }

    // ----- forced teams (QC Player_DetermineForcedTeam / Player_HasRealForcedTeam / bot_forced_team) -----

    /// <summary>
    /// QC <c>Team_IndexToTeam</c>: map a 1..4 forced-team index to its color code (other → <see cref="Teams.None"/>).
    /// </summary>
    public static int IndexToTeam(int index) =>
        index >= 1 && index <= Teams.All.Length ? Teams.All[index - 1] : Teams.None;

    /// <summary>
    /// QC <c>Player_DetermineForcedTeam</c>: compute and store <paramref name="player"/>'s forced team from the
    /// server config. In campaign, real clients are pinned to <c>g_campaign_forceteam</c> (1..4); otherwise the
    /// player's crypto id / IP is matched against <c>g_forced_team_{red,blue,yellow,pink}</c> (→ 1..4), falling
    /// back to <c>g_forced_team_otherwise</c> (red|blue|yellow|pink|spectate|default). A non-team game clears any
    /// real forced team. Called once on connect; <see cref="AssignBestTeam"/> later honors the result.
    /// </summary>
    public void DetermineForcedTeam(Player player, bool isCampaign = false)
    {
        int forced = TeamForceDefault;

        if (isCampaign)
        {
            // QC: only real clients (not bots) are campaign-forced.
            if (!player.IsBot)
            {
                int ct = Api.Services is not null ? (int)Api.Cvars.GetFloat("g_campaign_forceteam") : 0;
                forced = (ct >= 1 && ct <= Teams.All.Length) ? ct : TeamForceDefault;
            }
        }
        else if (PlayerInList(player, "g_forced_team_red"))
            forced = 1;
        else if (PlayerInList(player, "g_forced_team_blue"))
            forced = 2;
        else if (PlayerInList(player, "g_forced_team_yellow"))
            forced = 3;
        else if (PlayerInList(player, "g_forced_team_pink"))
            forced = 4;
        else
        {
            string otherwise = Api.Services is not null ? Api.Cvars.GetString("g_forced_team_otherwise") : "default";
            forced = otherwise switch
            {
                "red" => 1,
                "blue" => 2,
                "yellow" => 3,
                "pink" => 4,
                "spectate" or "spectator" => TeamForceSpectator,
                _ => TeamForceDefault,
            };
        }

        // QC: a non-team game clears any real forced team (spectator sentinel survives).
        if (!IsTeamGame && forced > TeamForceDefault)
            forced = TeamForceDefault;

        SetForcedTeam(player, forced);
    }

    /// <summary>QC <c>Player_SetForcedTeamIndex</c>: store a player's forced-team index directly (admin/script override).</summary>
    public void SetForcedTeam(Player player, int index)
    {
        _forcedTeam.Remove(player);
        if (index != TeamForceDefault)
            _forcedTeam.Add(player, index);
    }

    /// <summary>QC <c>Player_GetForcedTeamIndex</c>: the stored forced-team index (default if unset).</summary>
    public int GetForcedTeam(Player player) =>
        _forcedTeam.TryGetValue(player, out object? v) ? (int)v! : TeamForceDefault;

    /// <summary>QC <c>Player_HasRealForcedTeam</c>: true when the player is pinned to an actual team (1..4).</summary>
    public bool HasRealForcedTeam(Player player) => GetForcedTeam(player) > TeamForceDefault;

    /// <summary>
    /// QC <c>.bot_forced_team</c>: pin a bot to a team index (1..4) from its roster config, or clear with 0.
    /// A pinned bot skips auto-balance in <see cref="AssignBestTeam"/>.
    /// </summary>
    public void SetBotForcedTeam(Player bot, int index)
    {
        _botForcedTeam.Remove(bot);
        if (index >= 1 && index <= Teams.All.Length)
            _botForcedTeam.Add(bot, index);
    }

    /// <summary>The bot's forced-team index (1..4), or 0 if unpinned.</summary>
    public int GetBotForcedTeam(Player bot) =>
        _botForcedTeam.TryGetValue(bot, out object? v) ? (int)v! : 0;

    // ----- warmup/match join queue (QC QueueNeeded / TeamBalance_QueuedPlayersTagIn / QueuedPlayersReady) -----

    /// <summary>QC the queued-player set (the observers waiting for a team to open). Each entry stores the player's
    /// preferred team color (0 = any team) and FIFO join order so a deficit is filled oldest-first, specific-team
    /// preference before any-team — matching TeamBalance_QueuedPlayersTagIn's two-pass tag-in.</summary>
    private readonly List<QueuedPlayer> _joinQueue = new();

    /// <summary>QC a queued observer: the player, its preferred team color (0 = any), and an enqueue sequence for FIFO order.</summary>
    private sealed class QueuedPlayer
    {
        public Player Player = null!;   // the waiting observer
        public int PreferTeam;          // preferred team color code, or 0 for "any available team"
        public long Seq;                // enqueue order (oldest first)
    }

    private long _queueSeq;

    /// <summary>QC <c>autocvar_g_balance_teams_queue</c>: hold joiners in a queue during a live match to keep balance.</summary>
    private bool QueueEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_balance_teams_queue") != 0f;

    /// <summary>
    /// QC <c>QueueNeeded</c> (server/teamplay.qc): should <paramref name="joiner"/> be held in the join queue
    /// instead of joining now? Only when <c>g_balance_teams_queue</c> is set, the match has started (not warmup,
    /// not campaign), there are at least two humans, and joining the <paramref name="prefTeam"/> (0 = any) would
    /// leave teams more unbalanced than necessary — i.e. that team isn't a current best team. Returns false (join
    /// immediately) for bots, in warmup, or with the cvar off, so stock servers are unaffected.
    /// </summary>
    public bool QueueNeeded(Player joiner, int prefTeam, IReadOnlyList<Player> roster, bool isWarmup, bool isCampaign)
    {
        if (!IsTeamGame || joiner.IsBot || isWarmup || isCampaign || !QueueEnabled)
            return false;

        // QC: only queue once there are >=2 humans actually playing (a 1-human match never queues).
        int humans = 0;
        for (int i = 0; i < roster.Count; i++)
            if (!roster[i].IsBot && !roster[i].IsObserver && (int)roster[i].Team != Teams.None)
                humans++;
        if (humans < 2)
            return false;

        // QC: a specific-team request is queued when that team isn't a best team; an any-team request is queued
        // when the teams aren't already balanced (a join would tip them). Reuse the same size->strength ladder.
        if (prefTeam != Teams.None)
            return !IsTeamAllowedForSwitch(joiner, prefTeam, roster, isWarmup);
        return SizeDifference(roster) >= 1;
    }

    /// <summary>
    /// QC enqueue: hold <paramref name="joiner"/> as a queued observer with its <paramref name="prefTeam"/>
    /// preference (0 = any), networking the <c>JOIN_PREVENT_QUEUE[_TEAM_*]</c> center-print. Idempotent — a
    /// re-queue just refreshes the preference. The host keeps the player an observer; <see cref="QueuedPlayersTagIn"/>
    /// pulls them back in when a deficit opens.
    /// </summary>
    public void EnqueueJoin(Player joiner, int prefTeam)
    {
        QueuedPlayer? q = null;
        for (int i = 0; i < _joinQueue.Count; i++)
            if (ReferenceEquals(_joinQueue[i].Player, joiner)) { q = _joinQueue[i]; break; }
        if (q is null)
        {
            q = new QueuedPlayer { Player = joiner, Seq = _queueSeq++ };
            _joinQueue.Add(q);
        }
        q.PreferTeam = prefTeam;

        // QC Send_Notification(NOTIF_ONE_ONLY, ..., CENTER_JOIN_PREVENT_QUEUE[_TEAM_<col>]): tell the player they wait.
        if (!joiner.IsBot)
        {
            if (prefTeam != Teams.None)
                NotificationSystem.Send(NotifBroadcast.OneOnly, joiner, MsgType.Center,
                    $"JOIN_PREVENT_QUEUE_TEAM_{TeamSuffix(prefTeam)}");
            else
                NotificationSystem.Send(NotifBroadcast.OneOnly, joiner, MsgType.Center, "JOIN_PREVENT_QUEUE");
        }
    }

    /// <summary>QC: drop <paramref name="player"/> from the join queue (on disconnect, spectate, or a successful tag-in).</summary>
    public void DequeueJoin(Player player)
    {
        for (int i = _joinQueue.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_joinQueue[i].Player, player))
                _joinQueue.RemoveAt(i);
    }

    /// <summary>True while <paramref name="player"/> is sitting in the join queue (host gates respawn/auto-join on this).</summary>
    public bool IsQueued(Player player)
    {
        for (int i = 0; i < _joinQueue.Count; i++)
            if (ReferenceEquals(_joinQueue[i].Player, player))
                return true;
        return false;
    }

    /// <summary>
    /// QC <c>TeamBalance_QueuedPlayersTagIn</c> / <c>QueuedPlayersReady</c> (server/teamplay.qc): while the teams
    /// have a deficit (a smaller team that a join would help balance), pull queued observers back into the match
    /// oldest-first — a player whose preferred team is the deficient one first (the specific-team pass), then any
    /// any-team waiter (the second pass). Each tagged-in player is removed from the queue, assigned the deficient
    /// team via <see cref="AssignBestTeam"/>, and handed to <paramref name="join"/> (the host's PutPlayerInServer).
    /// Call once per server frame; a no-op when the queue is empty or teams are already balanced. Returns the
    /// number tagged in.
    /// </summary>
    public int QueuedPlayersTagIn(IReadOnlyList<Player> roster, Action<Player> join)
    {
        if (_joinQueue.Count == 0 || !IsTeamGame)
            return 0;

        int taggedIn = 0;
        // QC: keep filling while a deficit remains and the queue has a willing player for it.
        while (_joinQueue.Count > 0)
        {
            // QC: the team that most needs a player = the smallest active team, but only if it is strictly
            // smaller than the largest (otherwise teams are balanced and nobody is owed a slot).
            int small = SmallestTeam(roster, out int smallCount);
            if (small == Teams.None)
                break;
            int large = LargestTeamExcluding(roster, new HashSet<int>(), out int largeCount);
            if (large == Teams.None || largeCount - smallCount < 1)
                break; // QC QueuedPlayersReady: teams balanced -> stop tagging in.

            // QC two-pass tag-in: first a queued player who specifically wants the deficient team (oldest first),
            // else the oldest any-team waiter. Specific-team preference wins the slot (the dibs rule).
            QueuedPlayer? pick = null;
            for (int i = 0; i < _joinQueue.Count; i++)
                if (_joinQueue[i].PreferTeam == small && (pick is null || _joinQueue[i].Seq < pick.Seq))
                    pick = _joinQueue[i];
            if (pick is null)
                for (int i = 0; i < _joinQueue.Count; i++)
                    if (_joinQueue[i].PreferTeam == Teams.None && (pick is null || _joinQueue[i].Seq < pick.Seq))
                        pick = _joinQueue[i];
            if (pick is null)
                break; // nobody in the queue wants/accepts the deficient team.

            Player p = pick.Player;
            DequeueJoin(p);
            // QC SetPlayerTeam to the deficient team, then PutPlayerInServer (the host's join callback).
            AssignBestTeam(p, roster); // honors the now-deficient team as the best team
            join(p);
            taggedIn++;
        }
        return taggedIn;
    }

    /// <summary>
    /// QC <c>PlayerInList</c> (client.qc:1047): is the player's crypto id (<see cref="Player.PersistentId"/>) or
    /// IP (<see cref="Player.NetAddress"/>) present in the space-separated cvar list <paramref name="cvar"/>?
    /// Empty list → false.
    /// </summary>
    private static bool PlayerInList(Player player, string cvar)
    {
        if (Api.Services is null)
            return false;
        string list = Api.Cvars.GetString(cvar);
        if (string.IsNullOrEmpty(list))
            return false;
        string[] entries = list.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        foreach (string e in entries)
        {
            if (!string.IsNullOrEmpty(player.PersistentId) && e == player.PersistentId)
                return true;
            if (!string.IsNullOrEmpty(player.NetAddress) && e == player.NetAddress)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Port of <c>TeamBalance_JoinBestTeam</c> → <c>TeamBalance_FindBestTeam</c>: assign <paramref name="joiner"/>
    /// to the smallest active team over <paramref name="roster"/> (ignoring the joiner), breaking ties by the
    /// lower team score, then by the lower team index. Writes <see cref="Common.Framework.Entity.Team"/> and
    /// returns the chosen color code. A no-op returning <see cref="Teams.None"/> for a non-team game.
    /// </summary>
    public int AssignBestTeam(Player joiner, IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
        {
            joiner.Team = Teams.None;
            return Teams.None;
        }

        // QC TeamBalance_JoinBestTeam: a bot pinned by its roster config keeps its team, no auto-balance.
        int botForced = GetBotForcedTeam(joiner);
        if (botForced != 0)
        {
            int forcedColor = IndexToTeam(botForced);
            // QC SetPlayerTeam → SetPlayerColors: set .team AND .clientcolors (the auto-join skips the mutator hook).
            // NO -1 here: QC's `new_team - 1` maps SVQC .team values (5/14/13/10) to color codes; the port's
            // Teams constants (4/13/12/9) ARE the color codes already (r16 green-team bug — a double subtract).
            SetPlayerColors(joiner, forcedColor);
            return forcedColor;
        }

        // QC TeamBalance_CheckAllowedTeams: bot_vs_human bans all-but-one side for bots / the other for humans.
        bool[] allowed = AllowedTeams(joiner);

        // QC: a player with a real forced team joins it iff that team is allowed; otherwise normal balance.
        if (HasRealForcedTeam(joiner))
        {
            int idx = GetForcedTeam(joiner);
            if (idx >= 1 && idx <= TeamCount && allowed[idx - 1])
            {
                int forcedColor = IndexToTeam(idx);
                SetPlayerColors(joiner, forcedColor); // color code, not QC .team — no -1 (see AssignBestTeam note)
                return forcedColor;
            }
        }

        // QC TeamBalance_GetTeamCounts: compute the server skill average once before tallying each team.
        RecomputeServerSkillAverage(roster);
        bool joinerHuman = !joiner.IsBot;

        // QC TeamBalance_FindBestTeams warmup branch: when in warmup AND sv_teamnagger AND g_balance_teams_skill,
        // the "best" team is NOT the smallest/weakest but the team whose weighted-mean skill differs MOST from the
        // joiner's (z-score, significance-gated) — this avoids piling all the weak players on one team when they
        // join in the "wrong" order. Precompute the joiner's own skill mu/var (rated → fed skill at var 1;
        // unranked → the server average at var (avg*0.25)^2) so the per-team z-scores can be taken.
        bool warmupSkillBranch = IsWarmup() && SvTeamnagger() > 0f && SkillWeightingEnabled;
        float joinerMu = 0f, joinerVar = 0f;
        if (warmupSkillBranch)
        {
            if (IsRated(joiner)) { joinerMu = SkillProvider(joiner); joinerVar = SkillVariance; }
            else { joinerMu = _serverSkillAverage; joinerVar = (_serverSkillAverage * 0.25f) * (_serverSkillAverage * 0.25f); }
        }

        // QC TeamBalance_FindBestTeams + TeamBalance_FindBestTeam: gather ALL best (tied) teams, then pick one at
        // RANDOM among them (RandomSelection over the equal-best set) — Base randomizes ties; it does not pick a
        // fixed lowest index. A single best team is returned directly.
        int bestTeam = Teams.None;
        TeamStrength best = default;
        int tieCount = 0;            // QC RandomSelection reservoir count of equal-best teams
        int reservoir = Teams.None;  // the currently-chosen tied team
        bool joinerOnBest = false;   // QC: is the joiner already sitting on one of the best teams?

        int ti = 0;
        foreach (int team in Teams.Active(TeamCount))
        {
            if (!allowed[ti++])
                continue; // QC: a banned team is never a candidate (bot_vs_human partition).

            // QC TeamBalance_CompareTeamsInternal: order teams by player count first, then by team strength
            // (skill+score) as a tiebreak — a smaller team wins outright; equal-size teams break to the weaker.
            // In the warmup-skill branch the comparison is instead the skill-distance rule (CompareForWarmupJoin).
            TeamStrength cand = MeasureTeam(team, roster, joiner, joinerHuman);
            int cmp = bestTeam == Teams.None ? -1
                : warmupSkillBranch ? CompareForWarmupJoin(cand, best, joinerMu, joinerVar)
                : CompareTeams(cand, best);
            if (cmp < 0)
            {
                best = cand;
                bestTeam = team;
                tieCount = 1;
                reservoir = team;
                joinerOnBest = (int)joiner.Team == team;
            }
            else if (cmp == 0)
            {
                // QC RandomSelection_AddFloat(i, 1, 1): uniform reservoir pick among equally-best teams.
                tieCount++;
                if (Prandom.Float() * tieCount < 1f)
                    reservoir = team;
                if ((int)joiner.Team == team)
                    joinerOnBest = true;
            }
        }

        // QC TeamBalance_FindBestTeam: don't punish players for UI mistakes — if the joiner is already on one of
        // the best teams, keep it rather than randomly reshuffling them to another equal-best team.
        if (joinerOnBest && (int)joiner.Team != Teams.None)
            return (int)joiner.Team;

        if (reservoir != Teams.None)
            bestTeam = reservoir;
        if (bestTeam == Teams.None)
            bestTeam = Teams.Red; // degenerate guard (matches TeamBalance.JoinSmallestTeam)

        // QC SetPlayerTeam → SetPlayerColors: set .team AND .clientcolors (the auto-join skips the mutator hook).
        SetPlayerColors(joiner, bestTeam); // bestTeam is a color code already — no -1 (see the bot-forced note above)
        return bestTeam;
    }

    /// <summary>
    /// QC <c>MoveToTeam</c> (server/teamplay.qc:330): force <paramref name="client"/> onto <paramref name="newTeam"/>
    /// (a color code, or <see cref="Teams.None"/>/<see cref="TeamForceSpectator"/> to spectate) as an admin
    /// move, temporarily disabling the team lock for the duration so the move always goes through, then restoring
    /// it. This is the exact QC path: back up <c>lockteams</c>, clear it, run the <c>SetPlayerTeam</c>-equivalent
    /// (<see cref="SetTeam"/> — fires the Player_ChangeTeam/ChangedTeam hooks, a mutator may still veto), then on
    /// success kill + score-clear via <see cref="KillPlayerForTeamChange"/>, and restore the lock on every exit.
    /// Returns false only when the team set itself was vetoed (mutator hook) — matching QC's <c>SetPlayerTeam</c>
    /// false propagation. Admin <c>moveplayer</c>/<c>shuffleteams</c> route through here so they bypass the lock
    /// exactly like Base (the lock is only enforced on the player <c>join</c>/<c>selectteam</c> path).
    /// </summary>
    public bool MoveToTeam(Player client, int newTeam, int type = TeamChangeAdmin)
    {
        // QC SetPlayerTeam: the kill + score-clear only fire on a REAL change (team_index != old_team_index), so
        // snapshot the team before the (lock-disabled) set and skip the kill on a no-op move.
        int oldTeam = (int)client.Team;

        // QC: int lockteams_backup = lockteams; lockteams = 0;
        bool lockBackup = LockTeamsGet();
        LockTeamsSet(false);
        try
        {
            // QC: if (!SetPlayerTeam(client, team_index, type)) return false;
            if (!SetTeam(client, newTeam, type))
                return false;
        }
        finally
        {
            // QC: lockteams = lockteams_backup; (restored on both success and the early-false return)
            LockTeamsSet(lockBackup);
        }

        // QC SetPlayerTeam tail: KillPlayerForTeamChange + PlayerScore_Clear, but only when the team actually changed.
        if ((int)client.Team != oldTeam)
            KillPlayerForTeamChange(client);
        return true;
    }

    /// <summary>QC <c>Team_TeamToIndex</c>: map a team color code (1..4 colors) to its 1..4 index, or 0 for none.</summary>
    public static int TeamToIndex(int team)
    {
        for (int i = 0; i < Teams.All.Length; i++)
            if (Teams.All[i] == team) return i + 1;
        return 0;
    }

    /// <summary>
    /// QC <c>Player_SetTeamIndex</c> (server/teamplay.qc:239): change <paramref name="player"/>'s team to
    /// <paramref name="newTeam"/> (a color code), firing the <c>Player_ChangeTeam</c> mutator hook BEFORE the
    /// change (a handler may return true to BLOCK it) and <c>Player_ChangedTeam</c> AFTER. Early-outs as a no-op
    /// (returning true) when the player is already on that team. Returns false only when a mutator vetoed the
    /// change; the caller (the team-change command / shuffle / autobalance path) should then leave the team be.
    /// This is the single team-set seam that replaces a bare <c>player.Team = ...</c> so mutators can veto/react.
    /// </summary>
    public bool SetTeam(Player player, int newTeam, int type = TeamChangeManual)
    {
        // QC: early-out if already on this team (the "already on this team" guard lives in Player_SetTeamIndex).
        if ((int)player.Team == newTeam)
            return true;

        int oldIndex = TeamToIndex((int)player.Team);
        int newIndex = TeamToIndex(newTeam);

        // QC MUTATOR_CALLHOOK(Player_ChangeTeam, ...) == true → blocked.
        var pre = new MutatorHooks.PlayerChangeTeamArgs(player, oldIndex, newIndex);
        if (MutatorHooks.PlayerChangeTeam.Call(ref pre))
            return false;

        // QC Player_SetTeamIndex tail: a spectator sentinel zeroes clientcolors and sets team = -1; otherwise the
        // team is applied through SetPlayerColors so .team AND .clientcolors stay consistent. QC passes
        // `new_team - 1` because its SVQC .team values (5/14/13/10) sit one above the color codes — the port's
        // Teams constants ARE the color codes (4/13/12/9), so no -1 (the r16 all-green bug: red 4-1=3 = the
        // GREEN palette slot, blue 13-1=12 = yellow; the SetColor round-trip's +1 masked it for team identity).
        if (newTeam == Teams.None || newTeam == TeamForceSpectator)
        {
            player.ClientColors = 0;
            player.Team = newTeam == TeamForceSpectator ? TeamForceSpectator : Teams.None;
        }
        else
        {
            SetPlayerColors(player, newTeam);
        }

        // QC MUTATOR_CALLHOOK(Player_ChangedTeam, ...): react after the change (return value ignored).
        var post = new MutatorHooks.PlayerChangedTeamArgs(player, oldIndex, newIndex);
        MutatorHooks.PlayerChangedTeam.Call(ref post);

        // QC SetPlayerTeam tail (server/teamplay.qc:293-310), run here since this seam IS the change point.
        // LogTeamChange(player.playerid, player.team, type): the :team: event-log line on a real change.
        OnTeamChangeLog?.Invoke(player, (int)player.Team, type);
        // On a join to a REAL team (team_index != -1): broadcast INFO_JOIN_PLAY_TEAM + re-ReadyCount during warmup.
        if (newTeam != Teams.None && newTeam != TeamForceSpectator)
        {
            // QC: Send_Notification(NOTIF_ALL, NULL, MSG_INFO, APP_TEAM_NUM(player.team, INFO_JOIN_PLAY_TEAM), ...).
            NotificationSystem.Info($"JOIN_PLAY_TEAM_{TeamSuffix(newTeam)}", player.NetName);
            // QC: if (warmup_stage) ReadyCount(); — teams might be balanced now.
            if (IsWarmup())
                RequestReadyCount?.Invoke();
        }
        return true;
    }

    /// <summary>QC <c>APP_TEAM_NUM</c> team-name suffix (common/teams.qh) for the JOIN_PLAY_TEAM_&lt;col&gt;
    /// notification names. Falls back to RED for an unteamed/neutral value so the lookup always resolves.</summary>
    private static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "RED",
    };

    /// <summary>
    /// QC <c>setcolor</c> (server/teamplay.qc:190): store <paramref name="player"/>'s packed
    /// <c>clientcolors</c> and derive the team from it — in a team game <c>team = (clr &amp; 15) + 1</c> (the low
    /// nibble + 1 maps to a team color code), in FFA <c>team = -1</c>. The single low-level color sink.
    /// </summary>
    public void SetColor(Player player, int clr)
    {
        player.ClientColors = clr;
        // QC setcolor: in teamplay `self.team = (clr & 15) + 1` re-derives the SVQC .team value from the pants
        // nibble. The port's Team field holds the COLOR CODE itself (Teams.Red/Blue = 4/13 — QC's team-1), so the
        // low nibble maps back with NO +1. (The old `+ 1` paired with the SetPlayerColors `- 1` — the two
        // cancelled for team identity while painting everyone the wrong palette slot: the r16 all-green bug.)
        player.Team = IsTeamGame ? (clr & 15) : TeamForceSpectator;
    }

    /// <summary>
    /// QC <c>SetPlayerColors</c> (server/teamplay.qc:225): split <paramref name="color"/> into pants (low nibble)
    /// and shirt (high nibble); in a team game force BOTH to the team color (<c>16*pants + pants</c>) so the player
    /// model is fully team-colored, in FFA keep the player's chosen <c>shirt + pants</c>. Routes through
    /// <see cref="SetColor"/>, which also re-derives <c>.team</c>.
    /// </summary>
    public void SetPlayerColors(Player player, int color)
    {
        int pants = color & 0x0F;
        int shirt = color & 0xF0;
        if (IsTeamGame)
            SetColor(player, 16 * pants + pants);
        else
            SetColor(player, shirt + pants);
    }

    /// <summary>
    /// QC <c>SV_ChangeTeam</c> (server/teamplay.qc:1340): the engine <c>color</c> command hook. Only re-colors the
    /// player in a NON-team game (in teamplay the color is owned by the team, so a manual recolor is ignored).
    /// </summary>
    public void ChangeTeam(Player player, int newColor)
    {
        if (!IsTeamGame)
            SetPlayerColors(player, newColor);
    }

    /// <summary>
    /// QC <c>g_balance_teams_skill</c> gate: whether to weigh team sizes by player skill rather than raw
    /// count. True only when the cvar is set; without TrueSkill ratings the significance threshold is always
    /// considered met (every player contributes its <see cref="SkillProvider"/> rating).
    /// </summary>
    private bool SkillWeightingEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_balance_teams_skill") != 0f;

    /// <summary>QC <c>autocvar_timelimit &gt; 0 ? autocvar_timelimit * 60 : 20 * 60</c>: the match length in
    /// seconds used to ramp the score-strength bias (a 0/unset timelimit falls back to 20 minutes).</summary>
    private static float TimeLimitSeconds()
    {
        float tl = Api.Services is not null ? Api.Cvars.GetFloat("timelimit") : 0f;
        return tl > 0f ? tl * 60f : 20f * 60f;
    }

    /// <summary>
    /// QC the per-team balance entity (teamplay.qc:840-931): the player count plus the inverse-variance weighted
    /// mean skill (<c>m_skill_mu</c>, <c>m_skill_var = 1/mass</c>) and the team score, used by
    /// <see cref="CompareTeams"/> to order candidate teams (size first, then skill+score strength).
    /// </summary>
    private readonly struct TeamStrength
    {
        public readonly int Count;     // QC m_num_players
        public readonly float SkillMu; // QC m_skill_mu (inverse-variance weighted mean skill; 0 = empty team)
        public readonly float SkillVar;// QC m_skill_var (= 1/mass; 0 = no rated players)
        public readonly int Score;     // QC m_team_score

        public TeamStrength(int count, float skillMu, float skillVar, int score)
        {
            Count = count; SkillMu = skillMu; SkillVar = skillVar; Score = score;
        }
    }

    /// <summary>
    /// QC <c>m_skill_mu &amp;&amp; m_skill_var</c>: whether a player carries a real (TrueSkill) rating. The port has
    /// no TrueSkill, so a bot's fed <see cref="SkillProvider"/> value counts as "rated" and every human is
    /// "unranked" (its assumed skill is derived from the server average via the unranked factor, exactly like QC).
    /// </summary>
    private bool IsRated(Player p) => p.IsBot && SkillProvider(p) != 0f;

    /// <summary>
    /// QC <c>TeamBalance_GetTeamCounts</c>'s <c>server_skill_average</c> pre-pass: the inverse-variance weighted
    /// mean skill of all rated clients in <paramref name="roster"/>, scaled by
    /// <c>g_balance_teams_skill_unranked_factor</c>. Stored in <see cref="_serverSkillAverage"/> for the per-team
    /// unranked contribution. Falls back to QC's 1000 when nobody is rated (or the factor is ≤ 0).
    /// </summary>
    private void RecomputeServerSkillAverage(IReadOnlyList<Player> roster)
    {
        float muSum = 0f, weightSum = 0f;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (!IsRated(p)) continue;
            float w = 1f / SkillVariance;
            muSum += SkillProvider(p) * w;
            weightSum += w;
        }
        float factor = Api.Services is not null ? Api.Cvars.GetFloat("g_balance_teams_skill_unranked_factor") : 0f;
        _serverSkillAverage = (weightSum != 0f && factor > 0f) ? factor * muSum / weightSum : 1000f;
    }

    /// <summary>
    /// QC teamplay.qc:840-931 (TeamBalance_GetTeamCounts inner loop): tally a team's player count and its
    /// inverse-variance weighted mean skill, plus its current score. Rated players (bots) contribute their
    /// <see cref="SkillProvider"/> rating at weight <c>1/var</c>; unranked players (humans) contribute the
    /// server-average skill (<see cref="_serverSkillAverage"/>) at weight <c>1/(avg*0.25)^2</c> — the QC
    /// down-weighting of clients with no rating. <c>m_skill_mu = muSum/mass</c>, <c>m_skill_var = 1/mass</c>.
    /// Pass <see cref="RecomputeServerSkillAverage"/> first. Excludes <paramref name="ignore"/> (the joining
    /// player is scored against the team it would join, not counted as already on it). <paramref name="joinerHuman"/>
    /// selects net counts (humans deduct leavable bots) per QC's IS_REAL_CLIENT size branch.
    /// </summary>
    private TeamStrength MeasureTeam(int team, IReadOnlyList<Player> roster, Player? ignore, bool joinerHuman)
    {
        int count = 0, bots = 0;
        float muSum = 0f, mass = 0f;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (ReferenceEquals(p, ignore)) continue;
            if ((int)p.Team != team) continue;
            count++;
            if (p.IsBot) bots++;
            if (IsRated(p))
            {
                float weight = 1f / SkillVariance; // QC skill_weight = 1 / m_skill_var
                muSum += SkillProvider(p) * weight;
                mass += weight;
            }
        }
        // QC: each unranked client (here: humans) uses the server-average skill at the unranked weight.
        int unranked = count - RatedCount(team, roster, ignore);
        if (unranked > 0)
        {
            float avgVar = (_serverSkillAverage * 0.25f) * (_serverSkillAverage * 0.25f);
            float w = avgVar != 0f ? 1f / avgVar : 0f;
            muSum += _serverSkillAverage * w * unranked;
            mass += w * unranked;
        }
        float skillMu = mass > 0f ? muSum / mass : 0f;     // QC m_skill_mu /= mass
        float skillVar = mass > 0f ? 1f / mass : 0f;       // QC m_skill_var = 1 / mass
        int score = _scores?.TeamScore(team) ?? 0;

        // QC m_num_players_net: a human joiner sees the team size minus the bots that would leave to make room
        // (one leavable bot per team), so a team that is all-bots reads as smaller to a human. A bot joiner uses
        // the raw count. We approximate bots_would_leave as 1 deductible bot per non-empty bot-holding team.
        int size = joinerHuman ? count - System.Math.Min(bots, 1) : count;
        return new TeamStrength(size, skillMu, skillVar, score);
    }

    /// <summary>Count the rated (bot) players on a team, excluding <paramref name="ignore"/> — the rated half of the
    /// per-team tally, used to derive the unranked client count for the skill-average contribution.</summary>
    private int RatedCount(int team, IReadOnlyList<Player> roster, Player? ignore)
    {
        int n = 0;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (ReferenceEquals(p, ignore)) continue;
            if ((int)p.Team != team) continue;
            if (IsRated(p)) n++;
        }
        return n;
    }

    /// <summary>
    /// QC <c>TeamBalance_CompareTeamsInternal</c> (teamplay.qc:1299): order two candidate teams for a joiner —
    /// negative if <paramref name="a"/> is the better (preferable, "lesser") team. Player count dominates; on a
    /// tie, team strength (skill mu + score) breaks it, but skill only counts when the gap is statistically
    /// significant (z-score > <c>g_balance_teams_skill_significance_threshold</c> SDs, squared for perf), exactly
    /// like Base. Falls back to the lowest fixed index when fully equal (the foreach keeps the first seen).
    /// </summary>
    private int CompareTeams(in TeamStrength a, in TeamStrength b)
    {
        if (a.Count != b.Count)
            return a.Count < b.Count ? -1 : 1; // QC: fewer players => preferable.

        // QC: equal size — compare skill+score "strength". Skill applies only when significant.
        bool skillEnabled = SkillWeightingEnabled;
        float threshold = (Api.Services is not null)
            ? Api.Cvars.GetFloat("g_balance_teams_skill_significance_threshold")
            : 0f;
        bool useSkill = skillEnabled && a.SkillVar != 0f && b.SkillVar != 0f
            && ((a.SkillMu - b.SkillMu) * (a.SkillMu - b.SkillMu)) / (a.SkillVar + b.SkillVar)
               > threshold * threshold;

        float strengthA = useSkill ? a.SkillMu : 1f;
        float strengthB = useSkill ? b.SkillMu : 1f;

        // QC TeamBalance_CompareTeamsInternal score ramp: early in a match scores are ~random (too few samples),
        // so the team_score ratio is only applied once the match has started and ramped in with
        // min(1, (time-game_starttime)/timelimit)^1.5. A team with score 0 is treated as 0.96875 (31/32) so a
        // team with 1 point reads as stronger than one with 0 (but only slightly, sample size being small).
        // Applying the full ratio to team A alone is equivalent to applying half to each. While in warmup / before
        // the match clock starts the ramp is skipped entirely (strength stays the pure count/skill comparison).
        if (!IsWarmup() && SecondsSinceMatchStart() > 0f)
        {
            float timelimitSec = TimeLimitSeconds();
            float scoreA = a.Score != 0 ? a.Score : 0.96875f;
            float scoreB = b.Score != 0 ? b.Score : 0.96875f;
            float ramp = MathF.Pow(MathF.Min(1f, SecondsSinceMatchStart() / timelimitSec), 1.5f);
            strengthA *= 1f + (scoreA / scoreB - 1f) * ramp;
        }

        if (strengthA < strengthB) return -1; // QC: weaker team => preferable.
        if (strengthA > strengthB) return 1;
        return 0;
    }

    /// <summary>QC <c>autocvar_sv_teamnagger</c> (default 2): the team-size-diff nag threshold, which also gates the
    /// warmup skill-aware join branch. 0 disables both. Reads the registered cvar (0 when services are unwired).</summary>
    private static float SvTeamnagger() =>
        Api.Services is not null ? Api.Cvars.GetFloat("sv_teamnagger") : 0f;

    /// <summary>
    /// QC <c>TeamBalance_FindBestTeams</c> warmup branch (teamplay.qc:1036-1067): order two candidate teams for a
    /// joiner by how FAR each team's weighted-mean skill is from the joiner's (z-score), so a joiner is pushed to
    /// the team whose skill profile differs most — this spreads skill instead of clumping noobs by join order.
    /// Returns negative if <paramref name="a"/> is the better warmup pick. The QC rule: if the skill gap between
    /// the teams isn't significant (|z_a - z_b| ≤ threshold) prefer the smaller team; otherwise (significant OR
    /// equal size) prefer the team with the larger |z| (further from the joiner). Equal |z| and equal size → tie
    /// (the reservoir then randomizes among them). <paramref name="jMu"/>/<paramref name="jVar"/> are the joiner's
    /// own skill mu/var.
    /// </summary>
    private int CompareForWarmupJoin(in TeamStrength a, in TeamStrength b, float jMu, float jVar)
    {
        float threshold = Api.Services is not null
            ? Api.Cvars.GetFloat("g_balance_teams_skill_significance_threshold")
            : 0f;

        // QC: z = (team.m_skill_mu - player_skill_mu) / sqrt(team.m_skill_var + player_skill_var).
        float zaRaw = SafeZ(a.SkillMu - jMu, a.SkillVar + jVar);
        float zbRaw = SafeZ(b.SkillMu - jMu, b.SkillVar + jVar);
        bool significant = MathF.Abs(zaRaw - zbRaw) > threshold; // QC: |z_a - z_b| > threshold
        float za = MathF.Abs(zaRaw); // QC: fabs AFTER the significance test (joiner may sit between the teams).
        float zb = MathF.Abs(zbRaw);

        // QC: (!significant && a_size < b_size) || ((significant || a_size == b_size) && z_a > z_b) → a is better.
        if ((!significant && a.Count < b.Count)
            || ((significant || a.Count == b.Count) && za > zb))
            return -1;
        // QC: z_a == z_b && a_size == b_size → equal (accumulate as a tied best team).
        if (za == zb && a.Count == b.Count)
            return 0;
        return 1;
    }

    /// <summary>z = num / sqrt(denom), guarding a non-positive denominator (no rated players) → 0 (no skill signal).</summary>
    private static float SafeZ(float num, float denom) => denom > 0f ? num / MathF.Sqrt(denom) : 0f;

    /// <summary>QC <c>TeamBalance_GetNumberOfPlayers</c>: count the roster on a given team.</summary>
    public int CountTeam(int team, IReadOnlyList<Player> roster) => TeamBalance.CountTeam(team, roster);

    /// <summary>
    /// QC <c>TeamBalance_IsTeamAllowed</c> / the "are teams uneven?" check used by autobalance: true when the
    /// largest active team has at least two more players than the smallest (the QC threshold for moving a
    /// player). Returns false for a non-team game or fewer than two players.
    /// </summary>
    public bool TeamsAreUneven(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
            return false;
        int min = int.MaxValue, max = int.MinValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            int c = TeamBalance.CountTeam(team, roster);
            if (c < min) min = c;
            if (c > max) max = c;
        }
        return max != int.MinValue && (max - min) >= 2;
    }

    /// <summary>
    /// QC <c>TeamBalance_AutoBalanceBots</c>: if the teams are uneven by ≥2 players, move the
    /// <em>lowest-scoring</em> bot from the largest team to the smallest (QC
    /// <c>TeamBalance_GetPlayerForTeamSwitch</c> picks the lowest SP_SCORE). Returns the moved player, or null
    /// if nothing needed moving / no movable bot exists. Faithful to QC: only bots are moved (never yank a
    /// human across teams mid-match), and the lowest scorer goes so the strongest players stay put. The caller
    /// should follow up with <see cref="KillPlayerForTeamChange"/> + a re-spawn for the moved bot.
    /// </summary>
    public Player? AutoBalanceBots(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
            return null;

        // QC: find the smallest team, then the largest; bail if they're the same or the gap is < 2.
        int smallTeam = SmallestTeam(roster, out int smallCount);
        if (smallTeam == Teams.None)
            return null;

        // walk teams from largest down until we find one with a movable bot and a ≥2 gap (QC the while loop).
        int remainingTeams = TeamCount;
        var consideredLargest = new HashSet<int>();
        while (consideredLargest.Count < remainingTeams)
        {
            int largeTeam = LargestTeamExcluding(roster, consideredLargest, out int largeCount);
            if (largeTeam == Teams.None || largeTeam == smallTeam)
                return null;
            if (largeCount - smallCount < 2)
                return null; // QC: stop once the largest remaining team is within 1 of the smallest

            Player? bot = LowestScoringBotOnTeam(roster, largeTeam);
            if (bot is not null)
            {
                // QC SetPlayerTeam(bot, smallest, TEAM_CHANGE_AUTO): the move fires the team-change hooks (a mutator
                // may veto). If vetoed, try the next-largest team instead of forcing the move.
                if (SetTeam(bot, smallTeam, TeamChangeAuto))
                    return bot;
                consideredLargest.Add(largeTeam);
                continue;
            }
            consideredLargest.Add(largeTeam); // no movable bot here; try the next-largest team
        }
        return null;
    }

    /// <summary>
    /// QC <c>KillPlayerForTeamChange</c>: when a player is moved across teams mid-match, kill them
    /// (a self-kill that clears their auxiliary score columns so the move doesn't unfairly carry kills/deaths
    /// to the new team) and re-spawn them on the new side. The death type distinguishes a MANUAL switch
    /// (DEATH_TEAMCHANGE — the suicide DOES negate a frag) from an AUTO-balance move
    /// (<paramref name="autoBalance"/> → DEATH_AUTOTEAMCHANGE — server/damage.qc:304 skips the frag negation,
    /// since the player didn't choose to switch). Returns true if the player was alive (and thus killed).
    /// </summary>
    public bool KillPlayerForTeamChange(Player p, bool autoBalance = false)
    {
        // QC PlayerScore_Clear on a team change: reset this player's aux score columns.
        _scores?.Row(p).ClearForTeamChange();

        if (p.IsDead)
            return false;

        // QC server/teamplay.qc:1249: `Damage(player, player, player, 100000, DEATH_*TEAMCHANGE.m_id, ...)`
        // attacker == targ → SUICIDE branch. AUTOTEAMCHANGE (the auto-balance move) is special-cased in
        // damage.qc:304 to NOT negate a frag; a manual TEAMCHANGE does. The port passes the player as attacker
        // (self-kill) matching Base exactly; the gametype/scoring suicide path reads the death type to gate the −1.
        string deathType = autoBalance ? DeathTypes.AutoTeamChange : DeathTypes.TeamChange;
        Combat.Damage(p, null, p, 100000f, deathType, p.Origin, System.Numerics.Vector3.Zero);
        return true;
    }

    /// <summary>
    /// QC <c>TeamBalance_CheckAllowedTeams</c> (the bot_vs_human branch): which of the active teams (in
    /// <see cref="Teams.Active"/> order, indexed 0..TeamCount-1) <paramref name="forWhom"/> may join. Normally all
    /// teams are allowed; with <c>bot_vs_human</c> set on a 2-team match, bots are banned to one side and humans
    /// to the other (positive ratio → bots take the LAST team / humans the first; negative → bots the FIRST team
    /// / humans the last), so the join only ever offers the "correct" side. <paramref name="forWhom"/> null (a
    /// global query) leaves all teams allowed.
    /// </summary>
    private bool[] AllowedTeams(Player? forWhom)
    {
        var allowed = new bool[TeamCount];
        for (int i = 0; i < TeamCount; i++) allowed[i] = true;

        if (forWhom is null || TeamCount != 2 || Api.Services is null)
            return allowed;

        float ratio = Api.Cvars.GetFloat("bot_vs_human");
        if (ratio == 0f)
            return allowed;

        // QC: positive → bots take the LAST available team (index 1), humans the first (index 0).
        //      negative → bots take the FIRST available team (index 0), humans the last (index 1).
        bool botsLast = ratio > 0f;
        int botTeamIdx = botsLast ? 1 : 0;
        int humanTeamIdx = botsLast ? 0 : 1;
        int keep = forWhom.IsBot ? botTeamIdx : humanTeamIdx;
        for (int i = 0; i < TeamCount; i++)
            allowed[i] = (i == keep);
        return allowed;
    }

    /// <summary>
    /// QC <c>TeamBalance_SizeDifference</c>: the gap between the largest and smallest active team's player count
    /// over <paramref name="roster"/> (0 for a non-team game). Used by the unbalanced-teams nag and the warmup
    /// hold (warmup won't end while teams are this uneven).
    /// </summary>
    public int SizeDifference(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
            return 0;
        int min = int.MaxValue, max = int.MinValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            int c = TeamBalance.CountTeam(team, roster);
            if (c < min) min = c;
            if (c > max) max = c;
        }
        return max == int.MinValue ? 0 : max - min;
    }

    /// <summary>QC <c>Net_TeamNagger</c> (server/client.qc:143 Nagger SendFlags bit 5): the hardcoded
    /// unbalanced-teams center-print string the client renders. Base shows a fixed localized line (no registered
    /// notification template / cpid), so the port pushes the same literal via <see cref="NotificationSystem.SendCenterRaw"/>.</summary>
    private const string TeamNagText = "^1Teams are unbalanced!";

    /// <summary>
    /// QC the Nagger network entity (server/client.qc:143, SendFlags bit 5 -&gt; client Net_TeamNagger): broadcast
    /// the unbalanced-teams center-print to every client while <see cref="TeamsUnbalancedForNag"/> holds, refreshed
    /// at 1 Hz (matching QC's periodic re-send) so the (groupless) raw center print does not time out while the
    /// imbalance persists. The moment teams re-balance the re-send stops, so the line fades on its own — mirroring
    /// the client retracting the nag when SendFlags bit 5 clears. Call once per server frame; it self-throttles via
    /// <see cref="_nextTeamNagTime"/> and the <see cref="_teamNagShowing"/> latch so it neither spams the wire nor
    /// re-sends needlessly. A no-op when <c>sv_teamnagger</c> is 0 / not a team game (the trigger self-gates).
    /// </summary>
    public void SendTeamNag(IReadOnlyList<Player> roster)
    {
        // QC: the nag is only networked while the size gap is at least sv_teamnagger; otherwise stop re-sending it.
        if (!TeamsUnbalancedForNag(roster))
        {
            // QC: the client drops the nag once SendFlags bit 5 clears. The raw center print carries no cpid group
            // to actively kill, so we simply stop refreshing it — it fades when the next re-send doesn't arrive.
            _teamNagShowing = false;
            return;
        }

        // QC: refresh the center-print roughly once a second (the Nagger entity re-sends on its think cadence) so a
        // plain (groupless) center print does not time out while the imbalance persists.
        float now = Now();
        if (_teamNagShowing && now < _nextTeamNagTime)
            return;
        _nextTeamNagTime = now + 1f;
        _teamNagShowing = true;
        NotificationSystem.SendCenterRaw(NotifBroadcast.All, null, TeamNagText);
    }

    /// <summary>
    /// QC <c>sv_teamnagger</c> trigger: true when the team-size gap (<see cref="SizeDifference"/>) is at least
    /// <c>sv_teamnagger</c> players (Base default 2), i.e. the unbalanced-teams nag should show and warmup should
    /// be held open. Returns false when <c>sv_teamnagger</c> is 0 (nagger disabled) or for a non-team game.
    /// </summary>
    public bool TeamsUnbalancedForNag(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame || Api.Services is null)
            return false;
        // QC: (teamplay && total_players && autocvar_sv_teamnagger) ? ... : false — sv_teamnagger 0 disables the
        // nag (and the warmup badteams hold). The cvar is registered (default 2), so an explicit 0 is honored.
        float nagger = Api.Cvars.GetFloat("sv_teamnagger");
        if (nagger <= 0f)
            return false;
        return SizeDifference(roster) >= (int)nagger;
    }

    /// <summary>
    /// QC <c>g_balance_teams_prevent_imbalance</c> mid-match switch gate (cmd.qc:746): may
    /// <paramref name="switcher"/> manually switch to <paramref name="targetTeam"/> (a color code) right now? When
    /// the cvar is set and not in warmup, a human may only switch to a team that is a <em>best</em> team (smallest
    /// active team after the move, by the same count/score rule as <see cref="AssignBestTeam"/>), so they can't
    /// jump to the larger/stronger side. Bots, warmup, and a disabled cvar always allow the switch. The caller
    /// (the team/join command handler) should reject the switch when this returns false.
    /// </summary>
    public bool IsTeamAllowedForSwitch(Player switcher, int targetTeam, IReadOnlyList<Player> roster, bool isWarmup)
    {
        if (!IsTeamGame || switcher.IsBot || isWarmup)
            return true;
        if (Api.Services is null || Api.Cvars.GetFloat("g_balance_teams_prevent_imbalance") == 0f)
            return true;

        bool[] allowed = AllowedTeams(switcher);
        RecomputeServerSkillAverage(roster);
        bool switcherHuman = !switcher.IsBot;

        // QC TeamBalance_FindBestTeams: compute the best (by the size→strength ladder) candidate, then accept the
        // target only if it ties that best — i.e. switching there doesn't make teams more unbalanced.
        bool haveBest = false;
        TeamStrength best = default;
        TeamStrength target = default;
        bool haveTarget = false;

        int ti = 0;
        foreach (int team in Teams.Active(TeamCount))
        {
            bool isAllowed = allowed[ti++];
            if (!isAllowed)
            {
                if (team == targetTeam) return false; // banned team (bot_vs_human) is never switchable.
                continue;
            }
            TeamStrength cand = MeasureTeam(team, roster, switcher, switcherHuman);
            if (!haveBest || CompareTeams(cand, best) < 0)
            {
                best = cand;
                haveBest = true;
            }
            if (team == targetTeam)
            {
                target = cand;
                haveTarget = true;
            }
        }

        // Target ties the best team (CompareTeams == 0 means equally preferable) → allow.
        return haveTarget && haveBest && CompareTeams(target, best) <= 0;
    }

    // ----- balance scan helpers (QC TeamBalance_GetLargestTeamIndex / GetPlayerForTeamSwitch) -----

    private int SmallestTeam(IReadOnlyList<Player> roster, out int count)
    {
        int best = Teams.None, bestCount = int.MaxValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            int c = TeamBalance.CountTeam(team, roster);
            if (c < bestCount) { bestCount = c; best = team; }
        }
        count = bestCount == int.MaxValue ? 0 : bestCount;
        return best;
    }

    private int LargestTeamExcluding(IReadOnlyList<Player> roster, HashSet<int> exclude, out int count)
    {
        int best = Teams.None, bestCount = int.MinValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            if (exclude.Contains(team)) continue;
            int c = TeamBalance.CountTeam(team, roster);
            if (c > bestCount) { bestCount = c; best = team; }
        }
        count = bestCount == int.MinValue ? 0 : bestCount;
        return best;
    }

    private Player? LowestScoringBotOnTeam(IReadOnlyList<Player> roster, int team)
    {
        Player? lowest = null;
        int lowestScore = int.MaxValue;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (!p.IsBot || (int)p.Team != team) continue;
            int score = p.ScoreFrags;
            if (score < lowestScore) { lowestScore = score; lowest = p; }
        }
        return lowest;
    }

    // ----- excess-player removal (QC TeamBalance_RemoveExcessPlayers / Remove_Countdown) -----

    /// <summary>QC <c>autocvar_g_balance_teams_remove</c>: move the newest excess player to spectators on a leave.</summary>
    private bool RemoveExcessEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_balance_teams_remove") != 0f;

    /// <summary>QC <c>autocvar_g_balance_teams_remove_wait</c> (default 10): warning seconds before the move (0 = now).</summary>
    private int RemoveWaitSeconds =>
        Api.Services is not null ? (int)Api.Cvars.GetFloat("g_balance_teams_remove_wait") : 10;

    /// <summary>
    /// QC <c>TeamBalance_RemoveExcessPlayers</c> (server/teamplay.qc:702): on a leave that unbalances a 2-team
    /// match, move the NEWEST joiner (highest <see cref="Player.StartPlayTime"/>) on each overfull team to
    /// spectators — after a <c>g_balance_teams_remove_wait</c>-second countdown nag (or immediately if 0).
    /// Only runs in a 2-team game, not campaign, and only when <c>g_balance_teams_remove</c> is set. Called from
    /// the disconnect path (the leaver is already out of <paramref name="roster"/>) and re-entrantly from the
    /// countdown's expiry. <paramref name="isCampaign"/> mirrors QC's <c>autocvar_g_campaign</c> guard.
    /// </summary>
    public void RemoveExcessPlayers(IReadOnlyList<Player> roster, bool isCampaign = false)
    {
        // QC: if (AVAILABLE_TEAMS != 2 || autocvar_g_campaign) return;
        if (!IsTeamGame || TeamCount != 2 || isCampaign || !RemoveExcessEnabled)
            return;

        // QC: min = the smallest team's m_num_players.
        int min = int.MaxValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            int cur = TeamBalance.CountTeam(team, roster);
            if (cur < min) min = cur;
        }
        if (min == int.MaxValue)
            return;

        // QC: for each team with excess (cur > 0 && cur > min), pick the newest joiner and move it to spec.
        foreach (int team in Teams.Active(TeamCount))
        {
            int cur = TeamBalance.CountTeam(team, roster);
            if (cur <= 0 || cur <= min)
                continue;

            // QC: newest joiner on this team = max CS(it).startplaytime.
            Player? latest = null;
            float latestTime = 0f;
            for (int i = 0; i < roster.Count; i++)
            {
                Player p = roster[i];
                if ((int)p.Team != team)
                    continue;
                if (latest is null || p.StartPlayTime > latestTime)
                {
                    latestTime = p.StartPlayTime;
                    latest = p;
                }
            }
            if (latest is null)
                continue;

            int wait = RemoveWaitSeconds;
            if (wait > 0)
            {
                // QC: a warning countdown before moving to spectate (CENTER_MOVETOSPEC_REMOVE with the wait COUNT).
                if (_removeCountdown is null)
                {
                    _removeCountdown = new RemoveCountdownState { NextThink = Now() };
                    NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "MOVETOSPEC_REMOVE",
                        latest.NetName, wait);
                }
                _removeCountdown.Target = latest;
                _removeCountdown.Lifetime = wait;
            }
            else
            {
                // QC: move to spectators immediately (INFO_MOVETOSPEC_REMOVE then PutObserverInServer).
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "MOVETOSPEC_REMOVE", latest.NetName);
                MoveToSpectator?.Invoke(latest);
            }
        }
    }

    /// <summary>
    /// QC <c>Remove_Countdown</c> (server/teamplay.qc:677): the 1-second think driving the excess-player removal
    /// nag. Once the lifetime runs out (or the teams become even again), the queued player is moved to spectators
    /// (only if the lifetime actually expired), the center nag is retracted, and the excess check re-runs in case
    /// someone else also left during the countdown. Call once per server frame; it self-gates to a 1 Hz cadence.
    /// </summary>
    public void TickRemoveCountdown(IReadOnlyList<Player> roster, bool isCampaign = false)
    {
        if (_removeCountdown is null)
            return;

        float now = Now();
        if (now < _removeCountdown.NextThink)
            return;

        // QC Remove_Countdown: fire when the lifetime is spent OR the teams equalised again (someone re-balanced).
        if (_removeCountdown.Lifetime <= 0 || SizeDifference(roster) == 0)
        {
            if (_removeCountdown.Lifetime <= 0)
            {
                // QC: lifetime expired → announce + actually move the player to spectators.
                Player target = _removeCountdown.Target;
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "MOVETOSPEC_REMOVE", target.NetName);
                MoveToSpectator?.Invoke(target);
            }

            // QC: Kill_Notification(..., MSG_CENTER, CPID_REMOVE) — retract the running countdown center print.
            NotificationSystem.SendCenterKill(NotifBroadcast.All, null, "CPID_REMOVE");
            _removeCountdown = null;

            // QC: re-check for excess in case someone also left while in countdown.
            RemoveExcessPlayers(roster, isCampaign);
            return;
        }

        // QC: --lifetime; nextthink = time + 1.
        _removeCountdown.Lifetime--;
        _removeCountdown.NextThink = now + 1f;
    }
}
