using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Server;

/// <summary>
/// The client roster + connect/spawn lifecycle — the Godot-free essence of the relevant slice of
/// server/client.qc (<c>ClientConnect</c> / <c>PutClientInServer</c> / <c>ClientDisconnect</c>) plus the
/// <c>Join</c> path that turns an observer into a player. It owns the live <see cref="Player"/> list,
/// registers each player with the engine entity table and the <see cref="Scores"/> table, assigns a team
/// via <see cref="Teamplay"/>, spawns through <see cref="SpawnSystem"/>, and fires the
/// <see cref="MutatorHooks.PlayerSpawn"/> hook chain — keeping the gametype's roster (the
/// <see cref="MatchController"/>) in sync.
///
/// QC connection actually transmutes an edict Observer→Player on join; this port models a client as a
/// <see cref="Player"/> from the start (the entity model promotes player state onto the subclass, ADR-0007),
/// with an <see cref="ClientInfo.IsConnected"/> flag standing in for FL_CLIENT and a bot flag standing in
/// for IS_BOT_CLIENT. Clients are registered into the server-side entity registry
/// (<see cref="ServerEntities"/>) on connect so <c>FindByClass("player")</c> / <c>FindInRadius</c> see them
/// (the entity-table fix), and each carries server-only per-player state (<see cref="PlayerStates"/>) for
/// the regen/drown/contents timers.
///
/// The deep plumbing (netcode handshakes, cvar stuffing, spectator/observer states, playerstats, bans,
/// handicap) is deferred.
/// </summary>
public sealed class ClientManager
{
    /// <summary>Per-client bookkeeping (QC the ClientState component on the edict).</summary>
    public sealed class ClientInfo
    {
        public Player Player { get; }
        public bool IsBot { get; }

        /// <summary>QC FL_CLIENT: the slot is occupied by a connected client.</summary>
        public bool IsConnected { get; internal set; } = true;

        /// <summary>QC <c>.jointime</c>: sim time the client joined as a player (0 while observing).</summary>
        public float JoinTime { get; internal set; }

        /// <summary>Edge tracker for the observer's +attack (SpectateNext) press, so a HELD key cycles once.</summary>
        public bool SpecNextReleased { get; internal set; } = true;

        /// <summary>Edge tracker for the observer's +attack2 (drop to free-fly) press.</summary>
        public bool SpecFreeflyReleased { get; internal set; } = true;

        /// <summary>
        /// QC <c>CS(this).version_nagtime</c> (server/client.qc:1151, 2925): timer for version-mismatch
        /// notifications. Set to 10-20 seconds after client connect; when the timer fires, a notification is
        /// sent if the client's <c>g_xonoticversion</c> differs from the server's. The timer is then zeroed
        /// to fire only once. 0 = timer has fired or not yet armed.
        /// </summary>
        public float VersionNagTime { get; internal set; } = 0f;

        public ClientInfo(Player player, bool isBot)
        {
            Player = player;
            IsBot = isBot;
            player.IsBot = isBot;
        }
    }

    /// <summary>
    /// QC <c>CS_CVAR(this).cvar_cl_clippedspectating</c> (server/client.qc:2589): a free-fly spectator's per-client
    /// "pass through walls?" preference. <c>false</c> (the default, <c>cl_clippedspectating 0</c>) = <see
    /// cref="MoveType.Noclip"/> (free flight, no collision); <c>true</c> = <see cref="MoveType.FlyWorldOnly"/>
    /// (collide with the world). Wired by <c>Commands(GameWorld)</c> to the replicated-cvar store; null/unwired →
    /// the Base default (noclip), preserving the previous no-net-state behaviour for tests/headless paths.
    /// </summary>
    public static System.Func<Player, bool>? ClippedSpectatingProvider { get; set; }

    /// <summary>
    /// Set by the host while the map-editor gametype is active: forces free-fly observers to MOVETYPE_NOCLIP
    /// regardless of <see cref="ClippedSpectatingProvider"/>. Editing while colliding with the level is
    /// needlessly fiddly, and a brush dragged around you would otherwise trap the camera.
    /// </summary>
    public static bool? EditorFreeFlyNoclip { get; set; }

    private readonly List<ClientInfo> _clients = new();
    private readonly List<Player> _players = new();          // dense Player view (matches SimulationLoop.Clients)
    private readonly SimulationLoop _sim;
    private readonly Scores _scores;
    private readonly Teamplay _teamplay;
    private readonly MatchController _match;

    /// <summary>The connected clients (alive or awaiting respawn).</summary>
    public IReadOnlyList<ClientInfo> Clients => _clients;

    /// <summary>The connected players (the <see cref="Player"/> view of <see cref="Clients"/>).</summary>
    public IReadOnlyList<Player> Players => _players;

    // WS1 stage 3 (sv_threaded): a copy-on-write roster snapshot for CROSS-THREAD readers. The live _players
    // List is mutated only on the sim thread (join/leave/bot add-remove); the render thread's display reads
    // (NetGame.LocalServerPlayer, HUD mirrors) enumerate THIS immutable array instead — a List being resized
    // mid-foreach throws, an old array is merely one roster-change stale. Rebuilt at the two mutation sites.
    private volatile Player[] _playersSnapshot = System.Array.Empty<Player>();

    /// <summary>Immutable roster snapshot, safe to enumerate from any thread (may be one change stale).</summary>
    public Player[] PlayersSnapshot => _playersSnapshot;

    private void RefreshPlayersSnapshot() => _playersSnapshot = _players.ToArray();

    /// <summary>QC <c>player_count</c>: how many clients are connected.</summary>
    public int PlayerCount => _clients.Count;

    /// <summary>QC <c>currentbots</c>: how many connected clients are bots.</summary>
    public int BotCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _clients.Count; i++)
                if (_clients[i].IsBot) n++;
            return n;
        }
    }

    /// <summary>How many connected clients are humans (not bots).</summary>
    public int HumanCount => _clients.Count - BotCount;

    public ClientManager(SimulationLoop sim, Scores scores, Teamplay teamplay, MatchController match)
    {
        _sim = sim;
        _scores = scores;
        _teamplay = teamplay;
        _match = match;
    }

    /// <summary>
    /// The server-side entity registry that makes clients visible to find/radius (the entity-table fix). Set
    /// by <see cref="GameWorld"/> at boot. When set, every connecting client is registered here so
    /// <c>FindByClass("player")</c> / <c>FindInRadius</c> return it (QC the client edict joins the entity list);
    /// when null, clients stay out of those queries (the legacy behavior).
    /// </summary>
    public ServerEntityService? ServerEntities { get; set; }

    /// <summary>Server-only per-player state table (regen/drown/contents timers). Set by <see cref="GameWorld"/>.</summary>
    public ServerPlayerStates? PlayerStates { get; set; }

    /// <summary>QC <c>warmup_stage</c> query (wired by <see cref="GameWorld"/> to the WarmupController): when true,
    /// <see cref="Spawn"/> gives the warmup loadout (100/100/all-guns) instead of the live start loadout.</summary>
    public Func<bool>? IsWarmup { get; set; }

    /// <summary>
    /// On a single-player listen server hosting a campaign level, let the local host auto-spawn instead of
    /// being held as an observer by <c>g_campaign</c>. The listen server is "host AND play" and already forces
    /// the auto-join for Create Game via <c>sv_spectate 0</c>; campaign adds <c>g_campaign 1</c> (needed for
    /// PreInit/limits/win detection), whose spectator-hold (QC PlayerPreThink) would otherwise leave the host
    /// spectating the loaded level. Set by the host only for the single-player campaign case.
    /// </summary>
    public bool CampaignHostAutojoin { get; set; }

    /// <summary>
    /// Fired after a client is registered (QC ClientConnect tail) — the host wires it to the §5 connect hooks
    /// (ban enforcement, the <c>:connect:</c>/<c>:join:</c> event-log lines, player-stats AddPlayer, anticheat
    /// init, timeout-allowance reset). Fires before the initial <see cref="Spawn"/>.
    /// </summary>
    public Action<Player>? OnClientConnect { get; set; }

    /// <summary>Fired after a successful (re)spawn (QC PutPlayerInServer tail) — alivetime start, view reset.</summary>
    public Action<Player>? OnClientSpawn { get; set; }

    /// <summary>
    /// Fired at the head of <see cref="Spawn"/>, BEFORE spawnpoint selection (QC the spawn_evalfunc / forced-spot
    /// setup that runs ahead of SelectSpawnPoint). Lets the active gametype set a one-shot forced spawn
    /// (<see cref="Player.SpawnPointTarg"/>) — e.g. Race returning a respawning racer to their last checkpoint.
    /// </summary>
    public Action<Player>? OnPreSpawn { get; set; }

    /// <summary>Fired before a client is removed (QC ClientDisconnect head) — finalize stats, <c>:part:</c>, cleanup.</summary>
    public Action<Player>? OnClientDisconnect { get; set; }

    /// <summary>Fired AFTER a client is fully removed from the roster (QC the tail of ClientDisconnect, e.g.
    /// <c>TeamBalance_RemoveExcessPlayers(this)</c> at client.qc:3057) — the leaver is already out of the roster.</summary>
    public Action? OnAfterClientDisconnect { get; set; }

    /// <summary>
    /// QC <c>anticheat_spectatecopy</c> (the last line of server/client.qc <c>SpectateCopy</c>, client.qc:1837):
    /// after the regular spectate mirror, a following observer's body angle inherits the spectatee's
    /// evade-tracked view angle (<c>anticheat_div0_evade_v_angle</c>) instead of the raw angle. Wired by
    /// <see cref="GameWorld"/> to <c>AntiCheat.SpectateCopy</c>; null = no override (raw angle stands).
    /// </summary>
    public Action<Player, Player>? SpectateAngleCopy { get; set; }

    /// <summary>
    /// [T39] Fired after a BOT client has connected and auto-joined (QC bot_clientconnect + havocbot_setupbot:
    /// the hook where the bot gets its AI). GameWorld wires this to <c>BotPopulation.RegisterBot</c> so EVERY
    /// bot connect path (fixcount fill, console bot_add, a host's direct ClientConnect) grows a brain. Fired
    /// after the auto-Join so the spawned hull/view are real when the brain snapshots them.
    /// </summary>
    public Action<Player>? OnBotConnected { get; set; }

    private int _nextPlayerId = 1;

    /// <summary>
    /// QC <c>ClientConnect</c> + <c>Join</c>, reduced: create the player edict, register it everywhere
    /// (entity table, sim client list, score table, gametype roster), assign a team for a team game, then
    /// spawn it via <see cref="Spawn"/> (which fires the PlayerSpawn hook). Returns the new client's info.
    /// <paramref name="isBot"/> sets the simple bot-vs-human flag (QC IS_BOT_CLIENT).
    /// </summary>
    public ClientInfo ClientConnect(bool isBot = false, string? netName = null)
    {
        // Build the player edict through the engine entity table so it gets a slot/index, then re-tag it as
        // a Player (the table's Spawn() yields a bare Entity; in this port a client IS a Player subclass, so
        // we construct the Player and register it into the same dense list the table manages).
        Player p = new() { NetName = netName ?? (isBot ? "bot" : "player") };
        p.PlayerId = _nextPlayerId++;        // QC .playerid: a per-match unique id for the event log / stats
        if (isBot) p.NetAddress = "bot";
        RegisterEntity(p);

        var info = new ClientInfo(p, isBot);
        _clients.Add(info);
        _players.Add(p);
        RefreshPlayersSnapshot();

        // engine sim: clients are simulated first each tick (SimulationLoop.Clients / ClientMove).
        p.Flags |= EntFlags.Client;
        if (!_sim.Clients.Contains(p))
            _sim.Clients.Add(p);

        // score row (QC PlayerScore_Attach).
        _scores.Register(p);

        // gametype roster (QC the player joins the active gametype's player set).
        _match.AddPlayer(p);

        // QC the gametype's GameRules_spawning_teams(...) call (sv_rules / each mode's init): tell the spawn
        // system whether this mode requests team-only spawnpoints, so SelectSpawnPoint filters spots to the
        // player's team. Idempotent — runs the first time the gametype is known.
        EnsureTeamSpawnsRequested();

        // QC ClientConnect: Player_DetermineForcedTeam(this) (server/client.qc:1162) — runs for EVERY connecting
        // client, before the team balance. In campaign it pins a real client to g_campaign_forceteam (1..4);
        // otherwise it matches the player's id/IP against the g_forced_team_* lists. AssignBestTeam honors the
        // stored forced team (Player_HasRealForcedTeam). Without this call the _forcedTeam table is never
        // populated, so g_campaign_forceteam / the forced-team lists had no effect.
        _teamplay.DetermineForcedTeam(p, isCampaign: Api.Cvars.GetFloat("g_campaign") != 0f);

        // team assignment for a team game (QC TeamBalance_JoinBestTeam); FFA leaves Team = None.
        if (_teamplay.IsTeamGame)
            _teamplay.AssignBestTeam(p, _players);

        info.JoinTime = Now;
        // QC ClientConnect (server/client.qc:1151): arm the version-nagging timer 10-20 seconds after connect,
        // so the version check fires once during the player's session (PlayerPreThink @ 2925-2943).
        info.VersionNagTime = Now + 10f + (float)new System.Random().NextDouble() * 10f;

        // QC ClientConnect ends by TRANSMUTE(Observer, this) (server/client.qc:1164): a connecting client is an
        // OBSERVER, not yet a live player — it only enters the match via Join() (on +jump/+attack, a delayed
        // autojoin, or a bot's auto-join). Mark the observer phase here; the loadout/spawn happen in Join().
        p.IsObserver = true;
        // QC ClientConnect -> PutObserverInServer(this, false, use_spawnpoint=true) (server/client.qc:1900):
        // a connecting observer is placed AT A SPAWN POINT, not left at the world origin. The port skipped this
        // because a fresh edict already sits at '0 0 0', which is usually inside solid or out in the void — so
        // anyone who observes before joining (and, in the editor gametype, anyone who never joins at all) starts
        // buried in geometry looking at nothing.
        ApplyObserverHull(p);
        PlaceObserverAtSpawnPoint(p);
        p.WantsJoin = 0;            // QC this.wants_join = 0 (no team chosen yet; 0 = autoselect on Join)
        p.AutoJoinChecked = 0;      // QC .autojoin_checked: not yet attempted
        p.JoinJumpReleased = true;  // re-armed so the first +jump/+attack press fires Join once
        // QC TRANSMUTE(Observer) leaves the client with the spectator .frags sentinel so IS_PLAYER-style checks
        // exclude it from the live roster; PutPlayerInServer resets it to FRAGS_PLAYER on Join.
        p.FragsStatus = Player.FragsSpectator;

        // An observer is connected (FL_CLIENT) but inert: it must not move, take damage, or be picked as a spawn
        // target until joined. Keep it non-solid + MOVETYPE_NONE (QC PutObserverInServer sets these) so the
        // per-tick physics step is a no-op on the un-spawned hull. (See the GameWorld OnClientMove note: the
        // canMove gate should also exclude observers — reported as the one external change.)
        p.MoveType = MoveType.None;
        p.Solid = Solid.Not;
        p.TakeDamage = DamageMode.No;

        // QC ClientConnect tail: the §5 connect hooks (ban enforce, event log, stats, anticheat, timeout).
        OnClientConnect?.Invoke(p);
        // a ban enforcement may have dropped the client already — don't spawn a removed client.
        if (!info.IsConnected)
            return info;

        // QC ObserverOrSpectatorThink: a bot auto-joins ONCE on its first observer think (it never spectates).
        // Mirror that here so a bot drops into the match immediately, exactly as before this lifecycle landed.
        // A human stays an observer until it presses fire / the delayed autojoin trips (ObserverOrSpectatorThink
        // / PlayerPreThink), both driven per-tick by the net layer (ServerNet) for real clients.
        if (isBot)
        {
            p.AutoJoinChecked = 1; // QC CS(this).autojoin_checked = 1
            // QC: a forced-spectate client (g_forced_team_otherwise "spectate") stays an observer even when it's a
            // bot — don't auto-join it into the match.
            if (!(_teamplay.IsTeamGame && _teamplay.GetForcedTeam(p) == Teamplay.TeamForceSpectator))
                Join(p);
            OnBotConnected?.Invoke(p); // [T39] QC bot_clientconnect: hand the new bot its AI brain
        }

        return info;
    }

    /// <summary>
    /// QC <c>Join</c> (server/client.qc:2074): transmute an observing client into a live <see cref="Player"/> and
    /// put it in the server (the loadout/placement happens in <see cref="Spawn"/> → PutPlayerInServer). Clears the
    /// observer phase + join intent. Team selection / queue balancing is deferred; the core path autoselects the
    /// team (already assigned at connect for a team game) and spawns. Returns whether the spawn succeeded.
    /// </summary>
    public bool Join(Player p)
    {
        // QC Join: TRANSMUTE(Player, this) — leave the observer phase. Spawn() (PutPlayerInServer) resets MoveType
        // WALK / Solid SLIDEBOX / DAMAGE_AIM, so we only need to clear the observer marker + intent here.
        p.IsObserver = false;
        p.Spectatee = null;       // stop following anyone (entering the match as a live player)
        p.SpectateeStatus = 0;
        p.WantsJoin = 0;          // QC this.wants_join = 0
        p.JoinJumpReleased = true;

        // QC PutClientInServer (server/client.qc:776-780): when a player was a spectator (killcount ==
        // FRAGS_SPECTATOR) PlayerScore_Clear is called on the (re)join — gated by g_score_resetonjoin (default
        // 0 = no-op; 1 = always clear; -1 = clear unless PreferPlayerScore_Clear hook vetoes). This is the live
        // arm of the resetonjoin feature: at the default (0) it is a no-op, so joiners keep their score; only
        // diverges when an admin explicitly sets 1 or -1.
        XonoticGodot.Common.Gameplay.Scoring.GameScores.ClearPlayerOnJoin(p);

        // QC the gametype PutClientInServer / lms_AddPlayer seed: register the joiner with the gametype (LMS seeds
        // its lives column + clamps a late joiner to the lowest life count) BEFORE the spawn loadout is applied.
        GametypeOnJoin?.Invoke(p);

        bool spawned = Spawn(p); // QC PutClientInServer
        if (!spawned)
        {
            // no spawnpoint yet (Spawn armed a retry); fall back to the observer phase so the player isn't a
            // half-live actor with no placement. The retry path will re-Join via the respawn driver.
            p.IsObserver = true;
            return false;
        }

        // QC Join tail (server/client.qc:2135-2149): once the client is a live player, announce the join. FFA
        // broadcasts INFO_JOIN_PLAY ("<name> is now playing"); a team game centerprints CENTER_JOIN_PLAY_TEAM_<col>
        // to the joiner. The queue-conflict (CENTER_JOIN_PLAY_TEAM_QUEUECONFLICT) + queued-player ANNCE_BEGIN paths
        // depend on the unmodeled join-queue (wants_join / player_with_dibs) and stay deferred.
        if (!_teamplay.IsTeamGame)
            NotificationSystem.Info("JOIN_PLAY", p.NetName);
        else
            NotificationSystem.Center(p, $"JOIN_PLAY_TEAM_{TeamSuffix((int)p.Team)}");

        return true;
    }

    /// <summary>QC <c>APP_TEAM_NUM</c> team-name suffix (common/teams.qh) used by the join/scoring notifications
    /// (JOIN_PLAY_TEAM_RED, …). Falls back to RED for an unteamed/neutral value so the lookup always resolves.</summary>
    private static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "RED",
    };

    /// <summary>QC <c>MIN_SPEC_TIME</c> (server/client.qh:403): the minimum observer dwell before a join is allowed.</summary>
    public const float MinSpecTime = 1f;

    /// <summary>
    /// QC <c>joinAllowed</c> (server/client.qc:2258), reduced core: a client may join once it has observed for at
    /// least <see cref="MinSpecTime"/>, teams aren't admin-locked, and it is not play-banned (forced-spectate).
    /// The version-mismatch / g_maxping / queue-balance gates are deferred (those subsystems aren't modeled here).
    /// </summary>
    public bool JoinAllowed(Player p, float now)
    {
        ClientInfo? info = InfoOf(p);
        if (info is null) return false;
        if (now < info.JoinTime + MinSpecTime) // QC: time < jointime + MIN_SPEC_TIME
            return false;
        if (_teamplay.IsTeamGame && TeamsLocked) // QC: teamplay && lockteams
            return false;
        // QC Player_DetermineForcedTeam -> TEAM_FORCE_SPECTATOR (g_forced_team_otherwise "spectate"): an
        // unlisted client whose forced-team resolved to the spectator sentinel is pinned as an observer and may
        // never join the match (server/client.qc joinAllowed reads team_forced). The campaign / id-list real-team
        // branch is honored separately by AssignBestTeam; this is the spectate-routing half.
        if (_teamplay.IsTeamGame && _teamplay.GetForcedTeam(p) == Teamplay.TeamForceSpectator)
            return false;
        // QC Join_Try: a play-banned (forced-spectate) client may not join (CENTER_JOIN_PLAYBAN). Wired by the
        // host to Bans.IsPlayBanned; null until wired (e.g. headless test harness) so the gate is a no-op there.
        if (ForcedSpectate is not null && ForcedSpectate(p))
            return false;
        // QC the gametype ForbidSpawn gate (LMS lms_AddPlayer lockout): refuse the late join when the gametype
        // denies it (e.g. LMS once the match is locked to new joiners). No-op when unwired.
        if (GametypeJoinGate is not null && !GametypeJoinGate(p))
        {
            // QC MUTATOR_HOOKFUNCTION(ca, PutClientInServer): mark the refused mid-round joiner
            // INGAME_STATUS_JOINING and send INFO_CA_JOIN_LATE so they know to wait for next round.
            GametypeOnJoinRefused?.Invoke(p);
            return false;
        }
        return true;
    }

    /// <summary>QC <c>lockteams</c>: admin team-lock; mirrored from <see cref="GameWorld.TeamsLocked"/> by the host.</summary>
    public bool TeamsLocked { get; set; }

    /// <summary>QC the play-ban (forced-spectate) join gate: is this client on <c>g_playban_list</c>? Injected by
    /// the host (Bans.IsPlayBanned, which lives in the Server ban layer); when set, <see cref="JoinAllowed"/>
    /// refuses the join so a play-banned offender can't press fire / autojoin back into the match.</summary>
    public Func<Player, bool>? ForcedSpectate { get; set; }

    /// <summary>
    /// QC the gametype's <c>ForbidSpawn</c> / late-join gate (e.g. LMS lms_AddPlayer): may this observer become a
    /// live player right now? Injected by the host; when set and it returns false the <see cref="Join"/> is refused
    /// (the client stays an observer). LMS uses this for its mid-match lives lockout. Null ⇒ no gametype gate.
    /// </summary>
    public Func<Player, bool>? GametypeJoinGate { get; set; }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, PutClientInServer)</c> side-channel: called when the gametype gate
    /// (<see cref="GametypeJoinGate"/>) specifically refuses a join attempt, so the gametype can mark the
    /// would-be joiner as INGAME_STATUS_JOINING and send the "you'll play next round" notice. Injected by
    /// the host for round-based gametypes (CA) that need to know about refused mid-round joins; null elsewhere.
    /// Unlike <see cref="GametypeOnJoin"/> (which fires on a SUCCESSFUL join), this fires on the path where
    /// the gametype gate returned false.
    /// </summary>
    public Action<Player>? GametypeOnJoinRefused { get; set; }

    /// <summary>
    /// QC the gametype's <c>PutClientInServer</c> / lms_AddPlayer seed: called when an observer successfully joins
    /// as a live player, BEFORE the spawn, so the gametype can register the joiner (e.g. seed LMS lives on the
    /// scoreboard / clamp a late joiner to the lowest life count). Injected by the host. Null ⇒ no seed.
    /// </summary>
    public Action<Player>? GametypeOnJoin { get; set; }

    /// <summary>
    /// QC <c>ObserverOrSpectatorThink</c> + the delayed-autojoin slice of <c>PlayerPreThink</c>
    /// (server/client.qc:2501 / :2708) — the per-tick observer lifecycle for ONE client. Drives the
    /// join-on-fire gate and the real-client delayed autojoin; the net layer calls this each tick for every
    /// connected client (passing that client's current attack/jump buttons). A no-op once the client has joined
    /// (it's then a live player driven by the normal movement step). The +jump/+attack join and the delayed
    /// autojoin are NOT blocked by warmup/countdown (QC lets players join during warmup — a join during the
    /// pre-match countdown simply spawns the player frozen until game_starttime).
    /// </summary>
    /// <param name="p">The client to think for.</param>
    /// <param name="jumpHeld">QC PHYS_INPUT_BUTTON_JUMP this tick.</param>
    /// <param name="attackHeld">QC PHYS_INPUT_BUTTON_ATCK this tick (cycle to the next spectatee).</param>
    /// <param name="attack2Held">QC PHYS_INPUT_BUTTON_ATCK2 this tick (drop back to free-fly observing).</param>
    public void ObserverOrSpectatorThink(Player p, bool jumpHeld, bool attackHeld, bool attack2Held = false)
    {
        ClientInfo? info = InfoOf(p);
        if (info is null || !info.IsConnected || !p.IsObserver)
            return;

        float now = Now;
        bool withinGrace = now < info.JoinTime + MinSpecTime; // QC: time < jointime + MIN_SPEC_TIME

        // QC ObserverOrSpectatorThink: +jump = JOIN. FL_JUMPRELEASED gates it so a HELD key fires once. (Unlike
        // the older port, +attack no longer joins — it cycles the spectatee, matching QC; the listen-server
        // autojoin below still spawns the host without any keypress.)
        if (p.JoinJumpReleased)
        {
            if (jumpHeld && (JoinAllowed(p, now) || withinGrace))
            {
                p.JoinJumpReleased = false;
                if (JoinAllowed(p, now))
                {
                    Join(p);
                    return;
                }
                // inside the grace window: keep trying (QC autojoin_checked = -1) so the queued press lands.
                p.AutoJoinChecked = -1;
            }
        }
        else if (!jumpHeld)
        {
            p.JoinJumpReleased = true; // QC: re-arm once the key is released
        }

        // QC ObserverOrSpectatorThink spectate controls (server/client.qc:2540-2614): +attack = SpectateNext
        // (follow the next living player), +attack2 = drop back to free-fly. Gated on !withinGrace so the
        // pre-spawn connect window (before the autojoin) doesn't briefly snap the camera to another player.
        if (!withinGrace)
        {
            if (info.SpecNextReleased)
            {
                if (attackHeld)
                {
                    info.SpecNextReleased = false;
                    // QC ObserverOrSpectatorThink (client.qc:2544-2545): sv_spectate 2 forbids following a SPECIFIC
                    // player (free-fly is still allowed) outside warmup — show CENTER_SPECTATE_SPEC_NOTALLOWED
                    // instead of cycling. vote_master (an admin override) isn't modeled, so this applies to all.
                    if (Api.Cvars.GetFloat("sv_spectate") == 2f && !(IsWarmup?.Invoke() ?? false))
                        NotificationSystem.Center(p, "SPECTATE_SPEC_NOTALLOWED");
                    else
                        SpectateNext(p);
                }
            }
            else if (!attackHeld) { info.SpecNextReleased = true; }

            if (info.SpecFreeflyReleased)
            {
                if (attack2Held) { info.SpecFreeflyReleased = false; StopSpectating(p); }
            }
            else if (!attack2Held) { info.SpecFreeflyReleased = true; }
        }

        // QC SpectateUpdate (run each tick while following): mirror the spectatee's view/state onto this
        // observer so the owner-state snapshot (origin/health/armor) tracks the player being watched, and drop
        // back to free-fly if the spectatee left / died / itself became an observer.
        if (p.Spectatee is { } spec)
        {
            if (InfoOf(spec) is null || spec.IsObserver || spec.IsDead)
                StopSpectating(p);
            else
                SpectateCopy(p, spec);
        }

        // QC PlayerPreThink free-fly branch (server/client.qc:2588-2593): a NOT-following (free-fly) spectator's
        // movetype is recomputed each tick from its per-client cl_clippedspectating preference —
        // `wouldclip ? MOVETYPE_FLY_WORLDONLY : MOVETYPE_NOCLIP`. The Base default (cl_clippedspectating 0) is
        // NOCLIP (pass through walls); the port previously hardcoded FLY_WORLDONLY (always clipped), the opposite.
        // Honor the replicated cvar here so a spectator who sets it gets the right collision behaviour. (The QC
        // momentary +use toggle is a transient cosmetic override and is not modelled; the persistent preference is.)
        // MoveType.None is included deliberately: a client that has connected but not yet joined sits at
        // MOVETYPE_NONE (see ClientConnect), and this gate previously only PROMOTED an observer that was
        // already flying — so a never-joining client stayed frozen in place forever. That was invisible while
        // every client auto-joined within a second of connecting, and total in the editor gametype, where
        // nobody ever joins. QC gives a connecting observer the free-fly movetype in PutObserverInServer
        // (client.qc:261) precisely so a spectator can move before/without joining.
        if (p.Spectatee is null && p.MoveType is MoveType.Noclip or MoveType.FlyWorldOnly or MoveType.None)
        {
            // The EDITOR always flies free. cl_clippedspectating is a spectator-watching preference; a mapper
            // needs to pass through the geometry they are editing (and to get back out of a brush they just
            // moved around themselves), so the editor gametype ignores it.
            bool wouldClip = EditorFreeFlyNoclip is null or false
                ? ClippedSpectatingProvider?.Invoke(p) ?? false
                : false;
            p.MoveType = wouldClip ? MoveType.FlyWorldOnly : MoveType.Noclip;
        }

        // QC PlayerPreThink delayed autojoin (server/client.qc:2708): a REAL client that hasn't joined autojoins
        // once MIN_SPEC_TIME has elapsed, UNLESS sv_spectate / g_campaign hold it as a spectator. (g_maxping /
        // forced-team gates are deferred.) Bots autojoined at connect so this is the human path.
        if (p.IsBot || p.AutoJoinChecked > 0 || withinGrace)
            return;

        bool earlyJoinRequested = p.AutoJoinChecked < 0; // QC: a queued press during the grace window
        // QC holds an unjoined real client as a spectator while sv_spectate OR g_campaign is set; the listen
        // server clears the former (sv_spectate 0) and, for a single-player campaign, opts the host out of the
        // latter so it spawns straight into the level instead of spectating it (CampaignHostAutojoin).
        bool campaignHold = Api.Cvars.GetFloat("g_campaign") != 0f && !CampaignHostAutojoin;
        bool spectateOnly = Api.Services is not null
            && (Api.Cvars.GetFloat("sv_spectate") != 0f || campaignHold);
        // QC: autojoin unless held as a spectator (sv_spectate / g_campaign). Not gated on warmup (joinable).
        if (earlyJoinRequested || !spectateOnly)
        {
            p.AutoJoinChecked = 1; // QC CS(this).autojoin_checked = 1
            if (JoinAllowed(p, now))
            {
                Join(p);
                // Join can still fail (e.g. no usable spawn point yet) and re-set IsObserver — keep retrying
                // rather than latching off permanently with AutoJoinChecked == 1.
                if (p.IsObserver && !spectateOnly)
                    p.AutoJoinChecked = -1;
            }
            else if (!spectateOnly)
                p.AutoJoinChecked = -1; // QC: keep trying for MIN_SPEC_TIME (brief blockers)
        }
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, ClientCommand_Spectate)</c> (sv_clanarena.qc): intercept the <c>spectate</c>
    /// command for an in-game CA player. CA force-spectates them (MUT_SPECCMD_FORCE) and sends INFO_CA_LEAVE; the
    /// gametype records the leave so they re-enter next round. Returns true if CA consumed the command (so the
    /// generic spectate path doesn't also run its default notice). No-op (returns false) for non-CA gametypes or a
    /// client that is already spectating. Call from the spectate-command handler BEFORE the generic force-observe.
    /// </summary>
    public bool GametypeSpectateCommand(Player p)
    {
        if (_match.GameType is ClanArena ca)
            return ca.OnSpectateCommand(p);
        return false;
    }

    /// <summary>
    /// QC <c>PutObserverInServer</c> (server/client.qc:261): turn a live player (or a connecting client) into a
    /// free-fly OBSERVER — hide its model, strip its weapons, make it non-solid + non-damageable, give it the
    /// free-fly movetype (so <c>PlayerPhysics.SpectatorControl</c> flies it), and mark it a spectator on the
    /// scoreboard. Used by the <c>spectate</c> command (the live-player→observer direction QC has but the port
    /// lacked) and any forced-spectate path. The observer keeps its current origin (free-flies from there).
    /// </summary>
    /// <summary>
    /// QC <c>PutObserverInServer</c>'s <c>use_spawnpoint</c> branch (server/client.qc): put a free-fly observer
    /// at a spawn point, facing the way that spawn faces.
    ///
    /// Deliberately NOT applied by <see cref="PutObserverInServer"/> itself: the live-player→observer direction
    /// must keep the player's current position (that is what makes `spectate` drop you where you were standing,
    /// and what the editor's PLAYTEST→EDIT toggle relies on). Only the connect-time path wants relocation.
    /// </summary>
    /// <summary>
    /// QC <c>PutObserverInServer</c>'s hull setup (client.qc:318-320): an observer gets the CROUCH hull and a
    /// ZERO view offset — <c>setsize(this, PL_CROUCH_MIN, PL_CROUCH_MAX); this.view_ofs = '0 0 0'</c>.
    ///
    /// Both halves matter and the port had neither, because its connect path never ran the observer transition.
    /// The zero view offset is why the free-fly camera sat at standing eye height instead of at the entity
    /// origin (QC's own comment: so your view doesn't end up in the ceiling under MOVETYPE_FLY_WORLDONLY), and
    /// the full-size hull is why a free-flying observer collided like a standing player.
    /// </summary>
    public static void ApplyObserverHull(Player p)
    {
        ArgumentNullException.ThrowIfNull(p);
        p.ViewOfs = Vector3.Zero;
        if (Api.Services is not null)
            Api.Entities.SetSize(p, new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 25f)); // QC PL_CROUCH_MIN/MAX
    }

    public void PlaceObserverAtSpawnPoint(Player p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (Api.Services is null)
            return;

        SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(p, LivePlayers(), targetCheck: false);
        if (sp is not { } spot)
            return;

        Api.Entities.SetOrigin(p, spot.Origin);
        // Level the view: a spawn point's roll is never meaningful, and a tilted observer camera reads as a bug.
        var angles = new Vector3(0f, spot.Angles.Y, 0f);
        p.Angles = angles;
        p.FixAngle = true;
        p.FixAngleAngles = angles;
    }

    public void PutObserverInServer(Player p)
    {
        // QC PutObserverInServer (server/client.qc:268-273): if it WAS a live player with health, puff a despawn
        // EFFECT_SPAWN at its body before stripping it. Snapshot the prior state before we flip IsObserver below.
        // (VoteCount/ReadyCount recount on demote stays deferred — owned by the vote/ready subsystem.)
        if (!p.IsObserver && p.GetResource(ResourceType.Health) >= 1f)
            EffectEmitter.Emit("SPAWN", p.Origin, Vector3.Zero, 1);

        // QC status_effects MakePlayerObserver hook (sv_status_effects.qc:94-112): drop the demoted player's
        // own status effects (NORMAL removal for the removal sounds) then CLEAR the leftover state. The port has
        // no separate statuseffects_store entity for spectators, so the SpectateCopy effect-aliasing is N/A.
        StatusEffectsCatalog.RemoveAll(p, StatusEffectRemoval.Normal);
        StatusEffectsCatalog.ClearAll(p);

        p.IsObserver = true;
        ApplyObserverHull(p);

        // QC MUTATOR_HOOKFUNCTION(<mode>, MakePlayerObserver) (e.g. ctf_RemovePlayer): a player demoted to
        // observer relinquishes any objective they hold — a CTF carrier drops the flag where they stand and any
        // flag back-referencing them (pass sender/target, dropper) is cleared — so the objective can never stay
        // stuck on a now-spectating player. Default is a no-op for non-objective modes.
        _match.GameType?.OnPlayerRemoved(p);

        // QC MUTATOR_CALLHOOK(MakePlayerObserver, player) (the same dispatch CTF/KeyHunt/Keepaway etc. ride):
        // notify the mutators a live player was demoted to observer so they can drop per-player state. Dodging
        // (sv_dodging.qc:328 dodging_ResetPlayer) clears its dodging_* fields here so a player who spectates
        // mid-dodge doesn't keep stale state into a re-join.
        //
        // QC PutObserverInServer (server/client.qc): .frags = FRAGS_SPECTATOR is assigned BEFORE the
        // MUTATOR_CALLHOOK(MakePlayerObserver) fires, so a mode's MakePlayerObserver hook can OVERRIDE the
        // default sentinel — CTS/Race set FRAGS_PLAYER_OUT_OF_GAME for an observer who already holds a ranked
        // time (Cts.OnMakePlayerObserver, sv_cts.qc:194). The default must therefore precede the hook call;
        // otherwise the later assignment clobbers the hook's override and the ranked-observer flag is dead.
        p.FragsStatus = Player.FragsSpectator;   // QC RES_HEALTH/.frags = FRAGS_SPECTATOR (scoreboard sentinel)

        var observerArgs = new MutatorHooks.MakePlayerObserverArgs(p);
        MutatorHooks.MakePlayerObserver.Call(ref observerArgs);

        // QC PutObserverInServer (server/client.qc:322-323): RemoveGrapplingHooks(this); Portal_ClearAll(this).
        // A player demoted to observer must drop any latched grappling-hook chain (which would otherwise stay
        // attached through the relocation and reel the free-fly camera) and tear down any portals they placed
        // (a spectator owning live portals is a stuck-objective hazard). Hook removal is a Common.Gameplay
        // function; Portal_ClearAll is a host hook (null on a bare/test host → a no-op, exactly like death).
        Hook.RemoveGrapplingHooks(p);
        Porto.PortalClearAll?.Invoke(p);

        p.Spectatee = null;
        p.SpectateeStatus = 0;
        // (FragsStatus default is now set BEFORE the MakePlayerObserver hook above so a mode's hook — CTS's
        //  FRAGS_PLAYER_OUT_OF_GAME for a ranked observer — can override it; QC PutObserverInServer order.)

        // QC: solid=SOLID_NOT, takedamage=DAMAGE_NO, MOVETYPE_FLY_WORLDONLY (free-fly), FL_CLIENT|FL_NOTARGET.
        p.DeadState = DeadFlag.No;
        p.MoveType = MoveType.FlyWorldOnly;
        p.Solid = Solid.Not;
        p.TakeDamage = DamageMode.No;
        p.Flags = EntFlags.Client | EntFlags.NoTarget;
        p.CanPickupItems = false;                // QC drops FL_PICKUPITEMS
        p.Velocity = Vector3.Zero;
        p.AVelocity = Vector3.Zero;
        p.PunchAngle = Vector3.Zero;

        // QC: an observer has no health (FRAGS_SPECTATOR sentinel) and no weapons. QC PutObserverInServer sets
        // RES_ARMOR to g_balance_armor_start (server/client.qc:366 "was 666?!"), not 0 — cosmetic for the
        // spectator scoreboard/HUD but kept faithful (stock g_balance_armor_start is 0, so usually a no-op).
        p.SetResourceExplicit(ResourceType.Health, 0f);
        p.SetResourceExplicit(ResourceType.Armor, Cvars.FloatOr("g_balance_armor_start", 0f));
        p.OwnedWeapons.Clear();
        p.OwnedWeaponSet.Clear();
        p.ActiveWeaponId = -1;
        p.SwitchWeaponId = -1;

        // QC: setmodel(MDL_Null) — hide the body. Observers are also excluded from the entity stream
        // (ServerNet skips IsObserver), so no client sees a phantom at the observer's origin.
        if (Api.Services is not null)
            Api.Entities.SetModel(p, "");
        else
            p.Model = "";

        // clear respawn + transient state (no longer a dead player awaiting respawn).
        p.RespawnTime = 0f;
        p.RespawnTimeMax = 0f;
        p.RespawnFlags = RespawnFlag.None;
        p.RespawnTimeStat = 0f;

        ClientInfo? info = InfoOf(p);
        if (info is not null)
        {
            info.JoinTime = Now;                 // restart the MIN_SPEC_TIME dwell before a re-join
            info.SpecNextReleased = true;
            info.SpecFreeflyReleased = true;
        }
        p.JoinJumpReleased = true;
        p.AutoJoinChecked = 1;                   // a deliberate spectator should NOT be auto-rejoined

        PlayerStates?.Of(p).OnSpawn();           // reset air/contents timers
        ServerEntities?.LinkPlayer(p);
        // NOTE: the client stays in the gametype roster (Join re-uses it); IsObserver/FragsSpectator keep it out
        // of scoring/respawn/spawn-selection, which all already gate on those flags.
    }

    /// <summary>
    /// QC <c>SpectateNext</c> (server/client.qc:1987): follow the next living player after the current spectatee,
    /// honouring the gametype's anti-ghost <c>spectate_enemies</c> mode via <see cref="SpectatorRules"/>. No-op
    /// when no valid target exists (stays free-fly). <paramref name="forward"/> false = SpectatePrev.
    /// </summary>
    public void SpectateNext(Player p, bool forward = true)
    {
        if (!p.IsObserver) return;
        int mode = SpectatorRules.SpectateEnemiesMode(_match.GameType?.NetName);
        Player? next = SpectatorRules.CycleSpectatee(
            p, _players, p.Spectatee, spectatorInGame: false, mode, _teamplay.IsTeamGame, forward);
        if (next is not null)
            Spectate(p, next);
    }

    /// <summary>QC <c>Spectate</c>/<c>SpectateSet</c>: lock onto <paramref name="target"/> (MOVETYPE_NONE, glued
    /// via <see cref="SpectateCopy"/> each tick) and mark this observer as following it.</summary>
    public void Spectate(Player p, Player target)
    {
        p.Spectatee = target;
        p.MoveType = MoveType.None;   // QC SpectateSet: MOVETYPE_NONE — the spectator is glued to the spectatee
        p.Velocity = Vector3.Zero;
        SpectateCopy(p, target);
    }

    /// <summary>QC the <c>+attack2</c> drop-to-free-fly: stop following and return to free-fly observing.</summary>
    public void StopSpectating(Player p)
    {
        p.Spectatee = null;
        p.MoveType = MoveType.FlyWorldOnly;
    }

    /// <summary>
    /// QC <c>SpectateCopy</c> (server/client.qc:1799): mirror the spectatee's view/origin/resources onto the
    /// following observer so the observer's own owner-state snapshot — which the client renders the camera + HUD
    /// from — tracks the watched player.
    /// </summary>
    private void SpectateCopy(Player spectator, Player target)
    {
        spectator.Origin = target.Origin;
        spectator.Velocity = target.Velocity;
        spectator.Angles = target.Angles;
        spectator.ViewAngles = target.ViewAngles;
        spectator.ViewOfs = target.ViewOfs;
        spectator.SetResourceExplicit(ResourceType.Health, target.GetResource(ResourceType.Health));
        spectator.SetResourceExplicit(ResourceType.Armor, target.GetResource(ResourceType.Armor));
        // QC SpectateCopy also mirrors the ammo family + the weapon loadout (server/client.qc:1799 copies
        // ammo_* / weapons / switchweapon) — the follower's owner block feeds the full HUD's ammo/weapons
        // panels, so without these the panels would show the observer's EMPTY inventory, not the spectatee's.
        spectator.SetResourceExplicit(ResourceType.Shells, target.GetResource(ResourceType.Shells));
        spectator.SetResourceExplicit(ResourceType.Bullets, target.GetResource(ResourceType.Bullets));
        spectator.SetResourceExplicit(ResourceType.Rockets, target.GetResource(ResourceType.Rockets));
        spectator.SetResourceExplicit(ResourceType.Cells, target.GetResource(ResourceType.Cells));
        spectator.SetResourceExplicit(ResourceType.Fuel, target.GetResource(ResourceType.Fuel));
        spectator.ActiveWeaponId = target.ActiveWeaponId;
        spectator.SwitchWeaponId = target.SwitchWeaponId;
        spectator.OwnedWeaponSet = target.OwnedWeaponSet;   // WepSet is a bit-struct — plain value copy
        if (!spectator.OwnedWeapons.SetEquals(target.OwnedWeapons))
        {
            spectator.OwnedWeapons.Clear();
            foreach (string w in target.OwnedWeapons)
                spectator.OwnedWeapons.Add(w);
        }

        // QC SpectateCopy (server/client.qc:1820): STAT(PRESSED_KEYS, this) = STAT(PRESSED_KEYS, spectatee) — a
        // following observer inherits the watched player's held-key bitset so the pressed-keys / strafe HUD shows
        // the spectatee's keys. GetPressedKeys keeps target.PressedKeys current each server frame.
        spectator.PressedKeys = target.PressedKeys;

        // QC MUTATOR_HOOKFUNCTION(nades, SpectateCopy) (sv_nades.qc:937): a following observer mirrors the
        // spectatee's nade HUD/bonus stats (NADE_TIMER charge ring + NADE_BONUS_TYPE/pokenade_type/NADE_BONUS/
        // NADE_BONUS_SCORE) so the nade readouts track the watched player. No-op when g_nades is off.
        XonoticGodot.Common.Gameplay.Nades.NadesMutator.OnSpectateCopy(spectator, target);

        // QC SpectateCopy tail (server/client.qc:1837): anticheat_spectatecopy(this, spectatee) overrides the
        // observer's body angle with the spectatee's evade-tracked view angle. Runs last so it wins over the
        // raw .Angles copy above; no-op until the host wires it.
        SpectateAngleCopy?.Invoke(spectator, target);
    }

    /// <summary>
    /// QC <c>GameRules_spawning_teams</c> wiring: tell <see cref="SpawnSystem"/> whether the active gametype
    /// requests team-only spawnpoints, derived from <see cref="GameType.RequestsTeamSpawns"/> (per-mode; the
    /// cvar-gated team modes read their <c>g_*_team_spawns</c> cvar). Runs once the gametype is known, and
    /// re-syncs if the gametype changes. A no-op when no gametype is set yet (the FFA-style default of 0 stands).
    /// </summary>
    private GameType? _teamSpawnsSyncedFor;
    private void EnsureTeamSpawnsRequested()
    {
        GameType? gt = _match.GameType;
        if (gt is null || ReferenceEquals(gt, _teamSpawnsSyncedFor))
            return;
        _teamSpawnsSyncedFor = gt;
        SpawnSystem.RequestTeamSpawns(gt.RequestsTeamSpawns);
    }

    /// <summary>
    /// QC <c>PutClientInServer</c> + the PlayerSpawn mutator event: pick a spawnpoint away from the living,
    /// place + load out the player (<see cref="SpawnSystem.PutPlayerInServer"/>), then fire
    /// <see cref="MutatorHooks.PlayerSpawn"/>. Used for both the first spawn and every respawn. On a map with
    /// no spawnpoints, arms a short retry (QC: retry once a second) and returns false.
    /// </summary>
    public bool Spawn(Player p)
    {
        // QC SelectSpawnPoint(this, false): the player's team (set at connect via TeamBalance_JoinBestTeam) drives
        // the team-spawn filter — SelectSpawnPoint derives teamcheck from p.Team + the have_team_spawns globals, so
        // a team player spawns on a spawnpoint tagged with their own team, while an FFA player uses any spot.
        // QC's normal (non-anypoint) pass runs Spawn_FilterOutBadSpots(..., targetcheck=TRUE) (spawnpoints.qc:419),
        // which applies the ACTIVE_ACTIVE spawnpoint gate — so pass targetCheck:true here (inert on stock maps where
        // every spot is active; needed once Onslaught control-point deactivation toggles spot.active).
        EnsureTeamSpawnsRequested();
        // QC: the gametype's spawn_evalfunc / forced-spot setup runs before SelectSpawnPoint. Race uses this to
        // redirect a respawning racer back to their last passed checkpoint (race_respawn_spotref).
        OnPreSpawn?.Invoke(p);
        SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(p, LivePlayers(), targetCheck: true);
        if (sp is null)
        {
            p.RespawnTime = Now + 1f; // QC: no spawnpoint yet, retry next second
            return false;
        }

        // QC status_effects PutClientInServer hook (sv_status_effects.qc:133-147): a (re)spawning player starts
        // with a clean status-effect store — StatusEffects_clearall so no burning/stunned/superweapon timer
        // bleeds across a respawn (and the cleared state networks via the dirty-mark).
        StatusEffectsCatalog.ClearAll(p);

        // QC MUTATOR_HOOKFUNCTION(nades, PutClientInServer) (sv_nades.qc:470): nades_RemoveBonus(player) — a
        // (re)spawning player's banked bonus + accrual is wiped so a bonus banked in a previous life doesn't
        // carry into the new one. No-op when g_nades is off. Runs before the PlayerSpawn nades hook (which then
        // re-assigns the offhand + nade_refire), matching the QC PutClientInServer→PlayerSpawn order.
        XonoticGodot.Common.Gameplay.Nades.NadesMutator.OnPutClientInServer(p);

        SpawnSystem.PutPlayerInServer(p, sp.Value, warmup: IsWarmup?.Invoke() ?? false);

        // A (re)spawned player is ALIVE and no longer awaiting respawn — clear the dead-state respawn fields so the
        // networked STAT(RESPAWN_TIME) reads 0 (alive). DeadPlayerThink stops running once the player is alive, so
        // without this the last value it wrote (±respawn_time) stays non-zero and the client reads deadNow forever:
        // the death scoreboard + respawn UI stick "on" after respawn (was masked while the scoreboard mis-rendered
        // as a 64×64 box; #17). Mirrors the PutObserverInServer clear.
        p.RespawnTime = 0f;
        p.RespawnTimeMax = 0f;
        p.RespawnFlags = RespawnFlag.None;
        p.RespawnTimeStat = 0f;

        // [T35] QC PutClientInServer: this.flags = FL_CLIENT | FL_PICKUPITEMS. A spawned, live player can pick
        // up world items — Item_Touch's first gate (CanPickupItems) only passes for a flagged player, so an
        // observer/projectile never collects items. Set here on every spawn path; cleared on disconnect/observe.
        p.CanPickupItems = true;

        // A successful spawn means the client is now a LIVE player (QC TRANSMUTE(Player) in Join), so leave the
        // observer phase here too — this covers every spawn path (initial Join, the no-spawnpoint retry, and the
        // gametype respawn driver which calls Spawn directly), keeping the live/observer state consistent.
        p.IsObserver = false;

        // entity-table fix: keep the player's AbsMin/AbsMax current so radius queries hit it post-spawn.
        ServerEntities?.LinkPlayer(p);

        // reset the server-only per-player timers (QC PutPlayerInServer clears air/regen/contents state).
        PlayerStates?.Of(p).OnSpawn();

        // [sv-handicap.init.defaults_to_one] QC Handicap_Initialize(this) (server/handicap.qc:16-23), called from
        // PutClientInServer (server/client.qc:1240): reset the FORCED handicaps to 1 (no handicap) every (re)spawn
        // so a stale forced handicap from a previous round/match doesn't bleed across, and refresh handicap_level.
        // Runs BEFORE the PlayerSpawn mutator hook so the dynamic_handicap recompute (below) immediately overwrites
        // the reset with the freshly-computed value, matching Base's PutClientInServer → dynamic_handicap order.
        XonoticGodot.Common.Gameplay.Handicap.Initialize(p);

        // QC: MUTATOR_CALLHOOK(PlayerSpawn, spot, this) — fired after the shared spawn setup.
        var args = new MutatorHooks.PlayerSpawnArgs(p, sp.Value.Source);
        MutatorHooks.PlayerSpawn.Call(ref args);

        // QC PutPlayerInServer (client.qc:815-821): fire the spawnpoint's .target on spawn — a map can wire a
        // target_relay/trigger off the spot the player materialized on (lights, doors, scoring triggers). The
        // assault/race hack temporarily nulls spot.target so those modes (whose spawnpoints chain checkpoints via
        // .target) don't re-trigger the chain on every respawn. CTS is a Race variant (QC g_race covers it).
        if (sp.Value.Source is { } spot)
        {
            bool suppressTarget = _match.GameType is Assault or Race or Cts;
            string savedTarget = spot.Target;
            if (suppressTarget) spot.Target = "";
            MapMover.UseTargets(spot, p, null);
            if (suppressTarget) spot.Target = savedTarget;
        }

        p.Winning = false;          // QC: a fresh spawn clears the end-of-match winner latch
        OnClientSpawn?.Invoke(p);   // §5 spawn hooks (alivetime start, anticheat view reset)

        return true;
    }

    /// <summary>
    /// QC <c>ClientDisconnect</c>: remove the client everywhere it was registered (gametype roster, score
    /// table, sim client list, entity table) and clear FL_CLIENT. Returns true if the client was present.
    /// </summary>
    public bool ClientDisconnect(Player p)
    {
        int idx = _clients.FindIndex(c => ReferenceEquals(c.Player, p));
        if (idx < 0)
            return false;

        ClientInfo info = _clients[idx];
        info.IsConnected = false;

        // QC status_effects ClientDisconnect hook (sv_status_effects.qc:80-92): clear the leaving player's
        // status effects (NORMAL removal — fires the removal sounds, "just to get rid of the pickup sound")
        // so a stale burning/superweapon timer doesn't survive on a recycled player object.
        StatusEffectsCatalog.RemoveAll(p, StatusEffectRemoval.Normal);
        StatusEffectsCatalog.ClearAll(p);

        // QC ClientDisconnect (client.qc:1305): if (this.vehicle) vehicles_exit(this.vehicle, VHEF_RELEASE) — a
        // disconnecting pilot is ejected so the vehicle is freed for others (and the leaving player's object
        // isn't left coupled to a live vehicle). VehicleBoarding.Exit is the same VHEF_RELEASE used by the
        // rot-death path; a no-op when not in a vehicle.
        if (p.Vehicle is not null)
            VehicleBoarding.Exit(p);

        // QC ClientDisconnect (client.qc:1335): player_powerups_remove_all(this, IS_PLAYER(this)) — strip the
        // superweapon / unlimited item bits and play the power-off sound only if the leaver was a live player
        // (an observer carries no powerups). Mirrors the death-time strip so a recycled player object is clean.
        PlayerFrameLogic.PlayerPowerupsRemoveAll(p, !p.IsObserver);

        // QC ClientDisconnect (client.qc:1318-1319): RemoveGrapplingHooks(this); Portal_ClearAll(this). A leaving
        // player must drop any in-flight grappling-hook chain and tear down every portal they placed so neither
        // outlives the owner (a dangling hook entity / an ownerless live portal). Hook removal lives in
        // Common.Gameplay; Portal_ClearAll is a host hook (null on a bare/test host → no-op).
        Hook.RemoveGrapplingHooks(p);
        Porto.PortalClearAll?.Invoke(p);

        OnClientDisconnect?.Invoke(p); // §5 disconnect hooks (finalize stats, :part:, voter/timeout cleanup)

        // QC MUTATOR_HOOKFUNCTION(<mode>, ClientDisconnect) (e.g. ctf_RemovePlayer): let the active gametype
        // relinquish any objective this leaving player holds — a CTF carrier drops the flag where they stand and
        // every flag back-referencing them (pass sender/target, dropper) is cleared — BEFORE the roster removal
        // below, so the drop can still see the player. Default is a no-op for non-objective modes.
        _match.GameType?.OnPlayerRemoved(p);

        _clients.RemoveAt(idx);
        _players.Remove(p);
        RefreshPlayersSnapshot();
        _sim.Clients.Remove(p);
        _match.RemovePlayer(p);
        _scores.Unregister(p);
        ServerEntities?.UnregisterPlayer(p); // drop from find/radius (QC the client edict leaves the list)
        PlayerStates?.Remove(p);             // drop the server-side per-player timers

        p.Flags &= ~EntFlags.Client; // QC: this.flags &= ~FL_CLIENT
        p.CanPickupItems = false;    // [T35] QC drops FL_PICKUPITEMS with FL_CLIENT — a gone client can't pick up
        if (Api.Services is not null)
            Api.Entities.Remove(p);

        // QC MUTATOR_CALLHOOK(ClientDisconnect, this): generic mutator roster hook. dynamic_handicap recomputes
        // its score mean here (the departed client is already out of the roster, so the mean excludes them).
        var disconnectArgs = new MutatorHooks.ClientDisconnectArgs(p);
        MutatorHooks.ClientDisconnect.Call(ref disconnectArgs);

        // QC ClientDisconnect tail (client.qc:3057): TeamBalance_RemoveExcessPlayers(this) — re-check 2-team
        // balance now that this player has left (the leaver is already out of the roster above, so no ignore arg).
        OnAfterClientDisconnect?.Invoke();
        return true;
    }

    /// <summary>Find a connected client's info by its player (null if not connected).</summary>
    public ClientInfo? InfoOf(Player p) => _clients.Find(c => ReferenceEquals(c.Player, p));

    // ----- helpers -----

    private readonly List<Player> _liveScratch = new();

    /// <summary>The currently-alive players (spawn selection keeps new spawns away from these). Excludes
    /// observers: a free-fly spectator has DeadState==No (so IsDead is false) but is NOT a live player — QC's
    /// IS_PLAYER excludes spectators, so they must not repel/skew spawn-point selection.</summary>
    private IReadOnlyList<Player> LivePlayers()
    {
        _liveScratch.Clear();
        for (int i = 0; i < _players.Count; i++)
            if (!_players[i].IsDead && !_players[i].IsObserver)
                _liveScratch.Add(_players[i]);
        return _liveScratch;
    }

    /// <summary>Next client-entity index (negative space, distinct from the engine table's slots).</summary>
    private int _nextClientIndex = -1;

    /// <summary>
    /// Initialize a freshly-constructed client edict's engine state and register it for find/radius queries.
    /// The public <see cref="IEntityService"/> can only mint a bare <see cref="Entity"/>, not a
    /// <see cref="Player"/> subclass, so a client does not live in the engine table's dense list — it is
    /// simulated through the host-owned <see cref="SimulationLoop.Clients"/> list instead, and given a
    /// negative index so it never collides with a table slot.
    ///
    /// The entity-table fix: when a <see cref="ServerEntities"/> registry is wired, the player is registered
    /// there so <c>FindByClass("player")</c> / <c>FindInRadius</c> return it exactly like QuakeC's global
    /// entity list — so spawn selection, splash damage, item triggers and bot target discovery all see
    /// clients. (Spawn placement is still done by <see cref="Spawn"/> via SpawnSystem.)
    /// </summary>
    private void RegisterEntity(Player p)
    {
        p.Index = _nextClientIndex--;
        p.IsFreed = false;
        ServerEntities?.RegisterPlayer(p); // entity-table fix: clients become findable by class/radius
    }

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
