// Port of Base/darkplaces/prvm_cmds.c VM_VarString, VM_ftos, VM_vtos, VM_etos, VM_stof, VM_strlen,
// VM_strdecolorize, VM_strlennocol, VM_strtolower, VM_strtoupper, VM_strcat, VM_substring,
// VM_strreplace, VM_strireplace, VM_stov, VM_strzone, VM_strunzone, VM_strstrofs, VM_str2chr,
// VM_chr2str, VM_strconv, VM_strpad, VM_infoadd, VM_infoget, VM_strncmp, VM_strncasecmp, VM_crc16,
// VM_digest_hex, VM_uri_escape, VM_uri_unescape; common.c COM_StringDecolorize,
// COM_StringLengthNoColors, COM_ToLowerString, COM_ToUpperString; utf8lib.c
// u8_COM_StringLengthNoColors; com_infostring.c InfoString_GetValue, InfoString_SetValue;
// mathlib.c Math_atov; and the string rows of clvm_cmds.c vm_cl_builtins[].
using System.Security.Cryptography;
using System.Text;
using static VortexArena.QuakeC.QcStringUtf8;

namespace VortexArena.QuakeC;

/// <summary>
/// The string builtins of DarkPlaces' client program table: conversion, slicing, searching, sprintf,
/// the tokenizer and its argv, and string buffers. Builtin numbers are the ones in
/// <c>vm_cl_builtins[]</c>.
///
/// One instance per VM: the tokenizer's last result and the string buffers live here.
///
/// These are ports, quirks included, because the program was written against the quirks. Where
/// DarkPlaces works on bytes this does too (see <see cref="QcStringUtf8"/>), and every result is cut
/// where DarkPlaces' fixed buffers would cut it, which is also what bounds the memory a hostile
/// program can make one call allocate.
/// </summary>
public sealed partial class QcStringBuiltins
{
    private readonly QcVm _vm;
    private readonly IQcHost _host;

    // sizeof(char[VM_TEMPSTRING_MAXSIZE]): room for one byte less than this, plus the terminator.
    private readonly int _size;

    public QcStringBuiltins(QcVm vm, IQcHost host)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _size = Math.Max(vm.MaxStringLength, 16);
        _sprintfOut = new byte[_size];
        _tokenText = new byte[_size];
    }

    /// <summary>Registers every builtin of this class under its DarkPlaces CSQC number.</summary>
    public void Register()
    {
        foreach ((int number, string _, QcBuiltin builtin) in Table()) _vm.RegisterBuiltin(number, builtin);
    }

    /// <summary>Number and DarkPlaces name of every builtin <see cref="Register"/> installs.</summary>
    public IEnumerable<(int Number, string Name)> Registered => Table().Select(e => (e.Number, e.Name));

    /// <summary>
    /// The builtin of that DarkPlaces name, for a host whose table gives it another number: the menu
    /// program's vm_m_builtins[] has the same C functions as the client's under different numbers
    /// (VM_ftos is #26 for a client program and #17 for the menu). Null if there is none of that name.
    /// </summary>
    public QcBuiltin? Find(string name)
    {
        foreach ((int _, string candidate, QcBuiltin builtin) in Table())
            if (candidate == name) return builtin;
        return null;
    }

    private (int Number, string Name, QcBuiltin Builtin)[] Table() => new (int, string, QcBuiltin)[]
    {
        (26, "ftos", Ftos),
        (27, "vtos", Vtos),
        (65, "etos", Etos),
        (81, "stof", Stof),
        (114, "strlen", StrLen),
        (115, "strcat", StrCat),
        (116, "substring", Substring),
        (117, "stov", Stov),
        (118, "strzone", StrZone),
        (119, "strunzone", StrUnzone),
        (221, "strstrofs", StrStrOfs),
        (222, "str2chr", Str2Chr),
        (223, "chr2str", Chr2Str),
        (224, "strconv", StrConv),
        (225, "strpad", StrPad),
        (226, "infoadd", InfoAdd),
        (227, "infoget", InfoGet),
        (228, "strncmp", StrNCmp),
        (229, "strcasecmp", StrNCaseCmp),
        (230, "strncasecmp", StrNCaseCmp),
        (441, "tokenize", Tokenize),
        (442, "argv", Argv),
        (460, "buf_create", BufCreate),
        (461, "buf_del", BufDel),
        (462, "buf_getsize", BufGetSize),
        (463, "buf_copy", BufCopy),
        (464, "buf_sort", BufSort),
        (465, "buf_implode", BufImplode),
        (466, "bufstr_get", BufStrGet),
        (467, "bufstr_set", BufStrSet),
        (468, "bufstr_add", BufStrAdd),
        (469, "bufstr_free", BufStrFree),
        (476, "strlennocol", StrLenNoCol),
        (477, "strdecolorize", StrDecolorize),
        (479, "tokenizebyseparator", TokenizeBySeparator),
        (480, "strtolower", StrToLower),
        (481, "strtoupper", StrToUpper),
        (484, "strreplace", StrReplace),
        (485, "strireplace", StrIReplace),
        (494, "crc16", Crc16),
        (510, "uri_escape", UriEscape),
        (511, "uri_unescape", UriUnescape),
        (514, "tokenize_console", TokenizeConsole),
        (515, "argv_start_index", ArgvStartIndex),
        (516, "argv_end_index", ArgvEndIndex),
        (517, "buf_cvarlist", BufCvarList),
        (535, "buf_loadfile", BufLoadFile),
        (536, "buf_writefile", BufWriteFile),
        (537, "bufstr_find", BufStrFind),
        (538, "matchpattern", MatchPattern),
        (627, "sprintf", Sprintf),
        (639, "digest_hex", DigestHex),
    };

    // ---- shared plumbing ---------------------------------------------------------------------------

    private bool Utf8 => _host.Utf8Enabled;

    private void Warning(string message) => _host.Warning($"{_vm.Name} VM warning: {message}");

    // VM_SAFEPARMCOUNT: a wrong argument count is fatal to the program in DarkPlaces, and it is what
    // stops a builtin reading parameter cells the caller never wrote.
    private void Parms(int min, int max, string name)
    {
        int count = _vm.ArgCount;
        if (count >= min && count <= max) return;
        throw new QcRuntimeException(min == max
            ? $"{_vm.Name}: {name} wrong parameter count {count} ({min} expected ) !"
            : $"{_vm.Name}: {name} wrong parameter count {count} ({min} to {max} expected ) !");
    }

    private void ReturnBytes(ReadOnlySpan<byte> bytes) => _vm.ReturnString(Text(bytes));

    /// <summary>
    /// VM_VarString: arguments <paramref name="first"/> onward, concatenated into a buffer of
    /// <see cref="_size"/> bytes. Returns it NUL-terminated; what does not fit is dropped with a warning.
    /// </summary>
    private byte[] VarBytes(int first)
    {
        byte[] z = Z(Concat(first));
        if (z.Length <= _size) return z;
        Warning($"{_size - 1} bytes available, will truncate {z.Length - 1} byte string\n");
        byte[] cut = new byte[_size];
        Array.Copy(z, cut, _size - 1);
        return cut;
    }

    /// <summary><see cref="VarBytes"/> as text, without encoding it when it obviously fits.</summary>
    private string VarText(int first)
    {
        string text = Concat(first);
        // A UTF-16 unit is at most three UTF-8 bytes.
        if (text.Length * 3 < _size && !text.Contains('\0')) return text;
        byte[] z = VarBytes(first);
        return Text(z.AsSpan(0, z.Length - 1));
    }

    private string Concat(int first)
    {
        int count = _vm.ArgCount;
        if (first >= count) return "";
        if (first == count - 1) return _vm.ArgString(first);
        StringBuilder text = new();
        // Like the C loop, stop once the buffer is certainly full: nothing after that could be kept.
        for (int i = first; i < count && text.Length < _size; i++) text.Append(_vm.ArgString(i));
        return text.ToString();
    }

    /// <summary>The text cut to what a C buffer of <see cref="_size"/> bytes holds.</summary>
    private string Limit(string text)
    {
        if (text.Length * 3 < _size) return text;
        byte[] z = Z(text);
        return z.Length <= _size ? text : Text(z.AsSpan(0, _size - 1));
    }

    // ---- numbers to and from text ------------------------------------------------------------------

    // string(float f) ftos = #26
    private void Ftos(QcVm vm)
    {
        Parms(1, 1, "VM_ftos");
        float v = vm.ArgFloat(0);
        // A whole number prints without a fraction ("%.0f"); anything else is "%f", six decimals and
        // never an exponent - so 0.0000001 is "0.000000" and 1e30 is thirty-one digits long.
        vm.ReturnString(QcSprintfNumbers.Fixed(v, QcVm.FloatToInt(v) == v ? 0 : 6));
    }

    // string(vector v) vtos = #27
    private void Vtos(QcVm vm)
    {
        Parms(1, 1, "VM_vtos");
        QcVector v = vm.ArgVector(0);
        // "'%5.1f %5.1f %5.1f'"
        vm.ReturnString($"'{QcSprintfNumbers.Float('f', v.X, 0, 5, 1)} {QcSprintfNumbers.Float('f', v.Y, 0, 5, 1)} {QcSprintfNumbers.Float('f', v.Z, 0, 5, 1)}'");
    }

    // string(entity ent) etos = #65
    private void Etos(QcVm vm)
    {
        Parms(1, 1, "VM_etos");
        vm.ReturnString("entity " + vm.ArgInt(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // float(string s) stof = #81
    private static readonly string[] s_oneCharacter = OneCharacterStrings();

    private static string[] OneCharacterStrings()
    {
        string[] table = new string[128];
        for (int i = 0; i < table.Length; i++) table[i] = ((char)i).ToString();
        return table;
    }

    private void Stof(QcVm vm)
    {
        Parms(1, 8, "VM_stof");
        if (vm.ArgCount == 1)
        {
            // A single digit - what stof is handed by the same character-at-a-time loops.
            string only = vm.ArgString(0);
            if (only.Length == 1 && (uint)(only[0] - '0') <= 9)
            {
                vm.ReturnFloat(only[0] - '0');
                return;
            }
        }
        vm.ReturnFloat((float)QcSprintfNumbers.Atof(VarText(0)));
    }

    // vector(string) stov = #117
    private void Stov(QcVm vm)
    {
        Parms(1, 1, "VM_stov");
        vm.ReturnVector(Atov(VarText(0)));
    }

    /// <summary>
    /// Math_atov: up to three numbers separated by spaces or tabs, optionally inside single quotes.
    /// Parsing stops at the first thing that is not a number, leaving the remaining components 0.
    /// </summary>
    internal static QcVector Atov(ReadOnlySpan<char> s)
    {
        Span<float> v = stackalloc float[3];
        v.Clear();
        int p = 0;
        if (p < s.Length && s[p] == '\'') p++;
        for (int i = 0; i < 3; i++)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++;
            v[i] = (float)QcSprintfNumbers.Atof(s[p..]);
            char c = p < s.Length ? s[p] : '\0';
            if (v[i] == 0 && c != '-' && c != '+' && (c < '0' || c > '9')) break;
            while (p < s.Length && s[p] != ' ' && s[p] != '\t' && s[p] != '\'') p++;
            if (p < s.Length && s[p] == '\'') break;
        }
        return new QcVector(v[0], v[1], v[2]);
    }

    // ---- length, concatenation, slicing ------------------------------------------------------------

    // float(string s) strlen = #114
    private void StrLen(QcVm vm)
    {
        Parms(1, 1, "VM_strlen");
        string s = vm.ArgString(0);
        vm.ReturnFloat(System.Text.Ascii.IsValid(s) && !s.Contains('\0') ? s.Length : QcStringUtf8.StrLen(Z(s), Utf8));
    }

    // string(string s, string...) strcat = #115
    private void StrCat(QcVm vm)
    {
        Parms(1, 8, "VM_strcat");
        vm.ReturnString(VarText(0));
    }

    // string(string s, float start, float length) substring = #116
    private void Substring(QcVm vm)
    {
        Parms(3, 3, "VM_substring");
        string text = vm.ArgString(0);
        int start = QcVm.FloatToInt(vm.ArgFloat(1));
        int length = QcVm.FloatToInt(vm.ArgFloat(2));

        // In ASCII a character is a byte whichever way utf8_enable is set, and this is the hot path.
        if (System.Text.Ascii.IsValid(text) && !text.Contains('\0'))
        {
            int total = text.Length;
            if (start < 0) start = Math.Clamp(start + total, 0, total);
            if (length < 0) length += total - start + 1;
            if (start >= total) { vm.ReturnString(""); return; }
            // A length still negative is what C turns into a huge size_t: the rest of the string.
            int take = length < 0 ? total - start : Math.Min(length, total - start);
            // The program walks strings one character at a time (substring(s, i, 1) in a loop), so the
            // one-character result is the common case and need not allocate.
            vm.ReturnString(take == 1 ? s_oneCharacter[text[start]] : text.Substring(start, Math.Min(take, _size - 1)));
            return;
        }

        byte[] s = Z(text);
        bool utf8 = Utf8;
        int characters = 0;
        // FTE_STRINGS: a negative start counts from the end, and a negative length is "all but that
        // many, less one" (-1 is "to the end").
        if (start < 0)
        {
            characters = QcStringUtf8.StrLen(s, utf8);
            start = Math.Clamp(start + characters, 0, characters);
        }
        if (length < 0)
        {
            if (characters == 0) characters = QcStringUtf8.StrLen(s, utf8);
            length += characters - start + 1;
        }

        int byteStart = ByteOfs(s, start, utf8);
        if (byteStart < 0)
        {
            vm.ReturnString("");
            return;
        }
        int byteLength = ByteLen(s, byteStart, Size(length), utf8);
        if (byteLength >= _size - 1) byteLength = _size - 1;
        ReturnBytes(s.AsSpan(byteStart, byteLength));
    }

    // string(string s) strzone = #118
    private void StrZone(QcVm vm)
    {
        Parms(1, 1, "VM_strzone");
        vm.ReturnInt(vm.AllocString(VarText(0)));
    }

    // void(string s) strunzone = #119
    private void StrUnzone(QcVm vm)
    {
        Parms(1, 1, "VM_strunzone");
        vm.FreeString(vm.ArgInt(0));
    }

    // ---- searching and single characters -----------------------------------------------------------

    // float(string str, string sub[, float startpos]) strstrofs = #221
    private void StrStrOfs(QcVm vm)
    {
        Parms(2, 3, "VM_strstrofs");
        byte[] s = Z(vm.ArgString(0));
        byte[] match = Z(vm.ArgString(1));
        bool utf8 = Utf8;
        // The start is a character index; a negative one is a huge size_t, i.e. the end of the string,
        // where only the empty string can still be found.
        int first = ByteLen(s, 0, Size(vm.ArgCount > 2 ? QcVm.FloatToInt(vm.ArgFloat(2)) : 0), utf8);
        int found = s.AsSpan(first, s.Length - 1 - first).IndexOf(match.AsSpan(0, match.Length - 1));
        vm.ReturnFloat(found < 0 ? -1 : StrNLen(s, first + found, utf8));
    }

    // float(string str, float ofs) str2chr = #222
    private void Str2Chr(QcVm vm)
    {
        Parms(2, 2, "VM_str2chr");
        byte[] s = Z(vm.ArgString(0));
        bool utf8 = Utf8;
        // No counting from the end here: a negative index lands on the terminator and reads as 0.
        int index = ByteLen(s, 0, Size(QcVm.FloatToInt(vm.ArgFloat(1))), utf8);
        if (index >= s.Length - 1) vm.ReturnFloat(0);
        else vm.ReturnFloat(utf8 ? GetChar(s, index) : s[index]);
    }

    // string(float c, ...) chr2str = #223
    private void Chr2Str(QcVm vm)
    {
        Parms(0, 8, "VM_chr2str");
        Span<byte> t = stackalloc byte[9 * 4 + 1];
        bool utf8 = Utf8;
        int length = 0;
        for (int i = 0; i < vm.ArgCount && length < t.Length - 1; i++)
            length += FromChar(QcVm.FloatToInt(vm.ArgFloat(i)), t[length..], utf8);
        // A character whose low byte is 0 (with UTF-8 off) writes a terminator and ends the string there.
        int nul = t[..length].IndexOf((byte)0);
        ReturnBytes(t[..(nul < 0 ? length : nul)]);
    }

    // float(string s1, string s2[, float len]) strncmp = #228
    private void StrNCmp(QcVm vm)
    {
        Parms(2, 3, "VM_strncmp");
        vm.ReturnFloat(Compare(vm.ArgString(0), vm.ArgString(1), vm.ArgCount > 2 ? CompareLength(vm.ArgFloat(2)) : Unbounded, false));
    }

    // float(string s1, string s2) strcasecmp = #229, float(string s1, string s2, float len) strncasecmp = #230
    private void StrNCaseCmp(QcVm vm)
    {
        Parms(2, 3, "VM_strncasecmp");
        vm.ReturnFloat(Compare(vm.ArgString(0), vm.ArgString(1), vm.ArgCount > 2 ? CompareLength(vm.ArgFloat(2)) : Unbounded, true));
    }

    // (size_t)float: a negative length (or NaN) becomes a huge one, so it compares the whole strings.
    private static long CompareLength(float n) => n >= 0 && n < 9e18f ? (long)n : Unbounded;

    /// <summary>
    /// strncmp / strncasecmp over the UTF-8 bytes. C only promises the sign of the result, and the
    /// libraries differ (glibc returns the byte difference, the UCRT -1/0/1); this returns -1, 0 or 1.
    /// </summary>
    private static int Compare(string a, string b, long n, bool ignoreCase)
    {
        byte[] x = Z(a), y = Z(b);
        for (int i = 0; i < n; i++)
        {
            int c = x[i], d = y[i];
            if (ignoreCase)
            {
                if (c >= 'A' && c <= 'Z') c += 32;
                if (d >= 'A' && d <= 'Z') d += 32;
            }
            if (c != d) return c < d ? -1 : 1;
            if (c == 0) return 0;
        }
        return 0;
    }

    // ---- case, colour codes, charset ---------------------------------------------------------------

    // string(string s) strtolower = #480
    private void StrToLower(QcVm vm)
    {
        Parms(1, 1, "VM_strtolower");
        vm.ReturnString(ChangeCase(vm.ArgString(0), false));
    }

    // string(string s) strtoupper = #481
    private void StrToUpper(QcVm vm)
    {
        Parms(1, 1, "VM_strtoupper");
        vm.ReturnString(ChangeCase(vm.ArgString(0), true));
    }

    /// <summary>
    /// COM_ToLowerString / COM_ToUpperString. With UTF-8 off only A-Z and a-z change. With it on,
    /// DarkPlaces maps each character through tables generated from Unicode 6.0's simple case mappings;
    /// this uses .NET's invariant simple mappings instead, which are the same idea from a newer Unicode,
    /// so a character that gained a case pair since may convert here and not there.
    /// </summary>
    private string ChangeCase(string text, bool upper)
    {
        if (System.Text.Ascii.IsValid(text) || !Utf8)
        {
            // ToUpperInvariant would touch non-ASCII letters, which byte mode must leave alone.
            return Limit(string.Create(text.Length, (text, upper), static (span, state) =>
            {
                for (int i = 0; i < span.Length; i++)
                {
                    char c = state.text[i];
                    span[i] = state.upper ? (c >= 'a' && c <= 'z' ? (char)(c - 32) : c) : (c >= 'A' && c <= 'Z' ? (char)(c + 32) : c);
                }
            }));
        }

        StringBuilder result = new(text.Length);
        int bytes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            Rune mapped = upper
                ? rune.Value switch { 0x131 => new Rune('I'), 0x17F => new Rune('S'), _ => Rune.ToUpperInvariant(rune) }
                : Rune.ToLowerInvariant(rune);
            // The output buffer is the tempstring size; conversion stops at the character that does not fit.
            bytes += mapped.Utf8SequenceLength;
            if (bytes > _size - 1) break;
            result.Append(mapped.ToString());
        }
        return result.ToString();
    }

    // string(string s) strdecolorize = #477
    private void StrDecolorize(QcVm vm)
    {
        Parms(1, 1, "VM_strdecolorize");
        byte[] s = Z(vm.ArgString(0));
        // The output can be longer than the input (a lone caret is doubled so the result prints safely).
        byte[] o = new byte[Math.Min(s.Length * 2, _size)];
        int n = 0, p = 0;
        // COM_StringDecolorize with escape_carets: when the buffer runs out the result is "", not a prefix.
        bool Append(byte c)
        {
            if (n >= _size - 1) return false;
            o[n++] = c;
            return true;
        }
        while (true)
        {
            byte c = s[p];
            if (c == 0) break;
            if (c != '^')
            {
                if (!Append(c)) { n = 0; break; }
                p++;
                continue;
            }
            byte next = s[++p];
            bool ok = true;
            if (next == 'x')
            {
                if (IsHexDigit(s[p + 1]) && IsHexDigit(s[p + 2]) && IsHexDigit(s[p + 3])) p += 3;
                else ok = Append((byte)'^') && Append((byte)'^') && Append((byte)'x');
            }
            else if (next == 0)
            {
                // An unfinished code at the very end: finish it with a second caret.
                if (!(Append((byte)'^') && Append((byte)'^'))) n = 0;
                break;
            }
            else if (next == '^') ok = Append((byte)'^') && Append((byte)'^');
            else if (next < '0' || next > '9') ok = Append((byte)'^') && Append(next);
            if (!ok) { n = 0; break; }
            p++;
        }
        ReturnBytes(o.AsSpan(0, n));
    }

    // float(string s) strlennocol = #476
    private void StrLenNoCol(QcVm vm)
    {
        Parms(1, 1, "VM_strlennocol");
        byte[] s = Z(vm.ArgString(0));
        bool utf8 = Utf8;
        int p = 0, count = 0;
        while (s[p] != 0)
        {
            if (s[p] == '^')
            {
                byte next = s[++p];
                if (next == 'x')
                {
                    if (IsHexDigit(s[p + 1]) && IsHexDigit(s[p + 2]) && IsHexDigit(s[p + 3])) p += 3;
                    else count += 2;
                }
                else if (next == 0) { count++; break; }
                else if (next == '^') count++;
                else if (next < '0' || next > '9') count += 2;
                p++;
                continue;
            }
            if (!utf8 || s[p] < 0x80) { count++; p++; continue; }
            if (s[p] < 0xC2) { p++; continue; }
            if (!Analyze(s, p, out int st, out int ln, out _)) break;
            p += st + ln;
            count++;
        }
        vm.ReturnFloat(count);
    }

    // string(float ccase, float calpha, float cnum, string s, ...) strconv = #224
    private void StrConv(QcVm vm)
    {
        Parms(3, 8, "VM_strconv");
        int changeCase = QcVm.FloatToInt(vm.ArgFloat(0)); // 0 same, 1 lower, 2 upper
        int redAlpha = QcVm.FloatToInt(vm.ArgFloat(1));   // 0 same, 1 white, 2 red, 5 alternate, 6 alternate-alternate
        int redNumber = QcVm.FloatToInt(vm.ArgFloat(2));  // 0 same, 1 white, 2 red, 3 redspecial, 4 whitespecial
        byte[] s = VarBytes(3);
        int length = s.Length - 1;

        // This is a Quake charset conversion - "red" is the glyph 128 above - applied to bytes. It only
        // means anything for ASCII text; on UTF-8 it mangles multi-byte characters exactly as it does in C.
        for (int i = 0; i < length; i++)
        {
            int c = s[i];
            if (c >= '0' && c <= '9') c = ConvertNumber(c, '0', redNumber);
            else if (c >= '0' + 128 && c <= '9' + 128) c = ConvertNumber(c, '0' + 128, redNumber);
            else if (c >= '0' + 128 - 30 && c <= '9' + 128 - 30) c = ConvertNumber(c, '0' + 128 - 30, redNumber);
            else if (c >= '0' - 30 && c <= '9' - 30) c = ConvertNumber(c, '0' - 30, redNumber);
            else if (c >= 'a' && c <= 'z') c = ConvertAlpha(c, 'a', 0, changeCase, redAlpha, i);
            else if (c >= 'A' && c <= 'Z') c = ConvertAlpha(c, 'A', 0, changeCase, redAlpha, i);
            else if (c >= 'a' + 128 && c <= 'z' + 128) c = ConvertAlpha(c, 'a', 128, changeCase, redAlpha, i);
            else if (c >= 'A' + 128 && c <= 'Z' + 128) c = ConvertAlpha(c, 'A', 128, changeCase, redAlpha, i);
            else if ((c & 127) < 16 || redAlpha == 0) { }
            else if (c < 128) c = ConvertPunctuation(c, 0, redAlpha);
            else c = ConvertPunctuation(c, 128, redAlpha);
            s[i] = (byte)c;
        }
        // A byte converted to 0 ends the string, as it would for whoever read the C buffer.
        int nul = Array.IndexOf(s, (byte)0);
        ReturnBytes(s.AsSpan(0, nul));
    }

    private static int ConvertNumber(int c, int from, int conversion) => c - from + conversion switch
    {
        1 => '0',
        2 => '0' + 128,
        3 => '0' - 30,
        4 => '0' + 128 - 30,
        _ => from,
    };

    private static int ConvertPunctuation(int c, int from, int conversion) => c - from + conversion switch
    {
        1 => 0,
        2 => 128,
        _ => from,
    };

    private static int ConvertAlpha(int c, int caseBase, int colourBase, int caseConversion, int colourConversion, int index)
    {
        c -= colourBase + caseBase;
        colourBase = colourConversion switch
        {
            1 => 0,
            2 => 128,
            5 or 6 => (index & 1) == colourConversion - 5 ? 128 : 0,
            _ => colourBase,
        };
        caseBase = caseConversion switch { 1 => 'a', 2 => 'A', _ => caseBase };
        return c + caseBase + colourBase;
    }

    // string(float chars, string s, ...) strpad = #225
    private void StrPad(QcVm vm)
    {
        Parms(1, 8, "VM_strpad");
        int pad = QcVm.FloatToInt(vm.ArgFloat(0));
        byte[] s = VarBytes(1);
        int length = s.Length - 1;
        // snprintf("%*s", -pad, src): the width is in bytes whatever utf8_enable says, positive pads on
        // the right and negative on the left - the reverse of printf. DarkPlaces does not survive a
        // result longer than its buffer; this cuts it there.
        long width = Math.Abs((long)pad);
        int total = (int)Math.Min(Math.Max(width, length), _size - 1);
        byte[] o = new byte[total];
        o.AsSpan().Fill((byte)' ');
        if (pad >= 0) s.AsSpan(0, Math.Min(length, total)).CopyTo(o);
        else
        {
            int spaces = (int)Math.Min(Math.Max(width - length, 0), total);
            s.AsSpan(0, total - spaces).CopyTo(o.AsSpan(spaces));
        }
        ReturnBytes(o);
    }

    // ---- info strings ("\key\value\key\value") -----------------------------------------------------

    // string(string info, string key, string value, ...) infoadd = #226
    private void InfoAdd(QcVm vm)
    {
        Parms(2, 8, "VM_infoadd");
        byte[] info = Z(Limit(vm.ArgString(0)));
        byte[] key = Z(vm.ArgString(1));
        byte[] value = VarBytes(2);
        ReturnBytes(InfoSetValue(info, key, value));
    }

    // string(string info, string key) infoget = #227
    private void InfoGet(QcVm vm)
    {
        Parms(2, 2, "VM_infoget");
        ReturnBytes(InfoGetValue(Z(vm.ArgString(0)), Z(vm.ArgString(1))));
    }

    private static bool Contains(byte[] z, char c) => Array.IndexOf(z, (byte)c, 0, z.Length - 1) >= 0;

    // strncmp(buffer + at, key, keylength) == 0 followed by the end of the key's field.
    private static bool InfoKeyAt(byte[] buffer, int at, byte[] key)
    {
        int keyLength = key.Length - 1;
        for (int i = 0; i < keyLength; i++)
            if (buffer[at + i] != key[i]) return false; // also stops at the buffer's terminator
        return buffer[at + keyLength] == 0 || buffer[at + keyLength] == '\\';
    }

    private static int InfoSkipPair(byte[] buffer, int pos)
    {
        if (buffer[pos] == '\\') pos++;
        while (buffer[pos] != 0 && buffer[pos] != '\\') pos++;
        if (buffer[pos] == '\\') pos++;
        while (buffer[pos] != 0 && buffer[pos] != '\\') pos++;
        return pos;
    }

    /// <summary>InfoString_GetValue: the value stored for a key, or nothing.</summary>
    private ReadOnlySpan<byte> InfoGetValue(byte[] buffer, byte[] key)
    {
        if (Contains(key, '\\') || Contains(key, '"') || key[0] == 0) return default;
        int pos = 0;
        while (buffer[pos] == '\\')
        {
            if (InfoKeyAt(buffer, pos + 1, key))
            {
                pos += key.Length;
                if (buffer[pos] == '\\') pos++;
                int length = 0;
                while (buffer[pos + length] != 0 && buffer[pos + length] != '\\' && length < _size - 1) length++;
                return buffer.AsSpan(pos, length);
            }
            pos = InfoSkipPair(buffer, pos);
        }
        return default;
    }

    /// <summary>
    /// InfoString_SetValue: replaces, adds or (for an empty value) removes a key. Anything it will not
    /// do - a backslash or quote in the key or value, a nameless key, a result that does not fit -
    /// leaves the string as it was.
    /// </summary>
    private ReadOnlySpan<byte> InfoSetValue(byte[] buffer, byte[] key, byte[] value)
    {
        ReadOnlySpan<byte> unchanged = buffer.AsSpan(0, buffer.Length - 1);
        if (Contains(key, '\\') || Contains(value, '\\') || Contains(key, '"') || Contains(value, '"') || key[0] == 0)
        {
            Warning("InfoString_SetValue: the key or value is empty or contains \\ or \", which an infostring cannot store\n");
            return unchanged;
        }

        int pos = 0;
        while (buffer[pos] == '\\')
        {
            if (InfoKeyAt(buffer, pos + 1, key)) break;
            pos = InfoSkipPair(buffer, pos);
        }
        // If the key is there, find the end of its value: that span is what gets replaced.
        int pos2 = pos;
        if (buffer[pos] == '\\')
        {
            pos2 += key.Length;
            if (buffer[pos2] == '\\') pos2++;
            while (buffer[pos2] != 0 && buffer[pos2] != '\\') pos2++;
        }

        int keyLength = key.Length - 1, valueLength = value.Length - 1, restLength = buffer.Length - 1 - pos2;
        if (_size <= pos + 1 + keyLength + 1 + valueLength + restLength)
        {
            Warning("InfoString_SetValue: no room in infostring\n");
            return unchanged;
        }

        byte[] result = new byte[pos + (valueLength > 0 ? 2 + keyLength + valueLength : 0) + restLength];
        int n = pos;
        Array.Copy(buffer, result, pos);
        if (valueLength > 0)
        {
            result[n++] = (byte)'\\';
            Array.Copy(key, 0, result, n, keyLength); n += keyLength;
            result[n++] = (byte)'\\';
            Array.Copy(value, 0, result, n, valueLength); n += valueLength;
        }
        Array.Copy(buffer, pos2, result, n, restLength);
        return result;
    }

    // ---- replace -----------------------------------------------------------------------------------

    // string(string search, string replace, string subject) strreplace = #484
    private void StrReplace(QcVm vm)
    {
        Parms(3, 3, "VM_strreplace");
        Replace(vm, false);
    }

    // string(string search, string replace, string subject) strireplace = #485
    private void StrIReplace(QcVm vm)
    {
        Parms(3, 3, "VM_strireplace");
        Replace(vm, true);
    }

    private void Replace(QcVm vm, bool ignoreCase)
    {
        byte[] search = Z(vm.ArgString(0)), replace = Z(vm.ArgString(1)), subject = Z(vm.ArgString(2));
        int searchLength = search.Length - 1, replaceLength = replace.Length - 1, subjectLength = subject.Length - 1;

        // The output is one tempstring buffer and every write is checked against it, so a replacement
        // that would multiply the text simply stops when the buffer is full.
        int limit = _size - 1;
        byte[] o = new byte[(int)Math.Min(limit, (long)subjectLength + ((long)subjectLength + 1) * replaceLength)];
        int si = 0, i;
        for (i = 0; i <= subjectLength - searchLength; i++)
        {
            int j = 0;
            for (; j < searchLength; j++)
                if (ignoreCase ? Lower(subject[i + j]) != Lower(search[j]) : subject[i + j] != search[j]) break;
            if (j == searchLength)
            {
                for (j = 0; j < replaceLength && si < limit; j++) o[si++] = replace[j];
                if (searchLength > 0) i += searchLength - 1;
                // An empty search matches before every byte and once more at the end, so the replacement
                // lands around each byte. (At the end C also copies the terminator, which reads the same.)
                else if (si < limit && i < subjectLength) o[si++] = subject[i];
            }
            else if (si < limit) o[si++] = subject[i];
        }
        for (; i < subjectLength; i++)
            if (si < limit) o[si++] = subject[i];
        ReturnBytes(o.AsSpan(0, si));
    }

    // tolower in the "C" locale.
    private static byte Lower(byte c) => c >= 'A' && c <= 'Z' ? (byte)(c + 32) : c;

    // ---- URI escaping ------------------------------------------------------------------------------

    // string(string in) uri_escape = #510
    private void UriEscape(QcVm vm)
    {
        Parms(1, 8, "VM_uri_escape");
        byte[] s = VarBytes(0);
        const string hex = "0123456789ABCDEF";
        byte[] o = new byte[Math.Min((s.Length - 1) * 3, _size)];
        int n = 0;
        // Each step needs room for a whole escape, so the result ends up to two bytes short of full.
        for (int p = 0; s[p] != 0 && n < _size - 3; p++)
        {
            byte c = s[p];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == '!' || c == '~' || c == '\'' || c == '(' || c == ')')
                o[n++] = c;
            else
            {
                o[n++] = (byte)'%';
                o[n++] = (byte)hex[c >> 4];
                o[n++] = (byte)hex[c & 15];
            }
        }
        ReturnBytes(o.AsSpan(0, n));
    }

    // string(string in) uri_unescape = #511
    private void UriUnescape(QcVm vm)
    {
        Parms(1, 8, "VM_uri_unescape");
        byte[] s = VarBytes(0);
        byte[] o = new byte[s.Length];
        int n = 0;
        for (int p = 0; s[p] != 0;)
        {
            if (s[p] == '%')
            {
                int hi = HexValue(s[p + 1]);
                int lo = hi < 0 ? -1 : HexValue(s[p + 2]);
                if (lo >= 0)
                {
                    // %00 is consumed but writes nothing: a NUL must not be smuggled into a string.
                    if (hi != 0 || lo != 0) o[n++] = (byte)(hi * 16 + lo);
                    p += 3;
                    continue;
                }
            }
            o[n++] = s[p++];
        }
        ReturnBytes(o.AsSpan(0, n));
    }

    private static int HexValue(byte c) =>
        c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    // ---- checksums ---------------------------------------------------------------------------------

    // float(float caseinsensitive, string s, ...) crc16 = #494
    private void Crc16(QcVm vm)
    {
        Parms(2, 8, "VM_crc16");
        byte[] s = VarBytes(1);
        vm.ReturnFloat(QcHash.Crc16(s.AsSpan(0, s.Length - 1), vm.ArgFloat(0) != 0));
    }

    // string(string digest, string data, ...) digest_hex = #639
    private void DigestHex(QcVm vm)
    {
        Parms(2, 8, "VM_digest_hex");
        string digest = vm.ArgString(0);
        byte[] s = VarBytes(1);
        ReadOnlySpan<byte> data = s.AsSpan(0, s.Length - 1);
        // Any other name is not an error: the program gets the null string and can test for it.
        byte[]? hash = digest switch
        {
            "MD4" => QcHash.Md4(data),
            "SHA256" => SHA256.HashData(data),
            _ => null,
        };
        if (hash is null) vm.ReturnInt(0);
        else vm.ReturnString(Convert.ToHexString(hash).ToLowerInvariant());
    }
}
