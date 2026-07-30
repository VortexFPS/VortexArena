using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Hud;

/// <summary>
/// Lazy, bounded thumbnail store for the editor's texture browser (backlog T6).
///
/// The browser lists every parsed shader — around two thousand on the stock data — and each one's diffuse is
/// a 512²-1024² image: a megabyte or four of decode plus a GPU upload. Loading them all is gigabytes and a
/// multi-second stall on the frame the dialog opens, so this loads only what is ON SCREEN, and evicts the
/// least recently DRAWN once it is full. Scrolling the whole list then costs a fixed amount rather than a
/// growing one.
///
/// The work is split the way the model pipeline splits it: read, decode and downscale on a worker, and only
/// the <c>ImageTexture.CreateFromImage</c> upload on the main thread, inside the streamer's per-frame budget.
///
/// The loader and the scheduler are INJECTED rather than referenced, so <c>game/hud</c> keeps not depending
/// on <c>game/loaders</c> — the same inversion as <see cref="TextureCache.VfsResolver"/>. With neither wired
/// the browser simply draws swatches, which is also what happens in a session with no asset system at all.
/// </summary>
public sealed class EditorThumbnailCache
{
    private sealed class Entry
    {
        /// <summary>Null with <see cref="Pending"/> false is a KNOWN miss: draw the swatch, never re-request.</summary>
        public Texture2D? Tex;

        public bool Pending;

        /// <summary>Frame clock at the last <see cref="Peek"/> — the eviction order.</summary>
        public ulong Used;
    }

    // Case-insensitive because a face's Material and a browser row's Label are the same shader spelled two
    // ways: the shader dictionary is OrdinalIgnoreCase, but its keys come back AS DECLARED.
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, Entry>> _victims = new();
    private ulong _clock;
    private int _inFlight;

    /// <summary>
    /// Worker phase: (material, size) → a decoded, down-scaled image, or null when the material has none.
    /// Wired by the host to <c>AssetSystem.LoadThumbnailImage</c>.
    /// </summary>
    public Func<string, int, Image?>? Decode { get; set; }

    /// <summary>
    /// Two-phase scheduler, wired to the background streamer. Null runs the decode synchronously on the
    /// caller, which is correct for a session with no streamer and merely slower.
    /// </summary>
    public Action<Func<Image?>, Action<Image?>>? Schedule { get; set; }

    /// <summary>Source edge in pixels — what gets decoded and held, so this is the memory knob.</summary>
    public int Size { get; private set; } = 96;

    /// <summary>Resident thumbnails before the least recently drawn are freed.</summary>
    public int Capacity { get; set; } = 512;

    /// <summary>Loads in flight at once. A flick-scroll must not queue the entire list.</summary>
    public int MaxInFlight { get; set; } = 16;

    /// <summary>How many thumbnails are resident (diagnostics).</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Change the source resolution, discarding everything held at the old one.
    ///
    /// Mixed sizes would draw into the same rect at different sharpness, which looks like a rendering bug
    /// with nothing in any log to explain it.
    /// </summary>
    public void SetSize(int size)
    {
        size = Math.Clamp(size, 16, 512);
        if (size == Size)
            return;
        Size = size;
        Clear();
    }

    /// <summary>Advance the use clock. Once per drawn frame, before the peek/request pass.</summary>
    public void BeginFrame() => _clock++;

    /// <summary>The thumbnail if it is resident, marking it used. Null while pending or on a miss.</summary>
    public Texture2D? Peek(string material)
    {
        if (string.IsNullOrEmpty(material) || !_entries.TryGetValue(material, out Entry? e))
            return null;
        e.Used = _clock;
        return e.Tex;
    }

    /// <summary>True when this material is KNOWN to have no image — draw the fallback and stop asking.</summary>
    public bool IsMiss(string material)
        => !string.IsNullOrEmpty(material)
           && _entries.TryGetValue(material, out Entry? e) && !e.Pending && e.Tex is null;

    /// <summary>
    /// Queue a load if this material has never been asked for and the in-flight budget allows. Idempotent and
    /// cheap: the draw pass calls it for every visible cell, every frame, and a refusal here simply means the
    /// next frame tries again.
    /// </summary>
    public void Request(string material)
    {
        if (string.IsNullOrEmpty(material) || Decode is null)
            return;
        if (_entries.ContainsKey(material))
            return;                        // resident, pending, or a known miss — all three mean "don't ask"
        if (_inFlight >= MaxInFlight)
            return;

        _entries[material] = new Entry { Pending = true, Used = _clock };
        _inFlight++;

        int size = Size;
        Func<Image?> work = () => Decode(material, size);
        if (Schedule is null)
        {
            Deliver(material, work());
            return;
        }
        Schedule(work, img => Deliver(material, img));
    }

    /// <summary>Free every resident texture. Call on session teardown.</summary>
    public void Clear()
    {
        foreach (Entry e in _entries.Values)
        {
            e.Tex?.Dispose();
            e.Tex = null;
        }
        _entries.Clear();
        _inFlight = 0;
    }

    private void Deliver(string material, Image? img)
    {
        _inFlight = Math.Max(0, _inFlight - 1);

        // Evicted or Clear()ed while the worker was running. Dropping the image is the whole response: the
        // next time the cell is drawn it asks again.
        if (!_entries.TryGetValue(material, out Entry? e))
            return;

        e.Pending = false;
        e.Used = _clock;
        if (img is not null)
            e.Tex = ImageTexture.CreateFromImage(img);   // main thread — the only render call in this class
        Evict();
    }

    private void Evict()
    {
        if (_entries.Count <= Capacity)
            return;

        // Drop a batch in one pass so eviction is not paid on every single insert once the cache is full.
        int target = _entries.Count - (Capacity - Capacity / 4);

        _victims.Clear();
        foreach (KeyValuePair<string, Entry> kv in _entries)
            if (!kv.Value.Pending)
                _victims.Add(kv);
        _victims.Sort((a, b) => a.Value.Used.CompareTo(b.Value.Used));

        for (int i = 0; i < target && i < _victims.Count; i++)
        {
            // DISPOSED, not dropped. Letting the finalizer thread free a Godot resource races the main
            // thread's rendering work — the heap-corruption class ClientWorld.ReleaseItemFlat documents. A
            // full cache scrolling a two-thousand-entry grid evicts continuously, so this is a steady drip
            // rather than a rare edge.
            _victims[i].Value.Tex?.Dispose();
            _entries.Remove(_victims[i].Key);
        }
        _victims.Clear();
    }
}
