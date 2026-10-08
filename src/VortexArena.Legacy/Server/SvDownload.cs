// Port of Base/darkplaces/sv_main.c SV_Download_f, Download_CheckExtensions, SV_StartDownload_f and
// SV_Prepare_CSQC (the deflated copy), sv_send.c SV_SendClientDatagram (the svc_downloaddata block,
// lines 1556-1577), sv_user.c SV_ReadClientMessage (clc_ackdownloaddata, lines 1090-1139), and
// fs.c FS_CheckNastyPath / FS_FileExtension. The client it serves is Protocol/DpDownload.cs.
using System.Text;
using VortexArena.Legacy.Protocol;

namespace VortexArena.Legacy.Server;

/// <summary>The download cvars, with their DarkPlaces defaults. One instance can serve every client.</summary>
public sealed class SvDownloadSettings
{
    /// <summary>sv_allowdownloads: whether clients may download files at all. The client program
    /// (csprogs.dat) is served even when this is off.</summary>
    public bool AllowDownloads { get; set; } = true;
    /// <summary>sv_allowdownloads_inarchive: whether a file that lives inside a pak/pk3 may be downloaded.</summary>
    public bool AllowInArchive { get; set; }
    /// <summary>sv_allowdownloads_archive: whether a pak/pk3/dpk file itself may be downloaded.</summary>
    public bool AllowArchive { get; set; }
    /// <summary>sv_allowdownloads_config: whether .cfg files may be downloaded (they can hold passwords).</summary>
    public bool AllowConfig { get; set; }
    /// <summary>sv_allowdownloads_dlcache: whether files under dlcache/ may be downloaded (they are
    /// other servers' files, cached by this machine when it was a client).</summary>
    public bool AllowDlCache { get; set; }
    /// <summary>The largest file served. SV_Download_f refuses above 1&lt;&lt;30 ("is very large").</summary>
    public int MaxFileSize { get; set; } = 1 << 30;
}

/// <summary>
/// The server's copy of the client program, loaded once per level (SV_Prepare_CSQC): the file, and
/// the file deflated, so that neither is produced again for each client that asks.
/// </summary>
public sealed class SvCsqcProgram
{
    private SvCsqcProgram(string name, byte[] data, byte[]? deflated)
    {
        Name = name;
        Data = data;
        Deflated = deflated;
        Crc = Crc16.Block(data);
    }

    /// <summary>sv.csqc_progname: the name clients ask for, the <c>csqc_progname</c> cvar ("csprogs.dat").</summary>
    public string Name { get; }
    /// <summary>svs.csqc_progdata.</summary>
    public byte[] Data { get; }
    /// <summary>svs.csqc_progdata_deflated: raw DEFLATE, no zlib header (FS_Deflate passes -MAX_WBITS).
    /// Null when not prepared; the file is then always sent as it is.</summary>
    public byte[]? Deflated { get; }
    /// <summary>sv.csqc_progsize, for the <c>csqc_progsize</c> stufftext: the size of the inflated file.</summary>
    public int Size => Data.Length;
    /// <summary>sv.csqc_progcrc, for the <c>csqc_progcrc</c> stufftext: the CRC of the inflated file.</summary>
    public int Crc { get; }

    /// <param name="deflate">False reproduces a DarkPlaces built without zlib ("Cannot compress - need
    /// zlib for this. Using uncompressed progs only.").</param>
    public static SvCsqcProgram Create(string name, byte[] data, bool deflate = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(data);
        return new SvCsqcProgram(name, data, deflate ? DpDownload.DeflateBytes(data) : null);
    }
}

/// <summary>A file found for download.</summary>
/// <param name="Data">The whole file.</param>
/// <param name="Pack">FS_WhichPack: the archive the file lives in, or null for a loose file on disk.
/// When it is set and sv_allowdownloads_inarchive is off the download is refused and
/// <paramref name="Data"/> is not looked at, so it may be left empty.</param>
public readonly record struct SvDownloadFile(ReadOnlyMemory<byte> Data, string? Pack = null);

/// <summary>
/// The owner's file system: a name to its bytes, or null when there is no such file. It is only ever
/// called with a name that has passed every check that can be made on the name alone: a plain
/// forward-slash relative path that cannot leave the game directory, of at most
/// <see cref="SvDownload.MaxNameLength"/> bytes, and not one the settings exclude by its name.
/// </summary>
public delegate SvDownloadFile? SvDownloadLookup(string name);

/// <summary>How a <c>download</c> command ended.</summary>
public enum SvDownloadRequest
{
    /// <summary>cl_downloadbegin was sent; the transfer starts when the client answers sv_startdownload.</summary>
    Begun,
    /// <summary>No file name given; the usage text was printed.</summary>
    Usage,
    /// <summary>FS_CheckNastyPath refused the name (or it is one that cannot be sent back; see <see cref="SvDownload"/>).</summary>
    NastyName,
    /// <summary>sv_allowdownloads is off.</summary>
    Disabled,
    NotFound,
    InArchive,
    ConfigFile,
    DlCache,
    Archive,
    TooLarge,
}

/// <summary>
/// The server half of DarkPlaces' in-band file download, for one client.
///
/// <code>
/// client: stringcmd  download &lt;name&gt; [deflate]
/// server: stufftext  cl_downloadbegin &lt;size&gt; &lt;name&gt;[ deflate]        or: stopdownload
/// client: stringcmd  sv_startdownload
/// server: svc_downloaddata (long start, ushort size, bytes)   ... one per unreliable datagram
/// client: clc_ackdownloaddata (long start, short size)        ... echoing each block
/// server: stufftext  cl_downloadfinished &lt;size&gt; &lt;crc&gt; &lt;name&gt;
/// </code>
///
/// The blocks travel unreliably and all the bookkeeping is here. The server reads forward through
/// the file, one block per datagram, without waiting. It expects the acks to come back in the same
/// order: an ack that starts where the last one ended moves the expected position on, and any other
/// ack means a block (or its ack) was lost, so the read position jumps back to the expected position
/// and everything from there is sent again. At the end of the file it keeps sending empty blocks;
/// their acks are what reveal that the last real block went missing. The download is finished when
/// an in-order ack reaches the end of the file.
///
/// How the owner drives it, per client: feed "download ..." and "sv_startdownload" string commands to
/// <see cref="HandleCommand"/> with the client's reliable message; feed each clc_ackdownloaddata to
/// <see cref="Ack"/>; when building the client's unreliable datagram, halve the entity budget while
/// <see cref="Active"/> and call <see cref="WriteData"/> last; call <see cref="Abort"/> when the
/// client is dropped.
///
/// Deviations from the C, all on the side of refusing more:
/// - The checks that need only the name (config file, dlcache/, archive extension) run before the
///   file system is asked anything, not after FS_FileExists. A request that fails two checks can
///   therefore be told a different reason than DarkPlaces would give.
/// - A name longer than <see cref="MaxNameLength"/> bytes is refused. DarkPlaces copies it into
///   char[MAX_QPATH], which silently truncates, and then serves whatever the truncated name is.
/// - A name with a space, quote, semicolon or control character is refused: it would not survive the
///   trip back in cl_downloadbegin, which the client tokenizes as a console command.
/// - "deflate" is announced only when the deflated copy is what is sent. DarkPlaces announces it
///   whenever the client offered it, even if it has no deflated copy (a build without zlib).
/// - An ack with a negative size is treated as out of order. In the C it moves the expected position
///   backwards, below zero if the client likes.
/// - The "registered Quake" check (gfx/pop.lmp, FS_IsRegisteredQuakePack) is not ported.
/// </summary>
public sealed class SvDownload
{
    /// <summary>sizeof(download_name) - 1: MAX_QPATH less the terminator.</summary>
    public const int MaxNameLength = 127;
    /// <summary>sizeof(data) in SV_SendClientDatagram: the most file bytes in one block.</summary>
    public const int MaxBlockSize = 1400;
    /// <summary>What a block costs besides its data: the command byte, a long and a short.</summary>
    public const int BlockHeaderSize = 7;

    private readonly SvDownloadSettings _settings;
    private readonly SvDownloadLookup _lookup;
    private readonly Func<SvCsqcProgram?> _csqc;

    private ReadOnlyMemory<byte> _file;  // host_client->download_file
    private bool _active;                // download_file != NULL (a file may be empty)
    private int _position;               // FS_Tell(download_file): where the next block is read from

    /// <param name="settings">The cvars. Read at each request, so they may change while the server runs.</param>
    /// <param name="lookup">The file system; see <see cref="SvDownloadLookup"/> for what it is guaranteed.</param>
    /// <param name="csqc">The current level's client program, or null when the level has none
    /// (sv.csqc_progname empty).</param>
    public SvDownload(SvDownloadSettings settings, SvDownloadLookup lookup, Func<SvCsqcProgram?> csqc)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _csqc = csqc ?? throw new ArgumentNullException(nameof(csqc));
    }

    /// <summary>A download exists (host_client->download_file). While true, SV_SendClientDatagram
    /// gives entity updates only half the packet, leaving the rest for file data.</summary>
    public bool Active => _active;
    /// <summary>host_client->download_started: the client has sent sv_startdownload.</summary>
    public bool Started { get; private set; }
    /// <summary>host_client->download_name.</summary>
    public string Name { get; private set; } = "";
    /// <summary>host_client->download_expectedposition: the next position the client should ack.</summary>
    public int ExpectedPosition { get; private set; }
    /// <summary>Where the next block will be read from. Runs ahead of <see cref="ExpectedPosition"/>.</summary>
    public int Position => _position;
    /// <summary>Bytes being sent: the deflated size when <see cref="Deflated"/>.</summary>
    public int Size => _file.Length;
    /// <summary>The bytes being sent are the deflated form of the file.</summary>
    public bool Deflated { get; private set; }
    /// <summary>Downloads this client has completed.</summary>
    public int Completed { get; private set; }
    /// <summary>Times the read position was set back to the expected position by an out-of-order ack.</summary>
    public int Rewinds { get; private set; }

    /// <summary>
    /// Run <paramref name="line"/> if it is one of the two download commands. Returns false, having
    /// done nothing, for any other command.
    /// </summary>
    /// <param name="reliable">The client's reliable message (netconnection->message).</param>
    public bool HandleCommand(string line, DpMessageWriter reliable)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        // Cheap test first: every other command a client sends would otherwise be tokenized twice.
        ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
        if (!trimmed.StartsWith("download", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("sv_startdownload", StringComparison.OrdinalIgnoreCase))
            return false;
        List<string> args = DpStuffText.Tokenize(line);
        if (args.Count == 0)
            return false;
        // Command names are matched without regard to case, as Cmd_ExecuteString does.
        if (args[0].Equals("download", StringComparison.OrdinalIgnoreCase))
        {
            Download(args, reliable);
            return true;
        }
        if (args[0].Equals("sv_startdownload", StringComparison.OrdinalIgnoreCase))
        {
            StartDownload();
            return true;
        }
        return false;
    }

    /// <summary>
    /// SV_Download_f. <paramref name="args"/> is the tokenized command: "download", the name, then
    /// the encodings the client can take in order of preference (only "deflate" exists).
    ///
    /// On <see cref="SvDownloadRequest.Begun"/> the owner must make sure the reliable message goes
    /// out even while the client is between signon stages (host_client->sendsignon = true): the
    /// csprogs.dat download happens exactly then.
    /// </summary>
    public SvDownloadRequest Download(IReadOnlyList<string> args, DpMessageWriter reliable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(reliable);
        if (args.Count < 2)
        {
            Print(reliable, "usage: download <filename> {<extensions>}*\n");
            Print(reliable, "       supported extensions: deflate\n");
            return SvDownloadRequest.Usage;
        }

        string name = args[1];
        // The length is tested before the name is echoed anywhere, so that a 16 KB "name" costs the
        // reliable buffer nothing; see the class comment for the other additions to FS_CheckNastyPath.
        if (IsNastyPath(name) || Encoding.UTF8.GetByteCount(name) > MaxNameLength || !CanBeSentBack(name))
        {
            Print(reliable, name.Length <= MaxNameLength
                ? $"Download rejected: nasty filename \"{name}\"\n"
                : "Download rejected: nasty filename\n");
            return SvDownloadRequest.NastyName;
        }

        if (_active)
        {
            // at this point we'll assume the previous download should be aborted
            StuffText(reliable, "\nstopdownload\n");
            Abort();
        }

        SvCsqcProgram? csqc = _csqc();
        bool isCsqc = csqc is not null && csqc.Name.Length != 0 && string.Equals(name, csqc.Name, StringComparison.Ordinal);

        if (!_settings.AllowDownloads && !isCsqc)
        {
            Print(reliable, "Downloads are disabled on this server\n");
            StuffText(reliable, "\nstopdownload\n");
            return SvDownloadRequest.Disabled;
        }

        // Download_CheckExtensions
        bool wantsDeflate = false;
        for (int i = 2; i < args.Count; i++)
        {
            if (args[i] == "deflate")
            {
                wantsDeflate = true;
                break;
            }
        }

        if (isCsqc)
        {
            // The client program is served from memory, whatever the cvars say and wherever the file
            // lives: a client cannot play without the server's exact copy.
            bool deflated = wantsDeflate && csqc!.Deflated is not null;
            Begin(name, deflated ? csqc!.Deflated! : csqc!.Data, deflated);
            // no, no space is needed between %s and %s :P
            StuffText(reliable, $"\ncl_downloadbegin {Size} {name}{(deflated ? " deflate" : "")}\n");
            return SvDownloadRequest.Begun;
        }

        // The refusals that depend on the name alone come first (a deviation in order only; see the
        // class comment), so the file system is never asked about a name that is excluded.
        string extension = FileExtension(name);
        if (!_settings.AllowConfig && extension.Equals("cfg", StringComparison.OrdinalIgnoreCase))
            return Refuse(reliable, SvDownloadRequest.ConfigFile,
                $"Download rejected: file \"{name}\" is a .cfg file which is forbidden for security reasons\nYou must separately download or purchase the data archives for this game/mod to get this file\n");
        if (!_settings.AllowDlCache && name.StartsWith("dlcache/", StringComparison.OrdinalIgnoreCase))
            return Refuse(reliable, SvDownloadRequest.DlCache,
                $"Download rejected: file \"{name}\" is in the dlcache/ directory which is forbidden for security reasons\nYou must separately download or purchase the data archives for this game/mod to get this file\n");
        if (!_settings.AllowArchive && (extension.Equals("pak", StringComparison.OrdinalIgnoreCase)
            || extension.Equals("pk3", StringComparison.OrdinalIgnoreCase) || extension.Equals("dpk", StringComparison.OrdinalIgnoreCase)))
            return Refuse(reliable, SvDownloadRequest.Archive,
                $"Download rejected: file \"{name}\" is an archive\nYou must separately download or purchase the data archives for this game/mod to get this file\n");

        SvDownloadFile? found = _lookup(name);
        if (found is not { } file)
            return Refuse(reliable, SvDownloadRequest.NotFound,
                $"Download rejected: server does not have the file \"{name}\"\nYou may need to separately download or purchase the data archives for this game/mod to get this file\n");

        // check if the server has forbidden archive downloads entirely
        if (!_settings.AllowInArchive && file.Pack is not null)
            return Refuse(reliable, SvDownloadRequest.InArchive,
                $"Download rejected: file \"{name}\" is in an archive (\"{file.Pack}\")\nYou must separately download or purchase the data archives for this game/mod to get this file\n");

        if (file.Data.Length > Math.Min(_settings.MaxFileSize, 1 << 30))
            return Refuse(reliable, SvDownloadRequest.TooLarge, $"Download rejected: file \"{name}\" is very large\n");

        // Ordinary files are never deflated: "we can only do this if we would actually deflate on
        // the fly which we do not (yet)!"
        Begin(name, file.Data, deflated: false);
        StuffText(reliable, $"\ncl_downloadbegin {Size} {name}\n");
        return SvDownloadRequest.Begun;

        // the rest of the download process is handled in WriteData and Ack. No svc_downloaddata
        // messages will be sent until sv_startdownload is sent by the client.
    }

    private void Begin(string name, ReadOnlyMemory<byte> data, bool deflated)
    {
        _file = data;
        _active = true;
        _position = 0;
        Name = name;
        Deflated = deflated;
        ExpectedPosition = 0;
        Started = false;
    }

    private static SvDownloadRequest Refuse(DpMessageWriter reliable, SvDownloadRequest why, string message)
    {
        Print(reliable, message);
        StuffText(reliable, "\nstopdownload\n");
        return why;
    }

    /// <summary>SV_StartDownload_f: the client has allocated its buffer; blocks may flow.</summary>
    public void StartDownload()
    {
        if (_active)
            Started = true;
    }

    /// <summary>
    /// Forget the download without telling the client: the cleanup SV_DropClient and a superseding
    /// request do.
    /// </summary>
    public void Abort()
    {
        _file = default;
        _active = false;
        _position = 0;
        Name = "";
        Deflated = false;
        ExpectedPosition = 0;
        Started = false;
    }

    /// <summary>
    /// The download part of SV_SendClientDatagram: if a download is running and there is room, append
    /// one svc_downloaddata block to <paramref name="datagram"/>, which already holds whatever else
    /// this packet carries. Returns whether a block was written.
    /// </summary>
    /// <param name="maxSize">The packet's size budget from the client's rate (128..1400), already
    /// halved by the caller because a download is <see cref="Active"/>.</param>
    /// <param name="maxSize2">The hard limit for one datagram, 1400 in DP5 and later.</param>
    public bool WriteData(DpMessageWriter datagram, int maxSize, int maxSize2 = MaxBlockSize)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        if (!_active || !Started)
            return false;
        // downloadsize = min(maxsize*2,maxsize2) - msg.cursize - 7: the half the entities were denied
        // is given back here, minus what they did use.
        long room = Math.Min((long)maxSize * 2, maxSize2) - datagram.Length - BlockHeaderSize;
        // The C's message buffer is always large enough for that; a caller's writer need not be.
        room = Math.Min(room, datagram.MaxSize - datagram.Length - BlockHeaderSize);
        if (room <= 0)
            return false;

        int start = _position;
        int size = (int)Math.Min(Math.Min(room, MaxBlockSize), _file.Length - _position);
        // note this sends empty messages if at the end of the file, which is necessary to keep the
        // packet loss logic working (the last blocks may be lost and need to be re-sent, and that
        // will only occur if the client acks the empty end messages, revealing a gap in the download
        // progress, causing the last blocks to be sent again)
        datagram.WriteByte((int)Svc.DownloadData);
        datagram.WriteLong(start);
        datagram.WriteShort(size);
        if (size > 0)
        {
            datagram.WriteBytes(_file.Span.Slice(start, size));
            _position += size;
        }
        return true;
    }

    /// <summary>
    /// clc_ackdownloaddata: the client echoes the start and size of a block it received. Returns true
    /// when this ack completed the download; cl_downloadfinished has then been written to
    /// <paramref name="reliable"/> and the download is over.
    /// </summary>
    /// <param name="size">As read with MSG_ReadShort: signed.</param>
    public bool Ack(int start, int size, DpMessageWriter reliable)
    {
        ArgumentNullException.ThrowIfNull(reliable);
        if (!_active || !Started)
            return false;

        if (ExpectedPosition == start && size >= 0)
        {
            // a data block was successfully received by the client, update the expected position on
            // the next data block. Nothing checks that the block was one this server sent: a client
            // that acks what it never received only cheats itself out of the file.
            long next = (long)start + size;
            ExpectedPosition = (int)Math.Min(next, _file.Length);
            // if this was the last data block of the file, it's done
            if (next >= _file.Length)
            {
                // tell the client that the download finished. The crc is calculated now and not at
                // the start "because it reduces potential for Denial Of Service attacks against the
                // server": only a client that sat through the whole transfer can make it happen.
                // Both numbers describe the bytes as sent, so the deflated ones when deflated.
                int crc = Crc16.Block(_file.Span);
                StuffText(reliable, $"\ncl_downloadfinished {_file.Length} {crc} {Name}\n");
                Completed++;
                Abort();
                return true;
            }
        }
        else
        {
            // a data block was lost, reset to the expected position and resume sending from there
            _position = ExpectedPosition;
            Rewinds++;
        }
        return false;
    }

    // SV_ClientPrintf: svc_print and the text.
    private static void Print(DpMessageWriter reliable, string text)
    {
        reliable.WriteByte((int)Svc.Print);
        reliable.WriteString(text);
    }

    // SV_ClientCommands: svc_stufftext and the text. The leading newline in every call ends whatever
    // half-written command line the client's buffer may hold.
    private static void StuffText(DpMessageWriter reliable, string text)
    {
        reliable.WriteByte((int)Svc.StuffText);
        reliable.WriteString(text);
    }

    /// <summary>
    /// FS_CheckNastyPath(path, false): true for any name that is not a plain forward-slash relative
    /// path inside the game directory.
    /// </summary>
    public static bool IsNastyPath(string? path)
    {
        // all: never allow an empty path
        if (string.IsNullOrEmpty(path))
            return true;
        // Windows: don't allow \ in filenames. Mac, Amiga, Windows: ':' goes to a drive root.
        // Amiga: "//" is the parent directory.
        if (path.Contains('\\') || path.Contains(':') || path.Contains("//", StringComparison.Ordinal))
            return true;
        // all: don't allow going to parent directory (../ or /../), nor absolute paths
        if (path.Contains("..", StringComparison.Ordinal) || path[0] == '/')
            return true;
        // all: don't allow . character immediately before a slash, this catches all imaginable cases
        // of ./, ../, .../, etc; and forbid a leading dot on any filename for any reason
        if (path.Contains("./", StringComparison.Ordinal) || path.Contains("/.", StringComparison.Ordinal))
            return true;
        return false;
    }

    // Not in the C: whether the name comes back out of "cl_downloadbegin <size> <name>" as one
    // argument, unchanged. A control character includes the NUL that would cut the C string short
    // (and with it any check made on the whole of it).
    private static bool CanBeSentBack(string name)
    {
        foreach (char c in name)
            if (c <= ' ' || c == '"' || c == ';' || c == 0x7F)
                return false;
        // "//" starts a comment, and is already nasty.
        return true;
    }

    /// <summary>FS_FileExtension: what follows the last dot of the last path component, or "".</summary>
    public static string FileExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0)
            return "";
        int separator = name.LastIndexOfAny(SeparatorChars);
        return dot < separator ? "" : name[(dot + 1)..];
    }

    private static readonly char[] SeparatorChars = { '/', '\\', ':' };
}
