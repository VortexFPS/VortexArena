using System;
using System.Diagnostics;
using System.Threading;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Server;

namespace VortexArena.Game.Net;

/// <summary>
/// S5 (sv_threaded — DEFAULT ON since the 2026-07-11 WS1 rework) — the dedicated server-simulation worker
/// thread for the listen/host path.
///
/// When the listen server starts with <c>sv_threaded 1</c> (the default) on a windowed (non-headless) host, the heavy
/// authoritative work — <see cref="ServerNet.Tick"/> → <see cref="GameWorld.Frame"/> → the 72 Hz
/// <c>SimulationLoop</c> ticks — moves off the Godot main thread onto THIS long-lived background thread, so the
/// render frame no longer blocks on the sim. The main thread keeps only client prediction + render.
///
/// ISOLATION MODEL (the lock-fallback chosen for S5; see _scratch/perf-specs/s5.md and the parityContract):
/// the host keeps ONE shared <see cref="GameWorld"/> and serialises ALL access with a single object — the
/// gate the host installs on <see cref="ServerNet.SimGate"/>. This worker's <see cref="ServerNet.Tick"/> takes
/// that gate around the whole tick; the main thread takes the SAME gate around the span of <c>NetGame._Process</c>
/// that reads server-world state and runs the prediction replay. The two therefore never touch the entity table
/// concurrently. (The cleaner two-world split — a client-private prediction facade — was the spec's primary
/// design, but the listen host's <c>_Process</c> interleaves direct <c>_serverWorld</c>/<c>LocalServerPlayer</c>/
/// mutator reads with the prediction loop so densely that splitting them risked the default-off parity contract;
/// the spec authorises this lock fallback as the documented escape hatch — "correct-and-default-off-inert beats
/// clever-but-fragile".)
///
/// NOTHING here runs when sv_threaded is 0: the host never constructs a ServerThread, never installs a SimGate,
/// and the main thread calls <see cref="ServerNet.Tick"/> directly exactly as today.
///
/// THREAD SAFETY: this is pure C# work (<c>ServerNet</c>/<c>GameWorld</c>/<c>SimulationLoop</c>) — no Godot
/// scene-tree / RenderingServer call happens on it. The only Godot touches reached from <see cref="ServerNet.Tick"/>
/// are <c>GD.Print</c>/<c>GD.PrintErr</c> diagnostics, which route through Godot 4's thread-safe message queue.
/// </summary>
internal sealed class ServerThread : IDisposable
{
    private readonly GameWorld _world;
    private readonly ServerNet _net;
    private readonly Func<float> _tickRate;
    private readonly Thread _thread;

    // Stop signalling: a volatile flag the run loop polls, plus a wait handle so Dispose can wake a sleeping loop
    // and Join it promptly. Both touched from the main thread (Dispose) and read on the worker — the volatile +
    // the event are the only cross-thread state besides the SimGate the net layer owns.
    private volatile bool _running = true;
    private readonly ManualResetEventSlim _stopped = new(false);

    /// <summary>Bounded join timeout (ms) on <see cref="Dispose"/> so a wedged tick can't hang teardown forever.</summary>
    private const int JoinTimeoutMs = 2000;

    public ServerThread(GameWorld world, ServerNet net, Func<float> tickRate)
    {
        _world = world;
        _net = net;
        _tickRate = tickRate;
        _thread = new Thread(Run)
        {
            IsBackground = true,   // never keep the process alive on its own; dies with the app
            Name = "XG-ServerSim",
        };
    }

    /// <summary>Start the worker. Call once, after <see cref="ServerNet.SimGate"/> is installed.</summary>
    public void Start() => _thread.Start();

    private void Run()
    {
        // First action on this thread: make the server world the ambient facade FOR THIS THREAD ONLY, so every
        // Api.* read inside the sim (MovementParameters.FromCvars, TriggerTouch, find/radius, sound) resolves to
        // the server world without disturbing the process-wide ambient the main thread reads. With the shared-world
        // lock fallback this points at the SAME instance the main thread uses, so it is semantically a no-op here —
        // but it documents intent and keeps the worker correct even if some code later swaps the process ambient.
        Api.SetThreadServices(_world.ServerServices);

        // WS1 stage 1: sends from this thread STAGE into ServerNet's outbox (pooled copies) instead of touching
        // the main-thread-affine Godot ENet peer — the encode now runs here, right after the ticks it describes.
        ServerNet.OnSimWorker = true;

        // Fixed-cadence accumulator loop. We measure real elapsed time with a Stopwatch and feed it to
        // ServerNet.Tick, whose GameWorld.Frame → SimulationLoop.Advance accumulates and runs as many fixed
        // 72 Hz ticks as are due (identical to the single-threaded drive — we just supply the real delta here
        // instead of Godot's _Process delta). We spin/sleep to ~1 ms granularity so the cadence is tight without
        // busy-burning a core.
        var sw = Stopwatch.StartNew();
        long lastTicks = sw.ElapsedTicks;
        double tickFreq = Stopwatch.Frequency;

        try
        {
            while (_running)
            {
                long now = sw.ElapsedTicks;
                float realDelta = (float)((now - lastTicks) / tickFreq);
                lastTicks = now;

                // (§13.5) The worker runs ONLY the pure-C# sim (StepSimThreaded takes the SimGate internally) —
                // NOT the Godot ENet transport, which is main-thread-affine and is pumped by the main thread via
                // ServerNet.PumpTransportThreaded. This is the serialisation point against the main thread's
                // prediction span. A throw must NOT kill the thread silently — log and keep going.
                try
                {
                    _net.StepSimThreaded(realDelta);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[ServerThread] sim step threw: {ex}");
                }

                // Pace to roughly the sim period. We don't need sub-ms precision — Advance() absorbs jitter via its
                // accumulator — so sleep for most of the remaining budget and let the next delta carry the rest.
                float period = _tickRate();
                if (period <= 0f) period = 1f / 72f;
                int sleepMs = (int)(period * 1000f) - 1; // leave ~1 ms headroom; Advance smooths the remainder
                if (sleepMs > 0)
                    _stopped.Wait(sleepMs);              // wakes immediately on Dispose
                else
                    Thread.Yield();
            }
        }
        finally
        {
            // The thread-local override dies with the thread, but clear it explicitly for tidiness.
            Api.ClearThreadServices();
        }
    }

    /// <summary>
    /// Stop the worker and join it (bounded). Call from the MAIN thread BEFORE tearing down <see cref="ServerNet"/>
    /// / the world, so the socket/world teardown is single-threaded (no tick races the Dispose). Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (!_running && !_thread.IsAlive)
            return;
        _running = false;
        _stopped.Set();  // wake a sleeping run loop so it exits promptly
        bool joined = !_thread.IsAlive || _thread.Join(JoinTimeoutMs);
        if (!joined)
            GD.PrintErr("[ServerThread] worker did not exit within the join timeout — proceeding with teardown.");
        // Only dispose the wait handle once the worker has actually exited; disposing it while a wedged worker is
        // still inside _stopped.Wait() would throw ObjectDisposedException on that thread. A leaked handle on the
        // (degenerate) timeout path is the lesser evil.
        if (joined)
            _stopped.Dispose();
    }
}
