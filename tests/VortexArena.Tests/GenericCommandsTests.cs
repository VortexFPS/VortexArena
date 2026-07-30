using System;
using System.Collections.Generic;
using XonoticGodot.Common.Config;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the generic command family on the SHARED console surface (<see cref="ConsoleCommands"/>) — the
/// port of common/command/generic.qc (addtolist / removefromlist / maplist / settemp / settemp_restore /
/// nextframe) + the cons/word-list helpers (<see cref="WordList"/>). These are pure cvar/string ops, run
/// through the interpreter exactly as a cfg/quickmenu would issue them.
/// </summary>
public class GenericCommandsTests
{
    private static (ConfigInterpreter interp, CvarService cvars, List<string> output, ConsoleCommands cc) Make(
        Func<string, string?>? localRouter = null)
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        BindTable.Reset();
        var cc = new ConsoleCommands(interp, cvars, output.Add, clear: output.Clear, localRouter);
        return (interp, cvars, output, cc);
    }

    // ---- WordList.cons (the parity-critical "no leading space" semantics) ---------------------------------

    [Theory]
    [InlineData("", "x", "x")]      // cons("", x) == "x"  (NO leading space)
    [InlineData("a", "", "a")]      // cons(a, "") == "a"
    [InlineData("a", "b", "a b")]   // cons(a, b) == "a b"
    [InlineData("a b", "c", "a b c")]
    public void Cons_MatchesQc(string a, string b, string expected)
    {
        Assert.Equal(expected, WordList.Cons(a, b));
    }

    // ---- addtolist / removefromlist ----------------------------------------------------------------------

    [Fact]
    public void AddToList_OnEmpty_SetsValue()
    {
        var (interp, cvars, _, _) = Make();
        interp.ExecuteLine("addtolist g_list foo");
        Assert.Equal("foo", cvars.GetString("g_list"));
    }

    [Fact]
    public void AddToList_Appends_AndDedupes()
    {
        var (interp, cvars, _, _) = Make();
        interp.ExecuteLine("addtolist g_list foo");
        interp.ExecuteLine("addtolist g_list bar");
        Assert.Equal("foo bar", cvars.GetString("g_list")); // appended at the END
        interp.ExecuteLine("addtolist g_list foo");          // already present → unchanged
        Assert.Equal("foo bar", cvars.GetString("g_list"));
    }

    [Fact]
    public void RemoveFromList_DropsTheValue()
    {
        var (interp, cvars, _, _) = Make();
        cvars.Set("g_list", "foo bar baz");
        interp.ExecuteLine("removefromlist g_list bar");
        Assert.Equal("foo baz", cvars.GetString("g_list"));
    }

    // ---- maplist (add PREPENDS; remove; shuffle; cleanup) ------------------------------------------------

    [Fact]
    public void Maplist_Add_Prepends()
    {
        var (interp, cvars, _, _) = Make();
        cvars.Set("g_maplist", "dance");
        interp.ExecuteLine("maplist add boil");
        Assert.Equal("boil dance", cvars.GetString("g_maplist")); // PREPEND (unlike addtolist's append)
    }

    [Fact]
    public void Maplist_Add_OnEmpty_SetsIt()
    {
        var (interp, cvars, _, _) = Make();
        interp.ExecuteLine("maplist add boil");
        Assert.Equal("boil", cvars.GetString("g_maplist"));
    }

    [Fact]
    public void Maplist_Add_RejectsMissingMap_WhenCatalogWired()
    {
        var (interp, cvars, _, cc) = Make();
        cc.MapExists = m => m == "boil"; // only "boil" exists
        interp.ExecuteLine("maplist add nope");
        Assert.Equal("", cvars.GetString("g_maplist")); // not added
        interp.ExecuteLine("maplist add boil");
        Assert.Equal("boil", cvars.GetString("g_maplist"));
    }

    [Fact]
    public void Maplist_Remove_DropsTheMap()
    {
        var (interp, cvars, _, _) = Make();
        cvars.Set("g_maplist", "boil dance solarium");
        interp.ExecuteLine("maplist remove dance");
        Assert.Equal("boil solarium", cvars.GetString("g_maplist"));
    }

    [Fact]
    public void Maplist_Shuffle_IsAPermutation()
    {
        var (interp, cvars, _, cc) = Make();
        cc.ShuffleRng = new Random(12345); // deterministic
        cvars.Set("g_maplist", "a b c d e");
        interp.ExecuteLine("maplist shuffle");
        string shuffled = cvars.GetString("g_maplist");
        var words = WordList.Words(shuffled);
        words.Sort();
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, words); // same multiset, just reordered
        Assert.Equal(5, WordList.Words(shuffled).Count);
    }

    [Fact]
    public void Maplist_Cleanup_KeepsOnlyExistingMaps_WhenCatalogWired()
    {
        var (interp, cvars, _, cc) = Make();
        cc.MapExists = m => m != "gone"; // "gone" no longer exists
        cvars.Set("g_maplist", "boil gone dance");
        interp.ExecuteLine("maplist cleanup");
        Assert.Equal("boil dance", cvars.GetString("g_maplist"));
    }

    // ---- settemp / settemp_restore -----------------------------------------------------------------------

    [Fact]
    public void Settemp_OverridesThenRestores()
    {
        var (interp, cvars, _, _) = Make();
        cvars.Set("sv_gravity", "800");
        interp.ExecuteLine("settemp sv_gravity 500");
        Assert.Equal("500", cvars.GetString("sv_gravity"));
        interp.ExecuteLine("settemp_restore");
        Assert.Equal("800", cvars.GetString("sv_gravity"));
    }

    [Fact]
    public void Settemp_CapturesOriginalOnlyOnce()
    {
        var (interp, cvars, _, _) = Make();
        cvars.Set("x", "1");
        interp.ExecuteLine("settemp x 2");
        interp.ExecuteLine("settemp x 3"); // second override must NOT overwrite the saved original (1)
        interp.ExecuteLine("settemp_restore");
        Assert.Equal("1", cvars.GetString("x"));
    }

    // ---- rpn via the console surface ---------------------------------------------------------------------

    [Fact]
    public void Rpn_RegisteredOnConsole_WritesCvar()
    {
        var (interp, cvars, _, _) = Make();
        interp.ExecuteLine("rpn /score 10 5 add def");
        Assert.Equal("15", cvars.GetString("score"));
    }

    // ---- nextframe (routes to the live world when wired) -------------------------------------------------

    [Fact]
    public void NextFrame_ForwardsToTheWorldRouter_WhenWired()
    {
        string? routed = null;
        var (interp, _, _, _) = Make(localRouter: line => { routed = line; return ""; });
        interp.ExecuteLine("nextframe say hi");
        Assert.Equal("nextframe say hi", routed); // forwarded whole → server bus enqueues defer 0
    }

    [Fact]
    public void NextFrame_NoWorld_RunsInline()
    {
        // With no local router, "next frame" degenerates to running the command now (documented deviation).
        var (interp, cvars, _, _) = Make(localRouter: null);
        interp.ExecuteLine("nextframe set runme 1");
        Assert.Equal("1", cvars.GetString("runme"));
    }
}
