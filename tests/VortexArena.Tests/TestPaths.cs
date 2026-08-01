using System;
using System.Linq;
using System.IO;

namespace VortexArena.Tests;

/// <summary>
/// Resolves the on-disk content trees the real-data tests read, so that no test hardcodes a
/// workstation path. Every member returns a path that may not exist: the callers all guard with
/// <c>Directory.Exists</c>/<c>File.Exists</c> and no-op, which is how the suite stays CI-portable.
///
/// Resolution order for <see cref="Data"/>:
/// <list type="number">
///   <item><c>VA_DATA_DIR</c>, if set — the escape hatch for a tree parked anywhere.</item>
///   <item><c>&lt;repo&gt;/data</c>, walking up from <see cref="AppContext.BaseDirectory"/> to find
///     the repo root. There is deliberately NO <c>assets/data</c> fallback — see
///     <see cref="ResolveData"/> for why one would be actively harmful.</item>
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

    /// <summary>How much compiled map content is installed. See <see cref="Maps"/>.</summary>
    public enum MapContent
    {
        /// <summary>No map content by any layout. Map-dependent assertions skip; that is correct.</summary>
        None,
        /// <summary>Some maps, but not every pack <c>data/maps.lock.json</c> pins.</summary>
        Partial,
        /// <summary>Every pinned pack, or a legacy all-in-one bundle. Safe to assert at full strength.</summary>
        Full,
    }

    /// <summary>
    /// How much compiled map content (BSPs and their art) is present.
    ///
    /// Separate from <see cref="HasData"/> because the two arrive by different routes: core content is
    /// committed and comes with the clone, while compiled maps are build output fetched per
    /// <c>data/maps.lock.json</c> from a VortexMaps release (restructure D7, section 5.3.1). So a
    /// perfectly good checkout legitimately has core content and no maps.
    ///
    /// <para><b>Why three states and not two (2026-08-01).</b> This was a bool, and a bool cannot express
    /// the state <c>ci/ci.sh</c> creates for itself. The gate runs the suite, and LATER fetches
    /// <c>--only stormkeep</c> for its host smoke — so a fresh clone went green on the first run (no maps
    /// → assertions skipped) and RED on the second (one map → the flag flipped true → the full-set
    /// thresholds asserted → three failures), with nothing changed but the gate's own side effect. One
    /// installed pack is not the 32-pack set those thresholds describe. It was invisible on a dev box
    /// where the full set is always present, which is the same blind spot that hid four portability bugs
    /// in this tree until the same day.</para>
    ///
    /// <para>Probes every layout: per-map <c>.pk3</c> under <c>data/maps/</c> (current), extracted
    /// <c>.pk3dir</c>, the pre-restructure bundled <c>*-maps.pk3</c> archives, and loose BSPs.</para>
    /// </summary>
    public static MapContent Maps { get; } = ResolveMaps();

    /// <summary>
    /// True only for a COMPLETE map set, i.e. safe to assert full-set thresholds against.
    ///
    /// <para>Deliberately <c>Full</c> rather than <c>!= None</c>, so the many call sites that read
    /// <c>HasMaps ? highFloor : lowFloor</c> keep meaning what they were written to mean. A partial set
    /// takes the lowered threshold, which is the safe direction: it under-asserts on an incomplete
    /// checkout rather than failing on one.</para>
    /// </summary>
    public static bool HasMaps => Maps == MapContent.Full;

    private static MapContent ResolveMaps()
    {
        if (!Directory.Exists(Data))
            return MapContent.None;

        string maps = Path.Combine(Data, "maps");

        // Current layout: the fetcher installs one .pk3 per map into data/maps/, unextracted, because
        // MountGameDir mounts a .pk3 natively. Presence of a pack is enough - opening it to confirm
        // would cost a zip scan on every probe.
        //
        // This case was MISSING for a while and the omission is instructive: every earlier probe looked
        // for a loose .bsp or for a .pk3 at the data ROOT, so when the fetch switched from extracting to
        // installing, HasMaps went false with all 32 packs present. Nothing went red, because a false
        // HasMaps only LOWERS thresholds and SKIPS assertions - so the suite stayed green while the
        // map-dependent half of it stopped asserting. TestPathsTests guards this directly.
        if (Directory.Exists(maps))
        {
            int installed = Directory.EnumerateFiles(maps, "*.pk3", SearchOption.TopDirectoryOnly).Count();
            if (installed > 0)
                return installed >= PinnedPackCount() ? MapContent.Full : MapContent.Partial;

            // Extracted per-map packages, which the fetcher no longer produces but a developer may still
            // have unpacked by hand for editing (a .pk3dir IS the loose form - see section 9.3).
            if (Directory.EnumerateFiles(maps, "*.bsp", SearchOption.AllDirectories).Any())
                return MapContent.Full;
        }

        // Pre-restructure: the BSPs live inside the bundled .pk3 archives at the data root. These are
        // all-in-one, so there is no partial state to express - either the bundle is there or it is not.
        if (Directory.EnumerateFiles(Data, "*.pk3", SearchOption.TopDirectoryOnly)
            .Any(p => Path.GetFileName(p).Contains("maps", StringComparison.OrdinalIgnoreCase)))
            return MapContent.Full;

        // Any layout can also carry loose BSPs (a locally built or authored map).
        return Directory.EnumerateFiles(Data, "*.bsp", SearchOption.AllDirectories).Any()
            ? MapContent.Full
            : MapContent.None;
    }

    /// <summary>
    /// How many packs <c>data/maps.lock.json</c> pins, which is what "complete" means.
    ///
    /// <para>Returns 1 when the lockfile cannot be read, which makes any single installed pack count as
    /// Full. That is the deliberate direction: an unreadable lockfile means we cannot evaluate
    /// completeness, and the failure mode this whole property exists to prevent is the QUIET one — a
    /// lowered threshold that stops asserting without saying so. Better to over-assert and fail loudly on
    /// a broken checkout than to silently drop coverage on one.</para>
    /// </summary>
    private static int PinnedPackCount()
    {
        try
        {
            string lockPath = Path.Combine(Data, "maps.lock.json");
            if (!File.Exists(lockPath))
                return 1;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lockPath));
            return doc.RootElement.TryGetProperty("packs", out var packs)
                ? Math.Max(1, packs.EnumerateObject().Count())
                : 1;
        }
        catch
        {
            return 1;
        }
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
            string moved = Path.Combine(repo, "data");
            if (Directory.Exists(moved))
                return moved;
        }

        // No `assets/data` fallback any more, and its removal is a correctness fix rather than tidying.
        // That directory is a JUNCTION to the pristine upstream reference on a dev box, so the fallback
        // could silently point the whole suite at UPSTREAM content: no core.pk3dir, no vortex-*.cfg layer,
        // and every real-data assertion evaluated against a tree the game never mounts. Tests would pass
        // or fail for reasons unrelated to this repo. The same shape as the cvar differ diffing Base
        // against itself. If `data/` is absent the checkout is broken, and Unresolved says so.
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
    /// (<c>VortexArena.sln</c> → <c>VortexArena.sln</c>) and <c>.git</c> does not. It is a directory
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
