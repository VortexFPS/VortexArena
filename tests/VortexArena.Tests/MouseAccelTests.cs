using XonoticGodot.Common.Input;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins <see cref="MouseAccel"/> to DP's cl_input.c:550-662 (the m_accelerate block + m_filter). The key
/// invariant: at DP registration defaults every branch is a mathematical no-op — raw deltas pass through
/// bit-identical — so stock aim matches Base exactly. The accel curves are then checked at the boundary
/// values of the linear slope and through the filter's cross-frame state.
/// </summary>
public class MouseAccelTests
{
    private const float Dt = 1f / 144f; // a typical realframetime
    private const float Sens = 3f;      // stock `sensitivity`

    // ---------- defaults are a bit-exact passthrough ----------

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(5f, -3f)]
    [InlineData(-120.5f, 42.25f)]
    public void Defaults_PassThroughExactly(float dx, float dy)
    {
        var accel = new MouseAccel();
        var p = MouseAccelParams.DpDefaults;
        // several frames — state must not bleed into the output at defaults
        for (int i = 0; i < 5; i++)
        {
            (float x, float y) = accel.Apply(dx, dy, Dt, in p, Sens);
            Assert.Equal(dx, x);
            Assert.Equal(dy, y);
        }
    }

    [Fact]
    public void AccelerateZero_DisablesWholeBlock_PassThrough()
    {
        // DP gates on m_accelerate > 0: zero skips linear AND power AND natural.
        var accel = new MouseAccel();
        var p = MouseAccelParams.DpDefaults;
        p.Accelerate = 0f;
        p.PowerStrength = 5f;   // would scale if the block ran
        p.NaturalStrength = 5f;
        p.NaturalAccelSensCap = 3f;
        (float x, float y) = accel.Apply(10f, 4f, Dt, in p, Sens);
        Assert.Equal(10f, x);
        Assert.Equal(4f, y);
    }

    // ---------- linear slope (the menu's "Acceleration factor") ----------

    [Fact]
    public void Linear_BelowMinSpeed_NoScale_AboveMaxSpeed_FullScale()
    {
        var p = MouseAccelParams.DpDefaults;
        p.Accelerate = 2f;

        // slow: 10 px over 1s = 10 px/s << minspeed 5000 → factor 1
        var slow = new MouseAccel();
        (float sx, _) = slow.Apply(10f, 0f, 1f, in p, Sens);
        Assert.Equal(10f, sx, 3);

        // fast: 200 px in 1/144s ≈ 28800 px/s >> maxspeed 10000 → factor = m_accelerate = 2
        var fast = new MouseAccel();
        (float fx, _) = fast.Apply(200f, 0f, Dt, in p, Sens);
        Assert.Equal(400f, fx, 3);
    }

    [Fact]
    public void Linear_MidSlope_InterpolatesFactor()
    {
        // averagespeed exactly halfway between mi and ma → factor = (accel+1)/2.
        var p = MouseAccelParams.DpDefaults;
        p.Accelerate = 3f;
        p.AccelerateMinSpeed = 1000f;
        p.AccelerateMaxSpeed = 2000f;
        var accel = new MouseAccel();
        // 1500 px/s: 1500 px over 1 s
        (float x, _) = accel.Apply(1500f, 0f, 1f, in p, Sens);
        // f = (1500-1000)/(2000-1000) * (3-1) + 1 = 2
        Assert.Equal(3000f, x, 2);
    }

    // ---------- averagespeed lowpass (m_accelerate_filter) ----------

    [Fact]
    public void AccelerateFilter_SmoothsAverageSpeed_AcrossFrames()
    {
        // With filter = 2×dt, each frame blends half the instantaneous speed: frame 1 averagespeed = speed/2.
        var p = MouseAccelParams.DpDefaults;
        p.Accelerate = 2f;
        p.AccelerateMinSpeed = 1000f;
        p.AccelerateMaxSpeed = 2000f;
        p.AccelerateFilter = 2f;
        var accel = new MouseAccel();
        // 3000 px/s instantaneous (3000 px over 1 s), filter f = bound(0, 1/2, 1) = 0.5 → averagespeed 1500
        // → slope factor (1500-1000)/(2000-1000)·(2-1)+1 = 1.5
        (float x, _) = accel.Apply(3000f, 0f, 1f, in p, Sens);
        Assert.Equal(4500f, x, 2);
    }

    // ---------- m_filter (two-frame average, "Smooth aiming") ----------

    [Fact]
    public void MFilter_AveragesWithPreviousFrame_AndDrainsOnZeroInput()
    {
        var p = MouseAccelParams.DpDefaults;
        p.MFilter = true;
        var accel = new MouseAccel();

        (float x1, float y1) = accel.Apply(10f, 6f, Dt, in p, Sens);
        Assert.Equal(5f, x1); // (10 + 0)/2
        Assert.Equal(3f, y1);

        (float x2, _) = accel.Apply(20f, 0f, Dt, in p, Sens);
        Assert.Equal(15f, x2); // (20 + 10)/2

        // zero-input frame still drains the tail: (0 + 20)/2 — DP runs the block every frame
        (float x3, _) = accel.Apply(0f, 0f, Dt, in p, Sens);
        Assert.Equal(10f, x3);

        (float x4, _) = accel.Apply(0f, 0f, Dt, in p, Sens);
        Assert.Equal(0f, x4); // fully drained
    }

    // ---------- power acceleration (console-only, QL-style) ----------

    [Fact]
    public void Power_BelowOffset_NoScale()
    {
        var p = MouseAccelParams.DpDefaults;
        p.PowerStrength = 1f;
        p.PowerOffset = 100f; // px/ms — far above any speed here → adjusted ≤ 0 → accelsens stays 1
        var accel = new MouseAccel();
        (float x, _) = accel.Apply(10f, 0f, Dt, in p, Sens);
        Assert.Equal(10f, x, 3);
    }

    [Fact]
    public void Power_SensCap_Limits()
    {
        // Huge speed with an aggressive power curve, capped: accelsens = senscap / sensitivity.
        var p = MouseAccelParams.DpDefaults;
        p.PowerStrength = 10f;
        p.Power = 3f;
        p.PowerSensCap = 6f;
        var accel = new MouseAccel();
        (float x, _) = accel.Apply(500f, 0f, Dt, in p, Sens); // 72000 px/s → deep into the curve
        Assert.Equal(500f * (6f / Sens), x, 1); // capped at senscap/sens = 2×
    }

    // ---------- natural acceleration approaches its cap ----------

    [Fact]
    public void Natural_ApproachesSensCap_AtHighSpeed()
    {
        var p = MouseAccelParams.DpDefaults;
        p.NaturalStrength = 100f; // steep curve → effectively at the asymptote
        p.NaturalAccelSensCap = 2.5f;
        var accel = new MouseAccel();
        (float x, _) = accel.Apply(500f, 0f, Dt, in p, Sens);
        Assert.Equal(500f * 2.5f, x, 0); // accelsens → accelsenscap
    }

    [Fact]
    public void Reset_ClearsFilterTailAndAverageSpeed()
    {
        var p = MouseAccelParams.DpDefaults;
        p.MFilter = true;
        var accel = new MouseAccel();
        accel.Apply(100f, 100f, Dt, in p, Sens);
        accel.Reset();
        (float x, float y) = accel.Apply(0f, 0f, Dt, in p, Sens);
        Assert.Equal(0f, x); // no half-delta ghost from before the reset
        Assert.Equal(0f, y);
    }
}
