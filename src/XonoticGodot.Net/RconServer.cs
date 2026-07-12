using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace XonoticGodot.Net;

/// <summary>Live rcon config, fetched per request so a console/server.cfg change to any of these takes effect
/// immediately (DP reads the cvars each packet). <see cref="Password"/> empty ⇒ rcon is OFF.</summary>
public readonly struct RconConfig
{
    public string Password { get; init; }
    /// <summary>0 = plaintext (localhost-only here), 1 = time+HMAC, 2 = challenge+HMAC. DP <c>rcon_secure</c>.</summary>
    public int SecureLevel { get; init; }
    /// <summary>DP <c>rcon_secure_maxdiff</c> — max |serverTime − packetTime| seconds for a TIME request.</summary>
    public int MaxTimeDiffSeconds { get; init; }
    /// <summary>DP <c>rcon_secure_challengetimeout</c> — seconds an issued challenge stays valid.</summary>
    public double ChallengeTimeoutSeconds { get; init; }
}

/// <summary>
/// The stateful server side of DarkPlaces rcon (DS-6): authenticates <c>rcon</c>/<c>srcon</c> packets parsed by
/// <see cref="RconProtocol"/>, manages the mode-2 challenge lifecycle + mode-1 replay window, rate-limits failed
/// attempts per address, and runs the authenticated command through an injected executor — returning the output
/// as a DP rcon-print reply. Pure of Godot/engine types (BCL only) so it unit-tests headlessly; the time source
/// and RNG are injectable for deterministic tests. DP secure-level gating (netconn.c): Insecure only at level 0
/// (and, stricter than DP, only from localhost), TIME only at level ≤ 1, CHALLENGE at any level (the issued
/// challenge is the proof). All password/HMAC compares are constant-time (in <see cref="RconProtocol"/>).
/// </summary>
public sealed class RconServer
{
    private readonly Func<RconConfig> _config;
    private readonly Func<long> _unixTime;       // seconds; injectable for tests
    private readonly Func<double> _monotonic;    // seconds, for challenge/rate-limit expiry; injectable

    // One active challenge per source address (DP keeps a global ring; per-address is simpler and sufficient).
    private readonly Dictionary<string, (string token, double expiry)> _challenges = new();
    // Replay guard for TIME mode: HMACs seen inside the accept window, pruned by wall time.
    private readonly Dictionary<string, long> _recentTimeHmacs = new(); // hmacHex → serverUnixTime seen
    // Per-address failed-auth counters for rate limiting.
    private readonly Dictionary<string, (int count, double windowStart)> _failures = new();

    // Rate-limit policy: at most this many failures per address per window before we start dropping silently.
    private const int MaxFailuresPerWindow = 5;
    private const double FailureWindowSeconds = 30.0;

    public RconServer(Func<RconConfig> config, Func<long>? unixTime = null, Func<double>? monotonic = null)
    {
        _config = config;
        _unixTime = unixTime ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _monotonic = monotonic ?? (() => Environment.TickCount64 / 1000.0);
    }

    /// <summary>The outcome of handling one packet — surfaced for logging/tests.</summary>
    public enum Result { Ignored, ChallengeIssued, Denied, RateLimited, Executed }

    /// <summary>
    /// Handle one connectionless rcon payload (header already stripped). <paramref name="isLocalhost"/> gates
    /// insecure plaintext rcon. <paramref name="execute"/> runs an authenticated command and returns its console
    /// output (null/empty ⇒ no reply body). <paramref name="send"/> ships a reply packet back to
    /// <paramref name="from"/>. Returns what happened. Never throws on malformed input.
    /// </summary>
    public Result Handle(IPEndPoint from, ReadOnlySpan<byte> payload, bool isLocalhost,
        Func<string, string?> execute, Action<byte[]> send)
    {
        if (!RconProtocol.TryParse(payload, out RconRequest req))
            return Result.Ignored;

        string addr = from.Address.ToString();
        RconConfig cfg = _config();

        // getchallenge is unauthenticated by design (it hands out a nonce); still rate-limit it so it can't be a
        // free amplification/DoS lever, and only answer when rcon is actually enabled.
        if (req.Kind == RconKind.GetChallenge)
        {
            if (string.IsNullOrEmpty(cfg.Password) || IsRateLimited(addr))
                return Result.Ignored;
            string token = IssueChallenge(addr, cfg.ChallengeTimeoutSeconds);
            send(RconProtocol.BuildChallengeReply(token));
            return Result.ChallengeIssued;
        }

        // rcon disabled, or this source is being rate-limited after repeated failures → drop silently.
        if (string.IsNullOrEmpty(cfg.Password))
            return Result.Ignored;
        if (IsRateLimited(addr))
            return Result.RateLimited;

        bool authed = false;
        switch (req.Kind)
        {
            case RconKind.Insecure:
                // DP: only at secure level 0. Stricter than DP: only from localhost (a plaintext password must
                // never cross the network — the agent talks over loopback; remote operators use TIME/CHALLENGE).
                if (cfg.SecureLevel == 0 && isLocalhost)
                    authed = RconProtocol.IsCommandSafe(req.Command)
                             && CryptographicOperations.FixedTimeEquals(
                                    Encoding.UTF8.GetBytes(req.Password), Encoding.UTF8.GetBytes(cfg.Password));
                break;

            case RconKind.Time:
                // DP: ignored when secure level > 1.
                if (cfg.SecureLevel <= 1
                    && RconProtocol.VerifyTime(cfg.Password, req, _unixTime(), cfg.MaxTimeDiffSeconds)
                    && !IsTimeReplay(addr, req.Hmac!))
                {
                    MarkTimeHmac(addr, req.Hmac!);
                    authed = RconProtocol.IsCommandSafe(req.Command);
                }
                break;

            case RconKind.Challenge:
                // Any secure level: the challenge we issued is the proof. Consume it (single-use → replay-proof).
                if (TryConsumeChallenge(addr, req.ChallengeToken)
                    && RconProtocol.VerifyChallenge(cfg.Password, req))
                    authed = RconProtocol.IsCommandSafe(req.Command);
                break;
        }

        if (!authed)
        {
            RegisterFailure(addr);
            send(RconProtocol.BuildResponse("Bad rcon command.\n"));
            return Result.Denied;
        }

        string? output = execute(req.Command);
        send(RconProtocol.BuildResponse(string.IsNullOrEmpty(output) ? "" : output));
        return Result.Executed;
    }

    // ---- challenge lifecycle (mode 2) ----

    private string IssueChallenge(string addr, double timeoutSeconds)
    {
        string token = NewChallengeToken();
        _challenges[addr] = (token, _monotonic() + Math.Max(1.0, timeoutSeconds));
        return token;
    }

    private bool TryConsumeChallenge(string addr, string token)
    {
        if (string.IsNullOrEmpty(token) || !_challenges.TryGetValue(addr, out var c))
            return false;
        _challenges.Remove(addr); // single-use regardless of outcome (a wrong guess burns the challenge, like DP)
        return c.expiry >= _monotonic()
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.token), Encoding.ASCII.GetBytes(token));
    }

    // 11-char base62 token — opaque to the client (it just echoes it), sized like DP's challenge string.
    private static string NewChallengeToken()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> rnd = stackalloc byte[11];
        RandomNumberGenerator.Fill(rnd);
        Span<char> outp = stackalloc char[11];
        for (int i = 0; i < 11; i++)
            outp[i] = alphabet[rnd[i] % alphabet.Length];
        return new string(outp);
    }

    // ---- TIME replay window (mode 1) ----

    private bool IsTimeReplay(string addr, byte[] hmac)
    {
        PruneRecentHmacs();
        return _recentTimeHmacs.ContainsKey(addr + "|" + Convert.ToHexString(hmac));
    }

    private void MarkTimeHmac(string addr, byte[] hmac)
        => _recentTimeHmacs[addr + "|" + Convert.ToHexString(hmac)] = _unixTime();

    private void PruneRecentHmacs()
    {
        long cutoff = _unixTime() - Math.Max(1, _config().MaxTimeDiffSeconds);
        if (_recentTimeHmacs.Count == 0) return;
        var stale = new List<string>();
        foreach (var kv in _recentTimeHmacs)
            if (kv.Value < cutoff) stale.Add(kv.Key);
        foreach (var k in stale) _recentTimeHmacs.Remove(k);
    }

    // ---- per-address rate limiting ----

    private bool IsRateLimited(string addr)
    {
        if (!_failures.TryGetValue(addr, out var f))
            return false;
        if (_monotonic() - f.windowStart > FailureWindowSeconds)
        {
            _failures.Remove(addr); // window elapsed — reset
            return false;
        }
        return f.count >= MaxFailuresPerWindow;
    }

    private void RegisterFailure(string addr)
    {
        double now = _monotonic();
        if (_failures.TryGetValue(addr, out var f) && now - f.windowStart <= FailureWindowSeconds)
            _failures[addr] = (f.count + 1, f.windowStart);
        else
            _failures[addr] = (1, now);
    }
}
