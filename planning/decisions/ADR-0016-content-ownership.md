# ADR-0016 — Vortex Arena owns its content

**Status:** Accepted (2026-07-30, recording what stages 1–3 of the repo restructure actually built)

## Context

Until this migration the game had no content of its own. `download-assets.sh` cloned upstream Xonotic
repositories into `assets/data/` at build time, and that directory was gitignored. On the dev box it was
in fact a **junction to a pristine upstream checkout**, which made the arrangement worse than it looked:

- **Divergence could not survive.** The port's one config difference — a physics preset — was applied by
  hand-editing a copy of `physicsX.cfg` and hand-editing `xonotic-server.cfg` to exec it. Re-pointing the
  tree at a clean upstream checkout reverted both, silently. Nothing failed; the game ran stock physics
  while `ConfigLoader`'s own doc comment and `planning/parity/cvar-diff-known.yaml` both went on
  describing it as running ours. See [ADR-0018](ADR-0018-config-layer.md).
- **The parity tooling was comparing upstream against itself.** `tools/parity-cvar-diff.py` defaulted its
  port-side root into `assets/data`, so both roots resolved to the same directory and the differ could not
  report divergence at all. Every "0 value diffs" it produced was a tautology.
  `tools/parity-asset-check.py` likewise validated the upstream tree while claiming to model the VFS the
  game mounts.
- **The licence texts travelled with nothing.** Redistributing content we never committed left the
  licences behind in the upstream checkout.
- **A fork with no content is not a fork.** Every change to art, sound or config was either impossible or
  a merge conflict waiting on the next upstream refresh.

## Decision

**Core content is committed to this repository under `data/` and arrives with the clone. Compiled maps
are fetched build output. Map sources live in their own repository.**

1. **No upstream cloning at build time.** `download-assets.sh` is deleted. Core content — textures,
   models, sounds, fonts, music, and the config tree — is committed at `data/core.pk3dir`,
   `data/music.pk3dir` and `data/font-*.pk3dir`. The licence texts travel with it in `data/licenses/`.
2. **The content path is `res://data`**, not `res://assets/data`. `DataPaths.Resolve` derives the
   packaged exe-relative probe from that default, so `tools/package.sh` lays `data/` beside the binary and
   the two must move together.
3. **Compiled maps are release assets, pinned by a lockfile.** `data/maps.lock.json` pins each pack by
   sha256 to a [VortexMaps](https://github.com/VortexFPS/VortexMaps) release;
   `tools/data/fetch-maps.py` installs one `.pk3` per map into `data/maps/`, unextracted, because
   `MountGameDir` mounts a `.pk3` natively. `data/maps/` is gitignored — maps are build output, not source.
4. **Map sources live in VortexMaps**, which compiles them with q3map2 in CI and publishes per-map plus
   one shared-art archive. The game repo never carries the 1.5 GB source tree; release jobs set
   `submodules: false`.
5. **Convert before committing.** TGA content was converted to PNG *before* its first commit. This is not
   a preference: git keeps every blob, so importing 2,233 TGAs and converting afterwards would have left
   ~2.21 GB of dead TGA blobs in history permanently, fixable only by a rewrite that invalidates every
   clone.

## Consequences

- A clone is larger, and `--filter=blob:none` is the documented default for that reason.
- An upstream content refresh becomes a **file replacement rather than a merge**, because we do not edit
  the upstream files (ADR-0018).
- The parity tooling now compares two genuinely distinct trees. `parity-cvar-diff.py` gained
  `assert_distinct_roots()`, which refuses to run when both roots resolve to the same directory — because
  empty output from a differ must never be able to mean "compared nothing".
- `data/` being present is now a property of a *valid checkout*, so its absence is a hard error rather
  than a prompt to run a downloader. Only `data/maps/` can legitimately be missing, and the tests, the
  CI gate and `package.sh` all distinguish those two cases.
- Some content is CC-licensed rather than GPL. Committing it means the per-item notices have to be
  routed into whichever archive carries the art they cover — handled by `VortexMaps/build/split-pack.py`.

## Alternatives considered

- **Keep fetching at build time.** Rejected: it is the mechanism that lost our one divergence and made
  two parity tools tautological. Neither failure was noticed by anything.
- **Commit maps too.** Rejected: compiled BSPs and their lightmaps are build output, regenerated from
  sources on every map change. Committing them would put hundreds of megabytes of derived artefacts into
  history per revision, for content that a lockfile pins exactly as well.
- **Submodule the content.** Rejected for core content: it reintroduces "the tree can be re-pointed" as a
  failure mode, which is precisely what broke. Kept for map *sources*, where the size argument dominates
  and no divergence lives.
