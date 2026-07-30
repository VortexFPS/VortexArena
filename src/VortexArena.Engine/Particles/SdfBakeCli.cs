using System;
using System.IO;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Formats.Bsp;

namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  SdfBakeCli — the `--bake-sdf` command-line SDF baker (planning/particles-dual-system.md §A.7).
//
//    XonoticGodot --bake-sdf <map.bsp> [-o out.psdf]
//
//  This is the CANONICAL compiler-time SDF generation path: mappers add it as a post-q3map2 build step and
//  the resulting `maps/<map>.psdf` ships inside the pk3 (§A.2 search order picks it up at load). It is
//  deliberately Godot-FREE (lives in the Engine layer): it reads the BSP bytes off disk, builds the engine
//  static CollisionWorld via BspCollisionBuilder, and runs the SAME §A.3 SdfGenerator the load-time path uses
//  — so there is ZERO drift between compiler-time and load-time output (the §A.7 mandate). It then stamps the
//  BSP/params hashes (§A.2 cache identity) and writes the .psdf via the pinned PsdfFile.Write.
// =====================================================================================================

/// <summary>The <c>--bake-sdf</c> CLI entry. Call <see cref="Run"/> from the host's arg dispatch (Main.cs).</summary>
public static class SdfBakeCli
{
    /// <summary>The flag that selects this command in the host arg parse.</summary>
    public const string Flag = "--bake-sdf";

    /// <summary>
    /// Run the SDF baker. <paramref name="args"/> is the full process arg vector; we locate
    /// <c>--bake-sdf &lt;map.bsp&gt;</c> and an optional <c>-o &lt;out.psdf&gt;</c>. Returns a process exit
    /// code: 0 success, non-zero on a usage/IO/parse error. Logs the <c>[ParticleSDF]</c> summary line.
    /// </summary>
    public static int Run(string[] args)
    {
        if (args is null)
            return Usage("no arguments");

        int fi = Array.IndexOf(args, Flag);
        if (fi < 0)
            return Usage($"missing {Flag}");
        if (fi + 1 >= args.Length || args[fi + 1].StartsWith("-", StringComparison.Ordinal))
            return Usage($"{Flag} requires a <map.bsp> path");

        string inPath = args[fi + 1];

        // Optional `-o <out.psdf>` (anywhere in the arg vector).
        string? outPath = null;
        int oi = Array.IndexOf(args, "-o");
        if (oi >= 0 && oi + 1 < args.Length && !args[oi + 1].StartsWith("-", StringComparison.Ordinal))
            outPath = args[oi + 1];

        return Bake(inPath, outPath);
    }

    /// <summary>
    /// Bake one map: read <paramref name="inPath"/>, generate the field with the §A.3 <see cref="SdfGenerator"/>,
    /// stamp the cache identity, and write to <paramref name="outPath"/> (defaulting to
    /// <c>&lt;map&gt;.psdf</c> next to the input). Public so a test/tool can invoke it without an arg vector.
    /// <paramref name="prms"/> defaults to the stock <see cref="SdfGenParams"/> (the cvar defaults). Returns a
    /// process exit code.
    /// </summary>
    public static int Bake(string inPath, string? outPath = null, SdfGenParams? prms = null)
    {
        if (string.IsNullOrWhiteSpace(inPath))
            return Usage("empty input path");

        byte[] bspBytes;
        try
        {
            bspBytes = File.ReadAllBytes(inPath);
        }
        catch (Exception ex)
        {
            return Fail($"cannot read BSP '{inPath}': {ex.Message}");
        }

        string mapName = Path.GetFileNameWithoutExtension(inPath);

        BspData bsp;
        try
        {
            bsp = BspReader.Read(bspBytes);
        }
        catch (Exception ex)
        {
            return Fail($"cannot parse BSP '{inPath}': {ex.Message}");
        }

        // Static world collision (worldspawn brushes + tessellated worldspawn patches). The inline "*N" brush
        // models (doors/plats) are intentionally excluded — particles collide against the static world, like
        // the runtime SDF service's chunk colliders (a moving brush model isn't represented in a static field).
        CollisionWorld world;
        try
        {
            world = BspCollisionBuilder.Build(bsp).World;
        }
        catch (Exception ex)
        {
            return Fail($"cannot build collision for '{inPath}': {ex.Message}");
        }

        SdfGenParams p = prms ?? new SdfGenParams();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SdfField field;
        try
        {
            // The exact load-time generator (§A.3) — zero drift between compiler-time and runtime output.
            field = new SdfGenerator(p).Generate(world);
        }
        catch (Exception ex)
        {
            return Fail($"SDF generation failed for '{inPath}': {ex.Message}");
        }
        sw.Stop();

        // Cache identity (§A.2): sha256 of the raw BSP bytes + a stable params hash. Generate leaves these for
        // the caller to fill.
        field.BspHash = PsdfFile.ComputeBspHash(bspBytes);
        field.ParamsHash = PsdfFile.ComputeParamsHash(p);

        string resolvedOut = outPath ?? DefaultOutPath(inPath, mapName);
        try
        {
            string? dir = Path.GetDirectoryName(resolvedOut);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            using FileStream fs = File.Create(resolvedOut);
            PsdfFile.Write(fs, field);
        }
        catch (Exception ex)
        {
            return Fail($"cannot write '{resolvedOut}': {ex.Message}");
        }

        // The §A.3 headless regression-guard log line (everything is freshly generated for the CLI; nothing is
        // cache-loaded here — matches the runtime "[ParticleSDF] <map>: N chunks (G generated, C cached, T ms)").
        System.Console.WriteLine(
            $"[ParticleSDF] {mapName}: {field.Chunks.Count} chunks " +
            $"({field.Chunks.Count} generated, 0 cached, {sw.ElapsedMilliseconds} ms) -> {resolvedOut}");
        return 0;
    }

    /// <summary>Default output: <c>&lt;map&gt;.psdf</c> next to the input BSP (§A.7: ships as maps/&lt;map&gt;.psdf).</summary>
    private static string DefaultOutPath(string inPath, string mapName)
    {
        string inDir = Path.GetDirectoryName(Path.GetFullPath(inPath)) ?? ".";
        return Path.Combine(inDir, mapName + ".psdf");
    }

    // -------------------------------------------------------------------------------------------------
    //  Diagnostics
    // -------------------------------------------------------------------------------------------------

    private static int Usage(string why)
    {
        System.Console.Error.WriteLine($"[ParticleSDF] usage: {Flag} <map.bsp> [-o out.psdf]   ({why})");
        return 2;
    }

    private static int Fail(string why)
    {
        System.Console.Error.WriteLine($"[ParticleSDF] error: {why}");
        return 1;
    }
}
