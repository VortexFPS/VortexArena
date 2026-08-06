using System;
using System.Numerics;
using VortexArena.Engine.Rendering;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The r_motionblur smear arithmetic. These exist because the first version of the feature was wrong in a way
/// nothing else would have caught: the smear was real, correctly signed, and roughly a tenth of what it needed
/// to be, so the setting looked broken rather than subtle. A screenshot cannot catch that (the pass hides
/// itself whenever the view is still) and the compiler certainly cannot.
/// </summary>
public class MotionBlurMathTests
{
    /// <summary>A view forward vector for a given yaw in degrees (level pitch).</summary>
    private static Vector3 Forward(float yawDeg)
    {
        float r = yawDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(r), 0f, MathF.Cos(r));
    }

    private static Vector2 TurnOffset(float degPerSecond, float fps, float strength)
    {
        float dt = 1f / fps;
        return MotionBlurMath.Offset(
            Forward(0f), Forward(degPerSecond * dt), Vector3.UnitX, Vector3.UnitY,
            Vector3.Zero, dt, strength);
    }

    [Fact]
    public void Standing_Still_Produces_No_Smear()
    {
        Vector2 o = MotionBlurMath.Offset(Forward(0f), Forward(0f), Vector3.UnitX, Vector3.UnitY,
            Vector3.Zero, 1f / 250f, 0.4f);
        Assert.True(o.Length() < MotionBlurMath.MinOffset, $"expected no smear, got {o.Length()}");
    }

    [Fact]
    public void Zero_Strength_Produces_No_Smear()
        => Assert.Equal(Vector2.Zero, TurnOffset(300f, 250f, 0f));

    /// <summary>
    /// The load-bearing one. A brisk turn — 300°/s, roughly a deliberate look-around rather than a flick — at
    /// the menu's recommended 0.4 must produce a smear a player can actually SEE. The old sensitivity put this
    /// at ~1.7% of screen width (about ±9 px at 1080p spread over the taps), which is why it read as doing
    /// nothing. The bound below is what "visible" was chosen to mean.
    /// </summary>
    [Fact]
    public void Brisk_Turn_Produces_A_Visible_Smear()
    {
        float pct = TurnOffset(300f, 250f, 0.4f).Length() * 100f;
        Assert.True(pct > 3f, $"a 300 deg/s turn at r_motionblur 0.4 smears only {pct:0.00}% of screen width");
        Assert.True(pct <= MotionBlurMath.MaxOffset * 100f + 0.001f, $"{pct:0.00}% exceeds the cap");
    }

    /// <summary>
    /// The same turn RATE must smear the same amount at any framerate. Without the reference-time
    /// normalisation the effect would fade out as the machine got faster, so the cvar would mean something
    /// different on every PC — the kind of bug that gets reported as "it works on my friend's machine".
    /// </summary>
    [Theory]
    [InlineData(60f)]
    [InlineData(144f)]
    [InlineData(250f)]
    [InlineData(500f)]
    public void Smear_Is_Framerate_Independent(float fps)
    {
        float atReference = TurnOffset(200f, 60f, 0.4f).Length();
        float here = TurnOffset(200f, fps, 0.4f).Length();
        Assert.True(MathF.Abs(here - atReference) < 0.002f,
            $"at {fps} fps the same 200 deg/s turn smears {here:0.0000} vs {atReference:0.0000} at 60 fps");
    }

    [Fact]
    public void Strength_Scales_The_Smear()
    {
        float low = TurnOffset(100f, 250f, 0.2f).Length();
        float high = TurnOffset(100f, 250f, 0.8f).Length();
        Assert.True(high > low * 3.5f, $"0.8 should smear ~4x as far as 0.2; got {high:0.0000} vs {low:0.0000}");
    }

    /// <summary>A teleport (or a respawn) must not streak the entire screen.</summary>
    [Fact]
    public void A_Huge_Jump_Clamps_To_The_Cap()
    {
        Vector2 o = MotionBlurMath.Offset(Forward(0f), Forward(170f), Vector3.UnitX, Vector3.UnitY,
            new Vector3(4000f, 0f, 0f), 1f / 250f, 1f);
        Assert.True(o.Length() <= MotionBlurMath.MaxOffset + 1e-5f, $"clamp failed: {o.Length()}");
    }

    /// <summary>Turning the other way must smear the other way — a sign error here would drag the image
    /// against the motion, which reads as the view lagging rather than as blur.</summary>
    [Fact]
    public void Turn_Direction_Flips_The_Smear()
    {
        float right = TurnOffset(200f, 250f, 0.4f).X;
        float left = TurnOffset(-200f, 250f, 0.4f).X;
        Assert.True(right * left < 0f, $"expected opposite signs, got {right:0.0000} and {left:0.0000}");
    }

    /// <summary>Strafing smears too: DP blurs on translation, not only rotation.</summary>
    [Fact]
    public void Strafing_Smears_Even_Without_Turning()
    {
        Vector2 o = MotionBlurMath.Offset(Forward(0f), Forward(0f), Vector3.UnitX, Vector3.UnitY,
            new Vector3(12f, 0f, 0f), 1f / 250f, 0.4f);
        Assert.True(o.Length() > MotionBlurMath.MinOffset, $"a strafe produced no smear ({o.Length()})");
    }
}
