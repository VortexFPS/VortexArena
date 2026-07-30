namespace Conductor.Protocol;

/// <summary>One server in GET /api/v1/servers (spec §5).
///
/// There is no ping field and there will not be one. The master cannot measure a player's latency to a
/// server; only that player can. Clients keep the direct getinfo probe they already use against
/// dpmaster, and a number measured from the master would be a number measured from the wrong
/// place.</summary>
public sealed record ServerListEntry
{
    public required string ServerId { get; init; }

    /// <summary>As observed by the master from the announce connection, or the verified override.</summary>
    public required string Address { get; init; }
    public required int Port { get; init; }

    public required string Hostname { get; init; }
    public required string Map { get; init; }
    public required string Gametype { get; init; }

    public required int Players { get; init; }
    public required int Bots { get; init; }
    public required int MaxPlayers { get; init; }

    public required string GameVersion { get; init; }
    public required int NetProtocol { get; init; }

    public IReadOnlyList<string>? Mutators { get; init; }
    public IReadOnlyList<string>? Mods { get; init; }
    public bool PasswordProtected { get; init; }

    /// <summary>Master-observed, from GeoIP. A hint for sorting, not a promise.</summary>
    public string? Region { get; init; }

    /// <summary>When the UDP challenge last succeeded for this endpoint.</summary>
    public required DateTimeOffset VerifiedAt { get; init; }

    /// <summary>Under Conductor control. Shown to players on purpose: an officially controlled server
    /// is a different proposition from an unmanaged one, and hiding that would be the wrong
    /// default.</summary>
    public bool Orchestrated { get; init; }
}

/// <summary>Body of GET /api/v1/servers (spec §5).</summary>
public sealed record ServerListResponse
{
    public int ProtocolVersion { get; init; } = AnnounceProtocol.Version;
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Opaque. Null when this is the last page.</summary>
    public string? NextCursor { get; init; }

    public required IReadOnlyList<ServerListEntry> Servers { get; init; }
}

/// <summary>Query parameter names for GET /api/v1/servers. They live here so the game's browser and
/// the master cannot disagree about spelling, which is the kind of bug that looks like an empty server
/// list and reads like a network problem.</summary>
public static class ServerListQuery
{
    public const string Gametype = "gametype";   // repeatable, any-of
    public const string Map = "map";
    public const string NetProtocol = "net_protocol";
    public const string NotFull = "notfull";
    public const string NotEmpty = "notempty";
    public const string NoPassword = "nopassword";
    public const string Region = "region";       // repeatable, any-of
    public const string Limit = "limit";
    public const string Cursor = "cursor";
}
