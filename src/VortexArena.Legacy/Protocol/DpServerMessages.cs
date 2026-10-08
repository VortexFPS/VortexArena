// The decoded forms of the server messages parsed in DpServerMessageParser.cs, and the callback
// interface they are delivered through. Field layouts follow Base/darkplaces/cl_parse.c
// (CL_ParseServerInfo, CL_ParseClientdata, CL_ParseStartSoundPacket, CL_ParseStaticSound,
// CL_ParseEffect, CL_ParseTempEntity), cl_particles.c CL_ParseParticleEffect and view.c V_ParseDamage.
using System.Numerics;

namespace VortexArena.Legacy.Protocol;

/// <summary>svc_serverinfo: the level the server is running and everything it precached.</summary>
public sealed class DpServerInfo
{
    public int Protocol { get; init; }
    public int MaxClients { get; init; }
    /// <summary>GAME_COOP (0) or GAME_DEATHMATCH (1).</summary>
    public int GameType { get; init; }
    /// <summary>The map's title (worldspawn "message"), not its file name.</summary>
    public string WorldMessage { get; init; } = "";
    /// <summary>Model names; index 0 is unused and empty, index 1 is the map ("maps/x.bsp"), and
    /// "*N" names are the map's submodels. A model index on the wire is an index into this list.</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    /// <summary>Sound names, index 0 unused, without the leading "sound/".</summary>
    public IReadOnlyList<string> Sounds { get; init; } = Array.Empty<string>();
    /// <summary>cl.worldname: <see cref="Models"/>[1], or empty.</summary>
    public string WorldModel => Models.Count > 1 ? Models[1] : "";
}

/// <summary>svc_clientdata: per-client state that is not an entity. Fields whose bit was clear keep
/// the value DarkPlaces resets them to before reading (zero, or 1.0 for the zoom).</summary>
public struct DpClientData
{
    public int Bits;
    public bool HasViewHeight;
    public int ViewHeight;
    public int IdealPitch;
    public Vector3 PunchAngle;
    public Vector3 PunchVector;
    public Vector3 Velocity;
    public bool HasItems;
    public int Items;
    public bool OnGround;
    public bool InWater;
    public bool HasViewZoom;
    /// <summary>STAT_VIEWZOOM as sent: 255 is no zoom.</summary>
    public int ViewZoom;
}

/// <summary>svc_sound.</summary>
public struct DpSound
{
    public int Entity;
    public int Channel;
    public int SoundIndex;
    /// <summary>0..255.</summary>
    public int Volume;
    public float Attenuation;
    /// <summary>Playback rate, 1.0 normal.</summary>
    public float Speed;
    public Vector3 Origin;
}

/// <summary>svc_spawnstaticsound / svc_spawnstaticsound2: a looping ambient sound.</summary>
public struct DpStaticSound
{
    public Vector3 Origin;
    public int SoundIndex;
    public int Volume;
    public int Attenuation;
}

/// <summary>svc_particle.</summary>
public struct DpParticle
{
    public Vector3 Origin;
    public Vector3 Direction;
    /// <summary>Particle count; the wire value 255 means 1024 (an explosion).</summary>
    public int Count;
    public int Color;
}

/// <summary>svc_effect / svc_effect2: a sprite animation played once at a point.</summary>
public struct DpEffect
{
    public Vector3 Origin;
    public int ModelIndex;
    public int StartFrame;
    public int FrameCount;
    public int FrameRate;
}

/// <summary>svc_pointparticles / svc_pointparticles1 (velocity zero, count 1).</summary>
public struct DpPointParticles
{
    public int EffectIndex;
    public Vector3 Origin;
    public Vector3 Velocity;
    public int Count;
}

/// <summary>svc_trailparticles.</summary>
public struct DpTrailParticles
{
    public int Entity;
    public int EffectIndex;
    public Vector3 Start;
    public Vector3 End;
}

/// <summary>
/// One engine temp entity (the TE_* messages CL_ParseTempEntity decodes itself). Which fields are
/// meaningful depends on <see cref="Type"/>; unused ones are zero. Positions: <see cref="Origin"/> is
/// the point, box minimum or beam start; <see cref="Origin2"/> the box maximum or beam end.
/// </summary>
public struct DpTempEntity
{
    public TempEntityType Type;
    public Vector3 Origin;
    public Vector3 Origin2;
    /// <summary>Direction, velocity, or angles (TE_TEI_G3).</summary>
    public Vector3 Direction;
    public int Count;
    /// <summary>Beam owner entity (TE_LIGHTNING*, TE_BEAM).</summary>
    public int Entity;
    /// <summary>Palette colour, or the first colour of a range (TE_EXPLOSION2).</summary>
    public int ColorStart;
    /// <summary>Length of the palette range (TE_EXPLOSION2), or the gravity flag (TE_PARTICLECUBE).</summary>
    public int ColorLength;
    /// <summary>Light colour, 0..2 per channel (TE_EXPLOSION3, TE_EXPLOSIONRGB, TE_CUSTOMFLASH).</summary>
    public Vector3 Color;
    /// <summary>Speed (TE_BLOODSHOWER), random velocity (TE_PARTICLECUBE) or lifetime (TE_CUSTOMFLASH).</summary>
    public float Speed;
    public float Radius;
    /// <summary>Beam model name (TE_LIGHTNING4NEH).</summary>
    public string? Model;
}

/// <summary>What a handler did with a payload only it can measure.</summary>
public enum DpPayloadResult
{
    /// <summary>Read exactly the payload; parsing continues after it.</summary>
    Consumed,
    /// <summary>Did not touch it (svc_temp_entity only): the engine's own decoder runs instead.</summary>
    NotHandled,
    /// <summary>Cannot tell where the payload ends. The rest of this message is abandoned, because
    /// without the length there is no way to find the next message id.</summary>
    Abort,
}

/// <summary>
/// Receives every decoded server message. Each method has an empty default, so an implementation
/// overrides only what it uses.
///
/// Two messages are different in kind. svc_csqcentities and svc_temp_entity carry data whose layout
/// is defined by the game's client QuakeC (csprogs.dat), not by the engine: DarkPlaces calls the
/// QuakeC functions CSQC_Ent_Update and CSQC_Parse_TempEntity, which pull as many bytes as they
/// want through ReadByte/ReadShort/ReadCoord builtins. There is no length prefix. For those the
/// handler is given the live reader, positioned at the payload, and must leave it positioned just
/// past it, or say that it cannot.
/// </summary>
public interface IDpClientHandler
{
    void OnNop() { }
    void OnDisconnect() { }
    void OnUpdateStat(int index, int value) { }
    void OnVersion(int protocol) { }
    void OnSetView(int entity) { }
    void OnSound(in DpSound sound) { }
    void OnTime(float time) { }
    void OnPrint(string text) { }
    /// <summary>Text the server wants run as console commands. May be a fragment of a line; see <see cref="DpStuffTextBuffer"/>.</summary>
    void OnStuffText(string text) { }
    void OnSetAngle(Vector3 angles) { }
    void OnServerInfo(DpServerInfo info) { }
    void OnLightStyle(int style, string map) { }
    void OnUpdateName(int client, string name) { }
    void OnUpdateFrags(int client, int frags) { }
    void OnClientData(in DpClientData data) { }
    void OnStopSound(int entity, int channel) { }
    void OnUpdateColors(int client, int colors) { }
    void OnParticle(in DpParticle particle) { }
    void OnDamage(int armor, int blood, Vector3 from) { }
    void OnSpawnStatic(in EntityState state) { }
    void OnSpawnBaseline(int entity, in EntityState baseline) { }
    void OnSetPause(bool paused) { }
    void OnSignonNum(int stage) { }
    void OnCenterPrint(string text) { }
    void OnKilledMonster() { }
    void OnFoundSecret() { }
    void OnSpawnStaticSound(in DpStaticSound sound) { }
    void OnIntermission() { }
    void OnFinale(string text) { }
    void OnCdTrack(int track, int loopTrack) { }
    void OnSellScreen() { }
    void OnCutscene(string text) { }
    void OnShowLmp(string label, string picture, int x, int y) { }
    void OnHideLmp(string label) { }
    void OnSkybox(string name) { }
    /// <summary>svc_downloaddata: <paramref name="data"/> belongs at byte <paramref name="start"/> of the
    /// file being downloaded. An empty block marks the end and must be acknowledged like any other.</summary>
    void OnDownloadData(int start, ReadOnlySpan<byte> data) { }
    void OnEffect(in DpEffect effect) { }
    /// <summary>svc_precache: a model or sound added after svc_serverinfo.</summary>
    void OnPrecache(int index, bool isSound, string name) { }
    /// <summary>svc_entities, after the deltas were applied to <paramref name="entities"/>.</summary>
    void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities) { }
    void OnTrailParticles(in DpTrailParticles trail) { }
    void OnPointParticles(in DpPointParticles particles) { }

    /// <summary>
    /// svc_temp_entity. <paramref name="reader"/> is positioned at the first payload byte (for an
    /// engine temp entity that is its TE_* type). This is CSQC_Parse_TempEntity: return
    /// <see cref="DpPayloadResult.Consumed"/> after reading the whole effect,
    /// <see cref="DpPayloadResult.NotHandled"/> to have the engine decode it (the parser rewinds the
    /// reader first, as DarkPlaces does, and then calls <see cref="OnEngineTempEntity"/>), or
    /// <see cref="DpPayloadResult.Abort"/>. The default defers to the engine decoder.
    /// </summary>
    DpPayloadResult OnTempEntity(DpMessageReader reader) => DpPayloadResult.NotHandled;

    /// <summary>A temp entity the engine decoder understood.</summary>
    void OnEngineTempEntity(in DpTempEntity tempEntity) { }

    /// <summary>
    /// One entity of svc_csqcentities. <paramref name="reader"/> is positioned just after the entity
    /// number; this is CSQC_Ent_Update. Only the game's QuakeC knows how long the update is, so the
    /// default answers <see cref="DpPayloadResult.Abort"/>.
    /// </summary>
    DpPayloadResult OnCsqcEntityUpdate(int entity, DpMessageReader reader) => DpPayloadResult.Abort;

    /// <summary>svc_csqcentities told the client to remove an entity (CSQC_Ent_Remove). No payload.</summary>
    void OnCsqcEntityRemove(int entity) { }
}
