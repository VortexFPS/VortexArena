using System;
using VortexArena.Formats.Images;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The managed BC7 encoder (mode 6 only) that legacy compatibility mode uses to compress textures during play.
/// Each test decodes what was encoded with a decoder written from the format's description, so a wrong bit
/// layout shows as a wrong picture rather than as an agreeing pair of mistakes.
/// </summary>
public class Bc7Mode6EncoderTests
{
    private static readonly int[] s_weights = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    // BC7 mode 6: bit 6 is the mode bit; R0 R1 G0 G1 B0 B1 A0 A1 (7 bits each), P0, P1, then 16 indices of
    // 4 bits, the first stored in 3.
    private static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> px)
    {
        Assert.Equal(0x40, block[0] & 0x7F);
        int at = 7;
        int Take(ReadOnlySpan<byte> b, int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++, at++) value |= ((b[at >> 3] >> (at & 7)) & 1) << i;
            return value;
        }
        Span<int> e0 = stackalloc int[4], e1 = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            e0[c] = Take(block, 7);
            e1[c] = Take(block, 7);
        }
        int p0 = Take(block, 1), p1 = Take(block, 1);
        for (int c = 0; c < 4; c++)
        {
            e0[c] = (e0[c] << 1) | p0;
            e1[c] = (e1[c] << 1) | p1;
        }
        for (int i = 0; i < 16; i++)
        {
            int w = s_weights[Take(block, i == 0 ? 3 : 4)];
            for (int c = 0; c < 4; c++) px[i * 4 + c] = (byte)(((64 - w) * e0[c] + w * e1[c] + 32) >> 6);
        }
        Assert.Equal(128, at);
    }

    private static byte[] Decode(byte[] blocks, int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                DecodeBlock(blocks.AsSpan((by * bw + bx) * 16, 16), px);
                for (int y = 0; y < 4 && by * 4 + y < height; y++)
                    for (int x = 0; x < 4 && bx * 4 + x < width; x++)
                        px.Slice((y * 4 + x) * 4, 4).CopyTo(rgba.AsSpan(((by * 4 + y) * width + bx * 4 + x) * 4, 4));
            }
        return rgba;
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }
        if (sum == 0) return double.PositiveInfinity;
        return 10 * Math.Log10(255.0 * 255.0 / (sum / a.Length));
    }

    private static byte[] Encode(byte[] rgba, int width, int height)
    {
        byte[] blocks = new byte[Bc7Mode6Encoder.LevelBytes(width, height)];
        Assert.True(Bc7Mode6Encoder.EncodeLevel(rgba, width, height, blocks));
        return blocks;
    }

    [Fact]
    public void A_flat_opaque_colour_comes_back_within_one_step_and_fully_opaque()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            byte r = (byte)random.Next(256), g = (byte)random.Next(256), b = (byte)random.Next(256);
            byte[] rgba = new byte[64];
            for (int i = 0; i < 16; i++) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255; }
            byte[] back = Decode(Encode(rgba, 4, 4), 4, 4);
            for (int i = 0; i < 16; i++)
            {
                Assert.InRange(back[i * 4] - r, -1, 1);
                Assert.InRange(back[i * 4 + 1] - g, -1, 1);
                Assert.InRange(back[i * 4 + 2] - b, -1, 1);
                Assert.Equal(255, back[i * 4 + 3]);
            }
        }
    }

    [Fact]
    public void An_opaque_picture_stays_opaque_in_every_pixel()
    {
        var random = new Random(11);
        byte[] rgba = new byte[64 * 64 * 4];
        random.NextBytes(rgba);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        byte[] back = Decode(Encode(rgba, 64, 64), 64, 64);
        for (int i = 3; i < back.Length; i += 4) Assert.Equal(255, back[i]);
    }

    [Fact]
    public void A_smooth_gradient_is_reproduced_closely()
    {
        const int size = 64;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                rgba[o] = (byte)((x + y) * 2);
                rgba[o + 1] = (byte)(255 - (x + y) * 2);
                rgba[o + 2] = (byte)(40 + x + y);
                rgba[o + 3] = 255;
            }
        double psnr = Psnr(rgba, Decode(Encode(rgba, size, size), size, size));
        Assert.True(psnr > 45, $"PSNR {psnr:0.0} dB");
    }

    [Fact]
    public void Two_colours_changing_in_two_directions_at_once_is_the_limit_of_one_line()
    {
        // Red along x and green along y: the sixteen colours of a block span a plane, and mode 6 has one line
        // through it. The full encoder would split such a block; this one is held to S3TC's level here.
        const int size = 64;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                rgba[o] = (byte)(x * 4);
                rgba[o + 1] = (byte)(y * 4);
                rgba[o + 2] = (byte)((x + y) * 2);
                rgba[o + 3] = 255;
            }
        double psnr = Psnr(rgba, Decode(Encode(rgba, size, size), size, size));
        Assert.True(psnr > 38, $"PSNR {psnr:0.0} dB");
    }

    [Fact]
    public void Alpha_that_varies_is_carried()
    {
        const int size = 32;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                rgba[o] = 200; rgba[o + 1] = 120; rgba[o + 2] = 40;
                rgba[o + 3] = (byte)(x * 8);
            }
        byte[] back = Decode(Encode(rgba, size, size), size, size);
        double psnr = Psnr(rgba, back);
        Assert.True(psnr > 42, $"PSNR {psnr:0.0} dB");
    }

    [Fact]
    public void Noise_with_structure_beats_what_four_steps_of_565_could_do()
    {
        // A textured surface: two colours mixed by smooth noise. S3TC holds four steps between 5:6:5 endpoints
        // and reaches some 35 to 38 dB on this; sixteen steps between 8-bit endpoints must do clearly better.
        const int size = 128;
        var random = new Random(3);
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double t = 0.5 + 0.35 * Math.Sin(x * 0.21) * Math.Cos(y * 0.17) + (random.NextDouble() - 0.5) * 0.2;
                t = Math.Clamp(t, 0, 1);
                int o = (y * size + x) * 4;
                rgba[o] = (byte)(60 + t * 150);
                rgba[o + 1] = (byte)(40 + t * 100);
                rgba[o + 2] = (byte)(30 + t * 60);
                rgba[o + 3] = 255;
            }
        double psnr = Psnr(rgba, Decode(Encode(rgba, size, size), size, size));
        Assert.True(psnr > 42, $"PSNR {psnr:0.0} dB");
    }

    [Fact]
    public void A_picture_that_is_not_a_multiple_of_four_and_a_whole_mip_chain_encode()
    {
        var random = new Random(5);
        byte[] rgba = new byte[10 * 6 * 4];
        random.NextBytes(rgba);
        byte[] blocks = Encode(rgba, 10, 6);
        Assert.Equal(3 * 2 * 16, blocks.Length);
        Decode(blocks, 10, 6);   // every block is a valid mode-6 block

        // 8x8, 4x4, 2x2, 1x1: 4 + 1 + 1 + 1 blocks.
        byte[] chain = new byte[(64 + 16 + 4 + 1) * 4];
        random.NextBytes(chain);
        byte[]? encoded = Bc7Mode6Encoder.EncodeChain(chain, 8, 8, 4);
        Assert.NotNull(encoded);
        Assert.Equal(7 * 16, encoded!.Length);
        Assert.Null(Bc7Mode6Encoder.EncodeChain(chain, 8, 8, 4, () => true));
    }
}
