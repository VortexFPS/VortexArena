using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Downloads;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using VortexArena.Tests.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The packages a DarkPlaces server has a joining client download (libcurl.c "curl --pak --forthismap"), the
/// in-band fallback for a missing map, and the rule that a level is not entered without its world - against
/// a scripted game server and an HTTP listener on 127.0.0.1 that this test runs itself. Everything a server
/// controls is treated as hostile here: names, addresses, sizes, redirects, archive contents.
/// </summary>
public class LegacyDownloadTests
{
    // ---- a very small HTTP server ------------------------------------------------------------------------

    private sealed class Reply
    {
        public int Status = 200;
        public byte[] Body = Array.Empty<byte>();
        public string? Location;
        /// <summary>Announce this Content-Length instead of the body's (a lie), or -1 to announce none.</summary>
        public long? AnnouncedLength;
        /// <summary>Send the headers and then nothing more, keeping the connection open.</summary>
        public bool Stall;
    }

    private sealed class MiniHttp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, Reply> _handler;
        public readonly List<string> Requests = new();
        public readonly List<string> Headers = new();
        public int Port { get; }
        public string Base => $"http://127.0.0.1:{Port}/";

        public MiniHttp(Func<string, Reply> handler)
        {
            _handler = handler;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Accept);
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
                    string path = text.Split(' ')[1];
                    lock (Requests)
                    {
                        Requests.Add(path);
                        Headers.Add(text);
                    }
                    Reply reply = _handler(path);
                    StringBuilder response = new($"HTTP/1.1 {reply.Status} X\r\nConnection: close\r\n");
                    if (reply.Location is not null) response.Append("Location: ").Append(reply.Location).Append("\r\n");
                    long announced = reply.AnnouncedLength ?? reply.Body.Length;
                    if (announced >= 0) response.Append("Content-Length: ").Append(announced).Append("\r\n");
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

    private static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string name, byte[] data) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using Stream s = entry.Open();
                s.Write(data);
            }
        return stream.ToArray();
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "va-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string? Fetch(Uri url, string target, Action<LegacyFetchRequest>? check = null, long maxBytes = 1 << 20, double stall = 5, bool allowPrivate = true, int redirects = 5)
    {
        LegacyFetchRequest request = new()
        {
            Url = url, TargetPath = target, MaxBytes = maxBytes, StallTimeoutSeconds = stall, ConnectTimeoutSeconds = 5, AllowPrivateHosts = allowPrivate,
            MaxRedirects = redirects, Referer = "dp://127.0.0.1:26000/", UserAgent = "test-agent",
        };
        check?.Invoke(request);
        return new HttpPackageFetcher().FetchAsync(request, new LegacyFetchProgress(), CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---- the command ---------------------------------------------------------------------------------------

    [Fact]
    public void The_Server_s_Curl_Line_Is_Read_As_DarkPlaces_Reads_It()
    {
        // What Curl_SendRequirement writes.
        string[] argv = { "curl", "--pak", "--forthismap", "--as", "nicemap.pk3", "--maxspeed=300.0", "--for", "maps/nicemap.bsp", "http://host/dir/nicemap.pk3" };
        DpCurlCommand c = DpCurlCommand.Parse(argv, _ => false);
        Assert.Equal(DpCurlAction.Download, c.Action);
        Assert.Equal(DpCurlLoadType.Pak, c.LoadType);
        Assert.True(c.ForThisMap);
        Assert.Equal("nicemap.pk3", c.As);
        Assert.Equal(300.0, c.MaxSpeed);
        Assert.Equal("http://host/dir/nicemap.pk3", c.Url);
        Assert.Equal(new[] { "maps/nicemap.bsp" }, c.For);

        // "--for": nothing is fetched when the client has every file named.
        Assert.Equal(DpCurlAction.None, DpCurlCommand.Parse(argv, _ => true).Action);
        Assert.Equal(DpCurlAction.ClearAutodownload, DpCurlCommand.Parse(new[] { "curl", "--clear_autodownload" }, _ => false).Action);
        Assert.Equal(DpCurlAction.FinishAutodownload, DpCurlCommand.Parse(new[] { "curl", "--finish_autodownload" }, _ => false).Action);
        Assert.Equal(DpCurlAction.Info, DpCurlCommand.Parse(new[] { "curl", "--info" }, _ => false).Action);
        Assert.True(DpCurlCommand.Parse(new[] { "curl", "--cancel" }, _ => false).CancelAll);
        Assert.False(DpCurlCommand.Parse(new[] { "curl", "--cancel", "dlcache/x.pk3" }, _ => false).CancelAll);
        Assert.Equal(new[] { "--evil" }, DpCurlCommand.Parse(new[] { "curl", "--evil", "--pak", "http://h/x.pk3" }, _ => false).InvalidOptions);
        Assert.Equal(0, DpCurlCommand.Parse(new[] { "curl", "--maxspeed=NaN", "http://h/x.pk3" }, _ => false).MaxSpeed);
    }

    [Theory]
    [InlineData("nicemap.pk3", true)]
    [InlineData("Map-Name_v1r2+final.PK3", true)]
    [InlineData("data.dpk", true)]
    [InlineData("../evil.pk3", false)]
    [InlineData("..\\evil.pk3", false)]
    [InlineData("sub/evil.pk3", false)]
    [InlineData("c:evil.pk3", false)]
    [InlineData(".hidden.pk3", false)]
    [InlineData("evil.exe", false)]
    [InlineData("evil.pk3.exe", false)]
    [InlineData("autoexec.cfg", false)]
    [InlineData("con.pk3", false)]
    [InlineData("COM1.pk3", false)]
    [InlineData("evil.pk3 ", false)]
    [InlineData("a b.pk3", false)]
    [InlineData("", false)]
    public void Only_A_Plain_Package_Name_May_Be_Written_Into_The_Download_Cache(string name, bool allowed)
    {
        Assert.Equal(allowed, LegacyPackValidator.SafePackageName(name) is not null);
    }

    // ---- what arrives ----------------------------------------------------------------------------------------

    [Fact]
    public void A_Package_Is_Checked_Before_It_Is_Mounted()
    {
        string dir = TempDir();
        try
        {
            string Write(string name, byte[] data)
            {
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, data);
                return path;
            }
            LegacyPackLimits limits = new();
            Assert.Null(LegacyPackValidator.Validate(Write("ok.pk3", Zip(("maps/a.bsp", new byte[100]), ("textures/a.tga", new byte[10]))), limits));

            // An error page where the package should be.
            Assert.Contains("no zip signature", LegacyPackValidator.Validate(Write("html.pk3", Encoding.ASCII.GetBytes("<html><body>404 not found, sorry about that</body></html>")), limits));
            Assert.Contains("too short", LegacyPackValidator.Validate(Write("short.pk3", new byte[] { (byte)'P', (byte)'K' }), limits));
            // The signature and then rubbish.
            byte[] rubbish = new byte[4000];
            new Random(1).NextBytes(rubbish);
            rubbish[0] = (byte)'P'; rubbish[1] = (byte)'K'; rubbish[2] = 3; rubbish[3] = 4;
            Assert.Contains("not a readable zip", LegacyPackValidator.Validate(Write("rubbish.pk3", rubbish), limits));

            // A path that leaves the package.
            Assert.Contains("leaves the package", LegacyPackValidator.Validate(Write("up.pk3", Zip(("../../autoexec.cfg", new byte[10]))), limits));
            Assert.Contains("leaves the package", LegacyPackValidator.Validate(Write("abs.pk3", Zip(("/etc/passwd", new byte[10]))), limits));
            Assert.Contains("leaves the package", LegacyPackValidator.Validate(Write("drive.pk3", Zip(("c:/windows/x", new byte[10]))), limits));

            // A decompression bomb: sixty megabytes of zeros in sixty kilobytes.
            string bomb = Write("bomb.pk3", Zip(("maps/zero.bsp", new byte[60 << 20])));
            Assert.True(new FileInfo(bomb).Length < 200_000);
            Assert.Contains("decompression bomb", LegacyPackValidator.Validate(bomb, limits));
            // Bounds on one file, on all of them, and on their number.
            Assert.Contains("unpacks to", LegacyPackValidator.Validate(Write("big.pk3", Zip(("a", new byte[3000]))), new LegacyPackLimits { MaxEntryBytes = 1000 }));
            Assert.Contains("unpacks to more than", LegacyPackValidator.Validate(Write("sum.pk3", Zip(("a", new byte[600]), ("b", new byte[600]))), new LegacyPackLimits { MaxUnpackedBytes = 1000 }));
            Assert.Contains("holds 3 files", LegacyPackValidator.Validate(Write("many.pk3", Zip(("a", new byte[1]), ("b", new byte[1]), ("c", new byte[1]))), new LegacyPackLimits { MaxEntries = 2 }));
            Assert.Contains("could not be read", LegacyPackValidator.Validate(Path.Combine(dir, "absent.pk3"), limits));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- the fetch -------------------------------------------------------------------------------------------

    [Fact]
    public void A_Fetch_Follows_A_Few_Redirects_And_Stops_At_Every_Limit()
    {
        byte[] body = new byte[70_000];
        new Random(7).NextBytes(body);
        using MiniHttp http = new(path => path switch
        {
            "/file.pk3" => new Reply { Body = body },
            "/hop1" => new Reply { Status = 302, Location = "/hop2" },
            "/hop2" => new Reply { Status = 301, Location = "/file.pk3" },
            "/loop" => new Reply { Status = 302, Location = "/loop" },
            "/tofile" => new Reply { Status = 302, Location = "file:///C:/Windows/win.ini" },
            "/toftp" => new Reply { Status = 302, Location = "ftp://127.0.0.1/x.pk3" },
            "/nowhere" => new Reply { Status = 302 },
            "/huge" => new Reply { Body = new byte[10], AnnouncedLength = 5_000_000_000 },
            "/unannounced" => new Reply { Body = new byte[300_000], AnnouncedLength = -1 },
            "/short" => new Reply { Body = new byte[100], AnnouncedLength = 5000 },
            "/empty" => new Reply { Body = Array.Empty<byte>() },
            "/stall" => new Reply { Body = new byte[10], AnnouncedLength = 1000, Stall = true },
            "/error" => new Reply { Status = 500, Body = Encoding.ASCII.GetBytes("oops") },
            _ => new Reply { Status = 404, Body = Encoding.ASCII.GetBytes("not here") },
        });
        string dir = TempDir();
        try
        {
            string target = Path.Combine(dir, "out.part");
            Assert.Null(Fetch(new Uri(http.Base + "file.pk3"), target));
            Assert.Equal(body, File.ReadAllBytes(target));
            lock (http.Requests)
            {
                // DarkPlaces' Referer names the game server; the User-Agent is the owner's.
                Assert.Contains("Referer: dp://127.0.0.1:26000/", http.Headers[0]);
                Assert.Contains("User-Agent: test-agent", http.Headers[0]);
                Assert.DoesNotContain("Accept-Encoding", http.Headers[0]);
            }
            Assert.Null(Fetch(new Uri(http.Base + "hop1"), target));
            Assert.Equal(body, File.ReadAllBytes(target));

            string Failed(string path, long maxBytes = 1 << 20, double stall = 5, int redirects = 5)
            {
                string? error = Fetch(new Uri(http.Base + path), target, null, maxBytes, stall, true, redirects);
                Assert.NotNull(error);
                Assert.False(File.Exists(target), "a failed download must not leave a file: " + path);
                return error!;
            }
            Assert.Contains("404", Failed("missing.pk3"));
            Assert.Contains("500", Failed("error"));
            Assert.Contains("redirects", Failed("loop"));
            Assert.Contains("redirects", Failed("hop1", redirects: 1));
            Assert.Contains("not http or https", Failed("tofile"));
            Assert.Contains("not http or https", Failed("toftp"));
            Assert.Contains("without a destination", Failed("nowhere"));
            Assert.Contains("the limit", Failed("huge"));
            Assert.Contains("larger than", Failed("unannounced", maxBytes: 100_000));
            Assert.Contains("the limit", Failed("file.pk3", maxBytes: 1000));
            Assert.Contains("empty", Failed("empty"));
            Assert.NotNull(Failed("short"));
            Assert.Contains("stalled", Failed("stall", stall: 1));
            lock (http.Requests) Assert.Equal(1, http.Requests.Count(r => r == "/huge"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_Server_On_The_Internet_Cannot_Point_A_Download_At_This_Machine_Or_A_Private_Network()
    {
        using MiniHttp http = new(_ => new Reply { Body = new byte[100] });
        string dir = TempDir();
        try
        {
            string target = Path.Combine(dir, "out.part");
            string? error = Fetch(new Uri(http.Base + "x.pk3"), target, allowPrivate: false);
            Assert.NotNull(error);
            Assert.Contains("private network", error);
            lock (http.Requests) Assert.Empty(http.Requests);
            // Through a redirect as little as directly.
            using MiniHttp redirecting = new(_ => new Reply { Status = 302, Location = http.Base + "x.pk3" });
            Assert.NotNull(Fetch(new Uri(redirecting.Base + "r"), target, allowPrivate: false));
            lock (http.Requests) Assert.Empty(http.Requests);
        }
        finally { Directory.Delete(dir, recursive: true); }

        foreach (string a in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.1.1", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "fe80::1", "fc00::1", "fd12::1", "::ffff:192.168.0.1" })
            Assert.True(HttpPackageFetcher.IsPrivateAddress(IPAddress.Parse(a)), a);
        foreach (string a in new[] { "8.8.8.8", "172.32.0.1", "192.169.0.1", "100.128.0.1", "2001:4860:4860::8888" })
            Assert.False(HttpPackageFetcher.IsPrivateAddress(IPAddress.Parse(a)), a);
        Assert.NotNull(HttpPackageFetcher.CheckUrl(new Uri("file:///c:/x.pk3")));
        Assert.NotNull(HttpPackageFetcher.CheckUrl(new Uri("ftp://host/x.pk3")));
        Assert.NotNull(HttpPackageFetcher.CheckUrl(new Uri("http://user:secret@host/x.pk3")));
        Assert.Null(HttpPackageFetcher.CheckUrl(new Uri("https://host/x.pk3")));
    }

    // ---- a whole join ----------------------------------------------------------------------------------------

    private static byte[] Program()
    {
        ProgsBuilder b = new();
        b.Int(0, "self", QcType.Entity);
        b.Float(0, "time");
        b.Function("CSQC_UpdateView");
        b.Emit(QcOp.Done);
        return b.Build();
    }

    /// <summary>The scripted game server of LegacyClientSessionTests, with a download cache and a file
    /// system that packages are mounted on.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly string Root = TempDir(), Cache = TempDir();
        public readonly VirtualFileSystem Vfs = new();
        public readonly CvarService Cvars = new();
        public readonly LegacyClientSession Session;
        public readonly LegacyPackageDownloads Packages;
        public readonly List<string> Events = new(), Commands = new(), Printed = new();
        public DpNetChannel Server = new(0);
        public readonly List<byte[]> ToClient = new();
        public readonly byte[] ProgramBytes = Program();
        public double Now;

        public Rig(bool localProgram = true, string? cache = null, Action<LegacyDownloadLimits>? limits = null, bool serverIsPrivate = true)
        {
            if (cache is not null) { Directory.Delete(Cache); Cache = cache; }
            File.WriteAllText(Path.Combine(Root, "placeholder.txt"), "x");
            if (localProgram) File.WriteAllBytes(Path.Combine(Root, "csprogs.dat"), ProgramBytes);
            Assert.True(Vfs.Mount(Root));
            ConfigInterpreter interpreter = new(Cvars, path => Vfs.Exists(path) ? Vfs.ReadText(path) : null);
            LegacyQcHost services = new(Cvars, Vfs) { PrintSink = s => Printed.Add(s) };
            LegacyDownloadLimits l = new() { StallTimeoutSeconds = 3, ConnectTimeoutSeconds = 3 };
            limits?.Invoke(l);
            Packages = new LegacyPackageDownloads(Cache, l)
            {
                FileExists = services.FileExists,
                MountPack = path =>
                {
                    try { return Vfs.MountBelowDirectories(path) ? null : "not there"; }
                    catch (Exception e) when (e is IOException or InvalidDataException) { return e.Message; }
                },
                Print = s => Printed.Add(s),
                ServerHost = "127.0.0.1", ServerPort = 26000, ServerIsPrivate = serverIsPrivate,
            };
            LegacyClientOptions options = new() { Packages = Packages };
            options.Client.NetFps = 1000;
            options.Client.Signon.Rate = 1_000_000;
            options.Client.Signon.RequireWorld = true;
            options.Client.Signon.FileExists = services.FileExists;
            Session = new LegacyClientSession(services, interpreter, new HeadlessLegacyPresentation(Vfs), options);
            Session.Event += Events.Add;
        }

        private static byte[] Oob(string text)
        {
            byte[] t = Encoding.ASCII.GetBytes(text);
            byte[] p = new byte[4 + t.Length];
            p[0] = p[1] = p[2] = p[3] = 0xFF;
            t.CopyTo(p, 4);
            return p;
        }

        private void Take(IReadOnlyList<byte[]> sent)
        {
            foreach (byte[] d in sent)
            {
                if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xFF && d[2] == 0xFF && d[3] == 0xFF)
                {
                    string text = Encoding.ASCII.GetString(d, 4, d.Length - 4);
                    if (text == "getchallenge") ToClient.Add(Oob("challenge abc"));
                    else if (text.StartsWith("connect\\", StringComparison.Ordinal)) { Server = new DpNetChannel(Now); ToClient.Add(Oob("accept")); }
                    continue;
                }
                if (Server.Receive(d, Now, ToClient, out byte[]? message) != DpChannelReceive.Message) continue;
                DpMessageReader r = new(message!);
                while (r.Position < r.Length && !r.BadRead)
                {
                    int clc = r.ReadByte();
                    if (clc == 1) continue;
                    if (clc == 3) r.ReadSpan(55);
                    else if (clc == 4) Commands.Add(r.ReadString());
                    else if (clc == 50) r.ReadLong();
                    else if (clc == 51) { r.ReadLong(); r.ReadUShort(); }
                    else break;
                }
            }
        }

        public void Step(double dt = 0.01)
        {
            Now += dt;
            Session.BeginFrame(Now);
            byte[][] batch = ToClient.ToArray();
            ToClient.Clear();
            foreach (byte[] d in batch) Session.Receive(d, Now);
            Take(Session.Frame(Now, default));
            Session.Draw(dt);
            Server.Transmit(default, Now, ToClient);
        }

        public void Settle(int steps = 30)
        {
            for (int i = 0; i < steps; i++) Step();
        }

        /// <summary>Frames (with real time passing, for the transfers) until the condition holds.</summary>
        public bool Until(Func<bool> condition, double seconds = 20)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            while (System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds < seconds)
            {
                Step();
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        public void Reliable(Action<DpMessageWriter> build)
        {
            build(Server.Reliable);
            Server.Transmit(default, Now, ToClient);
            Settle();
        }

        public void Unreliable(Action<DpMessageWriter> build)
        {
            DpMessageWriter w = new();
            build(w);
            Server.Transmit(w.WrittenSpan, Now, ToClient);
        }

        /// <summary>What SV_SendServerinfo sends: the client program's cvars, the download extension, the
        /// curl lines (Curl_SendRequirements), svc_serverinfo, signon 1.</summary>
        public void SendServerInfo(string map, string curlLines, string progName = "csprogs.dat")
        {
            Reliable(w =>
            {
                w.WriteByte(9); w.WriteString($"csqc_progname {progName}\ncsqc_progsize {ProgramBytes.Length}\ncsqc_progcrc {Crc16.Block(ProgramBytes)}\n");
                w.WriteByte(9); w.WriteString("cl_serverextension_download 2\n");
                if (curlLines.Length > 0) { w.WriteByte(9); w.WriteString(curlLines); }
                DpServerMessageParserTests.WriteServerInfo(w, maxClients: 8, models: new[] { map, "*1" });
                w.WriteByte(25); w.WriteByte(1);
            });
        }

        public void Dispose()
        {
            Session.Dispose();
            Vfs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            if (Directory.Exists(Cache)) Directory.Delete(Cache, recursive: true);
        }
    }

    private static string CurlLines(string url, string pack, string forFile) =>
        $"curl --clear_autodownload\ncurl --pak --forthismap --as {pack} --for {forFile} {url}{pack}\ncurl --finish_autodownload\n";

    [Fact]
    public void A_Missing_Map_Is_Downloaded_Before_The_Level_Is_Entered_And_Comes_From_The_Cache_The_Second_Time()
    {
        byte[] map = Encoding.ASCII.GetBytes("not really a map, but a file of that name");
        byte[] pack = Zip(("maps/dltest.bsp", map), ("textures/dltest/wall.tga", new byte[64]));
        using MiniHttp http = new(path => path == "/dltest.pk3" ? new Reply { Body = pack } : new Reply { Status = 404 });
        string cache;
        using (Rig rig = new())
        {
            cache = rig.Cache;
            rig.Session.Connect(0);
            rig.Settle();
            rig.SendServerInfo("maps/dltest.bsp", CurlLines(http.Base, "dltest.pk3", "maps/dltest.bsp"));
            // The level waits: no program, no prespawn, and the world has not been looked for yet.
            Assert.DoesNotContain("prespawn", rig.Commands);
            Assert.Null(rig.Session.Host);
            Assert.True(rig.Session.State.LevelLoadDeferred);

            Assert.True(rig.Until(() => rig.Commands.Contains("prespawn")), string.Join(" | ", rig.Printed));
            Assert.True(rig.Vfs.Exists("maps/dltest.bsp"));
            Assert.Equal(map, rig.Vfs.ReadBytes("maps/dltest.bsp"));
            Assert.True(File.Exists(Path.Combine(rig.Cache, "dltest.pk3")));
            Assert.Empty(Directory.GetFiles(rig.Cache, "*.part-*"));
            Assert.Null(rig.Session.Client.Signon.MissingWorld);
            Assert.NotNull(rig.Session.Host);
            // The world was loaded after the download (this one is not a map, which is a different complaint).
            Assert.DoesNotContain("not in the game data", ((HeadlessLegacyPresentation)rig.Session.Presentation).Map.LoadError ?? "");
            Assert.Contains(rig.Events, e => e.Contains("files were loaded after its downloads"));
            Assert.Contains(rig.Printed, p => p.StartsWith("Downloading http://127.0.0.1", StringComparison.Ordinal) && p.Contains("-> dlcache/dltest.pk3"));
            lock (http.Requests) Assert.Single(http.Requests);
            rig.Cache.ToString();
            // Keep the cache for the second join.
            string kept = TempDir();
            Directory.Delete(kept);
            Directory.CreateDirectory(kept);
            File.Copy(Path.Combine(rig.Cache, "dltest.pk3"), Path.Combine(kept, "dltest.pk3"));
            cache = kept;
        }

        using (Rig again = new(cache: cache))
        {
            again.Session.Connect(0);
            again.Settle();
            again.SendServerInfo("maps/dltest.bsp", CurlLines(http.Base, "dltest.pk3", "maps/dltest.bsp"));
            // Found in dlcache/: mounted at once, nothing asked of the HTTP server, straight on to prespawn.
            Assert.Contains("prespawn", again.Commands);
            Assert.True(again.Vfs.Exists("maps/dltest.bsp"));
            Assert.Equal(1, again.Packages.FromCache);
            Assert.Equal(0, again.Packages.Fetched);
            Assert.Contains(again.Printed, p => p.Contains("already exists, not downloading"));
            lock (http.Requests) Assert.Single(http.Requests);
        }
    }

    [Fact]
    public void A_Server_Package_Can_Carry_The_Client_Program_The_Server_Names()
    {
        using Rig rig = new(localProgram: false);
        byte[] pack = Zip(("csprogs-custom-1.dat", rig.ProgramBytes), ("zz-test-serverpackage.txt", new byte[4]), ("sound/custom/a.wav", new byte[32]));
        byte[] mapPack = Zip(("maps/custom.bsp", new byte[128]));
        using MiniHttp http = new(path => path switch
        {
            "/zz-server.pk3" => new Reply { Body = pack },
            "/custom.pk3" => new Reply { Body = mapPack },
            _ => new Reply { Status = 404 },
        });
        rig.Session.Connect(0);
        rig.Settle();
        string lines = "curl --clear_autodownload\n" +
            $"curl --pak --forthismap --as custom.pk3 --for maps/custom.bsp {http.Base}custom.pk3\n" +
            $"curl --pak --forthismap --as zz-server.pk3 --for csprogs-custom-1.dat {http.Base}zz-server.pk3\n" +
            $"curl --pak --forthismap --as zz-server.pk3 --for zz-test-serverpackage.txt {http.Base}zz-server.pk3\n" +
            "curl --finish_autodownload\n";
        rig.SendServerInfo("maps/custom.bsp", lines, progName: "csprogs-custom-1.dat");
        Assert.True(rig.Until(() => rig.Commands.Contains("prespawn")), string.Join(" | ", rig.Printed));
        // The program came out of the package: it was never asked for through the game connection.
        Assert.DoesNotContain(rig.Commands, c => c.StartsWith("download", StringComparison.Ordinal));
        Assert.NotNull(rig.Session.Host);
        Assert.Equal("from the game data", rig.Session.ProgramSource);
        Assert.True(rig.Vfs.Exists("sound/custom/a.wav"));
        // The same package named twice is fetched once.
        lock (http.Requests) Assert.Equal(1, http.Requests.Count(r => r == "/zz-server.pk3"));
        Assert.Contains(rig.Printed, p => p.Contains("already getting it"));
    }

    [Fact]
    public void When_The_Address_Is_Unreachable_The_Map_Is_Asked_For_Through_The_Game_Connection()
    {
        using Rig rig = new();
        rig.Session.Connect(0);
        rig.Settle();
        // A port nothing listens on.
        rig.SendServerInfo("maps/inband.bsp", CurlLines("http://127.0.0.1:9/", "inband.pk3", "maps/inband.bsp"));
        // First the package the server named...
        Assert.True(rig.Until(() => rig.Commands.Contains("download inband.pk3")), string.Join(" | ", rig.Printed));
        Assert.DoesNotContain("prespawn", rig.Commands);
        Assert.Single(rig.Packages.Failures);
        // ...which this server refuses, as a stock one does ("sv_allowdownloads_archive 0").
        rig.Reliable(w => { w.WriteByte(9); w.WriteString("\nstopdownload\n"); });
        Assert.Contains("download maps/inband.bsp", rig.Commands);

        // A file nobody asked for is not taken.
        rig.Reliable(w => { w.WriteByte(9); w.WriteString("cl_downloadbegin 100 autoexec.cfg\n"); });
        Assert.DoesNotContain("sv_startdownload", rig.Commands);
        Assert.False(rig.Session.Client.Download.Active);

        byte[] map = new byte[3000];
        new Random(3).NextBytes(map);
        rig.Reliable(w => { w.WriteByte(9); w.WriteString($"cl_downloadbegin {map.Length} maps/inband.bsp\n"); });
        Assert.Contains("sv_startdownload", rig.Commands);
        rig.Unreliable(w => { w.WriteByte(50); w.WriteLong(0); w.WriteShort(map.Length); w.WriteBytes(map); });
        rig.Settle(3);
        rig.Reliable(w => { w.WriteByte(9); w.WriteString($"cl_downloadfinished {map.Length} {Crc16.Block(map)} maps/inband.bsp\n"); });
        Assert.Contains("prespawn", rig.Commands);
        Assert.Equal(map, rig.Vfs.ReadBytes("maps/inband.bsp"));
        Assert.True(Directory.Exists(Path.Combine(rig.Cache, "inband")));
        Assert.Null(rig.Session.Client.Signon.MissingWorld);
    }

    [Fact]
    public void When_Nothing_Delivers_The_Map_The_Level_Is_Not_Entered_And_The_Reasons_Are_Kept()
    {
        using MiniHttp http = new(_ => new Reply { Status = 404, Body = Encoding.ASCII.GetBytes("gone") });
        using Rig rig = new();
        rig.Session.Connect(0);
        rig.Settle();
        rig.SendServerInfo("maps/nowhere.bsp", CurlLines(http.Base, "nowhere.pk3", "maps/nowhere.bsp"));
        Assert.True(rig.Until(() => rig.Commands.Contains("download nowhere.pk3")), string.Join(" | ", rig.Printed));
        rig.Reliable(w => { w.WriteByte(9); w.WriteString("\nstopdownload\n"); });
        Assert.Contains("download maps/nowhere.bsp", rig.Commands);
        rig.Reliable(w => { w.WriteByte(9); w.WriteString("\nstopdownload\n"); });

        DpSignon signon = rig.Session.Client.Signon;
        Assert.Equal("maps/nowhere.bsp", signon.MissingWorld);
        Assert.DoesNotContain("prespawn", rig.Commands);
        Assert.Null(rig.Session.Host);
        Assert.Equal(DpClientState.Connected, rig.Session.Client.State);   // the owner leaves, with the reason
        Assert.Contains("404", Assert.Single(rig.Packages.Failures));
        Assert.Equal(2, signon.FallbackLog.Count(l => l.Contains("refused by the server")));
        Assert.Contains(rig.Printed, p => p.Contains("Map maps/nowhere.bsp not found"));
        Assert.Empty(Directory.GetFiles(rig.Cache));
    }

    [Fact]
    public void A_Server_Without_A_Download_Address_And_A_Missing_Map_Ends_The_Same_Way()
    {
        using Rig rig = new();
        rig.Session.Connect(0);
        rig.Settle();
        rig.SendServerInfo("maps/absent.bsp", "");
        Assert.Contains("download maps/absent.bsp", rig.Commands);
        rig.Reliable(w => { w.WriteByte(9); w.WriteString("\nstopdownload\n"); });
        Assert.Equal("maps/absent.bsp", rig.Session.Client.Signon.MissingWorld);
        Assert.DoesNotContain("prespawn", rig.Commands);
    }

    [Fact]
    public void What_A_Hostile_Server_Asks_For_Is_Refused_Or_Bounded()
    {
        byte[] notAZip = Encoding.ASCII.GetBytes("<html>this is the wrong file, a thousand times over</html>" + new string('x', 500));
        byte[] noise = new byte[200_000];
        new Random(11).NextBytes(noise);
        byte[] big = Zip(("maps/big.bsp", noise));
        using MiniHttp http = new(path => path switch
        {
            "/fake.pk3" => new Reply { Body = notAZip },
            "/big.pk3" => new Reply { Body = big },
            _ => new Reply { Body = Zip(("maps/x.bsp", new byte[10])) },
        });
        using Rig rig = new(limits: l => { l.MaxFileBytes = 50_000; l.MaxDownloadsPerLevel = 6; });
        rig.Session.Connect(0);
        rig.Settle();
        string b = http.Base;
        string lines = "curl --clear_autodownload\n" +
            $"curl --pak --forthismap --as ../../evil.pk3 --for maps/x.bsp {b}a.pk3\n" +       // a path out of the cache
            $"curl --pak --forthismap --as autoexec.cfg --for maps/x.bsp {b}a.pk3\n" +          // not a package
            $"curl --forthismap --as plain.pk3 --for maps/x.bsp {b}a.pk3\n" +                    // not --pak
            $"curl --pak --forthismap --cachepic --as pic.pk3 --for maps/x.bsp {b}a.pk3\n" +     // a picture download
            "curl --pak --forthismap --as local.pk3 --for maps/x.bsp file:///C:/Windows/win.ini\n" +
            "curl --pak --forthismap --as ftp.pk3 --for maps/x.bsp ftp://127.0.0.1/x.pk3\n" +
            $"curl --pak --forthismap --as fake.pk3 --for maps/x.bsp {b}fake.pk3\n" +           // not a zip archive
            $"curl --pak --forthismap --as big.pk3 --for maps/x.bsp {b}big.pk3\n" +             // over the size limit
            "curl --finish_autodownload\n";
        rig.SendServerInfo("maps/x.bsp", lines);
        // Both real transfers fail; the level then asks in-band, first for the packages the server named for the map.
        Assert.True(rig.Until(() => rig.Commands.Contains("download local.pk3")), string.Join(" | ", rig.Printed));
        Assert.Equal(2, rig.Packages.Failures.Count);
        Assert.Contains(rig.Packages.Failures, f => f.StartsWith("fake.pk3", StringComparison.Ordinal) && f.Contains("no zip signature"));
        Assert.Contains(rig.Packages.Failures, f => f.StartsWith("big.pk3", StringComparison.Ordinal) && f.Contains("limit"));
        lock (http.Requests) Assert.DoesNotContain("/a.pk3", http.Requests);
        Assert.Equal(4, rig.Printed.Count(p => p.Contains("refused:")));
        Assert.Equal(2, rig.Printed.Count(p => p.Contains("nasty URL scheme rejected")));
        // Nothing stayed in the cache, and nothing was written outside it.
        Assert.Empty(Directory.GetFiles(rig.Cache));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(rig.Cache)!, "evil.pk3")));
        Assert.False(rig.Vfs.Exists("maps/x.bsp"));

        // More downloads than one level may ask for.
        StringBuilder flood = new("curl --clear_autodownload\n");
        for (int i = 0; i < 20; i++) flood.Append($"curl --pak --as flood{i}.pk3 {b}flood{i}.pk3\n");
        rig.Reliable(w => { w.WriteByte(9); w.WriteString(flood.ToString()); });
        Assert.Equal(14, rig.Printed.Count(p => p.Contains("more than 6 downloads")));
    }

    [Fact]
    public void A_Download_On_A_Private_Address_Is_Refused_When_The_Game_Server_Is_Not_On_One()
    {
        using MiniHttp http = new(_ => new Reply { Body = Zip(("maps/p.bsp", new byte[10])) });
        using Rig rig = new(serverIsPrivate: false);
        rig.Session.Connect(0);
        rig.Settle();
        rig.SendServerInfo("maps/p.bsp", CurlLines(http.Base, "p.pk3", "maps/p.bsp"));
        Assert.True(rig.Until(() => rig.Packages.Failures.Count > 0), string.Join(" | ", rig.Printed));
        Assert.Contains("private network", rig.Packages.Failures[0]);
        lock (http.Requests) Assert.Empty(http.Requests);
    }

    [Fact]
    public void Download_Lines_Read_As_DarkPlaces_Draws_Them()
    {
        Assert.Equal("Still in queue: dlcache/a.pk3", new LegacyDownloadInfo("dlcache/a.pk3", true, -1, 0, -1, 0, true).Text);
        Assert.Equal("Downloading dlcache/a.pk3 ...  ???.?% @ 12.0 KiB/s", new LegacyDownloadInfo("dlcache/a.pk3", false, -1, 100, -1, 12288, true).Text);
        Assert.Equal("Downloading dlcache/a.pk3 ...   43.0% @ 1530.0 KiB/s", new LegacyDownloadInfo("dlcache/a.pk3", false, 0.43, 430, 1000, 1530 * 1024, true).Text);
    }

    [Fact]
    public void A_Downloaded_Package_Is_Mounted_Under_The_Loose_Directories_And_Over_The_Other_Packages()
    {
        string root = TempDir(), other = TempDir();
        try
        {
            // A loose file, an installed package, and a downloaded package all holding the same two names.
            Directory.CreateDirectory(Path.Combine(root, "zz-data.pk3dir"));
            File.WriteAllText(Path.Combine(root, "zz-data.pk3dir", "loose.txt"), "loose");
            File.WriteAllBytes(Path.Combine(root, "installed.pk3"), Zip(("packed.txt", Encoding.ASCII.GetBytes("installed")), ("only-installed.txt", new byte[1])));
            string downloaded = Path.Combine(other, "downloaded.pk3");
            File.WriteAllBytes(downloaded, Zip(("loose.txt", Encoding.ASCII.GetBytes("downloaded")), ("packed.txt", Encoding.ASCII.GetBytes("downloaded")), ("new.txt", new byte[1])));
            using VirtualFileSystem vfs = new();
            Assert.True(vfs.MountGameDir(root));
            Assert.True(vfs.MountBelowDirectories(downloaded));
            Assert.Equal("loose", vfs.ReadText("loose.txt"));          // FS_AddPack keep_plain_dirs: never over a loose file
            Assert.Equal("downloaded", vfs.ReadText("packed.txt"));    // but over another package, as in DarkPlaces
            Assert.True(vfs.Exists("new.txt"));
            Assert.True(vfs.Exists("only-installed.txt"));
            Assert.False(vfs.MountBelowDirectories(Path.Combine(other, "absent.pk3")));
            // A rescan keeps it where it was.
            vfs.Rescan();
            Assert.Equal("loose", vfs.ReadText("loose.txt"));
            Assert.Equal("downloaded", vfs.ReadText("packed.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }
}
