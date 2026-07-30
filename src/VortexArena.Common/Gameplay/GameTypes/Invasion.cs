using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Invasion gametype — port of <c>CLASS(Invasion, Gametype)</c>
/// (common/gametypes/gametype/invasion/invasion.qh + sv_invasion.qc).
///
/// A co-op survival mode: players fight successive waves ("rounds") of monsters. Each round spawns an
/// increasing number of monsters (QC <c>inv_maxspawned</c> scales with the round count and player count);
/// the round advances once every monster in it has been killed (QC <c>inv_numkilled &gt;= inv_maxspawned</c>).
/// Across rounds, killing monsters banks points and the match ends when the point limit (cvar
/// <c>g_invasion_point_limit</c> / pointlimit, default 50) is reached, or after the configured number of
/// rounds. (QC also supports STAGE/HUNT variants via <c>g_invasion_type</c>; the default co-op survival type
/// is modeled here.)
///
/// Faithfully ported (the wave/scoring rule):
///  - wave bookkeeping (<see cref="WaveState"/>: round counter, spawned/killed counts);
///  - Combat.Death → a killed monster advances the wave and banks a point toward the limit;
///  - point-limit / round-limit win check (<see cref="PointLimit"/>, QC pointlimit; <see cref="MaxRounds"/>).
///
/// Faithfully ported (objective layer):
///  - monster spawning from <c>invasion_spawnpoint</c> entities via the Monsters catalog + the shared
///    <see cref="MonsterAI"/> setup (<see cref="SpawnWaveMonster"/>/<see cref="SpawnMonsterDef"/>, QC
///    invasion_SpawnMonsters / invasion_PickMonster / invasion_SpawnChosenMonster);
///  - the wave fill loop (<see cref="Tick"/>, QC Invasion_CheckWinner: keep spawning until the wave is full,
///    advance the round when it's cleared);
///  - wave bookkeeping + point/round-limit win (<see cref="WaveState"/>, QC inv_* counters);
///  - the three g_invasion_type variants (QC INV_TYPE_*): ROUND (endless point-banked waves), HUNT (clear all
///    placed monsters), STAGE (reach the level end — an <c>invasion_roundend</c> trigger fires);
///  - per-wave monster lists (QC invasion_wave entities' <c>spawnmob</c>: <see cref="AddWave"/> keys a monster
///    list to a round; <see cref="PickMonster"/> draws from it when present, else picks at random);
///  - monster-skill scaling (QC inv_monsterskill = round + player factor) stamped on each spawned monster.
/// </summary>
[GameType]
public sealed class Invasion : GameType
{
    /// <summary>QC g_invasion_type (sv_invasion.qh): which invasion variant is active.</summary>
    public enum InvasionType
    {
        /// <summary>INV_TYPE_ROUND (0): endless point-banked rounds of scaling waves (the default).</summary>
        Round = 0,
        /// <summary>INV_TYPE_HUNT (1): clear the map of the placed/spawned enemies; win when none remain.</summary>
        Hunt = 1,
        /// <summary>INV_TYPE_STAGE (2): reach the end of the level — an <c>invasion_roundend</c> trigger ends it.</summary>
        Stage = 2,
    }

    private const string CvarType = "g_invasion_type";
    /// <summary>The active invasion variant (QC autocvar_g_invasion_type), default ROUND.</summary>
    public InvasionType Type => TryCvar(CvarType, out float v) ? (InvasionType)(int)v : InvasionType.Round;
    // ----- point/round limit cvars + defaults (gametype default pointlimit=50) -----
    private const string CvarPointLimit    = "g_invasion_point_limit";
    private const string CvarFragLimit     = "fraglimit"; // GameRules_limit_score writes the point limit here
    private const int    DefaultPointLimit = 50;          // gametype_init "pointlimit=50"

    private const string CvarMaxRounds    = "g_invasion_rounds";
    private const int    DefaultMaxRounds = 15;           // QC inv_maxrounds default

    private const string CvarMonsterCount    = "g_invasion_monster_count";
    private const int    DefaultMonsterCount = 10;        // QC autocvar_g_invasion_monster_count default

    // ----- round-cadence cvars (QC round_handler_Init args) + their balance-config defaults -----
    private const string CvarWarmup        = "g_invasion_warmup";            // inter-round warmup countdown
    private const float  DefaultWarmup     = 10f;                            // gametypes-server.cfg g_invasion_warmup 10
    private const string CvarRoundTimeLimit = "g_invasion_round_timelimit";  // per-round time limit
    private const float  DefaultRoundTimeLimit = 120f;                       // gametypes-server.cfg 120
    private const float  RoundEndDelay     = 5f;                             // QC round_handler_Init(5, ...)

    private const string CvarZombiesOnly   = "g_invasion_zombies_only";      // QC autocvar_g_invasion_zombies_only
    private const string CvarSpawnpointDelay = "g_invasion_spawnpoint_spawn_delay"; // recent-use de-weight window
    private const float  DefaultSpawnpointDelay = 0.5f;                      // gametypes-server.cfg 0.5

    // ----- QC MUTATOR_HOOKFUNCTION(inv, MonsterSpawn): mon.dphitcontentsmask = SOLID|BODY|BOTCLIP|MONSTERCLIP -----
    // SUPERCONTENTS_* bits (must match XonoticGodot.Engine.Collision.SuperContents — Common can't reference Engine,
    // so they are mirrored here exactly as LagComp.cs / Projectiles.cs / Nexball.cs do). Stamping this makes an
    // invasion monster's own movement trace honor bot-clip + monster-clip AI hint brushes (a SlideBox monster
    // already gets SOLID|BODY|MONSTERCLIP by default; the BOTCLIP bit is the part Base adds on top).
    private const int SuperContentsSolid       = 0x00000001; // QC DPCONTENTS_SOLID
    private const int SuperContentsBody        = 0x02000000; // QC DPCONTENTS_BODY
    private const int SuperContentsMonsterClip = 0x00000200; // QC DPCONTENTS_MONSTERCLIP
    private const int SuperContentsBotClip     = 0x00000800; // QC DPCONTENTS_BOTCLIP
    private const int MonsterHitContentsMask   = SuperContentsSolid | SuperContentsBody | SuperContentsBotClip | SuperContentsMonsterClip;

    /// <summary>Wave/round progress (QC inv_roundcnt / inv_numspawned / inv_numkilled / inv_maxspawned).</summary>
    public sealed class WaveState
    {
        /// <summary>Current round number (QC <c>inv_roundcnt</c>), starting at 1.</summary>
        public int Round = 1;

        /// <summary>How many monsters have been spawned in the current round (QC <c>inv_numspawned</c>).</summary>
        public int Spawned;

        /// <summary>How many monsters have been killed in the current round (QC <c>inv_numkilled</c>).</summary>
        public int Killed;

        /// <summary>Target number of monsters for the current round (QC <c>inv_maxspawned</c>).</summary>
        public int MaxSpawned;
    }

    /// <summary>The single shared wave state (co-op: all players fight the same monsters).</summary>
    public WaveState Wave { get; } = new();

    /// <summary>
    /// Per-wave monster lists (QC invasion_wave entities): a monster-netname list keyed by wave number. When a
    /// wave has a list, <see cref="PickMonster"/> draws from it (QC wave_ent.spawnmob) instead of picking at
    /// random. <see cref="GetWaveMonsters"/> resolves a round to the best matching list (exact, else the last
    /// list with a lower-or-equal number — QC invasion_GetWaveEntity).
    /// </summary>
    private readonly List<(int round, string[] monsters)> _waves = new();

    /// <summary>
    /// QC g_invasion_roundends winning latch: true once the STAGE objective has been reached by enough real
    /// players (>= ceil(realplayers * count), default count=0.7 = 70%). The STAGE win in <see cref="Tick"/>
    /// reads this (QC WinningCondition_Invasion: a roundend entity's <c>.winning</c> is set).
    /// </summary>
    public bool RoundEndReached { get; private set; }

    /// <summary>QC roundend <c>.count</c>: the fraction of real players who must reach the end (default 0.7).</summary>
    private const float DefaultRoundEndFraction = 0.7f;

    /// <summary>
    /// QC <c>.inv_endreached</c>: the distinct REAL players who have touched a STAGE level-end trigger. The win
    /// fires once <c>Count &gt;= ceil(realplayers * min(1, fraction))</c> (QC target_invasion_roundend_use), so a
    /// single player can't end a multi-player STAGE map.
    /// </summary>
    private readonly HashSet<Player> _endReached = new();

    private int _roundEnds;            // count of registered invasion_roundend triggers
    private int _monsterSkill = 1;     // QC inv_monsterskill, scaled per round + player count

    /// <summary>QC spawnfunc invasion_wave: define the monster list (space-separated netnames) for a wave number.</summary>
    public void AddWave(int waveNumber, string spawnmob)
    {
        if (string.IsNullOrWhiteSpace(spawnmob)) return;
        string[] list = spawnmob.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (list.Length > 0) _waves.Add((waveNumber, list));
    }

    /// <summary>QC invasion_GetWaveEntity: the monster list governing <paramref name="round"/> (null = random pick).</summary>
    public string[]? GetWaveMonsters(int round)
    {
        string[]? exact = null, best = null; int bestNum = int.MinValue;
        foreach (var (num, list) in _waves)
        {
            if (num == round) { exact = list; break; }
            if (num <= round && num > bestNum) { best = list; bestNum = num; }
        }
        return exact ?? best;
    }

    /// <summary>QC spawnfunc invasion_roundend: register a STAGE objective trigger (reaching it ends the level).</summary>
    public void AddRoundEnd() => _roundEnds++;

    /// <summary>
    /// QC <c>target_invasion_roundend_use</c> (sv_invasion.qc): a STAGE objective trigger was touched by
    /// <paramref name="actor"/>. Mark that REAL player as having reached the end (bots don't count), then win
    /// only once <c>ceil(realplayers * min(1, fraction))</c> distinct real players have reached it (fraction
    /// default 0.7 = 70%). A single touch ends a solo/co-op map (realplayers=1 → threshold 1) but not a
    /// multi-player STAGE map. <paramref name="fraction"/> mirrors the trigger's <c>.count</c> field (0 → 0.7).
    /// </summary>
    public void TriggerRoundEnd(Player? actor = null, float fraction = 0f)
    {
        if (Type != InvasionType.Stage) return;

        float frac = fraction > 0f ? System.MathF.Min(1f, fraction) : DefaultRoundEndFraction;

        // QC: only count real (non-bot) players. actor.inv_endreached = true.
        if (actor is not null && (actor.Flags & EntFlags.Client) != 0 && !actor.IsBot)
            _endReached.Add(actor);

        // QC: count real clients and how many have inv_endreached; win at >= ceil(realplnum * min(1, count)).
        int realCount = CountRealPlayers();
        if (realCount <= 0)
        {
            // No live roster to measure against (e.g. a headless/scripted touch): fall back to the legacy
            // single-touch behavior so solo/test STAGE maps still complete.
            RoundEndReached = true;
            return;
        }

        int reached = 0;
        foreach (Player p in _endReached)
            if (!p.IsFreed && (p.Flags & EntFlags.Client) != 0 && !p.IsBot)
                ++reached;

        int needed = (int)System.MathF.Ceiling(realCount * frac);
        if (needed < 1) needed = 1;
        if (reached >= needed)
            RoundEndReached = true;
    }

    /// <summary>QC FOREACH_CLIENT(IS_PLAYER(it) &amp;&amp; IS_REAL_CLIENT(it)): count the live non-bot players.</summary>
    private int CountRealPlayers()
    {
        if (Api.Services is null) return 0;
        int n = 0;
        foreach (Entity e in Api.Entities.FindByClass("player"))
            if (e is Player p && !p.IsFreed && (p.Flags & EntFlags.Client) != 0 && !p.IsBot)
                ++n;
        return n;
    }

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>
    /// The Invasion broadcast notifications QC sends via <c>Send_Notification</c> (CENTER_/INFO_ROUND_OVER,
    /// ROUND_PLAYER_WIN, CENTER_INVASION_SUPERMONSTER). As with <see cref="Survival.ISurvivalNotifications"/>,
    /// the gametype only signals WHAT to send; the actual MSG_CENTER/MSG_INFO networking is a host/CSQC concern,
    /// so the host injects an implementation. Null = headless/no-op (tests, listen server before HUD wiring).
    /// </summary>
    public interface IInvasionNotifications
    {
        /// <summary>QC <c>CENTER/INFO_ROUND_OVER</c>: the round timed out with no winner (monsters wiped).</summary>
        void RoundOver();

        /// <summary>QC <c>CENTER/INFO_ROUND_PLAYER_WIN</c>: <paramref name="winner"/> had the most KILLS this round.</summary>
        void RoundPlayerWin(Player winner);

        /// <summary>QC <c>CENTER_INVASION_SUPERMONSTER</c>: a supermonster (e.g. golem) arrived (<paramref name="name"/>).</summary>
        void Supermonster(string name);
    }

    /// <summary>Host sink for the Invasion notifications (see <see cref="IInvasionNotifications"/>).</summary>
    public IInvasionNotifications? Notifications;

    // ---- mutator-hook handlers (QC sv_invasion.qc MUTATOR_HOOKFUNCTIONs), subscribed in Activate ----
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _playerSpawnHandler;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _playerRegenHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _setStartItemsHandler;

    /// <summary>
    /// QC round_handler (server/round_handler.qc): the ROUND-mode wave/round cadence driver. Created in
    /// <see cref="Activate"/> with Invasion's real <c>Invasion_CheckPlayers</c> / <c>Invasion_CheckWinner</c> /
    /// <c>Invasion_RoundStart</c> callbacks and the warmup + per-round time limit; ticked from <see cref="Tick"/>.
    /// Mirrors the sibling self-drive pattern in <see cref="Survival.Handler"/> (the Wave-1 todo to fold this into
    /// the live GameWorld.EnableRounds handler is recorded for the host).
    /// </summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>Total monster kills banked toward the point limit across all rounds (QC team/score points).</summary>
    public int MonstersKilled { get; private set; }

    /// <summary>QC checkrules end-of-match latch: true once the point limit or final round is reached.</summary>
    public bool MatchEnded { get; private set; }

    public Invasion()
    {
        NetName = "inv";
        DisplayName = "Invasion";
        TeamGame = false; // QC: GAMETYPE_FLAG_USEPOINTS (co-op survival, default type)
    }

    /// <summary>The invasion_spawnpoint origins monsters spawn at (QC g_invasion_spawns). Empty → random.</summary>
    public readonly List<Vector3> SpawnPoints = new();

    /// <summary>
    /// Per-spawn-point recent-use clock (QC .spawnshieldtime), parallel to <see cref="SpawnPoints"/>: a point used
    /// at <c>time</c> is de-weighted (rating 0.2 vs 1.0) until <c>time + g_invasion_spawnpoint_spawn_delay</c>, so
    /// the wave doesn't cluster all monsters on one point (QC invasion_PickSpawn).
    /// </summary>
    private readonly List<float> _spawnShieldTimes = new();

    /// <summary>The monsters currently alive this wave (QC g_monsters), tracked for the fill loop.</summary>
    public readonly List<Entity> LiveMonsters = new();

    /// <summary>Seconds between wave-fill spawn attempts (QC g_invasion_spawn_delay).</summary>
    private const string CvarSpawnDelay = "g_invasion_spawn_delay";
    private float _nextSpawnTime;

    /// <summary>QC the inv_roundcnt 0→1 edge: whether the round handler has started its first round yet.</summary>
    private bool _firstRoundStarted;

    public override void OnInit()
    {
        // QC: monsters spawn at the map's invasion_spawnpoint entities (see SpawnPoints / AddSpawnPoint).
        // gametype_init flags (USEPOINTS) and the STAGE/HUNT type are engine/server-config concerns.
        SpawnPoints.Clear();
        _spawnShieldTimes.Clear();
        LiveMonsters.Clear();
        // QC the per-map wave lists (g_invasion_waves) + round-end triggers (g_invasion_roundends) are rebuilt
        // from the map entities each load — clear them so a map (re)load doesn't inherit the previous map's waves.
        // This is also the test-isolation guarantee: the gametype is a registry SINGLETON (GameTypes.ByName),
        // reused across GameWorld.Boot calls, so without this clear a prior boot's waves leak into the next.
        _waves.Clear();
        _roundEnds = 0;
        RoundEndReached = false;
        _endReached.Clear();
    }

    /// <summary>QC spawnfunc invasion_spawnpoint: register a monster spawn origin.</summary>
    public void AddSpawnPoint(Vector3 origin)
    {
        SpawnPoints.Add(origin);
        _spawnShieldTimes.Add(0f); // QC .spawnshieldtime starts at 0 (immediately eligible at full weight)
    }

    /// <summary>Seconds between wave-fill spawn attempts (QC autocvar_g_invasion_spawn_delay, cfg default 0.25).</summary>
    public float SpawnDelay => TryCvar(CvarSpawnDelay, out float v) && v > 0f ? v : 0.25f;

    /// <summary>
    /// The point limit in force (QC GameRules_limit_score(autocvar_g_invasion_point_limit)).
    ///
    /// Base semantics (sv_rules.qc GameRules_limit_score):
    ///  - <c>g_invasion_point_limit &gt; 0</c> → sets fraglimit to that value (explicit limit).
    ///  - <c>g_invasion_point_limit == 0</c> → sets fraglimit to 0 → WinningCondition_Scores gate
    ///    <c>(limit &amp;&amp; …)</c> is false → <b>no limit</b> (play without a kill cap).
    ///  - <c>g_invasion_point_limit &lt; 0</c> (default -1) → GameRules_limit_score returns early (no
    ///    fraglimit write) → fraglimit resolves to the mapinfo default (50).
    ///
    /// Returns 0 when no limit is in effect (caller's <see cref="CheckPointLimit"/> already skips on 0).
    /// </summary>
    public int PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimit, out float pl))
            {
                if (pl > 0f) return (int)pl;  // positive → explicit limit
                if (pl == 0f) return 0;        // explicit 0 → no limit (Base: fraglimit=0 → limit gate false)
                // pl < 0 (-1 sentinel) → fall through to mapinfo default
            }
            if (TryCvar(CvarFragLimit, out float fl) && fl > 0f) return (int)fl;
            return DefaultPointLimit;
        }
    }

    /// <summary>QC autocvar_g_invasion_warmup: inter-round warmup countdown length (cfg default 10s).</summary>
    public float Warmup => TryCvar(CvarWarmup, out float v) && v > 0f ? v : DefaultWarmup;

    /// <summary>QC autocvar_g_invasion_round_timelimit: per-round time limit (cfg default 120s; 0 = none).</summary>
    public float RoundTimeLimit => TryCvar(CvarRoundTimeLimit, out float v) && v >= 0f ? v : DefaultRoundTimeLimit;

    /// <summary>QC autocvar_g_invasion_zombies_only: restrict the random monster pick to undead (zombies).</summary>
    public bool ZombiesOnly => TryCvar(CvarZombiesOnly, out float v) && v != 0f;

    /// <summary>QC autocvar_g_invasion_spawnpoint_spawn_delay: how long a just-used spawn point is de-weighted.</summary>
    public float SpawnpointSpawnDelay => TryCvar(CvarSpawnpointDelay, out float v) && v > 0f ? v : DefaultSpawnpointDelay;

    /// <summary>The number of rounds before the match ends (QC inv_maxrounds, default 15).</summary>
    public int MaxRounds => TryCvar(CvarMaxRounds, out float v) && v > 0f ? (int)v : DefaultMaxRounds;

    /// <summary>Base monster count per round (QC autocvar_g_invasion_monster_count, default 10).</summary>
    public int BaseMonsterCount => TryCvar(CvarMonsterCount, out float v) && v > 0f ? (int)v : DefaultMonsterCount;

    /// <summary>The number of active players (QC numplayers) — drives the per-round monster-skill scaling. Host-set; default 1.</summary>
    public int PlayerCount { get; set; } = 1;

    /// <summary>QC <c>inv_monsterskill = inv_roundcnt + max(1, numplayers * 0.3)</c>: the wave's monster skill.</summary>
    public int ComputeMonsterSkill(int round)
        => round + (int)System.MathF.Max(1f, PlayerCount * 0.3f);

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        RoundEndReached = false;
        _endReached.Clear();
        MonstersKilled = 0;
        Wave.Round = 1;
        Wave.Spawned = 0;
        Wave.Killed = 0;
        Wave.MaxSpawned = ComputeWaveSize(Wave.Round);
        _monsterSkill = ComputeMonsterSkill(Wave.Round);
        _firstRoundStarted = false;
        _nextSpawnTime = 0f;

        // QC invasion_ScoreRules (sv_invasion.qc): GameRules_score_enabled(false); GameRules_scoring(0, 0, 0, {
        // field(SP_KILLS, "kills", SFL_SORT_PRIO_PRIMARY); }). Invasion is co-op vs monsters — the only score is
        // monster kills, so SP_KILLS is the primary sort key (no SP_SCORE). independent_players disables the rest.
        Scoring.GameScores.ScoreRulesBasics(teams: false, scoreEnabled: false);
        Scoring.GameScores.DeclareColumn("KILLS", Scoring.ScoreFlags.None, "kills");
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Kills);

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTIONs(inv, ...): co-op survival rules — no player-vs-player damage, no regen, the
        // ROUND 200/200 start loadout, and players removed from the bot/monster target lists (it's the players'
        // job to kill the monsters, not fight each other).
        _playerSpawnHandler   ??= OnPlayerSpawn;
        _playerRegenHandler   ??= OnPlayerRegen;
        _damageCalcHandler    ??= OnDamageCalculate;
        _setStartItemsHandler ??= OnSetStartItems;
        MutatorHooks.PlayerSpawn.Add(_playerSpawnHandler);
        MutatorHooks.PlayerRegen.Add(_playerRegenHandler);
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);
        MutatorHooks.SetStartItems.Add(_setStartItemsHandler);

        // QC MUTATOR_HOOKFUNCTION(inv, AccuracyTargetValid): a monster target returns MUT_ACCADD_INVALID
        // (always count) — so hits on the invasion monsters DO feed weapon-accuracy stats. Without this the
        // shared accuracy gate's IS_CLIENT check would reject non-client monster targets entirely.
        WeaponAccuracyEvents.TargetValidProvider = AccuracyTargetValid;

        // QC invasion_DelayedInit: the ROUND variant runs the round handler (round_handler_Spawn with the
        // Invasion-specific callbacks, then round_handler_Init(5, warmup, round_timelimit)). HUNT/STAGE use the
        // CheckRules winning-condition path instead, so they need no handler. We self-drive ours from Tick(),
        // matching the sibling Survival pattern (and the Wave-1 todo to fold this into GameWorld.EnableRounds).
        if (Type == InvasionType.Round)
        {
            Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
            {
                CanRoundStart = CheckPlayers,  // QC Invasion_CheckPlayers (always true)
                CanRoundEnd   = CheckWinner,    // QC Invasion_CheckWinner (timeout / wave-clear)
                OnRoundStart  = RoundStart,     // QC Invasion_RoundStart (++round, size the wave, unblock)
            };
            Handler.Init(RoundEndDelay, Warmup, RoundTimeLimit);
        }
        else
        {
            Handler = null;
        }
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;

        if (_playerSpawnHandler is not null)   MutatorHooks.PlayerSpawn.Remove(_playerSpawnHandler);
        if (_playerRegenHandler is not null)   MutatorHooks.PlayerRegen.Remove(_playerRegenHandler);
        if (_damageCalcHandler is not null)    MutatorHooks.DamageCalculate.Remove(_damageCalcHandler);
        if (_setStartItemsHandler is not null) MutatorHooks.SetStartItems.Remove(_setStartItemsHandler);
        if (WeaponAccuracyEvents.TargetValidProvider == AccuracyTargetValid)
            WeaponAccuracyEvents.TargetValidProvider = null;
        Handler = null;
    }

    // ---- QC sv_invasion.qc MUTATOR_HOOKFUNCTIONs (co-op survival rules) ----------------------------------

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(inv, PlayerSpawn): clear the spawned player's bot_attack so bots/monsters drop it
    /// from their target lists — in Invasion the monsters ignore players (the players hunt the monsters). The
    /// port's <see cref="MonsterAI.FindTarget"/> has no g_bot_targets/g_monster_targets gate, so the faithful
    /// stand-in is to flag the player <see cref="EntFlags.NoTarget"/>, which both bot aim and the monster
    /// <see cref="MonsterAI.ValidTarget"/> already honor. (Resolves invasion.spawn.chosen_monster monster_attack
    /// + invasion.bots.targeting in one shared mechanism.)
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        args.Player.Flags |= EntFlags.NoTarget;
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(inv, PlayerRegen): no health/armor regen in Invasion (any variant).</summary>
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args) => true; // QC return true = disable regen

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(inv, AccuracyTargetValid): <c>if (IS_MONSTER(frag_target)) return
    /// MUT_ACCADD_INVALID; return MUT_ACCADD_INDIFFERENT;</c>. A monster target is "always count" (Invalid)
    /// so hits on invasion monsters feed accuracy stats; any other target falls through to Indifferent (never
    /// count) — in co-op invasion the only valid accuracy target is a monster, never another player.
    /// </summary>
    private WeaponAccuracyEvents.AccuracyTarget AccuracyTargetValid(Entity attacker, Entity target)
        => IsMonster(target)
            ? WeaponAccuracyEvents.AccuracyTarget.Invalid
            : WeaponAccuracyEvents.AccuracyTarget.Indifferent;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(inv, Damage_Calculate): cancel all player-vs-player damage and knockback (co-op).
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (args.Attacker is Player && args.Target is Player && args.Attacker != args.Target)
        {
            args.Damage = 0f;
            args.Force = Vector3.Zero;
        }
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(inv, SetStartItems): the ROUND variant spawns players with 200 health / 200 armor.
    /// HUNT/STAGE keep the normal loadout (Base only buffs ROUND).
    /// </summary>
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        if (Type == InvasionType.Round)
        {
            args.Loadout.Health = 200f;
            args.Loadout.Armor = 200f;
        }
        return false;
    }

    /// <summary>
    /// QC inv_maxspawned: the number of monsters to spawn in a given round —
    /// <c>round(max(base, base * round * 0.5))</c>. Gives the wave its size target; <see cref="SpawnWaveMonster"/>
    /// fills it from the spawn points.
    /// </summary>
    public int ComputeWaveSize(int round)
    {
        int b = BaseMonsterCount;
        float scaled = System.MathF.Max(b, b * round * 0.5f);
        int n = (int)System.MathF.Round(scaled);
        return n < 1 ? 1 : n;
    }

    /// <summary>QC invasion spawn path: record that a monster spawned into the current round.</summary>
    public void NotifyMonsterSpawned()
    {
        Wave.Spawned += 1;
    }

    /// <summary>
    /// Advance Invasion one frame. ROUND mode drives the <see cref="Handler"/> (QC round_handler_Think), whose
    /// <c>CanRoundEnd</c> = <see cref="CheckWinner"/> runs the wave fill loop + the round-over / round-cleared
    /// determination and whose <c>OnRoundStart</c> = <see cref="RoundStart"/> sizes the next wave. HUNT/STAGE
    /// have no round handler (QC CheckRules path) and run the simpler direct win checks here. Call each tick;
    /// safe after <see cref="MatchEnded"/>.
    /// </summary>
    public void Tick()
    {
        if (MatchEnded)
            return;

        // Prune dead/removed monsters from the live list.
        for (int i = LiveMonsters.Count - 1; i >= 0; i--)
        {
            Entity m = LiveMonsters[i];
            if (m.IsFreed || m.DeadState != DeadFlag.No || m.Health <= 0f)
                LiveMonsters.RemoveAt(i);
        }

        // STAGE: the level-end objective trigger fired → the players win (QC WinningCondition_Invasion STAGE).
        if (Type == InvasionType.Stage && RoundEndReached)
        {
            MatchEnded = true;
            return;
        }

        // ROUND: the round handler is the wave driver (QC). It calls CheckWinner each frame (the fill loop + the
        // round-over/cleared test); when a round ends it waits out the warmup, calls RoundStart, and re-arms.
        if (Type == InvasionType.Round && Handler is not null)
        {
            Handler.Tick();
            return;
        }

        // HUNT: QC WinningCondition_Invasion INV_TYPE_HUNT —
        //   IL_EACH(g_monsters, !(it.spawnflags & MONSTERFLAG_RESPAWNED), { ++found; });
        //   if (found <= 0) { every alive player wins → WINNING_YES; }
        // Win when no non-respawned monster remains in the world. Base note: "NOTE: this ends the round if no
        // monsters are placed" — Base insta-wins on an empty HUNT map (first frame finds 0 remaining →
        // WINNING_YES). We mirror that exactly: no _huntSawMonster guard, no counter-based fallback path.
        if (Type == InvasionType.Hunt)
        {
            if (LiveNonRespawnedMonsterCount() <= 0)
                MatchEnded = true;
            return;
        }

        // STAGE while waiting for the objective: drive the wave fill + the clear advance directly.
        FillWave();

        if (Wave.Spawned < Wave.MaxSpawned)
            return;

        // STAGE while waiting for the objective: once the wave is dead, advance to the next round.
        if (Wave.Spawned >= 1 && Wave.Killed >= Wave.MaxSpawned)
            AdvanceWaveIfCleared();
    }

    /// <summary>
    /// QC Invasion_CheckWinner's spawn half: while the wave's quota isn't filled (alive + already-killed &lt; max),
    /// spawn one monster every <see cref="SpawnDelay"/> seconds (QC inv_lastcheck throttle). Returns true while the
    /// wave is still filling (so the round can't end yet).
    /// </summary>
    private bool FillWave()
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (Wave.Spawned < Wave.MaxSpawned)
        {
            if (now >= _nextSpawnTime)
            {
                SpawnWaveMonster();
                _nextSpawnTime = now + SpawnDelay;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// QC <c>Invasion_CheckPlayers</c> (sv_invasion.qc): the round-start gate — always true in Invasion (the
    /// round runs even solo, unlike the team modes' &gt;=2-players requirement).
    /// </summary>
    public bool CheckPlayers() => true;

    /// <summary>
    /// QC <c>Invasion_RoundStart</c> (sv_invasion.qc): begin a new round — cap and bump the round counter, scale
    /// the monster skill, reset the per-round spawn/kill counters, and size the next wave. Wired as the
    /// <see cref="Handler"/>'s OnRoundStart, so it fires once when the countdown reaches zero.
    /// </summary>
    public void RoundStart()
    {
        // QC: if (inv_roundcnt < inv_maxrounds) ++inv_roundcnt;  — a limiter, NOT a match-end (ROUND is bounded by
        // the point limit, not the round count). Activate already seeded round 1's wave (QC inv_roundcnt starts at
        // 0 and the first RoundStart bumps it to 1); so the FIRST handler round keeps round 1 (no double-bump),
        // and every subsequent round bumps the counter.
        if (_firstRoundStarted && Wave.Round < MaxRounds)
            Wave.Round += 1;
        _firstRoundStarted = true;

        _monsterSkill = ComputeMonsterSkill(Wave.Round); // QC inv_monsterskill = inv_roundcnt + max(1, np*0.3)
        Wave.Spawned = 0;
        Wave.Killed = 0;
        Wave.MaxSpawned = ComputeWaveSize(Wave.Round);
        _nextSpawnTime = 0f; // re-arm the fill throttle so the first monster of the round spawns promptly
    }

    /// <summary>
    /// QC <c>Invasion_CheckWinner</c> (sv_invasion.qc): the per-frame ROUND driver, wired as the
    /// <see cref="Handler"/>'s CanRoundEnd. (1) If the round time limit elapsed, wipe the monsters, broadcast
    /// ROUND_OVER and end the round (QC round_endtime branch). (2) Otherwise keep the wave filled; the round
    /// isn't over while it's still filling or any monster lives. (3) Once the whole wave is dead, find the
    /// top-KILLS player, announce ROUND_PLAYER_WIN, wipe the field and end the round. Returns true when the round
    /// is over.
    /// </summary>
    public bool CheckWinner()
    {
        // (1) QC: round_handler_GetEndTime() > 0 && expired → round over with no winner.
        if (Handler is not null && Handler.RoundEndTime > 0f
            && (Api.Services is not null ? Api.Clock.Time : 0f) >= Handler.RoundEndTime)
        {
            WipeMonsters();
            Notifications?.RoundOver(); // QC Send_Notification CENTER_ROUND_OVER + INFO_ROUND_OVER
            return true;
        }

        // (2) QC: keep spawning until the wave's quota is met; not over while filling or any monster is alive.
        if (FillWave())
            return false;
        if (Wave.Spawned < 1)
            return false;                          // QC: nothing has spawned yet
        if (LiveMonsters.Count > 0 || Wave.Killed < Wave.MaxSpawned)
            return false;                          // QC: inv_numkilled < inv_maxspawned (monsters still alive)

        // (3) QC: the wave is fully cleared — announce the top-KILLS player and wipe the field.
        Player? winner = TopKillsPlayer();
        WipeMonsters();
        if (winner is not null)
            Notifications?.RoundPlayerWin(winner); // QC CENTER/INFO_ROUND_PLAYER_WIN, winner.netname
        return true;
    }

    /// <summary>
    /// QC <c>Invasion_CheckWinner</c> winner pick: the live player with the most banked KILLS this match
    /// (FOREACH_CLIENT, GameRules_scoring_add(it, KILLS, 0) max). Null when no player has any kills.
    /// </summary>
    private Player? TopKillsPlayer()
    {
        if (Api.Services is null)
            return null;
        Player? winner = null;
        int best = 0;
        foreach (Entity e in Api.Entities.FindByClass("player"))
        {
            if (e is not Player p || (p.Flags & EntFlags.Client) == 0)
                continue;
            int kills = Scoring.GameScores.Get(p, Scoring.GameScores.Kills);
            if (kills > best) { best = kills; winner = p; }
        }
        return winner;
    }

    /// <summary>QC <c>IL_EACH(g_monsters, Monster_Remove); IL_CLEAR(g_monsters)</c>: clear the field between rounds.</summary>
    private void WipeMonsters()
    {
        if (Api.Services is null) { LiveMonsters.Clear(); return; }
        foreach (Entity m in LiveMonsters)
            if (!m.IsFreed)
            {
                MonsterAI.Forget(m);        // QC Monster_Remove: forget the monster state...
                Api.Entities.Remove(m);     // ...then delete the edict.
            }
        LiveMonsters.Clear();
    }

    /// <summary>
    /// QC WinningCondition_Invasion (INV_TYPE_HUNT): <c>IL_EACH(g_monsters, !(it.spawnflags &amp;
    /// MONSTERFLAG_RESPAWNED))</c> — count the monsters alive in the WORLD that have not gone through a
    /// death→respawn cycle. HUNT maps place their monsters with the map's monster spawnfuncs (NATURAL, first
    /// life), so the win source is the live world entities (Base's <c>g_monsters</c> intrusive list), not the
    /// ROUND wave-fill counters. A respawned monster (MONSTERFLAG_RESPAWNED) does NOT count toward "remaining".
    /// </summary>
    public int LiveNonRespawnedMonsterCount()
    {
        if (Api.Services is null)
            return LiveMonsters.Count; // headless fallback: use the tracked wave list
        int found = 0;
        foreach (Entity e in Api.Entities.FindByClass("monster"))
        {
            if (e.IsFreed || e.DeadState != DeadFlag.No || e.Health <= 0f)
                continue; // QC GetResource(it, RES_HEALTH) > 0 (only living monsters)
            if (MonsterAI.StateOf(e) is { Respawned: true })
                continue; // QC !(it.spawnflags & MONSTERFLAG_RESPAWNED)
            ++found;
        }
        return found;
    }

    /// <summary>
    /// QC invasion_SpawnChosenMonster: spawn one monster for the current wave at a random spawn point (or the
    /// origin when none are registered), via <see cref="invasion_PickMonster"/>. Increments the spawn count.
    /// Returns the monster entity (null when no facade or no monsters are registered).
    /// </summary>
    public Entity? SpawnWaveMonster()
    {
        Monster? def = PickMonster();
        if (def is null)
            return null;
        Vector3 origin = PickSpawnOrigin();
        Entity? m = SpawnMonsterDef(def, origin);
        if (m is not null)
        {
            m.GtWaveMonster = true;
            LiveMonsters.Add(m);
            NotifyMonsterSpawned();
        }
        return m;
    }

    /// <summary>
    /// QC invasion_PickSpawn: weighted-random spawn-point pick that de-weights a just-used point to 0.2 (vs 1.0)
    /// for <see cref="SpawnpointSpawnDelay"/> seconds (QC .spawnshieldtime), so a wave scatters across points
    /// instead of piling onto one. Falls back to <see cref="Vector3.Zero"/> when no spawn points are registered
    /// (the QC MoveToRandomMapLocation world-bounds scatter has no headless sampler yet — see todos).
    /// </summary>
    private Vector3 PickSpawnOrigin()
    {
        int n = SpawnPoints.Count;
        if (n <= 0)
            return Vector3.Zero; // QC MoveToRandomMapLocation fallback — not modeled headless (todo)

        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC RandomSelection over the spawn points: each has weight 1, rating 0.2 if recently used else 1.0.
        float totalRating = 0f;
        for (int i = 0; i < n; i++)
            totalRating += (now < _spawnShieldTimes[i]) ? 0.2f : 1.0f;

        int chosen = n - 1;
        if (totalRating > 0f)
        {
            float r = XonoticGodot.Common.Math.Prandom.Range(0f, totalRating);
            float acc = 0f;
            for (int i = 0; i < n; i++)
            {
                acc += (now < _spawnShieldTimes[i]) ? 0.2f : 1.0f;
                if (r <= acc) { chosen = i; break; }
            }
        }

        // QC: it.spawnshieldtime = time + autocvar_g_invasion_spawnpoint_spawn_delay (de-weight it next time).
        _spawnShieldTimes[chosen] = now + SpawnpointSpawnDelay;
        return SpawnPoints[chosen];
    }

    /// <summary>
    /// QC invasion_PickMonster / the per-wave spawnmob draw: if the current round has a configured monster list
    /// (<see cref="AddWave"/>), pick a monster by netname from it; otherwise pick uniformly over the registered
    /// Monsters catalog — but skip flying/swimming/passive/hidden/Quake-size monsters, skip supermonsters once
    /// one is already alive, and (under <see cref="ZombiesOnly"/>) keep only undead. Returns null if nothing is
    /// available.
    /// </summary>
    public Monster? PickMonster()
    {
        // QC: a wave entity's spawnmob list overrides the random pick for that round (no type filter on the list).
        string[]? list = GetWaveMonsters(Wave.Round);
        if (list is { Length: > 0 })
        {
            // try a handful of draws so a stray bad name doesn't abort the spawn.
            for (int attempt = 0; attempt < list.Length; attempt++)
            {
                string name = list[XonoticGodot.Common.Math.Prandom.RangeInt(0, list.Length)];
                Monster? m = Monsters.ByName(name);
                if (m is not null) return m;
            }
        }

        // QC invasion_PickMonster: RandomSelection over MON_* excluding HIDDEN | PASSIVE | FLY | SWIM | QUAKE_SIZE,
        // and excluding SUPERMONSTERs once one is alive (supermonster_count >= 1). zombies_only keeps only UNDEAD.
        bool superAlive = AnySupermonsterAlive();
        List<Monster> pool = new();
        foreach (Monster m in Monsters.All)
        {
            if (IsExcludedFromRandomPick(m)) continue;        // FLY/SWIM/PASSIVE/HIDDEN/QUAKE_SIZE
            if (superAlive && IsSupermonster(m)) continue;     // QC: only one supermonster at a time
            if (ZombiesOnly && !IsUndead(m)) continue;         // QC autocvar_g_invasion_zombies_only
            pool.Add(m);
        }
        if (pool.Count == 0)
        {
            // No eligible monster (e.g. zombies_only with no undead in the catalog) — fall back to the whole
            // catalog so a wave is never silently empty, mirroring QC's RandomSelection returning *something*.
            int all = Monsters.Count;
            return all > 0 ? Monsters.All[XonoticGodot.Common.Math.Prandom.RangeInt(0, all)] : null;
        }
        return pool[XonoticGodot.Common.Math.Prandom.RangeInt(0, pool.Count)];
    }

    // ---- QC monster type-flag classification (invasion_PickMonster spawnflag tests) --------------------------
    // The port's Monster descriptor carries no type-flag field yet (the FLY/UNDEAD/SUPERMONSTER tags only manifest
    // as entity flags during Spawn, or in the class doc-comments). Until a descriptor TypeFlags field lands (a
    // cross-file Framework change — see todos), classify by the catalog's known netnames, which is exact for the
    // five ported monsters: golem=SUPERMONSTER, wyvern=FLY, zombie=UNDEAD, mage/spider=normal melee/ranged.

    /// <summary>QC MON_FLAG_HIDDEN | MONSTER_TYPE_PASSIVE | MONSTER_TYPE_FLY | MONSTER_TYPE_SWIM | MONSTER_SIZE_QUAKE.</summary>
    private static bool IsExcludedFromRandomPick(Monster m)
        => m.NetName is "wyvern"; // wyvern is the catalog's only FLY monster; no passive/swim/hidden/quake monsters exist

    /// <summary>QC MON_FLAG_SUPERMONSTER: the boss-class monster (golem), spawned at most one at a time.</summary>
    private static bool IsSupermonster(Monster m) => m.NetName is "golem";

    /// <summary>QC MONSTER_TYPE_UNDEAD: the zombie (the only undead in the catalog).</summary>
    private static bool IsUndead(Monster m) => m.NetName is "zombie";

    /// <summary>QC invasion_PickMonster's supermonster_count: is a supermonster currently alive on the field?</summary>
    private bool AnySupermonsterAlive()
    {
        foreach (Entity m in LiveMonsters)
            if (!m.IsFreed && m.Health > 0f && m.NetName is "golem")
                return true;
        return false;
    }

    /// <summary>
    /// QC invasion_SpawnChosenMonster (sv_invasion.qc): spawn a concrete monster of <paramref name="def"/> at
    /// <paramref name="origin"/> through the shared <see cref="MonsterAI.SpawnMonster"/> driver — the port of QC
    /// <c>spawnmonster()</c> that invasion's QC equivalent calls. The driver runs the descriptor's
    /// <see cref="Monster.Spawn"/> (= <see cref="MonsterAI.Setup"/> + per-type specifics) exactly once, stamps the
    /// SPAWNED|NORESPAWN spawnflags, and — crucially — wires the per-frame think (<c>e.Think</c>/<c>e.NextThink</c>)
    /// so the simulation loop's SV_RunThink actually drives the monster brain each tick. Then stamps the wave's
    /// monster skill. Returns the new monster entity, or null when no facade is wired or no valid monster spawned.
    /// </summary>
    public Entity? SpawnMonsterDef(Monster def, Vector3 origin)
    {
        if (Api.Services is null)
            return null;

        // QC: spawnmonster(spawn(), tospawn, mon, spawn_point, spawn_point, spawn_point.origin, /*respwn*/ false,
        // /*removeifinvalid*/ false, /*moveflag*/ MONSTER_MOVE_WANDER). respwn=false adds MONSTERFLAG_NORESPAWN —
        // invasion monsters don't respawn (a kill advances the wave). The port has no spawn-point entity, so the
        // spawnedBy/follow args (only meaningful for player summons) are null; the resolved descriptor is passed
        // as monsterId (PickMonster already drew it, mirroring the wave's spawnmob list).
        // QC invasion_SpawnChosenMonster: on the final round, every monster is flagged MONSTERFLAG_MINIBOSS
        // (the last wave spawns minibosses). Stamp the spawnflag BEFORE SpawnMonster so MonsterAI.Setup's
        // miniboss branch (MonsterAI.cs:249) reads it and applies the health boost + red glow + miniboss loot.
        Entity e = Api.Entities.Spawn();
        if (Wave.Round >= MaxRounds)
            e.SpawnFlags |= MonsterAI.MonsterFlag_Miniboss;

        Entity? m = MonsterAI.SpawnMonster(e, monster: "", monsterId: def, spawnedBy: null, follow: null,
            origin: origin, respawn: false, removeIfInvalid: false, moveFlags: MonsterAI.MonsterMove_Wander);
        if (m is null)
            return null;

        // QC MUTATOR_HOOKFUNCTION(inv, MonsterSpawn): mon.dphitcontentsmask = SOLID|BODY|BOTCLIP|MONSTERCLIP, so
        // the monster's own movement trace honors bot-clip + monster-clip AI hint brushes that fence monster
        // movement on a Base server (a SlideBox monster already gets SOLID|BODY|MONSTERCLIP by default; this adds
        // the BOTCLIP bit and pins the full mask explicitly).
        m.DpHitContentsMask = MonsterHitContentsMask;

        // QC MUTATOR_HOOKFUNCTION(inv, MonsterSpawn): mon.monster_skill = inv_monsterskill. That hook fires at the
        // END of Monster_Spawn_Setup — after the skill-scaled health roll — so, like QC, this only scales the
        // monster's runtime speed/damage (via MONSTER_SKILLMOD), not its spawn health.
        if (MonsterAI.StateOf(m) is { } st)
            st.Skill = _monsterSkill;

        // QC MUTATOR_HOOKFUNCTION(inv, MonsterSpawn) supermonster branch: a supermonster arriving broadcasts
        // CENTER_INVASION_SUPERMONSTER with its display name.
        if (IsSupermonster(def))
            Notifications?.Supermonster(def.DisplayName.Length > 0 ? def.DisplayName : def.NetName);
        return m;
    }

    /// <summary>
    /// Once every monster in the current round has been killed (<c>Killed &gt;= MaxSpawned</c> and at least one
    /// spawned), advance to the next round and size its wave. In ROUND mode the <see cref="Handler"/> drives the
    /// round cadence (this is a no-op there — the handler's CheckWinner/RoundStart roll the round over with the
    /// warmup + notifications); this remains the simple direct rollover for the STAGE-while-waiting path. HUNT
    /// has no rounds. The ROUND match-end is the point limit (QC GameRules_limit_score), not the round count.
    /// </summary>
    public bool AdvanceWaveIfCleared()
    {
        if (Wave.Spawned < 1 || Wave.Killed < Wave.MaxSpawned)
            return false;

        // ROUND uses the round handler; HUNT clears the placed set (handled in Tick) — neither rolls over here.
        if (Type is InvasionType.Round or InvasionType.Hunt)
            return false;

        // STAGE while waiting for the objective: keep escalating waves (QC caps the round counter at maxrounds).
        if (Wave.Round < MaxRounds)
            Wave.Round += 1;
        Wave.Spawned = 0;
        Wave.Killed = 0;
        Wave.MaxSpawned = ComputeWaveSize(Wave.Round);
        _monsterSkill = ComputeMonsterSkill(Wave.Round); // QC: skill scales with the round + player count
        _nextSpawnTime = 0f;
        return false;
    }

    /// <summary>
    /// The obituary handler — when a monster is killed, advance the current wave's kill count, bank a point,
    /// check the point limit, and roll over to the next round if the wave is cleared. Player deaths don't
    /// score (this is co-op vs monsters); a non-monster victim is ignored here.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (MatchEnded)
            return false;

        // A player victim doesn't score in Invasion (co-op); only monster kills matter.
        if (ev.Victim is Player victim)
        {
            Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
            return false;
        }

        // Victim is a monster (QC IS_MONSTER): the killer banks a point and clears one of the wave.
        if (IsMonster(ev.Victim))
        {
            Wave.Killed += 1;        // QC inv_numkilled
            MonstersKilled += 1;     // banked toward the point limit

            // QC invasion MonsterDies/PlayerScore: a player killing a monster gets SP_KILLS +1 (the primary key).
            // The shared Scores table only credits player victims, so Invasion writes the monster-kill column here.
            if (ev.Attacker is Player killer)
                Scoring.GameScores.AddToPlayer(killer, Scoring.GameScores.Kills, 1);

            CheckPointLimit();
            // ROUND advances via the round handler (CheckWinner) so the warmup + round notifications fire; only
            // the direct STAGE-waiting path rolls the wave over on a kill here. HUNT's win is checked in Tick.
            if (!MatchEnded && Type == InvasionType.Stage)
                AdvanceWaveIfCleared();
        }

        return false;
    }

    /// <summary>QC IS_MONSTER(ent): a monster victim — carries the FL_MONSTER flag (set by <see cref="MonsterAI.Setup"/>).</summary>
    private static bool IsMonster(Entity e) => (e.Flags & EntFlags.Monster) != 0 || e.ClassName == "monster";

    /// <summary>
    /// QC GameRules_limit_score (sv_rules.qc) + WinningCondition_Scores (server/world.qc:1625): the match ends
    /// when the LEADING player's primary score reaches the limit — <c>WinningConditionHelper_topscore &gt;= limit</c>
    /// — and the primary score in Invasion is SP_KILLS (the sole, SFL_SORT_PRIO_PRIMARY column). So the limit is
    /// the single best player's KILLS, NOT the cumulative kills across all players. We read the top-KILLS player
    /// from the live score table; when there is no live roster (headless tests that bank kills without spawning
    /// player edicts) we fall back to the cumulative <see cref="MonstersKilled"/> so the latch still fires.
    /// </summary>
    public void CheckPointLimit()
    {
        int limit = PointLimit;
        if (limit <= 0)
            return;

        int topScore = TopKillsScore();
        // QC WinningConditionHelper_topscore >= fraglimit: the leading player's KILLS. Fall back to the
        // cumulative banked total only when no live player roster is present to measure against.
        int leading = topScore >= 0 ? topScore : MonstersKilled;
        if (leading >= limit)
            MatchEnded = true;
    }

    /// <summary>
    /// QC <c>WinningConditionHelper_topscore</c> for Invasion's primary score (SP_KILLS): the highest banked
    /// KILLS across the live players, or -1 when there is no live player roster to measure (headless tests).
    /// </summary>
    private int TopKillsScore()
    {
        if (Api.Services is null)
            return -1;
        int best = -1;
        foreach (Entity e in Api.Entities.FindByClass("player"))
        {
            if (e is not Player p || (p.Flags & EntFlags.Client) == 0)
                continue;
            int kills = Scoring.GameScores.Get(p, Scoring.GameScores.Kills);
            if (kills > best) best = kills;
        }
        return best;
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
