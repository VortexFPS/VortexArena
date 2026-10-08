using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// A match from its start to the next map, on the real server program: what sv_main.c
/// SV_SpawnServer does the second and later times it runs in one process, reached the way a server
/// reaches it - the time limit runs out, the program holds its intermission and asks for the next map
/// - and by the console commands that ask directly (changelevel, restart, map). Every test needs
/// <c>../Base</c> and returns early without it.
/// </summary>
public class ServerLifecycleTests
{
    private readonly ITestOutputHelper _output;
    public ServerLifecycleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_match_with_bots_runs_out_its_time_limit_and_the_server_moves_on_to_the_next_maps()
    {
        if (!ServerTestRig.HaveData) return;
        // Six seconds of match; no map vote, so the intermission alone stands between two levels.
        using ServerTestRig rig = new(8, ("bot_number", "3"), ("bot_join_empty", "1"), ("timelimit_override", "0.1"), ("g_maplist", "boil stormkeep"), ("g_maplist_votable", "0"));
        List<(string Map, int Faults, int Unimplemented, int Edicts, int Models, double Time, int Bots)> ended = new();
        List<SvqcHost> hosts = new();
        rig.Server.LevelStarted += hosts.Add;
        rig.Server.LevelEnding += h => ended.Add((h.WorldBaseName, h.FaultCount, h.UnimplementedBuiltins.Count, h.Vm.NumEdicts, h.ModelCount, h.Time, h.Clients.Count(c => c.Active)));
        Assert.True(rig.Server.Start("stormkeep"));

        Assert.True(rig.Until(() => rig.Server.LevelsStarted >= 3, 240), $"only {rig.Server.LevelsStarted} levels after {rig.Now:0} s; {rig.ServerFaults}");
        foreach ((string map, int faults, int unimplemented, int edicts, int models, double time, int bots) in ended)
            _output.WriteLine($"{map}: {time:0.0} s of game time, {edicts} edicts, {models} models, {bots} bots at the end, {faults} faults");

        Assert.Equal(2, ended.Count);
        Assert.Equal("stormkeep", ended[0].Map);
        Assert.All(ended, level => Assert.Equal(0, level.Faults));
        Assert.All(ended, level => Assert.Equal(0, level.Unimplemented));
        // the level changed because the match ended, not at once: six seconds of play and an intermission
        Assert.All(ended, level => Assert.InRange(level.Time, 7, 120));
        // each level is a new program on a new world; the ones before are shut down
        Assert.Equal(3, hosts.Count);
        Assert.Equal(3, hosts.Distinct().Count());
        Assert.Equal(SvState.Dead, hosts[0].State);
        Assert.Equal(SvState.Dead, hosts[1].State);
        SvqcHost now = rig.Host;
        Assert.Same(hosts[2], now);
        Assert.Equal(SvState.Active, now.State);
        Assert.Equal("maps/" + now.WorldBaseName + ".bsp", now.ModelName(1));
        Assert.NotEqual(ended[1].Map, now.WorldBaseName);
        Assert.True(now.Time < 3, $"the new level's clock reads {now.Time}");
        // and the game goes on: the bots come back and play
        Assert.True(rig.Until(() => now.Clients.Count(c => c.Active && c.Begun) >= 3, 15), "the bots did not join the third level");
        rig.Run(3);
        Assert.Equal(0, rig.Server.TotalFaults);
    }

    [Fact]
    public void A_connected_client_is_carried_across_changelevel_and_restart_and_dropped_by_map()
    {
        if (!ServerTestRig.HaveData) return;
        using ServerTestRig rig = new(8, ("bot_number", "2"), ("timelimit_override", "10"));
        Assert.True(rig.Server.Start("stormkeep"));
        SvLoopbackClient c = rig.AddClient("traveller");
        bool OnLevel(string map) => c.InGame && c.Session.State.WorldModel == "maps/" + map + ".bsp" && rig.Server.Host?.WorldBaseName == map;
        Assert.True(rig.Until(() => OnLevel("stormkeep"), 60), ServerTestRig.Summary(c));
        rig.Run(1.5);
        c.Session.Client.SendStringCommand("join");
        Assert.True(rig.Until(() => c.ServerSlot is { Frags: not -666 }, 10));
        c.Input.ForwardMove = 400;   // the change happens to a moving player
        rig.Run(2);

        // A map that is not there: the level stays as it is (SV_SpawnServer looks before it leaps).
        SvqcHost first = rig.Host;
        rig.Server.AddCommandText("changelevel no_such_map_anywhere\n");
        rig.Run(1);
        Assert.Same(first, rig.Host);
        Assert.Equal(1, rig.Server.LevelsStarted);
        Assert.Contains(rig.ServerPrints, l => l.Contains("no map file named maps/no_such_map_anywhere.bsp", StringComparison.Ordinal));
        Assert.True(c.InGame);

        // changelevel: SV_SaveSpawnparms, a new level, the same slot, and the client signs on again.
        int slot = c.ServerSlot!.Index;
        rig.Server.AddCommandText("changelevel boil\n");
        Assert.True(rig.Until(() => rig.Server.LevelsStarted == 2 && OnLevel("boil"), 60), ServerTestRig.Summary(c) + "; " + rig.ServerFaults);
        Assert.Equal(2, c.Session.ProgramsStarted);
        Assert.Equal(slot, c.ServerSlot!.Index);
        Assert.True(c.ServerSlot.Begun);
        // Xonotic's SetChangeParms leaves "how long idle" in parm1: a negative number of seconds.
        float savedParm = c.ServerSlot.SpawnParms[0];
        Assert.True(savedParm < 0, $"parm1 after changelevel is {savedParm}");
        Assert.True(ServerTestRig.Clean(c), ServerTestRig.Summary(c));
        rig.Run(2);
        int edictsAfterChange = rig.Host.Vm.NumEdicts, stringsAfterChange = rig.Host.Vm.ZonedStringCount;

        // restart: the same level again, everyone carried over, and the spawn parameters NOT saved
        // again (SV_Restart_f does not call SV_SaveSpawnparms).
        rig.Server.AddCommandText("restart\n");
        Assert.True(rig.Until(() => rig.Server.LevelsStarted == 3 && OnLevel("boil") && c.Session.ProgramsStarted == 3, 60), ServerTestRig.Summary(c) + "; " + rig.ServerFaults);
        Assert.Equal(savedParm, c.ServerSlot!.SpawnParms[0]);
        rig.Run(2);
        // Nothing accumulates from one level to the next: the same level with the same players
        // comes to the same size. (Not exactly: a bot may have fired in one and not the other.)
        _output.WriteLine($"2 s into boil: {edictsAfterChange} edicts and {stringsAfterChange} zoned strings after changelevel, {rig.Host.Vm.NumEdicts} and {rig.Host.Vm.ZonedStringCount} after restart");
        Assert.InRange(rig.Host.Vm.NumEdicts, edictsAfterChange - 40, edictsAfterChange + 40);
        Assert.InRange(rig.Host.Vm.ZonedStringCount, stringsAfterChange - 40, stringsAfterChange + 40);
        Assert.True(ServerTestRig.Clean(c), ServerTestRig.Summary(c));

        // map: SV_Shutdown first - everyone is dropped - then the level.
        c.Input = default;
        rig.Server.AddCommandText("map stormkeep\n");
        Assert.True(rig.Until(() => rig.Server.LevelsStarted == 4, 60), rig.ServerFaults);
        Assert.True(rig.Until(() => c.Session.Client.State != DpClientState.Connected, 5), "the client was not told it had been dropped");
        Assert.All(rig.Server.Clients, s => Assert.Null(s.Connection));
        Assert.Equal("stormkeep", rig.Host.WorldBaseName);
        // ... and it can come back (once the server's connect-flood window for its address has passed).
        rig.Run(6);
        rig.Loop!.Connect(c);
        Assert.True(rig.Until(() => OnLevel("stormkeep"), 60), ServerTestRig.Summary(c));
        rig.Run(2);
        Assert.True(ServerTestRig.Clean(c), ServerTestRig.Summary(c));
        Assert.Equal(0, rig.Server.TotalFaults);
    }
}
