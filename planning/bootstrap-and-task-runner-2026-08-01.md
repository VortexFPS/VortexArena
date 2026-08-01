# Fresh-clone bootstrap and a central task runner — plan

**Status: PLAN, NOT IMPLEMENTED (2026-08-01).** Written after a fresh clone on macOS could not be brought to a
running state without hand-editing repo config. Nothing here has been built; this is the design to argue with
before any of it is.

---

## The finding that shapes this plan

**Most of the bootstrap already exists, and it is good.** The instinct to "write setup scripts" would rebuild
work that is already done and better-reasoned than a rewrite would be:

| piece | state | where |
|---|---|---|
| Fetch + verify compiled map packs | ✅ done, with resume/backoff and sha256 pinning | `tools/data/fetch-maps.py` |
| Compile maps from source instead | ✅ done — `--rebuild`, `maps-src` submodule | same |
| Fetch + verify the pinned engine template | ✅ done | `tools/data/fetch-engine-template.py` |
| Build a patched engine template | ✅ done, in CI | `.github/workflows/build-engine-template.yml` |
| Patch set, pinned by sha256 | ✅ done | `tools/engine-patches/`, `engine.lock.json` |
| Verify what shipped is what we pinned | ✅ done, three independent gates | `tools/verify-engine-template.py` |
| Export + package all four presets | ✅ done | `tools/package.sh`, `export_presets.cfg` |
| Local CI gate | ✅ done | `ci/ci.sh` |

The prebuilt-vs-compile choice the brief asks for **already exists for maps** (`--rebuild`) and **already
exists for the engine** (fetch pinned template vs `build-engine-template.yml`). What is missing is not the
capability. It is that nothing *asks*, nothing *checks*, and there is no single door in.

### What actually blocks a fresh clone

Four things, and only the first is large:

1. **Nothing acquires the Godot editor.** Every script defaults to a hardcoded path:
   `GODOT="${GODOT:-/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe}"` — in `ci/ci.sh`,
   `run-release.sh`, `tools/visual-qa.sh` and others. On any machine that is not that one Windows box, every
   Godot-dependent path silently degrades or dies. There is no downloader, no version check, no discovery.
2. **`nuget.config` hard-fails `dotnet restore` on a fresh clone.** It adds `godot-editor` →
   `C:\Program Files\Godot\GodotSharp\Tools\nupkgs` as a package source, and NuGet treats a missing local
   source as a fatal error, not a warning. `.github/workflows/ci.yml:48` works around it with
   `dotnet nuget remove source godot-editor`. **Every fresh clone hits this, on every platform including
   Windows without Godot installed** — the test suite cannot even restore. (Hit today; the local workaround
   was a scratch `--configfile`.)
3. **No dependency check.** `dotnet`, `python3`, `git`, `git-lfs`, `curl`, `unzip`, `scons`, platform SDKs —
   all assumed. Failures surface as tool-specific errors far from the cause.
4. **No entry point.** Twelve-plus scripts across `tools/`, `ci/` and the repo root, split `.sh`/`.ps1`, with
   the naming conventions of three different eras. A newcomer cannot guess where to start, and neither could I.

Two of these are one-line fixes with outsized value. **Do them first and independently of the rest** — see
Phase 0.

---

## Name recommendation: `./vx`

Xonotic's `./all` is a poor model for the name specifically because `all` reads as a *target* ("build all"),
not as a *tool*, so every subcommand contradicts it: `./all clean` says "all clean".

**Recommended: `./vx`.** Two characters, no collision with any standard binary (checked), unmistakably
Vortex, and it reads correctly with every subcommand:

```
./vx setup            ./vx build            ./vx run
./vx test             ./vx export           ./vx server
```

Runners-up, if `vx` is too terse:

| name | for | against |
|---|---|---|
| `./forge` | evocative, build-flavoured | implies building only; `./forge run` reads oddly |
| `./vortex` | zero ambiguity | six chars on every invocation; shadows the umbrella repo name |
| `./va` | matches the `VortexArena` codename | `va` is close to `van`/`vi` typos; codename is slated to change |

Avoid `./do` (shell keyword), `./go` (Go toolchain), `./run` (collides with the existing `run-*.sh` family and
implies one verb), `./make` (Make).

---

## Shape

`./vx` is a **thin dispatcher, not a rewrite**. It parses global flags, resolves the toolchain, checks
dependencies, and delegates to the existing scripts, which stay where they are and stay independently
runnable. This matters for three reasons: CI keeps calling what it calls today, every script keeps its own
documented contract, and the migration can be incremental rather than a flag day.

```
./vx <command> [options]

  setup      [--profile <name>] [--yes]   bootstrap a clone to runnable
  doctor                                  diagnose toolchain/deps, fix nothing
  build      [--config Debug|Release]     dotnet build the host
  test       [--filter <expr>]            the suite
  run        [--host <map>] [--bots N]    launch a client
  server     [--map <m>] [--gametype <g>] dedicated
  export     [--preset <p>] [--all]       export presets (fetches+verifies templates)
  package    [--preset <p>]               zip the exports
  maps       [--rebuild] [--verify-only]  wraps fetch-maps.py
  engine     [--fetch|--build|--verify]   wraps the template tooling
  ci                                      the full local gate (ci/ci.sh)
  perf       [--label L] [--map M]        wraps perf-run.sh
```

**A Windows shim.** `vx.cmd` (or `vx.ps1`) forwards to the same logic. The `.sh`/`.ps1` split in `tools/` is
already a maintenance tax — `perf-run.ps1` and `perf-run.sh` are parallel implementations that have already
drifted (`perf-smoke.ps1` and `wobble-capture.ps1` have no `.sh` counterpart at all, so those workflows are
Windows-only today).

### What to implement it in

The first draft of this plan justified Python with "Python is a hard dependency already". **That is circular**
— it is only a dependency because the tooling was written in it. Redone properly below.

**Nothing is universally present.** Measured, not assumed:

| runtime | Windows | macOS | Linux |
|---|---|---|---|
| POSIX sh / bash | ❌ needs Git Bash — but the repo already requires it (`run-release.sh` uses `/c/…` Git Bash mount paths) | ✅ **bash 3.2.57 (2007)** | ✅ bash 4/5 |
| PowerShell | ✅ 5.1 built in | ❌ install | ❌ install |
| Python 3 | ❌ install | ✅ **3.9.6** at `/usr/bin/python3` via Xcode CLT — which `git` needs anyway, so it is present by the time you have a clone | ⚠️ **desktop yes, minimal no** — see below |
| .NET 8 SDK | ❌ install | ❌ install | ❌ install |

The one dependency that is genuinely **unavoidable** is .NET — you cannot build, test or run this project
without it. Everything else is a choice.

**"Python is free on Linux" is only true for desktop distros.** Verified rather than assumed:

- ✅ **Linux Mint ships it, unavoidably.** `mintupdate` (Update Manager) and `mintinstall` (Software Manager)
  are both Python projects, and `mintupdate`'s `debian/control` declares `Depends: python3-apt,
  ${python:Depends}`. Mint cannot reach a usable desktop without `python3`.
- ✅ Same argument holds for **Fedora** (`dnf` is Python) and **Ubuntu** desktop.
- ❌ **Not present by default** on Arch (`base` has no python; pacman is C), Alpine, minimal Debian netinst,
  and most container base images (`debian:*-slim`, `alpine`, RHEL UBI minimal).

That exception is not academic here: `linux-dedicated` is a shipping preset, and a server deployment is
exactly the case that lands on a minimal container image.

#### Option A — Python 3 (recommended)

- ✅ **Zero migration.** This plan explicitly does not rewrite `fetch-maps.py`, `fetch-engine-template.py` or
  `verify-engine-template.py`. Any other choice means two tooling languages permanently, or an expensive
  rewrite of code whose comments carry real history.
- ✅ Everything the job needs is stdlib: `json` for the lockfiles, `hashlib` for sha256, `urllib` with `Range`
  for resumable downloads, `subprocess` with a portable `timeout=`.
- ✅ Present by default on macOS and Linux; the Windows dev box demonstrably already has it.
- ✅ Starts instantly, needs no restore and no network — so `doctor` works on a machine missing everything.
- ❌ A Windows-only contributor with no Python must install it. Mitigated: the shim detects and links it.
- ❌ 3.9 is the macOS floor, so no `match`, no `X | Y` unions. Minor.

#### Option B — C# / .NET (the serious alternative; stronger than the first draft allowed)

- ✅ **Adds literally no new dependency** — the SDK is already mandatory.
- ✅ **Best launcher story by a distance.** `VortexLauncher` is .NET (`Launcher.Core`, `Launcher.Cli`, …), so
  it could reference `vx` as a *library* — real progress reporting, cancellation and typed state instead of
  parsing a subprocess.
- ✅ Team's primary language; could share types with the repo (read `engine.lock.json` with real records).
- ❌ **Worst bootstrap ordering, and this is the deciding flaw.** `dotnet run` needs a restore, which needs
  the network *and* the `nuget.config` fix — while `doctor`'s entire job is to diagnose a machine that may be
  missing .NET. A tool that cannot run until the thing it diagnoses is working is the wrong tool for step one.
- ❌ Leaves the existing Python tooling in place, so the two-language cost lands anyway unless it is rewritten.

#### Option C — POSIX sh / bash only

- ✅ No new dependency beyond Git Bash, already required.
- ❌ **Cannot do the work comfortably.** JSON lockfiles need `jq`, which ships by default on none of the three
  platforms. sha256 is `sha256sum` on Linux, `shasum -a 256` on macOS, `certutil` on Windows. And there is no
  portable `timeout` — *that is the exact bug that broke `ci/ci.sh` on macOS*, so choosing bash means adopting
  the bug class rather than fixing it.
- ❌ macOS is stuck on **bash 3.2** (2007, GPLv2): no associative arrays, no `${var,,}`, no `mapfile`.
- ❌ Realistically becomes sh + PowerShell, i.e. two implementations — the drift the `.sh`/`.ps1` pairs already
  demonstrate.

#### Option D — PowerShell 7

Inverts bash's problem: free on Windows, an install on macOS and Linux, plus the 5.1-vs-7 trap. Strictly worse
than A or B here.

#### A shim is unavoidable in every option — which weakens the case against C#

The first draft called bootstrap ordering C#'s "deciding flaw". **That was overstated.** Every option needs a
low-level shim, so the question is only how much the shim must do:

| option | what the shim does | size | first run |
|---|---|---|---|
| Python | find `python3`/`python`/`py -3`, check ≥3.9, exec | ~15 lines | instant |
| C# | find `dotnet`, check SDK vs `global.json`, build if missing/stale, exec the dll | ~40 lines | one `dotnet build` (~10-30 s, needs network + the Phase 0 nuget fix) |

Both are writable in POSIX `sh` + `.cmd` using nothing but `command -v` and string compares — no JSON, no
sha256, no timeout, so neither shim hits the problems that rule bash out for the real tool.

And "C# needs network on first run" is a weak objection: `vx setup` downloads Godot, the maps and the engine
template anyway. The genuine residual advantage is narrower than claimed — **`vx doctor` still works when .NET
is missing or a restore is failing**, which is exactly the machine you most want to diagnose. Real, but it is
one command, not the whole tool.

#### The dependency argument, done properly

This is what actually decides it, and the first draft got it wrong by comparing languages instead of
end-states. There are three coherent destinations:

| end state | runtimes a contributor needs | migration cost |
|---|---|---|
| **1.** `vx` in Python, existing `tools/*.py` stay | Python **+** .NET | none |
| **2.** `vx` in C#, existing `tools/*.py` stay | Python **+** .NET | none — but **no dependency win**; two tooling languages instead of one |
| **3.** `vx` in C#, `tools/*.py` migrated over time | **.NET only** | large (~15 scripts), staged |

So **C# only wins on dependencies if the Python tooling eventually moves too.** State 2 buys the launcher and
testability advantages but is worse than state 1 on the axis this whole question is about. State 3 is the only
one that actually reduces what a contributor must install — and it is the fewest of any option, because .NET
is mandatory regardless.

#### Decision — revised

**`vx` in C#, reached in stages, with a POSIX `sh` + `.cmd` shim.** Destination is state 3.

The case, in order of weight:

1. **.NET is the only unavoidable dependency.** State 3 is the only end-state where a contributor installs
   exactly one runtime, and it is the one they already need.
2. **`vx` becomes testable in the existing suite.** 3,771 tests already run in CI on every push; build tooling
   that can be unit-tested alongside the game is worth a lot, and neither bash nor a separate Python tool gets
   that for free.
3. **The launcher consumes `vx` as a shipping interface across repos.** `Launcher.Core` referencing it as a
   library beats parsing a subprocess — typed state, real cancellation, no schema drift between two codebases.
4. It is the team's language, so the build system stops being a second thing to context-switch into.

**Staging, so this is not a big-bang rewrite:**

- **Stage 1** — `vx` in C#, shelling out to the existing Python tools unchanged. Python remains required. This
  is *not* a regression: it is required today.
- **Stage 2** — migrate the critical-path tools only (table below).
- **Stage 3** — Python drops off the **critical path**. It stays an optional dependency for parity and perf
  analysis, which is fine — those are not things a fresh clone needs.

#### Which Python tools should move, and which should not

The split is cleaner than expected, because it falls out of an existing fact rather than a preference:
**`grep`ing every `tools/*.py` import shows the pip-dependent ones (`numpy`, `yaml`) are exactly the tools
nothing on the critical path calls.** Neither `ci/ci.sh` nor `ci.yml` references any of them.

| tool | imports | on fresh-clone path? | migrate? |
|---|---|---|---|
| `data/fetch-maps.py` | stdlib | ✅ yes | **yes** — HTTP+Range, sha256, JSON: C# does all three from the BCL |
| `data/fetch-engine-template.py` | stdlib | ✅ yes | **yes** — same shape |
| `verify-engine-template.py` | stdlib | ✅ yes | **yes** — JSON + file hashing + binary marker scan |
| `verify-built-template.py` | stdlib | ✅ yes | **yes** |
| `check-parity-refs.py` | stdlib | ✅ yes (`ci.sh:72`) | yes |
| `make-manifest.py` | stdlib | packaging | yes |
| `find-cvars.py` | stdlib | ❌ docs regen | **no** — regex source-scanning; pleasanter in Python, run rarely |
| `parity-*.py` (×5) | **numpy, yaml** | ❌ | **no** — numeric/report analysis; a C# rewrite buys nothing |
| `upstream-ledger-html.py` | **yaml** | ❌ | **no** |
| `wobble-report.py` | **numpy** | ❌ | **no** |
| `perf-report.py` | stdlib | ❌ perf analysis | optional |

Two things this settles:

1. **The end state is "`.NET` only for a fresh clone", not "no Python anywhere."** That is the goal worth
   having; chasing the stronger version would mean reimplementing numpy analysis in C#, which is a bad trade.
2. **Python tooling is already not stdlib-only**, so the "no pip" discipline proposed for a Python `vx` would
   have been a new rule the existing tools do not follow — `parity-*` already needs
   `pip install numpy pyyaml`. Whatever happens, that stays true for the analysis tools.

**Migration risk to respect:** `fetch-maps.py`'s retry/backoff/Range-resume is documented as *"proven against
real flaky transfers"* and deliberately kept rather than simplified. Port it as a translation with its
comments intact, not as a fresh implementation, and keep the Python version until the C# one has fetched a
real map set over a real connection.

**What would flip this back to Python:** if stage 1 shows the shim's build step is genuinely painful in daily
use — a stale-rebuild loop, slow CI, or a restore that fails offline. `vx doctor` should be the canary; if it
cannot reliably run on a broken machine, that is the signal.

**Keep regardless of language:** every command gets a `--json` output mode with a documented, versioned
schema. It is the contract the launcher depends on across a repo boundary, so treat a breaking change to it as
a breaking change even once a shared library exists.

---

## Phase 0 — unblock the fresh clone (small, do first, independent of everything else)

These two are worth landing on their own merit even if the rest of this plan is rejected.

1. **Remove `godot-editor` from `nuget.config`.** Its stated benefit is "resolves the EXACT packages the
   installed editor uses, and works offline". The packages are on nuget.org (the comment says so, verified
   2026-06), CI already deletes the source, and the cost is that *every* fresh clone fails to restore. If the
   offline/exact-match property is genuinely wanted, keep it opt-in via a gitignored `nuget.local.config` the
   dev box adds, rather than a committed source that only resolves on one machine.
2. **Centralise Godot discovery.** One resolver — env `GODOT`, then a repo-local `.godot-bin/`, then PATH,
   then the per-platform install locations — used by every script instead of eleven copies of a Windows path
   default. Ships with a clear error naming `./vx setup` when nothing is found, which is strictly better than
   today's silent degradation (`ci/ci.sh` skips the smoke test; `visual-qa.sh` fails obscurely).

**Gate everything else on Phase 0 landing**, because both are prerequisites for `setup` to work at all.

---

## Phase 1 — `./vx doctor` and `./vx setup`

`doctor` before `setup`, deliberately: a read-only diagnostic that changes nothing is the thing you want when
a build breaks six months from now, and `setup` is then "doctor, plus act on what it found".

### Dependency matrix to detect

| dependency | needed for | check | install offer |
|---|---|---|---|
| `git` | everything | `git --version` | never — chicken/egg |
| `git-lfs` | if any asset path uses it | `git lfs version` | brew / apt / winget |
| .NET SDK 8+ | build, test | `dotnet --list-sdks` vs `global.json` | link to installer; **do not auto-install a runtime** |
| Python 3.9+ | tooling, `vx` itself | running | never — chicken/egg |
| Godot 4.6.3 mono | run, export, smoke, visual QA | resolver + version probe | **download to `.godot-bin/`** (see below) |
| `curl`/`unzip` | fetchers | probe | brew / apt |
| `scons`, C++ toolchain | `engine --build` only | probe | offer, platform-specific |
| `q3map2` | `maps --rebuild` only | probe | point at `maps-src` toolchain, offer CI instead |
| Xcode CLT | macOS export/build | `xcode-select -p` | prompt user to run it themselves |

**Install policy — the part to get right.** Installing software is the most invasive thing a setup script
does, and it is where these scripts usually earn their bad reputation.

- **Never install without an explicit yes.** `--yes` is opt-in, never the default, and is what CI passes.
- **Never `sudo` silently.** If a step needs root, print the exact command and let the user run it. This is
  non-negotiable on a shared or corporate machine.
- **Prefer repo-local over system-wide.** The Godot editor goes to a gitignored `.godot-bin/` inside the
  clone — not `/usr/local`, not `~/.local`. Uninstall is `rm -rf`. This also lets two clones pin two engine
  versions, which the current global-install assumption cannot express.
- **Verify every download by sha256 against a lockfile**, exactly as `fetch-maps.py` and
  `fetch-engine-template.py` already do. A new `godot.lock.json` pins editor builds per platform. Reuse the
  existing fetch helper rather than writing a third downloader.
- **Package managers are a suggestion, not a mechanism.** Detect brew/apt/dnf/winget and offer the command;
  do not wrap them in abstraction. When it fails, the user needs to see the real command.

### The wizard

Interactive only when stdin is a TTY **and** no `--profile` was passed. Everything it asks must have a flag,
so the wizard is a front-end to the flags rather than a separate path — that is what keeps CI and the launcher
from diverging from what a human gets.

Questions, in order, each skipped when already satisfied or answered by a flag:

1. **What are you setting up for?** → play / develop / server / CI. Sets defaults for the rest.
2. **Godot editor:** download the pinned 4.6.3 mono build to `.godot-bin/` (recommended) · use an existing
   install (prompt for path) · skip (server/CI paths need no editor).
3. **Maps:** download prebuilt packs (recommended, ~200 MB) · compile from source (needs `maps-src` submodule
   + a Linux toolchain; warn it does not reproduce the pinned hashes and say why) · skip.
4. **Engine template** — only when the profile exports: fetch the pinned template (recommended) · build from
   source with our patches (long; probe for scons + toolchain first) · stock template (warn: loses the
   Windows mouse-input backport, and `verify-engine-template.py` will report it).
5. **Missing dependencies** — list them, offer the per-platform install command, ask before each.

**Answers are written to a gitignored `.vortex-setup.json`**, so re-running is idempotent and `doctor` can
report what was chosen. A profile is a named preset of the same answers.

### Built-in profiles (the "shortcuts/aliases" from the brief)

| profile | Godot | maps | engine template | deps |
|---|---|---|---|---|
| `play` | download | prebuilt | fetch pinned | offer |
| `dev` | download | prebuilt | fetch pinned | offer |
| `dev-full` | download | **compile from source** | **build from source** | offer |
| `server` | none | prebuilt | fetch pinned (linux-dedicated) | offer |
| `ci` | download, pinned | prebuilt | fetch pinned | **never install, fail loudly** |
| `launcher` | reuse launcher's | prebuilt | fetch pinned | never install |

`./vx setup --profile ci --yes` is fully non-interactive and is what `ci.yml` would call. Same for the
launcher's build-from-source path (`VortexLauncher` ADR-0015), which gets a stable contract instead of
reaching into individual scripts.

---

## Phase 2 — migrate the existing scripts behind `vx`

Incremental, one script per change, each keeping its direct entry point:

1. Point the Godot-dependent scripts at the Phase 0 resolver (deletes ~11 hardcoded paths).
2. Add `vx` subcommands that shell out to the existing scripts unchanged.
3. Port the Windows-only `.ps1` tools (`perf-smoke`, `wobble-capture`) to Python so `vx` exposes them
   everywhere, retiring the `.sh`/`.ps1` pairs as each is ported.
4. Switch `ci.yml` / `release.yml` to `./vx` **last**, once the local path has been used enough to trust.

**Explicitly not in scope:** rewriting `fetch-maps.py`, `fetch-engine-template.py`, `verify-engine-template.py`
or `package.sh`. They work, they are well-reasoned, and their comments carry history a rewrite would discard.
`vx` calls them.

---

## Risks

- **A wizard that hides what it does is worse than no wizard.** Every action prints the underlying command.
  `--dry-run` on `setup` prints the whole plan and does nothing.
- **`vx` becoming a second place where build logic lives.** The dispatcher must stay thin; any real logic
  belongs in the script it delegates to. Worth a rule in `CLAUDE.md`.
- **Bootstrapping `vx` itself.** It cannot depend on anything it installs. Python 3 stdlib only — no pip
  install, no venv, no third-party packages.
- **Windows parity is the usual failure mode.** The dev box is Windows and CI is Linux, so a macOS/Linux
  developer path can rot unnoticed. `doctor` should run in CI on all three platforms.
- **Download sizes are real** (maps ~200 MB, engine template ~67-150 MB, editor ~100 MB). Print totals and
  ask before fetching on a metered connection.

---

## macOS is a supported development platform — the concrete gaps

**Answered 2026-08-01: yes, macOS should work fully.** That promotes a set of items from "nice to have" to
"bugs", and makes the list below part of the work rather than a footnote. Each was verified by scanning the
tree, not inferred:

| # | gap | evidence | status |
|---|---|---|---|
| 4 | `nuget.config`'s `godot-editor` source aborts `dotnet restore` outright | Phase 0, item 1 | ✅ **FIXED** `24af7459` |
| 5 | ~11 hardcoded Windows Godot paths | Phase 0, item 2 | ✅ **FIXED** `24af7459` — `tools/lib/find-godot.sh` + `.ps1` twin |
| 8 | **Bare `python`**, which macOS 12.3+ and most current Linux do not provide. A blind swap to `python3` would have broken Windows instead: the python.org installer creates `python.exe` and `py`, not `python3.exe`. | 8 call sites in `ci/ci.sh` alone | ✅ **FIXED** `e91c1ea1` — `tools/lib/find-python.sh` |
| 1 | **`timeout` does not exist on macOS.** BSD userland has no `timeout`; it is `gtimeout` from `brew install coreutils`. | `ci/ci.sh:96,118,143` and `tools/perf-run.sh:48` | ❌ **OPEN** — not yet reached on this box only because Godot is absent, so the smoke block is skipped. Installing Godot on a Mac exposes it immediately. |
| 3 | **A fresh clone fails a test on Apple silicon.** `QuakeMathReferences` holds exactly two pins — x64/Windows CRT and x64/Linux glibc — and the test passes only on "any known platform". | `DeterminismTests.cs:156-160`; arm64/macOS measures `0xC1C9EEE2DA9D3297` (this box, 2026-08-01) | ❌ **OPEN** — now the *only* thing between macOS and a green `ci/ci.sh --no-smoke`. Deliberately not fixed here: see the note below. |
| 2 | **A documented house rule is impossible to follow on macOS.** `CLAUDE.md` says "Perf-relevant changes: run `tools/perf-smoke.ps1` before merging", and that script is PowerShell-only with no `.sh` twin. | `tools/perf-smoke.ps1`, `tools/wobble-capture.ps1` are the only two `.ps1` with no shell counterpart | ❌ **OPEN** — Phase 2 |
| 6 | `readlink -f` is BSD-incompatible on older macOS; both uses are `2>/dev/null`-guarded, so **verify the fallback actually fires** rather than assuming | `tools/run-client.sh:15`, `tools/run-dedicated.sh:19` | ❌ **OPEN** — low; guarded but unverified |
| 7 | `macos-client` exports from the **stock** template — a declared, tracked gap, not an oversight | `engine.lock.json` → `unpinned_presets.macos-client` | ❌ **OPEN** — provenance only; blocked on republishing the template in `macos.zip` form |

**State after Phase 0 + the `python` fix:** `ci/ci.sh --no-smoke` runs every step on macOS. Engine patch
provenance and the parity-pointer check — both pure Python — execute there for the first time. The gate stops
only at gap 3.

**Why gap 3 was not fixed in passing.** Adding `0xC1C9EEE2DA9D3297` to `QuakeMathReferences` is one line, but
it would pin *whatever this particular machine computes*. The two existing entries are each identified by
platform and toolchain (`x64 / Windows CRT`, `x64 / Linux glibc`) and came from those platforms' own runs. The
arm64 value should come from a clean `macos-latest` CI run and land with the same provenance, which also
proves the value is a property of arm64/Apple libm rather than of this laptop.

On (3): the fix is one line, but it is a **judgement call, not a mechanical edit** — adding the arm64 value
pins whatever this machine computes, so it should be taken from a clean CI run on `macos-latest` and landed
with the same reasoning the other two pins carry. Not done here.

On (1): `vx` being Python removes this class of problem rather than patching it, since `subprocess` timeouts
are portable. That is the strongest single argument for Python over bash.

**Recommended addition to Phase 2:** a `doctor` job in `ci.yml` running on `windows-latest`, `ubuntu-latest`
and `macos-latest`. Cross-platform rot is invisible when the dev box is Windows and CI is Linux — items 1-3
above all survived because nothing ever ran them on a Mac.

## Open questions

- ~~Should `./vx` live in `VortexArena` or one level up in `Vortex/`?~~ **Answered 2026-08-01: it lives in
  `VortexArena` and is the main way to build Vortex Arena.** `VortexLauncher` consumes it rather than wrapping
  it, which makes the `--json` contract above a shipping interface between two repos, not an internal
  convenience — version it from day one and treat a breaking change to it as a breaking change.
- **Do we pin the Godot editor by sha256 per platform?** Consistent with everything else here, and it is what
  makes `--profile ci` meaningful — but it is a new lockfile to maintain on every engine bump.
- **What happens on engine bump?** `godot.lock.json`, `engine.lock.json`, `global.json` and the patch set all
  move together. Worth one `./vx engine --bump <version>` rather than four manual edits.
