using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins Survival round-start role assignment against QC <c>Surv_RoundStart</c> (sv_survival.qc:160-193):
///  - the hunter count is derived from the LIVE player count (FOREACH_CLIENT(IS_PLAYER &amp;&amp; !IS_DEAD) —
///    observers and dead players do NOT count), bound(1, …, live − 1);
///  - the hunter subset is a RANDOM selection over the live players (FOREACH_CLIENT_RANDOM — the inside-out
///    Fisher–Yates shuffle), NOT the first roster slots;
///  - dead / observer players are never made hunters.
/// The regression this guards: counting roster.Count (incl. dead/observers) and marking roster[0..hunters) in
/// fixed order.
/// </summary>
[Collection("GlobalState")]
public class SurvivalRolesTests
{
    public SurvivalRolesTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        Api.Cvars.Set("g_survival_hunter_count", "0.25"); // QC default; make the test independent of leaked state
    }

    private static Player Live() => new Player { Flags = EntFlags.Client };
    private static Player Dead() => new Player { Flags = EntFlags.Client, DeadState = DeadFlag.Dead };
    private static Player Observer() => new Player { Flags = EntFlags.Client, IsObserver = true };

    [Fact]
    public void HunterCount_UsesLiveOnly_NotRosterCount()
    {
        // 6 roster entries, only 4 are live (2 dead/observer). floor(4 * 0.25) = 1 hunter over the LIVE count;
        // over the naive roster.Count (6) it would have been floor(6 * 0.25) = 1 here too, so use a count where
        // the two diverge: 8 live → floor(8*0.25)=2, but with 4 dead added the roster is 12 → floor(12*0.25)=3.
        var live = Enumerable.Range(0, 8).Select(_ => Live()).ToList();
        var dead = Enumerable.Range(0, 4).Select(_ => Dead()).ToList();
        var roster = live.Concat(dead).ToList();

        var surv = new Survival();
        surv.AssignRoles(roster);

        int hunters = roster.Count(p => surv.StatusOf(p) == Survival.SurvStatus.Hunter);
        Assert.Equal(2, hunters); // floor(8 live * 0.25) = 2 — NOT floor(12 * 0.25) = 3

        // Dead/observer players must never be chosen as hunters.
        Assert.All(dead, p => Assert.NotEqual(Survival.SurvStatus.Hunter, surv.StatusOf(p)));
    }

    [Fact]
    public void HunterCountFormula_BoundsToAtLeastOne_AndLessThanTotal()
    {
        var surv = new Survival();
        // floor(2 * 0.25) = 0 → bound up to 1.
        Assert.Equal(1, surv.HunterCount(2));
        // floor(8 * 0.25) = 2.
        Assert.Equal(2, surv.HunterCount(8));
        // A fraction that would meet/exceed the total is clamped to total − 1 (never all-hunters).
        Api.Cvars.Set("g_survival_hunter_count", "1");
        Assert.Equal(1, surv.HunterCount(2)); // 1 absolute, max = total-1 = 1
        Api.Cvars.Set("g_survival_hunter_count", "10");
        Assert.Equal(4, surv.HunterCount(5)); // 10 clamped to total-1 = 4
        Api.Cvars.Set("g_survival_hunter_count", "0.25"); // restore
    }

    [Fact]
    public void HunterPick_IsRandom_NotAlwaysFirstRosterSlot()
    {
        // QC FOREACH_CLIENT_RANDOM: with default 0.25 and 4 live players the count is 1 hunter. A fixed-order
        // pick would ALWAYS choose roster[0]; a faithful random pick chooses different slots across seeds.
        var picks = new HashSet<int>();
        for (uint seed = 1; seed <= 64 && picks.Count < 2; seed++)
        {
            Prandom.Seed(seed);
            var roster = Enumerable.Range(0, 4).Select(_ => Live()).ToList();
            var surv = new Survival();
            surv.AssignRoles(roster);

            int hunterIdx = roster.FindIndex(p => surv.StatusOf(p) == Survival.SurvStatus.Hunter);
            Assert.Equal(1, roster.Count(p => surv.StatusOf(p) == Survival.SurvStatus.Hunter)); // exactly one
            picks.Add(hunterIdx);
        }

        // If the pick were always roster[0] this set would only ever contain {0}. A random pick lands on a
        // non-first slot for at least one seed → more than one distinct index observed.
        Assert.True(picks.Count >= 2, $"hunter slot never varied across seeds: only saw {string.Join(",", picks)}");
        Assert.Contains(picks, idx => idx != 0); // and at least one of those is NOT the first slot
    }

    [Fact]
    public void EveryLivePlayerCanBeChosen_OverManySeeds()
    {
        // A stronger statement of randomness: over enough seeds every one of the 4 live slots is selected at
        // some point (the shuffle reaches the whole live set, not just an early prefix).
        var seen = new HashSet<int>();
        for (uint seed = 1; seed <= 256 && seen.Count < 4; seed++)
        {
            Prandom.Seed(seed);
            var roster = Enumerable.Range(0, 4).Select(_ => Live()).ToList();
            var surv = new Survival();
            surv.AssignRoles(roster);
            seen.Add(roster.FindIndex(p => surv.StatusOf(p) == Survival.SurvStatus.Hunter));
        }
        Assert.Equal(new HashSet<int> { 0, 1, 2, 3 }, seen);
    }

    [Fact]
    public void FewerThanTwoLivePlayers_NoHunters()
    {
        // Only 1 live player (rest dead) → no split possible, everyone stays prey (QC playercount < 2 guard).
        var roster = new List<Player> { Live(), Dead(), Dead() };
        var surv = new Survival();
        surv.AssignRoles(roster);
        Assert.All(roster, p => Assert.NotEqual(Survival.SurvStatus.Hunter, surv.StatusOf(p)));
    }
}
