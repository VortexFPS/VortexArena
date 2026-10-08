using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Menu;
using VortexArena.Legacy.Presentation;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The real Xonotic menu program driven without a window: frames of m_draw, a pointer that moves, clicks
/// and keys - what a player does, as far as a draw list can show it. Needs the ../Base checkout; without
/// it every case passes without running.
/// </summary>
public class MenuRealDataWalkTests
{
    private const int KMouse1 = 512, KEscape = 27;
    private readonly ITestOutputHelper _output;
    public MenuRealDataWalkTests(ITestOutputHelper output) => _output = output;

    private sealed class Walk : IDisposable
    {
        public readonly VirtualFileSystem Vfs = new();
        public readonly string UserData = Path.Combine(Path.GetTempPath(), "va-menuwalk-" + Guid.NewGuid().ToString("N"));
        public readonly LegacyConsole Console;
        public readonly HeadlessMenuDraw Draw;
        public readonly MenuHost Host;
        public readonly StringBuilder Printed = new();
        public readonly List<string> Commands = new();
        public (float X, float Y) Mouse = (640, 360);
        private double _time = 1;

        public Walk()
        {
            Console = MenuRealDataProbeTests.NewConsole(Vfs, UserData, Printed);
            Draw = new HeadlessMenuDraw(Vfs);
            Console.Interpreter.RegisterCommand("loadfont", argv => { if (argv.Count >= 3) Draw.Fonts.Load(argv[1], argv[2], -1, 1, 0); });
            // What the engine would do with these is not the menu's business: the test wants to see them asked for.
            foreach (string name in new[] { "quit", "connect", "disconnect", "map", "vid_restart", "cd", "r_restart", "snd_restart" })
                Console.Interpreter.RegisterCommand(name, argv => Commands.Add(string.Join(' ', argv)));
            Console.Interpreter.UnknownCommandHandler = (name, argv) => Commands.Add(string.Join(' ', argv));
            // A player who has a name: without one the first-run dialog covers the main menu.
            Console.Cvars.Set("_cl_name", "Walker");
            // What the engine makes of a 1280x720 window (cl_screen.c, vid_conwidthauto): 600 units high, 1066 wide.
            Console.Cvars.Set("vid_conwidth", "1066");
            Console.Cvars.Set("vid_conheight", "600");
            Host = new MenuHost(File.ReadAllBytes(MenuRealDataProbeTests.MenuDat), Console, Draw,
                new MenuHostOptions { VideoSize = () => (1280, 720), WindowMouse = () => Mouse, Clock = () => _time },
                text => Printed.Append(text), _ => { });
            Assert.True(Host.Init(), Host.Faults.Count > 0 ? Host.Faults[0].Message : "m_init did not run");
            Host.ToggleMenu(1);
        }

        public float ConWidth => Console.Cvars.GetFloat("vid_conwidth");
        public float ConHeight => Console.Cvars.GetFloat("vid_conheight");

        /// <summary>Frames of m_draw, 20 ms of the menu's clock apart, with the command buffer run between them.</summary>
        public void Frames(int count)
        {
            for (int i = 0; i < count && Host.FaultCount == 0; i++)
            {
                Draw.List.Clear();
                Host.DrawFrame(1280, 720);
                Console.Execute(_time += 0.02);
            }
        }

        /// <summary>The text commands of the last frame that say exactly this (colour codes aside) and are visible.</summary>
        public LegacyDrawCommand[] Find(string text) => Draw.List.Commands
            .Where(c => c.Kind == LegacyDrawKind.Text && c.Color.A > 0.5f && c.Text is not null && Plain(c.Text) == text).ToArray();

        public bool Shows(string text) => Draw.List.Commands.Any(c => c.Kind == LegacyDrawKind.Text && c.Text is not null && Plain(c.Text).Contains(text, StringComparison.Ordinal));

        private static string Plain(string text)
        {
            StringBuilder visible = new();
            LegacyTextColors.Walk(text, false, new LegacyColor(1, 1, 1, 1), null, visible);
            return visible.ToString().Trim();
        }

        /// <summary>Draws until the text is on screen (a dialog fades in), up to five seconds of the menu's clock.</summary>
        public LegacyDrawCommand WaitFor(string text)
        {
            for (int i = 0; i < 250; i++)
            {
                Frames(1);
                if (Find(text) is { Length: > 0 } found) return found[0];
            }
            string seen = string.Join(" | ", Draw.List.Commands.Where(c => c.Kind == LegacyDrawKind.Text).Select(c => c.Text).Distinct().Take(60));
            Assert.Fail($"\"{text}\" never appeared. Faults: {Host.FaultCount}. Text on screen: {seen}");
            return default;
        }

        /// <summary>Moves the pointer to a point given in the menu's own (vid_conwidth) units.</summary>
        public void MoveTo(float x, float y)
        {
            Mouse = (x * 1280 / ConWidth, y * 720 / ConHeight);
            Frames(3);
        }

        /// <summary>Clicks a main-menu dialog, found by its title. The title is drawn full size above the
        /// shrunken dialog and is not part of it: the click goes just below, into the dialog's frame.</summary>
        public void ClickDialog(in LegacyDrawCommand title) => ClickAt(title.X + 4, title.Y + title.Height + 6);

        public void ClickText(in LegacyDrawCommand text) => ClickAt(text.X + 4, text.Y + Math.Max(2, text.Height * 0.5f));

        public void ClickAt(float x, float y)
        {
            MoveTo(x, y);
            Host.KeyEvent(KMouse1, 0, true);
            Frames(2);
            Host.KeyEvent(KMouse1, 0, false);
            Frames(4);
        }

        public void Key(int key, int ascii = 0)
        {
            Host.KeyEvent(key, ascii, true);
            Frames(1);
            Host.KeyEvent(key, ascii, false);
            Frames(2);
        }

        public string Describe() => string.Join(" | ", Draw.List.Commands.Select(c => $"{c.Kind} {c.Text} {c.X},{c.Y} {c.Width}x{c.Height} a{c.Color.A}")) + " // keydest " + Host.KeyDest + " // cmds " + string.Join(";", Commands) + " // " + Printed.ToString()[^Math.Min(600, Printed.Length)..];

        public string Texts() => string.Join(" | ", Draw.List.Commands.Where(c => c.Kind == LegacyDrawKind.Text).Select(c => $"{c.Text}@{c.X:0},{c.Y:0} a{c.Color.A:0.##}").Take(80));

        public string Fault => Host.Faults.Count > 0 ? Host.Faults[0].EntryPoint + ": " + Host.Faults[0].Message : "";

        public void Dispose()
        {
            Host.Shutdown();
            Host.Dispose();
            Vfs.Dispose();
            try { if (Directory.Exists(UserData)) Directory.Delete(UserData, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void TheMainMenu_Draws_AndTheQuitDialogQuits()
    {
        if (!File.Exists(MenuRealDataProbeTests.MenuDat)) return;
        using Walk walk = new();
        walk.Frames(120);
        Assert.True(walk.Host.FaultCount == 0, walk.Fault);
        Assert.Equal(MenuKeyDest.Menu, walk.Host.KeyDest);
        Assert.True(walk.Draw.List.Count > 20, $"the main menu drew {walk.Draw.List.Count} commands: " + walk.Describe());
        Assert.Contains(walk.Draw.List.Commands, c => c.Kind == LegacyDrawKind.Picture);
        foreach (string button in new[] { "Singleplayer", "Multiplayer", "Settings", "Quit" })
            Assert.True(walk.Find(button).Length > 0, $"no \"{button}\" on the main menu: " + walk.Texts());

        // Click "Quit" where the program drew it, then "Yes" where the dialog that opens draws it.
        walk.ClickDialog(walk.Find("Quit")[0]);
        LegacyDrawCommand yes = walk.WaitFor("Yes");
        Assert.True(walk.Shows("Are you sure you want to quit?"));
        Assert.DoesNotContain("quit", walk.Commands);                             // not before the click
        walk.Frames(150);                                                         // let the dialog finish opening
        walk.ClickText(walk.Find("Yes").DefaultIfEmpty(yes).First());
        walk.Frames(5);
        Assert.True(walk.Host.FaultCount == 0, walk.Fault);
        Assert.True(walk.Commands.Contains("quit"), "no quit: " + string.Join(";", walk.Commands) + " // " + walk.Texts());
        Assert.Empty(walk.Host.UnimplementedBuiltins);
    }

    [Fact]
    public void EveryMainMenuDialog_OpensAndClosesWithoutAFault()
    {
        if (!File.Exists(MenuRealDataProbeTests.MenuDat)) return;
        using Walk walk = new();
        walk.Frames(120);
        int most = 0;
        foreach (string button in new[] { "Singleplayer", "Multiplayer", "Media", "Settings" })
        {
            LegacyDrawCommand[] found = walk.Find(button);
            if (found.Length == 0) { _output.WriteLine($"(no \"{button}\" button in this version of the menu)"); continue; }
            walk.ClickDialog(found[0]);
            walk.Frames(25);
            // The dialog came forward: its title is no longer where the overview had it.
            Assert.DoesNotContain(walk.Find(button), c => c.X == found[0].X && c.Y == found[0].Y);
            // Sweep the pointer over the open dialog - tooltips, hover states, list highlights - and wheel a list.
            for (int step = 0; step < 12; step++)
            {
                walk.MoveTo(walk.ConWidth * (0.15f + 0.06f * step), walk.ConHeight * (0.2f + 0.05f * step));
                walk.Key(515 + (step & 1));                                       // K_MWHEELUP / K_MWHEELDOWN
            }
            most = Math.Max(most, walk.Draw.List.Count);
            _output.WriteLine($"{button}: {walk.Draw.List.Count} draw commands, {walk.Host.FaultCount} faults");
            Assert.True(walk.Host.FaultCount == 0, button + ": " + walk.Fault);
            Assert.True(walk.Draw.List.Count > 20, $"{button} drew {walk.Draw.List.Count} commands");
            walk.Key(KEscape);
            walk.Frames(25);
            Assert.True(walk.Host.FaultCount == 0, button + " closing: " + walk.Fault);
        }
        Assert.True(most > 100, $"the fullest dialog drew {most} commands");
        Assert.Empty(walk.Host.UnimplementedBuiltins);
        Assert.Equal(MenuKeyDest.Menu, walk.Host.KeyDest);
    }

    [Fact]
    public void DirectMenu_OpensANamedDialog_AndASettingSetThereIsSaved()
    {
        if (!File.Exists(MenuRealDataProbeTests.MenuDat)) return;
        using Walk walk = new();
        walk.Frames(120);
        walk.Host.GameCommand("directmenu Quit");
        walk.WaitFor("Yes");
        walk.ClickText(walk.WaitFor("No"));
        walk.Frames(120);
        Assert.DoesNotContain("quit", walk.Commands);
        Assert.True(walk.Host.FaultCount == 0, walk.Fault);

        // The menu program sets a cvar (as a slider does) and the player's configuration has it.
        walk.Console.ExecuteNow("menu_cmd sync");
        walk.Console.Cvars.Set("fov", "110");
        walk.Console.Keys.SetBinding('t', 0, "+forward");
        string path = walk.Console.SaveConfig()!;
        Assert.StartsWith(walk.UserData, path);
        string[] lines = File.ReadAllLines(path);
        Assert.Contains("bind t \"+forward\"", lines);
        Assert.Contains(lines, line => line.Contains("\"fov\" \"110\""));
        // The only other file is the one the menu program itself writes with fopen, which lands under data/ as in DarkPlaces.
        Assert.Equal(new[] { "config.cfg", Path.Combine("data", "defaultMENUQC.cfg") },
            Directory.GetFiles(walk.UserData, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(walk.UserData, f)).OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.True(walk.Host.FaultCount == 0, walk.Fault);
    }
}
