using System.Collections.Generic;

namespace XonoticGodot.Net;

/// <summary>
/// Server-side input-queue bounding for the LEGACY fixed-cadence input mode (DP
/// <c>sv_clmovement_inputtimeout</c> semantics, default 0.1 s).
///
/// <para><b>Why this must exist:</b> the legacy client emits exactly one <see cref="InputCommand"/> per
/// 1/72 s of real time, and the server consumes exactly one per 72 Hz tick — so the queue's steady-state
/// length NEVER changes on its own. After a client hitch the accumulated commands arrive as one burst
/// (NetGame caps the burst at 0.25 s ≈ 18 commands), and without a bound that backlog becomes PERMANENT
/// input latency: every fire/jump/turn for the rest of the match executes queue-length ticks late, and
/// each further hitch compounds it. DP bounds the same failure by discarding moves older than
/// <c>sv_clmovement_inputtimeout</c>; this is the port's equivalent.</para>
///
/// <para>Dropped commands are "consumed unsimulated": the caller acks their <see cref="InputCommand.Seq"/>
/// (so client prediction stops replaying them — client and server agree the movement never happened, a
/// brief reconcile warp exactly like DP) and dispatches their one-shot impulses (a weapon switch must not
/// vanish with the hitch). The held-button state needs no folding: the kept newest commands carry it.</para>
/// </summary>
public static class InputQueuePolicy
{
    /// <summary>
    /// Queue depth past which the backlog is abnormal (a hitch burst, never healthy pacing): 7 ticks
    /// ≈ 0.097 s at 72 Hz — DP's <c>sv_clmovement_inputtimeout</c> default (0.1 s). A slow remote client's
    /// normal per-packet batching (2-5 commands at 15-30 fps) stays under this, so steady traffic is
    /// never trimmed.
    /// </summary>
    public const int MaxLegacyQueuedCommands = 7;

    /// <summary>
    /// Depth a triggered trim cuts down TO: a 2-command jitter floor (~28 ms). Cutting only down to the
    /// trigger threshold would leave a ~0.1 s tail as PERMANENT latency (the steady drain can never shrink
    /// it); cutting to the floor restores pre-hitch input feel in one tick.
    /// </summary>
    public const int LegacyTrimResidual = 2;

    /// <summary>
    /// When <paramref name="pending"/> (seq-monotonic, oldest first) exceeds <paramref name="triggerMax"/>,
    /// discard the oldest commands down to <paramref name="residual"/>, collecting any one-shot impulses
    /// carried by dropped commands into <paramref name="droppedImpulses"/> (when non-null). Returns the
    /// highest dropped <see cref="InputCommand.Seq"/>, or 0 when nothing was dropped (real seqs start at 1,
    /// so 0 is a safe "none" sentinel for the caller's ack-cursor max).
    /// </summary>
    public static uint Trim(Queue<InputCommand> pending, int triggerMax, int residual,
        List<int>? droppedImpulses = null)
    {
        if (pending.Count <= triggerMax)
            return 0;
        uint highestDropped = 0;
        while (pending.Count > residual)
        {
            InputCommand dropped = pending.Dequeue();
            highestDropped = dropped.Seq;   // queue is enqueued seq-monotonic; the last dropped is the highest
            if (dropped.Impulse != 0)
                droppedImpulses?.Add(dropped.Impulse);
        }
        return highestDropped;
    }
}
