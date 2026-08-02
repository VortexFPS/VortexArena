using System;
using System.Collections.Generic;
using System.Net;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Net;

namespace VortexArena.Game.Menu;

/// <summary>
/// One row in the server browser. C# successor to QC's <c>entity</c>-per-server <c>ServerList</c> entries
/// (qcsrc/menu/xonotic/serverlist.qc), trimmed to the columns the browser shows.
/// </summary>
public sealed class ServerEntry
{
    public string Name = "";
    public string Address = "";       // "ip:port" — what Connect parses
    public string Map = "";
    public string Gametype = "";
    public int Players;
    public int Bots;
    public int MaxPlayers;
    public int Ping = -1;             // -1 = unknown / not yet measured
    public bool Favorite;
    public bool IsLan;

    // --- Fields derived from the server's `qcstatus` infostring key (SLIST_FIELD_QCSTATUS). Xonotic servers
    //     pack their gameplay-visible state into one colon-separated token list built by WinningConditionHelper
    //     (qcsrc/server/scores.qc:452): "gametype:version:P<purechanges>:S<freeslots>:F<serverflags>:T<tos>:
    //     M<modname>::<player labels>". The browser's categories, icons and row dimming all read these, which
    //     is why they are parsed onto the row rather than left in the raw string.

    /// <summary>The raw <c>qcstatus</c> value, kept so the Server Info dialog can show it verbatim.</summary>
    public string QcStatus = "";
    /// <summary>The server's game version (<c>g_xonoticversion</c>), token 1 of qcstatus.</summary>
    public string Version = "";
    /// <summary>The mod name (qcstatus <c>M</c>), lowercased. Empty = the server didn't report one.</summary>
    public string ModName = "";
    /// <summary>True when the server reports zero setting changes from stock (qcstatus <c>P0</c>).</summary>
    public bool Pure;
    /// <summary>False when the server didn't report purity at all — then <see cref="Pure"/> means nothing.</summary>
    public bool PureAvailable;
    /// <summary>Slots the gamecode will actually let you play in (qcstatus <c>S</c>); -1 = not reported.</summary>
    public int QcFreeSlots = -1;
    /// <summary>SERVERFLAG_* bits (qcstatus <c>F</c>); -1 = not reported.</summary>
    public int ServerFlags = -1;
    /// <summary>The server's terms-of-service URL (qcstatus <c>T</c>), if it published one.</summary>
    public string TermsOfServiceUrl = "";
    /// <summary>Space-separated player names, for the filter box (SLIST_FIELD_PLAYERS).</summary>
    public string PlayerNames = "";

    /// <summary>True when the server reports that it submits player statistics (SERVERFLAG_PLAYERSTATS).</summary>
    public bool HasPlayerStats => ServerFlags >= 0 && (ServerFlags & ServerListInfo.ServerFlagPlayerStats) != 0;
    /// <summary>... and to a non-default stats server (SERVERFLAG_PLAYERSTATS_CUSTOM).</summary>
    public bool HasCustomStatsServer => ServerFlags >= 0 && (ServerFlags & ServerListInfo.ServerFlagPlayerStatsCustom) != 0;

    /// <summary>Human players (SLIST_FIELD_NUMHUMANS): the connected count minus the bots among them.</summary>
    public int Humans => System.Math.Max(0, Players - Bots);

    /// <summary>Connectable slots (SLIST_FIELD_FREESLOTS) — what the "Full" filter and the row dimming test.</summary>
    public int FreeSlots => MaxPlayers > 0 ? System.Math.Max(0, MaxPlayers - Players) : 1;

    /// <summary>True for a bracketed IPv6 literal address (the QC <c>substring(s,0,1) == "["</c> test).</summary>
    public bool IsIPv6 => Address.StartsWith('[');
    /// <summary>True for a dotted IPv4 literal (QC IS_DIGIT on the first character).</summary>
    public bool IsIPv4 => Address.Length > 0 && Address[0] >= '0' && Address[0] <= '9';

    /// <summary>Which <see cref="ServerCategory"/> this row sorts under; filled by the browser on refresh.</summary>
    public ServerCategory Category = ServerCategory.Normal;

    // --- Joinability. The browser asks the STOCK Xonotic masters for its internet list, so most of what comes
    //     back is Darkplaces servers running the original QuakeC game. VortexArena's netcode is a ground-up
    //     reimplementation and shares no wire format with them, so those rows are listed (they are real
    //     servers, and hiding them would just look like the browser is broken) but not connectable.

    /// <summary>True once this row has been filled in from a real <c>infoResponse</c>, rather than being a
    /// placeholder built from a bookmark or a typed address. Only a queried row can be judged.</summary>
    public bool Queried;

    /// <summary>
    /// True when the server tagged its reply with <see cref="Net.ServerNet.VortexServerKey"/> — i.e. it
    /// speaks this game's protocol rather than Darkplaces'.
    /// </summary>
    public bool IsVortexArena;

    /// <summary>The build-parity hash the server reported, or 0 when it isn't a VortexArena server.</summary>
    public uint BuildParity;

    /// <summary>
    /// A server we have queried and know to be running stock Xonotic (or any other Darkplaces game) — the
    /// case the Join path refuses with an explanation rather than dropping the player into a connection
    /// that cannot possibly complete. An unqueried row is NOT this: we simply don't know yet, and a typed
    /// address deserves the benefit of the doubt.
    /// </summary>
    public bool IsIncompatibleXonotic => Queried && !IsVortexArena;

    /// <summary>Humans/slots, the QC players column (SLIST_FIELD_NUMHUMANS / maxclients).</summary>
    public string PlayersText => MaxPlayers > 0 ? $"{Humans}/{MaxPlayers}" : Humans.ToString();

    /// <summary>
    /// The ping as the browser reads it. <see cref="Ping"/> is -1 while no reply has come back, but the QC
    /// has no such state: an entry the engine has not measured reads 0 out of the host cache, and every
    /// consumer — the column text, the row colour, the sort — treats that as a real zero. Keeping the -1
    /// internally means "unmeasured" is still distinguishable in code; this is what the UI uses.
    /// </summary>
    public int PingOrZero => System.Math.Max(0, Ping);

    public string PingText => PingOrZero.ToString();
}


/// <summary>
/// The chosen match configuration produced by the Create-Game screen and handed to whoever starts the
/// server. C# successor to the bundle of cvars the QC <c>MapList_LoadMap</c> set before issuing the map
/// change (gametype, map, bot count/skill, time/frag limits).
/// </summary>
public sealed class MatchConfig
{
    public string Gametype = "";   // GameType.NetName from the registry (e.g. "dm")
    public string Map = "";
    public int BotCount;
    public int BotSkill = -1;      // 0..10 (QC skill rungs); -1 = unspecified — leave the `skill` cvar alone
                                   // (a bare CLI `--host --bots N` must not stomp the user's/stock skill with 0)
    public int TimeLimit;          // minutes, 0 = none
    public int FragLimit;          // 0 = none

    // Campaign: a non-empty CampaignId boots this match in campaign mode (QC g_campaign 1 + _campaign_name +
    // _campaign_index). The server then resolves gametype/bots/skill/limits/mutators from the campaign file at
    // CampaignIndex; Map/Gametype/BotCount above are the menu's pre-resolved copy, used to load the BSP + fill
    // bots client-side. Empty = a normal Create-Game / Instant-Action match.
    public string CampaignId = "";
    public int CampaignIndex;

    public override string ToString() =>
        $"gametype={Gametype} map={Map} bots={BotCount} skill={BotSkill} " +
        $"timelimit={TimeLimit} fraglimit={FragLimit}" +
        (CampaignId.Length > 0 ? $" campaign={CampaignId}#{CampaignIndex}" : "");
}

/// <summary>
/// The server-browser model: owns the live <see cref="ServerEntry"/> list, persists favorites, runs a
/// best-effort LAN discovery, and queries the Xonotic master servers for the internet list. C# successor
/// to <c>serverlist.qc</c>'s refresh machinery — refresh populates a list, the UI renders it, Connect
/// resolves a row/address to a callback — matching the QC flow.
///
/// Both the LAN sweep and the internet query speak the real Darkplaces connectionless (out-of-band)
/// protocol via <see cref="MasterServerProtocol"/>: a 4×<c>0xFF</c> marker + ASCII command, so the probe
/// matches exactly what a VortexArena server's <c>getinfo</c> handler answers. The internet path is async —
/// <see cref="Refresh"/> kicks the queries off and returns; UDP replies arrive over the following frames,
/// so the menu must pump <see cref="Poll"/> each frame for the rows to fill in.
///
/// Networking is intentionally decoupled: this model never opens a game connection itself. It exposes
/// <see cref="ConnectRequested"/> (an "ip:port" string) which the host's net layer subscribes to and turns
/// into a real connect. Same idea for Create Game via <see cref="MatchConfig"/> and the screen's StartGame
/// callback. Owns a <see cref="MasterServerLink"/> UDP socket, hence <see cref="IDisposable"/>.
/// </summary>
public sealed class ServerBrowser : IDisposable
{
    /// <summary>Favorites persist alongside the menu settings file (<c>~/XonData/favorites.cfg</c> by default).</summary>
    private static string FavoritesPath => UserPaths.Resolve("favorites.cfg");

    /// <summary>The default VortexArena game port (DP <c>port</c> 26000) — the Connect default.</summary>
    public const int LanDiscoveryPort = 26000;

    /// <summary>
    /// How many ports above <see cref="LanDiscoveryPort"/> the LAN sweep probes. The game socket is ENet (it
    /// drops OOB datagrams), so a host answers <c>getinfo</c> on a side socket at <c>gamePort+1..+8</c>
    /// (<c>ServerNet.EnableLanDiscovery</c>); sweeping the small range finds every local server.
    /// </summary>
    private const int LanSweepRange = 9;

    /// <summary>
    /// The game names asked for in <c>getservers</c>. Both, against every configured master: <c>Vortex</c>
    /// because that is what this game's servers report (<see cref="GameIdentity.Name"/>), and <c>Xonotic</c>
    /// because the browser deliberately keeps listing upstream's servers — they are shown, and then refused
    /// at Join with an explanation, until backwards-compatible support exists. A master that knows nothing
    /// about one of the two simply doesn't answer that query.
    /// </summary>
    private static readonly string[] GameNames = { GameIdentity.Name, GameIdentity.LegacyName };

    private const int Protocol = GameIdentity.DpProtocol;

    /// <summary>The challenge token echoed in getinfo probes (matches replies to our request / ping).</summary>
    private const string InfoChallenge = "rebirth";

    /// <summary>
    /// Stock Xonotic master servers (the <c>sv_master*</c> defaults in xonotic-common.cfg). Mutable so a
    /// caller can point the browser at a private master; <see cref="RefreshInternet"/> resolves each
    /// <c>host:port</c> and queries it.
    /// </summary>
    public List<string> Masters { get; } = new()
    {
        "dpm4.xonotic.xyz:27777",
        "dpm6.xonotic.xyz:27777",
        "master3.xonotic.org:27950",
    };

    private readonly List<ServerEntry> _servers = new();
    private readonly List<string> _favoriteAddresses = new();

    /// <summary>getinfo send time per "ip:port" target, for the ping column (reply time − send time).</summary>
    private readonly Dictionary<string, long> _probeSent = new();

    /// <summary>
    /// The clock the ping column is measured against. A Stopwatch rather than <c>Environment.TickCount64</c>,
    /// whose ~15.6 ms granularity on Windows is coarser than the difference between a good server and a great
    /// one — it quantised most of the list onto the same handful of numbers.
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static long NowMs => Clock.ElapsedMilliseconds;

    /// <summary>The shared UDP socket for master queries + per-server info probes. Lazily created on refresh.</summary>
    private MasterServerLink? _link;

    /// <summary>The current server rows (read-only view for the UI).</summary>
    public IReadOnlyList<ServerEntry> Servers => _servers;

    /// <summary>
    /// Read-only lookup of the row for <paramref name="address"/> (normalized) — what the Server Info dialog
    /// reads when it pops up for the selected row (the C# stand-in for the QC host-cache index the
    /// serverinfo dialog reads via <c>gethostcachestring</c>). Returns null when no row matches (e.g. the
    /// address was typed manually and never queried). Append-only; never mutates the list.
    /// </summary>
    public ServerEntry? FindByAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
            return null;
        string norm = NormalizeAddress(address);
        return _servers.Find(s => s.Address == norm) ?? _servers.Find(s => s.Address == address);
    }

    /// <summary>
    /// Bumped on every change to the list — a row added, or an existing row's fields filled in by an async
    /// reply. The UI compares this between frames to know when to re-render (rows mutate in place, so a plain
    /// count check would miss detail fill-in). Starts at 0; <see cref="Refresh"/> is one change among many.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Raised when the user asks to connect. The argument is the raw "ip" or "ip:port" target; the host's
    /// net layer wires this up (the QC equivalent was issuing a <c>connect &lt;ip&gt;</c> command).
    /// </summary>
    public event Action<string>? ConnectRequested;

    // No constructor work on purpose: the bookmark list lives in a cvar now, and this type is created from a
    // static field initialiser that can run before MenuState.Boot has applied the user's config. Every read
    // path calls LoadFavorites() itself, so the first touch is always after the store is populated.

    // -------------------------------------------------------------------------------------------------
    //  Refresh — rebuild the list from favorites + a LAN discovery sweep.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuild the server list. Starts from saved favorites, folds in any servers that answer a LAN
    /// discovery ping (within a short, bounded poll window), then kicks off the asynchronous internet
    /// query against the master servers. Non-blocking: the LAN results are immediate, while internet rows
    /// (and their pings) trickle in as the menu pumps <see cref="Poll"/> over subsequent frames.
    /// Never throws — networking failures are swallowed so the menu stays alive when offline.
    /// </summary>
    public void Refresh()
    {
        _servers.Clear();
        LoadFavorites();

        // 1) Favorites first — always shown so a saved server is one click away even when offline.
        foreach (string addr in _favoriteAddresses)
        {
            AddAndReturn(new ServerEntry
            {
                Name = addr,
                Address = addr,
                Gametype = "?",
                Map = "?",
                Favorite = true,
                Category = ServerCategory.Favorited,
            });
        }
        Revision++; // the Clear + favorites rebuild is itself a change the UI must pick up

        // 2) LAN sweep — append anything that replies on the local network right now.
        foreach (var lan in QueryLan())
            UpsertEntry(lan.Address, lan, isLan: true);

        // 3) Internet — fire getservers at each master; replies complete asynchronously via Poll().
        RefreshInternet();
    }

    // -------------------------------------------------------------------------------------------------
    //  Internet query — master servers (async; completes via Poll).
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ask each configured master for the Xonotic server list. Resolves every <c>host:port</c> in
    /// <see cref="Masters"/> (skipping any that fail to resolve) and sends a <c>getservers</c> query.
    /// Results arrive asynchronously: <see cref="MasterServerLink.ServerListReceived"/> adds placeholder
    /// rows and probes each server, and <see cref="MasterServerLink.InfoReceived"/> fills the details —
    /// both driven by <see cref="Poll"/>. Never throws.
    /// </summary>
    public void RefreshInternet()
    {
        MasterServerLink? link = EnsureLink();
        if (link is null)
            return; // socket unavailable (e.g. no network) — already logged

        foreach (string master in Masters)
        {
            if (!TryResolveEndpoint(master, out IPEndPoint? ep))
                continue;
            foreach (string game in GameNames)
            {
                try
                {
                    link.RequestServers(ep!, game, Protocol);
                }
                catch (Exception e)
                {
                    GD.Print($"[Menu] master query to {master} for '{game}' failed: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Pump the UDP link so async master/server replies land in the list. The menu calls this each frame;
    /// rows appear and fill in over the following frames. No-op (and never throws) when no query is active.
    /// </summary>
    public void Poll()
    {
        try
        {
            _link?.Poll();
        }
        catch (Exception e)
        {
            GD.Print($"[Menu] server-browser poll error: {e.Message}");
        }
    }

    /// <summary>
    /// Lazily create the shared <see cref="MasterServerLink"/> and wire its events to populate the list.
    /// Returns null if the socket can't be opened (the internet path is then simply skipped). The handlers
    /// are attached exactly once, on first creation, so repeated <see cref="Refresh"/> calls don't stack
    /// duplicate subscriptions.
    /// </summary>
    private MasterServerLink? EnsureLink()
    {
        if (_link is not null)
            return _link;
        try
        {
            // The master hands back on the order of a thousand servers and we probe them all at once, so the
            // replies arrive in a burst. Draining only the stock 64 per frame would make the ping column read
            // "how deep in the queue this reply was" instead of "how far away this server is" — every row
            // landing on the same handful of values. See MaxPacketsPerPoll.
            var link = new MasterServerLink { MaxPacketsPerPoll = 4096 };

            // A master answered: add a placeholder row per server and probe each for its details/ping.
            link.ServerListReceived += servers =>
            {
                foreach ((IPAddress ip, int port) in servers)
                {
                    string address = $"{ip}:{port}";
                    if (!_servers.Exists(s => s.Address == address))
                        AddAndReturn(new ServerEntry { Name = address, Address = address });
                    try
                    {
                        _probeSent[address] = NowMs;
                        link.RequestInfo(new IPEndPoint(ip, port), InfoChallenge);
                    }
                    catch (Exception e) { GD.Print($"[Menu] info probe to {address} failed: {e.Message}"); }
                }
            };

            // A server answered our probe: find/create its row and populate it from the infostring.
            link.InfoReceived += (from, info) =>
            {
                string address = $"{from.Address}:{from.Port}";
                ServerEntry entry = _servers.Find(s => s.Address == address)
                                    ?? AddAndReturn(new ServerEntry { Address = address });
                if (_probeSent.TryGetValue(address, out long sent))
                    entry.Ping = (int)Math.Min(int.MaxValue, NowMs - sent);
                PopulateFromInfo(entry, info);
            };

            _link = link;
            return _link;
        }
        catch (Exception e)
        {
            GD.Print($"[Menu] internet server query unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>Add an entry to the list (bumping <see cref="Revision"/>) and hand it back so callers can keep populating it.</summary>
    private ServerEntry AddAndReturn(ServerEntry entry)
    {
        _servers.Add(entry);
        Revision++;
        return entry;
    }

    /// <summary>
    /// Insert <paramref name="entry"/> by address, or refresh an existing row's fields in place (favorites
    /// keep their star). Used by the immediate LAN sweep so a LAN server already listed as a favorite is
    /// updated rather than duplicated.
    /// </summary>
    private void UpsertEntry(string address, ServerEntry entry, bool isLan)
    {
        ServerEntry? existing = _servers.Find(s => s.Address == address);
        if (existing is null)
        {
            AddAndReturn(entry);
            return;
        }
        existing.Name = entry.Name;
        existing.Map = entry.Map;
        existing.Gametype = entry.Gametype;
        existing.Players = entry.Players;
        existing.Bots = entry.Bots;
        existing.MaxPlayers = entry.MaxPlayers;
        existing.Ping = entry.Ping;
        existing.QcStatus = entry.QcStatus;
        existing.Version = entry.Version;
        existing.ModName = entry.ModName;
        existing.Pure = entry.Pure;
        existing.PureAvailable = entry.PureAvailable;
        existing.QcFreeSlots = entry.QcFreeSlots;
        existing.ServerFlags = entry.ServerFlags;
        existing.TermsOfServiceUrl = entry.TermsOfServiceUrl;
        existing.PlayerNames = entry.PlayerNames;
        existing.Queried = entry.Queried;
        existing.IsVortexArena = entry.IsVortexArena;
        existing.BuildParity = entry.BuildParity;
        if (isLan) existing.IsLan = true;
        existing.Category = CategoryForEntry(existing);
        Revision++;
    }

    /// <summary>
    /// Copy the DP <c>infoResponse</c> key/values (the dict from
    /// <see cref="MasterServerProtocol.ParseInfoResponse"/>) onto a row: <c>hostname</c>, <c>mapname</c>,
    /// <c>gametype</c>, <c>clients</c>, <c>sv_maxclients</c>. Missing keys leave the field at its default.
    /// Bumps <see cref="Revision"/> since the row's visible fields changed.
    /// </summary>
    private void PopulateFromInfo(ServerEntry entry, IReadOnlyDictionary<string, string> info)
    {
        if (info.TryGetValue("hostname", out string? host) && host.Length > 0)
            entry.Name = host;
        else if (string.IsNullOrEmpty(entry.Name))
            entry.Name = entry.Address;

        // This IS a real reply, so the row can now be judged joinable or not (see IsIncompatibleXonotic).
        entry.Queried = true;
        entry.IsVortexArena = info.TryGetValue(Net.ServerNet.VortexServerKey, out string? parity);
        entry.BuildParity = entry.IsVortexArena && uint.TryParse(parity, out uint reported) ? reported : 0u;

        if (info.TryGetValue("mapname", out string? map)) entry.Map = map;
        if (info.TryGetValue("qcstatus", out string? qc) && qc.Length > 0)
            ParseQcStatus(entry, qc);
        if (info.TryGetValue("players", out string? pl))
            entry.PlayerNames = pl;
        if (info.TryGetValue("gametype", out string? gt) && gt.Length > 0)
            entry.Gametype = gt;
        if (info.TryGetValue("clients", out string? c) && int.TryParse(c, out int players))
            entry.Players = players;
        if (info.TryGetValue("bots", out string? b) && int.TryParse(b, out int bots))
            entry.Bots = bots;
        if (info.TryGetValue("sv_maxclients", out string? m) && int.TryParse(m, out int max))
            entry.MaxPlayers = max;

        // A VortexArena server answers getinfo on a SIDE socket and reports its real game port in the
        // infostring ("port") — re-key the row to the connectable address (and fold any duplicate row).
        if (info.TryGetValue("port", out string? p) && int.TryParse(p, out int gamePort) && gamePort > 0)
        {
            int colonAt = entry.Address.LastIndexOf(':');
            string ip = colonAt > 0 ? entry.Address[..colonAt] : entry.Address;
            string rekeyed = $"{ip}:{gamePort}";
            if (rekeyed != entry.Address)
            {
                ServerEntry? existing = _servers.Find(s => s.Address == rekeyed && !ReferenceEquals(s, entry));
                if (existing is not null)
                {
                    entry.Favorite |= existing.Favorite;
                    _servers.Remove(existing);
                }
                entry.Address = rekeyed;
            }
            entry.Favorite |= _favoriteAddresses.Contains(rekeyed);
        }
        entry.Category = CategoryForEntry(entry);
        Revision++;
    }

    /// <summary>
    /// Split a server's <c>qcstatus</c> onto <paramref name="entry"/> — a direct port of the token walk
    /// <c>CategoryForEntry</c> and <c>drawListBoxItem</c> both do (serverlist.qc:117 / :853). Token 0 is the
    /// gametype, token 1 the version; from token 2 on, each is a one-letter key with the value after it, and an
    /// empty token ends the header section (what follows is the score-label block). Unknown keys are skipped,
    /// so a newer server adding one doesn't break the parse.
    /// </summary>
    private static void ParseQcStatus(ServerEntry entry, string qcstatus)
    {
        QcStatus q = ServerListInfo.ParseQcStatus(qcstatus);
        entry.QcStatus = qcstatus;
        if (q.Gametype.Length > 0)
            entry.Gametype = q.Gametype;
        entry.Version = q.Version;
        entry.Pure = q.Pure;
        entry.PureAvailable = q.PureAvailable;
        entry.QcFreeSlots = q.FreeSlots;
        entry.ServerFlags = q.ServerFlags;
        entry.ModName = q.ModName;
        entry.TermsOfServiceUrl = q.TermsOfServiceUrl;
    }

    // -------------------------------------------------------------------------------------------------
    //  Categories (serverlist.qc CategoryForEntry / CategoryOverride)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Servers the master flagged as promoted, and servers the community list recommends. In Base these are
    /// fetched by the external response system (<c>_Nex_ExtResponseSystem_*</c>, an HTTP list pulled at
    /// startup); this port has no such fetch yet, so both stay empty and the <c>menu_slist_recommendations</c>
    /// bit-1 vote is a consistent "no" — which is exactly how Base behaves before its fetch completes. Public
    /// so a future fetcher can fill them without touching the categorisation logic.
    /// </summary>
    public List<string> PromotedServers { get; } = new();
    public List<string> RecommendedServers { get; } = new();

    /// <summary>
    /// Which category a row belongs to, before any override — the port of <c>CategoryForEntry</c>
    /// (serverlist.qc:117). Bookmarks win outright, then the recommendation vote, then the reported mod.
    /// </summary>
    public ServerCategory CategoryForEntry(ServerEntry e)
    {
        ICvarService cv = MenuState.Cvars;
        var input = new ServerListInfo.CategoryInput(
            IsFavorite: e.Favorite,
            IsPromoted: PromotedServers.Contains(e.Address),
            IsRecommended: RecommendedServers.Contains(e.Address),
            ModName: e.ModName,
            Pure: e.Pure,
            PureAvailable: e.PureAvailable,
            QcFreeSlots: e.QcFreeSlots,
            Humans: e.Humans,
            Ping: e.Ping);
        var rules = new ServerListInfo.RecommendationRules(
            Mode: (int)cv.GetFloat("menu_slist_recommendations"),
            MaxPing: cv.GetFloat("menu_slist_recommendations_maxping"),
            MinFreeSlots: cv.GetFloat("menu_slist_recommendations_minfreeslots"),
            MinHumans: cv.GetFloat("menu_slist_recommendations_minhumans"),
            PureThreshold: cv.GetFloat("menu_slist_recommendations_purethreshold"),
            ModImpurity: cv.GetFloat("menu_slist_modimpurity"));
        return ServerListInfo.CategoryForEntry(input, rules);
    }

    /// <summary>
    /// Fold a raw category through the override table, reading the enabled column out of the live cvar store.
    /// See <see cref="ServerListInfo.ApplyOverride"/> for what the two columns mean.
    /// </summary>
    public static ServerCategory CategoryOverride(ServerCategory cat)
        => ServerListInfo.ApplyOverride(cat,
            MenuState.Cvars.GetFloat("menu_slist_categories") != 0f,
            key => MenuState.Cvars.GetString($"menu_slist_categories_{key}_override"));

    /// <summary>The heading a category draws (the CTX'd SLCAT^ strings of serverlist.qh:150).</summary>
    public static string CategoryTitle(ServerCategory cat) => Localization.Tr(ServerListInfo.CategoryTitle(cat));

    /// <summary>
    /// Resolve a <c>host:port</c> string to an <see cref="IPEndPoint"/> via DNS. Returns false (and logs)
    /// for a malformed address or a resolution failure, so the caller can simply skip it.
    /// </summary>
    private static bool TryResolveEndpoint(string hostPort, out IPEndPoint? ep)
    {
        ep = null;
        int colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], out int port))
            return false;
        try
        {
            IPAddress[] addrs = Dns.GetHostAddresses(hostPort[..colon]);
            if (addrs.Length == 0)
                return false;
            ep = new IPEndPoint(addrs[0], port);
            return true;
        }
        catch (Exception e)
        {
            GD.Print($"[Menu] could not resolve master '{hostPort}': {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Best-effort LAN discovery: broadcast the real DP <c>getinfo</c> probe and collect immediate replies.
    /// Returns whatever answered within a short, non-blocking poll window. The probe is the exact wire
    /// format a VortexArena server's getinfo handler answers — a 4×<c>0xFF</c> marker + <c>"getinfo rebirth"</c>
    /// (via <see cref="MasterServerProtocol.EncodeGetInfo"/>) — and replies are decoded with
    /// <see cref="MasterServerProtocol.ParseInfoResponse"/>, so a server only has to answer the standard
    /// getinfo to show up. Any networking error is swallowed (discovery is strictly best-effort).
    /// </summary>
    private IReadOnlyList<ServerEntry> QueryLan()
    {
        var found = new List<ServerEntry>();
        var udp = new PacketPeerUdp();
        try
        {
            udp.SetBroadcastEnabled(true);
            // Bind to an ephemeral port so replies have somewhere to land.
            if (udp.Bind(0) != Error.Ok)
                return found;

            // The standard DP connectionless info probe: 4×0xFF + "getinfo rebirth" — broadcast across the
            // small discovery range (a host answers on gamePort+1..+8, since ENet owns the game port itself).
            byte[] probe = MasterServerProtocol.EncodeGetInfo(InfoChallenge);
            long sentAt = NowMs;
            for (int port = LanDiscoveryPort; port < LanDiscoveryPort + LanSweepRange; port++)
            {
                udp.SetDestAddress("255.255.255.255", port);
                udp.PutPacket(probe);
            }

            // Check a handful of times with a tiny sleep between — total well under one frame's worth of
            // stall, and only when the user explicitly hit Refresh. (UDP packets are available immediately;
            // PacketPeerUdp surfaces them through GetAvailablePacketCount without an explicit poll step.)
            for (int attempt = 0; attempt < 5; attempt++)
            {
                while (udp.GetAvailablePacketCount() > 0)
                {
                    byte[] packet = udp.GetPacket();
                    string fromIp = udp.GetPacketIP();
                    int fromPort = udp.GetPacketPort();
                    if (TryParseLanInfo(packet, fromIp, fromPort, out ServerEntry entry))
                    {
                        entry.Ping = (int)Math.Min(int.MaxValue, NowMs - sentAt);
                        found.Add(entry);
                    }
                }
                if (found.Count > 0)
                    break;
                OS.DelayMsec(10);
            }
        }
        catch (Exception e)
        {
            GD.Print($"[Menu] LAN discovery unavailable: {e.Message}");
        }
        finally
        {
            udp.Close();
        }
        return found;
    }

    /// <summary>
    /// Decode a LAN server's reply using the shared DP codec: confirm the 4×<c>0xFF</c> OOB marker and an
    /// <c>infoResponse</c>, then map the infostring onto a row. Returns false for any datagram that isn't a
    /// well-formed infoResponse (e.g. an in-band packet or unrelated traffic).
    /// </summary>
    private bool TryParseLanInfo(byte[] packet, string ip, int port, out ServerEntry entry)
    {
        entry = new ServerEntry { Address = $"{ip}:{port}", IsLan = true };

        // Gate on the connectionless marker so non-OOB traffic on the port is ignored.
        if (!MasterServerProtocol.TryStripOob(packet, out _))
            return false;

        IReadOnlyDictionary<string, string> info = MasterServerProtocol.ParseInfoResponse(packet);
        if (info.Count == 0)
            return false; // not an infoResponse (or empty) — nothing to show

        PopulateFromInfo(entry, info);
        return true;
    }

    // -------------------------------------------------------------------------------------------------
    //  Connect — resolve an address/row and fire the callback the net layer listens on.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Normalise <paramref name="rawAddress"/> (default the port when omitted) and raise
    /// <see cref="ConnectRequested"/>. Returns the resolved target, or null if the address is blank.
    /// </summary>
    public string? Connect(string rawAddress)
    {
        string target = NormalizeAddress(rawAddress);
        if (string.IsNullOrEmpty(target))
            return null;

        if (ConnectRequested is null)
            GD.Print($"[Menu] Connect requested -> {target} (no net handler attached yet).");
        else
            ConnectRequested.Invoke(target);
        return target;
    }

    /// <summary>Trim an address and append the default port if the user omitted one.</summary>
    public static string NormalizeAddress(string raw)
    {
        string addr = raw?.Trim() ?? "";
        if (addr.Length == 0)
            return "";
        // IPv6 in brackets, or already has a :port — leave as-is. Bare host/IPv4 gets the default port.
        if (addr.StartsWith('[') || addr.Contains(':'))
            return addr;
        return $"{addr}:{LanDiscoveryPort}";
    }

    // -------------------------------------------------------------------------------------------------
    //  Favorites — add/remove + persistence.
    // -------------------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="address"/> (normalised) is bookmarked — drives the Favorite toggle.</summary>
    public bool IsFavorite(string address)
    {
        LoadFavorites(); // the cvar is the store, and the console/`addfav` can change it behind our back
        return _favoriteAddresses.Contains(NormalizeAddress(address));
    }

    public void AddFavorite(string address)
    {
        string norm = NormalizeAddress(address);
        if (norm.Length == 0)
            return;
        LoadFavorites();
        if (_favoriteAddresses.Contains(norm))
            return;
        _favoriteAddresses.Add(norm);
        SaveFavorites();
    }

    public void RemoveFavorite(string address)
    {
        string norm = NormalizeAddress(address);
        LoadFavorites();
        if (_favoriteAddresses.Remove(norm))
            SaveFavorites();
    }

    /// <summary>
    /// QC <c>XonoticServerList_toggleFavorite</c>: flip a server's bookmark and re-sort, because bookmarking
    /// moves the row into (or out of) the Favorites category.
    /// </summary>
    public void ToggleFavorite(string address)
    {
        if (IsFavorite(address)) RemoveFavorite(address);
        else AddFavorite(address);

        // Recategorise in place rather than re-querying: the star changed, not the server.
        string norm = NormalizeAddress(address);
        foreach (ServerEntry s in _servers)
        {
            if (s.Address != norm)
                continue;
            s.Favorite = _favoriteAddresses.Contains(norm);
            s.Category = CategoryForEntry(s);
        }
        Revision++;
    }

    /// <summary>
    /// Read the bookmark list out of the <c>net_slist_favorites</c> cvar — the same store Base uses, so the
    /// <c>addfav</c>/<c>delfav</c> console aliases (data/core.pk3dir/commands.cfg:78) and the menu agree.
    /// The list is space-separated, exactly as <c>tokenize_console</c> reads it there.
    /// </summary>
    private void LoadFavorites()
    {
        MigrateLegacyFavorites();
        _favoriteAddresses.Clear();
        foreach (string token in MenuState.Cvars.GetString("net_slist_favorites")
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            _favoriteAddresses.Add(NormalizeAddress(token));
    }

    private void SaveFavorites()
        => MenuState.Cvars.Set("net_slist_favorites", string.Join(' ', _favoriteAddresses));

    /// <summary>
    /// One-time fold of the port's old <c>favorites.cfg</c> into the cvar, so nobody loses the servers they had
    /// bookmarked before the store moved. Deletes the file once its contents are in the cvar, which is what
    /// makes this run at most once.
    /// </summary>
    private void MigrateLegacyFavorites()
    {
        if (_migratedLegacy)
            return;
        _migratedLegacy = true;

        var cfg = new ConfigFile();
        if (cfg.Load(FavoritesPath) != Error.Ok)
            return;
        var legacy = (string[])cfg.GetValue("favorites", "addresses", Array.Empty<string>());

        var merged = new List<string>(MenuState.Cvars.GetString("net_slist_favorites")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (string addr in legacy)
        {
            string norm = NormalizeAddress(addr);
            if (norm.Length > 0 && !merged.Contains(norm))
                merged.Add(norm);
        }
        MenuState.Cvars.Set("net_slist_favorites", string.Join(' ', merged));
        DirAccess.RemoveAbsolute(FavoritesPath);
        GD.Print($"[Menu] migrated {legacy.Length} bookmark(s) from favorites.cfg into net_slist_favorites.");
    }

    private bool _migratedLegacy;

    // -------------------------------------------------------------------------------------------------
    //  Teardown — release the UDP socket.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Release the master-server UDP socket. Idempotent; safe to call when no query ever ran.</summary>
    public void Dispose()
    {
        _link?.Dispose();
        _link = null;
    }
}
