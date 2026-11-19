using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class RaceGridSpawnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QualifyingExpiryClearsScoresBeforeGridPlacementAtTheSelectedCountdownBoundary(bool deferred)
    {
        var previous = Api.Services;
        const string map = "race-grid-qualifying-lifetime";
        var world = new GameWorld(new CollisionWorld(), Course()) { MapName = map };
        RaceRecords.WriteTime(map, RaceRecords.RaceRecord, 4.25f, "grid-probe-record");
        world.Boot("rc", applyStartupOverrides: cvars =>
        {
            cvars.Set("g_warmup", "1");
            cvars.Set("g_start_delay", "0");
            cvars.Set("sv_ready_restart_after_countdown", deferred ? "1" : "0");
            cvars.Set("mapname", map);
            cvars.Set("timelimit", "12");
            cvars.Set("leadlimit", "2");
            cvars.Set("fraglimit", "8");
            cvars.Set("g_race_qualifying_timelimit", "0.025");
        });
        try
        {
            Player slower = Join(world, "Slower");
            Player faster = Join(world, "Faster");
            Player unranked = Join(world, "Unranked");
            Player observer = world.Clients.ClientConnect(netName: "Observer").Player;
            Assert.Equal(-400f, faster.Origin.X);
            Assert.Equal(-400f, slower.Origin.X);

            Assert.False(world.Warmup.WarmupStage);
            Assert.Equal(0f, world.Services.Cvars.GetFloat("fraglimit"));
            Assert.Equal(0f, world.Services.Cvars.GetFloat("leadlimit"));
            var fastest = GameScores.Field("RACE_FASTEST")!;
            var race = Assert.IsType<Race>(world.GameType);
            CompleteQualifyingLap(world, race, slower, ticks: 8);
            CompleteQualifyingLap(world, race, faster, ticks: 4);
            Assert.True(GameScores.Get(slower, fastest) > GameScores.Get(faster, fastest),
                $"slower={GameScores.Get(slower, fastest)} faster={GameScores.Get(faster, fastest)}");
            Assert.Equal(0, race.GetState(faster).LapsCompleted);
            StepUntil(world, () => world.Warmup.CountdownRunning);

            Assert.InRange(world.GameStartTime - world.Time, 9.9f, 10f);
            Assert.Equal(deferred, race.Qualifying);
            if (deferred)
            {
                Assert.Equal(-400f, faster.Origin.X);
                Assert.True(GameScores.Get(faster, fastest) > 0);
            }
            else
                Assert.Equal(-300f, faster.Origin.X);
            StepUntil(world, () => world.Time >= world.GameStartTime && !race.Qualifying);

            Assert.False(world.Intermission.Running);
            Assert.Equal(0, GameScores.Get(faster, fastest));
            Assert.Equal(0f, race.FastestLapOf(faster));
            Assert.Equal(12f, world.Services.Cvars.GetFloat("timelimit"));
            Assert.Equal(2f, world.Services.Cvars.GetFloat("leadlimit"));
            Assert.Equal(8f, world.Services.Cvars.GetFloat("fraglimit"));
            Assert.Equal(4.25f, race.ServerRecord);
            Assert.Equal(-300f, faster.Origin.X);
            Assert.Equal(-300f, slower.Origin.X);
            Assert.Equal(-300f, unranked.Origin.X);
            Assert.True(observer.IsObserver);
            Assert.Equal(-300f, Join(world, "Late").Origin.X);

        }
        finally
        {
            RaceRecords.DeleteTime(map, RaceRecords.RaceRecord, 1);
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Fact]
    public void ResetWithoutScoreClearUsesOrderedPlacesAndConsumesThem()
    {
        var previous = Api.Services;
        var world = new GameWorld(new CollisionWorld(), Course());
        try
        {
            BootQualifying(world);
            Player slower = Join(world, "Slower");
            Player faster = Join(world, "Faster");
            var fastest = GameScores.Field("RACE_FASTEST")!;
            GameScores.SetPlayer(slower, fastest, 200);
            GameScores.SetPlayer(faster, fastest, 100);
            Assert.Same(fastest, GameScores.Primary);
            Assert.True(GameScores.ComparePlayers(faster, slower) > 0,
                $"fastest flags {fastest.Flags}: {GameScores.Get(faster, fastest)} vs {GameScores.Get(slower, fastest)}");
            world.ResetMap(fakeRoundStart: true);
            Assert.False(Assert.IsType<Race>(world.GameType).Qualifying);
            Assert.Equal(-100f, faster.Origin.X);
            Assert.Equal(-200f, slower.Origin.X);
            KillAndWaitForRespawn(world, faster);
            Assert.Equal(-300f, faster.Origin.X);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Fact]
    public void QualifyingMapOverrideAndTeamLapDefaultsUseTheNativeBootOptions()
    {
        var previous = Api.Services;
        const string map = "race-qualifying-config";
        var world = new GameWorld(new CollisionWorld(), Course())
        {
            MapName = map,
            ConfigReader = path => path == $"maps/{map}.mapinfo"
                ? "gametype rc timelimit=10 qualifying_timelimit=5 laplimit=7 teamlaplimit=15"
                : null,
        };
        try
        {
            world.Boot("rc", applyStartupOverrides: cvars =>
            {
                cvars.Set("g_race_teams", "3");
                cvars.Set("g_race_qualifying_timelimit_override", "0");
                cvars.Set("g_race_laps_limit", "-1");
            });
            var race = Assert.IsType<Race>(world.GameType);
            Assert.True(world.Teamplay.IsTeamGame);
            Assert.Equal(3, world.Teamplay.TeamCount);
            Assert.False(race.Qualifying);
            Assert.Equal(10f, world.Services.Cvars.GetFloat("timelimit"));
            Assert.Equal(15, race.LapLimit);
            Assert.NotEqual(Teams.None, Join(world, "Team runner").Team);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Theory]
    [InlineData(1)] // Below the ready majority: qualifying goes on until expiry.
    [InlineData(2)] // Ready majority: takes the same restart path immediately.
    public void QualifyingReadyCommandStartsRaceAtTheCorrectTime(int readyCount)
    {
        var previous = Api.Services;
        var world = new GameWorld(new CollisionWorld(), Course());
        try
        {
            BootQualifying(world);
            var first = Join(world, "First");
            var second = Join(world, "Second");
            Join(world, "Third");
            var observer = world.Clients.ClientConnect(netName: "Observer").Player;
            world.Commands.Execute("ready", isServerConsole: false, caller: observer);
            Assert.Equal(0, world.Warmup.ReadyCount);
            world.Commands.Execute("ready", isServerConsole: false, caller: first);
            Assert.Equal(1, world.Warmup.ReadyCount);
            Assert.False(world.Warmup.CountdownRunning);
            if (readyCount == 2)
            {
                world.Commands.Execute("ready", isServerConsole: false, caller: second);
                Assert.True(world.Warmup.CountdownRunning);
            }
            else
                StepUntil(world, () => world.Warmup.CountdownRunning);
            Assert.Equal(0, world.Warmup.ReadyCount);
            Assert.False(Assert.IsType<Race>(world.GameType).Qualifying);
            Assert.Equal(-300f, first.Origin.X);
            Assert.False(world.Intermission.Running);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NoReadyWithoutTwoFinishesCanEndQualifying(int finishers)
    {
        var previous = Api.Services;
        var world = new GameWorld(new CollisionWorld(), Course());
        try
        {
            BootQualifying(world);
            var race = Assert.IsType<Race>(world.GameType);
            Player? leader = null;
            if (finishers == 1)
            {
                leader = Join(world, "Leader");
                Join(world, "Unranked");
                CompleteQualifyingLap(world, race, leader, ticks: 6);
            }
            StepUntil(world, () => world.Intermission.Running);
            Assert.Same(leader, world.Intermission.Winner);
            Assert.True(race.Qualifying);
            Assert.False(world.Warmup.CountdownRunning);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Fact]
    public void TiedNoReadyQualifyingUsesTimedOvertimeThenSuddenDeath()
    {
        var previous = Api.Services;
        var world = new GameWorld(new CollisionWorld(), Course());
        try
        {
            BootQualifying(world, overtimes: 1);
            Join(world, "First");
            Join(world, "Second");
            StepUntil(world, () => world.OverTime.OvertimesAdded == 1);
            Assert.False(world.Intermission.Running);
            Assert.InRange(world.Services.Cvars.GetFloat("timelimit"), 0.039f, 0.041f);
            StepUntil(world, () => world.OverTime.InSuddenDeath);
            Assert.False(world.Intermission.Running);
            StepUntil(world, () => world.Intermission.Running);
            Assert.Null(world.Intermission.Winner);
            Assert.True(Assert.IsType<Race>(world.GameType).Qualifying);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    private static void BootQualifying(GameWorld world, int overtimes = 0)
    {
        world.Boot("rc", applyStartupOverrides: cvars =>
        {
            cvars.Set("g_start_delay", "0");
            cvars.Set("g_race_qualifying_timelimit", "0.025");
            cvars.Set("timelimit_overtimes", overtimes.ToString());
            cvars.Set("timelimit_overtime", "0.015");
            cvars.Set("timelimit_suddendeath", "0.015");
        });
    }

    private static void CompleteQualifyingLap(GameWorld world, Race race, Player player, int ticks)
    {
        Entity start = race.Checkpoints.Single(checkpoint => checkpoint.GtCheckpointIndex == 0);
        Entity split = race.Checkpoints.Single(checkpoint => checkpoint.GtCheckpointIndex == 1);
        StepUntil(world, () => Api.Clock.Time > 0f);
        start.Touch!(start, player);
        Assert.Equal(1, race.GetState(player).NextCheckpoint);
        Assert.True(race.GetState(player).LapStartTime > 0f);
        for (int i = 0; i < ticks; i++)
            world.Frame(1f / 72f);
        split.Touch!(split, player);
        start.Touch!(start, player);
        Assert.True(GameScores.Get(player, GameScores.Field("RACE_FASTEST")!) > 0);
        world.Frame(1f / 72f);
        Assert.Equal(1, race.GetState(player).NextCheckpoint);
        Assert.True(race.GetState(player).LapStartTime > 0f);
    }

    private static void StepUntil(GameWorld world, System.Func<bool> condition)
    {
        for (int i = 0; i < 1800 && !condition(); i++)
            world.Frame(1f / 72f);
        Assert.True(condition());
    }

    [Fact]
    public void RaceDeathUsesTheSpawnTargetingTheLastCheckpointAndRequiresRecrossingIt()
    {
        var previous = Api.Services;
        List<EntityDict> course = Course();
        course.Add(Checkpoint(1, 650, "alternate-split"));
        course.Add(Spawn(550, "alternate-split", null));
        var world = new GameWorld(new CollisionWorld(), course);
        world.Boot("rc");
        try
        {
            var race = Assert.IsType<Race>(world.GameType);
            Player runner = Join(world, "Runner");
            Assert.Equal(-300f, runner.Origin.X);
            Entity start = race.Checkpoints.Single(c => c.GtCheckpointIndex == 0);
            Entity split = race.Checkpoints.Single(c => c.TargetName == "split");

            start.Touch!(start, runner);
            KillAndWaitForRespawn(world, runner);
            Assert.Equal(-50f, runner.Origin.X);
            start.Touch!(start, runner);
            split.Touch!(split, runner);
            split.Touch!(split, runner);
            Assert.False(runner.IsDead);

            KillAndWaitForRespawn(world, runner);
            Assert.Equal(250f, runner.Origin.X);
            Assert.Equal(90f, runner.Angles.Y);
            Assert.Equal(1, race.GetState(runner).NextCheckpoint);
            split.Touch!(split, runner);
            Assert.Equal(0, race.GetState(runner).NextCheckpoint);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Theory]
    [InlineData(250)] // Missing mid-course respawn as in the leave_em_behind overlay.
    [InlineData(-200)] // Missing intermediate grid place.
    [InlineData(-50)] // Missing place-zero start-line respawn.
    public void PinnedVerifierAcceptsHolesWhenTheEffectiveStartProbesPass(int missingSpot)
    {
        var previous = Api.Services;
        List<EntityDict> course = Course();
        course.RemoveAll(e => e.ClassName == "info_player_race" && e.Origin.X == missingSpot);
        var world = new GameWorld(new CollisionWorld(), course);
        try
        {
            world.Boot("rc");
            Player runner = Join(world, "Runner");
            Assert.Equal(-300f, runner.Origin.X);
            if (missingSpot == 250)
            {
                var race = Assert.IsType<Race>(world.GameType);
                foreach (Entity checkpoint in race.Checkpoints.OrderBy(e => e.GtCheckpointIndex))
                    checkpoint.Touch!(checkpoint, runner);
                KillAndWaitForRespawn(world, runner);
                // With no linked checkpoint spawn, the emergency pass ignores target evaluation.
                // The untargeted deathmatch point must still be excluded while Race spawn markers exist.
                Assert.Contains(course, e => e.ClassName == "info_player_race" && e.Origin.X == runner.Origin.X);
            }
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CheckpointOnlyCourseUsesOrdinaryDeathmatchFallback(bool hasDeathmatchSpawn)
    {
        var previous = Api.Services;
        List<EntityDict> course = Course();
        course.RemoveAll(e => e.ClassName == "info_player_race"
            || (!hasDeathmatchSpawn && e.ClassName == "info_player_deathmatch"));
        var world = new GameWorld(new CollisionWorld(), course);
        try
        {
            if (hasDeathmatchSpawn)
            {
                world.Boot("rc");
                Assert.Equal(800f, Join(world, "Runner").Origin.X);
            }
            else
            {
                var error = Assert.Throws<System.InvalidOperationException>(() => world.Boot("rc"));
                Assert.Contains("checkpoint 0 misses a spawnpoint with race_place=0 (race respawn)", error.Message);
            }
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Theory]
    [InlineData(-300, "checkpoint 0 misses a spawnpoint with race_place=0 (race respawn)")]
    [InlineData(-400, "checkpoint 0 misses a spawnpoint with race_place=-1 (qualifying)")]
    public void PinnedVerifierRejectsBrokenEffectiveStartProbes(int brokenSpot, string diagnostic)
    {
        var previous = Api.Services;
        List<EntityDict> course = Course();
        course.Single(e => e.Origin.X == brokenSpot).Fields["target"] = "missing-target";
        var world = new GameWorld(new CollisionWorld(), course) { MapName = "broken-grid" };
        try
        {
            var error = Assert.Throws<System.InvalidOperationException>(() => world.Boot("rc"));
            Assert.Contains(diagnostic, error.Message);
            Assert.Contains("broken-grid", error.Message);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    [Fact]
    public void DeathmatchDoesNotAdmitRaceGridOrValidateRaceCheckpoints()
    {
        var previous = Api.Services;
        var world = new GameWorld(new CollisionWorld(), Course());
        world.Boot("dm");
        try
        {
            Assert.Equal(800f, Join(world, "Deathmatch").Origin.X);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previous;
        }
    }

    private static Player Join(GameWorld world, string name)
    {
        Player player = world.Clients.ClientConnect(netName: name).Player;
        Assert.True(world.Clients.Join(player));
        return player;
    }

    private static void KillAndWaitForRespawn(GameWorld world, Player player)
    {
        Combat.Damage(player, player, player, 10000f, DeathTypes.Kill, player.Origin, Vector3.Zero);
        Assert.True(player.IsDead);
        for (int i = 0; i < 1800 && player.IsDead; i++)
            world.Frame(1f / 72f);
        Assert.False(player.IsDead);
    }

    private static List<EntityDict> Course() => new()
    {
        Spawn(-300, "start", "3"),
        Checkpoint(1, 400, "split"),
        Spawn(-200, "start", "2"),
        Spawn(250, "split", null),
        Spawn(-50, "start", "0"),
        Checkpoint(0, 0, "start"),
        Spawn(-100, "start", "1"),
        Spawn(-400, "start", "-1"),
        new EntityDict { ClassName = "info_player_deathmatch", Origin = new Vector3(800f, 0f, 24f) },
    };

    private static EntityDict Spawn(int x, string target, string? place)
    {
        var spawn = new EntityDict { ClassName = "info_player_race", Origin = new Vector3(x, 0f, 24f) };
        spawn.Fields["target"] = target;
        spawn.Fields["angle"] = "90";
        if (place is not null)
            spawn.Fields["race_place"] = place;
        return spawn;
    }

    private static EntityDict Checkpoint(int index, int x, string name)
    {
        var checkpoint = new EntityDict { ClassName = "trigger_race_checkpoint", Origin = new Vector3(x, 0f, 24f) };
        checkpoint.Fields["cnt"] = index.ToString();
        checkpoint.Fields["count"] = "90";
        checkpoint.Fields["targetname"] = name;
        checkpoint.Fields["spawnflags"] = "4";
        return checkpoint;
    }
}
