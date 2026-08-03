using System.Numerics;
using System.Threading;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The bounded-wait main-thread tracer (zero-hitch 2026-08-03, the cn.predict gate-contention fix):
/// uncontended and reentrant calls must reach the LIVE gated tracer unchanged; a gate held by another
/// thread past the timeout must degrade to the gate-free static-world twin WITHOUT blocking anywhere near
/// the worker's tick length — the property that turns a 10-25 ms mid-combat gate wait into a bounded ~2 ms.
/// </summary>
public class BoundedGateTracerTests
{
    /// <summary>A live-tracer stand-in that returns an unmistakable sentinel, so tests can tell which of
    /// the two paths (live vs static fallback) actually served a call.</summary>
    private sealed class SentinelTracer : ITraceService
    {
        public int Calls;
        public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
        {
            Calls++;
            return new TraceResult { Fraction = 0.123f, EndPos = new Vector3(9f, 9f, 9f) };
        }
        public int PointContents(Vector3 point) { Calls++; return 424242; }
        public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
    }

    private static CollisionWorld FloorWorld()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096, -4096, -64), new Vector3(4096, 4096, 0), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    [Fact]
    public void Uncontended_UsesTheLiveTracer()
    {
        var live = new SentinelTracer();
        var tracer = new BoundedGateTracer(live, FloorWorld(), new object());

        TraceResult tr = tracer.Trace(new Vector3(0, 0, 100), Vector3.Zero, Vector3.Zero,
            new Vector3(0, 0, -100), MoveFilter.Normal, null);

        Assert.Equal(0.123f, tr.Fraction);   // the sentinel — the live path served it
        Assert.Equal(1, live.Calls);
    }

    [Fact]
    public void ReentrantOwner_UsesTheLiveTracer()
    {
        var live = new SentinelTracer();
        var gate = new object();
        var tracer = new BoundedGateTracer(live, FloorWorld(), gate);

        // Main-thread code that mutates the world under lock(gate) and traces inside keeps its old path:
        // TryEnter on an already-owned monitor succeeds immediately (reentrant).
        lock (gate)
        {
            TraceResult tr = tracer.Trace(new Vector3(0, 0, 100), Vector3.Zero, Vector3.Zero,
                new Vector3(0, 0, -100), MoveFilter.Normal, null);
            Assert.Equal(0.123f, tr.Fraction);
        }
        Assert.Equal(1, live.Calls);
    }

    [Fact]
    public void Contended_ManyTraces_ShareOneFrameBudget()
    {
        // The first live capture's lesson: a slide-move runs 4-8 traces, and each waiting just UNDER the
        // per-trace timeout re-created the 10-16ms frame with zero fallbacks. The FRAME budget must make
        // trace 2..N skip the wait once trace 1 spent it.
        var live = new SentinelTracer();
        var gate = new object();
        var tracer = new BoundedGateTracer(live, FloorWorld(), gate) { TimeoutMs = 25 };
        BoundedGateTracer.ResetFrame();
        double savedBudget = BoundedGateTracer.FrameWaitBudgetMs;
        BoundedGateTracer.FrameWaitBudgetMs = 25.0;   // one full per-trace timeout, then stop waiting
        try
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var worker = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
            worker.Start();
            held.Wait();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 8; i++)
                tracer.Trace(new Vector3(0, 0, 100), Vector3.Zero, Vector3.Zero,
                    new Vector3(0, 0, -100), MoveFilter.Normal, null);
            sw.Stop();
            release.Set();
            worker.Join();

            Assert.Equal(0, live.Calls);   // all eight degraded to the static twin
            // Naive per-trace waiting would be ~8x25 = 200ms; the shared budget caps it near ONE timeout.
            Assert.True(sw.ElapsedMilliseconds < 120,
                $"8 contended traces took {sw.ElapsedMilliseconds}ms - the frame budget did not stick");
        }
        finally
        {
            BoundedGateTracer.FrameWaitBudgetMs = savedBudget;
            BoundedGateTracer.ResetFrame();
            BoundedGateTracer.FallbacksSinceRead = 0;
        }
    }

    [Fact]
    public void Contended_FallsBackToTheStaticWorld_Bounded()
    {
        var live = new SentinelTracer();
        var gate = new object();
        var tracer = new BoundedGateTracer(live, FloorWorld(), gate) { TimeoutMs = 2 };

        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var worker = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
        worker.Start();
        held.Wait();   // the "combat tick": another thread owns the gate for a long time

        BoundedGateTracer.ResetFrame();   // a prior test may have spent this frame's wait budget
        int before = BoundedGateTracer.FallbacksSinceRead;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TraceResult tr = tracer.Trace(new Vector3(0, 0, 100), Vector3.Zero, Vector3.Zero,
            new Vector3(0, 0, -100), MoveFilter.Normal, null);
        sw.Stop();
        release.Set();
        worker.Join();

        // Served by the STATIC twin: the ray from z=100 to z=-100 hits the floor at z=0 (fraction 0.5),
        // nothing like the live sentinel — and the live tracer was never entered.
        Assert.Equal(0, live.Calls);
        Assert.Equal(0.5f, tr.Fraction, 2);
        Assert.Equal(before + 1, BoundedGateTracer.FallbacksSinceRead);
        // Bounded: nowhere near a combat tick's 10-25 ms hold (generous CI slack over the 2 ms timeout).
        Assert.True(sw.ElapsedMilliseconds < 100, $"trace blocked {sw.ElapsedMilliseconds}ms under contention");
    }
}
