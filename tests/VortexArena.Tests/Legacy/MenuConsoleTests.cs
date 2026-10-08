using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VortexArena.Legacy.Menu;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The Xonotic console the menu program owns (Menu/LegacyConsole.cs), its key bindings
/// (MenuKeyBindings.cs, port of keys.c) and the key dispatch (LegacyKeyEvents.cs, port of Key_Event):
/// what is saved and how, what a session on the console may and may not leave behind, and where a key
/// press goes.
/// </summary>
public class MenuConsoleTests
{
    private const string Defaults = """
        // what Xonotic's default.cfg does, in small
        seta cl_zoomspeed 8 "how fast the zoom is"
        set g_temporary 3
        seta menu_sounds 0
        volume 0.6
        bind w +forward
        bind MOUSE1 "+fire"
        alias +fire +attack
        alias makesaved "seta $1 \"${$1}\""
        """;

    [Fact]
    public void LoadConfig_RunsDefaultsThenThePlayersOwnFile_AndSaveWritesOnlyWhatChanged()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        Directory.CreateDirectory(rig.UserData);
        File.WriteAllText(Path.Combine(rig.UserData, "config.cfg"), "unbindall\nbind e \"+hook\"\nseta \"cl_zoomspeed\" \"3.5\"\nseta my_own \"kept\"\n\"sensitivity\" \"7\"\n");
        Assert.True(rig.Console.LoadConfig());

        Assert.Equal("3.5", rig.Console.Cvars.GetString("cl_zoomspeed"));
        Assert.Equal("8", rig.Console.Cvars.GetDefault("cl_zoomspeed"));          // locked after default.cfg, before config.cfg
        Assert.Equal("how fast the zoom is", rig.Console.Cvars.GetDescription("cl_zoomspeed"));
        Assert.Equal("+hook", rig.Console.Keys.GetBind('e', 0));
        Assert.Null(rig.Console.Keys.GetBind('w', 0));                            // "unbindall" at the top of a saved config

        rig.Console.Cvars.Set("g_temporary", "9");                                // "set", not "seta": never saved
        rig.Console.Cvars.Set("volume", "0.25");                                  // an engine cvar with CF_ARCHIVE
        rig.Console.Cvars.Set("menu_sounds", "2");
        rig.Console.Cvars.Set("rcon_password", "hunter2");                        // CF_PRIVATE: never written
        rig.Console.Keys.SetBinding(32, 0, "say \"hi\"; +jump");
        rig.Console.Keys.SetBinding(9, 2, "+showscores");

        string? path = rig.Console.SaveConfig();
        Assert.Equal(Path.Combine(rig.UserData, "config.cfg"), path);
        string[] lines = File.ReadAllLines(path!);
        // Key_WriteBindings, then Cvar_WriteVariables: an engine cvar bare, a created one behind "seta".
        Assert.Equal("unbindall", lines[0]);
        Assert.Contains("bind e \"+hook\"", lines);
        Assert.Contains("bind SPACE \"say \\\"hi\\\"; +jump\"", lines);
        Assert.Contains("in_bind 2 TAB \"+showscores\"", lines);
        Assert.Contains("seta \"cl_zoomspeed\" \"3.5\"", lines);
        Assert.Contains("seta \"menu_sounds\" \"2\"", lines);
        Assert.Contains("seta \"my_own\" \"kept\"", lines);
        Assert.Contains("\"volume\" \"0.25\"", lines);
        Assert.Contains("\"sensitivity\" \"7\"", lines);
        Assert.DoesNotContain(lines, line => line.Contains("g_temporary") || line.Contains("rcon_password") || line.Contains("hunter2"));
        Assert.DoesNotContain(lines, line => line.Contains("pr_checkextension") || line.Contains("vid_conwidth"));   // at their defaults

        // What was written reads back to the same state.
        using MenuRig again = new(("default.cfg", Defaults));
        Directory.CreateDirectory(again.UserData);
        File.Copy(path!, Path.Combine(again.UserData, "config.cfg"));
        Assert.True(again.Console.LoadConfig());
        Assert.Equal("0.25", again.Console.Cvars.GetString("volume"));
        Assert.Equal("say \"hi\"; +jump", again.Console.Keys.GetBind(32, 0));
        Assert.Equal("+showscores", again.Console.Keys.GetBind(9, 2));
        Assert.Equal("kept", again.Console.Cvars.GetString("my_own"));
    }

    [Fact]
    public void NothingIsWritten_OutsideTheUserDirectory_OrBeforeTheDefaultsLoaded()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        Assert.Null(rig.Console.SaveConfig());                                    // "don't save a config if it crashed in startup"
        Assert.True(rig.Console.LoadConfig());
        Assert.Null(rig.Console.SaveConfig("../escape.cfg"));
        Assert.Null(rig.Console.SaveConfig("C:/escape.cfg"));
        Assert.NotNull(rig.Console.SaveConfig("backup/other.cfg"));
        Assert.True(File.Exists(Path.Combine(rig.UserData, "backup", "other.cfg")));
        Assert.Single(Directory.GetFiles(Path.Combine(rig.Root, "user"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ASession_LeavesThePlayersCvarsAsTheyWere()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        Assert.True(rig.Console.LoadConfig());
        List<string> ran = new();
        rig.Console.RegisterPlayerCommand("connect", argv => ran.Add(string.Join(' ', argv)));
        rig.Console.Cvars.Set("cl_zoomspeed", "4");

        rig.Console.BeginSession();
        rig.Console.EnterSession();
        Assert.True(rig.Console.SessionOrigin);
        // What a server's console text and its client program do.
        rig.Console.ExecuteNow("seta cl_zoomspeed 99; volume 0; seta sv_planted 1; bind w quit; unbindall");
        rig.Console.ExecuteNow("connect evil.example");
        rig.Console.ExecuteNow("saveconfig stolen.cfg");
        rig.Console.Cvars.Set("menu_sounds", "1");
        rig.Console.LeaveSession();
        Assert.False(rig.Console.SessionOrigin);

        Assert.Equal("99", rig.Console.Cvars.GetString("cl_zoomspeed"));          // live while the session lasts
        Assert.Equal("+forward", rig.Console.Keys.GetBind('w', 0));               // binds are the player's
        Assert.Empty(ran);
        Assert.False(File.Exists(Path.Combine(rig.UserData, "stolen.cfg")));
        Assert.Contains("not followed", rig.Printed.ToString());

        // The player changes a setting in the in-game menu meanwhile: that one stands.
        rig.Console.Cvars.Set("volume", "0.3");
        rig.Console.ExecuteNow("connect good.example");
        Assert.Equal(new[] { "connect good.example" }, ran);

        // A config saved DURING the session has the player's values, not the server's.
        string[] during = File.ReadAllLines(rig.Console.SaveConfig()!);
        Assert.Contains("seta \"cl_zoomspeed\" \"4\"", during);
        Assert.Contains("\"volume\" \"0.3\"", during);
        Assert.DoesNotContain(during, line => line.Contains("99") || line.Contains("menu_sounds"));

        Assert.Equal(2, rig.Console.EndSession());                                // cl_zoomspeed and menu_sounds
        Assert.Equal("4", rig.Console.Cvars.GetString("cl_zoomspeed"));
        Assert.Equal("0", rig.Console.Cvars.GetString("menu_sounds"));
        Assert.Equal("0.3", rig.Console.Cvars.GetString("volume"));
        string[] after = File.ReadAllLines(rig.Console.SaveConfig()!);
        Assert.Contains("seta \"cl_zoomspeed\" \"4\"", after);
        Assert.DoesNotContain(after, line => line.Contains("sv_planted"));       // a server's "seta" saves nothing
    }

    [Fact]
    public void VirtualCvars_AreOneVariableUnderTwoNames()
    {
        using MenuRig rig = new(("default.cfg", "name Player\n"));
        Assert.True(rig.Console.LoadConfig());
        Assert.Equal("Player", rig.Console.Cvars.GetString("_cl_name"));          // "name" is _cl_name
        rig.Console.Cvars.Set("showfps", "1");                                    // what the Video settings' box sets
        Assert.Equal("1", rig.Console.Cvars.GetString("cl_showfps"));
        rig.Console.Cvars.Set("_cl_name", "Tester");
        Assert.Equal("Tester", rig.Console.Cvars.GetString("name"));
        Assert.Equal(rig.Console.CvarTypeFlags("cl_showfps"), rig.Console.CvarTypeFlags("showfps"));
        Assert.Equal(1 | 2 | 8 | 16, rig.Console.CvarTypeFlags("showfps"));       // exists, saved, engine, described

        string[] lines = File.ReadAllLines(rig.Console.SaveConfig()!);
        Assert.Contains("\"cl_showfps\" \"1\"", lines);
        Assert.Contains("\"_cl_name\" \"Tester\"", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("\"showfps\"", StringComparison.Ordinal) || line.Contains("\"name\""));
    }

    [Fact]
    public void CvarTypeFlags_AreDarkPlaces()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        Assert.True(rig.Console.LoadConfig());
        Assert.Equal(0, rig.Console.CvarTypeFlags("no_such_cvar"));
        Assert.Equal(1 | 2 | 16, rig.Console.CvarTypeFlags("cl_zoomspeed"));      // a seta with a description: not the engine's
        Assert.Equal(1, rig.Console.CvarTypeFlags("g_temporary"));
        Assert.Equal(1 | 4 | 8 | 16, rig.Console.CvarTypeFlags("rcon_password")); // private
        Assert.Equal(1 | 8 | 16 | 32, rig.Console.CvarTypeFlags("pr_checkextension"));   // read-only
        Assert.Equal(OperatingSystem.IsMacOS() ? 1 | 2 | 8 | 16 : 0, rig.Console.CvarTypeFlags("apple_mouse_noaccel"));

        // The menu program's view: a private cvar reads as nothing and cannot be set.
        MenuQcHost services = new(rig.Console);
        rig.Console.Cvars.Set("rcon_password", "secret");
        services.CvarSet("rcon_password", "changed");
        Assert.Equal("secret", rig.Console.Cvars.GetString("rcon_password"));
        Assert.DoesNotContain("rcon_password", services.CvarNames("rcon", ""));
        Assert.False(services.RegisterCvar("volume", "1", 0));                    // exists
        Assert.False(services.RegisterCvar("toggle", "1", 0));                    // "is a command"
        Assert.True(services.RegisterCvar("_menu_new", "5", 0));
        Assert.Equal(1 | 8, rig.Console.CvarTypeFlags("_menu_new"));              // registercvar: not "allocated"
    }

    [Fact]
    public void TheBuffer_Waits_Defers_AndToggles()
    {
        using MenuRig rig = new(("default.cfg", Defaults));
        Assert.True(rig.Console.LoadConfig());
        rig.Console.AddText("set order a; wait; set order b\n");
        rig.Console.InsertText("set first 1\n");                                  // Cbuf_InsertText: ahead of what waits
        rig.Console.Execute(10);
        Assert.Equal("1", rig.Console.Cvars.GetString("first"));
        Assert.Equal("a", rig.Console.Cvars.GetString("order"));                  // "wait" stops the buffer for this frame
        Assert.Equal(1, rig.Console.Pending);
        rig.Console.Execute(10.1);
        Assert.Equal("b", rig.Console.Cvars.GetString("order"));

        rig.Console.AddText("defer 2 \"set late 1\"\n");
        rig.Console.Execute(11);
        rig.Console.Execute(12.5);
        Assert.False(rig.Console.Cvars.Has("late"));
        rig.Console.Execute(13.1);
        Assert.Equal("1", rig.Console.Cvars.GetString("late"));

        // Several that come due in the same frame are separate commands, in order (the menu's Leave button:
        // "defer 0.4 disconnect; defer 0.4 wait; defer 0.4 \"g_campaign 0\"; defer 0.4 menu_sync").
        rig.Console.AddText("defer 0.4 \"set due_a 1\"; defer 0.4 wait; defer 0.4 \"set due_b 2\"; defer 0.4 \"set due_c 3\"\n");
        rig.Console.Execute(14);
        rig.Console.Execute(14.2);
        Assert.False(rig.Console.Cvars.Has("due_a"));
        rig.Console.Execute(14.5);
        Assert.Equal("1", rig.Console.Cvars.GetString("due_a"));
        Assert.False(rig.Console.Cvars.Has("due_b"));                             // the deferred "wait" held the rest for a frame
        rig.Console.Execute(14.6);
        Assert.Equal("2", rig.Console.Cvars.GetString("due_b"));
        Assert.Equal("3", rig.Console.Cvars.GetString("due_c"));
        rig.Console.ExecuteNow("toggle menu_sounds");
        Assert.Equal("1", rig.Console.Cvars.GetString("menu_sounds"));
        rig.Console.ExecuteNow("toggle menu_sounds 2");                           // not 0 and not 2: back to 0
        Assert.Equal("0", rig.Console.Cvars.GetString("menu_sounds"));
        rig.Console.ExecuteNow("toggle menu_sounds 2");
        Assert.Equal("2", rig.Console.Cvars.GetString("menu_sounds"));
        rig.Console.ExecuteNow("toggle g_temporary a b c");                       // not in the list: the first
        Assert.Equal("a", rig.Console.Cvars.GetString("g_temporary"));
        rig.Console.ExecuteNow("toggle g_temporary a b c");
        Assert.Equal("b", rig.Console.Cvars.GetString("g_temporary"));
        rig.Console.ExecuteNow("prvm_language de; color 3 12");
        Assert.Equal("de", rig.Console.Cvars.GetString("prvm_language"));
        Assert.Equal("60", rig.Console.Cvars.GetString("_cl_color"));
    }

    // ---- key bindings -----------------------------------------------------------------------------

    [Fact]
    public void Bindings_AreFoundThroughTheActiveMaps()
    {
        MenuKeyBindings keys = new();
        Assert.True(keys.SetBinding('a', 0, "+moveleft"));
        Assert.True(keys.SetBinding('a', 1, "hidden by map 0"));
        Assert.True(keys.SetBinding('b', 1, "+moveleft"));
        Assert.True(keys.SetBinding(600, 3, "+moveleft"));
        Assert.False(keys.SetBinding(-1, 0, "x"));
        Assert.False(keys.SetBinding(MenuKeyBindings.MaxKeys, 0, "x"));
        Assert.False(keys.SetBinding('a', 8, "x"));

        Assert.Equal("+moveleft", keys.GetBind('a', -1));                         // foreground map 0
        Assert.Equal("+moveleft", keys.GetBind('b', -1));                         // falls back to map 1
        Assert.Null(keys.GetBind(600, -1));
        Assert.Equal("hidden by map 0", keys.GetBind('a', 1));

        Span<int> found = stackalloc int[5];
        keys.FindKeysForCommand("+moveleft", found, -1);
        Assert.Equal(new[] { (int)'a', 'b', -1, -1, -1 }, found.ToArray());
        keys.FindKeysForCommand("+moveleft", found, 3);
        Assert.Equal(new[] { 600, -1, -1, -1, -1 }, found.ToArray());
        keys.FindKeysForCommand("hidden by map 0", found, -1);                    // map 0 has another command on that key
        Assert.Equal(-1, found[0]);

        Assert.True(keys.SetBindMap(3, -1));                                      // a negative number leaves that map as it is
        Assert.Equal((3, 1), keys.BindMap);
        Assert.Equal("+moveleft", keys.GetBind(600, -1));
        Assert.False(keys.SetBindMap(8, 0));
        Assert.True(keys.SetBinding('a', 0, ""));                                 // "make "" binds be removed"
        Assert.Null(keys.GetBind('a', 0));
    }

    // ---- Key_Event ---------------------------------------------------------------------------------

    private sealed class Keyboard
    {
        public MenuKeyDest Dest = MenuKeyDest.Game;
        public bool Console, GameConsumes;
        public readonly List<string> Log = new();
        public readonly MenuKeyBindings Binds = new();
        public readonly LegacyKeyEvents Keys;

        public Keyboard()
        {
            Keys = new LegacyKeyEvents(Binds)
            {
                KeyDest = () => Dest,
                ConsoleActive = () => Console,
                MenuKey = (key, ascii, down) => Log.Add($"menu {key} {ascii} {(down ? "down" : "up")}"),
                ToggleMenu = mode => Log.Add("togglemenu " + mode),
                GameInput = (type, key, ascii) => { Log.Add($"csqc {type} {key} {ascii}"); return GameConsumes; },
                ConsoleKey = (key, ascii) => Log.Add($"console {key} {ascii}"),
                ToggleConsole = () => Log.Add("toggleconsole"),
                InsertText = text => Log.Add("insert " + text.TrimEnd()),
                AddText = text => Log.Add("add " + text.TrimEnd()),
            };
        }

        public string[] Take()
        {
            string[] taken = Log.ToArray();
            Log.Clear();
            return taken;
        }
    }

    [Fact]
    public void InTheGame_AKeyIsOfferedToTheClientProgram_ThenRunsItsBind()
    {
        Keyboard k = new();
        k.Binds.SetBinding('w', 0, "+forward");
        k.Binds.SetBinding('g', 0, "say hi");

        k.Keys.Event('w', 'w', true);
        k.Keys.Event('w', 'w', true);                                             // key repeat: offered again, bound once
        k.Keys.Event('w', 0, false);
        Assert.Equal(new[] { "csqc 0 119 119", "insert +forward 119", "csqc 0 119 119", "csqc 1 119 119", "add -forward 119" }, k.Take());

        k.Keys.Event('g', 'g', true);
        k.Keys.Event('g', 0, false);
        Assert.Equal(new[] { "csqc 0 103 103", "insert say hi", "csqc 1 103 103" }, k.Take());

        k.GameConsumes = true;                                                    // "only send the bind if the event hasnt been already processed by csqc"
        k.Keys.Event('w', 'w', true);
        k.Keys.Event('w', 0, false);
        Assert.Equal(new[] { "csqc 0 119 119", "csqc 1 119 119" }, k.Take());
    }

    [Fact]
    public void Escape_IsNeverABind_AndAReleaseGoesWhereThePressWent()
    {
        Keyboard k = new();
        k.Binds.SetBinding(27, 0, "quit");                                        // ignored: escape is the engine's
        k.Keys.Event(27, 0, true);
        k.Keys.Event(27, 0, true);                                                // "ignore key repeats on escape"
        Assert.Equal(new[] { "csqc 0 27 0", "togglemenu 1" }, k.Take());
        k.Dest = MenuKeyDest.Menu;                                                // the menu opened on the press ...
        k.Keys.Event(27, 0, false);
        Assert.Equal(new[] { "csqc 1 27 0" }, k.Take());                          // ... and the release is still the game's

        k.Keys.Event(27, 0, true);                                                // in the menu: the menu's key
        k.Keys.Event(27, 0, false);
        Assert.Equal(new[] { "menu 27 0 down", "menu 27 0 up" }, k.Take());

        // The click that closes the menu: its release must not arrive in the game as a button going up.
        k.Binds.SetBinding(512, 0, "+fire");
        k.Keys.Event(512, 0, true);
        k.Dest = MenuKeyDest.Game;
        k.Keys.Event(512, 0, false);
        Assert.Equal(new[] { "menu 512 0 down", "menu 512 0 up" }, k.Take());

        // Shift-escape is the console's, in every mode, and its release goes nowhere.
        k.Keys.Event(134, 0, true);
        k.Take();
        k.Keys.Event(27, 0, true);
        k.Keys.Event(27, 0, false);
        Assert.Equal(new[] { "toggleconsole" }, k.Take());
    }

    [Fact]
    public void TheMenu_GetsTypedCharacters_FunctionKeysRunBinds_UnlessTheMenuGrabbedTheKeyboard()
    {
        Keyboard k = new() { Dest = MenuKeyDest.Menu };
        k.Binds.SetBinding(135, 0, "vote yes");                                   // F1
        k.Binds.SetBinding('x', 0, "+attack");

        k.Keys.Event('x', 'X', true);                                             // shift held: the key is x, the character X
        k.Keys.Event('x', 0, false);
        Assert.Equal(new[] { "menu 120 88 down", "menu 120 88 up" }, k.Take());   // no bind in the menu; the up carries the down's character

        k.Keys.Event(135, 0, true);                                               // "send function keydowns to interpreter no matter what mode is"
        k.Keys.Event(135, 0, false);
        Assert.Equal(new[] { "insert vote yes" }, k.Take());

        k.Dest = MenuKeyDest.MenuGrabbed;                                         // "press the key to bind": the menu must see F1
        k.Keys.Event(135, 0, true);
        k.Keys.Event(135, 0, false);
        Assert.Equal(new[] { "menu 135 0 down", "menu 135 0 up" }, k.Take());

        k.Console = true;                                                         // the console is down: keys are its
        k.Keys.Event('q', 'q', true);
        k.Keys.Event('q', 0, false);
        Assert.Equal(new[] { "console 113 113" }, k.Take());
        k.Console = false;

        // Key_ReleaseAll: everything held is released, each where it went down.
        k.Dest = MenuKeyDest.Game;
        k.Keys.Event('x', 'x', true);
        k.Take();
        k.Keys.ReleaseAll();
        Assert.Equal(new[] { "csqc 1 120 120", "add -attack 120" }, k.Take());
        Assert.Equal(0, k.Keys.Down('x'));
        k.Keys.Event(MenuKeyBindings.MaxKeys, 0, true);                           // out of range: nothing
        k.Keys.Event(-5, 0, true);
        Assert.Empty(k.Take());
    }
}
