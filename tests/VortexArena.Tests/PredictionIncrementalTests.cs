using System.Numerics;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The incremental predict cache (zero-hitch 2026-08-03): <see cref="Reconciler.Predict"/> steps only the
/// commands pushed since the last call when its base (serverState, ackedSeq, vars) is unchanged, instead of
/// re-replaying the whole unacked window every input tick — the measured 15-21 ms cn.predict spikes when a
/// late server ack deepened that window. These pin the load-bearing property: the incremental path is
/// BIT-IDENTICAL to a fresh full replay, across pushes, acks (Reconcile re-arming the base), and explicit
/// invalidation — and it genuinely skips the redundant work (step-count assertion).
/// </summary>
public class PredictionIncrementalTests
{
    /// <summary>A deterministic toy integrator whose state depends on every (cmd, vars) it consumed — any
    /// missed/extra/reordered step changes the result, so equality really pins replay equivalence.</summary>
    private sealed class CountingStep : IMovementStep
    {
        public int Steps;
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars)
        {
            Steps++;
            var move = new Vector3(cmd.Forward, cmd.Side, cmd.Up);
            state.Origin += move * cmd.DeltaTime + new Vector3(0.001f, 0f, 0f) * vars.MaxSpeed;
            state.Velocity = state.Velocity * 0.97f + move;
            state.OnGround = !state.OnGround;
        }
    }

    private static InputCommand Cmd(int i) => new()
    {
        Forward = 10f + i,
        Side = i % 3,
        Up = -(i % 5),
        DeltaTime = 1f / 60f,
        ViewAngles = new Vector3(0f, i, 0f),
    };

    [Fact]
    public void Incremental_MatchesFullReplay_AndSkipsRedundantSteps()
    {
        var input = new PredictionBuffer();
        var step = new CountingStep();
        var rec = new Reconciler(input, step);
        var vars = new PlayerState { MaxSpeed = 320f };
        var baseState = new PredictedState { Origin = new Vector3(64f, 8f, 0f) };

        // 30 input ticks between acks: push + Predict each tick (the SendInput cadence).
        PredictedState last = default;
        for (int i = 0; i < 30; i++)
        {
            input.Push(Cmd(i));
            last = rec.Predict(baseState, 0, vars, now: i / 60f);
        }

        // Reference: a fresh reconciler full-replaying the same 30 commands once.
        var refStep = new CountingStep();
        var reference = new Reconciler(input, refStep);
        PredictedState full = reference.Predict(baseState, 0, vars, now: 0.5f);

        Assert.Equal(full.Origin, last.Origin);
        Assert.Equal(full.Velocity, last.Velocity);
        Assert.Equal(full.OnGround, last.OnGround);

        // The work claim: incremental spent ~1 step per tick (30 total); the naive shape would have spent
        // 1+2+...+30 = 465. The reference's single full replay is 30.
        Assert.Equal(30, step.Steps);
        Assert.Equal(30, refStep.Steps);
    }

    [Fact]
    public void Reconcile_ReArmsTheBase_AndLaterPredictsStayIncremental()
    {
        var input = new PredictionBuffer();
        var step = new CountingStep();
        var rec = new Reconciler(input, step);
        var vars = new PlayerState { MaxSpeed = 320f };
        var s0 = new PredictedState();

        for (int i = 0; i < 10; i++) { input.Push(Cmd(i)); rec.Predict(s0, 0, vars, 0f); }

        // Server acks seq 6 with a corrected state: Reconcile full-replays 7..10 and re-arms the cache.
        var acked = new PredictedState { Origin = new Vector3(5f, 5f, 5f) };
        rec.Reconcile(acked, 6, vars, now: 1f, previousPredictionAtAck: rec.Predicted);
        int afterReconcile = step.Steps;

        // Next input tick: exactly ONE more step (incremental against the reconciled base), and it matches
        // a fresh full replay of 7..11 from the acked state.
        input.Push(Cmd(10));
        PredictedState inc = rec.Predict(acked, 6, vars, 2f);
        Assert.Equal(afterReconcile + 1, step.Steps);

        var reference = new Reconciler(input, new CountingStep());
        PredictedState full = reference.Predict(acked, 6, vars, 2f);
        Assert.Equal(full.Origin, inc.Origin);
        Assert.Equal(full.Velocity, inc.Velocity);
    }

    [Fact]
    public void Invalidate_ForcesTheNextPredictToFullReplay()
    {
        var input = new PredictionBuffer();
        var step = new CountingStep();
        var rec = new Reconciler(input, step);
        var vars = new PlayerState { MaxSpeed = 320f };
        var s0 = new PredictedState();

        for (int i = 0; i < 5; i++) { input.Push(Cmd(i)); rec.Predict(s0, 0, vars, 0f); }
        int before = step.Steps;                 // 5 incremental steps
        Assert.Equal(5, before);

        // vars change (the movevars-reassignment case): the cached base is stale — invalidate, and the next
        // Predict must full-replay all 5 under the NEW vars, matching a fresh reconciler exactly.
        var vars2 = new PlayerState { MaxSpeed = 400f };
        rec.InvalidatePrediction();
        PredictedState after = rec.Predict(s0, 0, vars2, 1f);
        Assert.Equal(before + 5, step.Steps);    // full replay, not +1

        PredictedState full = new Reconciler(input, new CountingStep()).Predict(s0, 0, vars2, 1f);
        Assert.Equal(full.Origin, after.Origin);
        Assert.Equal(full.Velocity, after.Velocity);
    }
}
