using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Two related guards for the listen host spawning into its own match.
///
/// <para><b>The sv_spectate=0 delayed-autojoin (still a valid operator configuration).</b> Regression guard for
/// the "stuck on the loading screen, sounds playing" bug: with <c>sv_spectate 0</c> a would-be observer
/// auto-spawns via the per-tick <c>ObserverOrSpectatorThink</c> grace. The <c>sv_spectate=0</c> spectator-kick
/// block (GameWorld.PlayerFrameIdleAll) shared the <c>info.JoinTime</c> field with the autojoin's MIN_SPEC_TIME
/// grace and reset it EVERY tick while <c>AutoJoinChecked == 0</c> — pinning that grace open forever, so the host
/// never joined (Health stayed 0). Introduced in Waves 9-11 (ff8a699); fixed by not touching JoinTime while the
/// autojoin is still pending.</para>
///
/// <para><b>The shipping listen host uses the `join` command, NOT sv_spectate 0.</b> Since #44, the port ships
/// Base's <c>sv_spectate 1</c> and the host's "Create Game and play" spawn is driven by <c>NetGame</c> running the
/// <c>join</c> command for the local observer (retried until it spawns). The <c>Join*</c> tests below cover the
/// server-layer behavior that block depends on: a <c>join</c> on a normal DM map spawns the observer, and a
/// <c>join</c> with no reachable spawnpoint leaves it an observer WITHOUT throwing — which is exactly why NetGame
/// must retry rather than burn a one-shot latch on the first (failed) attempt.</para>
/// </summary>
[Collection("GlobalState")]
public class ListenHostAutojoinTests
{
    public ListenHostAutojoinTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
    }

    private static CollisionWorld FlatFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    private static List<EntityDict> SpawnDicts(params Vector3[] spots)
    {
        var dicts = new List<EntityDict> { new("worldspawn") };
        foreach (Vector3 s in spots)
            dicts.Add(new EntityDict("info_player_deathmatch", s));
        return dicts;
    }

    [Fact]
    public void ListenHostObserver_AutoJoins_AndIsNotPinnedByTheSpectatorKickGrace()
    {
        var ents = SpawnDicts(new Vector3(0f, 0f, 16f), new Vector3(128f, 0f, 16f));
        var world = new GameWorld(FlatFloor(), ents);
        world.Boot("dm");
        Api.Cvars.Set("sv_spectate", "0"); // the listen-server "host AND play" autojoin enable (NetGame.cs)

        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: "host");
        Player p = info.Player;
        Assert.True(p.IsObserver, "a connecting client starts as an observer (QC TRANSMUTE(Observer))");

        // Drive ~2 s of frames. world.Frame runs the sv_spectate=0 kick block (which reset JoinTime — the bug);
        // the net layer's per-tick ObserverOrSpectatorThink runs the ~1 s delayed autojoin. With the deadlock the
        // host stays a permanent observer; with the fix it auto-joins once the (now stable) grace elapses.
        RunTo(world, world.Time + 2.0f, () =>
            world.Clients.ObserverOrSpectatorThink(p, jumpHeld: false, attackHeld: false));

        Assert.False(p.IsObserver, "the listen host must auto-join within the grace, not stay a permanent observer");
        Assert.True(p.Health > 0f, "an auto-joined host is a live player with health (so the loading screen dismisses)");
    }

    /// <summary>The reset is still correct for a DELIBERATE spectator (autojoin already decided): the kick grace
    /// must keep running so a sv_spectate=0 server eventually kicks a parked spectator. Here we only assert the
    /// grace isn't pinned-open the way the bug pinned it for a fresh observer — i.e. JoinTime stops tracking now.</summary>
    [Fact]
    public void JoinTime_IsStableWhileAutojoinPending()
    {
        var ents = SpawnDicts(new Vector3(0f, 0f, 16f));
        var world = new GameWorld(FlatFloor(), ents);
        world.Boot("dm");
        Api.Cvars.Set("sv_spectate", "0");

        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: "host");
        float joinTimeAtConnect = info.JoinTime;

        // Two frames of the kick block must NOT advance JoinTime while the autojoin is still pending.
        world.Frame(SimulationLoop.TicRate);
        world.Frame(SimulationLoop.TicRate);

        Assert.Equal(joinTimeAtConnect, info.JoinTime, 3);
    }

    /// <summary>The shipping host-join path (NetGame's [#44] block): a <c>join</c> command for the local observer
    /// on a normal DM map spawns it as a live player. This is what NetGame's retry loop converges on.</summary>
    [Fact]
    public void JoinCommand_SpawnsTheObserver_OnANormalDmMap()
    {
        var ents = SpawnDicts(new Vector3(0f, 0f, 16f), new Vector3(128f, 0f, 16f));
        var world = new GameWorld(FlatFloor(), ents);
        world.Boot("dm");
        // Base default sv_spectate 1 — the passive delayed-autojoin does NOT fire; the explicit join is the path.
        Api.Cvars.Set("sv_spectate", "1");

        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: "host");
        Player p = info.Player;
        Assert.True(p.IsObserver, "a connecting client starts as an observer");

        world.Commands.Execute("join", isServerConsole: false, caller: p);

        Assert.False(p.IsObserver, "join must spawn the host as a live player");
        Assert.True(p.Health > 0f, "a joined host has health (so the loading screen dismisses)");
    }

    /// <summary>The failure case that justifies NetGame retrying instead of burning a one-shot latch: a
    /// <c>join</c> with no reachable spawnpoint leaves the caller an observer and does not throw — so a
    /// burn-before-success latch would park the host forever, while the bounded retry recovers when a spawn frees.</summary>
    [Fact]
    public void JoinCommand_WithNoSpawnpoint_LeavesObserver_WithoutThrowing()
    {
        var ents = SpawnDicts(); // worldspawn only — no info_player_deathmatch
        var world = new GameWorld(FlatFloor(), ents);
        world.Boot("dm");
        Api.Cvars.Set("sv_spectate", "1");

        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: "host");
        Player p = info.Player;

        world.Commands.Execute("join", isServerConsole: false, caller: p); // must not throw

        Assert.True(p.IsObserver, "no spawnpoint → the join no-ops and the caller stays an observer (retry territory)");
    }

    private static void RunTo(GameWorld world, float until, Action? perFrame)
    {
        while (world.Time < until)
        {
            world.Frame(SimulationLoop.TicRate);
            perFrame?.Invoke();
        }
    }
}
