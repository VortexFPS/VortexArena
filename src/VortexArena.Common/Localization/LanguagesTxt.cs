// Port of qcsrc/menu/xonotic/languagelist.qc — XonoticLanguageList_getLanguages (the languages.txt parse) +
// the LANGPARM_* column constants. Each line is `id  "English name"  "Localized name"  NN%` (confirmed against
// data/xonotic-data.pk3dir/languages.txt, 34 languages); fields are tokenize_console tokens (so the quoted
// names are single tokens with the quotes stripped). A line with fewer than 3 tokens is skipped; the percentage
// (token 3) is stored only when present AND not "100%" (a 100%/absent percentage means "fully translated", drawn
// as the ready-icon instead of a number).
//
// Pure (Godot-free, ADR-0008) so the menu language pickers (DialogSettingsUser/DialogFirstRun) and the unit
// tests share one parser; the game side reads the file text through the VFS and hands it here.
using System;
using System.Collections.Generic;

namespace XonoticGodot.Common.Localization;

/// <summary>One row of <c>languages.txt</c> — the QC <c>LANGPARM_*</c> tuple (id / English name / localized
/// name / optional percentage).</summary>
/// <param name="Id">The language code (<c>argv(0)</c>), e.g. <c>"de"</c>, <c>"en"</c>, <c>"zh_CN"</c>. The value
/// stored in <c>_menu_prvm_language</c> / <c>prvm_language</c>.</param>
/// <param name="Name">The English name (<c>argv(1)</c>), e.g. <c>"German"</c> — the focused-row tooltip in QC.</param>
/// <param name="Localized">The name written in its own language (<c>argv(2)</c>), e.g. <c>"Deutsch"</c> — the row
/// label.</param>
/// <param name="Percentage">The translated-percentage token (<c>argv(3)</c>), e.g. <c>"82%"</c>, or
/// <see cref="string.Empty"/> when the language is fully translated (token absent or <c>"100%"</c>).</param>
public readonly record struct LanguageEntry(string Id, string Name, string Localized, string Percentage)
{
    /// <summary>True when the language is fully translated (QC: no percentage stored — drawn with the ready icon).</summary>
    public bool IsComplete => Percentage.Length == 0;
}

/// <summary>
/// Parser for Xonotic's <c>languages.txt</c> — the C# successor to <c>XonoticLanguageList_getLanguages</c>.
/// </summary>
public static class LanguagesTxt
{
    /// <summary>
    /// Parse the contents of <c>languages.txt</c> into the ordered list of languages, faithful to
    /// <c>getLanguages</c>: per line <c>tokenize_console</c>, require <c>n &gt;= 3</c> tokens, take id/name/
    /// localized from <c>argv(0..2)</c>, and the percentage from <c>argv(3)</c> only when it is present and not
    /// <c>"100%"</c>. Lines that don't tokenize to at least 3 fields (blank lines, stray comments) are skipped.
    /// File order is preserved (the QC list is drawn in file order).
    /// </summary>
    public static List<LanguageEntry> Parse(string text)
    {
        var list = new List<LanguageEntry>();
        if (string.IsNullOrEmpty(text))
            return list;

        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            List<string> argv = TokenizeConsole(raw);
            if (argv.Count < 3)
                continue;

            string id = argv[0];
            string name = argv[1];
            string localized = argv[2];
            // QC: string percent = argv(3); if(percent && percent != "100%") store it. argv(3) is "" when absent.
            string percent = argv.Count > 3 ? argv[3] : "";
            if (percent == "100%")
                percent = "";

            list.Add(new LanguageEntry(id, name, localized, percent));
        }
        return list;
    }

    /// <summary>
    /// A faithful stand-in for the engine's <c>tokenize_console</c>: split on whitespace, but treat a
    /// <c>"double quoted"</c> run as a single token with the surrounding quotes removed (so a name with a space
    /// like <c>"English (United Kingdom)"</c> is one token). Quotes are not nested and there is no escape
    /// processing — exactly what the console tokenizer does for these data lines.
    /// </summary>
    private static List<string> TokenizeConsole(string s)
    {
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false, has = false;
        foreach (char c in s)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                has = true; // an empty "" is still a (zero-length) token
            }
            else if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else
            {
                sb.Append(c);
                has = true;
            }
        }
        if (has)
            tokens.Add(sb.ToString());
        return tokens;
    }
}
