using System.Globalization;
using System.Threading;
using VortexArena.Common.Framework;
using Xunit;

namespace VortexArena.Tests;

/// <summary>The canonical float-vector string parser (VecParse) — the shared replacement for the ~9 private
/// per-file copies. Pins the semantics new call sites rely on: invariant culture, WHITESPACE separators,
/// all-or-nothing parsing, and the min-arity gate.</summary>
public class VecParseTests
{
    [Theory]
    [InlineData("1 2 3", 3, new[] { 1f, 2f, 3f })]
    [InlineData(" 456 1288 220  45 10 ", 3, new[] { 456f, 1288f, 220f, 45f, 10f })] // optional tail kept
    [InlineData("-1 -1 -1", 3, new[] { -1f, -1f, -1f })]
    [InlineData("0.5\t2", 2, new[] { 0.5f, 2f })]
    [InlineData("1\n2\r\n3", 3, new[] { 1f, 2f, 3f })]   // Split(null) = every whitespace char, per the
    [InlineData("1\v2\f3", 3, new[] { 1f, 2f, 3f })]     // call sites this replaced (pasted multi-line values)
    public void Parses_Valid_Vectors(string s, int min, float[] expected)
    {
        Assert.True(VecParse.TryParseFloats(s, min, out float[] vals));
        Assert.Equal(expected, vals);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData("   ", 3)]
    [InlineData("1 2", 3)]        // too few
    [InlineData("1 2 x", 3)]      // bad token → whole parse fails (no zero-fill)
    [InlineData("1;2;3", 3)]      // unsupported separator
    [InlineData("1,2,3", 3)]      // comma is NOT a separator (DP Math_atov splits on space/tab only)
    public void Rejects_Invalid_Input(string? s, int min)
    {
        Assert.False(VecParse.TryParseFloats(s, min, out float[] vals));
        Assert.Empty(vals);
    }

    [Fact]
    public void DecimalCommaTypo_Fails_Loudly_Instead_Of_Silently_Shifting_Components()
    {
        // The reason comma is not a separator. `cl_ghost_items_color "-1,5 -1 -1"` is a decimal-comma typo for
        // "-1.5 -1 -1". With ',' as a separator it split into FOUR valid tokens (-1, 5, -1, -1) and the caller
        // took the first three → (-1, 5, -1) = a bright-green ghost, silently. The contract is that a malformed
        // config value is loud at the call site, so the caller falls back to its documented default.
        Assert.False(VecParse.TryParseFloats("-1,5 -1 -1", 3, out float[] vals));
        Assert.Empty(vals);
    }

    [Fact]
    public void Parses_Under_A_Comma_Decimal_Culture()
    {
        // Config/CLI values are always invariant-formatted ("1.5"), regardless of the machine's locale. Pin it:
        // under a comma-decimal culture a naive float.Parse would reject "1.5" (or read it as 15).
        CultureInfo prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.True(VecParse.TryParseFloats("1.5 -2.25 3", 3, out float[] vals));
            Assert.Equal(new[] { 1.5f, -2.25f, 3f }, vals);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }
}
