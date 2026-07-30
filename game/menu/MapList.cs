using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The available-maps catalog for the Create-Game screen. C# successor to QC's <c>MapInfo</c> /
/// <c>maplist.qc</c> directory scan: Xonotic enumerated <c>maps/*.bsp</c> (+ a <c>.mapinfo</c> per map);
/// here we scan the same <c>maps/</c> directory for <c>.bsp</c> files under the Godot resource and user
/// roots, and fall back to a representative hardcoded list when no maps are installed (so the menu is
/// always usable, e.g. when shown in isolation during development).
/// </summary>
public static class MapList
{
    // Directories to scan for installed maps. res://maps is the in-tree resource root; the second is the
    // user's loose-maps drop folder under ~/XonData (resolved to an absolute OS path). Mirrors the engine's
    // maps/ search; DirAccess.Open accepts both Godot virtual paths and absolute OS paths.
    private static string[] SearchDirs => new[] { "res://maps", UserPaths.Resolve("maps") };

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
    /// The list of selectable map names (file stem, e.g. "dm_example"), sorted and de-duplicated. Returns
    /// the on-disk scan when any maps are found, otherwise the hardcoded fallback list.
    /// </summary>
    public static IReadOnlyList<string> Available()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) The mounted content VFS — the REAL installed maps (inside the official pk3s). This is the
        //    authoritative source once the menu has booted (MenuState.Boot mounts the gamedir).
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

        // 2) Loose maps under the Godot resource/user roots (legacy / dev override).
        foreach (string root in SearchDirs)
            ScanDir(root, found);

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

    /// <summary>Add every <c>*.bsp</c> stem under <paramref name="dir"/> to <paramref name="into"/>.</summary>
    private static void ScanDir(string dir, ISet<string> into)
    {
        if (!DirAccess.DirExistsAbsolute(dir))
            return;

        using var da = DirAccess.Open(dir);
        if (da is null)
            return;

        foreach (string file in da.GetFiles())
        {
            // Godot reports an "imported" .bsp as "name.bsp.import"; accept both forms.
            string name = file;
            if (name.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
                name = name[..^".import".Length];

            if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                into.Add(name[..^".bsp".Length]);
        }
    }
}
