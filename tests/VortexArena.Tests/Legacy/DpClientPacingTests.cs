using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VortexArena.Legacy.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The send pacing of cl_input.c CL_SendMove and the rate limit of netconn.c NetConn_CanSend /
/// NetConn_UpdateCleartime, against a simulated clock: a client updated 250 times a second must put
/// cl_netfps packets a second on the wire, not 250.
/// </summary>
public class DpClientPacingTests
{
    private readonly ITestOutputHelper _output;
    public DpClientPacingTests(ITestOutputHelper output) => _output = output;

    private sealed class NullHandler : IDpClientHandler { }

    /// <summary>A client taken to "in the game" by a scripted server, with every datagram it sends counted.</summary>
    private sealed class Rig
    {
        public readonly DpClient Client;
        public DpNetChannel Server = new(0);
        public readonly List<byte[]> ToClient = new();
        public double Now;
        public int Frame = 1000;
        /// <summary>Datagrams the client sent that carried a clc_move, and all unreliable ones.</summary>
        public int MoveDatagrams, UnreliableDatagrams, ReliableDatagrams;
        public readonly List<(double Time, int Buttons, int Impulse)> Moves = new();
        public readonly List<string> Commands = new();
        public bool DropClientReliables;

        public Rig(Action<DpClientConfig>? configure = null)
        {
            DpClientConfig config = new();
            configure?.Invoke(config);
            Client = new DpClient(new NullHandler(), config);
        }

        private static byte[] Oob(string text)
        {
            byte[] t = Encoding.ASCII.GetBytes(text);
            byte[] p = new byte[4 + t.Length];
            p[0] = p[1] = p[2] = p[3] = 0xFF;
            t.CopyTo(p, 4);
            return p;
        }

        /// <summary>What the client sent goes to the server channel; what that answers goes back.</summary>
        public int Pump(IReadOnlyList<byte[]> sent)
        {
            foreach (byte[] d in sent)
            {
                if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xFF && d[2] == 0xFF && d[3] == 0xFF)
                {
                    string text = Encoding.ASCII.GetString(d, 4, d.Length - 4);
                    if (text == "getchallenge") ToClient.Add(Oob("challenge abc"));
                    else if (text.StartsWith("connect\\", StringComparison.Ordinal))
                    {
                        Server = new DpNetChannel(Now);
                        ToClient.Add(Oob("accept"));
                    }
                    continue;
                }
                bool reliable = (d[0] & 0x10) == 0 && (d[1] & 0x01) != 0; // NETFLAG_DATA is 0x00010000
                if (reliable)
                {
                    ReliableDatagrams++;
                    if (DropClientReliables) continue;
                }
                else if ((d[1] & 0x10) != 0) UnreliableDatagrams++;       // NETFLAG_UNRELIABLE is 0x00100000
                if (Server.Receive(d, Now, ToClient, out byte[]? message) != DpChannelReceive.Message) continue;
                DpMessageReader r = new(message!);
                bool sawMove = false;
                // A packet repeats the previous move ahead of the new one (cl_netrepeatinput): the new one is last.
                (double Time, int Buttons, int Impulse) newest = default;
                while (r.Position < r.Length && !r.BadRead)
                {
                    int clc = r.ReadByte();
                    if (clc == 3)
                    {
                        r.ReadLong();
                        float time = r.ReadFloat();
                        r.ReadSpan(12);
                        int buttons = r.ReadLong();
                        int impulse = r.ReadByte();
                        r.ReadSpan(30);
                        newest = (time, buttons, impulse);
                        sawMove = true;
                    }
                    else if (clc == 4) Commands.Add(r.ReadString());
                    else if (clc == 50) r.ReadLong();
                    else if (clc == 51) { r.ReadLong(); r.ReadUShort(); }
                    else if (clc != 1) break;
                }
                if (sawMove)
                {
                    MoveDatagrams++;
                    Moves.Add(newest);
                }
            }
            return sent.Count;
        }

        public void Deliver()
        {
            byte[][] batch = ToClient.ToArray();
            ToClient.Clear();
            foreach (byte[] d in batch) Client.Receive(d, Now);
        }

        public void Step(double dt)
        {
            Now += dt;
            Pump(Client.Update(Now));
            Server.Transmit(default, Now, ToClient);
            Deliver();
        }

        public void SendReliable(Action<DpMessageWriter> build)
        {
            build(Server.Reliable);
            Server.Transmit(default, Now, ToClient);
            Deliver();
        }

        /// <summary>svc_time plus an empty entity frame: one server tick.</summary>
        public void ServerTick(float time)
        {
            DpMessageWriter w = new();
            w.WriteByte(7); w.WriteFloat(time);
            w.WriteByte(57); w.WriteLong(Frame++); w.WriteLong((int)Client.ServerMoveSequence);
            w.WriteShort(0x8000);
            Server.Transmit(w.WrittenSpan, Now, ToClient);
            Deliver();
        }

        public void SignOn()
        {
            Client.Connect(Now);
            for (int i = 0; i < 20 && Client.State != DpClientState.Connected; i++) Step(0.05);
            Assert.Equal(DpClientState.Connected, Client.State);
            SendReliable(w =>
            {
                w.WriteByte(9); w.WriteString("csqc_progcrc -1\n");
                DpServerMessageParserTests.WriteServerInfo(w);
                w.WriteByte(25); w.WriteByte(1);
            });
            for (int i = 0; i < 40 && !Commands.Contains("prespawn"); i++) Step(0.05);
            SendReliable(w => { w.WriteByte(25); w.WriteByte(2); });
            for (int i = 0; i < 40 && !Commands.Contains("spawn"); i++) Step(0.05);
            SendReliable(w => { w.WriteByte(25); w.WriteByte(3); });
            for (int i = 0; i < 40 && !Commands.Contains("begin"); i++) Step(0.05);
            Assert.Contains("begin", Commands);
            ServerTick((float)Now);
            Assert.Equal(DpProtocol.Signons, Client.Signon.Stage);
            // Let the reliable stream go quiet before anything is counted.
            for (int i = 0; i < 20; i++) Step(0.05);
            MoveDatagrams = UnreliableDatagrams = ReliableDatagrams = 0;
            Moves.Clear();
        }

        /// <summary>Runs <paramref name="seconds"/> at <paramref name="hz"/> updates a second, queuing a move each update.</summary>
        public void Run(double seconds, int hz, Func<int, DpUserCmd>? move = null, double serverHz = 0)
        {
            int steps = (int)Math.Round(seconds * hz);
            double nextTick = Now;
            for (int i = 0; i < steps; i++)
            {
                Now += 1.0 / hz;
                if (serverHz > 0 && Now >= nextTick)
                {
                    nextTick += 1.0 / serverHz;
                    ServerTick((float)Now);
                }
                DpUserCmd cmd = move?.Invoke(i) ?? new DpUserCmd();
                cmd.Predicted = true;
                cmd.Time = (float)Now;
                Client.QueueMove(cmd);
                Pump(Client.Update(Now));
                Server.Transmit(default, Now, ToClient);
                Deliver();
            }
        }
    }

    [Theory]
    [InlineData(64, 62.5)]   // 250 / 64 = 3.9 updates per packet: every 4th update
    [InlineData(72, 62.5)]   // 13.9 ms: still the 4th update (16 ms)
    [InlineData(125, 125)]   // 8 ms: every 2nd
    [InlineData(20, 20)]     // 50 ms: every 13th update is 52 ms, 19.2 a second
    [InlineData(5, 10)]      // below the floor of 10
    [InlineData(5000, 250)]  // above the ceiling of 1000: every update
    public void Updates_At_250_Hz_Send_At_The_Configured_Rate(double netFps, double expectedPerSecond)
    {
        Rig rig = new(c => { c.NetFps = netFps; c.Signon.Rate = 1_000_000; c.Signon.RateBurstSize = 100_000; });
        rig.SignOn();
        rig.Run(10, 250);
        double perSecond = rig.MoveDatagrams / 10.0;
        _output.WriteLine($"cl_netfps {netFps}: {rig.MoveDatagrams} input packets in 10 s of 250 Hz updates = {perSecond:F1}/s (deferred {rig.Client.SendsDeferred}, choked {rig.Client.Channel.PacketsChoked})");
        // The interval is a whole number of 4 ms updates, so the rate is quantised downwards.
        Assert.InRange(perSecond, expectedPerSecond * 0.9, expectedPerSecond * 1.02);
        Assert.Equal(rig.MoveDatagrams, rig.Client.MovePacketsSent);
        Assert.Equal(0, rig.Client.Channel.PacketsChoked);
        // Every move covers real time: none of zero milliseconds.
        for (int i = 1; i < rig.Moves.Count; i++) Assert.True(rig.Moves[i].Time > rig.Moves[i - 1].Time);
    }

    [Fact]
    public void Known_Tick_Rate_Keeps_The_Packet_Rate_Between_One_And_Two_Per_Tick()
    {
        // cl_netfps 20 against a 60 Hz server: the interval is clamped to one tick (16.7 ms)...
        Rig slow = new(c => { c.NetFps = 20; c.Signon.Rate = 1_000_000; });
        slow.SignOn();
        slow.Client.MoveVarsTicRate = 1.0 / 60;
        slow.Run(10, 250);
        _output.WriteLine($"cl_netfps 20, ticrate 1/60: {slow.MoveDatagrams / 10.0:F1}/s");
        Assert.InRange(slow.MoveDatagrams / 10.0, 49, 61);   // every 5th update (20 ms) = 50

        // ...and cl_netfps 1000 to half a tick (8.3 ms): every 3rd update (12 ms) = 83.
        Rig fast = new(c => { c.NetFps = 1000; c.Signon.Rate = 1_000_000; });
        fast.SignOn();
        fast.Client.MoveVarsTicRate = 1.0 / 60;
        fast.Run(10, 250);
        _output.WriteLine($"cl_netfps 1000, ticrate 1/60: {fast.MoveDatagrams / 10.0:F1}/s");
        Assert.InRange(fast.MoveDatagrams / 10.0, 80, 121);
    }

    [Fact]
    public void A_Button_Change_Sends_Immediately_And_So_Does_An_Impulse()
    {
        Rig rig = new(c => { c.NetFps = 10; c.Signon.Rate = 1_000_000; });
        rig.SignOn();
        // Settle into the 10 Hz rhythm (a packet every 25 updates), then press fire one update after a packet.
        rig.Run(1, 250);
        int pressAt = -1, sentBeforePress = 0;
        rig.Run(1, 250, i =>
        {
            if (pressAt < 0 && rig.Client.LastUpdateSentMove) { pressAt = i; sentBeforePress = rig.MoveDatagrams; }
            return new DpUserCmd { Buttons = pressAt >= 0 && i >= pressAt ? 1 : 0 };
        });
        Assert.True(pressAt >= 0);
        // The update on which the button first differs from the last sent move carried a packet,
        // 4 ms after the previous one instead of 100 ms.
        (double Time, int Buttons, int Impulse)[] moves = rig.Moves.ToArray();
        int first = Array.FindIndex(moves, m => m.Buttons == 1);
        Assert.True(first > 0);
        Assert.InRange(moves[first].Time - moves[first - 1].Time, 0.0035, 0.0045);
        // After that it is steady again: holding a button is not a change.
        Assert.InRange(moves[first + 1].Time - moves[first].Time, 0.095, 0.105);

        // Without cl_netimmediatebuttons the press waits its turn.
        Rig lazy = new(c => { c.NetFps = 10; c.NetImmediateButtons = false; c.Signon.Rate = 1_000_000; });
        lazy.SignOn();
        lazy.Run(1, 250);
        bool pressed = false;
        lazy.Run(1, 250, i =>
        {
            if (lazy.Client.LastUpdateSentMove) pressed = true;
            return new DpUserCmd { Buttons = pressed ? 1 : 0 };
        });
        moves = lazy.Moves.ToArray();
        first = Array.FindIndex(moves, m => m.Buttons == 1);
        Assert.InRange(moves[first].Time - moves[first - 1].Time, 0.095, 0.105);

        // An impulse is important whatever the button setting, and is sent exactly once.
        bool queued = false;
        lazy.Run(0.5, 250, i =>
        {
            bool now = !queued && lazy.Client.LastUpdateSentMove;
            if (now) queued = true;
            return new DpUserCmd { Buttons = 1, Impulse = now ? (byte)7 : (byte)0 };
        });
        moves = lazy.Moves.ToArray();
        int impulse = Array.FindIndex(moves, m => m.Impulse == 7);
        Assert.True(impulse > 0);
        Assert.InRange(moves[impulse].Time - moves[impulse - 1].Time, 0.0035, 0.0045);
        Assert.Equal(1, moves.Count(m => m.Impulse == 7));
    }

    [Fact]
    public void The_Rate_Limit_Chokes_Packets_Down_To_The_Announced_Rate_After_The_Burst()
    {
        // 2 moves + 1 ack = 117 bytes; + 8 header + 28 overhead = 153 charged per packet. The rate is
        // raised to at least 20 * (117 + 40) = 3140 bytes a second by CL_SendMove.
        Rig rig = new(c => { c.NetFps = 125; c.Signon.Rate = 3000; c.Signon.RateBurstSize = 1024; });
        rig.SignOn();
        rig.Run(20, 250, serverHz: 0);
        double perSecond = rig.MoveDatagrams / 20.0;
        _output.WriteLine($"rate 3000 (effective 3140): {rig.MoveDatagrams} packets in 20 s = {perSecond:F1}/s, choked {rig.Client.Channel.PacketsChoked}");
        Assert.InRange(perSecond, 18, 24);   // 3140 / 153 = 20.5, plus the burst
        Assert.True(rig.Client.Channel.PacketsChoked > 0);

        // A button change goes out even while choked.
        int before = rig.MoveDatagrams;
        int chokedBefore = rig.Client.Channel.PacketsChoked;
        int toggles = 0;
        rig.Run(1, 250, i => { if (i % 5 == 0) toggles++; return new DpUserCmd { Buttons = (i / 5) & 1 }; });
        Assert.True(rig.MoveDatagrams - before >= toggles - 1, $"{rig.MoveDatagrams - before} packets for {toggles} button changes");
    }

    [Fact]
    public void Clear_Time_Follows_NetConn_UpdateCleartime()
    {
        DpNetChannel ch = new(0);
        List<byte[]> o = new();
        Assert.False(ch.CanSend(0));           // "realtime > cleartime", strictly
        Assert.True(ch.CanSend(0.001));
        // A quiet connection is credited one burst: 1000 / 10000 = 0.1 s. A 72-byte datagram
        // (64 + 8) is charged 100 bytes = 0.01 s, leaving the clear time at 10 - 0.1 + 0.01.
        ch.Transmit(new byte[64], 10, o, rate: 10000, burstSize: 1000);
        Assert.Equal(9.91, ch.ClearTime, 9);
        Assert.True(ch.CanSend(10));
        // Ten more at the same instant use the burst up and one packet more.
        for (int i = 0; i < 10; i++) ch.Transmit(new byte[64], 10, o, 10000, 1000);
        Assert.Equal(10.01, ch.ClearTime, 9);
        Assert.False(ch.CanSend(10));
        Assert.Equal(2, ch.PacketsChoked);
        Assert.False(ch.CanSend(10.005));
        Assert.True(ch.CanSend(10.011));
        // Sending nothing still drags a stale clear time up to one burst ago, and charges nothing.
        ch.Transmit(default, 50, o, 10000, 1000);
        Assert.Equal(49.9, ch.ClearTime, 9);
    }

    [Fact]
    public void A_Lost_Reliable_Is_Still_Resent_While_Pacing_Holds_Everything_Else_Back()
    {
        Rig rig = new(c => { c.NetFps = 10; c.Signon.Rate = 1_000_000; });
        rig.SignOn();
        rig.DropClientReliables = true;
        rig.Client.SendStringCommand("say hello");
        rig.Run(3.5, 250);
        // First transmission, then one repeat per second of silence: at 1 s, 2 s and 3 s.
        Assert.Equal(4, rig.ReliableDatagrams);
        Assert.Equal(3, rig.Client.Channel.PacketsResent);
        Assert.DoesNotContain("say hello", rig.Commands);
        // The line comes back: the next repeat is delivered, acknowledged, and not repeated again.
        rig.DropClientReliables = false;
        rig.Run(1.5, 250);
        Assert.Contains("say hello", rig.Commands);
        Assert.False(rig.Client.Channel.ReliableInFlight);
        int sent = rig.ReliableDatagrams;
        rig.Run(2, 250);
        Assert.Equal(sent, rig.ReliableDatagrams);
        // Input kept flowing at its own pace throughout.
        Assert.InRange(rig.MoveDatagrams / 7.0, 9, 10.5);
    }

    [Fact]
    public void No_Zero_Millisecond_Moves_Once_The_Server_Clock_Is_Running()
    {
        Rig rig = new(c => { c.NetFps = 1000; c.Signon.Rate = 1_000_000; });
        rig.SignOn();
        rig.ServerTick((float)rig.Now + 0.016f);   // a second, later stamp: mtime[0] > mtime[1]
        int before = rig.MoveDatagrams;
        // The same client time queued again and again (a frame faster than the clock's resolution).
        float frozen = (float)rig.Now;
        for (int i = 0; i < 50; i++)
        {
            rig.Now += 0.004;
            rig.Client.QueueMove(new DpUserCmd { Predicted = true, Time = frozen });
            rig.Pump(rig.Client.Update(rig.Now));
        }
        // The first has a frame time (the previous move was earlier); the 49 after it would be 0 ms.
        Assert.InRange(rig.MoveDatagrams - before, 0, 1);
    }

    [Fact]
    public void Client_Clock_Tracks_The_Previous_Server_Stamp()
    {
        foreach (int mode in new[] { 4, 5, 6, 7 })
        {
            DpClientClock clock = new() { BoundMode = mode, TicRate = 1.0 / 60 };
            clock.NetworkTimeReceived(100, signedOn: false);
            Assert.Equal(100, clock.Time);
            double server = 100;
            // 30 s at 250 Hz with a 60 Hz server.
            double nextTick = 0, wall = 0;
            double worst = 0;
            for (int i = 0; i < 7500; i++)
            {
                wall += 0.004;
                clock.Advance(0.004);
                if (wall >= nextTick)
                {
                    nextTick += 1.0 / 60;
                    server = 100 + wall;
                    clock.NetworkTimeReceived(server, signedOn: true);
                }
                if (i > 2500) worst = Math.Max(worst, Math.Abs(clock.Time - (clock.ServerTime - 1.0 / 60)));
            }
            _output.WriteLine($"cl_nettimesyncboundmode {mode}: after 30 s cl.time is {clock.Time - server:+0.0000;-0.0000} s from the newest stamp; worst distance from one tick behind {worst * 1000:F2} ms");
            // One update behind, give or take a tick.
            Assert.InRange(clock.Time, server - 2.5 / 60, server + 0.5 / 60);
        }
    }
}
