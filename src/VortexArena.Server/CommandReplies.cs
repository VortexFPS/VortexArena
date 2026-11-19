// Port of Base/data/xonotic-data.pk3dir/qcsrc/server/command/getreplies.qc (getmaplist / getlsmaps /
// getmonsterlist / getrankings / getrecords / getladder) + the cvar_changes/cvar_purechanges build in
// server/world.qc (cvar_changes_init).
using System.Collections.Generic;
using System.Globalization;
using VortexArena.Common.Gameplay.Scoring;
using System.Linq;
using System.Text;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services; // Api
using VortexArena.Engine.Simulation; // CvarService

namespace VortexArena.Server;

/// <summary>
/// Common command replies from <c>getreplies.qc</c>. The native commands read the current world settings,
/// mounted map catalog and record database on request.
/// </summary>
public sealed class CommandReplies
{
    private readonly GameWorld _world;

    public CommandReplies(GameWorld world) => _world = world;

    /// <summary>
    /// Optional host seam for the real installed-map catalog, keyed by gametype: the host hands back QC's
    /// finished <c>getlsmaps()</c> line for the maps it has mounted. The native host uses
    /// <see cref="RecordMapCatalog"/> so new-map markers and record reports share the same selection.
    /// </summary>
    public System.Func<string, string>? MapCatalogReply { get; set; }

    /// <summary>Mounted maps in catalog order and filtered for the requested mode.</summary>
    public System.Func<string, IReadOnlyList<string>>? RecordMapCatalog { get; set; }

    private const int RecordsPerPage = 200;
    private const int RecordPageCount = 10;
    private const int LadderRankCount = 10;
    private const int LadderSize = 30;
    private const int LadderFirstPoints = 100;

    /// <summary>QC <c>cvar_changes</c> (cvar_changes): the non-default server cvar log.</summary>
    public string CvarChanges { get; private set; } = "// this server runs at default server settings";

    /// <summary>QC <c>cvar_purechanges</c> (cvar_purechanges): the non-default GAMEPLAY cvar log.</summary>
    public string CvarPurechanges { get; private set; } = "// this server runs at default gameplay settings";
    internal int CvarPurechangesCount { get; private set; }

    // ---- generators (getreplies.qc) -----------------------------------------------------------------------

    /// <summary>
    /// QC <c>getmaplist()</c>: "^7Maps in list (N): &lt;colorized words&gt;" from <c>g_maplist</c>, alternating
    /// ^2/^3 by the original position in the list. The host supplies the rotation eligibility checks.
    ///
    /// <para>An empty <c>g_maplist</c> is not an empty rotation: it means "every map that supports the current
    /// gametype", so this lists what <see cref="MapRotation.Init"/> would actually cycle through rather than
    /// reporting nothing. Native splits long lists over several lines and reports a remainder if the list
    /// exceeds the page budget.</para>
    /// </summary>
    internal string GetMaplist()
    {
        // Resolve the same input tiers as rotation. An explicitly configured list remains visible
        // as configured until rotation initialization drops it if every entry is unusable.
        IReadOnlyList<string> words = MapListStore.Resolve(_world.Rotation.AllowedMaps);
        if (words.Count == 0)
            return "^7Map list is empty";

        var pages = new List<StringBuilder> { new() };
        int mapcount = 0, listed = 0;
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            if (!_world.Rotation.MapExists(w) || !_world.Rotation.MapSupportsGametype(w))
                continue;
            mapcount++;
            StringBuilder page = pages[^1];
            if (page.Length + w.Length + 4 > MaplistPageMaxLen)
            {
                if (pages.Count >= MaplistReplyPages)
                    continue; // out of pages: keep counting so the total stays honest
                pages.Add(page = new StringBuilder());
            }
            if (page.Length > 0) page.Append(' ');
            page.Append((i % 2) == 0 ? "^3" : "^2").Append(w);
            listed++;
        }
        if (mapcount > listed)
            pages[^1].Append(" ^7(").Append(mapcount - listed).Append(" not listed)");
        return $"^7Maps in list ({mapcount}): {string.Join('\n', pages)}";
    }

    /// <summary>QC <c>MAPLIST_REPLY_MAXLEN</c>: how much of the map list one reply line carries.</summary>
    public const int MaplistPageMaxLen = 15000;

    /// <summary>QC <c>MAPLIST_REPLY_PAGES</c>: how many such lines the reply may span.</summary>
    public const int MaplistReplyPages = 10;

    /// <summary>
    /// QC <c>getlsmaps()</c>: "^7Maps available (N): &lt;colorized catalog&gt;" over every installed map that
    /// supports the current gametype. A host without an installed catalog falls back to the rotation cvar.
    /// </summary>
    internal string GetLsmaps()
    {
        if (MapCatalogReply is { } catalog)
        {
            // The gametype the filter is against — QC MapInfo_CheckMap against MapInfo_CurrentGametype.
            try { return catalog(_world.CatalogGametype); }
            catch (System.Exception) { /* a broken host seam must not take the command down; fall through */ }
        }

        var maps = RecordMapCatalog?.Invoke(_world.CatalogGametype) ?? SplitWords(Cvars.String("g_maplist"));
        return FormatMapCatalog(maps, Mode);
    }

    public const int LsmapsMax = 250;

    /// <summary>QC getlsmaps. A host supplies the active record mode. An offline menu has no server records.</summary>
    public static string FormatMapCatalog(IReadOnlyList<string> maps, string? recordMode = null)
    {
        var names = new StringBuilder();
        bool hasNew = false;
        for (int i = 0; i < maps.Count && i < LsmapsMax; i++)
        {
            string map = maps[i];
            bool unplayed = recordMode switch
            {
                "rc" => RaceRecords.ReadTime(map, RaceRecords.RaceRecord, 1) == 0,
                "cts" => RaceRecords.ReadTime(map, RaceRecords.CtsRecord, 1) == 0,
                "ctf" => RaceRecords.ReadCaptureBest(map).Time == 0,
                _ => false
            };
            if (i > 0) names.Append(' ');
            string color = (i % 2 == 0, unplayed) switch
            {
                (true, true) => "^5*", (false, true) => "^4*",
                (true, false) => "^3", (false, false) => "^2"
            };
            names.Append(color).Append(map);
            hasNew |= unplayed;
        }
        if (maps.Count > LsmapsMax)
            names.Append(" ^7(").Append(maps.Count - LsmapsMax).Append(" not listed)");
        string note = hasNew ? " (New maps have asterisks marked in blue)" : "";
        return $"^7Maps available ({maps.Count}){note}: {names}";
    }

    /// <summary>
    /// QC <c>getmonsterlist()</c>: "^7Monsters available: &lt;colorized netnames&gt; " over the non-hidden
    /// monster registry (the port's monsters are all non-hidden, so every entry lists). Trailing space matches QC.
    /// </summary>
    internal string GetMonsterlist()
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (Monster m in Monsters.All)
        {
            string col = (i % 2) != 0 ? "^2" : "^3";
            sb.Append(col).Append(m.NetName).Append(' ');
            i++;
        }
        return $"^7Monsters available: {sb}";
    }

    private string Mode => _world.GameType?.NetName ?? "";
    private string RecordType => Mode switch
    {
        "cts" => RaceRecords.CtsRecord,
        "ctf" => RaceRecords.CtfRecord,
        _ => RaceRecords.RaceRecord,
    };

    private IReadOnlyList<string> RecordMaps() => RecordMapCatalog?.Invoke(Mode) ?? System.Array.Empty<string>();

    /// <summary>QC GetRecords hooks. Pages are indexed by map position, including maps without a record.</summary>
    internal string GetRecords(int page)
    {
        var pages = new StringBuilder?[RecordPageCount];
        if (Mode is "rc" or "cts" or "ctf")
        {
            IReadOnlyList<string> maps = RecordMaps();
            string type = RecordType;
            bool ctf = Mode == "ctf";
            for (int i = 0; i < maps.Count && i < RecordsPerPage * RecordPageCount; i++)
            {
                string map = maps[i];
                (float time, string holder) = ctf
                    ? RaceRecords.ReadCaptureBest(map)
                    : (RaceRecords.ReadTime(map, type, 1), RaceRecords.ReadName(map, type, 1));
                if (time <= 0f) continue;
                string display = ctf ? time.ToString("F2", CultureInfo.InvariantCulture) : FormatTime(time);
                StringBuilder target = pages[i / RecordsPerPage] ??= new StringBuilder();
                target.Append(map.PadLeft(32)).Append(' ')
                    .Append(display.PadRight(ctf ? 6 : 8)).Append(' ').Append(holder).Append('\n');
            }
        }

        if (page > 0 && page <= pages.Length && pages[page - 1] is { } selected)
            return selected.ToString();
        var all = new StringBuilder();
        foreach (StringBuilder? entry in pages)
            if (entry is not null) all.Append(entry);
        return all.Length > 0 ? all.ToString() : "No records available for the current gametype.\n";
    }

    /// <summary>QC getrankings uses the world's record type for the current map, including CTF.</summary>
    internal string GetRankings()
    {
        string map = string.IsNullOrEmpty(_world.MapName) ? "(unknown)" : _world.MapName;
        string type = RecordType;
        var rows = new StringBuilder();
        for (int i = 1; i <= RaceRecords.RankingsCnt; i++)
        {
            float time = RaceRecords.ReadTime(map, type, i);
            if (time <= 0f) continue;
            rows.Append(GameScores.CountOrdinal(i).PadLeft(8)).Append(' ')
                .Append(FormatTime(time).PadRight(8)).Append(' ').Append(RaceRecords.ReadName(map, type, i)).Append('\n');
        }
        return rows.Length == 0
            ? $"No records are available for the map: {map}\n"
            : $"Records for {map}:\n{rows}";
    }

    private static string FormatTime(float time) =>
        GameScores.TimeEncodedToString(GameScores.TimeEncode(time), compact: false);

    private sealed class LadderEntry
    {
        public readonly int[] Counts = new int[LadderRankCount + 1];
        public int Points;
        public int Order;
    }

    /// <summary>QC getladder combines each map's top ten positions and speed winner by UID.</summary>
    internal string GetLadder()
    {
        var scores = new Dictionary<string, LadderEntry>(System.StringComparer.Ordinal);
        string rankType = RecordType;
        // The stock ladder chooses CTS speed awards in CTS and Race speed awards in every other mode.
        string speedType = Mode == "cts" ? RaceRecords.CtsRecord : RaceRecords.RaceRecord;
        foreach (string map in RecordMaps())
        {
            if (RaceRecords.ReadSpeedAwardBest(map, speedType) > 0f)
                Award(RaceRecords.ReadSpeedAwardBestUid(map, speedType), 0, LadderFirstPoints / 10);
            for (int rank = 1; rank <= LadderRankCount; rank++)
                if (RaceRecords.ReadTime(map, rankType, rank) > 0f)
                    Award(RaceRecords.ReadUid(map, rankType, rank), rank, LadderFirstPoints / rank);
        }

        if (scores.Count == 0) return "No ladder on this server!\n";
        var reply = new StringBuilder($"Top {LadderSize} ladder rankings:\n^3-----------------------\n\nPos ^3| ^7Total  ^3|");
        for (int rank = 1; rank <= LadderRankCount; rank++)
            reply.Append(" ^7").Append(GameScores.CountOrdinal(rank)).Append(" ^3|");
        reply.Append(" ^7Speed awards ^3| ^7Name\n^3----+--------");
        for (int rank = 1; rank <= LadderRankCount; rank++)
            reply.Append(rank < 10 ? "+-----" : "+------");
        reply.Append("+--------------+--------------------\n");

        int pos = 0;
        foreach (var (uid, score) in scores.OrderByDescending(row => row.Value.Points)
            .ThenBy(row => row.Value.Order).Take(LadderSize))
        {
            reply.Append(GameScores.CountOrdinal(++pos).PadLeft(4)).Append("^3| ^7");
            reply.Append(score.Points.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append("^3| ^7");
            for (int rank = 1; rank <= LadderRankCount; rank++)
                reply.Append(score.Counts[rank].ToString(CultureInfo.InvariantCulture).PadLeft(4))
                    .Append(rank < 10 ? "^3| ^7" : " ^3| ^7");
            reply.Append(score.Counts[0].ToString(CultureInfo.InvariantCulture).PadLeft(13)).Append("^3| ^7")
                .Append(RaceRecords.ReadDisplayName(uid)).Append('\n');
        }
        return reply.ToString();

        void Award(string uid, int rank, int points)
        {
            if (uid.Length == 0) return;
            if (!scores.TryGetValue(uid, out var entry))
            {
                entry = new LadderEntry { Order = scores.Count };
                scores.Add(uid, entry);
            }
            entry.Counts[rank]++;
            entry.Points += points;
        }
    }

    // ---- cvar_changes / cvar_purechanges (server/world.qc cvar_changes_init) -------------------------------

    /// <summary>
    /// Port of <c>cvar_changes_init</c> (server/world.qc:145): walk every cvar, skip the temporary <c>_</c>
    /// cvars and the client/render/private namespaces, and for each whose value differs from its default emit
    /// <c>name "value" // "default"</c>. <see cref="CvarChanges"/> is the full server log; <see cref="CvarPurechanges"/>
    /// is the gameplay-relevant subset (further excludes the maplist/warmup/announce-themselves cvars). The
    /// exclude list mirrors QC's BADPREFIX/BADCVAR set for the common namespaces (a representative subset — the
    /// full QC list is ~120 entries; the namespaces that matter for "is this a gameplay change" are covered).
    /// </summary>
    internal void BuildCvarChanges()
    {
        CvarPurechangesCount = 0;
        if (Api.Services is null)
        {
            CvarChanges = "// this server runs at default server settings";
            CvarPurechanges = "// this server runs at default gameplay settings";
            return;
        }

        CvarService store = _world.Services.CvarsImpl;
        var all = new StringBuilder();
        var pure = new StringBuilder();

        foreach (string k in store.Names.OrderBy(x => x, System.StringComparer.Ordinal))
        {
            if (k.Length == 0 || k[0] == '_') // QC buf_cvarlist(h, "", "_") excludes temporary _ cvars
                continue;
            if (IsExcludedFromChanges(k))
                continue;

            string v = store.GetString(k);
            string d = store.GetDefault(k);
            if (string.Equals(v, d, System.StringComparison.Ordinal)) // QC: if (v == d) continue;
                continue;

            string line = $"{k} \"{v}\" // \"{d}\"";
            all.Append(line).Append('\n');

            // pure = also gameplay-relevant (QC's second exclude pass).
            if (!IsExcludedFromPure(k, v))
            {
                pure.Append(line).Append('\n');
                CvarPurechangesCount++;
            }
        }

        CvarChanges = all.Length == 0
            ? "// this server runs at default server settings"
            : "// this server runs at modified server settings:\n" + all.ToString().TrimEnd('\n');
        CvarPurechanges = pure.Length == 0
            ? "// this server runs at default gameplay settings"
            : "// this server runs at modified gameplay settings:\n" + pure.ToString().TrimEnd('\n');
    }

    /// <summary>QC the BADPREFIX/BADCVAR excludes from cvar_changes (client/render/private namespaces — subset).</summary>
    private static bool IsExcludedFromChanges(string k)
    {
        foreach (string p in ChangesBadPrefixes)
            if (k.StartsWith(p, System.StringComparison.Ordinal))
                return true;
        return ChangesBadCvars.Contains(k);
    }

    /// <summary>QC the extra excludes that make cvar_purechanges "gameplay-relevant only" (subset).</summary>
    private static bool IsExcludedFromPure(string k, string v)
    {
        foreach (string p in PureBadPrefixes)
            if (k.StartsWith(p, System.StringComparison.Ordinal))
                return true;
        return PureBadCvars.Contains(k);
    }

    // QC BADPREFIX(...) entries for cvar_changes (the client/render/menu namespaces + sv_world).
    private static readonly string[] ChangesBadPrefixes =
    {
        "help_", "csqc_", "cvar_check_", "sv_world", "chase_", "cl_", "con_", "scoreboard_",
        "g_waypointsprite_", "gl_", "joy", "hud_", "m_", "menu_", "net_slist_", "r_", "sbar_",
        "scr_", "snd_", "show", "sensitivity", "userbind", "v_", "vid_", "crosshair",
        "notification_", "prvm_", "sv_weaponstats_", "rcon_", "g_forced_team_",
    };

    // QC BADCVAR(...) entries for cvar_changes (internal/client/private singletons + the maplist/admin strings).
    private static readonly HashSet<string> ChangesBadCvars = new(System.StringComparer.Ordinal)
    {
        "gamecfg", "g_configversion", "halflifebsp", "sv_mapformat_is_quake2", "sv_mapformat_is_quake3",
        "mod_q3bsp_lightmapmergepower", "mod_q3bsp_nolightmaps", "fov", "mastervolume", "volume", "bgmvolume",
        "in_pitch_min", "in_pitch_max", "bottomcolor", "topcolor", "playermodel", "g_campaign",
        "developer", "log_dest_udp", "net_address", "net_address_ipv6", "port", "savedgamecfg",
        "serverconfig", "sv_autoscreenshot", "sv_heartbeatperiod", "sv_vote_master_ids",
        "sv_vote_master_password", "sys_colortranslation", "sys_specialcharactertranslation",
        "timeformat", "timestamps", "g_require_stats", "g_chatban_list", "g_playban_list",
        "g_playban_minigames", "g_maplist", "g_maplist_mostrecent", "sv_motd", "sv_termsofservice_url",
        "sv_adminnick", "hostname", "sv_allow_customplayermodels_idlist", "sv_allow_customplayermodels_speciallist",
        // The port mirrors QC's `serverflags` GLOBAL into a cvar so the shared-store client reads it; QC's
        // serverflags is a global (never in the cvarlist), so exclude it from the server-browser pure/impure
        // scan — it's a computed value derived from already-listed sv_* cvars, not an admin setting.
        "serverflags",
    };

    // QC the extra BADPREFIX(...) for the pure (gameplay) pass.
    private static readonly string[] PureBadPrefixes = { "g_warmup", "sv_info_", "sv_ready_restart_" };

    // QC the extra BADCVAR(...) for the pure (gameplay) pass (the announce-themselves mutators + admin cvars).
    private static readonly HashSet<string> PureBadCvars = new(System.StringComparer.Ordinal)
    {
        "captureleadlimit_override", "condump_stripcolors", "fs_gamedir", "teamplay_mode",
        "timelimit_override", "sv_teamnagger", "g_instagib", "g_new_toys", "g_nix",
        "g_grappling_hook", "g_jetpack",
    };

    // ---- helpers ------------------------------------------------------------------------------------------

    private static List<string> SplitWords(string? s)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(s)) return result;
        foreach (string w in s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
            result.Add(w);
        return result;
    }
}
