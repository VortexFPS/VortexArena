using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// Several headless legacy clients on one real server in one process: what players do to a server
/// beyond joining it. They arrive together and must see each other; they talk, vote, spectate and
/// change team; they leave and come back, go silent, get kicked, find the server full and send it
/// garbage. And a moving client's prediction is compared with the server frame by frame. Every test
/// needs <c>../Base</c> and returns early without it.
/// </summary>
public class ServerClientsTests
{
    private readonly ITestOutputHelper _output;
    public ServerClientsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Three_clients_see_each_other_talk_vote_spectate_and_one_is_kicked()
    {
        if (!ServerTestRig.HaveData) return;
        using ServerTestRig rig = new(8, ("bot_number", "1"), ("sv_vote_call", "1"));
        Assert.True(rig.Server.Start("stormkeep"));
        SvLoopbackClient[] clients = { rig.AddClient("player1"), rig.AddClient("player2"), rig.AddClient("player3") };
        int n = clients.Length;
        Assert.True(rig.Until(() => clients.All(c => c.InGame), 90), string.Join("; ", clients.Select(ServerTestRig.Summary)));
        rig.Run(1.5);   // "time < jointime + MIN_SPEC_TIME": a join in the first second is put off
        foreach (SvLoopbackClient c in clients) c.Session.Client.SendStringCommand("join");
        Assert.True(rig.Until(() => clients.All(c => c.ServerSlot is { Begun: true, Frags: not -666 }), 10), "not everyone became a player");
        rig.Run(1);

        // Spawn points are rooms apart, and a player in another room is rightly culled. Bring the
        // others to where the first stands, each to a spot the server's own line trace says is open.
        SvqcHost host = rig.Host;
        QcVector home = host.Vm.FieldVector(clients[0].ServerSlot!.Edict, host.F.Origin);
        (float X, float Y)[] around = { (72, 0), (-72, 0), (0, 72), (0, -72), (72, 72), (-72, -72) };
        int placed = 0;
        for (int i = 1; i < n; i++)
            foreach ((float dx, float dy) in around.Skip(placed))
            {
                placed++;
                QcVector spot = new(home.X + dx, home.Y + dy, home.Z + 8);
                if (host.World.Trace(home, default, default, spot, SvWorld.MoveWorldOnly, 0, SvWorld.ContentsOpaque).Fraction < 1) continue;
                int e = clients[i].ServerSlot!.Edict;
                host.Vm.FieldVector(e, host.F.Origin) = spot;
                host.Vm.FieldVector(e, host.F.Velocity) = default;
                host.LinkEdict(e);
                break;
            }
        rig.Run(1.5);

        // Each client's CSQC entity stream carries each other player, where the server has it.
        List<int> visible = new();
        for (int i = 0; i < n; i++)
        {
            CsqcHost program = clients[i].Session.Host!;
            rig.Server.VisibleEntities(clients[i].ServerSlot!, visible);
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                int other = clients[j].ServerSlot!.Edict;
                Assert.Contains(other, visible);
                int edict = program.EdictForServerEntity(other);
                Assert.True(edict > 0 && edict < program.Vm.NumEdicts && !program.Vm.IsFree(edict), $"player{i + 1}'s program has no entity for player{j + 1}");
                QcVector mine = program.Vm.FieldVector(edict, program.Fields.Origin), theirs = host.Vm.FieldVector(other, host.F.Origin);
                double d = Math.Sqrt(Math.Pow(mine.X - theirs.X, 2) + Math.Pow(mine.Y - theirs.Y, 2) + Math.Pow(mine.Z - theirs.Z, 2));
                Assert.True(d < 4, $"player{i + 1} has player{j + 1} {d:0.0} units from where the server has it");
            }
        }
        // ... and every scoreboard names everyone (svc_updatename).
        foreach (SvLoopbackClient c in clients)
            foreach (SvLoopbackClient other in clients)
                Assert.Contains(rig.Names[other], c.Session.State.Scores[other.ServerSlot!.Index].Name);

        // say
        int[] marks = rig.ClientPrints.Select(p => p.Count).ToArray();
        clients[0].Session.Client.SendStringCommand("say hello from one");
        rig.Run(1);
        for (int i = 0; i < n; i++) Assert.True(rig.Count(rig.ClientPrints[i], "hello from one", marks[i]) > 0, $"player{i + 1} did not hear the chat line");

        // spectate, join
        clients[1].Session.Client.SendStringCommand("spectate");
        Assert.True(rig.Until(() => clients[1].ServerSlot is { Frags: -666 }, 5), "cmd spectate did not make an observer");
        clients[1].Session.Client.SendStringCommand("join");
        Assert.True(rig.Until(() => clients[1].ServerSlot is { Frags: not -666 }, 8), "cmd join did not make a player again");

        // a vote, called by one and carried by the others
        int serverMark = rig.ServerPrints.Count;
        clients[0].Session.Client.SendStringCommand("vote call extendmatchtime");
        rig.Run(1);
        for (int i = 1; i < n; i++) clients[i].Session.Client.SendStringCommand("vote yes");
        Assert.True(rig.Until(() => rig.Count(rig.ServerPrints, "was accepted", serverMark) > 0, 5),
            "the vote was not accepted: " + string.Join(" | ", rig.ServerPrints.Skip(serverMark).Take(6)));

        // status, on the console and asked by a client
        serverMark = rig.ServerPrints.Count;
        marks = rig.ClientPrints.Select(p => p.Count).ToArray();
        rig.Server.AddCommandText("status\n");
        clients[2].Session.Client.SendStringCommand("status");
        rig.Run(0.5);
        foreach (SvLoopbackClient c in clients) Assert.True(rig.Count(rig.ServerPrints, rig.Names[c], serverMark) > 0, $"status does not list {rig.Names[c]}");
        Assert.True(rig.Count(rig.ClientPrints[2], "players:", marks[2]) > 0, "the client's status went unanswered");

        // kick, by slot number: that client is told and its slot freed; ClientDisconnect runs once
        SvLoopbackClient kicked = clients[n - 1];
        int kickedEdict = kicked.ServerSlot!.Edict, disconnects = rig.Count(rig.ServerPrints, "disconnected");
        rig.Server.AddCommandText($"kick # {kickedEdict} testing the kick\n");
        Assert.True(rig.Until(() => kicked.Session.Client.State != DpClientState.Connected, 3), "the kicked client still thinks it is connected");
        Assert.False(rig.Server.Clients[kickedEdict - 1].Active);
        rig.Run(1);
        Assert.Equal(disconnects + 1, rig.Count(rig.ServerPrints, "disconnected"));
        for (int i = 0; i < n - 1; i++) Assert.True(clients[i].InGame && ServerTestRig.Clean(clients[i]), ServerTestRig.Summary(clients[i]));
        Assert.Equal(0, rig.Server.TotalFaults);
    }

    [Fact]
    public void A_full_server_rejects_and_a_slot_is_freed_by_leaving_by_silence_and_by_garbage()
    {
        if (!ServerTestRig.HaveData) return;
        using ServerTestRig rig = new(3, ("bot_number", "0"), ("net_messagetimeout", "4"), ("net_connecttimeout", "4"));
        Assert.True(rig.Server.Start("stormkeep"));
        SvLoopbackClient a = rig.AddClient("alice"), b = rig.AddClient("bob"), c = rig.AddClient("carol");
        Assert.True(rig.Until(() => a.InGame && b.InGame && c.InGame, 90), $"{ServerTestRig.Summary(a)}; {ServerTestRig.Summary(b)}; {ServerTestRig.Summary(c)}");

        // maxplayers reached: the fourth is told so
        SvLoopbackClient d = rig.AddClient("dave");
        Assert.True(rig.Until(() => d.Session.Client.State == DpClientState.Rejected, 15), ServerTestRig.Summary(d));
        Assert.Equal("Server is full.", d.Session.Client.LastError);

        // a clean disconnect: slot free at once, ClientDisconnect once, slot reusable
        int disconnects = rig.Count(rig.ServerPrints, "disconnected"), slotOfB = b.ServerSlot!.Index;
        rig.Loop!.Disconnect(b);
        Assert.True(rig.Until(() => !rig.Server.Clients[slotOfB].Active, 2), "the slot was not freed");
        rig.Run(0.5);
        Assert.Equal(disconnects + 1, rig.Count(rig.ServerPrints, "disconnected"));

        // a client that stops sending: dropped after net_messagetimeout, ClientDisconnect once
        disconnects = rig.Count(rig.ServerPrints, "disconnected");
        int slotOfC = c.ServerSlot!.Index;
        c.Frozen = true;
        double silentAt = rig.Now;
        Assert.True(rig.Until(() => !rig.Server.Clients[slotOfC].Active, 12), "the silent client was never dropped");
        Assert.InRange(rig.Now - silentAt, 3.5, 6);
        rig.Run(1);
        Assert.Equal(disconnects + 1, rig.Count(rig.ServerPrints, "disconnected"));
        Assert.Contains(rig.ServerEvents, e => e.Contains("Timed out", StringComparison.Ordinal));

        // the one that left comes back, into the first of the two freed slots
        rig.Loop.Connect(b);
        Assert.True(rig.Until(() => b.InGame, 90), ServerTestRig.Summary(b));
        Assert.Equal(Math.Min(slotOfB, slotOfC), b.ServerSlot!.Index);

        // garbage from a connected client's address: bytes that are no packet of the protocol are
        // ignored; nonsense inside well-formed channel packets gets that client dropped, as
        // DarkPlaces drops it - and nobody else notices
        Random random = new(7);
        for (int i = 0; i < 300; i++)
        {
            byte[] junk = new byte[random.Next(4, 1500)];
            random.NextBytes(junk);
            if (i % 3 == 0) junk[0] = junk[1] = junk[2] = junk[3] = 0xFF;   // looks connectionless
            a.InjectToServer(junk);
            if (i % 8 == 0) rig.Step();
        }
        rig.Run(0.5);
        Assert.True(a.InGame, "raw garbage disturbed the client it claimed to come from: " + ServerTestRig.Summary(a));
        int slotOfA = a.ServerSlot!.Index;
        List<byte[]> framed = new();
        for (int i = 0; i < 40 && rig.Server.Clients[slotOfA].Active; i++)
        {
            byte[] nonsense = new byte[random.Next(1, 600)];
            random.NextBytes(nonsense);
            framed.Clear();
            a.Session.Client.Channel.Transmit(nonsense, rig.Now, framed);
            foreach (byte[] datagram in framed) a.InjectToServer(datagram);
            rig.Step();
        }
        Assert.False(rig.Server.Clients[slotOfA].Active, "nonsense in valid channel packets did not get the sender dropped");
        Assert.True(rig.Server.ClientsDroppedForBadMessages >= 1);
        rig.Run(1);
        Assert.True(b.InGame && ServerTestRig.Clean(b), ServerTestRig.Summary(b));
        Assert.Equal(0, rig.Server.TotalFaults);
    }

    [Fact]
    public void Clients_choose_and_change_teams_in_team_deathmatch()
    {
        if (!ServerTestRig.HaveData) return;
        using ServerTestRig rig = new(8, ("bot_number", "0"), ("g_tdm", "1"), ("g_dm", "0"), ("g_balance_teams", "0"), ("g_balance_teams_prevent_imbalance", "0"),
            ("g_changeteam_banned", "0"), ("sv_teamnagger", "0"));
        Assert.True(rig.Server.Start("stormkeep"));
        Assert.Equal("tdm", SvGameTypes.Current(rig.Env));
        SvLoopbackClient a = rig.AddClient("alice"), b = rig.AddClient("bob");
        Assert.True(rig.Until(() => a.InGame && b.InGame, 90), $"{ServerTestRig.Summary(a)}; {ServerTestRig.Summary(b)}");
        SvqcHost host = rig.Host;
        float Team(SvLoopbackClient c) => c.ServerSlot is { } slot ? host.Vm.FieldFloat(slot.Edict, host.F.Team) : -1;
        rig.Run(1.5);
        // NUM_TEAM_1 (red) is 5, NUM_TEAM_2 (blue) 14
        a.Session.Client.SendStringCommand("selectteam red");
        b.Session.Client.SendStringCommand("selectteam blue");
        Assert.True(rig.Until(() => Team(a) == 5 && Team(b) == 14 && a.ServerSlot!.Frags != -666 && b.ServerSlot!.Frags != -666, 8), $"teams {Team(a)} {Team(b)}");
        a.Session.Client.SendStringCommand("selectteam blue");
        Assert.True(rig.Until(() => Team(a) == 14, 8), $"alice is still on team {Team(a)}");
        rig.Run(1);
        // the other client's scoreboard follows (svc_updatecolors: the low nibble is the team colour, 13 for blue)
        Assert.Equal(13, b.Session.State.Scores[a.ServerSlot!.Index].Colors & 15);
        Assert.True(ServerTestRig.Clean(a) && ServerTestRig.Clean(b), $"{ServerTestRig.Summary(a)}; {ServerTestRig.Summary(b)}");
        Assert.Equal(0, rig.Server.TotalFaults);
    }

    [Fact]
    public void A_spectator_at_a_fixed_place_is_sent_the_engine_entities_DarkPlaces_sends()
    {
        if (!ServerTestRig.HaveData) return;
        // Sixteen slots, as the reference had: the map's entities are numbered after the player slots,
        // and with no bots every entity then has the number it has there.
        using ServerTestRig rig = new(16, ("bot_number", "0"));
        Assert.True(rig.Server.Start("glowplant"));
        SvLoopbackClient c = rig.AddClient("watcher");
        Assert.True(rig.Until(() => c.InGame, 90), ServerTestRig.Summary(c));
        SvqcHost host = rig.Host;
        SvClient slot = c.ServerSlot!;
        Assert.Equal(1, slot.Edict);

        List<int> EngineEntitiesFrom(float x, float y, float z)
        {
            // "prvm_edictset server 1 origin ..." on the reference; an observer stays where it is put.
            host.Vm.FieldVector(slot.Edict, host.F.Origin) = new QcVector(x, y, z);
            host.Vm.FieldVector(slot.Edict, host.F.Velocity) = default;
            host.LinkEdict(slot.Edict);
            rig.Run(2);
            List<int> visible = new();
            rig.Server.VisibleEntities(slot, visible);
            // the engine's stream only (svc_entities): entities the program does not send itself
            return visible.Where(e => e != slot.Edict && host.Vm.FieldInt(e, host.F.SendEntity) == 0).OrderBy(e => e).ToList();
        }

        // In front of a warpzone. The reference sends two brush models: the wall in this warpzone's
        // frame and the one in its partner's, half the map away - visible only to the eye
        // SV_AddCameraEyes puts on the far side. With the single-cluster visibility test and
        // line-of-sight through brushes this server sent six.
        long eyesBefore = rig.Server.CameraEyesAdded;
        List<int> a = EngineEntitiesFrom(120, -384, 576);
        _output.WriteLine("from '120 -384 576': " + string.Join(" ", a));
        Assert.Equal(new[] { 4463, 4465 }, a);
        Assert.True(rig.Server.CameraEyesAdded > eyesBefore, "no eye was added behind the warpzone");
        // Above an item in another room: three. (A fourth, the wall behind the warpzone again, comes
        // and goes: from here the warpzone is seen only by some of the random line-of-sight samples
        // SV_CanSeeBox takes - sv_cullentities_trace_samples_extra, one of them random, each frame.)
        List<int> b = EngineEntitiesFrom(-1472, 130, 700);
        _output.WriteLine("from '-1472 130 700': " + string.Join(" ", b));
        Assert.Equal(new[] { 4450, 4463, 4474 }, b.Where(e => e != 4465));
        Assert.True(rig.Server.EntitiesCulledByPvs > 0);
        Assert.True(ServerTestRig.Clean(c), ServerTestRig.Summary(c));
        Assert.Equal(0, rig.Server.TotalFaults);
    }

    [Fact]
    public void A_moving_clients_prediction_agrees_with_the_server_every_frame()
    {
        if (!ServerTestRig.HaveData) return;
        using ServerTestRig rig = new(8, ("bot_number", "0"));
        Assert.True(rig.Server.Start("stormkeep"));
        SvLoopbackClient c = rig.AddClient("runner");
        Assert.True(rig.Until(() => c.InGame, 90), ServerTestRig.Summary(c));
        rig.Run(1.5);
        c.Session.Client.SendStringCommand("join");
        Assert.True(rig.Until(() => c.ServerSlot is { Frags: not -666 }, 8));
        rig.Run(2);

        // The program moves its player in CSQC_UpdateView: draw after every step, or half the samples
        // would compare the server with a position one frame stale.
        rig.Loop!.DrawRate = 1000;
        // A scripted run - straight, a long turn, strafing, jumps while turning, held jump. After
        // every step: the client's predicted origin for the input sequence it has just built,
        // against the server's origin once it has executed that sequence.
        Dictionary<uint, QcVector> predicted = new();
        List<(double Horizontal, double Vertical)> errors = new();
        double start = rig.Now;
        uint lastServerSequence = 0;
        QcVector previousServer = default;
        int teleports = 0;
        while (rig.Now - start < 20)
        {
            double t = rig.Now - start;
            QcVector angles = c.Session.State.ViewAngles;
            c.Input = default;
            if (t < 3) c.Input.ForwardMove = 400;
            else if (t < 8) { c.Input.ForwardMove = 400; angles.Y += (float)(ServerTestRig.StepSeconds * 90); }
            else if (t < 11) { c.Input.SideMove = (int)(t * 2) % 2 == 0 ? 400 : -400; c.Input.ForwardMove = 200; }
            else if (t < 15) { c.Input.ForwardMove = 400; if (t % 1.2 < 0.15) c.Input.Buttons |= 2; angles.Y -= (float)(ServerTestRig.StepSeconds * 45); }
            else if (t < 19) { c.Input.ForwardMove = 400; c.Input.Buttons |= 2; angles.Y += (float)(ServerTestRig.StepSeconds * 20); }
            c.Session.State.ViewAngles = angles;
            rig.Step();

            if (c.TryGetClientPlayerOrigin(out QcVector mine)) predicted[c.Session.State.MoveCommands[0].Sequence] = mine;
            if (c.ServerSlot is { } slot && slot.MoveSequence != lastServerSequence && c.TryGetServerPlayerOrigin(out QcVector theirs))
            {
                lastServerSequence = slot.MoveSequence;
                double jump = Math.Sqrt(Math.Pow(theirs.X - previousServer.X, 2) + Math.Pow(theirs.Y - previousServer.Y, 2) + Math.Pow(theirs.Z - previousServer.Z, 2));
                previousServer = theirs;
                if (!predicted.Remove(slot.MoveSequence, out QcVector was)) continue;
                // A respawn or a teleporter is the server's alone for the one frame it takes to arrive.
                if (jump > 200 && errors.Count > 0) { teleports++; continue; }
                errors.Add((Math.Sqrt(Math.Pow(was.X - theirs.X, 2) + Math.Pow(was.Y - theirs.Y, 2)), Math.Abs(was.Z - theirs.Z)));
            }
        }
        Assert.True(errors.Count > 1000, $"only {errors.Count} samples");
        double worstHorizontal = errors.Max(e => e.Horizontal), worstVertical = errors.Max(e => e.Vertical);
        int horizontalOver = errors.Count(e => e.Horizontal > 0.01), verticalOver = errors.Count(e => e.Vertical > 0.01);
        _output.WriteLine($"{errors.Count} samples, {teleports} respawn frames left out: horizontal disagreement over 0.01 units in {horizontalOver} (worst {worstHorizontal:0.000}); vertical in {verticalOver} (worst {worstVertical:0.000})");
        // Horizontally the two agree to the float in all but a stray sample. Vertically the client
        // program eases its own player up and down steps (cl_stairsmoothspeed) while the server's
        // steps at once. (With the server's curved surfaces and the client's built differently, the
        // 99th percentile of this was ten units.)
        Assert.True(worstHorizontal < 1, $"the client's prediction was {worstHorizontal:0.000} units off horizontally");
        Assert.True(horizontalOver <= errors.Count / 200, $"{horizontalOver} of {errors.Count} samples disagree horizontally");
        Assert.True(verticalOver < errors.Count / 25 && worstVertical < 32, $"{verticalOver} samples disagree vertically, worst {worstVertical:0.00}");
        Assert.True(ServerTestRig.Clean(c), ServerTestRig.Summary(c));
        Assert.Equal(0, rig.Server.TotalFaults);
    }
}
