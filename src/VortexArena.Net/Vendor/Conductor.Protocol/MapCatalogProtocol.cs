namespace Conductor.Protocol;

/// <summary>Constants from protocol/map-catalog-v1.md. Same rule as <see cref="AnnounceProtocol"/>:
/// every number the game server, the master and the browser must agree on lives here exactly once, so
/// three implementations cannot drift by editing their own copy of a cap.
///
/// This is additive to a frozen announce v1 and deliberately carries no version of its own. The
/// catalog rides on announce v1 (announce-v1 §9), and a second version number would be a second thing
/// to get wrong.</summary>
public static class MapCatalogProtocol
{
    /// <summary>Upload phase 1: the index (§4).</summary>
    public const string IndexPath = "/api/v1/catalog/index";

    /// <summary>Upload phase 2: the details (§4).</summary>
    public const string PackagesPath = "/api/v1/catalog/packages";

    /// <summary>Prefix of the public per-package lookup (§9).</summary>
    public const string CatalogPath = "/api/v1/catalog";

    /// <summary>Public, unauthenticated, cacheable forever: the response is keyed by a content hash,
    /// so it can never change meaning (§9).
    ///
    /// This shares a prefix with <see cref="IndexPath"/> and <see cref="PackagesPath"/> and does not
    /// collide with them: those two are POST, this is GET, and neither literal is 64 hex characters.
    /// A router that matches this segment loosely should still constrain it to a package hash.</summary>
    public static string PackagePath(string packageSha256) => $"{CatalogPath}/{packageSha256}";

    /// <summary>What one server runs (§9).</summary>
    public static string ServerCatalogPath(string serverId) =>
        $"{AnnounceProtocol.ServersPath}/{serverId}/catalog";

    /// <summary>Prefix of the bearer token in <see cref="CatalogRequest.UploadToken"/>. Distinct from
    /// the panel's `vck_` API keys so a leaked credential is identifiable on sight.</summary>
    public const string UploadTokenPrefix = "vct_";

    /// <summary>Lowercase hex sha256, like every other hash on this protocol.</summary>
    public const int PackageHashLength = 64;

    // §8 limits.

    /// <summary>Larger is a pool nobody curated.</summary>
    public const int MaxPackagesPerCatalog = 2000;

    /// <summary>Bounds request size with thumbnails attached. A server with 300 new maps sends several
    /// phase-2 requests rather than one enormous one.</summary>
    public const int MaxPackagesPerRequest = 50;

    /// <summary>Applied to the decoded image, before any decoder sees it, so a bomb never reaches
    /// one.</summary>
    public const int MaxThumbnailBytes = 64 * 1024;

    /// <summary>Hard cap on declared dimensions, checked before decoding (§7).</summary>
    public const int MaxThumbnailDimension = 1024;

    /// <summary>What the master re-encodes to (§7). Nothing a server uploaded is ever served
    /// verbatim.</summary>
    public const int ServedThumbnailMaxWidth = 512;

    public const string ServedThumbnailFormat = "webp";

    /// <summary>Accepted on the way in (§7). WebP only on the way out.</summary>
    public static readonly IReadOnlyList<string> AllowedThumbnailFormats = ["webp", "png", "jpeg"];

    /// <summary>Single use, bound to one server_id.</summary>
    public const int UploadTokenLifetimeSeconds = 300;

    /// <summary>A pool that changes more often than this is churning, not being edited.</summary>
    public const int UploadsPerServerPerHour = 4;

    public const int TitleMaxLength = 128;
    public const int AuthorMaxLength = 128;
    public const int DescriptionMaxLength = 2000;

    // §8 sizes the fields an operator writes and stops there. The rest are sized here, because every
    // unbounded field is a way around the ones §8 does size: 2000 characters of description is a limit
    // only until the same text fits in a mapinfo value instead. These are set well above anything a
    // real package produces, so they reject abuse rather than content.

    /// <summary>A .pk3 file name. The filesystem's own limit; anything longer did not come from a
    /// file.</summary>
    public const int PackageNameMaxLength = 255;

    public const int DownloadUrlMaxLength = 2048;

    public const int MaxGametypes = 16;
    public const int GametypeMaxLength = 32;

    public const int MaxMapinfoEntries = 64;
    public const int MapinfoKeyMaxLength = 64;
    public const int MapinfoValueMaxLength = 2000;
}
