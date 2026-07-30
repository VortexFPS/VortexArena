using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Net;

namespace XonoticGodot.Tests.Camera;

/// <summary>
/// Shared helpers for the camera-drift apparatus: the exact rendered-eye composition the net path uses
/// (mirrors <c>game/net/NetGame.UpdateCamera</c>), and drift metrics (secular slope + max magnitude) over a
/// long sample stream. Pure <see cref="System.Numerics"/> — no Godot — so it runs headless under xUnit.
/// </summary>
public static class CameraComposition
{
    /// <summary>
    /// The PORT rendered eye (Quake space), exactly as <c>NetGame.UpdateCamera</c> composes it:
    /// <c>predicted = PredictedOrigin + errorOffset(now); predicted += viewVel*inputAccum; predicted.Z -=
    /// stairOffset(frameDt); eye = predicted + (0,0,eyeHeightZ)</c>. <paramref name="frameDt"/> advances the
    /// stair smoother, so call this exactly ONCE per render frame.
    /// </summary>
    public static Vector3 PortEye(Reconciler rec, float now, float frameDt, float inputAccum, float eyeHeightZ)
    {
        Vector3 predicted = rec.Predicted.Origin + rec.GetPredictionErrorOrigin(now);
        Vector3 viewVel = rec.Predicted.Velocity + rec.GetPredictionErrorVelocity(now);
        predicted += viewVel * inputAccum;
        predicted.Z -= rec.GetStairSmoothOffset(frameDt);
        return predicted + new Vector3(0f, 0f, eyeHeightZ);
    }
}

/// <summary>Drift metrics over a per-frame error stream.</summary>
public static class DriftMetrics
{
    /// <summary>The maximum absolute value in the stream.</summary>
    public static float MaxAbs(IReadOnlyList<float> ys)
    {
        float m = 0f;
        foreach (float y in ys) m = MathF.Max(m, MathF.Abs(y));
        return m;
    }

    /// <summary>
    /// Least-squares slope of y vs sample index, in units PER SAMPLE — the "secular" (non-transient) drift rate.
    /// A correct smoother converges to a steady state, so its slope over the back half of a long run is ~0; a
    /// genuine accumulating drift shows a persistent nonzero slope. Use <see cref="SlopeOverTail"/> to ignore the
    /// initial settling transient.
    /// </summary>
    public static float Slope(IReadOnlyList<float> ys)
    {
        int n = ys.Count;
        if (n < 2) return 0f;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            sx += i; sy += ys[i]; sxx += (double)i * i; sxy += (double)i * ys[i];
        }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12) return 0f;
        return (float)((n * sxy - sx * sy) / denom);
    }

    /// <summary>Slope over the final <paramref name="tailFraction"/> of the stream (skip the settling transient).</summary>
    public static float SlopeOverTail(IReadOnlyList<float> ys, float tailFraction = 0.5f)
    {
        int start = (int)(ys.Count * (1f - tailFraction));
        var tail = new List<float>(ys.Count - start);
        for (int i = start; i < ys.Count; i++) tail.Add(ys[i]);
        return Slope(tail);
    }
}

/// <summary>
/// A configurable, deterministic full-pipeline simulation: a server simulation and a client predictor (both the
/// SAME <see cref="PlayerPhysics"/> via <see cref="PlayerPhysicsStep"/>) driven by an identical per-tick input
/// stream, with realistic snapshot cadence + ack latency, plus optional injected disagreements that reproduce the
/// real-world prediction error the camera smoothing must absorb. Records the rendered-eye error per frame.
/// </summary>
public sealed class CameraPipelineSim
{
    public float TickDt = 1f / 72f;
    /// <summary>Send a server snapshot (and reconcile) every N ticks (1 = every tick).</summary>
    public int SnapshotEveryTicks = 1;
    /// <summary>The snapshot the client receives this tick reflects the server N ticks ago (in-flight depth).</summary>
    public int LatencyTicks = 0;
    /// <summary>Eye height (view offset Z) used for the rendered eye and the "true" eye reference.</summary>
    public float EyeHeightZ = 35f;

    /// <summary>
    /// Optional injected per-snapshot disagreement (Quake units) added to the SERVER seed the client reconciles
    /// from, modelling a systematic client/server mismatch the deterministic sim alone can't produce (e.g. a
    /// server-only nudge, an antilag rewind, an onground/jump-time disagreement). Same-direction values stress the
    /// smoother's accumulation; this is how the harness reproduces the reported drift.
    /// </summary>
    public Vector3 InjectedServerOffset = Vector3.Zero;

    /// <summary>Per-frame render delta pattern (cycled). Empty → one render frame per tick at <see cref="TickDt"/>.</summary>
    public float[] RenderDtPattern = Array.Empty<float>();

    /// <summary>When set, the rendered eye is composed via this FAITHFUL Base reference (CSQCPlayer_ApplySmoothing
    /// + errorcomp decay) instead of the port's <see cref="Reconciler"/> render-offset path — so the same pipeline
    /// can be measured under both regimes.</summary>
    public CameraReferenceQc? Faithful;

    public readonly record struct Sample(int Tick, Vector3 RenderedEye, Vector3 TrueEye)
    {
        public Vector3 Error => RenderedEye - TrueEye;
    }

    /// <summary>
    /// Run the pipeline for <paramref name="ticks"/> ticks with the per-tick <paramref name="input"/> generator
    /// and a configured <paramref name="rec"/> (its smoothing params select port vs faithful behavior). Returns
    /// the per-frame samples (rendered eye vs the authoritative "true" eye). The same <paramref name="serverStep"/>
    /// /<paramref name="clientStep"/> are distinct carriers running the identical sim.
    /// </summary>
    public List<Sample> Run(
        int ticks,
        Reconciler rec,
        PredictionBuffer buf,
        PlayerPhysicsStep clientStep,
        PlayerPhysicsStep serverStep,
        PredictedState start,
        Func<int, InputCommand> input,
        Action<float> setClock)
    {
        var samples = new List<Sample>(ticks * 2);
        var serverHistory = new List<PredictedState>(ticks + 1) { start };

        PredictedState lastSeed = start;
        uint lastAcked = 0;
        float now = 0f;
        int renderIdx = 0;

        // Seed the predictor to the start state (Predicted == start) BEFORE the first reconcile, so the first
        // snapshot measures its error against a real prior prediction — not the default zero pose (which would
        // inject a one-shot ~origin-sized phantom error). Production seeds via the FROMSERVER path; this is the
        // harness equivalent. The buffer is empty here, so Predict replays nothing.
        rec.Predict(start, 0, default, now);

        // Server runs the authoritative sim from the start state forward.
        PredictedState serverState = start;

        for (int t = 1; t <= ticks; t++)
        {
            now += TickDt;
            setClock(now);

            InputCommand cmd = input(t);
            cmd.DeltaTime = TickDt;

            // --- server authoritative step ---
            serverStep.Step(ref serverState, in cmd, default);
            serverHistory.Add(serverState);

            // --- client: reconcile a delayed snapshot FIRST (real order: Poll → SendInput), then push + predict ---
            // Reconciling before pushing tick t's input keeps `prevAtNewest` (last Predict, at newest=t-1) and the
            // reconcile replay at the SAME newest, so the error measured is a true same-frame residual (not a
            // phantom one-tick-of-motion). Mirrors NetGame._Process: ClientNet.Poll runs before SendInput.
            int ackTick = t - LatencyTicks;
            bool snapshot = ackTick >= 1 && (ackTick % SnapshotEveryTicks == 0);
            if (snapshot)
            {
                PredictedState seed = serverHistory[ackTick];
                seed.Origin += InjectedServerOffset; // injected systematic disagreement
                PredictedState prevAtNewest = rec.Predicted;
                rec.Reconcile(seed, (uint)ackTick, default, now, prevAtNewest);
                lastSeed = seed;
                lastAcked = (uint)ackTick;

                // Mirror the same error measurement into the faithful reference (Base CSQCPlayer_SetCamera feeds
                // CSQCPlayer_SetPredictionError the same residual): old prediction − new prediction at `newest`.
                Faithful?.SetPredictionError(prevAtNewest.Origin - rec.Predicted.Origin,
                                             prevAtNewest.Velocity - rec.Predicted.Velocity, now);
            }

            // SendInput: push this tick's input and predict forward to the new newest (= t).
            buf.Push(cmd);
            rec.Predict(lastSeed, lastAcked, default, now);

            // --- render frame(s) for this tick ---
            {
                float frameDt = RenderDtPattern.Length == 0 ? TickDt : RenderDtPattern[renderIdx++ % RenderDtPattern.Length];
                float eyeZ = clientStep.Carrier.ViewOfs.Z > 0f ? clientStep.Carrier.ViewOfs.Z : EyeHeightZ;
                Vector3 rendered;
                if (Faithful is not null)
                {
                    // Faithful Base eye: predicted origin + (decaying, off-by-default) error offset, then
                    // CSQCPlayer_ApplySmoothing (which adds the smoothed view height itself).
                    Vector3 o = rec.Predicted.Origin + Faithful.GetPredictionErrorO(now);
                    rendered = Faithful.ApplySmoothing(o, now, drawtime: now - TickDt,
                        onground: rec.Predicted.OnGround, viewOfsZ: eyeZ);
                }
                else
                {
                    rendered = CameraComposition.PortEye(rec, now, frameDt, inputAccum: 0f, eyeZ);
                }
                Vector3 trueEye = serverState.Origin + new Vector3(0f, 0f, EyeHeightZ);
                samples.Add(new Sample(t, rendered, trueEye));
            }
        }

        return samples;
    }
}
