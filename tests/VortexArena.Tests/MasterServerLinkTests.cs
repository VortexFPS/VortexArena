using System.Collections.Generic;
using System.Net;
using System.Threading;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Loopback round-trip test for the UDP socket layer of the master/server-info protocol
/// (<see cref="MasterServerLink"/>) — the send → receive → dispatch path over the (already unit-tested)
/// <see cref="MasterServerProtocol"/> codec. Uses two links on loopback; bounded-poll, no fixed sleeps that
/// could hang CI.
/// </summary>
public class MasterServerLinkTests
{
    private static bool PumpUntil(Func<bool> done, MasterServerLink a, MasterServerLink b, int iterations = 200)
    {
        for (int i = 0; i < iterations; i++)
        {
            a.Poll();
            b.Poll();
            if (done())
                return true;
            Thread.Sleep(5);
        }
        return false;
    }

    [Fact]
    public void Server_Answers_A_GetInfo_Probe_RoundTrip()
    {
        using var server = new MasterServerLink();
        using var client = new MasterServerLink();
        var serverEp = new IPEndPoint(IPAddress.Loopback, server.LocalEndPoint.Port);

        var info = new Dictionary<string, string> { ["hostname"] = "XonoticGodot Test", ["mapname"] = "dm_exomorph", ["clients"] = "3" };
        string? gotChallenge = null;
        server.GetInfoRequested += (from, challenge) => { gotChallenge = challenge; server.SendInfoResponse(from, info); };

        Dictionary<string, string>? got = null;
        client.InfoReceived += (_, d) => got = new Dictionary<string, string>(d);

        client.RequestInfo(serverEp, "ping42");
        bool ok = PumpUntil(() => got is not null, server, client);

        Assert.True(ok, "expected an infoResponse over loopback");
        Assert.Equal("ping42", gotChallenge);            // the server saw our getinfo + challenge
        Assert.Equal("XonoticGodot Test", got!["hostname"]);  // and we parsed its infostring back
        Assert.Equal("dm_exomorph", got["mapname"]);
        Assert.Equal("3", got["clients"]);
    }

    [Fact]
    public void Browser_Parses_A_Master_Server_List_RoundTrip()
    {
        using var master = new MasterServerLink();
        using var client = new MasterServerLink();
        var clientEp = new IPEndPoint(IPAddress.Loopback, client.LocalEndPoint.Port);

        // The "master" answers any getservers with a canned getserversResponse (built via the protocol's own
        // entry layout: '\' + 4-byte IPv4 + 2-byte big-endian port, terminated by \EOT\0\0\0).
        master.GetInfoRequested += (_, _) => { }; // unused here
        var listed = new List<(IPAddress ip, int port)>();
        client.ServerListReceived += l => listed.AddRange(l);

        // master sends the canned response directly to the client (simulating a master's reply to getservers).
        byte[] response = BuildGetServersResponse(("1.2.3.4", 26000), ("200.100.50.25", 27500));
        master.SendRaw(response, clientEp);

        bool ok = PumpUntil(() => listed.Count >= 2, master, client);
        Assert.True(ok, "expected the parsed server list");
        Assert.Contains((IPAddress.Parse("1.2.3.4"), 26000), listed);
        Assert.Contains((IPAddress.Parse("200.100.50.25"), 27500), listed);
    }

    private static byte[] BuildGetServersResponse(params (string ip, int port)[] servers)
    {
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("getserversResponse"));
        foreach ((string ip, int port) in servers)
        {
            bytes.Add((byte)'\\');
            bytes.AddRange(IPAddress.Parse(ip).GetAddressBytes());           // 4 bytes, network order
            bytes.Add((byte)(port >> 8)); bytes.Add((byte)(port & 0xFF));    // 2 bytes, big-endian
        }
        bytes.Add((byte)'\\'); bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("EOT")); bytes.AddRange(new byte[] { 0, 0, 0 });
        return bytes.ToArray();
    }
}
