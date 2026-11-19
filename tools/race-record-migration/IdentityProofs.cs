using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VortexArena.Net;

namespace VortexArena.RecordMigration;

public sealed record MigrationManifest(int Version, string SourceSha256, string SessionId, IdentityMapping[] Mappings);

public sealed record IdentityMapping(string LegacyUid, string DisplayName, byte[] NativePublicKey, byte[] NativeSignature,
    bool LegacySigned, string LegacyAuthenticationEvidence);

public sealed record VerifiedMapping(string NativeUid, string DisplayName);

public static class IdentityProofs
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IReadOnlyDictionary<string, VerifiedMapping> Verify(byte[] manifest, byte[] signature, string authorityPem,
        string sourceSha256, string sessionId)
    {
        using var authority = ReadAuthority(authorityPem);
        if (!HasValidSignature(authority, manifest, signature))
            throw new FormatException("The attestation signature does not match the trusted operator public key.");

        var proof = JsonSerializer.Deserialize<MigrationManifest>(manifest, Json);
        if (proof is not { Version: 1, Mappings: not null } || proof.SourceSha256 != sourceSha256 || proof.SessionId != sessionId)
            throw new FormatException("The attestation does not match this source database and native sessionid.");

        var mappings = new Dictionary<string, VerifiedMapping>(StringComparer.Ordinal);
        var nativeUids = new HashSet<string>(StringComparer.Ordinal);
        string authorityUid = PlayerIdentity.ComputeFingerprint(authority.ExportSubjectPublicKeyInfo());
        foreach (var mapping in proof.Mappings)
        {
            if (mapping is not { LegacySigned: true }
                || string.IsNullOrWhiteSpace(mapping.LegacyAuthenticationEvidence)
                || !IsLegacyUid(mapping.LegacyUid) || !IsDisplayName(mapping.DisplayName)
                || mapping.NativePublicKey is not { Length: > 0 } || mapping.NativeSignature is not { Length: 64 })
                throw new FormatException("Each attested mapping requires a signed legacy authentication record and native consent.");

            using var native = ECDsa.Create();
            native.ImportSubjectPublicKeyInfo(mapping.NativePublicKey, out int bytesRead);
            if (bytesRead != mapping.NativePublicKey.Length
                || native.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || !native.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(mapping.NativePublicKey)
                || !ServerChallenge.Verify(mapping.NativePublicKey,
                    ConsentBytes(sourceSha256, sessionId, authorityUid, mapping.LegacyUid, mapping.DisplayName),
                    mapping.NativeSignature))
                throw new FormatException("The native identity did not sign this mapping and consent statement.");

            string nativeUid = PlayerIdentity.ComputeFingerprint(mapping.NativePublicKey);
            if (!mappings.TryAdd(mapping.LegacyUid, new(nativeUid, mapping.DisplayName)) || !nativeUids.Add(nativeUid))
                throw new FormatException("Conflicting mappings: each original and native identity may appear once.");
        }
        return mappings;
    }

    public static IdentityMapping SignConsent(PlayerIdentity identity, string sourceSha256, string sessionId, string authorityPem,
        string legacyUid, string displayName)
    {
        if (sourceSha256 is not { Length: 64 } || sourceSha256.Any(c => !char.IsAsciiHexDigitLower(c))
            || !IsLegacyUid(legacyUid) || !IsDisplayName(displayName))
            throw new FormatException("Expected a lowercase SHA-256 source hash, original fingerprint and printable display name.");
        using var authority = ReadAuthority(authorityPem);
        string authorityUid = PlayerIdentity.ComputeFingerprint(authority.ExportSubjectPublicKeyInfo());
        return new(legacyUid, displayName, identity.PublicKey,
            identity.Sign(ConsentBytes(sourceSha256, sessionId, authorityUid, legacyUid, displayName)), false, "");
    }

    internal static void VerifyArchivedSignature(byte[] manifest, byte[] signature, string archivedPem, string trustedPem)
    {
        using var archived = ReadAuthority(archivedPem);
        using var trusted = ReadAuthority(trustedPem);
        if (!archived.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(trusted.ExportSubjectPublicKeyInfo()))
            throw new FormatException("The archived operator key differs from the trusted key.");
        if (!HasValidSignature(trusted, manifest, signature))
            throw new FormatException("The archived signature does not verify against the original attestation.");
    }

    internal static bool IsLegacyUid(string? value)
    {
        // DarkPlaces passes FP64_SIZE (44) to d0_blind_id_fingerprint64_public_id.
        // The reference encoder writes the complete SHA-256 result as padded standard Base64.
        if (value is not { Length: 44 }) return false;
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        return Convert.TryFromBase64Chars(value, hash, out int written) && written == hash.Length
            && Convert.ToBase64String(hash) == value;
    }

    private static bool HasValidSignature(ECDsa authority, byte[] manifest, byte[] signature) =>
        authority.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private static bool IsDisplayName(string? value) =>
        value is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static ECDsa ReadAuthority(string authorityPem)
    {
        var key = ECDsa.Create();
        try
        {
            // Intentionally accept only SPKI public PEM; the operator's signing key stays outside the profile.
            string pem = authorityPem.Trim();
            var fields = PemEncoding.Find(pem);
            if (!pem.AsSpan()[fields.Label].SequenceEqual("PUBLIC KEY") || fields.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length))
                throw new FormatException("Expected one trusted operator PUBLIC KEY PEM.");
            key.ImportFromPem(pem);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static byte[] ConsentBytes(string sourceSha256, string sessionId, string authorityUid, string legacyUid,
        string displayName) => Encoding.UTF8.GetBytes(
        "Vortex Arena record migration v1\n" +
        "I authorize identity tracking and display of my chosen nickname for these migrated rankings.\n" +
        sourceSha256 + "\n" + authorityUid + "\n" + legacyUid + "\n" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(sessionId)) + "\n" + displayName);
}
