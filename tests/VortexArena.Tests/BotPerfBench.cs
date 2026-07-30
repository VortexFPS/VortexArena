using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server.Bot;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Ad-hoc performance benchmark for the bot navigation hot path (T33 GC/alloc perf pass) — loads the REAL
/// atelier map collision + waypoint graph and times the pieces the havocbot think runs every strategy frame
/// (graph build, <see cref="WaypointNetwork.Nearest"/>, <see cref="BotTracewalk.CanWalk"/>,
/// <see cref="BotNavigation.SetGoal"/>, A* <see cref="WaypointNetwork.FindPath"/>). Not a CI assertion — it
/// no-ops where the content checkout is missing and writes a breakdown to a temp file + the test output.
///
/// Run: dotnet test tests/XonoticGodot.Tests --filter BotPerfBench -l "console;verbosity=detailed"
/// </summary>
[Collection("GlobalState")]
public class BotPerfBench
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";
    private const string Map = "atelier";
    private const string ReportPath = @"C:\Users\Bryan\AppData\Local\Temp\botbench.txt";

    private readonly ITestOutputHelper _out;
    public BotPerfBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Benchmark_AtelierBotNavigation()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }

        var sb = new StringBuilder();
        void Line(string s) { _out.WriteLine(s); sb.AppendLine(s); }

        // --- load the real map: BSP collision + entity lump + the shipped waypoints/cache ---
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountGameDir(DataDir));
        string bspPath = $"maps/{Map}.bsp";
        Assert.True(vfs.Exists(bspPath), $"missing {bspPath}");
        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        CollisionWorld world = BspCollisionBuilder.Build(bsp).World;

        var es = new EngineServices(world);
        es.Pvs = new BspPvs(bsp);
        GameInit.Boot(es); // installs Movement/registries so the full BotBrain.Think (Movement.Move) runs

        // spawn the map's gameplay entities (items + spawn points) into the table so role goal-rating + the
        // entity-derived auto-graph see them, exactly like the live server's spawn pass.
        var spawns = new List<Vector3>();
        int items = 0;
        foreach (var e in bsp.Entities)
        {
            if (!e.TryGetValue("classname", out string? cn) || cn is null) continue;
            if (!e.TryGetValue("origin", out string? os) || !TryVec(os, out Vector3 origin)) continue;
            Entity ent = es.EntityTable.Spawn();
            ent.ClassName = cn;
            ent.Origin = origin;
            if (cn.StartsWith("item_", StringComparison.Ordinal) || cn.StartsWith("weapon_", StringComparison.Ordinal)
                || cn.Contains("health", StringComparison.Ordinal) || cn.Contains("armor", StringComparison.Ordinal)
                || cn.Contains("ammo", StringComparison.Ordinal))
            { ent.Flags |= EntFlags.Item; items++; }
            if (cn.StartsWith("info_player", StringComparison.Ordinal)) spawns.Add(origin);
        }

        Line($"=== atelier bot-nav benchmark ===");
        Line($"bsp entities={bsp.Entities.Count}  spawned items={items}  spawn points={spawns.Count}");

        // --- graph build A: load the shipped .waypoints + .waypoints.cache (the 'correct' path) ---
        string wpText = vfs.Exists($"maps/{Map}.waypoints") ? vfs.ReadText($"maps/{Map}.waypoints") : "";
        string cacheText = vfs.Exists($"maps/{Map}.waypoints.cache") ? vfs.ReadText($"maps/{Map}.waypoints.cache") : "";
        Line($"waypoints file present={!string.IsNullOrWhiteSpace(wpText)}  cache present={!string.IsNullOrWhiteSpace(cacheText)}");

        var swA = Stopwatch.StartNew();
        WaypointNetwork netFile = WaypointNetwork.LoadFromText(wpText);
        if (!string.IsNullOrWhiteSpace(cacheText)) netFile.LoadLinks(cacheText);
        swA.Stop();
        int linkCountFile = CountLinks(netFile);
        Line($"[build A] LoadFromText + LoadLinks: {swA.Elapsed.TotalMilliseconds:F1} ms  nodes={netFile.Count} links={linkCountFile}");

        // --- graph build B: auto-generate from entities (GenerateFromEntities → AutoLink, O(N^2) tracewalks) ---
        var swB = Stopwatch.StartNew();
        var netAuto = new WaypointNetwork();
        int gen = netAuto.GenerateFromEntities(es.EntityTable.All, autoLink: true);
        swB.Stop();
        int linkCountAuto = CountLinks(netAuto);
        Line($"[build B] GenerateFromEntities + AutoLink: {swB.Elapsed.TotalMilliseconds:F1} ms  nodes={netAuto.Count} links={linkCountAuto}");

        // use the file-loaded graph (the realistic case) for the per-think probes.
        WaypointNetwork net = netFile.Count > 0 ? netFile : netAuto;
        var nodePts = new List<Vector3>();
        foreach (var wp in net.Nodes) nodePts.Add(wp.Center);
        Line($"probe graph: nodes={net.Count}");

        var mins = new Vector3(-16, -16, -24);
        var maxs = new Vector3(16, 16, 45);

        // --- probe 1: Nearest(pos) — the O(N) reachability scan (tracewalk per candidate) ---
        var sw1 = Stopwatch.StartNew();
        int found = 0;
        foreach (var p in nodePts) if (net.Nearest(p) is not null) found++;
        sw1.Stop();
        Line($"[Nearest] {nodePts.Count} calls: {sw1.Elapsed.TotalMilliseconds:F1} ms total, " +
             $"{sw1.Elapsed.TotalMilliseconds / Math.Max(1, nodePts.Count):F2} ms/call  (found={found})");

        // --- probe 2: CanWalk(a,b) — the tracewalk itself, on far-apart node pairs ---
        int pairs = 0; var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < nodePts.Count; i += 7)
            for (int j = 0; j < nodePts.Count; j += 13)
            { if (i == j) continue; BotTracewalk.CanWalk(nodePts[i], nodePts[j], mins, maxs); pairs++; }
        sw2.Stop();
        Line($"[CanWalk] {pairs} calls: {sw2.Elapsed.TotalMilliseconds:F1} ms total, " +
             $"{sw2.Elapsed.TotalMilliseconds / Math.Max(1, pairs):F3} ms/call");

        // --- probe 3: SetGoal(origin, goal) — the per-strategy-frame route plan (Nearest x2 + CanWalkStraight + A*) ---
        var nav = new BotNavigation { Mins = mins, Maxs = maxs };
        int plans = 0; var sw3 = Stopwatch.StartNew();
        for (int i = 0; i < nodePts.Count; i += 3)
        {
            Vector3 origin = nodePts[i];
            Vector3 goal = nodePts[(i + nodePts.Count / 2) % nodePts.Count]; // far side → forces A* (no straight walk)
            nav.SetGoal(origin, goal, net, null);
            plans++;
        }
        sw3.Stop();
        Line($"[SetGoal] {plans} calls: {sw3.Elapsed.TotalMilliseconds:F1} ms total, " +
             $"{sw3.Elapsed.TotalMilliseconds / Math.Max(1, plans):F2} ms/call");

        // --- probe 4: FindPath(a,b) — A* alone, on the file graph ---
        int aStar = 0; var sw4 = Stopwatch.StartNew();
        var nodes = net.Nodes;
        for (int i = 0; i < nodes.Count; i += 3)
        {
            var to = nodes[(i + nodes.Count / 2) % nodes.Count];
            net.FindPath(nodes[i], to);
            aStar++;
        }
        sw4.Stop();
        Line($"[FindPath] {aStar} calls: {sw4.Elapsed.TotalMilliseconds:F1} ms total, " +
             $"{sw4.Elapsed.TotalMilliseconds / Math.Max(1, aStar):F3} ms/call");

        // --- amortized per-bot-per-tick estimate: strategy clock replans ~every 1.5 s (skill 5) = ~108 ticks ---
        double setGoalMs = sw3.Elapsed.TotalMilliseconds / Math.Max(1, plans);
        double perTick = setGoalMs / 108.0;
        Line($"=> amortized SetGoal cost per bot/tick (1 replan / 108 ticks): {perTick:F3} ms");
        Line($"=> if a bot replanned EVERY tick: {setGoalMs:F2} ms/bot/tick");

        // --- probe 5: full end-to-end BotBrain.Think for N bots over T ticks (the real per-tick path) ---
        try
        {
            const int N = 4;
            const int T = 72 * 20; // 20 sim-seconds
            const float dt = 1f / 72f;
            var brains = new List<BotBrain>();
            for (int i = 0; i < N; i++)
            {
                var p = new Player { NetName = $"[BOT] {i}", IsBot = true, ClassName = "player" };
                p.Index = -1000 - i;
                p.Mins = mins; p.Maxs = maxs;
                p.ViewOfs = new Vector3(0, 0, 35);
                p.Origin = spawns.Count > 0 ? spawns[i % spawns.Count] + new Vector3(0, 0, 4) : nodePts[i % nodePts.Count];
                p.Health = 100; p.MaxHealth = 100;
                p.MoveType = MoveType.Walk;
                p.Solid = Solid.SlideBox;
                foreach (var w in SpawnSystem.DefaultLoadout) p.OwnedWeapons.Add(w);
                var brain = new BotBrain(p, net, skill: 5f, seed: i + 1);
                brains.Add(brain);
            }
            var roster = new List<Player>();
            foreach (var b in brains) roster.Add((Player)b.Bot);
            foreach (var b in brains) b.PlayerProvider = () => roster;

            // warm up (JIT the think path) then time
            for (int i = 0; i < brains.Count; i++) brains[i].Think((Player)brains[i].Bot, dt);

            // advance the (internally-settable) clock via reflection so the strategy/enemy/weapon throttles fire
            var timeProp = typeof(GameClock).GetProperty("Time")!;
            var ftProp = typeof(GameClock).GetProperty("FrameTime")!;
            ftProp.SetValue(es.ClockImpl, dt);

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw5 = Stopwatch.StartNew();
            for (int t = 0; t < T; t++)
            {
                timeProp.SetValue(es.ClockImpl, t * dt);
                for (int i = 0; i < brains.Count; i++)
                    brains[i].Think((Player)brains[i].Bot, dt);
            }
            sw5.Stop();
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            double totalThinks = (double)T * N;
            Line($"[Think] {N} bots x {T} ticks ({totalThinks:F0} thinks): {sw5.Elapsed.TotalMilliseconds:F0} ms total");
            Line($"=> {sw5.Elapsed.TotalMilliseconds / totalThinks:F4} ms/bot/tick   ({sw5.Elapsed.TotalMilliseconds / T:F3} ms/tick for {N} bots)");
            Line($"=> alloc {(allocAfter - allocBefore) / 1024.0 / 1024.0:F1} MB over the run  ({(allocAfter - allocBefore) / totalThinks:F0} B/think)");
        }
        catch (Exception ex)
        {
            Line($"[Think] end-to-end probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        File.WriteAllText(ReportPath, sb.ToString());
        Line($"(report written to {ReportPath})");
    }

    private static int CountLinks(WaypointNetwork net)
    {
        int n = 0; foreach (var wp in net.Nodes) n += wp.Links.Count; return n;
    }

    private static bool TryVec(string s, out Vector3 v)
    {
        v = default;
        string t = s.Trim();
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'') t = t[1..^1];
        var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!float.TryParse(parts[0].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(parts[1].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        if (!float.TryParse(parts[2].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }
}
