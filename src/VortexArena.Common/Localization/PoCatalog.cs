// Port of darkplaces/prvm_edict.c — PRVM_PO_ParseString / PRVM_PO_Load / PRVM_PO_Lookup (the gettext engine
// the QuakeC `_("...")` operator and the menu's i18n.qh helpers ride on). At PROGS LOAD the engine rewrites the
// string globals through the .po (prvm_edict.c:2631-2657): translation is keyed on the ENGLISH SOURCE STRING
// (the msgid IS the literal, incl. ^ color codes and a CTX^ prefix), so the C# equivalent is
// Lookup("English literal") -> translated-or-null.
//
// Lives in XonoticGodot.Common (ADR-0008: no Godot) so the menu (game/menu/framework/Localization.cs) and the unit
// tests share one parser. The faithful behaviors reproduced here:
//   * C-escapes \a \b \t \r \n \\ \" + octal \NNN (1-3 octal digits)              (PRVM_PO_ParseString)
//   * msgid/msgstr with consecutive "..." continuation lines, concatenated         (PRVM_PO_Load:1873-1894)
//   * `#...` comment lines (incl. `#:` `#,`) skipped; blank lines skipped          (PRVM_PO_Load:1840-1853)
//   * an EMPTY msgstr is untranslated -> skipped (the source is returned)          (PRVM_PO_Load:1902)
//   * load order: common.<lang>.po FIRST, progs.<lang>.po SECOND so progs wins,    (PRVM_PO_Load:1818-1824)
//     and within a file the LAST item wins (here: a dictionary indexer overwrite — the documented net effect of
//     the engine's prepend-chain + linear-scan, replicating the ORDER rather than the data structure).
using System;
using System.Collections.Generic;
using System.Text;

namespace XonoticGodot.Common.Localization;

/// <summary>
/// A loaded gettext message catalog — the C# successor to darkplaces' <c>po_t</c>. Maps an English source
/// string (the <c>msgid</c>) to its translation (the <c>msgstr</c>). Built from one or two <c>.po</c> texts via
/// <see cref="Parse"/> / <see cref="Load"/>; query with <see cref="Lookup"/> (translated-or-null) or
/// <see cref="Translate"/> (translated-or-self). No Godot — pure string parsing, headless-testable.
/// </summary>
public sealed class PoCatalog
{
    // msgid (English source) -> msgstr (translation). Ordinal so the key match is a byte-exact strcmp, exactly
    // like PRVM_PO_Lookup's strcmp on the decoded UTF-8 bytes.
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of stored (non-empty) translations.</summary>
    public int Count => _entries.Count;

    /// <summary>An empty catalog: every <see cref="Lookup"/> misses, every <see cref="Translate"/> is identity.</summary>
    public static PoCatalog Empty { get; } = new();

    /// <summary>
    /// Look up the translation for <paramref name="source"/> (the English literal / msgid). Returns
    /// <c>null</c> when there is no entry — the C# stand-in for <c>PRVM_PO_Lookup</c> returning NULL.
    /// </summary>
    public string? Lookup(string source)
        => _entries.TryGetValue(source, out string? v) ? v : null;

    /// <summary>
    /// The translation for <paramref name="source"/>, or <paramref name="source"/> itself when untranslated —
    /// the engine's net effect at progs load (<c>if(value) val->string = ...</c>; otherwise the original stands).
    /// </summary>
    public string Translate(string source) => Lookup(source) ?? source;

    /// <summary>
    /// Build a catalog from a single <c>.po</c> text. Mirrors one <c>i</c>-iteration of <c>PRVM_PO_Load</c>'s
    /// per-file scan (parse msgid/msgstr blocks with continuation lines + C-escapes, skip comments + empty
    /// translations). Within the file the last item for a given key wins.
    /// </summary>
    public static PoCatalog Parse(string poText)
    {
        var cat = new PoCatalog();
        cat.MergeFile(poText);
        return cat;
    }

    /// <summary>
    /// Build a catalog from the two files the engine loads, in the engine's order: <paramref name="common"/>
    /// (<c>common.&lt;lang&gt;.po</c>) FIRST, then <paramref name="progs"/> (<c>&lt;progs&gt;.&lt;lang&gt;.po</c>)
    /// SECOND — so a key in progs overrides the same key in common (PRVM_PO_Load:1818-1824 "progs.dat.de.po wins
    /// over common.de.po"). Either may be <c>null</c>/empty (a missing file, like <c>FS_LoadFile</c> returning
    /// NULL). Returns an empty catalog if both are absent.
    /// </summary>
    public static PoCatalog Load(string? common, string? progs)
    {
        var cat = new PoCatalog();
        if (!string.IsNullOrEmpty(common))
            cat.MergeFile(common!);
        if (!string.IsNullOrEmpty(progs))
            cat.MergeFile(progs!);
        return cat;
    }

    /// <summary>
    /// Overlay another <c>.po</c> text onto this catalog (last-wins per key). Used to layer the menu/progs file
    /// over the common file; exposed so a caller can build up the catalog incrementally in load order.
    /// </summary>
    public void MergeFile(string poText)
    {
        if (string.IsNullOrEmpty(poText))
            return;

        // The engine scans a NUL-terminated buffer char-by-char (p). We scan by lines, which is equivalent given
        // the format (every msgid/msgstr/continuation/comment is line-oriented). \r is tolerated (CRLF files).
        string[] lines = poText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        string? key = null;   // the current msgid's decoded text (thisstr.key)
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];

            // `#...` comment (incl. `#:` references, `#,` flags) -> skip to next line.
            if (line.Length > 0 && line[0] == '#')
            {
                i++;
                continue;
            }
            // Blank line -> skip.
            if (line.Length == 0)
            {
                i++;
                continue;
            }

            int mode; // 0 = msgid, 1 = msgstr
            string firstBody;
            if (StartsWith(line, "msgid \""))
            {
                mode = 0;
                // p += 6 lands the engine ON the opening quote of `msgid "`, so the first quoted body begins
                // here. We strip the `msgid ` prefix (6 chars) and let DecodeQuotedRun read from the quote.
                firstBody = line.Substring(6);
            }
            else if (StartsWith(line, "msgstr \""))
            {
                mode = 1;
                firstBody = line.Substring(7); // p += 7 -> on the opening quote of `msgstr "`
            }
            else
            {
                // Unrecognized line -> skip (the engine's final `else` branch).
                i++;
                continue;
            }

            // Consume this line's quoted body plus any following bare-"..." continuation lines, concatenating
            // their decoded contents (PRVM_PO_Load:1873-1894). decoded accumulates the full string.
            var decoded = new StringBuilder();
            AppendDecodedQuoted(decoded, firstBody);
            i++;
            while (i < lines.Length && lines[i].Length > 0 && lines[i][0] == '"')
            {
                AppendDecodedQuoted(decoded, lines[i]);
                i++;
            }

            string text = decoded.ToString();
            if (mode == 0)
            {
                key = text; // thisstr.key = decoded msgid (even when empty — the header's `msgid ""`)
            }
            else if (text.Length > 0 && key is not null)
            {
                // Non-empty translation with a preceding msgid -> store. Indexer overwrite = last-wins, the net
                // effect of the engine's load order. (Empty msgstr = untranslated -> skipped, so a partially
                // translated language falls through to the English source on Lookup.)
                _entries[key] = text;
                key = null; // mirror the engine's memset(&thisstr,0,...) after a stored pair
            }
        }
    }

    /// <summary>True when <paramref name="s"/> begins with <paramref name="prefix"/> (ordinal; the engine's strncmp).</summary>
    private static bool StartsWith(string s, string prefix)
        => s.Length >= prefix.Length && string.CompareOrdinal(s, 0, prefix, 0, prefix.Length) == 0;

    /// <summary>
    /// Decode ONE quoted segment from a line of the form <c>"body"</c> (possibly with trailing junk after the
    /// closing quote, which the engine ignores) and append its decoded text to <paramref name="sb"/>. Mirrors the
    /// engine's per-continuation-line handling: require a leading <c>"</c>, take the text up to the LAST <c>"</c>
    /// on the line as the body, then C-unescape it (PRVM_PO_ParseString).
    /// </summary>
    private static void AppendDecodedQuoted(StringBuilder sb, string segment)
    {
        if (segment.Length == 0 || segment[0] != '"')
            return;
        int close = segment.LastIndexOf('"');
        if (close <= 0)
            return; // no closing quote -> the engine `break`s out of the continuation loop
        string body = segment.Substring(1, close - 1);
        ParseString(sb, body);
    }

    /// <summary>
    /// C-string unescape, a faithful port of <c>PRVM_PO_ParseString</c>: <c>\a \b \t \r \n \\ \"</c> and octal
    /// <c>\NNN</c> (1-3 octal digits); a backslash before any other char yields that char literally; everything
    /// else is copied verbatim. Appends to <paramref name="sb"/>.
    /// </summary>
    internal static void ParseString(StringBuilder sb, string body)
    {
        int n = body.Length;
        for (int i = 0; i < n; i++)
        {
            char c = body[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            // Escape: look at the next char.
            i++;
            if (i >= n)
            {
                // Trailing lone backslash: the engine reads the terminating NUL via the default case (copies it,
                // ending the string). We simply stop.
                break;
            }
            char e = body[i];
            switch (e)
            {
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '0': case '1': case '2': case '3':
                case '4': case '5': case '6': case '7':
                {
                    // Octal \NNN: 1-3 octal digits, value built left-shifting by 3 each digit (matches the
                    // engine's `*out = (*out << 3) | (*in - '0')`). The result is a single byte (0..255), emitted
                    // as one char so a single-byte value round-trips (used for control chars).
                    int val = e - '0';
                    if (i + 1 < n && body[i + 1] >= '0' && body[i + 1] <= '7')
                    {
                        i++;
                        val = (val << 3) | (body[i] - '0');
                        if (i + 1 < n && body[i + 1] >= '0' && body[i + 1] <= '7')
                        {
                            i++;
                            val = (val << 3) | (body[i] - '0');
                        }
                    }
                    sb.Append((char)(val & 0xFF));
                    break;
                }
                default:
                    // `\x` for any other x -> literal x (the engine's default: *out++ = *in).
                    sb.Append(e);
                    break;
            }
        }
    }
}
