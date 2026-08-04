using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;

namespace VortexArena.Server.Bot;

/// <summary>
/// The per-bot brain — the C# port of server/bot/default/havocbot/havocbot.qc's <c>havocbot_ai</c> think
/// loop, glued to <see cref="BotNavigation"/>, <see cref="BotAim"/> and <see cref="BotRoles"/>.
///
/// Each tick <see cref="Think"/>:
///  1. evaluates the situation — picks the nearest attackable enemy with line of sight (QC havocbot_chooseenemy);
///  2. on a slower strategy clock, runs the current role to rate goals and routes to the best (QC role + navigation_routetogoal);
///  3. aims — at the enemy (with projectile lead + skill error) if one exists, else along the move direction (QC havocbot_aim/bot_aimdir);
///  4. navigates — steers toward the current goal, producing wish-move + jump/crouch (QC havocbot_movetogoal);
///  5. emits a <see cref="MovementInput"/>, sets the attack button when the enemy is in sight+range and the
///     aim is on target, and calls <see cref="Movement.Move"/> to advance the bot one physics tick.
///
/// <see cref="Skill"/> (0..10, or &gt;100 = SUPERBOT) scales aim error, turn rate, reaction interval and
/// aggression — the single knob the QC <c>skill</c> cvar controls.
/// </summary>
public sealed class BotBrain
{
    // ---- tuning (QC autocvar_bot_ai_*, defaults from xonotic-server.cfg) ----
    private const float EnemyDetectionRadius = 10000f;       // bot_ai_enemydetectionradius
    private const float EnemyDetectionInterval = 2f;         // bot_ai_enemydetectioninterval
    private const float ChooseWeaponInterval = 0.5f;         // bot_ai_chooseweaponinterval
    private const float AimInterval = 0.1f;                  // havocbot_aim cadence

    // These three were `private const` while their cvars sat registered and unread, so an operator lowering
    // bot_ai_enemydetectioninterval to make bots re-acquire faster — the natural first move when investigating
    // "bots don't shoot enough" — got no effect at all, and no way to discover why. Read live now; the
    // fallbacks are the shipped defaults, so stock behaviour is unchanged.
    private static float EnemyDetectionRadiusLive => Cvars.FloatOr("bot_ai_enemydetectionradius", EnemyDetectionRadius);
    private static float EnemyDetectionIntervalLive => Cvars.FloatOr("bot_ai_enemydetectioninterval", EnemyDetectionInterval);
    private static float ChooseWeaponIntervalLive => Cvars.FloatOr("bot_ai_chooseweaponinterval", ChooseWeaponInterval);
    private const float DefaultShotSpeed = 0f;               // 0 => treat unknown weapons as hitscan (no lead)

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(Bot_ForbidAttack, this, targ)</c> (server/bot/default/aim.qc:127): a gametype/
    /// mutator veto evaluated at the tail of <see cref="ShouldAttack"/>. Returns true to FORBID the bot from
    /// attacking the target (e.g. Survival forbids attacking a same-status ally so bots don't out their role —
    /// and, with teamkill punishment live, don't suicide). Set by the host on gametype activation; null = no veto.
    /// </summary>
    public static Func<Entity, Entity, bool>? ForbidAttackHook;

    /// <summary>The player entity this brain controls (QC the bot edict).</summary>
    public readonly Player Bot;

    /// <summary>The shared map waypoint graph (QC g_waypoints), or null for graphless roaming.</summary>
    public WaypointNetwork? Network;

    /// <summary>Skill level 0..10 (QC <c>skill</c>); &gt;100 = SUPERBOT (perfect aim/reflexes).</summary>
    public float Skill = 5f;

    /// <summary>The active role (QC <c>.havocbot_role</c>), selectable by gametype via <see cref="BotRoles.ChooseRole"/>.</summary>
    public BotRole Role = BotRoles.RoleGeneric;

    /// <summary>
    /// Supplies the set of players the bot can see/fight (QC the client list). Set by
    /// <see cref="BotPopulation"/> to its roster, because in this port clients are not in the engine entity
    /// table so <c>FindByClass("player")</c> can't find them. When null, falls back to the entity table.
    /// </summary>
    public Func<IEnumerable<Player>>? PlayerProvider;

    /// <summary>The active gametype NetName (QC GetGametype), used to pick objective roles. Set by the controller.</summary>
    public string? GameTypeNetName;

    /// <summary>
    /// The active gametype singleton (QC the registered gametype), so objective roles can read its state
    /// (CTF flag carriers, KH keys, …). Optional — null falls the team roles back to generic DM behaviour.
    /// </summary>
    public GameType? GameType;

    public readonly BotNavigation Nav;
    public readonly BotAim Aim;

    private readonly GoalRater _rater = new();
    private readonly Random _rng;

    // Per-bot wr_aim scratch (QC the actor fields a weapon's wr_aim stamps, e.g. rifle .bot_secondary_riflemooth).
    private BotAimState _botAimState;

    // QC timing fields
    private float _chooseEnemyTime;
    private float _stickEnemyTime;  // QC .havocbot_stickenemy_time (keep current enemy through a brief LOS break)
    private float _strategyTime;
    private float _aimTime;
    private float _chooseWeaponTime;
    private float _nextThink;       // QC .bot_nextthink (think throttle)
    private float _jumpTime;        // QC .bot_jump_time (jump stays held 0.2s for ramp jumps)
    private Entity? _ignoreGoal;    // QC .ignoregoal (a danger-unreachable goal, snubbed for a timeout)
    private float _ignoreGoalTime;  // QC .ignoregoaltime
    private bool _strategyForced;   // QC navigation_goalrating_timeout_force (re-rate on next token hold)

    // (perf 2026-07-03) The strategy pass is SPLIT across two thinks: the token frame runs the flood + role
    // rating and CAPTURES the winning goal + the entry-seed set here; the ROUTE BUILD (SetGoal — goal-side
    // lookup + multi-seed A*) runs at this bot's NEXT think, off the token. One pass used to do all of it in a
    // single tick — the tracewalk-heavy halves stacked into the ~100ms single-tick CPU-LOGIC hitches on a debug
    // build (stormkeep census 2026-07-03). QC runs both in the token frame; a route landing one think (~14ms)
    // later is behaviorally invisible at a seconds-scale strategy cadence. The seeds are COPIED because the
    // network's seed list is a shared scratch another bot's search may overwrite between the two thinks.
    private readonly List<(Waypoint Wp, float Cost)> _pendingSeeds = new();
    private GoalRating _pendingGoal;
    private bool _pendingGoalSet;

    /// <summary>QC WATERLEVEL_SWIMMING — waist-deep and actually swimming (the shore-waypoint push gate).</summary>
    private const int WaterLevelSwimming = 2;

    /// <summary>Scratch for the monster sweep in <see cref="ChooseEnemy"/> (alloc-free FindInRadius).</summary>
    private readonly List<Entity> _monsterScratch = new();

    // Set by BeginGoalRating during the current role invocation: whether the role actually opened a rating
    // pass this token frame (QC: whether navigation_goalrating_start ran), and the entry-seed set its flood
    // produced (handed to the deferred SetGoal). Reset by the strategy block before each role call.
    private bool _ratingRan;
    private IReadOnlyList<(Waypoint Wp, float Cost)>? _frameSeeds;
    private bool _triggerHurtEscape; // QC trigger_hurt escape (skill>6): jetpack up / Devastator rocketjump out

    /// <summary>
    /// QC <c>bot_strategytoken == this</c>: only the token holder may run its role (goal rating + routing)
    /// this frame — exactly one bot per frame across the population (bot.qc:786-813). Defaults to true so a
    /// standalone brain (tests/bench, no <see cref="BotPopulation"/>) keeps re-planning on its own clock.
    /// </summary>
    public bool StrategyTokenHeld = true;

    /// <summary>
    /// The unstuck machinery (QC navigation_unstuck + AI_STATUS_STUCK). See <see cref="BotUnstuck"/>.
    /// </summary>
    public BotUnstuck Unstuck { get; }

    /// <summary>
    /// Claim the server-wide unstuck-scan token (QC <c>bot_waypoint_queue_owner</c>): only one bot at a time
    /// runs the reachability scan, so a room full of wedged bots costs one scan, not N. Set by the population;
    /// the standalone/bench path (no population) always succeeds.
    /// </summary>
    public Func<BotBrain, bool>? TryOwnUnstuckQueueHook;

    /// <summary>Release the unstuck-scan token (QC <c>bot_waypoint_queue_owner = NULL</c>).</summary>
    public Action<BotBrain>? ReleaseUnstuckQueueHook;

    internal bool TryOwnUnstuckQueue() => TryOwnUnstuckQueueHook?.Invoke(this) ?? true;

    internal void ReleaseUnstuckQueue() => ReleaseUnstuckQueueHook?.Invoke(this);

    /// <summary>A draw from this bot's RNG (QC <c>random()</c>), for the unstuck wander re-roll.</summary>
    internal float NextRandom() => (float)_rng.NextDouble();

    /// <summary>
    /// QC <c>point_line_vec(p, l0, ldir)</c> (lib/vector.qh): the perpendicular offset from point
    /// <paramref name="p"/> to the line through <paramref name="lineFrom"/> along <paramref name="lineDir"/> —
    /// i.e. "how far off the centreline am I, and which way back". Returns the vector pointing from the line
    /// TO the point, so negating it steers back onto the path.
    /// </summary>
    private static Vector3 PointLineVec(Vector3 p, Vector3 lineFrom, Vector3 lineDir)
    {
        Vector3 d = QMath.Normalize(lineDir);
        if (d == Vector3.Zero) return Vector3.Zero;
        Vector3 rel = p - lineFrom;
        return rel - d * QMath.Dot(rel, d);
    }

    /// <summary>
    /// QC <c>navigation_goalrating_timeout_expire(t)</c>: push the next strategy pass out by
    /// <paramref name="seconds"/> so an unstuck wander goal gets a moment to play out.
    /// </summary>
    internal void DelayStrategy(float seconds)
    {
        _strategyForced = false;
        _strategyTime = Now + seconds;
    }

    /// <summary>Fired when the holder actually consumes the token (QC <c>bot_strategytoken_taken = true</c>).</summary>
    public Action? OnStrategyTokenUsed;

    /// <summary>QC bot_think:153-157 latch: this bot already auto-readied during the current warmup stage.
    /// Maintained by <see cref="BotPopulation"/> (cleared when warmup ends so a later warmup re-readies).</summary>
    public bool AutoReadied;

    /// <summary>
    /// QC <c>havocbot_role_timeout</c> (sv_assault.qc:473/509): absolute time at which the Assault role
    /// resets. 0 = unset (will be stamped to <c>time + 120</c> on the next role invocation). Stored here
    /// (not in the static role function) because roles are stateless delegates; each bot owns its own timer.
    /// </summary>
    public float AssaultRoleTimeout;

    /// <summary>
    /// QC <c>havocbot_attack_time</c> (sv_assault.qc:457): absolute time until which the Assault role skips
    /// goal re-rating (commit window). Set to <c>time + 2</c> when the bot has PVS to both the approach
    /// waypoint and the objective wall; cleared to 0 on death. When <c>AssaultAttackTime &gt; now</c> the role
    /// returns early (QC <c>if(this.havocbot_attack_time &gt; time) return;</c>) so the bot keeps its current
    /// route rather than immediately re-rating.
    /// </summary>
    public float AssaultAttackTime;

    /// <summary>
    /// QC <c>havocbot_role_timeout</c> (sv_onslaught.qc:1461): absolute time at which the Onslaught offense role
    /// resets (re-evaluates strategy). 0 = unset (stamped to <c>time + 120</c> on the next role invocation). The
    /// defense/assistant roles are no-ops that reset straight back to offense in Base, so offense is the only role.
    /// </summary>
    public float OnslaughtRoleTimeout;

    /// <summary>
    /// QC <c>havocbot_attack_time</c> (sv_onslaught.qc:1470): absolute time until which the Onslaught offense role
    /// skips goal re-rating (commit window). Set to <c>time + 5</c> when the bot has PVS to both an approach
    /// waypoint and the enemy generator, or <c>time + 2</c> for a control-point approach; cleared to 0 on death.
    /// </summary>
    public float OnslaughtAttackTime;

    /// <summary>
    /// QC <c>havocbot_role</c> KH variant (sv_keyhunt.qc): which of the four KH sub-roles this bot is
    /// currently playing. Set by <see cref="BotObjectiveRoles.RoleKeyHunt"/> on role transitions (pick up key →
    /// carrier; drop key → freelancer; timeout → random offense/defense). Initial value of 0 means
    /// "unassigned" — the first <see cref="BotObjectiveRoles.RoleKeyHunt"/> call picks a random starting role.
    /// Maps to the QC <c>bot.havocbot_role</c> pointer (offense / defense / freelancer / carrier).
    /// </summary>
    public KhBotRole KhRole;

    /// <summary>
    /// QC <c>havocbot_role_timeout</c> KH variant: absolute sim time at which the current non-carrier KH role
    /// expires and the bot randomly re-picks offense or defense (QC <c>random() &lt; 0.5</c>).
    /// 0 = unset (stamped to <c>time + random()*10 + 10|20</c> on the next role invocation). Carrier never has
    /// a timeout — it stays carrier until it loses the key.
    /// </summary>
    public float KhRoleTimeout;

    /// <summary>
    /// QC <c>.havocbot_role</c> CTF variant (sv_ctf.qc havocbot_role_ctf_*): which of the six CTF sub-roles
    /// this bot is currently playing. <see cref="CtfBotRole.None"/> = unassigned — the first
    /// <see cref="BotObjectiveRoles.RoleCtf"/> call runs the QC reset_role position balancing.
    /// </summary>
    public CtfBotRole CtfRole;

    /// <summary>QC <c>.havocbot_previous_role</c>: the role Retriever/Escort revert to when their temporary
    /// stint ends (flag returned / timeout).</summary>
    public CtfBotRole CtfPreviousRole;

    /// <summary>QC <c>.havocbot_role_timeout</c> CTF variant: absolute sim time the current CTF role expires
    /// (0 = unset; each role stamps its own duration on first invocation).</summary>
    public float CtfRoleTimeout;

    /// <summary>QC <c>.havocbot_cantfindflag</c> (sv_ctf.qc:1830): carrier watchdog — absolute time by which
    /// the carrier must have found a route home; QC suicides past it (the port clears the route and forces a
    /// re-rate instead — there is no bot-layer suicide path yet).</summary>
    public float CtfCantFindFlagTime;

    /// <summary>QC <c>.havocbot_role</c> Freeze Tag variant (sv_freezetag.qc havocbot_role_ft_offense /
    /// _freeing). <see cref="FtBotRole.None"/> = unassigned — first call picks randomly (QC HavocBot_ChooseRole).</summary>
    public FtBotRole FtRole;

    /// <summary>QC <c>.havocbot_role_timeout</c> FT variant: absolute sim time the current FT role expires.</summary>
    public float FtRoleTimeout;

    /// <summary>
    /// QC the pre-game movement holds (bot_think:80-83 campaign hold + :122-127 countdown): when this returns
    /// true the bot keeps its buttons but emits zero movement. Wired by <see cref="BotPopulation"/> to
    /// <c>time &lt; game_starttime || (g_campaign &amp;&amp; !campaign_bots_may_start)</c>; null = no hold.
    /// </summary>
    public Func<bool>? MovementHold;

    // last input emitted (useful for the host/tests); repeated between throttled thinks (QC's persisted CS movement)
    public MovementInput LastInput { get; private set; }

    /// <summary>The bot's current target (QC <c>.enemy</c>).</summary>
    public Entity? Enemy => Bot.Enemy;

    public BotBrain(Player bot, WaypointNetwork? network = null, float skill = 5f, int seed = 0)
    {
        Bot = bot;
        Network = network;
        Skill = skill;
        _rng = seed == 0 ? new Random() : new Random(seed);
        Aim = new BotAim(seed);
        Nav = new BotNavigation();
        Unstuck = new BotUnstuck(this);

        // sync hull/view from the entity (QC PL_MIN/PL_MAX, view_ofs)
        Nav.Mins = bot.Mins != Vector3.Zero ? bot.Mins : Nav.Mins;
        Nav.Maxs = bot.Maxs != Vector3.Zero ? bot.Maxs : Nav.Maxs;
        Nav.Skill = Skill;         // gates bunnyhop tuning (QC bot_ai_bunnyhop_skilloffset)
        Nav.MaxSpeed = Cvars.MaxSpeed;
        Aim.ViewOffset = bot.ViewOfs != Vector3.Zero ? bot.ViewOfs : Aim.ViewOffset;
        Aim.ViewAngles = bot.Angles;
        Aim.Reset(Now);
    }

    private static float Now => Api.Clock.Time;

    /// <summary>The players this bot is aware of (QC FOREACH_CLIENT), via <see cref="PlayerProvider"/> or the entity table.</summary>
    internal IEnumerable<Entity> Players()
    {
        if (PlayerProvider is not null)
        {
            foreach (var p in PlayerProvider())
                if (!p.IsFreed)
                    yield return p;
            yield break;
        }
        foreach (var e in Api.Entities.FindByClass("player"))
            if (!e.IsFreed)
                yield return e;
    }

    /// <summary>
    /// The LIVE players — QC's <c>FOREACH_CLIENT(IS_PLAYER(it), …)</c>, which is what almost every roster scan
    /// in the roles actually wants. <see cref="Players()"/> is the bare FOREACH_CLIENT and therefore includes
    /// OBSERVERS/spectators, who keep their <c>Team</c> and read <c>IsDead == false</c>
    /// (ClientManager.PutObserverInServer) — so a plain team+alive filter silently accepts them. A follow
    /// spectator is additionally glued to its spectatee's origin, so it wins any "nearest teammate" scan at
    /// distance ~0: that made a bot carrier pass the flag to a non-solid free-fly camera, skewed the CTF
    /// defense/offense census, and routed Freeze-Tag bots to a spectator.
    /// </summary>
    internal IEnumerable<Entity> LivePlayers()
    {
        foreach (Entity e in Players())
            if (e is not Player { IsObserver: true })
                yield return e;
    }

    /// <summary>QC <c>STAT(FROZEN, e) || StatusEffects_active(STATUSEFFECT_Frozen, e)</c> — a frozen player
    /// runs no role and holds no strategy token.</summary>
    internal static bool IsFrozen(Entity e)
        => VortexArena.Common.Gameplay.StatusEffectsCatalog.Frozen is { } f
           && VortexArena.Common.Gameplay.StatusEffectsCatalog.Has(e, f);

    // ---- goal-rating clock API for the roles (QC navigation_goalrating_timeout family) ----

    /// <summary>QC navigation_goalrating_timeout (navigation.qc:44-47): should the role re-rate goals now?
    /// Roles call this each token frame and skip their rating block until it fires.</summary>
    public bool GoalRatingTimedOut => _strategyForced || Now >= _strategyTime;

    /// <summary>QC navigation_goalrating_timeout_force: discard the current goal decision — re-rate on the
    /// next token hold.
    /// <para>ONLY effective from OUTSIDE a rating pass (an event handler, a think block). Called from inside a
    /// role while that role is rating, it is a silent NO-OP: the post-pass stamp below clears
    /// <c>_strategyForced</c> and re-stamps the interval unconditionally whenever the pass ran. A role that
    /// wants a fast retry should simply rate nothing and clear its route — that hits the "no goal captured and
    /// no route" arm, which re-arms in 2s.</para></summary>
    public void ForceGoalRating() => _strategyForced = true;

    /// <summary>QC navigation_goalrating_timeout_expire(seconds): keep the current goal at most
    /// <paramref name="seconds"/> longer (only ever SHORTENS the clock).</summary>
    public void ExpireGoalRating(float seconds)
    {
        if (seconds <= 0f) { _strategyForced = true; return; }
        float t = Now + seconds;
        if (_strategyTime > t) _strategyTime = t;
    }

    /// <summary>
    /// QC navigation_goalrating_start (navigation.qc:1831 — markroutes + reset best): open a rating pass for
    /// this token frame. Floods the waypoint graph from the bot's position ONCE so every subsequent
    /// <see cref="GoalRater.Rate"/> reads the real Dijkstra path cost, and captures the flood's entry-seed set
    /// for the deferred route build. Roles MUST call this before rating and only after checking
    /// <see cref="GoalRatingTimedOut"/> — the brain finishes the pass (goal capture + timeout re-stamp,
    /// QC navigation_goalrating_end + timeout_set) after the role returns.
    /// </summary>
    public void BeginGoalRating(GoalRater rater)
    {
        using (VortexArena.Common.Diagnostics.Prof.Sample("bot.seed")) // [profiling] entry-seed tracewalks + flood
            _frameSeeds = rater.SeedRoute(Network, Bot.Origin);
        rater.Start();
        _ratingRan = true;
    }

    /// <summary>QC IS_MOVABLE (navigation_goalrating_timeout_set): a goal that can move — a player (enemy or
    /// flag/ball/key carrier) or anything currently in motion — re-rates on the shorter movingtarget interval.</summary>
    private static bool IsMovableGoal(Entity? e)
        => e is Player || (e is not null && e.Velocity != Vector3.Zero);

    /// <summary>
    /// QC havocbot_ai:68-100 — the underwater "find the shore" fallback: pick the closest waypoint that leads
    /// UP and out (above the bot, no more than 100qu over eye level), whose top sits in empty (non-water,
    /// non-solid) space, with a clear line from the eye, and push it as a direct route (QC
    /// navigation_pushroute — no A*). Returns true if a shore waypoint was pushed.
    /// </summary>
    public bool TryPushShoreWaypoint(Player bot)
    {
        if (Network is null) return false;
        Waypoint? shore = null;
        float bestD2 = float.MaxValue;
        Vector3 eye = bot.Origin + bot.ViewOfs;
        foreach (var wp in Network.Nodes)
        {
            float d2 = (wp.Origin - bot.Origin).LengthSquared();
            if (d2 > 10000f * 10000f || d2 >= bestD2) continue;
            if (wp.Origin.Z < bot.Origin.Z) continue;                        // must lead UP and out
            if (wp.Origin.Z - bot.Origin.Z - bot.ViewOfs.Z > 100f) continue; // too high to climb out to
            // QC pointcontents(origin + maxs + '0 0 1') != CONTENT_EMPTY → still submerged/solid, skip.
            if (Api.Trace.PointContents(wp.Origin + wp.Maxs + new Vector3(0f, 0f, 1f)) != 0) continue;
            var tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, wp.Center, MoveFilter.Normal, bot);
            if (tr.Fraction < 1f) continue;
            shore = wp;
            bestD2 = d2;
        }
        if (shore is null) return false;
        Nav.PushRoute(shore.Origin); // QC navigation_pushroute(this, newgoal)
        return true;
    }

    /// <summary>
    /// Advance the bot one frame (QC havocbot_ai + bot_think) AND apply the produced move via
    /// <see cref="Movement.Move"/> — the standalone/bench entry point (the live server instead pulls
    /// <see cref="ThinkProduce"/> through the per-tick input path so the SAME tick's physics consumes it).
    /// No-op while the bot is dead (the old standalone behavior, kept for BotNavTests/BotPerfBench).
    /// </summary>
    public void Think(Player bot, float dt)
    {
        if (bot.IsDead)
        {
            Nav.ClearRoute();
            bot.Enemy = null;
            return;
        }
        MovementInput input = ThinkProduce(bot, dt);
        Movement.Move(bot, input);
    }

    /// <summary>
    /// The produce-only think (QC <c>bot_think</c> + <c>havocbot_ai</c>, run from the per-client physics step
    /// like ecs sys_phys_ai): assembles this tick's <see cref="MovementInput"/> WITHOUT stepping physics — the
    /// caller (GameWorld's bot input branch) feeds it to the same-tick movement + weapon drivers exactly like a
    /// human usercmd. Throttled by skill (QC bot_think:71-75); between thinks the last command is repeated
    /// (QC's CS(this).movement/buttons persist on the entity). Also writes the bot's own view angles onto the
    /// entity (bots steer by writing Angles; the server never clobbers a bot's aim from input).
    /// </summary>
    public MovementInput ThinkProduce(Player bot, float dt)
    {
        float now = Now;

        // ---- think throttle (QC bot_think:62-75) ----
        if (now < _nextThink)
        {
            MovementInput repeat = LastInput;
            repeat.FrameTime = dt;
            return repeat; // QC: early return leaves the persisted movement/buttons in force
        }

        // [profiling] the heavy think (past the throttle) — bot AI cost attribution for the hitch bench.
        using var _thinkScope = VortexArena.Common.Diagnostics.Prof.Sample("bot.think");

        // QC bot_aimdir works on this.v_angle, the SINGLE shared aim state. The port forks the bot's aim into
        // BotAim.ViewAngles, which Emit() copies back onto bot.ViewAngles each think — so any EXTERNAL write to
        // bot.ViewAngles between thinks (notably the damage-pipeline bot-aim shake, PlayerDamage's
        // v_angle jitter) must be folded back into the aim state, or it is clobbered and dead. Re-seed
        // Aim.ViewAngles from the entity at think start: a no-op in the steady state (Emit left them equal),
        // it carries the damage shake into the next turn exactly as QC's bot_aimdir reads the jittered v_angle.
        // Guard the pre-first-Emit case where bot.ViewAngles is still zero: keep the spawn-seeded aim then.
        if (bot.ViewAngles != Vector3.Zero)
            Aim.ViewAngles = bot.ViewAngles;

        // QC bot_god → FL_GODMODE re-stamped every think.
        bot.Flags &= ~EntFlags.GodMode;
        if (Cvars.Bool("bot_god"))
            bot.Flags |= EntFlags.GodMode;

        // QC: SUPERBOT thinks at 0.005; others at bot_ai_thinkinterval * min(14/(skill+14), 1), floor 0.01.
        // (The per-bot bot_aiskill modifier rides on Skill here — the port folds bots.txt's "ai" column into it.)
        if (Skill > BotAim.SuperbotSkill)
            _nextThink = MathF.Max(now, _nextThink) + 0.005f;
        else
        {
            float interval = Cvars.FloatOr("bot_ai_thinkinterval", 0.05f);
            // QC bot.qc:75: min(14 / (skill + bot_aiskill + 14), 1) — the per-bot reaction-rate spread. Without
            // it every bot re-aims, re-decides fire and re-steers on the identical clock.
            _nextThink = MathF.Max(now, _nextThink)
                + MathF.Max(0.01f, interval * MathF.Min(14f / (Skill + bot.BotAiSkill + 14f), 1f));
        }

        // QC aim.qc reads the per-bot skill modifiers (skill + this.bot_aggresskill / bot_aimskill) live each
        // bot_aim call; stamp the bot's modifier columns (parsed from bots.txt by BotPopulation, default 0)
        // onto the aimer so the fire decision + max-fire-deviation carry the bot's aggression/aim personality.
        Aim.AggresSkill = bot.BotAggresSkill;
        Aim.AimSkill = bot.BotAimSkill;
        Aim.OffsetSkill = bot.BotOffsetSkill;
        Aim.MouseSkill = bot.BotMouseSkill;
        Aim.ThinkSkill = bot.BotThinkSkill;
        Nav.MoveSkill = bot.BotMoveSkill;

        // ---- button baseline (QC clears all buttons each think; JUMP stays held for ramp jumps) ----
        bool jumpHeld = !bot.IsDead && now < _jumpTime + 0.2f; // QC bot_think:112

        // ---- dead / observer (QC bot_think:129-149 + havocbot_ai:113-119) ----
        // QC havocbot_ai:103 sets bot_strategytoken_taken = true UNCONDITIONALLY when this bot holds the token,
        // BEFORE the dead/frozen early return at :113. So a dead/observer token-holder must STILL consume the
        // token here, or BotPopulation.RotateStrategyToken (gated on _tokenTaken) freezes it on the corpse and
        // the whole population stops re-rating goals (observer = permanently) until this bot respawns.
        if (bot.IsObserver)
        {
            if (StrategyTokenHeld) OnStrategyTokenUsed?.Invoke();
            return Emit(bot, default, jump: false, crouch: false, attack: false, attack2: false, dt);
        }
        // FROZEN (Freeze Tag / the Frozen status effect): QC gates the whole havocbot role call on
        // `!STAT(FROZEN, this) && !StatusEffects_active(STATUSEFFECT_Frozen, this)` (havocbot.qc:52-64) and
        // skips frozen bots when rotating the strategy token (bot.qc:800). A frozen player keeps
        // DeadState == No and Health 1, so without this it fell through to the full think: every frozen bot
        // burned a token hold on a complete goal-rating flood it cannot act on (in a 5v5 with 8 frozen, the
        // two live bots re-planned ~5x less often than Base), and its role timeout kept expiring so it thawed
        // into a randomly re-rolled role. Consume the token like the dead branch — otherwise rotation stalls
        // on the frozen bot and the population stops re-rating entirely.
        if (IsFrozen(bot))
        {
            if (StrategyTokenHeld) OnStrategyTokenUsed?.Invoke();
            return Emit(bot, Vector3.Zero, jump: false, crouch: false, attack: false, attack2: false, dt);
        }
        if (bot.IsDead)
        {
            if (StrategyTokenHeld) OnStrategyTokenUsed?.Invoke();
            Nav.ClearRoute();
            _pendingGoalSet = false; // a captured-but-unbuilt route died with the bot (re-rated after respawn)
            bot.Enemy = null;
            _strategyForced = true; // QC navigation_goalrating_timeout_force while dead
            // QC havocbot_role_ast_offense/defense: clears havocbot_attack_time on death so the bot doesn't
            // remain committed to an assault push after respawning.
            AssaultAttackTime = 0f;
            // QC: jump must be RELEASED for a frame (DEAD_DYING) so PlayerThink sees the keydown edge, then
            // PRESSED while DEAD_DEAD — that's how a bot asks to respawn through the same DEAD_* machine.
            bool jump = bot.DeadState == DeadFlag.Dead;
            return Emit(bot, Vector3.Zero, jump, crouch: false, attack: false, attack2: false, dt);
        }

        // ---- pre-game / campaign movement hold (QC bot_think:80-83 + :122-127) ----
        if (MovementHold?.Invoke() == true)
            return Emit(bot, Vector3.Zero, jumpHeld, crouch: false, attack: false, attack2: false, dt);

        // 0) deferred ROUTE BUILD from the last token frame's rating (see the _pendingSeeds field doc): runs
        // before steering so the fresh route applies this think. The IsFreed guard covers a goal item picked
        // up / entity removed during the one-think gap.
        if (_pendingGoalSet)
        {
            _pendingGoalSet = false;
            GoalRating g = _pendingGoal;
            if (g.Target is not { IsFreed: true })
            {
                using (VortexArena.Common.Diagnostics.Prof.Sample("bot.path")) // [profiling] A* route build
                    Nav.SetGoal(bot.Origin, g.Position, Network, g.Target, bot.OnGround,
                        _pendingSeeds.Count > 0 ? _pendingSeeds : null);
            }
            _pendingSeeds.Clear();
            // QC navigation_goalrating_timeout_expire(2) idiom: a pass whose route build produced NO goal
            // retries SOON — on the timer, never per-think.
            if (!Nav.HasGoal)
                _strategyTime = now + 2f;
        }

        // 1) target selection (throttled; SUPERBOT reacts fast)
        using (VortexArena.Common.Diagnostics.Prof.Sample("bot.enemy")) // [profiling] enemy scan + vis traces
            ChooseEnemy(now);

        // 1b) weapon selection: pick the best owned weapon for the enemy's range (QC havocbot_chooseweapon).
        if (now >= _chooseWeaponTime)
        {
            _chooseWeaponTime = now + ChooseWeaponIntervalLive;
            ChooseWeapon(bot.Enemy);
        }

        // 2) strategy (QC havocbot_ai:52-104): ONLY the strategy-token holder may run its role — one goal
        // search per server frame across all bots. The ROLE runs on EVERY token hold (QC havocbot_ai:64 calls
        // this.havocbot_role(this) each token frame) so role state machines (CTF carrier/retriever/escort,
        // FT offense/freeing, KH, …) react at token cadence; the role itself decides whether to RE-RATE goals
        // via the goal-rating timeout (QC navigation_goalrating_timeout inside each role → GoalRatingTimedOut
        // here). [parity 2026-07-11: the old pass ran the WHOLE role on the 7s clock, so objective role
        // switches — "I just grabbed the flag", "our flag was stolen" — lagged by up to a full interval.]
        // 1c) QC bot_think:150-151 — a STUCK bot runs navigation_unstuck INSTEAD of its role, every think, until
        // it finds somewhere it can actually walk to. This is Base's only escape from "nothing rates", and
        // without it every other rating defect terminates in a bot standing still forever.
        if (Unstuck.IsStuck)
        {
            using (VortexArena.Common.Diagnostics.Prof.Sample("bot.unstuck"))
                Unstuck.Think(bot, Network, Nav);
        }

        // QC navigation_goalrating_start/_end short-circuit entirely while AI_STATUS_STUCK is set
        // (navigation.qc:1833, :1848), so the role does not run and cannot re-raise the flag it is escaping.
        if (StrategyTokenHeld && !Unstuck.IsStuck)
        {
            using var _stratScope = VortexArena.Common.Diagnostics.Prof.Sample("bot.strategy"); // [profiling] flood + role rating
            _ratingRan = false;
            _frameSeeds = null;
            // QC .ignoregoal / .ignoregoaltime: hand them to the rater so the ignored goal is skipped as a
            // CANDIDATE during the pass (roles.qc:140) instead of blanking the pass winner afterwards.
            _rater.IgnoreGoal = _ignoreGoal;
            _rater.IgnoreGoalUntil = _ignoreGoalTime;
            using (VortexArena.Common.Diagnostics.Prof.Sample("bot.rate")) // [profiling] role state machine + goal-rating loop
                Role(this, _rater);
            if (_ratingRan)
            {
                bool captured = false;
                if (_rater.HasGoal)
                {
                    // Capture the rating + seed snapshot; the route build runs at the NEXT think (block 0).
                    _pendingGoal = _rater.Best;
                    _pendingGoalSet = true;
                    captured = true;
                    _pendingSeeds.Clear();
                    if (_frameSeeds is not null)
                        for (int i = 0; i < _frameSeeds.Count; i++)
                            _pendingSeeds.Add(_frameSeeds[i]);
                }
                // QC navigation_goalrating_timeout_set (navigation.qc:20-26): a MOVABLE goal (a player — enemy,
                // flag/ball/key carrier) re-rates on the shorter movingtarget interval; static goals on the
                // full interval. Stamped AFTER the rating pass, exactly like the QC roles do.
                _strategyForced = false;
                _strategyTime = now + (captured && IsMovableGoal(_pendingGoal.Target)
                    ? Cvars.FloatOr("bot_ai_strategyinterval_movingtarget", 5.5f)
                    : Cvars.FloatOr("bot_ai_strategyinterval", 7f));
                // QC navigation_goalrating_timeout_expire(2) idiom: a pass that produced nothing to build (no
                // rated goal, or the winner is currently ignored and no route stands) retries SOON — on the
                // timer, never per-think (the roles gate on GoalRatingTimedOut). A captured pass re-checks
                // after its route build.
                if (!captured && !Nav.HasGoal)
                {
                    _strategyTime = now + 2f;
                    // QC navigation_goalrating_end (navigation.qc:1861-1867): a pass with no goal ENTITY raises
                    // AI_STATUS_STUCK, handing the bot to navigation_unstuck above. Without this the bot just
                    // re-rates from the same spot every 2 s and fails identically, forever.
                    Unstuck.NoteRatingProducedNothing();
                }
                else if (captured)
                {
                    Unstuck.Clear();
                }
            }

            // QC havocbot_ai:68-100: a SWIMMING bot with no goal (and none pending from this pass) pushes the
            // closest "shore" waypoint so it doesn't tread water aimlessly between strategy passes.
            if (!Nav.HasGoal && !_pendingGoalSet && bot.WaterLevel >= WaterLevelSwimming)
                TryPushShoreWaypoint(bot);

            OnStrategyTokenUsed?.Invoke(); // QC bot_strategytoken_taken = true (used this frame)
        }

        // stale goal: the target entity was freed (QC havocbot_ai:106-111).
        if (Nav.GoalEntity is { IsFreed: true })
        {
            Nav.ClearRoute();
            _strategyForced = true;
        }

        // 3) navigation: steer toward current goal -> wish-move + jump/crouch
        // QC havocbot.qc:136-137 sets AI_STATUS_ATTACKING when the bot has an enemy, before movetogoal runs;
        // the bunnyhop gate reads it (havocbot.qc:217).
        Nav.Attacking = bot.Enemy is { IsFreed: false };
        bool onGround = bot.OnGround;
        Vector3 move;
        using (VortexArena.Common.Diagnostics.Prof.Sample("bot.steer")) // [profiling] waypoint steering + tracewalks
            move = Nav.Steer(bot, Aim.ViewAngles.Y, onGround);

        // 3b) no-progress watchdog (QC havocbot_checkgoaldistance, havocbot.qc:1102-1128): >0.5s without closing
        // on the goal → drop the route and force a re-rate on the next token hold. (The terminal "nothing is
        // reachable from here" case is navigation_unstuck's job — see Unstuck above; this watchdog only handles
        // "this particular route stopped working".)
        if (Nav.HasGoal && Nav.CheckGoalProgress(bot, now))
        {
            Nav.ClearRoute();
            _strategyForced = true;
        }

        // 3b-ii) QC havocbot_ai:875-879 + navigation_goalrating_timeout_force: running the goal stack dry means
        // the bot has arrived and needs somewhere new to go. Force the re-rate rather than letting it idle out
        // the 7 s strategy interval standing on the goal it just reached.
        if (!Nav.HasGoal && !_pendingGoalSet && !_strategyForced)
            _strategyForced = true;

        // 3c) danger ahead (QC havocbot_movetogoal:1136-1182 → havocbot_checkdanger): probe the ground under
        // the point we're about to occupy; lava/slime/void/cliff → brake; a trigger_hurt under a high goal →
        // the goal is unreachable (clear + ignore it for bot_ai_ignoregoal_timeout).
        bool dangerBrakeEngaged = false; // QC do_break/evadedanger this frame (forbids bunnyhop, havocbot.qc:1315)
        // QC's two world-space correction vectors, folded into the wish-move at havocbot.qc:1269 rather than
        // replacing it. Both stay zero unless the danger probe below arms them.
        Vector3 dangerBrake = Vector3.Zero;   // QC do_break
        Vector3 evadeDanger = Vector3.Zero;   // QC evadedanger
        _triggerHurtEscape = false;      // QC trigger_hurt escape intent (skill>6), set by the danger probe below
        // QC havocbot.qc:407-409 — havocbot_checkdanger short-circuits while the bot is COMMITTED: mid-jumppad
        // flight, traversing a hardwired link, or launching off a JUMP waypoint. Those are deliberate
        // trajectories through space that a ground-based danger probe reads as "about to fall into a pit", so
        // without the skip the bot brakes in mid-air on exactly the manoeuvres it is supposed to commit to.
        // BotDanger honoured this parameter all along; every call site passed false. (parity report, D-list)
        bool committed = bot.JumpPadCount > 0
            || WaypointNetwork.IsHardwiredLink(Nav.PrevGoalWp, Nav.CurrentWp)
            || (Nav.PrevGoalWp is not null && Nav.PrevGoalWp.HasFlag(WaypointFlags.Jump));
        using (VortexArena.Common.Diagnostics.Prof.Sample("bot.danger")) // [profiling] danger probes (ground/hazard traces)
        if (Nav.Current is Vector3 cur)
        {
            Vector3 flat = new(cur.X - bot.Origin.X, cur.Y - bot.Origin.Y, 0f);
            Vector3 flatdir = flat.LengthSquared() > 0f ? QMath.Normalize(flat) : Vector3.Zero;
            Vector3 offset = bot.Velocity.Length() > 32f ? bot.Velocity * 0.2f : flatdir * 32f;
            Vector3 eye = bot.Origin + Aim.ViewOffset;
            int r = BotDanger.CheckDanger(bot, eye, eye + offset, cur.Z, Nav.Mins, Nav.Maxs,
                onGround, jumpHeld || Nav.WantJump, moving: move != Vector3.Zero, committed: false);
            bool danger = r is > 0 and < 4;
            if (r == 4)
            {
                if (cur.Z > bot.Origin.Z + BotNavigation.JumpStepHeightLive)
                {
                    // goal probably on an upper platform — unreachable (QC: clearroute + ignoregoal).
                    _ignoreGoal = Nav.GoalEntity;
                    _ignoreGoalTime = now + Cvars.FloatOr("bot_ai_ignoregoal_timeout", 3f);
                    Nav.ClearRoute();
                    _strategyForced = true;
                }
                else
                {
                    danger = true;
                    // QC havocbot_movetogoal trigger_hurt escape (skill > 6): a skilled bot tries to get OUT of a
                    // hurt volume instead of only braking. Jetpack straight up if it owns the jetpack; else, if it
                    // owns the Devastator and has the HP to eat the self-splash, rocketjump out (aim down + fire).
                    if (Skill > 6f)
                        _triggerHurtEscape = true;
                }
            }
            if (danger)
            {
                dangerBrakeEngaged = true; // QC: do_break/evadedanger set → bunnyhop forbidden this frame

                // QC havocbot.qc:1249-1268 — evadedanger is a CORRECTION BACK ONTO THE PATH, computed only for
                // a bot already running (>0.8 maxspeed) between two graph waypoints. It measures how far the
                // bot's projected position has drifted off the goalcurrent_prev -> destorg centreline and pushes
                // back toward it, scaled so low-skill bots give hazards a wider berth. Everything else about the
                // danger response is just a status bit.
                //
                // The port used to REPLACE the whole wish-move with a sideways peel on any danger result, with
                // no speed gate: around a railing, a stairwell, or any room with a hole in the floor the bot
                // oscillated between "advance" and "peel" and never traversed. See parity report D6.
                float speed = bot.Velocity.Length();
                if (speed > Nav.MaxSpeed * 0.8f && Nav.HasPrevGoal && Nav.CurrentIsWaypoint)
                {
                    Vector3 p = bot.Origin + bot.Velocity * 0.2f;
                    Vector3 lineFrom = new(Nav.PrevGoalPos.X, Nav.PrevGoalPos.Y, p.Z);
                    Vector3 lineDir = new(cur.X - Nav.PrevGoalPos.X, cur.Y - Nav.PrevGoalPos.Y, 0f);
                    Vector3 off = PointLineVec(p, lineFrom, lineDir);
                    float offLen = off.Length();
                    if (offLen > 20f)
                    {
                        if (offLen > 40f)
                            dangerBrake = QMath.Normalize(bot.Velocity) * -1f; // QC do_break
                        // QC: "Noobs fear dangers a lot and take more distance from them".
                        evadeDanger = QMath.Normalize(off)
                            * QMath.Bound(1f, 3f - (Skill + bot.BotDodgeSkill), 3f);
                    }
                }

                // QC havocbot.qc:1158-1163: only the IMMEDIATE goal being a player makes danger mean
                // "unreachable"; a routed-through waypoint just gets the status bit.
                if (Nav.CurrentGoalEntityIsPlayer)
                {
                    _ignoreGoal = Nav.GoalEntity;
                    _ignoreGoalTime = now + Cvars.FloatOr("bot_ai_ignoregoal_timeout", 3f);
                    Nav.ClearRoute();
                    _strategyForced = true;
                }
            }
        }

        // 4) aim
        bool wantAttack = false;
        bool wantAttack2 = false;
        // QC .dodge_enemy_factor (havocbot.qc:1195, applied at :1252): 1 = full commitment to the path.
        float dodgeEnemyFactor = 1f;
        using var _aimScope = VortexArena.Common.Diagnostics.Prof.Sample("bot.aim"); // [profiling] aim + fire decision + dodge (rest of think)
        Aim.UpdateShotVectors(bot.Origin);
        if (now >= _aimTime)
            _aimTime = now + AimInterval;

        var enemy = bot.Enemy;
        if (enemy is { IsFreed: false })
        {
            wantAttack = AimAndDecideFire(enemy, dt, now);
            // QC per-weapon wr_aim: some weapons route the bot's shot onto the SECONDARY fire by range (e.g. the
            // MachineGun presses its long-range no-spread burst beyond a skill-scaled distance). When the chosen
            // weapon wants secondary at this range, move the already-decided shot from ATCK to ATCK2 (Base wr_aim
            // sets exactly one of the two buttons).
            if (wantAttack && ChosenWeapon is { } w)
            {
                // QC wr_aim re-rolls random() each decision and persists its toggle on the actor (the rifle's
                // bot_secondary_riflemooth); carry that per-bot state + a fresh draw into the weapon's wr_aim.
                _botAimState.Random01 = (float)_rng.NextDouble();
                _botAimState.Actor = bot; // QC wr_aim's `actor` — an ammo-conditional wr_aim (Vaporizer) reads it
                if (w.BotWantsSecondary((enemy.Origin - bot.Origin).Length(), Skill, ref _botAimState))
                {
                    wantAttack = false;
                    wantAttack2 = true;
                }
            }
            // QC wr_aim auto-detonation (devastator.qc:360-450): regardless of whether the bot is taking a shot
            // this frame, a skill >= 2 bot may remote-detonate its in-flight rockets when the predicted splash
            // damage favours it. This runs independently of the primary-fire decision and, when it fires the
            // secondary, suppresses the primary ("don't fire a new shot at the same time", devastator.qc:447-449).
            if (ChosenWeapon is { } dw
                && dw.BotWantsDetonate(bot, new WeaponSlot(0), Skill, Players(), ShouldAttack))
            {
                wantAttack = false;
                wantAttack2 = true;
            }
            // QC havocbot_ai:142 `if (autocvar_bot_nofire || IS_INDEPENDENT_PLAYER(this))`: bot_nofire AND
            // independent-players modes (Race/CTS solo time-trial) suppress the fire button while leaving aim +
            // combat movement intact. The CTS gametype forces _independent_players=1 (Cts.Activate), so a CTS bot
            // runs the course without shooting at other runners.
            if (Cvars.Bool("bot_nofire") || Cvars.Bool("_independent_players"))
            {
                wantAttack = false;
                wantAttack2 = false;
            }
            // QC per-weapon wr_aim total fire suppression: the Port-O-Launch's wr_aim only presses ATCK in the
            // non-secondary balance mode and never in stock (secondary) play (porto.qc:wr_aim), so a porto-holding
            // bot must hold fire entirely. Apply the weapon's veto after every other fire decision.
            if (ChosenWeapon is { } fw && fw.BotForbidsFire((enemy.Origin - bot.Origin).Length(), Skill))
            {
                wantAttack = false;
                wantAttack2 = false;
            }
            // QC havocbot.qc:1262-1266 `dir *= dodge_enemy_factor`: with an enemy in direct sight the whole
            // wish-move is SCALED DOWN (never redirected) so the bot commits less hard to its path while
            // fighting. That is the entire combat-movement effect at normal skill — Base has no combat strafe
            // below SUPERBOT; the random-direction jitter at havocbot.qc:1280 is `skill > 100` only.
            //
            // The port used to substitute an invented strafe/retreat that REPLACED the navigation move whenever
            // an enemy existed, held one direction for up to 0.8 s, and probed nothing on the strafe axis. With
            // the sticky-enemy window that covered most of the time a bot spends near other players, which is
            // exactly when the corner-sticking was observed. See parity report D7.
            var losTr = Api.Trace.Trace(bot.Origin, (enemy.AbsMin + enemy.AbsMax) * 0.5f,
                Vector3.Zero, Vector3.Zero, MoveFilter.NoMonsters, null);
            if (losTr.Ent is Player)
                dodgeEnemyFactor = QMath.Bound(0f, (Skill + bot.BotDodgeSkill) / 7f, 1f);
        }
        else if (Nav.Current is Vector3 goalPos)
        {
            // no enemy: aim along the (flattened) move direction toward the goal (QC bot_aimdir with 0 deviation)
            Vector3 lookDir = goalPos - (bot.Origin + Aim.ViewOffset);
            lookDir.Z = 0f;
            Aim.AimAt(lookDir, bot.Origin, Skill, dt, now, 0f, hasEnemy: false);
        }

        // 4b) idle reload (QC havocbot_ai:181-211): when NOT attacking (no enemy this frame), keep the held
        // weapon topped up so it never runs dry mid-fight, and — for higher-skill bots — pre-rotate to an
        // owned reloadable weapon whose magazine isn't full so it can be reloaded next.
        if (enemy is not { IsFreed: false })
            IdleReload(bot, now);

        // 4c) projectile dodge (QC havocbot_dodge fold in havocbot_movetogoal:1185-1204,1269,1320-1330):
        // SUPERBOT bots specifically swerve away from incoming bolts/hazards on the bot_dodge danger list.
        // Computed in world space, scaled by skill, then blended into the (local-frame) wish-move just like
        // QC's `dir = normalize(dir + dodge + ...)` — composed with the rest in block 4c-i below.
        bool dodgeJump = false;
        Vector3 worldDodge = HavocbotDodge();
        if (worldDodge != Vector3.Zero)
        {
            // QC: dodge *= bound(0, 0.5 + (skill + bot_dodgeskill) * 0.1, 1). bot_dodgeskill defaults 0; SUPERBOT
            // skill (>100) saturates the bound to 1, so the dodge is applied at full strength.
            float dodgeScale = System.Math.Clamp(0.5f + Skill * 0.1f, 0f, 1f);
            // QC: don't dodge into a known danger (lava/cliff) — probe the spot we'd swerve toward.
            Vector3 eye = bot.Origin + Aim.ViewOffset;
            int dr = BotDanger.CheckDanger(bot, eye, eye + worldDodge * 32f, bot.Origin.Z, Nav.Mins, Nav.Maxs,
                onGround, jumpHeld || Nav.WantJump, moving: true, committed: committed);
            if (dr is > 0 and < 4)
            {
                worldDodge = Vector3.Zero; // QC: dodge = '0 0 0' when checkdanger trips
            }
            else
            {
                // QC: a dodge with an upward component may add a jump (skill-scaled chance); a SUPERBOT
                // dodges decisively, so apply the upward dodge jump deterministically here.
                if (worldDodge.Z > 0f) dodgeJump = true;
                worldDodge *= dodgeScale;
            }
        }

        // 4c-i) THE fold (QC havocbot.qc:1252 + :1269):
        //     dir *= dodge_enemy_factor;
        //     dir = normalize(dir + dodge + do_break + evadedanger);
        // One composition of the navigation direction with every world-space correction, then a single
        // projection into the local move frame. The port used to apply each correction separately — the danger
        // response overwrote the move, the combat strafe overwrote that, and the dodge was folded in local space
        // after the fact — so whichever ran last won and the others were silently discarded.
        if (Nav.HasGoal)
        {
            Vector3 composed = Nav.LastWorldDir * dodgeEnemyFactor + worldDodge + dangerBrake + evadeDanger
                + Nav.LastBrake;
            move = Nav.ComposeMove(bot, composed, Aim.ViewAngles.Y, Nav.LastGoalPos);
        }

        // 4c-ii) SUPERBOT combat jitter (QC havocbot_movetogoal:1280-1306). A superbot fighting with nothing
        // else going on OVERRIDES its horizontal wish-move with a fresh random direction, re-rolled on a 0.35s
        // clock and held for the first 0.3s of each window, with a 15% chance of rolling "no direction" so the
        // bot still drifts toward its goal between bursts. Vertical is deliberately left alone (QC: "no random
        // vertical direction"). This runs AFTER the dodge fold and overrides it, exactly as in QC — and QC
        // gates on `!dodge`, reading the POST-checkdanger-veto value, which is what worldDodge holds here.
        //
        // QC's gate is `this.aistatus == AI_STATUS_ATTACKING` — EQUALITY, not a bit test, so the jitter runs
        // only when "enemies in sight" is the bot's ONLY active status: not bunnyhopping (RUNNING), not
        // danger-ahead, not escaping a trigger_hurt, not out of a jumppad/water, not jetpacking, not stuck.
        // The port has no .aistatus bitfield (parity row bot-ai.state.aistatus — the statuses live as ad-hoc
        // per-behaviour state), so the equality is approximated by "has an enemy AND none of the statuses we
        // do model is active". The bunnyhop exclusion is the one that matters: jittering would scrub the speed
        // a bunnyhopping approach just built.
        if (Skill > BotAim.SuperbotSkill
            && enemy is { IsFreed: false }
            && worldDodge == Vector3.Zero
            && !dangerBrakeEngaged
            && !_triggerHurtEscape
            && !Nav.WantBunnyhop)
        {
            // QC: if (!this.randomdirectiontime || this.randomdirectiontime + 0.35 < time)
            if (_randomDirectionTime == 0f || _randomDirectionTime + 0.35f < now)
            {
                // QC: 15% chance to roll "no direction"; otherwise crandom() ∈ [-1,1] scaled by maxspeed on
                // both horizontal axes. (The QC comment above this block says "75% chance"; the code says 85%.
                // Ported to match the CODE, which is what actually runs.)
                _randomDirection = _rng.NextDouble() < 0.15
                    ? Vector3.Zero
                    : new Vector3(Crandom() * Nav.MaxSpeed, Crandom() * Nav.MaxSpeed, 0f);
                _randomDirectionTime = now;
            }
            // QC: if (this.randomdirectiontime + 0.3 >= time && this.randomdirection)
            if (_randomDirectionTime + 0.3f >= now && _randomDirection != Vector3.Zero)
                move = new Vector3(_randomDirection.X, _randomDirection.Y, move.Z);
        }

        // 4d) trigger_hurt escape (QC havocbot_movetogoal, skill > 6): jetpack up if owned (best escape — no
        // self-damage), else rocketjump out with the Devastator when HP can absorb the self-splash.
        bool wantJetpack = false;
        bool escapeJump = false;
        if (_triggerHurtEscape)
        {
            // QC trigger_hurt escape: jetpack straight up if owned (best escape — no self-damage)…
            if ((bot.Items & (int)ItemFlag.Jetpack) != 0)
            {
                wantJetpack = true;
                escapeJump = true; // also press jump so a cl_jetpack_jump host activates the pack
            }
            // …else rocketjump out with the Devastator if HP can absorb the self-splash (QC's HP gate).
            else if (Bot.HasWeapon("devastator")
                     && Bot.Health + Bot.GetResource(ResourceType.Armor) > 80f)
            {
                if (Weapons.ByName("devastator") is { } rl)
                {
                    ChosenWeapon = rl;
                    if (Bot.OwnedWeaponSet.Has(rl)) Inventory.SwitchWeapon(Bot, rl);
                }
                // aim straight down and fire (the blast launches the bot up out of the volume).
                Aim.ViewAngles = new Vector3(80f, Aim.ViewAngles.Y, 0f); // Quake pitch: +down
                wantAttack = true; wantAttack2 = false;
            }
        }

        // 5) assemble the command (the caller's same-tick physics/weapon drivers consume it).
        // QC havocbot.qc:1315: bunnyhop is suppressed when the danger brake (do_break/evadedanger) engaged this
        // frame. Steer keeps its bunnyhop intent in WantBunnyhop (separate from WantJump) precisely so this
        // late, post-danger-probe gate can be applied — a braking bot no longer keeps the jump button held.
        bool wantJump = Nav.WantJump || dodgeJump || escapeJump || (Nav.WantBunnyhop && !dangerBrakeEngaged);
        if (wantJump)
            _jumpTime = now;        // QC bot_jump_time: keep jump held ~0.2s so ramp jumps register
        if (wantAttack || wantAttack2)
        {
            _lastAttackTime = now;              // QC last-attack time
            LastFiredWeapon = ChosenWeapon;     // QC havocbot_ai:157 `lastfiredweapon = m_weapon.m_id`
        }
        return Emit(bot, move, wantJump || jumpHeld, Nav.WantCrouch, wantAttack, wantAttack2, dt, wantJetpack);
    }

    /// <summary>Stamp the bot's view onto the entity and record + return the assembled command.</summary>
    private MovementInput Emit(Player bot, Vector3 move, bool jump, bool crouch, bool attack, bool attack2, float dt, bool jetpack = false)
    {
        var input = new MovementInput
        {
            ViewAngles = Aim.ViewAngles,
            MoveValues = move,
            FrameTime = dt,
            ButtonJump = jump,
            ButtonCrouch = crouch,
            ButtonAttack1 = attack,
            ButtonAttack2 = attack2,
            ButtonJetpack = jetpack,
        };
        LastInput = input;

        // bots steer by writing their OWN angles (GameWorld's input path deliberately skips bots).
        bot.Angles = Aim.ViewAngles;
        bot.ViewAngles = Aim.ViewAngles;
        bot.ViewOfs = Aim.ViewOffset;
        return input;
    }

    /// <summary>
    /// Aim at the current enemy with projectile lead + skill error, and decide whether to fire this frame
    /// (QC bot_aim + the attack-button block of havocbot_ai). Fires only when the enemy is in range, the aim
    /// is within the deviation cone (the fire timer is armed), and there's a clear line of fire.
    /// </summary>
    private bool AimAndDecideFire(Entity enemy, float dt, float now)
    {
        var enemyCenter = (enemy.AbsMin != enemy.AbsMax)
            ? (enemy.AbsMin + enemy.AbsMax) * 0.5f
            : enemy.Origin + enemy.ViewOfs;

        float shotSpeed = CurrentShotSpeed();
        Vector3 lead = Aim.ShotLead(enemyCenter, enemy.Velocity, shotSpeed);
        Vector3 dir = lead - Aim.ShotOrigin;

        // Lobbed weapons (mortar/nade): arc the shot up to account for gravity drop over the flight time
        // (QC findtrajectorywithleading). Adds the vertical compensation onto the straight-line lead dir.
        if (shotSpeed > 0f && CurrentWeaponIsLobbed())
            dir = Aim.BallisticArc(lead, shotSpeed, ProjectileGravity());

        // QC wr_aim's shot_accurate argument: a per-weapon LITERAL, not an inference from hitscan-ness. The
        // weapon base returns true (the majority); Shotgun/OkShotgun/Minelayer/Fireball/Seeker override to
        // false and the Devastator computes it. See Weapon.BotAimAccurate and F4 in the parity report.
        bool accurate = ChosenWeapon?.BotAimAccurate() ?? true;
        float maxDev = Aim.MaxFireDeviation(lead, Skill, accurate);
        Aim.AimAt(dir, Bot.Origin, Skill, dt, now, maxDev, hasEnemy: true);

        // QC bot_aim runs the LOS traceline only on the NON-ballistic arm (aim.qc:395-402 sits in the `else` of
        // the applygravity branch): a lobbed shot is meant to arc over the cover between bot and target, so
        // vetoing it on that cover would silence every mortar bot behind a lip. See F14.
        if (!(shotSpeed > 0f && CurrentWeaponIsLobbed()))
        {
            // QC bot_aim overrides the hit mask for the whole function — dphitcontentsmask = SOLID|BODY|CORPSE
            // (aim.qc:343-344) — so this trace is TRANSPARENT to DPCONTENTS_PLAYERCLIP, exactly like the real
            // shot's W_SetupShot (tracing.qc:41-50). Without the override the bot's default SlideBox mask
            // includes PlayerClip, so every common/clip brush (railings, ledge caps, doorway smoothing — most
            // stock maps) reads as "line of fire blocked" and the bot holds fire at a visible enemy.
            // Save/restore around the trace, the same idiom LagComp.cs:52-53 uses.
            // See planning/bot-ai-parity-2026-08-03.md F3.
            int savedMask = Bot.DpHitContentsMask;
            Bot.DpHitContentsMask = BotAimHitContentsMask;
            TraceResult tr;
            try
            {
                tr = Api.Trace.Trace(Aim.ShotOrigin, Vector3.Zero, Vector3.Zero, enemyCenter, MoveFilter.Normal, Bot);
            }
            finally
            {
                Bot.DpHitContentsMask = savedMask;
            }
            bool clear = tr.Fraction >= 1f || ReferenceEquals(tr.Ent, enemy)
                || (tr.Ent is not null && ShouldAttack(Bot, tr.Ent));
            if (!clear)
                return false;
        }

        return Aim.ShouldFire(now);
    }

    // QC .randomdirection / .randomdirectiontime (havocbot.qh) — the SUPERBOT combat-jitter latch: the rolled
    // local-frame move (X forward, Y side) and the sim time it was rolled at. 0 == never rolled.
    private Vector3 _randomDirection;
    private float _randomDirectionTime;

    /// <summary>QC <c>crandom()</c> — a random float in [-1, 1].</summary>
    private float Crandom() => (float)(_rng.NextDouble() * 2.0 - 1.0);

    // QC the bot's last-attack time (used by bot_ai_weapon_combo: hold the combo weapon for combo_threshold
    // seconds after firing). Stamped when a fire button is emitted this frame.
    /// <summary>
    /// QC <c>bot_aim</c>'s hit-mask override (aim.qc:344): <c>DPCONTENTS_SOLID|BODY|CORPSE</c>. Transparent to
    /// PlayerClip (so the bot's line-of-fire test agrees with where its bullet actually goes — W_SetupShot sets
    /// the identical mask, tracing.qc:41-50) and opaque to corpses.
    /// </summary>
    private const int BotAimHitContentsMask =
        VortexArena.Engine.Collision.SuperContents.Solid
        | VortexArena.Engine.Collision.SuperContents.Body
        | VortexArena.Engine.Collision.SuperContents.Corpse;

    private float _lastAttackTime;

    /// <summary>
    /// QC <c>.lastfiredweapon</c> (havocbot.qc:157): the weapon whose wr_aim actually pressed the trigger.
    /// The combo rule compares this against the held weapon, so a bot that has not fired yet never combos.
    /// Stamped by <see cref="Emit"/> each firing think; public because it is per-bot state a debug overlay
    /// (and the fire-rate tests) read, exactly as the QC field is readable from anywhere.
    /// </summary>
    public Weapon? LastFiredWeapon { get; set; }

    /// <summary>
    /// QC <c>.lastcombotime</c> (havocbot.qc:1535): weapon switching is locked for 1 s after a combo.
    /// Starts at negative infinity, not 0 — the lockout must only apply after a combo has actually happened.
    /// A 0 default makes <c>Now - _lastComboTime &lt; 1</c> true for the first second of the map, which would
    /// freeze every bot on whatever weapon it spawned with.
    /// </summary>
    private float _lastComboTime = float.NegativeInfinity;

    /// <summary>
    /// QC <c>havocbot_dodge</c> (server/bot/default/havocbot/havocbot.qc:1773): scan the danger list
    /// (<c>findchainfloat(bot_dodge, true)</c> over the g_bot_dodge IntrusiveList — here every non-client edict
    /// flagged <see cref="Entity.BotDodge"/>, e.g. an in-flight Blaster bolt or a turret beam) and return a
    /// WORLD-space unit vector pointing away from the most dangerous incoming/nearby hazard, or zero if none.
    /// SUPERBOT-gated in Base ("disabled because too expensive ... re-enable only for bots with high enough
    /// skill"), so non-SUPERBOT bots never specifically dodge bolts (matching Base, which has no combat strafe below SUPERBOT).
    /// The caller scales + folds this into the move (QC: <c>dir = normalize(dir + dodge + ...)</c>).
    /// </summary>
    private Vector3 HavocbotDodge()
    {
        // QC: if (!SUPERBOT) return '0 0 0';  (the port's SUPERBOT gate is Skill > BotAim.SuperbotSkill)
        if (Skill <= BotAim.SuperbotSkill)
            return Vector3.Zero;

        IReadOnlyList<Entity>? all = Api.Entities.All;
        if (all is null)
            return Vector3.Zero; // minimal fakes don't expose the table; nothing to dodge

        Vector3 origin = Bot.Origin;
        float maxspeed = Nav.MaxSpeed; // QC autocvar_sv_maxspeed
        Vector3 dodge = Vector3.Zero;
        float bestDanger = -20f; // QC bestdanger = -20

        for (int i = 0; i < all.Count; i++)
        {
            Entity head = all[i];
            if (head.IsFreed || !head.BotDodge) continue;
            if (ReferenceEquals(head.Owner, Bot)) continue; // QC: head.owner != this (don't dodge own shots)

            float rating = head.BotDodgeRating;
            float vl = head.Velocity.Length();
            if (vl > maxspeed * 0.3f)
            {
                // moving hazard: dodge perpendicular to its flight path.
                Vector3 n = QMath.Normalize(head.Velocity);
                Vector3 v = origin - head.Origin;
                float d = QMath.Dot(v, n); // distance along the flight axis
                if (d > -rating && d < vl * 0.2f + rating)
                {
                    // remove the forward (flight-axis) component, leaving the lateral offset from the path.
                    v -= n * d;
                    float danger = rating - v.Length();
                    if (bestDanger < danger)
                    {
                        bestDanger = danger;
                        dodge = QMath.Normalize(v); // dodge to the side of the object
                    }
                }
            }
            else
            {
                // slow/stationary hazard: back straight away from it.
                Vector3 away = origin - head.Origin;
                float danger = rating - away.Length();
                if (bestDanger < danger)
                {
                    bestDanger = danger;
                    dodge = QMath.Normalize(away);
                }
            }
        }

        return dodge;
    }

    /// <summary>The weapon the bot has chosen to fire (QC <c>.switchweapon</c>), set by <see cref="ChooseWeapon"/>.</summary>
    public Weapon? ChosenWeapon { get; private set; }

    /// <summary>
    /// Pick the best owned weapon for the current engagement range (QC <c>havocbot_chooseweapon</c>): bucket the
    /// engagement into close/mid/far by <c>bot_ai_custom_weapon_priority_distances</c> ("300 850"), then take the
    /// first OWNED weapon from that band's ordered priority list
    /// (<c>bot_ai_custom_weapon_priority_{close,mid,far}</c>) via <see cref="PickFromPriority"/>, falling back to
    /// any owned weapon when the bot owns none of the listed ones. Honors the QC weapon-combo hold
    /// (<c>bot_ai_weapon_combo</c>): within <c>bot_ai_weapon_combo_threshold</c> seconds after firing a
    /// combo-capable splash weapon, keeps it for the follow-up shot rather than re-picking. Sets
    /// <see cref="ChosenWeapon"/> and equips it via <see cref="Inventory"/> so the weapon-frame + shot-speed read use it.
    /// </summary>
    public void ChooseWeapon(Entity? enemy)
    {
        // Resolve the bot's owned weapons to Weapon descriptors (OwnedWeapons is the spawn-filled NetName set;
        // also fold in the WepSet in case the inventory path granted any).
        EnsureWepSetSynced();

        // bot_ai_weapon_rotate <seconds> (port benchmark/demo knob, default 0 = off): instead of the range
        // pick, step through the owned loadout ONE WEAPON AT A TIME on a fixed clock, so a demo/capture run
        // (g_weaponarena loadout + a spectated bot) exercises every carried weapon's fire/effect/render path.
        // The bot's entity slot phases the cycle, so a match shows several different weapons at once while
        // each bot still rotates through all of them. Deliberately overrides the combo hold — rotation must
        // not stick to a favourite. Not a Base behavior; keep 0 outside benchmarks.
        float rotateSecs = Cvars.FloatOr("bot_ai_weapon_rotate", 0f);
        if (rotateSecs > 0f && PickRotating(rotateSecs) is { } rotated)
        {
            ChosenWeapon = rotated;
            if (Bot.OwnedWeaponSet.Has(rotated))
                Inventory.SwitchWeapon(Bot, rotated);
            return;
        }

        // ---- QC havocbot_chooseweapon combo rule (havocbot.qc:1535-1558) ----
        //
        // The combo is a SKIP, not a hold. When the weapon we just fired is a slow one still working off its
        // refire, `combo` is set and each priority loop then SKIPS that weapon (`m_weapon.m_id == w && combo
        // -> continue`, havocbot.qc:1573/1587/1600), so the bot switches to a second weapon and shoots with
        // that during the first one's cooldown. A one-second lockout after a combo (:1535) stops it flapping.
        //
        // The port used to `return` here instead, pinning the bot to the weapon on cooldown — the exact
        // opposite — and because _lastAttackTime is restamped on every firing think, the early return also
        // disabled range-based weapon selection for the whole engagement whenever the held weapon was
        // TypeSplash (which includes Blaster, the spawn weapon). See planning/bot-ai-parity-2026-08-03.md F2.
        if (enemy is not null && Now - _lastComboTime < 1f)
            return; // QC: "Do not change weapon during the next second after a combo"

        bool combo = false;
        if (enemy is not null && ChosenWeapon is { } held && Cvars.Bool("bot_ai_weapon_combo")
            && ReferenceEquals(held, LastFiredWeapon))
        {
            // QC: af = ATTACK_FINISHED(this, weaponentity); combo when it reaches past the skill-scaled
            // threshold, i.e. the held weapon is mid-refire for long enough that a second gun is worth it.
            float af = Bot.WeaponState(new WeaponSlot(0)).AttackFinished;
            float ct = Cvars.FloatOr("bot_ai_weapon_combo_threshold", 0.4f);
            float comboTime = Now + ct * (4f - 0.3f * (Skill + Bot.BotWeaponSkill));
            if (af > comboTime)
            {
                combo = true;
                _lastComboTime = Now;
            }
        }

        string distBand;
        if (enemy is null)
        {
            distBand = "bot_ai_custom_weapon_priority_mid"; // no enemy: a mid-range general-purpose weapon
        }
        else
        {
            float dist = (enemy.Origin - Bot.Origin).Length();
            float distClose = 300f, distFar = 850f; // bot_ai_custom_weapon_priority_distances ships "300 850"
            ReadDistances(ref distClose, ref distFar);
            // QC applies the per-bot range preference as a power-of-two scale on the measured distance
            // (havocbot.qc:1565), so a "sniper" bot reaches for its long-range gun sooner.
            dist = System.Math.Clamp(dist - 200f, 10f, 10000f) * MathF.Pow(2f, Bot.BotRangePreference);
            distBand = dist > distFar ? "bot_ai_custom_weapon_priority_far"
                : (dist <= distClose ? "bot_ai_custom_weapon_priority_close"
                : "bot_ai_custom_weapon_priority_mid");
        }

        // QC havocbot_chooseweapon: first owned weapon WITH AMMO in the band's priority list, skipping the
        // combo weapon and anything mid-reload; fall back to the old type-bucket / any-owned pick if the bot
        // owns none of the listed weapons (defensive — non-stock lists).
        Weapon? best = PickFromPriority(distBand, combo) ?? PickOwned(false, false);

        if (best is not null)
        {
            ChosenWeapon = best;
            if (Bot.OwnedWeaponSet.Has(best))
                Inventory.SwitchWeapon(Bot, best);
        }
    }

    /// <summary>
    /// QC <c>havocbot_chooseweapon_checkreload</c> (havocbot.qc:1472-1491): don't switch INTO a weapon that is
    /// scheduled for reloading while another weapon with ammo is available — the bot would raise it and then
    /// hold a dead trigger for the whole reload. Skill &lt; 5 bots skip the check (QC: so they still reload at
    /// all, and are a bit more stupid in combat).
    /// </summary>
    private bool ChooseWeaponCheckReload(Weapon w)
    {
        if (Skill < 5f) return false;

        var st = Bot.WeaponState(new WeaponSlot(0));
        if (Weapon.GetWeaponLoad(st, w.RegistryId) >= 0)
            return false; // not scheduled for reload

        if (Bot.UnlimitedAmmo || (Bot.Items & (int)ItemFlag.UnlimitedAmmo) != 0)
            return true;
        foreach (string netName in Bot.OwnedWeapons)
        {
            Weapon? other = Weapons.ByName(netName);
            if (other is not null && other != w
                && (other.WrCheckAmmo(Bot, new WeaponSlot(0), false)
                    || other.WrCheckAmmo(Bot, new WeaponSlot(0), true)))
                return true; // other weapon available — leave this one reloading
        }
        return false;
    }

    /// <summary>
    /// QC <c>havocbot_ai</c> idle-reload block (havocbot.qc:181-211), run only while NOT attacking: keep the
    /// bot's guns loaded between fights so a reloadable weapon doesn't run dry mid-combat. Two skill tiers,
    /// matching QC:
    /// <list type="bullet">
    /// <item>skill ≥ 2 — if the currently-held weapon's magazine isn't full, reload it now (drives the same
    /// reload impulse a human's +reload would, via <see cref="WeaponImpulses"/> impulse 20).</item>
    /// <item>skill ≥ 5 — if we're not mid-reload, pre-rotate to any owned reloadable weapon whose stored clip
    /// isn't full, so the held-weapon branch reloads it on the following think (QC sets <c>m_switchweapon</c>).</item>
    /// </list>
    /// </summary>
    private void IdleReload(Player bot, float now)
    {
        if (Skill < 2f) return; // bots below this skill never reload on purpose (QC skill >= 2 gate)

        var slot = new WeaponSlot(0);
        var st = bot.WeaponState(slot);

        // skill >= 2: reload the held weapon if its magazine isn't full. ClipLoad < 0 is QC's "scheduled for
        // reload" sentinel (a reload is already pending), so only act on a genuinely partial clip.
        if (st.ClipLoad >= 0 && st.ClipLoad < st.ClipSize)
        {
            WeaponImpulses.Handle(bot, ReloadImpulse); // QC CS(this).impulse = IMP_weapon_reload.impulse
            return;                                    // QC: don't also pre-rotate while a reload is starting
        }

        // skill >= 5: if we're not reloading a weapon already, switch to any owned reloadable weapon whose
        // stored clip isn't full, so the held-weapon branch reloads it next think (QC havocbot.qc:200-209).
        if (Skill < 5f) return;
        if (st.ClipLoad < 0) return; // already reloading (sentinel) — don't switch away mid-reload

        foreach (string netName in Bot.OwnedWeapons)
        {
            Weapon? w = Weapons.ByName(netName);
            if (w is null || (w.SpawnFlags & WeaponFlags.Reloadable) == 0) continue;
            if (ReferenceEquals(w, ChosenWeapon)) continue; // the held weapon is handled by the skill>=2 branch
            if (Weapon.GetWeaponLoad(st, w.RegistryId) < w.ReloadingAmmo())
            {
                if (Bot.OwnedWeaponSet.Has(w))
                    Inventory.SwitchWeapon(Bot, w);
                break; // QC: rotate to the first under-loaded weapon found, then stop
            }
        }
    }

    private const int ReloadImpulse = 20; // common/impulses/all.qh IMP_weapon_reload

    /// <summary>
    /// QC havocbot_chooseweapon / bot_custom_weapon_priority_setup: pick the first OWNED weapon from the
    /// distance-appropriate priority list (bot_ai_custom_weapon_priority_far/mid/close, shipped as ordered
    /// NetName lists). Walks the list in priority order and returns the first weapon the bot owns, so bots
    /// prefer e.g. Vortex at range and Shotgun up close exactly like stock. Returns null if none of the listed
    /// weapons are owned (the caller then falls back to any owned weapon).
    /// </summary>
    private Weapon? PickFromPriority(string cvarName, bool combo = false)
    {
        string list = Api.Cvars.GetString(cvarName);
        if (string.IsNullOrWhiteSpace(list)) return null;
        foreach (string netName in list.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            Weapon? w = Weapons.ByName(netName);
            if (w is null) continue;

            // QC client_hasweapon(this, w, weaponentity, andammo = true, complain = false) — havocbot.qc:1516,
            // 1571, 1585, 1598. OWNERSHIP IS NOT ENOUGH: an owned-but-dry weapon is skipped so the loop falls
            // through to the next entry. Without this the bot re-equips its empty top-priority gun every
            // ChooseWeapon tick, the fire gate's ammo auto-switch yanks it straight back, and the slot spends
            // its life in drop/raise where every shot is rejected.
            // See planning/bot-ai-parity-2026-08-03.md F1.
            if (!Inventory.ClientHasWeapon(Bot, w, andAmmo: true, complain: false)) continue;

            // QC (havocbot.qc:1573/1587/1600): skip the weapon we just fired when a combo is in play, and skip
            // anything scheduled for reloading.
            if ((combo && ReferenceEquals(w, ChosenWeapon)) || ChooseWeaponCheckReload(w)) continue;

            return w; // first eligible in priority order wins (QC's ordered list scan)
        }
        return null;
    }

    /// <summary>
    /// The <c>bot_ai_weapon_rotate</c> pick: owned weapons in stable registry order, indexed by the rotation
    /// clock plus this bot's entity slot (the per-bot phase). Time is the bot clock, so rotation pauses and
    /// slows with the sim like every other think. Returns null when the bot owns nothing yet (pre-spawn).
    /// </summary>
    private Weapon? PickRotating(float secsPerWeapon)
    {
        int count = 0;
        foreach (Weapon w in Weapons.All)
            if (Bot.OwnedWeaponSet.Has(w))
                count++;
        if (count == 0)
            return null;
        // Bot entity slots are NEGATIVE (server-assigned), so take the magnitude for the phase — the modulo
        // must see only non-negative terms or it returns a negative index and the pick silently misses.
        int idx = ((int)(Now / secsPerWeapon) + System.Math.Abs(Bot.Index)) % count;
        foreach (Weapon w in Weapons.All)
        {
            if (!Bot.OwnedWeaponSet.Has(w))
                continue;
            if (idx-- == 0)
                return w;
        }
        return null;
    }

    /// <summary>Pick the highest-impulse owned weapon matching the type preference (hitscan/splash/any).</summary>
    private Weapon? PickOwned(bool wantHitscan, bool wantSplash)
    {
        Weapon? best = null;
        foreach (string netName in Bot.OwnedWeapons)
        {
            Weapon? w = Weapons.ByName(netName);
            if (w is null) continue;
            bool isHitscan = (w.SpawnFlags & WeaponFlags.TypeHitscan) != 0;
            bool isSplash = (w.SpawnFlags & WeaponFlags.TypeSplash) != 0;
            if (wantHitscan && !isHitscan) continue;
            if (wantSplash && !isSplash) continue;
            if (best is null || w.Impulse > best.Impulse) best = w;
        }
        return best;
    }

    /// <summary>Make sure the WepSet mirrors the spawn-filled OwnedWeapons NetName set (so Inventory works).</summary>
    private void EnsureWepSetSynced()
    {
        foreach (string netName in Bot.OwnedWeapons)
        {
            Weapon? w = Weapons.ByName(netName);
            if (w is not null && !Bot.OwnedWeaponSet.Has(w))
                Bot.OwnedWeaponSet.Add(w);
        }
    }

    private static void ReadDistances(ref float close, ref float far)
    {
        string s = Api.Cvars.GetString("bot_ai_custom_weapon_priority_distances");
        if (string.IsNullOrWhiteSpace(s)) return;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
        {
            close = MathF.Min(a, b);
            far = MathF.Max(a, b);
        }
    }

    /// <summary>
    /// Projectile speed of the bot's chosen weapon (QC W_WeaponSpeedFactor * WEP_CVAR speed): read from the
    /// weapon's primary-fire balance cvar. Returns 0 (hitscan, no lead) for a hitscan weapon or an unknown
    /// projectile speed. Drives the aim lead in <see cref="AimAndDecideFire"/>.
    /// </summary>
    private float CurrentShotSpeed()
    {
        Weapon? w = ChosenWeapon;
        if (w is null)
            return DefaultShotSpeed;
        if ((w.SpawnFlags & WeaponFlags.TypeHitscan) != 0)
            return 0f; // hitscan: aim straight at the target
        float s = Api.Cvars.GetFloat(BalanceNames(w).Speed);
        // QC wr_aim may override the speed the bot leads by (the Devastator leads as if its rocket flew much
        // faster, "simulating rocket guide" — devastator.qc:351-355). A weapon that fires at a different speed on
        // secondary (Fireball: 900 vs 1200) reads ctx.SecondaryToggle to pick the right lead. Default falls through
        // to the 1-arg overload (no ctx) so existing weapons need not change.
        return w.BotAimShotSpeed(s > 0f ? s : DefaultShotSpeed, ref _botAimState);
    }

    /// <summary>True if the chosen weapon lobs under gravity (mortar/nade), so the aim should arc the shot.</summary>
    private bool CurrentWeaponIsLobbed()
    {
        Weapon? w = ChosenWeapon;
        if (w is null) return false;
        // grenade/mortar-style splash weapons that aren't hitscan and have a gravity cvar set.
        if ((w.SpawnFlags & WeaponFlags.TypeHitscan) != 0) return false;
        // QC wr_aim's gravity arg: a weapon may force the ballistic arc even though it has no per-weapon gravity
        // cvar (the Mortar grenade lobs under world sv_gravity), via BotAimLobbed; else infer from the cvar.
        if (w.BotAimLobbed is bool lobbed) return lobbed;
        return Api.Cvars.GetFloat(BalanceNames(w).Gravity) > 0f;
    }

    /// <summary>The gravity acting on the chosen weapon's projectile (QC its gravity factor × sv_gravity).</summary>
    private float ProjectileGravity()
    {
        Weapon? w = ChosenWeapon;
        float gravFactor = w is null ? 1f : Api.Cvars.GetFloat(BalanceNames(w).Gravity);
        if (gravFactor <= 0f) gravFactor = 1f;
        return gravFactor * Cvars.Gravity;
    }

    // Cached per-weapon balance-cvar names: the aim helpers above run inside EVERY bot think, and the
    // $"g_balance_{netname}_primary_*" interpolations allocated a fresh string per call (a steady per-bot
    // per-tick churn). Weapon registry names are fixed after boot, so the names are interned once. Shared by
    // all brains; sim-thread only (single-threaded by contract, like the Prof scopes).
    private static readonly Dictionary<string, (string Speed, string Gravity)> _balanceNameCache =
        new(StringComparer.Ordinal);

    private static (string Speed, string Gravity) BalanceNames(Weapon w)
    {
        if (!_balanceNameCache.TryGetValue(w.NetName, out (string Speed, string Gravity) names))
            _balanceNameCache[w.NetName] = names =
                ($"g_balance_{w.NetName}_primary_speed", $"g_balance_{w.NetName}_primary_gravity");
        return names;
    }

    /// <summary>
    /// Pick the nearest attackable enemy with line of sight (QC havocbot_chooseenemy). Keeps the current
    /// enemy if it's still valid; otherwise scans players on the detection clock. Stores the result on
    /// <c>Bot.Enemy</c>.
    /// </summary>
    private void ChooseEnemy(float now)
    {
        bool superbot = Skill > BotAim.SuperbotSkill;

        var current = Bot.Enemy;
        if (current is { IsFreed: false })
        {
            if (!ShouldAttack(Bot, current))
            {
                // enemy died / became invalid → drop it and re-scan now (QC havocbot_chooseenemy:1342-1349).
                Bot.Enemy = null;
                _chooseEnemyTime = now;
            }
            else if (_stickEnemyTime != 0f && now < _stickEnemyTime)
            {
                // QC sticky-enemy window (bot_ai_enemydetectioninterval_stickingtoenemy, default 4): keep
                // tracking the current enemy through a brief LOS break (rounding a pillar/corner) as long as
                // it's still close + still traces clear, so the bot doesn't instantly forget a target that
                // ducked behind cover for a moment (havocbot_chooseenemy:1350-1366).
                Vector3 eyeS = Bot.Origin + Aim.ViewOffset;
                var targPos = (current.AbsMin != current.AbsMax)
                    ? (current.AbsMin + current.AbsMax) * 0.5f : current.Origin + current.ViewOfs;
                var trS = Api.Trace.Trace(eyeS, Vector3.Zero, Vector3.Zero, targPos, MoveFilter.Normal, Bot);
                if ((trS.Fraction >= 1f || ReferenceEquals(trS.Ent, current))
                    && (targPos - Bot.Origin).Length() < 1000f)
                {
                    // remain on this enemy for a short window (QC: chooseenemy_finished = time + 0.5).
                    _chooseEnemyTime = now + 0.5f;
                    return;
                }
                _stickEnemyTime = 0f; // stop preferring this enemy
            }
        }
        else
        {
            Bot.Enemy = null;
        }

        if (now < _chooseEnemyTime)
            return;
        _chooseEnemyTime = now + (superbot ? 0.1f : EnemyDetectionIntervalLive);

        Vector3 eye = Bot.Origin + Aim.ViewOffset;
        Entity? best = null;
        // QC: non-SUPERBOT rates by squared distance (nearest wins); SUPERBOT by bound(50,hp+armor,250)*dist
        // (prefer the weak/close kill) — a LOWER rating is better in both, so the radius² seeds the ceiling.
        float bestRating = EnemyDetectionRadiusLive * EnemyDetectionRadiusLive;

        void Consider(Entity e)
        {
            if (!ShouldAttack(Bot, e)) return;
            var center = (e.AbsMin != e.AbsMax) ? (e.AbsMin + e.AbsMax) * 0.5f : e.Origin + e.ViewOfs;
            float d2 = (center - eye).LengthSquared();

            // QC SUPERBOT target rating: account for the target's health+armor so a skilled bot prefers
            // finishing a weak/close enemy over a healthy/far one (havocbot_chooseenemy:1409-1426).
            float rating = d2;
            if (superbot)
            {
                float hp = e.Health + e.GetResource(ResourceType.Armor);
                rating = QMath.Bound(50f, hp, 250f) * d2;
            }
            if (rating >= bestRating) return;

            // PVS pre-filter (QC checkpvs): a target in a non-visible cluster can't possibly be seen, so skip
            // the expensive traceline. Conservative (no false negatives) and a no-op on an unvised map.
            if (!Api.Trace.CheckPvs(eye, center)) return;

            // require line of sight (QC traceline; trace_ent == it || trace_fraction >= 1)
            var tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, center, MoveFilter.Normal, Bot);
            if (tr.Fraction >= 1f || ReferenceEquals(tr.Ent, e))
            {
                best = e;
                bestRating = rating;
            }
        }

        foreach (var e in Players())
            Consider(e);

        // QC havocbot_chooseenemy walks g_bot_targets — players AND live monsters/turrets (anything spawned
        // with .bot_attack; sv_monsters.qc:1449 pushes every monster). The port's roster is players-only, so
        // sweep the entity table for FL_MONSTER entities too — without this bots are blind to Invasion waves
        // and any map monsters. (No turret system exists in the port yet.)
        if (Api.Services is not null)
        {
            Api.Entities.FindInRadius(Bot.Origin, EnemyDetectionRadiusLive, _monsterScratch);
            for (int i = 0; i < _monsterScratch.Count; i++)
            {
                Entity m = _monsterScratch[i];
                if (m.IsFreed || (m.Flags & EntFlags.Monster) == 0) continue;
                Consider(m);
            }
        }

        Bot.Enemy = best;
        // QC: arm the sticky-enemy window so a target acquired now is tracked through a brief LOS break.
        _stickEnemyTime = best is not null
            ? now + Cvars.FloatOr("bot_ai_enemydetectioninterval_stickingtoenemy", 4f)
            : 0f;
    }

    /// <summary>
    /// Should <paramref name="self"/> attack <paramref name="targ"/>? — a faithful port of QC
    /// <c>bot_shouldattack</c> (server/bot/default/aim.qc): filters self and teammates (in a team game), the
    /// neutral team in a team game (no FFA targets), bots when <c>bot_ignore_bots</c> is set, and
    /// dead/frozen/non-damageable/notarget/alpha-invisible targets. A target "in chat" is spared unless
    /// <c>bot_typefrag</c> is on. Items are never attacked.
    /// </summary>
    public static bool ShouldAttack(Entity self, Entity targ)
    {
        bool teamplay = Cvars.Teamplay;

        // same team: never attack self; in a team game never attack a real teammate.
        if (targ.Team == self.Team)
        {
            if (ReferenceEquals(targ, self)) return false;
            if (teamplay && targ.Team != 0f) return false;
        }

        if (teamplay)
        {
            // in a team game, only attack players that have a (different) team — no neutral targets.
            if (targ.Team == 0f) return false;
        }
        else if (Cvars.Bool("bot_ignore_bots") && targ is Player { IsBot: true })
        {
            return false; // FFA + ignore-bots: leave other bots alone
        }

        if (targ.IsFreed) return false;
        if (targ.TakeDamage == DamageMode.No) return false;
        if (targ.DeadState != DeadFlag.No) return false;
        // QC bot_shouldattack (aim.qc:120): spare a player who is TYPING in chat (PHYS_INPUT_BUTTON_CHAT)
        // unless bot_typefrag allows typefragging (Base default 0). Entity.ButtonChat is the live input
        // mirror — the same field the monster typefrag spare reads (MonsterAI). (playtest #35b)
        if (targ.ButtonChat && !Cvars.Bool("bot_typefrag")) return false;
        if ((targ.Flags & EntFlags.NoTarget) != 0) return false;
        if ((targ.Flags & EntFlags.Item) != 0) return false; // only players/monsters

        // frozen targets (QC STAT(FROZEN)) aren't worth shooting.
        if (StatusEffectsCatalog.Frozen is { } fr && StatusEffectsCatalog.Has(targ, fr)) return false;

        // QC bot_shouldattack tail (aim.qc:127): a gametype/mutator may forbid the attack (e.g. Survival's
        // Bot_ForbidAttack vetoes same-status allies). Evaluated last so it only narrows the base eligibility.
        if (ForbidAttackHook is not null && ForbidAttackHook(self, targ)) return false;
        // The Common-side Bot_ForbidAttack mutator chain (QC MUTATOR_CALLHOOK(Bot_ForbidAttack, this, targ)):
        // the powerups mutator forbids attacking a player holding Invisibility (bot stealth).
        if (MutatorHooks.FireBotForbidAttack(self, targ)) return false;

        return true;
    }
}
