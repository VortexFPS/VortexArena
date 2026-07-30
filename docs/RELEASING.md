# Releasing Vortex Arena

How packaged builds are produced and published. Design rationale lives in
[ADR-0014](../planning/decisions/ADR-0014-ci-packaging-distribution.md).

## TL;DR — cut a release

```bash
git tag v0.1.0
git push origin v0.1.0
```

Pushing a `v*` tag runs [`.github/workflows/release.yml`](../.github/workflows/release.yml), which builds every
target on its native runner, bundles the Xonotic data, and **publishes a GitHub Release** with the zips
attached. They appear at `https://github.com/VortexFPS/VortexArena/releases` — that's the download page.

To shake out the build **without** publishing a release, run the workflow manually
(Actions → Release → *Run workflow*). That builds everything and uploads the zips as Actions artifacts, but
creates no Release.

## What ships

Each target is a **"fat" zip** — game binary + the Godot runtime + **all** Xonotic data in one download.
Unzip and play; nothing else to fetch.

| Zip | Contents | Run |
|---|---|---|
| `XonoticGodot-<ver>-windows-x86_64.zip` | `XonoticGodot.exe` (+ console wrapper, `data_*` .NET folder), `assets/data/` | double-click the `.exe` |
| `XonoticGodot-<ver>-linux-x86_64.zip` | `XonoticGodot.x86_64`, `run-client.sh`, `assets/data/` | `./run-client.sh` |
| `XonoticGodot-<ver>-linux-dedicated-x86_64.zip` | `xonoticgodot-dedicated.x86_64`, `run-dedicated.sh`, `assets/data/` | `./run-dedicated.sh [map]` |
| `XonoticGodot-<ver>-macos-universal.zip` | `XonoticGodot.app` (data inside `Contents/Resources/`) | double-click — see macOS note below |
| `SHA256SUMS-<ver>.txt` | checksums for the above | — |

The game finds its data **relative to the executable** (`DataPaths.Resolve`), so the zips work no
matter the working directory — a double-clicked binary, a file-manager launch, or a macOS `.app` (CWD `/`)
all resolve `assets/data` correctly. Keep the files together when you unzip.

## Pipeline shape

```
push tag v* ─┬─ (no assets job: core content is committed; maps fetched per data/maps.lock.json)
             ├─ windows  export windows-client                  ─┐
             ├─ linux    export linux-client + linux-dedicated  ─┤→ each: unpack assets, package.sh, upload zip
             ├─ macos    export macos-client (continue-on-error)─┘
             └─ release  collect zips, checksum, softprops/action-gh-release  (tag pushes only)
```

- Assets are downloaded **once** and fanned out as a single-file tar artifact — the three build jobs don't
  each pull ~1.5 GB from gitlab/dl.xonotic.org.
- Each platform exports on its **own OS** (no cross-export — ADR-0014 flags Linux→Windows as the flakiest).
- Godot + the .NET export templates are installed by [`chickensoft-games/setup-godot`](https://github.com/chickensoft-games/setup-godot).

## Build a zip locally

You need the Godot **4.6.3 mono** editor and its **export templates** installed
(editor → *Manage Export Templates* → *Download and Install*).

```bash
python tools/data/fetch-maps.py                         # one-time: fetch maps into data/maps/
ci/ci.sh --export                                       # export windows-client + linux-dedicated (your OS only)
# …or run a single preset:
#   "$GODOT" --headless --path . --export-release "linux-client" dist/linux-client/XonoticGodot.x86_64
tools/package.sh --version 0.1.0 linux-client           # lay out assets + zip → dist/XonoticGodot-0.1.0-linux-x86_64.zip
```

`tools/package.sh` with no target args packages every target whose export output exists under `dist/`.
On Windows, `run-release.ps1` exports + launches the windows-client preset directly.

## macOS note (best-effort)

The macOS target is **unverified** — its export config (codesign, bundle id) has never been run, so the
`macos` CI job is `continue-on-error` and a macOS failure never blocks the Windows/Linux release. The first
real test is the first release run; expect to iterate. The build is **unsigned**, so the first launch is
refused until the quarantine flag is cleared:

```bash
xattr -dr com.apple.quarantine XonoticGodot.app
```

If the universal (`x86_64`+`arm64`) .NET publish fails on CI, switch `binary_format/architecture` in the
`macos-client` preset to `arm64` (matches the Apple-Silicon `macos-latest` runner).

## Versioning

The zip names come from the tag (`v0.1.0` → `…-0.1.0-…`). Use [semver](https://semver.org) tags. Locally,
`package.sh` defaults the version to `git describe --tags --always --dirty` when `--version` is omitted.
