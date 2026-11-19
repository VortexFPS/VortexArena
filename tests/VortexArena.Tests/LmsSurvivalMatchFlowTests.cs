using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class LmsSurvivalMatchFlowTests
{
    [Fact]
    public void LmsResetsAtCountdownStartAndRespawnsMidMatchWithoutChangingTheSharedSetting()
    {
        var previousServices = Api.Services;
        var cvars = new CvarService();
        var world = NewWorld(cvars);
        world.Boot("lms", applyStartupOverrides: cv =>
        {
            cv.Set("sv_ready_restart_after_countdown", "1");
            cv.Set("g_warmup", "1");
            cv.Set("g_warmup_limit", "-1");
            cv.Set("g_lms_lives_override", "3");
            cv.Set("g_spawnshieldtime", "0");
            cv.Set("g_forced_respawn", "1");
        });
        var lms = Assert.IsType<LastManStanding>(world.GameType);
        try
        {
            var (first, second) = JoinTwo(world);
            Assert.True(world.Warmup.WarmupStage);
            Ready(world, first);
            Ready(world, second);

            Assert.False(world.Warmup.HasDeferredReset);
            Assert.True(world.Warmup.CountdownRunning);
            Assert.Null(world.Rounds);
            Assert.Equal(world.GameStartTime, first.SpawnShieldExpire, 3);
            AdvanceUntil(world, () => world.Time > world.GameStartTime);

            Kill(second, first);
            Assert.True(second.IsDead);
            Assert.Equal(2, lms.LivesOf(second));
            AdvanceUntil(world, () => !second.IsDead);
            Assert.Equal(2, lms.LivesOf(second));

            world.Commands.Execute("restart", isServerConsole: true);
            Assert.True(world.Warmup.CountdownRunning);
            Assert.False(world.Warmup.HasDeferredReset);
            Assert.Equal(3, lms.LivesOf(second));
            Assert.Equal(world.GameStartTime, second.SpawnShieldExpire, 3);
        }
        finally
        {
            lms.Deactivate();
            world.Shutdown();
            Api.Services = previousServices;
        }

        // Map replacement reuses the server cvar store. An ordinary mode can still defer its reset.
        var dm = NewWorld(cvars);
        dm.Boot("dm");
        try
        {
            dm.Commands.Execute("restart", isServerConsole: true);
            Assert.Equal("1", cvars.GetString("sv_ready_restart_after_countdown"));
            Assert.True(dm.Warmup.HasDeferredReset);
        }
        finally
        {
            (dm.GameType as Deathmatch)?.Deactivate();
            dm.Shutdown();
            Api.Services = previousServices;
        }
    }

    [Theory]
    [InlineData("3", "0", "0")]
    [InlineData("1", "3", "1")]
    public void SurvivalBanksRoundPointsAnnouncesOnceAndEndsAtTheMatchLimit(
        string fragLimit, string leadLimit, string deferReset)
    {
        var previousServices = Api.Services;
        var previousSink = NotificationSystem.Sink;
        var notifications = new NotificationSystem.RecordingSink();
        var world = NewWorld();
        world.Boot("surv", applyStartupOverrides: cv =>
        {
            cv.Set("g_warmup", "1");
            cv.Set("g_warmup_limit", "-1");
            cv.Set("fraglimit", fragLimit);
            cv.Set("leadlimit", leadLimit);
            cv.Set("leadlimit_and_fraglimit", "1");
            cv.Set("sv_ready_restart_after_countdown", deferReset);
            cv.Set("g_survival_warmup", "1");
            cv.Set("g_survival_round_enddelay", "0");
            cv.Set("g_spawnshieldtime", "0");
        });
        var survival = Assert.IsType<Survival>(world.GameType);
        NotificationSystem.Sink = notifications;
        try
        {
            var (first, second) = JoinTwo(world);
            Assert.Empty(FragsCues(notifications));
            Ready(world, first);
            Ready(world, second);
            Assert.Empty(FragsCues(notifications));
            AdvanceUntil(world, () => world.Time > world.GameStartTime && survival.RoleAssigned);
            Assert.Equal(new[] { "ANNCE_REMAINING_FRAG_3" }, FragsCues(notifications));

            for (int i = 1; i <= 3; i++)
            {
                Assert.False(first.IsDead);
                Assert.False(second.IsDead);
                Assert.False(survival.RoundOver);
                Assert.NotEqual(survival.StatusOf(first), survival.StatusOf(second));
                Kill(second, first);
                Assert.True(second.IsDead);
                Advance(world, 0.1f);
                Assert.Equal(i, GameScores.PrimaryScore(first));
                Assert.Equal(i == 3, world.Intermission.Running);
                Assert.True(survival.RoundOver);
                if (i < 3)
                    AdvanceUntil(world, () => survival.RoleAssigned && !survival.RoundOver);
            }

            Assert.Same(first, world.Intermission.Winner);
            Assert.Equal(new[] { "ANNCE_REMAINING_FRAG_3", "ANNCE_REMAINING_FRAG_2", "ANNCE_REMAINING_FRAG_1" },
                FragsCues(notifications));

            world.Commands.Execute("restart", isServerConsole: true);
            Assert.Equal(deferReset == "1", world.Warmup.HasDeferredReset);
            Assert.Equal(deferReset == "1" ? 3 : 0, GameScores.PrimaryScore(first));
            AdvanceUntil(world, () => world.Time > world.GameStartTime && survival.RoleAssigned);
            Assert.Equal(0, GameScores.PrimaryScore(first));
            Assert.Equal(2, FragsCues(notifications).Count(name => name == "ANNCE_REMAINING_FRAG_3"));
        }
        finally
        {
            survival.Deactivate();
            world.Shutdown();
            NotificationSystem.Sink = previousSink;
            Api.Services = previousServices;
        }
    }

    [Fact]
    public void SurvivalSuddenDeathExpiryDoesNotAnnounceAForcedFrag()
    {
        var previousServices = Api.Services;
        var previousSink = NotificationSystem.Sink;
        var notifications = new NotificationSystem.RecordingSink();
        var world = NewWorld();
        world.Boot("surv", applyStartupOverrides: cv =>
        {
            cv.Set("g_warmup", "0");
            cv.Set("fraglimit", "12");
            cv.Set("timelimit", "0.05");
            cv.Set("timelimit_overtimes", "0");
            cv.Set("timelimit_suddendeath", "0.01");
        });
        var survival = Assert.IsType<Survival>(world.GameType);
        NotificationSystem.Sink = notifications;
        try
        {
            JoinTwo(world);
            AdvanceUntil(world, () => world.OverTime.InSuddenDeath);
            Assert.False(world.Intermission.Running);
            AdvanceUntil(world, () => world.Intermission.Running);
            Assert.Null(world.Intermission.Winner);
            Assert.Empty(FragsCues(notifications));
        }
        finally
        {
            survival.Deactivate();
            world.Shutdown();
            NotificationSystem.Sink = previousSink;
            Api.Services = previousServices;
        }
    }

    [Fact]
    public void SurvivalMapReplacementStartsWithOnlyTheNewRoster()
    {
        var previousServices = Api.Services;
        var cvars = new CvarService();
        var previous = NewWorld(cvars);
        previous.Boot("surv", applyStartupOverrides: cv => cv.Set("g_survival_warmup", "1"));
        var oldMode = Assert.IsType<Survival>(previous.GameType);
        var (first, second) = JoinTwo(previous);
        AdvanceUntil(previous, () => oldMode.RoleAssigned);
        previous.Shutdown();

        var world = NewWorld(cvars);
        world.Boot("surv");
        var survival = Assert.IsType<Survival>(world.GameType);
        try
        {
            JoinTwo(world);
            AdvanceUntil(world, () => survival.RoleAssigned);
            Assert.Equal(Survival.SurvStatus.None, survival.StatusOf(first));
            Assert.Equal(Survival.SurvStatus.None, survival.StatusOf(second));
            Assert.Equal(1, survival.AliveHunters());
            Assert.Equal(1, survival.AlivePrey());
        }
        finally
        {
            survival.Deactivate();
            world.Shutdown();
            Api.Services = previousServices;
        }
    }

    private static GameWorld NewWorld(CvarService? cvars = null) => new(new CollisionWorld(),
    [
        new EntityDict("worldspawn"),
        new EntityDict("info_player_deathmatch", new Vector3(0, 0, 64)),
        new EntityDict("info_player_deathmatch", new Vector3(256, 0, 64))
    ], cvars);

    private static (Player First, Player Second) JoinTwo(GameWorld world)
    {
        var first = world.Clients.ClientConnect(netName: "first").Player;
        var second = world.Clients.ClientConnect(netName: "second").Player;
        Advance(world, 1.1f);
        world.Commands.Execute("join", isServerConsole: false, caller: first);
        world.Commands.Execute("join", isServerConsole: false, caller: second);
        Assert.False(first.IsObserver);
        Assert.False(second.IsObserver);
        return (first, second);
    }

    private static void Ready(GameWorld world, Player player) =>
        world.Commands.Execute("ready", isServerConsole: false, caller: player);

    private static void Kill(Player victim, Player attacker) =>
        Combat.Damage(victim, attacker, attacker, 10000f, DeathTypes.Kill, victim.Origin, Vector3.Zero);

    private static string[] FragsCues(NotificationSystem.RecordingSink sink) => sink.Log
        .Select(dispatch => dispatch.Notification.RegistryName)
        .Where(name => name.StartsWith("ANNCE_REMAINING_FRAG_", StringComparison.Ordinal))
        .ToArray();

    private static void Advance(GameWorld world, float seconds)
    {
        float target = world.Time + seconds;
        while (world.Time < target)
            world.Frame(1f / 64f);
    }

    private static void AdvanceUntil(GameWorld world, Func<bool> done)
    {
        for (int i = 0; i < 80 && !done(); i++)
            Advance(world, 0.25f);
        Assert.True(done());
    }
}
