// Port of Base/darkplaces/netconn.c as far as the server list's packets go: NetConn_QueryMasters (the
// "getservers" request), NetConn_QueryQueueFrame's "getstatus" request, and the getserversResponse /
// statusResponse / infoResponse cases of NetConn_ClientParsePacket (who a reply may come from, and how a
// status reply splits into its info line and its player lines).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using VortexArena.Legacy.Menu;
using VortexArena.Net;

namespace VortexArena.Game.Legacy;

/// <summary>
/// The socket behind the Xonotic menu's server browser: <see cref="IMenuServerQueries"/> for
/// <see cref="MenuHostCache"/>, which decides what to ask and what to believe.
///
/// It speaks the same connectionless protocol as the native browser and uses the same codec
/// (<see cref="MasterServerProtocol"/>: the "getservers" request and the parser of its reply). It has
/// its own UDP socket rather than a <see cref="MasterServerLink"/> for two reasons the cache depends on:
/// DarkPlaces asks a server "getstatus", not "getinfo" (the status reply carries the player list the
/// server-info dialog shows, and the link decodes only infoResponse), and the cache has to know WHICH
/// master answered and whether its list ended with the end-of-transmission mark, neither of which the
/// link's event reports.
///
/// PINGS. A ping is the time from a query leaving to its reply arriving. Reading the socket once a frame
/// would add up to a frame and a half to every one of them (ten milliseconds at 144 frames a second, more
/// than the whole ping of a nearby server), so a thread waits on the socket and stamps each datagram with
/// the cache's clock as it arrives; the frame only hands the stamped datagrams on. The stamp is the only
/// thing the thread decides: everything a datagram says is parsed on the main thread.
///
/// READ-ONLY. It asks masters for their list and servers for their status. It never sends a heartbeat,
/// never registers anything and never answers a query: a datagram that is not a reply to one of its own
/// questions is dropped (the cache checks the challenge it echoes).
/// </summary>
public sealed class LegacyServerListLink : IMenuServerQueries, IDisposable
{
    /// <summary>DPMASTER_PORT.</summary>
    public const int MasterPort = 27950;

    private const int MaxDatagramsPerPoll = 512;
    private const int MaxDatagramBytes = 16384;

    private readonly UdpClient _udp;
    private readonly MenuHostCache _cache;
    private readonly Action<string> _log;
    // Master name (as the sv_master cvar spells it) to its address: null while the lookup runs, an
    // endpoint once it has, and absent-with-failure recorded in _unresolvable.
    private readonly Dictionary<string, IPEndPoint?> _masters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolvable = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Master, Task<IPAddress[]> Lookup, int Port, byte[] Request)> _lookups = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(byte[] Data, IPEndPoint From, double At)> _arrived = new();
    private readonly System.Threading.Thread _receiver;
    private volatile bool _disposed;
    private int _queued;

    public long DatagramsSent { get; private set; }
    public long DatagramsReceived { get; private set; }
    public long DatagramsIgnored { get; private set; }

    public LegacyServerListLink(MenuHostCache cache, Action<string>? log = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _log = log ?? (_ => { });
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _udp.Client.ReceiveBufferSize = 1 << 20;
        _receiver = new System.Threading.Thread(ReceiveLoop) { IsBackground = true, Name = "LegacyServerList" };
        _receiver.Start();
    }

    // The socket's thread: wait for a datagram, note when it came, queue it. Bounded, so a flood costs
    // memory for a few thousand datagrams and no more; what does not fit is dropped unread.
    private void ReceiveLoop()
    {
        while (!_disposed)
        {
            try
            {
                IPEndPoint from = new(IPAddress.Any, 0);
                byte[] data = _udp.Receive(ref from);
                double at = _cache.Clock();
                if (data.Length > MaxDatagramBytes || System.Threading.Volatile.Read(ref _queued) >= 4096) continue;
                System.Threading.Interlocked.Increment(ref _queued);
                _arrived.Enqueue((data, from, at));
            }
            catch (SocketException) { }   // an ICMP "port unreachable" from a dead server, or the socket closing
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>"getservers &lt;game&gt; &lt;protocol&gt; empty full" to a master. The name is looked up off the
    /// frame (a slow resolver must not stall the menu); the request leaves when the answer is in.</summary>
    public bool QueryMaster(string master, string gameName, int protocol)
    {
        if (_disposed || _unresolvable.Contains(master)) return false;
        if (!SplitHostPort(master, MasterPort, out string host, out int port)) return false;
        byte[] request = MasterServerProtocol.EncodeGetServers(gameName, protocol);
        if (_masters.TryGetValue(master, out IPEndPoint? known))
        {
            if (known is not null) Send(request, known);
            // else: its lookup is still running and will send this refresh's request too
            return true;
        }
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            IPEndPoint endpoint = new(literal, port);
            _masters[master] = endpoint;
            Send(request, endpoint);
            return true;
        }
        _masters[master] = null;
        _lookups.Add((master, Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork), port, request));
        return true;
    }

    /// <summary>"getstatus &lt;challenge&gt;" to a game server.</summary>
    public void QueryServer(string address, string challenge)
    {
        if (_disposed || challenge.Length > 64) return;
        if (!SplitHostPort(address, 0, out string host, out int port) || port == 0 || !IPAddress.TryParse(host, out IPAddress? ip)) return;
        byte[] text = Encoding.ASCII.GetBytes("getstatus " + challenge);
        byte[] datagram = new byte[4 + text.Length];
        datagram[0] = datagram[1] = datagram[2] = datagram[3] = 0xFF;
        text.CopyTo(datagram, 4);
        Send(datagram, new IPEndPoint(ip, port));
    }

    private void Send(byte[] datagram, IPEndPoint to)
    {
        try
        {
            _udp.Send(datagram, datagram.Length, to);
            DatagramsSent++;
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static bool SplitHostPort(string text, int defaultPort, out string host, out int port)
    {
        host = text.Trim();
        port = defaultPort;
        if (host.Length is 0 or > 128) return false;
        int colon = host.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(host.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535) return false;
            host = host[..colon];
        }
        return host.Length > 0;
    }

    /// <summary>
    /// Once a frame: finish name lookups, then hand every datagram that arrived to the cache, each with the
    /// time it arrived at.
    /// </summary>
    public void Poll()
    {
        if (_disposed) return;
        for (int i = _lookups.Count - 1; i >= 0; i--)
        {
            (string master, Task<IPAddress[]> lookup, int port, byte[] request) = _lookups[i];
            if (!lookup.IsCompleted) continue;
            _lookups.RemoveAt(i);
            IPAddress? address = lookup.Status == TaskStatus.RanToCompletion && lookup.Result.Length > 0 ? lookup.Result[0] : null;
            if (address is null)
            {
                _masters.Remove(master);
                _unresolvable.Add(master);
                _cache.MasterUnreachable(master);
                _log($"master server \"{master}\" could not be resolved (to an IPv4 address): not asked");
                continue;
            }
            IPEndPoint endpoint = new(address, port);
            _masters[master] = endpoint;
            Send(request, endpoint);
        }

        for (int n = 0; n < MaxDatagramsPerPoll && _arrived.TryDequeue(out (byte[] Data, IPEndPoint From, double At) datagram); n++)
        {
            System.Threading.Interlocked.Decrement(ref _queued);
            DatagramsReceived++;
            if (!Dispatch(datagram.Data, datagram.From, datagram.At)) DatagramsIgnored++;
        }
    }

    private bool Dispatch(byte[] data, IPEndPoint from, double now)
    {
        if (!MasterServerProtocol.TryStripOob(data, out ReadOnlySpan<byte> body)) return false;

        if (StartsWith(body, "getserversResponse"))
        {
            // "ignoring DarkPlaces server list from unrecognised master": only a master that was asked.
            string? master = null;
            foreach ((string name, IPEndPoint? endpoint) in _masters)
                if (endpoint is not null && endpoint.Equals(from)) { master = name; break; }
            if (master is null) return false;
            IReadOnlyList<(IPAddress ip, int port)> servers = MasterServerProtocol.ParseGetServersResponse(data);
            List<string> addresses = new(servers.Count);
            foreach ((IPAddress ip, int port) in servers) addresses.Add(string.Create(CultureInfo.InvariantCulture, $"{ip}:{port}"));
            _cache.MasterReply(master, addresses, EndsTransmission(body["getserversResponse".Length..]));
            return true;
        }

        bool status = StartsWith(body, "statusResponse\n");
        if (status || StartsWith(body, "infoResponse\n"))
        {
            if (from.AddressFamily != AddressFamily.InterNetwork) return false;
            // The text is a server's. Xonotic servers write names in UTF-8 (utf8_enable 1), which is what the
            // menu program's strings are here; bytes that are not UTF-8 become U+FFFD. The cache cuts and
            // cleans each field.
            string text = Encoding.UTF8.GetString(body[(status ? 15 : 13)..]);
            string info = text, players = "";
            int newline = text.IndexOf('\n');
            if (newline >= 0)
            {
                // "cut off the string there": the first line is the info string, the rest the players block.
                info = text[..newline];
                players = status ? text[(newline + 1)..] : "";
            }
            return _cache.ServerReply(string.Create(CultureInfo.InvariantCulture, $"{from.Address}:{from.Port}"), info, players, now);
        }
        return false;
    }

    private static bool StartsWith(ReadOnlySpan<byte> body, string text)
    {
        if (body.Length < text.Length) return false;
        for (int i = 0; i < text.Length; i++)
            if (body[i] != text[i]) return false;
        return true;
    }

    // The list is a run of 7-byte records ('\' + 4 address bytes + 2 port bytes); "\EOT\0\0\0" ends the
    // master's whole answer, which may have come in several datagrams.
    private static bool EndsTransmission(ReadOnlySpan<byte> records)
    {
        for (; records.Length >= 7; records = records[7..])
        {
            if (records[0] != (byte)'\\') return false;
            if (records[1] == (byte)'E' && records[2] == (byte)'O' && records[3] == (byte)'T' && records[4] == 0 && records[5] == 0 && records[6] == 0) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _udp.Dispose();   // ends the receive in progress
        _receiver.Join(500);
    }
}
