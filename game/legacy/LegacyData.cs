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

    public const string CurlEnabledCvar = "legacy_curl_enabled";
    public const string CurlMaxSizeCvar = "legacy_curl_maxsize";
    public const string CurlMaxSpeedCvar = "legacy_curl_maxspeed";
    public const string CurlTimeoutCvar = "legacy_curl_timeout";
    public const string InBandCvar = "legacy_download_inband";

    /// <summary>The limits for a session's package downloads, from the player's own settings.</summary>
    public static VortexArena.Legacy.Downloads.LegacyDownloadLimits DownloadLimits(CvarService? player)
    {
        VortexArena.Legacy.Downloads.LegacyDownloadLimits limits = new() { UserAgent = "VortexArena (legacy compatibility; DarkPlaces protocol)" };
        if (player is null) return limits;
        limits.Enabled = !player.Has(CurlEnabledCvar) || player.GetFloat(CurlEnabledCvar) != 0;
        float size = player.GetFloat(CurlMaxSizeCvar);
        if (float.IsFinite(size) && size >= 1)
        {
            limits.MaxFileBytes = (long)Math.Min(size, 16384) << 20;
            limits.MaxConnectionBytes = limits.MaxFileBytes * 4;
        }
        float speed = player.GetFloat(CurlMaxSpeedCvar);
        limits.MaxKiBPerSecond = float.IsFinite(speed) && speed > 0 ? speed : 0;
        float timeout = player.GetFloat(CurlTimeoutCvar);
        if (float.IsFinite(timeout) && timeout >= 1) limits.StallTimeoutSeconds = Math.Min(timeout, 600);
        return limits;
    }

    /// <summary>
    /// Block-compressed textures made while packages a server had this client download are mounted go to a
    /// cache of their own, named after exactly that set of packages: the cache is keyed by a texture's name
    /// alone, and one server's "textures/foo" must not be what the next server's, or Xonotic's own, is read
    /// back as. The root is mounted on the session's file system and becomes where its asset system writes.
    /// </summary>
    public static void UseDownloadTextureCache(VortexArena.Formats.Vfs.VirtualFileSystem files, VortexArena.Game.Loaders.AssetSystem assets, string packageSetKey)
    {
        try
        {
            string root = Path.Combine(UserRoot, "texcache-dl", packageSetKey);
            Directory.CreateDirectory(root);
            if (files.Mount(root)) assets.DdsCacheRoot = root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    /// <summary><c>--legacy-data &lt;dir&gt;</c>: overrides the cvar for this run without being saved into the player's configuration.</summary>
    public static string? CommandLineDataDir { get; set; }

    /// <summary>
    /// <c>--legacy-extra-data &lt;dir&gt;</c> (or the environment variable <c>VORTEX_LEGACY_EXTRA_DATA</c>): a
    /// directory of further packages mounted on a session's file system over the Xonotic data, as if they had
    /// been in its data folder - a server's downloads kept somewhere else, for playing a recording made on
    /// that server or for looking at its maps. Command line and environment only: nothing a server sends can
    /// name a directory. Returns a line for the log, or null when nothing was asked for.
    /// </summary>
    public static string? MountExtraData(VortexArena.Formats.Vfs.VirtualFileSystem files)
    {
        string? directory = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_EXTRA_DATA");
        string[] args = Godot.OS.GetCmdlineArgs(), userArgs = Godot.OS.GetCmdlineUserArgs();
        foreach (string[] list in new[] { args, userArgs })
        {
            int at = Array.IndexOf(list, "--legacy-extra-data");
            if (at >= 0 && at + 1 < list.Length) directory = list[at + 1];
        }
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            string full = Path.GetFullPath(directory.Trim());
            foreach (string mounted in files.MountedPaths)
                if (string.Equals(mounted, full, StringComparison.OrdinalIgnoreCase)) return null;
            return files.MountGameDir(full)
                ? $"extra game data mounted from \"{full}\" (--legacy-extra-data)"
                : $"--legacy-extra-data: \"{full}\" does not exist; nothing was mounted";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"--legacy-extra-data: \"{directory}\" could not be mounted ({e.Message})";
        }
    }

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
        // Package downloads a server asks for ("curl --pak"). These live in the PLAYER's store, where nothing a
        // server sends can change them (a session has its own store for what the server sets).
        cvars.Register(CurlEnabledCvar, "1", CvarFlags.Save,
            "download the packages (maps, server packages) a Xonotic server names when joining it; 0 refuses them, and a server whose map is missing cannot be joined");
        cvars.Register(CurlMaxSizeCvar, "512", CvarFlags.Save,
            "largest package a Xonotic server may have this client download, in MiB (all packages of one connection together: four times this)");
        cvars.Register(CurlMaxSpeedCvar, "0", CvarFlags.Save,
            "download speed limit for packages a Xonotic server names, in KiB per second; 0 is no limit (DarkPlaces: curl_maxspeed)");
        cvars.Register(CurlTimeoutCvar, "45", CvarFlags.Save,
            "seconds without any data after which a package download from a Xonotic server is given up");
        cvars.Register(InBandCvar, "1", CvarFlags.Save,
            "ask a Xonotic server for a missing map through the game connection when no package download delivered it");
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

    /// <summary>
    /// One line of what the process holds right now, for the review scripts' "mem" and the session log: the
    /// working set, the managed heap, and Godot's own counters (static memory, video memory in textures and in
    /// vertex/index buffers, objects, nodes, resources). The numbers that have to come back down when a level
    /// is left.
    /// </summary>
    public static string MemoryReport()
    {
        const double mb = 1024.0 * 1024.0;
        long workingSet;
        int threads;
        using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
        {
            process.Refresh();
            workingSet = process.WorkingSet64;
            threads = process.Threads.Count;
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"working set {workingSet / mb:0} MB, private {PrivateBytes() / mb:0} MB, managed {GC.GetTotalMemory(false) / mb:0} MB (committed {GC.GetGCMemoryInfo().TotalCommittedBytes / mb:0} MB), " +
            $"static {Godot.Performance.GetMonitor(Godot.Performance.Monitor.MemoryStatic) / mb:0} MB, " +
            $"textures {Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderTextureMemUsed) / mb:0} MB, " +
            $"buffers {Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderBufferMemUsed) / mb:0} MB, " +
            $"video {Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderVideoMemUsed) / mb:0} MB, " +
            $"objects {Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectCount):0}, nodes {Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectNodeCount):0}, " +
            $"resources {Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectResourceCount):0}, orphan nodes {Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectOrphanNodeCount):0}, " +
            $"threads {threads} (legacy server {VortexArena.Legacy.Local.LegacyLocalServer.LiveThreads}, legacy precache {GodotLegacyPresentation.LivePrecacheWorkers})")
            + (LegacyMemoryMap.Enabled ? "\n[legacy] " + LegacyMemoryMap.Report() + (LevelReport is { } level ? "\n[legacy] memmap level: " + level() : "") : "");
    }

    /// <summary>Set by a session's presentation while its level is loaded: one more line for the memory report
    /// (what the level's textures are). Read only under VORTEX_LEGACY_MEMMAP.</summary>
    public static Func<string>? LevelReport { get; set; }

    private static long PrivateBytes()
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
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

    /// <summary>
    /// The block-compressed copies of Xonotic's textures (r_texture_dds_save), kept apart from the native
    /// game's own cache: <c>&lt;user directory&gt;/legacy/texcache</c>. A legacy session mounts this on its file
    /// system (<see cref="MountTextureCache"/>) and points its asset system at it, so a texture is encoded
    /// once and read back as blocks on every later load.
    /// </summary>
    public static string TextureCacheRoot
    {
        get
        {
            string root = Path.Combine(UserRoot, "texcache");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>Mounts <see cref="TextureCacheRoot"/> on a legacy file system (once) and gives it to the asset
    /// system that reads through that file system.</summary>
    public static void MountTextureCache(VortexArena.Formats.Vfs.VirtualFileSystem files, VortexArena.Game.Loaders.AssetSystem assets)
    {
        try
        {
            string root = TextureCacheRoot;
            bool mounted = false;
            foreach (string path in files.MountedPaths)
                if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) { mounted = true; break; }
            if (mounted || files.Mount(root)) assets.DdsCacheRoot = root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
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
