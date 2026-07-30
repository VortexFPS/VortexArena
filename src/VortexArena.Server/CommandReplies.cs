// Port of Base/data/xonotic-data.pk3dir/qcsrc/server/command/getreplies.qc (getmaplist / getlsmaps /
// getmonsterlist / getrankings / getrecords / getladder) + the cvar_changes/cvar_purechanges build in
// server/world.qc (cvar_changes_init).
using System.Collections.Generic;
using System.Linq;
using System.Text;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services; // Api
using XonoticGodot.Engine.Simulation; // CvarService

namespace XonoticGodot.Server;

/// <summary>
/// The precomputed reply strings for the common commands — the C# successor to <c>getreplies.qc</c> + the
/// reply caches QC builds once at world init (server/world.qc:1022-1038): <c>printmaplist</c>/<c>lsmaps</c>/
/// records/rankings/ladder/the monster list, plus the <c>cvar_changes</c>/<c>cvar_purechanges</c> non-default
/// cvar logs. In QC these are strzoned once and the command bodies just <c>print_to</c> them; this type mirrors
/// that — <see cref="Recompute"/> builds them (called from <c>GameWorld</c> after boot/map load), the commands
/// read the cached fields.
///
/// <para><b>Scope (deviation R8):</b> records/rankings/ladder are race/CTS/CTF-specific and read the persistent
/// race records DB (<c>ServerProgsDB</c>). The port's race store isn't wired into this seam, so these report the
/// honest QC empty case ("No records are available…", "No ladder on this server!") rather than fabricating —
/// exactly what QC prints for a non-race mode / fresh server. getmaplist/getlsmaps/getmonsterlist ARE fully
/// computed from <c>g_maplist</c> + the map catalog (when wired) + the monster registry.</para>
/// </summary>
public sealed class CommandReplies
{
    private readonly GameWorld _world;

    public CommandReplies(GameWorld world) => _world = world;

    // ---- the cached reply strings (QC the strzoned globals in getreplies.qh) -------------------------------

    /// <summary>QC <c>maplist_reply</c> (printmaplist): the colorized g_maplist.</summary>
    public string MaplistReply { get; private set; } = "";

    /// <summary>QC <c>lsmaps_reply</c> (lsmaps): the colorized catalog of available maps.</summary>
    public string LsmapsReply { get; private set; } = "";

    /// <summary>QC <c>monsterlist_reply</c> (editmob spawn list): the colorized monster registry.</summary>
    public string MonsterlistReply { get; private set; } = "";

    /// <summary>QC <c>rankings_reply</c> (rankings): race top-N for the current map (honest-empty without a store).</summary>
    public string RankingsReply { get; private set; } = "";

    /// <summary>QC <c>ladder_reply</c> (ladder): cross-map race ladder (honest-empty without a store).</summary>
    public string LadderReply { get; private set; } = "";

    /// <summary>QC <c>records_reply[10]</c> (records): per-page record strings (all empty without a store).</summary>
    public string[] RecordsReply { get; } = new string[10];

    /// <summary>QC <c>cvar_changes</c> (cvar_changes): the non-default server cvar log.</summary>
    public string CvarChanges { get; private set; } = "// this server runs at default server settings";

    /// <summary>QC <c>cvar_purechanges</c> (cvar_purechanges): the non-default GAMEPLAY cvar log.</summary>
    public string CvarPurechanges { get; private set; } = "// this server runs at default gameplay settings";

    /// <summary>
    /// QC the world-init reply precompute (server/world.qc:1022-1038): rebuild every cached reply string. Called
    /// from <see cref="GameWorld"/> at boot/post-map-load. Cheap; safe to call again on a map change.
    /// </summary>
    public void Recompute()
    {
        MaplistReply = GetMaplist();
        LsmapsReply = GetLsmaps();
        MonsterlistReply = GetMonsterlist();
        RankingsReply = GetRankings();
        LadderReply = GetLadder();
        for (int i = 0; i < RecordsReply.Length; i++)
            RecordsReply[i] = ""; // no race-records hook in this seam (deviation R8)
        BuildCvarChanges();
    }

    // ---- generators (getreplies.qc) -----------------------------------------------------------------------

    /// <summary>
    /// QC <c>getmaplist()</c>: "^7Maps in list (N): &lt;colorized words&gt;" from <c>g_maplist</c>, alternating
    /// ^2/^3 per word. (QC also filters MapInfo_CheckMap; the port colorizes every word — the rotation cvar is
    /// already a curated list, and no catalog is reachable here.)
    /// </summary>
    private string GetMaplist()
    {
        string maplist = Cvars.String("g_maplist");
        if (string.IsNullOrEmpty(maplist))
            return "^7Map list is empty";

        var words = SplitWords(maplist);
        var sb = new StringBuilder();
        int mapcount = 0;
        foreach (string w in words)
        {
            string col = (mapcount % 2) != 0 ? "^2" : "^3";
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(col).Append(w);
            mapcount++;
        }
        return $"^7Maps in list ({mapcount}): {sb}";
    }

    /// <summary>
    /// QC <c>getlsmaps()</c>: "^7Maps available (N): &lt;colorized catalog&gt;". The port's map catalog at this
    /// seam is the rotation list (no full MapInfo enumeration is reachable), so it lists the same words as
    /// <see cref="GetMaplist"/> when present, else "^7Maps available (0): " (the honest empty catalog).
    /// </summary>
    private string GetLsmaps()
    {
        string maplist = Cvars.String("g_maplist");
        var words = SplitWords(maplist);
        var sb = new StringBuilder();
        int added = 0;
        foreach (string w in words)
        {
            string col = (added % 2) != 0 ? "^2" : "^3";
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(col).Append(w);
            added++;
        }
        return $"^7Maps available ({added}): {sb}";
    }

    /// <summary>
    /// QC <c>getmonsterlist()</c>: "^7Monsters available: &lt;colorized netnames&gt; " over the non-hidden
    /// monster registry (the port's monsters are all non-hidden, so every entry lists). Trailing space matches QC.
    /// </summary>
    private string GetMonsterlist()
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

    /// <summary>
    /// QC <c>getrankings()</c> (server/command/getreplies.qc:46): the race/CTS top-N times for the current map,
    /// read from the persistent <see cref="XonoticGodot.Common.Gameplay.RaceRecords"/> table (the C# successor to
    /// the QC <c>ServerProgsDB</c> ranking store). In a non-race mode the table is empty, so this falls through to
    /// QC's verbatim empty case "No records are available for the map: &lt;map&gt;". The record-type segment keys
    /// off the live gametype (CTS_RECORD for cts, RACE_RECORD for rc), exactly as QC's <c>record_type</c> global.
    /// </summary>
    private string GetRankings()
    {
        string map = string.IsNullOrEmpty(_world.MapName) ? "(unknown)" : _world.MapName;

        // QC: getrankings() reads race_readTime(map, i)/race_readName for i in 1..RANKINGS_CNT off record_type.
        // record_type is CTS_RECORD under g_cts / RACE_RECORD otherwise; only those modes file ranked times.
        string gt = _world.GameType?.NetName ?? "";
        string? recordType = gt switch
        {
            "cts" => XonoticGodot.Common.Gameplay.RaceRecords.CtsRecord,
            "rc"  => XonoticGodot.Common.Gameplay.RaceRecords.RaceRecord,
            _     => null,
        };

        if (recordType is not null)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i <= XonoticGodot.Common.Gameplay.RaceRecords.RankingsCnt; i++)
            {
                float t = XonoticGodot.Common.Gameplay.RaceRecords.ReadTime(map, recordType, i);
                if (t == 0f)
                    continue; // QC: if (t == 0) continue;
                string name = XonoticGodot.Common.Gameplay.RaceRecords.ReadName(map, recordType, i);
                string pos = XonoticGodot.Common.Gameplay.Scoring.GameScores.CountOrdinal(i);
                string time = XonoticGodot.Common.Gameplay.Scoring.GameScores.TimeEncodedToString(
                    XonoticGodot.Common.Gameplay.Scoring.GameScores.TimeEncode(t), compact: false);
                // QC: strcat(strpad(8, p), " ", strpad(-8, TIME_ENCODED_TOSTRING(t, false)), " ", n, "\n")
                sb.Append(pos.PadLeft(8)).Append(' ').Append(time.PadRight(8)).Append(' ').Append(name).Append('\n');
            }
            if (sb.Length != 0)
                return $"Records for {map}:\n{sb}"; // QC: strcat("Records for ", map, ":\n", s)
        }

        return $"No records are available for the map: {map}"; // QC empty case
    }

    /// <summary>QC <c>getladder()</c>: the cross-map race ladder. No store wired → QC's "No ladder on this server!".</summary>
    private string GetLadder() => "No ladder on this server!";

    // ---- cvar_changes / cvar_purechanges (server/world.qc cvar_changes_init) -------------------------------

    /// <summary>
    /// Port of <c>cvar_changes_init</c> (server/world.qc:145): walk every cvar, skip the temporary <c>_</c>
    /// cvars and the client/render/private namespaces, and for each whose value differs from its default emit
    /// <c>name "value" // "default"</c>. <see cref="CvarChanges"/> is the full server log; <see cref="CvarPurechanges"/>
    /// is the gameplay-relevant subset (further excludes the maplist/warmup/announce-themselves cvars). The
    /// exclude list mirrors QC's BADPREFIX/BADCVAR set for the common namespaces (a representative subset — the
    /// full QC list is ~120 entries; the namespaces that matter for "is this a gameplay change" are covered).
    /// </summary>
    private void BuildCvarChanges()
    {
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
                pure.Append(line).Append('\n');
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
        "notification_", "prvm_",
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
        "sv_adminnick", "hostname",
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
