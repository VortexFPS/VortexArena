using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Local;
using VortexArena.Legacy.Menu;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// A local Xonotic game as the game window runs it: the server program on <see cref="LegacyLocalServer"/>
/// (its own thread, or the caller's), the client on <see cref="LegacyLoopbackTransport"/>, the start
/// options built from the player's cvars, and the console lines of a listen server. The tests that start
/// a level need <c>../Base</c> and return early without it.
/// </summary>
public class LocalGameTests
{
    private readonly ITestOutputHelper _output;
    public LocalGameTests(ITestOutputHelper output) => _output = output;

    /// <summary>A headless legacy client on any transport: what LegacyGame is around a session, without Godot.</summary>
    private sealed class Client : IDisposable
    {
        private readonly VirtualFileSystem _files = new();
        public readonly LegacyClientSession Session;
        public readonly CvarService Cvars = new();
        public ILegacyTransport? Wire;
        public LegacyInput Input;

        public Client(string name)
        {
            Assert.True(_files.MountGameDir(TestPaths.BaseData));
            ConfigInterpreter interpreter = new(Cvars, path => _files.Exists(path) ? _files.ReadText(path) : null) { NestedReferences = true };
            CsqcEngineCvars.Register(Cvars);
            Cvars.Register("pr_checkextension", "1");
            Cvars.Register("utf8_enable", "1");
            Cvars.Register("developer", "0");
            interpreter.ExecuteFile("default.cfg");
            LegacyQcHost services = new(Cvars, _files) { PrintSink = _ => { }, WarningSink = _ => { } };
            LegacyClientOptions options = new() { Host = new CsqcHostOptions { KeepRunningAfterFault = true } };
            options.Client.Signon.Name = name;
            options.Client.Signon.Rate = 262144;
            Session = new LegacyClientSession(services, interpreter, new HeadlessLegacyPresentation(_files), options);
            interpreter.UnknownCommandHandler = (_, argv) => Session.Client.SendStringCommand(LegacyLocalCommands.Join(argv, 0));
        }

        public bool InGame => Session.Client.State == DpClientState.Connected && Session.Client.Signon.Stage >= DpProtocol.Signons;

        public void Frame(double now)
        {
            if (Wire is not { } wire) return;
            Session.BeginFrame(now);
            while (wire.TryReceive(out byte[] datagram)) Session.Receive(datagram, now);
            foreach (byte[] datagram in Session.Frame(now, Input)) wire.Send(datagram);
            Session.Draw(1.0 / 64);
        }

        public void Dispose()
        {
            Session.Dispose();
            _files.Dispose();
        }
    }

    // ---- the transport seam ------------------------------------------------------------------------------

    [Fact]
    public void Both_transports_are_one_interface_and_the_loopback_counts_what_crosses_it()
    {
        // The socket: nothing is sent anywhere a server could hear, the interface is all that is checked.
        using (DpUdpTransport udp = new(new IPEndPoint(IPAddress.Loopback, 9)))
        {
            ILegacyTransport transport = udp;
            Assert.StartsWith("udp 127.0.0.1:9", transport.Peer);
            Assert.Equal(0, transport.Sent);
            Assert.False(transport.TryReceive(out _));
        }
        if (!ServerTestRig.HaveData) return;
        using SvLocalGame game = SvLocalGame.Start(new SvLocalGameOptions { DataDirectory = TestPaths.BaseData, Map = "stormkeep" });
        int woken = 0;
        LegacyLoopbackTransport loop = new(game.AttachLocalClient(), () => woken++);
        ILegacyTransport wire = loop;
        Assert.Equal("local", wire.Peer);
        wire.Send(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'g', (byte)'e', (byte)'t', (byte)'c', (byte)'h', (byte)'a', (byte)'l', (byte)'l', (byte)'e', (byte)'n', (byte)'g', (byte)'e' });
        Assert.Equal(1, wire.Sent);
        Assert.Equal(1, woken);
        game.Frame(1);
        Assert.True(wire.TryReceive(out byte[] reply));
        Assert.StartsWith("ÿÿÿÿchallenge ", Encoding.Latin1.GetString(reply));
        Assert.Equal(1, wire.Received);
        Assert.False(wire.TryReceive(out _));
        Assert.Equal(0, loop.Dropped);
        Assert.Null(game.ListenAddress);   // a local game has no socket unless one is asked for
    }

    // ---- sv_autopause: the three programs together -------------------------------------------------------

    [Fact]
    public void A_listen_server_pauses_while_its_only_player_is_in_a_menu_and_resumes_when_the_menu_closes()
    {
        if (!ServerTestRig.HaveData) return;
        double now = 10;
        using SvLocalGame game = SvLocalGame.Start(new LegacyLocalGameRequest { Map = "stormkeep", Bots = 2 }.ToOptions(TestPaths.BaseData, null), now);
        using Client client = new("host player") { Wire = new LegacyLoopbackTransport(game.AttachLocalClient()) };
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
        Assert.True(Until(() => client.InGame, 90));
        SvqcHost host = game.Server.Host!;
        // In the game with nothing in front of it: the match runs.
        Assert.True(Until(() => false, 1) || !host.Paused, "paused with no menu open");
        double before = host.Time;
        // A menu (or the console) is in front: DarkPlaces sets the chat button bit, and Xonotic's server pauses
        // a listen server whose every human player has it set.
        client.Input = new LegacyInput { Buttons = 512 };
        Assert.True(Until(() => host.Paused, 3), "the server did not pause while its only player was in a menu");
        double pausedAt = host.Time;
        Assert.True(pausedAt > before);
        Until(() => false, 1);
        Assert.True(host.Paused);
        Assert.Equal(pausedAt, host.Time);
        Assert.True(client.Session.State.Paused, "the client was not told the game is paused (svc_setpause)");
        // The menu closes. A paused server runs no physics, so it never reads the released button from a move:
        // the client program says so itself ("cmd c2s", its setpause message), and SV_PausedTic then resumes.
        client.Input = default;
        List<string> sent = new();
        client.Session.Client.CommandSent += c => sent.Add(c);
        Assert.True(Until(() => !host.Paused, 3), $"the server stayed paused after the menu closed (commands sent: {string.Join(" | ", sent)})");
        Assert.True(Until(() => host.Time > pausedAt + 0.5, 3), "time did not run on after the pause");
        Assert.False(client.Session.State.Paused);
        // The message itself, as Xonotic's server cuts it apart: c2s, a space, and the bytes INSIDE QUOTES.
        Assert.Contains(sent, c => c.StartsWith("c2s \"", StringComparison.Ordinal) && c.EndsWith('"') && c.Length > 6);
        Assert.Equal(0, game.Faults);
        Assert.Equal(0, client.Session.Host?.FaultCount ?? 0);
    }

    [Fact]
    public void The_cmd_command_forwards_its_arguments_as_they_were_written()
    {
        using VirtualFileSystem files = new();
        CvarService cvars = new();
        cvars.Register("some_cvar", "value");
        ConfigInterpreter interpreter = new(cvars, _ => null);
        List<string> sent = new();
        CsqcConsole console = new(interpreter, new LegacyQcHost(cvars, files)) { SendToServer = sent.Add };
        // cl_cmd.c CL_ForwardToServer_f sends Cmd_Args: the rest of the line, quotes and all.
        console.AddText("cmd c2s \"+*\"\n");
        console.AddText("cmd say hello   there\n");
        console.AddText("CMD c2s \"a$$b\"; cmd vote yes\n");     // $$ is how a program writes one dollar sign
        console.AddText("cmd\n");                                // by itself: nothing is sent
        console.AddText("cmd echo $some_cvar\n");                // a real reference is still the interpreter's to expand
        console.AddText("cmdx y\n");                             // not the cmd command
        console.Execute();
        Assert.Equal(new[] { "c2s \"+*\"", "say hello   there", "c2s \"a$b\"", "vote yes", "echo value" }, sent);
        Assert.Equal(5, console.ForwardedToServer);
    }

    // ---- the menu's console ------------------------------------------------------------------------------

    [Fact]
    public void The_menu_console_runs_a_command_a_program_wrote_in_several_pieces()
    {
        using VirtualFileSystem files = new();
        LegacyConsole console = new(files, null);
        // qcsrc/common/campaign_setup.qc CampaignSetup, call for call.
        console.AddText("set g_campaign 1\n");
        console.AddText("set _campaign_name \"");
        console.AddText("xonoticbeta");
        console.AddText("\"\n");
        console.AddText("set _campaign_index ");
        console.AddText("3");
        console.AddText("\n");
        console.AddText("disconnect\nmaxplayers 12\n");
        Assert.Equal(8, console.MaxPlayers);          // nothing has run yet: Cbuf_AddText only appends
        Assert.Equal(5, console.Pending);
        console.Execute(0);
        Assert.Equal("xonoticbeta", console.Cvars.GetString("_campaign_name"));
        Assert.Equal("3", console.Cvars.GetString("_campaign_index"));
        Assert.Equal("1", console.Cvars.GetString("g_campaign"));
        Assert.Equal(12, console.MaxPlayers);
        // A last line with no terminator runs when the buffer does, as in Cbuf_Execute.
        console.AddText("set tail_cvar 7");
        console.Execute(0.1);
        Assert.Equal("7", console.Cvars.GetString("tail_cvar"));
        // A server's text does not size the player's next local game.
        console.EnterSession();
        console.ExecuteNow("maxplayers 64");
        console.LeaveSession();
        Assert.Equal(12, console.MaxPlayers);
    }

    // ---- what a local game is started with ---------------------------------------------------------------

    [Fact]
    public void A_local_game_gets_the_players_settings_and_nothing_that_is_only_a_clients()
    {
        CvarService player = new();
        foreach ((string name, string value) in new[] { ("bot_number", "0"), ("timelimit_override", "-1"), ("g_ctf", "0"), ("g_dm", "1"), ("vid_width", "800"), ("sv_public", "1"),
                     ("_cl_name", "Player"), ("skill", "8"), ("sv_dedicated", "0"), ("port", "26000"), ("cl_movement", "1"), ("hostname", "Xonotic Server") })
            player.Register(name, value);
        player.LockDefaults();
        player.Set("bot_number", "4");            // the Create dialog's slider
        player.Set("timelimit_override", "10");
        player.Set("g_ctf", "1");
        player.Set("g_dm", "0");
        player.Set("_campaign_name", "xonoticbeta");   // created by the menu's "set", after the defaults
        player.Set("skill", "8");                  // set to its own default: still the player's choice, still sent
        player.Set("vid_width", "1280");
        player.Set("_cl_name", "Someone");
        player.Set("cl_movement", "0");
        player.Set("sv_public", "1");
        player.Set("port", "27000");
        player.Set("sv_dedicated", "1");
        List<KeyValuePair<string, string>> cvars = LegacyLocalCvars.FromPlayer(player);
        Assert.Equal(new[] { "_campaign_name", "bot_number", "g_ctf", "g_dm", "skill", "timelimit_override" }, cvars.Select(c => c.Key).ToArray());

        // sv_public: 1 never reaches a server that cannot be listed; 0 and below (stay private, answer nobody) do.
        Assert.False(LegacyLocalCvars.ReachesServer("sv_public", "1"));
        Assert.True(LegacyLocalCvars.ReachesServer("sv_public", "0"));
        Assert.True(LegacyLocalCvars.ReachesServer("sv_public", "-1"));
        Assert.False(LegacyLocalCvars.ReachesServer("bad name", "1"));
        Assert.False(LegacyLocalCvars.ReachesServer("x;quit", "1"));

        SvLocalGameOptions fromMenu = new LegacyLocalGameRequest { Map = "boil", Cvars = cvars, MaxPlayers = 12 }.ToOptions("data", "write");
        Assert.Equal("boil", fromMenu.Map);
        Assert.Null(fromMenu.GameType);                 // the cvars say which mode
        Assert.Equal(4, fromMenu.Bots);                 // bot_number among them
        Assert.Equal(12, fromMenu.MaxPlayers);
        Assert.False(fromMenu.Dedicated);               // a listen server
        Assert.Null(fromMenu.Listen);                   // no socket unless asked
        Assert.Contains(fromMenu.Cvars, c => c.Key == "g_ctf" && c.Value == "1");

        // From the command line: the mode and the bot count asked for win over what the store holds.
        SvLocalGameOptions direct = new LegacyLocalGameRequest { Map = "stormkeep", GameType = "ca", Bots = 2, Cvars = cvars }.ToOptions("data", null);
        Assert.Equal("ca", direct.GameType);
        Assert.Equal(2, direct.Bots);
        Assert.DoesNotContain(direct.Cvars, c => c.Key is "g_ctf" or "g_dm");
        Assert.Equal("2", direct.Cvars.Last(c => c.Key == "bot_number").Value);
    }

    [Theory]
    [InlineData("map stormkeep", LegacyMapCommand.StartMap, "stormkeep")]
    [InlineData("  devmap  \"boil\"  // comment", LegacyMapCommand.StartMap, "boil")]
    [InlineData("changelevel space-elevator", LegacyMapCommand.ChangeLevel, "space-elevator")]
    [InlineData("restart", LegacyMapCommand.Restart, "")]
    [InlineData("maps", LegacyMapCommand.ListMaps, "")]
    [InlineData("load quick", LegacyMapCommand.Unsupported, "")]
    [InlineData("map", LegacyMapCommand.Usage, "")]
    [InlineData("map a b", LegacyMapCommand.Usage, "")]
    [InlineData("map ../../etc/passwd", LegacyMapCommand.Usage, "")]
    [InlineData("map .hidden", LegacyMapCommand.Usage, "")]
    [InlineData("changelevel", LegacyMapCommand.Usage, "")]
    [InlineData("restart now", LegacyMapCommand.Usage, "")]
    [InlineData("mapx stormkeep", LegacyMapCommand.None, "")]
    [InlineData("say map stormkeep", LegacyMapCommand.None, "")]
    [InlineData("", LegacyMapCommand.None, "")]
    public void Level_commands_are_read_as_a_listen_servers_console_reads_them(string line, LegacyMapCommand expected, string map)
    {
        Assert.Equal(expected, LegacyLocalCommands.ParseMapCommand(line, out string parsed, out string usage));
        Assert.Equal(map, parsed);
        Assert.Equal(expected == LegacyMapCommand.Usage, usage.Length > 0);
    }

    [Fact]
    public void Server_commands_cvar_assignments_and_the_LAN_setting_are_recognised()
    {
        Assert.Contains("sv_cmd", LegacyLocalCommands.ServerCommands);
        Assert.Contains("kick", LegacyLocalCommands.ServerCommands);
        Assert.Equal("kick # 2 \"too slow\"", LegacyLocalCommands.Join(new[] { "kick", "#", "2", "too slow" }));

        bool IsCvar(string name) => name is "bot_number" or "timelimit";
        Assert.True(LegacyLocalCommands.TryParseCvarAssignment("bot_number 6", IsCvar, out string name, out string value));
        Assert.Equal(("bot_number", "6"), (name, value));
        Assert.True(LegacyLocalCommands.TryParseCvarAssignment("seta g_new \"a b\"", IsCvar, out name, out value));
        Assert.Equal(("g_new", "a b"), (name, value));
        Assert.False(LegacyLocalCommands.TryParseCvarAssignment("bot_number", IsCvar, out _, out _));        // a query
        Assert.False(LegacyLocalCommands.TryParseCvarAssignment("say bot_number 6", IsCvar, out _, out _));  // a command
        Assert.False(LegacyLocalCommands.TryParseCvarAssignment("kick 6", IsCvar, out _, out _));

        // legacy_listen: off by default, this machine only for "1", otherwise exactly the address written.
        Assert.True(LegacyLocalCommands.TryParseListen("", 26000, out IPEndPoint? listen, out _));
        Assert.Null(listen);
        Assert.True(LegacyLocalCommands.TryParseListen("0", 26000, out listen, out _));
        Assert.Null(listen);
        Assert.True(LegacyLocalCommands.TryParseListen("1", 26001, out listen, out _));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 26001), listen);
        Assert.True(LegacyLocalCommands.TryParseListen("192.168.1.20", 26000, out listen, out _));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 26000), listen);
        Assert.True(LegacyLocalCommands.TryParseListen(" 127.0.0.1:27500 ", 26000, out listen, out _));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 27500), listen);
        foreach (string bad in new[] { "yes", "localhost", "example.org:26000", "127.0.0.1:0", "127.0.0.1:99999", "::1", "1.2.3", "2" })
        {
            Assert.False(LegacyLocalCommands.TryParseListen(bad, 26000, out listen, out string problem), bad);
            Assert.Null(listen);
            Assert.Contains("legacy_listen", problem);
        }
    }

    // ---- the server on its own thread --------------------------------------------------------------------

    [Fact]
    public void A_server_that_cannot_start_says_why_and_leaves_no_thread()
    {
        if (!ServerTestRig.HaveData) return;
        foreach (bool threaded in new[] { true, false })
        {
            using LegacyLocalServer server = new(new LegacyLocalGameRequest { Map = "no_such_map_anywhere" }.ToOptions(TestPaths.BaseData, null), threaded);
            Assert.False(server.WaitUntilStarted(TimeSpan.FromSeconds(60)));
            Assert.Equal(LegacyLocalServerState.Failed, server.State);
            Assert.Contains("no_such_map_anywhere", server.Error);
            Assert.Null(server.Transport);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_local_game_is_joined_through_the_loopback_takes_the_players_cvars_and_carries_the_player_across_a_level_change(bool threaded)
    {
        if (!ServerTestRig.HaveData) return;
        int testThread = Environment.CurrentManagedThreadId;
        List<string> changing = new();
        List<(string From, string To)> changed = new();
        StringBuilder console = new();
        bool eventsOnOwnerThread = true;
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        LegacyLocalServer server = new(new LegacyLocalGameRequest { Map = "stormkeep", GameType = "dm", Bots = 1, MaxPlayers = 8 }.ToOptions(TestPaths.BaseData, null), threaded);
        using Client client = new("host player");
        try
        {
            server.Print += text => { console.Append(text); eventsOnOwnerThread &= Environment.CurrentManagedThreadId == testThread; };
            server.LevelChanging += map => { changing.Add(map); eventsOnOwnerThread &= Environment.CurrentManagedThreadId == testThread; };
            server.LevelChanged += (from, to) => { changed.Add((from, to)); eventsOnOwnerThread &= Environment.CurrentManagedThreadId == testThread; };
            Assert.True(server.WaitUntilStarted(TimeSpan.FromSeconds(60)), server.Error);
            Assert.Equal("stormkeep", server.Map);
            Assert.Equal("dm", server.GameType);
            Assert.Null(server.ListenAddress);
            client.Wire = server.Transport;
            Assert.Equal("local", client.Wire!.Peer);
            client.Session.Connect(clock.Elapsed.TotalSeconds);
            bool Until(Func<bool> condition, double seconds)
            {
                double end = clock.Elapsed.TotalSeconds + seconds;
                while (clock.Elapsed.TotalSeconds < end)
                {
                    if (condition()) return true;
                    server.Update();                        // the owner's frame: events, and the server's frame when it has no thread
                    client.Frame(clock.Elapsed.TotalSeconds);
                    Thread.Sleep(1);
                }
                return condition();
            }
            Assert.True(Until(() => client.InGame, 120), $"{client.Session.Client.State}, signon {client.Session.Client.Signon.Stage}, {client.Session.Client.LastError}");
            Assert.Contains("Server spawned", console.ToString());

            // One cvar store in DarkPlaces: what the player sets while the game runs is the server's at once.
            Assert.True(Until(() => server.ActiveClients == 2, 20), $"players {server.ActiveClients}");   // the player and one bot
            server.SetCvar("bot_number", "3");
            Assert.True(Until(() => server.ActiveClients == 4, 30), $"players {server.ActiveClients} after bot_number 3");
            server.SetCvar("no_such_cvar_of_the_server", "1");      // not created unless asked: nothing to see, nothing breaks

            // The server console, and a level change that takes the player along.
            console.Clear();
            server.Command("status");
            Assert.True(Until(() => console.ToString().Contains("host player", StringComparison.Ordinal), 10));
            server.Command("changelevel boil");
            Assert.True(Until(() => changed.Count == 1 && client.InGame && client.Session.State.WorldModel == "maps/boil.bsp", 120),
                $"changed {changed.Count}, client {client.Session.Client.State} signon {client.Session.Client.Signon.Stage} on {client.Session.State.WorldModel}");
            Assert.Equal(new[] { "stormkeep" }, changing);
            Assert.Equal(("stormkeep", "boil"), changed[0]);
            Assert.Equal("boil", server.Map);
            Assert.Equal(2, server.LevelsStarted);
            Assert.Equal(2, client.Session.ProgramsStarted);        // the client program was unloaded and loaded again
            Assert.Equal(0, client.Session.MessagesNotDecoded);
            Assert.Equal(0, server.Faults);
            Assert.True(eventsOnOwnerThread, "an event was raised on the server's thread");
            LegacyLocalServerStats stats = server.TakeStats();
            Assert.True(stats.Ticks > 0 && stats.MeanFrameMilliseconds > 0);
            _output.WriteLine($"threaded {threaded}: start {server.StartSeconds:0.00} s, level change {server.LastLevelChangeSeconds:0.00} s, {stats.Ticks} ticks at {stats.MeanFrameMilliseconds:0.00} ms a frame (longest {stats.LongestFrameMilliseconds:0.0} ms)");
        }
        finally
        {
            // SV_Shutdown: the client is told, the thread ends, nothing is left running.
            server.Dispose();
        }
        Assert.Equal(LegacyLocalServerState.Stopped, server.State);
        for (int i = 0; i < 200 && client.Session.Client.State == DpClientState.Connected; i++) client.Frame(clock.Elapsed.TotalSeconds);
        Assert.NotEqual(DpClientState.Connected, client.Session.Client.State);
        server.Update();            // a disposed server is inert
        server.Command("status");
    }
}
