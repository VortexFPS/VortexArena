// Port of the small menu text helpers the list backends need:
//   - strdecolorize  (DP string builtin; strips ^N and ^xRGB color codes)
//   - the menu filter glob ("*foo*" / "foo?") the screenshot list builds
//
// These live in XonoticGodot.Common (ADR-0008: no Godot) so the data sources stay
// headless-testable. The Godot-side menu already has an equivalent color stripper
// (MenuColorCodes.Strip); this is the same algorithm, in the Godot-free assembly.

using System.Text;

namespace XonoticGodot.Common.Menu;

/// <summary>Small Godot-free text helpers shared by the menu <see cref="DataSource"/> backends.</summary>
public static class MenuTextFormat
{
    private static bool IsHex(char c)
        => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>
    /// Port of DP <c>strdecolorize</c>: remove Quake <c>^</c> color codes, leaving the visible text. A
    /// numeric <c>^0</c>..<c>^9</c> and a hex <c>^xRGB</c> (three hex digits) are dropped; <c>^^</c> collapses
    /// to a single literal caret. Used for screenshot stems and map titles/authors (which the QC decolorizes
    /// before display/sort).
    /// </summary>
    public static string Decolorize(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('^') < 0)
            return s ?? "";

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '^' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }
                if (n is >= '0' and <= '9') { i++; continue; }
                if (n == 'x' && i + 4 < s.Length && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]))
                { i += 4; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Match <paramref name="text"/> against a simple wildcard <paramref name="pattern"/> using
    /// <c>*</c> (any run) and <c>?</c> (any single char), case-insensitively — the filter form the menu
    /// builds for the screenshot list ("*foo*" when the user types "foo", or a literal glob if they typed
    /// one). A pattern with no wildcards matches as a substring-equal (the menu always wraps a plain
    /// query in <c>*</c>, so an exact-pattern call is the wildcard path).
    /// </summary>
    public static bool GlobMatch(string? text, string? pattern)
    {
        text ??= "";
        pattern ??= "";
        if (pattern.Length == 0)
            return true;
        return GlobMatch(text, 0, pattern, 0);
    }

    private static bool GlobMatch(string text, int ti, string pattern, int pi)
    {
        while (pi < pattern.Length)
        {
            char p = pattern[pi];
            if (p == '*')
            {
                // collapse consecutive '*'
                while (pi < pattern.Length && pattern[pi] == '*')
                    pi++;
                if (pi == pattern.Length)
                    return true; // trailing '*' matches the rest
                // try to match the remaining pattern at every suffix of text
                for (int k = ti; k <= text.Length; k++)
                    if (GlobMatch(text, k, pattern, pi))
                        return true;
                return false;
            }

            if (ti >= text.Length)
                return false;

            if (p == '?' || char.ToLowerInvariant(p) == char.ToLowerInvariant(text[ti]))
            {
                ti++;
                pi++;
                continue;
            }
            return false;
        }
        return ti == text.Length;
    }
}
