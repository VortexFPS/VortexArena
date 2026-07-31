using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using VortexArena.Common.Diagnostics;

namespace VortexArena.Game.Client;

/// <summary>
/// Background asset streaming pipeline (planning/PERFORMANCE_REPORT.md §5 S1). Turns a synchronous cold asset load (a
/// 30–300 ms main-thread stall to read + parse + decode + GPU-upload) into a two-phase async job: the pure-C#
/// OFF-THREAD phase (VFS read + format parse + image decode — none of it touches Godot's RenderingServer) runs
/// on a small dedicated worker lane (see the lane note below), and the MAIN-THREAD phase (turning the parsed
/// data into a <c>Mesh</c>/<c>Material</c>/<c>Texture</c> + attaching the node) is drained from a priority queue
/// in <see cref="_Process"/> under a small millisecond budget — so even a burst of requests never spikes a frame.
///
/// <para>Requests carry a <see cref="Priority"/> (a viewmodel swap the player is waiting on = High, a distant
/// player's model = Low); the main-thread drain always builds the highest-priority ready job first. The off-
/// thread phase produces a value of type <c>T</c>; <c>onMain</c> consumes it on the main thread. A null off-
/// thread result (a missing/failed asset) is dropped silently.</para>
///
/// <para>This is the general mechanism; the felt cold-load cases are already covered eagerly (A3 precaches all
/// weapons/combat-sounds/default models, S3 idle-warms the rest), so today it is wired for the idle player-model
/// warm — moving the IQM parse off the main thread. Live cold-load callers (a renderer swapping a placeholder
/// for the real model 1–3 frames later) can adopt it incrementally.</para>
/// </summary>
public partial class BackgroundAssetStreamer : Node
{
    public enum Priority { High = 0, Low = 1 }

    /// <summary>Main-thread build budget per frame (ms). One ready job always runs even if it overshoots, so a
    /// single heavy build can't deadlock the queue; the loop then stops until next frame — and the overshoot is
    /// then PAID BACK by sitting out the following frames (see <see cref="_debtMs"/>), so the budget bounds the
    /// AVERAGE cost per frame even though a single job is indivisible.</summary>
    [Export] public double BudgetMs { get; set; } = 2.0;

    /// <summary>
    /// Unpaid overshoot from previous frames, in ms. "Run at least one job per frame" is what keeps an
    /// indivisible build from deadlocking the queue, but on its own it also makes the budget a fiction: when
    /// every ready job costs far more than <see cref="BudgetMs"/>, the drain runs one anyway, EVERY frame, and
    /// a queue of heavy builds becomes a solid wall of long frames rather than a spread of short ones. (That is
    /// exactly how the menu asset warm froze the main menu at ~1 fps for its first seconds: a 2 ms budget
    /// against ~900 ms skeletal builds.) So an overshoot is now recorded here and worked off by skipping whole
    /// frames — the queue still drains, one job at a time, at an amortised cost of <see cref="BudgetMs"/> per
    /// frame. Capped at <see cref="MaxDebtFrames"/>×budget so one pathological job can't stall the queue for
    /// minutes.
    /// </summary>
    private double _debtMs;

    /// <summary>Ceiling on accumulated debt, expressed in whole skipped frames — an outlier build pays back for
    /// at most this many frames before the drain resumes. The cap is the latency side of the trade: pacing
    /// delays a queued asset by at most this many frames (a live High-priority player model included), which is
    /// the price of not spiking the frames in between. Deliberately modest for that reason.</summary>
    private const int MaxDebtFrames = 60;

    private readonly object _lock = new();
    private readonly List<(Priority Prio, long Seq, Action Build, string? Label)> _ready = new(); // off-thread done, awaiting main build
    private long _seq;
    private int _inFlight; // off-thread phases still running (diagnostics)

    /// <summary>
    /// Queue an asset for streaming: run <paramref name="offThread"/> on the worker lane, then hand its result to
    /// <paramref name="onMain"/> on the main thread within the per-frame budget. <paramref name="offThread"/> must
    /// be pure C# (no Godot RenderingServer/scene-tree calls); a null result is dropped.
    /// <paramref name="label"/> (optional) names the asset in the profiler's forensic event stream, so a hitch
    /// frame can be read as "this is the frame the model build landed on".
    /// </summary>
    public void Request<T>(Func<T?> offThread, Action<T> onMain, Priority priority = Priority.Low,
        string? label = null) where T : class
    {
        Interlocked.Increment(ref _inFlight);
        long seq = Interlocked.Increment(ref _seq);
        PostWork(priority, seq, () =>
        {
            T? result = null;
            try { result = offThread(); }
            catch (Exception ex) { GD.PrintErr($"[Streamer] off-thread phase failed: {ex.Message}"); }

            if (result is not null)
            {
                lock (_lock)
                    _ready.Add((priority, seq, () => onMain(result), label));
            }
            Interlocked.Decrement(ref _inFlight);
        });
    }

    public override void _Process(double delta)
    {
        // Work off any overshoot from earlier frames before building anything new, so a heavy job costs one
        // long frame and then a run of clean ones instead of a wall of long ones. Done before the ready check
        // so idle frames repay the debt too — the overshoot is wall-clock time that has already elapsed.
        if (_debtMs > 0.0)
        {
            _debtMs -= BudgetMs;
            return;
        }

        // Fast path: nothing ready and nothing in flight → idle.
        lock (_lock)
        {
            if (_ready.Count == 0)
                return;
        }

        using var _scope = FrameProfiler.Scope("stream.build");   // attribute the drain instead of proc:other
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Action? build = null;
            string? label = null;
            lock (_lock)
            {
                if (_ready.Count > 0)
                {
                    // Highest priority (lowest enum), then FIFO by sequence. The ready list is short, so a linear
                    // scan is cheaper than maintaining a heap.
                    int best = 0;
                    for (int i = 1; i < _ready.Count; i++)
                    {
                        var a = _ready[i];
                        var b = _ready[best];
                        if (a.Prio < b.Prio || (a.Prio == b.Prio && a.Seq < b.Seq))
                            best = i;
                    }
                    build = _ready[best].Build;
                    label = _ready[best].Label;
                    _ready.RemoveAt(best);
                }
            }
            if (build is null)
                break;
            double t0 = sw.Elapsed.TotalMilliseconds;
            try { build(); }
            catch (Exception ex) { GD.PrintErr($"[Streamer] main-thread build failed: {ex.Message}"); }
            if (label is not null)
                Prof.Event($"stream: {label} built ({sw.Elapsed.TotalMilliseconds - t0:0.0}ms)");
            if (sw.Elapsed.TotalMilliseconds >= BudgetMs)
            {
                // Budget spent. Bank whatever we ran over by so the next frames sit it out; without this the
                // "always run one" rule silently converts the budget into "one heavy build EVERY frame".
                _debtMs = Math.Min(sw.Elapsed.TotalMilliseconds - BudgetMs, MaxDebtFrames * BudgetMs);
                break; // finish the rest next frame
            }
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Bounded off-thread worker lane (perf 2026-07-03). Request used to fan every job out via raw
    //  Task.Run, so a model/texture wave (one job per texture × a roster of models at load/bot-join)
    //  landed on a dozen+ DISTINCT thread-pool threads on a many-core box. Every [ThreadStatic] read/
    //  decode scratch (AssetSystem/AssetLoader file buffers, and RgbaDecodeBuffer before it went shared)
    //  grew one copy PER THREAD and never converged — the 100-230 MB single-frame alloc storms at
    //  load/join — and the herd competed with the main thread for cores. A small FIXED set of dedicated
    //  workers pins thread identity (per-thread scratch converges and stays warm), caps concurrent
    //  buffer demand, and honours Priority for the OFF-thread phase too (a live player-model job
    //  overtakes queued idle-warm work; Task.Run ran them all at once). The lane is process-wide and
    //  outlives the per-session streamer NODES — like the thread pool it replaces; idle workers park in
    //  Monitor.Wait and, being background threads, never block process exit.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Worker-lane width: enough for a load-time texture wave to overlap the main thread's build
    /// work without herding a many-core pool (see the lane note above).</summary>
    public static int WorkerCount { get; } = Math.Clamp(System.Environment.ProcessorCount / 4, 2, 4);

    private static readonly object WorkGate = new();
    private static readonly List<(Priority Prio, long Seq, Action Work)> WorkQueue = new();
    private static Thread[]? _workers;

    private static void PostWork(Priority priority, long seq, Action work)
    {
        lock (WorkGate)
        {
            if (_workers is null)
            {
                _workers = new Thread[WorkerCount];
                for (int i = 0; i < _workers.Length; i++)
                {
                    _workers[i] = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Name = $"AssetStreamer-{i}",
                        // BelowNormal: this lane is PREFETCH. Nothing on it is worth a frame — a late asset
                        // shows a placeholder a moment longer, a late frame is a visible stutter. The client
                        // process runs AboveNormal (sys_priority_boost), so leaving the workers at Normal put
                        // them two bands under the game only by luck of the process boost; saying it explicitly
                        // means the OS preempts THEM rather than the render loop whenever cores are contended.
                        Priority = ThreadPriority.BelowNormal,
                    };
                    _workers[i].Start();
                }
            }
            WorkQueue.Add((priority, seq, work));
            Monitor.Pulse(WorkGate);
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Action work;
            lock (WorkGate)
            {
                while (WorkQueue.Count == 0)
                    Monitor.Wait(WorkGate);
                // Highest priority (lowest enum), then FIFO by sequence — the same order as the main-thread
                // drain. The queue peaks at a few hundred texture jobs during a warm wave; linear scan.
                int best = 0;
                for (int i = 1; i < WorkQueue.Count; i++)
                {
                    var a = WorkQueue[i];
                    var b = WorkQueue[best];
                    if (a.Prio < b.Prio || (a.Prio == b.Prio && a.Seq < b.Seq))
                        best = i;
                }
                work = WorkQueue[best].Work;
                WorkQueue.RemoveAt(best);
            }
            // The posted body handles its own exceptions (Request wraps offThread); this guard only keeps a
            // worker alive if the bookkeeping itself ever throws.
            try { work(); }
            catch (Exception ex) { GD.PrintErr($"[Streamer] worker job failed: {ex.Message}"); }
        }
    }
}
