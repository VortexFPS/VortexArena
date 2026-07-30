using System.Security.Cryptography;
using System.Text;

namespace Conductor.Protocol;

/// <summary>The catalog hash from map-catalog-v1 §2, implemented once for both ends.
///
/// The game server computes it and announces it; the master recomputes it from an uploaded index and
/// rejects a mismatch (§4). Two implementations of this formula disagreeing would not degrade
/// gracefully into a slow directory, it would reject every upload from every server, so there is one
/// implementation and both sides call it.</summary>
public static class MapCatalogHash
{
    /// <summary>Domain prefix, trailing newline included. It is what stops a catalog hash from ever
    /// colliding with a package hash and being taken for one (§2).</summary>
    public const string Domain = "vortex-catalog-v1\n";

    private static readonly byte[] DomainBytes = Encoding.UTF8.GetBytes(Domain);
    private static readonly byte[] Separator = "\n"u8.ToArray();

    /// <summary>Lowercase hex sha256 of the domain prefix followed by the sorted package hashes,
    /// newline-separated. An empty pool is a legal input and hashes the prefix alone: a server that
    /// carries no maps still has a catalog, which is not the same as one that does not report a
    /// catalog at all (§3).
    ///
    /// The sort is ordinal. The input is lowercase hex, and a culture-aware sort would order it
    /// differently on somebody else's machine, which is the same failure as having two
    /// implementations.
    ///
    /// Duplicates are not collapsed, because §2 sorts and does not deduplicate. A pool that reports
    /// one package twice is rejected by <see cref="MapCatalogValidation.ValidateIndex"/> instead, so
    /// the case never reaches a second implementation that might guess differently.</summary>
    /// <exception cref="ArgumentException">A value is not a lowercase hex sha256. Quietly folding
    /// uppercase here would produce a hash that no other implementation of §2 agrees with, which is
    /// exactly the failure this function exists to prevent.</exception>
    public static string Compute(IEnumerable<string> packageHashes)
    {
        var sorted = packageHashes.ToArray();
        foreach (var packageHash in sorted)
            if (!AnnounceValidation.IsHexSha256(packageHash))
                throw new ArgumentException(
                    $"not a lowercase hex sha256: '{packageHash}'", nameof(packageHashes));

        Array.Sort(sorted, StringComparer.Ordinal);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(DomainBytes);
        for (var i = 0; i < sorted.Length; i++)
        {
            if (i > 0)
                sha.AppendData(Separator);
            sha.AppendData(Encoding.UTF8.GetBytes(sorted[i]));
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The master's half of §4: recompute from what was uploaded. Exists so the projection
    /// from entries to hashes is written once too, since getting that wrong fails identically to
    /// getting the formula wrong.
    ///
    /// Not an overload of <see cref="Compute"/>, because then `Compute([])` would be ambiguous and the
    /// empty pool is the one case a caller is most likely to write out by hand.</summary>
    public static string ComputeFromEntries(IEnumerable<CatalogIndexEntry> entries) =>
        Compute(entries.Select(entry => entry.PackageSha256));
}
