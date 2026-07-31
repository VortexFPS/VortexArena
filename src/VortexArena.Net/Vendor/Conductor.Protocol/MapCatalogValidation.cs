using System.Net;
using System.Net.Sockets;

namespace Conductor.Protocol;

/// <summary>The limits from map catalog §8 and the URL rules from §5, implemented once.
///
/// Same reason as <see cref="AnnounceValidation"/>: the master runs this because it must never trust
/// an upload, and the game server runs it before sending so an oversized description names itself in
/// the server's own log instead of arriving as an opaque 400 from a remote host.
///
/// Errors name a path, not just a field: `entries[3].package_sha256`. One bad row in a two thousand
/// row index is otherwise unfindable.</summary>
public static class MapCatalogValidation
{
    /// <summary>The announce field (§3). Null is valid and means "this server does not report a
    /// catalog"; it does not mean the pool is empty.</summary>
    public static ProtocolError? ValidateAnnouncedHash(string? mapCatalogHash)
    {
        if (mapCatalogHash is null)
            return null;

        // An empty string is not absence. A server that means "no catalog" omits the field.
        return AnnounceValidation.IsHexSha256(mapCatalogHash)
            ? null
            : Invalid("map_catalog_hash",
                $"expected {MapCatalogProtocol.PackageHashLength} lowercase hex characters");
    }

    /// <summary>Upload phase 1 (§4). Checks the body against itself, including that the entries hash to
    /// the `catalog_hash` in the same request.
    ///
    /// The master must additionally compare that hash to the one the server announced, with
    /// <see cref="ValidateMatchesAnnounced"/>. That half cannot live here because it needs the
    /// announce state, and it is the half that binds a server to its claim.</summary>
    public static ProtocolError? ValidateIndex(CatalogIndexRequest r)
    {
        if (string.IsNullOrEmpty(r.CatalogHash))
            return Missing("catalog_hash");
        if (!AnnounceValidation.IsHexSha256(r.CatalogHash))
            return Invalid("catalog_hash",
                $"expected {MapCatalogProtocol.PackageHashLength} lowercase hex characters");

        if (r.Entries is null)
            return Missing("entries");
        if (r.Entries.Count > MapCatalogProtocol.MaxPackagesPerCatalog)
            return Invalid("entries",
                $"a catalog carries at most {MapCatalogProtocol.MaxPackagesPerCatalog} packages");

        // Ordinal: these are lowercase hex, and a culture-aware set would consider values equal that
        // the hash in §2 treats as different.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < r.Entries.Count; i++)
        {
            var entry = r.Entries[i];
            var at = $"entries[{i}]";

            if (string.IsNullOrEmpty(entry.PackageSha256))
                return Missing($"{at}.package_sha256");
            if (!AnnounceValidation.IsHexSha256(entry.PackageSha256))
                return Invalid($"{at}.package_sha256",
                    $"expected {MapCatalogProtocol.PackageHashLength} lowercase hex characters");

            // The formula in §2 sorts and does not deduplicate, so a repeated hash would change the
            // catalog hash in a way no reader of the spec would predict. Rejecting it keeps the one
            // input that could make two correct implementations disagree out of the protocol.
            if (!seen.Add(entry.PackageSha256))
                return Invalid($"{at}.package_sha256", "duplicate package in this catalog");

            if (string.IsNullOrWhiteSpace(entry.Name))
                return Missing($"{at}.name");
            if (AnnounceValidation.Sanitize(entry.Name).Length > MapCatalogProtocol.PackageNameMaxLength)
                return Invalid($"{at}.name",
                    $"name exceeds {MapCatalogProtocol.PackageNameMaxLength} characters");

            if (entry.SizeBytes <= 0)
                return Invalid($"{at}.size_bytes", "size_bytes must be positive");
        }

        // §4: without this the catalog hash would stop being a claim the body is bound to, and a
        // server could describe a pool it never announced.
        if (MapCatalogHash.ComputeFromEntries(r.Entries) != r.CatalogHash)
            return ProtocolError.Of(ProtocolErrorCodes.CatalogHashMismatch,
                "catalog_hash does not match the entries in this request", "catalog_hash");

        return null;
    }

    /// <summary>The master's half of §4. Kept next to <see cref="ValidateIndex"/> so both ends read
    /// the two checks together and neither is mistaken for the other.</summary>
    public static ProtocolError? ValidateMatchesAnnounced(string announcedHash, CatalogIndexRequest r) =>
        string.Equals(announcedHash, r.CatalogHash, StringComparison.Ordinal)
            ? null
            : ProtocolError.Of(ProtocolErrorCodes.CatalogHashMismatch,
                "catalog_hash does not match the hash this server announced", "catalog_hash");

    /// <summary>Upload phase 2 (§4).</summary>
    public static ProtocolError? ValidatePackages(CatalogPackagesRequest r)
    {
        if (r.Packages is null)
            return Missing("packages");

        // An empty batch is a no-op, not an error. The last batch of a split upload is allowed to come
        // out empty, and failing an upload over an off-by-one in somebody's batching loop would cost
        // more than it catches.
        if (r.Packages.Count > MapCatalogProtocol.MaxPackagesPerRequest)
            return Invalid("packages",
                $"at most {MapCatalogProtocol.MaxPackagesPerRequest} packages per request");

        for (var i = 0; i < r.Packages.Count; i++)
        {
            var error = ValidatePackage(r.Packages[i], $"packages[{i}]");
            if (error is not null)
                return error;
        }

        return null;
    }

    /// <summary>One package. Everything but the hash is optional: a .pk3 with sparse mapinfo is still a
    /// real map, and dropping it because its author left a field blank would be the wrong trade.</summary>
    public static ProtocolError? ValidatePackage(CatalogPackage p, string at = "package")
    {
        if (string.IsNullOrEmpty(p.PackageSha256))
            return Missing($"{at}.package_sha256");
        if (!AnnounceValidation.IsHexSha256(p.PackageSha256))
            return Invalid($"{at}.package_sha256",
                $"expected {MapCatalogProtocol.PackageHashLength} lowercase hex characters");

        // Measured after sanitizing, exactly as hostname is: the limit is on what will be stored and
        // shown, and control characters are removed before either.
        if (TooLong(p.Title, MapCatalogProtocol.TitleMaxLength))
            return Invalid($"{at}.title", $"title exceeds {MapCatalogProtocol.TitleMaxLength} characters");
        if (TooLong(p.Author, MapCatalogProtocol.AuthorMaxLength))
            return Invalid($"{at}.author", $"author exceeds {MapCatalogProtocol.AuthorMaxLength} characters");
        if (TooLong(p.Description, MapCatalogProtocol.DescriptionMaxLength))
            return Invalid($"{at}.description",
                $"description exceeds {MapCatalogProtocol.DescriptionMaxLength} characters");

        if (p.Gametypes is { } gametypes)
        {
            if (gametypes.Count > MapCatalogProtocol.MaxGametypes)
                return Invalid($"{at}.gametypes",
                    $"at most {MapCatalogProtocol.MaxGametypes} gametypes");
            foreach (var gametype in gametypes)
                if (string.IsNullOrWhiteSpace(gametype) ||
                    TooLong(gametype, MapCatalogProtocol.GametypeMaxLength))
                    return Invalid($"{at}.gametypes",
                        $"each gametype is 1 to {MapCatalogProtocol.GametypeMaxLength} characters");
        }

        if (p.Mapinfo is { } mapinfo)
        {
            if (mapinfo.Count > MapCatalogProtocol.MaxMapinfoEntries)
                return Invalid($"{at}.mapinfo",
                    $"at most {MapCatalogProtocol.MaxMapinfoEntries} mapinfo keys");
            foreach (var (key, value) in mapinfo)
            {
                if (string.IsNullOrWhiteSpace(key) || TooLong(key, MapCatalogProtocol.MapinfoKeyMaxLength))
                    return Invalid($"{at}.mapinfo",
                        $"each mapinfo key is 1 to {MapCatalogProtocol.MapinfoKeyMaxLength} characters");
                if (TooLong(value, MapCatalogProtocol.MapinfoValueMaxLength))
                    return Invalid($"{at}.mapinfo",
                        $"mapinfo value for '{key}' exceeds {MapCatalogProtocol.MapinfoValueMaxLength} characters");
            }
        }

        if (p.DownloadUrl is { } url)
        {
            var urlError = ValidateDownloadUrl(url, $"{at}.download_url");
            if (urlError is not null)
                return urlError;
        }

        if (p.Thumbnail is { } thumbnail)
        {
            var thumbnailError = ValidateThumbnail(thumbnail, out _, $"{at}.thumbnail");
            if (thumbnailError is not null)
                return thumbnailError;
        }

        return null;
    }

    /// <summary>The checks from §5 that can be made without a resolver: https, no credentials, and no
    /// literal address in a range the master must not point players at.
    ///
    /// The third check in §5 is that the master never fetches the URL, which is not a validation at all
    /// but a rule about what the master does not do. A master that validated URLs by requesting them
    /// would be a request forwarder for anyone who could announce.
    ///
    /// A hostname that resolves into a forbidden range still passes here. The master must resolve and
    /// run <see cref="IsDisallowedDownloadAddress"/> on every answer, because a name is not an address
    /// and this layer has no business doing DNS.</summary>
    public static ProtocolError? ValidateDownloadUrl(string url, string field = "download_url")
    {
        if (string.IsNullOrWhiteSpace(url))
            return Missing(field);
        if (url.Length > MapCatalogProtocol.DownloadUrlMaxLength)
            return Invalid(field, $"download_url exceeds {MapCatalogProtocol.DownloadUrlMaxLength} characters");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Invalid(field, "download_url must be an absolute URL");
        if (uri.Scheme != Uri.UriSchemeHttps)
            return Invalid(field, "download_url must be https");

        // Credentials in a URL that ends up in front of players is a phishing shape, and no download
        // needs them.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return Invalid(field, "download_url must not carry credentials");

        var host = uri.Host;
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];
        if (IPAddress.TryParse(host, out var literal) && IsDisallowedDownloadAddress(literal))
            return Invalid(field, "download_url must not point at a private or loopback address");

        return null;
    }

    /// <summary>Ranges a download URL must not reach (§5). Public because the master has to run it
    /// again on every address a hostname resolves to, and because a second copy of this list would be
    /// the copy that forgets CGNAT.</summary>
    public static bool IsDisallowedDownloadAddress(IPAddress address)
    {
        // A v4 address wearing a v6 costume is still a v4 address, and checking the costume is how
        // these lists get bypassed.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        // Covers all of 127/8 and ::1.
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                  // 0.0.0.0/8, "this network"
                10 => true,                                 // 10/8
                100 when b[1] is >= 64 and <= 127 => true,   // 100.64/10 CGNAT
                169 when b[1] == 254 => true,                // 169.254/16 link-local
                172 when b[1] is >= 16 and <= 31 => true,    // 172.16/12
                192 when b[1] == 168 => true,                // 192.168/16
                >= 224 => true,                              // multicast, reserved, broadcast
                _ => false,
            };
        }

        return address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
    }

    /// <summary>The pre-decode checks from §7: declared format on the allowlist, dimensions capped,
    /// payload capped, and magic bytes matching the declared format. Everything here happens before any
    /// image decoder sees the bytes, which is the entire point of the order.</summary>
    public static ProtocolError? ValidateThumbnail(CatalogThumbnail t) =>
        ValidateThumbnail(t, out _);

    /// <summary>As above, and hands back the decoded bytes so the master does not base64-decode an
    /// untrusted payload a second time.</summary>
    public static ProtocolError? ValidateThumbnail(CatalogThumbnail t, out byte[] data,
        string at = "thumbnail")
    {
        data = [];

        if (string.IsNullOrEmpty(t.Format))
            return Missing($"{at}.format");
        if (!MapCatalogProtocol.AllowedThumbnailFormats.Contains(t.Format))
            return Invalid($"{at}.format",
                $"format must be one of {string.Join(", ", MapCatalogProtocol.AllowedThumbnailFormats)}");

        if (t.Width < 1 || t.Width > MapCatalogProtocol.MaxThumbnailDimension)
            return Invalid($"{at}.width",
                $"width must be 1-{MapCatalogProtocol.MaxThumbnailDimension}");
        if (t.Height < 1 || t.Height > MapCatalogProtocol.MaxThumbnailDimension)
            return Invalid($"{at}.height",
                $"height must be 1-{MapCatalogProtocol.MaxThumbnailDimension}");

        if (string.IsNullOrEmpty(t.DataBase64))
            return Missing($"{at}.data_base64");

        // Checked on the encoded length first, so an enormous payload is rejected without being
        // decoded into memory at all.
        if (t.DataBase64.Length > MaxEncodedThumbnailChars)
            return Invalid($"{at}.data_base64",
                $"thumbnail exceeds {MapCatalogProtocol.MaxThumbnailBytes} bytes");

        var buffer = new byte[MapCatalogProtocol.MaxThumbnailBytes];
        if (!Convert.TryFromBase64String(t.DataBase64, buffer, out var written))
            return Invalid($"{at}.data_base64", "data_base64 is not valid base64, or exceeds the size cap");
        if (written == 0)
            return Invalid($"{at}.data_base64", "data_base64 is empty");

        var bytes = buffer.AsSpan(0, written);
        if (!MatchesDeclaredFormat(t.Format, bytes))
            return Invalid($"{at}.data_base64", $"data does not look like {t.Format}");

        data = bytes.ToArray();
        return null;
    }

    /// <summary>Magic bytes, against the declared format (§7). A PNG announced as WebP is either a
    /// broken uploader or somebody probing for a decoder that trusts the label.</summary>
    private static bool MatchesDeclaredFormat(string format, ReadOnlySpan<byte> data) => format switch
    {
        "png" => data.StartsWith(PngMagic),
        "jpeg" => data.StartsWith(JpegMagic),
        // RIFF....WEBP: four bytes of container length sit between the two tags.
        "webp" => data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    /// <summary>Longest base64 that can decode to the byte cap. Padding makes this an upper bound, not
    /// an exact figure, which is the right direction to be wrong in.</summary>
    private static readonly int MaxEncodedThumbnailChars =
        (MapCatalogProtocol.MaxThumbnailBytes + 2) / 3 * 4;

    private static bool TooLong(string? value, int max) =>
        value is not null && AnnounceValidation.Sanitize(value).Length > max;

    private static ProtocolError Invalid(string field, string message) =>
        ProtocolError.Of(ProtocolErrorCodes.InvalidField, message, field);

    private static ProtocolError Missing(string field) =>
        ProtocolError.Of(ProtocolErrorCodes.MissingField, $"{field} is required", field);
}
