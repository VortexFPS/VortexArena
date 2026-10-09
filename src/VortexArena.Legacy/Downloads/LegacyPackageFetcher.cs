// What libcurl does for Base/darkplaces/libcurl.c CheckPendingDownloads (a GET with a Referer and a
// User-Agent, redirects followed, http and https only, given up when the transfer stalls), done with
// HttpClient on a worker. The address is one a game server chose, so every step is bounded: the number of
// redirects, the size, the time without progress, and which hosts may be asked at all.
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace VortexArena.Legacy.Downloads;

public sealed class LegacyFetchRequest
{
    public required Uri Url { get; init; }
    /// <summary>The file to write. It is created or truncated; on failure it is deleted.</summary>
    public required string TargetPath { get; init; }
    /// <summary>The largest body accepted, in bytes.</summary>
    public long MaxBytes { get; init; } = 512L << 20;
    /// <summary>Bytes per second, 0 for no limit (curl_maxspeed, --maxspeed).</summary>
    public double MaxBytesPerSecond { get; init; }
    public int MaxRedirects { get; init; } = 5;
    /// <summary>Seconds to wait for a connection and for the response headers.</summary>
    public double ConnectTimeoutSeconds { get; init; } = 15;
    /// <summary>Seconds without a single byte after which the transfer is given up (libcurl's low speed time, 45).</summary>
    public double StallTimeoutSeconds { get; init; } = 45;
    /// <summary>"dp://host:port/": DarkPlaces names the game server as the referer.</summary>
    public string? Referer { get; init; }
    /// <summary>Null or empty sends none.</summary>
    public string? UserAgent { get; init; }
    /// <summary>
    /// Whether the host may be on this machine or on a private network (loopback, 10/8, 172.16/12,
    /// 192.168/16, link-local, unique-local). The owner says yes only when the game server itself is at
    /// such an address: a server on the internet must not be able to make its players' clients send
    /// requests to their routers and to services on their own machines.
    /// </summary>
    public bool AllowPrivateHosts { get; init; }
}

/// <summary>What a transfer has done so far. Written by the worker, read by anyone.</summary>
public sealed class LegacyFetchProgress
{
    private long _received, _total = -1, _startedTicks, _lastTicks;
    public long Received => Interlocked.Read(ref _received);
    /// <summary>The size the server announced (Content-Length), or -1.</summary>
    public long Total => Interlocked.Read(ref _total);
    /// <summary>Bytes per second since the body began, as libcurl's CURLINFO_SPEED_DOWNLOAD.</summary>
    public double BytesPerSecond
    {
        get
        {
            long started = Interlocked.Read(ref _startedTicks), last = Interlocked.Read(ref _lastTicks);
            if (started == 0 || last <= started) return 0;
            return Received / System.Diagnostics.Stopwatch.GetElapsedTime(started, last).TotalSeconds;
        }
    }
    /// <summary>The received share, 0 to 1, or -1 while the size is not known.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : -1;
    public bool Started => Interlocked.Read(ref _startedTicks) != 0;

    internal void Begin(long total)
    {
        Interlocked.Exchange(ref _total, total);
        Interlocked.Exchange(ref _received, 0);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _startedTicks, now);
        Interlocked.Exchange(ref _lastTicks, now);
    }

    internal void Add(int bytes)
    {
        Interlocked.Add(ref _received, bytes);
        Interlocked.Exchange(ref _lastTicks, System.Diagnostics.Stopwatch.GetTimestamp());
    }
}

public interface ILegacyPackageFetcher
{
    /// <summary>Fetches the URL into the file. Returns null when the whole body is in the file, else the
    /// reason it is not (the file is then gone). Never throws for anything a server or a network can do.</summary>
    Task<string?> FetchAsync(LegacyFetchRequest request, LegacyFetchProgress progress, CancellationToken cancel);
}

public sealed class HttpPackageFetcher : ILegacyPackageFetcher
{
    public async Task<string?> FetchAsync(LegacyFetchRequest request, LegacyFetchProgress progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        string? error;
        try { error = await Fetch(request, progress, cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) { error = cancel.IsCancellationRequested ? "the download was cancelled" : "the transfer stalled and was given up"; }
        catch (HttpRequestException e) { error = "the request failed: " + Describe(e); }
        catch (IOException e) { error = "the transfer broke off: " + e.Message; }
        catch (SocketException e) { error = "the connection failed: " + e.Message; }
        catch (UnauthorizedAccessException e) { error = "the download could not be written: " + e.Message; }
        catch (InvalidOperationException e) { error = "the request failed: " + e.Message; }
        if (error is not null) TryDelete(request.TargetPath);
        return error;
    }

    private static string Describe(HttpRequestException e) =>
        e.InnerException is { } inner && inner.Message.Length > 0 && !e.Message.Contains(inner.Message, StringComparison.Ordinal) ? e.Message + " (" + inner.Message + ")" : e.Message;

    private static async Task<string?> Fetch(LegacyFetchRequest request, LegacyFetchProgress progress, CancellationToken cancel)
    {
        if (CheckUrl(request.Url) is { } bad) return bad;
        using SocketsHttpHandler handler = new()
        {
            // Redirects are followed here, one at a time, so that each address is checked like the first.
            AllowAutoRedirect = false,
            // No "Accept-Encoding": a body is taken as it is sent, so the size limit is a limit on what is stored.
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            // The request goes to the host that was checked, not through whatever proxy the machine is set up with.
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(request.ConnectTimeoutSeconds, 1, 120)),
            MaxResponseHeadersLength = 32,   // kilobytes
            MaxConnectionsPerServer = 2,
            ConnectCallback = (context, token) => Connect(context.DnsEndPoint, request.AllowPrivateHosts, token),
        };
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        Uri url = request.Url;
        for (int hop = 0; ; hop++)
        {
            using HttpRequestMessage message = new(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(request.UserAgent)) message.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
            if (!string.IsNullOrEmpty(request.Referer)) message.Headers.TryAddWithoutValidation("Referer", request.Referer);
            message.Headers.TryAddWithoutValidation("Accept", "*/*");

            using CancellationTokenSource headers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            headers.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.ConnectTimeoutSeconds, 1, 120) + 5));
            HttpResponseMessage response;
            try { response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return "the server at " + url.Host + " did not answer in time"; }
            using (response)
            {
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= request.MaxRedirects) return $"more than {request.MaxRedirects} redirects";
                    Uri? location = response.Headers.Location;
                    if (location is null) return "a redirect without a destination";
                    if (!location.IsAbsoluteUri) location = new Uri(url, location);
                    if (CheckUrl(location) is { } badTarget) return "redirected to an address that is not allowed: " + badTarget;
                    url = location;
                    continue;
                }
                // 4xx and 5xx are libcurl.c's CURL_DOWNLOAD_SERVERERROR; anything else that is not the file is refused too.
                if (status != 200) return $"the server answered {status} {response.ReasonPhrase}".TrimEnd();

                long? announced = response.Content.Headers.ContentLength;
                if (announced is { } length && length > request.MaxBytes)
                    return $"the file is {length} bytes (the limit is {request.MaxBytes})";
                progress.Begin(announced ?? -1);

                await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using FileStream file = new(request.TargetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                byte[] buffer = new byte[1 << 16];
                long received = 0;
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                TimeSpan stall = TimeSpan.FromSeconds(Math.Clamp(request.StallTimeoutSeconds, 1, 600));
                using CancellationTokenSource stalled = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                for (;;)
                {
                    stalled.CancelAfter(stall);
                    int n = await body.ReadAsync(buffer, stalled.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    received += n;
                    if (received > request.MaxBytes) return $"the file is larger than {request.MaxBytes} bytes (the limit)";
                    await file.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    progress.Add(n);
                    if (request.MaxBytesPerSecond > 0)
                    {
                        // Not ahead of the limit: wait until this many bytes are due.
                        double due = received / request.MaxBytesPerSecond, elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds;
                        if (due > elapsed) await Task.Delay(TimeSpan.FromSeconds(Math.Min(due - elapsed, 5)), cancel).ConfigureAwait(false);
                    }
                }
                if (received == 0) return "the server sent an empty file";
                if (announced is { } expected && received != expected) return $"the transfer ended after {received} of {expected} bytes";
                return null;
            }
        }
    }

    /// <summary>"nasty URL scheme rejected": http and https only (no ftp, no file), no credentials in the
    /// address, and a length DarkPlaces itself could hold.</summary>
    public static string? CheckUrl(Uri url)
    {
        if (!url.IsAbsoluteUri) return "not an absolute address";
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return $"the scheme \"{url.Scheme}\" is not http or https";
        if (url.OriginalString.Length > 1024) return "the address is longer than 1024 characters";
        if (url.UserInfo.Length != 0) return "the address carries a user name or password";
        if (url.Host.Length == 0) return "the address names no host";
        return null;
    }

    internal static async ValueTask<Stream> Connect(DnsEndPoint endPoint, bool allowPrivate, CancellationToken cancel)
    {
        // Resolved here and connected to by number, so the address that was checked is the address that is used.
        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out IPAddress? literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancel).ConfigureAwait(false);
        Exception? last = null;
        bool refused = false;
        foreach (IPAddress candidate in addresses)
        {
            IPAddress address = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
            if (!allowPrivate && IsPrivateAddress(address))
            {
                refused = true;
                continue;
            }
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), cancel).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = e;
                if (e is OperationCanceledException) throw;
            }
        }
        if (refused && last is null)
            throw new HttpRequestException($"\"{endPoint.Host}\" is an address on this machine or on a private network, and the game server is not: such a download is refused");
        throw new HttpRequestException($"no connection to {endPoint.Host}:{endPoint.Port}" + (last is null ? "" : " (" + last.Message + ")"));
    }

    /// <summary>Loopback, private, link-local, carrier-NAT, multicast and unspecified addresses.</summary>
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] >= 224;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any);
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
