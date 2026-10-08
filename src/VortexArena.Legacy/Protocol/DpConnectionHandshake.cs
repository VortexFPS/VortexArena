// Port of Base/darkplaces/netconn.c NetConn_ClientFrame (the connect retry, lines 2697-2719),
// NetConn_ClientParsePacket (challenge/accept/reject/ping, lines 2146-2187 and 2304-2312) and
// cl_main.c CL_EstablishConnection (the retry budget).
using System.Text;
using VortexArena.Net;

namespace VortexArena.Legacy.Protocol;

public enum DpHandshakeState
{
    /// <summary>Not started.</summary>
    Idle,
    /// <summary>Sending getchallenge once a second and waiting for the server.</summary>
    Connecting,
    /// <summary>The server answered <c>accept</c>; the netchan may start.</summary>
    Accepted,
    /// <summary>The server answered <c>reject</c>; <see cref="DpConnectionHandshake.RejectReason"/> has its text.</summary>
    Rejected,
    /// <summary>Every attempt went unanswered ("Connect: failed, no reply").</summary>
    TimedOut,
}

/// <summary>
/// The connectionless exchange that precedes a DarkPlaces game connection. All of it is the
/// out-of-band packet layer <see cref="MasterServerProtocol"/> already implements: four 0xFF bytes and
/// an ASCII command.
///
/// <code>
/// client: getchallenge
/// server: challenge &lt;token&gt;            (a crypto-capable server appends NUL and a binary blob)
/// client: connect\protocol\darkplaces 3\protocols\DP7\challenge\&lt;token&gt;
/// server: accept                         or: reject &lt;reason&gt;
/// </code>
///
/// Encryption and identity (d0_blind_id, the <c>d0pk\</c> packets of crypto.c) are not implemented.
/// A stock server runs with crypto_aeslevel 1, which makes them optional. One configured to require
/// them answers with a reject, and that reason is surfaced like any other.
/// </summary>
public sealed class DpConnectionHandshake
{
    private readonly string _userInfo;
    private readonly int _maxTries;
    private int _remainingTries;
    private double _nextSendTime;

    /// <param name="userInfo">Extra <c>\key\value</c> pairs for the connect request
    /// (cls.connect_userinfo). Empty for a normal connect; names and colours are sent later, in band.</param>
    /// <param name="maxTries">How many getchallenge requests to send before giving up. DarkPlaces uses 10.</param>
    public DpConnectionHandshake(string userInfo = "", int maxTries = 10)
    {
        _userInfo = userInfo ?? "";
        _maxTries = Math.Max(1, maxTries);
    }

    /// <summary>Seconds between getchallenge requests (cls.connect_nextsendtime = host.realtime + 1).</summary>
    public const double RetryInterval = 1.0;

    /// <summary>The longest challenge token echoed back. DarkPlaces builds the connect request in a
    /// 1400-byte buffer; a token that would not fit is not a token.</summary>
    public const int MaxChallengeLength = 1024;

    public DpHandshakeState State { get; private set; } = DpHandshakeState.Idle;
    public string Challenge { get; private set; } = "";
    public string RejectReason { get; private set; } = "";
    public int RemainingTries => _remainingTries;

    /// <summary>CL_EstablishConnection: arm the retry budget. The first request goes out on the next <see cref="Update"/>.</summary>
    public void Start(double now)
    {
        State = DpHandshakeState.Connecting;
        _remainingTries = _maxTries;
        // DarkPlaces sets nextsendtime to 0 so the first frame sends; "one tick ago" says the same
        // thing without assuming the caller's clock starts above zero.
        _nextSendTime = now - 1;
        Challenge = "";
        RejectReason = "";
    }

    /// <summary>The retry half of NetConn_ClientFrame: send another getchallenge when one is due.</summary>
    public void Update(double now, List<byte[]> outgoing)
    {
        if (State != DpHandshakeState.Connecting || !(_nextSendTime < now))
            return;
        if (_remainingTries <= 0)
        {
            State = DpHandshakeState.TimedOut;
            return;
        }
        _remainingTries--;
        _nextSendTime = now + RetryInterval;
        // DarkPlaces also sends a NetQuake CCREQ_CONNECT control packet here as a fallback for servers
        // that predate getchallenge. A Xonotic server never needs it, so it is not sent.
        outgoing.Add(RconProtocol.BuildGetChallenge());
    }

    /// <summary>
    /// Handle one datagram if it is connectionless. Returns true when it was (whether or not it meant
    /// anything), false when it is an in-band datagram for the netchan.
    /// </summary>
    public bool Receive(ReadOnlySpan<byte> datagram, double now, List<byte[]> outgoing)
    {
        if (!MasterServerProtocol.TryStripOob(datagram, out ReadOnlySpan<byte> body))
            return false;

        bool connecting = State == DpHandshakeState.Connecting;

        if (connecting && body.Length >= 10 && body[..10].SequenceEqual("challenge "u8))
        {
            // The C formats "%s" from string + 10, so the token ends at the first NUL. A server built
            // with d0_blind_id puts its key exchange after that NUL; without the library it is noise.
            ReadOnlySpan<byte> token = body[10..];
            int nul = token.IndexOf((byte)0);
            if (nul >= 0)
                token = token[..nul];
            if (token.Length > MaxChallengeLength)
                return true;
            Challenge = Encoding.ASCII.GetString(token);
            outgoing.Add(BuildConnect(Challenge, _userInfo));
            return true;
        }
        // "accept" must be the whole packet (length == 6): a crypto server's accept carries more and
        // is a different message.
        if (connecting && body.SequenceEqual("accept"u8))
        {
            State = DpHandshakeState.Accepted;
            return true;
        }
        if (connecting && body.Length > 7 && body[..7].SequenceEqual("reject "u8))
        {
            State = DpHandshakeState.Rejected;
            RejectReason = Encoding.UTF8.GetString(body[7..]).TrimEnd('\0', '\n', '\r');
            return true;
        }
        // A server pings a client it has not heard from; no answer and it drops us. Valid in any state.
        if (body.StartsWith("ping"u8))
        {
            outgoing.Add(Ack());
            return true;
        }
        return true; // some other connectionless packet (ack, infoResponse, ...): ours to ignore
    }

    /// <summary>The connect request (netconn.c:2163). DarkPlaces lists every protocol it speaks in
    /// <c>protocols</c>; this client speaks one.</summary>
    public static byte[] BuildConnect(string challenge, string userInfo = "")
    {
        string text = "connect\\protocol\\darkplaces 3\\protocols\\" + DpProtocol.ProtocolNameDp7
            + userInfo + "\\challenge\\" + challenge;
        return Oob(text);
    }

    private static byte[] Ack() => Oob("ack");

    private static byte[] Oob(string text)
    {
        int n = Encoding.UTF8.GetByteCount(text);
        var packet = new byte[4 + n];
        MasterServerProtocol.OobHeader.CopyTo(packet);
        Encoding.UTF8.GetBytes(text, packet.AsSpan(4));
        return packet;
    }
}
