// Port of Base/darkplaces/snd_main.c S_StartSound_StartPosition_Flags (which channel a new sound takes:
// "if an identical sound has also been started this frame... / replace the channel of the same entity
// and entchannel"), S_StopSound, S_StaticSound, S_LocalSoundEx, S_PrecacheSound, S_SoundLength,
// S_GetEntChannelPosition, S_UpdateSounds / SND_Spatialize (the linear distance falloff "dist *
// ch->distfade" with distfade = attenuation / snd_soundradius) and S_Update's listener; and of
// cl_parse.c CL_ParseStartSoundPacket / CL_ParseStaticSound for the server's own sound messages.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // snd_main.c: the channel flags a caller may set (sound.h).
    private const int ChannelFlagForceLoop = 2;

    private const int MaxVoices = 64;
    private const int MaxStaticVoices = 64;
    private const int MaxLocalVoices = 8;
    private const int MaxSoundsPerFrame = 32;
    private const int MaxPrecacheAnswers = 4096;

    // DarkPlaces gives the program's entities the numbers MAX_EDICTS + n, so they cannot collide with the
    // server's entity numbers in the channel table (csprogs.c: "entrender->entitynumber = edictnum + MAX_EDICTS").
    private const int ProgramEntityBase = DpProtocol.MaxEdicts;

    private sealed class Voice
    {
        public AudioStreamPlayer3D Player = null!;
        /// <summary>The owner (see <see cref="ProgramEntityBase"/>), 0 for the world, and the entity channel.</summary>
        public int Owner, Channel;
        public bool InUse, Loop, Static;
        public float Volume, Attenuation, DistanceFade;
        public Vector3 Origin;
        public ulong Started;
    }

    private readonly List<Voice> _voices = new();
    private readonly List<AudioStreamPlayer> _localVoices = new();
    private readonly Dictionary<string, bool> _precached = new(StringComparer.Ordinal);
    private AudioListener3D _listener = null!;
    private bool _listenerOverridden;
    private Transform3D _listenerTransform = Transform3D.Identity;
    private int _soundsThisFrame, _staticVoices;
    private ulong _voiceClock;

    private void InitializeSound()
    {
        InitializeDpSound();
        if (_dpSound) return;
        _listener = new AudioListener3D { Name = "LegacyListener" };
        _sceneRoot.AddChild(_listener);
    }

    // A sample name from the program or the server: a relative path, which the loader roots under sound/
    // in the session's game data and nowhere else.
    private AudioStream? LoadSample(string sample)
    {
        if (string.IsNullOrEmpty(sample) || sample.Length > 200 || !LegacyQcHost.IsSafePath(sample)) return null;
        try { return _assets.LoadSound(sample); }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>S_PrecacheSound, as far as "does it exist": the stream is decoded when it first plays.</summary>
    bool ILegacySound.Precache(string sample)
    {
        if (_dpSound) return DpPrecache(sample);
        if (string.IsNullOrEmpty(sample) || sample.Length > 200 || !LegacyQcHost.IsSafePath(sample)) return false;
        if (_precached.TryGetValue(sample, out bool known)) return known;
        string stem = sample.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || sample.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? sample[..^4] : sample;
        string rooted = stem.StartsWith("sound/", StringComparison.Ordinal) ? stem : "sound/" + stem;
        bool exists = _vfs.Exists(rooted + ".ogg") || _vfs.Exists(rooted + ".wav") || _vfs.Exists(stem + ".ogg") || _vfs.Exists(stem + ".wav");
        if (_precached.Count >= MaxPrecacheAnswers) _precached.Clear();
        _precached[sample] = exists;
        return exists;
    }

    /// <summary>#534 soundlength: seconds, or -1 if the sample cannot be loaded.</summary>
    float ILegacySound.Length(string sample)
    {
        if (_dpSound) return DpLength(sample);
        if (LoadSample(sample) is not { } stream) return -1;
        double length = stream.GetLength();
        return length > 0 ? (float)length : -1;
    }

    void ILegacySound.Start(int edict, int channel, string sample, QcVector origin, float volume, float attenuation, float startPosition, int flags, float speed)
    {
        if (_dpSound)
        {
            DpStart(DpOwner(edict), channel, sample, origin, volume, attenuation, startPosition, flags, speed);
            return;
        }
        // Entity 0 is the world (pointsound); -1 is the engine's own; anything else is the program's edict.
        int owner = edict > 0 ? ProgramEntityBase + edict : 0;
        StartVoice(owner, channel, sample, origin, volume, attenuation, startPosition, (flags & ChannelFlagForceLoop) != 0, speed, isStatic: false);
    }

    void ILegacySound.StartStatic(QcVector origin, string sample, float volume, float attenuation)
    {
        if (_dpSound)
        {
            DpStartStatic(origin, sample, volume, attenuation);
            return;
        }
        if (_staticVoices >= MaxStaticVoices) return;
        // VM_CL_ambientsound passes attenuation * 64, as svc_spawnstaticsound carries it; S_StaticSound divides it back.
        if (StartVoice(0, 0, sample, origin, volume, attenuation / 64f, 0, loop: true, 1, isStatic: true)) _staticVoices++;
    }

    private bool StartVoice(int owner, int channel, string sample, QcVector origin, float volume, float attenuation, float startPosition, bool loop, float speed, bool isStatic)
    {
        if (_soundsThisFrame >= MaxSoundsPerFrame || !Finite(origin)) return false;
        long t0 = LegacyPerfLog.Stamp();
        if (!(volume > 0) || LoadSample(sample) is not { } stream) return false;
        stream = Decoded(stream, loop);
        long t1 = LegacyPerfLog.Stamp();
        _soundsThisFrame++;

        // "replace the channel of the same entity and entchannel"; channel 0 never replaces.
        Voice? voice = null;
        if (channel != 0 && !isStatic)
            foreach (Voice candidate in _voices)
                if (candidate.InUse && !candidate.Static && candidate.Owner == owner && candidate.Channel == channel) { voice = candidate; break; }
        voice ??= FreeVoice();
        if (voice is null) return false;

        AudioStreamPlayer3D player = voice.Player;
        long t2 = LegacyPerfLog.Stamp();
        player.Stop();
        long t3 = LegacyPerfLog.Stamp();
        voice.InUse = true;
        voice.Static = isStatic;
        voice.Owner = owner;
        voice.Channel = channel;
        voice.Loop = loop;
        voice.Volume = Math.Clamp(volume, 0f, 1f);
        voice.Attenuation = float.IsFinite(attenuation) ? Math.Clamp(attenuation, 0f, 16f) : 1f;
        voice.Origin = G(origin);
        voice.Started = ++_voiceClock;
        // "ch->distfade = attenuation / snd_soundradius.value", fixed when the sound starts.
        float radius = _cvars.Has("snd_soundradius") ? _cvars.GetFloat("snd_soundradius") : 1200f;
        voice.DistanceFade = radius > 0 ? voice.Attenuation / radius : 0;
        player.Stream = stream;
        long t4 = LegacyPerfLog.Stamp();
        player.PitchScale = float.IsFinite(speed) ? Math.Clamp(speed, 0.05f, 8f) : 1f;
        ApplyFalloff(voice);
        long t5 = LegacyPerfLog.Stamp();
        double length = stream.GetLength();
        long t6 = LegacyPerfLog.Stamp();
        player.Play(startPosition > 0 && startPosition < length ? startPosition : 0f);
        if (LegacyPerfLog.Enabled)
        {
            long t7 = LegacyPerfLog.Stamp();
            double ms = (t7 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms >= 0.2)
                LegacyPerfLog.Event(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"sound {sample} ({stream.GetClass()}): load {(t1 - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0} us; voice {(t2 - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}; stop {(t3 - t2) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}; set stream {(t4 - t3) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}; falloff {(t5 - t4) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}; length {(t6 - t5) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}; play {(t7 - t6) * 1e6 / System.Diagnostics.Stopwatch.Frequency:0}"), t0);
        }
        SoundsStarted++;
        if (s_debugEntities && _debugSounds.Count < 400)
            _debugSounds.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"snd t {_time:0.00} {(isStatic ? "static" : owner >= ProgramEntityBase ? "csqc" + (owner - ProgramEntityBase) : "ent" + owner)} ch {channel} \"{sample}\" vol {volume:0.##} atten {attenuation:0.##} loop {loop} len {length:0.00} at {origin.X:0} {origin.Y:0} {origin.Z:0} dist {voice.Origin.DistanceTo(_listenerTransform.Origin):0} view {IsViewEntity(owner)} gain {Mathf.DbToLinear(player.VolumeDb):0.###}"));
        return true;
    }

    // A voice nobody is using, else the oldest one-shot (S_StartSound's "pick the channel with the least life left").
    private Voice? FreeVoice()
    {
        Voice? oldest = null;
        foreach (Voice voice in _voices)
        {
            if (!voice.InUse) return voice;
            if (!voice.Static && !voice.Loop && (oldest is null || voice.Started < oldest.Started)) oldest = voice;
        }
        if (_voices.Count < MaxVoices)
        {
            // The falloff is computed here, the way DarkPlaces does it; Godot's own is switched off.
            AudioStreamPlayer3D player = new()
            {
                Name = "LegacyVoice" + _voices.Count,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                MaxDistance = 0,
            };
            _sceneRoot.AddChild(player);
            Voice made = new() { Player = player };
            _voices.Add(made);
            return made;
        }
        return oldest;
    }

    private readonly List<string> _debugSounds = new();

    // ---- short compressed samples, decoded once ------------------------------------------------------------
    //
    // Starting a compressed (Ogg Vorbis) sample makes the engine set up a decoder for it: half a millisecond to a
    // millisecond and a half on the main thread, every time - a shotgun blast, two pain sounds and a ricochet in
    // one frame were three milliseconds of it. DarkPlaces decodes a sample to PCM when it loads it and starting it
    // is free. Here a sample is decoded the first time it has been started: on a pool thread, through the
    // engine's own decoder and resampler (so it sounds as it did), into a PCM stream that every later start of it
    // uses. Only what a match actually plays is decoded, short samples only, and never a looping one.
    // VORTEX_LEGACY_NOPCM=1 turns it off (the other arm of a comparison).
    private const double MaxDecodedSeconds = 6;
    private const int MaxDecodedSamples = 512;
    private static readonly bool s_noPcm = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_NOPCM"));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<AudioStream, AudioStream?> _decoded = new();
    private long _decodedBytes;

    /// <summary>Samples decoded to PCM so far in this session, and the bytes they take.</summary>
    public int DecodedSamples => _decoded.Count;
    public long DecodedBytes => Interlocked.Read(ref _decodedBytes);

    private AudioStream Decoded(AudioStream stream, bool loop)
    {
        if (s_noPcm || loop || stream is not AudioStreamOggVorbis ogg) return stream;
        if (_decoded.TryGetValue(stream, out AudioStream? pcm)) return pcm ?? stream;
        if (_decoded.Count >= MaxDecodedSamples || ogg.Loop) return stream;
        double length = stream.GetLength();
        if (!(length > 0) || length > MaxDecodedSeconds) { _decoded[stream] = null; return stream; }
        _decoded[stream] = null;   // being decoded: this start, and any before it is ready, plays the compressed one
        float mixRate = AudioServer.GetMixRate();
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (DecodeToPcm(ogg, mixRate, length) is { } wav)
                {
                    _decoded[stream] = wav;
                    Interlocked.Add(ref _decodedBytes, wav.Data.Length);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        });
        return stream;
    }

    // The whole sample through a playback of its own, at the mixer's rate: what the mixer would have been given.
    private static AudioStreamWav? DecodeToPcm(AudioStreamOggVorbis source, float mixRate, double length)
    {
        AudioStreamPlayback? playback = source.InstantiatePlayback();
        if (playback is null) return null;
        int expected = (int)Math.Ceiling(length * mixRate) + 64, limit = (int)(MaxDecodedSeconds * mixRate) + 4096;
        List<Vector2[]> blocks = new();
        int frames = 0;
        playback.Start();
        while (frames < expected && frames < limit)
        {
            Vector2[] block = playback.MixAudio(1f, Math.Min(4096, expected - frames));
            if (block.Length == 0) break;
            blocks.Add(block);
            frames += block.Length;
            if (!playback.IsPlaying()) break;
        }
        playback.Stop();
        if (frames == 0) return null;
        bool stereo = false;
        foreach (Vector2[] block in blocks)
        {
            foreach (Vector2 frame in block)
                if (MathF.Abs(frame.X - frame.Y) > 1f / 32768f) { stereo = true; break; }
            if (stereo) break;
        }
        // Trailing silence past the sample's end (the estimate of its length is rounded up) is dropped.
        int channels = stereo ? 2 : 1;
        byte[] data = new byte[frames * channels * 2];
        int at = 0, lastAudible = 0;
        foreach (Vector2[] block in blocks)
            foreach (Vector2 frame in block)
            {
                short left = (short)Math.Clamp(MathF.Round(frame.X * 32767f), -32768f, 32767f);
                data[at++] = (byte)left; data[at++] = (byte)(left >> 8);
                if (stereo)
                {
                    short right = (short)Math.Clamp(MathF.Round(frame.Y * 32767f), -32768f, 32767f);
                    data[at++] = (byte)right; data[at++] = (byte)(right >> 8);
                    if (right != 0) lastAudible = at;
                }
                if (left != 0) lastAudible = at;
            }
        int keep = Math.Min(data.Length, (lastAudible + channels * 2 - 1) / (channels * 2) * (channels * 2) + channels * 2 * 64);
        if (keep < data.Length) Array.Resize(ref data, Math.Max(channels * 2, keep));
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, Stereo = stereo, MixRate = (int)MathF.Round(mixRate), Data = data };
    }

    // CL_VM_GetViewEntity: the view entity as the server numbers it, or the program's entity for it.
    private bool IsViewEntity(int owner)
    {
        if (owner <= 0 || _state is not { } state) return false;
        if (owner == state.ViewEntity) return true;
        return owner >= ProgramEntityBase && _host is { } host && host.EdictForServerEntity(state.ViewEntity) is > 0 and var edict && owner == ProgramEntityBase + edict;
    }

    private float SoundCvar(string name, float fallback) => _cvars.Has(name) && float.IsFinite(_cvars.GetFloat(name)) && _cvars.GetString(name).Length > 0 ? _cvars.GetFloat(name) : fallback;

    // SND_Spatialize_WithSfx. The channel's volume is its own times the per-channel cvar (snd_staticvolume for a
    // static sound, snd_channelNvolume otherwise), "volume" and "mastervolume". "anything coming from the view
    // entity will always be full volume", and so is a sound with no attenuation; anything else falls off as
    //   pow(1 - min(1, dist * distfade), snd_attenuation_exponent) * pow(0.1, 0.1 * snd_attenuation_decibel * dist * distfade)
    // (Xonotic's default is exponent 4 over a radius of 2400, not Quake's straight line over 1200).
    // Not reproduced: ReplayGain, snd_maxchannelvolume, occlusion, and the per-speaker pan (Godot pans).
    private void ApplyFalloff(Voice voice)
    {
        Vector3 ear = _listenerTransform.Origin;
        float gain = voice.Volume;
        if (voice.Static) gain *= SoundCvar("snd_staticvolume", 1);
        else if (voice.Channel is >= 0 and <= 9) gain *= SoundCvar(voice.Channel switch
        {
            0 => "snd_channel0volume", 1 => "snd_channel1volume", 2 => "snd_channel2volume", 3 => "snd_channel3volume", 4 => "snd_channel4volume",
            5 => "snd_channel5volume", 6 => "snd_channel6volume", 7 => "snd_channel7volume", 8 => "snd_channel8volume", _ => "snd_channel9volume",
        }, 1);
        gain *= SoundCvar("volume", 1) * SoundCvar("mastervolume", 1);
        gain = MathF.Max(0, gain);
        if (voice.DistanceFade > 0 && !IsViewEntity(voice.Owner))
        {
            float f = voice.Origin.DistanceTo(ear) * voice.DistanceFade;
            float exponent = SoundCvar("snd_attenuation_exponent", 1), decibel = SoundCvar("snd_attenuation_decibel", 0);
            float falloff = (exponent == 0 ? 1f : MathF.Pow(1f - MathF.Min(1f, f), exponent)) * (decibel == 0 ? 1f : MathF.Pow(0.1f, 0.1f * decibel * f));
            gain *= float.IsFinite(falloff) ? Math.Clamp(falloff, 0f, 1f) : 0f;
            voice.Player.Position = voice.Origin;
        }
        else voice.Player.Position = ear;
        voice.Player.VolumeDb = gain > 0.0001f ? Mathf.LinearToDb(MathF.Min(gain, 4f)) : -80f;
    }

    /// <summary>#177 localsound: not positioned, not tied to an entity.</summary>
    bool ILegacySound.Local(string sample, int channel, float volume)
    {
        if (_dpSound) return DpLocal(sample, channel, volume);
        if (_soundsThisFrame >= MaxSoundsPerFrame || LoadSample(sample) is not { } stream) return false;
        _soundsThisFrame++;
        AudioStreamPlayer? free = null;
        foreach (AudioStreamPlayer candidate in _localVoices)
            if (!candidate.Playing) { free = candidate; break; }
        if (free is null)
        {
            if (_localVoices.Count >= MaxLocalVoices) free = _localVoices[0];
            else
            {
                free = new AudioStreamPlayer { Name = "LegacyLocalVoice" + _localVoices.Count };
                _sceneRoot.AddChild(free);
                _localVoices.Add(free);
            }
        }
        free.Stream = stream;
        free.VolumeDb = Mathf.LinearToDb(Math.Clamp(volume, 0.0001f, 1f));
        free.Play();
        SoundsStarted++;
        return true;
    }

    /// <summary>#351 SetListener: for this frame the ears are here, not at the view.</summary>
    void ILegacySound.SetListener(QcVector origin, QcVector forward, QcVector right, QcVector up)
    {
        if (!Finite(origin) || !Finite(forward) || !Finite(right) || !Finite(up)) return;
        Basis basis = new(G(right), G(up), -G(forward));
        // Axes that are not a rotation (zero vectors, a mirrored set) would make an invalid listener.
        if (MathF.Abs(basis.Determinant()) < 0.001f) return;
        _listenerTransform = new Transform3D(basis.Orthonormalized(), G(origin));
        _listenerOverridden = true;
    }

    /// <summary>#533 getsoundtime: seconds into what is playing on that entity channel, or -1.</summary>
    float ILegacySound.ChannelPosition(int edict, int channel)
    {
        if (_dpSound) return _dpAudio!.Sound.GetEntChannelPosition(DpOwner(edict), channel);
        int owner = edict > 0 ? ProgramEntityBase + edict : 0;
        foreach (Voice voice in _voices)
            if (voice.InUse && !voice.Static && voice.Owner == owner && voice.Channel == channel && voice.Player.Playing)
                return voice.Player.GetPlaybackPosition();
        return -1;
    }

    // S_UpdateSounds: once a frame. Entity sounds follow their entity; finished voices are returned;
    // forced loops are restarted; the falloff is recomputed against the listener.
    private void UpdateSounds()
    {
        if (_dpSound)
        {
            DpUpdateSounds();
            return;
        }
        _soundsThisFrame = 0;
        if (!_listenerOverridden) _listenerTransform = _camera.Transform;
        _listener.Transform = _listenerTransform;
        if (!_listener.IsCurrent()) _listener.MakeCurrent();

        foreach (Voice voice in _voices)
        {
            if (!voice.InUse) continue;
            if (!voice.Player.Playing)
            {
                if (voice.Loop) voice.Player.Play();
                else
                {
                    voice.InUse = false;
                    continue;
                }
            }
            if (!voice.Static && voice.Owner != 0 && EntitySoundOrigin(voice.Owner) is { } origin) voice.Origin = origin;
            ApplyFalloff(voice);
        }
    }

    // CL_GetEntitySoundOrigin: where an entity's sounds come from now.
    private Vector3? EntitySoundOrigin(int owner)
    {
        if (owner >= ProgramEntityBase)
        {
            int edict = owner - ProgramEntityBase;
            if (_host is not { } host || edict >= host.Vm.NumEdicts || host.Vm.IsFree(edict)) return null;
            QcVector origin = host.Vm.FieldVector(edict, host.Fields.Origin);
            return Finite(origin) ? G(origin) : null;
        }
        if (_state?.NetworkEntities is { } table && owner > 0 && owner < table.Count)
        {
            ref readonly EntityState entity = ref table.Current(owner);
            if (entity.IsActive && Finite(entity.Origin)) return G(entity.Origin);
        }
        // "else if (cl.csqc_server2csqcentitynumber[ch->entnum])": a server entity the program draws itself
        // (every player in Xonotic) is followed through the program's entity for it.
        if (owner > 0 && owner < ProgramEntityBase && _host is { } program && program.EdictForServerEntity(owner) is > 0 and var linked
            && linked < program.Vm.NumEdicts && !program.Vm.IsFree(linked))
        {
            QcVector origin = program.Vm.FieldVector(linked, program.Fields.Origin);
            if (Finite(origin)) return G(origin);
        }
        return null;
    }

    private void StopAllSounds()
    {
        if (_dpSound)
        {
            _dpAudio?.Sound.StopAllSounds();   // S_StopAllSounds: the music too ("it may be using a faketrack")
            _dpCd?.Stop();
            return;
        }
        foreach (Voice voice in _voices)
        {
            voice.Player.Stop();
            voice.InUse = false;
            voice.Static = false;
            voice.Loop = false;
        }
        foreach (AudioStreamPlayer local in _localVoices) local.Stop();
        _staticVoices = 0;
    }

    // ---- the server's own sound messages -----------------------------------------------------------------

    /// <summary>svc_sound (CL_ParseStartSoundPacket): a sample from the server's precache list, on one of its entities.</summary>
    void IDpClientHandler.OnSound(in DpSound sound)
    {
        if (_state is not { } state || (uint)sound.SoundIndex >= (uint)state.SoundNames.Length || state.SoundNames[sound.SoundIndex] is not { } sample) return;
        if (sound.Entity < 0 || sound.Entity >= DpProtocol.MaxEdicts) return;
        if (_dpSound)
        {
            // "S_StartSound_StartPosition_Flags (ent, channel, cl.sound_precache[sound_num], pos, nvolume/255.0f, attenuation, 0, fflags, speed)"
            DpStart(sound.Entity, sound.Channel, sample, new QcVector(sound.Origin.X, sound.Origin.Y, sound.Origin.Z), sound.Volume / 255f, sound.Attenuation, 0, 0, sound.Speed);
            return;
        }
        StartVoice(sound.Entity, sound.Channel, sample, new QcVector(sound.Origin.X, sound.Origin.Y, sound.Origin.Z),
            sound.Volume / 255f, sound.Attenuation, 0, loop: false, sound.Speed, isStatic: false);
    }

    /// <summary>svc_cdtrack: "CDAudio_Play ((unsigned char)cl.cdtrack, true)".</summary>
    void IDpClientHandler.OnCdTrack(int track, int loopTrack)
    {
        if (_dpSound) _dpCd?.Play(track & 255, true);
    }

    /// <summary>svc_stopsound (S_StopSound).</summary>
    void IDpClientHandler.OnStopSound(int entity, int channel)
    {
        if (_dpSound)
        {
            _dpAudio!.Sound.StopSound(entity, channel);
            return;
        }
        foreach (Voice voice in _voices)
            if (voice.InUse && !voice.Static && voice.Owner == entity && voice.Channel == channel)
            {
                voice.Player.Stop();
                voice.InUse = false;
                voice.Loop = false;
            }
    }

    /// <summary>svc_spawnstaticsound (CL_ParseStaticSound): "S_StaticSound(cl.sound_precache[sound_num], org, vol/255, atten/64)".</summary>
    void IDpClientHandler.OnSpawnStaticSound(in DpStaticSound sound)
    {
        if (_state is not { } state || (uint)sound.SoundIndex >= (uint)state.SoundNames.Length || state.SoundNames[sound.SoundIndex] is not { } sample) return;
        if (_dpSound)
        {
            // "S_StaticSound (cl.sound_precache[sound_num], org, vol/255.0f, atten)": the attenuation byte as sent.
            DpStartStatic(new QcVector(sound.Origin.X, sound.Origin.Y, sound.Origin.Z), sample, sound.Volume / 255f, sound.Attenuation);
            return;
        }
        if (_staticVoices >= MaxStaticVoices) return;
        if (StartVoice(0, 0, sample, new QcVector(sound.Origin.X, sound.Origin.Y, sound.Origin.Z), sound.Volume / 255f, sound.Attenuation / 64f, 0, loop: true, 1, isStatic: true))
            _staticVoices++;
    }
}
