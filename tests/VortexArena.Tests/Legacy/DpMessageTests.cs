using System;
using System.Numerics;
using System.Text;
using VortexArena.Legacy.Protocol;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>The DP7 wire primitives (com_msg.c) and CRC_Block (com_crc16.c), against hand-built bytes.</summary>
public class DpMessageTests
{
    [Fact]
    public void Reader_Integers_Are_Little_Endian_And_Sign_Extended()
    {
        var r = new DpMessageReader(new byte[]
        {
            0xFE,                   // byte 254
            0xFE,                   // char -2
            0x34, 0x12,             // short 0x1234
            0xFF, 0xFF,             // short -1
            0x00, 0x80,             // ushort 0x8000
            0x78, 0x56, 0x34, 0x12, // long 0x12345678
            0xFF, 0xFF, 0xFF, 0xFF, // long -1
            0x12, 0x34, 0x56, 0x78, // big long 0x12345678
        });
        Assert.Equal(254, r.ReadByte());
        Assert.Equal(-2, r.ReadChar());
        Assert.Equal(0x1234, r.ReadShort());
        Assert.Equal(-1, r.ReadShort());
        Assert.Equal(0x8000, r.ReadUShort());
        Assert.Equal(0x12345678, r.ReadLong());
        Assert.Equal(-1, r.ReadLong());
        Assert.Equal(0x12345678, r.ReadBigLong());
        Assert.False(r.BadRead);
        Assert.Equal(0, r.Remaining);
        Assert.Equal(20, r.Position);
    }

    [Fact]
    public void Reader_Float_Coord_And_Angles()
    {
        var bytes = new byte[4 + 2 + 2 + 1 + 2 + 2 + 1];
        BitConverter.GetBytes(1234.5f).CopyTo(bytes, 0);
        bytes[4] = 0x0C; bytes[5] = 0x00;        // coord13i: 12/8 = 1.5
        bytes[6] = 0xF8; bytes[7] = 0xFF;        // coord16i: -8
        bytes[8] = 0x40;                         // angle8i: 64 * 360/256 = 90
        bytes[9] = 0x00; bytes[10] = 0x40;       // angle16i: 16384 * 360/65536 = 90
        bytes[11] = 0x00; bytes[12] = 0xC0;      // angle16i: -16384 -> -90
        bytes[13] = 0xC0;                        // angle8i: -64 -> -90
        var r = new DpMessageReader(bytes);
        Assert.Equal(1234.5f, r.ReadCoord());
        Assert.Equal(1.5f, r.ReadCoord13i());
        Assert.Equal(-8f, r.ReadCoord16i());
        Assert.Equal(90f, r.ReadAngle8i());
        Assert.Equal(90f, r.ReadAngle());
        Assert.Equal(-90f, r.ReadAngle16i());
        Assert.Equal(-90f, r.ReadAngle8i());
        Assert.False(r.BadRead);
    }

    [Fact]
    public void Reader_Strings_Are_Nul_Terminated_Not_Length_Prefixed()
    {
        var r = new DpMessageReader(new byte[] { (byte)'h', (byte)'i', 0, 0, (byte)'x', (byte)'y', (byte)'z', 0, 7 });
        Assert.Equal("hi", r.ReadString());
        Assert.Equal("", r.ReadString());
        // Truncation keeps maxLength - 1 bytes but still consumes through the terminator.
        Assert.Equal("xy", r.ReadString(3));
        Assert.Equal(7, r.ReadByte());
        Assert.False(r.BadRead);
    }

    [Fact]
    public void Reader_String_Without_Terminator_Sets_BadRead()
    {
        var r = new DpMessageReader(new byte[] { (byte)'a', (byte)'b' });
        Assert.Equal("ab", r.ReadString());
        Assert.True(r.BadRead);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Reader_Utf8_And_Raw_Bytes()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("hé中");
        var data = new byte[utf8.Length + 1 + 3];
        utf8.CopyTo(data, 0);
        data[utf8.Length + 1] = 0x8E; // a Quake font character: not UTF-8
        data[utf8.Length + 2] = (byte)'A';
        var r = new DpMessageReader(data);
        Assert.Equal("hé中", r.ReadString());
        Assert.Equal(new byte[] { 0x8E, (byte)'A' }, r.ReadStringBytes().ToArray());
    }

    [Fact]
    public void Reader_Past_End_Returns_Minus_One_And_Never_Throws()
    {
        var r = new DpMessageReader(new byte[] { 1, 2, 3 });
        Assert.Equal(-1, r.ReadLong());      // 4 bytes wanted, 3 there: nothing consumed
        Assert.True(r.BadRead);
        Assert.Equal(0, r.Position);
        r.BadRead = false;
        Assert.Equal(0x0201, r.ReadShort());
        Assert.Equal(-1, r.ReadShort());
        Assert.Equal(65535, r.ReadUShort());
        Assert.Equal(3, r.ReadByte());
        Assert.Equal(-1, r.ReadByte());
        Assert.Equal(-1, r.ReadChar());
        Assert.Equal(-1f, r.ReadFloat());
        Assert.Equal(-1, r.ReadBigLong());
        Assert.Equal("", r.ReadString());
        Assert.True(r.BadRead);
    }

    [Fact]
    public void Reader_ReadBytes_And_ReadSpan_Stop_At_The_End()
    {
        var r = new DpMessageReader(new byte[] { 1, 2, 3, 4, 5 });
        Span<byte> two = stackalloc byte[2];
        Assert.Equal(2, r.ReadBytes(two));
        Assert.Equal(new byte[] { 1, 2 }, two.ToArray());
        Assert.Equal(new byte[] { 3, 4 }, r.ReadSpan(2).ToArray());
        Assert.False(r.BadRead);
        Assert.Equal(new byte[] { 5 }, r.ReadSpan(60000).ToArray());
        Assert.True(r.BadRead);
        Span<byte> more = stackalloc byte[4];
        Assert.Equal(0, r.ReadBytes(more));
    }

    [Fact]
    public void Reader_Honours_Offset_Window_And_Clamps_Position()
    {
        var r = new DpMessageReader(new byte[] { 9, 9, 1, 2, 9, 9 }, 2, 2);
        Assert.Equal(2, r.Length);
        Assert.Equal(1, r.ReadByte());
        r.Position = 500;
        Assert.Equal(2, r.Position);
        Assert.Equal(-1, r.ReadByte()); // does not read the 9 beyond the window
        r.Position = -5;
        Assert.Equal(0, r.Position);
        Assert.Equal(1, r.ReadByte());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DpMessageReader(new byte[4], 3, 2));
    }

    [Fact]
    public void Writer_Produces_The_Bytes_The_Reader_Expects()
    {
        var w = new DpMessageWriter();
        w.WriteByte(254);
        w.WriteChar(-2);
        w.WriteShort(-2);
        w.WriteLong(0x12345678);
        w.WriteBigLong(0x80010008);
        w.WriteFloat(-0.5f);
        w.WriteString("hi");
        w.WriteString(null);
        w.WriteString("a\0b");
        w.WriteVector(new Vector3(1, 2, 3));
        w.WriteBytes(new byte[] { 7, 8 });
        Assert.Equal(new byte[]
        {
            0xFE, 0xFE, 0xFE, 0xFF, 0x78, 0x56, 0x34, 0x12, 0x80, 0x01, 0x00, 0x08,
            0x00, 0x00, 0x00, 0xBF, (byte)'h', (byte)'i', 0, 0, (byte)'a', 0,
            0, 0, 0x80, 0x3F, 0, 0, 0, 0x40, 0, 0, 0x40, 0x40, 7, 8,
        }, w.ToArray());
    }

    [Theory]
    [InlineData(90f, 0x4000)]
    [InlineData(-90f, 0xC000)]
    [InlineData(180f, 0x8000)]
    [InlineData(360f, 0x0000)]
    [InlineData(0.002f, 0x0000)]  // below half a step
    [InlineData(0.003f, 0x0001)]  // above half a step: Q_rint rounds to nearest
    public void Writer_Angle16i_Rounds_To_Nearest_And_Wraps(float degrees, int expected)
    {
        var w = new DpMessageWriter();
        w.WriteAngle16i(degrees);
        byte[] b = w.ToArray();
        Assert.Equal(expected, b[0] | (b[1] << 8));
    }

    [Theory]
    [InlineData(0.5f, 1)]      // Q_rint rounds half away from zero, not to even
    [InlineData(-0.5f, -1)]
    [InlineData(2.5f, 3)]
    [InlineData(-320.4f, -320)]
    public void Writer_Coord16i_Uses_Q_Rint(float value, int expected)
    {
        var w = new DpMessageWriter();
        w.WriteCoord16i(value);
        Assert.Equal(expected, new DpMessageReader(w.ToArray()).ReadShort());
    }

    [Fact]
    public void Writer_Coord13i_And_Angle8i_Round_Trip()
    {
        var w = new DpMessageWriter();
        w.WriteCoord13i(-100.125f);
        w.WriteAngle8i(-90f);
        w.WriteAngle(45f);
        w.WriteCoord(3.25f);
        var r = new DpMessageReader(w.ToArray());
        Assert.Equal(-100.125f, r.ReadCoord13i());
        Assert.Equal(-90f, r.ReadAngle8i());
        Assert.Equal(45f, r.ReadAngle());
        Assert.Equal(3.25f, r.ReadCoord());
    }

    [Fact]
    public void Writer_Overflow_Drops_The_Write_And_Flags_It()
    {
        var w = new DpMessageWriter(maxSize: 6);
        w.WriteLong(1);
        Assert.False(w.Overflowed);
        w.WriteLong(2);           // would make 8
        Assert.True(w.Overflowed);
        Assert.Equal(4, w.Length); // nothing partial
        w.WriteString("toolong");
        Assert.Equal(4, w.Length);
        w.Clear();
        Assert.False(w.Overflowed);
        Assert.Equal(0, w.Length);
    }

    [Fact]
    public void Writer_Grows_Up_To_NetMaxMessage()
    {
        var w = new DpMessageWriter();
        w.WriteBytes(new byte[DpProtocol.NetMaxMessage]);
        Assert.False(w.Overflowed);
        w.WriteByte(1);
        Assert.True(w.Overflowed);
        Assert.Equal(DpProtocol.NetMaxMessage, w.Length);
    }

    // ---------------------------------------------------------------- CRC

    [Fact]
    public void Crc16_Matches_The_Published_Check_Value()
    {
        // CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, no final xor): "123456789" -> 0x29B1.
        Assert.Equal(0x29B1, Crc16.Block(Encoding.ASCII.GetBytes("123456789")));
        Assert.Equal(0xFFFF, Crc16.Block(ReadOnlySpan<byte>.Empty)); // CRC_INIT_VALUE ^ CRC_XOR_VALUE
    }

    [Theory] // entries copied from the crctable[] literal in Base/darkplaces/com_crc16.c
    [InlineData(0, 0x0000)]
    [InlineData(1, 0x1021)]
    [InlineData(7, 0x70e7)]
    [InlineData(8, 0x8108)]
    [InlineData(16, 0x1231)]
    [InlineData(127, 0x8f78)]
    [InlineData(128, 0x9188)]
    [InlineData(200, 0x5844)]
    [InlineData(248, 0x6e17)]
    [InlineData(255, 0x1ef0)]
    public void Crc16_Table_Matches_The_C_Source(int index, int expected)
    {
        Assert.Equal(expected, Crc16.TableEntry(index));
    }

    [Fact]
    public void Crc16_Single_Byte_Follows_The_C_Recurrence()
    {
        // crc = (crc << 8) ^ table[(crc >> 8) ^ byte] from 0xFFFF, with byte 0x00: (0xFF00) ^ table[0xFF].
        Assert.Equal((ushort)(0xFF00 ^ 0x1ef0), Crc16.Block(new byte[] { 0 }));
        Assert.Equal((ushort)(0xFF00 ^ Crc16.TableEntry(0xFF ^ 0x41)), Crc16.Block(new byte[] { 0x41 }));
    }
}
