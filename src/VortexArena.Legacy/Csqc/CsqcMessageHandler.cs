// Port of the cases of Base/darkplaces/cl_parse.c CL_ParseServerMessage that touch client state a
// program can see or that call into the program: svc_time, svc_clientdata, svc_updatestat*,
// svc_setview, svc_serverinfo, svc_precache, svc_updatename/frags/colors, svc_print, svc_centerprint,
// svc_stufftext, svc_signonnum, svc_setpause, svc_intermission/finale/cutscene, svc_damage,
// svc_temp_entity and svc_csqcentities.
using System.Numerics;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// Connects the protocol parser to the client program: an <see cref="IDpClientHandler"/> that keeps
/// <see cref="CsqcClientState"/> current and, while a program is loaded, hands it the messages it
/// decodes itself.
///
/// It exists apart from <see cref="CsqcHost"/> because messages arrive before there is a program:
/// the server describes the level first, and the program is loaded - by the owner, which sets
/// <see cref="Host"/> - only once that is known. Per message the owner calls
/// <see cref="BeginMessage"/>, runs the parser, then <see cref="EndMessage"/>.
/// </summary>
public sealed class CsqcMessageHandler : IDpClientHandler
{
    private DpMessageReader? _reader;
    private int _messageIndex = -1;

    public CsqcMessageHandler(CsqcClientState state, CsqcConsole console, ILegacyPresentation presentation)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Console = console ?? throw new ArgumentNullException(nameof(console));
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
    }

    public CsqcClientState State { get; }
    public CsqcConsole Console { get; }
    public ILegacyPresentation Presentation { get; }

    private CsqcHost? _host;

    /// <summary>The loaded program, or null. Setting it mid-message is allowed (the program starts
    /// reading the message from where the parser is).</summary>
    public CsqcHost? Host
    {
        get => _host;
        set
        {
            _host = value;
            if (value is not null && _reader is not null) value.BeginMessage(_reader, _messageIndex);
        }
    }

    /// <summary>Messages the handler does not act on itself are also passed here (downloads, entity
    /// frames, signon): whatever else the owner needs to see.</summary>
    public IDpClientHandler? Next { get; set; }

    /// <summary>Set when svc_serverinfo arrived in the current message: a new level, for which the
    /// owner must unload the old program and load the new one.</summary>
    public bool ServerInfoReceived { get; private set; }
    /// <summary>Asked as svc_serverinfo is handled: true if the level's files are still on their way
    /// (see <see cref="CsqcClientState.LevelLoadDeferred"/>).</summary>
    public Func<bool>? DeferLevelLoad { get; set; }

    /// <summary>Whether svc_time arrived in the current message: the cue for one frame.</summary>
    public bool TimeReceived { get; private set; }

    public long EngineTempEntities { get; private set; }

    public void BeginMessage(DpMessageReader reader, int messageIndex)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _messageIndex = messageIndex;
        ServerInfoReceived = false;
        TimeReceived = false;
        Host?.BeginMessage(reader, messageIndex);
    }

    /// <summary>
    /// The message is parsed. Runs the command buffer: DarkPlaces executes stuffed commands between
    /// frames, never from inside the parser, and a demo or a connection delivers at most one message
    /// that matters per frame - so "after each message" is the same point, and it is the one at which
    /// the order relative to the NEXT message's contents is preserved.
    /// </summary>
    public void EndMessage()
    {
        Host?.EndMessage();
        _reader = null;
        Console.Execute();
    }

    // ---- state -------------------------------------------------------------------------------------

    public void OnUpdateStat(int index, int value) => State.SetStat(index, value);

    public void OnSetView(int entity)
    {
        State.SetView(entity);
        Next?.OnSetView(entity);
    }

    public void OnTime(float time)
    {
        State.NetworkTimeReceived(time);
        TimeReceived = true;
        Host?.UpdateNetworkTimes();
        Next?.OnTime(time);
    }

    public void OnServerInfo(DpServerInfo info)
    {
        State.ApplyServerInfo(info);
        State.LevelLoadDeferred = DeferLevelLoad?.Invoke() ?? false;
        // CL_ParseServerInfo goes on to queue cl_begindownloads, which loads the world model first.
        Presentation.BeginLevel(State);
        ServerInfoReceived = true;
        Next?.OnServerInfo(info);
    }

    public void OnPrecache(int index, bool isSound, string name)
    {
        State.ApplyPrecache(index, isSound, name);
        if (isSound) Presentation.Sound.Precache(name);
        Next?.OnPrecache(index, isSound, name);
    }

    public void OnClientData(in DpClientData data) => State.ApplyClientData(data);
    public void OnUpdateName(int client, string name) => State.SetPlayerName(client, name);
    public void OnUpdateFrags(int client, int frags) => State.SetPlayerFrags(client, frags);
    public void OnUpdateColors(int client, int colors) => State.SetPlayerColors(client, colors);
    public void OnSetAngle(Vector3 angles) => State.ViewAngles = new QcVector(angles.X, angles.Y, angles.Z);
    public void OnSetPause(bool paused) => State.Paused = paused;

    public void OnSignonNum(int stage)
    {
        State.Signon = Math.Max(State.Signon, stage);
        Next?.OnSignonNum(stage);
    }

    public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities)
    {
        // "first update is the final signon stage"
        if (State.Signon == DpProtocol.Signons - 1) State.Signon = DpProtocol.Signons;
        State.NetworkEntities = entities;
        if (State.ViewEntity > 0 && State.ViewEntity < entities.Count)
        {
            Vector3 origin = entities.Current(State.ViewEntity).Origin;
            State.ViewEntityOrigin = new QcVector(origin.X, origin.Y, origin.Z);
        }
        Next?.OnEntityFrame(frame, entities);
    }

    private void SetIntermission(int value)
    {
        State.Intermission = value;
        Host?.UpdateIntermissionState(value);
    }

    public void OnIntermission() => SetIntermission(1);

    public void OnFinale(string text)
    {
        SetIntermission(2);
        Host?.CenterPrint(text);
    }

    public void OnCutscene(string text)
    {
        SetIntermission(3);
        Host?.CenterPrint(text);
    }

    public void OnDamage(int armor, int blood, Vector3 from) =>
        // V_ParseDamage hands the program (take, save, origin) in that order: blood is the damage taken.
        Host?.UpdateDamageGlobals(blood, armor, new QcVector(from.X, from.Y, from.Z));

    // ---- text --------------------------------------------------------------------------------------

    public void OnPrint(string text)
    {
        if (Host is { } host) host.ParsePrint(text);
        else Next?.OnPrint(text);
    }

    public void OnCenterPrint(string text)
    {
        if (Host is { } host) host.ParseCenterPrint(text);
        else Next?.OnCenterPrint(text);
    }

    public void OnStuffText(string text)
    {
        if (Host is { } host) host.ParseStuffCmd(text);
        // "if(!prog->loaded) Cbuf_AddText": the csqc_* special case needs no mention here, because
        // without a program nothing is protected from being set.
        else Console.AddText(text);
    }

    // ---- payloads only the program can measure -----------------------------------------------------

    public DpPayloadResult OnTempEntity(DpMessageReader reader)
    {
        if (Host is not { } host) return DpPayloadResult.NotHandled;
        // A program that has been stopped can no longer say whether the effect was one of its own, and
        // the engine decoder would read the game's payload as something else entirely.
        if (host.Disabled) return DpPayloadResult.Abort;
        int faultsBefore = host.FaultCount;
        if (host.ParseTempEntity()) return DpPayloadResult.Consumed;
        // A fault mid-payload: the program had started reading, so the bytes are not the engine's to
        // decode. The message cannot be continued.
        return host.FaultCount != faultsBefore ? DpPayloadResult.Abort : DpPayloadResult.NotHandled;
    }

    public void OnEngineTempEntity(in DpTempEntity tempEntity)
    {
        EngineTempEntities++;
        Presentation.Effects.TempEntity(tempEntity);
    }

    public DpPayloadResult OnCsqcEntityUpdate(int entity, DpMessageReader reader) =>
        Host is { } host && host.EntUpdate(entity) ? DpPayloadResult.Consumed : DpPayloadResult.Abort;

    public void OnCsqcEntityRemove(int entity) => Host?.EntRemove(entity);

    // ---- everything else goes to the owner ---------------------------------------------------------

    public void OnNop() => Next?.OnNop();
    public void OnDisconnect() => Next?.OnDisconnect();
    public void OnVersion(int protocol) => Next?.OnVersion(protocol);
    public void OnSound(in DpSound sound) => Next?.OnSound(sound);
    public void OnLightStyle(int style, string map)
    {
        Presentation.Scene.SetLightStyle(style, map);
        Next?.OnLightStyle(style, map);
    }
    public void OnStopSound(int entity, int channel) => Next?.OnStopSound(entity, channel);
    public void OnParticle(in DpParticle particle) => Next?.OnParticle(particle);
    public void OnSpawnStatic(in EntityState state) => Next?.OnSpawnStatic(state);
    public void OnSpawnBaseline(int entity, in EntityState baseline) => Next?.OnSpawnBaseline(entity, baseline);
    public void OnKilledMonster() => Next?.OnKilledMonster();
    public void OnFoundSecret() => Next?.OnFoundSecret();
    public void OnSpawnStaticSound(in DpStaticSound sound) => Next?.OnSpawnStaticSound(sound);
    public void OnCdTrack(int track, int loopTrack) => Next?.OnCdTrack(track, loopTrack);
    public void OnSellScreen() => Next?.OnSellScreen();
    public void OnShowLmp(string label, string picture, int x, int y) => Next?.OnShowLmp(label, picture, x, y);
    public void OnHideLmp(string label) => Next?.OnHideLmp(label);
    public void OnSkybox(string name) => Next?.OnSkybox(name);
    public void OnDownloadData(int start, ReadOnlySpan<byte> data) => Next?.OnDownloadData(start, data);
    public void OnEffect(in DpEffect effect) => Next?.OnEffect(effect);
    public void OnTrailParticles(in DpTrailParticles trail) => Next?.OnTrailParticles(trail);
    public void OnPointParticles(in DpPointParticles particles) => Next?.OnPointParticles(particles);
}
