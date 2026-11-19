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
// File access, entity-based generation and preview lookup are injected to keep this parser Godot-free.

using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Common.Services;

namespace VortexArena.Common.Menu;

[Flags]
public enum MapFeatures { None = 0, Weapons = 1, Turrets = 2, Vehicles = 4, Monsters = 8 }

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
    private HashSet<string>? _declaredBeforePromotion;
    private HashSet<string> DeclaredGametypes => _declaredBeforePromotion ?? SupportedGametypes;

    internal void ResetDetailSupport()
    {
        _declaredBeforePromotion ??= new HashSet<string>(SupportedGametypes, StringComparer.OrdinalIgnoreCase);
        SupportedGametypes.Clear();
        SupportedGametypes.UnionWith(_declaredBeforePromotion);
    }

    /// <summary>True if a "hidden" line was present (QC MAPINFO_FLAG_HIDDEN).</summary>
    public bool Hidden;
    public bool Forbidden;
    public bool Frustrating;
    public bool DoNotWant;
    public float MinimumGameVersion;

    public bool CanList(ICvarService cvars, bool inMenu, bool applyRequiredFlags = true)
    {
        float frustrating = cvars.GetFloat("g_maplist_allow_frustrating");
        return ((!Generated && !HasArenaMetadata) || DeclaredGametypes.Count > 0)
            && !Forbidden && (!Hidden || (!inMenu && cvars.GetFloat("g_maplist_allow_hidden") != 0))
            && (!Frustrating || frustrating != 0) && (!applyRequiredFlags || frustrating <= 1 || Frustrating);
    }

    public bool AvoidInRotation(ICvarService cvars) =>
        DoNotWant || MinimumGameVersion > cvars.GetFloat("gameversion");

    /// <summary>True if metadata came from an authored or previously generated sidecar.</summary>
    public bool HasMapInfoFile;
    public bool HasArenaMetadata;
    public bool Generated { get; internal set; }
    public readonly Dictionary<string, MapMatchDefaults> MatchDefaults = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> ModeDeclarationLines = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<MapTempDirective> ServerTempDirectives = new();
    public readonly List<MapTempDirective> ClientTempDirectives = new();
    public string? Fog;
    public MapFeatures Features;
    public string CdTrack = "";

    /// <summary>
    /// QC <c>vlen(MapInfo_Map_maxs - MapInfo_Map_mins)</c> (mapinfo.qc:414): the 3-D bounding diameter of
    /// the map in game units, derived from the <c>size x1 y1 z1 x2 y2 z2</c> line when present.
    /// Generated metadata instead uses the extent of the non-worldspawn entity origins.
    /// </summary>
    public float? Diameter;

    /// <summary>Does this map support <paramref name="gametypeNetName"/>? (QC MapInfo_Map_supportedGametypes &amp; flags.)</summary>
    public bool Supports(string gametypeNetName) => SupportedGametypes.Contains(gametypeNetName);

    /// <summary>MapInfo_CurrentFeatures requires weapon entities only for the stock melee-only combination.</summary>
    public static MapFeatures RequiredFeatures(string? mode, ICvarService cvars)
    {
        if (cvars.GetFloat("g_melee_only") == 0 || cvars.GetFloat("g_pickup_items") == 0 ||
            mode is "rc" or "cts" or "nb" or "ca" or "freezetag" or "lms" ||
            cvars.GetFloat("g_instagib") != 0 || cvars.GetFloat("g_overkill") != 0 ||
            cvars.GetFloat("g_nix") != 0 || cvars.GetFloat("g_weaponarena") != 0)
            return MapFeatures.None;
        return MapFeatures.Weapons;
    }

    /// <summary>Check the catalog against current settings, preserving open eligibility for missing metadata.</summary>
    public bool SupportsInCatalog(string gametypeNetName, MapForcedSupport settings) =>
        (!Generated && !HasArenaMetadata && DeclaredGametypes.Count == 0) || SupportsInDetails(gametypeNetName, settings);

    public bool SupportsInDetails(string gametypeNetName, MapForcedSupport settings) =>
        DeclaredGametypes.Contains(gametypeNetName)
        || AllowsForcedGametype(gametypeNetName, settings);

    internal bool AllowsForcedGametype(string gametypeNetName, MapForcedSupport settings)
    {
        HashSet<string> declared = DeclaredGametypes;
        return gametypeNetName.ToLowerInvariant() switch
        {
            "duel" => settings.DuelOnDmMaps && declared.Contains("dm"),
            "tdm" => settings.TdmOnDmMaps && declared.Contains("dm"),
            "mayhem" or "tmayhem" => declared.Contains("dm") || declared.Contains("tdm"),
            // Upstream evaluates Team Deathmatch before Team Keepaway.
            "tka" => (settings.TkaOnKaMaps && declared.Contains("ka"))
                || (settings.TkaOnTdmMaps && (declared.Contains("tdm") || (settings.TdmOnDmMaps && declared.Contains("dm")))),
            "surv" => settings.SurvivalOnLmsMaps && declared.Contains("lms"),
            _ => false,
        };
    }
}

/// <summary>The cvar-controlled rules for offering additional modes on declared maps.</summary>
public readonly record struct MapForcedSupport(
    bool DuelOnDmMaps, bool TdmOnDmMaps, bool TkaOnKaMaps, bool TkaOnTdmMaps, bool SurvivalOnLmsMaps)
{
    public static MapForcedSupport Default { get; } = new(
        DuelOnDmMaps: true, TdmOnDmMaps: false, TkaOnKaMaps: true, TkaOnTdmMaps: false, SurvivalOnLmsMaps: true);

    public static MapForcedSupport FromCvars(ICvarService cvars) => new(
        DuelOnDmMaps: cvars.GetFloat("g_duel_not_dm_maps") == 0f,
        TdmOnDmMaps: cvars.GetFloat("g_tdm_on_dm_maps") != 0f,
        TkaOnKaMaps: !cvars.Has("g_tka_on_ka_maps") || cvars.GetFloat("g_tka_on_ka_maps") != 0f,
        TkaOnTdmMaps: cvars.GetFloat("g_tka_on_tdm_maps") != 0f,
        SurvivalOnLmsMaps: cvars.GetFloat("g_survival_not_lms_maps") == 0f);
}

/// <summary>
/// Parses <c>maps/&lt;bsp&gt;.mapinfo</c> and resolves a map's display title/author/preview + supported
/// gametypes — C# successor to <c>MapInfo_Get_ByName_NoFallbacks</c> (mapinfo.qc:1035-1381) and the
/// create-game dialog's <c>loadMapInfo</c> (dialog_multiplayer_create_mapinfo.qc:9-39). Godot-free: the
/// file read + image-exists probe are injected (the menu host wires them to the VFS).
/// </summary>
public sealed class MapInfoBackend
{
    // Stock gametype_init short names and selection cvars, shared with the legacy host.
    public static readonly IReadOnlyList<(string ShortName, string Cvar)> StockGametypeCvars = new (string, string)[]
    {
        ("dm", "g_dm"), ("tdm", "g_tdm"), ("ctf", "g_ctf"), ("ca", "g_ca"), ("ft", "g_freezetag"), ("kh", "g_keyhunt"),
        ("dom", "g_domination"), ("lms", "g_lms"), ("ka", "g_keepaway"), ("tka", "g_tka"), ("cts", "g_cts"), ("rc", "g_race"),
        ("as", "g_assault"), ("ons", "g_onslaught"), ("nb", "g_nexball"), ("inv", "g_invasion"), ("mayhem", "g_mayhem"),
        ("tmayhem", "g_tmayhem"), ("duel", "g_duel"), ("surv", "g_survival"),
    };

    // The stock registry sorts its registered IDs before MapInfo_CurrentGametype examines flags.
    // For these stock modes their flag names have that same order.
    private static readonly (string ShortName, string Cvar)[] SelectionOrder =
        StockGametypeCvars.OrderBy(mode => mode.Cvar, StringComparer.Ordinal).ToArray();

    public static string ConfiguredGametype(ICvarService cvars)
    {
        string? previous = ResolveGametype(cvars.GetString("gamecfg"));
        bool known = false;
        foreach (var (shortName, flag) in SelectionOrder)
        {
            string mode = shortName == "ft" ? "freezetag" : shortName;
            if (mode == previous) known = true;
            else if (cvars.GetFloat(flag) != 0) return mode;
        }
        return known ? previous! : "dm";
    }
    private const string Untitled = "<TITLE>";
    private const string Unauthored = "<AUTHOR>";
    private const string Undescribed = "<DESCRIPTION>";

    private readonly Func<string, string?> _readText;  // vpath → text, or null when absent
    private readonly Func<string, bool> _imageExists;  // vpath → does the preview image exist
    private readonly Func<MapInfo, bool>? _generate;
    private readonly Func<MapInfo, bool>? _readArena;
    private readonly Dictionary<string, MapInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Keep Duel and TDM first so the existing detail dialog fallback order stays intact.
    private static readonly string[] ForcedGametypes = { "duel", "tdm", "mayhem", "surv", "tka", "tmayhem" };

    public MapForcedSupport ForcedSupport { get; init; } = MapForcedSupport.Default;

    /// <param name="readText">Reads a map's .mapinfo text (e.g. "maps/foo.mapinfo"), or null if it isn't present.</param>
    /// <param name="imageExists">Probes whether a preview image vpath exists (the QC draw_PictureExists chain).</param>
    /// <param name="generate">Populates raw metadata for a native map after both sidecar lookups miss.</param>
    public MapInfoBackend(Func<string, string?> readText, Func<string, bool> imageExists,
        Func<MapInfo, bool>? generate = null, Func<MapInfo, bool>? readArena = null)
    {
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _imageExists = imageExists ?? (_ => false);
        _generate = generate;
        _readArena = readArena;
    }

    /// <summary>
    /// Get (and cache) the parsed info for map <paramref name="bspName"/> — QC <c>MapInfo_Get_ByName</c>
    /// with the cache-retrieve shortcut (mapinfo.qc:1050-1052). The bsp name is the file stem
    /// (e.g. "stormkeep"); a "/" in it is rejected like the QC (mapinfo.qc:1044-1048).
    /// </summary>
    public MapInfo Get(string bspName)
    {
        if (_cache.TryGetValue(bspName, out MapInfo? cached))
            return cached;

        MapInfo info = Parse(bspName);
        ApplyForcedGametypes(info, ForcedSupport);
        _cache[bspName] = info;
        return info;
    }

    /// <summary>Add forced modes to the details for an already-parsed map.</summary>
    public static void ApplyForcedGametypes(MapInfo info, MapForcedSupport settings)
    {
        info.ResetDetailSupport();
        foreach (string mode in ForcedGametypes)
            if (info.AllowsForcedGametype(mode, settings))
                info.SupportedGametypes.Add(mode);
    }

    /// <summary>Drop the cache (QC MapInfo_Cache invalidation on a content reload).</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>Read the declared metadata without applying the current gametype policy.</summary>
    public MapInfo Parse(string bspName)
    {
        var info = new MapInfo { BspName = bspName };

        if (bspName.Length == 0 || bspName.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return Finish(info, rawTitle: Untitled, rawAuthor: Unauthored, rawDescription: Undescribed);

        string? text = _readText("maps/" + bspName + ".mapinfo");
        if (text is null && _readArena?.Invoke(info) == true)
            return FinishGenerated(info);
        text ??= _readText("maps/autogenerated/" + bspName + ".mapinfo");
        if (text == null)
        {
            if (_generate?.Invoke(info) == true)
            {
                info.Generated = true;
                return FinishGenerated(info);
            }
            return Finish(info, rawTitle: Untitled, rawAuthor: Unauthored, rawDescription: Undescribed);
        }

        info.HasMapInfoFile = true;

        // _MapInfo_Map_Reset defaults (mapinfo.qc:443-456).
        string rawTitle = Untitled;
        string rawAuthor = Unauthored;
        string rawDescription = Undescribed;

        int lineNumber = 0;
        foreach (string line in SplitLines(text))
        {
            lineNumber++;
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

            switch (t.ToLowerInvariant())
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
                case "cdtrack":
                    info.CdTrack = rest.Trim();
                    break;
                case "has":
                    info.Features |= Car(rest, out _).ToLowerInvariant() switch
                    {
                        "weapons" or "new_toys" => MapFeatures.Weapons,
                        "turrets" => MapFeatures.Turrets,
                        "vehicles" => MapFeatures.Vehicles,
                        "monsters" => MapFeatures.Monsters,
                        _ => MapFeatures.None,
                    };
                    break;
                case "hidden":
                    info.Hidden = true;
                    break;
                case "forbidden":
                    info.Forbidden = true;
                    break;
                case "frustrating":
                    info.Frustrating = true;
                    break;
                case "donotwant":
                case "noautomaplist":
                    info.DoNotWant = true;
                    break;
                case "gameversion_min":
                    if (float.TryParse(Car(rest, out _), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float version))
                        info.MinimumGameVersion = System.Math.Max(info.MinimumGameVersion, version);
                    break;
                case "type":   // legacy keyword (mapinfo.qc:1216-1226)
                case "gametype":
                {
                    string gt = Car(rest, out _);
                    string? netName = ResolveGametype(gt);
                    if (netName != null)
                    {
                        info.SupportedGametypes.Add(netName);
                        info.ModeDeclarationLines[netName] = lineNumber;
                        if (t.Equals("gametype", StringComparison.OrdinalIgnoreCase))
                        {
                            Car(rest, out string options);
                            info.MatchDefaults[netName] = MapMatchDefaults.Parse(options);
                        }
                        else if (netName.Equals("kh", StringComparison.OrdinalIgnoreCase))
                        {
                            Car(rest, out string limits);
                            string points = Car(limits, out string afterPoints);
                            string time = Car(afterPoints, out string afterTime);
                            string teams = Car(afterTime, out string afterTeams);
                            string lead = Car(afterTeams, out _);
                            info.MatchDefaults[netName] = MapMatchDefaults.Parse(
                                $"pointlimit={points} timelimit={time} teams={teams} leadlimit={lead}");
                        }
                        else if (netName.Equals("tdm", StringComparison.OrdinalIgnoreCase)
                            || netName.Equals("tmayhem", StringComparison.OrdinalIgnoreCase))
                        {
                            Car(rest, out string limits);
                            Car(limits, out string afterScore);
                            Car(afterScore, out string afterTime);
                            string teams = Car(afterTime, out _);
                            info.MatchDefaults[netName] = MapMatchDefaults.Parse($"teams={teams}");
                        }
                    }
                    break;
                }
                case "fog":
                    if (MapFogSettings.TryParse(rest, out _))
                        info.Fog = rest;
                    break;
                case "settemp_for_type":
                case "clientsettemp_for_type":
                {
                    string selector = Car(rest, out string body);
                    string? mode = selector == "all" ? selector : ResolveGametype(selector);
                    if (mode is not null && body.Length > 0)
                    {
                        var directives = t.Equals("clientsettemp_for_type", StringComparison.OrdinalIgnoreCase)
                            ? info.ClientTempDirectives : info.ServerTempDirectives;
                        directives.Add(new MapTempDirective(mode, lineNumber, body));
                    }
                    break;
                }
                case "size":
                {
                    // QC mapinfo.qc:1236-1271: "size x1 y1 z1 x2 y2 z2" — the lightgrid / world bounding box.
                    // Parse into MapInfo_Map_mins/maxs and compute vlen(maxs-mins) for the diameter gate
                    // QC _MapInfo_Generate also measures diameter for generation-time mode inference.
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
                // Fog and settemp directives are applied by map boot in QC.
                default:
                    break;
            }
        }

        return Finish(info, rawTitle, rawAuthor, rawDescription);
    }

    private static MapInfo FinishGenerated(MapInfo info) =>
        Finish(info, info.Title.Length > 0 ? info.Title : Untitled,
            info.Author.Length > 0 ? info.Author : Unauthored, info.Description);

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
    /// Preserves custom mode names. Q3-only forms (arena→ca)
    /// are accepted unconditionally here since the create-game list shows all supported modes.
    /// </summary>
    public static string? ResolveGametype(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        // mapinfo.qc:624-639 — deprecated / compat name remaps to the canonical short name.
        string gt = token.ToLowerInvariant() switch
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
        if (gt.Equals("ft", StringComparison.OrdinalIgnoreCase))
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
