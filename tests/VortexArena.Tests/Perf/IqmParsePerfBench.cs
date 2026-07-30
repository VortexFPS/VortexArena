using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Perf bench (BotPerfBench pattern — measurement, not a CI assertion) for the IQM model PARSE side of
/// the bot-join allocation storm: the ~230-255 MB single-frame alloc at the stormkeep join window is the
/// player-model parse pipeline (file read + <see cref="IqmReader.Read(ReadOnlySpan{byte})"/>) running for
/// every distinct roster model on the streamer workers, now that texture reads are pooled.
///
/// Measures, per shipped player model and in total: the file read alloc (pk3dir decompress/copy), the
/// parse alloc + wall time, and the model's shape (verts/joints/frames) so the alloc can be attributed.
/// Thread-exact via <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
///
/// No-ops when the content checkout is missing (CI without assets); the data dir can be overridden with
/// the XG_DATA_DIR environment variable.
///
/// Run: dotnet test tests/XonoticGodot.Tests --filter IqmParsePerfBench -l "console;verbosity=detailed"
/// </summary>
[Collection("GlobalState")]
public class IqmParsePerfBench
{
    private static readonly string DataDir =
        Environment.GetEnvironmentVariable("XG_DATA_DIR")
        ?? @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    private readonly ITestOutputHelper _out;
    public IqmParsePerfBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Benchmark_PlayerModelParseAlloc()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountGameDir(DataDir));

        string[] models = vfs.Find("models/player/", "iqm").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (models.Length == 0) { _out.WriteLine("no player models — skipped"); return; }

        // Warm-up parse (JIT + static init) so per-model numbers measure steady-state work, matching what
        // the Nth model of a join wave pays.
        {
            byte[] warm = vfs.ReadBytes(models[0]);
            IqmReader.Read(warm);
        }

        _out.WriteLine($"=== player-model parse alloc ({models.Length} models) ===");
        _out.WriteLine($"{"model",-28} {"file KB",8} {"read MB",8} {"parse MB",9} {"parse ms",9} " +
                       $"{"verts",7} {"joints",6} {"frames",6}");

        long totalReadBytes = 0, totalParseBytes = 0;
        double totalParseMs = 0;
        byte[]? scratch = null;

        foreach (string path in models)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            int length = vfs.ReadBytesInto(path, ref scratch);
            long a1 = GC.GetAllocatedBytesForCurrentThread();

            var sw = Stopwatch.StartNew();
            IqmData iqm = IqmReader.Read(new ReadOnlySpan<byte>(scratch, 0, length));
            sw.Stop();
            long a2 = GC.GetAllocatedBytesForCurrentThread();

            long readAlloc = a1 - a0;     // grow-only scratch: ~file size on first/largest, then ~0
            long parseAlloc = a2 - a1;
            totalReadBytes += readAlloc;
            totalParseBytes += parseAlloc;
            totalParseMs += sw.Elapsed.TotalMilliseconds;

            string name = Path.GetFileName(path);
            _out.WriteLine($"{name,-28} {length / 1024,8} {readAlloc / (1024.0 * 1024),8:F2} " +
                           $"{parseAlloc / (1024.0 * 1024),9:F2} {sw.Elapsed.TotalMilliseconds,9:F2} " +
                           $"{iqm.VertexCount,7} {iqm.Joints.Length,6} {iqm.Frames.Length,6}");
        }

        _out.WriteLine($"{"TOTAL",-28} {"",8} {totalReadBytes / (1024.0 * 1024),8:F2} " +
                       $"{totalParseBytes / (1024.0 * 1024),9:F2} {totalParseMs,9:F2}");

        // Contrast: the unpooled read path (fresh byte[] per file, what AssetLoader did before pooling).
        long f0 = GC.GetAllocatedBytesForCurrentThread();
        foreach (string path in models)
            vfs.ReadBytes(path);
        long freshReadAlloc = GC.GetAllocatedBytesForCurrentThread() - f0;
        _out.WriteLine($"unpooled ReadBytes total: {freshReadAlloc / (1024.0 * 1024):F2} MB " +
                       $"(pooled ReadBytesInto: {totalReadBytes / (1024.0 * 1024):F2} MB)");
    }
}
