// Port of Base/darkplaces/cl_parse.c CL_DownloadBegin_f, CL_ParseDownload and CL_StopDownload (the
// in-band file download), fs.c FS_Inflate (the "deflate" encoding) and FS_CheckNastyPath. The server
// side it talks to is sv_main.c SV_Download_f, sv_send.c:1555-1576 and sv_user.c:1093-1137.
using System.IO.Compression;

namespace VortexArena.Legacy.Protocol;

public enum DpDownloadStatus
{
    /// <summary>cl_downloadfinished arrived with no download active.</summary>
    NotActive,
    /// <summary>The file is complete and matches the size and CRC the server announced.</summary>
    Completed,
    /// <summary>The received bytes do not match the announced size or CRC.</summary>
    Corrupt,
    /// <summary>The bytes matched but would not inflate.</summary>
    InflateFailed,
}

/// <summary>A finished (or failed) download.</summary>
public sealed class DpDownloadResult
{
    public DpDownloadStatus Status { get; init; }
    public string Name { get; init; } = "";
    /// <summary>The file, inflated if it travelled deflated. Empty unless <see cref="DpDownloadStatus.Completed"/>.</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();
    /// <summary>CRC16 of <see cref="Data"/>.</summary>
    public int Crc { get; init; }
    /// <summary>Bytes that crossed the wire (the deflated size when deflate was used).</summary>
    public int WireSize { get; init; }
    public bool WasDeflated { get; init; }
}

/// <summary>
/// The client half of DarkPlaces' in-band file download, used here for csprogs.dat.
///
/// <code>
/// client: stringcmd  download &lt;name&gt; deflate
/// server: stufftext  cl_downloadbegin &lt;size&gt; &lt;name&gt; deflate
/// client: stringcmd  sv_startdownload
/// server: svc_downloaddata (long start, ushort size, bytes)   ... in unreliable datagrams
/// client: clc_ackdownloaddata (start, size)                   ... one per block, echoing it
/// server: stufftext  cl_downloadfinished &lt;size&gt; &lt;crc&gt; &lt;name&gt;
/// </code>
///
/// The blocks are unreliable and the server does the bookkeeping: it expects each ack to name the
/// block it is waiting for, and seeks back when one does not. The client's whole job is to echo
/// every block it sees, including the empty ones the server sends once it reaches the end (those are
/// what let it notice that the last real block was lost), and to store the data where it is told.
///
/// "deflate" is a raw DEFLATE stream with no zlib or gzip wrapper (FS_Deflate and FS_Inflate pass
/// -MAX_WBITS), which is <see cref="DeflateStream"/> and not ZLibStream. The size and CRC in
/// cl_downloadfinished describe the bytes as sent, so they are checked before inflating.
/// </summary>
public sealed class DpDownload
{
    /// <summary>cl_downloadbegin's own limit (1&lt;&lt;30). Kept as the outer bound.</summary>
    public const int ProtocolMaxSize = 1 << 30;

    private byte[]? _memory;
    private int _maxSize;
    private int _curSize;
    private readonly List<DpDownloadAck> _acks = new(DpProtocol.MaxDownloadAcks);

    /// <summary>The largest file accepted. DarkPlaces allocates whatever the server announces, up to
    /// a gigabyte; that is a gift to a hostile server, so the default here is 64 MiB.</summary>
    public int MaxSize { get; set; } = 64 << 20;

    /// <summary>The largest inflated result accepted, so a small deflated file cannot expand without bound.</summary>
    public int MaxInflatedSize { get; set; } = 256 << 20;

    /// <summary>
    /// How many blocks may wait to be acknowledged in the next packet. DarkPlaces keeps four
    /// (CL_MAX_DOWNLOADACKS) and forgets the rest; the server then finds a gap and sends everything after it
    /// again, so a client frame longer than four server frames costs a download a round trip of repeats.
    /// Nothing in the protocol limits the acknowledgements in one packet (seven bytes each), so an owner
    /// may raise this: every block that arrived is then acknowledged, in order.
    /// </summary>
    public int MaxPendingAcks { get; set; } = DpProtocol.MaxDownloadAcks;

    /// <summary>Blocks seen since the download began, how many of them the server had sent before (it
    /// went back after an acknowledgement it did not expect), and acknowledgements that found no room.</summary>
    public int BlocksReceived { get; private set; }
    public int BlocksRepeated { get; private set; }
    public int AcksDropped { get; private set; }
    private int _highWater;

    public bool Active => _memory is not null;
    public string Name { get; private set; } = "";
    public bool Deflate { get; private set; }
    public int ExpectedSize => _maxSize;
    /// <summary>End of the last block stored (cls.qw_downloadmemorycursize).</summary>
    public int ReceivedSize => _curSize;

    /// <summary>
    /// CL_DownloadBegin_f. Any download already running is dropped. Returns true when the download
    /// was set up and <c>sv_startdownload</c> should be sent; false for "received bogus information".
    /// </summary>
    public bool Begin(int size, string name, bool deflate)
    {
        Abort();
        if (size < 0 || size > ProtocolMaxSize || size > MaxSize || IsNastyPath(name))
            return false;
        Name = name;
        _maxSize = size;
        _memory = new byte[size];
        _curSize = 0;
        BlocksReceived = BlocksRepeated = AcksDropped = 0;
        _highWater = 0;
        Deflate = deflate;
        return true;
    }

    /// <summary>CL_StopDownload(0, 0): forget the download in progress.</summary>
    public void Abort()
    {
        _memory = null;
        Name = "";
        _maxSize = 0;
        _curSize = 0;
        Deflate = false;
    }

    /// <summary>
    /// CL_ParseDownload. Queues the ack whether or not a download is active, then stores the block.
    /// Returns false for a block that does not fit the announced file ("corrupt download message", a
    /// fatal error in DarkPlaces).
    /// </summary>
    public bool OnData(int start, ReadOnlySpan<byte> data)
    {
        // record the start/size information to ack in the next input packet. Only four are kept,
        // and a fifth before the next packet is dropped, as in the C: the server re-sends.
        if (_acks.Count < MaxPendingAcks)
            _acks.Add(new DpDownloadAck(start, data.Length));
        else
            AcksDropped++;
        BlocksReceived++;
        if (_memory is not null && data.Length > 0 && start < _highWater) BlocksRepeated++;
        if (start + data.Length > _highWater) _highWater = start + data.Length;

        if (_memory is null)
            return true; // "received %i bytes with no download active"

        // start comes straight from the server: it must not be negative and start + size must not overflow
        if (start < 0 || data.Length > _maxSize || start > _maxSize - data.Length)
            return false;

        data.CopyTo(_memory.AsSpan(start));
        _curSize = start + data.Length;
        return true;
    }

    /// <summary>Move the queued acks into <paramref name="acks"/> for the next input packet.</summary>
    public void TakeAcks(List<DpDownloadAck> acks)
    {
        acks.AddRange(_acks);
        _acks.Clear();
    }

    public int PendingAcks => _acks.Count;

    /// <summary>
    /// CL_StopDownload(size, crc) as called from cl_downloadfinished: check what arrived against
    /// what the server says it sent, inflate if needed, and end the download either way.
    /// </summary>
    public DpDownloadResult Finish(int size, int crc)
    {
        if (_memory is null)
            return new DpDownloadResult { Status = DpDownloadStatus.NotActive };

        string name = Name;
        bool deflate = Deflate;
        int wireSize = _curSize;
        ReadOnlySpan<byte> received = _memory.AsSpan(0, _curSize);
        bool matches = _curSize == size && Crc16.Block(received) == crc;
        byte[]? data = null;
        if (matches)
            data = deflate ? Inflate(received, MaxInflatedSize) : received.ToArray();
        Abort();

        if (!matches)
            return new DpDownloadResult { Status = DpDownloadStatus.Corrupt, Name = name, WireSize = wireSize, WasDeflated = deflate };
        if (data is null)
            return new DpDownloadResult { Status = DpDownloadStatus.InflateFailed, Name = name, WireSize = wireSize, WasDeflated = deflate };
        return new DpDownloadResult
        {
            Status = DpDownloadStatus.Completed,
            Name = name,
            Data = data,
            Crc = Crc16.Block(data),
            WireSize = wireSize,
            WasDeflated = deflate,
        };
    }

    /// <summary>FS_Inflate: raw DEFLATE to bytes. Null if the stream is damaged or larger than <paramref name="maxSize"/>.</summary>
    public static byte[]? Inflate(ReadOnlySpan<byte> deflated, int maxSize)
    {
        try
        {
            using var input = new MemoryStream(deflated.ToArray(), writable: false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            int n;
            while ((n = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + n > maxSize)
                    return null;
                output.Write(buffer, 0, n);
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>FS_Deflate: the encoding a server applies. Here for tests and for a future server.</summary>
    public static byte[] DeflateBytes(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var deflater = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflater.Write(data);
        return output.ToArray();
    }

    /// <summary>FS_CheckNastyPath(path, false): refuse any name that could leave the game directory
    /// or is not a plain forward-slash relative path.</summary>
    public static bool IsNastyPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;
        if (path.Contains('\\') || path.Contains(':') || path.Contains("//", StringComparison.Ordinal))
            return true;
        if (path.Contains("..", StringComparison.Ordinal) || path[0] == '/')
            return true;
        if (path.Contains("./", StringComparison.Ordinal) || path.Contains("/.", StringComparison.Ordinal))
            return true;
        return false;
    }
}
