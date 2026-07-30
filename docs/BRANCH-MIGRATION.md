# Migrating a pre-restructure branch

How to bring a branch that was cut before the repo restructure onto the new structure without
resolving the same conflict a hundred times.

Companion to [`planning/repo-restructure-2026-07-29.md`](../planning/repo-restructure-2026-07-29.md),
gotcha **G8**. Read that first if you want to know *why* the paths moved.

**This is the primary path, not a fallback.** `feature/map-editor` landed before the restructure
(along with `claude/tool-selection-usage-design-8159cd`, which carried the `.vmap` text format). Seven
branches come through here:

| Branch | Note |
|---|---|
| `feature/launcher-updater` | extracted to `VortexFPS/VortexLauncher` instead of merged |
| `feature/demo-merge` | |
| `feature/dedicated-server-v2` | |
| `feature/anim-smoothness-ragdolls` | |
| `feature/player-soft-collision` | |
| `feature/playermodel-lean` | |
| `fix/warpzone-view-smoothing` | smallest; used to prove `migrate-branch.sh` |

Migrate them as a batch soon after the restructure. Every one accrues drift while it waits, and none
of their authors can merge `main` in the meantime without doing this anyway.

**Check the branch is actually open before migrating it.** Eight refs still exist that are fully merged
into `main` and carry no unique commits — `feature/map-editor`,
`claude/tool-selection-usage-design-8159cd`, `claude/map-editor-backlog-continue-6865a0`,
`claude/viberadiant-review-vortex-204d70`, `claude/vortex-arena-anti-cheat-b51f29`,
`claude/vortex-startup-disclaimer-949f5e`, `fix/editor-grid-stringname-alloc`,
`parity/port-recent-fixes`. Running this playbook against one of them is wasted work. A merged branch
looks identical to an open one in `git branch`, so check:

```bash
git rev-list --count main..my-branch
```

Zero means delete it, not migrate it.

> **Do this after the restructure has landed on `main`, never before.** The script below transforms a
> branch to match `main`; running it against a `main` that has not moved yet just breaks the branch.

---

## What actually changed

Five mechanical transformations, all scriptable:

| # | Change | Scale |
|---|---|---|
| T1 | `VortexArena.*` → `VortexArena.*` namespaces and `using` lines | ~250 files |
| T2 | `src/VortexArena.<X>/` → `src/VortexArena.<X>/`, plus `.sln` / `.csproj` filenames, `RootNamespace`, `AssemblyName` | 6 projects |
| T3 | `assets/data` → `data`, and the `.pk3dir` suffixes on `core` / `music` / `font-*` | every path literal |
| T4 | all twelve `XG_*` / `Xg*` env vars and MSBuild properties → `VA_*` / `Va*`, `XONOTIC_USERDIR` → `VORTEX_USERDIR`, artifact filenames | scattered |
| T5 | `bryankruman/VortexArena` → `VortexFPS/VortexArena` in URLs | 6 files |

T4 is the one most often done half-way. **Done on `main` 2026-07-30**; a branch that predates it needs
the same mapping applied. One of these is NOT a straight prefix swap — see the note below the table.

| old | new |
| --- | --- |
| `XG_DATA_DIR` | `VA_DATA_DIR` |
| `XG_BENCH` | `VA_BENCH` |
| `XG_BOTPLAYER` | `VA_BOTPLAYER` (also the `DefineConstants` symbol and every `#if`) |
| `XG_BOTS` | `VA_BOTS` |
| `XG_MAPS` | `VA_MAPS` |
| `XG_TICKS` | `VA_TICKS` |
| `XG_PERF_ASSERT` | `VA_PERF_ASSERT` |
| `XG_MAP` | `VA_MAP` |
| `XG_PROBE_BSP` | `VA_PROBE_BSP` |
| `XG_BASE_DIR` | **`VA_UPSTREAM_ROOT`** — not `VA_BASE_DIR`. See below. |
| `XgBotPlayer` (MSBuild) | `VaBotPlayer` |
| `XgDebugUnoptimized` (MSBuild) | `VaDebugUnoptimized` |
| `XONOTIC_USERDIR` | `VORTEX_USERDIR` |

**`XG_BASE_DIR` is the trap.** It means the upstream **checkout root** (`<parent>/Base`), and
`tools/upstream-watch.py` reaches into two siblings below it — `data/xonotic-data.pk3dir` and
`darkplaces`. But `VA_BASE_DIR`, already in use by `tests/TestPaths.cs` and the parity resolvers, means
the upstream **content dir** (`<parent>/Base/data`). They are one directory apart, so the obvious-looking
rename yields `Base/data/data/xonotic-data.pk3dir` — and upstream-watch would then report "no new
commits" forever while finding no repositories at all. Hence the distinct name.

`XONOTIC_USERDIR` is only the **override** knob. The default user-data location is `~/XonData`
(`UserPaths.DefaultFolderName`) and is deliberately NOT renamed: changing it would orphan every existing
player profile, which is a Tier-0 decision with a migration cost, not part of this sweep.

Sweep with:

```bash
git ls-files | xargs grep -IohE '\bXG_[A-Z_]+|\bXg[A-Z][A-Za-z]+' | sort -u
```

`-I` skips binaries — without it, random byte sequences in `data/**` textures and `.ogg` tracks match the
pattern and bury the real hits. The result is **not** expected to be empty: this file and the dated
reports under `planning/` name the old symbols on purpose, the reports because they record commands as
they were actually run. Only code, scripts, workflows and live docs should come back clean.

None of them is a semantic change. That is the whole basis of the strategy below.

## The strategy: transform, then merge once

Because the restructure is mechanical, you can apply **the same transformation to the branch** and
then merge. Both sides then agree on names and paths, so the merge only has to resolve the branch's
real, semantic changes.

```bash
git checkout my-branch
git checkout -b my-branch-migrated          # keep the original until you are done

bash tools/migrate-branch.sh                # T1-T5, mechanical
git commit -am "chore: mechanical migration to the VortexArena structure"

git merge main                              # only semantic conflicts survive
```

### Why not rebase

`git rebase main` replays each of the branch's commits onto the moved tree **one at a time**. Every
commit that touches a renamed file conflicts on the rename, so a 25-commit branch means resolving the
same path conflict 25 times. `feature/map-editor` alone would be an afternoon of identical
resolutions.

The merge above pays that cost once. Take the merge.

### Why not just merge without the transform

Without the transform, git sees the branch's `src/VortexArena.Common/Foo.cs` and main's
`src/VortexArena.Common/Foo.cs` and must infer the rename. Rename detection is heuristic, capped, and
degrades badly when a file was both renamed and edited — which is exactly the case for any file the
branch touched. Doing the transform first turns "infer a rename and merge the edits" into "merge the
edits."

## Before you start

**Raise git's rename-detection limits.** The restructure moves more files in one commit than git will
consider by default; past the cap it silently stops detecting renames and treats everything as
delete-plus-add, which produces enormous, useless conflicts.

```bash
git config merge.renameLimit 20000
git config diff.renameLimit 20000
```

**Check what the branch adds under the old paths.** Files a branch created under `assets/` were
gitignored, so they will not appear in a diff but may still exist in your working tree:

```bash
git diff --stat main...my-branch -- assets/ ; ls -la assets/
```

**Note any new `.cs` files the branch adds.** They carry `.cs.uid` sidecars (committed deliberately,
per `.gitignore`). The transform renames directories, not UIDs, so the sidecars follow their files and
need no special handling — but a merge conflict *inside* a `.uid` file means two branches generated a
UID for the same path. Take either side; they are opaque identifiers.

## What the script does

`tools/migrate-branch.sh` is deliberately dumb and re-runnable:

1. `git mv` the six `src/VortexArena.<X>` directories and the `.sln` / `.csproj` files (T2).
2. `sed` the namespace, `RootNamespace`, and `AssemblyName` strings across `*.cs`, `*.csproj`, `*.sln`,
   `project.godot` (T1, T2).
3. `sed` the path literals: `assets/data` → `data`, `res://assets/data` → `res://data` (T3).
4. `sed` the env-var and artifact names (T4).
5. `sed` `bryankruman/VortexArena` → `VortexFPS/VortexArena` in URLs (T5).
6. Print anything it could not classify, rather than guessing.

It does **not** touch `data/` itself. Content arrives from `main` in the merge.

## After the merge

Work through these in order. Each one catches a different class of miss.

- **Build clean.** `dotnet build` after deleting `obj/` and `bin/`. The Roslyn generators in
  `VortexArena.SourceGen` emit namespaces as string literals, so a stale generated file is the classic
  post-rename break and an incremental build will hide it.
- **Run the suite.** `dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj`.
- **Fetch content and run the real gate.** `tools/data/fetch-maps.py` then `ci/ci.sh`. Asset-dependent
  tests no longer self-skip, so a path the transform missed shows up here rather than at runtime.
- **Grep for survivors.** `grep -rn "VortexArena\|assets/data\|XG_DATA_DIR" --include='*.cs'
  --include='*.csproj' --include='*.sh' --include='*.yml' .` Expect zero outside
  `planning/legacy/` and upstream-lineage comments, which stay per `docs/REBRANDING.md`.
- **Check the branch's own docs.** Design notes and briefs under `planning/` often hardcode old paths.
  They are not load-bearing, but they mislead the next reader.

## Branch-specific notes

- **`feature/map-editor`** — **landed before the restructure, so it does not come through here.** Its 26
  `Vmap*` files are on `main` and were migrated with everything else. Listed only because branches cut
  from it before it landed still need this playbook. See restructure §9 for how `.vmap` fits the new
  layout, and the open `vmap_publish` item there.
- **`feature/launcher-updater`** — being extracted to its own repo (restructure stage 6). Do not
  migrate it in place; migrate it as part of the extraction, and repoint its `<hash12>` key at
  `git rev-parse HEAD:data`.
- **Branches touching `download-assets.sh`** — that file is deleted. Their changes to it are dropped,
  not merged. Check whether the intent survives in `tools/data/fetch-maps.py`.
- **Branches with hardcoded test paths** — any test the branch added likely copied the
  `private const string DataDir = @"C:\Users\Bryan\..."` pattern (G13). The transform does not fix
  those; point them at the shared `TestPaths` helper by hand. The helper landed before the restructure
  (Stage −1), so it is already on `main` when you merge.
- **Branches that edit a `*-xonotic.cfg`** — those edits do not survive. D8 (restructure §11) keeps the
  Xonotic config files byte-identical to upstream and puts all divergence in `vortex-*.cfg`. Re-express
  the branch's intent as an assignment in the matching layer file (`vortex-physics.cfg`,
  `vortex-balance.cfg`, `vortex-binds.cfg`, …), and use plain `set` rather than `seta` unless the cvar
  is genuinely meant to be archiveable. Do not rename the provenance comments that cite upstream cfg
  filenames — those stay.
- **Your git remote** — the repo moved to the `VortexFPS` organization. Run `git remote set-url origin
  git@github.com:VortexFPS/VortexArena.git` on any checkout predating the transfer. GitHub redirects,
  so a stale remote keeps working rather than failing loudly, which is how it goes unnoticed.

## If a branch is small enough

Under roughly five commits and not touching `src/`, it is usually faster to read the diff and reapply
it by hand onto a fresh branch off the new `main` than to run any of the above. Use judgement; the
machinery here exists for the large branches.
