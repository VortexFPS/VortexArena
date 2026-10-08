// Port of Base/darkplaces/sv_user.c: SV_ReadClientMessage, SV_ReadClientMove, SV_ExecuteClientMoves,
// SV_FrameLost, SV_FrameAck, SV_PreSpawn_f, SV_Spawn_f, SV_Begin_f; sv_ccmds.c: the commands a client
// may send (cmd_serverfromclient: CF_SERVER_FROM_CLIENT and CF_USERINFO) - SV_Name_f, SV_Name,
// SV_Rate_f, SV_Rate_BurstSize_f, SV_Color_f, SV_Playermodel_f, SV_Playerskin_f, SV_Kill_f, SV_Say,
// SV_Ping_f, SV_Pings_f, SV_Status_f; sv_main.c SV_Download_f / SV_StartDownload_f (SvDownload).
using System.Globalization;
using System.Text;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

public sealed partial class SvServer
{
    private const int MaxUserCmds = 128;        // CL_MAX_USERCMDS
    private const int NetGraphPackets = 64;     // NETGRAPH_PACKETS
    private readonly SvUserCmd[] _readMoves = new SvUserCmd[MaxUserCmds];
    private int _numReadMoves;

    /// <summary>Client messages dropped for a reason DarkPlaces drops the client for.</summary>
    public long ClientsDroppedForBadMessages { get; private set; }
    public long StringCommandsReceived { get; private set; }
    public long MovesReceived { get; private set; }
    /// <summary>A clc_stringcmd arrived from a client, before anything acts on it (for a transcript).</summary>
    public event Action<SvClient, string>? ClientCommandReceived;

    // SV_ReadClientMessage: one complete message from a client.
    private void ReadClientMessage(SvqcHost host, SvClient client, SvNetConnection connection, byte[] data)
    {
        DpMessageReader msg = new(data);
        _numReadMoves = 0;
        while (true)
        {
            if (!client.Active || !ReferenceEquals(client.Connection, connection)) return;   // dropped by something it sent
            if (msg.BadRead)
            {
                ClientsDroppedForBadMessages++;
                host.DropClient(client, "An internal server error occurred");
                return;
            }
            if (msg.Remaining <= 0)
            {
                // end of message
                ExecuteClientMoves(host, client, connection);
                return;
            }
            int command = msg.ReadByte();
            switch ((Clc)command)
            {
                case Clc.Nop:
                    break;

                case Clc.StringCmd:
                {
                    // allow reliable messages now as the client is done with initial loading
                    if (client.SendSignon == 2) client.SendSignon = 0;
                    string s = msg.ReadString();
                    if (msg.BadRead) break;
                    StringCommandsReceived++;
                    ClientCommandReceived?.Invoke(client, s);
                    // "newline seen, THEN something else -> possible exploit": a command is one line
                    int newline = s.IndexOfAny(NewLines);
                    if (newline >= 0)
                    {
                        bool trailing = false;
                        for (int i = newline; i < s.Length; i++)
                            if (s[i] != '\n' && s[i] != '\r') { trailing = true; break; }
                        if (trailing)
                        {
                            Print($"Received invalid stringcmd from {Printable(client.Name)}\n");
                            break;
                        }
                        s = s[..newline];
                    }
                    if (s.StartsWith("spawn", StringComparison.OrdinalIgnoreCase) || s.StartsWith("begin", StringComparison.OrdinalIgnoreCase)
                        || s.StartsWith("prespawn", StringComparison.OrdinalIgnoreCase))
                        ExecuteClientCommand(client, s);
                    else if (host.Fn.SvParseClientCommand != 0)
                        // the program decides, and hands what it does not want back through clientcommand()
                        host.ParseClientCommand(client, s);
                    else
                        ExecuteClientCommand(client, s);
                    break;
                }

                case Clc.Disconnect:
                    // client wants to disconnect
                    host.DropClient(client, "Disconnect by user", leaving: true);
                    return;

                case Clc.Move:
                    ReadClientMove(host, client, msg);
                    break;

                case Clc.AckDownloadData:
                {
                    int start = msg.ReadLong(), size = (short)msg.ReadShort();
                    if (msg.BadRead) break;
                    connection.Download?.Ack(start, size, connection.Message);
                    break;
                }

                case Clc.AckFrame:
                {
                    int num = msg.ReadLong();
                    if (msg.BadRead) break;
                    if (client.Begun && connection.LatestFrameNum < num && connection.EntityDatabase is { } database)
                    {
                        // Every frame between the last acknowledged one and this one was lost. The loop
                        // ends at the newest frame the server has sent, whatever number the client names.
                        for (int i = connection.LatestFrameNum + 1; i < num; i++)
                        {
                            if (i > database.LatestFrameNumber) break;
                            database.LostFrame(i);
                            connection.CsqcFrames?.LostFrame(i);
                        }
                        database.AckFrame(num);
                        connection.LatestFrameNum = num;
                    }
                    break;
                }

                default:
                    Print($"SV_ReadClientMessage: unknown command char {command} (at offset 0x{msg.Position:x})\n");
                    ClientsDroppedForBadMessages++;
                    host.DropClient(client, "Unknown message sent to the server");
                    return;
            }
        }
    }

    private static readonly char[] NewLines = { '\n', '\r' };

    // SV_ReadClientMove: one clc_move, DP7 layout (56 bytes after the command byte).
    private void ReadClientMove(SvqcHost host, SvClient client, DpMessageReader msg)
    {
        SvUserCmd move = default;
        move.Sequence = (uint)msg.ReadLong();
        move.Time = msg.ReadFloat();
        move.ReceiveTime = (float)host.Time;
        // limit reported time to current time (incase the client is trying to speedhack; a NaN falls
        // to the server's time too)
        float limit = (float)(host.Time + host.FrameTime);
        move.Time = move.Time <= limit ? move.Time : limit;
        // read current angles
        move.ViewAngles = new QcVector(msg.ReadAngle16i(), msg.ReadAngle16i(), msg.ReadAngle16i());
        // read movement
        move.ForwardMove = msg.ReadCoord16i();
        move.SideMove = msg.ReadCoord16i();
        move.UpMove = msg.ReadCoord16i();
        // read buttons, impulse
        move.Buttons = msg.ReadLong();
        move.Impulse = msg.ReadByte();
        // PRYDON_CLIENTCURSOR
        move.CursorScreen = new QcVector((short)msg.ReadShort() * (1.0f / 32767.0f), (short)msg.ReadShort() * (1.0f / 32767.0f), 0);
        move.CursorStart = new QcVector(msg.ReadFloat(), msg.ReadFloat(), msg.ReadFloat());
        move.CursorImpact = new QcVector(msg.ReadFloat(), msg.ReadFloat(), msg.ReadFloat());
        int cursorEntity = msg.ReadUShort();
        // as requested by FrikaC, cursor_trace_ent is reset to world if the entity is free at time of
        // receipt; and an entity number outside the array is the client's invention
        move.CursorEntity = host.IsLive(cursorEntity) ? cursorEntity : 0;
        if (msg.BadRead) return;
        MovesReceived++;

        // if the previous move has not been applied yet, we need to accumulate the impulse/buttons
        // from it
        if (!client.Cmd.ApplyMove)
        {
            if (move.Impulse == 0) move.Impulse = client.Cmd.Impulse;
            move.Buttons |= client.Cmd.Buttons;
        }

        // now store this move for later execution (a packet carrying more than 128 moves is not one
        // a DarkPlaces client sends; the excess is dropped)
        if (_numReadMoves < MaxUserCmds) _readMoves[_numReadMoves++] = move;

        // movement packet loss tracking
        if (move.Sequence != 0 && client.Begun)
        {
            if (move.Sequence > client.MovementHighestSequenceSeen)
            {
                if (client.MovementHighestSequenceSeen != 0)
                {
                    // mark moves in between as lost
                    uint delta = move.Sequence - client.MovementHighestSequenceSeen - 1;
                    if (delta < NetGraphPackets)
                        for (uint u = 0; u < delta; u++) client.MovementCount[(client.MovementHighestSequenceSeen + 1 + u) % NetGraphPackets] = -1;
                    else Array.Fill(client.MovementCount, -1);
                }
                // mark THIS move as received
                client.MovementCount[move.Sequence % NetGraphPackets] = 1;
                client.MovementHighestSequenceSeen = move.Sequence;
            }
            else if (client.MovementCount[move.Sequence % NetGraphPackets] >= 0) client.MovementCount[move.Sequence % NetGraphPackets]++;
        }
        else
        {
            client.MovementHighestSequenceSeen = 0;
            Array.Clear(client.MovementCount);
        }
    }

    // SV_ExecuteClientMoves: apply the moves this message carried - each as its own physics step if
    // the client predicts (DP_SV_PLAYERPHYSICS), else the newest one for the next server frame.
    private void ExecuteClientMoves(SvqcHost host, SvClient client, SvNetConnection connection)
    {
        if (_numReadMoves < 1) return;
        // only start accepting input once the player is spawned
        if (!client.Begun) return;
        ref SvUserCmd last = ref _readMoves[_numReadMoves - 1];
        float inputTimeout = Cvar("sv_clmovement_inputtimeout", 0.1f);
        // disable clientside movement prediction in some cases
        if (Math.Ceiling(Math.Max(last.ReceiveTime - last.Time, 0) * 1000.0) < Cvar("sv_clmovement_minping", 0))
            connection.ClMovementDisableTimeout = RealTime + Cvar("sv_clmovement_minping_disabletime", 1000) / 1000.0;
        float disablePrediction = host.Fl(client.Edict, host.F.DisableClientPrediction);
        // several conditions govern whether clientside movement prediction is allowed
        if (last.Sequence != 0 && Cvar("sv_clmovement_enable", 1) != 0 && inputTimeout > 0 && connection.ClMovementDisableTimeout <= RealTime
            && (disablePrediction == -1 || (host.Fl(client.Edict, host.F.MoveType) == SvqcHost.MoveTypeWalk && disablePrediction == 0)))
        {
            // process the moves in order and ignore old ones; but always trust the latest move (this
            // deals with bogus initial move sequences after level change, where the client will
            // eventually catch up with the level change and begin predicting properly)
            for (int moveIndex = 0; moveIndex < _numReadMoves; moveIndex++)
            {
                ref SvUserCmd move = ref _readMoves[moveIndex];
                if (client.MoveSequence >= move.Sequence && moveIndex != _numReadMoves - 1) continue;
                // this is a new move
                move.Time = (float)Math.Clamp(move.Time, host.Time - 1, host.Time);   // prevent slowhack/speedhack combos
                move.Time = MathF.Max(move.Time, client.Cmd.Time);                    // prevent backstepping of time
                // limit moveframetime to the next multiple of the ticrate past the input timeout
                double ticLimit = host._cv.TicRate > 0 && host.FrameTime > 0 ? host.FrameTime * Math.Ceiling(inputTimeout / host.FrameTime) : inputTimeout;
                double moveFrameTime = Math.Min(move.Time - client.Cmd.Time, Math.Min(0.1, ticLimit));
                // discard (treat like lost) moves with too low distance from the previous one to
                // prevent hacks using float inaccuracy to cause strange stuff to happen
                if (!(moveFrameTime >= 0.0005))
                {
                    // count the move as LOST if we don't execute it but it has higher sequence count
                    if (client.MoveSequence != 0 && move.Sequence > client.MoveSequence) client.MovementCount[move.Sequence % NetGraphPackets] = -1;
                    continue;
                }
                client.Cmd = move;
                client.MoveSequence = move.Sequence;
                client.Ping = client.Cmd.ReceiveTime - client.Cmd.Time;
                // if using prediction, we need to perform moves when packets are received
                if (moveFrameTime > 0.05)
                {
                    host.ClientMove(client, moveFrameTime * 0.5);
                    host.ClientMove(client, moveFrameTime * 0.5);
                }
                else host.ClientMove(client, moveFrameTime);
                if (!client.Active) return;
                client.ClMovementInputTimeout = MathF.Min(0.1f, inputTimeout);
            }
        }
        else
        {
            // try to gather button bits from old moves, but only if their time is advancing (ones the
            // server already got are ignored)
            for (int moveIndex = 0; moveIndex < _numReadMoves - 1; moveIndex++)
            {
                ref SvUserCmd move = ref _readMoves[moveIndex];
                if (client.Cmd.Time < move.Time)
                {
                    last.Buttons |= move.Buttons;
                    if (move.Impulse != 0) last.Impulse = move.Impulse;
                }
            }
            float sentTime = last.Time;
            // now copy the new move
            client.Cmd = last;
            client.Cmd.Time = MathF.Max(client.Cmd.Time, (float)host.Time);
            // physics will run up to sv.time worth of simulation
            client.MoveSequence = 0;
            // make sure that normal physics takes over immediately
            client.ClMovementInputTimeout = 0;
            client.Ping = client.Cmd.ReceiveTime - sentTime;
        }
        if (!float.IsFinite(client.Ping)) client.Ping = 0;
    }

    // ---- commands a client may execute -----------------------------------------------------------------

    /// <summary>
    /// Cmd_ExecuteString(cmd_serverfromclient, ...): run one command line on a client's behalf - from
    /// its clc_stringcmd, or from the program's clientcommand() for a line it did not handle itself.
    /// Only the commands DarkPlaces lets a client execute exist here; anything else is ignored.
    /// </summary>
    public void ExecuteClientCommand(SvClient client, string line)
    {
        if (Host is not { } host || !client.Active) return;
        List<string> argv = DpStuffText.Tokenize(line);
        if (argv.Count == 0) return;
        SvNetConnection? connection = client.Connection as SvNetConnection;
        string args = DpStuffText.ArgsAfterFirst(line);
        switch (argv[0].ToLowerInvariant())
        {
            case "prespawn": PreSpawn(host, client, connection); break;
            case "spawn": Spawn(host, client, connection); break;
            case "begin": Begin(client); break;
            case "name": Name(host, client, connection, argv.Count == 2 ? argv[1] : args, argv.Count); break;
            case "color": Color(host, client, argv); break;
            case "rate": client.Rate = argv.Count > 1 ? DpStuffText.Atoi(argv[1]) : 0; break;
            case "rate_burstsize": if (argv.Count == 2) client.RateBurst = DpStuffText.Atoi(argv[1]); break;
            case "playermodel": if (argv.Count > 1) PlayerModel(host, client, argv.Count == 2 ? argv[1] : args, skin: false); break;
            case "playerskin": if (argv.Count > 1) PlayerModel(host, client, argv.Count == 2 ? argv[1] : args, skin: true); break;
            case "pmodel": break;   // Nehahra only
            case "kill":
                if (host.Fl(client.Edict, host.F.Health) <= 0) host.ClientPrint(client, "Can't suicide -- already dead!\n");
                else
                {
                    host.SetTime(host.Time);
                    host.Self = client.Edict;
                    host.Exec(host.Fn.ClientKill, "QC function ClientKill is missing");
                }
                break;
            case "say":
            case "say_team":
                Say(host, client, args, team: argv[0].Length > 3);
                break;
            case "ping":
                host.ClientPrint(client, "Client ping times:\n");
                foreach (SvClient other in _clients)
                    if (other.Active) host.ClientPrint(client, string.Create(CultureInfo.InvariantCulture, $"{(int)MathF.Floor(other.Ping * 1000 + 0.5f),4} {other.Name}\n"));
                break;
            case "pings":
            {
                // the "pingplreport" lines a DarkPlaces scoreboard asks for
                foreach (SvClient other in _clients)
                {
                    if (!other.Active) continue;
                    int ping = Math.Clamp((int)MathF.Floor(other.Ping * 1000 + 0.5f), 0, 9999), movementLoss = 0;
                    foreach (int count in other.MovementCount) if (count < 0) movementLoss++;
                    host.ClientCommands(client, string.Create(CultureInfo.InvariantCulture, $"pingplreport {other.Index} {ping} 0 {movementLoss * 100 / NetGraphPackets}\n"));
                }
                break;
            }
            case "status": host.ClientPrint(client, host.StatusText()); break;
            case "download":
            case "sv_startdownload":
                if (connection is null) break;
                connection.Download ??= NewDownload(host);
                if (connection.Download.HandleCommand(line, connection.Message) && connection.Download.Active)
                    // "host_client->sendsignon = true": make sure this message is sent
                    if (client.SendSignon == 2) client.SendSignon = 0;
                break;
            // pause, tell, god, notarget, fly, noclip, give and the ent_* commands exist in DarkPlaces'
            // table; Xonotic handles what it wants of them in QuakeC and the rest need sv_cheats.
        }
    }

    private SvDownload NewDownload(SvqcHost host)
    {
        SvDownloadSettings settings = new()
        {
            AllowDownloads = Cvar("sv_allowdownloads", 1) != 0,
            AllowInArchive = Cvar("sv_allowdownloads_inarchive", 0) != 0,
            AllowArchive = Cvar("sv_allowdownloads_archive", 0) != 0,
            AllowConfig = Cvar("sv_allowdownloads_config", 0) != 0,
            AllowDlCache = Cvar("sv_allowdownloads_dlcache", 0) != 0,
        };
        // Only the client program is served. DarkPlaces also serves loose files of the game directory
        // (and, by cvar, files inside packs); this server has no notion of "loose" yet, so until it
        // does it serves nothing else - which is the conservative end of sv_allowdownloads_inarchive 0.
        return new SvDownload(settings, _ => null, () => _csqcProgram);
    }

    // SV_PreSpawn_f
    private static void PreSpawn(SvqcHost host, SvClient client, SvNetConnection? connection)
    {
        if (client.PreSpawned) return;   // "prespawn not valid -- already prespawned"
        client.PreSpawned = true;
        if (connection is null) return;
        connection.Message.WriteBytes(host.Signon.WrittenSpan);
        connection.Message.WriteByte((int)Svc.SignonNum);
        connection.Message.WriteByte(2);
        client.SendSignon = 0;   // enable unlimited sends again
        // reset the name change timer because the client will send name soon
        connection.NameTime = 0;
    }

    // SV_Spawn_f
    private void Spawn(SvqcHost host, SvClient client, SvNetConnection? connection)
    {
        if (!client.PreSpawned || client.Spawned) return;   // "Spawn not valid"
        client.Spawned = true;
        // reset name change timer again because they might want to change name again in the first 5 seconds after connecting
        if (connection is not null) connection.NameTime = 0;

        // set up the edict; copy spawn parms out of the client_t
        for (int i = 0; i < SvClient.NumSpawnParms; i++) host.Vm.GlobalFloat(host.G.Parm1 + i) = client.SpawnParms[i];
        // call the spawn function
        client.ClientConnectCalled = true;
        host.SetTime(host.Time);
        host.Self = client.Edict;
        host.Exec(host.Fn.ClientConnect, "QC function ClientConnect is missing");
        if (!client.Active) return;   // the program may refuse a client by dropping it
        Print($"{Printable(client.Name)} connected\n");
        host.SetTime(host.Time);
        host.Self = client.Edict;
        host.Exec(host.Fn.PutClientInServer, "QC function PutClientInServer is missing");
        if (!client.Active || client.Connection is not SvNetConnection live) return;
        DpMessageWriter msg = live.Message;

        // send time of update
        msg.WriteByte((int)Svc.Time);
        msg.WriteFloat((float)host.Time);

        // send all current names, colors, and frag counts
        foreach (SvClient other in _clients)
        {
            if (!other.Active) continue;
            msg.WriteByte((int)Svc.UpdateName);
            msg.WriteByte(other.Index);
            msg.WriteString(other.Name);
            msg.WriteByte((int)Svc.UpdateFrags);
            msg.WriteByte(other.Index);
            msg.WriteShort(other.Frags);
            msg.WriteByte((int)Svc.UpdateColors);
            msg.WriteByte(other.Index);
            msg.WriteByte(other.Colors);
        }

        // send all current light styles
        for (int i = 0; i < host.LightStyles.Length; i++)
        {
            if (host.LightStyles[i].Length == 0) continue;
            msg.WriteByte((int)Svc.LightStyle);
            msg.WriteByte(i);
            msg.WriteString(host.LightStyles[i]);
        }

        // send some stats: STAT_TOTALSECRETS 11, STAT_TOTALMONSTERS 12, STAT_SECRETS 13, STAT_MONSTERS 14
        WriteStat(msg, 11, QcVm.FloatToInt(host.Vm.GlobalFloat(host.G.TotalSecrets)));
        WriteStat(msg, 12, QcVm.FloatToInt(host.Vm.GlobalFloat(host.G.TotalMonsters)));
        WriteStat(msg, 13, QcVm.FloatToInt(host.Vm.GlobalFloat(host.G.FoundSecrets)));
        WriteStat(msg, 14, QcVm.FloatToInt(host.Vm.GlobalFloat(host.G.KilledMonsters)));

        // send a fixangle. Never send a roll angle, because savegames can catch the server in a state
        // where it is rolled
        QcVector angles = host.Vec(client.Edict, host.F.Angles);
        msg.WriteByte((int)Svc.SetAngle);
        msg.WriteAngle(angles.X);
        msg.WriteAngle(angles.Y);
        msg.WriteAngle(0);

        Span<int> stats = stackalloc int[DpProtocol.MaxClStats];
        WriteClientData(host, client, live, msg, stats);

        msg.WriteByte((int)Svc.SignonNum);
        msg.WriteByte(3);
    }

    private static void WriteStat(DpMessageWriter msg, int index, int value)
    {
        msg.WriteByte((int)Svc.UpdateStat);
        msg.WriteByte(index);
        msg.WriteLong(value);
    }

    // SV_Begin_f
    private static void Begin(SvClient client)
    {
        if (!client.Spawned || client.Begun) return;   // "Begin not valid"
        client.Begun = true;
    }

    // SV_Name_f: sanitise a name the way DarkPlaces does - no line breaks, no leading chat marker,
    // colour codes closed - and limit how often it may change.
    private void Name(SvqcHost host, SvClient client, SvNetConnection? connection, string source, int argc)
    {
        if (argc == 1) return;
        string newName = Truncate(source, SvClient.MaxNameLength - 1);
        double timer = Math.Max(0.0, Cvar("sv_namechangetimer", 5));
        if (connection is not null)
        {
            if (RealTime < connection.NameTime && newName != client.Name)
            {
                host.ClientPrint(client, string.Create(CultureInfo.InvariantCulture, $"You can't change name more than once every {timer:0.0} seconds!\n"));
                return;
            }
            connection.NameTime = RealTime + timer;
        }

        // point the string back at updateclient->name to keep it safe
        StringBuilder name = new(newName.Length + 4);
        foreach (char c in newName)
            if (c != '\r' && c != '\n') name.Append(c);
        if (name.Length > 0 && (name[0] == '\x01' || name[0] == '\x02'))
        {
            // a leading chat marker would make the name print as a chat line: neutralise it
            name.Insert(0, "^7");
        }
        // an unfinished colour code at the end would eat whatever is printed after the name
        if (name.Length > 0 && name[^1] == '^' && !EndsWithEscapedCaret(name)) name.Append('^');
        // find the last color tag offset and decide if we need to add a reset tag
        int lastColor = -1;
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != '^' || i + 1 >= name.Length) continue;
            if (name[i + 1] >= '0' && name[i + 1] <= '9')
            {
                lastColor = name[i + 1] == '7' ? -1 : i;
                i++;
            }
            else if (name[i + 1] == 'x' && i + 4 < name.Length && Uri.IsHexDigit(name[i + 2]) && Uri.IsHexDigit(name[i + 3]) && Uri.IsHexDigit(name[i + 4]))
            {
                lastColor = i;
                i += 4;
            }
            else if (name[i + 1] == '^') i++;
        }
        // add the reset tag if needed
        if (lastColor >= 0) name.Append("^7");
        client.Name = Truncate(name.ToString(), SvClient.MaxNameLength - 1);
        UpdateName(host, client);
    }

    private static bool EndsWithEscapedCaret(StringBuilder name)
    {
        int carets = 0;
        for (int i = name.Length - 1; i >= 0 && name[i] == '^'; i--) carets++;
        return carets % 2 == 0;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    // SV_Name: the entity's netname follows the slot, and everyone is told of a change.
    private void UpdateName(SvqcHost host, SvClient client)
    {
        host.SetSlotName(client);
        if (client.OldName == client.Name) return;
        if (client.Begun && client.Connection is not null) host.BroadcastPrint($"\x03{client.OldName} ^7changed name to ^3{client.Name}\n");
        client.OldName = client.Name;
        // send notification to all clients
        host.ReliableDatagram.WriteByte((int)Svc.UpdateName);
        host.ReliableDatagram.WriteByte(client.Index);
        host.ReliableDatagram.WriteString(client.Name);
    }

    // SV_Color_f
    private static void Color(SvqcHost host, SvClient client, List<string> argv)
    {
        int top = (argv.Count > 1 ? DpStuffText.Atoi(argv[1]) : 0) & 15, bottom = (argv.Count > 2 ? DpStuffText.Atoi(argv[2]) : 0) & 15;
        int playerColor = top * 16 + bottom;
        if (host.Fn.SvChangeTeam != 0)
        {
            host.Vm.SetArgFloat(0, playerColor);
            host.SetTime(host.Time);
            host.Self = client.Edict;
            host.Vm.Execute(host.Fn.SvChangeTeam, 1);
            return;
        }
        host.Fl(client.Edict, host.F.ClientColors) = playerColor;
        host.Fl(client.Edict, host.F.Team) = bottom + 1;
        client.Colors = playerColor;
        if (client.OldColors == client.Colors) return;
        client.OldColors = client.Colors;
        // send notification to all clients
        host.ReliableDatagram.WriteByte((int)Svc.UpdateColors);
        host.ReliableDatagram.WriteByte(client.Index);
        host.ReliableDatagram.WriteByte(client.Colors);
    }

    // SV_Playermodel_f / SV_Playerskin_f
    private static void PlayerModel(SvqcHost host, SvClient client, string source, bool skin)
    {
        StringBuilder path = new(Math.Min(source.Length, DpProtocol.MaxQPath));
        foreach (char c in source)
        {
            if (path.Length >= DpProtocol.MaxQPath - 1) break;
            if (c != '\r' && c != '\n') path.Append(c);
        }
        if (skin) client.PlayerSkin = client.OldSkin = path.ToString();
        else client.PlayerModel = client.OldModel = path.ToString();
        host.SetSlotModel(client);
    }

    // SV_Say from a client: only reached when the program has no SV_ParseClientCommand (Xonotic's
    // has, and handles chat itself).
    private void Say(SvqcHost host, SvClient client, string text, bool team)
    {
        if (text.Length == 0) return;
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        text = Truncate(text, 1000);
        string line = $"\x01{client.Name}: {text}\n";
        foreach (SvClient other in _clients)
        {
            if (!other.Active || !other.Begun) continue;
            if (team && host.Fl(other.Edict, host.F.Team) != host.Fl(client.Edict, host.F.Team)) continue;
            host.ClientPrint(other, line);
        }
    }
}
