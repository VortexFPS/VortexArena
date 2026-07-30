namespace Conductor.Protocol;

/// <summary>Where a listing stands. Serialized snake_case: pending_challenge, listed, rejected.</summary>
public enum ListingState
{
    /// <summary>Accepted but not visible. The master has sent or will send a UDP challenge; this
    /// becomes <see cref="Listed"/> with no further request from the server.</summary>
    PendingChallenge,

    /// <summary>Visible in GET /api/v1/servers.</summary>
    Listed,

    /// <summary>Will not be listed until the server's configuration changes. A listing ban and a
    /// protocol floor both land here, and the server should stop re-announcing.</summary>
    Rejected,
}

/// <summary>200 response to an announce (spec §3).</summary>
public sealed record AnnounceResponse
{
    public int ProtocolVersion { get; init; } = AnnounceProtocol.Version;

    public required ListingState State { get; init; }

    /// <summary>Stable for the lifetime of a listing and the correlation key in logs on both sides.
    /// Not a secret and not a credential.</summary>
    public required string ServerId { get; init; }

    public int NextAnnounceIn { get; init; } = AnnounceProtocol.AnnounceIntervalSeconds;
    public int Ttl { get; init; } = AnnounceProtocol.TtlSeconds;

    /// <summary>Human-readable. Carries the reason when <see cref="State"/> is
    /// <see cref="ListingState.Rejected"/>.</summary>
    public string? Detail { get; init; }
}

/// <summary>Error body for every non-2xx response (spec §3).</summary>
public sealed record ProtocolError
{
    public required ProtocolErrorBody Error { get; init; }

    public static ProtocolError Of(string code, string message, string? field = null) =>
        new() { Error = new ProtocolErrorBody { Code = code, Message = message, Field = field } };
}

public sealed record ProtocolErrorBody
{
    /// <summary>Machine-readable. See <see cref="ProtocolErrorCodes"/>.</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>The offending request field, when the error is about one.</summary>
    public string? Field { get; init; }

    /// <summary>Set on unsupported_protocol_version, so a client learns what it could speak instead
    /// of guessing by retrying paths.</summary>
    public IReadOnlyList<int>? SupportedVersions { get; init; }
}

public static class ProtocolErrorCodes
{
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";
    public const string InvalidField = "invalid_field";
    public const string MissingField = "missing_field";
    public const string NotPublic = "not_public";
    public const string RateLimited = "rate_limited";
    public const string ListingBanned = "listing_banned";
    public const string ProtocolFloor = "protocol_floor";
    public const string Unavailable = "unavailable";
}
