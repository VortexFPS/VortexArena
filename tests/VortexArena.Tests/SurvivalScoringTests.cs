using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.8: Survival banked validkills + round-end scoring (QC GiveFragsForKill / Surv_UpdateScores) and
/// the hidden-role score anonymization hook.
/// </summary>
[Collection("GlobalState")]
public class SurvivalScoringTests
{
    private static Player NewPlayer() => new Player { Flags = EntFlags.Client };

    public SurvivalScoringTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameScores.AddPlayerScoreHook = null; // isolate from any leaked hook
    }

    [Fact]
    public void RoundEnd_AwardsBankedKillsAndSideBonuses()
    {
        var surv = new Survival();
        var hunter = NewPlayer();
        var prey = NewPlayer();

        surv.GetState(hunter).Status = Survival.SurvStatus.Hunter;
        surv.GetState(hunter).ValidKills = 3; // banked during the round
        surv.GetState(hunter).Alive = true;
        surv.GetState(prey).Status = Survival.SurvStatus.Prey;
        surv.GetState(prey).Alive = true;

        surv.UpdateScores(timedOut: false);

        // banked kills become SP_SCORE; the surviving hunter/prey get their per-side columns.
        Assert.Equal(3, GameScores.Get(hunter, GameScores.Score));
        Assert.Equal(1, GameScores.Get(hunter, GameScores.Field("SURV_HUNTS")!));
        Assert.Equal(1, GameScores.Get(prey, GameScores.Field("SURV_SURVIVALS")!));
        Assert.Equal(0, surv.GetState(hunter).ValidKills); // cleared after awarding
    }

    [Fact]
    public void TimedOut_RewardsSurvivingPrey()
    {
        var surv = new Survival();
        var prey = NewPlayer();
        surv.GetState(prey).Status = Survival.SurvStatus.Prey;
        surv.GetState(prey).Alive = true;

        surv.UpdateScores(timedOut: true); // g_survival_reward_survival defaults on
        // +1 SCORE for outlasting the timer, plus the SURV_SURVIVALS column.
        Assert.Equal(1, GameScores.Get(prey, GameScores.Score));
        Assert.Equal(1, GameScores.Get(prey, GameScores.Field("SURV_SURVIVALS")!));
    }

    [Fact]
    public void Anonymization_SuppressesKillsAndDeaths()
    {
        var surv = new Survival();
        surv.Activate(); // installs the AddPlayerScore anonymization hook
        var p = NewPlayer();

        GameScores.AddToPlayer(p, GameScores.Kills, 5);
        GameScores.AddToPlayer(p, GameScores.Deaths, 2);
        GameScores.AddToPlayer(p, GameScores.Score, 4); // SCORE is NOT anonymized

        Assert.Equal(0, GameScores.Get(p, GameScores.Kills));  // suppressed (won't out the hunter)
        Assert.Equal(0, GameScores.Get(p, GameScores.Deaths)); // suppressed
        Assert.Equal(4, GameScores.Get(p, GameScores.Score));  // score still accrues

        surv.Deactivate(); // removes the hook
        Assert.Null(GameScores.AddPlayerScoreHook);
    }
}
