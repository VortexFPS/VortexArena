using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// <see cref="SvLocalGame"/>, the facade a host process embeds: started from a map, a mode, a bot
/// count and a data directory; stepped with the owner's clock; joined by a client in the same process
/// through an endpoint that is not a socket; optionally listening on UDP for others. Every test that
/// starts a level needs <c>../Base</c> and returns early without it.
/// </summary>
public class ServerLocalGameTests
{
    private readonly ITestOutputHelper _output;
    public ServerLocalGameTests(ITestOutputHelper output) => _output = output;

    /// <summary>A headless legacy client on an in-process endpoint: what the game's own client is to the facade.</summary>
    private sealed class LocalClient : IDisposable
    {
        private readonly VirtualFileSystem _files = new();
        public readonly LegacyClientSession Session;
        private readonly SvLocalEndpoint _wire;

        public LocalClient(SvLocalEndpoint wire, string name)
        {
            _wire = wire;
            Assert.True(_files.MountGameDir(TestPaths.BaseData));
            CvarService cvars = new();
            ConfigInterpreter interpreter = new(cvars, path => _files.Exists(path) ? _files.ReadText(path) : null);
            CsqcEngineCvars.Register(cvars);
            cvars.Register("pr_checkextension", "1");
            cvars.Register("utf8_enable", "1");
            cvars.Register("developer", "0");
            interpreter.ExecuteFile("default.cfg");
            LegacyQcHost services = new(cvars, _files) { PrintSink = _ => { }, WarningSink = _ => { } };
            LegacyClientOptions options = new() { Host = new CsqcHostOptions { KeepRunningAfterFault = true } };
            options.Client.Signon.Name = name;
            options.Client.Signon.Rate = 262144;
            Session = new LegacyClientSession(services, interpreter, new HeadlessLegacyPresentation(_files), options);
        }

        public bool InGame => Session.Client.State == DpClientState.Connected && Session.Client.Signon.Stage >= DpProtocol.Signons;

        public void Frame(double now)
        {
            Session.BeginFrame(now);
            while (_wire.TryReceive(out byte[] datagram)) Session.Receive(datagram, now);
            foreach (byte[] datagram in Session.Frame(now, default)) _wire.Send(datagram);
            Session.Draw(1.0 / 64);
        }

        public void Dispose()
        {
            Session.Dispose();
            _files.Dispose();
        }
    }

    [Fact]
    public void Start_refuses_what_cannot_be_started_and_leaves_nothing_open()
    {
        Assert.Throws<SvLocalGameException>(() => SvLocalGame.Start(new SvLocalGameOptions { DataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-xonotic-data-" + Guid.NewGuid().ToString("N")) }));
        if (!ServerTestRig.HaveData) return;
        SvLocalGameException noMode = Assert.Throws<SvLocalGameException>(() => SvLocalGame.Start(new SvLocalGameOptions { DataDirectory = TestPaths.BaseData, GameType = "capture-the-bacon" }));
        Assert.Contains("not a game mode", noMode.Message);
        SvLocalGameException noMap = Assert.Throws<SvLocalGameException>(() => SvLocalGame.Start(new SvLocalGameOptions { DataDirectory = TestPaths.BaseData, Map = "no_such_map_anywhere" }));
        Assert.Contains("no_such_map_anywhere", noMap.Message);
    }

    [Fact]
    public void A_local_game_is_joined_without_a_socket_changes_level_and_answers_the_LAN_only_where_bound()
    {
        if (!ServerTestRig.HaveData) return;
        StringBuilder console = new();
        List<(string From, string To)> changes = new();
        double now = 100;   // the owner's clock need not start at zero
        using SvLocalGame game = SvLocalGame.Start(new SvLocalGameOptions
        {
            DataDirectory = TestPaths.BaseData, Map = "runningmanctf", GameType = "ctf", Bots = 2, MaxPlayers = 6,
            Listen = new IPEndPoint(IPAddress.Loopback, 0),
            Cvars = new[] { new KeyValuePair<string, string>("g_warmup", "0"), new KeyValuePair<string, string>("sv_public", "1"), new KeyValuePair<string, string>("hostname", "facade test") },
            Print = text => console.Append(text), RandomSeed = 1,
        }, now);
        game.LevelChanged += (from, to) => changes.Add((from, to));

        Assert.True(game.Running);
        Assert.Equal("runningmanctf", game.Map);
        Assert.Equal("ctf", game.GameType);
        Assert.Equal(6, game.Server.Clients.Count);
        // bound where asked and nowhere else
        Assert.NotNull(game.ListenAddress);
        Assert.Equal(IPAddress.Loopback, game.ListenAddress!.Address);
        Assert.NotEqual(0, game.ListenAddress.Port);
        // asked to be public: said plainly that it is not, because nothing here talks to a master
        Assert.Contains("does not send master server heartbeats", console.ToString());

        SvLocalEndpoint wire = game.AttachLocalClient();
        using LocalClient client = new(wire, "host player");
        client.Session.Connect(now);
        bool Until(Func<bool> condition, double seconds)
        {
            double end = now + seconds;
            while (now < end)
            {
                if (condition()) return true;
                now += 1.0 / 128;
                client.Frame(now);
                game.Frame(now);
            }
            return condition();
        }
        Assert.True(Until(() => client.InGame, 90), $"{client.Session.Client.State}, signon {client.Session.Client.Signon.Stage}, {client.Session.Client.LastError}");
        SvClient slot = game.Server.Clients.First(c => c.Connection is SvNetConnection n && n.Address.Equals(wire.Address));
        // the loopback driver's name for its peer, which is what the program's IS_LOCAL tests
        Assert.Equal("local", slot.NetAddress);
        Assert.Equal("local", game.Server.Host!.Vm.GetString(game.Server.Host.Vm.FieldInt(slot.Edict, game.Server.Host.F.NetAddress)));

        // A LAN browser's query over the socket is answered (sv_public 0 semantics); the reply names the map.
        using (UdpClient lan = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            byte[] query = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.Concat(Encoding.ASCII.GetBytes("getstatus abc")).ToArray();
            lan.Send(query, query.Length, game.ListenAddress);
            lan.Client.ReceiveTimeout = 2000;
            Until(() => lan.Available > 0, 2);
            Assert.True(lan.Available > 0, "no reply to getstatus on the listening socket");
            IPEndPoint? from = null;
            string reply = Encoding.Latin1.GetString(lan.Receive(ref from));
            Assert.Contains("statusResponse", reply);
            Assert.Contains("runningmanctf", reply);
            Assert.Contains("facade test", reply);
        }

        // The console goes in as text and comes back through Print; a level change is announced.
        console.Clear();
        game.Command("status");
        Until(() => console.ToString().Contains("host player", StringComparison.Ordinal), 2);
        Assert.Contains("host player", console.ToString());
        game.Command("changelevel stormkeep");
        Assert.True(Until(() => changes.Count == 1 && client.InGame && client.Session.State.WorldModel == "maps/stormkeep.bsp", 90),
            $"changes {changes.Count}, client {client.Session.Client.State} signon {client.Session.Client.Signon.Stage} on {client.Session.State.WorldModel}");
        Assert.Equal(("runningmanctf", "stormkeep"), changes[0]);
        Assert.Equal("stormkeep", game.Map);
        Assert.Equal(0, game.Faults);
        Assert.Equal(0, client.Session.MessagesNotDecoded);
        Assert.Equal(0, client.Session.Host?.FaultCount ?? 0);

        // Shutdown: the client is told, the endpoint closes, the socket is released.
        IPEndPoint bound = game.ListenAddress;
        game.Dispose();
        Assert.True(wire.Closed);
        Assert.False(game.Running);
        for (int i = 0; i < 64 && client.Session.Client.State == DpClientState.Connected; i++)
        {
            now += 1.0 / 128;
            client.Frame(now);
        }
        Assert.NotEqual(DpClientState.Connected, client.Session.Client.State);
        using UdpClient again = new(bound);   // would throw if the server still held the port
        game.Frame(now + 1);                  // and a disposed game is inert
    }

    [Fact]
    public void A_local_endpoint_is_a_bounded_queue_and_cannot_be_impersonated()
    {
        if (!ServerTestRig.HaveData) return;
        using SvLocalGame game = SvLocalGame.Start(new SvLocalGameOptions { DataDirectory = TestPaths.BaseData, Map = "stormkeep", Dedicated = true });
        SvLocalEndpoint a = game.AttachLocalClient(), b = game.AttachLocalClient();
        Assert.NotEqual(a.Address, b.Address);
        Assert.NotEqual(a.Address.Address, b.Address.Address);   // one host each: the connect-flood limit is per host
        // A stalled server costs a client its newest datagrams, not the process its memory.
        byte[] datagram = new byte[64];
        for (int i = 0; i < SvLocalEndpoint.Capacity + 500; i++) a.Send(datagram);
        Assert.Equal(500, a.Dropped);
        a.Send(ReadOnlySpan<byte>.Empty);
        Assert.Equal(501, a.Dropped);
        game.Frame(1);   // drains the queue; none of it is a packet of the protocol, and nothing throws
        Assert.Equal(0, game.Faults);
        game.DetachLocalClient(a);
        Assert.True(a.Closed);
        a.Send(datagram);
        Assert.Equal(502, a.Dropped);
        // no socket was asked for, so there is none
        Assert.Null(game.ListenAddress);
    }
}
