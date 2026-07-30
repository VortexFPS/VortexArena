using System;
using System.Collections.Generic;
using System.Net;
using Godot;
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

    /// <summary>Humans/slots, the QC players column (SLIST_FIELD_NUMHUMANS / maxclients).</summary>
    public string PlayersText => MaxPlayers > 0 ? $"{System.Math.Max(0, Players - Bots)}/{MaxPlayers}"
                                                : System.Math.Max(0, Players - Bots).ToString();
    public string PingText => Ping >= 0 ? Ping.ToString() : "--";
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
    /// The dpmaster game filter and DP network protocol version sent in <c>getservers</c> — Xonotic's
    /// <c>gamename</c> and DP protocol 3 (matches <see cref="MasterServerProtocol"/>'s tests and the
    /// server-side infostring in ServerNet).
    /// </summary>
    private const string GameName = "Xonotic";
    private const int Protocol = 3;

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

    private static long NowMs => System.Environment.TickCount64;

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

    public ServerBrowser()
    {
        LoadFavorites();
    }

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
            try
            {
                link.RequestServers(ep!, GameName, Protocol);
            }
            catch (Exception e)
            {
                GD.Print($"[Menu] master query to {master} failed: {e.Message}");
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
            var link = new MasterServerLink();

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
        if (isLan) existing.IsLan = true;
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

        if (info.TryGetValue("mapname", out string? map)) entry.Map = map;
        if (info.TryGetValue("gametype", out string? gt) && gt.Length > 0)
        {
            entry.Gametype = gt;
        }
        else if (info.TryGetValue("qcstatus", out string? qc) && qc.Length > 0)
        {
            // Real Xonotic servers carry the gametype as the first ":"-token of qcstatus ("ctf:git:P0:S16:...").
            int colon = qc.IndexOf(':');
            entry.Gametype = colon > 0 ? qc[..colon] : qc;
        }
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
        Revision++;
    }

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

    /// <summary>True when <paramref name="address"/> (normalised) is bookmarked — drives the Bookmark/Unbookmark toggle.</summary>
    public bool IsFavorite(string address) => _favoriteAddresses.Contains(NormalizeAddress(address));

    public void AddFavorite(string address)
    {
        string norm = NormalizeAddress(address);
        if (norm.Length == 0 || _favoriteAddresses.Contains(norm))
            return;
        _favoriteAddresses.Add(norm);
        SaveFavorites();
    }

    public void RemoveFavorite(string address)
    {
        string norm = NormalizeAddress(address);
        if (_favoriteAddresses.Remove(norm))
            SaveFavorites();
    }

    private void LoadFavorites()
    {
        _favoriteAddresses.Clear();
        var cfg = new ConfigFile();
        if (cfg.Load(FavoritesPath) != Error.Ok)
            return;
        var arr = (string[])cfg.GetValue("favorites", "addresses", Array.Empty<string>());
        _favoriteAddresses.AddRange(arr);
    }

    private void SaveFavorites()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("favorites", "addresses", _favoriteAddresses.ToArray());
        cfg.Save(FavoritesPath);
    }

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
