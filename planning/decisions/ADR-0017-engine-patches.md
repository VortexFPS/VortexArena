# ADR-0017 — Engine patches are pinned, built in CI, and asserted in the shipped binary

**Status:** Accepted (2026-07-30)

## Context

The Windows release export does not use a stock Godot export template. It uses one built from
`4.6.3-stable` plus a backport of
[godotengine/godot#109639](https://github.com/godotengine/godot/pull/109639), which batch-drains raw mouse
input. Measured cost of *not* having it: **+1.38 ms median frame time while turning**, against +0.01 ms
patched — the felt movement stutter whenever the mouse moves.

An export template *is* the engine: Godot appends the game's `.pck` to a prebuilt binary rather than
compiling anything. So "which template did this build use" decides whether the shipped game has the fix.

Three things made that question dangerous to leave unanswered:

- **An empty `custom_template/release` silently ships stock.** Verified by running the real export twice
  and discriminating on binary *content*: with the field emptied, Godot produced a complete, launchable
  108,301,144-byte binary containing **zero** occurrences of `GetRawInputBuffer`, versus 70,699,368 bytes
  and one occurrence with the patched template. A *wrong* path is different — it exits 1 with no output —
  but its error never mentions a missing file (`Mismatching custom export template executable
  architecture: found "invalid"`), so the natural response is to blank the field, which is the one
  genuinely dangerous value.
- **An existence check cannot catch it.** `release.yml` runs the export as `… || true` then `test -f`. A
  stock-template export produces a file, so the job goes green regardless of what Godot printed.
- **The only patched template lived on one dev box**, so no other machine — including CI — could produce a
  correct Windows release at all.

## Decision

**Pin the patch set by hash, build the template in CI, and assert the shipped binary's content.**

1. **`tools/engine-patches/engine.lock.json`** pins the engine version, upstream tag, scons flags, and
   each patch by sha256. The hash is over the **LF** form, which is why `.gitattributes` pins
   `*.patch text eol=lf`: without it the file checks out CRLF under `core.autocrlf` and the same commit
   hashes differently on Windows and on a Linux runner.
2. **`.github/workflows/build-engine-template.yml`** builds it on `windows-latest`, manual dispatch only.
   A .NET template cannot be built in one scons call — the C# bindings compile from generated glue, and
   the glue is produced by *running* a Godot editor binary, so the editor is built first purely to
   generate it. That is Godot's own sequence.
3. **Assert on the binary, not on the configuration.** `tools/verify-engine-template.py --binary` checks
   markers read out of the lockfile, so the template build and the export-time check cannot drift apart.
   Wired into `release.yml` after the export and into `ci/ci.sh` (`--patches` half only).
4. **Publish as a scripted `--prerelease` release asset.** GitHub resolves `releases/latest` to the newest
   *non-prerelease* release, and the launcher's primary update path is
   `/releases/latest/download/latest.json`. A full release here would 404 that manifest and drop every
   client onto the unauthenticated API feed at 60 req/hr — quietly. Scripting the flag removes the
   checkbox a human could forget, and a final step asserts `releases/latest` did not move.

### Marker choice was measured, not read off the patch

Two of the three obvious candidates are wrong, and both fail in the silent direction:

| candidate | stock | patched | verdict |
| --- | --- | --- | --- |
| `GetRawInputBuffer` | 0 | 1 | the only usable marker |
| `GetRawInputData` | 2 | 1 | in **both** — a presence check passes on a stock build |
| `RAWINPUTHEADER` | 0 | 0 | compile-time only — a presence check fails on a *good* build |

Recorded in the lockfile so nobody re-derives it.

### The check defeated itself once, which is why `forbidden` exists

Godot was baking `tools/` into the game pck, and `engine.lock.json` contains the literal string
`GetRawInputBuffer` several times. So an exported binary carried the marker whether or not the patched
template was used — the count read 3×, and a stock export would have passed. Fixed by excluding dev
material from the export (which also removed 3.1 MB from every release), plus a **contamination canary**:
the lockfile's `forbidden` list names `engine.lock.json`, so if anything re-includes it the check fails
loudly instead of silently becoming a tautology again.

## Consequences

- The template is a build input with a pinned identity, so "is this build stale" is answerable.
- Two full Godot compiles per template build (~1.5–3 h on a 4-core runner). Acceptable because the output
  changes only when the engine version or the patch set changes. The scons cache is keyed on engine tag +
  patch hash.
- **This mechanism has a known expiry.** PR109639 merged to `master` on 2026-07-24, milestone **4.8**, and
  is in no released or snapshot build yet (verified by counting the marker in `4.6.3-stable`, `4.7-stable`,
  `4.7.1-stable` and `4.8-dev2`: all 0; `master`: 2). On the recent minor cadence 4.8 lands around
  Oct–Nov 2026, at which point `tools/engine-patches/` should be **deleted**, not maintained. That bounded
  life is deliberately why this is a scripted prerelease on the game repo rather than a fifth repository
  with its own build pipeline.
- A dev snapshot cannot simply be dropped in: template and engine versions must match, so adopting
  4.8-dev means moving the whole project to an unreleased engine. Use one to *rehearse* the upgrade, not
  to ship.

## Alternatives considered

- **A dedicated `VortexEngine` repository.** Correct long-term shape, rejected on cost: the artefact
  expires in ~3 months, and the launcher-feed hazard that motivated separation is fully addressed by
  scripting `--prerelease` plus asserting `releases/latest`.
- **Build the template in every release run.** Rejected: hours added to every release for an artefact
  that changes only when the patch set does.
- **GitHub Actions cache instead of a release asset.** Rejected: the cache evicts after 7 days unused, and
  releases are rarer than that, so it would rebuild constantly.
- **Pre-export configuration check only.** Rejected as insufficient — it cannot see an empty field's
  consequence, and it says nothing about what actually shipped.
