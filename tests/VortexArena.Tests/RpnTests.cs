using System.Collections.Generic;
using System.Globalization;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the RPN calculator VM (<see cref="Rpn"/>) — the port of <c>GenericCommand_rpn</c>
/// (common/command/rpn.qc). Pure: it runs against a fresh <see cref="CvarService"/> and a capturing print sink.
/// Covers arithmetic / def / load / set ops / compares / bound / when / sprintf1s / the bare-cvar default /
/// underflow + leftover prints, and the parity-critical <c>%.9g</c> formatter round-trip.
/// </summary>
public class RpnTests
{
    public RpnTests()
    {
        // These are pure RPN-VM tests with no engine facade. The `time` op, however, reads the live clock when
        // Api.Services is wired (Rpn.cs) — so a facade a previously-run test leaked into the process-global
        // would make `time` push that test's advanced clock instead of the documented 0-fallback. The suite runs
        // serially and class order isn't fixed, so clear the global here to stay order-independent. (Every test
        // that needs a facade installs its own, so nulling it between runs is safe.)
        Api.Services = null!;
    }

    private static (CvarService cvars, List<string> output) Run(params string[] tokens)
    {
        var cvars = new CvarService();
        var output = new List<string>();
        var argv = new List<string> { "rpn" };
        argv.AddRange(tokens);
        Rpn.Run(argv, cvars, output.Add);
        return (cvars, output);
    }

    private static float Stof(string s)
        => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    // ---- def / arithmetic --------------------------------------------------------------------------------

    [Fact]
    public void Def_WritesComputedValueToCvar()
    {
        // rpn /x 2 3 add def  →  cvar x == "5"
        var (cvars, _) = Run("/x", "2", "3", "add", "def");
        Assert.Equal("5", cvars.GetString("x"));
    }

    [Fact]
    public void Def_RoundTripsAFloatThroughPercent9g()
    {
        // rpn /pi 3.14159 def — the value lands as %.9g and must stof back to the same float.
        var (cvars, _) = Run("/pi", "3.14159", "def");
        Assert.Equal((float)3.14159, Stof(cvars.GetString("pi")));
    }

    [Theory]
    [InlineData("2", "3", "add", 5f)]
    [InlineData("10", "4", "sub", 6f)]
    [InlineData("6", "7", "mul", 42f)]
    [InlineData("20", "5", "div", 4f)]
    [InlineData("7", "2", "mod", 1f)]
    [InlineData("2", "10", "pow", 1024f)]
    public void Arithmetic_BinaryOps(string a, string b, string op, float expected)
    {
        var (cvars, _) = Run("/r", a, b, op, "def");
        Assert.Equal(expected, Stof(cvars.GetString("r")));
    }

    [Theory]
    [InlineData("5", "neg", -5f)]
    [InlineData("-3", "abs", 3f)]
    [InlineData("3.7", "floor", 3f)]
    [InlineData("3.2", "ceil", 4f)]
    [InlineData("-2", "sgn", -1f)]
    [InlineData("0", "sgn", 0f)]
    [InlineData("9", "sgn", 1f)]
    [InlineData("0", "not", 1f)]
    [InlineData("5", "not", 0f)]
    public void Arithmetic_UnaryOps(string a, string op, float expected)
    {
        var (cvars, _) = Run("/r", a, op, "def");
        Assert.Equal(expected, Stof(cvars.GetString("r")));
    }

    [Theory]
    [InlineData("3", "5", "max", 5f)]
    [InlineData("3", "5", "min", 3f)]
    public void MinMax(string a, string b, string op, float expected)
    {
        var (cvars, _) = Run("/r", a, b, op, "def");
        Assert.Equal(expected, Stof(cvars.GetString("r")));
    }

    [Theory]
    // bound: stack (deep→top) = min num max; clamps num into [min,max]. QC bound(f3, f2, f).
    [InlineData("0", "5", "10", 5f)]   // in range
    [InlineData("0", "-3", "10", 0f)]  // below min
    [InlineData("0", "20", "10", 10f)] // above max
    public void Bound_ClampsTheMiddleValue(string lo, string mid, string hi, float expected)
    {
        var (cvars, _) = Run("/r", lo, mid, hi, "bound", "def");
        Assert.Equal(expected, Stof(cvars.GetString("r")));
    }

    [Theory]
    // F20: reversed bounds (min>max). The DP bound macro (darkplaces/mathlib.h:34)
    // ((num)>=(min) ? ((num)<(max)?(num):(max)) : (min)) tests against MIN first, so bound(10,5,0)==10 and
    // bound(5,3,1)==5 — NOT 0/1 as the old min(max(min,num),max) nesting returned. Guards the F20 fix.
    [InlineData("10", "5", "0", 10f)] // bound(10,5,0): 5>=10? no → 10  (old Min(Max()) gave 0)
    [InlineData("5", "3", "1", 5f)]   // bound(5,3,1):  3>=5?  no → 5   (old Min(Max()) gave 1)
    public void Bound_ReversedBounds_MatchesDpMacro(string lo, string mid, string hi, float expected)
    {
        var (cvars, _) = Run("/r", lo, mid, hi, "bound", "def");
        Assert.Equal(expected, Stof(cvars.GetString("r")));
    }

    [Theory]
    // QC `when`: f=pop(cond), s=pop, s2=get(peek); cond ? s2 : s. With stack (deep→top) = [v_deep, v_top, cond]
    // → cond true picks v_deep, cond false picks v_top. So `100 200 1 when` == 100; `100 200 0 when` == 200.
    [InlineData("100", "200", "1", 100f)] // cond true  → the deeper value
    [InlineData("100", "200", "0", 200f)] // cond false → the value just under the cond
    public void When_SelectsByCondition(string vDeep, string vTop, string cond, float expected)
    {
        var (cvars, _) = Run("/r", vDeep, vTop, cond, "when", "def");
        Assert.Equal(expected, Stof(cvars.GetString("r")));
    }

    [Theory]
    [InlineData("5", "3", "gt", 1f)]
    [InlineData("3", "5", "gt", 0f)]
    [InlineData("5", "5", "eq", 1f)]
    [InlineData("5", "4", "eq", 0f)]
    [InlineData("3", "5", "lt", 1f)]
    [InlineData("5", "5", "ge", 1f)]
    [InlineData("4", "5", "le", 1f)]
    [InlineData("4", "5", "ne", 1f)]
    public void Comparisons(string a, string b, string op, float expected)
    {
        var (cvars, _) = Run("/r", a, b, op, "def");
        Assert.Equal(expected, Stof(cvars.GetString("r")));
    }

    // ---- crc16 (F19) -------------------------------------------------------------------------------------

    [Theory]
    // The crc16 op: CRC-16/CCITT (XMODEM) with init 0xFFFF (darkplaces/com_crc16.c:36 CRC_INIT_VALUE), poly
    // 0x1021, final xor 0x0000. The seed MUST be 0xFFFF — the old 0x0000 seed gave 39686/0/40406/50018. These
    // expected values are the DP builtin's output (crc16(false, s)).
    [InlineData("test", "8134")]
    [InlineData("abc", "20810")]
    [InlineData("hello", "53870")]
    public void Crc16_MatchesDpSeed0xFFFF(string text, string expected)
    {
        // rpn /r /<text> crc16 def → r == crc16(text)
        var (cvars, _) = Run("/r", "/" + text, "crc16", "def");
        Assert.Equal(expected, cvars.GetString("r"));
    }

    [Fact]
    public void Crc16_EmptyString_Is65535()
    {
        // crc16("") == 0xFFFF == 65535 (the seed itself; the old 0x0000 seed wrongly gave 0). An unset cvar
        // pushes the empty string (QC's bare-token default rpn_push(cvar_string(tok))), so `unsetcvar crc16`
        // computes crc16("") — the cleanest way to get an empty string on the stack (a bare "/" is the div op,
        // and a "/x" literal needs len>=2 so it can't be empty).
        var cvars = new CvarService();
        var output = new List<string>();
        Rpn.Run(new List<string> { "rpn", "/r", "an_unset_cvar_xyz", "crc16", "def" }, cvars, output.Add);
        Assert.Equal("65535", cvars.GetString("r"));
    }

    // ---- time (F22) --------------------------------------------------------------------------------------

    [Fact]
    public void Time_PushesClock_NotEmptyCvarFallback()
    {
        // QC rpn.qc:547-548: `time` does rpn_pushf(time) — it pushes the VM clock, NOT cvar_string("time")
        // (which is empty). With no live services facade wired (a bare CvarService) the port pushes the sim
        // clock fallback 0 — the key regression: the result is the numeric "0", never the empty string the old
        // cvar-fallback produced. (The live-clock value is exercised on the server bus where Api.Services is set.)
        var (cvars, _) = Run("/r", "time", "def");
        Assert.Equal("0", cvars.GetString("r"));
    }

    // ---- stack ops ---------------------------------------------------------------------------------------

    [Fact]
    public void Dup_DuplicatesTop()
    {
        // 7 dup add  →  14
        var (cvars, _) = Run("/r", "7", "dup", "add", "def");
        Assert.Equal(14f, Stof(cvars.GetString("r")));
    }

    [Fact]
    public void Exch_SwapsTopTwo()
    {
        // 10 3 exch sub  →  3 - 10 = -7  (exch makes the stack 3 10, then sub = 3 - 10)
        var (cvars, _) = Run("/r", "10", "3", "exch", "sub", "def");
        Assert.Equal(-7f, Stof(cvars.GetString("r")));
    }

    [Fact]
    public void Load_ReadsACvarValue()
    {
        var cvars = new CvarService();
        cvars.Set("src", "42");
        var output = new List<string>();
        // /dst /src load def  →  dst = cvar_string(src) = "42"
        Rpn.Run(new List<string> { "rpn", "/dst", "/src", "load", "def" }, cvars, output.Add);
        Assert.Equal("42", cvars.GetString("dst"));
    }

    [Fact]
    public void BareToken_PushesCvarValue()
    {
        var cvars = new CvarService();
        cvars.Set("g_speed", "320");
        var output = new List<string>();
        // /dst g_speed def  →  dst = cvar_string(g_speed) (the bare-token default branch)
        Rpn.Run(new List<string> { "rpn", "/dst", "g_speed", "def" }, cvars, output.Add);
        Assert.Equal("320", cvars.GetString("dst"));
    }

    // ---- set / string ops --------------------------------------------------------------------------------

    [Fact]
    public void Union_MergesWordLists()
    {
        // "a b c" "b d" union → "a b c d"  (b is deduped)
        var (cvars, _) = Run("/r", "/a b c", "/b d", "union", "def");
        Assert.Equal("a b c d", cvars.GetString("r"));
    }

    [Fact]
    public void Intersection_KeepsCommonWords()
    {
        // "a b c" "b c d" intersection → "b c"
        var (cvars, _) = Run("/r", "/a b c", "/b c d", "intersection", "def");
        Assert.Equal("b c", cvars.GetString("r"));
    }

    [Fact]
    public void Difference_RemovesSecondSetWords()
    {
        // "a b c" "b" difference → "a c"
        var (cvars, _) = Run("/r", "/a b c", "/b", "difference", "def");
        Assert.Equal("a c", cvars.GetString("r"));
    }

    [Fact]
    public void Sprintf1s_FormatsTheStringArg()
    {
        // "world" "hello %s" sprintf1s → "hello world"
        var (cvars, _) = Run("/r", "/world", "/hello %s", "sprintf1s", "def");
        Assert.Equal("hello world", cvars.GetString("r"));
    }

    [Fact]
    public void Sprintf1s_HonorsWidthAndPrecision()
    {
        // precision cuts, width pads: "abcdef" "%-4.4s|" → "abcd|"
        var (cvars, _) = Run("/r", "/abcdef", "/%.4s|", "sprintf1s", "def");
        Assert.Equal("abcd|", cvars.GetString("r"));
    }

    // ---- diagnostics -------------------------------------------------------------------------------------

    [Fact]
    public void Underflow_PrintsTheStandardMessage()
    {
        // "add" with an empty stack underflows.
        var (_, output) = Run("add");
        Assert.Contains("rpn: stack underflow", output);
    }

    [Fact]
    public void LeftoverStack_IsPrinted()
    {
        // 7 2 mod leaves "1" on the stack (no def) → "rpn: still on stack: 1".
        var (_, output) = Run("7", "2", "mod");
        Assert.Contains("rpn: still on stack: 1", output);
    }

    [Fact]
    public void Def_WithMissingName_ReportsEmptyCvarName()
    {
        // `5 def` pushes only the value, so def's second pop underflows to "" → the empty-name error (QC: the
        // underflowed name is "" so it prints "empty cvar name for 'def'" in addition to the underflow line).
        var (cvars, output) = Run("5", "def");
        Assert.Contains("rpn: empty cvar name for 'def'", output);
    }

    // ---- %.9g formatter ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(5f, "5")]
    [InlineData(-5f, "-5")]
    [InlineData(1.5f, "1.5")]
    [InlineData(100f, "100")]
    [InlineData(0.5f, "0.5")]
    [InlineData(1000000f, "1000000")]
    public void Format9g_MatchesCPrintf_ForCleanValues(float value, string expected)
    {
        Assert.Equal(expected, Rpn.Format9g(value));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    [InlineData(-5f)]
    [InlineData(3.14159f)]
    [InlineData(0.1f)]
    [InlineData(123.456f)]
    [InlineData(-0.001f)]
    [InlineData(1234567f)]
    [InlineData(1e-5f)]
    [InlineData(1e9f)]
    public void Format9g_RoundTripsThroughStof(float value)
    {
        // The contract that matters for the stack/cvar round-trip: stof(format9g(x)) == x for any float that
        // 9 significant digits can represent exactly (which covers all float32 values).
        string s = Rpn.Format9g(value);
        Assert.Equal(value, Stof(s));
    }

    [Fact]
    public void Format9g_LargeValueUsesExponentForm()
    {
        // 1e9 has 10 integer digits > precision 9 → %e form (matching glibc %g).
        string s = Rpn.Format9g(1e9f);
        Assert.Contains("e", s);
        Assert.Equal(1e9f, Stof(s));
    }
}
