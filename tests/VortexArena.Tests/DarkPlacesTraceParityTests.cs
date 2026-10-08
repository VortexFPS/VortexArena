using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;
using VortexArena.Tests.Legacy;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Traces answered by a real DarkPlaces server, against the same traces answered here.
///
/// The files in <c>golden/dp-traces/</c> were recorded from the reference dedicated server
/// (<c>../Base/darkplaces</c>) with a console command added to a private copy of it for the purpose
/// ("probetraces": read traces from a file, write SV_TraceBox's answers to another; the patch, the build
/// script and the generators are in <c>_scratch/collision-probe/</c>). Three kinds of file:
/// <c>patch-*</c> - world-only traces over a map's curved surfaces (drops, slides, walks, lines from both
/// sides); <c>world-*</c> - world-only traces seeded from where a map's items and waypoints are;
/// <c>barrels-catharsis</c> - MOVE_NORMAL traces around two turned SOLID_BSP barrels.
///
/// A trace is "exact" when the end position is the same three floats and the solid flags agree, "close"
/// when it ends within 0.01 units, and otherwise it differs. Every test needs the maps (<c>../Base</c>)
/// and returns early without them.
/// </summary>
public class DarkPlacesTraceParityTests
{
    private readonly ITestOutputHelper _output;
    public DarkPlacesTraceParityTests(ITestOutputHelper output) => _output = output;

    private sealed record Golden(string Kind, int Type, Vector3 Start, Vector3 Mins, Vector3 Maxs, Vector3 End, float Extend, int Mask,
        float Fraction, Vector3 EndPos, bool StartSolid, bool AllSolid, Vector3 Normal);

    private static string GoldenDir([CallerFilePath] string thisFile = "") => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden", "dp-traces");

    private static List<Golden> Load(string name, out string map)
    {
        map = "";
        List<Golden> traces = new();
        foreach (string line in File.ReadLines(Path.Combine(GoldenDir(), name + ".txt")))
        {
            if (line.StartsWith('#'))
            {
                int at = line.IndexOf("map=", StringComparison.Ordinal);
                if (at >= 0) map = line[(at + 4)..].Trim();
                continue;
            }
            string[] parts = line.Split('|');
            if (parts.Length != 3) continue;
            float[] a = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            string[] o = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // the reference prints doubles; what a program sees, and what is compared, is the nearest float
            float F(int i) => (float)double.Parse(o[i], CultureInfo.InvariantCulture);
            traces.Add(new Golden(parts[0], (int)a[0], new Vector3(a[2], a[3], a[4]), new Vector3(a[5], a[6], a[7]), new Vector3(a[8], a[9], a[10]),
                new Vector3(a[11], a[12], a[13]), a[14], (int)a[15], F(0), new Vector3(F(1), F(2), F(3)), o[4] != "0", o[5] != "0", new Vector3(F(12), F(13), F(14))));
        }
        return traces;
    }

    private sealed class Tally
    {
        public int Total, Exact, Close, Differ;
        public double Worst;
        public readonly SortedDictionary<string, int> DifferByKind = new(StringComparer.Ordinal);
        public void Add(Golden g, float fraction, Vector3 endPos, bool startSolid, bool allSolid)
        {
            Total++;
            double distance = Vector3.Distance(g.EndPos, endPos);
            bool flags = g.StartSolid == startSolid && g.AllSolid == allSolid;
            if (distance == 0 && flags && MathF.Abs(g.Fraction - fraction) <= 1e-6f) Exact++;
            else if (distance < 0.01 && flags) Close++;
            else
            {
                Differ++;
                Worst = Math.Max(Worst, distance);
                DifferByKind[g.Kind] = DifferByKind.GetValueOrDefault(g.Kind) + 1;
            }
        }
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Total} traces: {Exact} exact ({100.0 * Exact / Math.Max(1, Total):0.0}%), {Close} close, {Differ} differ (worst {Worst:0.###})")
            + (Differ > 0 ? " [" + string.Join(", ", DifferByKind.Select(p => $"{p.Key} {p.Value}")) + "]" : "");
    }

    private static readonly string[] WorldSets =
    {
        "patch-vorix", "patch-glowplant", "patch-afterslime", "patch-courtfun", "patch-stormkeep", "world-stormkeep", "world-xoylent", "world-catharsis",
    };

    [Fact]
    public void Legacy_mode_answers_world_traces_as_DarkPlaces_does()
    {
        if (!ServerTestRig.HaveData) return;
        using SvEnvironment env = new(TestPaths.BaseData, writeRoot: null);
        Tally all = new();
        foreach (string set in WorldSets)
        {
            List<Golden> traces = Load(set, out string map);
            SvWorld world = new(env.Files);
            Assert.True(world.LoadMap($"maps/{map}.bsp"), world.LoadError);
            Tally tally = new();
            foreach (Golden g in traces)
            {
                SvTrace t = world.Trace(Q(g.Start), Q(g.Mins), Q(g.Maxs), Q(g.End), g.Type, 0, g.Mask, g.Extend);
                Vector3 end = new(t.EndPos.X, t.EndPos.Y, t.EndPos.Z);
                tally.Add(g, t.Fraction, end, t.StartSolid, t.AllSolid);
                all.Add(g, t.Fraction, end, t.StartSolid, t.AllSolid);
            }
            _output.WriteLine($"{set,-18} {tally}");
            Assert.True(tally.Differ == 0, $"{set}: {tally}");
            Assert.True(tally.Exact >= tally.Total * 0.9, $"{set}: {tally}");
        }
        _output.WriteLine($"{"all",-18} {all}");
    }

    private sealed record Config(string Name, BspCollisionOptions? Options, bool Arithmetic);

    private static Config[] Configs(ReadOnlyMemory<byte> file) => new[]
    {
        new Config("native default (slabs, grid)", new BspCollisionOptions(), false),
        new Config("DarkPlaces triangles", new BspCollisionOptions { PatchCollision = PatchCollisionMode.DarkPlacesTriangles, MapFile = file }, false),
        new Config("triangles + hierarchy", new BspCollisionOptions { PatchCollision = PatchCollisionMode.DarkPlacesTriangles, Broadphase = CollisionBroadphase.Bih, MapFile = file }, false),
        new Config("triangles + hierarchy + DarkPlaces brushes", new BspCollisionOptions { PatchCollision = PatchCollisionMode.DarkPlacesTriangles, Broadphase = CollisionBroadphase.Bih, DarkPlacesBrushPoints = true, MapFile = file }, false),
        new Config("everything (as legacy mode)", BspCollisionOptions.DarkPlaces(file), true),
    };

    /// <summary>
    /// The measurement behind "should the native game use DarkPlaces' curved-surface collision": the
    /// shared trace library as the native game builds it, and with each option added, against the
    /// recorded answers. Only the last configuration is held to anything; the rest is printed.
    /// (Every configuration is traced with the recorded trace's extension, so that what varies is the
    /// collision and nothing else; the native game does not extend its traces today.)
    /// </summary>
    [Fact]
    public void The_shared_library_against_DarkPlaces_option_by_option()
    {
        if (!ServerTestRig.HaveData) return;
        using SvEnvironment env = new(TestPaths.BaseData, writeRoot: null);
        Tally[] totals = null!;
        foreach (string set in WorldSets)
        {
            List<Golden> traces = Load(set, out string map);
            byte[] file = env.Files.ReadBytes($"maps/{map}.bsp");
            BspData bsp = BspReader.Read(file);
            Config[] configs = Configs(file);
            totals ??= configs.Select(_ => new Tally()).ToArray();
            _output.WriteLine($"== {set}");
            for (int c = 0; c < configs.Length; c++)
            {
                CollisionWorld world = BspCollisionBuilder.Build(bsp, null, configs[c].Options).World;
                var service = new TraceService(world) { DarkPlacesArithmetic = configs[c].Arithmetic };
                var mover = new Entity();
                Tally tally = new();
                foreach (Golden g in traces)
                {
                    mover.DpHitContentsMask = BspLegacyWorld.ContentsToEngine(g.Mask);
                    TraceResult t = service.TraceExtended(g.Start, g.Mins, g.Maxs, g.End, MoveFilter.WorldOnly, mover, g.Extend, out _);
                    tally.Add(g, t.Fraction, t.EndPos, t.StartSolid, t.AllSolid);
                    totals[c].Add(g, t.Fraction, t.EndPos, t.StartSolid, t.AllSolid);
                }
                _output.WriteLine($"  {configs[c].Name,-44} {tally}");
                if (c == configs.Length - 1) Assert.True(tally.Differ == 0, $"{set}: {tally}");
            }
        }
        _output.WriteLine("== all sets");
        Config[] names = Configs(default);
        for (int c = 0; c < names.Length; c++) _output.WriteLine($"  {names[c].Name,-44} {totals[c]}");
    }

    private sealed class Barrels : TraceService.IEntityProvider
    {
        public readonly List<Entity> Entities = new();
        public CollisionMesh? Mesh;
        public IReadOnlyList<Entity> SolidEntities => Entities;
        public void EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
        {
            results.Clear();
            results.AddRange(Entities);
        }
        public bool TryGetEntityBrushModel(Entity e, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld)
        {
            localBrushes = Array.Empty<Brush>();
            toWorld = EntityMatrix.Identity;
            return false;
        }
        public bool TryGetEntityMeshModel(Entity e, MoveFilter filter, out CollisionMesh? mesh, out EntityMatrix toWorld)
        {
            mesh = Mesh;
            // an alias model's pitch turns the other way (SV_GetPitchSign)
            toWorld = EntityMatrix.FromQuakeEntity(e.Origin, new Vector3(-e.Angles.X, e.Angles.Y, e.Angles.Z));
            return Mesh is not null;
        }
    }

    [Fact]
    public void A_turned_barrel_is_clipped_by_its_mesh_where_DarkPlaces_clips_it()
    {
        if (!ServerTestRig.HaveData) return;
        using SvEnvironment env = new(TestPaths.BaseData, writeRoot: null);
        List<Golden> traces = Load("barrels-catharsis", out string map);
        byte[] file = env.Files.ReadBytes($"maps/{map}.bsp");
        CollisionWorld world = BspCollisionBuilder.Build(BspReader.Read(file), null, BspCollisionOptions.DarkPlaces(file)).World;
        SvModelCollision models = new(env.Files);
        CollisionMesh? mesh = models.Get("models/containers/barrel01.md3");
        Assert.NotNull(mesh);
        _output.WriteLine($"barrel01.md3: {mesh!.TriangleCount} triangles, {mesh.VertexCount} vertices, {mesh.ApproximateBytes} bytes");

        // misc_breakablemodel, SOLID_BSP, as "prvm_edicts server" printed them on the reference server
        var tumbled = new Entity { Index = 4177, Solid = Solid.Bsp, Origin = new Vector3(820.799927f, 2512.79956f, -288.000183f), Angles = new Vector3(16.6743126f, 25.7860489f, -88.020752f), Mins = new Vector3(-49.9960098f), Maxs = new Vector3(49.9960098f) };
        var yawed = new Entity { Index = 4174, Solid = Solid.Bsp, Origin = new Vector3(1006.4007f, 1849.3844f, -288f), Angles = new Vector3(0, 44.586792f, 0), Mins = new Vector3(-22.2315254f, -22.2315254f, 0), Maxs = new Vector3(22.2315254f, 22.2315254f, 44.78125f) };

        // the other barrels within reach of the traces
        Entity[] neighbours =
        {
            new() { Index = 4230, Solid = Solid.Bsp, Origin = new Vector3(1067.27283f, 1818.39722f, -288f), Angles = new Vector3(0, 44.586792f, 0), Mins = new Vector3(-22.2315254f, -22.2315254f, 0), Maxs = new Vector3(22.2315254f, 22.2315254f, 44.78125f) },
            new() { Index = 4180, Solid = Solid.Bsp, Origin = new Vector3(1124.87256f, 1731.99768f, -288f), Angles = new Vector3(0, 44.586792f, 0), Mins = new Vector3(-22.2315254f, -22.2315254f, 0), Maxs = new Vector3(22.2315254f, 22.2315254f, 44.78125f) },
            new() { Index = 4240, Solid = Solid.Bsp, Origin = new Vector3(910.799988f, 2628f, -302.399994f), Mins = new Vector3(-15.921875f, -15.515625f, 0), Maxs = new Vector3(15.921875f, 15.515625f, 44.78125f) },
        };

        Tally Run(CollisionMesh? shape, out int hitBarrel)
        {
            var provider = new Barrels { Mesh = shape };
            provider.Entities.Add(tumbled);
            provider.Entities.Add(yawed);
            provider.Entities.AddRange(neighbours);
            var service = new TraceService(world, provider) { DarkPlacesArithmetic = true };
            var mover = new Entity();
            Tally tally = new();
            hitBarrel = 0;
            foreach (Golden g in traces)
            {
                mover.DpHitContentsMask = BspLegacyWorld.ContentsToEngine(g.Mask);
                TraceResult t = service.TraceExtended(g.Start, g.Mins, g.Maxs, g.End, MoveFilter.Normal, mover, g.Extend, out _);
                tally.Add(g, t.Fraction, t.EndPos, t.StartSolid, t.AllSolid);
                if (t.Ent is not null) hitBarrel++;
            }
            return tally;
        }

        Tally asMesh = Run(mesh, out int meshHits), asBox = Run(null, out _);
        _output.WriteLine($"as its mesh: {asMesh}; {meshHits} of the traces end on a barrel");
        _output.WriteLine($"as its box:  {asBox}");
        Assert.True(meshHits > 100, $"only {meshHits} traces met a barrel");
        Assert.True(asMesh.Differ == 0, asMesh.ToString());
        // and the box is not the answer: this is what the mesh is for
        Assert.True(asBox.Differ > 100, asBox.ToString());
    }

    /// <summary>
    /// What each option costs: building the world, the memory it holds, and a trace through it - a player
    /// box moved a few units, and a long line - on the stock map with the most curved floor. A
    /// measurement (run it in Release); it asserts nothing but that the worlds build.
    /// </summary>
    [Theory]
    [InlineData("vorix")]
    [InlineData("catharsis")]
    public void What_the_options_cost(string map)
    {
        if (!ServerTestRig.HaveData) return;
        using SvEnvironment env = new(TestPaths.BaseData, writeRoot: null);
        byte[] file = env.Files.ReadBytes($"maps/{map}.bsp");
        BspData bsp = BspReader.Read(file);
        // where things are: the map's own entities, lifted off the floor
        List<Vector3> seeds = new();
        foreach (var e in bsp.Entities)
        {
            if (!e.TryGetValue("origin", out string? origin) || origin is null) continue;
            string[] p = origin.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                seeds.Add(new Vector3(x, y, z + 25));
        }
        Assert.True(seeds.Count > 8);
        Random random = new(1);
        const int n = 20000;
        Vector3[] starts = new Vector3[n], shortEnds = new Vector3[n], longEnds = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            starts[i] = seeds[random.Next(seeds.Count)] + new Vector3(random.Next(-32, 32), random.Next(-32, 32), random.Next(0, 16));
            shortEnds[i] = starts[i] + new Vector3(random.Next(-8, 9), random.Next(-8, 9), random.Next(-8, 9));
            longEnds[i] = seeds[random.Next(seeds.Count)] + new Vector3(0, 0, 20);
        }
        Vector3 mins = new(-16, -16, -24), maxs = new(16, 16, 45);
        _output.WriteLine($"== {map}: {n} traces a figure, {(Debugger.IsAttached ? "debugger attached" : "no debugger")}, " +
#if DEBUG
            "DEBUG build (the figures are for comparing rows, not for quoting)");
#else
            "Release build");
#endif
        foreach (Config config in Configs(file))
        {
            long before = GC.GetTotalMemory(true);
            Stopwatch build = Stopwatch.StartNew();
            CollisionWorld world = BspCollisionBuilder.Build(bsp, null, config.Options).World;
            _ = world.Bih;   // built on first use
            build.Stop();
            long held = GC.GetTotalMemory(true) - before;
            var service = new TraceService(world) { DarkPlacesArithmetic = config.Arithmetic };
            var mover = new Entity { Solid = Solid.SlideBox };
            double Time(Func<int, TraceResult> trace, out double sum)
            {
                // the best of four passes: the first run before the runtime has finished optimising the code
                double best = double.MaxValue;
                sum = 0;
                for (int pass = 0; pass < 4; pass++)
                {
                    sum = 0;
                    Stopwatch sw = Stopwatch.StartNew();
                    for (int i = 0; i < n; i++) sum += trace(i).Fraction;
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / n);
                }
                return best;
            }
            double box = Time(i => service.Trace(starts[i], mins, maxs, shortEnds[i], MoveFilter.WorldOnly, mover), out double s1);
            double line = Time(i => service.Trace(starts[i], Vector3.Zero, Vector3.Zero, longEnds[i], MoveFilter.WorldOnly, mover), out double s2);
            double longBox = Time(i => service.Trace(starts[i], mins, maxs, longEnds[i], MoveFilter.WorldOnly, mover), out double s3);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {config.Name,-44} build {build.Elapsed.TotalMilliseconds,5:0} ms, {held / (1024.0 * 1024.0),6:0.0} MB, {world.Brushes.Count,6} brushes ({world.Brushes.Count(b => b.IsTriangle)} triangles); ")
                + string.Create(CultureInfo.InvariantCulture, $"short player box {box,6:0} ns, long line {line,7:0} ns, long player box {longBox,7:0} ns  (sums {s1:0.0} {s2:0.0} {s3:0.0})"));
            GC.KeepAlive(world);
        }
    }

    private static QcVector Q(Vector3 v) => new(v.X, v.Y, v.Z);
}
