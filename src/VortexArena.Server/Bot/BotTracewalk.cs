using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// The bot reachability test — a faithful, Godot-free port of QuakeC's <c>tracewalk</c>
/// (server/bot/default/navigation.qc): "a rough simulation of walking from one point to another to test if a
/// path can be traveled", used for both waypoint auto-linking and the bot's straight-to-goal shortcut.
///
/// It steps a player-hull tracebox along the flat direction from start to end in 32-unit increments, and at
/// each step handles the three QC navigation actions:
///  - <b>WALK</b>: tracebox forward; on a wall, retry stepped up by <c>stepheight</c> (a stair) then by
///    <c>jumpstepheight</c> (a jumpable lip); on success, trace straight down to stand on the ground
///    (the Quake walkmove logic), so stairs and ledges are climbed/descended like a real player;
///  - <b>SWIM_ONWATER</b> / <b>SWIM_UNDERWATER</b>: step toward the (vertically-clamped) end, stepswimming
///    over obstacles and resurfacing when blocked, so water gaps are crossed.
///
/// It returns true once the walker arrives within 1 unit of the destination height (or anywhere in the
/// vertical band [end, end+endHeight] when a box waypoint is the target), false if it gets stuck. This is
/// the deep tracewalk the bot navigation TODO calls for — it replaces the old single straight hull sweep.
/// </summary>
public static class BotTracewalk
{
    // QC step constants (stepheightvec.z = sv_stepheight = 34; jumpstepheightvec adds a jump's worth).
    private const float StepHeight = 34f;
    private const float JumpStepHeight = 48f;  // QC jumpstepheightvec.z (stepheight + a small jump lift)
    private const float JumpHeight = 130f;     // QC jumpheight_vec.z (apparent jump apex)
    private const float StepDist = 32f;        // QC stepdist
    private const int MaxIterations = 256;     // safety cap (a long path is many 32u steps)

    private enum NavAction { Walk, SwimOnWater, SwimUnderwater }

    // ---- per-tick strategy trace budget (variance program 2026-07-11) ------------------------------------
    // BUDGETED walks (maxWalkDistance > 0 — strategy-time seed/rating/straight-shot checks) share one pool of
    // hull traces per server tick, re-armed by BotPopulation.ServerFrame at StartFrame. When the pool runs
    // dry the remaining budgeted walks report UNREACHABLE: the strategy layer already treats a failed pass as
    // retry-on-interval (the 2s expire from the melt fixes), so the cost of a hot tick is capped at
    // ~TickTraceBudget × trace-cost (~1.5ms) instead of an unbounded pass (measured Release tails: 17-19ms).
    // UNBUDGETED walks (AutoLink graph building, offline tools) never touch the pool. Sim-thread only.
    // int.MaxValue until first armed: harness/bench callers without a world loop keep full QC behavior.
    internal const int TickTraceBudget = 96;
    // Per-WALK iteration cap in budgeted mode: 48 steps ≈ 1536qu of actual walking — full coverage for the
    // common nearby-seed case (most entry walks are < 750qu ≈ 24 steps), while clipping the expensive failure
    // mode where a walk WANDERS its whole flat distance before failing arrival (the all-candidates-unreachable
    // pass: 12 walks × ~70 steps × ~20-80µs/step was the measured 15-18ms bot.seed tail). Unbudgeted mode keeps
    // the full QC-scale MaxIterations.
    private const int BudgetedMaxIterations = 48;
    private static int _tickTracesLeft = int.MaxValue;
    internal static void ResetTickBudget() => _tickTracesLeft = TickTraceBudget;

    // True once this tick's pool is spent: any budgeted CanWalk from here on reports UNREACHABLE without
    // walking. Callers that CACHE a reachability verdict must check this — a starved answer is transient
    // (the pool re-arms next tick) and must not be persisted (see Waypoint.NearestForGoal).
    internal static bool TickBudgetSpent => _tickTracesLeft <= 0;

    /// <summary>
    /// QC <c>tracewalk(e, start, m1, m2, end, end_height, movemode)</c>: can a player hull
    /// (<paramref name="mins"/>/<paramref name="maxs"/>) walk/step/swim from <paramref name="start"/> to
    /// <paramref name="end"/>? <paramref name="endHeight"/> &gt; 0 makes the destination the vertical segment
    /// [end, end + endHeight·z] (a box-waypoint target). Ignores <paramref name="ignore"/> in the traces.
    ///
    /// <paramref name="maxWalkDistance"/> &gt; 0 = STRATEGY-BUDGETED mode (variance program, bot-tick tail):
    /// (1) a target farther than this on the flat is rejected up front — a "direct walk" beyond it is never a
    /// useful strategy answer (the waypoint router owns long routes), and the full walk there is exactly the
    /// 256-step × 4-trace melt the r16 sessions measured; (2) the per-step find-the-floor down-trace is bounded
    /// to a jumpable-descent reach instead of QC's 65536u full-map column sweep — a deeper fall reports
    /// UNREACHABLE (conservative: big-drop routes ride waypoint links, which are built in unbounded mode).
    /// Graph building (AutoLink) MUST keep the default 0 = exact QC semantics, or cached waypoint links change.
    /// </summary>
    public static bool CanWalk(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs,
        float endHeight = 0f, Entity? ignore = null, float maxWalkDistance = 0f)
    {
        if (Api.Services is null)
            return true; // no collision world: optimistically reachable (offline graph build)

        // Bad start: the hull is stuck in solid where it begins.
        TraceResult t0 = Box(start, mins, maxs, start, ignore);
        if (t0.StartSolid)
            return false;

        Vector3 org = start;
        Vector3 flatDir = end - start;
        flatDir.Z = 0f;
        float flatDist = flatDir.Length();
        flatDir = flatDist > 0f ? flatDir / flatDist : Vector3.Zero;

        bool budgeted = maxWalkDistance > 0f;
        if (budgeted && flatDist > maxWalkDistance)
            return false; // strategy pre-gate: too far for a direct walk to ever be the right answer
        if (budgeted && _tickTracesLeft <= 0)
            return false; // this tick's strategy trace pool is spent — the pass retries on its interval
        // Budgeted floor search: enough for stairs and jumpable-scale descents along a sane direct path —
        // and deliberately SHORT: this sweep runs once per step and its length dominates the step's trace cost
        // (the 65536u column sweep was the single most expensive query in the measured strategy tails).
        // Unbounded keeps QC's full column drop (auto-link parity — long falls ARE valid link paths).
        float downReach = budgeted ? 200f : 65536f;

        Vector3 end2 = end;
        if (endHeight > 0f) end2.Z += endHeight;
        Vector3 fixedEnd = end;

        var stepVec = new Vector3(0f, 0f, StepHeight);
        var jumpStepVec = new Vector3(0f, 0f, JumpStepHeight);
        var jumpVec = new Vector3(0f, 0f, JumpHeight);

        // Budgeted walks charge every collision query to the per-tick strategy pool (see TickTraceBudget) —
        // hull traces AND the per-step water PointContents pair (uncharged, those were the pool leak: the
        // budget-1 vs budget-200 A/B put ~half the measured iteration cost outside the hull traces).
        TraceResult BoxC(Vector3 from, Vector3 to)
        {
            if (budgeted) _tickTracesLeft--;
            return Box(from, mins, maxs, to, ignore);
        }
        NavAction WaterState(Vector3 at)
        {
            if (budgeted) _tickTracesLeft -= 2;
            return WetFeet(at) ? (Submerged(at) ? NavAction.SwimUnderwater : NavAction.SwimOnWater) : NavAction.Walk;
        }

        // Pick the initial nav action from the start's water state.
        NavAction action = WetFeet(org) ? (Submerged(org) ? NavAction.SwimUnderwater : NavAction.Walk) : NavAction.Walk;

        int iterCap = budgeted ? BudgetedMaxIterations : MaxIterations;
        for (int iter = 0; iter < iterCap; iter++)
        {
            if (budgeted && _tickTracesLeft <= 0)
                return false; // tick pool spent mid-walk — conservative unreachable (the pass retries on interval)

            // --- arrival check (the flatdist<=0 block in QC) ---
            if (flatDist <= 0f)
            {
                bool success = true;
                if (org.Z > end2.Z + 1f)
                {
                    TraceResult t = BoxC(org, end2);
                    org = t.EndPos;
                    if (org.Z > end2.Z + 1f) success = false;
                }
                else if (org.Z < end.Z - 1f)
                {
                    TraceResult t = BoxC(org, org - jumpVec);
                    org = t.EndPos;
                    if (org.Z < end.Z - 1f) success = false;
                }
                if (success)
                    return true;
                if (flatDist <= 0f)
                    break; // can't advance further and not arrived
            }

            // compute the next step target.
            Vector3 move;
            if (action == NavAction.SwimUnderwater || (action == NavAction.SwimOnWater && org.Z > end2.Z))
            {
                fixedEnd.Z = Clamp(org.Z, end.Z, end2.Z);
                float seg = MathF.Min(StepDist, flatDist);
                if (seg >= flatDist) { move = fixedEnd; flatDist = 0f; }
                else
                {
                    move = org + (fixedEnd - org) * (StepDist / flatDist);
                    var rem = new Vector3(fixedEnd.X - move.X, fixedEnd.Y - move.Y, 0f);
                    flatDist = rem.Length();
                }
            }
            else
            {
                float seg = MathF.Min(StepDist, flatDist);
                flatDist -= seg;
                move = org + flatDir * seg;
            }

            // --- WALK ---
            if (action == NavAction.Walk)
            {
                TraceResult t = BoxC(org, move);
                if (t.Fraction < 1f)
                {
                    // wall: try stepping up by stepheight (a stair).
                    TraceResult ts = BoxC(org + stepVec, move + stepVec);
                    if (ts.Fraction < 1f || ts.StartSolid)
                    {
                        // try a bigger jumpstep lip.
                        TraceResult tj = BoxC(org + jumpStepVec, move + jumpStepVec);
                        if (tj.Fraction < 1f && !tj.StartSolid)
                            return false; // genuinely blocked (no ladder/door handling in this slice)
                        move = tj.StartSolid ? ts.EndPos : tj.EndPos;
                    }
                    else move = ts.EndPos;
                }
                else move = t.EndPos;

                // stand on the ground: trace straight down as far as possible (QC walkmove logic).
                TraceResult down = BoxC(move, move - new Vector3(0f, 0f, downReach));
                if (budgeted && down.Fraction >= 1f)
                    return false; // no floor within the budgeted reach: a deeper-than-jumpable fall — not a strategy walk
                org = down.EndPos;

                // entered water while walking? switch to swimming.
                NavAction ws = WaterState(org);
                if (ws != NavAction.Walk)
                    action = ws;
                continue;
            }

            // --- SWIM (on/under water): step toward the clamped target, stepswim over small obstacles ---
            TraceResult sw = BoxC(org, move);
            if (sw.Fraction < 1f)
            {
                TraceResult ss = BoxC(org + stepVec, move + stepVec);
                if (ss.Fraction < 1f || ss.StartSolid)
                    return false; // can't jump the obstacle out of water
                org = ss.EndPos;
            }
            else org = sw.EndPos;

            // resolve the new water state after the swim step.
            action = WaterState(org);
            if (flatDist <= 0f && Approximately(org, end, end2))
                return true;
        }
        return false;
    }

    // ---- water helpers (QC WETFEET / SUBMERGED via PointContents) ----

    private static bool WetFeet(Vector3 org)
    {
        // QC WETFEET: the point a little above the feet is in water (pointcontents <= CONTENT_WATER).
        int c = Api.Trace.PointContents(org + new Vector3(0f, 0f, 1f));
        return IsWater(c);
    }

    private static bool Submerged(Vector3 org)
    {
        // QC SUBMERGED: the head (eye level) is in water too.
        int c = Api.Trace.PointContents(org + new Vector3(0f, 0f, 40f));
        return IsWater(c);
    }

    private static bool IsWater(int contents)
    {
        // Engine SUPERCONTENTS water bit OR the legacy CONTENT_WATER/SLIME/LAVA range.
        const int superContentsWater = 0x00000020; // SUPERCONTENTS_WATER
        const int superContentsLiquids = 0x00000020 | 0x00000010 | 0x00000008; // water|slime|lava
        if ((contents & superContentsLiquids) != 0) return true;
        return contents <= (int)Contents.Water && contents >= (int)Contents.Lava;
    }

    private static bool Approximately(Vector3 org, Vector3 end, Vector3 end2)
        => org.Z <= end2.Z + 1f && org.Z >= end.Z - 1f
           && new Vector3(end.X - org.X, end.Y - org.Y, 0f).LengthSquared() < 4f;

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static TraceResult Box(Vector3 from, Vector3 mins, Vector3 maxs, Vector3 to, Entity? ignore)
        => Api.Trace.Trace(from, mins, maxs, to, MoveFilter.NoMonsters, ignore);
}
