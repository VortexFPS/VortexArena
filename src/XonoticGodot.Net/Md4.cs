using System;

namespace XonoticGodot.Net;

/// <summary>
/// MD4 (RFC 1320) + HMAC-MD4 (RFC 2104), used ONLY for DarkPlaces <c>srcon</c> rcon parity (DS-6). MD4 is
/// cryptographically broken and must never be used for anything but reproducing DP's on-wire rcon auth, whose
/// format is frozen — <see cref="RconProtocol"/> hashes <c>"&lt;time-or-challenge&gt; &lt;command&gt;"</c> keyed by the
/// server's <c>rcon_password</c>, exactly as DP's <c>hmac_mdfour_time_matching</c>/<c>_challenge_matching</c> do
/// (darkplaces/netconn.c). .NET has no MD4, so this is a from-scratch RFC-1320 implementation, verified against
/// the RFC's published test vectors in <c>Md4Tests</c>.
/// </summary>
public static class Md4
{
    /// <summary>Compute the 16-byte MD4 digest of <paramref name="message"/>.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> message)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;

        // Length in bits, little-endian, appended after a 0x80 pad byte and 0x00 padding to a 56-mod-64 boundary.
        long bitLen = (long)message.Length * 8;
        int padded = message.Length + 1;
        while (padded % 64 != 56) padded++;
        byte[] buf = new byte[padded + 8];
        message.CopyTo(buf);
        buf[message.Length] = 0x80;
        for (int i = 0; i < 8; i++)
            buf[padded + i] = (byte)(bitLen >> (8 * i));

        uint[] x = new uint[16]; // heap (not a ref struct) so the round helpers below can capture it; reused per block
        for (int off = 0; off < buf.Length; off += 64)
        {
            for (int i = 0; i < 16; i++)
                x[i] = (uint)(buf[off + i * 4]
                    | (buf[off + i * 4 + 1] << 8)
                    | (buf[off + i * 4 + 2] << 16)
                    | (buf[off + i * 4 + 3] << 24));

            uint aa = a, bb = b, cc = c, dd = d;

            // Round 1: F(x,y,z) = (x&y) | (~x&z)
            static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
            void FF(ref uint p, uint q, uint r, uint s, int k, int shift)
                => p = Rotl(p + F(q, r, s) + x[k], shift);
            FF(ref a, b, c, d, 0, 3); FF(ref d, a, b, c, 1, 7); FF(ref c, d, a, b, 2, 11); FF(ref b, c, d, a, 3, 19);
            FF(ref a, b, c, d, 4, 3); FF(ref d, a, b, c, 5, 7); FF(ref c, d, a, b, 6, 11); FF(ref b, c, d, a, 7, 19);
            FF(ref a, b, c, d, 8, 3); FF(ref d, a, b, c, 9, 7); FF(ref c, d, a, b, 10, 11); FF(ref b, c, d, a, 11, 19);
            FF(ref a, b, c, d, 12, 3); FF(ref d, a, b, c, 13, 7); FF(ref c, d, a, b, 14, 11); FF(ref b, c, d, a, 15, 19);

            // Round 2: G(x,y,z) = (x&y) | (x&z) | (y&z), constant 0x5a827999
            static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
            void GG(ref uint p, uint q, uint r, uint s, int k, int shift)
                => p = Rotl(p + G(q, r, s) + x[k] + 0x5a827999u, shift);
            GG(ref a, b, c, d, 0, 3); GG(ref d, a, b, c, 4, 5); GG(ref c, d, a, b, 8, 9); GG(ref b, c, d, a, 12, 13);
            GG(ref a, b, c, d, 1, 3); GG(ref d, a, b, c, 5, 5); GG(ref c, d, a, b, 9, 9); GG(ref b, c, d, a, 13, 13);
            GG(ref a, b, c, d, 2, 3); GG(ref d, a, b, c, 6, 5); GG(ref c, d, a, b, 10, 9); GG(ref b, c, d, a, 14, 13);
            GG(ref a, b, c, d, 3, 3); GG(ref d, a, b, c, 7, 5); GG(ref c, d, a, b, 11, 9); GG(ref b, c, d, a, 15, 13);

            // Round 3: H(x,y,z) = x ^ y ^ z, constant 0x6ed9eba1
            static uint H(uint x, uint y, uint z) => x ^ y ^ z;
            void HH(ref uint p, uint q, uint r, uint s, int k, int shift)
                => p = Rotl(p + H(q, r, s) + x[k] + 0x6ed9eba1u, shift);
            HH(ref a, b, c, d, 0, 3); HH(ref d, a, b, c, 8, 9); HH(ref c, d, a, b, 4, 11); HH(ref b, c, d, a, 12, 15);
            HH(ref a, b, c, d, 2, 3); HH(ref d, a, b, c, 10, 9); HH(ref c, d, a, b, 6, 11); HH(ref b, c, d, a, 14, 15);
            HH(ref a, b, c, d, 1, 3); HH(ref d, a, b, c, 9, 9); HH(ref c, d, a, b, 5, 11); HH(ref b, c, d, a, 13, 15);
            HH(ref a, b, c, d, 3, 3); HH(ref d, a, b, c, 11, 9); HH(ref c, d, a, b, 7, 11); HH(ref b, c, d, a, 15, 15);

            a += aa; b += bb; c += cc; d += dd;
        }

        var outp = new byte[16];
        WriteLe(outp, 0, a); WriteLe(outp, 4, b); WriteLe(outp, 8, c); WriteLe(outp, 12, d);
        return outp;
    }

    /// <summary>
    /// HMAC-MD4 (RFC 2104) — the exact construction DP's <c>hmac()</c> uses for <c>srcon</c>: 64-byte block,
    /// key hashed if longer than the block and zero-padded if shorter, ipad 0x36 / opad 0x5c. Returns 16 bytes.
    /// </summary>
    public static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        const int block = 64;
        Span<byte> k = stackalloc byte[block];
        if (key.Length > block)
            Hash(key).CopyTo(k);            // hash a too-long key down to 16 bytes, zero-padded to the block
        else
            key.CopyTo(k);                  // short/exact key: copy, remainder stays zero (stackalloc is zeroed)

        Span<byte> ipad = stackalloc byte[block];
        Span<byte> opad = stackalloc byte[block];
        for (int i = 0; i < block; i++)
        {
            ipad[i] = (byte)(k[i] ^ 0x36);
            opad[i] = (byte)(k[i] ^ 0x5c);
        }

        // inner = MD4(ipad || message); out = MD4(opad || inner)
        byte[] inner = new byte[block + message.Length];
        ipad.CopyTo(inner);
        message.CopyTo(inner.AsSpan(block));
        byte[] innerHash = Hash(inner);

        byte[] outer = new byte[block + innerHash.Length];
        opad.CopyTo(outer);
        innerHash.CopyTo(outer.AsSpan(block));
        return Hash(outer);
    }

    private static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));

    private static void WriteLe(byte[] dst, int off, uint v)
    {
        dst[off] = (byte)v;
        dst[off + 1] = (byte)(v >> 8);
        dst[off + 2] = (byte)(v >> 16);
        dst[off + 3] = (byte)(v >> 24);
    }
}
