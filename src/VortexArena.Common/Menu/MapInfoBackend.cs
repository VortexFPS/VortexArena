// Port of qcsrc/common/mapinfo.qc (the .mapinfo parser + the create-game map-info dialog's
//   loadMapInfo, qcsrc/menu/xonotic/dialog_multiplayer_create_mapinfo.qc:9-39).
//
// MapInfo_Get_ByName_NoFallbacks (mapinfo.qc:1035-1381) opens maps/<bsp>.mapinfo and parses the
// title / description / author / gametype / has-* / size / hidden / cdtrack lines, accumulating the set
// of supported gametypes. The menu's XonoticMapInfoDialog_loadMapInfo then derives the displayed
// title/author (decolorized, author stripped from title) and the preview-image fallback chain
// (maps/<bsp> → levelshots/<bsp> → nopreview_map), and disables the gametype checklist entries the map
// doesn't support.
//
// This is a faithful, Godot-free (ADR-0008) port: the parser is pure string work; the file read and the
// image-existence probe are injected seams the menu host wires to the Godot VFS (tests pass fakes). The
// QC autogeneration path (writing a .mapinfo by scanning the BSP for spawnpoints/weapons) is NOT ported
// here — without a .mapinfo the map still appears in the create-game list (MapList scans maps/*.bsp), it
// just shows the bsp name as the title and an empty gametype set, which is the honest headless behavior.

using System;
using System.Collections.Generic;

namespace XonoticGodot.Common.Menu;

/// <summary>
/// The parsed contents of one map's <c>.mapinfo</c> — C# successor to the QC <c>MapInfo_Map_*</c> globals
/// (mapinfo.qh:6-13). <see cref="SupportedGametypes"/> holds gametype NetNames (the QC
/// <c>MapInfo_Map_supportedGametypes</c> bitmask, modeled here as a name set since the port has no
/// gametype flag bits).
/// </summary>
public sealed class MapInfo
{
    /// <summary>The bsp/file stem (QC MapInfo_Map_bspname).</summary>
    public string BspName = "";

    /// <summary>Decolorized title, author stripped (QC MapInfo_Map_title after strdecolorize + title_sans_author).</summary>
    public string Title = "";

    /// <summary>Decolorized author (QC MapInfo_Map_author; empty when the source was "&lt;AUTHOR&gt;").</summary>
    public string Author = "";

    /// <summary>Description line (QC MapInfo_Map_description; empty/"&lt;DESCRIPTION&gt;" when unset).</summary>
    public string Description = "";

    /// <summary>The gametype NetNames this map supports (QC MapInfo_Map_supportedGametypes).</summary>
    public readonly HashSet<string> SupportedGametypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if a "hidden" line was present (QC MAPINFO_FLAG_HIDDEN).</summary>
    public bool Hidden;

    /// <summary>True if a usable .mapinfo (or .arena/.defi — not ported) was found; false when the parser fell back to bare defaults.</summary>
    public bool HasMapInfoFile;

    /// <summary>
    /// QC <c>vlen(MapInfo_Map_maxs - MapInfo_Map_mins)</c> (mapinfo.qc:414): the 3-D bounding diameter of
    /// the map in game units, derived from the <c>size x1 y1 z1 x2 y2 z2</c> line when present.
    /// <c>null</c> when no <c>size</c> line was found (i.e. the bounds are unknown at parse time).
    ///
    /// Used by <see cref="MapInfoBackend.ApplyForcedGametypes"/> to apply the
    /// <c>Duel.m_isAlwaysSupported(diameter &lt; 3250)</c> guard: if the diameter is known AND ≥ 3250,
    /// forced-duel is suppressed even for DM maps (matching the intent of duel.qh:19's TODO comment).
    /// </summary>
    public float? Diameter;

    /// <summary>Does this map support <paramref name="gametypeNetName"/>? (QC MapInfo_Map_supportedGametypes &amp; flags.)</summary>
    public bool Supports(string gametypeNetName) => SupportedGametypes.Contains(gametypeNetName);
}

/// <summary>
/// Parses <c>maps/&lt;bsp&gt;.mapinfo</c> and resolves a map's display title/author/preview + supported
/// gametypes — C# successor to <c>MapInfo_Get_ByName_NoFallbacks</c> (mapinfo.qc:1035-1381) and the
/// create-game dialog's <c>loadMapInfo</c> (dialog_multiplayer_create_mapinfo.qc:9-39). Godot-free: the
/// file read + image-exists probe are injected (the menu host wires them to the VFS).
/// </summary>
public sealed class MapInfoBackend
{
    private const string Untitled = "<TITLE>";
    private const string Unauthored = "<AUTHOR>";
    private const string Undescribed = "<DESCRIPTION>";

    private readonly Func<string, string?> _readText;  // vpath → text, or null when absent
    private readonly Func<string, bool> _imageExists;  // vpath → does the preview image exist
    private readonly Dictionary<string, MapInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// QC <c>Duel.m_isForcedSupported</c> (duel.qh:15-28): when true, any map that supports DM but does
    /// not explicitly list duel is auto-promoted to also support duel. Mirrors <c>!autocvar_g_duel_not_dm_maps</c>
    /// (default 0 → forced support IS active). The menu host wires this from the cvar so the user can opt
    /// out by setting <c>g_duel_not_dm_maps 1</c>. Default: true (Base default).
    /// </summary>
    public bool ForceDuelOnDmMaps { get; set; } = true;

    /// <summary>
    /// QC <c>TeamDeathmatch.m_isForcedSupported</c> (tdm.qh): when <c>autocvar_g_tdm_on_dm_maps</c> is set,
    /// any map that supports DM but does not explicitly list TDM is auto-promoted to also support TDM.
    /// NOTE the OPPOSITE polarity to Duel: <c>g_tdm_on_dm_maps</c> defaults to <b>0</b> ("make all DM maps
    /// automatically support TDM" only when set), so forced TDM support is OFF by default — whereas
    /// <c>g_duel_not_dm_maps</c> defaults to 0 meaning Duel forced support is ON. The menu host wires this
    /// from the cvar. Default: false (Base default g_tdm_on_dm_maps 0).
    /// </summary>
    public bool ForceTdmOnDmMaps { get; set; } = false;

    /// <param name="readText">Reads a map's .mapinfo text (e.g. "maps/foo.mapinfo"), or null if it isn't present.</param>
    /// <param name="imageExists">Probes whether a preview image vpath exists (the QC draw_PictureExists chain).</param>
    public MapInfoBackend(Func<string, string?> readText, Func<string, bool> imageExists)
    {
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _imageExists = imageExists ?? (_ => false);
    }

    /// <summary>
    /// Get (and cache) the parsed info for map <paramref name="bspName"/> — QC <c>MapInfo_Get_ByName</c>
    /// with the cache-retrieve shortcut (mapinfo.qc:1050-1052). The bsp name is the file stem
    /// (e.g. "stormkeep"); a "/" in it is rejected like the QC (mapinfo.qc:1044-1048).
    ///
    /// After parsing, applies <c>Duel.m_isForcedSupported</c>: if DM is supported and duel is not, and
    /// <see cref="ForceDuelOnDmMaps"/> is true (the default, matching <c>g_duel_not_dm_maps=0</c>), duel
    /// is added to the supported set. The same pass applies <c>TeamDeathmatch.m_isForcedSupported</c>
    /// (tdm.qh): if DM is supported and TDM is not, and <see cref="ForceTdmOnDmMaps"/> is true (i.e.
    /// <c>g_tdm_on_dm_maps</c> is set — default OFF), TDM is added too. This matches QC's
    /// <c>MapInfo_Get_ByName</c> (mapinfo.qc:1386):
    /// <c>FOREACH(Gametypes, it.m_isForcedSupported(it), _MapInfo_Map_ApplyGametypeEx(...))</c>.
    /// </summary>
    public MapInfo Get(string bspName)
    {
        if (_cache.TryGetValue(bspName, out MapInfo? cached))
            return cached;

        MapInfo info = Parse(bspName);
        // QC m_isForcedSupported (mapinfo.qc:1386 FOREACH(Gametypes, it.m_isForcedSupported(it), ...)):
        // add duel to any DM map unless g_duel_not_dm_maps, and add tdm to any DM map when g_tdm_on_dm_maps.
        ApplyForcedGametypes(info, forceDuelOnDmMaps: ForceDuelOnDmMaps, forceTdmOnDmMaps: ForceTdmOnDmMaps);
        _cache[bspName] = info;
        return info;
    }

    /// <summary>
    /// Apply the <c>m_isForcedSupported</c> set-promotions to an already-parsed <see cref="MapInfo"/>:
    /// <list type="bullet">
    /// <item><c>Duel.m_isForcedSupported</c> (duel.qh:15-28): if the map supports DM but not duel, and
    /// <paramref name="forceDuelOnDmMaps"/> is true (i.e. <c>g_duel_not_dm_maps == 0</c>), add <c>"duel"</c>.</item>
    /// <item><c>TeamDeathmatch.m_isForcedSupported</c> (tdm.qh): if the map supports DM but not tdm, and
    /// <paramref name="forceTdmOnDmMaps"/> is true (i.e. <c>g_tdm_on_dm_maps</c> is set — default OFF),
    /// add <c>"tdm"</c>.</item>
    /// </list>
    /// Can also be called by callers that manage their own mapinfo cache (e.g. <c>MapInfoCache</c>).
    /// </summary>
    public static void ApplyForcedGametypes(MapInfo info, bool forceDuelOnDmMaps, bool forceTdmOnDmMaps = false)
    {
        if (forceDuelOnDmMaps
            && info.SupportedGametypes.Contains("dm")
            && !info.SupportedGametypes.Contains("duel")
            // QC Duel.m_isAlwaysSupported (duel.qh:12-14): duel auto-supports only maps with diameter < 3250.
            // Applied here to the forced-support path too (matching duel.qh:19's TODO: size check should guard
            // m_isForcedSupported as well). When Diameter is null (no size line in .mapinfo), we conservatively
            // allow forced-duel — the map might be small enough, and we can't prove otherwise from the .mapinfo.
            && !(info.Diameter.HasValue && info.Diameter.Value >= 3250f))
        {
            info.SupportedGametypes.Add("duel");
        }
        // QC TeamDeathmatch.m_isForcedSupported (tdm.qh): g_tdm_on_dm_maps makes DM maps also support TDM.
        if (forceTdmOnDmMaps
            && info.SupportedGametypes.Contains("dm")
            && !info.SupportedGametypes.Contains("tdm"))
        {
            info.SupportedGametypes.Add("tdm");
        }
    }

    /// <summary>Drop the cache (QC MapInfo_Cache invalidation on a content reload).</summary>
    public void ClearCache() => _cache.Clear();

    private MapInfo Parse(string bspName)
    {
        var info = new MapInfo { BspName = bspName };

        if (bspName.IndexOf('/') >= 0) // mapinfo.qc:1044-1048 — invalid char in map name
            return Finish(info, rawTitle: Untitled, rawAuthor: Unauthored, rawDescription: Undescribed);

        string? text = _readText("maps/" + bspName + ".mapinfo");
        if (text == null)
        {
            // No .mapinfo and no .arena/.defi (not ported) and no autogeneration here: bare defaults.
            return Finish(info, rawTitle: Untitled, rawAuthor: Unauthored, rawDescription: Undescribed);
        }

        info.HasMapInfoFile = true;

        // _MapInfo_Map_Reset defaults (mapinfo.qc:443-456).
        string rawTitle = Untitled;
        string rawAuthor = Unauthored;
        string rawDescription = Undescribed;

        foreach (string line in SplitLines(text))
        {
            string s = line;

            // mapinfo.qc:1164-1175 — comment styles + trailing "//".
            if (s.Length == 0) continue;
            if (s[0] == '#') continue;                                  // UNIX style
            if (s.Length >= 2 && s[0] == '/' && s[1] == '/') continue;  // C++ style
            if (s[0] == '_') continue;                                  // q3map style
            int comment = s.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                s = s.Substring(0, comment);

            string t = Car(s, out string rest);
            if (t.Length == 0)
                continue;

            switch (t)
            {
                case "title":
                    rawTitle = rest;
                    break;
                case "description":
                    rawDescription = rest;
                    break;
                case "author":
                    rawAuthor = rest;
                    break;
                case "hidden":
                    info.Hidden = true;
                    break;
                case "type":   // legacy keyword (mapinfo.qc:1216-1226)
                case "gametype":
                {
                    string gt = Car(rest, out _);
                    string? netName = ResolveGametype(gt);
                    if (netName != null)
                        info.SupportedGametypes.Add(netName);
                    break;
                }
                case "size":
                {
                    // QC mapinfo.qc:1236-1271: "size x1 y1 z1 x2 y2 z2" — the lightgrid / world bounding box.
                    // Parse into MapInfo_Map_mins/maxs and compute vlen(maxs-mins) for the diameter gate
                    // (QC _MapInfo_Generate:414; used here for Duel.m_isAlwaysSupported diameter<3250).
                    // The QC rejects lines where mins >= maxs on any axis; we mirror that guard.
                    string[] tok = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length >= 6
                        && float.TryParse(tok[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sx1)
                        && float.TryParse(tok[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sy1)
                        && float.TryParse(tok[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sz1)
                        && float.TryParse(tok[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sx2)
                        && float.TryParse(tok[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sy2)
                        && float.TryParse(tok[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sz2)
                        && sx1 < sx2 && sy1 < sy2 && sz1 < sz2)  // mapinfo.qc:1260-1262 mins-must-be-less-than-maxs guard
                    {
                        float dx = sx2 - sx1, dy = sy2 - sy1, dz = sz2 - sz1;
                        info.Diameter = (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz); // vlen(maxs-mins)
                    }
                    break;
                }
                // has / fog / cdtrack / settemp_for_type / flags: parsed-and-ignored here (the menu
                // map-info dialog only displays title/author/description/preview + the gametype checklist).
                default:
                    break;
            }
        }

        return Finish(info, rawTitle, rawAuthor, rawDescription);
    }

    // Apply the QC display normalization (mapinfo.qc:1360-1373): decolorize, strip author from title.
    private static MapInfo Finish(MapInfo info, string rawTitle, string rawAuthor, string rawDescription)
    {
        // title_sans_author: a title "Foo by Bar" promotes "Bar" to author when author is unset.
        string title = rawTitle;
        string author = rawAuthor;
        TitleSansAuthor(ref title, ref author);

        info.Title = MenuTextFormat.Decolorize(title == Untitled ? info.BspName : title);
        info.Author = (author == Unauthored) ? "" : MenuTextFormat.Decolorize(author); // mapinfo.qc:1372-1373
        info.Description = (rawDescription == Undescribed) ? "" : rawDescription;
        return info;
    }

    /// <summary>
    /// The preview-image fallback chain for map <paramref name="bspName"/> — QC loadMapInfo
    /// (dialog_multiplayer_create_mapinfo.qc:19-30): <c>/maps/&lt;bsp&gt;</c>, then the Quake 3
    /// <c>/levelshots/&lt;bsp&gt;</c>, then <c>nopreview_map</c>. Returns the first that exists (probed via the
    /// injected image-exists seam); always returns a non-null value (the last is the always-available
    /// placeholder).
    /// </summary>
    public string PreviewImage(string bspName)
    {
        string primary = "/maps/" + bspName;
        if (_imageExists(primary))
            return primary;
        string levelshot = "/levelshots/" + bspName; // Quake 3 compatibility (dialog:20-21)
        if (_imageExists(levelshot))
            return levelshot;
        return "nopreview_map"; // dialog:28
    }

    /// <summary>
    /// Resolve a <c>.mapinfo</c> gametype token to a port gametype NetName — C# successor to
    /// <c>MapInfo_Type_FromString</c> (mapinfo.qc:620-648): the deprecated-name remaps, then the gametype's
    /// short name (QC <c>mdl</c>). One port-specific fixup: QC's <c>ft</c> maps to the port's
    /// <c>freezetag</c> NetName (the lone NetName↔short-name mismatch, mirrored from CreateGameScreen).
    /// Returns null for an unknown token (QC returns NULL and the line is ignored). Q3-only forms (arena→ca)
    /// are accepted unconditionally here since the create-game list shows all supported modes.
    /// </summary>
    public static string? ResolveGametype(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        // mapinfo.qc:624-639 — deprecated / compat name remaps to the canonical short name.
        string gt = token switch
        {
            "nexball" => "nb",
            "freezetag" => "ft",
            "keepaway" => "ka",
            "invasion" => "inv",
            "assault" => "as",
            "race" => "rc",
            "ffa" => "dm",
            "cctf" => "ctf",
            "oneflag" => "ctf",
            "tourney" => "duel",
            "arena" => "ca", // Q3 compat (only when is_q3compat in QC; accepted here)
            _ => token,
        };

        // Port fixup: QC short name "ft" ↔ port NetName "freezetag" (CreateGameScreen.GametypeIconName).
        if (gt == "ft")
            return "freezetag";

        return gt;
    }

    // mapinfo.qc:title_sans_author — promote a trailing "by <author>" from the title to the author field.
    // The QC strips a "<title> by <author>" suffix and, if author was "<AUTHOR>", sets it. We mirror the
    // common-case " by " split (the QC also handles a few separators; this covers the shipped maps).
    private static void TitleSansAuthor(ref string title, ref string author)
    {
        if (title == Untitled)
            return;
        int by = title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (by <= 0)
            return;
        string maybeAuthor = title.Substring(by + 4).Trim();
        if (maybeAuthor.Length == 0)
            return;
        title = title.Substring(0, by).Trim();
        if (author == Unauthored)
            author = maybeAuthor;
    }

    // QC car(): the first whitespace-delimited token; rest = the remainder (cdr).
    private static string Car(string s, out string rest)
    {
        int i = 0;
        int n = s.Length;
        while (i < n && IsSpace(s[i])) i++;
        int start = i;
        while (i < n && !IsSpace(s[i])) i++;
        string head = s.Substring(start, i - start);
        while (i < n && IsSpace(s[i])) i++;
        rest = i < n ? s.Substring(i) : "";
        return head;
    }

    private static bool IsSpace(char c) => c == ' ' || c == '\t';

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
