// Port of Base/darkplaces/libcurl.c Curl_Curl_f: the argument loop of the "curl" console command, which a
// DarkPlaces server stuffs into a connecting client to make it fetch the packages it lacks
// (Curl_SendRequirements):
//
//     curl --clear_autodownload
//     curl --pak --forthismap --as nicemap.pk3 --for maps/nicemap.bsp http://host/dir/nicemap.pk3
//     curl --finish_autodownload
namespace VortexArena.Legacy.Downloads;

public enum DpCurlAction
{
    /// <summary>Nothing to do: no arguments, or "--for" named only files the client already has.</summary>
    None,
    /// <summary>curl --info: list the running downloads.</summary>
    Info,
    /// <summary>curl --cancel [name]: stop one download, or all of them.</summary>
    Cancel,
    /// <summary>curl --clear_autodownload: forget which downloads the current level waits for.</summary>
    ClearAutodownload,
    /// <summary>curl --finish_autodownload: every download for this level has been named; wait for them.</summary>
    FinishAutodownload,
    /// <summary>Fetch <see cref="DpCurlCommand.Url"/>.</summary>
    Download,
}

/// <summary>What a download is to be used as once it has arrived (LOADTYPE_*).</summary>
public enum DpCurlLoadType { None, Pak, CachePic, SkinFrame }

public sealed class DpCurlCommand
{
    public DpCurlAction Action { get; init; }
    public DpCurlLoadType LoadType { get; init; }
    /// <summary>--forthismap: the level does not start until this download has ended.</summary>
    public bool ForThisMap { get; init; }
    /// <summary>--as: the file name under dlcache/. Null: the last path component of the URL.</summary>
    public string? As { get; init; }
    /// <summary>--maxspeed=: KiB per second, 0 for no limit.</summary>
    public double MaxSpeed { get; init; }
    /// <summary>The last argument: the URL, or for --cancel the name of the download to stop.</summary>
    public string Url { get; init; } = "";
    /// <summary>--cancel with nothing after it: every download.</summary>
    public bool CancelAll { get; init; }
    /// <summary>The files after --for, whether or not the client has them: what this package is for.</summary>
    public IReadOnlyList<string> For { get; init; } = Array.Empty<string>();
    /// <summary>Options that were not understood ("curl: invalid option"); they are ignored, as in DarkPlaces.</summary>
    public IReadOnlyList<string> InvalidOptions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Reads the command as Curl_Curl_f does. <paramref name="argv"/>[0] is "curl".
    /// <paramref name="fileExists"/> answers "--for": the download is skipped when every file named after
    /// it is already in the game data.
    /// </summary>
    public static DpCurlCommand Parse(IReadOnlyList<string> argv, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (argv.Count < 2) return new DpCurlCommand { Action = DpCurlAction.None };

        string url = argv[^1];
        int end = argv.Count;
        DpCurlLoadType loadType = DpCurlLoadType.None;
        bool forThisMap = false;
        string? name = null;
        double maxSpeed = 0;
        List<string> wanted = new(), invalid = new();

        for (int i = 1; i != end; ++i)
        {
            string a = argv[i];
            if (a == "--info") return new DpCurlCommand { Action = DpCurlAction.Info };
            if (a == "--cancel")
                return new DpCurlCommand { Action = DpCurlAction.Cancel, CancelAll = i == end - 1, Url = url };
            if (a == "--pak") loadType = DpCurlLoadType.Pak;
            else if (a == "--cachepic") loadType = DpCurlLoadType.CachePic;
            else if (a == "--skinframe") loadType = DpCurlLoadType.SkinFrame;
            else if (a == "--for")   // must be last option
            {
                bool missing = false;
                for (i = i + 1; i < end - 1; ++i)
                {
                    wanted.Add(argv[i]);
                    if (!fileExists(argv[i])) missing = true;
                }
                // if we get here without a missing file, we have all the files...
                if (!missing) return new DpCurlCommand { Action = DpCurlAction.None, LoadType = loadType, ForThisMap = forThisMap, As = name, Url = url, For = wanted };
                break;
            }
            else if (a == "--forthismap") forThisMap = true;
            else if (a == "--as")
            {
                if (i < end - 1) name = argv[++i];
            }
            else if (a == "--clear_autodownload") return new DpCurlCommand { Action = DpCurlAction.ClearAutodownload };
            else if (a == "--finish_autodownload") return new DpCurlCommand { Action = DpCurlAction.FinishAutodownload };
            else if (a.StartsWith("--maxspeed=", StringComparison.Ordinal))
            {
                // atof: a number that does not parse is 0, which is "no limit".
                _ = double.TryParse(a.AsSpan(11), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out maxSpeed);
                if (!double.IsFinite(maxSpeed) || maxSpeed < 0) maxSpeed = 0;
            }
            else if (a.Length > 0 && a[0] == '-') invalid.Add(a);   // "curl: invalid option"; but we ignore the option
        }

        return new DpCurlCommand
        {
            Action = DpCurlAction.Download, LoadType = loadType, ForThisMap = forThisMap, As = name, MaxSpeed = maxSpeed, Url = url, For = wanted, InvalidOptions = invalid,
        };
    }
}
