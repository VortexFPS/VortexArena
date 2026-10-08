using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VortexArena.Legacy.Menu;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The menu program host (Menu/MenuHost.cs, port of menu.c MP_* and mvm_cmds.c): its builtin table
/// against the C source it was ported from, its entry points, what a fault does, and the menu-only
/// builtins, each exercised through a small assembled program that calls it by its menu number.
/// </summary>
public class MenuHostTests
{
    // ---- the table --------------------------------------------------------------------------------

    /// <summary>vm_m_builtins[] parsed out of mvm_cmds.c: the number of every non-NULL entry outside "#if 0".</summary>
    private static List<(int Number, string Function)> ParseC(string source)
    {
        int start = source.IndexOf("prvm_builtin_t vm_m_builtins[] = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "vm_m_builtins[] not found in mvm_cmds.c");
        int end = source.IndexOf("\n};", start, StringComparison.Ordinal);
        List<(int, string)> entries = new();
        int number = -1;
        bool skipping = false;
        foreach (string raw in source[(source.IndexOf('\n', start) + 1)..end].Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#if 0", StringComparison.Ordinal)) { skipping = true; continue; }
            if (line.StartsWith("#else", StringComparison.Ordinal)) { skipping = false; continue; }
            if (line.StartsWith("#endif", StringComparison.Ordinal)) continue;
            if (skipping || line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            Match entry = Regex.Match(line, @"^(NULL(?:/\*.*?\*/)?|VM_\w+)\s*,?");
            Assert.True(entry.Success, "unparsed line of vm_m_builtins[]: " + line);
            number++;
            if (!entry.Groups[1].Value.StartsWith("NULL", StringComparison.Ordinal)) entries.Add((number, entry.Groups[1].Value));
        }
        return entries;
    }

    [Fact]
    public void Table_IsTheOneInMvmCmds()
    {
        string source = Path.Combine(TestPaths.BaseData, "..", "darkplaces", "mvm_cmds.c");
        if (!File.Exists(source)) return;
        List<(int Number, string Function)> c = ParseC(File.ReadAllText(source));
        Assert.Equal(228, c.Count);
        Assert.Equal(c, MenuBuiltinTable.Entries.ToList());
        // The comments in the C number the entries too; three spot checks that the count did not drift.
        Assert.Contains((17, "VM_ftos"), c);
        Assert.Contains((456, "VM_drawpic"), c);
        Assert.Contains((643, "VM_M_crypto_getidstatus"), c);
    }

    [Fact]
    public void Table_IsNotTheClientTable_AndEveryEntryHasAnImplementation()
    {
        Assert.Equal(MenuBuiltinTable.Entries.Length, MenuBuiltinTable.Entries.Select(e => e.Number).Distinct().Count());
        Assert.True(MenuBuiltinTable.Entries.Zip(MenuBuiltinTable.Entries.Skip(1)).All(pair => pair.First.Number < pair.Second.Number));
        // The same C function under another number than the client program's table gives it.
        Assert.Contains((17, "VM_ftos"), MenuBuiltinTable.Entries);       // #26 for a client program
        Assert.Contains((4, "VM_print"), MenuBuiltinTable.Entries);       // #4 is setsize there
        Assert.Contains((456, "VM_drawpic"), MenuBuiltinTable.Entries);   // #322 there

        using MenuRig rig = new();
        MenuHost host = rig.Load(new MenuAsm());
        Assert.Empty(host.MissingBuiltins);
        foreach ((int number, string _) in MenuBuiltinTable.Entries) Assert.True(host.Vm.HasBuiltin(number), "builtin #" + number);
        Assert.Equal("strings", host.BuiltinSources[17]);
        Assert.Equal("core", host.BuiltinSources[14]);
        Assert.Equal("draw", host.BuiltinSources[456]);
        Assert.Equal("menu", host.BuiltinSources[601]);
        // copyentity is the menu table's own function, not the client's VM_CL_copyentity the core class has.
        Assert.Equal("menu", host.BuiltinSources[47]);
        Assert.False(host.Vm.HasBuiltin(63));    // "NULL, // #63 FIXME"
        Assert.False(host.Vm.HasBuiltin(300));   // the scene builtins are inside "#if 0"
    }

    // ---- loading and entry points -----------------------------------------------------------------

    [Fact]
    public void Load_RefusesAProgramWithoutARequiredFunction()
    {
        using MenuRig rig = new();
        VortexArena.Tests.QuakeC.ProgsBuilder b = new();
        b.Function("m_init");
        b.Emit(QcOp.Done);
        MenuLoadException e = Assert.Throws<MenuLoadException>(() => new MenuHost(b.Build(), rig.Console, rig.Draw));
        Assert.Contains("m_keydown not found", e.Message);
        Assert.Throws<MenuLoadException>(() => new MenuHost(new byte[10], rig.Console, rig.Draw));
    }

    [Fact]
    public void EntryPoints_PassDarkPlacesArguments()
    {
        using MenuRig rig = new();
        MenuAsm a = new();
        int inits = a.F("inits"), width = a.F("width"), height = a.F("height"), key = a.F("key"), ascii = a.F("ascii"), upKey = a.F("upkey");
        int mode = a.F("mode"), maps = a.F("maps"), shutdowns = a.F("shutdowns"), one = a.Const(1), command = a.S("command");
        a.Begin("m_init"); a.B.Emit(QcOp.AddF, inits, one, inits); a.End();
        a.Begin("m_draw"); a.Parm(0, width); a.Parm(1, height); a.End();
        a.Begin("m_keydown"); a.Parm(0, key); a.Parm(1, ascii); a.End();
        a.Begin("m_keyup"); a.Parm(0, upKey); a.End();
        a.Begin("m_toggle"); a.Parm(0, mode); a.End();
        a.Begin("m_newmap"); a.B.Emit(QcOp.AddF, maps, one, maps); a.End();
        a.Begin("m_shutdown"); a.B.Emit(QcOp.AddF, shutdowns, one, shutdowns); a.End();
        // GameCommand keeps its text: a temp string would not outlive the call, so it is zoned (#56 strzone).
        a.Begin("GameCommand"); a.Get(56, command, ProgsFile.OfsParm0); a.End();
        MenuHost host = rig.Load(a);

        Assert.True(host.Initialized);
        Assert.Equal(1, rig.Float("inits"));
        Assert.True(host.ClientMouse);                      // "in_client_mouse = true" before m_init
        Assert.Equal(MenuKeyDest.Game, host.KeyDest);

        Assert.True(host.DrawFrame(1280, 720));
        Assert.Equal(1280, rig.Float("width"));
        Assert.Equal(720, rig.Float("height"));

        host.KeyEvent(13, 0, true);
        host.KeyEvent(97, 'a', true);
        host.KeyEvent(97, 0, false);
        Assert.Equal(97, rig.Float("key"));
        Assert.Equal('a', rig.Float("ascii"));
        Assert.Equal(97, rig.Float("upkey"));

        host.ToggleMenu(-1);
        Assert.Equal(-1, rig.Float("mode"));
        host.NewMap();
        Assert.Equal(1, rig.Float("maps"));

        // The console commands that lead into the program.
        rig.Console.AddText("menu_cmd directmenu \"Game Menu\" x\ntogglemenu 0\ntogglemenu\n");
        rig.Console.Execute(0);
        Assert.Equal("directmenu \"Game Menu\" x", rig.String("command"));
        Assert.Equal(-1, rig.Float("mode"));                // "togglemenu" with no argument is -1

        host.Shutdown();
        Assert.Equal(MenuKeyDest.Game, host.KeyDest);
        Assert.False(host.DrawFrame(1280, 720));            // unloaded: nothing runs
    }

    [Fact]
    public void AFault_IsRecorded_StopsTheProgram_AndGivesTheKeysBack()
    {
        using MenuRig rig = new();
        MenuAsm a = new();
        int two = a.Const(2), bad = a.Const(7);
        a.Begin("m_init"); a.Call(601, two); a.End();        // setkeydest(KEY_MENU)
        a.Begin("m_draw"); a.Call(601, bad); a.End();        // "wrong destination 7"
        MenuHost host = rig.Load(a);
        List<MenuFault> seen = new();
        host.Faulted += seen.Add;
        Assert.Equal(MenuKeyDest.Menu, host.KeyDest);

        Assert.False(host.DrawFrame(640, 480));
        Assert.True(host.Failed);
        Assert.Equal(1, host.FaultCount);
        Assert.Equal("m_draw", Assert.Single(seen).EntryPoint);
        Assert.Contains("VM_M_setkeydest: wrong destination", host.Faults[0].Message);
        Assert.Equal(MenuKeyDest.Game, host.KeyDest);         // MVM_error_cmd: "key_dest = key_game"
        Assert.Contains("Menu_Error:", rig.Printed.ToString());
        Assert.False(host.DrawFrame(640, 480));               // mp_failed: not run again
        Assert.Equal(1, host.FaultCount);
    }

    [Fact]
    public void ASessionMayOnlyAskTheMenuToShowDialogs()
    {
        using MenuRig rig = new();
        MenuAsm a = new();
        int calls = a.F("calls"), one = a.Const(1);
        a.Begin("GameCommand"); a.B.Emit(QcOp.AddF, calls, one, calls); a.End();
        rig.Load(a);

        rig.Console.BeginSession();
        rig.Console.EnterSession();
        rig.Console.ExecuteNow("menu_cmd directmenu Welcome HOSTNAME x");
        rig.Console.ExecuteNow("menu_cmd closemenu Welcome");
        rig.Console.ExecuteNow("menu_cmd sync");
        Assert.Equal(3, rig.Float("calls"));
        // Xonotic's GameCommand runs any console command a frame later for "nextframe": not for a server.
        rig.Console.ExecuteNow("menu_cmd nextframe quit");
        rig.Console.ExecuteNow("menu_cmd rpn /x 1 def");
        rig.Console.ExecuteNow("menu_cmd");
        Assert.Equal(3, rig.Float("calls"));
        Assert.Contains("not followed", rig.Printed.ToString());
        rig.Console.LeaveSession();
        rig.Console.ExecuteNow("menu_cmd nextframe quit");   // the player's own: passed on
        Assert.Equal(4, rig.Float("calls"));
    }

    // ---- menu-only builtins -----------------------------------------------------------------------

    [Fact]
    public void KeyDestAndMouseTarget_RoundTrip_AndTheMousePositionIsScaledToTheVirtualScreen()
    {
        using MenuRig rig = new();
        rig.Console.Cvars.Set("vid_conwidth", "800");
        rig.Console.Cvars.Set("vid_conheight", "600");
        MenuAsm a = new();
        int dest = a.F("dest"), target = a.F("target"), pos = a.V("pos"), hidden = a.V("hidden", 9, 9, 9), want = a.F("want"), mouse = a.F("mouse");
        a.Begin("m_draw");
        a.GetV(66, hidden);                 // getmousepos while key_dest is the game: '0 0 0'
        a.Call(601, want);                  // setkeydest
        a.Call(603, mouse);                 // setmousetarget
        a.Get(602, dest);                   // getkeydest
        a.Get(604, target);                 // getmousetarget
        a.GetV(66, pos);
        a.End();
        (float X, float Y) pointer = (640, 360), delta = (5, -3);
        bool console = false;
        MenuHost host = rig.Load(a, new MenuHostOptions
        {
            VideoSize = () => (1280, 720), WindowMouse = () => pointer, MouseDelta = () => delta, ConsoleActive = () => console,
        });
        void Run(float keyDest, float mouseTarget)
        {
            host.KeyDest = MenuKeyDest.Game;
            host.Vm.GlobalFloat(want) = keyDest;
            host.Vm.GlobalFloat(mouse) = mouseTarget;
            Assert.True(host.DrawFrame(1280, 720));
        }

        Run(2, 2);                           // KEY_MENU, MT_CLIENT: the window's pointer
        Assert.Equal(default, rig.Vector("hidden"));
        Assert.Equal(2, rig.Float("dest"));
        Assert.Equal(2, rig.Float("target"));
        Assert.True(host.ClientMouse);
        Assert.Equal(new QcVector(400, 300, 0), rig.Vector("pos"));   // 640 * 800 / 1280, 360 * 600 / 720

        Run(3, 1);                           // KEY_MENU_GRABBED, MT_MENU: movement, for a menu-drawn cursor
        Assert.Equal(3, rig.Float("dest"));
        Assert.Equal(1, rig.Float("target"));
        Assert.Equal(MenuKeyDest.MenuGrabbed, host.KeyDest);
        Assert.False(host.ClientMouse);
        Assert.Equal(new QcVector(5 * 800f / 1280, -3 * 600f / 720, 0), rig.Vector("pos"));

        console = true;                      // the console is down: no mouse for the menu
        Run(2, 2);
        Assert.Equal(default, rig.Vector("pos"));
    }

    [Fact]
    public void KeyBindBuiltins_EditTheConsolesBindTable()
    {
        using MenuRig rig = new();
        rig.Console.Keys.SetBinding(119, 0, "+forward");
        rig.Console.Keys.SetBinding(128, 0, "+forward");
        MenuAsm a = new();
        int found = a.S("found"), bound = a.S("bound"), unbound = a.F("unbound", 5), ok = a.F("ok"), name = a.S("name"), number = a.F("number"), maps = a.V("maps");
        int forward = a.Text("+forward"), jump = a.Text("+jump"), k119 = a.Const(119), k32 = a.Const(32), k130 = a.Const(130), uparrow = a.Text("UPARROW");
        a.Begin("m_draw");
        a.Get(610, found, forward);            // findkeysforcommand
        a.Call(56, found); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, found);   // strzone, to read it after the call
        a.Get(630, ok, k32, jump);             // setkeybind
        a.Get(342, bound, k119);               // getkeybind
        a.Call(56, bound); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, bound);
        a.Get(342, unbound, k130);             // an unbound key: the null string
        a.Get(609, name, k130);                // keynumtostring
        a.Call(56, name); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, name);
        a.Get(614, number, uparrow);           // stringtokeynum
        a.GetV(631, maps);                     // getbindmaps
        a.End();
        MenuHost host = rig.Load(a);
        Assert.True(host.DrawFrame(640, 480));

        Assert.Equal(" '119' '128' '-1' '-1' '-1'", rig.String("found"));
        Assert.Equal(1, rig.Float("ok"));
        Assert.Equal("+jump", rig.Console.Keys.GetBind(32, 0));
        Assert.Equal("+forward", rig.String("bound"));
        Assert.Equal(0, rig.Handle("unbound"));
        Assert.Equal("LEFTARROW", rig.String("name"));
        Assert.Equal(128, rig.Float("number"));
        Assert.Equal(new QcVector(0, 1, 0), rig.Vector("maps"));
    }

    [Fact]
    public void AltStrings_Count_Get_Set_Insert()
    {
        using MenuRig rig = new();
        MenuAsm a = new();
        int list = a.Text("'one' 'it\\'s' 'three'"), count = a.F("count"), second = a.S("second"), past = a.F("past", 5), replaced = a.S("replaced");
        int inserted = a.S("inserted"), prepared = a.S("prepared"), c1 = a.Const(1), c0 = a.Const(0), c9 = a.Const(9), two = a.Text("TWO"), quote = a.Text("a'b");
        a.Begin("m_draw");
        a.Get(82, count, list);
        a.Get(84, second, list, c1);
        a.Call(56, second); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, second);
        a.Get(84, past, list, c9);
        a.Get(85, replaced, list, c1, two);
        a.Call(56, replaced); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, replaced);
        a.Get(86, inserted, list, c0, two);
        a.Call(56, inserted); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, inserted);
        a.Get(83, prepared, quote);
        a.Call(56, prepared); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, prepared);
        a.End();
        MenuHost host = rig.Load(a);
        Assert.True(host.DrawFrame(640, 480), host.Faults.Count > 0 ? host.Faults[0].Message : "");

        Assert.Equal(3, rig.Float("count"));
        Assert.Equal("it's", rig.String("second"));            // the escaped quote comes back plain
        Assert.Equal(0, rig.Handle("past"));                   // past the last item: the null string
        Assert.Equal("'one' 'TWO' 'three'", rig.String("replaced"));
        Assert.Equal("'one''TWO' 'it\\'s' 'three'", rig.String("inserted"));
        Assert.Equal("a\\'b", rig.String("prepared"));
    }

    [Fact]
    public void WhatThisHostDoesNotHave_IsAnsweredAsDarkPlacesAnswersWithoutIt()
    {
        using MenuRig rig = new();
        MenuAsm a = new();
        int address = a.Text("192.0.2.1:26000"), keyfp = a.F("keyfp", 5), idstatus = a.F("idstatus", 5), mykey = a.F("mykey", 5), mykeyOut = a.F("mykeyout", 5);
        int mystatus = a.F("mystatus", 5), cin = a.F("cin", 5), video = a.F("video", 5), state = a.F("state"), demo = a.F("demo", 5), server = a.F("server", 5);
        int uri = a.F("uri", 5), sound = a.F("sound", 5), resolved = a.S("resolved"), c0 = a.Const(0), c99 = a.Const(99), file = a.Text("video/intro.dpv");
        int url = a.Text("http://example.invalid/x"), sample = a.Text("misc/menu1.wav"), c26000 = a.Const(26000), host2 = a.Text("192.0.2.7");
        a.Begin("URI_Get_Callback"); a.End();
        a.Begin("m_draw");
        a.Get(633, keyfp, address);       // crypto_getkeyfp: no stored key for any server
        a.Get(643, idstatus, address);    // crypto_getidstatus: 0
        a.Get(636, mykey, c0);            // crypto_getmykeyfp(0): an empty slot, the empty string
        a.Get(636, mykeyOut, c99);        // past the last slot: the null string
        a.Get(641, mystatus, c0);         // crypto_getmyidstatus: 0, no ID there
        a.Get(461, cin, file, file);      // cin_open: no video
        a.Get(355, video);                // videoplaying
        a.Get(62, state);                 // clientstate: 1 disconnected
        a.Get(349, demo);                 // isdemo
        a.Get(60, server);                // isserver
        a.Get(513, uri, url, c0);         // uri_get: not started
        a.Get(65, sound, sample);         // localsound with no sound system: -4
        a.Get(625, resolved, host2, c26000);
        a.Call(56, resolved); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, resolved);
        a.End();
        MenuHost host = rig.Load(a);
        Assert.True(host.DrawFrame(640, 480), host.Faults.Count > 0 ? host.Faults[0].Message : "");

        Assert.Equal(0, rig.Handle("keyfp"));
        Assert.Equal(0, rig.Float("idstatus"));
        Assert.NotEqual(0, rig.Handle("mykey"));
        Assert.Equal("", rig.String("mykey"));
        Assert.Equal(0, rig.Handle("mykeyout"));
        Assert.Equal(0, rig.Float("mystatus"));
        Assert.Equal(0, rig.Float("cin"));
        Assert.Equal(0, rig.Float("video"));
        Assert.Equal(1, rig.Float("state"));
        Assert.Equal(0, rig.Float("demo"));
        Assert.Equal(0, rig.Float("server"));
        Assert.Equal(0, rig.Handle("uri"));
        Assert.Equal(-4, rig.Float("sound"));
        Assert.Equal("192.0.2.7:26000", rig.String("resolved"));
        Assert.False(new HashSet<string>(MenuBuiltinTable.Extensions).Contains("DP_CRYPTO"));
        Assert.Empty(host.UnimplementedBuiltins);
    }

    [Fact]
    public void ServerListBuiltins_ReadTheViewAndSetMasksAndSort()
    {
        using MenuRig rig = new();
        MenuAsm a = new();
        int count = a.F("count"), name = a.S("name"), ping = a.F("ping"), field = a.F("field"), badField = a.F("badfield");
        int key = a.Text("numhumans"), unknown = a.Text("nosuchkey"), c0 = a.Const(0), c1 = a.Const(1), fName = a.Const((int)HostCacheField.Name), fPing = a.Const((int)HostCacheField.Ping);
        int fHumans = a.Const((int)HostCacheField.NumHumans), opGreaterEqual = a.Const((int)HostCacheOp.GreaterEqual), descending = a.Const(MenuHostCache.SortDescending);
        a.Begin("m_draw");
        a.Call(615);                                         // resethostcachemasks
        a.Call(617, c0, fHumans, c1, opGreaterEqual);        // AND mask 0: numhumans >= 1
        a.Call(619, fPing, descending);                      // sort by ping, highest first
        a.Call(618);                                         // resorthostcache
        a.Get(611, count, c0);                               // servers in the view
        a.Get(612, name, fName, c0);
        a.Call(56, name); a.B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, name);
        a.Get(621, ping, fPing, c0);
        a.Get(622, field, key);
        a.Get(622, badField, unknown);
        a.End();
        MenuHost host = rig.Load(a);
        MenuHostCacheTests.Add(host.ServerList, "192.0.2.1:26000", "empty", 20, clients: 0);
        MenuHostCacheTests.Add(host.ServerList, "192.0.2.2:26000", "near", 30, clients: 2);
        MenuHostCacheTests.Add(host.ServerList, "192.0.2.3:26000", "far", 90, clients: 5);
        Assert.Equal(3, host.ServerList.ViewCount);
        Assert.True(host.DrawFrame(640, 480), host.Faults.Count > 0 ? host.Faults[0].Message : "");

        Assert.Equal(2, rig.Float("count"));                  // the empty server is masked out
        Assert.Equal("far", rig.String("name"));
        Assert.InRange(rig.Float("ping"), 85, 95);
        Assert.Equal((float)HostCacheField.NumHumans, rig.Float("field"));
        Assert.Equal(-1, rig.Float("badfield"));
    }
}
