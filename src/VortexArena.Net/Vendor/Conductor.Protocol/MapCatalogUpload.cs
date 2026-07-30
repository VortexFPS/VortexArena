namespace Conductor.Protocol;

/// <summary>Why the master is asking for a catalog. Serialized snake_case: unknown_hash,
/// resync.</summary>
public enum CatalogRequestReason
{
    /// <summary>Never seen this pool, from this server or any other.</summary>
    UnknownHash,

    /// <summary>The master lost or is rebuilding its copy. The server's pool did not change.</summary>
    Resync,
}

/// <summary>Carried on an <see cref="AnnounceResponse"/> only when the master wants an upload (map
/// catalog §3). Its absence is the steady state and the whole point of the design: a server whose pool
/// the master already knows sends nothing and simply keeps announcing.</summary>
public sealed record CatalogRequest
{
    public required CatalogRequestReason Reason { get; init; }

    /// <summary>Bearer credential for both upload phases. Single use and bound to one server_id, so it
    /// is worth nothing to anyone else (§8).</summary>
    public required string UploadToken { get; init; }

    public int ExpiresIn { get; init; } = MapCatalogProtocol.UploadTokenLifetimeSeconds;
}

/// <summary>Body of POST /api/v1/catalog/index, upload phase 1 (§4).
///
/// Sent with `Authorization: Bearer &lt;upload_token&gt;`. This is the cheap half: hashes, names and
/// sizes only, a few tens of kilobytes for a large pool, and on a mature master the exchange usually
/// ends here.</summary>
public sealed record CatalogIndexRequest
{
    /// <summary>Must equal the hash of <see cref="Entries"/> under map catalog §2, and must equal the
    /// hash the server announced. The master checks both. Without the second check a server could
    /// announce one pool and describe another, and the announced hash would stop being a claim the
    /// server is bound to.</summary>
    public required string CatalogHash { get; init; }

    /// <summary>May be empty: a server that reports a catalog and carries no maps is describing an
    /// empty pool, which is a fact about it, not a malformed request.</summary>
    public required IReadOnlyList<CatalogIndexEntry> Entries { get; init; }
}

/// <summary>One package in a phase-1 index (§4).</summary>
public sealed record CatalogIndexEntry
{
    /// <summary>sha256 of the whole .pk3. Identity, globally: the content store and the runner's
    /// fetch-by-hash already key on it, so the catalog introduces no new notion of identity (§2).</summary>
    public required string PackageSha256 { get; init; }

    /// <summary>The file name this server carries the package under, e.g. `stormkeep.pk3`. Advisory
    /// and per-server: identity is the hash, and two servers may hold the same bytes under different
    /// names.</summary>
    public required string Name { get; init; }

    public required long SizeBytes { get; init; }
}

/// <summary>200 response to phase 1 (§4). Names only what the master has never seen from anyone, so
/// the second phase carries the metadata for globally new maps and nothing else.</summary>
public sealed record CatalogIndexResponse
{
    /// <summary>Package hashes to describe in phase 2. Empty is the common case and means the upload
    /// is finished. Serialized as `[]` rather than omitted, because "nothing to send" is the answer,
    /// not a missing answer.</summary>
    public required IReadOnlyList<string> UnknownPackages { get; init; }
}

/// <summary>Body of POST /api/v1/catalog/packages, upload phase 2 (§4). Same token, and only for
/// hashes the master named in <see cref="CatalogIndexResponse.UnknownPackages"/>.</summary>
public sealed record CatalogPackagesRequest
{
    /// <summary>At most <see cref="MapCatalogProtocol.MaxPackagesPerRequest"/> per request; a server
    /// with more new maps than that sends several.</summary>
    public required IReadOnlyList<CatalogPackage> Packages { get; init; }
}

/// <summary>One package's metadata, as reported by a game server (§4).
///
/// Everything except the hash is optional. A .pk3 whose mapinfo is missing or sparse is still a real
/// map that players can download, and dropping it from the catalog because its author left a field
/// blank would be the wrong trade. The master stores the first description of a given hash and records
/// later disagreements as conflicts rather than applying them (§6).</summary>
public sealed record CatalogPackage
{
    public required string PackageSha256 { get; init; }

    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }

    /// <summary>Short codes, as in an announce: `ctf`, `dm`, ...</summary>
    public IReadOnlyList<string>? Gametypes { get; init; }

    /// <summary>Whatever else mapinfo provides, verbatim. Keys are data, not schema, so they are not
    /// snake_cased on the way out the way property names are.</summary>
    public IReadOnlyDictionary<string, string>? Mapinfo { get; init; }

    /// <summary>Where the package can be downloaded. Supplied by whoever runs the game server, checked
    /// but never followed by the master, and advisory rather than authoritative: the client verifies
    /// the package hash after downloading, so a hostile URL can serve whatever it likes and the client
    /// discards it (§5).</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>Absent is normal. A server with no thumbnail for a map sends the entry without one and
    /// the browser shows a placeholder (§10).</summary>
    public CatalogThumbnail? Thumbnail { get; init; }
}

/// <summary>An uploaded thumbnail (§4). Never served as given: the master decodes it and re-encodes to
/// WebP, stripping every metadata chunk, because an uploaded image reaching a player's client is a
/// decoder exposed to a stranger's bytes (§7).</summary>
public sealed record CatalogThumbnail
{
    /// <summary>One of <see cref="MapCatalogProtocol.AllowedThumbnailFormats"/>, and checked against
    /// the data's magic bytes.
    ///
    /// A string rather than an enum on purpose: this is a request field, and an unknown enum value
    /// throws inside the deserializer, which turns an operator's typo into a 500 and a stack trace
    /// instead of a 400 that names the field.</summary>
    public required string Format { get; init; }

    /// <summary>Declared dimensions, capped before anything decodes the image.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Standard base64 with padding, as JSON strings need no URL-safe alphabet. Decodes to at
    /// most <see cref="MapCatalogProtocol.MaxThumbnailBytes"/>.</summary>
    public required string DataBase64 { get; init; }
}
