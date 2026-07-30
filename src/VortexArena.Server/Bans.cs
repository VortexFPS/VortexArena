using System.Text;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The IP/identity mask set derived for one client — the C# successor to the QC scratch globals
/// <c>ban_ip1..ban_ip4</c> + <c>ban_idfp</c> that <c>Ban_GetClientIP</c> fills (server/ipban.qc). For an IPv4
/// address these are the /8, /16, /24 and /32 forms; for IPv6 the /32, /48, /56 and /64 forms. <see cref="Idfp"/>
/// is the client's crypto identity (or null). <see cref="Ok"/> is false when the address could not be parsed.
/// </summary>
public readonly struct ClientBanIp
{
    public readonly string Mask8, Mask16, Mask24, Mask32; // QC ban_ip1..ban_ip4
    public readonly string? Idfp;                          // QC ban_idfp
    public readonly bool Ok;

    public ClientBanIp(string m8, string m16, string m24, string m32, string? idfp)
    {
        Mask8 = m8; Mask16 = m16; Mask24 = m24; Mask32 = m32; Idfp = idfp; Ok = true;
    }

    /// <summary>QC <c>Ban_KickBanClient</c> masksize→ip pick: 1→/8, 2→/16, 3→/24, else→/32.</summary>
    public string ForMask(int masksize) => masksize switch
    {
        1 => Mask8,
        2 => Mask16,
        3 => Mask24,
        _ => Mask32,
    };

    public static readonly ClientBanIp Invalid = default;
}

/// <summary>One local ban slot — QC the parallel arrays <c>ban_ip[i]</c> / <c>ban_expire[i]</c>.</summary>
public sealed class BanEntry
{
    /// <summary>The banned IP/mask string OR a crypto identity (QC <c>ban_ip[i]</c>).</summary>
    public string Ip = "";

    /// <summary>Absolute sim time the ban lapses (QC <c>ban_expire[i]</c>); 0 = empty slot.</summary>
    public float Expire;

    public bool IsEmpty => Expire <= 0f;
}

/// <summary>
/// The server ban subsystem — the Godot-free essence of server/ipban.qc + server/command/banning.qc. It owns
/// the local ban store (an index-stable list of <see cref="BanEntry"/> slots persisted in the
/// <c>g_banned_list</c> cvar), plus the separate <c>g_chatban_list</c> / <c>g_playban_list</c> /
/// <c>g_voteban_list</c> prefix-match lists (mute / forced-spectate / vote-ban).
///
/// Faithful to QC: a crypto-id ban always wins; an IP ban under <c>g_banned_list_idmode</c> only applies to a
/// client that has no crypto id; an <see cref="Insert"/> never shortens an existing ban and evicts the
/// soonest-to-expire slot when full; the serialized form is the version-1 token string
/// <c>"1 &lt;ip&gt; &lt;secs&gt; ..."</c> storing time REMAINING (not absolute); <c>unban #N</c> uses the same
/// index <see cref="View"/> prints. Index stability matters, so expired/deleted slots are left in place
/// rather than compacted.
///
/// The host wires <see cref="Roster"/> (connected clients) and <see cref="DropClient"/> (the kick pipeline) so
/// <see cref="Enforce"/> can remove matching live clients. The online ban-list sync (the QC <c>uri_get</c>
/// HTTP cross-server propagation) is intentionally out of this Godot-free core; the local store + console
/// command surface is complete.
/// </summary>
public sealed class Bans
{
    /// <summary>QC <c>BAN_MAX</c>: max simultaneous ban slots.</summary>
    public const int BanMax = 256;

    /// <summary>QC <c>g_chatban_list</c> cvar name — the mute prefix-list.</summary>
    public const string ChatBanList = "g_chatban_list";

    /// <summary>QC <c>g_playban_list</c> cvar name — the forced-spectate prefix-list.</summary>
    public const string PlayBanList = "g_playban_list";

    /// <summary>QC <c>g_voteban_list</c> cvar name — the no-voting prefix-list.</summary>
    public const string VoteBanList = "g_voteban_list";

    private readonly List<BanEntry> _bans = new();
    private bool _loaded;

    /// <summary>Connected clients, for <see cref="Enforce"/> (QC the FOREACH_CLIENT scan). Wired by the host.</summary>
    public Func<IEnumerable<Player>>? Roster { get; set; }

    /// <summary>Kick callback (QC <c>dropclient</c>). Wired by the host; without it, enforce only logs.</summary>
    public Action<Player, string>? DropClient { get; set; }

    /// <summary>Diagnostics sink (QC bprint/LOG_INFO). Defaults to swallowing; a host/test can capture.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The ban slots (read-only) — empty slots included, to keep <c>unban #N</c> indices stable.</summary>
    public IReadOnlyList<BanEntry> Slots => _bans;

    private void Echo(string s) => Log?.Invoke(s);

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;

    // =============================================================================================
    // IP / identity mask derivation (QC Ban_GetClientIP)
    // =============================================================================================

    /// <summary>
    /// QC <c>Ban_GetClientIP</c>: derive the four IP-mask strings + crypto identity for a client from its
    /// <see cref="Player.NetAddress"/> / <see cref="Player.PersistentId"/>. Returns <see cref="ClientBanIp.Invalid"/>
    /// (Ok=false) for a non-remote client (local/bot/blank) or an unparsable address.
    /// </summary>
    public static ClientBanIp GetClientIp(Player client)
    {
        string? idfp = string.IsNullOrEmpty(client.PersistentId) ? null : client.PersistentId;
        string s = client.NetAddress ?? "";
        if (s.Length == 0 || s == "bot" || s == "local")
            return ClientBanIp.Invalid;

        int i1 = s.IndexOf('.');
        if (i1 >= 0)
        {
            // ---- IPv4 ----  ban_ip1=/8, ban_ip2=/16, ban_ip3=/24, ban_ip4=/32 (QC)
            int i2 = s.IndexOf('.', i1 + 1);
            if (i2 < 0) return ClientBanIp.Invalid;
            int i3 = s.IndexOf('.', i2 + 1);
            if (i3 < 0) return ClientBanIp.Invalid;
            int i4 = s.IndexOf('.', i3 + 1);
            if (i4 >= 0) s = s.Substring(0, i4);          // strip a trailing ".port"-ish remainder (QC)
            else
            {
                int colon = s.IndexOf(':');               // strip ":port" if present
                if (colon >= 0) s = s.Substring(0, colon);
            }
            string m8 = s.Substring(0, i1);
            string m16 = s.Substring(0, i2);
            string m24 = s.Substring(0, i3);
            string m32 = s;
            return new ClientBanIp(m8, m16, m24, m32, idfp);
        }

        // ---- IPv6 ----  (QC: ban_ip1=/32, ban_ip2=/48, ban_ip3=/56, ban_ip4=/64)
        int c1 = s.IndexOf(':');
        if (c1 < 0) return ClientBanIp.Invalid;
        int c2 = s.IndexOf(':', c1 + 1);
        if (c2 < 0) return ClientBanIp.Invalid;
        int c3 = s.IndexOf(':', c2 + 1);
        if (c3 < 0) return ClientBanIp.Invalid;
        string g32 = s.Substring(0, c1) + "::/32";
        string g48 = s.Substring(0, c2) + "::/48";
        string g64 = s.Substring(0, c3) + "::/64";
        string g56 = (c3 - c2 > 3)
            ? s.Substring(0, c2) + ":" + s.Substring(c2 + 1, c3 - c2 - 3) + "00::/56"
            : s.Substring(0, c2) + ":0::/56";
        return new ClientBanIp(g32, g48, g56, g64, idfp);
    }

    // =============================================================================================
    // ban checks (QC Ban_IsClientBanned / Ban_MaybeEnforceBan)
    // =============================================================================================

    /// <summary>
    /// QC <c>Ban_IsClientBanned(client, -1)</c>: is this client banned by any active slot? A crypto-id match
    /// always bans; an IP match bans unless <c>g_banned_list_idmode</c> is set and the client has a crypto id
    /// (idmode: IP bans only catch anonymous clients). Loads the store on first use.
    /// </summary>
    public bool IsClientBanned(Player client)
    {
        if (!_loaded) Load();
        ClientBanIp ip = GetClientIp(client);
        if (!ip.Ok) return false;

        bool ipBanned = false;
        float now = Now;
        foreach (BanEntry b in _bans)
        {
            if (b.IsEmpty || now > b.Expire) continue;
            string s = b.Ip;
            if (s == ip.Mask8 || s == ip.Mask16 || s == ip.Mask24 || s == ip.Mask32)
                ipBanned = true;
            if (ip.Idfp is not null && ip.Idfp == s)
                return true; // crypto-id ban always wins (QC returns immediately)
        }

        if (!ipBanned) return false;
        if (!Cvars.Bool("g_banned_list_idmode")) return true;
        return ip.Idfp is null; // idmode: an IP ban only applies when the client has no crypto id
    }

    /// <summary>
    /// QC <c>Ban_MaybeEnforceBan</c>: if the client is banned, tell them (when <c>g_ban_telluser</c>) and kick
    /// them. Returns true if the client was banned (and thus dropped).
    /// </summary>
    public bool MaybeEnforceBan(Player client)
    {
        if (!IsClientBanned(client))
            return false;
        // QC Ban_MaybeEnforceBan (server/ipban.qc:451-467): log netaddress (not netname), and include the
        // crypto_idfp in parentheses when the client has one — used for admin audit trails.
        string addr = client.NetAddress ?? client.NetName;
        if (!string.IsNullOrEmpty(client.PersistentId))
            Echo($"^1NOTE:^7 banned client {addr} ({client.PersistentId}) just tried to enter");
        else
            Echo($"^1NOTE:^7 banned client {addr} just tried to enter");
        if (Cvars.Bool("g_ban_telluser"))
            DropClient?.Invoke(client, "You are banned from this server.");
        else
            DropClient?.Invoke(client, "banned");
        return true;
    }

    // =============================================================================================
    // insert / delete (QC Ban_Insert / Ban_Delete)
    // =============================================================================================

    /// <summary>
    /// QC <c>Ban_Insert</c>: ban <paramref name="ip"/> (an IP-mask string or a crypto id) for
    /// <paramref name="bantime"/> seconds with <paramref name="reason"/>. Prolongs (never shortens) an existing
    /// matching ban; otherwise takes a free/expired slot, or evicts the soonest-to-expire slot when full
    /// (refusing if that victim's ban is longer than the new one). Persists + enforces. Returns true if a NEW
    /// slot was created (false if it only prolonged an existing one or could not be inserted).
    /// </summary>
    public bool Insert(string ip, float bantime, string reason)
    {
        if (!_loaded) Load();
        if (string.IsNullOrEmpty(ip))
            return false;
        float now = Now;

        // already banned? prolong only (never shorten), then re-enforce.
        for (int i = 0; i < _bans.Count; i++)
        {
            if (_bans[i].Ip == ip && !_bans[i].IsEmpty)
            {
                if (now + bantime > _bans[i].Expire)
                    _bans[i].Expire = now + bantime;
                Save();
                Enforce(i, reason);
                return false;
            }
        }

        // find a free/expired slot.
        int slot = -1;
        for (int i = 0; i < _bans.Count; i++)
            if (_bans[i].IsEmpty || now > _bans[i].Expire) { slot = i; break; }

        if (slot < 0)
        {
            if (_bans.Count < BanMax)
            {
                slot = _bans.Count;
                _bans.Add(new BanEntry());
            }
            else
            {
                // full: evict the slot with the smallest expiry (QC the soonest-to-expire victim).
                int victim = 0;
                for (int i = 1; i < _bans.Count; i++)
                    if (_bans[i].Expire < _bans[victim].Expire) victim = i;
                if (_bans[victim].Expire > now + bantime)
                {
                    Echo("Could not insert ban: no free ban slot (a longer ban occupies every slot)");
                    return false;
                }
                slot = victim;
            }
        }

        _bans[slot].Ip = ip;
        _bans[slot].Expire = now + bantime;
        Save();
        Enforce(slot, reason);
        return true;
    }

    /// <summary>QC <c>Ban_Delete</c>: free ban slot <paramref name="i"/> (e.g. <c>unban #i</c>). Persists.</summary>
    public bool Delete(int i)
    {
        if (i < 0 || i >= _bans.Count) return false;
        if (_bans[i].IsEmpty) return false;
        _bans[i].Ip = "";
        _bans[i].Expire = 0f;
        Save();
        return true;
    }

    /// <summary>
    /// QC <c>Ban_KickBanClient</c>: resolve the client's address, ban the chosen mask (+ its crypto id if any)
    /// for <paramref name="bantime"/> seconds, which enforces the kick. Falls back to a plain kick if the
    /// address can't be resolved.
    /// </summary>
    public void KickBanClient(Player client, float bantime, int masksize, string reason)
    {
        ClientBanIp ip = GetClientIp(client);
        if (!ip.Ok)
        {
            DropClient?.Invoke(client, $"Kickbanned: {reason}");
            return;
        }
        Insert(ip.ForMask(masksize), bantime, reason);
        if (ip.Idfp is not null)
            Insert(ip.Idfp, bantime, reason);
    }

    /// <summary>
    /// QC <c>Ban_Enforce</c>: kick every currently-connected client matching ban slot <paramref name="j"/>
    /// (or all slots if j&lt;0). Emits a per-client <c>^1NOTE:^7 banned client &lt;name&gt;^7 has to go\n</c>
    /// line (concatenated into one bprint) and appends <c>": affects &lt;name&gt;, ..."</c> to the reason,
    /// matching Base exactly (server/ipban.qc:Ban_Enforce). No-op without a wired <see cref="Roster"/>.
    /// </summary>
    public void Enforce(int j, string reason)
    {
        if (Roster is null) return;
        // QC Ban_Enforce: build `s` as per-client "^1NOTE:^7 banned client <name>^7 has to go\n" lines,
        // append ": affects <name>, ..." to the reason as the roster is walked, then bprint(s).
        var sb = new StringBuilder();
        foreach (Player p in Roster())
        {
            if (!(j < 0 ? IsClientBanned(p) : IsClientBannedBySlot(p, j))) continue;
            if (!string.IsNullOrEmpty(reason))
            {
                reason = sb.Length == 0
                    ? reason + ": affects " + p.NetName
                    : reason + ", " + p.NetName;
            }
            sb.Append("^1NOTE:^7 banned client ").Append(p.NetName).Append("^7 has to go\n");
            DropClient?.Invoke(p, string.IsNullOrEmpty(reason) ? "banned" : reason);
        }
        if (sb.Length > 0)
            Echo(sb.ToString());
    }

    private bool IsClientBannedBySlot(Player client, int slot)
    {
        if (slot < 0 || slot >= _bans.Count) return false;
        BanEntry b = _bans[slot];
        if (b.IsEmpty || Now > b.Expire) return false;
        ClientBanIp ip = GetClientIp(client);
        if (!ip.Ok) return false;
        if (ip.Idfp is not null && ip.Idfp == b.Ip) return true;
        bool ipMatch = b.Ip == ip.Mask8 || b.Ip == ip.Mask16 || b.Ip == ip.Mask24 || b.Ip == ip.Mask32;
        if (!ipMatch) return false;
        if (!Cvars.Bool("g_banned_list_idmode")) return true;
        return ip.Idfp is null;
    }

    /// <summary>
    /// QC <c>Ban_View</c>: print a header, each active ban (with its <c>unban #N</c> index), and the Base
    /// footer <c>^2Done listing all active (N) bans.</c> (server/ipban.qc:Ban_View).
    /// </summary>
    public void View()
    {
        if (!_loaded) Load();
        // QC: LOG_INFO("^2Listing all existing active bans:");
        Echo("^2Listing all existing active bans:");
        float now = Now;
        int n = 0;
        for (int i = 0; i < _bans.Count; i++)
        {
            if (_bans[i].IsEmpty || now > _bans[i].Expire) continue;
            Echo($"  #{i}: {_bans[i].Ip} is still banned for {_bans[i].Expire - now:0} seconds");
            n++;
        }
        // QC: LOG_INFO("^2Done listing all active (", ftos(n), ") bans.");
        Echo($"^2Done listing all active ({n}) bans.");
    }

    // =============================================================================================
    // persistence (QC Ban_SaveBans / Ban_LoadBans via the g_banned_list cvar)
    // =============================================================================================

    /// <summary>
    /// QC <c>Ban_SaveBans</c>: serialize the active bans into <c>g_banned_list</c> as the version-1 token
    /// string <c>"1 &lt;ip&gt; &lt;secs-remaining&gt; ..."</c> (storing time remaining, not absolute time, so
    /// it survives a process restart relative to the new clock). No-op until <see cref="Load"/> has run.
    /// </summary>
    public void Save()
    {
        if (!_loaded) return;
        float now = Now;
        var sb = new StringBuilder("1");
        foreach (BanEntry b in _bans)
        {
            if (b.IsEmpty || now > b.Expire) continue;
            sb.Append(' ').Append(b.Ip).Append(' ')
              .Append((b.Expire - now).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }
        Cvars.Set("g_banned_list", sb.Length <= 1 ? "" : sb.ToString());
    }

    /// <summary>
    /// QC <c>Ban_LoadBans</c>: rebuild the store from <c>g_banned_list</c> (version-1 token string). The stored
    /// seconds-remaining are turned back into an absolute expiry against the current clock.
    /// </summary>
    public void Load()
    {
        _bans.Clear();
        _loaded = true;
        string list = Cvars.String("g_banned_list");
        string[] tok = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return;
        if (tok[0] != "1") return; // only version 1 understood
        float now = Now;
        for (int i = 1; i + 1 < tok.Length; i += 2)
        {
            float secs = float.TryParse(tok[i + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
            _bans.Add(new BanEntry { Ip = tok[i], Expire = now + secs });
        }
    }

    // =============================================================================================
    // mute / playban / voteban prefix lists (QC the g_chatban/playban/voteban_list cvars)
    // =============================================================================================

    /// <summary>
    /// QC <c>findinlist_abbrev</c>: does any space-separated word of <paramref name="list"/> match
    /// <paramref name="tofind"/> as a prefix (an entry matches if it is a prefix of the target)? The matching
    /// rule for the mute / playban / voteban lists.
    /// </summary>
    public static bool FindInListAbbrev(string tofind, string list)
    {
        if (string.IsNullOrEmpty(tofind) || string.IsNullOrEmpty(list))
            return false;
        foreach (string w in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (w.Length > 0 && tofind.StartsWith(w, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>QC <c>PlayerInIPList</c> / <c>PlayerInIDList</c> combined: is the player in a prefix-match cvar list?</summary>
    public static bool PlayerInList(Player p, string listCvar)
    {
        string list = Cvars.String(listCvar);
        if (string.IsNullOrEmpty(list)) return false;
        bool byIp = p.NetAddress is { Length: > 0 } && p.NetAddress != "local" && p.NetAddress != "bot"
                    && FindInListAbbrev(p.NetAddress, list);
        bool byId = !string.IsNullOrEmpty(p.PersistentId) && FindInListAbbrev(p.PersistentId, list);
        return byIp || byId;
    }

    /// <summary>QC the mute/playban/voteban <c>add</c>: append the client's IP and/or id to a cvar list (deduped).</summary>
    public static void AddToList(Player p, string listCvar)
    {
        string list = Cvars.String(listCvar);
        var sb = new StringBuilder(list);
        if (p.NetAddress is { Length: > 0 } && p.NetAddress != "local" && p.NetAddress != "bot"
            && !FindInListAbbrev(p.NetAddress, list))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p.NetAddress);
        }
        if (!string.IsNullOrEmpty(p.PersistentId) && !FindInListAbbrev(p.PersistentId, list))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p.PersistentId);
        }
        Cvars.Set(listCvar, sb.ToString());
    }

    /// <summary>QC the un-mute/playban/voteban: rebuild a cvar list dropping the client's IP + id entries.</summary>
    public static void RemoveFromList(Player p, string listCvar)
    {
        string list = Cvars.String(listCvar);
        var kept = new List<string>();
        foreach (string w in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w == p.NetAddress) continue;
            if (!string.IsNullOrEmpty(p.PersistentId) && p.PersistentId.StartsWith(w, StringComparison.Ordinal)) continue;
            kept.Add(w);
        }
        Cvars.Set(listCvar, string.Join(' ', kept));
    }

    /// <summary>
    /// QC <c>PlayerInList(client, autocvar_g_chatban_list)</c> (server/client.qc:1246): is the player muted by
    /// the chat-ban prefix list? The seam callers use to re-apply mute on connect (and the mute command's check).
    /// </summary>
    public static bool IsChatBanned(Player p) => PlayerInList(p, ChatBanList);

    /// <summary>
    /// QC <c>PlayerInList(client, autocvar_g_playban_list)</c> (server/client.qc:1243 / 2274): is the player on
    /// the play-ban (forced-spectate) prefix list? The single seam for both the connect-time re-spectate and the
    /// load-bearing join-attempt gate (<c>Join_Try</c> refuses the join with <c>CENTER_JOIN_PLAYBAN</c> when this
    /// is true for a non-INGAME client).
    /// </summary>
    public static bool IsPlayBanned(Player p) => PlayerInList(p, PlayBanList);

    /// <summary>
    /// QC <c>PlayerInList(client, autocvar_g_voteban_list)</c>: is the player on the vote-ban prefix list (may not
    /// call or cast votes)? The seam the vote controller consults.
    /// </summary>
    public static bool IsVoteBanned(Player p) => PlayerInList(p, VoteBanList);
}
