using VortexArena.Common.Menu;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

public sealed class MapVisualSuggestionsWireTests
{
    [Fact]
    public void RoundTripsEveryCombinationInOneByte()
    {
        bool?[] options = [null, false, true];
        var writer = new BitWriter();
        foreach (bool? bloom in options)
        foreach (bool? sky in options)
        foreach (bool? fog in options)
        {
            writer.Reset();
            var settings = new MapClientVisualSettings(bloom, sky, fog);
            MapVisualSuggestionsWire.Write(writer, settings);
            Assert.Equal(1, writer.WrittenSpan.Length);
            var reader = new BitReader(writer.WrittenSpan);
            Assert.Equal(settings, MapVisualSuggestionsWire.Read(ref reader));
            Assert.Equal(0, reader.Remaining);
        }
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01, 0x00 })]
    [InlineData(new byte[] { 0x40 })]
    [InlineData(new byte[] { 0x03 })]
    [InlineData(new byte[] { 0x0C })]
    [InlineData(new byte[] { 0x30 })]
    public void MissingOrInvalidTailLeavesAllSuggestionsUnset(byte[] payload)
    {
        var reader = new BitReader(payload);
        Assert.Equal(default, MapVisualSuggestionsWire.Read(ref reader));
        Assert.False(reader.BadRead);
    }
}
