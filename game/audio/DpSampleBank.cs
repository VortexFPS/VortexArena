// Port of Base/darkplaces/snd_main.c S_FindName / S_PrecacheSound and snd_mem.c S_LoadSound: a sample name
// becomes a decoded sample, once. WAV files are decoded here (DpSoundFiles.LoadWav); Ogg Vorbis files are
// decoded by the engine's Vorbis decoder, run at the file's own rate so that what the mixer resamples is
// the same PCM DarkPlaces holds.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;
using VortexArena.Engine.Audio;

namespace VortexArena.Game.Audio;

/// <summary>
/// The samples of one set of game data (the native game's, or a legacy session's). <see cref="Get"/>
/// answers at once with a <see cref="DpSfx"/> whose format is known; the PCM of a compressed file is
/// filled in by a worker and the mixer holds a channel until its first frames are there (a sample that was
/// precached is decoded long before it is played, as in DarkPlaces, which decodes at precache).
/// </summary>
public sealed class DpSampleBank
{
    /// <summary>snd_streaming_length as Xonotic sets it: longer files are music. They are not decoded at precache, only when played.</summary>
    private const double PrecacheMaxSeconds = 40;
    // AudioStreamPlaybackResampled answers with its input delayed by this many frames (its interpolation history).
    private const int ResamplerDelay = 2;

    private sealed class Entry
    {
        public DpSfx Sfx = null!;
        public byte[]? Ogg;
        public int DecodeState;   // 0 not asked, 1 waiting for a background worker, 2 taken
    }

    private readonly Func<string, bool> _exists;
    private readonly Func<string, byte[]?> _read;
    private readonly Action<string>? _warn;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private static readonly BlockingCollection<Action> s_background = new();
    private static int s_workers;
    private long _decodedBytes;
    private int _decoded;

    public DpSampleBank(Func<string, bool> exists, Func<string, byte[]?> read, Action<string>? warn = null)
    {
        _exists = exists;
        _read = read;
        _warn = warn;
    }

    /// <summary>Compressed samples decoded so far, and the bytes of PCM they take.</summary>
    public int DecodedSamples => Volatile.Read(ref _decoded);
    public long DecodedBytes => Interlocked.Read(ref _decodedBytes);

    /// <summary>
    /// S_PrecacheSound and S_LoadSound. Null for an empty name; otherwise a sample, which has
    /// <see cref="DpSfx.Failed"/> set if no file stands behind it (DarkPlaces says so once, as this does).
    /// <paramref name="forPlay"/>: the PCM is wanted now, not when a background worker gets to it.
    /// </summary>
    public DpSfx? Get(string name, bool forPlay)
    {
        if (string.IsNullOrEmpty(name)) return null;
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(name, out entry))
            {
                entry = Load(name);
                _entries[name] = entry;
            }
        }
        if (entry.Ogg is not null && Volatile.Read(ref entry.DecodeState) != 2)
        {
            if (forPlay)
            {
                if (Interlocked.Exchange(ref entry.DecodeState, 2) != 2)
                {
                    // An offline capture is deterministic: the sample is whole before its channel starts.
                    if (DpAudio.OfflineFrames > 0) Decode(entry);
                    else ThreadPool.QueueUserWorkItem(_ => Decode(entry));
                }
                else if (DpAudio.OfflineFrames > 0) SpinWait.SpinUntil(() => entry.Sfx.Complete, 5000);
            }
            else if (entry.Sfx.LengthSeconds <= PrecacheMaxSeconds && Interlocked.CompareExchange(ref entry.DecodeState, 1, 0) == 0)
            {
                EnsureWorkers();
                s_background.Add(() =>
                {
                    if (Interlocked.Exchange(ref entry.DecodeState, 2) != 2) Decode(entry);
                });
            }
        }
        return entry.Sfx;
    }

    /// <summary>Does a file stand behind this name (precache_sound's answer)? Reads nothing.</summary>
    public bool Exists(string name)
    {
        lock (_lock)
            if (_entries.TryGetValue(name, out Entry? entry)) return !entry.Sfx.Failed;
        foreach (string candidate in DpSoundFiles.Candidates(name))
            if (_exists(candidate)) return true;
        return false;
    }

    private Entry Load(string name)
    {
        DpSfx? sfx = null;
        byte[]? ogg = null;
        foreach (string candidate in DpSoundFiles.Candidates(name))
        {
            if (!_exists(candidate)) continue;
            byte[]? bytes;
            try { bytes = _read(candidate); }
            catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or FormatException) { bytes = null; }
            if (bytes is null) continue;
            if (candidate.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                if (DpSoundFiles.ReadOggInfo(bytes) is not { } info) continue;
                sfx = new DpSfx(name) { VolumeMult = info.VolumeMult, VolumePeak = info.VolumePeak };
                sfx.SetFormat(info.Channels, info.Rate, info.Length, info.LoopStart);
                ogg = bytes;
            }
            else sfx = DpSoundFiles.LoadWav(name, bytes);
            if (sfx is not null) break;
        }
        if (sfx is null)
        {
            sfx = new DpSfx(name);
            sfx.Fail();
            _warn?.Invoke($"Could not find sound \"{name}\"");
        }
        return new Entry { Sfx = sfx, Ogg = ogg };
    }

    private static void EnsureWorkers()
    {
        if (Interlocked.CompareExchange(ref s_workers, 1, 0) != 0) return;
        for (int i = 0; i < 2; i++)
        {
            System.Threading.Thread worker = new(() =>
            {
                foreach (Action job in s_background.GetConsumingEnumerable())
                {
                    try { job(); }
                    catch (Exception e) when (e is not OutOfMemoryException) { }
                }
            }) { IsBackground = true, Name = "DpSampleDecode" + i, Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }
    }

    // The whole file through the engine's Vorbis decoder. Its playback resamples to the audio server's rate;
    // asked to play at (server rate / file rate) its step is exactly one input frame, and the cubic
    // interpolation at a zero fraction returns the input sample itself: the file's own PCM, two frames late.
    // Samples become 16 bit the way libvorbisfile's ov_read makes them (round to nearest, clamp), which is
    // what DarkPlaces holds.
    private void Decode(Entry entry)
    {
        DpSfx sfx = entry.Sfx;
        byte[]? bytes = entry.Ogg;
        entry.Ogg = null;
        try
        {
            if (bytes is null) return;
            AudioStreamOggVorbis? stream = AudioStreamOggVorbis.LoadFromBuffer(bytes);
            AudioStreamPlayback? playback = stream?.InstantiatePlayback();
            if (playback is null)
            {
                sfx.Publish(sfx.TotalLength);   // silence rather than a channel that waits for ever
                return;
            }
            float mixRate = AudioServer.GetMixRate();
            float rateScale = mixRate / sfx.Rate;
            if (sfx.Rate * rateScale < mixRate) rateScale = MathF.BitIncrement(rateScale);
            short[] data = sfx.Data;
            int channels = sfx.Channels, total = sfx.TotalLength, written = 0, skip = ResamplerDelay;
            playback.Start();
            while (written < total)
            {
                Vector2[] block = playback.MixAudio(rateScale, Math.Min(8192, total - written + skip));
                if (block.Length == 0) break;
                int from = Math.Min(skip, block.Length);
                skip -= from;
                for (int i = from; i < block.Length && written < total; i++, written++)
                {
                    if (channels == 2)
                    {
                        data[written * 2] = ToShort(block[i].X);
                        data[written * 2 + 1] = ToShort(block[i].Y);
                    }
                    else data[written] = ToShort(block[i].X);
                }
                sfx.Publish(written);
                if (!playback.IsPlaying() && written < total) break;
            }
            playback.Stop();
            sfx.Publish(total);   // whatever the decoder did not give stays silent
            Interlocked.Add(ref _decodedBytes, (long)total * channels * 2);
            Interlocked.Increment(ref _decoded);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            sfx.Publish(sfx.TotalLength);
        }
    }

    private static short ToShort(float sample) => (short)Math.Clamp((int)MathF.Floor(sample * 32768f + 0.5f), -32768, 32767);
}
