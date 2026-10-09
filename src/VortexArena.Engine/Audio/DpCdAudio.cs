// Port of Base/darkplaces/cd_shared.c: CD_f (the "cd" console command), CDAudio_Play_byName, CDAudio_Play,
// CDAudio_Stop, CDAudio_Pause, CDAudio_Resume, CDAudio_SetVolume and CDAudio_Update. A "track" is a sound
// file: music is an ordinary channel of the mixer, started on entity -1 with CHANNELFLAG_FULLVOLUME (so
// "volume" does not apply, only bgmvolume and mastervolume), CHANNELFLAG_LOCALSOUND (so pausing the game does
// not pause it) and, for "cd loop", CHANNELFLAG_FORCELOOP.
// Not ported: music_playlist_* (Xonotic does not use them) and the tray commands.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace VortexArena.Engine.Audio;

public sealed class DpCdAudio
{
    private readonly DpSoundSystem _sound;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, DpSfx?> _load;
    private readonly List<string> _remap = new();
    private int _fakeTrack = -1;
    private DpSfx? _sfx;
    private bool _enabled = true, _playing, _wasPlaying;
    private float _volume = 1.0f;

    /// <param name="exists">FS_FileExists for a path in the game data.</param>
    /// <param name="load">S_PrecacheSound for a path that exists.</param>
    public DpCdAudio(DpSoundSystem sound, Func<string, bool> exists, Func<string, DpSfx?> load)
    {
        _sound = sound;
        _exists = exists;
        _load = load;
    }

    /// <summary>The file playing (or paused), "" for none.</summary>
    public string Track { get; private set; } = "";
    public bool Playing => _playing;
    public bool Looping { get; private set; }
    public Action<string>? Log { get; set; }

    private bool ChannelAlive => _fakeTrack != -1 && _sfx is not null && _sound.IsChannelPlaying(_fakeTrack, _sfx, -1, 0);

    /// <summary>CD_f. <paramref name="argv"/>[0] is "cd".</summary>
    public void Command(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2) return;
        switch (argv[1].ToLowerInvariant())
        {
            case "on": _enabled = true; break;
            case "off": Stop(); _enabled = false; break;
            case "reset":
                _enabled = true;
                Stop();
                _remap.Clear();
                break;
            case "remap":
                // "cd remap a b c": track 1 is a. With no arguments DarkPlaces lists them and changes nothing.
                if (argv.Count > 2)
                {
                    _remap.Clear();
                    for (int i = 2; i < argv.Count && _remap.Count < 255; i++) _remap.Add(argv[i]);
                }
                break;
            case "play":
            case "loop":
                if (argv.Count >= 3)
                    PlayByName(argv[2], argv[1].Equals("loop", StringComparison.OrdinalIgnoreCase),
                        argv.Count > 3 && float.TryParse(argv[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float at) ? at : 0);
                break;
            case "stop": Stop(); break;
            case "pause": Pause(); break;
            case "resume": Resume(); break;
        }
    }

    /// <summary>CDAudio_Play: svc_cdtrack's track number.</summary>
    public void Play(int track, bool looping) => PlayByName(track.ToString(CultureInfo.InvariantCulture), looping, 0);

    /// <summary>CDAudio_Play_byName.</summary>
    public void PlayByName(string trackName, bool looping, float startPosition)
    {
        if (!_enabled || string.IsNullOrEmpty(trackName)) return;
        Stop();
        string? file = null;
        foreach (string candidate in DpSoundFiles.TrackCandidates(trackName, _remap))
            if (_exists(candidate)) { file = candidate; break; }
        DpSfx? sfx = file is null ? null : _load(file);
        if (sfx is not null)
            _fakeTrack = _sound.StartSound(-1, 0, sfx, Vector3.Zero, _volume, 0, startPosition,
                (looping ? DpSoundSystem.ChannelFlagForceLoop : 0) | DpSoundSystem.ChannelFlagFullVolume | DpSoundSystem.ChannelFlagLocalSound, 1.0f);
        if (_fakeTrack == -1)
        {
            Log?.Invoke($"Could not load BGM track {trackName}.");
            return;
        }
        _sfx = sfx;
        Track = file!;
        Looping = looping;
        _playing = true;
        Log?.Invoke($"BGM track {trackName} playing...");
        if (_volume == 0.0f) Pause();
    }

    /// <summary>CDAudio_GetPosition: seconds into the track, or -1.</summary>
    public float Position => ChannelAlive ? _sound.GetChannelPosition(_fakeTrack) : -1;

    public void Stop()
    {
        if (!_enabled) return;
        if (ChannelAlive) _sound.StopChannel(_fakeTrack);
        _fakeTrack = -1;
        _sfx = null;
        Track = "";
        _wasPlaying = false;
        _playing = false;
    }

    public void Pause()
    {
        if (!_enabled || !_playing || !ChannelAlive) return;
        _sound.SetChannelFlag(_fakeTrack, DpSoundSystem.ChannelFlagPaused, true);
        _wasPlaying = _playing;
        _playing = false;
    }

    public void Resume()
    {
        if (!_enabled || _playing || !_wasPlaying || !ChannelAlive) return;
        _sound.SetChannelFlag(_fakeTrack, DpSoundSystem.ChannelFlagPaused, false);
        _playing = true;
    }

    /// <summary>CDAudio_Update, once a frame: "CDAudio_SetVolume (bgmvolume.value)". A volume of zero pauses the track.</summary>
    public void Update(float bgmVolume)
    {
        if (!_enabled) return;
        // S_StopAllSounds takes the track with it ("stop CD audio because it may be using a faketrack").
        if (_fakeTrack != -1 && !ChannelAlive)
        {
            _fakeTrack = -1;
            _sfx = null;
            Track = "";
            _playing = _wasPlaying = false;
        }
        if (bgmVolume == _volume) return;
        if (bgmVolume <= 0.0f) Pause();
        else
        {
            if (_volume <= 0.0f) Resume();
            if (ChannelAlive) _sound.SetChannelVolume(_fakeTrack, bgmVolume);
        }
        _volume = bgmVolume;
    }
}
