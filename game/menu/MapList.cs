using System;
using System.Collections.Generic;

namespace VortexArena.Game.Menu;

/// <summary>
/// The available-maps catalog for the Create-Game screen. C# successor to QC's <c>MapInfo</c> /
/// <c>maplist.qc</c> directory scan: Xonotic enumerated <c>maps/*.bsp</c> (+ a <c>.mapinfo</c> per map); here
/// the same enumeration runs over the mounted content search path, which covers the shipped
/// <c>data/</c> tree AND the player's own gamedir (<see cref="UserPaths.GameDir"/>), packed or loose. Falls
/// back to a representative hardcoded list when no maps are installed, so the menu is always usable — e.g.
/// when shown in isolation during development.
/// </summary>
public static class MapList
{
    // Representative stock Xonotic map names — the fallback when nothing is installed on disk.
    private static readonly string[] Fallback =
    {
        "dm_example",
        "afterslime", "atelier", "courtfun", "darkzone", "drain",
        "erbium", "fuse", "glowplant", "implosion", "leave_em_behind",
        "oilrig", "runningman", "silvercity", "solarium", "space-elevator",
        "stormkeep", "techassault", "vorix", "warfare", "xoylent",
    };

    /// <summary>
    /// The list of selectable map names (file stem, e.g. "dm_example"), sorted and de-duplicated. Returns the
    /// search-path scan when any maps are found, otherwise the hardcoded fallback list. Not cached — the
    /// search path can change under it (<c>fs_rescan</c>), and the scan is a walk of already-built indexes.
    /// </summary>
    public static IReadOnlyList<string> Available()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // The mounted content VFS is the ONE source: every installed map, from the shipped packs and from the
        // player's own gamedir alike, packed or loose. (It used to also scan a couple of directories with
        // DirAccess for stray .bsp files. That listed maps the game could not then load — the directories were
        // never mounted, so the host resolved maps/<name>.bsp to nothing and came up on an empty world — and it
        // could not see inside a .pk3 at all, which is the form a map is actually distributed in.)
        var vfs = MenuState.Vfs;
        if (vfs is not null)
        {
            foreach (string vpath in vfs.Find("maps/", "bsp"))
            {
                // vpath like "maps/stormkeep.bsp" (or a "maps/sub/foo.bsp"); take the file stem.
                string name = vpath;
                int slash = name.LastIndexOf('/');
                if (slash >= 0)
                    name = name[(slash + 1)..];
                if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                    name = name[..^".bsp".Length];
                // Skip brush/prefab box models (b_*.bsp) the compiler ships; they aren't playable maps.
                if (name.Length > 0 && !name.StartsWith("b_", StringComparison.OrdinalIgnoreCase))
                    found.Add(name);
            }
        }

        if (found.Count > 0)
            return new List<string>(found);

        return Fallback;
    }

    /// <summary>
    /// The bsp name (file stem) at list index <paramref name="i"/> in the sorted <see cref="Available"/>
    /// catalog — the C# successor to QC <c>MapInfo_BSPName_ByID(i)</c> (mapinfo.qc), which the create-game
    /// map-info dialog uses to resolve the double-clicked row to a map (QC <c>MapInfo_Get_ByID</c>). Returns
    /// null when <paramref name="i"/> is out of range.
    /// </summary>
    public static string? ByIndex(int i)
    {
        IReadOnlyList<string> maps = Available();
        return (i >= 0 && i < maps.Count) ? maps[i] : null;
    }

    /// <summary>
    /// The installed maps that support <paramref name="gametype"/> — QC <c>MapInfo_CheckMap</c>'s filter, the
    /// one <c>getlsmaps()</c> applies. A map with no <c>.mapinfo</c> declares no gametypes and is treated as
    /// supporting everything, exactly as the create-game list treats it (QC MapInfo autogeneration).
    /// Empty/blank <paramref name="gametype"/> means "no filter".
    /// </summary>
    public static IReadOnlyList<string> ForGametype(string? gametype)
    {
        IReadOnlyList<string> all = Available();
        if (string.IsNullOrWhiteSpace(gametype))
            return all;

        var kept = new List<string>(all.Count);
        foreach (string map in all)
        {
            MapInfoCache.Entry info = MapInfoCache.Get(map);
            if (info.Gametypes.Count == 0 || info.Gametypes.Contains(gametype))
                kept.Add(map);
        }
        return kept;
    }

    /// <summary>
    /// QC <c>getlsmaps()</c>'s reply line for the maps installed HERE: <c>^7Maps available (N): </c> followed by
    /// the names in alternating ^3/^2, capped at QC's <c>LSMAPS_MAX</c>. This is the single formatter for the
    /// answer — the client console prints it when no server is running, and a listen/dedicated host feeds the
    /// same text to the server's <c>lsmaps</c> reply — so the two can never disagree about the format or the
    /// contents.
    /// </summary>
    public static string LsmapsReply(string? gametype)
    {
        IReadOnlyList<string> maps = ForGametype(gametype);
        var sb = new System.Text.StringBuilder();
        int added = 0;
        foreach (string map in maps)
        {
            added++;
            if (added > LsmapsMax)
                continue; // QC counts every map, it just stops listing them past the cap
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(added % 2 == 0 ? "^2" : "^3").Append(map);
        }
        if (added > LsmapsMax)
            sb.Append(" ^7(").Append(added - LsmapsMax).Append(" not listed)");
        return $"^7Maps available ({added}): {sb}";
    }

    /// <summary>QC <c>LSMAPS_MAX</c> (server/command/getreplies.qc): the cap on how many maps one reply lists,
    /// there because the reply is a single network string.</summary>
    public const int LsmapsMax = 250;

    /// <summary>
    /// The index of <paramref name="bspName"/> in the sorted <see cref="Available"/> catalog, or -1 — the C#
    /// successor to the QC maplist index lookup (used to seed the map-info dialog's currentMapIndex).
    /// </summary>
    public static int IndexOf(string bspName)
    {
        IReadOnlyList<string> maps = Available();
        for (int i = 0; i < maps.Count; ++i)
            if (string.Equals(maps[i], bspName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
