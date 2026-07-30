// Port of Base/darkplaces/cmd.c (Cmd_Defer_f + Cbuf_Execute_Deferred, the `defer` command + its pump).
using System.Collections.Generic;
using System.Globalization;

namespace XonoticGodot.Server;

/// <summary>
/// A sim-clock command queue — the C# successor to Darkplaces' deferred command buffer
/// (<c>cmd.c</c> <c>Cmd_Defer_f</c> + <c>Cbuf_Execute_Deferred</c>). A <c>defer &lt;seconds&gt; &lt;command&gt;</c>
/// links a command to run after a delay; <see cref="Pump"/> (called each server tick) fires every entry whose
/// delay has elapsed, in insertion order, and removes it. This is the queue a passed <c>restart</c> vote needs:
/// <c>VoteController</c> builds <c>defer 1 restart</c> so the announcer/result shows before the match resets, and
/// without this queue that command silently no-ops (the original bug).
///
/// <para><b>Time base (deviation R1):</b> DP pumps off <c>host.realtime</c> (advances even while paused); the
/// port pumps off the SIM clock (<c>GameWorld.Time</c>), which still ticks during a timeout pause
/// (SimulationLoop always advances <c>Time</c>), so for the 1 s restart defer this matches DP closely and is
/// deterministic/testable. DP's 1/128 s quantization gate is a micro-optimization (unobservable at 1 s) and is
/// not reproduced — the pump simply fires on the first tick whose <c>now &gt;= fireTime</c>.</para>
/// </summary>
public sealed class DeferredCommands
{
    private struct Entry
    {
        public float FireTime;   // absolute sim time at which the command runs (now + delay)
        public float Delay;      // the original delay, for the `defer` listing ("-> In %9.2f: %s")
        public string Command;
    }

    private readonly List<Entry> _pending = new();

    /// <summary>Whether any commands are queued (QC <c>!List_Is_Empty(&amp;cbuf-&gt;deferred)</c>).</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>How many commands are queued.</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// DP <c>Cmd_Defer_f</c> enqueue path: link <paramref name="command"/> to run <paramref name="delaySeconds"/>
    /// seconds from <paramref name="now"/>. A non-positive delay fires on the next <see cref="Pump"/>.
    /// </summary>
    public void Defer(float delaySeconds, string command, float now)
    {
        if (string.IsNullOrEmpty(command))
            return; // DP requires strlen(argv(2)) > 0
        _pending.Add(new Entry
        {
            FireTime = now + (delaySeconds > 0f ? delaySeconds : 0f),
            Delay = delaySeconds,
            Command = command,
        });
    }

    /// <summary>DP <c>defer clear</c>: drop ALL pending deferred commands.</summary>
    public void Clear() => _pending.Clear();

    /// <summary>
    /// DP <c>defer</c> (no args) listing: one line per pending entry, "-> In %9.2f: &lt;command&gt;" with the
    /// REMAINING time (delay) at <paramref name="now"/>, or a single "No commands are pending." line. Returned as
    /// strings so the command handler can route them to its output sink.
    /// </summary>
    public IReadOnlyList<string> Describe(float now)
    {
        if (_pending.Count == 0)
            return new[] { "No commands are pending." };
        var lines = new List<string>(_pending.Count);
        foreach (Entry e in _pending)
        {
            float remaining = e.FireTime - now;
            // DP prints current->delay (the field it decrements each frame) with %9.2f.
            lines.Add($"-> In {remaining,9:0.00}: {e.Command}");
        }
        return lines;
    }

    /// <summary>
    /// DP <c>Cbuf_Execute_Deferred</c>: fire (in insertion order) every entry whose <c>FireTime &lt;= now</c>,
    /// removing it, and run <paramref name="run"/> on its command (DP <c>Cbuf_AddText</c>). Entries that haven't
    /// elapsed are kept. A command queued by another command WHILE pumping (e.g. it re-defers) lands after this
    /// pass (it's added to the tail; the snapshot below isn't re-scanned this tick), matching DP's list walk.
    /// </summary>
    public void Pump(float now, System.Action<string> run)
    {
        if (_pending.Count == 0)
            return;
        // Collect the due commands first (in order), remove them, THEN run — so a command that itself enqueues
        // a new defer doesn't get fired again this same tick (DP moves the node to the free list before AddText).
        List<string>? due = null;
        for (int i = 0; i < _pending.Count; /* advance in body */)
        {
            if (_pending[i].FireTime <= now)
            {
                (due ??= new List<string>()).Add(_pending[i].Command);
                _pending.RemoveAt(i); // preserves order of the remaining entries
            }
            else
            {
                i++;
            }
        }
        if (due is null)
            return;
        foreach (string cmd in due)
            run(cmd);
    }
}
