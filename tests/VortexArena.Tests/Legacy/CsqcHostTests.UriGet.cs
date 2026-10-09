using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Downloads;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The uri_get builtin (#513, DP_QC_URI_GET / DP_QC_URI_POST) and URI_Get_Callback as prvm_cmds.c and
/// libcurl.c have them, against an HTTP listener on 127.0.0.1 that the test runs itself, or against a
/// fetcher the test holds the strings of. The addresses are a client program's, so a server's: what is
/// refused and what is bounded is tested as much as what works. No test here contacts another machine.
/// </summary>
public partial class CsqcHostTests
{
    // ---- a very small HTTP server (as LegacyDownloadTests has one, plus the request's method and body) -----

    private sealed class UriReply
    {
        public int Status = 200;
        public byte[] Body = Array.Empty<byte>();
        public string? Location;
        /// <summary>Announce no Content-Length and end the reply by closing the connection.</summary>
        public bool Unannounced;
        /// <summary>Send the headers and then nothing more, keeping the connection open.</summary>
        public bool Stall;
    }

    private sealed record UriSeen(string Method, string Path, string Head, byte[] Body);

    private sealed class UriHttp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, UriReply> _handler;
        private readonly List<UriSeen> _seen = new();
        public int Port { get; }
        public string Base => $"http://127.0.0.1:{Port}/";

        public UriHttp(Func<string, UriReply> handler)
        {
            _handler = handler;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        public List<UriSeen> Seen
        {
            get { lock (_seen) return new List<UriSeen>(_seen); }
        }

        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    StringBuilder head = new();
                    byte[] one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16384)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) <= 0) return;
                        head.Append((char)one[0]);
                    }
                    string text = head.ToString();
                    string[] first = text.Split(' ');
                    int length = 0;
                    foreach (string line in text.Split("\r\n"))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                    byte[] body = new byte[length];
                    for (int got = 0; got < length;)
                    {
                        int n = await stream.ReadAsync(body.AsMemory(got), _stop.Token);
                        if (n <= 0) return;
                        got += n;
                    }
                    lock (_seen) _seen.Add(new UriSeen(first[0], first[1], text, body));
                    UriReply reply = _handler(first[1]);
                    StringBuilder response = new($"HTTP/1.1 {reply.Status} X\r\nConnection: close\r\n");
                    if (reply.Location is not null) response.Append("Location: ").Append(reply.Location).Append("\r\n");
                    if (!reply.Unannounced) response.Append("Content-Length: ").Append(reply.Stall ? 1000 : reply.Body.Length).Append("\r\n");
                    response.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    if (reply.Stall)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    }
                    await stream.WriteAsync(reply.Body, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>A fetcher whose requests end when the test says so.</summary>
    private sealed class GatedFetcher : ILegacyUriFetcher
    {
        public sealed record Call(LegacyUriRequest Request, TaskCompletionSource<LegacyUriResult> Done, CancellationToken Cancel);
        private readonly List<Call> _calls = new();
        private int _running;
        public int MaxRunning;
        /// <summary>A thread other than the one that started the request ran it.</summary>
        public volatile bool OffThread;
        public int StarterThread = Environment.CurrentManagedThreadId;

        public List<Call> Calls
        {
            get { lock (_calls) return new List<Call>(_calls); }
        }

        public async Task<LegacyUriResult> FetchAsync(LegacyUriRequest request, CancellationToken cancel)
        {
            if (Environment.CurrentManagedThreadId != StarterThread) OffThread = true;
            Call call = new(request, new TaskCompletionSource<LegacyUriResult>(TaskCreationOptions.RunContinuationsAsynchronously), cancel);
            lock (_calls)
            {
                _calls.Add(call);
                _running++;
                MaxRunning = Math.Max(MaxRunning, _running);
            }
            try { return await call.Done.Task.WaitAsync(cancel); }
            catch (OperationCanceledException) { return new LegacyUriResult(LegacyUriStatus.Aborted, Array.Empty<byte>(), "cancelled"); }
            finally
            {
                lock (_calls)
                {
                    _running--;
                    _finished++;
                }
            }
        }

        private int _finished;

        /// <summary>Waits until that many requests have ended, and a moment more for their tasks to be seen as ended.</summary>
        public void WaitForFinished(int count)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 10_000)
            {
                lock (_calls)
                    if (_finished >= count) break;
                Thread.Sleep(2);
            }
            Thread.Sleep(50);
        }

        /// <summary>The request for that path, once it has been started (two started together may arrive in either order).</summary>
        public Call WaitForPath(string path)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 10_000)
            {
                lock (_calls)
                    foreach (Call call in _calls)
                        if (call.Request.Url.AbsolutePath == path) return call;
                Thread.Sleep(2);
            }
            throw new TimeoutException($"the request for {path} was never started");
        }

        public Call WaitForCall(int index)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 10_000)
            {
                lock (_calls)
                    if (_calls.Count > index) return _calls[index];
                Thread.Sleep(2);
            }
            throw new TimeoutException($"request {index} was never started");
        }
    }

    // ---- the program -----------------------------------------------------------------------------------------

    /// <summary>
    /// Inputs: the globals "url", "id", "ctype", "delim", "buf", "keyid". A builtin's answer lands in
    /// "result" (raw: uri_get returns the integer 1). URI_Get_Callback keeps what it was told in "cb_id",
    /// "cb_status", "cb_data" and counts in "cb_count"; with "cb_again" set it starts one more request.
    /// </summary>
    private static Asm UriProgram(bool withCallback = true)
    {
        Asm a = new();
        int url = a.S("url"), id = a.F("id"), result = a.F("result"), type = a.S("ctype"), delim = a.S("delim"), buf = a.F("buf", -1), key = a.F("keyid", -1);
        int cbId = a.F("cb_id"), cbStatus = a.F("cb_status", -99), cbData = a.S("cb_data"), cbCount = a.F("cb_count"), again = a.F("cb_again");
        int one = a.Const(1), zero = a.Const(0), two = a.Const(2);
        if (withCallback)
        {
            a.Begin("URI_Get_Callback");
            a.Parm(0, cbId);
            a.Parm(1, cbStatus);
            a.Get(118, cbData, ProgsFile.OfsParm0 + 6);   // strzone(data): the temp string dies with the call
            a.B.Emit(QcOp.AddF, cbCount, one, cbCount);
            int skip = a.B.Emit(QcOp.IfNot, again);
            a.Copy(zero, again);
            a.Get(513, result, url, id);
            a.B.PatchJump(skip, a.B.NextStatement);
            a.End();
        }
        a.Begin("get"); a.Get(513, result, url, id); a.End();
        a.Begin("post"); a.Get(513, result, url, id, type, delim); a.End();
        a.Begin("postbuf"); a.Get(513, result, url, id, type, delim, buf); a.End();
        a.Begin("postkey"); a.Get(513, result, url, id, type, delim, buf, key); a.End();
        a.Begin("oneparm"); a.Call(513, url); a.End();
        a.Begin("mkbuf");
        a.Get(460, buf);                                   // buf_create
        a.Call(467, buf, zero, a.Text("alpha"));           // bufstr_set; slot 1 stays empty
        a.Call(467, buf, two, a.Text("gamma"));
        a.End();
        a.Begin("ext_get"); a.Get(99, result, a.Text("DP_QC_URI_GET")); a.End();
        a.Begin("ext_post"); a.Get(99, result, a.Text("DP_QC_URI_POST")); a.End();
        a.Begin("ext_crypto"); a.Get(99, result, a.Text("DP_CRYPTO")); a.End();
        a.Begin("ext_sha256"); a.Get(99, result, a.Text("DP_QC_DIGEST_SHA256")); a.End();
        a.Begin("ext_escape"); a.Get(99, result, a.Text("DP_QC_URI_ESCAPE")); a.End();
        return a;
    }

    private static void SetS(Rig rig, string name, string value) =>
        rig.Host!.Vm.GlobalInt(rig.Host.Vm.FindGlobal(name)!.Offset) = rig.Host.Vm.EngineString(value);

    private static void Exec(Rig rig, string function) => rig.Host!.Vm.Execute(rig.Host.Vm.FindFunction(function));

    /// <summary>uri_get(url, id): true if the builtin answered 1 (the integer, as the C writes it).</summary>
    private static bool Get(Rig rig, string url, float id, string function = "get")
    {
        SetS(rig, "url", url);
        SetF(rig, "id", id);
        SetF(rig, "result", -99);
        Exec(rig, function);
        int raw = rig.GlobalInt("result");
        Assert.True(raw is 0 or 1, $"uri_get answered the bit pattern {raw}");
        return raw == 1;
    }

    /// <summary>Frames until the callback has been called <paramref name="count"/> times in all.</summary>
    private static void AwaitCallbacks(Rig rig, int count)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (rig.Global("cb_count") < count && watch.ElapsedMilliseconds < 20_000)
        {
            rig.Host!.DeliverUriReplies();
            Thread.Sleep(2);
        }
        Assert.Equal(count, rig.Global("cb_count"));
    }

    private static LegacyUriLimits TestLimits() => new() { Burst = 1000, MaxPending = 64, UserAgent = "test-agent", ConnectTimeoutSeconds = 5, TotalTimeoutSeconds = 10 };

    private static LegacyUriRequests Local(LegacyUriLimits? limits = null, ILegacyUriFetcher? fetcher = null, List<string>? log = null)
    {
        LegacyUriRequests requests = new(limits ?? TestLimits(), fetcher) { ServerHost = "127.0.0.1", ServerPort = 26000, ServerIsPrivate = true };
        if (log is not null) requests.Print = log.Add;
        return requests;
    }

    private static int ClosedPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ---- what works --------------------------------------------------------------------------------------------

    [Fact]
    public void UriGet_FetchesAndTellsTheCallbackOnce_WithDarkPlacesStatus()
    {
        using UriHttp http = new(path => path switch
        {
            "/ok?x=1" => new UriReply { Body = Encoding.UTF8.GetBytes("hello w\u00F6rld") },
            "/empty" => new UriReply(),
            "/missing" => new UriReply { Status = 404, Body = Encoding.ASCII.GetBytes("not here") },
            "/error" => new UriReply { Status = 500, Body = Encoding.ASCII.GetBytes("oops") },
            "/nul" => new UriReply { Body = new byte[] { (byte)'a', (byte)'b', 0, (byte)'c' } },
            "/hop" => new UriReply { Status = 302, Location = "/hop2" },
            "/hop2" => new UriReply { Status = 301, Location = "/empty" },
            _ => new UriReply { Status = 404 },
        });
        using Rig rig = new();
        using LegacyUriRequests requests = Local();
        CsqcHost host = rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });

        Assert.True(Get(rig, http.Base + "ok?x=1", 41));
        // Nothing reaches the program until the owner of the frame hands it over.
        Assert.Equal(0, rig.Global("cb_count"));
        AwaitCallbacks(rig, 1);
        Assert.Equal(41, rig.Global("cb_id"));
        Assert.Equal(0, rig.Global("cb_status"));
        Assert.Equal("hello w\u00F6rld", rig.GlobalString("cb_data"));
        UriSeen seen = Assert.Single(http.Seen);
        Assert.Equal("GET", seen.Method);
        Assert.Contains("Referer: dp://127.0.0.1:26000/", seen.Head);   // libcurl.c: the game server
        Assert.Contains("User-Agent: test-agent", seen.Head);
        Assert.DoesNotContain("Accept-Encoding", seen.Head);
        Assert.DoesNotContain("Cookie", seen.Head);

        // Once each, whatever the outcome; the id is the program's own and need not be whole.
        (string Path, float Id, int Status, string Data)[] cases =
        {
            ("empty", 2.5f, 0, ""), ("missing", 3, 404, "not here"), ("error", -4, 500, "oops"), ("nul", 5, 0, "ab"), ("hop", 6, 0, ""),
        };
        int count = 1;
        foreach ((string path, float id, int status, string data) in cases)
        {
            Assert.True(Get(rig, http.Base + path, id), path);
            AwaitCallbacks(rig, ++count);
            Assert.Equal(id, rig.Global("cb_id"));
            Assert.Equal(status, rig.Global("cb_status"));
            Assert.Equal(data, rig.GlobalString("cb_data"));
        }
        for (int i = 0; i < 20; i++) host.DeliverUriReplies();
        Assert.Equal(count, rig.Global("cb_count"));
        Assert.Equal(0, requests.Pending);
        Assert.Equal(count, requests.Delivered);
        Assert.False(host.Faulted);
    }

    [Fact]
    public void UriGet_AFailedRequest_IsStatusMinusOne()
    {
        byte[] full = Enumerable.Repeat((byte)'a', 16384).ToArray(), over = Enumerable.Repeat((byte)'b', 16385).ToArray();
        using UriHttp http = new(path => path switch
        {
            "/full" => new UriReply { Body = full },
            "/over" => new UriReply { Body = over },
            "/over-unannounced" => new UriReply { Body = Enumerable.Repeat((byte)'c', 40_000).ToArray(), Unannounced = true },
            "/stall" => new UriReply { Stall = true },
            "/loop" => new UriReply { Status = 302, Location = "/loop" },
            "/tofile" => new UriReply { Status = 302, Location = "file:///C:/Windows/win.ini" },
            "/toftp" => new UriReply { Status = 302, Location = "ftp://127.0.0.1/x" },
            "/nowhere" => new UriReply { Status = 302 },
            _ => new UriReply { Status = 404 },
        });
        List<string> log = new();
        LegacyUriLimits limits = TestLimits();
        limits.TotalTimeoutSeconds = 1;
        using Rig rig = new();
        using LegacyUriRequests requests = Local(limits, log: log);
        rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        int count = 0;

        // The buffer is DarkPlaces' MAX_INPUTLINE: 16384 bytes arrive, a string holds 16383 of them.
        Assert.True(Get(rig, http.Base + "full", 1));
        AwaitCallbacks(rig, ++count);
        Assert.Equal(0, rig.Global("cb_status"));
        Assert.Equal(new string('a', 16383), rig.GlobalString("cb_data"));

        // One byte more is libcurl's "buffer overrun": the transfer has failed.
        foreach (string path in new[] { "over", "over-unannounced", "stall", "loop", "tofile", "toftp", "nowhere" })
        {
            Assert.True(Get(rig, http.Base + path, 7), path);
            AwaitCallbacks(rig, ++count);
            Assert.Equal(-1, rig.Global("cb_status"));
        }
        Assert.Equal(1, http.Seen.Count(s => s.Path == "/over"));

        // Nobody listening.
        Assert.True(Get(rig, $"http://127.0.0.1:{ClosedPort()}/x", 8));
        AwaitCallbacks(rig, ++count);
        Assert.Equal(-1, rig.Global("cb_status"));
        Assert.Equal("", rig.GlobalString("cb_data"));

        // The log says why, and never shows a query.
        Assert.Contains(log, l => l.Contains("larger than the buffer"));
        Assert.Contains(log, l => l.Contains("took too long"));
        Assert.True(Get(rig, http.Base + "secret?key=hunter2", 9));
        AwaitCallbacks(rig, ++count);
        Assert.Equal(404, rig.Global("cb_status"));
        Assert.DoesNotContain(log, l => l.Contains("hunter2"));
        Assert.False(rig.Host!.Faulted);
    }

    [Fact]
    public void UriGet_Post_SendsTheDelimiterOrTheImplodedBuffer()
    {
        using UriHttp http = new(path => path switch
        {
            "/see-other" => new UriReply { Status = 303, Location = "/landed" },
            "/temporary" => new UriReply { Status = 307, Location = "/landed" },
            _ => new UriReply { Body = Encoding.ASCII.GetBytes("posted") },
        });
        using Rig rig = new();
        using LegacyUriRequests requests = Local();
        rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        SetS(rig, "ctype", "text/plain");
        SetS(rig, "delim", "\n");
        int count = 0;

        // Four arguments: "postdata" is the delimiter itself.
        Assert.True(Get(rig, http.Base + "a", 1, "post"));
        AwaitCallbacks(rig, ++count);
        Assert.Equal(0, rig.Global("cb_status"));
        Assert.Equal("posted", rig.GlobalString("cb_data"));
        UriSeen first = http.Seen[^1];
        Assert.Equal("POST", first.Method);
        Assert.Contains("Content-Type: text/plain", first.Head);
        Assert.Equal("\n", Encoding.UTF8.GetString(first.Body));

        // With a string buffer: its strings joined by the delimiter, an empty slot giving an empty string.
        Exec(rig, "mkbuf");
        SetS(rig, "delim", "&");
        Assert.True(Get(rig, http.Base + "b?q=1", 2, "postbuf"));
        AwaitCallbacks(rig, ++count);
        Assert.Equal("alpha&&gamma", Encoding.UTF8.GetString(http.Seen[^1].Body));

        // A key number asks for a d0_blind_id signature. There is no such library here, and DarkPlaces
        // without it sends the request unsigned.
        SetF(rig, "keyid", 0);
        Assert.True(Get(rig, http.Base + "c", 3, "postkey"));
        AwaitCallbacks(rig, ++count);
        Assert.Equal("alpha&&gamma", Encoding.UTF8.GetString(http.Seen[^1].Body));
        Assert.DoesNotContain("X-D0-Blind-ID", http.Seen[^1].Head);

        // An empty content type is a GET, whatever else is given.
        SetS(rig, "ctype", "");
        Assert.True(Get(rig, http.Base + "d", 4, "postbuf"));
        AwaitCallbacks(rig, ++count);
        Assert.Equal("GET", http.Seen[^1].Method);
        SetS(rig, "ctype", "application/x-www-form-urlencoded");

        // libcurl: a POST redirected by 303 arrives as a GET, by 307 as the same POST.
        Assert.True(Get(rig, http.Base + "see-other", 5, "postbuf"));
        AwaitCallbacks(rig, ++count);
        Assert.Equal(("GET", "/landed"), (http.Seen[^1].Method, http.Seen[^1].Path));
        Assert.True(Get(rig, http.Base + "temporary", 6, "postbuf"));
        AwaitCallbacks(rig, ++count);
        Assert.Equal(("POST", "/landed"), (http.Seen[^1].Method, http.Seen[^1].Path));
        Assert.Equal("alpha&&gamma", Encoding.UTF8.GetString(http.Seen[^1].Body));

        // A buffer that does not exist: a warning, no request, no callback.
        int requestsSeen = http.Seen.Count;
        SetF(rig, "buf", 4242);
        SetS(rig, "url", http.Base + "e");
        Exec(rig, "postbuf");
        Assert.Contains(rig.Warnings, w => w.Contains("uri_get: invalid buffer 4242"));
        // A content type that would start a header line of its own is not sent.
        SetF(rig, "buf", -1);
        SetS(rig, "ctype", "text/plain\r\nX-Injected: 1");
        Assert.False(Get(rig, http.Base + "f", 8, "post"));
        for (int i = 0; i < 50; i++) { rig.Host!.DeliverUriReplies(); Thread.Sleep(2); }
        Assert.Equal(requestsSeen, http.Seen.Count);
        Assert.Equal(count, rig.Global("cb_count"));
        Assert.False(rig.Host!.Faulted);
    }

    // ---- what is refused ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ftp://127.0.0.1/file")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("gopher://127.0.0.1/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hello")]
    [InlineData("\\\\127.0.0.1\\share\\x")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("127.0.0.1/x")]
    [InlineData("")]
    [InlineData("http://user:secret@127.0.0.1/x")]
    [InlineData("http://")]
    public void UriGet_OnlyHttpAndHttps_AnythingElseIsNotStarted(string url)
    {
        GatedFetcher fetcher = new();
        List<string> log = new();
        using Rig rig = new();
        using LegacyUriRequests requests = Local(fetcher: fetcher, log: log);
        rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        Assert.False(Get(rig, url, 1));
        Assert.Equal(0, requests.Pending);
        Assert.Equal(1, requests.Refused);
        Thread.Sleep(20);
        Assert.Empty(fetcher.Calls);
        Assert.Contains(log, l => l.Contains("nasty URL scheme rejected"));
        Assert.DoesNotContain(log, l => l.Contains("secret"));
        Assert.Equal(0, rig.Global("cb_count"));
    }

    [Fact]
    public void UriGet_AnAddressLongerThanDarkPlacesHolds_IsNotStarted_AndTheServersAddressIsFilledIn()
    {
        GatedFetcher fetcher = new();
        using Rig rig = new();
        using LegacyUriRequests requests = Local(fetcher: fetcher);
        rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        Assert.False(Get(rig, "http://127.0.0.1/" + new string('a', 1100), 1));

        // "if URL is protocol:///* or protocol://:port/*, insert the IP of the current server"
        Assert.True(Get(rig, "http:///stats/x", 2));
        Assert.True(Get(rig, "https://:8443/y", 3));
        Assert.Equal("http://127.0.0.1/stats/x", fetcher.WaitForPath("/stats/x").Request.Url.AbsoluteUri);
        Assert.Equal("https://127.0.0.1:8443/y", fetcher.WaitForPath("/y").Request.Url.AbsoluteUri);
        Assert.True(fetcher.Calls[0].Request.AllowPrivateHosts);
        Assert.Equal(16384, fetcher.Calls[0].Request.MaxResponseBytes);
        Assert.Null(fetcher.Calls[0].Request.PostContentType);
    }

    [Fact]
    public void UriGet_AServerOnTheInternet_CannotReachThisMachineOrAPrivateNetwork()
    {
        using UriHttp http = new(_ => new UriReply { Body = Encoding.ASCII.GetBytes("the router's admin page") });
        List<string> log = new();
        using Rig rig = new();
        // The game server is somewhere public: 127.0.0.1 is then not a place its program may ask.
        using LegacyUriRequests requests = new(TestLimits()) { ServerHost = "203.0.113.5", ServerIsPrivate = false, Print = log.Add };
        rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        int count = 0;
        foreach (string url in new[] { http.Base + "admin", $"http://localhost:{http.Port}/admin", $"http://[::1]:{http.Port}/admin", $"http://[::ffff:127.0.0.1]:{http.Port}/admin", $"http://0.0.0.0:{http.Port}/admin" })
        {
            // The request is taken (the address is well formed) and fails where it would connect.
            Assert.True(Get(rig, url, 1), url);
            AwaitCallbacks(rig, ++count);
            Assert.Equal(-1, rig.Global("cb_status"));
            Assert.Equal("", rig.GlobalString("cb_data"));
        }
        Assert.Empty(http.Seen);
        Assert.Contains(log, l => l.Contains("private network"));

        // The fetcher on its own, through a redirect: a public-looking first hop cannot be tested without a
        // public host, so the rule is shown from the other side - with private addresses allowed the
        // redirect is followed, and the same request without the allowance never connects.
        using UriHttp redirecting = new(_ => new UriReply { Status = 302, Location = http.Base + "admin" });
        LegacyUriResult allowed = new HttpUriFetcher().FetchAsync(new LegacyUriRequest { Url = new Uri(redirecting.Base + "r"), AllowPrivateHosts = true, TotalTimeoutSeconds = 10 }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(0, allowed.Status);
        Assert.Single(http.Seen);
        LegacyUriResult refused = new HttpUriFetcher().FetchAsync(new LegacyUriRequest { Url = new Uri(redirecting.Base + "r"), AllowPrivateHosts = false, TotalTimeoutSeconds = 10 }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(LegacyUriStatus.Failed, refused.Status);
        Assert.Single(http.Seen);
        Assert.Single(redirecting.Seen);
    }

    [Fact]
    public void UriGet_SwitchedOff_TheExtensionsAreNotThere_AndNothingIsAsked()
    {
        GatedFetcher fetcher = new();
        LegacyUriLimits off = TestLimits();
        off.Enabled = false;   // legacy_uri_get_enabled 0
        using (Rig rig = new())
        using (LegacyUriRequests requests = Local(off, fetcher))
        {
            rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
            Assert.False(requests.Available);
            foreach (string check in new[] { "ext_get", "ext_post" })
            {
                Exec(rig, check);
                Assert.Equal(0, rig.Global("result"));   // so Xonotic's "Engine lacks HTTP support" is printed by the program
            }
            Assert.False(Get(rig, "http://127.0.0.1/x", 1));
            Assert.False(Get(rig, "http://127.0.0.1/x", 1, "post"));
            Thread.Sleep(20);
            Assert.Empty(fetcher.Calls);
            Assert.Equal(0, rig.Global("cb_count"));
        }

        // Switched on: both extensions, as checkextension's "return Curl_Available()".
        using (Rig rig = new())
        using (LegacyUriRequests requests = Local(fetcher: fetcher))
        {
            rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
            foreach (string check in new[] { "ext_get", "ext_post", "ext_escape" })
            {
                Exec(rig, check);
                Assert.Equal(1, rig.Global("result"));
            }
            // d0_blind_id is not here, with HTTP or without: no player identity, no SHA-256 digest.
            foreach (string check in new[] { "ext_crypto", "ext_sha256" })
            {
                Exec(rig, check);
                Assert.Equal(0, rig.Global("result"));
            }
            Assert.True(rig.Host!.Vm.HasBuiltin(510) && rig.Host.Vm.HasBuiltin(511) && rig.Host.Vm.HasBuiltin(513));
            // DarkPlaces' client builtin table has no crypto_* builtins either (#633 to #637 and #641 are empty
            // there; they are the menu's): a program that calls one is counted, not faulted.
            foreach (int number in new[] { 633, 634, 635, 636, 637, 641 }) Assert.False(rig.Host.Vm.HasBuiltin(number), "#" + number);
            Assert.Equal(new[] { "DP_CRYPTO", "DP_QC_DIGEST_SHA256" }, rig.Host.ExtensionChecks.Where(kv => !kv.Value).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal));
        }

        // No HTTP client at all (a host that was given none): as DarkPlaces without libcurl.
        using (Rig rig = new())
        {
            rig.Load(UriProgram());
            foreach (string check in new[] { "ext_get", "ext_post", "ext_crypto" })
            {
                Exec(rig, check);
                Assert.Equal(0, rig.Global("result"));
            }
            Assert.False(Get(rig, "http://127.0.0.1/x", 1));
            Assert.Equal(0, rig.Host!.DeliverUriReplies());
        }
    }

    [Fact]
    public void UriGet_WithoutACallbackFunction_OrWithOneArgument_IsAProgramFault()
    {
        using Rig rig = new();
        using LegacyUriRequests requests = Local(fetcher: new GatedFetcher());
        rig.Load(UriProgram(withCallback: false), new CsqcHostOptions { UriRequests = requests });
        SetS(rig, "url", "http://127.0.0.1/x");
        QcRuntimeException e = Assert.Throws<QcRuntimeException>(() => Exec(rig, "get"));
        Assert.Contains("uri_get called by client without URI_Get_Callback defined", e.Message);
        Assert.Equal(0, requests.Started);

        using Rig second = new();
        second.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        e = Assert.Throws<QcRuntimeException>(() => Exec(second, "oneparm"));
        Assert.Contains("VM_uri_get wrong parameter count", e.Message);
    }

    // ---- what is bounded ---------------------------------------------------------------------------------------

    [Fact]
    public void UriGet_RunsOffTheThread_AFewAtOnce_AndRefusesMoreThanItWillHold()
    {
        GatedFetcher fetcher = new();
        LegacyUriLimits limits = TestLimits();
        limits.MaxConcurrent = 2;
        limits.MaxPending = 4;
        List<string> log = new();
        using Rig rig = new();
        using LegacyUriRequests requests = Local(limits, fetcher, log);
        rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });

        for (int i = 1; i <= 4; i++) Assert.True(Get(rig, "http://127.0.0.1/r" + i, i));
        // The fifth is uri_get's 0: four are unanswered.
        for (int i = 5; i <= 30; i++) Assert.False(Get(rig, "http://127.0.0.1/r" + i, i));
        Assert.Equal(4, requests.Pending);
        Assert.Equal(26, requests.Refused);
        // A program that asks every frame fills no log.
        Assert.Equal(8, log.Count);
        Assert.Contains("further refusals are counted", log[^1]);

        fetcher.WaitForCall(1);
        Thread.Sleep(50);
        Assert.Equal(2, fetcher.Calls.Count);   // two run, two wait
        Assert.True(fetcher.OffThread);

        // The second finishes first; it is delivered, and a waiting request takes its place.
        fetcher.WaitForPath("/r2").Done.SetResult(new LegacyUriResult(0, Encoding.ASCII.GetBytes("two"), null));
        AwaitCallbacks(rig, 1);
        Assert.Equal(2, rig.Global("cb_id"));
        Assert.Equal("two", rig.GlobalString("cb_data"));
        fetcher.WaitForPath("/r3");
        Assert.Equal(3, fetcher.Calls.Count);
        Assert.True(Get(rig, "http://127.0.0.1/r31", 31));
        Assert.False(Get(rig, "http://127.0.0.1/r32", 32));

        // Two that finish before the same frame, the later one first: handed over in the order they were started.
        fetcher.WaitForPath("/r3").Done.SetResult(new LegacyUriResult(404, Encoding.ASCII.GetBytes("three"), "the server answered 404"));
        fetcher.WaitForFinished(2);
        fetcher.WaitForPath("/r1").Done.SetResult(new LegacyUriResult(0, Encoding.ASCII.GetBytes("one"), null));
        fetcher.WaitForFinished(3);
        Assert.Equal(2, rig.Host!.DeliverUriReplies());
        Assert.Equal(3, rig.Global("cb_count"));
        Assert.Equal(3, rig.Global("cb_id"));
        Assert.Equal(404, rig.Global("cb_status"));
        Assert.Equal("three", rig.GlobalString("cb_data"));

        fetcher.WaitForPath("/r4").Done.SetResult(new LegacyUriResult(-3, Array.Empty<byte>(), "x"));
        AwaitCallbacks(rig, 4);
        Assert.Equal(4, rig.Global("cb_id"));
        Assert.Equal(-3, rig.Global("cb_status"));
        fetcher.WaitForPath("/r31").Done.SetResult(new LegacyUriResult(0, Encoding.ASCII.GetBytes("last"), null));
        AwaitCallbacks(rig, 5);
        Assert.Equal(31, rig.Global("cb_id"));
        Assert.Equal("last", rig.GlobalString("cb_data"));
        Assert.Equal(2, fetcher.MaxRunning);
        Assert.Equal(0, requests.Pending);
    }

    [Fact]
    public void UriGet_AProgramMayStartOnlySoManyRequestsInARow()
    {
        GatedFetcher fetcher = new();
        LegacyUriLimits limits = TestLimits();
        limits.Burst = 3;
        limits.RefillSeconds = 5;
        double now = 100;
        using LegacyUriRequests requests = new(limits, fetcher) { Clock = () => now };
        for (int i = 0; i < 3; i++) Assert.True(requests.Begin("http://203.0.113.5/a", i));
        Assert.False(requests.Begin("http://203.0.113.5/a", 3));
        now += 4.9;
        Assert.False(requests.Begin("http://203.0.113.5/a", 4));
        now += 0.2;
        Assert.True(requests.Begin("http://203.0.113.5/a", 5));
        Assert.False(requests.Begin("http://203.0.113.5/a", 6));
        // A long quiet time buys no more than the burst.
        now += 10_000;
        for (int i = 0; i < 3; i++) Assert.True(requests.Begin("http://203.0.113.5/a", 10 + i));
        Assert.False(requests.Begin("http://203.0.113.5/a", 13));
        Assert.Equal(7, requests.Started);
        // Too much to post.
        now += 10_000;
        Assert.False(requests.Begin("http://203.0.113.5/a", 20, "text/plain", new byte[limits.MaxPostBytes + 1]));
        Assert.True(requests.Begin("http://203.0.113.5/a", 21, "text/plain", new byte[limits.MaxPostBytes]));
    }

    [Fact]
    public void UriGet_TheCallbackMayStartARequest_AndAFaultInItIsTheProgramsOwn()
    {
        GatedFetcher fetcher = new();
        using Rig rig = new();
        using LegacyUriRequests requests = Local(fetcher: fetcher);
        CsqcHost host = rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        Assert.True(Get(rig, "http://127.0.0.1/first", 1));
        SetF(rig, "cb_again", 1);
        SetS(rig, "url", "http://127.0.0.1/second");
        SetF(rig, "id", 2);
        fetcher.WaitForCall(0).Done.SetResult(new LegacyUriResult(0, Encoding.ASCII.GetBytes("one"), null));
        AwaitCallbacks(rig, 1);
        Assert.Equal(1, rig.Global("cb_id"));
        Assert.Equal(1, rig.GlobalInt("result"));   // the request the callback started
        Assert.Equal("/second", fetcher.WaitForCall(1).Request.Url.AbsolutePath);
        fetcher.Calls[1].Done.SetResult(new LegacyUriResult(0, Encoding.ASCII.GetBytes("two"), null));
        AwaitCallbacks(rig, 2);
        Assert.Equal(2, rig.Global("cb_id"));

        // A reply longer than a string: cut, not a fault. (A real fetcher never returns one.)
        Assert.True(Get(rig, "http://127.0.0.1/third", 3));
        fetcher.WaitForCall(2).Done.SetResult(new LegacyUriResult(0, Enumerable.Repeat((byte)'z', 100_000).ToArray(), null));
        AwaitCallbacks(rig, 3);
        Assert.Equal(16383, rig.GlobalString("cb_data").Length);
        Assert.False(host.Faulted);
    }

    [Fact]
    public void UriGet_RequestsEndWithTheProgram()
    {
        GatedFetcher fetcher = new();
        using Rig rig = new();
        LegacyUriRequests requests = Local(fetcher: fetcher);
        CsqcHost host = rig.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        Assert.True(Get(rig, "http://127.0.0.1/a", 1));
        Assert.True(Get(rig, "http://127.0.0.1/b", 2));
        Assert.True(Get(rig, "http://127.0.0.1/c", 3));   // waits behind the two that run
        GatedFetcher.Call first = fetcher.WaitForPath("/a"), second = fetcher.WaitForPath("/b");
        second.Done.SetResult(new LegacyUriResult(0, Encoding.ASCII.GetBytes("late"), null));
        Assert.False(first.Cancel.IsCancellationRequested);

        // The level ends: the program is unloaded. What ran is cancelled, what had finished is dropped
        // ("curl reply came too late... so just drop it"), what waited is never started.
        host.Shutdown();
        Assert.True(first.Cancel.IsCancellationRequested);
        Assert.Equal(0, requests.Pending);
        Assert.Equal(0, requests.Deliver((_, _, _) => Assert.Fail("a reply was delivered after the program was unloaded")));
        Assert.Equal(0, host.DeliverUriReplies());
        Thread.Sleep(50);
        Assert.Equal(2, fetcher.Calls.Count);

        // The next level's program starts clean on the same session object...
        using Rig next = new();
        next.Load(UriProgram(), new CsqcHostOptions { UriRequests = requests });
        Assert.True(Get(next, "http://127.0.0.1/d", 4));
        GatedFetcher.Call fourth = fetcher.WaitForCall(2);
        fourth.Done.SetResult(new LegacyUriResult(0, Encoding.ASCII.GetBytes("new"), null));
        AwaitCallbacks(next, 1);
        Assert.Equal(4, next.Global("cb_id"));

        // ...and when the session ends nothing can be started any more.
        Assert.True(Get(next, "http://127.0.0.1/e", 5));
        GatedFetcher.Call fifth = fetcher.WaitForCall(3);
        requests.Dispose();
        Assert.True(fifth.Cancel.IsCancellationRequested);
        Assert.False(requests.Available);
        Assert.False(Get(next, "http://127.0.0.1/f", 6));
        Assert.Equal(0, next.Host!.DeliverUriReplies());
        Assert.Equal(1, next.Global("cb_count"));
    }

    [Fact]
    public void UriGet_ARealTransferIsCancelledWhenTheSessionEnds()
    {
        using UriHttp http = new(_ => new UriReply { Stall = true });
        LegacyUriRequests requests = Local();
        Assert.True(requests.Begin(http.Base + "slow", 1));
        Stopwatch watch = Stopwatch.StartNew();
        while (http.Seen.Count == 0 && watch.ElapsedMilliseconds < 10_000) Thread.Sleep(5);
        Assert.Single(http.Seen);
        watch.Restart();
        requests.Dispose();
        // Dispose waits a moment for the transfer to stop; the 10 s deadline of the request is not what ended it.
        Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);
        Assert.Equal(0, requests.Pending);
    }
}
