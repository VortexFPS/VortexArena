# ADR-0015 — Launcher/updater: Avalonia shell, Velopack self-update, split game payload

**Status:** Accepted (2026-07). **Moved 2026-07-30 to
[`VortexFPS/VortexLauncher`](https://github.com/VortexFPS/VortexLauncher/blob/main/ADR-0015-launcher-updater.md).**

This is a pointer stub, not the decision. The full ADR travelled with the code when the launcher was
extracted into its own repo (repo-restructure stage 6, items 38–39); the stub stays so the ADR sequence
has no gap and so a reader who lands on 0015 from the index is not left wondering whether it was
withdrawn.

## Why it moved

The launcher is an Avalonia app with its own release cadence, its own Velopack packaging, and no
dependency on the game's build — see §5.4 of
[`planning/repo-restructure-2026-07-29.md`](../repo-restructure-2026-07-29.md). Its code was extracted
from `launcher/` on `feature/launcher-updater` with `git subtree split`, so the history is preserved
there rather than duplicated here.

## What still lives in THIS repo

The launcher and the game meet at exactly one interface, and the game side of it is here:

- **`tools/make-manifest.py`** — emits `latest.json`, the manifest the launcher consumes.
- **`tools/package.sh`** and **`.github/workflows/release.yml`** — build and publish the per-platform
  zips and the content-addressed asset pack that `latest.json` describes.

So a change to the manifest's *shape* is a two-repo change and both sides have to land together.

## Two things to know before touching either side

- **Do not publish a non-game-build release in this repo.** The launcher's primary update path is
  `https://github.com/VortexFPS/VortexArena/releases/latest/download/latest.json`, and GitHub resolves
  `releases/latest` to the newest *non-draft, non-prerelease* release. Anything else that lands there
  (an engine template, a tooling artifact) becomes `latest`, `latest.json` 404s, and every launcher
  falls back to `GitHubApiFeed` — unauthenticated GitHub API at 60 requests/hour. It keeps working, on
  a rate-limited path, with nothing announcing the change. If something non-game must be published
  here, mark it `prerelease` — and script that flag rather than leaving it to a checkbox.
- **The stage-5 artifact rename is an update-continuity cutover.** `XonoticGodot-*` → `VortexArena-*`
  (item 35) does not carry existing installs across. The first `VortexArena`-named release is a
  deliberate break and needs documenting in `docs/RELEASING.md` and in the launcher repo *before* the
  tag is pushed.
