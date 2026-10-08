// Port of Base/darkplaces/com_crc16.c CRC_Block.
namespace VortexArena.Legacy.Protocol;

/// <summary>
/// The 16-bit CRC DarkPlaces uses to identify files (csqc_progcrc, cl_downloadfinished): the CCITT
/// polynomial 0x1021, not reflected, starting from 0xFFFF with no final xor. This is the variant
/// usually listed as CRC-16/CCITT-FALSE; its check value over "123456789" is 0x29B1.
/// </summary>
public static class Crc16
{
    private const ushort InitValue = 0xFFFF; // CRC_INIT_VALUE; CRC_XOR_VALUE is 0 so it is not applied

    // com_crc16.c holds this as a literal table. It is the standard table for the polynomial, so it is
    // generated here instead of retyped; Crc16Tests pins entries against the values in the C source.
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            table[i] = (ushort)crc;
        }
        return table;
    }

    /// <summary>The table entry for <paramref name="index"/> (crctable[] in the C source).</summary>
    public static ushort TableEntry(int index) => Table[index & 0xFF];

    public static ushort Block(ReadOnlySpan<byte> data)
    {
        ushort crc = InitValue;
        foreach (byte b in data)
            crc = (ushort)((crc << 8) ^ Table[(crc >> 8) ^ b]);
        return crc;
    }
}
