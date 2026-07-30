using System.Diagnostics;
using System.Numerics;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Formats.Vmap;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Perf;

/// <summary>
/// What the entity-occlusion sweep (backlog T1) actually costs on a real map, so the per-frame budget is a
/// measurement instead of a guess.
///
/// The sweep fires one <see cref="VmapPicking.IsOccluded"/> ray per entity box and spreads the map's entities
/// over several frames. Two numbers decide whether that is affordable: the cost of a single ray against the
/// map's solid count, and how many frames a full refresh therefore takes. Both are printed per map, alongside
/// what fraction of the boxes the test actually hides — a hit rate near zero would mean the feature is paying
/// for nothing.
///
/// <para>Skips without the content checkout (<c>VA_DATA_DIR</c>) and without <c>VA_BENCH=1</c>.</para>
/// </summary>
[Collection("GlobalState")]
public class EditorOcclusionBench
{
    private static readonly string DataDir = TestPaths.Data;

    private static bool Enabled => Environment.GetEnvironmentVariable("VA_BENCH") is { Length: > 0 };

    private static string[] Maps =>
        (Environment.GetEnvironmentVariable("VA_MAPS") ?? "stormkeep,fuse,catharsis")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The controller's own per-frame budget, mirrored so the frames-per-refresh figure is honest.</summary>
    private const int EntityVisibilityBudget = 24;

    private readonly ITestOutputHelper _out;
    public EditorOcclusionBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Benchmark_EntityOcclusionSweep()
    {
        if (!Enabled) { _out.WriteLine("bench — set VA_BENCH=1 to run"); return; }
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));

        _out.WriteLine("map          brushes patches entities   ray µs   sweep ms/frame   frames/refresh   hidden");
        foreach (string map in Maps)
        {
            string bspPath = $"maps/{map}.bsp";
            if (!vfs.Exists(bspPath)) { _out.WriteLine($"{map}: missing — skipped"); continue; }

            VmapDocument doc = BspToVmap.Import(
                BspReader.Read(vfs.ReadBytes(bspPath)), map, bspPath, sourceHash: "");
            var index = new VmapPickIndex();
            index.EnsureBuilt(doc, 0);

            IReadOnlyList<VmapPickIndex.EntityEntry> boxes = index.Entities;
            if (boxes.Count == 0) { _out.WriteLine($"{map}: no point entities — skipped"); continue; }

            // The eye the sweep would run from: a spawn point if the map has one, else the map's centre. A
            // viewpoint inside the level is the only one that produces representative ray lengths.
            Vector3 eye = EyeFor(doc, index);

            // Warm the JIT and the caches before timing; a first pass through 5400 windings measures neither.
            int hidden = SweepOnce(index, boxes, eye);

            var sw = Stopwatch.StartNew();
            const int passes = 5;
            for (int i = 0; i < passes; i++)
                hidden = SweepOnce(index, boxes, eye);
            sw.Stop();

            double rayUs = sw.Elapsed.TotalMilliseconds * 1000.0 / (passes * boxes.Count);
            double perFrameMs = rayUs * Math.Min(EntityVisibilityBudget, boxes.Count) / 1000.0;
            double framesPerRefresh = Math.Ceiling(boxes.Count / (double)EntityVisibilityBudget);

            _out.WriteLine(
                $"{map,-12} {index.Entries.Count,7} {index.Patches.Count,7} {boxes.Count,8} "
                + $"{rayUs,8:0.###} {perFrameMs,16:0.###} {framesPerRefresh,16:0} "
                + $"{hidden * 100.0 / boxes.Count,7:0.#}%   "
                + $"grid {index.BrushGridCellSize:0}u, {index.BrushGridOversized} oversized");
        }

        _out.WriteLine("");
        _out.WriteLine("sweep ms/frame is the cost inside editor.ctrl with the Entity tool up; it is zero otherwise.");
    }

    private static int SweepOnce(
        VmapPickIndex index, IReadOnlyList<VmapPickIndex.EntityEntry> boxes, Vector3 eye)
    {
        int hidden = 0;
        foreach (VmapPickIndex.EntityEntry ee in boxes)
        {
            Vector3 centre = (ee.Mins + ee.Maxs) * 0.5f;
            float half = (ee.Maxs - ee.Mins).Length() * 0.5f;
            bool visible = (centre - eye).LengthSquared() <= half * half
                           || !VmapPicking.IsOccluded(index, eye, centre);
            if (!visible)
                hidden++;
        }
        return hidden;
    }

    private static Vector3 EyeFor(VmapDocument doc, VmapPickIndex index)
    {
        foreach (VmapEntity e in doc.Entities)
            if (e.ClassName.StartsWith("info_player", StringComparison.OrdinalIgnoreCase))
                return e.Origin() + new Vector3(0f, 0f, 40f);

        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (VmapPickIndex.Entry entry in index.Entries)
        {
            lo = Vector3.Min(lo, entry.Mins);
            hi = Vector3.Max(hi, entry.Maxs);
        }
        return (lo + hi) * 0.5f;
    }
}
