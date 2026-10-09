using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using VortexArena.Engine.Audio;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Audio;

/// <summary>
/// What the native game's sound players share on the way to DarkPlaces' mixer (<see cref="DpAudio"/>):
/// whether that path is on, the sample bank of the mounted game data, the cvars, and the calls a player
/// with no place in the world makes (the hit beep, the announcer, a menu click).
///
/// <c>snd_darkplaces</c> (default 1) selects it. 0 keeps the previous path for one release: engine
/// audio nodes on per-channel buses, with the engine's own panning and resampling.
/// </summary>
public static class DpNative
{
    private static readonly ConditionalWeakTable<AssetLoader, DpSampleBank> s_banks = new();
    private static CvarService? s_cvars;

    /// <summary>True when the native game's sounds go through the DarkPlaces mixer.</summary>
    public static bool Active { get; private set; }
    public static DpSoundSettings Settings { get; } = DpSoundSettings.Xonotic();

    /// <summary>Read <c>snd_darkplaces</c> and the sound cvars. Called when the audio settings are applied (start-up, and on every change).</summary>
    public static void Configure(CvarService cvars)
    {
        s_cvars = cvars;
        Active = !DpAudio.ForceEngineNodes && DpCvars.Get(cvars, "snd_darkplaces", 1f) != 0;
        DpAudio.EnsureCapture();
        if (!Active) return;
        ReadSettings();
        DpAudio.Instance.Sound.Settings = Settings;
    }

    /// <summary>The cvars as DarkPlaces reads them: once a frame.</summary>
    public static void ReadSettings()
    {
        if (s_cvars is { } cvars) DpCvars.Read(cvars, Settings);
    }

    /// <summary>
    /// The sample bank behind a player's "AudioLoader" delegate (the host sets it to AssetLoader.LoadSound),
    /// or null when the delegate is not the asset loader's: the caller then keeps its engine-node path.
    /// </summary>
    public static DpSampleBank? Bank(Delegate? audioLoader)
    {
        if (audioLoader?.Target is not AssetLoader assets) return null;
        return s_banks.GetValue(assets, static a => new DpSampleBank(a.Vfs.Exists, a.Vfs.ReadBytes));
    }

    /// <summary>
    /// A sound of the client itself with no place in the world, as Xonotic's client program plays one:
    /// "sound(world, channel, sample, volume, ATTEN_NONE)" - entity MAX_EDICTS (the program's world), so
    /// the volume is the given one times snd_channelNvolume, "volume" and "mastervolume".
    /// Returns the mixer channel, or -1 (not active, no such sample: the caller falls back).
    /// </summary>
    public static int Local(Delegate? audioLoader, string sample, int channel, float volume, float speed = 1f)
    {
        if (!Active || Bank(audioLoader) is not { } bank) return -1;
        if (bank.Get(sample, forPlay: true) is not { Failed: false } sfx) return -1;
        return DpAudio.Instance.Sound.StartSound(DpSoundSystem.MaxEdicts, channel, sfx, Vector3.Zero, volume, 0f, 0f, 0, speed);
    }

    /// <summary>
    /// snd_spatialization_occlusion bit 1: the source's cluster is not in the listener's "fat" visible set
    /// (DarkPlaces: every leaf within 2 units of the ear). A source in solid is never occluded.
    /// </summary>
    public static bool PvsOccluded(BspPvs? pvs, Vector3 listener, Vector3 source)
    {
        if (pvs is not { HasVis: true }) return false;
        int cluster = pvs.LeafCluster(pvs.FindLeaf(source));
        if (cluster < 0) return false;
        for (int corner = 0; corner < 9; corner++)
        {
            Vector3 at = corner == 8 ? listener : listener + new Vector3((corner & 1) != 0 ? 2 : -2, (corner & 2) != 0 ? 2 : -2, (corner & 4) != 0 ? 2 : -2);
            int from = pvs.LeafCluster(pvs.FindLeaf(at));
            if (from >= 0 && pvs.ClustersVisible(from, cluster)) return false;
        }
        return true;
    }
}
