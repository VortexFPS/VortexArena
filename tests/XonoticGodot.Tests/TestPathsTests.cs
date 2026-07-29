using System.IO;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards <see cref="TestPaths"/> itself. The real-data tests all self-skip when the content tree is
/// absent, which means a resolution bug is indistinguishable from "no checkout here" — the whole
/// asset-dependent half of the suite would go green while asserting nothing. That is exactly how the
/// hardcoded dev-box paths went unnoticed after the repo moved (restructure plan G13).
///
/// So these assert the CONDITIONAL: if a content tree is discoverable from the repo root, TestPaths
/// must have found it. On a checkout with no assets they hold trivially, so CI stays portable.
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

        // Either layout counts: `data/` after the restructure, `assets/data/` before it.
        string moved = Path.Combine(repo, "data");
        string legacy = Path.Combine(repo, "assets", "data");
        string? expected = Directory.Exists(moved) ? moved
            : Directory.Exists(legacy) ? legacy
            : null;

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
