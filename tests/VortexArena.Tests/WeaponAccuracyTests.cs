using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises <see cref="WeaponAccuracyEvents.AccuracyByte"/> — the C# port of QC's <c>accuracy_byte(n, d)</c>
/// (server/weapons/accuracy.qc:17-24), the per-weapon accuracy% the HUD/scoreboard reads.
///
/// <para>The headline guard is the ROUNDING: QC uses DP's <c>rint</c> builtin, which rounds half AWAY FROM ZERO
/// (round-half-up here, since <c>n*100/d</c> is always &gt;= 0), NOT .NET's default round-half-to-EVEN (banker's).
/// At a clean 0.5 tie the two disagree (50.5 → 51 vs 50), so a hit/fired pair that lands on a .5 percent would
/// report the wrong byte without the <see cref="System.MidpointRounding.AwayFromZero"/> fix.</para>
/// </summary>
public class WeaponAccuracyTests
{
    [Fact]
    public void Byte_Sentinels_NeverFired_And_OverHundred()
    {
        // 0 = haven't fired (d <= 0); also n < 0 is the "no data" guard.
        Assert.Equal(0, WeaponAccuracyEvents.AccuracyByte(0f, 0f));
        Assert.Equal(0, WeaponAccuracyEvents.AccuracyByte(-1f, 100f));
        // 255 = >100% (hit damage exceeded fired damage, e.g. one beam through two players).
        Assert.Equal(255, WeaponAccuracyEvents.AccuracyByte(150f, 100f));
    }

    [Fact]
    public void Byte_Encodes_Accuracy_Plus_One()
    {
        // 1..101 == accuracy% + 1. 0% → 1, 100% → 101, a clean 50% → 51.
        Assert.Equal(1, WeaponAccuracyEvents.AccuracyByte(0f, 100f));
        Assert.Equal(101, WeaponAccuracyEvents.AccuracyByte(100f, 100f));
        Assert.Equal(51, WeaponAccuracyEvents.AccuracyByte(50f, 100f));
    }

    [Theory]
    // n/d, expected percent (before the +1). The .5 ties MUST round UP (rint, away-from-zero), not to-even.
    [InlineData(50.5f, 100f, 51)]   // 50.5% → 51 (round-half-up); banker's would give 50
    [InlineData(1.5f, 100f, 2)]     // 1.5% → 2 ; banker's would give 2 anyway, but next case disagrees
    [InlineData(2.5f, 100f, 3)]     // 2.5% → 3 (round-half-up); banker's would give 2 (nearest even)
    [InlineData(0.5f, 100f, 1)]     // 0.5% → 1 (round-half-up); banker's would give 0
    public void Byte_Rounds_Half_Up_Like_Rint(float n, float d, int expectedPercent)
    {
        // accuracy_byte returns 1 + rint(n*100/d); pull the percent back out.
        Assert.Equal(1 + expectedPercent, WeaponAccuracyEvents.AccuracyByte(n, d));
    }

    [Fact]
    public void Byte_NonHalf_Rounds_To_Nearest()
    {
        // Sanity: non-tie values round normally (down below .5, up above).
        Assert.Equal(1 + 33, WeaponAccuracyEvents.AccuracyByte(33.3f, 100f)); // 33.3 → 33
        Assert.Equal(1 + 67, WeaponAccuracyEvents.AccuracyByte(66.7f, 100f)); // 66.7 → 67
    }
}
