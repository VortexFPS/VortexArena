using System;
using System.Linq;
using System.IO;

namespace XonoticGodot.Tests;

/// <summary>
/// Resolves the on-disk content trees the real-data tests read, so that no test hardcodes a
/// workstation path. Every member returns a path that may not exist: the callers all guard with
/// <c>Directory.Exists</c>/<c>File.Exists</c> and no-op, which is how the suite stays CI-portable.
///
/// Resolution order for <see cref="Data"/>:
/// <list type="number">
///   <item><c>VA_DATA_DIR</c>, if set — the escape hatch for a tree parked anywhere.</item>
///   <item><c>&lt;repo&gt;/data</c>, then <c>&lt;repo&gt;/assets/data</c>, walking up from
///     <see cref="AppContext.BaseDirectory"/> to find the repo root. Probing BOTH is deliberate: it
///     makes this helper survive the <c>assets/data</c> → <c>data</c> move (repo-restructure plan
///     stage 3) with no test edits, and it keeps working in the meantime.</item>
///   <item><see cref="Unresolved"/>, a path that cannot exist, so guarded tests skip.</item>
/// </list>
///
/// <see cref="BaseData"/> is the upstream Xonotic reference checkout the parity comparisons diff
/// against. It stays outside the repo by design (see <c>planning/parity/</c>), so it gets its own
/// <c>VA_BASE_DIR</c> override and a sibling-of-the-repo fallback rather than sharing the above.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Returned when nothing resolves. Deliberately not the empty string: <c>Path.Combine("", "x")</c>
    /// yields the relative <c>"x"</c>, which can accidentally exist relative to the test host's
    /// working directory, whereas this cannot exist on any platform we run on.
    /// </summary>
    public const string Unresolved = "<no-content-tree>";

    /// <summary>
    /// The repository root. Unlike the content roots below, this one is NOT optional: every checkout
    /// has one, and the files it locates (<c>project.godot</c>, <c>export_presets.cfg</c>) are
    /// committed. So tests reading those assert this resolved rather than guarding and skipping —
    /// a "repo root not found" skip would just be the suite declining to check anything.
    /// </summary>
    public static string RepoRoot { get; } = FindRepoRoot() ?? Unresolved;

    /// <summary>The game's content root — the directory that gets handed to <c>MountGameDir</c>.</summary>
    public static string Data { get; } = ResolveData();

    /// <summary>The upstream Xonotic reference checkout's content root (parity baseline).</summary>
    public static string BaseData { get; } = ResolveBaseData();

    /// <summary>
    /// The core content pack: <c>core.pk3dir</c> after the restructure, <c>xonotic-data.pk3dir</c>
    /// before it. Probed in that order, so this needs no edit when the rename lands.
    /// </summary>
    public static string CorePk3Dir { get; } = ResolveCorePack(Data);

    /// <summary>
    /// The reference checkout's core pack. Always the upstream name — <c>Base/</c> is a pristine
    /// upstream clone and is never renamed.
    /// </summary>
    public static string BaseCorePk3Dir { get; } =
        BaseData == Unresolved ? Unresolved : Path.Combine(BaseData, "xonotic-data.pk3dir");

    /// <summary><c>models/weapons</c> inside the core pack — the muzzle/view-model tests' root.</summary>
    public static string CoreWeapons { get; } =
        CorePk3Dir == Unresolved ? Unresolved : Path.Combine(CorePk3Dir, "models", "weapons");

    /// <summary>True when a real content tree resolved, for tests that want an explicit guard.</summary>
    public static bool HasData => Directory.Exists(Data);

    /// <summary>
    /// True when compiled map content (BSPs and their art) is present.
    ///
    /// Separate from <see cref="HasData"/> because the two now arrive by different routes: the core
    /// content is committed and comes with the clone, while compiled maps are build output fetched per
    /// <c>data/maps.lock.json</c> from a VortexMaps release (restructure D7, section 5.3.1). So a
    /// perfectly good checkout legitimately has core content and no maps, and the map-dependent tests
    /// have to distinguish "not fetched" from "broken".
    ///
    /// Probes both layouts: per-map <c>.pk3dir</c> packages under <c>data/maps/</c> after the
    /// restructure, and the bundled <c>*-maps.pk3</c> / <c>*-nexcompat.pk3</c> archives before it.
    /// </summary>
    public static bool HasMaps { get; } = ResolveHasMaps();

    private static bool ResolveHasMaps()
    {
        if (!Directory.Exists(Data))
            return false;

        // Current layout: fetch-maps.py installs one .pk3 per map into data/maps/, unextracted, because
        // MountGameDir mounts a .pk3 natively. Presence of a pack is enough - opening it to confirm
        // would cost a zip scan on every probe.
        //
        // This case was MISSING for a while and the omission is instructive: every earlier probe looked
        // for a loose .bsp or for a .pk3 at the data ROOT, so when the fetch switched from extracting to
        // installing, HasMaps silently went false with all 32 packs present. Nothing went red, because a
        // false HasMaps only LOWERS thresholds and SKIPS assertions - so the suite stayed green while the
        // map-dependent half of it stopped asserting. TestPathsTests now guards this directly.
        string maps = Path.Combine(Data, "maps");
        if (Directory.Exists(maps)
            && Directory.EnumerateFiles(maps, "*.pk3", SearchOption.TopDirectoryOnly).Any())
            return true;

        // Extracted per-map packages, which fetch-maps.py no longer produces but a developer may still
        // have unpacked by hand for editing (a .pk3dir IS the loose form - see section 9.3).
        if (Directory.Exists(maps)
            && Directory.EnumerateFiles(maps, "*.bsp", SearchOption.AllDirectories).Any())
            return true;

        // Pre-restructure: the BSPs live inside the bundled .pk3 archives at the data root.
        if (Directory.EnumerateFiles(Data, "*.pk3", SearchOption.TopDirectoryOnly)
            .Any(p => Path.GetFileName(p).Contains("maps", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Any layout can also carry loose BSPs (a locally built or authored map).
        return Directory.EnumerateFiles(Data, "*.bsp", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    /// Message for a map-dependent test to explain a skip, so "0 assertions ran" is never silent.
    /// </summary>
    public const string NoMapsReason =
        "compiled map content is not present — run tools/data/fetch-maps.py (restructure D7)";

    private static string ResolveData()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("VA_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        string? repo = FindRepoRoot();
        if (repo is not null)
        {
            // Post-restructure layout first, then the pre-restructure one.
            string moved = Path.Combine(repo, "data");
            if (Directory.Exists(moved))
                return moved;

            string legacy = Path.Combine(repo, "assets", "data");
            if (Directory.Exists(legacy))
                return legacy;
        }

        return Unresolved;
    }

    private static string ResolveBaseData()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("VA_BASE_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        // Convention: the upstream clone sits beside the game repo, as <parent>/Base/data.
        string? repo = FindRepoRoot();
        string? parent = repo is null ? null : Path.GetDirectoryName(repo);
        if (parent is not null)
        {
            string sibling = Path.Combine(parent, "Base", "data");
            if (Directory.Exists(sibling))
                return sibling;
        }

        return Unresolved;
    }

    private static string ResolveCorePack(string dataRoot)
    {
        if (dataRoot == Unresolved)
            return Unresolved;

        string renamed = Path.Combine(dataRoot, "core.pk3dir");
        if (Directory.Exists(renamed))
            return renamed;

        return Path.Combine(dataRoot, "xonotic-data.pk3dir");
    }

    /// <summary>
    /// Walk up from the test assembly's location looking for the repo root. <c>.git</c> is the marker
    /// rather than the <c>.sln</c> filename, because the solution gets renamed
    /// (<c>XonoticGodot.sln</c> → <c>VortexArena.sln</c>) and <c>.git</c> does not. It is a directory
    /// in a normal clone and a file in a worktree, so both are accepted.
    /// </summary>
    private static string? FindRepoRoot()
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
}
