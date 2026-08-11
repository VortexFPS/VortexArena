using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;

namespace VortexArena.Server.Bot;

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

    /// <summary>
    /// QC <c>this.aistatus &amp; AI_STATUS_ATTACKING</c> (set at havocbot.qc:137 when the bot has an enemy in
    /// sight). Base forbids bunnyhopping while it is set (havocbot.qc:217) — a fighting bot needs to be able to
    /// change direction, not carry committed momentum. The brain stamps this each think.
    ///
    /// <para>The port passed <c>attacking: false</c> unconditionally, so bots fought airborne at above run
    /// speed; combined with corrections steering them off their velocity vector, they slammed into walls and
    /// overshot waypoints in tight spaces. See planning/bot-ai-parity-2026-08-03.md D13.</para>
    /// </summary>
    public bool Attacking;

    /// <summary>Set true while steering when an obstacle/up-step needs a jump (QC PHYS_INPUT_BUTTON_JUMP).</summary>
    public bool WantJump { get; private set; }

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

    /// <summary>The route's actual destination, after all intermediate waypoint nodes.</summary>
    public Vector3? FinalGoal => _goals.Count > 0 ? _goals[^1].Pos : null;

    public bool HasGoal => _goals.Count > 0;

    /// <summary>
    /// The <paramref name="index"/>-th node along the current route, clamped to the last one when the route
    /// is shorter. Feeds the neural locomotor's corridor look-ahead
    /// (<see cref="Neural.MoveIntent.CorridorA"/>/<see cref="Neural.MoveIntent.CorridorB"/>): two nodes of
    /// warning is what lets a policy carry speed through a corner instead of arriving at it flat.
    /// Returns <paramref name="fallback"/> when there is no route at all.
    /// </summary>
    public Vector3 RouteNode(int index, Vector3 fallback)
        => _goals.Count == 0 ? fallback : _goals[System.Math.Min(index, _goals.Count - 1)].Pos;

    /// <summary>Nodes remaining on the route (QC the goal stack depth).</summary>
    public int RouteLength => _goals.Count;

    /// <summary>Clear the route (QC navigation_clearroute).</summary>
    public void ClearRoute()
    {
        _goals.Clear();
        GoalEntity = null;
        LastTeleportTime = 0f;
        PrevGoalWp = null;
        HasPrevGoal = false;
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
        // QC havocbot_checkgoaldistance:346-347 — a bot deliberately holding still to re-orient is not stuck.
        if (StopMovingTimeout > now)
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
        {
            // QC .goalcurrent_prev: the node just left. Several behaviours key off it — the hardwired-link
            // brake skip, the evade-danger centreline, the post-JUMP-waypoint launch.
            PrevGoalWp = _goals[0].Wp;
            PrevGoalPos = _goals[0].Pos;
            HasPrevGoal = true;
            _goals.RemoveAt(0);
        }
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

        // ---- ladder waypoint: climb (QC WAYPOINTFLAG_LADDER) — bias the move upward and don't brake on the
        //      vertical gap, since a ladder lets us ascend without a jump.
        bool onLadder = (goal.Flags & WaypointFlags.Ladder) != 0;

        // ---- QC "stop and re-orient" brake (havocbot.qc:1130-1134, consumed at :907-911) ----
        // A low-skill bot running hard into a sharp turn stops for a moment instead of grinding along the wall
        // while its yaw slews. Base's primary anti-wall-grind device, and the port had nothing like it: the bot
        // kept pushing forward through the turn and, because it wasn't closing on the goal, the 0.5 s
        // no-progress watchdog then destroyed its route mid-corner. See parity report D9.
        float curSpeed = new Vector2(bot.Velocity.X, bot.Velocity.Y).Length();
        Vector3 deviation = Vector3.Zero;
        if (curSpeed < MaxSpeed * 0.2f)
            curSpeed = MaxSpeed * 0.2f;
        else
            deviation = WrapYaw(QMath.VecToAngles(diff) - QMath.VecToAngles(bot.Velocity));

        if (Now < StopMovingTimeout)
        {
            // QC havocbot.qc:909-911: destorg = origin; diff = dir = '0 0 0' — emit no move at all this frame.
            LastWorldDir = Vector3.Zero;
            LastBrake = Vector3.Zero;
            LastGoalPos = goal.Pos;
            return Vector3.Zero;
        }
        if (Skill + MoveSkill <= 3f && curSpeed > MaxSpeed * 0.9f && MathF.Abs(deviation.Y) > 70f)
            StopMovingTimeout = Now + 0.4f + (float)_kbRng.NextDouble() * 0.2f;

        // ---- QC look-ahead + corner cut (havocbot.qc:979-1048) ----
        // The bot does NOT steer at the goal point: it steers at `actual_destorg`, a point one "reaction
        // distance" ahead along its bearing, scaled by speed and by how far off-heading it currently is. When
        // it gets within that distance of the current goal it re-aims at the NEXT goal instead, which is what
        // makes a Xonotic bot round a corner in one smooth arc rather than driving to the node and pivoting.
        // The port steered straight at the goal centre, so its obstacle probe and jump decision were evaluated
        // against a direction the bot was not actually going to travel.
        float offsetLen = MathF.Max(32f, curSpeed * MathF.Cos(deviation.Y * MathF.PI / 180f) * 0.3f);
        Vector3 offset = flatdir * offsetLen;
        Vector3 actualDest = bot.Origin + offset;
        bool turning = false;
        float flatLen2 = flat.LengthSquared();
        Goal? next = _goals.Count > 1 ? _goals[1] : null;

        if (HasPrevGoal && PrevGoalWp is not null && PrevGoalWp.HasFlag(WaypointFlags.Jump))
        {
            // QC havocbot.qc:993-1009 — the launch AFTER a jump waypoint, not on approach to one. Base fires
            // the jump once the bot has already LEFT the jump node, is at speed, and is 50-150qu past it; the
            // port jumped while approaching, which is the opposite phase of the manoeuvre (D19).
            Vector3 fromPrev = new(bot.Origin.X - PrevGoalPos.X, bot.Origin.Y - PrevGoalPos.Y, 0f);
            float prevDist = fromPrev.Length();
            if (Now > StopMovingTimeout && MathF.Abs(deviation.Y) > 20f
                && curSpeed > MaxSpeed * 0.4f && prevDist < 50f)
                StopMovingTimeout = Now + 0.1f;

            Vector3 prevToDest = new(destorg.X - PrevGoalPos.X, destorg.Y - PrevGoalPos.Y, 0f);
            if (curSpeed > MaxSpeed * 0.9f && flatLen2 < prevToDest.LengthSquared()
                && prevDist > 50f && prevDist < 150f)
                WantJump = true;
        }
        else if (next is null || (goal.Flags & (WaypointFlags.Teleport | WaypointFlags.Ladder)) != 0)
        {
            // Last goal, or one that must be entered exactly: aim AT it once inside the look-ahead radius.
            if (flatLen2 < offsetLen * offsetLen)
            {
                if ((goal.Flags & WaypointFlags.Jump) != 0 && next is not null)
                    WantJump = true;   // QC: oblique warpzones need a jump or bots get stuck
                else
                    actualDest = new Vector3(destorg.X, destorg.Y, actualDest.Z);
            }
        }
        else if (flat.Length() < 32f && diff.Z < -16f)
        {
            actualDest = new Vector3(destorg.X, destorg.Y, actualDest.Z); // goal directly below: aim at it
        }
        else if (flatLen2 < offsetLen * offsetLen)
        {
            // CORNER CUT: close to this goal and another follows — steer past it toward the next one.
            Vector3 nextOrg = next.Value.Pos;
            Vector3 toNext = new(nextOrg.X - destorg.X, nextOrg.Y - destorg.Y, 0f);
            Vector3 nextDir = toNext.LengthSquared() > 0f ? QMath.Normalize(toNext) : Vector3.Zero;
            Vector3 overshoot = new(bot.Origin.X + offset.X - destorg.X, bot.Origin.Y + offset.Y - destorg.Y, 0f);
            float dist = overshoot.Length();
            actualDest = dist * dist > toNext.LengthSquared()
                ? nextOrg                                  // don't aim beyond the next goal
                : new Vector3(destorg.X, destorg.Y, 0f) + dist * nextDir;
            actualDest.Z = bot.Origin.Z;
            turning = true;
        }

        // ---- obstacle probe -> jump (QC jumpobstacle_check, havocbot.qc:1049-1099) ----
        // Retried once WITHOUT the corner cut: an obstacle that only exists because we are cutting the corner
        // is not a reason to jump, it is a reason to stop cutting. QC does this with a goto; the loop is the
        // same two passes.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            dir = flatdir = QMath.Normalize(actualDest - bot.Origin);

            bool jumpForbidden = !turning && MathF.Abs(deviation.Y) > 50f;
            if (!jumpForbidden && WantCrouch)
            {
                // QC: a ducked bot that would be stuck standing must not jump.
                var stand = Trace(bot, bot.Origin, bot.Origin);
                if (stand.StartSolid) jumpForbidden = true;
            }
            if (jumpForbidden) break;

            var trFlat = Trace(bot, bot.Origin, actualDest);
            if (trFlat.Fraction >= 1f || trFlat.PlaneNormal.Z >= 0.7f) break;

            float s = trFlat.Fraction;
            var step = new Vector3(0f, 0f, StepHeightLive);
            var trStep = Trace(bot, bot.Origin + step, actualDest + step);
            if (trStep.Fraction >= s + 0.01f || trStep.PlaneNormal.Z >= 0.7f) break;

            if (turning && MathF.Abs(deviation.Y) > 5f && attempt == 0)
            {
                // The obstacle may be an artefact of the corner cut — re-probe straight at the goal.
                actualDest = destorg;
                turning = false;
                continue;
            }

            s = trStep.Fraction;
            // QC havocbot.qc:1081: on the ground use the FULL jump apex; airborne use the reduced
            // jumpstepheightvec (the "easy jump" reach tracewalk assumes).
            var jh = new Vector3(0f, 0f, onGround ? StepHeightLive + JumpHeightApex : JumpStepHeightLive);
            if (Trace(bot, bot.Origin + jh, actualDest + jh).Fraction > s)
            {
                WantJump = true;
            }
            else
            {
                // QC's half-apex fallback: clearing at half height still beats not jumping at all.
                jh = new Vector3(0f, 0f, StepHeightLive + JumpHeightApex * 0.5f);
                if (Trace(bot, bot.Origin + jh, actualDest + jh).Fraction > s)
                    WantJump = true;
            }
            break;
        }

        // ---- goal above us -> jump up onto it (unless on a ladder, where we just climb) ----
        if (!onLadder && onGround && diff.Z > StepHeightLive && flat.Length() < Maxs.X * 2f)
            WantJump = true;

        // ---- dangerous edge / fall ahead -> brake (QC do_break, havocbot.qc:1165-1174) ----
        // Skipped for jumppad/teleport/jump goals (we WANT to commit), ladders (controlled descent), and a
        // hardwired link (a hand-authored drop the mapper intends the bot to take).
        Vector3 brake = Vector3.Zero;
        bool committing = (goal.Flags & (WaypointFlags.Teleport | WaypointFlags.Jump)) != 0 || onLadder
            || WaypointNetwork.IsHardwiredLink(PrevGoalWp, goal.Wp);
        // QC's gate is `!IS_ONGROUND || xy-speed > maxspeed * 0.3` — the comment above it reads "slow down if
        // bot is in the air and goal is under it", so the AIRBORNE case is the primary one. The port required
        // onGround with no speed term, which is the exact inverse: it braked a slow bot walking off a step that
        // Base lets go, and never braked the falling bot Base is actually aiming at. With `normalize(dir+brake)`
        // that made every route leg descending >120qu self-cancelling. See parity report D17.
        float xySpeed = new Vector2(bot.Velocity.X, bot.Velocity.Y).Length();
        if (!committing && (!onGround || xySpeed > MaxSpeed * 0.3f) && diff.Z < -120f && flat.Length() < 250f)
        {
            // The goal is far below and not far ahead horizontally: there may be a ledge. Probe straight
            // down ahead of us; if the drop is large, slow down so we don't overrun a deadly edge.
            var downStart = bot.Origin + flatdir * 16f;
            var trDown = Trace(bot, downStart, downStart - new Vector3(0f, 0f, 400f));
            if (downStart.Z - trDown.EndPos.Z > 200f)
                brake = QMath.Normalize(bot.Velocity) * -1f;
        }

        // QC havocbot.qc:1043 + :1155: the steering direction is FLATDIR — the horizontal bearing to the goal.
        // Using the full 3D direction scales horizontal speed by cos(elevation): 71% for a goal 45deg above,
        // ~0% at the foot of a ledge with the waypoint overhead, so the bot crept exactly where it was trying
        // to climb and then tripped the 0.5s no-progress watchdog. Vertical intent is carried by WantJump and
        // the ladder bias below, not by shortening the run. See parity report D8.
        Vector3 worldMove = flatdir;   // the look-ahead / corner-cut bearing settled on above
        // On a ladder, bias the move strongly upward so the climb works (QC pushes +z on ladders).
        if (onLadder && diff.Z > 0f)
            worldMove = QMath.Normalize(worldMove + new Vector3(0f, 0f, 1f));

        // The brain folds its danger/dodge corrections onto this before the local projection (QC composes
        // `dir = normalize(dir + dodge + do_break + evadedanger)` once, at havocbot.qc:1269).
        LastWorldDir = worldMove;
        LastBrake = brake;
        LastGoalPos = goal.Pos;

        // ---- bunnyhop tuning (QC havocbot_bunnyhop): keep jumping to maintain speed toward a far goal ----
        // QC havocbot.qc:1315 forbids bunnyhop when do_break/evadedanger is set this frame. The Steer-internal
        // ledge brake (do_break analogue, above) gates it here; the BotBrain per-frame danger brake (which runs
        // AFTER Steer) gates it by ANDing WantBunnyhop with "no danger this frame" before pressing jump. Result
        // is reported via WantBunnyhop (not WantJump) so the brain owns the final danger-suppression decision.
        if (brake == Vector3.Zero && Bunnyhop(bot, dir, onGround, goal, Attacking))
            WantBunnyhop = true;

        return ComposeMove(bot, worldMove + brake, viewYaw, goal.Pos);
    }

    /// <summary>
    /// The world direction <see cref="Steer"/> settled on before any brain-side correction (QC's <c>dir</c> at
    /// havocbot.qc:1155, the input to the <c>normalize(dir + dodge + do_break + evadedanger)</c> fold).
    /// </summary>
    public Vector3 LastWorldDir { get; private set; }

    /// <summary>Steer's own ledge brake this frame (QC <c>do_break</c>), so the brain can re-fold it.</summary>
    public Vector3 LastBrake { get; private set; }

    /// <summary>The goal point <see cref="Steer"/> aimed at, for a brain-side recompose.</summary>
    public Vector3 LastGoalPos { get; private set; }

    /// <summary>
    /// The previous goal's waypoint (QC <c>.goalcurrent_prev</c>) — the node the bot most recently left. Drives
    /// the hardwired-link brake skip and the evade-danger centreline.
    /// </summary>
    public Waypoint? PrevGoalWp { get; private set; }

    /// <summary>Previous goal position (QC <c>goalcurrent_prev.origin</c>), valid when <see cref="HasPrevGoal"/>.</summary>
    public Vector3 PrevGoalPos { get; private set; }

    /// <summary>Whether a previous goal has been recorded this route.</summary>
    public bool HasPrevGoal { get; private set; }

    /// <summary>True when the current goal is a graph waypoint (QC <c>goalcurrent.classname == "waypoint"</c>).</summary>
    public bool CurrentIsWaypoint => _goals.Count > 0 && _goals[0].Wp is not null;

    /// <summary>The current goal's waypoint (QC <c>goalcurrent</c>), or null for a bare position goal.</summary>
    public Waypoint? CurrentWp => _goals.Count > 0 ? _goals[0].Wp : null;

    /// <summary>
    /// True when the IMMEDIATE goal is a player (QC <c>IS_PLAYER(this.goalcurrent)</c>). Distinct from
    /// "<see cref="GoalEntity"/> is a player": the goal stack may be routing THROUGH waypoints toward a player,
    /// and QC only treats danger as "unreachable" when the player is the step the bot is walking to right now.
    /// Blacklisting on the final target instead would ban a chase the bot has barely started.
    /// </summary>
    public bool CurrentGoalEntityIsPlayer
        => _goals.Count > 0 && _goals[0].Wp is null && GoalEntity is Player;

    /// <summary>
    /// Project a world wish-direction into the bot's local move frame and apply the keyboard quantisation
    /// (QC makevectors(v_angle.y) + havocbot_keyboard_movement). Split out of <see cref="Steer"/> so the brain
    /// can fold its dodge/danger corrections into the WORLD direction first, the way QC composes them in one
    /// place at havocbot.qc:1269, instead of overwriting the already-projected local move.
    /// </summary>
    public Vector3 ComposeMove(Entity bot, Vector3 worldDir, float viewYaw, Vector3 goalPos)
    {
        Vector3 world = QMath.Normalize(worldDir);
        if (world == Vector3.Zero)
            world = LastWorldDir;

        // Use yaw-only basis (QC makevectors(v_angle.y * '0 1 0')) so forward/side don't tilt with pitch.
        QMath.AngleVectors(new Vector3(0f, viewYaw, 0f), out var forward, out var right, out var up);
        float fwd = QMath.Dot(world, forward);
        float side = QMath.Dot(world, right);
        float vert = QMath.Dot(world, up);

        // ---- keyboard-movement emulation (QC havocbot_keyboard_movement, havocbot.qc:272-341) ----
        // Below skill 10 the bot doesn't move with a fully analog wish-move: it quantizes the analog direction
        // onto keyboard keys (forward/back/strafe, with skill tiers that gate diagonals) on a skill-scaled
        // clock, then blends back toward the analog move as it nears the goal (so close-in maneuvering stays
        // smooth). This makes low-skill bots strafe/turn coarser, matching stock.
        if (Skill < 10f)
            KeyboardMovement(bot, goalPos, ref fwd, ref side, ref vert);

        return new Vector3(fwd, side, vert) * MaxSpeed;
    }

    /// <summary>Bot skill (QC <c>skill</c>), set by the brain — gates whether the bot bunnyhops at all.</summary>
    public float Skill = 5f;

    /// <summary>
    /// QC <c>bot_moveskill</c>, added to <see cref="Skill"/> in the bunnyhop gate (havocbot.qc:1315). Stock default 0;
    /// the midair mutator forces it to 0 on spawn so high-skill bots stop bunnyhopping while keeping aim/reaction.
    /// </summary>
    public float MoveSkill;

    /// <summary>
    /// QC <c>.bot_stop_moving_timeout</c> (havocbot.qc:1133): until this time the bot emits NO movement, so it
    /// can stop and let its yaw catch up instead of grinding along a wall through a hard turn. Also suppresses
    /// the no-progress watchdog (QC havocbot_checkgoaldistance early-returns while it is set, havocbot.qc:346)
    /// — otherwise a deliberate pause would be read as being stuck.
    /// </summary>
    public float StopMovingTimeout;

    /// <summary>Wrap a yaw delta into (-180, 180] (QC's `while (deviation.y &lt; -180) …` idiom).</summary>
    private static Vector3 WrapYaw(Vector3 a)
    {
        while (a.Y < -180f) a.Y += 360f;
        while (a.Y > 180f) a.Y -= 360f;
        return a;
    }

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
        // QC havocbot.qc:217-221: no hop while the immediate goal is a PLAYER (a chase needs manoeuvrability,
        // not committed momentum), nor on the frame after leaving a JUMP waypoint (that launch is its own move).
        if (CurrentGoalEntityIsPlayer)
            return false;
        if (PrevGoalWp is not null && PrevGoalWp.HasFlag(WaypointFlags.Jump))
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
        if (remaining > MathF.Max(0f, jumpDistance))
            return true;

        // QC havocbot.qc:237-255 — the CONTINUATION arm. Too close to this goal to hop over it, but if another
        // goal lies well beyond and the turn between them is gentle enough at this speed, keep hopping THROUGH
        // rather than landing and re-accelerating. Without it a high-skill bot stops dead at every waypoint,
        // which is both slower and visibly choppier. The turn budget tightens as speed rises — the four cvars
        // that tune it were registered and read by nothing until now (D31).
        if ((goal.Flags & (WaypointFlags.Jump | WaypointFlags.Teleport)) != 0) return false;
        if (_goals.Count < 2) return false;
        Goal nextGoal = _goals[1];
        if ((nextGoal.Flags & WaypointFlags.Jump) != 0) return false;
        Vector3 gno = nextGoal.Pos;
        if (new Vector2(gco.X - gno.X, gco.Y - gno.Y).Length() <= 70f) return false;

        Vector3 ang = QMath.VecToAngles(gco - bot.Origin);
        float turnDev = WrapDeg(QMath.VecToAngles(gno - gco).Y - velAngles.Y);
        float maxTurn = Cvars.FloatOr("bot_ai_bunnyhop_turn_angle_max", 80f)
            - Cvars.FloatOr("bot_ai_bunnyhop_turn_angle_reduction", 40f) * ((vel - MaxSpeed) / MaxSpeed);
        float minTurn = Cvars.FloatOr("bot_ai_bunnyhop_turn_angle_min", 4f);
        float downPitch = Cvars.FloatOr("bot_ai_bunnyhop_downward_pitch_max", 30f);
        return (ang.X < 90f || ang.X > 360f - downPitch)
               && MathF.Abs(turnDev) < MathF.Max(minTurn, maxTurn);
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
        // A TELEPORT goal (teleporter mouth or jumppad trigger) is NOT reached by being near it — it is reached
        // by the trigger actually having fired. QC navigation.qc:1652 gates the pop on
        //   lastteleporttime > 0 && TELEPORT_USED(pl, goalcurrent)
        // where TELEPORT_USED tests the player's hull AT THE MOMENT IT WAS TELEPORTED against the waypoint box
        // (navigation.qh:65). Popping on proximity alone is how a bot ends up dancing at a jumppad mouth: it
        // "reaches" the pad, drops the goal, walks off, re-routes back, repeats. The jumppad case additionally
        // holds the pop for a short random delay, because some pads need an extra run-up and popping instantly
        // strands the bot on the pad. See planning/bot-ai-parity-2026-08-03.md D18.
        if ((goal.Flags & WaypointFlags.Teleport) != 0 && goal.Wp is { } tw)
        {
            if (bot.LastTeleportTime <= 0f) return false;
            Vector3 lo = tw.AbsMin, hi = tw.AbsMax;
            Vector3 pmin = bot.LastTeleportOrigin + Mins, pmax = bot.LastTeleportOrigin + Maxs;
            bool used = lo.X <= pmax.X && hi.X >= pmin.X
                     && lo.Y <= pmax.Y && hi.Y >= pmin.Y
                     && lo.Z <= pmax.Z && hi.Z >= pmin.Z;
            if (!used) return false;

            // QC navigation.qc:1660-1669: jumppad pop delay (0.1s, halved when already launched fast).
            if (bot.JumpPadCount > 0)
            {
                float maxDelay = new Vector2(bot.Velocity.X, bot.Velocity.Y).Length() > 2f * MaxSpeed
                    ? 0.05f : 0.1f;
                if (Now - bot.LastTeleportTime < (float)_kbRng.NextDouble() * maxDelay)
                    return false;
            }
            return true;
        }

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
