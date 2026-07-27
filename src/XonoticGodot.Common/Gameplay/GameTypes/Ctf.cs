using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Capture the Flag — port of <c>CLASS(CaptureTheFlag, Gametype)</c>
/// (common/gametypes/gametype/ctf/{ctf.qh,sv_ctf.qc}). Each team owns a flag at its base; carry the
/// enemy flag to your own (still-present) flag to score a capture for your team. First team to the
/// capture limit (QC capturelimit_override, default 10) or the lead limit (leadlimit, 6) wins.
///
/// QC defaults (gametype_init): "timelimit=20 caplimit=10 leadlimit=6" (legacydefaults "300 20 10 0").
/// CTF routes its limit through <c>capturelimit</c> (m_setTeams sets "fraglimit", but the capture count
/// is gated by capturelimit) — we read capturelimit_override / capturelimit here.
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (<see cref="TeamBalance"/>);
///  - per-team flag entities with a state machine (AtBase / Carried / Dropped) — <see cref="FlagState"/>,
///    each backed by a real world <see cref="Entity"/> spawned at the map's item_flag_team* spawns
///    (<see cref="SpawnFlag"/>, QC ctf_FlagSetup) with the carry/drop/return/capture flow driven on touch
///    (<see cref="FlagTouch"/>, QC ctf_FlagTouch → Flag.giveTo) and a per-flag <see cref="FlagThink"/>
///    (QC ctf_FlagThink: dropped-flag auto-return timer, landtime tracking);
///  - the pickup → carry → capture flow (<see cref="Pickup"/>/<see cref="Capture"/>/<see cref="ReturnFlag"/>)
///    with QC's capture scoring (carrier team SCORE += g_ctf_score_capture, CTF_CAPS += 1) and the
///    flag-carrier-kill bonus on the obituary bus (g_ctf_score_kill);
///  - the capture shield (QC ctf_CaptureShield_*): players too far behind in score are shielded from taking
///    the flag (<see cref="CaptureShieldStatus"/>/<see cref="UpdateCaptureShields"/>);
///  - time-based auto-return of a dropped flag (g_ctf_flag_return_time) via <see cref="Tick"/>;
///  - team-count detection from the spawned flags (<see cref="OnInit"/> / <see cref="DelayedInit"/>);
///  - capture-limit + lead-limit win condition;
///  - flag PASSING + THROWING (g_ctf_pass / g_ctf_throw): <see cref="ThrowFlag"/> (forward+up toss with the
///    Strength multiplier + the throw-punish ramp), <see cref="PassFlag"/>/<see cref="RequestPass"/> (a flag
///    flown to a teammate as a FLAG_PASSING entity), the in-flight re-target/give-up think (<see cref="Tick"/>
///    → DrivePass, QC ctf_FlagThink FLAG_PASSING), and the receiver retrieve (<see cref="RetrieveFlag"/>).
///
/// Deferred (NOTE — cross-boundary): flag entity networking + waypoints/HUD (CSQC), speedrunning/record timing,
/// the flag-damage return path, the dropped-flag float-in-water physics, and the pass arc-height line-of-sight
/// trace (a presentation refinement — the headless pass aims straight at the target) — client/physics concerns.
/// </summary>
[GameType]
public sealed class Ctf : GameType
{
    // ----- capture-limit cvars + default (gametype_init caplimit=10; legacydefaults caps=10) -----
    private const string CvarCapLimitOverride = "capturelimit_override";
    private const string CvarCapLimit         = "capturelimit";
    private const string CvarLeadLimitOverride = "leadlimit_override";
    private const string CvarLeadLimit         = "leadlimit";
    private const float  DefaultCapLimit  = 10f;
    private const float  DefaultLeadLimit = 6f;

    // ----- capture/kill score cvars + defaults (g_ctf_score_*; xonotic defaults) -----
    private const string CvarScoreCapture = "g_ctf_score_capture";
    private const string CvarScoreKill    = "g_ctf_score_kill";
    private const string CvarScorePickupBase = "g_ctf_score_pickup_base";
    private const string CvarScoreReturn  = "g_ctf_score_return";
    private const string CvarScorePenaltyDrop = "g_ctf_score_penalty_drop";
    private const string CvarScorePenaltyReturned = "g_ctf_score_penalty_returned";
    private const string CvarScoreCaptureAssist = "g_ctf_score_capture_assist";       // SCORE to the previous dropper on a capture
    private const string CvarScorePickupDroppedEarly = "g_ctf_score_pickup_dropped_early"; // dropped-pickup score, fresh drop
    private const string CvarScorePickupDroppedLate  = "g_ctf_score_pickup_dropped_late";  // dropped-pickup score, near auto-return
    // Fallback defaults match the stock ctfscoring-samual.cfg (exec'd via gametypes-server.cfg) so the live
    // values are Base-correct even if, for some reason, the cfg chain is not exec'd on a given host.
    private const float  DefaultScoreCapture = 20f; // team SCORE per capture (CTF_CAPS always +1)
    private const float  DefaultScoreKill    = 5f;
    private const float  DefaultScorePickupBase = 1f;
    private const float  DefaultScoreReturn  = 10f;
    private const float  DefaultScorePenaltyDrop = 1f;
    private const float  DefaultScorePenaltyReturned = 1f;
    private const float  DefaultScoreCaptureAssist = 10f;
    private const float  DefaultScorePickupDroppedEarly = 1f;
    private const float  DefaultScorePickupDroppedLate  = 1f;

    // ----- carrier-death drop toss velocity (g_ctf_drop_velocity_*; gametypes-server.cfg defaults) -----
    private const string CvarDropVelocityUp   = "g_ctf_drop_velocity_up";   // 200 — upward kick when a flag is dropped
    private const string CvarDropVelocitySide = "g_ctf_drop_velocity_side"; // 100 — randomized sideways kick
    private const float  DefaultDropVelocityUp   = 200f;
    private const float  DefaultDropVelocitySide = 100f;

    // ----- stalemate / enemy-FC reveal (g_ctf_stalemate*; gametypes-server.cfg defaults) -----
    private const string CvarStalemate            = "g_ctf_stalemate";              // 1 — reveal carriers after a long hold
    private const string CvarStalemateTime        = "g_ctf_stalemate_time";         // 60 — per-flag hold before it goes stale
    private const string CvarStalemateEndCond     = "g_ctf_stalemate_endcondition"; // 1 — end when ONE flag un-stales (2 = both)
    private const float  DefaultStalemateTime     = 60f;
    private const float  DefaultStalemateEndCond  = 1f;
    private const float  WpfeThinkRate            = 0.5f; // QC WPFE_THINKRATE — stalemate re-check cadence

    // ----- flag timing + collect-delay cvars (g_ctf_flag_*; xonotic defaults) -----
    private const string CvarFlagReturnTime   = "g_ctf_flag_return_time";   // seconds a dropped flag auto-returns after
    private const string CvarFlagCollectDelay = "g_ctf_flag_collect_delay"; // delay before a dropper may re-pickup
    private const float  DefaultFlagReturnTime   = 30f;
    private const float  DefaultFlagCollectDelay = 1f;

    // ----- dropped-flag think extras (g_ctf_*; gametypes-server.cfg defaults) -----
    private const string CvarDroppedCaptureRadius = "g_ctf_dropped_capture_radius"; // 100 — auto-cap a dropped flag near a base
    private const string CvarDroppedCaptureDelay  = "g_ctf_dropped_capture_delay";  // 1 — settle time before the auto-cap
    private const string CvarFlagFloatInWater     = "g_ctf_flag_dropped_floatinwater"; // 200 — upward float velocity in water
    private const string CvarFlagReturnDropped    = "g_ctf_flag_return_dropped";    // 100 — auto-return when dropped this near base
    private const float  DefaultDroppedCaptureRadius = 100f;
    private const float  DefaultDroppedCaptureDelay  = 1f;
    private const float  DefaultFlagReturnDropped    = 100f;
    /// <summary>QC FLAG_FLOAT_OFFSET_Z — the height above midpoint sampled to decide submerged vs. surfacing.</summary>
    private const float  FlagFloatOffsetZ = 32f;
    /// <summary>QC DPCONTENTS_WATER (SUPERCONTENTS water bit) for the float-in-water pointcontents test.</summary>
    private const int    SuperContentsWater = 0x00000010;

    // ----- passing / throwing cvars (g_ctf_pass / g_ctf_throw; gametypes-server.cfg defaults) -----
    private const string CvarPass            = "g_ctf_pass";                    // 1 — allow passing to teammates
    private const string CvarPassRequest     = "g_ctf_pass_request";            // 1 — allow +use to request a pass
    private const string CvarPassRadius      = "g_ctf_pass_radius";             // 500 — max pass distance
    private const string CvarPassVelocity    = "g_ctf_pass_velocity";           // 750 — pass flight speed
    private const string CvarPassTimelimit   = "g_ctf_pass_timelimit";          // 2 — give-up time for an in-flight pass
    private const string CvarPassWait        = "g_ctf_pass_wait";               // 2 — antispam between passes/throws
    private const string CvarThrow           = "g_ctf_throw";                   // 1 — allow throwing the flag
    private const string CvarThrowVelFwd     = "g_ctf_throw_velocity_forward";  // 500
    private const string CvarThrowVelUp      = "g_ctf_throw_velocity_up";       // 200
    private const string CvarThrowStrengthMul= "g_ctf_throw_strengthmultiplier";// 2 — multiply forward when Strength
    private const string CvarThrowAngleMin   = "g_ctf_throw_angle_min";         // -90
    private const string CvarThrowAngleMax   = "g_ctf_throw_angle_max";         // 90
    private const string CvarThrowPunishCount= "g_ctf_throw_punish_count";      // 3 — throws before the punish cooldown
    private const string CvarThrowPunishDelay= "g_ctf_throw_punish_delay";      // 30 — cooldown once punished
    private const string CvarThrowPunishTime = "g_ctf_throw_punish_time";       // 10 — window the throw count resets after
    private const float  DefaultPassRadius     = 500f;
    private const float  DefaultPassVelocity   = 750f;
    private const float  DefaultPassTimelimit  = 2f;
    private const float  DefaultPassWait       = 2f;
    private const float  DefaultThrowVelFwd    = 500f;
    private const float  DefaultThrowVelUp     = 200f;
    private const float  DefaultThrowStrengthMul = 2f;
    private const float  DefaultThrowAngleMin  = -90f;
    private const float  DefaultThrowAngleMax  = 90f;
    private const int    DefaultThrowPunishCount = 3;
    private const float  DefaultThrowPunishDelay = 30f;
    private const float  DefaultThrowPunishTime  = 10f;

    /// <summary>QC autocvar_g_ctf_pass — passing the flag to a teammate is enabled.</summary>
    public bool PassEnabled => Cvar(CvarPass, 1f) != 0f;
    /// <summary>QC autocvar_g_ctf_pass_request — a teammate may +use-request a pass from the carrier.</summary>
    public bool PassRequestEnabled => Cvar(CvarPassRequest, 1f) != 0f;
    /// <summary>QC autocvar_g_ctf_throw — deliberately throwing the flag is enabled.</summary>
    public bool ThrowEnabled => Cvar(CvarThrow, 1f) != 0f;
    public float PassRadius    => Cvar(CvarPassRadius, DefaultPassRadius);
    public float PassVelocity  => Cvar(CvarPassVelocity, DefaultPassVelocity);
    public float PassTimelimit => Cvar(CvarPassTimelimit, DefaultPassTimelimit);
    public float PassWait      => Cvar(CvarPassWait, DefaultPassWait);

    // ----- capture-shield cvars (g_ctf_shield_*; anti-camp punishment) -----
    private const string CvarShieldMinNegScore = "g_ctf_shield_min_negscore"; // shield once score <= -this (default 20)
    private const string CvarShieldMaxRatio    = "g_ctf_shield_max_ratio";    // shield at most this fraction of a team (default 0.3)
    private const string CvarShieldForce       = "g_ctf_shield_force";        // push force of the shield
    private const float  DefaultShieldMinNegScore = 20f;
    private const float  DefaultShieldMaxRatio    = 0f;    // gametypes-server.cfg: 0 = shield disabled by default
    private const float  DefaultShieldForce       = 100f;  // gametypes-server.cfg: push force of the shield

    private int   _captureShieldMinNegScore = (int)DefaultShieldMinNegScore;
    private float _captureShieldMaxRatio    = DefaultShieldMaxRatio;
    private float _captureShieldForce       = DefaultShieldForce;

    /// <summary>Per-flag drop offset for the dropped flag body (QC FLAG_DROP_OFFSET).</summary>
    private static readonly Vector3 FlagDropOffset = new(0f, 0f, 32f);
    /// <summary>Carry offset of a flag riding its carrier (QC FLAG_CARRY_OFFSET).</summary>
    private static readonly Vector3 FlagCarryOffset = new(-16f, 0f, 8f);

    // QC VEHICLE_FLAG_OFFSET (sv_ctf.qh:64) = '0 0 96': a boarding flag-carrier's flag is parked directly above
    // the craft's origin (the VehicleEnter / ctf_Handle_Pickup vehicle branches setorigin(flag, VEHICLE_FLAG_OFFSET)).
    private static readonly Vector3 FlagVehicleCarryOffset = new(0f, 0f, 96f);
    /// <summary>Flag bbox (QC CTF_FLAG.m_mins/m_maxs scaled; here the vrint'd 60x60x70-ish hull).</summary>
    private static readonly Vector3 FlagMins = new(-30f, -30f, -32f);
    private static readonly Vector3 FlagMaxs = new(30f, 30f, 38f);

    public float ScorePickupBase => TryCvar(CvarScorePickupBase, out float v) ? v : DefaultScorePickupBase;
    public float ScoreReturn     => TryCvar(CvarScoreReturn, out float v) ? v : DefaultScoreReturn;
    public float ScorePenaltyDrop => TryCvar(CvarScorePenaltyDrop, out float v) ? v : DefaultScorePenaltyDrop;
    public float ScorePenaltyReturned => TryCvar(CvarScorePenaltyReturned, out float v) ? v : DefaultScorePenaltyReturned;
    public float ScoreCaptureAssist => TryCvar(CvarScoreCaptureAssist, out float v) ? v : DefaultScoreCaptureAssist;
    public float ScorePickupDroppedEarly => TryCvar(CvarScorePickupDroppedEarly, out float v) ? v : DefaultScorePickupDroppedEarly;
    public float ScorePickupDroppedLate  => TryCvar(CvarScorePickupDroppedLate,  out float v) ? v : DefaultScorePickupDroppedLate;
    public float FlagReturnTime   => TryCvar(CvarFlagReturnTime, out float v) ? v : DefaultFlagReturnTime;
    public float FlagCollectDelay => TryCvar(CvarFlagCollectDelay, out float v) ? v : DefaultFlagCollectDelay;

    // Per-team capture totals (QC ST_CTF_CAPS, team slot 1) now live in the unified GameScores two-slot team
    // store — the source of truth (common/scores.qh MAX_TEAMSCORE). Read via GetTeamCaps / written via
    // AddTeamCaps; CTF no longer keeps a private dict (was the divergence GameScores.LeaderTeam couldn't see).

    /// <summary>The flag belonging to each team (QC g_flags), keyed by the flag's home-team color code.</summary>
    public readonly Dictionary<int, FlagState> Flags = new();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    /// <summary>QC ctf_captimerecord (sv_ctf.qc): the fastest capture this match in seconds (0 = none yet). Drives
    /// the CHOICE_CTF_CAPTURE_TIME / _BROKEN / _UNBROKEN fastest-cap broadcast. The persistent ServerProgsDB
    /// record + cross-session leaderboard (g_ctf_leaderboard) remain out of scope (need a persistent DB).</summary>
    private float _ctfCapTimeRecord;

    /// <summary>QC ctf_captimerecord refername: the name of the player who holds the current fastest cap.</summary>
    private string _ctfCapTimeRecordHolder = "";

    /// <summary>
    /// QC ctf_stalemate: both teams have held flags long enough that carriers are revealed (the anti-stall
    /// "show the enemy flagcarrier location" state). Drives the reserved CTF_STALEMATE OBJECTIVE_STATUS bit
    /// (read by the net status producer) and the enemy-FC waypoint reveal. Maintained by <see cref="CheckStalemate"/>.
    /// </summary>
    public bool Stalemate { get; private set; }

    /// <summary>QC wpforenemy_nextthink: the next time <see cref="CheckStalemate"/> re-evaluates (WPFE_THINKRATE).</summary>
    private float _stalemateNextThink;

    /// <summary>QC wpforenemy_announced: the stalemate center-print is fired once per stalemate stretch, then
    /// suppressed until the stalemate clears (when <see cref="CheckStalemate"/> resets this).</summary>
    private bool _stalemateAnnounced;

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageHandler;
    private HookHandler<MutatorHooks.VehicleEnterArgs>? _vehicleEnterHandler;
    private HookHandler<MutatorHooks.VehicleExitArgs>? _vehicleExitHandler;
    private HookHandler<MutatorHooks.MatchEndArgs>? _matchEndHandler;
    private HookHandler<MutatorHooks.GiveFragsForKillArgs>? _giveFragsHandler;
    private HookHandler<MutatorHooks.PreferPlayerScore_ClearArgs>? _preferScoreClearHandler;

    /// <summary>Per-entity handler for the WarpzoneManager.OnPortalTeleport callback: a flag-carrier who
    /// crosses a Porto portal drops the flag (QC MUTATOR_HOOKFUNCTION(ctf, PortalTeleport), sv_ctf.qc:2404,
    /// gated by autocvar_g_ctf_portalteleport). Null when CTF is not active.</summary>
    private System.Action<Entity>? _portalTeleportHandler;

    // ----- flagcarrier damage/force factors + auto-helpme (g_ctf_flagcarrier_*; gametypes-server.cfg, all 1) -----
    private const string CvarFcSelfDamage = "g_ctf_flagcarrier_selfdamagefactor"; // 1
    private const string CvarFcSelfForce  = "g_ctf_flagcarrier_selfforcefactor";  // 1
    private const string CvarFcDamage     = "g_ctf_flagcarrier_damagefactor";     // 1
    private const string CvarFcForce      = "g_ctf_flagcarrier_forcefactor";      // 1
    private const string CvarFcHelpMeDamage = "g_ctf_flagcarrier_auto_helpme_damage"; // 100 — helpme when HP dips below
    private const string CvarFcHelpMeTime   = "g_ctf_flagcarrier_auto_helpme_time";   // 2 — antispam between helpme pings
    private const float  DefaultFcHelpMeDamage = 100f;
    private const float  DefaultFcHelpMeTime   = 2f;
    /// <summary>QC healtharmor_maxdamage(start_health, start_armorvalue, ...) for the carrier health-bar scale.
    /// Standard CTF spawn = 100 health / 0 armor (the (HP+armor) effective-health approximation), so the bar's
    /// max_health is 2× this (WaypointSprite_AttachCarrier). Matches the auto-helpme HP+armor proxy above.</summary>
    private const float  CarrierStartEffHealth = 100f;

    public Ctf()
    {
        NetName = "ctf";
        DisplayName = "Capture the Flag";
        TeamGame = true;
    }

    /// <summary>QC g_flags intrusive list: every flag entity spawned on the map (item_flag_team*).</summary>
    public readonly List<Entity> FlagEntities = new();

    /// <summary>
    /// QC ctf_oneflag: a neutral flag was found (one-flag CTF). One shared neutral flag is the carriable
    /// objective; each team's base flag is a capture point (you bring the neutral flag to your own base to
    /// score — or to an enemy base under <see cref="OneFlagReverse"/>). Team flags are not pickable in this mode.
    /// </summary>
    public bool OneFlag { get; private set; }

    /// <summary>QC <c>g_ctf_oneflag_reverse</c>: in one-flag mode, capture at an ENEMY base instead of your own.</summary>
    public bool OneFlagReverse => TryCvar("g_ctf_oneflag_reverse", out float v) && v != 0f;

    /// <summary>The neutral flag (QC ctf_oneflag), or null when not one-flag mode.</summary>
    public FlagState? NeutralFlag => Flags.TryGetValue(Teams.None, out var f) ? f : null;

    /// <summary>Detected team count (QC ctf_DelayedInit BIT scan of flag teams), 2..4. Defaults to 2.</summary>
    private int _detectedTeams = 2;

    public override void OnInit()
    {
        // QC ctf_Initialize → ctf_DelayedInit: detect the team count and one-flag mode from the flag
        // entities the BSP lump spawned, and seed the capture-shield tunables. GameRules_teams(true) and the
        // Flag registrable (NEW(Flag)) are the engine's job; here OnInit wires the shield cvars.
        _captureShieldMinNegScore = (int)Cvar(CvarShieldMinNegScore, DefaultShieldMinNegScore);
        _captureShieldMaxRatio    = Cvar(CvarShieldMaxRatio, DefaultShieldMaxRatio);
        _captureShieldForce       = Cvar(CvarShieldForce, DefaultShieldForce);
        DelayedInit();
    }

    /// <summary>
    /// QC ctf_DelayedInit: scan the spawned flags to decide how many teams play (a flag per team → that
    /// team is in play) and whether a neutral flag makes this one-flag CTF. Falls back to Red+Blue when
    /// fewer than two team-flags exist (QC NumTeams &lt; 2 → default two-base).
    /// </summary>
    public void DelayedInit()
    {
        int bits = 0;
        OneFlag = false;
        foreach (Entity f in FlagEntities)
        {
            switch ((int)f.Team)
            {
                case Teams.Red:    bits |= 1 << 0; break;
                case Teams.Blue:   bits |= 1 << 1; break;
                case Teams.Yellow: bits |= 1 << 2; break;
                case Teams.Pink:   bits |= 1 << 3; break;
                case Teams.None:   OneFlag = true; break;
            }
        }
        int n = System.Numerics.BitOperations.PopCount((uint)bits);
        _detectedTeams = n < 2 ? 2 : System.Math.Clamp(n, 2, 4);
    }

    /// <summary>
    /// CTF team count: detected from the map's flags (QC ctf_DelayedInit), 2..4. Two-base is the design
    /// default (QC m_isTwoBaseMode) so a map with only Red+Blue flags yields 2.
    /// </summary>
    public int TeamCount => _detectedTeams;

    /// <summary>Capture limit in force (capturelimit_override, else capturelimit, else 10). 0 == unlimited.</summary>
    public float CaptureLimit
    {
        get
        {
            if (TryCvar(CvarCapLimitOverride, out float ov)) return ov;
            if (TryCvar(CvarCapLimit, out float cl)) return cl;
            return DefaultCapLimit;
        }
    }

    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimitOverride, out float ov)) return ov;
            if (TryCvar(CvarLeadLimit, out float l)) return l;
            return DefaultLeadLimit;
        }
    }

    public float ScoreCapture => TryCvar(CvarScoreCapture, out float v) ? v : DefaultScoreCapture;
    public float ScoreKill    => TryCvar(CvarScoreKill,    out float v) ? v : DefaultScoreKill;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Stalemate = false;
        _stalemateNextThink = 0f;
        _stalemateAnnounced = false;
        _ctfCapTimeRecord = 0f;
        _ctfCapTimeRecordHolder = "";
        Scoring.GameScores.ResetTeams();  // QC Score_ClearAll at match start: zero both team slots before declaring
        DeclareScoreRules();
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)
        foreach (int team in Teams.Active(TeamCount))
            if (!Flags.ContainsKey(team)) Flags[team] = new FlagState(team);
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
        // QC MUTATOR_HOOKFUNCTION(ctf, Damage_Calculate): apply the flagcarrier damage/force factors + auto-helpme.
        _damageHandler = OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageHandler);
        // QC MUTATOR_HOOKFUNCTION(ctf, VehicleEnter/VehicleExit): if vehicle carry is not allowed, a carrier who
        // boards a vehicle drops the flag (DROP_NORMAL). On exit the flag re-follows the player via the per-tick
        // Tick() carrier-follow (carrier.Vehicle becomes null, so the next Tick anchor reverts to the player).
        _vehicleEnterHandler = OnVehicleEnter;
        MutatorHooks.VehicleEnter.Add(_vehicleEnterHandler);
        _vehicleExitHandler = OnVehicleExit;
        MutatorHooks.VehicleExit.Add(_vehicleExitHandler);
        // QC MUTATOR_HOOKFUNCTION(ctf, MatchEnd): lock any dropped/passing flag the instant the game ends so a
        // live flag can't keep moving or be touched during intermission.
        _matchEndHandler = OnMatchEnd;
        MutatorHooks.MatchEnd.Add(_matchEndHandler);

        // QC MUTATOR_HOOKFUNCTION(ctf, GiveFragsForKill): in CTF a regular kill awards no frag SCORE when the
        // host sets g_ctf_ignore_frags (default 0 = faithful no-op). The hook always returns true (so the kill
        // path reads our FragScore back); at the default it leaves the +1 frag intact, only zeroing it when the
        // cvar is set. Fired live from Scores.Obituary's GiveFragsForKill.Call site.
        _giveFragsHandler = OnGiveFragsForKill;
        MutatorHooks.GiveFragsForKill.Add(_giveFragsHandler);
        // QC MUTATOR_HOOKFUNCTION(ctf, PreferPlayerScore_Clear) { return true; }: under g_score_resetonjoin -1,
        // CTF prefers to KEEP a rejoining player's accumulated score. Fired live from GameScores.ClearPlayerOnJoin.
        _preferScoreClearHandler = OnPreferPlayerScoreClear;
        MutatorHooks.PreferPlayerScore_Clear.Add(_preferScoreClearHandler);

        // QC MUTATOR_HOOKFUNCTION(ctf, PortalTeleport) (sv_ctf.qc:2404): when a carrier crosses a Porto
        // portal, drop the flag (DROP_NORMAL) if autocvar_g_ctf_portalteleport is set (default 0 = disabled;
        // with ratio 0 the stock config is inert, exactly like Base). The WarpzoneManager.OnPortalTeleport
        // callback fires for every client crossing a Porto-owned warpzone — a player-only path.
        _portalTeleportHandler = OnPortalTeleport;
        WarpzoneManager.OnPortalTeleport += _portalTeleportHandler;
    }

    /// <summary>
    /// QC <c>ctf_ScoreRules</c> (sv_ctf.qc): declare CTF's scoreboard columns + the two TEAM-score slots and pin
    /// the sort keys. QC: <c>GameRules_scoring(teams, SFL_SORT_PRIO_PRIMARY, 0, { field_team(ST_CTF_CAPS, "caps",
    /// PRIMARY); field(SP_CTF_CAPS, "caps", SECONDARY); ... })</c> — so the TEAM primary is slot 1 (ST_CTF_CAPS,
    /// "caps"), slot 0 (ST_SCORE) is the team's secondary (stprio=0); the PLAYER primary is SP_SCORE with
    /// SP_CTF_CAPS secondary. Teams rank by total caps then total score; players rank by score then personal caps.
    /// The remaining columns (captime/pickups/fckills/returns/drops) are display stats.
    /// </summary>
    private static void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: true);
        // Team slots: ST_SCORE (slot 0) "score" no-prio; ST_CTF_CAPS (slot 1) "caps" PRIMARY.
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None);
        GS.SetTeamLabel(GS.TeamSlotSecondary, "caps", Scoring.ScoreFlags.SortPrioPrimary);
        GS.DeclareColumn("CTF_CAPS", Scoring.ScoreFlags.None, "caps");
        GS.DeclareColumn("CTF_CAPTIME", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "captime");
        GS.DeclareColumn("CTF_PICKUPS", Scoring.ScoreFlags.None, "pickups");
        GS.DeclareColumn("CTF_FCKILLS", Scoring.ScoreFlags.None, "fckills");
        GS.DeclareColumn("CTF_RETURNS", Scoring.ScoreFlags.None, "returns");
        GS.DeclareColumn("CTF_DROPS", Scoring.ScoreFlags.LowerIsBetter, "drops");
        // sprio PRIMARY = SP_SCORE; SP_CTF_CAPS SECONDARY (QC field(SP_CTF_CAPS, "caps", SFL_SORT_PRIO_SECONDARY)).
        GS.SetSortKeys(GS.Score, GS.Field("CTF_CAPS"));
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for one of CTF's player columns (no-op if the
    /// field is somehow unregistered). Centralizes the per-event column writes onto the unified score table.</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (_damageHandler is not null)
        {
            MutatorHooks.DamageCalculate.Remove(_damageHandler);
            _damageHandler = null;
        }
        if (_vehicleEnterHandler is not null)
        {
            MutatorHooks.VehicleEnter.Remove(_vehicleEnterHandler);
            _vehicleEnterHandler = null;
        }
        if (_vehicleExitHandler is not null)
        {
            MutatorHooks.VehicleExit.Remove(_vehicleExitHandler);
            _vehicleExitHandler = null;
        }
        if (_matchEndHandler is not null)
        {
            MutatorHooks.MatchEnd.Remove(_matchEndHandler);
            _matchEndHandler = null;
        }
        if (_giveFragsHandler is not null)
        {
            MutatorHooks.GiveFragsForKill.Remove(_giveFragsHandler);
            _giveFragsHandler = null;
        }
        if (_preferScoreClearHandler is not null)
        {
            MutatorHooks.PreferPlayerScore_Clear.Remove(_preferScoreClearHandler);
            _preferScoreClearHandler = null;
        }
        if (_portalTeleportHandler is not null)
        {
            WarpzoneManager.OnPortalTeleport -= _portalTeleportHandler;
            _portalTeleportHandler = null;
        }
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>The flag whose home base is <paramref name="team"/>, or null if no such flag exists.</summary>
    public FlagState? FlagOf(int team) => Flags.TryGetValue(team, out var f) ? f : null;

    /// <summary>Waypoint sprites: one per flag at its live position, with the state-resolved def (QC the CTF
    /// WaypointSprite_SpawnFixed flag base / WP_FlagDropped* / WP_FlagCarrier* state machine). The flag entity's
    /// origin follows its carrier when carried, so rebuilding from it each tick tracks the carrier. Shown to
    /// everyone, colored by the flag's home team; drives both the 3D in-world "Flag" marker and the radar icon.</summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        foreach (FlagState f in Flags.Values)
        {
            // ----- AtBase / Dropped: a single fixed flag marker shown to everyone (QC wps_flagbase / wps_flagdropped,
            // both spawned via showto=NULL/team=0 so they are SPRITERULE_DEFAULT-visible to all). -----
            if (f.Status != FlagStatus.Carried || f.Carrier is not { } carrier)
            {
                Vector3 fpos = f.Entity is { } fe ? fe.Origin : f.HomeOrigin;
                string fsuffix = FlagSuffix(f.HomeTeam);
                string fsprite = f.Status == FlagStatus.Dropped ? "FlagDropped" + fsuffix : "FlagBase" + fsuffix;
                into.Add(new Waypoints.WaypointSprite
                {
                    // Color is the RADAR tint = the flag's team color (QC WaypointSprite_UpdateTeamRadar with the
                    // team palette color); the 3D in-world sprite uses the def's own color (tan/white) from the registry.
                    // Visibility Team = 0: QC ctf_FlagSetup spawns the flag waypoints via WaypointSprite_SpawnFixed
                    // (showto=NULL, t=0) so flag markers are shown to EVERYONE (the SPRITERULE_DEFAULT team-restriction
                    // applies only when wp.team is set — the flag's home team lives in Color, not the visibility team).
                    SpriteName = fsprite, FixedOrigin = fpos, Team = 0, Color = TeamRadarColor(f.HomeTeam),
                    RadarIcon = 1, Health = -1f, MaxHealth = 1f,
                });
                continue;
            }

            // ----- Carried: QC ctf_FlagcarrierWaypoints spawns TWO sprites on the carrier (sv_ctf.qc:160). -----
            int carrierTeam = (int)carrier.Team;

            // (1) wps_flagcarrier (WP_FlagCarrier, team = carrier.team): the OWN-TEAM "your carrier is here" marker
            // with a HEALTH BAR + the auto-helpme flash. This marker is NO LONGER emitted from the per-tick snapshot:
            // it is now owned by the LIVE WaypointSprites registry via WaypointSprites.AttachCarrier, created in
            // Pickup/RetrieveFlag and stored on flag.CarrierWaypoint (so its Health/MaxHealth bar + HelpmeUntil flash
            // persist across ticks and are refreshed in Tick/UpdateCarrierHealth). Emitting it here too would draw a
            // DOUBLE carrier marker, so the snapshot deliberately skips it — see EnsureCarrierWaypoint.

            // (2) wps_enemyflagcarrier (WP_FlagCarrierEnemy<carrier-team>): the ENEMY reveal. QC only creates this
            // when the carrier holds their OWN flag (the one-flag self-carry case, sv_ctf.qc:167) OR during a
            // stalemate (ctf_CheckStalemate, sv_ctf.qc:945). It is gated to enemies + live players by
            // ctf_Stalemate_Customize (hidden from same-team and from observers). Without this gate the port leaked
            // the carrier's exact position to enemies at all times, defeating the whole point of the stalemate timer.
            // QC CTF_SAMETEAM(player, flag): same-team unless a reverse mode is on (then diff-team). The enemy-FC
            // self-carry reveal fires when the carrier "owns" the flag they hold, which in reverse modes means an
            // enemy-team flag instead.
            bool reverse = (TryCvar("g_ctf_reverse", out float rv) && rv != 0f) || (OneFlag && OneFlagReverse);
            bool sameAsFlag = reverse ? f.HomeTeam != carrierTeam : f.HomeTeam == carrierTeam;
            bool selfCarry = sameAsFlag; // CTF_SAMETEAM(player, player.flagcarried) — usually false (carry the ENEMY flag)
            if (Stalemate || selfCarry)
            {
                string esuffix = FlagSuffix(carrierTeam);
                into.Add(new Waypoints.WaypointSprite
                {
                    SpriteName = "FlagCarrierEnemy" + esuffix, Owner = carrier, Offset = FlagWaypointOffset,
                    Team = 0, Color = CarrierRadarColor(carrierTeam), // WPCOLOR_ENEMYFC(team) = same 0.75 scale
                    RadarIcon = 1, Health = -1f, MaxHealth = 1f,
                    // QC ctf_Stalemate_Customize (sv_ctf.qc:887): shown only to a live player on a DIFFERENT team,
                    // and hidden entirely while the carrier holds the Invisibility powerup.
                    VisibleForPlayer = viewer => viewer is not null && !viewer.IsObserver
                        && (int)viewer.Team != carrierTeam
                        && !(StatusEffectsCatalog.ByName("invisibility") is { } invis
                             && StatusEffectsCatalog.Has(carrier, invis)),
                });
            }

            // (3) wps_flagreturn (WP_FlagReturn, sv_ctf.qc:192): spawned ONLY when the carrier holds their OWN
            // flag (selfCarry, a reverse-mode scenario). It is a fixed-position sprite at the CARRIED flag's home
            // origin (ctf_spawnorigin + FLAG_WAYPOINT_OFFSET), colored cyan ('0 0.8 0.8'), and shown exclusively
            // to the carrier via ctf_Return_Customize (owner == this.owner). This gives the self-carrier a "go back
            // here to score" marker at their flag's spawn point (the capture base in one-flag-reverse mode).
            if (selfCarry)
            {
                // The "return" destination is the carried flag's own home base (QC: player.flagcarried.ctf_spawnorigin).
                Vector3 returnPos = f.HomeOrigin + FlagWaypointOffset;
                Player capturedCarrier = carrier; // closure capture for the visibility lambda
                into.Add(new Waypoints.WaypointSprite
                {
                    SpriteName = "FlagReturn", FixedOrigin = returnPos,
                    Team = 0, // not team-restricted by WP team; visibility is per-viewer via the lambda
                    Color = new Vector3(0f, 0.8f, 0.8f), // QC owp.colormod = '0 0.8 0.8'
                    RadarIcon = 1, Health = -1f, MaxHealth = 1f,
                    // QC ctf_Return_Customize (sv_ctf.qc:154): visible only to the carrier (client == this.owner).
                    VisibleForPlayer = viewer => ReferenceEquals(viewer, capturedCarrier),
                });
            }
        }
    }

    /// <summary>QC FLAG_WAYPOINT_OFFSET ('0 0 64'): the carrier waypoint sits above the player's origin.</summary>
    private static readonly Vector3 FlagWaypointOffset = new(0f, 0f, 64f);

    /// <summary>QC WPCOLOR_FLAGCARRIER / WPCOLOR_ENEMYFC: the carrier radar tint is the team palette color scaled by
    /// 0.75 (neutral → white). Built on the same <see cref="TeamRadarColor"/> the flag base/dropped markers use.</summary>
    private static Vector3 CarrierRadarColor(int team) =>
        team == Teams.None ? new Vector3(1f, 1f, 1f) : TeamRadarColor(team) * 0.75f;

    /// <summary>QC <c>WaypointSprite_AttachCarrier</c> in-world tint: WP_FlagCarrier's def color (tan '0.8 0.8 0').
    /// The radar tint is still the team color (CarrierRadarColor); this is the 3D sprite's body color.</summary>
    private static readonly Vector3 CarrierSpriteColor = new(0.8f, 0.8f, 0f);

    /// <summary>
    /// QC <c>healtharmor_maxdamage(health, armor, blockpercent, deathtype)</c> approximation: the effective
    /// health an attacker must remove to kill <paramref name="e"/> — modeled here as <c>health + armor</c> (the
    /// armor-block percentage is per-deathtype and not available at this seam, so the unblocked HP+armor sum is
    /// the faithful headless proxy, matching the auto-helpme effective-HP test in <see cref="OnDamageCalculate"/>).
    /// Used for the carrier health-bar scale (the bar's max is <c>2 *</c> the spawn value, per QC AttachCarrier).
    /// </summary>
    private static float HealthArmorMaxDamage(Entity e) => e.Health + e.ArmorValue;

    /// <summary>The carrier health-bar denominator (QC WaypointSprite_AttachCarrier <c>max_health =
    /// 2 * healtharmor_maxdamage(start_health, start_armorvalue, ...)</c>; CTF spawns 100 HP / 0 armor → 200).</summary>
    private static float CarrierMaxHealth => 2f * CarrierStartEffHealth;

    /// <summary>QC AttachCarrier health bar fill: <c>healtharmor_maxdamage(carrier) / max_health</c>, clamped 0..1
    /// (&lt;0 hides the bar when the carrier is gone/dead).</summary>
    private static float CarrierHealthFraction(Player carrier)
    {
        float max = CarrierMaxHealth;
        return max > 0f ? System.Math.Clamp(HealthArmorMaxDamage(carrier) / max, 0f, 1f) : -1f;
    }

    /// <summary>
    /// QC ctf_FlagcarrierWaypoints (sv_ctf.qc:160) WaypointSprite_AttachCarrier(WP_FlagCarrier, player,
    /// RADARICON_FLAGCARRIER) + WaypointSprite_UpdateMaxHealth(2*healtharmor_maxdamage) +
    /// WaypointSprite_UpdateHealth(healtharmor_maxdamage(player)): spawn the LIVE own-team carrier waypoint that
    /// rides <paramref name="carrier"/> with a health bar, and store it on the flag. Killing any prior sprite first
    /// keeps a single owner of the carrier marker (a re-pickup/retrieve replaces, never doubles).
    /// </summary>
    private void EnsureCarrierWaypoint(FlagState flag, Player carrier)
    {
        Waypoints.WaypointSprites.Kill(flag.CarrierWaypoint); // drop any stale sprite (re-pickup / pass retrieve)
        int carrierTeam = (int)carrier.Team;
        Waypoints.WaypointSprite wp = Waypoints.WaypointSprites.AttachCarrier(
            "FlagCarrier", carrier, carrierTeam, CarrierSpriteColor, radarIcon: 1, rule: Waypoints.SpriteRule.Default);
        // QC WaypointSprite_UpdateMaxHealth(wp, 2 * healtharmor_maxdamage(start_health, start_armorvalue)) then
        // WaypointSprite_UpdateHealth(wp, healtharmor_maxdamage(player)) — here Health is pre-normalized 0..1.
        Waypoints.WaypointSprites.UpdateMaxHealth(wp, CarrierMaxHealth);
        Waypoints.WaypointSprites.UpdateHealth(wp, CarrierHealthFraction(carrier));
        // The radar tint stays the team color (WPCOLOR_FLAGCARRIER = team palette * 0.75); the sprite body is tan.
        Waypoints.WaypointSprites.UpdateTeamRadar(wp, 1, CarrierRadarColor(carrierTeam));
        flag.CarrierWaypoint = wp;
    }

    /// <summary>Refresh the live carrier waypoint's health bar from the current carrier each tick (QC the per-think
    /// WaypointSprite_UpdateHealth on the flagcarrier sprite). No-op when the flag has no live carrier sprite.</summary>
    private void RefreshCarrierWaypoint(FlagState flag)
    {
        if (flag.CarrierWaypoint is null)
            return;
        if (flag.Status == FlagStatus.Carried && flag.Carrier is { } carrier)
            Waypoints.WaypointSprites.UpdateHealth(flag.CarrierWaypoint, CarrierHealthFraction(carrier));
    }

    /// <summary>QC the flag leaving the carrier (capture / return / drop / death / disconnect / reset): kill the
    /// live carrier waypoint and null the back-link so no orphaned health-bar sprite rides a non-carrier.</summary>
    private static void KillCarrierWaypoint(FlagState flag)
    {
        if (flag.CarrierWaypoint is null)
            return;
        Waypoints.WaypointSprites.Kill(flag.CarrierWaypoint);
        flag.CarrierWaypoint = null;
    }

    /// <summary>Flag team → the def-name suffix (QC the per-team WP_Flag*Red/Blue/Yellow/Pink/Neutral split).</summary>
    private static string FlagSuffix(int team) => team switch
    {
        Teams.Red => "Red",
        Teams.Blue => "Blue",
        Teams.Yellow => "Yellow",
        Teams.Pink => "Pink",
        _ => "Neutral",
    };

    /// <summary>Flag team → its radar tint (QC colormapPaletteColor(team-1); neutral → white). Matches the
    /// client radar's Team_ColorRGB so flag icons read in the team color.</summary>
    private static Vector3 TeamRadarColor(int team) => team switch
    {
        Teams.Red => new Vector3(1f, 0.0625f, 0.0625f),
        Teams.Blue => new Vector3(0.0625f, 0.0625f, 1f),
        Teams.Yellow => new Vector3(1f, 1f, 0.0625f),
        Teams.Pink => new Vector3(1f, 0.0625f, 1f),
        _ => new Vector3(1f, 1f, 1f),
    };

    /// <summary>
    /// QC ctf_Handle_Pickup (PICKUP_BASE/DROPPED): <paramref name="player"/> takes the enemy flag
    /// <paramref name="flag"/>. The flag must not be the player's own and must be takeable (at base or
    /// dropped). Sets the flag to Carried by the player. Returns true if the pickup happened.
    /// </summary>
    public bool Pickup(Player player, FlagState flag)
    {
        if (MatchEnded || player.IsDead)
            return false;
        if (flag.HomeTeam == (int)player.Team)
            return false; // can't pick up your own flag (that's a return, not a pickup)
        if (flag.Status == FlagStatus.Carried)
            return false; // already carried

        bool fromBase = flag.Status == FlagStatus.AtBase;
        flag.Status = FlagStatus.Carried;
        flag.Carrier = player;
        if (fromBase)
            flag.PickupTime = Api.Services is not null ? Api.Clock.Time : 0f; // QC PICKUP_BASE timing baseline

        // QC ctf_Handle_Pickup scoring: PICKUP_BASE awards g_ctf_score_pickup_base to the carrier's team; a
        // PICKUP_DROPPED awards an interpolated early→late score scaled by how much auto-return time is left
        // (sv_ctf.qc:819) — a fresh drop pays "early", one about to time out pays "late".
        if (fromBase)
        {
            if (ScorePickupBase != 0f)
            {
                player.ScoreFrags += (int)ScorePickupBase;
                AddTeamScore(player.Team, (int)ScorePickupBase); // QC GameRules_scoring_add_team(player, SCORE, pickup_base)
            }
        }
        else
        {
            int droppedScore = DroppedPickupScore(flag);
            if (droppedScore != 0)
            {
                player.ScoreFrags += droppedScore;
                AddTeamScore(player.Team, droppedScore); // QC GameRules_scoring_add_team(player, SCORE, pickup_dropped_score)
            }
        }
        AddCol(player, "CTF_PICKUPS", 1); // QC GameRules_scoring_add(player, CTF_PICKUPS, 1)

        // Attach the world flag entity to the carrier (QC setattachment + FLAG_CARRY_OFFSET).
        if (flag.Entity is not null)
        {
            GametypeEntities.AttachToCarrier(flag.Entity, player, FlagCarryOffset);
            flag.Entity.GtStatus = (int)FlagStatus.Carried;
        }
        GametypeEntities.ScoringVip(player, true); // QC GameRules_scoring_vip(player, true) (sv_ctf.qc:746)

        // QC ctf_FlagcarrierWaypoints: attach the LIVE own-team carrier waypoint (health bar + auto-helpme flash)
        // to the new carrier. This is the single owner of the carried-flag marker — CollectWaypoints no longer
        // emits a per-tick FlagCarrier sprite, so the bar persists + is refreshed each Tick (RefreshCarrierWaypoint).
        EnsureCarrierWaypoint(flag, player);

        // QC ctf_Handle_Pickup: the global "flag taken" voice + the kill-feed line + the carrier's centerprint.
        FlagAnnounceSound(flag.HomeTeam, "TAKEN");
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, $"CTF_PICKUP_{TeamSuffix(flag.HomeTeam)}", player.NetName);
        NotificationSystem.Center(player, $"CTF_PICKUP_{TeamSuffix(flag.HomeTeam)}");

        // QC ctf_Handle_Pickup per-audience fan-out (sv_ctf.qc:790-801): every OTHER player gets an explicit
        // on-screen line naming WHICH flag was taken — without it a third party only hears the voice and sees
        // the kill feed (r16: "just that a flag was taken"). FOREACH_CLIENT(IS_PLAYER(it) && it != player):
        //   same team as the carrier            → "Your %steammate got the <FLAG> flag! Protect them!"
        //   the flag's own team                 → "The %senemy got your flag! Retrieve it!"
        //   a third team (3/4-team CTF)         → "The %senemy got the <FLAG> flag! Retrieve it!"
        //   anyone else, neutral (one-flag)     → "The %senemy got the flag! Retrieve it!"
        // Args mirror QC: Team_ColorCode(player.team) + netname (the CHOICE verbose variant consumes both).
        if (_lastRoster is { } roster2)
        {
            bool neutral = flag.HomeTeam == Teams.None;
            string flagSuffix = neutral ? "NEUTRAL" : TeamSuffix(flag.HomeTeam);
            string carrierColor = Teams.ColorCode((int)player.Team);
            foreach (Player it in roster2)
            {
                if (ReferenceEquals(it, player) || it.IsObserver) // QC IS_PLAYER(it) && it != player
                    continue;
                string notif =
                    (int)it.Team == (int)player.Team ? $"CTF_PICKUP_TEAM_{flagSuffix}"
                    : neutral ? "CTF_PICKUP_ENEMY_NEUTRAL"
                    : (int)it.Team == flag.HomeTeam ? "CTF_PICKUP_ENEMY"
                    : $"CTF_PICKUP_ENEMY_OTHER_{flagSuffix}";
                NotificationSystem.Send(NotifBroadcast.One, it, MsgType.Choice, notif, carrierColor, player.NetName);
            }
        }
        return true;
    }

    /// <summary>
    /// QC ctf_Handle_Pickup PICKUP_DROPPED score (sv_ctf.qc:819): interpolate between
    /// g_ctf_score_pickup_dropped_early (fresh drop) and _late (about to auto-return) by the fraction of the
    /// auto-return time remaining, rounded as QC <c>floor(x + 0.5)</c>.
    /// </summary>
    private int DroppedPickupScore(FlagState flag)
    {
        float returnTime = FlagReturnTime;
        float remaining = returnTime > 0f
            ? QClamp(((flag.DropTime + returnTime) - Now) / returnTime, 0f, 1f)
            : 1f; // no auto-return → treated as fully "early"
        float score = ScorePickupDroppedLate * (1f - remaining) + ScorePickupDroppedEarly * remaining;
        return (int)System.MathF.Floor(score + 0.5f);
    }

    /// <summary>
    /// QC ctf_Handle_Capture (CAPTURE_NORMAL): <paramref name="player"/> brings the enemy flag they carry
    /// home and captures it. Requires that the player carries an enemy flag AND their own flag is at base
    /// (QC CTF_SAMETEAM(toucher, flag) &amp;&amp; toucher.flagcarried). Awards the team a capture and applies
    /// the win condition. Returns the captured flag's home team, or <see cref="Teams.None"/> if no capture.
    /// </summary>
    public int Capture(Player player)
    {
        if (MatchEnded)
            return Teams.None;

        FlagState? carried = CarriedBy(player);
        if (carried is null)
            return Teams.None;

        // The capturing player's own flag must be home (QC requires the home flag present to score).
        FlagState? home = FlagOf((int)player.Team);
        if (home is not null && home.Status != FlagStatus.AtBase)
            return Teams.None;

        // ----- score the capture (QC GameRules_scoring_add_team SCORE + CTF_CAPS) -----
        // QC adds capscore to BOTH the player SP_SCORE and the team ST_SCORE (slot 0); +1 to player+team CTF_CAPS.
        AddTeamCaps(player.Team, 1);
        player.ScoreFrags += (int)ScoreCapture;     // individual SCORE credit (carrier)
        AddTeamScore(player.Team, (int)ScoreCapture); // QC GameRules_scoring_add_team(player, SCORE, capscore): team ST_SCORE
        AddCol(player, "CTF_CAPS", 1);              // QC GameRules_scoring_add_team(player, CTF_CAPS, 1): player caps column
        // QC CTF_CAPTIME (best/fastest capture run): the carry duration of the captured flag, encoded as hundredths
        // (SFL_LOWER_IS_BETTER | SFL_TIME); only updates when it beats the player's prior best.
        if (carried.PickupTime > 0f)
        {
            float captime = (Api.Services is not null ? Api.Clock.Time : 0f) - carried.PickupTime;
            Scoring.ScoreField? cf = Scoring.GameScores.Field("CTF_CAPTIME");
            if (captime > 0f && cf is not null)
                Scoring.GameScores.SetBestTime(player, cf, Scoring.GameScores.TimeEncode(captime));
        }

        // QC ctf_Handle_Capture capture-assist (sv_ctf.qc:673): the teammate who previously dropped this flag
        // (its last ctf_dropper) is credited g_ctf_score_capture_assist for setting up the cap.
        if (carried.Dropper is { } assistPlayer && !ReferenceEquals(assistPlayer, player))
        {
            int assist = (int)ScoreCaptureAssist;
            if (assist != 0)
            {
                assistPlayer.ScoreFrags += assist;
                AddTeamScore(assistPlayer.Team, assist); // QC GameRules_scoring_add_team(ctf_dropper, SCORE, score_assist)
            }
        }

        // QC ctf_Handle_Capture (sv_ctf.qc:634): the capturer's centerprint, then ctf_CaptureRecord (the kill-feed
        // broadcast incl. the fastest-cap time), then the global "flag captured" voice.
        NotificationSystem.Center(player, $"CTF_CAPTURE_{TeamSuffix(carried.HomeTeam)}");
        float capRunTime = carried.PickupTime > 0f ? ((Api.Services is not null ? Api.Clock.Time : 0f) - carried.PickupTime) : 0f;
        CaptureRecord(carried.HomeTeam, player, capRunTime);
        // QC ctf_Handle_Capture (sv_ctf.qc:636): the capture VOICE is keyed on the CAPTURING team's flag
        // (_sound(player, …, flag.snd_flag_capture) — `flag` is the base flag the carrier touched, i.e. the
        // capturer's OWN team; DIFF_TEAM(player, flag) is false), NOT the captured enemy flag. Keying it on
        // carried.HomeTeam played the enemy's announcer, so "red scores" / "blue scores" were reversed
        // (playtest-bugs #13). The CENTERPRINT above stays on the captured flag's team, matching QC's
        // APP_NUM(enemy_flag.team, CENTER_CTF_CAPTURE).
        FlagAnnounceSound((int)player.Team, "CAPTURE");

        // QC ctf_Handle_Capture: Send_Effect_(flag.capeffect, …) — the team-colored capture burst at the flag base
        // (EFFECT_CAP(teamnum) = "<team>_cap"). The captured flag's home team picks the color (QC enemy_flag.team).
        if (carried.Entity is Entity capFe)
            EffectEmitter.Emit($"{TeamName(carried.HomeTeam)}_cap", capFe.Origin);

        // return the captured flag to its base, freeing the carrier (QC ctf_RespawnFlag(enemy_flag))
        KillCarrierWaypoint(carried); // QC: the carried-flag waypoint is removed as the flag leaves the carrier
        carried.ResetToBase();
        RespawnFlagEntity(carried);
        GametypeEntities.ScoringVip(player, false); // QC GameRules_scoring_vip(player, false) on capture (sv_ctf.qc:1249)

        // QC ctf_Handle_Capture: a successful capture clears the carrier's capture shield (they're scoring now).
        UpdateCaptureShield(player);

        UpdateLeaderAndCheckLimit();
        return carried.HomeTeam;
    }

    /// <summary>
    /// QC ctf_Handle_Return: <paramref name="player"/> touches their own dropped/away flag, returning it to
    /// base (only meaningful for a dropped flag). Awards the QC return SCORE to the player.
    /// </summary>
    public void ReturnFlag(Player player, FlagState flag)
    {
        if (flag.HomeTeam != (int)player.Team)
            return;
        if (flag.Status == FlagStatus.AtBase)
            return;

        // QC g_ctf_score_return: reward the returner; punish the team who was last carrying it (penalty_returned).
        player.ScoreFrags += (int)ScoreReturn;
        AddTeamScore(player.Team, (int)ScoreReturn); // QC GameRules_scoring_add_team(player, SCORE, score_return)
        AddCol(player, "CTF_RETURNS", 1); // QC GameRules_scoring_add(player, CTF_RETURNS, 1)
        AddTeamScorePenalty(flag.HomeTeam, (int)ScorePenaltyReturned);

        // QC ctf_Handle_Return (sv_ctf.qc:709): the player who DROPPED this flag is also docked penalty_returned,
        // shielded from re-taking it, and given a collect-delay before they may pick it up again.
        if (flag.Dropper is { } dropper)
        {
            dropper.ScoreFrags -= (int)ScorePenaltyReturned; // QC GameRules_scoring_add(ctf_dropper, SCORE, -penalty)
            // QC ctf_CaptureShield_Update(ctf_dropper, 0): recompute (and possibly raise) the dropper's shield. The
            // composite-score recompute is driven by the per-tick UpdateCaptureShields; here we just arm the delay.
            dropper.GtNextTakeTime = Now + FlagCollectDelay; // QC flag.ctf_dropper.next_take_time = time + collect_delay
        }

        // QC ctf_Handle_Return: the global "flag returned" voice + the team-colored kill-feed line
        // (APP_TEAM_NUM(flag.team, INFO_CTF_RETURN)) + the returner's centerprint (CENTER_CTF_RETURN).
        FlagAnnounceSound(flag.HomeTeam, "RETURNED");
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, $"CTF_RETURN_{TeamSuffix(flag.HomeTeam)}", player.NetName);
        NotificationSystem.Center(player, $"CTF_RETURN_{TeamSuffix(flag.HomeTeam)}");

        flag.ResetToBase();
        RespawnFlagEntity(flag);
    }

    /// <summary>
    /// QC ctf_CheckFlagReturn / auto-return: a dropped flag whose return timer elapsed (or that was killed by
    /// the world) returns itself to base. Used by <see cref="Tick"/>; no player credit.
    /// </summary>
    public void AutoReturnFlag(FlagState flag)
    {
        if (flag.Status != FlagStatus.Dropped)
            return;

        // QC ctf_CheckFlagReturn: the global "flag returned" voice + the team-colored timeout kill-feed line
        // (APP_NUM(flag.team, INFO_CTF_FLAGRETURN_TIMEOUT); the neutral flag uses the _NEUTRAL variant). No player credit.
        FlagAnnounceSound(flag.HomeTeam, "RETURNED");
        NotificationSystem.Info($"CTF_FLAGRETURN_TIMEOUT_{TeamSuffix(flag.HomeTeam)}");

        flag.ResetToBase();
        RespawnFlagEntity(flag);
    }

    /// <summary>QC ctf_Handle_Drop / PlayerDies: a carrier who dies drops the flag where they fell.</summary>
    public void DropFlag(Player carrier)
    {
        FlagState? carried = CarriedBy(carrier);
        if (carried is null)
            return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        carried.Status = FlagStatus.Dropped;
        carried.Carrier = null;
        carried.DropOrigin = carrier.Origin;
        carried.DropTime = now;
        carried.Dropper = carrier;
        KillCarrierWaypoint(carried); // QC: the carrier waypoint dies as the flag leaves the carrier (it's dropped now)

        // QC ctf_Handle_Drop scoring: the dropper's team is docked g_ctf_score_penalty_drop + CTF_DROPS +1.
        // QC GameRules_scoring_add_team(player, SCORE, -penalty) docks BOTH the player SP_SCORE and the team ST_SCORE.
        if (ScorePenaltyDrop != 0f)
        {
            carrier.ScoreFrags -= (int)ScorePenaltyDrop;
            AddTeamScore(carrier.Team, -(int)ScorePenaltyDrop);
        }
        AddCol(carrier, "CTF_DROPS", 1); // QC GameRules_scoring_add(player, CTF_DROPS, 1)
        // QC: the dropper can't immediately re-take the flag (next_take_time), and is shielded from camping it.
        carrier.GtNextTakeTime = now + FlagCollectDelay;

        GametypeEntities.ScoringVip(carrier, false); // QC GameRules_scoring_vip(flag.owner, false) (sv_ctf.qc:515)

        // QC ctf_Handle_Drop: the global "flag dropped/lost" voice + the kill-feed line crediting the dropper.
        FlagAnnounceSound(carried.HomeTeam, "DROPPED");
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, $"CTF_LOST_{TeamSuffix(carried.HomeTeam)}", carrier.NetName);

        // Position the world flag entity at the drop point as a tossable pickup (QC MOVETYPE_TOSS, SOLID_TRIGGER).
        if (carried.Entity is Entity fe)
        {
            GametypeEntities.DetachFromCarrier(fe);
            fe.Solid = Solid.Trigger;
            fe.MoveType = MoveType.Toss;
            fe.TakeDamage = DamageMode.Yes;
            fe.Angles = Vector3.Zero;
            fe.GtStatus = (int)FlagStatus.Dropped;
            fe.GtDropTime = now;
            fe.GtLandTime = 0f;
            fe.GtCapturer = carrier;
            GametypeEntities.SetOrigin(fe, carrier.Origin + FlagDropOffset);
            // QC ctf_Handle_Throw DROP_NORMAL (sv_ctf.qc:571): toss the flag up + a randomized sideways kick on top
            // of the carrier's velocity (W_CalculateProjectileVelocity) so it tumbles away from where they fell.
            float up   = Cvar(CvarDropVelocityUp,   DefaultDropVelocityUp);
            float side = Cvar(CvarDropVelocitySide, DefaultDropVelocitySide);
            float cx = Prandom.Float() * 2f - 1f; // QC crandom() ∈ [-1,1]
            float cy = Prandom.Float() * 2f - 1f;
            fe.Velocity = carrier.Velocity + new Vector3(cx * side, cy * side, up);
        }
    }

    /// <summary>The enemy flag <paramref name="player"/> is currently carrying, or null.</summary>
    public FlagState? CarriedBy(Player player)
    {
        foreach (var f in Flags.Values)
            if (f.Status == FlagStatus.Carried && ReferenceEquals(f.Carrier, player))
                return f;
        return null;
    }

    // ============================================================================================
    //  Flag passing / throwing (QC ctf_Handle_Throw / ctf_CalculatePassVelocity / FLAG_PASSING think)
    // ============================================================================================

    /// <summary>
    /// QC ctf_Handle_Throw DROP_THROW (sv_ctf.qc:551): the carrier deliberately throws the flag forward. Gated
    /// by autocvar_g_ctf_throw and the throw-punish ramp (QC throw_count/throw_prevtime: after
    /// g_ctf_throw_punish_count throws within g_ctf_throw_punish_time the player is benched for
    /// g_ctf_throw_punish_delay). Drops the flag with a forward+up velocity (× the Strength multiplier when the
    /// thrower has Strength). Returns true if the throw was performed.
    /// </summary>
    public bool ThrowFlag(Player player)
    {
        if (MatchEnded || !ThrowEnabled)
            return false;
        FlagState? carried = CarriedBy(player);
        if (carried is null)
            return false;

        float now = Now;
        // QC throw_antispam (g_ctf_pass_wait): rate-limit consecutive throws/passes (also enforced in PassFlag).
        if (now < player.GtThrowAntispam)
            return false;
        // QC throw-punish ramp (sv_ctf.qc:2488): -1 means benched; otherwise count throws within the window.
        if (player.GtThrowCount == -1)
        {
            if (now <= player.GtThrowPrevTime + Cvar(CvarThrowPunishDelay, DefaultThrowPunishDelay))
                return false; // still benched
            player.GtThrowPrevTime = now;
            player.GtThrowCount = 1;
        }
        else
        {
            if (now > player.GtThrowPrevTime + Cvar(CvarThrowPunishTime, DefaultThrowPunishTime))
                player.GtThrowCount = 1;
            else
                player.GtThrowCount += 1;
            if (player.GtThrowCount >= (int)Cvar(CvarThrowPunishCount, DefaultThrowPunishCount))
                player.GtThrowCount = -1;
            player.GtThrowPrevTime = now;
        }

        // QC: makevectors((v_angle.y * '0 1 0') + (bound(min,v_angle.x,max) * '1 0 0')); the thrown velocity is
        // up + forward, with the forward leg multiplied by the Strength bonus when the thrower has it.
        float pitch = QClamp(player.Angles.X, Cvar(CvarThrowAngleMin, DefaultThrowAngleMin), Cvar(CvarThrowAngleMax, DefaultThrowAngleMax));
        QMath.AngleVectors(new Vector3(pitch, player.Angles.Y, 0f), out Vector3 forward, out _, out _);
        float strengthMul = HasStrength(player) ? Cvar(CvarThrowStrengthMul, DefaultThrowStrengthMul) : 1f;
        Vector3 vel = new Vector3(0f, 0f, 1f) * Cvar(CvarThrowVelUp, DefaultThrowVelUp)
                    + forward * (Cvar(CvarThrowVelFwd, DefaultThrowVelFwd) * strengthMul);

        ThrowDropCommon(carried, player, FlagDropType.Throw);
        if (carried.Entity is Entity fe)
            fe.Velocity = player.Velocity + vel; // QC W_CalculateProjectileVelocity adds the thrower's velocity
        player.GtThrowAntispam = now + PassWait;
        return true;
    }

    /// <summary>
    /// QC ctf_Handle_Throw DROP_PASS (sv_ctf.qc:525): the carrier passes the flag toward <paramref name="receiver"/>.
    /// Gated by autocvar_g_ctf_pass and the throw antispam. The flag enters FLAG_PASSING, flying toward the
    /// receiver at g_ctf_pass_velocity along <see cref="CalculatePassVelocity"/>; the per-tick
    /// <see cref="DrivePass"/> re-targets it and gives up (becomes dropped) if the target is lost or the
    /// g_ctf_pass_timelimit elapses. Returns true if the pass was launched.
    /// </summary>
    public bool PassFlag(Player passer, Player receiver)
    {
        if (MatchEnded || !PassEnabled || receiver is null)
            return false;
        if (ReferenceEquals(passer, receiver) || (int)receiver.Team != (int)passer.Team || receiver.IsDead)
            return false;
        FlagState? carried = CarriedBy(passer);
        if (carried is null)
            return false;
        if (CarriedBy(receiver) is not null)
            return false; // QC: can't pass to someone already carrying a flag

        float now = Now;
        // QC throw_antispam (g_ctf_pass_wait): rate-limit consecutive throws/passes.
        if (now < passer.GtThrowAntispam)
            return false;
        // QC pass_distance = planar (XY) distance passer→receiver.
        Vector3 d = receiver.Origin - passer.Origin; d.Z = 0f;
        carried.PassDistance = d.Length();

        ThrowDropCommon(carried, passer, FlagDropType.Pass);
        carried.Status = FlagStatus.Passing;     // QC FLAG_PASSING
        carried.PassSender = passer;
        carried.PassTarget = receiver;
        carried.DropTime = now;                  // QC ctf_droptime — the give-up timer baseline

        if (carried.Entity is Entity fe)
        {
            fe.GtStatus = (int)FlagStatus.Passing;
            fe.TakeDamage = DamageMode.No;       // QC: a passing flag can't be shot down
            fe.MoveType = MoveType.Fly;          // QC MOVETYPE_FLY
            fe.GtCapturer = passer;              // remember the sender (credited on retrieve)
            fe.Velocity = CalculatePassVelocity(fe.Origin, receiver.Origin, carried.PassDistance, fe.Velocity, turnrate: false);
        }
        passer.GtThrowAntispam = now + PassWait;
        return true;
    }

    /// <summary>
    /// QC ctf_PrepareNeutralFlags-style +use pass request (sv_ctf.qc:2452, ctf_CheckPassDirection slice): a
    /// teammate <paramref name="requester"/> asks the nearest in-radius flag carrier to pass to them. With
    /// g_ctf_pass_request set and the requester empty-handed, the closest same-team carrier within
    /// g_ctf_pass_radius passes to them. Returns true if a pass was triggered.
    /// </summary>
    public bool RequestPass(Player requester, IReadOnlyList<Player> roster)
    {
        if (MatchEnded || !PassEnabled || !PassRequestEnabled)
            return false;
        if (CarriedBy(requester) is not null)
            return false;
        float radius = PassRadius;
        Player? best = null;
        float bestDist = radius;
        for (int i = 0; i < roster.Count; i++)
        {
            Player it = roster[i];
            if (ReferenceEquals(it, requester) || (int)it.Team != (int)requester.Team || it.IsDead)
                continue;
            if (CarriedBy(it) is null)
                continue;
            float dist = (it.Origin - requester.Origin).Length();
            if (dist <= bestDist)
            {
                bestDist = dist;
                best = it;
            }
        }
        if (best is null)
            return false;
        return PassFlag(best, requester);
    }

    /// <summary>
    /// QC ctf_Handle_Retrieve (sv_ctf.qc:428): an in-flight passed flag reaches its target — transfer it to the
    /// receiver as a normal carry. Used by <see cref="DrivePass"/> when the flag arrives.
    /// </summary>
    public void RetrieveFlag(FlagState flag, Player receiver)
    {
        flag.Status = FlagStatus.Carried;
        flag.Carrier = receiver;
        flag.PassTarget = null;
        flag.PassSender = null;
        flag.PassDistance = 0f;
        if (flag.Entity is Entity fe)
        {
            GametypeEntities.AttachToCarrier(fe, receiver, FlagCarryOffset);
            fe.GtStatus = (int)FlagStatus.Carried;
        }
        GametypeEntities.ScoringVip(receiver, true); // QC GameRules_scoring_vip(player, true) (sv_ctf.qc:435)
        // QC ctf_Handle_Retrieve: the flag has a NEW carrier now — re-attach the live carrier waypoint (health bar)
        // to the receiver (EnsureCarrierWaypoint kills any prior sprite first, so the marker follows the new owner).
        EnsureCarrierWaypoint(flag, receiver);
        // QC ctf_Handle_Retrieve (sv_ctf.qc:456): _sound(player, …, snd_flag_pass, ATTEN_NORM) — the positional
        // "pass received" cue on the receiver as the in-flight pass completes.
        if (Api.Services is not null)
            SoundSystem.PlayOn(receiver, Sounds.ByName("CTF_PASS"));
    }

    /// <summary>
    /// QC ctf_CalculatePassVelocity (sv_ctf.qc:202): the velocity for an in-flight passed flag — a straight
    /// shot toward the target at g_ctf_pass_velocity, optionally blended with the current direction by
    /// g_ctf_pass_turnrate while in flight (<paramref name="turnrate"/>). The arc-height trace fallback is a
    /// presentation refinement (it only nudges the aim point when line-of-sight is blocked); headlessly we aim
    /// straight at the target, which is the faithful flight outcome.
    /// </summary>
    public Vector3 CalculatePassVelocity(Vector3 from, Vector3 to, float passDistance, Vector3 currentVelocity, bool turnrate)
    {
        _ = passDistance; // (QC uses it only for the arc-height trace fallback, deferred — see XML doc)
        Vector3 desired = QNormalize(to - from);
        if (turnrate)
        {
            float tr = Cvar("g_ctf_pass_turnrate", 50f);
            Vector3 blended = QNormalize(QNormalize(currentVelocity) + desired * tr);
            return blended * PassVelocity;
        }
        return desired * PassVelocity;
    }

    /// <summary>
    /// QC the FLAG_PASSING case of ctf_FlagThink (sv_ctf.qc:1102), evaluated headlessly: each tick, give up the
    /// pass (become a dropped flag) if the target is gone/dead/now-carrying, out of g_ctf_pass_radius, or past
    /// the g_ctf_pass_timelimit (QC the give-up branch → ctf_Handle_Drop DROP_PASS); otherwise the pass is still
    /// viable and the receiver collects it (QC ctf_FlagTouch FLAG_PASSING → ctf_Handle_Retrieve).
    ///
    /// NOTE (deferral, per spec): without a physics toss/fly integrator the flag entity doesn't actually travel,
    /// so a viable pass completes as an instant transfer to the receiver here (the faithful OUTCOME) rather than
    /// faking multi-tick projectile flight. The flight velocity is still computed (so the entity carries the
    /// right initial velocity for any host that does integrate), and the give-up conditions are honored exactly.
    /// </summary>
    private void DrivePass(FlagState flag, float now)
    {
        if (flag.Status != FlagStatus.Passing)
            return;
        Player? target = flag.PassTarget;
        Entity? fe = flag.Entity;
        Vector3 flagPos = fe?.Origin ?? flag.DropOrigin;
        float dist = target is not null ? (flagPos - target.Origin).Length() : float.MaxValue;

        // QC give-up conditions: no/dead/already-carrying target, out of radius, or past the time limit.
        bool giveUp = target is null
            || target.IsDead
            || CarriedBy(target) is not null
            || dist > PassRadius
            || now > flag.DropTime + PassTimelimit;

        if (giveUp)
        {
            // QC: ctf_Handle_Drop(this, NULL, DROP_PASS) — the pass failed; the flag becomes a normal dropped flag.
            flag.Status = FlagStatus.Dropped;
            flag.Carrier = null;
            flag.DropTime = now;
            flag.DropOrigin = flagPos;
            flag.Dropper = flag.PassSender;
            flag.PassTarget = null;
            flag.PassSender = null;
            flag.PassDistance = 0f;
            if (fe is not null)
            {
                fe.GtStatus = (int)FlagStatus.Dropped;
                fe.MoveType = MoveType.Toss;
                fe.TakeDamage = DamageMode.Yes;
                fe.GtDropTime = now;
                fe.Velocity = Vector3.Zero;
            }
            return;
        }

        // Still viable → the receiver collects the pass (QC ctf_Handle_Retrieve). Degraded to an instant transfer
        // (see the NOTE) since the entity doesn't physically travel headlessly.
        RetrieveFlag(flag, target!);
    }

    /// <summary>
    /// The shared head of ctf_Handle_Throw (sv_ctf.qc:479): detach the flag from the carrier, place it at the
    /// carrier's feet as a tossable trigger, clear the carry back-links, and record the dropper/drop-time. The
    /// per-droptype velocity (throw/pass/normal) is set by the caller afterward.
    /// </summary>
    private void ThrowDropCommon(FlagState flag, Player carrier, FlagDropType droptype)
    {
        float now = Now;
        flag.Status = FlagStatus.Dropped;
        flag.Carrier = null;
        flag.DropOrigin = carrier.Origin;
        flag.DropTime = now;
        flag.Dropper = carrier;
        // QC: the live carrier waypoint dies as the flag leaves the carrier (throw → dropped, pass → in-flight; a
        // PASSING flag has no carrier marker — the receiver gets a fresh one on RetrieveFlag).
        KillCarrierWaypoint(flag);
        carrier.GtNextTakeTime = now + FlagCollectDelay; // QC: dropper can't instantly re-take
        GametypeEntities.ScoringVip(carrier, false); // QC GameRules_scoring_vip(flag.owner, false) on throw/pass

        if (flag.Entity is Entity fe)
        {
            GametypeEntities.DetachFromCarrier(fe);
            fe.Solid = Solid.Trigger;
            fe.MoveType = MoveType.Toss;
            fe.TakeDamage = DamageMode.Yes;
            fe.Angles = Vector3.Zero;
            fe.GtStatus = (int)FlagStatus.Dropped;
            fe.GtDropTime = now;
            fe.GtLandTime = 0f;
            fe.GtCapturer = carrier;
            GametypeEntities.SetOrigin(fe, carrier.Origin + FlagDropOffset);
        }

        // QC ctf_Handle_Throw routing: DROP_THROW falls through to ctf_Handle_Drop (the same body as a death/
        // disconnect DROP_NORMAL — INFO_CTF_LOST kill-feed line, the global snd_flag_dropped voice, and the
        // drop SCORE penalty + CTF_DROPS). DROP_PASS does NOT go through ctf_Handle_Drop; instead it plays the
        // positional snd_flag_touch on the passer (sv_ctf.qc:545) — the throw/pass audio that was absent.
        switch (droptype)
        {
            case FlagDropType.Throw:
                if (ScorePenaltyDrop != 0f)
                {
                    carrier.ScoreFrags -= (int)ScorePenaltyDrop;
                    AddTeamScore(carrier.Team, -(int)ScorePenaltyDrop); // QC GameRules_scoring_add_team(player, SCORE, -penalty)
                }
                AddCol(carrier, "CTF_DROPS", 1); // QC GameRules_scoring_add(player, CTF_DROPS, 1)
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, $"CTF_LOST_{TeamSuffix(flag.HomeTeam)}", carrier.NetName);
                FlagAnnounceSound(flag.HomeTeam, "DROPPED"); // QC _sound(flag, …, snd_flag_dropped, ATTEN_NONE)
                break;
            case FlagDropType.Pass:
                // QC _sound(player, CH_TRIGGER, flag.snd_flag_touch, VOL_BASE, ATTEN_NORM): positional on the passer.
                if (Api.Services is not null)
                    SoundSystem.PlayOn(carrier, Sounds.ByName("CTF_TOUCH"));
                break;
        }
    }

    /// <summary>QC StatusEffects_active(STATUSEFFECT_Strength, player) — the thrower has the Strength powerup.</summary>
    private static bool HasStrength(Player p)
        => StatusEffectsCatalog.ByName("strength") is { } s && StatusEffectsCatalog.Has(p, s);

    /// <summary>QC bound(lo, v, hi).</summary>
    private static float QClamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>
    /// QC <c>CTF_SAMETEAM(a,b)</c> (sv_ctf.qh:185): <c>(g_ctf_reverse || (ctf_oneflag &amp;&amp; g_ctf_oneflag_reverse))
    /// ? DIFF_TEAM(a,b) : SAME_TEAM(a,b)</c>. In a reverse mode the bases swap ownership, so "same team" toward a
    /// flag/shield flips to "different team". Used by the capture-shield touch (and any other CTF_SAMETEAM gate).
    /// </summary>
    private bool CtfSameTeam(int a, int b)
    {
        bool reverse = (TryCvar("g_ctf_reverse", out float rv) && rv != 0f) || (OneFlag && OneFlagReverse);
        return reverse ? a != b : a == b;
    }

    /// <summary>QC normalize(v) — zero for the zero vector.</summary>
    private static Vector3 QNormalize(Vector3 v) { float l = v.Length(); return l > 0f ? v / l : Vector3.Zero; }

    /// <summary>Current sim time (QC time); 0 with no facade.</summary>
    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;

    // ============================================================================================
    //  Flag presentation (QC ctf_FlagSetup model/skin/glow + the _sound / Send_Notification calls)
    // ============================================================================================

    /// <summary>EF_FULLBRIGHT (dpextensions) — set only when g_ctf_fullbrightflags (Base default 0 = off).</summary>
    private const int EfFullbright = 512;
    /// <summary>EF_LOWPRECISION (dpextensions.qc:274) — bandwidth hint; QC ctf_FlagSetup always sets it on the flag.</summary>
    private const int EfLowPrecision = 4194304;
    /// <summary>EF_ADDITIVE (dpextensions.qc:93) — additive-blend render mode; QC ctf_CaptureShield_Spawn uses it.</summary>
    private const int EfAdditive = 32;

    /// <summary>Lowercase team token for the <c>g_ctf_flag_&lt;team&gt;_*</c> cvars (red/blue/yellow/pink/neutral).</summary>
    private static string TeamName(int team) => team switch
    {
        Teams.Red => "red", Teams.Blue => "blue", Teams.Yellow => "yellow", Teams.Pink => "pink", _ => "neutral",
    };

    /// <summary>UPPERCASE team suffix used by the registered CTF sounds + notifications (CTF_TAKEN_RED, …).</summary>
    private static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "NEUTRAL",
    };

    /// <summary>Default per-team flag skin (gametypes-server.cfg: red=0/blue=1/yellow=2/pink=3/neutral=4).</summary>
    private static int DefaultFlagSkin(int team) => team switch
    {
        Teams.Red => 0, Teams.Blue => 1, Teams.Yellow => 2, Teams.Pink => 3, _ => 4,
    };

    /// <summary>Read a string cvar with a fallback (facade-guarded; used for the flag model path).</summary>
    private static string CvarStr(string name, string def)
    {
        if (Api.Services is null) return def;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? def : s;
    }

    /// <summary>
    /// QC ctf_FlagSetup audio: play one of the registered CTF flag voices GLOBALLY (QC <c>_sound(flag, …,
    /// ATTEN_NONE)</c> — heard by everyone, the flag "announcer"). <paramref name="evt"/> is the event token
    /// (TAKEN / CAPTURE / DROPPED / RETURNED); the sound is keyed by the flag's home team. No-op headlessly.
    /// </summary>
    private static void FlagAnnounceSound(int flagTeam, string evt)
    {
        if (Api.Services is null) return;
        SoundSystem.PlayGlobal(Sounds.ByName($"CTF_{evt}_{TeamSuffix(flagTeam)}"));
    }

    /// <summary>
    /// QC ctf_CaptureRecord (sv_ctf.qc:113): broadcast the capture, including the fastest-cap time. One-flag mode
    /// fires the plain INFO_CTF_CAPTURE_NEUTRAL; otherwise it fires the MSG_CHOICE CHOICE_CTF_CAPTURE_TIME /
    /// _BROKEN / _UNBROKEN (the client picks the plain "captured the flag" or the timed variant). The in-process
    /// fastest-cap record (<see cref="_ctfCapTimeRecord"/>) is updated when beaten; the persistent ServerProgsDB
    /// record + cross-session leaderboard are out of scope. <paramref name="capTime"/> is the carry duration (s).
    /// </summary>
    private void CaptureRecord(int flagTeam, Player player, float capTime)
    {
        bool validRecord = capTime > 0.01f;
        float capRecord = _ctfCapTimeRecord;
        string refername = _ctfCapTimeRecordHolder;
        string suffix = TeamSuffix(flagTeam);

        if (OneFlag)
        {
            // QC: one-flag mode shows the plain neutral capture line (no per-team record).
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "CTF_CAPTURE_NEUTRAL", player.NetName);
        }
        else if (capRecord <= 0f || !validRecord)
        {
            // QC CHOICE_CTF_CAPTURE_TIME: first cap (no record yet) → the plain/timed capture line (netname, time).
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Choice, $"CTF_CAPTURE_TIME_{suffix}",
                player.NetName, Scoring.GameScores.TimeEncode(capTime));
        }
        else if (capTime < capRecord)
        {
            // QC CHOICE_CTF_CAPTURE_BROKEN: a new record (netname, refername, new time, old record).
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Choice, $"CTF_CAPTURE_BROKEN_{suffix}",
                player.NetName, refername, Scoring.GameScores.TimeEncode(capTime), Scoring.GameScores.TimeEncode(capRecord));
        }
        else
        {
            // QC CHOICE_CTF_CAPTURE_UNBROKEN: failed to beat the record (netname, refername, this time, record).
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Choice, $"CTF_CAPTURE_UNBROKEN_{suffix}",
                player.NetName, refername, Scoring.GameScores.TimeEncode(capTime), Scoring.GameScores.TimeEncode(capRecord));
        }

        // QC: update the in-process fastest-cap record when this is the first valid cap or it beat the record.
        if (!OneFlag && validRecord && (capRecord <= 0f || capTime < capRecord))
        {
            _ctfCapTimeRecord = capTime;
            _ctfCapTimeRecordHolder = player.NetName;
        }
    }

    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        // QC PlayerDies: killing an enemy flag carrier rewards the attacker's team (g_ctf_score_kill);
        // a teamkill of a carrier is penalized.
        if (attacker is not null && !ReferenceEquals(attacker, victim) && CarriedBy(victim) is not null)
        {
            float kill = ScoreKill;
            attacker.ScoreFrags += Teams.SameTeam(attacker, victim) ? -(int)kill : (int)kill;
            AddCol(attacker, "CTF_FCKILLS", 1); // QC GameRules_scoring_add(frag_attacker, CTF_FCKILLS, 1)
        }

        // The victim drops any flag they were carrying (and the world entity falls to the ground).
        DropFlag(victim);

        // CTF respawns normally (it is not elimination); arm the respawn timer (QC calculate_respawntime).
        GametypeEntities.ScheduleRespawn(victim);
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ctf, Damage_Calculate) (sv_ctf.qc:2312): if the ATTACKER carries a flag, scale the
    /// outgoing damage/force by the flagcarrier self/other factors (all 1 by default, so no change at stock); if the
    /// TARGET is an enemy flag carrier whose effective health has dropped below g_ctf_flagcarrier_auto_helpme_damage,
    /// auto-ping a "help me" on their carrier waypoint (antispam g_ctf_flagcarrier_auto_helpme_time).
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs a)
    {
        Player? attacker = a.Attacker as Player;
        if (a.Target is not Player target)
            return false;

        if (attacker is not null && CarriedBy(attacker) is not null) // attacker is a flag carrier
        {
            if (ReferenceEquals(target, attacker)) // damage done to yourself
            {
                a.Damage *= Cvar(CvarFcSelfDamage, 1f);
                a.Force  *= Cvar(CvarFcSelfForce, 1f);
            }
            else // damage done to everyone else
            {
                a.Damage *= Cvar(CvarFcDamage, 1f);
                a.Force  *= Cvar(CvarFcForce, 1f);
            }
        }
        else if (CarriedBy(target) is { } targetFlag && !target.IsDead
                 && attacker is not null && (int)target.Team != (int)attacker.Team) // the target is an enemy flag carrier
        {
            // QC MUTATOR_HOOKFUNCTION(ctf, Damage_Calculate) (sv_ctf.qc:2337): if the carrier's effective HP has
            // dropped BELOW g_ctf_flagcarrier_auto_helpme_damage AND it's been at least g_ctf_flagcarrier_auto_helpme_time
            // since the last auto-ping (antispam), flash HELP ME on their LIVE carrier waypoint. effective HP =
            // healtharmor_maxdamage(health, armor, ...); the port approximates it with health + armor.
            float effHp = HealthArmorMaxDamage(target);
            float now = Now;
            float window = Cvar(CvarFcHelpMeTime, DefaultFcHelpMeTime);
            if (Cvar(CvarFcHelpMeDamage, DefaultFcHelpMeDamage) > effHp
                && now > target.GtHelpMeTime + window)
            {
                target.GtHelpMeTime = now; // QC frag_target.wps_helpme_time = time (antispam baseline)
                // QC WaypointSprite_HelpMePing(frag_target.wps_flagcarrier): flash the carrier waypoint's helpme
                // state for the antispam window. Also stamp GtHelpMeUntil so the enemy-FC reveal snapshot lights up.
                Waypoints.WaypointSprites.HelpMePing(targetFlag.CarrierWaypoint, window);
                target.GtHelpMeUntil = now + window;
            }
        }
        return false;
    }

    /// <summary>QC <c>GameRules_scoring_add_team(player, CTF_CAPS, delta)</c>'s team side: add to a team's
    /// ST_CTF_CAPS total (GameScores team slot 1 — the team primary).</summary>
    public void AddTeamCaps(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None)
            return;
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotSecondary, delta);
    }

    public int GetTeamCaps(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on captures (ST_CTF_CAPS,
    /// CTF's ranking primary), so a tied timed CTF enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(Scoring.GameScores.LeaderTeam(), Scoring.GameScores.SecondTeam(), GetTeamCaps);

    /// <summary>QC <c>GameRules_scoring_add_team(player, SCORE, delta)</c>'s team side: add to a team's ST_SCORE
    /// total (GameScores team slot 0). CTF tracks this as the team secondary (stprio=0 in ctf_ScoreRules).</summary>
    public void AddTeamScore(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None)
            return;
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotScore, delta);
    }

    /// <summary>
    /// QC ctf_Handle_Return penalty (sv_ctf.qc: <c>TeamScore_AddToTeam(team, ST_SCORE, -penalty)</c>): dock a
    /// team's SCORE (slot 0), NOT its capture total — ST_CTF_CAPS is the win-condition primary and must not move
    /// on a return. QC allows team SCORE to go negative, so no floor.
    /// </summary>
    public void AddTeamScorePenalty(int team, int penalty)
    {
        if (team == Teams.None || penalty == 0)
            return;
        Scoring.GameScores.AddToTeam(team, Scoring.GameScores.TeamSlotScore, -penalty);
    }

    // ============================================================================================
    //  Flag ENTITY layer (QC ctf_FlagSetup / ctf_FlagTouch / ctf_FlagThink / ctf_RespawnFlag)
    // ============================================================================================

    /// <summary>
    /// QC ctf_FlagSetup (spawnfunc item_flag_team*): create a world flag entity for <paramref name="team"/>
    /// at <paramref name="origin"/>, register it in <see cref="FlagEntities"/> + <see cref="Flags"/>, and
    /// wire its touch/think so the carry/drop/return/capture state machine runs on the entity. Returns the
    /// <see cref="FlagState"/> (with its <see cref="FlagState.Entity"/> set when a facade is present).
    /// </summary>
    public FlagState SpawnFlag(int team, Vector3 origin, Vector3 angles = default)
    {
        // QC ctf_FlagSetup (sv_ctf.qc:1426): the flag rests at (map origin + FLAG_SPAWN_OFFSET), a fixed
        // vertical lift of PL_MAX_CONST.z − 13 = 45 − 13 = 32qu, so the flag model sits ON the floor instead of
        // sinking into it. PL_MAX_CONST is the compile-time player-hull max (a constant, not the cvar), so the
        // lift is a constant 32. Applied to `origin` here so the stored HomeOrigin/spawn origin carries it too
        // (playtest-bugs #15).
        origin += new Vector3(0f, 0f, 32f);
        if (!Flags.TryGetValue(team, out FlagState? flag))
        {
            flag = new FlagState(team);
            Flags[team] = flag;
        }
        if (team != Teams.None)
            Scoring.GameScores.SetTeamScore(team, Scoring.GameScores.TeamSlotSecondary,
                Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary)); // ensure a zeroed caps slot

        Entity? e = GametypeEntities.SpawnObjective("item_flag_team", origin, team, FlagMins, FlagMaxs,
            touch: FlagTouchEntity, think: FlagThinkEntity);
        if (e is not null)
        {
            e.Angles = angles;
            e.GtSpawnAngles = angles;
            e.GtStatus = (int)FlagStatus.AtBase;
            e.NextThink = GametypeEntities.Now + 0.2f; // QC FLAG_THINKRATE
            // QC ctf_FlagSetup: flag.reset = ctf_Reset (sv_ctf.qc:1380). The engine round/map reset
            // (GameWorld.ResetMapObjects → Entity.Reset) fires this so a carried/dropped/passing flag is
            // force-thrown (DROP_RESET) and respawned at base on an in-place round/map reset.
            e.Reset = FlagResetEntity;

            // QC ctf_FlagSetup: give the flag its MODEL + per-team SKIN from the g_ctf_flag_<team>_model / _skin
            // cvars (gametypes-server.cfg: models/ctf/flags.md3, skin 0..4 = red/blue/yellow/pink/neutral). The
            // entity layer previously left Model empty, so the flag networked as an invisible point — the cause
            // of "flags not visible" despite the scoreboard logic working. EF_FULLBRIGHT keeps it clearly lit.
            string tn = TeamName(team);
            e.Model = CvarStr($"g_ctf_flag_{tn}_model", "models/ctf/flags.md3");
            e.Skin = GametypeEntities.TryCvar($"g_ctf_flag_{tn}_skin", out float sk) ? sk : DefaultFlagSkin(team);
            // QC ctf_FlagSetup (sv_ctf.qc:1442): the flag always gets EF_LOWPRECISION (bandwidth hint), and
            // EF_FULLBRIGHT ONLY when g_ctf_fullbrightflags is set (Base default 0 = off — the flag lights from the
            // world like any model). The glow trail (g_ctf_flag_glowtrails) + dynamic team light (g_ctf_dynamiclights)
            // need entity glow/dlight fields the port doesn't network yet, so they stay deferred.
            e.Effects |= EfLowPrecision;
            if (Cvar("g_ctf_fullbrightflags", 0f) != 0f)
                e.Effects |= EfFullbright;

            FlagEntities.Add(e);
            // QC ctf_DelayedFlagSetup → ctf_CaptureShield_Spawn(this): spawn the push entity that physically
            // blocks a shielded player from approaching the flag. This is separate from the shield status gate
            // (GtCaptureShielded) which blocks the pickup; the push entity is the VISIBLE shield presence.
            SpawnShieldEntity(e, team, origin);
        }
        flag.Entity = e;
        flag.HomeOrigin = origin;
        flag.Status = FlagStatus.AtBase;
        return flag;
    }

    /// <summary>
    /// QC spawnfunc item_flag_neutral: create the single neutral flag for one-flag CTF (team = <see cref="Teams.None"/>).
    /// Marks the gametype one-flag. The neutral flag is the only carriable objective in this mode; team flags
    /// (spawned via <see cref="SpawnFlag"/>) become capture-point bases.
    /// </summary>
    public FlagState SpawnNeutralFlag(Vector3 origin, Vector3 angles = default)
    {
        OneFlag = true;
        return SpawnFlag(Teams.None, origin, angles);
    }

    /// <summary>The <see cref="FlagState"/> bound to a world flag <see cref="Entity"/>, or null.</summary>
    public FlagState? FlagForEntity(Entity e)
    {
        foreach (var f in Flags.Values)
            if (ReferenceEquals(f.Entity, e)) return f;
        return null;
    }

    /// <summary>
    /// QC <c>ctf_CaptureShield_Spawn(flag)</c> (sv_ctf.qc:340): spawn a co-located SOLID_TRIGGER entity at the
    /// flag's origin that pushes away any shielded enemy player who touches it. The entity is <c>MOVETYPE_NOCLIP</c>
    /// so it stays put regardless of physics; its <c>EF_ADDITIVE</c> flag gives it an additive-blend shader glow.
    /// Scale 0.5 matches the QC (the model's natural bbox is halved). The shield entity stores a back-link to the
    /// flag entity in <c>GtShieldFlag</c> (QC <c>shield.enemy = flag</c>) so the touch handler can read the team.
    /// </summary>
    private void SpawnShieldEntity(Entity flagEnt, int team, Vector3 origin)
    {
        if (Api.Services is null)
            return;
        Entity s = Api.Entities.Spawn();
        s.ClassName = "ctf_captureshield";
        s.Team = team;
        s.GtHomeTeam = team;
        s.GtShieldFlag = flagEnt; // QC shield.enemy = flag
        s.Solid = Solid.Trigger;
        s.MoveType = MoveType.Noclip;
        // playtest-bugs #4: the shield MODEL must not render in normal play. QC gates its visibility with
        // ctf_CaptureShield_Customize — shown ONLY to a client who is currently capture-shielded vs the enemy
        // team; with the default g_ctf_shield_max_ratio 0 nobody is ever shielded, so the dome is invisible. The
        // port has no per-viewer model customize, so gate the visible model on the feature being enabled at all:
        // at ratio<=0 the shield is a pure (invisible) push-trigger and no giant purple dome appears. (When
        // ratio>0 it shows to everyone rather than only shielded players — a per-viewer Customize, the 0.5 visual
        // scale the port can't network yet, and the additive/env shield material are follow-ups; see #4 notes.)
        if (_captureShieldMaxRatio > 0f)
        {
            s.Effects |= EfAdditive;
            s.AVelocity = new Vector3(7f, 0f, 11f); // QC avelocity '7 0 11'
            s.Model = "models/ctf/shield.md3"; // QC MDL_CTF_SHIELD = "models/ctf/shield.md3"
        }
        // QC setsize(shield, shield.scale * shield.mins, shield.scale * shield.maxs): the (invisible) push-trigger
        // hull is the flag bbox halved. scale 0.5.
        const float scale = 0.5f;
        Vector3 scaledMins = FlagMins * scale;
        Vector3 scaledMaxs = FlagMaxs * scale;
        GametypeEntities.SetSize(s, scaledMins, scaledMaxs);
        GametypeEntities.SetOrigin(s, origin);
        s.Touch = ShieldTouchEntity;
    }

    /// <summary>
    /// QC <c>ctf_CaptureShield_Touch</c> (sv_ctf.qc:328): when a shielded player touches the shield entity,
    /// push them away (0 damage, <c>g_ctf_shield_force</c> impulse along the outward normal) and remind them
    /// they are shielded (CENTER_CTF_CAPTURESHIELD_SHIELDED). Same-team players are never pushed.
    /// </summary>
    private void ShieldTouchEntity(Entity self, Entity other)
    {
        if (other is not Player toucher || toucher.IsDead)
            return;
        // QC: if(!toucher.ctf_captureshielded) { return; }
        if (!toucher.GtCaptureShielded)
            return;
        // QC: if(CTF_SAMETEAM(this, toucher)) { return; } — same-team players pass through freely. CTF_SAMETEAM is
        // reverse-aware (sv_ctf.qh:185): under g_ctf_reverse / one-flag-reverse it flips to DIFF_TEAM, so the shield
        // pushes your OWN team and lets enemies pass (the bases swap ownership). A plain == comparison would push the
        // wrong team in those modes.
        if (CtfSameTeam((int)self.Team, (int)toucher.Team))
            return;

        // QC: mymid = (this.absmin + this.absmax) * 0.5; theirmid = (toucher.absmin + toucher.absmax) * 0.5;
        //     Damage(toucher, this, this, 0, DEATH_HURTTRIGGER.m_id, DMG_NOWEP, mymid,
        //            normalize(theirmid - mymid) * ctf_captureshield_force);
        Vector3 myMid     = (self.AbsMin + self.AbsMax) * 0.5f;
        Vector3 theirMid  = (toucher.AbsMin + toucher.AbsMax) * 0.5f;
        Vector3 pushDir   = QNormalize(theirMid - myMid);
        float   force     = _captureShieldForce;
        Combat.Damage(toucher, self, self, 0f, DeathTypes.Void, myMid, pushDir * force);

        // QC: if(IS_REAL_CLIENT(toucher)) { Send_Notification(NOTIF_ONE, toucher, MSG_CENTER,
        //         CENTER_CTF_CAPTURESHIELD_SHIELDED); }
        NotificationSystem.Center(toucher, "CTF_CAPTURESHIELD_SHIELDED");
    }

    /// <summary>
    /// QC ctf_RespawnFlag: snap a flag's world entity back to its home base (solid trigger, no carrier,
    /// home angles). Mirrors <see cref="FlagState.ResetToBase"/> on the entity side.
    /// </summary>
    public void RespawnFlagEntity(FlagState flag)
    {
        if (flag.Entity is not Entity e)
            return;
        GametypeEntities.DetachFromCarrier(e);
        e.Solid = Solid.Trigger;
        e.MoveType = MoveType.None;
        e.TakeDamage = DamageMode.No;
        e.Velocity = Vector3.Zero;
        e.Angles = e.GtSpawnAngles;
        e.GtStatus = (int)FlagStatus.AtBase;
        e.GtCarrier = null;
        e.GtCapturer = null;
        e.GtDropTime = 0f;
        e.GtLandTime = 0f;
        GametypeEntities.SetOrigin(e, e.GtSpawnOrigin);
    }

    /// <summary>
    /// QC ctf_Reset (sv_ctf.qc:1298), installed as the flag entity's <c>.reset</c> hook (ctf_FlagSetup:1380) and
    /// fired by the engine round/map reset (GameWorld.ResetMapObjects). If a player is carrying the flag it is
    /// force-thrown with DROP_RESET (no toss velocity), then the flag is respawned at its home base. This is what
    /// restores a carried/dropped/passing flag's at-base state on an in-place round/map reset that doesn't re-run
    /// Activate(). QC also clears flag.enemy (the takedamage-return back-link); the port has no flag-health bleed
    /// so there is no enemy back-link to clear.
    /// </summary>
    private void FlagResetEntity(Entity self)
    {
        FlagState? flag = FlagForEntity(self);
        if (flag is null)
            return;

        // QC: if(this.owner && IS_PLAYER(this.owner)) ctf_Handle_Throw(this.owner, NULL, DROP_RESET);
        // DROP_RESET drops the flag in place with ZERO velocity (a force reset) and clears the carrier's VIP +
        // collect-delay; the subsequent ctf_RespawnFlag then snaps it home. We collapse the throw+respawn into the
        // direct reset since the immediately-following ResetToBase makes the in-place drop unobservable.
        if (flag.Status == FlagStatus.Carried && flag.Carrier is { } carrier)
            GametypeEntities.ScoringVip(carrier, false); // QC GameRules_scoring_vip(flag.owner, false) (DROP_RESET → Handle_Throw)

        // QC ctf_RespawnFlag(this): clear the POJO + entity state and return the flag to its home base.
        KillCarrierWaypoint(flag); // a force-reset carried flag leaves its carrier — kill the live carrier waypoint
        flag.ResetToBase();
        RespawnFlagEntity(flag);
    }

    /// <summary>
    /// QC ctf_FlagTouch → Flag.giveTo: the world flag <paramref name="flagEnt"/> was touched by
    /// <paramref name="toucher"/>. Drives the FLAG_BASE/FLAG_DROPPED touch dispatch — capture an enemy flag
    /// at your base, return your own dropped flag, or pick up a takeable flag — honoring the capture shield
    /// and the collect-delay. This is the entity-facing counterpart of the POJO Pickup/Capture/ReturnFlag.
    /// </summary>
    public void FlagTouch(Entity flagEnt, Player toucher)
    {
        if (MatchEnded || toucher.IsDead)
            return;
        FlagState? flag = FlagForEntity(flagEnt);
        if (flag is null)
            return;

        int toucherTeam = (int)toucher.Team;
        bool sameTeam = flag.HomeTeam == toucherTeam;
        bool isNeutral = flag.HomeTeam == Teams.None;
        FlagState? carried = CarriedBy(toucher);

        // One-flag CTF: only the NEUTRAL flag is carriable; team flags are capture-point bases. A carrier scores
        // by touching their own team's base flag (QC CTF_DIFFTEAM(player, flag) return → capture at same team),
        // or an ENEMY base flag under g_ctf_oneflag_reverse.
        if (OneFlag)
        {
            switch (flag.Status)
            {
                case FlagStatus.AtBase:
                    if (isNeutral)
                    {
                        // grab the neutral flag from its base (hands free, not shielded, past collect delay).
                        if (carried is null && !toucher.GtCaptureShielded && GametypeEntities.Now >= toucher.GtNextTakeTime)
                            Pickup(toucher, flag);
                    }
                    else if (carried is not null && ReferenceEquals(carried, NeutralFlag))
                    {
                        // touching a team base flag while carrying the neutral flag → capture, at own base
                        // (default) or an enemy base (reverse).
                        bool atCaptureBase = OneFlagReverse ? !sameTeam : sameTeam;
                        if (atCaptureBase)
                            Capture(toucher);
                    }
                    break;

                case FlagStatus.Dropped:
                    // only the neutral flag can be dropped; reclaim it (any team), honoring the dropper delay.
                    if (isNeutral && carried is null
                        && (!ReferenceEquals(flag.Dropper, toucher) || GametypeEntities.Now > flag.DropTime + FlagCollectDelay))
                        Pickup(toucher, flag);
                    break;
            }
            return;
        }

        switch (flag.Status)
        {
            case FlagStatus.AtBase:
                // Same team + carrying an enemy flag → capture it home (QC CTF_SAMETEAM && flagcarried).
                if (sameTeam && carried is not null && carried.HomeTeam != toucherTeam)
                {
                    Capture(toucher);
                }
                // Enemy flag at base, hands free, not shielded, past collect delay → steal it.
                else if (!sameTeam && carried is null && !toucher.GtCaptureShielded
                         && GametypeEntities.Now >= toucher.GtNextTakeTime)
                {
                    Pickup(toucher, flag);
                }
                break;

            case FlagStatus.Dropped:
                // Your own dropped flag → return it (QC ctf_Immediate_Return_Allowed path).
                if (sameTeam)
                {
                    ReturnFlag(toucher, flag);
                }
                // An enemy's dropped flag, hands free, not the recent dropper (or delay elapsed) → pick up.
                else if (carried is null
                         && (!ReferenceEquals(flag.Dropper, toucher)
                             || GametypeEntities.Now > flag.DropTime + FlagCollectDelay))
                {
                    Pickup(toucher, flag);
                }
                break;

            case FlagStatus.Passing:
                // QC ctf_FlagTouch FLAG_PASSING (sv_ctf.qc:2280): the intended receiver catching the in-flight
                // pass collects it (ctf_Handle_Retrieve). Any other toucher is ignored — a passed flag is only
                // for its target.
                if (ReferenceEquals(flag.PassTarget, toucher) && carried is null)
                    RetrieveFlag(flag, toucher);
                break;
        }
    }

    /// <summary>Entity touch trampoline: only players (QC IS_PLAYER + alive) drive the flag state machine.</summary>
    private void FlagTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            FlagTouch(self, p);
    }

    /// <summary>
    /// QC ctf_FlagThink (per-flag, FLAG_THINKRATE): a base flag auto-captures a dropped enemy flag that settled
    /// within g_ctf_dropped_capture_radius; a dropped flag records its land time, floats in water, auto-returns
    /// when it settled near its own base, and bleeds its auto-return timer. Re-arms itself.
    /// </summary>
    private void FlagThinkEntity(Entity self)
    {
        self.NextThink = GametypeEntities.Now + 0.2f; // QC FLAG_THINKRATE
        float now = GametypeEntities.Now;
        var status = (FlagStatus)self.GtStatus;

        // QC FLAG_BASE: a home flag auto-captures any DROPPED flag that settled within the capture radius (a flag
        // thrown onto your base scores for the dropper). Settle = the dropped flag has a land time + the delay passed.
        if (status == FlagStatus.AtBase)
        {
            float capRadius = Cvar(CvarDroppedCaptureRadius, DefaultDroppedCaptureRadius);
            float capDelay  = Cvar(CvarDroppedCaptureDelay, DefaultDroppedCaptureDelay);
            if (capRadius > 0f)
            {
                foreach (FlagState other in Flags.Values)
                {
                    if (other.Status != FlagStatus.Dropped || other.Entity is not Entity oe)
                        continue;
                    if ((oe.Origin - self.Origin).Length() >= capRadius)
                        continue;
                    if (oe.GtLandTime <= 0f || now <= oe.GtLandTime + capDelay)
                        continue;
                    CaptureDropped(self, other);
                    break;
                }
            }
            return;
        }

        if (status != FlagStatus.Dropped)
            return;

        self.Angles = Vector3.Zero; // QC: reset flag angles in case warpzones adjust it
        if (self.OnGround && self.GtLandTime == 0f)
            self.GtLandTime = now; // QC: landtime set once it comes to rest

        // QC g_ctf_flag_dropped_floatinwater: a flag in water sinks slowly, then floats up when fully submerged.
        float floatVel = Cvar(CvarFlagFloatInWater, 0f);
        if (floatVel != 0f && Api.Services is not null)
        {
            Vector3 mid = self.Origin + (FlagMins + FlagMaxs) * 0.5f;
            if ((Api.Trace.PointContents(mid) & SuperContentsWater) != 0)
            {
                self.Velocity *= 0.5f;
                if ((Api.Trace.PointContents(mid + new Vector3(0f, 0f, FlagFloatOffsetZ)) & SuperContentsWater) != 0)
                    self.Velocity = new Vector3(self.Velocity.X, self.Velocity.Y, floatVel);
                else
                    self.MoveType = MoveType.Fly;
            }
            else if (self.MoveType == MoveType.Fly)
                self.MoveType = MoveType.Toss;
        }

        // QC g_ctf_flag_return_dropped: if the flag came to rest within this distance of its own base, return it now.
        float returnDropped = Cvar(CvarFlagReturnDropped, 0f);
        FlagState? flag = FlagForEntity(self);
        if (flag is not null && returnDropped != 0f)
        {
            bool nearBase = returnDropped == -1f || (self.Origin - self.GtSpawnOrigin).Length() <= returnDropped;
            if (nearBase)
            {
                AutoReturnFlag(flag);
                return;
            }
        }

        float returnTime = FlagReturnTime;
        if (returnTime > 0f && now >= self.GtDropTime + returnTime && flag is not null)
            AutoReturnFlag(flag);
    }

    /// <summary>
    /// QC ctf_Handle_Capture(baseFlag, droppedFlag, CAPTURE_DROPPED) (sv_ctf.qc:605): a dropped enemy flag that
    /// settled on a base is captured for the player who dropped it (enemy_flag.ctf_dropper). Mirrors the
    /// player-carry <see cref="Capture"/> scoring without requiring an active carry.
    /// </summary>
    private void CaptureDropped(Entity baseFlagEnt, FlagState droppedFlag)
    {
        Player? player = droppedFlag.Dropper;
        FlagState? baseFlag = FlagForEntity(baseFlagEnt);
        if (player is null || baseFlag is null)
            return;
        // QC CTF_DIFFTEAM(player, flag) guard: the dropper must be the SAME team as the BASE flag they land on
        // (you score a dropped-capture only at your OWN base), and the dropped flag must be an enemy flag.
        if (baseFlag.HomeTeam != (int)player.Team || droppedFlag.HomeTeam == (int)player.Team)
            return;

        AddTeamCaps(player.Team, 1);
        player.ScoreFrags += (int)ScoreCapture;
        AddTeamScore(player.Team, (int)ScoreCapture);
        AddCol(player, "CTF_CAPS", 1);
        if (droppedFlag.PickupTime > 0f)
        {
            float captime = Now - droppedFlag.PickupTime;
            Scoring.ScoreField? cf = Scoring.GameScores.Field("CTF_CAPTIME");
            if (captime > 0f && cf is not null)
                Scoring.GameScores.SetBestTime(player, cf, Scoring.GameScores.TimeEncode(captime));
        }

        // QC ctf_Handle_Capture(…, CAPTURE_DROPPED): same ctf_CaptureRecord broadcast (incl. fastest-cap time) + voice.
        float dropCapRunTime = droppedFlag.PickupTime > 0f ? (Now - droppedFlag.PickupTime) : 0f;
        CaptureRecord(droppedFlag.HomeTeam, player, dropCapRunTime);
        FlagAnnounceSound((int)player.Team, "CAPTURE"); // QC: capture voice keyed on the CAPTURING team (see the
                                                        // main Handle_Capture note) — not the captured flag (playtest-bugs #13).

        KillCarrierWaypoint(droppedFlag); // defensive: a dropped-capture flag has no carrier, but clear any stale sprite
        droppedFlag.ResetToBase();
        RespawnFlagEntity(droppedFlag);
        player.GtNextTakeTime = Now + FlagCollectDelay; // QC player.next_take_time = time + collect_delay
        UpdateLeaderAndCheckLimit();
    }

    /// <summary>
    /// Advance CTF's time-driven bits (QC ctf_FlagThink, evaluated headlessly): auto-return any dropped flag
    /// whose return timer has elapsed. Call each tick. Complements the per-entity think; safe with or without
    /// the entity layer (operates on <see cref="FlagState.DropTime"/>).
    /// </summary>
    // The most recent host roster (Clients.Players), cached by Tick for the touch-driven paths that QC
    // serves with FOREACH_CLIENT (Pickup's per-audience fan-out). Null until the first ticked frame / in
    // bare unit tests that drive Pickup directly.
    private IReadOnlyList<Player>? _lastRoster;

    public void Tick(IReadOnlyList<Player>? roster = null)
    {
        // Cache the roster for the touch-driven paths (Pickup's per-audience notification fan-out — QC
        // FOREACH_CLIENT — runs from FlagTouch, which has no roster parameter). Refreshed every tick by the
        // host (GameWorld passes Clients.Players); null in bare unit tests, where the fan-out politely skips.
        if (roster is not null)
            _lastRoster = roster;
        if (MatchEnded)
            return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float returnTime = FlagReturnTime;
        foreach (var flag in Flags.Values)
        {
            // QC ctf_FlagcarrierWaypoints per-think: refresh the live carrier waypoint's health bar from the
            // current carrier's effective HP each tick (independent of the entity layer, so it tracks even when
            // the world flag entity is headless). No-op when the flag has no live carrier sprite.
            RefreshCarrierWaypoint(flag);
            // QC setattachment: a carried flag rides its carrier. AttachToCarrier only places it ONCE, so without
            // a per-tick reposition the networked flag entity stays at the pickup spot instead of travelling with
            // the player. Re-place it behind+above the carrier (FLAG_CARRY_OFFSET, rotated by the carrier's yaw so
            // it trails them) and face it the carrier's way each tick.
            if (flag.Status == FlagStatus.Carried && flag.Carrier is { } carrier && flag.Entity is Entity cfe)
            {
                // QC vehicle vr_enter (e.g. racer): a flag-carrier who boards parks the flag at a fixed cockpit
                // offset so it rides the craft rather than the player's back. The vehicle owns the carrier's
                // motion, so anchor to the vehicle (origin rotated by its yaw) at the cockpit offset.
                Entity anchor = carrier.Vehicle ?? carrier;
                // QC vr_enter (racer parks at '-190 0 96', others at the shared VEHICLE_FLAG_OFFSET '0 0 96'):
                // read the boarded vehicle's own cockpit offset from its descriptor; fall back to the shared
                // VEHICLE_FLAG_OFFSET when the vehicle has no descriptor, and to FLAG_CARRY_OFFSET on foot.
                Vector3 off = carrier.Vehicle is not null
                    ? (carrier.Vehicle.VehicleDef?.FlagCarryOffset ?? FlagVehicleCarryOffset)
                    : FlagCarryOffset;
                QMath.AngleVectors(new Vector3(0f, anchor.Angles.Y, 0f), out Vector3 cf, out Vector3 cr, out Vector3 cu);
                Vector3 pos = anchor.Origin + cf * off.X + cr * off.Y + cu * off.Z;
                GametypeEntities.SetOrigin(cfe, pos);
                // playtest-bugs #7: the carry POSITION above is Quake-space and orbits +θ about Godot +Y. The
                // client renders a carried flag from its NETWORKED angles (ClientEntityView copies them onto its
                // proxy) via EntityNode's yaw-only Euler shortcut, which is handedness-flipped to −θ (a negated-yaw
                // Euler — see planning/COORDINATE_CONVENTIONS.md); left alone it counter-rotates the flag against
                // its orbit ("turn left, flag spins right"). A server-side render hint can't reach the client
                // (self-connecting listen server → snapshot-driven proxies), so pre-NEGATE the networked yaw here:
                // the −θ render path then lands on +θ, matching the position, and the flag rigidly trails the
                // carrier. (While carried the flag's angles are render-only — nothing reads them for logic; the
                // at-base flag keeps its true spawn angles.)
                cfe.Angles = new Vector3(0f, -anchor.Angles.Y, 0f);
            }
            // QC ctf_FlagThink FLAG_PASSING: re-aim / retrieve / give up an in-flight passed flag every tick.
            else if (flag.Status == FlagStatus.Passing)
                DrivePass(flag, now);
            // QC ctf_CheckFlagReturn: a dropped flag whose return timer elapsed returns itself to base.
            else if (returnTime > 0f && flag.Status == FlagStatus.Dropped && now >= flag.DropTime + returnTime)
                AutoReturnFlag(flag);
        }

        // QC ctf_FlagThink (sv_ctf.qc:1084): poll the stalemate condition at WPFE_THINKRATE.
        if (Cvar(CvarStalemate, 1f) != 0f && now >= _stalemateNextThink)
        {
            _stalemateNextThink = now + WpfeThinkRate;
            CheckStalemate(now, roster);
        }
        else if (Cvar(CvarStalemate, 1f) == 0f && Stalemate)
        {
            Stalemate = false; // stalemate disabled mid-match → clear it
            _stalemateAnnounced = false;
        }
    }

    /// <summary>
    /// QC ctf_CheckStalemate (sv_ctf.qc:903): both teams have held flags long enough (each beyond
    /// g_ctf_stalemate_time, or instantly in one-flag) → enter the stalemate "reveal carriers" state. End the
    /// stalemate per g_ctf_stalemate_endcondition (1 = when fewer than two flags are still stale, 2 = when none
    /// is). Sets <see cref="Stalemate"/>; the net status producer maps it to the CTF_STALEMATE OBJECTIVE_STATUS
    /// bit and the enemy-FC waypoint reveal is a presentation follow-on (see todos).
    /// </summary>
    public void CheckStalemate(float now, IReadOnlyList<Player>? roster = null)
    {
        float staleTime = Cvar(CvarStalemateTime, DefaultStalemateTime);
        int endCond = (int)Cvar(CvarStalemateEndCond, DefaultStalemateEndCond);

        // QC: a flag is "stale" if it's away from base AND (held past stalemate_time OR neutral/one-flag = instant).
        int staleRed = 0, staleBlue = 0, staleYellow = 0, stalePink = 0, staleNeutral = 0;
        foreach (FlagState f in Flags.Values)
        {
            if (f.Status == FlagStatus.AtBase)
                continue;
            bool instant = f.HomeTeam == Teams.None; // QC `|| !it.team` — neutral flags are instantly stale
            if (!instant && now < f.PickupTime + staleTime)
                continue;
            switch (f.HomeTeam)
            {
                case Teams.Red:    staleRed++;     break;
                case Teams.Blue:   staleBlue++;    break;
                case Teams.Yellow: staleYellow++;  break;
                case Teams.Pink:   stalePink++;    break;
                default:           staleNeutral++; break;
            }
        }

        int staleFlags = OneFlag
            ? (staleNeutral >= 1 ? 1 : 0)
            : (staleRed >= 1 ? 1 : 0) + (staleBlue >= 1 ? 1 : 0) + (staleYellow >= 1 ? 1 : 0) + (stalePink >= 1 ? 1 : 0);

        // QC ctf_CheckStalemate set/clear ladder. QC also resets wpforenemy_announced on a clear so the next
        // stalemate onset re-announces (the announce is a one-shot per stalemate stretch).
        if (OneFlag && staleFlags == 1)
            Stalemate = true;
        else if (staleFlags >= 2)
            Stalemate = true;
        else if (staleFlags == 0 && endCond == 2)
            { Stalemate = false; _stalemateAnnounced = false; }
        else if (staleFlags < 2 && endCond == 1)
            { Stalemate = false; _stalemateAnnounced = false; }

        // QC ctf_CheckStalemate announce (sv_ctf.qc:970): on the FIRST tick of a stalemate, center-print to every
        // player — flag carriers get CENTER_CTF_STALEMATE_CARRIER ("enemies can now see you"), everyone else gets
        // CENTER_CTF_STALEMATE_OTHER ("flag carriers can now be seen"). The enemy-FC waypoint reveal is already
        // emitted by CollectWaypoints (FlagCarrierEnemy sprites, shown to all). One-shot via _stalemateAnnounced.
        if (Stalemate && !_stalemateAnnounced && roster is not null)
        {
            _stalemateAnnounced = true;
            for (int i = 0; i < roster.Count; i++)
            {
                Player p = roster[i];
                if (p.IsObserver)
                    continue;
                NotificationSystem.Center(p, CarriedBy(p) is not null ? "CTF_STALEMATE_CARRIER" : "CTF_STALEMATE_OTHER");
            }
        }
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ctf, AbortSpeedrun)</c> (sv_ctf.qc): when a speedrun is aborted via
    /// impulse 141 (the SPEEDRUN cheat), any flag the carrier holds is force-respawned and
    /// <c>INFO_CTF_FLAGRETURN_ABORTRUN</c> is broadcast (QC <c>ctf_RespawnFlag</c> + announce). The port
    /// collapses the QC <c>ctf_RespawnFlag</c> into <see cref="AutoReturnFlag"/> which already handles the
    /// reset + voice + info line; the ABORTRUN variant sends the dedicated notification instead.
    /// </summary>
    public void AbortSpeedrun(Player p)
    {
        FlagState? carried = CarriedBy(p);
        if (carried is null)
            return;
        // QC MUTATOR_HOOKFUNCTION(ctf, AbortSpeedrun): ctf_RespawnFlag(player.flagcarried) +
        // INFO_CTF_FLAGRETURN_ABORTRUN broadcast + snd_flag_respawn global voice.
        FlagAnnounceSound(carried.HomeTeam, "RETURNED"); // QC snd_flag_respawn (ATTEN_NONE)
        NotificationSystem.Info($"CTF_FLAGRETURN_ABORTRUN_{TeamSuffix(carried.HomeTeam)}");
        KillCarrierWaypoint(carried);
        carried.ResetToBase();
        RespawnFlagEntity(carried);
    }

    /// <summary>
    /// QC ctf_RemovePlayer (sv_ctf.qc:2372), called from the MakePlayerObserver / ClientDisconnect / portal /
    /// vehicle hooks: a carrier who leaves play (disconnect, spectate, portal, board a vehicle) drops the flag
    /// where they stand, and any flag still referencing this player as pass sender / pass target / dropper has
    /// those back-links cleared so the flag can never get stuck pointing at a gone player.
    /// </summary>
    public void RemovePlayer(Player player)
    {
        if (CarriedBy(player) is not null)
            DropFlag(player); // QC ctf_Handle_Throw(player, NULL, DROP_NORMAL)

        foreach (FlagState f in Flags.Values)
        {
            if (ReferenceEquals(f.PassSender, player)) f.PassSender = null;
            if (ReferenceEquals(f.PassTarget, player)) f.PassTarget = null;
            if (ReferenceEquals(f.Dropper, player))    f.Dropper    = null;
        }
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ctf, LogDeath_AppendItemCodes)</c> (sv_ctf.qc): a player who dies while
    /// carrying a flag gets the item code <c>"F"</c> appended to their death-log line (the <c>:items=</c> /
    /// <c>:victimitems=</c> field of the <c>:kill:</c> event-log line). This overrides the per-GAMETYPE
    /// <see cref="GameType.LogDeathAppendItemCodes"/> (CTF is a gametype, not a mutator — the per-mutator
    /// variant on <see cref="MutatorBase"/> is a separate chain). Cosmetic server-log detail; no gameplay impact.
    /// NOTE: this override is dormant until <c>Scores.AppendItemcodes</c> also invokes the ACTIVE gametype's
    /// <c>LogDeathAppendItemCodes</c> (today it only fires the mutator chain via
    /// <c>MutatorActivation.LogDeathAppendItemCodes</c>) — see the cross-file follow-up.
    /// </summary>
    public override string LogDeathAppendItemCodes(Player player, string s)
        => CarriedBy(player) is not null ? s + "F" : s;

    /// <summary>QC ctf_RemovePlayer is hooked from both ClientDisconnect and MakePlayerObserver; this is the
    /// gametype's live leave-play hook (see <see cref="GameType.OnPlayerRemoved"/>) — it forwards to
    /// <see cref="RemovePlayer"/> so a leaving carrier drops the flag and stale back-links are cleared.</summary>
    public override void OnPlayerRemoved(Player player) => RemovePlayer(player);

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(ctf, PortalTeleport)</c> (sv_ctf.qc:2404): when a carrier crosses a Porto
    /// portal (player-placed via the Porto weapon), drop the flag with DROP_NORMAL — gated by
    /// <c>autocvar_g_ctf_portalteleport</c> (default 0 = disabled, matching Base). Subscribed to
    /// <see cref="WarpzoneManager.OnPortalTeleport"/> (fires for every client crossing a Porto-owned portal).
    /// </summary>
    private void OnPortalTeleport(Entity e)
    {
        if (MatchEnded || Cvar("g_ctf_portalteleport", 0f) == 0f)
            return;
        if (e is not Player player)
            return;
        // QC: ctf_RemovePlayer(player) — the same drop-and-clear path used by disconnect/observe/vehicle.
        RemovePlayer(player);
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ctf, GiveFragsForKill) (sv_ctf.qc): when g_ctf_ignore_frags is set, a regular
    /// CTF kill awards no frag score (FragScore = 0); at the default (0) the +1 frag is left intact. Always
    /// returns true so the GiveFrags path reads back the (possibly-zeroed) score. The CTF flag-carrier-kill
    /// bonus is scored separately in <see cref="OnDeath"/> (CTF_FCKILLS + g_ctf_score_kill) and is unaffected.
    /// </summary>
    private bool OnGiveFragsForKill(ref MutatorHooks.GiveFragsForKillArgs args)
    {
        if (Cvar("g_ctf_ignore_frags", 0f) != 0f)
            args.FragScore = 0f; // QC: M_ARGV(2, float) = 0 — regular frags give no points
        return true;             // QC: return true (the kill path reads M_ARGV(2)/FragScore back)
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ctf, PreferPlayerScore_Clear) { return true; }: CTF prefers to KEEP a rejoining
    /// player's accumulated score (under g_score_resetonjoin -1 this veto prevents the score wipe).
    /// </summary>
    private bool OnPreferPlayerScoreClear(ref MutatorHooks.PreferPlayerScore_ClearArgs args) => true;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ctf, VehicleEnter) (sv_ctf.qc:2537): when a flag-carrier boards a vehicle, either
    /// drop the flag (if neither <c>g_ctf_allow_vehicle_carry</c> nor <c>g_ctf_allow_vehicle_touch</c> is set) or
    /// let it ride the vehicle (the per-tick <see cref="Tick"/> repositions it at <see cref="FlagVehicleCarryOffset"/>
    /// relative to the vehicle when <c>carrier.Vehicle</c> is non-null). Returns false (does not consume the hook).
    /// </summary>
    private bool OnVehicleEnter(ref MutatorHooks.VehicleEnterArgs args)
    {
        if (args.Player is not Player player)
            return false;
        if (CarriedBy(player) is null)
            return false;

        // QC: if(!autocvar_g_ctf_allow_vehicle_carry && !autocvar_g_ctf_allow_vehicle_touch)
        //         { ctf_Handle_Throw(player, NULL, DROP_NORMAL); return true; }
        // When vehicle-carry is allowed the flag stays on the carrier and Tick() parks it at VEHICLE_FLAG_OFFSET
        // (FlagVehicleCarryOffset) relative to the vehicle each tick — no explicit reattachment needed here.
        bool vehicleCarry  = Cvar("g_ctf_allow_vehicle_carry",  0f) != 0f;
        bool vehicleTouch  = Cvar("g_ctf_allow_vehicle_touch",  0f) != 0f;
        if (!vehicleCarry && !vehicleTouch)
            DropFlag(player);

        return false; // QC return-value is consumed only for carry-allowed (attach), not for the drop path
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ctf, VehicleExit) (sv_ctf.qc:2560): when a flag-carrier leaves a vehicle, reattach
    /// the flag to the player at <see cref="FlagCarryOffset"/>. The port's per-tick <see cref="Tick"/> already
    /// repositions the flag to the player (using <c>carrier.Vehicle ?? carrier</c> as the anchor), so this
    /// handler only needs to fire when a world-entity attachment is present — it resets the entity to follow
    /// the player on the very next tick without a one-tick displacement. Returns false (does not consume).
    /// </summary>
    private bool OnVehicleExit(ref MutatorHooks.VehicleExitArgs args)
    {
        if (args.Player is not Player player)
            return false;
        FlagState? carried = CarriedBy(player);
        if (carried is null)
            return false;

        // QC VehicleExit: setattachment(flag, player, ""); setorigin(flag, FLAG_CARRY_OFFSET).
        // The Tick() will reposition the entity on the next frame; here we snap it immediately so there is no
        // single-frame visual lag at the vehicle exit point.
        if (carried.Entity is Entity fe)
            GametypeEntities.AttachToCarrier(fe, player, FlagCarryOffset);

        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ctf, MatchEnd) (sv_ctf.qc:2587): the game is over — walk every flag and lock any
    /// DROPPED or PASSING flag in place (MOVETYPE_NONE, DAMAGE_NO, SOLID_NOT, stop thinking) so a live flag can't
    /// keep integrating, be touched, or auto-return during intermission. Base/carried flags are left untouched.
    /// The gameplay outcome (no scoring after match end) is already enforced by the per-handler MatchEnded gate;
    /// this freezes the physical flag entity to match Base. Returns false (does not consume the hook).
    /// </summary>
    private bool OnMatchEnd(ref MutatorHooks.MatchEndArgs args)
    {
        // QC sets the gametype's own MatchEnded gate here too (the handlers early-return once the match is over);
        // the port latches MatchEnded in UpdateLeaderAndCheckLimit, but a timelimit/forced end runs through this
        // hook without a capture, so mirror QC and latch it now so Tick/Pickup/etc. stop the state machine.
        MatchEnded = true;
        foreach (Entity e in FlagEntities)
        {
            var status = (FlagStatus)e.GtStatus;
            if (status != FlagStatus.Dropped && status != FlagStatus.Passing)
                continue; // QC: do nothing for FLAG_BASE / FLAG_CARRY
            // QC: set_movetype(it, MOVETYPE_NONE); it.takedamage = DAMAGE_NO; it.solid = SOLID_NOT; it.nextthink = false;
            e.MoveType = MoveType.None;
            e.TakeDamage = DamageMode.No;
            e.Solid = Solid.Not;
            e.Velocity = Vector3.Zero;
            e.NextThink = 0f; // QC nextthink = false: stop the per-flag think (no more auto-return / pass drive)
        }
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ctf, PlayerUseKey) (sv_ctf.qc:2452): the +use key in a carrier's hands throws the
    /// flag (g_ctf_throw); in an empty-handed teammate's hands it requests a pass from the nearest carrier
    /// (g_ctf_pass_request). <paramref name="roster"/> is the live player list (needed to find a carrier to
    /// request from). Returns true if the key was consumed (a throw or pass-request fired).
    /// </summary>
    public bool HandleUseKey(Player player, IReadOnlyList<Player> roster)
    {
        if (MatchEnded || player.IsDead)
            return false;
        // Carrier → throw (QC: carrier presses +use → ctf_Handle_Throw DROP_THROW).
        if (CarriedBy(player) is not null)
            return ThrowFlag(player);
        // Empty-handed teammate → ask the nearest in-radius carrier to pass (QC g_ctf_pass_request).
        return RequestPass(player, roster);
    }

    // ============================================================================================
    //  Capture shield (QC ctf_CaptureShield_*) — shield the worst players from taking the flag
    // ============================================================================================

    /// <summary>
    /// QC ctf_CaptureShield_CheckStatus: a player is shielded (blocked from taking the flag) when their net
    /// CTF score is sufficiently negative AND they are in the worse part of their team relative to
    /// <see cref="_captureShieldMaxRatio"/>. With no negative-score floor configured, nobody is shielded.
    /// </summary>
    public bool CaptureShieldStatus(Player p, IReadOnlyList<Player> roster)
    {
        if (_captureShieldMaxRatio <= 0f)
            return false;

        // QC ctf_CaptureShield_CheckStatus: the shield score is the CTF composite (CAPS - PICKUPS) + (RETURNS +
        // FCKILLS), NOT the net SCORE — a player who steals a lot but never caps/returns is the one shielded.
        int myScore = CtfShieldScore(p);
        if (myScore >= -_captureShieldMinNegScore)
            return false;

        int playersTotal = 0, playersWorseEq = 0;
        for (int i = 0; i < roster.Count; i++)
        {
            Player it = roster[i];
            if ((int)it.Team != (int)p.Team)
                continue;
            if (CtfShieldScore(it) <= myScore)
                playersWorseEq++;
            playersTotal++;
        }

        // Shielded only if the player is in the worse part of the team (QC ratio test).
        return playersWorseEq < playersTotal * _captureShieldMaxRatio;
    }

    /// <summary>QC ctf_CaptureShield_CheckStatus score: <c>(CTF_CAPS - CTF_PICKUPS) + (CTF_RETURNS + CTF_FCKILLS)</c>.</summary>
    private static int CtfShieldScore(Player p)
    {
        return GetCol(p, "CTF_CAPS") - GetCol(p, "CTF_PICKUPS") + GetCol(p, "CTF_RETURNS") + GetCol(p, "CTF_FCKILLS");
    }

    /// <summary>Read one CTF score column off a player (0 when the field is unregistered).</summary>
    private static int GetCol(Player p, string field)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        return f is null ? 0 : Scoring.GameScores.Get(p, f);
    }

    /// <summary>
    /// QC <c>ctf_CaptureShield_Update(player, 1)</c> (wanted_status = 1, "unshield only"): clears the player's
    /// capture shield and sends <c>CENTER_CTF_CAPTURESHIELD_FREE</c> if they were previously shielded.
    /// Called after a successful capture — a capturing player has just scored, so their net CTF score improved
    /// and they should no longer be shielded. The per-tick <see cref="UpdateCaptureShields"/> recompute will
    /// restore the flag if the player somehow remains eligible, so this is a safe eager clear.
    /// <para>QC logic (wanted_status=1, unshield-only path): fires when
    /// <c>(1 == player.ctf_captureshielded) &amp;&amp; (updated_status != 1)</c>. Since a capture always
    /// improves the composite score, the update result is reliably 0 (not shielded), so we clear eagerly.</para>
    /// </summary>
    public void UpdateCaptureShield(Player p)
    {
        if (p.GtCaptureShielded)
        {
            p.GtCaptureShielded = false;
            NotificationSystem.Center(p, "CTF_CAPTURESHIELD_FREE");
        }
    }

    /// <summary>
    /// QC <c>ctf_CaptureShield_Update(player, 0)</c> (wanted_status = 0, "shield only") over the full roster:
    /// recompute each player's shielded flag and send the appropriate status-change centerprint when the shield
    /// transitions. Called each server tick from <c>GameWorld.Tick</c> so newly qualifying players get shielded
    /// and recovering players are set free promptly.
    /// <para>QC logic for each player: compute <c>updated_status = ctf_CaptureShield_CheckStatus(player)</c>;
    /// if <c>wanted_status == player.ctf_captureshielded &amp;&amp; updated_status != wanted_status</c> fire the
    /// notification and set the new flag. The per-tick call uses wanted_status=1 in Base PlayerPreThink, but
    /// here we unify both directions in one pass so the free/shielded transitions are both covered.</para>
    /// </summary>
    public void UpdateCaptureShields(IReadOnlyList<Player> roster)
    {
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            bool wasShielded  = p.GtCaptureShielded;
            bool nowShielded  = CaptureShieldStatus(p, roster);
            if (wasShielded != nowShielded)
            {
                p.GtCaptureShielded = nowShielded;
                // QC: Send_Notification(NOTIF_ONE, player, MSG_CENTER, updated_status ?
                //         CENTER_CTF_CAPTURESHIELD_SHIELDED : CENTER_CTF_CAPTURESHIELD_FREE)
                NotificationSystem.Center(p, nowShielded ? "CTF_CAPTURESHIELD_SHIELDED" : "CTF_CAPTURESHIELD_FREE");
            }
        }
    }

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: the team winner ranks by the team primary slot — ST_CTF_CAPS (caps). LeaderTeam / SecondTeam read
        // the flag-aware two-slot compare from GameScores (the source of truth), so caps drive the standings.
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamCaps(bestTeam);
        float capLimit = CaptureLimit;
        if (capLimit > 0f && bestScore >= capLimit)
            MatchEnded = true;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamCaps(secondTeam)) >= leadLimit)
            MatchEnded = true;
    }

    private static float Cvar(string name, float def) => TryCvar(name, out float v) ? v : def;

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

/// <summary>The lifecycle of a CTF flag (QC FLAG_BASE / FLAG_CARRY / FLAG_DROPPED / FLAG_PASSING, ctf_status).
/// Note: the port's ordinals (0/1/2/3) differ from QC's (1/2/3/4); the meaning is identical.</summary>
public enum FlagStatus
{
    /// <summary>Resting at its home base, capturable target and pickup source.</summary>
    AtBase = 0,
    /// <summary>Held by an enemy carrier.</summary>
    Carried = 1,
    /// <summary>Dropped on the ground after the carrier died (awaiting return or re-pickup).</summary>
    Dropped = 2,
    /// <summary>In flight toward a teammate (QC FLAG_PASSING): a thrown pass that has not yet been received.</summary>
    Passing = 3,
}

/// <summary>QC the drop variants passed to ctf_Handle_Throw (sv_ctf.qh DROP_NORMAL..DROP_RESET).</summary>
public enum FlagDropType
{
    /// <summary>QC DROP_NORMAL — the carrier died/disconnected; the flag tumbles where they fell.</summary>
    Normal = 1,
    /// <summary>QC DROP_THROW — the carrier deliberately threw the flag forward (g_ctf_throw).</summary>
    Throw = 2,
    /// <summary>QC DROP_PASS — the carrier passed the flag toward a teammate (g_ctf_pass).</summary>
    Pass = 3,
    /// <summary>QC DROP_RESET — the flag is being force-reset to base (no toss).</summary>
    Reset = 4,
}

/// <summary>
/// One CTF flag — the Godot-free essence of the QC flag edict (item_flag_team*, .ctf_status / .owner /
/// .flagcarried). Tracks which team it belongs to, its current <see cref="FlagStatus"/>, the carrier while
/// held, and — when a facade is wired — the world <see cref="Entity"/> that physically represents the flag
/// (spawned by <see cref="Ctf.SpawnFlag"/>). The model/animation, CSQC networking, and waypoint sprites
/// remain client concerns.
/// </summary>
public sealed class FlagState
{
    /// <summary>The team whose base this flag lives at (a <see cref="Teams"/> color code).</summary>
    public readonly int HomeTeam;

    public FlagStatus Status = FlagStatus.AtBase;

    /// <summary>The enemy player carrying this flag (QC flag.owner), or null when not carried.</summary>
    public Player? Carrier;

    /// <summary>The world entity representing this flag (QC the item_flag_team* edict), or null (headless).</summary>
    public Entity? Entity;

    /// <summary>Home base position the flag returns to (QC ctf_spawnorigin / dropped_origin).</summary>
    public Vector3 HomeOrigin;

    /// <summary>The player who last dropped this flag (QC ctf_dropper), gating quick re-pickup.</summary>
    public Player? Dropper;

    /// <summary>Sim time the flag was last picked up (QC ctf_pickuptime) — for capture-time records.</summary>
    public float PickupTime;

    /// <summary>Where the flag was dropped (QC drop origin), valid while <see cref="FlagStatus.Dropped"/>.</summary>
    public Vector3 DropOrigin;

    /// <summary>Sim time the flag was dropped (QC ctf_droptime) — drives the auto-return timer.</summary>
    public float DropTime;

    /// <summary>QC flag.pass_target: the teammate a FLAG_PASSING flag is flying toward, or null.</summary>
    public Player? PassTarget;

    /// <summary>QC flag.pass_sender: the player who initiated the in-flight pass (credited if it succeeds).</summary>
    public Player? PassSender;

    /// <summary>QC flag.pass_distance: the planar distance from the passer to the target when the pass began.</summary>
    public float PassDistance;

    /// <summary>
    /// QC <c>wps_flagcarrier</c> (sv_ctf.qc ctf_FlagcarrierWaypoints): the LIVE carrier waypoint sprite that rides
    /// the current carrier — owned by the <see cref="Waypoints.WaypointSprites"/> registry (not the per-tick
    /// <see cref="Ctf.CollectWaypoints"/> snapshot), so it carries a HEALTH BAR (UpdateMaxHealth/UpdateHealth) and
    /// the auto-helpme flash (HelpMePing). Created in <see cref="Ctf.Pickup"/> / <see cref="Ctf.RetrieveFlag"/> via
    /// <c>WaypointSprites.AttachCarrier</c>, refreshed each tick in <see cref="Ctf.Tick"/>, and killed the instant
    /// the flag leaves the carrier (capture / return / drop / death / disconnect). Null when not carried.
    /// </summary>
    public Waypoints.WaypointSprite? CarrierWaypoint;

    public FlagState(int homeTeam) => HomeTeam = homeTeam;

    /// <summary>Reset to resting at base (QC ctf_RespawnFlag): clear carrier + drop + pass bookkeeping.</summary>
    public void ResetToBase()
    {
        Status = FlagStatus.AtBase;
        Carrier = null;
        Dropper = null;
        DropOrigin = Vector3.Zero;
        DropTime = 0f;
        PassTarget = null;
        PassSender = null;
        PassDistance = 0f;
        // The live carrier sprite is killed by the call site (Capture/Return/Drop/etc.) before/around ResetToBase;
        // null the back-link here too so a reset flag never holds a stale sprite reference (defensive).
        CarrierWaypoint = null;
    }
}
