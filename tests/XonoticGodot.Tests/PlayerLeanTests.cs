using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Playermodel lean (<see cref="PlayerLean"/>) — the port of the never-merged Base branch
/// <c>mirceakitsune/lean_players</c>. Pins the ported algorithm AND the five fixes over the 2011 original:
/// (1) the operator-precedence bug that leaned at constant VELOCITY instead of on acceleration,
/// (2) the broken <c>g_leanplayer_acceleration 0</c> off-switch,
/// (3) tick-rate-dependent averaging/decay (now exponential),
/// (4) the output-fed-back-as-input accumulation that made dead players spin (the offset is stateless now —
///     stepping any fixed state twice must give the identical offset, and dead players get identity), and
/// (5) the direction-blind accel lean: the original's <c>Invert('0 y 0')∘'p y 0'</c> is algebraically a pure
///     local pitch (the yaw cancels), so decelerating tipped the body BACK too; the port conjugates by the
///     accel-vs-body yaw so deceleration tips forward and strafe acceleration banks sideways.
/// </summary>
public class PlayerLeanTests
{
    /// <summary>The original branch's defaultXonotic.cfg values (damage lean ON for the damage tests;
    /// the port's SHIPPED default for g_leanplayer_damage is 0 = disabled).</summary>
    private static PlayerLean.Config OriginalDefaults(float damage = 0.015f)
        => new(acceleration: 0.05f, accelerationFade: 5f, accelerationMax: 10f,
               damage: damage, damageFade: 6.3f, damageMax: 0.5f);

    private const float Dt = 1f / 64f;

    // ---- fix #1: precedence — lean answers ACCELERATION, not velocity ------------------------------------

    [Fact]
    public void ConstantVelocity_ProducesNoLean()
    {
        // The 2011 code computed accel = velocity - avg*cvar ≈ velocity, so a player RUNNING at constant
        // speed sat pegged at the lean bound forever. Fixed: once the average converges the offset is zero.
        var cfg = OriginalDefaults(damage: 0f);
        var vel = new Vector3(400f, 0f, 0f);
        Vector3 avg = vel; // converged tracker (constant running for a while)

        PlayerLean.Result r = PlayerLean.Step(vel, 0f, avg, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);

        Assert.Equal(Vector3.Zero, r.Offset);
    }

    [Fact]
    public void LongConstantRun_ConvergesToExactlyZero()
    {
        // Feed the state back tick-over-tick like the server does: from rest to a 400u/s run, the lean must
        // spike and then settle to EXACT zero (the epsilon snap keeps the networked field quiet at cruise).
        var cfg = OriginalDefaults(damage: 0f);
        var vel = new Vector3(400f, 0f, 0f);
        Vector3 avg = Vector3.Zero;
        Vector3 offset = default;
        bool sawLean = false;

        for (int i = 0; i < 64 * 5; i++) // 5 simulated seconds
        {
            PlayerLean.Result r = PlayerLean.Step(vel, 0f, avg, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);
            avg = r.AvgVel;
            offset = r.Offset;
            if (offset != Vector3.Zero) sawLean = true;
        }

        Assert.True(sawLean, "accelerating from rest should lean at least transiently");
        Assert.Equal(Vector3.Zero, offset);
        Assert.Equal(vel.X, avg.X, 1);
    }

    [Fact]
    public void ForwardAcceleration_LeansBack_PurePitch()
    {
        // "The player shall lean back while accelerating forward": +X (yaw 0) acceleration → a pure
        // NEGATIVE (up) makevectors pitch, no yaw/roll component.
        var cfg = OriginalDefaults(damage: 0f);
        var vel = new Vector3(400f, 0f, 0f);

        PlayerLean.Result r = PlayerLean.Step(vel, 0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);

        Assert.True(r.Offset.X < 0f, $"forward accel should pitch back (negative), got {r.Offset}");
        Assert.Equal(0f, r.Offset.Y, 2);
        Assert.Equal(0f, r.Offset.Z, 2);
    }

    [Fact]
    public void Deceleration_LeansForward()
    {
        // Stopping from a 400u/s run: the velocity-minus-average flips backward → the lean flips forward.
        var cfg = OriginalDefaults(damage: 0f);
        Vector3 avg = new(400f, 0f, 0f); // was cruising
        Vector3 vel = Vector3.Zero;      // slammed to a stop

        PlayerLean.Result r = PlayerLean.Step(vel, 0f, avg, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);

        Assert.True(r.Offset.X > 0f, $"deceleration should pitch forward (positive), got {r.Offset}");
    }

    [Fact]
    public void StrafeAcceleration_BanksSideways_OppositeTheAcceleration()
    {
        // Fix #5: accelerating toward +Y (Quake left) while facing +X must BANK the body — a roll, not the
        // original's face-relative pitch — and "lean opposite their acceleration direction" (issue #440)
        // means the body's up-vector tips AWAY from +Y.
        var cfg = OriginalDefaults(damage: 0f);
        var vel = new Vector3(0f, 400f, 0f);

        PlayerLean.Result r = PlayerLean.Step(vel, 0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);

        Assert.True(System.MathF.Abs(r.Offset.Z) > 1f, $"strafe accel should roll, got {r.Offset}");
        Assert.Equal(0f, r.Offset.X, 2); // no pitch component for a pure side accel
        QMath.AngleVectors(r.Offset, out _, out _, out Vector3 up);
        Assert.True(up.Y < 0f, $"lean should tip opposite the +Y acceleration (up.Y < 0), got up {up}");
    }

    [Fact]
    public void AccelLean_IsWorldAnchored_NotFaceRelative()
    {
        // The same world-space forward acceleration must produce the same WORLD-space tip no matter which
        // way the body faces: facing +Y while accelerating +X, the body-frame offset becomes a bank (roll),
        // and composing base ∘ offset reproduces the yaw-0 world tilt exactly.
        var cfg = OriginalDefaults(damage: 0f);
        var vel = new Vector3(400f, 0f, 0f);

        PlayerLean.Result facing0 = PlayerLean.Step(vel, 0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);
        PlayerLean.Result facing90 = PlayerLean.Step(vel, 90f, Vector3.Zero, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);

        // Compose each offset onto its body base and compare the resulting WORLD up-vectors.
        Vector3 composed0 = QMath.AnglesTransformMultiply(new Vector3(0f, 0f, 0f), facing0.Offset);
        Vector3 composed90 = QMath.AnglesTransformMultiply(new Vector3(0f, 90f, 0f), facing90.Offset);
        QMath.AngleVectors(composed0, out _, out _, out Vector3 up0);
        QMath.AngleVectors(composed90, out _, out _, out Vector3 up90);

        Assert.Equal(up0.X, up90.X, 3);
        Assert.Equal(up0.Y, up90.Y, 3);
        Assert.Equal(up0.Z, up90.Z, 3);
    }

    [Fact]
    public void Lean_IsBounded_ByAccelerationMax()
    {
        // A teleport-grade velocity difference must clamp at the per-component bound (max lean ≈ √3·max).
        var cfg = OriginalDefaults(damage: 0f);
        var vel = new Vector3(100000f, 100000f, 100000f);

        PlayerLean.Result r = PlayerLean.Step(vel, 0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);

        float maxLen = cfg.AccelerationMax * System.MathF.Sqrt(3f) + 0.1f;
        Assert.True(System.MathF.Abs(r.Offset.X) <= maxLen, $"pitch {r.Offset.X} exceeds bound {maxLen}");
    }

    // ---- fix #2: the off-switch actually switches off ----------------------------------------------------

    [Fact]
    public void AccelerationCvarZero_DisablesAccelLean_EvenWhileMoving()
    {
        // The 2011 gate only wrapped the AVERAGING, so acceleration 0 still leaned (accel = vel − 0·avg).
        var cfg = new PlayerLean.Config(0f, 5f, 10f, 0f, 6.3f, 0.5f);
        var vel = new Vector3(800f, 300f, 0f);

        PlayerLean.Result r = PlayerLean.Step(vel, 0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, dead: false, cfg, Dt);

        Assert.Equal(Vector3.Zero, r.Offset);
        // The tracker follows the live velocity while off, so a later enable starts from rest.
        Assert.Equal(vel, r.AvgVel);
    }

    [Fact]
    public void DamageLean_DisabledByDefault_ClearsStoredForce()
    {
        // The port SHIPS g_leanplayer_damage 0: no damage lean, and any leftover stored force drops at once.
        var cfg = OriginalDefaults(damage: 0f);
        var staleForce = new Vector3(0.4f, 0.1f, 0f);

        PlayerLean.Result r = PlayerLean.Step(Vector3.Zero, 0f, Vector3.Zero, new Vector3(0f, 0f, 24f), staleForce,
            dead: false, cfg, Dt);

        Assert.Equal(Vector3.Zero, r.Offset);
        Assert.Equal(Vector3.Zero, r.DmgForce);
    }

    [Fact]
    public void AccumulateDamage_NoOp_WhenDisabled()
    {
        var e = new Entity { Origin = new Vector3(10f, 20f, 30f) };

        PlayerLean.AccumulateDamage(e, hitLoc: new Vector3(10f, 20f, 60f), force: new Vector3(300f, 0f, 0f),
            damageScale: 0f, damageMax: 0.5f);

        Assert.Equal(Vector3.Zero, e.LeanDmgForce);
        Assert.Equal(Vector3.Zero, e.LeanDmgLoc);
    }

    // ---- damage lean (opt-in) ------------------------------------------------------------------------------

    [Fact]
    public void AccumulateDamage_StoresLeverArm_AndBoundedForce()
    {
        var e = new Entity { Origin = new Vector3(10f, 20f, 30f) };

        // A blaster-class knockback (≈300u force): scaled by 0.015 → 4.5, bounded to ±0.5 per component.
        PlayerLean.AccumulateDamage(e, hitLoc: new Vector3(10f, 20f, 60f), force: new Vector3(300f, -300f, 0f),
            damageScale: 0.015f, damageMax: 0.5f);

        Assert.Equal(new Vector3(0f, 0f, 30f), e.LeanDmgLoc); // hitloc − origin (QC lever arm)
        Assert.Equal(0.5f, e.LeanDmgForce.X);   // 4.5 clamped to the max ("don't get stuck leaning")
        Assert.Equal(-0.5f, e.LeanDmgForce.Y);
        Assert.Equal(0f, e.LeanDmgForce.Z);
    }

    [Fact]
    public void DamageLean_ProducesOffset_ThenDecaysToExactZero()
    {
        var cfg = OriginalDefaults(); // damage 0.015 (the original's default) → enabled
        Vector3 loc = new(0f, 0f, 24f);           // hit above the origin
        Vector3 force = new(0.4f, 0f, 0f);        // stored, pre-scaled force
        Vector3 offset = default;
        bool sawLean = false;

        for (int i = 0; i < 64 * 3; i++) // 3 simulated seconds at rate 6.3/s → e^-18.9 ≈ 6e-9, deep past the snap
        {
            PlayerLean.Result r = PlayerLean.Step(Vector3.Zero, 0f, Vector3.Zero, loc, force, dead: false, cfg, Dt);
            force = r.DmgForce;
            offset = r.Offset;
            if (offset != Vector3.Zero) sawLean = true;
        }

        Assert.True(sawLean, "an accumulated damage force should lean the model");
        Assert.Equal(Vector3.Zero, force);   // epsilon-snapped to rest
        Assert.Equal(Vector3.Zero, offset);  // and the offset with it
    }

    // ---- fix #3: framerate independence --------------------------------------------------------------------

    [Fact]
    public void DamageDecay_IsTickRateIndependent()
    {
        // One simulated second of decay at 64 Hz vs 128 Hz must land on the same force (the original's
        // force *= 0.9 PER FRAME halved ~6× faster at 128 Hz than at 64 Hz).
        var cfg = OriginalDefaults();
        Vector3 loc = new(0f, 0f, 24f);

        Vector3 f64 = new(0.4f, 0.2f, 0f);
        for (int i = 0; i < 64; i++)
            f64 = PlayerLean.Step(Vector3.Zero, 0f, Vector3.Zero, loc, f64, dead: false, cfg, 1f / 64f).DmgForce;

        Vector3 f128 = new(0.4f, 0.2f, 0f);
        for (int i = 0; i < 128; i++)
            f128 = PlayerLean.Step(Vector3.Zero, 0f, Vector3.Zero, loc, f128, dead: false, cfg, 1f / 128f).DmgForce;

        Assert.Equal(f64.X, f128.X, 4);
        Assert.Equal(f64.Y, f128.Y, 4);
    }

    [Fact]
    public void VelocityAveraging_IsTickRateIndependent()
    {
        var cfg = OriginalDefaults(damage: 0f);
        var vel = new Vector3(400f, 0f, 0f);

        Vector3 a64 = Vector3.Zero;
        for (int i = 0; i < 64; i++)
            a64 = PlayerLean.Step(vel, 0f, a64, Vector3.Zero, Vector3.Zero, dead: false, cfg, 1f / 64f).AvgVel;

        Vector3 a128 = Vector3.Zero;
        for (int i = 0; i < 128; i++)
            a128 = PlayerLean.Step(vel, 0f, a128, Vector3.Zero, Vector3.Zero, dead: false, cfg, 1f / 128f).AvgVel;

        Assert.Equal(a64.X, a128.X, 1);
    }

    // ---- fix #4: statelessness — no accumulation, no dead spin ---------------------------------------------

    [Fact]
    public void SameState_SteppedTwice_GivesIdenticalOffset()
    {
        // The 2011 code multiplied the lean INTO self.angles each frame — output fed back as input — which
        // compounded on any player whose angles weren't engine-reset (the dead-spin bug). The port's offset
        // is a pure function of (velocity, avg, damage) state: same input → same offset, never a product.
        var cfg = OriginalDefaults();
        var vel = new Vector3(250f, 100f, 0f);
        var avg = new Vector3(100f, 0f, 0f);
        var loc = new Vector3(0f, 0f, 24f);
        var force = new Vector3(0.3f, -0.2f, 0.1f);

        PlayerLean.Result r1 = PlayerLean.Step(vel, 0f, avg, loc, force, dead: false, cfg, Dt);
        PlayerLean.Result r2 = PlayerLean.Step(vel, 0f, avg, loc, force, dead: false, cfg, Dt);

        Assert.Equal(r1.Offset, r2.Offset);
        Assert.NotEqual(Vector3.Zero, r1.Offset);
    }

    [Fact]
    public void DeadPlayer_GetsIdentity_AndClearedState()
    {
        // QC final commits: "Don't lean dead players, which causes them to spin all over the place."
        var cfg = OriginalDefaults();
        var vel = new Vector3(500f, 0f, 0f); // corpse still sliding

        PlayerLean.Result r = PlayerLean.Step(vel, 0f, Vector3.Zero, new Vector3(0f, 0f, 24f),
            new Vector3(0.5f, 0f, 0f), dead: true, cfg, Dt);

        Assert.Equal(Vector3.Zero, r.Offset);
        Assert.Equal(Vector3.Zero, r.DmgForce);
        Assert.Equal(vel, r.AvgVel); // tracker follows the body so a revive doesn't read a fake accel spike
    }

    // ---- the wire (EntityField.Lean, bit 25 — appended after VehicleView) ----------------------------------

    [Fact]
    public void Lean_RoundTrips_Through_The_Codec()
    {
        // The lean block is the FINAL append in WriteDelta/ReadDelta — a round-trip pins both the bit and
        // the wire order. 8i angle quantization = 360/256 ≈ 1.406°/step → within ~0.71° per component.
        var baseline = NetEntityState.Empty(9);
        NetEntityState current = baseline;
        current.Lean = new Vector3(-8.4f, 3.2f, 5.6f);

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        Assert.True(mask.HasFlag(EntityField.Lean));

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.True(System.MathF.Abs(QMath.AnglesTransformNormalize(got.Lean - current.Lean).X) <= 0.71f);
        Assert.True(System.MathF.Abs(QMath.AnglesTransformNormalize(got.Lean - current.Lean).Y) <= 0.71f);
        Assert.True(System.MathF.Abs(QMath.AnglesTransformNormalize(got.Lean - current.Lean).Z) <= 0.71f);
    }

    [Fact]
    public void ZeroLean_Stays_Off_The_Wire()
    {
        // A player at rest / with lean disabled must not pay for the field (the delta bit stays clear).
        var baseline = NetEntityState.Empty(9);
        NetEntityState same = baseline;
        same.Origin = new Vector3(64f, 0f, 0f); // only the origin moved

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, same);
        Assert.False(mask.HasFlag(EntityField.Lean));
    }

    // ---- the new QMath primitive ----------------------------------------------------------------------------

    [Theory]
    [InlineData(30f, 0f, 0f)]
    [InlineData(0f, 120f, 0f)]
    [InlineData(0f, 0f, 45f)]
    [InlineData(-20f, 75f, 10f)]
    public void AnglesTransformInvert_ComposesToIdentity(float pitch, float yaw, float roll)
    {
        // QC AnglesTransform_Invert: Multiply(Invert(t), t) is the identity transform. Verify on the basis
        // (identity forward/up), which sidesteps Euler multi-representation.
        var t = new Vector3(pitch, yaw, roll);
        Vector3 id = QMath.AnglesTransformMultiply(QMath.AnglesTransformInvert(t), t);
        QMath.AngleVectors(id, out Vector3 fwd, out _, out Vector3 up);

        Assert.Equal(1f, fwd.X, 4);
        Assert.Equal(0f, fwd.Y, 4);
        Assert.Equal(0f, fwd.Z, 4);
        Assert.Equal(0f, up.X, 4);
        Assert.Equal(0f, up.Y, 4);
        Assert.Equal(1f, up.Z, 4);
    }
}
