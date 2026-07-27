using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;
using XonoticGodot.Tests.Camera;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// INVESTIGATION PROBE (air-strafe turn-rate parity vs Base): quantifies the maximum velocity-redirect
/// rate achievable with PURE STRAFE input (hold A/D + sweep the mouse) in
///   (a) the port's full <see cref="PlayerPhysics"/> sim, and
///   (b) an INDEPENDENT re-transcription of Base's QC air branch (ecs/systems/physics.qc:309-371 +
///       common/physics/player.qc PM_Accelerate/AdjustAirAccelQW/GeomLerp/IsMoveInDirection), written
///       fresh from the Base source — NOT from the port — so a shared transcription bug cannot hide.
/// The "mouse" holds the wish direction at a constant lead angle from the current velocity heading each
/// tick (how a player actually leads a strafe turn); the lead is swept to find the per-config maximum.
/// </summary>
public class StrafeParityProbeTests
{
    private readonly ITestOutputHelper _out;
    public StrafeParityProbeTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 60f;
    private const float Duration = 1.0f;
    private const float StartSpeed = 400f;

    [Fact]
    public void MaxStrafeTurnRate_Port_vs_QcReference()
    {
        // Three columns: the port sim; the QC reference with GAMEPLAYFIX_Q2AIRACCELERATE OFF (the pre-fix
        // port behavior — kept as documentation of the old ~3× divergence); and with the fix ON (what LIVE
        // Base runs: stats.qh:396 autocvar default 1, xonotic-server.cfg:562, replicated to CSQC via
        // MOVEFLAG_Q2AIRACCELERATE). player.qc:288-289 applies it INSIDE PM_Accelerate, i.e. AFTER the
        // strafe GeomLerp clamp reduced wishspeed to 100 — live Base's accel step is accel*dt*100, not
        // accel*dt*360. The PORT now runs the fix ON (MovementParameters.GameplayFixQ2AirAccelerate,
        // default true) and must land on the BASE column exactly.
        _out.WriteLine($"start speed {StartSpeed}, dt {Dt * 1000f:F2} ms, window {Duration}s, pure strafe (Side=+1)");
        _out.WriteLine("lead(deg) |   port deg/s  end-speed | ref(q2aa=0=old) deg/s end-spd | ref(q2aa=1=BASE) deg/s  end-spd");

        float bestPort = 0f, bestRefOff = 0f, bestRefOn = 0f;
        float portSpeedAtBest = 0f, refOffSpeedAtBest = 0f, refOnSpeedAtBest = 0f;
        float maxPortVsBase = 0f;
        for (float lead = 40f; lead <= 170f; lead += 10f)
        {
            (float pRate, float pSpeed) = RunPort(Dt, Duration, StartSpeed, lead);
            (float rRate, float rSpeed) = RunRef(Dt, Duration, StartSpeed, lead, q2aa: false);
            (float bRate, float bSpeed) = RunRef(Dt, Duration, StartSpeed, lead, q2aa: true);
            _out.WriteLine($"  {lead,5:F0}   | {pRate,10:F1} {pSpeed,9:F1} | {rRate,14:F1} {rSpeed,9:F1} | {bRate,14:F1} {bSpeed,9:F1}");
            if (pRate > bestPort) { bestPort = pRate; portSpeedAtBest = pSpeed; }
            if (rRate > bestRefOff) { bestRefOff = rRate; refOffSpeedAtBest = rSpeed; }
            if (bRate > bestRefOn) { bestRefOn = bRate; refOnSpeedAtBest = bSpeed; }
            maxPortVsBase = MathF.Max(maxPortVsBase, MathF.Abs(pRate - bRate));
        }
        _out.WriteLine($"MAX: port {bestPort:F1} deg/s (end {portSpeedAtBest:F1})   old(q2aa=0) {bestRefOff:F1} (end {refOffSpeedAtBest:F1})   BASE {bestRefOn:F1} (end {refOnSpeedAtBest:F1})");

        // PARITY GUARD: the port must match the LIVE-Base (q2aa=ON) reference at every lead. The reference
        // is an independent transcription of the QC (not the port), so the two only agree if the port's air
        // branch — clamps, GeomLerp blends, accelqw stretch, q2aa step — is Base-faithful.
        Assert.True(maxPortVsBase < 1.0f,
            $"port must match the live-Base (q2aa=1) reference at every lead (max |delta| {maxPortVsBase:F2} deg/s)");
    }

    [Fact]
    public void BunnyhopFloor_TickDetail_PortSim()
    {
        // Per-tick trace of one bunnyhop with a HUMAN-like constant mouse sweep: is the player ever simulated
        // with OnGround=true at step entry (which selects the GROUND branch: plain accel, no strafe-speed cap,
        // no QW clamp, plus friction)? Base QC runs ZERO ground-branch ticks under held-jump autohop: the first
        // grounded tick's CheckPlayerJump re-jumps and UNSETs onground BEFORE the branch selection.
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 88.03f), Velocity = new Vector3(StartSpeed, 0f, 0f), OnGround = true };

        const float omega = 90f; // deg/s constant mouse sweep to the left
        float now = 0f;
        float prevHeading = Heading(s.Velocity);
        int groundEntryTicks = 0, ticks = 0;
        float prevVz = 0f;
        for (int i = 0; i < 180; i++) // 3 s at 60 Hz
        {
            now += Dt; clock.Time = now; ticks++;
            bool entryGround = s.OnGround;
            if (entryGround) groundEntryTicks++;
            float yawDeg = 90f + omega * now; // start aiming wish (right vector) along +X velocity, sweep left
            var c = new InputCommand
            {
                ViewAngles = new Vector3(0f, yawDeg, 0f),
                Side = 1f,
                DeltaTime = Dt,
                Buttons = (int)InputButtons.Jump,
            };
            step.Step(ref s, in c, default);
            float h = Heading(s.Velocity);
            float dHead = WrapPi(h - prevHeading) * QMathRad2Deg;
            prevHeading = h;
            if (i < 100 || entryGround)
                _out.WriteLine($"t{i,3} entryGround={(entryGround ? 1 : 0)} exitGround={(s.OnGround ? 1 : 0)} z={s.Origin.Z,7:F2} vz={s.Velocity.Z,7:F1} (prev {prevVz,7:F1}) speed={MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y),7:F1} dHead={dHead,6:F2}");
            prevVz = s.Velocity.Z;
        }
        _out.WriteLine($"ground-entry ticks: {groundEntryTicks}/{ticks}");
    }

    [Fact]
    public void BunnyhopFloor_TurnRate_PortSim()
    {
        // Full bunnyhop on the flat floor: hold jump + pure strafe, lead the mouse; measure the turn rate the
        // whole loop delivers (air ticks + landing/jump ticks). If ground-branch ticks sneak extra turning in,
        // this exceeds the air-only number above.
        foreach (float lead in new[] { 60f, 80f, 90f, 100f, 120f })
        {
            (float rate, float speed) = RunPortBunny(Dt, 3.0f, StartSpeed, lead);
            _out.WriteLine($"bunny lead {lead,5:F0}: {rate,8:F1} deg/s   end speed {speed,7:F1}");
        }
    }

    // ==================================================================================================
    //  (a) the port's real sim
    // ==================================================================================================

    private (float degPerSec, float endSpeed) RunPort(float dt, float seconds, float speed0, float leadDeg)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 6000f), Velocity = new Vector3(speed0, 0f, 0f), OnGround = false };

        float now = 0f, turned = 0f;
        float prevHeading = Heading(s.Velocity);
        while (now < seconds)
        {
            now += dt; clock.Time = now;
            float psi = Heading(s.Velocity);
            // wishdir for Side=+1 is the view's RIGHT vector: right(yaw) = (sin yaw, -cos yaw, 0); its world
            // heading is (yaw - 90). Aim the wish LEAD degrees to the LEFT (ccw, +) of the velocity heading:
            float yawDeg = psi * QMathRad2Deg + leadDeg + 90f;
            var c = new InputCommand { ViewAngles = new Vector3(0f, yawDeg, 0f), Side = 1f, DeltaTime = dt };
            step.Step(ref s, in c, default);
            float h = Heading(s.Velocity);
            turned += WrapPi(h - prevHeading);
            prevHeading = h;
        }
        return (MathF.Abs(turned) * QMathRad2Deg / seconds, MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y));
    }

    private (float degPerSec, float endSpeed) RunPortBunny(float dt, float seconds, float speed0, float leadDeg)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        // floor top is at z=64 (AnalyticWorld brush below), hull mins.z=-24 → stand at 88.
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 88.03f), Velocity = new Vector3(speed0, 0f, 0f), OnGround = true };

        float now = 0f, turned = 0f;
        float prevHeading = Heading(s.Velocity);
        while (now < seconds)
        {
            now += dt; clock.Time = now;
            float psi = Heading(s.Velocity);
            float yawDeg = (psi + leadDeg) * QMathRad2Deg + 90f;
            var c = new InputCommand
            {
                ViewAngles = new Vector3(0f, yawDeg, 0f),
                Side = 1f,
                DeltaTime = dt,
                Buttons = (int)InputButtons.Jump, // hold space: autohop
            };
            step.Step(ref s, in c, default);
            float h = Heading(s.Velocity);
            turned += WrapPi(h - prevHeading);
            prevHeading = h;
        }
        return (MathF.Abs(turned) * QMathRad2Deg / seconds, MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y));
    }

    // ==================================================================================================
    //  (b) independent QC reference — transcribed from Base qcsrc (ecs/systems/physics.qc:309-371,
    //      common/physics/player.qc:154-168 / 270-341), physicsX.cfg values, pure-strafe input.
    // ==================================================================================================

    private const float MaxAirSpeed = 360f;        // sv_maxairspeed
    private const float AirAccelerateCv = 2f;      // sv_airaccelerate
    private const float AirAccelQWCv = -0.8f;      // sv_airaccel_qw
    private const float AirStrafeAccelQWCv = -0.95f; // sv_airstrafeaccel_qw
    private const float StretchFactor = 2f;        // sv_airaccel_qw_stretchfactor
    private const float AirStopAccelerateCv = 3f;  // sv_airstopaccelerate
    private const float AirStrafeAccelerateCv = 18f; // sv_airstrafeaccelerate
    private const float MaxAirStrafeSpeedCv = 100f;  // sv_maxairstrafespeed
    private const float SpeedLimitNonQW = 900f;    // sv_airspeedlimit_nonqw
    private const float SideMove = 360f;           // sys_phys_fixspeed stuffcmd value (matches WishMoveScaling)

    private (float degPerSec, float endSpeed) RunRef(float dt, float seconds, float speed0, float leadDeg, bool q2aa = false)
    {
        float vx = speed0, vy = 0f;
        float now = 0f, turned = 0f;
        float prevHeading = MathF.Atan2(vy, vx);
        while (now < seconds)
        {
            now += dt;
            float psi = MathF.Atan2(vy, vx);
            float w = psi + leadDeg * QMathDeg2Rad;
            float wdx = MathF.Cos(w), wdy = MathF.Sin(w);

            // --- ecs/systems/physics.qc:302-357 (com_phys_air), movement = (0, ±350, 0) → strafity 1 ---
            float wishspeed = MathF.Min(SideMove, MaxAirSpeed);            // :303/:307 vlen clamp (350)
            float airaccelqw = AirAccelQWCv;                               // :312
            float wishspeed0 = wishspeed;                                  // :313
            // :315 min(maxairspd) again (no-op), not ducked
            float airaccel = AirAccelerateCv;                              // :319

            // :325-335 CPM airstop (sinusoidal; _full=0)
            float speedNow = MathF.Sqrt(vx * vx + vy * vy);
            if (AirStopAccelerateCv != 0f && speedNow > 0f)
            {
                float dot = (vx / speedNow) * wdx + (vy / speedNow) * wdy;
                if (dot < 0f)
                    airaccel += (airaccel - AirStopAccelerateCv) * dot;
            }

            const float strafity = 1f;                                     // :344 pure sideways input
            if (MaxAirStrafeSpeedCv != 0f)                                 // :345
                wishspeed = MathF.Min(wishspeed, GeomLerp(MaxAirSpeed, strafity, MaxAirStrafeSpeedCv));
            if (AirStrafeAccelerateCv != 0f)                               // :350
                airaccel = GeomLerp(airaccel, strafity, AirStrafeAccelerateCv);
            if (AirStrafeAccelQWCv != 0f)                                  // :353
                airaccelqw = ((strafity > 0.5f ? AirStrafeAccelQWCv : AirAccelQWCv) >= 0f ? +1f : -1f)
                    * (1f - GeomLerp(1f - MathF.Abs(AirAccelQWCv), strafity, 1f - MathF.Abs(AirStrafeAccelQWCv)));

            // :364-366 PM_Accelerate (sidefric = 0/360 = 0)
            PmAccelerateRef(ref vx, ref vy, dt, wdx, wdy, wishspeed, wishspeed0, airaccel, airaccelqw,
                StretchFactor, 0f, SpeedLimitNonQW, q2aa);
            // :369 CPM_PM_Aircontrol skipped: pure strafe → movity 0 → k = -1 ≤ 0 (player.qc:236-246)

            float h = MathF.Atan2(vy, vx);
            turned += WrapPi(h - prevHeading);
            prevHeading = h;
        }
        return (MathF.Abs(turned) * QMathRad2Deg / seconds, MathF.Sqrt(vx * vx + vy * vy));
    }

    /// <summary>common/physics/player.qc:280-341 PM_Accelerate, 2D (vel_z carried outside).</summary>
    private static void PmAccelerateRef(ref float vx, ref float vy, float dt, float wdx, float wdy,
        float wishspeed, float wishspeed0, float accel, float accelqw, float stretchfactor, float sidefric, float speedlimit,
        bool q2aa)
    {
        float speedclamp = stretchfactor > 0f ? stretchfactor : accelqw < 0f ? 1f : -1f;   // :282-284
        accelqw = MathF.Abs(accelqw);                                                       // :286
        // player.qc:288-289 — GAMEPLAYFIX_Q2AIRACCELERATE (LIVE Base: ON): the step uses the FINAL
        // (strafe-clamped/duck-halved) wishspeed, not the pre-clamp wishspeed0. The verify-against-dp.md §2.2
        // "benign" claim only checked the call-site equality and missed the in-between strafe clamp.
        if (q2aa)
            wishspeed0 = wishspeed;
        float vel_straight = vx * wdx + vy * wdy;                                           // :291
        float perpx = vx - vel_straight * wdx, perpy = vy - vel_straight * wdy;             // :294
        float step = accel * dt * wishspeed0;                                               // :296
        float vel_xy_current = MathF.Sqrt(vx * vx + vy * vy);                               // :298
        if (speedlimit != 0f)                                                               // :299-300
            accelqw = AdjustAirAccelQW(accelqw,
                (speedlimit - Bound(wishspeed, vel_xy_current, speedlimit)) / MathF.Max(1f, speedlimit - wishspeed));
        float vel_xy_forward = vel_xy_current + Bound(0f, wishspeed - vel_xy_current, step) * accelqw + step * (1f - accelqw);
        float vel_xy_backward = vel_xy_current - Bound(0f, wishspeed + vel_xy_current, step) * accelqw - step * (1f - accelqw);
        vel_xy_backward = MathF.Max(0f, vel_xy_backward);
        vel_straight = vel_straight + Bound(0f, wishspeed - vel_straight, step) * accelqw + step * (1f - accelqw);

        // sidefric == 0 → vel_perpend unchanged (:325 factor = 1)
        _ = sidefric; _ = vel_xy_backward;

        vx = vel_straight * wdx + perpx;                                                    // :327
        vy = vel_straight * wdy + perpy;

        if (speedclamp >= 0f)                                                               // :329-338
        {
            float pre = MathF.Sqrt(vx * vx + vy * vy);
            if (pre > 0f)
            {
                vel_xy_current += (vel_xy_forward - vel_xy_current) * speedclamp;
                if (vel_xy_current < pre)
                {
                    float f = vel_xy_current / pre;
                    vx *= f; vy *= f;
                }
            }
        }
    }

    private static float AdjustAirAccelQW(float accelqw, float factor)                       // player.qc:270-273
        => MathF.CopySign(Bound(0.000001f, 1f - (1f - MathF.Abs(accelqw)) * factor, 1f), accelqw);

    private static float GeomLerp(float a, float lerp, float b)                              // player.qc:163-168
        => a == 0f ? (lerp < 1f ? 0f : b)
         : b == 0f ? (lerp > 0f ? 0f : a)
         : a * MathF.Pow(MathF.Abs(b / a), lerp);

    private static float Bound(float lo, float v, float hi) => MathF.Min(MathF.Max(lo, v), hi);

    // ==================================================================================================

    private const float QMathRad2Deg = 180f / MathF.PI;
    private const float QMathDeg2Rad = MathF.PI / 180f;

    private static float Heading(Vector3 v) => MathF.Atan2(v.Y, v.X);

    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    private static (AnalyticWorld world, MutableClock clock) FlatWorld()
    {
        var brushes = new List<(int, float[])>
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                0, 0, 1, 0, 0, 0, -1, 64, 1, 0, 0, 8192, -1, 0, 0, 8192, 0, 1, 0, 8192, 0, -1, 0, 8192,
            }),
        };
        return (AnalyticWorld.FromPlanes(brushes), new MutableClock());
    }
}
