using System;
using System.Collections.Generic;
using VortexArena.Common.Services;
using VortexArena.Common.Menu;

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
        var maps = Installed(MenuState.Vfs);
        return maps.Count > 0 ? maps : Fallback;
    }

    internal static IReadOnlyList<string> Installed(VortexArena.Formats.Vfs.VirtualFileSystem? vfs)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // The mounted content VFS is the ONE source: every installed map, from the shipped packs and from the
        // player's own gamedir alike, packed or loose. (It used to also scan a couple of directories with
        // DirAccess for stray .bsp files. That listed maps the game could not then load — the directories were
        // never mounted, so the host resolved maps/<name>.bsp to nothing and came up on an empty world — and it
        // could not see inside a .pk3 at all, which is the form a map is actually distributed in.)
        if (vfs is not null)
        {
            foreach (string vpath in vfs.Find("maps/", "bsp"))
            {
                if (InstalledMaps.BspName(vpath) is { } name)
                    found.Add(name);
            }
        }

        return new List<string>(found);
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
    /// one <c>getlsmaps()</c> applies. Native maps without sidecars derive support from their entities.
    /// Empty/blank <paramref name="gametype"/> means "no filter".
    /// </summary>
    public static IReadOnlyList<string> ForGametype(string? gametype, ICvarService? cvars = null, bool inMenu = false)
        => MapInfoCache.Catalog.ForGametype(Installed(MenuState.Vfs), gametype, cvars ?? MenuState.Cvars, inMenu);

    /// <summary>
    /// QC <c>getlsmaps()</c>'s reply line for the maps installed HERE: <c>^7Maps available (N): </c> followed by
    /// the names in alternating ^3/^2, capped at QC's <c>LSMAPS_MAX</c>. This is the single formatter for the
    /// answer in the offline client console. The hosted reply uses the same formatter and additionally
    /// marks maps with no relevant record in the server's profile.
    /// </summary>
    public static string LsmapsReply(string? gametype, ICvarService? cvars = null)
    {
        return Server.CommandReplies.FormatMapCatalog(ForGametype(gametype, cvars));
    }

    /// <summary>QC <c>LSMAPS_MAX</c> (server/command/getreplies.qc): the cap on how many maps one reply lists,
    /// there because the reply is a single network string.</summary>
    public const int LsmapsMax = Server.CommandReplies.LsmapsMax;

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
