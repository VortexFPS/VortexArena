// Port of Base/darkplaces/cl_parse.c CL_BeginDownloads (the "dlcache/csprogs.dat.SIZE.CRC" lookup that
// keeps a client from downloading a client program twice) and CL_ParseDownload (where a finished
// download is written to that name), and of fs.c FS_CheckNastyPath for the names that go into it.
using System;
using System.Globalization;
using System.IO;
using VortexArena.Common.Config;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Game.Legacy;

/// <summary>
/// Where a legacy session (planning/specs/legacy-compat.md) gets Xonotic's own game data, and where it
/// may write.
///
/// A stock Xonotic server expects the client to have Xonotic's data: its default configuration, its
/// models, sounds and maps. Whether Vortex ships a copy or borrows the player's Xonotic install is an
/// open decision (spec section 11); what is here is the mechanism, with the policy left in one cvar:
/// <c>legacy_xonotic_data</c> names a Xonotic <c>data</c> directory, and nothing joins a Xonotic server
/// until it is set.
///
/// Everything a session writes goes under <c>&lt;user directory&gt;/legacy/</c>, never beside the
/// player's own configuration.
/// </summary>
public static class LegacyData
{
    /// <summary>The cvar naming a Xonotic "data" directory (the one holding xonotic-*.pk3 or xonotic-data.pk3dir).</summary>
    public const string DataCvar = "legacy_xonotic_data";
    /// <summary>1 prints the once-a-second session status line to the log (always on in a headless run).</summary>
    public const string StatusCvar = "legacy_status";
    /// <summary>1 downloads the server's client program even when a matching copy is at hand.</summary>
    public const string ForceDownloadCvar = "legacy_csprogs_download";
    /// <summary>Seconds after entering the game at which "join" is sent; 0 never. A hook for unattended runs.</summary>
    public const string AutoJoinCvar = "legacy_autojoin";

    /// <summary><c>--legacy-data &lt;dir&gt;</c>: overrides the cvar for this run without being saved into the player's configuration.</summary>
    public static string? CommandLineDataDir { get; set; }

    /// <summary>Registers the cvars in the player's own store. They are the only legacy settings that live there.</summary>
    public static void RegisterCvars(CvarService cvars)
    {
        cvars.Register(DataCvar, "", CvarFlags.Save,
            "directory holding Xonotic's game data (the \"data\" folder of a Xonotic install); required to join stock Xonotic servers");
        cvars.Register(StatusCvar, "0", CvarFlags.None,
            "print a once-a-second status line while connected to a Xonotic server (always on in a headless run)");
        cvars.Register(ForceDownloadCvar, "0", CvarFlags.None,
            "download the server's client program (csprogs.dat) even when the game data or the download cache has it");
        cvars.Register(AutoJoinCvar, "0", CvarFlags.None,
            "seconds after entering a Xonotic server at which to send \"join\" (leave the spectators); 0 never. For unattended test runs");
        // Local games (the server program in this process): the LAN switch and where the server runs.
        LegacyLocalGames.RegisterCvars(cvars);
    }

    /// <summary>The directory as configured (the command line wins), or empty.</summary>
    public static string ConfiguredDir(CvarService cvars) =>
        !string.IsNullOrWhiteSpace(CommandLineDataDir) ? CommandLineDataDir!.Trim() : cvars.GetString(DataCvar).Trim();

    /// <summary>Whether a data directory is configured and exists: what decides if the browser offers the legacy path.</summary>
    public static bool IsConfigured(CvarService cvars) => TryResolve(cvars, out _, out _);

    /// <summary>What to tell a player who has not set the directory up. One sentence, naming the setting.</summary>
    public const string SetupHint =
        "Set the cvar legacy_xonotic_data to the \"data\" folder of a Xonotic install (or start with --legacy-data <folder>).";

    /// <summary>
    /// The configured data directory as an absolute path, or the reason there is none.
    /// </summary>
    public static bool TryResolve(CvarService cvars, out string directory, out string problem)
    {
        directory = "";
        problem = "";
        string configured = ConfiguredDir(cvars);
        if (configured.Length == 0)
        {
            problem = "No Xonotic game data is configured. " + SetupHint;
            return false;
        }
        string full;
        try { full = Path.GetFullPath(configured); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"legacy_xonotic_data \"{configured}\" is not a usable path. " + SetupHint;
            return false;
        }
        if (!Directory.Exists(full))
        {
            problem = $"The Xonotic data folder \"{full}\" does not exist. " + SetupHint;
            return false;
        }
        directory = full;
        return true;
    }

    /// <summary>The root of everything a legacy session may write: <c>&lt;user directory&gt;/legacy</c>.</summary>
    public static string UserRoot
    {
        get
        {
            string root = Path.Combine(UserPaths.BaseDir, "legacy");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>Where the client program's own files go (its fopen for writing): DarkPlaces' user directory + data/.</summary>
    public static string WriteRoot
    {
        get
        {
            string root = Path.Combine(UserRoot, "data");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>Verified downloads: <c>&lt;user directory&gt;/legacy/dlcache</c>.</summary>
    public static string DownloadCache
    {
        get
        {
            string root = Path.Combine(UserRoot, "dlcache");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    // "dlcache/%s.%i.%i": the name with its size and CRC appended. A name with a directory in it, or
    // anything else a file name should not have, gets no cache entry at all.
    private static string? CacheFileName(string name, int size, int crc)
    {
        if (name.Length is 0 or > 64 || size < 0 || crc < 0 || name[0] == '.') return null;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return null;
        return string.Create(CultureInfo.InvariantCulture, $"{name}.{size}.{crc}");
    }

    /// <summary>
    /// A cached client program of that name, size and CRC: from this client's own cache, else from a
    /// <c>dlcache</c> folder beside the Xonotic data (a Xonotic install's own downloads). The caller
    /// verifies the bytes; this only finds them. Null if there is none.
    /// </summary>
    public static byte[]? ReadCachedProgram(string? dataDirectory, string name, int size, int crc)
    {
        if (CacheFileName(name, size, crc) is not { } file) return null;
        foreach (string? root in new[] { DownloadCache, dataDirectory is null ? null : Path.Combine(dataDirectory, "dlcache") })
        {
            if (root is null) continue;
            string path = Path.Combine(root, file);
            try
            {
                FileInfo info = new(path);
                // The size is in the name; a file of another length is not the one asked for.
                if (info.Exists && info.Length == size) return File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Stores a verified download. Written under a temporary name first, so a crash cannot leave half a program behind.</summary>
    public static void WriteCachedProgram(string name, int size, int crc, byte[] data)
    {
        if (CacheFileName(name, size, crc) is not { } file || data.Length != size) return;
        string path = Path.Combine(DownloadCache, file);
        if (File.Exists(path)) return;
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, overwrite: true);
    }
}
