using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

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
    /// <see cref="BotController"/> to its roster, because in this port clients are not in the engine entity
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
    private bool _triggerHurtEscape; // QC trigger_hurt escape (skill>6): jetpack up / Devastator rocketjump out

    /// <summary>
    /// QC <c>bot_strategytoken == this</c>: only the token holder may run its role (goal rating + routing)
    /// this frame — exactly one bot per frame across the population (bot.qc:786-813). Defaults to true so a
    /// standalone brain (tests/bench, no <see cref="BotPopulation"/>) keeps re-planning on its own clock.
    /// </summary>
    public bool StrategyTokenHeld = true;

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
        using var _thinkScope = XonoticGodot.Common.Diagnostics.Prof.Sample("bot.think");

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
            _nextThink = MathF.Max(now, _nextThink)
                + MathF.Max(0.01f, interval * MathF.Min(14f / (Skill + 14f), 1f));
        }

        // QC aim.qc reads the per-bot skill modifiers (skill + this.bot_aggresskill / bot_aimskill) live each
        // bot_aim call; stamp the bot's modifier columns (parsed from bots.txt by BotPopulation, default 0)
        // onto the aimer so the fire decision + max-fire-deviation carry the bot's aggression/aim personality.
        Aim.AggresSkill = bot.BotAggresSkill;
        Aim.AimSkill = bot.BotAimSkill;

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
                using (XonoticGodot.Common.Diagnostics.Prof.Sample("bot.path")) // [profiling] A* route build
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
        using (XonoticGodot.Common.Diagnostics.Prof.Sample("bot.enemy")) // [profiling] enemy scan + vis traces
            ChooseEnemy(now);

        // 1b) weapon selection: pick the best owned weapon for the enemy's range (QC havocbot_chooseweapon).
        if (now >= _chooseWeaponTime)
        {
            _chooseWeaponTime = now + ChooseWeaponInterval;
            ChooseWeapon(bot.Enemy);
        }

        // 2) strategy (QC havocbot_ai:52-104): ONLY the strategy-token holder may run its role — one goal
        // search per server frame across all bots. The role re-rates when the slow clock expired, the route is
        // empty, or a clearroute forced a re-plan (QC navigation_goalrating_timeout/_force inside the roles).
        if (StrategyTokenHeld)
        {
            using var _stratScope = XonoticGodot.Common.Diagnostics.Prof.Sample("bot.strategy"); // [profiling] flood + role rating
            float strategyInterval = Cvars.FloatOr("bot_ai_strategyinterval", 7f);
            // QC navigation_goalrating_timeout (navigation.qc:44-47): the re-rate is INTERVAL-gated ONLY — there is
            // no "no goal → re-rate now" bypass; a goal-less bot waits out the timer (roaming meanwhile) and the
            // forced path (navigation_goalrating_timeout_force → strategytime = 0) is _strategyForced. The old
            // `!Nav.HasGoal` bypass here re-ran the full seed-flood + role rating EVERY THINK whenever routing
            // failed (e.g. no tracewalk-reachable seed at the bot's spot) — with a few bots that was a
            // 50-350ms/frame bot.strategy melt (the stormkeep "very slow with bots" report).
            if (_strategyForced || now >= _strategyTime)
            {
                _strategyTime = now + strategyInterval;
                _strategyForced = false;
                // QC navigation_markroutes (navigation.qc): before the role rates goals, flood the waypoint graph
                // ONCE from the bot's position so every navigation_routerating reads the real Dijkstra path cost
                // (.wpcost) instead of straight-line distance — bots stop preferring goals that are geometrically
                // near but graph-distant (across a wall/chasm), where GoalRater.Rate would otherwise fall back to
                // straight-line. Token-gated + sim-thread, so the per-frame cost is paid by exactly one bot.
                // Network == null (graphless tests/roaming) keeps the straight-line fallback.
                // (perf 2026-07-03) The flood's entry-seed set is captured and handed to the deferred SetGoal so
                // the tracewalk-heavy seed search runs ONCE per pass — see the _pendingSeeds field doc. The two
                // sub-scopes split a strategy hitch into its halves (seed tracewalks vs the rating loop) so a
                // census names the culprit directly.
                IReadOnlyList<(Waypoint Wp, float Cost)>? seeds;
                using (XonoticGodot.Common.Diagnostics.Prof.Sample("bot.seed")) // [profiling] entry-seed tracewalks + flood
                    seeds = _rater.SeedRoute(Network, bot.Origin);
                using (XonoticGodot.Common.Diagnostics.Prof.Sample("bot.rate")) // [profiling] role goal-rating loop
                    Role(this, _rater);
                bool captured = false;
                if (_rater.HasGoal)
                {
                    var g = _rater.Best;
                    // QC .ignoregoal: skip a goal that danger marked unreachable, for ignoregoal_timeout secs.
                    if (!(g.Target is not null && ReferenceEquals(g.Target, _ignoreGoal) && now < _ignoreGoalTime))
                    {
                        // Capture the rating + seed snapshot; the route build runs at the NEXT think (block 0).
                        _pendingGoal = g;
                        _pendingGoalSet = true;
                        captured = true;
                        _pendingSeeds.Clear();
                        if (seeds is not null)
                            for (int i = 0; i < seeds.Count; i++)
                                _pendingSeeds.Add(seeds[i]);
                    }
                }
                // QC navigation_goalrating_timeout_expire(2) idiom: a pass that produced nothing to build (no
                // rated goal, or the winner is currently ignored and no route stands) retries SOON — on the
                // timer, never per-think (see the gate above). A captured pass re-checks after its route build.
                if (!captured && !Nav.HasGoal)
                    _strategyTime = now + 2f;
            }
            OnStrategyTokenUsed?.Invoke(); // QC bot_strategytoken_taken = true (used this frame)
        }

        // stale goal: the target entity was freed (QC havocbot_ai:106-111).
        if (Nav.GoalEntity is { IsFreed: true })
        {
            Nav.ClearRoute();
            _strategyForced = true;
        }

        // 3) navigation: steer toward current goal -> wish-move + jump/crouch
        bool onGround = bot.OnGround;
        Vector3 move;
        using (XonoticGodot.Common.Diagnostics.Prof.Sample("bot.steer")) // [profiling] waypoint steering + tracewalks
            move = Nav.Steer(bot, Aim.ViewAngles.Y, onGround);

        // 3b) no-progress watchdog (QC havocbot_checkgoaldistance): >0.5s without closing on the goal →
        // drop the route and force a re-rate on the next token hold (covers navigation_unstuck's main value).
        if (Nav.HasGoal && Nav.CheckGoalProgress(bot, now))
        {
            Nav.ClearRoute();
            _strategyForced = true;
        }

        // 3c) danger ahead (QC havocbot_movetogoal:1136-1182 → havocbot_checkdanger): probe the ground under
        // the point we're about to occupy; lava/slime/void/cliff → brake; a trigger_hurt under a high goal →
        // the goal is unreachable (clear + ignore it for bot_ai_ignoregoal_timeout).
        bool dangerBrakeEngaged = false; // QC do_break/evadedanger this frame (forbids bunnyhop, havocbot.qc:1315)
        _triggerHurtEscape = false;      // QC trigger_hurt escape intent (skill>6), set by the danger probe below
        using (XonoticGodot.Common.Diagnostics.Prof.Sample("bot.danger")) // [profiling] danger probes (ground/hazard traces)
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
                // QC evadedanger: steer ALONG the safe edge, not straight back. Probe the danger to our left and
                // right of the flight path; if one side is clear, blend a sideways component toward it so the bot
                // sidesteps the hazard rather than reversing off it. Fall back to the reverse-velocity brake only
                // when both sides are dangerous (a dead-end ledge).
                Vector3 fwdFlat = new(cur.X - bot.Origin.X, cur.Y - bot.Origin.Y, 0f);
                Vector3 fdir = fwdFlat.LengthSquared() > 0f ? QMath.Normalize(fwdFlat) : Vector3.Zero;
                Vector3 sideDir = new(-fdir.Y, fdir.X, 0f); // left of the flight path
                Vector3 probeBase = bot.Origin + Aim.ViewOffset;
                int leftR = BotDanger.CheckDanger(bot, probeBase, probeBase + (fdir + sideDir) * 48f, cur.Z,
                    Nav.Mins, Nav.Maxs, onGround, jumpHeld || Nav.WantJump, moving: true, committed: false);
                int rightR = BotDanger.CheckDanger(bot, probeBase, probeBase + (fdir - sideDir) * 48f, cur.Z,
                    Nav.Mins, Nav.Maxs, onGround, jumpHeld || Nav.WantJump, moving: true, committed: false);
                bool leftSafe = leftR == 0, rightSafe = rightR == 0;
                Vector3 evadeWorld;
                if (leftSafe && !rightSafe) evadeWorld = sideDir - fdir * 0.5f;       // peel left along the edge
                else if (rightSafe && !leftSafe) evadeWorld = -sideDir - fdir * 0.5f; // peel right
                else evadeWorld = -bot.Velocity;                                     // both bad: brake straight back
                move = Nav.WorldToLocalMove(evadeWorld, Aim.ViewAngles.Y);
                if (Nav.GoalEntity is Player) // QC: a player goal past danger is unreachable
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
        using var _aimScope = XonoticGodot.Common.Diagnostics.Prof.Sample("bot.aim"); // [profiling] aim + fire decision + dodge (rest of think)
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
            // combat movement (QC havocbot_dodge + the retreat-when-outgunned behaviour): strafe to dodge
            // incoming fire and, if much weaker than the enemy, back away while still facing it.
            move = CombatMovement(enemy, move, now);
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
        // QC's `dir = normalize(dir + dodge + ...)`. Folded AFTER CombatMovement so it isn't clobbered.
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
                onGround, jumpHeld || Nav.WantJump, moving: true, committed: false);
            if (dr is > 0 and < 4)
            {
                worldDodge = Vector3.Zero; // QC: dodge = '0 0 0' when checkdanger trips
            }
            else
            {
                // QC: a dodge with an upward component may add a jump (skill-scaled chance); a SUPERBOT
                // dodges decisively, so apply the upward dodge jump deterministically here.
                if (worldDodge.Z > 0f) dodgeJump = true;
                Vector3 localDodge = Nav.WorldToLocalMove(worldDodge, Aim.ViewAngles.Y) * dodgeScale;
                Vector3 sum = new(move.X + localDodge.X, move.Y + localDodge.Y, 0f);
                if (sum != Vector3.Zero)
                {
                    sum = QMath.Normalize(sum) * Nav.MaxSpeed;
                    move = new Vector3(sum.X, sum.Y, move.Z); // preserve navigation/combat vertical
                }
            }
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
            _lastAttackTime = now;  // QC last-attack time for the bot_ai_weapon_combo hold window
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

        // QC wr_aim's shot_accurate argument: hitscan shots are accurate by default; a weapon may override (the
        // Devastator relaxes accuracy when its rockets are guidable — devastator.qc:356-357).
        bool accurate = ChosenWeapon?.BotAimAccurate() ?? (shotSpeed <= 0f);
        float maxDev = Aim.MaxFireDeviation(lead, Skill, accurate);
        Aim.AimAt(dir, Bot.Origin, Skill, dt, now, maxDev, hasEnemy: true);

        // line of fire check (QC traceline shotorg -> enemy center): don't shoot a wall/teammate
        var tr = Api.Trace.Trace(Aim.ShotOrigin, Vector3.Zero, Vector3.Zero, enemyCenter, MoveFilter.Normal, Bot);
        bool clear = tr.Fraction >= 1f || ReferenceEquals(tr.Ent, enemy) || (tr.Ent is not null && ShouldAttack(Bot, tr.Ent));
        if (!clear)
            return false;

        return Aim.ShouldFire(now);
    }

    // combat-movement state (QC havocbot_dodge: a strafe direction that flips on a clock).
    private float _dodgeFlipTime;
    private float _dodgeSign = 1f;

    // QC the bot's last-attack time (used by bot_ai_weapon_combo: hold the combo weapon for combo_threshold
    // seconds after firing). Stamped when a fire button is emitted this frame.
    private float _lastAttackTime;

    /// <summary>
    /// Adjust the wish-move while engaging an enemy (QC <c>havocbot_dodge</c> + the retreat behaviour): add a
    /// perpendicular strafe (dodging) whose direction flips on a skill-scaled clock, and bias the move toward
    /// or away from the enemy by health advantage — a healthier bot closes in, an outgunned one backs off.
    /// Returns the blended local-frame move (X forward, Y side), preserving the navigation's vertical/jump.
    /// </summary>
    private Vector3 CombatMovement(Entity enemy, Vector3 navMove, float now)
    {
        // flip the strafe direction periodically (lower skill = slower, more predictable dodging).
        if (now >= _dodgeFlipTime)
        {
            _dodgeFlipTime = now + 0.4f + (float)_rng.NextDouble() * (1.2f - System.Math.Min(Skill, 10f) * 0.1f);
            _dodgeSign = _rng.Next(2) == 0 ? -1f : 1f;
        }

        // health advantage: >0 means we're stronger (close in), <0 weaker (retreat).
        float myHp = Bot.Health + Bot.GetResource(ResourceType.Armor);
        float enHp = enemy.Health + enemy.GetResource(ResourceType.Armor);
        float advantage = (myHp - enHp) / 150f; // QC-style 150 normalizer
        float closeBias = System.Math.Clamp(advantage, -1f, 1f); // +1 charge, -1 flee

        // base combat move: forward = closeBias (toward/away), side = strafe; keep some navigation pull so the
        // bot still drifts toward its goal/items while fighting.
        float fwd = closeBias;
        float side = _dodgeSign * 0.8f;

        var combat = new Vector3(fwd, side, 0f);
        if (combat != Vector3.Zero) combat = QMath.Normalize(combat);

        // blend: mostly combat movement, a little of the navigation move, scaled to run speed. Preserve the
        // navigation's vertical component (jump-up onto ledges) untouched.
        Vector3 navLocal = navMove == Vector3.Zero ? Vector3.Zero : QMath.Normalize(navMove);
        Vector3 blended = QMath.Normalize(combat * 0.75f + new Vector3(navLocal.X, navLocal.Y, 0f) * 0.25f);
        float speed = Nav.MaxSpeed;
        return new Vector3(blended.X * speed, blended.Y * speed, navMove.Z);
    }

    /// <summary>
    /// QC <c>havocbot_dodge</c> (server/bot/default/havocbot/havocbot.qc:1773): scan the danger list
    /// (<c>findchainfloat(bot_dodge, true)</c> over the g_bot_dodge IntrusiveList — here every non-client edict
    /// flagged <see cref="Entity.BotDodge"/>, e.g. an in-flight Blaster bolt or a turret beam) and return a
    /// WORLD-space unit vector pointing away from the most dangerous incoming/nearby hazard, or zero if none.
    /// SUPERBOT-gated in Base ("disabled because too expensive ... re-enable only for bots with high enough
    /// skill"), so non-SUPERBOT bots never specifically dodge bolts (they still strafe via CombatMovement).
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

        // QC bot_ai_weapon_combo: within combo_threshold seconds after our last attack, keep the current
        // (combo-capable splash) weapon for the follow-up combo shot rather than re-picking by range.
        if (enemy is not null && Cvars.Bool("bot_ai_weapon_combo") && ChosenWeapon is { } held
            && (held.SpawnFlags & WeaponFlags.TypeSplash) != 0
            && Now - _lastAttackTime < Cvars.FloatOr("bot_ai_weapon_combo_threshold", 0.4f))
            return; // hold the current weapon for the combo (QC keeps switchweapon on the combo window)

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
            distBand = dist > distFar ? "bot_ai_custom_weapon_priority_far"
                : (dist <= distClose ? "bot_ai_custom_weapon_priority_close"
                : "bot_ai_custom_weapon_priority_mid");
        }

        // QC havocbot_chooseweapon: first owned weapon in the band's priority list; fall back to the old
        // type-bucket / any-owned pick if the bot owns none of the listed weapons (defensive — non-stock lists).
        Weapon? best = PickFromPriority(distBand) ?? PickOwned(false, false);

        if (best is not null)
        {
            ChosenWeapon = best;
            if (Bot.OwnedWeaponSet.Has(best))
                Inventory.SwitchWeapon(Bot, best);
        }
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
    private Weapon? PickFromPriority(string cvarName)
    {
        string list = Api.Cvars.GetString(cvarName);
        if (string.IsNullOrWhiteSpace(list)) return null;
        foreach (string netName in list.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Bot.OwnedWeapons.Contains(netName)) continue;
            Weapon? w = Weapons.ByName(netName);
            if (w is not null) return w; // first owned in priority order wins (QC's ordered list scan)
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
        _chooseEnemyTime = now + (superbot ? 0.1f : EnemyDetectionInterval);

        Vector3 eye = Bot.Origin + Aim.ViewOffset;
        Entity? best = null;
        // QC: non-SUPERBOT rates by squared distance (nearest wins); SUPERBOT by bound(50,hp+armor,250)*dist
        // (prefer the weak/close kill) — a LOWER rating is better in both, so the radius² seeds the ceiling.
        float bestRating = EnemyDetectionRadius * EnemyDetectionRadius;

        foreach (var e in Players())
        {
            if (!ShouldAttack(Bot, e)) continue;
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
            if (rating >= bestRating) continue;

            // PVS pre-filter (QC checkpvs): a target in a non-visible cluster can't possibly be seen, so skip
            // the expensive traceline. Conservative (no false negatives) and a no-op on an unvised map.
            if (!Api.Trace.CheckPvs(eye, center)) continue;

            // require line of sight (QC traceline; trace_ent == it || trace_fraction >= 1)
            var tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, center, MoveFilter.Normal, Bot);
            if (tr.Fraction >= 1f || ReferenceEquals(tr.Ent, e))
            {
                best = e;
                bestRating = rating;
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
