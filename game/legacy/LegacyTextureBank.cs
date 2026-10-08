// DarkPlaces compresses a texture on the frame it is loaded (gl_texturecompression) and writes the result to
// dds/ when r_texture_dds_save is on. BC7, which this client uses for gl_texturecompression 2, costs seconds
// of every core per texture: done where the texture is loaded it held a legacy session's frames for seconds
// at a time on a first visit. So a legacy session uploads such a texture uncompressed and hands its name to
// this bank, which encodes it into the session's texture cache in the background, one at a time, while a
// loading screen is up; the next load of the texture reads the blocks from the cache.
using System;
using System.Collections.Concurrent;
using System.Threading;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Legacy;

internal sealed class LegacyTextureBank : IDisposable
{
    private readonly AssetSystem _assets;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Action<string> _note;
    private Thread? _thread;
    private int _encoded, _failed;
    private long _encodeTicks;

    /// <summary>
    /// Set while frames of the game are being shown: the bank then does nothing. The BC7 encoder runs on the
    /// engine's own worker pool and fills it for the second or so a texture takes, and the frame thread waits
    /// for that pool every frame: measured in play, one encode every two seconds was a frame of 0.6 to 2.1 s
    /// every two seconds. So textures are compressed for the cache only while a loading screen is up.
    /// </summary>
    public volatile bool Playing;
    /// <summary>False holds the bank still (nothing is encoded): the other arm of a comparison.</summary>
    public volatile bool Enabled = true;

    public LegacyTextureBank(AssetSystem assets, Action<string> note)
    {
        _assets = assets;
        _note = note;
    }

    public int Pending => _queue.Count;
    public int Encoded => Volatile.Read(ref _encoded);

    /// <summary>A texture was uploaded uncompressed because compressing it where it was loaded would have
    /// been slow. Called from any thread.</summary>
    public void Defer(string vpath)
    {
        if (_cancel.IsCancellationRequested || !_seen.TryAdd(vpath, 0)) return;
        _queue.Enqueue(vpath);
        if (_thread is null)
            lock (_queue)
                if (_thread is null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "legacy-texture-bank", Priority = ThreadPriority.Lowest };
                    _thread.Start();
                }
    }

    private void Run()
    {
        CancellationToken cancel = _cancel.Token;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                if (!Enabled || Playing || !_queue.TryDequeue(out string? vpath))
                {
                    if (cancel.WaitHandle.WaitOne(250)) return;
                    continue;
                }
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                bool ok;
                try { ok = _assets.CompressToCache(vpath); }
                catch (Exception e) when (e is not OutOfMemoryException) { ok = false; }
                TimeSpan took = System.Diagnostics.Stopwatch.GetElapsedTime(began);
                if (ok) Interlocked.Increment(ref _encoded);
                else Interlocked.Increment(ref _failed);
                Interlocked.Add(ref _encodeTicks, took.Ticks);
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_cancel.IsCancellationRequested) return;
        _cancel.Cancel();
        // Not waited for: an encode in hand ends by itself (a read from the closed file system fails and is caught).
        _thread?.Join(250);
        if (_encoded + _failed + _queue.Count > 0)
            _note(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"texture cache: {_encoded} textures compressed in the background in {TimeSpan.FromTicks(Volatile.Read(ref _encodeTicks)).TotalSeconds:0.0} s for the next load, {_failed} failed, {_queue.Count} left for another session"));
        _cancel.Dispose();
    }
}
