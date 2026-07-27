using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// Per-bot navigation: the goal stack and path follower — the C# port of the navigation half of
/// server/bot/default/navigation.qc (clearroute/pushroute/poproute, routetogoal) and the steering core
/// of havocbot_movetogoal in havocbot.qc.
///
/// Model: <see cref="SetGoal"/> plans a path (waypoint A* + final goal) and pushes it onto the goal
/// stack (QC <c>goalcurrent</c>/<c>goalstack01..31</c>, here a plain <see cref="List{T}"/> used as a
/// stack with the front = current goal). Each frame <see cref="Steer"/> pops goals the bot has reached,
/// produces a forward/side <c>MoveValues</c> vector toward the current goal, and decides jump/crouch by
/// probing ahead with <see cref="Services.Api"/>.Trace (QC tracebox obstacle/step/fall checks).
///
/// One instance per bot, owned by <see cref="BotBrain"/>.
/// </summary>
public sealed class BotNavigation
{
    private const int MaxGoals = 32;        // QC goalstack depth (goalcurrent + goalstack01..31)

    // Waypoint types whose first node must NOT be skipped by the path-optimization shortcut: they carry
    // traversal semantics Steer needs (jump/crouch the link, climb a ladder, enter a teleporter trigger) or are
    // hand-authored special links (QC WPFLAGMASK_NORELINK = TELEPORT|LADDER|JUMP|CUSTOM_JP|SUPPORT, extended here
    // with CROUCH since the port encodes "crouch this link" on the node).
    private const WaypointFlags WaypointFlagsNoSkip =
        WaypointFlags.Teleport | WaypointFlags.Ladder | WaypointFlags.Jump
        | WaypointFlags.CustomJp | WaypointFlags.Support | WaypointFlags.Crouch;

    private const float GoalReachedXY = 24f; // horizontal "touched the waypoint" radius
    private const float GoalReachedZ = 48f;  // vertical tolerance
    public const float StepHeight = 34f;    // QC stepheightvec.z default (sv_stepheight) — walkable step
    public const float JumpStepHeight = 48f; // QC jumpstepheightvec.z default — reachable with a jump (brain danger check reads it)

    // ---- live step/jump-reach heights (QC bot_calculate_stepheightvec, bot.qc:615-621) ----
    // QC derives these from sv_stepheight/sv_jumpvelocity/sv_gravity at init and on cvar change, so a map or
    // server that retunes the physics cvars gets matching bot jump reach. The const fields above stay as the
    // stock defaults (they're the public symbols other files reference); Steer reads these live properties.
    //   stepheightvec.z   = sv_stepheight
    //   jumpheight_vec.z  = sv_jumpvelocity^2 / (2 * sv_gravity)        (apparent jump apex)
    //   jumpstepheightvec.z = stepheight + jumpheight_vec.z * 0.85       (reduced "easy jump" reach)

    /// <summary>QC stepheightvec.z — sv_stepheight (walkable step), read live so non-default physics adjusts it.</summary>
    private static float StepHeightLive => Cvars.FloatOr("sv_stepheight", StepHeight);

    /// <summary>QC jumpheight_vec.z — the apparent jump apex sv_jumpvelocity^2/(2*sv_gravity).</summary>
    private static float JumpHeightApex
    {
        get
        {
            float jv = Cvars.JumpVelocity;
            float g = Cvars.Gravity;
            return g > 0f ? (jv * jv) / (2f * g) : 0f;
        }
    }

    /// <summary>
    /// QC jumpstepheightvec.z (bot_calculate_stepheightvec, bot.qc:619): <c>stepheightvec + jumpheight_vec * 0.85</c>
    /// — the "easy jump" reach (the apex reduced a bit so the bot commits jumps it can actually clear). ≈70 @ stock
    /// (34 + 84.5*0.85). Read live so non-default sv_stepheight/sv_jumpvelocity/sv_gravity adjust it. This is the
    /// height QC's havocbot_movetogoal:1146 compares a high goal against (goal above this ⇒ on an upper platform),
    /// which the brain's danger check uses; the public <see cref="JumpStepHeight"/> const stays as the stock default
    /// for any caller that wants a compile-time symbol.
    /// </summary>
    public static float JumpStepHeightLive => StepHeightLive + JumpHeightApex * 0.85f;

    /// <summary>One entry on the goal stack: a world point plus the waypoint flags that govern how to
    /// traverse it (jump/crouch/teleport/ladder) and the source waypoint (for box-volume reach tests).</summary>
    private readonly struct Goal
    {
        public readonly Vector3 Pos;
        public readonly WaypointFlags Flags;
        public readonly Waypoint? Wp;
        public Goal(Vector3 pos, WaypointFlags flags = WaypointFlags.None, Waypoint? wp = null)
        {
            Pos = pos; Flags = flags; Wp = wp;
        }
    }

    /// <summary>The goal stack, front (index 0) = current goal (QC goalcurrent).</summary>
    private readonly List<Goal> _goals = new(MaxGoals);

    /// <summary>The final target entity, if the goal is an item/enemy (QC <c>.goalentity</c>). May be null.</summary>
    public Entity? GoalEntity;

    /// <summary>Sim time the bot last used a teleporter/jumppad goal (QC <c>.lastteleporttime</c>), to avoid re-triggering.</summary>
    public float LastTeleportTime;

    /// <summary>Player bounding box (QC PL_MIN/PL_MAX) used for trace boxes. Set by the brain on spawn.</summary>
    public Vector3 Mins = new(-16f, -16f, -24f);
    public Vector3 Maxs = new(16f, 16f, 45f);

    /// <summary>QC autocvar_sv_maxspeed — magnitude of emitted wish-move.</summary>
    public float MaxSpeed = 320f;

    /// <summary>Set true while steering when an obstacle/up-step needs a jump (QC PHYS_INPUT_BUTTON_JUMP).</summary>
    public bool WantJump { get; private set; }

    /// <summary>Force the jump intent for this frame (QC trigger_hurt jetpack escape sets +jump alongside the
    /// jetpack so a cl_jetpack_jump host activates the pack). Used by <see cref="BotBrain"/>; Steer overwrites
    /// WantJump next frame as usual.</summary>
    public void ForceJump() => WantJump = true;

    /// <summary>Set true while steering when traversing a crouch waypoint (QC PHYS_INPUT_BUTTON_CROUCH).</summary>
    public bool WantCrouch { get; private set; }

    /// <summary>
    /// QC <c>havocbot_bunnyhop</c> wants a jump this frame to maintain run speed toward a far goal. Kept
    /// SEPARATE from <see cref="WantJump"/> because Base only bunnyhops when <c>!evadedanger &amp;&amp; !do_break</c>
    /// (havocbot.qc:1315): the per-frame danger brake runs in <see cref="BotBrain"/> AFTER <see cref="Steer"/>,
    /// so the brain ANDs this with "no danger brake this frame" before folding it into the jump button.
    /// </summary>
    public bool WantBunnyhop { get; private set; }

    /// <summary>The current goal point, or null if the stack is empty (QC <c>.goalcurrent</c>).</summary>
    public Vector3? Current => _goals.Count > 0 ? _goals[0].Pos : null;

    public bool HasGoal => _goals.Count > 0;

    /// <summary>Clear the route (QC navigation_clearroute).</summary>
    public void ClearRoute()
    {
        _goals.Clear();
        GoalEntity = null;
        LastTeleportTime = 0f;
        ResetGoalProgress();
    }

    // ---- no-progress detection (QC havocbot_checkgoaldistance, havocbot.qc:344-368) ----
    private float _goalDistZ;
    private float _goalDist2d;
    private float _goalDistTime;

    private void ResetGoalProgress()
    {
        _goalDistZ = float.MaxValue;
        _goalDist2d = float.MaxValue;
        _goalDistTime = 0f;
    }

    /// <summary>
    /// QC <c>havocbot_checkgoaldistance</c>: returns true when the bot has spent &gt; 0.5 s without getting any
    /// closer to the current goal (both vertically and horizontally) — the stuck signal that makes the brain
    /// clear the route and force a goal re-rate (QC's caller re-verifies with tracewalk first; the port goes
    /// straight to the clearroute, trading a possible early re-plan for simplicity). Distances shrink-track
    /// like QC (each improvement re-arms the watchdog 10qu tighter, floored at 20).
    /// </summary>
    public bool CheckGoalProgress(Entity bot, float now)
    {
        if (_goals.Count == 0)
            return false;
        Vector3 gco = _goals[0].Pos;
        float currZ = MathF.Max(20f, MathF.Abs(bot.Origin.Z - gco.Z));
        float curr2d = MathF.Max(20f, new Vector2(bot.Origin.X - gco.X, bot.Origin.Y - gco.Y).Length());
        if (currZ >= _goalDistZ && curr2d >= _goalDist2d)
        {
            if (_goalDistTime == 0f)
                _goalDistTime = now;
            else if (now - _goalDistTime > 0.5f)
                return true;
        }
        else
        {
            // reduce a little so it works even with very small approaches to the goal (QC comment).
            _goalDistZ = MathF.Max(20f, currZ - 10f);
            _goalDist2d = MathF.Max(20f, curr2d - 10f);
            _goalDistTime = 0f;
        }
        return false;
    }

    /// <summary>
    /// Project a world-frame direction into the bot's local move frame (the same yaw-only basis
    /// <see cref="Steer"/> uses — QC makevectors(v_angle.y * '0 1 0')), scaled to <see cref="MaxSpeed"/>.
    /// Used by the brain's danger brake (QC <c>do_break = normalize(velocity) * -1</c>).
    /// </summary>
    public Vector3 WorldToLocalMove(Vector3 worldDir, float viewYaw)
    {
        if (worldDir == Vector3.Zero)
            return Vector3.Zero;
        Vector3 dir = QMath.Normalize(worldDir);
        QMath.AngleVectors(new Vector3(0f, viewYaw, 0f), out var forward, out var right, out var up);
        return new Vector3(QMath.Dot(dir, forward), QMath.Dot(dir, right), QMath.Dot(dir, up)) * MaxSpeed;
    }

    /// <summary>Push a goal point to the front of the stack (QC navigation_pushroute).</summary>
    public void PushRoute(Vector3 goal) => PushRoute(new Goal(goal));

    private void PushRoute(Goal goal)
    {
        if (_goals.Count >= MaxGoals)
            _goals.RemoveAt(_goals.Count - 1); // drop the farthest; bot will re-plan after the first 31 steps
        _goals.Insert(0, goal);
    }

    /// <summary>Pop the current goal (QC navigation_poproute), e.g. when a waypoint is reached.</summary>
    public void PopRoute()
    {
        if (_goals.Count > 0)
            _goals.RemoveAt(0);
        ResetGoalProgress(); // a fresh goal re-arms the no-progress watchdog (QC resets goalcurrent_distance_*)
    }

    /// <summary>
    /// Plan a route from <paramref name="origin"/> to <paramref name="goalPos"/> over the waypoint network
    /// and load it onto the goal stack (QC navigation_routetogoal). If origin and goal are directly
    /// reachable (or there's no network), pushes just the goal. The final <paramref name="goalEntity"/>
    /// (item/enemy) is remembered as <see cref="GoalEntity"/>.
    ///
    /// <paramref name="onGround"/> mirrors Base's navigation_markroutes_nearestwaypoints on-ground-vs-air seed
    /// radius growth (on-ground 750/50000, air 500/1500). It is threaded through to the network's nearest-seed
    /// search (<see cref="WaypointNetwork.NearestSeeds"/>), which seeds the multi-seed A*; the single-nearest
    /// start is used only as a fallback when no seed is reachable. Defaults to true so non-brain callers (tests)
    /// keep compiling.
    /// </summary>
    public void SetGoal(Vector3 origin, Vector3 goalPos, WaypointNetwork? net, Entity? goalEntity = null, bool onGround = true,
        IReadOnlyList<(Waypoint Wp, float Cost)>? seeds = null)
    {
        ClearRoute();
        GoalEntity = goalEntity;

        // Always end at the real goal position.
        PushRoute(goalPos);

        // If we can walk straight there, no waypoints needed (QC routetogoal early-out via tracewalk).
        if (CanWalkStraight(origin, goalPos))
            return;

        if (net is null || net.Count == 0)
            return; // no graph: just head toward the goal and rely on obstacle avoidance

        // QC navigation_findnearestwaypoint(ent, walkfromwp): the goal node is reached by walking FROM the
        // waypoint TO the goal (walkfromwp = false) — see routetogoal, which seeds the goal's with !walkfromwp.
        // An ENTITY goal rides the QC .nearestwaypoint cache (perf/parity 2026-07-03: QC routetogoal reads the
        // cache here too — a static item binds once per match instead of re-tracewalking every route build).
        var goalWp = goalEntity is not null ? net.NearestForGoal(goalEntity, goalPos) : net.Nearest(goalPos, walkFromWp: false);
        if (goalWp is null)
            return;

        // QC navigation_routetogoal seeds the flood from navigation_markroutes_nearestwaypoints — EVERY waypoint
        // reachable within an expanding radius (on-ground 750/50000, air 500/1500), each pre-charged with its
        // bot→seed entry cost — then A*s from that seed set to the goal node, so the planner picks the best graph
        // entry point rather than forcing the single geometrically-nearest one (the nearest is sometimes behind a
        // wall / on the wrong side of a ledge, so a slightly-farther seed can open the cheaper overall route).
        // Fall back to the single-nearest start node when no seed is reachable (e.g. no collision world in tests).
        // The caller may hand in the seed set its rating flood already computed from the same origin (perf
        // 2026-07-03) — the tracewalk-heavy search then runs ONCE per strategy pass instead of twice.
        seeds ??= net.NearestSeeds(origin, onGround, walkFromWp: true);
        List<Waypoint>? path = seeds.Count > 0 ? net.FindPath(seeds, goalWp) : null;
        if (path is null || path.Count == 0)
        {
            var startWp = net.Nearest(origin, walkFromWp: true);
            if (startWp is null)
                return;
            path = net.FindPath(startWp, goalWp);
        }
        if (path is null || path.Count == 0)
            return;

        // Path optimization (QC navigation_routetogoal:1488-1538 "often path can be optimized by not adding the
        // nearest waypoint"): if the bot can walk straight to the SECOND node, the nearest (first) waypoint is a
        // needless detour — drop it. Only when the shortcut is genuinely shorter than going via the first node
        // (QC's vlen2 comparison), so we never trade a clear path for a longer straight line. Cheap one-trace win
        // that keeps bots from doubling back to a waypoint behind them.
        if (path.Count >= 2)
        {
            Waypoint first = path[0], second = path[1];
            if ((first.Flags & WaypointFlagsNoSkip) == 0
                && (origin - second.Center).LengthSquared() < (first.Center - second.Center).LengthSquared()
                && CanWalkStraight(origin, second.Center))
            {
                path.RemoveAt(0);
            }
        }

        // QC navigation_routetogoal teleport-goal forcing (navigation.qc:1318-1334): when the planned route ENDS
        // at a teleporter/jumppad box, the goal isn't the box itself — it's the far side. Force the box's single
        // outgoing destination (its wp00 link) onto the stack ahead of the box so the bot commits to the trigger
        // and is steered toward where the teleport drops it, instead of trying to stand inside the trigger volume.
        if (path.Count > 0)
        {
            Waypoint last = path[^1];
            if (last.HasFlag(WaypointFlags.Teleport) && last.Links.Count > 0)
            {
                Waypoint exit = last.Links[0].To; // wp00 = the teleport destination
                if (!ReferenceEquals(exit, goalWp))
                    PushRoute(new Goal(exit.Center, exit.Flags, exit));
            }
        }

        // Push intermediate waypoints in reverse so the FIRST waypoint ends up at the front of the stack,
        // ahead of the final goal point (which is already at the front). QC pushes goal first, then walks
        // the back-pointer chain pushing each waypoint, achieving the same front-to-back ordering. Each
        // waypoint carries its flags so Steer can drive jump/crouch/teleport/ladder traversal.
        for (int i = path.Count - 1; i >= 0; i--)
        {
            Waypoint wp = path[i];
            PushRoute(new Goal(wp.Center, wp.Flags, wp));
        }
    }

    /// <summary>
    /// Advance the route follower one frame and produce a wish-move toward the current goal
    /// (QC havocbot_movetogoal core). Pops reached goals, sets <see cref="WantJump"/>/<see cref="WantCrouch"/>,
    /// and returns forward/side/up move values in the bot's local frame (X forward, Y side, Z up), scaled to
    /// <see cref="MaxSpeed"/>. <paramref name="viewYaw"/> is the bot's current yaw (degrees) used to project
    /// the world move direction into the local frame. Returns zero when there's no goal.
    /// </summary>
    public Vector3 Steer(Entity bot, float viewYaw, bool onGround)
    {
        WantJump = false;
        WantCrouch = false;
        WantBunnyhop = false;

        // Pop any goals we've effectively reached (QC navigation_poptouchedgoals). A teleport/jumppad goal is
        // "reached" once we've entered its trigger volume — the trigger then moves us, so we note the time and
        // pop so the bot doesn't try to stand on the destination (QC the lastteleporttime handling).
        while (_goals.Count > 0 && ReachedGoal(bot, _goals[0]))
        {
            if ((_goals[0].Flags & WaypointFlags.Teleport) != 0)
                LastTeleportTime = Now;
            PopRoute();
        }

        if (_goals.Count == 0)
            return Vector3.Zero;

        Goal goal = _goals[0];
        Vector3 destorg = goal.Pos;
        Vector3 diff = destorg - bot.Origin;
        Vector3 dir = QMath.Normalize(diff);
        var flat = new Vector3(diff.X, diff.Y, 0f);
        Vector3 flatdir = flat.LengthSquared() > 0f ? QMath.Normalize(flat) : dir;

        // ---- crouch waypoint: hold crouch while traversing (QC WAYPOINTFLAG_CROUCH) ----
        if ((goal.Flags & WaypointFlags.Crouch) != 0)
            WantCrouch = true;

        // ---- jump waypoint: the outgoing link requires a jump (QC WAYPOINTFLAG_JUMP) ----
        // Jump as we approach the jump-waypoint so the leap carries us along the link.
        if ((goal.Flags & WaypointFlags.Jump) != 0 && onGround && flat.Length() < 100f)
            WantJump = true;

        // ---- ladder waypoint: climb (QC WAYPOINTFLAG_LADDER) — bias the move upward and don't brake on the
        //      vertical gap, since a ladder lets us ascend without a jump.
        bool onLadder = (goal.Flags & WaypointFlags.Ladder) != 0;

        // ---- obstacle / step-up detection -> jump (QC tracebox jumpobstacle_check block) ----
        // Probe a little ahead at current and stepped-up heights; if a wall blocks at foot level but the
        // path opens up when raised by a jump's worth of height, jump.
        float speed = new Vector2(bot.Velocity.X, bot.Velocity.Y).Length();
        float reach = MathF.Max(32f, speed * 0.3f);
        Vector3 ahead = bot.Origin + flatdir * reach;

        var trFlat = Trace(bot, bot.Origin, ahead);
        if (trFlat.Fraction < 1f && trFlat.PlaneNormal.Z < 0.7f)
        {
            // Wall ahead. Can we walk up it as a step?
            var step = new Vector3(0f, 0f, StepHeightLive);
            var trStep = Trace(bot, bot.Origin + step, ahead + step);
            if (trStep.Fraction < trFlat.Fraction + 0.01f && trStep.PlaneNormal.Z < 0.7f)
            {
                // Still blocked at step height: try a full jump's height (QC stepheight + jumpheight_vec on ground).
                var jh = new Vector3(0f, 0f, StepHeightLive + JumpHeightApex);
                var trJump = Trace(bot, bot.Origin + jh, ahead + jh);
                if (trJump.Fraction > trStep.Fraction && onGround)
                    WantJump = true;
            }
        }

        // ---- goal above us -> jump up onto it (unless on a ladder, where we just climb) ----
        if (!onLadder && onGround && diff.Z > StepHeightLive && flat.Length() < Maxs.X * 2f)
            WantJump = true;

        // ---- dangerous edge / fall ahead -> brake (QC do_break, simplified) ----
        // Skipped for jumppad/teleport/jump goals (we WANT to commit) and ladders (controlled descent).
        Vector3 brake = Vector3.Zero;
        bool committing = (goal.Flags & (WaypointFlags.Teleport | WaypointFlags.Jump)) != 0 || onLadder;
        if (!committing && onGround && diff.Z < -120f && flat.Length() < 250f)
        {
            // The goal is far below and not far ahead horizontally: there may be a ledge. Probe straight
            // down ahead of us; if the drop is large, slow down so we don't overrun a deadly edge.
            var downStart = bot.Origin + flatdir * 16f;
            var trDown = Trace(bot, downStart, downStart - new Vector3(0f, 0f, 400f));
            if (downStart.Z - trDown.EndPos.Z > 200f)
                brake = QMath.Normalize(bot.Velocity) * -1f;
        }

        Vector3 worldMove = QMath.Normalize(dir + brake);
        if (worldMove == Vector3.Zero)
            worldMove = dir;
        // On a ladder, bias the move strongly upward so the climb works (QC pushes +z on ladders).
        if (onLadder && diff.Z > 0f)
            worldMove = QMath.Normalize(worldMove + new Vector3(0f, 0f, 1f));

        // ---- project world direction into the bot's local move frame ----
        // Use yaw-only basis (QC makevectors(v_angle.y * '0 1 0')) so forward/side don't tilt with pitch.
        QMath.AngleVectors(new Vector3(0f, viewYaw, 0f), out var forward, out var right, out var up);
        float fwd = QMath.Dot(worldMove, forward);
        float side = QMath.Dot(worldMove, right);
        float vert = QMath.Dot(worldMove, up);

        // ---- keyboard-movement emulation (QC havocbot_keyboard_movement, havocbot.qc:272-341) ----
        // Below skill 10 the bot doesn't move with a fully analog wish-move: it quantizes the analog direction
        // onto keyboard keys (forward/back/strafe, with skill tiers that gate diagonals) on a skill-scaled
        // clock, then blends back toward the analog move as it nears the goal (so close-in maneuvering stays
        // smooth). This makes low-skill bots strafe/turn coarser, matching stock. (fwd/side/vert are already the
        // normalized -1..1 move = QC's CS(this).movement / sv_maxspeed.)
        if (Skill < 10f)
            KeyboardMovement(bot, goal.Pos, ref fwd, ref side, ref vert);

        // ---- bunnyhop tuning (QC havocbot_bunnyhop): keep jumping to maintain speed toward a far goal ----
        // QC havocbot.qc:1315 forbids bunnyhop when do_break/evadedanger is set this frame. The Steer-internal
        // ledge brake (do_break analogue, above) gates it here; the BotBrain per-frame danger brake (which runs
        // AFTER Steer) gates it by ANDing WantBunnyhop with "no danger this frame" before pressing jump. Result
        // is reported via WantBunnyhop (not WantJump) so the brain owns the final danger-suppression decision.
        if (brake == Vector3.Zero && Bunnyhop(bot, dir, onGround, goal, attacking: false))
            WantBunnyhop = true;

        return new Vector3(fwd, side, vert) * MaxSpeed;
    }

    /// <summary>Bot skill (QC <c>skill</c>), set by the brain — gates whether the bot bunnyhops at all.</summary>
    public float Skill = 5f;

    /// <summary>
    /// QC <c>bot_moveskill</c>, added to <see cref="Skill"/> in the bunnyhop gate (havocbot.qc:1315). Stock default 0;
    /// the midair mutator forces it to 0 on spawn so high-skill bots stop bunnyhopping while keeping aim/reaction.
    /// </summary>
    public float MoveSkill;

    // ---- keyboard-movement emulation state (QC havocbot.qh .havocbot_keyboardtime / .havocbot_keyboard) ----
    private float _keyboardTime;      // QC .havocbot_keyboardtime — next time the keyboard direction may change
    private Vector3 _keyboard;        // QC .havocbot_keyboard — the last latched quantized move (×sv_maxspeed)
    private readonly Random _kbRng = new();

    /// <summary>
    /// QC <c>havocbot_keyboard_movement</c> (havocbot.qc:272-341): quantize the analog wish-move onto keyboard
    /// directions on a skill-scaled clock, then blend back toward the analog move as the bot nears the goal.
    /// Operates in place on the normalized local move (<paramref name="fwd"/>/<paramref name="side"/>/
    /// <paramref name="vert"/> = QC's CS(this).movement / sv_maxspeed, range -1..1). Skill tiers gate which
    /// directions/diagonals are allowed, so low-skill bots strafe/turn coarser exactly like stock.
    /// </summary>
    private void KeyboardMovement(Entity bot, Vector3 destorg, ref float fwd, ref float side, ref float vert)
    {
        float now = Now;
        if (now <= _keyboardTime)
        {
            // not time to re-key yet: keep blending the latched keyboard move with the analog move (below).
            BlendKeyboard(bot, destorg, ref fwd, ref side, ref vert);
            return;
        }

        float sk = Skill + MoveSkill;               // QC: skill + bot_moveskill (havocbot_keyboardskill folded to 0)
        // QC re-key clock: faster (more responsive) the higher the skill; +small random jitter.
        _keyboardTime = MathF.Max(
            _keyboardTime
                + 0.05f / MathF.Max(1f, sk)
                + (float)_kbRng.NextDouble() * 0.025f / MathF.Max(0.00025f, Skill),
            now);

        // start from the analog move (already normalized -1..1 = QC keyboard = movement/maxspeed).
        var keyboard = new Vector3(fwd, side, vert);
        float trigger = Cvars.FloatOr("bot_ai_keyboard_threshold", 0.57f);

        // categorize forward movement (QC's skill-tiered direction gating):
        //  sk < 1.5: only forward; sk < 2.5: only individual dirs; sk < 4.5: + forward diagonals; else all.
        if (keyboard.X > trigger)
        {
            keyboard.X = 1f;
            if (sk < 2.5f) keyboard.Y = 0f;
        }
        else if (keyboard.X < -trigger && sk > 1.5f)
        {
            keyboard.X = -1f;
            if (sk < 4.5f) keyboard.Y = 0f;
        }
        else
        {
            keyboard.X = 0f;
            if (sk < 1.5f) keyboard.Y = 0f;
        }
        if (sk < 4.5f) keyboard.Z = 0f;

        keyboard.Y = keyboard.Y > trigger ? 1f : (keyboard.Y < -trigger ? -1f : 0f);
        keyboard.Z = keyboard.Z > trigger ? 1f : (keyboard.Z < -trigger ? -1f : 0f);

        // anti-stuck: if nothing is pressed, don't hold the (high) re-key clock for long (QC havocbot.qc:330).
        if (keyboard == Vector3.Zero)
            _keyboardTime = MathF.Min(_keyboardTime, now + 0.2f);

        _keyboard = keyboard; // QC stores keyboard * sv_maxspeed; here normalized (×maxspeed applied by Steer's caller)
        BlendKeyboard(bot, destorg, ref fwd, ref side, ref vert);
    }

    /// <summary>
    /// QC havocbot_keyboard_movement tail (havocbot.qc:337-340): blend the analog move toward the latched
    /// keyboard move, the blend strength scaling with distance to the goal (full keyboard far out, fully analog
    /// once within <c>bot_ai_keyboard_distance</c> so close-in maneuvering stays smooth / 360-degree).
    /// </summary>
    private void BlendKeyboard(Entity bot, Vector3 destorg, ref float fwd, ref float side, ref float vert)
    {
        float kbDist = MathF.Max(1f, Cvars.FloatOr("bot_ai_keyboard_distance", 250f));
        float blend = QMath.Bound(0f, (destorg - bot.Origin).Length() / kbDist, 1f);
        fwd += (_keyboard.X - fwd) * blend;
        side += (_keyboard.Y - side) * blend;
        vert += (_keyboard.Z - vert) * blend;
    }

    /// <summary>
    /// QC <c>havocbot_bunnyhop</c>: decide whether to jump this frame to bunnyhop toward the goal. The bot
    /// bunnyhops only at/above the skill offset, when not attacking, already at/above run speed, on the
    /// ground, not crouched, out of deep water, and heading at the goal within the direction-deviation cone —
    /// and only when the remaining distance to the goal exceeds the jump distance (so it doesn't overshoot a
    /// near waypoint). Faithful to the QC gating including the jump-distance-vs-remaining check.
    /// </summary>
    private bool Bunnyhop(Entity bot, Vector3 dir, bool onGround, Goal goal, bool attacking)
    {
        // skill gate (QC havocbot.qc:1315: skill + bot_moveskill >= bot_ai_bunnyhop_skilloffset; ships 7). The
        // midair mutator zeroes MoveSkill on spawn but leaves Skill intact, so a high-skill bot still bhops unless
        // a configured moveskill pushed the sum over the offset (faithful to Base, which only nukes moveskill).
        float skillOffset = Cvars.FloatOr("bot_ai_bunnyhop_skilloffset", 7f);
        if (Skill + MoveSkill < skillOffset)
            return false;
        if (attacking || !onGround)
            return false;
        if (WantCrouch || bot.WaterLevel > 1) // WATERLEVEL_WETFEET
            return false;
        // don't bunnyhop straight into a jump/teleport goal (we handle those explicitly).
        if ((goal.Flags & (WaypointFlags.Jump | WaypointFlags.Teleport)) != 0)
            return false;

        var vel2 = new Vector2(bot.Velocity.X, bot.Velocity.Y);
        float vel = vel2.Length();
        if (vel < MaxSpeed) // QC: must already be at/above run speed
            return false;

        // direction deviation cone (QC: angle between velocity and desired dir within the max).
        Vector3 velAngles = QMath.VecToAngles(new Vector3(bot.Velocity.X, bot.Velocity.Y, 0f));
        Vector3 dirAngles = QMath.VecToAngles(new Vector3(dir.X, dir.Y, 0f));
        float devY = WrapDeg(velAngles.Y - dirAngles.Y);
        float maxDev = Cvars.FloatOr("bot_ai_bunnyhop_dir_deviation_max", 20f); // ships 20
        if (MathF.Abs(devY) >= maxDev)
            return false;

        // jump distance grows ~linearly with speed (QC formula); only hop if the goal is farther than that.
        Vector3 gco = goal.Pos;
        float jumpDistance = 52.661f + 0.606f * vel + (bot.Origin.Z - gco.Z);
        float remaining = new Vector2(gco.X - bot.Origin.X, gco.Y - bot.Origin.Y).Length();
        return remaining > MathF.Max(0f, jumpDistance);
    }

    private static float WrapDeg(float a)
    {
        while (a < -180f) a += 360f;
        while (a > 180f) a -= 360f;
        return a;
    }

    private static float Now => Api.Clock.Time;

    /// <summary>
    /// Have we reached this goal? (QC navigation_poptouchedgoals). A box waypoint (e.g. a teleporter trigger
    /// volume) counts as reached once the bot is inside the box footprint; a point waypoint uses the
    /// proximity radius. A teleport/jumppad goal also counts the moment we're inside its trigger so we let it
    /// fling us rather than overshooting.
    /// </summary>
    private bool ReachedGoal(Entity bot, Goal goal)
    {
        if (goal.Wp is { IsBox: true } wp)
        {
            Vector3 lo = wp.AbsMin, hi = wp.AbsMax;
            Vector3 o = bot.Origin;
            bool inside = o.X >= lo.X && o.X <= hi.X && o.Y >= lo.Y && o.Y <= hi.Y
                          && o.Z >= lo.Z - GoalReachedZ && o.Z <= hi.Z + GoalReachedZ;
            if (inside) return true;
        }
        return Reached(bot.Origin, goal.Pos);
    }

    /// <summary>Have we reached <paramref name="goal"/> from <paramref name="origin"/>? (QC poptouchedgoals proximity).</summary>
    private static bool Reached(Vector3 origin, Vector3 goal)
    {
        var d = goal - origin;
        float xy = new Vector2(d.X, d.Y).Length();
        return xy < GoalReachedXY && MathF.Abs(d.Z) < GoalReachedZ;
    }

    /// <summary>tracebox between two points using the bot's hull, ignoring the bot (QC tracebox MOVE_NOMONSTERS).</summary>
    private TraceResult Trace(Entity bot, Vector3 start, Vector3 end)
        => Api.Trace.Trace(start, Mins, Maxs, end, MoveFilter.NoMonsters, bot);

    /// <summary>
    /// Can the bot walk in a straight line from a to b? (QC tracewalk early-out in navigation_routetogoal).
    /// Uses the full <see cref="BotTracewalk"/> reachability test — stepping the hull along the path and
    /// handling stairs, ledges and water — rather than a single straight hull sweep, so a clear staircase or
    /// shallow ford counts as directly reachable (no waypoints needed).
    /// </summary>
    private bool CanWalkStraight(Vector3 a, Vector3 b)
        // Strategy-budgeted (variance program 2026-07-11): a straight-to-goal shortcut beyond the budget is
        // never the right answer anyway (the router owns long routes) — and the unbounded walk toward a far
        // goal was the melt-class 256-step × 4-trace tracewalk. See WaypointNetwork.StrategyWalkMaxDist.
        => BotTracewalk.CanWalk(a, b, Mins, Maxs, maxWalkDistance: WaypointNetwork.StrategyWalkMaxDist);
}
