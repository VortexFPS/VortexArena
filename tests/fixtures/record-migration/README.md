# Pinned DarkPlaces database fixtures

`input.db` contains synthetic fingerprints and names, an unrelated key that collides with a ranked key's bucket, and intentionally invalid or incomplete score rows. There were no player accounts or remote connections.

The original server running Xonotic data at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` loaded this input and wrote `bucketed.db` and `dumped.db`. The reference is `qcsrc/lib/map.qh` (`db_load`, `db_save`, `db_dump`), `qcsrc/server/race.qc` (rank and speed keys), `qcsrc/common/util.qh` (encoded times), and `qcsrc/common/gametypes/gametype/ctf/sv_ctf.qc` (ranked captures and the name-only fastest capture).

The fingerprint fixtures follow the original wire encoding. DarkPlaces passes a 44-byte length to `d0_blind_id_fingerprint64_public_id` in `crypto.c`. In the local reference binary, disassembly of that symbol and `d0_iobuf_conv_base64_out` confirms that the 32 SHA-256 bytes use the standard Base64 alphabet and trailing padding. The importer requires that canonical spelling and compares fingerprints case-sensitively.

To reproduce, place `input.db` in the original server's writable gamedir. Start a local reference dedicated server with a mounted map, and issue these commands after it loads.

```text
sv_cmd database load input.db
sv_cmd database save bucketed.db
sv_cmd database dump dumped.db
```

Verify the generated files against these SHA-256 hashes.

- `bucketed.db` `576815e3c90d9a61a7a61a4f535fb4333200c707cdbb1aeebe506ce455980c04`
- `dumped.db` `fc5a6853ccbb26eb70592ca32503e4d67a47b0f9c465af554a2abcd4166daf3e`
