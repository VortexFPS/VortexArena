// Tests for T36: the Nexball goal + ball map spawnfuncs (nexball_redgoal/.../fault/out + nexball_basketball/
// football + the ball_* compat aliases) wiring the BSP entity lump through GametypeObjectiveSpawns.Sink →
// GameWorld.WireObjectiveSpawns (Nexball arm) → Nexball.SpawnGoal / SpawnBall.
//
// Port of: common/gametypes/gametype/nexball/sv_nexball.qc spawnfuncs (lines 525-712). Key parity:
//  - the PORT sentinels Nexball.GoalFault (-2) / GoalOut (-3) are used, NOT QC's raw GOAL_FAULT=-1/GOAL_OUT=-2;
//  - ball_redgoal/ball_bluegoal are INTENTIONALLY swapped ("I blame Revenant", sv_nexball.qc:697-704);
//  - SpawnBall sets BallHome (QC spawnorigin = origin) so ResetBall returns the ball home after a goal.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

[Collection("GlobalState")]
public sealed class NexballSpawnTests
{
    // The gametype is a registry SINGLETON, so a prior test's match leaves the unified GameScores team table
    // populated; Nexball.Activate() only zeroes it on the FIRST activation (the _deathHandler guard skips a
    // re-activate). Reset the team-score table before each test so goal tallies start clean regardless of order.
    public NexballSpawnTests() => XonoticGodot.Common.Gameplay.Scoring.GameScores.ResetTeams();

    private static EntityDict Dict(string cls, Vector3 origin = default)
        => new() { ClassName = cls, Origin = origin };

    private static GameWorld BootNexballMap(IEnumerable<EntityDict> ents)
    {
        var world = new GameWorld(new CollisionWorld(), ents.ToList());
        world.Boot("nb"); // Nexball
        return world;
    }

    [Fact]
    public void GoalSpawnfuncs_RegisterGoalsWithCorrectTeamsAndSentinels()
    {
        GameWorld world = BootNexballMap(new[]
        {
            Dict("nexball_redgoal", new Vector3(100, 0, 0)),
            Dict("nexball_bluegoal", new Vector3(-100, 0, 0)),
            Dict("nexball_yellowgoal", new Vector3(0, 100, 0)),
            Dict("nexball_pinkgoal", new Vector3(0, -100, 0)),
            Dict("nexball_fault", new Vector3(0, 0, 100)),
            Dict("nexball_out", new Vector3(0, 0, -100)),
        });
        var nb = Assert.IsType<Nexball>(world.GameType);

        Assert.Equal(6, nb.Goals.Count);
        // each goal's GtHomeTeam carries its team color or the PORT fault/out sentinel.
        Assert.Contains(nb.Goals, g => g.GtHomeTeam == Teams.Red);
        Assert.Contains(nb.Goals, g => g.GtHomeTeam == Teams.Blue);
        Assert.Contains(nb.Goals, g => g.GtHomeTeam == Teams.Yellow);
        Assert.Contains(nb.Goals, g => g.GtHomeTeam == Teams.Pink);
        Assert.Contains(nb.Goals, g => g.GtHomeTeam == Nexball.GoalFault); // -2 (port), NOT QC's -1
        Assert.Contains(nb.Goals, g => g.GtHomeTeam == Nexball.GoalOut);   // -3 (port), NOT QC's -2
    }

    [Fact]
    public void BallSpawnfunc_SpawnsBall_AndSetsBallHome()
    {
        var spawn = new Vector3(64, 128, 256);
        GameWorld world = BootNexballMap(new[] { Dict("nexball_basketball", spawn) });
        var nb = (Nexball)world.GameType!;

        Assert.NotNull(nb.BallEntity);
        // QC SpawnBall: this.spawnorigin = this.origin → BallHome must be the spawn origin so ResetBall returns it.
        Assert.Equal(spawn, nb.BallHome);
    }

    [Fact]
    public void FootballAlias_AlsoSpawnsBall()
    {
        GameWorld world = BootNexballMap(new[] { Dict("nexball_football", new Vector3(1, 2, 3)) });
        var nb = (Nexball)world.GameType!;
        Assert.NotNull(nb.BallEntity);
        Assert.Equal(new Vector3(1, 2, 3), nb.BallHome);
    }

    [Fact]
    public void BallRedGoal_IsSwappedToBlue_AndBallBlueGoalToRed()
    {
        // QC compat: spawnfunc(ball_redgoal){ spawnfunc_nexball_bluegoal(this); } and vice versa.
        GameWorld world = BootNexballMap(new[]
        {
            Dict("ball_redgoal", new Vector3(100, 0, 0)),  // → BLUE goal
            Dict("ball_bluegoal", new Vector3(-100, 0, 0)), // → RED goal
        });
        var nb = (Nexball)world.GameType!;

        Entity redGoalEntity = nb.Goals.First(g => g.Origin.X > 0);  // spawned as ball_redgoal
        Entity blueGoalEntity = nb.Goals.First(g => g.Origin.X < 0); // spawned as ball_bluegoal
        Assert.Equal(Teams.Blue, redGoalEntity.GtHomeTeam); // the swap
        Assert.Equal(Teams.Red, blueGoalEntity.GtHomeTeam);
    }

    [Fact]
    public void BallAliases_SpawnBalls()
    {
        // ball / ball_football / ball_basketball all spawn a ball.
        foreach (string cls in new[] { "ball", "ball_football", "ball_basketball" })
        {
            GameWorld world = BootNexballMap(new[] { Dict(cls, new Vector3(5, 5, 5)) });
            var nb = (Nexball)world.GameType!;
            Assert.NotNull(nb.BallEntity);
        }
    }

    [Fact]
    public void EndToEnd_BallIntoEnemyGoal_ScoresForTheBallTeam()
    {
        GameWorld world = BootNexballMap(new[]
        {
            Dict("nexball_basketball", new Vector3(0, 0, 0)),
            Dict("nexball_bluegoal", new Vector3(200, 0, 0)), // blue's goal — a red ball here scores for red
        });
        var nb = (Nexball)world.GameType!;

        // A red player picks up the ball (becomes ball.team = red).
        var redPlayer = new Player { NetName = "red", Team = Teams.Red, Health = 100f };
        nb.GiveBall(redPlayer);
        Assert.Equal(Teams.Red, nb.BallTeam);

        Entity blueGoal = nb.Goals.First(g => g.GtHomeTeam == Teams.Blue);
        Assert.Equal(0, nb.GoalsFor(Teams.Red));

        // Fire the goal volume's touch with the ball (QC GoalTouch) — the spawnfunc wired the touch handler.
        Assert.NotNull(blueGoal.Touch);
        blueGoal.Touch!(blueGoal, nb.BallEntity!);

        // Ball entered the ENEMY (blue) goal → +1 for red.
        Assert.Equal(1, nb.GoalsFor(Teams.Red));
        // After a goal the ball resets (team cleared, returned to home).
        Assert.Equal(Teams.None, nb.BallTeam);
    }

    [Fact]
    public void EndToEnd_OwnGoal_CreditsTheOtherTeam()
    {
        GameWorld world = BootNexballMap(new[]
        {
            Dict("nexball_basketball", new Vector3(0, 0, 0)),
            Dict("nexball_redgoal", new Vector3(200, 0, 0)), // red's OWN goal
        });
        var nb = (Nexball)world.GameType!;

        var redPlayer = new Player { NetName = "red", Team = Teams.Red, Health = 100f };
        nb.GiveBall(redPlayer);

        Entity redGoal = nb.Goals.First(g => g.GtHomeTeam == Teams.Red);
        redGoal.Touch!(redGoal, nb.BallEntity!);

        // QC two-team own-goal: the point goes to the OTHER team (blue), not docked from red.
        Assert.Equal(1, nb.GoalsFor(Teams.Blue));
        Assert.Equal(0, nb.GoalsFor(Teams.Red));
    }

    [Fact]
    public void EndToEnd_OutVolume_ReturnsBall_NoScore()
    {
        GameWorld world = BootNexballMap(new[]
        {
            Dict("nexball_basketball", new Vector3(0, 0, 0)),
            Dict("nexball_out", new Vector3(200, 0, 0)),
        });
        var nb = (Nexball)world.GameType!;

        var redPlayer = new Player { NetName = "red", Team = Teams.Red, Health = 100f };
        nb.GiveBall(redPlayer);

        Entity outVol = nb.Goals.First(g => g.GtHomeTeam == Nexball.GoalOut);
        outVol.Touch!(outVol, nb.BallEntity!);

        // GOAL_OUT: ball returned, no score for anyone.
        Assert.Equal(0, nb.GoalsFor(Teams.Red));
        Assert.Equal(0, nb.GoalsFor(Teams.Blue));
        Assert.Equal(Teams.None, nb.BallTeam); // reset
    }

    // ---- Wave 6a: ball-spawn presentation/collision (QC SpawnBall EF_LOWPRECISION + playerclip mask) ----

    [Fact]
    public void BallSpawn_AppliesLowPrecisionAndPlayerclipMask()
    {
        // QC SpawnBall: this.effects |= EF_LOWPRECISION; and, when g_nexball_playerclip_collisions (default 1),
        // dphitcontentsmask = DPCONTENTS_BODY | DPCONTENTS_SOLID | DPCONTENTS_PLAYERCLIP.
        GameWorld world = BootNexballMap(new[] { Dict("nexball_basketball", new Vector3(0, 0, 0)) });
        var nb = (Nexball)world.GameType!;
        Entity ball = nb.BallEntity!;

        const int EfLowPrecision = 4194304; // QC EF_LOWPRECISION
        const int PlayerclipMask = 0x02000000 | 0x00000001 | 256; // BODY | SOLID | PLAYERCLIP
        Assert.True((ball.Effects & EfLowPrecision) != 0, "EF_LOWPRECISION must be set on the ball");
        Assert.Equal(PlayerclipMask, ball.DpHitContentsMask);
    }

    [Fact]
    public void CarrierHighspeed_DefaultsTo_0_8()
    {
        // QC g_nexball_basketball_carrier_highspeed default 0.8 (gametypes-server.cfg) — the carrier is slowed.
        GameWorld world = BootNexballMap(new[] { Dict("nexball_basketball", new Vector3(0, 0, 0)) });
        var nb = (Nexball)world.GameType!;
        Assert.Equal(0.8f, nb.CarrierHighspeed, 3);
    }
}
