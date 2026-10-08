using System;
using System.Collections.Generic;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The things done to make the client program's frame cheaper - the index that lets addentities skip
/// entities with nothing to think, predraw or draw, the mirror of .solid the traces read, the prefetch
/// of last frame's memory - must not change what the program computes. Here the same seeded game is
/// played twice on a simulated clock, with them and without, and every cell of the program's memory
/// compared at the end. Needs <c>../Base</c>; returns early without it.
/// </summary>
public class ClientProgramEquivalenceTests
{
    private readonly ITestOutputHelper _output;
    public ClientProgramEquivalenceTests(ITestOutputHelper output) => _output = output;

    private (ulong Digest, long Frames, int Faults, int Entities, int Indexed) Play(bool optimised, double seconds)
    {
        using SvEnvironment env = new(TestPaths.BaseData, writeRoot: null, print: _ => { });
        foreach ((string name, string value) in new[]
                 {
                     ("sv_public", "0"), ("g_warmup", "0"), ("g_start_delay", "0"), ("bot_number", "3"), ("skill", "5"),
                     ("timelimit_override", "0"), ("fraglimit_override", "0"), ("g_maplist_votable", "0"), ("g_forced_respawn", "1"), ("sv_spectate", "0"),
                 }) env.SetCvar(name, value);
        using SvServer server = new(env, new SvServerOptions { MaxClients = 8, RandomSeed = 1, Print = _ => { } });
        Assert.True(server.Start("stormkeep"));

        double clock = 0;
        LegacyClientOptions options = new()
        {
            Host = new CsqcHostOptions
            {
                KeepRunningAfterFault = true, RandomSeed = 7, DirtyTime = () => clock,
                EntityIndex = optimised, PrefetchFrameMemory = optimised,
                // With the index on, also check on every addentities that it left out nothing.
                VerifyEntityIndex = optimised,
            },
        };
        options.Client.Signon.Name = "equivalence";
        using SvLoopback loop = new(server, TestPaths.BaseData, null, options);
        SvLoopbackClient client = loop.Clients[0];
        client.Services.RealTimeSource = () => clock;
        loop.DrawRate = 0;
        loop.Connect();
        const double Step = 1.0 / 90;
        while (!client.InGame && loop.Now < 120) { loop.Step(Step); clock = loop.Now; }
        Assert.True(client.InGame, "the client did not reach the game");

        Random script = new(5);
        double end = loop.Now + seconds, joinAt = loop.Now + 1, until = 0, turn = 0;
        bool joined = false;
        while (loop.Now < end)
        {
            if (!joined && loop.Now >= joinAt) { joined = true; client.Session.Client.SendStringCommand("join"); }
            if (loop.Now >= until)
            {
                until = loop.Now + 0.5 + script.NextDouble();
                turn = (script.NextDouble() - 0.5) * 240;
                client.Input = default;
                client.Input.ForwardMove = script.Next(4) == 0 ? 0 : 400;
                if (script.Next(3) == 0) client.Input.Buttons |= 2;
                if (script.Next(2) == 0) client.Input.Buttons |= 1;
            }
            QcVector angles = client.Session.State.ViewAngles;
            angles.Y += (float)(turn * Step);
            client.Session.State.ViewAngles = angles;
            loop.Step(Step);
            clock = loop.Now;
            client.Session.Draw(Step);
        }

        CsqcHost host = client.Session.Host!;
        QcVm vm = host.Vm;
        ulong digest = 14695981039346656037UL;
        for (int i = 0; i < vm.NumGlobals; i++) digest = (digest ^ (uint)vm.GlobalInt(i)) * 1099511628211UL;
        int indexed = 0;
        for (int e = 0; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e)) { digest = (digest ^ 0xFFFFFFFFUL) * 1099511628211UL; continue; }
            if (vm.IsWatched(e)) indexed++;
            for (int f = 0; f < vm.EntityFields; f++) digest = (digest ^ (uint)vm.PeekField(e, f)) * 1099511628211UL;
        }
        foreach (CsqcFault fault in host.Faults) _output.WriteLine($"fault [{fault.EntryPoint}]: {fault.Message}");
        return (digest, client.Session.FramesDrawn, (int)(host.FaultCount + server.TotalFaults), vm.NumEdicts, indexed);
    }

    [Fact]
    public void The_same_game_ends_in_the_same_memory_with_and_without_the_frame_optimisations()
    {
        if (!ServerTestRig.HaveData) return;
        var plain = Play(optimised: false, seconds: 6);
        var fast = Play(optimised: true, seconds: 6);
        _output.WriteLine($"plain: digest {plain.Digest:X16}, {plain.Frames} frames, {plain.Entities} entities; optimised: digest {fast.Digest:X16}, {fast.Frames} frames, {fast.Indexed} of {fast.Entities} entities in the addentities index");
        Assert.Equal(0, plain.Faults);
        Assert.Equal(0, fast.Faults);
        Assert.True(plain.Frames > 300, "the program drew too few frames for the comparison to mean anything");
        Assert.Equal(plain.Frames, fast.Frames);
        Assert.Equal(plain.Digest, fast.Digest);
        // The index is worth having only if it is short: a client holds thousands of entities, a few
        // hundred of which draw or think.
        Assert.InRange(fast.Indexed, 1, fast.Entities / 4);
    }
}
