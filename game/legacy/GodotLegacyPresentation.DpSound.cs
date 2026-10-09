// A legacy session's sounds on DarkPlaces' own channel table and mixer (src/VortexArena.Engine/Audio,
// game/audio/DpAudio.cs). Port of the callers around them: clvm_cmds.c VM_CL_sound / VM_CL_pointsound /
// VM_CL_ambientsound (which entity number a program's sound is given), cl_parse.c CL_ParseStartSoundPacket /
// CL_ParseStaticSound / svc_stopsound / svc_cdtrack / svc_setpause, csprogs.c CL_VM_GetEntitySoundOrigin and
// CL_VM_GetViewEntity, snd_main.c S_Play_Common ("play", "play2", "playvol"), S_SoundLength, and the cvars
// S_Update and SND_Spatialize read each frame.
using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Engine.Audio;
using VortexArena.Formats.Bsp;
using VortexArena.Game.Audio;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // False: the engine-node path of GodotLegacyPresentation.Sound.cs (VORTEX_AUDIO_ENGINE_NODES=1, the other arm of a comparison).
    private readonly bool _dpSound = !DpAudio.ForceEngineNodes;
    private DpAudio? _dpAudio;
    private DpSampleBank? _dpBank;
    private DpCdAudio? _dpCd;
    private readonly DpSoundSettings _dpSettings = new();
    private BspPvs? _dpPvs;
    private BspData? _dpPvsMap;
    private static readonly bool s_dpTrace = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_TRACE"));
    private static readonly bool s_dpNoIdleMute = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_NOIDLEMUTE"));
    private double _dpTraceAt;

    private sealed class DpWorld : IDpSoundWorld
    {
        public GodotLegacyPresentation P = null!;
        public bool IsViewEntity(int entnum) => P.IsViewEntity(entnum);
        public int MaxClients => P._state?.MaxClients ?? 0;

        // "update sound origin if we know about the entity"
        public DpEntityOrigin EntityOrigin(int entnum, ref NVec3 origin)
        {
            if (entnum >= DpSoundSystem.MaxEdicts)
            {
                // "if (CLVM_prog->loaded && ch->entnum > MAX_EDICTS) if (!CL_VM_GetEntitySoundOrigin(...)) ch->entnum = MAX_EDICTS"
                if (P._host is not { } host || entnum == DpSoundSystem.MaxEdicts) return DpEntityOrigin.Keep;
                return ProgramEntity(host, entnum - DpSoundSystem.MaxEdicts, ref origin);
            }
            if (P._state?.NetworkEntities is { } table && entnum < table.Count)
            {
                ref readonly EntityState entity = ref table.Current(entnum);
                if (entity.IsActive)
                {
                    if (!Finite(entity.Origin)) return DpEntityOrigin.Keep;
                    origin = new NVec3(entity.Origin.X, entity.Origin.Y, entity.Origin.Z);
                    return DpEntityOrigin.Moved;
                }
            }
            // "else if (CLVM_prog->loaded && cl.csqc_server2csqcentitynumber[ch->entnum])": a server entity the
            // program draws itself (every player in Xonotic) is followed through the program's entity for it.
            if (P._host is { } program && program.EdictForServerEntity(entnum) is > 0 and var linked) return ProgramEntity(program, linked, ref origin);
            return DpEntityOrigin.Keep;
        }

        private static DpEntityOrigin ProgramEntity(CsqcHost host, int edict, ref NVec3 origin)
        {
            if (edict <= 0 || edict >= host.Vm.NumEdicts || host.Vm.IsFree(edict)) return DpEntityOrigin.Removed;   // "entity was removed, disown sound"
            QcVector at = host.Vm.FieldVector(edict, host.Fields.Origin);
            if (!Finite(at)) return DpEntityOrigin.Keep;
            origin = new NVec3(at.X, at.Y, at.Z);
            return DpEntityOrigin.Moved;
        }

        // snd_spatialization_occlusion bit 1: the source's cluster is not in the listener's "fat" visible set
        // (every leaf within 2 units of the ear). Bit 2 (a line-of-sight trace) is not ported; Xonotic leaves it off.
        public bool Occluded(NVec3 listener, NVec3 source, int mode)
        {
            if ((mode & 1) == 0 || P.DpPvs() is not { HasVis: true } pvs) return false;
            int cluster = pvs.LeafCluster(pvs.FindLeaf(source));
            if (cluster < 0) return false;
            for (int corner = 0; corner < 9; corner++)
            {
                NVec3 at = corner == 8 ? listener : listener + new NVec3((corner & 1) != 0 ? 2 : -2, (corner & 2) != 0 ? 2 : -2, (corner & 4) != 0 ? 2 : -2);
                int from = pvs.LeafCluster(pvs.FindLeaf(at));
                if (from >= 0 && pvs.ClustersVisible(from, cluster)) return false;
            }
            return true;
        }
    }

    private BspPvs? DpPvs()
    {
        if (!ReferenceEquals(_dpPvsMap, _levelBsp))
        {
            _dpPvsMap = _levelBsp;
            _dpPvs = _levelBsp is null ? null : new BspPvs(_levelBsp);
        }
        return _dpPvs;
    }

    private void InitializeDpSound()
    {
        DpAudio.EnsureCapture();
        if (!_dpSound) return;
        _dpAudio = DpAudio.Instance;
        _dpBank = new DpSampleBank(path => _vfs.Exists(path), path => _vfs.ReadBytes(path), text => _note(text));
        _dpCd = new DpCdAudio(_dpAudio.Sound, path => _vfs.Exists(path), path => _dpBank.Get(path, forPlay: true));
        _dpAudio.Sound.World = new DpWorld { P = this };
        ReadDpSettings();
        _dpAudio.Sound.Settings = _dpSettings;
    }

    private DpSfx? DpSample(string sample, bool forPlay)
    {
        if (sample.Length > 200 || !LegacyQcHost.IsSafePath(sample)) return null;
        return _dpBank!.Get(sample, forPlay);
    }

    private float DpCvar(string name, float fallback) => DpCvars.Get(_cvars, name, fallback);

    private void ReadDpSettings() => DpCvars.Read(_cvars, _dpSettings);

    // VM_CL_sound gives a program's entity the number MAX_EDICTS + its edict (the world: MAX_EDICTS itself,
    // which is also what VM_CL_pointsound uses); the engine's own effect sounds are entity -1.
    private static int DpOwner(int edict) => edict >= 0 ? DpSoundSystem.MaxEdicts + edict : -1;

    private bool DpPrecache(string sample)
    {
        if (string.IsNullOrEmpty(sample) || sample.Length > 200 || !LegacyQcHost.IsSafePath(sample)) return false;
        return DpSample(sample, forPlay: false) is { Failed: false };
    }

    private float DpLength(string sample) => DpSample(sample, forPlay: false) is { Failed: false } sfx ? sfx.LengthSeconds : -1;

    private int DpStart(int entnum, int channel, string sample, QcVector origin, float volume, float attenuation, float startPosition, int flags, float speed)
    {
        if (!Finite(origin) || !float.IsFinite(volume) || !float.IsFinite(attenuation) || !float.IsFinite(startPosition)) return -1;
        // S_FindName: the empty name is the "change volume" sample.
        DpSfx? sfx = sample.Length == 0 ? DpSoundSystem.ChangeVolume : DpSample(sample, forPlay: true);
        if (sfx is null) return -1;
        if (!float.IsFinite(speed) || speed <= 0) speed = 1f;
        _dpAudio!.Sound.ServerTickSeconds = _state is { } state ? state.ServerTime - state.ServerPrevTime : 0;
        int index = _dpAudio.Sound.StartSound(entnum, channel, sfx, new NVec3(origin.X, origin.Y, origin.Z), volume, attenuation, startPosition, flags, speed);
        if (index >= 0 && !ReferenceEquals(sfx, DpSoundSystem.ChangeVolume)) SoundsStarted++;
        if (s_debugEntities && _debugSounds.Count < 400)
            _debugSounds.Add(string.Create(CultureInfo.InvariantCulture, $"snd t {_time:0.00} ent {entnum} ch {channel} \"{sample}\" vol {volume:0.##} atten {attenuation:0.##} flags {flags} speed {speed:0.##} at {origin.X:0} {origin.Y:0} {origin.Z:0} -> channel {index}"));
        return index;
    }

    private void DpStartStatic(QcVector origin, string sample, float volume, float wireAttenuation)
    {
        if (!Finite(origin) || DpSample(sample, forPlay: true) is not { } sfx) return;
        if (_dpAudio!.Sound.StaticSound(sfx, new NVec3(origin.X, origin.Y, origin.Z), volume, wireAttenuation) >= 0) SoundsStarted++;
    }

    private bool DpLocal(string sample, int channel, float volume)
    {
        if (DpSample(sample, forPlay: true) is not { Failed: false } sfx) return false;
        bool started = _dpAudio!.Sound.LocalSound(sfx, channel, volume, _state?.ViewEntity ?? 0) >= 0;
        if (started) SoundsStarted++;
        return started;
    }

    /// <summary>
    /// The sound commands of DarkPlaces' console: "play" (at the listener, attenuation 1), "play2" (no
    /// attenuation), "playvol" (a volume after each name), "stopsound", "cd". A server sends "play2" for
    /// announcements, and a level's music is "cd loop".
    /// </summary>
    public void SoundCommand(IReadOnlyList<string> argv)
    {
        if (!_dpSound || _dpAudio is null || argv.Count == 0) return;
        switch (argv[0].ToLowerInvariant())
        {
            case "cd": _dpCd!.Command(argv); break;
            case "stopsound": StopAllSounds(); break;
            case "play":
            case "play2":
            case "playvol":
            {
                bool withVolume = argv[0].Equals("playvol", StringComparison.OrdinalIgnoreCase);
                float attenuation = argv[0].Equals("play2", StringComparison.OrdinalIgnoreCase) ? 0.0f : 1.0f;
                NVec3 ear = Coords.ToQuake(_listenerTransform.Origin);
                for (int i = 1; i < argv.Count; i++)
                {
                    // "Get the name, and appends ".wav" as an extension if there's none"
                    string name = argv[i];
                    if (name.LastIndexOf('.') < 0) name += ".wav";
                    float volume = 1.0f;
                    if (withVolume)
                    {
                        if (++i >= argv.Count) break;
                        float.TryParse(argv[i], NumberStyles.Float, CultureInfo.InvariantCulture, out volume);
                    }
                    if (DpSample(name, forPlay: true) is not { Failed: false } sfx) continue;
                    int index = _dpAudio.Sound.StartSound(-1, 0, sfx, ear, volume, attenuation);
                    if (index >= 0)
                    {
                        _dpAudio.Sound.SetChannelFlag(index, DpSoundSystem.ChannelFlagLocalSound, true);
                        SoundsStarted++;
                    }
                }
                break;
            }
        }
    }

    /// <summary>The music track playing, "" for none.</summary>
    public string MusicTrack => _dpCd?.Track ?? "";

    private bool _dpPaused;
    private double _dpOfflineTime = double.NaN;

    // S_Update, once a frame.
    private void DpUpdateSounds()
    {
        using var _prof = VortexArena.Common.Diagnostics.Prof.Sample("dpaudio");
        DpAudio audio = _dpAudio!;
        long t0 = LegacyPerfLog.Stamp();
        if (!_listenerOverridden) _listenerTransform = _camera.Transform;
        ReadDpSettings();
        bool paused = _state?.Paused ?? false;
        if (paused != _dpPaused)
        {
            _dpPaused = paused;
            audio.Sound.PauseGameSounds(paused);   // "S_PauseGameSounds (cl.paused)"
        }
        Basis basis = _listenerTransform.Basis;
        DpListener listener = new()
        {
            Origin = Coords.ToQuake(_listenerTransform.Origin),
            Forward = Coords.ToQuake(-basis.Z),
            Left = Coords.ToQuake(-basis.X),
            Up = Coords.ToQuake(basis.Y),
        };
        // snd_mutewhenidle: 1 = silent while the window is not the active one, 2 = only while minimised.
        int idle = (int)DpCvar("snd_mutewhenidle", 1f);
        Window? window = _sceneRoot.GetWindow();
        audio.Blocked = !s_dpNoIdleMute && window is not null
            && ((idle == 1 && !window.HasFocus()) || (idle == 2 && window.Mode == Window.ModeEnum.Minimized));
        audio.Sound.ServerTickSeconds = _state is { } state ? state.ServerTime - state.ServerPrevTime : 0;
        audio.GameFrame = Godot.Engine.GetProcessFrames();
        audio.Sound.Settings = _dpSettings;
        long t1 = LegacyPerfLog.Stamp();
        audio.Sound.Update(listener, Math.Clamp(_time - _oldTime, 0, 0.25), underwater: false);
        long t2 = LegacyPerfLog.Stamp();
        _dpCd!.Update(DpCvar("bgmvolume", 1f));   // CDAudio_Update
        if (LegacyPerfLog.Enabled)
        {
            long t3 = LegacyPerfLog.Stamp();
            double ms = (t3 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms >= 2.0)
                LegacyPerfLog.Event(string.Create(CultureInfo.InvariantCulture, $"sound update: cvars and listener {(t1 - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0} us; spatialise {(t2 - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}; music {(t3 - t2) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}; channels {audio.Sound.TotalSounds}"), t0);
        }
        // An offline capture (VORTEX_AUDIO_OFFLINE): as much sound as the server's clock moved on, which for a
        // recording stepped one message a frame is what DarkPlaces' own capture writes per picture.
        if (DpAudio.OfflineFrames > 0 && _state is { } clock)
        {
            if (double.IsNaN(_dpOfflineTime)) _dpOfflineTime = clock.ServerTime;
            int frames = (int)Math.Round((clock.ServerTime - _dpOfflineTime) * audio.MixRate);
            if (frames > 0)
            {
                frames = Math.Min(frames, audio.MixRate);
                audio.MixOffline(frames);
                _dpOfflineTime += frames / (double)audio.MixRate;
            }
            else if (frames < 0) _dpOfflineTime = clock.ServerTime;   // a new level's clock
        }

        if (s_dpTrace && _time >= _dpTraceAt)
        {
            _dpTraceAt = _time + 1.0;
            _note(string.Create(CultureInfo.InvariantCulture,
                $"audio: t {_time:0.00} ear {listener.Origin.X:0.0} {listener.Origin.Y:0.0} {listener.Origin.Z:0.0} forward {listener.Forward.X:0.000} {listener.Forward.Y:0.000} {listener.Forward.Z:0.000} channels {audio.Sound.TotalSounds} mixed {audio.Sound.MixedSounds} statics {audio.Sound.StaticChannels} music \"{MusicTrack}\" blocked {audio.Blocked}"));
        }
    }
}
