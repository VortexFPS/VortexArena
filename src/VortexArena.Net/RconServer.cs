using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VortexArena.Net;

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
    // Replay guard for TIME mode: HMACs seen inside the accept window, pruned by wall time. Keyed on the HMAC
    // ALONE — keying it on address too let the same sniffed packet be replayed from any other source address.
    private readonly Dictionary<string, long> _recentTimeHmacs = new(); // hmacHex → serverUnixTime seen
    // Per-address failed-auth counters for rate limiting.
    private readonly Dictionary<string, (int count, double windowStart)> _failures = new();

    // Rate-limit policy: at most this many failures per address per window before we start dropping silently.
    private const int MaxFailuresPerWindow = 5;
    private const double FailureWindowSeconds = 30.0;

    // HARD CAPS on every attacker-reachable table. All three are keyed by SOURCE ADDRESS, which on UDP is
    // attacker-chosen and unverified: without a cap, a flood of `getchallenge`/junk packets from randomised
    // source addresses grows these dictionaries forever and OOMs the host with no authentication at all.
    // On overflow we sweep expired entries first and only then refuse — an attacker can cost us a bounded
    // amount of memory and can deny only the challenge/limiter SLOTS, never the process.
    private const int MaxTrackedAddresses = 1024;
    private const int MaxRecentHmacs = 4096;

    // GLOBAL failure budget, on top of the per-address one. The per-address limiter is keyed on a spoofable
    // value, so it does nothing against a brute-force that varies the source address every packet — srcon auth
    // needs no round trip, so the attacker never has to receive a reply. This caps total failed attempts per
    // window across ALL addresses, which is the only control that survives source spoofing. Sized so a
    // legitimate operator fumbling a password is unaffected.
    private const int MaxGlobalFailuresPerWindow = 60;
    private int _globalFailures;
    private double _globalWindowStart;

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
            if (string.IsNullOrEmpty(cfg.Password) || IsRateLimited(addr, isLocalhost))
                return Result.Ignored;
            if (!TryIssueChallenge(addr, cfg.ChallengeTimeoutSeconds, out string token))
                return Result.Ignored; // challenge table saturated (flood) — refuse rather than grow
            send(RconProtocol.BuildChallengeReply(token));
            return Result.ChallengeIssued;
        }

        // rcon disabled, or this source is being rate-limited after repeated failures → drop silently.
        if (string.IsNullOrEmpty(cfg.Password))
            return Result.Ignored;
        if (IsRateLimited(addr, isLocalhost))
            return Result.RateLimited;

        // AUTHENTICATION and command-safety are scored separately: an authenticated operator whose command
        // merely contains a ';' must not burn the failed-auth budget (5 of those used to lock them out of
        // their own server for 30 s).
        bool authed = false;
        switch (req.Kind)
        {
            case RconKind.Insecure:
                // DP: only at secure level 0. Stricter than DP: only from localhost (a plaintext password must
                // never cross the network — the agent talks over loopback; remote operators use TIME/CHALLENGE).
                if (cfg.SecureLevel == 0 && isLocalhost)
                    authed = CryptographicOperations.FixedTimeEquals(
                                 Encoding.UTF8.GetBytes(req.Password), Encoding.UTF8.GetBytes(cfg.Password));
                break;

            case RconKind.Time:
                // DP: ignored when secure level > 1. The replay guard is checked BEFORE marking so a duplicate
                // never authenticates, and it is keyed on the HMAC alone (see _recentTimeHmacs).
                if (cfg.SecureLevel <= 1
                    && RconProtocol.VerifyTime(cfg.Password, req, _unixTime(), cfg.MaxTimeDiffSeconds)
                    && !IsTimeReplay(req.Hmac!))
                {
                    MarkTimeHmac(req.Hmac!);
                    authed = true;
                }
                break;

            case RconKind.Challenge:
                // Any secure level: the challenge we issued is the proof. Verify FIRST and consume only on a
                // successful match (DP clears the slot inside the success branch). Consuming unconditionally
                // let anyone who merely names the token burn it, starving rcon_secure 2 indefinitely.
                if (PeekChallenge(addr, req.ChallengeToken) && RconProtocol.VerifyChallenge(cfg.Password, req))
                {
                    _challenges.Remove(addr);
                    authed = true;
                }
                break;
        }

        if (!authed)
        {
            RegisterFailure(addr);
            send(RconProtocol.BuildResponse("Bad rcon command.\n"));
            return Result.Denied;
        }

        // Authenticated but the command is rejected by the injection filter: report it, do NOT count it as a
        // failed authentication.
        if (!RconProtocol.IsCommandSafe(req.Command))
        {
            send(RconProtocol.BuildResponse("Rejected: unsafe rcon command.\n"));
            return Result.Denied;
        }

        ClearFailures(addr); // a good password proves this address is the operator — drop its failure history
        string? output = execute(req.Command);
        send(RconProtocol.BuildResponse(string.IsNullOrEmpty(output) ? "" : output));
        return Result.Executed;
    }

    // ---- challenge lifecycle (mode 2) ----

    private bool TryIssueChallenge(string addr, double timeoutSeconds, out string token)
    {
        token = "";
        double now = _monotonic();
        if (_challenges.Count >= MaxTrackedAddresses && !_challenges.ContainsKey(addr))
        {
            SweepExpiredChallenges(now);
            if (_challenges.Count >= MaxTrackedAddresses)
                return false; // still saturated — refuse instead of growing without bound
        }
        token = NewChallengeToken();
        _challenges[addr] = (token, now + Math.Max(1.0, timeoutSeconds));
        return true;
    }

    /// <summary>Is this address's live challenge equal to <paramref name="token"/>? Does NOT consume it — the
    /// caller removes the slot only after the HMAC also verifies.</summary>
    private bool PeekChallenge(string addr, string token)
    {
        if (string.IsNullOrEmpty(token) || !_challenges.TryGetValue(addr, out var c))
            return false;
        if (c.expiry < _monotonic())
        {
            _challenges.Remove(addr); // expired — reclaim the slot
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(c.token), Encoding.UTF8.GetBytes(token));
    }

    private void SweepExpiredChallenges(double now)
    {
        if (_challenges.Count == 0) return;
        var stale = new List<string>();
        foreach (var kv in _challenges)
            if (kv.Value.expiry < now) stale.Add(kv.Key);
        foreach (string k in stale) _challenges.Remove(k);
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

    // Keyed on the HMAC alone: the digest already covers the time + command, and a sniffed srcon packet
    // (the command travels in cleartext — only the MAC is opaque) must not become replayable simply by
    // sending it from a different source address.
    private bool IsTimeReplay(byte[] hmac)
    {
        PruneRecentHmacs();
        return _recentTimeHmacs.ContainsKey(Convert.ToHexString(hmac));
    }

    private void MarkTimeHmac(byte[] hmac)
    {
        if (_recentTimeHmacs.Count >= MaxRecentHmacs)
        {
            PruneRecentHmacs();
            if (_recentTimeHmacs.Count >= MaxRecentHmacs)
                _recentTimeHmacs.Clear(); // saturated by a flood: better to forget than to grow unbounded
        }
        _recentTimeHmacs[Convert.ToHexString(hmac)] = _unixTime();
    }

    private void PruneRecentHmacs()
    {
        // TWO maxdiff windows: a packet stays acceptable until clientTime + maxdiff, and clientTime may
        // legitimately be up to maxdiff ahead of when we saw it. Pruning at one window dropped the record
        // while the packet was still replayable.
        long cutoff = _unixTime() - 2 * Math.Max(1, _config().MaxTimeDiffSeconds);
        if (_recentTimeHmacs.Count == 0) return;
        var stale = new List<string>();
        foreach (var kv in _recentTimeHmacs)
            if (kv.Value < cutoff) stale.Add(kv.Key);
        foreach (var k in stale) _recentTimeHmacs.Remove(k);
    }

    // ---- rate limiting (per-address + global) ----

    /// <summary>
    /// Loopback is never locked OUT (its failures still count toward the global budget). The per-address
    /// counter is keyed on a spoofable UDP source, so without this an attacker sending 5 junk packets per
    /// window with the operator's address spoofed — or simply sharing their NAT egress IP — could deny the
    /// operator rcon indefinitely. The local operator/launcher agent talks over loopback, so that path stays
    /// available; a local attacker already has far stronger levers than rcon.
    /// </summary>
    private bool IsRateLimited(string addr, bool isLocalhost)
    {
        double now = _monotonic();
        if (now - _globalWindowStart > FailureWindowSeconds)
        {
            _globalWindowStart = now;
            _globalFailures = 0;
        }
        if (_globalFailures >= MaxGlobalFailuresPerWindow)
            return true;

        if (isLocalhost)
            return false;
        if (!_failures.TryGetValue(addr, out var f))
            return false;
        if (now - f.windowStart > FailureWindowSeconds)
        {
            _failures.Remove(addr); // window elapsed — reset
            return false;
        }
        return f.count >= MaxFailuresPerWindow;
    }

    private void RegisterFailure(string addr)
    {
        double now = _monotonic();
        if (now - _globalWindowStart > FailureWindowSeconds)
        {
            _globalWindowStart = now;
            _globalFailures = 0;
        }
        _globalFailures++;

        if (_failures.TryGetValue(addr, out var f) && now - f.windowStart <= FailureWindowSeconds)
        {
            _failures[addr] = (f.count + 1, f.windowStart);
            return;
        }
        if (_failures.Count >= MaxTrackedAddresses && !_failures.ContainsKey(addr))
        {
            SweepExpiredFailures(now);
            if (_failures.Count >= MaxTrackedAddresses)
                return; // saturated: the global budget above is what throttles this case
        }
        _failures[addr] = (1, now);
    }

    private void ClearFailures(string addr) => _failures.Remove(addr);

    private void SweepExpiredFailures(double now)
    {
        var stale = new List<string>();
        foreach (var kv in _failures)
            if (now - kv.Value.windowStart > FailureWindowSeconds) stale.Add(kv.Key);
        foreach (string k in stale) _failures.Remove(k);
    }
}
