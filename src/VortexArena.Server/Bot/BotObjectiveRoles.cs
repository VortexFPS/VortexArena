using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;

namespace VortexArena.Server.Bot;

/// <summary>
/// Per-gametype objective bot roles — the C# port of the havocbot_role_* / havocbot_goalrating_* objective
/// functions that live in each gametype's sv_*.qc (ctf/keyhunt/domination/onslaught/keepaway). Each role
/// rates the gametype's objective targets (the enemy flag, a dropped key, an unclaimed control point, the
/// enemy generator, the keepaway ball) alongside items and roam waypoints, then the brain routes to the best.
///
/// The objective <em>positions</em> come from two sources: the gametype singleton's own state (e.g. CTF's
/// <see cref="Ctf.Flags"/> for carriers/drops) and the spawned map-marker entities found by classname through
/// the (player-visible) entity table — control points (<c>dom_controlpoint</c>), generators
/// (<c>onslaught_generator</c>), flag bases (<c>item_flag_team*</c>) etc. A role with no resolvable objective
/// state gracefully falls back to <see cref="BotRoles.RoleGeneric"/> so the bot still plays.
/// </summary>
public static class BotObjectiveRoles
{
    // QC BOT_RATING_* (roles.qh) — objective goals weigh much higher than loose items.
    // (CTF uses the QC per-role literal scales directly — see the CTF role bodies.)
    private const float RatingControlPoint = 10000f;
    private const float RatingBall = 8000f;
    // QC havocbot_goalrating_tkaball ratingscale_sameteam: chasing your OWN team's carrier rates lower (TKA only).
    private const float RatingBallSameTeam = 4000f;
    private const float RatingGenerator = 20000f;
    private const float RatingKey = 10000f;
    // QC havocbot_role_cts: navigation_routerating(this, it, 1000000, 5000) — the next race checkpoint dwarfs every
    // other goal (value 1000000, rangebias 5000) so a CTS bot single-mindedly runs the course start→finish.
    private const float RatingRaceCheckpoint = 1000000f;

    /// <summary>Scratch buffer reused across the objective FindByClass scans (alloc-free, replaces a per-call
    /// iterator). Bot roles run single-threaded under the strategy token and never re-enter, and every scan here
    /// is fully consumed (return-on-match / accumulate into the rater) before the next reuses it — so one shared
    /// static list is safe. Do NOT hold a reference to it across a second find.</summary>
    private static readonly List<Entity> _scratch = new();

    // ============================================================================================
    // Capture the Flag (QC havocbot_role_ctf_* + havocbot_goalrating_ctf_* — sv_ctf.qc:1572-2217)
    // ============================================================================================

    /// <summary>
    /// CTF role dispatcher — the FULL six-role QC state machine (carrier / retriever / middle / offense /
    /// defense / escort), not a collapse. Runs every token hold: role transitions (grab/drop/steal/return
    /// reactions, timeouts, team position balancing) happen at token cadence exactly like QC havocbot_ai:64,
    /// while each role's goal rating is gated on the strategy clock (<see cref="BotBrain.GoalRatingTimedOut"/>).
    /// Role state lives on the brain (<see cref="BotBrain.CtfRole"/> / CtfPreviousRole / CtfRoleTimeout).
    /// </summary>
    public static void RoleCtf(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        if (brain.GameType is not Ctf ctf || ctf.Flags.Count == 0)
        {
            BotRoles.RoleGeneric(brain, rater); // no CTF state resolvable — still play (gates itself)
            return;
        }

        int myTeam = (int)bot.Team;
        FlagState? mf = FindFlag(ctf, myTeam, ours: true);
        FlagState? ef = FindFlag(ctf, myTeam, ours: false);
        if (mf is null || ef is null)
        {
            BotRoles.RoleGeneric(brain, rater);
            return;
        }

        // QC havocbot_ctf_calculate_middlepoint (sv_ctf.qc:1480): middle = mean of the flag base origins;
        // radius = distance from a base to the middle. Cheap (2-4 flags) — recompute per invocation.
        CtfMiddle(ctf, out Vector3 middle, out float middleRadius);

        if (brain.CtfRole == CtfBotRole.None)
            CtfResetRole(brain, ctf, mf, ef, middle, middleRadius, initial: true);

        switch (brain.CtfRole)
        {
            case CtfBotRole.Carrier:   CtfRoleCarrier(brain, rater, ctf, mf, ef, middle, middleRadius); break;
            case CtfBotRole.Retriever: CtfRoleRetriever(brain, rater, ctf, mf, ef, middle, middleRadius); break;
            case CtfBotRole.Middle:    CtfRoleMiddle(brain, rater, ctf, mf, ef, middle, middleRadius); break;
            case CtfBotRole.Offense:   CtfRoleOffense(brain, rater, ctf, mf, ef, middle, middleRadius); break;
            case CtfBotRole.Escort:    CtfRoleEscort(brain, rater, ctf, mf, ef, middle, middleRadius); break;
            default:                   CtfRoleDefense(brain, rater, ctf, mf, ef, middle, middleRadius); break;
        }
    }

    /// <summary>
    /// QC havocbot_ctf_find_flag / _find_enemy_flag: our team's flag, or the flag to take. In one-flag mode
    /// the "enemy" flag is the NEUTRAL flag (Teams.None) — the thing bots steal and run home. (QC's oneflag
    /// find_enemy_flag returns an enemy TEAM flag while carrying, but its own capture rule fires at the
    /// carrier's OWN base — sv_ctf.qc:1186, pinned by the port's OneFlag tests — so the port routes carriers
    /// via <c>mf</c> (the capture base) and always tracks the neutral flag here.)
    /// </summary>
    private static FlagState? FindFlag(Ctf ctf, int myTeam, bool ours)
    {
        if (!ours && ctf.OneFlag)
            return ctf.NeutralFlag;
        foreach (var (team, flag) in ctf.Flags)
        {
            if (team == Teams.None) continue; // the neutral flag is nobody's team flag
            if ((team == myTeam) == ours)
                return flag;
        }
        return null;
    }

    /// <summary>Where the flag currently IS (QC the flag edict origin: carrier while carried, drop spot
    /// while dropped, home base otherwise).</summary>
    private static Vector3 FlagPosition(FlagState flag)
    {
        if (flag.Status == FlagStatus.Carried && flag.Carrier is not null)
            return flag.Carrier.Origin;
        if (flag.Status == FlagStatus.Dropped)
            return flag.DropOrigin;
        return flag.HomeOrigin;
    }

    private static void CtfMiddle(Ctf ctf, out Vector3 middle, out float radius)
    {
        Vector3 s = Vector3.Zero, last = Vector3.Zero;
        int n = 0;
        foreach (var (_, flag) in ctf.Flags)
        {
            last = flag.HomeOrigin;
            s += last;
            ++n;
        }
        middle = n > 0 ? s * (1f / n) : Vector3.Zero;
        radius = n > 0 ? (last - middle).Length() : 0f;
        if (radius <= 0f) radius = 1000f; // degenerate single-flag map: keep the radius terms sane
    }

    /// <summary>QC havocbot_ctf_teamcount: LIVING teammates (≠ this bot) within <paramref name="radius"/> of
    /// <paramref name="org"/>.</summary>
    private static int CtfTeamCount(BotBrain brain, Vector3 org, float radius)
    {
        int c = 0;
        float r2 = radius * radius;
        foreach (Entity e in brain.LivePlayers())
        {
            if (ReferenceEquals(e, brain.Bot) || e is not Player p || p.IsDead) continue;
            if ((int)p.Team != (int)brain.Bot.Team) continue;
            if ((p.Origin - org).LengthSquared() < r2) ++c;
        }
        return c;
    }

    /// <summary>
    /// QC havocbot_role_ctf_setrole (sv_ctf.qc:2171): the role switch bookkeeping — previous-role capture for
    /// the temporary roles, per-role timeouts, and the goal-rating force/expire that makes a switch take
    /// effect promptly instead of waiting out the 7s strategy clock.
    /// </summary>
    private static void CtfSetRole(BotBrain brain, CtfBotRole role)
    {
        float now = Api.Clock.Time;
        switch (role)
        {
            case CtfBotRole.Carrier:
                if (brain.CtfRole != CtfBotRole.Carrier)
                    brain.ForceGoalRating(); // QC: navigation_goalrating_timeout_force on entering carrier
                brain.CtfRole = role;
                brain.CtfRoleTimeout = 0f;
                brain.CtfCantFindFlagTime = now + 10f; // QC bot.havocbot_cantfindflag = time + 10
                break;
            case CtfBotRole.Retriever:
                brain.CtfPreviousRole = brain.CtfRole;
                if (brain.CtfRole != CtfBotRole.Retriever)
                    brain.ExpireGoalRating(2f); // QC: navigation_goalrating_timeout_expire(bot, 2)
                brain.CtfRole = role;
                brain.CtfRoleTimeout = now + 10f;
                break;
            case CtfBotRole.Escort:
                brain.CtfPreviousRole = brain.CtfRole;
                if (brain.CtfRole != CtfBotRole.Escort)
                    brain.ExpireGoalRating(2f);
                brain.CtfRole = role;
                brain.CtfRoleTimeout = now + 30f;
                break;
            default: // Defense / Middle / Offense
                brain.CtfRole = role;
                brain.CtfRoleTimeout = 0f;
                break;
        }
    }

    /// <summary>
    /// QC havocbot_ctf_reset_role (sv_ctf.qc:1697): pick the best role from the game state — carrier while
    /// carrying, retriever while our flag is away, middle while the enemy flag is away, else balance
    /// defense/offense/middle by where teammates already stand. <paramref name="initial"/> stands in for the
    /// QC "just joined" (jointime) window: fixed first roles for tiny teams and a 10-20s shakeout timeout.
    /// </summary>
    private static void CtfResetRole(BotBrain brain, Ctf ctf, FlagState mf, FlagState ef,
        Vector3 middle, float middleRadius, bool initial = false)
    {
        var bot = brain.Bot;
        if (bot.IsDead) return;

        if (ctf.CarriedBy(bot) is not null) { CtfSetRole(brain, CtfBotRole.Carrier); return; }
        if (mf.Status != FlagStatus.AtBase) { CtfSetRole(brain, CtfBotRole.Retriever); return; }
        if (ef.Status != FlagStatus.AtBase) { CtfSetRole(brain, CtfBotRole.Middle); return; }

        // Count teammates (QC counts all players on the team, dead or not).
        int count = 0;
        foreach (Entity e in brain.LivePlayers())
            if (!ReferenceEquals(e, bot) && (int)e.Team == (int)bot.Team) ++count;

        if (count == 0) { CtfSetRole(brain, CtfBotRole.Offense); return; }
        if (initial)
        {
            // QC "if bots spawn all at once set good default roles" (jointime < 1s).
            if (count == 1) { CtfSetRole(brain, CtfBotRole.Defense); return; }
            if (count == 2) { CtfSetRole(brain, CtfBotRole.Middle); return; }
        }

        // Evaluate the least-covered position (QC teamcount over middle / our base / enemy base).
        int cmiddle = CtfTeamCount(brain, middle, middleRadius * 0.5f);
        int cdefense = CtfTeamCount(brain, mf.HomeOrigin, middleRadius * 0.5f);
        int coffense = CtfTeamCount(brain, ef.HomeOrigin, middleRadius);

        if (cdefense <= coffense) CtfSetRole(brain, CtfBotRole.Defense);
        else if (coffense <= cmiddle) CtfSetRole(brain, CtfBotRole.Offense);
        else CtfSetRole(brain, CtfBotRole.Middle);

        // QC: staggered re-evaluation when a full team spawned at once.
        if (initial && count > 2)
            brain.CtfRoleTimeout = Api.Clock.Time + 10f + (float)Prandom.Float() * 10f;
    }

    /// <summary>
    /// [PORT IMPROVEMENT] Pass the carried flag to the best same-team receiver: alive, empty-handed, inside
    /// g_ctf_pass_radius, with a clear straight line (the port's pass flies straight — no arc trace). Nearest
    /// wins. Returns true when a pass actually launched (<see cref="Ctf.PassFlag"/> applies the full rule set:
    /// pass enabled, antispam, receiver validity).
    /// </summary>
    private static bool TryCarrierPass(BotBrain brain, Ctf ctf)
    {
        if (brain.Bot is not Player self || Api.Services is null) return false;
        float radius = Cvars.FloatOr("g_ctf_pass_radius", 500f);
        Player? best = null;
        float bestD = radius;
        Vector3 eye = self.Origin + self.ViewOfs;
        foreach (Entity e in brain.LivePlayers())
        {
            if (e is not Player p || ReferenceEquals(p, self) || p.IsDead) continue;
            if ((int)p.Team != (int)self.Team) continue;
            if (ctf.CarriedBy(p) is not null) continue; // QC: can't pass to another carrier
            float d = (p.Origin - self.Origin).Length();
            if (d > bestD) continue;
            var tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, p.Origin + p.ViewOfs,
                MoveFilter.Normal, self);
            if (tr.Fraction < 1f && !ReferenceEquals(tr.Ent, p)) continue; // wall in the way
            best = p;
            bestD = d;
        }
        return best is not null && ctf.PassFlag(self, best);
    }

    // ---- CTF goal-rating helpers (QC havocbot_goalrating_ctf_*) ----

    /// <summary>QC havocbot_goalrating_ctf_ourbase / _enemybase: route toward a flag base (QC rates the
    /// bot_basewaypoint; the port rates the base position directly).</summary>
    private static void CtfRateBase(BotBrain brain, GoalRater rater, FlagState flag, float ratingscale)
    {
        Entity? target = flag.Status == FlagStatus.AtBase ? flag.Entity : null;
        rater.Rate(brain.Bot.Origin, target, flag.HomeOrigin, ratingscale, 10000f);
    }

    /// <summary>QC havocbot_goalrating_ctf_enemyflag: route to the enemy flag wherever it is; when a TEAMMATE
    /// carries it, nudge the rating by the carrier's health (escort healthy carriers harder: f = ((hp+armor)/100
    /// clamped 0..2) - 1; scale += scale·f·0.1).</summary>
    private static void CtfRateEnemyFlag(BotBrain brain, GoalRater rater, FlagState ef, float ratingscale)
    {
        var bot = brain.Bot;
        if (ef.Status == FlagStatus.Carried && ef.Carrier is Player carrier)
        {
            float hp = carrier.Health + carrier.GetResource(ResourceType.Armor);
            float f = System.Math.Clamp(hp / 100f, 0f, 2f) - 1f;
            ratingscale += ratingscale * f * 0.1f;
            rater.Rate(bot.Origin, carrier, carrier.Origin, ratingscale, 10000f);
            return;
        }
        rater.Rate(bot.Origin, ef.Entity, FlagPosition(ef), ratingscale, 10000f);
    }

    /// <summary>QC havocbot_goalrating_ctf_ourstolenflag: route to OUR flag's carrier while it is carried
    /// (the dropped case is handled by <see cref="CtfRateDroppedFlags"/>).</summary>
    private static void CtfRateOurStolenFlag(BotBrain brain, GoalRater rater, FlagState mf, float ratingscale)
    {
        if (mf.Status == FlagStatus.Carried && mf.Carrier is not null)
            rater.Rate(brain.Bot.Origin, mf.Carrier, mf.Carrier.Origin, ratingscale, 10000f);
    }

    /// <summary>QC havocbot_goalrating_ctf_droppedflags: route to any flag lying dropped in the field within
    /// <paramref name="radius"/> of <paramref name="org"/> (0 = unlimited).</summary>
    private static void CtfRateDroppedFlags(BotBrain brain, GoalRater rater, Ctf ctf, float ratingscale,
        Vector3 org, float radius)
    {
        float r2 = radius * radius;
        foreach (var (_, flag) in ctf.Flags)
        {
            if (flag.Status != FlagStatus.Dropped) continue;
            if (radius > 0f && (flag.DropOrigin - org).LengthSquared() >= r2) continue;
            rater.Rate(brain.Bot.Origin, flag.Entity, flag.DropOrigin, ratingscale, 10000f);
        }
    }

    // ---- the six CTF roles (QC sv_ctf.qc:1788-2169) ----

    /// <summary>QC havocbot_role_ctf_carrier: run the enemy flag home. When our flag is away the capture
    /// can't complete — the base pull drops to 2000/1000 so nearby items win and the bot loiters at base.</summary>
    private static void CtfRoleCarrier(BotBrain brain, GoalRater rater, Ctf ctf, FlagState mf, FlagState ef,
        Vector3 middle, float middleRadius)
    {
        var bot = brain.Bot;
        if (ctf.CarriedBy(bot) is null) { CtfResetRole(brain, ctf, mf, ef, middle, middleRadius); return; }

        // [PORT IMPROVEMENT — Base bots never pass] A carrier about to die hands the flag off to a teammate
        // in pass range with a clear line, keeping the run alive instead of dumping the flag on death.
        // Runs at token cadence; PassFlag's own antispam (g_ctf_pass_wait) rate-limits retries. Skill-gated
        // so low-skill bots keep their tunnel vision.
        if (brain.Skill >= 3f && bot.Health + bot.GetResource(ResourceType.Armor) < 40f
            && TryCarrierPass(brain, ctf))
        {
            CtfResetRole(brain, ctf, mf, ef, middle, middleRadius); // no longer carrying
            return;
        }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        Vector3 baseOrg = mf.HomeOrigin;
        float baseRating = mf.Status == FlagStatus.AtBase
            ? 10000f
            : ((bot.Origin - baseOrg).Length() > 100f ? 2000f : 1000f);
        CtfRateBase(brain, rater, mf, baseRating);

        // QC: collect items very close by, but only inside our base radius; plus the base-area items.
        if ((bot.Origin - baseOrg).Length() < middleRadius)
            BotRoles.GoalrateItems(brain, rater, bot.Origin, System.MathF.Min(500f, middleRadius * 0.5f), 15000f);
        BotRoles.GoalrateItems(brain, rater, baseOrg, middleRadius * 0.5f, 10000f);
        rater.End();

        // QC havocbot_cantfindflag watchdog: a carrier that can't rate ANY goal for 10s suicides in Base
        // (Damage DEATH_KILL) so the flag returns to play. The port has no bot-layer suicide path — clear the
        // route instead, and re-arm so this doesn't spin every token frame.
        if (rater.HasGoal)
            brain.CtfCantFindFlagTime = Api.Clock.Time + 10f;
        else if (Api.Clock.Time > brain.CtfCantFindFlagTime)
        {
            // [PORT IMPROVEMENT] Before giving the route search another spin, try to PASS the flag to a
            // reachable teammate — a stuck carrier handing off beats QC's suicide and beats circling forever.
            TryCarrierPass(brain, ctf);
            brain.Nav.ClearRoute();
            // NOTE: deliberately NO ForceGoalRating() here. We are inside the rating pass, and the post-pass
            // stamp in BotBrain unconditionally clears the force flag — the call was a silent no-op. The fast
            // retry it was reaching for already exists: this pass rated nothing and the route was just cleared,
            // so BotBrain's "no goal captured and no route" arm re-arms the rating in 2s.
            brain.CtfCantFindFlagTime = Api.Clock.Time + 10f;
        }
        // (QC also LOCKS the chosen goal for 2-3s when parked on the base waypoint waiting for our flag —
        // the port's 7s re-rate cadence makes an explicit lock a no-op, so it is intentionally omitted.)
    }

    /// <summary>QC havocbot_role_ctf_escort: shadow our flag carrier until the enemy flag is home again.</summary>
    private static void CtfRoleEscort(BotBrain brain, GoalRater rater, Ctf ctf, FlagState mf, FlagState ef,
        Vector3 middle, float middleRadius)
    {
        var bot = brain.Bot;
        float now = Api.Clock.Time;
        if (ctf.CarriedBy(bot) is not null) { CtfSetRole(brain, CtfBotRole.Carrier); return; }

        // Enemy flag back at its base → job done, revert to the previous role.
        if (ef.Status == FlagStatus.AtBase)
        {
            brain.CtfRole = brain.CtfPreviousRole != CtfBotRole.None ? brain.CtfPreviousRole : CtfBotRole.None;
            brain.CtfRoleTimeout = 0f;
            return;
        }
        // Enemy flag dropped → re-rate soon (someone must scoop it; roles re-sort on the next pass).
        if (ef.Status == FlagStatus.Dropped)
        {
            brain.ExpireGoalRating(1f);
            return;
        }
        // Our carrier reached (the vicinity of) our base but can't cap (our flag away) → switch to defense.
        if (mf.Status != FlagStatus.AtBase && (FlagPosition(ef) - mf.HomeOrigin).Length() < 900f)
        {
            CtfSetRole(brain, CtfBotRole.Defense);
            return;
        }

        if (brain.CtfRoleTimeout == 0f)
            brain.CtfRoleTimeout = now + (float)Prandom.Float() * 30f + 60f;
        if (now > brain.CtfRoleTimeout)
        {
            brain.CtfRole = brain.CtfPreviousRole != CtfBotRole.None ? brain.CtfPreviousRole : CtfBotRole.None;
            brain.CtfRoleTimeout = 0f;
            return;
        }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        CtfRateEnemyFlag(brain, rater, ef, 10000f);
        CtfRateOurStolenFlag(brain, rater, mf, 6000f);
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 21000f);
        rater.End();
    }

    /// <summary>QC havocbot_role_ctf_offense: push the enemy base; peel off to retrieve or escort as the
    /// flag states demand.</summary>
    private static void CtfRoleOffense(BotBrain brain, GoalRater rater, Ctf ctf, FlagState mf, FlagState ef,
        Vector3 middle, float middleRadius)
    {
        var bot = brain.Bot;
        float now = Api.Clock.Time;
        if (ctf.CarriedBy(bot) is not null) { CtfSetRole(brain, CtfBotRole.Carrier); return; }

        // Our flag stolen and closer than the enemy base → go retrieve instead.
        if (mf.Status != FlagStatus.AtBase)
        {
            Vector3 pos = FlagPosition(mf);
            if ((bot.Origin - ef.HomeOrigin).LengthSquared() > (bot.Origin - pos).LengthSquared())
            {
                CtfSetRole(brain, CtfBotRole.Retriever);
                return;
            }
        }
        // Enemy flag taken and already away from our base → escort the carrier in.
        if (ef.Status != FlagStatus.AtBase && (FlagPosition(ef) - mf.HomeOrigin).Length() > 700f)
        {
            CtfSetRole(brain, CtfBotRole.Escort);
            return;
        }

        if (brain.CtfRoleTimeout == 0f)
            brain.CtfRoleTimeout = now + 120f;
        if (now > brain.CtfRoleTimeout) { CtfResetRole(brain, ctf, mf, ef, middle, middleRadius); return; }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        CtfRateOurStolenFlag(brain, rater, mf, 10000f);
        CtfRateBase(brain, rater, ef, 10000f); // QC havocbot_goalrating_ctf_enemybase(10000)
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 22000f);
        rater.End();
    }

    /// <summary>QC havocbot_role_ctf_retriever (temporary): hunt our stolen/dropped flag down.</summary>
    private static void CtfRoleRetriever(BotBrain brain, GoalRater rater, Ctf ctf, FlagState mf, FlagState ef,
        Vector3 middle, float middleRadius)
    {
        var bot = brain.Bot;
        float now = Api.Clock.Time;
        if (ctf.CarriedBy(bot) is not null) { CtfSetRole(brain, CtfBotRole.Carrier); return; }

        // Flag home again → back to normal duties (QC also force-rerates the bot that returned it; the port
        // doesn't track the returner, so the reset's own expire covers it).
        if (mf.Status == FlagStatus.AtBase) { CtfResetRole(brain, ctf, mf, ef, middle, middleRadius); return; }

        if (brain.CtfRoleTimeout == 0f)
            brain.CtfRoleTimeout = now + 20f;
        if (now > brain.CtfRoleTimeout) { CtfResetRole(brain, ctf, mf, ef, middle, middleRadius); return; }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        const float RtRadius = 10000f;
        CtfRateOurStolenFlag(brain, rater, mf, 10000f);
        CtfRateDroppedFlags(brain, rater, ctf, 12000f, bot.Origin, RtRadius);
        CtfRateBase(brain, rater, ef, 8000f);
        // QC: collect items very close by, but only inside the ENEMY base radius; plus the wider sweep.
        if ((bot.Origin - ef.HomeOrigin).Length() < middleRadius)
            BotRoles.GoalrateItems(brain, rater, bot.Origin, System.MathF.Min(500f, middleRadius * 0.5f), 27000f);
        BotRoles.GoalrateItems(brain, rater, bot.Origin, middleRadius, 18000f);
        rater.End();
    }

    /// <summary>QC havocbot_role_ctf_middle: hold the map middle — intercept runners, farm the mid items.</summary>
    private static void CtfRoleMiddle(BotBrain brain, GoalRater rater, Ctf ctf, FlagState mf, FlagState ef,
        Vector3 middle, float middleRadius)
    {
        var bot = brain.Bot;
        float now = Api.Clock.Time;
        if (ctf.CarriedBy(bot) is not null) { CtfSetRole(brain, CtfBotRole.Carrier); return; }
        if (mf.Status != FlagStatus.AtBase) { CtfSetRole(brain, CtfBotRole.Retriever); return; }

        if (brain.CtfRoleTimeout == 0f)
            brain.CtfRoleTimeout = now + 10f;
        if (now > brain.CtfRoleTimeout) { CtfResetRole(brain, ctf, mf, ef, middle, middleRadius); return; }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        Vector3 org = middle;
        org.Z = bot.Origin.Z; // QC: org = havocbot_middlepoint; org.z = this.origin.z
        CtfRateOurStolenFlag(brain, rater, mf, 8000f);
        CtfRateDroppedFlags(brain, rater, ctf, 9000f, bot.Origin, 10000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, org, middleRadius * 0.5f, 25000f);
        BotRoles.GoalrateItems(brain, rater, org, middleRadius * 0.5f, 25000f);
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 18000f);
        CtfRateBase(brain, rater, ef, 3000f);
        rater.End();
    }

    /// <summary>QC havocbot_role_ctf_defense: guard our base — rush back when an enemy is closer to it than
    /// we are, fight around it, farm the base items.</summary>
    private static void CtfRoleDefense(BotBrain brain, GoalRater rater, Ctf ctf, FlagState mf, FlagState ef,
        Vector3 middle, float middleRadius)
    {
        var bot = brain.Bot;
        float now = Api.Clock.Time;
        if (ctf.CarriedBy(bot) is not null) { CtfSetRole(brain, CtfBotRole.Carrier); return; }
        if (mf.Status != FlagStatus.AtBase) { CtfSetRole(brain, CtfBotRole.Retriever); return; }

        if (brain.CtfRoleTimeout == 0f)
            brain.CtfRoleTimeout = now + 30f;
        if (now > brain.CtfRoleTimeout) { CtfResetRole(brain, ctf, mf, ef, middle, middleRadius); return; }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        Vector3 org = mf.HomeOrigin;

        // QC: find the closest living player to our base; if it's an enemy and we're far from base, and we
        // can see them (or a coin flip), fall back to the base.
        Player? closest = null;
        float best = 10000f;
        foreach (Entity e in brain.LivePlayers())
        {
            if (e is not Player p || p.IsDead) continue;
            float d = (org - p.Origin).Length();
            if (d < best) { best = d; closest = p; }
        }
        if (closest is not null
            && (int)closest.Team != (int)bot.Team
            && (org - bot.Origin).Length() > 1000f
            && (Api.Trace.CheckPvs(bot.Origin, closest.Origin) || Prandom.Float() < 0.5))
        {
            CtfRateBase(brain, rater, mf, 10000f);
        }

        CtfRateOurStolenFlag(brain, rater, mf, 5000f);
        CtfRateDroppedFlags(brain, rater, ctf, 6000f, org, middleRadius);
        BotRoles.GoalrateEnemyPlayers(brain, rater, org, middleRadius, 25000f);
        BotRoles.GoalrateItems(brain, rater, org, middleRadius, 25000f);
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 18000f);
        rater.End();
    }

    // ============================================================================================
    // Domination (QC havocbot_role_dom + havocbot_goalrating_controlpoints)
    // ============================================================================================

    /// <summary>
    /// Domination role (QC havocbot_role_dom): rate control points that are unclaimed, contested, or held by
    /// another team (the ones worth capturing), plus items + roaming. Control points are the spawned
    /// <c>dom_controlpoint</c> map entities, found by classname.
    /// </summary>
    public static void RoleDomination(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        // QC havocbot_role_dom: dead → return; rate only on the goal-rating clock.
        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        GoalrateControlPoints(brain, rater, bot.Origin, 15000f);
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f, 20000f); // QC items(20000, org, 8000)
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static void GoalrateControlPoints(BotBrain brain, GoalRater rater, Vector3 org, float radius)
    {
        if (Api.Services is null) return;
        var bot = brain.Bot;
        float radius2 = radius * radius;
        Api.Entities.FindByClass("dom_controlpoint", _scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            Entity cp = _scratch[i];
            if (cp.IsFreed) continue;
            Vector3 pos = (cp.AbsMin != cp.AbsMax) ? (cp.AbsMin + cp.AbsMax) * 0.5f : cp.Origin;
            if ((pos - org).LengthSquared() > radius2) continue;
            // QC: rate if contested (cnt > -1), unclaimed, or held by another team. We approximate "not mine"
            // via the point's Team field (0 = unclaimed); a point already on our team is skipped.
            if (cp.Team != 0f && (int)cp.Team == (int)bot.Team) continue;
            rater.Rate(org, cp, pos, RatingControlPoint, 5000f);
        }
    }

    // ============================================================================================
    // Onslaught (QC havocbot_role_ons_* — attack the enemy generator / defend ours)
    // ============================================================================================

    /// <summary>
    /// Onslaught role (QC havocbot_role_ons_offense — sv_onslaught.qc:1451). The QC defense/assistant roles are
    /// legacy no-ops that immediately reset back to offense, so offense is effectively the only role; this single
    /// function reproduces it faithfully:
    ///   - QC havocbot_goalrating_ons_generator_attack FIRST — rate every ENEMY generator that is NOT shielded
    ///     (an attackable, exposed generator). If at least one is rated, the control-point pass is SKIPPED
    ///     (QC: <c>if(!generator_attack(…)) controlpoints_attack(…)</c>).
    ///   - QC havocbot_goalrating_ons_controlpoints_attack otherwise — rate only control points that are NOT
    ///     shielded AND neighbor the bot's team (QC the <c>aregensneighbor|arecpsneighbor &amp; BIT(team)</c>
    ///     filter): a shielded / unreachable point is never a goal. A free (no-icon) point is worth DOUBLE
    ///     (QC <c>ratingscale * 2</c> for a touchable point vs an icon to attack).
    /// The shield/neighbor state comes from the live <see cref="Onslaught"/> power graph (<see cref="Onslaught.OnsNode"/>),
    /// matching what the QC reads off <c>.isshielded</c> / the neighbor bitmasks. Falls back to the coarse
    /// classname scan only when the gametype singleton isn't an Onslaught (headless/edge).
    /// </summary>
    public static void RoleOnslaught(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        int myTeam = (int)bot.Team;
        float now = Api.Clock.Time;

        // QC havocbot_role_ons_offense (sv_onslaught.qc:1451): dead → clear attack_time + reset role.
        if (bot.IsDead)
        {
            brain.OnslaughtAttackTime = 0f;
            brain.OnslaughtRoleTimeout = 0f;
            bot.GtOnsTarget = null;
            return;
        }

        // QC: stamp the 120s role timeout if unset; reset (re-evaluate) when it expires.
        if (brain.OnslaughtRoleTimeout == 0f)
            brain.OnslaughtRoleTimeout = now + 120f;
        if (now > brain.OnslaughtRoleTimeout)
        {
            brain.OnslaughtRoleTimeout = 0f;
            bot.GtOnsTarget = null; // QC havocbot_ons_reset_role: this.havocbot_ons_target = NULL
            return;
        }

        // QC: if(this.havocbot_attack_time > time) return — committed to the current push (skip re-rating).
        if (now < brain.OnslaughtAttackTime)
            return;

        // QC: rating gated on the strategy clock (navigation_goalrating_timeout).
        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        // QC havocbot_goalrating_enemyplayers(this, 20000, this.origin, 650) — runs every offense re-rate.
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 650f, 20000f);

        var ons = brain.GameType as Onslaught;
        if (ons is not null && Api.Services is not null)
        {
            // QC: if(!generator_attack(this, 10000)) controlpoints_attack(this, 10000).
            if (!GoalrateOnsGeneratorAttack(brain, rater, ons, myTeam, now))
                GoalrateOnsControlPointsAttack(brain, rater, ons, myTeam, now);
        }
        else if (Api.Services is not null)
        {
            // Headless/edge fallback (no live Onslaught graph): the coarse classname scan.
            Api.Entities.FindByClass("onslaught_generator", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity gen = _scratch[i];
                if (gen.IsFreed) continue;
                if (gen.Team != 0f && (int)gen.Team == myTeam) continue;
                rater.Rate(bot.Origin, gen, gen.Origin, RatingGenerator, 100000f);
            }
            Api.Entities.FindByClass("onslaught_controlpoint", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity cp = _scratch[i];
                if (cp.IsFreed) continue;
                if (cp.Team != 0f && (int)cp.Team == myTeam) continue;
                rater.Rate(bot.Origin, cp, cp.Origin, RatingControlPoint, 30000f);
            }
        }

        // QC havocbot_goalrating_items(this, 25000, this.origin, 10000).
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 25000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    /// <summary>
    /// QC havocbot_goalrating_ons_generator_attack (sv_onslaught.qc:1396): rate every ENEMY, UN-shielded
    /// generator. For each, find the least-visited non-generated waypoint within 400 qu that has PVS to the
    /// generator and route to it (++cnt to spread bots); if the bot then has PVS to both the generator and that
    /// waypoint, commit for 5 s (havocbot_attack_time = time + 5). With no waypoints near the generator, route
    /// straight to it. Returns true if at least one attackable generator was rated (so the CP pass is skipped).
    /// </summary>
    private static bool GoalrateOnsGeneratorAttack(BotBrain brain, GoalRater rater, Onslaught ons, int myTeam, float now)
    {
        var bot = brain.Bot;
        bool rated = false;
        Api.Entities.FindByClass("onslaught_generator", _scratch);
        // Snapshot the generators (the inner waypoint loop must not reuse the shared _scratch mid-iteration).
        int count = _scratch.Count;
        var gens = new Entity[count];
        for (int i = 0; i < count; i++) gens[i] = _scratch[i];

        foreach (Entity gen in gens)
        {
            if (gen.IsFreed) continue;
            int genTeam = gen.GtHomeTeam != 0 ? gen.GtHomeTeam : (int)gen.Team;
            if (genTeam == myTeam) continue;                       // QC SAME_TEAM(g, this): skip ours
            Onslaught.OnsNode? node = ons.GeneratorNode(genTeam);
            if (node is null || node.Shielded) continue;           // QC g.isshielded: skip shielded

            // QC: IL_EACH(g_waypoints, vdist(g.origin - it.origin, <, 400)) — least-visited PVS waypoint.
            Waypoint? bestWp = PickApproachWaypoint(brain, gen.Origin, 400f, 400f);
            if (bestWp is not null)
            {
                // QC: navigation_routerating(this, bestwp, ratingscale, 10000); ++bestwp.cnt.
                rater.Rate(bot.Origin, null, bestWp.Origin, RatingGenerator, 10000f);
                bestWp.VisitCount++;

                // QC: havocbot_attack_time = 0; then = time+5 if PVS to both generator and waypoint.
                brain.OnslaughtAttackTime = 0f;
                Vector3 botEye = bot.Origin + bot.ViewOfs;
                if (Api.Trace.CheckPvs(botEye, gen.Origin) && Api.Trace.CheckPvs(botEye, bestWp.Origin))
                    brain.OnslaughtAttackTime = now + 5f;
            }
            else
            {
                // QC: no waypoints near the generator → go straight to it; havocbot_attack_time = 0.
                rater.Rate(bot.Origin, gen, gen.Origin, RatingGenerator, 10000f);
                brain.OnslaughtAttackTime = 0f;
            }
            rated = true;
        }
        return rated;
    }

    /// <summary>
    /// QC havocbot_goalrating_ons_controlpoints_attack (sv_onslaught.qc:1294): consider only UN-shielded control
    /// points that neighbor the bot's team, then pick the one fewest teammates are already targeting (teammate-
    /// interest cost balancing, QC <c>.wpcost</c> = count of same-team players whose havocbot_ons_target is this
    /// CP). For an attackable point (has a build icon) route to the least-visited PVS waypoint within 500/1000 qu
    /// and commit 2 s on PVS; for a free/touchable point route straight to it at DOUBLE ratingscale.
    /// </summary>
    private static void GoalrateOnsControlPointsAttack(BotBrain brain, GoalRater rater, Onslaught ons, int myTeam, float now)
    {
        var bot = brain.Bot;

        // QC phase 1: filter to attackable, team-neighbouring CPs and compute each one's teammate-interest cost
        // (how many same-team players already target it — lower is more desirable), picking the cheapest.
        Onslaught.OnsNode? best = null;
        Entity? bestEnt = null;
        float bestValue = float.MaxValue;
        Api.Entities.FindByClass("onslaught_controlpoint", _scratch);
        int count = _scratch.Count;
        var cps = new Entity[count];
        for (int i = 0; i < count; i++) cps[i] = _scratch[i];

        foreach (Entity cp in cps)
        {
            if (cp.IsFreed) continue;
            Onslaught.OnsNode? node = ons.ControlPointNode(cp.GtPointId);
            if (node is null || node.Shielded) continue;            // QC cp2.isshielded
            if (node.Team == myTeam) continue;                      // already ours
            if (!NeighborsTeam(node, myTeam)) continue;             // QC aregensneighbor|arecpsneighbor & BIT(team)

            // QC: count teammates already interested in this CP (cp2.wpcost).
            int interest = CountTeamInterest(brain, node, myTeam);
            if (interest < bestValue)
            {
                bestValue = interest;
                best = node;
                bestEnt = cp;
                bot.GtOnsTarget = node; // QC: this.havocbot_ons_target = cp1
            }
        }

        if (best is null || bestEnt is null)
            return;

        // QC: a point WITH a build icon should be ATTACKED; one without should be TOUCHED (captured).
        bool touchable = ons.CpCombat.IconFor(best.Id) is null;
        if (!touchable)
        {
            // Attackable icon: route to the least-visited PVS waypoint within 500→1000 qu (QC waypoint search).
            Waypoint? bestWp = PickApproachWaypoint(brain, bestEnt.Origin, 500f, 1000f);
            if (bestWp is not null)
            {
                rater.Rate(bot.Origin, null, bestWp.Origin, RatingControlPoint, 10000f);
                bestWp.VisitCount++;
                brain.OnslaughtAttackTime = 0f;
                Vector3 botEye = bot.Origin + bot.ViewOfs;
                if (Api.Trace.CheckPvs(botEye, bestEnt.Origin) && Api.Trace.CheckPvs(botEye, bestWp.Origin))
                    brain.OnslaughtAttackTime = now + 2f;
            }
            else
            {
                rater.Rate(bot.Origin, bestEnt, bestEnt.Origin, RatingControlPoint, 10000f);
            }
        }
        else
        {
            // QC: navigation_routerating(this, cp, ratingscale * 2, 10000) — a touchable point is worth DOUBLE.
            rater.Rate(bot.Origin, bestEnt, bestEnt.Origin, RatingControlPoint * 2f, 10000f);
        }
    }

    /// <summary>
    /// QC the inner waypoint search shared by ons_generator_attack / ons_controlpoints_attack: expand the radius
    /// shell from <paramref name="minRadius"/> to <paramref name="maxRadius"/> in 500-qu steps until at least one
    /// non-generated waypoint with PVS to <paramref name="target"/> is found, then return the least-visited one
    /// (QC <c>.cnt</c> = <see cref="Waypoint.VisitCount"/>). Null if the bot has no waypoint network / none qualify.
    /// </summary>
    private static Waypoint? PickApproachWaypoint(BotBrain brain, Vector3 target, float minRadius, float maxRadius)
    {
        int visits = int.MaxValue;
        Waypoint? best = null;
        if (brain.Network is null)
            return null;
        for (float radius = minRadius; radius <= maxRadius; radius += 500f)
        {
            bool foundAny = false;
            float r2 = radius * radius;
            foreach (var wp in brain.Network.Nodes)
            {
                if (wp.HasFlag(WaypointFlags.Generated)) continue;          // QC: !(wpflags & WAYPOINTFLAG_GENERATED)
                if ((wp.Origin - target).LengthSquared() > r2) continue;
                if (!Api.Trace.CheckPvs(wp.Origin, target)) continue;        // QC: checkpvs(it.origin, target)
                foundAny = true;
                if (wp.VisitCount < visits) { best = wp; visits = wp.VisitCount; }
            }
            if (foundAny) break; // QC: stop at the first shell that has a PVS-visible waypoint
        }
        return best;
    }

    /// <summary>
    /// QC the teammate-interest count in havocbot_goalrating_ons_controlpoints_attack (sv_onslaught.qc:1316): how
    /// many OTHER same-team players currently have <see cref="Entity.GtOnsTarget"/> set to this control-point node
    /// — used so bots spread across the attackable points instead of all swarming one.
    /// </summary>
    private static int CountTeamInterest(BotBrain brain, Onslaught.OnsNode node, int myTeam)
    {
        int c = 0;
        foreach (Entity e in brain.LivePlayers())
        {
            if (ReferenceEquals(e, brain.Bot)) continue;            // QC: it != this
            if ((int)e.Team != myTeam) continue;                   // QC SAME_TEAM(it, this)
            if (ReferenceEquals(e.GtOnsTarget, node)) ++c;         // QC: it.havocbot_ons_target == cp2
        }
        return c;
    }

    /// <summary>
    /// QC the <c>aregensneighbor|arecpsneighbor &amp; BIT(team)</c> filter (sv_onslaught.qc:1311): true if the
    /// node is adjacent to a powered (linked) node owned by <paramref name="team"/> — i.e. the team can actually
    /// reach/attack it. Derived from the live power graph (the same data onslaught_updatelinks would set the bits from).
    /// </summary>
    private static bool NeighborsTeam(Onslaught.OnsNode node, int team)
    {
        foreach (Onslaught.OnsNode m in node.Neighbors)
            if (m.Linked && m.Captured && m.Team == team)
                return true;
        return false;
    }

    // ============================================================================================
    // KeyHunt (QC havocbot_role_kh_* + havocbot_goalrating_kh)
    // ============================================================================================

    /// <summary>
    /// KeyHunt role (QC havocbot_role_kh_carrier / _defense / _offense / _freelancer + HavocBot_ChooseRole).
    /// Implements the FULL four-role QC behavior tree with per-bot role timeouts and stochastic role transitions,
    /// stored in <see cref="BotBrain.KhRole"/> / <see cref="BotBrain.KhRoleTimeout"/>:
    ///
    /// <list type="bullet">
    ///   <item><see cref="KhBotRole.None"/> (initial): QC <c>HavocBot_ChooseRole</c> random()*3 → offense/defense/
    ///     freelancer (equal thirds), matching Base sv_keyhunt.qc:1322-1335.</item>
    ///   <item><see cref="KhBotRole.Carrier"/>: no role_timeout. If the bot loses its key → freelancer. Ratings:
    ///     my-team-owns-all (10,0.1,0.05) else (4,4,0.5). QC havocbot_role_kh_carrier.</item>
    ///   <item><see cref="KhBotRole.Defense"/>: role_timeout = time + random()*10+20 (20-30 s). If bot picks up a
    ///     key → carrier. On timeout → freelancer. Neutral triple (4,1,0.05). QC havocbot_role_kh_defense.</item>
    ///   <item><see cref="KhBotRole.Offense"/>: role_timeout = time + random()*10+20 (20-30 s). If bot picks up a
    ///     key → carrier. On timeout → freelancer. Neutral triple (0.1,1,2). QC havocbot_role_kh_offense.</item>
    ///   <item><see cref="KhBotRole.Freelancer"/>: role_timeout = time + random()*10+10 (10-20 s). If bot picks up
    ///     a key → carrier. On timeout → random(0.5) offense|defense. Neutral triple (1,10,2). QC havocbot_role_kh_freelancer.</item>
    /// </list>
    ///
    /// The two non-neutral branches (my-team-owns-all → (10,0.1,0.05); enemy-owns-all → (0.1,0.1,5)) are the
    /// same for ALL three non-carrier roles and override the per-role neutral triple. Keys are the spawned
    /// <c>item_kh_key</c> entities; a carried key's carrier is <see cref="Entity.GtCarrier"/>.
    /// </summary>
    public static void RoleKeyHunt(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        int myTeam = (int)bot.Team;
        var kh = brain.GameType as KeyHunt;
        float now = Api.Clock.Time;
        bool botCarries = bot.GtCarried is not null; // QC this.kh_next — the bot is holding at least one key

        // ---- Role state machine (QC havocbot_role_kh_* + HavocBot_ChooseRole) ----

        // Dead bots don't transition; the role persists across death (Base's dead-early-return in each role).
        if (!bot.IsDead)
        {
            // QC HavocBot_ChooseRole (sv_keyhunt.qc:1322): initial random role pick (none → offense/defense/freelancer).
            if (brain.KhRole == KhBotRole.None)
            {
                float r = (float)Prandom.Float() * 3f;
                brain.KhRole = r < 1f ? KhBotRole.Offense : r < 2f ? KhBotRole.Defense : KhBotRole.Freelancer;
                brain.KhRoleTimeout = 0f;
            }

            // QC all non-carrier roles: if the bot picks up a key → become carrier (no timeout).
            if (brain.KhRole != KhBotRole.Carrier && botCarries)
            {
                brain.KhRole = KhBotRole.Carrier;
                brain.KhRoleTimeout = 0f;
            }
            // QC havocbot_role_kh_carrier: if the bot loses its key → become freelancer.
            else if (brain.KhRole == KhBotRole.Carrier && !botCarries)
            {
                brain.KhRole = KhBotRole.Freelancer;
                brain.KhRoleTimeout = 0f;
            }

            // Per-role timeout handling.
            switch (brain.KhRole)
            {
                case KhBotRole.Defense:
                case KhBotRole.Offense:
                    // QC defense/offense: role_timeout = time + random()*10+20 (20-30 s); on timeout → freelancer.
                    if (brain.KhRoleTimeout == 0f)
                        brain.KhRoleTimeout = now + (float)Prandom.Float() * 10f + 20f;
                    if (now > brain.KhRoleTimeout)
                    {
                        brain.KhRole = KhBotRole.Freelancer;
                        brain.KhRoleTimeout = 0f;
                    }
                    break;

                case KhBotRole.Freelancer:
                    // QC freelancer: role_timeout = time + random()*10+10 (10-20 s); on timeout → offense|defense.
                    if (brain.KhRoleTimeout == 0f)
                        brain.KhRoleTimeout = now + (float)Prandom.Float() * 10f + 10f;
                    if (now > brain.KhRoleTimeout)
                    {
                        brain.KhRole = (float)Prandom.Float() < 0.5f ? KhBotRole.Offense : KhBotRole.Defense;
                        brain.KhRoleTimeout = 0f;
                    }
                    break;

                // Carrier: no timeout.
                default:
                    break;
            }
        }

        // ---- Rating triple selection (QC havocbot_goalrating_kh(team_scale, dropped_scale, enemy_scale)) ----
        int allOwned = kh?.AllOwnedByWhichTeam() ?? Teams.None;

        float rsTeam, rsDropped, rsEnemy;
        if (brain.KhRole == KhBotRole.Carrier)
        {
            // QC havocbot_role_kh_carrier ratings.
            if (allOwned == myTeam) { rsTeam = 10f; rsDropped = 0.1f;  rsEnemy = 0.05f; } // bring home
            else                    { rsTeam = 4f;  rsDropped = 4f;    rsEnemy = 0.5f;  } // play defensively
        }
        else if (allOwned == myTeam)
        {
            // All roles agree: defend the gathered carriers.
            rsTeam = 10f; rsDropped = 0.1f; rsEnemy = 0.05f;
        }
        else if (allOwned != Teams.None)
        {
            // All roles agree: enemy owns all → emergency attack.
            rsTeam = 0.1f; rsDropped = 0.1f; rsEnemy = 5f;
        }
        else
        {
            // Nobody owns all yet → the per-role neutral triple:
            //   defense (4,1,0.05) leans home; offense (0.1,1,2) leans attack; freelancer (1,10,2) prefers dropped.
            switch (brain.KhRole)
            {
                case KhBotRole.Defense:    rsTeam = 4f;   rsDropped = 1f;  rsEnemy = 0.05f; break;
                case KhBotRole.Offense:    rsTeam = 0.1f; rsDropped = 1f;  rsEnemy = 2f;    break;
                default: /* Freelancer */  rsTeam = 1f;   rsDropped = 10f; rsEnemy = 2f;    break;
            }
        }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        RateKeyHuntTargets(brain, rater, bot, rsTeam, rsDropped, rsEnemy);
        // QC havocbot_goalrating_kh tail: items at 80000 (keys apart, KH bots stay stocked); QC's KH roles
        // rate NO enemy players — the key triple already routes toward enemy carriers when appropriate.
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 80000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    /// <summary>Helper to rate all reachable keys and carriers per the three ratingscale components.</summary>
    private static void RateKeyHuntTargets(BotBrain brain, GoalRater rater, Entity bot, float rsTeam, float rsDropped, float rsEnemy)
    {
        // QC havocbot_goalrating_kh: rate every key — toward an ally carrier (team), an enemy carrier (enemy),
        // or a dropped key (dropped). The base scales are *10000 in QC; RatingKey is the port's BOT_RATING_* anchor.
        if (Api.Services is null) return;
        int myTeam = (int)bot.Team;
        Api.Entities.FindByClass("item_kh_key", _scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            Entity key = _scratch[i];
            if (key.IsFreed) continue;
            Entity? carrier = key.GtCarrier;
            if (carrier is not null)
            {
                if (ReferenceEquals(carrier, bot)) continue; // QC: head.owner == this → skip my own key
                float scale = (int)carrier.Team == myTeam ? rsTeam : rsEnemy;
                rater.Rate(bot.Origin, carrier, carrier.Origin, RatingKey * scale, 100000f);
            }
            else
            {
                rater.Rate(bot.Origin, key, key.Origin, RatingKey * rsDropped, 100000f);
            }
        }
    }

    // ============================================================================================
    // Keepaway (QC havocbot_role_ka_* + havocbot_goalrating_ball)
    // ============================================================================================

    /// <summary>
    /// Keepaway role (QC havocbot_role_ka_collector/carrier): if carrying the ball, fight enemies for points;
    /// otherwise chase the loose ball (or the enemy carrier to take it). The ball is the spawned
    /// <c>keepawayball</c> entity; its <see cref="Entity.GtCarrier"/> marks the carrier while held
    /// (== Base ka_TouchEvent's <c>ball.owner = toucher</c>; the port reserves <see cref="Entity.Owner"/>
    /// for the previous-owner self-recapture lockout, set only on drop).
    /// </summary>
    public static void RoleKeepaway(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        // QC havocbot_role_ka_*: no role state machine (carrier/collector split by live ball ownership);
        // rate only on the goal-rating clock.
        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        Entity? ball = FindBall();
        bool carryingBall = ball is not null && ReferenceEquals(ball.GtCarrier, bot);

        if (carryingBall)
        {
            // Carrier (QC havocbot_role_ka_carrier / havocbot_role_tka_carrier, identical values): bank points by
            // surviving + fighting — items 10000, enemyplayers 10000, then roam waypoints (added below at 3000).
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 10000f);
        }
        else if (ball is not null)
        {
            // Collector (QC havocbot_role_ka_collector / havocbot_role_tka_collector, identical values): items
            // 10000, enemyplayers 500 (don't get distracted fragging — go get the ball), then the ball rating.
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 500f);
            // Go to the ball (or its carrier to take it). Base havocbot_goalrating_ball / _tkaball prefer a loose
            // ball, else the carrier (it.owner) — here GtCarrier when held.
            Entity? carrier = ball.GtCarrier;
            Entity target = carrier ?? ball;
            // Team Keepaway (havocbot_goalrating_tkaball): a loose ball or an ENEMY carrier rates 8000; a SAME-TEAM
            // carrier rates 4000 (chase your own carrier less). Plain (FFA) Keepaway has no teams, so always 8000.
            float scale = RatingBall;
            if (carrier is not null && (brain.GameType?.TeamGame ?? false) && (int)carrier.Team == (int)bot.Team)
                scale = RatingBallSameTeam;
            rater.Rate(bot.Origin, target, target.Origin, scale, 2000f);
        }
        else
        {
            // No ball found: play like DM.
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 7000f);
        }
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    // ============================================================================================
    // Freeze Tag (QC havocbot_role_ft_offense / havocbot_role_ft_freeing + havocbot_goalrating_freeplayers)
    // ============================================================================================

    /// <summary>
    /// Freeze Tag role (QC havocbot_role_ft_offense / havocbot_role_ft_freeing, sv_freezetag.qc:298-397): the
    /// FULL two-role QC machine, not a collapse. The roles alternate on 20-30s timeouts (offense also flips to
    /// freeing when its whole team is frozen); offense fights (items 12000 / enemies 10000 / frozen mates 9000)
    /// while freeing prioritises thawing (frozen mates 20000 / items 10000 / enemies 5000). The frozen set is
    /// read live from the gametype's <see cref="FreezeTag.Frozen"/> map. Falls back to DM goals otherwise.
    /// </summary>
    public static void RoleFreezeTag(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        if (brain.GameType is not FreezeTag ft || bot is not Player self)
        {
            BotRoles.RoleGeneric(brain, rater);
            return;
        }

        float now = Api.Clock.Time;

        // QC HavocBot_ChooseRole ft (sv_freezetag.qc:965-974): initial random 50/50 pick + a 10-20s shakeout
        // timeout so a team that spawned all at once staggers its first re-evaluation.
        if (brain.FtRole == FtBotRole.None)
        {
            brain.FtRole = Prandom.Float() < 0.5 ? FtBotRole.Freeing : FtBotRole.Offense;
            brain.FtRoleTimeout = now + 10f + (float)Prandom.Float() * 10f;
        }

        if (brain.FtRoleTimeout == 0f)
            brain.FtRoleTimeout = now + (float)Prandom.Float() * 10f + 20f;

        if (brain.FtRole == FtBotRole.Offense)
        {
            // QC: count unfrozen players on the team (self included).
            int unfrozen = 0;
            foreach (Entity e in brain.LivePlayers())
                if (e is Player p && Teams.SameTeam(self, p) && !ft.IsFrozen(p))
                    ++unfrozen;

            // QC: last one standing (or role timed out) → start freeing teammates.
            if ((unfrozen == 0 && !ft.IsFrozen(self)) || now > brain.FtRoleTimeout)
            {
                brain.FtRole = FtBotRole.Freeing;
                brain.FtRoleTimeout = 0f;
                return;
            }

            if (!brain.GoalRatingTimedOut) return;
            brain.BeginGoalRating(rater);
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 12000f);
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 10000f, 10000f);
            FtRateFreePlayers(brain, rater, ft, self, 9000f, bot.Origin, 10000f);
            BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
            rater.End();
            return;
        }

        // Freeing role.
        if (now > brain.FtRoleTimeout)
        {
            brain.FtRole = FtBotRole.Offense;
            brain.FtRoleTimeout = 0f;
            return;
        }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 10000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 10000f, 5000f);
        FtRateFreePlayers(brain, rater, ft, self, 20000f, bot.Origin, 10000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    /// <summary>
    /// QC havocbot_goalrating_ft_freeplayers (sv_freezetag.qc:303): rate every frozen same-team player within
    /// <paramref name="sradius"/> at full scale, and — buddy grouping — the nearest healthy UNFROZEN teammate
    /// beyond 700qu at half scale (fighting in a group beats roaming solo). A teammate already within 700qu
    /// cancels the buddy pull entirely.
    /// </summary>
    private static void FtRateFreePlayers(BotBrain brain, GoalRater rater, FreezeTag ft, Player self,
        float ratingscale, Vector3 org, float sradius)
    {
        var bot = brain.Bot;
        Player? bestPl = null;
        float bestDist2 = float.MaxValue;
        float sradius2 = sradius * sradius;
        foreach (var e in brain.LivePlayers())
        {
            if (e is not Player mate || ReferenceEquals(mate, self) || mate.IsDead) continue;
            if (!Teams.SameTeam(self, mate)) continue;
            if (ft.IsFrozen(mate))
            {
                if ((mate.Origin - org).LengthSquared() > sradius2) continue;
                rater.Rate(bot.Origin, mate, mate.Origin, ratingscale, 2000f);
            }
            else if (bestDist2 > 0f
                && mate.Health < self.Health + 30f
                && (mate.Origin - org).LengthSquared() < bestDist2)
            {
                float d2 = (mate.Origin - org).LengthSquared();
                if (d2 < 700f * 700f)
                {
                    bestPl = null;
                    bestDist2 = 0f; // already close to a teammate — no buddy pull needed
                }
                else
                {
                    bestPl = mate;
                    bestDist2 = d2;
                }
            }
        }
        if (bestPl is not null)
            rater.Rate(bot.Origin, bestPl, bestPl.Origin, ratingscale / 2f, 2000f);
    }

    private static Entity? FindBall()
    {
        if (Api.Services is null) return null;
        // The live ball is spawned with classname "keepawayball" (BallEntity.WithKindDefaults, no underscore) for
        // both Keepaway and Team Keepaway — querying the wrong class made the role inert (bots fell back to DM).
        Api.Entities.FindByClass("keepawayball", _scratch);
        for (int i = 0; i < _scratch.Count; i++)
            if (!_scratch[i].IsFreed) return _scratch[i];
        return null;
    }

    // ----------------------------------------------------------------------------------------
    // Nexball (QC havocbot_role_nexball) — carry/shoot the ball into the ENEMY goal
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Nexball role (QC havocbot_role_nexball): if carrying the ball, route to the enemy goal to score;
    /// otherwise chase the loose ball (or its carrier). Always also wants items/enemies/roam (a behavioural
    /// collapse of the QC carrier/support roles). Falls back to generic if no ball is on the map.
    /// </summary>
    public static void RoleNexball(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        // (Base ships NO nexball bot role at all — QC nexball bots play generic. This role is a port
        // improvement; it still honors the standard goal-rating clock.)
        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        if (brain.GameType is Nexball nb && nb.BallEntity is Entity ball && !ball.IsFreed)
        {
            if (ReferenceEquals(ball.Owner, bot))
            {
                Entity? goal = FindEnemyNexballGoal(bot);          // carrying → drive to the enemy goal
                if (goal is not null)
                    rater.Rate(bot.Origin, goal, goal.Origin, RatingControlPoint, 100000f);
            }
            else
            {
                Entity target = ball.Owner ?? ball;                // loose / enemy-carried → go get it
                rater.Rate(bot.Origin, target, target.Origin, RatingBall, 100000f);
            }
        }

        BotRoles.GoalrateItems(brain, rater, bot.Origin, 8000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 6000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static Entity? FindEnemyNexballGoal(Player bot)
    {
        if (Api.Services is null) return null;
        Api.Entities.FindByClass("nexball_goal", _scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            Entity g = _scratch[i];
            if (!g.IsFreed && g.GtHomeTeam != (int)bot.Team) return g; // score in the enemy team's goal
        }
        return null;
    }

    // ----------------------------------------------------------------------------------------
    // Assault (QC havocbot_role_ass) — attack / defend the objective destructible walls
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Assault role (QC havocbot_role_ast_offense / havocbot_role_ast_defense + havocbot_goalrating_ast_targets):
    /// route to the objective destructible walls whose linked objective is currently ACTIVE (QC filters
    /// <c>0 &lt; hlth &lt; ASSAULT_VALUE_INACTIVE</c> — a not-yet-active or already-fallen wall isn't a goal), with
    /// the QC ast-targets ratingscale (20000). The attack/defend split only differs in the enemy-player radius (QC
    /// 650 offense vs 3000 defense). Both rate items + enemies + the targets.
    ///
    /// Three QC behaviours implemented here:
    /// <list type="bullet">
    ///   <item>120s role timeout (QC <c>havocbot_role_timeout</c>): after 120s the role resets so the bot
    ///     re-evaluates its strategy rather than being stuck on a single dead path forever.</item>
    ///   <item>2s attack-time commitment (QC <c>havocbot_attack_time</c>): once the bot has PVS to both the
    ///     approach waypoint and the objective wall, it commits to the approach for 2s without re-rating goals.</item>
    ///   <item>PVS-best-waypoint pick (QC <c>havocbot_goalrating_ast_targets</c> inner waypoint search): instead
    ///     of routing directly to the wall, the role finds the best non-generated waypoint within 500/1000/1500u of
    ///     the wall that has PVS to it, picks the least-visited one (QC <c>.cnt</c> = <see cref="Waypoint.VisitCount"/>),
    ///     and routes to that waypoint — matching QC's PVS-approach-path selection for bots attacking objectives.</item>
    /// </list>
    /// The active-objective filter is read back through the Assault POJO chain (DestructibleFor → DecreaserRef →
    /// ObjectiveRef.Active), which is the port's authoritative objective health.
    /// </summary>
    public static void RoleAssault(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        float now = Api.Clock.Time;

        // QC havocbot_role_ast_offense/defense: dead → clear attack_time + reset role. In the port the dead
        // early-return in ThinkProduce clears AssaultAttackTime and never calls the role, so this is defensive.
        if (bot.IsDead)
        {
            brain.AssaultAttackTime = 0f;
            brain.AssaultRoleTimeout = 0f;
            return;
        }

        // QC: Set the role timeout if not yet stamped; reset + bail if it expired.
        if (brain.AssaultRoleTimeout == 0f)
            brain.AssaultRoleTimeout = now + 120f;
        if (now > brain.AssaultRoleTimeout)
        {
            brain.AssaultRoleTimeout = 0f;
            return;
        }

        // QC: if(this.havocbot_attack_time > time) return — bot is committed to the current push.
        if (now < brain.AssaultAttackTime)
            return;

        // QC: rating gated on the strategy clock (navigation_goalrating_timeout).
        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        var asl = brain.GameType as Assault;
        bool attacker = asl is not null && (int)bot.Team == asl.AttackerTeam;

        // QC havocbot_goalrating_ast_targets: for each active-objective destructible, find the best nearby
        // PVS-visible non-generated waypoint and route to it rather than directly to the wall. This gives the
        // bot an approach path the nav-graph can actually resolve, and the PVS check ensures the bot can see
        // (and thus shoot) the objective from that waypoint. ratingscale = 20000 (RatingGenerator).
        if (asl is not null && Api.Services is not null && brain.Network is not null)
        {
            Api.Entities.FindByClass("func_assault_destructible", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity wall = _scratch[i];
                if (wall.IsFreed) continue;
                Assault.Destructible? d = asl.DestructibleFor(wall);
                // QC IL_EACH(g_assault_destructibles, it.bot_attack): only a still-alive, shootable wall is a
                // bot target — Destructible.Active is the port's bot_attack mirror (cleared the instant the wall
                // is destroyed in DamageDestructible). PLUS the QC inner-loop active-objective filter
                // (0 < hlth < ASSAULT_VALUE_INACTIVE): a wall whose objective is inactive/destroyed isn't a goal.
                if (d is null || !d.Active || d.Destroyed
                    || d.DecreaserRef?.ObjectiveRef is not { Active: true })
                    continue;

                // QC: center of the wall's bbox (0.5 * (absmin + absmax)).
                Vector3 wallCenter = wall.AbsMin != wall.AbsMax
                    ? (wall.AbsMin + wall.AbsMax) * 0.5f
                    : wall.Origin;

                // QC: expand radius shell 500→1000→1500 until we find at least one PVS-visible waypoint.
                Waypoint? bestWp = null;
                int bestVisits = int.MaxValue;
                for (float radius = 500f; radius <= 1500f; radius += 500f)
                {
                    bool foundAny = false;
                    float r2 = radius * radius;
                    foreach (var wp in brain.Network.Nodes)
                    {
                        if (wp.HasFlag(WaypointFlags.Generated)) continue; // QC: !(wpflags & WAYPOINTFLAG_GENERATED)
                        if ((wp.Origin - wallCenter).LengthSquared() > r2) continue;
                        if (!Api.Trace.CheckPvs(wp.Origin, wallCenter)) continue; // QC: checkpvs(it.origin, des)
                        foundAny = true;
                        if (wp.VisitCount < bestVisits)
                        {
                            bestWp = wp;
                            bestVisits = wp.VisitCount;
                        }
                    }
                    if (foundAny) break; // QC: stop at the first radius shell that has a PVS-visible waypoint
                }

                if (bestWp is not null)
                {
                    // QC: navigation_routerating(this, best, ratingscale, 4000) — route to the waypoint.
                    rater.Rate(bot.Origin, null, bestWp.Origin, RatingGenerator, 4000f);
                    // QC: ++best.cnt — load-balance bots across waypoints near the same objective.
                    bestWp.VisitCount++;

                    // QC: havocbot_attack_time = 0; then set to time+2 if bot has PVS to both wall and waypoint.
                    brain.AssaultAttackTime = 0f;
                    Vector3 botEye = bot.Origin + bot.ViewOfs;
                    if (Api.Trace.CheckPvs(botEye, wallCenter)
                        && Api.Trace.CheckPvs(botEye, bestWp.Origin))
                    {
                        brain.AssaultAttackTime = now + 2f;
                    }
                }
                else
                {
                    // No waypoints near this destructible — fall back to rating the wall directly (so bots still
                    // converge on the objective even on unwaypointed maps).
                    rater.Rate(bot.Origin, wall, wallCenter, RatingGenerator, 100000f);
                }
            }
        }
        else if (asl is not null && Api.Services is not null)
        {
            // Network absent (headless/no-waypoints): rate walls directly so bots still push.
            Api.Entities.FindByClass("func_assault_destructible", _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity wall = _scratch[i];
                if (wall.IsFreed) continue;
                Assault.Destructible? d = asl.DestructibleFor(wall);
                // QC it.bot_attack (alive shootable wall) + the active-objective filter (see the network branch).
                if (d is null || !d.Active || d.Destroyed
                    || d.DecreaserRef?.ObjectiveRef is not { Active: true })
                    continue;
                rater.Rate(bot.Origin, wall, wall.Origin, RatingGenerator, 100000f);
            }
        }
        else
        {
            // No POJO chain (headless/edge): fall back to the nearest wall so bots still converge on the objective.
            Entity? wall = NearestByClass(bot.Origin, "func_assault_destructible");
            if (wall is not null)
                rater.Rate(bot.Origin, wall, wall.Origin, RatingGenerator, 100000f);
        }

        // QC havocbot_goalrating_items(this, 30000, org, 10000); enemyplayers(this, 10000, org, 650|3000).
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f, 30000f);
        BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, attacker ? 650f : 3000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    // ----------------------------------------------------------------------------------------
    // CTS (QC sv_cts.qc havocbot_role_cts) — run the course: route to the next race checkpoint waypoint
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// CTS bot role (QC <c>havocbot_role_cts</c>): route the bot to its next expected race checkpoint. QC walks
    /// <c>g_racecheckpoints</c> and route-rates every checkpoint whose <c>.cnt</c> matches the bot's
    /// <c>.race_checkpoint</c> (with -1 → any), so the bot heads to the next gate. The port's CTS is a single
    /// start→stop course (no intermediate <c>trigger_race_checkpoint</c> entities), so "the next checkpoint" is the
    /// start timer until the run is in progress, then the stop timer (finish). Both are rated with QC's enormous
    /// 1000000/5000 routerating so the course goal always wins over items/enemies — exactly the QC intent.
    /// Falls back to the generic role if the CTS gametype/timers aren't resolvable.
    /// </summary>
    public static void RoleCts(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        if (brain.GameType is not Cts cts || bot is not Player p)
        {
            BotRoles.RoleGeneric(brain, rater);
            return;
        }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        // QC: the bot's expected next checkpoint. A run not yet in progress (NextCheckpoint < 0, or not Running)
        // wants the start timer (cp 0); once the start is crossed (Running) it wants the stop timer (the finish).
        Cts.CtsState st = cts.GetState(p);
        bool wantFinish = st.Running;

        Entity? goal = null;
        for (int i = 0; i < cts.Timers.Count; i++)
        {
            Entity t = cts.Timers[i];
            if (t.IsFreed) continue;
            bool isStart = t.GtIsStartTimer;
            bool isStop = t.GtIsStopTimer;
            if (wantFinish ? isStop : isStart)
            {
                // QC: rate every matching checkpoint; the brain routes to the highest-rated (nearest wins on bias).
                rater.Rate(bot.Origin, t, t.Origin, RatingRaceCheckpoint, 5000f);
                goal = t;
            }
        }

        // If the expected timer kind isn't on the map (e.g. a stop-only or start-only authoring quirk), fall back to
        // the other timer so the bot still moves toward the course rather than freezing.
        if (goal is null)
            for (int i = 0; i < cts.Timers.Count; i++)
            {
                Entity t = cts.Timers[i];
                if (!t.IsFreed && (t.GtIsStartTimer || t.GtIsStopTimer))
                    rater.Rate(bot.Origin, t, t.Origin, RatingRaceCheckpoint, 5000f);
            }

        rater.End();
    }

    // ----------------------------------------------------------------------------------------
    // Race (QC sv_race.qc havocbot_role_race) — run the track: route to the next race checkpoint
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Race bot role (QC <c>havocbot_role_race</c>, sv_race.qc:18): route the bot to its next expected race
    /// checkpoint. QC walks <c>g_racecheckpoints</c> and route-rates every checkpoint whose <c>.cnt</c> matches the
    /// bot's <c>.race_checkpoint</c> (with -1 → ANY checkpoint), so the bot heads to the next gate in sequence; each
    /// is rated with QC's enormous 1000000/5000 routerating so the track goal dwarfs items/enemies. Unlike CTS,
    /// Race has real intermediate <c>trigger_race_checkpoint</c> entities (<see cref="Race.Checkpoints"/>), so the
    /// bot's <see cref="Race.RaceState.NextCheckpoint"/> selects exactly which gate(s) to head for. Falls back to the
    /// generic role if the Race gametype isn't resolvable.
    /// </summary>
    public static void RoleRace(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        if (brain.GameType is not Race race || bot is not Player p)
        {
            BotRoles.RoleGeneric(brain, rater);
            return;
        }

        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        // QC: int cp = this.race_checkpoint; (the bot's expected next checkpoint, -1 = none yet → match ANY).
        Race.RaceState st = race.GetState(p);
        int cp = st.NextCheckpoint; // -1 = expect the start line / any; >=0 = that specific gate

        Entity? goal = null;
        for (int i = 0; i < race.Checkpoints.Count; i++)
        {
            Entity it = race.Checkpoints[i];
            if (it.IsFreed) continue;
            // QC: if(it.cnt == cp || cp == -1) — rate the gate matching the bot's next checkpoint (or any if -1).
            if (cp == -1 || it.GtCheckpointIndex == cp)
            {
                rater.Rate(bot.Origin, it, it.Origin, RatingRaceCheckpoint, 5000f);
                goal = it;
            }
        }

        // If the expected gate isn't resolvable (e.g. a bot expecting a checkpoint index the map doesn't carry), fall
        // back to rating ALL checkpoints so the bot still moves toward the track rather than freezing.
        if (goal is null)
            for (int i = 0; i < race.Checkpoints.Count; i++)
            {
                Entity it = race.Checkpoints[i];
                if (!it.IsFreed)
                    rater.Rate(bot.Origin, it, it.Origin, RatingRaceCheckpoint, 5000f);
            }

        rater.End();
    }

    // ----------------------------------------------------------------------------------------
    // Invasion — hunt the monster waves
    // ----------------------------------------------------------------------------------------

    /// <summary>QC BOT_RATING_ENEMY-adjacent scale for a live monster as a route goal. Modest — a wave that
    /// spawned across the map is worth walking to, but health/armor between the bot and the monsters still
    /// win when needed.</summary>
    private const float RatingMonster = 2500f;

    /// <summary>
    /// Invasion role (PORT IMPROVEMENT — Base's Invasion bots run havocbot_role_generic and only fight
    /// monsters that wander into view, since QC's goal rating covers players only): rate every LIVE monster
    /// as a route goal so bots actively hunt the wave down instead of roaming until one finds them. Target
    /// acquisition itself is the shared ChooseEnemy monster sweep (QC g_bot_targets); the gametype's
    /// Bot_ForbidAttack hook (players vetoed, QC sv_invasion.qc:426) keeps the fire discipline faithful.
    /// </summary>
    public static void RoleInvasion(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);

        if (Api.Services is not null)
        {
            Api.Entities.FindInRadius(bot.Origin, 15000f, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Entity m = _scratch[i];
                if (m.IsFreed || (m.Flags & EntFlags.Monster) == 0) continue;
                if (m.DeadState != DeadFlag.No || m.Health <= 0f) continue;
                Vector3 pos = (m.AbsMin != m.AbsMax) ? (m.AbsMin + m.AbsMax) * 0.5f : m.Origin;
                rater.Rate(bot.Origin, m, pos, RatingMonster, 2000f);
            }
        }

        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);
        BotRoles.GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    private static Entity? NearestByClass(Vector3 from, string className)
    {
        if (Api.Services is null) return null;
        Entity? best = null; float bestD2 = float.MaxValue;
        Api.Entities.FindByClass(className, _scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            Entity e = _scratch[i];
            if (e.IsFreed) continue;
            float d2 = (e.Origin - from).LengthSquared();
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }
}
