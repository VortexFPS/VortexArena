using System.Security.Cryptography;
using System.Text.Json;
using VortexArena.Common.Gameplay;
using VortexArena.Net;
using VortexArena.RecordMigration;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class RecordMigrationTests : IDisposable
{
    private const string RaceMap = "stormkeep";
    private const string OtherMap = "leave_em_behind";
    private const string Session = "test ladder";
    private static readonly string LegacyA = new string('A', 41) + "+A=";
    private static readonly string LegacyB = new string('B', 41) + "/A=";
    private static readonly string LegacyC = new string('C', 42) + "A=";
    private static readonly string LegacyD = new string('D', 42) + "A=";
    private static readonly byte[] Source = Fixture("bucketed");
    private static readonly string SourceHash = Migration.Hash(Source);
    private readonly string _profile = Directory.CreateTempSubdirectory("record-migration-").FullName;
    private readonly ECDsa _authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly PlayerIdentity _player = PlayerIdentity.Generate();

    private string Ladder => Path.Combine(_profile, RaceRecordFile.ProfileFileName(Session));
    private string Authority => _authority.ExportSubjectPublicKeyInfoPem();

    [Fact]
    public void ReadsPinnedBucketRowsAndDumpWithIdenticalRecords()
    {
        Assert.Equal("576815e3c90d9a61a7a61a4f535fb4333200c707cdbb1aeebe506ce455980c04", SourceHash);
        var bucketed = OriginalRecords.Read(Source);
        Assert.Equal(bucketed, OriginalRecords.Read(Fixture("dumped")));
        Assert.Contains(bucketed, r => r is { Map: RaceMap, Rank: 1, Amount: 12.34f, OriginalName: "^2Café\\+%" }
                                     && r.LegacyUid == LegacyA);
        Assert.Contains(bucketed, r => r is { Kind: "capture", Amount: 3.046875f, OriginalName: "Same Alias", LegacyUid: null });
        Assert.Contains(bucketed, r => r is { Kind: "speed", Amount: 950.25f });
        Assert.Contains(bucketed, r => r is { Rank: 2, OriginalName: "^3Historical" } && r.LegacyUid == LegacyB);
        Assert.Contains(bucketed, r => r is { Rank: 4, OriginalName: null } && r.LegacyUid == LegacyD);
        Assert.Contains(bucketed, r => r is { Rank: 5, LegacyUid: null });
        Assert.Contains(bucketed, r => r is { Rank: 6, Value: "broken", Amount: null });
        Assert.Empty(OriginalRecords.Read("8192\n"u8.ToArray()));
    }

    [Fact]
    public void ArchiveOnlyLeavesAllRecordsHistoricalAndPreservesOriginalBytes()
    {
        string originalPath = Path.Combine(_profile, "server.db");
        File.WriteAllBytes(originalPath, Source);
        var result = Migration.Apply(File.ReadAllBytes(originalPath), _profile, Session, null);

        Assert.Equal(0, result.Updated);
        Assert.All(result.Records, entry => Assert.NotEqual("verified", entry.Disposition));
        Assert.Equal(Source, File.ReadAllBytes(originalPath));
        Assert.Equal(Source, File.ReadAllBytes(Path.Combine(result.ArchivePath, "server.db")));
        Assert.False(File.Exists(Ladder));
        using var historical = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.ArchivePath, "history.json")));
        Assert.Equal(12, historical.RootElement.GetProperty("records").GetArrayLength());
    }

    [Theory]
    [InlineData("Native Alias")]
    [InlineData("^1Unregistered Player")]
    public void VerifiedBestTimesAndSpeedMergeIntoNativeFileTheWorldLoads(string nativeAlias)
    {
        using var playerWithNoOldName = PlayerIdentity.Generate();
        var native = RaceRecordFile.Open(Ladder);
        RaceRecords.SetTime(RaceMap, RaceRecords.RaceRecord, 10f, "native-faster", "Native Faster");
        RaceRecords.SetTime(RaceMap, RaceRecords.RaceRecord, 13f, _player.Fingerprint, nativeAlias);
        RaceRecords.SetTime(OtherMap, RaceRecords.CtsRecord, 10f, _player.Fingerprint, nativeAlias);
        RaceRecords.SetTime(OtherMap, RaceRecords.CtfRecord, 7f, _player.Fingerprint, nativeAlias);
        RaceRecords.WriteSpeedAwardBest(OtherMap, RaceRecords.CtsRecord, 900f, "native-speed");
        RaceRecords.WriteCaptureBest(OtherMap, 3.5f, "Native Capture");
        native.Close(cheated: false);

        var proof = Attest(Mapping(_player, LegacyA), Mapping(playerWithNoOldName, LegacyD));
        var result = Migration.Apply(Source, _profile, Session, proof);
        Assert.Equal(4, result.Updated);
        Assert.Equal(Source, File.ReadAllBytes(Path.Combine(result.ArchivePath, "server.db")));
        Assert.Contains(result.Records, row => row.Disposition == "name-only-capture");
        Assert.Contains(result.Records, row => row.Disposition == "invalid-value");
        Assert.Contains(result.Records, row => row.Disposition == "unsupported-ctf-speed");
        Assert.Contains(result.Records, row => row.Disposition == "missing-or-invalid-identity");
        Assert.Contains(result.Records, row => row is { Disposition: "unmatched-identity", Record.OriginalName: "Same Alias" });

        native = RaceRecordFile.Open(Ladder);
        try
        {
            Assert.Equal(10f, RaceRecords.ReadTime(RaceMap, RaceRecords.RaceRecord, 1));
            Assert.Equal(12.34f, RaceRecords.ReadTime(RaceMap, RaceRecords.RaceRecord, 2));
            Assert.Equal(_player.Fingerprint, RaceRecords.ReadUid(RaceMap, RaceRecords.RaceRecord, 2));
            Assert.Equal(nativeAlias, RaceRecords.ReadName(RaceMap, RaceRecords.RaceRecord, 2));
            Assert.Equal(14.99f, RaceRecords.ReadTime(RaceMap, RaceRecords.RaceRecord, 3));
            Assert.Equal(playerWithNoOldName.Fingerprint, RaceRecords.ReadUid(RaceMap, RaceRecords.RaceRecord, 3));
            Assert.Equal("Same Alias", RaceRecords.ReadName(RaceMap, RaceRecords.RaceRecord, 3));
            Assert.Equal(0f, RaceRecords.ReadTime(RaceMap, RaceRecords.RaceRecord, 4));
            Assert.Equal(10f, RaceRecords.ReadPersonalBest(OtherMap, RaceRecords.CtsRecord, _player.Fingerprint));
            Assert.Equal(5.23f, RaceRecords.ReadPersonalBest(OtherMap, RaceRecords.CtfRecord, _player.Fingerprint));
            Assert.Equal(950.25f, RaceRecords.ReadSpeedAwardBest(RaceMap, RaceRecords.RaceRecord));
            Assert.Equal(_player.Fingerprint, RaceRecords.ReadSpeedAwardBestUid(RaceMap, RaceRecords.RaceRecord));
            Assert.Equal(900f, RaceRecords.ReadSpeedAwardBest(OtherMap, RaceRecords.CtsRecord));
            Assert.Equal(0f, RaceRecords.ReadSpeedAwardBest(OtherMap, RaceRecords.CtfRecord));
            Assert.Equal((3.5f, "Native Capture"), RaceRecords.ReadCaptureBest(OtherMap));
        }
        finally { native.Close(cheated: true); }
    }

    [Fact]
    public void FullNativeTableKeepsFasterEntriesAndSpeedTieKeepsIncumbent()
    {
        var native = RaceRecordFile.Open(Ladder);
        for (int i = 1; i <= RaceRecords.RankingsCnt; i++)
            RaceRecords.SetTime(RaceMap, RaceRecords.RaceRecord, i / 10f, $"native-{i}", "");
        RaceRecords.WriteSpeedAwardBest(RaceMap, RaceRecords.RaceRecord, 950.25f, "native-speed");
        native.Close(cheated: false);

        var result = Migration.Apply(Source, _profile, Session, Attest(Mapping(_player, LegacyA)));
        Assert.Equal(3, result.Updated); // CTS time and speed, plus CTF rank.

        native = RaceRecordFile.Open(Ladder);
        try
        {
            Assert.Equal(0f, RaceRecords.ReadPersonalBest(RaceMap, RaceRecords.RaceRecord, _player.Fingerprint));
            Assert.Equal("native-99", RaceRecords.ReadUid(RaceMap, RaceRecords.RaceRecord, 99));
            Assert.Equal("native-speed", RaceRecords.ReadSpeedAwardBestUid(RaceMap, RaceRecords.RaceRecord));
        }
        finally { native.Close(cheated: true); }
    }

    [Theory]
    [InlineData("unsigned-legacy")]
    [InlineData("unsigned-consent")]
    [InlineData("substituted-native-key")]
    [InlineData("changed-consented-name")]
    [InlineData("changed-legacy-uid")]
    [InlineData("wrong-authority")]
    [InlineData("changed-manifest")]
    [InlineData("wrong-database")]
    [InlineData("wrong-session")]
    [InlineData("missing-evidence")]
    public void InvalidOrOutOfScopeProofLeavesNativeAndArchiveUntouched(string scenario)
    {
        byte[] original = "R\tkeep/race100record/time1\t5\n"u8.ToArray();
        File.WriteAllBytes(Ladder, original);
        using var otherPlayer = PlayerIdentity.Generate();
        using var otherAuthority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var mapping = Mapping(_player, LegacyA);
        mapping = scenario switch
        {
            "unsigned-legacy" => mapping with { LegacySigned = false },
            "unsigned-consent" => mapping with { NativeSignature = [] },
            "substituted-native-key" => mapping with { NativePublicKey = otherPlayer.PublicKey },
            "changed-consented-name" => mapping with { DisplayName = "Different Alias" },
            "changed-legacy-uid" => mapping with { LegacyUid = LegacyC },
            "missing-evidence" => mapping with { LegacyAuthenticationEvidence = "" },
            _ => mapping,
        };
        var proof = Attest(mapping);
        proof = scenario switch
        {
            "wrong-authority" => proof with { TrustedAuthorityPem = otherAuthority.ExportSubjectPublicKeyInfoPem() },
            "changed-manifest" => proof with { Manifest = [.. proof.Manifest, (byte)' '] },
            _ => proof,
        };
        byte[] source = scenario == "wrong-database" ? Fixture("dumped") : Source;
        string session = scenario == "wrong-session" ? "" : Session;
        Assert.Throws<FormatException>(() => Migration.Apply(source, _profile, session, proof));
        Assert.Equal(original, File.ReadAllBytes(Ladder));
        Assert.False(Directory.Exists(Path.Combine(_profile, "record-migrations")));
    }

    [Fact]
    public void RejectsConflictingClaimsWithinAndAcrossBatches()
    {
        using var other = PlayerIdentity.Generate();
        var a = Mapping(_player, LegacyA);
        Assert.Throws<FormatException>(() => Migration.Apply(Source, _profile, Session, Attest(a, Mapping(other, LegacyA))));
        Assert.Throws<FormatException>(() => Migration.Apply(Source, _profile, Session, Attest(a, Mapping(_player, LegacyB))));
        Migration.Apply(Source, _profile, Session, Attest(a));
        byte[] before = File.ReadAllBytes(Ladder);
        Assert.Throws<FormatException>(() => Migration.Apply(Source, _profile, Session, Attest(Mapping(other, LegacyA))));
        Assert.Throws<FormatException>(() => Migration.Apply(Source, _profile, Session, Attest(Mapping(_player, LegacyB))));
        Assert.Equal(before, File.ReadAllBytes(Ladder));
    }

    [Fact]
    public void ReplayRetainsNativeDeletionAndAdditionalVerifiedOwnersCanBeImported()
    {
        using var other = PlayerIdentity.Generate();
        var a = Mapping(_player, LegacyA);
        Migration.Apply(Source, _profile, Session, Attest(a));
        byte[] beforeReplay = File.ReadAllBytes(Ladder);
        Assert.Equal(0, Migration.Apply(Source, _profile, Session, Attest(a)).Updated);
        Assert.Equal(beforeReplay, File.ReadAllBytes(Ladder));

        var native = RaceRecordFile.Open(Ladder);
        RaceRecords.DeleteTime(RaceMap, RaceRecords.RaceRecord, 1);
        native.Close(cheated: false);

        Assert.Equal(1, Migration.Apply(Source, _profile, Session, Attest(a, Mapping(other, LegacyB))).Updated);
        native = RaceRecordFile.Open(Ladder);
        try
        {
            Assert.Equal(other.Fingerprint, RaceRecords.ReadUid(RaceMap, RaceRecords.RaceRecord, 1));
            Assert.Equal(0f, RaceRecords.ReadPersonalBest(RaceMap, RaceRecords.RaceRecord, _player.Fingerprint));
        }
        finally { native.Close(cheated: true); }
    }

    [Theory]
    [InlineData("missing-signature", true)]
    [InlineData("invalid-signature", true)]
    [InlineData("missing-key", true)]
    [InlineData("invalid-key", true)]
    [InlineData("replaced-key-and-signature", true)]
    [InlineData("missing-signature", false)]
    [InlineData("invalid-signature", false)]
    [InlineData("missing-key", false)]
    [InlineData("invalid-key", false)]
    [InlineData("replaced-key-and-signature", false)]
    public void ArchiveProofDamageAbortsReplayOrCommitRetry(string damage, bool committed)
    {
        string sourcePath = Path.Combine(_profile, "original.db");
        File.WriteAllBytes(sourcePath, Source);
        byte[] original = "R\tkeep/race100record/time1\t5\n"u8.ToArray();
        File.WriteAllBytes(Ladder, original);
        var proof = Attest(Mapping(_player, LegacyA));
        var result = Migration.Apply(File.ReadAllBytes(sourcePath), _profile, Session, proof);
        if (!committed)
            File.WriteAllBytes(Ladder, original); // Archive was committed, but the native replacement did not happen.

        byte[] before = File.ReadAllBytes(Ladder);
        string signaturePath = Path.Combine(result.ArchivePath, "attestation.sig");
        string keyPath = Path.Combine(result.ArchivePath, "authority.pem");
        switch (damage)
        {
            case "missing-signature":
                File.Delete(signaturePath);
                break;
            case "invalid-signature":
                File.WriteAllBytes(signaturePath, "corrupted signature"u8.ToArray());
                break;
            case "missing-key":
                File.Delete(keyPath);
                break;
            case "invalid-key":
                File.WriteAllText(keyPath, "corrupted public key");
                break;
            case "replaced-key-and-signature":
                using (var otherAuthority = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    File.WriteAllText(keyPath, otherAuthority.ExportSubjectPublicKeyInfoPem());
                    File.WriteAllBytes(signaturePath, otherAuthority.SignData(proof.Manifest, HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence));
                }
                break;
        }

        var error = Assert.Throws<IOException>(() => Migration.Apply(File.ReadAllBytes(sourcePath), _profile, Session, proof));
        Assert.Contains("Historical archive verification failed", error.Message);
        Assert.Equal(before, File.ReadAllBytes(Ladder));
        Assert.Equal(Source, File.ReadAllBytes(sourcePath));
        Assert.Equal(Source, File.ReadAllBytes(Path.Combine(result.ArchivePath, "server.db")));
    }

    [Fact]
    public void ArchiveCanPrecedeCommitAndReplayAcceptsEquivalentProofFiles()
    {
        byte[] original = "R\tkeep/race100record/time1\t5\n"u8.ToArray();
        File.WriteAllBytes(Ladder, original);
        var proof = Attest(Mapping(_player, LegacyA));
        var first = Migration.Apply(Source, _profile, Session, proof);
        byte[] expected = File.ReadAllBytes(Ladder);
        File.WriteAllBytes(Ladder, original); // Simulate interruption immediately before the native replacement.

        byte[] equivalentSignature = _authority.SignData(proof.Manifest, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        string equivalentPem = "\r\n" + Authority.Replace("\n", "\r\n") + "\r\n";
        File.WriteAllBytes(Path.Combine(first.ArchivePath, "attestation.sig"), equivalentSignature);
        File.WriteAllText(Path.Combine(first.ArchivePath, "authority.pem"), equivalentPem);

        var retry = Migration.Apply(Source, _profile, Session, proof);
        Assert.Equal(first.Updated, retry.Updated);
        Assert.Equal(first.ArchivePath, retry.ArchivePath);
        Assert.Equal(expected, File.ReadAllBytes(Ladder));
        Assert.Equal(0, Migration.Apply(Source, _profile, Session, proof).Updated);
        Assert.Equal(expected, File.ReadAllBytes(Ladder));
    }

    [Fact]
    public void LegacyFingerprintMustHaveTheCanonicalWireEncodingAndMatchExactly()
    {
        Assert.Throws<FormatException>(() => Mapping(_player, LegacyA[..^1]));
        Assert.Throws<FormatException>(() => Mapping(_player, LegacyA[..^2] + "B=")); // Nonzero Base64 pad bits.
        Assert.Throws<FormatException>(() => Mapping(_player, LegacyA.Replace('+', '-')));
        Assert.Throws<FormatException>(() => Mapping(_player, " " + LegacyA[1..]));

        string differentFingerprint = "a" + LegacyA[1..];
        var result = Migration.Apply(Source, _profile, Session, Attest(Mapping(_player, differentFingerprint)));
        Assert.Equal(0, result.Updated);
        Assert.False(File.Exists(Ladder));
        Assert.Contains(result.Records, row => row.Record.LegacyUid == LegacyA && row.Disposition == "unmatched-identity");
    }

    [Fact]
    public void LockedProfileMalformedFilesAndArchiveWriteFailureDoNotChangeActiveRecords()
    {
        RaceRecordFile native = RaceRecordFile.Open(Ladder);
        try { Assert.Throws<IOException>(() => Migration.Apply(Source, _profile, Session, Attest(Mapping(_player, LegacyA)))); }
        finally { native.Close(cheated: true); }

        var proof = Attest(Mapping(_player, LegacyA));
        Assert.Throws<FormatException>(() => Migration.Apply("0\n\\orphan\n"u8.ToArray(), _profile, Session, proof));
        Assert.Throws<FormatException>(() => Migration.Apply(Source[..^1], _profile, Session, null));
        Assert.Throws<FormatException>(() => Migration.Apply("8192\n\\somekey\\wrong bucket\n"u8.ToArray(), _profile, Session, null));

        const string malformed = "unexpected native data\n";
        File.WriteAllText(Ladder, malformed);
        Assert.Throws<FormatException>(() => Migration.Apply(Source, _profile, Session, proof));
        Assert.Equal(malformed, File.ReadAllText(Ladder));

        const string existing = "R\tkeep/race100record/time1\t5\n";
        File.WriteAllText(Ladder, existing);
        File.WriteAllText(Path.Combine(_profile, "record-migrations"), "blocked");
        Assert.ThrowsAny<IOException>(() => Migration.Apply(Source, _profile, Session, proof));
        Assert.Equal(existing, File.ReadAllText(Ladder));
        Assert.Empty(Directory.EnumerateFiles(_profile, "*.tmp"));
    }

    [Fact]
    public void NativeProfileKeepsOneKeyAcrossLoadsAndCorruptKeyIsNeverReplaced()
    {
        string path = Path.Combine(_profile, PlayerIdentityFile.FileName);
        using var first = PlayerIdentityFile.LoadOrCreate(path);
        using var again = PlayerIdentityFile.LoadOrCreate(path);
        Assert.Equal(first.Fingerprint, again.Fingerprint);
        Assert.Equal(first.Fingerprint, PlayerIdentity.ComputeFingerprint(Mapping(again, LegacyA).NativePublicKey));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        File.WriteAllText(path, "corrupt");
        Assert.Throws<CryptographicException>(() => PlayerIdentityFile.LoadOrCreate(path));
        Assert.Equal("corrupt", File.ReadAllText(path));
    }

    private IdentityMapping Mapping(PlayerIdentity identity, string legacyUid) =>
        IdentityProofs.SignConsent(identity, SourceHash, Session, Authority, legacyUid, "Same Alias") with
        {
            LegacySigned = true,
            LegacyAuthenticationEvidence = "Synthetic pinned-session evidence #42",
        };

    private SignedAttestation Attest(params IdentityMapping[] mappings)
    {
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new MigrationManifest(1, SourceHash, Session, mappings),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new(manifest, _authority.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), Authority);
    }

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "RecordMigrationFixtures", name + ".db"));

    public void Dispose()
    {
        _player.Dispose();
        _authority.Dispose();
        RaceRecords.Clear();
        Directory.Delete(_profile, recursive: true);
    }
}
