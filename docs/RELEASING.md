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
| `VortexArena-<ver>-windows-x86_64.zip` | `VortexArena.exe` (+ console wrapper, `data_*` .NET folder), `data/` | double-click the `.exe` |
| `VortexArena-<ver>-linux-x86_64.zip` | `VortexArena.x86_64`, `run-client.sh`, `data/` | `./run-client.sh` |
| `VortexArena-<ver>-linux-dedicated-x86_64.zip` | `vortexarena-dedicated.x86_64`, `run-dedicated.sh`, `data/` | `./run-dedicated.sh [map]` |
| `VortexArena-<ver>-macos-universal.zip` | `VortexArena.app` (data inside `Contents/Resources/`) | double-click — see macOS note below |
| `SHA256SUMS-<ver>.txt` | checksums for the above | — |

The game finds its data **relative to the executable** (`DataPaths.Resolve`), so the zips work no
matter the working directory — a double-clicked binary, a file-manager launch, or a macOS `.app` (CWD `/`)
all resolve `data/` correctly. Keep the files together when you unzip.

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
#   "$GODOT" --headless --path . --export-release "linux-client" dist/linux-client/VortexArena.x86_64
tools/package.sh --version 0.1.0 linux-client           # lay out assets + zip → dist/VortexArena-0.1.0-linux-x86_64.zip
```

`tools/package.sh` with no target args packages every target whose export output exists under `dist/`.
On Windows, `run-release.ps1` exports + launches the windows-client preset directly.

## macOS note (best-effort)

The macOS target is **unverified** — its export config (codesign, bundle id) has never been run, so the
`macos` CI job is `continue-on-error` and a macOS failure never blocks the Windows/Linux release. The first
real test is the first release run; expect to iterate. The build is **unsigned**, so the first launch is
refused until the quarantine flag is cleared:

```bash
xattr -dr com.apple.quarantine VortexArena.app
```

If the universal (`x86_64`+`arm64`) .NET publish fails on CI, switch `binary_format/architecture` in the
`macos-client` preset to `arm64` (matches the Apple-Silicon `macos-latest` runner).

## Versioning

The zip names come from the tag (`v0.1.0` → `…-0.1.0-…`). Use [semver](https://semver.org) tags. Locally,
`package.sh` defaults the version to `git describe --tags --always --dirty` when `--version` is omitted.

## The artifact rename is an update-continuity break — read before the next tag

**Status: the rename has landed on `main` (2026-07-30); the cutover release has NOT been cut yet.**

Every shipped artifact changed name in the Tier-1 rename (restructure stage 5, items 34–35):

| was | is |
| --- | --- |
| `XonoticGodot-<ver>-windows-x86_64.zip` | `VortexArena-<ver>-windows-x86_64.zip` |
| `XonoticGodot.exe` / `.x86_64` / `.app` | `VortexArena.exe` / `.x86_64` / `.app` |
| `xonoticgodot-dedicated.x86_64` | `vortexarena-dedicated.x86_64` |

The launcher resolves updates through `latest.json` and installs by asset name, so **existing installs do
not follow the rename**. The last `XonoticGodot`-named release is the end of that update chain: a client
pinned to it will keep checking, find a manifest describing assets whose names it does not recognise, and
stop updating rather than fail loudly.

That makes the first `VortexArena`-named release a **deliberate one-way cutover**, not a routine tag. Before
pushing it:

1. Say so in the release notes, in the first line, with a plain "existing installs must reinstall once".
2. Keep the last `XonoticGodot`-named release available — it is what an un-migrated client still points at.
3. Check the note in [VortexLauncher's README](https://github.com/VortexFPS/VortexLauncher) still matches
   what actually shipped. The manifest shape is the only interface between the two repos, so a change on
   either side is a two-repo change.

Also worth knowing, since it is easy to trip over while doing releases: **do not publish a
non-game-build release in this repository.** GitHub resolves `releases/latest` to the newest *non-draft,
non-prerelease* release, and the launcher's primary feed is `/releases/latest/download/latest.json`.
Anything else that lands there — an engine template, a tooling artifact — becomes `latest`, that manifest
404s, and every client silently falls back to the unauthenticated GitHub API at 60 requests/hour. Mark such
releases `prerelease`, and script the flag rather than trusting a checkbox
([ADR-0017](../planning/decisions/ADR-0017-engine-patches.md)).
