// What libcurl.c Curl_EndDownload and Curl_Begin check of a downloaded package ("PK\x03\x04", then
// FS_AddPack succeeding), made strict: the file comes from an address a game server chose, so it is read
// as hostile input before anything is mounted from it.
using System.IO.Compression;

namespace VortexArena.Legacy.Downloads;

/// <summary>Bounds on a downloaded package's contents. The defaults are far above any real map package
/// (the largest stock Xonotic map package unpacks to about 80 MB) and far below what would hurt.</summary>
public sealed class LegacyPackLimits
{
    /// <summary>Files in one package.</summary>
    public int MaxEntries { get; set; } = 16384;
    /// <summary>One file, unpacked.</summary>
    public long MaxEntryBytes { get; set; } = 256L << 20;
    /// <summary>All files of one package, unpacked.</summary>
    public long MaxUnpackedBytes { get; set; } = 2048L << 20;
    /// <summary>A file larger than <see cref="RatioCheckBytes"/> may not claim to unpack to more than this many
    /// times its packed size. Deflate cannot do better than about 1032 to 1, and real content above a few
    /// megabytes never comes near it: a file that does is a decompression bomb.</summary>
    public int MaxCompressionRatio { get; set; } = 200;
    public long RatioCheckBytes { get; set; } = 8L << 20;
}

public static class LegacyPackValidator
{
    /// <summary>
    /// Null if <paramref name="path"/> is a zip archive that may be mounted, else the reason it may not.
    /// Only the archive's directory is read; no file in it is unpacked.
    /// </summary>
    public static string? Validate(string path, LegacyPackLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length < 22) return "it is not a package (too short to be a zip archive)";
            Span<byte> magic = stackalloc byte[4];
            stream.ReadExactly(magic);
            // "PK\x03\x04": a local file header. (DarkPlaces also takes Quake's "PACK"; this client reads zip only.)
            if (!(magic[0] == (byte)'P' && magic[1] == (byte)'K' && magic[2] == 3 && magic[3] == 4))
                return "it is not a package (no zip signature: the server's address answered with something else)";
            stream.Position = 0;
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > limits.MaxEntries) return $"it holds {archive.Entries.Count} files (the limit is {limits.MaxEntries})";
            long total = 0;
            int files = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName;
                if (name.Length == 0 || name[^1] == '/') continue;   // a directory
                if (IsNastyEntryName(name)) return $"it holds a file with a path that leaves the package (\"{Printable(name)}\")";
                long length = entry.Length, packed = entry.CompressedLength;
                if (length < 0 || length > limits.MaxEntryBytes) return $"\"{Printable(name)}\" in it unpacks to {length} bytes (the limit is {limits.MaxEntryBytes})";
                if (length > limits.RatioCheckBytes && (packed <= 0 || length / packed > limits.MaxCompressionRatio))
                    return $"\"{Printable(name)}\" in it claims to unpack {packed} bytes to {length} (a decompression bomb)";
                total += length;
                if (total > limits.MaxUnpackedBytes) return $"it unpacks to more than {limits.MaxUnpackedBytes} bytes";
                files++;
            }
            if (files == 0) return "it holds no files";
            return null;
        }
        catch (InvalidDataException e) { return "it is not a readable zip archive (" + e.Message + ")"; }
        catch (IOException e) { return "it could not be read (" + e.Message + ")"; }
        catch (UnauthorizedAccessException e) { return "it could not be read (" + e.Message + ")"; }
        catch (NotSupportedException e) { return "it is not a readable zip archive (" + e.Message + ")"; }
    }

    // FS_CheckNastyPath, for a name inside an archive: nothing in a package may name a place outside it.
    private static bool IsNastyEntryName(string name)
    {
        if (name.Length > 512 || name[0] == '/' || name[0] == '\\') return true;
        if (name.Contains(':') || name.Contains('\0')) return true;
        foreach (string part in name.Split('/', '\\'))
            if (part == "..") return true;
        return false;
    }

    /// <summary>
    /// The name a download may have under dlcache/: a plain file name ending in .pk3 or .dpk (the zip package
    /// kinds), with no directory, no leading dot and nothing a file system treats specially. Null if
    /// <paramref name="name"/> is not such a name.
    /// </summary>
    public static string? SafePackageName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 120) return null;
        if (name[0] == '.' || name[^1] is '.' or ' ') return null;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or '~' or '!' or '(' or ')' or '[' or ']' or '@' or '=' or ',')) return null;
        if (!name.EndsWith(".pk3", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".dpk", StringComparison.OrdinalIgnoreCase)) return null;
        string stem = name[..name.IndexOf('.')].ToUpperInvariant();
        // Device names of Windows: a file called so is the device.
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3])))
            return null;
        return name;
    }

    internal static string Printable(string text, int limit = 120)
    {
        System.Text.StringBuilder result = new(Math.Min(text.Length, limit));
        foreach (char c in text)
        {
            if (result.Length >= limit) { result.Append("..."); break; }
            result.Append(c < ' ' || c == 127 ? '?' : c);
        }
        return result.ToString();
    }
}
