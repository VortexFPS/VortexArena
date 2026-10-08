// Port of Base/darkplaces/cl_parse.c CL_SignonReply, CL_SendPlayerInfo, CL_BeginDownloads (the
// csprogs part), CL_DownloadBegin_f / CL_DownloadFinished_f / CL_StopDownload_f, and cl_cmd.c
// CL_ForwardToServer_f (the "cmd" command). The server side of the sequence is sv_main.c
// SV_SendServerinfo and sv_user.c SV_PreSpawn_f / SV_Spawn_f / SV_Begin_f.
namespace VortexArena.Legacy.Protocol;

/// <summary>What the client tells the server about itself during signon (cvars in DarkPlaces).</summary>
public sealed class DpSignonConfig
{
    /// <summary>_cl_name.</summary>
    public string Name { get; set; } = "player";
    /// <summary>topcolor / bottomcolor: shirt and pants palette indices.</summary>
    public int TopColor { get; set; }
    public int BottomColor { get; set; }
    /// <summary>rate: bytes per second the server may send.</summary>
    public int Rate { get; set; } = 20000;
    /// <summary>rate_burstsize.</summary>
    public int RateBurstSize { get; set; } = 1024;
    /// <summary>playermodel / playerskin; sent only when not empty.</summary>
    public string PlayerModel { get; set; } = "";
    public string PlayerSkin { get; set; } = "";

    /// <summary>
    /// Values for <c>$name</c> in commands the server stuffs with a <c>cmd</c> prefix. Xonotic kicks a
    /// client that does not answer "cmd clientversion $gameversion" with its game version, so
    /// <c>gameversion</c> is here by default: 806 is Xonotic 0.8.6 (xonotic-common.cfg).
    /// </summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal) { ["gameversion"] = "806" };

    /// <summary>
    /// Asked before downloading the server's client program: (name, size, crc) → true if a matching
    /// file is already on hand. Null means "never", so it is always downloaded.
    /// </summary>
    public Func<string, int, int, bool>? HaveFile { get; set; }
}

/// <summary>
/// The signon sequence: the scripted exchange between "connection accepted" and "in the game".
///
/// <code>
/// server (reliable): print, stufftext csqc_progname/csqc_progsize/csqc_progcrc,
///                    stufftext cl_serverextension_download 2, svc_serverinfo, cdtrack, setview,
///                    signonnum 1
/// client: stringcmds name, color, rate, rate_burstsize; then the csprogs download if needed; then
///         stringcmd prespawn
/// server: baselines, statics, lightstyles, ..., signonnum 2
/// client: stringcmd spawn
/// server: client stats, scoreboard, ..., signonnum 3
/// client: stringcmd begin
/// server: entity frames; the first one is signon stage 4 (SIGNONS), fully connected
/// </code>
///
/// This class decides what to send; it owns no channel. Commands it wants sent accumulate in
/// <see cref="Commands"/> for the owner to forward as clc_stringcmd.
///
/// It is also where stuffed console commands that the <em>engine</em> must act on are recognised:
/// the three csqc_* cvars, cl_serverextension_download, cl_downloadbegin, cl_downloadfinished,
/// stopdownload, and <c>cmd</c>. Anything else is not for this layer.
/// </summary>
public sealed class DpSignon
{
    private readonly DpSignonConfig _config;
    private bool _downloadCsqc;   // cl.downloadcsqc
    private bool _loadBegun;      // cl.loadbegun
    private bool _loadFinished;   // cl.loadfinished
    private bool _beginDownloadsPending;

    public DpSignon(DpSignonConfig config, DpDownload download)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Download = download ?? throw new ArgumentNullException(nameof(download));
    }

    public DpDownload Download { get; }

    /// <summary>cls.signon: 0 after connecting, 4 (<see cref="DpProtocol.Signons"/>) once in the game.</summary>
    public int Stage { get; private set; }

    /// <summary>String commands waiting to be sent, in order. The owner sends and clears them.</summary>
    public List<string> Commands { get; } = new();

    // The cvars the server sets by stufftext, with DarkPlaces' defaults.
    public string CsqcProgName { get; private set; } = "csprogs.dat";
    public int CsqcProgSize { get; private set; } = -1;
    public int CsqcProgCrc { get; private set; } = -1;
    /// <summary>cl_serverextension_download: 0 none, 1 in-band download, 2 also the deflate encoding.</summary>
    public int ServerExtensionDownload { get; private set; }

    /// <summary>The downloaded client program, once a download of <see cref="CsqcProgName"/> completed.</summary>
    public byte[]? CsprogsData { get; private set; }
    /// <summary>True when <see cref="CsprogsData"/> has exactly the size and CRC the server announced
    /// in csqc_progsize / csqc_progcrc. A program that fails this must not be run.</summary>
    public bool CsprogsVerified { get; private set; }
    /// <summary>The last download to finish, whatever its outcome.</summary>
    public DpDownloadResult? LastDownload { get; private set; }

    /// <summary>NetConn_ConnectionEstablished: a new connection starts the sequence over.</summary>
    public void Reset()
    {
        Stage = 0;
        Commands.Clear();
        CsqcProgName = "csprogs.dat";
        CsqcProgSize = -1;
        CsqcProgCrc = -1;
        ServerExtensionDownload = 0;
        CsprogsData = null;
        CsprogsVerified = false;
        LastDownload = null;
        _downloadCsqc = _loadBegun = _loadFinished = _beginDownloadsPending = false;
        Download.Abort();
    }

    /// <summary>
    /// svc_serverinfo (CL_ParseServerInfo): a level is starting. The download extension flag is
    /// cleared here and set again by a stufftext; that stufftext travels <em>before</em> the
    /// serverinfo in the same message and still wins, because DarkPlaces runs stuffed commands only
    /// after the message has been parsed. Callers must keep that order: parse the message, then feed
    /// its stufftext to <see cref="HandleCommand"/>, then call <see cref="EndOfMessage"/>.
    /// </summary>
    public void OnServerInfo()
    {
        ServerExtensionDownload = 0;
        _downloadCsqc = true;
        _loadBegun = false;
        _loadFinished = false;
    }

    /// <summary>
    /// svc_signonnum. Returns false for a stage that goes backwards ("Received signon %i when at %i",
    /// fatal in DarkPlaces); a repeated stage 1 is allowed, since a level change sends it again.
    /// </summary>
    public bool OnSignonNum(int stage)
    {
        if (stage <= Stage && stage != 1)
            return false;
        Stage = stage;
        switch (stage)
        {
            case 1:
                // send player info before we begin downloads
                // (so that the server can see the player name while downloading)
                Commands.Add($"name \"{_config.Name}\"");
                Commands.Add($"color {_config.TopColor} {_config.BottomColor}");
                Commands.Add($"rate {_config.Rate}");
                Commands.Add($"rate_burstsize {_config.RateBurstSize}");
                if (_config.PlayerModel.Length != 0)
                    Commands.Add($"playermodel {_config.PlayerModel}");
                if (_config.PlayerSkin.Length != 0)
                    Commands.Add($"playerskin {_config.PlayerSkin}");
                // "execute cl_begindownloads next frame (after any commands added by svc_stufftext
                // have been executed)"
                _beginDownloadsPending = true;
                break;
            case 2:
                Commands.Add("spawn");
                break;
            case 3:
                Commands.Add("begin");
                break;
        }
        return true;
    }

    /// <summary>svc_entities: "first update is the final signon stage".</summary>
    public void OnEntityFrame()
    {
        if (Stage == DpProtocol.Signons - 1)
            Stage = DpProtocol.Signons;
    }

    /// <summary>
    /// Offer one stuffed console command (a line from <see cref="DpStuffTextBuffer"/>). Returns true
    /// if it was one of the engine's and has been dealt with; false if it is somebody else's (a
    /// game alias, a cvar the game reads, curl, ...).
    /// </summary>
    public bool HandleCommand(string line)
    {
        List<string> args = DpStuffText.Tokenize(line);
        if (args.Count == 0)
            return true; // blank or all comment

        switch (args[0].ToLowerInvariant())
        {
            case "csqc_progname":
                if (args.Count > 1) CsqcProgName = args[1];
                return true;
            case "csqc_progsize":
                if (args.Count > 1) CsqcProgSize = DpStuffText.Atoi(args[1]);
                return true;
            case "csqc_progcrc":
                if (args.Count > 1) CsqcProgCrc = DpStuffText.Atoi(args[1]);
                return true;
            case "cl_serverextension_download":
                if (args.Count > 1) ServerExtensionDownload = DpStuffText.Atoi(args[1]);
                return true;

            case "cl_downloadbegin":
            {
                int size = args.Count > 1 ? DpStuffText.Atoi(args[1]) : 0;
                string name = args.Count > 2 ? args[2] : "";
                bool deflate = args.Count >= 4 && args[3] == "deflate";
                if (Download.Begin(size, name, deflate))
                    Commands.Add("sv_startdownload");
                return true;
            }

            case "cl_downloadfinished":
            {
                if (args.Count < 3)
                    return true; // "Malformed cl_downloadfinished command"
                bool wasActive = Download.Active;
                DpDownloadResult result = Download.Finish(DpStuffText.Atoi(args[1]), DpStuffText.Atoi(args[2]));
                if (wasActive)
                    FinishedDownload(result);
                BeginDownloads();
                return true;
            }

            case "stopdownload":
                // The server refused or abandoned the download ("Download rejected").
                Download.Abort();
                BeginDownloads();
                return true;

            case "cmd":
            {
                // CL_ForwardToServer_f: strip "cmd" and send the rest, after $cvar expansion.
                string rest = DpStuffText.ArgsAfterFirst(DpStuffText.Expand(line, _config.Variables));
                if (rest.Length != 0)
                    Commands.Add(rest);
                return true;
            }
        }
        return false;
    }

    private void FinishedDownload(DpDownloadResult result)
    {
        LastDownload = result;
        if (result.Status != DpDownloadStatus.Completed || result.Name != CsqcProgName)
            return;
        CsprogsData = result.Data;
        CsprogsVerified = result.Crc == CsqcProgCrc && (CsqcProgSize == -1 || result.Data.Length == CsqcProgSize);
    }

    /// <summary>
    /// The owner calls this after each message's stufftext has gone through <see cref="HandleCommand"/>.
    /// It is the "next frame" on which DarkPlaces runs the cl_begindownloads it queued at signon 1.
    /// </summary>
    public void EndOfMessage()
    {
        if (!_beginDownloadsPending)
            return;
        _beginDownloadsPending = false;
        // "cl_begindownloads is only valid once per match"
        if (!_loadBegun)
            BeginDownloads();
    }

    // CL_BeginDownloads. DarkPlaces goes on from here to load every model and sound, downloading the
    // missing ones one at a time, and (with curl) waits for HTTP package downloads first. None of
    // that is here: after the client program the loading is declared finished.
    private void BeginDownloads()
    {
        _loadBegun = true;
        // if already downloading something, don't stop it
        if (Download.Active)
            return;

        if (_downloadCsqc)
        {
            _downloadCsqc = false;
            if (CsqcProgName.Length != 0 && CsqcProgCrc >= 0 && ServerExtensionDownload != 0
                && !(_config.HaveFile?.Invoke(CsqcProgName, CsqcProgSize, CsqcProgCrc) ?? false))
            {
                Commands.Add(ServerExtensionDownload == 2
                    ? $"download {CsqcProgName} deflate"
                    : $"download {CsqcProgName}");
                return;
            }
        }

        if (!_loadFinished)
        {
            _loadFinished = true;
            // now issue the spawn to move on to signon 2 like normal
            Commands.Add("prespawn");
        }
    }
}
