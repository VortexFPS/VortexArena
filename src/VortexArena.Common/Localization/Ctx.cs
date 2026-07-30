// Port of qcsrc/lib/i18n.qh — CTX(s) (the msgctxt emulation) + the en/""/dump identity rule from
// language_filename(). The QC `_("PREFIX^Text")` translates the WHOLE literal "PREFIX^Text" (the .po msgid
// INCLUDES the prefix); CTX then strips the "PREFIX^" for DISPLAY only — so the prefix must be stripped AFTER
// the PO lookup, never before. This is the pure (Godot-free) string logic; the game-side Localization facade
// composes it with the active PoCatalog.
using System;

namespace XonoticGodot.Common.Localization;

/// <summary>
/// gettext-context (msgctxt) helpers — the C# successor to i18n.qh's <c>CTX</c>. A QuakeC string like
/// <c>_("GAMETYPE^Deathmatch")</c> is translated as the full <c>"GAMETYPE^Deathmatch"</c> (the PO key carries
/// the prefix so identical English words in different contexts can translate differently), and only displayed as
/// <c>"Deathmatch"</c> after <see cref="Strip"/> removes the disambiguating prefix.
/// </summary>
public static class Ctx
{
    /// <summary>
    /// Strip a leading <c>PREFIX^</c> disambiguation prefix from <paramref name="s"/>, faithful to i18n.qh
    /// <c>CTX</c>:
    /// <list type="bullet">
    ///   <item>find the first <c>^</c>; if none, return <paramref name="s"/> unchanged.</item>
    ///   <item>a prefix of length 0 (caret at index 0) or 1 (caret at index 1) is invalid — left unchanged
    ///         (matches the QC <c>caret_ofs &gt; 1</c> guard).</item>
    ///   <item>if the part before the caret contains a space, the caret is taken to be part of a <c>^</c> color
    ///         code (not a context prefix) and the string is left unchanged (the QC <c>space_ofs</c> guard).</item>
    ///   <item>otherwise return everything AFTER the caret.</item>
    /// </list>
    /// </summary>
    public static string Strip(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;

        int caret = s.IndexOf('^');
        // empty (caret == 0) and one-char (caret == 1) prefixes are invalid; no caret -> -1.
        if (caret > 1)
        {
            // A space anywhere in the candidate prefix means the caret is likely a color code, not a context
            // prefix (QC: strstrofs(substring(s,0,caret_ofs), " ", 0)). IndexOf over s[0..caret) == any index
            // strictly less than caret.
            int space = s.IndexOf(' ');
            if (space < 0 || space > caret)
                return s.Substring(caret + 1);
        }
        return s;
    }

    /// <summary>
    /// True for the languages that translate to the identity (no PO is loaded / no real entries): the empty
    /// string, <c>"en"</c>, and <c>"dump"</c> — the i18n.qh <c>language_filename</c> guard (and the engine's
    /// behavior where <c>en</c> has no real <c>common.en.po</c>).
    /// </summary>
    public static bool IsIdentityLanguage(string? language)
        => string.IsNullOrEmpty(language) || language == "en" || language == "dump";
}
