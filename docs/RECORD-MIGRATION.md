# Migrate a Xonotic server's records

Use the offline migration tool from a Vortex Arena checkout. It reads the original server database and writes the native profile's existing ladder format. It can also archive a database before any player mappings have been verified.

## Inspect the source

Stop the original server cleanly or take a copy of a completed save. Use `server.db` for the default Xonotic session, or `server.db<sessionid>` if Xonotic used a custom session. Keep the input file as a backup.

```sh
tool=tools/race-record-migration/RaceRecordMigration.csproj
dotnet run --project "$tool" -- inspect /path/to/server.db
```

The JSON includes `sourceSha256`, map, mode, original rank, original owner fingerprint and the display name, when present. Ranked Race, CTS and CTF times are converted from hundredths of a second. Race and CTS speed awards use game units per second. CTF's separate fastest-capture time is already in seconds and carries only a nickname. It stays in history, even if the nickname appears on a ranked capture.

## Verify the mapping and collect consent

The operator supplies the trust root. Use one key controlled by the operator responsible for verifying the original identity, or use an existing trusted ECDSA signing key. Generate a key locally if needed.

```sh
umask 077
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out operator-private.pem
openssl pkey -in operator-private.pem -pubout -out operator-public.pem
```

For each player, observe a fresh authenticated connection on a trusted original server. In DarkPlaces, use the server's connection record with `client is <id>@<key>` and check that the identity has a valid signature under the expected original authority. A `~` before the authority key means the ID was unsigned. The server-side QuakeC fields are `crypto_idfp`, `crypto_idfp_signed` and `crypto_keyfp`. Copy the full 44-character, case-sensitive fingerprint, including its trailing `=`. Keep a private reference to the server, the evidence and when you collected it. Have the player demonstrate control of that original session, for example by returning a fresh operator challenge from the authenticated client in a supervised session. A nickname match, a screenshot, an unsigned ID and a fingerprint supplied by the player are insufficient evidence.

Give the verified player the source SHA-256, the target native `sessionid` and the trusted operator's public PEM. The player must be the same participant you verified in the original session. On their own machine, they run the following command to authorize identity tracking and display of their chosen nickname for this import. The game creates the local key on its first native connection attempt. Use the client profile where the player normally plays, which is `~/XonData` by default or the directory selected by `VORTEX_USERDIR`.

```sh
dotnet run --project "$tool" -- consent "$HOME/XonData" "$source_sha256" "" \
  operator-public.pem "$verified_legacy_fingerprint" '^2Chosen Nickname' > consent.json
```

The player returns only `consent.json`. Their `player-identity.pkcs8` is the private authentication key and stays in their profile. Pass `""` in both commands when the target native `sessionid` is empty.

After verifying the original session and its link to the player returning the consent, copy the consent object into an attestation. Set its `legacySigned` field to `true` and set `legacyAuthenticationEvidence` to the private evidence reference. Keep the other fields exactly as provided. Use this manifest format.

```json
{
  "version": 1,
  "sourceSha256": "<sourceSha256 from inspect>",
  "sessionId": "",
  "mappings": [
    {
      "legacyUid": "<verified original fingerprint>",
      "displayName": "^2Chosen Nickname",
      "nativePublicKey": "<base64 from consent.json>",
      "nativeSignature": "<base64 from consent.json>",
      "legacySigned": true,
      "legacyAuthenticationEvidence": "<private verification record reference>"
    }
  ]
}
```

Each original identity can map to one native identity and each native identity to one original identity per profile. Add additional objects for other verified players. The native signature binds the original fingerprint, selected nickname, source bytes, native session and operator public key. The attestation certifies your independent verification of the original DarkPlaces session and control of the identity. Sign the final JSON file byte for byte.

```sh
openssl dgst -sha256 -sign operator-private.pem -out attestation.sig attestation.json
```

## Apply to the native profile

Stop the native Vortex server and back up its profile. Use the same server profile directory and `sessionid` that the host uses. The default target is `race-records.tsv` in the profile. A named session uses the same SHA-256-based filename as the host. Supply the public key from your own trusted copy.

```sh
dotnet run --project "$tool" -- apply /path/to/server.db /path/to/native-profile "" \
  attestation.json attestation.sig operator-public.pem
```

Omit the attestation, signature and PEM together to archive the database with every result kept historical. Start the native server after a successful import. Its normal rankings and ladder commands read the merged records. Use `g_ctf_leaderboard 1` to enable the ranked CTF display.

The tool prints the archive directory under `record-migrations` in the target profile. Open `history.json` to inspect historical results and their eligibility. A `verified` row has an approved identity, while the normal native ranking limit and best-result rules determine whether it enters the active ladder. The archive includes a byte-for-byte copy of the original database and the proof material used for that batch. Keep it private because it can contain historical names and identity evidence.

The merge keeps a player's equal or faster native times, and keeps equal or higher native speed awards. Already established native display names take precedence. The tool shares the server's exclusive lease and replaces the ladder atomically after creating the archive. If the ladder update fails after archiving, the completed historical archive remains available for a retry. A retry verifies the archived manifest, signature and operator public key before updating the native ladder. A missing or invalid proof stops the retry and leaves the source and native ladder intact. Replaying an applied source and identity leaves the active ladder unchanged, including after a native record deletion. You can import additional eligible players from the same source with a new signed attestation.

## Repeatable verification

```sh
DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj \
  --filter 'FullyQualifiedName~RecordMigrationTests|FullyQualifiedName~RaceRecordsTests|FullyQualifiedName~SessionAuthTests'
```

The tests exercise the same file the host opens, using [synthetic database exports from the pinned QuakeC](../tests/fixtures/record-migration/README.md).
