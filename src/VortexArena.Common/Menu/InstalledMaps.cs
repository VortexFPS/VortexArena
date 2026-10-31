// Which installed maps a map list offers, and what counts as "a map that exists" — the parts of
// qcsrc/common/mapinfo.qc (MapInfo_Enumerate's search_begin("maps/*.bsp"), MapInfo_ForbiddenFlags,
// MapInfo_RequiredFlags) that every map list in the port shares: the Create Game list, lsmaps, the
// server's empty-g_maplist rotation and the editor's `editor <map>` check. Godot-free so it is testable;
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

    /// <summary>
    /// The bare map name in what a player typed after <c>map</c> or <c>editor</c>: <c>maps/stormkeep.bsp</c>,
    /// <c>stormkeep.bsp</c> and <c>stormkeep</c> all name <c>stormkeep</c>. Backslashes are treated as
    /// slashes. Whatever is left may still contain a <c>/</c>; that is for the caller's existence check to
    /// reject, not for this to guess at.
    /// </summary>
    public static string Stem(string typed)
    {
        string s = (typed ?? "").Trim().Replace('\\', '/');
        if (s.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
            s = s["maps/".Length..];
        if (s.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
            s = s[..^".bsp".Length];
        return s;
    }

    /// <summary>
    /// The <c>editor &lt;map&gt;</c> check: null when there is something to open, otherwise the message to
    /// print. The editor opens two kinds of map — an installed one (<c>maps/&lt;name&gt;.bsp</c> on the search
    /// path, imported live) and one the mapper saved with <c>editor_save &lt;name&gt;</c> (a <c>.vmap</c> in the
    /// editor's own directory), which may have no compiled bsp at all. A name that is neither used to "open"
    /// silently: the host came up on a flat test floor with no document, which looks like a broken editor.
    /// </summary>
    /// <param name="typed">The argument as typed.</param>
    /// <param name="installed">Whether <c>maps/&lt;stem&gt;.bsp</c> is on the search path.</param>
    /// <param name="saved">Whether the editor has a saved <c>&lt;stem&gt;.vmap</c>.</param>
    public static string? EditorOpenError(string typed, Func<string, bool> installed, Func<string, bool> saved)
    {
        string stem = Stem(typed);
        if (stem.Length == 0)
            return "editor: no map named — use `editor <map>`.";
        if (stem.IndexOf('/') >= 0)
            return $"editor: \"{stem}\" is not a map name — a map is maps/<name>.bsp, with no folder in between. "
                   + "`lsmaps` lists the installed maps.";
        if (installed(stem) || saved(stem))
            return null;
        return $"editor: there is no map called \"{stem}\" — maps/{stem}.bsp is not installed and there is no "
               + $"saved {stem}.vmap. `lsmaps` lists the installed maps and `vmap_list` the saved ones; after "
               + "adding a map pack, run `fs_rescan`.";
    }
}
