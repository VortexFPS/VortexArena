using System;
using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the in-game console's Godot-free core: the interpreter's unknown-command routing hook
/// (<see cref="ConfigInterpreter.UnknownCommandHandler"/> / <see cref="ConfigInterpreter.CvarArchiveHook"/>),
/// the console/cvar builtins + routing (<see cref="ConsoleCommands"/>), Tab completion, and the
/// <see cref="BindTable"/> +/- button system. The Godot overlay + input glue are verified manually (windowed).
/// </summary>
public class ConsoleTests
{
    /// <summary>An interpreter + fresh cvar store + capturing print + a wired <see cref="ConsoleCommands"/>.</summary>
    private static (ConfigInterpreter interp, CvarService cvars, List<string> output) Make(
        Func<string, string?>? localRouter = null, Action<string>? remote = null)
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        BindTable.Reset();
        _ = new ConsoleCommands(interp, cvars, output.Add, clear: output.Clear, localRouter, remote);
        return (interp, cvars, output);
    }

    // ---- unknown-command routing (the gameplay-command surface) -------------------------------------------

    [Fact]
    public void UnknownHandler_FiresForSingleBareToken()
    {
        string? captured = null;
        var (interp, _, _) = Make(localRouter: line => { captured = line; return ""; });
        interp.ExecuteLine("kill");
        Assert.Equal("kill", captured);
    }

    [Fact]
    public void UnknownHandler_FiresForDenylistedMultiToken()
    {
        // "say" is on the interpreter's NonCvarCommands denylist, so "say hello world" must route as a whole line
        // (the case a bare 2-token line like "sv_gravity 800" must NOT take — that's a cvar assignment).
        string? captured = null;
        var (interp, _, _) = Make(localRouter: line => { captured = line; return ""; });
        interp.ExecuteLine("say hello world");
        Assert.Equal("say hello world", captured);
    }

    [Fact]
    public void BareCvarAssignment_IsNotRouted()
    {
        bool routed = false;
        var (interp, cvars, _) = Make(localRouter: _ => { routed = true; return ""; });
        interp.ExecuteLine("sv_gravity 800");
        Assert.False(routed);
        Assert.Equal("800", cvars.GetString("sv_gravity"));
    }

    [Fact]
    public void VidVsync_ConsoleChain_SetsFiresChanged_AndVidRestartDispatches()
    {
        // The 2026-07-11 "couldn't put on vid_vsync mid game" report: pins the full console chain —
        // bare `vid_vsync 1` is a cvar ASSIGNMENT (DP console semantics, not a query), it fires Changed
        // (ClientSettings.InstallLiveVideoCvars live-applies vsync off that event, DP parity — no
        // vid_restart needed), and a typed `vid_restart` reaches the registered host command.
        var (interp, cvars, _) = Make();
        cvars.Register("vid_vsync", "0");
        bool restarted = false;
        interp.RegisterCommand("vid_restart", _ => restarted = true); // mirrors ConsoleOverlay.RegisterHostCommands
        var changed = new List<string>();
        cvars.Changed += changed.Add;

        interp.ExecuteLine("vid_vsync 1");
        interp.ExecuteLine("vid_restart");

        Assert.Equal("1", cvars.GetString("vid_vsync"));
        Assert.Contains("vid_vsync", changed);
        Assert.True(restarted);
    }

    [Fact]
    public void NullHandler_StillCountsUnknown()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);   // no ConsoleCommands → hook stays null (cfg-load behaviour)
        interp.ExecuteLine("kill");
        Assert.Equal(1, interp.UnknownCommands);
    }

    [Fact]
    public void Unknown_FallsBackToRemote_WhenNoLocalWorld()
    {
        string? sent = null;
        var (interp, _, _) = Make(localRouter: _ => null, remote: line => sent = line); // null = no local world
        interp.ExecuteLine("kill");
        Assert.Equal("kill", sent);
    }

    [Fact]
    public void BareCvarName_PrintsValue_AndIsNotRouted()
    {
        // DP: a lone cvar name typed at the console prints its value and is NOT forwarded to the game.
        bool routed = false;
        var (interp, cvars, output) = Make(localRouter: _ => { routed = true; return ""; });
        cvars.Set("g_balance_blaster_primary_radius", "60");
        interp.ExecuteLine("g_balance_blaster_primary_radius");
        Assert.False(routed);
        Assert.Contains(output, l => l.Contains("g_balance_blaster_primary_radius") && l.Contains("60"));
    }

    [Fact]
    public void BareUnknownName_StillRoutes_NotTreatedAsCvar()
    {
        // A single token that is NOT a known cvar must still route to the game (the `kill` path), unchanged.
        string? captured = null;
        var (interp, _, _) = Make(localRouter: line => { captured = line; return ""; });
        interp.ExecuteLine("not_a_cvar_xyz");
        Assert.Equal("not_a_cvar_xyz", captured);
    }

    // ---- cvar / console builtins -------------------------------------------------------------------------

    [Fact]
    public void RegisteredCommand_WinsOverCvarAssignment()
    {
        var (interp, cvars, _) = Make();
        cvars.Set("foo", "0");
        interp.ExecuteLine("toggle foo");
        Assert.Equal("1", cvars.GetString("foo"));
        interp.ExecuteLine("toggle foo");
        Assert.Equal("0", cvars.GetString("foo"));
    }

    [Fact]
    public void Toggle_CyclesThroughValues()
    {
        var (interp, cvars, _) = Make();
        cvars.Set("g_mode", "a");
        interp.ExecuteLine("toggle g_mode a b c");
        Assert.Equal("b", cvars.GetString("g_mode"));
        interp.ExecuteLine("toggle g_mode a b c");
        Assert.Equal("c", cvars.GetString("g_mode"));
        interp.ExecuteLine("toggle g_mode a b c");
        Assert.Equal("a", cvars.GetString("g_mode")); // wraps
    }

    [Fact]
    public void IncDec_StepCvar()
    {
        var (interp, cvars, _) = Make();
        cvars.Set("x", "5");
        interp.ExecuteLine("inc x");
        Assert.Equal(6f, cvars.GetFloat("x"));
        interp.ExecuteLine("dec x 2");
        Assert.Equal(4f, cvars.GetFloat("x"));
    }

    [Fact]
    public void Echo_JoinsArgs()
    {
        var (interp, _, output) = Make();
        interp.ExecuteLine("echo hello world");
        Assert.Contains("hello world", output);
    }

    [Fact]
    public void Cvar_Get_PrintsNameAndValue()
    {
        var (interp, cvars, output) = Make();
        cvars.Set("hostname", "Test Server");
        interp.ExecuteLine("cvar hostname");
        Assert.Contains(output, s => s.Contains("hostname") && s.Contains("Test Server"));
    }

    [Fact]
    public void CvarList_Filters()
    {
        var (interp, cvars, output) = Make();
        cvars.Set("sv_gravity", "800");
        cvars.Set("cl_foo", "1");
        interp.ExecuteLine("cvarlist sv_");
        Assert.Contains(output, s => s.StartsWith("sv_gravity"));
        Assert.DoesNotContain(output, s => s.StartsWith("cl_foo"));
    }

    [Fact]
    public void Search_IsAnAliasForApropos()
    {
        var (interp, cvars, output) = Make();
        cvars.Set("sv_gravity", "800");
        interp.ExecuteLine("search gravity");
        // same output shape as apropos: a "cvar  <name> ..." line for the matching cvar.
        Assert.Contains(output, s => s.Contains("cvar") && s.Contains("sv_gravity"));
    }

    [Fact]
    public void Search_EchoesItsOwnNameInUsage()
    {
        var (interp, _, output) = Make();
        interp.ExecuteLine("search"); // no argument → usage line, naming the invoked command
        Assert.Contains(output, s => s.Contains("usage: search"));
    }

    [Fact]
    public void CvarOrphans_ListsReadButNeverRegisteredOrSet()
    {
        var (interp, cvars, output) = Make();
        cvars.GetFloat("nonexistent_cvar");   // a read of an absent cvar → recorded as an orphan
        cvars.Set("real_cvar", "1");
        _ = cvars.GetFloat("real_cvar");      // present → never an orphan
        output.Clear();
        interp.ExecuteLine("cvar_orphans");
        Assert.Contains(output, s => s == "nonexistent_cvar");
        Assert.DoesNotContain(output, s => s == "real_cvar");
    }

    [Fact]
    public void CvarOrphans_DropsNamesOnceTheyAreSet()
    {
        var (interp, cvars, output) = Make();
        _ = cvars.GetString("late_cvar");     // read while absent → orphan
        cvars.Set("late_cvar", "x");          // now defined → no longer an orphan
        output.Clear();
        interp.ExecuteLine("cvar_orphans");
        Assert.DoesNotContain(output, s => s == "late_cvar");
    }

    [Fact]
    public void Seta_MarksCvarArchived()
    {
        var (interp, cvars, _) = Make();
        interp.ExecuteLine("seta my_pref 3");
        Assert.Equal("3", cvars.GetString("my_pref"));
        Assert.True(cvars.IsArchived("my_pref")); // CvarArchiveHook → MarkArchived (DP CVAR_SAVE)
    }

    [Fact]
    public void PlainSet_DoesNotArchive()
    {
        var (interp, cvars, _) = Make();
        interp.ExecuteLine("set my_pref 3");
        Assert.Equal("3", cvars.GetString("my_pref"));
        Assert.False(cvars.IsArchived("my_pref"));
    }

    // ---- binds (DP bind/unbind over BindTable + the +/- button system) ----------------------------------

    [Fact]
    public void Bind_Unbind_Roundtrip()
    {
        var (interp, _, _) = Make();
        interp.ExecuteLine("bind x \"+forward\"");
        Assert.Equal("+forward", BindTable.Get("x"));
        interp.ExecuteLine("unbind x");
        Assert.Equal("", BindTable.Get("x"));
    }

    [Fact]
    public void Bind_IsCaseInsensitive()
    {
        BindTable.Reset();
        BindTable.Bind("w", "+forward");          // typed lowercase
        Assert.Equal("+forward", BindTable.Get("W")); // matches the encoder's "W"
    }

    [Fact]
    public void PlusForward_DrivesForwardAxis()
    {
        BindTable.Reset();
        BindTable.Bind("W", "+forward");
        BindTable.HandleBind("W", pressed: true, _ => { });
        Assert.Equal(1f, BindTable.Forward);
        BindTable.HandleBind("W", pressed: false, _ => { });
        Assert.Equal(0f, BindTable.Forward);
    }

    [Fact]
    public void OpposedStrafe_Cancels()
    {
        BindTable.Reset();
        BindTable.Bind("A", "+moveleft");
        BindTable.Bind("D", "+moveright");
        BindTable.HandleBind("A", true, _ => { });
        Assert.Equal(-1f, BindTable.Side);
        BindTable.HandleBind("D", true, _ => { });
        Assert.Equal(0f, BindTable.Side);
    }

    [Fact]
    public void Jump_SetsUpAxisAndButton()
    {
        BindTable.Reset();
        BindTable.Bind("Space", "+jump");
        BindTable.HandleBind("Space", true, _ => { });
        Assert.Equal(1f, BindTable.Up);
        Assert.True(BindTable.JumpHeld);
    }

    [Fact]
    public void OneShotBind_RunsOnPressOnly()
    {
        BindTable.Reset();
        BindTable.Bind("K", "kill");
        int runs = 0;
        string? cmd = null;
        BindTable.HandleBind("K", true, c => { runs++; cmd = c; });
        BindTable.HandleBind("K", false, _ => runs++); // release must NOT re-run a one-shot
        Assert.Equal(1, runs);
        Assert.Equal("kill", cmd);
    }

    [Fact]
    public void ReleaseAll_ClearsHeldButtons()
    {
        BindTable.Reset();
        BindTable.Bind("W", "+forward");
        BindTable.HandleBind("W", true, _ => { });
        Assert.Equal(1f, BindTable.Forward);
        BindTable.ReleaseAll();
        Assert.Equal(0f, BindTable.Forward);
    }

    // ---- Tab completion ----------------------------------------------------------------------------------

    [Fact]
    public void Complete_SingleMatch_ReturnsIt()
    {
        CompletionResult r = ConsoleCommands.Complete("toggl", new[] { "toggle", "cvar", "cvarlist" });
        Assert.Single(r.Matches);
        Assert.Equal("toggle", r.Completed);
    }

    [Fact]
    public void Complete_MultipleMatches_ReturnsCommonPrefix()
    {
        CompletionResult r = ConsoleCommands.Complete("cvar", new[] { "cvar", "cvarlist", "cvar_foo", "toggle" });
        Assert.Equal(3, r.Matches.Count);
        Assert.Equal("cvar", r.Completed);
    }

    [Fact]
    public void Complete_NoMatch_LeavesPrefix()
    {
        CompletionResult r = ConsoleCommands.Complete("zzz", new[] { "cvar", "toggle" });
        Assert.Empty(r.Matches);
        Assert.Equal("zzz", r.Completed);
    }

    [Fact]
    public void CompletionNames_IncludesCommandsAndCvars()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var cmds = new ConsoleCommands(interp, cvars, _ => { });
        cvars.Set("sv_gravity", "800");
        List<string> names = cmds.CompletionNames().ToList();
        Assert.Contains("toggle", names);     // a registered command
        Assert.Contains("set", names);        // an interpreter builtin
        Assert.Contains("sv_gravity", names); // a cvar
    }

    // ---- log → console colour bridge --------------------------------------------------------------------

    [Fact]
    public void ToBBCode_ConvertsQuakeColorCode()
    {
        string bb = Log.ToBBCode(LogLevel.Info, "^1red");
        Assert.Contains("#ff0000", bb); // ^1 → red, the colour the console renders server output with
    }
}
