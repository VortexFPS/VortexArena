using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.QuakeC;

/// <summary>
/// The string builtins against what DarkPlaces' C does (Base/darkplaces/prvm_cmds.c and the helpers it
/// calls). Expected values were worked out from the C, not from the port; where one is not what a
/// reader would guess, the comment says which lines produce it.
/// </summary>
public class QcStringBuiltinsTests
{
    /// <summary>A raw integer cell as an argument (an entity number, or what sprintf's %i reads).</summary>
    private readonly record struct Raw(int Cell);

    private sealed class StringTestHost : IQcHost
    {
        public List<string> Warnings { get; } = new();
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<string> Cvars { get; } = new();

        public void Print(string text) { }
        public void Warning(string text) => Warnings.Add(text);
        public bool Developer => true;
        public bool Utf8Enabled { get; set; } = true;
        public double RealTime => 0;
        public bool CvarExists(string name) => Cvars.Contains(name);
        public string CvarString(string name) => "";
        public float CvarFloat(string name) => 0;
        public string CvarDefaultString(string name) => "";
        public string CvarDescription(string name) => "";
        public int CvarTypeFlags(string name) => 0;
        public void CvarSet(string name, string value) { }
        public bool RegisterCvar(string name, string value, int flags) => false;
        // Deliberately unfiltered: the builtin must apply DarkPlaces' rule itself.
        public IEnumerable<string> CvarNames(string prefix, string antiPrefix) => Cvars;
        public void LocalCommand(string text) { }
        public Stream? OpenRead(string path) => Files.TryGetValue(path, out byte[]? data) ? new MemoryStream(data) : null;
        public Stream? OpenWrite(string path, bool append) => null;
        public IReadOnlyList<string> Search(string pattern, bool caseInsensitive, string? packFile) => Array.Empty<string>();
        public string WhichPack(string path) => "";
    }

    // vm_cl_builtins[] in clvm_cmds.c, the string rows.
    private static readonly (int Number, string Name)[] Expected =
    {
        (26, "ftos"), (27, "vtos"), (65, "etos"), (81, "stof"),
        (114, "strlen"), (115, "strcat"), (116, "substring"), (117, "stov"), (118, "strzone"), (119, "strunzone"),
        (221, "strstrofs"), (222, "str2chr"), (223, "chr2str"), (224, "strconv"), (225, "strpad"),
        (226, "infoadd"), (227, "infoget"), (228, "strncmp"), (229, "strcasecmp"), (230, "strncasecmp"),
        (441, "tokenize"), (442, "argv"),
        (460, "buf_create"), (461, "buf_del"), (462, "buf_getsize"), (463, "buf_copy"), (464, "buf_sort"),
        (465, "buf_implode"), (466, "bufstr_get"), (467, "bufstr_set"), (468, "bufstr_add"), (469, "bufstr_free"),
        (476, "strlennocol"), (477, "strdecolorize"), (479, "tokenizebyseparator"),
        (480, "strtolower"), (481, "strtoupper"), (484, "strreplace"), (485, "strireplace"),
        (494, "crc16"), (510, "uri_escape"), (511, "uri_unescape"),
        (514, "tokenize_console"), (515, "argv_start_index"), (516, "argv_end_index"), (517, "buf_cvarlist"),
        (535, "buf_loadfile"), (536, "buf_writefile"), (537, "bufstr_find"), (538, "matchpattern"),
        (627, "sprintf"), (639, "digest_hex"),
    };

    /// <summary>A VM whose only functions are the string builtins, called directly.</summary>
    private sealed class Rig
    {
        public QcVm Vm { get; }
        public StringTestHost Host { get; } = new();
        public QcStringBuiltins Strings { get; }
        private readonly Dictionary<int, int> _functions = new();

        public Rig(bool utf8 = true, long? bufferMemory = null, int? maxBuffers = null)
        {
            ProgsBuilder b = new();
            foreach ((int number, string name) in Expected) b.Builtin(name, number);
            Vm = b.BuildVm();
            Host.Utf8Enabled = utf8;
            Strings = bufferMemory is null && maxBuffers is null
                ? new QcStringBuiltins(Vm, Host)
                : new QcStringBuiltins(Vm, Host) { MaxStringBufferMemory = bufferMemory ?? long.MaxValue, MaxStringBuffers = maxBuffers ?? 65536 };
            Strings.Register();
            foreach ((int number, string name) in Expected) _functions[number] = Vm.FindFunction(name);
        }

        public Rig Call(int number, params object[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case string s: Vm.SetArgInt(i, Vm.EngineString(s)); break;
                    case float f: Vm.SetArgFloat(i, f); break;
                    case int n: Vm.SetArgFloat(i, n); break;
                    case double d: Vm.SetArgFloat(i, (float)d); break;
                    case QcVector v: Vm.SetArgVector(i, v); break;
                    case Raw raw: Vm.SetArgInt(i, raw.Cell); break;
                    default: throw new ArgumentException(args[i].GetType().Name);
                }
            }
            Vm.Execute(_functions[number], args.Length);
            return this;
        }

        public string S(int number, params object[] args) => Call(number, args).Vm.ResultString;
        public float F(int number, params object[] args) => Call(number, args).Vm.ResultFloat;
    }

    private static readonly Rig Utf = new();
    private static readonly Rig Bytes = new(utf8: false);
    private static Rig For(bool utf8) => utf8 ? Utf : Bytes;

    [Fact]
    public void RegistersExactlyTheStringRowsOfTheClientBuiltinTable()
    {
        Assert.Equal(Expected, new Rig().Strings.Registered.OrderBy(e => e.Number).ToArray());
        Assert.All(Expected, e => Assert.True(Utf.Vm.HasBuiltin(e.Number)));
    }

    [Fact]
    public void NullString_IsReturnedOnlyWhereDarkPlacesReturnsOfsNull()
    {
        // Programs test the handle (`if (s)`), so "" from PRVM_SetTempString and OFS_NULL are different answers.
        Rig rig = new();
        float b = Buf(rig);
        rig.Call(467, b, 1, "");
        rig.Call(441, "\"\"");

        // Empty temp strings: a real handle whose text is "".
        Assert.NotEqual(0, rig.Call(116, "hello", 5, 1).Vm.ResultInt);   // prvm_cmds.c:2466
        Assert.NotEqual(0, rig.Call(227, "\\a\\1", "zz").Vm.ResultInt);  // prvm_cmds.c:5158
        Assert.NotEqual(0, rig.Call(115, "").Vm.ResultInt);
        Assert.NotEqual(0, rig.Call(223).Vm.ResultInt);
        Assert.NotEqual(0, rig.Call(627, "").Vm.ResultInt);
        Assert.NotEqual(0, rig.Call(477, "^1").Vm.ResultInt);
        Assert.NotEqual(0, rig.Call(442, 0).Vm.ResultInt);               // an empty token is still a token
        Assert.NotEqual(0, rig.Call(466, b, 1).Vm.ResultInt);            // a slot set to ""
        Assert.Equal("", rig.Vm.ResultString);

        // The null string.
        Assert.Equal(0, rig.Call(442, 1).Vm.ResultInt);                  // prvm_cmds.c:2835
        Assert.Equal(0, rig.Call(466, b, 0).Vm.ResultInt);               // prvm_cmds.c:4204: unset slot
        Assert.Equal(0, rig.Call(466, 99, 0).Vm.ResultInt);
        Assert.Equal(0, rig.Call(465, 99, ",").Vm.ResultInt);            // prvm_cmds.c:4166
        Assert.Equal(0, rig.Call(639, "MD5", "x").Vm.ResultInt);         // prvm_cmds.c:5255
    }

    [Fact]
    public void WrongArgumentCount_IsAProgramFault()
    {
        // VM_SAFEPARMCOUNT calls prog->error_cmd (prvm_cmds.h:206-207).
        Assert.Throws<QcRuntimeException>(() => Utf.Call(114));
        Assert.Throws<QcRuntimeException>(() => Utf.Call(116, "abc", 1));
        Assert.Throws<QcRuntimeException>(() => Utf.Call(442));
    }

    // ---- ftos / vtos / etos / stof / stov -----------------------------------------------------------

    [Theory]
    [InlineData(1f, "1")]
    [InlineData(-3f, "-3")]
    [InlineData(16777216f, "16777216")]
    [InlineData(0.5f, "0.500000")]          // prvm_cmds.c:884-887: whole numbers "%.0f", the rest "%f"
    [InlineData(1.25e-7f, "0.000000")]      // "%f" never switches to an exponent
    [InlineData(-2.75f, "-2.750000")]
    [InlineData(1e30f, "1000000015047466219876688855040.000000")] // does not fit an int, so "%f" of the exact value
    [InlineData(-0f, "-0")]                 // (int)-0.0 == -0.0, and "%.0f" keeps the sign
    [InlineData(float.NaN, "nan")]
    [InlineData(float.PositiveInfinity, "inf")]
    public void Ftos(float value, string expected) => Assert.Equal(expected, Utf.S(26, value));

    [Fact]
    public void Vtos_IsThreeFieldsOfWidthFiveWithOneDecimal()
    {
        // prvm_cmds.c:924 "'%5.1f %5.1f %5.1f'". 2.25 is exact in binary, so it rounds half to even.
        Assert.Equal("'  1.0   2.2  -3.0'", Utf.S(27, new QcVector(1, 2.25f, -3)));
        Assert.Equal("'12345.5   0.0  -0.1'", Utf.S(27, new QcVector(12345.5f, 0, -0.06f)));
    }

    [Fact]
    public void Etos() => Assert.Equal("entity 5", Utf.S(65, new Raw(5)));

    [Theory]
    [InlineData("12.5abc", 12.5f)]   // atof converts the longest numeric prefix
    [InlineData("  \t-3", -3f)]
    [InlineData("abc", 0f)]
    [InlineData("", 0f)]
    [InlineData("1e3", 1000f)]
    [InlineData("1e", 1f)]           // an exponent with no digits is not part of the number
    [InlineData(".5", 0.5f)]
    [InlineData("5.", 5f)]
    [InlineData("+7", 7f)]
    [InlineData("0x10", 16f)]        // strtod reads hexadecimal
    [InlineData("0x1.8p1", 3f)]
    [InlineData("1e999", float.PositiveInfinity)]
    [InlineData("-inf", float.NegativeInfinity)]
    [InlineData(".", 0f)]
    [InlineData("- 1", 0f)]
    public void Stof(string text, float expected) => Assert.Equal(expected, Utf.F(81, text));

    [Fact]
    public void Stof_ConcatenatesItsArguments()
    {
        // VM_stof is VM_VarString then atof (prvm_cmds.c:957-959).
        Assert.Equal(12.5f, Utf.F(81, "1", "2.", "5"));
        Assert.True(float.IsNaN(Utf.F(81, "nan")));
    }

    [Theory]
    [InlineData("'1 2 3'", 1f, 2f, 3f)]
    [InlineData("1 2 3", 1f, 2f, 3f)]
    [InlineData("1 2", 1f, 2f, 0f)]
    [InlineData("  4 abc 6", 4f, 0f, 0f)]      // mathlib.c:867: a non-number stops the parse
    [InlineData("0 0 7", 0f, 0f, 7f)]          // a real zero does not
    [InlineData("-1\t+2 .5", -1f, 2f, 0.5f)]
    [InlineData("'1 2' 3", 1f, 2f, 0f)]        // mathlib.c:871: so does the closing quote
    [InlineData("", 0f, 0f, 0f)]
    [InlineData("1,2,3", 1f, 0f, 0f)]          // only spaces and tabs separate
    public void Stov(string text, float x, float y, float z)
    {
        QcVector v = Utf.Call(117, text).Vm.ResultVector;
        Assert.Equal((x, y, z), (v.X, v.Y, v.Z));
    }

    // ---- strlen / strcat / substring / zone ---------------------------------------------------------

    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("hello", 5, 5)]
    [InlineData("héllo", 5, 6)]
    [InlineData("a\U0001F600b", 3, 6)]   // an astral character is one character, four bytes
    [InlineData("", 1, 3)]
    public void Strlen_CountsCharactersOrBytes(string text, int characters, int bytes)
    {
        Assert.Equal(characters, Utf.F(114, text));
        Assert.Equal(bytes, Bytes.F(114, text));
    }

    [Fact]
    public void Strcat()
    {
        Assert.Equal("abc", Utf.S(115, "a", "b", "c"));
        Assert.Equal("a", Utf.S(115, "a"));
        Assert.Equal("", Utf.S(115, "", ""));
        Assert.Equal("12345678", Utf.S(115, "1", "2", "3", "4", "5", "6", "7", "8"));
    }

    [Fact]
    public void Strcat_IsCutAtTheTempstringSize_WithAWarning()
    {
        // VM_VarString (prvm_cmds.c:295-303): 16383 bytes and a VM_Warning.
        Rig rig = new();
        string big = new('x', 10000);
        Assert.Equal(16383, rig.S(115, big, big).Length);
        Assert.Single(rig.Host.Warnings);
        // The limit is bytes: 6000 two-byte characters are 12000 bytes, so only 8191 of 12000 fit.
        string wide = new('é', 6000);
        Assert.Equal(8191, rig.S(115, wide, wide).TrimEnd('�').Length);
    }

    [Theory]
    [InlineData("hello", 1f, 3f, "ell")]
    [InlineData("hello", 0f, 100f, "hello")]
    [InlineData("hello", 5f, 1f, "")]
    [InlineData("hello", 9f, 1f, "")]
    [InlineData("hello", 2e9f, 1f, "")]
    [InlineData("hello", -3f, 2f, "ll")]       // prvm_cmds.c:2448-2453: a negative start counts from the end
    [InlineData("hello", -100f, 2f, "he")]     // and is clamped to the string
    [InlineData("hello", 1f, -1f, "ello")]     // prvm_cmds.c:2459: length += strlen - start + 1
    [InlineData("hello", 1f, -2f, "ell")]
    [InlineData("hello", 2f, -100f, "llo")]    // still negative: (size_t) makes it "everything"
    [InlineData("hello", -2f, -1f, "lo")]
    [InlineData("hello", 0f, 0f, "")]
    [InlineData("hello", 1.9f, 2.9f, "el")]    // both truncate toward zero
    [InlineData("hello", float.NaN, 2f, "he")] // (int)NaN is INT_MIN on x86, which clamps to the start
    public void Substring_Ascii(string text, float start, float length, string expected)
    {
        Assert.Equal(expected, Utf.S(116, text, start, length));
        Assert.Equal(expected, Bytes.S(116, text, start, length));
    }

    [Fact]
    public void Substring_IndexesCharactersInUtf8Mode_AndBytesOtherwise()
    {
        Assert.Equal("él", Utf.S(116, "héllo", 1, 2));
        Assert.Equal("é", Bytes.S(116, "héllo", 1, 2));
        Assert.Equal("\U0001F600", Utf.S(116, "a\U0001F600b", 1, 1));
        Assert.Equal("b", Utf.S(116, "a\U0001F600b", 2, 5));
        Assert.Equal("llo", Utf.S(116, "héllo", -3, 3));
        Assert.Equal("éllo", Utf.S(116, "héllo", 1, -1));
        Assert.Equal("", Utf.S(116, "héllo", 5, 1));
    }

    [Fact]
    public void Strzone_OutlivesTheCall_AndStrunzoneFreesIt()
    {
        Rig rig = new();
        int before = rig.Vm.ZonedStringCount;
        int handle = rig.Call(118, "keep me").Vm.ResultInt;
        Assert.Equal("keep me", rig.Vm.GetString(handle));
        Assert.Equal(before + 1, rig.Vm.ZonedStringCount);
        rig.Call(119, new Raw(handle));
        Assert.Equal(before, rig.Vm.ZonedStringCount);
        rig.Call(119, new Raw(handle));          // twice, and on a non-zoned string: ignored
        rig.Call(119, new Raw(0));
        Assert.Equal(before, rig.Vm.ZonedStringCount);
    }

    // ---- strstrofs / str2chr / chr2str --------------------------------------------------------------

    [Theory]
    [InlineData("hello world", "o", 0f, 4f)]
    [InlineData("hello world", "o", 5f, 7f)]
    [InlineData("hello", "z", 0f, -1f)]
    [InlineData("abc", "", 1f, 1f)]            // strstr finds the empty string where it starts looking
    [InlineData("abc", "c", 99f, -1f)]
    [InlineData("abc", "c", -1f, -1f)]         // prvm_cmds.c:4916: a negative start is a huge size_t, the end
    [InlineData("abc", "", -1f, 3f)]
    public void Strstrofs(string text, string find, float start, float expected)
    {
        Assert.Equal(expected, Utf.F(221, text, find, start));
        Assert.Equal(expected, Bytes.F(221, text, find, start));
    }

    [Fact]
    public void Strstrofs_ReturnsACharacterIndexInUtf8Mode()
    {
        Assert.Equal(2, Utf.F(221, "héllo", "l"));
        Assert.Equal(3, Bytes.F(221, "héllo", "l"));
        Assert.Equal(3, Utf.F(221, "héllo", "l", 3));
    }

    [Fact]
    public void Str2chr()
    {
        Assert.Equal(98, Utf.F(222, "abc", 1));
        Assert.Equal(0, Utf.F(222, "abc", 3));
        // prvm_cmds.c:4939: the index goes through u8_bytelen as a size_t, so -1 is the terminator.
        Assert.Equal(0, Utf.F(222, "abc", -1));
        Assert.Equal(0xE9, Utf.F(222, "héllo", 1));
        Assert.Equal(0xC3, Bytes.F(222, "héllo", 1));
        Assert.Equal(0x1F600, Utf.F(222, "a\U0001F600", 1));
    }

    [Fact]
    public void Chr2str()
    {
        Assert.Equal("Hi", Utf.S(223, 72, 105));
        Assert.Equal("é", Utf.S(223, 0xE9));
        Assert.Equal("\U0001F600", Utf.S(223, 0x1F600));
        Assert.Equal("ab", Utf.S(223, 97, 0, 98));     // utf8lib.c:633: NUL encodes as nothing
        Assert.Equal("", Utf.S(223));
        Assert.Equal("", Utf.S(223, 0x110000));        // past the last code point: nothing
        // utf8lib.c:636: with UTF-8 off the private-use Quake glyph range folds back to a byte.
        Assert.Equal("A", Bytes.S(223, 0xE041));
        Assert.Equal("A", Bytes.S(223, 0x141));        // and anything else is its low byte
    }

    // ---- strconv / strpad ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0, 0, "HeLLo 123!", "hello 123!")]
    [InlineData(2, 0, 0, "HeLLo 123!", "HELLO 123!")]
    [InlineData(0, 0, 0, "HeLLo 123!", "HeLLo 123!")]
    [InlineData(0, 1, 1, "HeLLo 123!", "HeLLo 123!")]   // already "white"
    public void Strconv_Case(int ccase, int alpha, int number, string text, string expected) =>
        Assert.Equal(expected, Utf.S(224, ccase, alpha, number, text));

    [Fact]
    public void Strconv_RedIsTheGlyph128Higher()
    {
        // prvm_cmds.c:5019-5055: 'a' becomes byte 0xE1. That is not UTF-8, and a .NET string cannot
        // carry it: the documented limit of this port (QcStringUtf8) is that it reads back as U+FFFD.
        Assert.Equal("\ufffd", Utf.S(224, 0, 2, 0, "a"));
        // "3 = redspecial": the digit glyphs 30 below '0' (prvm_cmds.c:4992-4994); those are still ASCII.
        Assert.Equal("\u0012\u0013", Utf.S(224, 0, 0, 3, "01"));
        Assert.Equal("01", Utf.S(224, 0, 0, 1, "\u0012\u0013"));
    }

    [Theory]
    [InlineData(5f, "ab", "ab   ")]      // prvm_cmds.c:5117-5119: positive pads on the right - printf reversed
    [InlineData(-5f, "ab", "   ab")]
    [InlineData(1f, "abc", "abc")]
    [InlineData(0f, "abc", "abc")]
    [InlineData(3f, "", "   ")]
    public void Strpad(float width, string text, string expected) => Assert.Equal(expected, Utf.S(225, width, text));

    [Fact]
    public void Strpad_CountsBytes_AndJoinsItsArguments()
    {
        // "%*s" is the C library's: the width is bytes even with utf8_enable 1.
        Assert.Equal("é   ", Utf.S(225, 5, "é"));
        Assert.Equal("abcd  ", Utf.S(225, 6, "ab", "cd"));
    }

    [Fact]
    public void Strpad_HugeWidth_IsCutAtTheTempstringSize()
    {
        string right = Utf.S(225, 2e9f, "x");
        Assert.Equal(16383, right.Length);
        Assert.StartsWith("x ", right);
        string left = Utf.S(225, -2e9f, "x");
        Assert.Equal(new string(' ', 16383), left);
        Assert.Equal(16383, Utf.S(225, float.NegativeInfinity, "x").Length);
    }

    // ---- infoadd / infoget --------------------------------------------------------------------------

    [Theory]
    [InlineData("", "k", "v", "\\k\\v")]
    [InlineData("\\a\\1\\b\\2", "a", "9", "\\a\\9\\b\\2")]   // replaced in place
    [InlineData("\\a\\1\\b\\2", "b", "9", "\\a\\1\\b\\9")]
    [InlineData("\\a\\1\\b\\2", "a", "", "\\b\\2")]          // an empty value removes the key
    [InlineData("\\a\\1", "c", "3", "\\a\\1\\c\\3")]
    [InlineData("\\ab\\1", "a", "3", "\\ab\\1\\a\\3")]       // a key is matched whole, not as a prefix
    [InlineData("\\a\\1", "b\\c", "3", "\\a\\1")]            // com_infostring.c:84-98: refused, unchanged
    [InlineData("\\a\\1", "b", "x\"y", "\\a\\1")]
    [InlineData("\\a\\1", "", "3", "\\a\\1")]
    [InlineData("\\a\\1", "zz", "", "\\a\\1")]
    public void Infoadd(string info, string key, string value, string expected) =>
        Assert.Equal(expected, new Rig().S(226, info, key, value));

    [Theory]
    [InlineData("\\a\\1\\b\\2", "b", "2")]
    [InlineData("\\a\\1\\b\\2", "a", "1")]
    [InlineData("\\a\\1\\b\\2", "c", "")]
    [InlineData("\\ab\\1", "a", "")]
    [InlineData("\\a", "a", "")]
    [InlineData("a\\1", "a", "")]           // an info string starts with a backslash
    [InlineData("\\a\\1", "", "")]
    [InlineData("\\a\\1\\", "a", "1")]
    public void Infoget(string info, string key, string expected) => Assert.Equal(expected, Utf.S(227, info, key));

    [Fact]
    public void Infoadd_ThatWouldNotFit_LeavesTheStringAlone()
    {
        // com_infostring.c:118.
        Rig rig = new();
        string value = new('v', 16000);
        string info = rig.S(226, "", "a", value);
        Assert.Equal(16003, info.Length);
        Assert.Equal(info, rig.S(226, info, "b", new string('w', 500)));
    }

    // ---- comparisons --------------------------------------------------------------------------------

    [Fact]
    public void Strncmp_AndTheCaseInsensitivePair()
    {
        Assert.Equal(-1, Utf.F(228, "abc", "abd"));
        Assert.Equal(1, Utf.F(228, "b", "a"));
        Assert.Equal(0, Utf.F(228, "abc", "abc"));
        Assert.Equal(0, Utf.F(228, "abc", "abd", 2));
        Assert.Equal(-1, Utf.F(228, "abc", "abd", 3));
        Assert.Equal(0, Utf.F(228, "abc", "xyz", 0));
        Assert.Equal(-1, Utf.F(228, "ab", "abc"));
        Assert.Equal(-1, Utf.F(228, "abc", "abd", -1));   // (size_t)-1: compare everything
        Assert.Equal(-1, Utf.F(228, "ABC", "abc"));       // 'A' < 'a'

        Assert.Equal(0, Utf.F(229, "ABC", "abc"));
        Assert.Equal(-1, Utf.F(229, "a", "B"));
        Assert.Equal(0, Utf.F(230, "ABc", "abd", 2));
        Assert.Equal(-1, Utf.F(230, "ABc", "abd", 3));
        // Only ASCII folds (C locale): É and é stay different.
        Assert.NotEqual(0, Utf.F(229, "É", "é"));
    }

    // ---- case and colour ----------------------------------------------------------------------------

    [Fact]
    public void StrtolowerAndStrtoupper()
    {
        Assert.Equal("hello 1!", Utf.S(480, "HeLLo 1!"));
        Assert.Equal("HELLO 1!", Utf.S(481, "HeLLo 1!"));
        Assert.Equal("école", Utf.S(480, "ÉCOLE"));
        Assert.Equal("ÉCOLE", Utf.S(481, "école"));
        // common.c:1081-1088: with UTF-8 off only A-Z change.
        Assert.Equal("Éa", Bytes.S(480, "ÉA"));
        Assert.Equal("éA", Bytes.S(481, "éa"));
        // utf8lib.c: dotless i uppercases to I in DarkPlaces' table.
        Assert.Equal("I", Utf.S(481, "ı"));
    }

    [Theory]
    [InlineData("^1red^7", "red", 3)]
    [InlineData("^xF00hi", "hi", 2)]
    [InlineData("^xf0", "^^xf0", 4)]         // not three hex digits: the caret is doubled so it prints
    [InlineData("^xZZZ", "^^xZZZ", 5)]
    [InlineData("a^^b", "a^^b", 3)]          // an escaped caret stays escaped, and is one character wide
    [InlineData("a^", "a^^", 2)]             // common.c:1325-1331: an unfinished code is finished
    [InlineData("^q", "^q", 2)]
    [InlineData("plain", "plain", 5)]
    [InlineData("^1^2^3", "", 0)]
    [InlineData("", "", 0)]
    public void StrdecolorizeAndStrlennocol(string text, string stripped, int visible)
    {
        Assert.Equal(stripped, Utf.S(477, text));
        Assert.Equal(visible, Utf.F(476, text));
        Assert.Equal(stripped, Bytes.S(477, text));
        Assert.Equal(visible, Bytes.F(476, text));
    }

    [Fact]
    public void Strlennocol_CountsCharactersInUtf8Mode()
    {
        Assert.Equal(2, Utf.F(476, "^1é\U0001F600"));
        Assert.Equal(6, Bytes.F(476, "^1é\U0001F600"));
    }

    [Fact]
    public void Strdecolorize_ThatOverflows_ReturnsNothing()
    {
        // common.c:1294: APPEND returns 0 from the function when the buffer runs out.
        string carets = string.Concat(Enumerable.Repeat("^q", 8191)) + "^";
        Assert.Equal(16383, Encoding.UTF8.GetByteCount(carets));
        Assert.Equal("", Utf.S(477, carets));
    }

    // ---- strreplace ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("l", "L", "hello", "heLLo", "heLLo")]
    [InlineData("L", "x", "heLlo", "hexlo", "hexxo")]
    [InlineData("xyz", "q", "hello", "hello", "hello")]
    [InlineData("hello!", "q", "hello", "hello", "hello")]  // longer than the subject
    [InlineData("ll", "", "hello", "heo", "heo")]
    [InlineData("aa", "b", "aaaaa", "bba", "bba")]          // left to right, no overlap
    [InlineData("", "-", "ab", "-a-b-", "-a-b-")]           // prvm_cmds.c:2511-2526: before every byte, and at the end
    [InlineData("", "-", "", "-", "-")]
    [InlineData("a", "a", "banana", "banana", "banana")]
    public void StrreplaceAndStrireplace(string search, string replace, string subject, string exact, string anyCase)
    {
        Assert.Equal(exact, Utf.S(484, search, replace, subject));
        Assert.Equal(anyCase, Utf.S(485, search, replace, subject));
    }

    [Fact]
    public void Strreplace_ThatWouldMultiplyTheText_StopsAtTheBuffer()
    {
        string result = Utf.S(484, "a", new string('b', 5000), new string('a', 16000));
        Assert.Equal(new string('b', 16383), result);
        Assert.Equal(16383, Utf.S(484, "", new string('b', 9000), new string('a', 9000)).Length);
    }

    // ---- crc16 / digest_hex / uri -------------------------------------------------------------------

    [Fact]
    public void Crc16()
    {
        // CRC-16/CCITT-FALSE check value; com_crc16.c:36 initial value 0xffff, polynomial 0x1021.
        Assert.Equal(0x29B1, Utf.F(494, 0, "123456789"));
        Assert.Equal(0x29B1, Utf.F(494, 0, "1234", "56789"));
        Assert.Equal(0xFFFF, Utf.F(494, 0, ""));
        Assert.Equal(Utf.F(494, 0, "abc"), Utf.F(494, 1, "ABC"));
        Assert.NotEqual(Utf.F(494, 0, "abc"), Utf.F(494, 0, "ABC"));
    }

    [Fact]
    public void DigestHex()
    {
        // RFC 1320 appendix A.5 and FIPS 180-2 appendix B.1.
        Assert.Equal("31d6cfe0d16ae931b73c59d7e0c089c0", Utf.S(639, "MD4", ""));
        Assert.Equal("a448017aaf21d8525fc10ae87aa6729d", Utf.S(639, "MD4", "abc"));
        Assert.Equal("d9130a8164549fe818874806e1c7014b", Utf.S(639, "MD4", "message ", "digest"));
        Assert.Equal("e33b4ddc9c38f2199c3e7b164fcc0536", Utf.S(639, "MD4", "12345678901234567890123456789012345678901234567890123456789012345678901234567890"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Utf.S(639, "SHA256", "abc"));
        // prvm_cmds.c:5240: an unknown digest is the null string, without a warning.
        Assert.Equal(0, Utf.Call(639, "MD5", "abc").Vm.ResultInt);
        Assert.Equal(0, Utf.Call(639, "md4", "abc").Vm.ResultInt);
    }

    [Fact]
    public void UriEscapeAndUnescape()
    {
        Assert.Equal("a%20b%2F%C3%A9", Utf.S(510, "a b/é"));
        Assert.Equal("AZaz09-_.!~'()", Utf.S(510, "AZaz09-_.!~'()"));
        Assert.Equal("a b/é", Utf.S(511, "a%20b%2f%C3%A9"));
        Assert.Equal("%zz%4", Utf.S(511, "%zz%4"));
        Assert.Equal("ab", Utf.S(511, "a%00b"));      // prvm_cmds.c:5383: %00 is swallowed
        Assert.Equal("100%", Utf.S(511, "100%"));
        // prvm_cmds.c:5330: stops while a whole escape still fits, so 5461 escapes of 3 bytes.
        Assert.Equal(16383, Utf.S(510, new string(' ', 9000)).Length);
        Assert.Equal(16381, Utf.S(510, new string('a', 16381) + "  ").Length);
    }

    // ---- tokenizers ---------------------------------------------------------------------------------

    private static string[] Tokens(Rig rig, int count) => Enumerable.Range(0, count).Select(i => rig.S(442, i)).ToArray();

    [Fact]
    public void Tokenize_SplitsWordsQuotesAndPunctuation()
    {
        Rig rig = new();
        //                         0123456789012345678
        Assert.Equal(7, rig.F(441, "foo bar; \"a b\" {x}"));
        Assert.Equal(new[] { "foo", "bar", ";", "a b", "{", "x", "}" }, Tokens(rig, 7));
        Assert.Equal("}", rig.S(442, -1));            // prvm_cmds.c:2829: negative counts from the end
        Assert.Equal("foo", rig.S(442, -7));
        Assert.Equal(0, rig.Call(442, 7).Vm.ResultInt);
        Assert.Equal(0, rig.Call(442, -8).Vm.ResultInt);
        Assert.Equal(9, rig.F(515, 3));               // the opening quote
        Assert.Equal(14, rig.F(516, 3));              // just past the closing one
        Assert.Equal(17, rig.F(515, -1));
        Assert.Equal(18, rig.F(516, -1));
        Assert.Equal(-1, rig.F(515, 99));
        Assert.Equal(-1, rig.F(516, -99));
    }

    [Theory]
    [InlineData("a // c\nb /* d */ c", new[] { "a", "b", "c" })]
    [InlineData("'x\\ny' \"a\\\"b\" \"t\\tq\\\\\"", new[] { "x\ny", "a\"b", "t\tq\\" })]  // common.c:765-775
    [InlineData("a:b,c(d)[e]", new[] { "a", ":", "b", ",", "c", "(", "d", ")", "[", "e", "]" })]
    [InlineData("\"unterminated", new[] { "unterminated" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("  \t\r\n ", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("a /* never closed", new[] { "a" })]
    [InlineData("a/b /*/ c", new[] { "a/b", "c" })]     // "/*/" is a complete comment (common.c:749-750)
    [InlineData("x\\", new[] { "x\\" })]
    [InlineData("\"x\\", new[] { "x" })]                // a trailing backslash in quotes is dropped
    public void Tokenize_Cases(string text, string[] expected)
    {
        Rig rig = new();
        Assert.Equal(expected.Length, rig.F(441, text));
        Assert.Equal(expected, Tokens(rig, expected.Length));
    }

    [Theory]
    [InlineData("say \"hello world\" // c", new[] { "say", "hello world" })]
    [InlineData("a;b {c} 'd e'", new[] { "a;b", "{c}", "'d", "e'" })]   // no punctuation, no single quotes
    [InlineData("\"a\\\"b\" \"a\\nb\" \"c\\\\\"", new[] { "a\"b", "a\\nb", "c\\" })]  // common.c:864: only \" and \\
    [InlineData("a /* b */", new[] { "a", "/*", "b", "*/" })]
    [InlineData("", new string[0])]
    public void TokenizeConsole_Cases(string text, string[] expected)
    {
        Rig rig = new();
        Assert.Equal(expected.Length, rig.F(514, text));
        Assert.Equal(expected, Tokens(rig, expected.Length));
    }

    [Fact]
    public void TokenizeConsole_IndexesAreByteOffsets()
    {
        Rig rig = new();
        Assert.Equal(2, rig.F(514, "éé \"b c\""));
        Assert.Equal(0, rig.F(515, 0));
        Assert.Equal(4, rig.F(516, 0));
        Assert.Equal(5, rig.F(515, 1));
        Assert.Equal(10, rig.F(516, 1));
    }

    [Fact]
    public void TokenizeBySeparator()
    {
        Rig rig = new();
        Assert.Equal(4, rig.F(479, "10.1.2.3", "."));
        Assert.Equal(new[] { "10", "1", "2", "3" }, Tokens(rig, 4));
        Assert.Equal(3, rig.F(515, 1));
        Assert.Equal(4, rig.F(516, 1));

        Assert.Equal(3, rig.F(479, "a, b;c", ", ", ";"));
        Assert.Equal(new[] { "a", "b", "c" }, Tokens(rig, 3));

        // prvm_cmds.c:2812: the loop ends when a separator is the last thing in the string, so a
        // trailing separator adds no empty token - but a leading or doubled one does, and "" is one token.
        Assert.Equal(1, rig.F(479, "a.", "."));
        Assert.Equal("a", rig.S(442, 0));
        Assert.Equal(1, rig.F(479, "", "."));
        Assert.Equal("", rig.S(442, 0));
        Assert.Equal(2, rig.F(479, "..", "."));
        Assert.Equal(2, rig.F(479, ".a", "."));
        Assert.Equal(new[] { "", "a" }, Tokens(rig, 2));
        // Blank separators are skipped (prvm_cmds.c:2775); with none the string is one token.
        Assert.Equal(1, rig.F(479, "a.b", ""));
        Assert.Equal("a.b", rig.S(442, 0));
        // The first separator that matches wins, in argument order.
        Assert.Equal(3, rig.F(479, "a::b", ":", "::"));
        Assert.Equal(new[] { "a", "", "b" }, Tokens(rig, 3));
        Assert.Equal(2, rig.F(479, "a::b", "::", ":"));
    }

    [Fact]
    public void Tokenizers_StopAtTheTokenLimit()
    {
        // sizeof(tokens)/sizeof(tokens[0]) is VM_TEMPSTRING_MAXSIZE / 2 = 8192 (prvm_cmds.c:2676), and
        // the input is first cut to 16383 bytes (prvm_cmds.c:2686).
        Rig rig = new();
        string many = string.Concat(Enumerable.Repeat("a ", 12000));
        Assert.Equal(8192, rig.F(441, many));
        Assert.Equal(8192, rig.F(514, many));
        Assert.Equal(8192, rig.F(479, string.Concat(Enumerable.Repeat("a.", 12000)), "."));
        Assert.Equal(1, rig.F(441, new string('x', 40000)));
        Assert.Equal(16383, rig.S(442, 0).Length);
    }

    [Fact]
    public void Argv_SurvivesLaterCalls()
    {
        Rig rig = new();
        rig.Call(441, "one two");
        rig.S(115, "something", " else");
        Assert.Equal("two", rig.S(442, 1));
        rig.Call(441, "");
        Assert.Equal(0, rig.Call(442, 0).Vm.ResultInt);
    }

    // ---- sprintf ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("%d", 3.7f, "3")]                   // every argument is a float unless told otherwise
    [InlineData("%d", -3.7f, "-3")]
    [InlineData("%5d|", 42f, "   42|")]
    [InlineData("%-5d|", 42f, "42   |")]
    [InlineData("%05d|", 42f, "00042|")]            // prvm_cmds.c:5880-5885: a leading 0 is the flag
    [InlineData("%05d|", -42f, "-0042|")]
    [InlineData("%+d", 5f, "+5")]
    [InlineData("% d", 5f, " 5")]
    [InlineData("%.3d", 5f, "005")]
    [InlineData("%.0d|", 0f, "|")]                  // C: zero with precision zero is no digits
    [InlineData("%08.3d|", 5f, "     005|")]        // C: a precision turns the 0 flag off
    [InlineData("%u", 3e9f, "3000000000")]
    [InlineData("%x", 255f, "ff")]
    [InlineData("%X", 255f, "FF")]
    [InlineData("%#x", 255f, "0xff")]
    [InlineData("%#x", 0f, "0")]
    [InlineData("%o", 8f, "10")]
    [InlineData("%#o", 8f, "010")]
    [InlineData("%x", -1f, "ffffffffffffffff")]     // (uintmax_t) of a negative float, as x86-64 does it
    [InlineData("%d", 1e30f, "-9223372036854775808")]
    [InlineData("%f", 3.5f, "3.500000")]
    [InlineData("%.2f", 3.14159f, "3.14")]
    [InlineData("%.0f", 2.5f, "2")]                 // exact tie: round half to even
    [InlineData("%.0f", 3.5f, "4")]
    [InlineData("%#.0f", 3f, "3.")]
    [InlineData("%8.3f|", -3.14159f, "  -3.142|")]
    [InlineData("%-8.3f|", 3.14159f, "3.142   |")]
    [InlineData("%08.3f|", -3.14159f, "-003.142|")]
    [InlineData("%5.1f%%", 99.5f, " 99.5%")]
    [InlineData("%e", 12345.678f, "1.234568e+04")]
    [InlineData("%.2E", 0.00012345f, "1.23E-04")]
    [InlineData("%e", 0f, "0.000000e+00")]
    [InlineData("%g", 0.0001f, "0.0001")]
    [InlineData("%g", 0.00001f, "1e-05")]
    [InlineData("%g", 100000f, "100000")]
    [InlineData("%g", 1000000f, "1e+06")]
    [InlineData("%g", 0.1f, "0.1")]
    [InlineData("%g", 123456792f, "1.23457e+08")]
    [InlineData("%g", 0f, "0")]
    [InlineData("%g", 1.5f, "1.5")]
    [InlineData("%.3g", 0.00001234f, "1.23e-05")]
    [InlineData("%.10g", 0.1f, "0.1000000015")]     // the float's own digits, not 0.1's
    [InlineData("%#g", 1f, "1.00000")]
    [InlineData("%G", 1e-10f, "1E-10")]
    [InlineData("%.0g", 25f, "2e+01")]
    [InlineData("%g", 999999.5f, "1e+06")]          // rounds up into the next exponent
    [InlineData("%f", float.NaN, "nan")]
    [InlineData("%5F|", float.NegativeInfinity, " -INF|")]
    [InlineData("%05f|", float.PositiveInfinity, "  inf|")]
    [InlineData("%c", 65f, "A")]
    [InlineData("%c", 233f, "é")]
    [InlineData("%3c|", 65f, "  A|")]
    [InlineData("%-3c|", 65f, "A  |")]
    [InlineData("%c|", 0f, "|")]
    [InlineData("%hd", 3.7f, "3")]
    [InlineData("%jd", 3.7f, "3")]
    [InlineData("100%%", 0f, "100%")]
    [InlineData("%ld", 1f, "1065353216")]           // "l" reads the cell as an integer: 1.0f's bits
    [InlineData("%i", 1f, "1065353216")]            // and so does %i (prvm_cmds.c:6004)
    public void Sprintf_OneFloat(string format, float value, string expected) =>
        Assert.Equal(expected, Utf.S(627, format, value));

    [Fact]
    public void Sprintf_IntegerCells()
    {
        Assert.Equal("42", Utf.S(627, "%i", new Raw(42)));
        Assert.Equal("-7", Utf.S(627, "%ld", new Raw(-7)));
        // prvm_cmds.c:6050: a raw int is sign-extended to uintmax_t.
        Assert.Equal("fffffffffffffff9", Utf.S(627, "%lx", new Raw(-7)));
        Assert.Equal("42.000000", Utf.S(627, "%lf", new Raw(42)));
        Assert.Equal("1 2 3", Utf.S(627, "%lv", new QcVector(BitConverter.Int32BitsToSingle(1), BitConverter.Int32BitsToSingle(2), BitConverter.Int32BitsToSingle(3))));
    }

    [Fact]
    public void Sprintf_StringsAndPositions()
    {
        Assert.Equal("a=b", Utf.S(627, "%s=%s", "a", "b"));
        Assert.Equal("b a", Utf.S(627, "%2$s %1$s", "a", "b"));
        Assert.Equal("a a a", Utf.S(627, "%1$s %1$s %s", "a", "b"));   // a positional one does not advance the cursor
        Assert.Equal("   ab|", Utf.S(627, "%5s|", "ab"));
        Assert.Equal("ab   |", Utf.S(627, "%-5s|", "ab"));
        Assert.Equal("ab", Utf.S(627, "%.2s", "abcdef"));
        Assert.Equal("  ab|", Utf.S(627, "%4.2s|", "abcdef"));
        Assert.Equal("", Utf.S(627, "%s"));                              // GETARG_STRING past argc is ""
        Assert.Equal("0", Utf.S(627, "%d"));
        Assert.Equal("0|", Utf.S(627, "%9$d|%9$s", 1f));
        Assert.Equal("7 x", Utf.S(627, "%d %s", 7f, "x"));
    }

    [Fact]
    public void Sprintf_StarWidthAndPrecision()
    {
        Assert.Equal("   42", Utf.S(627, "%*d", 5f, 42f));
        Assert.Equal("42   |", Utf.S(627, "%*d|", -5f, 42f));          // prvm_cmds.c:5923-5927
        Assert.Equal("3.14", Utf.S(627, "%.*f", 2f, 3.14159f));
        Assert.Equal("  3.1", Utf.S(627, "%*.*f", 5f, 1f, 3.14159f));
        Assert.Equal("  3.1", Utf.S(627, "%3$*1$.*2$f", 5f, 1f, 3.14159f));
        Assert.Equal("3.141590", Utf.S(627, "%.*f", -1f, 3.14159f));   // negative precision is "not given"
    }

    [Fact]
    public void Sprintf_StringWidthSkipsColourCodes()
    {
        // utf8lib.c:899-908 u8_strpad_colorcodes is the default; "+" selects plain u8_strpad
        // (prvm_cmds.c:6108-6111) and "#" the C library's byte-counting %s (prvm_cmds.c:6097-6102).
        Assert.Equal("   ^1ab|", Utf.S(627, "%5s|", "^1ab"));
        Assert.Equal(" ^1ab|", Utf.S(627, "%+5s|", "^1ab"));
        Assert.Equal("^xF00ab   |", Utf.S(627, "%-5s|", "^xF00ab"));
        Assert.Equal("^1a", Utf.S(627, "%.1s", "^1ab^2c"));
        Assert.Equal("^1ab", Utf.S(627, "%.2s", "^1ab^2c"));           // a trailing code is not included
        Assert.Equal("^1", Utf.S(627, "%+.2s", "^1ab"));
        Assert.Equal(" a^^b|", Utf.S(627, "%4s|", "a^^b"));            // ^^ is one visible character
    }

    [Fact]
    public void Sprintf_StringWidthIsCharactersInUtf8Mode()
    {
        Assert.Equal("  é|", Utf.S(627, "%3s|", "é"));
        Assert.Equal(" é|", Bytes.S(627, "%3s|", "é"));
        Assert.Equal(" é|", Utf.S(627, "%#3s|", "é"));       // "#": bytes regardless
        Assert.Equal("é\U0001F600", Utf.S(627, "%.2s", "é\U0001F600z"));
        Assert.Equal("\U0001F600", Utf.S(627, "%c", (float)0x1F600));
        Assert.Equal("  é|", Utf.S(627, "%3c|", 233f));
    }

    [Fact]
    public void Sprintf_Vectors()
    {
        // prvm_cmds.c:6060-6073: three %g joined by spaces, each with the directive's flags.
        Assert.Equal("1 2.5 -3", Utf.S(627, "%v", new QcVector(1, 2.5f, -3)));
        Assert.Equal("1.0 2.5 -3.0", Utf.S(627, "%#.2v", new QcVector(1, 2.5f, -3)));
        Assert.Equal("    1   2.5    -3", Utf.S(627, "%5v", new QcVector(1, 2.5f, -3)));
        Assert.Equal("0 0 0", Utf.S(627, "%v"));
        Assert.Equal("1E-10 0 0", Utf.S(627, "%V", new QcVector(1e-10f, 0, 0)));
    }

    [Theory]
    [InlineData("ab%qcd", "ab")]         // prvm_cmds.c:6114-6116: unknown conversion ends the output
    [InlineData("abc%", "abc")]
    [InlineData("a%.xd", "a")]           // prvm_cmds.c:5977-5981: "." needs digits or "*"
    [InlineData("a%*1d", "a")]           // "*N" must be followed by "$"
    [InlineData("a%5", "a")]
    public void Sprintf_InvalidDirective_EndsTheOutputWithAWarning(string format, string expected)
    {
        Rig rig = new();
        Assert.Equal(expected, rig.S(627, format, 1f));
        Assert.Single(rig.Host.Warnings);
    }

    [Fact]
    public void Sprintf_HostileWidthsAndPrecisions_AreBoundedByTheBuffer()
    {
        Rig rig = new();
        Assert.Equal(new string(' ', 16383), rig.S(627, "%999999999d", 1f));
        Assert.Equal("1" + new string(' ', 16382), rig.S(627, "%-999999999d", 1f));
        Assert.Equal(new string('0', 16383), rig.S(627, "%.999999999d", 1f));
        Assert.Equal("1." + new string('0', 16381), rig.S(627, "%.999999999f", 1f));
        Assert.Equal(16383, rig.S(627, "%.999999999e", 1f).Length);
        Assert.Equal(16383, rig.S(627, "%#.999999999g", 1f).Length);
        Assert.Equal("1", rig.S(627, "%.999999999g", 1f));
        Assert.Equal(16383, rig.S(627, "%99999999999999999999s", "x").Length);
        Assert.Equal(16383, rig.S(627, "%*d", 2e9f, 1f).Length);
        Assert.Equal(16383, rig.S(627, "%*d", -2e9f, 1f).Length);
        Assert.Equal("1", rig.S(627, "%*d", float.NaN, 1f));
        Assert.Equal(16383, rig.S(627, "%999999999c", 65f).Length);
        Assert.Equal(16383, rig.S(627, "%999999999v", new QcVector(1, 2, 3)).Length);
        Assert.Equal("x", rig.S(627, "%2147483648$sx", "y"));
        // Once the buffer is full the rest of the format is consumed without output.
        Assert.Equal(16383, rig.S(627, "%20000s%d%s%", "x", 1f, "y").Length);
        Assert.Equal(16383, rig.S(627, new string('z', 30000)).Length);
        string big = new('q', 16000);
        Assert.Equal(16383, rig.S(627, "%s%s", big, big).Length);
    }

    // ---- string buffers -----------------------------------------------------------------------------

    private static float Buf(Rig rig) => rig.F(460);

    [Fact]
    public void BufCreate_ReusesTheLowestFreeHandle()
    {
        Rig rig = new();
        Assert.Equal(0, Buf(rig));
        Assert.Equal(1, Buf(rig));
        Assert.Equal(2, Buf(rig));
        rig.Call(461, 0);
        rig.Call(461, 1);
        Assert.Equal(1, rig.Strings.StringBufferCount);
        Assert.Equal(0, Buf(rig));
        Assert.Equal(1, Buf(rig));
        Assert.Equal(3, Buf(rig));
        // prvm_cmds.c:3998: the optional format must be "string".
        Assert.Equal(4, rig.F(460, "string"));
        Assert.Equal(-1, rig.F(460, "float"));
        Assert.Throws<QcRuntimeException>(() => rig.Call(460, "string", 0, 0));
    }

    [Fact]
    public void Bufstr_SetGetAddFree()
    {
        Rig rig = new();
        float b = Buf(rig);
        Assert.Equal(0, rig.F(462, b));
        rig.Call(467, b, 3, "x");
        Assert.Equal(4, rig.F(462, b));
        Assert.Equal("x", rig.S(466, b, 3));
        Assert.Equal(0, rig.Call(466, b, 1).Vm.ResultInt);     // an empty slot is the null string
        Assert.Equal(0, rig.Call(466, b, 99).Vm.ResultInt);
        Assert.Equal(0, rig.Call(466, b, -1).Vm.ResultInt);
        Assert.Equal(0, rig.F(468, b, "y", 0));                // order 0: the first empty slot
        Assert.Equal(1, rig.F(468, b, "w", 0));
        Assert.Equal(4, rig.F(468, b, "z", 1));                // order 1: after the last string
        Assert.Equal(5, rig.F(462, b));
        rig.Call(469, b, 4);
        Assert.Equal(4, rig.F(462, b));                        // BufStr_Shrink drops trailing empties
        rig.Call(469, b, 3);
        Assert.Equal(2, rig.F(462, b));
        rig.Call(469, b, 50);                                  // past the end: nothing
        Assert.Equal(2, rig.F(462, b));
        rig.Call(467, b, 1, "replaced");
        Assert.Equal("replaced", rig.S(466, b, 1));
        // prvm_cmds.c:4249: bufstr_set stores even the null string as a real (empty) entry.
        rig.Call(467, b, 5, "");
        Assert.Equal(6, rig.F(462, b));
        Assert.Empty(rig.Host.Warnings);
    }

    [Fact]
    public void BufstrAdd_FillsTheLowestEmptySlot()
    {
        // prvm_cmds.c:4287-4289. The port caches where to start looking; this is what it must not get wrong.
        Rig rig = new();
        float b = Buf(rig);
        for (int i = 0; i < 5; i++) Assert.Equal(i, rig.F(468, b, "s", 0));
        rig.Call(469, b, 3);
        rig.Call(469, b, 1);
        Assert.Equal(1, rig.F(468, b, "s", 0));
        Assert.Equal(3, rig.F(468, b, "s", 0));
        Assert.Equal(5, rig.F(468, b, "s", 0));
        rig.Call(467, b, 9, "far");               // leaves 6, 7 and 8 empty
        Assert.Equal(6, rig.F(468, b, "s", 0));
        rig.Call(469, b, 9);
        rig.Call(469, b, 6);                      // the buffer shrinks back to six strings
        Assert.Equal(6, rig.F(462, b));
        Assert.Equal(6, rig.F(468, b, "s", 0));
        rig.Call(469, b, 0);
        rig.Call(464, b, 0, 0);                   // sorting moves the empty slot to the end and trims it
        Assert.Equal(6, rig.F(462, b));
        Assert.Equal(6, rig.F(468, b, "s", 0));
        for (int i = 6; i >= 0; i--) rig.Call(469, b, i);
        Assert.Equal(0, rig.F(462, b));
        Assert.Equal(0, rig.F(468, b, "s", 0));
    }

    [Fact]
    public void Buffers_BadHandlesAndIndexes_WarnAndDoNothing()
    {
        Rig rig = new();
        float b = Buf(rig);
        Assert.Equal(-1, rig.F(462, 7));
        Assert.Equal(-1, rig.F(462, -1));
        Assert.Equal(-1, rig.F(462, 2e9f));
        Assert.Equal(-1, rig.F(462, float.NaN));
        rig.Call(461, 7);
        rig.Call(467, 7, 0, "x");
        Assert.Equal(-1, rig.F(468, 7, "x", 1));
        rig.Call(469, 7, 0);
        rig.Call(464, 7, 0, 0);
        Assert.Equal(0, rig.Call(465, 7, ",").Vm.ResultInt);
        Assert.Equal(0, rig.Call(466, 7, 0).Vm.ResultInt);
        Assert.Equal(11, rig.Host.Warnings.Count);

        rig.Host.Warnings.Clear();
        rig.Call(467, b, -1, "x");
        rig.Call(467, b, 1000000, "x");       // prvm_cmds.c:4243
        rig.Call(467, b, 2e9f, "x");
        rig.Call(469, b, -1);
        Assert.Equal(-1, rig.F(468, b, new Raw(0), 1));   // prvm_cmds.c:4277: the null string cannot be added
        rig.Call(464, b, 0, 0);               // prvm_cmds.c:4130: sorting an empty buffer
        Assert.Equal(6, rig.Host.Warnings.Count);
        Assert.Equal(0, rig.F(462, b));
        rig.Call(467, b, 999999, "last");
        Assert.Equal(1000000, rig.F(462, b));
    }

    [Fact]
    public void BufImplode_PutsTheGlueBeforeEveryString()
    {
        Rig rig = new();
        float b = Buf(rig);
        Assert.Equal(0, rig.Call(465, b, ",").Vm.ResultInt);
        rig.Call(467, b, 0, "a");
        rig.Call(467, b, 2, "c");
        rig.Call(467, b, 1, "b");
        // prvm_cmds.c:4184: dp_strlcat(k, sep) is not conditional on i > 0, unlike the length count.
        Assert.Equal(",a,b,c", rig.S(465, b, ","));
        Assert.Equal("abc", rig.S(465, b, ""));
        rig.Call(469, b, 1);
        Assert.Equal("--a--c", rig.S(465, b, "--"));
        // prvm_cmds.c:4182: a string that would not fit ends the result; nothing is cut in half.
        rig.Call(467, b, 1, new string('x', 16000));
        rig.Call(467, b, 2, new string('y', 500));
        Assert.Equal(16003, rig.S(465, b, ",").Length);
    }

    [Fact]
    public void BufSort()
    {
        Rig rig = new();
        float b = Buf(rig);
        string[] Read() => Enumerable.Range(0, (int)rig.F(462, b)).Select(i => rig.S(466, b, i)).ToArray();
        void Fill(params string[] items)
        {
            for (int i = (int)rig.F(462, b) - 1; i >= 0; i--) rig.Call(469, b, i);
            for (int i = 0; i < items.Length; i++) rig.Call(467, b, i, items[i]);
        }

        Fill("b", "a", "", "c", "B");
        rig.Call(464, b, 0, 0);
        // prvm_cmds.c:3873-3881: strncmp order (bytes, so capitals first), empty strings last.
        Assert.Equal(new[] { "B", "a", "b", "c", "" }, Read());
        rig.Call(464, b, 0, 1);
        Assert.Equal(new[] { "c", "b", "a", "B", "" }, Read());

        // cmplength compares only that many bytes; equal keys keep their order here.
        Fill("ab", "aa", "b1", "a");
        rig.Call(464, b, 1, 0);
        Assert.Equal(new[] { "ab", "aa", "a", "b1" }, Read());
        rig.Call(464, b, -5, 0);          // prvm_cmds.c:4136: <= 0 means the whole string
        Assert.Equal(new[] { "a", "aa", "ab", "b1" }, Read());

        // Empty slots sort to the end and are then trimmed off.
        Fill("z");
        rig.Call(467, b, 4, "y");
        rig.Call(464, b, 0, 0);
        Assert.Equal(new[] { "y", "z" }, Read());
    }

    [Fact]
    public void BufCopy()
    {
        Rig rig = new();
        float from = Buf(rig), to = Buf(rig);
        rig.Call(467, from, 0, "a");
        rig.Call(467, from, 2, "c");
        rig.Call(467, to, 5, "old");
        rig.Call(463, from, to);
        Assert.Equal(3, rig.F(462, to));
        Assert.Equal("a", rig.S(466, to, 0));
        Assert.Equal("c", rig.S(466, to, 2));
        Assert.Equal(3, rig.F(462, from));
        rig.Call(467, to, 0, "changed");
        Assert.Equal("a", rig.S(466, from, 0));      // a copy, not an alias
        Assert.Empty(rig.Host.Warnings);
        rig.Call(463, from, from);
        rig.Call(463, 9, to);
        rig.Call(463, from, 9);
        Assert.Equal(3, rig.Host.Warnings.Count);
    }

    [Fact]
    public void BufCvarlist()
    {
        Rig rig = new();
        rig.Host.Cvars.AddRange(new[] { "g_a", "g_b", "sv_x", "g_bx" });
        float b = Buf(rig);
        rig.Call(467, b, 9, "old");
        string[] Read() => Enumerable.Range(0, (int)rig.F(462, b)).Select(i => rig.S(466, b, i)).ToArray();

        // prvm_cmds.c:4741-4745.
        rig.Call(517, b, "g_", "");
        Assert.Equal(new[] { "g_a", "g_b", "g_bx" }, Read());
        rig.Call(517, b, "g_", "g_b");
        Assert.Equal(new[] { "g_a" }, Read());
        rig.Call(517, b, "", "g_");
        Assert.Equal(new[] { "sv_x" }, Read());
        rig.Call(517, b, "*x");                 // a "*" or "?" makes it a pattern over the whole name
        Assert.Equal(new[] { "sv_x", "g_bx" }, Read());
        rig.Call(517, b, "g_", "*x");
        Assert.Equal(new[] { "g_a", "g_b" }, Read());
        rig.Call(517, b, "nothing");
        Assert.Equal(0, rig.F(462, b));
    }

    [Fact]
    public void BufLoadfile_AppendsLines()
    {
        Rig rig = new();
        rig.Host.Files["data/lines.txt"] = Encoding.UTF8.GetBytes("a\r\nb\n\nc\rd");
        rig.Host.Files["plain.txt"] = Encoding.UTF8.GetBytes("only\n");
        float b = Buf(rig);
        rig.Call(467, b, 0, "first");
        // prvm_cmds.c:4358: "data/" is tried first. Lines end at \n, \r or \r\n; a blank line is a string.
        Assert.Equal(1, rig.F(535, "lines.txt", b));
        Assert.Equal(new[] { "first", "a", "b", "", "c", "d" }, Enumerable.Range(0, 6).Select(i => rig.S(466, b, i)).ToArray());
        Assert.Equal(6, rig.F(462, b));
        Assert.Equal(1, rig.F(535, "plain.txt", b));
        Assert.Equal(7, rig.F(462, b));
        Assert.Equal(0, rig.F(535, "missing.txt", b));
        Assert.Equal(0, rig.F(535, "plain.txt", 55));
    }

    [Fact]
    public void BufWritefile_WritesOneLinePerString_ThroughTheHostsFileTable()
    {
        Rig rig = new();
        float b = Buf(rig);
        rig.Call(467, b, 0, "a");
        rig.Call(467, b, 2, "cé");
        rig.Call(467, b, 3, "d");
        Assert.Equal(0, rig.F(536, 1, b));                  // no file table wired up: warning
        Assert.Single(rig.Host.Warnings);

        MemoryStream file = new();
        rig.Strings.OpenFile = handle => handle == 1 ? file : null;
        Assert.Equal(1, rig.F(536, 1, b));
        Assert.Equal("a\ncé\nd\n", Encoding.UTF8.GetString(file.ToArray()));   // prvm_cmds.c:4488: empty slots are skipped
        file.SetLength(0);
        Assert.Equal(1, rig.F(536, 1, b, 2));
        Assert.Equal("cé\nd\n", Encoding.UTF8.GetString(file.ToArray()));
        file.SetLength(0);
        Assert.Equal(1, rig.F(536, 1, b, 0, 2));            // two slots, one of them empty
        Assert.Equal("a\n", Encoding.UTF8.GetString(file.ToArray()));
        Assert.Equal(0, rig.F(536, 2, b));
        Assert.Equal(0, rig.F(536, 1, b, 9));
        Assert.Equal(0, rig.F(536, 1, b, 0, -1));
        Assert.Equal(0, rig.F(536, 1, 44));
    }

    [Theory]
    [InlineData("hello", "hello", 1, 1)]     // whole
    [InlineData("hello", "hell", 1, 0)]
    [InlineData("hello", "he", 2, 1)]        // left
    [InlineData("hello", "lo", 2, 0)]
    [InlineData("hello", "lo", 3, 1)]        // right
    [InlineData("abab", "ab", 3, 0)]         // prvm_cmds.c:4573: only the first occurrence is tried
    [InlineData("hello", "ell", 4, 1)]       // middle
    [InlineData("hello", "xyz", 4, 0)]
    [InlineData("hello", "h*o", 5, 1)]       // pattern
    [InlineData("hello", "h?llo", 5, 1)]
    [InlineData("hello", "h?lo", 5, 0)]
    [InlineData("hello", "*", 5, 1)]
    [InlineData("", "*", 5, 1)]
    [InlineData("", "?", 5, 0)]
    [InlineData("hello", "hello", 0, 1)]     // auto: no wildcard is "whole"
    [InlineData("hello", "*lo", 0, 1)]       // auto: "*x" is "right"
    [InlineData("hello", "he*", 0, 1)]       // auto: falls to the pattern matcher, same answer
    [InlineData("hello", "*ell*", 0, 1)]
    [InlineData("hello", "*a*", 0, 0)]
    [InlineData("hello", "hel", 0, 0)]
    public void Matchpattern(string text, string pattern, int rule, int expected) =>
        Assert.Equal(expected, Utf.F(538, text, pattern, rule));

    [Fact]
    public void Matchpattern_StartOffsetAndBadRule()
    {
        Assert.Equal(1, Utf.F(538, "hello", "llo", 1, 2));
        Assert.Equal(1, Utf.F(538, "hello", "o", 1, 99));        // prvm_cmds.c:4682: clamped to the last byte
        Assert.Equal(1, Utf.F(538, "hello", "hello", 1, -5));
        Rig rig = new();
        Assert.Equal(0, rig.F(538, "hello", "hello", 6));
        Assert.Equal(0, rig.F(538, "hello", "hello", -1));
        Assert.Equal(2, rig.Host.Warnings.Count);
    }

    [Fact]
    public void Matchpattern_AdversarialPattern_DoesNotBacktrackExponentially()
    {
        string text = new('a', 12000);
        string pattern = string.Concat(Enumerable.Repeat("*a", 40)) + "*b";
        Assert.Equal(0, Utf.F(538, text, pattern, 5));
        Assert.Equal(1, Utf.F(538, text, string.Concat(Enumerable.Repeat("*a", 40)) + "*", 5));
    }

    [Fact]
    public void BufstrFind()
    {
        Rig rig = new();
        float b = Buf(rig);
        string[] items = { "apple", "banana", "cherry", "apricot", "avocado" };
        for (int i = 0; i < items.Length; i++) rig.Call(467, b, i * 2, items[i]);   // odd slots stay empty

        Assert.Equal(2, rig.F(537, b, "banana", 1));
        Assert.Equal(0, rig.F(537, b, "ap", 2));
        Assert.Equal(6, rig.F(537, b, "ap", 2, 1));               // from a start index
        Assert.Equal(-1, rig.F(537, b, "ap", 2, 7));
        Assert.Equal(4, rig.F(537, b, "rry", 3));
        Assert.Equal(8, rig.F(537, b, "oca", 4));
        Assert.Equal(4, rig.F(537, b, "c*y", 5));
        Assert.Equal(4, rig.F(537, b, "*rry", 0));
        Assert.Equal(-1, rig.F(537, b, "*ana", 0));               // "right" tries only the first "ana" in banana
        Assert.Equal(8, rig.F(537, b, "a*", 0, 1, 7));            // step 7: slots 1 and 8
        Assert.Equal(-1, rig.F(537, b, "a*", 0, 1, 2e9f));        // a step that would overflow an int
        Assert.Equal(-1, rig.F(537, b, "zzz", 1));
        Assert.Empty(rig.Host.Warnings);
        Assert.Equal(-1, rig.F(537, b, "a", 9));
        Assert.Equal(-1, rig.F(537, b, "a", 1, -1));
        Assert.Equal(-1, rig.F(537, b, "a", 1, 0, 0));
        Assert.Equal(-1, rig.F(537, 31, "a", 1));
        Assert.Equal(4, rig.Host.Warnings.Count);
    }

    [Fact]
    public void Buffers_CannotGrowWithoutBound()
    {
        // DarkPlaces has no limit here; these two are this port's.
        Rig rig = new(bufferMemory: 1 << 20);
        float b = Buf(rig);
        string chunk = new('x', 16000);
        QcRuntimeException e = Assert.Throws<QcRuntimeException>(() =>
        {
            for (int i = 0; i < 1000; i++) rig.Call(467, b, i, chunk);
        });
        Assert.Contains("string buffers", e.Message);
        // The fault left the accounting consistent: freeing makes room again.
        rig.Call(461, b);
        b = Buf(rig);
        rig.Call(467, b, 0, chunk);
        Assert.Equal(chunk, rig.S(466, b, 0));
        // One sparse index costs a megabyte of slots, which this budget does not have.
        Assert.Throws<QcRuntimeException>(() => rig.Call(467, b, 999999, "x"));

        Rig few = new(maxBuffers: 2);
        Assert.Equal(0, Buf(few));
        Assert.Equal(1, Buf(few));
        Assert.Equal(-1, Buf(few));
        few.Call(461, 0);
        Assert.Equal(0, Buf(few));

        few.Strings.Reset();
        Assert.Equal(0, few.Strings.StringBufferCount);
        Assert.Equal(0, Buf(few));
    }
}
