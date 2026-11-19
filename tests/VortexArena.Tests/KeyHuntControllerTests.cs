using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Waypoints;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using VortexArena.Server.Bot;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class KeyHuntControllerTests
{
    [Theory]
    [InlineData("1.25", 3f)]
    [InlineData("0", 1f)]
    [InlineData("-1", -1f)]
    public void MatchStartAndTrackingUseTheControllerTick(string trackingDelay, float revealAfter)
    {
        var previousServices = Api.Services;
        var world = Boot(startDelay: "4", trackingDelay: trackingDelay);
        try
        {
            var kh = Assert.IsType<KeyHunt>(world.GameType);
            JoinTwo(world);
            Assert.Equal(4f, world.GameStartTime, 2);
            AdvanceUntil(world, () => world.Time >= 5.9f);
            Assert.Equal(KeyHunt.RoundPhase.WaitingForPlayers, kh.Phase);
            Assert.Empty(kh.WorldKeys);

            AdvanceUntil(world, () => kh.Phase == KeyHunt.RoundPhase.Countdown);
            Assert.InRange(kh.RoundStartTime - world.Time, 2.97f, 3.02f);
            float roundDeadline = kh.RoundStartTime;
            AdvanceUntil(world, () => kh.Phase == KeyHunt.RoundPhase.InProgress);
            Assert.InRange(world.Time - roundDeadline, 0f, 0.06f);
            Assert.Equal(new[] { Teams.Blue, Teams.Red }, kh.WorldKeys.Select(key => key.HomeTeam));
            Assert.False(kh.TrackingEnabled);

            float spawned = world.Time;
            if (revealAfter < 0f)
            {
                AdvanceUntil(world, () => world.Time >= spawned + 4f);
                Assert.False(kh.TrackingEnabled);
                return;
            }

            AdvanceUntil(world, () => world.Time >= spawned + revealAfter - 0.05f);
            Assert.False(kh.TrackingEnabled);
            AdvanceUntil(world, () => kh.TrackingEnabled);
            Assert.InRange(world.Time - spawned, revealAfter - 0.02f, revealAfter + 0.06f);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previousServices;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmupUsesTheRosterAndResetClearsKeysAtTheConfiguredBoundary(bool deferReset)
    {
        var previousServices = Api.Services;
        var world = Boot(warmup: "1", deferReset: deferReset);
        try
        {
            var kh = Assert.IsType<KeyHunt>(world.GameType);
            var (first, second) = JoinTwo(world);
            Assert.True(world.Warmup.WarmupStage);

            // Chatting players cannot begin the round, even when every team has joined.
            world.InputProvider = p => new MovementInput
            {
                Typing = ReferenceEquals(p, second),
                FrameTime = world.Simulation.FrameTime,
            };
            AdvanceUntil(world, () => world.Time >= 5f);
            Assert.True(kh.AnyTeamMissing());
            Assert.Empty(kh.WorldKeys);
            world.InputProvider = _ => new MovementInput { FrameTime = world.Simulation.FrameTime };
            AdvanceUntil(world, () => kh.Phase == KeyHunt.RoundPhase.InProgress);
            Assert.True(world.Warmup.WarmupStage);
            Assert.Equal(2, kh.WorldKeys.Count);

            world.Commands.Execute("ready", isServerConsole: false, caller: first);
            world.Commands.Execute("ready", isServerConsole: false, caller: second);
            Assert.True(world.Warmup.CountdownRunning);
            Assert.Equal(deferReset, world.Warmup.HasDeferredReset);
            Assert.Equal(deferReset ? 2 : 0, kh.WorldKeys.Count);

            AdvanceUntil(world, () => world.Time >= world.GameStartTime);
            Assert.Empty(kh.WorldKeys);
            Assert.Null(first.GtCarried);
            Assert.Null(second.GtCarried);
            AdvanceUntil(world, () => kh.Phase == KeyHunt.RoundPhase.InProgress);
            Assert.Equal(2, kh.WorldKeys.Count);
            Assert.False(world.Warmup.WarmupStage);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previousServices;
        }
    }

    [Fact]
    public void MatchEndRemovesActiveKeysAndStopsTheController()
    {
        var previousServices = Api.Services;
        var world = Boot();
        try
        {
            var kh = Assert.IsType<KeyHunt>(world.GameType);
            var (first, second) = JoinTwo(world);
            AdvanceUntil(world, () => kh.Phase == KeyHunt.RoundPhase.InProgress);
            Entity[] keys = kh.WorldKeys.Select(key => key.Entity!).ToArray();

            world.Commands.Execute("endmatch", isServerConsole: true);
            AdvanceUntil(world, () => world.Intermission.Running && kh.WorldKeys.Count == 0);
            Assert.Empty(kh.WorldKeys);
            Assert.All(keys, key => Assert.True(key.IsFreed));
            Assert.Null(first.GtCarried);
            Assert.Null(second.GtCarried);
            float pastDeadline = world.Time + 5f;
            AdvanceUntil(world, () => world.Time >= pastDeadline);
            Assert.Empty(kh.WorldKeys);
            Assert.False(kh.TrackingEnabled);
        }
        finally
        {
            world.Shutdown();
            Api.Services = previousServices;
        }
    }

    [Fact]
    public void BotUsesWorldKeyOrderEvenWhenTrackingHidesPlayerMarkers()
    {
        var previousServices = Api.Services;
        bool previousWarmup = NotificationSystem.WarmupStage;
        Api.Services = new EngineServices(new CollisionWorld());
        NotificationSystem.WarmupStage = false;
        try
        {
            var kh = new KeyHunt();
            var red = new Player { Team = Teams.Red };
            var blue = new Player { Team = Teams.Blue, Origin = Vector3.UnitX * 256f };
            var yellow = new Player { Team = Teams.Yellow, Origin = -Vector3.UnitX * 256f };
            var bot = new Player { Team = Teams.Red, IsBot = true };
            kh.SetRoster(new[] { red, blue, yellow });
            kh.StartRound();
            var markers = new List<WaypointSprite>();
            kh.CollectWaypoints(markers);
            Assert.False(kh.TrackingEnabled);
            Assert.False(markers.Single(marker => ReferenceEquals(marker.Owner, yellow)).VisibleForPlayer!(bot));
            Assert.False(markers.Single(marker => ReferenceEquals(marker.Owner, blue)).VisibleForPlayer!(bot));

            // An unrelated classname match could beat real targets at this position and weight.
            Entity unlisted = Api.Entities.Spawn();
            unlisted.ClassName = "item_kh_key";
            var brain = new BotBrain(bot) { GameType = kh, GameTypeNetName = "kh", KhRole = KhBotRole.Offense };
            var rater = new GoalRater();
            BotRoles.ChooseRole("kh")(brain, rater);

            Assert.Same(yellow, rater.Best.Target); // Equal enemy ratings, newest key first.
            Assert.True(kh.DropOneKey(yellow));
            markers.Clear();
            kh.CollectWaypoints(markers);
            Entity dropped = kh.KeyOf(Teams.Yellow)!.Entity!;
            Assert.False(markers.Single(marker => ReferenceEquals(marker.Owner, dropped)).VisibleForPlayer!(bot));
            brain.KhRole = KhBotRole.Freelancer;
            BotRoles.ChooseRole("kh")(brain, rater);
            Assert.Same(dropped, rater.Best.Target);
        }
        finally
        {
            Api.Services = previousServices;
            NotificationSystem.WarmupStage = previousWarmup;
        }
    }

    private static GameWorld Boot(string startDelay = "0", string trackingDelay = "1.25", string warmup = "0",
        bool deferReset = false)
    {
        var world = new GameWorld(new CollisionWorld(),
        [
            new EntityDict("worldspawn"),
            new EntityDict("info_player_deathmatch", new Vector3(0, 0, 64)),
            new EntityDict("info_player_deathmatch", new Vector3(256, 0, 64)),
        ]);
        world.Boot("kh", applyStartupOverrides: cvars =>
        {
            cvars.Set("g_keyhunt_teams_override", "2");
            cvars.Set("g_start_delay", startDelay);
            cvars.Set("g_warmup", warmup);
            cvars.Set("g_warmup_limit", "-1");
            cvars.Set("g_balance_keyhunt_delay_round", "1.25");
            cvars.Set("g_balance_keyhunt_delay_tracking", trackingDelay);
            cvars.Set("sv_ready_restart_after_countdown", deferReset ? "1" : "0");
        });
        return world;
    }

    private static (Player, Player) JoinTwo(GameWorld world)
    {
        Player first = world.Clients.ClientConnect(netName: "first").Player;
        Player second = world.Clients.ClientConnect(netName: "second").Player;
        AdvanceUntil(world, () => world.Time > 1.1f);
        world.Commands.Execute("join", isServerConsole: false, caller: first);
        world.Commands.Execute("join", isServerConsole: false, caller: second);
        Assert.False(first.IsObserver);
        Assert.False(second.IsObserver);
        Assert.NotEqual(first.Team, second.Team);
        return (first, second);
    }

    private static void AdvanceUntil(GameWorld world, Func<bool> done)
    {
        for (int i = 0; i < 1600 && !done(); i++)
            world.Frame(1f / 64f);
        Assert.True(done());
    }
}
