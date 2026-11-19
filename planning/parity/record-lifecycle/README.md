# Native local record lifecycle

Reference checkout `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. Pinned `server/world.qc:867–879,2603–2652` loads `server.db` with the session suffix at world startup or restore. World shutdown saves it only if the world has a zero cheat count. Native opens the record file before accepting clients and saves it after the simulation worker has stopped. A clean rotation saves the outgoing world and reloads the incoming world. A cheated world keeps its changes in memory for that world and leaves the last clean file intact.

| Record | Pinned keys and gate |
| --- | --- |
| Race laps, CTS completions, CTF capture ladder | Map name, mode segment `/race100record/`, `/cts100record/` or `/ctf100record/`, then rank 1–99 and time plus owner fingerprint. Ranked times require an identity and both UID/name opt-ins. CTF also requires `g_ctf_leaderboard`, a non-neutral capture, and duration over 0.01 seconds. See `server/race.qc:69–141,373–427` and `sv_ctf.qc:113–141,2899–2903`. |
| Race and CTS speed best | Map and mode, then `speed/speed` and `speed/crypto_idfp`. A UID is sufficient for the speed result. Resolving a display name uses the opt-in UID/name mapping. The round-best speed remains match-local. See `server/race.qc:304–345`. |
| Separate CTF fastest capture | Map plus `/captimerecord/time` and `/captimerecord/netname`. A valid non-neutral capture can improve this record regardless of leaderboard enable or UID/name opt-in. The value drives the fastest-capture notification; it has no ranking slot. See `sv_ctf.qc:113–141,2899–2903`. |
| Holder aliases and deletion | Names are keyed by identity across maps and the three ladders. Opted-in players refresh the alias. Deleting a rank compacts that map and mode table. It leaves the alias, speed best, CTF fastest capture and records in other map/mode tables intact. See `server/race.qc:36–54,133–145,461–480`. |

Native ranked UID uses the existing verified profile fingerprint and cvar replication carries both consent values, with the default ask state withheld. The record file stores only local game data.

## Native verification

A disposable dedicated profile started with known native TSV entries for two Darkzone CTF ranks, the separate Darkzone fastest capture, and Stormkeep Race, CTS and speed records. On a dedicated server at UDP 28766, `delrec 1 darkzone` removed the first CTF rank. The same process rotated Darkzone → Stormkeep → Darkzone. After it exited gracefully, a new process opened the same profile. A separate temporary client joined and opened the death scoreboard. It showed the retained second capture promoted to first at **0:12.50**. The snapshots after rotation and after restart were byte-identical.

- [01 CTF ranking after restart](01-ctf-after-restart.webp)
- [Native startup, rotation and shutdown excerpt](native-run.txt)
- [Profile delta](profile-delta.txt)

The focused suite passed **73 tests**. It covers existing native rows, world reload, map/mode isolation, rank deletion and compaction, alias serialization, speed and fastest-capture retention, malformed-file preservation, cheated-world discard, and CTF consent across flag-touch cases. The live screenshot uses seeded data plus real server deletion, rotation, transport and rendering. Race and CTS durability were checked through the same file path and focused tests.

The gameplay follow-up earned Race, CTS and CTF records through a graphical client, then checked them after restarting the dedicated server. It includes consent cases and a pinned original comparison. [Gameplay-earned record evidence](earned/README.md)

## Native storage choices

- The writable profile owns `race-records.tsv` independently of config-save flags. A nonempty `sessionid` selects a separate filename using a deterministic SHA-256 suffix. It is sampled at world startup. The native TSV format retains older native tab-separated rows, with tagged escaped rows for control characters.
- A per-file lease permits one writable owner per profile partition. A failed lease or unreadable/malformed file disables writes for that world. Clean shutdown replaces the file via a staging file in the same directory. Abrupt termination retains the last clean snapshot.
- The native format and identity stay local under the ecosystem and legacy boundaries. The owner selected operator migration for verified identity mappings, preservation of the source database, and a separate historical list for unmatched records. The [operator guide](../../../docs/RECORD-MIGRATION.md) covers signed mappings, historical entries, replay and file preservation. The pinned opt-in gates control new ranked results. Rank deletion compacts that table and retains aliases. We found no opt-out erasure requirement in these paths. [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) · [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md)

The native profile accepts UTF-8 with an optional UTF-8 byte-order mark. The council identified two ways a clean shutdown could rewrite an unreadable profile. A malformed encoded field could acquire a replacement character, and a foreign byte-order mark could switch the reader to UTF-16. Regression tests verify byte-for-byte preservation in both cases and successful updates to a valid UTF-8 file with a preamble. Replacement preserves the existing nine Unix read, write and execute permission bits and leaves an adjacent `.tmp` file intact. The shell tears down the previous match synchronously and stops the server worker before closing the database. Each profile partition has one writable lease across host processes.
