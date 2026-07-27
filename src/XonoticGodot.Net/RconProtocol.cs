using System;
using System.Collections.Generic;
using System.Text;

namespace XonoticGodot.Net;

/// <summary>The three rcon request flavours DarkPlaces accepts (netconn.c NetConn_ServerParsePacket), plus the
/// challenge request that precedes a <see cref="Challenge"/> command. Frozen wire grammar — an interop boundary.</summary>
public enum RconKind
{
    None,
    Insecure,     // "rcon <password> <command>"                                   (rcon_secure 0)
    Time,         // "srcon HMAC-MD4 TIME <16-hmac> <time> <command>"              (rcon_secure 1)
    Challenge,    // "srcon HMAC-MD4 CHALLENGE <16-hmac> <challenge> <command>"    (rcon_secure 2)
    GetChallenge, // "getchallenge" — client asks for a challenge to hash against  (precedes Challenge)
}

/// <summary>A parsed rcon request. The HMAC/time/challenge fields are populated per <see cref="Kind"/>.</summary>
public readonly struct RconRequest
{
    public RconKind Kind { get; init; }
    public string Command { get; init; }        // the command to run (Insecure/Time/Challenge)
    public string Password { get; init; }       // the plaintext password (Insecure only)
    public byte[]? Hmac { get; init; }           // the 16-byte HMAC-MD4 (Time/Challenge)
    public string Time { get; init; }            // the client's unix-time string (Time)
    public string ChallengeToken { get; init; }  // the echoed challenge (Challenge)
    public string HmacMessage { get; init; }     // exactly the bytes HMAC'd: "<time-or-challenge> <command>"
}

/// <summary>
/// The DarkPlaces <c>rcon</c>/<c>srcon</c> connectionless wire codec (DS-6) — byte-compatible with
/// darkplaces/netconn.c so stock rcon tooling and our own Launcher.GameControl speak the same protocol. Pure
/// (encode/decode/verify only); the stateful challenge + replay + rate-limit bookkeeping lives in the server
/// wiring. All packets are the standard OOB form: the caller's transport prepends/strips the four <c>0xFF</c>
/// header (<see cref="MasterServerProtocol"/> owns that), so this operates on the payload AFTER the header.
///
/// The secure variants embed a RAW 16-byte HMAC-MD4 mid-packet (not hex), so the payload is binary: we match the
/// ASCII prefix + copy the 16 HMAC bytes + decode the trailing <c>"&lt;time-or-challenge&gt; &lt;command&gt;"</c> as
/// ASCII. The HMAC is keyed by the server's <c>rcon_password</c> over exactly that trailing string (DP's
/// <c>hmac_mdfour_time_matching</c>/<c>_challenge_matching</c>). Response is <c>\xFF\xFF\xFF\xFFn&lt;output&gt;</c>
/// (DP's "QW rcon print").
/// </summary>
public static class RconProtocol
{
    private static readonly byte[] Oob = { 0xFF, 0xFF, 0xFF, 0xFF };
    private const string TimePrefix = "srcon HMAC-MD4 TIME ";           // 20 bytes
    private const string ChallengePrefix = "srcon HMAC-MD4 CHALLENGE "; // 25 bytes
    private const string InsecurePrefix = "rcon ";                       // 5 bytes
    private const int HmacLen = 16;

    // ---- server-side: parse an incoming OOB payload (header already stripped) ----

    /// <summary>
    /// Parse the payload of a connectionless packet into an <see cref="RconRequest"/>. Returns false (Kind=None)
    /// for anything that isn't an rcon/srcon/getchallenge packet or is malformed. Does NOT verify the HMAC or
    /// password — that's <see cref="VerifyTime"/>/<see cref="VerifyChallenge"/> and the caller's password compare.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out RconRequest req)
    {
        req = default;

        if (StartsWithAscii(payload, "getchallenge"))
        {
            req = new RconRequest { Kind = RconKind.GetChallenge };
            return true;
        }

        if (StartsWithAscii(payload, TimePrefix))
            return TryParseSecure(payload, TimePrefix.Length, RconKind.Time, out req);

        if (StartsWithAscii(payload, ChallengePrefix))
            return TryParseSecure(payload, ChallengePrefix.Length, RconKind.Challenge, out req);

        if (StartsWithAscii(payload, InsecurePrefix))
        {
            // "rcon <password> <command>": password up to the first space, command is the rest.
            string rest = Encoding.UTF8.GetString(payload.Slice(InsecurePrefix.Length));
            int sp = rest.IndexOf(' ');
            if (sp <= 0 || sp + 1 >= rest.Length)
                return false;
            req = new RconRequest
            {
                Kind = RconKind.Insecure,
                Password = rest.Substring(0, sp),
                Command = rest.Substring(sp + 1),
            };
            return true;
        }

        return false;
    }

    // "<prefix><16-byte HMAC><space><value> <command>" where value = time (Time) or challenge (Challenge),
    // and the HMAC covers exactly "<value> <command>" (the ASCII tail). prefixLen is 20 (TIME) or 25 (CHALLENGE).
    private static bool TryParseSecure(ReadOnlySpan<byte> payload, int prefixLen, RconKind kind, out RconRequest req)
    {
        req = default;
        // prefix + 16 HMAC bytes + 1 space + at least "v c" (value, space, command)
        if (payload.Length < prefixLen + HmacLen + 1 + 3)
            return false;
        if (payload[prefixLen + HmacLen] != (byte)' ')
            return false;

        byte[] hmac = payload.Slice(prefixLen, HmacLen).ToArray();
        string tail = Encoding.UTF8.GetString(payload.Slice(prefixLen + HmacLen + 1)); // "<value> <command>"
        int sp = tail.IndexOf(' ');
        if (sp <= 0 || sp + 1 >= tail.Length)
            return false;

        string value = tail.Substring(0, sp);
        string command = tail.Substring(sp + 1);
        req = new RconRequest
        {
            Kind = kind,
            Hmac = hmac,
            Command = command,
            HmacMessage = tail,                                  // the exact bytes DP hashes
            Time = kind == RconKind.Time ? value : "",
            ChallengeToken = kind == RconKind.Challenge ? value : "",
        };
        return true;
    }

    // ---- verification (server-side) ----

    /// <summary>Verify a TIME request's HMAC and freshness (DP <c>hmac_mdfour_time_matching</c>): the packet time
    /// must be within <paramref name="maxDiffSeconds"/> of the server's unix time, and HMAC-MD4(password, message)
    /// must equal the packet HMAC. Uses a constant-time digest compare.</summary>
    public static bool VerifyTime(string password, in RconRequest req, long serverUnixTime, int maxDiffSeconds)
    {
        if (req.Kind != RconKind.Time || req.Hmac is null || string.IsNullOrEmpty(password))
            return false;
        if (!long.TryParse(req.Time, out long clientTime))
            return false;
        if (Math.Abs(serverUnixTime - clientTime) > maxDiffSeconds)
            return false;
        byte[] expect = Md4.Hmac(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(req.HmacMessage));
        return FixedTimeEquals(expect, req.Hmac);
    }

    /// <summary>Verify a CHALLENGE request's HMAC (DP <c>hmac_mdfour_challenge_matching</c>). The caller must
    /// SEPARATELY confirm the challenge token was one this server issued (and consume it) — that state isn't
    /// here. Constant-time digest compare.</summary>
    public static bool VerifyChallenge(string password, in RconRequest req)
    {
        if (req.Kind != RconKind.Challenge || req.Hmac is null || string.IsNullOrEmpty(password))
            return false;
        byte[] expect = Md4.Hmac(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(req.HmacMessage));
        return FixedTimeEquals(expect, req.Hmac);
    }

    /// <summary>
    /// DP's post-auth exploit filter (RCon_Execute's <c>check:</c> loop): reject a command containing a control
    /// character (a positive byte below space) or a <c>;</c> — both can chain/inject commands past the parser.
    /// A command that fails this must NOT run even with a valid password.
    /// </summary>
    public static bool IsCommandSafe(string command)
    {
        foreach (char ch in command)
        {
            if (ch > 0 && ch < ' ')
                return false;
            if (ch == ';')
                return false;
        }
        return true;
    }

    // ---- encoding: responses + challenge reply (server) and requests (client / tests / Launcher.GameControl) ----

    /// <summary>
    /// Build the rcon reply packet: <c>\xFF\xFF\xFF\xFFn</c> + the console output text (DP "QW rcon print").
    /// TRUNCATED to one safe datagram — see <see cref="MaxResponseTextBytes"/>. Use
    /// <see cref="BuildResponseChunks"/> to send long output as several readable packets.
    /// </summary>
    public static byte[] BuildResponse(string outputText)
    {
        byte[] body = Encoding.UTF8.GetBytes(outputText ?? "");
        if (body.Length > MaxResponseTextBytes)
        {
            const string note = "\n[output truncated]\n";
            byte[] noteBytes = Encoding.UTF8.GetBytes(note);
            var clipped = new byte[MaxResponseTextBytes];
            Array.Copy(body, clipped, MaxResponseTextBytes - noteBytes.Length);
            Array.Copy(noteBytes, 0, clipped, MaxResponseTextBytes - noteBytes.Length, noteBytes.Length);
            body = clipped;
        }
        return Concat(Oob, new[] { (byte)'n' }, body);
    }

    /// <summary>
    /// The largest reply body we put in one datagram. DP chunks rcon output through Con_Rcon_Redirect at about
    /// this size; anything past the path MTU is silently lost, and a body over the ~65507-byte UDP limit makes
    /// the send THROW — which, on the master-server pump, propagated out of _Process and took the server's
    /// frame loop down on any verbose authenticated command (`status` on a full server, `cvarlist`).
    /// </summary>
    public const int MaxResponseTextBytes = 1200;

    /// <summary>Split long console output into MTU-sized reply packets, in order (DP's rcon redirect chunking).
    /// Always returns at least one packet so a caller can send unconditionally.</summary>
    public static IReadOnlyList<byte[]> BuildResponseChunks(string outputText)
    {
        var packets = new List<byte[]>();
        byte[] body = Encoding.UTF8.GetBytes(outputText ?? "");
        if (body.Length == 0)
        {
            packets.Add(Concat(Oob, new[] { (byte)'n' }));
            return packets;
        }
        for (int off = 0; off < body.Length; off += MaxResponseTextBytes)
        {
            int len = Math.Min(MaxResponseTextBytes, body.Length - off);
            var slice = new byte[len];
            Array.Copy(body, off, slice, 0, len);
            packets.Add(Concat(Oob, new[] { (byte)'n' }, slice));
        }
        return packets;
    }

    /// <summary>Build the reply to a <c>getchallenge</c>: <c>\xFF\xFF\xFF\xFFchallenge </c> + the token.</summary>
    public static byte[] BuildChallengeReply(string challenge)
        => Concat(Oob, Encoding.UTF8.GetBytes("challenge " + challenge));

    /// <summary>Client: build an insecure <c>rcon</c> request. Localhost-only on the server side by policy.</summary>
    public static byte[] BuildInsecureRequest(string password, string command)
        => Concat(Oob, Encoding.UTF8.GetBytes($"rcon {password} {command}"));

    /// <summary>Client: build a TIME <c>srcon</c> request. <paramref name="unixTime"/> is the client's clock.</summary>
    public static byte[] BuildTimeRequest(string password, long unixTime, string command)
    {
        string message = $"{unixTime} {command}";                        // exactly what gets HMAC'd
        byte[] hmac = Md4.Hmac(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(message));
        return Concat(Oob, Encoding.UTF8.GetBytes(TimePrefix), hmac, new[] { (byte)' ' }, Encoding.UTF8.GetBytes(message));
    }

    /// <summary>Client: build a CHALLENGE <c>srcon</c> request from a challenge the server issued.</summary>
    public static byte[] BuildChallengeRequest(string password, string challenge, string command)
    {
        string message = $"{challenge} {command}";
        byte[] hmac = Md4.Hmac(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(message));
        return Concat(Oob, Encoding.UTF8.GetBytes(ChallengePrefix), hmac, new[] { (byte)' ' }, Encoding.UTF8.GetBytes(message));
    }

    /// <summary>Client: build a <c>getchallenge</c> request.</summary>
    public static byte[] BuildGetChallenge() => Concat(Oob, Encoding.UTF8.GetBytes("getchallenge"));

    // ---- helpers ----

    private static bool StartsWithAscii(ReadOnlySpan<byte> payload, string ascii)
    {
        if (payload.Length < ascii.Length)
            return false;
        for (int i = 0; i < ascii.Length; i++)
            if (payload[i] != (byte)ascii[i])
                return false;
        return true;
    }

    /// <summary>Constant-time 16-byte compare (no early-out on the first differing byte — don't leak the HMAC).</summary>
    private static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);

    private static byte[] Concat(params byte[][] parts)
    {
        int n = 0;
        foreach (var p in parts) n += p.Length;
        var outp = new byte[n];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(outp, o); o += p.Length; }
        return outp;
    }
}
