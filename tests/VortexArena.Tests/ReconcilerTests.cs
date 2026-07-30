using System;
using System.Numerics;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Prediction-error smoother (<see cref="Reconciler.SetPredictionError"/>) tests — the view-smoothing half of
/// client-side reconciliation (port of csqcmodel <c>CSQCPlayer_SetPredictionError</c>).
///
/// A small in-bounds error arms a decaying offset that glides the camera onto the corrected position. But a
/// TELEPORT-sized origin jump (respawn / teleporter / the observer→spawn transition) must SNAP — and crucially
/// CLEAR any accumulated smoothing, or the offset the predictor built up (e.g. while a frozen observer, or
/// across a spawn fall) survives the teleport and floats the camera far from the player. A velocity-only spike
/// (jumppad / jump-time disagreement) is NOT a teleport and must be ignored WITHOUT clearing the residual
/// origin smoothing, faithful to QC's <c>vdist(o,&gt;,32) || vdist(v,&gt;,192)</c> split.
/// </summary>
public class ReconcilerTests
{
    private sealed class NoOpStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) { }
    }

    /// <summary>A perfectly deterministic step: advances origin by a fixed delta per tick (models a client and
    /// server whose sims agree exactly — so any measured prediction error must come from the MEASUREMENT, not the sim).</summary>
    private sealed class ConstantStep : IMovementStep
    {
        private readonly Vector3 _d;
        public ConstantStep(Vector3 d) { _d = d; }
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) => state.Origin += _d;
    }

    private static Reconciler NewReconciler() =>
        new(new PredictionBuffer(), new NoOpStep()) { ErrorCompensation = 100f, TickRate = 72f };

    [Fact]
    public void Reconcile_PerfectPrediction_WithInFlightCommands_InjectsNoError()
    {
        // The reconcile must measure the prediction error LIKE-FOR-LIKE (same timeline frame). When the client is
        // predicting ahead of the server (commands in flight) and the deterministic sims AGREE, the error fed to
        // the smoother must be ~0 — otherwise the camera is tugged by a phantom offset that scales with player
        // speed × in-flight depth every snapshot (the "view jumps while moving" bug). Regression guard for that.
        var buf = new PredictionBuffer();
        var rec = new Reconciler(buf, new ConstantStep(new Vector3(5f, 0f, 0f))) { ErrorCompensation = 1f, TickRate = 72f };

        // Client pushed 7 commands and predicted forward from origin@seq0 → predicted (35,0,0) at newest=7.
        for (int i = 0; i < 7; i++) buf.Push(new InputCommand { Forward = 1f });
        var seed0 = new PredictedState { Origin = Vector3.Zero, OnGround = true };
        rec.Predict(seed0, ackedSeq: 0, default, now: 0f);
        Assert.Equal(35f, rec.Predicted.Origin.X, 3);

        // A snapshot acks seq 3 (truth there = (15,0,0)); 4 commands are still in flight (a real prediction lead).
        // The old form diffed truth@3 (15) against prediction@newest (35) → a 20u phantom error (<32u, so it would
        // be smoothed, not discarded). The fixed form compares old-vs-new prediction at the same newest frame → 0.
        PredictedState prevAtNewest = rec.Predicted;
        var serverState = new PredictedState { Origin = new Vector3(15f, 0f, 0f), OnGround = true };
        rec.Reconcile(serverState, ackedSeq: 3, default, now: 0f, prevAtNewest);

        Assert.True(rec.GetPredictionErrorOrigin(0f).Length() < 1e-3f,
            $"perfect prediction with 4 in-flight commands must inject NO smoother error; got {rec.GetPredictionErrorOrigin(0f).Length():F3}");
        // The corrected prediction still lands at the right place (replay from truth@3 forward to newest).
        Assert.Equal(35f, rec.Predicted.Origin.X, 3);
    }

    [Fact]
    public void SetPredictionError_TeleportSizedOriginJump_SnapsAndClearsAccumulatedSmoothing()
    {
        Reconciler rec = NewReconciler();

        // A small in-bounds error arms the view smoother (a nonzero decaying offset).
        rec.SetPredictionError(new Vector3(0f, 0f, 5f), Vector3.Zero, now: 0f);
        Assert.True(rec.GetPredictionErrorOrigin(0f).Length() > 0.5f, "a small error should arm the smoother");

        // A teleport-sized ORIGIN jump (respawn / teleporter / observer→spawn) must SNAP: clear the accumulated
        // smoothing rather than fold the jump in or leave the old offset floating the view.
        rec.SetPredictionError(new Vector3(1000f, 0f, 0f), Vector3.Zero, now: 1f / 72f);
        Assert.True(rec.GetPredictionErrorOrigin(1f / 72f).Length() < 1e-3f, "a teleport must clear the smoother");
    }

    [Fact]
    public void SetPredictionError_KnockbackSpike_SmoothsAsForce_NotDiscarded()
    {
        // PORT EXTENSION (diverges from stock QC's vdist(v,>,192) discard): jumppads + teleporters are now client-
        // predicted, so a big VELOCITY spike with a SMALL origin delta is damage KNOCKBACK (a rocket/blaster shove),
        // not a jumppad/teleport to discard. The smoother arms it over the longer ForceSmoothWindow so the shove
        // glides the view, accumulating onto — never clearing — any residual origin smoothing already in flight.
        var rec = new Reconciler(new PredictionBuffer(), new NoOpStep())
            { ErrorCompensation = 100f, TickRate = 72f, ForceSmoothWindow = 0.12f };

        rec.SetPredictionError(new Vector3(0f, 0f, 5f), Vector3.Zero, now: 0f);
        float before = rec.GetPredictionErrorOrigin(0f).Length();
        Assert.True(before > 0.5f, "precondition: the residual smoother is armed");

        // Knockback signature: |v| > 192 with a small origin delta. Smoothed (force), not discarded.
        rec.SetPredictionError(new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 500f), now: 0f);
        Assert.True(rec.GetPredictionErrorOrigin(0f).Length() >= before - 1e-3f,
            "a knockback shove must accumulate onto (never clear) residual origin smoothing");
        Assert.True(rec.GetPredictionErrorVelocity(0f).Length() > 1f,
            "a knockback shove must arm the velocity-error smoother so the rendered velocity ramps into the shove");

        // The force window (~0.12 s) is far longer than the tiny errorcomp=100 residual window (~0.14 ms): the offset
        // is still decaying well into it (proof it took the force path, not the one-tick residual path)...
        Assert.True(rec.GetPredictionErrorOrigin(0.05f).Length() > 0.1f,
            "the knockback glide must persist across the ~0.12 s force window, not the sub-ms residual window");
        // ...and fully decays once the force window elapses.
        Assert.True(rec.GetPredictionErrorOrigin(0.2f).Length() < 1e-3f,
            "the knockback glide must fully decay once the force window elapses");
    }

    [Fact]
    public void SetPredictionError_TeleportWithVelocityChange_StillSnaps()
    {
        // A genuine cross-map teleport whose exit ALSO redirects velocity (>192) must NOT be mistaken for knockback
        // and smoothed: the origin jump dwarfs MaxForceOrigin (250 u), so it snaps + clears like any teleport.
        Reconciler rec = NewReconciler();
        rec.SetPredictionError(new Vector3(0f, 0f, 5f), Vector3.Zero, now: 0f);
        Assert.True(rec.GetPredictionErrorOrigin(0f).Length() > 0.5f, "precondition: the smoother is armed");

        rec.SetPredictionError(new Vector3(1000f, 0f, 0f), new Vector3(0f, 0f, 400f), now: 1f / 72f);
        Assert.True(rec.GetPredictionErrorOrigin(1f / 72f).Length() < 1e-3f,
            "a teleport-sized origin jump (even with a velocity change) must snap, not smooth as knockback");
    }

    [Fact]
    public void SetPredictionError_ResidualDecayWindow_IsOneTick_NotSeconds()
    {
        // Fix #1 (units): QC's factor is errorcompensation / ticrate where ticrate is the tick PERIOD in seconds, so
        // the decay window is ONE TICK. The port multiplies by TickRate(Hz). With errorcomp=1 @ 72 Hz the window is
        // ~13.9 ms: a small residual is fully decayed by 2 ticks — NOT lingering for ~72 s (the old divide-by-Hz bug).
        var rec = new Reconciler(new PredictionBuffer(), new NoOpStep()) { ErrorCompensation = 1f, TickRate = 72f };
        rec.SetPredictionError(new Vector3(0f, 0f, 5f), Vector3.Zero, now: 0f);
        Assert.True(rec.GetPredictionErrorOrigin(0f).Length() > 4f, "armed at full magnitude at t=0");
        Assert.True(rec.GetPredictionErrorOrigin(0.5f / 72f).Length() > 0.1f, "still decaying mid-tick");
        Assert.True(rec.GetPredictionErrorOrigin(2f / 72f).Length() < 1e-3f,
            "a residual must fully decay within one tick (~13.9 ms), not linger for seconds");
    }

    // ---- Stair-step view smoothing ------------------------------------------------------------------
    // The smoother tracks an absolute rendered Z that catches up to the LIVE predicted Z at StairSmoothSpeed,
    // hard-clamped to within one step height. Following a genuine jump/fall (airborne AND |velocity.z| >
    // StairSnapVerticalSpeed) it adds a velocity FEEDFORWARD so the camera keeps pace with the arc (no lag) while
    // still EASING — not snapping — an instant step pop clipped mid-jump.

    /// <summary>Drives the predicted pose to a controllable Z + onground + vertical velocity so we can sample the
    /// per-frame smoother (vertical velocity drives the airborne snap gate).</summary>
    private sealed class ZStep : IMovementStep
    {
        public float Z;
        public bool OnGround = true;
        public float VelZ;
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars)
        { state.Origin = new Vector3(0f, 0f, Z); state.OnGround = OnGround; state.Velocity = new Vector3(0f, 0f, VelZ); }
    }

    private static Reconciler NewStairRec(ZStep step, PredictionBuffer buf)
    {
        buf.Push(new InputCommand());
        // StairCatchupTime 0 → pure fixed-speed catch-up, so the legacy single-step/slope assertions below stay exact.
        return new Reconciler(buf, step) { StairSmoothTime = 0.16f, StairSmoothSpeed = 160f, StairCatchupTime = 0f };
    }

    private static void DriveZ(Reconciler rec, ZStep step, float z, bool onGround = true, float velZ = 0f)
    {
        step.Z = z; step.OnGround = onGround; step.VelZ = velZ;
        rec.Predict(new PredictedState { Origin = new Vector3(0f, 0f, z), OnGround = onGround, Velocity = new Vector3(0f, 0f, velZ) },
            ackedSeq: 0, default, now: 0f);
    }

    private const float FrameDt = 1f / 72f; // a typical render-frame delta

    [Fact]
    public void Stair_StepUp_Grounded_OffsetAppearsThenDecaysToZero()
    {
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = NewStairRec(step, buf);

        DriveZ(rec, step, 0f);
        Assert.Equal(0f, rec.GetStairSmoothOffset(FrameDt), 3); // seed frame, no offset

        // A 16u grounded step-up in one tick: the rendered Z lags, so the camera offset jumps toward the step...
        DriveZ(rec, step, 16f);
        float justAfter = rec.GetStairSmoothOffset(FrameDt);
        Assert.True(justAfter > 8f, $"right after a step-up the view should lag the true Z (got {justAfter})");

        // ...and at StairSmoothSpeed (160 u/s) it catches up within ~16/160 s. After 20 frames it is ~0.
        for (int i = 0; i < 20; i++) rec.GetStairSmoothOffset(FrameDt);
        Assert.True(MathF.Abs(rec.GetStairSmoothOffset(FrameDt)) < 0.5f,
            $"the stair offset must decay to ~0 once the Z stabilizes (got {rec.GetStairSmoothOffset(FrameDt)})");
    }

    [Fact]
    public void Stair_ContinuousSlope_OffsetStaysBounded_AndClearsWhenFlat()
    {
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = NewStairRec(step, buf);

        DriveZ(rec, step, 0f);
        rec.GetStairSmoothOffset(FrameDt);

        // Walk up a STEEP slope: +8u every render frame for 60 frames (Z rate ~576 u/s >> StairSmoothSpeed). The OLD
        // code would re-arm every tick and pin the offset; the rewrite hard-clamps the lag to one step height.
        float z = 0f;
        for (int i = 0; i < 60; i++)
        {
            z += 8f;
            DriveZ(rec, step, z);
            float off = rec.GetStairSmoothOffset(FrameDt);
            Assert.True(MathF.Abs(off) <= 31f + 1e-3f, $"slope offset must stay clamped to one step height (31u), got {off} at frame {i}");
        }

        // Reach flat ground (Z stops changing): the offset must catch up to ~0 (not stay pinned).
        for (int i = 0; i < 30; i++) rec.GetStairSmoothOffset(FrameDt);
        Assert.True(MathF.Abs(rec.GetStairSmoothOffset(FrameDt)) < 0.5f,
            $"once on flat ground the pinned-on-slope offset must clear (got {rec.GetStairSmoothOffset(FrameDt)})");
    }

    [Fact]
    public void Stair_Airborne_SteadyJump_TrackedNoLag()
    {
        // A steady jump (eye-Z rising consistently with velocity.z) is tracked 1:1 by the velocity feedforward, so
        // the camera keeps pace with the arc with ~no lag (offset ~0) — what the old snap achieved, minus the snap.
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = NewStairRec(step, buf);

        float z = 0f; const float vz = 260f;
        DriveZ(rec, step, z, onGround: false, velZ: vz);
        rec.GetStairSmoothOffset(FrameDt); // seed

        for (int i = 0; i < 10; i++)
        {
            z += vz * FrameDt;   // a consistent ballistic rise (FrameDt == one tic here)
            DriveZ(rec, step, z, onGround: false, velZ: vz);
            float off = rec.GetStairSmoothOffset(FrameDt);
            Assert.True(MathF.Abs(off) < 0.5f, $"a steady jump must be tracked with no lag (offset {off} at i={i})");
        }
    }

    [Fact]
    public void Stair_Airborne_FlickerWithSmallVelocity_KeepsSmoothing()
    {
        // The anti-jitter fix: a stair step momentarily clears the onground flag for ONE tick while velocity.z stays
        // ~0 (the step is a positional pop). The smoother must KEEP RUNNING across that flicker instead of snapping —
        // snapping on it was what made stair climbing jitter despite the smoother.
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = NewStairRec(step, buf);

        DriveZ(rec, step, 0f);
        rec.GetStairSmoothOffset(FrameDt);

        // 16u step up, reported AIRBORNE for this tick (the flicker) but with a tiny vertical velocity (gravity blip).
        DriveZ(rec, step, 16f, onGround: false, velZ: -8f);
        float off = rec.GetStairSmoothOffset(FrameDt);
        Assert.True(off > 8f, $"a low-velocity airborne flicker on a step must still be smoothed (got {off})");
    }

    [Fact]
    public void Stair_Airborne_JumpIntoStep_PopEasedNotSnapped()
    {
        // Clipping a step on the way up a jump: the eye-Z change = ballistic rise + a step pop. The feedforward
        // tracks the ballistic part; the step pop is EASED (an offset appears and then decays) instead of snapping
        // the camera to the step. Needs the adaptive term, so use a real catch-up time.
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = new(buf, step)
            { StairSmoothTime = 1f, StairSmoothSpeed = 200f, StairCatchupTime = 0.1f, StairStepHeight = 31f, StairSnapVerticalSpeed = 30f };
        buf.Push(new InputCommand());

        const float vz = 200f;
        DriveZ(rec, step, 0f, onGround: false, velZ: vz);
        rec.GetStairSmoothOffset(FrameDt); // seed

        float z = vz * FrameDt + 24f;       // one tick: ballistic rise + a 24u step clip
        DriveZ(rec, step, z, onGround: false, velZ: vz);
        float first = rec.GetStairSmoothOffset(FrameDt);
        Assert.True(first > 8f, $"the step pop clipped mid-jump should be eased (camera trails the step), not snapped away (got {first})");

        // ...and it decays as the camera catches up.
        for (int i = 0; i < 20; i++) rec.GetStairSmoothOffset(FrameDt);
        float later = rec.GetStairSmoothOffset(FrameDt);
        Assert.True(later < first, $"the eased step pop must decay (first {first}, later {later})");
        Assert.True(MathF.Abs(later) < 0.5f, $"the step pop must settle to ~0 (got {later})");
    }

    [Fact]
    public void Stair_FastMultiStepClimb_AdaptiveCatchup_ReducesPerStepYank()
    {
        // At running speed you climb a staircase faster than the fixed floor catch-up, so the old fixed-speed
        // smoother saturated and the ±step clamp yanked the rendered Z up on every step (the jitter). The adaptive
        // catch-up scales with the lag, so it stays closer to the live Z and the per-step clamp yank shrinks.
        // A/B: the same discrete climb with adaptive catch-up must yank LESS than with the fixed-speed (old) model.
        float adaptiveWorst = WorstPerFrameYankClimbing(catchupTime: 0.1f);
        float fixedWorst    = WorstPerFrameYankClimbing(catchupTime: 0f);
        Assert.True(adaptiveWorst < fixedWorst,
            $"adaptive catch-up should reduce the per-step camera yank (adaptive {adaptiveWorst:F2}u vs fixed {fixedWorst:F2}u)");
    }

    /// <summary>Walk a 24u-per-2-tic staircase (~860 u/s vertical, faster than the 200 u/s floor) and return the
    /// worst single-render-frame change in the rendered camera Z — the visible "yank".</summary>
    private static float WorstPerFrameYankClimbing(float catchupTime)
    {
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = new(buf, step) { StairSmoothTime = 1f, StairSmoothSpeed = 200f, StairCatchupTime = catchupTime, StairStepHeight = 31f };
        buf.Push(new InputCommand());

        DriveZ(rec, step, 0f);
        rec.GetStairSmoothOffset(1f / 144f);

        float trueZ = 0f, prevRenderedZ = 0f, worst = 0f;
        for (int s = 0; s < 10; s++)
        {
            trueZ += 24f;
            DriveZ(rec, step, trueZ);                  // grounded walk up the stairs (velocity.z ~ 0)
            for (int f = 0; f < 2; f++)                // ~144 fps: render frames within the 1/72 s tic pair
            {
                float renderedZ = trueZ - rec.GetStairSmoothOffset(1f / 144f);
                if (s > 1) worst = MathF.Max(worst, MathF.Abs(renderedZ - prevRenderedZ)); // skip the warm-up steps
                prevRenderedZ = renderedZ;
            }
        }
        return worst;
    }

    [Fact]
    public void Stair_FlatGround_NeverOffsets()
    {
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = NewStairRec(step, buf);

        for (int i = 0; i < 30; i++)
        {
            DriveZ(rec, step, 25f); // constant Z, on ground
            Assert.Equal(0f, rec.GetStairSmoothOffset(FrameDt), 3);
        }
    }

    [Fact]
    public void Stair_JitteryFrameDt_DoesNotJitterOnFlatGround()
    {
        // Regression for the reported up/down jitter: feeding an IRREGULAR per-frame dt (the symptom of driving the
        // smoother off the snapshot-rebasing render clock) must NOT produce a nonzero offset on flat ground — the
        // rendered Z is already AT the true Z, so any clamped dt leaves it there.
        var buf = new PredictionBuffer();
        var step = new ZStep();
        Reconciler rec = NewStairRec(step, buf);

        DriveZ(rec, step, 25f);
        rec.GetStairSmoothOffset(FrameDt);

        float[] jitter = { 0f, 1f / 144f, 1f / 30f, 0f, 1f / 72f, 0.2f /*hitch, clamped*/, 1f / 240f };
        foreach (float d in jitter)
        {
            DriveZ(rec, step, 25f);
            Assert.Equal(0f, rec.GetStairSmoothOffset(d), 3);
        }
    }
}
