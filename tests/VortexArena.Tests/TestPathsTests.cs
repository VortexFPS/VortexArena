using System.IO;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Guards <see cref="TestPaths"/> itself. The real-data tests all self-skip when the content tree is
/// absent, which means a resolution bug is indistinguishable from "no checkout here" — the whole
/// asset-dependent half of the suite would go green while asserting nothing. That is exactly how the
/// hardcoded dev-box paths went unnoticed after the repo moved (restructure plan G13).
///
/// So these assert the CONDITIONAL: if a content tree is discoverable from the repo root, TestPaths
/// must have found it. Core content is committed, so `data/` is present on every checkout.
/// </summary>
public class TestPathsTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void Data_Resolves_When_A_Content_Tree_Is_Present()
    {
        // VA_DATA_DIR is the documented first resolution step, so when it is set it IS the expectation —
        // comparing against the repo-relative tree would fail a legitimate override.
        string? fromEnv = Environment.GetEnvironmentVariable("VA_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            Assert.Equal(
                Path.GetFullPath(fromEnv).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(TestPaths.Data).TrimEnd(Path.DirectorySeparatorChar));
            return;
        }

        string? repo = RepoRoot();
        if (repo is null)
            return;

        // Only `data/` counts now. The `assets/data` alternative is gone on purpose: it is a junction to
        // the upstream reference on a dev box, so accepting it would let this test pass while TestPaths
        // pointed the suite at content the game never mounts.
        string moved = Path.Combine(repo, "data");
        string? expected = Directory.Exists(moved) ? moved : null;

        if (expected is null)
            return; // no tree here — nothing to resolve, and the real-data tests will self-skip

        Assert.True(
            Directory.Exists(TestPaths.Data),
            $"a content tree exists at {expected} but TestPaths.Data resolved to '{TestPaths.Data}' — " +
            "the asset-dependent tests would silently assert nothing");
        Assert.Equal(
            Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(TestPaths.Data).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CorePk3Dir_Points_At_A_Real_Pack_When_Data_Resolves()
    {
        if (!TestPaths.HasData)
            return;

        Assert.True(
            Directory.Exists(TestPaths.CorePk3Dir),
            $"TestPaths.Data resolved to {TestPaths.Data} but no core pack was found at " +
            $"'{TestPaths.CorePk3Dir}' (expected core.pk3dir or xonotic-data.pk3dir)");
    }

    /// <summary>
    /// The guard this file was missing, and its absence cost real coverage.
    ///
    /// <see cref="TestPaths.HasMaps"/> gates the map-dependent assertions, and it fails in the one
    /// direction that cannot report itself: a false value only LOWERS thresholds and SKIPS assertions, so
    /// the suite stays green while a third of it stops checking anything. That happened — when the map
    /// fetch changed from extracting archives to installing <c>.pk3</c> packs, every probe in
    /// <c>ResolveHasMaps</c> was looking for a loose <c>.bsp</c> or a root-level <c>.pk3</c>, so
    /// <c>HasMaps</c> went false with all 32 packs installed and nothing went red.
    ///
    /// So: if map content is discoverable by ANY layout, <see cref="TestPaths.Maps"/> must not say None.
    ///
    /// <para>Asserts on the TRI-STATE, not on <c>HasMaps</c>, since 2026-08-01: <c>HasMaps</c> now means
    /// specifically "the complete pinned set", so one installed pack correctly makes it false while the
    /// content is still very much discoverable. The property this guard exists to protect is unchanged —
    /// discoverable content must never read as None — and it is now expressible without conflating
    /// "some maps" with "all maps".</para>
    /// </summary>
    [Fact]
    public void MapContent_Is_Not_None_When_Map_Content_Is_Present()
    {
        if (!TestPaths.HasData)
            return;

        string maps = Path.Combine(TestPaths.Data, "maps");
        bool packedPerMap = Directory.Exists(maps)
            && Directory.EnumerateFiles(maps, "*.pk3", SearchOption.TopDirectoryOnly).Any();
        bool extracted = Directory.Exists(maps)
            && Directory.EnumerateFiles(maps, "*.bsp", SearchOption.AllDirectories).Any();
        bool bundled = Directory.EnumerateFiles(TestPaths.Data, "*.pk3", SearchOption.TopDirectoryOnly)
            .Any(p => Path.GetFileName(p).Contains("maps", StringComparison.OrdinalIgnoreCase));
        bool looseBsp = Directory.EnumerateFiles(TestPaths.Data, "*.bsp", SearchOption.AllDirectories).Any();

        bool discoverable = packedPerMap || extracted || bundled || looseBsp;
        if (!discoverable)
            return; // genuinely no maps installed — the map-dependent tests are right to skip

        Assert.True(
            TestPaths.Maps != TestPaths.MapContent.None,
            $"map content IS present under {TestPaths.Data} (per-map .pk3={packedPerMap}, "
                + $"extracted={extracted}, bundled .pk3={bundled}, loose .bsp={looseBsp}) but "
                + "TestPaths.Maps is None — every map-dependent assertion is silently skipped or "
                + "running against a lowered threshold. Fix ResolveMaps.");
    }

    [Fact]
    public void No_Test_Hardcodes_A_Workstation_Path()
    {
        string? repo = RepoRoot();
        if (repo is null)
            return;

        string tests = Path.Combine(repo, "tests");
        if (!Directory.Exists(tests))
            return;

        var offenders = new System.Collections.Generic.List<string>();
        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build output — it carries copies of the sources' string literals.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            if (Path.GetFileName(file) == nameof(TestPathsTests) + ".cs")
                continue;

            string text = File.ReadAllText(file);
            if (text.Contains(@"\Users\", System.StringComparison.OrdinalIgnoreCase)
                && text.Contains(@"\Projects\", System.StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetRelativePath(repo, file));
        }

        Assert.True(
            offenders.Count == 0,
            "these tests hardcode a workstation path instead of using TestPaths: "
                + string.Join(", ", offenders));
    }
}
