using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Nexball gametype — port of <c>CLASS(NexBall, Gametype)</c>
/// (common/gametypes/gametype/nexball/nexball.qh + sv_nexball.qc).
///
/// A team sport: players carry/shoot a ball and try to put it through the enemy team's goal. Putting the ball
/// in the <em>enemy</em> goal scores a point for the scorer's team (QC <c>GoalTouch</c> → pscore=+1,
/// TeamScore_AddToTeam(ST_NEXBALL_GOALS)); an own-goal or a "fault" goal costs a point (pscore=−1, credited
/// to the other team in a two-team game). The first team to the goal limit (cvar <c>g_nexball_goallimit</c>,
/// default 5) wins (QC GameRules_limit_score). It is a weapon-arena gametype (only the ball-launcher weapon).
///
/// Faithfully ported (the goal-scoring rule):
///  - per-team goal tally (<see cref="GoalsFor"/>, routed through the unified GameScores team store);
///  - goal resolution via <see cref="ScoreGoal"/> (own-goal/fault → −1 to other team; enemy goal → +1);
///  - goal-limit win check (<see cref="GoalLimit"/>, QC g_nexball_goallimit) and winning team.
///
/// Faithfully ported (objective layer):
///  - the ball entity (basketball/football) with carry/drop/reset (<see cref="SpawnBall"/>/
///    <see cref="GiveBall"/>/<see cref="DropBall"/>, QC GiveBall / DropBall / ResetBall);
///  - the goal trigger entities (team goals + GOAL_FAULT + GOAL_OUT) and the GoalTouch dispatch that derives
///    the goal kind and scores it (<see cref="SpawnGoal"/>/<see cref="GoalTouch"/>, QC GoalTouch pscore).
///
/// Deferred (NOTE — cross-boundary): the ball physics tuning (bounce factors, basketball meter), the ball-launcher arena
/// weapon, the ball/goal models + waypoint sprites (CSQC), and score networking — client or weapon concerns.
/// </summary>
[GameType]
public sealed class Nexball : GameType
{
    // ----- goal limit cvars + default (gametype default pointlimit=5) -----
    private const string CvarGoalLimit    = "g_nexball_goallimit";
    private const string CvarFragLimit    = "fraglimit"; // GameRules_limit_score writes the goal limit here
    private const int    DefaultGoalLimit = 5;           // gametype_init "pointlimit=5"

    // ----- ball-lifecycle / physics cvars + their gametypes-server.cfg defaults (QC autocvar_g_nexball_*) -----
    private const float DelayStart   = 3f;    // g_nexball_delay_start    — ball stands on spawn before release
    private const float DelayIdle    = 10f;   // g_nexball_delay_idle     — max idle time before a reset
    private const float DelayGoal    = 3f;    // g_nexball_delay_goal     — delay between a goal and the ball reset
    private const float DelayCollect = 0.5f;  // g_nexball_delay_collect  — relaunch self-recapture cooldown
    private const float DelayHold        = 20f; // g_nexball_basketball_delay_hold         — anti-ballcamp DropOwner
    private const float DelayHoldForTeam = 60f; // g_nexball_basketball_delay_hold_forteam — team-hold auto-return
    private const float FootballBoostForward = 100f; // g_nexball_football_boost_forward
    private const float FootballBoostUp      = 200f; // g_nexball_football_boost_up
    private const float FootballPhysics      = 2f;   // g_nexball_football_physics (2 = view-independent, the default)
    private const int   EffectsDefault       = 8;    // g_nexball_basketball_effects_default (EF_DIMLIGHT)

    // ----- ball-spawn presentation / collision constants (QC SpawnBall) -----
    private const int   EfLowPrecision       = 4194304; // QC EF_LOWPRECISION (dpextensions.qc:274) bandwidth hint
    private const int   DpContentsSolid      = 0x00000001; // QC DPCONTENTS_SOLID
    private const int   DpContentsBody       = 0x02000000; // QC DPCONTENTS_BODY
    private const int   DpContentsPlayerClip = 256;        // QC DPCONTENTS_PLAYERCLIP (ball bounces off clips)

    // ----- per-frame carry constants (QC PlayerPreThink / PlayerPhysics_UpdateStats) -----
    private const string CvarCarrierHighspeed = "g_nexball_basketball_carrier_highspeed"; // default 0.8 (slows carrier)
    private const float  DefaultCarrierHighspeed = 0.8f;
    /// <summary>QC g_nexball_viewmodel_offset "8 8 0" — where the view-ball sits on the carrier (forward right up).</summary>
    private static readonly Vector3 ViewModelOffset = new(8f, 8f, 0f);

    /// <summary>QC PlayerPhysics_UpdateStats hook handler (carrier highspeed multiplier).</summary>
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _physicsHandler;

    /// <summary>How a goal was scored, mirroring QC's GoalTouch pscore branches.</summary>
    public enum GoalKind
    {
        /// <summary>Ball entered the enemy goal: +1 to the scorer's team (QC pscore=+1).</summary>
        Score,
        /// <summary>Ball entered the scorer's own goal: −1 (QC own-goal / GOAL_FAULT, pscore=−1).</summary>
        OwnGoal,
    }

    // Per-team goal counts (QC ST_NEXBALL_GOALS, team slot 1 — the TEAM primary) now live in the unified
    // GameScores two-slot team store — the source of truth (common/scores.qh). Read/written via GoalsFor /
    // GameScores.AddToTeam (slot 1).

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill (no nexball-specific scoring on kills).</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a team reaches the goal limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The team color code (see <see cref="Teams"/>) that reached the goal limit, or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    public Nexball()
    {
        NetName = "nb";
        DisplayName = "Nexball";
        TeamGame = true;
    }

    // ----- goal-kind sentinels stored in a goal entity's GtHomeTeam (QC GOAL_FAULT / GOAL_OUT) -----
    /// <summary>QC GOAL_FAULT: a goal volume that docks the ball team a point (or credits the other team).</summary>
    public const int GoalFault = -2;
    /// <summary>QC GOAL_OUT: an out-of-bounds volume that just returns the ball (no scoring).</summary>
    public const int GoalOut = -3;

    /// <summary>The ball entity (QC nexball_basketball / nexball_football), or null (headless).</summary>
    public Entity? BallEntity { get; private set; }

    /// <summary>The team that currently "owns" the ball (QC ball.team) — set on pickup, used by GoalTouch.</summary>
    public int BallTeam { get; private set; } = Teams.None;

    /// <summary>The player carrying / who last touched the ball (QC ball.pusher), credited on goals.</summary>
    public Player? BallPusher { get; private set; }

    /// <summary>The goal trigger entities on the map (QC nexball_*goal / fault / out).</summary>
    public readonly List<Entity> Goals = new();

    // ----- per-ball QC state (kept on the gametype since there is one ball; QC stored these on the ball edict) -----
    /// <summary>QC ball.cnt: the ResetBall glide step (0/1 = active, 1 = goal-reset pending, 2..4 = glide-home steps).</summary>
    private int _ballCnt;
    /// <summary>QC ball.lifetime: absolute time a team-held ball auto-returns (delay_hold_forteam); 0 = none.</summary>
    private float _ballLifetime;
    /// <summary>QC ball.lastground: last world-bounce time, throttling the bounce sound (football_touch 0.1 s).</summary>
    private float _ballLastGround;
    /// <summary>QC ball.nb_dropper / ball.nb_droptime: the player who last launched the ball + when (delay_collect).</summary>
    private Player? _ballDropper;
    private float _ballDropTime;
    /// <summary>True iff the live ball is a basketball (NBM_BASKETBALL — carryable). False = football (kickable only).</summary>
    private bool _isBasketball = true;
    /// <summary>QC SpawnBall: g_nexball_sound_bounce (default 1) — when 0, ball.noise is cleared so NO bounce sound plays.
    /// The touch handlers play this.noise, so an empty noise means a silent world bounce.</summary>
    private bool _bounceSoundEnabled = true;
    /// <summary>QC: the ball's think is InitBall (release) rather than ResetBall (glide) — set by SpawnBall and by
    /// ResetBall step 4, cleared once InitBall runs. Keeps ball.cnt faithful to QC (0 during the pre-release hold).</summary>
    private bool _releasePending;

    public override void OnInit()
    {
        // QC: the ball + goals are spawned from the map's nexball_* entities (see SpawnBall / SpawnGoal).
        // gametype_init flags (TEAMPLAY|USEPOINTS|WEAPONARENA) are the engine's job; OnInit clears state.
        BallEntity = null;
        BallTeam = Teams.None;
        BallPusher = null;
        Goals.Clear();
        _ballCnt = 0;
        _ballLifetime = 0f;
        _ballLastGround = 0f;
        _ballDropper = null;
        _ballDropTime = 0f;
        _isBasketball = true;
        _releasePending = false;
        _teamsSeededFromGoals = false;
    }

    // ============================================================================================
    //  Ball ENTITY layer (QC SpawnBall / GiveBall / DropBall / ResetBall)
    // ============================================================================================

    /// <summary>
    /// QC SpawnBall (+ spawnfunc nexball_basketball/football): create the world ball entity at
    /// <paramref name="origin"/> through the shared <see cref="global::XonoticGodot.Common.Gameplay.BallEntity"/>
    /// framework so it gets the bbox / movetype / glow defaults, then install Nexball's own touch + the
    /// <see cref="BallThink"/> state machine (delay_start release → idle reset → 4-step glide-home). The ball
    /// stands on its spawn origin for <see cref="DelayStart"/> before InitBall releases it (QC nextthink =
    /// game_starttime + delay_start). <paramref name="basketball"/> selects the carryable basketball vs the
    /// kick-only football. Returns the entity (null when no facade is wired).
    /// </summary>
    public Entity? SpawnBall(Vector3 origin, bool basketball = true)
    {
        _isBasketball = basketball;
        // QC SpawnBall: if(!g_nexball_sound_bounce) this.noise = ""; — the bounce sound is gated by the cvar (default 1).
        _bounceSoundEnabled = GametypeEntities.Cvar("g_nexball_sound_bounce", 1f) != 0f;
        var cfg = new BallConfig
        {
            Touch = basketball ? BasketballTouch : FootballTouch, // QC settouch(basketball_touch/football_touch)
            Think = BallThink,                                    // QC InitBall/ResetBall state machine (not the loose default)
            RespawnTime = DelayIdle,                              // QC g_nexball_delay_idle
            Effects = basketball ? EffectsDefault : 0,            // QC basketball_effects_default (EF_DIMLIGHT); football none
        };
        Entity? e = global::XonoticGodot.Common.Gameplay.BallEntity.SpawnForGametype(
            basketball ? BallKind.NexballBasketball : BallKind.NexballFootball, origin, cfg);

        BallEntity = e;
        BallTeam = Teams.None;
        BallPusher = null;
        _ballCnt = 0;            // QC ball.cnt default 0 (a ball can be caught during the pre-release hold)
        _releasePending = true;  // the FIRST BallThink runs InitBall (QC setthink(this, InitBall) in SpawnBall)
        _ballLifetime = 0f;
        if (e is not null)
        {
            // QC SpawnBall: relocate_nexball(this) — tracebox the ball's bbox at its origin and, if it starts in
            // solid, nudge it out so the ball doesn't spawn embedded in a brush. spawnorigin is recorded AFTER the
            // relocate so ResetBall returns the ball to its corrected (in-bounds) home, not the raw map origin.
            origin = RelocateNexball(e, origin);
            e.GtSpawnOrigin = origin; // keep the entity's QC spawnorigin in sync with the corrected (relocated) home

            e.Effects |= EfLowPrecision; // QC SpawnBall: this.effects |= EF_LOWPRECISION (bandwidth hint)

            // QC SpawnBall: if(cvar("g_" + classname + "_trail")) { glow_color = trail_color; glow_trail = true; } —
            // the glow_trail/glow_color rendering is a CLIENT concern (deferred; BallConfig.TrailColor already carries
            // the faithful 254 default for the renderer). No server-side state to set here.

            // QC SpawnBall: if(g_nexball_playerclip_collisions) dphitcontentsmask = BODY|SOLID|PLAYERCLIP — the ball
            // bounces off func_clip/PLAYERCLIP brushes (default 1).
            if (GametypeEntities.Cvar("g_nexball_playerclip_collisions", 1f) != 0f)
                e.DpHitContentsMask = DpContentsBody | DpContentsSolid | DpContentsPlayerClip;

            e.MoveType = MoveType.Fly; // QC SpawnBall: set_movetype(this, MOVETYPE_FLY) until release
            // QC SpawnBall: setthink(this, InitBall); nextthink = game_starttime + delay_start.
            e.Think = BallThink;
            e.NextThink = global::System.Math.Max(GametypeEntities.Now, GameStartTime) + DelayStart;
        }
        BallHome = origin; // QC SpawnBall: this.spawnorigin = this.origin (after relocate) — ResetBall returns here
        return e;
    }

    /// <summary>
    /// QC relocate_nexball: tracebox the ball's ±16 bbox at <paramref name="origin"/>; if it starts in solid, walk
    /// it out by sampling small offsets (the headless analogue of QC's engine move_out_of_solid) so the ball isn't
    /// spawned embedded in a brush. Returns the corrected origin (and moves the entity there); the entity stays put
    /// when it's already clear or no trace facade is wired.
    /// </summary>
    private static Vector3 RelocateNexball(Entity e, Vector3 origin)
    {
        if (Api.Services is null)
            return origin;
        Vector3 mins = e.Mins == default ? new Vector3(-16f, -16f, -16f) : e.Mins;
        Vector3 maxs = e.Maxs == default ? new Vector3(16f, 16f, 16f) : e.Maxs;
        var tr = Api.Trace.Trace(origin, mins, maxs, origin, MoveFilter.Normal, e);
        if (!tr.StartSolid)
            return origin; // already clear — QC leaves this.origin untouched

        // QC move_out_of_solid: nudge the ball up (and out) until the bbox no longer starts in solid. Sample a few
        // increasing vertical offsets (the common "embedded in the floor" case) then give up and keep the origin.
        for (float dz = 8f; dz <= 128f; dz += 8f)
        {
            Vector3 candidate = origin + new Vector3(0f, 0f, dz);
            if (!Api.Trace.Trace(candidate, mins, maxs, candidate, MoveFilter.Normal, e).StartSolid)
            {
                GametypeEntities.SetOrigin(e, candidate);
                return candidate;
            }
        }
        return origin; // QC objerror fallback: couldn't get out — leave it (the map needs fixing)
    }

    /// <summary>The ball's home/reset position (QC spawnorigin), defaults to the spawn origin.</summary>
    public Vector3 BallHome { get; set; }

    /// <summary>QC game_starttime: the absolute time the match goes live (the ball releases delay_start after it).
    /// The host sets this before SpawnBall; 0 = release immediately (the headless/test default).</summary>
    public float GameStartTime { get; set; }

    /// <summary>
    /// QC GiveBall: <paramref name="player"/> picks up the ball, becoming its carrier + team owner. Sets the
    /// team-hold auto-return lifetime when the catcher is on a DIFFERENT team than the last owner
    /// (delay_hold_forteam), arms the anti-ballcamp DropOwner timer (delay_hold), and stops/attaches the ball.
    /// The weapon-arena swap to WEP_NEXBALL + carrier light/waypoint are deferred (separate weapon/CSQC seams).
    /// </summary>
    public void GiveBall(Player player)
    {
        if (MatchEnded || player.IsDead)
            return;

        // QC GiveBall: if(ownr) { ownr.effects &= ~effects_default; ownr.ballcarried = NULL;
        //   GameRules_scoring_vip(ownr, false); if(NB_METERSTART(ownr)) { NB_METERSTART=0; wepent.state=WS_READY; } }
        // A steal (W_Nexball_Touch) calls GiveBall while the ball is still attached to the VICTIM — clear the old
        // carrier's back-link/VIP/meter (and restore their weapons) before re-attaching, or the victim is left
        // dangling as a phantom carrier. In the normal loose-ball catch this is a no-op (no prior carrier).
        if (BallEntity?.GtCarrier is Player prevOwner && !ReferenceEquals(prevOwner, player))
        {
            GametypeEntities.ScoringVip(prevOwner, false);
            prevOwner.GtCarried = null;
            prevOwner.ForEachWeaponSlot(s => { s.NbMeterStart = 0f; });
            if (_isBasketball)
                RestoreWeapons(prevOwner);
        }

        // QC GiveBall: if(ball.team != plyr.team) ball.lifetime = time + delay_hold_forteam.
        if (BallTeam != (int)player.Team)
            _ballLifetime = GametypeEntities.Now + DelayHoldForTeam;

        BallTeam = (int)player.Team; // QC ball.team = plyr.team
        BallPusher = player;          // QC ball.owner = ball.pusher = plyr (owner = carrier, pusher = last toucher)
        _ballCnt = 0;                 // QC ball gets caught — cnt stays clear (no reset pending)
        _ballDropper = player;        // QC ball.nb_dropper = plyr (delay_collect cooldown is keyed off the catcher)
        _ballDropTime = GametypeEntities.Now;

        if (BallEntity is Entity e)
        {
            // QC GiveBall: setorigin to plyr.origin + view_ofs, MOVETYPE_NONE, settouch func_null, scale down.
            global::XonoticGodot.Common.Gameplay.BallEntity.AttachToCarrier(e, player, player.ViewOfs);
            e.Team = BallTeam;
            e.Touch = null;          // QC settouch(ball, func_null) — held ball isn't picked up by touch
            e.Velocity = Vector3.Zero;
            e.Effects &= ~EffectsDefault; // QC ball.effects &= ~effects_default (the glow moves to the carrier, client)

            // QC GiveBall: if(delay_hold) setthink(ball, DropOwner); nextthink = time + delay_hold.
            if (DelayHold > 0f)
            {
                e.Think = DropOwnerThink;
                e.NextThink = GametypeEntities.Now + DelayHold;
            }
        }
        // QC GiveBall: STAT(WEAPONS, actor.(weaponentity)) = STAT(WEAPONS, actor); STAT(WEAPONS, actor) =
        // WEPSET(NEXBALL); W_SwitchWeapon(actor, WEP_NEXBALL) — save normal weapons, grant only BallStealer.
        if (_isBasketball)
            SaveAndApplyNexballWeapons(player);
    }

    /// <summary>
    /// QC DropBall: the carrier loses the ball; it drops where they stood inheriting <paramref name="vel"/>,
    /// keeping the last-toucher team. Restores bounce + the pickup touch, arms the idle-reset think
    /// (delay_idle, capped by the team-hold lifetime), and records the dropper for the delay_collect cooldown.
    /// </summary>
    public void DropBall(Vector3 org, Vector3 vel)
    {
        Player? carrier = BallEntity?.GtCarrier as Player ?? BallPusher;
        GametypeEntities.ScoringVip(carrier, false); // QC GameRules_scoring_vip(ownr, false) on drop (sv_nexball.qc:147)
        if (BallEntity is Entity e)
        {
            GametypeEntities.DetachFromCarrier(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.Bounce;     // QC set_movetype(ball, MOVETYPE_BOUNCE)
            e.Flags &= ~EntFlags.OnGround;     // QC UNSET_ONGROUND(ball)
            GametypeEntities.SetOrigin(e, org);
            e.Velocity = vel;                  // QC ball.velocity = vel (carrier velocity inherited)
            e.Effects |= EffectsDefault;       // QC ball.effects |= effects_default
            e.Touch = _isBasketball ? BasketballTouch : FootballTouch; // QC settouch(ball, basketball_touch)
            e.Think = BallThink;               // QC setthink(ball, ResetBall)
            // QC nextthink = min(time + delay_idle, ball.lifetime).
            e.NextThink = MinIdle(GametypeEntities.Now + DelayIdle);
        }
        _ballDropTime = GametypeEntities.Now;  // QC ball.nb_droptime = time
        if (carrier is not null)
            _ballDropper = carrier;            // QC ball.nb_dropper stays the launching player (delay_collect)
        // QC keeps ball.team + ball.pusher after a drop (the last toucher still "owns" it for goal scoring).
    }

    /// <summary>QC nb_DropBall(player) / the PlayerDies+ClientDisconnect+MakePlayerObserver hooks: the carrier's
    /// ball drops where they stand with their velocity. No-op if the player isn't carrying this ball.
    /// Also restores the carrier's saved weapon loadout and clears the power-meter state.</summary>
    public void DropBall(Player carrier)
    {
        if (BallEntity is null || !ReferenceEquals(carrier.GtCarried, BallEntity))
            return;
        // QC DropBall: NB_METERSTART is implicitly cleared because the ball leaves the carrier.
        carrier.ForEachWeaponSlot(s => { s.NbMeterStart = 0f; });
        // QC PlayerPreThink non-carrier: restore the saved weapon set on the next frame.
        // We do it eagerly on DropBall so the player can fire immediately after losing the ball.
        if (_isBasketball)
            RestoreWeapons(carrier);
        DropBall(carrier.Origin, carrier.Velocity);
    }

    /// <summary>QC DropOwner (the anti-ballcamp think): force the held ball loose and shove the camping carrier
    /// up-and-back so a basketball can't be hoarded past delay_hold.</summary>
    public void DropOwner()
    {
        Player? ownr = BallEntity?.GtCarrier as Player;
        if (ownr is null)
            return;
        DropBall(ownr.Origin, ownr.Velocity); // QC DropBall(this, ownr.origin, ownr.velocity)
        // QC: makevectors(ownr.v_angle.y * '0 1 0'); ownr.velocity += ('0 0 0.75' - v_forward) * 1000.
        Vector3 fwd = QMath.Forward(new Vector3(0f, ownr.VAngle.Y, 0f));
        ownr.Velocity += (new Vector3(0f, 0f, 0.75f) - fwd) * 1000f;
        ownr.Flags &= ~EntFlags.OnGround;     // QC UNSET_ONGROUND(ownr)
    }

    private void DropOwnerThink(Entity self) => DropOwner();

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(nb, PlayerPreThink) + nexball_setstatus, driven once per server frame for the live
    /// roster. For the basketball carrier: networks the ball velocity (so the held ball interpolates with the
    /// carrier) and positions the "view ball" in front of the carrier at view_ofs + g_nexball_viewmodel_offset
    /// (forward/right/up). It also enforces the forteam auto-return — a team-held ball whose lifetime has elapsed
    /// is dropped and reset (QC nexball_setstatus RETURN_HELD). The safe-pass crosshair lock + the NB_CARRYING
    /// objective-status bit + the per-frame viewmodel scale/flame are weapon/HUD concerns (deferred).
    /// </summary>
    public void CarryFrame()
    {
        if (BallEntity is not Entity ball || !_isBasketball)
            return;

        // QC nexball_setstatus: a team-held ball that has outlived its forteam lifetime is force-returned.
        if (_ballLifetime > 0f && _ballLifetime < GametypeEntities.Now && ball.GtCarrier is Player held)
        {
            // QC nexball_setstatus: Send_Notification(NOTIF_ALL, MSG_INFO, APP_TEAM_NUM(this.team,
            // INFO_NEXBALL_RETURN_HELD)) — announce the team that held the ball too long, keyed by the holder's team.
            NotificationSystem.Info($"NEXBALL_RETURN_HELD_{TeamSuffix((int)held.Team)}");
            DropBall(held.Origin, Vector3.Zero); // QC DropBall(ball, owner.origin, '0 0 0')
            _ballCnt = 0;
            ResetBall();                          // QC ResetBall(ball) — start the glide-home state machine
            return;
        }

        if (ball.GtCarrier is not Player carrier)
            return;

        // QC PlayerPreThink: ball.velocity = player.velocity (so the held ball networks smoothly).
        ball.Velocity = carrier.Velocity;

        // QC: makevectors(player.v_angle); org = player.origin + view_ofs + v_forward*off.x + v_right*off.y +
        // v_up*off.z; setorigin(ball, org). The ball rides in front of the carrier at the viewmodel offset.
        QMath.AngleVectors(carrier.VAngle, out Vector3 fwd, out Vector3 right, out Vector3 up);
        Vector3 org = carrier.Origin + carrier.ViewOfs
                      + fwd * ViewModelOffset.X
                      + right * ViewModelOffset.Y
                      + up * ViewModelOffset.Z;
        GametypeEntities.SetOrigin(ball, org);

        // QC PlayerPreThink 'safe passing': when g_nexball_safepass_maxdist != 0, crosshair_trace the carrier
        // and lock the ball.enemy onto a live teammate within range. The lock (ball.enemy + ball.wait) is what the
        // BallStealer secondary reads to fire the homing safe-pass arc instead of the tackle projectile.
        UpdateSafePassLock(ball, carrier, fwd);
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(nb, PlayerPreThink)</c> safe-pass lock: with <c>g_nexball_safepass_maxdist</c>
    /// nonzero, expire any stale lock (<c>ball.wait &lt; time</c>) then crosshair-trace the carrier; if the trace
    /// hits a live teammate within <c>safepass_maxdist</c> (5000), set <c>ball.enemy</c> = that teammate and
    /// refresh <c>ball.wait = time + g_nexball_safepass_holdtime</c> (0.75). No facade (headless tests) → skip the
    /// trace but still honour the expiry, so a directly-set <see cref="Entity.GtSafePassTarget"/> can be tested.
    /// </summary>
    private void UpdateSafePassLock(Entity ball, Player carrier, Vector3 forward)
    {
        float maxDist = GametypeEntities.Cvar("g_nexball_safepass_maxdist", 5000f);
        if (maxDist == 0f)
            return; // QC: if(autocvar_g_nexball_safepass_maxdist) — feature disabled when 0

        // QC: if(ball.wait < time && ball.enemy) ball.enemy = NULL; — the lock expires after safepass_holdtime.
        if (ball.GtSafePassExpire < GametypeEntities.Now && ball.GtSafePassTarget is not null)
            ball.GtSafePassTarget = null;

        if (Api.Services is null)
            return; // headless: no trace facade — keep the expiry-only semantics

        // QC: crosshair_trace(player) — trace from the carrier's eyes along their view angles.
        Vector3 eyes = carrier.Origin + carrier.ViewOfs;
        Vector3 end = eyes + forward * WeaponFiring.CurrentMaxShotDistance;
        TraceResult tr = Api.Trace.Trace(eyes, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, carrier);

        // QC: if(trace_ent && IS_CLIENT(trace_ent) && !IS_DEAD(trace_ent) && trace_ent.team == player.team &&
        //        vdist(trace_ent.origin - player.origin, <=, safepass_maxdist)) → lock.
        if (tr.Ent is Player mate && !ReferenceEquals(mate, carrier) && !mate.IsDead
            && mate.Team == carrier.Team
            && (mate.Origin - carrier.Origin).LengthSquared() <= maxDist * maxDist)
        {
            ball.GtSafePassTarget = mate; // QC ball.enemy = trace_ent
            ball.GtSafePassExpire = GametypeEntities.Now
                                    + GametypeEntities.Cvar("g_nexball_safepass_holdtime", 0.75f); // QC ball.wait = time + holdtime
        }
    }

    /// <summary>
    /// QC ResetBall (the 4-step think state machine): return the ball home and clear team ownership. cnt&lt;2 stops
    /// it and noclips (step 1); cnt 2/3 glides it toward the spawn origin (step 2/3, 0.5 s each); cnt 4 snaps it
    /// home, re-arms InitBall after delay_start, and clears ownership (step 4). The instantaneous form is the
    /// cnt&gt;=4 leg; the host's BallThink drives the intermediate steps each think tick.
    /// </summary>
    public void ResetBall()
    {
        if (BallEntity is not Entity e)
        {
            BallTeam = Teams.None;
            BallPusher = null;
            return;
        }

        if (_ballCnt < 2) // QC step 1: stop, go noclip, schedule the glide
        {
            e.Touch = null;                    // QC settouch(this, func_null)
            e.MoveType = MoveType.Noclip;      // QC set_movetype(this, MOVETYPE_NOCLIP)
            e.Velocity = Vector3.Zero;
            _ballCnt = 2;
            e.NextThink = GametypeEntities.Now; // QC nextthink = time (run step 2 next tick)
        }
        else if (_ballCnt < 4) // QC steps 2 & 3: velocity-glide back toward spawnorigin over 0.5 s
        {
            // QC velocity = (spawnorigin - origin) * (cnt - 1) — 1.0 then 0.5 second movement.
            e.Velocity = (BallHome - e.Origin) * (_ballCnt - 1);
            e.NextThink = GametypeEntities.Now + 0.5f;
            _ballCnt += 1;
        }
        else // QC step 4: snap home, re-arm InitBall after delay_start, clear ownership
        {
            e.Velocity = Vector3.Zero;
            GametypeEntities.SetOrigin(e, BallHome); // QC setorigin(this, spawnorigin)
            e.MoveType = MoveType.None;              // QC set_movetype(this, MOVETYPE_NONE)
            e.Think = BallThink;                     // QC setthink(this, InitBall)
            e.NextThink = global::System.Math.Max(GametypeEntities.Now, GameStartTime) + DelayStart;
            _ballCnt = 0;                            // glide complete; the next BallThink runs InitBall (release)
            _releasePending = true;
            BallTeam = Teams.None;
            BallPusher = null;
            _ballLifetime = 0f;
        }
    }

    /// <summary>
    /// QC InitBall: release the ball into play after the delay_start hold — restore bounce + the mode touch, clear
    /// the reset step, arm the idle-reset think (delay_idle), play the drop sound (noise1), and clear ownership.
    /// </summary>
    public void InitBall()
    {
        if (BallEntity is not Entity e)
            return;
        _releasePending = false;                // released — subsequent thinks run ResetBall (the glide)
        e.Flags &= ~EntFlags.OnGround;          // QC UNSET_ONGROUND(this)
        e.MoveType = MoveType.Bounce;           // QC set_movetype(this, MOVETYPE_BOUNCE)
        e.Touch = _isBasketball ? BasketballTouch : FootballTouch;
        _ballCnt = 0;                           // QC this.cnt = 0
        e.Think = BallThink;                    // QC setthink(this, ResetBall)
        e.NextThink = GametypeEntities.Now + DelayIdle + 3f; // QC nextthink = time + delay_idle + 3
        _ballLifetime = 0f;                     // QC this.lifetime = 0
        BallPusher = null;                      // QC this.pusher = NULL
        BallTeam = Teams.None;                  // QC this.team = false
        SoundSystem.PlayOn(e, Sounds.ByName("NB_DROP"), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm); // QC _sound(this, CH_TRIGGER, this.noise1, ...)
    }

    /// <summary>
    /// The ball's single Think trampoline (QC the InitBall/ResetBall function pointers). A carried ball with the
    /// DropOwner timer armed is handled separately (DropOwnerThink). Dispatches InitBall when the reset state
    /// machine has completed (cnt == -1, i.e. "release pending"), else advances ResetBall.
    /// </summary>
    private void BallThink(Entity self)
    {
        if (!ReferenceEquals(self, BallEntity))
            return;
        if (_releasePending) // QC setthink(InitBall) — release the ball into play after the delay_start hold
            InitBall();
        else
            ResetBall();
    }

    /// <summary>QC min(time + delay_idle, ball.lifetime): the idle-reset deadline, capped by the team-hold lifetime
    /// when one is armed (lifetime 0 = uncapped).</summary>
    private float MinIdle(float idleDeadline)
        => _ballLifetime > 0f ? global::System.Math.Min(idleDeadline, _ballLifetime) : idleDeadline;

    /// <summary>
    /// QC basketball_touch: a carryable ball. A toucher who already carries a ball falls through to
    /// <see cref="FootballTouch"/> (bump-as-soccer); otherwise a live player who isn't on the delay_collect
    /// cooldown and the reset isn't pending (cnt==0) catches it; a world touch plays the bounce sfx + re-arms idle.
    /// </summary>
    private void BasketballTouch(Entity self, Entity other)
    {
        // QC: if(toucher.ballcarried) { football_touch(this, toucher); return; } — a toucher who already carries a
        // ball doesn't catch this one, it bumps it like a football (bump-as-soccer).
        if (other.GtCarried is not null)
        {
            FootballTouch(self, other);
            return;
        }

        // QC: if(!this.cnt && IS_PLAYER && !IS_DEAD && (toucher != nb_dropper || time > nb_droptime + delay_collect))
        if (_ballCnt == 0 && other is Player p && !p.IsDead
            && (!ReferenceEquals(p, _ballDropper) || GametypeEntities.Now > _ballDropTime + DelayCollect))
        {
            GiveBall(p); // QC GiveBall(toucher, this)
            return;
        }

        // QC: else if(toucher.solid == SOLID_BSP) — a world bounce: sfx + re-arm idle.
        if (other.Solid == Solid.Bsp)
        {
            if (_bounceSoundEnabled) // QC: _sound(this, CH_TRIGGER, this.noise, ...) — noise is "" when g_nexball_sound_bounce=0
                SoundSystem.PlayOn(self, Sounds.ByName("NB_BOUNCE"), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
            if (self.Velocity != Vector3.Zero && _ballCnt == 0)
                self.NextThink = MinIdle(GametypeEntities.Now + DelayIdle);
        }
    }

    /// <summary>
    /// QC football_touch: a kickable (soccer) ball. A world bounce plays the bounce sfx (throttled 0.1 s) + re-arms
    /// idle; a live player/vehicle touch becomes the new pusher and is given a velocity boost (boost_forward/up via
    /// the view-independent physics mode 2) plus a backspin avelocity, so the ball can be driven toward a goal.
    /// </summary>
    private void FootballTouch(Entity self, Entity other)
    {
        if (other.Solid == Solid.Bsp)
        {
            // QC: world bounce — throttle the bounce sound to once per 0.1 s, re-arm idle.
            if (GametypeEntities.Now > _ballLastGround + 0.1f)
            {
                if (_bounceSoundEnabled) // QC noise is "" when g_nexball_sound_bounce=0 → silent bounce
                    SoundSystem.PlayOn(self, Sounds.ByName("NB_BOUNCE"), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
                _ballLastGround = GametypeEntities.Now;
            }
            if (self.Velocity != Vector3.Zero && _ballCnt == 0)
                self.NextThink = GametypeEntities.Now + DelayIdle;
            return;
        }

        if (other is not Player kicker || kicker.IsDead)
            return; // QC: !IS_PLAYER && !IS_VEHICLE, or RES_HEALTH < 1

        if (_ballCnt == 0)
            self.NextThink = GametypeEntities.Now + DelayIdle;

        BallPusher = kicker;            // QC this.pusher = toucher
        BallTeam = (int)kicker.Team;    // QC this.team = toucher.team

        // QC physics mode 2 (the default, fully view-independent): use only the kicker's YAW.
        // makevectors(toucher.v_angle.y * '0 1 0'); velocity = toucher.velocity + v_forward*boost_forward + v_up*boost_up.
        Vector3 angles = FootballPhysics == 2f ? new Vector3(0f, kicker.VAngle.Y, 0f) : kicker.VAngle;
        QMath.AngleVectors(angles, out Vector3 fwd, out _, out Vector3 up);
        if (FootballPhysics == -1f)
        {
            // QC mode -1: velocity = toucher.velocity * 1.5 + '0 0 1'*boost_up (only when the kicker is moving).
            if (kicker.Velocity != Vector3.Zero)
                self.Velocity = kicker.Velocity * 1.5f + new Vector3(0f, 0f, 1f) * FootballBoostUp;
        }
        else if (FootballPhysics == 2f)
        {
            self.Velocity = kicker.Velocity + fwd * FootballBoostForward + up * FootballBoostUp;
        }
        else // QC modes 0 (Revenant) and 1: full view angles, v_forward + '0 0 1'/v_up boost.
        {
            self.Velocity = kicker.Velocity + fwd * FootballBoostForward + up * FootballBoostUp;
        }
        self.AVelocity = fwd * -250f; // QC this.avelocity = -250 * v_forward (backspin)
    }

    // ============================================================================================
    //  Goal ENTITIES (QC nexball_*goal / nexball_fault / nexball_out + GoalTouch)
    // ============================================================================================

    /// <summary>
    /// QC SpawnGoal (nexball_redgoal/.../fault/out): create a goal trigger volume owned by
    /// <paramref name="goalTeam"/> (a team color, or <see cref="GoalFault"/>/<see cref="GoalOut"/>). Touch
    /// dispatches scoring via <see cref="GoalTouch"/>.
    /// </summary>
    public Entity? SpawnGoal(int goalTeam, Vector3 origin, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-64f, -64f, -64f); maxs = new Vector3(64f, 64f, 64f); }
        Entity? e = GametypeEntities.SpawnObjective("nexball_goal", origin, Teams.None, mins, maxs,
            touch: GoalTouchEntity);
        if (e is not null)
        {
            e.Flags = EntFlags.None;
            e.GtHomeTeam = goalTeam; // the goal's "team"/kind sentinel
            Goals.Add(e);
        }
        return e;
    }

    /// <summary>
    /// QC GoalTouch: the ball entered goal <paramref name="goalEnt"/>. Derive the goal kind from the goal's
    /// team vs the ball's team — own-goal/fault → −1 (credited to the other team in 2-team play), enemy goal
    /// → +1, out → no score — apply it via <see cref="ScoreGoal"/>, play the goal sound, then arm the delayed
    /// ball reset (delay_goal; 0 for OUT). A scored basketball drops its carrier and is converted to football
    /// control (cnt=1 + football_touch) for the duration of the reset delay so the ball physically falls through.
    /// </summary>
    public void GoalTouch(Entity goalEnt)
    {
        if (MatchEnded)
            return; // QC: game_stopped guard
        int goalTeam = goalEnt.GtHomeTeam;

        // QC: if((!ball.pusher && this.team != GOAL_OUT) || ball.cnt) return; — no scorer (except OUT) or a ball
        // already mid-reset can't score.
        if ((BallTeam == Teams.None && goalTeam != GoalOut) || _ballCnt != 0)
            return;

        int otherTeam = TeamCountIsTwo() && BallTeam != Teams.None ? OtherTeam(BallTeam) : Teams.None;

        // QC: the scorer name for the bprint announcement (IS_CLIENT(ball.pusher) ? netname : "Someone (?)").
        string pname = BallPusher?.NetName ?? "Someone (?)";
        bool ballWasCarried = BallEntity?.GtCarrier is Player; // QC: ball.owner set ⇒ GOAL_TOUCHPLAYER carrier scored

        if (goalTeam == GoalOut)
        {
            // QC GOAL_OUT: pscore = 0 — no score, the ball is just returned (delay 0).
            // QC bprint: a carried ball out-of-bounds names the carrier; a loose ball "was returned".
            if (ballWasCarried)
                NotificationSystem.Info("NEXBALL_OUT_PLAYER", pname);
            else
                NotificationSystem.Info("NEXBALL_OUT");
        }
        else if (goalTeam == BallTeam)
        {
            // QC own-goal: pscore = -1, bprint "Boo! <name> scored a goal against their own team!".
            NotificationSystem.Info("NEXBALL_OWNGOAL", pname);
            ScoreGoal(BallTeam, GoalKind.OwnGoal, otherTeam);
        }
        else if (goalTeam == GoalFault)
        {
            // QC fault: pscore = -1. Two-team → the OTHER team gains a point ("<otherteam> gets a point due to
            // <name>'s silliness"); >2-team → the ball's team loses one ("<ballteam> loses a point ...").
            if (TeamCountIsTwo())
                NotificationSystem.Info($"NEXBALL_FAULT_{TeamSuffix(otherTeam)}", pname);
            else
                NotificationSystem.Info($"NEXBALL_FAULT_LOSE_{TeamSuffix(BallTeam)}", pname);
            ScoreGoal(BallTeam, GoalKind.OwnGoal, otherTeam);
        }
        else
        {
            // QC enemy goal: pscore = +1 for the ball's team, bprint "Goaaaaal! <name> scored a point for the <team>".
            NotificationSystem.Info($"NEXBALL_GOAL_{TeamSuffix(BallTeam)}", pname);
            ScoreGoal(BallTeam, GoalKind.Score, otherTeam);
        }

        if (BallEntity is Entity ball)
        {
            // QC: _sound(ball, CH_TRIGGER, this.noise, VOL_BASE, ATTEN_NONE) — the goal's noise. A team goal's noise
            // defaults to "ctf/respawn.wav" (NB_GOAL); a fault/out goal overrides it with SND(TYPEHIT). The goal entity
            // carries no per-goal noise field in the port, so pick the faithful default by goal kind.
            string goalSound = goalTeam is GoalFault or GoalOut ? "TYPEHIT" : "NB_GOAL"; // QC ctf/respawn.wav score cue
            SoundSystem.PlayOn(ball, Sounds.ByName(goalSound), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNone);

            // QC GOAL_TOUCHPLAYER (ball.owner): a carrier scored directly — drop the ball where they stand.
            if (ball.GtCarrier is Player owner)
                DropBall(owner.Origin, owner.Velocity);

            // QC: ball.cnt = 1; setthink(ResetBall); for a basketball settouch(football_touch) (football control
            // until reset); nextthink = time + delay_goal * (this.team != GOAL_OUT) — OUT resets next tick.
            _ballCnt = 1;
            ball.Think = BallThink;
            if (_isBasketball)
                ball.Touch = FootballTouch;
            ball.NextThink = GametypeEntities.Now + DelayGoal * (goalTeam != GoalOut ? 1f : 0f);
        }

        // The goal is scored and the reset is armed; ownership is logically cleared now (the ball.cnt guard above
        // prevents a second score before the glide-home think actually returns the ball). This keeps BallTeam/
        // BallPusher consistent with "no live ball in play" immediately, matching the headless reset contract.
        BallTeam = Teams.None;
        BallPusher = null;
        _ballLifetime = 0f;
    }

    private void GoalTouchEntity(Entity self, Entity other)
    {
        // The ball (or a ball-carrier) hit the goal.
        if (ReferenceEquals(other, BallEntity) || (other is Player p && ReferenceEquals(p.GtCarried, BallEntity)))
            GoalTouch(self);
    }

    /// <summary>
    /// QC nb_spawnteams: the distinct team colours present on the map, derived from the goal entities (a
    /// <c>nexball_*goal</c> per team contributes that team; fault/out volumes don't). Mirrors QC scanning the
    /// nexball_goal ents to set teamplay_bitmask, so a 3/4-team Nexball map ranks all its teams instead of a
    /// hardcoded red/blue pair. Falls back to {Red, Blue} when no team goals are wired yet (the headless/test
    /// default, and before WireObjectiveSpawns runs).
    /// </summary>
    private List<int> PresentTeams()
    {
        // Nexball is at least two-team (QC gametype_init teams default; AVAILABLE_TEAMS >= 2), so red+blue are
        // always present. Yellow/Pink are added only when a goal of that colour exists on the map (QC nb_spawnteams
        // sets teamplay_bitmask bits 2/3 for yellow/pink goals). This keeps the standard red/blue map two-team while
        // ranking the extra teams on a 3/4-team Nexball map.
        var teams = new List<int> { Teams.Red, Teams.Blue };
        foreach (Entity g in Goals)
        {
            int t = g.GtHomeTeam;
            if ((t == Teams.Yellow || t == Teams.Pink) && !teams.Contains(t))
                teams.Add(t);
        }
        return teams;
    }

    private bool _teamsSeededFromGoals;

    /// <summary>QC nb_delayedinit → nb_ScoreRules(teamplay_bitmask): once the goal entities exist, seed a score
    /// slot for every team that has a goal on the map (instead of the unconditional red/blue pair). One-shot per
    /// activation (re-armed by OnInit); safe to call each tick — it only does work the first time goals exist.</summary>
    public void SeedTeamsFromGoals()
    {
        if (_teamsSeededFromGoals || Goals.Count == 0)
            return;
        foreach (int t in PresentTeams())
            GS.SeedTeam(t);
        _teamsSeededFromGoals = true;
    }

    /// <summary>QC OtherTeam (two-team only): the "other" team colour among the present teams. For the standard
    /// two-team map this is the opposing colour; with &gt;2 teams QC's OtherTeam is undefined (it only runs when
    /// AVAILABLE_TEAMS==2), so we keep the two-team semantics and pick the first present team that isn't
    /// <paramref name="team"/>.</summary>
    private int OtherTeam(int team)
    {
        foreach (int t in PresentTeams())
            if (t != team)
                return t;
        return team == Teams.Red ? Teams.Blue : Teams.Red;
    }

    /// <summary>QC AVAILABLE_TEAMS == 2: exactly two teams have goals on the map (the common case; the own-goal
    /// "credit the other team" branch only applies in two-team play).</summary>
    private bool TeamCountIsTwo() => PresentTeams().Count == 2;

    /// <summary>The goal limit in force (g_nexball_goallimit, else fraglimit, else 5). 0 means no limit.</summary>
    public int GoalLimit
    {
        get
        {
            if (TryCvar(CvarGoalLimit, out float gl)) return (int)gl;
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return DefaultGoalLimit;
        }
    }

    // ============================================================================================
    //  Weapon-arena seams  (QC sv_nexball.qc MUTATOR_HOOKFUNCTION(nb, PlayerSpawn / PlayerPreThink /
    //  ForbidThrowCurrentWeapon / FilterItem / ItemTouch / WantWeapon + GiveBall weapon-swap))
    // ============================================================================================

    /// <summary>QC <c>autocvar_g_nexball_basketball_meter</c> (default 1): when true the primary power-meter is
    /// active (the BallStealer WrThink reads this to decide start-on-press vs fire-on-press).</summary>
    public bool MeterEnabled
        => GametypeEntities.Cvar("g_nexball_basketball_meter", 1f) != 0f;

    /// <summary>QC <c>ball.enemy</c>: the teammate locked as a safe-pass target by the PreThink crosshair trace.
    /// Null when no lock is active. Read by the BallStealer secondary fire path.</summary>
    public Entity? BallSafePassTarget
        => BallEntity?.GtSafePassTarget;

    /// <summary>
    /// QC <c>W_Nexball_Attack</c> conclusion: drop the ball as a projectile at <paramref name="org"/> with
    /// <paramref name="vel"/>. When <paramref name="homingTarget"/> is set the ball steers toward it each frame
    /// (safe-pass arc). Clears the carrier's NbMeterStart on slot 0.
    /// </summary>
    public void LaunchBall(Entity actor, Vector3 org, Vector3 vel, Entity? homingTarget = null)
    {
        if (BallEntity is not Entity ball)
            return;

        // Clear the carrier's power-meter (QC: NB_METERSTART clears on DropBall or on the shot path).
        actor.WeaponState(new WeaponSlot(0)).NbMeterStart = 0f;

        // Restore normal weapons so GiveBall weapon-swap doesn't leave the player stuck on BallStealer.
        RestoreWeapons(actor);

        // QC DropBall(ball, w_shotorg, w_shotdir * speed * mul): drop the ball at the muzzle origin.
        DropBall(org, vel);

        if (homingTarget is not null)
        {
            // QC W_Nexball_Think: install a per-frame homing think that steers ball.velocity toward ball.enemy.
            ball.GtSafePassTarget = homingTarget;
            ball.Think = HomingThink;
            ball.NextThink = GametypeEntities.Now; // run immediately on the next tick
        }
    }

    /// <summary>
    /// QC <c>W_Nexball_Think</c>: the homing think for a safe-pass arc — steers the ball toward
    /// <c>ball.enemy</c> each frame at <c>g_nexball_safepass_turnrate</c> (default 0.1). Runs every tick
    /// until the ball touches something (touch reinstalls the normal touch handler).
    /// </summary>
    private void HomingThink(Entity ball)
    {
        Entity? target = ball.GtSafePassTarget;
        if (target is null || target.IsFreed)
        {
            ball.Think = BallThink;
            return;
        }

        float turnrate = GametypeEntities.Cvar("g_nexball_safepass_turnrate", 0.1f);
        // QC W_Nexball_Think (sv_weapon.qc):
        //   new_dir = normalize(this.enemy.origin + '0 0 50' - this.origin);
        //   old_dir = normalize(this.velocity);
        //   new_vel = normalize(old_dir + (new_dir * turnrate)) * vlen(this.velocity);
        // Note: old_dir keeps weight 1.0 (it is NOT scaled by 1-turnrate); the +'0 0 50' lofts the aim point.
        float speed = ball.Velocity.Length();
        if (speed < 1f)
        {
            ball.GtSafePassTarget = null;
            return;
        }
        Vector3 newDir = Vector3.Normalize(target.Origin + new Vector3(0f, 0f, 50f) - ball.Origin);
        Vector3 oldDir = ball.Velocity / speed;
        Vector3 sum = oldDir + newDir * turnrate;
        float sumLen = sum.Length();
        if (sumLen > 0f)
            ball.Velocity = sum / sumLen * speed;

        ball.NextThink = GametypeEntities.Now; // QC: this.nextthink = time (run again next tick)
    }

    /// <summary>
    /// QC <c>GiveBall</c> weapon-arena swap: the carrier gets only <c>WEP_NEXBALL</c>; their normal weapons
    /// are saved in <c>STAT(WEAPONS, player.(weaponentity))</c> (the per-slot scratch) and restored when they
    /// stop carrying. Called by <see cref="GiveBall"/> after the carry-attach.
    /// </summary>
    private static void SaveAndApplyNexballWeapons(Entity player)
    {
        Weapon? ballStealer = Weapons.ByName("ballstealer");
        if (ballStealer is null)
            return; // BallStealer not registered yet (headless tests that don't load weapons)

        // QC GiveBall: STAT(WEAPONS, actor.(weaponentity)) = STAT(WEAPONS, actor) — save existing set.
        player.GtSavedWeaponSet = player.OwnedWeaponSet;
        player.GtSavedActiveWeaponId = player.ActiveWeaponId;

        // QC: STAT(WEAPONS, actor) = WEPSET(WEP_NEXBALL); W_SwitchWeapon(actor, WEP_NEXBALL).
        player.OwnedWeaponSet.Clear();
        Inventory.GiveWeapon(player, ballStealer);
        Inventory.SwitchWeapon(player, ballStealer);
    }

    /// <summary>
    /// QC <c>PlayerPreThink</c> non-carrier branch: restore the player's normal weapons from the per-slot
    /// save (<c>STAT(WEAPONS, player.(weaponentity))</c>) and switch back to the weapon they had before.
    /// Called by <see cref="OnPlayerPreThink"/> for non-carrying players in basketball mode.
    /// </summary>
    private static void RestoreWeapons(Entity player)
    {
        if (player.GtSavedActiveWeaponId < 0)
            return; // nothing saved — player never had the ball or was already restored

        WepSet saved = player.GtSavedWeaponSet;
        int savedActive = player.GtSavedActiveWeaponId;

        // QC: STAT(WEAPONS, actor.(weaponentity)) is zeroed after restore.
        player.GtSavedWeaponSet = default;
        player.GtSavedActiveWeaponId = -1;

        // Restore set and switch back to the previously held weapon.
        player.OwnedWeaponSet = saved;

        Weapon? prev = savedActive >= 0 && savedActive < Registry<Weapon>.Count
            ? Registry<Weapon>.ById(savedActive) : null;
        if (prev is not null && player.OwnedWeaponSet.Has(prev))
            Inventory.SwitchWeapon(player, prev);
        else
            Inventory.SwitchToBest(player);
    }

    /// <summary>
    /// QC <c>nb_StealBall(attacker)</c> / the <c>W_Nexball_Touch</c> steal path: give the ball to the
    /// attacker (the tackle projectile already removed it from the victim via <see cref="DropBall"/>).
    /// Called by <see cref="BallStealer.TackleTouch"/>.
    /// </summary>
    public void StealBall(Player attacker)
    {
        if (BallEntity is null)
            return;
        // QC W_Nexball_Touch: GiveBall(attacker, toucher.ballcarried) — the ball is handed straight from the
        // victim to the attacker WITHOUT a loose-drop. GiveBall now detaches the previous carrier (the victim)
        // at its top, re-attaches the ball to the attacker, and applies the weapon-arena swap.
        GiveBall(attacker);
    }

    // ---- hook handler fields ----
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _playerSpawnHandler;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _playerPreThinkHandler;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _forbidThrowHandler;
    private HookHandler<MutatorHooks.FilterItemArgs>? _filterItemHandler;
    private HookHandler<MutatorHooks.ItemTouchArgs>? _itemTouchHandler;

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(nb, PlayerSpawn)</c> (sv_nexball.qc): on (re)spawn in basketball mode, zero
    /// the player's weapon slots and grant only <c>WEP_NEXBALL</c>. The non-carrier branch of PlayerPreThink
    /// restores normal weapons; this zeroing matches the QC <c>for(int slot…) STAT(WEAPONS, wepent)=0</c> +
    /// <c>if(NBM_BASKETBALL) STAT(WEAPONS, player)|=WEPSET(NEXBALL)</c> pass.
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;

        // QC PlayerSpawn:
        //   STAT(NB_METERSTART, player) = 0;
        //   for(slot) STAT(WEAPONS, player.(weaponentity)) = '0 0 0';  // clear the SAVED per-slot set
        //   if (NBM_BASKETBALL) STAT(WEAPONS, player) |= WEPSET(NEXBALL);  // ADD nexball, keep start weapons
        //   else STAT(WEAPONS, player) = '0 0 0';                          // football: NO weapons at all
        // The meter + saved-loadout scratch is wiped on every (re)spawn (no stale carry state).
        player.ForEachWeaponSlot(s => { s.NbMeterStart = 0f; });
        player.GtSavedWeaponSet = default;
        player.GtSavedActiveWeaponId = -1;

        if (_isBasketball)
        {
            // QC `|=`: the player keeps their normal start weapons and ALSO gains the (ball-less-unusable)
            // BallStealer; the active weapon is NOT changed here (GiveBall does the arena swap on pickup).
            Weapon? ballStealer = Weapons.ByName("ballstealer");
            if (ballStealer is not null)
                Inventory.GiveWeapon(player, ballStealer);
        }
        else
        {
            // QC football mode: STAT(WEAPONS, player) = '0 0 0' — strip every weapon (kick-only).
            player.OwnedWeaponSet.Clear();
            Inventory.SwitchToBest(player); // settles ActiveWeaponId to -1 (no weapons)
        }

        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(nb, PlayerPreThink)</c> non-carrier branch: a player who is no longer
    /// carrying the ball has their saved weapon set restored and switches back to their previous weapon.
    /// (The carrier branch — view-ball follow + safe-pass crosshair lock — is <see cref="CarryFrame"/>.)
    /// </summary>
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        if (!_isBasketball)
            return false;

        Entity player = args.Player;
        // QC: if(!player.ballcarried) → restore weapons.
        bool isCarrier = BallEntity is not null && ReferenceEquals(player.GtCarried, BallEntity);
        if (!isCarrier && player.GtSavedActiveWeaponId >= 0)
        {
            // Non-carrier with a saved loadout: restore it (QC restores STAT(WEAPONS) and switches back).
            RestoreWeapons(player);
        }
        return false;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(nb, ForbidThrowCurrentWeapon)</c> (sv_nexball.qc):
    /// the BallStealer (<c>WEP_NEXBALL</c>) can never be thrown/dropped as a pickup.
    /// (The port folds QC's ForbidDropCurrentWeapon into this same chain.)
    /// </summary>
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args)
    {
        Weapon? ballStealer = Weapons.ByName("ballstealer");
        if (ballStealer is null) return false;
        // QC ForbidThrowCurrentWeapon: wepent.m_weapon == WEP_NEXBALL → return true.
        Weapon? cur = Inventory.CurrentWeapon(args.Player);
        return cur is not null && cur.RegistryId == ballStealer.RegistryId;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(nb, FilterItem)</c> (sv_nexball.qc):
    /// remove any loot BallStealer pickup that might have been spawned (they should never appear in the world).
    /// </summary>
    private bool OnFilterItem(ref MutatorHooks.FilterItemArgs args)
    {
        Weapon? ballStealer = Weapons.ByName("ballstealer");
        if (ballStealer is null) return false;
        // QC: if (ITEM_IS_LOOT(item) && item.weapon == WEP_NEXBALL.m_id) return true (remove it).
        Entity item = args.Item;
        // QC: ITEM_IS_LOOT(item) && item.weapon == WEP_NEXBALL.m_id → remove.
        // In the port a weapon pickup item carries the weapon in its OwnedWeaponSet (WeaponPickup.ItemInit).
        return item.ItemIsLoot && item.OwnedWeaponSet.Has(ballStealer);
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(nb, ItemTouch)</c> (sv_nexball.qc): a carrier who touches a weapon item
    /// doesn't get it — the nexball weapon-arena keeps carriers locked to BallStealer while holding the ball.
    /// Returns true (= <c>MUT_ITEMTOUCH_RETURN</c>) when the toucher is carrying the nexball ball AND the item
    /// has a weapon (QC: <c>if(item.weapon && toucher.ballcarried) return MUT_ITEMTOUCH_RETURN</c>).
    /// </summary>
    private bool OnItemTouch(ref MutatorHooks.ItemTouchArgs args)
    {
        Entity toucher = args.Toucher;
        Entity item = args.Item;
        // QC: item.weapon != 0 → it is a weapon item (instanceOfWeaponPickup in QC).
        // The port stores the weapon pickup discriminator on item.Pickup.IsWeaponPickup.
        bool isWeaponItem = item.Pickup?.IsWeaponPickup == true;
        bool isCarrier = BallEntity is not null && ReferenceEquals(toucher.GtCarried, BallEntity);
        return isWeaponItem && isCarrier;
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC nb_ScoreRules (sv_nexball.qc) GameRules_scoring(teams, 0, 0, { field_team(ST_NEXBALL_GOALS, "goals",
        // PRIMARY); field(SP_NEXBALL_GOALS, "goals", PRIMARY); field(SP_NEXBALL_FAULTS, "faults", SECONDARY |
        // LOWER_IS_BETTER); }): BOTH SP_SCORE (spprio=0) and ST_SCORE (stprio=0) are non-primary; the player
        // primary is SP_NEXBALL_GOALS (secondary FAULTS, fewer is better); the TEAM primary is ST_NEXBALL_GOALS
        // (slot 1, goal totals).
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0 (non-primary)
        GS.SetTeamLabel(GS.TeamSlotSecondary, "goals", Scoring.ScoreFlags.SortPrioPrimary); // ST_NEXBALL_GOALS (slot 1)
        GS.DeclareColumn("NEXBALL_GOALS", Scoring.ScoreFlags.None, "goals");
        GS.DeclareColumn("NEXBALL_FAULTS", Scoring.ScoreFlags.LowerIsBetter, "faults"); // QC: SECONDARY | LOWER_IS_BETTER
        GS.SetSortKeys(GS.Field("NEXBALL_GOALS")!, GS.Field("NEXBALL_FAULTS"));
        GS.SeedTeams(2); // Nexball is two-team (red vs blue; AVAILABLE_TEAMS == 2)

        // QC nb_Initialize: round the basketball meter period to 1/32 s (sent as a byte ×32), min 2 if <= 0. The
        // power-meter weapon doesn't exist in the port yet, but we compute the value so the future weapon/HUD seam
        // reads the bit-faithful period (see nexball.weapon.power_meter / nexball.hud.modicon todos).
        float meter = GametypeEntities.Cvar("g_nexball_meter_period", 1f);
        if (meter <= 0f) meter = 2f;
        MeterPeriod = global::System.MathF.Round(meter * 32f) / 32f;

        // QC nb_Initialize: radar_showenemies = g_nexball_radar_showallplayers (default 1) — the radar shows every
        // player (not just teammates). radar_showenemies is a CLIENT-read replicated cvar; mirror the value so the
        // (client) radar reads the faithful default. No server consumer, so this is a thin cvar bridge.
        if (Api.Services is not null)
            Api.Cvars.Set("radar_showenemies", GametypeEntities.Cvar("g_nexball_radar_showallplayers", 1f) != 0f ? "1" : "0");

        // QC MUTATOR_HOOKFUNCTION(nb, PlayerPhysics_UpdateStats): the basketball carrier is slowed —
        // STAT(MOVEVARS_HIGHSPEED) *= g_nexball_basketball_carrier_highspeed (default 0.8). Wire the live
        // PlayerPhysics hook so the carrier actually moves slower.
        _physicsHandler ??= OnPlayerPhysics;
        MutatorHooks.PlayerPhysics.Add(_physicsHandler);

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTION(nb, PlayerSpawn): weapon-arena loadout on spawn.
        _playerSpawnHandler = OnPlayerSpawn;
        MutatorHooks.PlayerSpawn.Add(_playerSpawnHandler);

        // QC MUTATOR_HOOKFUNCTION(nb, PlayerPreThink): restore normal weapons for non-carriers.
        _playerPreThinkHandler = OnPlayerPreThink;
        MutatorHooks.PlayerPreThink.Add(_playerPreThinkHandler);

        // QC MUTATOR_HOOKFUNCTION(nb, ForbidThrowCurrentWeapon): can't drop the ball-stealer.
        _forbidThrowHandler = OnForbidThrowCurrentWeapon;
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_forbidThrowHandler);

        // QC MUTATOR_HOOKFUNCTION(nb, FilterItem): remove loot WEP_NEXBALL pickups.
        _filterItemHandler = OnFilterItem;
        MutatorHooks.FilterItem.Add(_filterItemHandler);

        // QC MUTATOR_HOOKFUNCTION(nb, ItemTouch): carriers can't pick up weapon items.
        _itemTouchHandler = OnItemTouch;
        MutatorHooks.ItemTouch.Add(_itemTouchHandler);
    }

    /// <summary>QC autocvar_g_nexball_basketball_carrier_highspeed (default 0.8): the carrier top-speed multiplier.</summary>
    public float CarrierHighspeed => TryCvar(CvarCarrierHighspeed, out float v) && v > 0f ? v : DefaultCarrierHighspeed;

    /// <summary>QC MUTATOR_HOOKFUNCTION(nb, PlayerPhysics_UpdateStats): slow the basketball carrier by
    /// <see cref="CarrierHighspeed"/>. "these automatically reset, no need to worry" (QC) — the multiplier is
    /// re-applied every physics frame the player is carrying the ball.</summary>
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        if (args.Player is Player p && ReferenceEquals(p.GtCarried, BallEntity) && BallEntity is not null)
            p.SpeedMultiplier *= CarrierHighspeed; // QC STAT(MOVEVARS_HIGHSPEED, player) *= carrier_highspeed
        return false;
    }

    /// <summary>QC g_nexball_meter_period (nb_Initialize): the rounded basketball power-meter cycle in seconds
    /// (rounded to 1/32 s, min 2 when the cvar is &lt;= 0). Read by the (deferred) power-meter weapon + HUD bar.</summary>
    public float MeterPeriod { get; private set; } = 1f;

    public override void Deactivate()
    {
        if (_physicsHandler is not null)
        {
            MutatorHooks.PlayerPhysics.Remove(_physicsHandler);
            _physicsHandler = null;
        }
        if (_deathHandler is not null)
        {
            Combat.Death.Remove(_deathHandler);
            _deathHandler = null;
        }
        if (_playerSpawnHandler is not null) { MutatorHooks.PlayerSpawn.Remove(_playerSpawnHandler); _playerSpawnHandler = null; }
        if (_playerPreThinkHandler is not null) { MutatorHooks.PlayerPreThink.Remove(_playerPreThinkHandler); _playerPreThinkHandler = null; }
        if (_forbidThrowHandler is not null) { MutatorHooks.ForbidThrowCurrentWeapon.Remove(_forbidThrowHandler); _forbidThrowHandler = null; }
        if (_filterItemHandler is not null) { MutatorHooks.FilterItem.Remove(_filterItemHandler); _filterItemHandler = null; }
        if (_itemTouchHandler is not null) { MutatorHooks.ItemTouch.Remove(_itemTouchHandler); _itemTouchHandler = null; }
    }

    /// <summary>The current goal count for a team color code (QC teamscores(team, ST_NEXBALL_GOALS); 0 if none).</summary>
    public int GoalsFor(int team) => GS.TeamScore(team, GS.TeamSlotSecondary);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on goals
    /// (ST_NEXBALL_GOALS, Nexball's ranking primary), so a tied timed Nexball enters overtime instead of
    /// drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(GS.LeaderTeam(), GS.SecondTeam(), GoalsFor);

    /// <summary>
    /// Resolve a ball-into-goal event (QC GoalTouch). For a normal <see cref="GoalKind.Score"/> the scorer's
    /// team gains a point; for an <see cref="GoalKind.OwnGoal"/> a point is taken away — in a two-team game by
    /// crediting the <paramref name="otherTeam"/> instead (QC: pscore&lt;0 ⇒ TeamScore_AddToTeam(otherteam)),
    /// otherwise by docking the scorer's own team. Then re-check the goal limit. The ball/goal geometry that
    /// decides which branch applies is deferred; the host calls this when it detects a goal.
    /// </summary>
    /// <param name="scorerTeam">The team color code of the player who last touched the ball (QC ball.team).</param>
    /// <param name="otherTeam">The opposing team in a two-team game, or 0 if not applicable.</param>
    public void ScoreGoal(int scorerTeam, GoalKind kind, int otherTeam = 0)
    {
        if (MatchEnded || scorerTeam == Teams.None)
            return;

        if (kind == GoalKind.Score)
        {
            AddTeamGoal(scorerTeam, +1);
            // QC GoalTouch: pscore > 0 → GameRules_scoring_add(ball.pusher, NEXBALL_GOALS, pscore).
            if (BallPusher is not null) AddCol(BallPusher, "NEXBALL_GOALS", 1);
        }
        else // OwnGoal / fault: −1
        {
            if (otherTeam != Teams.None)
                AddTeamGoal(otherTeam, +1);   // QC two-team: the point goes to the other team
            else
                AddTeamGoal(scorerTeam, -1);  // otherwise dock the scorer's team
            // QC GoalTouch: pscore < 0 → GameRules_scoring_add(ball.pusher, NEXBALL_FAULTS, -pscore).
            if (BallPusher is not null) AddCol(BallPusher, "NEXBALL_FAULTS", 1);
        }

        CheckGoalLimit();
    }

    /// <summary>The team-color suffix used to key the per-team INFO notifications (QC Team_ColoredFullName baked
    /// into the per-team notification variant, matching the CTF/KeyHunt/Onslaught convention).</summary>
    private static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "NEUTRAL",
    };

    private void AddTeamGoal(int team, int delta)
        => GS.AddToTeam(team, GS.TeamSlotSecondary, delta); // QC TeamScore_AddToTeam(team, ST_NEXBALL_GOALS, delta)

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a Nexball player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>QC GameRules_limit_lead(g_nexball_goalleadlimit): the goal lead at which the match ends early.
    /// The cvar default is -1 = "use the mapinfo leadlimit"; QC's GameRules_limit_lead leaves the engine
    /// <c>leadlimit</c> cvar (set by mapinfo) untouched when the arg is &lt; 0, so -1 means "fall back to the
    /// mapinfo/engine leadlimit"; a value &gt;= 0 overrides it (0 = disabled).</summary>
    public int GoalLeadLimit
    {
        get
        {
            if (TryCvar("g_nexball_goalleadlimit", out float ll))
            {
                if (ll < 0f) // -1: use the mapinfo/engine leadlimit (GameRules_limit_lead returns early, no override)
                    return TryCvar("leadlimit", out float ml) && ml > 0f ? (int)ml : 0;
                return ll > 0f ? (int)ll : 0; // >= 0 overrides; 0 disables
            }
            return 0;
        }
    }

    /// <summary>QC GameRules_limit_score + GameRules_limit_lead: latch the match once a team reaches the goal limit,
    /// or once the leader is ahead of the runner-up by at least the lead limit.</summary>
    public void CheckGoalLimit()
    {
        if (MatchEnded)
            return;

        // QC GameRules_limit_score: the leading team (the flag-aware ST_NEXBALL_GOALS leader) reaching the limit wins.
        int leader = GS.LeaderTeam();
        if (leader == Teams.None)
            return;

        int limit = GoalLimit;
        if (limit > 0 && GoalsFor(leader) >= limit)
        {
            MatchEnded = true;
            WinningTeam = leader;
            return;
        }

        // QC GameRules_limit_lead: end when the leader's margin over the runner-up reaches the lead limit.
        int lead = GoalLeadLimit;
        if (lead > 0)
        {
            int second = GS.SecondTeam();
            int margin = GoalsFor(leader) - (second != Teams.None ? GoalsFor(second) : 0);
            if (margin >= lead)
            {
                MatchEnded = true;
                WinningTeam = leader;
            }
        }
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(nb, ClientDisconnect) + (nb, MakePlayerObserver) → nb_DropBall(player): a player who
    /// leaves play (disconnects, or is forced to observer) drops the ball where they stand, inheriting their
    /// velocity, so the ball can never stay stuck on a gone/spectating carrier. Dispatched once per leave-play event
    /// from the server (ClientManager.ClientDisconnect / PutObserverInServer → GameType.OnPlayerRemoved).
    /// </summary>
    public override void OnPlayerRemoved(Player player)
    {
        // QC nb_DropBall: if(player.ballcarried && g_nexball) DropBall(player.ballcarried, player.origin, player.velocity).
        DropBall(player); // no-op unless this player is the live ball's carrier
    }

    /// <summary>
    /// QC nexball waypoint sprites, rebuilt each tick from live state (the port's pull model, like CTF/Domination):
    ///  - WP_NbBall (sv_nexball.qc): attached to the CARRIER while held (QC GiveBall WaypointSprite_AttachCarrier
    ///    on plyr), else floating on the LOOSE ball at its ground position +'0 0 64' (QC DropBall / SpawnBall
    ///    WaypointSprite_Spawn at the ball, offset '0 0 64'). Suppressed during the goal/idle reset glide
    ///    (_ballCnt != 0) — there is no in-play ball to mark while it returns home.
    ///  - WP_NbGoal (sv_nexball.qc SpawnGoal WaypointSprite_SpawnFixed): one fixed marker per team goal at its
    ///    center, shown only to the OTHER team (QC nb_Goal_Customize: SAME_TEAM(viewer, goal) → hidden), tinted the
    ///    goal team's color. Fault/out volumes get no waypoint (QC only spawns one for a valid team goal).
    /// </summary>
    public override void CollectWaypoints(List<Waypoints.WaypointSprite> into)
    {
        // WP_NbBall — only while a live ball is actually in play (not mid-reset glide / pre-release).
        if (BallEntity is Entity ball && _ballCnt == 0)
        {
            if (ball.GtCarrier is Player carrier)
            {
                // QC GiveBall: WaypointSprite_AttachCarrier(WP_NbBall, plyr, ...) — rides the carrier.
                into.Add(new Waypoints.WaypointSprite
                {
                    SpriteName = "NbBall",
                    Owner = carrier,
                    Team = 0,                       // shown to everyone (carrier marker)
                    Color = NbBallColor,
                    RadarIcon = 1,                  // RADARICON_FLAGCARRIER
                    Health = -1f,                   // QC "no health bar please"
                });
            }
            else
            {
                // QC DropBall / SpawnBall: WaypointSprite at the ball, offset '0 0 64'.
                into.Add(new Waypoints.WaypointSprite
                {
                    SpriteName = "NbBall",
                    FixedOrigin = ball.Origin + new Vector3(0f, 0f, 64f),
                    Team = 0,
                    Color = NbBallColor,
                    RadarIcon = 1,
                    Health = -1f,
                });
            }
        }

        // WP_NbGoal — one fixed marker per VALID team goal, shown to the enemy team only (QC nb_Goal_Customize).
        foreach (Entity g in Goals)
        {
            int goalTeam = g.GtHomeTeam;
            if (goalTeam is GoalFault or GoalOut || goalTeam == Teams.None)
                continue; // QC: WaypointSprite_SpawnFixed only for Team_IsValidTeam(this.team) && != GOAL_OUT
            int gt = goalTeam; // capture for the closure
            into.Add(new Waypoints.WaypointSprite
            {
                SpriteName = "NbGoal",
                // QC WaypointSprite_SpawnFixed((absmin+absmax)*0.5) — the goal volume's center. The port spawns the
                // goal at its center origin (SpawnObjective), so g.Origin is already the midpoint.
                FixedOrigin = g.Origin,
                Team = goalTeam,
                Color = Teams.ColorRgb(goalTeam),
                RadarIcon = 0, // QC RADARICON_NONE (goals are not radar dots)
                Health = -1f,
                // QC nb_Goal_Customize: hide for a viewer on the goal's OWN team (you defend your own goal).
                VisibleForPlayer = viewer => viewer is null || (int)viewer.Team != gt,
            });
        }
    }

    /// <summary>QC WP_NbBall radar color (the ball waypoint tint), matching the waypoint-def palette.</summary>
    private static readonly Vector3 NbBallColor = new(0.91f, 0.85f, 0.62f);

    /// <summary>Kills don't score in Nexball (the ball does); a carrier who dies drops the ball.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        // QC nb (PlayerDies / DropSpecialItems hooks → nb_DropBall): a carrier who dies drops the ball where they
        // fell, inheriting their velocity.
        DropBall(victim);
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
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
}
