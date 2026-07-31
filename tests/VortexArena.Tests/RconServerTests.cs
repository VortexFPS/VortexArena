using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// End-to-end auth tests for <see cref="RconServer"/> (DS-6): the challenge lifecycle, the TIME replay window,
/// DP secure-level gating, the localhost-only rule for plaintext rcon, and per-address rate limiting. The clocks
/// are injected so every case is deterministic. The "client" side reuses <see cref="RconProtocol"/>'s builders,
/// so these also prove our client tooling round-trips against our own server.
/// </summary>
public class RconServerTests
{
    private const string Pw = "letmein";
    private static readonly IPEndPoint Local = new(IPAddress.Loopback, 40000);
    private static readonly IPEndPoint Remote = new(IPAddress.Parse("203.0.113.7"), 40000);

    private sealed class Harness
    {
        public long UnixTime = 1_700_000_000;
        public double Mono = 100.0;
        public RconConfig Config = new() { Password = Pw, SecureLevel = 1, MaxTimeDiffSeconds = 5, ChallengeTimeoutSeconds = 5 };
        public readonly List<string> Executed = new();
        public readonly List<byte[]> Sent = new();
        public readonly RconServer Server;

        public Harness()
        {
            Server = new RconServer(() => Config, () => UnixTime, () => Mono);
        }

        public RconServer.Result Handle(IPEndPoint from, byte[] packetWithOob)
            => Server.Handle(from, packetWithOob.AsSpan(4), from.Address.Equals(IPAddress.Loopback),
                cmd => { Executed.Add(cmd); return $"ran:{cmd}"; },
                pkt => Sent.Add(pkt));

        public string LastSentText() => Sent.Count == 0 ? "" : Encoding.UTF8.GetString(Sent[^1].AsSpan(5)); // skip \xFF\xFF\xFF\xFFn
    }

    [Fact]
    public void Time_Valid_Executes()
    {
        var h = new Harness();
        var pkt = RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status");
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, pkt));
        Assert.Equal(new[] { "status" }, h.Executed);
        Assert.Equal("ran:status", h.LastSentText());
    }

    [Fact]
    public void Time_Replay_IsRejected()
    {
        var h = new Harness();
        var pkt = RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status");
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, pkt));
        // Exact same packet again (same HMAC, same time) → replay guard denies it.
        Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, pkt));
        Assert.Single(h.Executed);
    }

    [Fact]
    public void Time_WrongPassword_Denied()
    {
        var h = new Harness();
        var pkt = RconProtocol.BuildTimeRequest("nope", h.UnixTime, "status");
        Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, pkt));
        Assert.Empty(h.Executed);
    }

    [Fact]
    public void Time_Ignored_AtSecureLevel2()
    {
        var h = new Harness { Config = new RconConfig { Password = Pw, SecureLevel = 2, MaxTimeDiffSeconds = 5, ChallengeTimeoutSeconds = 5 } };
        var pkt = RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status");
        Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, pkt)); // parsed but not accepted at level 2
        Assert.Empty(h.Executed);
    }

    [Fact]
    public void Challenge_Lifecycle_IssuesConsumesAndIsSingleUse()
    {
        var h = new Harness { Config = new RconConfig { Password = Pw, SecureLevel = 2, MaxTimeDiffSeconds = 5, ChallengeTimeoutSeconds = 5 } };

        // 1. getchallenge → server issues a token in a "challenge <token>" reply.
        Assert.Equal(RconServer.Result.ChallengeIssued, h.Handle(Remote, RconProtocol.BuildGetChallenge()));
        string reply = Encoding.ASCII.GetString(h.Sent[^1].AsSpan(4)); // "challenge <token>"
        Assert.StartsWith("challenge ", reply);
        string token = reply["challenge ".Length..];

        // 2. srcon CHALLENGE with the issued token + right password → executes.
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, RconProtocol.BuildChallengeRequest(Pw, token, "endmatch")));
        Assert.Equal(new[] { "endmatch" }, h.Executed);

        // 3. Reusing the same challenge is rejected (single-use).
        Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, RconProtocol.BuildChallengeRequest(Pw, token, "endmatch")));
        Assert.Single(h.Executed);
    }

    [Fact]
    public void Challenge_Expired_IsRejected()
    {
        var h = new Harness { Config = new RconConfig { Password = Pw, SecureLevel = 2, MaxTimeDiffSeconds = 5, ChallengeTimeoutSeconds = 5 } };
        h.Handle(Remote, RconProtocol.BuildGetChallenge());
        string token = Encoding.ASCII.GetString(h.Sent[^1].AsSpan(4))["challenge ".Length..];
        h.Mono += 10.0; // past the 5s challenge timeout
        Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, RconProtocol.BuildChallengeRequest(Pw, token, "quit")));
        Assert.Empty(h.Executed);
    }

    [Fact]
    public void Insecure_OnlyLocalhost_AndOnlySecure0()
    {
        var h = new Harness { Config = new RconConfig { Password = Pw, SecureLevel = 0, MaxTimeDiffSeconds = 5, ChallengeTimeoutSeconds = 5 } };

        // localhost + secure 0 → allowed.
        Assert.Equal(RconServer.Result.Executed, h.Handle(Local, RconProtocol.BuildInsecureRequest(Pw, "status")));
        // remote + secure 0 → denied (plaintext never crosses the network, stricter than DP).
        Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, RconProtocol.BuildInsecureRequest(Pw, "status")));

        // secure 1 → plaintext ignored even from localhost.
        h.Config = new RconConfig { Password = Pw, SecureLevel = 1, MaxTimeDiffSeconds = 5, ChallengeTimeoutSeconds = 5 };
        Assert.Equal(RconServer.Result.Denied, h.Handle(Local, RconProtocol.BuildInsecureRequest(Pw, "status")));
        Assert.Single(h.Executed); // only the first (localhost, secure 0) ran
    }

    [Fact]
    public void Disabled_WhenNoPassword()
    {
        var h = new Harness { Config = new RconConfig { Password = "", SecureLevel = 1, MaxTimeDiffSeconds = 5, ChallengeTimeoutSeconds = 5 } };
        Assert.Equal(RconServer.Result.Ignored, h.Handle(Remote, RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status")));
        Assert.Equal(RconServer.Result.Ignored, h.Handle(Remote, RconProtocol.BuildGetChallenge()));
        Assert.Empty(h.Executed);
    }

    [Fact]
    public void RateLimited_AfterRepeatedFailures()
    {
        var h = new Harness();
        // 5 bad attempts (wrong password) exhaust the window budget…
        for (int i = 0; i < 5; i++)
            Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, RconProtocol.BuildTimeRequest("bad", h.UnixTime, "status")));
        // …the 6th is dropped as rate-limited, and even a VALID packet is now refused until the window passes.
        Assert.Equal(RconServer.Result.RateLimited, h.Handle(Remote, RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status")));
        Assert.Empty(h.Executed);

        h.Mono += 31.0; // window elapses → a valid packet works again
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status")));
    }

    // =============================================================================================
    //  Security regressions (2026-07-27 review). rcon is a remote code-execution surface; each of
    //  these was a live hole.
    // =============================================================================================

    [Fact]
    public void Time_Replay_FromADifferentAddress_IsAlsoRejected()
    {
        // The replay guard used to be keyed on address+HMAC. The command travels in CLEARTEXT (only the MAC is
        // opaque), so a sniffed srcon packet replayed from any other source address re-executed for the rest of
        // the maxdiff window.
        var h = new Harness();
        var pkt = RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status");
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, pkt));

        var elsewhere = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 40000);
        Assert.Equal(RconServer.Result.Denied, h.Handle(elsewhere, pkt));
        Assert.Equal(new[] { "status" }, h.Executed); // executed exactly once
    }

    [Fact]
    public void Challenge_IsNotConsumedByAWrongHmac()
    {
        // DP clears the challenge slot only inside the success branch. Consuming it unconditionally let anyone
        // who merely NAMED the token burn it, starving rcon_secure 2 for the real operator indefinitely.
        var h = new Harness();
        h.Config = h.Config with { SecureLevel = 2 };
        Assert.Equal(RconServer.Result.ChallengeIssued, h.Handle(Remote, RconProtocol.BuildGetChallenge()));
        string challenge = Encoding.UTF8.GetString(h.Sent[^1].AsSpan(4))["challenge ".Length..];

        // An attacker echoes the right token with the WRONG password → denied, but the challenge must survive.
        Assert.Equal(RconServer.Result.Denied,
            h.Handle(Remote, RconProtocol.BuildChallengeRequest("wrong-password", challenge, "status")));
        // The legitimate operator's request with the SAME challenge still works.
        Assert.Equal(RconServer.Result.Executed,
            h.Handle(Remote, RconProtocol.BuildChallengeRequest(Pw, challenge, "status")));
        Assert.Equal(new[] { "status" }, h.Executed);
    }

    [Fact]
    public void SuccessfulAuth_ClearsTheFailureBudget()
    {
        // Otherwise the counter only ever decayed on the 30 s window, so an attacker trickling 5 junk packets
        // per window with the operator's address spoofed could keep them locked out forever.
        var h = new Harness();
        for (int i = 0; i < 4; i++)
            Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, RconProtocol.BuildTimeRequest("bad", h.UnixTime, "status")));

        h.UnixTime += 1; // fresh HMAC (not a replay)
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status")));

        // Budget reset: five more bad attempts are needed to trip the limiter again, so the 5th here is still
        // a plain Denied rather than RateLimited.
        for (int i = 0; i < 5; i++)
        {
            h.UnixTime += 1;
            Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, RconProtocol.BuildTimeRequest("bad", h.UnixTime, "status")));
        }
    }

    [Fact]
    public void NonAsciiPassword_Authenticates()
    {
        // Encoding.ASCII folds every byte >= 0x80 to '?', so "Sécurité!" was HMAC'd as the key "S?curit?!" —
        // a silent key-space collapse where any password folding to the same string also authenticated.
        var h = new Harness();
        const string pw = "Sécurité-Ω-2026";
        h.Config = h.Config with { Password = pw };
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, RconProtocol.BuildTimeRequest(pw, h.UnixTime, "status")));

        // And a DIFFERENT password that used to fold to the same ASCII key must still be rejected.
        h.UnixTime += 1;
        Assert.Equal(RconServer.Result.Denied, h.Handle(Remote, RconProtocol.BuildTimeRequest("S?curit?-?-2026", h.UnixTime, "status")));
    }

    [Fact]
    public void UnsafeCommand_IsRejected_ButDoesNotBurnTheAuthBudget()
    {
        // An authenticated operator whose command contains ';' used to be scored as a failed AUTH; five of
        // those locked them out of their own server.
        var h = new Harness();
        for (int i = 0; i < 5; i++)
        {
            h.UnixTime += 1;
            Assert.Equal(RconServer.Result.Denied,
                h.Handle(Remote, RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status; quit")));
        }
        Assert.Empty(h.Executed);

        h.UnixTime += 1; // still not rate-limited: a clean command goes through
        Assert.Equal(RconServer.Result.Executed, h.Handle(Remote, RconProtocol.BuildTimeRequest(Pw, h.UnixTime, "status")));
    }

    [Fact]
    public void ChallengeTable_DoesNotGrowWithoutBound()
    {
        // getchallenge is unauthenticated, and the source address on UDP is attacker-chosen — an unswept,
        // uncapped table is a remote memory-exhaustion lever needing no credentials at all.
        var h = new Harness();
        int issued = 0;
        for (int i = 0; i < 5000; i++)
        {
            var spoofed = new IPEndPoint(IPAddress.Parse($"10.{(i >> 16) & 0xFF}.{(i >> 8) & 0xFF}.{i & 0xFF}"), 40000);
            if (h.Handle(spoofed, RconProtocol.BuildGetChallenge()) == RconServer.Result.ChallengeIssued)
                issued++;
        }
        // Bounded well below the number of distinct addresses that asked.
        Assert.True(issued < 2000, $"challenge table grew unbounded: {issued} slots handed out");
    }

    [Fact]
    public void Response_IsTruncatedToOneSafeDatagram()
    {
        // An unbounded single UDP send threw past ~65507 bytes, and that throw escaped the master-server pump
        // into _Process — one verbose authenticated command took the server's frame loop down.
        byte[] pkt = RconProtocol.BuildResponse(new string('x', 200_000));
        Assert.True(pkt.Length < 2000, $"reply datagram not bounded: {pkt.Length} bytes");

        // The chunked form keeps everything, in order, each piece inside the same bound.
        var chunks = RconProtocol.BuildResponseChunks(new string('y', 5000));
        Assert.True(chunks.Count > 1);
        foreach (byte[] c in chunks)
            Assert.True(c.Length < 2000);
    }
}
