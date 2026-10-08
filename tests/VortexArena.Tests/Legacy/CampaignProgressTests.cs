using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Local;
using VortexArena.Legacy.Menu;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The campaign's progress on its way from the server program of a local game back to the Xonotic menu.
/// DarkPlaces has one cvar store, so when server/campaign.qc CampaignSaveCvar sets
/// <c>g_campaign&lt;name&gt;_index</c> the menu's level list reads it on its next frame, and the
/// <c>campaign.cfg</c> the same function writes carries it to the next start (quake.rc: "exec
/// data/campaign.cfg"). Here the server has a store of its own, and exactly those cvars are handed back.
/// The last test runs Xonotic's own CampaignSaveCvar and needs <c>../Base</c>; it returns early without it.
/// </summary>
public class CampaignProgressTests
{
    private readonly ITestOutputHelper _output;
    public CampaignProgressTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("g_campaignxonoticbeta_index", "1", true)]
    [InlineData("g_campaignxonoticbeta_index", "17", true)]
    [InlineData("g_campaignxonoticbeta_won", "1", true)]
    [InlineData("g_campaign_index", "0", true)]                 // the campaign with the empty name ("singleplayer_start")
    [InlineData("g_campaignmy-own.campaign_index", "3", true)]
    [InlineData("g_campaign", "1", false)]
    [InlineData("g_campaign_skill", "2", false)]
    [InlineData("g_campaign_name", "xonoticbeta", false)]
    [InlineData("g_campaign_forceteam", "1", false)]
    [InlineData("_campaign_index", "4", false)]
    [InlineData("_campaign_name", "xonoticbeta", false)]
    [InlineData("fraglimit", "1", false)]
    [InlineData("sv_cheats", "1", false)]
    [InlineData("rcon_password", "1", false)]
    [InlineData("g_campaignxonoticbeta_index", "", false)]
    [InlineData("g_campaignxonoticbeta_index", "-1", false)]
    [InlineData("g_campaignxonoticbeta_index", "1.5", false)]
    [InlineData("g_campaignxonoticbeta_index", "1; quit", false)]
    [InlineData("g_campaignxonoticbeta_index", "1234567", false)]
    [InlineData("g_campaign x_index", "1", false)]
    [InlineData("g_campaign\"x_index", "1", false)]
    [InlineData("g_campaign;quit;_index", "1", false)]
    [InlineData("g_campaign$x_index", "1", false)]
    public void Only_the_two_cvars_CampaignSaveCvar_writes_are_campaign_progress(string name, string value, bool expected)
    {
        Assert.Equal(expected, LegacyLocalCvars.IsCampaignProgress(name, value));
    }

    private const string Defaults = "seta g_campaign_name \"xonoticbeta\"\nset g_campaign 0\nset fraglimit 30\nseta cl_zoomspeed 8\n";

    [Fact]
    public void Progress_from_a_local_server_becomes_the_players_and_stays_when_the_session_ends()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        LegacyConsole console = rig.Console;
        Assert.True(console.LoadConfig());
        // The menu's level list creates the cvar the first time it is shown: registercvar(name, "", 0), then cvar_set.
        console.Cvars.Register("g_campaignxonoticbeta_index", "");
        console.Cvars.Set("g_campaignxonoticbeta_index", "0");

        // A game on the menu's console: what the session does to cvars is put back at its end...
        console.BeginSession();
        console.EnterSession();
        console.Cvars.Set("fraglimit", "1");                       // a server's stufftext, the client program
        console.Cvars.Set("g_campaignxonoticbeta_index", "7");     // ... also when it is this cvar: a REMOTE server has no say
        console.LeaveSession();

        // ... except the progress the LOCAL server program saved, handed over while the session is still up -
        // even from inside the session's own work.
        console.EnterSession();
        Assert.True(console.AcceptLocalServerCvar("g_campaignxonoticbeta_index", "1"));
        Assert.True(console.AcceptLocalServerCvar("g_campaignxonoticbeta_won", "1"));       // one the menu never created
        Assert.False(console.AcceptLocalServerCvar("fraglimit", "99"));
        Assert.False(console.AcceptLocalServerCvar("g_campaign", "1"));
        Assert.False(console.AcceptLocalServerCvar("g_campaign_name", "evil"));
        Assert.False(console.AcceptLocalServerCvar("cl_zoomspeed", "1"));
        console.LeaveSession();
        Assert.Equal(1, console.Cvars.GetFloat("g_campaignxonoticbeta_index"));   // the menu's list sees it at once

        int restored = console.EndSession();
        Assert.True(restored >= 1);
        Assert.Equal("30", console.Cvars.GetString("fraglimit"));
        Assert.Equal("1", console.Cvars.GetString("g_campaignxonoticbeta_index"));
        Assert.Equal("1", console.Cvars.GetString("g_campaignxonoticbeta_won"));
        Assert.Equal("xonoticbeta", console.Cvars.GetString("g_campaign_name"));
        Assert.Equal("8", console.Cvars.GetString("cl_zoomspeed"));

        // Not a saved cvar, in DarkPlaces either: campaign.cfg is what remembers it.
        string[] saved = File.ReadAllLines(console.SaveConfig()!);
        Assert.DoesNotContain(saved, line => line.Contains("g_campaignxonoticbeta", StringComparison.Ordinal));
    }

    [Fact]
    public void A_remote_sessions_change_to_the_progress_cvar_never_reaches_the_player()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        LegacyConsole console = rig.Console;
        Assert.True(console.LoadConfig());
        console.Cvars.Register("g_campaignxonoticbeta_index", "");
        console.Cvars.Set("g_campaignxonoticbeta_index", "2");
        console.BeginSession();
        console.EnterSession();
        console.Interpreter.ExecuteLine("set g_campaignxonoticbeta_index 40");   // svc_stufftext from a server
        console.Interpreter.ExecuteLine("set g_campaignxonoticbeta_won 1");
        console.LeaveSession();
        Assert.Equal("40", console.Cvars.GetString("g_campaignxonoticbeta_index"));
        console.EndSession();
        Assert.Equal("2", console.Cvars.GetString("g_campaignxonoticbeta_index"));
        Assert.DoesNotContain(File.ReadAllLines(console.SaveConfig()!), line => line.Contains("g_campaign", StringComparison.Ordinal) && !line.Contains("g_campaign_name", StringComparison.Ordinal));
    }

    // server/campaign.qc CampaignSaveCvar, as far as the file goes: read every "set name value" line of
    // campaign.cfg but the one being saved, then write them all back with the new one - and never fclose.
    private static void SaveAsTheProgramDoes(LegacyQcHost host, string name, string value)
    {
        StringBuilder contents = new();
        using (Stream? read = host.OpenRead("data/campaign.cfg"))
        {
            if (read is not null)
            {
                using StreamReader reader = new(read, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 3 && words[0] == "set" && words[1] != name) contents.Append("set ").Append(words[1]).Append(' ').Append(words[2]).Append('\n');
                }
            }
        }
        contents.Append("set ").Append(name).Append(' ').Append(value).Append('\n');
        Stream? write = host.OpenWrite("data/campaign.cfg", append: false);
        Assert.NotNull(write);                       // "Cannot write to campaign file" is a QuakeC error()
        byte[] bytes = Encoding.UTF8.GetBytes(contents.ToString());
        write!.Write(bytes, 0, bytes.Length);
        // No Dispose, no Flush: the program leaks the handle until its VM is shut down.
        GC.KeepAlive(write);
        s_leaked.Add(write);
    }

    private static readonly List<Stream> s_leaked = new();

    [Fact]
    public void A_campaign_file_the_program_never_closes_is_complete_on_disk_and_is_read_at_the_next_start()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        using VirtualFileSystem files = new();
        Assert.True(files.Mount(rig.GameDir));
        LegacyQcHost host = new(new CvarService(), files) { WriteRoot = rig.UserData };
        try
        {
            // Level 1 won: one save. The last level won: two saves in a row (the _won flag, then the index),
            // the second of which reads and rewrites the file the first still holds open.
            SaveAsTheProgramDoes(host, "g_campaignxonoticbeta_index", "1");
            string path = Path.Combine(rig.UserData, "data", "campaign.cfg");
            using (FileStream peek = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new(peek))
                Assert.Equal("set g_campaignxonoticbeta_index 1\n", reader.ReadToEnd());
            SaveAsTheProgramDoes(host, "g_campaignxonoticbeta_won", "1");
            SaveAsTheProgramDoes(host, "g_campaignxonoticbeta_index", "2");

            // "Restarting the game": a new console over the same user directory runs quake.rc's scripts.
            using VirtualFileSystem again = new();
            Assert.True(again.Mount(rig.GameDir));
            LegacyConsole restarted = new(again, rig.UserData, _ => { });
            Assert.True(restarted.LoadConfig());
            Assert.Equal(2, restarted.Cvars.GetFloat("g_campaignxonoticbeta_index"));
            Assert.Equal(1, restarted.Cvars.GetFloat("g_campaignxonoticbeta_won"));
            // And it is still not something config.cfg takes over.
            Assert.DoesNotContain(File.ReadAllLines(restarted.SaveConfig()!), line => line.Contains("g_campaignxonoticbeta", StringComparison.Ordinal));
        }
        finally
        {
            foreach (Stream stream in s_leaked) stream.Dispose();
            s_leaked.Clear();
        }
    }

    [Fact]
    public void A_session_host_lets_go_of_a_cvar_store_that_outlives_it()
    {
        // The leak this guards against: a session on the menu's console subscribed to the menu's cvar store and
        // was kept alive by it - with its program and its whole level - for as long as the menu stayed open.
        CvarService shared = new();
        shared.Register("fov", "100");
        using VirtualFileSystem files = new();
        int heard = 0;
        WeakReference weak = MakeHost(shared, files, () => heard++);
        shared.Set("fov", "90");
        Assert.Equal(1, heard);
        Detach(weak);
        shared.Set("fov", "110");
        Assert.Equal(1, heard);
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "the cvar store still holds the detached host");

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference MakeHost(CvarService cvars, VirtualFileSystem vfs, Action onChange)
        {
            LegacyQcHost host = new(cvars, vfs);
            host.CvarChanged += _ => onChange();
            return new WeakReference(host);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Detach(WeakReference host) => ((LegacyQcHost)host.Target!).Detach();
    }

    [Fact]
    public void Session_commands_on_a_shared_interpreter_lead_nowhere_once_released_and_hold_nothing()
    {
        CvarService cvars = new();
        VortexArena.Common.Config.ConfigInterpreter interpreter = new(cvars, _ => null);
        List<string> ran = new();
        WeakReference weak = Register(interpreter, ran);
        interpreter.ExecuteLine("+attack");
        interpreter.ExecuteLine("impulse 7");
        Assert.Equal(new[] { "+attack", "impulse 7" }, ran);
        Release(weak);
        int unknown = interpreter.UnknownCommands;
        interpreter.ExecuteLine("+attack");                       // still a known command, as in DarkPlaces; it does nothing
        Assert.Equal(new[] { "+attack", "impulse 7" }, ran);
        Assert.Equal(unknown, interpreter.UnknownCommands);
        Assert.Contains("+attack", interpreter.CommandNames);
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "the interpreter's command table still holds the session");

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference Register(VortexArena.Common.Config.ConfigInterpreter interpreter, List<string> ran)
        {
            Session session = new(ran);
            session.Commands.Register(interpreter, "+attack", _ => session.Ran.Add("+attack"), "engine button: press");
            session.Commands.Register(interpreter, "impulse", argv => session.Ran.Add("impulse " + argv[1]));
            session.Commands.Register(interpreter, "impulse", argv => session.Ran.Add("impulse " + argv[1]));   // twice: replaced, not doubled
            Assert.Equal(2, session.Commands.Count);
            return new WeakReference(session);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Release(WeakReference session)
        {
            Session target = (Session)session.Target!;
            // The interpreter holds the relay, which until now holds the session: release is what cuts that.
            target.Commands.Release();
            Assert.Equal(0, target.Commands.Count);
        }
    }

    private sealed class Session
    {
        public readonly List<string> Ran;
        public readonly LegacySessionCommands Commands = new();
        public readonly byte[] Level = new byte[1 << 20];          // "the level"
        public Session(List<string> ran) => Ran = ran;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Xonotics_own_CampaignSaveCvar_reaches_the_owner_the_file_and_the_next_start(bool threaded)
    {
        if (!ServerTestRig.HaveData) return;
        string root = Path.Combine(Path.GetTempPath(), "va-campaign-" + Guid.NewGuid().ToString("N"));
        string userData = Path.Combine(root, "user", "data");
        Directory.CreateDirectory(userData);
        int testThread = Environment.CurrentManagedThreadId;
        List<(string Name, string Value)> handed = new();
        bool onOwnerThread = true;
        try
        {
            // The server starts with what the menu had: level 0 of the campaign. That value going round is not news.
            LegacyLocalGameRequest request = new()
            {
                Map = "boil", GameType = "dm", Bots = 0, MaxPlayers = 4,
                Cvars = new[] { new KeyValuePair<string, string>("g_campaignxonoticbeta_index", "0") },
            };
            LegacyLocalServer server = new(request.ToOptions(TestPaths.BaseData, userData), threaded);
            server.PlayerCvar += (name, value) =>
            {
                handed.Add((name, value));
                onOwnerThread &= Environment.CurrentManagedThreadId == testThread;
            };
            try
            {
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                bool Until(Func<bool> condition, double seconds)
                {
                    double end = clock.Elapsed.TotalSeconds + seconds;
                    while (clock.Elapsed.TotalSeconds < end)
                    {
                        if (condition()) return true;
                        server.Update();
                        Thread.Sleep(1);
                    }
                    return condition();
                }
                Assert.True(Until(() => server.State != LegacyLocalServerState.Starting, 120), server.Error);
                Assert.Equal(LegacyLocalServerState.Running, server.State);
                Until(() => false, 0.2);
                Assert.Empty(handed);

                // The program's own function, as CampaignPreIntermission calls it when a level is won.
                int ran = 0;
                void Save(string name, float value) => server.Post(game =>
                {
                    QcVm vm = game.Server.Host!.Vm;
                    int function = vm.FindFunction("CampaignSaveCvar");
                    if (function == 0) return;
                    vm.SetArgInt(0, vm.TempString(name));
                    vm.SetArgFloat(1, value);
                    vm.Execute(function, 2);
                    Interlocked.Increment(ref ran);
                });
                Save("g_campaignxonoticbeta_index", 1);
                Assert.True(Until(() => handed.Count >= 1, 20), $"ran {ran}, state {server.State}, error {server.Error}");
                Assert.Equal(("g_campaignxonoticbeta_index", "1"), handed[0]);
                string path = Path.Combine(userData, "data", "campaign.cfg");
                Assert.True(File.Exists(path), "the program did not write campaign.cfg under the user directory");
                string Read()
                {
                    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using StreamReader reader = new(stream);
                    return reader.ReadToEnd();
                }
                Assert.Equal("set g_campaignxonoticbeta_index 1\n", Read());        // on disk although never closed

                // The last level: the _won flag (created with its value: no change is raised) and the index again.
                Save("g_campaignxonoticbeta_won", 1);
                Save("g_campaignxonoticbeta_index", 2);
                Assert.True(Until(() => handed.Contains(("g_campaignxonoticbeta_index", "2")), 20), $"ran {ran}");
                Assert.Equal(0, server.Faults);
            }
            finally { server.Dispose(); }
            Assert.Contains(("g_campaignxonoticbeta_won", "1"), handed);          // at the latest when the game ended
            Assert.Equal(3, handed.Count);
            Assert.True(onOwnerThread, "a cvar was handed over on the server's thread");
            Assert.Equal(new[] { "set g_campaignxonoticbeta_won 1", "set g_campaignxonoticbeta_index 2" },
                File.ReadAllLines(Path.Combine(userData, "data", "campaign.cfg")));
            Assert.Equal(0, LegacyLocalServer.LiveThreads);

            // The menu's console takes what was handed over...
            using (VirtualFileSystem files = new())
            {
                Assert.True(files.MountGameDir(TestPaths.BaseData));
                LegacyConsole console = new(files, userData, _ => { });
                Assert.True(console.LoadConfig());
                foreach ((string name, string value) in handed) Assert.True(console.AcceptLocalServerCvar(name, value));
                Assert.Equal(2, console.Cvars.GetFloat("g_campaignxonoticbeta_index"));
            }
            // ... and a game started afresh reads the file the program wrote.
            using (VirtualFileSystem files = new())
            {
                Assert.True(files.MountGameDir(TestPaths.BaseData));
                LegacyConsole restarted = new(files, userData, _ => { });
                Assert.True(restarted.LoadConfig());
                Assert.Equal(2, restarted.Cvars.GetFloat("g_campaignxonoticbeta_index"));
                Assert.Equal(1, restarted.Cvars.GetFloat("g_campaignxonoticbeta_won"));
            }
            _output.WriteLine($"threaded {threaded}: handed over {string.Join(", ", handed.Select(h => h.Name + "=" + h.Value))}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
