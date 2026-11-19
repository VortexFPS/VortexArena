using System;
using System.Text;

namespace VortexArena.Game.Menu;

/// <summary>
/// Translates Quake/DarkPlaces <c>^</c> colour codes into Godot BBCode so menu text (player names, server names,
/// …) renders coloured in a <see cref="Godot.RichTextLabel"/> — the C# stand-in for the engine's
/// <c>drawcolorcodedstring</c> path (menu/draw.qc <c>draw_Text</c> with <c>allowColorCodes</c>). Mirrors the
/// colour table used by <c>VortexArena.Common.Diagnostics.Log.ToBBCode</c>: <c>^0</c>–<c>^9</c> palette colours,
/// <c>^xRGB</c> (3 hex digits) → <c>#RRGGBB</c>, <c>^^</c> → a literal caret, <c>^7</c> resets to the default.
/// BBCode-special <c>[</c> is escaped as <c>[lb]</c> so names can't inject tags.
/// </summary>
public static class MenuColorCodes
{
    /// <summary>Convert <paramref name="s"/> (which may contain <c>^</c> codes) to BBCode for a RichTextLabel.</summary>
    public static string ToBBCode(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.IndexOf('^') < 0)
            return s.Replace("[", "[lb]"); // no codes — only need to neutralise '['

        var sb = new StringBuilder(s.Length + 16);
        bool spanOpen = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '^' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }       // ^^ → literal ^
                string? col = Code(s, i, out int extra);
                if (col != null)
                {
                    if (spanOpen) { sb.Append("[/color]"); spanOpen = false; }
                    if (col.Length > 0) { sb.Append("[color=").Append(col).Append(']'); spanOpen = true; }
                    i += extra;
                    continue;
                }
            }
            if (c == '[') { sb.Append("[lb]"); continue; }             // escape so names can't inject BBCode
            sb.Append(c);
        }
        if (spanOpen)
            sb.Append("[/color]");
        return sb.ToString();
    }

    /// <summary>
    /// The text split at its colour codes, each run with the colour it is drawn in (DrawQ_String with
    /// allowColorCodes): <paramref name="baseColor"/> until the first code, ^7 back to it. Alpha is the base's.
    /// </summary>
    public static System.Collections.Generic.List<(string Text, Godot.Color Color)> Segments(string? s, Godot.Color baseColor)
    {
        var runs = new System.Collections.Generic.List<(string, Godot.Color)>();
        if (string.IsNullOrEmpty(s)) return runs;
        var sb = new StringBuilder();
        Godot.Color current = baseColor;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '^' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }
                string? col = Code(s, i, out int extra);
                if (col != null)
                {
                    if (sb.Length > 0) { runs.Add((sb.ToString(), current)); sb.Clear(); }
                    // DrawQ_GetTextColor: the code's colour times the colour the string is drawn in.
                    current = col.Length == 0 ? baseColor : Godot.Color.FromHtml(col) * baseColor;
                    i += extra;
                    continue;
                }
            }
            sb.Append(c);
        }
        if (sb.Length > 0) runs.Add((sb.ToString(), current));
        return runs;
    }

    /// <summary>
    /// Replace the color at or immediately before the insertion point. A code starting at the insertion
    /// point still applies to the following text, so leave it in place.
    /// </summary>
    internal static string ApplyAtCaret(string text, int caret, string code, out int end)
    {
        // checkColorCode(..., true): match from inside a valid code or just after it. Repeat for adjacent codes.
        while (true)
        {
            int start = caret;
            int length = 0;
            for (int i = Math.Max(0, caret - 5); i < caret && i + 1 < text.Length; i++)
            {
                if (text[i] != '^')
                    continue;
                int candidateLength = ColorCodeLength(text, i);
                if (candidateLength >= caret - i && !IsCaretEscaped(text, i))
                {
                    start = i;
                    length = candidateLength;
                    break;
                }
            }
            if (length == 0)
                break;
            text = text.Remove(start, length);
            caret = start;
        }

        // Quote a bare caret so it cannot consume the leading caret of the new color code.
        if (caret > 0 && text[caret - 1] == '^' && !IsCaretEscaped(text, caret - 1))
            code = "^" + code;
        end = caret + code.Length;
        return text.Insert(caret, code);
    }

    private static bool IsCaretEscaped(string text, int caret)
    {
        int start = caret;
        while (start > 0 && text[start - 1] == '^')
            start--;
        return ((caret - start) & 1) != 0;
    }

    private static int ColorCodeLength(string s, int caret)
    {
        char first = s[caret + 1];
        if (first is >= '0' and <= '9')
            return 2;
        if (first == 'x' && caret + 4 < s.Length
            && IsHex(s[caret + 2]) && IsHex(s[caret + 3]) && IsHex(s[caret + 4]))
            return 5;
        return 0;
    }

    /// <summary>One Quake colour code → a BBCode hex colour, <c>""</c> for ^7 (reset), or null if not a code.
    /// <paramref name="extra"/> = chars after the caret the code occupies.</summary>
    private static string? Code(string s, int caret, out int extra)
    {
        int length = ColorCodeLength(s, caret);
        extra = Math.Max(0, length - 1);
        if (length == 2)
        {
            return s[caret + 1] switch
            {
                '0' => "#000000",
                '1' => "#ff0000",
                '2' => "#00ff00",
                '3' => "#ffff00",
                '4' => "#0000ff",
                '5' => "#00ffff",
                '6' => "#ff00ff",
                '7' => "",            // white == reset to the surrounding (default) colour
                '8' => "#ffffff80",   // DarkPlaces string_colors: half-transparent white
                _ => "#808080",       // ^9: half brightness
            };
        }
        if (length == 5)
        {
            char r = s[caret + 2], g = s[caret + 3], b = s[caret + 4]; // ^xRGB (3 hex) → #RRGGBB
            return new string(new[] { '#', r, r, g, g, b, b });
        }
        return null;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>
    /// Remove <c>^</c> colour codes entirely (server hostnames in plain widgets — Tree/ItemList cells can't
    /// render BBCode, so the codes would show as garbage). <c>^^</c> collapses to a literal caret.
    /// </summary>
    public static string Strip(string? s)
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
}
