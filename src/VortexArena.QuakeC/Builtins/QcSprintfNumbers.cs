// Port of the C library behaviour Base/darkplaces/prvm_cmds.c VM_sprintf, VM_ftos, VM_vtos and
// VM_stof lean on: printf's d i o u x X e E f F g G conversions, and strtod.
using System.Globalization;

namespace VortexArena.QuakeC;

[Flags]
internal enum QcPrintfFlags
{
    None = 0,
    Alternate = 1,      // #
    ZeroPad = 2,        // 0
    Left = 4,           // -
    SpacePositive = 8,  // (space)
    SignPositive = 16,  // +
}

/// <summary>
/// C's printf number conversions, reproduced digit for digit: .NET's own formats differ in the
/// exponent's width, in %g altogether, and in how flags combine. Always culture-invariant.
/// </summary>
internal static class QcSprintfNumbers
{
    /// <summary>
    /// No width or precision is honoured past this. It is at least the size of every buffer the results
    /// go into, so the clamp cannot be seen - it only stops a program asking for a gigabyte of zeros.
    /// </summary>
    public const int Cap = 16384;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>C's (intmax_t)float. Out of range is undefined in C; this is what x86-64 produces.</summary>
    public static long ToInt64(float value) =>
        value >= -9223372036854775808f && value < 9223372036854775808f ? (long)value : long.MinValue;

    /// <summary>
    /// C's (uintmax_t)float as x86-64 compilers emit it: below 2^63 it is the signed conversion
    /// reinterpreted (so -1 is all ones), and 2^64 and above, and NaN, come out as 0.
    /// </summary>
    public static ulong ToUInt64(float value)
    {
        if (value < 9223372036854775808f) return (ulong)ToInt64(value);
        return value < 18446744073709551616f ? (ulong)value : 0;
    }

    /// <summary>The digits of "%.{precision}f" for a finite, non-negative value.</summary>
    private static string FixedDigits(double magnitude, int precision) =>
        magnitude.ToString("F" + precision.ToString(Invariant), Invariant);

    /// <summary>"%.{precision}f" with no flags or width.</summary>
    public static string Fixed(double value, int precision) => Float('f', value, QcPrintfFlags.None, 0, precision);

    /// <summary>
    /// %e %E %f %F %g %G. A negative <paramref name="precision"/> means "not given" (6).
    /// </summary>
    public static string Float(char conversion, double value, QcPrintfFlags flags, int width, int precision)
    {
        bool upper = conversion is 'E' or 'F' or 'G';
        string sign = double.IsNegative(value) && !double.IsNaN(value) ? "-"
            : (flags & QcPrintfFlags.SignPositive) != 0 ? "+"
            : (flags & QcPrintfFlags.SpacePositive) != 0 ? " " : "";

        if (!double.IsFinite(value))
        {
            // glibc and the UCRT differ on the sign of a NaN; neither zero-pads these.
            string word = double.IsNaN(value) ? "nan" : "inf";
            return Pad(sign, upper ? word.ToUpperInvariant() : word, flags & ~QcPrintfFlags.ZeroPad, width);
        }

        double magnitude = Math.Abs(value);
        precision = precision < 0 ? 6 : Math.Min(precision, Cap);
        bool alternate = (flags & QcPrintfFlags.Alternate) != 0;
        string body;
        switch (char.ToLowerInvariant(conversion))
        {
            case 'f':
                body = FixedDigits(magnitude, precision);
                if (alternate && precision == 0) body += ".";
                break;
            case 'e':
                body = Exponential(magnitude, precision, alternate, upper, out _);
                break;
            default: // g
            {
                // "%g" chooses by the exponent the value has after rounding to P significant digits.
                int significant = precision == 0 ? 1 : precision;
                string scientific = Exponential(magnitude, significant - 1, alternate, upper, out int exponent);
                if (exponent < -4 || exponent >= significant)
                {
                    body = scientific;
                    if (!alternate) body = StripMantissaZeros(body);
                }
                else
                {
                    body = FixedDigits(magnitude, significant - 1 - exponent);
                    if (alternate) { if (!body.Contains('.')) body += "."; }
                    else if (body.Contains('.')) body = body.TrimEnd('0').TrimEnd('.');
                }
                break;
            }
        }
        return Pad(sign, body, flags, width);
    }

    /// <summary>"d.ddde+XX" for a finite, non-negative value: C's exponent has a sign and at least two digits.</summary>
    private static string Exponential(double magnitude, int precision, bool alternate, bool upper, out int exponent)
    {
        // .NET writes "d.dddE+XXX".
        string net = magnitude.ToString("E" + precision.ToString(Invariant), Invariant);
        int e = net.LastIndexOf('E');
        exponent = int.Parse(net.AsSpan(e + 1), NumberStyles.AllowLeadingSign, Invariant);
        string mantissa = net[..e];
        if (alternate && precision == 0) mantissa += ".";
        int magnitudeOfExponent = Math.Abs(exponent);
        return mantissa + (upper ? "E" : "e") + (exponent < 0 ? "-" : "+") + (magnitudeOfExponent < 10 ? "0" : "") + magnitudeOfExponent.ToString(Invariant);
    }

    private static string StripMantissaZeros(string scientific)
    {
        int e = scientific.IndexOfAny(ExponentMarkers);
        string mantissa = scientific[..e];
        if (!mantissa.Contains('.')) return scientific;
        return mantissa.TrimEnd('0').TrimEnd('.') + scientific[e..];
    }

    private static readonly char[] ExponentMarkers = { 'e', 'E' };

    /// <summary>%d and %i. A negative <paramref name="precision"/> means "not given".</summary>
    public static string Signed(long value, QcPrintfFlags flags, int width, int precision)
    {
        string sign = value < 0 ? "-"
            : (flags & QcPrintfFlags.SignPositive) != 0 ? "+"
            : (flags & QcPrintfFlags.SpacePositive) != 0 ? " " : "";
        // Negate as unsigned so the most negative value keeps its digits.
        ulong magnitude = value < 0 ? unchecked((ulong)(-value)) : (ulong)value;
        return Integer(sign, "", magnitude.ToString(Invariant), flags, width, precision);
    }

    /// <summary>%o %u %x %X. The sign flags do not apply to unsigned conversions.</summary>
    public static string Unsigned(char conversion, ulong value, QcPrintfFlags flags, int width, int precision)
    {
        bool alternate = (flags & QcPrintfFlags.Alternate) != 0;
        string digits, prefix = "";
        switch (conversion)
        {
            case 'o':
                digits = Convert.ToString(unchecked((long)value), 8);
                break;
            case 'x':
                digits = value.ToString("x", Invariant);
                if (alternate && value != 0) prefix = "0x";
                break;
            case 'X':
                digits = value.ToString("X", Invariant);
                if (alternate && value != 0) prefix = "0X";
                break;
            default:
                digits = value.ToString(Invariant);
                break;
        }
        string text = Integer("", prefix, digits, flags, width, precision, conversion == 'o' && alternate);
        return text;
    }

    private static string Integer(string sign, string prefix, string digits, QcPrintfFlags flags, int width, int precision, bool octalAlternate = false)
    {
        if (precision >= 0)
        {
            // A precision is a minimum digit count, turns the 0 flag off, and prints zero as no digits at all.
            flags &= ~QcPrintfFlags.ZeroPad;
            if (precision == 0 && digits == "0") digits = "";
            if (digits.Length < precision) digits = new string('0', Math.Min(precision - digits.Length, Cap)) + digits;
        }
        // "%#o" guarantees a leading zero, whatever the precision did.
        if (octalAlternate && (digits.Length == 0 || digits[0] != '0')) digits = "0" + digits;
        return Pad(sign + prefix, digits, flags, width);
    }

    /// <summary>
    /// Applies a field width: spaces on the left by default, on the right with '-', and zeros between
    /// the sign and the digits with '0' (which '-' overrides).
    /// </summary>
    public static string Pad(string sign, string body, QcPrintfFlags flags, int width)
    {
        int length = sign.Length + body.Length;
        if (width <= length) return sign + body;
        int fill = Math.Min(width - length, Cap);
        if ((flags & QcPrintfFlags.Left) != 0) return sign + body + new string(' ', fill);
        if ((flags & QcPrintfFlags.ZeroPad) != 0) return sign + new string('0', fill) + body;
        return new string(' ', fill) + sign + body;
    }

    /// <summary>
    /// C's atof (strtod): skips leading white space, converts the longest prefix that is a number -
    /// decimal, hexadecimal ("0x1.8p3"), "inf" or "nan" - and ignores the rest. No number is 0.
    /// </summary>
    public static double Atof(ReadOnlySpan<char> s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r'))) i++;
        int start = i;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) negative = s[i++] == '-';

        ReadOnlySpan<char> rest = s[i..];
        if (rest.StartsWith("inf", StringComparison.OrdinalIgnoreCase)) return negative ? double.NegativeInfinity : double.PositiveInfinity;
        if (rest.StartsWith("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;

        if (rest.Length >= 2 && rest[0] == '0' && (rest[1] | 0x20) == 'x')
        {
            double hex = HexFloat(rest[2..], out bool any);
            // "0x" with nothing after it is just the zero before the x.
            if (!any) return negative ? -0.0 : 0.0;
            return negative ? -hex : hex;
        }

        bool digits = false;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits = true; }
        if (i < s.Length && s[i] == '.')
        {
            int j = i + 1;
            bool fraction = false;
            while (j < s.Length && char.IsAsciiDigit(s[j])) { j++; fraction = true; }
            if (digits || fraction) { i = j; digits = true; }
        }
        if (!digits) return 0;
        if (i < s.Length && (s[i] | 0x20) == 'e')
        {
            // An exponent only counts if it has digits; "1e" and "1e+" are 1.
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
            if (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
                i = j;
            }
        }
        return double.TryParse(s[start..i], NumberStyles.Float, Invariant, out double value) ? value : 0;
    }

    private static double HexFloat(ReadOnlySpan<char> s, out bool any)
    {
        double mantissa = 0;
        int exponent = 0, i = 0;
        any = false;
        bool point = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '.' && !point) { point = true; continue; }
            if (!char.IsAsciiHexDigit(c)) break;
            // Past 2^53 the low digits cannot change the result; just keep the scale.
            if (mantissa < 1e18) { mantissa = mantissa * 16 + Convert.ToInt32(c.ToString(), 16); if (point) exponent -= 4; }
            else if (!point) exponent += 4;
            any = true;
        }
        if (!any) return 0;
        if (i < s.Length && (s[i] | 0x20) == 'p')
        {
            int j = i + 1;
            bool negative = false;
            if (j < s.Length && (s[j] == '+' || s[j] == '-')) negative = s[j++] == '-';
            if (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                int p = 0;
                for (; j < s.Length && char.IsAsciiDigit(s[j]); j++) p = Math.Min(p * 10 + (s[j] - '0'), 100000);
                exponent += negative ? -p : p;
            }
        }
        return Math.ScaleB(mantissa, exponent);
    }
}
