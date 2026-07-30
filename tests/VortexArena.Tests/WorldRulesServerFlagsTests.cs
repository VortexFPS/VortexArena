using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// [Wave 8e sv-world-rules] Tests for the world-rules boot side-effects this unit owns: the networked
/// <c>serverflags</c> bit table (QC readlevelcvars, server/world.qc:2189-2195) and the worldspawn
/// animated-lightstyle table (QC spawnfunc(worldspawn), server/world.qc:882-920). Both are pure/static, so they
/// are exercised directly without the full GameWorld boot.
/// </summary>
public class WorldRulesServerFlagsTests
{
    [Fact]
    public void ServerFlags_BitValues_MatchBaseConstantsQh()
    {
        // QC common/constants.qh:16-20 (BIT(N)).
        Assert.Equal(1 << 0, ServerFlags.AllowFullbright);
        Assert.Equal(1 << 1, ServerFlags.Teamplay);
        Assert.Equal(1 << 2, ServerFlags.PlayerStats);
        Assert.Equal(1 << 3, ServerFlags.PlayerStatsCustom);
        Assert.Equal(1 << 4, ServerFlags.ForbidPickupTimer);
    }

    [Fact]
    public void LightStyles_Table_MatchesBaseStrings()
    {
        LightStyles.InstallWorldspawnTable();

        // verbatim from server/world.qc:882-920 (the named 12 + style 63).
        Assert.Equal("m", LightStyles.Frames(0));
        Assert.Equal("mmnmmommommnonmmonqnmmo", LightStyles.Frames(1));
        Assert.Equal("abcdefghijklmnopqrstuvwxyzyxwvutsrqponmlkjihgfedcba", LightStyles.Frames(2));
        Assert.Equal("mmmmmaaaaammmmmaaaaaabcdefgabcdefg", LightStyles.Frames(3));
        Assert.Equal("mamamamamama", LightStyles.Frames(4));
        Assert.Equal("jklmnopqrstuvwxyzyxwvutsrqponmlkj", LightStyles.Frames(5));
        Assert.Equal("nmonqnmomnmomomno", LightStyles.Frames(6));
        Assert.Equal("mmmaaaabcdefgmmmmaaaammmaamm", LightStyles.Frames(7));
        Assert.Equal("mmmaaammmaaammmabcdefaaaammmmabcdefmmmaaaa", LightStyles.Frames(8));
        Assert.Equal("aaaaaaaazzzzzzzz", LightStyles.Frames(9));
        Assert.Equal("mmamammmmammamamaaamammma", LightStyles.Frames(10));
        Assert.Equal("abcdefghijklmnopqrrqponmlkjihgfedcba", LightStyles.Frames(11));
        Assert.Equal("a", LightStyles.Frames(63));

        // styles 12-62 are unset (12-31 unused here, 32-62 are runtime switchable lights).
        Assert.Null(LightStyles.Frames(12));
        Assert.Null(LightStyles.Frames(40));
        Assert.Null(LightStyles.Frames(62));
    }

    [Theory]
    [InlineData('a', 0.0f)]        // dark
    [InlineData('m', 1.0f)]        // normal
    [InlineData('z', 25f / 12f)]   // brightest ('z'-'a')/('m'-'a') = 25/12 ~= 2.083
    public void LightStyles_Sample_DecodesBrightnessChar(char frame, float expected)
    {
        LightStyles.InstallWorldspawnTable();
        // a single-frame style holds that brightness for all time. Use the documented styles to reach each char:
        // style 0 = "m" (always normal), style 63 = "a" (always dark), style 9 "aaaaaaaazzzzzzzz" reaches 'z'.
        if (frame == 'm')
            Assert.Equal(expected, LightStyles.Sample(0, 1.234f), 3);
        else if (frame == 'a')
            Assert.Equal(expected, LightStyles.Sample(63, 1.234f), 3);
        else
            // style 9 second half is 'z'; t=1.0s => frame 10 => 'z'.
            Assert.Equal(expected, LightStyles.Sample(9, 1.0f), 3);
    }

    [Fact]
    public void LightStyles_Sample_UnknownStyle_IsSteadyNormal()
    {
        LightStyles.InstallWorldspawnTable();
        // an out-of-table / unstyled index renders steady (1.0).
        Assert.Equal(1f, LightStyles.Sample(40, 5f));
        Assert.Equal(1f, LightStyles.Sample(-1, 5f));
        Assert.Equal(1f, LightStyles.Sample(999, 5f));
    }

    [Fact]
    public void LightStyles_Sample_FastStrobe_AlternatesOverTime()
    {
        LightStyles.InstallWorldspawnTable();
        // style 4 "mamamamamama" alternates normal/dark each 0.1s frame.
        float f0 = LightStyles.Sample(4, 0.00f);  // frame 0 = 'm' = 1.0
        float f1 = LightStyles.Sample(4, 0.10f);  // frame 1 = 'a' = 0.0
        Assert.Equal(1.0f, f0, 3);
        Assert.Equal(0.0f, f1, 3);
    }
}
