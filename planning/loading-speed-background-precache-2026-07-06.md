# Loading speed — background / scope-limited precache analysis

**Status: IN PROGRESS (2026-07-06).** Phases 1 (persist caches across maps/servers) and 2 (warm the eager set
at game load, in the background) are **landed + verified** (see the implementation log below). This doc captures
the load-path map, the reusable infrastructure, the one architectural constraint that governs menu-time precache,
and a ranked/phased plan. Timings below are from a single **Debug (jit-opt)** capture and are caveated — a clean
**release-export** baseline is Phase 0 of the plan.

## Implementation log

- **2026-07-06 — Phase 2 (warm the eager set at game load) landed.** A plain menu boot now warms the
  MAP-INDEPENDENT eager set — every weapon v_/h_ model, the stock player-model roster (erebus + the 5 idle-warm
  models + the local pick), and the 36 combat sounds — into `MenuState.SharedAssets` in the background, via a new
  [MenuAssetWarmer.cs](game/client/MenuAssetWarmer.cs) that Shell starts on the menu-only boot path
  ([Shell.cs](game/Shell.cs) `StartMenuAssetWarm`, the `else` branch skipped by `--map`/`--host`/`--connect` so a
  scripted match isn't double-warmed). The heavy skeletal IQM parse runs OFF the main thread on the shared
  `BackgroundAssetStreamer` lane; weapon builds + sound decodes drain on a 1.5 ms/frame main budget — so the menu
  never hitches. Gated by `cl_warm_at_boot` (default 1) AND `cl_persist_asset_cache` (a warm is wasted if the match
  builds its own loader). Combined with Phase 1, the first match's precache then finds these already parsed +
  GPU-uploaded → cache hits → fast map load. `WeaponVModelPath` was made `internal` so the warmer hits the same
  cache key. **Latent bug found + fixed en route:** `Sounds.All` was empty at the menu (the sound catalog was
  registered only at `GameInit`/world boot, not at process boot like the weapon/item/gametype catalogs), so the
  first cut of the warm saw 0 sounds. Added `Sounds.RegisterAll()` (idempotent, `_done`-guarded) to
  `GameRegistries.Bootstrap()` ([Registries.cs](src/XonoticGodot.Common/Gameplay/Registries.cs)) and a
  `Sounds:` line to the boot banner. Verified: menu smoke warms `24 weapon models + 6 player models + 36 combat
  sounds` to completion with 0 errors and `Sounds: 223` at boot; `--map` boot still runs its own precache and
  skips the menu warm (no double work); full suite green. The first-map speedup is true by construction (the warm
  fills the same caches the per-map precache reads) — a live launch→menu→start-map playtest is the empirical
  confirmation. Follow-ups noted: the warmer keeps running (harmlessly, at Low priority) if a match starts
  mid-warm — could free it on match start; and pipeline (PSO) warm stays per-match (viewport-specific), so the
  first map still pays the (cheap) GpuWarmPass, now rendering cache-hit models.

- **2026-07-06 — Phase 1 (persistent shared asset cache) landed.** `MenuState` now builds ONE process-lifetime
  `AssetLoader` at boot ([MenuState.cs](game/menu/framework/MenuState.cs) `Boot`, exposed as
  `MenuState.SharedAssets`), and every match reuses it via a new injected-loader seam in `NetGame`
  ([NetGame.cs](game/net/NetGame.cs) `_Ready`: `_assets = (persist && _injectedAssets is not null) ? _injectedAssets
  : new AssetLoader(_vfs)`), threaded through `ConfigureClient`/`ConfigureListenServer` from
  [Shell.cs](game/Shell.cs). Gated by the new `cl_persist_asset_cache` cvar (default 1; `0` restores per-match
  loaders) registered in [ClientSettings.cs](game/menu/framework/ClientSettings.cs). Verified: the
  `scripts/*.shader` parse now runs ONCE at boot (`[AssetSystem] loaded 2439 shaders` appears pre-map, before the
  config line) and NOT again at map load (parse count = 1 on a `--map stormkeep` boot); menu + map headless smokes
  clean (0 errors); full suite 2959/2959. The cross-map cache-hit win is true by construction — the same
  `AssetLoader` instance is reused and teardown (`NetGame.Shutdown`) never clears its caches (only the transient
  predecode handoff) — with a live menu→match→match / server-switch playtest as the empirical confirmation.
  **Verified architecture (the design's load-bearing facts):** `ResolveMaterial`/`LoadTexture` cache by
  name/vpath (map-independent), while `MakeLightmapMaterial` builds per-surface materials bound to the per-map
  lightmap atlas and never enters `_materialCache` — so no map-specific GPU resource leaks between maps. Known
  memory note: each visited map's external lightmaps (`maps/<name>/lm_NNNN`) do accumulate in the persistent
  `_textureCache` by vpath — bounded, opt-out via the cvar, and a candidate for map-scoped eviction later.

Branch: `fix/improve-loading-speed`. Related memories: `bot-join-iqm-modelload-stutter`,
`debug-smoothness-2026-07-03`, `godot-pipeline-compile-internals`, `client-map-load-implemented`,
`graphics-settings-wiring-reality`. Playbook: `PERF-DEBUGGING.md`. Prior audit: `PERFORMANCE_REPORT.md` §1.2/§5.

---

## The question

> Can we improve loading speed without causing hitches — precache in the background while in the menu,
> limit what we precache to what we know we need, precache on a background thread so it doesn't disturb
> the main thread?

## Short answer

**Yes, meaningfully — and the two lowest-risk levers already have infrastructure in the tree; they're just
switched off or scoped conservatively.** Three levers, ranked by payoff ÷ risk:

1. **Limit eager precache scope (biggest, lowest-risk).** Today the default `cl_precache_all_weapons 1`
   forces warming **all 24 weapon v_ models** under the loading screen, plus 36 combat sounds and 10 player
   models. In the capture below that was **~11.5 s of a ~14.7 s Debug load**. A *smart* expected-only path
   (`ComputeExpectedWeapons()`) that warms just the map's actual `weapon_*` spawns + start loadout + active
   mutator set **already exists** and is fully mutator-aware — it's disabled by the default cvar value. This
   is the "limit what we precache to what we know we need" the question asks for, and it is a config +
   default-flip + a client-side wiring gap, not new machinery.

2. **Move precache earlier — menu-time prewarm (medium payoff, one real blocker).** The map is knowable at
   menu selection time (campaign pick / Create-Game map picker) with a 0.1–5 s idle window before "Start".
   The blocker is architectural: **asset caches are per-`NetGame`-instance**, and the `NetGame` doesn't
   exist during the menu (see "The constraint" below). Prewarming needs a process-lifetime cache layer
   first; without it, anything the menu warms is thrown away when the match's `NetGame` is created.

3. **Widen background-thread coverage (incremental).** A bounded worker lane + budgeted main-thread drain
   already exist (`BackgroundAssetStreamer`). More *parse/decode* can move off the main thread, but **GPU
   upload and pipeline warm are main-thread-bound by Godot** and cap how much of the load can ever leave the
   main thread. This is refinement, not a step-change.

The good news for "without hitches": the heavy work (eager precache, GPU warm) already runs **during the
loading screen**, which yields frames — so today's cost is *load duration*, not in-match hitches. Every
lever here either shortens that duration or moves it earlier; none of them re-introduce mid-match hitches
as long as the expected-set logic stays correct (a wrong expected set trades load time for a first-use
hitch — the documented tradeoff behind `cl_precache_all_weapons 1`).

---

## Measured baseline (⚠ caveated — Debug + contaminated run)

Command (worktree, stormkeep + 6 bots, 30 s, profiler on):

```
Godot_..._console.exe --path . --host stormkeep --gametype dm --bots 6 \
  --cvar cl_frameprofiler 2 --cvar cl_autopause 0 --quit-after-seconds 30
```

Milestones from timestamped stdout (`_scratch/loadtime_baseline.log`, seconds since process start):

| t (s) | Event | Δ from prev |
|------:|-------|------:|
| 4.70 | `[Shell] hosting listen server … map=stormkeep` (load begins) | — |
| 4.95 | `[AssetSystem] loaded 2439 shaders from 185 scripts` | 0.25 |
| 7.73 | `[MapLoader] 'stormkeep' materials … cells=31, surfaces=380` (render geo built) | 2.78 |
| **14.25** | `[NetGame] precached 24 weapon models, skipped 0 (lazy)` | **6.52** |
| **19.34** | `[NetGame] precached 36 combat sounds, 10 player models (rendered for pipeline warm)` | **5.09** |
| 19.42 | `[ClientNet] handshake accepted` (playable threshold) | 0.08 |
| 27.24 | `[IdleWarmer] asset warm queue drained — full asset set is hot` | 7.82 |

**Load begin → playable ≈ 14.7 s (Debug).** The two eager-precache phases (weapons **6.5 s** + sounds/models
**5.1 s** = **11.6 s, ~79 %**) dominate; map render-geometry build is only ~2.8 s.

**Caveats — do not quote these as the baseline:**
- **Debug (jit-opt) build**, not the release export. Release is materially faster; `PERF-DEBUGGING.md`
  requires the release export for representative numbers. The *shape* (eager precache = long pole) holds
  regardless.
- **Contaminated run:** a leftover game instance (PID 46304) held UDP 26000, so this process's listen server
  failed to bind (`could not start the listen server on UDP 26000 (port in use?)`) and its self-client
  bogus-joined the squatter (`netId 8`, server name `Xonotic  Server`) — the exact failure mode in the
  `net-join-two-instance-test` memory. The *asset-load* timings are still this process's own real work
  (map build + precache run regardless of which server the client attaches to), but the handshake/net
  numbers are meaningless here.
- `proc:other 514153 ms` in the profiler line is the single load-screen frame accumulator, not a per-frame
  cost — load-screen frames get no hitch trees (`debug-smoothness-2026-07-03`).

**Phase 0 of the plan is a clean release-export capture** (`tools/perf-run.ps1`) after killing strays, to
get the real per-phase budget.

---

## Current load architecture (what already blocks vs. what's already async)

Entry: `Shell.StartListenServer` / `Shell.ConnectToServer` → `NetGame._Ready()`
([Shell.cs:588](game/Shell.cs), [NetGame.cs](game/net/NetGame.cs) `_Ready`). A staged
`LoadingScreen` overlay ([LoadingScreen.cs](game/LoadingScreen.cs)) is painted *before* the blocking load
starts (`WaitForFramePainted`, [Shell.cs:640](game/Shell.cs)) and animates a progress bar via yields.

*File:line references below are largely from an exploration pass and are point-in-time — verify before
editing (the memories warn line numbers drift).*

**Main-thread blocking (critical path, ~phases 1–7 + connect):**
- BSP parse (VFS read + `BspReader`) — synchronous, no `Prof.Sample`.
- Collision build (`BspCollisionBuilder`) — synchronous.
- Entity spawn (`GameWorld.Boot`, gametype init + entity loop) — synchronous; has `start.*` Prof scopes.
- **Render geometry build** (`MapLoader.BuildMap`) — hybrid: bezier patches tessellate on `Parallel.For`
  workers, but face bucketing, lightmap atlas, and **all material resolve + texture GPU upload** run on the
  main thread. ~2.8 s here in the capture.
- Client collision, render/camera/HUD setup, music decode — synchronous.

**Already async / yielding (don't block, run under the loading screen):**
- `PrecacheWeaponModelsAsync` — off-thread IQM parse, yields every 4 weapons, then a GPU warm pass. **This
  is the 6.5 s long pole**, and it's scoped by `cl_precache_all_weapons` (see below).
- `PrecacheCombatSoundsAndModelsAsync` — sounds + player-model roster, yields per asset + GPU warm. **5.1 s.**
- `StartIdleWarmup` / `IdleWarmer` — deferred, ~1.5 ms/frame budget, spreads the long tail over ~first
  minute (announcer voices, alt player models). Drained at t≈27 s here.
- `GpuWarmPass` — offscreen 64×64 SubViewport sharing the **live World3D** (so it compiles the correct PSO
  variants — `godot-pipeline-compile-internals`), 4 frames, then frees.

---

## Reusable infrastructure (already built)

| Piece | Location (point-in-time) | Reuse verdict |
|---|---|---|
| **BackgroundAssetStreamer** — bounded worker lane (`Clamp(cores/4, 2, 4)` threads) + priority queue + 2 ms/frame main-thread drain | `game/client/BackgroundAssetStreamer.cs` | ✅ The spine for any background precache. Priority orders the worker phase too (live jobs overtake idle-warm). |
| **IdleWarmer** — FIFO, 1.5 ms/frame budget, main-thread dequeue | `game/client/IdleWarmer.cs` | ✅ For the low-priority tail; created per-`NetGame` today. |
| **GpuWarmPass** — offscreen PSO compile on the live World3D | `game/client/GpuWarmPass.cs` | ⚠ Main-thread + needs the live viewport/World3D. Can't run before the match scene exists. |
| **Skeletal parse cache** — `(IqmData, FrameGroups, AnimationLibrary, defaultClip)` keyed by vpath, **locked** for worker access | `AssetLoader.cs:98` (`_skeletalCacheGate`) | ✅ Thread-safe; the model long-pole's cache. **Instance field** (see constraint). |
| **DecodeBuffer.Pool** — process-wide `ConcurrentDictionary<int, ConcurrentBag<byte[]>>`, exact-size rent/return | `game/loaders/DecodeBuffer.cs:33` | ✅ **Static / process-lifetime** — survives match teardown. |
| **VFS** — volatile mount swap + `ConcurrentDictionary` resolve caches; per-mount reads serialized | `src/XonoticGodot.Formats/Vfs/VirtualFileSystem.cs` | ✅ Concurrent-read-safe across *different* pk3s; static-ish (menu-shared). |
| **Texture predecode handoff** — `_predecodedImages` `ConcurrentDictionary`, worker decodes → main uploads | `AssetSystem.cs:551` | ✅ The off-thread decode → main-thread `CreateFromImage` split. **Instance field.** |
| **Smart weapon set** — `ComputeExpectedWeapons()`, mutator-aware (instagib/overkill/nix/arena + map pickups + start loadout) | `NetGame.cs:2450` | ✅ **The scope-limit logic already exists**; gated off by `cl_precache_all_weapons 1`. |

---

## The constraint that governs menu-time precache

**Asset caches are per-`NetGame`-instance, not process-wide.** Verified at the instantiation sites:

- `NetGame._Ready` → `_assets = new AssetLoader(_vfs)` ([NetGame.cs:414](game/net/NetGame.cs))
- `AssetLoader` ctor → `_assets = new AssetSystem(_vfs)` ([AssetLoader.cs:118](game/loaders/AssetLoader.cs))
- The caches are plain instance fields: `_modelCache`, `_md3Cache`, `_soundCache`, `_skeletalParseCache`
  ([AssetLoader.cs:59–99](game/loaders/AssetLoader.cs)); `_materialCache`, `_textureCache`
  ([AssetSystem.cs:39–40](game/loaders/AssetSystem.cs)).
- Every match builds a fresh `NetGame` and `TeardownGame` `QueueFree`s it ([Shell.cs:527](game/Shell.cs)).

So a `NetGame`'s parsed models, materials, and GPU textures **die with the match**. The menu (`MenuState`)
shares only the VFS and the cvar store — it has no asset cache. **Therefore:**

- **What menu-time work pays off today, with no new plumbing:** anything landing in the *process-lifetime*
  layer — `DecodeBuffer.Pool`, the VFS resolve caches, and the OS file cache (warm the pk3 pages off disk).
  That helps the **I/O + decode** portion of the eventual load but **not** the GPU-upload / mesh-build /
  pipeline-warm portion, which re-runs in the new `NetGame`.
- **What menu-time work needs first:** a **process-lifetime asset cache** (or a cache handed from menu into
  the new `NetGame`) so parsed IQM / built materials / warmed pipelines survive into the match. This is the
  central design decision for lever #2, and the prerequisite for prewarming the expensive model phases
  during the menu.

---

## Ranked opportunities

### O1 — Scope-limit eager precache (do first)
- **Change:** flip default to the smart path, or make it adaptive. The mechanism (`ComputeExpectedWeapons`
  + the `warmAll` gate at [NetGame.cs:2214](game/net/NetGame.cs)) exists.
- **Payoff:** directly attacks the 6.5 s weapon phase; on a typical DM map the expected set is a fraction
  of 24. Player-model roster (10) is a second candidate — warm connected/roster models, idle-warm the rest.
- **Risk:** low, but real — a weapon *not* in the expected set pays a 30–300 ms first-use hitch
  (`PERFORMANCE_REPORT.md` §row). Keep the idle-warmer covering the remainder so "not precached" means
  "warmed a few seconds later in the background", not "cold forever". The default was set to `1` on purpose;
  revisit with data, ideally warm-expected-eager + warm-rest-idle rather than a blunt flip.
- **Client gap:** `ComputeExpectedWeapons` falls back to all-24 for a **pure client** (`!_isListenServer`,
  [NetGame.cs:2456](game/net/NetGame.cs)) because it "doesn't see the map's entities". But
  `client-map-load-implemented` (merged) now ships mapname+gametype and loads the server BSP on the client —
  so the client *can* compute the expected set from the loaded entities + gametype. Wiring that closes the
  fallback.

### O2 — Process-lifetime asset cache (unlocks menu-time prewarm)
- **Change:** hoist the parse/decode/material caches (or a subset — start with the skeletal parse cache and
  texture predecode, the model long-pole) into a process-lifetime service the menu can populate and the
  match's `NetGame` reads. Watch lifetime/memory: today caches free on match end for a reason (VRAM).
- **Payoff:** the enabler for O3; on its own, makes back-to-back matches on the same map near-instant.
- **Risk:** medium — memory pressure (holding roster textures across the menu), and correctness (skin/mount
  invalidation on `--data` change). The parse cache is already worker-locked, which helps.

### O3 — Menu-time background prewarm (needs O2)
- **Change:** on map selection in the menu, kick `BackgroundAssetStreamer` (Low priority) to read+parse the
  BSP, queue lightmap/texture decode, and parse likely weapon/player models into the O2 process cache.
- **Payoff:** hides the parse/decode portion of the load behind menu dwell time (0.1–5 s).
- **Risk:** low *if* O2 exists and priority stays Low (don't stutter the menu). GPU upload + pipeline warm
  still happen at match start (main-thread-bound) — so this shortens, not eliminates, the load.

### O4 — Widen background-thread coverage (incremental)
- **Change:** push more of `MapLoader.BuildMap`'s decode onto the streamer; confirm texture predecode covers
  the map's surface textures, not just models.
- **Payoff/Risk:** modest / low. Bounded by the main-thread GPU-upload floor.

### O5 — Instrument the load (supporting, do alongside O1)
- **Change:** add `Prof.Sample`/stopwatch scopes to the blocking phases (BSP parse, collision, render-geo
  build currently have none) + a single "load begin → playable" span. House rule already: new per-frame
  systems ship a `Prof.Sample`; extend to load phases so O1's win is measurable, not inferred from GD.Print.

---

## What won't work / hard limits

- **GPU resource creation is main-thread-only** (`ImageTexture.CreateFromImage`, `ArrayMesh.AddSurfaceFromArrays`,
  material creation, scene-tree ops). No amount of background threading moves the *upload* off the main
  thread — only the decode/parse ahead of it. This is the floor on lever #3.
- **Pipeline warm needs the live World3D** — can't be done before the match viewport exists, so it can't be
  fully hoisted into the menu (`godot-pipeline-compile-internals`).
- **Exact loadout for sounds / connected-player models is genuinely unknowable at menu time** — those stay
  lazy/idle-warmed by design; don't try to eager them from the menu.
- **Squatter gotcha for measurement:** kill stray `Godot*`/`XonoticGodot*` before any capture, or the run
  bogus-joins a leftover host and the numbers lie (`net-join-two-instance-test`).

---

## Proposed phased plan

- **Phase 0 — clean release-export baseline.** `tools/perf-run.ps1 -Label loadbase` after killing strays;
  add O5 load-phase instrumentation so per-phase budgets are real. *Gate: numbers before code.*
- **Phase 1 — O1 scope-limit precache.** Expected-eager + rest-idle; close the pure-client fallback via the
  merged client-map-load entity data. Re-measure; confirm no new first-use hitches with the idle warmer on.
- **Phase 2 — O2 process-lifetime cache.** Start with the skeletal parse cache + texture predecode. Memory
  audit (VRAM across menu).
- **Phase 3 — O3 menu-time prewarm.** Wire map-selection → Low-priority streamer prewarm into the O2 cache.
- **Phase 4 — O4 background widening + polish.** Opportunistic.

Each phase is independently shippable and measurable; Phases 0–1 deliver most of the win at the least risk.

---

## Open questions for Bryan

1. **Precache-scope default:** OK to flip toward expected-eager + rest-idle (accepting a possible brief
   background warm for off-loadout weapons), or keep all-eager and only chase the *earlier/parallel* levers?
2. **Menu-time prewarm appetite:** worth the O2 process-cache refactor (memory/lifetime complexity) to hide
   load behind menu dwell, or is shortening the in-match load (O1) enough for now?
3. **Target:** what's the goal — a wall-clock load-time number on the release export, or specifically
   "no hitches" (already largely true, since heavy work runs under the loading screen)?

---

## How to reproduce the measurement

```powershell
# kill strays first (an orphan host holds UDP 26000 and poisons the run)
Get-Process Godot*, XonoticGodot* -ErrorAction SilentlyContinue | Stop-Process -Force

# clean release-export capture (preferred — representative)
tools\perf-run.ps1 -Label loadbase -Map stormkeep -Bots 6 -Secs 30

# quick Debug capture with timestamped milestones (what produced the table above)
#   pipe stdout through a timestamper; grep the [MapLoader]/[NetGame]/[IdleWarmer] lines
```
