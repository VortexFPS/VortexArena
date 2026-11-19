using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

public sealed class WelcomeBriefingTests
{
    [Fact]
    public void ByteLimitKeepsWholeUtf8Scalars()
    {
        string prefix = new('é', (WelcomeBriefing.MaxUtf8Bytes - 4) / 2);
        var writer = new BitWriter();
        WelcomeBriefing.Write(writer, prefix + "🎮more");
        var reader = new BitReader(writer.WrittenSpan);

        Assert.Equal(WelcomeBriefing.MaxUtf8Bytes + sizeof(ushort), writer.WrittenSpan.Length);
        Assert.Equal(prefix + "🎮", WelcomeBriefing.Read(ref reader));
        Assert.Equal(0, reader.Remaining);
        Assert.False(reader.BadRead);

        writer.Reset();
        WelcomeBriefing.Write(writer, prefix + "é🎮");
        reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(prefix + "é", WelcomeBriefing.Read(ref reader));
        Assert.False(reader.BadRead);
    }

    [Fact]
    public void DiscardsOversizedOrInvalidTextButDistinguishesTruncation()
    {
        var writer = new BitWriter();
        writer.WriteString(new string('é', WelcomeBriefing.MaxUtf8Bytes));
        var reader = new BitReader(writer.WrittenSpan);
        Assert.Empty(WelcomeBriefing.Read(ref reader));
        Assert.Equal(0, reader.Remaining);
        Assert.False(reader.BadRead);

        writer.Reset();
        writer.WriteUShort(2);
        writer.WriteByte(0xC3);
        writer.WriteByte(0x28);
        reader = new BitReader(writer.WrittenSpan);
        Assert.Empty(WelcomeBriefing.Read(ref reader));
        Assert.False(reader.BadRead);

        reader = new BitReader(writer.WrittenSpan[..^1]);
        Assert.Empty(WelcomeBriefing.Read(ref reader));
        Assert.True(reader.BadRead);
    }
}
