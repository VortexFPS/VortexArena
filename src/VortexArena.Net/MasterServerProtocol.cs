using System.Collections.Generic;
using System.Net;
using System.Text;

namespace XonoticGodot.Net;

/// <summary>
/// The Darkplaces/Quake3 <em>connectionless</em> (out-of-band, "OOB") query protocol: the small ASCII
/// UDP datagrams the engine exchanges with the dpmaster master server (server browser + heartbeat) and
/// directly with game servers (info queries). Port of the relevant pieces of DP <c>netconn.c</c>
/// (<c>NetConn_Heartbeat</c>, the <c>getservers</c>/<c>getserversResponse</c> handling in
/// <c>NetConn_QueryMasters</c>/<c>NetConn_ClientParsePacket_ServerList_ParseDPList</c>, and
/// <c>getinfo</c>/<c>infoResponse</c>) plus the <c>\key\value</c> infostring helpers from
/// <c>com_infostring.c</c>.
///
/// Every OOB datagram begins with four <c>0xFF</c> bytes (the connectionless marker, distinguishing it
/// from an in-band netchan packet whose first bytes are a sequence number) followed by an ASCII command
/// token. This class is pure byte encode/decode + parsing: it owns no socket. The UDP send/receive is
/// wired separately in the Godot host (ADR-0005), which hands raw datagrams to <see cref="ParseGetServersResponse"/>
/// / <see cref="ParseInfoResponse"/> and transmits the byte[] from the <c>Encode*</c> methods.
///
/// Unlike the in-band wire format (which we own, little-endian — see <see cref="BitWriter"/>), this
/// protocol is an interop boundary: the byte layout is dictated by dpmaster and existing DP servers, so
/// it is reproduced exactly — ASCII text, and big-endian (network order) IPv4 addresses and ports in
/// <c>getserversResponse</c>.
/// </summary>
public static class MasterServerProtocol
{
    /// <summary>
    /// The connectionless packet marker as a 32-bit value: all four bytes <c>0xFF</c>. DP writes this as
    /// the literal byte sequence <c>\377\377\377\377</c>; exposed here as a constant for callers that want
    /// to compare/emit it as an integer. The on-the-wire bytes are <see cref="OobHeader"/>.
    /// </summary>
    public const uint OobPrefix = 0xFFFFFFFFu;

    /// <summary>The four <c>0xFF</c> header bytes that prefix every connectionless datagram.</summary>
    public static ReadOnlySpan<byte> OobHeader => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

    // The byte that separates infostring tokens and prefixes every server entry in getserversResponse.
    private const byte Backslash = (byte)'\\';

    // ASCII encoding without a BOM and (by default) throwing on unmappable chars would be ideal, but the
    // shared Encoding.ASCII replaces non-ASCII with '?'. Command/infostring text is ASCII by construction
    // (dpmaster's grammar), so the default is fine and matches DP, which is byte-oriented C strings.
    private static readonly Encoding Ascii = Encoding.ASCII;

    // =====================================================================================
    // Server -> master: heartbeat
    // =====================================================================================

    /// <summary>
    /// Build the DP server heartbeat keepalive: <c>\xFF\xFF\xFF\xFF</c> + <c>"heartbeat DarkPlaces\x0A"</c>.
    /// A public server sends this to each master periodically (DP <c>sv_heartbeatperiod</c>, 30..270s) so the
    /// master keeps it listed; the master replies by querying the server's info. Port of the
    /// <c>NetConn_WriteString(mysocket, "\377\377\377\377heartbeat DarkPlaces\x0A", ...)</c> call in
    /// <c>NetConn_Heartbeat</c>.
    ///
    /// The protocol string is fixed (<c>DarkPlaces</c>) — dpmaster keys games off the per-server <c>gamename</c>
    /// infostring reported in the subsequent <c>infoResponse</c>, not off the heartbeat line — so
    /// <paramref name="gameName"/> is accepted only for API symmetry with the other encoders and does not
    /// alter the bytes. The trailing <c>\x0A</c> (LF) is part of the literal DP sends.
    /// </summary>
    /// <param name="gameName">Unused by the wire format (see remarks); present for call-site symmetry.</param>
    public static byte[] EncodeHeartbeat(string gameName = "Xonotic")
    {
        _ = gameName; // intentionally not encoded; the DP heartbeat literal is game-independent
        return BuildOob("heartbeat DarkPlaces\x0A");
    }

    // =====================================================================================
    // Client -> master: getservers
    // =====================================================================================

    /// <summary>
    /// Build a DP master query: <c>\xFF\xFF\xFF\xFF</c> + <c>"getservers &lt;gameName&gt; &lt;protocol&gt; empty full"</c>.
    /// Asks dpmaster for the list of servers for game <paramref name="gameName"/> speaking protocol version
    /// <paramref name="protocol"/> (e.g. Xonotic uses DP protocol 3). The <c>empty</c>/<c>full</c> filter
    /// words, appended when the corresponding flag is set, tell the master to <em>include</em> empty and/or
    /// full servers respectively (DP requests both: <c>"%s %s %u empty full"</c> in <c>NetConn_QueryMasters</c>).
    /// Omitting a flag asks the master to exclude that category.
    /// </summary>
    /// <param name="gameName">dpmaster game filter, e.g. <c>"Xonotic"</c>.</param>
    /// <param name="protocol">DP network protocol version to match.</param>
    /// <param name="empty">When true, append <c>empty</c> so empty servers are included.</param>
    /// <param name="full">When true, append <c>full</c> so full servers are included.</param>
    public static byte[] EncodeGetServers(string gameName, int protocol, bool empty = true, bool full = true)
    {
        var sb = new StringBuilder("getservers ");
        sb.Append(gameName).Append(' ').Append(protocol);
        if (empty) sb.Append(" empty");
        if (full) sb.Append(" full");
        return BuildOob(sb.ToString());
    }

    /// <summary>
    /// Parse a master's <c>getserversResponse</c> packet into its packed (ip, port) list. The payload after the
    /// OOB header is the token <c>getserversResponse</c> immediately followed by a run of entries, each of which
    /// is the literal byte <c>\</c> then six bytes: a 4-byte IPv4 address and a 2-byte port, both big-endian
    /// (network order). The run ends at the <c>\EOT\0\0\0</c> terminator (a <c>\</c> entry whose "address" is
    /// the ASCII bytes <c>E O T \0</c> and whose port is <c>0</c>). Port of the IPv4 branch of DP's
    /// <c>NetConn_ClientParsePacket_ServerList_ParseDPList</c>.
    ///
    /// Defensive about truncation: stops cleanly if fewer than 7 bytes remain, on a missing leading <c>\</c>,
    /// or at end of buffer with no terminator (a partial/garbled datagram yields the entries decoded so far
    /// rather than throwing). Mirroring DP, an entry whose address is all-<c>0xFF</c> or whose port is <c>0</c>
    /// (a broadcast/placeholder guard) is skipped without being emitted. IPv6 (<c>getserversExtResponse</c>,
    /// <c>/</c>-prefixed 18-byte entries) is out of scope here.
    /// </summary>
    /// <param name="packet">The full received datagram, including the four <c>0xFF</c> header bytes.</param>
    /// <returns>The decoded server endpoints, in packet order. Empty if the packet is not a valid response.</returns>
    public static IReadOnlyList<(IPAddress ip, int port)> ParseGetServersResponse(ReadOnlySpan<byte> packet)
    {
        var result = new List<(IPAddress, int)>();

        if (!TryStripOob(packet, out ReadOnlySpan<byte> body))
            return result;

        const string token = "getserversResponse";
        if (!StartsWithAscii(body, token))
            return result;
        // Skip the token. DP advances past "getserversResponse" (18 chars) and leaves the cursor on the
        // first entry's leading backslash, which is the 19th char it matched ("getserversResponse\").
        ReadOnlySpan<byte> data = body.Slice(token.Length);

        // Each IPv4 record is '\' + 4 (ip) + 2 (port) = 7 bytes. The EOT marker has the same 7-byte shape.
        while (data.Length >= 7)
        {
            if (data[0] != Backslash)
                break; // not a well-formed entry (and not '/': IPv6 is unsupported here) — stop, like DP.

            // bytes [1..4] = IPv4, [5..6] = port (big-endian)
            byte b0 = data[1], b1 = data[2], b2 = data[3], b3 = data[4];
            int port = (data[5] << 8) | data[6];

            // End Of Transmission terminator: \EOT\0 with a zero port.
            if (port == 0 && b0 == (byte)'E' && b1 == (byte)'O' && b2 == (byte)'T' && b3 == 0)
                break;

            // Skip broadcast/placeholder guards exactly as DP does (port 0, or all-0xFF address), but keep scanning.
            bool allFf = b0 == 0xFF && b1 == 0xFF && b2 == 0xFF && b3 == 0xFF;
            if (port != 0 && !allFf)
            {
                var ip = new IPAddress(new[] { b0, b1, b2, b3 });
                result.Add((ip, port));
            }

            data = data.Slice(7);
        }

        return result;
    }

    // =====================================================================================
    // Client -> server: getinfo
    // =====================================================================================

    /// <summary>
    /// Build a direct server info query: <c>\xFF\xFF\xFF\xFF</c> + <c>"getinfo &lt;challenge&gt;"</c>. Sent to a
    /// specific game server (e.g. one returned by a <c>getserversResponse</c>) to retrieve its
    /// <see cref="EncodeInfoResponse">infoResponse</see>. The <paramref name="challenge"/> string is echoed back
    /// by the server in the response's <c>challenge</c> infostring key, letting the client match the reply to the
    /// request and measure ping. When empty, only <c>"getinfo"</c> is sent (DP accepts a challenge-less getinfo).
    /// </summary>
    public static byte[] EncodeGetInfo(string challenge)
    {
        string cmd = string.IsNullOrEmpty(challenge) ? "getinfo" : "getinfo " + challenge;
        return BuildOob(cmd);
    }

    // =====================================================================================
    // Server -> client: infoResponse
    // =====================================================================================

    /// <summary>
    /// Build a server <c>infoResponse</c>: <c>\xFF\xFF\xFF\xFF</c> + <c>"infoResponse\x0A"</c> + an
    /// <see cref="EncodeInfostring">infostring</see> of the supplied key/value pairs. The reply to a
    /// <see cref="EncodeGetInfo">getinfo</see>; carries the compact server description the browser shows
    /// (<c>gamename</c>, <c>hostname</c>, <c>mapname</c>, <c>protocol</c>, <c>clients</c>, <c>sv_maxclients</c>,
    /// the echoed <c>challenge</c>, …). The <c>\x0A</c> (LF) after the token is part of the DP literal
    /// (<c>infoResponse\x0A</c> is the 13-byte prefix DP matches on).
    /// </summary>
    public static byte[] EncodeInfoResponse(IReadOnlyDictionary<string, string> info)
    {
        string body = "infoResponse\x0A" + EncodeInfostring(info);
        return BuildOob(body);
    }

    /// <summary>
    /// Parse a server <c>infoResponse</c> packet into its key/value dictionary: strips the OOB header and the
    /// <c>"infoResponse\x0A"</c> token, then decodes the trailing <see cref="ParseInfostring">infostring</see>.
    /// Port of the <c>string += 13; InfoString_GetValue(string, …)</c> path in DP's client packet handler,
    /// returning the whole parsed map rather than pulling individual keys. Returns an empty dictionary if the
    /// packet is not a well-formed infoResponse.
    /// </summary>
    /// <param name="packet">The full received datagram, including the four <c>0xFF</c> header bytes.</param>
    public static IReadOnlyDictionary<string, string> ParseInfoResponse(ReadOnlySpan<byte> packet)
    {
        if (!TryStripOob(packet, out ReadOnlySpan<byte> body))
            return new Dictionary<string, string>();

        const string token = "infoResponse\x0A";
        if (!StartsWithAscii(body, token))
            return new Dictionary<string, string>();

        // The infostring VALUES are byte-oriented in DP and, on real Xonotic servers, UTF-8 (decorated
        // hostnames). The protocol grammar itself is ASCII (a UTF-8 subset), so decoding the whole
        // infostring as UTF-8 is loss-free for ASCII and renders the unicode hostnames correctly
        // (Encoding.ASCII would flatten every non-ASCII byte to '?').
        ReadOnlySpan<byte> infoBytes = body.Slice(token.Length);
        return ParseInfostring(Encoding.UTF8.GetString(infoBytes));
    }

    // =====================================================================================
    // Infostring helpers: the DP \key\value format (com_infostring.c)
    // =====================================================================================

    /// <summary>
    /// Encode a key/value map as a DP infostring: <c>\key\value\key\value…</c> (a leading backslash, then each
    /// key and value separated by backslashes). The serialization counterpart to <see cref="ParseInfostring"/>
    /// and the format produced by <c>InfoString_SetValue</c>. An empty map encodes to the empty string.
    ///
    /// Backslash is the delimiter and therefore cannot appear inside a key or value (DP's
    /// <c>InfoString_SetValue</c> refuses such inputs); to stay faithful and avoid emitting an unparseable
    /// string, any backslash in a key/value is dropped here. Enumeration order is the dictionary's own order.
    /// </summary>
    public static string EncodeInfostring(IReadOnlyDictionary<string, string> info)
    {
        if (info is null || info.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (KeyValuePair<string, string> kv in info)
        {
            sb.Append('\\').Append(Sanitize(kv.Key));
            sb.Append('\\').Append(Sanitize(kv.Value));
        }
        return sb.ToString();

        static string Sanitize(string? s) =>
            string.IsNullOrEmpty(s) ? string.Empty
            : s.IndexOf('\\') < 0 ? s
            : s.Replace("\\", string.Empty);
    }

    /// <summary>
    /// Parse a DP infostring (<c>\key\value\key\value…</c>) into a dictionary. Tokens are split on the backslash
    /// delimiter; the first token before the leading <c>\</c> (normally empty) is ignored. A trailing key with no
    /// value maps to the empty string. On a duplicate key, the <em>first</em> occurrence wins — matching
    /// <c>InfoString_GetValue</c>, which returns the first match found scanning left to right.
    /// </summary>
    public static Dictionary<string, string> ParseInfostring(string infostring)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(infostring))
            return dict;

        // Split on '\'. A leading '\' yields an empty first element which we skip; pairs follow as key,value.
        string[] parts = infostring.Split('\\');
        // parts[0] is the (usually empty) text before the first backslash.
        for (int i = 1; i + 1 < parts.Length; i += 2)
        {
            string key = parts[i];
            string value = parts[i + 1];
            if (key.Length == 0)
                continue; // ignore empty keys (e.g. from a stray "\\")
            if (!dict.ContainsKey(key))
                dict[key] = value;
        }
        // Odd trailing token: a key with no value (DP would read it as an empty value).
        if (parts.Length >= 2 && (parts.Length % 2) == 0)
        {
            string key = parts[parts.Length - 1];
            if (key.Length != 0 && !dict.ContainsKey(key))
                dict[key] = string.Empty;
        }
        return dict;
    }

    // =====================================================================================
    // OOB header helpers
    // =====================================================================================

    /// <summary>
    /// Test for the four <c>0xFF</c> connectionless header and, if present, hand back the remaining bytes
    /// (the command + payload) in <paramref name="body"/>. The gate every OOB parser runs first: an in-band
    /// netchan packet (whose leading bytes are a sequence number) fails this and is rejected. Returns false
    /// and an empty body for any packet shorter than four bytes or without the marker.
    /// </summary>
    public static bool TryStripOob(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> body)
    {
        if (packet.Length >= 4 &&
            packet[0] == 0xFF && packet[1] == 0xFF && packet[2] == 0xFF && packet[3] == 0xFF)
        {
            body = packet.Slice(4);
            return true;
        }
        body = default;
        return false;
    }

    // =====================================================================================
    // internals
    // =====================================================================================

    /// <summary>Compose an OOB datagram: the four <c>0xFF</c> header bytes followed by <paramref name="command"/>
    /// encoded as ASCII.</summary>
    private static byte[] BuildOob(string command)
    {
        int n = Ascii.GetByteCount(command);
        var buf = new byte[4 + n];
        buf[0] = buf[1] = buf[2] = buf[3] = 0xFF;
        Ascii.GetBytes(command, 0, command.Length, buf, 4);
        return buf;
    }

    /// <summary>True if <paramref name="body"/> begins with the ASCII bytes of <paramref name="token"/>.</summary>
    private static bool StartsWithAscii(ReadOnlySpan<byte> body, string token)
    {
        if (body.Length < token.Length)
            return false;
        for (int i = 0; i < token.Length; i++)
        {
            if (body[i] != (byte)token[i])
                return false;
        }
        return true;
    }
}
