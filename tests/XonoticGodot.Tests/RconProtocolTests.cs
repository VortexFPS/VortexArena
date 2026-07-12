using System;
using System.Text;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Round-trip + wire-layout tests for <see cref="RconProtocol"/> (DS-6), the DarkPlaces rcon/srcon codec. These
/// pin the exact byte offsets DP's netconn.c parses (prefix lengths 20/25, the raw 16-byte HMAC, the trailing
/// "&lt;time-or-challenge&gt; &lt;command&gt;" the HMAC covers) and that a client-built packet verifies server-side,
/// so our own tooling and stock DP rcon clients interoperate. The OOB 4×0xFF header is included by the builders.
/// </summary>
public class RconProtocolTests
{
    private const string Password = "s3cret";
    private static ReadOnlySpan<byte> AfterOob(byte[] packet) => packet.AsSpan(4); // strip the 4×0xFF the transport handles

    [Fact]
    public void Insecure_RoundTrips_PasswordAndCommand()
    {
        byte[] pkt = RconProtocol.BuildInsecureRequest(Password, "status");
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, pkt[..4]);
        Assert.True(RconProtocol.TryParse(AfterOob(pkt), out RconRequest req));
        Assert.Equal(RconKind.Insecure, req.Kind);
        Assert.Equal(Password, req.Password);
        Assert.Equal("status", req.Command);
    }

    [Fact]
    public void Time_Packet_HasExactDpLayout_And_Verifies()
    {
        long now = 1_700_000_000;
        byte[] pkt = RconProtocol.BuildTimeRequest(Password, now, "kick 3");

        // DP layout after the 4×0xFF: "srcon HMAC-MD4 TIME " (20) + 16 HMAC bytes + ' ' + "<time> <command>".
        ReadOnlySpan<byte> body = AfterOob(pkt);
        Assert.Equal("srcon HMAC-MD4 TIME ", Encoding.ASCII.GetString(body[..20]));
        Assert.Equal((byte)' ', body[20 + 16]);                                  // the separator space at offset 36
        Assert.Equal($"{now} kick 3", Encoding.ASCII.GetString(body[(20 + 16 + 1)..]));

        Assert.True(RconProtocol.TryParse(body, out RconRequest req));
        Assert.Equal(RconKind.Time, req.Kind);
        Assert.Equal("kick 3", req.Command);
        Assert.Equal($"{now} kick 3", req.HmacMessage);
        Assert.True(RconProtocol.VerifyTime(Password, req, now, maxDiffSeconds: 5));       // in-window, right key
    }

    [Fact]
    public void Time_Rejects_WrongPassword_StaleTime_And_Tamper()
    {
        long now = 1_700_000_000;
        byte[] pkt = RconProtocol.BuildTimeRequest(Password, now, "quit");
        Assert.True(RconProtocol.TryParse(AfterOob(pkt), out RconRequest req));

        Assert.False(RconProtocol.VerifyTime("wrong", req, now, 5));                        // wrong key
        Assert.False(RconProtocol.VerifyTime(Password, req, now + 6, 5));                   // outside the 5s window
        Assert.True(RconProtocol.VerifyTime(Password, req, now + 6, 60));                   // wider window accepts it

        // Tamper the command but keep the old HMAC → must fail (the HMAC covers "<time> <command>").
        byte[] tampered = RconProtocol.BuildTimeRequest(Password, now, "quit");
        int cmdStart = 4 + 20 + 16 + 1 + $"{now} ".Length;
        tampered[cmdStart] = (byte)'X';
        Assert.True(RconProtocol.TryParse(tampered.AsSpan(4), out RconRequest tampReq));
        Assert.False(RconProtocol.VerifyTime(Password, tampReq, now, 5));
    }

    [Fact]
    public void Challenge_Packet_HasExactDpLayout_And_Verifies()
    {
        const string challenge = "Ab3Xy9Kp2Qz";
        byte[] pkt = RconProtocol.BuildChallengeRequest(Password, challenge, "say hi");

        ReadOnlySpan<byte> body = AfterOob(pkt);
        Assert.Equal("srcon HMAC-MD4 CHALLENGE ", Encoding.ASCII.GetString(body[..25]));    // 25-byte prefix
        Assert.Equal((byte)' ', body[25 + 16]);                                             // separator at offset 41
        Assert.Equal($"{challenge} say hi", Encoding.ASCII.GetString(body[(25 + 16 + 1)..]));

        Assert.True(RconProtocol.TryParse(body, out RconRequest req));
        Assert.Equal(RconKind.Challenge, req.Kind);
        Assert.Equal(challenge, req.ChallengeToken);
        Assert.Equal("say hi", req.Command);
        Assert.True(RconProtocol.VerifyChallenge(Password, req));
        Assert.False(RconProtocol.VerifyChallenge("nope", req));
    }

    [Fact]
    public void GetChallenge_And_Response_And_ChallengeReply_Encode()
    {
        Assert.True(RconProtocol.TryParse(AfterOob(RconProtocol.BuildGetChallenge()), out RconRequest req));
        Assert.Equal(RconKind.GetChallenge, req.Kind);

        // Response is \xFF\xFF\xFF\xFFn<text>; challenge reply is \xFF\xFF\xFF\xFFchallenge <token>.
        byte[] resp = RconProtocol.BuildResponse("players: 3\n");
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'n' }, resp[..5]);
        Assert.Equal("players: 3\n", Encoding.UTF8.GetString(resp[5..]));

        byte[] reply = RconProtocol.BuildChallengeReply("Ab3Xy9Kp2Qz");
        Assert.Equal("challenge Ab3Xy9Kp2Qz", Encoding.ASCII.GetString(reply.AsSpan(4)));
    }

    [Theory]
    [InlineData("status", true)]
    [InlineData("set g_gravity 800", true)]
    [InlineData("kick 3; quit", false)]           // ';' chains commands — blocked
    [InlineData("say hi\nquit", false)]           // embedded newline (control char) — blocked
    public void CommandSafetyFilter_MatchesDp(string command, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, RconProtocol.IsCommandSafe(command));
    }

    [Fact]
    public void TryParse_RejectsNonRconPayloads()
    {
        Assert.False(RconProtocol.TryParse(Encoding.ASCII.GetBytes("getinfo xyz"), out _));
        Assert.False(RconProtocol.TryParse(Encoding.ASCII.GetBytes("rcon "), out _));           // no password/command
        Assert.False(RconProtocol.TryParse(Array.Empty<byte>(), out _));
    }
}
