# Interactive loading screen — feasibility, cost model, and the remaining prewarm surface

**Status: MEASURED (2026-08-04).** T1–T3 in §4 are unstarted. Two things landed: the effectinfo warm
(**§5.1**) and the O5 load instrumentation (**§6b**), which produced the first real per-phase numbers and
**overturned two standing assumptions** — read §6b before acting on §1–§5.

Continues the thread in
[loading-speed-background-precache-2026-07-06.md](loading-speed-background-precache-2026-07-06.md) and
[loading-and-assets-progress-2026-07-31.md](loading-and-assets-progress-2026-07-31.md).

## The question

> Could the loading screen stay interactive — chat usable while the game loads / connects — instead of
> dropping to ~1 fps? Given the menu-warmer work, is that reasonable?

Answer: **yes, and the cheap tier is cheaper than the warmer work was** — but the lever is different from the
one the menu warm used, the load gets measurably *longer* unless it is paired with the off-thread tier, and
the chat half is a netcode-ordering problem rather than a perf one.

**Everything below is derived from reading the code, not from a capture.** No clean release-export load
measurement exists (see §6). Treat the structure as verified and every duration as unmeasured.

---

## 1. Why it is ~1 fps

The load is a coroutine in [NetGame.cs:566](../game/net/NetGame.cs) shaped as
`BeginStage` → yield one frame → a large synchronous block → repeat. Frame rate is simply the inverse of the
block sizes. The blocks:

| Block | Contents | Divisible? |
|---|---|---|
| `StartListenServer()` | BSP parse, `BspCollisionBuilder`, `GameWorld.Boot` (entity spawn, waypoint load) | parse: no (off-thread instead); spawn: loop |
| `SetupRender()` → [MapLoader.BuildMap](../game/MapLoader.cs:142) | face bucketing, occluder, lightmap atlas, PVS regroup, per-cell `ArrayMesh` + `MeshInstance3D` + material resolve + texture upload | mostly loops |
| `SetupCameraAndHud()`, `SetupMusic()` | camera/HUD tree; music decode (tens of MB) | small / decode |
| `PrecacheWeaponModelsAsync` | yields every 4 weapons; **builds and renders** models for PSO warm | already yields |
| `PrecacheCombatSoundsAndModelsAsync` | yields every 16 sounds; roster build + render | already yields |

The two precache stages already yield, and since the menu warm landed their asset work should be mostly cache
hits. So the ~1 fps window is almost certainly concentrated in the first two blocks — three or four
indivisible chunks of a second or more, not a uniformly slow loop.

**The problem is block size, not thread occupancy.** That is the whole difference from the menu-warm work:
there the main thread was idle and background work reached the frame through the allocator and the driver.
Here the main thread is genuinely saturated with work that must happen now.

## 2. What transfers from the menu warm, and what warns against the obvious approach

**Transfers:** the proof that Godot 4.6.3 Forward+/Vulkan accepts texture upload *and* material/shader
construction from a worker thread ([AssetSystem.WarmTextureOffThread](../game/loaders/AssetSystem.cs:813),
verified by byte-comparing a rendered turntable). The 2026-07-06 doc's "hard limit — GPU upload is
main-thread-only" is **falsified** and should not be quoted as a constraint any more. That reopens the map
texture/material portion of `BuildMap`.

**Warns:** the warm's headline finding was that *serial beat parallel* on every axis — a GC must suspend every
allocating thread (17.4 ms gen0+gen1 with four workers busy) and concurrent uploads saturate driver ingest.
It bought zero hitches by going ~6 s → ~21 s of wall clock. Nothing was waiting on the menu warm, so that was
free. **During a load screen the player is waiting**, so the same trade is not available. Fanning the map
load onto workers would trade load duration for smoothness at a bad exchange rate.

## 3. Cost model — how much slower does chunking make the load?

Chunking converts one 2000 ms block into N frames of B ms of work each. Every yielded frame costs overhead O
regardless of the work it carries (present/vsync, loading-screen repaint). Efficiency is `B/(B+O)`:

| Target | Work/frame | Load inflation (O ≈ 7 ms) |
|---|---|---|
| 20 fps | ~50 ms | ~14% |
| 30 fps | ~33 ms | ~20% |
| 60 fps | ~16 ms | ~43% |

**Aim at 30 and no higher.** Chat input does not need 60, and the curve gets steep fast. On a 10 s load, 30 fps
costs ~2 s.

Total CPU work is unchanged — chunking adds no work and no allocation, and stays single-threaded, so none of
the GC-suspension or driver-ingest penalties from §2 apply.

**Two traps that would make it much worse if unhandled:**

1. **The half-built map renders behind the loading screen.** The overlay is opaque (black `ColorRect` + the
   `gfx/loading` image) but Godot still draws whatever cells `BuildMap` has added so far. O therefore *grows*
   through the load, and chunking `BuildMap` specifically gets worse the further in it gets. Hide the map root
   / deactivate the 3D viewport until the load completes. This must be deliberate — it is not free.
2. **Vsync.** An under-budget frame still costs a full present at the refresh cap. Use the existing
   debt-pacing in `BackgroundAssetStreamer` rather than a naive per-frame yield loop, so the budget bounds the
   *average* (this is the same lesson as "a budget that always runs at least one item is not a budget").

**The offset:** moving BSP parse + collision build off-thread is genuinely parallel — the main thread paints
while a worker parses. That claws time back rather than spending it. Depending on the real block sizes,
tier 1 + tier 2 together could net out *faster* than today while also being interactive. That is the version
worth pitching; tier 1 alone is a pure duration regression.

## 3b. What if the load went WIDE? (2026-08-04)

§2 says the menu warm's "serial beat parallel" finding warns against fanning the load out. That is right about
the *menu* and too blunt about the *load screen*. Two corrections and one real ceiling.

**Correction 1 — the 6 s → 21 s tax was not the upload gate.** It came from `MenuAssetWarmer`'s
`MaxUnitsInFlight = 1` plus `Chain` serialising jobs *within* a unit. The `_uploadGate` is a different
mechanism and is cheap. Those two get lumped together easily; they behave nothing alike.

**Correction 2 — every tuning knob on that lane is calibrated for "do not disturb a running game", and under
a load screen each one is backwards.** None of them are architectural; they are per-caller policy:

| Knob | Value | Why it is wrong under a load screen |
|---|---|---|
| `WorkerCount` | `Clamp(cores/4, 2, 4)` ([BackgroundAssetStreamer.cs:189](../game/client/BackgroundAssetStreamer.cs)) | **4 workers max on a 16-core box.** During load the process owns the machine |
| lane priority | `BelowNormal`, → `Normal` only while High work is queued | During load, the load *is* the foreground work |
| `MaxUnitsInFlight` | `1` + `Chain` | This is the 6 s → 21 s |
| `BudgetMs` | 1.0–2.0 | Sized to protect a 6.9 ms frame; a load screen can spend 30 |

**Where wide actually buys speed:** the CPU-serial work — BSP lump parse, collision build, PVS/cluster sets,
face bucketing, lightmap + texture decode, model parse. Nearly all independent, all single-threaded today.

**Where it buys nothing: GPU upload.** Saturating driver ingest was the *menu's problem*; under a load screen
it is the *goal*, and past the pipe limit more uploader threads only queue. `WarmTextureOffThread`'s existing
parallel-decode / serialised-upload split is already the throughput-optimal shape — tuned for latency, but the
architecture happens to be right either way. **Leave the gate alone.**

**The biggest unknown is the biggest prize:** `ArrayMesh.AddSurfaceFromArrays` off-thread — per-cell mesh
packing, 31 cells on stormkeep, plausibly a large share of `BuildMap`. Untested. Two "known limits" in this
exact area were falsified this cycle, and the methodology for settling it already exists (build off-thread,
byte-compare a turntable render — the same check that validated the texture and material paths).

**The ceiling that does not go away: GC.** Pauses stop *every* thread, so they cut throughput, not just
smoothness — the real Amdahl tax. 17.4 ms for a gen0+gen1 with four workers busy, and a map load allocates
considerably more than the menu warm. Two levers, **and the order matters — widening before pooling just buys
more GC**:

1. **Reduce allocation first.** Already a known item: `PERFORMANCE_REPORT.md` §13.3 #2 (ArrayPool in
   `IqmReader` + TGA/DDS), with `DecodeBuffer.Pool` as the existing pattern.
2. **`GCSettings.LatencyMode`** — untouched today (no runtime GC knobs anywhere in `src/` or `game/`). The
   csproj sets `ServerGarbageCollection=false` + concurrent deliberately, with a comment warning not to flip
   Server GC without profiling — but that warning is about *gameplay* frame pauses. A load screen wants
   throughput mode, and `LatencyMode` is runtime-settable and reversible for the duration of the load, unlike
   the process-wide csproj knob.

**Why this matters for §3:** if wide load is genuinely faster, the responsiveness-vs-duration tradeoff
**dissolves** — T1's ~20% chunking tax gets paid for several times over. But the plausible range is 2–4× if
CPU-bound and ~nothing if upload-bandwidth-bound, which is not a gap that can be closed by reading code. This
makes §6 *more* valuable, not less: it now answers "how much parallelism is even available", not just "which
block do I chunk".

## 4. Proposed tiers

- **T1 — chunk the existing loops** (face bucketing, cell packing, entity spawn) against a debt-paced budget.
  No threading, no new risk surface. Gets ~1 fps → 20–30 fps. The 80% win.
- **T2 — move BSP parse + collision build off-thread.** Pure C#, touches no Godot types. A genuinely long
  block that resists chunking (it is a parser), and the item that offsets T1's cost.
- **T3 — chat during connect.** Not perf. `ChatPrompt` already lives on Shell's own CanvasLayer
  ([Shell.cs:250](../game/Shell.cs)), outside `NetGame`, so the UI plumbing is already in the right place —
  but `OpenChatPrompt` gates on `MatchRunning` and the client handshake completes near the *end* of the load.
  Real chat-while-connecting needs that ordering inverted (connect first, stream assets after). Separate
  project, mostly netcode.

**What stays on the main thread regardless:** `AddSurfaceFromArrays` and scene-tree node adds (chunkable, not
movable). `GameWorld.Boot` is an atomicity problem rather than a threading one — the sim needs a coherent
world before it ticks.

## 5. Remaining prewarm surface

### 5.1 Map-independent, paid at connect, not warmed — actionable now

- **`effectinfo.txt` + the `effectinfo_xg.txt` style overlay** — ✅ **LANDED 2026-08-04** (implementation log
  below). 9374 lines / 169 KB, tokenised and expanded into 831 emitter blocks grouped under **314** distinct
  effect names.
  **Correction to an earlier read of this file:** it is *not* lazily parsed on first effect. The doc comment
  on [EffectSystem.cs:105](../game/client/EffectSystem.cs) says "loaded lazily on first use", but
  [NetGame.cs:1893](../game/net/NetGame.cs) calls `_render.Effects.Warmup()` at map load, which forces
  `EnsureInfoLoaded()`. So this is a **load-time** cost, not a mid-match hitch. That makes it a smaller win
  than it first appeared — expect tens of ms, not hundreds. Taken forward anyway because it is pure text
  parse, map-independent, touches no Godot types, and drops into the existing warm lane at near-zero risk.
- **`ParticleFont`** — the atlas *texture* is already warmed (`ParticleFont.AtlasVPath` is in the warmer's
  loose-texture set), but the UV-table text parse and the pre-cropped `AtlasTexture` cells in `Warmup()` are
  not. Deliberately **not** taken in this pass: those are Godot `Resource`s, so sharing them across matches is
  a materially different risk from sharing a parsed dictionary. Follow-up.
- **`PlayerSoundResolver.Install(_vfs)`** at [NetGame.cs:581](../game/net/NetGame.cs) — parses the `.sounds`
  manifests. Map-independent, cheap, free to hoist.

### 5.2 The floor the warm cannot remove — know it so it is not chased

- **Per-instance mesh builds.** `Skeleton3D` + skinned `ArrayMesh` are per-instance and cached nowhere (the
  warmer's own notes are explicit about this). Every match rebuilds them.
- **PSO / pipeline compile.** Both precache stages build *and render* models specifically to compile pipelines
  ([NetGame.cs:3191](../game/net/NetGame.cs) — the 2026-06-15 warm-by-render fix), and `GpuWarmPass` needs the
  live World3D. Viewport-variant specific, so it cannot hoist to the menu.
  **Since the menu warm made the asset caches hot, this is very likely now the dominant remaining cost of both
  precache stages.** If those stages are still slow, the target is PSO, not assets. Highest-value thing to
  measure next. (Note the standing caution in the progress doc: two "known limits" in this area turned out
  false this session — re-test the viewport-variant claim before accepting it.)

### 5.3 Map-dependent but knowable earlier — the unimplemented O3

The process-lifetime cache that used to block this now exists (Phase 1), so O3 is unblocked:

- **BSP parse + collision build**, started the moment the map is known. Create-Game picker gives seconds of
  dwell; for a server connect the mapname arrives in the handshake, so it is "as early as possible" rather
  than "at menu". **Highest-value item on the list** — it is both the largest block and the one that is
  hardest to chunk, and it doubles as T2.
- Lightmap atlas + map surface textures.
- **Waypoint network** — [GameWorld.cs:5165](../src/VortexArena.Server/GameWorld.cs) parses
  `.waypoints` / `.cache` / `.hardwired`, and runs `AutoLink` when a map ships none. Inside `GameWorld.Boot`.
- Music decode — [SetupMusic](../game/net/NetGame.cs:2034), tens of MB from the mapinfo cdtrack.

### 5.4 Unverified gap worth a look

The warmer covers weapon `v_` and `h_` models; world/dropped models arrive via `Registry<Pickup>` +
`StartItem.ResolveModelPath`. **Not confirmed** that this path yields the `g_` weapon models rather than only
item pickups. If it does not, every first weapon-drop is still a cold load.

---

## Implementation log

### 2026-08-04 — effectinfo + style overlay moved to the menu warm

**The change.** A process-lifetime shared catalog, filled by the menu warm and read by every match:

- `EffectInfo.GetShared(vpath, textLoader)` ([EffectInfo.cs](../game/client/EffectInfo.cs)) and the matching
  `EffectInfoOverlay.GetShared` ([EffectInfoOverlay.cs](../game/client/particles/EffectInfoOverlay.cs)) — a
  static vpath-keyed cache. **The parse runs OUTSIDE the lock**, then publishes under a re-check, so two
  callers never serialize behind one 169 KB tokenise and the loser discards its own parse. Same publish
  pattern as `AssetSystem.WarmTextureOffThread`. A read miss still caches an empty catalog, so a miss is not
  re-probed by every map load.
- `EffectStyleRegistry.LoadShared` ([EffectStyleRegistry.cs](../game/client/particles/EffectStyleRegistry.cs))
  adopts the shared overlay. Its `_overlay` stopped being `readonly` for this, so `Parse` (the unit-test entry
  point, and the one mutating path) now allocates a **private** overlay first — a shared instance must never
  be written through.
- `EffectSystem.Info` became `{ get; private set; }` and `EnsureInfoLoaded` now takes both catalogs from the
  shared cache ([EffectSystem.cs](../game/client/EffectSystem.cs)).
- `MenuAssetWarmer` queues one warm unit for both catalogs, **first in the queue**
  ([MenuAssetWarmer.cs](../game/client/MenuAssetWarmer.cs)).

**Why first in the queue.** The warm is strictly serial (`MaxUnitsInFlight = 1`), so queue position *is*
arrival time. Queued last it landed ~360 units and tens of seconds deep — after any player who clicks Start
promptly, i.e. warming nothing. It is also the one unit every match needs regardless of map, gametype or
weapon set, unlike any individual model, and it is cheap enough that it delays the rest by almost nothing.

**Why sharing is safe.** The catalog is immutable after parse — `Parse` is the only writer of `_byName`, a
cached instance is never re-loaded, and `TextLoader` is read only inside `Load`. The one standing assumption
is that consumers do not mutate the `EffectInfoEmitter` blocks handed back by `Get`; that is already required
today, since those blocks are shared across every spawn of an effect within a match. Keyed by vpath only, so
it assumes mounts do not change under a running process — `ClearShared()` exists on both types for a host that
remounts.

**Verified (Debug, headless):**

| check | result |
|---|---|
| menu-only boot — warm reaches effectinfo | `[EffectInfo] parsed 'effectinfo.txt': 314 effects`, **first unit**, immediately after the warmer banner |
| distinct names vs the file | 314 == `grep '^effect ' \| sort -u \| wc -l` (from 831 blocks) |
| **menu warm + match in one process** | **cold parses: 1**; match's `[EffectSystem] effectinfo: 314 effects` prints with **no second parse** — the match hit the cache |
| headless host, `sv_dedicated_slim 0` | 314 effects, overlay 0, no exceptions |
| suite | **4159/4159**, 0 failed |
| build | Release + Debug, 0 errors; the 24 warnings are pre-existing and none are in the touched files |

`[EffectInfo] parsed …` is printed on the **cold parse only**, deliberately: it must appear exactly once per
process, and a second occurrence means a match missed the cache. That is what made the table row above an
observation rather than an assertion — the per-match `[EffectSystem] effectinfo: N effects` line prints either
way and proves nothing on its own.

**Not measured:** the actual time saved. It is a load-time saving of the order of tens of ms and no capture
was taken; §6 still applies. **Not done:** `./vx perf-smoke` (house rule for perf-relevant changes) — the
change adds one unit to the front of the existing serial warm queue and removes a per-match parse, but the
smoke has not been run.

---

## 6b. MEASURED (2026-08-04) — O5 instrumentation, and what it overturned

`LoadTimeline` ([game/LoadTimeline.cs](../game/LoadTimeline.cs)) — wall-clock phase accounting for the load,
`Begin`/`Phase`/`Report`, wired through `NetGame._Ready`, `StartListenServer` and `MapLoader.BuildMap`.
Deliberately **not** `Prof.Sample`: the frame profiler accounts per frame, and a `Prof` scope opened during a
load lands in the one enormous load-screen frame's accumulator (`proc:other 514153 ms` in the 2026-07-06
capture is exactly that). Not a per-frame system, so the `TopLevelNodeScopes` house rule does not apply.

### The decisive capture: cold vs warm caches, same process

stormkeep + 2 bots, **Debug, headless**, `sv_dedicated_slim 0`. Two loads in one process — the second reuses
the first's caches via `cl_persist_asset_cache`, so its numbers *are* the warm-cache numbers.

| phase | cold (ms) | warm (ms) | |
|---|---:|---:|---|
| `bsp.parse` | 31.9 | 24.0 | |
| `collision.build` | 277.3 | 228.3 | pure CPU, **barely caches** |
| `world.boot` | 117.3 | 34.6 | |
| **`server.start`** | **462.3** | **291.7** | |
| `map.faces` | 32.8 | 35.5 | |
| `map.patches` | 82.9 | 33.5 | |
| `map.occluder` | 7.9 | 4.3 | |
| `map.lightmap-atlas` | 42.6 | 36.3 | |
| `map.pvs+regroup` | 103.5 | 102.9 | pure CPU, **does not cache at all** |
| `map.cell-meshes` | 175.6 | 46.6 | |
| **`render.setup`** | **2499.8** | **799.2** | sub-phases account for only 259 of the warm 799 |
| `camera.hud` | 252.2 | 87.3 | |
| `precache.weapons` | 6561.7 | **341.1** | **−95%** |
| `precache.sounds+models` | 5843.3 | **184.9** | **−97%** |
| **TOTAL** | **15738** | **1746** | **9× faster** |

### What this overturns

1. **The "precache stages are now almost entirely PSO" hypothesis is wrong as stated.** They are not
   PSO-dominated; when cold they are asset-dominated (79% of the load, matching the 2026-07-06 shape), and
   when warm they nearly vanish — 12405 ms → 526 ms. The hypothesis was directionally right only about the
   *residual*: from the windowed capture below, PSO warm is ~1.0 s for weapons, so once assets are cached the
   PSO half is what is left of those stages. It was wrong about the magnitude of the whole.
2. **The old "79% is precache" figure describes the COLD path only, and the CLI is always cold.**
   `StartMenuAssetWarm()` runs only on a plain menu boot — `--map`/`--host`/`--connect` skip it
   ([Shell.cs:353](../game/Shell.cs) vs [:363](../game/Shell.cs)). So every CLI capture ever taken for this
   investigation, including this one's cold column, measures the worst case rather than what a player sees.
3. **In the warm case the long pole moves to `render.setup` (46%) and `collision.build` (13%).** Both are
   things the menu warm structurally cannot help with, and both are T1/T2 targets. `map.pvs+regroup` does not
   cache *at all* (103.5 → 102.9) — pure CPU, and 5.9% of a warm load.

### Windowed capture — the only valid PSO reading

Headless uses the `dummy_video` renderer, so `GpuWarmPass` has nothing to compile against and every
`pso-warm` span reads ~1 ms. Re-run windowed (Debug, 1280×720, cold): total 20078 ms, `render.setup` 5186 ms,
`precache.weapons` 7124 ms, and **`precache.weapons.pso-warm` 1042.6 ms** — the real number.
**Any PSO conclusion requires a windowed run.**

### Caveats — do not over-read the warm column

- **Debug, headless.** Release is materially faster and headless does no real GPU work at all.
- **The warm column is the same map twice**, so it also reuses map-SPECIFIC caches (lightmaps, map surface
  textures) that a menu warm never fills. A menu-warmed load of a *fresh* map sits **between** the columns:
  the precache stages should collapse as shown (those assets are map-independent), while `render.setup`
  stays nearer its cold 2500 ms. Predicted, not measured — ~3.5–4 s rather than 1.7 s or 15.7 s.
- **~540 ms of the warm `render.setup` is unmeasured** (799 total, 259 in `BuildMap` sub-phases). That gap is
  now the single largest unexplained block on the warm path and the next thing to instrument: `SetupRender`
  also builds `ClientWorld`, wires resolvers, runs `Effects.Warmup()` and warms item/gib instances.
- **Cross-frame phases nest by OPEN order, not containment.** `precache.weapons.pso-warm` is opened inside
  `precache.weapons` but closes after it, so it is indented under a parent it only partly overlaps — its ms
  is not a subset of the parent's. A phase closing after `Report()` now prints its own `(late)` line rather
  than vanishing, which is how `precache.models.pso-warm` went missing from the first windowed capture.

### Ranked, for the warm (realistic) path

| block | warm ms | % | reachable by |
|---|---:|---:|---|
| `render.setup` | 799 | 46% | T1 chunking; ~540 ms needs instrumenting first |
| `precache.weapons` | 341 | 20% | PSO-bound residual — not the menu warm |
| `server.start` | 292 | 17% | T2 off-thread (`collision.build` is 228 of it) |
| `precache.sounds+models` | 185 | 11% | as above |
| `camera.hud` | 87 | 5% | — |

## 6c. The `render.setup` gap, resolved (2026-08-04)

§6b left ~540 ms of the warm `render.setup` unaccounted. Instrumented the rest of `SetupRender` /
`AttachWorldRender`; the gap is now closed (892 of 917 ms named). Same two-load capture, Debug/headless.

| phase | cold (ms) | warm (ms) | note |
|---|---:|---:|---|
| `fx.warmup` | 176.2 | 52.8 | effectinfo + particlefont |
| **`fx.trails`** | **1438.5** | **6.8** | projectile-trail resources — **8.3% of a COLD load, fully cacheable** |
| `gpu.warm-items(build)` | 2.7 | 1.2 | sync build only; the compile is later |
| `map.build` | 549.6 | 287.3 | (sub-phases in §6b) |
| `render.pvs(dup)` | 0.0 | 0.0 | **the "third PVS build" concern was wrong** — construction is lazy |
| **`effects.collision(dup)`** | 291.1 | **307.2** | **does not cache; larger than the whole map render build** |
| `decal.geometry` | 112.1 | 70.7 | |
| `editor.nodes` | 20.0 | 7.2 | |
| `light` | 177.1 | 158.7 | `AddLight`, does not cache |
| **`render.setup`** | **2841.6** | **916.9** | |

### Finding 1 — the listen path builds map collision TWICE, and it is ~30% of a warm load

`MapLoader.BuildCollision(bsp, assets)` is literally `BspCollisionBuilder.Build(bsp).World` — the **same
builder** `StartListenServer` already called. So a listen-server load runs it twice:

| | call | warm ms |
|---|---|---:|
| `StartListenServer` | `BspCollisionBuilder.Build(bsp, _droppedSubmodels)` | 288.7 |
| `AttachWorldRender` | `BspCollisionBuilder.Build(bsp)` — no submodel filter | 307.2 |
| | **total** | **596 ms = 29.6% of a 2016 ms warm load** |

The only difference is the gametype-dropped `"*N"` submodel filter. **The open question is whether the
effects/decal world should be filtered too** — the render IS filtered, and `MapLoader.BuildMap`'s own comment
insists "render and collision MUST agree", which argues the *unfiltered* one is the odd one out. The pure
`--connect` path already shares a single world into effects and documents why that is safe ("both the
prediction trace and these effect traces are main-thread"). If the listen path can do the same, that is
**~300 ms off every warm load for a deletion**, which beats any threading work in this document.

Not filed as a bug yet: the filter difference may be deliberate. It needs someone to confirm what decals
should collide with on a gametype-filtered map.

### Finding 2 — `fx.trails` is a prime menu-warm candidate

1438 ms cold, 6.8 ms warm — entirely cacheable, entirely map-independent (per-projectile-type trail
materials), and **not in the menu warm set today**. It only looks cheap in the warm column because the *first*
match paid it. On the CLI/dedicated path, which never warms, it is 8.3% of the load, every time.

### Finding 3 — what threading could actually buy on the warm path

Ranked for the warm (realistic) load, with what each is reachable by:

| block | warm ms | % | lever |
|---|---:|---:|---|
| `precache.weapons` | 415.0 | 20.6% | PSO-bound residual — **not** threading (windowed: ~1.0 s of PSO) |
| `effects.collision(dup)` | 307.2 | 15.2% | **delete it** (Finding 1) |
| `collision.build` | 288.7 | 14.3% | pure CPU, no Godot types → **T2 off-thread** |
| `map.build` | 287.3 | 14.3% | mixed; `map.pvs+regroup` 110.7 is pure CPU, `map.cell-meshes` 58.8 is Godot-bound |
| `precache.sounds+models` | 195.6 | 9.7% | mostly PSO residual |
| `light` | 158.7 | 7.9% | Godot, main thread |
| `decal.geometry` | 70.7 | 3.5% | unexamined |

**The honest ceiling.** About 700 ms of the 2016 ms warm load is pure-CPU work with no Godot types
(`collision.build` + `map.pvs+regroup` + roughly half of `map.build`'s bucketing) — that is what can move to
workers. Another ~300 ms can be *deleted* rather than threaded. The rest is either GPU-pipeline compilation
(which threading cannot touch) or Godot resource/scene-tree construction (which must stay main-thread and can
only be chunked).

So a plausible warm-path target is **~2.0 s → ~1.2 s**, and the largest single contributor to that is a
deletion, not parallelism. **Do Finding 1 before any threading work.**

## 6d. Can the MENU WARM itself go faster without costing menu FPS? (2026-08-04)

The warm is strictly serial (`MaxUnitsInFlight = 1` + `Chain`) and drains in ~21 s on release / ~35 s in
Debug. Since a player who clicks Start at t=5 s gets only a fraction of it, "faster" translates directly into
"more of the load is already warm".

**The measurement that justified serial is now stale, and that is checkable.** The four-config table
(2026-07-31, window 1/2/3 × chained/fanned) picked width 1 because parallel decoders drove GC pauses — the
configs allocated 26–63 MB/s and a gen0+gen1 with four workers busy measured 17.4 ms. **The very next day**,
the code-review pass cut asset-path allocation from 1371.5 MB to 975.2 MB (−28.9%), specifically by removing
a double read and un-pooled LOH allocation *in the asset read path the warm uses*. The reason serial won was
GC pressure; that pressure then dropped by nearly a third and nobody re-ran the comparison.

(The 2026-08-03 GC audit, `ce847083`, is **not** additional evidence here — it cut `cev.process` and
`decals.splat`, which are in-match per-frame allocators, not the warm's. It did leave behind a useful tool:
a per-scope `[frameprofile] alloc/s:` census, which would make a re-test far better instrumented than the
original was.)

Ranked, cheapest first:

1. **Re-test width 2.** One-line change, directly motivated by the above. The menu currently sits at p50/p95
   **6.9 / 6.9 ms with zero hitches** while warming — i.e. it is at the frame cap with headroom to spare, so
   there is room to spend before anything becomes visible.
2. **Order by likelihood, not just cost.** Free, and it raises the warm fraction at any given click time
   without touching concurrency at all. Putting effectinfo first (landed, §5.1) was one instance; the local
   player's own model and the default gametype's weapons are the obvious next ones.
3. **Type-aware concurrency.** The window is type-blind today. Sounds decode on the worker lane and never
   touch the GPU; textures upload; models parse. One sound unit alongside one texture unit contends far less
   than two texture units, because they bottleneck on different resources. Width 1 *per type* is strictly
   better than width 1 overall.
4. **Cut the warm's own allocation — the real enabler.** `PERFORMANCE_REPORT.md` §13.3 #2, and specifically
   the sound path: one exact-size managed `byte[]` per file for `LoadFromBuffer`, **much of it LOH**. 223
   sounds × an LOH buffer each is gen2 pressure, which is the worst kind. Reducing this makes every
   concurrency increase cheaper, so it should come **before** widening, not after.
5. **`GCSettings.LatencyMode` for the warm's duration.** Untouched today. Reversible, scoped, and unlike the
   process-wide csproj knob it cannot affect gameplay.

**Add `fx.trails` to the warm set regardless** (§6c Finding 2) — 1438 ms cold, map-independent, currently
unwarmed. That is a bigger win than any of the above and needs no concurrency change.

## 6. Measure before building any of this

There is no clean release-export load breakdown. The only table in the tree
([loading-speed-background-precache-2026-07-06.md](loading-speed-background-precache-2026-07-06.md)) is Debug,
from a run contaminated by a stray host holding UDP 26000, **and it predates the menu warm invalidating its
"79% is precache" conclusion**. `MapLoader` carries no load-phase instrumentation at all — O5 in that plan
never landed.

First step for the interactive-loading work, before any of T1–T3:

1. Add stopwatch spans to the four blocks in §1, plus one "load begin → playable" span.
2. **Split the precache stages into asset-work vs pipeline-compile** — §5.2 predicts they are now almost
   entirely PSO, which would redirect the whole effort.
3. Kill strays first (`Get-Process Godot*, VortexArena*`), capture on the release export via
   `tools/perf-run.ps1`, and confirm the capture actually mounted content (the `data/`-beside-the-binary trap).
