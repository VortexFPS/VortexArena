using System.IO;
using XonoticGodot.Common.Localization;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises <see cref="SkinValues"/> — the Godot-free port of skin.qh's <c>SKIN*</c> table + the
/// <c>Skin_ApplySetting</c> switch + the skinvalues.txt value-substring parse (menu.qc m_init_delayed). The case
/// label is the BARE key (no SKIN prefix); vectors parse via stov, floats via stof, strings stored raw;
/// <c>""</c>/<c>//</c>/unknown keys are ignored; the value substring keeps multi-word vectors verbatim. Pure
/// logic, no GlobalState.
/// </summary>
public class SkinValuesTests
{
    // ---- schema defaults (skin-customizables.inc) --------------------------------------------------------

    [Fact]
    public void Schema_Defaults_Have_The_Right_Type_And_Value()
    {
        var s = new SkinValues();
        Assert.Equal(12f, s.Float("FONTSIZE_NORMAL"));
        Assert.Equal(16f, s.Float("FONTSIZE_TITLE"));
        Assert.Equal(new SkinVec(1, 1, 1), s.Vector("COLOR_TEXT"));
        Assert.Equal(new SkinVec(0, 0, 1), s.Vector("COLOR_LISTBOX_SELECTED")); // Generic blue
        Assert.Equal("tooltip", s.Str("GFX_TOOLTIP"));
        Assert.Equal("border", s.Str("GFX_DIALOGBORDER"));
        Assert.Equal(0.7f, s.Float("ALPHA_TEXT"));
        Assert.Equal(16f, s.Float("WIDTH_SCROLLBAR"));
    }

    [Fact]
    public void Schema_Covers_The_Whole_Range_Of_Keys()
    {
        var s = new SkinValues();
        // first and last keys of the .inc, plus a few colour/string/float keys across the file.
        Assert.True(s.HasFloat("FONTSIZE_NORMAL"));        // first key
        Assert.True(s.HasFloat("WIDTH_SLIDERTEXT"));       // last key
        Assert.True(s.HasVector("COLOR_DIALOG_HUDCONFIRM"));
        Assert.True(s.HasVector("POSITION_DIALOG_QUIT"));
        Assert.True(s.HasString("GFX_CURSOR"));
        Assert.True(s.HasVector("ALPHAS_MAINMENU"));
        Assert.True(s.HasVector("COLOR_SKINLIST_AUTHOR"));
    }

    [Fact]
    public void Vector_Defaults_Are_Component_Exact()
    {
        var s = new SkinValues();
        Assert.Equal(new SkinVec(0.7f, 0.7f, 1f), s.Vector("COLOR_DIALOG_FIRSTRUN"));
        Assert.Equal(new SkinVec(1f, 0.7f, 0.7f), s.Vector("COLOR_DIALOG_WEAPONS"));
        Assert.Equal(new SkinVec(1f, 0f, 0f), s.Vector("COLOR_DIALOG_QUIT"));
        Assert.Equal(new SkinVec(0.4f, 0.4f, 0.7f), s.Vector("COLOR_MAPLIST_AUTHOR"));
        Assert.Equal(new SkinVec(32, 32, 0), s.Vector("SIZE_CURSOR"));
    }

    // ---- ApplySetting (Skin_ApplySetting) ----------------------------------------------------------------

    [Fact]
    public void ApplySetting_Vector_Uses_Bare_Key_And_Stov()
    {
        var s = new SkinValues();
        s.ApplySetting("COLOR_TEXT", "0.9 0.5 0.1"); // bare key (no SKIN prefix), bare vector
        Assert.Equal(new SkinVec(0.9f, 0.5f, 0.1f), s.Vector("COLOR_TEXT"));
        Assert.True(s.IsOverridden("COLOR_TEXT"));
    }

    [Fact]
    public void ApplySetting_Float_Uses_Stof()
    {
        var s = new SkinValues();
        s.ApplySetting("FONTSIZE_NORMAL", "14");
        Assert.Equal(14f, s.Float("FONTSIZE_NORMAL"));
        Assert.True(s.IsOverridden("FONTSIZE_NORMAL"));
    }

    [Fact]
    public void ApplySetting_String_Is_Stored_Raw()
    {
        var s = new SkinValues();
        s.ApplySetting("GFX_TOOLTIP", "mytooltip");
        Assert.Equal("mytooltip", s.Str("GFX_TOOLTIP"));
    }

    [Theory]
    [InlineData("")]     // case "": break;
    [InlineData("//")]   // case "//": break;
    public void ApplySetting_Empty_And_Comment_Keys_Are_Ignored(string key)
    {
        var s = new SkinValues();
        s.ApplySetting(key, "anything"); // must not throw, must not store
        Assert.False(s.HasFloat(key));
        Assert.False(s.HasVector(key));
        Assert.False(s.HasString(key));
    }

    [Fact]
    public void ApplySetting_Unknown_Key_Is_Dropped()
    {
        var s = new SkinValues();
        s.ApplySetting("NOT_A_REAL_KEY", "1 2 3"); // default: LOG_TRACE + drop
        Assert.False(s.HasFloat("NOT_A_REAL_KEY"));
        Assert.False(s.HasVector("NOT_A_REAL_KEY"));
        Assert.False(s.HasString("NOT_A_REAL_KEY"));
    }

    [Fact]
    public void Stov_Accepts_Quoted_And_Bare_Vectors_And_Fills_Missing_With_Zero()
    {
        Assert.Equal(new SkinVec(1, 0.7f, 0.7f), SkinValues.Stov("1 0.7 0.7"));
        Assert.Equal(new SkinVec(1, 0.7f, 0.7f), SkinValues.Stov("'1 0.7 0.7'"));
        Assert.Equal(new SkinVec(1, 2, 0), SkinValues.Stov("1 2"));     // missing z -> 0
        Assert.Equal(SkinVec.Zero, SkinValues.Stov("garbage"));
    }

    // ---- Load (skinvalues.txt value-substring semantics) -------------------------------------------------

    [Fact]
    public void Load_Keeps_Multiword_Vector_Verbatim()
    {
        var s = new SkinValues();
        s.Load("COLOR_DIALOG_WEAPONS            1 0.7 0.7\n");
        Assert.Equal(new SkinVec(1, 0.7f, 0.7f), s.Vector("COLOR_DIALOG_WEAPONS"));
    }

    [Fact]
    public void Load_Overrides_Beat_Schema_Default_Absent_Keys_Keep_Default()
    {
        var s = new SkinValues();
        s.Load("FONTSIZE_NORMAL                16\nCOLOR_TEXT                     0.9 0.5 0.1\n");
        Assert.Equal(16f, s.Float("FONTSIZE_NORMAL"));                   // overridden
        Assert.Equal(new SkinVec(0.9f, 0.5f, 0.1f), s.Vector("COLOR_TEXT")); // overridden
        Assert.Equal(16f, s.Float("FONTSIZE_TITLE"));                   // absent -> schema default kept
        Assert.False(s.IsOverridden("FONTSIZE_TITLE"));
        Assert.True(s.IsOverridden("FONTSIZE_NORMAL"));
    }

    [Fact]
    public void Load_Skips_Title_And_Author_Lines()
    {
        var s = new SkinValues();
        s.Load("title Generic\nauthor Morphed\nFONTSIZE_NORMAL 20\n");
        Assert.Equal(20f, s.Float("FONTSIZE_NORMAL"));
        // "title"/"author" are not keys -> not stored as anything.
        Assert.False(s.IsOverridden("title"));
    }

    [Fact]
    public void Load_Tolerates_Tabs_And_Trailing_Whitespace()
    {
        var s = new SkinValues();
        s.Load("COLOR_HEADER\t0.2 0.3 0.4   \n");
        Assert.Equal(new SkinVec(0.2f, 0.3f, 0.4f), s.Vector("COLOR_HEADER"));
    }

    [Fact]
    public void SplitKeyValue_Matches_The_Value_Substring_Rule()
    {
        Assert.True(SkinValues.SplitKeyValue("KEY   1 0.7 0.7   ", out string k, out string v));
        Assert.Equal("KEY", k);
        Assert.Equal("1 0.7 0.7", v); // internal spaces preserved, trailing trimmed (argv_end_index(-1))

        Assert.False(SkinValues.SplitKeyValue("LONELYKEY", out _, out _)); // < 2 tokens
        Assert.False(SkinValues.SplitKeyValue("   ", out _, out _));        // blank
    }

    [Fact]
    public void Real_Luma_Skinvalues_Overlays_The_Schema()
    {
        // Proof against the shipped luma skin: loading its skinvalues.txt overrides some keys away from the
        // Generic defaults. Guarded so CI without the data checkout still passes.
        string? path = FindLumaSkinValues();
        if (path is null)
            return;
        var s = new SkinValues();
        s.Load(File.ReadAllText(path));
        // luma overrides the text colour away from the Generic '1 1 1' to its blue-white (any override is enough
        // to prove the overlay path); COLOR_TEXT is one of the keys luma sets.
        Assert.True(s.IsOverridden("COLOR_TEXT") || s.IsOverridden("COLOR_LISTBOX_SELECTED"),
            "expected luma to override at least one palette key");
    }

    private static string? FindLumaSkinValues()
    {
        string[] roots =
        {
            @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir",
            @"C:\Users\Bryan\Projects\Xonotic\Base\data\xonotic-data.pk3dir",
        };
        foreach (string r in roots)
        {
            string p = Path.Combine(r, "gfx", "menu", "luma", "skinvalues.txt");
            if (File.Exists(p))
                return p;
        }
        return null;
    }
}
