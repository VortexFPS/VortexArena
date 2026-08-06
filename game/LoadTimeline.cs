using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;

namespace VortexArena.Game;

/// <summary>
/// Wall-clock accounting for the map-load phases (O5 of the loading-speed plan —
/// planning/interactive-loading-screen-2026-08-04.md §6). Answers one question: of the seconds between
/// "Loading…" appearing and the match being playable, which block owns them?
///
/// <para><b>Why not <c>Prof.Sample</c>.</b> The frame profiler accounts per FRAME, and the load is a handful
/// of enormous frames — a `Prof` scope opened during the load lands in the single load-screen frame's
/// accumulator (the `proc:other 514153 ms` line in the 2026-07-06 capture is exactly that, and it is
/// useless). The hitch detector is equally blind here: it needs a rolling median to spike above, so when
/// every frame is slow it reports "no hitches". Load phases need plain wall clock, which is what this is.
/// It is NOT a per-frame system, so the `Prof.Sample`/`TopLevelNodeScopes` house rule does not apply.</para>
///
/// <para><b>Shape.</b> <see cref="Begin"/> at the top of the load, <c>using (LoadTimeline.Phase("name"))</c>
/// around each block, <see cref="Report"/> at the end. Phases nest (the report indents), and a phase whose
/// work is spread across frames — a GPU warm pass that renders for N frames before its callback — uses
/// <see cref="Open"/>/<see cref="Handle.Close"/> instead of the <c>using</c> form.</para>
///
/// <para><b>Nesting is by call order, not by thread.</b> The load coroutine is main-thread and effectively
/// sequential, which is what makes a single depth counter correct. Recording a phase from a worker thread
/// would mis-indent (though the timing itself stays right, and the list is locked).</para>
/// </summary>
internal static class LoadTimeline
{
    private readonly record struct Entry(int Depth, string Name, double Ms);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static readonly Stopwatch Total = new();
    private static int _depth;
    private static bool _active;
    private static string _label = "";

    /// <summary>Start a new timeline, discarding any previous one. Called once per map load.</summary>
    public static void Begin(string label)
    {
        lock (Gate)
        {
            Entries.Clear();
            _depth = 0;
            _label = label ?? "";
            _active = true;
            Total.Restart();
        }
    }

    /// <summary>Time a synchronous block: <c>using (LoadTimeline.Phase("bsp.parse")) { … }</c>.</summary>
    public static Handle Phase(string name) => Open(name);

    /// <summary>
    /// Open a phase that finishes later — for work spread across frames (a GPU warm pass renders for several
    /// frames before its completion callback). Close the returned handle when the work actually ends. The
    /// <c>using</c> form (<see cref="Phase"/>) is the same thing with a scoped close.
    /// </summary>
    public static Handle Open(string name)
    {
        lock (Gate)
        {
            if (!_active)
                return new Handle(null, 0, 0);
            int depth = _depth++;
            return new Handle(name, depth, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Record a phase whose duration the caller measured itself.</summary>
    public static void Mark(string name, double ms)
    {
        lock (Gate)
        {
            if (_active)
                Entries.Add(new Entry(_depth, name, ms));
        }
    }

    /// <summary>
    /// Print the table and stop accounting. Idempotent — a second call is ignored, so a teardown path that
    /// also reports cannot double-print.
    /// </summary>
    public static void Report()
    {
        List<Entry> snapshot;
        double totalMs;
        lock (Gate)
        {
            if (!_active)
                return;
            _active = false;
            Total.Stop();
            totalMs = Total.Elapsed.TotalMilliseconds;
            snapshot = new List<Entry>(Entries);
        }

        if (snapshot.Count == 0)
        {
            GD.Print($"[LoadTimeline] {_label}: {totalMs:0} ms total (no phases recorded).");
            return;
        }

        // Width the name column to the deepest/longest entry so the ms column lines up and the table can be
        // eyeballed for the long pole without arithmetic.
        int width = 0;
        foreach (Entry e in snapshot)
            width = Math.Max(width, (e.Depth * 2) + e.Name.Length);

        var sb = new System.Text.StringBuilder();
        sb.Append("[LoadTimeline] ").Append(_label).Append(" — load begin → ready, ")
          .Append(totalMs.ToString("0", CultureInfo.InvariantCulture)).AppendLine(" ms total");
        // Texture compression is deliberately NOT a phase: it runs on the streamer's worker threads,
        // interleaved through several phases, so wrapping it in a scope would double-count. It gets its own
        // line instead, because leaving it unnamed is how ~100 s hid inside phases named after other work.
        sb.Append("    ").AppendLine(Loaders.AssetSystem.CompressionTimeReport());
        foreach (Entry e in snapshot)
        {
            string indented = new string(' ', e.Depth * 2) + e.Name;
            double pct = totalMs > 0.01 ? (e.Ms / totalMs) * 100.0 : 0.0;
            sb.Append("  ").Append(indented.PadRight(width))
              .Append("  ").Append(e.Ms.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(9)).Append(" ms")
              .Append("  ").Append(pct.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5)).AppendLine(" %");
        }
        // Depth-0 entries are the top-level blocks; whatever they do not account for is yield/present time
        // between them plus anything unmeasured, and naming it keeps the table honest.
        double top = 0;
        foreach (Entry e in snapshot)
            if (e.Depth == 0)
                top += e.Ms;
        sb.Append("  ").Append("(unaccounted: yields, present, unmeasured)".PadRight(width))
          .Append("  ").Append((totalMs - top).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(9)).Append(" ms");
        GD.Print(sb.ToString());
    }

    /// <summary>A phase in flight. Disposing (or <see cref="Close"/>) records its elapsed time.</summary>
    internal readonly struct Handle : IDisposable
    {
        private readonly string? _name;
        private readonly int _level;   // NOT _depth: that would shadow the outer static this restores
        private readonly long _start;

        internal Handle(string? name, int level, long start)
        {
            _name = name;
            _level = level;
            _start = start;
        }

        public void Close()
        {
            if (_name is null)
                return;
            double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
            lock (Gate)
            {
                if (_active)
                {
                    Entries.Add(new Entry(_level, _name, ms));
                    // Restore depth to this handle's own level rather than decrementing: an inner phase whose
                    // Dispose was skipped (an early return, an exception) then cannot leave the rest of the
                    // table permanently indented.
                    _depth = _level;
                    return;
                }
            }

            // Closed AFTER the table printed. Cross-frame phases do this routinely — a GpuWarmPass renders
            // for several frames and its callback lands well after _Ready returns — and silently dropping
            // them is how `precache.models.pso-warm` went missing from the first windowed capture entirely
            // while its weapon twin reported. A late phase gets its own line rather than no line.
            GD.Print($"[LoadTimeline] (late) {_name}: {ms.ToString("0.0", CultureInfo.InvariantCulture)} ms " +
                     "— finished after the table printed; not included in the total above.");
        }

        public void Dispose() => Close();
    }
}
