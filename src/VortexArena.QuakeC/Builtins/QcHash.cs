// Port of Base/darkplaces/com_crc16.c CRC_Block, CRC_Block_CaseInsensitive and mdfour.c mdfour.
namespace VortexArena.QuakeC;

/// <summary>The two checksums the crc16 and digest_hex builtins need that the BCL does not have.</summary>
internal static class QcHash
{
    /// <summary>
    /// CRC-16/XMODEM (CCITT polynomial 0x1021, initial value 0xFFFF, not reflected) - Quake's CRC.
    /// The case-insensitive form lowers ASCII letters only, which is C's tolower in the "C" locale.
    /// </summary>
    public static ushort Crc16(ReadOnlySpan<byte> data, bool caseInsensitive)
    {
        ushort crc = 0xFFFF;
        ushort[] table = CrcTable;
        foreach (byte raw in data)
        {
            byte b = caseInsensitive && raw >= 'A' && raw <= 'Z' ? (byte)(raw + 32) : raw;
            crc = (ushort)((crc << 8) ^ table[(crc >> 8) ^ b]);
        }
        return crc;
    }

    // crctable[] of com_crc16.c, generated from the polynomial instead of copied.
    private static readonly ushort[] CrcTable = BuildCrcTable();

    private static ushort[] BuildCrcTable()
    {
        ushort[] table = new ushort[256];
        for (int n = 0; n < 256; n++)
        {
            int x = n << 8;
            for (int bit = 0; bit < 8; bit++) x = (x & 0x8000) != 0 ? (x << 1) ^ 0x1021 : x << 1;
            table[n] = (ushort)x;
        }
        return table;
    }

    /// <summary>MD4 (RFC 1320). Broken as a hash; here only because the program can ask for it by name.</summary>
    public static byte[] Md4(ReadOnlySpan<byte> message)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;

        // Padding: 0x80, zeros to 56 mod 64, then the length in bits, little-endian.
        int padded = message.Length + 1;
        while (padded % 64 != 56) padded++;
        byte[] buffer = new byte[padded + 8];
        message.CopyTo(buffer);
        buffer[message.Length] = 0x80;
        long bits = (long)message.Length * 8;
        for (int i = 0; i < 8; i++) buffer[padded + i] = (byte)(bits >> (8 * i));

        Span<uint> x = stackalloc uint[16];
        for (int offset = 0; offset < buffer.Length; offset += 64)
        {
            for (int i = 0; i < 16; i++) x[i] = BitConverter.ToUInt32(buffer, offset + i * 4);
            if (!BitConverter.IsLittleEndian) for (int i = 0; i < 16; i++) x[i] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x[i]);
            uint aa = a, bb = b, cc = c, dd = d;

            for (int i = 0; i < 16; i += 4)
            {
                a = Rotl(a + ((b & c) | (~b & d)) + x[i], 3);
                d = Rotl(d + ((a & b) | (~a & c)) + x[i + 1], 7);
                c = Rotl(c + ((d & a) | (~d & b)) + x[i + 2], 11);
                b = Rotl(b + ((c & d) | (~c & a)) + x[i + 3], 19);
            }
            for (int i = 0; i < 4; i++)
            {
                a = Rotl(a + Majority(b, c, d) + x[i] + 0x5a827999u, 3);
                d = Rotl(d + Majority(a, b, c) + x[i + 4] + 0x5a827999u, 5);
                c = Rotl(c + Majority(d, a, b) + x[i + 8] + 0x5a827999u, 9);
                b = Rotl(b + Majority(c, d, a) + x[i + 12] + 0x5a827999u, 13);
            }
            foreach (int i in Round3Order)
            {
                a = Rotl(a + (b ^ c ^ d) + x[i] + 0x6ed9eba1u, 3);
                d = Rotl(d + (a ^ b ^ c) + x[i + 8] + 0x6ed9eba1u, 9);
                c = Rotl(c + (d ^ a ^ b) + x[i + 4] + 0x6ed9eba1u, 11);
                b = Rotl(b + (c ^ d ^ a) + x[i + 12] + 0x6ed9eba1u, 15);
            }

            a += aa; b += bb; c += cc; d += dd;
        }

        byte[] digest = new byte[16];
        Write(digest, 0, a); Write(digest, 4, b); Write(digest, 8, c); Write(digest, 12, d);
        return digest;
    }

    private static readonly int[] Round3Order = { 0, 2, 1, 3 };

    private static uint Majority(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
    private static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));

    private static void Write(byte[] to, int at, uint v)
    {
        to[at] = (byte)v; to[at + 1] = (byte)(v >> 8); to[at + 2] = (byte)(v >> 16); to[at + 3] = (byte)(v >> 24);
    }
}
