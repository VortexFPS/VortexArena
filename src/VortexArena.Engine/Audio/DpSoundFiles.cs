// Port of Base/darkplaces/snd_wav.c GetWavinfo and S_LoadWavFile (the RIFF chunks, the "cue " loop start and
// the "LIST"/"mark" loop length), snd_ogg.c OGG_DecodeTags (loop and ReplayGain comments), snd_mem.c
// S_LoadSound (which files a sample name is looked for under) and cd_shared.c CDAudio_Play_byName (which
// files a music track is looked for under, and the remap list).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VortexArena.Engine.Audio;

public static class DpSoundFiles
{
    /// <summary>
    /// S_LoadSound: the files a sample name stands for, in the order DarkPlaces tries them. A name that does
    /// not begin with "sound/" is tried under it first; "foo.wav" is tried as foo.wav then foo.ogg, "foo.ogg"
    /// as itself only, and a name with neither extension as it stands.
    /// </summary>
    public static IEnumerable<string> Candidates(string name)
    {
        if (string.IsNullOrEmpty(name)) yield break;
        if (!name.StartsWith("sound/", StringComparison.OrdinalIgnoreCase))
            foreach (string c in WithExtensions("sound/" + name)) yield return c;
        foreach (string c in WithExtensions(name)) yield return c;
    }

    private static IEnumerable<string> WithExtensions(string path)
    {
        if (path.Length >= 4 && path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            yield return path;
            yield return path[..^3] + "ogg";
        }
        else yield return path;
    }

    /// <summary>
    /// CDAudio_Play_byName: the files a "cd play"/"cd loop" argument stands for. A number is first put
    /// through the remap list ("cd remap a b c": track 1 is a); a number still a number is trackNNN, else NN.
    /// </summary>
    public static IEnumerable<string> TrackCandidates(string track, IReadOnlyList<string> remap)
    {
        string name = track;
        if (IsNumber(name, out int number) && number > 0 && number <= remap.Count && remap[number - 1].Length > 0)
            name = remap[number - 1];
        if (IsNumber(name, out number))
        {
            if (number <= 0) yield break;
            yield return $"sound/cdtracks/track{number:000}.wav";
            yield return $"sound/cdtracks/track{number:000}.ogg";
            yield return $"music/track{number:000}.ogg";
            yield return $"music/cdtracks/track{number:000}.ogg";
            yield return $"sound/cdtracks/track{number:00}.wav";
            yield return $"sound/cdtracks/track{number:00}.ogg";
            yield return $"music/track{number:00}.ogg";
            yield return $"music/cdtracks/track{number:00}.ogg";
        }
        else
        {
            yield return name;
            yield return name + ".wav";
            yield return name + ".ogg";
            yield return "sound/" + name;
            yield return "sound/" + name + ".wav";
            yield return "sound/" + name + ".ogg";
            yield return "sound/cdtracks/" + name;
            yield return "sound/cdtracks/" + name + ".wav";
            yield return "sound/cdtracks/" + name + ".ogg";
            yield return "music/" + name + ".ogg";
            yield return "music/cdtracks/" + name + ".ogg";
        }
    }

    private static bool IsNumber(string text, out int number)
    {
        number = 0;
        if (text.Length is 0 or > 9) return false;
        foreach (char c in text)
            if (c is < '0' or > '9') return false;
        number = int.Parse(text, CultureInfo.InvariantCulture);
        return true;
    }

    // ---- WAV ---------------------------------------------------------------------------------------------

    /// <summary>
    /// S_LoadWavFile. PCM only, one or two channels, 8 or 16 bit. The loop start is the first cue point's
    /// sample offset; a LIST chunk right after the cue chunk whose "mark" entry gives a length cuts the
    /// sample there ("info.samples = info.loopstart + i"). Returns null for anything DarkPlaces refuses.
    /// </summary>
    public static DpSfx? LoadWav(string name, ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE")) return null;
        int end = wav.Length;
        // The RIFF chunk itself must fit ("truncated chunk!").
        int riffLength = BinaryPrimitives.ReadInt32LittleEndian(wav[4..]);
        if (riffLength < 0 || 8 + (long)riffLength > end) return null;
        const int first = 12;

        int fmt = FindChunk(wav, first, end, "fmt ", out _, out _);
        if (fmt < 0 || fmt + 24 > end) return null;
        if (BinaryPrimitives.ReadInt16LittleEndian(wav[(fmt + 8)..]) != 1) return null;
        int channels = BinaryPrimitives.ReadInt16LittleEndian(wav[(fmt + 10)..]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(wav[(fmt + 12)..]);
        int width = BinaryPrimitives.ReadInt16LittleEndian(wav[(fmt + 22)..]) / 8;
        if (channels < 1 || channels > 2 || width < 1 || width > 2) return null;

        int loopStart = -1, loopSamples = 0;
        int cue = FindChunk(wav, first, end, "cue ", out _, out int afterCue);
        if (cue >= 0 && cue + 36 <= end)
        {
            loopStart = BinaryPrimitives.ReadInt32LittleEndian(wav[(cue + 32)..]);
            // "if the next chunk is a LIST chunk, look for a cue length marker"
            int list = FindChunk(wav, afterCue, end, "LIST", out _, out _);
            if (list >= 0 && list + 32 <= end && Tag(wav, list + 28, "mark"))
                loopSamples = loopStart + BinaryPrimitives.ReadInt32LittleEndian(wav[(list + 24)..]);
        }

        int data = FindChunk(wav, first, end, "data", out int dataLength, out _);
        if (data < 0) return null;
        int samples = Math.Max(0, dataLength / width / channels);
        // "a negative loop length from the cue chunk would otherwise be trusted over the data chunk size"
        if (loopSamples != 0)
        {
            if (loopSamples < 0 || samples < loopSamples) loopSamples = samples;
        }
        else loopSamples = samples;
        samples = loopSamples;

        short[] pcm = new short[samples * channels];
        ReadOnlySpan<byte> body = wav.Slice(data + 8, samples * channels * width);
        if (width == 2)
            for (int i = 0; i < pcm.Length; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]);
        else
            // "convert unsigned byte sound data to signed bytes": the mixer reads it as value / 128.
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)((sbyte)(body[i] - 0x80) << 8);

        DpSfx sfx = new(name);
        // A negative loop start other than "none" would be a huge unsigned number in C: no loop either way.
        sfx.SetFormat(channels, rate, samples, loopStart < 0 ? -1 : loopStart, pcm);
        sfx.Publish(samples);
        return sfx;
    }

    private static bool Tag(ReadOnlySpan<byte> data, int at, string tag) =>
        at >= 0 && at + 4 <= data.Length && data[at] == tag[0] && data[at + 1] == tag[1] && data[at + 2] == tag[2] && data[at + 3] == tag[3];

    // FindNextChunk: the offset of the chunk's tag, or -1. A chunk that runs past the end stops the search.
    private static int FindChunk(ReadOnlySpan<byte> data, int from, int end, string name, out int length, out int next)
    {
        int at = from;
        length = 0;
        next = end;
        while (at >= 0 && at + 8 <= end)
        {
            length = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
            if (length < 0 || at + 8 + (long)length > end) return -1;
            next = at + 8 + ((length + 1) & ~1);
            if (Tag(data, at, name)) return at;
            at = next;
        }
        return -1;
    }

    // ---- Ogg Vorbis --------------------------------------------------------------------------------------

    /// <summary>What DarkPlaces reads from an Ogg Vorbis file before decoding it.</summary>
    public struct OggInfo
    {
        public int Channels, Rate;
        /// <summary>ov_pcm_total: sample frames in the file.</summary>
        public long TotalFrames;
        /// <summary>After OGG_DecodeTags: the length a loop tag cut the sample to, and where a loop returns to (-1 for none).</summary>
        public int Length, LoopStart;
        /// <summary>ReplayGain: 0 peak means no tags.</summary>
        public float VolumeMult, VolumePeak;
    }

    /// <summary>
    /// The identification header (channels, rate), the comment header (LOOP_START with LOOP_END or
    /// LOOP_LENGTH; LOOPSTART with LOOPLENGTH or LOOPEND; LOOPPOINT; REPLAYGAIN_TRACK_PEAK and _GAIN) and the
    /// last page's granule position of the first logical stream. Null if the file is not Ogg Vorbis.
    /// </summary>
    public static OggInfo? ReadOggInfo(ReadOnlySpan<byte> ogg)
    {
        List<byte[]> packets = new();
        int at = 0;
        uint serial = 0;
        bool haveSerial = false;
        List<byte> packet = new();
        while (packets.Count < 2 && at + 27 <= ogg.Length && Tag(ogg, at, "OggS"))
        {
            int segments = ogg[at + 26];
            if (at + 27 + segments > ogg.Length) return null;
            uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(ogg[(at + 14)..]);
            int body = at + 27 + segments, offset = body;
            bool mine = !haveSerial || pageSerial == serial;
            if (!haveSerial) { serial = pageSerial; haveSerial = true; }
            for (int i = 0; i < segments; i++)
            {
                int size = ogg[at + 27 + i];
                if (offset + size > ogg.Length) return null;
                if (mine)
                {
                    packet.AddRange(ogg.Slice(offset, size).ToArray());
                    if (size < 255)
                    {
                        if (packets.Count < 2) packets.Add(packet.ToArray());
                        packet.Clear();
                    }
                }
                offset += size;
            }
            at = offset;
        }
        if (packets.Count < 2) return null;
        byte[] id = packets[0], comment = packets[1];
        if (id.Length < 30 || id[0] != 1 || Encoding.ASCII.GetString(id, 1, 6) != "vorbis") return null;
        if (comment.Length < 15 || comment[0] != 3 || Encoding.ASCII.GetString(comment, 1, 6) != "vorbis") return null;
        OggInfo info = new() { Channels = id[11], Rate = BinaryPrimitives.ReadInt32LittleEndian(id.AsSpan(12)), LoopStart = -1 };
        if (info.Channels < 1 || info.Channels > 2 || info.Rate <= 0) return null;

        // The granule position of the stream's last page: scan back for the last page with this serial.
        for (int p = ogg.Length - 27; p >= 0; p--)
        {
            if (ogg[p] != (byte)'O' || !Tag(ogg, p, "OggS")) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(ogg[(p + 14)..]) != serial) continue;
            long granule = BinaryPrimitives.ReadInt64LittleEndian(ogg[(p + 6)..]);
            if (granule < 0) continue;
            info.TotalFrames = granule;
            break;
        }

        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        int c = 7;
        if (c + 4 <= comment.Length)
        {
            int vendor = BinaryPrimitives.ReadInt32LittleEndian(comment.AsSpan(c));
            c += 4 + Math.Max(0, vendor);
            if (c + 4 <= comment.Length)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(comment.AsSpan(c));
                c += 4;
                for (int i = 0; i < count && c + 4 <= comment.Length; i++)
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(comment.AsSpan(c));
                    c += 4;
                    if (length < 0 || c + length > comment.Length) break;
                    string text = Encoding.UTF8.GetString(comment, c, length);
                    c += length;
                    int eq = text.IndexOf('=');
                    if (eq > 0) tags.TryAdd(text[..eq], text[(eq + 1)..]);   // vorbis_comment_query(..., 0): the first of a tag
                }
            }
        }
        DecodeTags(tags, ref info);
        return info;
    }

    /// <summary>OGG_DecodeTags.</summary>
    public static void DecodeTags(IReadOnlyDictionary<string, string> tags, ref OggInfo info)
    {
        uint numSamples = (uint)Math.Min(info.TotalFrames, int.MaxValue);
        uint start = numSamples, length = numSamples;
        double peak = 0.0, gainDb = 0.0;
        if (tags.TryGetValue("REPLAYGAIN_TRACK_PEAK", out string? text)) peak = Atof(text);
        if (tags.TryGetValue("REPLAYGAIN_TRACK_GAIN", out text)) gainDb = Atof(text);

        string? startComment, endComment = null, lengthComment = null;
        if (tags.TryGetValue("LOOP_START", out startComment))   // "DarkPlaces, and some Japanese app"
        {
            if (!tags.TryGetValue("LOOP_END", out endComment)) tags.TryGetValue("LOOP_LENGTH", out lengthComment);
        }
        else if (tags.TryGetValue("LOOPSTART", out startComment))   // "RPG Maker VX"
        {
            if (!tags.TryGetValue("LOOPLENGTH", out lengthComment)) tags.TryGetValue("LOOPEND", out endComment);
        }
        else tags.TryGetValue("LOOPPOINT", out startComment);   // "Sonic Robo Blast 2"

        if (startComment is not null)
        {
            start = (uint)Math.Clamp(Atof(startComment), 0, numSamples);
            if (endComment is not null) length = (uint)Math.Clamp(Atof(endComment), 0, numSamples);
            else if (lengthComment is not null) length = (uint)Math.Clamp(start + Atof(lengthComment), 0, numSamples);
        }
        info.Length = (int)length;
        info.LoopStart = startComment is not null ? (int)start : -1;

        // "sfx->volume_mult = min(1.0f / peak, exp(gaindb * 0.05f * log(10.0f)))"
        if (peak != 0)
        {
            info.VolumeMult = (float)Math.Min(1.0f / peak, Math.Exp(gainDb * 0.05f * Math.Log(10.0f)));
            info.VolumePeak = (float)peak;
        }
        else if (gainDb != 0)
        {
            info.VolumeMult = (float)Math.Exp(gainDb * 0.05f * Math.Log(10.0f));
            info.VolumePeak = 1.0f;   // "if peak is not defined, we won't trust it"
        }
        else
        {
            info.VolumeMult = 1f;
            info.VolumePeak = 0f;
        }
    }

    // C's atof: the longest leading number, 0 if there is none.
    private static double Atof(string text)
    {
        int i = 0, n = text.Length;
        while (i < n && char.IsWhiteSpace(text[i])) i++;
        int from = i;
        if (i < n && text[i] is '+' or '-') i++;
        while (i < n && char.IsAsciiDigit(text[i])) i++;
        if (i < n && text[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(text[i])) i++;
        }
        if (i < n && text[i] is 'e' or 'E')
        {
            int mark = i++;
            if (i < n && text[i] is '+' or '-') i++;
            if (i < n && char.IsAsciiDigit(text[i]))
                while (i < n && char.IsAsciiDigit(text[i])) i++;
            else i = mark;
        }
        return double.TryParse(text.AsSpan(from, i - from), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
    }
}
