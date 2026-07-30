using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Last Man Standing (LMS) gametype — port of <c>CLASS(LastManStanding, Gametype)</c>
/// (common/gametypes/gametype/lms/lms.qh + sv_lms.qc).
///
/// Each player starts with a fixed pool of lives (cvar <c>lives</c>, default 5; legacy default 9). A death
/// costs the *victim* one life (QC MUTATOR_HOOKFUNCTION(lms, GiveFragsForKill): the target loses a life and
/// the frag score itself is zeroed — kills don't score points in LMS, lives are the currency). When a
/// player's lives hit 0 they are out of the game (QC <c>frags = FRAGS_PLAYER_OUT_OF_GAME</c>) and assigned a
/// finishing rank. The match ends when at most one player still has lives — that last player standing wins
/// (QC <c>WinningCondition_LMS</c>).
///
/// Faithfully ported (the win/lives rule):
///  - per-player lives state (<see cref="LmsState"/>), seeded to <see cref="StartingLives"/> on join;
///  - Combat.Death → victim loses a life (QC GiveFragsForKill), eliminated at 0 lives with a finishing rank;
///  - kills award no frag score (lives are the scoring currency, QC zeroes the frag score);
///  - last-player-standing win latch (QC WinningCondition_LMS: ≤1 player with lives ⇒ winner).
///
/// Faithfully ported (cont.):
///  - join-anytime gating (<see cref="NewPlayerLives"/>, QC LMS_NewPlayerLives / lms_lowest_lives): a late
///    joiner gets the current lowest life count (so they aren't at an advantage), and once the leader has
///    been out long enough nobody else may join;
///  - the dynamic respawn delay (<see cref="RespawnDelayFor"/>, QC the g_lms_dynamic_respawn_delay scaling).
///
/// Faithfully ported (cont. 2):
///  - extra-life pickups (QC ITEM_ExtraLife / lms_replace_with_extralife): an extra-life item grants
///    <see cref="ExtraLives"/> lives on touch (<see cref="GiveExtraLife"/> / <see cref="SpawnExtraLife"/>);
///  - leader computation (QC lms_UpdateLeaders): the max-lives players become leaders when their lead is large
///    enough and they're a small enough fraction of the field (<see cref="UpdateLeaders"/>); the waypoint
///    sprite that marks them is the only client-side remainder.
///
/// Faithfully ported (cont. 3 — Wave-2 mutator hooks, subscribed in <see cref="Activate"/>):
///  - the fixed LMS start loadout (QC SetStartItems: g_lms_start_health/armor 200/200 + ammo 60/320/160/180/0,
///    unlimited-ammo flag per g_use_ammunition);
///  - the forced weapon arena (QC SetWeaponArena: g_lms_weaponarena "most_available");
///  - no weapon dropping (QC ForbidThrowCurrentWeapon);
///  - regen/rot disabled (QC PlayerRegen: g_lms_regenerate / g_lms_rot, both off by default);
///  - the dynamic-vampire damage modifier (QC Damage_Calculate: under-dogs steal a scaled fraction of damage
///    dealt to leaders, base 0.1 + 0.1/life behind, capped 0.5, clamped at start_health);
///  - the map-item filter (QC FilterItemDefinition: suppress map items unless g_lms_items / g_pickup_items&gt;0).
///
/// Deferred (NOTE — cross-boundary, tracked in the lms registry shard): the leader WAYPOINT SPRITE rendering +
/// periodic visibility window + HUD mod-icon (client/SV_StartFrame), respawn denial wired off OutOfGame
/// (CalculateRespawnTime — currently the round gate substitutes), the late-join NewPlayerLives/AddPlayer +
/// forfeit RemovePlayer rank reshuffle + map/round reset forwarding from MatchController, and the tie/time-limit
/// overtime path — all need a host-side .Call site outside this file.
/// </summary>
[GameType]
public sealed class LastManStanding : GameType
{
    // ----- lives cvars + default (gametype default lives=5; m_legacydefaults "9 20 0") -----
    private const string CvarLivesDefault = "g_lms_lives_override"; // menu override (m_configuremenu)
    private const string CvarFragLimit    = "fraglimit";            // QC LMS_NewPlayerLives derives lives from fraglimit
    private const int    DefaultLives     = 5;                      // gametype_init "lives=5" (legacy maps "9 20 0" ⇒ 9)

    // ----- extra-life + leader cvars (QC g_lms_extra_lives / g_lms_leader_*) -----
    private const string CvarExtraLives        = "g_lms_extra_lives";        // lives granted by an extralife pickup
    private const string CvarLeaderLivesDiff   = "g_lms_leader_lives_diff";  // min lead over 2nd to be a leader (2)
    private const string CvarLeaderMinPercent  = "g_lms_leader_minpercent";  // leaders must be <= this fraction (0.5)

    // ----- loadout / regen / arena / vampire cvars (QC SetStartItems / PlayerRegen / SetWeaponArena / Damage_Calculate) -----
    private const string CvarWeaponArena = "g_lms_weaponarena"; // forced weapon arena ("most_available")

    /// <summary>Per-player LMS bookkeeping (QC LMS_LIVES / LMS_RANK scoring fields + FRAGS_PLAYER_OUT_OF_GAME).</summary>
    public sealed class LmsState
    {
        /// <summary>QC LMS_LIVES: remaining lives. Player is eliminated when this reaches 0.</summary>
        public int Lives;

        /// <summary>QC LMS_RANK: finishing place (1 = winner). 0 while still in the game.</summary>
        public int Rank;

        /// <summary>QC <c>frags == FRAGS_PLAYER_OUT_OF_GAME</c>: true once eliminated (no lives left).</summary>
        public bool OutOfGame => Lives <= 0;

        /// <summary>QC <c>.lms_leader</c>: this player currently leads on lives (gets a leader waypoint sprite).</summary>
        public bool IsLeader;

        /// <summary>
        /// QC <c>INGAME_STATUS_JOINING</c>: set when a player joins mid-match (not before game_starttime).
        /// Cleared by <see cref="LastManStanding.OnPlayerSpawn"/> after the least-healthy health/armor clamp is
        /// applied (QC: <c>INGAME_STATUS_SET(player, INGAME_STATUS_JOINED)</c> at the end of PlayerSpawn).
        /// </summary>
        public bool IsJoiningMidMatch;
    }

    /// <summary>The lives/rank table, keyed by player (QC stored on the client edict's scoring fields).</summary>
    private readonly Dictionary<Player, LmsState> _states = new();

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _startItemsHandler;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _weaponArenaHandler;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _forbidThrowHandler;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _regenHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _filterItemHandler;
    private HookHandler<MutatorHooks.ItemTouchArgs>? _itemTouchHandler;
    private HookHandler<MutatorHooks.PlayerPowerupsArgs>? _powerupsHandler;
    private HookHandler<MutatorHooks.MakePlayerObserverArgs>? _makeObserverHandler;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _spawnHandler;

    /// <summary>Optional sink for the host/controller to react to a frag/elimination.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once ≤1 player still has lives.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The last player standing once the match has ended (QC first_player.winning), else null.</summary>
    public Player? Winner { get; private set; }

    /// <summary>
    /// QC <c>game_starttime</c> mirror (set from the host each frame, like Assault.GameStartTime). The win
    /// condition + life-loss are suppressed while <c>time &lt;= game_starttime</c> (the pre-match countdown).
    /// </summary>
    public float GameStartTime { get; set; }

    /// <summary>
    /// QC <c>warmup_stage</c> mirror (set from the host each frame). Lives aren't lost and the win condition
    /// returns "no winner" during warmup (QC GiveFragsForKill / WinningCondition_LMS both early-return).
    /// </summary>
    public bool InWarmup { get; set; }

    /// <summary>QC <c>warmup_stage || time &lt;= game_starttime</c>: the pre-match window where lives/win are frozen.</summary>
    private bool PreMatch => InWarmup || (Api.Services is not null && Api.Clock.Time <= GameStartTime);

    public LastManStanding()
    {
        NetName = "lms";
        DisplayName = "Last Man Standing";
        TeamGame = false;
    }

    public override void OnInit()
    {
        // QC INIT(LastManStanding): identity is set in the ctor. gametype_init flags (USEPOINTS|HIDELIMITS)
        // and legacy defaults are engine concerns; lives are seeded per join via StartingLives / NewPlayerLives.
    }

    // QC lms_lowest_lives (sv_lms.qc:32): a RUNNING global of the all-time lowest life count any player has hit
    // this match, seeded to 999 (lms_Initialize / reset_map_global) and lowered in GiveFragsForKill as players lose
    // lives. Crucially it drops to 0 once the first player is eliminated, which is what LMS_NewPlayerLives keys off
    // to lock the match to late joiners (so a single survivor wins). NOT the current living minimum — an eliminated
    // 0-lives player must keep it pinned at 0 (the current-living-min would bounce back up and never lock the match).
    private int _lowestLives = 999;

    /// <summary>QC <c>lms_lowest_lives</c>: the all-time lowest life count reached this match (999 before any loss).</summary>
    public int LowestLives() => _lowestLives;

    /// <summary>
    /// QC LMS_NewPlayerLives: the lives a player joining now should get. A late joiner receives the current
    /// lowest life count (bounded to the fraglimit), so they don't start ahead; returns 0 (can't join) once
    /// the leader has lost enough lives and join-anytime is off, or once the first player is fully out.
    /// </summary>
    public int NewPlayerLives()
    {
        int fl = 999;
        if (TryCvar(CvarFragLimit, out float f)) { int v = (int)f; if (v > 0 && v <= 999) fl = v; }

        int lowest = LowestLives();
        if (lowest < 1)
            return 0; // a player already left for dying too much → nobody else can get in

        bool joinAnytime = TryCvar("g_lms_join_anytime", out float ja) && ja != 0f;
        if (!joinAnytime)
        {
            int lastJoin = TryCvar("g_lms_last_join", out float lj) ? (int)System.Math.Max(0f, System.MathF.Floor(lj)) : 0;
            if (lowest < fl - lastJoin)
                return 0; // too late to join
        }
        return System.Math.Clamp(lowest, 1, fl);
    }

    /// <summary>
    /// QC the g_lms_dynamic_respawn_delay scaling: a player with fewer lives than the leader respawns slower
    /// (base + increase * (maxLives - playerLives), capped). Returns the delay in seconds for a player with
    /// <paramref name="playerLives"/> lives. With the dynamic delay off, returns the base delay.
    /// <paramref name="respawning"/> is the player who is respawning (excluded from the max-lives scan, QC's
    /// <c>FOREACH_CLIENT(it != player ...)</c>); when only one OTHER player is alive (a heads-up) Base flattens
    /// the delay to the base value (<c>if (pl_cnt == 1) max_lives = 0</c>).
    /// </summary>
    public float RespawnDelayFor(int playerLives, Player? respawning = null)
    {
        if (!(TryCvar("g_lms_dynamic_respawn_delay", out float dyn) && dyn != 0f))
            return TryCvar("g_lms_dynamic_respawn_delay_base", out float b0) ? b0 : 2f;

        float baseDelay = TryCvar("g_lms_dynamic_respawn_delay_base", out float b) ? b : 2f;
        float increase = TryCvar("g_lms_dynamic_respawn_delay_increase", out float i) ? i : 3f;
        float max = TryCvar("g_lms_dynamic_respawn_delay_max", out float m) ? m : 20f;

        // QC CalculateRespawnTime: max-lives over the OTHER in-game players (it != player).
        int maxLives = 0, plCnt = 0;
        foreach (KeyValuePair<Player, LmsState> kv in _states)
        {
            if (kv.Value.OutOfGame || ReferenceEquals(kv.Key, respawning)) continue;
            if (kv.Value.Lives > maxLives) maxLives = kv.Value.Lives;
            plCnt++;
        }

        // QC: "min delay with only 2 players" — if just one other player is alive, flatten to the base delay.
        if (plCnt == 1)
            maxLives = 0;

        float delay = baseDelay + increase * System.Math.Max(0, maxLives - playerLives);
        return System.Math.Min(delay, max);
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, CalculateRespawnTime): set a dead LMS player's respawn timing. An eliminated
    /// (0-lives) player is denied a respawn (RESPAWN_SILENT, held 2s to stop a sudden spectator-camera jump); a
    /// still-living player gets the dynamic delay (<see cref="RespawnDelayFor"/>) when enabled. Returns true when
    /// LMS owns the timing (Base returns true to take over), false to let the generic timing stand. Called from
    /// the host's per-tick dead-player think (the CalculateRespawnTime seam) for the active LMS match.
    /// </summary>
    public bool ApplyRespawnTiming(Player p)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC: player.respawn_flags |= RESPAWN_FORCE (LMS auto-respawns living players, no fire press needed).
        p.RespawnFlags |= RespawnFlag.Force;

        int plLives = GetState(p).Lives;
        if (plLives <= 0)
        {
            // QC: eliminated → RESPAWN_SILENT, respawn_time = time + 2 (prevents an unwanted spectator rejoin).
            p.RespawnFlags = RespawnFlag.Silent;
            p.RespawnTime = now + 2f;
            p.RespawnTimeMax = p.RespawnTime;
            return true;
        }

        if (!(TryCvar("g_lms_dynamic_respawn_delay", out float dyn) && dyn != 0f))
            return false; // QC: dynamic delay off → let the generic timing stand

        float delay = RespawnDelayFor(plLives, p);
        p.RespawnTime = now + delay;
        p.RespawnTimeMax = p.RespawnTime;
        return true;
    }

    /// <summary>
    /// The number of lives a freshly joining player starts with. QC REGISTER_MUTATOR(lms) ⇒
    /// <c>GameRules_limit_score(g_lms_lives_override > 0 ? override : -1)</c>: the lives pool is the engine
    /// <c>fraglimit</c>, which the menu override raises (>0) and which otherwise carries the per-map mapinfo
    /// <c>lives=N</c> value (gametype_init default 5; legacy maps "9 20 0" ⇒ 9). LMS_NewPlayerLives then returns
    /// <c>bound(1, lms_lowest_lives(=999 for the first joiner), floor(fraglimit))</c> ⇒ the first player gets the
    /// full fraglimit. Mirror that: override (>0) wins, else the live fraglimit (the mapinfo/legacy lives value),
    /// else the gametype default 5. The port has no mapinfo→fraglimit `lives=` plumbing, so the live fraglimit is
    /// the faithful stand-in for the mapinfo value (an explicit override still wins, as in Base).
    /// </summary>
    public int StartingLives
    {
        get
        {
            // QC: g_lms_lives_override > 0 sets fraglimit to the override; otherwise fraglimit holds the mapinfo
            // lives= value (default 5, legacy 9).
            if (TryCvar(CvarLivesDefault, out float ov) && ov > 0f)
                return (int)System.Math.Max(1f, ov);

            int lives = DefaultLives;
            if (TryCvar(CvarFragLimit, out float fl))
            {
                int v = (int)System.MathF.Floor(fl);
                if (v > 0 && v <= 999) lives = v;          // QC: fl==0 || fl>999 ⇒ keep the gametype default
            }
            return lives < 1 ? 1 : lives;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Winner = null;
        _lowestLives = 999; // QC lms_Initialize: lms_lowest_lives = 999

        // QC sv_lms.qh GameRules_scoring(0, 0, 0, { field(SP_LMS_LIVES, "lives", SFL_SORT_PRIO_SECONDARY);
        // field(SP_LMS_RANK, "rank", SFL_LOWER_IS_BETTER | SFL_RANK | SFL_SORT_PRIO_PRIMARY | SFL_ALLOW_HIDE); }):
        // rank the standings by LMS_RANK ascending (1st place lowest), then by remaining LMS_LIVES. SP_SCORE is
        // present (sprio 0) but not a sort key. The LmsState mirror stays authoritative; columns mirror it for sorting.
        GS.ScoreRulesBasics(teams: false);
        GS.DeclareColumn("LMS_LIVES", Scoring.ScoreFlags.None, "lives");
        GS.DeclareColumn("LMS_RANK",
            Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Rank | Scoring.ScoreFlags.AllowHide, "rank");
        GS.SetSortKeys(GS.Field("LMS_RANK")!, GS.Field("LMS_LIVES"));

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC mutator hooks (LMS is a CA-like fixed-loadout survival mode): the fixed start loadout, the forced
        // weapon arena, no weapon-drop, no regen/rot, dynamic vampire, and the map-item filter. These are the
        // shared Wave-1 seams already wired into the spawn/damage/item pipelines (Mayhem/CA subscribe the same way).
        _startItemsHandler = OnSetStartItems;
        MutatorHooks.SetStartItems.Add(_startItemsHandler);

        _weaponArenaHandler = OnSetWeaponArena;
        MutatorHooks.SetWeaponArena.Add(_weaponArenaHandler);

        _forbidThrowHandler = OnForbidThrowCurrentWeapon;
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_forbidThrowHandler);

        _regenHandler = OnPlayerRegen;
        MutatorHooks.PlayerRegen.Add(_regenHandler);

        _damageCalcHandler = OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);

        _filterItemHandler = OnFilterItemDefinition;
        MutatorHooks.FilterItemDefinition.Add(_filterItemHandler);

        // QC MUTATOR_HOOKFUNCTION(lms, ItemTouch): grant lives when a player picks up an item_extralife
        // (the HealthMega that OnFilterItemDefinition replaced in place when g_lms_extra_lives is on).
        _itemTouchHandler = OnItemTouch;
        MutatorHooks.ItemTouch.Add(_itemTouchHandler);

        // QC MUTATOR_HOOKFUNCTION(lms, PlayerPowerups): a leader's model glows (EF_ADDITIVE | EF_FULLBRIGHT) so
        // the field can find a hider; everyone else has those bits cleared each frame.
        _powerupsHandler = OnPlayerPowerups;
        MutatorHooks.PlayerPowerups.Add(_powerupsHandler);

        // QC MUTATOR_HOOKFUNCTION(lms, MakePlayerObserver) (sv_lms.qc:413): when a player is force-demoted to
        // observer (e.g. by the server / admin kick to spec) AFTER game start, treat them as a forced leaver:
        // assign a finishing rank and reshuffle. During warmup/pre-start, just drop their LMS state (lives cleared).
        // This is the path Base calls lms_RemovePlayer from non-disconnect forced-spectator demotion.
        _makeObserverHandler = OnMakePlayerObserver;
        MutatorHooks.MakePlayerObserver.Add(_makeObserverHandler);

        // QC MUTATOR_HOOKFUNCTION(lms, PlayerSpawn) (sv_lms.qc:298): a mid-match joiner (INGAME_JOINING) spawns
        // with health/armor clamped to the minimum among same-lives living players (the least-healthy clone), so
        // they can't out-heal the field on their first spawn. Normal re-spawns of already-JOINED players are
        // unaffected (only the IsJoiningMidMatch flag, cleared here after one spawn, triggers the clamp).
        _spawnHandler = OnPlayerSpawn;
        MutatorHooks.PlayerSpawn.Add(_spawnHandler);
    }

    /// <summary>QC <c>GameRules_scoring_add</c>: mirror the authoritative <see cref="LmsState"/> into the networked
    /// LMS_RANK + LMS_LIVES columns so the scoreboard shows + sorts by them. Called wherever lives/rank change.</summary>
    private static void SyncColumns(Player p, LmsState st)
    {
        Scoring.ScoreField? lives = Scoring.GameScores.Field("LMS_LIVES");
        Scoring.ScoreField? rank = Scoring.GameScores.Field("LMS_RANK");
        if (lives is not null) Scoring.GameScores.SetPlayer(p, lives, System.Math.Max(0, st.Lives));
        if (rank is not null) Scoring.GameScores.SetPlayer(p, rank, st.Rank);
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;

        if (_startItemsHandler is not null) { MutatorHooks.SetStartItems.Remove(_startItemsHandler); _startItemsHandler = null; }
        if (_weaponArenaHandler is not null) { MutatorHooks.SetWeaponArena.Remove(_weaponArenaHandler); _weaponArenaHandler = null; }
        if (_forbidThrowHandler is not null) { MutatorHooks.ForbidThrowCurrentWeapon.Remove(_forbidThrowHandler); _forbidThrowHandler = null; }
        if (_regenHandler is not null) { MutatorHooks.PlayerRegen.Remove(_regenHandler); _regenHandler = null; }
        if (_damageCalcHandler is not null) { MutatorHooks.DamageCalculate.Remove(_damageCalcHandler); _damageCalcHandler = null; }
        if (_filterItemHandler is not null) { MutatorHooks.FilterItemDefinition.Remove(_filterItemHandler); _filterItemHandler = null; }
        if (_itemTouchHandler is not null) { MutatorHooks.ItemTouch.Remove(_itemTouchHandler); _itemTouchHandler = null; }
        if (_powerupsHandler is not null) { MutatorHooks.PlayerPowerups.Remove(_powerupsHandler); _powerupsHandler = null; }
        if (_makeObserverHandler is not null) { MutatorHooks.MakePlayerObserver.Remove(_makeObserverHandler); _makeObserverHandler = null; }
        if (_spawnHandler is not null) { MutatorHooks.PlayerSpawn.Remove(_spawnHandler); _spawnHandler = null; }
    }

    /// <summary>Look up (or lazily create, seeded with <see cref="StartingLives"/>) a player's LMS state.</summary>
    public LmsState GetState(Player p)
    {
        if (!_states.TryGetValue(p, out LmsState? st))
        {
            st = new LmsState { Lives = StartingLives };
            _states[p] = st;
        }
        return st;
    }

    /// <summary>
    /// QC lms_AddPlayer: register a player and seed their lives on the scoreboard. Before the match starts (or in
    /// warmup) the player gets the full starting pool (lazy <see cref="GetState"/>); a LATE joiner mid-match instead
    /// gets <see cref="NewPlayerLives"/> — the current lowest life count, clamped to the fraglimit — so they don't
    /// join ahead. Returns false (Base lms_AddPlayer: can't join) when the lockout is active (NewPlayerLives==0).
    /// </summary>
    public bool AddPlayer(Player p, bool matchRunning = false)
    {
        if (matchRunning && !_states.ContainsKey(p))
        {
            int lives = NewPlayerLives();
            if (lives <= 0)
                return false; // QC: lives <= 0 → not added (the match is locked to late joiners)
            // QC lms_AddPlayer mid-match: INGAME_STATUS_SET(player, INGAME_STATUS_JOINING) — mark the player as
            // joining mid-match so the PlayerSpawn hook can apply the least-healthy health/armor clamp on first
            // spawn. IsJoiningMidMatch is cleared by OnPlayerSpawn after the clamp (INGAME_STATUS_SET → JOINED).
            _states[p] = new LmsState { Lives = lives, IsJoiningMidMatch = true };
        }
        SyncColumns(p, GetState(p)); // seed LMS_LIVES on the scoreboard
        return true;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, ForbidSpawn) / lms_AddPlayer gate: may this client become a live player right
    /// now? Always yes during <paramref name="warmupOrPreStart"/>; once the match is running a late joiner is
    /// refused when the join lockout is active (<see cref="NewPlayerLives"/> == 0 — the leader has lost too many
    /// lives / the first player is fully out). An already-tracked in-game player may always (re)spawn.
    /// </summary>
    public bool CanJoin(Player p, bool warmupOrPreStart)
    {
        if (warmupOrPreStart) return true;
        if (_states.TryGetValue(p, out LmsState? st)) return !st.OutOfGame;
        return NewPlayerLives() > 0;
    }

    // QC last_forfeiter_*: the lives/health/armor of the least-healthy player who has forfeited, used to clone a
    // late joiner's loadout (the PlayerSpawn least-healthy half — host-side, still deferred).
    private int _lastForfeiterLives;
    private float _lastForfeiterHealth;
    private float _lastForfeiterArmor;

    /// <summary>QC last_forfeiter_lives (the lives of the least-healthy forfeiter, for the late-join health clone).</summary>
    public int LastForfeiterLives => _lastForfeiterLives;
    /// <summary>QC last_forfeiter_health.</summary>
    public float LastForfeiterHealth => _lastForfeiterHealth;
    /// <summary>QC last_forfeiter_armorvalue.</summary>
    public float LastForfeiterArmor => _lastForfeiterArmor;

    /// <summary>
    /// QC <c>lms_RemovePlayer</c> (fired on disconnect / forced-observer / voluntary spectate): assign a finishing
    /// rank to a leaver who was still in the game and reshuffle the other ranks. <paramref name="voluntary"/> is
    /// Base's <c>lms_spectate</c> (a disconnect is treated as voluntary): a still-in-game leaver who is NOT
    /// voluntarily leaving is ranked at <c>pl_cnt+1</c> (they keep their place); a voluntary leaver instead
    /// decrements every out-of-game player's rank (closing the gap), records their health/armor as the
    /// last-forfeiter values, clears their lives, and broadcasts INFO_LMS_NOLIVES. No-op during warmup / before
    /// game start (Base early-returns there). Only acts on a player who has not already been ranked out.
    /// </summary>
    public void RemovePlayer(Player p, bool voluntary = true, bool warmupOrPreStart = false)
    {
        if (warmupOrPreStart)
        {
            _states.Remove(p);
            return;
        }

        if (_states.TryGetValue(p, out LmsState? st) && st.Rank == 0)
        {
            if (!voluntary)
            {
                // QC: a still-in-game player forced out keeps their place — rank = (#still playing) + 1.
                st.Rank = CountAlive() + 1;
                SyncColumns(p, st);
            }
            else
            {
                // QC: a voluntary leaver decrements every OTHER out-of-game player's rank (closes the gap)...
                foreach (KeyValuePair<Player, LmsState> kv in _states)
                {
                    if (ReferenceEquals(kv.Key, p)) continue;
                    if (kv.Value.OutOfGame && kv.Value.Rank > 0)
                    {
                        kv.Value.Rank -= 1;
                        SyncColumns(kv.Key, kv.Value);
                    }
                }
                // ...records last-forfeiter health/armor (least-healthy among same/fewer lives)...
                int plLives = st.Lives;
                float plHealth = p.IsDead ? Cvar("g_lms_start_health", 200f) : p.GetResource(ResourceType.Health);
                float plArmor = p.IsDead ? Cvar("g_lms_start_armor", 200f) : p.GetResource(ResourceType.Armor);
                if (_lastForfeiterLives == 0 || plLives < _lastForfeiterLives)
                {
                    _lastForfeiterLives = plLives;
                    _lastForfeiterHealth = plHealth;
                    _lastForfeiterArmor = plArmor;
                }
                else if (plLives == _lastForfeiterLives)
                {
                    _lastForfeiterHealth = System.Math.Min(_lastForfeiterHealth, plHealth);
                    _lastForfeiterArmor = System.Math.Min(_lastForfeiterArmor, plArmor);
                }
            }

            // QC: re-run the leader scan after the roster changes (g_lms_leader_lives_diff > 0).
            if (TryCvar(CvarLeaderLivesDiff, out float ld2) && ld2 > 0f)
                UpdateLeaders();
        }

        // QC: announce the leaver if they were ranked out (INFO_LMS_NOLIVES "<name> has no more lives left").
        if (_states.TryGetValue(p, out LmsState? after) && after.Rank > 0)
            NotificationSystem.Info("LMS_NOLIVES", p.NetName);

        _states.Remove(p);
    }

    /// <summary>The lives a player has left (0 if eliminated / unknown).</summary>
    public int LivesOf(Player p) => _states.TryGetValue(p, out LmsState? st) ? st.Lives : 0;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, reset_map_players) (sv_lms.qc:222) + reset_map_global (sv_lms.qc:217): on a
    /// map/match restart, restore every player to a fresh in-game state — an out-of-game (eliminated) player
    /// becomes a live player again, LMS_RANK + LMS_LIVES are zeroed then re-seeded to the fresh starting pool, the
    /// leader flag + waypoint are cleared, and the lowest-lives sentinel (<c>lms_lowest_lives</c>) is reset to 999.
    /// Re-PutClientInServer is done by the host's reset loop; here we re-seed the per-player LMS state.
    /// <paramref name="players"/> is the live roster.
    /// </summary>
    public void ResetMapPlayers(System.Collections.Generic.IReadOnlyList<Player> players)
    {
        // Drop the latched win/leader/forfeiter state + the running lowest-lives so a fresh match starts clean.
        MatchEnded = false;
        Winner = null;
        TimeLimitCancelled = false;
        LeaderCount = 0;
        LeadersVisible = false;
        LeadersLivesDiff = 0;
        _visibleLeadersTime = 0f;
        _visibleLeadersInit = false;
        _lastForfeiterLives = 0;
        _lastForfeiterHealth = 0f;
        _lastForfeiterArmor = 0f;
        _lowestLives = 999;                                 // QC reset_map_global: lms_lowest_lives = 999

        // QC reset_map_players: per client, frags OUT_OF_GAME → PLAYER (re-livened), LMS_RANK/LMS_LIVES zeroed then
        // re-seeded (PutClientInServer ⇒ lms_AddPlayer re-grants the fresh pool), lms_leader + waypoint cleared.
        _states.Clear();
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            LmsState st = new() { Lives = StartingLives, Rank = 0, IsLeader = false };
            _states[p] = st;
            SyncColumns(p, st);
        }
    }

    /// <summary>
    /// The obituary handler — QC MUTATOR_HOOKFUNCTION(lms, GiveFragsForKill): the victim loses one life and
    /// the kill scores no frag points. On reaching 0 lives the victim is eliminated (out of game) and given
    /// a finishing rank below the players still alive. Then re-check the last-player-standing win condition.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        // QC GiveFragsForKill (sv_lms.qc:605): the life is only removed `if (!warmup_stage && time > game_starttime)`
        // — a death during warmup or the pre-match countdown does NOT cost a life (the frag score is still zeroed).
        LmsState st = GetState(victim);
        if (!PreMatch && !st.OutOfGame)
        {
            st.Lives -= 1;                                  // QC: int tl = GameRules_scoring_add(target, LMS_LIVES, -1)
            if (st.Lives < _lowestLives)
                _lowestLives = st.Lives;                    // QC: if (tl < lms_lowest_lives) lms_lowest_lives = tl
            if (st.OutOfGame)
                st.Rank = CountAlive() + 1;                 // QC: rank = (#players still in) + 1
            SyncColumns(victim, st);                        // mirror LMS_LIVES / LMS_RANK to the scoreboard
        }

        // QC zeroes the frag score (M_ARGV(2,float)=0): kills don't add points in LMS — lives are the score.

        Player? attacker = ev.Attacker as Player;
        Events?.OnFrag(attacker, victim, ev.DeathType);

        CheckWinningCondition();
        return false;
    }

    /// <summary>Number of tracked players that still have lives (QC IS_PLAYER &amp;&amp; frags == FRAGS_PLAYER).</summary>
    public int CountAlive()
    {
        int n = 0;
        foreach (LmsState st in _states.Values)
            if (!st.OutOfGame) n++;
        return n;
    }

    /// <summary>
    /// QC <c>WinningConditionHelper_topscore == WinningConditionHelper_secondscore</c> ⇒ <c>WINNING_NEVER</c>: with
    /// ≥2 living players whose top two LIVES counts are equal, LMS cancels the time limit (the match must be decided
    /// by an elimination, not the clock). Recomputed each <see cref="CheckWinningCondition"/>; consumed by the host
    /// via <see cref="ReportsTie"/> so <c>OverTimeManager.GetWinningCode</c> resolves the tie to
    /// <c>WINNING_NEVER</c> / overtime rather than a timelimit draw.
    /// </summary>
    public bool TimeLimitCancelled { get; private set; }

    /// <summary>
    /// QC <c>WinningCondition_LMS</c> ⇒ <c>WINNING_NEVER</c> when the top two living players have equal lives:
    /// surfaces <see cref="TimeLimitCancelled"/> (recomputed each frame by <see cref="CheckWinningCondition"/>) to the
    /// host's <c>CheckRules_World</c> overtime cascade via the <c>GameType.ReportsTie</c> seam, so a timelimit expiry
    /// while the top two are tied enters overtime rather than ending the match. Returns <c>false</c> (no tie) while
    /// the match is over or during the pre-match window (QC early-returns WINNING_NO there, never WINNING_NEVER).
    /// </summary>
    public override bool ReportsTie(System.Collections.Generic.IReadOnlyList<Player> roster)
        => !MatchEnded && !PreMatch && TimeLimitCancelled;

    /// <summary>
    /// QC <c>campaign_bots_may_start</c> (sv_lms.qc:85): true once the human has spawned in a campaign. The server
    /// wires this to <c>Campaign.BotsMayStart</c>; unset (non-campaign / headless test) → false (the campaign
    /// human-lost early-end never fires).
    /// </summary>
    public static System.Func<bool>? CampaignBotsMayStart;

    /// <summary>
    /// QC <c>WinningCondition_LMS</c> (sv_lms.qc:60): drive the LMS end-of-match check. Returns no-winner while the
    /// match hasn't begun (warmup / pre-start countdown). With ≥2 living players the match continues — except the
    /// time limit is cancelled (<see cref="TimeLimitCancelled"/>) when the top two lives counts are equal (QC
    /// WinningConditionHelper ⇒ WINNING_NEVER). With exactly one living player: that survivor WINS once no further
    /// joiner is possible (QC LMS_NewPlayerLives()==0), OR forfeit-wins early if joins are still allowed but enough
    /// match time has elapsed AND someone has already been eliminated (QC <c>totalplayed &amp;&amp; time &gt;
    /// game_starttime + g_lms_forfeit_min_match_time</c>); otherwise the match waits for more joiners. With zero
    /// living players it's a draw/forfeit end.
    /// </summary>
    public void CheckWinningCondition()
    {
        if (_states.Count == 0)
            return; // nobody playing yet

        TimeLimitCancelled = false;

        // QC: WINNING_NO while warmup_stage || time <= game_starttime — the match hasn't started, no win/forfeit.
        if (PreMatch)
            return;

        int alive = 0, totalPlayed = 0;
        Player? lastAlive = null;
        foreach (KeyValuePair<Player, LmsState> kv in _states)
        {
            if (!kv.Value.OutOfGame)
            {
                alive++;
                lastAlive = kv.Key;
            }
            else if (kv.Value.Rank > 0)
                totalPlayed++;                              // QC: a player who has a finishing rank ("played")
        }

        if (alive > 1)
        {
            // QC sv_lms.qc:84-92 — campaign human-lost early end: with the campaign live AND the human spawned
            // (campaign_bots_may_start), if the (first) real client has 0 lives the level is over (WINNING_YES,
            // "human player lost, game over"). Bots fighting on don't keep a lost campaign running.
            if (Cvar("g_campaign", 0f) != 0f && (CampaignBotsMayStart?.Invoke() ?? false))
            {
                foreach (KeyValuePair<Player, LmsState> kv in _states)
                {
                    if (kv.Key.IsBot) continue;        // QC FOREACH_CLIENT IS_REAL_CLIENT (first real client; break)
                    if (kv.Value.Lives <= 0)
                    {
                        MatchEnded = true;             // QC return WINNING_YES — the campaign level ends (human lost)
                        return;
                    }
                    break;                              // QC: break after the first real client
                }
            }

            // QC: two or more living players — game continues. Run WinningConditionHelper: if the top two LIVES
            // counts are equal, cancel the time limit (WINNING_NEVER); otherwise let the timelimit decide.
            TimeLimitCancelled = TopTwoLivesEqual();
            return;
        }

        if (alive == 1 && lastAlive is not null)
        {
            // QC: exactly one living player. Are further joiners still possible?
            if (NewPlayerLives() > 0)
            {
                // Joins still allowed: forfeit-win only after enough match time AND someone already eliminated,
                // else keep waiting (no winner yet — a late joiner could still turn up).
                float now = Api.Services is not null ? Api.Clock.Time : 0f;
                float forfeitMin = Cvar("g_lms_forfeit_min_match_time", 0f);
                if (totalPlayed > 0 && GameStartTime > 0f && now > GameStartTime + forfeitMin)
                {
                    WinSurvivor(lastAlive); // QC: forfeit-win for the lone survivor
                }
                // else: game still running (nobody removed by a frag yet) → wait for players.
                return;
            }

            // QC: no more joiners possible → the lone survivor wins.
            WinSurvivor(lastAlive);
            return;
        }

        // QC: zero living players (mutual elimination) — forfeit/draw end.
        MatchEnded = true;
    }

    /// <summary>QC: latch the win for the single survivor (LMS_RANK=1, winning=1, end the match).</summary>
    private void WinSurvivor(Player survivor)
    {
        MatchEnded = true;
        Winner = survivor;
        LmsState ws = GetState(survivor);
        ws.Rank = 1; // QC: first_player.winning = 1; LMS_RANK = 1
        SyncColumns(survivor, ws);
    }

    /// <summary>
    /// QC WinningConditionHelper (the LMS sort is by LIVES): true when the two highest living lives counts are
    /// equal — the tie that cancels the time limit (WINNING_NEVER).
    /// </summary>
    private bool TopTwoLivesEqual()
    {
        int top = int.MinValue, second = int.MinValue;
        foreach (KeyValuePair<Player, LmsState> kv in _states)
        {
            if (kv.Value.OutOfGame) continue;
            int l = kv.Value.Lives;
            if (l > top) { second = top; top = l; }
            else if (l > second) second = l;
        }
        return second != int.MinValue && top == second;
    }

    // ============================================================================================
    //  Extra-life pickups (QC ITEM_ExtraLife / lms_replace_with_extralife / lms_extralife touch)
    // ============================================================================================

    /// <summary>The number of lives an extra-life pickup grants (QC g_lms_extra_lives, default 0 = off).</summary>
    public int ExtraLives => TryCvar(CvarExtraLives, out float v) && v > 0f ? (int)v : 0;

    /// <summary>
    /// QC the lms_extralife touch: grant <see cref="ExtraLives"/> lives to a still-in-game player who picks up
    /// an extra-life item. No effect once the player is out (you can't pick up extra lives after elimination)
    /// or when extra lives are disabled. Returns the lives added.
    /// </summary>
    public int GiveExtraLife(Player p)
    {
        int extra = ExtraLives;
        if (extra <= 0) return 0;
        LmsState st = GetState(p);
        if (st.OutOfGame) return 0; // QC: eliminated players can't gain lives
        st.Lives += extra;          // QC GameRules_scoring_add(toucher, LMS_LIVES, g_lms_extra_lives)
        SyncColumns(p, st);
        return extra;
    }

    /// <summary>
    /// QC lms_replace_with_extralife / item_extralife spawnfunc: spawn an extra-life pickup at
    /// <paramref name="origin"/> whose touch grants lives to a still-in-game player. Returns the item entity
    /// (null when no facade is wired or extra lives are off).
    /// </summary>
    public Entity? SpawnExtraLife(System.Numerics.Vector3 origin)
    {
        if (ExtraLives <= 0)
            return null;
        Entity? e = GametypeEntities.SpawnObjective("item_extralife", origin, Teams.None,
            new System.Numerics.Vector3(-16, -16, -16), new System.Numerics.Vector3(16, 16, 48),
            touch: ExtraLifeTouch);
        if (e is not null)
            e.Flags = EntFlags.Item;
        return e;
    }

    private void ExtraLifeTouch(Entity item, Entity other)
    {
        if (other is not Player p || p.IsDead) return;
        if (GiveExtraLife(p) > 0)
        {
            NotificationSystem.Center(p, "EXTRALIVES", ExtraLives); // QC CENTER_EXTRALIVES
            Api.Entities?.Remove(item);                              // consumed
        }
    }

    // ============================================================================================
    //  Leader computation (QC lms_UpdateLeaders) — mark the max-lives players as leaders
    // ============================================================================================

    /// <summary>The lives lead the current leader(s) hold over the next-best player (QC lms_leaders_lives_diff).</summary>
    public int LeadersLivesDiff { get; private set; }

    /// <summary>QC <c>lms_leaders</c>: number of in-game players currently flagged as leaders (HUD mod-icon stat).</summary>
    public int LeaderCount { get; private set; }

    /// <summary>QC <c>lms_visible_leaders</c>: are leaders currently inside their show-window (radar-visible)?</summary>
    public bool LeadersVisible { get; private set; }

    // QC SV_StartFrame visibility-window state machine (lms_visible_leaders / _time / _prev).
    private float _visibleLeadersTime;
    private bool _visibleLeadersInit; // QC seeds lms_visible_leaders=true so the first frame schedules the timer

    /// <summary>The players currently marked as leaders (QC .lms_leader). Recomputed by <see cref="UpdateLeaders"/>.</summary>
    public IEnumerable<Player> Leaders
    {
        get { foreach (var kv in _states) if (kv.Value.IsLeader) yield return kv.Key; }
    }

    /// <summary>
    /// QC <c>lms_UpdateLeaders</c>: the in-game players with the most lives become leaders — but only when their
    /// lead over the second-best is at least <c>g_lms_leader_lives_diff</c> AND they are at most
    /// <c>g_lms_leader_minpercent</c> of the in-game field (so a tight pack has no "leader"). Sets each player's
    /// <see cref="LmsState.IsLeader"/>; the host renders the leader waypoint sprite. Call per frame.
    /// </summary>
    public void UpdateLeaders()
    {
        int maxLives = 0, plCnt = 0;
        foreach (var kv in _states)
            if (!kv.Value.OutOfGame) { plCnt++; if (kv.Value.Lives > maxLives) maxLives = kv.Value.Lives; }

        int secondMax = 0, cntWithMax = 0;
        foreach (var kv in _states)
        {
            if (kv.Value.OutOfGame) continue;
            if (kv.Value.Lives == maxLives) cntWithMax++;
            else if (kv.Value.Lives > secondMax) secondMax = kv.Value.Lives;
        }

        LeadersLivesDiff = maxLives - secondMax;

        int livesDiff = TryCvar(CvarLeaderLivesDiff, out float ld) ? (int)ld : 2;
        float minPercent = TryCvar(CvarLeaderMinPercent, out float mp) ? mp : 0.5f;

        bool haveLeaders = LeadersLivesDiff >= livesDiff && cntWithMax <= plCnt * minPercent;
        foreach (var kv in _states)
        {
            if (kv.Value.OutOfGame) { kv.Value.IsLeader = false; continue; }
            kv.Value.IsLeader = haveLeaders && kv.Value.Lives == maxLives;
        }
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, SV_StartFrame): the leader radar-visibility window state machine. Leaders
    /// are shown on radar for <c>g_lms_leader_wp_time</c> (5s), then hidden for the wp_interval (25s) + a random
    /// jitter (up to 10s) before the next show window. On the rising edge of a show window each in-game player is
    /// centerprinted CENTER_LMS_VISIBLE_LEADER (if they ARE a leader: "enemies can see you") / CENTER_LMS_VISIBLE_OTHER
    /// (otherwise: "leaders can now be seen"). Drives <see cref="LeaderCount"/> / <see cref="LeadersVisible"/>
    /// (the recycled REDALIVE/OBJECTIVE_STATUS stats). Call once per frame after <see cref="UpdateLeaders"/>.
    /// </summary>
    public void DriveLeaderVisibility()
    {
        // QC: count the in-game leaders this frame.
        int leaders = 0;
        foreach (var kv in _states)
            if (!kv.Value.OutOfGame && kv.Value.IsLeader) leaders++;
        LeaderCount = leaders;

        float leaderTime = TryCvar("g_lms_leader_wp_time", out float lt) ? lt : 5f;
        float wpInterval = TryCvar("g_lms_leader_wp_interval", out float wi) ? wi : 25f;
        float jitter = TryCvar("g_lms_leader_wp_interval_jitter", out float jt) ? jt : 10f;
        float leaderInterval = leaderTime + wpInterval;

        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC seeds lms_visible_leaders=true so the very first frame (visible_prev=true, visible=false) schedules
        // the first window; mirror that with a one-shot init.
        bool prev = _visibleLeadersInit ? LeadersVisible : true;
        _visibleLeadersInit = true;

        LeadersVisible = leaders != 0 && now > _visibleLeadersTime && now < _visibleLeadersTime + leaderTime;
        if (leaders == 0 || (prev && !LeadersVisible))
            _visibleLeadersTime = now + leaderInterval + XonoticGodot.Common.Math.Prandom.Float() * jitter;

        // QC: on the !prev && visible rising edge, centerprint every in-game player (leader vs other variant).
        if (!prev && LeadersVisible)
        {
            foreach (var kv in _states)
            {
                if (kv.Value.OutOfGame) continue;
                NotificationSystem.Center(kv.Key,
                    kv.Value.IsLeader ? "LMS_VISIBLE_LEADER" : "LMS_VISIBLE_OTHER");
            }
        }
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, SV_StartFrame) (sv_lms.qc:480-487): attach a WP_LmsLeader radar waypoint to
    /// each in-game leader (RADARICON_FLAGCARRIER, the player's colormap radar tint, '0 0 64' head offset). The
    /// sprite's visibility follows QC <c>lms_waypointsprite_visible_for_player</c>: a spectator watching a leader
    /// doesn't see the attached marker (it would clutter the top of their screen), and EVERY viewer only sees it
    /// while the leaders are inside their periodic show-window (<see cref="LeadersVisible"/>, driven by
    /// <see cref="DriveLeaderVisibility"/>). Rebuilt each tick from the live leader set (the port's CollectWaypoints
    /// pull, like CTF's flag sprites); the radar-visibility window is the timer, this is the sprite Base draws.
    /// </summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        // QC g_lms_leader_lives_diff <= 0 disables the whole leader system (UpdateLeaders never flags a leader).
        foreach (KeyValuePair<Player, LmsState> kv in _states)
        {
            if (kv.Value.OutOfGame || !kv.Value.IsLeader)
                continue;
            into.Add(new Waypoints.WaypointSprite
            {
                SpriteName = "LmsLeader",                                    // QC WP_LmsLeader
                Owner = kv.Key,                                             // QC WaypointSprite_AttachCarrier(it)
                Offset = new System.Numerics.Vector3(0f, 0f, 64f),          // QC the standard carrier head offset
                Team = Teams.None,                                          // FFA: no team gate
                RadarIcon = 1,                                              // QC RADARICON_FLAGCARRIER
                Health = -1f,
                // QC lms_waypointsprite_visible_for_player: a spectator of a leader doesn't see the attached
                // sprite; nobody sees it outside the current show-window (lms_visible_leaders).
                VisibleForPlayer = viewer => LeadersVisible && !(viewer is not null && viewer.IsObserver),
            });
        }
    }

    // ============================================================================================
    //  Mutator-style hooks (QC SetStartItems / SetWeaponArena / ForbidThrowCurrentWeapon /
    //  PlayerRegen / Damage_Calculate / FilterItem) — LMS is a CA-like fixed-loadout survival mode
    // ============================================================================================

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, SetStartItems): give the fixed LMS spawn loadout
    /// (g_lms_start_health/armor 200/200 + ammo 60/320/160/180/0, balance-xonotic.cfg). Strip the unlimited
    /// flags, then grant unlimited ammo when g_use_ammunition is off (QC: <c>if(!cvar("g_use_ammunition"))
    /// start_items |= IT_UNLIMITED_AMMO</c>). Tag convention matches the other SetStartItems handlers.
    /// </summary>
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        l.ItemFlags.Remove("UNLIMITED_AMMO");
        l.ItemFlags.Remove("UNLIMITED_SUPERWEAPONS");
        if (Cvar("g_use_ammunition", 1f) == 0f)
            l.ItemFlags.Add("UNLIMITED_AMMO");

        l.Health      = Cvar("g_lms_start_health", 200f);
        l.Armor       = Cvar("g_lms_start_armor", 200f);
        l.AmmoShells  = Cvar("g_lms_start_ammo_shells", 60f);
        l.AmmoBullets = Cvar("g_lms_start_ammo_nails", 320f);
        l.AmmoRockets = Cvar("g_lms_start_ammo_rockets", 160f);
        l.AmmoCells   = Cvar("g_lms_start_ammo_cells", 180f);
        l.AmmoFuel    = Cvar("g_lms_start_ammo_fuel", 0f);
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, SetWeaponArena): default the weapon arena to g_lms_weaponarena
    /// ("most_available") when the configured arena is unset / "0".
    /// </summary>
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        if (args.Arena == "0" || string.IsNullOrEmpty(args.Arena))
            args.Arena = CvarString(CvarWeaponArena, "most_available");
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(lms, ForbidThrowCurrentWeapon): always forbid dropping the current weapon.</summary>
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, PlayerPowerups) (sv_lms.qc:515): a leader's model glows
    /// (<c>EF_ADDITIVE | EF_FULLBRIGHT</c>) so the field can spot a hiding leader; a non-leader has those bits
    /// cleared. Base keys this off the leader's attached waypoint sprite (<c>waypointsprite_attachedforcarrier</c>),
    /// which is attached to exactly the <see cref="LmsState.IsLeader"/> players by <see cref="UpdateLeaders"/>, so
    /// the port keys the glow directly off IsLeader. Runs each frame (the leader set is recomputed per frame).
    /// </summary>
    private bool OnPlayerPowerups(ref MutatorHooks.PlayerPowerupsArgs args)
    {
        if (args.Player is not Player player)
            return false;
        bool isLeader = _states.TryGetValue(player, out LmsState? st) && !st.OutOfGame && st.IsLeader;
        if (isLeader)
            player.Effects |= EffectFlags.Additive | EffectFlags.FullBright;   // QC: player.effects |= (EF_ADDITIVE | EF_FULLBRIGHT)
        else
            player.Effects &= ~(EffectFlags.Additive | EffectFlags.FullBright); // QC: player.effects &= ~(EF_ADDITIVE | EF_FULLBRIGHT)
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(lms, MakePlayerObserver)</c> (sv_lms.qc:413): a player demoted to observer
    /// mid-match undergoes the voluntary-spectate cleanup: other out-of-game players' ranks are decremented, the
    /// leaver's lives are zeroed, <c>last_forfeiter</c> health/armor is recorded, and <c>INFO_LMS_NOLIVES</c> is
    /// broadcast. In Base, the hook always sets <c>player.lms_spectate=true</c> (for both <c>is_forced=true</c>
    /// and <c>killindicator_teamchange==-2</c>), routing <c>lms_RemovePlayer</c> into the voluntary branch — so the
    /// port calls <see cref="RemovePlayer"/> with <c>voluntary:true</c>. A player already ranked out (Rank&gt;0)
    /// is a no-op. During warmup / pre-start the state is simply removed with no rank assignment.
    /// Returns <c>false</c>.
    /// </summary>
    private bool OnMakePlayerObserver(ref MutatorHooks.MakePlayerObserverArgs args)
    {
        // QC: `if (!IS_PLAYER(player)) return true;` — only handle player entities.
        if (args.Player is not Player p)
            return false;

        // QC lms_RemovePlayer (sv_lms.qc:341): Base sets player.lms_spectate=true for ALL MakePlayerObserver
        // paths (both is_forced=true and killindicator_teamchange==-2), which routes lms_RemovePlayer into the
        // voluntary-spectate cleanup branch: decrement other out-of-game ranks, record last_forfeiter health/armor,
        // clear the leaver's lives, and broadcast INFO_LMS_NOLIVES. Map to voluntary:true here (same branch).
        // During warmup / pre-start, RemovePlayer early-returns after dropping the state (no rank assigned).
        RemovePlayer(p, voluntary: true, warmupOrPreStart: PreMatch);
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, PlayerSpawn) (sv_lms.qc:298): on a mid-match joiner's FIRST spawn, clamp
    /// their health and armor to the minimum among living players with the same life count (the "least-healthy
    /// clone"). This ensures a late joiner can't out-heal the field on arrival. The clamp uses the recorded
    /// <see cref="LastForfeiterHealth"/>/<see cref="LastForfeiterArmor"/> as a floor (from a player who forfeited
    /// with the same lives) AND scans all same-lives living players for the current minimum — mirroring Base's:
    /// <code>
    ///   if (last_forfeiter_lives == pl_lives) min = last_forfeiter_health/armor;
    ///   FOREACH_CLIENT(it != player &amp;&amp; IS_PLAYER(it) &amp;&amp; !IS_DEAD(it) &amp;&amp; LMS_LIVES == pl_lives)
    ///     min = min(min, it.health/armor);
    ///   if (min != start_health) SetResource(health, max(1, min));
    ///   if (min_armor != start_armor) SetResource(armor, min_armor);
    /// </code>
    /// Only fires for a player flagged <see cref="LmsState.IsJoiningMidMatch"/>; that flag is cleared here
    /// (QC: <c>INGAME_STATUS_SET(player, INGAME_STATUS_JOINED)</c>). During warmup/pre-start the clamp is
    /// skipped (Base: <c>if (warmup_stage || time &lt; game_starttime) return true</c>).
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        if (args.Player is not Player player)
            return false;

        // QC: if (warmup_stage || time < game_starttime) return true — no clamp during warmup/pre-start.
        if (PreMatch)
            return false;

        if (!_states.TryGetValue(player, out LmsState? st) || !st.IsJoiningMidMatch)
            return false;

        // QC: INGAME_STATUS_SET(player, INGAME_STATUS_JOINED) — clear the joining flag now (one-shot).
        st.IsJoiningMidMatch = false;

        int plLives = st.Lives;
        float startHealth = Cvar("g_lms_start_health", 200f);
        float startArmor  = Cvar("g_lms_start_armor",  200f);

        // Seed minimums from last_forfeiter_* if the forfeiter had the same lives count.
        float minHealth = startHealth;
        float minArmor  = startArmor;
        if (_lastForfeiterLives != 0 && _lastForfeiterLives == plLives)
        {
            minHealth = _lastForfeiterHealth;
            minArmor  = _lastForfeiterArmor;
        }

        // QC: FOREACH_CLIENT(it != player && IS_PLAYER(it) && !IS_DEAD(it) && LMS_LIVES == pl_lives): scan all
        // living same-lives players for the current minimum health/armor.
        foreach (KeyValuePair<Player, LmsState> kv in _states)
        {
            if (ReferenceEquals(kv.Key, player)) continue;
            if (kv.Value.OutOfGame) continue;
            if (kv.Value.Lives != plLives) continue;
            if (kv.Key.IsDead) continue;
            float h = kv.Key.GetResource(ResourceType.Health);
            float a = kv.Key.GetResource(ResourceType.Armor);
            if (h < minHealth) minHealth = h;
            if (a < minArmor)  minArmor  = a;
        }

        // QC: only clamp if below the start values (a fresh field means no clamp needed).
        if (minHealth != startHealth)
            player.SetResource(ResourceType.Health, System.Math.Max(1f, minHealth)); // QC: max(1, min_health)
        if (minArmor != startArmor)
            player.SetResource(ResourceType.Armor, minArmor);

        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, PlayerRegen): disable regen and rot unless g_lms_regenerate / g_lms_rot
    /// are set (both default 0). Returns true (regen fully off) when neither is enabled — matching QC's
    /// <c>return (!regenerate &amp;&amp; !rot)</c>, the common case (both off ⇒ no regen, no rot).
    /// </summary>
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args)
    {
        bool regenerate = Cvar("g_lms_regenerate", 0f) != 0f;
        bool rot = Cvar("g_lms_rot", 0f) != 0f;
        return !regenerate && !rot;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, Damage_Calculate): the dynamic vampire — an under-dog (more lives behind)
    /// who hits a leader steals a fraction of the dealt damage as health. factor = base(0.1) + 0.1*lives_behind
    /// (over the 2-life threshold), capped at 0.5; health is clamped at start_health. ENABLED by default
    /// (g_lms_dynamic_vampire 1). Reads the live LmsState lives, not the (deferred) frags field.
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (Cvar("g_lms_dynamic_vampire", 1f) == 0f)
            return false;

        if (args.Attacker is not Player attacker || args.Target is not Player target)
            return false;
        if (ReferenceEquals(attacker, target))
            return false;
        if (attacker.IsDead || target.IsDead)
            return false;

        float factorBase = Cvar("g_lms_dynamic_vampire_factor_base", 0.1f);
        float factorIncrease = Cvar("g_lms_dynamic_vampire_factor_increase", 0.1f);
        float factorMax = Cvar("g_lms_dynamic_vampire_factor_max", 0.5f);
        int minDiff = TryCvar("g_lms_dynamic_vampire_min_lives_diff", out float md) ? (int)md : 2;

        // Read lives through GetState so a not-yet-tracked combatant is lazily seeded with StartingLives
        // (mirrors QC, where every spawned player already has LMS_LIVES set), not treated as 0.
        int diff = GetState(target).Lives - GetState(attacker).Lives - minDiff;
        if (diff < 0)
            return false;

        float vampireFactor = factorBase + diff * factorIncrease;
        if (vampireFactor <= 0f)
            return false;
        vampireFactor = System.Math.Min(vampireFactor, factorMax);

        // QC clamps the steal at start_health (the LMS start health, 200 by default).
        float startHealth = Cvar("g_lms_start_health", 200f);
        float healed = attacker.GetResource(ResourceType.Health) + args.Damage * vampireFactor;
        attacker.SetResource(ResourceType.Health, System.Math.Min(healed, startHealth));
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, FilterItemDefinition) + MUTATOR_HOOKFUNCTION(lms, FilterItem): suppress map
    /// item spawns. Items are kept only when g_lms_items or g_pickup_items&gt;0; otherwise everything is filtered,
    /// EXCEPT ITEM_ExtraLife/ITEM_HealthMega when g_powerups + g_lms_extra_lives are on. For a HealthMega in that
    /// case we fold in Base's separate <c>FilterItem</c> + <c>lms_replace_with_extralife</c>: the item is REPLACED
    /// IN PLACE with an item_extralife (ClassName retagged + its mega-health resource stripped) so picking it up
    /// grants lives (via <see cref="OnItemTouch"/>) instead of health. Returns true to FILTER OUT. Definitions are
    /// matched by ClassName (the item registry isn't fully ported — same stand-in Mayhem/NIX use).
    /// </summary>
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        if (Cvar("g_lms_items", 0f) != 0f || Cvar("g_pickup_items", -1f) > 0f)
            return false; // items allowed → don't filter

        string id = args.Definition.ClassName;
        bool isMega = id is "item_health_mega" or "item_healthmega";
        bool isExtraLifeOrMega = id is "item_extralife" || isMega;
        if (Cvar("g_powerups", -1f) != 0f && Cvar(CvarExtraLives, 0f) != 0f && isExtraLifeOrMega)
        {
            // QC lms_replace_with_extralife: a HealthMega becomes an item_extralife. The port retags the EXISTING
            // edict in place (rather than spawning a fresh ITEM_ExtraLife): set its classname so OnItemTouch routes
            // it to GiveExtraLife, and zero its health resource so the normal give grants no mega-health. The item
            // keeps its origin/respawn and re-touches as an extralife each cycle.
            if (isMega)
            {
                args.Definition.ClassName = "item_extralife";
                args.Definition.SetResourceExplicit(ResourceType.Health, 0f);
            }
            return false; // kept (HealthMega now acts as an extralife; a real item_extralife kept as-is)
        }

        return true; // filter out all other map items
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, ItemTouch): when a still-in-game player picks up an item_extralife (the
    /// replaced HealthMega), grant <see cref="ExtraLives"/> lives, centerprint CENTER_EXTRALIVES, and consume the
    /// item. Matches the item by ClassName (Base keys off <c>item.itemdef == ITEM_ExtraLife</c>).
    /// </summary>
    private bool OnItemTouch(ref MutatorHooks.ItemTouchArgs args)
    {
        Entity item = args.Item;
        if (args.Toucher is not Player p || p.IsDead)
            return false;
        if (item.ClassName != "item_extralife" && item.NetName != "extralife")
            return false;

        if (GiveExtraLife(p) > 0)
        {
            NotificationSystem.Center(p, "EXTRALIVES", ExtraLives); // QC CENTER_EXTRALIVES
            Api.Entities?.Remove(item);                              // QC Inventory_pickupitem + MUT_ITEMTOUCH_PICKUP
        }
        return false;
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

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;

    private static string CvarString(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}
