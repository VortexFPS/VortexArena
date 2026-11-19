using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VortexArena.Common.Gameplay;
using VortexArena.Server;

namespace VortexArena.RecordMigration;

public sealed record HistoricalRecord(OriginalRecord Record, string Disposition);
public sealed record MigrationResult(string SourceSha256, string ArchivePath, int Updated, IReadOnlyList<HistoricalRecord> Records);
public sealed record SignedAttestation(byte[] Manifest, byte[] Signature, string TrustedAuthorityPem);

public static class Migration
{
    private const string BindingPrefix = "R\t/record-migration/v1/binding/";
    private const string SourcePrefix = "R\t/record-migration/v1/source/";

    public static string Hash(byte[] input) => Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

    public static MigrationResult Apply(byte[] source, string profileDirectory, string sessionId, SignedAttestation? attestation)
    {
        string sourceHash = Hash(source);
        var records = OriginalRecords.Read(source);
        var mappings = attestation is null ? new Dictionary<string, VerifiedMapping>() :
            IdentityProofs.Verify(attestation.Manifest, attestation.Signature, attestation.TrustedAuthorityPem, sourceHash, sessionId);

        string target = Path.Combine(profileDirectory, RaceRecordFile.ProfileFileName(sessionId));
        string archiveId = attestation is null ? "unmapped" : Hash(attestation.Manifest);
        string archive = Path.Combine(profileDirectory, "record-migrations", Path.GetFileNameWithoutExtension(target), sourceHash, archiveId);

        var history = records.Select(record => new HistoricalRecord(record, Classify(record, mappings))).ToArray();
        int updated = 0;
        RaceRecordFile.UpdateOffline(target, original =>
        {
            string previous = RaceRecords.Export();
            try
            {
                RaceRecords.Import(original);
                var bindings = ReadBindings(original);
                string replayPrefix = SourcePrefix + sourceHash + "/";
                var applied = original.Split('\n').Select(line => line.TrimEnd('\r'))
                    .Where(line => line.StartsWith(replayPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

                var markers = new StringBuilder();
                var eligibleUids = history.Where(entry => entry.Disposition == "verified")
                    .Select(entry => entry.Record.LegacyUid!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var pending = new HashSet<string>(StringComparer.Ordinal);
                foreach (string legacyUid in eligibleUids)
                {
                    string nativeUid = mappings[legacyUid].NativeUid;
                    if (bindings.TryGetValue(legacyUid, out string? prior))
                    {
                        if (prior != nativeUid)
                            throw new FormatException("This original identity is already mapped to another native identity.");
                    }
                    else
                    {
                        if (bindings.ContainsValue(nativeUid))
                            throw new FormatException("This native identity is already mapped to another original identity.");
                        bindings.Add(legacyUid, nativeUid);
                        markers.Append(BindingPrefix).Append(legacyUid).Append('\t').Append(nativeUid).Append('\n');
                    }

                    string marker = replayPrefix + legacyUid + "\t1";
                    if (applied.Contains(marker)) continue;
                    pending.Add(legacyUid);
                    markers.AppendLine(marker);
                }

                foreach (var entry in history.Where(entry => entry.Disposition == "verified"))
                {
                    var record = entry.Record;
                    if (pending.Contains(record.LegacyUid!) && Merge(record, mappings[record.LegacyUid!]))
                        updated++;
                }

                // Make the inspectable backup durable before exposing any new active results.
                WriteArchive(archive, source, sourceHash, history, attestation);
                return markers.Length == 0 ? original : RaceRecords.Export() + markers;
            }
            finally
            {
                RaceRecords.Import(previous);
            }
        });
        return new(sourceHash, archive, updated, history);
    }

    private static string Classify(OriginalRecord record, IReadOnlyDictionary<string, VerifiedMapping> mappings)
    {
        if (record.Amount is null) return "invalid-value";
        if (record.Kind == "capture") return "name-only-capture";
        if (record.Kind == "speed" && record.Mode == RaceRecords.CtfRecord) return "unsupported-ctf-speed";
        if (!IdentityProofs.IsLegacyUid(record.LegacyUid)) return "missing-or-invalid-identity";
        return mappings.ContainsKey(record.LegacyUid!) ? "verified" : "unmatched-identity";
    }

    private static bool Merge(OriginalRecord record, VerifiedMapping mapping)
    {
        string uid = mapping.NativeUid;
        // Keep the active name when a player has already established one in native play.
        string name = RaceRecords.HasDisplayName(uid) ? "" : mapping.DisplayName;
        float amount = record.Amount!.Value;
        if (record.Kind == "speed")
        {
            if (amount <= RaceRecords.ReadSpeedAwardBest(record.Map, record.Mode)) return false;
            RaceRecords.WriteSpeedAwardBest(record.Map, record.Mode, amount, uid);
            RaceRecords.SetName(uid, name);
            return true;
        }

        float best = RaceRecords.ReadPersonalBest(record.Map, record.Mode, uid);
        if (best > 0f && best <= amount) return false;
        return RaceRecords.SetTime(record.Map, record.Mode, amount, uid, name).Kind != RaceRecordKind.Fail;
    }

    private static Dictionary<string, string> ReadBindings(string original)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var native = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(original);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith(BindingPrefix, StringComparison.Ordinal)) continue;
            string[] fields = line[BindingPrefix.Length..].Split('\t');
            if (fields.Length != 2 || !bindings.TryAdd(fields[0], fields[1]) || !native.Add(fields[1]))
                throw new FormatException("Conflicting identity metadata in the native record file.");
        }
        return bindings;
    }

    private static void WriteArchive(string path, byte[] source, string sourceHash, HistoricalRecord[] history,
        SignedAttestation? attestation)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["server.db"] = source,
            ["history.json"] = JsonSerializer.SerializeToUtf8Bytes(new { sourceSha256 = sourceHash, records = history }, IdentityProofs.Json),
        };
        if (attestation is not null)
        {
            files["attestation.json"] = attestation.Manifest;
            files["attestation.sig"] = attestation.Signature;
            files["authority.pem"] = Encoding.UTF8.GetBytes(attestation.TrustedAuthorityPem);
        }

        if (Directory.Exists(path))
        {
            try
            {
                RequireMatchingFile("server.db");
                RequireMatchingFile("history.json");
                if (attestation is not null)
                {
                    byte[] archivedManifest = RequireMatchingFile("attestation.json");
                    byte[] archivedSignature = File.ReadAllBytes(Path.Combine(path, "attestation.sig"));
                    string archivedAuthority = File.ReadAllText(Path.Combine(path, "authority.pem"));
                    IdentityProofs.VerifyArchivedSignature(archivedManifest, archivedSignature,
                        archivedAuthority, attestation.TrustedAuthorityPem);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or FormatException or CryptographicException)
            {
                throw new IOException($"Historical archive verification failed at '{path}'. {ex.Message}", ex);
            }
            return;
        }

        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(staging);
        else Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            foreach (var (name, bytes) in files)
            {
                using var file = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            Directory.Move(staging, path);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        byte[] RequireMatchingFile(string name)
        {
            byte[] archived = File.ReadAllBytes(Path.Combine(path, name));
            if (!archived.AsSpan().SequenceEqual(files[name]))
                throw new FormatException($"Archived {name} differs from this import.");
            return archived;
        }
    }
}
