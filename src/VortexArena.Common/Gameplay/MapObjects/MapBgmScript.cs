using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VortexArena.Common.Gameplay;

/// <summary>Map-authored parameters for a named lane in maps/&lt;map&gt;.bgs.</summary>
public sealed record MapBgmSettings(string Track, bool Angular, float Attack, float Decay, float Sustain, float Release);

/// <summary>The note events shared by a map's scripted models and particle emitters.</summary>
public sealed class MapBgmScript
{
    internal readonly record struct Note(float Time, float Velocity);
    private readonly Dictionary<string, List<Note>> _tracks = new(StringComparer.Ordinal);

    public static MapBgmScript Parse(string text)
    {
        var script = new MapBgmScript();
        using var reader = new StringReader(text);
        string? current = null;
        List<Note>? notes = null;
        while (reader.ReadLine() is { } line)
        {
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3 || !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float time)
                || !float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float velocity)
                || !float.IsFinite(time) || !float.IsFinite(velocity))
            {
                current = null;
                continue;
            }

            // QC finds the first row for a lane and stops at the first row with a different lane.
            if (current != tokens[0])
            {
                current = tokens[0];
                notes = new List<Note>();
                if (!script._tracks.TryAdd(current, notes))
                    notes = null;
            }
            notes?.Add(new Note(time, velocity));
        }
        return script;
    }

    public Playback? Bind(MapBgmSettings? settings) =>
        settings is not null && _tracks.TryGetValue(settings.Track, out List<Note>? notes)
            ? new Playback(settings, notes)
            : null;

    public readonly record struct Sample(float Amplitude, bool NoteOn)
    {
        public static Sample Unscripted => new(1f, false);

        public float ModelAlpha(float lip) => Amplitude < 0f ? 1f
            : lip < 0f ? 1f + lip * Amplitude : 1f - lip * (1f - Amplitude);
    }

    /// <summary>Per-entity note cursor and ADSR state (QC client/bgmscript.qc).</summary>
    public sealed class Playback
    {
        private readonly MapBgmSettings _settings;
        private readonly IReadOnlyList<Note> _notes;
        private int _cursor;
        private float _lastTime, _stateStart, _velocity;
        private bool _pressed;

        internal Playback(MapBgmSettings settings, IReadOnlyList<Note> notes)
        {
            _settings = settings;
            _notes = notes;
        }

        public Sample Advance(float musicTime, float frameTime)
        {
            if (!(musicTime >= 0f))
                return new Sample(-1f, false);

            if (musicTime < _lastTime)
            {
                // Loop/seek back. Release the old note smoothly and replay the lane from its first event.
                float amplitude = Amplitude(_lastTime - _stateStart + frameTime);
                _cursor = 0;
                SetState(musicTime, 0f, amplitude, rewind: true);
            }

            bool noteOn = false;
            while (_cursor < _notes.Count && _notes[_cursor].Time < musicTime)
            {
                Note note = _notes[_cursor++];
                SetState(note.Time, note.Velocity, Amplitude(note.Time - _stateStart));
                noteOn = _pressed;
            }

            _lastTime = musicTime;
            return new Sample(Amplitude(musicTime - _stateStart), noteOn);
        }

        private void SetState(float time, float velocity, float amplitude, bool rewind = false)
        {
            _pressed = velocity > 0f;
            if (_pressed)
            {
                _velocity = velocity;
                _stateStart = time - amplitude / velocity * _settings.Attack;
            }
            else
            {
                if (rewind) _velocity = 1f;
                _stateStart = time - ReleaseTime(amplitude);
            }
        }

        private float Amplitude(float elapsed)
        {
            if (_pressed)
            {
                if (elapsed < 0f) return 0f;
                if (_settings.Attack > 0f && elapsed <= _settings.Attack)
                    return _velocity * elapsed / _settings.Attack;
                if (_settings.Decay > 0f && elapsed <= _settings.Attack + _settings.Decay)
                    return _velocity * (1f + (elapsed - _settings.Attack) / _settings.Decay * (_settings.Sustain - 1f));
                return _velocity * _settings.Sustain;
            }

            // CONSTANT_SPEED_DECAY uses the scaled sustain for both the release and its pre-time decay.
            float sustain = _settings.Sustain * _velocity;
            if (_settings.Release == 0f || elapsed > _settings.Release) return 0f;
            float release = sustain * (1f - elapsed / _settings.Release);
            if (elapsed < -_settings.Decay) return 1f;
            if (elapsed < 0f)
            {
                float decay = 1f + (elapsed + _settings.Decay) / _settings.Decay * (sustain - 1f);
                return MathF.Max(decay, release);
            }
            return release;
        }

        private float ReleaseTime(float amplitude)
        {
            float sustain = _settings.Sustain * _velocity;
            if (sustain == 0f) return 0f;
            float release = (1f - amplitude / sustain) * _settings.Release;
            if (amplitude <= sustain) return release;
            if (sustain == 1f) return 0f;
            float decay = (amplitude - 1f) / (sustain - 1f) * _settings.Decay - _settings.Decay;
            return MathF.Max(decay, release);
        }
    }
}
