using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Team Keepaway (TKA) gametype — port of <c>CLASS(TeamKeepaway, Gametype)</c>
/// (common/gametypes/gametype/tka/{tka.qh,sv_tka.qc}). The team variant of Keepaway: a single ball sits on the
/// map and the team that holds it scores for kills its members make while in possession. Concretely
/// (QC sv_tka.qc PlayerDies, applied PER-TEAM):
///  - the DISTINGUISHING TKA rule (<c>g_tka_score_team</c>, default 1): while ANY teammate carries the ball,
///    EVERY cross-team kill that team makes scores <c>g_tka_score_killac</c> for the team — not just the
///    carrier's own kills (the FFA Keepaway semantics);
///  - fragging the enemy ball-carrier scores <c>g_tka_score_bckill</c> bonus points for the team and credits
///    the killer's TKA_CARRIERKILLS column, independent of whether the killer carries;
///  - frags themselves award no DM frag points (QC GiveFragsForKill zeroes the frag delta).
/// The first team to the point limit (<c>g_tka_point_limit</c> → mapinfo 50) — or to a lead of
/// <c>g_tka_point_leadlimit</c> — wins.
///
/// IMPORTANT — cvar family: TKA has its OWN <c>g_tka_*</c> / <c>g_tkaball_*</c> tunables (DISTINCT engine cvars
/// from Keepaway's <c>g_keepaway_*</c>). This port reads the TKA cvars throughout (scoring, damage/force matrix,
/// respawn time), so it honors TKA's tuning rather than Keepaway's.
///
/// Faithfully ported (the possession-scoring rule):
///  - which team currently holds the ball (<see cref="BallTeam"/>) and which player carries it (<see cref="Carrier"/>);
///  - Combat.Death → team kill scoring (the g_tka_score_team team-has-ball scan, carrier-kill bonus, and
///    DIFF_TEAM team-kill exclusion — QC PlayerDies);
///  - per-team score (<see cref="ScoreFor"/>) and point-limit / lead-limit win check (<see cref="CheckPointLimit"/>).
///
/// Faithfully ported (objective layer, via the shared <see cref="BallEntity"/> framework):
///  - the world ball entity with carry/drop/respawn-relocate (<see cref="SpawnBall"/> / <see cref="GiveBall"/> /
///    <see cref="DropBall"/>, QC tka_TouchEvent / tka_DropEvent / tka_RespawnBall) plus pickup/drop/world-touch
///    sounds + notifications, the 0.5 s self-recapture lockout, and the carried-ball orbit anim (<see cref="Tick"/>);
///  - the possession-based damage AND force scaling matrix (<see cref="DamageScale"/> / <see cref="DamageForceScale"/>,
///    QC Damage_Calculate), subscribed to the live MutatorHooks.DamageCalculate channel;
///  - per-team timed-possession points (<c>g_tka_score_timepoints</c>, off by default) + the TKA_BCTIME column.
///
/// Faithfully ported (Wave 6a — presentation + physics):
///  - the carrier-highspeed PlayerPhysics modifier (<see cref="OnPlayerPhysics"/>, QC PlayerPhysics_UpdateStats →
///    STAT(MOVEVARS_HIGHSPEED) *= <c>g_tka_ballcarrier_highspeed</c>, default 1 = no effect), live on the same
///    MutatorHooks.PlayerPhysics channel as Keepaway/buffs/powerups;
///  - the team-colored carrier / loose-ball waypoint sprites (<see cref="CollectWaypoints"/>, QC tka_TouchEvent
///    WP_TkaBallCarrier{Red,Blue,Yellow,Pink} + tka_RespawnBall/tka_DropEvent WP_KaBall), honoring
///    <c>g_tkaball_tracking</c>, shipped through the live ServerNet.SendWaypoints pull;
///  - the TKA_BALLSTATUS HUD mod-icon (<see cref="BallStatusFor"/>, QC PlayerPreThink bit pack → GametypeStatusBlock
///    Kind.TeamKeepaway → ModIconsPanel.DrawTeamKeepaway: carrying / per-team taken / dropped icons).
///
/// Deferred (NOTE — cross-boundary): multi-ball chaining (<c>g_tka_ballcarrier_maxballs</c>), and the use-key /
/// disconnect / observe drop triggers (need MutatorHooks.PlayerUseKey / MakePlayerObserver / ClientDisconnect /
/// DropSpecialItems, not yet on MutatorHooks — see todos). Death-drop IS wired (<see cref="OnDeath"/>).
/// </summary>
[GameType]
public sealed class TeamKeepaway : GameType
{
    // ----- point limit cvars + default (gametype default pointlimit=50; lead limit default 0) -----
    private const string CvarPointLimit    = "g_tka_point_limit";
    private const string CvarLeadLimit     = "g_tka_point_leadlimit";
    private const string CvarFragLimit     = "fraglimit"; // GameRules_limit_score writes the point limit here
    private const int    DefaultPointLimit = 50;          // gametype_init "pointlimit=50" (mapinfo default)

    // ----- kill-scoring cvars (TKA's OWN g_tka_* family — distinct from Keepaway's g_keepaway_*) -----
    private const string CvarScoreKillAc    = "g_tka_score_killac"; // team points for a kill while the team holds the ball
    private const string CvarScoreBcKill    = "g_tka_score_bckill"; // team bonus for killing the enemy carrier
    private const string CvarScoreTeam      = "g_tka_score_team";   // any teammate's kill scores while team holds ball
    private const string CvarScoreTimePoint = "g_tka_score_timepoints"; // team points/sec while a teammate carries
    private const string CvarNonCarrierWarn = "g_tka_noncarrier_warn";  // centerprint warn on a no-ball-possession kill
    // QC defaults (sv_tka.qc autocvars / balance): g_tka_score_killac 1, g_tka_score_bckill 1, g_tka_score_team 1.
    // When the server cfg is unloaded (headless/tests) these fallbacks must match Base, not 0.
    private const int    DefaultScoreKillAc = 1;
    private const int    DefaultScoreBcKill = 1;
    private const bool   DefaultScoreTeam   = true;

    // ----- team count cvars (g_tka_teams_override >= 2 ? override : g_tka_teams), clamped 2..4 -----
    private const string CvarTeamsOverride = "g_tka_teams_override";
    private const string CvarTeams         = "g_tka_teams";
    private const int    DefaultTeams      = 2;

    // ----- ball respawn cvar (TKA's own g_tkaball_respawntime, NOT g_keepawayball_respawntime) -----
    private const string CvarRespawnTime = "g_tkaball_respawntime";

    // Per-team score (QC ST_SCORE, team slot 0 — the TEAM primary) now lives in the unified GameScores two-slot
    // team store — the source of truth (common/scores.qh). TKA's team primary IS ST_SCORE (stprio=PRIMARY, with no
    // slot-1 team field), like TDM. Read/written via ScoreFor / GameScores.AddToTeam (slot 0).

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.GiveFragsForKillArgs>? _giveFragsHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _physicsHandler;
    private HookHandler<MutatorHooks.PlayerUseKeyArgs>? _useKeyHandler;
    private HookHandler<MutatorHooks.PreferPlayerScore_ClearArgs>? _preferScoreClearHandler;

    // QC g_tka_ballcarrier_highspeed: the carrier's MOVEVARS_HIGHSPEED multiplier (default 1 = no effect).
    private const string CvarBallCarrierHighspeed = "g_tka_ballcarrier_highspeed";
    /// <summary>QC autocvar_g_tka_ballcarrier_highspeed (default 1): speed multiplier applied to the ball carrier.</summary>
    public float BallCarrierHighspeed => TryCvar(CvarBallCarrierHighspeed, out float v) && v > 0f ? v : 1f;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>The player currently carrying the ball (QC <c>.ballcarried</c> owner), or null if loose.</summary>
    public Player? Carrier { get; private set; }

    /// <summary>QC checkrules end-of-match latch: true once a team reaches the point/lead limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The team color code that reached the point limit, or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    /// <summary>Per-team timed-possession remainder accumulator (QC float2int_decimal_fld) so fractional points carry over.</summary>
    private float _timePointsRemainder;

    /// <summary>Ball-carry-time remainder accumulator (QC TKA_BCTIME += frametime), whole seconds banked.</summary>
    private float _bcTimeRemainder;

    public TeamKeepaway()
    {
        NetName = "tka";
        DisplayName = "Team Keepaway";
        TeamGame = true;
    }

    /// <summary>The single world ball entity (QC keepawayball edict), or null (headless).</summary>
    public Entity? BallEntity { get; private set; }

    /// <summary>QC g_tkaball_respawntime: seconds a loose ball waits before relocating.</summary>
    public float RespawnTime => TryCvar(CvarRespawnTime, out float v) && v > 0f ? v : 10f;

    public override void OnInit()
    {
        // QC tka shares keepaway's ball: one ball spawned procedurally at map start (see SpawnBall). gametype_init
        // flags (TEAMPLAY|USEPOINTS) and teams=2 are the engine's job; OnInit clears the ball reference.
        BallEntity = null;
        Carrier = null;
    }

    /// <summary>
    /// QC sv_tka.qh: <c>GameRules_spawning_teams(autocvar_g_tka_team_spawns)</c> — TKA gates team spawns on
    /// g_tka_team_spawns (stock default 0, so it does NOT use team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_tka_team_spawns", out float v) ? v != 0f : false;

    /// <summary>
    /// QC tka_SpawnBalls: create the single world ball entity at <paramref name="origin"/> via the shared
    /// <see cref="BallEntity"/> framework (touch = pickup, built-in relocate think + the orbblue.md3 model /
    /// EF_DIMLIGHT / glow_trail / g_tkaball_damageforcescale 2 defaults). The Keepaway-family ball immediately
    /// relocates to a random map location and arms the respawn timer (QC tka_RespawnBall tail of tka_SpawnBalls).
    /// The host's round-drive seam (GameWorld) calls this once the match is live, since the TKA ball is
    /// procedural (no map entity) — see <see cref="AdoptBall"/>. Returns the entity (or null when headless).
    /// </summary>
    public Entity? SpawnBall(Vector3 origin)
    {
        var cfg = new BallConfig
        {
            // QC tka_TouchEvent / tka_RespawnBall: our touch handles pickup + world-touch; RespawnTime drives the
            // built-in relocate think (RelocateOnRespawn defaulted true by BallKind.KeepawayBall).
            Touch = BallTouchEntity,
            RespawnTime = RespawnTime,
        };
        Entity? e = global::XonoticGodot.Common.Gameplay.BallEntity.SpawnForGametype(BallKind.KeepawayBall, origin, cfg);
        return AdoptBall(e);
    }

    /// <summary>
    /// Adopt a ball created by the host's round-drive seam (QC tka_Handler_CheckBall → tka_SpawnBalls) as THIS
    /// gametype's single ball: record it and clear any carrier. Returns the same entity.
    /// </summary>
    public Entity? AdoptBall(Entity? e)
    {
        BallEntity = e;
        Carrier = null;
        return e;
    }

    /// <summary>Entity touch trampoline (QC tka_TouchEvent): a live player picks the loose ball up; a non-player
    /// world touch plays the touch sfx; the 0.5 s self-recapture lockout is honored.</summary>
    private void BallTouchEntity(Entity self, Entity other)
    {
        if (Carrier is not null)
            return; // already carried

        if (other is Player p)
        {
            if (p.IsDead)
                return;
            // QC tka_DropEvent self-recapture lockout: a carrier who just dropped can't instantly re-grab it.
            if (global::XonoticGodot.Common.Gameplay.BallEntity.IsRecaptureLocked(self, p))
                return;
            GiveBall(p);
            return;
        }

        // QC tka_TouchEvent: the ball just touched a non-player (most likely the world) — spark + touch sfx.
        EffectEmitter.Emit("BALL_SPARKS", self.Origin, Vector3.Zero, 1); // QC Send_Effect(EFFECT_BALL_SPARKS, this.origin, '0 0 0', 1)
        SoundSystem.PlayOn(self, Sounds.ByName("KA_TOUCH")); // QC SND_KA_TOUCH, ATTEN_NORM
    }

    /// <summary>QC tka_RespawnBall (think): relocate a loose ball to a fresh random map location, re-arm the
    /// timer, and play the respawn sound (ATTEN_NONE).</summary>
    public void RespawnBall(Entity self)
    {
        if (!ReferenceEquals(self, BallEntity) || Carrier is not null)
            return;
        // QC tka_RespawnBall: capture the old origin BEFORE relocating (the respawn effect plays at both the
        // old and the new spot).
        Vector3 oldBallOrigin = self.Origin;
        // QC tka_RespawnBall: MoveToRandomMapLocation (with the SelectSpawnPoint fallback) + '0 0 200' kick + arm.
        global::XonoticGodot.Common.Gameplay.BallEntity.Relocate(self, RespawnTime);
        self.Think = RespawnBallThink;
        // QC: Send_Effect(EFFECT_KA_BALL_RESPAWN, oldballorigin, '0 0 0', 1);
        //     Send_Effect(EFFECT_KA_BALL_RESPAWN, this.origin, '0 0 0', 1);
        EffectEmitter.Emit("KA_BALL_RESPAWN", oldBallOrigin, Vector3.Zero, 1);
        EffectEmitter.Emit("KA_BALL_RESPAWN", self.Origin, Vector3.Zero, 1);
        SoundSystem.PlayOn(self, Sounds.ByName("KA_RESPAWN")); // QC SND_KA_RESPAWN, ATTEN_NONE
    }

    private void RespawnBallThink(Entity self) => RespawnBall(self);

    // ============================================================================================
    //  Possession damage/force scaling (QC tka Damage_Calculate)
    // ============================================================================================

    /// <summary>
    /// QC Damage_Calculate (tka): scale player-vs-player damage by ball possession. The x/y/z components of
    /// <c>g_tka_ballcarrier_damage</c> / <c>g_tka_noncarrier_damage</c> select the self/other-carrier/noncarrier
    /// case (the carrier-vs-noncarrier branch is chosen by whether the ATTACKER carries). Returns the scaled
    /// damage. (Force scaling is the same matrix on the force cvars; <see cref="DamageForceScale"/> exposes it.)
    /// </summary>
    public float DamageScale(Player attacker, Player target, float damage)
        => damage * ScaleFactor(attacker, target, "g_tka_ballcarrier_damage", "g_tka_noncarrier_damage");

    /// <summary>The QC force multiplier for a hit (same matrix as <see cref="DamageScale"/> on the force cvars).</summary>
    public float DamageForceScale(Player attacker, Player target)
        => ScaleFactor(attacker, target, "g_tka_ballcarrier_force", "g_tka_noncarrier_force");

    private float ScaleFactor(Player attacker, Player target, string carrierCvar, string nonCarrierCvar)
    {
        // QC Damage_Calculate reads a single VECTOR cvar and indexes its component: .x = self-damage,
        // .y = damage to (other) ballcarriers, .z = damage to noncarriers. Base ships these as vectors
        // (g_tka_ballcarrier_damage "1 1 1"), so we parse the vector string. Default 1 (no scaling) when unset.
        bool attackerCarries = ReferenceEquals(Carrier, attacker);
        bool targetCarries = ReferenceEquals(Carrier, target);
        string cvarName = attackerCarries ? carrierCvar : nonCarrierCvar;
        int component = ReferenceEquals(target, attacker) ? 0 : (targetCarries ? 1 : 2);
        return CvarVectorComponent(cvarName, component, 1f);
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

    /// <summary>QC MUTATOR_HOOKFUNCTION(tka, Damage_Calculate): apply the ballcarrier/noncarrier damage+force
    /// matrix to player-vs-player hits (no-op for non-player attacker/target, mirroring the QC IS_PLAYER guard).</summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (args.Attacker is not Player attacker || args.Target is not Player target)
            return false; // QC: only apply scaling to player versus player combat
        args.Damage = DamageScale(attacker, target, args.Damage);
        args.Force *= DamageForceScale(attacker, target);
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tka, GiveFragsForKill): M_ARGV(2,float)=0; return true. No frags in TKA.</summary>
    private bool OnGiveFragsForKill(ref MutatorHooks.GiveFragsForKillArgs args)
    {
        args.FragScore = 0f; // QC: no frags counted in keepaway
        return true;         // QC returns true so GiveFrags reads the zeroed delta back
    }

    /// <summary>The team color code (see <see cref="Teams"/>) of the current ball-carrier, or 0 if loose.</summary>
    public int BallTeam => Carrier is null ? Teams.None : (int)Carrier.Team;

    // QC tka.qh STAT(TKA_BALLSTATUS) bit layout: per-team taken (BIT 0..3), self-carrying (BIT 4), dropped (BIT 5).
    public const int BallTakenRed = 1 << 0;    // TKA_BALL_TAKEN_RED
    public const int BallTakenBlue = 1 << 1;   // TKA_BALL_TAKEN_BLUE
    public const int BallTakenYellow = 1 << 2; // TKA_BALL_TAKEN_YELLOW
    public const int BallTakenPink = 1 << 3;   // TKA_BALL_TAKEN_PINK
    public const int BallCarrying = 1 << 4;    // TKA_BALL_CARRYING (the RECIPIENT carries it)
    public const int BallDropped = 1 << 5;     // TKA_BALL_DROPPED (a ball is loose)

    /// <summary>
    /// QC sv_tka.qc PlayerPreThink: pack STAT(TKA_BALLSTATUS) for <paramref name="viewer"/>. TKA_BALL_CARRYING
    /// if the viewer holds the (single) ball; otherwise per the ball's state — TKA_BALL_DROPPED when loose, or the
    /// TKA_BALL_TAKEN_{RED,BLUE,YELLOW,PINK} bit for the carrier's team. With one ball, exactly one of carrying /
    /// taken / dropped is set (the QC IL_EACH over g_tkaballs collapses to the single ball here).
    /// </summary>
    public int BallStatusFor(Player viewer)
    {
        int status = 0;
        if (ReferenceEquals(Carrier, viewer))
            status |= BallCarrying; // QC: if(player.ballcarried) |= TKA_BALL_CARRYING
        // QC IL_EACH(g_tkaballs, ...): the single ball is either loose (dropped) or owned by a team.
        if (Carrier is null)
            status |= BallDropped; // QC: if(!it.owner) |= TKA_BALL_DROPPED
        else
            status |= (int)Carrier.Team switch
            {
                Teams.Red => BallTakenRed,
                Teams.Blue => BallTakenBlue,
                Teams.Yellow => BallTakenYellow,
                Teams.Pink => BallTakenPink,
                _ => 0,
            };
        return status;
    }

    /// <summary>The point limit in force (g_tka_point_limit, else fraglimit, else 50). 0 means no limit.</summary>
    public int PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimit, out float pl)) return (int)pl;
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return DefaultPointLimit;
        }
    }

    /// <summary>The lead limit in force (g_tka_point_leadlimit; QC mapinfo default 0 = no lead limit).</summary>
    public int LeadLimit => TryCvar(CvarLeadLimit, out float v) ? (int)v : 0;

    /// <summary>Number of teams in play (g_tka_teams_override if &gt;= 2, else g_tka_teams), clamped to 2..4.</summary>
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

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        Carrier = null;
        _timePointsRemainder = 0f;
        _bcTimeRemainder = 0f;
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring
        // QC fragsleft_last reset: re-arm the "N points left" announcer (tka's Scores_CountFragsRemaining hook,
        // gated on !g_tka_score_timepoints).
        GS.ResetFragsRemaining();

        // QC sv_tka.qc GameRules_scoring(tka_teams, SFL_SORT_PRIO_PRIMARY, SFL_SORT_PRIO_PRIMARY, {
        // field(SP_TKA_PICKUPS, "pickups", 0); field(SP_TKA_CARRIERKILLS, "bckills", 0); field(SP_TKA_BCTIME,
        // "bctime", SFL_SORT_PRIO_SECONDARY); }): SP_SCORE is the player primary (team-scored kills); ST_SCORE
        // (team slot 0) is the TEAM primary (stprio=PRIMARY — no slot-1 team field); SP_TKA_BCTIME the player secondary.
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.SortPrioPrimary); // ST_SCORE (slot 0) is the team primary
        GS.DeclareColumn("TKA_PICKUPS", Scoring.ScoreFlags.None, "pickups");
        GS.DeclareColumn("TKA_CARRIERKILLS", Scoring.ScoreFlags.None, "bckills");
        GS.DeclareColumn("TKA_BCTIME", Scoring.ScoreFlags.None, "bctime");
        GS.SetSortKeys(GS.Score, GS.Field("TKA_BCTIME"));
        GS.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTION(tka, GiveFragsForKill): no normal frags are counted in TKA (only the team
        // killac/bckill bonuses score). Zero the per-kill frag and claim the hook so the frag path is suppressed
        // while tka is active.
        _giveFragsHandler ??= OnGiveFragsForKill;
        MutatorHooks.GiveFragsForKill.Add(_giveFragsHandler);

        // QC MUTATOR_HOOKFUNCTION(tka, Damage_Calculate): scale player-vs-player damage/force by the possession
        // matrix. Wiring the live DamageSystem.DamageCalculate hook here makes DamageScale/DamageForceScale
        // actually affect combat (registry tka.damage.matrix — previously zero callers).
        _damageCalcHandler ??= OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);

        // QC MUTATOR_HOOKFUNCTION(tka, PlayerPhysics_UpdateStats): the carrier's top speed is scaled by
        // g_tka_ballcarrier_highspeed (STAT(MOVEVARS_HIGHSPEED) *= ...). The PlayerPhysics path resets
        // SpeedMultiplier to 1 each frame and folds it after this hook, so a pure multiply composes with
        // powerup/buff factors (registry tka.carrier.highspeed — previously missing).
        _physicsHandler ??= OnPlayerPhysics;
        MutatorHooks.PlayerPhysics.Add(_physicsHandler);

        // QC MUTATOR_HOOKFUNCTION(tka, PlayerUseKey): a carrier pressing +use drops the ball (tka_DropEvent)
        // and consumes the press. Wires the use-key drop trigger live (registry tka.ball.drop; the death-drop
        // path is OnDeath, the observer/disconnect paths are OnPlayerRemoved below).
        _useKeyHandler ??= OnUseKey;
        MutatorHooks.PlayerUseKey.Add(_useKeyHandler);

        // QC MUTATOR_HOOKFUNCTION(tka, PreferPlayerScore_Clear): TKA always prefers to KEEP player scores when
        // g_score_resetonjoin == -1 (the distinguishing TKA rule — persistent team score across specs).
        // Subscribed here so GameScores.ClearPlayerOnJoin can call the hook on rejoin (registry tka.score.preferplayerscore).
        _preferScoreClearHandler ??= OnPreferPlayerScoreClear;
        MutatorHooks.PreferPlayerScore_Clear.Add(_preferScoreClearHandler);
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tka, PlayerUseKey): a carrier pressing +use drops the ball (tka_DropEvent)
    /// and consumes the press (returns true). Otherwise the press falls through to other handlers.</summary>
    private bool OnUseKey(ref MutatorHooks.PlayerUseKeyArgs args)
    {
        if (MatchEnded || args.Player is not Player player)
            return false;
        if (!ReferenceEquals(Carrier, player))
            return false; // QC: only fires `if(player.ballcarried)`
        DropBall();
        return true; // QC tka PlayerUseKey returns true when it dropped (consumes the +use)
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tka, PreferPlayerScore_Clear): TKA always returns true, meaning the
    /// player's accumulated score is KEPT when rejoining (the distinguishing TKA rule is persistent team score
    /// across specs). Under g_score_resetonjoin -1, this veto prevents the score wipe.</summary>
    private bool OnPreferPlayerScoreClear(ref MutatorHooks.PreferPlayerScore_ClearArgs args)
    {
        return true; // QC: TKA always prefers to keep score (return true to veto the clear)
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tka, MakePlayerObserver) and MUTATOR_HOOKFUNCTION(tka, ClientDisconnect):
    /// both loop `while(player.ballcarried) tka_DropEvent(player)` — a carrier demoted to spectator or who
    /// disconnects relinquishes the ball. Wired through the shared <see cref="GameType.OnPlayerRemoved"/> seam,
    /// fired live by ClientManager.PutObserverInServer + ClientDisconnect (registry tka.ball.drop).</summary>
    public override void OnPlayerRemoved(Player player)
    {
        if (MatchEnded)
            return;
        if (ReferenceEquals(Carrier, player))
            DropBall(); // QC: while(player.ballcarried) tka_DropEvent(player)
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tka, PlayerPhysics_UpdateStats): scale the carrier's top speed by
    /// g_tka_ballcarrier_highspeed (STAT(MOVEVARS_HIGHSPEED, player) *= ...).</summary>
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        if (args.Player is Player player && ReferenceEquals(Carrier, player))
            player.SpeedMultiplier *= BallCarrierHighspeed;
        return false;
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a TKA player column (no-op if unregistered).</summary>
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
        if (_giveFragsHandler is not null)
        {
            MutatorHooks.GiveFragsForKill.Remove(_giveFragsHandler);
            _giveFragsHandler = null;
        }
        if (_damageCalcHandler is not null)
        {
            MutatorHooks.DamageCalculate.Remove(_damageCalcHandler);
            _damageCalcHandler = null;
        }
        if (_physicsHandler is not null)
        {
            MutatorHooks.PlayerPhysics.Remove(_physicsHandler);
            _physicsHandler = null;
        }
        if (_useKeyHandler is not null)
        {
            MutatorHooks.PlayerUseKey.Remove(_useKeyHandler);
            _useKeyHandler = null;
        }
        if (_preferScoreClearHandler is not null)
        {
            MutatorHooks.PreferPlayerScore_Clear.Remove(_preferScoreClearHandler);
            _preferScoreClearHandler = null;
        }
    }

    /// <summary>QC tka_TouchEvent: a player picked up the ball and is now the carrier (attaches the entity,
    /// plays the pickup sfx + notifications, credits TKA_PICKUPS).</summary>
    public void GiveBall(Player carrier)
    {
        if (MatchEnded || carrier.IsDead || Carrier is not null)
            return;
        Carrier = carrier;
        AddCol(carrier, "TKA_PICKUPS", 1); // QC tka_TouchEvent: GameRules_scoring_add(toucher, TKA_PICKUPS, 1)
        _bcTimeRemainder = 0f;             // fresh carry-time accumulator for the new carrier
        if (BallEntity is Entity e)
        {
            global::XonoticGodot.Common.Gameplay.BallEntity.AttachToCarrier(e, carrier, Vector3.Zero);
            e.Think = null;   // carried-ball orbit is driven from Tick, not the loose-ball relocate think
            e.NextThink = 0f; // carried ball has no respawn think
            SoundSystem.PlayOn(e, Sounds.ByName("KA_PICKEDUP")); // QC SND_KA_PICKEDUP, ATTEN_NONE
        }

        // QC tka_TouchEvent messages: kill-feed line to all + centerprint to everyone-except + self centerprint.
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "KEEPAWAY_PICKUP", carrier.NetName);
        NotificationSystem.Send(NotifBroadcast.AllExcept, carrier, MsgType.Center, "KEEPAWAY_PICKUP", carrier.NetName);
        NotificationSystem.Send(NotifBroadcast.One, carrier, MsgType.Center, "KEEPAWAY_PICKUP_SELF");
    }

    /// <summary>QC tka_DropEvent: the ball was dropped / reset; detach it, drop it (with crandom scatter + the
    /// 0.5 s self-recapture lockout), re-arm the respawn, and play the drop sfx + notifications.</summary>
    public void DropBall()
    {
        Player? carrier = Carrier;
        Carrier = null;
        if (BallEntity is Entity e)
        {
            // Detach + drop at the carrier's feet with the crandom scatter, arm the 0.5 s self-recapture lockout,
            // and re-arm the relocate timer (QC tka_DropEvent: setattachment NULL + '0 0 200'+crandom + wait).
            global::XonoticGodot.Common.Gameplay.BallEntity.DropFromCarrier(e, RespawnTime, takesDamage: true);
            e.Think = RespawnBallThink;
            e.Alpha = 1f; // QC tka_DropEvent: ball.alpha = 1 — restore full opacity in case the carrier had an invisibility effect
        }
        if (carrier is null)
            return;

        // QC tka_DropEvent messages and sounds: kill-feed line to all + centerprint to all + global drop sfx.
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "KEEPAWAY_DROPPED", carrier.NetName);
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "KEEPAWAY_DROPPED", carrier.NetName);
        SoundSystem.PlayGlobal(Sounds.ByName("KA_DROPPED")); // QC SND_KA_DROPPED, NULL emitter, ATTEN_NONE
    }

    /// <summary>The current score for a team color code (QC teamscores(team, ST_SCORE); 0 if none).</summary>
    public int ScoreFor(int team) => GS.TeamScore(team, GS.TeamSlotScore);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on team points (ST_SCORE),
    /// so a tied timed Team Keepaway enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(GS.LeaderTeam(), GS.SecondTeam(), ScoreFor);

    /// <summary>g_tka_score_team (default 1): any teammate's kill scores while the team holds the ball.</summary>
    private bool ScoreTeam => TryCvar(CvarScoreTeam, out float v) ? v != 0f : DefaultScoreTeam;

    /// <summary>QC team-has-ball scan: does <paramref name="team"/> currently hold the (single) ball? In Base this
    /// is <c>IL_EACH(g_tkaballs, SAME_TEAM(it.owner, frag_attacker))</c>; with one ball it is simply whether the
    /// carrier is on that team.</summary>
    private bool TeamHasBall(int team) => team != Teams.None && BallTeam == team;

    /// <summary>
    /// QC sv_tka.qc MUTATOR_HOOKFUNCTION(tka, Bot_ForbidAttack): "if neither player has the ball then don't attack
    /// unless the ball is on the ground". A bot is forbidden from attacking <paramref name="targ"/> when neither
    /// it nor the target carries the ball AND the ball is currently held (by anyone) AND it is NOT the case that
    /// g_tka_score_team is on and the bot's team holds the ball (in which case any kill scores, so attacking is fine).
    /// With one ball, "ball is held" = a carrier exists.
    /// </summary>
    public bool ForbidBotAttack(Player bot, Player targ)
    {
        bool haveHeldBall = Carrier is not null;
        bool targCarries = ReferenceEquals(Carrier, targ);
        bool botCarries = ReferenceEquals(Carrier, bot);
        bool teamHasBall = ScoreTeam && TeamHasBall((int)bot.Team);
        return !targCarries && !botCarries && haveHeldBall && !teamHasBall;
    }

    /// <summary>
    /// The obituary handler — QC sv_tka.qc PlayerDies, applied per-team. A cross-team frag scores for the
    /// attacker's team when the attacker personally carries OR (g_tka_score_team) a teammate holds the ball:
    /// the team gains <c>g_tka_score_killac</c>, plus <c>g_tka_score_bckill</c> + a TKA_CARRIERKILLS credit when
    /// the victim was the (enemy) ball-carrier. DM frag points are never awarded (handled by GiveFragsForKill).
    /// Then drop the ball if the carrier died and re-check the limit.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;
        // QC PlayerDies guard: frag_attacker != frag_target && IS_PLAYER(frag_attacker) && DIFF_TEAM(attacker, target).
        bool scoringKill = attacker is not null
            && !ReferenceEquals(attacker, victim)
            && !Teams.SameTeam(attacker, victim);

        // QC: the bonus for fragging the enemy ball-carrier is checked BEFORE the ball drops below.
        bool victimWasCarrier = ReferenceEquals(Carrier, victim);

        if (scoringKill && attacker is not null)
        {
            int team = (int)attacker.Team;
            bool attackerCarries = ReferenceEquals(Carrier, attacker);
            bool teamHasBall = ScoreTeam && TeamHasBall(team);

            if (victimWasCarrier)
            {
                // QC: GameRules_scoring_add(attacker, TKA_CARRIERKILLS, 1) + team bckill bonus (independent of
                // whether the attacker carries — any cross-team kill of the carrier counts).
                AddCol(attacker, "TKA_CARRIERKILLS", 1);
                AddTeamScore(team, ScoreBcKill);
            }
            else if (!attackerCarries && !teamHasBall)
            {
                // QC: a no-possession kill warns the killer (only when not team-scoring with the ball held).
                if (TryCvar(CvarNonCarrierWarn, out float warn) && warn != 0f)
                    NotificationSystem.Send(NotifBroadcast.OneOnly, attacker, MsgType.Center, "KEEPAWAY_WARN");
            }

            // QC: add killac to the team if the attacker carries OR (team scoring on AND the team holds the ball).
            if (attackerCarries || teamHasBall)
                AddTeamScore(team, ScoreKillAc);
        }

        // If the carrier died, the ball drops where they fell (QC PlayerDies → tka_DropEvent).
        if (victimWasCarrier)
            DropBall();

        Events?.OnFrag(attacker, victim, ev.DeathType);
        CheckPointLimit();
        return false;
    }

    private void AddTeamScore(int team, int delta)
    {
        if (team == Teams.None || delta == 0)
            return;
        GS.AddToTeam(team, GS.TeamSlotScore, delta); // QC GameRules_scoring_add_team(attacker, SCORE, delta)
    }

    /// <summary>g_tka_score_timepoints (default 0/off): team points per second while a teammate carries.</summary>
    private int ScoreTimePoints => TryCvar(CvarScoreTimePoint, out float v) ? (int)v : 0;

    /// <summary>
    /// Advance Team Keepaway one step (QC tka_BallThink_Carried): while the ball is carried, accrue
    /// <see cref="ScoreTimePoints"/> per second to the carrier's TEAM score (carrying the fractional remainder
    /// like QC's float2int accumulator) and TKA_BCTIME to the carrier, orbit the carried ball, then re-check the
    /// limit. <paramref name="dt"/> is the frame delta (QC frametime). Call each tick; safe after MatchEnded.
    /// </summary>
    public void Tick(float dt)
    {
        if (MatchEnded)
        {
            CheckPointLimit();
            return;
        }

        Player? carrier = Carrier;
        if (carrier is not null && !carrier.IsDead)
        {
            int team = (int)carrier.Team;

            // QC tka_BallThink_Carried: if (g_tka_score_timepoints) GameRules_scoring_add_team_float2int(owner,
            // SCORE, timepoints*frametime, ...). Bank whole points to the team and carry the fractional remainder.
            int pps = ScoreTimePoints;
            if (pps > 0)
            {
                _timePointsRemainder += pps * dt;
                int whole = (int)_timePointsRemainder; // floor toward zero (pps, dt >= 0)
                if (whole != 0)
                {
                    _timePointsRemainder -= whole;
                    AddTeamScore(team, whole);
                }
            }

            // QC tka_BallThink_Carried: GameRules_scoring_add(owner, TKA_BCTIME, frametime). Integer column, so
            // bank whole seconds and carry the fractional remainder.
            _bcTimeRemainder += dt;
            int wholeSecs = (int)_bcTimeRemainder;
            if (wholeSecs != 0)
            {
                _bcTimeRemainder -= wholeSecs;
                AddCol(carrier, "TKA_BCTIME", wholeSecs);
            }

            // QC tka_BallThink_Carried orbit anim: the carried ball orbits the carrier in the xy plane (single
            // ball, so cnt=chainCount=1). Keeps the ball entity riding the carrier so the model/glow tracks them.
            if (BallEntity is Entity e)
            {
                Vector3 pos = global::XonoticGodot.Common.Gameplay.BallEntity.CarryOrbit(
                    carrier.Origin, GametypeEntities.Now, cnt: 1, chainCount: 1);
                GametypeEntities.SetOrigin(e, pos);
                // QC tka_BallThink_Carried: this.alpha = this.owner.alpha — sync the invisibility effect from the
                // carrier to the ball entity each frame (Entity.Alpha is defined on the partial Entity in
                // DamageEntityState.cs; setting it here lets the client-side binding read and apply it).
                e.Alpha = carrier.Alpha;
            }
        }

        CheckPointLimit();
    }

    /// <summary>QC GameRules_limit_score / GameRules_limit_lead: latch the match once a team reaches the point
    /// limit, or leads the runner-up by the lead limit.</summary>
    public void CheckPointLimit()
    {
        if (MatchEnded)
            return;

        int leaderTeam = GS.LeaderTeam();
        if (leaderTeam == Teams.None)
            return;
        int leaderScore = ScoreFor(leaderTeam);

        // QC GameRules_limit_score: the leading team reaching the point limit wins.
        int limit = PointLimit;
        if (limit > 0 && leaderScore >= limit)
        {
            MatchEnded = true;
            WinningTeam = leaderTeam;
            return;
        }

        // QC GameRules_limit_lead: the leader winning by at least the lead limit ends the match.
        int lead = LeadLimit;
        int secondTeam = GS.SecondTeam();
        int secondScore = secondTeam == Teams.None ? 0 : ScoreFor(secondTeam);
        if (lead > 0)
        {
            if (leaderScore - secondScore >= lead)
            {
                MatchEnded = true;
                WinningTeam = leaderTeam;
                return;
            }
        }

        // QC MUTATOR_HOOKFUNCTION(tka, Scores_CountFragsRemaining) returns !autocvar_g_tka_score_timepoints:
        // announce "N points left" once as the leading team approaches the point limit, but only when
        // timed-possession scoring is off (time-points makes the score continuous, rendering 1/2/3 meaningless).
        bool timePoints = TryCvar(CvarScoreTimePoint, out float tp) && tp != 0f;
        if (!timePoints)
            GS.CountFragsRemaining(limit, lead, leaderScore, secondScore, suddenDeathEnding: false);
    }

    private int ScoreKillAc => TryCvar(CvarScoreKillAc, out float v) ? (int)v : DefaultScoreKillAc;
    private int ScoreBcKill => TryCvar(CvarScoreBcKill, out float v) ? (int)v : DefaultScoreBcKill;

    // ----- waypoint tracking cvar (g_tkaball_tracking: 0=none / 1=always / 2=dropped-only; QC default 1) -----
    private const string CvarBallTracking = "g_tkaball_tracking";
    /// <summary>QC autocvar_g_tkaball_tracking (default 1): 0 = no waypoint at all; 1 = always track the
    /// ball/carrier; 2 = only the loose ball is shown to enemies (the carrier sprite is hidden from them — see
    /// <see cref="CarrierWaypointVisibleFor"/>, which returns the QC <c>tracking == 1</c> for the enemy case).</summary>
    private int BallTracking => TryCvar(CvarBallTracking, out float v) ? (int)v : 1;

    /// <summary>
    /// QC tka_RespawnBall (WaypointSprite_Spawn WP_KaBall on the loose ball, '0 0 64') + tka_TouchEvent
    /// (WaypointSprite_AttachCarrier WP_TkaBallCarrier{Red,Blue,Yellow,Pink} on the carrier, colormod by team,
    /// SPRITERULE_TEAMPLAY). One sprite for the (single) ball, rebuilt each tick from its live state — a
    /// TEAM-COLORED carrier sprite riding the carrier when held, the neutral KaBall when loose. Honors
    /// g_tkaball_tracking (0 = none, 1 = track); QC also always shows it during warmup, but the host pull path
    /// has no warmup flag here, so the cvar governs.
    /// </summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        if (BallEntity is null)
            return;
        if (BallTracking == 0)
            return; // QC: g_tkaball_tracking 0 → no waypoint at all

        Player? carrier = Carrier;
        if (carrier is not null)
        {
            // QC tka_TouchEvent: WaypointSprite_AttachCarrier with WP_TkaBallCarrier<team> + colormapPaletteColor,
            // SPRITERULE_TEAMPLAY, RADARICON_FLAGCARRIER, plus the per-frame
            // tka_ballcarrier_waypointsprite_visible_for_player predicate. The sprite name + radar tint come from
            // the carrier's team; the predicate (CarrierWaypointVisibleFor) governs the per-viewer visibility,
            // including the g_tkaball_tracking == 1 vs == 2 (dropped-only) distinction for enemies.
            int team = (int)carrier.Team;
            Player held = carrier;
            into.Add(new Waypoints.WaypointSprite
            {
                // QC tka_TouchEvent WaypointSprite_UpdateSprites(spr, WP_TkaBallCarrier<team>, WP_KaBallCarrier,
                // WP_TkaBallCarrier<team>) (sv_tka.qc:169): SPRITERULE_TEAMPLAY three-image — ENEMY (model1) +
                // SPECTATOR (model3) see the team-colored TkaBallCarrier<team>; the OWN team (model2) sees the
                // neutral WP_KaBallCarrier.
                SpriteName = CarrierSpriteName(team),       // model1 (enemy)
                SpriteNameOwn = "KaBallCarrier",           // model2 (own team)
                SpriteNameSpec = CarrierSpriteName(team),  // model3 (spectator)
                Owner = carrier,
                Offset = new System.Numerics.Vector3(0f, 0f, 64f),
                Team = team, // colormod / radar tint keyed on the carrier's team
                Color = TeamRadarColor(team),
                RadarIcon = 1, // RADARICON_FLAGCARRIER
                Rule = Waypoints.SpriteRule.Teamplay, // QC WaypointSprite_UpdateRule(..., SPRITERULE_TEAMPLAY)
                VisibleForPlayer = viewer => CarrierWaypointVisibleFor(held, viewer),
                Health = -1f,
            });
            return;
        }

        // QC tka_RespawnBall / tka_DropEvent: WaypointSprite_Spawn(WP_KaBall, ..., ball, '0 0 64', team 0,
        // SPRITERULE_DEFAULT) — a neutral marker on the loose ball.
        into.Add(new Waypoints.WaypointSprite
        {
            SpriteName = "KaBall",
            FixedOrigin = BallEntity.Origin + new System.Numerics.Vector3(0f, 0f, 64f),
            Team = Teams.None,
            RadarIcon = 1,
            Health = -1f,
        });
    }

    /// <summary>
    /// QC sv_tka.qc tka_ballcarrier_waypointsprite_visible_for_player: the per-viewer visibility predicate on the
    /// carrier waypoint. Spectators and the carrier's own team always see it; during warmup everyone does; otherwise
    /// an enemy sees it only when <c>g_tkaball_tracking == 1</c> (so <c>== 2</c> = dropped-only hides the carrier
    /// sprite from enemies). The QC <c>IS_INVISIBLE(owner)</c> hide is ported (the carrier sprite is hidden from
    /// enemies while the carrier holds the invisibility powerup). The remaining spectators-of-the-carrier nuance is
    /// approximated (the predicate has no spectatee field here).
    /// </summary>
    private bool CarrierWaypointVisibleFor(Player carrier, Player? viewer)
    {
        if (viewer is null)
            return false;
        // QC: if(IS_SPEC(player) || warmup_stage || SAME_TEAM(player, this.owner)) return true;
        if (viewer.IsObserver || NotificationSystem.WarmupStage || Teams.SameTeam(viewer, carrier))
            return true;
        // QC: if(IS_INVISIBLE(this.owner)) return false; — hide the carrier sprite from ENEMIES while the carrier
        // holds the invisibility powerup (this is AFTER the spec/warmup/same-team early-return, so spectators and
        // teammates still see it). Mirrors Keepaway.CarrierWaypointVisibleFor (which already ports this hide).
        var invisEffect = StatusEffectsCatalog.ByName("invisibility");
        if (invisEffect is not null && StatusEffectsCatalog.Has(carrier, invisEffect))
            return false;
        // QC: return autocvar_g_tkaball_tracking == 1; (enemies see the carrier only in the always-track mode,
        // not in the dropped-only mode 2).
        return BallTracking == 1;
    }

    /// <summary>QC tka_TouchEvent WaypointSprite_UpdateSprites switch (NUM_TEAM_1..4 → WP_TkaBallCarrier
    /// Red/Blue/Yellow/Pink; WP_KaBallCarrier as the default-team fallback).</summary>
    private static string CarrierSpriteName(int team) => team switch
    {
        Teams.Red => "TkaBallCarrierRed",
        Teams.Blue => "TkaBallCarrierBlue",
        Teams.Yellow => "TkaBallCarrierYellow",
        Teams.Pink => "TkaBallCarrierPink",
        _ => "KaBallCarrier",
    };

    /// <summary>Team → radar tint (QC colormapPaletteColor(team-1, 0); neutral handled by the loose-ball path).</summary>
    private static System.Numerics.Vector3 TeamRadarColor(int team) => team switch
    {
        Teams.Red => new System.Numerics.Vector3(1f, 0.0625f, 0.0625f),
        Teams.Blue => new System.Numerics.Vector3(0.0625f, 0.0625f, 1f),
        Teams.Yellow => new System.Numerics.Vector3(1f, 1f, 0.0625f),
        Teams.Pink => new System.Numerics.Vector3(1f, 0.0625f, 1f),
        _ => new System.Numerics.Vector3(1f, 1f, 1f),
    };

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
