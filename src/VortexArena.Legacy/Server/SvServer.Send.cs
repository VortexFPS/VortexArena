// Port of Base/darkplaces/sv_main.c SV_SendServerinfo; sv_send.c SV_PrepareEntityForSending,
// SV_PrepareEntitiesForSending, SV_CanSeeBox, SV_MarkWriteEntityStateToClient, SV_AddCameraEyes,
// SV_CleanupEnts, SV_WriteClientdataToMessage, SV_WriteUnreliableMessages, SV_SendClientDatagram,
// SV_UpdateToReliableMessages, SV_SendClientMessages; sv_ents.c SV_WriteEntitiesToClient; svvm_cmds.c
// VM_SV_UpdateCustomStats; sv_ents_csqc.c's call into the program (the SendEntity half of
// EntityFrameCSQC_WriteFrame, behind ISvCsqcEntityHost). The encoders themselves are SvEntityFrame5
// and SvCsqcEntityFrames.
using System.Numerics;
using System.Text;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

public sealed partial class SvServer
{
    // protocol.h RENDER_*, EF_*, PFLAGS_*
    private const int RenderStep = 1, RenderGlowTrail = 2, RenderViewModel = 4, RenderExteriorModel = 8, RenderLowPrecision = 16,
        RenderColorMapped = 32, RenderComplexAnimation = 128;
    private const int EfBrightField = 1, EfMuzzleFlash = 2, EfBrightLight = 4, EfDimLight = 8, EfNoDraw = 16, EfBlue = 64, EfRed = 128,
        EfFullBright = 512, EfFlame = 1024, EfStarDust = 2048, EfNoDepthTest = 8192, EfLowPrecision = 4194304;
    private const int PFlagsFullDynamic = 128;
    private const int NetMinRate = 1000;                 // NET_MINRATE
    private const int MaxClientNetworkEyes = 16;         // MAX_CLIENTNETWORKEYES
    private const int MaxLevelNetworkEyes = 512;         // MAX_LEVELNETWORKEYES
    private const int MaxEyeRecursion = 1;               // MAX_EYE_RECURSION: "increase if recursion gets supported by portals"
    private const int MaxLineOfSightTraces = 64;         // MAX_LINEOFSIGHTTRACES

    // sv.sendentities and its index by entity number, rebuilt once per send; the per-entity cull box.
    private SvEntityState[] _sendEntities = new SvEntityState[1024];
    private int[] _sendEntityIndex = new int[1024];
    private QcVector[] _cullMins = new QcVector[1024], _cullMaxs = new QcVector[1024];
    private int _numSendEntities;
    private int[] _consideration = new int[1024], _sent = new int[1024];
    private int _sentMark;
    private SvEntityState[] _sendStates = new SvEntityState[1024];
    private ushort[] _csqcNumbers = new ushort[1024];
    private readonly QcVector[] _eyes = new QcVector[MaxClientNetworkEyes];
    private int _numEyes;
    // sv.writeentitiestoclient_pvs: what the client's eyes can see between them, or null when the
    // map has no visibility data (then nothing is culled by it).
    private byte[]? _pvs;
    private byte[] _pvsBuffer = Array.Empty<byte>();
    private readonly int[] _cameras = new int[MaxLevelNetworkEyes];
    private readonly QcVector[] _cameraOrigins = new QcVector[MaxLevelNetworkEyes];
    private readonly int[] _eyeLevels = new int[MaxClientNetworkEyes];
    /// <summary>Entities kept from a client by the visibility set / by the line-of-sight traces, over all sends (sv_cullentities_stats).</summary>
    public long EntitiesCulledByPvs { get; private set; }
    public long EntitiesCulledByTrace { get; private set; }
    /// <summary>Eyes added behind warpzones and other cameras (SV_AddCameraEyes), over all sends.</summary>
    public long CameraEyesAdded { get; private set; }
    private int _toClientEntity;
    private SvClient? _toClient;
    private readonly DpMessageWriter _datagram = new(DpProtocol.NetMaxMessage);

    public long EntityFramesSent { get; private set; }
    public long EntityFramesSkipped { get; private set; }

    // ---- SV_SendServerinfo ----------------------------------------------------------------------------

    /// <summary>
    /// SV_SendServerinfo: "Sends the first message from the server to a connected client. This will
    /// be sent on the initial connection and upon each server load."
    /// </summary>
    private void SendServerInfo(SvClient client, SvNetConnection connection)
    {
        SvqcHost host = Host!;
        client.WeaponModel = "";
        client.WeaponModelIndex = 0;
        connection.LatestFrameNum = 0;
        client.Cmd = default;
        client.Cmd.Time = (float)host.Time;
        (connection.EntityDatabase ??= new SvEntityFrame5Database(this)).Reset();
        (connection.CsqcFrames ??= new SvCsqcEntityFrames(this)).Reset();
        Array.Clear(client.Stats);
        Array.Clear(client.StatsDeltaBits);

        DpMessageWriter msg = connection.Message;
        msg.Clear();
        msg.WriteByte((int)Svc.Print);
        msg.WriteString($"\nServer: Vortex legacy server (progs {host.ProgramCrc} crc)\n");

        if (host.CsqcProgName.Length > 0)
        {
            Stuff(msg, $"csqc_progname {host.CsqcProgName}\n");
            Stuff(msg, $"csqc_progsize {host.CsqcProgSize}\n");
            Stuff(msg, $"csqc_progcrc {host.CsqcProgCrc}\n");
            // if (sv.csqc_progname[0]) SV_InitCmd: commands the program wants every client to run first
            if (host.G.SvInitCmd >= 0 && host.Vm.GetString(host.Vm.GlobalInt(host.G.SvInitCmd)) is { Length: > 0 } initCommand)
                Stuff(msg, initCommand + "\n");
        }
        Stuff(msg, "cl_serverextension_download 2\n");
        // (Curl_SendRequirements: the "curl --pak" lines for HTTP downloads are not sent: no HTTP.)

        // send at this time so it's guaranteed to get executed at the right time
        msg.WriteByte((int)Svc.ServerInfo);
        msg.WriteLong(DpProtocol.ProtocolNumberDp7);
        msg.WriteByte(_clients.Length);
        // GAME_DEATHMATCH 1, GAME_COOP 0
        msg.WriteByte(Cvar("coop", 0) == 0 && Cvar("deathmatch", 0) != 0 ? 1 : 0);
        msg.WriteString(host.Vm.GetString(host.Vm.FieldInt(0, host.F.Message)));
        foreach (string model in host.PrecachedModels) msg.WriteString(model);
        msg.WriteByte(0);
        foreach (string sound in host.PrecachedSounds) msg.WriteString(sound);
        msg.WriteByte(0);

        // send music
        int track = QcVm.FloatToInt(host.Vm.FieldFloat(0, host.F.Sounds));
        msg.WriteByte((int)Svc.CdTrack);
        msg.WriteByte(track);
        msg.WriteByte(track);

        // set view; store this in clientcamera too
        client.ClientCamera = client.Edict;
        msg.WriteByte((int)Svc.SetView);
        msg.WriteShort(client.ClientCamera);

        msg.WriteByte((int)Svc.SignonNum);
        msg.WriteByte(1);

        client.PreSpawned = false;   // need prespawn, spawn, etc
        client.Spawned = false;
        client.Begun = false;
        client.SendSignon = 1;       // send this message, and increment to 2, 2 will be set to 0 by the prespawn command

        // clear movement info until client enters the new level properly
        client.MoveSequence = 0;
        client.MovementHighestSequenceSeen = 0;
        Array.Clear(client.MovementCount);
        client.Ping = 0;
        connection.LastMoveSequence = 0;
        connection.NumSkippedEntityFrames = 0;
        connection.UnreliableMsg.Clear();
        connection.UnreliableSplitPoints.Clear();
        connection.Download?.Abort();

        // allow the client some time to send his keepalives, even if map loading took ages
        connection.Timeout = RealTime + Cvar("net_connecttimeout", 15);
    }

    private static void Stuff(DpMessageWriter msg, string text)
    {
        msg.WriteByte((int)Svc.StuffText);
        msg.WriteString(text);
    }

    // ---- SV_SendClientMessages -------------------------------------------------------------------------

    private void SendClientMessages(SvqcHost host)
    {
        host.FlushBroadcastMessages();
        UpdateToReliableMessages(host);

        bool prepared = false;
        foreach (SvClient client in _clients)
        {
            if (!client.Active || client.Connection is not SvNetConnection connection) continue;
            if (connection.Message.Overflowed)
            {
                // if the message couldn't send, kick off
                host.DropClient(client, "Buffer overflow in net message", leaving: true);
                continue;
            }
            if (!prepared)
            {
                prepared = true;
                // only prepare entities once per frame
                PrepareEntitiesForSending(host);
            }
            SendClientDatagram(host, client, connection);
        }

        // SV_CleanupEnts: clear muzzle flashes
        QcVm vm = host.Vm;
        int effects = host.F.Effects;
        for (int e = 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e)) continue;
            int value = QcVm.FloatToInt(vm.FieldFloat(e, effects));
            if ((value & EfMuzzleFlash) != 0) vm.FieldFloat(e, effects) = value & ~EfMuzzleFlash;
        }
    }

    // SV_UpdateToReliableMessages: the slots follow what the program wrote into the player entities
    // (netname, clientcolors, playermodel, playerskin, clientcamera, frags), and what everyone must
    // know of a change goes to everyone.
    private void UpdateToReliableMessages(SvqcHost host)
    {
        QcVm vm = host.Vm;
        SvFieldOffsets f = host.F;
        foreach (SvClient client in _clients)
        {
            int e = client.Edict;
            // update the host_client fields we care about according to the entity fields
            // DP_SV_CLIENTNAME
            string name = vm.GetString(vm.FieldInt(e, f.NetName));
            if (name != client.Name) client.Name = Truncate(name, SvClient.MaxNameLength - 1);
            UpdateName(host, client);

            // DP_SV_CLIENTCOLORS
            client.Colors = QcVm.FloatToInt(vm.FieldFloat(e, f.ClientColors));
            if (client.OldColors != client.Colors)
            {
                client.OldColors = client.Colors;
                host.ReliableDatagram.WriteByte((int)Svc.UpdateColors);
                host.ReliableDatagram.WriteByte(client.Index);
                host.ReliableDatagram.WriteByte(client.Colors);
            }

            // NEXUIZ_PLAYERMODEL / NEXUIZ_PLAYERSKIN: always point the string back at host_client
            string model = vm.GetString(vm.FieldInt(e, f.PlayerModel)), skin = vm.GetString(vm.FieldInt(e, f.PlayerSkin));
            if (model != client.PlayerModel) client.PlayerModel = Truncate(model, DpProtocol.MaxQPath - 1);
            if (skin != client.PlayerSkin) client.PlayerSkin = Truncate(skin, DpProtocol.MaxQPath - 1);
            host.SetSlotModel(client);

            // TODO: add an extension name for this [1/17/2008 Black]
            int camera = vm.FieldInt(e, f.ClientCamera);
            if (camera > 0)
            {
                int old = client.ClientCamera;
                if (!host.IsLive(camera)) camera = e;
                client.ClientCamera = camera;
                if (old != client.ClientCamera && client.Connection is { } connection)
                {
                    connection.Message.WriteByte((int)Svc.SetView);
                    connection.Message.WriteShort(client.ClientCamera);
                }
            }

            // frags
            client.Frags = QcVm.FloatToInt(vm.FieldFloat(e, f.Frags));
            if (client.OldFrags != client.Frags)
            {
                client.OldFrags = client.Frags;
                // send notification to all clients
                host.ReliableDatagram.WriteByte((int)Svc.UpdateFrags);
                host.ReliableDatagram.WriteByte(client.Index);
                host.ReliableDatagram.WriteShort(client.Frags);
            }
        }

        // also send MSG_ALL to people who are past ClientConnect, but not spawned yet
        foreach (SvClient client in _clients)
            if (client.Connection is { } connection && (client.Begun || client.ClientConnectCalled))
                connection.Message.WriteBytes(host.ReliableDatagram.WrittenSpan);
        host.ReliableDatagram.Clear();
    }

    // ---- SV_SendClientDatagram ------------------------------------------------------------------------

    private void SendClientDatagram(SvqcHost host, SvClient client, SvNetConnection connection)
    {
        DpNetChannel channel = connection.Channel;
        if (!channel.CanSend(RealTime)) return;

        // limit the rate to the highest known good rate for the sake of old clients
        int maxRate = Math.Max(NetMinRate, (int)Cvar("sv_maxrate", 1000000));
        int clientRate = Math.Clamp(client.Rate, NetMinRate, maxRate);
        // DP5 and later protocols support packet size limiting, which is a better method than
        // limiting packet frequency as QW does: this rate limiting does not understand packet loss
        // (it adds resentful resends), so it's best to leave some headroom
        double timeDelta = client.RateBurst / (double)Math.Max(1, client.Rate);
        timeDelta *= 1 - Cvar("net_burstreserve", 0.3f);
        timeDelta = Math.Clamp(RealTime - channel.ClearTime, 0, Math.Max(0, timeDelta));
        timeDelta += host._cv.TicRate;
        int maxSize = Math.Clamp((int)(clientRate * timeDelta) - 28, 128, 1400);
        const int maxSize2 = 1400;
        // csqc entities can easily exceed 128 bytes, so disable throttling in mods that use csqc
        // (they are more robust anyway)
        int useSizeLimit = (int)Cvar("net_usesizelimit", 2);
        if (useSizeLimit == 1 ? host.CsqcProgSize > 0 : useSizeLimit < 1) maxSize = maxSize2;

        bool downloading = connection.Download is { Active: true };
        // while downloading, limit entity updates to half the packet (any leftover space is used)
        if (downloading) maxSize /= 2;

        DpMessageWriter msg = _datagram;
        msg.Clear();
        if (client.Begun)
        {
            // the client has spawned: send the full update
            msg.WriteByte((int)Svc.Time);
            msg.WriteFloat((float)host.Time);

            // add the client specific data to the datagram
            Span<int> stats = client.Stats;
            WriteClientData(host, client, connection, msg, stats);
            // now update the stats[] array using any registered custom fields
            UpdateCustomStats(host, client.Edict, stats);
            // set host_client->statsdeltabits
            connection.EntityDatabase?.UpdateStats(stats);

            // add as many queued unreliable messages (effects) as we can fit, limited to half of the
            // maximum packet size because the client may need to update its entities too
            if (connection.UnreliableMsg.Length > 0) WriteUnreliableMessages(connection, msg, maxSize / 2, maxSize2);
            // now write as many entities as we can fit, and also sends stats
            WriteEntitiesToClient(host, client, connection, msg, maxSize);
        }
        else if (RealTime > connection.KeepAliveTime)
        {
            // the player isn't totally in the game yet: send small keepalive messages if too much
            // time has passed (also keeps the client's connection from timing out during loading)
            connection.KeepAliveTime = RealTime + 5;
            msg.WriteChar((int)Svc.Nop);
        }

        // if a download is active, see if there is room to fit some download data (leave room for
        // the header and the time stamp)
        if (downloading) connection.Download!.WriteData(msg, maxSize, maxSize2);

        // send the datagram
        channel.SuppressNewReliables = client.SendSignon == 2;
        _scratch.Clear();
        // NetConn_SendUnreliableMessage: nothing unreliable to say still lets a reliable message go
        channel.Transmit(msg.Overflowed ? ReadOnlySpan<byte>.Empty : msg.WrittenSpan, RealTime, _scratch, clientRate, client.RateBurst);
        foreach (byte[] datagram in _scratch) Send(connection, datagram);
        if (client.SendSignon == 1 && connection.Message.Length == 0)
            client.SendSignon = 2;   // prevent reliable until client sends prespawn (this is the keepalive phase)
    }

    // SV_WriteUnreliableMessages: as many whole queued messages as fit, never part of one.
    private static void WriteUnreliableMessages(SvNetConnection connection, DpMessageWriter msg, int maxSize, int maxSize2)
    {
        List<int> splits = connection.UnreliableSplitPoints;
        if (splits.Count == 0)
        {
            connection.UnreliableMsg.Clear();
            return;
        }
        // always accept the first one if it's within 1024 bytes, this ensures that very big
        // datagrams which are over the rate limit still get through the rate limit eventually
        int numSegments;
        for (numSegments = 1; numSegments < splits.Count; numSegments++)
            if (msg.Length + splits[numSegments] > maxSize) break;
        // the first segment gets an exemption from the rate limiting, otherwise it could get dropped
        // consistently due to a low rate limit
        if (numSegments == 1) maxSize = maxSize2;
        // the first segment gets to be bigger than the rate limit
        int split = splits[numSegments - 1];
        ReadOnlySpan<byte> queued = connection.UnreliableMsg.WrittenSpan;
        if (msg.Length + split <= maxSize) msg.WriteBytes(queued[..split]);
        // remove the part we sent, keeping any remaining data
        byte[] rest = queued[split..].ToArray();
        connection.UnreliableMsg.Clear();
        connection.UnreliableMsg.WriteBytes(rest);
        splits.RemoveRange(0, numSegments);
        for (int j = 0; j < splits.Count; j++) splits[j] -= split;
    }

    // SV_WriteClientdataToMessage, DP7: svc_damage, svc_setangle, then svc_clientdata with the view
    // height, punch and velocity; everything else a DP7 client knows about itself comes as stats.
    // (SV_SetIdealPitch, the Quake auto-pitch on slopes, is not ported: .idealpitch is whatever the
    // program leaves in it.) With sv_qcstats 0 DarkPlaces also fills the movement-variable stats 220
    // and up from its own cvars; that is not ported - Xonotic sets sv_qcstats 1 and sends them itself.
    private void WriteClientData(SvqcHost host, SvClient client, SvNetConnection connection, DpMessageWriter msg, Span<int> stats)
    {
        QcVm vm = host.Vm;
        SvFieldOffsets f = host.F;
        int ent = client.Edict;

        // send a damage message
        if (vm.FieldFloat(ent, f.DmgTake) != 0 || vm.FieldFloat(ent, f.DmgSave) != 0)
        {
            int other = host.EdictField(ent, f.DmgInflictor);
            QcVector origin = vm.FieldVector(other, f.Origin), mins = vm.FieldVector(other, f.Mins), maxs = vm.FieldVector(other, f.Maxs);
            msg.WriteByte(19 /* svc_damage */);
            msg.WriteByte(QcVm.FloatToInt(vm.FieldFloat(ent, f.DmgSave)));
            msg.WriteByte(QcVm.FloatToInt(vm.FieldFloat(ent, f.DmgTake)));
            msg.WriteCoord(origin.X + 0.5f * (mins.X + maxs.X));
            msg.WriteCoord(origin.Y + 0.5f * (mins.Y + maxs.Y));
            msg.WriteCoord(origin.Z + 0.5f * (mins.Z + maxs.Z));
            vm.FieldFloat(ent, f.DmgTake) = 0;
            vm.FieldFloat(ent, f.DmgSave) = 0;
        }

        // a fixangle might get lost in a dropped packet. Oh well.
        if (vm.FieldFloat(ent, f.FixAngle) != 0)
        {
            // angle fixing was requested by global thinking code...so store the current angles for later use
            client.FixAngleAngles = vm.FieldVector(ent, f.Angles);
            client.FixAngleAnglesSet = true;
            // and clear fixangle for the next frame
            vm.FieldFloat(ent, f.FixAngle) = 0;
        }
        if (client.FixAngleAnglesSet)
        {
            msg.WriteByte((int)Svc.SetAngle);
            msg.WriteAngle(client.FixAngleAngles.X);
            msg.WriteAngle(client.FixAngleAngles.Y);
            msg.WriteAngle(client.FixAngleAngles.Z);
            client.FixAngleAnglesSet = false;
        }

        // the runes are in serverflags, pack them into the items value, also pack in the items2 value
        // for mission pack huds (used only in the mission packs, which do not use serverflags)
        int items = QcVm.FloatToInt(vm.FieldFloat(ent, f.Items)) | ((QcVm.FloatToInt(vm.FieldFloat(ent, f.Items2)) & ((1 << 9) - 1)) << 23)
            | ((QcVm.FloatToInt(vm.GlobalFloat(host.G.ServerFlags)) & ((1 << 4) - 1)) << 28);
        QcVector punchVector = vm.FieldVector(ent, f.PunchVector), punchAngle = vm.FieldVector(ent, f.PunchAngle), velocity = vm.FieldVector(ent, f.Velocity);

        // cache weapon model name and index in client struct to save time (this could be done
        // elsewhere, but here seems as good a place as any)
        string weaponModel = vm.GetString(vm.FieldInt(ent, f.WeaponModel));
        if (weaponModel != client.WeaponModel)
        {
            client.WeaponModel = weaponModel;
            client.WeaponModelIndex = host.ModelIndex(weaponModel, 1);
        }

        // LadyHavoc: viewzoom is a byte (0-255) where 255 is 1.0x
        int viewZoom = QcVm.FloatToInt(vm.FieldFloat(ent, f.ViewZoom) * 255.0f);
        if (viewZoom == 0) viewZoom = 255;

        int bits = 0;
        if ((QcVm.FloatToInt(vm.FieldFloat(ent, f.Flags)) & SvqcHost.FlOnGround) != 0) bits |= DpProtocol.SuOnGround;
        if (vm.FieldFloat(ent, f.WaterLevel) >= 2) bits |= DpProtocol.SuInWater;
        if (vm.FieldFloat(ent, f.IdealPitch) != 0) bits |= DpProtocol.SuIdealPitch;
        if (punchAngle.X != 0) bits |= DpProtocol.SuPunch1;
        if (punchAngle.Y != 0) bits |= DpProtocol.SuPunch1 << 1;
        if (punchAngle.Z != 0) bits |= DpProtocol.SuPunch1 << 2;
        if (punchVector.X != 0) bits |= DpProtocol.SuPunchVec1;
        if (punchVector.Y != 0) bits |= DpProtocol.SuPunchVec1 << 1;
        if (punchVector.Z != 0) bits |= DpProtocol.SuPunchVec1 << 2;
        if (velocity.X != 0) bits |= DpProtocol.SuVelocity1;
        if (velocity.Y != 0) bits |= DpProtocol.SuVelocity1 << 1;
        if (velocity.Z != 0) bits |= DpProtocol.SuVelocity1 << 2;

        stats.Clear();
        stats[16] = QcVm.FloatToInt(vm.FieldVector(ent, f.ViewOfs).Z);             // STAT_VIEWHEIGHT
        stats[15] = items;                                                         // STAT_ITEMS
        stats[5] = QcVm.FloatToInt(vm.FieldFloat(ent, f.WeaponFrame));             // STAT_WEAPONFRAME
        stats[4] = QcVm.FloatToInt(vm.FieldFloat(ent, f.ArmorValue));              // STAT_ARMOR
        stats[2] = client.WeaponModelIndex;                                        // STAT_WEAPON
        stats[0] = QcVm.FloatToInt(vm.FieldFloat(ent, f.Health));                  // STAT_HEALTH
        stats[3] = QcVm.FloatToInt(vm.FieldFloat(ent, f.CurrentAmmo));             // STAT_AMMO
        stats[6] = QcVm.FloatToInt(vm.FieldFloat(ent, f.AmmoShells));              // STAT_SHELLS
        stats[7] = QcVm.FloatToInt(vm.FieldFloat(ent, f.AmmoNails));               // STAT_NAILS
        stats[8] = QcVm.FloatToInt(vm.FieldFloat(ent, f.AmmoRockets));             // STAT_ROCKETS
        stats[9] = QcVm.FloatToInt(vm.FieldFloat(ent, f.AmmoCells));               // STAT_CELLS
        stats[10] = QcVm.FloatToInt(vm.FieldFloat(ent, f.Weapon));                 // STAT_ACTIVEWEAPON
        stats[21] = viewZoom;                                                      // STAT_VIEWZOOM
        stats[11] = QcVm.FloatToInt(vm.GlobalFloat(host.G.TotalSecrets));          // STAT_TOTALSECRETS
        stats[12] = QcVm.FloatToInt(vm.GlobalFloat(host.G.TotalMonsters));         // STAT_TOTALMONSTERS
        // (STAT_SECRETS and STAT_MONSTERS are sent by svc_killedmonster / svc_foundsecret.)

        if (stats[16] != 22 /* DEFAULT_VIEWHEIGHT */) bits |= DpProtocol.SuViewHeight;
        if (bits >= 65536) bits |= DpProtocol.SuExtend1;
        if (bits >= 16777216) bits |= DpProtocol.SuExtend2;

        // send the data
        msg.WriteByte((int)Svc.ClientData);
        msg.WriteShort(bits);
        if ((bits & DpProtocol.SuExtend1) != 0) msg.WriteByte(bits >> 16);
        if ((bits & DpProtocol.SuExtend2) != 0) msg.WriteByte(bits >> 24);
        if ((bits & DpProtocol.SuViewHeight) != 0) msg.WriteChar(stats[16]);
        if ((bits & DpProtocol.SuIdealPitch) != 0) msg.WriteChar(QcVm.FloatToInt(vm.FieldFloat(ent, f.IdealPitch)));
        for (int i = 0; i < 3; i++)
        {
            if ((bits & (DpProtocol.SuPunch1 << i)) != 0) msg.WriteAngle16i(i == 0 ? punchAngle.X : i == 1 ? punchAngle.Y : punchAngle.Z);
            if ((bits & (DpProtocol.SuPunchVec1 << i)) != 0) msg.WriteFloat(i == 0 ? punchVector.X : i == 1 ? punchVector.Y : punchVector.Z);
            if ((bits & (DpProtocol.SuVelocity1 << i)) != 0) msg.WriteFloat(i == 0 ? velocity.X : i == 1 ? velocity.Y : velocity.Z);
        }
    }

    // VM_SV_UpdateCustomStats: the stats the program registered with addstat, read from the player's entity.
    private static void UpdateCustomStats(SvqcHost host, int ent, Span<int> stats)
    {
        QcVm vm = host.Vm;
        Span<byte> text = stackalloc byte[16];
        for (int i = 32 /* MIN_VM_STAT */; i <= host.CustomStatsLast && i < stats.Length; i++)
        {
            (byte type, int field) = host.CustomStats[i];
            switch (type)
            {
                case 1:   // string: 16 bytes over four stats
                {
                    if (i + 3 >= stats.Length) break;
                    text.Clear();
                    string s = vm.GetString(vm.FieldInt(ent, field));
                    // dp_strlcpy(s, ..., 16): at most 15 bytes and a terminator
                    Span<byte> utf8 = stackalloc byte[64];
                    int length = Encoding.UTF8.GetBytes(s.AsSpan(0, Math.Min(s.Length, 15)), utf8);
                    utf8[..Math.Min(length, 15)].CopyTo(text);
                    for (int k = 0; k < 4; k++) stats[i + k] = text[k * 4] | (text[k * 4 + 1] << 8) | (text[k * 4 + 2] << 16) | (text[k * 4 + 3] << 24);
                    break;
                }
                case 8:   // float, sent as its bits
                    stats[i] = vm.FieldInt(ent, field);
                    break;
                case 2:   // integer: truncate
                    stats[i] = QcVm.FloatToInt(vm.FieldFloat(ent, field));
                    break;
            }
        }
    }

    // ---- entities --------------------------------------------------------------------------------------

    private void EnsureSendCapacity(int numEdicts)
    {
        if (_sendEntityIndex.Length >= numEdicts) return;
        int size = Math.Min(DpProtocol.MaxEdicts, Math.Max(numEdicts, _sendEntityIndex.Length * 2));
        Array.Resize(ref _sendEntities, size);
        Array.Resize(ref _sendEntityIndex, size);
        Array.Resize(ref _cullMins, size);
        Array.Resize(ref _cullMaxs, size);
        Array.Resize(ref _consideration, size);
        Array.Resize(ref _sent, size);
        Array.Resize(ref _sendStates, size);
        Array.Resize(ref _csqcNumbers, size);
    }

    // SV_PrepareEntitiesForSending
    private void PrepareEntitiesForSending(SvqcHost host, bool collectSendFlags = true)
    {
        QcVm vm = host.Vm;
        EnsureSendCapacity(vm.NumEdicts);
        _numSendEntities = 0;
        Array.Fill(_sendEntityIndex, -1, 0, vm.NumEdicts);
        _toClientEntity = 0;
        for (int e = 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e) || !PrepareEntityForSending(host, e, ref _sendEntities[_numSendEntities], collectSendFlags)) continue;
            _sendEntityIndex[e] = _numSendEntities++;
        }
    }

    // SV_PrepareEntityForSending: the wire state of one entity, or false for one no client is told of.
    private bool PrepareEntityForSending(SvqcHost host, int e, ref SvEntityState state, bool collectSendFlags)
    {
        QcVm vm = host.Vm;
        SvFieldOffsets f = host.F;
        bool sendEntity = vm.FieldInt(e, f.SendEntity) != 0;
        // this 2 billion unit check is actually to detect NAN origins (we really don't want to send those)
        QcVector origin = vm.FieldVector(e, f.Origin);
        if (!(origin.X * (double)origin.X + origin.Y * (double)origin.Y + origin.Z * (double)origin.Z < 2000000000.0 * 2000000000.0)) return false;

        // EF_NODRAW prevents sending for any reason except for your own client, so we must keep all clients in this superset
        uint effects = FloatToUInt(vm.FieldFloat(e, f.Effects));

        // we can omit invisible entities with no effects that are not clients
        // LadyHavoc: this could kill tags attached to an invisible entity, I just hope no one else is as weird as me...
        int index = QcVm.FloatToInt(vm.FieldFloat(e, f.ModelIndex));
        int modelIndex = index >= 1 && index < host.ModelCount && vm.GetString(vm.FieldInt(e, f.Model)).Length > 0 ? index : 0;

        int flags = 0;
        int glowSize = Math.Clamp(QcVm.FloatToInt(vm.FieldFloat(e, f.GlowSize) * 0.25f), 0, 255);
        if (vm.FieldFloat(e, f.GlowTrail) != 0) flags |= RenderGlowTrail;
        if (vm.FieldInt(e, f.ViewModelForClient) != 0) flags |= RenderViewModel;

        QcVector color = vm.FieldVector(e, f.Color);
        int light0 = (int)Math.Clamp(color.X * 256, 0, 65535), light1 = (int)Math.Clamp(color.Y * 256, 0, 65535), light2 = (int)Math.Clamp(color.Z * 256, 0, 65535);
        int light3 = (int)Math.Clamp(vm.FieldFloat(e, f.LightLev), 0, 65535);
        int lightStyle = (byte)QcVm.FloatToInt(vm.FieldFloat(e, f.Style)), lightPFlags = (byte)QcVm.FloatToInt(vm.FieldFloat(e, f.PFlags));

        int specialVisibilityRadius = 0;
        if ((lightPFlags & PFlagsFullDynamic) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, light3);
        if (glowSize != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, glowSize * 4);
        if ((flags & RenderGlowTrail) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 100);
        if ((effects & (EfBrightField | EfMuzzleFlash | EfBrightLight | EfDimLight | EfRed | EfBlue | EfFlame | EfStarDust)) != 0)
        {
            if ((effects & EfBrightField) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 80);
            if ((effects & EfMuzzleFlash) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 100);
            if ((effects & EfBrightLight) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 400);
            if ((effects & EfDimLight) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 200);
            if ((effects & EfRed) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 200);
            if ((effects & EfBlue) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 200);
            if ((effects & EfFlame) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 250);
            if ((effects & EfStarDust) != 0) specialVisibilityRadius = Math.Max(specialVisibilityRadius, 100);
        }

        // early culling checks (final culling is done by SV_MarkWriteEntityStateToClient)
        int customize = vm.FieldInt(e, f.CustomizeEntityForClient);
        if (customize == 0 && e > _clients.Length && modelIndex == 0 && specialVisibilityRadius == 0) return false;

        state = default;
        ref EntityState net = ref state.Net;
        net = EntityState.Default;
        net.Active = DpProtocol.ActiveNetwork;
        net.Number = (ushort)e;
        net.Origin = SvWorld.V(origin);
        QcVector angles = vm.FieldVector(e, f.Angles);
        net.Angles = SvWorld.V(angles);
        net.Effects = (int)effects;
        net.Colormap = (byte)FloatToUInt(vm.FieldFloat(e, f.ColorMap));
        net.ModelIndex = (ushort)modelIndex;
        net.Skin = (byte)FloatToUInt(vm.FieldFloat(e, f.Skin));
        net.Frame = (ushort)FloatToUInt(vm.FieldFloat(e, f.Frame));
        state.ViewModelForClient = EntityNumber(vm.FieldInt(e, f.ViewModelForClient));
        state.ExteriorModelForClient = EntityNumber(vm.FieldInt(e, f.ExteriorModelToClient));
        state.NoDrawToClient = EntityNumber(vm.FieldInt(e, f.NoDrawToClient));
        state.DrawOnlyToClient = EntityNumber(vm.FieldInt(e, f.DrawOnlyToClient));
        state.CustomizeEntityForClient = (uint)customize;
        net.TagEntity = EntityNumber(vm.FieldInt(e, f.TagEntity));
        net.TagIndex = (byte)QcVm.FloatToInt(vm.FieldFloat(e, f.TagIndex));
        net.GlowSize = (byte)glowSize;
        net.TrailEffectNum = (ushort)FloatToUInt(vm.FieldFloat(e, f.TrailEffectNum));

        // don't need to init cs->colormod because the defaultstate did that for us
        QcVector colorMod = vm.FieldVector(e, f.ColorMod);
        if (colorMod.X != 0 || colorMod.Y != 0 || colorMod.Z != 0)
        {
            net.ColorMod0 = (byte)Math.Clamp((int)(colorMod.X * 32.0f), 0, 255);
            net.ColorMod1 = (byte)Math.Clamp((int)(colorMod.Y * 32.0f), 0, 255);
            net.ColorMod2 = (byte)Math.Clamp((int)(colorMod.Z * 32.0f), 0, 255);
        }
        QcVector glowMod = vm.FieldVector(e, f.GlowMod);
        if (glowMod.X != 0 || glowMod.Y != 0 || glowMod.Z != 0)
        {
            net.GlowMod0 = (byte)Math.Clamp((int)(glowMod.X * 32.0f), 0, 255);
            net.GlowMod1 = (byte)Math.Clamp((int)(glowMod.Y * 32.0f), 0, 255);
            net.GlowMod2 = (byte)Math.Clamp((int)(glowMod.Z * 32.0f), 0, 255);
        }

        net.Alpha = 255;
        float value = vm.FieldFloat(e, f.Alpha) * 255.0f;
        if (value != 0) net.Alpha = (byte)Math.Clamp(QcVm.FloatToInt(value), 0, 255);
        // halflife
        value = vm.FieldFloat(e, f.RenderAmt);
        if (value != 0) net.Alpha = (byte)Math.Clamp(QcVm.FloatToInt(value), 0, 255);

        net.Scale = 16;
        value = vm.FieldFloat(e, f.Scale) * 16.0f;
        if (value != 0) net.Scale = (byte)Math.Clamp(QcVm.FloatToInt(value), 0, 255);

        net.GlowColor = 254;
        value = vm.FieldFloat(e, f.GlowColor);
        if (value != 0) net.GlowColor = (byte)QcVm.FloatToInt(value);

        if (vm.FieldFloat(e, f.FullBright) != 0) net.Effects |= EfFullBright;
        value = vm.FieldFloat(e, f.ModelFlags);
        if (value != 0) net.Effects |= (int)((FloatToUInt(value) & 0xff) << 24);

        if (vm.FieldFloat(e, f.MoveType) == SvqcHost.MoveTypeStep || (QcVm.FloatToInt(vm.FieldFloat(e, f.Flags)) & SvqcHost.FlMonster) != 0)
            flags |= RenderStep;
        if (e != _toClientEntity && (net.Effects & EfLowPrecision) != 0 && origin.X >= -32768 && origin.Y >= -32768 && origin.Z >= -32768
            && origin.X <= 32767 && origin.Y <= 32767 && origin.Z <= 32767)
            flags |= RenderLowPrecision;
        if (vm.FieldFloat(e, f.ColorMap) >= 1024) flags |= RenderColorMapped;
        if (state.ViewModelForClient != 0) flags |= RenderViewModel;   // show relative to the view

        if (vm.FieldFloat(e, f.SendComplexAnimation) != 0)
        {
            flags |= RenderComplexAnimation;
            // (.skeletonindex: the server builds no skeleton objects, so none is sent.)
            net.Blend0.Frame = QcVm.FloatToInt(vm.FieldFloat(e, f.Frame));
            net.Blend1.Frame = QcVm.FloatToInt(vm.FieldFloat(e, f.Frame2));
            net.Blend2.Frame = QcVm.FloatToInt(vm.FieldFloat(e, f.Frame3));
            net.Blend3.Frame = QcVm.FloatToInt(vm.FieldFloat(e, f.Frame4));
            state.BlendStart0 = vm.FieldFloat(e, f.Frame1Time);
            state.BlendStart1 = vm.FieldFloat(e, f.Frame2Time);
            state.BlendStart2 = vm.FieldFloat(e, f.Frame3Time);
            state.BlendStart3 = vm.FieldFloat(e, f.Frame4Time);
            net.Blend1.Lerp = vm.FieldFloat(e, f.LerpFrac);
            net.Blend2.Lerp = vm.FieldFloat(e, f.LerpFrac3);
            net.Blend3.Lerp = vm.FieldFloat(e, f.LerpFrac4);
            net.Blend0.Lerp = 1.0f - net.Blend1.Lerp - net.Blend2.Lerp - net.Blend3.Lerp;
            net.Frame = 0;   // don't need the legacy frame
        }
        net.Flags = (byte)flags;

        net.Light0 = (ushort)light0;
        net.Light1 = (ushort)light1;
        net.Light2 = (ushort)light2;
        net.Light3 = (ushort)light3;
        net.LightStyle = (byte)lightStyle;
        net.LightPFlags = (byte)lightPFlags;
        state.SpecialVisibilityRadius = (ushort)Math.Min(specialVisibilityRadius, 65535);
        state.Time = host.Time;

        // calculate the visible box of this entity (don't use the physics box as that is often
        // smaller than a model, and may not be a good box)
        QcVector cullMins, cullMaxs;
        if (modelIndex != 0 && host.ModelBounds(modelIndex) is { } model)
        {
            float scale = net.Scale * (1.0f / 16.0f);
            QcVector lo, hi;
            if (angles.X != 0 || angles.Z != 0) (lo, hi) = (model.RotatedMins, model.RotatedMaxs);   // pitch and roll
            else if (angles.Y != 0 || ((effects | (uint)(net.Effects >> 24 << 24)) & 0x08000000) != 0) (lo, hi) = (model.YawMins, model.YawMaxs);   // EF_ROTATE by model flag
            else (lo, hi) = (model.NormalMins, model.NormalMaxs);
            cullMins = new QcVector(origin.X + scale * lo.X, origin.Y + scale * lo.Y, origin.Z + scale * lo.Z);
            cullMaxs = new QcVector(origin.X + scale * hi.X, origin.Y + scale * hi.Y, origin.Z + scale * hi.Z);
        }
        else
        {
            // if there is no model (or it could not be loaded), use the physics box
            QcVector mins = vm.FieldVector(e, f.Mins), maxs = vm.FieldVector(e, f.Maxs);
            cullMins = new QcVector(origin.X + mins.X, origin.Y + mins.Y, origin.Z + mins.Z);
            cullMaxs = new QcVector(origin.X + maxs.X, origin.Y + maxs.Y, origin.Z + maxs.Z);
        }
        if (specialVisibilityRadius != 0)
        {
            cullMins = new QcVector(MathF.Min(cullMins.X, origin.X - specialVisibilityRadius), MathF.Min(cullMins.Y, origin.Y - specialVisibilityRadius), MathF.Min(cullMins.Z, origin.Z - specialVisibilityRadius));
            cullMaxs = new QcVector(MathF.Max(cullMaxs.X, origin.X + specialVisibilityRadius), MathF.Max(cullMaxs.Y, origin.Y + specialVisibilityRadius), MathF.Max(cullMaxs.Z, origin.Z + specialVisibilityRadius));
        }
        // calculate center of bbox for network prioritization purposes
        state.NetCenter = new Vector3((cullMins.X + cullMaxs.X) * 0.5f, (cullMins.Y + cullMaxs.Y) * 0.5f, (cullMins.Z + cullMaxs.Z) * 0.5f);
        _cullMins[e] = cullMins;
        _cullMaxs[e] = cullMaxs;

        // we need to do some csqc entity upkeep here; get self.SendFlags and clear them, then mark
        // this entity as CSQC-networked. (Only on the once-per-frame pass: a second preparation for
        // one client's customizeentityforclient finds the flags already collected.)
        if (sendEntity)
        {
            if (collectSendFlags)
            {
                uint sendFlags = _csqcVersions.CollectSendFlags(this, e);
                if (sendFlags != 0)
                    foreach (SvClient client in _clients)
                        if (client.Connection is SvNetConnection { CsqcFrames: { } frames }) frames.AddSendFlags(e, sendFlags);
            }
            net.Active = DpProtocol.ActiveShared;
        }
        return true;
    }

    private static uint FloatToUInt(float value) => value >= 0 && value < 4294967296f ? (uint)value : value < 0 && value > -2147483648f ? (uint)(int)value : 0;

    // An entity field naming another entity, as the wire carries it: 0 for anything out of range.
    private static ushort EntityNumber(int value) => (uint)value < DpProtocol.MaxEdicts ? (ushort)value : (ushort)0;

    // SV_CanSeeBox: line-of-sight samples from the eye to points in the entity's box, each through
    // the world's BSP tree (Mod_Q3BSP_TraceLineOfSight: structural walls stop a sample, detail brushes
    // and curves do not) - or, with mod_q3bsp_tracelineofsight_brushes, against the world's opaque
    // brushes (SUPERCONTENTS_VISBLOCKERMASK). sv_cullentities_trace_entityocclusion (doors, default
    // 0) is not ported.
    private bool CanSeeBox(SvqcHost host, int numTraces, float eyeJitter, float enlarge, float expand, QcVector eye, QcVector boxMins, QcVector boxMaxs)
    {
        numTraces = Math.Min(numTraces, MaxLineOfSightTraces);
        bool byBrushes = Cvar("mod_q3bsp_tracelineofsight_brushes", 0) != 0;
        QcVector lo = new((enlarge + 1) * boxMins.X - enlarge * boxMaxs.X - expand, (enlarge + 1) * boxMins.Y - enlarge * boxMaxs.Y - expand, (enlarge + 1) * boxMins.Z - enlarge * boxMaxs.Z - expand);
        QcVector hi = new((enlarge + 1) * boxMaxs.X - enlarge * boxMins.X + expand, (enlarge + 1) * boxMaxs.Y - enlarge * boxMins.Y + expand, (enlarge + 1) * boxMaxs.Z - enlarge * boxMins.Z + expand);
        for (int i = 0; i < numTraces; i++)
        {
            // the first sample is the centre of the box, the rest are random points in it
            QcVector end = i == 0 ? new QcVector((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, (lo.Z + hi.Z) * 0.5f)
                : new QcVector(Random(lo.X, hi.X), Random(lo.Y, hi.Y), Random(lo.Z, hi.Z));
            QcVector start = eyeJitter == 0 ? eye : new QcVector(Random(eye.X - eyeJitter, eye.X + eyeJitter), Random(eye.Y - eyeJitter, eye.Y + eyeJitter), Random(eye.Z - eyeJitter, eye.Z + eyeJitter));
            if (!byBrushes)
            {
                if (host.World.TraceLineOfSight(start, end, lo, hi)) return true;
                continue;
            }
            SvTrace trace = host.World.Trace(start, default, default, end, SvWorld.MoveWorldOnly, 0, SvWorld.ContentsOpaque);
            // "it hit something, check if it is in the box": a sample that ends inside the box sees it
            if (trace.Fraction == 1 || (trace.EndPos.X >= lo.X && trace.EndPos.X <= hi.X && trace.EndPos.Y >= lo.Y && trace.EndPos.Y <= hi.Y
                && trace.EndPos.Z >= lo.Z && trace.EndPos.Z <= hi.Z))
                return true;
        }
        return false;
    }

    private float Random(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    // SV_MarkWriteEntityStateToClient: decide whether one prepared entity is sent to the client
    // being written to, pulling in whatever it is attached to.
    private void MarkWriteEntityStateToClient(SvqcHost host, int index, SvClient client, SvNetConnection connection, int depth = 0)
    {
        ref SvEntityState s = ref _sendEntities[index];
        int number = s.Net.Number;
        if (_consideration[number] == _sentMark) return;
        _consideration[number] = _sentMark;

        if (s.CustomizeEntityForClient != 0)
        {
            host.SetTime(host.Time);
            host.Self = number;
            host.Other = _toClientEntity;
            host.Exec((int)s.CustomizeEntityForClient, "customizeentityforclient: NULL function");
            if (host.Vm.ResultFloat == 0 || !host.IsLive(number) || !PrepareEntityForSending(host, number, ref s, collectSendFlags: false)) return;
        }

        // never reject player
        if (number != _toClientEntity)
        {
            // check various rejection conditions
            if (s.NoDrawToClient == _toClientEntity) return;
            if (s.DrawOnlyToClient != 0 && s.DrawOnlyToClient != _toClientEntity) return;
            if ((s.Net.Effects & EfNoDraw) != 0) return;
            // LadyHavoc: only send entities with a model or important effects
            if (s.Net.ModelIndex == 0 && s.SpecialVisibilityRadius == 0) return;

            bool isBModel = s.Net.ModelIndex != 0 && host.ModelName(s.Net.ModelIndex) is { Length: > 0 } name && name[0] == '*';
            // viewmodels don't have visibility checking
            if (s.ViewModelForClient != 0)
            {
                if (s.ViewModelForClient != _toClientEntity) return;
            }
            else if (s.Net.TagEntity != 0)
            {
                // tag attached entities simply check their parent
                int parent = s.Net.TagEntity < _sendEntityIndex.Length ? _sendEntityIndex[s.Net.TagEntity] : -1;
                if (parent < 0 || parent >= _numSendEntities) return;
                // (an attachment chain is at most as long as the list; a loop in it ends at the mark)
                if (depth < 256) MarkWriteEntityStateToClient(host, parent, client, connection, depth + 1);
                if (_sent[s.Net.TagEntity] != _sentMark) return;
            }
            // always send world submodels in newer protocols because they don't generate much
            // traffic (in old protocols they hog bandwidth)
            // but only if sv_cullentities_nevercullbmodels is off
            else if ((s.Net.Effects & EfNoDepthTest) == 0 && (!isBModel || Cvar("sv_cullentities_nevercullbmodels", 0) == 0))
            {
                // entity has survived every check so far, check if visible
                QcVector cullMins = _cullMins[number], cullMaxs = _cullMaxs[number];
                // if not touching a visible leaf
                if (_pvs is { } pvs && Cvar("sv_cullentities_pvs", 1) != 0 && !host.World.BoxTouchingPvs(pvs, cullMins, cullMaxs))
                {
                    EntitiesCulledByPvs++;
                    return;
                }
                // or not seen by random tracelines
                if (Cvar("sv_cullentities_trace", 0) != 0 && !isBModel && (client.Frags != -666 || Cvar("sv_cullentities_trace_spectators", 0) != 0))
                {
                    int samples = number <= _clients.Length ? (int)Cvar("sv_cullentities_trace_samples_players", 8)
                        : s.SpecialVisibilityRadius != 0 ? (int)Cvar("sv_cullentities_trace_samples_extra", 2)
                        : (int)Cvar("sv_cullentities_trace_samples", 2);
                    if (samples > 0)
                    {
                        float jitter = Cvar("sv_cullentities_trace_eyejitter", 16), enlarge = Cvar("sv_cullentities_trace_enlarge", 0), expand = Cvar("sv_cullentities_trace_expand", 0);
                        bool seen = false;
                        for (int eye = 0; eye < _numEyes && !seen; eye++) seen = CanSeeBox(host, samples, jitter, enlarge, expand, _eyes[eye], cullMins, cullMaxs);
                        double[] visibleTime = connection.VisibleTime ??= new double[DpProtocol.MaxEdicts];
                        if (seen)
                            visibleTime[number] = RealTime + (number <= _clients.Length ? Cvar("sv_cullentities_trace_delay_players", 0.2f) : Cvar("sv_cullentities_trace_delay", 1));
                        else if ((float)RealTime > visibleTime[number])
                        {
                            EntitiesCulledByTrace++;
                            return;
                        }
                    }
                }
            }
        }

        // this just marks it for sending
        // FIXME: it would be more efficient to send here, but the entity compressor isn't that flexible
        _sent[number] = _sentMark;
    }

    /// <summary>
    /// The entity numbers the server would put in this client's next update - both streams, the
    /// engine's (svc_entities) and the program's (svc_csqcentities) - after every visibility test
    /// SV_MarkWriteEntityStateToClient applies. For comparing culling against another server and for
    /// tests; it runs the same code the send does (customizeentityforclient included) but sends
    /// nothing and consumes no SendFlags.
    /// </summary>
    public void VisibleEntities(SvClient client, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (Host is not { } host || !client.Active || client.Connection is not SvNetConnection connection) return;
        host.Guard("SV_MarkWriteEntityStateToClient", () =>
        {
            PrepareEntitiesForSending(host, collectSendFlags: false);
            MarkEntitiesForClient(host, client, connection);
            for (int i = 0; i < _numSendEntities; i++)
                if (_sent[_sendEntities[i].Net.Number] == _sentMark) into.Add(_sendEntities[i].Net.Number);
        });
        _toClient = null;
        _toClientEntity = 0;
    }

    /// <summary>Whether <see cref="VisibleEntities"/> contains the entity.</summary>
    public bool WouldSendEntity(SvClient client, int entityNumber)
    {
        List<int> visible = new();
        VisibleEntities(client, visible);
        return visible.Contains(entityNumber);
    }

    // The first half of SV_WriteEntitiesToClient: where the client looks from, and which of the
    // prepared entities it is to be told about.
    private void MarkEntitiesForClient(SvqcHost host, SvClient client, SvNetConnection connection)
    {
        QcVm vm = host.Vm;
        _toClient = client;
        _toClientEntity = client.Edict;   // LadyHavoc: for comparison purposes
        _numEyes = 0;
        // get eye location
        int camera = host.IsLive(client.ClientCamera) ? client.ClientCamera : client.Edict;
        QcVector cameraOrigin = vm.FieldVector(camera, host.F.Origin), viewOfs = vm.FieldVector(client.Edict, host.F.ViewOfs);
        QcVector eye = new(cameraOrigin.X + viewOfs.X, cameraOrigin.Y + viewOfs.Y, cameraOrigin.Z + viewOfs.Z);
        _eyes[_numEyes++] = eye;
        // get the PVS values for the eye location, later FatPVS calls will merge
        int pvsBytes = host.World.PvsBytes;
        if (pvsBytes > 0)
        {
            if (_pvsBuffer.Length != pvsBytes) _pvsBuffer = new byte[pvsBytes];   // "don't reuse stale data when the worldmodel changes"
            _pvs = _pvsBuffer;
            host.World.FatPvs(eye, 8, _pvs, merge: false);
        }
        else _pvs = null;

        // movement prediction: also look from where the player will be by the time this arrives
        if (Cvar("sv_cullentities_trace_prediction", 1) != 0)
        {
            float predictTime = Math.Clamp(client.Ping, 0, Math.Max(0, Cvar("sv_cullentities_trace_prediction_time", 0.2f)));
            QcVector velocity = vm.FieldVector(camera, host.F.Velocity);
            QcVector predicted = new(eye.X + predictTime * velocity.X, eye.Y + predictTime * velocity.Y, eye.Z + predictTime * velocity.Z);
            if (CanSeeBox(host, 1, 0, 0, 0, eye, predicted, predicted)) _eyes[_numEyes++] = predicted;
        }
        AddCameraEyes(host, connection);
        // build PVS from the new eyes
        if (_pvs is not null)
            for (int i = 1; i < _numEyes; i++) host.World.FatPvs(_eyes[i], 8, _pvs, merge: true);

        _sentMark++;
        for (int i = 0; i < _numSendEntities; i++) MarkWriteEntityStateToClient(host, i, client, connection);
    }

    // SV_AddCameraEyes: "check line of sight to portal entities and add them to PVS". An entity with
    // a camera_transform function (Xonotic's warpzones, its portals and cameras) shows the client
    // another place: the function is asked where the client's eye is as seen through it, and if the
    // client can see the entity, that place is another eye - so what stands on the far side of a
    // warpzone is sent, though no line leads there.
    private void AddCameraEyes(SvqcHost host, SvNetConnection connection)
    {
        QcVm vm = host.Vm;
        int cameraTransform = host.F.CameraTransform;
        if (cameraTransform < 0 || host.G.TraceEndPos < 0) return;
        int numCameras = 0;
        QcVector eye0 = _eyes[0];
        for (int e = 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e)) continue;
            int function = vm.FieldInt(e, cameraTransform);
            if (function == 0) continue;
            host.SetTime(host.Time);
            host.Self = e;
            host.Other = _toClientEntity;
            vm.GlobalVector(host.G.TraceEndPos) = eye0;
            vm.SetArgVector(0, eye0);
            vm.SetArgVector(1, default);
            host.Exec(function, "QC function e.camera_transform is missing");
            QcVector seen = vm.GlobalVector(host.G.TraceEndPos);
            if (seen.X == eye0.X && seen.Y == eye0.Y && seen.Z == eye0.Z) continue;
            _cameraOrigins[numCameras] = seen;
            _cameras[numCameras] = e;
            if (++numCameras >= MaxLevelNetworkEyes) break;
        }
        if (numCameras == 0) return;

        Array.Clear(_eyeLevels);
        double[] visibleTime = connection.VisibleTime ??= new double[DpProtocol.MaxEdicts];
        int samples = (int)Cvar("sv_cullentities_trace_samples_extra", 2);
        float jitter = Cvar("sv_cullentities_trace_eyejitter", 16), enlarge = Cvar("sv_cullentities_trace_enlarge", 0), expand = Cvar("sv_cullentities_trace_expand", 0);
        float delay = Cvar("sv_cullentities_trace_delay", 1);
        // i is loop counter, is reset to 0 when an eye got added; j is camera index to check
        for (int i = 0, j = 0; _numEyes < MaxClientNetworkEyes && i < numCameras; ++i, ++j, j %= numCameras)
        {
            int camera = _cameras[j];
            if (camera == 0 || !host.IsLive(camera)) continue;
            QcVector origin = vm.FieldVector(camera, host.F.Origin), mins = vm.FieldVector(camera, host.F.Mins), maxs = vm.FieldVector(camera, host.F.Maxs);
            QcVector mi = new(origin.X + mins.X, origin.Y + mins.Y, origin.Z + mins.Z), ma = new(origin.X + maxs.X, origin.Y + maxs.Y, origin.Z + maxs.Z);
            for (int k = 0; k < _numEyes; k++)
            {
                if (_eyeLevels[k] > MaxEyeRecursion) continue;
                if (CanSeeBox(host, samples, jitter, enlarge, expand, _eyes[k], mi, ma)) visibleTime[camera] = RealTime + delay;
                // "this use of visibletime doesn't conflict because sv_cullentities_trace doesn't
                // consider portal entities; the explicit cast prevents float precision differences
                // that cause the condition to fail"
                if ((float)RealTime <= visibleTime[camera])
                {
                    _eyeLevels[_numEyes] = _eyeLevels[k] + 1;
                    _eyes[_numEyes++] = _cameraOrigins[j];
                    CameraEyesAdded++;
                    _cameras[j] = 0;
                    i = 0;    // (and the loop's own ++i then makes it 1, as in the C)
                    break;
                }
            }
        }
    }

    // SV_WriteEntitiesToClient
    private void WriteEntitiesToClient(SvqcHost host, SvClient client, SvNetConnection connection, DpMessageWriter msg, int maxSize)
    {
        // if there isn't enough space to accomplish anything, skip it
        if (msg.Length + 25 > maxSize || connection.EntityDatabase is not { } database || connection.CsqcFrames is not { } csqc) return;
        MarkEntitiesForClient(host, client, connection);
        int numSendStates = 0, numCsqcSendStates = 0;
        for (int i = 0; i < _numSendEntities; i++)
        {
            ref SvEntityState s = ref _sendEntities[i];
            if (_sent[s.Net.Number] != _sentMark) continue;
            if (s.Net.Active == DpProtocol.ActiveNetwork)
            {
                if (s.ExteriorModelForClient != 0)
                {
                    if (s.ExteriorModelForClient == _toClientEntity) s.Net.Flags |= RenderExteriorModel;
                    else s.Net.Flags = (byte)(s.Net.Flags & ~RenderExteriorModel);
                }
                _sendStates[numSendStates++] = s;
            }
            else if (s.Net.Active == DpProtocol.ActiveShared) _csqcNumbers[numCsqcSendStates++] = s.Net.Number;
        }

        bool needEmpty = csqc.WriteFrame(msg, maxSize, _csqcNumbers.AsSpan(0, numCsqcSendStates), database.LatestFrameNumber + 1, _toClientEntity);
        // force every 16th frame to be not empty (or cl_movement replay takes too long); BTW, this
        // should normally not kick in any more due to the check below, except if the client stopped
        // sending movement info
        if (connection.NumSkippedEntityFrames >= 16) needEmpty = true;
        // help cl_movement a bit more
        if (client.MoveSequence != connection.LastMoveSequence) needEmpty = true;
        connection.LastMoveSequence = client.MoveSequence;

        bool success = database.WriteFrame(msg, maxSize, _sendStates.AsSpan(0, numSendStates), client.Edict, client.MoveSequence, needEmpty);
        if (success)
        {
            connection.NumSkippedEntityFrames = 0;
            EntityFramesSent++;
        }
        else
        {
            connection.NumSkippedEntityFrames++;
            EntityFramesSkipped++;
        }
        _toClient = null;
        _toClientEntity = 0;
    }

    // ---- what the encoders ask of the level -------------------------------------------------------------

    int ISvEntityFrame5Host.MaxClients => _clients.Length;
    int ISvEntityFrame5Host.MaxEdicts => Host?.Vm.MaxEdicts ?? 0;
    double ISvEntityFrame5Host.Time => Host?.Time ?? 0;
    bool ISvEntityFrame5Host.HasSendEntity(int entityNumber) => HasSendEntity(entityNumber);

    double ISvEntityFrame5Host.FrameDuration(int modelIndex, int frame)
    {
        if (Host is not { } host) return 0;
        string model = host.ModelName(modelIndex);
        return model.Length == 0 || model[0] == '*' ? 0 : host.Models.FrameDuration(model, frame);
    }

    int ISvCsqcEntityHost.MaxClients => _clients.Length;
    int ISvCsqcEntityHost.NumEdicts => Host?.Vm.NumEdicts ?? 0;
    bool ISvCsqcEntityHost.RandomizeOrder => Cvar("sv_sendentities_csqc_randomize_order", 1) != 0;
    int ISvCsqcEntityHost.NextRandom(int exclusiveMax) => exclusiveMax > 0 ? _random.Next(exclusiveMax) : 0;
    bool ISvCsqcEntityHost.HasSendEntity(int entityNumber) => HasSendEntity(entityNumber);

    private bool HasSendEntity(int entityNumber) =>
        Host is { } host && host.IsLive(entityNumber) && host.Vm.FieldInt(entityNumber, host.F.SendEntity) != 0;

    float ISvCsqcEntityHost.GetSendFlags(int entityNumber) => Host is { } host && host.IsLive(entityNumber) ? host.Vm.FieldFloat(entityNumber, host.F.SendFlags) : 0;

    void ISvCsqcEntityHost.ClearSendFlags(int entityNumber)
    {
        if (Host is { } host && host.IsLive(entityNumber)) host.Vm.FieldFloat(entityNumber, host.F.SendFlags) = 0;
    }

    float ISvCsqcEntityHost.GetVersion(int entityNumber) => Host is { } host && host.IsLive(entityNumber) ? host.Vm.FieldFloat(entityNumber, host.F.Version) : 0;

    // EntityFrameCSQC_WriteFrame's call of the entity's SendEntity function: self is the entity, the
    // arguments are the client it is for and the flags to send, MSG_ENTITY writes go to the frame
    // being built, and the return value says whether the entity exists for that client at all.
    bool ISvCsqcEntityHost.CallSendEntity(int entityNumber, int toClientEntityNumber, int sendFlags, DpMessageWriter msg)
    {
        if (Host is not { } host || !host.IsLive(entityNumber)) return false;
        int function = host.Vm.FieldInt(entityNumber, host.F.SendEntity);
        if (function == 0) return false;
        DpMessageWriter? previous = host.EntityMessage;
        host.EntityMessage = msg;
        try
        {
            host.SetTime(host.Time);
            host.Self = entityNumber;
            host.Vm.SetArgInt(0, toClientEntityNumber);
            host.Vm.SetArgFloat(1, sendFlags);
            host.Vm.Execute(function, 2);
            return host.Vm.ResultFloat != 0;
        }
        finally { host.EntityMessage = previous; }
    }
}
