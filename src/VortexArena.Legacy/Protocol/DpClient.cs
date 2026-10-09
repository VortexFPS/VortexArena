// Ties together the pieces ported from Base/darkplaces: netconn.c NetConn_ClientFrame /
// NetConn_ClientParsePacket / NetConn_ConnectionEstablished (who gets each datagram), cl_parse.c
// CL_ParseServerMessage and its stufftext handling, cl_input.c CL_SendMove (whether a packet goes
// out this frame, lines 1918-1958, and what goes in it, lines 2074-2167), cl_parse.c
// CL_KeepaliveMessage and cl_main.c CL_DisconnectEx.
using System.Numerics;

namespace VortexArena.Legacy.Protocol;

public enum DpClientState
{
    Disconnected,
    /// <summary>Handshake in progress.</summary>
    Connecting,
    /// <summary>Accepted; see <see cref="DpSignon.Stage"/> for how far signon has got (4 = in the game).</summary>
    Connected,
    /// <summary>The server refused; <see cref="DpClient.LastError"/> has its reason.</summary>
    Rejected,
    /// <summary>No answer to the handshake, or silence for longer than the timeout once connected.</summary>
    TimedOut,
    /// <summary>The server sent something that is not valid DP7 (a Host_Error in DarkPlaces).</summary>
    Failed,
}

public sealed class DpClientConfig
{
    public DpSignonConfig Signon { get; } = new();
    /// <summary>Extra <c>\key\value</c> pairs for the connect request. Normally empty.</summary>
    public string ConnectUserInfo { get; set; } = "";
    /// <summary>getchallenge attempts before giving up (cls.connect_remainingtries).</summary>
    public int ConnectTries { get; set; } = 10;
    /// <summary>cl_netrepeatinput: how many earlier moves ride along in each input packet (0..2).</summary>
    public int NetRepeatInput { get; set; } = 1;
    /// <summary>cl_netfps: input packets per second, clamped to 10..1000 and then to between one and
    /// two per server tick once the tick rate is known. 72 is DarkPlaces' default; Xonotic's
    /// configuration sets 64.</summary>
    public double NetFps { get; set; } = 72;
    /// <summary>cl_netimmediatebuttons: a change of buttons sends a packet at once, whatever the pacing says.</summary>
    public bool NetImmediateButtons { get; set; } = true;
    /// <summary>Seconds of silence after which a connected server is considered gone (cl_timeout).</summary>
    public double Timeout { get; set; } = 30;
    /// <summary>Seconds between clc_nop keepalives while signing on and otherwise idle (CL_KeepaliveMessage sends one every 5).</summary>
    public double KeepAliveInterval { get; set; } = 5;
}

/// <summary>
/// A DarkPlaces 7 client connection, without a socket and without a clock. Datagrams from the server
/// go in through <see cref="Receive"/>; <see cref="Update"/> returns the datagrams to send; the time
/// is an argument to both. <see cref="DpUdpTransport"/> supplies the socket for real use, and tests
/// supply byte arrays.
///
/// It runs the handshake, then the netchan, parses each server message into calls on the
/// <see cref="IDpClientHandler"/> it was given, and answers the parts of the conversation the engine
/// answers by itself: the signon stages, the csprogs download and its acks, entity frame acks,
/// keepalives. The handler sees everything else, including the two QuakeC-defined payloads it alone
/// can decode.
///
/// Not thread-safe; drive it from one thread, once per frame.
/// </summary>
public sealed class DpClient
{
    private readonly IDpClientHandler _handler;
    private readonly DpClientConfig _config;
    private readonly Relay _relay;
    private readonly DpStuffTextBuffer _stuffText = new();
    private readonly List<string> _stuffLines = new();
    private List<byte[]> _outgoing = new();
    private readonly DpMessageWriter _unreliable = new(DpProtocol.NetMaxMessage);
    private readonly List<int> _ackFrames = new();
    private readonly List<DpDownloadAck> _downloadAcks = new();

    // cl.movecmd[]: [0] is the move for the next packet, [1..] the ones already sent, newest first.
    private readonly DpUserCmd[] _moves = new DpUserCmd[4];
    private bool _moveQueued;
    private bool _framesToAck;
    private double _lastSendTime;
    private string? _relayError;

    // CL_SendMove's pacing state.
    private double _lastUpdateTime;      // when Update last ran, for cl.realframetime
    private bool _updatedBefore;
    private double _timeSincePacket;     // cl.timesincepacket
    private int _optInputsSinceUpdate;   // cl.opt_inputs_since_update
    private double _mtime0, _mtime1;     // cl.mtime: the last two svc_time stamps
    private uint _commandSequence;       // cl.cmd.sequence: what the last command built was stamped with

    public DpClient(IDpClientHandler handler, DpClientConfig? config = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _config = config ?? new DpClientConfig();
        _relay = new Relay(this);
        Parser = new DpServerMessageParser(_relay);
        Download = new DpDownload();
        Signon = new DpSignon(_config.Signon, Download);
        Handshake = new DpConnectionHandshake(_config.ConnectUserInfo, _config.ConnectTries);
        Channel = new DpNetChannel(0);
    }

    public DpClientState State { get; private set; } = DpClientState.Disconnected;
    /// <summary>Why the connection ended, for <see cref="DpClientState.Rejected"/>, <see cref="DpClientState.TimedOut"/> and <see cref="DpClientState.Failed"/>.</summary>
    public string? LastError { get; private set; }

    public DpConnectionHandshake Handshake { get; private set; }
    public DpNetChannel Channel { get; private set; }
    public DpServerMessageParser Parser { get; }
    public DpSignon Signon { get; }
    public DpDownload Download { get; }

    /// <summary>cls.servermovesequence: the newest input the server has applied, from the last entity frame.</summary>
    public uint ServerMoveSequence { get; private set; }
    /// <summary>The outcome of the most recent message parse.</summary>
    public DpParseResult LastParse { get; private set; }
    public int MessagesParsed { get; private set; }
    /// <summary>Messages cut short at a payload the handler could not measure.</summary>
    public int MessagesAborted { get; private set; }

    /// <summary>
    /// cl.movevars_ticrate: seconds per server tick, or 0 while unknown. The server publishes it as
    /// a stat (STAT_MOVEVARS_TICRATE, 240); whoever keeps the stats sets this. Known, it keeps the
    /// packet rate between one and two per tick and lets sends line up with server updates.
    /// </summary>
    public double MoveVarsTicRate { get; set; }
    /// <summary>cl.mtime[0]: the time stamp of the newest server message.</summary>
    public double ServerTime => _mtime0;
    /// <summary>Whether the last <see cref="Update"/> put an input command on the wire. An impulse
    /// that was queued but not sent has to be queued again.</summary>
    public bool LastUpdateSentMove { get; private set; }
    /// <summary>Input packets (datagrams carrying a clc_move) sent on this connection.</summary>
    public int MovePacketsSent { get; private set; }
    /// <summary>Entity frame acknowledgements (clc_ackframe) sent on this connection, repeats included.</summary>
    public int FrameAcksSent { get; private set; }
    /// <summary>Updates on which the pacing or the rate limit held the packet back.</summary>
    public int SendsDeferred { get; private set; }

    /// <summary>
    /// Raised before a server message is parsed, with the reader the parser is about to use and the
    /// message's index on this connection. The two payloads only a client program can measure
    /// (<see cref="IDpClientHandler.OnCsqcEntityUpdate"/>, <see cref="IDpClientHandler.OnTempEntity"/>)
    /// are read from this reader, so whoever hosts the program hands it over here.
    /// </summary>
    public event Action<DpMessageReader, int>? MessageStarting;
    /// <summary>Raised after a message has been parsed and the commands stuffed in it have been
    /// dealt with: the point at which DarkPlaces is "between frames".</summary>
    public event Action<DpParseResult>? MessageFinished;

    /// <summary>CL_EstablishConnection: begin the handshake. The first datagram leaves on the next <see cref="Update"/>.</summary>
    public void Connect(double now)
    {
        Handshake = new DpConnectionHandshake(_config.ConnectUserInfo, _config.ConnectTries);
        Handshake.Start(now);
        DemoPlayback = false;
        State = DpClientState.Connecting;
        LastError = null;
        _outgoing.Clear();
    }

    /// <summary>cls.demoplayback: the messages come from a recording (<see cref="ReceiveDemoMessage"/>),
    /// nothing is sent, and nothing times out.</summary>
    public bool DemoPlayback { get; private set; }

    /// <summary>CL_PlayDemo_f: the connection is "established" at once, with nobody at the other end.</summary>
    public void BeginDemo(double now)
    {
        LastError = null;
        _outgoing.Clear();
        ConnectionEstablished(now);
        DemoPlayback = true;
    }

    /// <summary>One recorded server message (CL_ReadDemoMessage's CL_ParseServerMessage).</summary>
    public void ReceiveDemoMessage(byte[] message)
    {
        if (DemoPlayback && State == DpClientState.Connected) ProcessMessage(message);
    }

    /// <summary>The recording has no more messages: CL_Disconnect.</summary>
    public void EndDemo()
    {
        if (DemoPlayback && State == DpClientState.Connected) End(DpClientState.Disconnected, "the demo ended");
    }

    /// <summary>Hand over one datagram that arrived from the server. Never throws on malformed data.</summary>
    public void Receive(ReadOnlySpan<byte> datagram, double now)
    {
        if (State != DpClientState.Connecting && State != DpClientState.Connected)
            return;

        // Connectionless packets first, in every state: the server's ping must be answered while
        // connected too.
        if (Handshake.Receive(datagram, now, _outgoing))
        {
            if (State != DpClientState.Connecting)
                return;
            if (Handshake.State == DpHandshakeState.Accepted)
                ConnectionEstablished(now);
            else if (Handshake.State == DpHandshakeState.Rejected)
                End(DpClientState.Rejected, Handshake.RejectReason);
            return;
        }

        if (State != DpClientState.Connected)
            return;
        if (Channel.Receive(datagram, now, _outgoing, out byte[]? message) == DpChannelReceive.Message)
            ProcessMessage(message!);
    }

    // NetConn_ConnectionEstablished
    private void ConnectionEstablished(double now)
    {
        Channel = new DpNetChannel(now);
        Signon.Reset();
        Parser.Entities.Clear();
        _stuffText.Clear();
        Array.Clear(_moves);
        _moveQueued = false;
        _framesToAck = false;
        ServerMoveSequence = 0; // reset move sequence numbering on this new connection
        _lastSendTime = now;
        _lastUpdateTime = now;
        _updatedBefore = false;
        _timeSincePacket = 0;
        _optInputsSinceUpdate = 0;
        _mtime0 = _mtime1 = 0;
        _commandSequence = 0;
        MovePacketsSent = FrameAcksSent = SendsDeferred = 0;
        LastUpdateSentMove = false;
        State = DpClientState.Connected;
    }

    /// <summary>Levels entered on this connection whose first input command found the history of the level
    /// before still in place (it no longer does; counted for a test and a log line).</summary>
    public int LevelStateClears { get; private set; }

    // CL_ParseServerInfo calls CL_ClearState, which wipes the whole of "cl" (memset): of what this class
    // keeps, that is the input history (cl.movecmd, cl.cmd), the server time stamps (cl.mtime) and the send
    // pacing (cl.timesincepacket, cl.opt_inputs_since_update). The connection itself (cls.netcon, its
    // sequences and its reliable stream) and cls.servermovesequence live in "cls" and go on.
    //
    // It matters for more than tidiness. CL_SendMove measures a command's length against the last one sent,
    // "cl.cmd.frametime = bound(0.0, cl.cmd.time - cl.movecmd[1].time, 0.255)", and "do not send 0ms packets
    // because they mess up physics" holds the WHOLE packet back - the reliable stream with it. A new level's
    // clock starts over, so with the old level's last command still in the history every command of the new
    // level measured zero until its clock had passed the old level's: on a server joined twenty minutes into
    // a match, nothing the player did - chat, "join", movement - reached the server for twenty minutes.
    private void ClearLevelState()
    {
        if (_moves[1].Time != 0 || _moves[0].Time != 0) LevelStateClears++;
        Array.Clear(_moves);
        _moveQueued = false;
        _mtime0 = _mtime1 = 0;
        _timeSincePacket = 0;
        _optInputsSinceUpdate = 0;
        _commandSequence = 0;
        LastUpdateSentMove = false;
    }

    private void ProcessMessage(byte[] message)
    {
        // An entity frame is remembered against cl.cmd.sequence as it stood when the frame arrived:
        // the sequence the last input command was stamped with, which is one behind the channel's
        // if that command went out. (Using the channel's own would acknowledge every frame in three
        // packets instead of the two DarkPlaces sends: "this is 10 bytes".)
        Parser.MoveSequence = _commandSequence;
        _relayError = null;
        DpMessageReader reader = new(message);
        MessageStarting?.Invoke(reader, MessagesParsed);
        DpParseResult result = Parser.Parse(reader);
        LastParse = result;
        MessagesParsed++;
        if (result.Status == DpParseStatus.Aborted)
            MessagesAborted++;

        // Stuffed commands run after the message, never in the middle of it (in DarkPlaces they sit
        // in the console buffer until the next frame). The signon logic depends on that order.
        if (_stuffLines.Count != 0)
        {
            // Each command goes on terminated, as it would sit in the console buffer: a handler that
            // queues it (the client program's CSQC_Parse_StuffCmd does, through localcmd) must not find
            // it run together with the next one.
            foreach (string line in _stuffLines)
                if (!Signon.HandleCommand(line))
                    _handler.OnStuffText(line + '\n');
            _stuffLines.Clear();
        }
        Signon.EndOfMessage();
        FlushSignonCommands();
        MessageFinished?.Invoke(result);

        if (State != DpClientState.Connected)
            return; // svc_disconnect
        if (_relayError is not null)
            End(DpClientState.Failed, _relayError);
        else if (result.IsError)
            End(DpClientState.Failed, result.ToString());
    }

    private void FlushSignonCommands()
    {
        if (Signon.Commands.Count == 0)
            return;
        foreach (string command in Signon.Commands)
            SendStringCommand(command);
        Signon.Commands.Clear();
    }

    /// <summary>
    /// The package downloads the level's loading waited for have ended (libcurl.c Curl_CheckCommandWhenDone
    /// running "cl_begindownloads"): the signon goes on - to the client program's download, the map check,
    /// "prespawn" - and what it wants sent is queued.
    /// </summary>
    public void ContinueDownloads()
    {
        if (State != DpClientState.Connected) return;
        Signon.PackagesFinished();
        FlushSignonCommands();
    }

    /// <summary>Whether a command of that name is among the stuffed commands of the message being parsed
    /// that have not run yet. (They run after the message; a handler of svc_serverinfo can ask whether
    /// "curl" downloads were announced ahead of it.)</summary>
    public bool StuffedCommandPending(string name)
    {
        foreach (string line in _stuffLines)
        {
            ReadOnlySpan<char> text = line.AsSpan().TrimStart();
            if (text.StartsWith(name, StringComparison.OrdinalIgnoreCase) && (text.Length == name.Length || text[name.Length] is ' ' or '\t')) return true;
        }
        return false;
    }

    /// <summary>Raised with each console command queued for the server, the signon's own included.</summary>
    public event Action<string>? CommandSent;

    /// <summary>
    /// CL_KeepaliveMessage: while the client is busy loading and cannot run frames, a clc_nop every
    /// <see cref="DpClientConfig.KeepAliveInterval"/> seconds keeps the server from giving up on it.
    /// Called from inside long work (DarkPlaces calls it from its model and texture loaders); returns
    /// what to send, which is nothing unless the interval has passed. Only before the client is in
    /// the game: after that, frames run and input packets do the job.
    /// </summary>
    public IReadOnlyList<byte[]> KeepAlive(double now)
    {
        if (DemoPlayback || State != DpClientState.Connected || Signon.Stage >= DpProtocol.Signons || now - _lastSendTime < _config.KeepAliveInterval)
            return Array.Empty<byte[]>();
        _unreliable.Clear();
        DpClientMessages.WriteNop(_unreliable);
        // "NetConn_SendUnreliableMessage(cls.netcon, &msg, cls.protocol, 10000, 0, false)"
        Channel.Transmit(_unreliable.WrittenSpan, now, _outgoing, 10000, 0);
        _lastSendTime = now;
        List<byte[]> result = _outgoing;
        _outgoing = new List<byte[]>();
        return result;
    }

    /// <summary>CL_ForwardToServer: queue a console command for the server on the reliable stream.</summary>
    public void SendStringCommand(string command)
    {
        if (State != DpClientState.Connected || DemoPlayback)
            return;
        CommandSent?.Invoke(command);
        DpClientMessages.WriteStringCommand(Channel.Reliable, command);
        if (Channel.Reliable.Overflowed)
            End(DpClientState.Failed, "reliable message overflowed");
    }

    /// <summary>
    /// Set the input for the next packet. Call once per frame; a second call before the next
    /// <see cref="Update"/> replaces the first, as DarkPlaces keeps rebuilding cl.cmd until a packet
    /// goes out. Input is only sent once signon is complete. <see cref="DpUserCmd.Sequence"/> is
    /// filled in when the packet is built.
    /// </summary>
    public void QueueMove(in DpUserCmd cmd)
    {
        _moves[0] = cmd;
        _moveQueued = true;
    }

    /// <summary>
    /// Advance to <paramref name="now"/> and collect what has to be sent. The returned list is valid
    /// until the next call.
    /// </summary>
    public IReadOnlyList<byte[]> Update(double now)
    {
        if (State == DpClientState.Connecting)
        {
            Handshake.Update(now, _outgoing);
            if (Handshake.State == DpHandshakeState.TimedOut)
                End(DpClientState.TimedOut, "Connect: failed, no reply");
        }
        else if (State == DpClientState.Connected && !DemoPlayback)
        {
            SendMove(now);
            if (State == DpClientState.Connected && now - Channel.LastMessageTime > _config.Timeout)
                End(DpClientState.TimedOut, "server stopped responding");
        }

        List<byte[]> result = _outgoing;
        _outgoing = new List<byte[]>();
        return result;
    }

    // CL_SendMove for DP7 (cl_input.c:1771-2200): build the input command, decide whether this
    // update sends a packet at all, and if so write it. The decision is the C's, in the C's order:
    //   1. never a move of zero milliseconds once in the game ("they mess up physics");
    //   2. not more often than cl_netfps, unless the buttons changed or an impulse is pending, or the
    //      moment is "opportune" (just after a server update) and nothing was sent since that update;
    //   3. not while the rate limit says the line is busy (NetConn_CanSend), again unless important.
    //
    // One addition to the C: a reliable fragment whose ACK is overdue is repeated even on an update
    // that sends nothing else. DarkPlaces repeats it only from inside the next packet it sends, and
    // while signing on it may send none for seconds; a lost "prespawn" then waits for the next
    // keepalive. Repeating it here costs one datagram a second at most.
    private void SendMove(double now)
    {
        // cl.realframetime: "networking assumes at least 10fps".
        double realFrameTime = _updatedBefore ? Math.Clamp(now - _lastUpdateTime, 0, 0.1) : 0;
        _lastUpdateTime = now;
        _updatedBefore = true;
        LastUpdateSentMove = false;

        bool inGame = Signon.Stage == DpProtocol.Signons;
        bool haveMove = _moveQueued;
        int rate = _config.Signon.Rate, burst = _config.Signon.RateBurstSize;
        int msec = 0;
        bool important = false;
        double moveTime = 0;
        // "cl.cmd.sequence = cls.netcon->outgoing_unreliable_sequence", every frame, sent or not.
        _commandSequence = Channel.OutgoingUnreliableSequence;
        if (haveMove)
        {
            ref DpUserCmd cmd = ref _moves[0];
            cmd.Sequence = Channel.OutgoingUnreliableSequence;
            moveTime = cmd.Time;
            double frameTime = Math.Clamp((double)cmd.Time - _moves[1].Time, 0.0, 0.255);
            // ridiculous value rejection (matches qw)
            if (frameTime > 0.25)
                frameTime = 0.1;
            msec = (int)Math.Floor(frameTime * 1000);
            // always dump the first two moves, because they may contain leftover inputs from the last level
            if (cmd.Sequence <= 2)
            {
                cmd.ForwardMove = cmd.SideMove = cmd.UpMove = 0;
                cmd.Impulse = 0;
                cmd.Buttons = 0;
            }
            // always send if buttons changed or an impulse is pending
            // even if it violates the rate limit!
            important = cmd.Impulse != 0 || (_config.NetImmediateButtons && cmd.Buttons != _moves[1].Buttons);
        }

        _timeSincePacket += realFrameTime;

        bool send = true;
        bool opportune = false;
        // do not send 0ms packets because they mess up physics
        if (haveMove && msec == 0 && _mtime0 > _mtime1 && inGame)
            send = false;
        else
        {
            // don't send too often or else network connections can get clogged by a high renderer framerate
            double packetTime = 1.0 / Math.Clamp(_config.NetFps, 10.0, 1000.0);
            double ticRate = MoveVarsTicRate;
            if (ticRate > 0)
                packetTime = Math.Clamp(packetTime, ticRate * 0.5, ticRate);

            // improve and stabilise ping by synchronising with the server
            double lag = _mtime0 - moveTime;
            //  unknown ticrate || PL or ping spike || loading
            if (haveMove && ticRate > 0 && lag <= ticRate && lag >= 0 && realFrameTime > 0)
            {
                double framesPerTic = ticRate / realFrameTime;
                opportune = lag < 0.999 * realFrameTime * (framesPerTic <= 1 ? 1 : Math.Sqrt(framesPerTic));
            }

            // don't send too often (cl_netfps)
            if (!important && _timeSincePacket < packetTime * 0.999 && (!opportune || _optInputsSinceUpdate != 0))
                send = false;
            // don't choke the connection with packets (obey rate limit); we also still send if it is important
            else if (!Channel.CanSend(now) && !important)
                send = false;
        }

        if (!send)
        {
            SendsDeferred++;
            if (Channel.ReliableResendDue(now))
            {
                int resendBefore = _outgoing.Count;
                Channel.Transmit(default, now, _outgoing, rate, burst);
                if (_outgoing.Count != resendBefore)
                    _lastSendTime = now;
            }
            return;
        }

        if (opportune)
            _optInputsSinceUpdate++;
        _timeSincePacket = 0;

        _unreliable.Clear();
        int repeat = Math.Clamp(_config.NetRepeatInput + 1, 1, 3);
        // when movement prediction is off, there's not much point in repeating old input as it will just be ignored
        int moveCount = haveMove && !_moves[0].Predicted ? 1 : repeat;
        bool sentMove = false;

        if (inGame && haveMove)
        {
            // send the latest moves in order, the old ones will be ignored by the server harmlessly,
            // however if the previous packets were lost these moves will be used
            Span<DpUserCmd> ordered = stackalloc DpUserCmd[moveCount];
            int n = 0;
            for (int i = moveCount - 1; i >= 0; i--)
                if (i == 0 || _moves[i].Sequence != 0)
                    ordered[n++] = _moves[i];
            DpClientMessages.WriteInputPacket(_unreliable, ordered[..n], ServerMoveSequence, default, default);
            sentMove = _unreliable.Length != 0;
        }

        if (sentMove || (_framesToAck && inGame))
        {
            // ack entity frame numbers received since the last input was sent
            // (redundant to improve handling of client->server packet loss)
            _ackFrames.Clear();
            Parser.Entities.CollectAcks(Channel.OutgoingUnreliableSequence, repeat, _ackFrames);
            foreach (int frame in _ackFrames)
                DpClientMessages.WriteAckFrame(_unreliable, frame);
            FrameAcksSent += _ackFrames.Count;
            _framesToAck = false;
        }

        // acknowledge any recently received data blocks
        _downloadAcks.Clear();
        Download.TakeAcks(_downloadAcks);
        foreach (DpDownloadAck ack in _downloadAcks)
            DpClientMessages.WriteAckDownloadData(_unreliable, ack.Start, ack.Size);

        // While signing on there may be nothing to say for a long time (a slow level load on the
        // other side); an unreliable nop keeps the server from timing the client out.
        if (_unreliable.Length == 0 && Channel.Reliable.Length == 0 && !Channel.ReliableInFlight
            && Signon.Stage < DpProtocol.Signons && now - _lastSendTime >= _config.KeepAliveInterval)
            DpClientMessages.WriteNop(_unreliable);

        // NetConn_SendUnreliableMessage(..., max(20 * (buf.cursize + 40), cl_rate), cl_rate_burstsize):
        // the rate is never so low that one packet of this size takes more than a twentieth of a second.
        int before = _outgoing.Count;
        if (!Channel.Transmit(_unreliable.WrittenSpan, now, _outgoing, Math.Max(20 * (_unreliable.Length + 40), rate), burst))
        {
            End(DpClientState.Failed, "outgoing message too large");
            return;
        }
        if (_outgoing.Count != before)
            _lastSendTime = now;

        if (sentMove)
        {
            MovePacketsSent++;
            LastUpdateSentMove = true;
            // the move just sent becomes history; a new slot for the next input
            for (int i = _moves.Length - 1; i >= 1; i--)
                _moves[i] = _moves[i - 1];
            _moveQueued = false;
        }
    }

    /// <summary>
    /// CL_DisconnectEx: tell the server, three times because the message is unreliable and there will
    /// be no retry, then drop the connection. The datagrams are returned by the next <see cref="Update"/>.
    /// </summary>
    public void Disconnect(double now)
    {
        if (State == DpClientState.Connected)
        {
            _unreliable.Clear();
            DpClientMessages.WriteDisconnect(_unreliable);
            // The reliable buffer is dropped: nothing queued there can be delivered any more.
            Channel.Reliable.Clear();
            for (int i = 0; i < 3; i++)
                Channel.Transmit(_unreliable.WrittenSpan, now, _outgoing);
        }
        State = DpClientState.Disconnected;
    }

    private void End(DpClientState state, string? reason)
    {
        State = state;
        LastError = reason;
    }

    /// <summary>
    /// Sits between the parser and the user's handler: forwards every call, and on the way picks out
    /// what the connection itself must react to.
    /// </summary>
    private sealed class Relay : IDpClientHandler
    {
        private readonly DpClient _c;
        private IDpClientHandler H => _c._handler;

        public Relay(DpClient client) => _c = client;

        public void OnNop() => H.OnNop();

        public void OnDisconnect()
        {
            _c.End(DpClientState.Disconnected, "Server disconnected");
            H.OnDisconnect();
        }

        public void OnUpdateStat(int index, int value) => H.OnUpdateStat(index, value);
        public void OnVersion(int protocol) => H.OnVersion(protocol);
        public void OnSetView(int entity) => H.OnSetView(entity);
        public void OnSound(in DpSound sound) => H.OnSound(sound);
        // The head of CL_NetworkTimeReceived, as far as CL_SendMove depends on it: the last two time
        // stamps, equal until the client is in the game, and never more than a tenth of a second apart.
        public void OnTime(float time)
        {
            _c._optInputsSinceUpdate = 0;
            _c._mtime1 = _c._mtime0;
            _c._mtime0 = time;
            if (_c._mtime1 == _c._mtime0 || _c.Signon.Stage < DpProtocol.Signons)
                _c._mtime1 = time;
            else
                _c._mtime1 = Math.Max(_c._mtime1, _c._mtime0 - 0.1);
            H.OnTime(time);
        }
        public void OnPrint(string text) => H.OnPrint(text);

        // Not forwarded here: the text is cut into commands first, the engine's own are taken out,
        // and the rest reach the handler one command at a time after the message.
        public void OnStuffText(string text) => _c._stuffText.Add(text, _c._stuffLines);

        public void OnSetAngle(Vector3 angles) => H.OnSetAngle(angles);

        public void OnServerInfo(DpServerInfo info)
        {
            _c.ClearLevelState();
            _c.Signon.OnServerInfo(info.WorldModel);
            H.OnServerInfo(info);
        }

        public void OnLightStyle(int style, string map) => H.OnLightStyle(style, map);
        public void OnUpdateName(int client, string name) => H.OnUpdateName(client, name);
        public void OnUpdateFrags(int client, int frags) => H.OnUpdateFrags(client, frags);
        public void OnClientData(in DpClientData data) => H.OnClientData(data);
        public void OnStopSound(int entity, int channel) => H.OnStopSound(entity, channel);
        public void OnUpdateColors(int client, int colors) => H.OnUpdateColors(client, colors);
        public void OnParticle(in DpParticle particle) => H.OnParticle(particle);
        public void OnDamage(int armor, int blood, Vector3 from) => H.OnDamage(armor, blood, from);
        public void OnSpawnStatic(in EntityState state) => H.OnSpawnStatic(state);
        public void OnSpawnBaseline(int entity, in EntityState baseline) => H.OnSpawnBaseline(entity, baseline);
        public void OnSetPause(bool paused) => H.OnSetPause(paused);

        public void OnSignonNum(int stage)
        {
            int was = _c.Signon.Stage;
            if (!_c.Signon.OnSignonNum(stage))
                _c._relayError ??= $"Received signon {stage} when at {was}";
            H.OnSignonNum(stage);
        }

        public void OnCenterPrint(string text) => H.OnCenterPrint(text);
        public void OnKilledMonster() => H.OnKilledMonster();
        public void OnFoundSecret() => H.OnFoundSecret();
        public void OnSpawnStaticSound(in DpStaticSound sound) => H.OnSpawnStaticSound(sound);
        public void OnIntermission() => H.OnIntermission();
        public void OnFinale(string text) => H.OnFinale(text);
        public void OnCdTrack(int track, int loopTrack) => H.OnCdTrack(track, loopTrack);
        public void OnSellScreen() => H.OnSellScreen();
        public void OnCutscene(string text) => H.OnCutscene(text);
        public void OnShowLmp(string label, string picture, int x, int y) => H.OnShowLmp(label, picture, x, y);
        public void OnHideLmp(string label) => H.OnHideLmp(label);
        public void OnSkybox(string name) => H.OnSkybox(name);

        public void OnDownloadData(int start, ReadOnlySpan<byte> data)
        {
            if (!_c.Download.OnData(start, data))
                _c._relayError ??= "corrupt download message";
            H.OnDownloadData(start, data);
        }

        public void OnEffect(in DpEffect effect) => H.OnEffect(effect);
        public void OnPrecache(int index, bool isSound, string name) => H.OnPrecache(index, isSound, name);

        public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities)
        {
            _c.Signon.OnEntityFrame();
            _c.ServerMoveSequence = (uint)frame.ServerMoveSequence;
            _c._framesToAck = true;
            H.OnEntityFrame(frame, entities);
        }

        public void OnTrailParticles(in DpTrailParticles trail) => H.OnTrailParticles(trail);
        public void OnPointParticles(in DpPointParticles particles) => H.OnPointParticles(particles);
        public DpPayloadResult OnTempEntity(DpMessageReader reader) => H.OnTempEntity(reader);
        public void OnEngineTempEntity(in DpTempEntity tempEntity) => H.OnEngineTempEntity(tempEntity);
        public DpPayloadResult OnCsqcEntityUpdate(int entity, DpMessageReader reader) => H.OnCsqcEntityUpdate(entity, reader);
        public void OnCsqcEntityRemove(int entity) => H.OnCsqcEntityRemove(entity);
    }
}
