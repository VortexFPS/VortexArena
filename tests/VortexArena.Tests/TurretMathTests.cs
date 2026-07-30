using System;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first coverage for <see cref="TurretMath"/> (ports of QC <c>anglemods</c> /
/// <c>shortangle_f</c> / <c>shortangle_vxy</c> / <c>angleofs3</c> from lib/angle.qc, the
/// <c>steerlib_*</c> helpers from server/steerlib.qc, and <c>movelib_move_simple</c> /
/// <c>movelib_brake_simple</c> from common/physics/movelib.qc). Pure math on bare entities — no
/// engine boot needed. Angle assertions go through QMath round-trips, never hand-derived pitch signs
/// (the VecToAngles pitch convention is intentionally inverse — see the QMath pitch-convention note).
/// </summary>
public class TurretMathTests
{
    // ---------------------------------------------------------------- anglemods / shortangle

    [Fact]
    public void AngleMods_WrapsIntoHalfOpenRange()
    {
        Assert.Equal(0f, TurretMath.AngleMods(0f));
        Assert.Equal(0f, TurretMath.AngleMods(360f));
        Assert.Equal(0f, TurretMath.AngleMods(-360f));
        Assert.Equal(-170f, TurretMath.AngleMods(190f));
        Assert.Equal(170f, TurretMath.AngleMods(-190f));
        Assert.Equal(90f, TurretMath.AngleMods(450f));
        // the boundary: 180 wraps to -180 (the QC branch order maps v>=180 -> v-360)
        Assert.Equal(-180f, TurretMath.AngleMods(180f));
        Assert.Equal(-180f, TurretMath.AngleMods(540f));
    }

    [Fact]
    public void AngleMods_IsPeriodic()
    {
        foreach (float a in new[] { -719f, -45.5f, 0f, 13f, 179f, 359f, 1234.5f })
            Assert.Equal(TurretMath.AngleMods(a), TurretMath.AngleMods(a + 720f), 3);
    }

    [Fact]
    public void ShortAngle_PicksTheShortWayAround()
    {
        // direct transcription of the QC branch logic
        Assert.Equal(-170f, TurretMath.ShortAngle(190f, 10f));   // ang1 > ang2 and > 180 -> -360
        Assert.Equal(170f, TurretMath.ShortAngle(-190f, -10f));  // ang1 < ang2 and < -180 -> +360
        Assert.Equal(170f, TurretMath.ShortAngle(170f, 10f));    // within range: unchanged
        Assert.Equal(-170f, TurretMath.ShortAngle(-170f, -10f));
        Assert.Equal(0f, TurretMath.ShortAngle(0f, 0f));
    }

    [Fact]
    public void ShortAngleVxy_AppliesPerComponent_AndZeroesZ()
    {
        Vector3 r = TurretMath.ShortAngleVxy(new Vector3(190f, -190f, 77f), new Vector3(10f, -10f, 0f));
        Assert.Equal(-170f, r.X);
        Assert.Equal(170f, r.Y);
        Assert.Equal(0f, r.Z); // z is always forced to 0
    }

    // ---------------------------------------------------------------- angleofs3

    [Fact]
    public void AngleOfs_ZeroWhenAlreadyFacingTheTarget()
    {
        // round-trip-safe: derive the facing from the direction itself via QMath.VecToAngles.
        Vector3 from = new(10f, 20f, 30f);
        Vector3 to = new(110f, -40f, 55f);
        Vector3 facing = QMath.VecToAngles(QMath.Normalize(to - from));

        Vector3 ofs = TurretMath.AngleOfs(from, facing, to);
        Assert.Equal(0f, ofs.X, 3);
        Assert.Equal(0f, ofs.Y, 3);
        Assert.Equal(0f, ofs.Z, 3);
    }

    [Fact]
    public void AngleOfs_YawOffset_MatchesTheTurnBetweenHeadings()
    {
        // facing +X, target along +Y: the yaw offset magnitude is 90 degrees.
        Vector3 from = Vector3.Zero;
        Vector3 facing = QMath.VecToAngles(new Vector3(1f, 0f, 0f));
        Vector3 ofs = TurretMath.AngleOfs(from, facing, new Vector3(0f, 100f, 0f));
        Assert.Equal(90f, MathF.Abs(ofs.Y), 3);
        Assert.Equal(0f, ofs.X, 3);
    }

    [Fact]
    public void AngleOfs_ComponentsAreWrappedToHalfTurn()
    {
        // wherever the target is, each component must come back wrapped into (-180, 180]
        Vector3 from = Vector3.Zero;
        foreach (Vector3 to in new[]
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0.5f, -0.7f, 0.3f), new Vector3(-3, -4, 5),
        })
        {
            Vector3 ofs = TurretMath.AngleOfs(from, new Vector3(10f, 350f, 0f), to * 100f);
            Assert.InRange(ofs.X, -180f, 180f);
            Assert.InRange(ofs.Y, -180f, 180f);
        }
    }

    // ---------------------------------------------------------------- steering

    [Fact]
    public void SteerPull_IsAUnitVectorTowardThePoint()
    {
        var e = new Entity { Origin = new Vector3(0f, 0f, 0f) };
        Vector3 pull = TurretMath.SteerPull(e, new Vector3(100f, 0f, 0f));
        Assert.Equal(new Vector3(1f, 0f, 0f), pull);
        Assert.Equal(1f, pull.Length(), 5);
    }

    [Fact]
    public void SteerArrive_EasesInWithDistance()
    {
        var e = new Entity { Origin = Vector3.Zero };

        // at (or beyond) maxDist the pull is full strength 1
        Assert.Equal(1f, TurretMath.SteerArrive(e, new Vector3(500f, 0f, 0f), 500f).Length(), 4);
        Assert.Equal(1f, TurretMath.SteerArrive(e, new Vector3(900f, 0f, 0f), 500f).Length(), 4);
        // at half the distance the pull is half strength
        Assert.Equal(0.5f, TurretMath.SteerArrive(e, new Vector3(250f, 0f, 0f), 500f).Length(), 4);
        // direction is always toward the point
        Vector3 v = TurretMath.SteerArrive(e, new Vector3(0f, 250f, 0f), 500f);
        Assert.True(v.Y > 0f && MathF.Abs(v.X) < 1e-4f);
    }

    [Fact]
    public void SteerAttract2_InfluenceRisesAsItGetsCloser()
    {
        var e = new Entity { Origin = Vector3.Zero };
        // infl = minInfl + (1 - dist/maxDist) * (maxInfl - minInfl)
        float far = TurretMath.SteerAttract2(e, new Vector3(400f, 0f, 0f), 0.2f, 500f, 1f).Length();
        float near = TurretMath.SteerAttract2(e, new Vector3(100f, 0f, 0f), 0.2f, 500f, 1f).Length();
        Assert.True(near > far, $"closer should pull harder ({near} vs {far})");
        Assert.Equal(0.2f + 0.2f * 0.8f, far, 3);   // dist 400/500 -> infl 0.36
        Assert.Equal(0.2f + 0.8f * 0.8f, near, 3);  // dist 100/500 -> infl 0.84
    }

    // ---------------------------------------------------------------- movement

    [Fact]
    public void MoveSimple_BlendsVelocityTowardTarget()
    {
        var e = new Entity { Velocity = new Vector3(100f, 0f, 0f) };
        TurretMath.MoveSimple(e, new Vector3(0f, 1f, 0f), 200f, 0.25f);
        // v = old*(1-w) + dir*speed*w = (75, 50, 0)
        Assert.Equal(75f, e.Velocity.X, 3);
        Assert.Equal(50f, e.Velocity.Y, 3);
        Assert.Equal(0f, e.Velocity.Z, 3);

        // inertia weight 1 jumps straight to the target velocity
        var e2 = new Entity { Velocity = new Vector3(100f, 0f, 0f) };
        TurretMath.MoveSimple(e2, new Vector3(0f, 1f, 0f), 200f, 1f);
        Assert.Equal(new Vector3(0f, 200f, 0f), e2.Velocity);
    }

    [Fact]
    public void BrakeSimple_BleedsSpeed_PreservesVertical_NeverReverses()
    {
        var e = new Entity { Velocity = new Vector3(3f, 4f, 0f) };   // speed 5
        TurretMath.BrakeSimple(e, 2f);
        Assert.Equal(1.8f, e.Velocity.X, 3);                          // 3/5 * 3
        Assert.Equal(2.4f, e.Velocity.Y, 3);
        Assert.Equal(0f, e.Velocity.Z, 3);

        // braking harder than the speed stops, never reverses (max(0, ...))
        var e2 = new Entity { Velocity = new Vector3(1f, 0f, 0f) };
        TurretMath.BrakeSimple(e2, 50f);
        Assert.Equal(0f, e2.Velocity.X, 4);
        Assert.Equal(0f, e2.Velocity.Y, 4);

        // the vertical component is restored after the brake (gravity unaffected)
        var e3 = new Entity { Velocity = new Vector3(30f, 0f, -100f) };
        TurretMath.BrakeSimple(e3, 10f);
        Assert.Equal(-100f, e3.Velocity.Z, 3);
    }
}
