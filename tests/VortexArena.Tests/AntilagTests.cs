using System.Numerics;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the lag-compensation (antilag) history ported from <c>server/antilag.qc</c>:
/// <see cref="AntilagBuffer"/>'s ring storage + bracketing-pair lerp (<c>antilag_takebackorigin</c>) and
/// <see cref="LagCompensation.ComputeTakebackTime"/>'s 0.4s rewind cap (<c>ANTILAG_LATENCY</c>).
/// </summary>
public class AntilagTests
{
    // Per-component vector compare with tolerance — the 3-arg Assert.Equal is float-only, and the lerp
    // results aren't bit-exact.
    private static void AssertVecEqual(Vector3 expected, Vector3 actual, int precision = 3)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    [Fact]
    public void SampleAt_Returns_Exact_Sample_On_Direct_Hit()
    {
        var buf = new AntilagBuffer();
        buf.Store(0f, new Vector3(0, 0, 0));
        buf.Store(1f, new Vector3(100, 0, 0));
        buf.Store(2f, new Vector3(100, 50, 0));

        Assert.True(buf.HasData);
        // Sampling exactly at a recorded time returns that sample's origin (clamps + bracket endpoints).
        AssertVecEqual(new Vector3(0, 0, 0), buf.SampleAt(0f));
        AssertVecEqual(new Vector3(100, 0, 0), buf.SampleAt(1f));
        AssertVecEqual(new Vector3(100, 50, 0), buf.SampleAt(2f));
    }

    [Fact]
    public void SampleAt_Interpolates_Between_Bracketing_Samples_At_Midpoint()
    {
        var buf = new AntilagBuffer();
        buf.Store(0f, new Vector3(0, 0, 0));
        buf.Store(1f, new Vector3(100, 0, 0));

        // QC lerpv at the midpoint: halfway between (0,0,0) and (100,0,0) is (50,0,0).
        AssertVecEqual(new Vector3(50, 0, 0), buf.SampleAt(0.5f));
        // And a quarter / three-quarter of the way to confirm it is linear, not just the midpoint.
        AssertVecEqual(new Vector3(25, 0, 0), buf.SampleAt(0.25f));
        AssertVecEqual(new Vector3(75, 0, 0), buf.SampleAt(0.75f));
    }

    [Fact]
    public void SampleAt_Interpolates_Across_Multiple_Axes()
    {
        var buf = new AntilagBuffer();
        buf.Store(10f, new Vector3(0, 0, 0));
        buf.Store(12f, new Vector3(20, -40, 8));

        // Midpoint of a multi-axis move (t=11 of [10,12]).
        AssertVecEqual(new Vector3(10, -20, 4), buf.SampleAt(11f));
    }

    [Fact]
    public void SampleAt_Clamps_Time_Newer_Than_Newest_To_Newest()
    {
        var buf = new AntilagBuffer();
        buf.Store(0f, new Vector3(0, 0, 0));
        buf.Store(1f, new Vector3(100, 0, 0));

        // QC "IN THE PRESENT": a time past the newest sample returns the newest origin (no extrapolation).
        AssertVecEqual(new Vector3(100, 0, 0), buf.SampleAt(1.5f));
        AssertVecEqual(new Vector3(100, 0, 0), buf.SampleAt(999f));
    }

    [Fact]
    public void SampleAt_Clamps_Time_Older_Than_Oldest_To_Oldest()
    {
        var buf = new AntilagBuffer();
        buf.Store(5f, new Vector3(7, 0, 0));
        buf.Store(6f, new Vector3(100, 0, 0));

        // A time before the oldest retained sample clamps to that oldest origin.
        AssertVecEqual(new Vector3(7, 0, 0), buf.SampleAt(4.9f));
        AssertVecEqual(new Vector3(7, 0, 0), buf.SampleAt(-100f));
    }

    [Fact]
    public void SampleAt_Empty_Buffer_Returns_Zero()
    {
        var buf = new AntilagBuffer();
        Assert.False(buf.HasData);
        AssertVecEqual(Vector3.Zero, buf.SampleAt(0f));
        AssertVecEqual(Vector3.Zero, buf.SampleAt(123f));
    }

    [Fact]
    public void Store_Drops_Stale_Or_Duplicate_Timestamps()
    {
        var buf = new AntilagBuffer();
        buf.Store(1f, new Vector3(10, 0, 0));
        buf.Store(1f, new Vector3(999, 0, 0)); // duplicate stamp — dropped (QC monotonic guard)
        buf.Store(0.5f, new Vector3(888, 0, 0)); // older stamp — dropped

        Assert.Equal(1, buf.Count);
        AssertVecEqual(new Vector3(10, 0, 0), buf.SampleAt(1f));
    }

    [Fact]
    public void Ring_Overflows_Dropping_Oldest_But_Recent_Samples_Still_Sampled()
    {
        var buf = new AntilagBuffer();
        // Store more than the ring capacity; each sample's origin.X mirrors its time so we can check exactly.
        const int n = AntilagBuffer.Capacity + 50; // 114 stores into a 64-slot ring
        for (int i = 0; i < n; i++)
            buf.Store(i, new Vector3(i, 0, 0));

        // Count pins at capacity once full.
        Assert.Equal(AntilagBuffer.Capacity, buf.Count);

        // The newest sample is intact.
        AssertVecEqual(new Vector3(n - 1, 0, 0), buf.SampleAt(n - 1));

        // A recent in-range time still interpolates correctly across the wrap (origin.X == time).
        AssertVecEqual(new Vector3(n - 5.5f, 0, 0), buf.SampleAt(n - 5.5f));
        AssertVecEqual(new Vector3(n - 20, 0, 0), buf.SampleAt(n - 20));

        // The oldest retained sample is time = n - Capacity; anything older clamps to it (dropped history).
        float oldestTime = n - AntilagBuffer.Capacity;
        AssertVecEqual(new Vector3(oldestTime, 0, 0), buf.SampleAt(oldestTime));
        AssertVecEqual(new Vector3(oldestTime, 0, 0), buf.SampleAt(oldestTime - 30f)); // older than oldest → clamp
    }

    [Fact]
    public void Clear_Resets_To_Empty()
    {
        var buf = new AntilagBuffer();
        buf.Store(0f, new Vector3(1, 2, 3));
        buf.Store(1f, new Vector3(4, 5, 6));
        Assert.True(buf.HasData);

        buf.Clear();
        Assert.False(buf.HasData);
        Assert.Equal(0, buf.Count);
        AssertVecEqual(Vector3.Zero, buf.SampleAt(0.5f));

        // Usable again after clearing.
        buf.Store(10f, new Vector3(7, 7, 7));
        AssertVecEqual(new Vector3(7, 7, 7), buf.SampleAt(10f));
    }

    [Fact]
    public void ComputeTakebackTime_Rewinds_By_Ping_Plus_Interpolation_Delay()
    {
        // ping 0.25 + interp 0.05 = 0.30, under the 0.4 cap → rewind serverTime by 0.30.
        float t = LagCompensation.ComputeTakebackTime(serverTime: 10f, clientPing: 0.25f, interpolationDelay: 0.05f);
        Assert.Equal(9.70f, t, 3);
    }

    [Fact]
    public void ComputeTakebackTime_Honors_The_0_4_Second_Cap()
    {
        // ping 1.0 (+ any interp) far exceeds the cap → clamp the rewind to MaxDelay (0.4).
        float t = LagCompensation.ComputeTakebackTime(serverTime: 10f, clientPing: 1.0f, interpolationDelay: 0.05f);
        Assert.Equal(9.60f, t, 3);
        Assert.Equal(0.4f, LagCompensation.MaxDelay, 3);
    }

    [Fact]
    public void ComputeTakebackTime_Clamps_Negative_Latency_To_Zero()
    {
        // Clock skew could make ping+interp negative; bound(0, ...) means no rewind (sample the present).
        float t = LagCompensation.ComputeTakebackTime(serverTime: 5f, clientPing: -0.5f, interpolationDelay: 0f);
        Assert.Equal(5f, t, 3);
    }

    [Fact]
    public void Takeback_Roundtrip_Samples_The_Shooter_View_Position()
    {
        // End-to-end shape of an antilag shot: record a target's path, then rewind by the shooter's lag and
        // sample where the shooter saw it. Target moves +100 X/sec; shooter has 0.1s ping (no interp nudge).
        var target = new AntilagBuffer();
        for (int i = 0; i <= 20; i++)            // 0.0s .. 2.0s at 0.1s steps
            target.Store(i * 0.1f, new Vector3(i * 10f, 0, 0));

        float serverTime = 2.0f;                  // "now"
        float takeback = LagCompensation.ComputeTakebackTime(serverTime, clientPing: 0.1f, interpolationDelay: 0f);
        Assert.Equal(1.9f, takeback, 3);

        // At t=1.9s the target was at X = 190 (it reaches X=200 at t=2.0). The shot rewinds to there.
        AssertVecEqual(new Vector3(190, 0, 0), target.SampleAt(takeback));
    }
}
