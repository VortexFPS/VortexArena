using System.Security.Cryptography;
using System.Text.Json;
using VortexArena.Net;
using VortexArena.RecordMigration;

try
{
    switch (args)
    {
        case ["inspect", var sourcePath]:
        {
            byte[] source = File.ReadAllBytes(sourcePath);
            Console.WriteLine(JsonSerializer.Serialize(new { sourceSha256 = Migration.Hash(source), records = OriginalRecords.Read(source) },
                IdentityProofs.Json));
            return 0;
        }
        case ["consent", var profile, var sourceHash, var session, var trustedKey, var legacyUid, var displayName]:
        {
            using var identity = PlayerIdentityFile.Load(Path.Combine(profile, PlayerIdentityFile.FileName));
            var consent = IdentityProofs.SignConsent(identity, sourceHash, session, File.ReadAllText(trustedKey), legacyUid, displayName);
            Console.WriteLine(JsonSerializer.Serialize(consent, IdentityProofs.Json));
            return 0;
        }
        case ["apply", var sourcePath, var profile, var session, .. var proofPaths] when proofPaths.Length is 0 or 3:
        {
            var proof = proofPaths.Length == 0 ? null : new SignedAttestation(
                File.ReadAllBytes(proofPaths[0]), File.ReadAllBytes(proofPaths[1]), File.ReadAllText(proofPaths[2]));
            var result = Migration.Apply(File.ReadAllBytes(sourcePath), Path.GetFullPath(profile), session, proof);
            Console.WriteLine($"Active records updated: {result.Updated}");
            Console.WriteLine($"Historical archive: {result.ArchivePath}");
            return 0;
        }
        default:
            Console.WriteLine("""
                Usage:
                  inspect <server.db>
                  consent <client-profile> <source-sha256> <sessionid> <trusted-public.pem> <legacy-fingerprint> <display-name>
                  apply <server.db> <server-profile> <sessionid> [<attestation.json> <attestation.sig> <trusted-public.pem>]

                Pass "" for the default sessionid. The server profile directory must already exist.
                The consent command signs permission to track and display the named native identity for this migration.
                Applying without an attestation archives the history only.
                """);
            return args is [] or ["--help"] or ["-h"] ? 0 : 2;
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                           or CryptographicException or ArgumentException or JsonException)
{
    Console.Error.WriteLine($"Record migration failed: {ex.Message}");
    return 1;
}
