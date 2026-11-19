using System.Text;

namespace VortexArena.Net;

/// <summary>The length-prefixed UTF-8 briefing at the end of the native handshake accept.</summary>
public static class WelcomeBriefing
{
    public const int MaxUtf8Bytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Write(BitWriter writer, string text)
    {
        int bytes = 0;
        int chars = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxUtf8Bytes)
                break;
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        writer.WriteString(text[..chars]);
    }

    public static string Read(ref BitReader reader)
    {
        // The span lets us discard oversized data before decoding or allocating a string.
        int length = reader.ReadUShort();
        ReadOnlySpan<byte> bytes = reader.ReadBytes(length);
        if (bytes.Length > MaxUtf8Bytes)
            return "";
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return "";
        }
    }
}
