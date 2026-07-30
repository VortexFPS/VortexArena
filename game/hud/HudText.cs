using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Game.Menu;   // MenuState.Cvars — live hud_colorset_* for the CCR macro expansion

namespace VortexArena.Game.Hud;

/// <summary>
/// Xonotic colored-text parsing for the HUD — the modernized successor to QuakeC's <c>drawcolorcodedstring</c>
/// / <c>COLOR_TAG_*</c> handling (Base/.../qcsrc/lib/string.qh + the engine's color-code lexer). Player
/// names, centerprints and kill-feed lines carry inline <c>^N</c> color codes (a single digit 0-9) and the
/// long form <c>^xRGB</c> (three hex nibbles), which select the color of the text that follows. This helper
/// turns such a string into a list of (text, color) runs the panels draw left-to-right, and also strips the
/// codes for width measurement.
///
/// Supported (matching the engine):
///   <c>^0</c>..<c>^9</c>  — the ten palette colors (0 black … 7 white, etc.).
///   <c>^xRGB</c>          — 12-bit hex color, e.g. <c>^xf80</c> = orange.
///   <c>^^</c>             — a literal caret.
/// A trailing/!invalid <c>^</c> is treated as a literal.
/// </summary>
public static class HudText
{
    /// <summary>One colored run of a parsed string (QC: a segment between color tags).</summary>
    public readonly struct Run
    {
        public readonly string Text;
        public readonly Color Color;
        public Run(string text, Color color) { Text = text; Color = color; }
    }

    // The Quake/Xonotic ^0..^9 palette (engine "qfont" color table). Alpha applied by the caller.
    private static readonly Color[] Palette =
    {
        new(0.20f, 0.20f, 0.20f), // ^0 black-ish
        new(1.00f, 0.20f, 0.20f), // ^1 red
        new(0.20f, 1.00f, 0.20f), // ^2 green
        new(1.00f, 1.00f, 0.20f), // ^3 yellow
        new(0.20f, 0.40f, 1.00f), // ^4 blue
        new(0.20f, 1.00f, 1.00f), // ^5 cyan
        new(1.00f, 0.20f, 1.00f), // ^6 magenta
        new(1.00f, 1.00f, 1.00f), // ^7 white
        new(0.60f, 0.60f, 0.60f), // ^8 grey
        new(0.50f, 0.50f, 0.50f), // ^9 dark grey
    };

    /// <summary>
    /// QC <c>CCR()</c> (Base/.../qcsrc/lib/string.qh) — expand Xonotic's NAMED color macros into engine
    /// <c>^&lt;digit&gt;</c> codes before parsing, using the live <c>hud_colorset_*</c> cvars, and drop the
    /// <c>^BOLD</c> font marker (a centerprint-only operator handled there, not a color). Notification /
    /// kill-feed / centerprint strings carry these tags (<c>^F1</c>-<c>^F4</c> foreground, <c>^K1</c>-<c>^K3</c>
    /// kill, <c>^BG</c> background, <c>^N</c> reset); without this pass they'd render as literal "^F1"/"^BOLD".
    /// Idempotent and cheap (no-op when the string has no caret). Runs at the head of <see cref="Parse"/> and
    /// <see cref="Strip"/> so every panel that renders/measures text via <see cref="HudText"/> is covered.
    /// </summary>
    public static string Expand(string? text)
    {
        if (string.IsNullOrEmpty(text) || text!.IndexOf('^') < 0) return text ?? "";
        string s = text;
        if (s.Contains("^BOLD")) s = s.Replace("^BOLD", "");   // font marker, not a color (centerprint reads it raw first)
        if (s.IndexOf('^') < 0) return s;
        // ^F1..^F4 foreground, ^K1..^K3 kill, ^BG background — cvar-backed (defaults match hud_luma.cfg).
        s = s.Replace("^F1", "^" + Cset("hud_colorset_foreground_1", "2"));
        s = s.Replace("^F2", "^" + Cset("hud_colorset_foreground_2", "3"));
        s = s.Replace("^F3", "^" + Cset("hud_colorset_foreground_3", "4"));
        s = s.Replace("^F4", "^" + Cset("hud_colorset_foreground_4", "1"));
        s = s.Replace("^K1", "^" + Cset("hud_colorset_kill_1", "1"));
        s = s.Replace("^K2", "^" + Cset("hud_colorset_kill_2", "3"));
        s = s.Replace("^K3", "^" + Cset("hud_colorset_kill_3", "4"));
        s = s.Replace("^BG", "^" + Cset("hud_colorset_background", "7"));
        s = s.Replace("^N", "^7");   // "none" — reset to white
        return s;
    }

    private static string Cset(string name, string def)
    {
        string v = MenuState.Cvars.GetString(name);
        return string.IsNullOrEmpty(v) ? def : v;
    }

    /// <summary>
    /// Split <paramref name="text"/> into colored runs. <paramref name="baseColor"/> tints the leading
    /// (uncolored) run and supplies the alpha for every run (the codes only set RGB, like the engine).
    /// </summary>
    public static List<Run> Parse(string? text, Color baseColor)
    {
        var runs = new List<Run>();
        text = Expand(text);
        if (string.IsNullOrEmpty(text)) return runs;

        Color cur = baseColor;
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length > 0) { runs.Add(new Run(sb.ToString(), cur)); sb.Clear(); }
        }

        for (int i = 0; i < text!.Length; i++)
        {
            char ch = text[i];
            if (ch == '^' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }          // ^^ -> literal caret
                if (n >= '0' && n <= '9')                                  // ^N palette
                {
                    Flush();
                    cur = Palette[n - '0'];
                    cur.A = baseColor.A;
                    i++;
                    continue;
                }
                if ((n == 'x' || n == 'X') && i + 4 < text.Length          // ^xRGB hex
                    && IsHex(text[i + 2]) && IsHex(text[i + 3]) && IsHex(text[i + 4]))
                {
                    Flush();
                    float r = HexNibble(text[i + 2]) / 15f;
                    float g = HexNibble(text[i + 3]) / 15f;
                    float b = HexNibble(text[i + 4]) / 15f;
                    cur = new Color(r, g, b, baseColor.A);
                    i += 4;
                    continue;
                }
                // not a valid code: fall through and emit the caret literally
            }
            sb.Append(ch);
        }
        Flush();
        return runs;
    }

    /// <summary>Remove all color codes, returning the plain text (QC <c>strdecolorize</c>).</summary>
    public static string Strip(string? text)
    {
        text = Expand(text);
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text!.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '^' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }
                if (n >= '0' && n <= '9') { i++; continue; }
                if ((n == 'x' || n == 'X') && i + 4 < text.Length
                    && IsHex(text[i + 2]) && IsHex(text[i + 3]) && IsHex(text[i + 4]))
                { i += 4; continue; }
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexNibble(char c) =>
        c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);
}
