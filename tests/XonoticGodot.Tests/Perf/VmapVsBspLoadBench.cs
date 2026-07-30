using System.Diagnostics;
using System.Globalization;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Formats.Vmap;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests.Perf;

/// <summary>
/// What a BSP compile is actually worth in THIS engine (design doc §13.1, backlog P5).
///
/// The question the numbers answer: if <c>.vmap</c> stays uncompiled and the engine builds its render and
/// collision data at load, what does that cost, and is the cost per-load or per-frame? Godot owns frustum
/// culling and draw submission, so a BSP tree buys nothing at render time here — the only per-frame asset a
/// compile produces is PVS. Everything else it does (winding generation, batching, collision hulls) is work
/// this engine can do at load, and the question is only how long it takes.
///
/// <para>Reports, per map: the two collision builds, the surface build, and the batch/triangle counts each
/// path produces — because equal batching is what makes the load-time-only claim true. Skips without the
/// content checkout (<c>VA_DATA_DIR</c>); <c>VA_MAPS</c> overrides the map list.</para>
/// </summary>
[Collection("GlobalState")]
public class VmapVsBspLoadBench
{
    private static readonly string DataDir = TestPaths.Data;

    /// <summary>
    /// Explicit opt-in — an experiment, not an assertion, and the default content path is ABSOLUTE,
    /// so on a machine with the assets checked out this would otherwise run in full on every
    /// <c>ci.sh</c>. Run with <c>VA_BENCH=1</c>.
    /// </summary>
    private static bool Enabled => Environment.GetEnvironmentVariable("VA_BENCH") is { Length: > 0 };

    private static string[] Maps =>
        (Environment.GetEnvironmentVariable("VA_MAPS") ?? "stormkeep,fuse,catharsis")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private readonly ITestOutputHelper _out;
    public VmapVsBspLoadBench(ITestOutputHelper output) => _out = output;

    private static double MsOf(Action work, int runs = 3)
    {
        work();                                  // warm: JIT + first-touch allocation
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            work();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;                             // best-of, so a GC pause in one run does not become the answer
    }

    [Fact]
    public void Benchmark_VmapBuildVsBspLoad()
    {
        if (!Enabled) { _out.WriteLine("bench — set VA_BENCH=1 to run"); return; }
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));

        _out.WriteLine($"{"map",-12} {"brush",6} {"patch",6} | {"bsp coll",9} {"vmap coll",9} "
                       + $"| {"import",8} {"surfaces",9} | {"bsp surf",8} {"vmap surf",9} {"vmap tris",10}");
        _out.WriteLine(new string('-', 108));

        foreach (string map in Maps)
        {
            string bspPath = $"maps/{map}.bsp";
            if (!vfs.Exists(bspPath))
            {
                _out.WriteLine($"{map,-12} (missing)");
                continue;
            }

            BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));

            // The compiled path: brush planes and patch slabs straight out of the lumps.
            double bspColl = MsOf(() => BspCollisionBuilder.Build(bsp));

            // The uncompiled path: planes recovered from the BSP, then the same two products built from them.
            VmapDocument doc = BspToVmap.Import(bsp, map, bspPath, sourceHash: "");
            double import = MsOf(() => BspToVmap.Import(bsp, map, bspPath, sourceHash: ""));
            double vmapColl = MsOf(() => VmapCollisionBuilder.Build(doc));

            var options = new VmapSurfaceOptions();
            double surfaces = MsOf(() => VmapGeometryBuilder.BuildSurfaces(doc, options));

            IReadOnlyList<VmapSurface> built = VmapGeometryBuilder.BuildSurfaces(doc, options);
            int tris = 0;
            foreach (VmapSurface s in built)
                tris += s.TriangleCount;

            // Draw batches, both ways. The BSP path batches by texture+lightmap page (MapLoader groups the
            // face list); the vmap path batches by material. If these are the same order of magnitude, the
            // uncompiled format costs load time and not frame time — which is the whole question.
            int bspBatches = CountBspBatches(bsp);

            _out.WriteLine(
                $"{map,-12} {doc.Brushes.Count,6} {doc.Patches.Count,6} "
                + $"| {bspColl,8:0.0}m {vmapColl,8:0.0}m "
                + $"| {import,7:0.0}m {surfaces,8:0.0}m "
                + $"| {bspBatches,8} {built.Count,9} {tris,10}");
        }

        _out.WriteLine("");
        _out.WriteLine("bsp coll  = BspCollisionBuilder.Build      (compiled: lump brushes + patch slabs)");
        _out.WriteLine("vmap coll = VmapCollisionBuilder.Build     (uncompiled: document planes + patch slabs)");
        _out.WriteLine("import    = BspToVmap.Import               (only paid when converting, not when loading a .vmap)");
        _out.WriteLine("surfaces  = VmapGeometryBuilder.BuildSurfaces (uncompiled: windings + triangulation + batching)");
        _out.WriteLine("bsp surf / vmap surf = draw batches each path produces");
    }

    /// <summary>
    /// Draw batches a compiled load would produce: distinct (texture, lightmap) pairs over the drawable world
    /// faces, which is how the BSP loader groups them.
    /// </summary>
    private static int CountBspBatches(BspData bsp)
    {
        var keys = new HashSet<long>();
        foreach (BspFace f in bsp.Faces)
        {
            if (f.Type is not (BspFaceType.Flat or BspFaceType.Mesh or BspFaceType.Patch))
                continue;
            keys.Add(((long)f.TextureIndex << 32) ^ (uint)f.LightmapIndex);
        }
        return keys.Count;
    }
}
