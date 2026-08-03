# Perf deep dive 2026-08-02 — the DarkPlaces-gap accounting

**Goal (Bryan, 2026-08-02):** DarkPlaces gets **500+ fps (2.0 ms/frame)** on this box (RTX 3080, 24-core)
with these maps and scenarios, at comparable visual fidelity. Today we measure 4.1 ms p50 (catharsis) /
5.8 ms (stormkeep) in combat. This document is the full accounting: where every millisecond goes, what
reclaims it, and what eliminated the hitches found on the way. Supersedes the Phase-2/3 ranking in
`perf-campaign-2026-07-06.md` (whose Phase-1 numbers predate `sv_threaded`, the bot budgets, and the
engine-template fixes).

All release numbers: fresh windows-client export @ this working tree, demo scenario, 90 s, 6 bots,
uncapped, portals pinned off, quiet machine, two-run rule. Sessions `20260802-2054..2103` +
`_scratch/perf_{cath,storm}_{warm,a,b}.json` are the reference points.

---

## 1. Today's state vs the 2026-07-06 baselines

| | catharsis 07-06 | catharsis today | stormkeep 07-06 | stormkeep today |
|---|---|---|---|---|
| pl avg fps | 171.6 | **215.5** | 195.6 | **162.2** |
| pl p50 ms | 5.4 | **4.1** | 4.9 | **5.8** |
| pl p99 ms | 12.5 | 9.9 | 8.3 | 12.4 |
| pl 0.1%-low | 21.8 | 37.0 | 116.4 | 41.0 |
| pl hitch time | 3.2 s | 6.1 s | 0.14 s | 1.8 s |
| worst frame | ~190 ms | ~860 ms | ~190 ms | ~890 ms |
| gen2 / session | 7 | 9 | 6 | **15** |
| sync PSO compiles | ~65 | 84–89 | ~65 | 89 |

Two corrections to how those rows must be read:

1. **The `pl` (t ≥ 20 s) window is contaminated by the world-entry tail** (§2): entry now completes at
   t≈24. Re-cut at `--postload 25`, the true steady state is: **catharsis avg 230 fps, p50 4.1, p99 8.9,
   0.1%-low 48, worst 47.8 ms, hitch 1.1 s/77 s · stormkeep avg 162, p50 5.8, p99 12.0, 0.1%-low 41,
   worst 211.5 ms, hitch 1.6 s/71 s.** Use `--postload 25` (or a real world-entry marker, §6) from now on.
2. **Stormkeep genuinely regressed** since 07-06 (p50 +0.9 ms, p99 8.3→12.0, avg −17%): draws p50 grew
   396→up to 503, `particles.cpu` is now top scope on 19% of frames (1.9 s/session; it also owns the
   211 ms worst hitch at t=57), and `phys` p95 grew to 1.55 ms (casings/gibs now `_PhysicsProcess`, §5.9).
   Catharsis meanwhile banked the July wins (threading, bot budgets, input-pump/jitterfix templates).

## 2. The ~600–890 ms "monster hitch" is a LOAD-TIME cost, not a gameplay stutter

**Correction (2026-08-02, later the same day).** An earlier revision of this document called this "the
world-entry build storm" and claimed the client "enters the world before the roster is built, dribbling
250–830 ms frames into gameplay". **That was wrong, and the error was reading the hitch log without the
frame-density check.** Frames-per-second-bucket over the first 28 s:

```
t(s):   0  1 .. 12 13 14 15 16 17 18 19 20 21 22 23 | 24  25  26  27
fps:    0  1 ..  1  1  1  0  2  2  4  1  2  4  8 23 |134 183 163 180
```

The whole t≈0→24 window renders **52–81 frames total (2–3 fps)**. That is the loading screen — the world
renders behind the overlay, which is why `draws` reads 567–920 and why it looked like gameplay in the
scope trees. The player sees a loading bar, not a stutter. Post-load the same session is clean (catharsis
worst frame **47.8 ms**, §1). The profiler counts these as hitches and they are what pinned the
full-session `0.1%-low` at 2–16 fps and inflated `hitch time` to 10–12 s — the `--postload 25` rows in §1
were the honest numbers all along.

**So the real finding here is a ~24 s load time**, of which ~11 s is model building, plus a metrics-hygiene
problem (load frames polluting every session-level smoothness number). Both worth fixing; neither is the
mid-combat freeze the earlier text implied. The genuine mid-combat hitch classes are the ones in §1:
`particles.cpu` bursts (stormkeep 211/88/55 ms), and the residual 12–47 ms CPU-LOGIC drip.

**What the load window is actually doing:** `iqm.mesh` dominates at ~8.2–8.5 s of over-budget time per
session, and it is charged from `IqmBuilder.BuildMesh` on BOTH model paths — the player roster
(`BuildSkeletalModel`) and the generic `LoadModel` path. 69 of the shipped weapon models are IQM, and
`LoadModel` "rebuilds a fresh node from the cached parse" on **every call** (`AssetLoader.cs:55`), so each
`v_` model is re-meshed per warm, per viewmodel and per carried copy.

The sim thread also stalls up to ~1.5 s inside `Bots.ServerFrame`'s unscoped remainder during this window,
near-zero-alloc, while main sits in `ng.camera` (new sub-scopes `bots.fixcount`/`bots.prewarm`/`bots.danger`
exonerate the bracketed work: 13.5/4.0/2.0 ms of 1537 ms). Since it only ever happens under the load-phase
alloc storm (100–180 MB/s, gen2s land here), the leading hypothesis is GC suspension rather than a lock —
an open probe (§7), and lower priority now that the window is known to be pre-gameplay.

**A hypothesis that was implemented, measured, and REVERTED — read this before re-attempting it:**
`IqmBuilder.BuildMesh` rebuilt the **entire ArrayMesh per instance** — per-vertex Quake→Godot position and
normal conversion, UV copy, 4-bone index/weight fill, index rebasing, `AddSurfaceFromArrays` (a GPU
upload), and material resolution — for *every wearer of the model*. But that geometry is a pure function
of `(IqmData, SkinFile)`: six bots wearing one model produced six byte-identical meshes. The parse cache
(2026-06-14) had already shared the `IqmData` + `AnimationLibrary`; the *build* stayed per-instance.

**Fix (landed 2026-08-02):** `IqmBuilder.SharedSkinnedGeometry` — one `ArrayMesh` + rest `Skin` cached per
`"<vpath>#<skin>"` in `AssetLoader._skeletalGeometryCache`, bound by every instance. Safe because all
per-player appearance is already per-INSTANCE, not per-material: colormod/glowmod/shirt/pants ride
`SetInstanceShaderParameter` (`ModelTint.cs:43-64`), alpha rides `GeometryInstance3D.Transparency`
(`PlayerModel.cs:318-324`), and the pose rides a per-instance `Skeleton3D` (Godot makes its own
`SkinReference` per `MeshInstance3D`).

**The one hazard, handled:** `PlayerModel._ExitTree` deterministically disposed the mesh + skin as
"exclusively owned" — the 2026-07-26 crash fix that keeps `RenderingServer::free` off the .NET finalizer
thread. Shared resources must survive one wearer leaving, so `IqmBuilder.MarkShared`/`IsShared` now marks
them cache-owned and `DisposeOwnedVisuals` skips exactly those (dropping the instance's reference without
freeing) — the same exclusion surface materials and the `AnimationLibrary` always had. Net memory *drops*:
one GPU vertex buffer per model instead of one per player.

### Measured outcome on today's bench: NO benefit on any axis. Reverted — then RE-LANDED on Bryan's call.

**Resolution (same day):** Bryan overruled the revert on architectural grounds, and he is right about the
precedent: DarkPlaces shares model geometry by construction (`Mod_ForName` caches one `model_t` per model;
entities are thin references — there is no per-instance geometry build anywhere in the idTech lineage),
and Godot's `Mesh`-as-`Resource` design intends exactly this sharing; the per-instance rebuild was the
anomaly. The implementation is re-landed **with the honest framing carried in the code comments**: it is
perf-NEUTRAL on this bench (measured, table below) and is justified by N-wearer correctness and VRAM at
scale — forcemodels (N players, one model), high player counts, corpse copies, mid-match joins. The
measured table stays so nobody re-sells it as an fps win.

| arm | `iqm.mesh` (over-budget) | load (time to steady state) | VRAM steady |
|---|---|---|---|
| baseline ×2 | 8238 / 8440 ms | 24 s / 24 s | 3458 MB |
| + player-model sharing | 8344 ms | 25 s | — |
| + weapon/`LoadModel` sharing ×2 | 8147 / 8247 ms | 25 s / 24 s | 3459 MB |

**Why it cannot work, in hindsight:** sharing only pays from the *second* instance of a model onward, and
the load window is ~40–70 **distinct** IQM models (10 roster + 24 weapons + hand rigs) each built exactly
**once**. There is no repeat build to eliminate. The ~8.2 s is inherent *first*-build cost.

It was reverted rather than kept. It was correct and tested (4111/4111, and every one of the three mesh
teardown sites — `PlayerModel._ExitTree`, `ModelAnimator`, `ViewEntityRenderer.DisposeOwnedMeshes` — was
guarded), but keeping unmeasured resource-lifetime complexity in code with a documented use-after-free
history (the 2026-07-26 `0xC0000374` family) is a net negative. **It also crashed on first run**:
`ViewEntityRenderer.DisposeOwnedMeshes` freed every held-weapon mesh under a comment asserting "the
ArrayMesh is per-build", which the change had silently made false → `ObjectDisposedException` inside
`ClientEntityView._Process`. That is the shape of hazard this area produces; do not re-attempt sharing
without a full ownership audit *and* a measurement that justifies it.

**The fix that actually targets first-build cost** (now the tracked item): `BuildMesh` does per-vertex
`Coords.ToGodot` on positions and normals, the 4-bone index/weight fill and the index rebase **on the main
thread** — all of it pure C# over already-parsed data. `ParseSkeletalModel` is **already off-thread** and
already hands over a prebuilt `AnimationLibrary` (the §12.3-1 precedent). Move the vertex conversion there
too and the main thread is left with `AddSurfaceFromArrays` — the GPU upload alone. The 2026-07-31
menu-warm work established that Godot 4.6.3 accepts texture upload and material construction from a worker
thread (`loading-and-assets-progress-2026-07-31.md`), so the remaining upload may be movable as well.
Secondary: chunk a cold build across frames so it costs ≤30 ms slices, and move the roster warm's
`WarmNodes` GPU pass fully under the bar (its "done (10 instances)" event fires at t≈23.5).

## 3. Profiler integrity fixes (landed today — trust no earlier scope census)

- **`Prof.ScopeToken.Dispose` popped by stack position with no identity check**
  (`src/VortexArena.Common/Diagnostics/Prof.cs`). One leaked scope (exception path, or a `using`
  spanning an `await`) permanently corrupted that thread's attribution — the `particles.cpu`
  "constant 30005.6 ms" artifact, the `proc:other 10731357.6 ms/frame` boot lines, and the CSV frame-0
  `proc_ms 1.28e8` rows are all this bug. Fixed: tokens record their depth; `Dispose` repairs the stack
  to it, charging leaked frames their own recorded names and emitting `scope leak: 'X' closed by 'Y'`
  events. Regression tests added (`ProfHierarchyTests.LeakedInnerScope_IsRepairedByOuterDispose`,
  `DoubleDispose_IsANoOp`).
- **A skeletal parse MISS now prints itself** with loader identity + cache contents
  (`AssetLoader.DescribeSkeletalParseCache`), and the roster warm prints its exact warm set — a
  mid-match cold-parse anomaly names itself in stdout.
- **`tools/lib/Find-Godot.ps1` (and `wobble-capture.ps1`) had non-ASCII em-dashes** — in a BOM-less file
  PS 5.1 reads ANSI, the em-dash's 0x94 byte becomes a smart quote that closes the string, and
  `perf-run -DebugBuild` had been broken with a parse error. ASCII-fied (the repo rule existed; these
  two violated it).
- `bots.fixcount` / `bots.prewarm` / `bots.danger` sub-scopes under `start.bots`.

## 4. The frame-budget decomposition (post-load t≥25, release, today)

Per-frame CSV columns over clean frames (catharsis n=18 005 / stormkeep n=12 304):

| bucket | catharsis p50 | stormkeep p50 | what it is |
|---|---|---|---|
| **total ms** | **4.07** | **5.78** | 245 / 173 fps |
| proc | 1.71 | 2.28 | all C# `_Process` on main |
| rest | 1.68 | 2.58 | Godot main-loop + present (overlaps gpu) |
| rcpu | 0.64 | 0.83 | render-thread submit (251 / ~400–500 draws) |
| gpu | 1.04 | 1.26 | measured GPU (not the limiter) |
| phys | 0.08 | 0.41 | Godot physics phase (casings/gibs/weather now live here) |

DP's 500 fps = 2.0 ms/frame. The gap is **~2.1 ms on catharsis and ~3.8 ms on stormkeep**, and it lives
almost entirely in `proc` + `rest`. Bucket targets for a 2.0–2.5 ms frame: proc ≤ 0.8, rest ≤ 1.0,
rcpu ≤ 0.5, with gpu (~1.0, overlapped) not binding at 1440p on this card.

**Floors (35 s idle, 0 bots, release, today):**

| | total p50 | proc | rest | rcpu | gpu | phys | draws | fps |
|---|---|---|---|---|---|---|---|---|
| catharsis floor | **3.38** | 1.12 | 1.40 | 0.77 | 1.04 | 0.07 | 400 | ~296 |
| stormkeep floor | **4.09** | 1.20 | 2.32 | 0.49 | 1.09 | 0.07 | 628 | ~244 |

The DP gap exists **before gameplay starts**: an empty world already costs 3.4–4.1 ms. Combat adds only
~0.6–1.7 ms on top. So the program is two-front: (a) shave the floor's `proc` (1.1 ms of always-on
`_Process` work with zero players — entity drive, HUD, particles idle, callbacks) and `rest`
(1.4–2.3 ms Godot loop/present, visibly draw-scaled: stormkeep's 628-draw vantage reads rest 2.32 vs
catharsis 1.40 @ 400 draws — so the draw-count items in §5.3 pay into `rest` too), then (b) keep combat's
increment small (bots/particles/effects). The old "228 fps June anchor" and the 07-06 3.70 ms stormkeep
floor are both retired by these numbers.

top1 census (which scope leads a clean frame): catharsis — `cw.process` 59%, `ng.process` 28%,
`sim.move` 12%; stormkeep — `ng.process` 46%, `cw.process` 23%, **`particles.cpu` 19%**, `sim.move` 10%.

## 5. The full optimization accounting

Every item carries: expected reclaim (per-frame ms unless marked), evidence, effort S/M/L. Ranked within
each bucket. Items marked ⚠ are hitch-class (variance), the rest are throughput.

### 5.1 `proc` — our own per-frame CPU (1.71 → target ≤ 0.8 ms)

| # | Item | Reclaim est. | Evidence / where | Effort |
|---|---|---|---|---|
| P1 ⚠ | **World-entry roster build under the load screen + chunked `IqmBuilder.Build`** (§2) | kills the 250–830 ms class | timeline §2 | M |
| P2 | **`cw.process` split + one-pass entity drive**: `DriveEntityNodes` runs a PVS box query per entity per frame plus a second full `_entityNodes` pass for csqc hooks (`ClientWorld.cs:1337-1486`); fold the loops, re-test PVS only when the entity moved > margin (64 qu) — most items/idle players are static | ~0.2–0.4 (cw.process leads 59% of catharsis frames at ~0.7–1.0) | ClientWorld.cs:1379,1425 | M |
| P3 | **Particles**: per-frame budget on bounce traces (one world `Trace` per bouncing particle per frame, unbounded, `ParticleSim.cs:637`) + free-flight caching; key-sort instead of `Comparison<int>` delegate (`FaithfulParticleRenderer.cs:513`); `LengthSquared` at `ParticleSim.cs:623`. Stormkeep-critical (19% of frames, 211 ms worst ⚠) | ~0.3–0.6 stormkeep; kills its 50–211 ms bursts | §1 census | M |
| P4 ⚠ | **R7 entity pool**: every spawn `new Entity` (~666 fields ≈ 4 KB) across 97 sites — the dominant combat allocator (2.4 GB/session, gen2 9–15) | gen2 → ~0 mid-match; removes GC-PAUSE class | EngineServices.cs:98-115 | M |
| P5 | **Audio interop cache**: ~5 native crossings per live one-shot per frame (`DriveOneShots` → position get/set + `VolumeDb`); skip writes when unchanged (<0.25 dB, unmoved emitter) | ~0.1–0.2 in combat | ClientWorld.cs:1078-1094, 916-921 | S |
| P6 | **HUD**: `NeedsRedraw` for HealthArmor/Weapons/Powerups/Crosshair (they force-redraw every frame; stale comment at HudManager.cs:347 claims otherwise); dirty-gate the three unconditional `QueueRedraw()` layers (ShowNames, **Radar** — full minimap + blips at display rate, WaypointSprite); cache the per-panel cvar-name concat (`HudPanel.cs:92`, ~4 500 string allocs/s) | ~0.1–0.3 + canvas rcpu | hud/* | S–M |
| P7 | Cheap alloc sweep: `SceneTreeTimer`+closure per effect spawn → one ager list (EffectSystem.cs:2215); `cl_gunoffset` string parse per frame (ViewModel.cs:1119) and colormod `float[]` parses (ClientWorld.cs:1422) behind `Changed` hooks; per-remote `Snapshot` alloc per snapshot (ClientNet.cs:1389) | alloc rate ↓, gen0 cadence ↓ | listed lines | S |
| P8 | `hud.trueaim` → the gate-free client-world tracer (particles/casings already use one); removes 2 gated traces per re-aim and decouples crosshair tint from sim-tick timing | tail smoothing | CrosshairPanel.cs:1147, EffectSystem.cs:181 | S |
| P9 | R4 `FromCvars` memo (version-stamp the store): ~45 dict reads per player move — measured small now (~0.02–0.05) but scales with fps; do it when touching movement | ~0.05 | MovementParameters.cs:154 | S |
| P10 ⚠ | Bot strategy-interval jitter (one line, all bots re-rate on the same tick forever) + pool the steering/danger probes that sit outside the WS-BOT budget | de-clusters 12–35 ms drip | BotBrain.cs:538; probes at :567-:626 | S |

### 5.2 `rest` — Godot main-loop + present (1.68 → target ≤ 1.0 ms)

| # | Item | Reclaim est. | Evidence | Effort |
|---|---|---|---|---|
| R1 | **Consolidate `_Process` callbacks**: ~55–60 native→managed callbacks alive per frame (86 overrides in game/), each with marshaling prologue — drive the ~25 HUD panels from HudManager (most are `_localClock += delta` one-liners), and the 5 `clientmisc` nodes from one driver | ~0.2–0.4 (measure per-callback cost first with a counting probe) | CPU audit §9 | M |
| R2 | **Present-path audit at uncapped fps**: `vid_vsync 0` is IMMEDIATE (no queue), yet `rest` p50 is 1.68 on a rendered scene vs the June floor's 1.71 claim — profile Godot's per-iteration servers (audio server mix push, input flush, `OS::add_frame_delay`, RenderingServer::sync) on the patched template; we already ship a custom template (input-pump backport), so an engine-side patch is an available lever | up to ~0.5 | wobble-audit 2026-07-26 methodology | L |
| R3 | Stormkeep rest delta (+0.9 vs catharsis): tracks draw count (up to 503) and submit; falls out of rcpu/GPU items below | — | §4 | — |

### 5.3 `rcpu` + draws — render submit (0.64–0.83; draws 251→503)

| # | Item | Reclaim est. | Evidence | Effort |
|---|---|---|---|---|
| D1 | **Shader dedup by generated code**: one `Shader` per Q3 shader *name* → byte-identical GLSL compiled N times; dedup collapses program count, PSO families (⚠ fewer sync compiles: 84–89/session today), and improves material sorting/batching | submit ↓ + hitch ↓ | ShaderCompiler.cs:165,485 | S |
| D2 | **Portals** (off in captures; real play pays it): ~1.4 ms p50 + 2× draws when facing one — half-rate update cvar, drop the MSAA inherit (portal PSO family ⚠), screen-coverage gate (`projPx` already computed), hoist the 6 duplicate portal shaders, gate the per-frame `RenderTargetUpdateMode` write | ~0.7–1.4 when visible | PortalRenderer.cs:303,312,338,455,521 | S–M |
| D3 | PVS-cull the escapees: map ambient `GpuParticles3D` emitters (each runs GPU compute + draw every frame regardless of room), map dynlights (2000-qu omnis two rooms away enter the cluster), DecalSplats' 256 transparent `MeshInstance3D`s | draws ↓, cluster load ↓ | MapParticleEmitters.cs:66, DynamicLightRenderer.cs:126, DecalSplats.cs:53 | S–M |
| D4 | Particle MultiMesh uploads full capacity every frame (up to ~327 KB ×2 for 10 s after a fight, ~94 MB/s staging); shorten `DecaySeconds`, tier the buffers | submit spikes ↓ | FaithfulParticleRenderer.cs:660, :79 | S |
| D5 | Per-projectile GPU-resource churn ⚠: fresh `ParticleProcessMaterial` + `GradientTexture1D` + `GpuParticles3D` + `OmniLight3D` + `AudioStreamPlayer3D` per rocket per trail block — share materials by effect-block identity (the `_infoMeshCache` pattern), pool the nodes | combat-burst hitches ↓ | EffectSystem.cs:1661-1755, ProjectileRenderer.cs:553 | M |

### 5.4 `gpu` — not the limiter on the 3080, but pure waste + the low-end story

| # | Item | Evidence | Effort |
|---|---|---|---|
| G1 | **Sun shadows nothing consumes**: `DirectionalLight3D ShadowEnabled=true` renders 4 PSSM cascades into a 4096² atlas every frame; the world shader *discards* directional light and the grid-lit player shader zeroes ALBEDO — and `LightmapShader` lacks `shadows_disabled`, so every world pixel still pays the PCF taps at 4× MSAA. Two-line fix | NetGame.cs:12604; LightmapShader.cs:109,252 | S |
| G2 | **World-shader `discard` defeats the depth prepass** for ALL world geometry (one shared shader; `alpha_cutoff` is 0 for almost every surface). Split opaque/masked shader variants (one extra PSO family, warmed) | LightmapShader.cs:243 | S |
| G3 | 4× MSAA + glow hardcoded; **no graphics settings wired at all** (menu widgets bind cvars nothing reads) — wire `Msaa3D`/scaling/glow; MSAA change ⚠ needs a warm pass (pipeline key) | project.godot:67-79; ClientSettings.cs | M |
| G4 | Clustered light loop: fx flash lights up to 24 × 2000-qu range — tighten ranges/lifetimes | EffectSystem.cs:2098-2124 | S |
| G5 | VRAM: 3.3–3.5 GB and caches never evict across maps; texture compression stays default-off pending the re-measure with fixed normal maps (see texture doc 07-31) | AssetSystem.cs | M |

### 5.5 `phys` — casings/gibs/weather on the 10 Hz physics tick

⚠ + fidelity: `ShellCasings.CasingBody._PhysicsProcess` + `ModelGibs` gib bodies integrate at
**10 Hz** (`project.godot` rationale "they never spawn" is stale — they do now): brass moves in 100 ms
steps, `max_physics_steps_per_frame=8` replays 8 steps after a stall (hitch amplifier), and up to ~100
per-node callbacks. Move both onto a pooled `_Process` ager (the fx-light pattern in the same file).
Stormkeep `phys` p95 1.55 / p99 2.46 ms is this + weather. — ShellCasings.cs:247, ModelGibs.cs:276. (S–M)

### 5.6 Streaming/warm residue in play

`stream.predecode` runs ~1–4 s of worker time through the whole match (idle warm long tail by design) —
fine off-thread, but `IdleWarmer` drains **on the main thread at a 1.5 ms budget with no debt
bookkeeping** (a slow item overshoots with nothing to repay; unlike `BackgroundAssetStreamer._Process`).
Give it the same debt accounting + route its uploads through the upload gate. — IdleWarmer.cs:36-55. (S)

### 5.7 PSO warm coverage (⚠ 84–89 sync compiles per session)

World animMap shaders and unshaded-additive variants have no warm entry (weapon-carried ones are
covered by the v_ warm; `grid_lit` was a false alarm — instance uniform, same PSO). With D1 (dedup) the
variant space shrinks first; then extend the warm list. — ShaderCompiler.cs:437, GpuWarmPass.cs. (S–M)

### 5.8 Server tick (off the render thread, still worth it)

`sim.integrate` single-tick bursts (8.4 ms observed) un-investigated; bot jitter P10; encode already
worker-side. Tick cost reaches the frame only through the trace gate now — keep `sv.gatewait_ms` on
hitch lines honest (it read 0 during the entry-window stalls: either the wait is not a gated trace, or
the counter misses a path — part of the §7 probe).

## 5a. Resource-sharing audit (Bryan 2026-08-02: "use shared resources for other items too")

The DP/Godot contract: **immutable data is cached once and referenced** (DP: `Mod_ForName` models,
skinframe dedup, `S_PrecacheSound`; Godot: `Resource` is ref-counted and shared by design), and
**per-instance variation rides per-instance channels** (DP: per-entity render params; Godot: instance
shader uniforms, `GeometryInstance3D` properties, `MaterialOverride`), never a resource copy.

Where this codebase stood, and what changed today:

| Resource | Before | Now / disposition |
|---|---|---|
| Textures, materials (by name), AnimationLibrary, sounds, IQM parse data | already shared (AssetSystem / parse caches) | unchanged — the pattern was established |
| Skeletal ArrayMesh + rest Skin | fresh per instance + per-instance dispose | **shared per (model,skin)** — `IqmBuilder.SharedSkinnedGeometry`, both the player path and `LoadModel`'s IQM branch; teardown sites guarded by `IsShared` |
| Q3-generated `Shader` programs | one per shader NAME — byte-identical GLSL compiled N times, own PSO family each (part of the 84–89 sync compiles) | **shared per generated CODE** — `ShaderCompiler.SharedShader`, used by the animated-stage + autosprite paths; materials stay per-use (they carry the textures/params — the program/parameter split both DP and Godot intend) |
| Portal shader | `new Shader` per portal (up to 6 identical programs) | **one static shared program**; per-portal materials keep their viewport texture + plane params |
| Particle gradient ramps (`GradientTexture1D`) | fresh Gradient + GPU texture upload **per shot** (burst init ramp, trail init ramp, alpha-fade ramp, class ramp) | **shared by value key** — `EffectSystem._rampCache`; the textures are immutable after build |
| Singleton shaders (Lightmap, PlayerSkin, Md3Morph, EditorWorld) | already `_shared ??=` | unchanged |
| MD3 morph meshes | per instance, mutated in place every bracket | **correctly per-instance** — morph mutates the mesh; sharing would corrupt all wearers. Not a violation. |
| `ParticleProcessMaterial` per trail block per projectile | per shot | **deferred with a design note**: the material bakes per-shot values (`speed`, `velDir` → Direction/velocity/damping), so block-keyed sharing is WRONG as-is. The share becomes possible if direction moves into the emitter node's transform (orient the node along flight, make Direction a constant local vector) — then key by (block, speed-tier, tint). Needs visual verification of trail orientation. |
| `ViewEntityRenderer.GlowMaterial`, Md3Morph per-surface material `Duplicate()` | per instance | justified per-instance today (they carry per-instance uniforms); the clean fix is instance uniforms, queued behind the trail-material refactor |

## 5b. Two architecture questions, answered with measurement (2026-08-02)

### "Should we move entities off Godot's scene tree?" — **No. It is a distraction.**

| scenario | total | entity-system cost |
|---|---|---|
| empty map, 0 bots | 3.38 ms | `cw.process` **0.1 ms**, EntityNode count **0** |
| 6-bot match | 4.07 ms | whole entity path **0.5–0.9 ms** |

The June diagnosis ("the per-node scene-tree submission tax", `cpu-fps-optimization-2026-06-16.md:124`)
drove R1/R2 — **and they landed and worked**. `ClientWorld.cs:581` / `:2507` call `SetProcess(false)` on
every entity node, and `EntityNode.DriveSync` (`:83-100`) dirty-gates to **zero native calls** for a
static or culled entity, 2–3 for a moving one. There is no per-node tax left to reclaim: a
RenderingServer-RID rewrite would rebuild `EntityNode`, `PlayerModel`, nameplates, attachments, LOD,
tint, csqc effects and `ViewEntityRenderer`'s bone reparenting, would forfeit engine frustum/occlusion
culling and per-instance shader params, and its **ceiling is 0.1–0.2 ms in combat and ~0 at the floor**.
Reading the June doc as current would send this work down exactly that path; today's numbers don't
support it. (Precedent check: `InstanceSetTransform`/`InstanceCreate` appear **zero** times in the tree.)

Cheap wins that fall out of the same audit instead:
- `EntityNode.cs:136/150/156/164` writes `Position`, then `Basis` **or** `Rotation`, then `Scale` — 2–3
  separate property sets, each triggering `_propagate_transform_changed`. Collapse to one `Transform3D`
  write (June's R13, never done).
- `PlayerModel.cs:412` calls `GetBoneParent(i)` **inside the per-bone loop every frame** — the hierarchy
  is immutable after `Setup`. ~60 wasted native reads/player/frame (~420/frame). Cache to an `int[]`.
- `ClientWorld.cs:1215` reads `pm.Visible` (native GET) unconditionally per player per frame.
- `PushBones` does a global→local→global round trip: we compute model-space bones, `AffineInverse()` each
  into parent-local (`PlayerModel.cs:413`), and Godot recomputes globals to build the skinning texture.
  `RenderingServer.SkeletonBoneSetTransform` takes skeleton-space directly — a **spike**, not a certainty
  (bind-pose composition and the `_tagWeapon` marker need re-deriving).

**Menu residency is NOT a per-frame cost — measured, not assumed.** ~1829 of 3437 in-match nodes are menu
Controls (`Shell.cs` only sets `_menu.Visible = false`, never frees). But `FrameProfiler`'s census tags any
type with `IsProcessing()`/`IsPhysicsProcessing()` with `(procN)`, and that marker appears **zero times
across every session log** — not one menu node processes, and Godot 4 dispatches `_Process` from a cached
group list rather than walking the tree. It costs VRAM (~2.6 GB menu-resident) and heap, not frame time.
What *does* cost is the ~55–60 live `_Process` callbacks among the 86 overrides in `game/` — item R1.

### "What can move to the GPU?" — several things, ranked; and two traps

The framing constraint: `rest` is **draw-count-scaled** (1.40 ms @ 400 draws vs 2.32 @ 628). So a GPU move
only pays if it does not add nodes or draw calls.

| # | Item | Reclaims | Risk |
|---|---|---|---|
| 1 | **Lightgrid → two `ImageTexture3D` global uniforms sampled in `fragment()`.** The CPU sampler (`LightGridData.cs:93-138`) runs ~24–32 transcendentals per sample. Per-entity grid lighting is **not implemented yet** (`NetGame.cs:2854`) — build it as a texture and never write the CPU version. Hardware trilinear reproduces the 8-corner blend exactly; bake direction as a pre-converted `vec3` (interpolating lat/long bytes would be wrong). Bonus: per-pixel instead of per-origin. | avoids the whole feature | none |
| 2 | **Particle 4-pack, keeping DP's CPU sim and the 2-batch MultiMesh:** don't sort the additive stream (premultiplied additive is order-independent); move the spark/oriented basis into `vertex()`; do `SrgbToLinear` at spawn (color is spawn-constant); upload `n × stride` not full capacity; replace the per-particle `Dictionary` atlas probe with `int[256]`. | ~0.3–0.6 ms stormkeep + kills the 50–211 ms bursts | none — same math relocated |
| 3 | **DecalSplats → one merged `ArrayMesh`** with spawn time as a vertex attribute and `TIME`-driven fade. Can't be MultiMesh (geometry is uniquely clipped per splat) but can be one mesh. | 256 draws → 1, per-frame CPU → 0 | none |
| 4 | **HUD**: `NeedsRedraw` on the always-on set (`HudPanel.cs:166` defaults `true`; only 7 panels override); shaderize the base crosshair; bake the radar background to a texture (it re-records the whole minimap at display rate, `RadarPanel.cs:170`). Also every string costs **two** `DrawString` crossings for the drop shadow — a `FontVariation` outline halves it. | ~0.1–0.3 ms + canvas `rcpu` | low — gate must include animation windows |
| 5 | **Casings/gibs → 2 MultiMesh batches** (the `FaithfulParticleRenderer` pattern). | 128 draws → 2; drops the 10 Hz `_PhysicsProcess` amplifier | low |
| 6 | **Measure `r_occlusion_cull 1`** — the `OccluderInstance3D` is built, wired and cvar-gated, defaulted **0** "until this is measured" (`WorldOcclusion.cs:38-39`), and never was. One cvar. Caveat: Godot's occlusion culling is a software rasterizer on an engine worker — it moves work off *our* main thread, which is what we want, but may raise `rest`. | unknown | none — pure experiment |

**Trap 1: `cl_particles_modern 2` is not the particle fix.** The dual system is implemented
(`ModernParticleBackend.cs`, SDF collision, custom process shader) but it allocates **one
`GpuParticles3D` per emitter block per effect spawn**, replacing today's **2 total draw calls** with N
nodes + N dispatches + N draws — straight into the worst-scaling bucket. It also has no decals at all
(`:110-113`), unvalidated SDF axis encoding (`SdfCollisionService.cs:515` `TODO`), and a no-op blood
splat. Items above keep the 2-batch renderer.

**Trap 2 (verified today): the GPU-morph "silent fallback" does not apply to these maps.** The theory is
sound — any model whose surfaces use a generated Q3 animated-stage shader drops the whole model to
`ApplyFrameCpu` (`ModelAnimator.cs:386-394`). But the build breadcrumb shows catharsis loads exactly ONE
morph model, `models/players/model/model.md3`, and it reports **`gpu=True`**. Worth fixing for the CTF-flag
class, but it is not an fps win here. Do not rank it as one.

**Closed:** autosprite is already fully GPU (`AutospriteShaderGen.cs:57-70`) and beats DP, which does it
CPU-side. `md3.morph` and `cw.anim` read 0.0 ms. PhysicsServer is entirely unused. Don't re-chase these.

## 5c. VRAM investigation (Bryan 2026-08-02: "our VRAM usage seems very high")

The compression work IS on main (bec0d4e9/a28b534a, not a branch): `gl_texturecompression` + eleven
DP-parity category gates, compressing CPU-side at upload (`AssetSystem.PrepareImage → MaybeCompress`).
It shipped default-0 pending a re-measure (the July 3139→956 number was taken with inverted BC5 normals
on screen; `CompressSource.Generic` fixed that on 08-01).

New tool: **`r_vram_census [N]`** (console) + an automatic one-shot census in every profiler session
summary — estimated bytes per `TexCategory` + top offenders, so a "vram N MB" line names its own
composition. (`AssetSystem.VramCensus`; estimation is format-bpp × pixels ×4/3 mips.)

**Measured (catharsis, 6 bots, debug census runs):**

| | textures est | total VRAM monitor |
|---|---|---|
| `gl_texturecompression 0` (default) | **2378 MB**, 499 textures, ALL rgba8 | ~3.4 GB |
| `gl_texturecompression 1` | **805 MB** | **1578 MB** |

Per category (off → on): Color 847→141 · Gloss 427→78 · Glow 427→67 · ReflectMask 215→56 ·
**Normal 458→458 (its gate defaults 0, DP parity — the biggest remaining class; a deliberate
Vortex-over-DP call could flip `gl_texturecompression_normal 1` now that the encode source is Generic,
at a quality-inspection gate)**. Top offenders are 2048² player-model texture sets at 21.3 MB apiece
(fullbright + _shirt/_gloss/_glow per model) — heavy even compressed; candidates for source downsizing.

**The release-flat mystery — SOLVED and fixed (2026-08-02, same day):** the release export printed 250
`texture compression skipped ... (unsupported source format)` lines per session. Root cause, verified in
Godot 4.6.3 source: **etcpak registers its BC/ETC encoders under `#ifdef TOOLS_ENABLED`**
(`modules/etcpak/register_types.cpp`) — export templates ship *decode-only*, so `Image.Compress(S3Tc)`
fails for every texture in the exported game while the editor binary compresses the same set fine. Not
init order, not wiring: an engine build-configuration fact. cvtt (BPTC) registers unconditionally.

Fixes landed, layered:
1. **Runtime fallback** (`AssetSystem.MaybeCompress`): probe the S3TC encoder once; when absent, route
   S3Tc→**Bptc** (BC7 — present in all builds, higher quality, 8 bpp vs 4 so roughly half the color-class
   saving). The census line now reports `compression: mode N, s3tc yes/NO, ok/bc7-fallback/failed` so an
   engagement failure can never be silent again.
2. **Engine patch** `godot-4.6.3-etcpak-runtime-encode.patch`: un-gates the encoder registrations +
   declarations (the encode kernels were always compiled into templates; only entry points were
   stripped). A locally-built `vortex2` template PROVED the patch works end-to-end in release
   (`compression: mode 1, s3tc yes, ok 250, failed 0`; textures 794 MB) — **and was then rejected by
   measurement**: the local MSVC build ran ~25% slower than the CI-built vortex1 across every CPU
   bucket on identical code and flags (catharsis p50 4.06→5.46 ms features-on, and **6.17 ms with the
   features OFF** — the control that convicted the template, not the features). The pin is back on
   vortex1 with the rejection documented in `engine.lock.json`; the patch stays in `patches[]` as the
   source of truth. **Next step: build vortex2 via the CI workflow** (same toolchain as vortex1),
   verify a capture pair against the vortex1 baseline, then re-pin — an explicit follow-up, not a
   local rebuild. A useful lesson got measured for free: template toolchain/codegen is worth ~25% of
   total CPU frame time on this workload — worth a dedicated look (`production=yes`/LTO is NOT in the
   current recipe on either build; turning it on in CI is a candidate for a real win on top of stock).
3. **Default flipped ON** in `vortex-client.cfg` (Bryan's call, deliberate deviation from upstream's 0),
   with the measured numbers in the comment. Remaining known caveats, accepted + documented: the texture
   cache is not keyed on the setting (mid-session flips affect later loads only), and a cold in-match
   sync load compresses on the main thread (bounded; the streamed paths compress on workers).

Residency policy is the other half of the number: the idle warmer makes the full stock asset set
GPU-resident every session by design (hitch avoidance), and caches never evict across maps
(`PERFORMANCE_REPORT.md` records 2451→2672 MB growth in one session). With compression landed, ~1.6 GB
resident is defensible; without it, an LRU/eviction policy is the fallback lever.

**Normal maps (Bryan's question: "industry compresses them — why doesn't DP, and shouldn't we?").**
Industry standard is **BC5/RGTC**: two independently-coded channels carry tangent-space X/Y at excellent
gradient quality, and the shader reconstructs `z = sqrt(1 − x² − y²)`. DP defaults
`gl_texturecompression_normal 0` because its runtime codec is the S3TC *color* family (DXT1/5), whose
color-weighted blocks visibly band normal maps — a quality call about the codec it had, not a claim that
normal compression is bad (DP happily consumes pre-baked BC content). We inherited both the default and,
until today, a worse constraint: Godot's `CompressSource.Normal` emits BC5, but all three consuming
shaders sample `.rgb` and use `.z` directly — BC5's blue reads 0 and inverts the lighting (the July bug).
**The right path, queued as a task:** teach LightmapShader + PlayerSkinShader (+ the DDS BC5 ingest,
which currently CPU-decodes to rgba8 for the same reason) to reconstruct Z behind a per-material
`norm_rg` flag, then flip `gl_texturecompression_normal 1` with `CompressSource.Normal` — reclaiming the
**458 MB Normal class at ~1/4 size with the proper codec**, plus free pass-through for shipped BC5 DDS.
Until then Normal stays uncompressed (correct, just big).

## 5d. Draw-reduction batch (landed 2026-08-02 — all cvar-gated, nothing reverted on neutrality)

Per Bryan's standing instruction: work that measures flat is kept behind cvars and documented, not
reverted. The `rest` bucket is draw-count-scaled (~4 µs CPU/draw: 1.40 ms @ 400 draws vs 2.32 @ 628), so
each item pays into `rcpu` AND `rest`.

| Item | Cvar / default | What it does |
|---|---|---|
| Sun shadow retired | `r_sun_shadow 0` | The DirectionalLight3D rendered 4 PSSM cascades into a 4096² atlas whose output BOTH consumer shaders provably discard. Off = fewer draws (casters×4), no atlas passes. `1` restores for A/B or a future consumer. |
| World shader `shadows_disabled` | — (unconditional) | The world receives no shadow map (light() drops directional light; no omni casts) — stops the per-pixel PCF tap chain at 4× MSAA that fed a discarded term. Retired as a pair with the sun default. |
| Opaque/masked world-shader split | — (automatic by `alpha_cutoff`) | The shared shader *contained* `discard`, classifying ALL world geometry alpha-discard and forcing fragment-shaded depth prepass + weak early-Z. Opaque surfaces now compile a discard-free program; grates/foliage keep the masked variant. One extra PSO family, warmed. |
| Shader dedup by generated code | — (structural) | `ShaderCompiler.SharedShader`: one compiled program per distinct generated GLSL instead of one per Q3 shader NAME (byte-identical code was compiled N times, N PSO families — part of the 84–89 sync compiles). Portal shader likewise collapsed 6→1. |
| Portal update interval | `cl_portal_update_interval 1` | Render each visible portal every Nth frame, phase-staggered; held frames keep the last texture (same staleness contract as the off-screen gate). 2 ≈ halves the measured ~1.4 ms p50 + 2× draws portal tax. Default 1 = parity; Bryan A/Bs the look. |
| Portal MSAA opt-out | `cl_portal_msaa 1` | 0 renders portal viewports MSAA-off (4× fragment/resolve for a ≤1024² projective sample buys little). Default inherit = parity, and avoids a new MSAA-keyed PSO family until the warm covers it. |
| Portal min-px gate | `cl_portal_min_px 0` | Freeze portals whose projected size is under N px on their last texture. 0 = off (parity). |
| Portal UpdateMode write gating | — (unconditional) | The per-frame `RenderTargetUpdateMode` re-assert per portal is now equality-gated (RenderingServer call only on change). |
| Map ambient emitters PVS cull | `r_pvs_cull_emitters 1` | func_pointparticles/sparks in rooms no viewpoint sees stop BOTH their GPU process dispatch and draw (courtfun: 44 always-on before). Cluster cached per emitter, re-derived on >32 qu movement; conservative like the world cells. |
| Map dynlights PVS cull | `r_pvs_cull_dynlights 1` | An out-of-view dynlight no longer enters the clustered light grid. Documented approximation: light-through-doorway from a hidden room can gate a frame early (cluster visibility isn't transitive); cvar restores always-on. |
| `WorldPvsCuller` point service | — | `Instance` + `ClusterAt`/`ClusterVisibleFromView` — the shared conservative PVS contract the two culls above ride on (portal exit views included). |

Not yet in this batch (queued tasks): DecalSplats 256→1 merged mesh, casings/gibs → 2 MultiMesh batches,
world-cell material merge analysis (the ~10-draws-per-instance NextPass/material split), the
`r_occlusion_cull` verdict (measured in this battery), and the IQM off-thread vertex build.

## 5e-RESOLVED (2026-08-03, interleaved A/B): the late-night slowdown was the compression cfg on an
## encoder-less binary — NOT the code batch, and NOT (only) thermals

Five interleaved catharsis cells, drift-controlled (A = morning code in a worktree + morning cfg;
B = tonight's code + tonight's cfg; identical machine window, warm caches, real content verified):

| cell | p50 ms | proc | rcpu | rest | draws |
|---|---|---|---|---|---|
| A1 morning | 4.60 | 1.88 | 0.71 | 1.87 | 253 |
| A2 morning | 4.10 | 1.71 | 0.64 | 1.67 | 251 |
| B1 tonight, comp ON | 5.46 | 2.16 | 0.93 | 2.11 | 253 |
| B2 tonight, comp ON | 5.06 | 2.05 | 0.83 | 1.95 | 424 |
| **B3 tonight, comp OFF** | **4.51** | **1.89** | **0.65** | **1.83** | 253 |

**B3 sits inside the A band on every bucket ⇒ tonight's code (draw batch, shader split/dedup, shared
geometry, census, Prof fix) is perf-clean.** The whole delta was `gl_texturecompression 1` running on a
binary with NO encoder and NO early-out: that build re-attempted and failed compression per texture
through the whole idle-warm tail (`stream.predecode` +70%, worker contention + a failure print per
texture). The NEWEST build already guards this (probe once → skip all → one loud warning), so the
shipped default is safe on vortex1 — verified by the `guard_verify` capture. Consequences:

- The local-vortex2 "~25% slower" rejection is now CONFOUNDED (its runs carried the same comp-on cfg —
  with a working encoder doing real load-time work — plus possible thermal). The rejection stays (the
  CI toolchain is the right provenance anyway) but its magnitude claim is withdrawn; the CI-vs-local
  codegen question moves into the vortex2 verification A/B.
- The 2026-08-02 §5e thermal narrative below is retained as history; its discipline rule stands
  (cool machine, interleave arms — the interleaved protocol is exactly what resolved this).
- **Guard verified (2026-08-03 00:08, `guard_verify`):** the newest binary with the comp-on defaults
  reads **p50 4.18 ms (proc 1.78, rcpu 0.62, rest 1.69, draws 253)** — in-band — with the census line
  honestly reporting `s3tc NO … bc7-fallback 289, failed 289` (i.e., 289 cheap early-outs, one warning).
  The shipped defaults are safe on vortex1 today and become the full VRAM win the moment the CI
  template lands. The A/B worktree lives at `../VortexArena-abtest` (morning code + nade WIP, own
  export + scratch profile) — keep it; interleaved arms are now the house protocol for any disputed delta.

## 5e. Late-night addendum: tonight's perf numbers are SUSPECT — do not baseline on them

The 23:0x runs degraded monotonically on effectively identical code (catharsis post-load p50:
4.06 ms morning → 5.46 features-on → **6.17 features-OFF** → 6.91 after the vortex1 template restore),
with every CPU bucket inflated ~25-70% and the GPU flat. The features-off control exonerates the new
cvar'd work; the vortex1 restore exonerates the local template as the *sole* cause; identical-code runs
getting progressively slower across half an hour points at **machine state** — the 22-core engine
compile ran immediately before this window (heat soak / sustained-boost limits), on the same evening as
~20 prior captures. This is the playbook's "quiet machine" rule wearing a new hat: **also require a
cool machine — do not capture within ~15 min of a large compile, and interleave A/B arms** (the morning
runs, whose arms interleaved naturally, stayed consistent for hours).

Standing items from tonight, in order: (1) cold re-baseline of the current build (all features on,
vortex1) on a rested machine — that pair becomes the new reference and the honest measurement of the
draw batch; (2) CI vortex2 template (etcpak patch + `cvtt_export_templates=yes`), verified against that
baseline — it turns the compression default from a loud no-op into the measured 3.4→~1.6 GB;
(3) the template-codegen investigation (a ~25% swing hides real wins if either arm is thermally dirty —
re-run the local-vs-CI comparison cold before drawing the LTO conclusion).

### 5e-POSTSCRIPT (2026-08-03): the A/B orchestration itself broke — root causes + the harness that replaces it

Found while investigating "the A/B run that's still running": an **orphaned watcher loop** from the 23:48
batch had been polling every 30 s for ~11 hours. Timeline from the task files: the batch's first cell
launched the A-arm worktree **before its maps were synced** (`data/maps` is gitignored, ~700 MB via
`vx setup` — a fresh worktree has none), and the engine does not fail on a missing map: it printed
`map 'catharsis' not found in the VFS — listen server runs on a flat floor` and kept benchmarking the wrong
scene. Moments later the batch task died (the session hit its context limit right then), so the
`AB BATCH1 DONE` marker its watcher was grep-polling for never appeared. The maps were synced at 23:50 and
all six real cells completed 23:52–00:01 — **the §5e-RESOLVED conclusion stands on those cells** — but
nobody killed the watcher for the dead first batch.

Three defects, three fixes (landed 2026-08-03, no captures re-run):
1. **A degraded run could pass.** `perf-run.ps1` now throws **before launch** when the map isn't in the
   checkout's content, and exits 1 **without writing json** when the stdout capture shows the flat-floor
   fallback. Verified against the real failing output (detects it) and a known-good capture (doesn't).
2. **Fresh A-arms start contentless.** `tools/ab-run.ps1` (new) preflights both arms and syncs
   `data/maps` A←B via additive robocopy before any cell.
3. **The orchestration was orphanable.** `ab-run.ps1` is sequential and foreground — no background tasks,
   no completion markers, no watchers; a dead run leaves nothing behind. It prints per-cell rows, per-arm
   medians, the B−A delta, and both arms' HEADs.

## 5f. What Base/DP does that we don't (audit 2026-08-03, `../Base` + `../Base/darkplaces`)

Ranked by value to a CPU-bound port. Every line has Base evidence; several invert an assumption we made.

**1. Sound: DP never pushes positions from script — the engine PULLS them.** A channel stores
`entnum`/`entchannel` (`snd_main.c:1547`), and every `S_Update` re-derives the origin from the entity,
including CSQC tag matrices (`snd_main.c:1200-1228`, `csprogs.c:1172`); `cl_gameplayfix_soundsmovewithentities`
defaults 1. Xonotic QC calls `sound()` only on state transitions — start once, stop once
(`csqcmodel_hooks.qc:691-706`). **Our per-frame `GlobalPosition` + volume push is work Base does at no
level.** Today's change-gating is a band-aid; the real fix is to PARENT the `AudioStreamPlayer3D` to the
emitter's node so Godot's own transform propagation does it, and the push disappears. DP also merges
duplicate static sounds into one channel ("so we don't mix five torches every frame", `snd_main.c:2169`)
and keeps channels in a flat fixed array, not a managed live-sound list.

**2. LOD is computed and DISCARDED in this port** (`ClientWorld.ApplyLod` → `_ = SelectLodIndex(...)`),
yet the stock models ship `_lod1`/`_lod2` (e.g. `erebus_lod1.iqm`). Worse, Base's threshold is far nearer
than the cvar names suggest: `f = (dist * viewzoom + 100) * detailreduction`, `/ view_quality`
(`csqcmodel_hooks.qc:84-85`) with `cl_playerdetailreduction 4` in every profile below ultra
(`effects-normal.cfg:8`) — so **LOD1 lands at ~156 qu and LOD2 at ~668 qu**, not 1024/3072. We render
full detail everywhere. Wiring the swap is a real, unexploited win (fewer verts, and fewer bones to pose
if the LOD rigs are reduced — verify before claiming the bone half).

**3. Adaptive quality feedback — the single most applicable design for a 2.0 ms target.**
`CL_UpdateScreen` (`cl_screen.c:2130-2213`) EMA-filters measured render time, computes an adjustment
toward `1/cl_minfps`, applies one-sided hysteresis, clamps the per-frame step
(`cl_minfps_qualitystepmax 0.1`) and the range (`0.25..1`), then publishes `r_refdef.view.quality`, which
LOD (`view.qc:1685`), particle draw distance (`cl_particles.c:2935`) and offsetmapping all consume. A
CPU-bound port with a frame-time target should have exactly this loop.

**4. Our per-entity PVS shape vs DP's.** Today's memo (97.5% of descents skipped) reaches a similar place
by a different route. DP: world visibility resolved ONCE per frame into flat `world_leafvisible[]` /
`world_surfacevisible[]` byte arrays (`gl_rsurf.c:511-524`), then per entity an ITERATIVE descent with an
explicit `nodestack[1024]`, returning at the first visible leaf (`model_brush.c:394+`) — no recursion, no
PVS bit decoding per entity, no allocation (viewcache arrays resized only when counts change). Server-side
it caches each entity's cluster list ON THE ENTITY, recomputed only when the cull box moves, capped at
`MAX_ENTITYCLUSTERS 16` (`sv_send.c:659-675`), so the per-client test is ≤16 bit tests. Also: frustum
culling tests ONE corner selected by precomputed plane signbits, 5 dot products, near plane skipped
(`gl_rmain.c:3436-3467`).

**5. Things Base deliberately does NOT do — check we haven't over-built them.** Xonotic ships
`r_cullentities_trace 0` (client) and leaves it to `sv_cullentities_trace 1`
(`xonotic-client.cfg:987`, `xonotic-server.cfg:591`). `cl_particles_visculling` defaults 0 — no PVS test
per particle. CSQC predraw is NOT visibility-gated (`clvm_cmds.c:811`), and effects/glowmod are recomputed
from scratch every frame with no dirty flags (`csqcmodel_hooks.qc:546`, `:339`) — the cost control is that
each branch is a test on a usually-zero mask. Where our port added change-detection machinery around cheap
work, that machinery may cost more than the work.

**6. Skeletal: Base does TWO `skel_build` calls per player per frame.** Bones are pre-sorted into
contiguous UPPER/LOWER runs once at skeleton creation (`player_skeleton.qc:67-82`), then each run is one
engine call whose C body loops the bones (`clvm_cmds.c:4651`), blending dual quaternions over 7×int16
poses (`model_alias.c:65-135`). Per-bone QC work is only the aim bones. Our `PushBones` does per-bone
managed→native interop — the `RenderingServer.SkeletonBoneSetTransform` route (§5b) is the equivalent
shape. Setup work is gated on `modelindex`/`skin` change (`player_skeleton.qc:19`), as is `animdecide`.

**7. Allocation discipline.** All transient render data comes from a frame-scoped bump allocator reset by
rewinding a pointer (`R_FrameData_Alloc`, `gl_rmain.c:3521-3571`); array "clears" are generation/sequence
counters, not memsets (`sv_ents.c:396`; the collision trace cache bumps a 1-byte sequence,
`collision.c:1548-1571`). DP also ships per-phase timers as a first-class feature (`R_TimeReport`,
including "audioprep"/"audiospatialize") — the same instinct as our `Prof` scopes.

**8. Fixed-timestep systems with interpolation and an fps floor** (`ecs/lib.qh:34-62`): the sim advances
in fixed `dt` with an accumulator clamped by `minfps`, then components interpolate by the remainder. A
port running everything at render rate lost that decoupling.

## 5g. The separate render thread — the biggest single win, and our own docs hid it (2026-08-03)

`PERFORMANCE_REPORT.md` §S7 asserted `rendering/driver/threads/thread_model` was "removed in Godot 4 …
the key is inert … tested → reverted". **Every clause was false**, verified against the pinned 4.6.3
source (see the corrected S7 entry for the file:line proof). At Godot's DEFAULT the entire render pass —
cull, render-list build, sort, submission, present — runs INLINE on the main thread inside
`Main::iteration`, immediately after `SceneTree::process`. That *is* the `rest` bucket, and it is why
`rest` tracks draw count.

| catharsis demo, release, 90 s | before | after |
|---|---|---|
| p50 frame | 4.21 ms | **3.72 ms** |
| avg fps | 237 | **269** |
| `rest` | 1.76 | **0.78** |
| `proc` | 1.70 | 2.06 |

The bucket movement confirms the mechanism rather than just the outcome: `rest` halves because draw left
the main thread; `proc` *rises* because RenderingServer calls from main now marshal through
`CommandQueueMT` instead of calling directly. Net **+13%**. (The draw counter reading 515 vs 253 is the
documented racy-under-threading read, not a real doubling.)

Not yet default-safe: upstream flags resize/particle crashes and `CommandQueueMT` contention against
background resource loading (godot#112452) — which is exactly what `BackgroundAssetStreamer`/`IdleWarmer`
do. **Gate on a soak + window-resize/alt-tab pass before shipping.** Also landed alongside:
`depth_prepass/enable=false` (the prepass re-records the whole opaque list a second time — CPU spent to
save overdraw we don't need at 84% GPU idle).

## 5h. Per-frame callback audit — the menu was working while invisible (2026-08-03)

Full inventory: **104 Godot callbacks** in `game/` (81 `_Process`, 2 `_PhysicsProcess`, 19 input-family,
2 `_Notification`); `src/` has none. Measured dispatch cost is ~177–224 ns per callback per frame in a
**release export** (godot#89826, #115960) and each C# node pays **two** native↔managed crossings, because
`CSharpInstance::_call_notification` fires unconditionally even when `_Notification` isn't overridden.

**Calibration first, because it inverts the usual advice:** at ~200 ns, our ~150–200 processing nodes are
≈0.04 ms — about 2% of `proc`. The dramatic 14× figures circulating in Godot issues are 5,000–100,000-node
scenes, and one headline report is **13.5× inflated by running in the editor** (239 ms editor vs 17.67 ms
exported, same scene). **Consolidation only pays where instances are numerous; a singleton doing real work
is not worth touching.**

**What the audit actually found — bugs, not micro-optimizations.** The menu tree stays INSTANTIATED during
a match (`Shell` only sets `_menu.Visible = false`) and `Shell` runs `ProcessModeEnum.Always`, so these ran
every frame while invisible: `MainMenu._Process` executed the full `LayoutFrame()` **completely ungated**
(~40 marshalled property writes across 6 tiles); `LeaveMatchButton` wrote a marshalled **string** property
(`Text`) plus `Disabled` every frame for the whole match after the first Escape; `PauseMenu` did a cvar read
+ string compares on the same lifetime; `CreditsScreen` kept auto-scrolling a hidden pane. All four now gate
on `IsVisibleInTree()`.

Biggest per-instance item, also landed: **`Md3Morph`** — one node per `.md3` instance (items, world weapons,
map props, effect models; hundreds alive), each idle one paying dispatch to reach `if (!_playing) return`.
`_playing` now routes through `SetPlaying()`, which toggles `SetProcess`, so a statically-posed model never
joins the process list. Plus `DamageTextLayer` got the empty early-out its siblings already had.

**Queued, ranked by (live count × cheapness of body):**
1. **`SmoothScroll` → static drive from `MenuRoot`** — ~26 live instances, each with a `_Process` AND an
   `_Input` on the **global** stage, so every mouse-motion event costs 26 dispatches (we ship a mouse-flood
   engine patch, so input volume is known-high). It already keeps a static `Live` list. **52 callbacks → 2.**
2. **Casings/gibs parent-driven** — up to **164** live `_PhysicsProcess` at cap (`MaxCasings 100` +
   `MaxGibs 64`); both parents already own the child lists. Also evaluates the slowmo scale once, not 164×.
3. The remaining 23 HUD panels via `HudPanel.DriveFrame` (cheapest bodies first: `RadarPanel` is literally
   `=> QueueRedraw()`).
4. **`Shell._Process`** calls a Win32 `GetForegroundWindow` + several cvar reads **every frame**, unthrottled.
5. `ScreenshotService._Input` runs a bind-table lookup on **every** input event — needs a type gate.

**Doc correction found in passing:** `HudManager.cs:297` claims the migrated panels' "callbacks are switched
off in `HudPanel._Ready`". There is no such `_Ready`. They work because their `_Process` overrides were
deleted — the next migration must delete, not rely on a mechanism that doesn't exist.

## 5i. THE MID-MATCH FREEZE: unbounded particle bounce traces (fixed 2026-08-03)

**This was the hitch that made the game feel bad.** Repeated multi-hundred-millisecond mid-match freezes on
stormkeep — 767.6 / 833.8 / 837.3 / 874.3 / 879.9 ms, five of them in one 200 s capture, all *after* load.

**The false lead.** The frame tree named `particles.cpu`, and the event log showed
`particles: fp_premul capacity -> 2048 (GPU realloc); -> 4096 (GPU realloc)` on the hitch frames. That looked
conclusive: a MultiMesh `InstanceCount` write reallocates the GPU buffer. `FaithfulParticleRenderer` was
changed to a fixed `MaxInstances = 8192` allocation with `VisibleInstanceCount` as the per-frame limiter
(the grow-then-decay policy is gone — that change is right and stays). Result: **every realloc event
disappeared and every freeze remained** — 879.9 ms still there. The reallocs were a *correlate* of the same
bursts, not the cause. Worth stating plainly because the evidence for the wrong answer was strong.

**The actual cause** — `ParticleSim.Update`, the bounce trace. Every bouncing particle got a full world BSP
sweep every frame, with no bound of any kind. The pool ceiling is 65,536. One heavy burst therefore means
tens of thousands of world sweeps in a single frame, and at a realistic ~25 µs per sweep on a map this size
that lands exactly in the 700–900 ms range observed. It is also self-amplifying: a long frame means a large
`frametime`, which means each particle steps further, which means each sweep crosses more BSP nodes, which
makes the *next* frame worse.

**The fix** — a per-frame trace budget, `ParticleSim.TraceBudgetPerFrame` (default 512), with a cursor that
resumes next frame where this one stopped, so no particle is starved. A particle that misses its budget flies
ballistically for a frame or two before its next collision check. The fidelity cost is small for a real
reason: DP only traces at frame granularity anyway, so a bounce is already resolved up to a frame late, and
the visible signature (a spark dying against a wall) survives because the particle is still traced within a
few frames. Set `TraceBudgetPerFrame = int.MaxValue` to restore the old unbounded behaviour. All 4111 tests
pass unchanged — the budget never binds during the C-reference parity replays.

**Measured, stormkeep 200 s, 8 bots, release export, same scenario:**

| | before | after |
|---|---|---|
| post-load hitches > 100 ms | 7 | **0** |
| worst post-load hitch | 879.9 ms | **47.8 ms** |
| worst *CPU-side* post-load hitch | 879.9 ms | **34.9 ms** |
| median frame | 5.8 ms | 4.1 ms |
| avg fps | 156 | 216 |

`particles.cpu` now reads **0.9 ms** in the hitch trees it still appears in. The remaining 47.8 ms is an
EXTERNAL frame (`proc 2.5, gpu 1.1, rest 44.5`) — OS/compositor/driver, game-side quiet.

**Lesson for the profiler, unresolved:** during these freezes `particles.cpu` reported 2966 ms *inside an
880 ms frame* (337 %), which is impossible. The independent sampling watchdog was the trustworthy signal
(1864/1870 samples in `particles.cpu`) and it is what actually located this. Scope accounting still
mis-attributes across frame boundaries when a single scope spans a frame flip; the `ScopeToken` identity fix
in §3 did not cover that case. **Trust the watchdog over the tree for giant frames.**

**What is now at the top of the list** — and a correction to make before chasing it. The run-summary line
`scope coverage debt: proc:other dominated 27 hitch(es)` reads like proc:other is the top in-match problem.
It is not: that counter spans the whole session and is dominated by the **load** phase. Split by phase:

*Post-load (t > 40 s), 38 hitches, none over 100 ms:*
1. **EXTERNAL — 47.8 ms**, the worst remaining frame, and it is not ours: `proc 2.5, gpu 1.1, rest 44.5`,
   game-side quiet. OS/compositor/driver. 2 occurrences.
2. **PIPELINE-COMPILE and its `proc:other` companions.** Only 5 post-load hitches have any `proc:other` at
   all (max 16.2 ms), and **4 of the 5 sit within 0.0–2.4 s of a detected pipeline compile**, clustered at
   t=88–95 s. So the unscoped time is largely first-sight shader/pipeline compilation the classifier only
   partly attributes — not a missing Prof scope on one of our nodes. **The fix is pipeline warmup, not more
   scopes.** Bots rotate weapons every 8 s here (`bot_ai_weapon_rotate 8`), which is what keeps dragging
   never-before-drawn material/mesh/light combinations into view.
3. Scattered CPU-LOGIC, 20–35 ms, no single dominant scope.

*Load phase (t ≤ 40 s):* 24 reported hitches, 0.49 s of stall total, worst 39.6 ms — also all pipeline
compiles and `proc:other`. The 847.5 ms "worst frame" in the fps summary is **not** among them: it falls in
the window where hitch reporting is suppressed, i.e. it is a loading-screen frame (the `iqm.mesh` asset-build
work, task #13). Worth fixing, but it is a loading screen, not a felt in-match stutter.

**So the menu-residency lead is dead.** The node census is flat all session (`Label` 878→876, `CvarCheckBox`
104, `HSlider` 62, ~1,900 resident Control nodes) — constant, not spiking, and it does not correlate with the
hitch timestamps. Resident menu widgets are a memory and tree-iteration cost, not the hitch cause.

## 5j. The freeze took four laps, not one (2026-08-03) — and what each lap teaches

§5i above declared victory after the trace budget; the next morning's runs took it back — same particle
code, 6 freezes up to 841.8 ms, watchdog again ~99% in `particles.cpu`. The clean 01:53 run was a small-burst
run, not a fixed build: the freeze scales with burst magnitude, and the morning slaughters simply got bigger.
There were FOUR unbounded per-particle frame costs, and every lap the watchdog (never the tree — the tree
kept reporting impossible 300–680% scope times on giant frames) named the survivor:

1. **Bounce traces** (§5i) — necessary, insufficient. The first cut was also a first-come spend counter that
   starved every particle above the budget in pool order; the comment promised a cursor the code didn't have.
2. **Content checks** — `PointContents` per liquid-friction particle plus per Blood/Bubble/Rain/Snow particle
   EVERY frame: tens of thousands of broadphase queries at a full pool. Capped the same run — freezes stayed.
3. **The renderer depth sort** — `List.Sort` with a comparison delegate over up to 65k indices, recomputing
   both distances per comparison through random ~100-byte struct reads: >1M delegate calls and ~2M
   cache-missing distance computes in one frame. Found only after `particles.sim`/`particles.sync` child
   scopes split the parent — the watchdog then said `particles.sim`, proving the sort fix necessary but,
   again, insufficient.
4. **The pool ceiling itself** — 65,536 simulated against 16,384 drawable (two fixed 8,192 MultiMesh
   batches). Size is what turned the fair rings against us: at 65k live a trace-ring lap is 128 frames, so
   the resume segments accumulated into map-length BVH sweeps and 1,024 of those was a freeze all by itself.

**Landed shape** (`ParticleSim`, all tunable statics, `int.MaxValue` disables): fair rotating rings over
live-particle ordinals — `TraceBudgetPerFrame` 512, `ContentBudgetPerFrame` 2048 — with 2× per-frame spend
caps as the mega-burst backstop; resume sweeps from `Particle.LastTraced` (no tunneling between ring turns),
length-clamped by `TraceMaxSegment` 1024 qu; cached `Particle.InLiquid` on skipped frames; pool ceiling
16,384 = the drawable ceiling, so truncation past it is invisible by construction. Renderer: one sortable
ulong key per particle (`~bits(distSq) << 32 | poolIndex` — non-negative float bits are order-isomorphic,
the embedded index is DP's pool-order tie-break) + `Array.Sort(keys, indices)`; the overflow clamp now packs
the NEAREST window (it used to keep the first `MaxInstances` of a farthest-first stream — i.e. keep the far
particles and drop the ones in the player's face).

**Parity:** pools at-or-under the ring widths are fully covered every frame — the C-reference replays are
bit-identical, all 10 pass untouched. `ParticleBudgetRingTests` pins bounded-frame + no-starvation against
the default budgets.

**Measured** (stormkeep 200 s, 8 bots, release, morning machine-state that produced the 6-freeze run):
post-load hitches >100 ms **6 → 0**, worst post-load frame **841.8 → 28.7 ms**, median 5.9 → 4.1 ms,
avg 150 → 215 fps. `particles.*` no longer appears in the top-10 hitch list at all.

## 5k. The equip pipeline compiles: we precached the wrong node (2026-08-03)

Bryan's challenge — "I would figure that we would have precached all the weapons" — was correct, and the
audit found the precise gap. MenuAssetWarmer + `PrecacheWeaponModelsAsync` warm every weapon's
parse/texture/material caches, and the precache even warm-RENDERS the `v_` models offscreen. But since the
r9 viewmodel rework the node a first-person equip actually renders is the **h_ hand rig** (DPM full-model
rigs draw their own gun+hand mesh; IQM invisible-hand rigs carry the `v_` on a live bone), and the rig only
ever got a throwaway attach-transform build, freed **unrendered**. A Vulkan pipeline is compiled on first
DRAW of (shader × vertex format × pass) — a skinned rig is a different vertex format — so every weapon's
first mid-match equip still sync-compiled. With `bot_ai_weapon_rotate 8` + spectate that is exactly the
SYNC compile cluster at t≈88–95 s. The muzzle-flash models (`flash.md3`/`uziflash.md3`) were never precached
anywhere and compiled on the first devastator/machinegun shot.

**Landed:** the precache builds the REAL equip (`ViewModelEquip.Build`) per weapon and warm-renders it (also
seeding the shared skeletal-geometry cache, so live equips reuse the mesh built here); muzzle models warm
with the weapons. And the MenuAssetWarmer staging is now a **mid-match fallback** (Bryan's "graceful load"
ask): a cold-model equip raises the placeholder, streams read/parse/texture/material through the background
lane, and retries on a per-frame dictionary probe (`AssetLoader.IsModelPrepared`) until hot — no more
synchronous cold builds on the equip frame. Measured: the t=88–95 s cluster is gone; ~2 post-load compile
singles remain, worst 15.3 ms.

**What remains on the hitch list** (same capture, post-load, worst first): scattered 20–29 ms CPU-LOGIC
with `proc:other` ~15 ms (the watchdog says `(unscoped)` — genuinely unattributed main-thread work, now
small enough to hunt calmly), ~22 ms VSYNC/PRESENT singles (`rest`-dominated, game-side quiet), and the two
pipeline-compile singles. Nothing over 28.7 ms.

## 5l. The sub-40 ms campaign (2026-08-03 afternoon): three passes, two classes dead, honest residue

Bryan: "I want *zero* hitches." Baseline for this stretch: stormkeep 39 post-load hitches / 644 ms total /
worst 28.8 ms; catharsis 65 / worst 37.6 ms. Method: census every remaining hitch by (class, watchdog
phase), attack the biggest attributable group, verify by capture, repeat.

**Pass 1 — the profiler was hitching the game it profiles.** 44 of the 50 VSYNC/PRESENT + EXTERNAL hitches
sat within 150 ms of a `[frameprofile]` snapshot block: EmitSnapshot recursed the whole scene tree
synchronously (4+ interop calls x ~3,500 nodes) and echoed four lines through the redirected-stdout pipe.
Census walk is now INCREMENTAL (250 visits/frame, armed after each emit, printed a window later); the
periodic block is file-only. Verified: that class went 17 -> 0. Also: the ng.viewfx class (10-15 ms +
~1.5 MB alloc per weapon switch) was EquipNetworkedWeapon rebuilding the first-person rig on every switch —
now a per-weapon EQUIP CACHE seeded by the precache with the exact nodes it warm-renders
(ViewModel.ReleaseWeaponModel hands the outgoing node back detached). Verified: 7 -> 0.

**Pass 2 — decal splats: one mesh, aging in the shader.** The unscoped CPU-LOGIC hitches sat on draw-count
spikes (draws 755 / objs 2525 vs ~310/~2000 steady). DecalSplats was node-pooled but still one
MeshInstance3D + ShaderMaterial per splat = 256 objects/draws at cap plus a fade-uniform push per splat per
frame. Now ONE ArrayMesh on ONE node, UVs remapped into the particle-font atlas (ParticleFont.CellUvRect),
and fade computed in the shader from a per-vertex spawn stamp (UV2) against a single `now` uniform — mesh
rebuilt only on add/prune. Also cn.events/cn.sounds child scopes (the ng.poll hitch class was event/sound
bundle handlers, 10-15 ms with cn.snapshot at 0.2). Verified capture: 21 hitches / 331 ms / worst 23.2 ms,
median 3.4 ms, avg 266 fps — the best stormkeep numbers recorded, and the steady draw floor dropped.

**Pass 3 — casings: struct-array sim + two MultiMesh batches.** 100 CasingBody nodes x _PhysicsProcess ->
one pool + one callback (scoped `casings`, reads <0.05 ms/frame), two draws, buffers sized once. End-of-life
is a short floor-sink instead of per-instance alpha (does not compose with skin ShaderMaterials under
MultiMesh; reads the same at casing scale). Warm pass warms MULTIMESH instances — instancing is its own
vertex format/pipeline.

**Honest verification note:** the pass-3 capture read WORSE than pass-2 (38 hitches / 633 ms / 227 fps vs
21 / 331 / 266) with the new work itself clean (zero casings-attributed hitches; the growth was ng.input and
GC). These single-run slaughter captures swing ~2x run-to-run (the section-5e lesson at smaller scale) — at
this depth the per-run hitch census is inside the noise floor, and only structural evidence (callbacks
removed, draws capped, scopes clean) plus multi-run medians should drive conclusions.

**Remaining kill-list (worst first, all <=35 ms):**
1. `ng.input` 15-24 ms spikes — the local weapon-fire path (one record showed `mp.weapon` 14.7 ms on a
   mortar shot at t=41: likely first-fire lazy init somewhere in the weapon think). Needs child scopes.
2. `(unscoped)` CPU-LOGIC ~15-28 ms — shrunk but present; next lever is the GIBS MultiMesh conversion
   (same recipe as casings: 9 meshes, cap 64 — the remaining per-node burst renderer), then whatever the
   draw census shows.
3. GC-PAUSE up to 34 ms + steady 15 MB/s alloc — needs an allocator audit (the equip-cache already removed
   the biggest known per-switch burst).
4. `(post-process)` 10-16 ms — deferred-free churn candidates shrink with the gibs conversion.

## 6. Measurement discipline updates

- **Post-load boundary**: use `--postload 25` until a real world-entry marker exists; better, emit a
  `Prof.Event("world-entry")`/counter when the first snapshot applies and let perf-report cut there.
- Debug perf-run works again (Find-Godot ASCII fix); debug builds remain non-representative for
  *verdicts* but fine for cache/ordering forensics.
- The Prof fix invalidates prior suspicions built on `particles.cpu`/`proc:other` totals from hitch
  trees where a leak was active. Re-census before trusting any scope ranking not re-measured today.

## 7. Open probes

1. **The entry-window cross-thread stall edge**: main in `ng.camera`/`ng.input` and sim in
   `Bots.ServerFrame` unscoped remainder, both ~zero-alloc, 0.6–1.5 s, only during the build storm.
   Candidates: GC suspensions under the 100–180 MB/s build alloc rate; a lock/marshal shared with the
   camera-spectate or asset path; `sv.gatewait_ms` under-counting. Bracket `ServerFrame`'s remaining
   blocks ((d)/(e)/(h)/(i)) or sample the sim thread's stacks if it survives P1.
2. Why catharsis draws p50 fell 433→251 vs 07-06 (content? culling? camera luck) — worth one look while
   validating D-items.
3. Stormkeep bucket regression owners beyond particles (draws +27%, rest +0.4 vs 07-06) — re-decompose
   after P3/D-items land.

## 8. Suggested execution order

1. **P1 world-entry** (kills the worst hitch class) + §5.7's D1 dedup (shrinks PSO storm) — the two
   structural hitch levers.
2. P4 entity pool + P3 particle budget + §5.5 casings ager — variance + stormkeep.
3. The S-tier sweep: P5–P8, P10, D3–D4, G1–G2, §5.6 — each a small PR with a capture pair.
4. R1/R2 rest-bucket program + G3 settings wiring — the throughput long poles toward 2 ms.
5. Re-baseline `tools/perf-baselines/` after each phase (the checked-in baselines are 07-06 vintage).
