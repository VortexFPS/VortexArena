namespace Conductor.Protocol;

/// <summary>Body of GET /api/v1/catalog/{package_sha256} (§9).
///
/// Public, unauthenticated and cacheable forever: the response is keyed by a content hash, so it can
/// never change meaning. That is also why it carries no server: metadata belongs to the package, and
/// four hundred servers running the same forty community maps share this one record between them
/// (§1).</summary>
public sealed record CatalogPackageResponse
{
    public int ProtocolVersion { get; init; } = AnnounceProtocol.Version;

    public required string PackageSha256 { get; init; }

    /// <summary>A function of the content, so it is a property of the package rather than of whoever
    /// reported it. The file name is not, and lives on <see cref="ServerCatalogEntry.Name"/>.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Null until some server has described the package. A package can be known (some server
    /// listed its hash in an index) before it is described (§4).</summary>
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }

    public IReadOnlyList<string>? Gametypes { get; init; }
    public IReadOnlyDictionary<string, string>? Mapinfo { get; init; }

    /// <summary>As supplied by the first server to describe this package, stored verbatim and never
    /// followed by the master (§5). Clients must verify the package hash of whatever they
    /// download.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>Master-served WebP, re-encoded from the upload and stored by the hash of the
    /// re-encoded output (§7). Null when nobody has supplied a thumbnail, which the browser shows as a
    /// placeholder rather than as a broken entry (§10).</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>When the master accepted the description that this record holds. Useful next to the
    /// first-write-wins rule in §6: it says which report won.</summary>
    public DateTimeOffset? DescribedAt { get; init; }
}

/// <summary>One package in a server's pool (§9).</summary>
public sealed record ServerCatalogEntry
{
    public required string PackageSha256 { get; init; }

    /// <summary>What this server calls the file. Per-server by nature, so it is here and not on the
    /// global package record.</summary>
    public required string Name { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Enough to render a list without one request per row. The full record is at
    /// <see cref="MapCatalogProtocol.PackagePath"/>, which is cacheable forever.</summary>
    public string? Title { get; init; }
    public string? ThumbnailUrl { get; init; }
}

/// <summary>Body of GET /api/v1/servers/{server_id}/catalog (§9), for the "what does this server run"
/// panel in the browser.</summary>
public sealed record ServerCatalogResponse
{
    public int ProtocolVersion { get; init; } = AnnounceProtocol.Version;

    public required string ServerId { get; init; }

    /// <summary>The pool this list describes. A client that already holds this hash can skip the
    /// response, and a client that sees it differ from the server's current
    /// <see cref="ServerListEntry.MapCatalogHash"/> is looking at a list the master has not caught up
    /// with yet.</summary>
    public required string CatalogHash { get; init; }

    /// <summary>When this pool was last uploaded. Not when it was last announced: a pool that has not
    /// changed is never re-uploaded, and reporting an announce time here would suggest otherwise.</summary>
    public required DateTimeOffset ReportedAt { get; init; }

    public required IReadOnlyList<ServerCatalogEntry> Packages { get; init; }
}
