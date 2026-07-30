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
/// BUNNYHOP / JUMP-TIMING characterization. Xonotic BASE steps the LOCAL player at the real (variable) render-frame
/// dt (Movetype_Physics_NoMatchTicrate) — and so does PATH A (cl_movement_perframe 1, the default). The earlier
/// fixed-tick attempt was reverted because, while it made the hop PERIOD perfectly periodic, it introduced a
/// render LURCH (the predicted origin only advanced on tick boundaries → 0/1/2-tick jumps at fps != 72*n; see
/// <see cref="Phase2_VariableDt_RendersEveryFrame_FixedTick_Lurches_AtNon72Fps"/>). The KNOWINGLY-ACCEPTED cost of
/// variable-dt is a tiny, BOUNDED hop-period quantization (the landing tick lands within ±dt) — exactly what Base
/// ships with. These tests document that trade-off: jump HEIGHT is frametime-independent
/// (<see cref="Phase1_JumpHeight_IsFrametimeIndependent_AndFlightQuantizationBounded"/>), and the variable-dt
/// hop-period jitter, though larger than the fixed-tick path's, stays SMALL in absolute terms.
/// </summary>
public class MovementTimingTests
{
    private readonly ITestOutputHelper _out;
    public MovementTimingTests(ITestOutputHelper o) => _out = o;

    private sealed record HopStats(int Hops, float PeriodStdMs, float PeakStd, float FinalSpeed, float SpeedStd);

    [Fact]
    public void Bunnyhop_VariableTimestep_HopPeriodJitter_StaysBounded()
    {
        HopStats fixedDt = RunBhop(Fixed(1f / 72f, 6f));
        HopStats jitter = RunBhop(Jitter(6f));

        _out.WriteLine($"FIXED 1/72:  hops={fixedDt.Hops} periodStd={fixedDt.PeriodStdMs:F3}ms peakStd={fixedDt.PeakStd:F3}u " +
                       $"finalSpeed={fixedDt.FinalSpeed:F1} speedStd={fixedDt.SpeedStd:F3}");
        _out.WriteLine($"JITTERY dt:  hops={jitter.Hops} periodStd={jitter.PeriodStdMs:F3}ms peakStd={jitter.PeakStd:F3}u " +
                       $"finalSpeed={jitter.FinalSpeed:F1} speedStd={jitter.SpeedStd:F3}");

        // Fixed timestep is essentially perfectly periodic (documents the fixed-tick path's one virtue).
        Assert.True(fixedDt.PeriodStdMs < 1.0f, $"fixed-timestep bhop period is consistent (std {fixedDt.PeriodStdMs:F3}ms)");
        // PATH A's accepted trade-off: variable-dt adds hop-period jitter, but it stays SMALL in ABSOLUTE terms
        // (one-frame landing quantization) — Base accepts this; we take it in exchange for lurch-free rendering.
        Assert.True(jitter.PeriodStdMs < 8.0f,
            $"variable-timestep hop-period jitter must stay bounded (std {jitter.PeriodStdMs:F3}ms — the accepted Path A cost)");
        // And the final bhop SPEED gain must match between fixed and variable dt (fps-independent strafe physics —
        // the competitive-fairness invariant that makes variable-dt safe).
        Assert.True(MathF.Abs(jitter.FinalSpeed - fixedDt.FinalSpeed) < 30f,
            $"bhop speed gain must be ~fps-independent (fixed {fixedDt.FinalSpeed:F1} vs variable {jitter.FinalSpeed:F1})");
    }

    [Fact]
    public void Phase1_JumpHeight_IsFrametimeIndependent_AndFlightQuantizationBounded()
    {
        // PATH A viability gate: Xonotic Base steps the LOCAL player at the real (variable) render-frame dt
        // (Movetype_Physics_NoMatchTicrate), which is only safe if the physics is frametime-independent. Verify it:
        // a single jump from rest must reach the SAME apex height regardless of dt (the half-step / Verlet gravity
        // makes peak = v0^2/2g, dt-invariant), and the flight TIME (the hop-period driver) may vary only by the
        // landing-detection quantization — at most ~one dt. If apex height drifted with dt, variable-dt would be
        // unsafe and we'd have to fall back to fixed-tick + render interpolation instead.
        float[] dts = { 1f / 240f, 1f / 144f, 1f / 100f, 1f / 72f, 1f / 45f, 1f / 30f };
        var apex = new List<float>();
        var flight = new List<float>();

        foreach (float dt in dts)
        {
            var (world, clock) = FlatWorld();
            Api.Services = new MovementTestServices(world, clock);
            XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
            XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
            XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

            var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
            var s = new PredictedState { Origin = new Vector3(0f, 0f, 24f), Velocity = Vector3.Zero, OnGround = true };
            float now = 0f;
            for (int i = 0; i < 20; i++) { now += dt; clock.Time = now; var c = Idle(dt); step.Step(ref s, in c, default); }

            float groundZ = s.Origin.Z;
            // one jump (no horizontal move, so it's a clean vertical arc)
            now += dt; clock.Time = now;
            var jump = new InputCommand { ViewAngles = Vector3.Zero, Buttons = (int)InputButtons.Jump, DeltaTime = dt };
            step.Step(ref s, in jump, default);
            float launchTime = now;
            float peak = s.Origin.Z;

            // release jump and fall/idle until grounded again (single hop)
            int guard = 0;
            while (!s.OnGround && guard++ < 100000)
            {
                now += dt; clock.Time = now; var c = Idle(dt); step.Step(ref s, in c, default);
                peak = MathF.Max(peak, s.Origin.Z);
            }
            apex.Add(peak - groundZ);
            flight.Add(now - launchTime);
        }

        float apexSpread = Max(apex) - Min(apex);
        float flightSpread = Max(flight) - Min(flight);
        _out.WriteLine($"apex height across dt 1/240..1/30: {Min(apex):F2}..{Max(apex):F2}u (spread {apexSpread:F3}u); " +
                       $"flight time spread {flightSpread * 1000f:F1}ms (max dt {Max(dts) * 1000f:F1}ms)");

        // Apex height must be essentially dt-invariant (frametime-independent physics → variable-dt is SAFE).
        Assert.True(apexSpread < 1.0f, $"jump apex must be frametime-independent (spread {apexSpread:F3}u across dt)");
        // Flight time may vary only by landing-detection quantization — bounded by ~one max-dt (this is the small,
        // Base-accepted hop-period jitter we knowingly take back in exchange for killing the render lurch).
        Assert.True(flightSpread <= Max(dts) + 0.003f,
            $"flight-time variance must be bounded by ~one dt (spread {flightSpread * 1000f:F1}ms > {Max(dts) * 1000f:F1}ms)");
    }

    private static float Max(IReadOnlyList<float> xs) { float m = xs[0]; foreach (float x in xs) if (x > m) m = x; return m; }
    private static float Min(IReadOnlyList<float> xs) { float m = xs[0]; foreach (float x in xs) if (x < m) m = x; return m; }

    [Fact]
    public void Phase2_VariableDt_RendersEveryFrame_FixedTick_Lurches_AtNon72Fps()
    {
        // PATH A core verification ("verify movement first"). The rendered LOCAL origin is the predicted origin
        // (ClientNet.PredictedOrigin → Reconciler.Predicted.Origin). At a display fps that is not a multiple of 72
        // (here 100), the FIXED-tick model drains 0/1/2 ticks per frame, so on the 0-tick frames the rendered origin
        // does NOT move — that is the bhop "lurch". PATH A pushes ONE variable-dt command per render frame, so the
        // predicted origin advances EVERY frame by the real dt → smooth. This models the local prediction exactly
        // (no server needed — the camera reads this origin directly).
        const float fps = 100f, dt = 1f / fps;
        int fixedZero = CountZeroAdvanceFrames(dt, 180, variableDt: false, out float fixedStd, out float fixedMean);
        int varZero   = CountZeroAdvanceFrames(dt, 180, variableDt: true,  out float varStd,   out float varMean);
        _out.WriteLine($"@100fps  FIXED-tick: {fixedZero} zero-advance frames, per-frame X mean {fixedMean:F3} std {fixedStd:F3}; " +
                       $"PATH A var-dt: {varZero} zero-advance frames, per-frame X mean {varMean:F3} std {varStd:F3}");

        Assert.True(fixedZero > 0, $"fixed-tick must lurch at fps!=72*n (got {fixedZero} zero-advance frames)");
        Assert.Equal(0, varZero); // variable-dt advances the rendered origin every single frame → no lurch
        Assert.True(varStd < fixedStd, $"variable-dt motion must be more uniform than fixed-tick ({varStd:F3} vs {fixedStd:F3})");
    }

    // Model the rendered LOCAL origin over `frames` render frames at `dt`. On a listen server the rendered origin
    // (Reconciler.Predicted) tracks the immediately-stepped predicted state (acks are same-frame), so we step the
    // carrier directly: PATH A advances it once per render frame at the real dt; the legacy path drains fixed tics
    // (0/1/2 per frame). Returns how many frames the origin did NOT advance (the lurch) + the per-frame X std/mean.
    private int CountZeroAdvanceFrames(float dt, int frames, bool variableDt, out float perFrameStd, out float perFrameMean)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        const float tic = 1f / 72f;
        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 24f), Velocity = Vector3.Zero, OnGround = true };

        // settle AND accelerate to cruising ground speed at the sim rate, so the measurement window is steady-state
        // (no acceleration ramp that would look like a per-frame advance change).
        float now = 0f;
        for (int i = 0; i < 80; i++) { now += tic; clock.Time = now; var c = Forward(tic); step.Step(ref s, in c, default); }

        var deltas = new List<float>();
        float prevX = s.Origin.X;
        float accum = 0f;
        for (int f = 0; f < frames; f++)
        {
            now += dt; clock.Time = now;
            if (variableDt)
            {
                var c = Forward(dt); step.Step(ref s, in c, default); // PATH A: one step per render frame at real dt
            }
            else
            {
                accum += dt; // legacy: drain fixed tics (0/1/2 per frame at fps != 72*n)
                while (accum >= tic) { accum -= tic; var c = Forward(tic); step.Step(ref s, in c, default); }
            }
            float x = s.Origin.X;
            deltas.Add(x - prevX);
            prevX = x;
        }

        int zero = 0; foreach (float d in deltas) if (d < 1e-4f) zero++;
        perFrameStd = Std(deltas);
        double mean = 0; foreach (float d in deltas) mean += d; perFrameMean = (float)(mean / deltas.Count);
        return zero;
    }

    private static InputCommand Forward(float dt) => new() { ViewAngles = Vector3.Zero, Forward = 1f, DeltaTime = dt };

    [Fact]
    public void PreMatchFreeze_FrozenPredictor_StaysAtSpawn_NoReconcileError()
    {
        // The pre-match countdown rubberband fix. During the freeze the SERVER pins the player at spawn
        // (canMove=false); if the client predictor keeps walking forward (FROZEN=false), the reconcile measures a
        // steady error against the frozen-at-spawn authoritative origin every snapshot — which the smoother re-arms
        // = the spawn-countdown vibrate. The fix mirrors the freeze onto the predictor (FROZEN=true) so it runs NO
        // movement and the predicted origin stays AT spawn → reconcile error ~0. This pins both: bug reproduces with
        // Frozen=false, fix holds with Frozen=true.
        float frozenDrift = MaxPredictedDriftVsFrozenServer(frozenPredictor: true);
        float unfrozenDrift = MaxPredictedDriftVsFrozenServer(frozenPredictor: false);
        _out.WriteLine($"pre-match freeze: predictor FROZEN max drift from spawn {frozenDrift:F2}u; UNFROZEN (bug) {unfrozenDrift:F2}u");

        Assert.True(frozenDrift < 0.5f, $"a frozen predictor must stay at spawn, got {frozenDrift:F2}u");
        Assert.True(unfrozenDrift > 10f, $"an unfrozen predictor must drift forward off the frozen server (the bug), got {unfrozenDrift:F2}u");
    }

    // Model the pre-match freeze: the SERVER holds the player at spawn (never moves, canMove=false) while the client
    // holds +forward and predicts AHEAD by a latency window (the server acks `latency` ticks behind newest). Returns
    // the max distance the client's PREDICTED (rendered) origin drifts from the frozen-spawn truth — ≈0 if the
    // predictor is correctly frozen, large (it walks off) if it isn't (= the rubberband the camera shows).
    private float MaxPredictedDriftVsFrozenServer(bool frozenPredictor)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        const float tic = 1f / 72f;
        const int latency = 5; // client predicts ~5 ticks ahead of the server ack
        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f)) { Frozen = frozenPredictor };
        var buf = new PredictionBuffer();
        var rec = new Reconciler(buf, step) { ErrorCompensation = 0f, TickRate = 72f };

        var spawn = new PredictedState { Origin = new Vector3(0f, 0f, 24f), Velocity = Vector3.Zero, OnGround = true };
        float now = 0f, maxDrift = 0f;
        rec.Predict(spawn, 0, default, now); // seed Predicted = spawn

        for (int f = 0; f < 80; f++)
        {
            now += tic; clock.Time = now;
            buf.Push(new InputCommand { ViewAngles = Vector3.Zero, Forward = 1f, Buttons = (int)InputButtons.Jump, DeltaTime = tic });
            uint newest = buf.NextSeq - 1;
            uint acked = newest > latency ? newest - latency : 0u; // server stays `latency` ticks behind
            rec.Reconcile(spawn, acked, default, now, rec.Predicted); // server origin never leaves spawn (frozen)
            rec.Predict(spawn, acked, default, now);
            maxDrift = MathF.Max(maxDrift, (rec.Predicted.Origin - spawn.Origin).Length());
        }
        return maxDrift;
    }

    // Run an auto-bhop (forward + jump-when-onground) over the given per-tick dt stream; return hop consistency.
    private HopStats RunBhop(IEnumerable<float> dts)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 24f), Velocity = Vector3.Zero, OnGround = true };
        // settle onto the floor
        float now = 0f;
        for (int i = 0; i < 20; i++) { now += 1f / 72f; clock.Time = now; var c = Idle(1f / 72f); step.Step(ref s, in c, default); }

        var jumpTimes = new List<float>();
        var jumpPeaks = new List<float>();
        var jumpSpeeds = new List<float>();
        float curPeak = s.Origin.Z;

        foreach (float dt in dts)
        {
            now += dt; clock.Time = now;
            bool wasOnGround = s.OnGround;
            var cmd = new InputCommand
            {
                ViewAngles = Vector3.Zero,
                Forward = 1f,
                Buttons = wasOnGround ? (int)InputButtons.Jump : 0, // jump the instant we're grounded (auto-bhop)
                DeltaTime = dt,
            };
            step.Step(ref s, in cmd, default);

            curPeak = MathF.Max(curPeak, s.Origin.Z);
            if (wasOnGround && s.Velocity.Z > 100f) // a jump just fired
            {
                jumpTimes.Add(now);
                float horiz = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
                jumpSpeeds.Add(horiz);
                if (jumpPeaks.Count > 0 || jumpTimes.Count > 1) jumpPeaks.Add(curPeak);
                curPeak = s.Origin.Z;
            }
        }

        var periods = new List<float>();
        for (int i = 1; i < jumpTimes.Count; i++) periods.Add((jumpTimes[i] - jumpTimes[i - 1]) * 1000f); // ms
        float finalSpeed = jumpSpeeds.Count > 0 ? jumpSpeeds[^1] : 0f;
        return new HopStats(jumpTimes.Count, Std(periods), Std(jumpPeaks), finalSpeed, Std(jumpSpeeds));
    }

    private static IEnumerable<float> Fixed(float dt, float seconds)
    {
        float t = 0f;
        while (t < seconds) { yield return dt; t += dt; }
    }

    // A deterministic jittery frame-time stream (mimics a map whose fps swings 40..165): the SAME total real time
    // as the fixed run, but each tick a different dt — exactly what per-frame mode feeds the physics.
    private static IEnumerable<float> Jitter(float seconds)
    {
        float[] pattern = { 1f / 144f, 1f / 60f, 1f / 90f, 1f / 45f, 1f / 120f, 1f / 72f, 1f / 165f, 1f / 50f };
        float t = 0f; int i = 0;
        while (t < seconds) { float dt = pattern[i++ % pattern.Length]; yield return dt; t += dt; }
    }

    private static InputCommand Idle(float dt) => new() { ViewAngles = Vector3.Zero, DeltaTime = dt };

    private static float Std(IReadOnlyList<float> xs)
    {
        if (xs.Count < 2) return 0f;
        double mean = 0; foreach (float x in xs) mean += x; mean /= xs.Count;
        double v = 0; foreach (float x in xs) v += (x - mean) * (x - mean);
        return (float)Math.Sqrt(v / xs.Count);
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
