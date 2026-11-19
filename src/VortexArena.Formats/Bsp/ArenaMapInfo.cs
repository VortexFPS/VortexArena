using VortexArena.Common.Menu;
using VortexArena.Formats.Vfs;

namespace VortexArena.Formats.Bsp;

internal static class ArenaMapInfo
{
    private static readonly HashSet<string> KnownModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "dm", "duel", "tdm", "ctf", "ca", "freezetag", "mayhem", "tmayhem", "ka", "tka",
        "kh", "lms", "dom", "nb", "ons", "as", "surv", "rc", "cts", "inv",
    };

    public static bool Read(VirtualFileSystem vfs, MapInfo info, string bspPath)
    {
        bool found = ReadExtension(".arena", false);
        found |= ReadExtension(".defi", true);
        info.HasArenaMetadata = found;
        return found;

        bool ReadExtension(string extension, bool isDefi)
        {
            string? text = Find(vfs, bspPath, info.BspName, extension);
            if (text is null || ParseMatchingBlock(text, info.BspName, isDefi) is not { } block)
                return false;
            if (block.Title.Length != 0)
                info.Title = block.Title;
            if (block.Author.Length != 0)
                info.Author = block.Author;
            info.Description = block.Description;
            info.Features |= block.Features;
            info.SupportedGametypes.UnionWith(block.SupportedGametypes);
            return true;
        }
    }

    public static bool HasCompanion(VirtualFileSystem vfs, string bspPath)
    {
        string map = Path.GetFileNameWithoutExtension(bspPath);
        return Find(vfs, bspPath, map, ".arena") is not null || Find(vfs, bspPath, map, ".defi") is not null;
    }

    private static string? Find(VirtualFileSystem vfs, string bspPath, string map, string extension)
    {
        string named = $"scripts/{map}{extension}";
        string reference = $"\"{map}\"";
        foreach (string path in vfs.FindInAssetPack(bspPath, "scripts/", extension).Prepend(named))
        {
            if (!vfs.Exists(path))
                continue;
            string text;
            try { text = vfs.ReadText(path); }
            catch (AssetParseException) { continue; }
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } raw)
            {
                string line = raw.Replace("\t", "", StringComparison.Ordinal).TrimStart(' ');
                if (line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // _MapInfo_CheckArenaFile is a lexical discovery pass. A named candidate
                // wins here even if its matching text later proves to be an inline comment.
                int start = line.IndexOf("map", StringComparison.Ordinal);
                if (start >= 0 && line.AsSpan(start).Contains(reference, StringComparison.OrdinalIgnoreCase))
                    return text;
            }
        }
        return null;
    }

    // The pinned parser is line-oriented. Each block is staged until its matching closing brace.
    private static MapInfo? ParseMatchingBlock(string text, string name, bool isDefi)
    {
        using var reader = new StringReader(text);
        MapInfo? block = null;
        bool matches = false;
        while (reader.ReadLine() is { } raw)
        {
            // QC checks braces on the raw line, before trimming trailing comments or quotes.
            if (raw.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (raw.Contains('{'))
            {
                if (block is not null)
                    return null;
                block = new MapInfo();
                matches = false;
                continue;
            }
            if (block is null)
                continue;
            if (raw.Contains('}'))
            {
                if (matches)
                    return block;
                block = null;
                continue;
            }
            int comment = raw.IndexOf("//", StringComparison.Ordinal);
            string line = (comment >= 0 ? raw[..comment] : raw).Trim();
            int split = line.IndexOfAny([' ', '\t']);
            if (split < 0)
                continue;
            string value = line[(split + 1)..].Trim();
            bool quoted = value.Length >= 2 && value[0] == '"' && value[^1] == '"';
            if (quoted)
                value = value[1..^1];

            switch (line[..split].ToLowerInvariant())
            {
                case "map":
                    matches |= value.Equals(name, StringComparison.OrdinalIgnoreCase);
                    break;
                case "longname": block.Title = value; break;
                case "author": block.Author = value; break;
                case "quote": block.Description = value; break;
                case "style" when isDefi: block.SupportedGametypes.Add("cts"); break;
                case "type":
                    block.Features |= MapFeatures.Weapons;
                    string types = value.Replace("team", "tdm ft", StringComparison.Ordinal)
                        .Replace("ffa", "dm lms ka", StringComparison.Ordinal)
                        .Replace("tourney", "duel", StringComparison.Ordinal);
                    if (!types.Contains("duel", StringComparison.Ordinal) && types.Contains("tdm", StringComparison.Ordinal))
                        types += " ca kh";
                    foreach (string token in types.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        if (MapInfoBackend.ResolveGametype(token) is { } mode && KnownModes.Contains(mode))
                            block.SupportedGametypes.Add(mode.ToLowerInvariant());
                    break;
            }
        }
        return null;
    }
}
