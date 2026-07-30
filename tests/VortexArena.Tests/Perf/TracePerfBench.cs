using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using XonoticGodot.Common.Framework;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Engine.Collision;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// T33 perf bench (BotPerfBench pattern — measurement, not a CI assertion) for the collision/trace
/// hot path on REAL map data: <see cref="TraceService.Trace"/> box sweeps + point traces +
/// <see cref="TraceService.PointContents"/> against the atelier collision world, plus the map-load
/// startup wall time (<see cref="BspReader.Read"/> + <see cref="BspCollisionBuilder.Build"/>).
/// Every movement step, projectile flight, bot tracewalk, and hitscan shot funnels through this path
/// (DP SV_TraceBox), so ms/trace here bounds the whole server tick.
///
/// No-ops when the content checkout is missing (CI without assets); the data dir can be overridden
/// with the XG_DATA_DIR environment variable.
///
/// Run: dotnet test tests/XonoticGodot.Tests --filter TracePerfBench -l "console;verbosity=detailed"
///
/// Measured baseline (2026-06-09, dev machine, Debug build, atelier):
///   map load:      BspReader.Read 22 ms + BspCollisionBuilder.Build 219 ms (4.5 MB bsp)
///   box sweep:     len 32 → 0.043 ms/trace · len 256 → 0.135 ms/trace · len 2048 → 2.06 ms/trace
///                  (long sweeps broadphase a huge swept AABB — bot LOS / hitscan length matters)
///   point trace:   len 2048 → 1.49 ms/trace
///   PointContents: 0.0012 ms/call, 0 B/call
///   alloc:         424 B/box-trace, 128 B/point-trace — NOT zero: Trace news a Brush via
///                  Brush.FromBox(mins, maxs) every call (TraceService.cs:65) even though the
///                  _candidates scratch list is pooled (TraceService.cs:56). Reported to the
///                  TraceService owner (T33 is infra-only); a cached/stack box would zero this.
/// </summary>
[Collection("GlobalState")]
public class TracePerfBench
{
    private static readonly string DataDir =
        Environment.GetEnvironmentVariable("XG_DATA_DIR")
        ?? @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";
    private const string Map = "atelier";

    private readonly ITestOutputHelper _out;
    public TracePerfBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Benchmark_AtelierTraces()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }

        var sb = new StringBuilder();
        void Line(string s) { _out.WriteLine(s); sb.AppendLine(s); }

        // --- map load (the dedicated-server startup metric): read + parse + build collision ---
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountGameDir(DataDir));
        string bspPath = $"maps/{Map}.bsp";
        Assert.True(vfs.Exists(bspPath), $"missing {bspPath}");

        byte[] bytes = vfs.ReadBytes(bspPath);
        var swRead = Stopwatch.StartNew();
        BspData bsp = BspReader.Read(bytes);
        swRead.Stop();
        var swBuild = Stopwatch.StartNew();
        BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp);
        swBuild.Stop();
        CollisionWorld world = built.World;
        var trace = new TraceService(world);

        Line($"=== atelier trace benchmark ===");
        Line($"[map load] BspReader.Read: {swRead.Elapsed.TotalMilliseconds:F0} ms   " +
             $"BspCollisionBuilder.Build: {swBuild.Elapsed.TotalMilliseconds:F0} ms   ({bytes.Length / 1024} KB bsp)");

        // --- seed points: entity origins (spawn points / items sit in open playable space), lifted off the floor ---
        var seeds = new List<Vector3>();
        foreach (var e in bsp.Entities)
        {
            if (!e.TryGetValue("origin", out string? os) || os is null) continue;
            string[] p = os.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) continue;
            if (float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                seeds.Add(new Vector3(x, y, z + 24f));
        }
        Assert.True(seeds.Count > 8, $"too few entity seed points ({seeds.Count})");
        Line($"seed points: {seeds.Count} entity origins");

        var mins = new Vector3(-16, -16, -24);  // the player hull
        var maxs = new Vector3(16, 16, 45);
        var rng = new Random(1234);             // deterministic endpoints run-to-run

        // --- probe 1: player-hull box sweeps at short / medium / long range (move step → LOS-length).
        //     Long sweeps cost ~50x a short one (huge swept-AABB broadphase), so cap their count to keep
        //     the default `dotnet test` run fast. ---
        RunTraces(Line, trace, seeds, rng, mins, maxs, 32f, count: 10_000, label: "box sweep len   32");
        RunTraces(Line, trace, seeds, rng, mins, maxs, 256f, count: 10_000, label: "box sweep len  256");
        RunTraces(Line, trace, seeds, rng, mins, maxs, 2048f, count: 1_500, label: "box sweep len 2048");

        // --- probe 2: point traces (hitscan / bot LOS — mins == maxs) ---
        RunTraces(Line, trace, seeds, rng, Vector3.Zero, Vector3.Zero, 2048f, count: 1_500, label: "point trace len 2048");

        // --- probe 3: PointContents (water/lava checks — every player + projectile, every tick) ---
        {
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            int inSolid = 0;
            const int N = 10_000;
            for (int i = 0; i < N; i++)
            {
                Vector3 pt = seeds[i % seeds.Count] + RandomDir(rng) * (float)(rng.NextDouble() * 64.0);
                if (trace.PointContents(pt) != 0) inSolid++;
            }
            sw.Stop();
            long alloc1 = GC.GetAllocatedBytesForCurrentThread();
            Line($"[PointContents] {N} calls: {sw.Elapsed.TotalMilliseconds:F1} ms total, " +
                 $"{sw.Elapsed.TotalMilliseconds / N:F4} ms/call, {(alloc1 - alloc0) / (double)N:F1} B/call (non-empty={inSolid})");
        }

        Line("(numbers are informational — record significant regressions in the baseline comment atop this file)");
        Assert.True(sb.Length > 0);
    }

    private static void RunTraces(Action<string> line, TraceService trace, List<Vector3> seeds, Random rng,
        Vector3 mins, Vector3 maxs, float length, int count, string label)
    {
        // warmup (JIT + grid cache touch)
        for (int i = 0; i < 256; i++)
        {
            Vector3 s = seeds[i % seeds.Count];
            trace.Trace(s, mins, maxs, s + RandomDir(rng) * length, MoveFilter.Normal, null);
        }

        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            Vector3 s = seeds[i % seeds.Count];
            var res = trace.Trace(s, mins, maxs, s + RandomDir(rng) * length, MoveFilter.Normal, null);
            if (res.Fraction < 1f) hits++;
        }
        sw.Stop();
        long alloc1 = GC.GetAllocatedBytesForCurrentThread();

        line($"[{label}] {count} traces: {sw.Elapsed.TotalMilliseconds:F1} ms total, " +
             $"{sw.Elapsed.TotalMilliseconds / count:F4} ms/trace, " +
             $"{(alloc1 - alloc0) / (double)count:F1} B/trace (hit rate {(double)hits / count:P0})");
    }

    private static Vector3 RandomDir(Random rng)
    {
        // uniform-ish direction on the sphere (rejection-free: normalize a cube sample away from zero)
        while (true)
        {
            var v = new Vector3(
                (float)(rng.NextDouble() * 2.0 - 1.0),
                (float)(rng.NextDouble() * 2.0 - 1.0),
                (float)(rng.NextDouble() * 2.0 - 1.0));
            float len = v.Length();
            if (len > 0.05f) return v / len;
        }
    }
}
