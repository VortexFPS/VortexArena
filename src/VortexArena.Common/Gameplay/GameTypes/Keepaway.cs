using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Keepaway — port of <c>CLASS(Keepaway, Gametype)</c>
/// (common/gametypes/gametype/keepaway/{keepaway.qh,sv_keepaway.qc}). A single ball sits on the map;
/// hold it to score. The carrier earns points over time (g_keepaway_score_timepoints) and a bonus per
/// kill made while carrying (g_keepaway_score_killac); killing the carrier earns the killer a bonus
/// (g_keepaway_score_bckill). First player to the point limit (g_keepaway_point_limit, default 30) wins.
///
/// NOTE on TeamGame: in Xonotic, Keepaway is a FREE-FOR-ALL objective mode — its gametype flags are
/// <c>GAMETYPE_FLAG_USEPOINTS</c> only, with NO <c>GAMETYPE_FLAG_TEAMPLAY</c> (see keepaway.qh
/// gametype_init "timelimit=20 pointlimit=30", no teams= arg). We therefore set <see cref="TeamGame"/> =
/// false to stay faithful to the QC source. (The team variant is a SEPARATE gametype, "tka" /
/// TeamKeepaway, common/gametypes/gametype/tka — out of scope here.) The brief grouped Keepaway with the
/// team modes; the faithful port keeps it FFA, with per-player scoring rather than per-team.
///
/// QC defaults: "timelimit=20 pointlimit=30".
///
/// Faithfully ported (Godot-free essence):
///  - a single ball with a carrier (<see cref="BallState"/>, QC .ballcarried / keepawayball entity);
///  - pickup / drop (<see cref="PickUp"/>/<see cref="Drop"/>, QC ka_TouchEvent / ka_DropEvent);
///  - per-frame time scoring for the carrier (<see cref="Tick"/>, QC ka_BallThink_Carried →
///    g_keepaway_score_timepoints * frametime) and the kill bonuses on the obituary bus
///    (carrier-kill bonus to the killer, kill-while-carrying bonus to the carrier);
///  - point-limit win condition (pointlimit).
///
/// Faithfully ported (objective layer):
///  - a single world ball entity spawned at map start (<see cref="SpawnBall"/>, QC ka_SpawnBalls) with
///    touch = pickup and a respawn timer when loose (<see cref="RespawnBall"/>, QC ka_RespawnBall);
///  - pickup attaches the ball to the carrier, drop detaches and re-arms the respawn (<see cref="PickUp"/>/
///    <see cref="Drop"/>, QC ka_TouchEvent / ka_DropEvent);
///  - the ballcarrier damage/force scaling matrix (<see cref="DamageScale"/>, QC Damage_Calculate
///    ballcarrier/noncarrier damage/force).
///
/// Wired in Wave 2: the procedural ball now spawns through the shared <see cref="BallEntity"/> framework
/// (<see cref="SpawnBall"/>/<see cref="AdoptBall"/>, driven by the host round-drive seam), pickup/drop/world-touch
/// play their KA sounds + notifications (<see cref="PickUp"/>/<see cref="Drop"/>/<see cref="BallTouchEntity"/>),
/// the carried ball orbits its carrier each <see cref="Tick"/>, the 0.5 s self-recapture lockout is honored, the
/// possession damage/force matrix reads the real vector cvars (<see cref="DamageScale"/>), and normal frags are
/// suppressed (<see cref="OnGiveFragsForKill"/>). The HUD KA_CARRYING mod-icon reads <see cref="Ball"/>.Carrier
/// via the net status block, so simply maintaining the carrier feeds it.
///
/// Deferred (NOTE — cross-boundary): the ball model/glow/effects + respawn/spark particles + waypoint sprites
/// (CSQC presentation), the carried-ball invisibility alpha sync, multiple-ball chaining
/// (g_keepaway_ballcarrier_maxballs), the carrier highspeed PlayerPhysics modifier (g_keepaway_ballcarrier_highspeed),
/// and the use-key / disconnect / observe drop triggers (need the PlayerUseKey / MakePlayerObserver / ClientDisconnect
/// / DropSpecialItems mutator hooks, which are not yet on MutatorHooks — see todos). Death-drop IS wired (OnDeath).
/// </summary>
[GameType]
public sealed class Keepaway : GameType
{
    // ----- point-limit cvars + default (g_keepaway_point_limit; gametype_init pointlimit=30) -----
    private const string CvarPointLimitKa = "g_keepaway_point_limit";
    private const string CvarPointLimit   = "fraglimit";
    private const float  DefaultPointLimit = 30f;

    // ----- score cvars + defaults (g_keepaway_score_*) -----
    private const string CvarScoreTimePoints = "g_keepaway_score_timepoints"; // points/sec while carrying
    private const string CvarScoreKillAc     = "g_keepaway_score_killac";     // bonus per kill while carrying
    private const string CvarScoreBcKill     = "g_keepaway_score_bckill";     // bonus for killing the carrier
    private const float  DefaultScoreTimePoints = 0f; // QC default off (time scoring opt-in)
    // QC keepaway.cfg / autocvar defaults: g_keepaway_score_killac 1, g_keepaway_score_bckill 1 (BIT(0)-style
    // bonus of 1 each). When the server cfg is unloaded (headless/tests) these fallbacks must award 1, not 0,
    // to match Base — see registry keepaway.score.killbonuses.
    private const float  DefaultScoreKillAc     = 1f;
    private const float  DefaultScoreBcKill     = 1f;

    /// <summary>The (single) ball (QC keepawayball / g_kaballs[0]).</summary>
    public readonly BallState Ball = new();

    /// <summary>Time-points remainder accumulator (QC float2int_decimal_fld) so fractional points carry over.</summary>
    private float _timePointsRemainder;

    public bool MatchEnded { get; private set; }

    /// <summary>The current points leader (highest <see cref="Player.ScoreFrags"/>), or null.</summary>
    public Player? Leader { get; private set; }

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.GiveFragsForKillArgs>? _giveFragsHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<MutatorHooks.PlayerUseKeyArgs>? _useKeyHandler;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _physicsHandler;
    private HookHandler<MutatorHooks.PreferPlayerScore_ClearArgs>? _preferScoreClearHandler;

    // QC g_keepaway_noncarrier_warn (gametypes-server.cfg:446 = 1): centerprint-warn a non-carrier who frags
    // another non-carrier, nudging them toward the ball. Read in OnDeath (registry keepaway.warn.noncarrier).
    private const string CvarNonCarrierWarn = "g_keepaway_noncarrier_warn";

    // QC g_keepaway_ballcarrier_highspeed: the carrier's MOVEVARS_HIGHSPEED multiplier (default 1 = no effect).
    private const string CvarBallCarrierHighspeed = "g_keepaway_ballcarrier_highspeed";
    /// <summary>QC autocvar_g_keepaway_ballcarrier_highspeed (default 1): speed multiplier applied to the ball carrier.</summary>
    public float BallCarrierHighspeed => TryCvar(CvarBallCarrierHighspeed, out float v) && v > 0f ? v : 1f;

    public Keepaway()
    {
        NetName = "ka";
        DisplayName = "Keepaway";
        TeamGame = false; // QC: Keepaway is FFA (no GAMETYPE_FLAG_TEAMPLAY). See class remarks.
    }

    /// <summary>The single ball's world entity (QC keepawayball edict), or null (headless).</summary>
    public Entity? BallEntity { get; private set; }

    /// <summary>
    /// QC <c>ka_EventLog</c> (sv_keepaway.qc:44-48) — the server gamelog echo seam. When wired (the host calls
    /// <c>GameWorld.ActivateGameType</c>), <see cref="PickUp"/> / <see cref="Drop"/> emit
    /// <c>:ka:pickup:&lt;playerid&gt;</c> / <c>:ka:dropped:&lt;playerid&gt;</c> to the server event log (gated by
    /// <c>sv_eventlog</c> at the host call site). When null (headless / tests) the echo is a no-op.
    /// QC: <c>if(autocvar_sv_eventlog) GameLogEcho(strcat(":ka:", mode, ((actor != NULL) ? strcat(":", ftos(actor.playerid)) : "")))</c>.
    /// </summary>
    public Action<string>? EventLogEcho { get; set; }

    /// <summary>QC ka_EventLog: emit <c>:ka:&lt;mode&gt;[:&lt;playerid&gt;]</c> through the host event-log seam.</summary>
    private void EmitEventLog(string mode, Player? actor)
        => EventLogEcho?.Invoke(actor is not null ? ":ka:" + mode + ":" + actor.PlayerId : ":ka:" + mode);

    // ----- ball respawn + damage-scaling cvars (g_keepaway*) -----
    private const string CvarRespawnTime = "g_keepawayball_respawntime"; // loose-ball relocate timer

    public override void OnInit()
    {
        // QC ka_SpawnBalls: one ball is spawned at map start (see SpawnBall). The host calls SpawnBall once
        // the map is loaded; OnInit clears the ball reference. GameRules USEPOINTS is the engine's job.
        BallEntity = null;
        Ball.Carrier = null;
    }

    /// <summary>QC g_keepawayball_respawntime: seconds a loose ball waits before relocating itself.</summary>
    public float RespawnTime => TryCvar(CvarRespawnTime, out float v) && v > 0f ? v : 10f;

    /// <summary>Keepaway is free-for-all; there are no teams (a single ball, individual scoring).</summary>
    public int TeamCount => 0;

    /// <summary>QC FFA equality (server/scores.qc:537): the top two players are tied on the primary score, so a
    /// tied timed Keepaway enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster) => FfaTie.TopTwoTied(roster);

    /// <summary>Point limit in force (g_keepaway_point_limit, else fraglimit, else 30). 0 == unlimited.</summary>
    public float PointLimit
    {
        get
        {
            // QC GameRules_limit_score(autocvar_g_keepaway_point_limit): the ka cvar default is -1, which means
            // "use the mapinfo limit" (gametypes-server.cfg:436; keepaway.qh gametype_init pointlimit=30). A
            // value > 0 is an explicit override; 0 means "no limit". So map -1 to the mapinfo/gametype_init
            // default (30) — without this the cfg's -1 leaks through and the limit>0 guard plays the match with
            // NO point limit, letting a carrier run past Base's 30-point end (registry keepaway.win.pointlimit).
            if (TryCvar(CvarPointLimitKa, out float pl) && pl != -1f) return pl;
            if (TryCvar(CvarPointLimit, out float fl)) return fl;
            return DefaultPointLimit;
        }
    }

    public float ScoreTimePoints => TryCvar(CvarScoreTimePoints, out float v) ? v : DefaultScoreTimePoints;
    public float ScoreKillAc     => TryCvar(CvarScoreKillAc,     out float v) ? v : DefaultScoreKillAc;
    public float ScoreBcKill     => TryCvar(CvarScoreBcKill,     out float v) ? v : DefaultScoreBcKill;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Leader = null;
        _timePointsRemainder = 0f;
        _bcTimeRemainder = 0f;
        Ball.Carrier = null;

        // QC sv_keepaway.qh GameRules_scoring(0, SFL_SORT_PRIO_PRIMARY, 0, { field(SP_KEEPAWAY_PICKUPS, "pickups",
        // 0); field(SP_KEEPAWAY_CARRIERKILLS, "bckills", 0); field(SP_KEEPAWAY_BCTIME, "bctime", SECONDARY); }):
        // SP_SCORE is the player primary (time-points + kill bonuses), SP_KEEPAWAY_BCTIME the secondary.
        GS.ScoreRulesBasics(teams: false);
        GS.DeclareColumn("KEEPAWAY_PICKUPS", Scoring.ScoreFlags.None, "pickups");
        GS.DeclareColumn("KEEPAWAY_CARRIERKILLS", Scoring.ScoreFlags.None, "bckills");
        GS.DeclareColumn("KEEPAWAY_BCTIME", Scoring.ScoreFlags.None, "bctime");
        GS.SetSortKeys(GS.Score, GS.Field("KEEPAWAY_BCTIME"));

        // QC fragsleft_last reset: re-arm the remaining-frags announcer so "1/2/3 frags left"
        // (ANNCE_REMAINING_FRAG_{1,2,3}) can fire again this match. ka's Scores_CountFragsRemaining hook
        // returns !g_keepaway_score_timepoints, so by default (timepoints 0) the cue is enabled.
        GS.ResetFragsRemaining();

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTION(ka, GiveFragsForKill): no normal frags are counted in Keepaway (only the
        // ka-specific time/kill bonuses score). Zero the per-kill frag and claim the hook so the FFA frag path is
        // suppressed while ka is active — without this the normal +1 kill frag would leak into the ka score.
        _giveFragsHandler ??= OnGiveFragsForKill;
        MutatorHooks.GiveFragsForKill.Add(_giveFragsHandler);

        // QC MUTATOR_HOOKFUNCTION(ka, Damage_Calculate): scale player-vs-player damage/force by the possession
        // matrix. Wiring the live DamageSystem.DamageCalculate hook here makes DamageScale/DamageForceScale
        // actually affect combat (registry keepaway.damage.matrix — previously zero callers).
        _damageCalcHandler ??= OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);

        // QC MUTATOR_HOOKFUNCTION(ka, PlayerUseKey): the +use key in a carrier's hands drops the ball
        // (ka_DropEvent) and consumes the press. Wires the use-key drop trigger live (registry keepaway.ball.drop).
        _useKeyHandler ??= OnUseKey;
        MutatorHooks.PlayerUseKey.Add(_useKeyHandler);

        // QC MUTATOR_HOOKFUNCTION(ka, PlayerPhysics_UpdateStats): the carrier's top speed is scaled by
        // g_keepaway_ballcarrier_highspeed (STAT(MOVEVARS_HIGHSPEED) *= ...). The PlayerPhysics path resets
        // SpeedMultiplier to 1 each frame and folds it after this hook, so a pure multiply composes with
        // powerup/buff factors (registry keepaway.carrier.highspeed — previously missing).
        _physicsHandler ??= OnPlayerPhysics;
        MutatorHooks.PlayerPhysics.Add(_physicsHandler);

        // QC MUTATOR_HOOKFUNCTION(ka, PreferPlayerScore_Clear) { return true; }: ka is FFA, so it always prefers
        // to keep the per-player score (vetoes the team-score-preference clear). Under g_score_resetonjoin -1 this
        // veto is consulted by GameScores.ClearPlayerOnJoin on rejoin (registry keepaway.score.preferplayerscore).
        _preferScoreClearHandler ??= OnPreferPlayerScoreClear;
        MutatorHooks.PreferPlayerScore_Clear.Add(_preferScoreClearHandler);
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(ka, PreferPlayerScore_Clear): ka always returns true — keep the per-player
    /// FFA score (veto the clear), matching the QC hook that returns true unconditionally.</summary>
    private bool OnPreferPlayerScoreClear(ref MutatorHooks.PreferPlayerScore_ClearArgs args) => true;

    /// <summary>QC MUTATOR_HOOKFUNCTION(ka, PlayerUseKey): a carrier pressing +use drops the ball (ka_DropEvent)
    /// and consumes the press (returns true). Otherwise the press falls through to other handlers.</summary>
    private bool OnUseKey(ref MutatorHooks.PlayerUseKeyArgs args)
    {
        if (MatchEnded || args.Player is not Player player)
            return false;
        if (!ReferenceEquals(Ball.Carrier, player))
            return false; // QC: only fires `if(player.ballcarried)`
        Drop();
        return true; // QC ka PlayerUseKey returns true when it dropped (consumes the +use)
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(ka, PlayerPhysics_UpdateStats): scale the carrier's top speed by
    /// g_keepaway_ballcarrier_highspeed (STAT(MOVEVARS_HIGHSPEED, player) *= ...).</summary>
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        if (args.Player is Player player && ReferenceEquals(Ball.Carrier, player))
            player.SpeedMultiplier *= BallCarrierHighspeed;
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(ka, MakePlayerObserver) and MUTATOR_HOOKFUNCTION(ka, ClientDisconnect):
    /// both call ka_DropEvent — a carrier demoted to spectator or who disconnects relinquishes the ball. Wired
    /// through the shared <see cref="GameType.OnPlayerRemoved"/> seam (registry keepaway.ball.drop).</summary>
    public override void OnPlayerRemoved(Player player)
    {
        if (MatchEnded)
            return;
        if (ReferenceEquals(Ball.Carrier, player))
            Drop(); // QC: while(player.ballcarried) ka_DropEvent(player)
    }

    /// <summary>
    /// QC sv_keepaway.qc MUTATOR_HOOKFUNCTION(ka, Bot_ForbidAttack): "if neither player has the ball then don't
    /// attack unless the ball is on the ground". A bot is forbidden from attacking <paramref name="targ"/> when
    /// neither it nor the target carries the ball AND the ball is currently held (by anyone) — so bots cluster the
    /// carrier instead of fragging bystanders. With one ball, "ball is held" = a carrier exists.
    /// </summary>
    public bool ForbidBotAttack(Player bot, Player targ)
    {
        bool haveHeldBall = Ball.Carrier is not null;
        bool targCarries = ReferenceEquals(Ball.Carrier, targ);
        bool botCarries = ReferenceEquals(Ball.Carrier, bot);
        return !targCarries && !botCarries && haveHeldBall;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(ka, Damage_Calculate): apply the ballcarrier/noncarrier damage+force
    /// matrix to player-vs-player hits (no-op for non-player attacker/target, mirroring the QC IS_PLAYER guard).</summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (args.Attacker is not Player attacker || args.Target is not Player target)
            return false; // QC: only apply scaling to player versus player combat
        args.Damage = DamageScale(attacker, target, args.Damage);
        args.Force *= DamageForceScale(attacker, target);
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(ka, GiveFragsForKill): M_ARGV(2,float)=0; return true. No frags in ka.</summary>
    private bool OnGiveFragsForKill(ref MutatorHooks.GiveFragsForKillArgs args)
    {
        args.FragScore = 0f; // QC: no frags counted in keepaway
        return true;         // QC returns true so GiveFrags reads the zeroed delta back
    }

    /// <summary>Ball-carry-time remainder accumulator (QC SP_KEEPAWAY_BCTIME += frametime), whole seconds banked.</summary>
    private float _bcTimeRemainder;

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a Keepaway player column (no-op if unregistered).</summary>
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
        if (_useKeyHandler is not null)
        {
            MutatorHooks.PlayerUseKey.Remove(_useKeyHandler);
            _useKeyHandler = null;
        }
        if (_physicsHandler is not null)
        {
            MutatorHooks.PlayerPhysics.Remove(_physicsHandler);
            _physicsHandler = null;
        }
        if (_preferScoreClearHandler is not null)
        {
            MutatorHooks.PreferPlayerScore_Clear.Remove(_preferScoreClearHandler);
            _preferScoreClearHandler = null;
        }
    }

    /// <summary>
    /// QC ka_TouchEvent: <paramref name="player"/> picks up the ball. No-op if already carried or the
    /// player is dead. Sets the carrier. Returns true if the pickup happened.
    /// </summary>
    public bool PickUp(Player player)
    {
        if (MatchEnded || player.IsDead || Ball.Carrier is not null)
            return false;
        Ball.Carrier = player;
        Ball.PickupTime = Api.Services is not null ? Api.Clock.Time : 0f;
        AddCol(player, "KEEPAWAY_PICKUPS", 1); // QC ka_TouchEvent: GameRules_scoring_add(toucher, KEEPAWAY_PICKUPS, 1)
        // Attach the ball entity to the carrier (QC ka_TouchEvent setattachment + SOLID_NOT + carried-ball think).
        if (BallEntity is Entity e)
        {
            global::XonoticGodot.Common.Gameplay.BallEntity.AttachToCarrier(e, player, Vector3.Zero);
            e.Think = null;        // carried-ball orbit is driven from Tick, not the loose-ball relocate think
            e.NextThink = 0f;      // carried ball has no respawn think
            SoundSystem.PlayOn(e, Sounds.ByName("KA_PICKEDUP")); // QC SND_KA_PICKEDUP, ATTEN_NONE
        }

        // QC ka_TouchEvent: ka_EventLog("pickup", toucher) — write the :ka:pickup:<playerid> gamelog line.
        EmitEventLog("pickup", player);

        // QC ka_TouchEvent messages: kill-feed line to all + centerprint to everyone-except + self centerprint.
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "KEEPAWAY_PICKUP", player.NetName);
        NotificationSystem.Send(NotifBroadcast.AllExcept, player, MsgType.Center, "KEEPAWAY_PICKUP", player.NetName);
        NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "KEEPAWAY_PICKUP_SELF");
        return true;
    }

    /// <summary>QC ka_DropEvent: the carrier loses the ball (on death, use-key, disconnect, or observe).</summary>
    public void Drop()
    {
        Player? carrier = Ball.Carrier;
        if (carrier is null)
            return;
        Ball.Carrier = null;
        // Detach + drop at the carrier's feet with the crandom scatter, arm the 0.5 s self-recapture lockout, and
        // re-arm the relocate timer (QC ka_DropEvent: setattachment NULL + '0 0 200'+crandom + wait/previous_owner).
        if (BallEntity is Entity e)
        {
            global::XonoticGodot.Common.Gameplay.BallEntity.DropFromCarrier(e, RespawnTime, takesDamage: true);
            e.Think = RespawnBallThink;
        }

        // QC ka_DropEvent: ka_EventLog("dropped", player) — write the :ka:dropped:<playerid> gamelog line.
        EmitEventLog("dropped", carrier);

        // QC ka_DropEvent messages and sounds: kill-feed line to all + centerprint to all + global drop sfx.
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "KEEPAWAY_DROPPED", carrier.NetName);
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "KEEPAWAY_DROPPED", carrier.NetName);
        SoundSystem.PlayGlobal(Sounds.ByName("KA_DROPPED")); // QC SND_KA_DROPPED, NULL emitter, ATTEN_NONE
    }

    // ============================================================================================
    //  Ball ENTITY layer (QC ka_SpawnBalls / ka_RespawnBall / ka_TouchEvent)
    // ============================================================================================

    /// <summary>
    /// QC ka_SpawnBalls: create the single world ball entity at <paramref name="origin"/> via the shared
    /// <see cref="BallEntity"/> framework (touch = pickup, built-in relocate think + EF_DIMLIGHT / glow_trail /
    /// damageforcescale defaults). Returns the entity (or null when no facade is wired). The Keepaway ball
    /// immediately relocates to a random map location and arms the respawn timer (QC ka_RespawnBall tail of
    /// ka_SpawnBalls). The host's round-drive seam (GameWorld) calls this once the match is live, since the
    /// Keepaway ball is procedural (no map entity) — see <see cref="AdoptBall"/>.
    /// </summary>
    public Entity? SpawnBall(Vector3 origin)
    {
        var cfg = new BallConfig
        {
            // QC ka_TouchEvent / ka_RespawnBall: our touch picks up / handles world-touch; RespawnTime drives the
            // built-in relocate think (RelocateOnRespawn defaulted true by BallKind.KeepawayBall).
            Touch = BallTouchEntity,
            RespawnTime = RespawnTime,
        };
        Entity? e = global::XonoticGodot.Common.Gameplay.BallEntity.SpawnForGametype(BallKind.KeepawayBall, origin, cfg);
        return AdoptBall(e);
    }

    /// <summary>
    /// Adopt a ball created by the host's round-drive seam (QC ka_Handler_CheckBall → ka_SpawnBalls) as THIS
    /// gametype's single ball. The shared <see cref="global::XonoticGodot.Common.Gameplay.BallEntity"/> spawner
    /// builds + relocates the edict; the gametype records it and clears any carrier. Returns the same entity.
    /// </summary>
    public Entity? AdoptBall(Entity? e)
    {
        BallEntity = e;
        Ball.Carrier = null;
        // QC ka_SpawnBalls: e.event_damage = ka_DamageEvent. Route the ball's damage through the live
        // DamageSystem (GtEventDamage) so a NEEDKILL hit (fell into a hurt/lava/slime/swamp volume) force-
        // respawns it (registry keepaway.ball.respawn — the NEEDKILL respawn branch was previously missing).
        if (e is not null)
            e.GtEventDamage = OnBallDamage;
        return e;
    }

    /// <summary>QC ka_DamageEvent: when the loose ball takes a NEEDKILL hit (it fell into a hurt-trigger / lava /
    /// slime / swamp volume) force it to relocate so players can reach it again.</summary>
    private void OnBallDamage(Entity self, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        if (Ball.Carrier is not null)
            return; // a carried ball doesn't take damage (QC takedamage = DAMAGE_NO while carried)
        if (DeathTypes.ItemDamageNeedKill(deathType))
            RespawnBall(self); // QC ka_DamageEvent → ka_RespawnBall(this)
    }

    /// <summary>QC ka_RespawnBall (think): relocate a loose ball to a random map location, re-arm the timer, and
    /// play the respawn sound (ATTEN_NONE).</summary>
    public void RespawnBall(Entity e)
    {
        if (!ReferenceEquals(e, BallEntity) || Ball.Carrier is not null)
            return;
        // QC ka_RespawnBall: capture the OLD origin before relocating — the respawn effect plays at BOTH the old
        // and the new spot (Send_Effect(EFFECT_KA_BALL_RESPAWN, oldballorigin, ...) + at this.origin).
        Vector3 oldBallOrigin = e.Origin;
        // QC ka_RespawnBall: MoveToRandomMapLocation (with the SelectSpawnPoint fallback) + '0 0 200' kick + arm.
        global::XonoticGodot.Common.Gameplay.BallEntity.Relocate(e, RespawnTime);
        e.Think = RespawnBallThink;
        // QC ka_RespawnBall: Send_Effect(EFFECT_KA_BALL_RESPAWN, oldballorigin, '0 0 0', 1);
        //                    Send_Effect(EFFECT_KA_BALL_RESPAWN, this.origin, '0 0 0', 1);
        EffectEmitter.Emit("KA_BALL_RESPAWN", oldBallOrigin, Vector3.Zero, 1);
        EffectEmitter.Emit("KA_BALL_RESPAWN", e.Origin, Vector3.Zero, 1);
        SoundSystem.PlayOn(e, Sounds.ByName("KA_RESPAWN")); // QC SND_KA_RESPAWN, ATTEN_NONE
    }

    /// <summary>Entity touch trampoline (QC ka_TouchEvent): a live player picks the loose ball up; a non-player
    /// world touch plays the touch sfx; the 0.5 s self-recapture lockout is honored.</summary>
    private void BallTouchEntity(Entity self, Entity other)
    {
        if (Ball.Carrier is not null)
            return; // already carried

        if (other is Player p)
        {
            if (p.IsDead)
                return;
            // QC ka_DropEvent self-recapture lockout: a carrier who just dropped can't instantly re-grab it.
            if (global::XonoticGodot.Common.Gameplay.BallEntity.IsRecaptureLocked(self, p))
                return;
            PickUp(p);
            return;
        }

        // QC ka_TouchEvent: the ball just touched a non-player (most likely the world) — spark + touch sfx.
        EffectEmitter.Emit("BALL_SPARKS", self.Origin, Vector3.Zero, 1); // QC Send_Effect(EFFECT_BALL_SPARKS, this.origin, '0 0 0', 1)
        SoundSystem.PlayOn(self, Sounds.ByName("KA_TOUCH")); // QC SND_KA_TOUCH, ATTEN_NORM
    }

    private void RespawnBallThink(Entity self) => RespawnBall(self);

    // ============================================================================================
    //  Possession damage/force scaling (QC ka_Damage_Calculate)
    // ============================================================================================

    /// <summary>
    /// QC Damage_Calculate (ka): scale player-vs-player damage by whether the attacker and/or target carries
    /// the ball. The x/y/z components of g_keepaway_ballcarrier_damage and g_keepaway_noncarrier_damage select
    /// the self/other-carrier/noncarrier case. Returns the scaled damage. (Force scaling is the same matrix on
    /// the force cvars; <see cref="DamageForceScale"/> exposes that factor.)
    /// </summary>
    public float DamageScale(Player attacker, Player target, float damage)
        => damage * ScaleFactor(attacker, target, "g_keepaway_ballcarrier_damage", "g_keepaway_noncarrier_damage");

    /// <summary>The QC force multiplier for a hit (same matrix as <see cref="DamageScale"/> on the force cvars).</summary>
    public float DamageForceScale(Player attacker, Player target)
        => ScaleFactor(attacker, target, "g_keepaway_ballcarrier_force", "g_keepaway_noncarrier_force");

    private float ScaleFactor(Player attacker, Player target, string carrierCvar, string nonCarrierCvar)
    {
        // QC Damage_Calculate reads a single VECTOR cvar and indexes its component: .x = self-damage,
        // .y = damage to (other) ballcarriers, .z = damage to noncarriers (the carrier vs. noncarrier branch
        // is selected by whether the ATTACKER carries). Base ships only the vector form
        // (g_keepaway_ballcarrier_damage "1 1 1"), so we must parse the vector string — not absent _x/_y/_z
        // suffix cvars — to honor a server-set non-default value. Default 1 (no scaling) when unset.
        bool attackerCarries = ReferenceEquals(Ball.Carrier, attacker);
        bool targetCarries = ReferenceEquals(Ball.Carrier, target);
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

    /// <summary>
    /// Advance Keepaway one step (QC ka_BallThink_Carried): while the ball is carried, accrue
    /// <see cref="ScoreTimePoints"/> per second to the carrier's SCORE, carrying the fractional remainder
    /// like QC's float2int accumulator. <paramref name="dt"/> is the frame delta (QC frametime). Call each
    /// tick; safe after <see cref="MatchEnded"/>.
    /// </summary>
    public void Tick(float dt)
    {
        if (MatchEnded)
            return;
        Player? carrier = Ball.Carrier;
        if (carrier is null || carrier.IsDead)
            return;

        float pps = ScoreTimePoints;
        if (pps > 0f)
        {
            _timePointsRemainder += pps * dt;
            int whole = (int)_timePointsRemainder; // floor toward zero (pps, dt >= 0)
            if (whole != 0)
            {
                _timePointsRemainder -= whole;
                carrier.ScoreFrags += whole;
                UpdateLeaderAndCheckLimit(carrier);
            }
        }

        // QC ka_BallThink_Carried: GameRules_scoring_add(owner, KEEPAWAY_BCTIME, frametime) — accrue ball-carry
        // seconds. The column is an integer, so bank whole seconds and carry the fractional remainder.
        _bcTimeRemainder += dt;
        int wholeSecs = (int)_bcTimeRemainder;
        if (wholeSecs != 0)
        {
            _bcTimeRemainder -= wholeSecs;
            AddCol(carrier, "KEEPAWAY_BCTIME", wholeSecs);
        }

        // QC ka_BallThink_Carried orbit anim: the carried ball orbits the carrier in the xy plane (single ball,
        // so cnt=chainCount=1). Keeps the ball entity riding the carrier so the model/glow tracks them.
        if (BallEntity is Entity e)
        {
            Vector3 pos = global::XonoticGodot.Common.Gameplay.BallEntity.CarryOrbit(
                carrier.Origin, GametypeEntities.Now, cnt: 1, chainCount: 1);
            GametypeEntities.SetOrigin(e, pos);
            // QC also syncs the ball alpha to the carrier's invisibility (this.alpha = this.owner.alpha); the
            // entity layer has no alpha field yet, so that visual sync is deferred to presentation (see todos).
        }
    }

    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;
        bool victimCarried = ReferenceEquals(Ball.Carrier, victim);

        if (attacker is not null && !ReferenceEquals(attacker, victim))
        {
            // QC ka PlayerDies: killing the ballcarrier earns a bonus; a kill made WHILE carrying earns one.
            if (victimCarried)
            {
                attacker.ScoreFrags += (int)ScoreBcKill;
                AddCol(attacker, "KEEPAWAY_CARRIERKILLS", 1); // QC GameRules_scoring_add(attacker, KEEPAWAY_CARRIERKILLS, 1)
            }
            else if (!ReferenceEquals(Ball.Carrier, attacker))
            {
                // QC ka PlayerDies: a non-carrier who frags a non-carrier is nudged toward the ball
                // (Send_Notification(NOTIF_ONE_ONLY, attacker, MSG_CENTER, CENTER_KEEPAWAY_WARN)), gated by
                // g_keepaway_noncarrier_warn (registry keepaway.warn.noncarrier).
                if (TryCvar(CvarNonCarrierWarn, out float warn) && warn != 0f)
                    NotificationSystem.Send(NotifBroadcast.OneOnly, attacker, MsgType.Center, "KEEPAWAY_WARN");
            }
            if (ReferenceEquals(Ball.Carrier, attacker))
                attacker.ScoreFrags += (int)ScoreKillAc;

            UpdateLeaderAndCheckLimit(attacker);
        }

        // The victim drops the ball if they had it.
        if (victimCarried)
            Drop();

        // FFA respawn; arm the timer.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        victim.RespawnTime = now + 2f;
        return false;
    }

    private void UpdateLeaderAndCheckLimit(Player candidate)
    {
        if (Leader is null || candidate.ScoreFrags > Leader.ScoreFrags)
            Leader = candidate;

        float limit = PointLimit;
        if (limit > 0f && Leader is not null && Leader.ScoreFrags >= limit)
            MatchEnded = true;
    }

    /// <summary>Authoritative leader + limit pass over the roster (QC checkrules). The host may call each tick.</summary>
    public void RecomputeLeader(IReadOnlyList<Player> players)
    {
        Player? best = null;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            if (best is null || p.ScoreFrags > best.ScoreFrags)
                best = p;
        }
        Leader = best;

        float limit = PointLimit;
        if (limit > 0f && best is not null && best.ScoreFrags >= limit)
            MatchEnded = true;

        AnnounceFragsRemaining(players, limit);
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ka, Scores_CountFragsRemaining): announce the "N frags left" cue only when timed
    /// scoring is OFF (the hook returns <c>!autocvar_g_keepaway_score_timepoints</c>) — when scoring is by
    /// carry-time the frag countdown is meaningless. When the hook would return true (timepoints==0, the default)
    /// fire the WinningCondition_Scores remaining-frags announcer (REMAINING_FRAG_{1,2,3}) over the FFA roster.
    /// (registry keepaway.score.countfragsremaining).
    /// </summary>
    private void AnnounceFragsRemaining(IReadOnlyList<Player> players, float limit)
    {
        if (Api.Services is null)
            return;
        // QC Scores_CountFragsRemaining: suppress the announce while timed scoring is enabled.
        if (ScoreTimePoints != 0f)
            return;

        // Top/second primary scores over non-spectators (QC WinningConditionHelper_topscore/_secondscore).
        int topScore = 0, secondScore = 0;
        bool haveTop = false, haveSecond = false;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            if (Scoring.GameScores.IsSpectator(p))
                continue;
            int s = Scoring.GameScores.PrimaryScore(p);
            if (!haveTop || s > topScore)
            {
                secondScore = topScore; haveSecond = haveTop;
                topScore = s; haveTop = true;
            }
            else if (!haveSecond || s > secondScore)
            {
                secondScore = s; haveSecond = true;
            }
        }
        // ka has no lead limit; suddenDeathEnding is framework-owned and surfaced elsewhere, so pass false.
        Scoring.GameScores.CountFragsRemaining(limit, 0f, topScore, secondScore, suddenDeathEnding: false);
    }

    // ----- waypoint tracking cvar (g_keepawayball_tracking: 0=none/1=always/2=dropped-only) -----
    private const string CvarBallTracking = "g_keepawayball_tracking";
    /// <summary>QC autocvar_g_keepawayball_tracking (default 1): 0 = no waypoint, 1 = always, 2 = only when dropped.</summary>
    private int BallTracking => TryCvar(CvarBallTracking, out float v) ? (int)v : 1;

    /// <summary>
    /// QC sv_keepaway.qc ka_ballcarrier_waypointsprite_visible_for_player: the per-viewer visibility predicate on
    /// the carrier waypoint. Spectators and during warmup always see it; the sprite is hidden when the carrier has
    /// the invisibility powerup; otherwise it is shown only when g_keepawayball_tracking == 1 (so tracking == 2 —
    /// dropped-only — hides the carrier sprite). (Keepaway is FFA, so the QC SAME_TEAM branch is moot; the
    /// spectating-OF-the-carrier nuance is approximated by the IS_SPEC always-visible rule, matching KeyHunt/TKA.)
    /// </summary>
    private bool CarrierWaypointVisibleFor(Player carrier, Player? viewer)
    {
        // QC: if(IS_SPEC(player) || warmup_stage) return true;
        if (viewer is null || viewer.IsObserver || NotificationSystem.WarmupStage)
            return true;
        // QC: if(IS_INVISIBLE(this.owner)) return false;
        var invisEffect = StatusEffectsCatalog.ByName("invisibility");
        if (invisEffect is not null && StatusEffectsCatalog.Has(carrier, invisEffect))
            return false;
        // QC: return autocvar_g_keepawayball_tracking == 1; (enemies see the carrier only in always-track mode).
        return BallTracking == 1;
    }

    /// <summary>
    /// QC ka_RespawnBall (WaypointSprite_Spawn WP_KaBall on the loose ball, '0 0 64') + ka_TouchEvent
    /// (WaypointSprite_AttachCarrier WP_KaBallCarrier on the carrier). One sprite for the (single) ball,
    /// rebuilt each tick from its live state — KaBall when loose, KaBallCarrier riding the carrier when held.
    /// Honors g_keepawayball_tracking (0 = none, 1 = always, 2 = only when the ball is dropped/loose); QC also
    /// always shows it during warmup, but the host pull path has no warmup flag here, so the cvar governs.
    /// </summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        if (BallEntity is null)
            return;
        int tracking = BallTracking;
        if (tracking == 0)
            return; // QC: g_keepawayball_tracking 0 → no waypoint at all

        Player? carrier = Ball.Carrier;
        if (carrier is not null)
        {
            if (tracking != 1)
                return; // QC g_keepawayball_tracking 2 = only show a waypoint when the ball is DROPPED (loose)
            // QC ka_TouchEvent: WaypointSprite_AttachCarrier(WP_KaBallCarrier, toucher, RADARICON_FLAGCARRIER),
            // plus the per-frame ka_ballcarrier_waypointsprite_visible_for_player predicate that hides the carrier
            // sprite from spectators-OF-the-carrier and on the carrier's invisibility powerup, and otherwise gates
            // it on g_keepawayball_tracking == 1 (registry keepaway.waypoints.tracking — the visibility predicate).
            Player held = carrier;
            into.Add(new Waypoints.WaypointSprite
            {
                SpriteName = "KaBallCarrier",
                Owner = carrier,
                Offset = new System.Numerics.Vector3(0f, 0f, 64f),
                Team = Teams.None,
                RadarIcon = 1,
                VisibleForPlayer = viewer => CarrierWaypointVisibleFor(held, viewer),
                Health = -1f,
            });
            return;
        }

        // QC ka_RespawnBall / ka_DropEvent: WaypointSprite_Spawn(WP_KaBall, ..., this/ball, '0 0 64', ...).
        into.Add(new Waypoints.WaypointSprite
        {
            SpriteName = "KaBall",
            FixedOrigin = BallEntity.Origin + new System.Numerics.Vector3(0f, 0f, 64f),
            Team = Teams.None,
            RadarIcon = 1,
            Health = -1f,
        });
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
/// The Keepaway ball state — the Godot-free essence of the QC keepawayball edict (.owner carrier, pickup
/// time). Tracks who carries it; the world ball <see cref="Entity"/> lives on <see cref="Keepaway.BallEntity"/>.
/// The ball model/effects, multi-ball chaining, and waypoints remain client concerns.
/// </summary>
public sealed class BallState
{
    /// <summary>The player carrying the ball (QC ball.owner / player.ballcarried), or null when loose.</summary>
    public Player? Carrier;

    /// <summary>Sim time the ball was last picked up (QC pickup time).</summary>
    public float PickupTime;
}
