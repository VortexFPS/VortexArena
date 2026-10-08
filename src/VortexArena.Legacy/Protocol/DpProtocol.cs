// Port of Base/darkplaces/protocol.h (svc_*/clc_* ids, SU_*/SND_*/E5_*/TE_* values), netconn.h
// (NETFLAG_*) and qdefs.h (the limits every count read off the wire is checked against).
namespace VortexArena.Legacy.Protocol;

/// <summary>
/// Numbers of the DarkPlaces 7 ("DP7") network protocol. Nothing here is ours to choose: a stock
/// Xonotic server speaks exactly these values, so they are reproduced, not designed.
/// </summary>
public static class DpProtocol
{
    /// <summary>PROTOCOL_DARKPLACES7 as written in svc_serverinfo (protocol.c protocolversioninfo).</summary>
    public const int ProtocolNumberDp7 = 3504;
    /// <summary>The name the client offers in the connect request's <c>protocols</c> key.</summary>
    public const string ProtocolNameDp7 = "DP7";

    // qdefs.h, the non-"minimal" block (the one a normal build uses).
    public const int NetMaxMessage = 65536;      // NET_MAXMESSAGE: largest reliable message
    public const int MaxPacketFragment = 1024;   // MAX_PACKETFRAGMENT: reliable data per datagram
    public const int NetHeaderSize = 8;          // NET_HEADERSIZE: two big-endian uint32
    public const int MaxEdicts = 32768;          // MAX_EDICTS (the wire reserves bit 15 of an entity number)
    public const int MaxModels = 8192;           // MAX_MODELS
    public const int MaxSounds = 4096;           // MAX_SOUNDS
    public const int MaxLightStyles = 256;       // MAX_LIGHTSTYLES
    public const int MaxScoreboard = 255;        // MAX_SCOREBOARD
    public const int MaxQPath = 128;             // MAX_QPATH: a precache name must be shorter than this
    public const int MaxInputLine = 16384;       // MAX_INPUTLINE: sizeof(cl_readstring), the cap on any wire string
    public const int MaxClStats = 256;           // MAX_CL_STATS (qstats.h)
    public const int Signons = 4;                // SIGNONS (client.h)
    public const int LatestFrameNums = 32;       // LATESTFRAMENUMS (client.h)
    public const int MaxDownloadAcks = 4;        // CL_MAX_DOWNLOADACKS (client.h)

    // netconn.h NetHeader flags. The low 16 bits of the first header word are the datagram length.
    public const uint NetFlagLengthMask = 0x0000FFFF;
    public const uint NetFlagData = 0x00010000;
    public const uint NetFlagAck = 0x00020000;
    public const uint NetFlagNak = 0x00040000;
    public const uint NetFlagEom = 0x00080000;
    public const uint NetFlagUnreliable = 0x00100000;
    public const uint NetFlagCtl = 0x80000000;

    // svc_clientdata bits (protocol.h SU_*). Punch, velocity and punchvec are three consecutive bits each.
    public const int SuViewHeight = 1 << 0, SuIdealPitch = 1 << 1, SuPunch1 = 1 << 2, SuVelocity1 = 1 << 5,
        SuItems = 1 << 9, SuOnGround = 1 << 10, SuInWater = 1 << 11, SuWeaponFrame = 1 << 12, SuArmor = 1 << 13,
        SuWeapon = 1 << 14, SuExtend1 = 1 << 15, SuPunchVec1 = 1 << 16, SuViewZoom = 1 << 19, SuExtend2 = 1 << 23;

    // svc_sound field mask (protocol.h SND_*).
    public const int SndVolume = 1 << 0, SndAttenuation = 1 << 1, SndLooping = 1 << 2, SndLargeEntity = 1 << 3,
        SndLargeSound = 1 << 4, SndSpeedUShort4000 = 1 << 5;
    public const int DefaultSoundPacketVolume = 255;      // sound.h
    public const float DefaultSoundPacketAttenuation = 1; // sound.h

    // EntityFrame5 delta bits (protocol.h E5_*). Bits 7, 15 and 23 say "another flag byte follows".
    public const uint E5FullUpdate = 1u << 0, E5Origin = 1u << 1, E5Angles = 1u << 2, E5Model = 1u << 3,
        E5Frame = 1u << 4, E5Skin = 1u << 5, E5Effects = 1u << 6, E5Extend1 = 1u << 7,
        E5Flags = 1u << 8, E5Alpha = 1u << 9, E5Scale = 1u << 10, E5Origin32 = 1u << 11, E5Angles16 = 1u << 12,
        E5Model16 = 1u << 13, E5Colormap = 1u << 14, E5Extend2 = 1u << 15,
        E5Attachment = 1u << 16, E5Light = 1u << 17, E5Glow = 1u << 18, E5Effects16 = 1u << 19,
        E5Effects32 = 1u << 20, E5Frame16 = 1u << 21, E5ColorMod = 1u << 22, E5Extend3 = 1u << 23,
        E5GlowMod = 1u << 24, E5ComplexAnimation = 1u << 25, E5TrailEffectNum = 1u << 26;

    /// <summary>entity_state_t.active values (protocol.h entity_state_active_t).</summary>
    public const byte ActiveNot = 0, ActiveNetwork = 1, ActiveShared = 2;
}

/// <summary>Server-to-client message ids (protocol.h svc_*). 54 was svc_sound2 before DP6.</summary>
public enum Svc : byte
{
    Bad = 0, Nop = 1, Disconnect = 2, UpdateStat = 3, Version = 4, SetView = 5, Sound = 6, Time = 7, Print = 8,
    StuffText = 9, SetAngle = 10, ServerInfo = 11, LightStyle = 12, UpdateName = 13, UpdateFrags = 14,
    ClientData = 15, StopSound = 16, UpdateColors = 17, Particle = 18, Damage = 19, SpawnStatic = 20,
    SpawnBaseline = 22, TempEntity = 23, SetPause = 24, SignonNum = 25, CenterPrint = 26, KilledMonster = 27,
    FoundSecret = 28, SpawnStaticSound = 29, Intermission = 30, Finale = 31, CdTrack = 32, SellScreen = 33,
    Cutscene = 34, ShowLmp = 35, HideLmp = 36, Skybox = 37,
    DownloadData = 50, UpdateStatUByte = 51, Effect = 52, Effect2 = 53, Precache = 54, SpawnBaseline2 = 55,
    SpawnStatic2 = 56, Entities = 57, CsqcEntities = 58, SpawnStaticSound2 = 59, TrailParticles = 60,
    PointParticles = 61, PointParticles1 = 62,
}

/// <summary>Client-to-server message ids (protocol.h clc_*).</summary>
public enum Clc : byte
{
    Bad = 0, Nop = 1, Disconnect = 2, Move = 3, StringCmd = 4, AckFrame = 50, AckDownloadData = 51,
}

/// <summary>Engine temp-entity types (protocol.h TE_*), the ones CL_ParseTempEntity understands.</summary>
public enum TempEntityType : byte
{
    Spike = 0, SuperSpike = 1, Gunshot = 2, Explosion = 3, TarExplosion = 4, Lightning1 = 5, Lightning2 = 6,
    WizSpike = 7, KnightSpike = 8, Lightning3 = 9, LavaSplash = 10, Teleport = 11, Explosion2 = 12, Beam = 13,
    Explosion3 = 16, Lightning4Neh = 17,
    Blood = 50, Spark = 51, BloodShower = 52, ExplosionRgb = 53, ParticleCube = 54, ParticleRain = 55,
    ParticleSnow = 56, GunshotQuad = 57, SpikeQuad = 58, SuperSpikeQuad = 59,
    ExplosionQuad = 70, SmallFlash = 72, CustomFlash = 73, FlameJet = 74, PlasmaBurn = 75,
    TeiG3 = 76, TeiSmoke = 77, TeiBigExplosion = 78, TeiPlasmaHit = 79,
}
