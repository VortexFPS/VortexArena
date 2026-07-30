using System.Collections.Generic;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for <see cref="CampaignCatalog"/> — the menu-side campaign reader. The load surface that matters most
/// is index parity with the server's <see cref="Campaign.Load"/>: the singleplayer screen hands a level's
/// <see cref="CampaignLevel.Index"/> to <c>_campaign_index</c>, so a mismatch would boot the wrong map. These
/// are pure parsing tests (no Api/global state), so they don't need the GlobalState collection.
/// </summary>
public class CampaignCatalogTests
{
    // A faithful slice of maps/campaignxonoticbeta.txt: the header, the four comment lines, two data rows, a
    // COMMENTED-OUT level (transparent to the index, like the real file's rc/cts/nexball rows), then a row
    // AFTER it — so the post-comment index must still line up.
    private const string Sample =
        "//campaign:Xonotic Campaign\n" +
        "//\"game\",\"mapname\",\"bots\",...\n" +
        "//fraglimit: ...\n" +
        "//timelimit: ...\n" +
        "\"dm\",\"boil\",\"5\",\"2\",\"10\",,,\"Deathmatch: Boil\",\"Welcome to Xonotic!\\nWe'll start off easy.\"\n" +
        "\"tdm\",\"stormkeep\",\"5\",\"2\",,\"5\",,\"Team Deathmatch: Stormkeep\",\"Next we'll try 3v3.\"\n" +
        "//\"rc\",\"leave_em_behind\",\"0\",\"0\",\"2400\",,,\"Race\",\"hidden\"\n" +
        "\"dm\",\"solarium\",\"8\",\"6\",\"15\",,\"g_nix 1\",\"[Special] Deathmatch: Solarium\",\"NIX.\"\n";

    [Fact]
    public void Parse_ReadsTitleLevelsAndDescriptions()
    {
        List<CampaignLevel> levels = CampaignCatalog.Parse(Sample, out string title);

        Assert.Equal("Xonotic Campaign", title);
        Assert.Equal(3, levels.Count); // the commented-out rc row is skipped

        Assert.Equal("dm", levels[0].Gametype);
        Assert.Equal("boil", levels[0].MapName);
        Assert.Equal(5, levels[0].Bots);
        Assert.Equal(2, levels[0].BotSkill);
        Assert.Equal("10", levels[0].FragLimit);
        Assert.Equal("Deathmatch: Boil", levels[0].ShortDesc);
        Assert.Contains("Welcome to Xonotic", levels[0].LongDesc);

        // Mutators are carried through for the server to apply as settemps.
        Assert.Equal("g_nix 1", levels[2].Mutators);
        Assert.Equal("solarium", levels[2].MapName);
    }

    [Fact]
    public void Parse_IndexIsTransparentToCommentsAndBlanks()
    {
        List<CampaignLevel> levels = CampaignCatalog.Parse(Sample, out _);

        // boil=0, stormkeep=1; the commented rc row does NOT consume an index, so solarium is 2 (not 3).
        Assert.Equal(0, levels[0].Index);
        Assert.Equal(1, levels[1].Index);
        Assert.Equal(2, levels[2].Index);
    }

    [Theory]
    [InlineData(0, "boil")]
    [InlineData(1, "stormkeep")]
    [InlineData(2, "solarium")]
    public void Parse_IndexMatchesServerCampaignLoad(int index, string expectedMap)
    {
        // The server loads the same file at _campaign_index; the catalog's Index must select the same level.
        var server = new Campaign { FileReader = _ => Sample };
        server.Load(index, 1);
        Assert.Equal(expectedMap, server.CurrentMap);

        List<CampaignLevel> levels = CampaignCatalog.Parse(Sample, out _);
        CampaignLevel byIndex = levels.Find(l => l.Index == index)!;
        Assert.Equal(expectedMap, byIndex.MapName);
    }

    [Fact]
    public void Parse_EmptyOrNullIsEmpty()
    {
        Assert.Empty(CampaignCatalog.Parse(null, out string t1));
        Assert.Equal("", t1);
        Assert.Empty(CampaignCatalog.Parse("", out _));
    }
}
