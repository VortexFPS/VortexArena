# Scripted multiplayer capture provenance

The [comparison page](scripted-comparisons.html) publishes seven pairs from two graphical clients per recording. The [evidence index](scripted-capture-evidence.json) binds each displayed path to its SHA-256. It records source WebPs and timestamps, with the tile order and ImageMagick settings for each contact sheet. Published Vampire evidence uses the [original Stormkeep pickup](scripted-sandbox-buffs/captures/original/buff.jpg) and the [native repeat with the restored carrier sign](scripted-sandbox-buffs/captures/relic-render-fixed/buff.jpg).

Run these commands from the checkout root with Python 3.11 or later.

The host restart cleared the raw stage directories under `/tmp`. The portable command passed again for all seven pairs after restart. The raw results below were recorded before the restart. Repeating them requires the original staged inputs or a preserved copy with the recorded directory layout.

```sh
python3 tools/check-parity-captures.py
python3 tools/check-parity-captures.py --raw /tmp --base /tmp/vortex-parity-base
```

The portable check reads the saved artifacts, compares each image to its published checksum and checks the paired fixture manifests, two client joins and sampling records. The raw check uses the stage directories recorded in each server command. It verifies the mounted original data checkout's Git pin and tracked status, then reads the BSP and the full external lightmap set from the staged PK3 for each arm. It also reads the three compiled QuakeC files through the recorded staging mounts and hashes the engine executables used by the server and clients. Commands, normalized client and server logs, and timelines are compared with the raw sessions. The objective exports put sample times in `frames.json` and keep only action records in `timeline.json`.

The raw check matched 14 mounted fixture packages, 42 compiled QC records, 21 engine binary records, and 28 client logs. It verified eight published WebPs directly against their source WebPs and sample times. The 38 source WebPs for the JPEG comparisons matched their recorded checksums and times. All six sheets rebuilt byte for byte with ImageMagick 6.9.12-98 Q16 and DejaVu Sans. The final Vampire JPEG also matched the separately saved `/tmp/va-relic-render5.jpg`. The original Sandbox read-only hold has two additional timed samples that sit outside its displayed four-row sheet. The index marks those samples with `"tile": false`.

Paths recorded under `/tmp` are relative to the supplied `--raw` root. A relocated archive needs the staged files and mount targets in their original relative locations and the `--base` path must point to the staged data Git checkout. The JPEG rebuild uses `montage` with the settings and ordered tiles in the index. The exact-binary comparison also depends on the captured DarkPlaces and Godot binaries remaining available.

The original QC file hashes identify the compiled bytes available through the staging links. Native DLL manifests describe the build files at record time. The raw check found matching build bytes for 21 of 45 native DLL records in the surviving temporary build directories. The other 24 remain unverified because the build address was reused, the original bytes could no longer be located there, or the project ran from a mutable checkout. A matching file today confirms byte availability. The logs and stage directories record what the runner launched, and the check provides no independent attestation of the process image at capture time.

Mismatch tests used disposable copies and a temporary raw archive overlay. The checker rejected an unpublished but valid Crylink WebP copied over the displayed WebP, a raw tile replaced with another frame, a changed raw frame timestamp, a removed guest join, changed bytes in a mounted Stormkeep lightmap, and a Git checkout at the wrong revision.
