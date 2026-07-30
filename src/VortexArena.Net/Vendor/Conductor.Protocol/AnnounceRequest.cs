namespace Conductor.Protocol;

/// <summary>Body of POST /api/v1/announce (spec §3). Sent by the game server on first bind, on every
/// map change, and every <see cref="AnnounceProtocol.AnnounceIntervalSeconds"/> otherwise.
///
/// There is no address field. The master takes the IP from the connection source, which is both
/// correct for almost every host and impossible to forge. <see cref="AddressOverride"/> exists for
/// split-horizon and proxied deployments and grants nothing on its own: the UDP challenge is sent to
/// the overridden address, so a wrong or hostile value just fails to verify.</summary>
public sealed record AnnounceRequest
{
    public int ProtocolVersion { get; init; } = AnnounceProtocol.Version;

    public required int Port { get; init; }
    public string? AddressOverride { get; init; }

    public required string Hostname { get; init; }
    public required string Map { get; init; }
    public required string Gametype { get; init; }

    /// <summary>Humans only. Bots are counted separately so that a bot-filled server is not
    /// indistinguishable from a populated one in a `notempty` filter.</summary>
    public required int Players { get; init; }
    public required int Bots { get; init; }
    public required int MaxPlayers { get; init; }

    public required string GameVersion { get; init; }

    /// <summary>Wire-compatibility number. Clients filter on their own value, so a server running an
    /// incompatible build is invisible rather than joinable-and-broken.</summary>
    public required int NetProtocol { get; init; }

    public IReadOnlyList<string>? Mutators { get; init; }
    public IReadOnlyList<string>? Mods { get; init; }

    /// <summary>Must be 1. A server with sv_public 0 does not announce at all, because the announce
    /// itself is the disclosure. Campaign forces sv_public 0 game-side.</summary>
    public required int SvPublic { get; init; }

    public bool PasswordProtected { get; init; }

    /// <summary>The operator set conductor_control 1. Puts an offer in the adoption queue and grants
    /// nothing: control begins only when the runner dials out and proves the key (spec §7).</summary>
    public bool AvailableForControl { get; init; }

    /// <summary>Lowercase hex sha256 of the runner's public key. Required when
    /// <see cref="AvailableForControl"/> is set. Not a secret.</summary>
    public string? ControlKeyFingerprint { get; init; }

    /// <summary>The server's map pool, as the hash from map-catalog-v1 §2. Optional, and additive to a
    /// frozen v1 under announce-v1 §9: a master that does not know this field ignores it, and a server
    /// that does not send it is listed exactly as before.
    ///
    /// Absent means "this server does not report a catalog", which is what `sv_master_catalog 0` sends.
    /// It does not mean the server has no maps, and the master must not render it as an empty pool: an
    /// empty pool has a hash like any other and is reported as one.
    ///
    /// Sent on every announce and cheap because it is the only thing sent. The catalog itself moves
    /// only when the master answers with a <see cref="AnnounceResponse.CatalogRequest"/>.</summary>
    public string? MapCatalogHash { get; init; }
}
