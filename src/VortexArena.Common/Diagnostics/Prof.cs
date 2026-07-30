using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace XonoticGodot.Common.Diagnostics;

/// <summary>
/// A tiny, allocation-free subsystem timing primitive shared by every layer (Common/Engine/Server AND the Godot
/// game host). It exists so code that can't see the Godot-side <c>FrameProfiler</c> — the simulation loop, the
/// netcode, the physics — can still contribute named timing scopes to the per-frame profile. <c>FrameProfiler</c>
/// is the <em>collector</em>: each frame it flips <see cref="Enabled"/> from the <c>cl_frameprofiler</c> cvar and
/// drains the accumulators via <see cref="SnapshotAndReset"/> for display + hitch attribution.
///
/// <para>Usage at a call site: <c>using (Prof.Sample("sim.move")) { ... }</c>. When <see cref="Enabled"/> is false
/// (a real dedicated server, or the profiler turned off) <see cref="Sample"/> returns a no-op <c>default</c> token
/// that never reads the clock — so the instrumentation is free in production.</para>
///
/// <para><b>Per-thread accumulation (no shared lock).</b> Each thread owns its own <see cref="Accumulator"/>
/// (lazily created, registered once in <see cref="_accumulators"/>). A scope writes only to its own thread's
/// accumulator under that accumulator's private gate — so the common case (scopes + the once-per-frame drain both
/// on the Godot main thread) is uncontended, and the S5 <c>sv_threaded</c> sim worker contends only with the
/// once-per-frame collector drain, never with the main thread. This replaced a single process-global lock whose
/// cross-thread contention was the reason we couldn't afford many fine-grained scopes.</para>
///
/// <para><b>Hierarchy + self-time.</b> Each thread keeps a small scope stack, so every scope records its parent
/// and its <em>self</em> time (inclusive minus the time spent in its direct children). The collector renders this
/// as an indented tree with an automatic "&lt;parent&gt;:other" remainder at each level — turning one opaque
/// <c>proc:other</c> number into a real call tree without path-keying every bucket. A scope reached under two
/// different parents in one frame is attributed to its most-recent parent (a documented coarseness; nesting is
/// stable in practice).</para>
///
/// <para><b>Watchdog phase.</b> The innermost open scope on the MAIN thread is mirrored into the volatile
/// <see cref="MainPhase"/> (one volatile store per push/pop on the main thread, ~1 ns). A background sampling
/// watchdog in the collector polls it during a long frame to attribute stalls in code we never wrapped — the
/// closest practical thing to a sampling profiler in-process.</para>
/// </summary>
public static class Prof
{
    /// <summary>Set by the collector (FrameProfiler) each frame from <c>cl_frameprofiler</c>. False ⇒ scopes are free no-ops.</summary>
    public static bool Enabled;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    // ---- per-thread accumulators ------------------------------------------------------------------------------
    // Each thread writes only to its own Accumulator (guarded by that accumulator's private gate); the collector
    // drains every registered accumulator once per frame. Registration is the only place the shared list lock is
    // taken on the hot path, and only once per thread (first scope it opens).
    internal sealed class Accumulator
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, double> Bucket = new();    // inclusive ms per scope, this frame
        public readonly Dictionary<string, double> Alloc = new();     // bytes allocated per scope, this frame
        public readonly Dictionary<string, double> Children = new();  // summed inclusive ms of DIRECT children (⇒ self = inclusive - children)
        public readonly Dictionary<string, string> Parent = new();    // scope -> representative parent ("" = frame root)
        public readonly Dictionary<string, double> Counters = new();  // per-frame numeric markers (latest wins)
        public readonly Dictionary<string, double> Calls = new();     // number of times the scope opened this frame
        public readonly Dictionary<string, double> Max = new();       // longest single open of the scope this frame (ms)

        // Open-scope stack for this thread (array-based so a parent frame's accumulated child time is mutable in place).
        public StackFrame[] Stack = new StackFrame[64];
        public int Depth;
        public readonly bool IsMain;

        public Accumulator(bool isMain) => IsMain = isMain;
    }

    internal struct StackFrame
    {
        public string Name;
        public long Start;       // Stopwatch ticks at open
        public long StartAlloc;  // thread alloc counter at open
        public double ChildMs;   // accumulated inclusive ms of this frame's direct children
    }

    [ThreadStatic] private static Accumulator? _threadAcc;
    private static readonly List<Accumulator> _accumulators = new();
    private static readonly object _registryGate = new();
    private static int _mainThreadId = -1;

    private static Accumulator ThreadAcc()
    {
        Accumulator? acc = _threadAcc;
        if (acc is not null)
            return acc;
        acc = new Accumulator(Environment.CurrentManagedThreadId == _mainThreadId);
        _threadAcc = acc;
        lock (_registryGate)
            _accumulators.Add(acc);
        return acc;
    }

    /// <summary>
    /// Declare the calling thread the MAIN (frame) thread — the one whose innermost scope feeds the watchdog's
    /// <see cref="MainPhase"/>. Called once from <c>FrameProfiler._Ready</c> (which runs on the Godot main thread).
    /// </summary>
    public static void SetMainThread()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        _threadAcc = null; // re-tag this thread's accumulator as main on next use
    }

    // ---- watchdog phase + frame timing (read by the collector's sampling watchdog) ----------------------------

    /// <summary>The name of the innermost open scope on the main thread (null when between scopes / in un-wrapped
    /// code). A single volatile store per main-thread push/pop; the watchdog samples it during a long frame.</summary>
    public static volatile string? MainPhase;

    /// <summary>The synthetic phase the collector installs when its _Process ends (see
    /// <see cref="MarkCollectorEnd"/>): a watchdog sample landing here means the main thread is in the
    /// POST-process part of the iteration — deferred calls, render submit, present/vsync, next-iteration input +
    /// physics — not in un-wrapped process-phase game code. A named scope opened during that window (a deferred
    /// handler with a Prof.Sample) still overrides it while open; after it pops the phase falls back to
    /// "(unscoped)" — a documented coarseness.</summary>
    public const string PostProcessPhase = "(post-process)";

    private static long _frameStart;   // stamp at the first _Process of the frame
    private static long _frameSeq;     // increments each MarkFrameStart, so the watchdog knows when a new frame began
    private static long _collectorEnd; // stamp at the END of the collector's _Process (main thread only)
    private static double _lastLateMs; // collector-end → next fence: the deferred+present+input/physics gap

    /// <summary>Stopwatch ticks stamped at the start of the current process step (see <see cref="MarkFrameStart"/>).</summary>
    public static long FrameStartTicks => Volatile.Read(ref _frameStart);

    /// <summary>Monotonic frame counter; bumped by <see cref="MarkFrameStart"/> so the watchdog can tell frames apart.</summary>
    public static long FrameSeq => Volatile.Read(ref _frameSeq);

    /// <summary>Convert a Stopwatch-tick span to milliseconds (for the watchdog, which works in raw ticks).</summary>
    public static double SpanMs(long ticks) => ticks * MsPerTick;

    /// <summary>
    /// Stamp the start of the process step. Called by a fence node that runs FIRST each frame (lowest
    /// process priority); paired with <see cref="FrameProcessMs"/> read by the collector that runs LAST, it
    /// measures the wall-clock span across ALL nodes' <c>_Process</c> — a true per-frame total to compare
    /// against the sum of scopes (Godot's TIME_PROCESS monitor is smoothed, so it can't be differenced per frame).
    /// Also closes the previous iteration's post-process window: it computes <see cref="LastLateMs"/> (the
    /// collector-end → here gap = deferred calls + render submit + present + next-iteration input/physics) and
    /// clears the <see cref="PostProcessPhase"/> watchdog marker for the fresh process phase.
    /// </summary>
    public static void MarkFrameStart()
    {
        if (Enabled)
        {
            long now = Stopwatch.GetTimestamp();
            long ce = _collectorEnd;
            _lastLateMs = ce > 0 ? (now - ce) * MsPerTick : 0.0;   // fence + collector share the main thread
            MainPhase = null;
            Volatile.Write(ref _frameStart, now);
            Interlocked.Increment(ref _frameSeq);
        }
    }

    /// <summary>
    /// Stamp the END of the collector's <c>_Process</c> (it runs last) and install the
    /// <see cref="PostProcessPhase"/> watchdog marker. Everything the main thread does between this call and the
    /// next fence — deferred calls, render submit, the present/vsync block — is main-loop time that NO process
    /// scope can see; the marker lets the watchdog attribute it, and the fence turns the gap into
    /// <see cref="LastLateMs"/> so the collector can print it on a hitch line.
    /// </summary>
    public static void MarkCollectorEnd()
    {
        if (Enabled)
        {
            _collectorEnd = Stopwatch.GetTimestamp();
            MainPhase = PostProcessPhase;
        }
    }

    /// <summary>The previous iteration's post-process gap (collector-end → fence) in ms — deferred calls +
    /// render submit + present + the next iteration's input/physics. Written by the fence, read by the collector;
    /// both run on the main thread, so a plain field suffices. 0 until one full iteration has elapsed.</summary>
    public static double LastLateMs => _lastLateMs;

    /// <summary>Per-frame total <c>_Process</c> span in ms (first node's _Process → this call). 0 when disabled.</summary>
    public static double FrameProcessMs() => Enabled ? (Stopwatch.GetTimestamp() - _frameStart) * MsPerTick : 0.0;

    // ---- timing scope ------------------------------------------------------------------------------------------

    /// <summary>A timing scope; accumulates its elapsed time AND bytes-allocated into this thread's buckets on
    /// Dispose, and records its parent + self-time for the call tree. <c>default</c> = no-op. The alloc delta uses
    /// the cheap thread-local <see cref="GC.GetAllocatedBytesForCurrentThread"/> counter, so a scope reports both
    /// "how long" and "how much garbage" — the attribution a GC-stutter hunt needs.</summary>
    public readonly struct ScopeToken : System.IDisposable
    {
        private readonly Accumulator? _acc;   // non-null ⇒ this token opened a real scope (and must close it)
        internal ScopeToken(Accumulator acc) => _acc = acc;

        public void Dispose()
        {
            Accumulator? acc = _acc;
            if (acc is null || acc.Depth == 0)
                return;

            // Pop our frame (LIFO: `using` disposes in reverse open order, so the top IS us).
            ref StackFrame top = ref acc.Stack[acc.Depth - 1];
            string name = top.Name;
            double elapsedMs = (Stopwatch.GetTimestamp() - top.Start) * MsPerTick;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - top.StartAlloc;
            double childMs = top.ChildMs;
            acc.Depth--;

            // Charge our full elapsed to the parent's child-time so the parent's self-time excludes us.
            string parentName = "";
            if (acc.Depth > 0)
            {
                ref StackFrame parent = ref acc.Stack[acc.Depth - 1];
                parent.ChildMs += elapsedMs;
                parentName = parent.Name;
            }

            lock (acc.Gate)
            {
                acc.Bucket.TryGetValue(name, out double cur); acc.Bucket[name] = cur + elapsedMs;
                acc.Alloc.TryGetValue(name, out double a);    acc.Alloc[name] = a + bytes;
                acc.Children.TryGetValue(name, out double ch); acc.Children[name] = ch + childMs;
                acc.Parent[name] = parentName;
                acc.Calls.TryGetValue(name, out double cc);   acc.Calls[name] = cc + 1.0;
                acc.Max.TryGetValue(name, out double mx);     if (elapsedMs > mx) acc.Max[name] = elapsedMs;
            }

            if (acc.IsMain)
                MainPhase = parentName.Length == 0 ? null : parentName;
        }
    }

    /// <summary>Open a named timing scope (no-op + zero cost when <see cref="Enabled"/> is false).</summary>
    public static ScopeToken Sample(string name)
    {
        if (!Enabled)
            return default;

        Accumulator acc = ThreadAcc();
        if (acc.Depth == acc.Stack.Length)
            Array.Resize(ref acc.Stack, acc.Stack.Length * 2);
        acc.Stack[acc.Depth++] = new StackFrame
        {
            Name = name,
            Start = Stopwatch.GetTimestamp(),
            StartAlloc = GC.GetAllocatedBytesForCurrentThread(),
            ChildMs = 0.0,
        };
        if (acc.IsMain)
            MainPhase = name;
        return new ScopeToken(acc);
    }

    /// <summary>
    /// Record a per-frame numeric marker shown in the hitch log (latest value wins, reset each frame) — e.g. the
    /// number of catch-up sim ticks that ran, so a <c>server.tick</c> spike can be read as "ran 3 ticks" vs
    /// "one expensive tick". No-op when disabled.
    /// </summary>
    public static void Mark(string name, double value)
    {
        if (!Enabled)
            return;
        Accumulator acc = ThreadAcc();
        lock (acc.Gate)
            acc.Counters[name] = value;
    }

    // ---- one-shot events (hitch forensics) --------------------------------------------------------------------
    // Markers (above) are per-frame NUMBERS; events are timestamped one-shot STRINGS for the things that
    // correlate with hitches but happen between frames or rarely: "player model built", "input backlog trimmed",
    // "sim backlog dropped", "MultiMesh capacity grew". The collector drains them each frame and stamps them with
    // the frame id, so a hitch dump can say "this 40 ms frame is the one where the model build landed". Bounded
    // ring (drops oldest on overflow), thread-safe (the sim worker + streamer pool threads raise events too).

    private const int MaxPendingEvents = 64;
    private static readonly Queue<string> _events = new();
    private static readonly object _eventGate = new();

    /// <summary>Raise a one-shot forensic event (e.g. <c>"stream: built player model X"</c>). Cheap no-op when
    /// the profiler is off; bounded, so an event storm can't grow memory. Callable from any thread.</summary>
    public static void Event(string label)
    {
        if (!Enabled)
            return;
        lock (_eventGate)
        {
            if (_events.Count >= MaxPendingEvents)
                _events.Dequeue();
            _events.Enqueue(label);
        }
    }

    /// <summary>Collector hook: drain the pending one-shot events into <paramref name="into"/> (appended).</summary>
    public static void DrainEvents(List<string> into)
    {
        if (_events.Count == 0)
            return; // racy read is fine: a just-raised event is picked up next frame
        lock (_eventGate)
        {
            while (_events.Count > 0)
                into.Add(_events.Dequeue());
        }
    }

    // ---- collector drain ---------------------------------------------------------------------------------------

    private static readonly Dictionary<string, double> _childrenMerge = new(); // scratch for self-time computation

    /// <summary>
    /// Collector hook: merge every thread's accumulated scopes + markers into the supplied dictionaries and clear
    /// the internal accumulators for the next frame. Called once per frame by FrameProfiler. Capacity is retained,
    /// so the steady state allocates nothing.
    ///
    /// <para><paramref name="scopesInto"/> = inclusive ms per scope (back-compat: the original 3-arg behaviour is
    /// unchanged). The optional <paramref name="selfInto"/>/<paramref name="parentInto"/> add the hierarchy view:
    /// self ms (inclusive minus direct children) and each scope's representative parent ("" = frame root).</para>
    /// </summary>
    public static void SnapshotAndReset(Dictionary<string, double> scopesInto, Dictionary<string, double> countersInto,
        Dictionary<string, double>? allocInto = null,
        Dictionary<string, double>? selfInto = null, Dictionary<string, string>? parentInto = null,
        Dictionary<string, double>? callsInto = null, Dictionary<string, double>? maxInto = null)
    {
        scopesInto.Clear();
        countersInto.Clear();
        allocInto?.Clear();
        selfInto?.Clear();
        parentInto?.Clear();
        callsInto?.Clear();
        maxInto?.Clear();
        _childrenMerge.Clear();

        // Snapshot the registry under its lock, then drain each accumulator under its OWN gate (so a worker thread
        // mid-scope contends only on its own accumulator, never on the main thread or the registry).
        Accumulator[] accs;
        lock (_registryGate)
            accs = _accumulators.ToArray();

        foreach (Accumulator acc in accs)
        {
            lock (acc.Gate)
            {
                foreach (KeyValuePair<string, double> kv in acc.Bucket)
                {
                    scopesInto.TryGetValue(kv.Key, out double v); scopesInto[kv.Key] = v + kv.Value;
                }
                acc.Bucket.Clear();

                if (allocInto is not null)
                    foreach (KeyValuePair<string, double> kv in acc.Alloc)
                    {
                        allocInto.TryGetValue(kv.Key, out double v); allocInto[kv.Key] = v + kv.Value;
                    }
                acc.Alloc.Clear();

                foreach (KeyValuePair<string, double> kv in acc.Children)
                {
                    _childrenMerge.TryGetValue(kv.Key, out double v); _childrenMerge[kv.Key] = v + kv.Value;
                }
                acc.Children.Clear();

                if (parentInto is not null)
                    foreach (KeyValuePair<string, string> kv in acc.Parent)
                        parentInto[kv.Key] = kv.Value;
                acc.Parent.Clear();

                foreach (KeyValuePair<string, double> kv in acc.Counters)
                    countersInto[kv.Key] = kv.Value;
                acc.Counters.Clear();

                if (callsInto is not null)
                    foreach (KeyValuePair<string, double> kv in acc.Calls)
                    {
                        callsInto.TryGetValue(kv.Key, out double v); callsInto[kv.Key] = v + kv.Value;
                    }
                acc.Calls.Clear();

                if (maxInto is not null)
                    foreach (KeyValuePair<string, double> kv in acc.Max)
                    {
                        maxInto.TryGetValue(kv.Key, out double v); maxInto[kv.Key] = System.Math.Max(v, kv.Value);
                    }
                acc.Max.Clear();
            }
        }

        // Self-time = inclusive - sum(direct children inclusive), clamped (clock jitter can make a parent read a
        // hair under its children). Only computed when the caller asked for the hierarchy view.
        if (selfInto is not null)
            foreach (KeyValuePair<string, double> kv in scopesInto)
            {
                _childrenMerge.TryGetValue(kv.Key, out double ch);
                double self = kv.Value - ch;
                selfInto[kv.Key] = self > 0.0 ? self : 0.0;
            }
    }
}
