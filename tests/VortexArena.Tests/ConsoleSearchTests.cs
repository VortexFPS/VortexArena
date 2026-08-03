using System.Collections.Generic;
using System.Linq;
using VortexArena.Common.Config;
using VortexArena.Common.Services;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests for <see cref="ConsoleSearch"/> — the ranking engine behind <c>search</c>/<c>apropos</c> — and for the
/// description plumbing that feeds it (cfg-tree <c>set … "description"</c> capture, the packaged DP engine help
/// table, and <c>Register</c>'s help argument).
///
/// <para>The headline case is the one the whole feature exists for: <c>search max fps</c> must find
/// <c>cl_maxfps</c>, whose name contains neither the word "max fps" nor "maximum", and must rank it above the
/// half-dozen neighbours that also carry both keywords.</para>
/// </summary>
public class ConsoleSearchTests
{
    /// <summary>A slice of the real DP/Xonotic cvar corpus, descriptions included verbatim — the ranking is only
    /// meaningful against names and help text that actually collide the way the shipped ones do.</summary>
    private static SearchCandidate[] FpsCorpus() => new[]
    {
        new SearchCandidate(SearchKind.Cvar, "cl_maxfps",
            "maximum fps cap, 0 = unlimited, if game is running faster than this it will wait before running " +
            "another frame (useful to make cpu time available to other programs)"),
        new SearchCandidate(SearchKind.Cvar, "cl_maxidlefps",
            "maximum fps cap when the game is not the active window (makes cpu time available to other programs"),
        new SearchCandidate(SearchKind.Cvar, "cl_maxfps_alwayssleep",
            "gives up some processing time to other applications each frame, value in milliseconds, disabled if " +
            "a timedemo is running"),
        new SearchCandidate(SearchKind.Cvar, "scr_loadingscreen_maxfps",
            "restricts fps during loading (to prevent wasting cpu time)"),
        new SearchCandidate(SearchKind.Cvar, "showfps",
            "shows your rendered fps (frames per second)"),
        new SearchCandidate(SearchKind.Cvar, "sys_ticrate",
            "how long a server frame is in seconds, 0.05 is 20fps server rate, 0.1 is 10fps"),
        new SearchCandidate(SearchKind.Cvar, "host_maxwait",
            "maximum time in milliseconds to wait for a frame"),
        new SearchCandidate(SearchKind.Command, "timedemo",
            "take a demo, play it back as fast as possible, report how long it took and the max fps reached"),
    };

    // ---- the headline case ---------------------------------------------------------------------------------

    [Fact]
    public void Search_MaxFps_RanksClMaxfpsBest()
    {
        List<SearchHit> hits = ConsoleSearch.Rank(new[] { "max", "fps" }, FpsCorpus());

        Assert.NotEmpty(hits);
        // Best match is LAST — the line that ends up directly above the prompt.
        Assert.Equal("cl_maxfps", hits[^1].Name);
    }

    [Fact]
    public void Search_ResultsAreOrderedWorstFirst()
    {
        List<SearchHit> hits = ConsoleSearch.Rank(new[] { "max", "fps" }, FpsCorpus());

        for (int i = 1; i < hits.Count; i++)
            Assert.True(hits[i - 1].Score <= hits[i].Score,
                $"hit {i - 1} ({hits[i - 1].Name}) scored above hit {i} ({hits[i].Name}) — order must ascend");
    }

    [Fact]
    public void Search_ContiguousNameMatch_OutranksSplitNameMatch()
    {
        // "maxfps" is contiguous in cl_maxfps but interrupted in cl_maxidlefps; both carry both keywords in the
        // name AND the description, so contiguity is the only thing that can separate them.
        List<SearchHit> hits = ConsoleSearch.Rank(new[] { "max", "fps" }, FpsCorpus());

        double contiguous = hits.Single(h => h.Name == "cl_maxfps").Score;
        double split = hits.Single(h => h.Name == "cl_maxidlefps").Score;
        Assert.True(contiguous > split);
    }

    [Fact]
    public void Search_NameMatch_OutranksDescriptionOnlyMatch()
    {
        List<SearchHit> hits = ConsoleSearch.Rank(new[] { "max", "fps" }, FpsCorpus());

        // `timedemo` has both keywords only in its description; every name match must beat it.
        double descOnly = hits.Single(h => h.Name == "timedemo").Score;
        foreach (SearchHit h in hits.Where(h => h.Name.Contains("max") && h.Name.Contains("fps")))
            Assert.True(h.Score > descOnly, $"{h.Name} (name match) must outrank timedemo (description only)");
    }

    // ---- matching semantics --------------------------------------------------------------------------------

    [Fact]
    public void Search_RequiresEveryKeyword()
    {
        // "fps" alone matches sys_ticrate's description ("20fps server rate"); adding "max" must drop it, since
        // neither its name nor its description contains "max".
        Assert.Contains(ConsoleSearch.Rank(new[] { "fps" }, FpsCorpus()), h => h.Name == "sys_ticrate");
        Assert.DoesNotContain(ConsoleSearch.Rank(new[] { "max", "fps" }, FpsCorpus()), h => h.Name == "sys_ticrate");
    }

    [Fact]
    public void Search_FindsByDescriptionAlone()
    {
        // Nothing in the corpus is NAMED "unlimited" — this only matches through cl_maxfps's help string.
        List<SearchHit> hits = ConsoleSearch.Rank(new[] { "unlimited" }, FpsCorpus());
        Assert.Equal("cl_maxfps", Assert.Single(hits).Name);
    }

    [Fact]
    public void Search_ExactCvarName_RanksItBest()
    {
        List<SearchHit> hits = ConsoleSearch.Rank(new[] { "cl_maxidlefps" }, FpsCorpus());
        Assert.Equal("cl_maxidlefps", hits[^1].Name);
    }

    [Fact]
    public void Search_KeywordWildcards_StillGlob()
    {
        // DP's apropos took one glob pattern; a keyword carrying * or ? must keep behaving that way.
        List<SearchHit> hits = ConsoleSearch.Rank(new[] { "cl_max*" }, FpsCorpus());
        Assert.Equal(
            new[] { "cl_maxfps", "cl_maxfps_alwayssleep", "cl_maxidlefps" },
            hits.Select(h => h.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Search_EmptyKeywords_MatchNothing()
        => Assert.Empty(ConsoleSearch.Rank(System.Array.Empty<string>(), FpsCorpus()));

    [Fact]
    public void Search_MatchesCaseInsensitively()
        => Assert.Equal("cl_maxfps", ConsoleSearch.Rank(new[] { "MAX", "FPS" }, FpsCorpus())[^1].Name);

    [Theory]
    [InlineData("cl_maxfps", "cl_max*", true)]
    [InlineData("cl_maxfps", "*fps", true)]
    [InlineData("cl_maxfps", "*max*", true)]
    [InlineData("cl_maxfps", "cl_?axfps", true)]
    [InlineData("cl_maxfps", "cl_maxfps", true)]
    [InlineData("cl_maxfps", "cl_maxfp", false)]
    [InlineData("cl_maxfps", "*idle*", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaab", "*a*a*a*a*a*a*b", true)]
    public void Glob_MatchesLikeDarkplaces(string text, string pattern, bool expected)
        => Assert.Equal(expected, ConsoleSearch.Glob(text, pattern));

    // ---- the description sources ---------------------------------------------------------------------------

    [Fact]
    public void CfgSetThirdArgument_BecomesTheDescription()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null)
        {
            CvarDescriptionHook = (name, desc) => cvars.SetDescription(name, desc),
        };

        interp.ExecuteLine("set g_balance_blaster_primary_damage 20 \"damage the blaster does per hit\"");

        Assert.Equal("20", cvars.GetString("g_balance_blaster_primary_damage"));
        Assert.Equal("damage the blaster does per hit",
            cvars.GetDescription("g_balance_blaster_primary_damage"));
    }

    [Fact]
    public void SetWithoutDescription_LeavesTheExistingOneAlone()
    {
        // A later bare `set` (how the cfgs assign engine cvars, and how the console assigns anything) must not
        // wipe the help string the declaring line supplied.
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null)
        {
            CvarDescriptionHook = (name, desc) => cvars.SetDescription(name, desc),
        };

        interp.ExecuteLine("set con_textsize 8 \"console text size in virtual 2D pixels\"");
        interp.ExecuteLine("set con_textsize 10");

        Assert.Equal("10", cvars.GetString("con_textsize"));
        Assert.Equal("console text size in virtual 2D pixels", cvars.GetDescription("con_textsize"));
    }

    [Fact]
    public void EngineHelpTable_LoadsNameTabDescriptionLines()
    {
        var cvars = new CvarService();
        int n = CvarHelpTable.Load(cvars,
            "# a comment\n" +
            "\n" +
            "cl_maxfps\tmaximum fps cap, 0 = unlimited\n" +
            "con_textsize\tconsole text size in virtual 2D pixels\n" +
            "malformed-line-with-no-tab\n");

        Assert.Equal(2, n);
        Assert.Equal("maximum fps cap, 0 = unlimited", cvars.GetDescription("cl_maxfps"));
        Assert.Equal("", cvars.GetDescription("malformed-line-with-no-tab"));
    }

    [Fact]
    public void EngineHelpTable_NeverOverwritesTheCfgTreesDescription()
    {
        // Load order at boot is cfg tree first, engine table second — the tree is the authority for any cvar it
        // describes, and the table only fills the gaps.
        var cvars = new CvarService();
        cvars.SetDescription("cl_maxfps", "from the cfg tree");
        CvarHelpTable.Load(cvars, "cl_maxfps\tfrom the engine table\n");

        Assert.Equal("from the cfg tree", cvars.GetDescription("cl_maxfps"));
    }

    [Fact]
    public void EngineHelpTable_MissingFileIsANoOp()
        => Assert.Equal(0, CvarHelpTable.Load(new CvarService(), (string?)null));

    [Fact]
    public void Register_CarriesItsDescription()
    {
        var cvars = new CvarService();
        cvars.Register("cl_frameprofiler", "0", CvarFlags.None, "record per-frame timing scopes");
        Assert.Equal("record per-frame timing scopes", cvars.GetDescription("cl_frameprofiler"));
    }

    [Fact]
    public void RegisteredCommands_CarryTheirDescription()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        _ = new ConsoleCommands(interp, cvars, _ => { });

        Assert.Contains("keyword", interp.CommandDescription("search"));
        Assert.Equal("", interp.CommandDescription("no_such_command"));
    }

    // ---- end-to-end through the console command ------------------------------------------------------------

    [Fact]
    public void SearchCommand_PrintsBestMatchOnTheLastLine()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        _ = new ConsoleCommands(interp, cvars, output.Add);

        foreach (SearchCandidate c in FpsCorpus().Where(c => c.Kind == SearchKind.Cvar))
        {
            cvars.Set(c.Name, "0");
            cvars.SetDescription(c.Name, c.Description);
        }

        interp.ExecuteLine("search max fps");

        Assert.NotEmpty(output);
        Assert.Contains("best last", output[0]);            // the header carries the count, not a trailing line
        Assert.Contains("cl_maxfps^7 is", output[^1]);      // …so the winner is the very last line printed
    }

    [Fact]
    public void SearchCommand_FindsCommandsAndAliases_NotJustCvars()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        _ = new ConsoleCommands(interp, cvars, output.Add);
        interp.DefineAlias("hud_configure", "toggle _hud_configure");

        interp.ExecuteLine("search bindlist");
        Assert.Contains(output, l => l.StartsWith("command") && l.Contains("bindlist"));

        output.Clear();
        interp.ExecuteLine("search hud_configure");
        Assert.Contains(output, l => l.StartsWith("alias") && l.Contains("hud_configure"));

        // …and through a command's DESCRIPTION, not just its name (nothing is named "scrollback").
        output.Clear();
        interp.ExecuteLine("search scrollback");
        Assert.Contains(output, l => l.Contains("condump") || l.Contains("clear"));
    }

    // ---- cvar_changes -------------------------------------------------------------------------------------

    [Fact]
    public void CvarChanges_ListsOnlyWhatDiffersFromTheShippedDefault()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        _ = new ConsoleCommands(interp, cvars, output.Add);

        cvars.Set("cl_bob", "0.01");        // the shipped tree's value…
        cvars.Set("cl_zoomfactor", "3");
        cvars.LockDefaults();               // …becomes the baseline
        cvars.Set("cl_bob", "0.02");        // then the user changes one

        interp.ExecuteLine("cvar_changes");

        Assert.Contains(output, l => l.Contains("cl_bob") && l.Contains("0.02") && l.Contains("was \"0.01"));
        Assert.DoesNotContain(output, l => l.Contains("cl_zoomfactor"));
    }

    [Fact]
    public void CvarChanges_SeparatesSavedFromSessionOnly()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        _ = new ConsoleCommands(interp, cvars, output.Add);

        cvars.Set("cl_bob", "0.01");
        cvars.Set("r_pvs_cull", "1");
        cvars.LockDefaults();
        cvars.Set("cl_bob", "0.02");
        cvars.MarkArchived("cl_bob");       // a real setting — follows you to the next launch
        cvars.Set("r_pvs_cull", "0");       // a console/--cvar debug pin — evaporates on restart

        interp.ExecuteLine("cvar_changes");

        int savedHeader = output.FindIndex(l => l.Contains("saved to your config"));
        int sessionHeader = output.FindIndex(l => l.Contains("this session only"));
        Assert.True(savedHeader >= 0 && sessionHeader > savedHeader);
        Assert.True(output.FindIndex(l => l.Contains("cl_bob")) < sessionHeader);
        Assert.True(output.FindIndex(l => l.Contains("r_pvs_cull")) > sessionHeader);
    }

    [Fact]
    public void CvarChanges_StockConfigSaysSo()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        _ = new ConsoleCommands(interp, cvars, output.Add);

        cvars.Set("cl_bob", "0.01");
        cvars.LockDefaults();

        interp.ExecuteLine("cvar_changes");
        Assert.Contains(output, l => l.Contains("stock configuration"));
    }

    // ---- WasSetByUser (what backs the cl_maxfps "auto" rule) ------------------------------------------------

    [Fact]
    public void WasSetByUser_SeparatesThePlayersChoiceFromTheShippedDefault()
    {
        var cvars = new CvarService();
        cvars.Set("cl_maxfps", "256");      // the shipped cfg tree
        cvars.LockDefaults();

        Assert.False(cvars.WasSetByUser("cl_maxfps"));

        // The player picks the SAME number the default happens to be. IsModified cannot see that — which is
        // exactly why the framerate cap used to ignore anyone who chose 256.
        cvars.Set("cl_maxfps", "256");
        Assert.False(cvars.IsModified("cl_maxfps"));
        Assert.True(cvars.WasSetByUser("cl_maxfps"));
    }

    [Fact]
    public void SearchCommand_NoMatch_SaysSo()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        var output = new List<string>();
        _ = new ConsoleCommands(interp, cvars, output.Add);

        interp.ExecuteLine("search zzzznotacvar");

        Assert.Contains(output, l => l.Contains("nothing matching"));
    }
}
