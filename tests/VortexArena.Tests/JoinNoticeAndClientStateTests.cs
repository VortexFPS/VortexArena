using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Physics;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Net;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class JoinNoticeAndClientStateTests
{
    [Fact]
    public void DelayedPresentationUsesReceiptDeadlineAndDropsAtEquality()
    {
        var pending = new TimedJoinNotice(new JoinNotice(5, "^1Welcome|^2Second line")).WithClock(20f);
        Assert.Equal(25d, pending.ExpiresAt);
        Assert.Equal(3, pending.WithClock(22.5f).RemainingSeconds(22.5f));
        Assert.Equal(3, pending.WithClock(22.5f).RemainingSeconds(22.5f)); // paused server clock
        Assert.Equal(1, pending.RemainingSeconds(MathF.BitDecrement(25f)));
        Assert.Equal(0, pending.RemainingSeconds(25f));
        Assert.Equal(0, pending.WithClock(31f).RemainingSeconds(31f));
    }

    [Fact]
    public void NoticeAwaitingFirstSnapshotStartsWhenAClockArrives()
    {
        var pending = new TimedJoinNotice(new JoinNotice(2, "Welcome")).WithClock(0f);
        Assert.Null(pending.ExpiresAt);
        pending = pending.WithClock(71.5f);
        Assert.Equal(73.5d, pending.ExpiresAt);
        Assert.Equal(1, pending.WithClock(72.5f).RemainingSeconds(72.5f));
    }

    private static GameWorld NewWorld(Action<CvarService>? configure = null)
    {
        var world = new GameWorld(new CollisionWorld(),
        [
            new EntityDict("worldspawn"),
            new EntityDict("info_player_deathmatch", new Vector3(0, 0, 128)),
            new EntityDict("info_player_deathmatch", new Vector3(512, 0, 128)),
        ]);
        world.Boot("dm", applyStartupOverrides: cvars =>
        {
            cvars.Set("g_warmup", "0");
            cvars.Set("sv_spectate", "1");
            configure?.Invoke(cvars);
        });
        return world;
    }

    [Fact]
    public void ConnectHooksSeeIdentityAndDetachClearsOnlyThatConnectionsPreferences()
    {
        const string id = "test-local-client-identity";
        var world = NewWorld(cvars =>
        {
            cvars.Set(Bans.ChatBanList, id);
            cvars.Set("sv_vote_master_ids", id);
        });
        try
        {
            Player first = world.Clients.ClientConnect(netName: "first", netAddress: "192.0.2.12", persistentId: id).Player;
            Player other = world.Clients.ClientConnect(netName: "other").Player;
            Assert.True(first.Muted);
            Assert.True(world.Voting.IsMaster(first));
            Assert.False(other.Muted);
            Assert.True(first.IsObserver);
            Assert.True((first.Flags & EntFlags.Client) != 0);
            Assert.False(first.CanPickupItems);

            // The preference arrives while the connection has no active player state yet.
            world.Commands.Execute("sentcvar cl_spawn_near_teammate 1", isServerConsole: false, caller: first);
            world.Commands.Execute("sentcvar cl_spawn_near_teammate 1", isServerConsole: false, caller: other);
            ServerPlayerState clientState = world.PlayerStates.Of(first);
            float idleSince = clientState.IdleSince;
            clientState.KillCntdownNextTime = 12f;
            Assert.True(world.Clients.Join(first));
            Assert.True(first.CanPickupItems);
            Assert.True(world.Clients.Spawn(first));
            Assert.Same(clientState, world.PlayerStates.Of(first));
            Assert.Equal(idleSince, clientState.IdleSince);
            Assert.Equal(12f, clientState.KillCntdownNextTime);
            world.Clients.PutObserverInServer(first);
            Assert.False(first.CanPickupItems);
            Assert.True((first.Flags & EntFlags.NoTarget) != 0);
            Assert.Empty(first.OwnedWeapons);
            Assert.True(world.Commands.GetClientCvarBool(first, "cl_spawn_near_teammate"));

            Assert.True(world.Clients.ClientDisconnect(first));
            Assert.False((first.Flags & EntFlags.Client) != 0);
            Assert.False(world.Voting.IsMaster(first));
            Assert.False(world.Commands.GetClientCvarBool(first, "cl_spawn_near_teammate"));
            Assert.True(world.Commands.GetClientCvarBool(other, "cl_spawn_near_teammate"));

            Player replacement = world.Clients.ClientConnect(netName: "replacement", netAddress: "192.0.2.12", persistentId: id).Player;
            Assert.NotSame(first, replacement);
            Assert.False(world.Commands.GetClientCvarBool(replacement, "cl_spawn_near_teammate"));
            Assert.True(replacement.Muted);
            Assert.True(world.Voting.IsMaster(replacement));
        }
        finally { world.Shutdown(); }
    }

    [Fact]
    public void ConnectAllowsLoadingGraceAndRealInputStartsTheNormalIdleClock()
    {
        var world = NewWorld(cvars =>
        {
            cvars.Set("sv_maxidle", "5");
            cvars.Set("sv_maxidle_alsokickspectators", "1");
            cvars.Set("sv_maxidle_minplayers", "0");
        });
        try
        {
            Player player = world.Clients.ClientConnect().Player;
            var state = world.PlayerStates.Of(player);
            Assert.Equal(world.Time + 55f, state.IdleSince);

            // The default input does not consume the grace while the observer is loading.
            for (int i = 0; i < 60; i++)
                world.Frame(0.1f);
            Assert.NotNull(world.Clients.InfoOf(player));
            Assert.Equal(55f, state.IdleSince);

            world.InputProvider = _ => new MovementInput { MoveValues = Vector3.UnitX };
            world.Frame(SimulationLoop.TicRate);
            Assert.InRange(state.IdleSince, world.Time - SimulationLoop.TicRate * 2f, world.Time);
        }
        finally { world.Shutdown(); }
    }
}
