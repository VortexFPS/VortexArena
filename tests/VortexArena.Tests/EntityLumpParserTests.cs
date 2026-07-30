using System.Linq;
using XonoticGodot.Formats.Bsp;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first direct coverage for <see cref="EntityLumpParser"/> (the BSP lump-0 text parser, mirror of
/// Darkplaces <c>Mod_Q3BSP_LoadEntities</c> + <c>COM_ParseToken_Simple</c> with parsebackslash=false).
/// Pins: quoted key/value tokens, <c>//</c> and <c>/* */</c> comment skipping, literal backslashes kept
/// (NOT unescaped), duplicate-key-last-wins, stray-token tolerance, quoted braces treated as data, and
/// truncation behavior at EOF. Pure text — always runs, no assets.
/// </summary>
public class EntityLumpParserTests
{
    [Fact]
    public void SingleEntity_QuotedPairs()
    {
        var ents = EntityLumpParser.Parse("{ \"classname\" \"worldspawn\" \"message\" \"Hello World\" }");

        Assert.Single(ents);
        Assert.Equal("worldspawn", ents[0]["classname"]);
        Assert.Equal("Hello World", ents[0]["message"]);
    }

    [Fact]
    public void MultipleEntities_InOrder()
    {
        var ents = EntityLumpParser.Parse(
            "{ \"classname\" \"worldspawn\" }\n" +
            "{ \"classname\" \"info_player_deathmatch\" \"origin\" \"0 64 24\" }\n" +
            "{ \"classname\" \"item_armor_big\" }");

        Assert.Equal(3, ents.Count);
        Assert.Equal("worldspawn", ents[0]["classname"]);
        Assert.Equal("info_player_deathmatch", ents[1]["classname"]);
        Assert.Equal("0 64 24", ents[1]["origin"]);
        Assert.Equal("item_armor_big", ents[2]["classname"]);
    }

    [Fact]
    public void LineAndBlockComments_AreSkipped()
    {
        var ents = EntityLumpParser.Parse(
            "// leading line comment\n" +
            "{ /* block comment */ \"classname\" // trailing comment\n" +
            "\"worldspawn\" /* multi\nline\nblock */ \"gravity\" \"800\" }");

        Assert.Single(ents);
        Assert.Equal("worldspawn", ents[0]["classname"]);
        Assert.Equal("800", ents[0]["gravity"]);
    }

    [Fact]
    public void Backslashes_AreKeptLiteral()
    {
        // DP parses the entity lump with parsebackslash=false: a literal '\' stays in the value.
        var ents = EntityLumpParser.Parse("{ \"model\" \"maps\\\\thing.md3\" \"note\" \"a\\nb\" }");

        Assert.Equal(@"maps\\thing.md3", ents[0]["model"]);
        Assert.Equal(@"a\nb", ents[0]["note"]); // NOT a newline
    }

    [Fact]
    public void DuplicateKey_LastWins()
    {
        var ents = EntityLumpParser.Parse("{ \"wait\" \"1\" \"wait\" \"5\" }");
        Assert.Equal("5", ents[0]["wait"]);
    }

    [Fact]
    public void UnderscorePrefixedKeys_ArePreserved()
    {
        // Unlike DP's worldspawn-only quick parse (which strips '_'), the full parse keeps the key as-is.
        var ents = EntityLumpParser.Parse("{ \"_deluxeMaps\" \"1\" }");
        Assert.True(ents[0].ContainsKey("_deluxeMaps"));
        Assert.Equal("1", ents[0]["_deluxeMaps"]);
    }

    [Fact]
    public void StrayTokensBeforeBrace_AreTolerated()
    {
        var ents = EntityLumpParser.Parse("junk tokens here { \"classname\" \"worldspawn\" }");
        Assert.Single(ents);
        Assert.Equal("worldspawn", ents[0]["classname"]);
    }

    [Fact]
    public void QuotedBraces_AreDataNotStructure()
    {
        var ents = EntityLumpParser.Parse("{ \"message\" \"}\" \"classname\" \"worldspawn\" }");
        Assert.Single(ents);
        Assert.Equal("}", ents[0]["message"]);
        Assert.Equal("worldspawn", ents[0]["classname"]);
    }

    [Fact]
    public void EmptyValue_IsKept()
    {
        var ents = EntityLumpParser.Parse("{ \"noise\" \"\" }");
        Assert.Equal("", ents[0]["noise"]);
    }

    [Fact]
    public void TruncatedEntityAtEof_ReturnsCompletedEntitiesOnly()
    {
        var ents = EntityLumpParser.Parse(
            "{ \"classname\" \"worldspawn\" }\n{ \"classname\" \"light\" \"origin\"");
        // the second entity is cut mid-pair; only the complete one is returned
        Assert.Single(ents);
        Assert.Equal("worldspawn", ents[0]["classname"]);
    }

    [Fact]
    public void EmptyAndNullText_ParseToNothing()
    {
        Assert.Empty(EntityLumpParser.Parse(""));
        Assert.Empty(EntityLumpParser.Parse("   \n\t  "));
        Assert.Empty(EntityLumpParser.Parse("// only a comment"));
    }

    [Fact]
    public void BareWordTokens_AreAcceptedAsKeysOrValues()
    {
        // Keys/values are always quoted in real maps, but the tokenizer's bare-word branch (stops only at
        // whitespace, like DP's "regular word") must still pair them up.
        var ents = EntityLumpParser.Parse("{ classname worldspawn }");
        Assert.Single(ents);
        Assert.Equal("worldspawn", ents[0]["classname"]);
    }

    [Fact]
    public void RealWorldShape_WorldspawnFirstThenPointEntities()
    {
        // the conventional Xonotic lump shape: worldspawn first, then point entities with origins/angles
        const string lump = "{\n\"classname\" \"worldspawn\"\n\"message\" \"Test Arena\"\n\"_description\" \"x\"\n}\n" +
                            "{\n\"origin\" \"-192 512 96\"\n\"angle\" \"135\"\n\"classname\" \"info_player_deathmatch\"\n}\n";
        var ents = EntityLumpParser.Parse(lump);

        Assert.Equal(2, ents.Count);
        Assert.Equal("worldspawn", ents[0]["classname"]);
        Assert.Equal(3, ents[0].Count);
        Assert.Equal("135", ents[1]["angle"]);
        Assert.Equal(3, ents.Skip(1).Single().Count);
    }
}
