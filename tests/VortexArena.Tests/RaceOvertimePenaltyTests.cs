using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for T18 Race DEPTH: penalty zones (QC trigger_race_penalty / race_ImposePenaltyTime — freeze in a
/// plain race, accumulate in qualifying), sudden-death overtime (QC WinningCondition_Race: the lap limit alone
/// doesn't end the race), and team race (g_race_teams: laps add up into ST_RACE_LAPS).
/// </summary>
[Collection("GlobalState")]
public class RaceOvertimePenaltyTests
{
    private static EngineServices Facade()
    {
        var es = new EngineServices(new CollisionWorld());
        Api.Services = es;
        RaceRecords.Clear();
        // default: not qualifying, not team, a small lap limit
        Api.Cvars.Set("g_race_qualifying", "0");
        Api.Cvars.Set("g_race_teams", "0");
        Api.Cvars.Set("g_race_laps_limit", "2");
        return es;
    }

    private static void SetTime(EngineServices es, float t) =>
        typeof(GameClock).GetProperty("Time")!.SetValue(es.ClockImpl, t);

    private static Player NewPlayer(int team = Teams.None) => new Player { Team = team, Flags = EntFlags.Client };

    /// <summary>
    /// In a plain race a penalty zone FREEZES the racer (velocity 0 + MOVETYPE_NONE) until the penalty time
    /// elapses, then <see cref="Race.Tick"/> releases them (QC the rc PlayerPhysics race_penalty slice).
    /// </summary>
    [Fact]
    public void PenaltyZone_FreezesRacerThenReleases()
    {
        var es = Facade();
        var race = new Race();
        race.Activate();
        var p = NewPlayer();
        race.AddPlayer(p);
        p.MoveType = MoveType.Walk;
        p.Velocity = new Vector3(400, 0, 0);

        SetTime(es, 10f);
        race.SetPenalty(p, 3f); // QC race: freeze until time + 3
        Assert.True(race.IsPenalized(p));
        Assert.Equal(Vector3.Zero, p.Velocity);     // frozen
        Assert.Equal(MoveType.None, p.MoveType);

        // Still inside the penalty window → stays frozen.
        SetTime(es, 12f);
        race.Tick(12f);
        Assert.True(race.IsPenalized(p));
        Assert.Equal(MoveType.None, p.MoveType);

        // Past the penalty → released (movement restored).
        SetTime(es, 14f);
        race.Tick(14f);
        Assert.False(race.IsPenalized(p));
        Assert.Equal(MoveType.Walk, p.MoveType);
    }

    /// <summary>
    /// In QUALIFYING a penalty is added to the lap time (race_penalty_accumulator), with no freeze
    /// (QC race_ImposePenaltyTime qualifying branch).
    /// </summary>
    [Fact]
    public void PenaltyZone_Qualifying_AccumulatesNoFreeze()
    {
        var es = Facade();
        Api.Cvars.Set("g_race_qualifying", "1");
        var race = new Race();
        race.Activate();
        var p = NewPlayer();
        race.AddPlayer(p);
        p.MoveType = MoveType.Walk;

        race.SetPenalty(p, 4f);
        Assert.False(race.IsPenalized(p));            // no freeze in qualifying
        Assert.Equal(MoveType.Walk, p.MoveType);
        Assert.Equal(4f, race.PenaltyAccumulatorOf(p));
    }

    /// <summary>
    /// QC WinningCondition_Race: reaching the lap limit with another racer unfinished does NOT end the match —
    /// it starts SUDDEN DEATH (runs on). The match only ends once EVERYONE has finished.
    /// </summary>
    [Fact]
    public void Overtime_LapLimitWithUnfinished_StartsSuddenDeath()
    {
        var es = Facade(); // laps_limit = 2
        var race = new Race();
        race.Activate();
        var fast = NewPlayer();
        var slow = NewPlayer();
        race.AddPlayer(fast);
        race.AddPlayer(slow);

        // fast racer completes 2 laps (crossing the finish line three times: start, lap1, lap2). The first
        // crossing must be at t>0 so the lap timer (race_movetime baseline) is non-zero.
        SetTime(es, 1f); race.CrossCheckpoint(fast, 0, true);  // start lap 1
        SetTime(es, 11f); race.CrossCheckpoint(fast, 0, true); // close lap 1
        SetTime(es, 21f); race.CrossCheckpoint(fast, 0, true); // close lap 2 → hits the limit (FinishRacer)

        race.CheckWinningCondition();
        Assert.False(race.MatchEnded);   // not everyone finished → run on
        Assert.True(race.SuddenDeath);   // sudden-death overtime latched

        // The slow racer eventually finishes too → now the match ends.
        SetTime(es, 31f); race.CrossCheckpoint(slow, 0, true);
        SetTime(es, 41f); race.CrossCheckpoint(slow, 0, true);
        SetTime(es, 51f); race.CrossCheckpoint(slow, 0, true);
        race.CheckWinningCondition();
        Assert.True(race.MatchEnded);
    }

    /// <summary>
    /// Team race (g_race_teams): a racer's completed laps add up into their team's ST_RACE_LAPS (QC the team
    /// race branch). Two red racers each closing a lap give red 2 team laps.
    /// </summary>
    [Fact]
    public void TeamRace_LapsAddUpToTeam()
    {
        var es = Facade();
        Api.Cvars.Set("g_race_teams", "2");
        Api.Cvars.Set("g_race_laps_limit", "0"); // no lap limit so nobody "finishes" early
        var race = new Race();
        race.Activate();
        Assert.True(race.TeamGame);

        var r1 = NewPlayer(Teams.Red);
        var r2 = NewPlayer(Teams.Red);
        race.AddPlayer(r1);
        race.AddPlayer(r2);

        // r1 closes one lap (first crossing at t>0 so the lap timer baseline is non-zero).
        SetTime(es, 1f); race.CrossCheckpoint(r1, 0, true);
        SetTime(es, 11f); race.CrossCheckpoint(r1, 0, true);
        // r2 closes one lap.
        SetTime(es, 1f); race.CrossCheckpoint(r2, 0, true);
        SetTime(es, 13f); race.CrossCheckpoint(r2, 0, true);

        Assert.Equal(2, XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamScore(
            Teams.Red, XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamSlotSecondary));
    }
}
