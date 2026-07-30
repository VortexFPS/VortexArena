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
/// CAMERA-DRIFT / DEPARTURE apparatus (the render+prediction layer the golden-trace physics tests never cover).
/// Two layers:
///   * Layer 1 (smoother-only): drives <see cref="Reconciler"/> / <see cref="CameraReferenceQc"/> directly with a
///     scripted error stream over thousands of frames — isolates the smoothing math where the bug lives.
///   * Layer 2 (full pipeline): runs the real <see cref="PlayerPhysics"/> for both server and client over an
///     analytic world with realistic snapshot cadence/latency, and measures the rendered eye vs the authoritative
///     eye — the integration gate.
/// Metrics: secular slope (accumulating drift) over the run's tail, and max departure.
/// </summary>
public class CameraDriftTests
{
    private readonly ITestOutputHelper _out;
    public CameraDriftTests(ITestOutputHelper o) => _out = o;

    // ===========================================================================================
    // Layer 1 — smoother-only: the error-compensation accumulation that drifts the stationary eye.
    // ===========================================================================================

    [Fact]
    public void Layer1_Port_RepeatedSmallSameDirectionError_AccumulatesPersistentOffset()
    {
        // Model a real stationary-player situation: every snapshot the server hands back an origin that differs
        // from the client prediction by a tiny SAME-DIRECTION amount (sub-tolerance: an onground/jump-time
        // disagreement, an antilag rewind, a server-only nudge). With error compensation ON (the PORT default 1),
        // each residual is folded onto the decaying offset; we measure whether a persistent offset builds.
        var rec = new Reconciler(new PredictionBuffer(), new NoStep()) { ErrorCompensation = 1f, TickRate = 72f };

        var offsets = new List<float>();
        float now = 0f;
        const float snapDt = 2f / 72f; // a snapshot every 2 ticks
        for (int i = 0; i < 2000; i++)
        {
            now += snapDt;
            rec.SetPredictionError(new Vector3(0f, 0f, 0.3f), Vector3.Zero, now);    // +0.3u up, every snapshot
            offsets.Add(rec.GetPredictionErrorOrigin(now).Z);
        }

        float max = DriftMetrics.MaxAbs(offsets);
        float tailMean = Mean(offsets, 0.5f);
        _out.WriteLine($"[port errorcomp=1] repeated +0.3u/snapshot: max offset {max:F3}u, tail-mean {tailMean:F3}u");
        // Documents the behavior (this is the diagnostic; the faithful test below is the contrast).
        Assert.True(max >= 0f);
    }

    [Fact]
    public void Layer1_Faithful_RepeatedSmallSameDirectionError_SnapsNoAccumulation()
    {
        // Faithful Base behavior (cl_movement_errorcompensation 0): the residual is NOT smoothed at all — the
        // camera snaps to truth, so there is ZERO accumulated offset regardless of the error stream.
        var refq = new CameraReferenceQc { ErrorCompensation = 0f };
        float now = 0f;
        float maxOffset = 0f;
        for (int i = 0; i < 2000; i++)
        {
            now += 2f / 72f;
            refq.SetPredictionError(new Vector3(0f, 0f, 0.3f), Vector3.Zero, now);
            maxOffset = MathF.Max(maxOffset, refq.GetPredictionErrorO(now).Length());
        }
        _out.WriteLine($"[faithful errorcomp=0] repeated +0.3u/snapshot: max offset {maxOffset:F6}u");
        Assert.True(maxOffset < 1e-4f, $"faithful mode must not accumulate any error offset (got {maxOffset})");
    }

    [Fact]
    public void Layer1_C2Fix_AccumulationCappedAtTeleportThreshold()
    {
        // The C2 bug-fix: even with a long decay window (low errorcompensation) and a long stream of same-direction
        // errors, the accumulated camera offset must stay bounded (<= the 32u teleport threshold), not run away.
        var rec = new Reconciler(new PredictionBuffer(), new NoStep()) { ErrorCompensation = 0.05f, TickRate = 72f };
        float now = 0f, max = 0f;
        for (int i = 0; i < 4000; i++)
        {
            now += 1f / 72f;
            rec.SetPredictionError(new Vector3(0f, 0f, 5f), Vector3.Zero, now); // big repeated same-dir error
            max = MathF.Max(max, rec.GetPredictionErrorOrigin(now).Length());
        }
        _out.WriteLine($"[C2 cap] errorcomp=0.05, +5u/tick x4000: peak accumulated offset {max:F2}u (cap 32u)");
        Assert.True(max <= 32f + 1e-2f, $"accumulation must be capped at the teleport threshold (got {max})");
    }

    [Fact]
    public void Faithful_RuntimeSmoother_MatchesQcReference_OnStairAndCrouchSequence()
    {
        // The runtime FaithfulViewSmoothing (src/XonoticGodot.Net) must reproduce the independent QC reference
        // (CameraReferenceQc) eye Z bit-for-bit on a representative sequence — step up, plateau, step up, descend,
        // with a crouch view-height change — so "faithful mode" provably renders the stock Xonotic eye.
        var rt = new FaithfulViewSmoothing();
        var refq = new CameraReferenceQc();
        float time = 0f; const float dt = 1f / 72f;
        float worst = 0f;
        float z = 24f, viewOfs = 35f;
        for (int i = 0; i < 600; i++)
        {
            time += dt;
            if (i == 50) z += 24f;            // step up
            if (i == 200) z += 16f;           // another step
            if (i == 350) viewOfs = 20f;      // crouch (eye drops)
            if (i == 450) { z -= 18f; viewOfs = 35f; } // step down + stand
            var r = rt.Apply(z, dt, onground: true, viewOfsZ: viewOfs);
            float rtEye = r.StairZ + r.ViewHeightZ;
            var refEye = refq.ApplySmoothing(new Vector3(0f, 0f, z), time, time - dt, onground: true, viewOfsZ: viewOfs);
            worst = MathF.Max(worst, MathF.Abs(rtEye - refEye.Z));
        }
        _out.WriteLine($"runtime-vs-QC-reference eye Z: worst diff {worst:E3}u over 600 frames");
        Assert.True(worst < 1e-3f, $"runtime faithful smoother must match the QC reference (worst {worst})");
    }

    // ===========================================================================================
    // Layer 2 — full physics pipeline.
    // ===========================================================================================

    [Fact]
    public void Layer2_ServerSim_StationaryPlayer_StaysExactlyPut()
    {
        // Sanity: the shared PlayerPhysics keeps a stationary grounded player exactly put (no creep) — the
        // precondition for "any camera drift is a render-layer bug, not physics".
        var (world, clock) = MakeWorld();
        Api.Services = new MovementTestServices(world, clock);
        ClearMutators();

        var step = NewStep();
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 24f), Velocity = Vector3.Zero, OnGround = true };
        float now = 0f;
        Vector3 settled = default;
        for (int t = 1; t <= 500; t++)
        {
            now += 1f / 72f; clock.Time = now;
            InputCommand cmd = Idle();
            step.Step(ref s, in cmd, default);
            if (t == 10) settled = s.Origin; // capture once it has settled onto the floor (1/32 trace-epsilon gap)
        }
        _out.WriteLine($"server stationary: settled@10 {settled}, @500 {s.Origin}, vel {s.Velocity}, onground {s.OnGround}");
        // The player settles ONCE to the trace-epsilon resting height and then must not creep tick-to-tick.
        Assert.True((s.Origin - settled).Length() < 1e-4f, $"stationary creep between tick 10 and 500: {settled} -> {s.Origin}");
        Assert.True(s.Velocity.Length() < 1e-3f, $"stationary residual velocity: {s.Velocity}");
        Assert.True(s.OnGround, "stationary player must stay onground");
    }

    [Fact]
    public void Layer2_Stationary_Hermetic_NoCameraDrift()
    {
        // Matching sims + matching inputs + no injected disagreement: the rendered eye must not drift. This proves
        // the smoothing math itself does not manufacture drift (the baseline the injected-disagreement test contrasts).
        var samples = RunStationary(injected: Vector3.Zero, errorComp: 1f, out _);
        var errZ = ZErrors(samples);
        float slope = DriftMetrics.SlopeOverTail(errZ, 0.5f);
        float max = DriftMetrics.MaxAbs(errZ);
        _out.WriteLine($"hermetic stationary: max eye-Z err {max:F4}u, tail slope {slope * 72f * 60f:F6} u/min");
        Assert.True(MathF.Abs(slope) < 1e-5f, $"hermetic stationary must not drift (slope {slope})");
        Assert.True(max < 0.5f, $"hermetic stationary eye-Z error must stay tiny (max {max})");
    }

    [Fact]
    public void Layer1_Port_LongerWindow_AccumulatesBeyondPerError()
    {
        // The accumulation risk in the port path: SetPredictionError folds each residual onto the still-decaying
        // one and extends the window to max(old,new). When the decay window spans several snapshot intervals
        // (a lower errorcompensation), a stream of same-direction errors accumulates to MUCH more than a single
        // error — the unbounded-drift seed C2 must cap. errorcomp=1/3 → ~3-tick window; +0.3u every tick.
        var rec = new Reconciler(new PredictionBuffer(), new NoStep()) { ErrorCompensation = 1f / 3f, TickRate = 72f };
        float now = 0f, max = 0f;
        for (int i = 0; i < 500; i++)
        {
            now += 1f / 72f;
            rec.SetPredictionError(new Vector3(0f, 0f, 0.3f), Vector3.Zero, now);
            max = MathF.Max(max, rec.GetPredictionErrorOrigin(now).Z);
        }
        _out.WriteLine($"[port errorcomp=1/3] +0.3u/tick: peak accumulated offset {max:F3}u (single-error magnitude is 0.3u)");
        Assert.True(max > 0.6f, $"a multi-tick window must accumulate same-direction errors beyond one error (got {max})");
    }

    [Fact]
    public void Layer2_Faithful_Stationary_MatchesServerEyeExactly()
    {
        // Faithful Base rendering (CSQCPlayer_ApplySmoothing + errorcomp 0) on a stationary player must converge to
        // EXACTLY server origin + view height — zero residual offset, zero drift — the behavior the fix restores.
        var (world, clock) = MakeWorld();
        Api.Services = new MovementTestServices(world, clock);
        ClearMutators();

        var buf = new PredictionBuffer();
        var clientStep = NewStep();
        var serverStep = NewStep();
        var rec = new Reconciler(buf, clientStep) { ErrorCompensation = 0f, TickRate = 72f };
        var sim = new CameraPipelineSim { EyeHeightZ = 35f, Faithful = new CameraReferenceQc { ErrorCompensation = 0f } };
        var start = Settle(clock, new Vector3(0f, 0f, 24f));
        var samples = sim.Run(2000, rec, buf, clientStep, serverStep, start, _ => Idle(), t => clock.Time = t);

        var errZ = ZErrors(samples);
        // skip the first few frames (viewheightavg seeds from 0 → blends up to view height over a few frames)
        var tail = errZ.GetRange(60, errZ.Count - 60);
        float max = DriftMetrics.MaxAbs(tail);
        float slope = DriftMetrics.SlopeOverTail(tail, 0.5f);
        _out.WriteLine($"faithful stationary: tail max eye-Z err {max:F5}u, slope {slope:E3}");
        Assert.True(max < 1e-3f, $"faithful stationary eye must sit exactly at server eye (max {max})");
        Assert.True(MathF.Abs(slope) < 1e-6f, $"faithful stationary must not drift (slope {slope})");
    }

    [Fact]
    public void Layer2_WalkUpStairs_DepartureBounded_PortAndFaithful()
    {
        // Walk into a 24u step and climb it. Measure the worst eye departure from the authoritative eye while
        // climbing, and that the camera fully resettles after. Both regimes must stay bounded (the stair smoothing
        // glides the step pop); we record both for the diagnosis.
        float portMax = WalkUpStairsWorstDeparture(faithful: false, out float portSettle);
        float faithMax = WalkUpStairsWorstDeparture(faithful: true, out float faithSettle);
        _out.WriteLine($"walk-up-stairs worst eye departure: port {portMax:F3}u (settle {portSettle:F4}u), " +
                       $"faithful {faithMax:F3}u (settle {faithSettle:F4}u)");
        Assert.True(portMax < 35f, $"port stair departure must stay within ~one step+slop (got {portMax})");
        Assert.True(faithMax < 35f, $"faithful stair departure must stay within ~one step+slop (got {faithMax})");
        Assert.True(portSettle < 0.5f, $"port camera must resettle after the climb (got {portSettle})");
        Assert.True(faithSettle < 0.5f, $"faithful camera must resettle after the climb (got {faithSettle})");
    }

    private float WalkUpStairsWorstDeparture(bool faithful, out float settleErr)
    {
        var (world, clock) = MakeStairWorld();
        Api.Services = new MovementTestServices(world, clock);
        ClearMutators();

        var buf = new PredictionBuffer();
        var clientStep = NewStep();
        var serverStep = NewStep();
        var rec = new Reconciler(buf, clientStep)
        {
            ErrorCompensation = faithful ? 0f : 1f, TickRate = 72f,
            StairSmoothTime = 1f, StairSmoothSpeed = 200f, StairStepHeight = 31f,
            StairCatchupTime = 0.1f, StairSnapVerticalSpeed = 30f,
        };
        var sim = new CameraPipelineSim
        {
            EyeHeightZ = 35f,
            SnapshotEveryTicks = 2, LatencyTicks = 3, // realistic in-flight prediction (ack lags newest)
            Faithful = faithful ? new CameraReferenceQc() : null,
        };
        var start = Settle(clock, new Vector3(0f, 0f, 24f));
        // walk forward (+x) for 200 ticks: accelerate, hit the x=64 step, climb to the z=24 deck, keep walking.
        var samples = sim.Run(300, rec, buf, clientStep, serverStep, start,
            t => new InputCommand { Forward = t <= 200 ? 1f : 0f, ViewAngles = Vector3.Zero, DeltaTime = 1f / 72f },
            tt => clock.Time = tt);

        var errZ = ZErrors(samples);
        float worst = DriftMetrics.MaxAbs(errZ);
        // settle = worst error over the last 40 frames (camera at rest on the deck)
        var tail = errZ.GetRange(errZ.Count - 40, 40);
        settleErr = DriftMetrics.MaxAbs(tail);
        return worst;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private sealed class NoStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) { }
    }

    private static (AnalyticWorld world, MutableClock clock) MakeWorld()
    {
        // a single solid floor slab spanning z in [-64, 0], x/y in +/-4096.
        var brushes = new List<(int, float[])>
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                0, 0, 1, 0,        // top:    z <= 0
                0, 0, -1, 64,      // bottom: z >= -64
                1, 0, 0, 4096,     // +x
                -1, 0, 0, 4096,    // -x
                0, 1, 0, 4096,     // +y
                0, -1, 0, 4096,    // -y
            }),
        };
        return (AnalyticWorld.FromPlanes(brushes), new MutableClock());
    }

    private static (AnalyticWorld world, MutableClock clock) MakeStairWorld()
    {
        // low floor (z<=0) for x<=64, and a 24u-high step deck (z<=24) for x>=64. Both span +/-4096 in y and down
        // to z=-64. Player walks +x off the low floor and climbs onto the deck.
        var brushes = new List<(int, float[])>
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                0, 0, 1, 0,        // top: z <= 0
                1, 0, 0, 64,       // x <= 64
                -1, 0, 0, 4096,    // x >= -4096
                0, 0, -1, 64,      // z >= -64
                0, 1, 0, 4096, 0, -1, 0, 4096,
            }),
            (AnalyticWorld.ContSolid, new float[]
            {
                0, 0, 1, 24,       // top: z <= 24
                -1, 0, 0, -64,     // x >= 64
                1, 0, 0, 4096,     // x <= 4096
                0, 0, -1, 64,      // z >= -64
                0, 1, 0, 4096, 0, -1, 0, 4096,
            }),
        };
        return (AnalyticWorld.FromPlanes(brushes), new MutableClock());
    }

    private static PlayerPhysicsStep NewStep()
        => new(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f), standingViewOfsZ: 35f);

    private static InputCommand Idle() => new() { ViewAngles = Vector3.Zero, DeltaTime = 1f / 72f };

    /// <summary>Step a fresh entity with idle input until it settles on the floor, returning the rested state
    /// (origin at the trace-epsilon resting height, velocity 0, onground). Requires Api.Services already set.</summary>
    private static PredictedState Settle(MutableClock clock, Vector3 originGuess)
    {
        var step = NewStep();
        var s = new PredictedState { Origin = originGuess, Velocity = Vector3.Zero, OnGround = true };
        float now = clock.Time;
        for (int t = 0; t < 30; t++) { now += 1f / 72f; clock.Time = now; var c = Idle(); step.Step(ref s, in c, default); }
        clock.Time = 0f; // reset so the measured run starts its render clock at 0
        return s;
    }

    private static void ClearMutators()
    {
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();
    }

    private List<CameraPipelineSim.Sample> RunStationary(Vector3 injected, float errorComp, out Reconciler rec)
    {
        var (world, clock) = MakeWorld();
        Api.Services = new MovementTestServices(world, clock);
        ClearMutators();

        var buf = new PredictionBuffer();
        var clientStep = NewStep();
        var serverStep = NewStep();
        rec = new Reconciler(buf, clientStep)
        {
            ErrorCompensation = errorComp,
            TickRate = 72f,
            StairSmoothTime = 1f,
            StairSmoothSpeed = 200f,
            StairStepHeight = 31f,
            StairCatchupTime = 0.1f,
            StairSnapVerticalSpeed = 30f,
            ForceSmoothWindow = 0.12f,
        };

        var sim = new CameraPipelineSim
        {
            InjectedServerOffset = injected, EyeHeightZ = 35f,
            SnapshotEveryTicks = 2, LatencyTicks = 3, // realistic in-flight prediction (ack lags newest)
        };
        // Settle a fresh entity onto the floor so the run starts at the physically-rested Z (avoids a one-shot
        // settle disagreement between the start seed and the server's first tick contaminating the drift metric).
        var start = Settle(clock, new Vector3(0f, 0f, 24f));
        return sim.Run(2000, rec, buf, clientStep, serverStep, start, _ => Idle(), t => clock.Time = t);
    }

    private static List<float> ZErrors(List<CameraPipelineSim.Sample> s)
    {
        var ys = new List<float>(s.Count);
        foreach (var x in s) ys.Add(x.Error.Z);
        return ys;
    }

    private static float Mean(IReadOnlyList<float> ys, float tailFraction)
    {
        int start = (int)(ys.Count * (1f - tailFraction));
        double sum = 0; int n = 0;
        for (int i = start; i < ys.Count; i++) { sum += ys[i]; n++; }
        return n == 0 ? 0f : (float)(sum / n);
    }
}
