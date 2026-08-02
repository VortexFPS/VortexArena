using System;
using VortexArena.Common.Gameplay;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Exercises <see cref="ServerListInfo"/> — the Godot-free half of the server browser: the <c>qcstatus</c>
/// infostring parse, the category assignment and override folding, the filter-box split, and the listbox
/// scroll easing. All four are defined by Base (qcsrc/menu/xonotic/serverlist.qc and
/// qcsrc/menu/item/listbox.qc), so the cases below are written against what the QC does, not against what
/// the port happens to do.
/// </summary>
public class ServerListInfoTests
{
    // ── qcstatus ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void QcStatus_Parses_The_Full_WinningConditionHelper_Line()
    {
        // The exact shape qcsrc/server/scores.qc:452 builds — note the ToS URL arrives with its colons
        // swapped for pipes, because the line itself is colon-separated (world.qc:798).
        QcStatus q = ServerListInfo.ParseQcStatus("ctf:0.8.6:P0:S12:F4:Thttps|//example/tos:Mxonotic::,,");

        Assert.Equal("ctf", q.Gametype);
        Assert.Equal("0.8.6", q.Version);
        Assert.True(q.PureAvailable);
        Assert.True(q.Pure);                       // P0 == "no settings changed from stock"
        Assert.Equal(12, q.FreeSlots);
        Assert.Equal(ServerListInfo.ServerFlagPlayerStats, q.ServerFlags);
        Assert.Equal("https://example/tos", q.TermsOfServiceUrl);
        Assert.Equal("xonotic", q.ModName);
    }

    [Fact]
    public void QcStatus_Stops_At_The_Empty_Token_That_Ends_The_Header()
    {
        // Everything after the "::" is the score-label block; a label starting with 'S' or 'P' must not be
        // mistaken for a header key.
        QcStatus q = ServerListInfo.ParseQcStatus("dm:git:P3:S0:Mxonotic::Sfake,Pfake:tlabel");

        Assert.Equal(0, q.FreeSlots);              // NOT re-read from "Sfake"
        Assert.False(q.Pure);                      // P3 = three changes
        Assert.True(q.PureAvailable);
    }

    [Fact]
    public void QcStatus_Unknown_Keys_Are_Skipped_Not_Fatal()
    {
        QcStatus q = ServerListInfo.ParseQcStatus("dm:git:Znewthing:S5:Mxonotic::");
        Assert.Equal(5, q.FreeSlots);
        Assert.Equal("xonotic", q.ModName);
    }

    [Fact]
    public void QcStatus_Purity_Is_Cleared_For_Mods_That_Cannot_Report_It()
    {
        // serverlist.qc:900 — only a handful of mods implement the check; elsewhere a P token means nothing.
        Assert.True(ServerListInfo.ParseQcStatus("dm:git:P0:Minstagib::").PureAvailable);
        Assert.False(ServerListInfo.ParseQcStatus("dm:git:P0:Moverkill::").PureAvailable);
        Assert.False(ServerListInfo.ParseQcStatus("dm:git:P0:Msomething::").PureAvailable);
    }

    [Fact]
    public void QcStatus_Missing_Or_Empty_Yields_Nothing_Known()
    {
        Assert.Equal(QcStatus.Unknown, ServerListInfo.ParseQcStatus(null));
        Assert.Equal(QcStatus.Unknown, ServerListInfo.ParseQcStatus(""));
        // A bare gametype (no header keys at all) still leaves the numeric fields at "not reported".
        QcStatus q = ServerListInfo.ParseQcStatus("dm");
        Assert.Equal(-1, q.FreeSlots);
        Assert.Equal(-1, q.ServerFlags);
        Assert.False(q.PureAvailable);
    }

    [Fact]
    public void QcStatus_This_Builds_Version_Survives_The_Round_Trip()
    {
        // The version token is field 1 of a COLON-separated line that also travels inside a backslash-
        // separated Darkplaces infostring, so a version string carrying either separator would be read as the
        // start of the next field and silently corrupt everything after it. BuildInfo.Sanitize is what stops
        // that; this pins the contract from both ends.
        string version = VortexArena.Common.BuildInfo.Version;
        Assert.NotEmpty(version);
        Assert.DoesNotContain(":", version);
        Assert.DoesNotContain("\\", version);

        QcStatus q = ServerListInfo.ParseQcStatus($"dm:{version}:P0:S16:F1:M{ServerListInfo.OwnModName}::");
        Assert.Equal(version, q.Version);
        Assert.Equal("dm", q.Gametype);
        Assert.Equal(16, q.FreeSlots);   // the fields AFTER the version still land where they should
    }

    [Fact]
    public void BuildInfo_Sanitize_Replaces_Every_Separator_It_Guards_Against()
    {
        // Replaced, not dropped: a mangled version should look wrong rather than quietly shorter.
        Assert.Equal("1-0-0", VortexArena.Common.BuildInfo.Sanitize("1:0:0"));
        Assert.Equal("a-b", VortexArena.Common.BuildInfo.Sanitize("a\\b"));
        Assert.Equal("my-build", VortexArena.Common.BuildInfo.Sanitize("my build"));
        Assert.Equal("v1-2", VortexArena.Common.BuildInfo.Sanitize("v1\"2"));
        Assert.Equal("1.0.0+abcd1234", VortexArena.Common.BuildInfo.Sanitize("1.0.0+abcd1234"));
    }

    // ── categories ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shipped cvar values (data/core.pk3dir/xonotic-client.cfg:706-710).</summary>
    private static readonly ServerListInfo.RecommendationRules StockRules =
        new(Mode: 3, MaxPing: 150, MinFreeSlots: 1, MinHumans: 0, PureThreshold: -1, ModImpurity: 0);

    private static ServerListInfo.CategoryInput Server(
        string mod = "xonotic", bool favorite = false, bool promoted = false, bool recommended = false,
        int freeSlots = 4, int humans = 2, int ping = 40, bool pure = true, bool pureAvailable = true)
        => new(favorite, promoted, recommended, mod, pure, pureAvailable, freeSlots, humans, ping);

    [Fact]
    public void Category_Bookmark_Wins_Over_Everything_Else()
    {
        Assert.Equal(ServerCategory.Favorited,
            ServerListInfo.CategoryForEntry(Server(mod: "overkill", favorite: true), StockRules));
    }

    [Fact]
    public void Category_Follows_The_Reported_Mod_Name()
    {
        Assert.Equal(ServerCategory.Normal, ServerListInfo.CategoryForEntry(Server(), StockRules));
        Assert.Equal(ServerCategory.Xpm, ServerListInfo.CategoryForEntry(Server(mod: "xpm"), StockRules));
        Assert.Equal(ServerCategory.Instagib, ServerListInfo.CategoryForEntry(Server(mod: "instagib"), StockRules));
        Assert.Equal(ServerCategory.Instagib, ServerListInfo.CategoryForEntry(Server(mod: "minstagib"), StockRules));
        Assert.Equal(ServerCategory.Overkill, ServerListInfo.CategoryForEntry(Server(mod: "overkill"), StockRules));
        Assert.Equal(ServerCategory.Defrag, ServerListInfo.CategoryForEntry(Server(mod: "xdf"), StockRules));
        Assert.Equal(ServerCategory.Defrag, ServerListInfo.CategoryForEntry(Server(mod: "cts"), StockRules));
        Assert.Equal(ServerCategory.Modified, ServerListInfo.CategoryForEntry(Server(mod: "whatever"), StockRules));
        // A server too old to report a mod name at all counts as modified.
        Assert.Equal(ServerCategory.Modified, ServerListInfo.CategoryForEntry(Server(mod: ""), StockRules));
    }

    [Fact]
    public void Category_This_Games_Own_Servers_Are_Normal_Not_Modified()
    {
        // The one divergence from Base's table. A VortexArena host reports its OWN mod name in qcstatus (see
        // ServerNet.BuildQcStatus), and without an arm for it the switch's default would file every server of
        // this game under "Modified Servers" — behind a heading that says somebody has been editing it.
        Assert.Equal(ServerCategory.Normal,
            ServerListInfo.CategoryForEntry(Server(mod: ServerListInfo.OwnModName), StockRules));
        // ...and it must be the name the server actually sends, or the arm never matches. GameIdentity.Name
        // is the authority; this is the seam between the two projects, which nothing else checks.
        Assert.Equal(ServerListInfo.OwnModName, VortexArena.Net.GameIdentity.Name.ToLowerInvariant());
        // Its purity report is meaningful too, so a stock host gets the "official settings" tick.
        Assert.True(ServerListInfo.ParseQcStatus($"dm:git:P0:M{ServerListInfo.OwnModName}::").PureAvailable);
    }

    [Fact]
    public void Category_A_Promoted_Server_Is_Recommended_Outright()
    {
        Assert.Equal(ServerCategory.Recommended,
            ServerListInfo.CategoryForEntry(Server(promoted: true, ping: 900), StockRules));
    }

    [Fact]
    public void Category_The_Recommendation_Vote_Needs_A_Majority()
    {
        // Mode 3 = both votes. A good server that is NOT on the community list nets 0 (-1 then +1), which is
        // not > 0 — so it stays Normal. This is also why nothing is recommended before the external list
        // loads, in Base as much as here.
        Assert.Equal(ServerCategory.Normal, ServerListInfo.CategoryForEntry(Server(), StockRules));
        // On the list AND good: +1 +1.
        Assert.Equal(ServerCategory.Recommended,
            ServerListInfo.CategoryForEntry(Server(recommended: true), StockRules));
        // On the list but laggy: +1 -1 = 0.
        Assert.Equal(ServerCategory.Normal,
            ServerListInfo.CategoryForEntry(Server(recommended: true, ping: 400), StockRules));

        // Mode 2 = the local heuristic alone, which a good server passes on its own.
        var heuristicOnly = StockRules with { Mode = 2 };
        Assert.Equal(ServerCategory.Recommended, ServerListInfo.CategoryForEntry(Server(), heuristicOnly));
        Assert.Equal(ServerCategory.Normal,
            ServerListInfo.CategoryForEntry(Server(freeSlots: 0), heuristicOnly));
        // An unqueried server has no ping to judge, so it cannot pass.
        Assert.Equal(ServerCategory.Normal, ServerListInfo.CategoryForEntry(Server(ping: -1), heuristicOnly));
    }

    [Fact]
    public void Category_Recommendations_Off_Skips_The_Vote_Entirely()
    {
        var off = StockRules with { Mode = 0 };
        Assert.Equal(ServerCategory.Normal, ServerListInfo.CategoryForEntry(Server(promoted: true), off));
    }

    [Fact]
    public void Override_With_Categories_Off_Collapses_Everything_But_Favorites_And_Recommended()
    {
        // The shipped default (menu_slist_categories 0): one "Servers" heading, plus the two that stay apart.
        foreach (ServerCategory cat in Enum.GetValues<ServerCategory>())
        {
            ServerCategory folded = ServerListInfo.ApplyOverride(cat, categoriesEnabled: false, null);
            ServerCategory expected = cat is ServerCategory.Favorited or ServerCategory.Recommended
                ? cat
                : ServerCategory.Servers;
            Assert.Equal(expected, folded);
        }
    }

    [Fact]
    public void Override_With_Categories_On_Follows_The_Per_Category_Cvars()
    {
        // The shipped enabled column: only CAT_SERVERS is overridden, and it points at itself → no change.
        string Cvar(string key) => key == "CAT_SERVERS" ? "CAT_NORMAL" : "";

        Assert.Equal(ServerCategory.Xpm, ServerListInfo.ApplyOverride(ServerCategory.Xpm, true, Cvar));
        Assert.Equal(ServerCategory.Normal, ServerListInfo.ApplyOverride(ServerCategory.Servers, true, Cvar));
        // A self-referential override is ignored, exactly as the QC's `s != cat_name` guard does.
        Assert.Equal(ServerCategory.Xpm,
            ServerListInfo.ApplyOverride(ServerCategory.Xpm, true, _ => "CAT_XPM"));
        // So is one naming a category that doesn't exist.
        Assert.Equal(ServerCategory.Xpm,
            ServerListInfo.ApplyOverride(ServerCategory.Xpm, true, _ => "CAT_NOPE"));
    }

    [Fact]
    public void Category_Numbering_Matches_The_QC_Sort_Order()
    {
        // The category IS the primary sort key (SLSF_CATEGORIES), so the declaration order is load-bearing:
        // Favorites first, then Recommended, then the rest.
        Assert.Equal(1, (int)ServerCategory.Favorited);
        Assert.True(ServerCategory.Favorited < ServerCategory.Recommended);
        Assert.True(ServerCategory.Recommended < ServerCategory.Normal);
        Assert.True(ServerCategory.Normal < ServerCategory.Defrag);
    }

    // ── the filter box ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_Splits_A_Gametype_Prefix_Off_The_Free_Text()
    {
        Assert.Equal(("", "pickup"), ServerListInfo.SplitFilter("pickup"));
        Assert.Equal(("ctf", "pickup"), ServerListInfo.SplitFilter("ctf:pickup"));
        // The QC skips the spaces after the colon.
        Assert.Equal(("ctf", "pickup"), ServerListInfo.SplitFilter("ctf:   pickup"));
        // A bare "type:" filters by gametype alone — this is what the Type header button produces.
        Assert.Equal(("ctf", ""), ServerListInfo.SplitFilter("ctf:"));
        Assert.Equal(("", ""), ServerListInfo.SplitFilter(""));
        Assert.Equal(("", ""), ServerListInfo.SplitFilter(null));
    }

    // ── scroll easing ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scroll_Easing_Converges_Toward_The_Target_And_Snaps()
    {
        double pos = 0, target = 1000;
        for (int i = 0; i < 200 && pos != target; i++)
            pos = ServerListInfo.AdvanceScroll(pos, target, dt: 1.0 / 60.0, averagingTime: 0.16, epsilon: 0.5);
        Assert.Equal(target, pos); // reached exactly, via the epsilon snap — never asymptotically stuck short
    }

    [Fact]
    public void Scroll_Easing_Is_Framerate_Independent()
    {
        // The whole point of the exp(-dt/t) form: the same wall-clock time gives the same position whatever
        // the frame rate. One second at 30 fps and at 240 fps must agree.
        double Run(int fps)
        {
            double pos = 0;
            for (int i = 0; i < fps; i++)
                pos = ServerListInfo.AdvanceScroll(pos, 1.0, 1.0 / fps, averagingTime: 0.16, epsilon: 1e-9);
            return pos;
        }
        Assert.Equal(Run(30), Run(240), precision: 5);
    }

    [Fact]
    public void Scroll_Easing_With_No_Averaging_Time_Jumps()
    {
        // averaging_time 0 disables smoothing (the QC guards the same way, since exp() would divide by zero).
        Assert.Equal(500.0, ServerListInfo.AdvanceScroll(0, 500, 1.0 / 60.0, averagingTime: 0, epsilon: 0.5));
    }

    [Fact]
    public void Scroll_Easing_Shorter_Averaging_Time_Catches_Up_Sooner()
    {
        // menu_scroll_averaging_time_pressed (0.06) exists so dragging the scrollbar tracks the cursor; it
        // must actually be the faster of the two.
        double slow = ServerListInfo.AdvanceScroll(0, 1, 1.0 / 60.0, 0.16, 1e-9);
        double fast = ServerListInfo.AdvanceScroll(0, 1, 1.0 / 60.0, 0.06, 1e-9);
        Assert.True(fast > slow);
    }
}
