using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

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
}
