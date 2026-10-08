// Port of Base/darkplaces/netconn.c, the server list ("host cache"): serverlist_cache / serverlist_viewlist,
// serverlist_andmasks / serverlist_ormasks, _ServerList_Entry_Compare, _ServerList_CompareInt,
// _ServerList_CompareStr, _ServerList_Entry_Mask, ServerList_ViewList_Insert, ServerList_ViewList_Remove,
// ServerList_RebuildViewList, ServerList_ResetMasks, ServerList_GetPlayerStatistics,
// ServerList_BuildDPServerQuery, ServerList_QueryList, NetConn_QueryMasters, NetConn_QueryQueueFrame,
// NetConn_UpdateFavorites_c, NetConn_ClientParsePacket_ServerList_ProcessReply / _UpdateCache /
// _PrepareQuery / _ParseDPList and the statusResponse / infoResponse cases of NetConn_ClientParsePacket;
// with netconn.h serverlist_info_t, serverlist_infofield_t, serverlist_maskop_t.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VortexArena.Common.Services;

namespace VortexArena.Legacy.Menu;

/// <summary>serverlist_infofield_t: what a server-list entry can be read, filtered and sorted by.</summary>
public enum HostCacheField
{
    CName, Ping, Game, Mod, Map, Name, MaxPlayers, NumPlayers, Protocol, NumBots, NumHumans, FreeSlots, QcStatus, Players,
    Category, IsFavorite, Count,
}

/// <summary>serverlist_maskop_t. Contains is the default for strings; for numbers the four string
/// operators mean "greater or equal".</summary>
public enum HostCacheOp
{
    Contains, NotContain, LessEqual, Less, Equal, Greater, GreaterEqual, NotEqual, StartsWith, NotStartsWith,
}

/// <summary>serverlist_info_t: everything known about one server. Every string is cut to the size of
/// the C's buffer when it is stored, so nothing a server sends can make an entry larger than this.</summary>
public sealed class HostCacheInfo
{
    public const int CNameSize = 128, GameSize = 32, ModSize = 32, MapSize = 32, NameSize = 128, QcStatusSize = 128, PlayersSize = 2800;

    /// <summary>Address for connecting, "a.b.c.d:port". Built by this client from the socket address, never taken from a packet's text.</summary>
    public string CName = "";
    /// <summary>Ping in milliseconds; 0 means no data (the entry is then not shown).</summary>
    public int Ping;
    public string Game = "", Mod = "", Map = "", Name = "", QcStatus = "", Players = "";
    public int MaxPlayers, NumPlayers, NumBots, NumHumans, FreeSlots, Protocol, GameVersion, Category;
    public bool IsFavorite;
}

/// <summary>serverlist_entry_t.</summary>
public sealed class HostCacheEntry
{
    /// <summary>Whether the server answered during the current refresh; one that did not is timed out of the view.</summary>
    public bool Responded;
    /// <summary>When it was last asked, for the retry interval and the timeout.</summary>
    public double QueryTime;
    /// <summary>Its position in the cache: what "sort by index" compares (the C compares pointers).</summary>
    public int Index;
    public HostCacheInfo Info = new();
    /// <summary>The two description lines DarkPlaces' own menu draws (fields 1024 and 1025 of getserverliststring).</summary>
    public string Line1 = "", Line2 = "";
}

/// <summary>serverlist_mask_t: one filter. An entry passes if every test with a value passes.</summary>
public sealed class HostCacheMask
{
    public bool Active;
    public readonly HostCacheOp[] Tests = new HostCacheOp[(int)HostCacheField.Count];
    public HostCacheInfo Info = new() { NumBots = -1 };

    public void Reset()
    {
        Active = false;
        Array.Clear(Tests);
        // "numbots needs to be compared to -1 to always succeed"
        Info = new HostCacheInfo { NumBots = -1 };
    }
}

/// <summary>
/// The network half of the server list, which the owner of the socket implements. The cache decides
/// what to ask and when; this only sends. Replies come back through
/// <see cref="MenuHostCache.MasterReply"/> and <see cref="MenuHostCache.ServerReply"/>.
/// </summary>
public interface IMenuServerQueries
{
    /// <summary>
    /// "getservers &lt;game&gt; &lt;protocol&gt; empty full" to one master server, named as the sv_master
    /// cvars name it ("host" or "host:port", default port 27950). False if it could not be sent (the
    /// name does not resolve): the master is then not waited for.
    /// </summary>
    bool QueryMaster(string master, string gameName, int protocol);

    /// <summary>"getstatus &lt;challenge&gt;" to one game server, addressed as "a.b.c.d:port".</summary>
    void QueryServer(string address, string challenge);
}

/// <summary>
/// The server browser's data: the servers known (the cache), which of them pass the menu's filters and
/// in what order (the view), and the queue that asks masters for addresses and servers for their status.
///
/// Everything in it that came off the network is untrusted - master replies, server names, the
/// player list - and all of it is bounded here: at most <see cref="MaxServers"/> entries, every string
/// cut to DarkPlaces' buffer size and stripped of control characters, a status reply accepted only if
/// it echoes a challenge this client made. Nothing in an entry is ever executed; the address the menu
/// connects to is the one this client built from the datagram's source.
/// </summary>
public sealed class MenuHostCache
{
    /// <summary>SERVERLIST_TOTALSIZE.</summary>
    public const int MaxServers = 2048;
    /// <summary>SERVERLIST_ANDMASKCOUNT, SERVERLIST_ORMASKCOUNT.</summary>
    public const int AndMaskCount = 16, OrMaskCount = 16;
    /// <summary>MAX_FAVORITESERVERS.</summary>
    public const int MaxFavorites = 256;
    /// <summary>The seven sv_master cvars, in DarkPlaces' order (sv_masters[]).</summary>
    public static readonly string[] MasterCvars = { "sv_master1", "sv_master2", "sv_master3", "sv_master4", "sv_masterextra1", "sv_masterextra2", "sv_masterextra3" };

    // serverlist_querystage bits; the QuakeWorld stage is not ported (Xonotic never asks for it).
    private const int StageMasters = 1, StageServers = 4;
    private const int MasterQuerySent = 1, MasterResponse = 2, MasterComplete = 3;
    // SLSF_*
    public const int SortDescending = 1, SortFavorites = 2, SortCategories = 4;

    private readonly ICvarService _cvars;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<HostCacheEntry> _cache = new();
    private readonly Dictionary<string, HostCacheEntry> _byAddress = new(StringComparer.Ordinal);
    private readonly List<HostCacheEntry> _view = new();
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
    private readonly int[] _masterStatus = new int[MasterCvars.Length];
    private readonly string[] _masterNames = new string[MasterCvars.Length];
    private byte[] _queryKey = RandomNumberGenerator.GetBytes(16);
    private int _queryStage;
    private double _masterQueryTime, _queryCounter;
    private int _pass, _server;
    private string _favoritesText = "\0";

    /// <param name="cvars">The console's cvars: the masters, the favourites, the game version and the net_slist_* settings are read from it.</param>
    public MenuHostCache(ICvarService cvars)
    {
        _cvars = cvars ?? throw new ArgumentNullException(nameof(cvars));
        AndMasks = new HostCacheMask[AndMaskCount];
        OrMasks = new HostCacheMask[OrMaskCount];
        for (int i = 0; i < AndMaskCount; i++) AndMasks[i] = new HostCacheMask();
        for (int i = 0; i < OrMaskCount; i++) OrMasks[i] = new HostCacheMask();
        Clock = () => _stopwatch.Elapsed.TotalSeconds;
    }

    /// <summary>host.realtime: the clock pings are measured on. The owner passes its value to
    /// <see cref="Frame"/> and <see cref="ServerReply"/>; a refresh the menu program asks for reads it here.</summary>
    public Func<double> Clock { get; set; }

    /// <summary>The socket's owner. Null: nothing is sent, and a refresh finds no servers.</summary>
    public IMenuServerQueries? Queries { get; set; }

    /// <summary>MR_GetServerListEntryCategory: the menu program's category for an entry (m_gethostcachecategory). Null: 0.</summary>
    public Func<HostCacheEntry, int>? Category { get; set; }

    /// <summary>gamenetworkfiltername: the game name a master is asked for.</summary>
    public string GameName { get; set; } = "Xonotic";
    /// <summary>NET_PROTOCOL_VERSION.</summary>
    public int NetProtocolVersion { get; set; } = 3;

    public HostCacheMask[] AndMasks { get; }
    public HostCacheMask[] OrMasks { get; }
    /// <summary>serverlist_sortbyfield, serverlist_sortflags.</summary>
    public HostCacheField SortField { get; set; }
    public int SortFlags { get; set; }

    /// <summary>serverlist_cachecount, serverlist_viewcount.</summary>
    public int CacheCount => _cache.Count;
    public int ViewCount => _view.Count;
    public int MasterQueryCount { get; private set; }
    public int MasterReplyCount { get; private set; }
    public int ServerQueryCount { get; private set; }
    public int ServerReplyCount { get; private set; }
    /// <summary>True while masters or servers are still being asked.</summary>
    public bool Querying => _queryStage != 0;
    /// <summary>Replies dropped because they answered no query of this client's (a wrong or missing challenge).</summary>
    public long RepliesRefused { get; private set; }
    /// <summary>
    /// Set while the menu program's category function is running on an entry (serverlist_callbackentry):
    /// "it must not rebuild or free the list under itself", so a rebuild or a refresh asked for meanwhile
    /// is ignored.
    /// </summary>
    public HostCacheEntry? CallbackEntry { get; private set; }

    public IReadOnlyList<HostCacheEntry> Cache => _cache;
    public IReadOnlyList<HostCacheEntry> View => _view;

    // ---- cvars the list reads ----------------------------------------------------------------------

    private float Cvar(string name, float fallback) => _cvars.Has(name) ? _cvars.GetFloat(name) : fallback;
    private bool Paused => Cvar("net_slist_pause", 0) != 0;
    private float MaxPing => Cvar("net_slist_maxping", 420);

    // ---- ordering and filtering --------------------------------------------------------------------

    /// <summary>_ServerList_Entry_Compare: true if <paramref name="a"/> belongs before <paramref name="b"/>.</summary>
    public bool Before(HostCacheEntry a, HostCacheEntry b)
    {
        int result;
        if ((SortFlags & SortCategories) != 0)
        {
            result = a.Info.Category - b.Info.Category;
            if (result != 0) return result < 0;
        }
        if ((SortFlags & SortFavorites) != 0 && a.Info.IsFavorite != b.Info.IsFavorite) return a.Info.IsFavorite;

        // "> 0 if for numbers A > B and for text if A < B"
        result = SortField switch
        {
            HostCacheField.Ping => a.Info.Ping - b.Info.Ping,
            HostCacheField.MaxPlayers => a.Info.MaxPlayers - b.Info.MaxPlayers,
            HostCacheField.NumPlayers => a.Info.NumPlayers - b.Info.NumPlayers,
            HostCacheField.NumBots => a.Info.NumBots - b.Info.NumBots,
            HostCacheField.NumHumans => a.Info.NumHumans - b.Info.NumHumans,
            HostCacheField.FreeSlots => a.Info.FreeSlots - b.Info.FreeSlots,
            HostCacheField.Protocol => a.Info.Protocol - b.Info.Protocol,
            HostCacheField.CName => string.CompareOrdinal(b.Info.CName, a.Info.CName),
            HostCacheField.Game => CompareNoCase(b.Info.Game, a.Info.Game),
            HostCacheField.Map => CompareNoCase(b.Info.Map, a.Info.Map),
            HostCacheField.Mod => CompareNoCase(b.Info.Mod, a.Info.Mod),
            HostCacheField.Name => CompareNoCase(b.Info.Name, a.Info.Name),
            HostCacheField.QcStatus => CompareNoCase(b.Info.QcStatus, a.Info.QcStatus),
            HostCacheField.Category => a.Info.Category - b.Info.Category,
            HostCacheField.IsFavorite => (b.Info.IsFavorite ? 1 : 0) - (a.Info.IsFavorite ? 1 : 0),
            _ => 0,
        };
        if (result != 0) return (SortFlags & SortDescending) != 0 ? result > 0 : result < 0;

        // "if the chosen sort key is identical, sort by index (makes this a stable sort, so that later
        // replies from servers won't shuffle the servers around when they have the same ping)"
        return a.Index < b.Index;
    }

    // strcasecmp: bytes compared after ASCII lower-casing.
    private static int CompareNoCase(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int ca = a[i] is >= 'A' and <= 'Z' ? a[i] + 32 : a[i], cb = b[i] is >= 'A' and <= 'Z' ? b[i] + 32 : b[i];
            if (ca != cb) return ca - cb;
        }
        return a.Length - b.Length;
    }

    /// <summary>_ServerList_CompareInt.</summary>
    public static bool CompareInt(int a, HostCacheOp op, int b) => op switch
    {
        HostCacheOp.Less => a < b,
        HostCacheOp.LessEqual => a <= b,
        HostCacheOp.Equal => a == b,
        HostCacheOp.Greater => a > b,
        HostCacheOp.NotEqual => a != b,
        HostCacheOp.GreaterEqual or HostCacheOp.Contains or HostCacheOp.NotContain or HostCacheOp.StartsWith or HostCacheOp.NotStartsWith => a >= b,
        _ => false,
    };

    /// <summary>
    /// _ServerList_CompareStr: the entry's text with its colour codes removed, and the mask's text as
    /// it stands, both ASCII lower-cased.
    /// </summary>
    public static bool CompareString(string info, HostCacheOp op, string mask)
    {
        string a = Lower(Decolorize(info)), b = Lower(mask);
        return op switch
        {
            HostCacheOp.Contains => b.Length != 0 && a.Contains(b, StringComparison.Ordinal),
            HostCacheOp.NotContain => b.Length == 0 || !a.Contains(b, StringComparison.Ordinal),
            HostCacheOp.StartsWith => b.Length != 0 && a.StartsWith(b, StringComparison.Ordinal),
            HostCacheOp.NotStartsWith => b.Length == 0 || !a.StartsWith(b, StringComparison.Ordinal),
            HostCacheOp.Less => string.CompareOrdinal(a, b) < 0,
            HostCacheOp.LessEqual => string.CompareOrdinal(a, b) <= 0,
            HostCacheOp.Equal => string.CompareOrdinal(a, b) == 0,
            HostCacheOp.Greater => string.CompareOrdinal(a, b) > 0,
            HostCacheOp.NotEqual => string.CompareOrdinal(a, b) != 0,
            HostCacheOp.GreaterEqual => string.CompareOrdinal(a, b) >= 0,
            _ => false,
        };

        static string Lower(string s)
        {
            if (s.Length > 1399) s = s[..1399];   // "char bufferA[1400]"
            StringBuilder lower = new(s.Length);
            foreach (char c in s) lower.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
            return lower.ToString();
        }
    }

    /// <summary>common.c COM_StringDecolorize(in, 0, out, size, false): "^0".."^9" and "^xRGB" removed, "^^" becomes "^".</summary>
    public static string Decolorize(string text)
    {
        if (!text.Contains('^')) return text;
        StringBuilder result = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '^' || i + 1 >= text.Length)
            {
                result.Append(c);
                continue;
            }
            char next = text[i + 1];
            if (next is >= '0' and <= '9') i++;
            else if (next == 'x' && i + 4 < text.Length && Uri.IsHexDigit(text[i + 2]) && Uri.IsHexDigit(text[i + 3]) && Uri.IsHexDigit(text[i + 4])) i += 4;
            else if (next == '^')
            {
                result.Append('^');
                i++;
            }
            else result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>_ServerList_Entry_Mask.</summary>
    public static bool Passes(HostCacheMask mask, HostCacheInfo info)
    {
        HostCacheOp[] t = mask.Tests;
        HostCacheInfo m = mask.Info;
        if (!CompareInt(info.Ping, t[(int)HostCacheField.Ping], m.Ping)) return false;
        if (!CompareInt(info.MaxPlayers, t[(int)HostCacheField.MaxPlayers], m.MaxPlayers)) return false;
        if (!CompareInt(info.NumPlayers, t[(int)HostCacheField.NumPlayers], m.NumPlayers)) return false;
        if (!CompareInt(info.NumBots, t[(int)HostCacheField.NumBots], m.NumBots)) return false;
        if (!CompareInt(info.NumHumans, t[(int)HostCacheField.NumHumans], m.NumHumans)) return false;
        if (!CompareInt(info.FreeSlots, t[(int)HostCacheField.FreeSlots], m.FreeSlots)) return false;
        if (!CompareInt(info.Protocol, t[(int)HostCacheField.Protocol], m.Protocol)) return false;
        if (m.CName.Length != 0 && !CompareString(info.CName, t[(int)HostCacheField.CName], m.CName)) return false;
        if (m.Game.Length != 0 && !CompareString(info.Game, t[(int)HostCacheField.Game], m.Game)) return false;
        if (m.Mod.Length != 0 && !CompareString(info.Mod, t[(int)HostCacheField.Mod], m.Mod)) return false;
        if (m.Map.Length != 0 && !CompareString(info.Map, t[(int)HostCacheField.Map], m.Map)) return false;
        if (m.Name.Length != 0 && !CompareString(info.Name, t[(int)HostCacheField.Name], m.Name)) return false;
        if (m.QcStatus.Length != 0 && !CompareString(info.QcStatus, t[(int)HostCacheField.QcStatus], m.QcStatus)) return false;
        if (m.Players.Length != 0 && !CompareString(info.Players, t[(int)HostCacheField.Players], m.Players)) return false;
        if (!CompareInt(info.Category, t[(int)HostCacheField.Category], m.Category)) return false;
        if (!CompareInt(info.IsFavorite ? 1 : 0, t[(int)HostCacheField.IsFavorite], m.IsFavorite ? 1 : 0)) return false;
        return true;
    }

    // ServerList_ViewList_Insert.
    private void ViewInsert(HostCacheEntry entry)
    {
        HostCacheInfo info = entry.Info;
        // "reject incompatible servers"
        if (!GameVersionAccepted(info.GameVersion)) return;
        // "also display entries that are currently being refreshed ... if their previous ping was acceptable"
        if (info.Ping == 0) return;

        // "refresh the favorite status". (The C also matches a server's crypto identity against the
        // key fingerprints in net_slist_favorites; this client has no identities to match.)
        UpdateFavorites();
        info.IsFavorite = _favorites.Contains(info.CName);

        // "refresh the category"
        info.Category = CategoryOf(entry);

        int start;
        for (start = 0; start < AndMaskCount && AndMasks[start].Active; start++)
            if (!Passes(AndMasks[start], info)) return;
        for (start = 0; start < OrMaskCount && OrMasks[start].Active; start++)
            if (Passes(OrMasks[start], info)) break;
        if (start == OrMaskCount || (start > 0 && !OrMasks[start].Active)) return;

        if (_view.Count == 0)
        {
            InsertBefore(0, entry);
            return;
        }
        if (Before(entry, _view[0]))
        {
            InsertBefore(0, entry);
            return;
        }
        if (!Before(entry, _view[^1]))
        {
            InsertBefore(_view.Count, entry);
            return;
        }
        start = 0;
        int end = _view.Count - 1;
        while (end > start + 1)
        {
            int mid = (start + end) / 2;
            if (Before(entry, _view[mid])) end = mid;
            else start = mid;
        }
        InsertBefore(start + 1, entry);
    }

    // _ServerList_ViewList_Helper_InsertBefore: a full view loses its last entry.
    private void InsertBefore(int index, HostCacheEntry entry)
    {
        if (_view.Count >= MaxServers) _view.RemoveAt(_view.Count - 1);
        _view.Insert(Math.Min(index, _view.Count), entry);
    }

    private int CategoryOf(HostCacheEntry entry)
    {
        if (Category is not { } category) return 0;
        CallbackEntry = entry;
        // "the entry pointer is only valid during the call, so it is cleared on every way out"
        try { return category(entry); }
        finally { CallbackEntry = null; }
    }

    private bool GameVersionAccepted(int version)
    {
        int own = (int)Cvar("gameversion", 0);
        if (version == own) return true;
        int min = (int)Cvar("gameversion_min", -1), max = (int)Cvar("gameversion_max", -1);
        return min >= 0 && max >= 0 && min <= version && max >= version;
    }

    /// <summary>ServerList_RebuildViewList: filter and sort the whole cache again.</summary>
    public void RebuildView()
    {
        if (Paused || CallbackEntry is not null) return;
        _view.Clear();
        foreach (HostCacheEntry entry in _cache) ViewInsert(entry);
    }

    /// <summary>ServerList_ResetMasks.</summary>
    public void ResetMasks()
    {
        foreach (HostCacheMask mask in AndMasks) mask.Reset();
        foreach (HostCacheMask mask in OrMasks) mask.Reset();
    }

    /// <summary>The mask a menu program names by number: 0.. are AND masks, 512.. OR masks. Null for any other number.</summary>
    public HostCacheMask? Mask(int number)
    {
        if (number >= 0 && number < AndMaskCount) return AndMasks[number];
        if (number >= 512 && number - 512 < OrMaskCount) return OrMasks[number - 512];
        return null;
    }

    /// <summary>ServerList_GetPlayerStatistics: humans and slots over every server that has answered.</summary>
    public (int Players, int Slots) PlayerStatistics()
    {
        int players = 0, slots = 0;
        foreach (HostCacheEntry entry in _cache)
        {
            if (entry.Info.Ping == 0) continue;
            players += entry.Info.NumHumans;
            slots += entry.Info.MaxPlayers;
        }
        return (players, slots);
    }

    // NetConn_UpdateFavorites_c: the addresses in net_slist_favorites. A token that is a key
    // fingerprint (44 characters, no dot) names an identity, which nothing here can check.
    private void UpdateFavorites()
    {
        string text = _cvars.Has("net_slist_favorites") ? _cvars.GetString("net_slist_favorites") : "";
        if (text == _favoritesText) return;
        _favoritesText = text;
        _favorites.Clear();
        foreach (string token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_favorites.Count >= MaxFavorites) break;
            if (token[0] != '[' && token.Length == 44 && !token.Contains('.')) continue;
            if (NormalizeAddress(token, 26000) is { } address) _favorites.Add(address);
        }
    }

    /// <summary>
    /// "a.b.c.d" or "a.b.c.d:port" as the cache spells an address, or null if it is not a literal IPv4
    /// address. (LHNETADDRESS_FromString also resolves names and takes IPv6; a favourite written as a
    /// host name is not found here.)
    /// </summary>
    public static string? NormalizeAddress(string text, int defaultPort)
    {
        int port = defaultPort;
        string host = text;
        int colon = text.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535) return null;
            host = text[..colon];
        }
        string[] parts = host.Split('.');
        if (parts.Length != 4) return null;
        Span<int> octets = stackalloc int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]) || octets[i] > 255) return null;
        return string.Create(CultureInfo.InvariantCulture, $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}:{port}");
    }

    // ---- querying ----------------------------------------------------------------------------------

    /// <summary>
    /// ServerList_BuildDPServerQuery: the challenge a server is asked to echo - 22 characters of a keyed
    /// hash of the time, then the time in milliseconds in hexadecimal. The echo is what makes a reply
    /// believable (it answers a question this client asked, with this refresh's key) and what the ping
    /// is measured from, so a server cannot claim a better ping than it has.
    /// </summary>
    /// <remarks>The C keys HMAC-MD4 with a 12-byte challenge string; this is HMAC-SHA256 with 16 random
    /// bytes. Only this client ever checks the hash, so the choice is invisible to servers.</remarks>
    public string BuildQuery(double now)
    {
        ulong timestamp = (ulong)Math.Max(0, now * 1000.0);
        return string.Concat(Hash(timestamp), timestamp.ToString("x", CultureInfo.InvariantCulture));
    }

    private string Hash(ulong timestamp)
    {
        Span<byte> stamp = stackalloc byte[8];
        BitConverter.TryWriteBytes(stamp, timestamp);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_queryKey, stamp, mac);
        // 16 bytes are 22 base64 characters and two of padding.
        return Convert.ToBase64String(mac[..16])[..22];
    }

    /// <summary>
    /// ServerList_QueryList(resetcache, querydp = true, queryqw = false, consoleoutput = false): start a
    /// refresh. With <paramref name="resetCache"/> every entry is forgotten first; without it the known
    /// servers are asked again and those that stay silent drop out of the view.
    /// </summary>
    public void Refresh(bool resetCache) => Refresh(resetCache, Clock());

    /// <summary><see cref="Refresh(bool)"/> at a given time.</summary>
    public void Refresh(bool resetCache, double now)
    {
        if (CallbackEntry is not null) return;
        if (resetCache)
        {
            ServerQueryCount = 0;
            ServerReplyCount = 0;
            _cache.Clear();
            _byAddress.Clear();
            _view.Clear();
        }
        else
        {
            // "unsetting responded now would cause live servers to be timed out"
            if (_queryStage != 0) return;
            foreach (HostCacheEntry entry in _cache) entry.Responded = false;
        }
        bool wasIdle = _queryStage == 0;
        _queryStage = StageMasters;
        MasterQueryCount = 0;
        MasterReplyCount = 0;
        _pass = 0;
        _server = 0;

        QueryMasters();

        // "Generate new DP server query key string ... don't change key while updating"
        if (wasIdle) _queryKey = RandomNumberGenerator.GetBytes(16);
        _masterQueryTime = now;
    }

    // NetConn_QueryMasters, the DarkPlaces half: every master named by a cvar, then the favourites.
    private void QueryMasters()
    {
        if (_cache.Count >= MaxServers) return;
        Array.Clear(_masterStatus);
        for (int i = 0; i < MasterCvars.Length; i++)
        {
            string master = _cvars.Has(MasterCvars[i]) ? _cvars.GetString(MasterCvars[i]).Trim() : "";
            _masterNames[i] = master;
            if (master.Length is 0 or > 128 || Queries is null) continue;
            if (!Queries.QueryMaster(master, GameName, NetProtocolVersion)) continue;
            MasterQueryCount++;
            _masterStatus[i] = MasterQuerySent;
        }
        UpdateFavorites();
        foreach (string favorite in _favorites) PrepareQuery(favorite, true);
        // With no master to ask ("Unable to query master servers") the stage stays where it is: the
        // favourites are asked once the cool-down in Frame has passed, as in the C.
    }

    // NetConn_ClientParsePacket_ServerList_PrepareQuery: false once the cache is full.
    private bool PrepareQuery(string address, bool isFavorite)
    {
        if (_cache.Count >= MaxServers) return false;
        // "also ignore it if we have already queried it (other master server response)"
        if (_byAddress.ContainsKey(address)) return true;
        HostCacheEntry entry = new() { Index = _cache.Count };
        entry.Info.CName = address;
        entry.Info.IsFavorite = isFavorite;
        _cache.Add(entry);
        _byAddress[address] = entry;
        ServerQueryCount++;
        return true;
    }

    /// <summary>
    /// A master's answer (NetConn_ClientParsePacket_ServerList_ParseDPList): the addresses it listed, and
    /// whether the packet ended with the end-of-transmission marker. Ignored unless that master was asked
    /// in this refresh ("ignoring DarkPlaces server list from unrecognised master").
    /// </summary>
    /// <param name="master">The master as it was named in <see cref="IMenuServerQueries.QueryMaster"/>.</param>
    public void MasterReply(string master, IReadOnlyList<string> addresses, bool endOfTransmission)
    {
        int index = Array.IndexOf(_masterNames, master);
        if (index < 0 || _masterStatus[index] == 0) return;
        MasterReplyCount++;
        if (_masterStatus[index] < MasterResponse) _masterStatus[index] = MasterResponse;
        foreach (string listed in addresses)
        {
            // The owner of the socket builds these from the packet's bytes; anything else is refused here.
            if (NormalizeAddress(listed, 0) is not { } address || address.EndsWith(":0", StringComparison.Ordinal)) continue;
            if (!PrepareQuery(address, false)) break;
        }
        if (endOfTransmission) _masterStatus[index] = MasterComplete;

        StartServersIfMastersDone();
    }

    /// <summary>
    /// A master that <see cref="IMenuServerQueries.QueryMaster"/> said it would ask turned out to have no
    /// address (its name did not resolve). DarkPlaces resolves before it sends and never counts such a
    /// master as asked; the owner of the socket resolves in the background and takes the count back here,
    /// so the list does not sit out the time-out waiting for an answer that cannot come.
    /// </summary>
    public void MasterUnreachable(string master)
    {
        int index = Array.IndexOf(_masterNames, master);
        if (index < 0 || _masterStatus[index] != MasterQuerySent) return;
        _masterStatus[index] = 0;
        if (MasterQueryCount > 0) MasterQueryCount--;
        StartServersIfMastersDone();
    }

    // "begin or resume serverlist queries" once every master that was asked has finished its list.
    private void StartServersIfMastersDone()
    {
        bool any = false;
        foreach (int status in _masterStatus)
        {
            if (status != 0 && status < MasterComplete) return;
            any |= status != 0;
        }
        if (any && _queryStage != 0) _queryStage = StageServers;
    }

    /// <summary>
    /// NetConn_QueryQueueFrame: once a frame. Sends the frame's share of server queries (three passes
    /// over the cache by default), then times out the servers that never answered and ends the refresh.
    /// </summary>
    public void Frame(double now, double frameTime)
    {
        if (_queryStage == 0) return;

        // "apply a cool down time after master server replies, to avoid messing up the ping times"
        if (_queryStage < StageServers)
        {
            if (now < _masterQueryTime + Cvar("net_slist_timeout", 4)) return;
            _queryStage = StageServers;
        }

        // "each time querycounter reaches 1.0 issue a query"
        _queryCounter += Math.Clamp(frameTime, 0, 1) * Cvar("net_slist_queriespersecond", 128);
        int maxQueries = Math.Clamp((int)_queryCounter, 0, Math.Max(0, (int)Cvar("net_slist_queriesperframe", 2)));
        _queryCounter -= maxQueries;
        if (maxQueries == 0) return;

        int queriesPerServer = Math.Clamp((int)Cvar("net_slist_maxtries", 3), 1, 8);
        if (_pass < queriesPerServer)
        {
            string query = BuildQuery(now);
            float interval = Cvar("net_slist_interval", 1);
            for (int queries = 0; _server < _cache.Count; _server++)
            {
                HostCacheEntry entry = _cache[_server];
                // "continue this pass at the current server on a later frame"
                if (queries >= maxQueries || now <= entry.QueryTime + interval) return;
                Queries?.QueryServer(entry.Info.CName, query);
                entry.QueryTime = now;
                queries++;
            }
        }
        else
        {
            // "check timeouts"
            for (; _server < _cache.Count; _server++)
            {
                HostCacheEntry entry = _cache[_server];
                if (entry.Responded || entry.Info.Ping == 0) continue;
                // "you have no chance to survive make your timeout"
                if (now <= entry.QueryTime + MaxPing / 1000.0) return;
                ServerReplyCount--;
                if (!Paused) _view.Remove(entry);
                entry.Info.Ping = 0;   // "removed later by ServerList_ViewList_Insert if net_slist_pause"
            }
        }

        // "done with this pass"
        _pass++;
        _server = 0;
        if (_pass > queriesPerServer)
        {
            _pass = 0;
            _queryStage = 0;
        }
    }

    // ---- replies -----------------------------------------------------------------------------------

    /// <summary>
    /// A server's statusResponse or infoResponse (the two cases of NetConn_ClientParsePacket and
    /// NetConn_ClientParsePacket_ServerList_ProcessReply / _UpdateCache).
    /// </summary>
    /// <param name="address">Where the datagram came from, "a.b.c.d:port" - the socket's word, not the packet's.</param>
    /// <param name="infoString">The reply's "\key\value..." line.</param>
    /// <param name="players">The player lines of a statusResponse, "" for an infoResponse.</param>
    /// <returns>True if the reply was accepted into the cache.</returns>
    public bool ServerReply(string address, string infoString, string players, double now)
    {
        if (NormalizeAddress(address, 0) is not { } cname || infoString.Length > 8192) return false;

        // "the challenge is (ab)used to return the query time": 22 hash characters, then hex milliseconds.
        string challenge = InfoValue(infoString, "challenge", 128);
        if (challenge.Length <= 22 || !ulong.TryParse(challenge.AsSpan(22), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong timestamp)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(timestamp)), Encoding.ASCII.GetBytes(challenge[..22])))
        {
            RepliesRefused++;
            return false;
        }

        if (!_byAddress.TryGetValue(cname, out HostCacheEntry? entry))
        {
            // "Received reply from unlisted server": a LAN server, or one a master did not list.
            if (_cache.Count >= MaxServers) return false;
            entry = new HostCacheEntry { Index = _cache.Count, QueryTime = _masterQueryTime };
            entry.Info.CName = cname;
            _cache.Add(entry);
            _byAddress[cname] = entry;
        }

        float ping = (float)(now * 1000.0 - timestamp);
        // "server loading map, client stall, etc"
        if (!(ping > 0) || ping > MaxPing || (entry.Info.Ping != 0 && ping > entry.Info.Ping + 100)) return false;
        // "never round down to 0, 0 latency is impossible, 0 means no data available"
        if (ping < 1) ping = 1;
        if (entry.Info.Ping != 0) entry.Info.Ping = (int)((entry.Info.Ping + ping) * 0.5f + 0.5f);
        else
        {
            entry.Info.Ping = (int)(ping + 0.5f);
            ServerReplyCount++;
        }
        entry.Responded = true;

        HostCacheInfo info = entry.Info;
        info.Players = Clean(players, HostCacheInfo.PlayersSize, keepNewlines: true);
        info.Game = InfoValue(infoString, "gamename", HostCacheInfo.GameSize);
        info.Mod = InfoValue(infoString, "modname", HostCacheInfo.ModSize);
        info.Map = InfoValue(infoString, "mapname", HostCacheInfo.MapSize);
        info.Name = InfoValue(infoString, "hostname", HostCacheInfo.NameSize);
        info.QcStatus = InfoValue(infoString, "qcstatus", HostCacheInfo.QcStatusSize);
        info.Protocol = InfoNumber(infoString, "protocol", -1);
        info.NumPlayers = InfoNumber(infoString, "clients", 0);
        info.NumBots = InfoNumber(infoString, "bots", -1);
        info.MaxPlayers = InfoNumber(infoString, "sv_maxclients", 0);
        info.GameVersion = InfoNumber(infoString, "gameversion", 0);
        info.NumHumans = info.NumPlayers - Math.Max(0, info.NumBots);
        info.FreeSlots = info.MaxPlayers - info.NumPlayers;

        // NetConn_ClientParsePacket_ServerList_UpdateCache: the two legacy description lines, then the view.
        char pingColor = info.Ping >= 300 ? '1' : info.Ping >= 200 ? '3' : '7';
        char playersColor = info.NumHumans > 0 && info.NumHumans < info.MaxPlayers ? (info.NumHumans >= 4 ? '7' : '3') : '1';
        entry.Line1 = Cut(string.Create(CultureInfo.InvariantCulture, $"^{pingColor}{info.Ping,5}^7 ^{playersColor}{info.NumPlayers,3}^7/{info.MaxPlayers,3} {Cut(info.Name, 65),-65}"), 127);
        char versionColor = GameVersionAccepted(info.GameVersion) ? '4' : '1';
        entry.Line2 = Cut($"^4{Cut(info.CName, 21),-21} {Cut(info.Game, 19),-19} ^{versionColor}{Cut(info.Mod, 17),-17}^4 {Cut(info.Map, 20),-20}", 127);
        if (!Paused && CallbackEntry is null)
        {
            _view.Remove(entry);
            ViewInsert(entry);
        }
        return true;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    // What a server may put in a field: no control characters (a line feed would end a console line if
    // the text were ever put in one; DarkPlaces relies on the packet's own line structure for that).
    private static string Clean(string text, int size, bool keepNewlines = false)
    {
        int limit = size - 1;
        StringBuilder result = new(Math.Min(text.Length, limit));
        foreach (char c in text)
        {
            if (result.Length >= limit) break;
            if (c == '\n' && keepNewlines) result.Append(c);
            else if (c >= ' ' && c != '\x7f') result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>
    /// com_infostring.c InfoString_GetValue: the value of <paramref name="key"/> in "\key\value\key\value",
    /// cut to a buffer of <paramref name="size"/> bytes, "" if absent. Control characters are dropped.
    /// </summary>
    public static string InfoValue(string info, string key, int size)
    {
        int i = 0;
        while (i < info.Length && info[i] == '\\')
        {
            int keyEnd = info.IndexOf('\\', i + 1);
            if (keyEnd < 0) return "";
            int valueEnd = info.IndexOf('\\', keyEnd + 1);
            if (valueEnd < 0) valueEnd = info.Length;
            if (keyEnd - i - 1 == key.Length && string.CompareOrdinal(info, i + 1, key, 0, key.Length) == 0)
                return Clean(info[(keyEnd + 1)..valueEnd], size);
            i = valueEnd;
        }
        return "";
    }

    // "InfoString_GetValue(...) ? atoi(value) : fallback"
    private static int InfoNumber(string info, string key, int fallback)
    {
        string value = InfoValue(info, key, 128);
        if (value.Length == 0) return fallback;
        int i = 0;
        while (i < value.Length && value[i] is ' ' or '\t') i++;
        bool negative = false;
        if (i < value.Length && value[i] is '+' or '-') negative = value[i++] == '-';
        long number = 0;
        for (; i < value.Length && char.IsAsciiDigit(value[i]); i++) number = Math.Min(number * 10 + (value[i] - '0'), int.MaxValue);
        return (int)(negative ? -number : number);
    }
}
