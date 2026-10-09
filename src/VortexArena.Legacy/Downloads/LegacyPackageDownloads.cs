// Port of Base/darkplaces/libcurl.c as far as a client joining a server needs it: Curl_Curl_f (through
// DpCurlCommand), Curl_Begin's rules for a download to a file (the name under dlcache/, "already getting it",
// "already exists, not downloading", the URL scheme check, the insertion of the server's address into
// "http:///x"), the downloads list with curl_maxdownloads running at once (CheckPendingDownloads),
// Curl_EndDownload (the package is added to the search path, or the download counts as failed), the
// numdownloads_added / _success / _fail counters with Curl_Clear_forthismap, Curl_Have_forthismap,
// Curl_Register_predownload and Curl_CheckCommandWhenDone (the level goes on when the last download for it
// has ended, whether it worked or not), Curl_CancelAll, and Curl_GetDownloadInfo for the display.
//
// What differs from DarkPlaces, all of it stricter, because the commands and the addresses in them come
// from whoever runs the server:
//   - only "--pak" downloads are taken, and only to a plain *.pk3 / *.dpk name (no --cachepic, no
//     --skinframe, no download of an arbitrary file into dlcache/);
//   - http and https only (no ftp); redirects are followed at most MaxRedirects times and each target is
//     checked again; an address on this machine or a private network is refused unless the game server is
//     at one itself;
//   - a size limit per file and per connection, a limit on downloads per level, a stall timeout;
//   - a download is written under a temporary name, checked to be a readable zip archive within
//     LegacyPackLimits, and only then given its name and mounted: a transfer is never resumed, and a file
//     that is not a package never stays under dlcache/.
using System.Globalization;
using System.Net;
using VortexArena.Legacy.Protocol;

namespace VortexArena.Legacy.Downloads;

public sealed class LegacyDownloadLimits
{
    /// <summary>curl_enabled.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The largest package fetched over HTTP, in bytes.</summary>
    public long MaxFileBytes { get; set; } = 512L << 20;
    /// <summary>All packages fetched on one connection together, in bytes.</summary>
    public long MaxConnectionBytes { get; set; } = 2048L << 20;
    /// <summary>Downloads a server may ask for between two "curl --clear_autodownload" (one level).</summary>
    public int MaxDownloadsPerLevel { get; set; } = 32;
    /// <summary>curl_maxdownloads: transfers running at once.</summary>
    public int MaxConcurrent { get; set; } = 3;
    /// <summary>curl_maxspeed: KiB per second for one transfer, 0 for no limit. A server's "--maxspeed=" can lower it, never raise it.</summary>
    public double MaxKiBPerSecond { get; set; }
    public int MaxRedirects { get; set; } = 5;
    public double ConnectTimeoutSeconds { get; set; } = 15;
    public double StallTimeoutSeconds { get; set; } = 45;
    /// <summary>curl_useragent: what is sent as User-Agent; empty sends none.</summary>
    public string UserAgent { get; set; } = "";
    public LegacyPackLimits Pack { get; } = new();
}

/// <summary>One line of the download display (Curl_downloadinfo_t).</summary>
public readonly record struct LegacyDownloadInfo(string FileName, bool Queued, double Fraction, long Received, long Total, double BytesPerSecond, bool ForThisMap)
{
    /// <summary>The line cl_screen.c SCR_DrawCurlDownload draws for it.</summary>
    public string Text =>
        Queued ? "Still in queue: " + FileName
        : Fraction <= 0 ? string.Create(CultureInfo.InvariantCulture, $"Downloading {FileName} ...  ???.?% @ {BytesPerSecond / 1024.0:0.0} KiB/s")
        : string.Create(CultureInfo.InvariantCulture, $"Downloading {FileName} ...  {100.0 * Fraction,5:0.0}% @ {BytesPerSecond / 1024.0:0.0} KiB/s");
}

/// <summary>
/// The package downloads of one legacy session. Everything here runs on the session's thread except the
/// transfers themselves; <see cref="Update"/> is called once a frame and is where a finished transfer is
/// mounted and where the level is told to go on.
/// </summary>
public sealed class LegacyPackageDownloads : IDpPackageDownloads, IDisposable
{
    private sealed class Item
    {
        public required string Name;        // "nicemap.pk3"
        public required Uri Url;
        public bool ForThisMap;
        public double MaxKiBPerSecond;
        public long MaxBytes;
        public Task<string?>? Task;
        public readonly LegacyFetchProgress Progress = new();
        public readonly CancellationTokenSource Cancel = new();
        public long Began;
    }

    private readonly string _cache;
    private readonly LegacyDownloadLimits _limits;
    private readonly ILegacyPackageFetcher _fetcher;
    private readonly List<Item> _items = new();
    private readonly HashSet<string> _mounted = new(StringComparer.OrdinalIgnoreCase);
    // What this level's commands said each package is for, and why a package for it did not arrive.
    private readonly List<(string Name, IReadOnlyList<string> For)> _named = new();
    private readonly List<string> _failures = new();
    private int _added, _succeeded, _failed, _askedThisLevel;
    private bool _whenDone;
    private long _connectionBytes;
    private bool _disposed;

    public LegacyPackageDownloads(string cacheDirectory, LegacyDownloadLimits? limits = null, ILegacyPackageFetcher? fetcher = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectory);
        _cache = Path.GetFullPath(cacheDirectory);
        _limits = limits ?? new LegacyDownloadLimits();
        _fetcher = fetcher ?? new HttpPackageFetcher();
    }

    /// <summary>FS_FileExists over the session's game data (for "--for").</summary>
    public Func<string, bool> FileExists { get; set; } = _ => false;
    /// <summary>FS_AddPack: mounts a package (a file, or for an in-band map a directory) on the session's
    /// file system. Returns null, or why it could not be mounted.</summary>
    public Func<string, string?> MountPack { get; set; } = _ => "nothing can mount it";
    /// <summary>Con_Printf: the lines DarkPlaces prints about downloads.</summary>
    public Action<string> Print { get; set; } = _ => { };
    /// <summary>The game server's address as the player gave it: inserted into "http:///x" addresses and
    /// named in the Referer ("dp://host:port/").</summary>
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; } = 26000;
    /// <summary>Whether the game server is on this machine or a private network: only then may a download be.</summary>
    public bool ServerIsPrivate { get; set; }

    public LegacyDownloadLimits Limits => _limits;
    /// <summary>Packages mounted on this connection (downloaded now, or found in the cache).</summary>
    public int MountedCount => _mounted.Count;
    /// <summary>Packages fetched over HTTP on this connection, and found in the cache without a fetch.</summary>
    public int Fetched { get; private set; }
    public int FromCache { get; private set; }
    /// <summary>Why each failed download of the current level failed, in the order they ended.</summary>
    public IReadOnlyList<string> Failures => _failures;
    /// <summary>True while a transfer is running or queued (Curl_Running).</summary>
    public bool Running => _items.Count != 0;

    // ---- IDpPackageDownloads ---------------------------------------------------------------------------

    /// <summary>Curl_Have_forthismap: a download the current level waits for has been started and not yet accounted for.</summary>
    public bool HaveForThisMap => _added != 0;

    /// <summary>Curl_Register_predownload: when the downloads have ended, the level's loading goes on.</summary>
    public void RegisterPredownload() => _whenDone = true;

    public IReadOnlyList<string> PackagesFor(string file)
    {
        List<string> result = new();
        foreach ((string name, IReadOnlyList<string> wanted) in _named)
            if (!_mounted.Contains(name) && !result.Contains(name))
                foreach (string f in wanted)
                    if (string.Equals(f, file, StringComparison.OrdinalIgnoreCase)) { result.Add(name); break; }
        return result;
    }

    /// <summary>Curl_Curl_f. <paramref name="argv"/>[0] is "curl". <paramref name="loadBegun"/> is cl.loadbegun.</summary>
    public void Command(IReadOnlyList<string> argv, bool loadBegun)
    {
        if (_disposed) return;
        if (!_limits.Enabled)
        {
            Print("curl support not enabled. Set legacy_curl_enabled to 1 to enable.\n");
            return;
        }
        if (argv.Count < 2)
        {
            Print("usage:\ncurl --info, curl --cancel [filename], curl url\n");
            return;
        }
        DpCurlCommand command = DpCurlCommand.Parse(argv, FileExists);
        foreach (string option in command.InvalidOptions) Print($"curl: invalid option {LegacyPackValidator.Printable(option, 60)}\n");
        switch (command.Action)
        {
            case DpCurlAction.Info:
                PrintInfo();
                break;
            case DpCurlAction.Cancel:
                if (command.CancelAll) CancelAll();
                else if (_items.Find(i => "dlcache/" + i.Name == command.Url || i.Name == command.Url) is { } one) End(one, "the download was cancelled");
                else Print("download not found\n");
                break;
            case DpCurlAction.ClearAutodownload:
                // "mark all running downloads as not for this map, so if they fail, it does not matter"
                ClearForThisMap();
                _askedThisLevel = 0;
                _named.Clear();
                _failures.Clear();
                break;
            case DpCurlAction.FinishAutodownload:
                if (_added != 0)
                {
                    // DarkPlaces, once loading has begun, drops the connection and joins again when the downloads
                    // are done. Here the packages are mounted when they arrive and the connection stays.
                    if (loadBegun) Print("curl: packages asked for after the level began loading are mounted when they arrive\n");
                    RegisterPredownload();
                }
                break;
            case DpCurlAction.Download:
                Begin(command);
                break;
        }
    }

    // ---- Curl_Begin --------------------------------------------------------------------------------------

    private void Begin(DpCurlCommand command)
    {
        string urlText = InsertServerAddress(command.Url, ServerHost);
        string shown = LegacyPackValidator.Printable(CleanUrl(urlText), 200);
        if (command.LoadType != DpCurlLoadType.Pak)
        {
            Print($"curl: \"{shown}\" refused: only package downloads (--pak) are taken from a server\n");
            return;
        }

        // The name under dlcache/: "--as", else the last path component of the address without its query.
        string? rawName = command.As;
        if (rawName is null)
        {
            string path = urlText;
            int query = path.IndexOf('?');
            if (query >= 0) path = path[..query];
            rawName = path[(path.LastIndexOf('/') + 1)..];
        }
        if (LegacyPackValidator.SafePackageName(rawName) is not { } name)
        {
            Print($"curl: \"{LegacyPackValidator.Printable(rawName, 80)}\" refused: a download must be a plain file name ending in .pk3 or .dpk\n");
            return;
        }
        if (command.For.Count > 0) _named.Add((name, command.For));

        // already downloading the file?
        if (_items.Find(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            Print($"Can't download dlcache/{name}, already getting it from {LegacyPackValidator.Printable(CleanUrl(existing.Url.OriginalString), 200)}!\n");
            // however, if it was not for this map yet... this "fakes" a download attempt so the client will wait
            if (command.ForThisMap && !existing.ForThisMap)
            {
                existing.ForThisMap = true;
                _added++;
            }
            return;
        }

        string final = Path.Combine(_cache, name);
        if (File.Exists(final))
        {
            if (_mounted.Contains(name)) return;   // "(pak was already loaded)"
            string? invalid = LegacyPackValidator.Validate(final, _limits.Pack);
            string? mountError = invalid ?? MountPack(final);
            if (mountError is null)
            {
                _mounted.Add(name);
                FromCache++;
                Print($"dlcache/{name} already exists, not downloading!\n");
                if (command.ForThisMap)
                {
                    _added++;
                    _succeeded++;
                }
                return;
            }
            // "Detected non-PAK, clearing and NOT resuming."
            Print($"dlcache/{name} is in the download cache but {mountError}: it is fetched again\n");
            TryDelete(final);
        }

        // if we get here, we actually want to download... so first verify the URL scheme
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url) || HttpPackageFetcher.CheckUrl(url) is not null)
        {
            string why = url is null ? "not an address" : HttpPackageFetcher.CheckUrl(url)!;
            Print($"Curl_Begin(\"{shown}\"): nasty URL scheme rejected ({why})\n");
            return;
        }
        if (_askedThisLevel >= _limits.MaxDownloadsPerLevel)
        {
            Print($"curl: \"{shown}\" refused: the server has asked for more than {_limits.MaxDownloadsPerLevel} downloads for one level\n");
            return;
        }
        _askedThisLevel++;
        if (command.ForThisMap) _added++;
        double speed = _limits.MaxKiBPerSecond;
        if (command.MaxSpeed > 0 && (speed <= 0 || command.MaxSpeed < speed)) speed = command.MaxSpeed;
        _items.Add(new Item { Name = name, Url = url, ForThisMap = command.ForThisMap, MaxKiBPerSecond = speed });
    }

    // "if URL is protocol:///* or protocol://:port/*, insert the IP of the current server"
    internal static string InsertServerAddress(string url, string serverHost)
    {
        int colon = url.IndexOf(':');
        if (colon <= 0 || serverHost.Length == 0) return url;
        ReadOnlySpan<char> rest = url.AsSpan(colon);
        if (!rest.StartsWith(":///", StringComparison.Ordinal) && !rest.StartsWith("://:", StringComparison.Ordinal)) return url;
        string host = serverHost.Contains(':') && !serverHost.StartsWith('[') ? "[" + serverHost + "]" : serverHost;
        return string.Concat(url.AsSpan(0, colon), "://", host, url.AsSpan(colon + 3));
    }

    // CleanURL: "anything://user:password@rest" is shown as "anything://rest".
    private static string CleanUrl(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;
        int at = url.IndexOf('@', scheme + 3), slash = url.IndexOf('/', scheme + 3);
        return at >= 0 && (slash < 0 || at < slash) ? string.Concat(url.AsSpan(0, scheme + 3), url.AsSpan(at + 1)) : url;
    }

    // ---- Curl_Frame --------------------------------------------------------------------------------------

    /// <summary>
    /// Once a frame: finished transfers are mounted and counted, queued ones are started, and - as
    /// Curl_CheckCommandWhenDone - returns true exactly once when the last download the level waits for has
    /// ended and the level's loading was told to wait for it (<see cref="RegisterPredownload"/>): the caller
    /// then lets the signon go on. <see cref="Failures"/> says whether they all worked.
    /// </summary>
    public bool Update()
    {
        if (_disposed) return false;
        for (int i = 0; i < _items.Count; i++)
        {
            Item item = _items[i];
            if (item.Task is not { IsCompleted: true } task) continue;
            string? error;
            try { error = task.IsCompletedSuccessfully ? task.Result : "the download failed (" + (task.Exception?.GetBaseException().Message ?? "cancelled") + ")"; }
            catch (AggregateException e) { error = "the download failed (" + e.GetBaseException().Message + ")"; }
            _connectionBytes += item.Progress.Received;
            if (error is null)
            {
                error = MountPack(Path.Combine(_cache, item.Name));
                if (error is null)
                {
                    _mounted.Add(item.Name);
                    Fetched++;
                    double seconds = Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(item.Began).TotalSeconds);
                    Print(string.Create(CultureInfo.InvariantCulture,
                        $"Downloaded dlcache/{item.Name} ({item.Progress.Received} bytes in {seconds:0.0} s, {item.Progress.Received / seconds / 1024.0:0.0} KiB/s) and added it to the search path\n"));
                }
            }
            End(item, error);
            i--;
        }

        // CheckPendingDownloads
        int running = 0;
        foreach (Item item in _items)
            if (item.Task is not null) running++;
        foreach (Item item in _items)
        {
            if (running >= Math.Max(1, _limits.MaxConcurrent)) break;
            if (item.Task is not null) continue;
            long left = _limits.MaxConnectionBytes - _connectionBytes;
            if (left <= 0)
            {
                End(item, $"the downloads of this connection have reached {_limits.MaxConnectionBytes} bytes (the limit)");
                return Update();
            }
            item.MaxBytes = Math.Min(_limits.MaxFileBytes, left);
            Start(item);
            running++;
        }

        // Curl_CheckCommandWhenDone
        if (_added != 0 && _succeeded + _failed == _added)
        {
            bool go = _whenDone;
            ClearForThisMap();
            return go;
        }
        return false;
    }

    private void Start(Item item)
    {
        Print($"Downloading {LegacyPackValidator.Printable(CleanUrl(item.Url.OriginalString), 200)} -> dlcache/{item.Name}...\n");
        item.Began = System.Diagnostics.Stopwatch.GetTimestamp();
        string final = Path.Combine(_cache, item.Name);
        string part = final + ".part-" + Guid.NewGuid().ToString("N")[..8];
        LegacyFetchRequest request = new()
        {
            Url = item.Url, TargetPath = part, MaxBytes = item.MaxBytes, MaxBytesPerSecond = item.MaxKiBPerSecond * 1024.0,
            MaxRedirects = _limits.MaxRedirects, ConnectTimeoutSeconds = _limits.ConnectTimeoutSeconds, StallTimeoutSeconds = _limits.StallTimeoutSeconds,
            // "dp://serverhost:serverport/ so you can filter on this"
            Referer = ServerHost.Length == 0 ? "dp://notconnected.invalid/" : $"dp://{ServerHost}:{ServerPort}/",
            UserAgent = _limits.UserAgent, AllowPrivateHosts = ServerIsPrivate,
        };
        ILegacyPackageFetcher fetcher = _fetcher;
        LegacyPackLimits pack = _limits.Pack;
        LegacyFetchProgress progress = item.Progress;
        CancellationToken cancel = item.Cancel.Token;
        string cache = _cache;
        item.Task = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(cache);
                string? error = await fetcher.FetchAsync(request, progress, cancel).ConfigureAwait(false);
                if (error is null && LegacyPackValidator.Validate(part, pack) is { } invalid) error = "what arrived is not a usable package: " + invalid;
                if (error is null) File.Move(part, final, overwrite: true);
                else TryDelete(part);
                return error;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                TryDelete(part);
                return e is OperationCanceledException ? "the download was cancelled" : "the download could not be stored: " + e.Message;
            }
        });
    }

    // Curl_EndDownload's bookkeeping. A null error is success.
    private void End(Item item, string? error)
    {
        _items.Remove(item);
        if (item.Task is { IsCompleted: false }) item.Cancel.Cancel();
        if (error is not null)
        {
            string line = $"{item.Name}: {error} ({LegacyPackValidator.Printable(CleanUrl(item.Url.OriginalString), 160)})";
            _failures.Add(line);
            Print("Download of dlcache/" + line + ": FAILED\n");
        }
        if (!item.ForThisMap) return;
        if (error is null) _succeeded++;
        else _failed++;
    }

    // Curl_Clear_forthismap
    private void ClearForThisMap()
    {
        foreach (Item item in _items) item.ForThisMap = false;
        _whenDone = false;
        _added = _succeeded = _failed = 0;
    }

    /// <summary>Curl_CancelAll: every transfer is stopped and counts as failed.</summary>
    public void CancelAll()
    {
        foreach (Item item in _items.ToArray()) End(item, "the download was cancelled");
    }

    /// <summary>
    /// A file that arrived through the game connection instead (the in-band download): a package is checked
    /// and mounted like one fetched over HTTP; a map is stored under dlcache/inband/ in a directory of its
    /// own and that directory is mounted. Returns null, or why it was not taken.
    /// </summary>
    public string? AcceptInBand(string name, byte[] data, int crc)
    {
        if (_disposed) return "the session is over";
        try
        {
            Directory.CreateDirectory(_cache);
            if (LegacyPackValidator.SafePackageName(name) is { } package)
            {
                string final = Path.Combine(_cache, package), part = final + ".part-" + Guid.NewGuid().ToString("N")[..8];
                File.WriteAllBytes(part, data);
                if (LegacyPackValidator.Validate(part, _limits.Pack) is { } invalid)
                {
                    TryDelete(part);
                    return "what arrived is not a usable package: " + invalid;
                }
                File.Move(part, final, overwrite: true);
                string? error = MountPack(final);
                if (error is null) _mounted.Add(package);
                return error;
            }
            if (DpDownload.IsNastyPath(name) || !name.StartsWith("maps/", StringComparison.Ordinal) || !name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)
                || name.AsSpan(5).Contains('/') || name.Length > 80)
                return "only a package or a map is taken through the game connection";
            foreach (char c in name.AsSpan(5))
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+')) return "the map's name has characters a file name should not have";
            // "dlcache/%s.%i.%i" in DarkPlaces; here a directory per size and checksum, so that it can be mounted as it is.
            string root = Path.Combine(_cache, "inband", string.Create(CultureInfo.InvariantCulture, $"{data.Length}.{crc}"));
            string file = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, data);
            return MountPack(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "it could not be stored: " + e.Message; }
    }

    // ---- display -----------------------------------------------------------------------------------------

    /// <summary>Curl_GetDownloadInfo: one entry per transfer, running ones with their progress.</summary>
    public IReadOnlyList<LegacyDownloadInfo> Snapshot()
    {
        List<LegacyDownloadInfo> result = new(_items.Count);
        foreach (Item item in _items)
            result.Add(new LegacyDownloadInfo("dlcache/" + item.Name, item.Task is null,
                item.Progress.Fraction, item.Progress.Received, item.Progress.Total, item.Progress.BytesPerSecond, item.ForThisMap));
        return result;
    }

    /// <summary>Curl_GetDownloadInfo's additional_info: what happens when the downloads are done, or null.</summary>
    public string? AdditionalInfo => _whenDone && _failed == 0 && _added != 0 ? "(will enter the game when done)" : null;

    // Curl_Info_f
    private void PrintInfo()
    {
        if (_items.Count == 0)
        {
            Print("No downloads running.\n");
            return;
        }
        Print("Currently running downloads:\n");
        foreach (Item item in _items)
        {
            string head = $"  {LegacyPackValidator.Printable(CleanUrl(item.Url.OriginalString), 200)} -> dlcache/{item.Name} ";
            Print(item.Task is null ? head + "(queued)\n"
                : head + string.Create(CultureInfo.InvariantCulture, $"({100.0 * Math.Max(0, item.Progress.Fraction):0.0}% @ {item.Progress.BytesPerSecond / 1024.0:0.0} KiB/s)\n"));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Task> running = new();
        foreach (Item item in _items)
        {
            item.Cancel.Cancel();
            if (item.Task is { } task) running.Add(task);
        }
        _items.Clear();
        // The transfers remove their temporary files as they stop; not waited for longer than a moment.
        try { Task.WaitAll(running.ToArray(), 2000); }
        catch (AggregateException) { }
    }

    /// <summary>Whether a game server address counts as private for <see cref="ServerIsPrivate"/>.</summary>
    public static bool IsPrivateServer(IPAddress address) => HttpPackageFetcher.IsPrivateAddress(address);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
