using System.IO;
using System.Linq;
using System.Text;
using XonoticGodot.Common.Localization;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises <see cref="LanguagesTxt"/> — the Godot-free port of <c>XonoticLanguageList_getLanguages</c>
/// (the languages.txt parse). Each line is <c>id  "English name"  "Localized name"  NN%</c>; quoted names are
/// single tokens, the percentage is optional and "100%"/absent means fully translated. Pure logic, no GlobalState.
/// </summary>
public class LanguagesTxtTests
{
    [Fact]
    public void Parses_A_Basic_Line()
    {
        var list = LanguagesTxt.Parse("de \"German\" \"Deutsch\" 82%\n");
        Assert.Single(list);
        Assert.Equal("de", list[0].Id);
        Assert.Equal("German", list[0].Name);
        Assert.Equal("Deutsch", list[0].Localized);
        Assert.Equal("82%", list[0].Percentage);
        Assert.False(list[0].IsComplete);
    }

    [Fact]
    public void Hundred_Percent_Is_Treated_As_Complete_No_Percentage()
    {
        // QC: store percent only if present AND != "100%".
        var list = LanguagesTxt.Parse("en \"English\" \"English\" 100%\n");
        Assert.Equal("", list[0].Percentage);
        Assert.True(list[0].IsComplete);
    }

    [Fact]
    public void Missing_Percentage_Is_Allowed()
    {
        // n >= 3 is enough; argv(3) absent -> no percentage.
        var list = LanguagesTxt.Parse("xx \"X\" \"X\"\n");
        Assert.Single(list);
        Assert.Equal("", list[0].Percentage);
        Assert.True(list[0].IsComplete);
    }

    [Fact]
    public void Lines_With_Fewer_Than_Three_Tokens_Are_Skipped()
    {
        var list = LanguagesTxt.Parse("\n   \nonlyid\nid \"name\"\nde \"German\" \"Deutsch\" 82%\n");
        Assert.Single(list); // only the well-formed German line survives
        Assert.Equal("de", list[0].Id);
    }

    [Fact]
    public void Quoted_Names_With_Spaces_Are_Single_Tokens()
    {
        var list = LanguagesTxt.Parse("en_GB \"English (United Kingdom)\" \"English (United Kingdom)\" 100%\n");
        Assert.Equal("English (United Kingdom)", list[0].Name);
        Assert.Equal("English (United Kingdom)", list[0].Localized);
    }

    [Fact]
    public void Non_Ascii_Localized_Names_Survive()
    {
        var list = LanguagesTxt.Parse("ru \"Russian\" \"Русский\" 100%\nja_JP \"Japanese\" \"日本語\" 78%\n");
        Assert.Equal("Русский", list[0].Localized);
        Assert.Equal("日本語", list[1].Localized);
        Assert.Equal("78%", list[1].Percentage);
    }

    [Fact]
    public void File_Order_Is_Preserved()
    {
        var list = LanguagesTxt.Parse("a \"A\" \"A\" 10%\nb \"B\" \"B\" 20%\nc \"C\" \"C\" 30%\n");
        Assert.Equal(new[] { "a", "b", "c" }, list.Select(l => l.Id).ToArray());
    }

    [Fact]
    public void Empty_Input_Yields_Empty_List()
    {
        Assert.Empty(LanguagesTxt.Parse(""));
        Assert.Empty(LanguagesTxt.Parse(null!));
    }

    [Fact]
    public void Real_Languages_Txt_Parses_To_34_Entries()
    {
        // Proof against the real shipped file. Guarded so CI without the data checkout still passes.
        string? path = FindLanguagesTxt();
        if (path is null)
            return;
        var list = LanguagesTxt.Parse(File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal(34, list.Count);

        var de = list.First(l => l.Id == "de");
        Assert.Equal("German", de.Name);
        Assert.Equal("Deutsch", de.Localized);
        Assert.Equal("82%", de.Percentage);

        var en = list.First(l => l.Id == "en");
        Assert.True(en.IsComplete);       // 100% -> complete
        Assert.Equal("", en.Percentage);

        var ru = list.First(l => l.Id == "ru");
        Assert.Equal("Русский", ru.Localized);
        Assert.True(ru.IsComplete);       // ru is 100% in this build

        var it = list.First(l => l.Id == "it");
        Assert.Equal("99%", it.Percentage); // not 100% -> kept
    }

    private static string? FindLanguagesTxt()
    {
        string[] roots =
        {
            @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir",
            @"C:\Users\Bryan\Projects\Xonotic\Base\data\xonotic-data.pk3dir",
        };
        foreach (string r in roots)
        {
            string p = Path.Combine(r, "languages.txt");
            if (File.Exists(p))
                return p;
        }
        return null;
    }
}
