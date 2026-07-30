# ADR-0014 — CI, perf baselines, packaging & dedicated-server distribution

**Status:** Accepted (T33, 2026-06)

## Context

The port had no CI, no export presets, no packaging story, and no recorded performance baselines.
Upstream's distribution is the reference shape: exactly **two binaries sharing one data dir**
(`Base/Makefile:3-4` — `CLIENTBIN=xonotic-sdl`, `SERVERBIN=xonotic-dedicated`), launched by shell
scripts that **`cd` to their own directory first** (`Base/xonotic-linux-sdl.sh`; the dedicated
script is a symlink that derives its mode from `$0`), updated via **rsync, not an installer**
(`Base/Makefile:67-73`). The dedicated server is the **same host loop with the client gated off at
boot** (`Base/darkplaces/host.c:437` — `cls.state = ca_dedicated`). There is no QuakeC source for
any of this (../TODO.md T33 notes) — CI/packaging design is constrained only by the port's own build
reality, plus ADR-0012 (desktop clients + headless dedicated server; web/mobile deferred).

Build reality: the test suite + all `src/` libraries are plain `Microsoft.NET.Sdk` (no Godot);
only `XonoticGodot.csproj` needs `Godot.NET.Sdk/4.6.3`. `nuget.config` adds the Windows editor's
bundled nupkgs folder as a source — but **Godot.NET.Sdk / GodotSharp / Godot.SourceGenerators /
GodotSharpEditor 4.6.3 are all published on nuget.org** (verified live 2026-06 via the
flat-container API), so the older "the stable SDK isn't on public NuGet" comments were stale.

## Decision

### 1. CI topology (`.github/workflows/ci.yml` + `ci/ci.sh` mirror)

| Job | When | What |
|---|---|---|
| `test` | every push/PR to `main` | restore + `dotnet test` the suite on ubuntu-latest, upload trx |
| `build-host` | every push/PR | `dotnet build XonoticGodot.csproj` — proves the Godot host compiles from a clean clone with **no Godot editor installed**, restoring the SDK from nuget.org |
| `export` | `workflow_dispatch` / `v*` tags only, `continue-on-error` | headless Godot 4.6.3 export of both presets, artifacts uploaded |

- **The nuget.config hazard:** the `godot-editor` local source points at a Windows path; on a Linux
  runner NuGet hard-fails on a missing local source, and the MSBuild resolver for
  `Sdk="Godot.NET.Sdk/4.6.3"` reads the same config. Every CI job therefore runs
  `dotnet nuget remove source godot-editor --configfile nuget.config` **before any dotnet command**.
  The source stays in the repo for dev machines (exact editor parity + offline). Keep the key name
  stable — CI targets it by name.
- **No assets in CI.** The asset tree is ~960 MB + multi-GB git clones; the ~18 real-data test
  classes already self-skip when `assets/data` is missing. **CI green therefore proves less than a
  local run** — `ci/ci.sh` (which also runs the headless boot smoke from ../../docs/RUNNING.md, and the
  real-data tests when assets are present) is the authoritative pre-push gate. A cache-warmed
  nightly real-data job is a deferred follow-up.
- The export job is `continue-on-error` until it has actually succeeded once: the export path has
  **never been exercised anywhere** (no templates were installed on the dev machine when this
  landed), the mono export-templates tpz is ~1 GB (cached by version key), and the Windows C#
  cross-export from Linux (Godot driving `dotnet publish -r win-x64`) is unproven.

### 2. Packaging (`export_presets.cfg`, `tools/package.sh`, `tools/run-dedicated.sh`)

Two presets mirror the upstream two-binary topology:

- **`windows-client`** (Windows Desktop, x86_64, embedded pck) → `dist/windows-client/XonoticGodot.exe`
- **`linux-dedicated`** (Linux, x86_64, `dedicated_server=true`) → `dist/linux-dedicated/xonoticgodot-dedicated.x86_64`.
  Note: Godot serializes the "Export as dedicated server" mode as the top-level **boolean**
  `dedicated_server`, *not* an `export_filter` value (verified against the 4.6.3 editor binary's
  serialization table: `export_filter ∈ all_resources/scenes/resources/exclude/customized`). The
  mode strips visual resources and applies the `dedicated_server` feature tag ADR-0012 names.

**The assets-beside-binary CWD contract:** game data is *not* packed into the pck (the presets
exclude `assets/*`; the in-repo tree is `.gdignore`d anyway). In an exported build
`ProjectSettings.GlobalizePath("res://")` returns `""`, so `DataPaths.Resolve` resolves the
default `res://assets/data` to the **CWD-relative** `assets/data`. Packaging therefore lays
`assets/data/` beside each binary, and `run-dedicated.sh` `cd`s to its own directory before
exec'ing — exactly the upstream launcher shape. (`tools/package.sh` assembles dist dirs + zips;
it reuses `download-assets.sh`, excluding the pk3dir `.git` clones.) The known sharp edge — a user
launching the exe from a different CWD gets a silent blank world — is mitigated by the run script
and documented; the durable fix is a `--data <path>` CLI flag in `Main.cs` (a one-line seam into
the existing `Shell.DataPath`), deferred to the runtime owner.

### 3. Dedicated server: v1 = headless listen server

`--headless --host <map>` is the v1 dedicated story: the same host loop with a dummy renderer and
a local self-connected client (`Shell.StartHost` → `NetGame`). A true client-less host — the
analogue of `host.c:437`'s `ca_dedicated`, instantiating only the ServerNet/GameWorld half — is a
**deferred Shell/NetGame seam** (those files belong to other tasks' charters this wave). The
`linux-dedicated` preset is still correct for v1 (the headless listen server never touches the
stripped visual resources).

### 4. Performance baselines (measurement-first; no code changes)

The named hot paths were **already pooled** (snapshot ring dicts, the ServerNet writer/scratch,
the TraceService candidates list), so the perf pass produced *benches + baselines*, not forced
micro-optimizations. Three new bench-as-test harnesses live in `tests/XonoticGodot.Tests/Perf/`
(BotPerfBench pattern: `Stopwatch` + `GC.GetAllocatedBytesForCurrentThread`, skip-without-assets,
`VA_DATA_DIR` override; baselines recorded as comments in each file):

- **NetSnapshotPerfBench** — 16 clients × 256 entities × 72 Hz: encode 0.115 ms/client-tick,
  ~1 KB/snapshot steady-state, 272 B/client-tick allocated (boxed `IReadOnlyDictionary`
  enumerators in `EncodeSnapshot` — reported to the Net owner).
- **TracePerfBench** — atelier: 0.043 ms (len-32 hull sweep) → 2.06 ms (len-2048); **424 B/trace**
  from the per-call `Brush.FromBox` in `TraceService.Trace` (reported); map load = 22 ms parse +
  219 ms collision build.
- **ServerTickPerfBench** — a booted `GameWorld` on atelier: 0.118 ms/tick empty / 0.622 ms/tick
  with 4 running players (4.5 % of the 72 Hz budget) — but **~32 KB/tick allocated even idle**
  (~2.3 MB/s gen0 churn), the top dedicated-server GC target (reported to the GameWorld owner).

GC-mode flips (`ServerGarbageCollection` etc.) are **propose-only** pending `dotnet-counters`
evidence from a live process (procedure documented in ../../docs/RUNNING.md).

## Cut list (deferred, with rationale)

- **Installers (MSI/NSIS/deb/flatpak):** zip artifacts suffice pre-1.0; upstream ships zips too.
- **Auto-update:** upstream's channel is rsync (`Base/Makefile:67-73`); no release channel exists yet.
- **macOS export:** sanctioned by ADR-0012 but gated on signing/notarization — revisit at release.
- **Code signing (all platforms):** same gate; presets carry no credentials (those live in Godot's
  separate `export_credentials.cfg`, which stays untracked).
- **Steam/itch distribution:** out of scope.
- **Per-run CI asset download:** ~960 MB + multi-GB clones per run; real-data tests skip by design.
  A cache-warmed nightly real-data job is the follow-up shape.
- **BenchmarkDotNet:** rejected — the zero-dependency bench-as-test pattern (BotPerfBench) already
  proved out, runs under plain `dotnet test`, and needs no Release-build orchestration.
- **True client-less dedicated host:** deferred Shell/NetGame seam (see §3).

## Consequences

- A clean clone on any OS builds + tests with zero Godot installed; the Godot host build is gated
  in CI on nuget.org alone.
- `export_presets.cfg` is now tracked (the `.gitignore` rule was removed) — safe because desktop
  presets hold no secrets.
- The first real Actions run (after push) and the first `workflow_dispatch` export run are the
  remaining end-to-end proofs; expect a debug iteration on the export job.

## Update (2026-06-10) — full release matrix + GitHub Releases

The on-demand `export` job (CI artifacts only, binary-only) is superseded by a dedicated
[`release.yml`](../../.github/workflows/release.yml) that builds the **full ADR-0012 client matrix** and
**publishes downloadable zips to GitHub Releases** on `v*` tags. Operator guide: [RELEASING.md](../../docs/RELEASING.md).

- **Targets (4):** added `linux-client` (preset.2) and `macos-client` (preset.3) alongside the original
  `windows-client` + `linux-dedicated`. The Linux desktop client was the gap — Linux was server-only.
- **Distribution = fat per-platform zips** (decided with the runtime owner): binary + Godot runtime + ALL
  Xonotic data in one download. Simpler for players than a shared data pack; each zip is ~1.5 GB, under
  GitHub's 2 GB/file cap. The assets are downloaded **once** per release (an `assets` job → single-file tar
  artifact, cached on the `download-assets.sh` hash) and fanned out to the build jobs.
- **Native per-runner export** (windows-latest / ubuntu-latest / macos-latest) — the cross-export the
  original ADR flagged as unproven is avoided entirely. Godot + .NET export templates via
  `chickensoft-games/setup-godot@v2`; release upload via `softprops/action-gh-release@v2`
  (`permissions: contents: write`). `ci.yml` keeps only the lean per-push gate (test + build-host).
- **The assets-beside-binary contract is now executable-relative, not CWD-relative.**
  `DataPaths.Resolve`, in an exported build (`GlobalizePath("res://") == ""`), resolves `assets/data`
  against `OS.GetExecutablePath()`'s directory, probing `<exe-dir>/assets/data` then the macOS
  `<exe-dir>/../Resources/assets/data`. This closes the "launched from the wrong CWD → silent blank world"
  sharp edge for ALL platforms (the deferred fix from §2) and is what makes a double-clicked macOS `.app`
  work. The `--data <path>` flag (also §2) is wired in `Main.cs` as the explicit override.
- **macOS is best-effort / unverified:** the `macos` job is `continue-on-error` (codesign/bundle-id config
  has never run), the build is unsigned (quarantine note in the per-zip README), and `architecture` is
  `universal` with an `arm64` fallback documented. A macOS failure never blocks the Win/Linux release.
- **Still cut:** installers, auto-update, code signing/notarization (the macOS unsigned caveat stands),
  Steam/itch — unchanged from the original cut list.

---

## Amendment — 2026-07-30 (repo restructure)

Two statements in this ADR are no longer true, and one was never a good idea.

- **`download-assets.sh` is deleted.** It is no longer the single source of asset truth, because there is
  no build-time fetch of core content: it is committed under `data/` and arrives with the clone. See
  [ADR-0016](ADR-0016-content-ownership.md). The `assets` job in `release.yml` — download once, tar,
  fan out to the build jobs — is gone with it.
- **The CI cache key moved from `hashFiles('download-assets.sh')` to `hashFiles('data/maps.lock.json')`,**
  which is strictly better and not merely different: the key now invalidates when the **map set** changes
  rather than when the fetching *code* changes. The old key would miss a content change that did not touch
  the script, and churn on a comment edit that changed nothing.
- **`assets/data` is gone**, along with the dev-box junction that pointed it at a pristine upstream
  checkout. The content path is `res://data`, and `tools/package.sh` lays `data/` beside the binary.
  `DataPaths.Resolve` derives the packaged probe from that default, so the two are coupled by
  construction.
- **`test -f <output>` after an export is not a sufficient release gate.** This ADR's export step runs
  `… || true` then checks the file exists. An emptied `custom_template/release` makes Godot produce a
  complete binary from the *stock* template, so that check goes green on a build missing every engine
  backport. `release.yml` now also asserts the binary's **content** —
  [ADR-0017](ADR-0017-engine-patches.md).

Unchanged: the native-runner-per-platform topology (no cross-export), the perf-baseline gate, and the
`VA_DATA_DIR` override — though that env var is spelled `VA_DATA_DIR` now, not `XG_DATA_DIR`.