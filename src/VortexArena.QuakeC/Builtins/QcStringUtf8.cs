// Port of Base/darkplaces/utf8lib.c u8_analyze, u8_strlen, u8_strnlen, u8_bytelen, u8_byteofs,
// u8_getchar_utf8_enabled, u8_fromchar, colorcode_skipwidth, u8_strnlen_colorcodes, u8_bytelen_colorcodes.
using System.Text;

namespace VortexArena.QuakeC;

/// <summary>
/// DarkPlaces' UTF-8 helpers, over the same thing they run over in C: a NUL-terminated byte array.
///
/// The string builtins are defined in terms of bytes - an offset from argv_start_index is a byte offset,
/// strpad pads to a byte width, every result is cut at a byte count - and the program can observe all
/// of it. So the builtins that depend on that encode their arguments with <see cref="Z"/>, run the C
/// algorithm unchanged, and decode the result.
///
/// The one thing this cannot reproduce is a string that is not valid UTF-8: the VM holds .NET strings,
/// so a byte sequence a builtin leaves malformed (a character cut in half by a length limit, a lone
/// byte above 0x7F made by chr2str or strconv with utf8_enable 0) becomes U+FFFD when it is decoded,
/// where DarkPlaces would have kept the raw bytes.
/// </summary>
internal static class QcStringUtf8
{
    /// <summary>What a size_t parameter holds after C converts a negative int to it: more than any string.</summary>
    public const long Unbounded = long.MaxValue;

    // u8_analyze gives up after skipping this many bytes that cannot start a character.
    private const int AnalyzeInfinity = 7;

    /// <summary>The string as NUL-terminated UTF-8. The terminator is what makes the C lookaheads safe.</summary>
    public static byte[] Z(string text)
    {
        // A NUL inside the text ends it, as it would for every C function that later reads it.
        int nul = text.IndexOf('\0');
        ReadOnlySpan<char> chars = nul < 0 ? text : text.AsSpan(0, nul);
        byte[] z = new byte[Encoding.UTF8.GetByteCount(chars) + 1];
        Encoding.UTF8.GetBytes(chars, z);
        return z;
    }

    public static string Text(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? "" : Encoding.UTF8.GetString(bytes);

    /// <summary>C's conversion of an int to size_t, for the callers that pass a possibly negative count.</summary>
    public static long Size(int value) => value < 0 ? Unbounded : value;

    public static bool IsHexDigit(byte c) => (uint)(c - '0') <= 9 || (uint)((c | 0x20) - 'a') <= 5;

    // utf8_lengths[]: 0 for a byte that cannot start a character (a continuation byte, the overlong
    // leads C0/C1, and F5 and up, which would encode past U+10FFFF).
    private static int SequenceLength(byte b) => b < 0x80 ? 1 : b < 0xC2 ? 0 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : b < 0xF5 ? 4 : 0;

    private static int MinimumCodePoint(int length) => length switch { 2 => 0x80, 3 => 0x800, 4 => 0x10000, _ => 1 };

    /// <summary>
    /// u8_analyze: finds the next valid character at or after <paramref name="at"/>, skipping malformed
    /// bytes. <paramref name="start"/> is how many bytes were skipped and <paramref name="length"/> the
    /// character's size.
    /// </summary>
    public static bool Analyze(byte[] s, int at, out int start, out int length, out int ch, long maxLength = AnalyzeInfinity)
    {
        int i = 0;
        while (true)
        {
            int bits = 0;
            while (i < maxLength && s[at + i] != 0 && (bits = SequenceLength(s[at + i])) == 0) i++;

            if (i >= maxLength || s[at + i] == 0)
            {
                start = i; length = 0; ch = 0;
                return false;
            }
            if (bits == 1)
            {
                start = i; length = 1; ch = s[at + i];
                return true;
            }

            int value = s[at + i] & (0xFF >> bits);
            int j = 1;
            // The terminator is not a continuation byte, so this never reads past it.
            for (; j < bits; j++)
            {
                if ((s[at + i + j] & 0xC0) != 0x80) break;
                value = (value << 6) | (s[at + i + j] & 0x3F);
            }
            if (j < bits)
            {
                i += j;
                continue;
            }
            // ">= 0x10FFFF" is DarkPlaces' own off-by-one: the last code point is rejected too.
            if (value < MinimumCodePoint(bits) || value >= 0x10FFFF)
            {
                i += bits;
                continue;
            }
            start = i; length = bits; ch = value;
            return true;
        }
    }

    /// <summary>u8_strlen: characters in the string, or bytes when UTF-8 is off.</summary>
    public static int StrLen(byte[] s, bool utf8)
    {
        if (!utf8) return s.Length - 1;
        int p = 0, count = 0;
        while (s[p] != 0)
        {
            if (s[p] < 0x80) { count++; p++; continue; }
            if (s[p] < 0xC2) { p++; continue; }
            if (!Analyze(s, p, out int st, out int ln, out _)) break;
            p += st + ln;
            count++;
        }
        return count;
    }

    /// <summary>u8_strnlen: characters wholly inside the first <paramref name="n"/> bytes.</summary>
    public static int StrNLen(byte[] s, long n, bool utf8)
    {
        if (!utf8) return (int)Math.Min(s.Length - 1, n);
        int p = 0, count = 0;
        while (s[p] != 0 && n > 0)
        {
            if (s[p] < 0x80) { count++; p++; n--; continue; }
            if (s[p] < 0xC2) { p++; n--; continue; }
            if (!Analyze(s, p, out int st, out int ln, out _, n)) break;
            if (n < st + ln) return count;
            count++;
            n -= st + ln;
            p += st + ln;
        }
        return count;
    }

    /// <summary>u8_bytelen: bytes the first <paramref name="n"/> characters at <paramref name="at"/> occupy.</summary>
    public static int ByteLen(byte[] s, int at, long n, bool utf8)
    {
        if (!utf8) return (int)Math.Min(s.Length - 1 - at, n);
        int p = at;
        while (s[p] != 0 && n > 0)
        {
            if (s[p] < 0x80) { p++; n--; continue; }
            if (s[p] < 0xC2) { p++; continue; }
            if (!Analyze(s, p, out int st, out int ln, out _)) break;
            n--;
            p += st + ln;
        }
        return p - at;
    }

    /// <summary>u8_byteofs: byte offset of character <paramref name="index"/>, or -1 if the string is shorter.</summary>
    public static int ByteOfs(byte[] s, long index, bool utf8)
    {
        if (!utf8) return s.Length - 1 < index ? -1 : (int)index;
        int ofs = 0, ln = 0;
        do
        {
            ofs += ln;
            if (!Analyze(s, ofs, out int st, out ln, out _)) return -1;
            ofs += st;
        }
        while (index-- > 0);
        return ofs;
    }

    /// <summary>u8_getchar_utf8_enabled: the character at <paramref name="at"/>, or 0 if there is none.</summary>
    public static int GetChar(byte[] s, int at) => Analyze(s, at, out _, out _, out int ch) ? ch : 0;

    /// <summary>
    /// u8_fromchar: encodes one character, returning the bytes written (0 for NUL and for anything past
    /// U+10FFFF). With UTF-8 off the result is the low byte, and the U+E0xx range DarkPlaces uses for the
    /// Quake glyphs maps back to the glyph's byte.
    /// </summary>
    public static int FromChar(int w, Span<byte> to, bool utf8)
    {
        if (w == 0) return 0;
        if (w >= 0xE000 && !utf8) w -= 0xE000;
        // A negative value takes this branch too: (char)w in C.
        if (w < 0x80 || !utf8)
        {
            to[0] = (byte)w;
            return 1;
        }
        if (w < 0x800)
        {
            to[0] = (byte)(0xC0 | (w >> 6));
            to[1] = (byte)(0x80 | (w & 0x3F));
            return 2;
        }
        if (w < 0x10000)
        {
            to[0] = (byte)(0xE0 | (w >> 12));
            to[1] = (byte)(0x80 | ((w >> 6) & 0x3F));
            to[2] = (byte)(0x80 | (w & 0x3F));
            return 3;
        }
        if (w <= 0x10FFFF)
        {
            to[0] = (byte)(0xF0 | (w >> 18));
            to[1] = (byte)(0x80 | ((w >> 12) & 0x3F));
            to[2] = (byte)(0x80 | ((w >> 6) & 0x3F));
            to[3] = (byte)(0x80 | (w & 0x3F));
            return 4;
        }
        return 0;
    }

    /// <summary>
    /// colorcode_skipwidth: 2 for ^0-^9, 5 for ^xRGB, and 1 for the first caret of ^^ (the caller then
    /// treats the second as an ordinary character). 0 otherwise.
    /// </summary>
    private static int ColorCodeSkipWidth(byte[] s, int p)
    {
        if (s[p] != '^') return 0;
        if (s[p + 1] >= '0' && s[p + 1] <= '9') return 2;
        if (s[p + 1] == 'x' && IsHexDigit(s[p + 2]) && IsHexDigit(s[p + 3]) && IsHexDigit(s[p + 4])) return 5;
        return s[p + 1] == '^' ? 1 : 0;
    }

    /// <summary>u8_bytelen_colorcodes: bytes the first <paramref name="n"/> visible characters occupy, colour codes included.</summary>
    public static int ByteLenColorCodes(byte[] s, long n, bool utf8)
    {
        int p = 0;
        while (s[p] != 0 && n > 0)
        {
            int w = ColorCodeSkipWidth(s, p);
            p += w;
            if (w > 1) continue;
            if (s[p] < 0x80 || !utf8) { p++; n--; continue; }
            if (s[p] < 0xC2) { p++; continue; }
            if (!Analyze(s, p, out int st, out int ln, out _)) break;
            n--;
            p += st + ln;
        }
        return p;
    }

    /// <summary>u8_strnlen_colorcodes: visible characters in the first <paramref name="n"/> bytes.</summary>
    public static int StrNLenColorCodes(byte[] s, long n, bool utf8)
    {
        int p = 0, count = 0;
        while (s[p] != 0 && n > 0)
        {
            int w = ColorCodeSkipWidth(s, p);
            n -= w;
            p += w;
            if (w > 1) continue;
            if (s[p] < 0x80 || !utf8) { count++; p++; n--; continue; }
            if (s[p] < 0xC2) { p++; n--; continue; }
            if (!Analyze(s, p, out int st, out int ln, out _, n)) break;
            if (n < st + ln) return count;
            count++;
            n -= st + ln;
            p += st + ln;
        }
        return count;
    }
}
