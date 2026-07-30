using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Server;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Bot-load server-tick benchmark — the deterministic harness for the "NetGame hitches with bots, GameDemo
/// doesn't" investigation. GameDemo runs ONE local player and no server; NetGame runs the full headless
/// <see cref="GameWorld"/> tick INCLUDING the bot AI (A* pathfinding + per-tick havocbot think), which is the
/// suspect. This boots a real world on a map, adds N brained bots, lets them spawn + roam, then times
/// <see cref="GameWorld.Frame"/> across many 72 Hz ticks — reporting not just the average but the
/// <b>p99 and MAX</b> tick (a hitch IS the worst tick — a single A* replan or GC pause — not the mean) plus the
/// <see cref="Prof"/> per-scope breakdown of the worst tick, so it attributes the spike.
///
/// <para>Parameterised by env vars so it doubles as an experiment harness — change something, re-run, compare:
/// <c>XG_BOTS</c> (default 6), <c>XG_MAP</c> (stormkeep), <c>XG_TICKS</c> (72*30). Run:
/// <c>XG_BOTS=8 dotnet test tests/XonoticGodot.Tests --filter BotTickPerfBench -l "console;verbosity=detailed"</c>.
/// Skips without the content checkout (<c>XG_DATA_DIR</c> overrides the path).</para>
/// </summary>
[Collection("GlobalState")]
public class BotTickPerfBench
{
    private static readonly string DataDir =
        Environment.GetEnvironmentVariable("XG_DATA_DIR")
        ?? @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";
    private static string Map => Environment.GetEnvironmentVariable("XG_MAP") ?? "stormkeep";
    private static int BotCount =>
        int.TryParse(Environment.GetEnvironmentVariable("XG_BOTS"), out int n) ? n : 6;
    private static int BenchTicks =>
        int.TryParse(Environment.GetEnvironmentVariable("XG_TICKS"), out int n) ? n : 72 * 30;

    private readonly ITestOutputHelper _out;
    public BotTickPerfBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Benchmark_BotServerTick()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        void Line(string s) => _out.WriteLine(s);

        // --- build the world exactly like the live listen server (NetGame.StartListenServer) ---
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountGameDir(DataDir));
        string bspPath = $"maps/{Map}.bsp";
        Assert.True(vfs.Exists(bspPath), $"missing {bspPath}");
        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp);
        var world = new GameWorld(built.World, BuildEntityDicts(bsp)) { MapName = Map };
        world.BrushModels = built.Submodels;
        world.MapBsp = bsp;
        world.Pvs = new BspPvs(bsp);
        world.ConfigReader = path => vfs.Exists(path) ? vfs.ReadText(path) : null;
        world.Boot("dm");
        world.Services.Cvars.Set("sv_spectate", "0");
        // bot_join_empty 1 keeps bots on a human-less server (else fixcount trims them); bot_number is the target.
        world.Services.Cvars.Set("bot_join_empty", "1");
        world.Services.Cvars.Set("bot_number", BotCount.ToString(CultureInfo.InvariantCulture));
        world.Services.Cvars.Set("skill", "5");

        const float dt = SimulationLoop.TicRate;

        // Let the live fixcount fill the roster (one bot/frame after the 2.5 s sentinel), then spawn, path, and
        // start roaming + fighting so the measured window is representative of live play (and JIT-warm). ~14 s.
        for (int t = 0; t < 72 * 14; t++) world.Frame(dt);

        Line($"=== bot server-tick bench: map={Map}, bots={world.Clients.BotCount}, ticks={BenchTicks} ===");

        // --- measure ---
        Prof.Enabled = true;
        int ticks = BenchTicks;
        var tickMs = new double[ticks];
        var totals = new Dictionary<string, double>();
        var allocTotals = new Dictionary<string, double>();
        var frameScopes = new Dictionary<string, double>();
        var frameCounters = new Dictionary<string, double>();
        var frameAlloc = new Dictionary<string, double>();
        Dictionary<string, double>? worstScopes = null;
        double worstMs = 0;

        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        int g0a = GC.CollectionCount(0), g1a = GC.CollectionCount(1), g2a = GC.CollectionCount(2);

        for (int t = 0; t < ticks; t++)
        {
            long start = Stopwatch.GetTimestamp();
            world.Frame(dt);
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            tickMs[t] = ms;

            Prof.SnapshotAndReset(frameScopes, frameCounters, frameAlloc);
            foreach (KeyValuePair<string, double> kv in frameScopes)
                totals[kv.Key] = (totals.TryGetValue(kv.Key, out double v) ? v : 0.0) + kv.Value;
            foreach (KeyValuePair<string, double> kv in frameAlloc)
                allocTotals[kv.Key] = (allocTotals.TryGetValue(kv.Key, out double v) ? v : 0.0) + kv.Value;
            if (ms > worstMs) { worstMs = ms; worstScopes = new Dictionary<string, double>(frameScopes); }
        }
        Prof.Enabled = false;

        long allocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        int dg0 = GC.CollectionCount(0) - g0a, dg1 = GC.CollectionCount(1) - g1a, dg2 = GC.CollectionCount(2) - g2a;

        var sorted = (double[])tickMs.Clone();
        Array.Sort(sorted);
        double med = sorted[ticks / 2];
        double p99 = sorted[Math.Min(ticks - 1, (int)(ticks * 0.99))];
        double max = sorted[ticks - 1];
        const double budget = 1000.0 / 72.0;
        int overBudget = tickMs.Count(m => m > budget);

        Line($"per-tick: med {med:F3}ms  p99 {p99:F3}ms  MAX {max:F3}ms  (72 Hz budget {budget:F2}ms; " +
             $"{overBudget}/{ticks} ticks over budget = {overBudget / (double)ticks:P1})");
        Line($"alloc: {allocBytes / (double)ticks:F0} B/tick, {allocBytes / 1024.0 / 1024.0:F1} MB over {ticks} ticks " +
             $"| GC gen0+{dg0} gen1+{dg1} gen2+{dg2}");
        Line($"top scopes (avg ms/tick): {Top(totals, ticks, 10)}");
        Line($"top alloc scopes (avg B/tick): {TopBytes(allocTotals, ticks, 10)}");
        if (worstScopes is not null)
            Line($"WORST tick ({worstMs:F2}ms) attribution: {Top(worstScopes, 1, 10)}");
        Line("(informational; a big MAX vs med = the hitch. A large gen2+ = GC pauses. Compare runs after a change.)");

        Assert.True(ticks > 0);
    }

    private static string Top(Dictionary<string, double> d, int divisor, int k)
        => string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(k)
            .Where(kv => kv.Value / divisor > 0.0005)
            .Select(kv => $"{kv.Key} {kv.Value / divisor:F3}"));

    private static string TopBytes(Dictionary<string, double> d, int divisor, int k)
        => string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(k)
            .Where(kv => kv.Value / divisor > 8)
            .Select(kv => $"{kv.Key} {kv.Value / divisor:F0}B"));

    // --- test-side mirror of game/net/NetGame.BuildEntityDicts (game/ is not visible to tests) ---
    private static List<EntityDict> BuildEntityDicts(BspData bsp)
    {
        var list = new List<EntityDict>(bsp.Entities.Count);
        foreach (IReadOnlyDictionary<string, string> dict in bsp.Entities)
        {
            if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                continue;
            var ed = new EntityDict { ClassName = cls, Origin = ParseVec(dict, "origin"), Angles = ParseVec(dict, "angles") };
            foreach (KeyValuePair<string, string> kv in dict)
                ed.Fields[kv.Key] = kv.Value;
            list.Add(ed);
        }
        return list;
    }

    private static Vector3 ParseVec(IReadOnlyDictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s)) return Vector3.Zero;
        string[] p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return p.Length < 3 ? Vector3.Zero : new Vector3(ParseF(p[0]), ParseF(p[1]), ParseF(p[2]));
    }

    private static float ParseF(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
}
