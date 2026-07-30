using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Server;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests.Perf;

/// <summary>
/// What map visibility is WORTH, so the backlog's P4 can be decided rather than assumed.
///
/// A BSP supplied rendering visibility and gameplay visibility from one structure, so they read as one
/// problem. They are two, they have different consumers, and the cheap answer for each may be different from
/// the expensive one that covers both. This measures each separately.
///
/// <list type="number">
/// <item><b>Gameplay.</b> <c>CheckPvs</c> is <c>Pvs?.IsInPvs(..) ?? true</c> — a conservative pre-filter in
/// front of exact traces, used by bot targeting, spawn scoring, monsters and turrets. Running the same bot
/// server tick with the PVS wired and unwired measures what it saves. If the answer is "little", a coarse
/// cell-visibility bitset is more than enough and a portal-flood vis compiler is wasted work.</item>
/// <item><b>Rendering.</b> Godot frustum-culls per <c>MeshInstance3D</c>, and the world is built as one
/// instance per (1024-unit cell, material). Counting those says what PVS would even be culling — a number
/// small enough makes the question moot before any occluder work starts.</item>
/// </list>
///
/// <para>Skips without the content checkout (<c>VA_DATA_DIR</c>). <c>XG_MAP</c>, <c>XG_BOTS</c>,
/// <c>XG_TICKS</c> parameterise it.</para>
/// </summary>
[Collection("GlobalState")]
public class VisibilityValueBench
{
    private static readonly string DataDir = TestPaths.Data;

    /// <summary>
    /// Explicit opt-in, because these are experiments rather than assertions and the default content path is
    /// ABSOLUTE — on a machine with the assets checked out they would otherwise run in full on every
    /// <c>ci.sh</c>, which is minutes of gate time to re-measure something nobody asked about.
    /// Run with <c>XG_BENCH=1</c>.
    /// </summary>
    private static bool Enabled => Environment.GetEnvironmentVariable("XG_BENCH") is { Length: > 0 };

    private static string Map => Environment.GetEnvironmentVariable("XG_MAP") ?? "stormkeep";
    private static int BotCount =>
        int.TryParse(Environment.GetEnvironmentVariable("XG_BOTS"), out int n) ? n : 8;
    private static int BenchTicks =>
        int.TryParse(Environment.GetEnvironmentVariable("XG_TICKS"), out int n) ? n : 72 * 20;

    private readonly ITestOutputHelper _out;
    public VisibilityValueBench(ITestOutputHelper output) => _out = output;

    // ---------------------------------------------------------------- gameplay half

    [Fact]
    public void Benchmark_GameplayPvsValue()
    {
        if (!Enabled) { _out.WriteLine("bench — set XG_BENCH=1 to run"); return; }
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        void Line(string s) => _out.WriteLine(s);

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));
        string bspPath = $"maps/{Map}.bsp";
        if (!vfs.Exists(bspPath)) { Line($"{bspPath} missing — skipped"); return; }
        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));

        Line($"=== gameplay PVS value: map={Map}, bots={BotCount}, ticks={BenchTicks} ===");

        (double med, double p99, double max) with = RunTicks(bsp, vfs, usePvs: true);
        (double med, double p99, double max) without = RunTicks(bsp, vfs, usePvs: false);

        Line($"  PVS wired   : med {with.med:F3}ms  p99 {with.p99:F3}ms  max {with.max:F3}ms");
        Line($"  PVS unwired : med {without.med:F3}ms  p99 {without.p99:F3}ms  max {without.max:F3}ms");
        Line($"  delta (med) : {without.med - with.med:+0.000;-0.000;0.000}ms "
             + $"({(with.med > 0 ? (without.med - with.med) / with.med : 0):+0.0%;-0.0%;0.0%})");
        Line("");
        Line("NOTE: this timing is dominated by run-to-run variance — the sign of the delta is not stable");
        Line("across repeats. Read the rejection rate below instead; it is deterministic.");

        Assert.True(with.med > 0);
    }

    /// <summary>
    /// How often PVS actually says NO — the number that decides P4, and unlike a timing it does not move
    /// between runs.
    ///
    /// <c>CheckPvs</c> only earns its cost when it rejects: a query it passes is a trace that happens anyway
    /// plus the lookup that preceded it. Sampled over the positions gameplay genuinely asks about — spawn
    /// points and bot waypoints, which is where <c>SpawnSystem</c> and the bot roles query from — rather than
    /// uniformly over the bounding box, which would over-count pairs separated by solid rock that no gameplay
    /// code ever compares.
    /// </summary>
    [Fact]
    public void Benchmark_PvsRejectionRate()
    {
        if (!Enabled) { _out.WriteLine("bench — set XG_BENCH=1 to run"); return; }
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        void Line(string s) => _out.WriteLine(s);

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));

        Line("How often PVS rejects a gameplay visibility query (deterministic — no timing noise).");
        Line($"{"map",-12} {"points",7} {"pairs",9} {"rejected",9} {"rate",7}");
        Line(new string('-', 48));

        foreach (string map in (Environment.GetEnvironmentVariable("XG_MAPS") ?? "stormkeep,fuse,catharsis")
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string bspPath = $"maps/{map}.bsp";
            if (!vfs.Exists(bspPath)) { Line($"{map,-12} (missing)"); continue; }

            BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
            var pvs = new BspPvs(bsp);

            // Eye-height points at the map's own gameplay positions.
            var points = new List<Vector3>();
            foreach (IReadOnlyDictionary<string, string> e in bsp.Entities)
            {
                if (!e.TryGetValue("classname", out string? cls))
                    continue;
                if (!cls.StartsWith("info_player_", StringComparison.Ordinal)
                    && !cls.StartsWith("item_", StringComparison.Ordinal)
                    && !cls.StartsWith("weapon_", StringComparison.Ordinal))
                    continue;
                Vector3 o = ParseVec(e, "origin");
                if (o != Vector3.Zero)
                    points.Add(o + new Vector3(0, 0, 40f));
            }

            if (points.Count < 2) { Line($"{map,-12} (no gameplay points)"); continue; }

            int pairs = 0, rejected = 0;
            for (int i = 0; i < points.Count; i++)
                for (int j = i + 1; j < points.Count; j++)
                {
                    pairs++;
                    if (!pvs.IsInPvs(points[i], points[j]))
                        rejected++;
                }

            Line($"{map,-12} {points.Count,7} {pairs,9} {rejected,9} {rejected / (double)pairs,7:P1}");
        }

        Line("");
        Line("A LOW rate means PVS passes nearly every query it is asked, so it is a lookup in front of a");
        Line("trace that runs anyway — and a coarse cell bitset would reject the same obvious cases.");
    }

    private static (double Med, double P99, double Max) RunTicks(
        BspData bsp, VirtualFileSystem vfs, bool usePvs)
    {
        BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp);
        var world = new GameWorld(built.World, BuildEntityDicts(bsp)) { MapName = Map };
        world.BrushModels = built.Submodels;
        world.MapBsp = bsp;

        // The one variable. Null leaves CheckPvs returning true for every query, which is precisely the
        // "no baked visibility" case the backlog is deciding about.
        world.Pvs = usePvs ? new BspPvs(bsp) : null;

        world.ConfigReader = path => vfs.Exists(path) ? vfs.ReadText(path) : null;
        world.Boot("dm");
        world.Services.Cvars.Set("sv_spectate", "0");
        world.Services.Cvars.Set("bot_join_empty", "1");
        world.Services.Cvars.Set("bot_number", BotCount.ToString(CultureInfo.InvariantCulture));
        world.Services.Cvars.Set("skill", "5");

        const float dt = SimulationLoop.TicRate;
        for (int t = 0; t < 72 * 14; t++) world.Frame(dt);   // fill the roster, spawn, path, start fighting

        int ticks = BenchTicks;
        var tickMs = new double[ticks];
        for (int t = 0; t < ticks; t++)
        {
            long start = Stopwatch.GetTimestamp();
            world.Frame(dt);
            tickMs[t] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(tickMs);
        return (tickMs[ticks / 2], tickMs[Math.Min(ticks - 1, (int)(ticks * 0.99))], tickMs[ticks - 1]);
    }

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
        if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s)) return Vector3.Zero;
        string[] p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return Vector3.Zero;
        return new Vector3(ParseF(p[0]), ParseF(p[1]), ParseF(p[2]));
    }

    private static float ParseF(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    // ---------------------------------------------------------------- rendering half

    [Fact]
    public void Benchmark_RenderInstanceCount()
    {
        if (!Enabled) { _out.WriteLine("bench — set XG_BENCH=1 to run"); return; }
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        void Line(string s) => _out.WriteLine(s);

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));

        Line("What PVS would be culling: the world is one MeshInstance3D per (1024-unit cell, material).");
        Line($"{"map",-12} {"instances",10} {"cells",7} {"materials",10} {"tris",10} {"tris/inst",10}");
        Line(new string('-', 64));

        foreach (string map in (Environment.GetEnvironmentVariable("XG_MAPS") ?? "stormkeep,fuse,catharsis")
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string bspPath = $"maps/{map}.bsp";
            if (!vfs.Exists(bspPath)) { Line($"{map,-12} (missing)"); continue; }

            BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
            VmapDocument doc = BspToVmap.Import(bsp, map, bspPath, sourceHash: "");
            IReadOnlyList<VmapSurface> surfaces =
                VmapGeometryBuilder.BuildSurfaces(doc, new VmapSurfaceOptions());

            const float cellSize = 1024f;      // VmapMapBuilder.CellSize
            var instances = new HashSet<(int, int, int, string)>();
            var cells = new HashSet<(int, int, int)>();
            int tris = 0;

            foreach (VmapSurface s in surfaces)
            {
                for (int i = 0; i + 2 < s.Indices.Count; i += 3)
                {
                    // Cell of the triangle's first corner — the same bucketing the builder does.
                    Vector3 p = s.Positions[s.Indices[i]];
                    var cell = ((int)MathF.Floor(p.X / cellSize),
                                (int)MathF.Floor(p.Y / cellSize),
                                (int)MathF.Floor(p.Z / cellSize));
                    cells.Add(cell);
                    instances.Add((cell.Item1, cell.Item2, cell.Item3, s.Material));
                    tris++;
                }
            }

            Line($"{map,-12} {instances.Count,10} {cells.Count,7} {surfaces.Count,10} {tris,10} "
                 + $"{(instances.Count > 0 ? tris / instances.Count : 0),10}");
        }

        Line("");
        Line("Godot frustum-culls these per instance every frame, and occlusion-culls them when");
        Line("r_occlusion_cull is on. PVS would be a third filter over the same list.");
    }
}
