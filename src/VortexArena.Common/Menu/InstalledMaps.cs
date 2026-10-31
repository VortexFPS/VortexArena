// Which installed maps a map list offers, and what counts as "a map that exists" — the parts of
// qcsrc/common/mapinfo.qc (MapInfo_Enumerate's search_begin("maps/*.bsp"), MapInfo_ForbiddenFlags,
// MapInfo_RequiredFlags) that every map list in the port shares: the Create Game list, lsmaps, the
// server's empty-g_maplist rotation. Godot-free so it is testable;
// the game host feeds it the VFS keys and the parsed .mapinfo flags.

using System;

namespace VortexArena.Common.Menu;

/// <summary>The rules for turning installed files into the maps a list offers.</summary>
public static class InstalledMaps
{
    /// <summary>The <c>.mapinfo</c> flags that decide whether a list offers a map (QC <c>MAPINFO_FLAG_*</c>).</summary>
    /// <param name="Forbidden"><c>forbidden</c>: never offered — a utility level such as <c>_hudsetup</c>, which
    /// the game loads by name for its own purpose.</param>
    /// <param name="Hidden"><c>hidden</c>: offered only when <c>g_maplist_allow_hidden</c> is set.</param>
    /// <param name="Frustrating"><c>frustrating</c>: offered only when <c>g_maplist_allow_frustrating</c> is set
    /// (and, at 2, the only kind offered).</param>
    public readonly record struct Flags(bool Forbidden, bool Hidden, bool Frustrating);

    /// <summary>
    /// The map name a virtual path names, or null when it is not a map a list should offer. DarkPlaces'
    /// <c>search_begin("maps/*.bsp")</c> never lets the wildcard cross a <c>/</c>, so only a bsp directly under
    /// <c>maps/</c> is a map: a nested one such as <c>maps/_init/_init.bsp</c> (the old DarkPlaces boot stub)
    /// cannot be loaded by its bare name, and Xonotic rejects it ("Invalid character in map name"). The
    /// <c>b_*.bsp</c> brush/prefab boxes the map compiler emits are not playable levels either.
    /// </summary>
    public static string? BspName(string vpath)
    {
        const string prefix = "maps/";
        const string ext = ".bsp";
        if (string.IsNullOrEmpty(vpath)
            || !vpath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !vpath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            || vpath.Length <= prefix.Length + ext.Length)
            return null;

        string name = vpath[prefix.Length..^ext.Length];
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            return null;
        if (name.StartsWith("b_", StringComparison.OrdinalIgnoreCase))
            return null;
        return name;
    }

    /// <summary>
    /// QC <c>MapInfo_ForbiddenFlags</c> + <c>MapInfo_RequiredFlags</c>: whether a map carrying
    /// <paramref name="flags"/> is offered, given <c>g_maplist_allow_hidden</c> and
    /// <c>g_maplist_allow_frustrating</c>.
    /// </summary>
    public static bool IsOffered(Flags flags, bool allowHidden, float allowFrustrating)
    {
        if (flags.Forbidden)
            return false;
        if (flags.Hidden && !allowHidden)
            return false;
        if (allowFrustrating > 1)
            return flags.Frustrating;
        return !flags.Frustrating || allowFrustrating != 0;
    }
}
