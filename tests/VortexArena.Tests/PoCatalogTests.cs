using System.IO;
using System.Text;
using XonoticGodot.Common.Localization;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises <see cref="PoCatalog"/> — the Godot-free port of darkplaces' gettext engine
/// (PRVM_PO_ParseString / PRVM_PO_Load / PRVM_PO_Lookup). Pure string parsing, no registry/Api, so no
/// GlobalState collection. Translation is keyed on the ENGLISH SOURCE string (the msgid IS the literal).
/// </summary>
public class PoCatalogTests
{
    [Fact]
    public void Basic_Msgid_Msgstr_Lookup()
    {
        var po = PoCatalog.Parse("msgid \"Hello\"\nmsgstr \"Hallo\"\n");
        Assert.Equal("Hallo", po.Lookup("Hello"));
        Assert.Equal("Hallo", po.Translate("Hello"));
        Assert.Equal(1, po.Count);
    }

    [Fact]
    public void Untranslated_Source_Returns_Self_Or_Null()
    {
        var po = PoCatalog.Parse("msgid \"Hello\"\nmsgstr \"Hallo\"\n");
        Assert.Null(po.Lookup("Missing"));         // PRVM_PO_Lookup returns NULL
        Assert.Equal("Missing", po.Translate("Missing")); // the engine keeps the original global
    }

    [Fact]
    public void Empty_Msgstr_Is_Skipped_As_Untranslated()
    {
        // PRVM_PO_Load:1902 stores only when decodedpos > 0 (a non-empty msgstr). An empty translation is dropped,
        // so a partially-translated language falls through to the English source instead of blanking the UI.
        var po = PoCatalog.Parse("msgid \"Foo\"\nmsgstr \"\"\n");
        Assert.Null(po.Lookup("Foo"));
        Assert.Equal(0, po.Count);
        Assert.Equal("Foo", po.Translate("Foo"));
    }

    [Theory]
    [InlineData("#: qcsrc/x.qc:1")]      // source reference comment
    [InlineData("#, c-format")]          // flag comment
    [InlineData("# plain translator note")]
    public void Comment_Lines_Are_Skipped(string comment)
    {
        var po = PoCatalog.Parse($"{comment}\nmsgid \"A\"\nmsgstr \"B\"\n");
        Assert.Equal("B", po.Lookup("A"));
    }

    [Fact]
    public void C_Escapes_Are_Decoded()
    {
        // \t \n \\ \" — PRVM_PO_ParseString. The C# source escapes once more for the literal here.
        var po = PoCatalog.Parse("msgid \"k\"\nmsgstr \"a\\tb\\nc\\\\d\\\"e\"\n");
        Assert.Equal("a\tb\nc\\d\"e", po.Lookup("k"));
    }

    [Theory]
    [InlineData("\\101", "A")]   // octal 101 = 0x41 = 'A'
    [InlineData("\\012", "\n")]  // octal 012 = newline
    [InlineData("\\7", "\a")]    // single octal digit = bell
    public void Octal_Escapes_Are_Decoded(string octal, string expected)
    {
        var po = PoCatalog.Parse($"msgid \"k\"\nmsgstr \"{octal}\"\n");
        Assert.Equal(expected, po.Lookup("k"));
    }

    [Fact]
    public void Backslash_Before_Other_Char_Is_Literal()
    {
        // The engine's default escape case: `\x` for any other x copies x verbatim.
        var po = PoCatalog.Parse("msgid \"k\"\nmsgstr \"a\\zb\"\n");
        Assert.Equal("azb", po.Lookup("k"));
    }

    [Fact]
    public void Continuation_Lines_Are_Concatenated()
    {
        // The PO header form: msgstr "" followed by several "..." lines, concatenated (PRVM_PO_Load:1873-1894).
        var po = PoCatalog.Parse("msgid \"\"\nmsgstr \"\"\n\"part1 \"\n\"part2\"\n");
        Assert.Equal("part1 part2", po.Lookup(""));
    }

    [Fact]
    public void Continuation_Lines_On_Msgid_Too()
    {
        var po = PoCatalog.Parse("msgid \"\"\n\"long key \"\n\"two\"\nmsgstr \"V\"\n");
        Assert.Equal("V", po.Lookup("long key two"));
    }

    [Fact]
    public void Within_File_Last_Item_Wins()
    {
        // "within file, last item wins" (PRVM_PO_Load:1824). The indexer overwrite mirrors the documented effect.
        var po = PoCatalog.Parse("msgid \"D\"\nmsgstr \"first\"\nmsgid \"D\"\nmsgstr \"second\"\n");
        Assert.Equal("second", po.Lookup("D"));
    }

    [Fact]
    public void Overlay_Order_Common_First_Progs_Second_Progs_Wins()
    {
        // PRVM_PO_Load loads filename2 (common) FIRST, filename (progs) SECOND, so progs overrides common.
        string common = "msgid \"X\"\nmsgstr \"common\"\nmsgid \"Y\"\nmsgstr \"onlyCommon\"\n";
        string progs = "msgid \"X\"\nmsgstr \"progs\"\n";
        var po = PoCatalog.Load(common, progs);
        Assert.Equal("progs", po.Lookup("X"));      // progs wins
        Assert.Equal("onlyCommon", po.Lookup("Y")); // common-only key kept
    }

    [Fact]
    public void Load_Tolerates_Missing_Files()
    {
        Assert.Equal(0, PoCatalog.Load(null, null).Count);          // both absent -> empty (FS_LoadFile NULL)
        Assert.Equal("V", PoCatalog.Load(null, "msgid \"k\"\nmsgstr \"V\"\n").Lookup("k")); // only progs
        Assert.Equal("V", PoCatalog.Load("msgid \"k\"\nmsgstr \"V\"\n", null).Lookup("k")); // only common
    }

    [Fact]
    public void Ctx_Prefixed_Msgid_Is_Keyed_With_The_Prefix()
    {
        // The .po key INCLUDES the CTX prefix (the QC `_("GAMETYPE^Foo")` translates the whole literal). The
        // prefix is stripped only for display (see CtxTests), never before lookup.
        var po = PoCatalog.Parse("msgid \"GAMETYPE^%s:\"\nmsgstr \"SPIELTYP^%s:\"\n");
        Assert.Equal("SPIELTYP^%s:", po.Lookup("GAMETYPE^%s:"));
        Assert.Null(po.Lookup("%s:")); // the stripped form is NOT a key
    }

    [Fact]
    public void Real_German_Common_Po_Slice_Parses()
    {
        // Proof against a real shipped file: a few known German translations. Guarded so CI without the data
        // checkout still passes (the hand-written fixtures above carry the parser coverage).
        string? de = FindPo("common.de.po");
        if (de is null)
            return; // data dir not present
        string text = File.ReadAllText(de, Encoding.UTF8);
        var cat = PoCatalog.Parse(text);
        Assert.Equal("Kontrollpunktzeiten:", cat.Lookup("Checkpoint times:"));
        Assert.Equal("SPIELTYP^%s:", cat.Lookup("GAMETYPE^%s:")); // CTX prefix lives in the key
        Assert.Equal("Icons anzeigen", cat.Lookup("Show icons"));
        Assert.True(cat.Count > 1000, $"expected a large German catalog, got {cat.Count}");
    }

    /// <summary>Locate a shipped .po by probing the content repo then the reference Base checkout; null if absent.</summary>
    private static string? FindPo(string name)
    {
        string[] roots =
        {
            @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir",
            @"C:\Users\Bryan\Projects\Xonotic\Base\data\xonotic-data.pk3dir",
        };
        foreach (string r in roots)
        {
            string p = Path.Combine(r, name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }
}
