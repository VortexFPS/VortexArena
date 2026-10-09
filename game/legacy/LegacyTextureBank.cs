// DarkPlaces compresses a texture on the frame it is loaded (gl_texturecompression) and writes the result to
// dds/ when r_texture_dds_save is on. BC7, which this client uses for gl_texturecompression 2, costs seconds
// of every core per texture in the engine's encoder: done where the texture is loaded it held a legacy
// session's frames for seconds at a time on a first visit. So a legacy session uploads such a texture
// uncompressed and hands its name to this bank, which compresses it afterwards on a thread of its own, banks
// the blocks in the session's texture cache for the next load, and hands the compressed picture to the frame
// thread, which puts it in the place of the uncompressed one a little at a time.
using System;
using System.Collections.Concurrent;
using System.Threading;
using Godot;
using VortexArena.Formats.Images;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Legacy;

internal sealed class LegacyTextureBank : IDisposable
{
    private readonly AssetSystem _assets;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentQueue<Ready> _ready = new();

    // A compressed texture waiting for the frame thread: already in the renderer (Texture2DCreate on the
    // bank's thread), so taking the old one's place is one queued command.
    private readonly record struct Ready(string Vpath, Rid Texture, Image.Format Format, bool Mipmaps, int Width, int Height, long Bytes, long Was);
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Action<string> _note;
    private Thread? _thread;
    private int _encoded, _failed, _encodedInPlay, _swapped, _notSwapped;
    private long _encodeTicks, _inPlayTicks, _readyBytes, _savedBytes, _inPlayPixels;
    private byte[] _blocks = Array.Empty<byte>();

    /// <summary>
    /// Set while frames of the game are being shown. The engine's BC7 encoder runs on the engine's own worker
    /// pool and fills it for the second or so a texture takes, and the frame thread waits for that pool every
    /// frame: measured in play, one encode every two seconds was a frame of 0.6 to 2.1 s every two seconds. So
    /// that encoder is used only while a loading screen is up. In play the bank uses <see cref="Bc7Mode6Encoder"/>
    /// instead - managed code on this thread alone, at the lowest priority - unless <see cref="InPlay"/> is off,
    /// in which case it rests as it always did. (With gl_texturecompression 1 the engine's S3TC encoder runs on
    /// the calling thread and is used at all times.)
    /// </summary>
    public volatile bool Playing;
    /// <summary>False holds the bank still (nothing is encoded): the other arm of a comparison.</summary>
    public volatile bool Enabled = true;

    /// <summary>Compress during play (the default). VORTEX_LEGACY_BANK_INPLAY=0: only behind loading screens, as before.</summary>
    public static readonly bool InPlay = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_BANK_INPLAY") != "0";
    /// <summary>Put the compressed picture in the place of the uncompressed one (the default).
    /// VORTEX_LEGACY_BANK_SWAP=0: bank it for the next load only, as before.</summary>
    public static readonly bool Swap = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_BANK_SWAP") != "0";

    // Compressed pictures waiting for the frame thread: past this the thread waits (a hidden window draws no frames).
    private const long MaxReadyBytes = 32L << 20;

    public LegacyTextureBank(AssetSystem assets, Action<string> note)
    {
        _assets = assets;
        _note = note;
    }

    public int Pending => _queue.Count;
    public int Encoded => Volatile.Read(ref _encoded);
    /// <summary>Textures whose compressed form took the uncompressed one's place, and the memory that gave back.</summary>
    public int Swapped => _swapped;
    public long SavedBytes => _savedBytes;
    public int WaitingForSwap => _ready.Count;

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

    /// <summary>
    /// Frame thread, once a frame: puts compressed pictures in the place of the uncompressed ones, at most
    /// <paramref name="budgetBytes"/> of them (and always one, however large), so that a frame hands the
    /// renderer a bounded upload. Returns how many were swapped.
    /// </summary>
    public int Pump(long budgetBytes = 3L << 20)
    {
        if (_ready.IsEmpty) return 0;
        int swapped = 0;
        long spent = 0;
        while ((swapped == 0 || spent < budgetBytes) && _ready.TryPeek(out Ready next)
               && (swapped == 0 || spent + next.Bytes <= budgetBytes) && _ready.TryDequeue(out next))
        {
            Interlocked.Add(ref _readyBytes, -next.Bytes);
            long began = LegacyPerfLog.Stamp();
            bool done = false;
            try { done = _assets.ReplaceTexture(next.Vpath, next.Texture, next.Format, next.Mipmaps, next.Width, next.Height); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            if (!done) RenderingServer.FreeRid(next.Texture);
            if (done)
            {
                _swapped++;
                _savedBytes += next.Was - next.Bytes;
                swapped++;
                spent += next.Bytes;
                if (LegacyPerfLog.Enabled && System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds >= 0.3) LegacyPerfLog.Event("texture swap " + next.Vpath, began);
            }
            else _notSwapped++;
        }
        return swapped;
    }

    private void Run()
    {
        CancellationToken cancel = _cancel.Token;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                bool playing = Playing;
                if (!Enabled || (playing && !InPlay))
                {
                    if (cancel.WaitHandle.WaitOne(250)) return;
                    continue;
                }
                if (Interlocked.Read(ref _readyBytes) > MaxReadyBytes)
                {
                    if (cancel.WaitHandle.WaitOne(30)) return;
                    continue;
                }
                if (!_queue.TryDequeue(out string? vpath))
                {
                    if (cancel.WaitHandle.WaitOne(250)) return;
                    continue;
                }
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                bool managed = playing && AssetSystem.SlowEncoder;
                Image? image = null;
                long was = 0;
                try { image = managed ? EncodeManaged(vpath, out was) : EncodeEngine(vpath, out was); }
                catch (Exception e) when (e is not OutOfMemoryException) { image = null; }
                long took = System.Diagnostics.Stopwatch.GetTimestamp() - began;
                if (image is null)
                {
                    Interlocked.Increment(ref _failed);
                    continue;
                }
                Interlocked.Increment(ref _encoded);
                Interlocked.Add(ref _encodeTicks, took);
                if (managed)
                {
                    Interlocked.Increment(ref _encodedInPlay);
                    Interlocked.Add(ref _inPlayTicks, took);
                    LegacyPerfLog.Event("texture compressed in play " + vpath, began);
                }
                if (Swap)
                {
                    long bytes = image.GetDataSize();
                    // At the frame thread's own priority for this one call: the renderer's locks are held in
                    // it, and a thread of the lowest priority that is put aside while holding one keeps the
                    // frame thread waiting for as long as the machine has something better to do.
                    Thread.CurrentThread.Priority = ThreadPriority.Normal;
                    Rid made;
                    try { made = RenderingServer.Texture2DCreate(image); }
                    finally { Thread.CurrentThread.Priority = ThreadPriority.Lowest; }
                    Ready ready = new(vpath, made, image.GetFormat(), image.HasMipmaps(), image.GetWidth(), image.GetHeight(), bytes, was);
                    Interlocked.Add(ref _readyBytes, bytes);
                    _ready.Enqueue(ready);
                }
                image.Dispose();
                // In play the machine belongs to the game: a breath between textures.
                if (playing && cancel.WaitHandle.WaitOne(2)) return;
            }
        }
        catch (ObjectDisposedException) { }
    }

    // The engine's encoder, as before this bank compressed in play: banked by the asset system itself.
    private Image? EncodeEngine(string vpath, out long was)
    {
        was = 0;
        Image? image = _assets.CompressForCache(vpath);
        if (image is null) return null;
        was = (long)image.GetWidth() * image.GetHeight() * 4 * 4 / 3;
        return image;
    }

    // The managed encoder: the picture as it was uploaded, to RGBA8, to BC7 blocks, banked like the engine's own.
    private Image? EncodeManaged(string vpath, out long was)
    {
        was = 0;
        Image? source = _assets.DecodeForCompression(vpath);
        if (source is null) return null;
        byte[] rgba;
        int width, height, levels;
        try
        {
            if (source.GetFormat() != Image.Format.Rgba8) source.Convert(Image.Format.Rgba8);
            width = source.GetWidth();
            height = source.GetHeight();
            levels = source.GetMipmapCount() + 1;
            rgba = source.GetData();
        }
        finally { source.Dispose(); }
        if (rgba.Length < Bc7Mode6Encoder.ChainRgbaBytes(width, height, levels)) return null;
        int bytes = Bc7Mode6Encoder.ChainBytes(width, height, levels);
        if (_blocks.Length < bytes) _blocks = new byte[Math.Max(bytes, _blocks.Length * 2)];
        if (!Bc7Mode6Encoder.EncodeChain(rgba, width, height, levels, _blocks, () => _cancel.IsCancellationRequested)) return null;
        was = rgba.Length;
        Interlocked.Add(ref _inPlayPixels, (long)width * height);
        Image compressed = Image.CreateFromData(width, height, true, Image.Format.BptcRgba, new ReadOnlySpan<byte>(_blocks, 0, bytes));
        _assets.BankCompressed(vpath, compressed);
        return compressed;
    }

    public void Dispose()
    {
        if (_cancel.IsCancellationRequested) return;
        _cancel.Cancel();
        // Not waited for: an encode in hand ends by itself (a read from the closed file system fails and is caught).
        _thread?.Join(250);
        while (_ready.TryDequeue(out Ready left)) RenderingServer.FreeRid(left.Texture);
        if (_encoded + _failed + _queue.Count > 0)
        {
            double perSecond = (double)System.Diagnostics.Stopwatch.Frequency;
            _note(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"texture cache: {_encoded} textures compressed in the background in {Volatile.Read(ref _encodeTicks) / perSecond:0.0} s for the next load " +
                $"({_encodedInPlay} of them during play by the managed encoder: {Volatile.Read(ref _inPlayPixels) / 1e6:0.0} megapixels in {Volatile.Read(ref _inPlayTicks) / perSecond:0.0} s), " +
                $"{_failed} failed, {_queue.Count} left for another session; {_swapped} took the place of their uncompressed texture ({_savedBytes / 1048576.0:0} MB given back), {_notSwapped} could not"));
        }
        _cancel.Dispose();
    }
}
