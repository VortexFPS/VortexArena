using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using XonoticGodot.Common.Physics;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Server;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// T33 perf bench (BotPerfBench pattern — measurement, not a CI assertion) for the WHOLE headless
/// server tick: boot a real <see cref="GameWorld"/> on atelier (full entity lump + real cfg tree, the
/// same construction `game/net/NetGame.StartListenServer` does), then time <see cref="GameWorld.Frame"/>
/// across 72 Hz ticks — first with an empty roster (the idle dedicated-server floor), then with 4
/// running/jumping players (the per-player movement + PreThink/PostThink + trigger cost). ms/tick and
/// B/tick here are THE dedicated-server capacity numbers.
///
/// No-ops when the content checkout is missing (CI without assets); the data dir can be overridden
/// with the XG_DATA_DIR environment variable.
///
/// Run: dotnet test tests/XonoticGodot.Tests --filter ServerTickPerfBench -l "console;verbosity=detailed"
///
/// Measured baseline (2026-06-09, dev machine, Debug build, atelier — 453 map entities):
///   boot (cfg tree + registries + spawn): 172 ms
///   empty world:  0.118 ms/tick, ~32 KB/tick allocated (the idle tick's GC floor — StartFrame/
///                 EndFrame hook plumbing + entity think scans; the top dedicated-server GC target)
///   4 players:    0.622 ms/tick, ~59 KB/tick (≈0.126 ms + ~6.8 KB marginal per player-tick;
///                 4.5% of the 13.9 ms budget at 72 Hz — CPU headroom for 16+ players, but the
///                 ~2.5 MB/s steady allocation rate (gen0 churn) is worth a dotnet-counters pass)
/// </summary>
[Collection("GlobalState")]
public class ServerTickPerfBench
{
    private static readonly string DataDir =
        Environment.GetEnvironmentVariable("XG_DATA_DIR")
        ?? @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";
    private const string Map = "atelier";

    private readonly ITestOutputHelper _out;
    public ServerTickPerfBench(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A mutable, class-based input (no per-call boxing — a struct returned through
    /// <see cref="IMovementInput"/> would allocate every tick and pollute the B/tick numbers).
    /// </summary>
    private sealed class BenchInput : IMovementInput
    {
        public Vector3 ViewAngles { get; set; }
        public Vector3 MoveValues { get; set; }
        public float FrameTime { get; set; } = SimulationLoop.TicRate;
        public bool ButtonJump { get; set; }
        public bool ButtonCrouch => false;
        public bool ButtonUse => false;
        public bool ButtonAttack1 => false;
        public bool ButtonAttack2 => false;
    }

    [Fact]
    public void Benchmark_AtelierServerTick()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }

        var sb = new StringBuilder();
        void Line(string s) { _out.WriteLine(s); sb.AppendLine(s); }

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
        world.ConfigReader = path => vfs.Exists(path) ? vfs.ReadText(path) : null; // the real cfg tree

        var swBoot = Stopwatch.StartNew();
        world.Boot("dm");
        swBoot.Stop();
        world.Services.Cvars.Set("sv_spectate", "0"); // players spawn immediately (the listen-server setting)

        Line($"=== atelier server-tick benchmark ===");
        Line($"[boot] GameWorld.Boot(dm): {swBoot.Elapsed.TotalMilliseconds:F0} ms " +
             $"(cfg tree + registries + {bsp.Entities.Count} map entities)");

        const float dt = SimulationLoop.TicRate;

        // --- phase A: empty world — the idle dedicated-server floor (item respawn timers, triggers, hooks) ---
        for (int t = 0; t < 72; t++) world.Frame(dt); // warmup/JIT
        MeasurePhase(Line, world, ticks: 72 * 10, label: "empty world (0 players)");

        // --- connect 4 human players and drive them with real movement input ---
        var inputs = new BenchInput[4];
        var players = new XonoticGodot.Common.Gameplay.Player[4];
        for (int i = 0; i < 4; i++)
        {
            inputs[i] = new BenchInput();
            ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: $"perf{i}");
            players[i] = info.Player;
            world.Clients.Join(info.Player); // straight into the match (sv_spectate 0 path)
        }
        int tick = 0;
        world.InputProvider = p =>
        {
            for (int i = 0; i < players.Length; i++)
            {
                if (!ReferenceEquals(players[i], p)) continue;
                BenchInput inp = inputs[i];
                // run forward while slowly turning (roams the map; QC movement is maxspeed-scaled wishspeed)
                inp.ViewAngles = new Vector3(0f, (i * 90f + tick * 0.6f) % 360f, 0f);
                inp.MoveValues = new Vector3(360f, 0f, 0f);
                inp.ButtonJump = ((tick + i * 16) & 63) < 2; // a hop every ~0.9 s, staggered per player
                return inp;
            }
            return new BenchInput(); // a stray (non-bench) player — zero input
        };

        for (int t = 0; t < 72 * 2; t++) { tick++; world.Frame(dt); } // settle: spawn + land + start running
        int alive = 0;
        foreach (var p in players) if (!p.IsDead && !p.IsObserver) alive++;
        Line($"[roster] 4 players connected, {alive} alive after the settle window");

        // --- phase B: 4 players running + jumping — the per-player tick cost ---
        double msB = MeasurePhase(Line, world, ticks: 72 * 20, label: "4 players running", advanceTick: () => tick++);
        Line($"=> per-player marginal cost ≈ {(msB - _emptyMsPerTick) / 4.0:F4} ms/player/tick " +
             $"(vs the empty-world floor {_emptyMsPerTick:F4} ms/tick)");

        Line("(numbers are informational — record significant regressions in the baseline comment atop this file)");

        // (P9 2026-07-03) Loose budget gates: catch a 2×+ tick regression (the 2026-07-02 bot-strategy melt
        // class) before a human feels it, without tripping on machine noise — the thresholds are ~4-5× the
        // 2026-06-09 Debug baselines documented above. A slower box can opt out with XG_PERF_ASSERT=0.
        if (Environment.GetEnvironmentVariable("XG_PERF_ASSERT") != "0")
        {
            Assert.True(_emptyMsPerTick < 1.0,
                $"empty-world server tick regressed: {_emptyMsPerTick:F4} ms/tick (budget 1.0, baseline 0.118)");
            Assert.True(msB < 3.0,
                $"4-player server tick regressed: {msB:F4} ms/tick (budget 3.0, baseline 0.622)");
        }
        Assert.True(sb.Length > 0);
    }

    private double _emptyMsPerTick;

    private double MeasurePhase(Action<string> line, GameWorld world, int ticks, string label,
        Action? advanceTick = null)
    {
        const float dt = SimulationLoop.TicRate;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < ticks; t++)
        {
            advanceTick?.Invoke();
            world.Frame(dt);
        }
        sw.Stop();
        long alloc1 = GC.GetAllocatedBytesForCurrentThread();

        double msPerTick = sw.Elapsed.TotalMilliseconds / ticks;
        line($"[{label}] {ticks} ticks: {sw.Elapsed.TotalMilliseconds:F0} ms total, " +
             $"{msPerTick:F4} ms/tick, {(alloc1 - alloc0) / (double)ticks:F0} B/tick " +
             $"({msPerTick / (1000.0 / 72.0):P1} of the 72 Hz budget)");
        if (label.StartsWith("empty", StringComparison.Ordinal)) _emptyMsPerTick = msPerTick;
        return msPerTick;
    }

    /// <summary>
    /// Parse the BSP entity lump into <see cref="EntityDict"/>s — a test-side mirror of
    /// `game/net/NetGame.BuildEntityDicts` (game/ is not visible to tests).
    /// </summary>
    private static List<EntityDict> BuildEntityDicts(BspData bsp)
    {
        var list = new List<EntityDict>(bsp.Entities.Count);
        foreach (IReadOnlyDictionary<string, string> dict in bsp.Entities)
        {
            if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                continue;
            var ed = new EntityDict
            {
                ClassName = cls,
                Origin = ParseVec(dict, "origin"),
                Angles = ParseVec(dict, "angles"),
            };
            foreach (KeyValuePair<string, string> kv in dict)
                ed.Fields[kv.Key] = kv.Value;
            list.Add(ed);
        }
        return list;
    }

    private static Vector3 ParseVec(IReadOnlyDictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s))
            return Vector3.Zero;
        string[] p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return Vector3.Zero;
        return new Vector3(ParseF(p[0]), ParseF(p[1]), ParseF(p[2]));
    }

    private static float ParseF(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
}
