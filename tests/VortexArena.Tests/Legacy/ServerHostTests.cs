using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The server half of legacy compatibility on the real thing: Xonotic's own server program
/// (progs.dat) from the reference checkout, on the real map. Every test needs <c>../Base</c> and
/// returns early without it - a test here that reports 0 ms has not run.
///
/// What each milestone of the port claims is asserted here: the program loads and names no builtin
/// the engine lacks beyond a known list (stage 1); a level boots, bots join, move and fight for a
/// minute of game time without a fault (milestone A); the headless legacy client connects over a
/// loopback pair of queues, downloads the client program from the server, signs on, joins and walks,
/// and the two sides agree where it is (milestone B); and nothing a client can send makes the server
/// throw.
/// </summary>
public class ServerHostTests
{
    private readonly ITestOutputHelper _output;
    public ServerHostTests(ITestOutputHelper output) => _output = output;

    private static bool HaveData => Directory.Exists(TestPaths.BaseCorePk3Dir);

    private static SvEnvironment Environment(params (string Name, string Value)[] cvars)
    {
        SvEnvironment env = new(TestPaths.BaseData, writeRoot: null);
        foreach ((string name, string value) in cvars) env.SetCvar(name, value);
        return env;
    }

    [Fact]
    public void The_stock_server_program_loads_and_names_only_known_missing_builtins()
    {
        if (!HaveData) return;
        using SvEnvironment env = Environment();
        byte[] data = env.Services.ReadFile("progs.dat")!;
        ProgsFile progs = ProgsFile.Load(data);
        // The facts of the 0.8.6-line build this checkout pins; a different build changes them, and
        // then _scratch/server-probe.txt (tools/legacy-server probe) is what to regenerate.
        Assert.Equal(602825, progs.Statements.Length);
        Assert.Equal(13740, progs.Functions.Length);
        Assert.Equal(50373, progs.NumGlobals);
        Assert.Equal(5259, progs.EntityFields);
        Assert.True(progs.Statements.All(s => s.Op <= (int)QcOp.BitOrF), "the program uses an opcode above the classic 0..65");

        using SvqcHost host = new(data, env.Services, env.Cvars, env.Interpreter, env.Files);
        Assert.Equal(1368, host.ProgramCrc);
        Assert.Equal(44352, host.CsqcProgCrc);
        int[] missing = progs.Functions.Where(f => f.IsBuiltin).Select(f => f.BuiltinNumber).Distinct().Where(n => !host.Vm.HasBuiltin(n)).OrderBy(n => n).ToArray();
        _output.WriteLine("declared builtins not implemented: " + string.Join(", ", missing));
        // skel_build and the bone accessors, three surface queries, two sound queries, ODE, and #608
        // (which DarkPlaces' own server table leaves empty too).
        Assert.Equal(new[] { 264, 269, 270, 271, 272, 273, 274, 438, 439, 486, 533, 534, 540, 541, 542, 608 }, missing);
    }

    [Fact]
    public void Stormkeep_boots_and_two_bots_play_for_a_minute_without_a_fault()
    {
        if (!HaveData) return;
        using SvEnvironment env = Environment(("bot_number", "2"), ("bot_join_empty", "1"));
        using SvqcHost? host = env.StartLevel("stormkeep", new SvqcHostOptions { MaxClients = 16, RandomSeed = 7 });
        Assert.NotNull(host);
        Assert.Equal(0, host!.FaultCount);
        Assert.Equal(SvState.Active, host.State);
        // What a DarkPlaces dedicated server reports for this level with 16 slots ("prvm_edictcount
        // server": num_edicts 4256), and what it lists in svc_serverinfo.
        Assert.Equal(4256, host.Vm.NumEdicts);
        Assert.Equal(311, host.ModelCount - 1);
        Assert.Equal(434, host.SoundCount - 1);

        double ticRate = env.Cvars.GetFloat("sys_ticrate");
        Assert.Equal(1.0 / 64, ticRate, 6);
        Dictionary<int, (QcVector Last, double Distance)> travelled = new();
        double start = host.Time;
        while (host.Time - start < 60)
        {
            host.RealTime += ticRate;
            Assert.True(host.RunFrame(ticRate), host.Faults.Count > 0 ? host.Faults[0].Message : "the frame did not run");
            foreach (SvClient client in host.Clients)
            {
                if (!client.Active) continue;
                QcVector origin = host.Vm.FieldVector(client.Edict, host.F.Origin);
                if (travelled.TryGetValue(client.Edict, out (QcVector Last, double Distance) t))
                {
                    double dx = origin.X - t.Last.X, dy = origin.Y - t.Last.Y, dz = origin.Z - t.Last.Z, step = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    travelled[client.Edict] = (origin, t.Distance + (step < 64 ? step : 0));   // a respawn is not travel
                }
                else travelled[client.Edict] = (origin, 0);
            }
        }
        int totalFrags = host.Vm.FindField("totalfrags")!.Offset;
        float frags = host.Clients.Where(c => c.Active).Sum(c => host.Vm.FieldFloat(c.Edict, totalFrags));
        _output.WriteLine($"frames {host.Frames}, bots {host.ActiveClients}, travelled {string.Join(", ", travelled.Values.Select(t => t.Distance.ToString("0")))}, frags {frags}, traces {host.World.Traces}");
        Assert.Equal(0, host.FaultCount);
        Assert.Empty(host.UnimplementedBuiltins);
        Assert.Equal(0, host.BadMoveTypes);
        Assert.Equal(2, host.ActiveClients);
        Assert.All(host.Clients.Where(c => c.Active), c => Assert.Null(c.Connection));
        Assert.All(travelled.Values, t => Assert.True(t.Distance > 2000, $"a bot moved only {t.Distance:0} units in a minute"));
    }

    [Fact]
    public void The_legacy_client_joins_over_loopback_downloads_the_program_and_walks()
    {
        if (!HaveData) return;
        using SvEnvironment env = Environment(("bot_number", "2"), ("bot_join_empty", "1"));
        using SvServer server = new(env, new SvServerOptions { MaxClients = 8, RandomSeed = 3 });
        Assert.True(server.Start("stormkeep"));
        LegacyClientOptions options = new() { AlwaysDownloadProgram = true };
        options.Client.Signon.Name = "tester";
        using SvLoopback loop = new(server, TestPaths.BaseData, clientWriteRoot: null, options);
        loop.Connect();

        const double step = 1.0 / 256;
        double inGameAt = -1;
        bool joined = false, measured = false;
        QcVector before = default;
        double worst = 0;
        while (loop.Now < 90 && (inGameAt < 0 || loop.Now - inGameAt < 14))
        {
            LegacyInput input = default;
            if (inGameAt < 0 && loop.Client.Client.Signon.Stage == DpProtocol.Signons) inGameAt = loop.Now;
            double t = inGameAt < 0 ? -1 : loop.Now - inGameAt;
            if (t >= 3 && !joined)
            {
                joined = true;
                loop.Client.Client.SendStringCommand("join");
            }
            if (t >= 6 && t < 9)
            {
                if (!measured) measured = loop.TryGetServerPlayerOrigin(out before);
                input.ForwardMove = 360;
            }
            loop.Step(step, input);
            if (t >= 5 && loop.TryGetClientPlayerOrigin(out QcVector mine) && loop.TryGetServerPlayerOrigin(out QcVector theirs))
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(mine.X - theirs.X, 2) + Math.Pow(mine.Y - theirs.Y, 2) + Math.Pow(mine.Z - theirs.Z, 2)));
        }

        LegacyClientSession session = loop.Client;
        Assert.True(inGameAt >= 0, $"not in the game: client {session.Client.State}, signon stage {session.Client.Signon.Stage}, {session.Client.LastError}");
        Assert.Equal(DpClientState.Connected, session.Client.State);
        // the in-band download of csprogs.dat, deflated, byte for byte
        Assert.NotNull(session.Client.Signon.LastDownload);
        Assert.Equal(DpDownloadStatus.Completed, session.Client.Signon.LastDownload!.Status);
        Assert.True(session.Client.Signon.CsprogsVerified);
        Assert.Equal(server.Host!.CsqcProgData, session.Client.Signon.LastDownload.Data);
        Assert.Equal("downloaded", session.ProgramSource);
        // both programs ran clean and every server message was decoded to its end
        Assert.Equal(0, server.Host.FaultCount);
        Assert.NotNull(session.Host);
        Assert.Equal(0, session.Host!.FaultCount);
        Assert.Equal(0, session.Host.DesyncCount);
        Assert.Equal(0, session.MessagesNotDecoded);
        Assert.Null(session.ProgramError);
        Assert.True(session.EntityFrames > 600, $"only {session.EntityFrames} entity frames");
        Assert.True(session.Host.EntityUpdates > 500, $"only {session.Host.EntityUpdates} CSQC entity updates");
        // the client is a player, not an observer, and moved under its own input
        SvClient slot = loop.ServerSlot!;
        Assert.True(slot.Begun);
        Assert.NotEqual(-666, slot.Frags);
        Assert.True(measured && loop.TryGetServerPlayerOrigin(out QcVector after));
        loop.TryGetServerPlayerOrigin(out after);
        double moved = Math.Sqrt(Math.Pow(after.X - before.X, 2) + Math.Pow(after.Y - before.Y, 2));
        _output.WriteLine($"in game at {inGameAt:0.00} s; moved {moved:0.0} units; client and server disagree by at most {worst:0.00} units; entity frames {session.EntityFrames}, csqc updates {session.Host.EntityUpdates}");
        Assert.True(moved > 100, $"the player moved {moved:0.0} units in three seconds of +forward");
        Assert.True(worst < 32, $"client and server disagree about the player's position by {worst:0.0} units");
    }

    // ---- hostile clients ----------------------------------------------------------------------------

    private static byte[] Oob(string text)
    {
        byte[] body = Encoding.ASCII.GetBytes(text), packet = new byte[4 + body.Length];
        packet[0] = packet[1] = packet[2] = packet[3] = 0xFF;
        body.CopyTo(packet, 4);
        return packet;
    }

    // A raw client: the real handshake, then whatever bytes the test wants on a real channel.
    private sealed class RawClient
    {
        public readonly IPEndPoint Address;
        public readonly DpNetChannel Channel = new(0);
        public readonly List<(IPEndPoint To, byte[] Datagram)> Out = new();
        private readonly SvServer _server;
        public double Now = 1;
        public RawClient(SvServer server, int port) { _server = server; Address = new IPEndPoint(IPAddress.Loopback, port); }

        public bool Connect()
        {
            Send(Oob("getchallenge"));
            byte[]? challenge = Out.Where(o => o.To.Equals(Address)).Select(o => o.Datagram).LastOrDefault(d => d.Length > 14 && Encoding.ASCII.GetString(d, 4, 9) == "challenge");
            if (challenge is null) return false;
            string token = Encoding.ASCII.GetString(challenge, 14, challenge.Length - 14).Split('\0')[0];
            Out.Clear();
            Send(Oob($"connect\\protocol\\darkplaces 3\\protocols\\DP7\\challenge\\{token}"));
            return Out.Any(o => o.To.Equals(Address) && o.Datagram.Length >= 10 && Encoding.ASCII.GetString(o.Datagram, 4, 6) == "accept");
        }

        public void Send(byte[] datagram)
        {
            Now += 0.001;
            _server.Receive(datagram, Address, Now, Out);
        }

        public void SendMessage(byte[] message, bool reliable)
        {
            List<byte[]> datagrams = new();
            if (reliable)
            {
                Channel.Reliable.WriteBytes(message);
                Channel.Transmit(ReadOnlySpan<byte>.Empty, Now, datagrams);
            }
            else Channel.Transmit(message, Now, datagrams);
            foreach (byte[] d in datagrams) Send(d);
        }
    }

    [Fact]
    public void Nothing_a_client_sends_makes_the_server_throw()
    {
        if (!HaveData) return;
        using SvEnvironment env = Environment();
        using SvServer server = new(env, new SvServerOptions { MaxClients = 4, RandomSeed = 5 });
        Assert.True(server.Start("stormkeep"));
        List<(IPEndPoint To, byte[] Datagram)> outgoing = new();
        Random random = new(11);

        // noise from strangers: neither connectionless nor anyone's channel
        for (int i = 0; i < 3000; i++)
        {
            byte[] noise = new byte[random.Next(0, 1500)];
            random.NextBytes(noise);
            if ((i & 3) == 0 && noise.Length >= 4) noise[0] = noise[1] = noise[2] = noise[3] = 0xFF;
            server.Receive(noise, new IPEndPoint(new IPAddress(new byte[] { 10, 0, (byte)(i >> 8), (byte)i }), 1000 + (i % 5000)), 1 + i * 0.0001, outgoing);
        }
        Assert.All(server.Clients, c => Assert.False(c.Active));

        // an unknown message type drops the client that sent it (SV_ReadClientMessage)
        RawClient unknown = new(server, 40001);
        Assert.True(unknown.Connect());
        Assert.Contains(server.Clients, c => c.Active);
        unknown.SendMessage(new byte[] { 0x7E, 1, 2, 3 }, reliable: false);
        Assert.All(server.Clients, c => Assert.False(c.Active));
        Assert.Equal(1, server.ClientsDroppedForBadMessages);

        // truncated and absurd messages of every known type; none may throw, and the frame still runs
        // (a message that ends in the middle of a field is a bad read, and DarkPlaces drops the client
        // for it; the next message then comes from a new connection, a flood-timeout later)
        int port = 40002;
        double clock = 20;
        RawClient Fresh()
        {
            clock += 10;
            RawClient fresh = new(server, port++) { Now = clock };
            Assert.True(fresh.Connect(), "the server refused a new connection");
            return fresh;
        }
        RawClient hostile = Fresh();
        byte[][] messages =
        {
            new byte[] { (byte)Clc.Move, 1, 2, 3 },                                            // a move cut short
            new byte[] { (byte)Clc.AckFrame, 0xFF, 0xFF, 0xFF, 0x7F },                         // "I have frame two billion"
            new byte[] { (byte)Clc.AckFrame, 0, 0, 0, 0x80 },                                  // and a negative one
            new byte[] { (byte)Clc.AckDownloadData, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0x7F },      // an ack for a download that is not running
            new byte[] { (byte)Clc.StringCmd },                                                // a command with no text and no terminator
            Encoding.ASCII.GetBytes("\x04name \"x\"\nrcon_password secret\0"),                 // a second line smuggled after the first
            Encoding.ASCII.GetBytes("\x04" + "download ../../../etc/passwd\0"),               // a file outside the game data
            Encoding.ASCII.GetBytes("\x04" + "download progs.dat\0"),                         // a file the server does not serve
            Encoding.ASCII.GetBytes("\x04" + "sv_startdownload\0"),
            Encoding.ASCII.GetBytes("\x04" + "prespawn\0"),
            Encoding.ASCII.GetBytes("\x04" + "begin\0"),                                      // out of order: not spawned
            Encoding.ASCII.GetBytes("\x04" + "color 999999999 -5\0"),
            Encoding.ASCII.GetBytes("\x04" + "rate -1\0"),
            Encoding.ASCII.GetBytes("\x04" + "name " + new string('A', 20000) + "\0"),
            Encoding.ASCII.GetBytes("\x04" + new string('"', 9000) + "\0"),
        };
        int dropped = 0;
        foreach (byte[] message in messages)
        {
            hostile.SendMessage(message, reliable: false);
            hostile.Now += 0.02;
            outgoing.Clear();
            server.Frame(hostile.Now, outgoing);
            clock = hostile.Now;
            if (server.Clients.Any(c => c.Active)) continue;
            dropped++;
            hostile = Fresh();
        }
        _output.WriteLine($"{dropped} of {messages.Length} malformed messages got their sender dropped");
        Assert.InRange(dropped, 1, messages.Length);
        hostile.SendMessage(Encoding.ASCII.GetBytes("" + "name " + new string('B', 300) + " "), reliable: false);
        hostile.SendMessage(Encoding.ASCII.GetBytes("" + "prespawn "), reliable: false);
        SvClient slot = server.Clients.Single(c => c.Active);
        Assert.True(slot.Name.Length <= SvClient.MaxNameLength);
        Assert.False(slot.Begun);
        Assert.Equal(0, server.Host!.FaultCount);

        // moves full of NaNs and entity numbers out of range, from a client that got as far as the game
        hostile.SendMessage(Encoding.ASCII.GetBytes("\x04" + "spawn\0"), reliable: false);
        hostile.SendMessage(Encoding.ASCII.GetBytes("\x04" + "begin\0"), reliable: false);
        Assert.True(slot.Begun);
        for (int i = 0; i < 400; i++)
        {
            byte[] move = new byte[57];
            random.NextBytes(move);
            move[0] = (byte)Clc.Move;
            if ((i & 1) == 0) BitConverter.GetBytes(float.NaN).CopyTo(move, 5);                // the move's time
            hostile.SendMessage(move, reliable: false);
            hostile.Now += 0.01;
            if ((i & 7) == 0)
            {
                outgoing.Clear();
                server.Frame(hostile.Now, outgoing);
            }
        }
        Assert.Equal(0, server.Host.FaultCount);
        QcVector origin = server.Host.Vm.FieldVector(slot.Edict, server.Host.F.Origin);
        Assert.True(float.IsFinite(origin.X) && float.IsFinite(origin.Y) && float.IsFinite(origin.Z), "the player's origin is not finite");

        // random bytes on the live channel: the client is dropped or ignored, never an exception
        for (int i = 0; i < 2000 && slot.Active; i++)
        {
            byte[] junk = new byte[random.Next(1, 300)];
            random.NextBytes(junk);
            hostile.SendMessage(junk, reliable: (i & 15) == 0);
        }
        outgoing.Clear();
        server.Frame(hostile.Now + 1, outgoing);
        Assert.Equal(0, server.Host.FaultCount);
    }
}
