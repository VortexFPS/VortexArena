using System.Globalization;
using System.Text;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Legacy.Protocol;

namespace VortexArena.RecordMigration;

public sealed record OriginalRecord(string Map, string Mode, string Kind, int? Rank,
    string? Value, string? LegacyUid, string? OriginalName, float? Amount);

public static class OriginalRecords
{
    private const int Buckets = 8192;
    private static readonly UTF8Encoding KeyEncoding = new(false, true);
    private static readonly string[] Modes = [RaceRecords.RaceRecord, RaceRecords.CtsRecord, RaceRecords.CtfRecord];

    public static IReadOnlyList<OriginalRecord> Read(byte[] bytes)
    {
        var db = ReadDatabase(bytes);
        var entries = new Dictionary<(string Map, string Mode, string Kind, int? Rank), (string? Value, string? Owner)>();

        foreach (var (key, value) in db)
        {
            int slash = key.IndexOf('/');
            if (slash <= 0) continue;
            string map = key[..slash];
            string suffix = key[slash..];
            if (suffix is "/captimerecord/time" or "/captimerecord/netname")
            {
                Add(map, RaceRecords.CtfRecord, "capture", null, suffix.EndsWith("/time", StringComparison.Ordinal), value);
                continue;
            }

            foreach (string mode in Modes)
            {
                if (!suffix.StartsWith(mode, StringComparison.Ordinal)) continue;
                string field = suffix[mode.Length..];
                if (field is "speed/speed" or "speed/crypto_idfp")
                    Add(map, mode, "speed", null, field == "speed/speed", value);
                else
                {
                    bool time = field.StartsWith("time", StringComparison.Ordinal);
                    string prefix = time ? "time" : "crypto_idfp";
                    if (field.StartsWith(prefix, StringComparison.Ordinal)
                        && int.TryParse(field.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int rank)
                        && rank is >= 1 and <= RaceRecords.RankingsCnt
                        && field == prefix + rank.ToString(CultureInfo.InvariantCulture))
                        Add(map, mode, "rank", rank, time, value);
                }
                break;
            }
        }

        var result = new List<OriginalRecord>();
        foreach (var (id, entry) in entries.OrderBy(e => e.Key.Map, StringComparer.Ordinal)
                     .ThenBy(e => e.Key.Mode, StringComparer.Ordinal).ThenBy(e => e.Key.Kind, StringComparer.Ordinal)
                     .ThenBy(e => e.Key.Rank))
        {
            string? owner = id.Kind == "capture" ? null : entry.Owner;
            string? name = id.Kind == "capture" ? entry.Owner : null;
            if (owner is { Length: > 0 })
            {
                if (!db.TryGetValue("/uid2name/" + owner, out name) || string.IsNullOrEmpty(name))
                    db.TryGetValue("uid2name" + owner, out name);
            }

            result.Add(new(id.Map, id.Mode, id.Kind, id.Rank, entry.Value, owner, name, ParseAmount(id.Kind, entry.Value)));
        }
        return result;

        void Add(string map, string mode, string kind, int? rank, bool isValue, string data)
        {
            var id = (map, mode, kind, rank);
            entries.TryGetValue(id, out var pair);
            entries[id] = isValue ? (data, pair.Owner) : (pair.Value, data);
        }
    }

    private static float? ParseAmount(string kind, string? value)
    {
        if (kind == "rank")
        {
            // QC stores the integer TIME_ENCODE value in a single-precision float.
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ticks)
                && ticks is > 0 and <= 16_777_216)
                return GameScores.TimeDecode(ticks);
            return null;
        }
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float amount)
               && float.IsFinite(amount) && amount > (kind == "capture" ? 0.01f : 0f) ? amount : null;
    }

    private static Dictionary<string, string> ReadDatabase(byte[] bytes)
    {
        // Latin-1 is a byte-preserving projection for parsing delimiters and CRC. Decode values after URI unescaping.
        using var reader = new StringReader(Encoding.Latin1.GetString(bytes));
        string? header = reader.ReadLine();
        if (header is not ("0" or "8192"))
            throw new FormatException("Expected the pinned DarkPlaces database header (0 or 8192).");
        if (bytes[^1] != (byte)'\n')
            throw new FormatException("The DarkPlaces database has an incomplete final line.");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        int bucket = 0;
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine(), bucket++)
        {
            if (header == "8192" && bucket >= Buckets)
                throw new FormatException("The DarkPlaces database exceeds 8192 bucket positions.");
            if (line.Length == 0) continue;
            string[] symbols = line.Split('\\');
            if (symbols.Length < 3 || symbols.Length % 2 == 0 || symbols[0].Length != 0 || line.Contains('\0'))
                throw new FormatException($"Malformed DarkPlaces database line {bucket + 2}.");

            for (int i = 1; i < symbols.Length; i += 2)
            {
                var keyBytes = Encoding.Latin1.GetBytes(symbols[i]);
                string key = KeyEncoding.GetString(keyBytes);
                if (key.Length == 0 || (header == "8192" && Crc16.Block(keyBytes) % Buckets != bucket))
                    throw new FormatException($"Invalid key or bucket on DarkPlaces database line {bucket + 2}.");
                if (!entries.TryAdd(key, Unescape(symbols[i + 1])))
                    throw new FormatException($"Duplicate DarkPlaces database key on line {bucket + 2}.");
            }
        }
        return entries;
    }

    private static string Unescape(string value)
    {
        byte[] data = Encoding.Latin1.GetBytes(value);
        int written = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == '%' && i + 2 < data.Length
                && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte decoded))
            {
                // DarkPlaces consumes %00 without inserting a NUL.
                if (decoded != 0) data[written++] = decoded;
                i += 2;
            }
            else data[written++] = data[i];
        }

        // Old/modded names may contain non-UTF8 bytes. The archive also carries the exact input for those names.
        return Encoding.UTF8.GetString(data, 0, written);
    }
}
