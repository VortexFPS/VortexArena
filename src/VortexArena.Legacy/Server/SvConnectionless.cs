// Port of Base/darkplaces/netconn.c NetConn_ServerParsePacket (the connectionless half: getchallenge,
// connect, getinfo, getstatus, ping; lines 3207-3508 and 3572-3584), NetConn_BuildChallengeString,
// NetConn_BuildStatusResponse, NetConn_PreventFlood and NetConn_ClearFlood, with the challenge table
// of netconn.h and the flood tables of server.h; and com_infostring.c InfoString_GetValue.
using System.Security.Cryptography;
using System.Text;

namespace VortexArena.Legacy.Server;

/// <summary>What the owner knows about the sender of a connect request (the scan over svs.clients).</summary>
public enum SvClientPresence
{
    /// <summary>No client slot holds this address.</summary>
    None,
    /// <summary>A slot holds it and the client has not yet sent <c>begin</c> (client->begun is false):
    /// its first <c>accept</c> was probably lost, so it is sent again.</summary>
    Connecting,
    /// <summary>A slot holds it and the client was in the game (client->begun): it crashed and is
    /// coming back, so it is accepted into the same slot and sent the server info again.</summary>
    Begun,
}

/// <summary>One active client as getstatus lists it (the fields NetConn_BuildStatusResponse reads off client_t).</summary>
/// <param name="Name">client->name.</param>
/// <param name="Frags">client->frags. Xonotic marks a spectator with -666.</param>
/// <param name="Ping">client->ping, in seconds. Reported in milliseconds, clamped to 1..9999 for a human.</param>
/// <param name="IsBot">True when the slot has no network connection. A bot is reported with ping 0.</param>
/// <param name="Colors">client->colors (shirt * 16 + pants), from which the team number is derived.</param>
/// <param name="ClientStatus">The player entity's <c>clientstatus</c> string field; replaces the frags when not empty.</param>
public readonly record struct SvStatusPlayer(string Name, int Frags, float Ping, bool IsBot, int Colors, string ClientStatus);

/// <summary>
/// What <see cref="SvConnectionless{TAddress}"/> needs from the server that owns it: the values a
/// status query reports, and the client slots a connect request is decided against.
/// </summary>
public interface ISvConnectionlessHost<TAddress>
{
    /// <summary>gamenetworkfiltername: "Xonotic". The master server and the browser filter on it.</summary>
    string GameName { get; }
    /// <summary>com_modname: the game directory, "data" for stock Xonotic.</summary>
    string ModName { get; }
    /// <summary>The <c>gameversion</c> cvar (Xonotic 0.8.6 sets 806).</summary>
    int GameVersion { get; }
    /// <summary>svs.maxclients.</summary>
    int MaxClients { get; }
    /// <summary>sv.worldbasename: the map name without directory or extension.</summary>
    string MapName { get; }
    /// <summary>The <c>hostname</c> cvar.</summary>
    string HostName { get; }
    /// <summary>The server program's <c>worldstatus</c> global (Xonotic: gametype, version, slots, mutators).</summary>
    string WorldStatus { get; }
    /// <summary>The <c>teamplay</c> cvar. Above zero, each getstatus line carries a team number.</summary>
    int TeamPlay { get; }

    /// <summary>Append every active client, in slot order.</summary>
    void GetStatusPlayers(List<SvStatusPlayer> players);

    /// <summary>Whether a client slot already holds exactly this address (port included).</summary>
    SvClientPresence FindClient(TAddress address);

    /// <summary>
    /// A validated connect request from an address no slot holds: put it in a free slot
    /// (NetConn_Open + SV_ConnectClient) and return true, or return false when there is none, which
    /// is answered with "reject Server is full.".
    /// </summary>
    /// <param name="userInfo">The request's info string, from its first backslash
    /// (<c>\protocol\darkplaces 3\protocols\DP7\challenge\...</c>).</param>
    bool TryConnectClient(TAddress address, string userInfo);

    /// <summary>A <see cref="SvClientPresence.Begun"/> client reconnected: send it the server info
    /// again (SV_SendServerinfo), keeping everything else about it.</summary>
    void ReconnectClient(TAddress address);
}

/// <summary>What <see cref="SvConnectionless{TAddress}.Handle"/> made of a datagram.</summary>
public enum SvConnectionlessKind
{
    /// <summary>Not a connectionless packet: hand it to the client's netchan.</summary>
    NotConnectionless,
    /// <summary>Connectionless, and nothing this server answers (including an unknown command).</summary>
    Ignored,
    /// <summary>Dropped without a reply by one of the flood limits.</summary>
    FloodDropped,
    /// <summary>getchallenge answered.</summary>
    Challenge,
    /// <summary>connect dropped without a reply: the challenge is missing, unknown or for another address.</summary>
    BadChallenge,
    /// <summary>connect accepted into a new slot; <see cref="SvConnectionlessResult.UserInfo"/> is set.</summary>
    Accepted,
    /// <summary>connect from a client still signing on: <c>accept</c> repeated, nothing else changes.</summary>
    AcceptedDuplicate,
    /// <summary>connect from a client that was in the game: accepted into its old slot.</summary>
    AcceptedReconnect,
    /// <summary>connect answered with <c>reject</c>; <see cref="SvConnectionlessResult.RejectReason"/> is set.</summary>
    Rejected,
    /// <summary>getinfo answered (or getstatus that did not fit, answered as infoResponse).</summary>
    Info,
    /// <summary>getstatus answered.</summary>
    Status,
    /// <summary>ping answered with ack.</summary>
    Ack,
}

public readonly record struct SvConnectionlessResult(SvConnectionlessKind Kind, string UserInfo = "", string RejectReason = "")
{
    public bool IsConnectionless => Kind != SvConnectionlessKind.NotConnectionless;
}

/// <summary>
/// The server's side of everything DarkPlaces exchanges before (and beside) a game connection: four
/// 0xFF bytes and a text command.
///
/// <code>
/// client: getchallenge
/// server: challenge &lt;11 characters&gt;
/// client: connect\protocol\darkplaces 3\protocols\DP7\challenge\&lt;the 11 characters&gt;
/// server: accept                         or: reject &lt;reason&gt;
///
/// anyone: getinfo [challenge]            server: infoResponse\n\key\value...
/// anyone: getstatus [challenge]          server: statusResponse\n\key\value...\n&lt;one line per player&gt;
/// anyone: ping                           server: ack
/// </code>
///
/// The challenge exists because UDP source addresses can be forged: only someone who really receives
/// packets at an address can echo the token sent there. Until it has been echoed, nothing the sender
/// does costs the server more than a table slot, and every table here is a fixed size, as in the C.
///
/// No socket and no clock: datagrams come in through <see cref="Handle"/>, replies are appended to
/// the caller's list, time is whatever the caller says it is, and <typeparamref name="TAddress"/> is
/// whatever the transport uses to name a peer (an IPEndPoint over UDP, anything equatable over a
/// loopback pair of queues).
///
/// Left out, knowingly, with where the C has them:
/// - d0_blind_id: Crypto_ServerParsePacket (netconn.c:3251, every packet goes through it first),
///   Crypto_ServerAppendToChallenge (:3306), the "crypto->authenticated" branches of connect
///   (:3318-3411) and the <c>\d0_blind_id\</c> key of the status response (:2803).
/// - rcon: "srcon HMAC-MD4 TIME", "srcon HMAC-MD4 CHALLENGE" and "rcon" (netconn.c:3508-3563), with
///   the rcon half of the challenge table (RCon_Authenticate, :3020-3055).
/// - "extResponse" (:3564), which only feeds a QuakeC builtin.
/// - The master server heartbeat: NetConn_Heartbeat (:4010), called from connect at :3456.
/// - NetQuake control packets (CCREQ_CONNECT and friends, :3587-3860). DarkPlaces itself ignores
///   them when the game protocol is DP4 or later.
///
/// The loopback exemption - a peer of type LHNETADDRESSTYPE_LOOP ("islocal") bypasses sv_public - is
/// <see cref="IsLocal"/>: the owner says which addresses are its own in-process endpoints. Without
/// one, <see cref="Public"/> applies to every address.
/// </summary>
public sealed class SvConnectionless<TAddress> where TAddress : notnull
{
    /// <summary>MAX_CHALLENGES (netconn.h).</summary>
    public const int MaxChallenges = 128;
    /// <summary>Characters in a challenge: sizeof(challenge_t.string) - 1.</summary>
    public const int ChallengeLength = 11;
    /// <summary>MAX_CONNECTFLOODADDRESSES (server.h).</summary>
    public const int MaxConnectFloodAddresses = 16;
    /// <summary>MAX_GETSTATUSFLOODADDRESSES (server.h).</summary>
    public const int MaxGetStatusFloodAddresses = 128;
    /// <summary>The most text taken from one packet: sizeof(stringbuf) - 1. The rest is cut off.</summary>
    public const int MaxCommandLength = 16383;
    /// <summary>sizeof(response) in NetConn_ServerParsePacket: a status reply, terminator included, fits in this or is not sent.</summary>
    public const int ResponseBufferSize = 2800;
    /// <summary>NET_PROTOCOL_VERSION (netconn.h): the <c>protocol</c> key of a status reply.</summary>
    public const int NetProtocolVersion = 3;

    private const int QcStatusMax = 255;   // sizeof(qcstatus) - 1
    private const int CleanNameMax = 127;  // MAX_SCOREBOARDNAME - 1

    private struct ChallengeSlot
    {
        public TAddress? Address;
        public double Time; // 0 = never used; a slot only matches while this is above 0
    }

    private struct FloodSlot
    {
        public TAddress? Address; // port stripped
        public double LastTime;   // 0 = free
    }

    private readonly ISvConnectionlessHost<TAddress> _host;
    private readonly Func<TAddress, TAddress> _withoutPort;
    private readonly IEqualityComparer<TAddress> _comparer;
    private readonly Random? _random;

    // challenges[]: the strings live side by side in one array, ChallengeLength bytes each.
    private readonly ChallengeSlot[] _challenges = new ChallengeSlot[MaxChallenges];
    private readonly byte[] _challengeStrings = new byte[MaxChallenges * ChallengeLength];
    // sv.connectfloodaddresses / sv.getstatusfloodaddresses
    private readonly FloodSlot[] _connectFlood = new FloodSlot[MaxConnectFloodAddresses];
    private readonly FloodSlot[] _getStatusFlood = new FloodSlot[MaxGetStatusFloodAddresses];

    // Scratch for the status reply, reused so that a query costs one allocation: the reply itself.
    private readonly byte[] _response = new byte[ResponseBufferSize];
    private int _responseLength;
    private readonly byte[] _qcStatus = new byte[QcStatusMax];
    private readonly byte[] _cleanName = new byte[CleanNameMax];
    private readonly List<SvStatusPlayer> _players = new();

    /// <param name="host">The server.</param>
    /// <param name="withoutPort">Maps an address to the same host with no port (LHNETADDRESS_SetPort(..., 0)).
    /// The flood limits are per host, so that changing the source port does not evade them. Null
    /// means the address has no port part.</param>
    /// <param name="comparer">Address equality (LHNETADDRESS_Compare). Null for the type's own.</param>
    /// <param name="random">Source of challenge characters. Null, the default, uses the operating
    /// system's cryptographic generator; a seeded <see cref="Random"/> makes a test repeatable.
    /// (The C uses rand(), which a peer who has seen a few challenges can predict; a challenge is only
    /// worth anything if it cannot be guessed.)</param>
    public SvConnectionless(ISvConnectionlessHost<TAddress> host, Func<TAddress, TAddress>? withoutPort = null,
        IEqualityComparer<TAddress>? comparer = null, Random? random = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _withoutPort = withoutPort ?? (static a => a);
        _comparer = comparer ?? EqualityComparer<TAddress>.Default;
        _random = random;
    }

    /// <summary>net_connectfloodblockingtimeout: after a connect request from a host, further ones are
    /// dropped for this many seconds, and each dropped one restarts the wait. (Retries from an address
    /// that already has a slot are answered before this is consulted.)</summary>
    public double ConnectFloodBlockingTimeout { get; set; } = 5;

    /// <summary>net_challengefloodblockingtimeout: a getchallenge from an address that asked less than
    /// this many seconds ago is dropped. DarkPlaces clients retry once a second, so it must stay below 1.</summary>
    public double ChallengeFloodBlockingTimeout { get; set; } = 0.5;

    /// <summary>net_getstatusfloodblockingtimeout: getinfo and getstatus share one table; a host that
    /// asked less than this many seconds ago is dropped. Unlike connect, a dropped query does not
    /// restart the wait.</summary>
    public double GetStatusFloodBlockingTimeout { get; set; } = 1;

    /// <summary>
    /// sv_public. Only its negative values matter here: at -1 or below getinfo and getstatus go
    /// unanswered, at -2 or below every connect is rejected with <see cref="PublicRejectReason"/>,
    /// at -3 or below getchallenge goes unanswered too. (1 additionally means "advertise on the
    /// master server", which is the heartbeat and not in this class.) The cvar's default is 0.
    /// </summary>
    public int Public { get; set; }

    /// <summary>
    /// "islocal" (netconn.c:3213): true for the address of an endpoint inside this process, which
    /// sv_public does not apply to - the player hosting a closed game can still join it. Only the
    /// owner of the transport can know this; an address is never local because of what it looks like.
    /// </summary>
    public Func<TAddress, bool>? IsLocal { get; set; }

    private int PublicFor(TAddress from) => IsLocal is { } local && local(from) ? 1 : Public;

    /// <summary>sv_public_rejectreason.</summary>
    public string PublicRejectReason { get; set; } = "The server is closing.";

    /// <summary>
    /// DarkPlaces validates the challenge of a connect request only when the request has one: a
    /// request with no <c>challenge</c> key at all skips the check and is accepted (netconn.c:3336,
    /// "if (InfoString_GetValue(...)) { validate }"). That defeats the purpose of the challenge - a
    /// forged source address can occupy a client slot - so this port refuses such a request unless
    /// this is set. No DarkPlaces client ever omits the key.
    /// </summary>
    public bool AllowConnectWithoutChallenge { get; set; }

    /// <summary>
    /// Seconds a challenge stays valid for connect after it was last handed out; 0, the default,
    /// means forever, which is what DarkPlaces does: its table has no expiry, an entry only ends
    /// when it is the oldest of the 128 and a new address needs the slot.
    /// </summary>
    public double ChallengeLifetime { get; set; }

    /// <summary>Challenge slots in use, for diagnostics and tests. Never above <see cref="MaxChallenges"/>.</summary>
    public int ChallengeCount
    {
        get
        {
            int n = 0;
            foreach (ref readonly ChallengeSlot slot in _challenges.AsSpan())
                if (slot.Time > 0)
                    n++;
            return n;
        }
    }

    /// <summary>
    /// NetConn_ServerParsePacket for one datagram. Replies are appended to <paramref name="replies"/>
    /// and go back to <paramref name="from"/>. Never throws on any input.
    /// </summary>
    /// <param name="now">host.realtime. It must be above zero: the tables use 0 for "free slot", as
    /// the C does, so a challenge issued at time 0 would never validate.</param>
    public SvConnectionlessResult Handle(ReadOnlySpan<byte> datagram, TAddress from, double now, List<byte[]> replies)
    {
        if (datagram.Length < 5 || datagram[0] != 255 || datagram[1] != 255 || datagram[2] != 255 || datagram[3] != 255)
            return new SvConnectionlessResult(SvConnectionlessKind.NotConnectionless);

        // "received a command string - strip off the packaging and put it into our string buffer with
        // NULL termination". The C then has two views of it: the bytes (memcmp against a length) and
        // the C string, which ends at the first NUL wherever that is. Both are kept here.
        ReadOnlySpan<byte> data = datagram[4..];
        if (data.Length > MaxCommandLength)
            data = data[..MaxCommandLength];
        int length = data.Length;

        if (length >= 12 && data[..12].SequenceEqual("getchallenge"u8))
        {
            if (PublicFor(from) <= -3)
                return new SvConnectionlessResult(SvConnectionlessKind.Ignored);
            return GetChallenge(from, now, replies);
        }
        if (length > 8 && data[..8].SequenceEqual("connect\\"u8))
            return Connect(CString(data[7..]), from, now, replies);
        if (length >= 7 && data[..7].SequenceEqual("getinfo"u8) && PublicFor(from) > -1)
        {
            if (PreventFlood(_getStatusFlood, from, now, GetStatusFloodBlockingTimeout, renew: false))
                return new SvConnectionlessResult(SvConnectionlessKind.FloodDropped);
            // "If there was a challenge in the getinfo message": everything after the space, as text.
            ReadOnlySpan<byte> challenge = length > 8 && data[7] == ' ' ? CString(data[8..]) : default;
            return Status(challenge, hasChallenge: length > 8 && data[7] == ' ', fullStatus: false, replies);
        }
        if (length >= 9 && data[..9].SequenceEqual("getstatus"u8) && PublicFor(from) > -1)
        {
            if (PreventFlood(_getStatusFlood, from, now, GetStatusFloodBlockingTimeout, renew: false))
                return new SvConnectionlessResult(SvConnectionlessKind.FloodDropped);
            ReadOnlySpan<byte> challenge = length > 10 && data[9] == ' ' ? CString(data[10..]) : default;
            return Status(challenge, hasChallenge: length > 10 && data[9] == ' ', fullStatus: true, replies);
        }
        // (srcon / rcon / extResponse are tested here in the C.)
        if (CString(data).StartsWith("ping"u8))
        {
            replies.Add(Oob("ack"u8));
            return new SvConnectionlessResult(SvConnectionlessKind.Ack);
        }
        // "we may not have liked the packet, but it was a command packet, so we're done processing
        // this packet now"
        return new SvConnectionlessResult(SvConnectionlessKind.Ignored);
    }

    // ---------------------------------------------------------------- getchallenge

    private SvConnectionlessResult GetChallenge(TAddress from, double now, List<byte[]> replies)
    {
        int i, best = 0;
        double bestTime = now;
        for (i = 0; i < MaxChallenges; i++)
        {
            if (_challenges[i].Time > 0 && _comparer.Equals(from, _challenges[i].Address!))
                break;
            if (bestTime > _challenges[i].Time)
            {
                best = i;
                bestTime = _challenges[i].Time;
            }
        }
        if (i == MaxChallenges)
        {
            // if we did not find an exact match, choose the oldest and update address and string.
            // This is the whole of the table's expiry: 128 newer addresses push an entry out. A flood
            // of forged getchallenge packets therefore can evict a real client's challenge before it
            // connects; the client's once-a-second retry is what recovers from that.
            i = best;
            _challenges[i].Address = from;
            BuildChallengeString(_challengeStrings.AsSpan(i * ChallengeLength, ChallengeLength));
        }
        else
        {
            // flood control: drop if requesting challenge too often. An address asking again gets the
            // same string, so a client whose first reply was lost can still use either.
            if (_challenges[i].Time > now - ChallengeFloodBlockingTimeout)
                return new SvConnectionlessResult(SvConnectionlessKind.FloodDropped);
        }
        _challenges[i].Time = now;

        // "challenge %s", sent with its terminating NUL (response_len = strlen(response) + 1). The
        // NUL is where a crypto-capable server appends its key exchange; DarkPlaces sends it even
        // when there is nothing to append, and so does this.
        var reply = new byte[4 + 10 + ChallengeLength + 1];
        reply.AsSpan(0, 4).Fill(255);
        "challenge "u8.CopyTo(reply.AsSpan(4));
        _challengeStrings.AsSpan(i * ChallengeLength, ChallengeLength).CopyTo(reply.AsSpan(14));
        replies.Add(reply);
        return new SvConnectionlessResult(SvConnectionlessKind.Challenge);
    }

    // NetConn_BuildChallengeString: printable ASCII 33..126, minus the characters that would break an
    // info string (\), a console command (; "), a format string (%) or a comment (/).
    private void BuildChallengeString(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            byte c;
            do
            {
                c = (byte)(33 + (_random?.Next(127 - 33) ?? RandomNumberGenerator.GetInt32(127 - 33)));
            } while (c == '\\' || c == ';' || c == '"' || c == '%' || c == '/');
            buffer[i] = c;
        }
    }

    // ---------------------------------------------------------------- connect

    // "string" is the request from its first backslash, cut at the first NUL.
    private SvConnectionlessResult Connect(ReadOnlySpan<byte> info, TAddress from, double now, List<byte[]> replies)
    {
        if (InfoStringGetValue(info, "challenge"u8, out ReadOnlySpan<byte> value))
        {
            // validate the challenge: the string must be the one issued to exactly this address
            int i;
            for (i = 0; i < MaxChallenges; i++)
            {
                if (_challenges[i].Time > 0
                    && _comparer.Equals(from, _challenges[i].Address!)
                    && value.SequenceEqual(_challengeStrings.AsSpan(i * ChallengeLength, ChallengeLength))
                    && (ChallengeLifetime <= 0 || now - _challenges[i].Time <= ChallengeLifetime))
                    break;
            }
            // if the challenge is not recognized, drop the packet (no reject: a reply to a forged
            // address is exactly what must not be sent)
            if (i == MaxChallenges)
                return new SvConnectionlessResult(SvConnectionlessKind.BadChallenge);
        }
        else if (!AllowConnectWithoutChallenge)
            return new SvConnectionlessResult(SvConnectionlessKind.BadChallenge); // deviation: see the property

        // (The C prints the request's optional \message\ key to the developer console here.)

        if (PublicFor(from) <= -2)
            return Reject(PublicRejectReason, replies);

        // check engine protocol
        if (!InfoStringGetValue(info, "protocol"u8, out value) || !value.SequenceEqual("darkplaces 3"u8))
            return Reject("Wrong game protocol.", replies);

        // see if this is a duplicate connection request or a disconnected client who is rejoining to
        // the same client slot
        switch (_host.FindClient(from))
        {
            case SvClientPresence.Begun:
                // client crashed and is coming back, keep their stuff intact
                replies.Add(Oob("accept"u8));
                _host.ReconnectClient(from);
                return new SvConnectionlessResult(SvConnectionlessKind.AcceptedReconnect);
            case SvClientPresence.Connecting:
                // client is still trying to connect, so we send a duplicate reply
                replies.Add(Oob("accept"u8));
                return new SvConnectionlessResult(SvConnectionlessKind.AcceptedDuplicate);
        }

        if (PreventFlood(_connectFlood, from, now, ConnectFloodBlockingTimeout, renew: true))
            return new SvConnectionlessResult(SvConnectionlessKind.FloodDropped);

        // find an empty client slot for this new client. Which slot is the owner's business
        // (net_connect_entnum_ofs, a developer cvar that rotates the search, is not ported).
        string userInfo = Encoding.UTF8.GetString(info);
        if (_host.TryConnectClient(from, userInfo))
        {
            replies.Add(Oob("accept"u8));
            // (NetConn_Heartbeat(1) here: tell the master the player count changed.)
            return new SvConnectionlessResult(SvConnectionlessKind.Accepted, userInfo);
        }

        // no empty slots found - server is full
        return Reject("Server is full.", replies);
    }

    private static SvConnectionlessResult Reject(string reason, List<byte[]> replies)
    {
        // The reason is a C string in the original: it ends at a NUL, and NetConn_WriteString sends
        // no terminator.
        int nul = reason.IndexOf('\0');
        if (nul >= 0)
            reason = reason[..nul];
        int n = Encoding.UTF8.GetByteCount(reason);
        var packet = new byte[4 + 7 + n];
        packet.AsSpan(0, 4).Fill(255);
        "reject "u8.CopyTo(packet.AsSpan(4));
        Encoding.UTF8.GetBytes(reason, packet.AsSpan(11));
        replies.Add(packet);
        return new SvConnectionlessResult(SvConnectionlessKind.Rejected, RejectReason: reason);
    }

    // ---------------------------------------------------------------- flood protection

    // NetConn_PreventFlood. True means "this host is flooding, drop the packet". The table is
    // searched for the host and, in the same pass, for the slot to recycle: the one with the oldest
    // time, the last such when several tie (so free slots fill from the end of the table).
    private bool PreventFlood(FloodSlot[] list, TAddress peer, double now, double floodTime, bool renew)
    {
        // see if this is a connect flood
        TAddress noPort = _withoutPort(peer);
        int best = 0;
        double bestTime = list[0].LastTime;
        for (int slot = 0; slot < list.Length; slot++)
        {
            if (bestTime >= list[slot].LastTime)
            {
                bestTime = list[slot].LastTime;
                best = slot;
            }
            if (list[slot].LastTime != 0 && _comparer.Equals(noPort, list[slot].Address!))
            {
                // this address matches an ongoing flood address
                if (now < list[slot].LastTime + floodTime)
                {
                    // renew the ban on this address so it does not expire until the flood has subsided
                    if (renew)
                        list[slot].LastTime = now;
                    return true;
                }
                // the flood appears to have subsided, so allow this
                best = slot; // reuse the same slot
                break;
            }
        }
        // begin a new timeout on this address
        list[best].Address = noPort;
        list[best].LastTime = now;
        return false;
    }

    /// <summary>
    /// NetConn_ClearFlood on the connect table, which NetConn_Close calls: "allow the client to
    /// reconnect immediately". The owner calls this when it drops a client, or that client's next
    /// connect within <see cref="ConnectFloodBlockingTimeout"/> seconds is silently ignored.
    /// </summary>
    public void ClearConnectFlood(TAddress address)
    {
        TAddress noPort = _withoutPort(address);
        for (int slot = 0; slot < _connectFlood.Length; slot++)
        {
            if (_connectFlood[slot].LastTime != 0 && _comparer.Equals(noPort, _connectFlood[slot].Address!))
            {
                // this address matches an ongoing flood address: remove the ban
                _connectFlood[slot] = default;
            }
        }
    }

    /// <summary>
    /// Empty both flood tables. They are fields of the <c>sv</c> struct, which SV_SpawnServer clears
    /// on every level change; the challenge table is a separate global and survives.
    /// </summary>
    public void ResetFloodTables()
    {
        Array.Clear(_connectFlood);
        Array.Clear(_getStatusFlood);
    }

    // ---------------------------------------------------------------- getinfo / getstatus

    private SvConnectionlessResult Status(ReadOnlySpan<byte> challenge, bool hasChallenge, bool fullStatus, List<byte[]> replies)
    {
        _players.Clear();
        _host.GetStatusPlayers(_players);
        bool full = fullStatus;
        if (!BuildStatusResponse(challenge, hasChallenge, ref full))
            return new SvConnectionlessResult(SvConnectionlessKind.Ignored); // did not fit: no reply at all
        replies.Add(_response.AsSpan(0, _responseLength).ToArray());
        return new SvConnectionlessResult(full ? SvConnectionlessKind.Status : SvConnectionlessKind.Info);
    }

    // NetConn_BuildStatusResponse. "build the full response only if possible; better a getinfo
    // response than no response at all if getstatus won't fit". On return fullStatus says which was built.
    private bool BuildStatusResponse(ReadOnlySpan<byte> challenge, bool hasChallenge, ref bool fullStatus)
    {
        // How many clients are there?
        int clients = _players.Count, bots = 0;
        foreach (SvStatusPlayer p in _players)
            if (p.IsBot)
                bots++;

        // worldstatus with the characters that would end the value or the header line taken out
        int qcLength = Filter(_host.WorldStatus, _qcStatus, StatusFilter.World);

        // The keys and their order are what dpmaster and every server browser parse; the challenge
        // goes last so that it cannot be shadowed by a key the server did not write.
        _response.AsSpan(0, 4).Fill(255);
        _responseLength = 4;
        bool ok = Put(fullStatus ? "statusResponse\n"u8 : "infoResponse\n"u8)
            && Put("\\gamename\\"u8) && Put(_host.GameName)
            && Put("\\modname\\"u8) && Put(_host.ModName)
            && Put("\\gameversion\\"u8) && Put(_host.GameVersion)
            && Put("\\sv_maxclients\\"u8) && Put(_host.MaxClients)
            && Put("\\clients\\"u8) && Put(clients)
            && Put("\\bots\\"u8) && Put(bots)
            && Put("\\mapname\\"u8) && Put(_host.MapName)
            && Put("\\hostname\\"u8) && Put(_host.HostName)
            && Put("\\protocol\\"u8) && Put(NetProtocolVersion);
        if (ok && qcLength > 0)
            ok = Put("\\qcstatus\\"u8) && Put(_qcStatus.AsSpan(0, qcLength));
        // The challenge is echoed byte for byte, as "%s" does. It is the asker's own text coming
        // back to the asker's own address, and one too long to fit costs it the whole reply.
        if (ok && hasChallenge)
            ok = Put("\\challenge\\"u8) && Put(challenge);
        // (\d0_blind_id\ goes here on a server with a key.)
        if (ok && fullStatus)
            ok = Put("\n"u8);
        // Make sure it fits in the buffer
        if (!ok)
            return false;

        if (!fullStatus)
            return true;

        bool teams = _host.TeamPlay > 0; // IS_NEXUIZ_DERIVED(gamemode) is taken as given: this is a Xonotic server
        foreach (SvStatusPlayer p in _players)
        {
            // Remove all characters '"' and '\' in the player name
            int nameLength = Filter(p.Name, _cleanName, StatusFilter.Name);

            int ping = 0;
            if (!p.IsBot)
                ping = Math.Clamp(float.IsFinite(p.Ping) ? (int)(p.Ping * 1000.0f) : 9999, 1, 9999);

            int statusLength = Filter(p.ClientStatus, _qcStatus, StatusFilter.Client);

            // note: team number is inserted according to SoF2 protocol. Xonotic's team colours are
            // fixed, and a spectator is marked by the frags value -666.
            ReadOnlySpan<byte> team = default;
            if (teams)
            {
                team = p.Frags == -666 ? " 0"u8
                    : p.Colors == 0x44 ? " 1"u8  // red
                    : p.Colors == 0xDD ? " 2"u8  // blue
                    : p.Colors == 0xCC ? " 3"u8  // yellow
                    : p.Colors == 0x99 ? " 4"u8  // pink
                    : " 0"u8;
            }

            // "<qcstatus or frags> <ping>[ <team>] "<name>"\n"
            bool lineOk = (statusLength > 0 ? Put(_qcStatus.AsSpan(0, statusLength)) : Put(p.Frags))
                && Put(" "u8) && Put(ping) && Put(team)
                && Put(" \""u8) && Put(_cleanName.AsSpan(0, nameLength)) && Put("\"\n"u8);
            if (!lineOk)
            {
                // out of space? turn it into an infoResponse!
                //
                // Deviation. The C rewrites the header in place and leaves three bytes of debris at
                // the end ("\n", the last character of the header, "\n": its memmove is two bytes
                // short of the terminator it wrote), which lands in the value of the last key - the
                // challenge the asker will compare. What is sent here is the reply getinfo would
                // have produced, which is what that code is trying to make.
                fullStatus = false;
                return BuildStatusResponse(challenge, hasChallenge, ref fullStatus);
            }
        }
        return true;
    }

    private enum StatusFilter { World, Client, Name }

    // Copy text into a fixed buffer as UTF-8, dropping the characters that would break the reply and
    // stopping at a NUL or when the buffer is full (the C buffers are this size and truncate the same
    // way, bytes not characters). Allocates nothing whatever the text is.
    private static int Filter(string? text, Span<byte> destination, StatusFilter filter)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        int n = 0;
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (c == 0)
                break;
            bool skip = filter switch
            {
                StatusFilter.World => c == '\\' || c == '\n',
                StatusFilter.Client => c == '\\' || c == '"' || c == ' ' || c == '\t' || c == '\r' || c == '\n',
                _ => c == '"' || c == '\\',
            };
            if (skip)
                continue;
            int size = rune.EncodeToUtf8(utf8);
            for (int i = 0; i < size; i++)
            {
                if (n == destination.Length)
                    return n;
                destination[n++] = utf8[i];
            }
        }
        return n;
    }

    // dpsnprintf into response[]: fails, as it does, when the text and its terminator do not fit.
    private bool Put(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= ResponseBufferSize - _responseLength)
            return false;
        bytes.CopyTo(_response.AsSpan(_responseLength));
        _responseLength += bytes.Length;
        return true;
    }

    private bool Put(int value)
    {
        Span<byte> digits = stackalloc byte[11];
        return value.TryFormat(digits, out int written) && Put(digits[..written]);
    }

    // A server-side string ("%s"): UTF-8, ending at a NUL, otherwise as it is. A backslash in the
    // hostname would break the info string here exactly as it does in DarkPlaces.
    private bool Put(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value == 0)
                break;
            if (!Put(utf8[..rune.EncodeToUtf8(utf8)]))
                return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Oob(ReadOnlySpan<byte> text)
    {
        var packet = new byte[4 + text.Length];
        packet.AsSpan(0, 4).Fill(255);
        text.CopyTo(packet.AsSpan(4));
        return packet;
    }

    private static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return nul >= 0 ? bytes[..nul] : bytes;
    }

    /// <summary>
    /// InfoString_GetValue over a NUL-free span: find <paramref name="key"/> in <c>\key\value\key\value</c>.
    /// True when the key is there with a value that is not empty (the C returns the value's length and
    /// every caller tests it for zero). The value is a slice of <paramref name="buffer"/>, at most
    /// MAX_INPUTLINE - 1 bytes of it.
    /// </summary>
    private static bool InfoStringGetValue(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        const int valueSize = 16384; // sizeof(infostringvalue) = MAX_INPUTLINE
        value = default;
        if (key.IsEmpty)
            return false;
        int pos = 0;
        while (At(buffer, pos) == '\\')
        {
            int afterKey = pos + 1 + key.Length;
            if (afterKey <= buffer.Length && buffer.Slice(pos + 1, key.Length).SequenceEqual(key)
                && (At(buffer, afterKey) == 0 || At(buffer, afterKey) == '\\'))
            {
                pos = afterKey;                         // Skip \key
                if (At(buffer, pos) == '\\') pos++;     // Skip \ before value.
                int j = 0;
                while (At(buffer, pos + j) != 0 && At(buffer, pos + j) != '\\' && j < valueSize - 1)
                    j++;
                value = buffer.Slice(pos, j);
                return j > 0;
            }
            if (At(buffer, pos) == '\\') pos++;         // Skip \ before key.
            while (At(buffer, pos) != 0 && At(buffer, pos) != '\\') pos++;
            if (At(buffer, pos) == '\\') pos++;         // Skip \ before value.
            while (At(buffer, pos) != 0 && At(buffer, pos) != '\\') pos++;
        }
        // if we reach this point the key was not found
        return false;
    }

    // The byte at an index of a C string: the terminator at and past its end.
    private static byte At(ReadOnlySpan<byte> s, int i) => (uint)i < (uint)s.Length ? s[i] : (byte)0;
}
