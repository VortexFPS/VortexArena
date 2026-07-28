using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers <see cref="MapInfo"/> (phase E8) — the <c>.mapinfo</c> reader and writer the port never had.
///
/// The constraint that shapes the whole class is ROUND-TRIPPING. These are hand-edited files in a map's source
/// tree, so an editor that reads one and writes it back must not delete a directive it did not model or
/// reorder a mapper's gametype list. The "unrecognised lines survive" tests below are the ones holding that
/// down, because that failure is silent: you would only notice when the map stopped behaving.
/// </summary>
public class MapInfoTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    private const string Fuse = """
        title Fuse
        description Duel Mapping Contest Winner of 2012
        author Ferdinand 'cityy' List
        cdtrack 4
        has weapons
        gametype dm
        gametype tdm
        gametype duel

        """;

    // ---------------------------------------------------------------- reading

    [Fact]
    public void ReadsTheIdentityFields()
    {
        MapInfo m = MapInfo.Parse(Fuse);
        Assert.Equal("Fuse", m.Title);
        Assert.Equal("Duel Mapping Contest Winner of 2012", m.Description);
        Assert.Equal("Ferdinand 'cityy' List", m.Author);
        Assert.Equal("4", m.CdTrack);
    }

    [Fact]
    public void ReadsGametypesInFileOrder()
    {
        MapInfo m = MapInfo.Parse(Fuse);
        Assert.Equal(3, m.Gametypes.Count);
        Assert.Equal("dm", m.Gametypes[0].Name);
        Assert.Equal("duel", m.Gametypes[2].Name);
        Assert.True(m.Supports("DUEL"));         // case-insensitive
        Assert.False(m.Supports("ctf"));
    }

    [Fact]
    public void ReadsFeatureDeclarations()
        => Assert.Equal("weapons", Assert.Single(MapInfo.Parse(Fuse).Has));

    /// <summary>A gametype line can carry per-mode cvar overrides after the name.</summary>
    [Fact]
    public void ReadsPerGametypeSettings()
    {
        MapInfo m = MapInfo.Parse("gametype rc timelimit=10 qualifying_timelimit=5\n");
        MapInfoGametype g = Assert.Single(m.Gametypes);

        Assert.Equal("rc", g.Name);
        Assert.Equal(2, g.Settings.Count);
        Assert.Equal("timelimit", g.Settings[0].Key);
        Assert.Equal("10", g.Settings[0].Value);
        Assert.Equal("qualifying_timelimit", g.Settings[1].Key);
    }

    /// <summary>
    /// <c>size</c> reads like a player-count range and is not one: it is six floats giving the lightgrid
    /// bounds, which the shipped files use to stop the minimap coming out mis-scaled. Modelling it as two
    /// integers parsed six real files without complaint and silently dropped the line on write.
    /// </summary>
    [Fact]
    public void ReadsSizeAsMapBounds_NotAPlayerCount()
    {
        MapInfo m = MapInfo.Parse("size -1024 -4672 -1216 1088 4672 1024\n");
        Assert.True(m.HasBounds);
        Assert.Equal(new System.Numerics.Vector3(-1024, -4672, -1216), m.BoundsMin);
        Assert.Equal(new System.Numerics.Vector3(1088, 4672, 1024), m.BoundsMax);
    }

    [Fact]
    public void TheTrailingCommentOnASizeLineSurvives()
    {
        MapInfo m = MapInfo.Parse("size -960 -5888 -576 5376 0 1408 // Bounds of the lightgrid brush\n");
        Assert.Equal("// Bounds of the lightgrid brush", m.BoundsComment);
        Assert.Contains("// Bounds of the lightgrid brush", m.Write());
    }

    [Fact]
    public void BoundsRoundTrip()
    {
        MapInfo m = MapInfo.Parse("size -1024 -4672 -1216 1088 4672 1024\n");
        MapInfo again = MapInfo.Parse(m.Write());
        Assert.True(again.HasBounds);
        Assert.Equal(m.BoundsMin, again.BoundsMin);
        Assert.Equal(m.BoundsMax, again.BoundsMax);
    }

    [Fact]
    public void ReadsFlags()
    {
        MapInfo m = MapInfo.Parse("hidden\nforbidden\nnoautomaplist\n");
        Assert.True(m.Hidden);
        Assert.True(m.Forbidden);
        Assert.True(m.NoAutoMapList);
    }

    [Fact]
    public void ReadsSettempLinesVerbatim()
    {
        MapInfo m = MapInfo.Parse("settemp_for_type all bot_number 0\nsettemp_for_type rc g_pickup_shells 3\n");
        Assert.Equal(2, m.SetTemp.Count);
        Assert.Equal("all bot_number 0", m.SetTemp[0]);
    }

    [Fact]
    public void AMapWithNoGametypes_IsNotPlayable()
        => Assert.False(MapInfo.Parse("title Orphan\n").IsPlayable);

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("garbage line with no verb meaning\n")]
    [InlineData("size not-a-number\n")]
    public void MalformedInput_DoesNotThrow(string bad)
        => Assert.NotNull(MapInfo.Parse(bad));

    // ---------------------------------------------------------------- writing

    [Fact]
    public void WritesBackWhatItRead()
    {
        MapInfo m = MapInfo.Parse(Fuse);
        MapInfo again = MapInfo.Parse(m.Write());

        Assert.Equal(m.Title, again.Title);
        Assert.Equal(m.Description, again.Description);
        Assert.Equal(m.Author, again.Author);
        Assert.Equal(m.CdTrack, again.CdTrack);
        Assert.Equal(m.Gametypes.Count, again.Gametypes.Count);
        Assert.Equal("duel", again.Gametypes[2].Name);
    }

    [Fact]
    public void PerGametypeSettingsSurviveARoundTrip()
    {
        MapInfo m = MapInfo.Parse("gametype rc timelimit=10 qualifying_timelimit=5\n");
        MapInfo again = MapInfo.Parse(m.Write());

        MapInfoGametype g = Assert.Single(again.Gametypes);
        Assert.Equal(2, g.Settings.Count);
        Assert.Equal("10", g.Settings[0].Value);
    }

    /// <summary>
    /// The silent-data-loss guard. A directive this parser does not model must come back out, or the first
    /// time the editor saves a mapinfo it quietly strips whatever it did not expect.
    /// </summary>
    [Fact]
    public void UnrecognisedDirectivesSurviveARoundTrip()
    {
        const string exotic = "title Test\nfog 0.5 0.5 0.5\nsomething_new_in_2027 42\ngametype dm\n";
        MapInfo m = MapInfo.Parse(exotic);
        string written = m.Write();

        Assert.Contains("fog 0.5 0.5 0.5", written);
        Assert.Contains("something_new_in_2027 42", written);
    }

    [Fact]
    public void CommentsSurviveARoundTrip()
    {
        MapInfo m = MapInfo.Parse("// bot_number 0 because the map is tiny\ntitle Test\ngametype dm\n");
        Assert.Contains("// bot_number 0 because the map is tiny", m.Write());
    }

    [Fact]
    public void UndeclaredDirectivesAreNotWritten()
    {
        string written = MapInfo.Parse("title Test\ngametype dm\n").Write();

        Assert.DoesNotContain("author", written);
        Assert.DoesNotContain("cdtrack", written);
        Assert.DoesNotContain("hidden", written);
        Assert.DoesNotContain("size", written);
    }

    // ---------------------------------------------------------------- editing

    [Fact]
    public void AddingAGametypeIsIdempotent()
    {
        MapInfo m = MapInfo.Parse(Fuse);
        Assert.True(m.AddGametype("ctf"));
        Assert.False(m.AddGametype("ctf"));
        Assert.False(m.AddGametype("CTF"));
        Assert.Equal(4, m.Gametypes.Count);
    }

    [Fact]
    public void RemovingAGametypeWorksAndReportsMisses()
    {
        MapInfo m = MapInfo.Parse(Fuse);
        Assert.True(m.RemoveGametype("TDM"));
        Assert.False(m.RemoveGametype("tdm"));
        Assert.Equal(2, m.Gametypes.Count);
    }

    [Fact]
    public void EditedFieldsAppearInTheOutput()
    {
        MapInfo m = MapInfo.Parse(Fuse);
        m.Title = "Fuse Remix";
        m.Author = "somebody else";
        m.AddGametype("ctf");

        MapInfo again = MapInfo.Parse(m.Write());
        Assert.Equal("Fuse Remix", again.Title);
        Assert.Equal("somebody else", again.Author);
        Assert.True(again.Supports("ctf"));
    }

    // ---------------------------------------------------------------- the real files

    /// <summary>
    /// Every shipped .mapinfo must parse, declare at least one gametype, and round-trip without losing a
    /// directive. Thirty real files exercise formatting the samples above do not.
    /// </summary>
    [Fact]
    public void EveryShippedMapInfoRoundTrips()
    {
        string dir = Path.Combine(DataDir, "xonotic-maps.pk3dir", "maps");
        if (!Directory.Exists(dir))
            return;

        string[] files = Directory.GetFiles(dir, "*.mapinfo");
        if (files.Length == 0)
            return;

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            MapInfo m = MapInfo.Parse(text);
            string name = Path.GetFileName(file);

            Assert.True(m.IsPlayable, $"{name} declared no gametype");
            Assert.False(string.IsNullOrWhiteSpace(m.Title), $"{name} has no title");

            MapInfo again = MapInfo.Parse(m.Write());
            Assert.Equal(m.Title, again.Title);
            Assert.Equal(m.Author, again.Author);
            Assert.Equal(m.Description, again.Description);
            Assert.Equal(m.CdTrack, again.CdTrack);
            Assert.Equal(m.Gametypes.Count, again.Gametypes.Count);
            Assert.Equal(m.SetTemp.Count, again.SetTemp.Count);
            Assert.Equal(m.Has.Count, again.Has.Count);

            for (int i = 0; i < m.Gametypes.Count; i++)
            {
                Assert.Equal(m.Gametypes[i].Name, again.Gametypes[i].Name);
                Assert.Equal(m.Gametypes[i].Settings.Count, again.Gametypes[i].Settings.Count);
            }

            // No directive may vanish: every non-blank source line has to be represented somewhere in the
            // output, whether the parser modelled it or carried it through as unrecognised.
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                string verb = line.Split(' ')[0];
                Assert.True(m.Write().Contains(verb, StringComparison.OrdinalIgnoreCase),
                    $"{name}: '{verb}' was dropped on write");
            }
        }
    }
}
