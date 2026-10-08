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
        using ModCacheWriter writer = BeginStore(artifact);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            writer.Append(buffer.AsSpan(0, read));
        return writer.Commit();
    }

    /// <summary>
    /// Starts receiving <paramref name="artifact"/> piece by piece (the in-band download). Bytes go to a
    /// temporary file beside the final address; <see cref="ModCacheWriter.Commit"/> moves it into place
    /// only when the size and SHA-256 both match, and disposing without committing deletes it.
    /// </summary>
    /// <exception cref="ModManifestException">The artifact has no usable size or hash.</exception>
    public ModCacheWriter BeginStore(ModArtifact artifact)
    {
        if (!ModManifest.IsSha256(artifact.Sha256) || artifact.SizeBytes <= 0)
            throw new ModManifestException($"'{artifact.Name}' has no valid size or SHA-256");
        string finalPath = PathFor(artifact.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        return new ModCacheWriter(artifact, finalPath);
    }

    /// <summary>Deletes the cached file for <paramref name="artifact"/> (used when it fails a re-check at load).</summary>
    public void Remove(ModArtifact artifact)
    {
        if (!ModManifest.IsSha256(artifact.Sha256)) return;
        try { File.Delete(PathFor(artifact.Sha256)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes temporary download files older than <paramref name="olderThan"/>: what a crash or a killed
    /// process leaves behind. Age-limited so a second running client's download in progress is left alone.
    /// Returns the bytes freed.
    /// </summary>
    public long CleanPartials(TimeSpan olderThan)
    {
        if (!Directory.Exists(_root)) return 0;
        long freed = 0;
        DateTime cutoff = DateTime.UtcNow - olderThan;
        foreach (FileInfo file in new DirectoryInfo(_root).EnumerateFiles("*" + ModCacheWriter.PartialSuffix, SearchOption.AllDirectories))
        {
            try
            {
                if (file.LastWriteTimeUtc > cutoff) continue;
                long length = file.Length;
                file.Delete();
                freed += length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return freed;
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

/// <summary>
/// One file on its way into the <see cref="ModCache"/>. Never holds more than the caller's current piece
/// in memory, never writes past the size the manifest declared, and never leaves anything at the final
/// address unless the whole file hashed to the expected value.
/// </summary>
public sealed class ModCacheWriter : IDisposable
{
    internal const string PartialSuffix = ".part";

    private readonly ModArtifact _artifact;
    private readonly string _finalPath, _tempPath;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private FileStream? _output;
    private bool _finished;

    public long BytesWritten { get; private set; }
    public long BytesExpected => _artifact.SizeBytes;
    public bool IsComplete => BytesWritten == _artifact.SizeBytes;

    internal ModCacheWriter(ModArtifact artifact, string finalPath)
    {
        _artifact = artifact;
        _finalPath = finalPath;
        _tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + PartialSuffix;
        _output = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    /// <exception cref="ModManifestException">The piece would take the file past its declared size.</exception>
    public void Append(ReadOnlySpan<byte> piece)
    {
        if (_finished || _output is null) throw new InvalidOperationException("the download has already finished");
        if (piece.Length > _artifact.SizeBytes - BytesWritten)
            throw new ModManifestException($"'{_artifact.Name}' is larger than the {_artifact.SizeBytes} bytes its manifest declares");
        _hash.AppendData(piece);
        _output.Write(piece);
        BytesWritten += piece.Length;
    }

    /// <summary>Verifies size and SHA-256 and moves the file to its address. Returns the cached path.</summary>
    /// <exception cref="ModManifestException">The content is not the file the artifact describes; nothing was stored.</exception>
    public string Commit()
    {
        if (_finished) throw new InvalidOperationException("the download has already finished");
        _finished = true;
        _output?.Dispose();
        _output = null;
        try
        {
            if (BytesWritten != _artifact.SizeBytes)
                throw new ModManifestException($"'{_artifact.Name}' is {BytesWritten} bytes; its manifest declares {_artifact.SizeBytes}");
            string actual = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != _artifact.Sha256)
                throw new ModManifestException($"'{_artifact.Name}' does not match its SHA-256 (got {actual})");
            File.Move(_tempPath, _finalPath, overwrite: true);
            return _finalPath;
        }
        finally
        {
            DeleteTemp();
        }
    }

    public void Dispose()
    {
        _finished = true;
        _output?.Dispose();
        _output = null;
        _hash.Dispose();
        DeleteTemp();
    }

    private void DeleteTemp()
    {
        try { if (File.Exists(_tempPath)) File.Delete(_tempPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
