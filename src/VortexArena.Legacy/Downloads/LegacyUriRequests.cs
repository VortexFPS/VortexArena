// What Base/darkplaces/libcurl.c does for prvm_cmds.c VM_uri_get: Curl_Begin_ToMemory_POST (a GET, or a
// POST with a content type and a body, into a buffer of MAX_INPUTLINE bytes), Curl_Begin's rules (curl_enabled,
// the URL scheme check, the insertion of the server's address into "http:///x"), CheckPendingDownloads
// (a few transfers at once, the rest wait), CURL_fwrite's "buffer overrun", Curl_Frame's classification of a
// finished transfer and Curl_EndDownload's status for the callback (CURLCBSTATUS_*), and Curl_CancelAll.
//
// The addresses come from the client program, and the client program comes from whoever runs the server.
// So, as for package downloads (LegacyPackageFetcher.cs), everything is bounded and stricter than DarkPlaces:
// http and https only (no ftp), no address on this machine or a private network unless the game server is at
// one, redirects followed a few times with every target checked again, one deadline for the whole request, a
// bound on requests in flight, waiting and per minute, no proxy, no cookies, and nothing is ever written to
// disk. The transfer runs on a pool thread; its outcome is handed over on the session's thread by Deliver.
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace VortexArena.Legacy.Downloads;

/// <summary>libcurl.h CURLCBSTATUS_*: what URI_Get_Callback is told. A positive status is the HTTP status of a 4xx or 5xx reply.</summary>
public static class LegacyUriStatus
{
    public const int Ok = 0;
    /// <summary>"failed for generic reason (e.g. buffer too small)": no connection, a timeout, a refused address, a reply larger than the buffer.</summary>
    public const int Failed = -1;
    /// <summary>"aborted by curl --cancel": here, the request was cancelled.</summary>
    public const int Aborted = -2;
    /// <summary>"only used if no HTTP status code is available".</summary>
    public const int ServerError = -3;
    /// <summary>"should never happen".</summary>
    public const int Unknown = -4;
}

public sealed class LegacyUriLimits
{
    /// <summary>curl_enabled for the client program's requests. Off: uri_get answers 0 and the extensions
    /// DP_QC_URI_GET / DP_QC_URI_POST are not advertised, so the program says itself that HTTP is missing.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The reply buffer. DarkPlaces' is MAX_INPUTLINE (16384) bytes, of which a QuakeC string holds
    /// 16383; a longer reply is a failed request (status -1). Not raised above DarkPlaces' size.</summary>
    public int MaxResponseBytes { get; set; } = 16384;
    /// <summary>The largest POST body, in bytes. DarkPlaces has no limit but its string buffers'.</summary>
    public int MaxPostBytes { get; set; } = 256 << 10;
    /// <summary>curl_maxdownloads for these requests: transfers running at once (DarkPlaces: 3, shared with downloads).</summary>
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>Requests running, waiting to run, or finished and not yet handed to the program; one more is refused.</summary>
    public int MaxPending { get; set; } = 16;
    /// <summary>Requests a program may start in a burst, and the seconds after which one more is allowed.</summary>
    public int Burst { get; set; } = 20;
    public double RefillSeconds { get; set; } = 3;
    public int MaxRedirects { get; set; } = 5;
    public double ConnectTimeoutSeconds { get; set; } = 10;
    /// <summary>Seconds one request may take from its start to the last byte of the reply, redirects included.
    /// (DarkPlaces gives up after 45 s below 256 bytes a second, with no limit on the whole.)</summary>
    public double TotalTimeoutSeconds { get; set; } = 30;
    /// <summary>curl_useragent: what is sent as User-Agent; empty sends none.</summary>
    public string UserAgent { get; set; } = "";
}

public sealed class LegacyUriRequest
{
    public required Uri Url { get; init; }
    /// <summary>Null for a GET. Otherwise the request is a POST of <see cref="PostBody"/> with this Content-Type.</summary>
    public string? PostContentType { get; init; }
    public byte[]? PostBody { get; init; }
    public int MaxResponseBytes { get; init; } = 16384;
    public int MaxRedirects { get; init; } = 5;
    public double ConnectTimeoutSeconds { get; init; } = 10;
    public double TotalTimeoutSeconds { get; init; } = 30;
    /// <summary>"dp://host:port/": DarkPlaces names the game server as the referer.</summary>
    public string? Referer { get; init; }
    public string? UserAgent { get; init; }
    /// <summary>See <see cref="LegacyFetchRequest.AllowPrivateHosts"/>.</summary>
    public bool AllowPrivateHosts { get; init; }
}

/// <summary>The outcome of one request: a <see cref="LegacyUriStatus"/> (or an HTTP status), what was received
/// (at most the buffer's size), and for a log why it failed.</summary>
public readonly record struct LegacyUriResult(int Status, byte[] Body, string? Error);

public interface ILegacyUriFetcher
{
    /// <summary>Never throws for anything a server, a network or the request itself can do.</summary>
    Task<LegacyUriResult> FetchAsync(LegacyUriRequest request, CancellationToken cancel);
}

public sealed class HttpUriFetcher : ILegacyUriFetcher
{
    public async Task<LegacyUriResult> FetchAsync(LegacyUriRequest request, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TotalTimeoutSeconds, 1, 600)));
        try { return await Fetch(request, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return cancel.IsCancellationRequested
                ? new LegacyUriResult(LegacyUriStatus.Aborted, Array.Empty<byte>(), "the request was cancelled")
                : new LegacyUriResult(LegacyUriStatus.Failed, Array.Empty<byte>(), "the request took too long and was given up");
        }
        catch (HttpRequestException e) { return Failed("the request failed: " + (e.InnerException is { Message.Length: > 0 } inner ? e.Message + " (" + inner.Message + ")" : e.Message)); }
        catch (IOException e) { return Failed("the transfer broke off: " + e.Message); }
        catch (SocketException e) { return Failed("the connection failed: " + e.Message); }
        catch (InvalidOperationException e) { return Failed("the request failed: " + e.Message); }
        catch (FormatException e) { return Failed("the request could not be formed: " + e.Message); }

        static LegacyUriResult Failed(string why) => new(LegacyUriStatus.Failed, Array.Empty<byte>(), why);
    }

    private static async Task<LegacyUriResult> Fetch(LegacyUriRequest request, CancellationToken cancel)
    {
        if (HttpPackageFetcher.CheckUrl(request.Url) is { } bad) return new LegacyUriResult(LegacyUriStatus.Failed, Array.Empty<byte>(), bad);
        using SocketsHttpHandler handler = new()
        {
            // As HttpPackageFetcher: redirects one at a time so each target is checked, the body as it is sent,
            // no cookies, no proxy, and a connection only to an address that was looked at.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(request.ConnectTimeoutSeconds, 1, 120)),
            MaxResponseHeadersLength = 32,   // kilobytes
            MaxConnectionsPerServer = 1,
            ConnectCallback = (context, token) => HttpPackageFetcher.Connect(context.DnsEndPoint, request.AllowPrivateHosts, token),
        };
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        Uri url = request.Url;
        bool post = request.PostContentType is not null;
        int cap = Math.Clamp(request.MaxResponseBytes, 1, 1 << 20);
        for (int hop = 0; ; hop++)
        {
            using HttpRequestMessage message = new(post ? HttpMethod.Post : HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(request.UserAgent)) message.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
            if (!string.IsNullOrEmpty(request.Referer)) message.Headers.TryAddWithoutValidation("Referer", request.Referer);
            message.Headers.TryAddWithoutValidation("Accept", "*/*");
            if (post)
            {
                message.Content = new ByteArrayContent(request.PostBody ?? Array.Empty<byte>());
                message.Content.Headers.TryAddWithoutValidation("Content-Type", request.PostContentType);
            }

            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= request.MaxRedirects) return new LegacyUriResult(LegacyUriStatus.Failed, Array.Empty<byte>(), $"more than {request.MaxRedirects} redirects");
                Uri? location = response.Headers.Location;
                if (location is null) return new LegacyUriResult(LegacyUriStatus.Failed, Array.Empty<byte>(), "a redirect without a destination");
                if (!location.IsAbsoluteUri) location = new Uri(url, location);
                if (HttpPackageFetcher.CheckUrl(location) is { } badTarget)
                    return new LegacyUriResult(LegacyUriStatus.Failed, Array.Empty<byte>(), "redirected to an address that is not allowed: " + badTarget);
                url = location;
                // libcurl's default: a POST redirected by 301, 302 or 303 is repeated as a GET; 307 and 308 keep it.
                if (status is 301 or 302 or 303) post = false;
                continue;
            }

            // "case 4: case 5: failed = CURL_DOWNLOAD_SERVERERROR; result = code" - the body is still received.
            int outcome = status / 100 is 4 or 5 ? status : LegacyUriStatus.Ok;
            if (response.Content.Headers.ContentLength is { } announced && announced > cap)
                return new LegacyUriResult(LegacyUriStatus.Failed, Array.Empty<byte>(), $"the reply is {announced} bytes (the buffer is {cap})");
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            byte[] buffer = new byte[cap + 1];
            int received = 0;
            while (received < buffer.Length)
            {
                int n = await body.ReadAsync(buffer.AsMemory(received), cancel).ConfigureAwait(false);
                if (n <= 0) break;
                received += n;
            }
            // CURL_fwrite: "otherwise: buffer overrun, ret stays -1" - the transfer fails with what fitted.
            if (received > cap) return new LegacyUriResult(LegacyUriStatus.Failed, buffer[..cap], $"the reply is larger than the buffer ({cap} bytes)");
            return new LegacyUriResult(outcome, buffer[..received], outcome == LegacyUriStatus.Ok ? null : $"the server answered {status}");
        }
    }
}

/// <summary>
/// The HTTP requests of one legacy session's client program (the uri_get builtin). <see cref="Begin"/> and
/// <see cref="Deliver"/> are called on the session's thread; the transfers run elsewhere and touch nothing
/// of the session.
/// </summary>
public sealed class LegacyUriRequests : IDisposable
{
    private sealed class Item
    {
        public required float Id;
        public required LegacyUriRequest Request;
        public Task<LegacyUriResult>? Task;
        public readonly CancellationTokenSource Cancel = new();
    }

    private readonly LegacyUriLimits _limits;
    private readonly ILegacyUriFetcher _fetcher;
    private readonly List<Item> _items = new();
    private readonly List<Task> _abandoned = new();
    private double _tokens;
    private double _tokensAt = double.NaN;
    private int _refusalsPrinted;
    private bool _disposed;

    public LegacyUriRequests(LegacyUriLimits? limits = null, ILegacyUriFetcher? fetcher = null)
    {
        _limits = limits ?? new LegacyUriLimits();
        _fetcher = fetcher ?? new HttpUriFetcher();
        _tokens = _limits.Burst;
    }

    public LegacyUriLimits Limits => _limits;
    /// <summary>The game server's address: inserted into "http:///x" addresses and named in the Referer.</summary>
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; } = 26000;
    /// <summary>Whether the game server is on this machine or a private network: only then may a request go to one.</summary>
    public bool ServerIsPrivate { get; set; }
    /// <summary>A line for the session's log: a refused request, a failed one. Never the query part of an address.</summary>
    public Action<string> Print { get; set; } = _ => { };
    /// <summary>Replaces the monotonic clock of the rate limit (seconds). For tests.</summary>
    public Func<double>? Clock { get; set; }

    /// <summary>Whether uri_get can work at all: what decides if DP_QC_URI_GET and DP_QC_URI_POST are advertised.</summary>
    public bool Available => _limits.Enabled && !_disposed;
    /// <summary>Requests running, waiting, or finished and not yet delivered.</summary>
    public int Pending => _items.Count;
    public long Started { get; private set; }
    public long Refused { get; private set; }
    public long Delivered { get; private set; }

    /// <summary>
    /// Curl_Begin_ToMemory_POST. True if the request was taken: its outcome will come through
    /// <see cref="Deliver"/>. False is uri_get's 0, and no callback follows.
    /// </summary>
    /// <param name="id">The program's own number for the request, given back with the outcome.</param>
    /// <param name="postContentType">Null for a GET.</param>
    public bool Begin(string url, float id, string? postContentType = null, byte[]? postBody = null)
    {
        if (!Available) return false;   // "if(!curl_dll || !curl_enabled.integer) return false", silently
        ArgumentNullException.ThrowIfNull(url);
        string urlText = LegacyPackageDownloads.InsertServerAddress(url, ServerHost);
        // "first verify the URL scheme (so one can't read local files using file://)"
        if (urlText.Length > 1024 || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? address) || HttpPackageFetcher.CheckUrl(address) is not null)
            return Refuse($"Curl_Begin(\"{Shown(urlText)}\"): nasty URL scheme rejected");
        if (postContentType is not null)
        {
            // The C keeps 127 characters of the type; one that would break out of its header line is not sent at all.
            if (postContentType.Length > 127) postContentType = postContentType[..127];
            foreach (char c in postContentType)
                if (c < ' ' || c == 0x7F) return Refuse($"uri_get: \"{Shown(urlText)}\" refused: the content type holds a control character");
            if ((postBody?.Length ?? 0) > _limits.MaxPostBytes)
                return Refuse($"uri_get: \"{Shown(urlText)}\" refused: {postBody!.Length} bytes to post (the limit is {_limits.MaxPostBytes})");
        }
        if (_items.Count >= _limits.MaxPending)
            return Refuse($"uri_get: \"{Shown(urlText)}\" refused: {_items.Count} requests of the program are already unanswered");
        if (!TakeToken())
            return Refuse($"uri_get: \"{Shown(urlText)}\" refused: the program starts requests faster than one every {_limits.RefillSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");

        _items.Add(new Item
        {
            Id = id,
            Request = new LegacyUriRequest
            {
                Url = address, PostContentType = postContentType, PostBody = postContentType is null ? null : postBody ?? Array.Empty<byte>(),
                MaxResponseBytes = Math.Clamp(_limits.MaxResponseBytes, 1, 16384), MaxRedirects = _limits.MaxRedirects,
                ConnectTimeoutSeconds = _limits.ConnectTimeoutSeconds, TotalTimeoutSeconds = _limits.TotalTimeoutSeconds,
                // dpsnprintf(di->referer, ..., "dp://%s/", cls.netcon ? cls.netcon->address : "notconnected.invalid")
                Referer = ServerHost.Length == 0 ? "dp://notconnected.invalid/" : $"dp://{(ServerHost.Contains(':') && !ServerHost.StartsWith('[') ? "[" + ServerHost + "]" : ServerHost)}:{ServerPort}/",
                UserAgent = _limits.UserAgent, AllowPrivateHosts = ServerIsPrivate,
            },
        });
        Started++;
        Pump();
        return true;
    }

    private bool Refuse(string why)
    {
        Refused++;
        // A program can ask every frame; the log is told the first few times.
        if (_refusalsPrinted < 8)
        {
            _refusalsPrinted++;
            Print(why + (_refusalsPrinted == 8 ? " (further refusals are counted, not printed)" : "") + "\n");
        }
        return false;
    }

    private bool TakeToken()
    {
        if (_limits.Burst <= 0) return true;
        double now = Clock?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (double.IsNaN(_tokensAt)) _tokensAt = now;
        _tokens = Math.Min(_limits.Burst, _tokens + Math.Max(0, now - _tokensAt) / Math.Max(0.001, _limits.RefillSeconds));
        _tokensAt = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }

    // CheckPendingDownloads: "up to a maximum number of curl_maxdownloads are running".
    private void Pump()
    {
        int running = 0;
        foreach (Item item in _items)
            if (item.Task is { IsCompleted: false }) running++;
        foreach (Item item in _items)
        {
            if (running >= Math.Max(1, _limits.MaxConcurrent)) break;
            if (item.Task is not null) continue;
            ILegacyUriFetcher fetcher = _fetcher;
            LegacyUriRequest request = item.Request;
            CancellationToken cancel = item.Cancel.Token;
            // Task.Run: whatever a fetcher does before its first await happens off the session's thread too.
            item.Task = Task.Run(() => fetcher.FetchAsync(request, cancel));
            running++;
        }
    }

    /// <summary>
    /// Once a frame, at a point where the program may be called: every finished request is handed to
    /// <paramref name="callback"/> (id, status, reply text) in the order the requests were started, and
    /// waiting ones are started. Returns how many were delivered.
    /// </summary>
    public int Deliver(Action<float, int, string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _items.Count == 0) return 0;
        // Taken out of the list first: the callback runs the program, which may start requests of its own.
        List<(float Id, LegacyUriResult Result, Uri Url)>? done = null;
        for (int i = 0; i < _items.Count; i++)
        {
            Item item = _items[i];
            if (item.Task is not { IsCompleted: true } task) continue;
            LegacyUriResult result;
            if (task.IsCompletedSuccessfully) result = task.Result;
            else
            {
                // A fetcher that threw after all (they are not meant to): "should never happen".
                result = new LegacyUriResult(LegacyUriStatus.Unknown, Array.Empty<byte>(), task.Exception?.GetBaseException().Message ?? "cancelled");
            }
            (done ??= new()).Add((item.Id, result, item.Request.Url));
            item.Cancel.Dispose();
            _items.RemoveAt(i--);
        }
        Pump();
        if (done is null) return 0;
        foreach ((float id, LegacyUriResult result, Uri url) in done)
        {
            if (result.Error is not null) Print($"uri_get: \"{Shown(url.OriginalString)}\" ended with status {result.Status}: {LegacyPackValidator.Printable(result.Error, 200)}\n");
            Delivered++;
            callback(id, result.Status, Text(result.Body, _limits.MaxResponseBytes));
        }
        return done.Count;
    }

    // "handle->buffer[length_received] = '\0'": a C string, so it ends at the first zero byte, and a reply
    // that filled the buffer loses its last byte. Decoded as UTF-8, as every string the VM is given is.
    private static string Text(byte[] body, int bufferSize)
    {
        int length = Math.Min(body.Length, Math.Max(1, bufferSize) - 1);
        int zero = Array.IndexOf(body, (byte)0, 0, length);
        if (zero >= 0) length = zero;
        return Encoding.UTF8.GetString(body, 0, length);
    }

    /// <summary>
    /// The program was unloaded (a new level, the end of the session): its running requests are cancelled and
    /// nothing of them is delivered - "curl reply came too late... so just drop it".
    /// </summary>
    public void CancelAll()
    {
        foreach (Item item in _items)
        {
            item.Cancel.Cancel();
            if (item.Task is { IsCompleted: false } task)
            {
                _abandoned.Add(task);
                CancellationTokenSource source = item.Cancel;
                task.ContinueWith(_ => source.Dispose(), TaskScheduler.Default);
            }
            else item.Cancel.Dispose();
        }
        _items.Clear();
        _abandoned.RemoveAll(t => t.IsCompleted);
        _refusalsPrinted = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelAll();
        _disposed = true;
        // Cancelled transfers stop at their next await; not waited for longer than a moment.
        try { Task.WaitAll(_abandoned.ToArray(), 2000); }
        catch (AggregateException) { }
        _abandoned.Clear();
    }

    // What a log may show of an address: no credentials, no query (a query can carry a key), no control characters.
    private static string Shown(string url)
    {
        int query = url.IndexOf('?');
        if (query >= 0) url = url[..query] + "?...";
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            int at = url.IndexOf('@', scheme + 3), slash = url.IndexOf('/', scheme + 3);
            if (at >= 0 && (slash < 0 || at < slash)) url = string.Concat(url.AsSpan(0, scheme + 3), url.AsSpan(at + 1));
        }
        return LegacyPackValidator.Printable(url, 200);
    }
}
