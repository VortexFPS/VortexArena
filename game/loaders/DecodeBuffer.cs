using System;
using System.Collections.Concurrent;

namespace VortexArena.Game.Loaders;

/// <summary>
/// Shared reusable decode scratch for the image decoders (<see cref="TgaDecoder"/> / <see cref="DdsDecoder"/>).
/// Each decoder used to allocate a fresh <c>new byte[w*h*4]</c> per texture — a 2048² surface is 16 MB, and a
/// material/player texture SET decodes many same-size images back-to-back, so these were the 50-160 MB
/// worker-side decode bursts driving gen0/1/2 GC (PERFORMANCE_REPORT §12.6b; confirmed in the 2026-06-14
/// release profile, where GC pause tripled as bot models streamed in).
///
/// <para>Why a hand-rolled pool rather than <see cref="System.Buffers.ArrayPool{T}"/>:
/// <c>Image.CreateFromData</c> COPIES the buffer synchronously into the Godot Image (so it's free to reuse on
/// the next decode) but requires the array length to match the surface EXACTLY — an ArrayPool rental is only
/// guaranteed to be ≥ the requested size, so its oversized tail would be read as extra pixels. Buffers are
/// therefore keyed by exact byte length, and a reused buffer is cleared so it is byte-identical to a fresh
/// <c>new byte[]</c>.</para>
///
/// <para>Why PROCESS-WIDE rent/return rather than <c>[ThreadStatic]</c> (perf 2026-07-03): the streamer used
/// to fan decode jobs out via raw <c>Task.Run</c>, so a texture wave touched a dozen+ DISTINCT thread-pool
/// threads on a many-core box and each grew its own per-size buffer set — "pooled" decodes still allocated
/// 100-230 MB single frames at load/join (the <c>stream.predecode</c> / load-screen storm frames), and the
/// per-thread sets never converged because the pool's thread identity keeps changing. One shared pool serves
/// the (now bounded — see <c>BackgroundAssetStreamer</c>'s worker lane) decode workers plus the main thread;
/// retained memory is capped per size below instead of growing per thread.</para>
/// </summary>
internal static class RgbaDecodeBuffer
{
    // One bag per exact byte size: a material/player texture SET alternates sizes (2048² diffuse, 1024² norm,
    // 256² glow, the S3TC pass-through chain sizes, …), so a single last-size slot would re-allocate on every
    // size change. Bounded by the distinct texture byte-sizes seen, times at most MaxRetainedPerSize.
    private static readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> Pool = new();

    // Soft cap on RETAINED buffers per size. Decode concurrency is the streamer's worker lane (≤4) plus the
    // main thread, so more same-size buffers than that can only come from a one-off burst — let extras GC.
    private const int MaxRetainedPerSize = 6;

    /// <summary>
    /// A buffer of EXACTLY <paramref name="byteLength"/> bytes — zeroed by default so the result matches
    /// <c>new byte[]</c> even if a decoder leaves any byte unwritten (pass <paramref name="clear"/> = false
    /// when the caller overwrites every byte, e.g. a straight <c>Array.Copy</c> fill). Hand it back with
    /// <see cref="Return"/> once the pixels are copied out (e.g. by <c>Image.CreateFromData</c>).
    /// </summary>
    public static byte[] Rent(int byteLength, bool clear = true)
    {
        if (Pool.TryGetValue(byteLength, out ConcurrentBag<byte[]>? bag) && bag.TryTake(out byte[]? b))
        {
            if (clear)
                Array.Clear(b, 0, byteLength);
            return b;
        }
        return new byte[byteLength];   // fresh arrays are already zero
    }

    /// <summary>
    /// Return a rented buffer for reuse. Call ONLY after the pixels are copied out (<c>Image.CreateFromData</c>
    /// copies synchronously) — the next same-size <see cref="Rent"/> on ANY thread may hand the array out again.
    /// Rent/Return pairs are decoder-internal (try/finally around the decode), so a buffer is never returned twice.
    /// </summary>
    public static void Return(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0)
            return;
        ConcurrentBag<byte[]> bag = Pool.GetOrAdd(buffer.Length, static _ => new ConcurrentBag<byte[]>());
        if (bag.Count < MaxRetainedPerSize)   // racy over-admission is harmless (one extra retained buffer)
            bag.Add(buffer);
    }
}
