using System.Collections.Generic;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression coverage for the legacy input-queue bound (<see cref="InputQueuePolicy"/>, DP
/// <c>sv_clmovement_inputtimeout</c> semantics). The bug this pins down: with the legacy fixed-cadence
/// input mode, the client produces and the server consumes exactly one command per 72 Hz tick — the queue
/// length never shrinks on its own. A client hitch bursts up to 18 commands per long frame while the sim's
/// B3 soft-cap + spiral guard run/drop FEWER catch-up ticks, so the residue became PERMANENT input latency
/// (~0.3-0.5 s after a rough loading stretch) felt as "the projectile leaves half a second after firing".
/// The trim consumes the oldest surplus unsimulated (seqs ack, impulses dispatch) down to a jitter floor.
/// </summary>
public class InputQueuePolicyTests
{
    private static Queue<InputCommand> Burst(int count, uint firstSeq = 1, int impulseOnSeq = -1)
    {
        var q = new Queue<InputCommand>();
        for (int i = 0; i < count; i++)
        {
            var cmd = new InputCommand { Seq = firstSeq + (uint)i };
            if (impulseOnSeq >= 0 && cmd.Seq == (uint)impulseOnSeq)
                cmd.Impulse = 3;   // e.g. a weapon-switch impulse inside the dropped span
            q.Enqueue(cmd);
        }
        return q;
    }

    [Fact]
    public void HitchBurstIsCutToTheJitterFloor()
    {
        // An 18-command burst (a 0.25 s hitch at 72 Hz) must cut to the residual floor in ONE call —
        // the per-tick drain alone would never shrink it, and trimming only to the trigger threshold
        // would leave a ~0.1 s tail as permanent latency.
        Queue<InputCommand> q = Burst(18);
        uint highestDropped = InputQueuePolicy.Trim(q,
            InputQueuePolicy.MaxLegacyQueuedCommands, InputQueuePolicy.LegacyTrimResidual);

        Assert.Equal(InputQueuePolicy.LegacyTrimResidual, q.Count);
        // 18 − 2 = 16 dropped, seqs 1..16; the kept head must be the next seq (17) — oldest dropped first.
        Assert.Equal(16u, highestDropped);
        Assert.Equal(17u, q.Peek().Seq);
    }

    [Fact]
    public void NormalDepthIsNeverTrimmed()
    {
        // A slow remote client's healthy per-packet batching (depth ≤ trigger) must pass untouched —
        // the trim only fires on abnormal hitch bursts.
        Queue<InputCommand> q = Burst(InputQueuePolicy.MaxLegacyQueuedCommands);
        uint highestDropped = InputQueuePolicy.Trim(q,
            InputQueuePolicy.MaxLegacyQueuedCommands, InputQueuePolicy.LegacyTrimResidual);

        Assert.Equal(0u, highestDropped);   // 0 = nothing dropped (real seqs start at 1)
        Assert.Equal(InputQueuePolicy.MaxLegacyQueuedCommands, q.Count);
        Assert.Equal(1u, q.Peek().Seq);
    }

    [Fact]
    public void DroppedImpulsesAreCollectedForDispatch()
    {
        // A weapon-switch impulse inside the dropped span must surface (the server dispatches it once,
        // unsimulated) — a hitch must not eat a weapon switch.
        var impulses = new List<int>();
        Queue<InputCommand> q = Burst(18, firstSeq: 1, impulseOnSeq: 5);
        InputQueuePolicy.Trim(q,
            InputQueuePolicy.MaxLegacyQueuedCommands, InputQueuePolicy.LegacyTrimResidual, impulses);

        Assert.Equal(new[] { 3 }, impulses);
    }

    [Fact]
    public void RepeatedHitchesCannotCompoundLatency()
    {
        // Three hitches in a row: each burst lands on the residual queue; the trim must hold the floor
        // every time (the latency-compounding failure mode).
        var q = new Queue<InputCommand>();
        uint seq = 1;
        for (int hitch = 0; hitch < 3; hitch++)
        {
            for (int i = 0; i < 18; i++)
                q.Enqueue(new InputCommand { Seq = seq++ });
            InputQueuePolicy.Trim(q,
                InputQueuePolicy.MaxLegacyQueuedCommands, InputQueuePolicy.LegacyTrimResidual);
            Assert.True(q.Count <= InputQueuePolicy.LegacyTrimResidual);
        }
        // The surviving head is from the LAST burst's tail — fresh input, not a minutes-old command.
        Assert.True(q.Peek().Seq > 36u);
    }
}
