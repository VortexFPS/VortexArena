using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Clan Arena — port of <c>CLASS(ClanArena, Gametype)</c>
/// (common/gametypes/gametype/clanarena/{clanarena.qh,sv_clanarena.qc}). A round-based elimination
/// team mode: no respawns within a round; the last team with a living player wins the round and gains
/// one round point. First team to the round limit (QC fraglimit_override / GameRules_limit_score, default
/// 10) wins the match. Kills award NO individual frags (QC GiveFragsForKill → 0); damage dealt accrues to
/// the player's SCORE (g_ca_damage2score) for the scoreboard only, not the win condition.
///
/// QC defaults (gametype_init): "timelimit=20 pointlimit=10 teams=2 leadlimit=6" (legacydefaults "10 20 0").
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (server/teamplay.qc, via <see cref="TeamBalance"/>);
///  - the round lifecycle: dead players stay dead (no respawn) until the round resolves
///    (CA_CheckWinner: when exactly one team has alive players, that team scores a round);
///  - round-limit win condition (GameRules_limit_score → fraglimit) on team round wins;
///  - kills give no frags (GiveFragsForKill → 0).
///
/// Faithfully ported (objective layer):
///  - per-round warmup + countdown timing via the shared <see cref="RoundHandler"/> (QC round_handler_Spawn
///    with CA_CheckTeams / CA_CheckWinner / CA_RoundStart), driven by <see cref="Tick"/>;
///  - stalemate prevention on the round time limit (<see cref="PreventStalemate"/>, QC CA_PreventStalemate by
///    survivor count and/or total team health);
///  - CA's start-loadout via the live SetStartItems hook (<see cref="OnSetStartItems"/>, QC g_ca_start_health/
///    armor 200/200 + ammo) and the "most" weapon arena (<see cref="OnSetWeaponArena"/>);
///  - the g_ca_damage2score scoreboard accrual on the live PlayerDamage_SplitHealthArmor hook
///    (<see cref="OnSplitHealthArmor"/>, with the per-player decimal carry of <see cref="AddScoreFloat2Int"/>);
///  - no map pickups (<see cref="OnFilterItemDefinition"/>), no regen (<see cref="OnPlayerRegen"/>), no weapon
///    drop (<see cref="OnForbidThrowCurrentWeapon"/>), and the round-outcome / "alone" notifications.
///
/// Faithfully ported (damage rule):
///  - the no-friendly-fire damage filter (QC ca Damage_Calculate): a live player takes ZERO damage from a
///    teammate, from themselves, or from a fall (<see cref="OnDamageCalculate"/> on the shared DamageCalculate
///    hook); mirror damage is always zeroed. (The spectate-enemies rule is the server-side
///    <see cref="XonoticGodot.Server"/> SpectatorRules system, fed by g_ca_spectate_enemies.)
/// </summary>
[GameType]
public sealed class ClanArena : GameType
{
    // ----- round-limit cvars + default (CA uses fraglimit_override; legacydefaults rounds=10) -----
    private const string CvarRoundLimitCa = "g_ca_point_limit";      // CA-specific
    private const string CvarRoundLimit   = "fraglimit";            // GameRules_limit_score target
    private const string CvarLeadLimitCa  = "g_ca_point_leadlimit";
    private const string CvarLeadLimit    = "leadlimit";
    private const float  DefaultRoundLimit = 10f;
    private const float  DefaultLeadLimit  = 6f;

    // ----- team count cvars (g_ca_teams_override >= 2 ? override : g_ca_teams), clamped 2..4 -----
    private const string CvarTeamsOverride = "g_ca_teams_override";
    private const string CvarTeams         = "g_ca_teams";
    private const int    DefaultTeams      = 2;

    // Per-team round wins (QC ST_CA_ROUNDS, team slot 1 — the TEAM primary) now live in the unified GameScores
    // two-slot team store — the source of truth (common/scores.qh). CA's player primary is SP_SCORE (the
    // damage2score accrual); the team primary is ST_CA_ROUNDS. Read/written via GetTeamRounds / GameScores.AddToTeam.

    /// <summary>Round-elimination bookkeeping (QC round_handler / CA_count_alive_players).</summary>
    public readonly RoundState Round = new();

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>The roster the round handler evaluates (set by the host before <see cref="Tick"/>).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    /// <summary>
    /// QC <c>INGAME_STATUS_JOINING</c> (sv_clanarena.qc PutClientInServer / ForbidSpawn): the set of players
    /// who connected/joined WHILE a round was live (<c>!allowed_to_spawn</c>). They are force-observed until the
    /// next round reset, are greyed on the scoreboard (<see cref="IsEliminatedPlayer"/>), and are flipped to
    /// INGAME_JOINED on their first live spawn (<see cref="OnPlayerSpawn"/>) or the round reset
    /// (<see cref="ResetMapPlayers"/>). The port has no separate INGAME enum, so this set models QC's
    /// INGAME_STATUS_JOINING bit. A player NOT in this set and tracked as a live <see cref="Player"/> is the
    /// INGAME_JOINED case.
    /// </summary>
    private readonly HashSet<Player> _joiningMidRound = new();

    /// <summary>
    /// QC <c>.prev_team</c> (sv_clanarena.qc CA_RoundStart / MatchEnd_RestoreSpectatorAndTeamStatus): the team a
    /// player held before CA forced them between observer/player on a mid-round join. Snapshotted at round start so
    /// the end-of-match scoreboard can restore who was actually playing (<see cref="OnMatchEndBeforeScores"/>).
    /// </summary>
    private readonly Dictionary<Player, int> _prevTeam = new();

    /// <summary>
    /// QC <c>ca_isEliminated</c> half: true while this player joined mid-round and is waiting out the round as an
    /// observer (INGAME_STATUS_JOINING). Read by the host's force-observe path and the scoreboard grey-out.
    /// </summary>
    public bool IsJoiningMidRound(Player p) => _joiningMidRound.Contains(p);

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, ForbidSpawn)</c> + <c>PutClientInServer</c> (sv_clanarena.qc): while a round
    /// is live (<c>!allowed_to_spawn</c>) no client may become a live player — ForbidSpawn forbids an in-game
    /// player's respawn and PutClientInServer force-observes a fresh joiner, so the combined net outcome is "nobody
    /// spawns mid-round". The port models that single outcome by refusing the join while the round is started; the
    /// refused client stays an observer until the next round reset. Mirrors Survival.CanJoin (sv_survival.qc).
    /// <paramref name="roundStarted"/> stands in for QC's <c>!allowed_to_spawn</c> (a non-warmup round is live).
    /// Returns true to allow the join (pre-round / warmup), false to force-observe.
    /// </summary>
    public bool CanJoin(Player p, bool roundStarted) => !roundStarted;

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, PutClientInServer)</c> (sv_clanarena.qc): a client who tries to join while a
    /// round is live is marked INGAME_STATUS_JOINING and told they enter next round (INFO_CA_JOIN_LATE). The host
    /// calls this whenever a client's join is refused by <see cref="CanJoin"/> mid-round. Idempotent.
    /// </summary>
    public void OnJoinLate(Player p)
    {
        if (p is null || !_joiningMidRound.Add(p))
            return;
        // QC Send_Notification(NOTIF_ONE_ONLY, player, MSG_INFO, INFO_CA_JOIN_LATE): personal "you'll play next round".
        NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Info, "CA_JOIN_LATE");
    }

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    // ----- start-loadout cvars + defaults (QC g_ca_start_*; 200/200 + full ammo) -----
    private const string CvarStartHealth = "g_ca_start_health";
    private const string CvarStartArmor  = "g_ca_start_armor";
    private const string CvarDamage2Score = "g_ca_damage2score";
    private const string CvarPreventStalemate = "g_ca_prevent_stalemate";
    private const string CvarWarmup = "g_ca_warmup";
    private const string CvarRoundTimelimit = "g_ca_round_timelimit";
    private const string CvarRoundEndDelay = "g_ca_round_enddelay";
    private const float  DefaultStartHealth = 200f;
    private const float  DefaultStartArmor  = 200f;
    private const float  DefaultDamage2Score = 100f; // QC: SCORE per 100 damage dealt

    public ClanArena()
    {
        NetName = "ca";
        DisplayName = "Clan Arena";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC: GameRules_teams(true) + round_handler_Spawn(CA_CheckTeams, CA_CheckWinner, CA_RoundStart) +
        // EliminatedPlayers_Init. The round handler is created in Activate (it needs the live roster); OnInit
        // just clears state. Start-items are applied per spawn via ApplyStartItems.
    }

    /// <summary>
    /// QC sv_clanarena.qh: <c>GameRules_spawning_teams(autocvar_g_ca_team_spawns)</c> — CA gates team spawns on
    /// g_ca_team_spawns (stock default 1, so CA uses team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => Cvar("g_ca_team_spawns", 1f) != 0f;

    /// <summary>QC g_ca_start_health (default 200): the CA spawn health.</summary>
    public float StartHealth => Cvar(CvarStartHealth, DefaultStartHealth);
    /// <summary>QC g_ca_start_armor (default 200): the CA spawn armor.</summary>
    public float StartArmor => Cvar(CvarStartArmor, DefaultStartArmor);
    /// <summary>QC g_ca_damage2score (default 100): SCORE awarded per this much damage dealt (scoreboard only).</summary>
    public float Damage2Score => Cvar(CvarDamage2Score, DefaultDamage2Score);
    /// <summary>QC g_ca_prevent_stalemate bitmask: bit0 = break ties by survivors, bit1 = by total team health.</summary>
    public int PreventStalemateMode => (int)Cvar(CvarPreventStalemate, 0f);

    public int TeamCount
    {
        get
        {
            int n = DefaultTeams;
            if (TryCvar(CvarTeamsOverride, out float ov) && ov >= 2f) n = (int)ov;
            else if (TryCvar(CvarTeams, out float t)) n = (int)t;
            return System.Math.Clamp(n, 2, 4);
        }
    }

    /// <summary>Round limit in force (g_ca_point_limit, else fraglimit, else 10). 0 == unlimited.</summary>
    public float RoundLimit
    {
        get
        {
            if (TryCvar(CvarRoundLimitCa, out float rl)) return rl;
            if (TryCvar(CvarRoundLimit, out float fl)) return fl;
            return DefaultRoundLimit;
        }
    }

    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimitCa, out float ll)) return ll;
            if (TryCvar(CvarLeadLimit, out float l)) return l;
            return DefaultLeadLimit;
        }
    }

    private const string CvarWeaponArena = "g_ca_weaponarena"; // QC sv_clanarena.qh:17 default "most"

    /// <summary>Per-player decimal accumulator for the SCORE damage2score accrual — QC <c>.float2int_decimal_fld</c>
    /// (sv_clanarena.qc:271). Carries the sub-point remainder across hits so rounding matches GameRules_scoring_add_float2int.</summary>
    private readonly Dictionary<Player, float> _scoreDecimal = new();

    /// <summary>HookHandler so Deactivate can remove the exact instance (CA's PlayerDies path).</summary>
    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageHandler;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _startItemsHandler;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _weaponArenaHandler;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _filterItemHandler;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _forbidThrowHandler;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _regenHandler;
    private HookHandler<GameHooks.PlayerDamageArgs>? _splitHandler;
    private HookHandler<MutatorHooks.MakePlayerObserverArgs>? _makeObserverHandler;
    private HookHandler<MutatorHooks.ClientDisconnectArgs>? _disconnectHandler;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _spawnHandler;
    private HookHandler<MutatorHooks.MatchEndBeforeScoresArgs>? _matchEndHandler;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Round.Reset();
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring
        // QC fragsleft_last reset: re-arm the "N rounds left" announcer (ca's Scores_CountFragsRemaining hook).
        Scoring.GameScores.ResetFragsRemaining();

        // QC sv_clanarena.qh GameRules_scoring(teamplay_bitmask, SFL_SORT_PRIO_PRIMARY, 0, {
        // field_team(ST_CA_ROUNDS, "rounds", PRIMARY); }): the player primary is SP_SCORE (damage2score accrual);
        // ST_SCORE (team slot 0) has no prio (stprio=0); ST_CA_ROUNDS (team slot 1) "rounds" is the TEAM primary
        // (round wins). No per-player gametype columns.
        Scoring.GameScores.ScoreRulesBasics(teams: true);
        Scoring.GameScores.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0
        Scoring.GameScores.SetTeamLabel(Scoring.GameScores.TeamSlotSecondary, "rounds", Scoring.ScoreFlags.SortPrioPrimary); // ST_CA_ROUNDS (slot 1)
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score); // player primary = SP_SCORE
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)

        // QC round_handler_Spawn(CA_CheckTeams, CA_CheckWinner, CA_RoundStart).
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = CheckTeams,
            CanRoundEnd = () => CheckWinner() != 0,
            OnRoundStart = () => { /* QC CA_RoundStart: snapshot teams; spawn-allow is the host's job */ },
        };
        Handler.Init(Cvar(CvarRoundEndDelay, 5f), Cvar(CvarWarmup, 5f), Cvar(CvarRoundTimelimit, 180f));

        _scoreDecimal.Clear();

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC ca Damage_Calculate: install the no-friendly-fire filter (zero self/team/fall damage on live players).
        _damageHandler = OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageHandler);

        // QC ca SetStartItems: 200/200 + full ammo (the defining CA survivability). SpawnSystem.ComputeStartItems
        // fires this hook live, so subscribing here applies the loadout on every spawn (Wave-1 seam, _wave1-seams.md:190).
        _startItemsHandler = OnSetStartItems;
        MutatorHooks.SetStartItems.Add(_startItemsHandler);

        // QC ca SetWeaponArena: default the arena to g_ca_weaponarena ("most") so players spawn with the full arsenal.
        _weaponArenaHandler = OnSetWeaponArena;
        MutatorHooks.SetWeaponArena.Add(_weaponArenaHandler);

        // QC ca FilterItem: no map item/powerup pickups in CA.
        _filterItemHandler = OnFilterItemDefinition;
        MutatorHooks.FilterItemDefinition.Add(_filterItemHandler);

        // QC ca ForbidThrowCurrentWeapon: can't drop the current weapon.
        _forbidThrowHandler = OnForbidThrowCurrentWeapon;
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_forbidThrowHandler);

        // QC ca PlayerRegen: no health/armor regen in CA.
        _regenHandler = OnPlayerRegen;
        MutatorHooks.PlayerRegen.Add(_regenHandler);

        // QC ca PlayerDamage_SplitHealthArmor: the live g_ca_damage2score scoreboard accrual.
        _splitHandler = OnSplitHealthArmor;
        GameHooks.PlayerDamageSplitHealthArmor.Add(_splitHandler);

        // QC MUTATOR_HOOKFUNCTION(ca, MakePlayerObserver) (sv_clanarena.qc:397): when a live player is force-
        // spectated mid-round, notify the last remaining teammate "You are now alone!" exactly as on death.
        _makeObserverHandler = OnMakePlayerObserver;
        MutatorHooks.MakePlayerObserver.Add(_makeObserverHandler);

        // QC MUTATOR_HOOKFUNCTION(ca, ClientDisconnect) (sv_clanarena.qc:388): when a live player disconnects
        // mid-round, notify the last remaining teammate "You are now alone!" exactly as on death.
        _disconnectHandler = OnClientDisconnect;
        MutatorHooks.ClientDisconnect.Add(_disconnectHandler);

        // QC MUTATOR_HOOKFUNCTION(ca, PlayerSpawn) (sv_clanarena.qc:272): on spawn at/before game_starttime
        // (i.e. a game restart, NOT a per-round respawn) zero this player's damage2score decimal carry.
        _spawnHandler = OnPlayerSpawn;
        MutatorHooks.PlayerSpawn.Add(_spawnHandler);

        // QC MUTATOR_HOOKFUNCTION(ca, MatchEnd_BeforeScores) (sv_clanarena.qc): before the final scoreboard is
        // dumped, restore each player's pre-CA spectator/team status (MatchEnd_RestoreSpectatorAndTeamStatus) so a
        // mid-round joiner who never actually played isn't reported as a live competitor. The port has no separate
        // observer-restore, so we clear the INGAME-joining set + prev_team snapshot here (the joiners revert to plain
        // observers, exactly the state the final scoreboard should show).
        _matchEndHandler = OnMatchEndBeforeScores;
        MutatorHooks.MatchEndBeforeScores.Add(_matchEndHandler);
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, Damage_Calculate)</c>: a live player takes no damage from a teammate, from
    /// themselves, or from a fall — and CA never mirrors damage. Knockback (force) is left intact (QC only zeros
    /// the damage), so a teammate can still nudge you without hurting you.
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs a)
    {
        if (a.Target is not Player target || target.DeadState != DeadFlag.No)
        {
            a.MirrorDamage = 0f; // QC: frag_mirrordamage = 0 unconditionally in CA
            return false;
        }
        bool isFall = DeathTypes.BaseOf(a.DeathType) == DeathTypes.Fall;
        bool selfOrTeam = a.Attacker is Player atk && (ReferenceEquals(atk, target) || Teams.SameTeam(atk, target));
        if (isFall || selfOrTeam)
            a.Damage = 0f;
        a.MirrorDamage = 0f;
        return false;
    }

    /// <summary>Provide the round handler with the current roster (call before <see cref="Tick"/>).</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    /// <summary>
    /// QC <c>round_handler_Think</c> at cnt==0: <c>FOREACH_CLIENT((IS_PLAYER(it) || INGAME(it)),
    /// GameRules_scoring_add(it, ROUNDS_PL, 1))</c> — credit every present player one ROUNDS_PL point as the
    /// round begins. The port has no INGAME_JOINING state, so the playing roster (IS_PLAYER) is the present set.
    /// Wired to the live <see cref="RoundHandler.OnRoundCounted"/> by the host (GameWorld CA branch).
    /// </summary>
    public void AwardRoundStartScore()
    {
        Scoring.ScoreField? roundsPl = Scoring.GameScores.Field("ROUNDS_PL");
        if (roundsPl is null)
            return;
        for (int i = 0; i < _roster.Count; i++)
            Scoring.GameScores.AddToPlayer(_roster[i], roundsPl, 1);
    }

    /// <summary>
    /// Advance the CA round handler one frame (QC round_handler_Think). Resolves the round when CA_CheckWinner
    /// produces a result. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick() => Handler?.Tick();

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (_damageHandler is not null)     { MutatorHooks.DamageCalculate.Remove(_damageHandler);             _damageHandler = null; }
        if (_startItemsHandler is not null) { MutatorHooks.SetStartItems.Remove(_startItemsHandler);           _startItemsHandler = null; }
        if (_weaponArenaHandler is not null){ MutatorHooks.SetWeaponArena.Remove(_weaponArenaHandler);         _weaponArenaHandler = null; }
        if (_filterItemHandler is not null) { MutatorHooks.FilterItemDefinition.Remove(_filterItemHandler);    _filterItemHandler = null; }
        if (_forbidThrowHandler is not null){ MutatorHooks.ForbidThrowCurrentWeapon.Remove(_forbidThrowHandler); _forbidThrowHandler = null; }
        if (_regenHandler is not null)          { MutatorHooks.PlayerRegen.Remove(_regenHandler);                  _regenHandler = null; }
        if (_splitHandler is not null)          { GameHooks.PlayerDamageSplitHealthArmor.Remove(_splitHandler);    _splitHandler = null; }
        if (_makeObserverHandler is not null)   { MutatorHooks.MakePlayerObserver.Remove(_makeObserverHandler);    _makeObserverHandler = null; }
        if (_disconnectHandler is not null)     { MutatorHooks.ClientDisconnect.Remove(_disconnectHandler);        _disconnectHandler = null; }
        if (_spawnHandler is not null)          { MutatorHooks.PlayerSpawn.Remove(_spawnHandler);                  _spawnHandler = null; }
        if (_matchEndHandler is not null)       { MutatorHooks.MatchEndBeforeScores.Remove(_matchEndHandler);       _matchEndHandler = null; }
        _scoreDecimal.Clear();
        _joiningMidRound.Clear();
        _prevTeam.Clear();
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>
    /// QC CA PlayerDies: the kill gives no frags (GiveFragsForKill → 0). The victim is NOT scheduled to
    /// respawn — in CA the dead stay out until the round resets. We just leave the death to the round check.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        // QC ca PlayerDies (sv_clanarena.qc:374): ca_LastPlayerForTeam_Notify(frag_target) — before the round
        // check, if this death leaves exactly one living teammate, center-print "You are now alone!" to that
        // last survivor. The Death bus fires here BEFORE the victim is flagged dead (DamageSystem.Killed sets
        // DeadState only after Combat.Death.Call), so we pass the victim as `leaving` and NotifyLastPlayerForTeam
        // excludes it explicitly (matching QC ca_LastPlayerForTeam's `it != this`).
        if (ev.Victim is Player victim)
            NotifyLastPlayerForTeam(victim, _roster);

        // No per-kill scoring in CA: round wins are the only score that affects the limit (GiveFragsForKill → 0).
        // Damage-to-score accrual happens continuously in the damage pipeline (see AddDamageScore), not here.
        // The victim is already marked dead by the damage system; CheckWinner resolves the round.
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, PlayerDamage_SplitHealthArmor)</c> (sv_clanarena.qc:481): the live
    /// g_ca_damage2score scoreboard accrual. Credits the attacker's SCORE for damage dealt to an enemy
    /// (minus the overkill excess), subtracts it for friendly fire, and self-penalises an environmental
    /// suicide (kill/drown/hurttrigger/camp/lava/slime/swamp). The remainder carries across hits via the
    /// per-player decimal accumulator (QC float2int_decimal_fld). Cosmetic — does not feed the round-win limit.
    /// </summary>
    private bool OnSplitHealthArmor(ref GameHooks.PlayerDamageArgs a)
    {
        // QC gate: no accrual before game_starttime, nor while the round is active-but-not-started. The CA
        // round handler is always "active" (spawned) once a CA match runs, so QC's (IsActive && !IsRoundStarted)
        // reduces to !IsRoundStarted here.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (Handler is not null && (now < Handler.GameStartTime || !Handler.IsRoundStarted))
            return false;

        float d2s = Damage2Score;
        if (d2s <= 0f)
            return false;

        if (a.Target is not Player target)
            return false;

        // QC: excess = max(0, frag_damage - damage_take - damage_save); skip if no effective damage landed.
        float damageTake = System.Math.Clamp(a.DamageTake, 0f, target.GetResource(ResourceType.Health));
        float damageSave = System.Math.Clamp(a.DamageSave, 0f, target.GetResource(ResourceType.Armor));
        float excess = System.Math.Max(0f, a.FragDamage - damageTake - damageSave);
        float effective = a.FragDamage - excess;
        if (effective == 0f)
            return false;

        Player? scorer = null;
        float scorerDamage = 0f;

        if (a.FragAttacker is Player atk)
        {
            // Enemy hit credits the attacker; friendly fire debits them.
            scorerDamage = Teams.SameTeam(atk, target) ? -effective : effective;
            scorer = atk;
        }
        else
        {
            // Environmental suicide: penalise the victim (QC's enumerated self-death deathtypes).
            string dt = DeathTypes.BaseOf(a.FragDeathType);
            if (dt is DeathTypes.Kill or DeathTypes.Drown or "hurttrigger" or "camp"
                   or DeathTypes.Lava or DeathTypes.Slime or DeathTypes.Swamp)
            {
                scorerDamage = -effective;
                scorer = target;
            }
        }

        if (scorer is not null)
            AddScoreFloat2Int(scorer, scorerDamage, d2s);
        return false;
    }

    /// <summary>
    /// QC <c>GameRules_scoring_add_float2int(scorer, SCORE, value, float2int_decimal_fld, factor)</c>
    /// (common/gametypes/sv_rules.qc): accumulate <c>value * factor / 100</c> as a fractional point total,
    /// carry the sub-point remainder in the per-player decimal field, and emit only the whole-point delta
    /// (<c>floor(counter + 0.5)</c>) to the SCORE column — so rounding matches QC across many small hits.
    /// </summary>
    private void AddScoreFloat2Int(Player scorer, float value, float factor)
    {
        _scoreDecimal.TryGetValue(scorer, out float carry);
        carry += value * factor / 100f;
        int whole = (int)System.MathF.Floor(carry + 0.5f);
        if (whole != 0)
        {
            Scoring.GameScores.AddToPlayer(scorer, Scoring.GameScores.Score, whole);
            carry -= whole;
        }
        _scoreDecimal[scorer] = carry;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, SetStartItems)</c> (sv_clanarena.qc:435): the CA spawn loadout —
    /// g_ca_start_health/armor (200/200) + full ammo (60/320/160/180/0). Strips the unlimited flags, then
    /// grants unlimited ammo when g_use_ammunition is off (QC <c>if(!cvar("g_use_ammunition")) start_items |=
    /// IT_UNLIMITED_AMMO</c>). SpawnSystem.ComputeStartItems fires this live (the readplayerstartcvars seam).
    /// </summary>
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        l.ItemFlags.Remove("UNLIMITED_AMMO");
        l.ItemFlags.Remove("UNLIMITED_SUPERWEAPONS");
        if (Cvar("g_use_ammunition", 1f) == 0f)
            l.ItemFlags.Add("UNLIMITED_AMMO");

        l.Health      = StartHealth;
        l.Armor       = StartArmor;
        l.AmmoShells  = Cvar("g_ca_start_ammo_shells", 60f);
        l.AmmoBullets = Cvar("g_ca_start_ammo_nails", 320f);
        l.AmmoRockets = Cvar("g_ca_start_ammo_rockets", 160f);
        l.AmmoCells   = Cvar("g_ca_start_ammo_cells", 180f);
        l.AmmoFuel    = Cvar("g_ca_start_ammo_fuel", 0f);
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, SetWeaponArena)</c> (sv_clanarena.qc:627): default the weapon arena to
    /// g_ca_weaponarena ("most") when the configured arena is unset / "0", so players spawn with the full arsenal.
    /// </summary>
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        if (args.Arena == "0" || string.IsNullOrEmpty(args.Arena))
            args.Arena = CvarString(CvarWeaponArena, "most");
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, FilterItem)</c> (sv_clanarena.qc:469): remove map pickups in CA. Powerups
    /// are filtered when g_powerups &lt;= 0; ALL items are filtered when g_pickup_items &lt;= 0 (the stock default,
    /// so CA maps spawn no pickups). Returns true to FILTER OUT. Powerups are matched by classname (the item
    /// registry isn't fully ported — the same stand-in Mayhem/LMS/NIX use).
    /// </summary>
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        string id = args.Definition.ClassName;
        bool isPowerup = id is "item_strength" or "item_shield" or "item_invincible";
        if (Cvar("g_powerups", 0f) <= 0f && isPowerup)
            return true;
        if (Cvar("g_pickup_items", 0f) <= 0f)
            return true;
        return false;
    }

    /// <summary>QC <c>MUTATOR_HOOKFUNCTION(ca, ForbidThrowCurrentWeapon)</c>: always forbid dropping the current weapon.</summary>
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;

    /// <summary>QC <c>MUTATOR_HOOKFUNCTION(ca, PlayerRegen)</c>: no health/armor regeneration in CA (return true).</summary>
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args) => true;

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, MakePlayerObserver)</c> (sv_clanarena.qc:397): when a live (non-dead)
    /// player is force-spectated mid-round, trigger the "You are now alone!" center-print to the last surviving
    /// teammate — the same notification path as the death case (<see cref="OnDeath"/>). Fires from
    /// <c>PutObserverInServer</c> after <c>p.IsObserver = true</c> is set but before <c>DeadState</c> is
    /// cleared, so <c>!p.IsDead</c> correctly identifies a formerly-live player. QC guard:
    /// <c>IS_PLAYER(player) &amp;&amp; !IS_DEAD(player)</c>.
    /// </summary>
    private bool OnMakePlayerObserver(ref MutatorHooks.MakePlayerObserverArgs args)
    {
        if (args.Player is Player leaving && !leaving.IsDead)
            NotifyLastPlayerForTeam(leaving, _roster);
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, ClientDisconnect)</c> (sv_clanarena.qc:388): when a live (non-dead,
    /// non-observer) player disconnects mid-round, trigger the "You are now alone!" center-print to the last
    /// surviving teammate. Fires from <c>ClientManager.ClientDisconnect</c> after the player is removed from
    /// the active roster, so <c>NotifyLastPlayerForTeam</c> naturally excludes the leaver without an explicit
    /// identity check. QC guard: <c>IS_PLAYER(player) &amp;&amp; !IS_DEAD(player)</c>.
    /// </summary>
    private bool OnClientDisconnect(ref MutatorHooks.ClientDisconnectArgs args)
    {
        if (args.Player is Player leaving && !leaving.IsObserver && !leaving.IsDead)
            NotifyLastPlayerForTeam(leaving, _roster);
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, PlayerSpawn)</c> (sv_clanarena.qc:272):
    /// <c>if (time &lt;= game_starttime) player.float2int_decimal_fld = 0</c> — reset the per-player damage2score
    /// decimal carry ON GAME RESTART (a spawn at/before game_starttime), NOT on a per-round respawn. The match
    /// boundary (Activate/Deactivate) already clears the whole map; this catches a mid-session game restart that
    /// re-runs game_starttime without tearing the gametype down, so a player's carried sub-point remainder is
    /// zeroed the QC way rather than persisting across the restart.
    /// (The spawn tail also flips this player out of the mid-round INGAME_STATUS_JOINING set —
    /// INGAME_STATUS_SET(JOINED) — so a late joiner who finally spawns stops being greyed; see
    /// <see cref="_joiningMidRound"/> / clanarena.join.late_join_observer. The eliminatedPlayers.SendFlags resend
    /// is N/A: the port captures the elimination list every frame.)
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        if (args.Player is not Player player || Handler is null)
            return false;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (now <= Handler.GameStartTime)
            _scoreDecimal.Remove(player); // QC float2int_decimal_fld = 0 (a fresh game)
        // QC ca PlayerSpawn tail: INGAME_STATUS_SET(player, INGAME_STATUS_JOINED). A player who actually spawns is
        // no longer a mid-round JOINING observer — flip them to JOINED so they stop being greyed on the scoreboard.
        _joiningMidRound.Remove(player);
        return false;
    }

    /// <summary>
    /// QC CA_CheckTeams (canRoundStart): the countdown may proceed once every active team has at least one
    /// live player (and at least one player is present). Mirrors Team_GetNumberOfAliveTeams == AVAILABLE_TEAMS.
    /// </summary>
    public bool CheckTeams()
    {
        int totalPlayers = 0;
        Round.AliveByTeam.Clear();
        foreach (int team in Teams.Active(TeamCount))
            Round.AliveByTeam[team] = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            int t = (int)p.Team;
            if (!Round.AliveByTeam.ContainsKey(t))
                continue;
            totalPlayers++;
            if (!p.IsDead)
                Round.AliveByTeam[t] += 1;
        }
        if (totalPlayers == 0)
            return false;
        foreach (var kv in Round.AliveByTeam)
            if (kv.Value == 0)
                return false; // a team has no living players → can't start yet
        return true;
    }

    /// <summary>
    /// QC CA_CheckWinner (canRoundEnd core): count living players per team; if the round time limit elapsed
    /// apply <see cref="PreventStalemate"/>, else award the round to the sole surviving team. Returns the
    /// winning team (&gt;0), -1 for a tie, -2 for an undecidable stalemate, or 0 if the round continues. When a
    /// winner is produced (&gt;0) it banks a round point and applies the match win condition.
    /// </summary>
    public int CheckWinner()
    {
        if (MatchEnded)
            return -2;

        // Recount alive teams over the roster.
        Round.AliveByTeam.Clear();
        int totalPlayers = 0;
        foreach (int team in Teams.Active(TeamCount))
            Round.AliveByTeam[team] = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            int t = (int)p.Team;
            if (!Round.AliveByTeam.ContainsKey(t))
                continue;
            totalPlayers++;
            if (!p.IsDead)
                Round.AliveByTeam[t] += 1;
        }
        if (totalPlayers == 0)
            return 0;

        int aliveTeams = 0, soleTeam = Teams.None;
        foreach (var kv in Round.AliveByTeam)
            if (kv.Value > 0) { aliveTeams++; soleTeam = kv.Key; }

        int winner = 0;
        // Round time limit elapsed → resolve by stalemate-prevention (or declare a stalemate).
        bool timeUp = Handler is not null && Handler.RoundEndTime > 0f
                      && (Api.Services is not null ? Api.Clock.Time : 0f) >= Handler.RoundEndTime;
        if (timeUp)
            winner = (PreventStalemateMode != 0) ? PreventStalemate() : -2;

        if (winner == 0)
        {
            if (aliveTeams > 1)
                return 0;                 // round still live
            winner = aliveTeams == 1 ? soleTeam : -1; // one team left → winner; zero → tie
        }

        if (winner > 0)
        {
            // QC CA_CheckWinner: ROUND_TEAM_WIN center+info to all, then bank the round point.
            string suffix = TeamSuffix(winner);
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, $"ROUND_TEAM_WIN_{suffix}");
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, $"ROUND_TEAM_WIN_{suffix}");
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner, ST_CA_ROUNDS, +1)
            Round.Number++;
            UpdateLeaderAndCheckLimit();
        }
        else
        {
            // QC CA_CheckWinner: -1 → ROUND_TIED, -2 → ROUND_OVER (no winner this round).
            string name = winner == -1 ? "ROUND_TIED" : "ROUND_OVER";
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, name);
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, name);
            Round.Number++; // tie / stalemate: a round elapsed, no team score
        }
        return winner;
    }

    /// <summary>QC <c>APP_TEAM_NUM</c> team-code → notification suffix (RED/BLUE/YELLOW/PINK).</summary>
    private static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "NEUTRAL",
    };

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, reset_map_players)</c> + <c>reset_map_global</c> (sv_clanarena.qc): on a round
    /// reset, every in-game-or-joining client is cleared and re-transmuted to a live Player — clear killcount,
    /// flip INGAME_STATUS_JOINING → JOINED, and snapshot prev_team (CA_RoundStart). reset_map_global sets
    /// allowed_to_spawn = true; in the port the round-phase gate already re-allows spawning, so this only needs to
    /// flush the per-round bookkeeping. Called by the host (GameWorld.ResetMap) on each map/round reset.
    /// </summary>
    public void ResetMapPlayers(IReadOnlyList<Player> roster)
    {
        _joiningMidRound.Clear();   // QC: INGAME_STATUS_SET(it, INGAME_STATUS_JOINED) for the whole joining set
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            p.GtKillCount = 0;      // QC reset_map_players: it.killcount = 0
            _prevTeam[p] = (int)p.Team; // QC CA_RoundStart: prev_team snapshot for MatchEnd restore
        }
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, MatchEnd_BeforeScores)</c> → <c>MatchEnd_RestoreSpectatorAndTeamStatus</c>
    /// (sv_clanarena.qc): at match end, before the per-player scores are dumped, drop the mid-round INGAME-joining
    /// flags (those players never actually competed) so the final scoreboard reflects who was really playing. The
    /// prev_team snapshot is released here too. Subscribed to <see cref="MutatorHooks.MatchEndBeforeScores"/> in
    /// <see cref="Activate"/>; fired live by the host's NextLevel (FireMatchEndBeforeScores).
    /// </summary>
    private bool OnMatchEndBeforeScores(ref MutatorHooks.MatchEndBeforeScoresArgs args)
    {
        _joiningMidRound.Clear();
        _prevTeam.Clear();
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ca, ClientCommand_Spectate)</c> (sv_clanarena.qc): when an in-game CA player runs
    /// the <c>spectate</c> command, force them to spectate (MUT_SPECCMD_FORCE) and, when sv_spectate allows it,
    /// send INFO_CA_LEAVE ("you'll spectate next round"). The host calls this from the spectate-command path; it
    /// records the leaver as a mid-round joiner-equivalent (so they re-enter next round) and emits the notice.
    /// Returns true if CA consumed the command (the leaver was a live player), matching QC's force result.
    /// </summary>
    public bool OnSpectateCommand(Player p)
    {
        if (p is null || p.IsObserver)
            return false; // already spectating/observer → CA does not force again (QC: only IS_PLAYER is forced)
        // QC: Send_Notification(NOTIF_ONE_ONLY, player, MSG_INFO, INFO_CA_LEAVE) when sv_spectate is on.
        if (Cvar("sv_spectate", 1f) != 0f)
            NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Info, "CA_LEAVE");
        _prevTeam.Remove(p);
        return true; // MUT_SPECCMD_FORCE: the live player is forced to spectate
    }

    /// <summary>
    /// QC <c>ca_LastPlayerForTeam_Notify</c> (sv_clanarena.qc:360): when a death/leave leaves exactly one living
    /// teammate, center-print "You are now alone!" (CENTER_ALONE) to that last survivor. Only fires while a round
    /// is active and started (not warmup). Call from the host's PlayerDies/disconnect path with the leaving player.
    /// </summary>
    public void NotifyLastPlayerForTeam(Player leaving, IReadOnlyList<Player> roster)
    {
        if (Handler is null || !Handler.IsRoundStarted) // QC: !warmup_stage && IsActive && IsRoundStarted
            return;
        Player? last = null;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (ReferenceEquals(p, leaving) || p.IsDead || !Teams.SameTeam(leaving, p))
                continue;
            if (last is null) last = p;
            else return; // more than one teammate alive → not alone
        }
        if (last is not null)
            NotificationSystem.Center(last, "ALONE");
    }

    /// <summary>
    /// QC CA_PreventStalemate: at the round time limit, break the tie by the configured criteria — bit0 picks
    /// the team with more survivors, bit1 the team with more total (health+armor). Returns the winning team,
    /// or -2 when even those are equal (a true stalemate). Survivor counts come from <see cref="Round.AliveByTeam"/>.
    /// </summary>
    public int PreventStalemate()
    {
        int mode = PreventStalemateMode;

        if ((mode & 1) != 0) // by survivor count
        {
            int best = Teams.None, second = Teams.None, bestN = -1, secondN = -1;
            foreach (var kv in Round.AliveByTeam)
            {
                if (kv.Value > bestN) { second = best; secondN = bestN; best = kv.Key; bestN = kv.Value; }
                else if (kv.Value > secondN) { second = kv.Key; secondN = kv.Value; }
            }
            if (bestN != secondN && best != Teams.None)
                return best;
        }

        if ((mode & 2) != 0) // by total team health + armor
        {
            var health = new Dictionary<int, int>();
            foreach (int team in Teams.Active(TeamCount)) health[team] = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                Player p = _roster[i];
                int t = (int)p.Team;
                if (p.IsDead || !health.ContainsKey(t))
                    continue;
                health[t] += (int)p.GetResource(ResourceType.Health) + (int)p.GetResource(ResourceType.Armor);
            }
            int best = Teams.None, second = Teams.None, bestH = -1, secondH = -1;
            foreach (var kv in health)
            {
                if (kv.Value > bestH) { second = best; secondH = bestH; best = kv.Key; bestH = kv.Value; }
                else if (kv.Value > secondH) { second = kv.Key; secondH = kv.Value; }
            }
            if (bestH != secondH && best != Teams.None)
                return best;
        }

        return -2; // genuinely equal — can't break the stalemate
    }

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;

    private static string CvarString(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    public int GetTeamRounds(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary);

    /// <summary>
    /// QC STAT(REDALIVE..PINKALIVE) source (<c>CA_count_alive_players</c>, sv_clanarena.qc:17-43): living
    /// players on <paramref name="teamCode"/> (a <see cref="Teams"/> color code) per the last recount.
    /// <see cref="CheckTeams"/> and <see cref="CheckWinner"/> both refresh
    /// <see cref="RoundState.AliveByTeam"/>, and the live path (CheckWinner — the round_handler canRoundEnd
    /// callback, polled every server frame while a round is live) recounts per frame, matching QC's
    /// per-frame recount. Inactive/unknown teams read 0.
    /// </summary>
    public int AliveCount(int teamCode) => Round.AliveByTeam.TryGetValue(teamCode, out int n) ? n : 0;

    /// <summary>
    /// QC <c>ca_isEliminated</c> (sv_clanarena.qc:243-250): (INGAME_JOINED &amp;&amp;
    /// (IS_DEAD || frags == FRAGS_PLAYER_OUT_OF_GAME)) || INGAME_JOINING. A live joined player greys when dead; a
    /// mid-round late joiner greys while they wait out the round (INGAME_STATUS_JOINING, tracked in
    /// <see cref="_joiningMidRound"/>). FRAGS_PLAYER_OUT_OF_GAME is N/A to CA (no per-life elimination column).
    /// </summary>
    public bool IsEliminatedPlayer(Player p) => p.IsDead || _joiningMidRound.Contains(p);

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: CA teams rank by the team primary slot ST_CA_ROUNDS (round wins). LeaderTeam / SecondTeam read the
        // flag-aware two-slot compare from GameScores (the source of truth); the round/lead-limit check uses it.
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamRounds(bestTeam);
        float limit = RoundLimit;
        if (limit > 0f && bestScore >= limit)
            MatchEnded = true;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamRounds(secondTeam)) >= leadLimit)
            MatchEnded = true;

        // QC MUTATOR_HOOKFUNCTION(ca, Scores_CountFragsRemaining) returns true: announce "N rounds left" once as
        // the leading team approaches the round limit (WinningCondition_Scores remaining-frags announcer).
        // For team modes the top/second "scores" are the ST_CA_ROUNDS team totals (primary slot).
        int secondScore = secondTeam == Teams.None ? 0 : GetTeamRounds(secondTeam);
        Scoring.GameScores.CountFragsRemaining(limit, leadLimit, bestScore, secondScore, suddenDeathEnding: false);
    }

    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s))
            return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }
}

/// <summary>
/// Round-elimination state shared by the round-based team modes (CA, FreezeTag) — the Godot-free essence
/// of QC's round_handler globals + CA_count_alive_players. Tracks the current round number and the live
/// player count per team. The wait/countdown/end-delay timing now lives in the shared
/// <see cref="RoundHandler"/>; this struct holds the per-round survivor bookkeeping.
/// </summary>
public sealed class RoundState
{
    /// <summary>1-based round counter (incremented when a round resolves).</summary>
    public int Number;

    /// <summary>Absolute sim time the round's timelimit expires (QC round_handler_GetEndTime); 0 = none.</summary>
    public float EndTime;

    /// <summary>Living players per team, keyed by <see cref="Teams"/> color code (recomputed each check).</summary>
    public readonly Dictionary<int, int> AliveByTeam = new();

    public void Reset()
    {
        Number = 0;
        EndTime = 0f;
        AliveByTeam.Clear();
    }
}
