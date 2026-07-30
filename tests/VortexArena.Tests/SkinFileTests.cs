using XonoticGodot.Formats.Sidecars;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first coverage for <see cref="SkinFile"/> (port of Darkplaces <c>Mod_LoadSkinFiles</c>,
/// model_shared.c). Pins both accepted line syntaxes (DP <c>replace "mesh" "shader"</c> and Quake3
/// CSV <c>mesh,shader</c>), tag lines (<c>tag_*,</c> in file order), last-write-wins on duplicate
/// mesh names, the shader value kept VERBATIM (image extension NOT stripped — DP strips it at texture
/// resolve time, not parse time), the nodraw sentinels, comment stripping, and the 10-word overflow
/// guard. Pure text — always runs, no assets.
/// </summary>
public class SkinFileTests
{
    [Fact]
    public void ReplaceCommand_MapsQuotedMeshToShader()
    {
        SkinFile skin = SkinFile.Parse("replace \"upper\" \"models/player/erebus_upper\"");
        Assert.Equal("models/player/erebus_upper", skin.MeshToTexture["upper"]);
        Assert.Empty(skin.Unrecognized);
    }

    [Fact]
    public void Quake3Csv_MapsMeshToShader()
    {
        SkinFile skin = SkinFile.Parse("head,models/player/erebus_head\ntorso,models/player/erebus_torso");
        Assert.Equal("models/player/erebus_head", skin.MeshToTexture["head"]);
        Assert.Equal("models/player/erebus_torso", skin.MeshToTexture["torso"]);
    }

    [Fact]
    public void CsvWithSpacesAroundComma_StillParses()
    {
        SkinFile skin = SkinFile.Parse("head , models/x");
        Assert.Equal("models/x", skin.MeshToTexture["head"]);
    }

    [Fact]
    public void ShaderValue_KeptVerbatim_ExtensionNotStripped()
    {
        // The companion-extension handling happens at texture-resolve time (see the WireCompanions fix);
        // the PARSER must keep the value exactly as written.
        SkinFile skin = SkinFile.Parse("body,models/thing/skin.tga");
        Assert.Equal("models/thing/skin.tga", skin.MeshToTexture["body"]);
    }

    [Fact]
    public void DuplicateMeshName_LastWriteWins()
    {
        SkinFile skin = SkinFile.Parse("mesh1,first\nreplace mesh1 second\nmesh1,third");
        Assert.Equal("third", skin.MeshToTexture["mesh1"]);
    }

    [Fact]
    public void EmptyShaderAfterComma_RecordsEmptyMapping()
    {
        SkinFile skin = SkinFile.Parse("mesh1,");
        Assert.True(skin.MeshToTexture.ContainsKey("mesh1"));
        Assert.Equal(string.Empty, skin.MeshToTexture["mesh1"]);
    }

    [Fact]
    public void TagLines_RecordedInFileOrder()
    {
        SkinFile skin = SkinFile.Parse("tag_head,\ntag_weapon,\ntag_torso,alias");
        Assert.Equal(new[] { "tag_head", "tag_weapon", "tag_torso" }, skin.TagOrder);
        Assert.Equal(string.Empty, skin.TagAliases["tag_head"]);
        Assert.Equal("alias", skin.TagAliases["tag_torso"]);
        // tag lines are NOT mesh remaps
        Assert.False(skin.MeshToTexture.ContainsKey("tag_head"));
    }

    [Fact]
    public void Comments_AreStripped()
    {
        SkinFile skin = SkinFile.Parse(
            "// whole-line comment\n" +
            "head,models/x // trailing comment\n" +
            "torso,/* inline */models/y\n");
        Assert.Equal("models/x", skin.MeshToTexture["head"]);
        Assert.Equal("models/y", skin.MeshToTexture["torso"]);
        Assert.Empty(skin.Unrecognized);
    }

    [Fact]
    public void UnclassifiableLines_GoToUnrecognized_NeverThrow()
    {
        SkinFile skin = SkinFile.Parse("just_a_word\nreplace too few\nanother bare line");
        // "replace too few" has 3 words and IS a valid replace; "replace" with wrong arity is unrecognized
        Assert.Equal("few", skin.MeshToTexture["too"]);
        Assert.Contains("just_a_word", skin.Unrecognized);
        Assert.Contains("another bare line", skin.Unrecognized);
    }

    [Fact]
    public void ReplaceWithWrongArity_IsUnrecognized()
    {
        SkinFile skin = SkinFile.Parse("replace onlyone");
        Assert.Empty(skin.MeshToTexture);
        Assert.Single(skin.Unrecognized);
    }

    [Fact]
    public void MoreThanTenWords_LineIsDropped()
    {
        // DP's word[10] overflow guard skips the line entirely.
        SkinFile skin = SkinFile.Parse("a b c d e f g h i j k");
        Assert.Empty(skin.MeshToTexture);
        Assert.Single(skin.Unrecognized);
    }

    [Fact]
    public void IsNoDraw_MatchesBothSentinels_CaseInsensitive()
    {
        Assert.True(SkinFile.IsNoDraw("common/nodraw"));
        Assert.True(SkinFile.IsNoDraw("textures/common/nodraw"));
        Assert.True(SkinFile.IsNoDraw("Common/NoDraw"));
        Assert.False(SkinFile.IsNoDraw("models/player/skin"));
        Assert.False(SkinFile.IsNoDraw(""));
    }

    [Fact]
    public void EmptyAndBlankInput_ParseToEmptySkin()
    {
        Assert.Empty(SkinFile.Parse("").MeshToTexture);
        SkinFile blank = SkinFile.Parse("\n\n   \n\t\n");
        Assert.Empty(blank.MeshToTexture);
        Assert.Empty(blank.Unrecognized);
    }

    [Fact]
    public void CrLfAndLoneCrLineEndings_AreTolerated()
    {
        SkinFile skin = SkinFile.Parse("a,one\r\nb,two\rc,three");
        Assert.Equal("one", skin.MeshToTexture["a"]);
        Assert.Equal("two", skin.MeshToTexture["b"]);
        Assert.Equal("three", skin.MeshToTexture["c"]);
    }

    [Fact]
    public void RealWorldShape_MixedSkinFile()
    {
        // shape of a real Xonotic player .skin: CSV remaps + a nodraw + tag lines
        SkinFile skin = SkinFile.Parse(
            "tag_head,\n" +
            "head,models/player/erebus_head\n" +
            "helmet,common/nodraw\n" +
            "upper,models/player/erebus_upper\n");
        Assert.Equal(3, skin.MeshToTexture.Count);
        Assert.True(SkinFile.IsNoDraw(skin.MeshToTexture["helmet"]));
        Assert.Single(skin.TagOrder);
    }
}
