// Port of Base/darkplaces/snd_main.c S_PrecacheSound and S_LocalSoundEx as the menu program reaches them
// (precache_sound, localsound, soundlength), and cd_shared.c CD_f - the "cd" console command, whose
// "tracks" have been files under sound/cdtracks/ since DarkPlaces stopped playing discs.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Loaders;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Game.Legacy;

/// <summary>
/// What the Xonotic menu sounds like: its clicks and hovers (localsound) and its music (the configuration
/// runs <c>cd loop $menu_cdtrack</c> when the menu first shows).
///
/// Sample and track names come from the menu program and the configuration; each is a plain relative
/// path read from the game data through the asset loader, never a Godot resource path.
/// </summary>
public sealed class LegacyMenuSound : ILegacySound
{
    private const int MaxVoices = 8;

    private readonly Node _parent;
    private readonly VirtualFileSystem _vfs;
    private readonly AssetLoader _assets;
    private readonly CvarService _cvars;
    private readonly Action<string> _log;
    private readonly List<AudioStreamPlayer> _voices = new();
    private readonly Dictionary<string, bool> _precached = new(StringComparer.Ordinal);
    private AudioStreamPlayer? _music;
    private string _track = "";
    private readonly List<string> _remap = new();
    private bool _musicPaused;

    public LegacyMenuSound(Node parent, VirtualFileSystem files, AssetLoader assets, CvarService cvars, Action<string> log)
    {
        _parent = parent;
        _vfs = files;
        _assets = assets;
        _cvars = cvars;
        _log = log;
    }

    /// <summary>Sounds started with localsound, and what the last one was: what a run without a listener can report.</summary>
    public long SoundsStarted { get; private set; }
    public string LastSound { get; private set; } = "";
    /// <summary>The music track playing (or paused), "" for none.</summary>
    public string Track => _track;

    private AudioStream? LoadSample(string sample)
    {
        if (string.IsNullOrEmpty(sample) || sample.Length > 200 || !LegacyQcHost.IsSafePath(sample)) return null;
        try { return _assets.LoadSound(sample); }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or FormatException) { return null; }
    }

    private float Cvar(string name, float fallback) =>
        _cvars.Has(name) && float.IsFinite(_cvars.GetFloat(name)) ? Math.Clamp(_cvars.GetFloat(name), 0, 1) : fallback;

    public bool Precache(string sample)
    {
        if (string.IsNullOrEmpty(sample) || sample.Length > 200 || !LegacyQcHost.IsSafePath(sample)) return false;
        if (_precached.TryGetValue(sample, out bool known)) return known;
        string stem = sample.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || sample.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? sample[..^4] : sample;
        string rooted = stem.StartsWith("sound/", StringComparison.Ordinal) ? stem : "sound/" + stem;
        bool exists = _vfs.Exists(rooted + ".ogg") || _vfs.Exists(rooted + ".wav") || _vfs.Exists(stem + ".ogg") || _vfs.Exists(stem + ".wav");
        if (_precached.Count >= 1024) _precached.Clear();
        _precached[sample] = exists;
        return exists;
    }

    /// <summary>S_LocalSoundEx: played at full volume wherever the listener is ("menu sounds must not be freed on level change").</summary>
    public bool Local(string sample, int channel, float volume)
    {
        if (LoadSample(sample) is not { } stream) return false;
        AudioStreamPlayer? free = null;
        foreach (AudioStreamPlayer candidate in _voices)
            if (!candidate.Playing) { free = candidate; break; }
        if (free is null)
        {
            if (_voices.Count >= MaxVoices) free = _voices[0];
            else
            {
                free = new AudioStreamPlayer { Name = "LegacyMenuVoice" + _voices.Count, ProcessMode = Node.ProcessModeEnum.Always };
                _parent.AddChild(free);
                _voices.Add(free);
            }
        }
        float gain = Math.Clamp(volume, 0, 1) * Cvar("volume", 1) * Cvar("mastervolume", 1);
        free.Stream = stream;
        free.VolumeDb = Mathf.LinearToDb(Math.Max(gain, 0.0001f));
        free.Play();
        SoundsStarted++;
        LastSound = sample;
        return true;
    }

    public float Length(string sample)
    {
        if (LoadSample(sample) is not { } stream) return -1;
        double length = stream.GetLength();
        return length > 0 ? (float)length : -1;
    }

    // The menu has no world: nothing is positioned and nothing plays on an entity's channel.
    public void Start(int edict, int channel, string sample, QcVector origin, float volume, float attenuation, float startPosition, int flags, float speed) { }
    public void StartStatic(QcVector origin, string sample, float volume, float attenuation) { }
    public void SetListener(QcVector origin, QcVector forward, QcVector right, QcVector up) { }
    public float ChannelPosition(int edict, int channel) => -1;

    /// <summary>
    /// The "cd" console command: <c>cd loop &lt;track&gt;</c>, <c>cd play &lt;track&gt;</c>, <c>cd stop</c>,
    /// <c>cd pause</c>, <c>cd resume</c>. A track is a file name under sound/cdtracks/ (or a number, which
    /// is "trackNNN" there).
    /// </summary>
    public void CdCommand(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2) return;
        switch (argv[1].ToLowerInvariant())
        {
            case "remap":
                // "cd remap <track1> <track2> ...": what the track NUMBERS a map names mean. Xonotic's
                // cdtracks.cfg lists its music files this way.
                _remap.Clear();
                for (int i = 2; i < argv.Count && _remap.Count < 256; i++) _remap.Add(argv[i]);
                break;
            case "loop":
            case "play":
                if (argv.Count >= 3) PlayTrack(argv[2], loop: argv[1].Equals("loop", StringComparison.OrdinalIgnoreCase));
                break;
            case "stop":
            case "off":
            case "reset":
                StopMusic();
                break;
            case "pause":
                if (_music is { Playing: true })
                {
                    _music.StreamPaused = true;
                    _musicPaused = true;
                }
                break;
            case "resume":
                if (_music is not null && _musicPaused)
                {
                    _music.StreamPaused = false;
                    _musicPaused = false;
                }
                break;
        }
    }

    private void PlayTrack(string track, bool loop)
    {
        if (track.Length is 0 or > 64 || !LegacyQcHost.IsSafePath(track)) return;
        // "cd loop 5" is the fifth name of the remap list if there is one, else track005; anything else is a file name.
        string name = track;
        if (int.TryParse(track, out int number))
            name = number >= 1 && number <= _remap.Count && LegacyQcHost.IsSafePath(_remap[number - 1]) ? _remap[number - 1] : $"track{number:000}";
        if (name == _track && _music is { Playing: true }) return;   // "already playing" - the menu asks again on every reload
        AudioStream? stream = LoadSample("cdtracks/" + name);
        if (stream is null)
        {
            _log($"cd: track \"{name}\" is not in the game data (sound/cdtracks/{name}.ogg)");
            StopMusic();
            return;
        }
        if (loop && stream is AudioStreamOggVorbis ogg) ogg.Loop = true;
        if (_music is null)
        {
            _music = new AudioStreamPlayer { Name = "LegacyMenuMusic", ProcessMode = Node.ProcessModeEnum.Always };
            _parent.AddChild(_music);
        }
        _music.Stream = stream;
        _music.StreamPaused = false;
        _musicPaused = false;
        _track = name;
        UpdateMusicVolume();
        _music.Play();
        _log($"cd: playing \"{name}\"{(loop ? " (looping)" : "")}");
    }

    public void StopMusic()
    {
        _music?.Stop();
        _track = "";
        _musicPaused = false;
    }

    /// <summary>bgmvolume and mastervolume, applied each frame: the Audio settings' sliders move them.</summary>
    public void UpdateMusicVolume()
    {
        if (_music is null) return;
        float gain = Cvar("bgmvolume", 1) * Cvar("mastervolume", 1);
        _music.VolumeDb = Mathf.LinearToDb(Math.Max(gain, 0.0001f));
    }

    public void Shutdown()
    {
        StopMusic();
        foreach (AudioStreamPlayer voice in _voices) voice.Stop();
    }
}
