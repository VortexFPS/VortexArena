using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Common.Config;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests for <see cref="CommandCompletion"/> (DP <c>Con_CompleteCommandLine</c>) and
/// <see cref="ConsoleHistory"/> (DP's <c>Key_History_*</c> family) — the two halves of the console's input
/// behaviour that are pure enough to verify headlessly. The Godot overlay that drives them is checked by hand.
/// </summary>
public class ConsoleCompletionTests
{
    private static readonly string[] Builtins = { "set", "seta", "exec", "alias" };

    private static (CommandCompletion completion, ConfigInterpreter interp, CvarService cvars) Make()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        interp.RegisterCommand("cvarlist", _ => { }, "list every cvar");
        interp.RegisterCommand("cvar_orphans", _ => { }, "list unregistered cvar reads");
        interp.RegisterCommand("cmdlist", _ => { }, "list every command");
        interp.RegisterCommand("bind", _ => { }, "bind a key");
        interp.RegisterCommand("map", _ => { }, "host a map");
        interp.RegisterCommand("toggle", _ => { }, "flip a cvar");

        cvars.Set("cl_bob", "0.01");
        cvars.SetDescription("cl_bob", "view bobbing height");
        cvars.Set("cl_bobcycle", "0.6");
        cvars.Set("cl_bobup", "0.5");
        cvars.Set("con_textsize", "10");
        cvars.Set("con_completion_exec", "*.cfg");

        interp.DefineAlias("cvar_changes", "cmd cvar_changes");

        var completion = new CommandCompletion(interp, cvars, Builtins, _ => "");
        return (completion, interp, cvars);
    }

    // ---- the generic path (commands / variables / aliases) --------------------------------------------------

    [Fact]
    public void Complete_UniqueMatch_CompletesAndAppendsSpace()
    {
        var (completion, _, _) = Make();
        CompletionOutcome r = completion.Complete("cmdli", 5);

        Assert.Equal("cmdlist ", r.Line);
        Assert.Equal(8, r.Caret);
        Assert.Empty(r.Output);          // a unique completion is silent, as in DP
    }

    [Fact]
    public void Complete_SeveralMatches_AdvancesToCommonPrefixAndLists()
    {
        var (completion, _, _) = Make();
        CompletionOutcome r = completion.Complete("cl_bob", 6);

        // cl_bob, cl_bobcycle, cl_bobup share exactly "cl_bob" — the line cannot advance, but the choices show.
        Assert.Equal("cl_bob", r.Line);
        Assert.Contains(r.Output, l => l.Contains("3") && l.Contains("possible variables"));
        Assert.Contains(r.Output, l => l.Contains("cl_bobcycle"));
    }

    [Fact]
    public void Complete_CommonPrefixSpansEveryKind()
    {
        var (completion, _, _) = Make();
        // Commands cvarlist + cvar_orphans and the alias cvar_changes all start "cvar"; the completion must be
        // the prefix common to ALL of them, not to one group.
        CompletionOutcome r = completion.Complete("cvar", 4);
        Assert.Equal("cvar", r.Line);
        Assert.Contains(r.Output, l => l.Contains("possible command"));
        Assert.Contains(r.Output, l => l.Contains("possible alias"));
    }

    [Fact]
    public void Complete_PrintsDescriptionsForCommandsAndCvars()
    {
        var (completion, _, _) = Make();
        CompletionOutcome r = completion.Complete("c", 1);

        Assert.Contains(r.Output, l => l.Contains("list every cvar"));       // command help
        Assert.Contains(r.Output, l => l.Contains("view bobbing height"));   // cvar help
    }

    [Fact]
    public void Complete_ManyMatches_FallsBackToPackedColumns()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        for (int i = 0; i < 40; i++)
        {
            cvars.Set($"g_thing{i:00}", "0");
            cvars.SetDescription($"g_thing{i:00}", "a very long description that would flood the scrollback");
        }
        var completion = new CommandCompletion(interp, cvars, Array.Empty<string>(), _ => "") { LineWidth = 80 };

        CompletionOutcome r = completion.Complete("g_", 2);

        // Past the threshold, names only in columns — no descriptions, and far fewer lines than matches.
        Assert.DoesNotContain(r.Output, l => l.Contains("would flood"));
        Assert.True(r.Output.Count < 40, $"expected packed columns, got {r.Output.Count} lines");
        Assert.Contains(r.Output, l => l.Contains("g_thing00") && l.Contains("g_thing01"));
    }

    [Fact]
    public void Complete_NoMatch_LeavesTheLineAlone()
    {
        var (completion, _, _) = Make();
        CompletionOutcome r = completion.Complete("zzz_nothing", 11);

        Assert.Equal("zzz_nothing", r.Line);
        Assert.Empty(r.Output);
    }

    [Fact]
    public void Complete_KeepsTextAfterTheCaret()
    {
        var (completion, _, _) = Make();
        //                     caret here ^
        CompletionOutcome r = completion.Complete("cmdli extra", 5);

        Assert.Equal("cmdlist  extra", r.Line);
        Assert.Equal(8, r.Caret);
    }

    [Fact]
    public void Complete_CompletesTheTokenUnderTheCaret_NotTheWholeLine()
    {
        var (completion, _, _) = Make();
        // `toggle` has no con_completion_* rule, so DP falls through to generic completion of the second token.
        CompletionOutcome r = completion.Complete("toggle con_texts", 16);
        Assert.Equal("toggle con_textsize ", r.Line);
    }

    // ---- argument completion (DP's con_completion_<command> block) ------------------------------------------

    [Fact]
    public void Complete_MapCommand_CompletesMapNames()
    {
        var (completion, _, _) = Make();
        completion.MapNames = () => new[] { "afterslime", "solarium", "stormkeep" };

        CompletionOutcome r = completion.Complete("map sol", 7);
        Assert.Equal("map solarium ", r.Line);
    }

    [Fact]
    public void Complete_MapCommand_ListsWhenAmbiguous()
    {
        var (completion, _, _) = Make();
        completion.MapNames = () => new[] { "solarium", "solarpower" };

        CompletionOutcome r = completion.Complete("map sol", 7);
        Assert.Equal("map solar", r.Line);
        Assert.Contains(r.Output, l => l.Contains("possible maps"));
    }

    [Fact]
    public void Complete_ExecCommand_UsesTheConCompletionPattern()
    {
        var (completion, cvars, _) = (Make().completion, Make().interp, Make().cvars);
        var searched = new List<string>();
        completion.FileSearch = glob =>
        {
            searched.Add(glob);
            return glob.EndsWith("/", StringComparison.Ordinal)
                ? Array.Empty<string>()
                : new[] { "autoexec.cfg", "binds-xonotic.cfg" };
        };

        CompletionOutcome r = completion.Complete("exec bin", 8);

        Assert.Contains("*.cfg", searched);          // the pattern came from con_completion_exec
        Assert.Equal("exec binds-xonotic.cfg ", r.Line);
    }

    [Fact]
    public void Complete_BindCommand_CompletesKeyNames()
    {
        var (completion, _, _) = Make();
        completion.KeyNames = () => new[] { "MOUSE1", "MOUSE2", "MWHEELUP", "Space" };

        CompletionOutcome r = completion.Complete("bind MW", 7);
        Assert.Equal("bind MWHEELUP ", r.Line);
    }

    [Fact]
    public void Complete_ArgumentRuleWithNoSource_FallsBackToNames()
    {
        // MapNames unwired (a headless console): `map sol` must not blow up, it just completes nothing.
        var (completion, _, _) = Make();
        CompletionOutcome r = completion.Complete("map sol", 7);
        Assert.Equal("map sol", r.Line);
    }

    // ---- nick completion (DP Nicks_Complete*) ---------------------------------------------------------------

    [Fact]
    public void Complete_CompletesPlayerNicks_WithoutTheirColourCodes()
    {
        var (completion, _, _) = Make();
        completion.NickNames = () => new[] { "^1Play^7er", "Spectator" };

        // The console argument wants the name the server matches on, not the one with the codes in it.
        CompletionOutcome r = completion.Complete("kick Play", 9);
        Assert.Equal("kick Player ", r.Line);
    }

    [Fact]
    public void Complete_ListsNicksAsTheirOwnGroup()
    {
        var (completion, _, _) = Make();
        completion.NickNames = () => new[] { "Player1", "Player2" };

        CompletionOutcome r = completion.Complete("kick Play", 9);
        Assert.Equal("kick Player", r.Line);
        Assert.Contains(r.Output, l => l.Contains("2") && l.Contains("possible nicks"));
    }

    [Fact]
    public void Complete_BareTabDoesNotDumpTheRoster()
    {
        // A nick has no prefix to anchor on, so an empty token must not pull every player into the command list.
        var (completion, _, _) = Make();
        completion.NickNames = () => new[] { "Player1", "Player2" };

        CompletionOutcome r = completion.Complete("", 0);
        Assert.DoesNotContain(r.Output, l => l.Contains("possible nick"));
    }

    // ---- Ctrl+Tab ------------------------------------------------------------------------------------------

    [Fact]
    public void AppendCvarValue_InsertsTheCurrentValue()
    {
        var (completion, _, _) = Make();
        CompletionOutcome r = completion.AppendCvarValue("cl_bob", 6);

        Assert.Equal("cl_bob 0.01", r.Line);
        Assert.Equal(11, r.Caret);
    }

    [Fact]
    public void AppendCvarValue_OnANonCvar_DoesNothing()
    {
        var (completion, _, _) = Make();
        CompletionOutcome r = completion.AppendCvarValue("cmdlist", 7);
        Assert.Equal("cmdlist", r.Line);
    }

    // ---- Con_DisplayList columns ---------------------------------------------------------------------------

    [Fact]
    public void Columns_PackNamesToTheGivenWidth()
    {
        List<string> rows = CommandCompletion.Columns(
            new[] { "aaa", "bbb", "ccc", "ddd", "eee" }, width: 22);

        // cell = 3 + 2 = 5; (22 - 2) / 5 = 4 per row.
        Assert.Equal(2, rows.Count);
        Assert.Equal("aaa  bbb  ccc  ddd", rows[0]);
        Assert.Equal("eee", rows[1]);
    }

    [Fact]
    public void Columns_NarrowConsoleStillEmitsOnePerRow()
    {
        List<string> rows = CommandCompletion.Columns(new[] { "averylongname", "another" }, width: 4);
        Assert.Equal(2, rows.Count);
    }

    // ---- history (DP Key_History_*) ------------------------------------------------------------------------

    private static ConsoleHistory Filled()
    {
        var h = new ConsoleHistory();
        h.Push("map solarium");
        h.Push("bind x kill");
        h.Push("cl_bob 0.02");
        h.Push("map stormkeep");
        return h;
    }

    [Fact]
    public void History_UpWalksBackwards_DownReturnsToTheTypedLine()
    {
        ConsoleHistory h = Filled();

        Assert.Equal("map stormkeep", h.Up("half typed"));
        Assert.Equal("cl_bob 0.02", h.Up("half typed"));
        Assert.Equal("bind x kill", h.Up("half typed"));

        Assert.Equal("cl_bob 0.02", h.Down());
        Assert.Equal("map stormkeep", h.Down());
        Assert.Equal("half typed", h.Down());     // the stashed in-progress line comes back
        Assert.Null(h.Down());                    // and stepping past it does nothing
    }

    [Fact]
    public void History_UpHoldsAtTheOldestLine()
    {
        ConsoleHistory h = Filled();
        for (int i = 0; i < 4; i++) h.Up("");
        Assert.Null(h.Up(""));                    // already at the oldest — DP holds rather than wrapping
    }

    [Fact]
    public void History_FirstAndLastJumpToTheEnds()
    {
        ConsoleHistory h = Filled();
        Assert.Equal("map solarium", h.First(""));
        Assert.Equal("map stormkeep", h.Last(""));
    }

    [Fact]
    public void History_SearchPointsWithoutFetching_AndUpThenFetchesIt()
    {
        ConsoleHistory h = Filled();

        // Ctrl+R: finds the newest "map" line and reports it, but does NOT put it in the edit line…
        var first = h.FindBackwards("map");
        Assert.Equal("map stormkeep", first!.Value.Line);

        // …pressing Ctrl+R again continues past it to the older one…
        var second = h.FindBackwards("map");
        Assert.Equal("map solarium", second!.Value.Line);

        // …and Up is what finally fetches the pointed-at line (DP Key_History_Get_foundCommand).
        Assert.Equal("map solarium", h.Up(""));
    }

    [Fact]
    public void History_SearchAcceptsWildcards()
    {
        ConsoleHistory h = Filled();
        Assert.Equal("bind x kill", h.FindBackwards("bind*kill")!.Value.Line);
    }

    [Fact]
    public void History_FindAll_ReturnsEveryMatchWithOneBasedIndices()
    {
        ConsoleHistory h = Filled();
        var all = h.FindAll("map");

        Assert.Equal(2, all.Count);
        Assert.Equal((1, "map solarium", false), all[0]);
        Assert.Equal((4, "map stormkeep", false), all[1]);
    }

    [Fact]
    public void History_NeverRecordsQuitOrRconPassword()
    {
        var h = new ConsoleHistory();
        h.Push("quit");
        h.Push("quit now");
        h.Push("rcon_password hunter2");
        h.Push("kill");

        Assert.Equal(new[] { "kill" }, h.Lines);
    }

    [Fact]
    public void History_CollapsesConsecutiveDuplicates()
    {
        var h = new ConsoleHistory();
        h.Push("kill");
        h.Push("kill");
        h.Push("say hi");
        h.Push("kill");

        Assert.Equal(new[] { "kill", "say hi", "kill" }, h.Lines);
    }

    [Fact]
    public void History_RoundTripsThroughSaveAndLoad()
    {
        ConsoleHistory saved = Filled();
        var loaded = new ConsoleHistory();
        loaded.Load(saved.Save());

        Assert.Equal(saved.Lines, loaded.Lines);
        Assert.False(loaded.IsNavigating);
    }

    [Fact]
    public void History_LoadDropsAnExcludedLineFromAnOlderFile()
    {
        var h = new ConsoleHistory();
        h.Load("kill\nrcon_password hunter2\nsay hi\n");
        Assert.Equal(new[] { "kill", "say hi" }, h.Lines);
    }

    [Fact]
    public void History_IsBounded()
    {
        var h = new ConsoleHistory();
        for (int i = 0; i < ConsoleHistory.MaxLines + 50; i++)
            h.Push($"echo {i}");

        Assert.Equal(ConsoleHistory.MaxLines, h.Count);
        Assert.Equal($"echo {ConsoleHistory.MaxLines + 49}", h.Lines[^1]);
    }

    [Fact]
    public void History_ClearForgetsEverything()
    {
        ConsoleHistory h = Filled();
        h.Clear();
        Assert.Equal(0, h.Count);
        Assert.Null(h.Up(""));
    }
}
