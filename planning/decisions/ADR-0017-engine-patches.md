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

## Amendment 2026-07-31: the pin covers every preset, not just Windows

As accepted, only `windows-client` set `custom_template/release`; the other three presets were empty,
which is the value this ADR itself identifies as the dangerous one. The reasoning at the time was that
the Linux and macOS templates carry no patches, so pinning them bought nothing. That is true about
*behaviour* and wrong about *provenance*: it left Linux and macOS players, and every dedicated server,
running whatever stock template the build machine happened to have, with no record of which.

All four presets now point at a pinned template, and a new
`verify-engine-template.py --preset-config PRESET` gates the field **before** the export: non-empty,
naming the file the lockfile pins, and matching its sha256. That check exists because the binary check
this ADR argues for cannot be extended to the other platforms:

**Content verification is Windows-only, and that is a property of the patch set rather than a gap to
close later.** The patches touch `platform/windows/` exclusively, so a "patched" Linux template and a
stock one contain the same engine code. No marker can discriminate them because there is nothing
different to find, and a required marker invented for those platforms would fail a good binary: the
`RAWINPUTHEADER` row in the table above, in its worst form. So `verify-engine-template.py` prints
`NOT CONTENT-VERIFIED` there and qualifies its summary line rather than reporting a pass for a check
that did not run. The non-Windows presets are covered by the pre-export gate plus Godot's hard abort on
a populated-but-missing path, which together leave only "Godot ignored a valid path" uncovered, as
opposed to the empty-field case, which is the one that fails silently.

This also revises one line under *Alternatives considered*: a pre-export configuration check was
rejected as **insufficient**, which it is, since it cannot see what shipped. It is not, however, worthless,
and on the platforms where no content check is possible it is the only thing standing between an
emptied field and a stock build. It is now run everywhere, *in addition to* the binary check on
Windows, not instead of it.

`linux-dedicated` is pinned as well, though a headless server has no mouse and would not carry the
backport in any case. It consumes the same file `linux-client` already fetches, so the pin is free;
and leaving exactly one preset empty would re-create this ADR's hole in the least-watched build.

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
