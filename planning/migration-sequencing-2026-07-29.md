# Remaining migration work: dependency graph and execution order

**Date:** 2026-07-29 · **Source:** a 14-agent parallelization workflow — 5 independent tracks, 4
sequential-stage recons each adversarially verified, then synthesised into one order.

> **Produced by subagents and not fully re-verified by hand.** Its headline finding (F0 — a broken
> `TestPaths.HasMaps` probe that silently disabled the map-dependent assertions) was confirmed and fixed
> immediately, which is a point in its favour. But every file count in it is measured against a tree that
> moved during the run, and the document says so itself: re-measure anything you are about to act on.
> All four recon groups came back with problems found, so read the per-group caveats rather than the
> summary line.

---


Single engineer, serial unless stated. Everything below re-verified against the working tree at `ede208c` (2026-07-29 22:17) unless marked **[inherited]**, which means I am relying on a sub-report's measurement and did not reproduce it.

**Tree moved during recon.** `HEAD` was `97e988d` when the tracks were dispatched and is `ede208c` now — the `.pk3`-instead-of-extracted switch landed mid-recon. That commit is the direct cause of finding **F0** below, and every file count in the sub-reports is as-of a moving target. Re-measure any count you are about to act on.

---

## 0. F0 — FIXED 2026-07-29, left here for the reasoning

> Resolved in the commit following this workflow: `ResolveHasMaps` now probes
> `data/maps/*.pk3`, and `TestPathsTests` gained the missing `HasMaps` conditional (proven
> to fail by re-breaking the probe). Suite green at **3,919 with the map assertions
> actually active**. Do not redo it; the diagnosis below is worth keeping.

`tests/XonoticGodot.Tests/TestPaths.cs:75-94` `ResolveHasMaps()` has three probes and the current layout matches none:

- `:81-84` recursive `*.bsp` under `data/maps` — the BSPs are inside the zips. Verified: `find data -name '*.bsp'` = 0.
- `:88-90` `*.pk3` at the `data/` root, `TopDirectoryOnly` — the 32 packs are one level down. Verified: `ls data/maps` = 32 `.pk3`; `data/*.pk3` = none.
- `:93` loose `*.bsp` anywhere under `data/` — 0.

So `TestPaths.HasMaps` is **false with all 32 packs installed**. `ede208c` did not touch `TestPaths.cs` (verified: its last commits are `0baec7d`, `b7e1700`). Because a false `HasMaps` only *lowers* thresholds and *skips* assertions, nothing goes red — `AssetParserTests.cs:29,57`, `ShaderPreviewTests.cs:191,204`, `VisualQaTests.cs:355`, `VmapTextFormatTests.cs:437`.

This poisons the gate of **every other batch in this document**, and it is worse than a lost assertion: `ede208c`'s own commit message reasons from a post-switch suite count of 3,918 as though the map-dependent half were active. Fix `TestPaths.cs` first, add the missing conditional to `TestPathsTests.cs` (it guards `Data` at `:31-65` and `CorePk3Dir` at `:68-77` but has no `HasMaps` guard), and only then trust any `dotnet test` result in this plan.

---

## 1. Dependency graph

```
                          ┌─────────────────────────────────────┐
                          │ F0  TestPaths.HasMaps .pk3 probe    │
                          │     + TestPathsTests conditional    │  ← DO THIS FIRST
                          └──────────────┬──────────────────────┘
                                         │ (every dotnet-test gate below)
      ┌──────────────────┬───────────────┼──────────────────┬──────────────────┐
      │                  │               │                  │                  │
┌─────▼─────┐    ┌───────▼──────┐  ┌─────▼──────┐   ┌───────▼──────┐   ┌──────▼──────┐
│ BATCH-4   │    │ BATCH-6AB    │  │ BATCH-3    │   │ BATCH-1M     │   │ BATCH-7D    │
│ Stage 4   │    │ Stage 6      │  │ Stage 3    │   │ VortexMaps   │   │ ADR drafts  │
│ engine    │    │ items 38,39  │  │ (serial    │   │ item 7/7b    │   │ (write now, │
│ template  │    │ (prep only)  │  │  inside)   │   │ + doc fixes  │   │  land later)│
└─────┬─────┘    └──────┬───────┘  └─────┬──────┘   └──────────────┘   └──────┬──────┘
      │                 │                │                                    │
      │ unblocks B1's   │ needs repo     │                                    │
      │ gate on CI      │ created        │                                    │
      │                 │                ▼                                    │
      │                 │        ┌───────────────┐                            │
      │                 │        │ BATCH-5       │                            │
      └─────────────────┼───────►│ Stage 5       │                            │
                        │        │ rename        │                            │
                        │        └───────┬───────┘                            │
                        │                │                                    │
                        └────────────────┼────────────────┐                   │
                                         ▼                ▼                   │
                                  ┌──────────────┐  ┌──────────────┐          │
                                  │ BATCH-5.4    │  │ BATCH-6C     │          │
                                  │ launcher     │  │ item 40      │          │
                                  │ name consts  │  │ (BLOCKED)    │          │
                                  └──────┬───────┘  └──────────────┘          │
                                         │                                    │
                                         └──────────────►┌───────────────◄────┘
                                                         │ BATCH-7L      │
                                                         │ ADRs land +   │
                                                         │ citation pass │
                                                         └───────────────┘
```

### Edges, with the constraint that creates each

**F0 → all** — `TestPaths.cs:75-94` vs `data/maps/*.pk3`. Not a preference: five assertions are currently off and cannot report it.

**Stage 3 internal order (hard, this is the only serial chain in the plan):**

1. **19+24+EffectInfo atomic.** `game/Shell.cs:35` and `game/DataPaths.cs:36` both default to `"res://assets/data"`; `tools/package.sh:171,173` write the tree to `.../assets/data`. Item 19 names only `ASSETS_SRC` at `:30`. Change one side and it fails **silently**: `DataPaths.ResolveExported` (`:68-84`) returns the exe-relative path even when nothing exists (`:83`, comment: "so the loader logs a sensible 'mounted …' path even on a broken install"), and `MountContentRoot`'s failure surfaces as `Log.Warn`, which `ci/ci.sh`'s `^ERROR:|SCRIPT ERROR|Unhandled exception` regex does not catch. Add `game/client/EffectInfo.cs:107,115` and `game/client/particles/EffectInfoOverlay.cs:148,156` — both hardcode `Path.Combine(projectDir, "assets", "data")` **and** `$"xonotic-data.pk3dir/{rel}"`, so both path segments are wrong post-restructure. Verified. Item 24 names neither file. Note `ResolveExported` needs **no** edit — it is fully parameterised on `rel` (`:73-75`), so item 24's second sentence describes an edit that does not exist.
2. **22 + 28b + `parity-cvar-diff.py`** before the junction dies. `ci/ci.sh:62,83,122` (three guards, not four) test `[ -d "$ROOT/assets/data" ]`; `tools/parity-asset-check.py:35` is `DATA = ROOT / "assets" / "data"` with a hard `sys.exit`; `tools/parity-cvar-diff.py:39` is `ROOT / "assets" / "data" / "xonotic-data.pk3dir"`. All verified.
3. **18 + `assets/` deletion + `.gitignore:48-52`** last. `tools/package.sh:94-98` still invokes `download-assets.sh`; `release.yml:78` does too. Delete the script first and packaging and the release workflow break on the next tag.
4. **20/21 with or after 19** — same `download-assets.sh` call reason.
5. **28f (D8 config layer) can go anywhere in the window**, and should go *early* — see the Stage-5 edge.

**Stage 3 → Stage 5 (gate dependency, not code).** Stage 5's proof runs through `ci/ci.sh`, and `ci/ci.sh:83` gates the headless host smoke — the **only** step that can detect a stale `project.godot:27 assembly_name` — on `assets/data`. Worse, the recon's gate opens with `git clean -xdf`, and `assets/data` is an **untracked symlink** (verified: `assets/data -> /c/Users/Bryan/Projects/Vortex/Base/data`; `git ls-files assets/` returns only `.gdignore`). So the Stage-5 gate as written deletes its own teeth. Stage 5's *code* could technically land first; I am ruling that out because the alternative is landing 1,026 files behind a gate that self-skips.

**D8 (28f) → Stage 5 (do D8 first).** D8 appends to `src/XonoticGodot.Common/Config/ConfigLoader.cs:69` (verified: `=> Load(cvars, readFile, ServerEntry, NotificationsEntry);`) and `game/menu/framework/MenuState.cs:165, 187, 265` and deletes `:204-205`. Stage 5 sweeps namespaces across both files. A 4-line content append rebased onto a 1,026-file mechanical sweep is a worse merge than the reverse. Also both D8 and stage3-data-docs edit `tools/parity-cvar-diff.py` (`:39` path, `:44` `ENTRIES`) and `planning/parity/cvar-diff-known.yaml` — **one owner, one commit**.

**Stage 4 ∥ Stage 3 (genuinely parallel).** Three stages touch `export_presets.cfg` on disjoint lines, verified: item 23 → `exclude_filter` at `36/86/120/154`; Stage 4 → `custom_template/release` at `49` (the only preset with one; `99/133/171` are `""`); item 34 → `export_path` at `37/87/121/155`. Stage 4 reads nothing under `data/`. The coupling is not the merge — it is that one editor save destroys all three plus the 24-line comment header (G9). Land **one** pinning test class covering all three keys.

**Stage 4 → Stage 3's gate on CI.** `export_presets.cfg:49` is `C:/Users/Bryan/Projects/Vortex/godot-4.6.3-inputfix/bin/...` — an absolute path on one workstation. Until Stage 4 lands, the B1 packaging gate is runnable **only on Bryan's box**. This edge is not in the plan and it is the reason to run Stage 4 early rather than late.

**Stage 6 items 38/39 ∥ everything.** `launcher/` is not on `main` (verified: `git ls-files launcher` = 0; the tree exists only on `feature/launcher-updater`). Nothing in the game repo changes. Blocked only on repo creation, and only for the push.

**Stage 5 → 5.4 launcher constants (must reach players in the same release).** `PlatformKey.cs:36-39,50` and `ReleaseFeeds.cs:106` on the launcher branch **[inherited]**. Plus `LauncherConfig.cs:6` = `bryankruman/XonoticGodot`, which is stale *today* against `origin git@github.com:VortexFPS/VortexArena.git` — so Stage 0's URL sweep did not reach the launcher branch, and the feed slug fails before the artifact names ever matter.

**Item 40 → the unlanded pipeline half of `3e6791f`.** Verified on `main`: `tools/make-manifest.py` does not exist; `grep -c core tools/package.sh` = 0; `.github/workflows/release.yml` has exactly one `hashFiles` occurrence, the cache key at `:74`. **The line item 40 rewrites is not in the repository.** This is not an ordering preference — there is no file to edit. Stage 6 needs a fourth item: land the pipeline half first.

**Stage 7 → Stages 3/4/5 (citations only).** The ADR drafts hardcode verified line numbers in exactly the files those stages move. Write now, land after, budget one citation-refresh pass.

---

## 2. Critical path

```
F0 → 28f(D8) → 19+24+EffectInfo → 22+28b+parity → 18+assets/ delete → 20/21 → 28c/28d
   → Stage 5.1 (move) → Stage 5.2 (sweep, ATOMIC) → Stage 5.3 (artifacts) → 5.4 (launcher)
   → Stage 7 land + citation refresh
```

**Length is dominated by Stage 5.2, and it cannot be shortened by parallelising.** The generator matches marker attributes by **metadata name string**, never by symbol identity — `GeneratorHelpers.cs:82-88` (verified: seven `*AttributeName` constants, e.g. `:82 "XonoticGodot.Common.Framework.WeaponAttribute"`), consumed via `ForAttributeWithMetadataName`. So the 1,026 `.cs` namespace edits (verified: `git grep -lI XonoticGodot -- '*.cs'` = 1,026 — item 31's "six src/ projects" understates this by roughly 2x), the 16 `GeneratorHelpers.cs` literals (`:69`, `:76`, `:82-88`, `:159-165` — 16, not 17), and the `SourceGenTests.cs` stub literals must be **one commit**. There is no intermediate state that both compiles and registers content.

And the failure mode of getting it half-right is the worst in the whole restructure: the partial rename **builds clean with zero diagnostics** and empties all seven gameplay registries at runtime **[inherited, but proven by running the real generator DLL]**. The only tripwire is one test class — `SourceGenTests.cs:287-293` parity plus `:304-310` count floors. Everything else about Stage 5 is grep-able; this is not.

Secondary contributors to path length, in order: the Stage-3 serial chain (four commits that cannot be reordered, each needing a real export to prove); the export-and-package gate turnaround; and the citation-refresh pass on Stage 7.

**Not on the critical path and should not be allowed onto it:** Stage 4, Stage 6 items 38/39, VortexMaps item 7/7b, the ADR drafting.

---

## 3. Startable right now, zero coordination

Ordered by value. Every one is self-contained, verified, and needs no decision.

| # | Work | Why it is free |
|---|---|---|
| 1 | **F0** — `TestPaths.cs:75-94` add a `data/maps/*.pk3` probe; fix the stale docs at `:70-71` and `:80` (they still describe the extracted layout); add the conditional to `TestPathsTests.cs` | Reads only the test project. Unblocks every other gate. |
| 2 | **Item 26 pinning test**, extended to cover `project.godot` `run/delta_smooth` (`:22`), `physics_ticks_per_second` (`:56`), `physics_jitter_fix` (`:58`) **and `max_physics_steps_per_frame` (`:57`)**, plus `export_presets.cfg`'s three hand-maintained keys, plus `cl_engine_jitterfix`'s registered default | Needs no content tree; runs on a bare CI checkout. Must be a **text parse** — the tests csproj references no GodotSharp, so `ProjectSettings`/`Engine.*` are unreachable. |
| 3 | **Item 23** — `exclude_filter="assets/*"` → `"data/*"` at `export_presets.cfg:36,86,120,154`, hand-edited | Verified disjoint from Stage 4's `:49` and item 34's `:37/87/121/155`. Inert either way today. |
| 4 | **`.claude/workflows/parity-diff.js:12-13`, `upstream-watch.js:12,14,15`, `_verify-only.workflow.js:7`** — dead `Projects/Xonotic/` absolute paths, interpolated into agent prompts | Broken *today*, before any restructure work. Not owned by any plan item. |
| 5 | **VortexMaps item 7/7b** — plan-doc corrections (item 7 is already done; line 126's "180 `.map`" is 150) and `build/compile-map.sh:40`'s missing `//` strip | Different repository, no overlap. |
| 6 | **Stage 6 items 38/39 prep** — `git worktree add` a scratch tree of `feature/launcher-updater`, `git subtree split -P launcher`, add the root `Directory.Build.props`, confirm the build/tests green | Verified `launcher/` is absent from `main`, so nothing here touches the game repo. Do **not** `git checkout feature/launcher-updater` in the main worktree — `main`'s tree is ~900 MB of `data/` and the branch predates it. |
| 7 | **ADR-0016/0017/0018 + the three amendments + the 0015 stub + five README index rows** as drafts | Verified: 14 `ADR-*.md` on disk, 13 index rows, and the missing one is **ADR-0014**, present since 2026-07-09. So Stage 7 must land five rows, not three. |
| 8 | **`tools/find-cvars.py:119-124`** and **`DeterminismTests.cs:260-262`** — both hardcode `src/XonoticGodot.*` directory prefixes that Stage 5 relocates | Verified. `DeterminismTests.cs:266` is `if (!Directory.Exists(dir)) continue;` and `:275` asserts `offenders.Count == 0`, so after the move the non-deterministic-API guard scans nothing and passes. `find-cvars.py:193-196` falls through to `return "host"`, so every server/shared/net cvar collapses. Both are **path strings, independent of the namespace sweep** — pull them out of Stage 5.2 and land them now, or Stage 5.1 is a "clean bisect checkpoint" that is green because two guards are dead. |
| 9 | **`.gitattributes`** — add `*.patch text eol=lf` and renormalize | Verified: on-disk `bf25ca8b…` (CRLF), committed blob `be0a415f…` (LF), and `.gitattributes` declares rules only for `*.sh/*.ps1/*.cmd/*.bat`. Without this, Stage 4's lockfile hash is platform-dependent. |

**Not free, despite looking it:** `tools/upstream-watch.py:57` `XG_BASE_DIR` → `VA_BASE_DIR`. The two names are **one directory level apart** — `XG_BASE_DIR` means `<…>/Base` (`:65` appends `"data"/"xonotic-data.pk3dir"`), `VA_BASE_DIR` means `<…>/Base/data` (`TestPaths.cs:126`, fallback `:135`). Renaming blind *manufactures* the collision it claims to fix. Needs a level adjustment or a distinct name.

---

## 4. HUMAN DECISIONS AND EXTERNAL INPUTS — nothing downstream of these can be finished

### External inputs (someone must create/upload something)

| Blocks | Needed |
|---|---|
| Stage 6 push (items 38/39) | **`VortexFPS/VortexLauncher` does not exist.** Prep is fully doable locally first. |
| Stage 4 lockfile `urls` + the Windows release job | **No `engine-4.6.3-pr109639` release asset.** The repo has one release and one tag (`v0.1.0-alpha`) **[inherited]**. The 67 MB template is a dangling pin until uploaded. |
| Item 40 entirely | **The pipeline half of `3e6791f` must land on `main`.** Verified absent. |
| Item 18's submodule wrapper | **No `.gitmodules`, no `maps-src`** (verified). Item 18's "thin `git submodule update --init maps-src` wrapper" is dead scope — writing it now ships a script that fails for every caller. Item 20's `submodules: false` is defensive only. |

### Decisions

1. **`g_mod_physics`.** `physicsX.cfg:1` sets it `"Xonotic"`; the lost hand-edit changed it (it is suppressed at `cvar-diff-known.yaml:13-14`, verified). §11.4's three lines do not. Leaving it advertises stock physics on a server whose step-up is capped. Product call. **Blocks 28f's final shape.**
2. **`warsow` in `g_physics_clientselect_options`.** Verified `data/core.pk3dir/physics.cfg:10` = `"xonotic nexuiz vecxis quake quake2 quake3 cpma bones xdf"` — no `warsow`, no `bryan`. The D8 track appends both (citing `planning/parity/registry/physics-player.yaml:214` as a lost live parity unit); the Stage 7 track appends `bryan` only (upstream deliberately excludes all three `physicsWarsow*` presets). **Two of your own tracks disagree.** Settle before writing `vortex-physics.cfg`.
3. **Does §11.4's video move belong in 28f?** It is the only part of D8 that changes runtime behaviour outside physics (bare-run window mode, needing a compensating `Register("vid_fullscreen","2")` in `ClientSettings.cs`) and the only part that adds *new* parity-diff rows rather than restoring lost ones. Splitting keeps 28f's diff purely "restore what was lost".
4. **ADR-0015 §7 vs plan §5.4 — one release train or two.** §7 mandates launcher Velopack packages on the same `v*` release as the game, and `SelfUpdateService.cs:16` implements it off the same `LauncherConfig.RepoUrl` the game feed uses **[inherited]**; §5.4 asserts an independent cadence. Whether `LauncherConfig` gets one repo constant or two. **Blocks item 39's ADR rewrite.** Settle together with the release-continuity cutover, since the feed host and every artifact filename change in the same event.
5. **`res://` vs bare repo-relative for `custom_template/release`.** Both verified to work **[inherited]**; `res://` is CWD-independent, bare-relative is the only form that would resolve the Windows exporter's sibling-DLL lookups if a future template ever ships DLLs. The lockfile's `path` field and the assertion both hard-code the choice.
6. **Upload the existing local template (hash known, `cc0660d5…`) or build on CI first** (hash unknown until the run finishes, lockfile lands in two steps). Godot builds are not reproducible, so the lockfile pins *a binary*, not a recipe.
7. **Amend or supersede ADR-0006/0008/0014.** `planning/decisions/README.md:3-4` says "Once **Accepted**, an ADR is immutable" — which forbids exactly what Stage 7 does, while ADR-0014 has carried an in-file `## Update` since 2026-07-09. Soften the rule, or all three amendments become new ADRs whose content is "the earlier one drifted".
8. **`~/XonData` → a Vortex name.** `XONOTIC_USERDIR` is in 12 tracked files and `XonData` in ~28. Item 27 says the rename lands "alongside the Tier-0 `XONOTIC_USERDIR` → `VORTEX_USERDIR` rename"; item 36 defers "campaign id, hostname defaults, macOS bundle id" and does **not** mention `XonData`. **Named by no item and deferred by no item.**
9. **`docs/REBRANDING.md:79-82` vs its own Decision 3 at `:294-320`.** `:79` says artifact filenames and `project/assembly_name` are "Kept as-is … not player-facing brand"; Decision 3 folds them into Tier 1, which is what items 32/34 execute. An executor reads `:79` first because it is in the file's change table. **Reconcile the wording before Stage 5.**
10. **`planning/parity/` — in scope for the Stage 5 path move or not?** 343 files under `planning/parity/` carry the token (verified), most as `src/XonoticGodot.*/….cs:LINE` citations that item 31 invalidates wholesale. Item 28b classifies parity registry/spec files as "machine-checked data … so it moves with the code". The Stage 5 recon neither lists them nor excludes them. **An executor gets no instruction in either direction.**
11. **Analyzer diagnostic IDs `XG0001`/`XG0002`.** 10 + 14 occurrences across 4 + 11 files, including `WarningsAsErrors` at `XonoticGodot.csproj:22` and seven explanatory comments in shipping `game/` code. Item 27's regex `\bXG_[A-Z_]+|\bXg[A-Z][A-Za-z]+` matches neither. **Decide or record as out of scope** — right now it is neither, and the prescribed gate reports clean with them untouched.
12. **`XgBotPlayer`/`XgDebugUnoptimized`/`XG_BOTPLAYER` are double-booked** between item 27 (Stage 3) and item 35a (Stage 5), both on `Directory.Build.props`. Pick one stage.
13. **`textures/stormkeep/lava.xcf`** (1.04 MB layered GIMP source, the only source file in either pack that `VortexMaps/sources/` lacks, on the legacy Nexuiz path). Route it in or record it as legacy — and confirm Nexuiz-era licensing first, since `data/licenses/` + CREDITS would need the entry.
14. **`build/map-compiler-config.pl`'s second `-fs_basepath`.** Measured unnecessary for the 31 stock maps; the call depends on whether third-party maps are expected to reference core content.
15. **`TODO-FONTS`** — DejaVu and Nimbus Sans L ship with no notice **[inherited]**. Recorded as a **release blocker**, so it gates the first VortexArena-named release, which is also the launcher cutover.

---

## 5. Verifier verdicts — safe as reconned, or rework first

| Group | Verdict | Rework required before executing |
|---|---|---|
| **stage3-build-ci** | **NEEDS REWORK** | Gate (1) cannot run — `dist/` does not exist and `package.sh:80-91` hard-fails on the missing marker; it needs `rm -rf dist` plus a full `ci/ci.sh --export` (itself needing the workstation-only template at `export_presets.cfg:49`). Gate (3) is blind: the suite reports 3,918/3,918 **[inherited]** both before and after the F0 fix, and `grep -c "maps present: False"` sees only `ShaderPreviewTests.cs:207`'s skip path — the other three sites embed the string in `Assert` messages that never print green. T12's dangling-reference count is ~13, not 7 (`git grep -c download-assets` = 20 files), and omits `.gitignore:48`, `docs/REBRANDING.md`, `tools/data/fetch-maps.py:29`, and the three sibling scripts. The item-20 ordering constraint is **spurious** — the only `actions/cache` step is inside the job item 20 deletes, so the two keys cannot coexist; do not split that commit on its advice. The T5 rationale is false (the plan *does* record the least-privilege change at `:1169-1177`) even though the advice is right. |
| **stage3-runtime** | **NEEDS REWORK** | The `XG_BASE_DIR` → `VA_BASE_DIR` rename as specified introduces a bug (level mismatch, §3 above) — and it is the one item flagged "correct on its own today, land it first". The `Rationale_Comments_Survive` test as exemplified covers block 3 and one line of block 1, leaving block 2 (`project.godot:31-37`) and all four `[rendering]` blocks unmarked; the prescribed teeth-proof (delete `:39-55`) exercises only block 3. `tools/wobble-detect.py:76-105` is a **third** reader of the pinned timing values with Godot's stock defaults hardcoded at `:88` as the absent-key fallback — so a pruned key silently mis-scores every wobble capture, which contradicts the "treat absent `max_physics_steps_per_frame` as 8" advice. Guard count is 3 (`:62,83,122`), not 4. Cite errors: `WarningsAsErrors` is `XonoticGodot.csproj:22`; `ClientSettings.cs` is at `game/menu/framework/`; `GeneratorHelpers` has 16 literals, not 17. |
| **stage3-data-docs** | **NEEDS REWORK** | The cvar-diff proof and its gate are a **tautology**: `assets/data` is a symlink to `Base/data` (verified), so `PORT_DATA` and `BASE_DATA` resolve to the same directory and "identical to the pre-edit run" proves nothing. The pinned "6013 cvars, 0 value diffs" additionally **certifies the stale suppression file as correct** — `cvar-diff-known.yaml:8-22` still describes `physicsBryan.cfg` chain divergence that §11.5 mandates replacing (verified: 5 entries, stale header). It also forbids the correct fix, since adding `vortex-common.cfg` to `ENTRIES` at `:44` moves the count. The asset-check gate is unsatisfiable pre-fetch (`missing=14 / mounts=6` before `fetch-maps.py`). `git ls-files | xargs grep` word-splits on the four `.run/Release Build*.run.xml` paths — needs `-z`. The survivor sweep run verbatim returns 85 files including the plan itself. Missed: `planning/parity/_wave1-seams.md`, `planning/warpzone-base-vs-port-audit-2026-06-15.md`, `docs/RUNNING.md:318`, `README.md:112`. |
| **stage5-rename** | **NEEDS REWORK — do not run its gate** | The gate is **unsatisfiable by construction**: its `git grep -lI XonoticGodot -- '*.cs' project.godot export_presets.cfg …` clauses cover the Tier-0 exclusion list the same document declares non-negotiable, and the clauses are `&&`-chained, so `echo "RENAME PROVEN"` is unreachable even after a perfect rename. Verified: that grep hits `project.godot` (`:13`), `export_presets.cfg` (`:64,65,175`), `Cvars.cs:400`, `NetGame.cs:75,496,509`, and ~12 more. `git clean -xdf -e _scratch -e data/maps` **deletes the `assets/data` symlink**, taking out all three `ci/ci.sh` guards including the host smoke — the only step that can catch a stale `project.godot:27`. The same clause deletes `uid-inventory-baseline.txt`, and `sed 's#.*/##'` reduces the diff to basenames, so a sidecar landing in the wrong directory compares equal. The 5.3 artifact list omits `run-release.sh:23,26`, `run-release.ps1:13`, `tools/run-client.sh:19,28`, `tools/run-dedicated.sh:26,35` — all verified, and the last two ship inside the player zips (`package.sh:184,187`). All four `.run/*.run.xml` do carry zero occurrences (verified) but all four invoke `run-release.sh`, so the "not in scope" conclusion is true of the XML and false of the outcome. `SourceGen.csproj` is referenced from **three** csproj, not two (`tests/…/XonoticGodot.Tests.csproj:25`, a deliberate non-analyzer reference). `LauncherConfig.cs:6` is stale today. |
| **Stage 6 track (items 38/39)** | **SAFE as written** | Its own three verifications were run and reported: split fidelity to tree `1e89deae…`, standalone build green with the new root props (20/20 tests), and the tree-SHA key exact and stable over 15 commit pairs. Use `git rev-parse -q --verify HEAD:data` — bare `rev-parse` echoes its argument on stdout and exits 128, so `full=$(…)` yields the literal `HEAD:data`. |
| **Stage 6 item 40** | **NOT EXECUTABLE** | Verified: no target on `main`. |
| **Stage 4 track** | **SAFE to execute**, blocked on decisions 5/6 | Its own probes were run and reported (`res://` accepted, empty field silently substitutes the stock template with zero warnings, the `.exe` is not packed into the pck, headless export does not rewrite `export_presets.cfg`). I independently confirmed the `.gitattributes`/CRLF hash split — which is the finding that makes the lockfile a trap rather than a pin, and it must land in the **same commit** as `engine.lock.json`. |
| **D8 / 28f track** | **SAFE to execute**, blocked on decisions 1/2/3 | I independently confirmed its central correction: every `MenuState.cs` line number in §11.2/§11.4/G15 is **+3** (`:165` config chain, `:187` binds, `:265` scratch binds, `:211` `LockDefaults`, `:230` `LoadUserConfig`, `:204-205` video), that `physics.cfg:10` lacks both `warsow` and `bryan`, that `xonotic-server.cfg:675` is `exec physicsX.cfg`, and that `ConfigLoader.cs:69` is the two-entry call. Match on code text, not on line numbers. |
| **Stage 1 item 7 / 7b** | **SAFE — and item 7 is already done** | Its own measurements were run. Two live-file fixes plus plan corrections. Its "benign" verdict on the `//` leak is from source reading, not execution — no `q3map2` binary exists in this checkout. |
| **Stage 7 track** | **SAFE as drafts**, blocked on decision 7 | I confirmed 14 ADR files / 13 index rows / ADR-0014 unindexed / no ADR-0015 on `main`. |

---

## 6. Gates

One command per batch. Corrected where the verifiers found the recon's gate unsound.

**F0**
```bash
dotnet test tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj -c Debug \
  --filter "FullyQualifiedName~TestPathsTests|FullyQualifiedName~ShaderPreviewTests" \
  --logger "console;verbosity=detailed" 2>&1 | tee /tmp/f0.log
! grep -q "maps present: False" /tmp/f0.log && grep -q "resolution-rate" /tmp/f0.log
```
The negative grep alone is insufficient (only one site prints it). The new `TestPathsTests` conditional — *if `data/maps` holds any `.pk3`, `HasMaps` must be true* — is what actually has teeth; the second grep confirms `ShaderPreviewTests.cs:204`'s 85% assertion now executes instead of skipping.

**BATCH-4 (Stage 4)**
```bash
python tools/data/fetch-engine-template.py --verify-only && ci/ci.sh --export
```
Then prove it has teeth: blank `export_presets.cfg:49` and confirm the verify **fails**. Without that step you have not tested G10, because a blank field produces a byte-identical stock binary with zero errors and zero warnings.

**BATCH-3 step 1 (items 19+24+EffectInfo)**
```bash
rm -rf dist && ci/ci.sh --export && bash tools/package.sh --no-zip windows-client \
  && test -d dist/windows-client/data/core.pk3dir \
  && ls dist/windows-client/data/maps/*.pk3 >/dev/null \
  && test ! -d dist/windows-client/assets
```
`rm -rf dist` is load-bearing — `rsync --delete` at `package.sh:105` prunes only inside the new destination, so a stale `assets/` from an earlier run survives a correct edit. The decisive proof is the **host** smoke's three greps (`[MapLoader]`, `waypoints for`, `handshake accepted`), not the plain `--quit-after 200` smoke, which passes over a completely unmounted tree. Precondition: Godot 4.6.3 mono templates **and** the custom template at `:49` — i.e. Bryan's box only until Stage 4 lands.

**BATCH-3 step 2 (items 22+28b+parity)**
```bash
python tools/data/fetch-maps.py \
  && python tools/parity-asset-check.py \
  && python tools/parity-cvar-diff.py
```
`parity-asset-check.py` must report `mounts` ≥ 38 and a finding set **bit-identical** to a baseline regenerated on the *old* layout first (the committed `ASSET-CHECK.md` is 2026-07-02 and 27 days of unrelated drift stale). `mounts` is the only field allowed to move. For `parity-cvar-diff.py`, **do not** pin the pre-edit number — that baseline is a self-comparison because `assets/data` symlinks to `Base/data`. Pin the §11.5 *invariant* instead: Base-only files none, text-differs none, port-only files exactly the `vortex-*.cfg` set, and every value row sourced to a `vortex-*.cfg`.

**BATCH-3 step 3 (item 18 + `assets/` deletion)**
```bash
test ! -e download-assets.sh && test ! -e assets \
  && ! git grep -nI 'download-assets' -- ':!planning/wave-a*' ':!planning/legacy' \
       ':!planning/repo-restructure-2026-07-29.md' ':!docs/BRANCH-MIGRATION.md' \
       ':!COPYING' ':!data/licenses/README' ':!tools/data/fetch-maps.py' \
  && ci/ci.sh
```
Seven exclusions, not four: `COPYING:67-70`, `data/licenses/README:25`, `fetch-maps.py:29` and `docs/BRANCH-MIGRATION.md` are deliberate history, and the plan document names the script by design. After this, `ci/ci.sh` must run the host smoke **unconditionally** — if it prints any `NOTE: … skipping`, the gate is lying.

**BATCH-3 step 6 (28f, D8)**
```bash
python tools/parity-cvar-diff.py \
  && dotnet test tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj -c Debug \
     --filter "FullyQualifiedName~Config|FullyQualifiedName~PhysicsPreset|FullyQualifiedName~Binds|FullyQualifiedName~StepUpSpeed"
```
Baseline is 87 passed / 0 failed / 0 skipped **[inherited]**. The `FilesMissing == 0` assertion is the one that earns its keep — `ConfigInterpreter.Diag` writes only to an in-memory list, so a misspelled `exec` produces no log line anywhere. And do **not** wire the G15 config-persistence check to `--quit-after-seconds`: `MenuState.cs:99-108` suppresses config saving for that flag, so the check would pass vacuously. Use `--quit-after <frames>`.

**BATCH-5 (Stage 5)** — replaces the recon's gate, which cannot pass
```bash
BASE=/c/Users/Bryan/AppData/Local/Temp/claude/.../scratchpad   # OUTSIDE the tree
git ls-files '*.cs.uid' | sort > "$BASE/uid-after.txt"
git clean -xdf -e _scratch -e data/maps -e assets \
  && dotnet build VortexArena.sln -c Debug --nologo \
  && GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe" ci/ci.sh --export \
  && bash tools/package.sh --version rename-proof \
  && test 3 -eq $(ls dist/VortexArena-rename-proof-*.zip | wc -l) \
  && diff "$BASE/uid-before.txt" "$BASE/uid-after.txt" \
  && git grep -nI XonoticGodot -- '*.cs' '*.csproj' '*.sln' '*.props' project.godot \
       export_presets.cfg tools/ ci/ .github/ > "$BASE/residue.txt"; \
  diff "$BASE/tier0-expected.txt" "$BASE/residue.txt"
```
Four corrections to the original: `-e assets` (the symlink is untracked and its removal disables the host smoke); the uid baseline lives **outside** the tree and keeps **full paths** so a sidecar in the wrong directory is caught; the residue grep is diffed against an explicit expected Tier-0 allowlist instead of asserted empty; and the zip count is the only check on the artifact chain, because `package.sh:80-85` warns-and-skips a missing marker while `:87-91` fails only when *every* target is missing (and `release.yml:208` uses `if-no-files-found: warn` for macOS, so that platform can vanish silently). Two things this still does not prove and must be checked by hand: that `DeterminismTests.cs:260-262` scans a non-empty directory set, and that `tools/find-cvars.py` still classifies by scope (re-run it and diff `docs/reference/CVARS.md`). Both are better fixed pre-Stage-5 per §3 item 8.

**BATCH-6AB (Stage 6 items 38/39)** — in the extracted tree
```bash
test "$(git rev-parse <split>^{tree})" = "$(git rev-parse feature/launcher-updater:launcher)" \
  && dotnet build XonoticGodot.Launcher.Tests/XonoticGodot.Launcher.Tests.csproj -c Debug \
  && dotnet test XonoticGodot.Launcher.Tests/XonoticGodot.Launcher.Tests.csproj -c Debug --no-build
```
Expect tree `1e89deae613f6f1ad8247af2323e6c7f0d4f5682` and 20/20 **[inherited]**. The build clause is the gate that **fails** without the new root `Directory.Build.props`.

**BATCH-7L (Stage 7)**
```bash
python tools/check-adr-index.py
```
Currently fails with `ADR-0014 is on disk but has no row in README.md's index` **[inherited]**; must pass with 18 files / 18 rows / no broken relative links.

---

## 7. Where I am not confident

- **The 3,918 suite figure and the "0 skipped" baselines.** I did not run the full suite; both are inherited. Given F0, treat any suite count in the plan as measured with the map-dependent guards off.
- **Godot's acceptance of `res://` in `custom_template/release`**, the silent stock fallback, and the `Mismatching custom export template executable architecture: found "invalid"` diagnostic. All inherited from one track's probes. They read as carefully done (source cites plus a production-shaped export) but I ran no export.
- **Everything on `feature/launcher-updater`** except its existence and `LauncherConfig.cs:6`. Line numbers in `PlatformKey.cs`, `InstallService.cs`, `ReleaseFeeds.cs`, `SelfUpdateService.cs` and ADR-0015 §7 are inherited.
- **`export_presets.cfg`'s `exclude_filter` vs `data/.gdignore`.** The stage3-runtime recon asserts both "belt-and-braces only, because `.gdignore` already keeps Godot out" *and* "a wrong value fails silently as a ~900 MB pck" — mutually exclusive, and I could not settle it without running an export. Item 23's real risk level is undetermined. Its recommendation (assert exported artifact size rather than trust the filter) is right either way.
- **Whether `planning/parity/`'s 343 token-bearing files are in Stage 5 scope.** I verified the count and that item 28b's reasoning would pull them in. Whether that reasoning transfers from the path move to the namespace rename is a judgement I am leaving to decision 10, not making.
- **The `q3map2` `//`-leak verdict.** No binary in the checkout; source-read only.
- **`tools/perf-baselines/*.json` provenance paths and the `-Live` perf gate.** Flagged by the Stage 5 verifier, not independently checked. If the artifact rename breaks `tools/perf-run.ps1:61,69`, it breaks the instrument G7 still needs — and G7 (PNG decode CPU at map load) is the one Stage 2 gate recorded as still unmeasured.
- **`.github/workflows/ci.yml:1` is reported as double-encoded UTF-8-with-BOM.** I did not verify the byte sequence. If true, a scripted in-place sweep on Windows risks compounding it in a file no gate reads.
