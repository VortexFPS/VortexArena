using System.Collections.Generic;
using System.Net;
using System.Text;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Byte-layout and round-trip tests for <see cref="MasterServerProtocol"/>, the DP/Q3 connectionless
/// (OOB) master-server query protocol. These pin the exact on-the-wire bytes against the dpmaster /
/// DarkPlaces <c>netconn.c</c> grammar (the protocol is an interop boundary, so the layout is fixed, not
/// ours to change): the four <c>0xFF</c> header, the ASCII command tokens, the big-endian packed server
/// list, and the <c>\key\value</c> infostring.
/// </summary>
public class MasterServerProtocolTests
{
    private static readonly byte[] OobPrefix = { 0xFF, 0xFF, 0xFF, 0xFF };

    // --- command byte layout: 4x0xFF prefix + exact ASCII command ---

    [Fact]
    public void Heartbeat_HasOobPrefix_AndDarkPlacesLiteral()
    {
        byte[] pkt = MasterServerProtocol.EncodeHeartbeat();

        AssertOobPrefix(pkt);
        // The DP heartbeat literal is game-independent: "heartbeat DarkPlaces\x0A".
        Assert.Equal("heartbeat DarkPlaces\x0A", AsciiBody(pkt));

        // The gameName argument must not change the bytes (API symmetry only).
        byte[] pkt2 = MasterServerProtocol.EncodeHeartbeat("SomeOtherGame");
        Assert.Equal(pkt, pkt2);
    }

    [Fact]
    public void GetServers_HasOobPrefix_AndExactCommand_WithFlags()
    {
        byte[] pkt = MasterServerProtocol.EncodeGetServers("Xonotic", 3);

        AssertOobPrefix(pkt);
        Assert.Equal("getservers Xonotic 3 empty full", AsciiBody(pkt));
    }

    [Fact]
    public void GetServers_OmitsFlags_WhenFalse()
    {
        Assert.Equal("getservers Xonotic 3",
            AsciiBody(MasterServerProtocol.EncodeGetServers("Xonotic", 3, empty: false, full: false)));
        Assert.Equal("getservers Xonotic 3 empty",
            AsciiBody(MasterServerProtocol.EncodeGetServers("Xonotic", 3, empty: true, full: false)));
        Assert.Equal("getservers Xonotic 3 full",
            AsciiBody(MasterServerProtocol.EncodeGetServers("Xonotic", 3, empty: false, full: true)));
    }

    [Fact]
    public void GetInfo_HasOobPrefix_AndChallenge()
    {
        byte[] pkt = MasterServerProtocol.EncodeGetInfo("A_B_C_123");

        AssertOobPrefix(pkt);
        Assert.Equal("getinfo A_B_C_123", AsciiBody(pkt));

        // empty challenge -> bare "getinfo"
        Assert.Equal("getinfo", AsciiBody(MasterServerProtocol.EncodeGetInfo("")));
    }

    // --- getserversResponse: hand-built packet round-trips to the right (ip, port) pairs ---

    [Fact]
    public void ParseGetServersResponse_DecodesPackedEntries_AndStopsAtEot()
    {
        // Build: \xFF\xFF\xFF\xFF + "getserversResponse" + entry1 + entry2 + \EOT\0\0\0
        // entry = '\' + 4-byte IPv4 (big-endian) + 2-byte port (big-endian).
        var pkt = new List<byte>();
        pkt.AddRange(OobPrefix);
        pkt.AddRange(Encoding.ASCII.GetBytes("getserversResponse"));

        // 1.2.3.4:26000  (26000 = 0x6590 -> bytes 0x65,0x90)
        pkt.Add((byte)'\\');
        pkt.AddRange(new byte[] { 1, 2, 3, 4 });
        pkt.AddRange(new byte[] { 0x65, 0x90 });

        // 200.100.50.25:27500 (27500 = 0x6B6C -> bytes 0x6B,0x6C)
        pkt.Add((byte)'\\');
        pkt.AddRange(new byte[] { 200, 100, 50, 25 });
        pkt.AddRange(new byte[] { 0x6B, 0x6C });

        // terminator: \EOT\0\0\0  ('\' + 'E','O','T',0 + port 0,0)
        pkt.Add((byte)'\\');
        pkt.AddRange(new byte[] { (byte)'E', (byte)'O', (byte)'T', 0, 0, 0 });

        // Anything after EOT must be ignored (a real datagram may have trailing padding/garbage).
        pkt.Add((byte)'\\');
        pkt.AddRange(new byte[] { 9, 9, 9, 9, 0x01, 0x02 });

        var list = MasterServerProtocol.ParseGetServersResponse(pkt.ToArray());

        Assert.Equal(2, list.Count);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), list[0].ip);
        Assert.Equal(26000, list[0].port);
        Assert.Equal(IPAddress.Parse("200.100.50.25"), list[1].ip);
        Assert.Equal(27500, list[1].port);
    }

    [Fact]
    public void ParseGetServersResponse_RejectsNonResponse_AndHandlesTruncation()
    {
        // Wrong token.
        var wrong = new List<byte>(OobPrefix);
        wrong.AddRange(Encoding.ASCII.GetBytes("someOtherResponse\\"));
        Assert.Empty(MasterServerProtocol.ParseGetServersResponse(wrong.ToArray()));

        // Missing OOB header.
        Assert.Empty(MasterServerProtocol.ParseGetServersResponse(
            Encoding.ASCII.GetBytes("getserversResponse\\")));

        // Truncated trailing entry (only 3 of the 6 payload bytes present): the complete first entry is
        // still returned, the partial second is dropped rather than throwing.
        var trunc = new List<byte>(OobPrefix);
        trunc.AddRange(Encoding.ASCII.GetBytes("getserversResponse"));
        trunc.Add((byte)'\\');
        trunc.AddRange(new byte[] { 10, 0, 0, 1, 0x65, 0x90 }); // 10.0.0.1:26000
        trunc.Add((byte)'\\');
        trunc.AddRange(new byte[] { 10, 0, 0 });                // truncated
        var partial = MasterServerProtocol.ParseGetServersResponse(trunc.ToArray());
        Assert.Single(partial);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), partial[0].ip);
        Assert.Equal(26000, partial[0].port);
    }

    // --- infostring round-trip ---

    [Fact]
    public void Infostring_RoundTrips()
    {
        var info = new Dictionary<string, string>
        {
            ["gamename"] = "Xonotic",
            ["hostname"] = "My Cool Server",
            ["mapname"] = "dance",
            ["protocol"] = "3",
            ["clients"] = "5",
        };

        string encoded = MasterServerProtocol.EncodeInfostring(info);

        // Format sanity: leading backslash, and a \key\value pair present.
        Assert.StartsWith("\\", encoded);
        Assert.Contains("\\gamename\\Xonotic", encoded);
        Assert.Contains("\\protocol\\3", encoded);

        Dictionary<string, string> decoded = MasterServerProtocol.ParseInfostring(encoded);
        Assert.Equal(info.Count, decoded.Count);
        foreach (var kv in info)
            Assert.Equal(kv.Value, decoded[kv.Key]);
    }

    [Fact]
    public void ParseInfostring_FirstDuplicateWins_AndEmptyIsEmpty()
    {
        // DP's InfoString_GetValue returns the first match.
        var d = MasterServerProtocol.ParseInfostring("\\k\\first\\k\\second");
        Assert.Equal("first", d["k"]);

        Assert.Empty(MasterServerProtocol.ParseInfostring(""));
        Assert.Empty(MasterServerProtocol.ParseInfostring(null!));
    }

    // --- infoResponse round-trip ---

    [Fact]
    public void InfoResponse_RoundTrips()
    {
        var info = new Dictionary<string, string>
        {
            ["challenge"] = "abc123",
            ["gamename"] = "Xonotic",
            ["hostname"] = "Test Box",
            ["sv_maxclients"] = "16",
        };

        byte[] pkt = MasterServerProtocol.EncodeInfoResponse(info);

        AssertOobPrefix(pkt);
        // Body must begin with the 13-byte DP literal "infoResponse\x0A".
        Assert.StartsWith("infoResponse\x0A", AsciiBody(pkt));

        IReadOnlyDictionary<string, string> decoded = MasterServerProtocol.ParseInfoResponse(pkt);
        Assert.Equal(info.Count, decoded.Count);
        foreach (var kv in info)
            Assert.Equal(kv.Value, decoded[kv.Key]);
    }

    [Fact]
    public void ParseInfoResponse_RejectsNonInfoResponse()
    {
        Assert.Empty(MasterServerProtocol.ParseInfoResponse(MasterServerProtocol.EncodeHeartbeat()));
        Assert.Empty(MasterServerProtocol.ParseInfoResponse(new byte[] { 1, 2, 3 }));
    }

    // --- TryStripOob accept / reject ---

    [Fact]
    public void TryStripOob_AcceptsHeaderAndReturnsBody()
    {
        byte[] pkt = { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'h', (byte)'i' };
        Assert.True(MasterServerProtocol.TryStripOob(pkt, out var body));
        Assert.Equal(2, body.Length);
        Assert.Equal((byte)'h', body[0]);
        Assert.Equal((byte)'i', body[1]);

        // header with empty body is still valid (and yields an empty body).
        Assert.True(MasterServerProtocol.TryStripOob(OobPrefix, out var empty));
        Assert.Equal(0, empty.Length);
    }

    [Fact]
    public void TryStripOob_RejectsMissingHeaderOrShortPacket()
    {
        Assert.False(MasterServerProtocol.TryStripOob(new byte[] { 0xFF, 0xFF, 0xFF, 0x00, 1 }, out _));
        Assert.False(MasterServerProtocol.TryStripOob(new byte[] { 0xFF, 0xFF, 0xFF }, out _)); // too short
        Assert.False(MasterServerProtocol.TryStripOob(System.Array.Empty<byte>(), out _));
    }

    // --- helpers ---

    private static void AssertOobPrefix(byte[] pkt)
    {
        Assert.True(pkt.Length >= 4, "packet shorter than the 4-byte OOB header");
        Assert.Equal(0xFF, pkt[0]);
        Assert.Equal(0xFF, pkt[1]);
        Assert.Equal(0xFF, pkt[2]);
        Assert.Equal(0xFF, pkt[3]);
    }

    /// <summary>The ASCII command/body after the four 0xFF header bytes.</summary>
    private static string AsciiBody(byte[] pkt) =>
        Encoding.ASCII.GetString(pkt, 4, pkt.Length - 4);
}
