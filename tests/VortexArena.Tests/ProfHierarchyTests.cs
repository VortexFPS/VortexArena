using System.Collections.Generic;
using System.Threading;
using XonoticGodot.Common.Diagnostics;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Unit coverage for the <see cref="Prof"/> rework (per-thread accumulators + hierarchy/self-time + the watchdog
/// phase marker). The collector (FrameProfiler) can only be exercised inside Godot, so these pin the pure
/// accumulation contract it depends on: inclusive vs self time, parent edges, the per-frame drain/reset, and that
/// scopes opened on a worker thread merge into the same drain.
/// </summary>
public sealed class ProfHierarchyTests
{
    private static void Drain(out Dictionary<string, double> incl, out Dictionary<string, double> self,
        out Dictionary<string, string> parent)
    {
        incl = new Dictionary<string, double>();
        self = new Dictionary<string, double>();
        parent = new Dictionary<string, string>();
        var counters = new Dictionary<string, double>();
        var alloc = new Dictionary<string, double>();
        Prof.SnapshotAndReset(incl, counters, alloc, self, parent);
    }

    [Fact]
    public void NestedScopes_RecordParentAndSelfTime()
    {
        Prof.Enabled = true;
        try
        {
            Drain(out _, out _, out _); // clear anything pending from another test

            using (Prof.Sample("outer"))
            {
                Spin(3);
                using (Prof.Sample("inner"))
                    Spin(3);
            }

            Drain(out var incl, out var self, out var parent);

            Assert.True(incl.ContainsKey("outer"));
            Assert.True(incl.ContainsKey("inner"));

            // Inner is a child of outer; outer is a frame root.
            Assert.Equal("outer", parent["inner"]);
            Assert.Equal("", parent["outer"]);

            // Inclusive(outer) covers inner; self(outer) excludes it. So inclusive ≥ self, and inner's inclusive
            // is accounted for in outer's inclusive-minus-self.
            Assert.True(incl["outer"] >= self["outer"]);
            Assert.True(incl["outer"] >= incl["inner"]);
            Assert.True(self["inner"] <= incl["inner"] + 1e-9);
            // outer's self should be (roughly) inclusive minus inner's inclusive — never negative.
            Assert.True(self["outer"] >= 0.0);
            Assert.True(self["outer"] <= incl["outer"] - incl["inner"] + 0.5);
        }
        finally { Prof.Enabled = false; Drain(out _, out _, out _); }
    }

    [Fact]
    public void Drain_ResetsBetweenFrames()
    {
        Prof.Enabled = true;
        try
        {
            using (Prof.Sample("a")) Spin(2);
            Drain(out var first, out _, out _);
            Assert.True(first.ContainsKey("a"));

            // Nothing opened this "frame" → the drain is empty (capacity retained, values cleared).
            Drain(out var second, out _, out _);
            Assert.False(second.ContainsKey("a"));
        }
        finally { Prof.Enabled = false; Drain(out _, out _, out _); }
    }

    [Fact]
    public void Disabled_IsNoOp()
    {
        Prof.Enabled = false;
        using (Prof.Sample("ghost")) Spin(1);
        Drain(out var incl, out _, out _);
        Assert.False(incl.ContainsKey("ghost"));
    }

    [Fact]
    public void WorkerThreadScopes_MergeIntoDrain()
    {
        Prof.Enabled = true;
        try
        {
            Drain(out _, out _, out _);

            var t = new Thread(() =>
            {
                using (Prof.Sample("worker.outer"))
                    using (Prof.Sample("worker.inner"))
                        Spin(2);
            });
            t.Start();
            t.Join();

            Drain(out var incl, out var _, out var parent);
            Assert.True(incl.ContainsKey("worker.outer"));
            Assert.True(incl.ContainsKey("worker.inner"));
            Assert.Equal("worker.outer", parent["worker.inner"]);
        }
        finally { Prof.Enabled = false; Drain(out _, out _, out _); }
    }

    /// <summary>Burn a few hundred microseconds of wall time so the Stopwatch-based scope records a nonzero span
    /// (and child time is strictly inside parent time) without depending on Thread.Sleep resolution.</summary>
    private static void Spin(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double target = ms;
        long acc = 0;
        while (sw.Elapsed.TotalMilliseconds < target) acc++;
        if (acc < 0) System.Console.WriteLine(acc); // defeat dead-code elimination
    }
}
