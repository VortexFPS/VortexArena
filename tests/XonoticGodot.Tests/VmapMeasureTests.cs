using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers <see cref="VmapMeasure"/> (phase E8) — the measure tool, and specifically the reachability verdict
/// that a desktop editor cannot produce.
///
/// The verdict is a ballistic approximation over the game's real <c>sv_jumpvelocity</c> and <c>sv_gravity</c>,
/// so the tests below check it against numbers a mapper would recognise: Xonotic's shipped jump clears a
/// little over 42 units of rise, which is why 64-unit ledges need a crouch-jump and 32-unit ones do not.
/// </summary>
public class VmapMeasureTests
{
    private static readonly ReachParams P = ReachParams.Default;

    // ---------------------------------------------------------------- distance and angle

    [Fact]
    public void DistanceIsStraightLine()
        => Assert.Equal(5f, VmapMeasure.Distance(Vector3.Zero, new Vector3(3, 4, 0)), 4);

    [Fact]
    public void HorizontalDistanceIgnoresHeight()
        => Assert.Equal(3f, VmapMeasure.HorizontalDistance(Vector3.Zero, new Vector3(3, 0, 999)), 4);

    [Fact]
    public void RiseIsSignedAndFromTheFirstPoint()
    {
        Assert.Equal(64f, VmapMeasure.Rise(Vector3.Zero, new Vector3(0, 0, 64)), 4);
        Assert.Equal(-64f, VmapMeasure.Rise(new Vector3(0, 0, 64), Vector3.Zero), 4);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 1, 0, 90)]
    [InlineData(1, 0, 0, 1, 0, 0, 0)]
    [InlineData(1, 0, 0, -1, 0, 0, 180)]
    [InlineData(1, 0, 0, 1, 1, 0, 45)]
    public void AngleAtAVertex(float ax, float ay, float az, float bx, float by, float bz, float expected)
        => Assert.Equal(expected,
            VmapMeasure.Angle(Vector3.Zero, new Vector3(ax, ay, az), new Vector3(bx, by, bz)), 3);

    [Fact]
    public void AngleWithADegenerateArmIsZero()
        => Assert.Equal(0f, VmapMeasure.Angle(Vector3.Zero, Vector3.Zero, new Vector3(1, 0, 0)), 4);

    // ---------------------------------------------------------------- the jump arc

    /// <summary>
    /// v^2/2g with the shipped numbers: 260^2 / 1600, a little over 42 units. This is the constant that
    /// decides every ledge height in the game, so it is worth pinning explicitly.
    /// </summary>
    [Fact]
    public void JumpHeightMatchesTheShippedNumbers()
        => Assert.Equal(42.25f, VmapMeasure.JumpHeight(P), 2);

    [Fact]
    public void ZeroGravityDoesNotDivideByZero()
        => Assert.Equal(0f, VmapMeasure.JumpHeight(P with { Gravity = 0f }), 4);

    [Fact]
    public void AFlatJumpReachesFurtherThanAnUphillOne()
    {
        float flat = VmapMeasure.JumpReach(P, P.MaxSpeed, 0f);
        float uphill = VmapMeasure.JumpReach(P, P.MaxSpeed, 32f);
        Assert.True(flat > uphill, $"flat={flat} uphill={uphill}");
    }

    [Fact]
    public void ADropReachesFurtherThanFlat()
        => Assert.True(VmapMeasure.JumpReach(P, P.MaxSpeed, -128f) > VmapMeasure.JumpReach(P, P.MaxSpeed, 0f));

    /// <summary>Asking for a rise above the apex has no solution, and the honest answer is zero reach.</summary>
    [Fact]
    public void ARiseAboveTheApexIsUnreachable()
        => Assert.Equal(0f, VmapMeasure.JumpReach(P, P.MaxSpeed, 500f), 4);

    [Fact]
    public void FasterMeansFurther()
        => Assert.True(VmapMeasure.JumpReach(P, P.BhopSpeed, 0f) > VmapMeasure.JumpReach(P, P.MaxSpeed, 0f));

    // ---------------------------------------------------------------- verdicts

    /// <summary>
    /// "Walk" means the destination is directly adjacent and low enough to be stepped onto, NOT merely that it
    /// is close. The verdict assumes no floor between the two points, so distance alone can never make
    /// something walkable.
    /// </summary>
    [Fact]
    public void AnAdjacentStepUpIsAWalk()
        => Assert.Equal(ReachVerdict.Walk, VmapMeasure.Reach(Vector3.Zero, new Vector3(24, 0, 24), P));

    [Fact]
    public void ALowLedgeBeyondArmsReachIsStillAJump()
        => Assert.Equal(ReachVerdict.Jump, VmapMeasure.Reach(Vector3.Zero, new Vector3(96, 0, 24), P));

    [Fact]
    public void AWideFlatGapNeedsAJump()
        => Assert.Equal(ReachVerdict.Jump, VmapMeasure.Reach(Vector3.Zero, new Vector3(200, 0, 0), P));

    /// <summary>
    /// A flat jump at sv_maxspeed covers about 234 units (0.65s of flight at 360ups), so a 256-unit gap is
    /// past it. That number is the one a mapper is really asking about when they measure a ledge.
    /// </summary>
    [Fact]
    public void AGapJustBeyondAPlainJumpNeedsMoreSpeed()
    {
        Assert.Equal(234f, VmapMeasure.JumpReach(P, P.MaxSpeed, 0f), 0);
        Assert.Equal(ReachVerdict.Bhop, VmapMeasure.Reach(Vector3.Zero, new Vector3(256, 0, 0), P));
    }

    /// <summary>
    /// The verdict a mapper actually reaches for. A 64-unit ledge is above the 42-unit jump apex, so it needs
    /// the extra clearance a crouch-jump buys; a 32-unit one does not.
    /// </summary>
    [Fact]
    public void ALedgeAboveTheJumpApexNeedsACrouchJump()
    {
        Assert.Equal(ReachVerdict.Jump, VmapMeasure.Reach(Vector3.Zero, new Vector3(96, 0, 32), P));
        Assert.Equal(ReachVerdict.CrouchJump, VmapMeasure.Reach(Vector3.Zero, new Vector3(96, 0, 56), P));
    }

    [Fact]
    public void AGapTooWideForRunningSpeedNeedsBhop()
        => Assert.Equal(ReachVerdict.Bhop, VmapMeasure.Reach(Vector3.Zero, new Vector3(500, 0, 0), P));

    [Fact]
    public void AnImpossibleGapSaysSo()
    {
        Assert.Equal(ReachVerdict.Unreachable, VmapMeasure.Reach(Vector3.Zero, new Vector3(4096, 0, 0), P));
        Assert.Equal(ReachVerdict.Unreachable, VmapMeasure.Reach(Vector3.Zero, new Vector3(64, 0, 512), P));
    }

    /// <summary>The verdict must follow the physics, not be hardcoded: raise the jump and the answer changes.</summary>
    [Fact]
    public void TheVerdictFollowsTheMovementParameters()
    {
        var high = new Vector3(96, 0, 56);
        Assert.Equal(ReachVerdict.CrouchJump, VmapMeasure.Reach(Vector3.Zero, high, P));

        // A mod with a stronger jump turns the same ledge into an ordinary one.
        Assert.Equal(ReachVerdict.Jump, VmapMeasure.Reach(Vector3.Zero, high, P with { JumpVelocity = 400f }));
    }

    [Fact]
    public void LowGravityMakesMoreReachable()
    {
        var far = new Vector3(700, 0, 0);
        Assert.Equal(ReachVerdict.Unreachable, VmapMeasure.Reach(Vector3.Zero, far, P));
        Assert.Equal(ReachVerdict.Jump, VmapMeasure.Reach(Vector3.Zero, far, P with { Gravity = 200f }));
    }

    // ---------------------------------------------------------------- readout

    [Fact]
    public void DescribeCarriesBothTheNumbersAndTheVerdict()
    {
        string s = VmapMeasure.Describe(Vector3.Zero, new Vector3(200, 0, 0), P);
        Assert.Contains("200", s);
        Assert.Contains("run", s);
        Assert.Contains("rise", s);
        Assert.Contains("jump", s);
    }

    [Fact]
    public void EveryVerdictHasALabel()
    {
        foreach (ReachVerdict v in System.Enum.GetValues<ReachVerdict>())
            Assert.False(string.IsNullOrWhiteSpace(VmapMeasure.VerdictLabel(v)));
    }
}
