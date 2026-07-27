using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Client;

/// <summary>
/// An always-available, low-overhead frame-time + GC hitch monitor — the instrument the per-second
/// <see cref="Hud.FpsPanel"/> can't be. <c>FpsPanel</c> averages over a 1-second window, which HIDES the very
/// thing that matters for smoothness: a single long frame. This node samples the real per-frame delta, the
/// managed-heap GC activity (gen-0/1/2 collection deltas + bytes allocated <em>this frame</em>), and a handful
/// of coarse subsystem timers, then flags any frame that spikes above the rolling baseline as a "hitch",
/// <b>classifies</b> what dominated it (GC pause / pipeline compile / asset build / CPU logic / GPU / present /
/// external), and logs the attribution. That turns "it stuttered" into "it stuttered because gen-0 collected
/// after 0.4 MB of allocation during server.tick".
///
/// <para><b>How it measures.</b> A single <see cref="_Process"/> per frame diffs <see cref="GC.CollectionCount"/>
/// and <see cref="GC.GetTotalAllocatedBytes"/> against the previous frame, so the window covers exactly one
/// frame's worth of allocation regardless of node order. Subsystem timing uses the allocation-free
/// <see cref="Scope"/> struct: <c>using (FrameProfiler.Scope("server.tick")) ...</c> accumulates elapsed
/// <see cref="Stopwatch"/> ticks into a per-thread bucket (now also tracking parent + self-time, so the flat
/// list renders as a call tree) that this node snapshots + clears each frame. When the profiler is off the scope
/// is a no-op, so the instrumentation costs nothing in a release build with the cvar at 0.</para>
///
/// <para><b>Recording.</b> Whenever the profiler is active it writes a per-launch session log + parallel CSV
/// under <c>&lt;userdir&gt;/logs/</c> via a background thread (<see cref="SessionProfileLog"/>) — the game thread
/// only enqueues, never touches the disk, so recording can't itself cause a hitch.</para>
///
/// <para><b>Cvars</b> (all <see cref="CvarFlags.Save"/> except the transient triggers):
/// <list type="bullet">
///   <item><c>cl_frameprofiler</c> — 0 off, 1 on-screen graph + stats + hitch log + recording, 2 also emits the
///         periodic snapshot to the console. Defaults <b>on (1) in debug builds</b> unless the player set it.</item>
///   <item><c>cl_frameprofiler_hitchms</c> — absolute floor (ms) below which a frame is never a hitch (default 12);
///         a hitch must also exceed <see cref="HitchFactor"/>× the rolling median.</item>
///   <item><c>cl_frameprofiler_watchdog</c> — 1 (default) runs the sampling watchdog thread that attributes
///         stalls inside un-scoped code; 0 disables it.</item>
///   <item><c>cl_frameprofiler_dump</c> — transient: writes the forensic ring to <c>frameprofile_ring.csv</c>.</item>
/// </list></para>
///
/// <para><b>Hotkey.</b> <c>F11</c> (the one unbound function key) toggles the expanded overlay — top live scopes
/// with rolling baselines + the call tree. Handled at the <c>_Input</c> stage with <c>ProcessMode.Always</c>, so
/// it works in the menu, a paused match, anywhere.</para>
///
/// <para>Instantiated once from <c>Main._Ready</c> so it spans the whole session (menu + match), above every
/// other layer. The graph sits unobtrusively in the top-left.</para>
/// </summary>
public partial class FrameProfiler : CanvasLayer
{
    public static FrameProfiler? Instance { get; private set; }

    /// <summary>A frame is only a "hitch" if it exceeds this multiple of the rolling median (and the ms floor).</summary>
    private const double HitchFactor = 1.8;

    /// <summary>Frame-time ring length — a few seconds of history for the graph + the rolling median/p99.</summary>
    private const int RingSize = 240;

    /// <summary>Cadence of the periodic steady-state snapshot (percentiles + hitch breakdown).</summary>
    private const double SnapshotIntervalMs = 5000.0;

    // ---- subsystem scope timing -----------------------------------------------------------------------------
    private static readonly Dictionary<string, double> _lastScopes = new();    // inclusive ms per scope
    private static readonly Dictionary<string, double> _lastCounters = new();  // per-frame numeric markers
    private static readonly Dictionary<string, double> _lastSelf = new();      // self ms (inclusive - children)
    private static readonly Dictionary<string, string> _lastParent = new();    // scope -> representative parent
    private static readonly Dictionary<string, double> _lastCalls = new();     // open count per scope this frame
    private static readonly Dictionary<string, double> _lastMax = new();       // longest single open per scope (ms)

    // Whole-node _Process scopes (distinct nodes ⇒ non-overlapping); summed to compute "proc:other". Nested
    // sub-scopes (server.tick, sim.*, net.*, cw.*) are intentionally NOT here so they don't double-count.
    // The §18 effect nodes (music/weather/decals/vehicles/damagetext) are included so they leave proc:other.
    private static readonly string[] TopLevelNodeScopes =
        { "ng.process", "cw.process", "md3.morph", "entitynode", "hud.mgr", "proj", "viewmodel", "nethud",
          "stream.build", "particles.cpu", "music", "weather", "decals.splat", "vehicle.vis", "damagetext",
          // (perf-investigation 2026-06-14) the previously-unscoped client _Process nodes that leaked into
          // proc:other on big/populated maps — now attributed so the residual is provably Godot-internal.
          "cev.process", "world.pvscull", "emitters", "clientmisc", "hud.trueaim",
          // (P4 2026-07-03) the warpzone portal pass — the _Process side of the per-portal SubViewport drive
          // (the actual exit-view render lands in rcpu/gpu, but the gate/frustum/resize work is process CPU).
          "portal.render",
          // (M2/M3) the newer client render nodes: nade-orb followers + dynamic-light spawns — scoped so their
          // per-frame CPU is attributed instead of leaking into proc:other.
          "nadeorbs", "dynlights",
          // the messagemode chat prompt's caret-blink _Process (only ticks while the prompt is open — it disables
          // its own _Process when closed — but scoped so the open-window cost is attributed, not proc:other).
          "chat" };

    /// <summary>
    /// Open a named timing scope: <c>using (FrameProfiler.Scope("name")) { ... }</c> (or as a one-statement
    /// prefix). Forwards to <see cref="Prof.Sample"/> — a no-op <c>default</c> token when the profiler is off, so
    /// wrapping a hot path is free when disabled. Engine/Server code calls <see cref="Prof.Sample"/> directly.
    /// </summary>
    public static Prof.ScopeToken Scope(string name) => Prof.Sample(name);

    // ---- frame-time ring + GC tracking -----------------------------------------------------------------------
    private readonly double[] _ring = new double[RingSize];
    private readonly double[] _sortScratch = new double[RingSize];
    private int _ringHead;
    private int _ringCount;

    private int _lastGc0, _lastGc1, _lastGc2;
    private long _lastAllocBytes;
    private int _dGc0, _dGc1, _dGc2;   // collections that happened THIS frame
    private long _dAllocBytes;          // bytes allocated THIS frame
    private double _median, _p99;
    private double _snapshotClock;      // accumulates ms for the periodic snapshot
    private bool _defaultsRegistered;

    private readonly Stopwatch _sessionSw = Stopwatch.StartNew();
    private double SessionSeconds => _sessionSw.Elapsed.TotalSeconds;

    // ---- forensic frame records (the hitch-attribution ring) --------------------------------------------------
    private readonly record struct ScopeRow(string Name, double Ms, double Self, double Bytes, double Calls, double MaxMs);

    private sealed class FrameRecord
    {
        public ulong FrameId;
        public double Ms, ProcMs, RcpuMs, GpuMs, PhysMs;
        public long AllocBytes;
        public int G0, G1, G2;
        public double GcPauseMs;
        public double DrawCalls;
        public long PipeCompiles;          // total of the five RenderingInfo pipeline-compilation counters
        public long PipeCompilesUber;      // the SYNCHRONOUS render-thread stalls = Surface + Draw (the hitch culprits)
        // (engine-perf 2026-06-15) The five Godot pipeline-compile counters, tracked SEPARATELY. Per the Godot docs:
        //   Surface = first-instancing of a (mesh,material,render-feature) variant — a SYNCHRONOUS render-thread stall.
        //   Draw    = a precompile MISS at draw time — should be ~0 with the ubershader system (nonzero ⇒ upstream bug).
        //   Mesh / Specialization = load-time / async (benign). Canvas = 2D first-draw.
        public long PipeSurface, PipeDraw, PipeMesh, PipeSpec, PipeCanvas;
        // (P2 2026-07-03) Filled at FINALIZE time, one collector pass after the scopes: the iteration's
        // post-process gap (deferred+present, measured by the fence) and the Prof.FrameSeq whose watchdog
        // window (fence→fence) covers this iteration.
        public double LateMs;
        public long WdSeq;
        public readonly List<ScopeRow> Scopes = new(32);
        public readonly List<string> Events = new(2);

        public void Reset(ulong frameId)
        {
            FrameId = frameId;
            Scopes.Clear();
            Events.Clear();
        }
    }

    private readonly FrameRecord[] _records = new FrameRecord[RingSize];
    private ulong _frameId;
    private readonly Dictionary<string, double> _allocScratch = new();
    private readonly List<string> _eventScratch = new(8);
    private long _lastPipeTotal, _lastPipeUber;
    private long _lastPipeCanvas, _lastPipeMesh, _lastPipeSurface, _lastPipeDraw, _lastPipeSpec;
    // (engine-perf 2026-06-16) Budget of RenderDoc auto-captures to trigger on SURFACE compiles (no-op unless
    // running under RenderDoc). A handful is enough to name the residual; capped so a compile storm can't spam.
    private int _rdocCapturesLeft = 6;
    private double _lastGcPauseMs;
    private double _lastForensicDumpAt = -1000.0;
    private double _forensicClock;

    private double _procMs, _physMs, _renderCpuMs, _renderGpuMs;
    private Rid _viewportRid;
    private bool _measureArmed;

    // ---- rolling alloc rate + heap (10) ----------------------------------------------------------------------
    private double _allocRateMbPerSec;   // managed garbage over the ring window, MB/s
    private long _heapBytes;             // GC.GetTotalMemory(false)

    // ---- per-scope rolling baseline (9) ----------------------------------------------------------------------
    private readonly Dictionary<string, double> _scopeEwma = new();   // EWMA of inclusive ms per scope

    // ---- classification + collapse (5,6) ---------------------------------------------------------------------
    private enum HitchClass { Unknown, CpuLogic, GpuBound, VsyncPresent, GcPause, PipelineCompile, AssetBuild, External }

    private HitchClass _runClass = HitchClass.Unknown;
    private int _runCount;
    private double _runMinMs, _runMaxMs, _runStartS, _runLastS, _runLastInterimS;
    private string _runReason = "";
    private bool _runRecovery;

    private long _sessionHitchCount;
    // (P8) The class arrays count PRIMARIES only; recovery tails (a VSYNC/PRESENT hitch within 1 s of a primary
    // = the present queue draining that primary's backlog, hitch-resolution-2026-06-14 §1) are counted apart so
    // the census reads "N primaries" instead of drowning in the tail (102 of 179 in the 2026-07-02 session).
    private readonly int[] _windowHitchByClass = new int[8];
    private readonly int[] _sessionHitchByClass = new int[8];
    private int _windowRecovery, _sessionRecovery;
    private double _lastPrimaryHitchAtS = -1000.0;

    // (P11) Scope-coverage debt: hitches attributed to unnamed code — each one is an "add a Prof scope" TODO,
    // surfaced in the session summary so the debt is visible instead of re-discovered every hunt.
    private int _covProcOther, _covUnscoped;

    private string _lastHitchSummary = "";
    private double _lastHitchAtS = -1000.0;

    // (P7) Opt-in on-screen hitch flash (cl_frameprofiler_alert): a short loud marker when a hitch fires, so a
    // felt stutter is nameable without watching the console.
    private double _alertUntilS = -1.0;
    private string _alertText = "";

    // (P5) DEBUG-build watermark: debug frame times/censuses are NOT release-representative (a debug session on
    // 2026-07-02 showed 255 ms iqm.anims and 433 MB single-frame allocs that release builds don't have).
    private bool _debugBuild;
    private string _buildTag = "";

    // ---- (P2 2026-07-03) one-frame-delayed record pipeline -----------------------------------------------------
    // Godot's `delta` measures the PREVIOUS main-loop iteration, while the scopes drained by this collector
    // describe THIS frame's process phase. A record is therefore held PENDING for one collector pass and
    // finalized when its wall time (the next delta) + its draw-side counters arrive — so hitch classification
    // sees internally-consistent evidence. Before this, an isolated CPU spike always classified as EXTERNAL one
    // frame late with quiet evidence and a stale watchdog suffix (verified: session-20260702-225602.csv rows
    // t=61.096 [scopes ng.process=119.43, ms=6.94] / t=61.105 [ms=119.72, proc=5.24 → "EXTERNAL"]).
    private FrameRecord? _pending;

    // ---- session summary (12) --------------------------------------------------------------------------------
    private readonly FrameHistogram _sessionHist = new();
    private readonly FrameHistogram _windowHist = new();
    // (perf-investigation) per-scope inclusive-ms summed over the snapshot window → a steady-state top-scope
    // breakdown printed each snapshot, so every experiment yields comparable per-frame averages without a hitch.
    private readonly Dictionary<string, double> _windowScopeMs = new();
    private int _initGc0, _initGc1, _initGc2;
    private double _initGcPauseMs;
    private long _sessionAllocTotal;
    private long _peakAllocFrame;
    private double _peakAllocAtS;
    private readonly List<(double Ms, HitchClass Cls, string Scope, double TimeS)> _worst = new(12);
    private bool _summaryEmitted;

    // ---- recording (14) --------------------------------------------------------------------------------------
    private readonly SessionProfileLog _sessionLog = new();
    private DateTime _launchWall;

    // ---- sampling watchdog (17) ------------------------------------------------------------------------------
    private PhaseWatchdog? _watchdog;

    private FrameProfilerGraph _view = null!;
    private bool _expanded;   // F11 toggle: expanded overlay (top scopes + tree)

    public override void _Ready()
    {
        Instance = this;
        Layer = 10;                    // a dev overlay above the HUD
        ProcessPriority = 1_000_000;   // run late so most scopes have closed before we snapshot the bucket
        ProcessMode = ProcessModeEnum.Always; // the F11 hotkey + recording keep working while the tree is paused

        Prof.SetMainThread();          // tag this (Godot main) thread for the watchdog's MainPhase
        TryRegisterDefaults();

        _lastGc0 = _initGc0 = GC.CollectionCount(0);
        _lastGc1 = _initGc1 = GC.CollectionCount(1);
        _lastGc2 = _initGc2 = GC.CollectionCount(2);
        _lastAllocBytes = GC.GetTotalAllocatedBytes(false);
        _lastGcPauseMs = _initGcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        _launchWall = DateTime.Now;

        _view = new FrameProfilerGraph { Name = "Graph", Visible = false };
        _view.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _view.Position = new Vector2(8f, 8f);
        _view.CustomMinimumSize = new Vector2(GraphWidth, GraphHeight + 70f);
        _view.Size = _view.CustomMinimumSize;
        _view.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_view);

        AddChild(new FrameProfilerFence { Name = "Fence", ProcessPriority = -1_000_000 });

#if DEBUG
        // (perf 2026-07-03) Debug builds default to JIT-OPTIMIZED codegen (Directory.Build.props); report which
        // flavor actually ran so session censuses stay comparable — an unopt run is 2-5x slower in hot loops.
        var dbgAttr = (System.Diagnostics.DebuggableAttribute?)Attribute.GetCustomAttribute(
            typeof(FrameProfiler).Assembly, typeof(System.Diagnostics.DebuggableAttribute));
        string csharpConfig = dbgAttr is { IsJITOptimizerDisabled: true } ? "Debug(unopt)" : "Debug(jit-opt)";
#else
        const string csharpConfig = "Release(optimized)";
#endif
        string env = $"env: managed-debugger={System.Diagnostics.Debugger.IsAttached} " +
                     $"godot-context={(OS.IsDebugBuild() ? "debug" : "release")} csharp={csharpConfig} " +
                     $"vsync={DisplayServer.WindowGetVsyncMode()} maxfps={Godot.Engine.MaxFps} " +
                     $"refresh={DisplayServer.ScreenGetRefreshRate():0}Hz " +
                     $"cpu={OS.GetProcessorCount()}c gpu={RenderingServer.GetVideoAdapterName()}";
        _envBanner = env;
        _debugBuild = csharpConfig.StartsWith("Debug", StringComparison.Ordinal) || OS.IsDebugBuild();
        _buildTag = _debugBuild ? " | DEBUG-BUILD" : "";
        Log.Info($"[frameprofiler] {env}");
        Log.Info($"[frameprofiler] {ColumnsLegend}");   // (8) one-time legend
    }

    private string _envBanner = "";

    /// <summary>(8) The column legend — printed once at boot and written into the log header.</summary>
    private const string ColumnsLegend =
        "columns: proc=all _Process CPU | rcpu=render-thread submit | gpu=main viewport GPU | " +
        "rest=present/vsync/stall (ms-proc-rcpu) | late=deferred+present gap | alloc=managed garbage/frame | " +
        "pipe=shader-pipeline compiles";

    public override void _Input(InputEvent @event)
    {
        // (4) F11 toggles the expanded overlay. Direct physical-key intercept (F11 is the one unbound function
        // key) so it needs no cfg bind; consumed because nothing else uses it.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F11 })
        {
            _expanded = !_expanded;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        TryRegisterDefaults();
        int mode = Mode();
        Prof.Enabled = mode >= 1;

        // Start/stop recording + watchdog with the active state.
        if (mode >= 1 && !_sessionLog.Active)
            _sessionLog.Start(_launchWall, _envBanner, ColumnsLegend);
        if (mode >= 1 && _watchdog is null && WatchdogEnabled())
            _watchdog = new PhaseWatchdog();

        if (mode < 1)
        {
            if (_view.Visible) _view.Visible = false;
            _pending = null;   // a mid-session re-enable restarts the delayed-record pipeline cleanly
            return;   // fully idle when off (besides the cheap cvar reads above)
        }

        // GC deltas across exactly one collector-to-collector window: the previous iteration's post-process tail
        // + THIS iteration's physics + process phase. Process-phase-dominant, so they charge to the PENDING
        // record below; a present-tail gen2 is additionally considered by Classify via the fresh _dGc2.
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long alloc = GC.GetTotalAllocatedBytes(false);
        _dGc0 = g0 - _lastGc0; _dGc1 = g1 - _lastGc1; _dGc2 = g2 - _lastGc2;
        _dAllocBytes = alloc - _lastAllocBytes;
        _lastGc0 = g0; _lastGc1 = g1; _lastGc2 = g2; _lastAllocBytes = alloc;
        double pauseTotal = GC.GetTotalPauseDuration().TotalMilliseconds;
        double dGcPauseMs = Math.Max(0.0, pauseTotal - _lastGcPauseMs);
        _lastGcPauseMs = pauseTotal;

        // Wall time of the PREVIOUS main-loop iteration — Godot's `delta` spans the previous two frame starts,
        // so it belongs to the record drained LAST pass (the P2 pairing; see the _pending field doc).
        double ms = delta * 1000.0;

        _physMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        if (!_measureArmed && GetViewport() is { } vp)
        {
            _viewportRid = vp.GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true);
            _measureArmed = true;
        }
        if (_measureArmed)
        {
            _renderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewportRid);
            _renderGpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid);
        }

        // The five pipeline-compile counters, read now = compiles during the PREVIOUS iteration's draw — they
        // belong to the record being finalized, like the measured render times above. (engine-perf 2026-06-15:
        // Surface + Draw are the synchronous render-thread stalls; Mesh + Specialization are load/async.)
        long cCanvas  = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsCanvas);
        long cMesh    = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsMesh);
        long cSurface = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSurface);
        long cDraw    = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsDraw);
        long cSpec    = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSpecialization);
        long dCanvas  = Math.Max(0, cCanvas  - _lastPipeCanvas);
        long dMesh    = Math.Max(0, cMesh    - _lastPipeMesh);
        long dSurface = Math.Max(0, cSurface - _lastPipeSurface);
        long dDraw    = Math.Max(0, cDraw    - _lastPipeDraw);
        long dSpec    = Math.Max(0, cSpec    - _lastPipeSpec);
        _lastPipeCanvas = cCanvas; _lastPipeMesh = cMesh; _lastPipeSurface = cSurface; _lastPipeDraw = cDraw; _lastPipeSpec = cSpec;
        _lastPipeTotal = cCanvas + cMesh + cSurface + cDraw + cSpec; _lastPipeUber = cSurface + cDraw;
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);

        // ---- FINALIZE the previous iteration's record (P2) ----------------------------------------------------
        // Its scopes/proc were stashed last pass; its wall time (this `delta`) and draw-side counters arrive
        // now. _lastScopes/_lastParent/_lastCounters still hold THAT frame's drain here — hitch forensics read
        // them before this frame's drain overwrites them below.
        if (_pending is { } fin)
        {
            fin.Ms = ms;
            fin.RcpuMs = _renderCpuMs; fin.GpuMs = _renderGpuMs;
            fin.DrawCalls = drawCalls;
            fin.PipeCanvas = dCanvas; fin.PipeMesh = dMesh; fin.PipeSurface = dSurface; fin.PipeDraw = dDraw; fin.PipeSpec = dSpec;
            fin.PipeCompiles = dCanvas + dMesh + dSurface + dDraw + dSpec;
            fin.PipeCompilesUber = dSurface + dDraw;   // the synchronous stalls (kept name for the CSV slot)
            fin.LateMs = Prof.LastLateMs;              // that iteration's deferred+present gap (stamped at this fence)

            _ring[_ringHead] = ms;
            FrameRecord recycled = _records[_ringHead] ?? new FrameRecord();
            _records[_ringHead] = fin;
            _pending = recycled;
            _ringHead = (_ringHead + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
            ComputeStats();

            // Session accumulators (12).
            _sessionHist.Add(ms);
            _windowHist.Add(ms);
            _sessionAllocTotal += fin.AllocBytes;
            if (fin.AllocBytes > _peakAllocFrame) { _peakAllocFrame = fin.AllocBytes; _peakAllocAtS = SessionSeconds; }
            _forensicClock += ms;
            _snapshotClock += ms;

            // (engine-perf 2026-06-16) Under RenderDoc only: self-trigger a capture of the next frame whenever a
            // SYNCHRONOUS SURFACE pipeline-compile fires, so the stochastic stall is captured deterministically.
            // Gate on SessionSeconds > 28 so the budget targets the MID-MATCH residual (the felt join/combat
            // SURFACE compiles), not the expected load-time ones hidden by the loading screen.
            const double RdocCaptureAfterSeconds = 28.0;
            if (fin.PipeSurface > 0 && _rdocCapturesLeft > 0 && SessionSeconds > RdocCaptureAfterSeconds && RenderDocCapture.Available)
            {
                RenderDocCapture.TriggerCapture();
                _rdocCapturesLeft--;
                Prof.Event($"rdoc: capture queued on SURFACE compile (surface+{fin.PipeSurface}, {_rdocCapturesLeft} left)");
            }

            // Per-frame CSV row — the finalized, internally-consistent record (writer thread formats it).
            if (_sessionLog.Active)
            {
                string? topName = null; double topMs = 0.0;
                foreach (ScopeRow row in fin.Scopes)
                    if (row.Ms > topMs) { topMs = row.Ms; topName = row.Name; }
                double restCsv = Math.Max(0.0, ms - fin.ProcMs - fin.RcpuMs);
                _sessionLog.WriteFrame(new FrameSample(
                    (long)fin.FrameId, SessionSeconds, ms, fin.ProcMs, fin.RcpuMs, fin.GpuMs, fin.PhysMs, restCsv,
                    fin.AllocBytes, fin.G0, fin.G1, fin.G2, fin.GcPauseMs, fin.DrawCalls, fin.PipeCompiles,
                    fin.PipeCompilesUber, topName, topMs, fin.LateMs));
            }

            // ---- hitch detection + classification + collapse (on the coherent record) ------------------------
            if (_ringCount > 30)
            {
                double floor = HitchFloorMs();
                if (ms > floor && ms > _median * HitchFactor)
                    OnHitch(ms, Math.Max(0.0, ms - fin.ProcMs - fin.RcpuMs), fin);
            }
            MaybeFlushRun();
        }

        // ---- start THIS iteration's pending record: drain the scopes its process phase produced ----------------
        Prof.SnapshotAndReset(_lastScopes, _lastCounters, _allocScratch, _lastSelf, _lastParent, _lastCalls, _lastMax);

        // The true process span of THIS iteration (fence → here). NOT clamped against `ms` — that is the
        // PREVIOUS iteration's time, and the old clamp destroyed exactly the evidence an isolated CPU spike
        // leaves (a 119 ms process phase was clamped to the prior frame's 6.94 in the 2026-07-02 session).
        _procMs = Prof.FrameProcessMs();

        // proc:other = the per-frame _Process span not attributed to a named WHOLE-NODE scope.
        double accounted = 0.0;
        foreach (string k in TopLevelNodeScopes)
            if (_lastScopes.TryGetValue(k, out double v)) accounted += v;
        _lastScopes["proc:other"] = Math.Max(0.0, _procMs - accounted);
        _lastParent["proc:other"] = "";   // a root-level bucket in the tree

        // Feed the watchdog this frame's "is it long yet?" threshold so it only samples over-budget frames.
        if (_watchdog is { } wd)
            wd.SampleThresholdMs = Math.Max(6.0, HitchFloorMs() * 0.6);

        FrameRecord rec = _pending ??= new FrameRecord();
        rec.Reset(_frameId++);
        rec.ProcMs = _procMs; rec.PhysMs = _physMs;
        rec.WdSeq = Prof.FrameSeq;   // the watchdog window (this fence → the next) covering this iteration
        rec.AllocBytes = _dAllocBytes;
        rec.G0 = _dGc0; rec.G1 = _dGc1; rec.G2 = _dGc2;
        rec.GcPauseMs = dGcPauseMs;

        // Scope table (inclusive + self + alloc per scope) + the one-shot events.
        foreach (KeyValuePair<string, double> kv in _lastScopes)
        {
            _allocScratch.TryGetValue(kv.Key, out double bytes);
            _lastSelf.TryGetValue(kv.Key, out double self);
            _lastCalls.TryGetValue(kv.Key, out double calls);
            _lastMax.TryGetValue(kv.Key, out double maxms);
            rec.Scopes.Add(new ScopeRow(kv.Key, kv.Value, self, bytes, calls, maxms));
            // EWMA baseline (9): slow alpha so a spike barely moves its own "typical".
            _scopeEwma.TryGetValue(kv.Key, out double e);
            _scopeEwma[kv.Key] = e <= 0.0 ? kv.Value : e * 0.95 + kv.Value * 0.05;
            // (perf-investigation) sum into the snapshot window for the steady-state top-scope dump.
            _windowScopeMs.TryGetValue(kv.Key, out double ws);
            _windowScopeMs[kv.Key] = ws + kv.Value;
        }
        _eventScratch.Clear();
        Prof.DrainEvents(_eventScratch);
        for (int i = 0; i < _eventScratch.Count; i++)
            rec.Events.Add(_eventScratch[i]);

        // Rolling alloc rate (10) heap size (the rate itself is ring-derived in ComputeStats).
        _heapBytes = GC.GetTotalMemory(false);

        if (ConsumeDumpRequest())
            DumpRingCsv();

        // (11) periodic steady-state snapshot.
        if (_snapshotClock >= SnapshotIntervalMs)
        {
            EmitSnapshot();
            _snapshotClock = 0.0;
        }

        bool show = true;
        if (_view.Visible != show)
            _view.Visible = show;
        _view.CustomMinimumSize = new Vector2(GraphWidth, _expanded ? GraphHeight + 70f + ExpandedExtraHeight : GraphHeight + 70f);
        _view.Size = _view.CustomMinimumSize;
        _view.QueueRedraw();

        Prof.MarkCollectorEnd();   // open the post-process window: the fence measures it, the watchdog labels it
    }

    private void ComputeStats()
    {
        int n = _ringCount;
        if (n == 0) { _median = _p99 = 0; return; }
        Array.Copy(_ring, _sortScratch, n);
        Array.Sort(_sortScratch, 0, n);
        _median = _sortScratch[n / 2];
        int idx = Math.Min(n - 1, (int)(n * 0.99));
        _p99 = _sortScratch[idx];

        // Alloc rate (10): sum the ring's per-frame alloc / its wall-time → MB/s.
        double sumMs = 0.0, sumBytes = 0.0;
        for (int i = 0; i < n; i++)
        {
            int slot = (_ringHead - 1 - i + RingSize * 2) % RingSize;
            sumMs += _ring[slot];
            if (_records[slot] is { } r) sumBytes += r.AllocBytes;
        }
        _allocRateMbPerSec = sumMs > 0.0 ? sumBytes / sumMs * 1000.0 / (1024.0 * 1024.0) : 0.0;
    }

    // ---- hitch handling ---------------------------------------------------------------------------------------

    private void OnHitch(double ms, double restMs, FrameRecord rec)
    {
        _sessionHitchCount++;
        (string? wdPhase, int wdSamples, int wdTotal, bool wdFresh) = WatchdogCompleted(rec);
        (HitchClass cls, string reason) = Classify(ms, restMs, rec, wdPhase, wdSamples, wdTotal, wdFresh);
        double nowS = SessionSeconds;

        // (P8) A VSYNC/PRESENT hitch within a second of a primary is the present queue draining that primary's
        // backlog, not an independent stutter — tag + count it as a recovery TAIL so the census stays readable.
        bool recovery = cls == HitchClass.VsyncPresent && nowS - _lastPrimaryHitchAtS <= 1.0;
        if (recovery)
        {
            _windowRecovery++; _sessionRecovery++;
        }
        else
        {
            _lastPrimaryHitchAtS = nowS;
            _windowHitchByClass[(int)cls]++;
            _sessionHitchByClass[(int)cls]++;
        }

        // (P11) Scope-coverage debt: a hitch attributed to unnamed code = an "add a Prof scope here" TODO.
        if (cls == HitchClass.CpuLogic && reason.StartsWith("proc:other", StringComparison.Ordinal)) _covProcOther++;
        if (wdFresh && wdPhase == "(unscoped)" && wdSamples * 2 >= wdTotal && wdTotal >= 4) _covUnscoped++;

        // Worst-frame hall of shame (12).
        ConsiderWorst(ms, cls, rec);

        // (3) pin the latest hitch for the overlay (+ the opt-in P7 alert flash).
        _lastHitchSummary = $"{ms:0.0}ms {ClassTag(cls)} ({reason})";
        _lastHitchAtS = nowS;
        if (ms >= HitchFloorMs() * 2.0 && AlertEnabled())
        {
            _alertUntilS = nowS + 1.5;
            _alertText = $"{ms:0.0}ms {ClassTag(cls)}";
        }

        // (6) collapse: extend a run of the same class+kind seen within the last second instead of re-logging.
        if (_runCount > 0 && _runClass == cls && _runRecovery == recovery && nowS - _runLastS < 1.0)
        {
            _runCount++;
            _runMinMs = Math.Min(_runMinMs, ms);
            _runMaxMs = Math.Max(_runMaxMs, ms);
            _runLastS = nowS;
            return;
        }

        // New run: flush the previous one, then log THIS hitch in full.
        FlushRun();
        _runClass = cls; _runRecovery = recovery; _runCount = 1; _runMinMs = _runMaxMs = ms;
        _runStartS = _runLastS = _runLastInterimS = nowS; _runReason = reason;
        LogHitchFull(ms, restMs, rec, cls, reason, recovery, wdPhase, wdSamples, wdTotal, wdFresh);
    }

    /// <summary>(5) Decide what dominated the frame — reading ONLY the finalized record (its ms, scopes, proc and
    /// draw counters all describe the SAME iteration since P2) plus the watchdog's completed histogram for that
    /// iteration. Priority order: the unambiguous one-shot causes (a pipeline compile, a gen-2 / long GC pause)
    /// first, then a dominating asset build, then the proportional split (CPU / GPU / present) and finally the
    /// rest-dominated external-stall heuristic — which the watchdog can VETO: a sample majority in a NAMED scope
    /// means main-thread game code ran during the iteration's late (deferred/post-process) phase, where neither
    /// ProcMs nor any process scope can see it.</summary>
    private (HitchClass, string) Classify(double ms, double restMs, FrameRecord rec,
        string? wdPhase, int wdSamples, int wdTotal, bool wdFresh)
    {
        if (rec.PipeCompiles > 0)
            return (HitchClass.PipelineCompile,
                $"{rec.PipeCompiles} pipeline compile(s) — SYNC[surface+{rec.PipeSurface} draw+{rec.PipeDraw}] " +
                $"async[mesh+{rec.PipeMesh} spec+{rec.PipeSpec}] canvas+{rec.PipeCanvas}");

        // A gen2 in the iteration's PRESENT tail lands in the deltas read one collector pass later — the fresh
        // _dGc2 covers exactly that window, so consider it when the frame is rest-dominated (P2).
        if (rec.G2 > 0 || rec.GcPauseMs >= Math.Max(2.0, 0.3 * ms) || (_dGc2 > 0 && restMs >= 0.5 * ms))
        {
            string tail = rec.G2 == 0 && _dGc2 > 0 ? " (gen2 in the present tail)" : "";
            return (HitchClass.GcPause,
                $"GC g0+{rec.G0} g1+{rec.G1} g2+{rec.G2}, pause {rec.GcPauseMs:0.0}ms, {Bytes(rec.AllocBytes)} allocated{tail}{TopAllocSuffix(rec)}");
        }

        // A scope total can EXCEED the frame when it ran CONCURRENTLY on a worker thread (stream.predecode /
        // iqm.anims aggregate cross-thread into the drain) — that is background work, not this frame's cause,
        // so it must not hijack the classification (it produced phantom ASSET-BUILD verdicts on ordinary
        // frames: "stream.predecode 294ms" inside a 25ms frame). Cap candidacy at ~the frame span; the worker
        // totals still show in the scope tree for context.
        string dom = DominantScope(rec, ms * 1.2 + 2.0, out double domMs);
        bool assetDom = dom.StartsWith("stream.", StringComparison.Ordinal) || dom.StartsWith("iqm.", StringComparison.Ordinal);
        if (assetDom && domMs >= 0.4 * ms)
            return (HitchClass.AssetBuild, $"{dom} {domMs:0.0}ms (asset build/upload)");

        // External: rest-dominated, game-side quiet (the §12.6c compositor/driver/OS class).
        bool external = ms >= 25.0 && restMs >= ms * 0.7 && rec.ProcMs <= ms * 0.3
                        && rec.PipeCompiles == 0 && rec.G2 == 0 && rec.GpuMs <= ms * 0.3;
        if (external)
        {
            // (P2) Watchdog veto: the numbers say rest-dominated, but the sampler caught the main thread in a
            // NAMED scope for most of the window ⇒ game code ran in the late phase (a deferred call / work after
            // the collector) that ProcMs structurally cannot see. Attribute it — don't blame the OS.
            if (wdFresh && wdPhase is { Length: > 0 } wp && wp[0] != '(' && wdTotal >= 4 && wdSamples * 2 >= wdTotal)
                return (HitchClass.CpuLogic, $"{wp} ≈{wdSamples}ms late-phase (watchdog {wdSamples}/{wdTotal})");

            // (hitch-fix) GcPauseMs is a once-per-frame GC.GetTotalPauseDuration delta, so an all-thread gen2
            // freeze that STARTED in frame N-1 lands its tail on frame N with G2==0 there — which used to fall
            // through to EXTERNAL and blame the OS for what is really our GC (the catharsis 139ms "EXTERNAL" worst
            // frame was a gen2 + mp.weapon catch-up tail). If a very recent frame collected gen2 or paused long,
            // attribute this rest-dominated tail to GC, not the compositor.
            if (RecentGcEvent(out double recentPauseMs))
                return (HitchClass.GcPause, $"gen2/GC-pause tail (prior frame paused {recentPauseMs:0.0}ms; this frame rest-dominated)");
            return (HitchClass.External,
                $"rest-dominated (late {rec.LateMs:0.0}ms), game-side quiet — OS/compositor/driver");
        }

        if (rec.ProcMs >= 0.55 * ms)
            return (HitchClass.CpuLogic, $"{dom} {domMs:0.0}ms{Anomaly(dom, domMs)}");

        if (rec.GpuMs >= 0.45 * ms)
            return (HitchClass.GpuBound, $"gpu {rec.GpuMs:0.0}ms, {rec.DrawCalls:0} draws");

        if (restMs >= 0.55 * ms)
            return (HitchClass.VsyncPresent, $"present/vsync-bound (rest {restMs:0.0}ms, gpu {rec.GpuMs:0.0}ms)");

        return (HitchClass.Unknown, $"mixed — {dom} {domMs:0.0}ms");
    }

    /// <summary>(P11) Name the ALLOCATOR on a GC hitch, not just the pause: the scope with the biggest per-scope
    /// allocation this frame, when it is a meaningful share (≥1 MB and ≥25% of the frame's total).</summary>
    private static string TopAllocSuffix(FrameRecord rec)
    {
        string? name = null; double best = 0.0;
        foreach (ScopeRow row in rec.Scopes)
            if (row.Bytes > best) { best = row.Bytes; name = row.Name; }
        if (name is null || best < 1024.0 * 1024.0 || best < 0.25 * rec.AllocBytes)
            return "";
        return $", top alloc {name} {Bytes(best)}";
    }

    private void LogHitchFull(double ms, double restMs, FrameRecord rec, HitchClass cls, string reason,
        bool recovery, string? wdPhase, int wdSamples, int wdTotal, bool wdFresh)
    {
        int dropped = FramesDropped(ms);
        string drop = dropped > 0 ? $" ({dropped} dropped @{Hz():0}Hz)" : "";
        // Only a FRESH histogram (sampled during this record's own fence→fence window) is shown — a stale one
        // (from whichever frame last went over budget) was the pre-P2 misdirection.
        string wd = wdFresh && wdPhase is not null && wdTotal >= 3
            ? $" | watchdog: {wdSamples}/{wdTotal} samples in '{wdPhase}'" : "";
        string marks = Markers();
        string late = rec.LateMs >= 1.0 ? $" late {rec.LateMs:0.0}" : "";
        Emit($"[hitch {Tag(cls, recovery)}] {ms:0.0}ms{drop} (med {_median:0.0}, ×{ms / Math.Max(0.1, _median):0.0}) — {reason} | " +
             $"proc {rec.ProcMs:0.0} rcpu {rec.RcpuMs:0.0} gpu {rec.GpuMs:0.0} rest {restMs:0.0}{late} | alloc {Bytes(rec.AllocBytes)}{marks}{wd}{_buildTag}",
             toConsole: true);

        // Multi-line forensic block (rate-limited so a hitch storm logs one block, not a wall).
        if (_forensicClock - _lastForensicDumpAt < 500.0)
            return;
        _lastForensicDumpAt = _forensicClock;
        DumpForensics(rec);
    }

    /// <summary>(6) Emit a one-line summary for a collapsed run of ≥2 same-class hitches, then optionally clear it.</summary>
    private void FlushRun()
    {
        if (_runCount >= 2)
        {
            double span = Math.Max(0.0, _runLastS - _runStartS);
            Emit($"[hitch {Tag(_runClass, _runRecovery)} ×{_runCount}] {_runMinMs:0.0}–{_runMaxMs:0.0}ms over {span:0.0}s " +
                 $"(med {_median:0.0}) — {_runReason}", toConsole: true);
        }
        _runCount = 0;
    }

    /// <summary>Flush a run that has gone quiet (&gt;1s since the last hit), or emit an interim line for a long
    /// ongoing run (every ~3s) so a sustained stutter still reports progress.</summary>
    private void MaybeFlushRun()
    {
        if (_runCount == 0)
            return;
        double nowS = SessionSeconds;
        if (nowS - _runLastS > 1.0)
        {
            FlushRun();
            return;
        }
        if (_runCount >= 2 && nowS - _runLastInterimS > 3.0)
        {
            _runLastInterimS = nowS;
            double span = Math.Max(0.0, nowS - _runStartS);
            Emit($"[hitch {Tag(_runClass, _runRecovery)} ×{_runCount}…] ongoing {_runMinMs:0.0}–{_runMaxMs:0.0}ms over {span:0.0}s " +
                 $"— {_runReason}", toConsole: true);
        }
    }

    private void ConsiderWorst(double ms, HitchClass cls, FrameRecord rec)
    {
        if (_worst.Count >= 10 && ms <= _worst[^1].Ms)
            return;
        string scope = DominantScope(rec, ms * 1.2 + 2.0, out _);   // skip concurrent-worker totals (see Classify)
        _worst.Add((ms, cls, scope, SessionSeconds));
        _worst.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));
        if (_worst.Count > 10)
            _worst.RemoveRange(10, _worst.Count - 10);
    }

    private void DumpForensics(FrameRecord rec)
    {
        // (16) Call tree: indented self-time, with a "<parent>:other" remainder synthesised at each level. FILE
        // ONLY (Option C) — the console stays clean (the one-liner already names the dominant scope + reason);
        // the file has no width limit, so it gets the FULL tree (uncapped) for offline analysis.
        string tree = BuildScopeTree(rec, maxRows: 64);
        if (tree.Length > 0)
            Emit($"[hitch]   tree:\n{tree}", toConsole: false);

        if (rec.PipeCompiles > 0 || rec.DrawCalls > 0)
            Emit($"[hitch]   gpu: draws {rec.DrawCalls:0}, pipeline compiles +{rec.PipeCompiles}" +
                 (rec.PipeCompiles > 0 ? $" — SYNC[surface+{rec.PipeSurface} draw+{rec.PipeDraw}] async[mesh+{rec.PipeMesh} spec+{rec.PipeSpec}] canvas+{rec.PipeCanvas}" : "") +
                 $", vram {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0):0}MB", toConsole: true);

        if (rec.G0 + rec.G1 + rec.G2 > 0 || rec.GcPauseMs > 0.1)
            Emit($"[hitch]   gc: g0+{rec.G0} g1+{rec.G1} g2+{rec.G2} pause {rec.GcPauseMs:0.0}ms alloc {Bytes(rec.AllocBytes)} " +
                 $"(rate {_allocRateMbPerSec:0}MB/s, heap {Bytes(_heapBytes)})", toConsole: true);

        // Events on this frame + the few before it.
        string events = "";
        for (int back = 7; back >= 0; back--)
        {
            FrameRecord? r = RecordAt(back + 1);
            if (r is null) continue;
            foreach (string e in r.Events)
            {
                if (events.Length > 0) events += "; ";
                events += back == 0 ? e : $"[-{back}f] {e}";
            }
        }
        if (events.Length > 0)
            Emit($"[hitch]   events: {events}", toConsole: true);

        // The run-up: the preceding 8 frames' totals.
        string prev = "";
        for (int back = 8; back >= 1; back--)
        {
            FrameRecord? r = RecordAt(back + 1);
            if (r is null) continue;
            if (prev.Length > 0) prev += " ";
            prev += r.Ms.ToString("0.0");
        }
        if (prev.Length > 0)
            Emit($"[hitch]   prev: {prev}", toConsole: true);
    }

    // ---- periodic snapshot (11) -------------------------------------------------------------------------------

    private void EmitSnapshot()
    {
        if (_windowHist.Count == 0)
            return;
        string hitches = HitchBreakdown(_windowHitchByClass);
        if (_windowRecovery > 0)
            hitches += $" (+{_windowRecovery} recovery)";
        // (perf-investigation) scene-graph complexity — the suspected driver of the Godot-internal proc:other
        // floor on entity-heavy maps. nodes = total scene-tree nodes; robj = objects drawn this frame.
        double nodeCount = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        double renderObjs = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        Emit($"[frameprofile] scene: nodes {nodeCount:0} objs-drawn {renderObjs:0} draws {RecordAt(1)?.DrawCalls ?? 0:0} " +
             $"vram {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0):0}MB",
             toConsole: Mode() >= 2);
        Emit($"[frameprofile] census: {NodeTypeCensus()}", toConsole: Mode() >= 2);
        Emit($"[frameprofile] {_windowHist.Count} frames: p50 {_windowHist.Percentile(0.50):0.0} " +
             $"p95 {_windowHist.Percentile(0.95):0.0} p99 {_windowHist.Percentile(0.99):0.0} " +
             $"p99.9 {_windowHist.Percentile(0.999):0.0}ms (max {_windowHist.Max:0.0}) | " +
             $"{hitches} | alloc {_allocRateMbPerSec:0}MB/s heap {Bytes(_heapBytes)} | proc {_procMs:0.0} gpu {_renderGpuMs:0.0}",
             toConsole: Mode() >= 2);

        // (perf-investigation) steady-state per-scope breakdown: the top scopes by total window-ms, printed as a
        // PER-FRAME AVERAGE (ms/frame) so two experiments are directly comparable without provoking a hitch. File
        // only by default (console at mode>=2). proc:other here = the still-unattributed main-thread _Process span.
        if (_windowScopeMs.Count > 0 && _windowHist.Count > 0)
        {
            var top = new List<KeyValuePair<string, double>>(_windowScopeMs);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder("[frameprofile] ms/frame: ");
            for (int i = 0; i < top.Count && i < 10; i++)
            {
                double perFrame = top[i].Value / _windowHist.Count;
                if (perFrame < 0.05) break;
                if (i > 0) sb.Append("  ");
                sb.Append(top[i].Key).Append(' ').Append(perFrame.ToString("0.0"));
            }
            Emit(sb.ToString(), toConsole: Mode() >= 2);
        }

        _windowHist.Reset();
        _windowScopeMs.Clear();
        Array.Clear(_windowHitchByClass);
        _windowRecovery = 0;
    }

    // ---- session summary (12) ---------------------------------------------------------------------------------

    private void EmitSummary()
    {
        if (_summaryEmitted || _sessionHist.Count == 0)
            return;
        _summaryEmitted = true;

        double dur = SessionSeconds;
        double avgFps = _sessionHist.Mean > 0 ? 1000.0 / _sessionHist.Mean : 0;
        double low1 = _sessionHist.Percentile(0.99), low01 = _sessionHist.Percentile(0.999);
        Emit($"=== frame profiler session summary ({dur:0}s, {_sessionHist.Count} frames) ===", toConsole: true);
        Emit($"  fps: avg {avgFps:0.0}  1%low {(low1 > 0 ? 1000.0 / low1 : 0):0}  0.1%low {(low01 > 0 ? 1000.0 / low01 : 0):0}" +
             $"  (median {_sessionHist.Percentile(0.5):0.0}ms, worst {_sessionHist.Max:0.0}ms)", toConsole: true);
        int primaries = 0;
        for (int i = 0; i < _sessionHitchByClass.Length; i++) primaries += _sessionHitchByClass[i];
        string tails = _sessionRecovery > 0 ? $"; +{_sessionRecovery} VSYNC/PRESENT recovery tails" : "";
        Emit($"  hitches: {_sessionHitchCount} total — {primaries} primaries: {HitchBreakdown(_sessionHitchByClass)}{tails}", toConsole: true);
        for (int i = 0; i < _worst.Count && i < 5; i++)
        {
            var w = _worst[i];
            Emit($"    worst[{i + 1}]: {w.Ms:0.0}ms {ClassTag(w.Cls)} ({w.Scope}) @ t={w.TimeS:0.0}s", toConsole: true);
        }
        Emit($"  gc: g0+{GC.CollectionCount(0) - _initGc0} g1+{GC.CollectionCount(1) - _initGc1} " +
             $"g2+{GC.CollectionCount(2) - _initGc2}, total pause {GC.GetTotalPauseDuration().TotalMilliseconds - _initGcPauseMs:0}ms", toConsole: true);
        Emit($"  alloc: {Bytes(_sessionAllocTotal)} total, {_allocRateMbPerSec:0}MB/s recent, " +
             $"peak {Bytes(_peakAllocFrame)} in one frame @ t={_peakAllocAtS:0.0}s, heap {Bytes(_heapBytes)}", toConsole: true);
        // (P11) Scope-coverage debt: where the profiler could NOT name the code — each count is a place to add a
        // Prof scope so the next hunt starts attributed instead of at "(unscoped)".
        if (_covProcOther + _covUnscoped > 0)
            Emit($"  scope coverage debt: proc:other dominated {_covProcOther} hitch(es), watchdog '(unscoped)' won " +
                 $"{_covUnscoped} — add Prof scopes there", toConsole: true);
        // (P5) A debug census must never be mistaken for the shipped game's behaviour.
        if (_debugBuild)
            Emit("  note: DEBUG build — frame times and the hitch census are NOT release-representative " +
                 "(run the release export via tools/perf-run.ps1 for real numbers)", toConsole: true);
        if (_sessionLog.DroppedFrames > 0)
            Emit($"  note: {_sessionLog.DroppedFrames} CSV rows dropped under disk backpressure (log lines unaffected)", toConsole: true);
    }

    public override void _ExitTree()
    {
        // Flush the in-flight collapse run, write the summary footer, then stop the background writer cleanly.
        if (Prof.Enabled || _sessionLog.Active)
        {
            FlushRun();
            EmitSummary();
        }
        _watchdog?.Stop();
        _sessionLog.Stop();
        if (Instance == this) Instance = null;
    }

    // ---- scope tree (16) --------------------------------------------------------------------------------------

    /// <summary>Render the frame's scopes as a box-drawing call tree (Option D): a fixed-width name column with
    /// <c>├─/└─/│</c> connectors, then right-aligned stat columns — inclusive <c>ms</c>, <c>%fr</c> (share of the
    /// frame), <c>×n</c> (open count this frame), <c>max</c> (longest single open, shown only when n&gt;1),
    /// <c>alloc</c>, and <c>typ</c> (the rolling-baseline multiplier when the node is abnormal). Self-time is left
    /// implicit (= a node's ms minus the sum of its children, which the tree shows). A synthetic <c>(other)</c>
    /// child carries each level's unattributed remainder. File-only (no width limit); built off the hot path.</summary>
    private string BuildScopeTree(FrameRecord rec, int maxRows)
    {
        var children = new Dictionary<string, List<string>>();
        var incl = new Dictionary<string, double>();
        var bytes = new Dictionary<string, double>();
        var calls = new Dictionary<string, double>();
        var maxms = new Dictionary<string, double>();
        foreach (ScopeRow row in rec.Scopes)
        {
            string parent = _lastParent.TryGetValue(row.Name, out string? p) ? p : "";
            (children.TryGetValue(parent, out List<string>? list) ? list : children[parent] = new List<string>()).Add(row.Name);
            incl[row.Name] = row.Ms; bytes[row.Name] = row.Bytes; calls[row.Name] = row.Calls; maxms[row.Name] = row.MaxMs;
        }

        const int NameW = 30;
        double frame = rec.Ms > 0.0 ? rec.Ms : 1.0;
        var sb = new StringBuilder(1024);
        int rows = 0;

        sb.Append("scope".PadRight(NameW))
          .Append("ms".PadLeft(6)).Append(' ').Append("%fr".PadLeft(4)).Append(' ').Append("×n".PadLeft(4)).Append(' ')
          .Append("max".PadLeft(6)).Append(' ').Append("alloc".PadLeft(7)).Append(' ').Append("typ".PadLeft(6)).Append('\n');

        string Nums(string node)
        {
            incl.TryGetValue(node, out double mi);
            bytes.TryGetValue(node, out double b);
            calls.TryGetValue(node, out double c);
            maxms.TryGetValue(node, out double mx);
            _scopeEwma.TryGetValue(node, out double typ);
            string callsStr = c > 1.0 ? "×" + (int)c : "";
            string maxStr = c > 1.0 ? mx.ToString("0.0") : "";
            string allocStr = b >= 1024.0 ? (long)(b / 1024.0) + "KB" : "";
            string typStr = typ > 0.05 && mi > typ * 2.5 ? (mi / typ).ToString("0") + "×" : "";
            string pctStr = (mi / frame * 100.0).ToString("0") + "%";
            return mi.ToString("0.0").PadLeft(6) + " " + pctStr.PadLeft(4) + " " + callsStr.PadLeft(4) + " " +
                   maxStr.PadLeft(6) + " " + allocStr.PadLeft(7) + " " + typStr.PadLeft(6);
        }

        void Row(string nameField, string? node, double otherMs)
        {
            string nf = nameField.Length > NameW ? nameField.Substring(0, NameW - 1) + "…" : nameField.PadRight(NameW);
            if (node is not null)
                sb.Append(nf).Append(Nums(node)).Append('\n');
            else // synthetic "(other)" remainder — only ms + %fr are meaningful
                sb.Append(nf).Append(otherMs.ToString("0.0").PadLeft(6)).Append(' ')
                  .Append(((otherMs / frame * 100.0).ToString("0") + "%").PadLeft(4)).Append('\n');
            rows++;
        }

        void Walk(string node, string prefix, bool isLast, int depth)
        {
            if (rows >= maxRows) return;
            string connector = depth == 0 ? "" : (isLast ? "└─ " : "├─ ");
            Row(prefix + connector + node, node, 0.0);

            if (!children.TryGetValue(node, out List<string>? kids)) return;
            kids.Sort((a, c) => incl.GetValueOrDefault(c).CompareTo(incl.GetValueOrDefault(a)));
            incl.TryGetValue(node, out double mi);
            double kidSum = 0.0;
            foreach (string k in kids) kidSum += incl.GetValueOrDefault(k);
            // Synthesise an "(other)" remainder only when the parent's unattributed self-time is worth a row
            // (≥1ms AND ≥8% of the parent) — a tiny remainder is just rounding/self and only clutters the tree.
            double other = mi - kidSum;
            bool hasOther = other >= 1.0 && other >= 0.08 * mi && kids.Count > 0;
            string childPrefix = prefix + (depth == 0 ? "" : (isLast ? "   " : "│  "));
            for (int i = 0; i < kids.Count && rows < maxRows; i++)
                Walk(kids[i], childPrefix, i == kids.Count - 1 && !hasOther, depth + 1);
            if (hasOther && rows < maxRows)
                Row(childPrefix + "└─ (other)", null, other);
        }

        if (children.TryGetValue("", out List<string>? roots))
        {
            roots.Sort((a, c) => incl.GetValueOrDefault(c).CompareTo(incl.GetValueOrDefault(a)));
            foreach (string r in roots) Walk(r, "", false, 0);
        }
        return sb.ToString().TrimEnd('\n');
    }

    // (perf-investigation) one-shot scene-tree node-type census (called only from the 5s snapshot, not per frame):
    // counts nodes by concrete type so an entity-heavy / animation-heavy scene shows WHICH built-in node type
    // dominates the per-frame engine processing that lands in proc:other (AnimationPlayer / AudioStreamPlayer3D /
    // GpuParticles3D / etc.). Reports the count of nodes with processing actually enabled, where it matters.
    private readonly Dictionary<string, int> _censusScratch = new();
    private readonly Dictionary<string, int> _censusProc = new();
    private string NodeTypeCensus()
    {
        _censusScratch.Clear();
        _censusProc.Clear();
        if (GetTree()?.Root is { } root)
            CensusWalk(root);
        var list = new List<KeyValuePair<string, int>>(_censusScratch);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count && i < 10; i++)
        {
            if (i > 0) sb.Append("  ");
            _censusProc.TryGetValue(list[i].Key, out int proc);
            sb.Append(list[i].Key).Append(' ').Append(list[i].Value);
            if (proc > 0) sb.Append("(proc").Append(proc).Append(')');
        }
        return sb.ToString();
    }
    private void CensusWalk(Node n)
    {
        string t = n.GetType().Name;
        _censusScratch.TryGetValue(t, out int c); _censusScratch[t] = c + 1;
        if (n.IsProcessing() || n.IsPhysicsProcessing())
        { _censusProc.TryGetValue(t, out int p); _censusProc[t] = p + 1; }
        int kids = n.GetChildCount();
        for (int i = 0; i < kids; i++)
            CensusWalk(n.GetChild(i));
    }

    private FrameRecord? RecordAt(int back)
    {
        if (back > _ringCount) return null;
        return _records[(_ringHead - back + RingSize * 2) % RingSize];
    }

    /// <summary>(hitch-fix) Did one of the last few frames just take a gen2 collection or a long GC pause? Used to
    /// re-attribute a rest-dominated "EXTERNAL"-looking frame to GC when it is really the tail of a freeze that the
    /// once-per-frame pause delta charged to the previous frame. RecordAt(1) is the current frame; look back 2-4.</summary>
    private bool RecentGcEvent(out double pauseMs)
    {
        pauseMs = 0.0;
        for (int back = 2; back <= 4; back++)
        {
            FrameRecord? r = RecordAt(back);
            if (r is null) continue;
            if (r.G2 > 0 || r.GcPauseMs >= 8.0)
            {
                pauseMs = r.GcPauseMs;
                return true;
            }
        }
        return false;
    }

    // ---- on-demand ring export --------------------------------------------------------------------------------

    private bool ConsumeDumpRequest()
    {
        CvarService? cv = ClientCvars();
        if (cv is null || cv.GetFloat("cl_frameprofiler_dump") == 0f)
            return false;
        cv.Set("cl_frameprofiler_dump", "0");
        return true;
    }

    private void DumpRingCsv()
    {
        string ringPath = UserPaths.Resolve("frameprofile_ring.csv");
        using var f = Godot.FileAccess.Open(ringPath, Godot.FileAccess.ModeFlags.Write);
        if (f is null)
        {
            Log.Info($"[frameprofiler] ring dump FAILED (couldn't open {ringPath})");
            return;
        }
        f.StoreLine("frame,ms,proc_ms,rcpu_ms,gpu_ms,phys_ms,rest_ms,late_ms,alloc_kb,gc0,gc1,gc2,gc_pause_ms," +
                    "draw_calls,pipe_compiles,pipe_sync,top1,top1_ms,top2,top2_ms,top3,top3_ms,events");
        for (int back = _ringCount; back >= 1; back--)
        {
            FrameRecord? r = RecordAt(back);
            if (r is null) continue;
            r.Scopes.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));
            string t1 = r.Scopes.Count > 0 ? $"{Csv(r.Scopes[0].Name)},{r.Scopes[0].Ms:0.00}" : ",";
            string t2 = r.Scopes.Count > 1 ? $"{Csv(r.Scopes[1].Name)},{r.Scopes[1].Ms:0.00}" : ",";
            string t3 = r.Scopes.Count > 2 ? $"{Csv(r.Scopes[2].Name)},{r.Scopes[2].Ms:0.00}" : ",";
            double rest = Math.Max(0.0, r.Ms - r.ProcMs - r.RcpuMs);
            f.StoreLine($"{r.FrameId},{r.Ms:0.00},{r.ProcMs:0.00},{r.RcpuMs:0.00},{r.GpuMs:0.00},{r.PhysMs:0.00}," +
                        $"{rest:0.00},{r.LateMs:0.00},{r.AllocBytes / 1024},{r.G0},{r.G1},{r.G2},{r.GcPauseMs:0.00}," +
                        $"{r.DrawCalls:0},{r.PipeCompiles},{r.PipeCompilesUber},{t1},{t2},{t3}," +
                        Csv(string.Join("; ", r.Events)));
        }
        Log.Info($"[frameprofiler] ring dumped: {_ringCount} frames -> {ringPath}");
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---- output sink ------------------------------------------------------------------------------------------

    /// <summary>Route a profile line to the console (when <paramref name="toConsole"/>) and ALWAYS to the
    /// background session log (when recording is active). The console gate keeps the per-second snapshot off the
    /// console unless <c>cl_frameprofiler 2</c>, while still recording it to the file.</summary>
    private void Emit(string line, bool toConsole)
    {
        if (toConsole)
            Log.Info(line);
        if (_sessionLog.Active)
            _sessionLog.WriteLine(SessionSeconds, line);
    }

    // ---- small helpers ----------------------------------------------------------------------------------------

    /// <summary>The record's biggest inclusive scope — the FINALIZED record's own drain, not whatever the live
    /// dictionaries currently hold (they may already describe the next frame). <paramref name="maxMs"/> filters
    /// out concurrent-worker totals (a scope bigger than the frame ran on another thread and cannot be this
    /// frame's cause); pass <see cref="double.MaxValue"/> for the unfiltered view. Falls back to the unfiltered
    /// winner if the filter rejects everything.</summary>
    private static string DominantScope(FrameRecord rec, double maxMs, out double ms)
    {
        string best = "?"; double bestMs = 0.0;
        string bestAny = "?"; double bestAnyMs = 0.0;
        foreach (ScopeRow row in rec.Scopes)
        {
            if (row.Ms > bestAnyMs) { bestAnyMs = row.Ms; bestAny = row.Name; }
            if (row.Ms <= maxMs && row.Ms > bestMs) { bestMs = row.Ms; best = row.Name; }
        }
        if (bestMs <= 0.0) { ms = bestAnyMs; return bestAny; }
        ms = bestMs;
        return best;
    }

    private static string DominantScope(FrameRecord rec, out double ms) => DominantScope(rec, double.MaxValue, out ms);

    /// <summary>(9) "(typ X, N× over)" suffix when a scope is far above its rolling baseline; empty otherwise.</summary>
    private string Anomaly(string scope, double ms)
    {
        if (!_scopeEwma.TryGetValue(scope, out double typ) || typ < 0.05 || ms < typ * 3.0)
            return "";
        return $" (typ {typ:0.0}ms, {ms / typ:0}× over)";
    }

    /// <summary>This frame's numeric markers (e.g. "ticks 3" — the catch-up tick count) as a compact suffix;
    /// empty when none. Lets a server.tick spike be read as "ran 3 ticks" vs "one expensive tick".</summary>
    private string Markers()
    {
        if (_lastCounters.Count == 0) return "";
        var sb = new StringBuilder(" | ");
        bool first = true;
        foreach (KeyValuePair<string, double> kv in _lastCounters)
        {
            if (!first) sb.Append(", ");
            sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("0.##"));
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>The completed watchdog histogram for the record's OWN window (its fence→fence covers exactly the
    /// iteration the finalized record describes). Fresh=false ⇒ the sampler never fired during that window (the
    /// frame stayed under budget) or only older data exists — callers show nothing rather than a stale,
    /// misleading suffix (the pre-P2 bug: quiet frames reported whichever frame last went over budget).</summary>
    private (string? Phase, int Samples, int Total, bool Fresh) WatchdogCompleted(FrameRecord rec)
    {
        if (_watchdog is not { } wd)
            return (null, 0, 0, false);
        wd.SnapshotCompleted(rec.WdSeq, out string? phase, out int samples, out int total, out bool fresh);
        return (phase, samples, total, fresh);
    }

    private static string ClassTag(HitchClass c) => c switch
    {
        HitchClass.CpuLogic => "CPU-LOGIC",
        HitchClass.GpuBound => "GPU-BOUND",
        HitchClass.VsyncPresent => "VSYNC/PRESENT",
        HitchClass.GcPause => "GC-PAUSE",
        HitchClass.PipelineCompile => "PIPELINE-COMPILE",
        HitchClass.AssetBuild => "ASSET-BUILD",
        HitchClass.External => "EXTERNAL",
        _ => "MIXED",
    };

    /// <summary>(P8) The log-line tag: the class, plus a "·recovery" marker on a VSYNC/PRESENT tail that is just
    /// the present queue draining a primary hitch's backlog.</summary>
    private static string Tag(HitchClass c, bool recovery) => recovery ? ClassTag(c) + "·recovery" : ClassTag(c);

    private string HitchBreakdown(int[] byClass)
    {
        var sb = new StringBuilder();
        int total = 0;
        for (int i = 0; i < byClass.Length; i++) total += byClass[i];
        if (total == 0) return "no hitches";
        for (int i = byClass.Length - 1; i >= 0; i--)
            if (byClass[i] > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(byClass[i]).Append(' ').Append(ClassTag((HitchClass)i));
            }
        return sb.ToString();
    }

    /// <summary>(7) Human-readable byte size: B / KB / MB / GB.</summary>
    private static string Bytes(long b) => Bytes((double)b);
    private static string Bytes(double b)
    {
        double abs = Math.Abs(b);
        if (abs >= 1024.0 * 1024.0 * 1024.0) return $"{b / (1024.0 * 1024.0 * 1024.0):0.0}GB";
        if (abs >= 1024.0 * 1024.0) return $"{b / (1024.0 * 1024.0):0.0}MB";
        if (abs >= 1024.0) return $"{b / 1024.0:0}KB";
        return $"{b:0}B";
    }

    /// <summary>(7) Whole vsync/refresh frames missed by a frame of <paramref name="ms"/> (rounds to the nearest
    /// refresh interval, then subtracts the one frame we were entitled to).</summary>
    private int FramesDropped(double ms)
    {
        double budget = BudgetMs();
        return budget > 0 ? Math.Max(0, (int)Math.Round(ms / budget) - 1) : 0;
    }

    private double Hz() => 1000.0 / BudgetMs();

    // ---- drawing data (read by FrameProfilerGraph._Draw) -----------------------------------------------------
    internal const float GraphWidth = 300f;
    internal const float GraphHeight = 56f;
    private const float ExpandedExtraHeight = 132f;   // room for the live-scope list + last-hitch line

    internal void DrawInto(Control c)
    {
        Font font = c.GetThemeDefaultFont();
        if (font is null)
            return;
        double budget = BudgetMs();
        double scaleMax = Math.Max(budget * 3.0, _p99 * 1.2 + 1.0);

        c.DrawRect(new Rect2(0f, 0f, GraphWidth, GraphHeight), new Color(0f, 0f, 0f, 0.5f));

        // (1) Stacked category bars: proc (blue) + rcpu (cyan) + rest (grey) = ms; gpu drawn as an orange marker
        // (it runs async, so it doesn't stack); a pipeline-compile or gen-2 frame gets a red cap.
        int n = _ringCount;
        float bw = GraphWidth / RingSize;
        var cProc = new Color(0.35f, 0.6f, 1f);
        var cRcpu = new Color(0.3f, 0.85f, 0.95f);
        var cRest = new Color(0.5f, 0.5f, 0.55f);
        var cGpu = new Color(1f, 0.6f, 0.2f);
        var cCap = new Color(1f, 0.2f, 0.2f);
        for (int i = 0; i < n; i++)
        {
            int slot = (_ringHead - n + i + RingSize * 2) % RingSize;
            double v = _ring[slot];
            FrameRecord? r = _records[slot];
            float x = i * bw, w = Math.Max(1f, bw);
            float Scale(double t) => (float)Math.Clamp(t / scaleMax, 0.0, 1.0) * GraphHeight;

            if (r is null)
            {
                float hh = Scale(v);
                c.DrawRect(new Rect2(x, GraphHeight - hh, w, hh), cRest);
                continue;
            }
            double proc = r.ProcMs, rcpu = r.RcpuMs, rest = Math.Max(0.0, v - proc - rcpu);
            float y = GraphHeight;
            void Seg(double t, Color col) { float h = Scale(t); if (h <= 0f) return; c.DrawRect(new Rect2(x, y - h, w, h), col); y -= h; }
            Seg(proc, cProc);
            Seg(rcpu, cRcpu);
            Seg(rest, cRest);
            // GPU marker tick.
            float gy = GraphHeight - Scale(r.GpuMs);
            c.DrawRect(new Rect2(x, gy - 1f, w, 2f), cGpu);
            // Red cap on a one-shot hazard frame.
            if (r.PipeCompiles > 0 || r.G2 > 0)
                c.DrawRect(new Rect2(x, GraphHeight - Scale(v) - 2f, w, 2f), cCap);
        }

        // Budget line.
        float by = GraphHeight - (float)Math.Clamp(budget / scaleMax, 0.0, 1.0) * GraphHeight;
        c.DrawLine(new Vector2(0f, by), new Vector2(GraphWidth, by), new Color(1f, 1f, 1f, 0.5f), 1f);

        var white = new Color(1f, 1f, 1f, 0.95f);
        double cur = n > 0 ? _ring[(_ringHead - 1 + RingSize) % RingSize] : 0;
        double curFps = cur > 0 ? 1000.0 / cur : 0;
        double low1 = _p99 > 0 ? 1000.0 / _p99 : 0;

        // (2) Header: fps + 1%-low + session hitch counter.
        float ty = GraphHeight + 13f;
        c.DrawString(font, new Vector2(2f, ty), $"{curFps,3:0} fps ({cur:0.0}ms)  med {_median:0.0}  p99 {_p99:0.0}  1%low {low1:0}  hitch {_sessionHitchCount}",
            HorizontalAlignment.Left, -1f, 12, white);
        Color gpuCol = _renderGpuMs > budget ? new Color(1f, 0.55f, 0.3f) : white;
        c.DrawString(font, new Vector2(2f, ty + 16f), $"proc {_procMs:0.0}  rcpu {_renderCpuMs:0.0}  gpu {_renderGpuMs:0.0}",
            HorizontalAlignment.Left, -1f, 12, gpuCol);
        Color allocCol = _dAllocBytes > 256 * 1024 ? new Color(1f, 0.7f, 0.3f) : white;
        c.DrawString(font, new Vector2(2f, ty + 32f), $"GC {_lastGc0}/{_lastGc1}/{_lastGc2}  {Bytes(_dAllocBytes)}/f  {_allocRateMbPerSec:0}MB/s  heap {Bytes(_heapBytes)}",
            HorizontalAlignment.Left, -1f, 12, allocCol);
        if (RecordAt(1) is { } latest)
        {
            Color pipeCol = latest.PipeCompiles > 0 ? new Color(1f, 0.55f, 0.3f) : white;
            c.DrawString(font, new Vector2(2f, ty + 48f), $"draws {latest.DrawCalls:0}  pipe +{latest.PipeCompiles}  pause {latest.GcPauseMs:0.0}ms  [F11 detail]",
                HorizontalAlignment.Left, -1f, 12, pipeCol);
        }

        // (P7) Opt-in hitch alert flash (cl_frameprofiler_alert): a short, loud marker over the graph so a felt
        // stutter is immediately nameable without watching the console.
        if (SessionSeconds < _alertUntilS)
            c.DrawString(font, new Vector2(2f, 12f), $"HITCH {_alertText}", HorizontalAlignment.Left, -1f, 14,
                new Color(1f, 0.25f, 0.2f));

        // (3) Pinned last-hitch line (dim) — only meaningful for a while after a hitch.
        if (_lastHitchSummary.Length > 0)
        {
            double age = SessionSeconds - _lastHitchAtS;
            var dim = new Color(1f, 0.8f, 0.5f, age < 6.0 ? 0.95f : 0.45f);
            c.DrawString(font, new Vector2(2f, ty + 64f), $"last hitch: {_lastHitchSummary} ({age:0.0}s ago)",
                HorizontalAlignment.Left, -1f, 11, dim);
        }

        // (4/16) Expanded overlay: top live scopes with their rolling baseline.
        if (_expanded)
        {
            float ey = ty + 82f;
            c.DrawString(font, new Vector2(2f, ey), "live scopes (ms, typ):", HorizontalAlignment.Left, -1f, 11, white);
            ey += 14f;
            var top = new List<KeyValuePair<string, double>>(_lastScopes);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < top.Count && i < 7; i++)
            {
                if (top[i].Value < 0.05) break;
                _scopeEwma.TryGetValue(top[i].Key, out double typ);
                bool hot = typ > 0.05 && top[i].Value > typ * 2.5;
                c.DrawString(font, new Vector2(6f, ey),
                    $"{top[i].Key,-16} {top[i].Value,5:0.0}  (typ {typ:0.0})",
                    HorizontalAlignment.Left, -1f, 11, hot ? new Color(1f, 0.55f, 0.3f) : white);
                ey += 13f;
            }
        }
    }

    private static double BudgetMs()
    {
        double hz = DisplayServer.ScreenGetRefreshRate();
        if (hz <= 1.0) hz = 60.0;
        return 1000.0 / hz;
    }

    // ---- cvars -----------------------------------------------------------------------------------------------
    private static CvarService? ClientCvars()
    {
        CvarService cv = XonoticGodot.Game.Menu.MenuState.Cvars;
        return cv is not null && cv.Has("cl_frameprofiler") ? cv : null;
    }

    private void TryRegisterDefaults()
    {
        if (_defaultsRegistered)
            return;
        CvarService cv = XonoticGodot.Game.Menu.MenuState.Cvars;
        if (cv is null)
            return;
        RegisterDefaults(cv);
        _defaultsRegistered = true;
    }

    /// <summary>Register the <c>cl_frameprofiler*</c> defaults (archived). Idempotent; safe to call from boot.</summary>
    public static void RegisterDefaults(ICvarService cvars)
    {
        if (cvars is null) return;
        cvars.Register("cl_frameprofiler", "0");
        cvars.Register("cl_frameprofiler_hitchms", "12");
        cvars.Register("cl_frameprofiler_watchdog", "1");
        // (P7) Opt-in on-screen flash when a ≥2×floor hitch fires (class + ms at the graph) — for feel-testing
        // sessions where the console isn't being watched.
        cvars.Register("cl_frameprofiler_alert", "0");
        // Transient trigger (NOT archived): `set cl_frameprofiler_dump 1` writes the forensic ring CSV + re-arms.
        cvars.Register("cl_frameprofiler_dump", "0");
    }

    /// <summary>Effective mode: 0 off / 1 graph+hitch-log+record / 2 +console snapshot. Debug-default-on.</summary>
    private static int Mode()
    {
        CvarService? cv = ClientCvars();
        if (cv is null)
            return OS.IsDebugBuild() ? 1 : 0;
        int m = (int)cv.GetFloat("cl_frameprofiler");
        if (m != 0)
            return m;
        if (OS.IsDebugBuild() && !cv.IsModified("cl_frameprofiler"))
            return 1;
        return 0;
    }

    private static bool WatchdogEnabled()
    {
        CvarService? cv = ClientCvars();
        return cv is null || cv.GetFloat("cl_frameprofiler_watchdog") != 0f;
    }

    private static bool AlertEnabled()
    {
        CvarService? cv = ClientCvars();
        return cv is not null && cv.GetFloat("cl_frameprofiler_alert") != 0f;
    }

    private static double HitchFloorMs()
    {
        if (Api.Services is null) return 12.0;
        float v = Api.Cvars.GetFloat("cl_frameprofiler_hitchms");
        return v > 0f ? v : 12.0;
    }
}

/// <summary>The drawing surface for <see cref="FrameProfiler"/>; defers all painting back to the owner.</summary>
public partial class FrameProfilerGraph : Control
{
    public override void _Draw()
    {
        FrameProfiler.Instance?.DrawInto(this);
    }
}

/// <summary>
/// Runs first each frame (lowest <see cref="Node.ProcessPriority"/>) to stamp the start of the process step, so
/// <see cref="FrameProfiler"/> (which runs last) can measure the true per-frame total <c>_Process</c> span.
/// </summary>
public partial class FrameProfilerFence : Node
{
    public FrameProfilerFence() => ProcessMode = ProcessModeEnum.Always;
    public override void _Process(double delta) => Prof.MarkFrameStart();
}

/// <summary>
/// A fixed-bucket frame-time histogram (0.25 ms buckets up to 300 ms + an overflow bin) for cheap percentile
/// queries without storing every frame. <see cref="Add"/> is one array increment; the session instance is never
/// reset, the window instance is reset each periodic snapshot.
/// </summary>
internal sealed class FrameHistogram
{
    private const double BucketMs = 0.25;
    private const int N = 1200;                 // 1200 * 0.25 ms = 300 ms
    private readonly long[] _buckets = new long[N + 1];
    public long Count { get; private set; }
    private double _sum;
    public double Max { get; private set; }

    public void Add(double ms)
    {
        int b = (int)(ms / BucketMs);
        if (b < 0) b = 0;
        if (b > N) b = N;
        _buckets[b]++;
        Count++;
        _sum += ms;
        if (ms > Max) Max = ms;
    }

    public double Mean => Count > 0 ? _sum / Count : 0.0;

    /// <summary>The frame time at percentile <paramref name="p"/> (0..1). Bucket-resolution; exact for the tails
    /// that matter (a p99.9 in the overflow bin reports the true max).</summary>
    public double Percentile(double p)
    {
        if (Count == 0) return 0.0;
        long target = (long)(Count * p);
        long acc = 0;
        for (int i = 0; i <= N; i++)
        {
            acc += _buckets[i];
            if (acc >= target)
                return i >= N ? Max : (i + 0.5) * BucketMs;
        }
        return Max;
    }

    public void Reset()
    {
        Array.Clear(_buckets);
        Count = 0;
        _sum = 0.0;
        Max = 0.0;
    }
}

/// <summary>
/// (17) A sampling watchdog: a background thread that, during an over-budget frame, polls the main thread's
/// innermost open scope (<see cref="Prof.MainPhase"/>) so a stall can be attributed even inside code we never
/// wrapped in a scope (it reports <c>(unscoped)</c> then). Near-zero main-thread cost — the main thread only does
/// a volatile store per scope push/pop. The thread sleeps cheaply when frames are short and only samples once a
/// frame has already exceeded <see cref="SampleThresholdMs"/>.
/// </summary>
internal sealed class PhaseWatchdog
{
    // Plain field: written by the main thread each frame, read by the watchdog once per loop. A one-cycle-stale
    // read just shifts the sample-start by ~1 ms — harmless for a heuristic, so no volatile/interlock needed.
    public double SampleThresholdMs = 8.0;

    private readonly Thread _thread;
    private volatile bool _stop;

    private readonly object _gate = new();
    private Dictionary<string, int> _hist = new();       // the in-progress frame (seq == _curSeq)
    private Dictionary<string, int> _histDone = new();   // the last frame that produced samples (retired)
    private long _curSeq = -1, _doneSeq = -1;
    private int _curTotal, _doneTotal;

    public PhaseWatchdog()
    {
        _thread = new Thread(Loop)
        {
            Name = "frameprofiler-watchdog",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,   // it must wake promptly to catch a short hitch
        };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            if (!Prof.Enabled)
            {
                Thread.Sleep(50);
                continue;
            }
            long seq = Prof.FrameSeq;
            long started = Prof.FrameStartTicks;
            double elapsed = Prof.SpanMs(Stopwatch.GetTimestamp() - started);
            if (elapsed >= SampleThresholdMs)
            {
                string phase = Prof.MainPhase ?? "(unscoped)";
                lock (_gate)
                {
                    if (seq != _curSeq)
                    {
                        // (P2) The previous frame's histogram is COMPLETE — retire it for the collector, which
                        // reads it when it finalizes that frame's record one fence later. (Discarding it here was
                        // the pre-P2 stale-suffix bug: a quiet frame's hitch line showed whichever frame's
                        // histogram happened to still be lying around.)
                        (_histDone, _hist) = (_hist, _histDone);
                        _doneSeq = _curSeq; _doneTotal = _curTotal;
                        _hist.Clear();
                        _curSeq = seq; _curTotal = 0;
                    }
                    _hist.TryGetValue(phase, out int v); _hist[phase] = v + 1;
                    _curTotal++;
                }
            }
            Thread.Sleep(1);   // ~1 ms cadence; plenty of samples for a >12 ms hitch
        }
    }

    /// <summary>The dominant sampled phase of the frame with sequence <paramref name="seq"/>, if the sampler
    /// fired during that frame's fence→fence window. The collector asks when FINALIZING that frame's record (one
    /// fence later), so the wanted histogram is either still "in progress" (nothing has sampled the next frame
    /// yet) or the freshly retired one. Anything else ⇒ that frame never went over budget: fresh=false, and the
    /// caller shows nothing instead of stale, misleading data.</summary>
    public void SnapshotCompleted(long seq, out string? phase, out int samples, out int total, out bool fresh)
    {
        lock (_gate)
        {
            Dictionary<string, int> src;
            if (_curSeq == seq) { src = _hist; total = _curTotal; }
            else if (_doneSeq == seq) { src = _histDone; total = _doneTotal; }
            else { phase = null; samples = 0; total = 0; fresh = false; return; }

            phase = null; samples = 0; fresh = true;
            foreach (KeyValuePair<string, int> kv in src)
                if (kv.Value > samples) { samples = kv.Value; phase = kv.Key; }
        }
    }

    public void Stop()
    {
        _stop = true;
        _thread.Join(200);
    }
}
