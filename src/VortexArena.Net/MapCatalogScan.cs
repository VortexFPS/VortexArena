using System.Security.Cryptography;
using Conductor.Protocol;
using VortexArena.Common.Menu;
using VortexArena.Formats;
using VortexArena.Formats.Vfs;
using VmapMapInfo = VortexArena.Formats.Vmap.MapInfo;

namespace VortexArena.Net;

/// <summary>One server's map pool, in the shape the master wants it (map-catalog-v1 §2/§4).
///
/// Immutable, and that is load-bearing rather than tidy: the hash is announced now and the body is
/// uploaded minutes later on a different request, and the master rejects an index whose hash is not the
/// one that was announced. Holding a reference to the exact snapshot that was announced is what makes a
/// refresh landing in between harmless.</summary>
public sealed record MapCatalogSnapshot(
    string CatalogHash,
    IReadOnlyList<CatalogIndexEntry> Entries,
    IReadOnlyDictionary<string, CatalogPackage> Packages)
{
    /// <summary>The phase-2 bodies for <paramref name="unknownPackages"/>, split at the §8 per-request
    /// cap. A hash this server does not carry is skipped rather than sent empty: the master is asking
    /// what these packages are, and answering for one we cannot describe would be a fabrication.</summary>
    public IEnumerable<CatalogPackagesRequest> BatchDetails(IEnumerable<string> unknownPackages)
    {
        var batch = new List<CatalogPackage>(MapCatalogProtocol.MaxPackagesPerRequest);
        foreach (var sha in unknownPackages)
        {
            if (!Packages.TryGetValue(sha, out var package))
                continue;
            batch.Add(package);
            if (batch.Count < MapCatalogProtocol.MaxPackagesPerRequest)
                continue;
            yield return new CatalogPackagesRequest { Packages = batch };
            batch = new List<CatalogPackage>(MapCatalogProtocol.MaxPackagesPerRequest);
        }

        if (batch.Count > 0)
            yield return new CatalogPackagesRequest { Packages = batch };
    }
}

/// <summary>Builds a <see cref="MapCatalogSnapshot"/> from the .pk3 files a server carries
/// (map-catalog-v1 §10).
///
/// <para><b>This is seconds of disk work and must never run on the simulation thread or inside an
/// announce.</b> Every package is read twice — once to hash it, once as a zip — and a stock content tree
/// ships a half-gigabyte shared pack. <see cref="MapCatalogCache"/> owns the thread rule; this class is
/// deliberately a pure function of a list of paths so it can be exercised without one.</para>
///
/// <para>Everything it emits already satisfies <see cref="MapCatalogValidation"/>. A package whose
/// mapinfo would not (a 4 KB description, a download URL pointing at a private address) is reported with
/// the offending field dropped rather than dropped itself, because one map with a strange mapinfo must
/// not cost a server the batch its other forty-nine maps are in.</para></summary>
public static class MapCatalogScan
{
    /// <summary>Hash, name and size for every readable package, plus whatever metadata its mapinfo
    /// carries. Never throws for one bad package: a corrupt or locked .pk3 is logged and skipped, the
    /// same way <see cref="VirtualFileSystem.MountGameDir"/> skips one rather than failing the mount.
    ///
    /// <paramref name="downloadBaseUrl"/> is the operator's `sv_master_catalog_url`, empty when they have
    /// not published their packages anywhere. It is advisory in any case: the master never fetches it and
    /// the client verifies the package hash after downloading, so a wrong one costs a download attempt
    /// and nothing else (§5).</summary>
    public static MapCatalogSnapshot Scan(
        IEnumerable<string> packagePaths,
        Action<string> log,
        string? downloadBaseUrl = null,
        CancellationToken ct = default)
    {
        var entries = new List<CatalogIndexEntry>();
        var packages = new Dictionary<string, CatalogPackage>(StringComparer.Ordinal);

        // Checked once rather than per package, so a mistyped cvar is one log line and not one for every
        // map on the server.
        var downloadBase = UsableDownloadBase(downloadBaseUrl, log);

        foreach (var path in packagePaths)
        {
            ct.ThrowIfCancellationRequested();

            FileInfo file;
            try
            {
                file = new FileInfo(path);
                if (!file.Exists || file.Length <= 0)
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log($"catalog: cannot stat '{path}': {ex.Message}");
                continue;
            }

            string sha;
            try
            {
                sha = HashFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"catalog: cannot hash '{path}': {ex.Message}");
                continue;
            }

            // Identity is the hash, so the same bytes under two names are one package. Left in, the
            // duplicate would change the catalog hash in a way §2 does not describe, and the master's
            // ValidateIndex rejects the whole upload over it.
            if (packages.ContainsKey(sha))
                continue;

            entries.Add(new CatalogIndexEntry
            {
                PackageSha256 = sha,
                Name = Truncate(file.Name, MapCatalogProtocol.PackageNameMaxLength),
                SizeBytes = file.Length,
            });

            packages[sha] = Describe(sha, path, DownloadUrlFor(downloadBase, file.Name), log);
        }

        // Ordinal by hash, which is the order §2 hashes them in anyway. Sorting here as well makes the
        // uploaded index byte-stable across restarts, so a diff of two uploads is a real change rather
        // than a directory-enumeration reshuffle.
        entries.Sort(static (a, b) => string.CompareOrdinal(a.PackageSha256, b.PackageSha256));

        if (entries.Count > MapCatalogProtocol.MaxPackagesPerCatalog)
        {
            // Truncating beats reporting nothing: an over-cap server still gets most of its pool
            // browsable, where a rejected upload leaves players with no catalog at all. Deterministic,
            // because the list is already sorted — the same box reports the same pool every boot
            // instead of a different arbitrary slice.
            log($"catalog: {entries.Count} packages exceeds the protocol cap of "
                + $"{MapCatalogProtocol.MaxPackagesPerCatalog}; reporting the first "
                + $"{MapCatalogProtocol.MaxPackagesPerCatalog} by hash");
            for (var i = MapCatalogProtocol.MaxPackagesPerCatalog; i < entries.Count; i++)
                packages.Remove(entries[i].PackageSha256);
            entries.RemoveRange(MapCatalogProtocol.MaxPackagesPerCatalog,
                entries.Count - MapCatalogProtocol.MaxPackagesPerCatalog);
        }

        return new MapCatalogSnapshot(
            MapCatalogHash.ComputeFromEntries(entries), entries, packages);
    }

    /// <summary>Streamed, because a content tree ships packs in the hundreds of megabytes and reading
    /// one into memory to hash it would be a spike this can trivially avoid.</summary>
    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Read one package's metadata out of the archive. Only the hash is required (§4), so every
    /// failure below degrades to a package with no description rather than to a missing package — a map
    /// whose author left the mapinfo out is still a map players can download.</summary>
    private static CatalogPackage Describe(string sha, string path, string? downloadUrl, Action<string> log)
    {
        var bare = new CatalogPackage { PackageSha256 = sha, DownloadUrl = downloadUrl };

        VirtualFileSystem? vfs = null;
        try
        {
            vfs = new VirtualFileSystem();
            if (!vfs.Mount(path))
                return bare;

            // Ordinal, so a multi-map pack describes itself the same way on every machine.
            var mapinfoPaths = vfs.Find("maps/", "mapinfo").ToList();
            mapinfoPaths.Sort(StringComparer.Ordinal);

            var maps = new List<(string Bsp, VmapMapInfo Info)>(mapinfoPaths.Count);
            foreach (var mapinfoPath in mapinfoPaths)
            {
                try
                {
                    maps.Add((BspNameOf(mapinfoPath), VmapMapInfo.Parse(vfs.ReadText(mapinfoPath))));
                }
                catch (Exception ex) when (ex is AssetParseException or IOException or InvalidDataException)
                {
                    log($"catalog: cannot read '{mapinfoPath}' from '{path}': {ex.Message}");
                }
            }

            if (maps.Count == 0)
                return bare;

            var described = maps.Count > 1
                // A pack holding several maps has no single title, author or description, and borrowing
                // the first map's would put a name on a bundle that is not what the bundle is. The
                // browser then shows the file name, which is at least true. Gametypes still union
                // usefully ("this pack has CTF in it") and the map list goes in the free-form block.
                ? bare with
                {
                    Gametypes = GametypeCodes(maps),
                    Mapinfo = ExtraMapinfo(maps),
                }
                : bare with
                {
                    // Decolorized, because a mapper's ^1 codes are this engine's markup and would reach
                    // a web panel as literal noise. Same treatment the map-info dialog gives them.
                    Title = Clean(maps[0].Info.Title, MapCatalogProtocol.TitleMaxLength),
                    Description = Clean(maps[0].Info.Description, MapCatalogProtocol.DescriptionMaxLength),
                    Author = Clean(maps[0].Info.Author, MapCatalogProtocol.AuthorMaxLength),
                    Gametypes = GametypeCodes(maps),
                    Mapinfo = ExtraMapinfo(maps),
                    Thumbnail = FindThumbnail(vfs, maps[0].Bsp, log),
                };

            // Belt and braces against a mapinfo nobody anticipated. The master validates this anyway and
            // would answer 400 for the whole batch; falling back to the undescribed package keeps the
            // other forty-nine maps in that batch uploadable.
            if (MapCatalogValidation.ValidatePackage(described) is { } error)
            {
                log($"catalog: '{Path.GetFileName(path)}' reported without metadata: "
                    + $"{error.Error.Field} {error.Error.Message}");
                return bare;
            }

            return described;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Not a zip, truncated, or held open by something else. The package is still real and still
            // downloadable by hash, so it stays in the catalog undescribed.
            log($"catalog: cannot read '{path}' as a package: {ex.Message}");
            return bare;
        }
        finally
        {
            vfs?.Dispose();
        }
    }

    /// <summary>The announce-shaped short codes for every gametype the pack's maps declare, deduplicated
    /// and in declaration order. Routed through the same resolver the map list uses, so the deprecated
    /// spellings a shipped .mapinfo still carries (<c>arena</c>, <c>ffa</c>, <c>tourney</c>) reach the
    /// directory as the codes an announce uses, rather than as three names for one mode.</summary>
    private static IReadOnlyList<string>? GametypeCodes(List<(string Bsp, VmapMapInfo Info)> maps)
    {
        var codes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, info) in maps)
        foreach (var declared in info.Gametypes)
        {
            if (MapInfoBackend.ResolveGametype(declared.Name) is not { Length: > 0 } code)
                continue;
            if (code.Length > MapCatalogProtocol.GametypeMaxLength || codes.Count >= MapCatalogProtocol.MaxGametypes)
                continue;
            if (seen.Add(code))
                codes.Add(code);
        }
        return codes.Count == 0 ? null : codes;
    }

    /// <summary>The mapinfo directives with no field of their own on the wire, kept in the free-form
    /// block §4 leaves for exactly this. Keys are data, not schema, so they go out spelled the way the
    /// .mapinfo spells them.</summary>
    private static IReadOnlyDictionary<string, string>? ExtraMapinfo(List<(string Bsp, VmapMapInfo Info)> maps)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);

        // The bsp names are the only way to tell what is inside a multi-map pack, and the one thing a
        // player looking at an entry called "shared.pk3" actually wants to know.
        Put(extra, "maps", string.Join(", ", maps.Select(m => m.Bsp)));

        if (maps.Count == 1)
        {
            Put(extra, "cdtrack", maps[0].Info.CdTrack);
            Put(extra, "has", string.Join(", ", maps[0].Info.Has));
            if (maps[0].Info.Hidden)
                Put(extra, "hidden", "1");
        }

        return extra.Count == 0 ? null : extra;
    }

    private static void Put(Dictionary<string, string> into, string key, string value)
    {
        if (into.Count >= MapCatalogProtocol.MaxMapinfoEntries)
            return;
        if (Clean(value, MapCatalogProtocol.MapinfoValueMaxLength) is { } clean)
            into[key] = clean;
    }

    /// <summary>The levelshot chain the map-info dialog draws from: <c>maps/&lt;bsp&gt;</c> first, then
    /// the Quake 3 <c>levelshots/&lt;bsp&gt;</c>. Restricted to what §7 accepts and to what fits under the
    /// §8 byte cap before anything is base64'd.
    ///
    /// Returning null is the normal outcome for a pack without one, and §10 says so: a missing thumbnail
    /// draws a placeholder in the browser, it does not make the entry broken.</summary>
    private static CatalogThumbnail? FindThumbnail(VirtualFileSystem vfs, string bsp, Action<string> log)
    {
        foreach (var dir in new[] { "maps/", "levelshots/" })
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png" })
        {
            var vpath = dir + bsp + ext;
            if (!vfs.Exists(vpath))
                continue;

            byte[] data;
            try
            {
                data = vfs.ReadBytes(vpath);
            }
            catch (Exception ex) when (ex is AssetParseException or IOException or InvalidDataException)
            {
                log($"catalog: cannot read thumbnail '{vpath}': {ex.Message}");
                continue;
            }

            // Checked first, so an oversized levelshot costs one read and not a base64 expansion of it.
            // Several shipped maps land just over the cap; they simply have no thumbnail in the
            // directory, which §10 treats as ordinary.
            if (data.Length > MapCatalogProtocol.MaxThumbnailBytes)
                continue;

            // Sniffed, not trusted from the extension: the master checks magic bytes against the
            // declared format and would reject a .jpg that is really a PNG, which is a real thing to
            // find in a community map pack.
            if (!ImageHeader.TryRead(data, out var format, out var width, out var height))
                continue;
            if (width > MapCatalogProtocol.MaxThumbnailDimension
                || height > MapCatalogProtocol.MaxThumbnailDimension)
                continue;

            return new CatalogThumbnail
            {
                Format = format,
                Width = width,
                Height = height,
                DataBase64 = Convert.ToBase64String(data),
            };
        }

        return null;
    }

    /// <summary>The operator's <c>sv_master_catalog_url</c>, run through the §5 checks once, or null when
    /// they published nowhere or wrote something the master would refuse. Dropping it here rather than
    /// sending it is the difference between one warning in the operator's own log and the master rejecting
    /// every batch it rides in.</summary>
    private static string? UsableDownloadBase(string? baseUrl, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (MapCatalogValidation.ValidateDownloadUrl(trimmed + "/probe.pk3") is not { } error)
            return trimmed;

        log($"sv_master_catalog_url is not usable, sending no download URLs: {error.Error.Message}");
        return null;
    }

    /// <summary>The base joined to one package's file name. Re-checked, because the length cap is the one
    /// §5 rule a per-package name can still break — and a URL past it is dropped on its own rather than
    /// allowed to take the rest of that package's metadata down with it at the master.</summary>
    private static string? DownloadUrlFor(string? baseUrl, string fileName)
    {
        if (baseUrl is null)
            return null;
        var url = baseUrl + "/" + Uri.EscapeDataString(fileName);
        return MapCatalogValidation.ValidateDownloadUrl(url) is null ? url : null;
    }

    /// <summary>"maps/stormkeep.mapinfo" → "stormkeep". Paths out of the VFS are already normalized to
    /// lowercase forward slashes.</summary>
    private static string BspNameOf(string mapinfoVpath)
    {
        var slash = mapinfoVpath.LastIndexOf('/');
        var stem = slash < 0 ? mapinfoVpath : mapinfoVpath[(slash + 1)..];
        var dot = stem.LastIndexOf('.');
        return dot <= 0 ? stem : stem[..dot];
    }

    /// <summary>Decolorize, strip control characters, cap at the §8 length, and collapse an empty result
    /// to null so an absent field is absent on the wire rather than present and blank. Sanitizing with
    /// the protocol's own helper is what makes the length this measures the same length the master
    /// measures.</summary>
    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var clean = Truncate(AnnounceValidation.Sanitize(MenuTextFormat.Decolorize(value)), maxLength);
        return clean.Length == 0 ? null : clean;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
