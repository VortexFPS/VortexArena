// dp-probe: a headless legacy client. It joins a live DarkPlaces/Xonotic server with the whole legacy
// stack - the DP7 connection (src/VortexArena.Legacy/Protocol), the server's own client program on the
// QuakeC VM (Csqc/CsqcHost) and the headless presentation that answers the program from the real map
// and models - signs on through "begin", plays for a while to a script, and disconnects.
// What a DarkPlaces client does at each point is in Base/darkplaces/cl_main.c (CL_Frame), cl_parse.c
// (CL_SignonReply, CL_BeginDownloads) and cl_input.c (CL_SendMove); the rcon request it can send on
// the side is netconn.c NetConn_ServerParsePacket's "rcon " case.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Tools.DpProbe;

internal static class Program
{

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter? _transcript;
    private static string? _secret;

    private static void Log(string line)
    {
        // The rcon password of the test server never reaches a transcript.
        if (_secret is { Length: > 0 }) line = line.Replace(_secret, "<rcon password>", StringComparison.Ordinal);
        string stamped = string.Create(CultureInfo.InvariantCulture, $"[{Clock.Elapsed.TotalSeconds,8:F3}] {line}");
        Console.WriteLine(stamped);
        _transcript?.WriteLine(stamped);
        _transcript?.Flush();
    }

    private static string Printable(string s, int limit = 200)
    {
        StringBuilder sb = new(s.Length);
        foreach (char c in s)
            sb.Append(c == '\n' ? "\\n" : c < ' ' ? $"\\x{(int)c:X2}" : c.ToString());
        return sb.Length > limit ? sb.ToString(0, limit) + "..." : sb.ToString();
    }

    private static string V(QcVector v) => string.Create(CultureInfo.InvariantCulture, $"'{v.X:0.0} {v.Y:0.0} {v.Z:0.0}'");

    private const string Usage =
        "usage: dp-probe [--host H] [--port P] [--data DIR] [--duration SEC] [--out TRANSCRIPT] [--name N] [--timeout SEC]\n" +
        "                [--csprogs FILE] [--local-csprogs] [--no-join] [--no-script] [--netfps N] [--fps N]\n" +
        "  --data DIR        the Xonotic data directory to mount (default ../Base/data)\n" +
        "  --duration SEC    how long to stay in the game (default 30)\n" +
        "  --timeout SEC     how long signing on may take before giving up (default 120)\n" +
        "  --csprogs FILE    compare the downloaded client program with this file, byte for byte\n" +
        "  --local-csprogs   use the data directory's csprogs.dat if it is the one the server names, instead of downloading\n" +
        "  --no-join         stay an observer (do not send \"join\")\n" +
        "  --no-script       stand still for the whole duration\n" +
        "  The environment variable DP_PROBE_RCON, if set, is the server's rcon_password (rcon_secure 0): the probe then asks\n" +
        "  the server for \"status\" and for its own entity's origin, and logs the answers.";

    private static int Main(string[] args)
    {
        string host = "127.0.0.1", name = "dp-probe", data = Path.Combine("..", "Base", "data");
        int port = 26000;
        string? csprogsPath = null, outPath = null;
        double timeout = 120, duration = 30, fps = 60, netFps = 64;
        bool localCsprogs = false, join = true, script = true;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            double Number() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "--host": host = Next(); break;
                case "--port": port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--data": data = Next(); break;
                case "--csprogs": csprogsPath = Next(); break;
                case "--out": outPath = Next(); break;
                case "--timeout": timeout = Number(); break;
                case "--duration": duration = Number(); break;
                case "--fps": fps = Math.Clamp(Number(), 10, 1000); break;
                case "--netfps": netFps = Number(); break;
                case "--name": name = Next(); break;
                case "--local-csprogs": localCsprogs = true; break;
                case "--no-join": join = false; break;
                case "--no-script": script = false; break;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        _secret = Environment.GetEnvironmentVariable("DP_PROBE_RCON");
        if (outPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            _transcript = new StreamWriter(outPath, append: false, new UTF8Encoding(false));
        }

        Log($"dp-probe: target {host}:{port}, started {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC, data {Path.GetFullPath(data)}");
        if (!Directory.Exists(data))
        {
            Log("RESULT: FAILED (the data directory does not exist)");
            return 1;
        }
        if (!IPAddress.TryParse(host, out IPAddress? address))
            address = Dns.GetHostAddresses(host).First();
        IPEndPoint endpoint = new(address, port);

        // --- the client: game data, console, engine cvars, the game's defaults, the presentation ---
        using VirtualFileSystem vfs = new();
        if (!vfs.MountGameDir(data))
        {
            Log("RESULT: FAILED (nothing could be mounted from the data directory)");
            return 1;
        }
        CvarService cvars = new();
        ConfigInterpreter interpreter = new(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null);
        // The engine's own cvars first, as DarkPlaces registers them before any configuration runs.
        int engineCvars = CsqcEngineCvars.Register(cvars);
        cvars.Register("pr_checkextension", "1");
        cvars.Register("utf8_enable", "1");
        cvars.Register("developer", "0");
        bool defaults = interpreter.ExecuteFile("default.cfg");
        Log($"mounted {vfs.MountedPaths.Count} packages; {engineCvars} engine cvars; default.cfg {(defaults ? "executed" : "NOT FOUND")} ({interpreter.FilesExecuted} files)");

        int printLines = 0, warnings = 0;
        StringBuilder printLine = new();
        Dictionary<string, int> warningCounts = new(StringComparer.Ordinal);
        string writeRoot = Path.Combine(Path.GetTempPath(), "dp-probe-" + Guid.NewGuid().ToString("N"));
        LegacyQcHost services = new(cvars, vfs)
        {
            WriteRoot = writeRoot,
            PrintSink = text =>
            {
                foreach (char c in text)
                {
                    if (c != '\n') { if (printLine.Length < 400) printLine.Append(c); continue; }
                    if (printLines++ < 200) Log("print: " + Printable(printLine.ToString()));
                    printLine.Clear();
                }
            },
            WarningSink = text =>
            {
                warnings++;
                text = text.TrimEnd();
                if (warningCounts.Count < 2000 || warningCounts.ContainsKey(text)) warningCounts[text] = warningCounts.GetValueOrDefault(text) + 1;
                if (warningCounts[text] == 1 && warningCounts.Count <= 60) Log("VM warning: " + Printable(text, 300));
            },
        };
        HeadlessLegacyPresentation presentation = new(vfs);
        LegacyClientOptions options = new() { AlwaysDownloadProgram = !localCsprogs, Host = new CsqcHostOptions { KeepRunningAfterFault = true } };
        options.Client.Signon.Name = name;
        // DarkPlaces' "rate" default of 20000 bytes a second would make the megabyte of csprogs take a
        // minute; Xonotic's own client configuration asks for 262144 ("_cl_rate").
        options.Client.Signon.Rate = cvars.Has("_cl_rate") && cvars.GetFloat("_cl_rate") > 0 ? (int)cvars.GetFloat("_cl_rate") : 262144;
        options.Client.Signon.RateBurstSize = 1024;
        options.Client.NetFps = netFps;
        options.PredictMovement = !cvars.Has("cl_movement") || cvars.GetFloat("cl_movement") != 0;
        using LegacyClientSession session = new(services, interpreter, presentation, options);
        if (cvars.Has("cl_nettimesyncboundmode")) session.Clock.BoundMode = (int)cvars.GetFloat("cl_nettimesyncboundmode");
        session.Event += text => Log("event: " + Printable(text, 600));
        DpClient client = session.Client;
        Log($"client: name \"{name}\", rate {options.Client.Signon.Rate}, cl_netfps {netFps}, cl_movement {(options.PredictMovement ? 1 : 0)}, cl_nettimesyncboundmode {session.Clock.BoundMode}, " +
            $"gameversion {options.Client.Signon.Variables["gameversion"]}, {fps} frames a second, client program {(localCsprogs ? "from the data directory if it matches" : "downloaded from the server")}");

        using DpUdpTransport transport = new(endpoint);
        Log($"local socket {transport.LocalEndPoint}");

        // What the client says to the server on the reliable stream, signon included.
        int commandsLogged = 0;
        client.CommandSent += command => { if (commandsLogged++ < 80) Log("cmd> " + Printable(command)); };
        // CL_KeepaliveMessage: starting the client program parses a few hundred models in one call,
        // during which no frame runs; the model loader calls back here.
        int keepAlives = 0;
        presentation.ModelData.Working = () =>
        {
            foreach (byte[] d in session.KeepAlive(Clock.Elapsed.TotalSeconds))
            {
                transport.Send(d);
                keepAlives++;
            }
        };

        // --- rcon on the side: what the server itself says about this client ---
        void Rcon(string command)
        {
            if (_secret is not { Length: > 0 }) return;
            byte[] text = Encoding.UTF8.GetBytes($"rcon {_secret} {command}");
            byte[] packet = new byte[4 + text.Length];
            packet[0] = packet[1] = packet[2] = packet[3] = 0xFF;
            text.CopyTo(packet, 4);
            transport.Send(packet);
            Log($"rcon> {command}");
        }

        session.Connect(Clock.Elapsed.TotalSeconds);
        DpClientState lastState = DpClientState.Disconnected;
        DpHandshakeState lastHandshake = DpHandshakeState.Idle;
        int lastStage = -1;
        bool announcedDownload = false, reportedDownload = false, downloadOk = true;
        double lastDownloadProgress = 0;
        string? failure = null;

        double inGameAt = -1, nextSecond = 0, nextFrame = 0;
        int second = 0;
        long lastEntityFrames = 0, lastAcks = 0, lastUpdates = 0, lastFrames = 0, lastDatagramsSent = 0, lastDatagramsReceived = 0;
        int lastMovePackets = 0;
        bool joined = false, askedStatusEarly = false, askedStatusLate = false, askedOriginBefore = false, askedOriginAfter = false;
        QcVector originAtForwardStart = default, originAtForwardEnd = default, originBeforeJump = default;
        float highestDuringJump = float.MinValue;
        bool sawForwardStart = false, sawForwardEnd = false, sawJump = false, jumpWasRespawn = false;
        double frameCpu = 0, frameCpuMax = 0;
        long framesTimed = 0;
        int mySlot = -1;
        long iterations = 0;

        // The script, in seconds after entering the game.
        const double JoinAt = 6, TurnFrom = 12, TurnTo = 15, ForwardFrom = 15, ForwardTo = 19, JumpAt = 22, JumpFor = 0.15;
        float scriptYawStart = 0;
        bool turnStarted = false;

        // The local player as the client program has it: its own entity for the server's player slot.
        bool PlayerOrigin(out QcVector origin, out QcVector velocity, out int edict)
        {
            origin = velocity = default;
            edict = 0;
            if (session.Host is not { } h) return false;
            edict = h.EdictForServerEntity(session.State.PlayerEntity);
            if (edict <= 0 || edict >= h.Vm.NumEdicts || h.Vm.IsFree(edict)) return false;
            origin = h.Vm.FieldVector(edict, h.Fields.Origin);
            velocity = h.Vm.FieldVector(edict, h.Fields.Velocity);
            return true;
        }

        while (true)
        {
            double now = Clock.Elapsed.TotalSeconds;
            bool inGame = inGameAt >= 0;
            if (!inGame && now > timeout) { failure = $"not in the game after {timeout} s (signon stage {client.Signon.Stage})"; break; }
            if (inGame && now - inGameAt >= duration) break;

            // --- the frame begins: the clock runs on; then the network is read ---
            session.BeginFrame(now);
            while (transport.TryReceive(out byte[] datagram))
            {
                if (datagram.Length > 5 && datagram[0] == 0xFF && datagram[1] == 0xFF && datagram[2] == 0xFF && datagram[3] == 0xFF && datagram[4] == (byte)'n')
                {
                    // An out-of-band print: the reply to an rcon command.
                    foreach (string line in Encoding.UTF8.GetString(datagram, 5, datagram.Length - 5).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        Log("rcon< " + Printable(line, 300));
                        // "prvm_edictget server N origin" answers with the bare vector.
                        string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && parts[0].StartsWith('#') && int.TryParse(parts[0].AsSpan(1), out int slot) && line.Contains(name, StringComparison.Ordinal)) mySlot = slot;
                    }
                    continue;
                }
                session.Receive(datagram, now);
            }

            // --- input ---
            LegacyInput input = default;
            double t = inGame ? now - inGameAt : -1;
            if (inGame && script)
            {
                if (join && !joined && t >= JoinAt)
                {
                    joined = true;
                    client.SendStringCommand("join");
                    Log("script: sent \"join\" (leave the observers and spawn as a player)");
                }
                if (t >= TurnFrom && t < TurnTo)
                {
                    if (!turnStarted) { turnStarted = true; scriptYawStart = session.State.ViewAngles.Y; Log($"script: turning the view from yaw {scriptYawStart:0.0}, 30 degrees a second for {TurnTo - TurnFrom} s"); }
                    QcVector angles = session.State.ViewAngles;
                    angles.Y = scriptYawStart + (float)((t - TurnFrom) * 30);
                    session.State.ViewAngles = angles;
                }
                if (t >= ForwardFrom && t < ForwardTo)
                {
                    input.ForwardMove = cvars.Has("cl_forwardspeed") && cvars.GetFloat("cl_forwardspeed") > 0 ? cvars.GetFloat("cl_forwardspeed") : 400;
                    if (!sawForwardStart && PlayerOrigin(out originAtForwardStart, out _, out _))
                    {
                        sawForwardStart = true;
                        Log($"script: +forward for {ForwardTo - ForwardFrom} s (forwardmove {input.ForwardMove}), view yaw {session.State.ViewAngles.Y:0.0}; origin {V(originAtForwardStart)}");
                        Rcon($"prvm_edictget server {Math.Max(1, session.State.PlayerEntity)} origin");
                        askedOriginBefore = true;
                    }
                }
                else if (t >= ForwardTo && sawForwardStart && !sawForwardEnd && PlayerOrigin(out originAtForwardEnd, out _, out _))
                {
                    sawForwardEnd = true;
                    Log($"script: -forward; origin {V(originAtForwardEnd)}");
                }
                if (t >= JumpAt && t < JumpAt + JumpFor)
                {
                    input.Buttons |= 2;
                    if (!sawJump && PlayerOrigin(out originBeforeJump, out _, out _))
                    {
                        sawJump = true;
                        Log($"script: +jump for {JumpFor} s; origin {V(originBeforeJump)}");
                    }
                }
                if (t >= JumpAt && t < JumpAt + 1.5 && PlayerOrigin(out QcVector jumping, out _, out _))
                {
                    highestDuringJump = MathF.Max(highestDuringJump, jumping.Z);
                    // Pressing jump while dead respawns the player somewhere else: that is not a jump.
                    float away = MathF.Abs(jumping.X - originBeforeJump.X) + MathF.Abs(jumping.Y - originBeforeJump.Y);
                    if (sawJump && away > 64) jumpWasRespawn = true;
                }
                if (!askedOriginAfter && askedOriginBefore && t >= ForwardTo + 1.5)
                {
                    askedOriginAfter = true;
                    Rcon($"prvm_edictget server {Math.Max(1, session.State.PlayerEntity)} origin");
                }
            }
            int serverEdict = Math.Max(1, session.State.PlayerEntity);
            if (inGame && !askedStatusEarly && t >= 3)
            {
                askedStatusEarly = true;
                Rcon("status 1");
                Rcon($"prvm_edictget server {serverEdict} classname");
            }
            if (inGame && !askedStatusLate && t >= Math.Min(duration - 3, 25))
            {
                askedStatusLate = true;
                Rcon("status 1");
                Rcon($"prvm_edictget server {serverEdict} classname");
            }

            // --- one client frame: clock, command, send ---
            foreach (byte[] d in session.Frame(now, input))
                transport.Send(d);

            // --- progress worth a line ---
            if (client.Handshake.State != lastHandshake)
            {
                lastHandshake = client.Handshake.State;
                Log($"handshake: {lastHandshake}" + (client.Handshake.Challenge.Length != 0 ? $" (challenge token of {client.Handshake.Challenge.Length} characters)" : ""));
            }
            if (client.State != lastState)
            {
                lastState = client.State;
                Log($"client state: {lastState}" + (client.LastError is null ? "" : $" ({client.LastError})"));
                if (lastState is DpClientState.Rejected or DpClientState.TimedOut or DpClientState.Failed or DpClientState.Disconnected)
                {
                    failure = $"{lastState}: {client.LastError}";
                    break;
                }
            }
            if (client.Signon.Stage != lastStage)
            {
                lastStage = client.Signon.Stage;
                Log($"signon stage {lastStage}" + lastStage switch
                {
                    1 => $": sent name, color, rate; csqc_progname {client.Signon.CsqcProgName}, csqc_progsize {client.Signon.CsqcProgSize}, csqc_progcrc {client.Signon.CsqcProgCrc}, cl_serverextension_download {client.Signon.ServerExtensionDownload}",
                    2 => ": sent \"spawn\"",
                    3 => ": sent \"begin\"",
                    4 => $": first svc_entities - in the game. view entity {session.State.ViewEntity}, player entity {session.State.PlayerEntity}, server time {session.Clock.ServerTime:0.000}",
                    _ => "",
                });
                if (lastStage == DpProtocol.Signons)
                {
                    inGameAt = now;
                    nextSecond = now + 1;
                    nextFrame = now;
                    lastEntityFrames = session.EntityFrames;
                    lastAcks = client.FrameAcksSent;
                    lastMovePackets = client.MovePacketsSent;
                    lastDatagramsSent = transport.DatagramsSent;
                    lastDatagramsReceived = transport.DatagramsReceived;
                }
            }
            if (client.Download.Active && !announcedDownload)
            {
                announcedDownload = true;
                Log($"cl_downloadbegin: \"{client.Download.Name}\", {client.Download.ExpectedSize} bytes on the wire, deflate={client.Download.Deflate}; sent sv_startdownload");
            }
            if (client.Download.Active && now - lastDownloadProgress > 2)
            {
                lastDownloadProgress = now;
                Log($"  downloading: {client.Download.ReceivedSize}/{client.Download.ExpectedSize} bytes; datagrams sent {transport.DatagramsSent}, deferred by pacing {client.SendsDeferred}, choked by rate {client.Channel.PacketsChoked}");
            }
            if (client.Signon.LastDownload is { } result && !reportedDownload)
            {
                reportedDownload = true;
                Log($"cl_downloadfinished: status {result.Status}, {result.WireSize} bytes on the wire, deflated={result.WasDeflated}");
                if (result.Status == DpDownloadStatus.Completed)
                {
                    Log($"  inflated size {result.Data.Length}, CRC16 {result.Crc}; server announced csqc_progsize {client.Signon.CsqcProgSize}, csqc_progcrc {client.Signon.CsqcProgCrc} -> " +
                        (client.Signon.CsprogsVerified ? "MATCH" : "MISMATCH"));
                    downloadOk = client.Signon.CsprogsVerified;
                    if (csprogsPath is not null && File.Exists(csprogsPath))
                    {
                        bool same = File.ReadAllBytes(csprogsPath).AsSpan().SequenceEqual(result.Data);
                        Log($"  local file {csprogsPath}: " + (same ? "IDENTICAL to the download, byte for byte" : "DIFFERENT from the download"));
                        downloadOk &= same;
                    }
                }
                else downloadOk = false;
            }

            // --- draw: CSQC_UpdateView at the frame rate, once in the game ---
            if (inGame && now >= nextFrame)
            {
                nextFrame += 1.0 / fps;
                if (nextFrame < now - 0.25) nextFrame = now;   // fell far behind (a long frame): do not burst to catch up
                long before = Stopwatch.GetTimestamp();
                session.Draw(1.0 / fps);
                double cost = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                frameCpu += cost;
                frameCpuMax = Math.Max(frameCpuMax, cost);
                framesTimed++;
            }

            // --- once a second in the game: the numbers ---
            if (inGame && now >= nextSecond)
            {
                nextSecond += 1;
                second++;
                CsqcHost? h = session.Host;
                long updates = h?.EntityUpdates ?? 0;
                bool hasOrigin = PlayerOrigin(out QcVector origin, out QcVector velocity, out int edict);
                int slot = session.State.PlayerEntity - 1;
                int frags = (uint)slot < (uint)session.State.Scores.Length ? session.State.Scores[slot].Frags : 0;
                Log($"t+{second,2}: svc_entities {session.EntityFrames - lastEntityFrames,3}/s (total {session.EntityFrames}), acks sent {client.FrameAcksSent - lastAcks,3}/s, " +
                    $"input packets {client.MovePacketsSent - lastMovePackets,3}/s, datagrams out {transport.DatagramsSent - lastDatagramsSent,3}/s in {transport.DatagramsReceived - lastDatagramsReceived,3}/s, " +
                    $"csqc ent updates {updates - lastUpdates,4}/s, frames {session.FramesDrawn - lastFrames,3}/s, faults {h?.FaultCount ?? 0}, desyncs {h?.DesyncCount ?? 0}, undecoded {session.MessagesNotDecoded}, " +
                    $"cl.time {session.State.Time:0.000} (server {session.Clock.ServerTime:0.000}), " +
                    (hasOrigin ? $"player edict {edict} origin {V(origin)} vel {V(velocity)}" : "no player entity yet") +
                    $", view {V(presentation.ViewOrigin)} yaw {session.State.ViewAngles.Y:0.0}, frags {frags}{(frags == -666 ? " (spectator)" : "")}, server acked move {client.ServerMoveSequence} of {client.Channel.OutgoingUnreliableSequence}");
                lastEntityFrames = session.EntityFrames;
                lastAcks = client.FrameAcksSent;
                lastMovePackets = client.MovePacketsSent;
                lastUpdates = updates;
                lastFrames = session.FramesDrawn;
                lastDatagramsSent = transport.DatagramsSent;
                lastDatagramsReceived = transport.DatagramsReceived;
            }

            // The loop is the client's frame rate as far as the connection is concerned (cl.realframetime),
            // and CL_SendMove's pacing assumes it is well above the packet rate. Thread.Sleep(1) cannot
            // give that on Windows: it sleeps a whole 15.6 ms timer tick, and Windows 11 ignores a
            // request for a finer timer from a process without a visible window (measured here: 64
            // iterations a second with timeBeginPeriod(1) in force). So: yield until 2 ms have passed.
            // It keeps one core busy for the length of the run, which a probe can afford.
            iterations++;
            double wake = now + 0.002;
            while (Clock.Elapsed.TotalSeconds < wake) Thread.Yield();
        }
        Log(string.Create(CultureInfo.InvariantCulture, $"main loop: {iterations} iterations, {iterations / Math.Max(0.001, Clock.Elapsed.TotalSeconds):0} a second on average"));
        Log($"keepalive nops sent while loading: {keepAlives}");

        // --- summary ---
        CsqcHost? program = session.Host;
        double played = inGameAt >= 0 ? Clock.Elapsed.TotalSeconds - inGameAt : 0;
        Log($"summary: client state {client.State}, signon stage {client.Signon.Stage}, in the game for {played:0.0} s of {duration} s; last error: {client.LastError ?? "none"}");
        Log($"summary: messages parsed {client.MessagesParsed}, not decoded to the end {session.MessagesNotDecoded}{(session.FirstUndecoded is null ? "" : " (first: " + Printable(session.FirstUndecoded, 500) + ")")}, last parse: {client.LastParse}");
        Log($"summary: svc_entities frames {session.EntityFrames}, frame acks sent {client.FrameAcksSent}, input packets {client.MovePacketsSent}" +
            (played > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({client.MovePacketsSent / played:0.0}/s)") : "") +
            $", datagrams sent {transport.DatagramsSent} received {transport.DatagramsReceived}, sends deferred by pacing {client.SendsDeferred}, choked by the rate limit {client.Channel.PacketsChoked}, " +
            $"reliable resends {client.Channel.PacketsResent}, duplicates {client.Channel.DuplicatesReceived}, unreliable gaps {client.Channel.DroppedDatagrams}");
        if (program is not null)
        {
            Log($"summary: client program crc {program.ProgramCrc}: CSQC_UpdateView frames {session.FramesDrawn} ({session.FramesFaulted} faulted), CSQC_Ent_Update {program.EntityUpdates}, CSQC_Ent_Remove {program.EntityRemoves}, " +
                $"temp entities consumed {program.TempEntitiesConsumed} declined {program.TempEntitiesDeclined}, VM faults {program.FaultCount}, desyncs {program.DesyncCount}, " +
                $"network reads outside a message {program.ReadsOutsideMessage}, entities {program.Vm.NumEdicts}, VM warnings {warnings} ({warningCounts.Count} distinct), print lines {printLines}");
            if (framesTimed > 0)
                Log(string.Create(CultureInfo.InvariantCulture, $"summary: CSQC_UpdateView cost {frameCpu / framesTimed:0.000} ms a frame on average, {frameCpuMax:0.0} ms at worst"));
            foreach (CsqcFault fault in program.Faults.Take(5))
                Log($"fault [{fault.EntryPoint}, message {fault.MessageIndex}, time {fault.Time:0.000}]: {Printable(fault.Message, 1500)}");
            foreach (CsqcDesync desync in program.Desyncs.Take(3))
                Log("desync: " + Printable(desync.ToString(), 600) + " | stack: " + Printable(desync.Stack, 600));
            foreach (((int number, string builtin), long calls) in program.UnimplementedBuiltins.OrderByDescending(u => u.Value).Take(10))
                Log($"unimplemented builtin called: #{number} {builtin} x{calls}");
            Log("summary: presentation calls: " + string.Join(", ", presentation.Calls.OrderByDescending(c => c.Value).Take(24).Select(c => $"{c.Key} {c.Value}")));
            Log($"summary: world {presentation.Map.MapName ?? "NOT LOADED: " + presentation.Map.LoadError}; models parsed {presentation.ModelData.ModelsParsed}; skeleton objects {presentation.ModelData.LiveSkeletons}; pictures {presentation.Pictures.Count}");
        }
        else Log($"summary: no client program was running ({session.ProgramError ?? "none was loaded"})");
        foreach ((string warning, int count) in warningCounts.OrderByDescending(w => w.Value).Take(12))
            Log($"VM warning x{count}: {Printable(warning, 300)}");
        if (sawForwardStart && sawForwardEnd)
        {
            float dx = originAtForwardEnd.X - originAtForwardStart.X, dy = originAtForwardEnd.Y - originAtForwardStart.Y;
            Log(string.Create(CultureInfo.InvariantCulture, $"movement: +forward for {ForwardTo - ForwardFrom} s moved the player {MathF.Sqrt(dx * dx + dy * dy):0.0} units in the plane, from {V(originAtForwardStart)} to {V(originAtForwardEnd)} (direction {MathF.Atan2(dy, dx) * 180 / MathF.PI:0.0} degrees)"));
        }
        if (sawJump && jumpWasRespawn)
            Log($"movement: the player was dead when +jump was pressed; it respawned elsewhere (from {V(originBeforeJump)}), so there is no jump height to report");
        else if (sawJump)
            Log(string.Create(CultureInfo.InvariantCulture, $"movement: +jump raised the player from z {originBeforeJump.Z:0.0} to at most z {highestDuringJump:0.0} ({highestDuringJump - originBeforeJump.Z:0.0} units)"));
        if (mySlot >= 0) Log($"server's status listed this client in slot #{mySlot}");

        bool stayed = failure is null && client.State == DpClientState.Connected && inGameAt >= 0;
        if (client.State == DpClientState.Connected)
        {
            foreach (byte[] d in session.Disconnect(Clock.Elapsed.TotalSeconds))
                transport.Send(d);
            Log("sent clc_disconnect x3");
        }

        bool clean = stayed && downloadOk && session.MessagesNotDecoded == 0 && (program is null || (program.FaultCount == 0 && program.DesyncCount == 0)) && session.ProgramError is null;
        failure ??= !stayed ? "the connection did not last"
            : !downloadOk ? "csprogs verification failed"
            : session.ProgramError is not null ? "client program: " + session.ProgramError
            : !clean ? "stayed connected, but with faults, desyncs or undecoded messages (see the summary)" : null;
        Log(failure is null ? "RESULT: OK" : "RESULT: FAILED (" + failure + ")");
        _transcript?.Dispose();
        try { if (Directory.Exists(writeRoot)) Directory.Delete(writeRoot, recursive: true); } catch (IOException) { }
        return failure is null ? 0 : 1;
    }
}
