using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.7: the Invasion g_invasion_type variants (ROUND/HUNT/STAGE) and per-wave monster lists
/// (invasion_wave spawnmob), including the wave-entity resolution (exact + fallback).
/// </summary>
[Collection("GlobalState")]
public class InvasionVariantsTests
{
    private static EngineServices Facade()
    {
        var f = new EngineServices(new CollisionWorld());
        Api.Services = f;
        return f;
    }

    [Fact]
    public void Type_DefaultsToRound()
    {
        Facade();
        var inv = new Invasion();
        Assert.Equal(Invasion.InvasionType.Round, inv.Type);
    }

    [Fact]
    public void PerWaveMonsterList_ResolvesExactThenFallback()
    {
        Facade();
        var inv = new Invasion();
        inv.AddWave(1, "zombie spider");
        inv.AddWave(3, "golem");

        // exact match
        Assert.Equal(new[] { "zombie", "spider" }, inv.GetWaveMonsters(1));
        // round 2 has no exact wave → falls back to the last list with number <= 2 (wave 1)
        Assert.Equal(new[] { "zombie", "spider" }, inv.GetWaveMonsters(2));
        // round 4 → falls back to wave 3
        Assert.Equal(new[] { "golem" }, inv.GetWaveMonsters(4));
        // round 0 → no wave at or below → null (random pick)
        Assert.Null(inv.GetWaveMonsters(0));
    }

    [Fact]
    public void HuntType_WinsWhenAllSpawnedMonstersCleared()
    {
        var f = Facade();
        f.Cvars.Set("g_invasion_type", "1"); // INV_TYPE_HUNT
        var inv = new Invasion();
        inv.Activate();
        Assert.Equal(Invasion.InvasionType.Hunt, inv.Type);

        // simulate: the placed set fully spawned and all killed, none alive.
        inv.Wave.MaxSpawned = 3;
        inv.Wave.Spawned = 3;
        inv.Wave.Killed = 3;
        inv.LiveMonsters.Clear();

        inv.Tick();
        Assert.True(inv.MatchEnded); // hunt cleared → players win
    }

    [Fact]
    public void HuntType_DoesNotAdvanceRounds()
    {
        var f = Facade();
        f.Cvars.Set("g_invasion_type", "1");
        var inv = new Invasion();
        inv.Activate();
        inv.Wave.MaxSpawned = 2;
        inv.Wave.Spawned = 2;
        inv.Wave.Killed = 2;
        bool advanced = inv.AdvanceWaveIfCleared();
        Assert.False(advanced);
        Assert.Equal(1, inv.Wave.Round); // hunt never rolls the round over
    }

    [Fact]
    public void StageType_WinsWhenRoundEndTriggered()
    {
        var f = Facade();
        f.Cvars.Set("g_invasion_type", "2"); // INV_TYPE_STAGE
        var inv = new Invasion();
        inv.Activate();
        Assert.Equal(Invasion.InvasionType.Stage, inv.Type);

        Assert.False(inv.RoundEndReached);
        inv.TriggerRoundEnd(); // QC target_invasion_roundend_use — players reached the end
        Assert.True(inv.RoundEndReached);

        inv.Tick();
        Assert.True(inv.MatchEnded);
    }

    [Fact]
    public void MonsterSkill_ScalesWithRoundAndPlayers()
    {
        Facade();
        var inv = new Invasion { PlayerCount = 4 };
        // QC: skill = round + max(1, players*0.3). round 1, 4 players => 1 + max(1, 1.2)=1+1=2
        Assert.Equal(2, inv.ComputeMonsterSkill(1));
        // round 5 => 5 + 1 = 6
        Assert.Equal(6, inv.ComputeMonsterSkill(5));
    }

    [Fact]
    public void PointLimit_ExplicitZero_MeansNoLimit()
    {
        // QC GameRules_limit_score(0) sets fraglimit=0; WinningCondition_Scores gate is
        // (limit && score>=limit) — when limit=0 that is always false → no limit ever fires.
        // The port must return 0 from PointLimit (not the mapinfo default 50) so CheckPointLimit
        // skips the check (limit<=0 → return early).
        var f = Facade();
        f.Cvars.Set("g_invasion_point_limit", "0");
        var inv = new Invasion();
        Assert.Equal(0, inv.PointLimit); // explicit 0 = no kill cap
    }

    [Fact]
    public void PointLimit_NegativeSentinel_FallsBackToDefault50()
    {
        // QC: g_invasion_point_limit default -1 → GameRules_limit_score(-1) returns early (no fraglimit write)
        // → mapinfo pointlimit=50. With no fraglimit cvar set the port returns the 50 default.
        var f = Facade();
        f.Cvars.Set("g_invasion_point_limit", "-1");
        var inv = new Invasion();
        Assert.Equal(50, inv.PointLimit);
    }

    [Fact]
    public void HuntType_EmptyMap_InstaWins()
    {
        // QC: "NOTE: this ends the round if no monsters are placed" — Base WINNING_YES immediately on 0 found.
        var f = Facade();
        f.Cvars.Set("g_invasion_type", "1"); // INV_TYPE_HUNT
        var inv = new Invasion();
        inv.Activate();
        // No monsters placed/spawned; LiveMonsters is empty; world entity scan returns 0.
        inv.Tick();
        Assert.True(inv.MatchEnded); // empty HUNT map → insta-win (matches Base behavior)
    }
}
