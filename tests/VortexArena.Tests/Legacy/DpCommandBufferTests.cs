using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Menu;
using VortexArena.Legacy.Protocol;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// DarkPlaces' command buffer (cmd.c Cbuf_AddText / Cbuf_InsertText / Cbuf_Execute / Cbuf_Execute_Deferred,
/// Cmd_Wait_f, Cmd_Defer_f) as <see cref="DpCommandBuffer"/> ports it, on both consoles that use it, and the
/// rule that came out of a playtest: <c>wait</c> is a local command and never reaches a server.
/// </summary>
public class DpCommandBufferTests
{
    private sealed class Bench
    {
        public readonly CvarService Cvars = new();
        public readonly ConfigInterpreter Interpreter;
        public readonly DpCommandBuffer Buffer;
        public readonly List<string> Ran = new();
        public readonly List<string> Printed = new();
        public readonly Dictionary<string, string> Files = new();

        public Bench()
        {
            Interpreter = new ConfigInterpreter(Cvars, path => Files.GetValueOrDefault(path));
            Buffer = new DpCommandBuffer(Interpreter);
            DpCommandBuffer.Handlers(Buffer, Printed.Add, out Action<IReadOnlyList<string>> wait, out Action<IReadOnlyList<string>> defer);
            Interpreter.RegisterCommand("wait", wait);
            Interpreter.RegisterCommand("defer", defer);
            Interpreter.RegisterCommand("echo", argv => Ran.Add(string.Join(' ', argv.Skip(1))));
            foreach (string name in new[] { "impulse", "+attack", "-attack" })
                Interpreter.RegisterCommand(name, argv => Ran.Add(string.Join(' ', argv)));
        }

        /// <summary>One frame (Cbuf_Frame); returns what ran in it.</summary>
        public string Frame(double realTime = 0)
        {
            Ran.Clear();
            Buffer.Frame(realTime, Interpreter.ExecuteLine);
            return string.Join(",", Ran);
        }
    }

    [Fact]
    public void Wait_Holds_The_Rest_Of_The_Buffer_Until_The_Next_Frame()
    {
        // cmd.c: "This allows commands like: bind g "impulse 5 ; +attack ; wait ; -attack ; impulse 2""
        Bench b = new();
        b.Buffer.AddText("impulse 5 ; +attack ; wait ; -attack ; impulse 2\n");
        Assert.Equal("impulse 5,+attack", b.Frame());
        Assert.True(b.Buffer.HeldForNextFrame);
        Assert.Equal(2, b.Buffer.Pending);
        // Another Cbuf_Execute in the same frame runs nothing more.
        b.Buffer.Execute(b.Interpreter.ExecuteLine);
        Assert.Equal(2, b.Buffer.Pending);
        Assert.Equal("-attack,impulse 2", b.Frame());
        Assert.False(b.Buffer.HeldForNextFrame);
        Assert.Equal("", b.Frame());
        Assert.Equal(1, b.Buffer.Waits);
    }

    [Fact]
    public void Each_Wait_Costs_One_Frame_And_Text_Added_Meanwhile_Runs_Behind_What_Was_Held()
    {
        Bench b = new();
        b.Buffer.AddText("echo a; wait; wait; echo b\n");
        Assert.Equal("a", b.Frame());
        b.Buffer.AddText("echo later\n");
        Assert.Equal("", b.Frame());
        Assert.Equal("b,later", b.Frame());
    }

    [Fact]
    public void A_Wait_Inside_An_Alias_Holds_The_Rest_Of_The_Alias_In_Front_Of_Everything_Else()
    {
        // Cmd_ExecuteAlias inserts the body at the front of the buffer, so the wait stops in the middle of it.
        Bench b = new();
        b.Interpreter.ExecuteLine("alias tap \"echo down; wait; echo up\"");
        b.Buffer.AddText("tap; echo after\n");
        Assert.Equal("down", b.Frame());
        Assert.Equal("up,after", b.Frame());
    }

    [Fact]
    public void Nested_Aliases_Resume_Innermost_First()
    {
        Bench b = new();
        b.Interpreter.ExecuteLine("alias inner \"echo i1; wait; echo i2\"");
        b.Interpreter.ExecuteLine("alias outer \"echo o1; inner; echo o2\"");
        b.Buffer.AddText("outer; echo z\n");
        Assert.Equal("o1,i1", b.Frame());
        Assert.Equal("i2,o2,z", b.Frame());
    }

    [Fact]
    public void The_Held_Rest_Of_An_Alias_Keeps_Its_Arguments_And_Reads_Cvars_When_It_Runs()
    {
        Bench b = new();
        b.Cvars.Register("probe", "before");
        b.Interpreter.ExecuteLine("alias two \"echo $1; wait; echo $2 $probe\"");
        b.Buffer.AddText("two first second\n");
        Assert.Equal("first", b.Frame());
        b.Cvars.Set("probe", "after");
        Assert.Equal("second after", b.Frame());
    }

    [Fact]
    public void A_Wait_In_An_Executed_File_Holds_The_Rest_Of_The_File()
    {
        // Cmd_Exec inserts the file's text at the front of the buffer.
        Bench b = new();
        b.Files["seq.cfg"] = "echo one\nwait\necho two\n";
        b.Buffer.AddText("exec seq.cfg; echo three\n");
        Assert.Equal("one", b.Frame());
        Assert.Equal("two,three", b.Frame());
    }

    [Fact]
    public void Insert_Goes_In_Front_And_Is_Complete_Without_A_Line_End()
    {
        Bench b = new();
        b.Buffer.AddText("echo queued\n");
        b.Buffer.InsertText("echo bind");   // a key's bind: Cbuf_InsertText, no newline
        Assert.Equal("bind,queued", b.Frame());
    }

    [Fact]
    public void A_Line_Without_Its_End_Waits_For_The_Text_That_Completes_It()
    {
        // The buffer is one run of text: Xonotic's menu builds one command out of three localcmd calls.
        Bench b = new();
        b.Buffer.AddText("set _campaign_name \"");
        b.Buffer.AddText("level 5");
        Assert.Equal(1, b.Buffer.Pending);
        b.Buffer.AddText("\"\n");
        Assert.Equal(1, b.Buffer.Pending);
        b.Frame();
        Assert.Equal("level 5", b.Cvars.GetString("_campaign_name"));
        // Cbuf_Execute runs a last line as it stands ("current->pending = false") - on a console that runs its
        // buffer once a frame. A session's runs after every server message, where half a line has to wait.
        b.Buffer.AddText("echo tail");
        Assert.Equal("", b.Frame());
        Assert.Equal(1, b.Buffer.Pending);
        b.Buffer.RunUnterminatedLine = true;
        Assert.Equal("tail", b.Frame());
    }

    [Fact]
    public void Defer_Runs_A_Command_After_Its_Delay_As_Whole_Commands_Of_Its_Own()
    {
        Bench b = new();
        b.Frame(100);
        b.Buffer.AddText("defer 1 \"echo late; echo too\"; defer 0.4 \"echo soon\"; echo now\n");
        Assert.Equal("now", b.Frame(100));
        Assert.Equal(2, b.Buffer.DeferredCount);
        Assert.Equal("", b.Frame(100.2));
        Assert.Equal("soon", b.Frame(100.5));
        // A half line waiting in the buffer is not completed by a deferred command.
        b.Buffer.AddText("echo half");
        Assert.Equal("half,late,too", b.Frame(101.1));
        Assert.Equal(0, b.Buffer.DeferredCount);
    }

    [Fact]
    public void Defer_Lists_And_Clears()
    {
        Bench b = new();
        b.Frame(5);
        b.Buffer.AddText("defer\ndefer 3 \"echo x\"\ndefer\ndefer clear\ndefer 1\n");
        b.Frame(5);
        Assert.Equal("No commands are pending.\n", b.Printed[0]);
        Assert.Equal("-> In      3.00: echo x\n", b.Printed[1]);
        Assert.StartsWith("usage: defer <seconds> <command>", b.Printed[2]);
        Assert.Equal(0, b.Buffer.DeferredCount);
        Assert.Equal("", b.Frame(60));
    }

    [Fact]
    public void A_Command_That_Queues_Itself_Again_Costs_A_Frame_Not_A_Hang()
    {
        Bench b = new();
        int runs = 0;
        b.Interpreter.RegisterCommand("again", _ => { runs++; b.Buffer.AddText("again\n"); });
        b.Buffer.AddText("again\n");
        b.Frame();
        Assert.Equal(DpCommandBuffer.MaxPerExecute, runs);
        // With a wait in it, it is one run a frame: what DarkPlaces' own scripts do.
        b.Buffer.Clear();
        runs = 0;
        b.Interpreter.RegisterCommand("again", _ => { runs++; b.Buffer.AddText("wait; again\n"); });
        b.Buffer.AddText("again\n");
        for (int i = 0; i < 5; i++) b.Frame();
        Assert.Equal(5, runs);
    }

    // ---- the session's console --------------------------------------------------------------------------

    private sealed class Session : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "va-cbuf-" + Guid.NewGuid().ToString("N"));
        public readonly VirtualFileSystem Vfs = new();
        public readonly CvarService Cvars = new();
        public readonly ConfigInterpreter Interpreter;
        public readonly LegacyQcHost Services;
        public readonly CsqcConsole Console;
        public readonly List<string> Sent = new(), Unknown = new(), Ran = new();

        public Session()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "placeholder.txt"), "x");
            Assert.True(Vfs.Mount(Root));
            Interpreter = new ConfigInterpreter(Cvars, _ => null);
            Services = new LegacyQcHost(Cvars, Vfs);
            Console = new CsqcConsole(Interpreter, Services) { SendToServer = Sent.Add, UnknownCommand = Unknown.Add };
            Console.ForwardAsDarkPlaces();
            Interpreter.RegisterCommand("echo", argv => Ran.Add(string.Join(' ', argv.Skip(1))));
        }

        public void Frame(double now)
        {
            Console.NewFrame(now);
            Console.Execute();
            Console.Execute();   // a session runs its buffer more than once a frame
        }

        public void Dispose()
        {
            Vfs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void Wait_Is_Never_Sent_To_The_Server()
    {
        using Session s = new();
        // What Xonotic's map vote queues for every map (client/mapvoting.qc MapVote_CheckPK3), and a bind.
        s.Console.AddText("\necho curl one; wait; cl_cmd mv_download 0\n");
        s.Console.AddText("\necho curl two; wait; cl_cmd mv_download 1\n");
        s.Console.AddText("+showscores; wait; -showscores\n");
        s.Interpreter.ExecuteLine("alias dropit \"cmd say_team dropped; wait; cmd drop\"");
        s.Console.AddText("dropit\n");
        for (int frame = 0; frame < 12; frame++) s.Frame(frame * 0.016);
        Assert.DoesNotContain(s.Sent, c => c.StartsWith("wait", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "say_team dropped", "drop" }, s.Sent);
        Assert.Equal(new[] { "curl one", "curl two" }, s.Ran);
        Assert.Equal(4, s.Console.Buffer.Waits);
        Assert.Equal(0, s.Console.Buffer.Pending);
        Assert.Equal(2, s.Console.GameCommands);   // cl_cmd mv_download 0 and 1 (no program is loaded to take them)
        // "+showscores" is the engine's command; nothing registered it on this bare console, so it is unknown -
        // and an unknown command goes nowhere.
        Assert.Equal(new[] { "+showscores", "-showscores" }, s.Unknown);
    }

    [Fact]
    public void One_Wait_Sequence_A_Frame_Like_DarkPlaces()
    {
        using Session s = new();
        for (int i = 0; i < 3; i++) s.Console.AddText($"\necho request {i}; wait; cmd mv_getpicture {i}\n");
        s.Frame(0);
        Assert.Equal(new[] { "request 0" }, s.Ran);
        Assert.Empty(s.Sent);
        s.Frame(0.016);
        Assert.Equal(new[] { "mv_getpicture 0" }, s.Sent);
        Assert.Equal(new[] { "request 0", "request 1" }, s.Ran);
        s.Frame(0.032);
        s.Frame(0.048);
        Assert.Equal(new[] { "mv_getpicture 0", "mv_getpicture 1", "mv_getpicture 2" }, s.Sent);
    }

    [Fact]
    public void Only_What_DarkPlaces_Forwards_Reaches_The_Server()
    {
        using Session s = new();
        s.Console.AddText("say hello there\nkill\nstatus\nname \"New Name\"\nfrobnicate\njoin\ncmd join\nnoclip\n");
        s.Frame(0);
        // cmd.c Cmd_ExecuteString: "Unknown command" for the rest - "join" is an alias of "cmd join" in
        // Xonotic's commands.cfg, which this bare console has not executed.
        Assert.Equal(new[] { "say hello there", "kill", "status", "name \"New Name\"", "join", "noclip" }, s.Sent);
        Assert.Equal(new[] { "frobnicate", "join" }, s.Unknown);
        Assert.Equal(2, s.Console.UnknownCommands);
        Assert.True(DpClientCommands.IsForwarded("SAY_TEAM"));
        Assert.False(DpClientCommands.IsForwarded("wait"));
        Assert.False(DpClientCommands.IsForwarded("defer"));
    }

    [Fact]
    public void A_Session_Defers_On_Its_Own_Clock_And_A_Console_Nobody_Drives_By_Frames_Does_Not_Stall()
    {
        using Session s = new();
        s.Frame(10);
        s.Console.AddText("defer 0.5 \"cmd ready\"\n");
        s.Frame(10.1);
        Assert.Empty(s.Sent);
        s.Frame(10.7);
        Assert.Equal(new[] { "ready" }, s.Sent);

        // A replay has no frames: there a wait ends one Execute and the next carries on.
        using Session replay = new();
        replay.Console.AddText("echo a; wait; echo b\n");
        replay.Console.Execute();
        Assert.Equal(new[] { "a" }, replay.Ran);
        replay.Console.Execute();
        Assert.Equal(new[] { "a", "b" }, replay.Ran);
    }

    // ---- two buffers on one console (a session started from the Xonotic menu) ---------------------------

    [Fact]
    public void On_The_Menu_Console_A_Servers_Wait_And_Defer_Stay_With_The_Servers_Text()
    {
        string root = Path.Combine(Path.GetTempPath(), "va-cbuf2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "placeholder.txt"), "x");
        using VirtualFileSystem vfs = new();
        try
        {
            Assert.True(vfs.Mount(root));
            List<string> ran = new();
            LegacyConsole menu = new(vfs, null);
            menu.Interpreter.RegisterCommand("note", argv => ran.Add((menu.SessionOrigin ? "session:" : "player:") + argv[1]));
            LegacyQcHost services = new(menu.Cvars, vfs);
            CsqcConsole session = new(menu.Interpreter, services);

            // The server's text: held by its own wait, and what it defers comes back in the session's buffer.
            session.AddText("note s1; wait; note s2; defer 1 \"note s3\"\n");
            menu.AddText("note p1; wait; note p2\n");

            void Frame(double now)
            {
                menu.Execute(now);
                menu.EnterSession();
                try
                {
                    session.NewFrame(now);
                    session.Execute();
                }
                finally { menu.LeaveSession(); }
            }

            Frame(0);
            Assert.Equal(new[] { "player:p1", "session:s1" }, ran);
            Frame(0.1);
            Assert.Equal(new[] { "player:p1", "session:s1", "player:p2", "session:s2" }, ran);
            Assert.Equal(0, menu.Buffer.DeferredCount);
            Assert.Equal(1, session.Buffer.DeferredCount);
            Frame(1.5);
            Assert.Equal("session:s3", ran[^1]);
            session.Detach();
        }
        finally
        {
            vfs.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
