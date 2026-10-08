using System.Security.Cryptography;

namespace VortexArena.Modding;

/// <summary>
/// The on-disk store of downloaded mod files, keyed by the SHA-256 of their contents.
///
/// Content-addressed so that the same file offered by two servers under two names is downloaded once,
/// and so that a file can never be served from the cache under a hash it does not have: nothing enters
/// except through <see cref="Store"/>, which hashes what it was given, and <see cref="TryGetPath"/>
/// re-checks the size. Layout: <c>&lt;root&gt;/&lt;first two hex digits&gt;/&lt;sha256&gt;</c>.
/// </summary>
public sealed class ModCache
{
    private readonly string _root;

    /// <summary>Total bytes the cache may hold before the least recently used files are evicted.</summary>
    public long BudgetBytes { get; init; } = 8L << 30;

    public ModCache(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
    }

    /// <summary>The cached file for <paramref name="artifact"/>, if present with the right size.</summary>
    public bool TryGetPath(ModArtifact artifact, out string path)
    {
        path = "";
        if (!ModManifest.IsSha256(artifact.Sha256)) return false;
        string candidate = PathFor(artifact.Sha256);
        FileInfo file = new(candidate);
        if (!file.Exists || file.Length != artifact.SizeBytes) return false;
        // Reading a file is what "recently used" means for eviction.
        try { file.LastAccessTimeUtc = DateTime.UtcNow; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        path = candidate;
        return true;
    }

    public bool Contains(ModArtifact artifact) => TryGetPath(artifact, out _);

    /// <summary>
    /// Copies <paramref name="content"/> into the cache if - and only if - it is exactly the file the
    /// artifact describes. Reads at most <c>SizeBytes + 1</c> bytes, so an endless or oversized stream
    /// cannot fill the disk. Returns the cached path.
    /// </summary>
    /// <exception cref="ModManifestException">The content's size or SHA-256 does not match the artifact.</exception>
    public string Store(ModArtifact artifact, Stream content)
    {
        if (!ModManifest.IsSha256(artifact.Sha256) || artifact.SizeBytes <= 0)
            throw new ModManifestException($"'{artifact.Name}' has no valid size or SHA-256");

        string finalPath = PathFor(artifact.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        // Written under a temporary name and renamed only after the hash checks out, so a crash or a
        // failed verification never leaves a file at the address of a hash it does not have.
        string tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > artifact.SizeBytes)
                        throw new ModManifestException($"'{artifact.Name}' is larger than the {artifact.SizeBytes} bytes its manifest declares");
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
            }

            if (total != artifact.SizeBytes)
                throw new ModManifestException($"'{artifact.Name}' is {total} bytes; its manifest declares {artifact.SizeBytes}");
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != artifact.Sha256)
                throw new ModManifestException($"'{artifact.Name}' does not match its SHA-256 (got {actual})");

            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>The artifacts of <paramref name="manifest"/> that still have to be downloaded.</summary>
    public IReadOnlyList<ModArtifact> Missing(ModManifest manifest)
    {
        List<ModArtifact> missing = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ModArtifact artifact in manifest.Artifacts)
            if (seen.Add(artifact.Sha256) && !Contains(artifact)) missing.Add(artifact);
        return missing;
    }

    /// <summary>
    /// Deletes least-recently-used files until the cache fits <see cref="BudgetBytes"/>, never touching a
    /// hash in <paramref name="keep"/> (the mod in use). Returns the bytes freed.
    /// </summary>
    public long Evict(IEnumerable<string>? keep = null)
    {
        if (!Directory.Exists(_root)) return 0;
        HashSet<string> pinned = new(keep ?? Array.Empty<string>(), StringComparer.Ordinal);
        List<FileInfo> files = new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => ModManifest.IsSha256(f.Name))
            .OrderBy(f => f.LastAccessTimeUtc)
            .ToList();

        long total = files.Sum(f => f.Length), freed = 0;
        foreach (FileInfo file in files)
        {
            if (total - freed <= BudgetBytes) break;
            if (pinned.Contains(file.Name)) continue;
            try
            {
                long length = file.Length;
                file.Delete();
                freed += length;
            }
            catch (IOException) { }                    // in use by another client instance: skip it
            catch (UnauthorizedAccessException) { }
        }
        return freed;
    }

    private string PathFor(string sha256) => Path.Combine(_root, sha256[..2], sha256);
}
