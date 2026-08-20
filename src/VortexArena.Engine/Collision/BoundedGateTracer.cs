using System.Numerics;
using System.Threading;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;

namespace VortexArena.Engine.Collision;

/// <summary>
/// The MAIN-thread trace facade for a THREADED listen host (zero-hitch 2026-08-03, the cn.predict residual).
///
/// <para><b>The problem.</b> With <c>sv_threaded 1</c> the worker holds the sim gate around each WHOLE tick,
/// and every main-thread trace (client prediction above all — <c>Movement.Move</c> resolves the ambient
/// <c>Api.Trace</c>) serialises against it per-trace. The p99 wait is small, but a heavy combat tick holds
/// the gate for 10-25 ms — and ONE prediction trace landing mid-tick eats the remainder as a frame hitch
/// (the measured cn.predict 15-20 ms class that survived the incremental-replay fix, plus the hud.trueaim
/// singles: same gate, different caller).</para>
///
/// <para><b>The fix.</b> Bound the wait: try the LIVE gated tracer for at most <see cref="TimeoutMs"/>; under
/// contention fall back to a gate-free STATIC-WORLD twin (same immutable <see cref="CollisionWorld"/>, no
/// entity provider, no gate — the FaithfulParticleBackend tracer's construction). The fallback is not an
/// approximation invented here: DarkPlaces' own client prediction traces the client-known world WITHOUT
/// live-entity access (<c>CL_ClientMovement</c>: hitnetworkplayers=false), so a contended frame simply
/// degrades to DP-classic prediction — a mover/player mispredict for one trace, corrected by the next
/// snapshot's reconcile — instead of blocking the render thread mid-combat.</para>
///
/// <para>Reentrancy-safe: <see cref="Monitor.TryEnter(object, int)"/> succeeds immediately when the calling
/// thread already owns the gate (main-thread code that mutates the world under <c>lock(_simGate)</c> and
/// traces inside keeps its exact old behaviour). Unthreaded hosts never install this facade at all.</para>
/// </summary>
public sealed class BoundedGateTracer : ITraceService
{
    private readonly ITraceService _live;      // the server world's tracer (ConcurrencyGate installed)
    private readonly TraceService _staticOnly; // gate-free twin over the immutable static world
    private readonly object _gate;             // the sim gate the worker holds around each tick

    /// <summary>Max ms ONE trace may wait on the sim gate before degrading to the static world.
    /// 2 ms ≈ the measured p99 uncontended wait; a combat tick's 10-25 ms hold is what this refuses to pay.</summary>
    public int TimeoutMs { get; set; } = 2;

    /// <summary>Max CUMULATIVE ms of gate waiting per frame across every trace. The per-trace timeout alone
    /// was not enough: a slide-move runs 4-8 traces, and each waiting just UNDER the timeout added back a
    /// 10-16 ms frame with ZERO fallbacks recorded (the first live capture's shape — cn.predict persisted
    /// while cn.tracefb stayed ~0). Once the frame's budget is spent, every further trace skips the wait
    /// entirely (an instant TryEnter still serves the free-gate and reentrant-owner cases for free) until
    /// <see cref="ResetFrame"/>. Main-thread only.</summary>
    public static double FrameWaitBudgetMs { get; set; } = 2.0;

    /// <summary>Traces that fell back to the static world since the last <see cref="ResetFrame"/> (surfaced
    /// as the <c>cn.tracefb</c> mark beside <c>sv.gatewait_ms</c>). Main-thread only.</summary>
    public static int FallbacksSinceRead;

    /// <summary>Traces allowed to WAIT on the gate since the last <see cref="ResetFrame"/> — calls that reached
    /// <see cref="Monitor.TryEnter"/> with a non-zero timeout because the frame's budget was still unspent.
    ///
    /// <para>This is the budget working, stated as a count rather than as elapsed time: on a contended frame it
    /// should be 1, because the first trace spends the whole budget and every trace after it is refused a wait.
    /// A number that climbs with the trace count is the exact regression <see cref="FrameWaitBudgetMs"/> was
    /// added to prevent — 4-8 traces each waiting just under <see cref="TimeoutMs"/>, rebuilding the 10-16 ms
    /// frame with no fallbacks recorded. Surfaced beside <c>cn.tracefb</c> as <c>cn.tracewait</c>.</para>
    ///
    /// <para>Counting the waits rather than timing them is also what makes this testable: how long a
    /// <see cref="Monitor.TryEnter"/> actually blocks depends on the machine, but how many were permitted
    /// depends only on the budget.</para></summary>
    public static int BudgetedWaitsSinceReset;

    // Cumulative gate-wait this frame (Stopwatch ticks). Static: one threaded host per process, main-thread
    // writers only — the same single-writer contract FallbacksSinceRead already carries.
    private static long _waitTicksThisFrame;

    /// <summary>Per-frame reset (the host calls this where it reads the counters): re-arms the wait budget.</summary>
    public static void ResetFrame()
    {
        _waitTicksThisFrame = 0;
        BudgetedWaitsSinceReset = 0;
    }

    private bool BudgetBlown
        => _waitTicksThisFrame * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= FrameWaitBudgetMs;

    public BoundedGateTracer(ITraceService live, CollisionWorld staticWorld, object gate)
    {
        _live = live;
        _staticOnly = new TraceService(staticWorld);   // entities=null, gate=null — the particle-tracer shape
        _gate = gate;
    }

    public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
    {
        if (TryGate())
        {
            try { return _live.Trace(start, mins, maxs, end, filter, ignore); }
            finally { Monitor.Exit(_gate); }
        }
        FallbacksSinceRead++;
        return _staticOnly.Trace(start, mins, maxs, end, filter, ignore);
    }

    public int PointContents(Vector3 point)
    {
        if (TryGate())
        {
            try { return _live.PointContents(point); }
            finally { Monitor.Exit(_gate); }
        }
        FallbacksSinceRead++;
        return _staticOnly.PointContents(point);
    }

    /// <summary>Try to acquire the sim gate under the per-trace timeout AND the per-frame wait budget.
    /// Charges the actual wall time waited (success or not) against the frame budget; once blown, later
    /// calls use a zero timeout — free-gate and reentrant-owner acquisitions still succeed instantly.</summary>
    private bool TryGate()
    {
        int wait = BudgetBlown ? 0 : TimeoutMs;
        if (wait > 0)
            BudgetedWaitsSinceReset++;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool got = Monitor.TryEnter(_gate, wait);
        _waitTicksThisFrame += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return got;
    }

    // PVS is compiled static map data — no live-entity access, no gate needed either way.
    public bool CheckPvs(Vector3 viewpoint, Vector3 target) => _live.CheckPvs(viewpoint, target);
}

/// <summary>
/// The main thread's per-thread ambient for a threaded listen host: every service delegates to the server
/// world's, except <see cref="Trace"/> which is the <see cref="BoundedGateTracer"/>. Installed with
/// <see cref="Api.SetThreadServices"/> on the MAIN thread only (the worker installs the raw server services
/// on its own thread; the process-wide ambient is untouched, so streamer workers and tests see exactly what
/// they always did).
/// </summary>
public sealed class MainThreadPredictionServices : IEngineServices
{
    private readonly IEngineServices _server;
    private readonly BoundedGateTracer _tracer;

    public MainThreadPredictionServices(IEngineServices server, CollisionWorld staticWorld, object gate)
    {
        _server = server;
        _tracer = new BoundedGateTracer(server.Trace, staticWorld, gate);
    }

    public ITraceService Trace => _tracer;
    public IEntityService Entities => _server.Entities;
    public ICvarService Cvars => _server.Cvars;
    public ISoundService Sound => _server.Sound;
    public IModelService Models => _server.Models;
    public IGameClock Clock => _server.Clock;
    public ISurfaceService Surfaces => _server.Surfaces;
}
