// Port of Base/darkplaces/snd_main.h sfx_t as far as the mixer reads it: a decoded sample.
using System;
using System.Threading;

namespace VortexArena.Engine.Audio;

/// <summary>
/// One decoded sample, as DarkPlaces holds it after S_LoadSound: 16-bit PCM at the file's own rate
/// (DarkPlaces resamples while mixing, not while loading), its length in sample frames and its loop point.
/// <see cref="LoopStart"/> equals <see cref="TotalLength"/> when the file carries no loop marker
/// ("sfx-&gt;loopstart = sfx-&gt;total_length").
///
/// The PCM may arrive after the object exists (a worker decodes it): <see cref="AvailableFrames"/> says how
/// much of <see cref="Data"/> is valid. The mixer holds a channel still, without advancing it, until the
/// frames it needs are there; a sample whose decode failed plays as nothing and ends at once.
/// </summary>
public sealed class DpSfx
{
    /// <summary>The name S_FindName keys on (the path as the program gave it).</summary>
    public string Name { get; }
    /// <summary>1 or 2.</summary>
    public int Channels { get; private set; } = 1;
    /// <summary>The file's sample rate ("sfx-&gt;format.speed").</summary>
    public int Rate { get; private set; } = 48000;
    /// <summary>Interleaved 16-bit samples, <see cref="TotalLength"/> frames long once sized.</summary>
    public short[] Data { get; private set; } = Array.Empty<short>();
    /// <summary>Length in sample frames (after a loop tag has trimmed it).</summary>
    public int TotalLength { get; private set; }
    /// <summary>The frame a looping channel returns to; <see cref="TotalLength"/> when the file has no loop.</summary>
    public int LoopStart { get; private set; }
    /// <summary>ReplayGain: "sfx-&gt;volume_mult" and "sfx-&gt;volume_peak" (0 = the file has no ReplayGain tags).</summary>
    public float VolumeMult { get; set; } = 1f;
    public float VolumePeak { get; set; }

    private volatile int _available;
    private volatile bool _formatKnown, _failed;

    public DpSfx(string name) { Name = name; }

    /// <summary>A complete sample (tests, WAV files: decoded where they are read).</summary>
    public DpSfx(string name, short[] data, int channels, int rate, int loopStart = -1) : this(name)
    {
        SetFormat(channels, rate, data.Length / Math.Max(1, channels), loopStart, data);
        Publish(TotalLength);
    }

    /// <summary>True once channels, rate, length and loop point are known (a channel can be started on it).</summary>
    public bool FormatKnown => _formatKnown;
    /// <summary>The file was missing or could not be decoded: "sfx-&gt;fetcher == NULL".</summary>
    public bool Failed => _failed;
    /// <summary>Frames of <see cref="Data"/> that hold decoded audio.</summary>
    public int AvailableFrames => _available;
    public bool Complete => _formatKnown && _available >= TotalLength;
    /// <summary>Seconds, as S_SoundLength answers: total_length / format.speed.</summary>
    public float LengthSeconds => _formatKnown && Rate > 0 ? TotalLength / (float)Rate : -1f;
    /// <summary>"sfx-&gt;loopstart &lt; sfx-&gt;total_length".</summary>
    public bool HasLoop => LoopStart < TotalLength;

    /// <summary>
    /// Size the sample. <paramref name="loopStart"/> below zero means no loop marker; it is bounded to the
    /// length as snd_wav.c does ("sfx-&gt;loopstart = min(sfx-&gt;loopstart, sfx-&gt;total_length)").
    /// </summary>
    public void SetFormat(int channels, int rate, int totalFrames, int loopStart, short[]? data = null)
    {
        Channels = channels == 2 ? 2 : 1;
        Rate = Math.Max(1, rate);
        TotalLength = Math.Max(0, totalFrames);
        LoopStart = loopStart < 0 ? TotalLength : Math.Min(loopStart, TotalLength);
        Data = data ?? new short[(long)TotalLength * Channels];
        Thread.MemoryBarrier();
        _formatKnown = true;
    }

    /// <summary>The decoder has filled <see cref="Data"/> up to this frame.</summary>
    public void Publish(int frames) => _available = Math.Min(frames, TotalLength);

    public void Fail()
    {
        _failed = true;
        if (!_formatKnown) SetFormat(1, 48000, 0, -1);
    }
}
