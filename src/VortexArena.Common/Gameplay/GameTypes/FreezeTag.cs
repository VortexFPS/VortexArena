using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Freeze Tag — port of the Freeze Tag mutator/gametype
/// (common/gametypes/gametype/freezetag/{freezetag.qh,sv_freezetag.qc}). A round-based team mode like
/// Clan Arena, but instead of dying a fragged player is FROZEN (encased in ice, HP=1) and counts as
/// eliminated; a living teammate standing in range thaws them. A team is eliminated when all its players
/// are frozen. The last team standing wins the round (QC ST_FT_ROUNDS +1); first team to the round limit
/// (fraglimit) wins the match.
///
/// QC defaults (registered via REGISTER_MUTATOR; round timelimit + teams from g_freezetag_* cvars).
///
/// Scoring (QC freezetag_Add_Score + PlayerPreThink revive): kills give no frags
/// (GiveFragsForKill → 0); freezing an enemy → attacker SCORE +1, victim −1; freezing a teammate or
/// self → −1; reviving a teammate → reviver FREEZETAG_REVIVALS +1 plus SCORE — with revive_time_to_score
/// ON (the stock default) the SCORE accrues over time (+1 per 1.5 s reviving) rather than on completion,
/// and the revived player gets no SCORE. Round wins are the team score that drives the win condition.
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (<see cref="TeamBalance"/>);
///  - the freeze model: a fragged player becomes <see cref="FrozenState.Frozen"/> rather than respawning,
///    and counts as not-alive for the round check (<see cref="CheckWinner"/>);
///  - manual revive by a nearby teammate (<see cref="TryRevive"/>) clears the freeze;
///  - the freeze/revive SCORE matrix and round-limit win condition.
///
/// Faithfully ported (objective layer):
///  - the freeze applies the shared <see cref="StatusEffectsCatalog.Frozen"/> status effect (QC STAT(FROZEN));
///  - revive-progress accumulation + range geometry over the roster (<see cref="ReviveTick"/>, QC
///    PlayerPreThink IN_REVIVING_RANGE box-overlap) with the time-to-score model (revive_time_to_score 1.5 /
///    revive_speed_t2s 0.25, progress not cleared out of range, SCORE accrued over time), the auto-revive
///    timeout + smooth base-progress ramp (g_freezetag_frozen_maxtime / _revive_auto / _revive_auto_progress),
///    and the post-revive spawn shield (g_freezetag_revive_spawnshield);
///  - round warmup/countdown timing via the shared <see cref="RoundHandler"/> (QC round_handler_Spawn with
///    freezetag_CheckTeams / freezetag_CheckWinner), driven by <see cref="Tick"/>;
///  - FT's start-loadout (<see cref="OnSetStartItems"/> via the live SetStartItems hook, QC g_ft_start_health/
///    armor 100/100 + ammo).
///
/// Deferred (NOTE — cross-boundary): the ice entity model + waypoint visuals (CSQC), and bot freeing roles.
/// </summary>
[GameType]
public sealed class FreezeTag : GameType
{
    // ----- round-limit cvars + defaults -----
    // QC sv_freezetag.qh: GameRules_limit_score(g_freezetag_point_limit) / GameRules_limit_lead(
    // g_freezetag_point_leadlimit). Both ship at -1 = "use the mapinfo limit". The port has no mapinfo
    // limit plumbing, so -1 (or an unset cvar) falls back to the gametype legacy defaults (pointlimit 10,
    // leadlimit 6 — freezetag.qh INIT). A 0 means "play without limit" (matched by limit > 0 guards).
    private const string CvarPointLimit     = "g_freezetag_point_limit";     // FT-specific (mapinfo passthrough)
    private const string CvarPointLeadLimit = "g_freezetag_point_leadlimit"; // FT-specific (mapinfo passthrough)
    private const string CvarRoundLimit = "fraglimit";
    private const string CvarLeadLimit  = "leadlimit";
    private const float  DefaultRoundLimit = 10f; // QC freezetag.qh INIT pointlimit
    private const float  DefaultLeadLimit  = 6f;  // QC freezetag.qh INIT leadlimit

    // ----- team count cvars (g_freezetag_teams_override >= 2 ? override : g_freezetag_teams), 2..4 -----
    private const string CvarTeamsOverride = "g_freezetag_teams_override";
    private const string CvarTeams         = "g_freezetag_teams";
    private const int    DefaultTeams      = 2;

    // ----- revive geometry/score cvars (g_freezetag_*) -----
    private const string CvarReviveSpeed = "g_freezetag_revive_speed";       // progress/sec when a teammate is in range (t2s OFF)
    private const string CvarReviveSpeedT2s = "g_freezetag_revive_speed_t2s"; // progress/sec when a teammate is in range (t2s ON)
    private const string CvarReviveClearSpeed = "g_freezetag_revive_clearspeed"; // progress/sec decay out of range
    private const string CvarReviveExtraSize = "g_freezetag_revive_extra_size";  // bbox expansion for the range test
    private const string CvarReviveTimeToScore = "g_freezetag_revive_time_to_score"; // seconds of reviving per +1 SCORE (0 = off)
    private const string CvarReviveAuto = "g_freezetag_revive_auto";         // auto-thaw on/off gate
    private const string CvarReviveAutoProgress = "g_freezetag_revive_auto_progress"; // ramp the thaw ring from freeze time
    private const string CvarReviveSpawnShield = "g_freezetag_revive_spawnshield"; // post-revive spawn-shield seconds
    private const string CvarFrozenMaxtime = "g_freezetag_frozen_maxtime";   // auto-thaw after this many seconds (0 = off)
    private const string CvarWeaponArena = "g_freezetag_weaponarena"; // QC gametypes-server.cfg:429 default "most_available"
    private const string CvarStartHealth = "g_ft_start_health";
    private const string CvarStartArmor  = "g_ft_start_armor";
    private const string CvarWarmup = "g_freezetag_warmup";
    private const string CvarRoundTimelimit = "g_freezetag_round_timelimit";
    private const string CvarRoundEndDelay = "g_freezetag_round_enddelay";
    private const float  DefaultReviveSpeed = 0.4f;
    private const float  DefaultReviveSpeedT2s = 0.25f;
    private const float  DefaultReviveClearSpeed = 1.6f;
    private const float  DefaultReviveExtraSize = 100f;
    private const float  DefaultReviveTimeToScore = 1.5f;
    private const float  DefaultReviveSpawnShield = 1f;
    private const float  DefaultStartHealth = 100f;
    private const float  DefaultStartArmor  = 100f;

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>The roster the handler + revive geometry evaluate (set by the host before the per-frame calls).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    /// <summary>Provide the roster used by the round handler + <see cref="ReviveTick"/>.</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    public float ReviveClearSpeed => TryCvar(CvarReviveClearSpeed, out float s) ? s : DefaultReviveClearSpeed;
    public float ReviveExtraSize  => TryCvar(CvarReviveExtraSize, out float s) ? s : DefaultReviveExtraSize;
    public float FrozenMaxtime    => TryCvar(CvarFrozenMaxtime, out float s) ? s : 60f;
    public float StartHealth      => TryCvar(CvarStartHealth, out float s) ? s : DefaultStartHealth;
    public float StartArmor       => TryCvar(CvarStartArmor, out float s) ? s : DefaultStartArmor;

    /// <summary>QC g_freezetag_revive_speed_t2s: progress/sec used while time-to-score reviving is active.</summary>
    public float ReviveSpeedT2s => TryCvar(CvarReviveSpeedT2s, out float s) ? s : DefaultReviveSpeedT2s;

    /// <summary>QC g_freezetag_revive_time_to_score (default 1.5, ON): seconds of reviving per +1 SCORE; 0 = off.</summary>
    public float ReviveTimeToScore => TryCvar(CvarReviveTimeToScore, out float s) ? s : DefaultReviveTimeToScore;

    /// <summary>QC g_freezetag_revive_auto (default 1): whether frozen players auto-thaw at the maxtime timeout.</summary>
    public bool ReviveAuto => TryCvar(CvarReviveAuto, out float s) ? s != 0f : true;

    /// <summary>QC g_freezetag_revive_auto_progress (default 1): ramp the thaw ring smoothly from freeze time.</summary>
    public bool ReviveAutoProgress => TryCvar(CvarReviveAutoProgress, out float s) ? s != 0f : true;

    /// <summary>QC g_freezetag_revive_spawnshield (default 1 s): spawn-shield granted to a freshly revived player.</summary>
    public float ReviveSpawnShield => TryCvar(CvarReviveSpawnShield, out float s) ? s : DefaultReviveSpawnShield;

    // Per-team round wins (QC ST_FT_ROUNDS, team slot 1 — the TEAM primary) now live in the unified GameScores
    // two-slot team store — the source of truth (common/scores.qh). FT's player primary is SP_SCORE (the
    // freeze/revive ±1); the team primary is ST_FT_ROUNDS. Read/written via GetTeamRounds / GameScores.AddToTeam.

    /// <summary>Per-player freeze + revive-progress state (QC STAT(FROZEN)/STAT(REVIVE_PROGRESS)).</summary>
    public readonly Dictionary<Player, FrozenState> Frozen = new();

    /// <summary>
    /// Per-reviver fractional time-to-score accumulator (QC <c>.freezetag_revive_time</c>). While a reviver
    /// stands near a frozen teammate it accrues frametime/revive_time_to_score; each whole unit yields +1 SCORE.
    /// </summary>
    private readonly Dictionary<Player, float> _reviveTime = new();

    public readonly RoundState Round = new();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _startItemsHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _playerSpawnHandler;
    private HookHandler<MutatorHooks.GiveFragsForKillArgs>? _giveFragsHandler;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _weaponArenaHandler;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _regenHandler;
    private HookHandler<MutatorHooks.ItemTouchArgs>? _itemTouchHandler;
    private HookHandler<MutatorHooks.FragCenterMessageArgs>? _fragCenterMsgHandler;

    public FreezeTag()
    {
        NetName = "freezetag";
        DisplayName = "Freeze Tag";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC: GameRules_teams(true) + round_handler_Spawn(freezetag_CheckTeams, freezetag_CheckWinner) +
        // EliminatedPlayers_Init(freezetag_isEliminated). The round handler is created in Activate (needs the
        // roster); start-items are applied per spawn via the live SetStartItems hook (OnSetStartItems).
    }

    /// <summary>
    /// QC sv_freezetag.qh: <c>GameRules_spawning_teams(autocvar_g_freezetag_team_spawns)</c> — FreezeTag gates
    /// team spawns on g_freezetag_team_spawns (stock default 1, so it uses team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_freezetag_team_spawns", out float v) ? v != 0f : true;

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

    /// <summary>
    /// Round limit in force (QC GameRules_limit_score(g_freezetag_point_limit)). FT's own point-limit cvar
    /// wins (ships -1 = use the mapinfo/legacy default 10); -1 (or unset) falls back to the gametype default
    /// 10, then to the generic fraglimit. 0 == play without limit.
    /// </summary>
    public float RoundLimit
    {
        get
        {
            if (TryCvar(CvarPointLimit, out float pl) && pl >= 0f) return pl; // -1 = use default below
            if (TryCvar(CvarRoundLimit, out float fl) && fl > 0f) return fl;
            return DefaultRoundLimit;
        }
    }

    /// <summary>
    /// Lead limit in force (QC GameRules_limit_lead(g_freezetag_point_leadlimit)). FT's own lead-limit cvar
    /// wins (ships -1 = use the mapinfo/legacy default 6); -1 (or unset) falls back to the gametype default
    /// 6, then to the generic leadlimit. 0 == no lead limit.
    /// </summary>
    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarPointLeadLimit, out float ll) && ll >= 0f) return ll; // -1 = use default below
            if (TryCvar(CvarLeadLimit, out float l) && l > 0f) return l;
            return DefaultLeadLimit;
        }
    }

    /// <summary>Revive progress accrued per second while a teammate is in reviving range (g_freezetag_revive_speed).</summary>
    public float ReviveSpeed => TryCvar(CvarReviveSpeed, out float s) ? s : DefaultReviveSpeed;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Round.Reset();
        Frozen.Clear();
        _reviveTime.Clear();
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring
        // QC fragsleft_last reset: re-arm the "N rounds left" announcer (ft's Scores_CountFragsRemaining hook).
        Scoring.GameScores.ResetFragsRemaining();

        // QC sv_freezetag.qc GameRules_scoring(freezetag_teams, SFL_SORT_PRIO_PRIMARY, 0, {
        // field_team(ST_FT_ROUNDS, "rounds", PRIMARY); field(SP_FREEZETAG_REVIVALS, "revivals", 0); }): SP_SCORE
        // is the player primary (freeze/revive ±1); ST_SCORE (team slot 0) has no prio (stprio=0); ST_FT_ROUNDS
        // (team slot 1) "rounds" is the TEAM primary (round wins), plus the REVIVALS player column.
        Scoring.GameScores.ScoreRulesBasics(teams: true);
        Scoring.GameScores.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0
        Scoring.GameScores.SetTeamLabel(Scoring.GameScores.TeamSlotSecondary, "rounds", Scoring.ScoreFlags.SortPrioPrimary); // ST_FT_ROUNDS (slot 1)
        Scoring.GameScores.DeclareColumn("FREEZETAG_REVIVALS", Scoring.ScoreFlags.None, "revivals");
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score); // player primary = SP_SCORE
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)

        // QC round_handler_Spawn(freezetag_CheckTeams, freezetag_CheckWinner, NULL).
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = CheckTeams,
            CanRoundEnd = () => CheckWinner() != 0,
            OnRoundStart = () => { Frozen.Clear(); _reviveTime.Clear(); }, // QC reset_map_players: thaw everyone + clear revive-time accumulators at the start of a new round
        };
        // QC gametypes-server.cfg / round_handler_Init(5, g_freezetag_warmup, g_freezetag_round_timelimit):
        // warmup 10, round_timelimit 360, round_enddelay 0 (the shipped Base defaults). The fallbacks only
        // bite when there is no cvar store (unit tests) — keep them Base-faithful.
        float warmup = TryCvar(CvarWarmup, out float w) ? w : 10f;
        float rtl = TryCvar(CvarRoundTimelimit, out float t) ? t : 360f;
        float edly = TryCvar(CvarRoundEndDelay, out float e) ? e : 0f;
        Handler.Init(edly, warmup, rtl);

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, SetStartItems): the FT spawn loadout — g_ft_start_health/armor (100/100) +
        // full ammo (60/320/160/180/0). SpawnSystem.ComputeStartItems fires this live (the readplayerstartcvars seam).
        _startItemsHandler = OnSetStartItems;
        MutatorHooks.SetStartItems.Add(_startItemsHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, Damage_Calculate): frozen players take 0 health damage (g_frozen_force
        // knockback scaling), enemy hits speed their auto-thaw (g_freezetag_revive_auto_reducible), and fall
        // damage can revive (g_frozen_revive_falldamage). Subscribed onto the live damage pipeline.
        _damageCalcHandler = OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, PlayerSpawn) (sv_freezetag.qc:530): a player who spawns while a round is
        // already live joins AS FROZEN (CENTER_FREEZETAG_SPAWN_LATE), so a mid-round joiner can't run around the
        // live round. Round-reset respawns happen during the countdown phase (IsRoundStarted false), so they're
        // naturally excluded — only a genuine mid-round join is caught.
        _playerSpawnHandler = OnPlayerSpawn;
        MutatorHooks.PlayerSpawn.Add(_playerSpawnHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, GiveFragsForKill, CBC_ORDER_FIRST): no normal frags are counted in Freeze
        // Tag — the only scoring is the freeze ±1 SCORE matrix (freezetag_Add_Score) + the round wins. Zero the
        // per-kill frag delta so an enemy freeze doesn't ALSO award a +1 engine kill on top (mirror CA/Keepaway).
        _giveFragsHandler = OnGiveFragsForKill;
        MutatorHooks.GiveFragsForKill.Add(_giveFragsHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, SetWeaponArena): default the weapon arena to g_freezetag_weaponarena
        // ("most_available", gametypes-server.cfg:429) when no arena is otherwise set, so FT players spawn with
        // (nearly) the full arsenal on top of the start loadout rather than just the start weapons.
        _weaponArenaHandler = OnSetWeaponArena;
        MutatorHooks.SetWeaponArena.Add(_weaponArenaHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, PlayerRegen): a frozen player's health/armor regen is suppressed (they
        // stay at HP=1 while frozen, return STAT(FROZEN)).
        _regenHandler = OnPlayerRegen;
        MutatorHooks.PlayerRegen.Add(_regenHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, ItemTouch): a frozen toucher does NOT collect items (return
        // MUT_ITEMTOUCH_RETURN) — a pushed/teleported frozen body can't illegitimately pick items up.
        _itemTouchHandler = OnItemTouch;
        MutatorHooks.ItemTouch.Add(_itemTouchHandler);

        // QC MUTATOR_HOOKFUNCTION(ft, FragCenterMessage) (sv_freezetag.qc): swap the per-kill personal centerprint
        // from the generic FRAG/FRAGGED ("You fragged X" / "You were fragged by Y") to CHOICE_FRAG_FREEZE /
        // CHOICE_FRAGGED_FREEZE ("You froze X" / "You were frozen by Y"), and SUPPRESS both lines entirely when the
        // target was ALREADY frozen (a void/lava re-kill of a frozen body shouldn't print a fresh "froze" message).
        // The live FragCenterMessage.Call (Scores.cs:809) fires from the obituary BEFORE FreezeTag.OnDeath, so
        // IsFrozen(target) here still reflects the pre-death state — exactly QC's STAT(FROZEN, frag_target) test.
        _fragCenterMsgHandler = OnFragCenterMessage;
        MutatorHooks.FragCenterMessage.Add(_fragCenterMsgHandler);
    }

    /// <summary>
    /// Advance the FT round handler one frame (QC round_handler_Think). Resolves the round when
    /// freezetag_CheckWinner produces a result. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick() => Handler?.Tick();

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (_startItemsHandler is not null) { MutatorHooks.SetStartItems.Remove(_startItemsHandler); _startItemsHandler = null; }
        if (_damageCalcHandler is not null) { MutatorHooks.DamageCalculate.Remove(_damageCalcHandler); _damageCalcHandler = null; }
        if (_playerSpawnHandler is not null) { MutatorHooks.PlayerSpawn.Remove(_playerSpawnHandler); _playerSpawnHandler = null; }
        if (_giveFragsHandler is not null) { MutatorHooks.GiveFragsForKill.Remove(_giveFragsHandler); _giveFragsHandler = null; }
        if (_weaponArenaHandler is not null) { MutatorHooks.SetWeaponArena.Remove(_weaponArenaHandler); _weaponArenaHandler = null; }
        if (_regenHandler is not null) { MutatorHooks.PlayerRegen.Remove(_regenHandler); _regenHandler = null; }
        if (_itemTouchHandler is not null) { MutatorHooks.ItemTouch.Remove(_itemTouchHandler); _itemTouchHandler = null; }
        if (_fragCenterMsgHandler is not null) { MutatorHooks.FragCenterMessage.Remove(_fragCenterMsgHandler); _fragCenterMsgHandler = null; }
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>True if <paramref name="p"/> is currently frozen (QC STAT(FROZEN)).</summary>
    public bool IsFrozen(Player p) => Frozen.TryGetValue(p, out var st) && st.IsFrozen;

    /// <summary>QC freezetag_isEliminated: a frozen OR dead player counts as out for the round check.</summary>
    public bool IsEliminated(Player p) => p.IsDead || IsFrozen(p);

    /// <summary>
    /// QC PlayerDies (round active): the fragged player is frozen instead of respawning. Applies the
    /// freeze/score matrix (<see cref="Freeze"/>). Returns false so other Death subscribers run.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        // QC freezetag PlayerDies (sv_freezetag.qc:484): freezetag_LastPlayerForTeam_Notify(frag_target) — if this
        // freeze leaves exactly one still-living (unfrozen) teammate, that lone survivor gets CENTER_ALONE ("You
        // are now alone!"). Evaluated BEFORE the freeze takes effect, with the victim passed as `leaving` (and
        // excluded), so the count reflects who remains alive once the victim is frozen.
        NotifyLastPlayerForTeam(victim, _roster);

        Freeze(victim, ev.Attacker as Player);
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, PlayerSpawn)</c> (sv_freezetag.qc:530): a player who (re)spawns while a
    /// round is already live joins AS FROZEN — they get CENTER_FREEZETAG_SPAWN_LATE and are frozen with a NULL
    /// attacker (no score). Round-RESET respawns run during the countdown phase (Handler.IsRoundStarted is
    /// false then), so a player materializing for the fresh round is naturally excluded; only a genuine
    /// mid-round joiner is caught. No-op if the player is already frozen or the match has ended.
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        if (MatchEnded || args.Player is not Player player)
            return false;
        if (Handler is not { IsRoundStarted: true })
            return false;
        if (IsFrozen(player))
            return false;

        // QC: Send_Notification(NOTIF_ONE, player, MSG_CENTER, CENTER_FREEZETAG_SPAWN_LATE); freezetag_Freeze(player, NULL);
        // Freeze with announce=false: only the SPAWN_LATE center line fires (Base's freezetag_Freeze itself is silent).
        NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "FREEZETAG_SPAWN_LATE");
        Freeze(player, null, announce: false);
        return false;
    }

    /// <summary>
    /// Freeze <paramref name="targ"/> (QC freezetag_Freeze + freezetag_Add_Score): mark frozen with HP 1,
    /// reset revive progress, and apply the score matrix — self freeze → victim −1; teammate freeze →
    /// attacker −1 + victim −1; enemy freeze → attacker +1 + victim −1; and a NULL/non-player attacker
    /// (frozen by the gametype rules themselves) → NO score change. No-op if already frozen.
    /// </summary>
    public void Freeze(Player targ, Player? attacker, bool announce = true)
    {
        if (IsFrozen(targ))
            return;

        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        var st = GetState(targ);
        st.IsFrozen = true;
        st.ReviveProgress = 0f;
        st.FrozenTime = now;
        targ.RevivalTime = 0f; // QC freezetag_Freeze (sv_freezetag.qc:227): clear the last-revive timestamp.
        // QC freezetag_Freeze: arm the auto-thaw timer only when revive_auto is on AND maxtime > 0.
        float maxtime = FrozenMaxtime;
        st.FrozenTimeout = (ReviveAuto && maxtime > 0f) ? now + maxtime : 0f;
        // QC sets RES_HEALTH = 1 on freeze; the entity stays "dead" for combat but counts via IsFrozen.
        targ.SetResource(ResourceType.Health, 1f);
        // QC STAT(FROZEN): apply the shared Frozen status effect so the rest of the sim sees the freeze.
        if (StatusEffectsCatalog.Frozen is { } frozenDef)
            StatusEffectsCatalog.Apply(targ, frozenDef, maxtime > 0f ? maxtime : 0f, 1f, attacker);

        // ----- score matrix (QC freezetag_Add_Score, sv_freezetag.qc:162-181) -----
        if (ReferenceEquals(attacker, targ))
        {
            // QC (attacker == targ): froze your own dumb self — victim −1 (counted as suicide already).
            targ.ScoreFrags -= 1;
        }
        else if (attacker is not null) // QC IS_PLAYER(attacker): froze a teammate or an enemy
        {
            if (Teams.SameTeam(attacker, targ))
                attacker.ScoreFrags -= 1; // froze a teammate
            else
                attacker.ScoreFrags += 1; // froze an enemy
            targ.ScoreFrags -= 1;
        }
        // QC else: NULL / non-player attacker — got frozen by the gametype rules themselves → NO score change.

        // QC PlayerDies (sv_freezetag.qc:516-525): announce the freeze. Self/NULL freeze → CENTER_FREEZETAG_SELF to
        // the victim + INFO_FREEZETAG_SELF to all; an enemy/teammate freeze → INFO_FREEZETAG_FREEZE to all
        // (kill-feed "X was frozen by Y"). The spawn-late path (QC PlayerSpawn) sends its OWN center notification
        // (CENTER_FREEZETAG_SPAWN_LATE) and suppresses these — Base's freezetag_Freeze itself never notifies.
        if (!announce)
        {
            // no freeze-line notification (spawn-as-frozen)
        }
        else if (ReferenceEquals(attacker, targ) || attacker is null)
        {
            NotificationSystem.Send(NotifBroadcast.One, targ, MsgType.Center, "FREEZETAG_SELF");
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "FREEZETAG_SELF", targ.NetName);
        }
        else
        {
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "FREEZETAG_FREEZE", targ.NetName, attacker.NetName);
        }
    }

    /// <summary>
    /// QC freezetag_Unfreeze: clear the freeze and restore the player to alive. Used both by a successful
    /// revive and by round reset. Does not award score by itself (the revive path does that).
    /// </summary>
    public void Unfreeze(Player targ, float spawnShieldSeconds = 0f)
    {
        if (Frozen.TryGetValue(targ, out var st))
        {
            st.IsFrozen = false;
            st.ReviveProgress = 0f;
            st.FrozenTimeout = 0f;
            st.FrozenTime = 0f;
        }
        targ.DeadState = DeadFlag.No;
        // QC freezetag_Unfreeze (sv_freezetag.qc:262): stamp the revive time so the ice nade's 1.5s re-freeze
        // grace (ice.qc:59) and other revive-window checks can see a just-thawed player.
        targ.RevivalTime = Api.Services is not null ? Api.Clock.Time : 0f;
        // QC freezetag_Unfreeze: pauseregen_finished = time + g_balance_pause_health_regen — a freshly thawed
        // player's health/armor regen is paused for the same window a damaged player's is, so revive doesn't
        // instantly start topping them up.
        float nowU = Api.Services is not null ? Api.Clock.Time : 0f;
        float pauseRegen = TryCvar("g_balance_pause_health_regen", out float pr) ? pr : 5f;
        targ.PauseRegenFinished = System.Math.Max(targ.PauseRegenFinished, nowU + pauseRegen);
        // QC freezetag_Unfreeze: clear the Frozen status effect and restore full start health.
        if (StatusEffectsCatalog.Frozen is { } frozenDef)
            StatusEffectsCatalog.Remove(targ, frozenDef);
        targ.SetResource(ResourceType.Health, StartHealth);
        // QC PlayerPreThink full-revive: spawnshieldtime = time + g_freezetag_revive_spawnshield (so a freshly
        // thawed player isn't instantly re-frozen). The damage pipeline reads Entity.SpawnShieldExpire.
        if (spawnShieldSeconds > 0f)
            targ.SpawnShieldExpire = (Api.Services is not null ? Api.Clock.Time : 0f) + spawnShieldSeconds;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, Damage_Calculate)</c> (sv_freezetag.qc:618): the frozen-player damage
    /// rules. On every hit, save the target's armor (for a soft-kill restore). For a frozen target:
    ///  - <b>auto-thaw reduction</b> (g_freezetag_revive_auto_reducible, default 1): an enemy hit (or any hit
    ///    when set to -1) accumulates hit force up to <c>_reducible_maxforce</c> (400), converts it via
    ///    <c>_reducible_forcefactor</c> (0.01), and subtracts that many seconds from the auto-thaw timeout —
    ///    so shooting a frozen enemy speeds their thaw (the core "don't shoot frozen enemies" tactic);
    ///  - <b>damage immunity</b>: a non-NEEDKILL/non-teamchange hit deals 0 health damage and its knockback is
    ///    scaled by g_frozen_force (0.6) — the HP=1 frozen body can't be chip-killed;
    ///  - <b>fall-damage revive</b> (g_frozen_revive_falldamage, default 0/off): a hard enough DEATH_FALL hit
    ///    thaws the player to g_frozen_revive_falldamage_health (40) instead.
    /// The void/lava soft-kill relocate (g_frozen_damage_trigger 0, default 1 = die) needs a spawn-point
    /// selector that lives in the server layer and is intentionally NOT done here.
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (args.Target is not Player targ)
            return false;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC: frag_target.freezetag_frozen_armor = GetResource(frag_target, RES_ARMOR) — snapshot every hit.
        targ.FrozenArmor = targ.GetResource(ResourceType.Armor);

        if (!IsFrozen(targ))
            return false;

        float maxtime = FrozenMaxtime;
        bool autoRevive = ReviveAuto && maxtime > 0f;
        var st = GetState(targ);

        // ----- auto-thaw reduction (QC sv_freezetag.qc:628-654) -----
        int reducible = TryCvar("g_freezetag_revive_auto_reducible", out float rr) ? (int)rr : 1;
        if (autoRevive && reducible != 0)
        {
            bool enemyOrAlways = reducible < 0
                || (args.Attacker is Player atk && !Teams.SameTeam(atk, targ));
            if (enemyOrAlways && st.FrozenTimeout > now)
            {
                float t = 0f;
                if (System.Math.Abs(reducible) == 1)
                {
                    float maxforce = TryCvar("g_freezetag_revive_auto_reducible_maxforce", out float mf) ? mf : 400f;
                    float forcefactor = TryCvar("g_freezetag_revive_auto_reducible_forcefactor", out float ff) ? ff : 0.01f;
                    t = args.Force.Length();
                    // QC: limit hit force considered at once (Strength powerup / multi-projectile weapons).
                    if (targ.FrozenForce + t > maxforce)
                    {
                        t = System.Math.Max(0f, maxforce - targ.FrozenForce);
                        targ.FrozenForce = maxforce;
                    }
                    else
                        targ.FrozenForce += t;
                    t *= forcefactor;
                }
                st.FrozenTimeout -= t;
                if (st.FrozenTimeout < now)
                    st.FrozenTimeout = now;
            }
        }

        // ----- nade self-revive (QC nades Damage_Calculate, sv_nades.qc:883-893) -----
        // A frozen player hit by their OWN nade (DEATH_NADE) within 0.1s of the toss is thawed instead of
        // damaged: restore g_freezetag_revive_nade_health, suppress the hit, and announce REVIVED_NADE +
        // REVIVE_SELF. The nade mutator already zeroes damage/force on the same hook, but the actual unfreeze
        // lives here (FreezeTag owns freezetag_Unfreeze), so do it self-contained for hook-order independence.
        if (TryCvar("g_freezetag_revive_nade", out float rn) && rn != 0f
            && args.Attacker is Player nadeSelf && ReferenceEquals(nadeSelf, targ)
            && DeathTypes.BaseOf(args.DeathType) == Nades.NadeDeathTypes.Nade
            && args.Inflictor is { } nadeInfl && now - nadeInfl.NadeTossTime <= 0.1f)
        {
            float nadeHealth = TryCvar("g_freezetag_revive_nade_health", out float nh) ? nh : 40f;
            Unfreeze(targ);
            targ.SetResource(ResourceType.Health, nadeHealth);
            args.Damage = 0f;
            args.Force = Vector3.Zero;
            // QC Send_Effect(EFFECT_ICEORGLASS) is host-side VFX.
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "FREEZETAG_REVIVED_NADE", targ.NetName);
            NotificationSystem.Send(NotifBroadcast.One, targ, MsgType.Center, "FREEZETAG_REVIVE_SELF");
            return false;
        }

        // ----- frozen damage immunity + fall-damage revive (QC sv_freezetag.qc:656-670) -----
        string dt = args.DeathType;
        if (!IsNeedKill(dt) && !DeathTypes.IsTeamChange(dt))
        {
            // QC fall-damage revive (default off): a hard enough fall thaws the player to falldamage_health.
            float fallRevive = TryCvar("g_frozen_revive_falldamage", out float fr) ? fr : 0f;
            if (fallRevive > 0f && DeathTypes.BaseOf(dt) == DeathTypes.Fall && args.Damage >= fallRevive)
            {
                float fallHealth = TryCvar("g_frozen_revive_falldamage_health", out float fh) ? fh : 40f;
                Unfreeze(targ);
                targ.SetResource(ResourceType.Health, fallHealth);
                // QC Send_Effect(EFFECT_ICEORGLASS) is host-side VFX.
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "FREEZETAG_REVIVED_FALL", targ.NetName);
                NotificationSystem.Send(NotifBroadcast.One, targ, MsgType.Center, "FREEZETAG_REVIVE_SELF");
            }

            // QC: a frozen player takes NO health damage; knockback is scaled by g_frozen_force (0.6).
            float frozenForce = TryCvar("g_frozen_force", out float gff) ? gff : 0.6f;
            args.Damage = 0f;
            args.Force *= frozenForce;
        }

        return false;
    }

    /// <summary>
    /// QC <c>ITEM_DAMAGE_NEEDKILL(dt)</c> (server/items/items.qh:123): the void(HURTTRIGGER)/slime/lava/swamp
    /// "must-kill" deathtypes — the only damage that still kills a frozen player (so they don't float forever in
    /// a kill volume). All map to the port's environment deathtype tags.
    /// </summary>
    private static bool IsNeedKill(string? deathType)
    {
        string b = DeathTypes.BaseOf(deathType);
        return b == DeathTypes.Void || b == DeathTypes.Slime || b == DeathTypes.Lava || b == DeathTypes.Swamp;
    }

    /// <summary>
    /// Advance revive of <paramref name="frozen"/> by a nearby living teammate <paramref name="reviver"/>
    /// (QC PlayerPreThink reviving loop, distilled). Accumulates <see cref="ReviveSpeed"/> * <paramref name="dt"/>
    /// into the freeze's progress; on reaching 1.0 the player is thawed and BOTH get SCORE +1 (reviver also
    /// FREEZETAG_REVIVALS, modeled here just as the score). The caller supplies the range test. Returns true
    /// if the player was fully revived this call.
    /// </summary>
    public bool TryRevive(Player frozen, Player reviver, float dt)
    {
        if (!IsFrozen(frozen) || frozen.IsDead)
            return false;
        if (!Teams.SameTeam(frozen, reviver) || reviver.IsDead || IsFrozen(reviver))
            return false;

        var st = GetState(frozen);
        // QC PlayerPreThink: t2s ON uses revive_speed_t2s (0.25); t2s OFF uses revive_speed*(1-base_progress).
        // From a single explicit reviver there is no auto-progress floor, so base_progress is 0 here.
        bool t2s = ReviveTimeToScore > 0f;
        float spd = t2s ? ReviveSpeedT2s : ReviveSpeed;
        st.ReviveProgress = System.Math.Clamp(st.ReviveProgress + dt * System.Math.Max(1f / 60f, spd), 0f, 1f);

        // QC: with t2s active, score accrues over time (revive_time_to_score seconds per +1), not on completion.
        if (t2s)
        {
            float acc = (_reviveTime.TryGetValue(reviver, out float rt) ? rt : 0f) + dt / ReviveTimeToScore;
            while (acc > 1f) { reviver.ScoreFrags += 1; acc -= 1f; }
            _reviveTime[reviver] = acc;
        }

        if (st.ReviveProgress < 1f)
            return false;

        Unfreeze(frozen, ReviveSpawnShield);
        // QC freezetag PlayerPreThink full-revive: each nearby reviver gets FREEZETAG_REVIVALS +1 (and, with t2s
        // OFF, SCORE +1 on completion; with t2s ON the SCORE was already awarded by the time-to-score accrual).
        AddRevival(reviver);
        if (!t2s)
            reviver.ScoreFrags += 1;
        GiveReviveNadeBonus(reviver); // QC sv_freezetag.qc:846 nades_GiveBonus(it, g_nades_bonus_score_low)
        return true;
    }

    /// <summary>
    /// QC PlayerPreThink full-revive (sv_freezetag.qc:846): <c>nades_GiveBonus(it, autocvar_g_nades_bonus_score_low)</c>
    /// — a completed revive accrues a "low" amount of bonus-nade score toward the reviver's next bonus grenade.
    /// No-op unless the nades mutator is enabled (NadeBonus.GiveBonus self-gates on g_nades + g_nades_bonus).
    /// </summary>
    private static void GiveReviveNadeBonus(Player reviver)
    {
        float low = TryCvar("g_nades_bonus_score_low", out float s) ? s : 20f; // mutators.cfg g_nades_bonus_score_low 20
        Nades.NadeBonus.GiveBonus(reviver, low);
    }

    /// <summary>QC <c>GameRules_scoring_add(it, FREEZETAG_REVIVALS, +1)</c>: credit a reviver one revival (the
    /// scoreboard "revivals" column). Routed through the unified score table so it networks + sorts.</summary>
    private static void AddRevival(Player reviver)
    {
        Scoring.ScoreField? revivals = Scoring.GameScores.Field("FREEZETAG_REVIVALS");
        if (revivals is not null) Scoring.GameScores.AddToPlayer(reviver, revivals, 1);
    }

    /// <summary>
    /// QC PlayerPreThink revive loop, ported over the roster: for every frozen player, accumulate revive
    /// progress while a living teammate is within reviving range (box-overlap expanded by
    /// <see cref="ReviveExtraSize"/>). With time-to-score active (the stock default) progress advances at
    /// revive_speed_t2s, is NOT cleared when nobody is near (it sticks at max of the manual progress and the
    /// auto-thaw ramp), and each in-range reviver accrues SCORE over time (+1 per revive_time_to_score sec);
    /// with t2s off, progress advances at revive_speed*(1-base) and decays at clearspeed out of range, and the
    /// SCORE +1 is awarded on completion. The frozen-maxtime auto-thaw (gated on revive_auto) provides a
    /// smooth base-progress ramp (revive_auto_progress) and a last-resort thaw that credits nobody. On reaching
    /// 1.0 the player is unfrozen (with a spawn shield) and manual revivers get FREEZETAG_REVIVALS +1. Call once
    /// per frame with the frame delta after <see cref="SetRoster"/>. Returns the number of players thawed.
    /// </summary>
    public int ReviveTick(float dt)
    {
        if (MatchEnded || _roster.Count == 0)
            return 0;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float extra = ReviveExtraSize;
        float clear = ReviveClearSpeed;
        float maxtime = FrozenMaxtime;
        bool t2s = ReviveTimeToScore > 0f;
        float t2sTime = ReviveTimeToScore;
        bool autoRevive = ReviveAuto && maxtime > 0f;
        bool autoProgress = autoRevive && ReviveAutoProgress;
        float spawnShield = ReviveSpawnShield;
        int thawed = 0;

        // QC PlayerPreThink: player.freezetag_frozen_force = 0 every frame — the auto-thaw reduction force
        // accumulator (Damage_Calculate) is per-frame, so clear it for the whole roster before the revive scan.
        for (int i = 0; i < _roster.Count; i++)
            _roster[i].FrozenForce = 0f;

        for (int i = 0; i < _roster.Count; i++)
        {
            Player frozen = _roster[i];
            if (!IsFrozen(frozen) || frozen.IsDead)
                continue;
            var st = GetState(frozen);

            // Collect nearby living unfrozen teammates (QC IN_REVIVING_RANGE box-overlap) — the revivers chain.
            // n == -1 marks an automatic revive of last resort (no manual reviver but the maxtime timeout elapsed).
            int n = 0;
            for (int j = 0; j < _roster.Count; j++)
            {
                Player it = _roster[j];
                if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                    continue;
                if (!Teams.SameTeam(it, frozen))
                    continue;
                if (InRevivingRange(frozen, it, extra))
                    n++;
            }
            if (n == 0 && st.FrozenTimeout > 0f && now >= st.FrozenTimeout)
                n = -1;

            // QC WaypointSprite_UpdateSprites: WP_Reviving when n>0 (a reviver is actively in range). Mirror
            // that state into FrozenState.HasReviver so CollectWaypoints can reproduce the exact QC condition
            // (n>0 || progress>0.95) without a second full O(n) scan.
            st.HasReviver = n > 0;

            // QC base_progress: with auto-revive-progress on, the thaw ring fills smoothly from freeze time so the
            // bar reflects the auto-thaw countdown even with no reviver near.
            float baseProgress = 0f;
            if (autoProgress && st.FrozenTimeout > 0f)
                baseProgress = System.Math.Clamp(1f - (st.FrozenTimeout - now) / maxtime, 0f, 1f);

            if (n == 0)
            {
                // No teammate nearby (and no auto-thaw): hold/clear the bar per the t2s mode.
                if (t2s)
                {
                    // With time-to-score active, progress is NOT cleared (it would let players stack points by
                    // entering/exiting the zone); it sticks at max(manual progress, auto-ramp floor). A higher
                    // manual progress also shortens the remaining auto-thaw time.
                    if (st.ReviveProgress > baseProgress)
                    {
                        baseProgress = st.ReviveProgress;
                        if (autoRevive)
                            st.FrozenTimeout = now + maxtime * (1f - st.ReviveProgress);
                    }
                    st.ReviveProgress = baseProgress;
                }
                else
                {
                    // t2s OFF: decay toward the auto-ramp floor at clearspeed (scaled by 1-base_progress).
                    float decayed = st.ReviveProgress - dt * clear * (1f - baseProgress);
                    st.ReviveProgress = System.Math.Clamp(decayed, baseProgress, 1f);
                }
                continue;
            }

            // At least one reviver (or the auto-thaw of last resort): advance progress.
            // QC: t2s ON uses revive_speed_t2s; t2s OFF uses revive_speed*(1-base_progress).
            float spd = t2s ? ReviveSpeedT2s : ReviveSpeed * (1f - baseProgress);
            st.ReviveProgress = System.Math.Clamp(st.ReviveProgress + dt * System.Math.Max(1f / 60f, spd), baseProgress, 1f);

            // QC: with t2s active each in-range reviver accrues time → +1 SCORE per revive_time_to_score seconds.
            if (t2s && n > 0)
            {
                for (int j = 0; j < _roster.Count; j++)
                {
                    Player it = _roster[j];
                    if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                        continue;
                    if (!Teams.SameTeam(it, frozen) || !InRevivingRange(frozen, it, extra))
                        continue;
                    float acc = (_reviveTime.TryGetValue(it, out float rt) ? rt : 0f) + dt / t2sTime;
                    while (acc > 1f) { it.ScoreFrags += 1; acc -= 1f; }
                    _reviveTime[it] = acc;
                }
            }

            if (st.ReviveProgress >= 1f)
            {
                // QC: frozen_time = time - freezetag_frozen_time, captured before Unfreeze resets the timer.
                float frozenTime = System.MathF.Round(now - st.FrozenTime);
                Unfreeze(frozen, spawnShield);
                thawed++;
                // QC: an auto-revive (n == -1) credits nobody; a manual revive credits EVERY nearby reviver with
                // FREEZETAG_REVIVALS +1 (and, with t2s OFF, SCORE +1 — t2s ON already scored via the accrual).
                if (n == -1)
                {
                    // QC sv_freezetag.qc:835-836: an auto-thaw of last resort — center to the player + info to all.
                    NotificationSystem.Send(NotifBroadcast.One, frozen, MsgType.Center, "FREEZETAG_AUTO_REVIVED", frozenTime);
                    NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "FREEZETAG_AUTO_REVIVED", frozen.NetName, frozenTime);
                }
                else if (n > 0)
                {
                    Player? firstReviver = null;
                    for (int j = 0; j < _roster.Count; j++)
                    {
                        Player it = _roster[j];
                        if (ReferenceEquals(it, frozen) || it.IsDead || IsFrozen(it))
                            continue;
                        if (Teams.SameTeam(it, frozen) && InRevivingRange(frozen, it, extra))
                        {
                            firstReviver ??= it;
                            AddRevival(it);
                            if (!t2s)
                                it.ScoreFrags += 1;
                            GiveReviveNadeBonus(it); // QC sv_freezetag.qc:846 nades_GiveBonus(it, g_nades_bonus_score_low)
                        }
                    }
                    // QC sv_freezetag.qc:849-851: center to the revived player (named by the first reviver), center to
                    // the first reviver (named by the revived), and a kill-feed info line to all.
                    if (firstReviver is not null)
                    {
                        NotificationSystem.Send(NotifBroadcast.One, frozen, MsgType.Center, "FREEZETAG_REVIVED", firstReviver.NetName);
                        NotificationSystem.Send(NotifBroadcast.One, firstReviver, MsgType.Center, "FREEZETAG_REVIVE", frozen.NetName);
                        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "FREEZETAG_REVIVED", frozen.NetName, firstReviver.NetName);
                    }
                }
            }
        }

        // Mirror each roster player's per-player FrozenState.ReviveProgress onto the networkable entity field so
        // the player snapshot (NetEntityState.ReviveProgress) can ship the thaw ring to clients (QC STAT(REVIVE_
        // PROGRESS)). Frozen players carry their accumulated 0..1; everyone else reads 0 (Unfreeze reset it).
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            p.ReviveProgress = Frozen.TryGetValue(p, out var fs) ? fs.ReviveProgress : 0f;
        }
        return thawed;
    }

    /// <summary>
    /// QC IN_REVIVING_RANGE: the reviver's bbox (expanded by <paramref name="extra"/>) overlaps the frozen
    /// player's bbox. Uses the entities' absolute bounds (falls back to a sphere test if bounds are unset).
    /// </summary>
    public static bool InRevivingRange(Player frozen, Player reviver, float extra)
    {
        Vector3 e = new(extra, extra, extra);
        Vector3 aMin = reviver.AbsMin - e, aMax = reviver.AbsMax + e;
        Vector3 bMin = frozen.AbsMin, bMax = frozen.AbsMax;
        // If bounds are degenerate (headless without a physics link), fall back to an origin distance test.
        if (aMin == aMax && bMin == bMax)
            return Vector3.Distance(frozen.Origin, reviver.Origin) <= extra;
        return aMin.X <= bMax.X && aMax.X >= bMin.X
            && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
            && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    /// <summary>
    /// QC freezetag_Freeze (WaypointSprite_Spawn WP_Frozen at '0 0 64', owner = the frozen player) + PlayerPreThink
    /// (WaypointSprite_UpdateSprites swap to WP_Reviving + UpdateHealth(REVIVE_PROGRESS)): one overhead/radar sprite
    /// per frozen player, following them, with a thaw-progress health bar. The sprite reads "Frozen!" (icy-blue),
    /// switching to "Reviving" (orange) once thaw progress has started — the proxy for QC's n&gt;0 reviving state.
    /// Rebuilt each tick like CTF's flag sprites; shipped via ServerNet.SendWaypoints (GameType.CollectWaypoints).
    /// </summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            if (!Frozen.TryGetValue(p, out var st) || !st.IsFrozen || p.IsDead)
                continue;
            int team = (int)p.Team;
            // QC WaypointSprite_UpdateSprites (sv_freezetag.qc): WP_Reviving when n>0 (an active reviver is
            // standing in range) OR when progress>0.95 (nearly thawed, cosmetic warning). The port previously
            // used ReviveProgress>0 which fires too early (the auto-thaw ramp makes progress>0 immediately).
            bool reviving = st.HasReviver || st.ReviveProgress >= 0.95f;
            into.Add(new Waypoints.WaypointSprite
            {
                // QC WaypointSprite_Spawn(WP_Frozen, ..., '0 0 64', NULL, targ.team, targ, ...): owner = the frozen
                // player so it follows them; offset 64 qu above the origin; radar tinted by the sprite color.
                SpriteName = reviving ? "Reviving" : "Frozen",
                Owner = p,
                Offset = new Vector3(0f, 0f, 64f),
                Team = team,
                Color = reviving ? new Vector3(1f, 0.5f, 0f) : new Vector3(0.25f, 0.9f, 1f), // WP_REVIVING/FROZEN_COLOR
                RadarIcon = 1,
                // QC UpdateMaxHealth(1) + UpdateHealth(REVIVE_PROGRESS): the thaw-progress bar (0..1, pre-normalized).
                Health = System.Math.Clamp(st.ReviveProgress, 0f, 1f),
                MaxHealth = 1f,
            });
        }
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, SetStartItems)</c> (sv_freezetag.qc:912): the FT spawn loadout —
    /// g_ft_start_health/armor (100/100) + full ammo (60/320/160/180/0). Strips the unlimited flags, then
    /// grants unlimited ammo when g_use_ammunition is off (QC <c>if(!cvar("g_use_ammunition")) start_items |=
    /// IT_UNLIMITED_AMMO</c>). SpawnSystem.ComputeStartItems fires this live (the readplayerstartcvars seam).
    /// </summary>
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        l.ItemFlags.Remove("UNLIMITED_AMMO");
        l.ItemFlags.Remove("UNLIMITED_SUPERWEAPONS");
        float useAmmunition = TryCvar("g_use_ammunition", out float use) ? use : 1f; // QC default 1 (consume ammo)
        if (useAmmunition == 0f)
            l.ItemFlags.Add("UNLIMITED_AMMO");

        l.Health      = StartHealth;
        l.Armor       = StartArmor;
        l.AmmoShells  = TryCvar("g_ft_start_ammo_shells", out float s) ? s : 60f;
        l.AmmoBullets = TryCvar("g_ft_start_ammo_nails", out float n) ? n : 320f;
        l.AmmoRockets = TryCvar("g_ft_start_ammo_rockets", out float r) ? r : 160f;
        l.AmmoCells   = TryCvar("g_ft_start_ammo_cells", out float c) ? c : 180f;
        l.AmmoFuel    = TryCvar("g_ft_start_ammo_fuel", out float f) ? f : 0f;
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, GiveFragsForKill, CBC_ORDER_FIRST)</c> (sv_freezetag.qc:590):
    /// <c>M_ARGV(2, float) = 0; return true;</c> — no normal frags are counted in Freeze Tag. The freeze/revive
    /// ±1 SCORE matrix and the round wins are the only scoring; without this an enemy freeze would also award a
    /// +1 engine kill/score on top of <see cref="Freeze"/>'s own matrix (double-count). Mirrors CA/Keepaway.
    /// </summary>
    private bool OnGiveFragsForKill(ref MutatorHooks.GiveFragsForKillArgs args)
    {
        args.FragScore = 0f;
        return true;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, SetWeaponArena)</c> (sv_freezetag.qc:978): default the weapon arena to
    /// g_freezetag_weaponarena ("most_available") when the configured arena is unset / "0", so FT players spawn
    /// with (nearly) the full arsenal on top of the start loadout (mirrors CA's OnSetWeaponArena).
    /// </summary>
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        if (args.Arena == "0" || string.IsNullOrEmpty(args.Arena))
            args.Arena = CvarString(CvarWeaponArena, "most_available");
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, PlayerRegen)</c> (sv_freezetag.qc:887): <c>return STAT(FROZEN, player);</c>
    /// — a frozen player's health/armor regen is suppressed (they stay at HP=1 while frozen).
    /// </summary>
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args)
        => args.Player is Player p && IsFrozen(p);

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, ItemTouch)</c> (sv_freezetag.qc:894): a frozen toucher does NOT collect
    /// the item — return MUT_ITEMTOUCH_RETURN (true) to abort the pickup (QC's BuffTouch frozen gate is the same
    /// rule for buffs; the port has no separate buff-touch hook so this single gate covers world-item pickups).
    /// </summary>
    private bool OnItemTouch(ref MutatorHooks.ItemTouchArgs args)
        => args.Toucher is Player p && IsFrozen(p);

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ft, FragCenterMessage)</c> (sv_freezetag.qc): rewrite the per-kill personal
    /// centerprint for a freeze. If the target was ALREADY frozen (a re-kill in void/lava of a frozen body),
    /// SUPPRESS both lines (QC <c>if(STAT(FROZEN, frag_target)) { M_ARGV(4,bool) = true; return true; }</c>).
    /// Otherwise swap the attacker's line to CHOICE_FRAG_FREEZE ("You froze X") and the target's to
    /// CHOICE_FRAGGED_FREEZE ("You were frozen by Y") and mark the message handled (QC returns true). The
    /// kill-feed INFO lines (INFO_FREEZETAG_FREEZE/SELF) are sent separately by <see cref="Freeze"/>.
    /// </summary>
    private bool OnFragCenterMessage(ref MutatorHooks.FragCenterMessageArgs args)
    {
        if (MatchEnded || args.Target is not Player targ)
            return false;
        // QC: STAT(FROZEN, frag_target) — the obituary fires before FreezeTag.OnDeath, so IsFrozen still reads
        // the pre-death state. An already-frozen body being re-killed prints nothing personal.
        if (IsFrozen(targ))
        {
            args.Suppress = true;
            return true;
        }
        args.AttackerChoice = "FRAG_FREEZE";   // CHOICE_FRAG_FREEZE: "You froze X"
        args.TargetChoice   = "FRAGGED_FREEZE"; // CHOICE_FRAGGED_FREEZE: "You were frozen by Y"
        return true;
    }

    /// <summary>
    /// QC freezetag_CheckTeams (canRoundStart): a round may begin once every active team has a live,
    /// non-frozen player (and at least one player is present).
    /// </summary>
    public bool CheckTeams()
    {
        int totalPlayers = 0;
        var alive = new Dictionary<int, int>();
        foreach (int team in Teams.Active(TeamCount))
            alive[team] = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            int t = (int)p.Team;
            if (!alive.ContainsKey(t))
                continue;
            totalPlayers++;
            if (!IsEliminated(p))
                alive[t] += 1;
        }
        if (totalPlayers == 0)
            return false;
        foreach (var kv in alive)
            if (kv.Value == 0)
                return false;
        return true;
    }

    /// <summary>
    /// QC freezetag_CheckWinner (canRoundEnd): a team is alive while it has a non-frozen, non-dead player.
    /// When exactly one team is alive (or the round time limit elapses with one team left) it wins the round
    /// (ST_FT_ROUNDS +1). Returns the winning team (&gt;0), -1 for a tie, or 0 if the round continues.
    /// </summary>
    public int CheckWinner()
    {
        if (MatchEnded)
            return -1;

        // QC freezetag_CheckWinner (sv_freezetag.qc:75-88): if the round timelimit elapses while the round is
        // still live (both teams have survivors), the round is OVER as a TIE — thaw everyone, clear their
        // freeze timers, and announce ROUND_OVER. The live server RoundHandler mirrors RoundEndTime into
        // ft.Handler, so read it here (0 = no timelimit). This is the round-timelimit-expiry tie path.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (Handler is { RoundEndTime: > 0f } h && h.RoundEndTime - now <= 0f)
        {
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "ROUND_OVER");
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "ROUND_OVER");
            for (int i = 0; i < _roster.Count; i++)
                if (IsFrozen(_roster[i]))
                    Unfreeze(_roster[i]); // QC: thaw all frozen + clear freezetag_frozen_timeout/revive_time
            Round.Number++;
            return -1; // tie (no team score)
        }

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
            if (!IsEliminated(p))
                Round.AliveByTeam[t] += 1;
        }
        if (totalPlayers == 0)
            return 0;

        int aliveTeams = 0, soleTeam = Teams.None;
        foreach (var kv in Round.AliveByTeam)
            if (kv.Value > 0) { aliveTeams++; soleTeam = kv.Key; }

        if (aliveTeams > 1)
            return 0; // round still live

        int winner = aliveTeams == 1 ? soleTeam : -1;
        if (winner > 0)
        {
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner, ST_FT_ROUNDS, +1)
            Round.Number++;
            UpdateLeaderAndCheckLimit();
        }
        else
        {
            Round.Number++; // mutual elimination → tie, no score
        }
        return winner;
    }

    /// <summary>
    /// QC <c>freezetag_LastPlayerForTeam_Notify</c> (sv_freezetag.qc) via <c>freezetag_LastPlayerForTeam</c>: when a
    /// freeze/leave/spawn leaves exactly one still-living (unfrozen, undead) teammate, center-print
    /// "You are now alone!" (CENTER_ALONE) to that last survivor. Only fires while a round is started (not warmup).
    /// <paramref name="leaving"/> is the player being frozen/removed and is excluded from the survivor scan
    /// (matching QC's <c>it != this</c>). A frozen teammate does NOT count as a survivor (QC counts only
    /// !STAT(FROZEN) &amp;&amp; !IS_DEAD), so this uses <see cref="IsEliminated"/> — the FT-specific difference from CA.
    /// </summary>
    public void NotifyLastPlayerForTeam(Player leaving, IReadOnlyList<Player> roster)
    {
        if (Handler is not { IsRoundStarted: true }) // QC: !warmup_stage && round_handler_IsRoundStarted
            return;
        Player? last = null;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (ReferenceEquals(p, leaving) || !Teams.SameTeam(leaving, p))
                continue;
            if (IsEliminated(p)) // QC: a frozen OR dead teammate is not a living survivor
                continue;
            if (last is null) last = p;
            else return; // more than one teammate still alive → not alone
        }
        if (last is not null)
            NotificationSystem.Center(last, "ALONE");
    }

    public int GetTeamRounds(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary);

    /// <summary>
    /// QC STAT(REDALIVE..PINKALIVE) source (<c>freezetag_count_alive_players</c>, sv_freezetag.qc:22-50):
    /// living (unfrozen, undead — QC <c>GetResource(RES_HEALTH) &gt;= 1 &amp;&amp; !STAT(FROZEN)</c>) players on
    /// <paramref name="teamCode"/> (a <see cref="Teams"/> color code) per the last recount. QC recounts on
    /// freeze/revive/death/spawn EVENTS; the port's per-frame <see cref="CheckWinner"/>
    /// (the round handler's per-tick CanRoundEnd) recomputes the same pure function of the Frozen/IsDead state —
    /// value-identical, so no second event-driven recount path is added. Inactive/unknown teams read 0.
    /// </summary>
    public int AliveCount(int teamCode) => Round.AliveByTeam.TryGetValue(teamCode, out int n) ? n : 0;

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: FT teams rank by the team primary slot ST_FT_ROUNDS (round wins). LeaderTeam / SecondTeam read the
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

        // QC MUTATOR_HOOKFUNCTION(ft, Scores_CountFragsRemaining) returns true: announce "N rounds left" once as
        // the leading team approaches the round limit (WinningCondition_Scores remaining-frags announcer).
        int secondScore = secondTeam == Teams.None ? 0 : GetTeamRounds(secondTeam);
        Scoring.GameScores.CountFragsRemaining(limit, leadLimit, bestScore, secondScore, suddenDeathEnding: false);
    }

    private FrozenState GetState(Player p)
    {
        if (!Frozen.TryGetValue(p, out var st))
        {
            st = new FrozenState();
            Frozen[p] = st;
        }
        return st;
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

    private static string CvarString(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}

/// <summary>
/// Per-player Freeze Tag state — the Godot-free essence of QC's STAT(FROZEN) + STAT(REVIVE_PROGRESS) and
/// the freeze timing fields (including the auto-revive timeout). The ice entity model and waypoint sprite
/// remain client concerns.
/// </summary>
public sealed class FrozenState
{
    /// <summary>QC STAT(FROZEN): the player is encased in ice and counts as eliminated.</summary>
    public bool IsFrozen;

    /// <summary>
    /// True iff at least one living unfrozen same-team reviver was within range last <see cref="FreezeTag.ReviveTick"/>
    /// call (QC <c>n &gt; 0</c>). Used by <see cref="FreezeTag.CollectWaypoints"/> to decide the
    /// WP_Frozen → WP_Reviving swap (Base swaps on <c>n&gt;0 || progress&gt;0.95</c>).
    /// </summary>
    public bool HasReviver;

    /// <summary>QC STAT(REVIVE_PROGRESS): 0..1 thaw progress from nearby teammates.</summary>
    public float ReviveProgress;

    /// <summary>Sim time the player was frozen (QC freezetag_frozen_time).</summary>
    public float FrozenTime;

    /// <summary>Sim time the freeze auto-thaws (QC freezetag_frozen_timeout); 0 = no auto-thaw.</summary>
    public float FrozenTimeout;
}
