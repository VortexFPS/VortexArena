using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The server side of the DarkPlaces connection: <see cref="SvConnectionless{TAddress}"/> against the
/// existing client handshake, <see cref="SvDownload"/> against the existing client download over a
/// lossy link made of two real netchans, and one loopback smoke test of <see cref="SvUdpTransport"/>.
/// </summary>
public class ServerConnectionTests
{
    // ---------------------------------------------------------------- a scripted server

    private sealed class FakeHost : ISvConnectionlessHost<string>
    {
        public string GameName { get; set; } = "Xonotic";
        public string ModName { get; set; } = "data";
        public int GameVersion { get; set; } = 806;
        public int MaxClients { get; set; } = 16;
        public string MapName { get; set; } = "stormkeep";
        public string HostName { get; set; } = "Test Server";
        public string WorldStatus { get; set; } = "";
        public int TeamPlay { get; set; }

        public List<SvStatusPlayer> Players { get; } = new();
        /// <summary>address → begun</summary>
        public Dictionary<string, bool> Clients { get; } = new();
        public List<(string Address, string UserInfo)> Connected { get; } = new();
        public List<string> Reconnected { get; } = new();
        public int FreeSlots { get; set; } = 16;

        public void GetStatusPlayers(List<SvStatusPlayer> players) => players.AddRange(Players);

        public SvClientPresence FindClient(string address) =>
            !Clients.TryGetValue(address, out bool begun) ? SvClientPresence.None
            : begun ? SvClientPresence.Begun : SvClientPresence.Connecting;

        public bool TryConnectClient(string address, string userInfo)
        {
            if (FreeSlots <= 0)
                return false;
            FreeSlots--;
            Clients[address] = false;
            Connected.Add((address, userInfo));
            return true;
        }

        public void ReconnectClient(string address) => Reconnected.Add(address);
    }

    // "host:port" → "host"
    private static string NoPort(string address) => address[..address.LastIndexOf(':')];

    private static SvConnectionless<string> NewServer(FakeHost host, int seed = 1) =>
        new(host, NoPort, random: new Random(seed));

    private static byte[] Oob(string text)
    {
        byte[] body = Encoding.Latin1.GetBytes(text);
        var packet = new byte[4 + body.Length];
        packet.AsSpan(0, 4).Fill(255);
        body.CopyTo(packet, 4);
        return packet;
    }

    private static string Text(byte[] packet) => Encoding.Latin1.GetString(packet, 4, packet.Length - 4);

    /// <summary>Ask for a challenge and return it.</summary>
    private static string Challenge(SvConnectionless<string> sv, string from, double now)
    {
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Challenge, sv.Handle(Oob("getchallenge"), from, now, replies).Kind);
        byte[] reply = Assert.Single(replies);
        string text = Text(reply);
        Assert.StartsWith("challenge ", text);
        Assert.Equal('\0', text[^1]); // sent with its terminator, as DarkPlaces does
        return text[10..^1];
    }

    private static string Connect(string challenge) =>
        "connect\\protocol\\darkplaces 3\\protocols\\DP7\\challenge\\" + challenge;

    // ---------------------------------------------------------------- handshake

    [Fact]
    public void Client_Handshake_Completes_Against_The_Server()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        var hs = new DpConnectionHandshake();
        const string addr = "10.0.0.1:1234";

        double now = 10;
        hs.Start(now);
        var toServer = new List<byte[]>();
        var toClient = new List<byte[]>();
        var kinds = new List<SvConnectionlessKind>();
        for (int step = 0; step < 10 && hs.State == DpHandshakeState.Connecting; step++)
        {
            hs.Update(now, toServer);
            foreach (byte[] d in toServer)
                kinds.Add(sv.Handle(d, addr, now, toClient).Kind);
            toServer.Clear();
            foreach (byte[] d in toClient)
                Assert.True(hs.Receive(d, now, toServer));
            toClient.Clear();
            now += 0.1;
        }

        Assert.Equal(DpHandshakeState.Accepted, hs.State);
        Assert.Equal(new[] { SvConnectionlessKind.Challenge, SvConnectionlessKind.Accepted }, kinds);
        Assert.Equal(SvConnectionless<string>.ChallengeLength, hs.Challenge.Length);
        Assert.All(hs.Challenge, c => Assert.True(c >= 33 && c < 127 && !"\\;\"%/".Contains(c)));
        (string address, string userInfo) = Assert.Single(host.Connected);
        Assert.Equal(addr, address);
        Assert.Equal("\\protocol\\darkplaces 3\\protocols\\DP7\\challenge\\" + hs.Challenge, userInfo);
    }

    [Fact]
    public void Accept_Is_Exactly_Six_Bytes_After_The_Marker()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        string c = Challenge(sv, "a:1", 1);
        var replies = new List<byte[]>();
        SvConnectionlessResult r = sv.Handle(Oob(Connect(c)), "a:1", 1.1, replies);
        Assert.Equal(SvConnectionlessKind.Accepted, r.Kind);
        Assert.Equal(new byte[] { 255, 255, 255, 255, (byte)'a', (byte)'c', (byte)'c', (byte)'e', (byte)'p', (byte)'t' }, Assert.Single(replies));
    }

    [Fact]
    public void Wrong_Missing_Or_Foreign_Challenge_Is_Dropped_Silently()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        string c = Challenge(sv, "a:1", 1);
        var replies = new List<byte[]>();

        // not the string that was issued
        string wrong = (c[0] == 'x' ? "y" : "x") + c[1..];
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect(wrong)), "a:1", 1.1, replies).Kind);
        // a prefix and an extension of it
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect(c[..10])), "a:1", 1.1, replies).Kind);
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect(c + "x")), "a:1", 1.1, replies).Kind);
        // the right string from another port, and from another host
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect(c)), "a:2", 1.1, replies).Kind);
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect(c)), "b:1", 1.1, replies).Kind);
        // no challenge at all, and an empty one
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob("connect\\protocol\\darkplaces 3"), "a:1", 1.1, replies).Kind);
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob("connect\\protocol\\darkplaces 3\\challenge\\"), "a:1", 1.1, replies).Kind);

        Assert.Empty(replies);
        Assert.Empty(host.Connected);

        // and the real one still works afterwards
        Assert.Equal(SvConnectionlessKind.Accepted, sv.Handle(Oob(Connect(c)), "a:1", 1.2, replies).Kind);
    }

    [Fact]
    public void Connect_Without_A_Challenge_Is_Accepted_Only_In_DarkPlaces_Compatible_Mode()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        sv.AllowConnectWithoutChallenge = true;
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Accepted, sv.Handle(Oob("connect\\protocol\\darkplaces 3"), "a:1", 1, replies).Kind);
        // a challenge that is present must still be right
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect("AAAAAAAAAAA")), "b:1", 1, replies).Kind);
    }

    [Fact]
    public void Challenge_Lifetime_Is_Unlimited_By_Default_And_Enforced_When_Set()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        string c = Challenge(sv, "a:1", 1);
        var replies = new List<byte[]>();
        sv.ChallengeLifetime = 30;
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect(c)), "a:1", 100, replies).Kind);
        sv.ChallengeLifetime = 0;
        Assert.Equal(SvConnectionlessKind.Accepted, sv.Handle(Oob(Connect(c)), "a:1", 100000, replies).Kind);
    }

    [Fact]
    public void Wrong_Protocol_And_Full_Server_Are_Rejected_With_The_DarkPlaces_Texts()
    {
        var host = new FakeHost { FreeSlots = 0 };
        var sv = NewServer(host);
        var replies = new List<byte[]>();

        string c = Challenge(sv, "a:1", 1);
        SvConnectionlessResult r = sv.Handle(Oob("connect\\protocol\\quake 3\\challenge\\" + c), "a:1", 1, replies);
        Assert.Equal(SvConnectionlessKind.Rejected, r.Kind);
        Assert.Equal("Wrong game protocol.", r.RejectReason);
        Assert.Equal("reject Wrong game protocol.", Text(replies[^1]));
        // no protocol key at all
        r = sv.Handle(Oob("connect\\challenge\\" + c), "a:1", 1, replies);
        Assert.Equal("reject Wrong game protocol.", Text(replies[^1]));

        r = sv.Handle(Oob(Connect(c)), "a:1", 1, replies);
        Assert.Equal(SvConnectionlessKind.Rejected, r.Kind);
        Assert.Equal("reject Server is full.", Text(replies[^1]));

        // and the existing client reads that reason
        var hs = new DpConnectionHandshake();
        hs.Start(1);
        Assert.True(hs.Receive(replies[^1], 1, new List<byte[]>()));
        Assert.Equal(DpHandshakeState.Rejected, hs.State);
        Assert.Equal("Server is full.", hs.RejectReason);
    }

    [Fact]
    public void A_Known_Address_Is_Accepted_Again_Without_A_New_Slot()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        var replies = new List<byte[]>();
        string c = Challenge(sv, "a:1", 1);
        Assert.Equal(SvConnectionlessKind.Accepted, sv.Handle(Oob(Connect(c)), "a:1", 1, replies).Kind);

        // the accept was lost and the client retries: answered at once, flood limit or not
        Assert.Equal(SvConnectionlessKind.AcceptedDuplicate, sv.Handle(Oob(Connect(c)), "a:1", 1.01, replies).Kind);
        Assert.Equal("accept", Text(replies[^1]));
        Assert.Single(host.Connected);
        Assert.Empty(host.Reconnected);

        // the client was in the game, crashed and came back from the same address
        host.Clients["a:1"] = true;
        Assert.Equal(SvConnectionlessKind.AcceptedReconnect, sv.Handle(Oob(Connect(c)), "a:1", 1.02, replies).Kind);
        Assert.Equal("accept", Text(replies[^1]));
        Assert.Single(host.Connected);
        Assert.Equal(new[] { "a:1" }, host.Reconnected);
    }

    [Fact]
    public void Sv_Public_Gates()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        var replies = new List<byte[]>();
        string c = Challenge(sv, "a:1", 1);

        sv.Public = -1;
        Assert.Equal(SvConnectionlessKind.Ignored, sv.Handle(Oob("getinfo"), "a:1", 2, replies).Kind);
        Assert.Equal(SvConnectionlessKind.Ignored, sv.Handle(Oob("getstatus"), "a:1", 2, replies).Kind);
        Assert.Empty(replies);

        sv.Public = -2;
        SvConnectionlessResult r = sv.Handle(Oob(Connect(c)), "a:1", 2, replies);
        Assert.Equal(SvConnectionlessKind.Rejected, r.Kind);
        Assert.Equal("reject The server is closing.", Text(replies[^1]));

        sv.Public = -3;
        replies.Clear();
        Assert.Equal(SvConnectionlessKind.Ignored, sv.Handle(Oob("getchallenge"), "b:1", 2, replies).Kind);
        Assert.Empty(replies);
    }

    [Fact]
    public void Ping_Is_Answered_With_Ack_And_Other_Packets_Are_Not_Answered()
    {
        var sv = NewServer(new FakeHost());
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Ack, sv.Handle(Oob("ping"), "a:1", 1, replies).Kind);
        Assert.Equal("ack", Text(Assert.Single(replies)));
        replies.Clear();

        Assert.Equal(SvConnectionlessKind.Ignored, sv.Handle(Oob("ack"), "a:1", 1, replies).Kind);
        Assert.Equal(SvConnectionlessKind.Ignored, sv.Handle(Oob("rcon password status"), "a:1", 1, replies).Kind);
        Assert.Equal(SvConnectionlessKind.Ignored, sv.Handle(Oob("x"), "a:1", 1, replies).Kind);
        // in-band and too-short datagrams are not ours
        Assert.Equal(SvConnectionlessKind.NotConnectionless, sv.Handle(new byte[] { 0, 1, 0, 12, 0, 0, 0, 0, 1, 2, 3, 4 }, "a:1", 1, replies).Kind);
        Assert.Equal(SvConnectionlessKind.NotConnectionless, sv.Handle(new byte[] { 255, 255, 255, 255 }, "a:1", 1, replies).Kind);
        Assert.Equal(SvConnectionlessKind.NotConnectionless, sv.Handle(ReadOnlySpan<byte>.Empty, "a:1", 1, replies).Kind);
        Assert.Empty(replies);
    }

    // ---------------------------------------------------------------- flood limits

    [Fact]
    public void Challenge_Requests_Are_Limited_Per_Address_And_Reuse_The_Same_String()
    {
        var sv = NewServer(new FakeHost());
        var replies = new List<byte[]>();
        string first = Challenge(sv, "a:1", 10);

        // within net_challengefloodblockingtimeout (0.5 s): dropped
        Assert.Equal(SvConnectionlessKind.FloodDropped, sv.Handle(Oob("getchallenge"), "a:1", 10.4, replies).Kind);
        Assert.Empty(replies);
        // another address is not affected, and gets a different string
        string other = Challenge(sv, "a:2", 10.4);
        Assert.NotEqual(first, other);
        // after the timeout (counted from the last answered request): the same string again
        Assert.Equal(first, Challenge(sv, "a:1", 10.6));
        Assert.Equal(2, sv.ChallengeCount);
    }

    [Fact]
    public void Challenge_Table_Holds_128_And_Evicts_The_Oldest()
    {
        var host = new FakeHost();
        var sv = NewServer(host);
        string first = Challenge(sv, "h0:1", 1);
        for (int i = 1; i < SvConnectionless<string>.MaxChallenges; i++)
            Challenge(sv, $"h{i}:1", 1 + i * 0.001);
        Assert.Equal(128, sv.ChallengeCount);

        // all 128 are still valid: the first connects
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Accepted, sv.Handle(Oob(Connect(first)), "h0:1", 2, replies).Kind);

        // the 129th address takes the oldest slot (h0's), so h1's challenge survives and h0's does not
        string second = Challenge(sv, "h1:1", 3); // same string, refreshed
        Challenge(sv, "new:1", 3);
        Assert.Equal(128, sv.ChallengeCount);
        host.Clients.Clear();
        sv.ClearConnectFlood("h0:1");
        Assert.Equal(SvConnectionlessKind.BadChallenge, sv.Handle(Oob(Connect(first)), "h0:1", 3, replies).Kind);
        Assert.Equal(SvConnectionlessKind.Accepted, sv.Handle(Oob(Connect(second)), "h1:1", 3, replies).Kind);
    }

    [Fact]
    public void Connect_Requests_Are_Limited_Per_Host_And_The_Ban_Renews()
    {
        var host = new FakeHost { FreeSlots = 0 }; // full, so no request ever becomes a known client
        var sv = NewServer(host);
        var replies = new List<byte[]>();
        string c1 = Challenge(sv, "a:1", 10);
        string c2 = Challenge(sv, "a:2", 10);
        string cb = Challenge(sv, "b:1", 10);

        Assert.Equal(SvConnectionlessKind.Rejected, sv.Handle(Oob(Connect(c1)), "a:1", 10, replies).Kind);
        // same host, other port, within net_connectfloodblockingtimeout (5 s): dropped without a reply
        replies.Clear();
        Assert.Equal(SvConnectionlessKind.FloodDropped, sv.Handle(Oob(Connect(c2)), "a:2", 12, replies).Kind);
        // the dropped request renewed the ban: 10 + 5 has passed, 12 + 5 has not
        Assert.Equal(SvConnectionlessKind.FloodDropped, sv.Handle(Oob(Connect(c1)), "a:1", 16, replies).Kind);
        Assert.Empty(replies);
        // another host is not affected
        Assert.Equal(SvConnectionlessKind.Rejected, sv.Handle(Oob(Connect(cb)), "b:1", 16, replies).Kind);
        // 16 + 5 has passed
        Assert.Equal(SvConnectionlessKind.Rejected, sv.Handle(Oob(Connect(c1)), "a:1", 21.5, replies).Kind);

        // NetConn_Close clears the ban so a dropped client can come straight back
        Assert.Equal(SvConnectionlessKind.FloodDropped, sv.Handle(Oob(Connect(c1)), "a:1", 22, replies).Kind);
        sv.ClearConnectFlood("a:9");
        Assert.Equal(SvConnectionlessKind.Rejected, sv.Handle(Oob(Connect(c1)), "a:1", 22.1, replies).Kind);

        // a level change empties the table
        Assert.Equal(SvConnectionlessKind.FloodDropped, sv.Handle(Oob(Connect(c1)), "a:1", 22.2, replies).Kind);
        sv.ResetFloodTables();
        Assert.Equal(SvConnectionlessKind.Rejected, sv.Handle(Oob(Connect(c1)), "a:1", 22.3, replies).Kind);
    }

    [Fact]
    public void Status_Queries_Are_Limited_Per_Host_Without_Renewal()
    {
        var sv = NewServer(new FakeHost());
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Info, sv.Handle(Oob("getinfo"), "a:1", 10, replies).Kind);
        // getinfo and getstatus share the table, and the port does not matter
        Assert.Equal(SvConnectionlessKind.FloodDropped, sv.Handle(Oob("getstatus"), "a:2", 10.5, replies).Kind);
        Assert.Equal(SvConnectionlessKind.FloodDropped, sv.Handle(Oob("getinfo"), "a:1", 10.9, replies).Kind);
        // not renewed by the dropped ones: one second after the answered query it is open again
        Assert.Equal(SvConnectionlessKind.Status, sv.Handle(Oob("getstatus"), "a:1", 11.0, replies).Kind);
        Assert.Equal(SvConnectionlessKind.Info, sv.Handle(Oob("getinfo"), "b:1", 11.0, replies).Kind);
        Assert.Equal(3, replies.Count);
    }

    // ---------------------------------------------------------------- getinfo / getstatus

    private static FakeHost HostWithPlayers()
    {
        var host = new FakeHost { WorldStatus = "dm:0.8.6:P0:S8:F4:MXonotic::score\\!!\n" };
        host.Players.Add(new SvStatusPlayer("Al\"i\\ce", 5, 0.0425f, false, 0x44, ""));
        host.Players.Add(new SvStatusPlayer("[BOT]Eve", 7, 0.5f, true, 0xDD, "kills: 3\t\"x\"\\"));
        host.Players.Add(new SvStatusPlayer("Spec", -666, 99f, false, 0, ""));
        return host;
    }

    private const string ExpectedInfo =
        "\\gamename\\Xonotic\\modname\\data\\gameversion\\806\\sv_maxclients\\16\\clients\\3\\bots\\1"
        + "\\mapname\\stormkeep\\hostname\\Test Server\\protocol\\3\\qcstatus\\dm:0.8.6:P0:S8:F4:MXonotic::score!!";

    [Fact]
    public void GetInfo_Writes_The_DarkPlaces_Keys_In_Order_And_Parses_Back()
    {
        var sv = NewServer(HostWithPlayers());
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Info, sv.Handle(Oob("getinfo abc123"), "a:1", 1, replies).Kind);
        byte[] reply = Assert.Single(replies);
        Assert.Equal("infoResponse\n" + ExpectedInfo + "\\challenge\\abc123", Text(reply));

        IReadOnlyDictionary<string, string> info = MasterServerProtocol.ParseInfoResponse(reply);
        Assert.Equal("Xonotic", info["gamename"]);
        Assert.Equal("data", info["modname"]);
        Assert.Equal("806", info["gameversion"]);
        Assert.Equal("16", info["sv_maxclients"]);
        Assert.Equal("3", info["clients"]);
        Assert.Equal("1", info["bots"]);
        Assert.Equal("stormkeep", info["mapname"]);
        Assert.Equal("Test Server", info["hostname"]);
        Assert.Equal("3", info["protocol"]);
        Assert.Equal("dm:0.8.6:P0:S8:F4:MXonotic::score!!", info["qcstatus"]);
        Assert.Equal("abc123", info["challenge"]);
        Assert.Equal(11, info.Count);

        // without a challenge, and without a world status, those keys are absent
        var bare = NewServer(new FakeHost());
        replies.Clear();
        bare.Handle(Oob("getinfo"), "a:1", 1, replies);
        info = MasterServerProtocol.ParseInfoResponse(replies[0]);
        Assert.False(info.ContainsKey("challenge"));
        Assert.False(info.ContainsKey("qcstatus"));
        Assert.Equal(9, info.Count);
    }

    [Fact]
    public void GetStatus_Adds_One_Line_Per_Player()
    {
        var sv = NewServer(HostWithPlayers());
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Status, sv.Handle(Oob("getstatus xyz"), "a:1", 1, replies).Kind);
        Assert.Equal(
            "statusResponse\n" + ExpectedInfo + "\\challenge\\xyz\n"
            + "5 42 \"Alice\"\n"            // frags, ping in ms, name without \" and \\
            + "kills:3x 0 \"[BOT]Eve\"\n"   // clientstatus in place of frags, a bot's ping is 0
            + "-666 9999 \"Spec\"\n",       // ping clamped
            Text(Assert.Single(replies)));
    }

    [Fact]
    public void GetStatus_In_Teamplay_Inserts_The_Team_Number()
    {
        FakeHost host = HostWithPlayers();
        host.TeamPlay = 1;
        host.Players.Add(new SvStatusPlayer("Y", 1, 0.0001f, false, 0xCC, ""));
        host.Players.Add(new SvStatusPlayer("P", 2, 0.0105f, false, 0x99, ""));
        host.Players.Add(new SvStatusPlayer("N", 3, 0.0105f, false, 0x12, ""));
        var sv = NewServer(host);
        var replies = new List<byte[]>();
        sv.Handle(Oob("getstatus"), "a:1", 1, replies);
        string[] lines = Text(replies[0]).Split('\n');
        Assert.Equal("statusResponse", lines[0]);
        Assert.Equal(new[]
        {
            "5 42 1 \"Alice\"",
            "kills:3x 0 2 \"[BOT]Eve\"",
            "-666 9999 0 \"Spec\"",
            "1 1 3 \"Y\"",       // ping never reported below 1 for a human
            "2 10 4 \"P\"",
            "3 10 0 \"N\"",
            "",
        }, lines[2..]);
    }

    [Fact]
    public void GetStatus_That_Does_Not_Fit_Degrades_To_InfoResponse_And_Oversize_Gets_Nothing()
    {
        FakeHost host = HostWithPlayers();
        for (int i = 0; i < 200; i++)
            host.Players.Add(new SvStatusPlayer(new string('n', 300) + i, i, 0.05f, false, 0, new string('s', 400)));
        var sv = NewServer(host);
        var replies = new List<byte[]>();
        Assert.Equal(SvConnectionlessKind.Info, sv.Handle(Oob("getstatus c"), "a:1", 1, replies).Kind);
        byte[] reply = Assert.Single(replies);
        Assert.True(reply.Length < SvConnectionless<string>.ResponseBufferSize);
        Assert.Equal("infoResponse\n" + ExpectedInfo.Replace("clients\\3", "clients\\203") + "\\challenge\\c", Text(reply));

        // long names and statuses are cut to the C buffer sizes, not dropped
        host.Players.RemoveRange(4, host.Players.Count - 4);
        replies.Clear();
        Assert.Equal(SvConnectionlessKind.Status, sv.Handle(Oob("getstatus"), "b:1", 1, replies).Kind);
        string last = Text(replies[0]).Split('\n')[^2];
        Assert.Equal(new string('s', 255) + " 50 \"" + new string('n', 127) + "\"", last);

        // a challenge too long to echo costs the asker the reply
        replies.Clear();
        Assert.Equal(SvConnectionlessKind.Ignored, sv.Handle(Oob("getinfo " + new string('c', 3000)), "c:1", 1, replies).Kind);
        Assert.Empty(replies);
    }

    // ---------------------------------------------------------------- hostile input

    /// <summary>The length of every array and the capacity of every list an object holds.</summary>
    private static Dictionary<string, int> StorageSizes(object o)
    {
        var sizes = new Dictionary<string, int>();
        foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            object? v = f.GetValue(o);
            if (v is Array a)
                sizes[f.Name] = a.Length;
            else if (v is IList && v.GetType().GetProperty("Capacity") is { } cap)
                sizes[f.Name] = (int)cap.GetValue(v)!;
        }
        return sizes;
    }

    [Fact]
    public void Hostile_Datagrams_Never_Throw()
    {
        var host = HostWithPlayers();
        var sv = NewServer(host);
        var replies = new List<byte[]>();
        var rng = new Random(1234);
        double now = 1;

        string[] seeds =
        {
            "getchallenge", "getchallenge extra\0\0junk", "connect\\", "connect\\\\\\\\\\", "connect\\challenge",
            "connect\\challenge\\", "connect\\protocol\\darkplaces 3\\challenge\\\0", "connect\\\0challenge\\x",
            "getinfo", "getinfo ", "getinfo \0", "getinfo\0 x", "getinfo \\\\\n\"%s%n", "getstatus", "getstatus ",
            "getstatus \xFF\xFE\x80", "ping", "pin", "ping\0", "ack", "srcon HMAC-MD4 TIME ", "rcon ", "extResponse ",
            "\0", "\xFF\xFF\xFF\xFF", "connect\\protocol\\darkplaces 3\\protocols\\DP7\\challenge\\AAAAAAAAAAA\\message\\hi",
        };
        foreach (string s in seeds)
        {
            byte[] packet = Oob(s);
            // every truncation of it, the marker included
            for (int n = 0; n <= packet.Length; n++)
                sv.Handle(packet.AsSpan(0, n), "h" + rng.Next(50) + ":" + rng.Next(5), now += 0.01, replies);
        }

        // huge: the largest datagram there is, of each kind
        foreach (string prefix in new[] { "getchallenge", "connect\\", "connect\\challenge\\", "getinfo ", "getstatus ", "ping", "" })
        {
            foreach (byte fill in new byte[] { (byte)'\\', (byte)'A', 0, 0xFF, (byte)'\n' })
            {
                var big = new byte[65536];
                big.AsSpan().Fill(fill);
                Oob(prefix).CopyTo(big, 0);
                sv.Handle(big, "big:1", now += 2, replies);
            }
        }

        // noise, with and without the marker
        var noise = new byte[2048];
        for (int i = 0; i < 20000; i++)
        {
            int n = rng.Next(noise.Length);
            rng.NextBytes(noise.AsSpan(0, n));
            if ((i & 1) == 0 && n >= 4)
                noise.AsSpan(0, 4).Fill(255);
            if ((i & 3) == 0 && n >= 16)
            {
                string seed = seeds[rng.Next(seeds.Length)];
                Encoding.ASCII.GetBytes(seed.AsSpan(0, Math.Min(12, seed.Length)), noise.AsSpan(4));
            }
            sv.Handle(noise.AsSpan(0, n), "n" + rng.Next(300) + ":1", now += 0.001, replies);
            if (replies.Count > 1000)
                replies.Clear();
        }

        // every reply is connectionless and no longer than the response buffer
        Assert.All(replies, r => Assert.True(r.Length >= 5 && r.Length < SvConnectionless<string>.ResponseBufferSize && r[0] == 255));
        // nothing got in without a valid challenge
        Assert.Empty(host.Connected);
    }

    [Fact]
    public void Ten_Thousand_Addresses_Do_Not_Grow_The_Tables()
    {
        var host = new FakeHost { FreeSlots = 0 };
        var sv = NewServer(host);
        var replies = new List<byte[]>();

        // touch every path once so lazily sized storage has its final size, then record it
        string c0 = Challenge(sv, "warm:1", 1);
        sv.Handle(Oob(Connect(c0)), "warm:1", 1, replies);
        sv.Handle(Oob("getstatus x"), "warm:1", 1, replies);
        Dictionary<string, int> before = StorageSizes(sv);
        Assert.Equal(SvConnectionless<string>.MaxChallenges, before["_challenges"]);
        Assert.Equal(SvConnectionless<string>.MaxConnectFloodAddresses, before["_connectFlood"]);
        Assert.Equal(SvConnectionless<string>.MaxGetStatusFloodAddresses, before["_getStatusFlood"]);

        double now = 2;
        int rejected = 0;
        for (int i = 0; i < 10000; i++)
        {
            string addr = $"10.{i >> 8}.{i & 255}.7:{1000 + i}";
            now += 0.001;
            replies.Clear();
            Assert.Equal(SvConnectionlessKind.Challenge, sv.Handle(Oob("getchallenge"), addr, now, replies).Kind);
            string c = Text(replies[0])[10..^1];
            Assert.Equal(SvConnectionlessKind.Status, sv.Handle(Oob("getstatus"), addr, now, replies).Kind);
            Assert.Equal(SvConnectionlessKind.Info, sv.Handle(Oob("getinfo"), addr + "0", now + 1, replies).Kind);
            if (sv.Handle(Oob(Connect(c)), addr, now, replies).Kind == SvConnectionlessKind.Rejected)
                rejected++;
        }
        Assert.Equal(10000, rejected); // distinct hosts: none is flood-limited, all hear "Server is full."
        Assert.Equal(SvConnectionless<string>.MaxChallenges, sv.ChallengeCount);
        Assert.Equal(before, StorageSizes(sv));
    }

    // ---------------------------------------------------------------- download: requests

    private static readonly byte[] SmallFile = Encoding.ASCII.GetBytes("hello, download");

    private sealed class FileSystem
    {
        public Dictionary<string, SvDownloadFile> Files { get; } = new();
        public List<string> Asked { get; } = new();
        public SvDownloadFile? Lookup(string name)
        {
            Asked.Add(name);
            return Files.TryGetValue(name, out SvDownloadFile f) ? f : null;
        }
    }

    /// <summary>The svc_print and svc_stufftext texts in a reliable message, through the client's own parser.</summary>
    private sealed class TextCollector : IDpClientHandler
    {
        public List<string> Prints { get; } = new();
        public List<string> Stuffed { get; } = new();
        public void OnPrint(string text) => Prints.Add(text);
        public void OnStuffText(string text) => Stuffed.Add(text);
    }

    private static TextCollector Read(DpMessageWriter reliable)
    {
        var texts = new TextCollector();
        DpParseResult result = new DpServerMessageParser(texts).Parse(reliable.ToArray());
        Assert.Equal(DpParseStatus.Complete, result.Status);
        reliable.Clear();
        return texts;
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("maps/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/win.ini")]
    [InlineData("maps\\x.bsp")]
    [InlineData("maps//x.bsp")]
    [InlineData("./x.bsp")]
    [InlineData("maps/.hidden")]
    [InlineData("maps./x")]
    [InlineData("")]
    [InlineData("a b.txt")]
    [InlineData("a;quit")]
    [InlineData("a\"b")]
    [InlineData("a\nquit")]
    [InlineData("a\0../x")]
    public void Nasty_Names_Are_Refused_Before_The_File_System_Is_Asked(string name)
    {
        var fs = new FileSystem();
        fs.Files[name] = new SvDownloadFile(SmallFile);
        var sv = new SvDownload(new SvDownloadSettings { AllowInArchive = true, AllowArchive = true, AllowConfig = true, AllowDlCache = true }, fs.Lookup, () => null);
        var reliable = new DpMessageWriter();
        Assert.Equal(SvDownloadRequest.NastyName, sv.Download(new[] { "download", name }, reliable));
        Assert.Empty(fs.Asked);
        Assert.False(sv.Active);
        TextCollector texts = Read(reliable);
        Assert.StartsWith("Download rejected: nasty filename", Assert.Single(texts.Prints));
        Assert.Empty(texts.Stuffed); // as in DarkPlaces: a nasty name gets a print and no stopdownload
    }

    [Theory]
    [InlineData("server.cfg", SvDownloadRequest.ConfigFile, false)]
    [InlineData("sub/Autoexec.CFG", SvDownloadRequest.ConfigFile, false)]
    [InlineData("dlcache/csprogs.dat.1.2", SvDownloadRequest.DlCache, false)]
    [InlineData("DLCache/x.bsp", SvDownloadRequest.DlCache, false)]
    [InlineData("data.pk3", SvDownloadRequest.Archive, false)]
    [InlineData("id1/pak0.PAK", SvDownloadRequest.Archive, false)]
    [InlineData("x.dpk", SvDownloadRequest.Archive, false)]
    [InlineData("maps/missing.bsp", SvDownloadRequest.NotFound, true)]
    [InlineData("maps/packed.bsp", SvDownloadRequest.InArchive, true)]
    [InlineData("maps/huge.bsp", SvDownloadRequest.TooLarge, true)]
    public void Excluded_Files_Are_Refused_With_Stopdownload(string name, SvDownloadRequest expected, bool asksFileSystem)
    {
        var fs = new FileSystem();
        foreach (string n in new[] { "server.cfg", "sub/Autoexec.CFG", "dlcache/csprogs.dat.1.2", "DLCache/x.bsp", "data.pk3", "id1/pak0.PAK", "x.dpk" })
            fs.Files[n] = new SvDownloadFile(SmallFile);
        fs.Files["maps/packed.bsp"] = new SvDownloadFile(SmallFile, "data/xonotic-maps.pk3");
        fs.Files["maps/huge.bsp"] = new SvDownloadFile(new byte[100]);
        var sv = new SvDownload(new SvDownloadSettings { MaxFileSize = 99 }, fs.Lookup, () => null);
        var reliable = new DpMessageWriter();

        Assert.Equal(expected, sv.Download(new[] { "download", name, "deflate" }, reliable));
        Assert.Equal(asksFileSystem ? new[] { name } : Array.Empty<string>(), fs.Asked);
        Assert.False(sv.Active);
        TextCollector texts = Read(reliable);
        Assert.StartsWith("Download rejected: ", Assert.Single(texts.Prints));
        Assert.Contains(name, texts.Prints[0]);
        Assert.Equal(new[] { "\nstopdownload\n" }, texts.Stuffed);

        // and the client's signon reads that as "give up on this file"
        var download = new DpDownload();
        Assert.True(download.Begin(10, "x", false));
        var signon = new DpSignon(new DpSignonConfig(), download);
        Assert.True(signon.HandleCommand("stopdownload"));
        Assert.False(download.Active);
    }

    [Fact]
    public void Each_Exclusion_Follows_Its_Setting()
    {
        var fs = new FileSystem();
        foreach (string n in new[] { "server.cfg", "dlcache/x.bsp", "data.pk3", "maps/packed.bsp" })
            fs.Files[n] = new SvDownloadFile(SmallFile, n == "maps/packed.bsp" ? "data.pk3" : null);
        var settings = new SvDownloadSettings();
        var sv = new SvDownload(settings, fs.Lookup, () => null);
        var reliable = new DpMessageWriter();

        // the DarkPlaces defaults: downloads on, every exception off
        Assert.True(settings.AllowDownloads);
        Assert.False(settings.AllowInArchive || settings.AllowArchive || settings.AllowConfig || settings.AllowDlCache);
        Assert.Equal(1 << 30, settings.MaxFileSize);

        settings.AllowConfig = true;
        Assert.Equal(SvDownloadRequest.Begun, sv.Download(new[] { "download", "server.cfg" }, reliable));
        settings.AllowDlCache = true;
        Assert.Equal(SvDownloadRequest.Begun, sv.Download(new[] { "download", "dlcache/x.bsp" }, reliable));
        settings.AllowArchive = true;
        Assert.Equal(SvDownloadRequest.Begun, sv.Download(new[] { "download", "data.pk3" }, reliable));
        settings.AllowInArchive = true;
        Assert.Equal(SvDownloadRequest.Begun, sv.Download(new[] { "download", "maps/packed.bsp" }, reliable));

        // each new request aborted the one before it
        TextCollector texts = Read(reliable);
        Assert.Equal(new[]
        {
            "\ncl_downloadbegin 15 server.cfg\n",
            "\nstopdownload\n", "\ncl_downloadbegin 15 dlcache/x.bsp\n",
            "\nstopdownload\n", "\ncl_downloadbegin 15 data.pk3\n",
            "\nstopdownload\n", "\ncl_downloadbegin 15 maps/packed.bsp\n",
        }, texts.Stuffed);

        settings.AllowDownloads = false;
        Assert.Equal(SvDownloadRequest.Disabled, sv.Download(new[] { "download", "maps/packed.bsp" }, reliable));
        Assert.False(sv.Active);
        texts = Read(reliable);
        Assert.Equal(new[] { "Downloads are disabled on this server\n" }, texts.Prints);
        Assert.Equal(new[] { "\nstopdownload\n", "\nstopdownload\n" }, texts.Stuffed);
    }

    [Fact]
    public void Csprogs_Is_Served_From_Memory_Whatever_The_Settings_Say()
    {
        byte[] progs = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
        SvCsqcProgram csqc = SvCsqcProgram.Create("csprogs.dat", progs);
        Assert.Equal(progs.Length, csqc.Size);
        Assert.Equal(Crc16.Block(progs), csqc.Crc);
        Assert.NotNull(csqc.Deflated);
        Assert.Equal(progs, DpDownload.Inflate(csqc.Deflated!, 1 << 20)); // raw DEFLATE, what the client inflates

        var fs = new FileSystem();
        var sv = new SvDownload(new SvDownloadSettings { AllowDownloads = false }, fs.Lookup, () => csqc);
        var reliable = new DpMessageWriter();

        Assert.True(sv.HandleCommand("download csprogs.dat deflate", reliable));
        Assert.True(sv.Active && sv.Deflated && !sv.Started);
        Assert.Equal(csqc.Deflated!.Length, sv.Size);
        Assert.Equal(new[] { $"\ncl_downloadbegin {csqc.Deflated.Length} csprogs.dat deflate\n" }, Read(reliable).Stuffed);

        // a client that does not offer deflate gets the file as it is
        Assert.True(sv.HandleCommand("DOWNLOAD csprogs.dat lzo huffman", reliable));
        Assert.False(sv.Deflated);
        Assert.Equal(new[] { "\nstopdownload\n", "\ncl_downloadbegin 5000 csprogs.dat\n" }, Read(reliable).Stuffed);

        // and so does every client of a server that has no deflated copy
        SvCsqcProgram plain = SvCsqcProgram.Create("csprogs.dat", progs, deflate: false);
        var sv2 = new SvDownload(new SvDownloadSettings(), fs.Lookup, () => plain);
        Assert.True(sv2.HandleCommand("download csprogs.dat deflate", reliable));
        Assert.Equal(new[] { "\ncl_downloadbegin 5000 csprogs.dat\n" }, Read(reliable).Stuffed);

        Assert.Empty(fs.Asked);
        // other commands are not this class's
        Assert.False(sv.HandleCommand("prespawn", reliable));
        Assert.False(sv.HandleCommand("downloadx csprogs.dat", reliable));
        Assert.False(sv.HandleCommand("", reliable));
        Assert.Equal(0, reliable.Length);
    }

    [Fact]
    public void Usage_And_Hostile_Download_Input_Never_Throw()
    {
        var fs = new FileSystem();
        fs.Files["a.txt"] = new SvDownloadFile(SmallFile);
        var sv = new SvDownload(new SvDownloadSettings(), fs.Lookup, () => null);
        var reliable = new DpMessageWriter();

        Assert.True(sv.HandleCommand("download", reliable));
        Assert.Equal(2, Read(reliable).Prints.Count);

        // a 16 KB name costs the reliable buffer one short line, and is never looked up
        Assert.Equal(SvDownloadRequest.NastyName, sv.Download(new[] { "download", new string('a', 16000) }, reliable));
        Assert.True(reliable.Length < 64);
        Assert.Equal(SvDownloadRequest.NastyName, sv.Download(new[] { "download", new string('a', 128) }, reliable));
        Assert.Equal(SvDownloadRequest.NotFound, sv.Download(new[] { "download", new string('a', 127) }, reliable));
        Assert.Equal(new[] { new string('a', 127) }, fs.Asked);
        reliable.Clear();

        // acks and data with nothing active, then with a download that has not been started
        var datagram = new DpMessageWriter();
        Assert.False(sv.Ack(0, 100, reliable));
        Assert.False(sv.WriteData(datagram, 700));
        sv.StartDownload();
        Assert.False(sv.Started);
        Assert.Equal(SvDownloadRequest.Begun, sv.Download(new[] { "download", "a.txt" }, reliable));
        Assert.False(sv.Ack(0, 15, reliable));
        Assert.False(sv.WriteData(datagram, 700));
        sv.StartDownload();

        // wild acks only ever rewind; none finishes the file or moves a position out of range
        var rng = new Random(5);
        foreach ((int start, int size) in new[] { (-1, 5), (int.MinValue, -1), (int.MaxValue, int.MaxValue), (0, -1), (0, short.MinValue), (15, 0), (16, 1), (1, 14) })
        {
            Assert.False(sv.Ack(start, size, reliable));
            Assert.InRange(sv.Position, 0, sv.Size);
            Assert.Equal(0, sv.ExpectedPosition);
        }
        for (int i = 0; i < 1000; i++)
        {
            sv.Ack(rng.Next(-20, 20) == 0 ? 0 : rng.Next(int.MinValue, int.MaxValue), rng.Next(short.MinValue, 0), reliable);
            sv.WriteData(datagram, rng.Next(-5, 2000), rng.Next(-5, 2000));
            Assert.InRange(sv.Position, 0, sv.Size);
            if (datagram.Length > 60000)
                datagram.Clear();
        }
        Assert.True(sv.Active);

        // no room, no block: a full writer, a zero budget
        var tiny = new DpMessageWriter(maxSize: 7);
        Assert.False(sv.WriteData(tiny, 700));
        Assert.False(sv.WriteData(new DpMessageWriter(), 0));
        Assert.False(sv.WriteData(new DpMessageWriter(), int.MaxValue, 7));
        var eight = new DpMessageWriter(maxSize: 8);
        sv.Ack(-1, 0, reliable); // rewind to 0
        Assert.True(sv.WriteData(eight, int.MaxValue));
        Assert.Equal(new byte[] { 50, 0, 0, 0, 0, 1, 0, (byte)'h' }, eight.ToArray());

        // an honest overshooting ack finishes: start + size is compared against the size, not required to equal it
        reliable.Clear();
        Assert.True(sv.Ack(0, 32767, reliable));
        Assert.False(sv.Active);
        Assert.Equal(new[] { $"\ncl_downloadfinished 15 {Crc16.Block(SmallFile)} a.txt\n" }, Read(reliable).Stuffed);
    }

    [Fact]
    public void An_Empty_File_Downloads()
    {
        var fs = new FileSystem();
        fs.Files["empty.txt"] = new SvDownloadFile(Array.Empty<byte>());
        var sv = new SvDownload(new SvDownloadSettings(), fs.Lookup, () => null);
        LinkStats stats = RunDownload(sv, "download empty.txt", seed: 3, loss: 0, out DpDownloadResult result);
        Assert.Equal(DpDownloadStatus.Completed, result.Status);
        Assert.Empty(result.Data);
        Assert.Equal(1, sv.Completed);
        Assert.True(stats.Ticks < 20);
    }

    // ---------------------------------------------------------------- download: the transfer

    private sealed class LinkStats
    {
        public int Ticks, Dropped, Duplicated, Delayed, BlocksReceived, AcksReceived;
        public List<string> Stuffed { get; } = new();
    }

    /// <summary>
    /// One direction of a bad network: each datagram may be lost, delivered twice, or held back until
    /// after the next batch (which reorders it behind newer ones).
    /// </summary>
    private sealed class LossyLink
    {
        private readonly Random _rng;
        private readonly double _loss;
        private readonly LinkStats _stats;
        private readonly List<byte[]> _held = new();

        public LossyLink(Random rng, double loss, LinkStats stats)
        {
            _rng = rng;
            _loss = loss;
            _stats = stats;
        }

        public List<byte[]> Deliver(List<byte[]> sent)
        {
            var arriving = new List<byte[]>();
            var late = new List<byte[]>(_held);
            _held.Clear();
            foreach (byte[] d in sent)
            {
                double roll = _rng.NextDouble();
                if (roll < _loss)
                    _stats.Dropped++;
                else if (roll < _loss * 2)
                {
                    arriving.Add(d);
                    arriving.Add(d);
                    _stats.Duplicated++;
                }
                else if (roll < _loss * 3)
                {
                    _held.Add(d);
                    _stats.Delayed++;
                }
                else
                    arriving.Add(d);
            }
            arriving.AddRange(late); // last tick's stragglers arrive behind this tick's datagrams
            sent.Clear();
            return arriving;
        }
    }

    /// <summary>What a DP7 client does with the messages of a download, using the client's own classes.</summary>
    private sealed class DownloadingClient : IDpClientHandler
    {
        private readonly DpStuffTextBuffer _stuff = new();
        private readonly List<string> _lines = new();
        private readonly LinkStats _stats;
        public DownloadingClient(LinkStats stats)
        {
            _stats = stats;
            Download = new DpDownload();
            Signon = new DpSignon(new DpSignonConfig(), Download);
        }

        public DpDownload Download { get; }
        public DpSignon Signon { get; }

        public void OnStuffText(string text)
        {
            _stats.Stuffed.Add(text);
            _lines.Clear();
            _stuff.Add(text, _lines);
            foreach (string line in _lines)
                Signon.HandleCommand(line);
        }

        public void OnDownloadData(int start, ReadOnlySpan<byte> data)
        {
            _stats.BlocksReceived++;
            Assert.True(Download.OnData(start, data));
        }
    }

    /// <summary>
    /// Run one download to its end: the server's <see cref="SvDownload"/> on one side, the client's
    /// DpDownload/DpSignon/DpServerMessageParser on the other, two DpNetChannels between them, and a
    /// lossy link in each direction. The netchans are part of the mechanism and not scaffolding:
    /// their sequence numbers are what turn a duplicated or reordered ack into a lost one, which is
    /// the only kind of trouble the download protocol itself recovers from.
    /// </summary>
    private static LinkStats RunDownload(SvDownload sv, string request, int seed, double loss, out DpDownloadResult result)
    {
        var stats = new LinkStats();
        var rng = new Random(seed);
        double now = 100;
        var serverChannel = new DpNetChannel(now);
        var clientChannel = new DpNetChannel(now);
        var toClient = new LossyLink(rng, loss, stats);
        var toServer = new LossyLink(rng, loss, stats);
        var client = new DownloadingClient(stats);
        var parser = new DpServerMessageParser(client);

        var serverOut = new List<byte[]>();
        var clientOut = new List<byte[]>();
        var datagram = new DpMessageWriter();
        var input = new DpMessageWriter();
        var acks = new List<DpDownloadAck>();

        DpClientMessages.WriteStringCommand(clientChannel.Reliable, request);

        for (stats.Ticks = 0; stats.Ticks < 400000 && client.Signon.LastDownload is null; stats.Ticks++)
        {
            now += 0.02;

            // --- server frame: SV_SendClientDatagram with nothing but the download in it
            datagram.Clear();
            if (!sv.WriteData(datagram, maxSize: 1400 / 2))
                datagram.WriteByte((int)Svc.Nop); // the keepalive of a client that is not in the game yet
            Assert.True(serverChannel.Transmit(datagram.WrittenSpan, now, serverOut));

            foreach (byte[] d in toClient.Deliver(serverOut))
            {
                if (clientChannel.Receive(d, now, clientOut, out byte[]? message) == DpChannelReceive.Message)
                    Assert.Equal(DpParseStatus.Complete, parser.Parse(message!).Status);
            }

            // --- client frame: forward the signon's commands, ack what arrived
            foreach (string command in client.Signon.Commands)
                DpClientMessages.WriteStringCommand(clientChannel.Reliable, command);
            client.Signon.Commands.Clear();
            acks.Clear();
            client.Download.TakeAcks(acks);
            input.Clear();
            foreach (DpDownloadAck ack in acks)
                DpClientMessages.WriteAckDownloadData(input, ack.Start, ack.Size);
            Assert.True(clientChannel.Transmit(input.WrittenSpan, now, clientOut));

            foreach (byte[] d in toServer.Deliver(clientOut))
            {
                if (serverChannel.Receive(d, now, serverOut, out byte[]? message) != DpChannelReceive.Message)
                    continue;
                // SV_ReadClientMessage, as far as this test's client goes
                var r = new DpMessageReader(message!);
                while (r.Remaining > 0 && !r.BadRead)
                {
                    switch ((Clc)r.ReadByte())
                    {
                        case Clc.Nop:
                            break;
                        case Clc.StringCmd:
                            sv.HandleCommand(r.ReadString(), serverChannel.Reliable);
                            break;
                        case Clc.AckDownloadData:
                        {
                            int start = r.ReadLong();
                            int size = r.ReadShort();
                            stats.AcksReceived++;
                            sv.Ack(start, size, serverChannel.Reliable);
                            break;
                        }
                        default:
                            Assert.Fail("unexpected clc");
                            break;
                    }
                }
                Assert.False(r.BadRead);
            }
        }

        Assert.NotNull(client.Signon.LastDownload);
        result = client.Signon.LastDownload!;
        return stats;
    }

    private static byte[] Blob(int size, int seed, bool compressible)
    {
        var data = new byte[size];
        var rng = new Random(seed);
        if (!compressible)
        {
            rng.NextBytes(data);
            return data;
        }
        // runs of repeated short phrases: deflates well without being trivial
        for (int i = 0; i < size;)
        {
            int word = rng.Next(64);
            int run = rng.Next(4, 40);
            for (int j = 0; j < run && i < size; j++, i++)
                data[i] = (byte)(word * 3 + j % 5);
        }
        return data;
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.02, 2)]
    [InlineData(0.08, 3)]
    public void Client_Downloads_A_Multi_Megabyte_File_Over_A_Lossy_Link(double loss, int seed)
    {
        byte[] file = Blob(2_600_000, seed, compressible: false);
        var fs = new FileSystem();
        fs.Files["maps/big.bsp"] = new SvDownloadFile(file);
        var sv = new SvDownload(new SvDownloadSettings(), fs.Lookup, () => null);

        // "deflate" is offered and, for an ordinary file, not taken up
        LinkStats stats = RunDownload(sv, "download maps/big.bsp deflate", seed, loss, out DpDownloadResult result);

        Assert.Equal(DpDownloadStatus.Completed, result.Status);
        Assert.Equal("maps/big.bsp", result.Name);
        Assert.False(result.WasDeflated);
        Assert.Equal(file.Length, result.WireSize);
        Assert.Equal(file.Length, result.Data.Length);
        Assert.True(file.AsSpan().SequenceEqual(result.Data));
        Assert.Equal(Crc16.Block(file), result.Crc);
        Assert.Equal(1, sv.Completed);
        Assert.False(sv.Active);
        Assert.Contains($"\ncl_downloadbegin {file.Length} maps/big.bsp\n", stats.Stuffed);
        Assert.Contains($"\ncl_downloadfinished {file.Length} {Crc16.Block(file)} maps/big.bsp\n", stats.Stuffed);
        // 1400-byte blocks, so at least this many, and the lossy runs really were lossy
        Assert.True(stats.BlocksReceived >= file.Length / SvDownload.MaxBlockSize);
        if (loss > 0)
        {
            Assert.True(stats.Dropped > 20 && stats.Duplicated > 20 && stats.Delayed > 20, $"{stats.Dropped}/{stats.Duplicated}/{stats.Delayed}");
            Assert.True(sv.Rewinds > 20);
        }
        else
            Assert.Equal(0, sv.Rewinds);
    }

    [Theory]
    [InlineData(true, 0.0, 11)]
    [InlineData(true, 0.05, 12)]
    [InlineData(false, 0.05, 13)]
    public void Client_Downloads_Csprogs_With_And_Without_Deflate(bool deflate, double loss, int seed)
    {
        byte[] progs = Blob(3_200_000, seed, compressible: true);
        SvCsqcProgram csqc = SvCsqcProgram.Create("csprogs.dat", progs);
        Assert.True(csqc.Deflated!.Length < progs.Length / 2);
        var sv = new SvDownload(new SvDownloadSettings { AllowDownloads = false }, _ => null, () => csqc);

        LinkStats stats = RunDownload(sv, deflate ? "download csprogs.dat deflate" : "download csprogs.dat", seed, loss, out DpDownloadResult result);

        Assert.Equal(DpDownloadStatus.Completed, result.Status);
        Assert.Equal("csprogs.dat", result.Name);
        Assert.Equal(deflate, result.WasDeflated);
        // the size and CRC on the wire describe the bytes as sent; the file is what the csqc_prog* cvars describe
        byte[] wire = deflate ? csqc.Deflated : progs;
        Assert.Equal(wire.Length, result.WireSize);
        Assert.Contains($"\ncl_downloadbegin {wire.Length} csprogs.dat{(deflate ? " deflate" : "")}\n", stats.Stuffed);
        Assert.Contains($"\ncl_downloadfinished {wire.Length} {Crc16.Block(wire)} csprogs.dat\n", stats.Stuffed);
        Assert.Equal(csqc.Size, result.Data.Length);
        Assert.Equal(csqc.Crc, result.Crc);
        Assert.True(progs.AsSpan().SequenceEqual(result.Data));
        Assert.Equal(1, sv.Completed);
    }

    // ---------------------------------------------------------------- UDP

    [Fact]
    public void Udp_Transport_Loopback_Smoke()
    {
        using var transport = new SvUdpTransport(new IPEndPoint(IPAddress.Loopback, 0)) { MaxDatagramSize = 512 };
        IPEndPoint serverAddress = transport.LocalEndPoint;
        Assert.NotEqual(0, serverAddress.Port);
        var sv = new SvConnectionless<IPEndPoint>(HostFor<IPEndPoint>(), SvUdpTransport.WithoutPort);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 5000;
        client.Send(Array.Empty<byte>(), 0, serverAddress);   // empty: dropped
        client.Send(new byte[600], 600, serverAddress);        // over MaxDatagramSize: dropped
        byte[] query = Oob("getinfo smoke");
        client.Send(query, query.Length, serverAddress);

        var replies = new List<byte[]>();
        SvConnectionlessKind kind = SvConnectionlessKind.NotConnectionless;
        for (int i = 0; i < 500 && kind != SvConnectionlessKind.Info; i++)
        {
            if (!transport.TryReceive(out byte[] datagram, out IPEndPoint from))
            {
                Thread.Sleep(10);
                continue;
            }
            Assert.Equal(query, datagram);
            Assert.Equal(((IPEndPoint)client.Client.LocalEndPoint!).Port, from.Port);
            kind = sv.Handle(datagram, from, 1 + i, replies).Kind;
            foreach (byte[] reply in replies)
                transport.Send(reply, from);
        }
        Assert.Equal(SvConnectionlessKind.Info, kind);
        Assert.Equal(1, transport.DatagramsReceived);
        Assert.Equal(2, transport.DatagramsDropped);
        Assert.Equal(1, transport.DatagramsSent);
        Assert.False(transport.TryReceive(out _, out _));

        var any = new IPEndPoint(IPAddress.Any, 0);
        IReadOnlyDictionary<string, string> info = MasterServerProtocol.ParseInfoResponse(client.Receive(ref any));
        Assert.Equal(serverAddress.Port, any.Port);
        Assert.Equal("Xonotic", info["gamename"]);
        Assert.Equal("smoke", info["challenge"]);

        // IPEndPoint works as the address key: equal by value, and the flood key ignores the port
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 0), SvUdpTransport.WithoutPort(new IPEndPoint(IPAddress.Loopback, 777)));
    }

    private sealed class GenericHost<T> : ISvConnectionlessHost<T>
    {
        public string GameName => "Xonotic";
        public string ModName => "data";
        public int GameVersion => 806;
        public int MaxClients => 8;
        public string MapName => "boil";
        public string HostName => "udp";
        public string WorldStatus => "";
        public int TeamPlay => 0;
        public void GetStatusPlayers(List<SvStatusPlayer> players) { }
        public SvClientPresence FindClient(T address) => SvClientPresence.None;
        public bool TryConnectClient(T address, string userInfo) => true;
        public void ReconnectClient(T address) { }
    }

    private static ISvConnectionlessHost<T> HostFor<T>() => new GenericHost<T>();
}
