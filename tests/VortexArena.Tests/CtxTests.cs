using XonoticGodot.Common.Localization;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises <see cref="Ctx"/> — the Godot-free port of i18n.qh's <c>CTX</c> (the msgctxt-prefix strip) and the
/// en/""/dump identity rule. The CTX prefix lives IN the .po msgid and is stripped only for display, AFTER the
/// translation lookup. Pure logic, no GlobalState.
/// </summary>
public class CtxTests
{
    [Fact]
    public void Strips_Context_Prefix()
    {
        // _("GAMETYPE^Deathmatch") translates the whole literal, displays "Deathmatch".
        Assert.Equal("Deathmatch", Ctx.Strip("GAMETYPE^Deathmatch"));
    }

    [Fact]
    public void Translated_Prefixed_String_Strips_The_Translated_Prefix()
    {
        // After translation the prefix may itself be translated (GAMETYPE^ -> SPIELTYP^); strip removes it.
        Assert.Equal("%s:", Ctx.Strip("SPIELTYP^%s:"));
    }

    [Fact]
    public void No_Caret_Returns_Unchanged()
    {
        Assert.Equal("Deathmatch", Ctx.Strip("Deathmatch"));
    }

    [Theory]
    [InlineData("^1Red")]   // caret at index 1 -> one-char prefix is invalid (color code)
    [InlineData("^Foo")]    // caret at index 0 -> empty prefix is invalid
    public void Color_Code_Style_Carets_Are_Left_Unchanged(string s)
    {
        // i18n.qh: caret_ofs must be > 1; a leading color code (^1, ^x...) is not a context prefix.
        Assert.Equal(s, Ctx.Strip(s));
    }

    [Fact]
    public void Space_Before_Caret_Means_Color_Code_Not_Context()
    {
        // i18n.qh space_ofs guard: a space inside the candidate prefix means the caret is part of a color code.
        Assert.Equal("has space^x", Ctx.Strip("has space^x"));
    }

    [Fact]
    public void Caret_After_Multichar_Prefix_With_No_Space_Strips()
    {
        Assert.Equal("text", Ctx.Strip("CTX^text"));
        Assert.Equal("Right", Ctx.Strip("DIRECTION^Right"));
    }

    [Fact]
    public void Empty_And_Null_Pass_Through()
    {
        Assert.Equal("", Ctx.Strip(""));
        Assert.Null(Ctx.Strip(null!));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("en", true)]
    [InlineData("dump", true)]
    [InlineData("de", false)]
    [InlineData("zh_CN", false)]
    public void IsIdentityLanguage_Matches_The_QC_Rule(string lang, bool expected)
    {
        // language_filename: lang == "" || "en" || "dump" -> no translation (identity).
        Assert.Equal(expected, Ctx.IsIdentityLanguage(lang));
        Assert.True(Ctx.IsIdentityLanguage(null)); // null is treated as empty
    }
}
