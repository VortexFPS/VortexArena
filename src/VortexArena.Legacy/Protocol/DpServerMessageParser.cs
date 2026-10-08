// Port of Base/darkplaces/cl_parse.c CL_ParseServerMessage (the non-QuakeWorld loop, lines 3836-4291)
// for PROTOCOL_DARKPLACES7, with the helpers it calls: CL_ParseServerInfo, CL_ParseBaseline,
// CL_ParseClientdata, CL_ParseStatic, CL_ParseStaticSound, CL_ParseStartSoundPacket, CL_ParseEffect,
// CL_ParseEffect2, CL_ParseDownload, CL_ParseTrailParticles, CL_ParsePointParticles; plus
// cl_particles.c CL_ParseParticleEffect, view.c V_ParseDamage, cl_screen.c SHOWLMP_decodeshow/hide
// and csprogs.c CSQC_ReadEntities / CL_VM_Parse_TempEntity (the framing only).
using System.Numerics;

namespace VortexArena.Legacy.Protocol;

public enum DpParseStatus
{
    /// <summary>Every command up to the end of the message was decoded.</summary>
    Complete,
    /// <summary>A handler could not measure a QuakeC-defined payload; the rest of the message was skipped.</summary>
    Aborted,
    /// <summary>The message is not valid DP7. DarkPlaces would Host_Error and drop the connection.</summary>
    Error,
}

/// <summary>The outcome of parsing one message.</summary>
public readonly struct DpParseResult
{
    public DpParseStatus Status { get; init; }
    /// <summary>Commands fully decoded before parsing stopped.</summary>
    public int Commands { get; init; }
    /// <summary>The message id parsing stopped at (for <see cref="DpParseStatus.Aborted"/> and
    /// <see cref="DpParseStatus.Error"/>), or -1.</summary>
    public int Svc { get; init; }
    /// <summary>Byte offset of that id within the message, or -1.</summary>
    public int Offset { get; init; }
    public string? Message { get; init; }

    public bool IsError => Status == DpParseStatus.Error;
    public override string ToString() => Status == DpParseStatus.Complete
        ? $"complete ({Commands} commands)"
        : $"{Status} at svc {Svc} offset {Offset}: {Message}";
}

/// <summary>
/// Turns one server message (the payload of a netchan message, or one block of a demo) into calls on
/// an <see cref="IDpClientHandler"/>. A message is a sequence of commands, each one id byte followed by
/// a payload whose layout only the id tells you; there are no lengths, so a single command that is
/// decoded wrongly makes everything after it garbage. Hence the rule in here: any id that is not a
/// DP7 command, any count out of range, any read past the end stops the message with an error that
/// names the id and where it was.
///
/// The server is not trusted. Nothing read off the wire is used as an index or a length before it is
/// compared with the same limits DarkPlaces checks (MAX_EDICTS, MAX_MODELS, MAX_SOUNDS, MAX_QPATH,
/// MAX_SCOREBOARD), and no input can make <see cref="Parse(DpMessageReader)"/> throw.
/// </summary>
public sealed class DpServerMessageParser
{
    private readonly IDpClientHandler _handler;

    public DpServerMessageParser(IDpClientHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>The entity table svc_entities and svc_spawnbaseline write to.</summary>
    public DpEntityTable Entities { get; } = new();

    /// <summary>cl.maxclients from the last svc_serverinfo; 0 before one arrives. Scoreboard updates
    /// naming a client at or past it are errors, as in DarkPlaces.</summary>
    public int MaxClients { get; private set; }

    /// <summary>The protocol number from the last svc_serverinfo or svc_version; 0 before either.</summary>
    public int Protocol { get; private set; }

    /// <summary>The client's current input sequence, recorded against each entity frame so its ack can
    /// be repeated (cl.cmd.sequence in CL_NewFrameReceived). The owner of the netchan keeps it current.</summary>
    public uint MoveSequence { get; set; }

    /// <summary>Called with (id, offset) as each command is recognised, before it is decoded. For
    /// statistics and for "packet dump" style diagnostics.</summary>
    public Action<int, int>? CommandTrace { get; set; }

    public DpParseResult Parse(byte[] message) => Parse(new DpMessageReader(message));

    public DpParseResult Parse(DpMessageReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int commands = 0;
        int svc = -1;
        int offset = -1;
        try
        {
            while (true)
            {
                // A command that ran off the end is caught here, one iteration late, exactly as the
                // C does it ("CL_ParseServerMessage: Bad server message").
                if (reader.BadRead)
                    return Stop(DpParseStatus.Error, commands, svc, offset, "message ended inside the command");

                offset = reader.Position;
                svc = reader.ReadByte();
                if (svc == -1)
                    return new DpParseResult { Status = DpParseStatus.Complete, Commands = commands, Svc = -1, Offset = -1 };

                CommandTrace?.Invoke(svc, offset);
                DpParseStatus status = Dispatch(svc, reader, out string? message, out bool stop);
                if (status != DpParseStatus.Complete)
                    return Stop(status, commands, svc, offset, message);
                if (reader.BadRead)
                    return Stop(DpParseStatus.Error, commands, svc, offset, "message ended inside the command");
                commands++;
                if (stop)
                    return new DpParseResult { Status = DpParseStatus.Complete, Commands = commands, Svc = -1, Offset = -1 };
            }
        }
        catch (Exception ex)
        {
            // The decoder itself does not throw. This is for a handler that does: a callback failing
            // must not unwind through the network pump.
            return Stop(DpParseStatus.Error, commands, svc, offset, "handler threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static DpParseResult Stop(DpParseStatus status, int commands, int svc, int offset, string? message) =>
        new() { Status = status, Commands = commands, Svc = svc, Offset = offset, Message = message };

    private DpParseStatus Dispatch(int svc, DpMessageReader r, out string? error, out bool stop)
    {
        error = null;
        stop = false;

        // "if the high bit of the command byte is set, it is a fast update": the NetQuake entity
        // format. A DP7 server never sends it, so here it can only be corruption.
        if ((svc & 128) != 0)
        {
            error = "NetQuake fast entity update is not part of DP7";
            return DpParseStatus.Error;
        }

        switch ((Svc)svc)
        {
            case Svc.Nop:
                _handler.OnNop();
                break;

            case Svc.Disconnect:
                // DP8 adds a reason string here; DP7 has no payload.
                _handler.OnDisconnect();
                stop = true;
                break;

            case Svc.UpdateStat:
            {
                int index = r.ReadByte(); // a byte, so always below MAX_CL_STATS unless the read failed
                int value = r.ReadLong();
                if (!r.BadRead)
                    _handler.OnUpdateStat(index, value);
                break;
            }

            case Svc.UpdateStatUByte:
            {
                int index = r.ReadByte();
                int value = r.ReadByte();
                if (!r.BadRead)
                    _handler.OnUpdateStat(index, value);
                break;
            }

            case Svc.Version:
            {
                int protocol = r.ReadLong();
                if (r.BadRead)
                    break;
                if (protocol != DpProtocol.ProtocolNumberDp7)
                    return Fail(out error, $"server protocol {protocol} is not DP7 ({DpProtocol.ProtocolNumberDp7})");
                Protocol = protocol;
                _handler.OnVersion(protocol);
                break;
            }

            case Svc.SetView:
            {
                int entity = r.ReadUShort();
                if (r.BadRead)
                    break;
                if (entity >= DpProtocol.MaxEdicts)
                    return Fail(out error, "svc_setview >= MAX_EDICTS");
                _handler.OnSetView(entity);
                break;
            }

            case Svc.Sound:
                ParseSound(r);
                break;

            case Svc.Time:
            {
                float time = r.ReadFloat();
                if (!r.BadRead)
                    _handler.OnTime(time);
                break;
            }

            case Svc.Print:
            {
                string text = r.ReadString();
                if (!r.BadRead)
                    _handler.OnPrint(text);
                break;
            }

            case Svc.StuffText:
            {
                string text = r.ReadString();
                if (!r.BadRead)
                    _handler.OnStuffText(text);
                break;
            }

            case Svc.SetAngle:
            {
                Vector3 angles = r.ReadAngles();
                if (!r.BadRead)
                    _handler.OnSetAngle(angles);
                break;
            }

            case Svc.ServerInfo:
                return ParseServerInfo(r, out error);

            case Svc.LightStyle:
            {
                int style = r.ReadByte(); // a byte: cannot reach MAX_LIGHTSTYLES (256)
                string map = r.ReadString();
                if (!r.BadRead)
                    _handler.OnLightStyle(style, map);
                break;
            }

            case Svc.UpdateName:
            {
                int client = r.ReadByte();
                string name = r.ReadString();
                if (r.BadRead)
                    break;
                if (!ValidClient(client))
                    return Fail(out error, "svc_updatename >= cl.maxclients");
                _handler.OnUpdateName(client, name);
                break;
            }

            case Svc.UpdateFrags:
            {
                int client = r.ReadByte();
                int frags = r.ReadShort();
                if (r.BadRead)
                    break;
                if (!ValidClient(client))
                    return Fail(out error, "svc_updatefrags >= cl.maxclients");
                _handler.OnUpdateFrags(client, frags);
                break;
            }

            case Svc.UpdateColors:
            {
                int client = r.ReadByte();
                int colors = r.ReadByte();
                if (r.BadRead)
                    break;
                if (!ValidClient(client))
                    return Fail(out error, "svc_updatecolors >= cl.maxclients");
                _handler.OnUpdateColors(client, colors);
                break;
            }

            case Svc.ClientData:
                ParseClientData(r);
                break;

            case Svc.StopSound:
            {
                int packed = r.ReadUShort();
                if (!r.BadRead)
                    _handler.OnStopSound(packed >> 3, packed & 7);
                break;
            }

            case Svc.Particle:
            {
                DpParticle p = default;
                p.Origin = r.ReadVector();
                float x = r.ReadChar(), y = r.ReadChar(), z = r.ReadChar();
                p.Direction = new Vector3(x, y, z) * (1.0f / 16.0f);
                int count = r.ReadByte();
                p.Color = r.ReadByte();
                p.Count = count == 255 ? 1024 : count;
                if (!r.BadRead)
                    _handler.OnParticle(p);
                break;
            }

            case Svc.Damage:
            {
                int armor = r.ReadByte();
                int blood = r.ReadByte();
                Vector3 from = r.ReadVector();
                if (!r.BadRead)
                    _handler.OnDamage(armor, blood, from);
                break;
            }

            case Svc.SpawnStatic:
            case Svc.SpawnStatic2:
            {
                EntityState state = ParseBaseline(r, large: svc == (int)Svc.SpawnStatic2);
                if (r.BadRead)
                    break;
                if (state.ModelIndex >= DpProtocol.MaxModels)
                    return Fail(out error, "static entity modelindex >= MAX_MODELS");
                // "static entity without model": DarkPlaces throws these away.
                if (state.ModelIndex != 0)
                    _handler.OnSpawnStatic(state);
                break;
            }

            case Svc.SpawnBaseline:
            case Svc.SpawnBaseline2:
            {
                int entity = r.ReadUShort();
                if (r.BadRead)
                    break;
                if (entity >= DpProtocol.MaxEdicts)
                    return Fail(out error, $"svc_spawnbaseline: invalid entity number {entity}");
                EntityState state = ParseBaseline(r, large: svc == (int)Svc.SpawnBaseline2);
                if (r.BadRead)
                    break;
                state.Number = (ushort)entity;
                Entities.SetBaseline(entity, state);
                _handler.OnSpawnBaseline(entity, state);
                break;
            }

            case Svc.TempEntity:
                return ParseTempEntity(r, out error);

            case Svc.SetPause:
            {
                int paused = r.ReadByte();
                if (!r.BadRead)
                    _handler.OnSetPause(paused != 0);
                break;
            }

            case Svc.SignonNum:
            {
                int stage = r.ReadByte();
                if (!r.BadRead)
                    _handler.OnSignonNum(stage);
                break;
            }

            case Svc.CenterPrint:
            {
                string text = r.ReadString();
                if (!r.BadRead)
                    _handler.OnCenterPrint(text);
                break;
            }

            case Svc.KilledMonster:
                _handler.OnKilledMonster();
                break;

            case Svc.FoundSecret:
                _handler.OnFoundSecret();
                break;

            case Svc.SpawnStaticSound:
            case Svc.SpawnStaticSound2:
            {
                DpStaticSound s = default;
                s.Origin = r.ReadVector();
                s.SoundIndex = svc == (int)Svc.SpawnStaticSound2 ? r.ReadUShort() : r.ReadByte();
                s.Volume = r.ReadByte();
                s.Attenuation = r.ReadByte();
                if (r.BadRead)
                    break;
                // DarkPlaces returns before reading volume and attenuation when the index is out of
                // range, which leaves its own parser two bytes out of step. Calling it an error is
                // the same outcome without the garbage in between.
                if (s.SoundIndex >= DpProtocol.MaxSounds)
                    return Fail(out error, $"static sound index {s.SoundIndex} >= MAX_SOUNDS");
                _handler.OnSpawnStaticSound(s);
                break;
            }

            case Svc.Intermission:
                _handler.OnIntermission();
                break;

            case Svc.Finale:
            {
                string text = r.ReadString();
                if (!r.BadRead)
                    _handler.OnFinale(text);
                break;
            }

            case Svc.CdTrack:
            {
                int track = r.ReadByte();
                int loop = r.ReadByte();
                if (!r.BadRead)
                    _handler.OnCdTrack(track, loop);
                break;
            }

            case Svc.SellScreen:
                _handler.OnSellScreen();
                break;

            case Svc.Cutscene:
            {
                string text = r.ReadString();
                if (!r.BadRead)
                    _handler.OnCutscene(text);
                break;
            }

            case Svc.ShowLmp:
            {
                // (GAME_TENEBRAE reuses this id for a particle effect and GAME_NEHAHRA sends bytes
                // for x/y; neither is Xonotic.)
                string label = r.ReadString();
                string picture = r.ReadString();
                int x = r.ReadShort();
                int y = r.ReadShort();
                if (!r.BadRead)
                    _handler.OnShowLmp(label, picture, x, y);
                break;
            }

            case Svc.HideLmp:
            {
                string label = r.ReadString();
                if (!r.BadRead)
                    _handler.OnHideLmp(label);
                break;
            }

            case Svc.Skybox:
            {
                string name = r.ReadString();
                if (!r.BadRead)
                    _handler.OnSkybox(name);
                break;
            }

            case Svc.DownloadData:
            {
                int start = r.ReadLong();
                int size = r.ReadUShort();
                if (r.BadRead)
                    break;
                ReadOnlySpan<byte> data = r.ReadSpan(size);
                if (!r.BadRead)
                    _handler.OnDownloadData(start, data);
                break;
            }

            case Svc.Effect:
            case Svc.Effect2:
            {
                bool large = svc == (int)Svc.Effect2;
                DpEffect e = default;
                e.Origin = r.ReadVector();
                e.ModelIndex = large ? r.ReadUShort() : r.ReadByte();
                e.StartFrame = large ? r.ReadUShort() : r.ReadByte();
                e.FrameCount = r.ReadByte();
                e.FrameRate = r.ReadByte();
                if (!r.BadRead)
                    _handler.OnEffect(e);
                break;
            }

            case Svc.Precache:
            {
                int index = r.ReadUShort();
                string name = r.ReadString();
                if (r.BadRead)
                    break;
                // precacheindex is + 0 for a model and + 32768 for a sound. An index outside the
                // table is logged and ignored by DarkPlaces, not fatal.
                if (index < 32768)
                {
                    if (index >= 1 && index < DpProtocol.MaxModels)
                        _handler.OnPrecache(index, false, name);
                }
                else if (index - 32768 >= 1 && index - 32768 < DpProtocol.MaxSounds)
                    _handler.OnPrecache(index - 32768, true, name);
                break;
            }

            case Svc.Entities:
            {
                if (!Entities.ReadFrame(r, MoveSequence, out DpEntityFrame frame, out error))
                    return DpParseStatus.Error;
                if (!r.BadRead)
                    _handler.OnEntityFrame(frame, Entities);
                break;
            }

            case Svc.CsqcEntities:
                return ParseCsqcEntities(r, out error);

            case Svc.TrailParticles:
            {
                DpTrailParticles t = default;
                t.Entity = r.ReadUShort();
                if (t.Entity >= DpProtocol.MaxEdicts)
                    t.Entity = 0;
                t.EffectIndex = r.ReadUShort();
                t.Start = r.ReadVector();
                t.End = r.ReadVector();
                if (!r.BadRead)
                    _handler.OnTrailParticles(t);
                break;
            }

            case Svc.PointParticles:
            {
                DpPointParticles p = default;
                p.EffectIndex = r.ReadUShort();
                p.Origin = r.ReadVector();
                p.Velocity = r.ReadVector();
                p.Count = r.ReadUShort();
                if (!r.BadRead)
                    _handler.OnPointParticles(p);
                break;
            }

            case Svc.PointParticles1:
            {
                DpPointParticles p = default;
                p.EffectIndex = r.ReadUShort();
                p.Origin = r.ReadVector();
                p.Count = 1;
                if (!r.BadRead)
                    _handler.OnPointParticles(p);
                break;
            }

            default:
                // svc_bad (0), the unassigned gaps (21, 38-49) and everything past 62.
                return Fail(out error, "illegible server message");
        }
        return DpParseStatus.Complete;
    }

    private static DpParseStatus Fail(out string? error, string message)
    {
        error = message;
        return DpParseStatus.Error;
    }

    // Before a svc_serverinfo there is no maxclients to compare with; the byte itself cannot exceed
    // MAX_SCOREBOARD, so the table a handler indexes with it is bounded either way.
    private bool ValidClient(int client) => client >= 0 && client < (MaxClients > 0 ? MaxClients : DpProtocol.MaxScoreboard);

    // CL_ParseServerInfo, non-QuakeWorld branch.
    private DpParseStatus ParseServerInfo(DpMessageReader r, out string? error)
    {
        error = null;
        int protocol = r.ReadLong();
        if (r.BadRead)
            return DpParseStatus.Complete;
        // DarkPlaces accepts a dozen protocols here and switches its parser. This one knows DP7, and
        // a wrong guess would turn every later byte into nonsense.
        if (protocol != DpProtocol.ProtocolNumberDp7)
            return Fail(out error, $"server protocol {protocol} is not DP7 ({DpProtocol.ProtocolNumberDp7})");

        int maxClients = r.ReadByte();
        if (r.BadRead)
            return DpParseStatus.Complete;
        if (maxClients < 1 || maxClients > DpProtocol.MaxScoreboard)
            return Fail(out error, $"Bad maxclients ({maxClients}) from server");
        int gameType = r.ReadByte();
        string worldMessage = r.ReadString();

        if (!ReadPrecacheList(r, DpProtocol.MaxModels, "model", out List<string> models, out error))
            return DpParseStatus.Error;
        if (!ReadPrecacheList(r, DpProtocol.MaxSounds, "sound", out List<string> sounds, out error))
            return DpParseStatus.Error;
        if (r.BadRead)
            return DpParseStatus.Complete;

        // CL_ClearState: a new level starts from an empty world.
        Entities.Clear();
        Protocol = protocol;
        MaxClients = maxClients;
        _handler.OnServerInfo(new DpServerInfo
        {
            Protocol = protocol,
            MaxClients = maxClients,
            GameType = gameType,
            WorldMessage = worldMessage,
            Models = models,
            Sounds = sounds,
        });
        return DpParseStatus.Complete;
    }

    // Names until an empty one. Slot 0 is never sent (index 0 means "none"), so the list starts at 1.
    private static bool ReadPrecacheList(DpMessageReader r, int max, string what, out List<string> names, out string? error)
    {
        error = null;
        names = new List<string> { "" };
        while (true)
        {
            ReadOnlySpan<byte> raw = r.ReadStringBytes();
            if (raw.IsEmpty || r.BadRead)
                return true; // the terminator, or a short message the caller reports
            if (names.Count == max)
            {
                error = $"Server sent too many {what} precaches";
                return false;
            }
            if (raw.Length >= DpProtocol.MaxQPath)
            {
                error = $"Server sent a precache name of {raw.Length} characters (max {DpProtocol.MaxQPath - 1})";
                return false;
            }
            names.Add(System.Text.Encoding.UTF8.GetString(raw));
        }
    }

    // CL_ParseBaseline for DP7: model and frame are bytes, or shorts in the "2" variants; then
    // colormap, skin, and origin and angle interleaved per axis.
    private static EntityState ParseBaseline(DpMessageReader r, bool large)
    {
        EntityState s = EntityState.Default;
        s.Active = DpProtocol.ActiveNetwork;
        if (large)
        {
            s.ModelIndex = (ushort)r.ReadShort();
            s.Frame = (ushort)r.ReadShort();
        }
        else
        {
            s.ModelIndex = (ushort)(r.ReadByte() & 0xFF);
            s.Frame = (ushort)(r.ReadByte() & 0xFF);
        }
        s.Colormap = (byte)r.ReadByte();
        s.Skin = (byte)r.ReadByte();
        float ox = r.ReadCoord(), ax = r.ReadAngle();
        float oy = r.ReadCoord(), ay = r.ReadAngle();
        float oz = r.ReadCoord(), az = r.ReadAngle();
        s.Origin = new Vector3(ox, oy, oz);
        s.Angles = new Vector3(ax, ay, az);
        return s;
    }

    // CL_ParseClientdata for DP7. Everything the older protocols packed in here after the items
    // (health, ammo, weapon) travels as stats in DP6 and later.
    private void ParseClientData(DpMessageReader r)
    {
        DpClientData d = default;
        d.ViewZoom = 255;
        int bits = r.ReadUShort();
        if ((bits & DpProtocol.SuExtend1) != 0)
            bits |= (r.ReadByte() & 0xFF) << 16;
        if ((bits & DpProtocol.SuExtend2) != 0)
            bits |= (r.ReadByte() & 0xFF) << 24;
        if (r.BadRead)
            return;
        d.Bits = bits;

        if ((bits & DpProtocol.SuViewHeight) != 0)
        {
            d.HasViewHeight = true;
            d.ViewHeight = r.ReadChar();
        }
        if ((bits & DpProtocol.SuIdealPitch) != 0)
            d.IdealPitch = r.ReadChar();

        Span<float> punch = stackalloc float[3];
        Span<float> punchVec = stackalloc float[3];
        Span<float> velocity = stackalloc float[3];
        punch.Clear();
        punchVec.Clear();
        velocity.Clear();
        for (int i = 0; i < 3; i++)
        {
            if ((bits & (DpProtocol.SuPunch1 << i)) != 0)
                punch[i] = r.ReadAngle16i();
            if ((bits & (DpProtocol.SuPunchVec1 << i)) != 0)
                punchVec[i] = r.ReadCoord32f();
            if ((bits & (DpProtocol.SuVelocity1 << i)) != 0)
                velocity[i] = r.ReadCoord32f();
        }
        d.PunchAngle = new Vector3(punch[0], punch[1], punch[2]);
        d.PunchVector = new Vector3(punchVec[0], punchVec[1], punchVec[2]);
        d.Velocity = new Vector3(velocity[0], velocity[1], velocity[2]);

        if ((bits & DpProtocol.SuItems) != 0)
        {
            d.HasItems = true;
            d.Items = r.ReadLong();
        }
        d.OnGround = (bits & DpProtocol.SuOnGround) != 0;
        d.InWater = (bits & DpProtocol.SuInWater) != 0;
        if ((bits & DpProtocol.SuViewZoom) != 0)
        {
            d.HasViewZoom = true;
            d.ViewZoom = r.ReadUShort();
        }
        if (!r.BadRead)
            _handler.OnClientData(d);
    }

    // CL_ParseStartSoundPacket for DP7.
    private void ParseSound(DpMessageReader r)
    {
        DpSound s = default;
        int mask = r.ReadByte();
        if (r.BadRead)
            return;
        s.Volume = (mask & DpProtocol.SndVolume) != 0 ? r.ReadByte() : DpProtocol.DefaultSoundPacketVolume;
        s.Attenuation = (mask & DpProtocol.SndAttenuation) != 0 ? r.ReadByte() / 64.0f : DpProtocol.DefaultSoundPacketAttenuation;
        s.Speed = (mask & DpProtocol.SndSpeedUShort4000) != 0 ? r.ReadUShort() / 4000.0f : 1.0f;
        if ((mask & DpProtocol.SndLargeEntity) != 0)
        {
            s.Entity = r.ReadUShort();
            s.Channel = r.ReadChar();
        }
        else
        {
            int packed = r.ReadUShort();
            s.Entity = packed >> 3;
            s.Channel = packed & 7;
        }
        s.SoundIndex = (mask & DpProtocol.SndLargeSound) != 0 ? r.ReadUShort() : r.ReadByte();
        s.Origin = r.ReadVector();
        if (r.BadRead)
            return;
        // Out-of-range indices are logged and the sound dropped in DarkPlaces; the payload has been
        // read in full by now, so parsing carries on.
        if (s.SoundIndex >= DpProtocol.MaxSounds || s.Entity >= DpProtocol.MaxEdicts)
            return;
        _handler.OnSound(s);
    }

    // CL_VM_Parse_TempEntity, then CL_ParseTempEntity if the QuakeC declined.
    private DpParseStatus ParseTempEntity(DpMessageReader r, out string? error)
    {
        error = null;
        int start = r.Position;
        DpPayloadResult result = _handler.OnTempEntity(r);
        if (result == DpPayloadResult.Consumed)
            return DpParseStatus.Complete;
        if (result == DpPayloadResult.Abort)
        {
            error = "temp entity needs the game's QuakeC (CSQC_Parse_TempEntity)";
            return DpParseStatus.Aborted;
        }
        // The QuakeC may have read a few bytes before deciding the effect was not its own.
        r.Position = start;
        r.BadRead = false;
        if (!DpTempEntityParser.TryParse(r, out DpTempEntity te))
        {
            if (r.BadRead)
                return DpParseStatus.Complete; // empty payload: reported as a short message by the caller
            return Fail(out error, $"CL_ParseTempEntity: bad type {(int)te.Type}");
        }
        if (!r.BadRead)
            _handler.OnEngineTempEntity(te);
        return DpParseStatus.Complete;
    }

    // CSQC_ReadEntities: entity numbers until a zero; bit 15 means remove; otherwise the QuakeC
    // function CSQC_Ent_Update reads the update, however long it is.
    private DpParseStatus ParseCsqcEntities(DpMessageReader r, out string? error)
    {
        error = null;
        while (true)
        {
            int n = r.ReadUShort();
            if (n == 0 || r.BadRead)
                return DpParseStatus.Complete;
            int entity = n & 0x7FFF;
            if ((n & 0x8000) != 0)
            {
                _handler.OnCsqcEntityRemove(entity);
                continue;
            }
            if (_handler.OnCsqcEntityUpdate(entity, r) != DpPayloadResult.Consumed)
            {
                error = $"entity {entity} needs the game's QuakeC (CSQC_Ent_Update)";
                return DpParseStatus.Aborted;
            }
        }
    }
}
