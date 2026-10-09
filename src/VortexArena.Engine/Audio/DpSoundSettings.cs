// The cvars Base/darkplaces/snd_main.c and snd_mix.c read, as one value object, and the listener S_Update is given.
using System;
using System.Numerics;

namespace VortexArena.Engine.Audio;

/// <summary>
/// Every console variable DarkPlaces' sound code reads while starting, spatialising and mixing a channel.
/// The defaults here are DarkPlaces' own (snd_main.c); <see cref="Xonotic"/> gives what Xonotic's shipped
/// configuration changes (xonotic-common.cfg, xonotic-client.cfg).
/// </summary>
public sealed class DpSoundSettings
{
    /// <summary>"volume": sound effects. Not applied to a CHANNELFLAG_FULLVOLUME channel (music).</summary>
    public float Volume = 0.7f;
    /// <summary>"mastervolume": applied to everything.</summary>
    public float MasterVolume = 0.7f;
    /// <summary>"snd_staticvolume": static (ambient) sounds, in place of the per-channel variables.</summary>
    public float StaticVolume = 1f;
    /// <summary>"snd_soundradius": distfade = attenuation / radius, fixed when a sound starts.</summary>
    public float SoundRadius = 1200f;
    public float AttenuationExponent = 1f;
    public float AttenuationDecibel;
    /// <summary>"snd_maxchannelvolume": 0 switches the clamps off (Xonotic).</summary>
    public float MaxChannelVolume = 10f;
    /// <summary>"snd_spatialization_occlusion": bit 1 = potentially-visible-set test, bit 2 = line of sight. An occluded sound is halved.</summary>
    public int Occlusion = 1;
    public bool SpatializationControl;
    public float SpatializationMin = 0.70f, SpatializationMax = 0.95f;
    public float SpatializationMinRadius = 10000f, SpatializationMaxRadius = 100f;
    public float SpatializationPower;
    public bool SwapStereo;
    /// <summary>"snd_softclip": 1 = the limiter runs when the output is 8 or 16 bit, 2 = always.</summary>
    public int SoftClip;
    /// <summary>Output sample width in bytes. DarkPlaces bounds snd_width to 2 (SND_MAX_WIDTH), so 16 bit.</summary>
    public int OutputWidth = 2;
    /// <summary>"snd_waterfx": strength of the low-pass applied while the view is under water, 0 to 2.</summary>
    public float WaterFx = 1f;
    public float IdenticalSoundRandomizationTime = 0.1f;
    public float IdenticalSoundRandomizationTics;
    /// <summary>"cl_gameplayfix_soundsmovewithentities".</summary>
    public bool SoundsMoveWithEntities = true;
    public bool StartLoopingSounds = true, StartNonLoopingSounds = true;

    /// <summary>snd_entchannelNvolume, snd_playerchannelNvolume, snd_worldchannelNvolume, snd_csqcchannelNvolume, snd_channelNvolume for N = 0..7.</summary>
    public readonly float[] EntChannelVolume = Ones(), PlayerChannelVolume = Ones(), WorldChannelVolume = Ones(), CsqcChannelVolume = Ones(), ChannelVolume = Ones();
    /// <summary>
    /// "snd_channel%dvolume" for an entity channel outside 0..7, asked by the channel's absolute number
    /// (CHAN_ENGINE2CVAR); a variable that does not exist counts as 1 (Cvar_VariableValueOr).
    /// </summary>
    public Func<int, float>? ExtraChannelVolume;

    private static float[] Ones() => new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

    /// <summary>
    /// DarkPlaces' defaults with what Xonotic's configuration sets on a client: volume 1, the "new style"
    /// attenuation (radius 2400, exponent 4), no per-channel clamp, the limiter on, and identical sounds
    /// started together delayed by up to one server tick instead of skipped into.
    /// </summary>
    public static DpSoundSettings Xonotic() => new()
    {
        Volume = 1f,
        SoundRadius = 2400f,
        AttenuationExponent = 4f,
        AttenuationDecibel = 0f,
        MaxChannelVolume = 0f,
        SoftClip = 1,
        IdenticalSoundRandomizationTime = -0.1f,
        IdenticalSoundRandomizationTics = 1f,
    };
}

/// <summary>Where the ears are: the origin and the axes of the matrix S_Update is given (Quake coordinates; left is +Y of the view).</summary>
public struct DpListener
{
    public Vector3 Origin, Forward, Left, Up;

    public static DpListener Identity => new() { Forward = Vector3.UnitX, Left = Vector3.UnitY, Up = Vector3.UnitZ };
}

/// <summary>What SND_Spatialize learns about the entity a channel belongs to.</summary>
public enum DpEntityOrigin
{
    /// <summary>Nothing known: the channel keeps the origin it has.</summary>
    Keep,
    /// <summary>The origin was written.</summary>
    Moved,
    /// <summary>"entity was removed, disown sound": the channel stays where it is and stops belonging to anyone.</summary>
    Removed,
}

/// <summary>What the sound code asks of the client around it.</summary>
public interface IDpSoundWorld
{
    /// <summary>"ch-&gt;entnum == cl.viewentity || ch-&gt;entnum == CL_VM_GetViewEntity()".</summary>
    bool IsViewEntity(int entnum);
    /// <summary>cl.maxclients: entities 1..maxclients take the snd_playerchannel variables.</summary>
    int MaxClients { get; }
    /// <summary>The first block of SND_Spatialize_WithSfx: where the entity's sounds come from now.</summary>
    DpEntityOrigin EntityOrigin(int entnum, ref Vector3 origin);
    /// <summary>Is the world between the listener and the source, by the methods <paramref name="mode"/> selects?</summary>
    bool Occluded(Vector3 listener, Vector3 source, int mode);
}

/// <summary>A world with no entities and no walls (the menu, tests).</summary>
public sealed class DpNullSoundWorld : IDpSoundWorld
{
    public int ViewEntity = -2;
    public bool IsViewEntity(int entnum) => entnum == ViewEntity;
    public int MaxClients => 0;
    public DpEntityOrigin EntityOrigin(int entnum, ref Vector3 origin) => DpEntityOrigin.Keep;
    public bool Occluded(Vector3 listener, Vector3 source, int mode) => false;
}
