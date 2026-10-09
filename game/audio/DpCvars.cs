using System.Collections.Generic;
using System.Globalization;
using VortexArena.Engine.Audio;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Audio;

/// <summary>
/// The console variables DarkPlaces' sound code reads, taken from a cvar store into a
/// <see cref="DpSoundSettings"/>: once a frame, as S_Update and SND_Spatialize read them. A variable the store
/// does not have takes DarkPlaces' own default (snd_main.c).
/// </summary>
public static class DpCvars
{
    private static readonly string[] s_ent = Names("snd_entchannel{0}volume"), s_player = Names("snd_playerchannel{0}volume"),
        s_world = Names("snd_worldchannel{0}volume"), s_csqc = Names("snd_csqcchannel{0}volume"), s_channel = Names("snd_channel{0}volume");
    private static readonly Dictionary<int, string> s_extraNames = new();

    private static string[] Names(string format)
    {
        string[] names = new string[8];
        for (int i = 0; i < 8; i++) names[i] = string.Format(CultureInfo.InvariantCulture, format, i);
        return names;
    }

    public static float Get(CvarService cvars, string name, float fallback)
    {
        if (!cvars.Has(name)) return fallback;
        float value = cvars.GetFloat(name);
        return float.IsFinite(value) ? value : fallback;
    }

    public static void Read(CvarService cvars, DpSoundSettings s)
    {
        s.Volume = Get(cvars, "volume", 0.7f);
        s.MasterVolume = Get(cvars, "mastervolume", 0.7f);
        s.StaticVolume = Get(cvars, "snd_staticvolume", 1f);
        s.SoundRadius = Get(cvars, "snd_soundradius", 1200f);
        s.AttenuationExponent = Get(cvars, "snd_attenuation_exponent", 1f);
        s.AttenuationDecibel = Get(cvars, "snd_attenuation_decibel", 0f);
        s.MaxChannelVolume = Get(cvars, "snd_maxchannelvolume", 10f);
        s.Occlusion = (int)Get(cvars, "snd_spatialization_occlusion", 1f);
        s.SpatializationControl = Get(cvars, "snd_spatialization_control", 0f) != 0;
        s.SpatializationMin = Get(cvars, "snd_spatialization_min", 0.70f);
        s.SpatializationMax = Get(cvars, "snd_spatialization_max", 0.95f);
        s.SpatializationMinRadius = Get(cvars, "snd_spatialization_min_radius", 10000f);
        s.SpatializationMaxRadius = Get(cvars, "snd_spatialization_max_radius", 100f);
        s.SpatializationPower = Get(cvars, "snd_spatialization_power", 0f);
        s.SwapStereo = Get(cvars, "snd_swapstereo", 0f) != 0;
        s.SoftClip = (int)Get(cvars, "snd_softclip", 0f);
        s.WaterFx = Get(cvars, "snd_waterfx", 1f);
        s.IdenticalSoundRandomizationTime = Get(cvars, "snd_identicalsoundrandomization_time", 0.1f);
        s.IdenticalSoundRandomizationTics = Get(cvars, "snd_identicalsoundrandomization_tics", 0f);
        s.SoundsMoveWithEntities = Get(cvars, "cl_gameplayfix_soundsmovewithentities", 1f) != 0;
        s.StartLoopingSounds = Get(cvars, "snd_startloopingsounds", 1f) != 0;
        s.StartNonLoopingSounds = Get(cvars, "snd_startnonloopingsounds", 1f) != 0;
        for (int i = 0; i < 8; i++)
        {
            s.EntChannelVolume[i] = Get(cvars, s_ent[i], 1f);
            s.PlayerChannelVolume[i] = Get(cvars, s_player[i], 1f);
            s.WorldChannelVolume[i] = Get(cvars, s_world[i], 1f);
            s.CsqcChannelVolume[i] = Get(cvars, s_csqc[i], 1f);
            s.ChannelVolume[i] = Get(cvars, s_channel[i], 1f);
        }
        // "snd_channel%dvolume" for a channel outside 0..7 (Xonotic declares 8, music, and 9, ambient).
        s.ExtraChannelVolume ??= channel =>
        {
            string? name;
            lock (s_extraNames)
                if (!s_extraNames.TryGetValue(channel, out name))
                    s_extraNames[channel] = name = "snd_channel" + channel.ToString(CultureInfo.InvariantCulture) + "volume";
            return Get(cvars, name, 1.0f);
        };
    }
}
