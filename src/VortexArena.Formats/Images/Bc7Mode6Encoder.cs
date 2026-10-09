using System;

namespace VortexArena.Formats.Images;

/// <summary>
/// A BC7 (BPTC) block encoder that writes mode 6 only: one subset, RGBA endpoints of 7 bits plus one shared
/// low bit each, sixteen interpolation steps. It exists so that a texture can be block-compressed during play
/// on a thread of our own. The engine's BC7 encoder searches all eight modes and their partitions, is spread
/// over the engine's worker pool, and holds the frame thread for the second or so a texture takes; this one
/// is plain managed code that runs wherever it is called, at a few tens of milliseconds a megapixel.
///
/// <para>What is given up: a block with two unrelated colour groups (a hard edge between two materials) is
/// drawn along one line through both, where the full encoder would give each group its own endpoints. On
/// smooth or single-material blocks - most of a texture - mode 6 is what the full encoder picks anyway.
/// Against S3TC (DXT1/DXT5) it is better everywhere: 8-bit endpoints instead of 5:6:5, sixteen steps instead
/// of four, and alpha at full precision in the same block.</para>
///
/// <para>The output is ordinary BC7: any decoder reads it.</para>
/// </summary>
public static class Bc7Mode6Encoder
{
    // BC7's interpolation weights for a 4-bit index (of 64).
    private static readonly int[] s_weights = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    /// <summary>Bytes of BC7 for one image of the given size (16 a block, blocks of 4 by 4, partial ones whole).</summary>
    public static int LevelBytes(int width, int height) => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;

    /// <summary>Bytes of RGBA8 for a mip chain of <paramref name="levels"/> images, each half the last (not below 1).</summary>
    public static long ChainRgbaBytes(int width, int height, int levels)
    {
        long total = 0;
        for (int i = 0; i < levels; i++)
        {
            total += (long)width * height * 4;
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return total;
    }

    /// <summary>
    /// Encodes a whole mip chain laid out the way the engine stores one: the images one after another, largest
    /// first, each RGBA8 row by row. Returns the BC7 chain in the same order. <paramref name="cancel"/> is asked
    /// between rows of blocks; a cancelled call returns null.
    /// </summary>
    public static byte[]? EncodeChain(ReadOnlySpan<byte> rgba, int width, int height, int levels, Func<bool>? cancel = null)
    {
        byte[] blocks = new byte[ChainBytes(width, height, levels)];
        return EncodeChain(rgba, width, height, levels, blocks, cancel) ? blocks : null;
    }

    /// <summary>Bytes of BC7 for a mip chain of <paramref name="levels"/> images, each half the last (not below 1).</summary>
    public static int ChainBytes(int width, int height, int levels)
    {
        if (width <= 0 || height <= 0 || levels <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int total = 0;
        for (int i = 0; i < levels; i++, width = Math.Max(1, width >> 1), height = Math.Max(1, height >> 1)) total += LevelBytes(width, height);
        return total;
    }

    /// <summary><see cref="EncodeChain(ReadOnlySpan{byte}, int, int, int, Func{bool}?)"/> into a buffer of the
    /// caller's (<see cref="ChainBytes"/> long, or longer). False if cancelled.</summary>
    public static bool EncodeChain(ReadOnlySpan<byte> rgba, int width, int height, int levels, Span<byte> blocks, Func<bool>? cancel = null)
    {
        if (width <= 0 || height <= 0 || levels <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgba.Length < ChainRgbaBytes(width, height, levels)) throw new ArgumentException("the pixel data is shorter than the mip chain", nameof(rgba));
        if (blocks.Length < ChainBytes(width, height, levels)) throw new ArgumentException("the block buffer is shorter than the mip chain", nameof(blocks));
        int from = 0, to = 0;
        for (int i = 0; i < levels; i++)
        {
            int bytes = LevelBytes(width, height);
            if (!EncodeLevel(rgba.Slice(from, width * height * 4), width, height, blocks.Slice(to, bytes), cancel)) return false;
            from += width * height * 4;
            to += bytes;
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return true;
    }

    /// <summary>Encodes one RGBA8 image into <paramref name="blocks"/> (<see cref="LevelBytes"/> long). False if cancelled.</summary>
    public static bool EncodeLevel(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> blocks, Func<bool>? cancel = null)
    {
        int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
        if (blocks.Length < bw * bh * 16) throw new ArgumentException("the block buffer is too short", nameof(blocks));
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        {
            if (cancel is not null && cancel()) return false;
            for (int bx = 0; bx < bw; bx++)
            {
                // A block that hangs over the edge repeats the edge's pixels, as the engine's encoders do.
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, height - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, width - 1);
                        rgba.Slice((sy * width + sx) * 4, 4).CopyTo(px.Slice((y * 4 + x) * 4, 4));
                    }
                }
                EncodeBlock(px, blocks.Slice((by * bw + bx) * 16, 16));
            }
        }
        return true;
    }

    /// <summary>Encodes sixteen RGBA8 pixels (row by row) into one 16-byte block.</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> block)
    {
        // Mean, range, and whether the block is opaque (then both endpoints keep alpha at exactly 255).
        float m0 = 0, m1 = 0, m2 = 0, m3 = 0;
        bool opaque = true, flat = true;
        for (int i = 0; i < 64; i += 4)
        {
            m0 += px[i]; m1 += px[i + 1]; m2 += px[i + 2]; m3 += px[i + 3];
            if (px[i + 3] != 255) opaque = false;
            if (px[i] != px[0] || px[i + 1] != px[1] || px[i + 2] != px[2] || px[i + 3] != px[3]) flat = false;
        }
        m0 *= 1f / 16; m1 *= 1f / 16; m2 *= 1f / 16; m3 *= 1f / 16;

        Span<int> lo = stackalloc int[4], hi = stackalloc int[4];
        Span<int> bestLo = stackalloc int[4], bestHi = stackalloc int[4];
        Span<byte> index = stackalloc byte[16], bestIndex = stackalloc byte[16];
        Span<float> eLo = stackalloc float[4], eHi = stackalloc float[4];

        if (flat)
        {
            // One colour: both endpoints the nearest value the format holds, every index 0.
            eLo[0] = px[0]; eLo[1] = px[1]; eLo[2] = px[2]; eLo[3] = px[3];
            Quantize(eLo, opaque, bestLo);
            bestLo.CopyTo(bestHi);
            bestIndex.Clear();
            Pack(bestLo, bestHi, bestIndex, block);
            return;
        }

        // The principal axis of the sixteen points in RGBA: a few steps of power iteration on the covariance.
        Span<float> cov = stackalloc float[10];   // upper triangle: 00 01 02 03 11 12 13 22 23 33
        for (int i = 0; i < 64; i += 4)
        {
            float d0 = px[i] - m0, d1 = px[i + 1] - m1, d2 = px[i + 2] - m2, d3 = px[i + 3] - m3;
            cov[0] += d0 * d0; cov[1] += d0 * d1; cov[2] += d0 * d2; cov[3] += d0 * d3;
            cov[4] += d1 * d1; cov[5] += d1 * d2; cov[6] += d1 * d3;
            cov[7] += d2 * d2; cov[8] += d2 * d3;
            cov[9] += d3 * d3;
        }
        // Start from the direction of the largest variances, which the iteration cannot lose.
        float a0 = cov[0], a1 = cov[4], a2 = cov[7], a3 = cov[9];
        if (a0 + a1 + a2 + a3 <= 0) { a0 = a1 = a2 = 1; a3 = 0; }
        for (int step = 0; step < 6; step++)
        {
            float n0 = cov[0] * a0 + cov[1] * a1 + cov[2] * a2 + cov[3] * a3;
            float n1 = cov[1] * a0 + cov[4] * a1 + cov[5] * a2 + cov[6] * a3;
            float n2 = cov[2] * a0 + cov[5] * a1 + cov[7] * a2 + cov[8] * a3;
            float n3 = cov[3] * a0 + cov[6] * a1 + cov[8] * a2 + cov[9] * a3;
            float len = MathF.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + n3 * n3);
            if (!(len > 1e-12f)) break;
            len = 1f / len;
            a0 = n0 * len; a1 = n1 * len; a2 = n2 * len; a3 = n3 * len;
        }
        {
            float len = MathF.Sqrt(a0 * a0 + a1 * a1 + a2 * a2 + a3 * a3);
            if (len > 0) { len = 1f / len; a0 *= len; a1 *= len; a2 *= len; a3 *= len; }
        }

        float tMin = float.MaxValue, tMax = float.MinValue;
        for (int i = 0; i < 64; i += 4)
        {
            float t = (px[i] - m0) * a0 + (px[i + 1] - m1) * a1 + (px[i + 2] - m2) * a2 + (px[i + 3] - m3) * a3;
            if (t < tMin) tMin = t;
            if (t > tMax) tMax = t;
        }
        eLo[0] = m0 + a0 * tMin; eLo[1] = m1 + a1 * tMin; eLo[2] = m2 + a2 * tMin; eLo[3] = m3 + a3 * tMin;
        eHi[0] = m0 + a0 * tMax; eHi[1] = m1 + a1 * tMax; eHi[2] = m2 + a2 * tMax; eHi[3] = m3 + a3 * tMax;

        long bestError = long.MaxValue;
        Span<float> ap = stackalloc float[4], bp = stackalloc float[4];
        // The first pass uses the axis; each later one solves for the endpoints that best fit the indices the
        // pass before chose (least squares), which is where most of the quality comes from.
        for (int pass = 0; pass < 3; pass++)
        {
            Quantize(eLo, opaque, lo);
            Quantize(eHi, opaque, hi);
            long error = Assign(px, lo, hi, index);
            if (error < bestError)
            {
                bestError = error;
                lo.CopyTo(bestLo);
                hi.CopyTo(bestHi);
                index.CopyTo(bestIndex);
                if (error == 0) break;
            }
            else break;

            // Least squares: minimise sum |(1 - w) L + w H - p|^2 over L and H, per channel.
            float saa = 0, sab = 0, sbb = 0;
            ap.Clear();
            bp.Clear();
            for (int i = 0; i < 16; i++)
            {
                float w = s_weights[index[i]] * (1f / 64), u = 1f - w;
                saa += u * u; sab += u * w; sbb += w * w;
                for (int c = 0; c < 4; c++)
                {
                    ap[c] += u * px[i * 4 + c];
                    bp[c] += w * px[i * 4 + c];
                }
            }
            float det = saa * sbb - sab * sab;
            if (MathF.Abs(det) < 1e-6f) break;
            det = 1f / det;
            for (int c = 0; c < 4; c++)
            {
                eLo[c] = (ap[c] * sbb - bp[c] * sab) * det;
                eHi[c] = (bp[c] * saa - ap[c] * sab) * det;
            }
        }
        Pack(bestLo, bestHi, bestIndex, block);
    }

    // An endpoint as the format holds it: four values of 7 bits and one low bit shared by all four. Both low
    // bits are tried and the nearer kept; an opaque block takes 1, the only way to say alpha 255.
    private static void Quantize(ReadOnlySpan<float> wanted, bool opaque, Span<int> endpoint)
    {
        float best = float.MaxValue;
        Span<int> trial = stackalloc int[4];
        for (int p = opaque ? 1 : 0; p < 2; p++)
        {
            float error = 0;
            for (int c = 0; c < 4; c++)
            {
                float v = wanted[c] < 0 ? 0 : wanted[c] > 255 ? 255 : wanted[c];
                int q = (int)MathF.Floor((v - p) * 0.5f + 0.5f);
                q = q < 0 ? 0 : q > 127 ? 127 : q;
                int value = (q << 1) | p;
                trial[c] = value;
                float d = value - v;
                error += d * d;
            }
            if (opaque) trial[3] = 255;
            if (error < best)
            {
                best = error;
                trial.CopyTo(endpoint);
            }
        }
    }

    // The nearest of the sixteen interpolated colours for each pixel; returns the summed squared error.
    private static long Assign(ReadOnlySpan<byte> px, ReadOnlySpan<int> lo, ReadOnlySpan<int> hi, Span<byte> index)
    {
        Span<int> palette = stackalloc int[64];
        for (int k = 0; k < 16; k++)
        {
            int w = s_weights[k];
            for (int c = 0; c < 4; c++) palette[k * 4 + c] = ((64 - w) * lo[c] + w * hi[c] + 32) >> 6;
        }
        // Along the line from lo to hi the error is convex in the step, so the search starts at the projection
        // and walks while it improves.
        int d0 = hi[0] - lo[0], d1 = hi[1] - lo[1], d2 = hi[2] - lo[2], d3 = hi[3] - lo[3];
        int len2 = d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
        long total = 0;
        for (int i = 0; i < 16; i++)
        {
            int p0 = px[i * 4], p1 = px[i * 4 + 1], p2 = px[i * 4 + 2], p3 = px[i * 4 + 3];
            int k = 0;
            if (len2 > 0)
            {
                int dot = (p0 - lo[0]) * d0 + (p1 - lo[1]) * d1 + (p2 - lo[2]) * d2 + (p3 - lo[3]) * d3;
                k = (int)((dot * 15L + (len2 >> 1)) / len2);
                k = k < 0 ? 0 : k > 15 ? 15 : k;
            }
            int best = Distance(palette, k, p0, p1, p2, p3);
            while (k > 0)
            {
                int e = Distance(palette, k - 1, p0, p1, p2, p3);
                if (e >= best) break;
                best = e;
                k--;
            }
            while (k < 15)
            {
                int e = Distance(palette, k + 1, p0, p1, p2, p3);
                if (e >= best) break;
                best = e;
                k++;
            }
            index[i] = (byte)k;
            total += best;
        }
        return total;
    }

    private static int Distance(ReadOnlySpan<int> palette, int k, int p0, int p1, int p2, int p3)
    {
        int e0 = palette[k * 4] - p0, e1 = palette[k * 4 + 1] - p1, e2 = palette[k * 4 + 2] - p2, e3 = palette[k * 4 + 3] - p3;
        return e0 * e0 + e1 * e1 + e2 * e2 + e3 * e3;
    }

    // Mode 6, least significant bit first: 0000001, then R0 R1 G0 G1 B0 B1 A0 A1 at 7 bits each, the two
    // low bits, then the indices: 3 bits for the first (its top bit is implied 0), 4 for the rest.
    private static void Pack(ReadOnlySpan<int> lo, ReadOnlySpan<int> hi, ReadOnlySpan<byte> index, Span<byte> block)
    {
        Span<int> a = stackalloc int[4], b = stackalloc int[4];
        lo.CopyTo(a);
        hi.CopyTo(b);
        bool swap = index[0] >= 8;
        if (swap)
        {
            // The first index must fit in 3 bits: exchange the endpoints and count the steps from the other end.
            hi.CopyTo(a);
            lo.CopyTo(b);
        }
        ulong low = 1UL << 6, high = 0;
        int at = 7;
        for (int c = 0; c < 4; c++)
        {
            Put(ref low, ref high, ref at, (ulong)(a[c] >> 1), 7);
            Put(ref low, ref high, ref at, (ulong)(b[c] >> 1), 7);
        }
        Put(ref low, ref high, ref at, (ulong)(a[0] & 1), 1);
        Put(ref low, ref high, ref at, (ulong)(b[0] & 1), 1);
        for (int i = 0; i < 16; i++)
        {
            int k = swap ? 15 - index[i] : index[i];
            Put(ref low, ref high, ref at, (ulong)k, i == 0 ? 3 : 4);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(block, low);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(8), high);
    }

    private static void Put(ref ulong low, ref ulong high, ref int at, ulong value, int bits)
    {
        if (at < 64)
        {
            low |= value << at;
            if (at + bits > 64) high |= value >> (64 - at);
        }
        else high |= value << (at - 64);
        at += bits;
    }
}
