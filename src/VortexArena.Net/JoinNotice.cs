using System.Text;

namespace VortexArena.Net;

/// <summary>One display interval for the server's pipe-separated welcome messages.</summary>
public readonly record struct JoinNotice(int Seconds, string Text)
{
    public const int MaxUtf8Bytes = 8192;

    public static JoinNotice? Create(float seconds, string text)
    {
        if (!float.IsFinite(seconds) || seconds < 1f || text.Length == 0)
            return null;

        if (Encoding.UTF8.GetByteCount(text) > MaxUtf8Bytes)
        {
            int bytes = 0;
            int chars = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                bytes += rune.Utf8SequenceLength;
                if (bytes > MaxUtf8Bytes)
                    break;
                chars += rune.Utf16SequenceLength;
            }
            text = text[..chars];
        }
        return new JoinNotice((int)System.Math.Min((double)seconds, int.MaxValue), text);
    }

    public string[] Lines() => Text.Split('|');

    public void Write(BitWriter writer)
    {
        writer.WriteLong(Seconds);
        writer.WriteString(Text);
    }

    public static JoinNotice? Read(ref BitReader reader)
    {
        int seconds = reader.ReadLong();
        string text = reader.ReadString();
        return seconds > 0 && text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= MaxUtf8Bytes ? new(seconds, text) : null;
    }
}

/// <summary>A received notice whose lifetime runs on the accepted client clock, including during loading.</summary>
public readonly record struct TimedJoinNotice(JoinNotice Notice, double? ExpiresAt = null)
{
    public TimedJoinNotice WithClock(float time) => ExpiresAt is null && time > 0f && float.IsFinite(time)
        ? this with { ExpiresAt = (double)time + Notice.Seconds }
        : this;

    public int RemainingSeconds(float time) => ExpiresAt is double expiry
        ? (int)System.Math.Clamp(System.Math.Ceiling(expiry - time), 0d, Notice.Seconds)
        : Notice.Seconds;
}
