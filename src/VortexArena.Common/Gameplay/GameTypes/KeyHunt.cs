using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Key Hunt — port of <c>CLASS(KeyHunt, Gametype)</c>
/// (common/gametypes/gametype/keyhunt/{keyhunt.qh,sv_keyhunt.qc}). There is one key per team; a team
/// scores a "capture" when it gathers ALL keys onto its own players simultaneously (QC
/// kh_Key_AllOwnedByWhichTeam → kh_FinishCapture). Captures and collect/push events add to the team's
/// SCORE; first team to the point limit (QC g_keyhunt_point_limit, default 1000) wins. Default 3 teams.
///
/// QC defaults (gametype_init): "timelimit=20 pointlimit=1000 teams=3 leadlimit=0" (legacydefaults "1000 20 3 0").
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (<see cref="TeamBalance"/>);
///  - per-team keys with a carrier (<see cref="KeyState"/>), assigned by pickup
///    (<see cref="AssignKey"/>, QC kh_Key_AssignTo);
///  - the all-keys-on-one-team capture check (<see cref="AllOwnedByWhichTeam"/> →
///    <see cref="CheckCaptureGeometry"/>, QC kh_Key_AllOwnedByWhichTeam → kh_Key_Think)
///    awarding the team the capture SCORE + KH_CAPS and resetting the keys;
///  - point-limit + lead-limit win condition (GameRules_limit_score → fraglimit).
///
/// Faithfully ported (objective layer):
///  - one key entity per team spawned at round start onto a random teammate (<see cref="StartRound"/>,
///    QC kh_StartRound → kh_Key_Spawn);
///  - the controller think loop with wait-for-players (<see cref="Tick"/>/<see cref="WaitForPlayers"/>,
///    QC kh_Controller_Think / kh_WaitForPlayers);
///  - drop on death + collect by an enemy + auto-return of a dropped key (<see cref="DropAllKeys"/>/
///    <see cref="KeyTouch"/>/<see cref="KeyThink"/>, QC kh_Key_DropAll / kh_Key_Collect / kh_Key_Think);
///  - the all-keys-on-one-team-AND-in-range capture (<see cref="CheckCaptureGeometry"/>, QC kh_Key_Think →
///    kh_WinnerTeam) distributing the capture SCORE, and the carrier-frag bonus.
///
/// Faithfully ported (presentation-feeding):
///  - key model + team coloring + glow + spin (QC kh_Key_Spawn: model "key", colormod = Team_ColorRGB × KH_KEY_BRIGHTNESS,
///    per-team netname): <see cref="SpawnKey"/> stamps the model/effects/colormap/netname + a slow spin so the
///    networked entity stream renders the key (the ServerNet picks up any entity carrying a model). The carried
///    key swaps to the carried model on attach (<see cref="SetKeyVisual"/>); the waypoint SPRITE is the only
///    client-only remainder.
///
/// Deferred (NOTE — cross-boundary): the key WAYPOINT SPRITE rendering, the push-off-map "destroyed" loser
/// bonus split (kh_LoserTeam), and the interfere/meet center-print notifies.
/// </summary>
[GameType]
public sealed class KeyHunt : GameType
{
    // ----- key presentation (QC kh_Key_Spawn model/colormod/netname; KH_KEY_BRIGHTNESS = 2) -----
    private const string KeyModel = "models/keyhunt/key.md3";   // QC MDL_KH_KEY (dropped) / MDL_KH_KEY_CARRIED
    private const float  KeyBrightness = 2f;                    // QC KH_KEY_BRIGHTNESS
    private static readonly Vector3 KeySpin = new(0f, 90f, 0f); // a slow yaw spin for a dropped key (cosmetic)
    // ----- point-limit cvars + default (g_keyhunt_point_limit → fraglimit; legacydefaults 1000) -----
    private const string CvarPointLimitKh = "g_keyhunt_point_limit";
    private const string CvarPointLimit   = "fraglimit";
    private const string CvarLeadLimitKh  = "g_keyhunt_point_leadlimit";
    private const string CvarLeadLimit    = "leadlimit";
    private const float  DefaultPointLimit = 1000f;

    // ----- capture/collect score cvars + defaults (g_balance_keyhunt_score_*) -----
    private const string CvarScoreCapture = "g_balance_keyhunt_score_capture";
    private const string CvarScoreCollect = "g_balance_keyhunt_score_collect";
    private const string CvarScoreCarrierFrag = "g_balance_keyhunt_score_carrierfrag";
    private const string CvarScorePush      = "g_balance_keyhunt_score_push";
    private const string CvarScoreDestroyed = "g_balance_keyhunt_score_destroyed";
    private const float  DefaultScoreCapture = 100f;
    private const float  DefaultScoreCollect = 3f;            // QC default g_balance_keyhunt_score_collect 3
    private const float  DefaultScoreCarrierFrag = 2f;        // QC default g_balance_keyhunt_score_carrierfrag 2
    private const float  DefaultScorePush      = 60f;         // QC default g_balance_keyhunt_score_push 60
    private const float  DefaultScoreDestroyed = 50f;         // QC default g_balance_keyhunt_score_destroyed 50

    // ----- key timing cvars (g_balance_keyhunt_*) -----
    private const string CvarDelayReturn  = "g_balance_keyhunt_delay_return";  // seconds a dropped key auto-returns
    private const string CvarDelayCollect = "g_balance_keyhunt_delay_collect"; // dropper re-collect delay
    private const string CvarDelayRound   = "g_balance_keyhunt_delay_round";   // countdown between rounds
    private const string CvarDelayTracking = "g_balance_keyhunt_delay_tracking"; // delay before radar/waypoint reveal
    private const string CvarMaxDist      = "g_balance_keyhunt_maxdist";       // carriers must be within this to capture
    private const string CvarDropVelocity  = "g_balance_keyhunt_dropvelocity";  // death-drop throw speed
    private const string CvarThrowVelocity = "g_balance_keyhunt_throwvelocity"; // voluntary +use drop speed
    private const string CvarProtectTime   = "g_balance_keyhunt_protecttime";   // pusher-credit window
    private const float  DefaultDelayReturn  = 60f;            // QC default g_balance_keyhunt_delay_return 60
    private const float  DefaultDelayCollect = 1.5f;
    private const float  DefaultDelayRound   = 5f;
    private const float  DefaultDelayTracking = 10f;          // QC default g_balance_keyhunt_delay_tracking 10
    private const float  DefaultMaxDist      = 150f;           // QC default g_balance_keyhunt_maxdist 150
    private const float  DefaultDropVelocity  = 300f;          // QC default g_balance_keyhunt_dropvelocity 300
    private const float  DefaultThrowVelocity = 400f;          // QC default g_balance_keyhunt_throwvelocity 400
    private const float  DefaultProtectTime   = 0.8f;          // QC default g_balance_keyhunt_protecttime 0.8

    /// <summary>QC kh_Key_Think siren cadence: re-play SND_KH_ALARM every 2.5s while one team holds all keys.</summary>
    private const float SirenPeriod = 2.5f;

    // ----- carried-key orbit (QC kh_Key_Think #ifndef KH_PLAYER_USE_ATTACHMENT — the LIVE path; the attachment
    // block is commented out in Base sv_keyhunt.qc:37). The carried key circles the carrier at radius KH_KEY_XYDIST
    // and height KH_KEY_ZSHIFT, the yaw advancing at KH_KEY_XYSPEED deg/sec from the key's per-team spawn angle. -----
    private const float KeyZShift  = 22f; // QC KH_KEY_ZSHIFT
    private const float KeyXyDist  = 24f; // QC KH_KEY_XYDIST
    private const float KeyXySpeed = 45f; // QC KH_KEY_XYSPEED

    // ----- carrier/noncarrier combat damage+force matrix cvars (QC Damage_Calculate; stock all "1 1 1") -----
    private const string CvarCarrierDamage    = "g_balance_keyhunt_carrier_damage";
    private const string CvarCarrierForce     = "g_balance_keyhunt_carrier_force";
    private const string CvarNoncarrierDamage = "g_balance_keyhunt_noncarrier_damage";
    private const string CvarNoncarrierForce  = "g_balance_keyhunt_noncarrier_force";

    // ----- lava/slime/trigger destroy + damageforcescale (QC return_when_unreachable / kh_Key_Damage) -----
    private const string CvarDelayDamageReturn = "g_balance_keyhunt_delay_damage_return";
    private const string CvarReturnWhenUnreachable = "g_balance_keyhunt_return_when_unreachable";
    private const string CvarDamageForcescale = "g_balance_keyhunt_damageforcescale";
    private const float  DefaultDelayDamageReturn  = 5f;   // QC default g_balance_keyhunt_delay_damage_return 5
    private const float  DefaultDamageForcescale   = 1f;   // QC default g_balance_keyhunt_damageforcescale 1

    // ----- destroy own-team-holder bonus factor (QC score_destroyed_ownfactor, default 1) -----
    private const string CvarScoreDestroyedOwnFactor = "g_balance_keyhunt_score_destroyed_ownfactor";
    private const float  DefaultScoreDestroyedOwnFactor = 1f;

    public float DelayDamageReturn => TryCvar(CvarDelayDamageReturn, out float v) ? v : DefaultDelayDamageReturn;
    public float DamageForcescale => TryCvar(CvarDamageForcescale, out float v) ? v : DefaultDamageForcescale;
    public float ScoreDestroyedOwnFactor => TryCvar(CvarScoreDestroyedOwnFactor, out float v) ? v : DefaultScoreDestroyedOwnFactor;

    /// <summary>Key bbox (QC KH_KEY_MIN/MAX, current Base const; 0.8.6 used the legacy box with sv_legacy_bbox_expand).</summary>
    private static readonly Vector3 KeyMins = new(-25f, -25f, -46f);
    private static readonly Vector3 KeyMaxs = new(25f, 25f, 4f);

    public float ScoreCarrierFrag => TryCvar(CvarScoreCarrierFrag, out float v) ? v : DefaultScoreCarrierFrag;
    public float ScorePush      => TryCvar(CvarScorePush, out float v) ? v : DefaultScorePush;
    public float ScoreDestroyed => TryCvar(CvarScoreDestroyed, out float v) ? v : DefaultScoreDestroyed;
    public float DelayReturn  => TryCvar(CvarDelayReturn, out float v) ? v : DefaultDelayReturn;
    public float DelayCollect => TryCvar(CvarDelayCollect, out float v) ? v : DefaultDelayCollect;
    public float DelayRound   => TryCvar(CvarDelayRound, out float v) ? v : DefaultDelayRound;
    public float DelayTracking => TryCvar(CvarDelayTracking, out float v) ? v : DefaultDelayTracking;
    public float MaxDist      => TryCvar(CvarMaxDist, out float v) ? v : DefaultMaxDist;
    public float DropVelocity  => TryCvar(CvarDropVelocity, out float v) ? v : DefaultDropVelocity;
    public float ThrowVelocity => TryCvar(CvarThrowVelocity, out float v) ? v : DefaultThrowVelocity;
    public float ProtectTime   => TryCvar(CvarProtectTime, out float v) ? v : DefaultProtectTime;

    /// <summary>UPPERCASE team suffix used by the registered KH notifications (KEYHUNT_PICKUP_RED, …).</summary>
    private static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "RED",
    };

    /// <summary>QC kh_controller state: the round phase the controller is driving.</summary>
    public enum RoundPhase { WaitingForPlayers, Countdown, InProgress }

    /// <summary>The current round phase (QC kh_controller think state).</summary>
    public RoundPhase Phase { get; private set; } = RoundPhase.WaitingForPlayers;

    /// <summary>Absolute sim time the countdown ends and the round begins (QC kh_controller countdown).</summary>
    public float RoundStartTime { get; private set; }

    /// <summary>The roster the controller drives (set by the host before <see cref="Tick"/>).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    /// <summary>Provide the controller with the current roster (call before <see cref="Tick"/>).</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    // ----- team count cvars (g_keyhunt_teams_override >= 2 ? override : g_keyhunt_teams), 2..4, default 3 -----
    private const string CvarTeamsOverride = "g_keyhunt_teams_override";
    private const string CvarTeams         = "g_keyhunt_teams";
    private const int    DefaultTeams      = 3;

    // Per-team running totals (QC ST_SCORE slot 0 + ST_KH_CAPS slot 1) now live in the unified GameScores two-slot
    // team store — the source of truth (common/scores.qh). KH's team primary is ST_SCORE with ST_KH_CAPS
    // secondary. Read/written via GetTeamScore / AddTeamScore (slot 0) + CreditCapture (slot 1 caps).

    /// <summary>The key belonging to each team (QC FOR_EACH_KH_KEY), keyed by the key's home-team color code.</summary>
    public readonly Dictionary<int, KeyState> Keys = new();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    /// <summary>QC kh_Key_Think .siren_time: next sim time the all-keys-owned alarm may sound (rate-limited).</summary>
    private float _sirenTime;

    /// <summary>QC kh_tracking_enabled: whether the radar/waypoint tracking device has powered up this round (after
    /// delay_tracking). Gates the dropped-key + carrier waypoint sprite visibility (sprites themselves deferred).</summary>
    public bool TrackingEnabled { get; private set; }

    /// <summary>Absolute sim time the tracking device powers up (QC kh_EnableTrackingDevice schedule); 0 = none.</summary>
    private float _trackingEnableTime;

    /// <summary>QC kh_interferemsg_time/team: a deferred (time+0.2) INTERFERE/MEET/HELP center-print volley.</summary>
    private float _interfereMsgTime;
    private int _interfereMsgTeam = Teams.None;
    /// <summary>Which team currently owns ALL keys (QC kh_Key_AllOwnedByWhichTeam result), to detect transitions.</summary>
    private int _allOwnedTeam = Teams.None;

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.PlayerUseKeyArgs>? _useKeyHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;

    /// <summary>QC KH_KEY_WP_ZSHIFT: the height offset for key waypoint sprites above the origin.</summary>
    private const float KeyWpZShift = 20f;

    public KeyHunt()
    {
        NetName = "kh";
        DisplayName = "Key Hunt";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC kh_Initialize: the controller starts in wait-for-players; the first round spawns one key per
        // team once every team has a live player (kh_WaitForPlayers → kh_StartRound). GameRules_teams(true)
        // and kh_ScoreRules are the engine's job; here we put the controller in its initial phase.
        Phase = RoundPhase.WaitingForPlayers;
        RoundStartTime = 0f;
    }

    /// <summary>
    /// QC sv_keyhunt.qh: <c>GameRules_spawning_teams(autocvar_g_keyhunt_team_spawns)</c> — KeyHunt gates team
    /// spawns on g_keyhunt_team_spawns (stock default 0, so it does NOT use team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_keyhunt_team_spawns", out float v) ? v != 0f : false;

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

    /// <summary>Point limit in force (g_keyhunt_point_limit, else fraglimit, else 1000). 0 == unlimited.</summary>
    public float PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimitKh, out float pl)) return pl;
            if (TryCvar(CvarPointLimit, out float fl)) return fl;
            return DefaultPointLimit;
        }
    }

    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimitKh, out float ll)) return ll;
            if (TryCvar(CvarLeadLimit, out float l)) return l;
            return 0f;
        }
    }

    public float ScoreCapture => TryCvar(CvarScoreCapture, out float v) ? v : DefaultScoreCapture;
    public float ScoreCollect => TryCvar(CvarScoreCollect, out float v) ? v : DefaultScoreCollect;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring
        DeclareScoreRules();
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)
        foreach (int team in Teams.Active(TeamCount))
            if (!Keys.ContainsKey(team)) Keys[team] = new KeyState(team);
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
        // QC MUTATOR_HOOKFUNCTION(kh, PlayerUseKey): the +use key in a carrier's hands drops one key (kh_Key_DropOne).
        _useKeyHandler = OnUseKey;
        MutatorHooks.PlayerUseKey.Add(_useKeyHandler);
        // QC MUTATOR_HOOKFUNCTION(kh, Damage_Calculate): scale player-vs-player damage/force by the carrier matrix.
        _damageCalcHandler ??= OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);
    }

    /// <summary>
    /// QC <c>kh_ScoreRules</c> (sv_keyhunt.qc): declare KeyHunt's columns + the two TEAM-score slots and pin the
    /// sort keys. QC: <c>GameRules_scoring(teams, PRIMARY, PRIMARY, { field_team(ST_KH_CAPS,"caps",SECONDARY);
    /// field(SP_KH_CAPS,"caps",SECONDARY); ... })</c> — so the TEAM primary is slot 0 (ST_SCORE, stprio=PRIMARY)
    /// with slot 1 (ST_KH_CAPS, "caps") SECONDARY; the PLAYER primary is SP_SCORE with SP_KH_CAPS secondary. The
    /// other columns (kckills/pushes/destructions/pickups/losses) are display stats.
    /// </summary>
    private static void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: true);
        // Team slots: ST_SCORE (slot 0) "score" PRIMARY; ST_KH_CAPS (slot 1) "caps" SECONDARY.
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.SortPrioPrimary);
        GS.SetTeamLabel(GS.TeamSlotSecondary, "caps", Scoring.ScoreFlags.SortPrioSecondary);
        GS.DeclareColumn("KH_CAPS", Scoring.ScoreFlags.None, "caps");
        GS.DeclareColumn("KH_PUSHES", Scoring.ScoreFlags.None, "pushes");
        GS.DeclareColumn("KH_DESTRUCTIONS", Scoring.ScoreFlags.LowerIsBetter, "destructions");
        GS.DeclareColumn("KH_PICKUPS", Scoring.ScoreFlags.None, "pickups");
        GS.DeclareColumn("KH_KCKILLS", Scoring.ScoreFlags.None, "kckills");
        GS.DeclareColumn("KH_LOSSES", Scoring.ScoreFlags.LowerIsBetter, "losses");
        GS.SetSortKeys(GS.Score, GS.Field("KH_CAPS"));
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a KH player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>QC <c>GameRules_scoring_add_team(key.owner, KH_CAPS, 1)</c>: credit KH_CAPS to BOTH the team
    /// (ST_KH_CAPS, team slot 1 — the team secondary) and a key carrier on the capturing team (the port has no
    /// single "key.owner", so the first such carrier is credited the player SP_KH_CAPS column).</summary>
    private void CreditCapture(int team)
    {
        if (team != Teams.None)
            Scoring.GameScores.AddToTeam(team, Scoring.GameScores.TeamSlotSecondary, 1); // QC team ST_KH_CAPS
        foreach (var key in Keys.Values)
            if (key.Carrier is { } c && (int)c.Team == team) { AddCol(c, "KH_CAPS", 1); return; }
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (_useKeyHandler is not null)
        {
            MutatorHooks.PlayerUseKey.Remove(_useKeyHandler);
            _useKeyHandler = null;
        }
        if (_damageCalcHandler is not null)
        {
            MutatorHooks.DamageCalculate.Remove(_damageCalcHandler);
            _damageCalcHandler = null;
        }
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(kh, Damage_Calculate): scale player-vs-player damage AND force by the 9-way
    /// carrier/target matrix. The attacker's carry state selects the cvar (g_balance_keyhunt_carrier_* if the
    /// attacker holds a key, else _noncarrier_*); the cvar's x/y/z component selects the target case — x = self,
    /// y = the target is a (other) key carrier, z = the target is a noncarrier. Stock defaults are all "1 1 1"
    /// (a no-op at defaults), but a server tuning these now takes effect. No-op for non-player attacker/target.
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (args.Attacker is not Player attacker || args.Target is not Player target)
            return false; // QC: only apply scaling to player versus player combat

        bool attackerCarries = IsCarrier(attacker);
        string damageCvar = attackerCarries ? CvarCarrierDamage : CvarNoncarrierDamage;
        string forceCvar  = attackerCarries ? CvarCarrierForce  : CvarNoncarrierForce;
        // QC component select: .x = self (target==attacker), .y = target carries a key, .z = target is a noncarrier.
        int component = ReferenceEquals(target, attacker) ? 0 : (IsCarrier(target) ? 1 : 2);

        args.Damage *= CvarVectorComponent(damageCvar, component, 1f);
        args.Force  *= CvarVectorComponent(forceCvar, component, 1f);
        return false;
    }

    /// <summary>QC <c>player.kh_next != NULL</c>: the player is carrying at least one key.</summary>
    private bool IsCarrier(Player p)
    {
        foreach (var k in Keys.Values)
            if (ReferenceEquals(k.Carrier, p)) return true;
        return false;
    }

    /// <summary>Read component <paramref name="index"/> (0=x,1=y,2=z) of a vector cvar string ("a b c"),
    /// falling back to <paramref name="fallback"/> when unset/short (QC autocvar &lt;vector&gt; semantics).</summary>
    private static float CvarVectorComponent(string name, int index, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s))
            return fallback;
        string[] parts = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (index < 0 || index >= parts.Length)
            return fallback;
        return float.TryParse(parts[index], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(kh, PlayerUseKey) (sv_keyhunt.qc:1307): the +use key — if the player is carrying a
    /// key (<c>player.kh_next</c>), drop one (<see cref="DropOneKey"/> = kh_Key_DropOne) to pass it to a teammate,
    /// and consume the press (return true). Otherwise let the press fall through to other handlers.
    /// </summary>
    private bool OnUseKey(ref MutatorHooks.PlayerUseKeyArgs args)
    {
        if (args.Player is not Player player)
            return false;
        return DropOneKey(player); // true only if the player carried (and dropped) a key
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(kh, MakePlayerObserver) and MUTATOR_HOOKFUNCTION(kh, ClientDisconnect) — both call
    /// kh_Key_DropAll(player, true): a player who is demoted to spectator or who disconnects relinquishes every key
    /// they were carrying (as a suicide drop, so their own team can't instantly free-collect). Wired through the
    /// shared <see cref="GameType.OnPlayerRemoved"/> seam (ClientManager.PutObserverInServer + ClientDisconnect).
    /// </summary>
    public override void OnPlayerRemoved(Player player)
    {
        if (MatchEnded)
            return;
        DropAllKeys(player, suicide: true); // QC kh_Key_DropAll(player, true)
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>The key whose home team is <paramref name="team"/>, or null.</summary>
    public KeyState? KeyOf(int team) => Keys.TryGetValue(team, out var k) ? k : null;

    /// <summary>
    /// QC kh_Key_AssignTo (pickup/collect): <paramref name="player"/> picks up <paramref name="key"/>,
    /// becoming its carrier. Awards the collect SCORE to the player's team (QC kh_Scores_Event "collect").
    /// Then re-evaluates the all-keys-in-range capture geometry.
    ///
    /// Base-faithful: there is NO instant capture-on-pickup path in QC — kh_Key_AssignTo only assigns/attaches;
    /// the capture itself is decided by kh_Key_Think with the maxdist geometry. So this routes the capture check
    /// through <see cref="CheckCaptureGeometry"/> (the live, maxdist-gated path), NOT a no-maxdist shortcut.
    /// Returns the captured team color code if a capture just happened, else <see cref="Teams.None"/>.
    /// </summary>
    public int AssignKey(Player player, KeyState key)
    {
        if (MatchEnded || player.IsDead)
            return Teams.None;

        key.Carrier = player;
        key.DropTime = 0f;
        AddTeamScore(player.Team, (int)ScoreCollect);
        AddCol(player, "KH_PICKUPS", 1); // QC kh_Key_Collect: GameRules_scoring_add(player, KH_PICKUPS, 1)
        // Attach the world entity to the new carrier (QC kh_Key_Attach) when the entity layer is live.
        if (key.Entity is not null)
            GametypeEntities.AttachToCarrier(key.Entity, player, new Vector3(0f, 0f, KeyZShift));

        // QC: the capture is decided by the maxdist geometry in kh_Key_Think, not at pickup time.
        return CheckCaptureGeometry();
    }

    /// <summary>QC kh_Key_AssignTo(key, NULL) on drop/death: the key becomes loose (no carrier).</summary>
    public void DropKey(KeyState key) => key.Carrier = null;

    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        // QC kh_HandleFrags: killing a key-carrier adjusts the attacker's score (only when attacker != victim).
        int keysHeld = 0;
        foreach (var key in Keys.Values)
            if (ReferenceEquals(key.Carrier, victim)) keysHeld++;
        if (keysHeld > 0 && attacker is not null && !ReferenceEquals(attacker, victim))
        {
            if (Teams.SameTeam(attacker, victim))
            {
                // QC: team-kill of a carrier → -nk * score_collect penalty (nk = number of keys held).
                attacker.ScoreFrags -= keysHeld * (int)ScoreCollect; // QC kh_Scores_Event "carrierfrag" (negative)
            }
            else
            {
                // QC: enemy carrier-frag → (score_carrierfrag - 1); the implicit +1 normal frag is added by the engine.
                attacker.ScoreFrags += (int)ScoreCarrierFrag - 1; // QC kh_Scores_Event "carrierfrag"
                AddCol(attacker, "KH_KCKILLS", 1);                 // QC GameRules_scoring_add(attacker, KH_KCKILLS, 1)
            }
        }

        // QC PlayerDies: a suicide / world-death drop is marked (dropperteam set); an enemy kill is not.
        bool suicide = attacker is null || ReferenceEquals(attacker, victim);
        // The victim drops every key they were carrying (QC kh_Key_DropAll) — they become loose pickups.
        DropAllKeys(victim, suicide);

        // KH respawns normally; arm the respawn timer (QC calculate_respawntime).
        GametypeEntities.ScheduleRespawn(victim);
        return false;
    }

    /// <summary>QC <c>GameRules_scoring_add_team(player, SCORE, delta)</c>'s team side: add to a team's ST_SCORE
    /// total (GameScores team slot 0 — the team primary). No-op for the neutral team.</summary>
    public void AddTeamScore(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None)
            return;
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotScore, delta);
    }

    public int GetTeamScore(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotScore);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on team points (ST_SCORE),
    /// so a tied timed KeyHunt enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(Scoring.GameScores.LeaderTeam(), Scoring.GameScores.SecondTeam(), GetTeamScore);

    // ============================================================================================
    //  Controller + round loop (QC kh_Controller_Think / kh_WaitForPlayers / kh_StartRound)
    // ============================================================================================

    /// <summary>
    /// Advance the KeyHunt controller one step (QC kh_Controller_Think): wait until every team has a live
    /// player, then count a round down and spawn the keys; while a round runs, evaluate the capture geometry
    /// and auto-return any dropped key whose timer elapsed. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick()
    {
        if (MatchEnded)
            return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC kh_WaitForPlayers / kh_StartRound: if(time < game_starttime) → defer until the match clock has
        // started (prematch warmup gate). Same guard as Domination.cs and DamageSystem.cs.
        float gameStart = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;

        switch (Phase)
        {
            case RoundPhase.WaitingForPlayers:
                if (now < gameStart)
                    break; // QC: kh_Controller_SetThink(game_starttime - time + 0.1, kh_WaitForPlayers); return;
                if (!AnyTeamMissing())
                {
                    Phase = RoundPhase.Countdown;
                    RoundStartTime = now + DelayRound;
                    // QC kh_WaitForPlayers / kh_FinishRound: announce the round-start countdown to everyone.
                    CenterAll("KEYHUNT_ROUNDSTART", DelayRound);
                }
                break;

            case RoundPhase.Countdown:
                if (now < gameStart) { Phase = RoundPhase.WaitingForPlayers; break; } // QC: same early-return
                if (AnyTeamMissing()) { Phase = RoundPhase.WaitingForPlayers; break; }
                if (now >= RoundStartTime)
                    StartRound();
                break;

            case RoundPhase.InProgress:
                TickKeys(now);
                break;
        }
    }

    /// <summary>QC kh_GetMissingTeams: true if any active team has no live, non-chatting player.</summary>
    public bool AnyTeamMissing()
    {
        foreach (int team in Teams.Active(TeamCount))
        {
            bool has = false;
            for (int i = 0; i < _roster.Count; i++)
            {
                Player p = _roster[i];
                if ((int)p.Team == team && !p.IsDead) { has = true; break; }
            }
            if (!has)
                return true;
        }
        return false;
    }

    /// <summary>
    /// QC kh_StartRound: spawn one key per active team, each initially assigned to a random live player on
    /// that team (and positioned on them). Transitions the controller to <see cref="RoundPhase.InProgress"/>.
    /// </summary>
    public void StartRound()
    {
        if (AnyTeamMissing())
        {
            Phase = RoundPhase.WaitingForPlayers;
            return;
        }
        // Clear any leftover keys from a prior round.
        foreach (var k in Keys.Values)
        {
            if (k.Entity is not null && Api.Services is not null)
                Api.Entities.Remove(k.Entity);
            k.Entity = null;
            k.Carrier = null;
        }

        int teamCount = TeamCount;
        int keyIndex = 0;
        foreach (int team in Teams.Active(teamCount))
        {
            Player? owner = PickRandomLivePlayer(team);
            KeyState key = SpawnKey(team, owner);
            key.SpawnAngle = 360f * keyIndex / teamCount; // QC kh_Key_Spawn: key.cnt = 360*i/AVAILABLE_TEAMS
            keyIndex++;
            if (owner is not null)
            {
                AssignKeyNoScore(owner, key); // QC kh_Key_Spawn → kh_Key_AssignTo(key, initial_owner)
                // QC kh_Key_Spawn: tell the initial owner "You are starting with the X Key".
                NotificationSystem.Send(NotifBroadcast.One, owner, MsgType.Center,
                    $"KEYHUNT_START_{TeamSuffix(team)}");
            }
        }
        // QC kh_StartRound: the radar/waypoint tracking device starts OFF and powers up after delay_tracking. Only
        // if delay_tracking >= 0 is the "Scanning frequency range…" countdown shown and the reveal scheduled (a
        // negative delay_tracking means the trackers never show — QC gametypes-server.cfg note on the cvar).
        TrackingEnabled = false;
        _trackingEnableTime = 0f;
        float now0 = Api.Services is not null ? Api.Clock.Time : 0f;
        float delayTracking = DelayTracking;
        if (delayTracking >= 0f)
        {
            CenterAll("KEYHUNT_SCAN", delayTracking); // QC CENTER_KEYHUNT_SCAN delay_tracking
            _trackingEnableTime = now0 + delayTracking; // QC kh_Controller_SetThink(delay_tracking, kh_EnableTrackingDevice)
        }
        // Fresh round: clear the alarm + interfere-message state.
        _sirenTime = 0f;
        _interfereMsgTime = 0f;
        _allOwnedTeam = Teams.None;
        Phase = RoundPhase.InProgress;
        Round.Number++;
    }

    /// <summary>Round bookkeeping (reused from the round-based modes for the round counter).</summary>
    public readonly RoundState Round = new();

    private Player? PickRandomLivePlayer(int team)
    {
        // QC reservoir pick: random()*players <= 1. Deterministic via the shared spawn RNG analogue.
        Player? chosen = null;
        int seen = 0;
        for (int i = 0; i < _roster.Count; i++)
        {
            Player p = _roster[i];
            if ((int)p.Team != team || p.IsDead)
                continue;
            seen++;
            if (XonoticGodot.Common.Math.Prandom.Float() * seen <= 1f)
                chosen = p;
        }
        return chosen;
    }

    // ============================================================================================
    //  Key ENTITY layer (QC kh_Key_Spawn / kh_Key_Touch / kh_Key_Think / kh_Key_DropAll)
    // ============================================================================================

    /// <summary>
    /// QC kh_Key_Spawn: create the world key entity for <paramref name="team"/> (touch = collect, think =
    /// drop/return/capture geometry). When no facade is wired the <see cref="KeyState"/> still tracks the
    /// carrier so the headless capture logic works.
    /// </summary>
    public KeyState SpawnKey(int team, Player? initialOwner)
    {
        if (!Keys.TryGetValue(team, out KeyState? key))
        {
            key = new KeyState(team);
            Keys[team] = key;
        }
        Vector3 origin = initialOwner?.Origin ?? Vector3.Zero;
        Entity? e = GametypeEntities.SpawnObjective("item_kh_key", origin, team, KeyMins, KeyMaxs,
            touch: KeyTouchEntity, think: KeyThinkEntity);
        if (e is not null)
        {
            e.TakeDamage = DamageMode.Yes; // a loose key can be returned by damage (QC takedamage = DAMAGE_YES)
            e.DamageForceScale = DamageForcescale; // QC kh_Key_Detach: key.damageforcescale = g_balance_keyhunt_damageforcescale (loose key knockback)
            e.NextThink = GametypeEntities.Now + 0.05f; // QC kh_Key_Think rate
            // QC kh_Key_Spawn: event_damage = kh_Key_Damage; damagedbytriggers/contents = return_when_unreachable.
            // The port routes a non-player edict's damage through GtEventDamage, and a dropped key carries FL_ITEM
            // (IsPushable) so a trigger_hurt touch reaches it. Install the damage handler when return_when_unreachable.
            if (GametypeEntities.Cvar(CvarReturnWhenUnreachable, 1f) != 0f)
                e.GtEventDamage = (self, infl, atk, dt, dmg, loc, frc) => KeyDamage(self, atk, dt, frc);
            SetKeyVisual(e, team, carried: false);       // QC kh_Key_Spawn: model + team color + glow + spin
        }
        key.Entity = e;
        key.Carrier = null;
        return key;
    }

    /// <summary>
    /// QC kh_Key_Spawn's presentation block: give the key entity its model, fullbright glow, team colormap and
    /// per-team netname so the server's networked-entity stream renders it. A dropped key slowly spins; a carried
    /// key rides the carrier (no spin, swapped to the carried model). The model/animation playback is the client's.
    /// </summary>
    public void SetKeyVisual(Entity e, int team, bool carried)
    {
        if (Api.Services is not null) Api.Entities.SetModel(e, KeyModel);
        else e.Model = KeyModel;
        e.Effects |= EffectFlags.FullBright;     // QC KH_KEY_BRIGHTNESS glow
        e.Skin = TeamKeySkin(team);
        e.Team = team;                           // colormap = team palette (Team_ColorRGB × brightness)
        e.NetName = TeamKeyName(team);
        e.AVelocity = carried ? Vector3.Zero : KeySpin; // a loose key spins; a carried one is fixed to the carrier
    }

    /// <summary>QC the per-team key skin (the key.md3 carries a skin per team color).</summary>
    private static float TeamKeySkin(int team) => team switch
    {
        Teams.Red => 0f, Teams.Blue => 1f, Teams.Yellow => 2f, Teams.Pink => 3f, _ => 0f,
    };

    /// <summary>QC the per-team colored key name ("^1red key" / "^4blue key" / …).</summary>
    private static string TeamKeyName(int team) => team switch
    {
        Teams.Red => "^1red key", Teams.Blue => "^4blue key",
        Teams.Yellow => "^3yellow key", Teams.Pink => "^6pink key", _ => "^7key",
    };

    // ============================================================================================
    //  Audio + notifications (QC sound()/play2all + Send_Notification — all silent before this)
    // ============================================================================================

    /// <summary>QC <c>sound(emitter, CH_TRIGGER, snd, VOL_BASE, ATTEN_NORM)</c>: a KH cue heard near the emitter.</summary>
    private static void SoundOn(Entity? e, string snd)
    {
        if (e is not null && Api.Services is not null)
            SoundSystem.PlayOn(e, Sounds.ByName(snd));
    }

    /// <summary>QC <c>play2all(SND(...))</c>: a KH cue heard by everyone (capture jingle / destroy boom).</summary>
    private static void SoundGlobal(string snd)
    {
        if (Api.Services is not null)
            SoundSystem.PlayGlobal(Sounds.ByName(snd));
    }

    /// <summary>QC <c>Send_Notification(NOTIF_ALL, NULL, MSG_INFO, APP_TEAM_NUM(realteam, INFO_KEYHUNT_X), …)</c>.</summary>
    private static void InfoTeam(int realteam, string evt, params object[] args)
        => NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, $"KEYHUNT_{evt}_{TeamSuffix(realteam)}", args);

    /// <summary>QC <c>Send_Notification(NOTIF_ALL, NULL, MSG_CENTER, KH_X, …)</c> (teamless center-print).</summary>
    private static void CenterAll(string name, params object[] args)
        => NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, name, args);

    /// <summary>The <see cref="KeyState"/> bound to a world key <see cref="Entity"/>, or null.</summary>
    public KeyState? KeyForEntity(Entity e)
    {
        foreach (var k in Keys.Values)
            if (ReferenceEquals(k.Entity, e)) return k;
        return null;
    }

    /// <summary>
    /// QC kh_Key_AssignTo at round start (no collect score): give a key to its initial owner and attach the
    /// world entity to them. Distinct from <see cref="AssignKey"/> which awards the enemy-collect score.
    /// </summary>
    public void AssignKeyNoScore(Player player, KeyState key)
    {
        key.Carrier = player;
        key.DropTime = 0f;
        if (key.Entity is not null)
            GametypeEntities.AttachToCarrier(key.Entity, player, new Vector3(0f, 0f, KeyZShift));
    }

    /// <summary>
    /// QC kh_Key_Collect: a player picks up a loose key. If it isn't their own dropped key, award the collect
    /// score (handled by <see cref="AssignKey"/>); then attach it and re-check the capture. The dropper can't
    /// instantly re-collect their own key (delay_collect).
    /// </summary>
    public int CollectKey(Player player, KeyState key)
    {
        if (MatchEnded || player.IsDead || key.Carrier is not null)
            return Teams.None;
        // QC: a player can't re-collect a key they just dropped within delay_collect.
        if (ReferenceEquals(key.Dropper, player)
            && GametypeEntities.Now < key.DropTime + DelayCollect)
            return Teams.None;

        SoundOn(player, "KH_COLLECT"); // QC kh_Key_Collect: sound(player, CH_TRIGGER, SND_KH_COLLECT, …) — unconditional

        bool wasOwnDrop = key.DropperTeam == (int)player.Team;
        key.DropperTeam = Teams.None;
        key.Dropper = null;
        key.DropTime = 0f;
        key.Carrier = player;
        if (key.Entity is not null)
            GametypeEntities.AttachToCarrier(key.Entity, player, new Vector3(0f, 0f, KeyZShift));

        // Only an enemy collecting earns the collect score + the PICKUPS stat (QC kh_dropperteam != player.team).
        if (!wasOwnDrop)
        {
            AddTeamScore(player.Team, (int)ScoreCollect); // QC kh_Scores_Event "collect"
            AddCol(player, "KH_PICKUPS", 1); // QC kh_Key_Collect: GameRules_scoring_add(player, KH_PICKUPS, 1)
        }
        // QC kh_Key_Collect: "^BG%s^BG picked up the X Key" — keyed by the key's HOME team (realteam).
        InfoTeam(key.HomeTeam, "PICKUP", player.NetName);

        // QC: the actual capture is decided by kh_Key_Think with the maxdist geometry, not at pickup time.
        return CheckCaptureGeometry();
    }

    /// <summary>
    /// QC kh_Key_Damage (the key's event_damage): only a LOOSE key reacts. A NEEDKILL deathtype (lava/slime/
    /// trigger_hurt/void) fast-forwards the auto-return to delay_damage_return (bounded so it can only SHORTEN the
    /// timer, never extend it). Otherwise, a forceful hit from a player outside the protect window re-teams the key
    /// to the attacker's team (so the puncher's team can re-collect it without the dropper-team gate).
    /// </summary>
    private void KeyDamage(Entity self, Entity? attacker, string deathType, Vector3 force)
    {
        KeyState? key = KeyForEntity(self);
        if (key is null || key.Carrier is not null) // QC: if(this.owner) return;
            return;

        // QC ITEM_DAMAGE_NEEDKILL(deathtype): lava/slime/swamp/hurttrigger → return in delay_damage_return.
        string b = Damage.DeathTypes.BaseOf(deathType);
        bool needKill = b == Damage.DeathTypes.Void || b == Damage.DeathTypes.Slime
            || b == Damage.DeathTypes.Lava || b == Damage.DeathTypes.Swamp;
        if (needKill)
        {
            // QC: pain_finished = bound(time, time + delay_damage_return, pain_finished). In port terms the return
            // fires at DropTime + DelayReturn; shorten DropTime so the new return time = min(old, Now+damage_return).
            float now = GametypeEntities.Now;
            if (key.DropTime <= 0f) key.DropTime = now; // a never-dropped key (defensive) starts its clock now
            float oldReturn = key.DropTime + DelayReturn;
            float newReturn = System.Math.Min(oldReturn, now + DelayDamageReturn);
            newReturn = System.Math.Max(newReturn, now); // bound below by time
            key.DropTime = newReturn - DelayReturn;
            return;
        }

        // QC kh_Key_Damage: if(force == '0 0 0') return; — the handler checks the RAW force vector. The
        // damageforcescale (g_balance_keyhunt_damageforcescale) is an ENTITY property (key.damageforcescale,
        // set on the key in kh_Key_Detach) that the damage engine applies to the key's physical knockback in
        // DamageSystem.ApplyKnockback — NOT a scale applied to the force seen here. We set
        // e.DamageForceScale = DamageForcescale on drop (SpawnKey/DropAllKeys/DropOneKey); a carried key takes
        // no damage at all (AttachToCarrier sets TakeDamage=No, matching kh_Key_Attach's damageforcescale=0),
        // so the key's bbox-shoving knockback honors a tuned damageforcescale while this gate stays raw.
        if (force == Vector3.Zero) // QC: if(force == '0 0 0') return;
            return;
        // QC: if(time > this.pushltime) if(IS_PLAYER(attacker)) this.team = attacker.team. In Base this re-teams
        // the loose key to the puncher's team (the dropper-collect gate keys off kh_dropperteam, not home team).
        // Port analogue: re-stamp the dropper team so the puncher's team is no longer the gated dropper.
        if (GametypeEntities.Now > key.ProtectTime && attacker is Player ap)
            key.DropperTeam = (int)ap.Team;
    }

    /// <summary>Entity touch trampoline for a loose key: a live player collects it (QC kh_Key_Touch).</summary>
    private void KeyTouchEntity(Entity self, Entity other)
    {
        if (other is not Player p || p.IsDead)
            return;
        KeyState? key = KeyForEntity(self);
        if (key is not null && key.Carrier is null)
            CollectKey(p, key);
    }

    /// <summary>
    /// QC kh_Key_Think (per-key): a carried key keeps the carrier in sync and, if all keys are on one team,
    /// evaluates the in-range capture; a loose key auto-returns once its return timer elapses. Re-arms.
    /// </summary>
    private void KeyThinkEntity(Entity self)
    {
        self.NextThink = GametypeEntities.Now + 0.05f;
        KeyState? key = KeyForEntity(self);
        if (key is null)
            return;

        if (key.Carrier is null)
        {
            // dropped: auto-return after delay_return (QC pain_finished timeout → kh_LoserTeam → respawn)
            if (key.DropTime > 0f && GametypeEntities.Now > key.DropTime + DelayReturn)
            {
                AutoReturnKey(key);
                return;
            }
        }
        else
        {
            // QC kh_Key_Think (#ifndef KH_PLAYER_USE_ATTACHMENT — the LIVE Base path): a carried key circles its
            // carrier. makevectors('0 1 0' * (cnt + (time%360)*XYSPEED)); setorigin(v_forward*XYDIST + z*'0 0 1').
            OrbitCarriedKey(key);
        }
        // QC kh_Key_Think: deferred interfere/meet/help volley + the all-owned capture geometry.
        UpdateInterfereMessages(GametypeEntities.Now);
        CheckCaptureGeometry();
    }

    /// <summary>
    /// QC kh_Key_Think carried-key orbit (the LIVE #ifndef KH_PLAYER_USE_ATTACHMENT path): place the carried key
    /// circling the carrier — yaw = key.cnt + (time%360)*KH_KEY_XYSPEED, origin = carrier + v_forward*KH_KEY_XYDIST
    /// at height KH_KEY_ZSHIFT. (Base keeps the z from the attach and only rewrites x,y each think; the net effect
    /// is the carrier origin + a radius-24 ring at z = KH_KEY_ZSHIFT.)
    /// </summary>
    private void OrbitCarriedKey(KeyState key)
    {
        if (key.Carrier is not { } carrier || key.Entity is not Entity e)
            return;
        float yaw = key.SpawnAngle + (GametypeEntities.Now % 360f) * KeyXySpeed;
        XonoticGodot.Common.Math.QMath.AngleVectors(new Vector3(0f, yaw, 0f), out Vector3 fwd, out _, out _);
        Vector3 pos = carrier.Origin + fwd * KeyXyDist + new Vector3(0f, 0f, KeyZShift);
        GametypeEntities.SetOrigin(e, pos);
    }

    private void TickKeys(float now)
    {
        // QC kh_EnableTrackingDevice (scheduled at kh_StartRound for delay_tracking seconds): once the device
        // powers up, the dropped-key + carrier waypoint sprites become visible (the sprites themselves are deferred).
        if (!TrackingEnabled && _trackingEnableTime > 0f && now >= _trackingEnableTime)
        {
            TrackingEnabled = true;
            _trackingEnableTime = 0f;
        }
        // Headless think: auto-return loose keys, then evaluate the in-range capture.
        foreach (var key in Keys.Values)
            if (key.Carrier is null && key.DropTime > 0f && now > key.DropTime + DelayReturn)
                AutoReturnKey(key);
        UpdateInterfereMessages(now);
        CheckCaptureGeometry();
    }

    /// <summary>QC kh_Key_DropAll: a dying player drops every key they carry; each becomes a loose pickup.
    /// <paramref name="suicide"/> (QC) marks the dropper team so the dropper's own team can't free-collect.</summary>
    public void DropAllKeys(Player player, bool suicide = true)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        // QC kh_Key_DropAll: if the dropping carrier was recently PUSHED (a pusher within their pushltime window —
        // set by teleporters / jumppads / the +use shove / push damage on Player.Pusher/PushLTime), record that
        // pusher on each dropped key so a subsequent timeout-return can credit them a "push" (kh_LoserTeam).
        Player? mypusher = (player.Pusher is Player pu && now < player.PushLTime) ? pu : null;
        bool droppedAny = false;
        foreach (var key in Keys.Values)
        {
            if (!ReferenceEquals(key.Carrier, player))
                continue;
            key.Carrier = null;
            key.Dropper = player;
            // QC: only a suicide marks kh_dropperteam (a kill leaves it 0 so any teammate can re-collect freely).
            key.DropperTeam = suicide ? (int)player.Team : Teams.None;
            key.DropTime = now;
            key.Pusher = mypusher;               // QC key.pusher = mypusher (credited if the key times out in-window)
            key.ProtectTime = now + ProtectTime; // QC pushltime: pusher-credit window
            AddCol(player, "KH_LOSSES", 1); // QC kh_Key_DropAll: GameRules_scoring_add(player, KH_LOSSES, 1)
            // QC kh_Key_DropAll: "^BG%s^BG lost the X Key" (keyed by the key's HOME team).
            InfoTeam(key.HomeTeam, "LOST", player.NetName);
            droppedAny = true;
            if (key.Entity is Entity e)
            {
                GametypeEntities.DetachFromCarrier(e);
                e.Solid = Solid.Trigger;
                e.MoveType = MoveType.Toss;
                e.TakeDamage = DamageMode.Yes;
                e.DamageForceScale = DamageForcescale; // QC kh_Key_Detach: key.damageforcescale = autocvar_g_balance_keyhunt_damageforcescale
                GametypeEntities.SetOrigin(e, player.Origin);
                // QC: fling the key out — makevectors('-1 0 0'*(45+45*random()) + '0 360 0'*random()), dropvelocity*v_forward.
                float pitch = -(45f + 45f * XonoticGodot.Common.Math.Prandom.Float());
                float yaw = 360f * XonoticGodot.Common.Math.Prandom.Float();
                XonoticGodot.Common.Math.QMath.AngleVectors(new Vector3(pitch, yaw, 0f), out Vector3 fwd, out _, out _);
                e.Velocity = player.Velocity + fwd * DropVelocity; // QC W_CalculateProjectileVelocity adds the thrower's velocity
                SetKeyVisual(e, key.HomeTeam, carried: false); // dropped: restore the spin + dropped model
            }
        }
        if (droppedAny)
            SoundOn(player, "KH_DROP"); // QC kh_Key_DropAll: one SND_KH_DROP after dropping all keys
    }

    /// <summary>
    /// QC kh_Key_DropOne (PlayerUseKey): a carrier voluntarily drops ONE key (to pass it to a teammate). The key
    /// is thrown forward at throwvelocity, the dropper is gated from instant re-collect (delay_collect), and the
    /// drop sound + DROP info line fire. Returns true if a key was dropped. Call from the +use mutator hook.
    /// </summary>
    public bool DropOneKey(Player player)
    {
        if (MatchEnded || player.IsDead)
            return false;
        // Find one key this player carries (QC player.kh_next — the first in their carry list).
        KeyState? key = null;
        foreach (var k in Keys.Values)
            if (ReferenceEquals(k.Carrier, player)) { key = k; break; }
        if (key is null)
            return false;

        float now = GametypeEntities.Now;
        key.Carrier = null;
        key.Dropper = player;
        key.DropTime = now;            // QC key.kh_droptime = time (re-collect gate baseline)
        key.DropperTeam = (int)player.Team; // QC key.kh_dropperteam = key.team (own-team can't free-collect)
        key.ProtectTime = now + ProtectTime;
        key.Pusher = null;
        AddCol(player, "KH_LOSSES", 1); // QC kh_Key_DropOne: GameRules_scoring_add(player, KH_LOSSES, 1)
        // QC kh_Key_DropOne: "^BG%s^BG dropped the X Key" (keyed by the key's HOME team).
        InfoTeam(key.HomeTeam, "DROP", player.NetName);

        if (key.Entity is Entity e)
        {
            GametypeEntities.DetachFromCarrier(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.Toss;
            e.TakeDamage = DamageMode.Yes;
            e.DamageForceScale = DamageForcescale; // QC kh_Key_Detach: key.damageforcescale = autocvar_g_balance_keyhunt_damageforcescale
            GametypeEntities.SetOrigin(e, player.Origin);
            // QC: makevectors(player.v_angle); throwvelocity * v_forward (+ thrower velocity).
            XonoticGodot.Common.Math.QMath.AngleVectors(player.Angles, out Vector3 fwd, out _, out _);
            e.Velocity = player.Velocity + fwd * ThrowVelocity;
            SetKeyVisual(e, key.HomeTeam, carried: false);
        }
        SoundOn(player, "KH_DROP"); // QC sound(player, CH_TRIGGER, SND_KH_DROP, …)
        return true;
    }

    /// <summary>QC kh_Key_Think timeout (pain_finished elapsed) → kh_LoserTeam: a key left loose too long is
    /// "destroyed" — score the destruction (split among the other teams), notify + boom, then end the round.</summary>
    public void AutoReturnKey(KeyState key)
    {
        LoserTeam(key.HomeTeam, key);
    }

    /// <summary>
    /// QC kh_LoserTeam: a key was lost (timed out / pushed off-map). If a pusher (recorded on the key at drop
    /// time, when its carrier was pushed within their pushltime window) on another team owns the loss, they get
    /// score_push + KH_PUSHES (a "push"); otherwise the key is destroyed and score_destroyed is split (QC
    /// DistributeEvenly) across the other teams' players AND each non-loser key holder (the
    /// score_destroyed_ownfactor "destroyed_holdingkey" chunk), the previous owner takes a KH_DESTRUCTIONS mark,
    /// and a tar boom + loss notify fire. Then the round ends (QC kh_FinishRound).
    /// </summary>
    public void LoserTeam(int loserTeam, KeyState lostKey)
    {
        // QC kh_LoserTeam: if(lostkey.pusher) if(lostkey.pusher.team != loser_team) if(IS_PLAYER(lostkey.pusher)).
        // The push-attribution WINDOW (pushltime / ProtectTime) is enforced at DROP time in DropAllKeys (only a
        // recent pusher is recorded onto the key); kh_LoserTeam itself does NOT re-gate on the window, so a key
        // that times out long after the protect window still credits the pusher it carries.
        Player? pusher = null;
        if (lostKey.Pusher is { } pu && (int)pu.Team != loserTeam && !pu.IsDead)
            pusher = pu;

        Vector3 boomOrigin = lostKey.Entity?.Origin ?? lostKey.Dropper?.Origin ?? Vector3.Zero;
        int realteam = lostKey.HomeTeam;
        Player? prevOwner = lostKey.Dropper;

        if (pusher is not null)
        {
            // QC: the pusher is credited a push (the previous owner's -score_push is logged, not deducted).
            pusher.ScoreFrags += (int)ScorePush; // QC kh_Scores_Event "push"
            AddCol(pusher, "KH_PUSHES", 1);
            CenterAll($"ROUND_TEAM_LOSS_{TeamSuffix(loserTeam)}");
            InfoTeam(realteam, "PUSHED", pusher.NetName, prevOwner?.NetName ?? "");
        }
        else
        {
            // QC kh_LoserTeam destruction branch: score_destroyed is shared via DistributeEvenly across BOTH the
            // other teams' players AND each non-loser key holder (the "destroyed_holdingkey" own-factor chunk).
            if (prevOwner is not null)
                AddCol(prevOwner, "KH_DESTRUCTIONS", 1); // QC GameRules_scoring_add(previous_owner, KH_DESTRUCTIONS, 1)

            int of = (int)ScoreDestroyedOwnFactor; // QC g_balance_keyhunt_score_destroyed_ownfactor (default 1)

            // QC: players = FOREACH_CLIENT(IS_PLAYER && team != loser_team) → count of all non-loser players.
            int players = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                Player p = _roster[i];
                if (!p.IsDead && (int)p.Team != loserTeam && (int)p.Team != Teams.None)
                    players++;
            }

            // QC: keys = FOR_EACH_KH_KEY(key.owner && key.team != loser_team) → non-loser key holders right now.
            int keys = 0;
            foreach (var k in Keys.Values)
                if (k.Carrier is { } kc && (int)kc.Team != loserTeam)
                    keys++;

            // QC: DistributeEvenly_Init(score_destroyed, keys*of + players).
            var dist = new EvenDistributor((int)ScoreDestroyed, keys * of + players);

            // QC: each non-loser key holder gets DistributeEvenly_Get(of) as a "destroyed_holdingkey" chunk
            // (credited to their INDIVIDUAL SP_SCORE via the float2int team:1 side — and so the team too).
            foreach (var k in Keys.Values)
                if (k.Carrier is { } kc && (int)kc.Team != loserTeam)
                {
                    int f = dist.Get(of);
                    kc.ScoreFrags += f;          // QC kh_Scores_Event "destroyed_holdingkey" (individual SP_SCORE)
                    AddTeamScore(kc.Team, f);    // ... AND the team ST_SCORE
                }

            // QC: fragsleft = DistributeEvenly_Get(players); then re-distribute that across the other teams' players.
            int fragsleft = dist.Get(players);
            int j = TeamCount - 1;
            foreach (int t in Teams.Active(TeamCount))
            {
                if (t == loserTeam)
                    continue; // QC: skip the loser team
                int teamPlayers = 0;
                for (int i = 0; i < _roster.Count; i++)
                {
                    Player p = _roster[i];
                    if (!p.IsDead && (int)p.Team == t) teamPlayers++;
                }
                // QC: DistributeEvenly_Init(fragsleft, j); fragsleft = DistributeEvenly_Get(j-1) (the OTHER teams'
                // carry-forward, taken FIRST); then DistributeEvenly_Get(1) is THIS team's share, re-split among
                // its players. Order matters: the j-1 carry is drawn before this team's last share.
                var teamDist = new EvenDistributor(fragsleft, System.Math.Max(1, j));
                fragsleft = teamDist.Get(System.Math.Max(0, j - 1));
                int thisTeamShare = teamDist.Get(1);
                var playerDist = new EvenDistributor(thisTeamShare, System.Math.Max(1, teamPlayers));
                for (int i = 0; i < _roster.Count; i++)
                {
                    Player p = _roster[i];
                    if (p.IsDead || (int)p.Team != t) continue;
                    int f = playerDist.Get(1);
                    p.ScoreFrags += f;        // QC kh_Scores_Event "destroyed" (individual SP_SCORE)
                    AddTeamScore(t, f);       // ... AND the team ST_SCORE
                }
                --j;
            }
            CenterAll($"ROUND_TEAM_LOSS_{TeamSuffix(loserTeam)}");
            InfoTeam(realteam, "DESTROYED", prevOwner?.NetName ?? "");
        }

        SoundGlobal("KH_DESTROY");        // QC play2all(SND(KH_DESTROY))
        if (Api.Services is not null)
            EffectEmitter.TeTarExplosion(boomOrigin); // QC te_tarexplosion(lostkey.origin)

        // remove the key + end the round (QC kh_FinishRound). The waypoint sprites are transient (rebuilt each
        // tick by CollectWaypoints from live key state), so clearing the entity/carrier state drops them.
        key_RemoveAll();
        UpdateLeaderAndCheckLimit();
        Phase = RoundPhase.WaitingForPlayers;
    }

    /// <summary>Remove every key entity from the field and clear carrier/drop state (QC kh_FinishRound loop).</summary>
    private void key_RemoveAll()
    {
        _interfereMsgTime = 0f;
        _allOwnedTeam = Teams.None;
        foreach (var key in Keys.Values)
        {
            key.Carrier = null;
            key.Dropper = null;
            key.DropperTeam = Teams.None;
            key.DropTime = 0f;
            if (key.Entity is not null && Api.Services is not null)
                Api.Entities.Remove(key.Entity);
            key.Entity = null;
        }
    }

    /// <summary>
    /// QC kh_Key_Think → kh_WinnerTeam: a capture requires every key carried by the SAME team AND all those
    /// carriers within <see cref="MaxDist"/> of each other. When satisfied, score the capture (distributed
    /// like QC DistributeEvenly) and start the next round. Returns the capturing team or <see cref="Teams.None"/>.
    /// </summary>
    public int CheckCaptureGeometry()
    {
        if (MatchEnded || Keys.Count == 0)
            return Teams.None;

        int owner = Teams.None;
        Player? anchor = null;
        foreach (var key in Keys.Values)
        {
            Player? c = key.Carrier;
            if (c is null || c.IsDead)
                return Teams.None; // a key is loose → no capture
            int t = (int)c.Team;
            if (owner == Teams.None) { owner = t; anchor = c; }
            else if (owner != t) return Teams.None; // split across teams
        }
        if (owner == Teams.None || anchor is null)
            return Teams.None;

        // QC kh_Key_Think: all keys are on one team → sound the alarm every 2.5s on the carriers.
        float now = GametypeEntities.Now;
        if (_sirenTime < now)
        {
            SoundOn(anchor, "KH_ALARM"); // QC sound(this.owner, CH_TRIGGER, SND_KH_ALARM, …)
            _sirenTime = now + SirenPeriod;
        }

        // QC maxdist: every carrier must be within maxdist of the anchor (the first key's carrier).
        float maxDist = MaxDist;
        if (maxDist > 0f)
        {
            foreach (var key in Keys.Values)
            {
                Player c = key.Carrier!;
                if (Vector3.Distance(c.Origin, anchor.Origin) > maxDist)
                    return Teams.None; // carriers too far apart → no capture yet
            }
        }

        WinnerTeam(owner);
        return owner;
    }

    /// <summary>
    /// QC kh_Key_AllOwnedByWhichTeam: the team that owns ALL keys (every key carried, all carriers on the
    /// same team), or <see cref="Teams.None"/> if no team fully owns them yet.
    /// </summary>
    public int AllOwnedByWhichTeam()
    {
        if (Keys.Count == 0)
            return Teams.None;
        int team = Teams.None;
        foreach (var key in Keys.Values)
        {
            Player? c = key.Carrier;
            if (c is null || c.IsDead)
                return Teams.None;
            int t = (int)c.Team;
            if (team == Teams.None) team = t;
            else if (team != t) return Teams.None;
        }
        return team;
    }

    /// <summary>
    /// QC kh_WinnerTeam → kh_FinishRound: a team has gathered all keys in range. Credit the capture score +
    /// caps, fan out the win center-print + capture info line, fire the capture VFX/jingle, then reset the keys
    /// and arm the next round.
    /// </summary>
    private void WinnerTeam(int owner)
    {
        // QC kh_WinnerTeam: DistributeEvenly_Init((AVAILABLE_TEAMS-1)*score_capture, AVAILABLE_TEAMS), then for
        // EACH key kh_Scores_Event(key.owner, "capture", DistributeEvenly_Get(1)). kh_Scores_Event routes that
        // chunk through GameRules_scoring_add_team_float2int(player, SCORE, f, ..., team:1), which credits BOTH
        // the carrier's INDIVIDUAL SP_SCORE (scoreboard rank) AND the team ST_SCORE. So: the team total = sum of
        // the chunks, and each carrier individually banks their chunk + a nade bonus + a KH_CAPS.
        int teams = TeamCount;
        int total = (teams - 1) * (int)ScoreCapture;
        if (total <= 0) total = (int)ScoreCapture; // 2-team fallback (kept from the prior port behavior)
        var dist = new EvenDistributor(total, System.Math.Max(1, teams));
        float nadeBonusHigh = GametypeEntities.Cvar("g_nades_bonus_score_high", 15f);
        foreach (var key in Keys.Values)
        {
            if (key.Carrier is not { } c || (int)c.Team != owner)
                continue;
            int chunk = dist.Get(1);
            c.ScoreFrags += chunk;             // QC: the carrier's INDIVIDUAL SP_SCORE (the float2int team:1 side)
            AddTeamScore(owner, chunk);        // QC: ... AND the team ST_SCORE (the team:1 second side)
            if (Api.Services is not null)
                Nades.NadeBonus.GiveBonus(c, nadeBonusHigh); // QC nades_GiveBonus(key.owner, g_nades_bonus_score_high)
        }
        CreditCapture(owner); // QC GameRules_scoring_add_team(key.owner, KH_CAPS, 1)

        // QC kh_WinnerTeam: the winning team's name in the center, and "X captured the keys" in the kill-feed.
        string keyowner = "";
        foreach (var key in Keys.Values)
            if (key.Carrier is { } c && (int)c.Team == owner) { keyowner = c.NetName; break; }
        CenterAll($"ROUND_TEAM_WIN_{TeamSuffix(owner)}");
        InfoTeam(owner, "CAPTURE", keyowner);

        // QC capture VFX: a plasma trail linking the carriers + a flash at their midpoint.
        CaptureVfx(owner);
        SoundGlobal("KH_CAPTURE"); // QC play2all(SND(KH_CAPTURE))

        UpdateLeaderAndCheckLimit();

        // reset keys + arm the next round (QC kh_FinishRound)
        _interfereMsgTime = 0f;
        _allOwnedTeam = Teams.None;
        foreach (var key in Keys.Values)
        {
            key.Carrier = null;
            key.DropTime = 0f;
            if (key.Entity is not null && Api.Services is not null)
                Api.Entities.Remove(key.Entity);
            key.Entity = null;
        }
        Phase = RoundPhase.WaitingForPlayers;
    }

    /// <summary>QC kh_WinnerTeam VFX: a NEXUIZPLASMA trail chaining the key carriers + a customflash midpoint.</summary>
    private void CaptureVfx(int owner)
    {
        if (Api.Services is null)
            return;
        Vector3 firstOrigin = Vector3.Zero, lastOrigin = Vector3.Zero, midpoint = Vector3.Zero;
        bool first = true;
        int n = 0;
        foreach (var key in Keys.Values)
        {
            if (key.Carrier is not { } c)
                continue;
            Vector3 o = c.Origin;
            midpoint += o;
            n++;
            if (!first)
                EffectEmitter.EmitTrail(Effects.ByName("TR_NEXUIZPLASMA"), lastOrigin, o); // QC Send_Effect(EFFECT_TR_NEXUIZPLASMA, …)
            else
                firstOrigin = o;
            lastOrigin = o;
            first = false;
        }
        if (TeamCount > 2 && n > 1) // QC: close the loop for 3-4 team games
            EffectEmitter.EmitTrail(Effects.ByName("TR_NEXUIZPLASMA"), lastOrigin, firstOrigin);
        if (n > 0)
            EffectEmitter.Emit("TE_EXPLOSION", midpoint * (1f / n)); // QC te_customflash(midpoint, …) → flash at the meeting point
    }

    /// <summary>
    /// QC kh_Key_AssignTo: when the all-keys-owned state changes, schedule (time+0.2) the INTERFERE/MEET/HELP
    /// center-prints, and fire them once the delay elapses (QC kh_Key_Think interferemsg block). Call each think.
    /// (The carrier waypoint-sprite "Run here"/"Key Carrier" flip is computed fresh each tick in CollectWaypoints
    /// from the live all-owned state, so no explicit sprite swap is needed here.)
    /// </summary>
    private void UpdateInterfereMessages(float now)
    {
        int ownerTeam = AllOwnedByWhichTeam();
        if (ownerTeam != _allOwnedTeam)
        {
            _allOwnedTeam = ownerTeam;
            if (ownerTeam != Teams.None)
            {
                _interfereMsgTime = now + 0.2f; // QC kh_interferemsg_time = time + 0.2
                _interfereMsgTeam = ownerTeam;
            }
            else
            {
                _interfereMsgTime = 0f;
            }
        }

        if (_interfereMsgTime != 0f && now > _interfereMsgTime)
        {
            _interfereMsgTime = 0f;
            int owningTeam = _interfereMsgTeam;
            for (int i = 0; i < _roster.Count; i++)
            {
                Player p = _roster[i];
                if (p.IsDead)
                    continue;
                if ((int)p.Team == owningTeam)
                {
                    // QC: a carrier on the owning team gets MEET (rendezvous), everyone else on it gets HELP.
                    bool carrying = false;
                    foreach (var key in Keys.Values)
                        if (ReferenceEquals(key.Carrier, p)) { carrying = true; break; }
                    NotificationSystem.Send(NotifBroadcast.One, p, MsgType.Center,
                        carrying ? "KEYHUNT_MEET" : "KEYHUNT_HELP");
                }
                else
                {
                    // QC: other teams get the team-coloured INTERFERE warning.
                    NotificationSystem.Send(NotifBroadcast.One, p, MsgType.Center,
                        $"KEYHUNT_INTERFERE_{TeamSuffix(owningTeam)}");
                }
            }
        }
    }

    // ============================================================================================
    //  Waypoint sprites (QC kh_Key_Spawn / kh_Key_AssignTo state machine)
    // ============================================================================================

    /// <summary>
    /// QC kh_Key_Spawn (WP_KeyDropped) + kh_Key_AssignTo (WP_KeyCarrier{Red,Blue,Yellow,Pink} / WP_KeyCarrierFinish):
    /// rebuild every key's waypoint sprite from live state each tick (the same transient-pull pattern CTF/Keepaway/
    /// Domination use — the sprites are NOT registered in the persistent WaypointSprites manager). A loose key gets
    /// the WP_KeyDropped marker on the key entity; a carried key gets the team-colored carrier marker riding the
    /// carrier — flipped to WP_KeyCarrierFinish ("Run here") when one team owns ALL keys (QC kh_Key_AssignTo).
    /// The per-viewer visibility predicates mirror kh_Key_waypointsprite_visible_for_player and
    /// kh_KeyCarrier_waypointsprite_visible_for_player (tracking-device gate + warmup/spec/team/invisibility rules).
    /// </summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        if (Phase != RoundPhase.InProgress)
            return;
        int allOwnedTeam = AllOwnedByWhichTeam();
        foreach (var key in Keys.Values)
        {
            if (key.Carrier is { } carrier)
            {
                // QC kh_Key_AssignTo (sv_keyhunt.qc:360-367,388-411): WaypointSprite_AttachCarrier on the carrier,
                // SPRITERULE_TEAMPLAY three-image — ENEMY (model1) + SPECTATOR (model3) see the team-colored
                // WP_KeyCarrier{Red,Blue,Yellow,Pink}; the carrier's OWN team (model2) sees WP_KeyCarrierFriend
                // ("Key carrier", green), flipped to WP_KeyCarrierFinish ("Run here") for the own team ONLY when one
                // team owns ALL keys. Per-frame visibility predicate (kh_KeyCarrier_waypointsprite_visible_for_player).
                string teamSprite = CarrierSpriteForTeam((int)carrier.Team); // model1/model3 (enemy + spectator)
                string ownSprite = allOwnedTeam == (int)carrier.Team
                    ? "KeyCarrierFinish"                         // QC model2 when all keys owned → "Run here"
                    : "KeyCarrierFriend";                        // QC model2 normally → green "Key carrier"
                Waypoints.WaypointDef cdef = Waypoints.WaypointRegistry.Get(teamSprite);
                Player held = carrier;
                into.Add(new Waypoints.WaypointSprite
                {
                    SpriteName = teamSprite,         // model1 (enemy)
                    SpriteNameOwn = ownSprite,       // model2 (own team)
                    SpriteNameSpec = teamSprite,     // model3 (spectator)
                    Owner = carrier,
                    // QC kh_Key_AssignTo: WaypointSprite_AttachCarrier rides the carrier at the standard head
                    // offset ('0 0 64'), like every other carrier sprite (CTF/Keepaway); the KH_KEY_WP_ZSHIFT (20)
                    // offset is the DROPPED-key marker's, not the carrier's.
                    Offset = new Vector3(0f, 0f, 64f),
                    Team = key.HomeTeam,                       // radar tint keyed on the key's home team
                    Color = cdef.Color,
                    RadarIcon = cdef.RadarIcon,
                    Rule = Waypoints.SpriteRule.Teamplay,      // QC WaypointSprite_UpdateRule(..., SPRITERULE_TEAMPLAY)
                    VisibleForPlayer = viewer => CarrierWaypointVisibleFor(held, viewer),
                    Health = -1f,
                });
            }
            else if (key.Entity is { } e)
            {
                // QC kh_Key_Spawn: WaypointSprite_Spawn(WP_KeyDropped, ..., key, '0 0 1'*KH_KEY_WP_ZSHIFT, ...,
                // key.team, ...) with kh_Key_waypointsprite_visible_for_player.
                Waypoints.WaypointDef ddef = Waypoints.WaypointRegistry.Get("KeyDropped");
                into.Add(new Waypoints.WaypointSprite
                {
                    SpriteName = "KeyDropped",
                    Owner = e,
                    Offset = new Vector3(0f, 0f, KeyWpZShift),
                    Team = key.HomeTeam,
                    Color = ddef.Color,
                    RadarIcon = ddef.RadarIcon,
                    Rule = Waypoints.SpriteRule.Default,
                    VisibleForPlayer = DroppedKeyWaypointVisibleFor,
                    Health = -1f,
                });
            }
        }
    }

    /// <summary>QC kh_Key_waypointsprite_visible_for_player (sv_keyhunt.qc:115): a dropped-key marker is always
    /// shown to spectators/during warmup; otherwise it requires the tracking device to be enabled.</summary>
    private bool DroppedKeyWaypointVisibleFor(Player? viewer)
    {
        // QC: if(this.owner && !this.owner.owner && (IS_SPEC(player) || warmup_stage)) return true;
        if (viewer is null || viewer.IsObserver || NotificationSystem.WarmupStage)
            return true;
        // QC: if(!kh_tracking_enabled) return false; return !this.owner || !this.owner.owner; (key is loose here).
        return TrackingEnabled;
    }

    /// <summary>QC kh_KeyCarrier_waypointsprite_visible_for_player (sv_keyhunt.qc:103): the carrier marker is shown
    /// to spectators / during warmup / to the carrier's own team always; hidden if the carrier is invisible;
    /// otherwise it requires the tracking device.</summary>
    private bool CarrierWaypointVisibleFor(Player carrier, Player? viewer)
    {
        // QC: if(IS_SPEC(player) || warmup_stage || SAME_TEAM(player, this.owner)) return true;
        if (viewer is null || viewer.IsObserver || NotificationSystem.WarmupStage || Teams.SameTeam(viewer, carrier))
            return true;
        // QC: if(IS_INVISIBLE(this.owner)) return false;
        var invisEffect = StatusEffectsCatalog.ByName("invisibility");
        if (invisEffect is not null && StatusEffectsCatalog.Has(carrier, invisEffect))
            return false;
        // QC: return kh_tracking_enabled;
        return TrackingEnabled;
    }

    /// <summary>QC kh_Key_AssignTo sprite selection: the team-colored WP_KeyCarrier* def name.</summary>
    private static string CarrierSpriteForTeam(int team) => team switch
    {
        Teams.Red => "KeyCarrierRed",
        Teams.Blue => "KeyCarrierBlue",
        Teams.Yellow => "KeyCarrierYellow",
        Teams.Pink => "KeyCarrierPink",
        _ => "KeyCarrierRed",
    };

    // ============================================================================================
    //  HUD stat pack (QC kh_update_state → STAT(OBJECTIVE_STATUS))
    // ============================================================================================

    /// <summary>
    /// QC <c>kh_update_state</c> (sv_keyhunt.qc:126-148): pack one 5-bit slot per key at bits [5i..5i+4]
    /// (key i = the i-th team's key in red,blue,yellow,pink order — QC <c>key.count</c>). Slot values:
    /// 0 = no such key in play (also the whole state between rounds, when every key is removed);
    /// 30 = dropped (QC "no owner"); 31 = carried by <paramref name="viewer"/> (QC's per-recipient
    /// <c>STAT |= 31</c> self override — this makes the pack PERSONALIZED, so the server serializes it per
    /// peer); otherwise the CARRIER's team as (team index + 1), i.e. 2=red .. 5=pink.
    ///
    /// DELIBERATE wire deviation: QC writes the SVQC team code (<c>f = key.team</c>, the carrier's — 5/14/13/10)
    /// and the CSQC decode's −1 bridges to the NUM_TEAM codes (cl_keyhunt.qc:25); the port's panel decode
    /// (ModIconsPanel.DrawKeyhunt, the same <c>((s &gt;&gt; (5i)) &amp; 31) − 1</c> expression) expects 1..4 team
    /// INDICES, so we write index+1 — observationally identical icons, one index convention on the wire.
    /// </summary>
    public uint PackKeyState(Player? viewer)
    {
        if (Phase != RoundPhase.InProgress)
            return 0u; // between rounds kh_Key_Remove deletes every key → QC state 0

        uint s = 0;
        for (int i = 0; i < Teams.All.Length; i++)
        {
            if (!Keys.TryGetValue(Teams.All[i], out KeyState? key))
                continue; // no key for that team (slot 0) — only the first TeamCount teams have keys
            uint f;
            if (key.Carrier is { } c)
                f = ReferenceEquals(c, viewer) ? 31u : (uint)(TeamIndex((int)c.Team) + 1); // QC f = key.team (the CARRIER's)
            else if (key.Entity is not null)
                f = 30u; // QC: key exists with no owner → dropped
            else
                continue; // out of play (returned/removed) → slot 0
            s |= f << (5 * i);
        }
        return s;
    }

    /// <summary>Team color code → 1-based team index (1 red, 2 blue, 3 yellow, 4 pink; 0 unknown).</summary>
    private static int TeamIndex(int team) => team switch
    {
        Teams.Red => 1, Teams.Blue => 2, Teams.Yellow => 3, Teams.Pink => 4, _ => 0,
    };

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: KH teams rank by the team primary slot ST_SCORE, then ST_KH_CAPS. LeaderTeam / SecondTeam read the
        // flag-aware two-slot compare from GameScores (the source of truth). The point-limit check uses ST_SCORE.
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamScore(bestTeam);
        float pointLimit = PointLimit;
        if (pointLimit > 0f && bestScore >= pointLimit)
            MatchEnded = true;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamScore(secondTeam)) >= leadLimit)
            MatchEnded = true;
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

    /// <summary>
    /// Port of QC <c>DistributeEvenly_Init</c>/<c>DistributeEvenly_Get</c> (common/util.qc): hand out a fixed
    /// integer <c>total</c> in <c>n</c> roughly-equal chunks whose sum is EXACTLY the total (no rounding loss).
    /// <c>Get(k)</c> takes the next <c>k</c> shares' worth; the remainder rides forward to the next call.
    /// </summary>
    private struct EvenDistributor
    {
        private int _amount;
        private int _n;
        public EvenDistributor(int total, int n) { _amount = total; _n = n; }
        public int Get(int k)
        {
            if (_n <= 0) return 0;
            // QC: f = floor(amount * k / n + 0.5); amount -= f; n -= k.
            int f = (int)System.Math.Floor((double)_amount * k / _n + 0.5);
            _amount -= f;
            _n -= k;
            return f;
        }
    }
}

/// <summary>
/// One Key Hunt key — the Godot-free essence of the QC key edict (one per team; .owner carrier,
/// .kh_dropperteam, the worldkey list). Tracks the key's home team, current carrier, drop bookkeeping, and
/// — when a facade is wired — the world <see cref="Entity"/> that physically represents it. The key model,
/// attachment tag, and CSQC networking remain client concerns.
/// </summary>
public sealed class KeyState
{
    /// <summary>The team this key belongs to (a <see cref="Teams"/> color code).</summary>
    public readonly int HomeTeam;

    /// <summary>The player currently carrying the key (QC key.owner), or null when loose.</summary>
    public Player? Carrier;

    /// <summary>The world entity representing this key (QC the item_kh_key edict), or null (headless).</summary>
    public Entity? Entity;

    /// <summary>The player who last dropped this key (QC key.enemy), gating quick re-collect.</summary>
    public Player? Dropper;

    /// <summary>The team that owns the drop (QC kh_dropperteam): a same-team collect earns no score.</summary>
    public int DropperTeam = Teams.None;

    /// <summary>Sim time the key was dropped (QC kh_droptime / pain_finished baseline) for the return timer.</summary>
    public float DropTime;

    /// <summary>Sim time the pusher-credit window closes (QC pushltime): a push after this is no longer credited.</summary>
    public float ProtectTime;

    /// <summary>The player who last pushed the carrier (QC key.pusher), credited if a push lands in the window.</summary>
    public Player? Pusher;

    /// <summary>QC key.cnt — the per-team spawn angle (360*i/teams) the carried-key orbit advances from.</summary>
    public float SpawnAngle;

    public KeyState(int homeTeam) => HomeTeam = homeTeam;
}
