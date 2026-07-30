using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Common.Menu;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises the menu data-list backends — the C# port of QC's shared DataSource abstraction
/// (datasource.qc: StringSource / CvarStringSource) and the file-scan list backends (screenshotlist.qc,
/// soundlist.qc, skinlist.qc) + the .mapinfo parser (mapinfo.qc / dialog_multiplayer_create_mapinfo.qc).
/// Everything here is Godot-free (the data sources take an <see cref="IFileEnumerator"/> / text-reader seam),
/// so the tests use fakes — no VFS, no engine.
/// </summary>
public class MenuDataSourceTests
{
    // -------------------------------------------------------------------------------------------------
    //  Fakes
    // -------------------------------------------------------------------------------------------------

    private sealed class FakeFiles : IFileEnumerator
    {
        private readonly List<string> _paths;
        public FakeFiles(params string[] paths) => _paths = paths.ToList();

        public IEnumerable<string> Find(string prefix, string? extension = null)
        {
            string? ext = extension is null ? null
                : extension.StartsWith('.') ? extension
                : "." + extension;
            foreach (string p in _paths)
            {
                if (!p.StartsWith(prefix, System.StringComparison.Ordinal))
                    continue;
                if (ext != null && !p.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return p;
            }
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  StringSource / CvarStringSource — datasource.qc
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void StringSource_Tokenizes_By_Separator_Keeping_Empties()
    {
        // tokenizebyseparator semantics: each char in the separator splits; empty tokens are kept.
        var src = new StringSource("a:b::c", ":");
        Assert.Equal(4, src.Reload(null)); // "a","b","","c"

        Assert.True(src.TryGetEntry(0, out var e0));
        Assert.Equal("a", e0.Name);
        Assert.True(src.TryGetEntry(2, out var e2));
        Assert.Equal("", e2.Name); // the empty token between "::"
        Assert.True(src.TryGetEntry(3, out var e3));
        Assert.Equal("c", e3.Name);

        // out of bounds → DataSource_false (datasource.qc:12).
        Assert.False(src.TryGetEntry(4, out _));
        Assert.False(src.TryGetEntry(-1, out _));
    }

    [Fact]
    public void StringSource_Empty_String_Yields_No_Entries()
    {
        var src = new StringSource("", " ");
        Assert.Equal(0, src.Reload(null));
        Assert.False(src.TryGetEntry(0, out _));
    }

    [Fact]
    public void StringSource_Null_Separator_Returns_Whole_String()
    {
        var src = new StringSource("whole thing", "");
        Assert.Equal(1, src.Reload(null));
        Assert.True(src.TryGetEntry(0, out var e));
        Assert.Equal("whole thing", e.Name);
    }

    [Fact]
    public void CvarStringSource_Reads_Cvar_Each_Access()
    {
        // The live playlist source: CvarStringSource over a space-separated cvar (music_playlist_list0).
        var store = new Dictionary<string, string> { ["music_playlist_list0"] = "track1 track2" };
        var src = new CvarStringSource("music_playlist_list0", " ", n => store.TryGetValue(n, out var v) ? v : null);

        Assert.Equal(2, src.Reload(null));
        Assert.True(src.TryGetEntry(1, out var e));
        Assert.Equal("track2", e.Name);

        // Mutate the cvar — the source re-reads it on the next access (datasource.qc:30-31, the live-draw behavior).
        store["music_playlist_list0"] = "a b c";
        Assert.Equal(3, src.Reload(null));
        Assert.True(src.TryGetEntry(2, out var e2));
        Assert.Equal("c", e2.Name);

        // Empty cvar → no entries.
        store["music_playlist_list0"] = "";
        Assert.Equal(0, src.Reload(null));
    }

    // -------------------------------------------------------------------------------------------------
    //  ScreenshotSource — screenshotlist.qc (prefix/ext strip, decolorize, sort, filter)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ScreenshotSource_Strips_Prefix_Ext_And_Sorts()
    {
        var files = new FakeFiles(
            "screenshots/zeta.jpg",
            "screenshots/alpha.png",
            "screenshots/mid.tga",
            "models/notascreenshot.jpg"); // wrong prefix → ignored
        var src = new ScreenshotSource(files);

        int n = src.Reload(null);
        Assert.Equal(3, n);
        // buf_sort(…, false) — case-insensitive ascending.
        Assert.Equal(new[] { "alpha", "mid", "zeta" }, src.Names.ToArray());
    }

    [Fact]
    public void ScreenshotSource_Decolorizes_Names()
    {
        var files = new FakeFiles("screenshots/^1red^7name.jpg");
        var src = new ScreenshotSource(files);
        src.Reload(null);
        Assert.Equal("redname", src.Names.Single()); // strdecolorize removed ^1 and ^7
    }

    [Fact]
    public void ScreenshotSource_Filter_Glob_Restricts()
    {
        var files = new FakeFiles(
            "screenshots/duel_foo.jpg",
            "screenshots/dm_bar.jpg",
            "screenshots/duel_baz.png");
        var src = new ScreenshotSource(files);

        int n = src.Reload("*duel*"); // the menu wraps a plain query "duel" → "*duel*"
        Assert.Equal(2, n);
        Assert.Equal(new[] { "duel_baz", "duel_foo" }, src.Names.ToArray()); // sorted
        Assert.Equal(-1, src.IndexOf("dm_bar"));
        Assert.Equal(0, src.IndexOf("duel_baz"));
    }

    // -------------------------------------------------------------------------------------------------
    //  SoundSource — soundlist.qc
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void SoundSource_Strips_Cdtracks_Prefix_And_Ogg()
    {
        var files = new FakeFiles(
            "sound/cdtracks/digital-pursuit.ogg",
            "sound/cdtracks/breakdown.ogg",
            "sound/other/nope.ogg"); // wrong subdir → ignored
        var src = new SoundSource(files);

        Assert.Equal(2, src.Reload(null));
        Assert.Contains("digital-pursuit", src.Names);
        Assert.Contains("breakdown", src.Names);
        Assert.Equal(0, src.IndexOf("digital-pursuit"));
    }

    [Fact]
    public void SoundSource_Filter_Is_Substring()
    {
        var files = new FakeFiles(
            "sound/cdtracks/digital-pursuit.ogg",
            "sound/cdtracks/breakdown.ogg");
        var src = new SoundSource(files);

        Assert.Equal(1, src.Reload("break"));
        Assert.Equal("breakdown", src.Names.Single());
    }

    // -------------------------------------------------------------------------------------------------
    //  SkinSource — skinlist.qc (name from dir, title/author parse, preview fallback, Default tag)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void SkinSource_Parses_Title_Author_And_Default_Tag()
    {
        var files = new FakeFiles(
            "gfx/menu/luma/skinvalues.txt",
            "gfx/menu/default/skinvalues.txt");

        var texts = new Dictionary<string, string>
        {
            ["gfx/menu/luma/skinvalues.txt"] = "title Luminos\nauthor Morphed\n",
            ["gfx/menu/default/skinvalues.txt"] = "title Xolonium\nauthor div0\n",
        };
        // default has a preview image; luma doesn't (→ nopreview_menuskin).
        var images = new HashSet<string> { "/gfx/menu/default/skinpreview" };

        var src = new SkinSource(files,
            p => texts.TryGetValue(p, out var t) ? t : null,
            v => images.Contains(v),
            defaultSkin: "default");

        Assert.Equal(2, src.Reload(null));

        SkinInfo luma = src.Skins.Single(s => s.Name == "luma");
        Assert.Equal("Luminos", luma.Title);          // no "(Default)" tag — not the default skin
        Assert.Equal("Morphed", luma.Author);
        Assert.Equal("nopreview_menuskin", luma.Preview); // no preview image → fallback

        SkinInfo def = src.Skins.Single(s => s.Name == "default");
        Assert.Equal("Xolonium (Default)", def.Title);  // skinlist.qc:86-87 default tag
        Assert.Equal("/gfx/menu/default/skinpreview", def.Preview);

        // loadCvars finds the row whose NAME matches menu_skin.
        Assert.Equal(src.Skins.ToList().FindIndex(s => s.Name == "default"), src.IndexOf("default"));
    }

    [Fact]
    public void SkinSource_Missing_Title_Uses_Placeholder()
    {
        var files = new FakeFiles("gfx/menu/bare/skinvalues.txt");
        var src = new SkinSource(files, _ => "", _ => false, "default"); // empty file → no title/author lines
        src.Reload(null);
        SkinInfo s = src.Skins.Single();
        Assert.Equal("bare", s.Name);
        Assert.Equal("<TITLE>", s.Title);   // QC _("<TITLE>")
        Assert.Equal("<AUTHOR>", s.Author); // QC _("<AUTHOR>")
    }

    // -------------------------------------------------------------------------------------------------
    //  MapInfoBackend — mapinfo.qc parser + loadMapInfo
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void MapInfo_Parses_Title_Author_Description_And_Gametypes()
    {
        const string mapinfo =
            "title Storm Keep\n" +
            "author FruitieX\n" +
            "description A medieval fortress\n" +
            "gametype dm\n" +
            "gametype ctf weapons\n" +   // extra tokens after the mode are ignored
            "// a comment\n" +
            "has weapons\n";

        var backend = new MapInfoBackend(
            p => p == "maps/stormkeep.mapinfo" ? mapinfo : null,
            _ => false);

        MapInfo info = backend.Get("stormkeep");
        Assert.True(info.HasMapInfoFile);
        Assert.Equal("Storm Keep", info.Title);
        Assert.Equal("FruitieX", info.Author);
        Assert.Equal("A medieval fortress", info.Description);
        Assert.True(info.Supports("dm"));
        Assert.True(info.Supports("ctf"));
        Assert.False(info.Supports("ka"));
    }

    [Fact]
    public void MapInfo_Resolves_Deprecated_And_Mismatched_Gametype_Names()
    {
        // mapinfo.qc:624-639 deprecated remaps + the port's "ft" ↔ "freezetag" fixup.
        Assert.Equal("nb", MapInfoBackend.ResolveGametype("nexball"));
        Assert.Equal("ka", MapInfoBackend.ResolveGametype("keepaway"));
        Assert.Equal("dm", MapInfoBackend.ResolveGametype("ffa"));
        Assert.Equal("ctf", MapInfoBackend.ResolveGametype("oneflag"));
        Assert.Equal("duel", MapInfoBackend.ResolveGametype("tourney"));
        Assert.Equal("freezetag", MapInfoBackend.ResolveGametype("freezetag")); // remaps to "ft", then to port NetName
        Assert.Equal("freezetag", MapInfoBackend.ResolveGametype("ft"));        // the short name directly
        Assert.Equal("dm", MapInfoBackend.ResolveGametype("dm"));               // canonical passes through
        Assert.Null(MapInfoBackend.ResolveGametype(""));
    }

    [Fact]
    public void MapInfo_Legacy_Type_Keyword_Adds_Gametype()
    {
        var backend = new MapInfoBackend(
            _ => "type dm 30 20\ntype ctf 0 20\n", // legacy "type" keyword (mapinfo.qc:1216)
            _ => false);
        MapInfo info = backend.Get("oldmap");
        Assert.True(info.Supports("dm"));
        Assert.True(info.Supports("ctf"));
    }

    [Fact]
    public void MapInfo_Missing_File_Falls_Back_To_Bsp_Name()
    {
        var backend = new MapInfoBackend(_ => null, _ => false);
        MapInfo info = backend.Get("nomapinfo");
        Assert.False(info.HasMapInfoFile);
        Assert.Equal("nomapinfo", info.Title); // QC: titlestring uses the bspname when title is "<TITLE>"
        Assert.Equal("", info.Author);          // "<AUTHOR>" is not displayed (mapinfo.qc:1372-1373)
        Assert.Empty(info.SupportedGametypes);
    }

    [Fact]
    public void MapInfo_TitleSansAuthor_Promotes_Author_When_Unset()
    {
        // A "Foo by Bar" title with no explicit author → author becomes "Bar", title becomes "Foo".
        var backend = new MapInfoBackend(_ => "title Glow Plant by tZork\n", _ => false);
        MapInfo info = backend.Get("glowplant");
        Assert.Equal("Glow Plant", info.Title);
        Assert.Equal("tZork", info.Author);
    }

    [Fact]
    public void MapInfo_Decolorizes_Title_And_Author()
    {
        var backend = new MapInfoBackend(_ => "title ^2Green^7 Map\nauthor ^1Red^7Guy\n", _ => false);
        MapInfo info = backend.Get("colored");
        Assert.Equal("Green Map", info.Title);
        Assert.Equal("RedGuy", info.Author);
    }

    [Fact]
    public void MapInfo_Preview_Fallback_Chain()
    {
        // 1) /maps/<bsp> exists → primary
        var b1 = new MapInfoBackend(_ => null, v => v == "/maps/foo");
        Assert.Equal("/maps/foo", b1.PreviewImage("foo"));

        // 2) only /levelshots/<bsp> exists → Quake-3 fallback
        var b2 = new MapInfoBackend(_ => null, v => v == "/levelshots/foo");
        Assert.Equal("/levelshots/foo", b2.PreviewImage("foo"));

        // 3) neither → nopreview_map placeholder
        var b3 = new MapInfoBackend(_ => null, _ => false);
        Assert.Equal("nopreview_map", b3.PreviewImage("foo"));
    }

    [Fact]
    public void MapInfo_Caches_Parsed_Result()
    {
        int reads = 0;
        var backend = new MapInfoBackend(
            p => { reads++; return "title Cached\ngametype dm\n"; },
            _ => false);
        backend.Get("m");
        backend.Get("m");
        Assert.Equal(1, reads); // second Get hits the cache (QC MapInfo_Cache_Retrieve)
    }

    [Fact]
    public void MapInfo_ForcedDuelOnDmMap_Default_Active()
    {
        // QC Duel.m_isForcedSupported (duel.qh:15-28): a map with "gametype dm" but NO "gametype duel"
        // is auto-promoted to also support duel when g_duel_not_dm_maps=0 (default).
        // MapInfoBackend.ForceDuelOnDmMaps defaults to true (matching the Base default).
        var backend = new MapInfoBackend(
            _ => "title Boil\ngametype dm\ngametype tdm\n",
            _ => false);
        // default: ForceDuelOnDmMaps = true
        MapInfo info = backend.Get("boil");
        Assert.True(info.Supports("dm"));
        Assert.True(info.Supports("duel"));   // forced-in by m_isForcedSupported
        Assert.True(info.Supports("tdm"));
    }

    [Fact]
    public void MapInfo_ForcedDuelOnDmMap_Suppressed_By_Flag()
    {
        // When g_duel_not_dm_maps=1 (ForceDuelOnDmMaps=false), duel must NOT be auto-added for DM maps.
        var backend = new MapInfoBackend(
            _ => "title Boil\ngametype dm\ngametype tdm\n",
            _ => false)
        {
            ForceDuelOnDmMaps = false, // g_duel_not_dm_maps 1
        };
        MapInfo info = backend.Get("boil");
        Assert.True(info.Supports("dm"));
        Assert.False(info.Supports("duel"));  // not forced — g_duel_not_dm_maps is set
    }

    [Fact]
    public void MapInfo_ForcedDuelOnDmMap_NotAdded_When_Explicit()
    {
        // If the .mapinfo already lists "gametype duel", the forced-support logic is a no-op (HashSet → no dup).
        var backend = new MapInfoBackend(
            _ => "title Darkzone\ngametype dm\ngametype duel\n",
            _ => false);
        MapInfo info = backend.Get("darkzone");
        Assert.True(info.Supports("dm"));
        Assert.True(info.Supports("duel")); // explicitly listed — still present after forced-support pass
    }

    [Fact]
    public void MapInfo_ForcedDuelOnDmMap_NonDmMap_Unaffected()
    {
        // A map without DM (e.g. CTF-only) must NOT get duel forced in.
        var backend = new MapInfoBackend(
            _ => "title Dance\ngametype ctf\n",
            _ => false);
        MapInfo info = backend.Get("dance");
        Assert.False(info.Supports("dm"));
        Assert.False(info.Supports("duel"));  // CTF-only map → no forced duel
    }

    [Fact]
    public void MapInfo_ForcedDuelOnDmMap_Suppressed_When_DiameterTooLarge()
    {
        // QC Duel.m_isAlwaysSupported diameter guard (mirrored by MapInfoBackend.ApplyForcedGametypes +
        // MapInfo.Diameter, MapInfoBackend.cs:51-60): a "size" line whose bounding diameter >= 3250
        // suppresses forced duel even on a DM map. This mirrors the implosion case — gametype dm, no
        // gametype duel, size line giving diameter ~8874 (vlen of (6336, 5888, 1984)).
        var backend = new MapInfoBackend(
            _ => "title Implosion\ngametype dm\nsize -960 -5888 -576 5376 0 1408\n",
            _ => false);
        MapInfo info = backend.Get("implosion");
        Assert.True(info.Supports("dm"));
        Assert.NotNull(info.Diameter);              // size line was parsed
        Assert.True(info.Diameter!.Value >= 3250f); // well above the duel threshold
        Assert.False(info.Supports("duel"));        // forced-duel suppressed by the diameter gate
    }

    [Fact]
    public void MapInfo_ForcedDuelOnDmMap_AllowedWhenDiameterSmall()
    {
        // The other branch of the diameter guard (MapInfoBackend.cs:51-60): a small DM map whose size
        // line gives a diameter below 3250 still gets forced duel. Bounds (1000, 1000, 600) → ~1536.
        var backend = new MapInfoBackend(
            _ => "title Tiny\ngametype dm\nsize -500 -500 -300 500 500 300\n",
            _ => false);
        MapInfo info = backend.Get("tiny");
        Assert.True(info.Supports("dm"));
        Assert.NotNull(info.Diameter);              // size line was parsed
        Assert.True(info.Diameter!.Value < 3250f);  // below threshold
        Assert.True(info.Supports("duel"));         // small map → forced duel still allowed
    }

    // -------------------------------------------------------------------------------------------------
    //  MenuTextFormat — strdecolorize + glob
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Decolorize_Strips_Numeric_And_Hex_Codes()
    {
        Assert.Equal("plain", MenuTextFormat.Decolorize("^1pl^2ain"));
        Assert.Equal("hex", MenuTextFormat.Decolorize("^xf00hex"));
        Assert.Equal("^caret", MenuTextFormat.Decolorize("^^caret")); // ^^ → literal ^
        Assert.Equal("none", MenuTextFormat.Decolorize("none"));
    }

    [Theory]
    [InlineData("*foo*", "barfoobaz", true)]
    [InlineData("*foo*", "barbaz", false)]
    [InlineData("duel_?", "duel_1", true)]
    [InlineData("duel_?", "duel_12", false)]
    [InlineData("EXACT", "exact", true)]   // case-insensitive
    [InlineData("", "anything", true)]     // empty pattern matches
    public void GlobMatch_Wildcards(string pattern, string text, bool expected)
        => Assert.Equal(expected, MenuTextFormat.GlobMatch(text, pattern));
}
