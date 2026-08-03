# Vortex Arena Performance Report — Hitching, Stutter, and Low-FPS Causes

**Date:** 2026-06-10 · **Engine:** Godot 4.6.3 (Forward+) · **Runtime:** .NET 8 (default GC/JIT config)
**Scope:** every mechanism found that can cause frame hitches, micro-stutter, or sustained low FPS, with concrete optimization plans, including parallelization strategies.
**Method:** four parallel code audits (GC/alloc, Godot rendering, loading/threading, sim-loop/pacing) over `game/` + `src/` (~46.5k LoC), cross-checked against the live `FrameProfiler` measurements taken earlier today. Every file:line below was verified by reading the code, not just pattern-matched.

---

## 0. Executive summary

There are **four distinct problem classes**, and they need different fixes:

| # | Class | Felt as | Status | Biggest lever |
|---|-------|---------|--------|---------------|
| 1 | **First-use stalls** — lazy asset load + runtime-created materials compiling shaders/pipelines on first draw | one big hitch on first shot / first pickup / first effect of a kind | partially mitigated | cache runtime materials + GPU warm pass at map load; precache **all** assets |
| 2 | **Frame pacing / vsync beat** | continuous judder or "drops to 60 from 160" while CPU+GPU are mostly idle | **root-caused empirically** (`rest` dominates hitch frames) | Mailbox vsync option, fps cap guidance, keep per-frame cost ≪ refresh budget |
| 3 | **Steady per-frame CPU & GC churn** | lowered fps ceiling, occasional Gen0 spikes in firefights | several fixes landed; effect-spawn churn remains | pool/cache `GpuParticles3D` + materials; HUD string/redraw caching |
| 4 | **Sim-loop scaling** | fps degrades with players/projectiles/entity count | not yet hit in 1-player tests; will bite in real matches | DP-style areagrid (spatial partition) for traces/triggers/radius queries |

**Key empirical facts already established** (via the in-tree `FrameProfiler`, `cl_frameprofiler 1/2`):
- In the investigated runs, hitch frames showed `proc ≈ 3 ms`, `rcpu+gpu ≈ 3 ms`, **`rest` 8–17 ms** → the stall was **vsync/present wait**, not game code, not GC.
- Measured allocation rate is modest (**40–87 KB/frame, near-zero collections**) → GC is currently *not* the dominant hitch source. The goal of the GC section is to keep it that way under combat load.
- `server.tick` spikes were a **symptom**: the 72 Hz accumulator runs catch-up ticks *after* a long frame (`ticks ≥ 2` in the hitch log means the cause is elsewhere).

**Already landed** (don't re-do): FrameProfiler + `Prof` scopes (`proc/rcpu/gpu/rest`, hitch logger, env banner); MD3 morph skip-guard for static frames; `CsqcModelEffects` light-list scratch reuse; gib/casing fallback mesh+material caching; projectile **trail** resource cache + `WarmupTrails()`; snapshot broadcast gated on `ticksRan > 0`; sub-tic eye extrapolation; stair-smoothing feedforward.

---

## 1. Class 1 — One-time hitches (first-use stalls)

These are the "game freezes for 30–300 ms the first time X happens" events. Two mechanisms stack: **(a)** synchronous disk-read + parse + GPU upload on the main thread, and **(b)** Godot compiling the shader/pipeline for a never-drawn material on first draw.

> Godot 4.6 note: the 4.4+ ubershader/pipeline-precompile system only helps materials that exist when their mesh is **instantiated/loaded**. Materials constructed at runtime (`new StandardMaterial3D`, `new ParticleProcessMaterial`) still pay shader + pipeline compile when they first enter the tree/draw. The cure is the same as for class 3: **stop constructing them at runtime — build once, cache, pre-instantiate during load.**

### 1.1 Runtime-constructed materials & particle resources (VERIFIED, HIGH)

Every call below builds fresh Godot resources **per spawn** — first use compiles a pipeline; every use churns native handles (see §3.2):

| Site | What's created per spawn |
|------|--------------------------|
| `game/client/EffectSystem.cs:533-562` (`BuildBurst`) | `GpuParticles3D` + `ParticleProcessMaterial` + draw-pass mesh |
| `game/client/EffectSystem.cs:584-615` (`BuildTrail`) | same |
| `game/client/EffectSystem.cs:634-660` (`BuildParticleMesh`) | `QuadMesh` + `StandardMaterial3D` |
| `game/client/EffectSystem.cs:903/918` (effectinfo burst) | `GpuParticles3D` + `ParticleProcessMaterial` |
| `game/client/EffectSystem.cs:1138/1147` (info mesh) | `QuadMesh` + `StandardMaterial3D` |
| `game/client/ProjectileRenderer.cs:339` (body) | `StandardMaterial3D` per projectile spawn (trail path is cached ✅) |
| `game/client/BeamRenderer.cs:195`, `LaserRenderer.cs:112-122` | material/quad on first beam/laser |
| `game/client/WeatherSystem.cs:107/134/206/213` | per-map weather emitters (load-time — OK, but include in warm pass) |
| `game/client/Decals.cs:171-192` | `ImageTexture` baked per unique decal color (has `_texCache` ✅ — first hit per color still stalls) |

**Plan A1 — resource caching (also fixes §3.2):**
- Key burst resources by `(EffectClass, tint-bucket)` — quantize tint (e.g. 4 bits/channel) exactly like `Decals.SolidTexture` does. All other parameters are pure functions of `EffectClass`, so the `ParticleProcessMaterial`, draw-pass mesh, and material are **shareable across emitters** (same reasoning that made `_trailResCache` safe).
- Same for projectile bodies: key by `BodyFamily` + color; share `CapsuleMesh`/`SphereMesh` instances.
- Pre-build the whole cache in `EffectSystem.Warmup()` / `ProjectileRenderer.WarmupTrails()` (both already run at map load, in the right order).

**Plan A2 — GPU warm pass at map load (the deferred item; do it offscreen to avoid the visible-flash problem):**
1. Create a 64×64 `SubViewport` (`UpdateMode.Always`, own `Camera3D`) during the loading screen.
2. Parent one hidden-cost instance of **every cached runtime material family** into it: each effect-class burst (1-particle, `Explosiveness=1`), each projectile body+trail, beam, laser, gib, casing, decal quad, viewmodel placeholder.
3. `await RenderingServer.FramePostDraw` twice (one frame to compile, one to flush), then free the viewport.
4. Result: first rocket/explosion/gib in real play hits a warm pipeline. ~50 lines; no visible flash because nothing touches the main viewport.
- Cheap verification: `cl_frameprofiler 1`, fire each weapon once — the `[hitch] … gpu` spikes on first-fire disappear.

### 1.2 Synchronous cold asset loads mid-game (VERIFIED, HIGH)

All asset loads are synchronous on the main thread; anything not precached stalls play on first use:

| Asset | Trigger | Cost | Path |
|-------|---------|------|------|
| Weapon model (v_/h_) | switch/pickup of a weapon not in `ComputeExpectedWeapons()` | 30–300 ms | `ViewEntityRenderer.Update → AssetSystem.LoadModel` (VFS read → IQM/DPM/MD3 parse → textures → ArrayMesh → materials) |
| Player model | first sight of a new player/model | 20–150 ms | `ClientWorld.BuildRenderNode → LoadSkeletalModel` (`ClientWorld.cs:~1298`) |
| Sound sample | first play of a sample | 5–50 ms (OGG decode) | `ClientWorld.OnSound → AssetLoader.LoadSound` (`AssetLoader.cs:483-510`) |
| Texture | first material referencing it | 20–100 ms | `AssetSystem.LoadTexture` (decode + `ImageTexture.CreateFromImage`) |
| effectinfo/particlefont | warmed at map load ✅ (`EffectSystem.Warmup`) | — | — |

Existing mitigation: `NetGame.PrecacheWeaponModelsAsync` (~`NetGame.cs:1081-1150`) warms only the *expected* weapon set; everything else can arrive cold.

**Plan A3 — precache everything, then stream the stragglers:**
1. **Warm all ~25 weapons** at load, not just expected (remove the `expected.Contains()` filter; ~50–100 ms more load time, hidden by the loading screen). Trivial, zero risk, big felt win.
2. **Precache combat sounds** (per-weapon fire/impact lists are already in the weapon defs + `SoundsList`) during a loading stage.
3. **Precache player models** for connected clients at join time (and default models at load).
4. **Background streaming for the rest** — see §5 strategy S1: `Task.Run` the VFS read + parse (pure C#), then build Godot objects on the main thread via `CallDeferred`, budgeted.

### 1.3 VFS resolve overhead (VERIFIED, MEDIUM)

`VirtualFileSystem.Exists` linearly probes every mount, and image/sound resolution probes **4 extensions × mounts** per name with **no negative-result cache** (`src/XonoticGodot.Formats/Vfs/VirtualFileSystem.cs:162-171`). Misses repeat the full scan every time (and many lookups are misses by design — e.g. `_norm/_gloss` companion probes).

**Plan A4:** add a `HashSet<string>` negative cache (cleared on mount changes) + a resolved-path cache keyed by base name. ~20 lines; compounds with every loader.

### 1.4 JIT warmup (LOW, release builds)

`XonoticGodot.csproj` / `Directory.Build.props` set no JIT/GC knobs → tiered JIT compiles novel call paths at tier-0 on first use mid-game (small but real hitches).
**Plan A5:** for exported release builds, publish with **ReadyToRun** (`<PublishReadyToRun>true</PublishReadyToRun>` per-RID) so game-code methods are pre-compiled. Don't disable tiered compilation globally (hurts startup + steady-state code quality).

---

## 2. Class 2 — Frame pacing (the empirically-confirmed one)

This is the judder you measured: **CPU and GPU mostly idle, but frames take 2–3× the refresh interval.**

### 2.1 Vsync beat (hypothesised from early runs — SUPERSEDED for the bot-load case; see §9)

With `vsync=Enabled`, `maxfps=0` on a ~160 Hz display, total frame work (~6 ms) sits right at the vblank budget (~6.25 ms). Any jitter (IDE overhead, a Gen0 GC, an unwarm pipeline) pushes the frame past the vblank → it waits for the *next* one → frame doubles/triples (the observed 12.5/16.7/20.4 ms frames with `rest` dominating).

> **⚠ Wave-0 correction (2026-06-11, §9).** Measured empirically with bots: `rest` is **NOT** the vsync beat here. Turning vsync fully OFF (`vid_vsync 0`, confirmed applied) does **not** remove `rest`, and in an exported **release** build the steady-state `rest` is just normal GPU + present (~3.5 ms). The large `rest` in the original (Debug/editor, windowed) runs was Debug-assembly `proc` spillover + windowed-DWM pacing, not a fixable vsync beat. **Mailbox/B1 is therefore not the lever** for the felt bot-load hitching — keep it as a user option, but the real causes are the Debug build (§9) and a GC stall (§9), not pacing.

**Plan B1:**
1. **Expose and try `DisplayServer.VSyncMode.Mailbox`** (`vid_vsync 2`?) — on Vulkan this renders uncapped and presents the latest complete frame at vblank: no tearing, no beat-doubling. This is the single best pacing fix for high-refresh displays. Wire it in `ClientSettings.ApplyVideoSettings` (`game/menu/framework/ClientSettings.cs:134-142`).
2. **`cl_maxfps` guidance**: when vsync is off, cap to a rate the machine holds *consistently* (or to the display rate). A cap slightly *under* the worst-case sustainable rate is smoother than uncapped. **Second reason to cap (per godot#105750, see §3.4):** per-frame allocation cost scales with framerate — uncapped at 200–600 fps multiplies any per-frame Godot-interop or managed alloc into a much higher *bytes/second* GC rate. Our measured 40–87 KB/*frame* is benign at 100 fps but becomes a Gen0 treadmill uncapped. A cap protects pacing *and* GC headroom at once.
3. **Test in an exported release build** — editor/IDE runs always load the Debug C# assembly (verified: Rider's Release switch does NOT change the launched game) and add scheduler noise. `./vx run` is that build (it exports when stale and places `data/` beside the binary); `./vx engine` installs the pinned export template. (Was `run-release.sh`, retired 2026-08-03 once `vx` carried its two workarounds.)
4. Everything in classes 1/3/4 that lowers per-frame cost adds headroom that prevents budget overruns in the first place.

### 2.2 Input quantization + local view stepping (PARTIALLY FIXED)

- Legacy input mode emits one command per 1/72 s regardless of display rate (`NetGame.cs:1516-1537`) → up to ~14 ms input latency and stepwise prediction. A **per-frame input mode already exists** (`cl_movement_perframe`, `NetGame.cs:1502-1515`, DP-style variable-dt commands).
- The 72 Hz view stepping is already smoothed: **sub-tic eye extrapolation is implemented** (`NetGame.cs:2312-2323`, `predicted += viewVelocity * _inputAccum`) plus stair smoothing (`NetGame.cs:2325-2336`).

**Plan B2:** feel-test `cl_movement_perframe 1` and consider making it the default; it removes both the input quantization and the residual tick-stepping (each rendered frame gets its own predicted move). Keep the legacy path for parity testing.

### 2.3 Catch-up amplification (VERIFIED, MEDIUM)

`SimulationLoop.Advance` (`src/XonoticGodot.Engine/Simulation/SimulationLoop.cs:114-137`) runs up to **`MaxTicksPerAdvance = 16`** catch-up ticks in one render frame after a stall — i.e. a hitch is *followed* by a frame doing 16× sim work (then drops backlog). With sim ~1 ms/tick today that's ~16 ms — itself a missed vblank.

**Plan B3:** lower the per-frame catch-up for the interactive client-hosted path — e.g. clamp to `min(4, backlog)` ticks per render frame and let the remainder drain over the next frames (brief, invisible slow-motion instead of a second hitch). Keep 16 for headless dedicated servers. One constant + one parameter; verify with the `ticks N` marker in hitch logs.

### 2.4 Remote-entity snapshot pacing (LOW)

`SnapshotInterpolation` lerps prev→cur with snap-on-stale (`src/XonoticGodot.Net/SnapshotInterpolation.cs`). Fine on LAN; on lossy/high-ping connections a late snapshot can pop. **Optional:** one-snapshot de-jitter buffer (adds one snapshot interval of latency) behind a cvar. Not a local-play issue — lowest priority.

---

## 3. Class 3 — Steady per-frame CPU & GC churn

### 3.1 Current GC posture

.NET 8 defaults: workstation concurrent GC, tiered JIT — reasonable. Measured 40–87 KB/frame with almost no collections. **The discipline in the net/sim core is good** (struct snapshots, reused `BitWriter`s, scratch dicts, for-loops). Do **not** chase `new Vector3/Basis/Transform3D/NetEntityState/...` — they are structs, zero GC (a naive allocation scan over-flags them).

**Plan C1 — runtime settings (cheap insurance):**
- At boot: `GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency` (defers blocking Gen2 collections; right mode for a game).
- Add explicit `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>` + `<ServerGarbageCollection>false</ServerGarbageCollection>` to the csproj so the intent is pinned, not default-dependent.

### 3.2 Real churn sites (VERIFIED, ordered by impact)

1. **Effect spawns** — every burst/trail allocates 3–5 Godot objects (§1.1 table). Godot C# objects are not just managed allocs: each is a **native handle + finalizer-queue pressure**, and `QueueFree` churn (`EffectSystem.ScheduleFree → QueueFree`, `EffectSystem.cs:1260-1275`) adds tree add/remove cost. Dominant churn in firefights.
   **Fix:** Plan A1 caching removes the *resource* churn (the expensive part). Then optionally pool the `GpuParticles3D` **nodes** per class (reset via `Restart()`/`Emitting=true`, hide on finish instead of free). Pooling is Tier-2: do caching first, measure, pool if node churn still shows.
2. **`CsqcModelEffects` mesh walks** — `ResetRenderFlags` runs 4 recursive `Meshes()` traversals per *effect-bearing* entity per frame; each node visited calls `GetChildren()` (allocates a `Godot.Collections.Array` + marshals) plus nested iterator allocs (`CsqcModelEffects.cs:181-216, 273-280`). Correctly **gated** by `effectsActive` (`ClientWorld.cs:937-944`), so it only costs when EF_* bits are live (powerup glows, flame, ghosts) — but those are exactly the busy moments.
   **Fix:** cache the `List<MeshInstance3D>` on `CsqcModelEffects.State` when the model node is (re)built; invalidate on model swap. Removes all per-frame `GetChildren()` marshaling.
3. **HUD redraw + string churn** — `HudManager.cs:168-169` calls `QueueRedraw()` every frame for every `IsDynamic` panel (default **true**, `HudPanel.cs:89`); each redraw re-records the canvas item and re-formats strings: `FpsPanel.cs:109-112`, `PingPanel.cs:68`, `TimerPanel.cs:138`, `RaceTimerPanel.cs:84-121`. (Scoreboard is correctly change-gated, `IsDynamic => false`.)
   **Fix:** cache the formatted string + redraw only when the *displayed* value changes (fps text changes ~1×/s; timer 1×/s; ping on update). Make `IsDynamic` panels store a small "last drawn state" struct. Cuts both allocs and canvas re-record cost.
4. **`ServerNet.BuildEntitySet` projectile key** — `$"{e.ClassName} {e.NetName}"` per model-less projectile per tick-frame (`game/net/ServerNet.cs:1218-1220`).
   **Fix:** compute once at projectile spawn and stash on the entity (or memoize per `(ClassName, NetName)` in a small dict).
5. **`BuildScoreboard`** rebuilds `_scoreRows` + per-player columns every tick-frame (known). Gating on a score-version risks missing name/team edits — if touched, version-stamp *all* inputs. Modest cost; fine to defer.
6. **`FindInRadius`** allocates an iterator per call and linearly scans all entities (`src/XonoticGodot.Engine/Simulation/EngineServices.cs:186-202`); called per splash-damage event. Folds into the §4 areagrid work (add a list-filling overload to also kill the enumerator alloc).

### 3.3 Per-frame interop cost (not GC, still CPU)

- **Skeletal pose push:** 3 interop calls per bone (`SetBonePosePosition/Rotation/Scale`) × ~50–60 bones × players × frame (`game/client/PlayerModel.cs:107-123`). No bulk API in Godot 4.6.
  **Mitigations:** skip `SetBonePoseScale` when scale is unit (saves 1/3); skip pose push entirely when the model is off-screen (`VisibleOnScreenNotifier3D`) or refresh distant players at half rate. Engine-side, a batched pose API would be the real fix.
- **MD3 morph re-upload:** animated morphs still `ClearSurfaces + AddSurfaceFromArrays` (full vertex+normal upload) per surface per frame (`game/client/ModelAnimator.cs:378-427`); the landed skip-guard removes the static-model cost.
  **Optional big win (Tier-3):** GPU morphing — bake MD3 frames into a float texture (or two mesh streams + a custom shader lerping by uniform), so per-frame work becomes "set 2 uniforms". Eliminates the re-upload entirely; medium effort, isolated to ModelAnimator.

---

### 3.4 Godot interop allocations — the hidden GC class (godot#105750)

[godotengine/godot#105750](https://github.com/godotengine/godot/issues/105750) documents a class of GC stutter a `new List<>`/`$"..."` grep **cannot** find: **Godot's C# marshaling allocates on the managed heap for many ordinary API calls**, independent of your own `new`s. The reported offenders (Godot 4.3–4.4, Forward+/Vulkan):
- `Input.IsActionPressed("jump")` / any string-keyed input API → mints a `StringName` **every call**.
- `GetTree().GetNodesInGroup(name)` / `CallGroup` → returns a heap `Godot.Collections.Array`.
- `GetNode("path")` / string `NodePath` args → `NodePath` alloc per call.
- Any `Set*ShaderParameter("name", …)` / `GlobalShaderParameterSet("name", …)` → implicit `string`→`StringName` alloc per call (the API parameter is `StringName`, so even a `const string` converts).
- Godot `PhysicsServer` raycasts marshal per call.
- The reporter's key insight: **the cost scales with framerate** — a single alloc/frame is nothing at 60 fps and a GC treadmill at 600+ fps (hence the §2.1 fps-cap cross-link). The brute-force workaround in the thread (`GC.Collect()` every frame) is a smell; the principled version is `GCLatencyMode.SustainedLowLatency` (Plan C1) + just not allocating.

**I audited this codebase against every offender above — it is, somewhat remarkably, already clean in the hot paths, by construction rather than by StringName-caching:**
- **No string-based input.** Gameplay input reads the port's own `BindTable`, not Godot's string `InputMap`. The only hot `Godot.Input` call is `HudManager.cs:233` `Input.IsKeyPressed(Key.Up)` — takes a **`Key` enum, no `StringName`**.
- **No `GetNode(string)` / `GetNodesInGroup` / `CallGroup` anywhere in `game/`** (verified — zero matches). Entities are held in typed dictionaries, not the scene-group system.
- **Animated world textures are GPU-driven.** `ShaderCompiler` bakes `TIME` into the generated GLSL (`ShaderCompiler.cs:477/488/515`) for scroll/rotate/turb, so animated stages cost **zero per-frame interop** — the GPU's built-in `TIME` drives them.
- **All `Set*ShaderParameter` calls are build-time or change-gated**, not per-frame (verified: the only `_Process`-bearing file with shader-param calls, `VignetteOverlay.cs:97-101`, gates `Apply` behind a `Sig.Equals(_last)` check; `WorldTint.cs:227-237` guards each `GlobalShaderParameterSet` on value-change; `ModelTint` runs on appearance change, not per frame).
- **Traces don't use Godot physics.** `TraceService` is pure C# (§5), so the per-raycast marshaling alloc the issue cites simply doesn't exist here — an underrated advantage of the custom collision system.

**Residual exposure (LOW — on-event, not per-frame):** the shader-uniform name constants are `const string` (`PlayerSkinShader.ColormodUniform = "colormod"`, `WorldTint.MapTintUniform = "map_tint"`, etc.) passed to `StringName` parameters → one `StringName` alloc per call, but only at material build / appearance-change frequency.

**Plan C2 — harden the convention before someone adds a per-frame call (trivial, zero-risk):**
- Convert the uniform-name constants from `const string` → `static readonly StringName` (in `PlayerSkinShader`, `LightmapShader`, `WorldTint`, `HeroMaterials`, `ModelTint`, `VignetteOverlay`). Eliminates even the on-event allocs and means a future per-frame `SetShaderParameter` is automatically alloc-free.
- Standing rule (worth a comment in the shader-uniform files): **never pass a string literal to a Godot API typed `StringName`/`NodePath` from a per-frame path; cache it as `static readonly StringName`.** Same for avoiding `GetNodesInGroup`/`GetNode(string)`/string input APIs in `_Process`.
- This class is invisible to source greps — the only reliable detector is `dotnet-counters monitor … System.Runtime` alloc-rate while playing (already the §7 recommendation; godot#105750 confirms it's the right tool). Re-measure after any new rendering/HUD feature.

**Plan C3 — enforce the convention so new code can't regress it (compile-time, not a test):**

*Why not a unit test:* the test project (`tests/XonoticGodot.Tests`) references the five `src/` libraries but **not the Godot host project**, so a test literally cannot see `game/` — where 100% of the Godot interop lives — and can't reflection-load the host DLL without the full Godot runtime. A test is a dead end for this rule.

*The right tool is a Roslyn `DiagnosticAnalyzer`*, which runs during the **host's own compile**: it sees `game/` natively, surfaces as a build warning/error **and** an IDE squiggle at the exact call site, and is suppressible per-line when genuinely intended. Home it in the existing `src/XonoticGodot.SourceGen` analyzer assembly (netstandard2.0 + Roslyn) — it's already consumed by the host build, so no new wiring — and unit-test it in-memory the way `tests/.../SourceGenTests.cs` tests the generator (the test project already references `Microsoft.CodeAnalysis.CSharp 4.8.0`). Split the rule into two tiers by false-positive profile:

- **Tier 1 — blanket ban on the string-keyed APIs with zero legitimate uses here** (verified: **zero current matches** in `game/`, so a hard ban has zero false positives and zero churn): `Input.IsActionPressed`/`IsActionJustPressed`/`IsActionJustReleased`/`GetActionStrength`/`GetActionRawStrength` (string input — the port uses its own `BindTable`), `GetNodesInGroup`/`CallGroup`/`AddToGroup` (string group system), `GetNode(string)`/`GetNodeOrNull(string)` (typed dicts instead). Pure regression protection that locks in a property currently true only by habit. **~1 hour incl. unit test.**
- **Tier 2 — flag implicit `string`-literal→`StringName`/`NodePath` conversions (and string-keyed Godot calls) ONLY inside `_Process`/`_PhysicsProcess`/`_Draw` method bodies.** That syntactic scope encodes "not *per-frame*" precisely and has near-zero false positives, because build-time material setup (the 40+ legit `SetShaderParameter("literal", …)` sites) is never in those methods. Cheap to write — no interprocedural reachability, just "is this node inside a method declared `_Process`/…". **Land the C2 `StringName` conversion alongside it** so the rule is satisfiable without friction (a uniform name needed in a hot path is already a cached `StringName`, nothing to suppress). **~half a day incl. tests.**

*Honest limitation:* neither tier catches a bad call buried in a helper *invoked from* `_Process` (static reachability isn't worth it). That gap is covered by the runtime backstop already in §7 (`dotnet-counters` alloc-rate + the FrameProfiler GC segment), which surfaces the alloc regardless of source pattern. Treat the analyzer as **defense-in-depth that fails the build on the obvious mistakes**, not a proof of zero per-frame allocation.

*Low-tech alternative (if not authoring an analyzer):* an MSBuild pre-build `<Target>` that greps `game/**/*.cs` and fails on the Tier-1 API names — ~15 minutes, but worse UX (no IDE squiggle, no per-line suppression). Adequate for Tier 1; can't reasonably express Tier 2's scoping.

---

## 4. Class 4 — Simulation scaling (will bite with players/bots/projectile spam)

The sim core is faithful and clean but has **no spatial acceleration structure** — DarkPlaces has the areagrid; the port currently scans linearly:

| Site | Pattern | Cost shape |
|------|---------|-----------|
| `src/XonoticGodot.Engine/Collision/TraceService.cs:160-165` (`ClipToEntities`) | every trace tests **every** solid entity (`SolidEntities` cached list ✅, but O(n) per trace) | players × (5–8 traces/tick) × solids + projectiles × solids |
| `src/XonoticGodot.Engine/Simulation/TriggerTouch.cs:37-50` | after each client move, scan **all** entities for trigger overlap | clients × entities / tick |
| `EngineServices.FindInRadius:186-202` | linear scan per damage event | events × entities |
| `SimulationLoop.Tick:178-190` (`sim.integrate`) | every non-client entity integrates each tick; movers trace (O(solids) each) | projectiles × solids |

At today's test scale (1 player, few entities) this measures ~1 ms/tick total — invisible. At 8–16 players + bots + rocket spam it's the main fps-floor risk: order of 10⁴–10⁵ entity tests per tick.

**Plan D1 — port the DP areagrid (the one algorithmic fix that pays everywhere):**
- A uniform XY grid (DP uses 32×32 cells over the world bounds) holding entity links, updated in `LinkEdict` (the single choke-point already exists: `EntityService.LinkEdict`).
- `ClipToEntities`, `TriggerTouch.Run`, and `FindInRadius` query cells overlapping the swept AABB instead of the global list.
- This is *faithful* (DP does exactly this — `SV_AreaGrid` semantics), so no gameplay-parity risk, and it's the precondition that makes 16-player matches viable. Medium effort (~1–2 days incl. tests); fully testable headless (golden-trace suite + a new grid-vs-linear differential test: same results, fewer candidates).

**Plan D2 — bot AI budget (currently moot, future-proof):** bot A*/goal-rating runs unbudgeted inside `sim.start`. Note: per the nav-perf investigation, **`BotBrain.Think` has no caller in the live `--host` loop yet** — so this costs nothing today. When bots get wired: rotate one "strategy token" so only one bot replans per tick (the QC games do exactly this), and reuse the pooled A* (already landed).

**Plan D3 — do NOT time-slice projectile integration.** It was considered and rejected: staggering projectile movement diverges from DP tick semantics (parity risk for a port whose value is faithfulness). The areagrid removes the need.

---

## 5. Parallelization & mitigation strategies

### What is safe to move off the main thread (verified boundaries)

- **Pure C#, no Godot:** all of `src/` — BSP/MD3/IQM/DPM parsers, TGA/DDS decoders, collision/brush building, bezier patch tessellation, movement traces (`TraceService` is pure math — *no* Godot PhysicsServer involved), VFS reads/zip decompression, effectinfo/text parsing. All `Task.Run`/`Parallel.For`-able.
- **Thread-tolerant Godot:** creating/manipulating `Image` instances (decode into an `Image` off-thread is what Godot's own threaded loader does).
- **Main thread (or `CallDeferred`):** scene-tree ops (`AddChild`/`QueueFree`), and conservatively all RenderingServer-backed resource finalization (`ImageTexture.CreateFromImage`, `ArrayMesh.AddSurfaceFromArrays`, material creation). Godot's servers are technically thread-safe via command queues, but keeping GPU-resource creation on the main thread, **frame-budgeted**, is the robust pattern.

### S1 — Background streaming pipeline (kills remaining cold-load hitches)
```
request(name) ──► Task.Run: VFS read + parse (+ Image decode)   [worker]
              ──► CallDeferred: build mesh/material/texture      [main, ≤2ms/frame budget]
              ──► attach node / swap placeholder                 [main]
```
- One shared `AssetStreamer` with a priority queue (viewmodel swap = high, far player model = low) and a per-frame millisecond budget (Stopwatch-guarded drain in `_Process`).
- First-use callers get a cheap placeholder for 1–3 frames instead of a 100 ms stall (weapon viewmodel already has a placeholder path: `ViewEntityRenderer.cs:176`).

### S2 — Parallel map load (shrinks load time; frees budget for more precaching)
Inside `MapLoader.BuildMap` + `NetGame` startup, fan out the pure-C# stages with `Parallel.For`/`Task.WhenAll`:
lightmap `lm_NNNN.jpg` decode (Image per worker), texture decode, bezier patch tessellation, `BspCollisionBuilder` brush builds — then do the Godot mesh/material/texture creation on the main thread in batches between loading-screen pumps (the `await YieldForLoadingFrame()` scaffolding already exists in `NetGame._Ready`). Typical 4–8× on the decode-heavy stages.

### S3 — Idle-time full warmup
After the loading screen drops, keep a low-priority budget (~1–2 ms/frame) warming *everything not yet touched* (remaining weapon skins, announcer lines, gib variants). Within the first minute of play the entire asset set is hot regardless of precache lists.

### S4 — Areagrid (Plan D1) — algorithmic, not threaded, but it's the scaling fix that makes everything else irrelevant at high entity counts.

### S5 — Server sim on a worker thread (listen server) — **Phase 3, design carefully**
Today `NetGame._Process` runs client prediction, rendering feeds, *and* `ServerNet.Tick` (sim + broadcast) on the main thread. Moving the server side (`SimulationLoop` + `GameWorld` + `ServerNet`) to a dedicated thread would make server cost invisible to render. **Blockers found:** the ambient `Api.Services` global is shared by client-side prediction (`TriggerTouch.RunAmbient`, predicted jumppads/teleports) and the server sim — they'd race; the shared↔server cvar bridge and `SoundService.Broadcast` cross the boundary too. Prerequisites: split the ambient facade per-world, make the loopback transport the *only* contact surface, hand snapshots over via a lock-free queue. High effort/high risk — only worth it if profiling shows server tick > ~2 ms in real matches *after* the areagrid. The transport already treats the local client as a normal peer, which is the right foundation.

### S6 — Parallel trace batches inside a tick (experimental, off by default)
Traces are pure C# and read-only over a stable entity set; projectile sweeps could run `Parallel.For` in a gather phase with touch *responses* applied serially afterward in entity order. Faithfulness caveat: DP resolves serially, so ordering effects (two projectiles hitting the same mover) could differ subtly — gate behind a cvar, default off, and only revisit if areagrid + real-match profiling still shows `sim.integrate` dominating.

### S7 — Render-thread experiment
`project.godot` sets no `rendering/driver/threads/thread_model`. Try `thread_model=2` (separate render thread) in a test build: moves RenderingServer submission off the main thread (helps `proc`-bound frames slightly). Historically occasional issues with viewport-heavy code — verify menus/HUD/SubViewport warm pass under it.

### S8 — Pipeline/shader cache hygiene
Godot caches compiled shaders/pipelines on disk per driver. Don't disable it; note that a driver update invalidates it (one cold run). The §1.1 warm pass makes even that first run clean.

---

## 6. Prioritized action plan

| Pri | Item | Section | Effort | Felt impact | Risk |
|-----|------|---------|--------|-------------|------|
| **P0** | Cache effect/projectile runtime materials + meshes | A1/3.2-1 | S–M | High (firefight hitches + churn) | Low (proven pattern: `_trailResCache`) |
| **P0** | Offscreen GPU warm pass at map load | A2 | S | High (first-use hitches) | Low (offscreen; verify no flash) |
| **P0** | Precache all weapons + combat sounds + player models | A3 | S | High | Low |
| **P0** | Mailbox vsync option + maxfps guidance + release-build test | B1 | S | High (the measured judder) | Low |
| **P1** | VFS negative-lookup cache | A4 | S | Med | Low |
| **P1** | HUD string/redraw change-gating | 3.2-3 | S | Med | Low |
| **P1** | `CsqcModelEffects` mesh-list cache | 3.2-2 | S | Med | Low |
| **P1** | `ServerNet` projectile-key string cache | 3.2-4 | S | Low-Med | Low |
| **P1** | GC: SustainedLowLatency + explicit csproj GC config | C1 | S | Insurance | Low |
| **P3** | Uniform names `const string`→`static readonly StringName` (godot#105750 hardening) | C2 | S | Insurance | Low |
| **P2** | Tier-1 analyzer: ban never-used string APIs (`Input.IsAction*` / `GetNodesInGroup` / `CallGroup` / `GetNode(string)`) | C3 | S | Regression guard (0 false pos) | Low |
| **P3** | Tier-2 analyzer: string-literal→`StringName`/`NodePath` inside `_Process`/`_PhysicsProcess`/`_Draw` (+ land C2) | C3 | M | Regression guard | Low |
| **P1** | Catch-up clamp (4 ticks/frame on client-hosted path) | B3 | S | Med (post-hitch recovery) | Low-Med (verify headless) |
| **P2** | **Areagrid** for traces/triggers/radius | D1 | M | High at scale | Med (differential-test it) |
| **P2** | Background asset streamer (Task.Run parse + budgeted build) | S1 | M | High (long tail of cold loads) | Med |
| **P2** | Parallel map load | S2 | M | Med (load time) | Low-Med |
| **P2** | Idle-time full warmup queue | S3 | S | Med | Low |
| **P2** | `cl_movement_perframe` default-on (after feel test) | B2 | S | Med (input feel @144Hz+) | Med (parity feel) |
| **P3** | GpuParticles3D node pooling | 3.2-1 | M | Med | Med (reset semantics) |
| **P3** | GPU vertex-shader MD3 morphing | 3.3 | M–L | Med (many animated models) | Med |
| **P3** | ReadyToRun release publish | A5 | S | Small | Low |
| **P3** | Render thread_model=2 experiment | S7 | S | Small-Med | Med |
| **P3** | Server-sim worker thread | S5 | L | High at scale | High |

**Suggested batches:** ship the four P0s together (they attack the two *felt* problems: first-use hitches and pacing), measure a windowed run, then do P1 as one cleanup pass, then start the areagrid.

---

## 7. Verification protocol (use what's already built)

1. `cl_frameprofiler 1` (graph + hitch log) / `2` (per-second breakdown). Read hitch lines as: big `gpu` → pipeline compile or GPU load; big `rest` → pacing/present (class 2); big `proc` with small scopes → unscoped `_Process` work; `GC gN` segment → allocation spike; `ticks ≥ 2` → the frame is *amortizing a prior stall*, look earlier.
2. `dotnet-counters monitor --process-id <pid> System.Runtime` — alloc-rate + pause-time ground truth while playing.
3. A/B each fix in a windowed run with a fixed scenario (same map, scripted fire of every weapon, `--host`): compare median frame, P99 frame, hitch count from the log.
4. For the areagrid: extend the golden-trace movement suite with a grid-vs-linear differential test (identical trace results on recorded scenarios).
5. Always confirm the final feel in an **exported release build** — editor/IDE runs misrepresent both pacing and C# perf (Debug assembly always loads there; verified).

---

## 8. Implementation status (2026-06-10)

All 23 prioritized items were worked through in priority order. Build green; **1495 tests pass** (incl. new
areagrid differential + analyzer suites); stormkeep windowed screenshots verify rendering parity at each phase.

**Landed + verified:**
- **P0** — A1 effect/projectile material+mesh caches (`EffectSystem._heurMeshCache`/`_infoMeshCache`,
  `ProjectileRenderer._bodyResCache`); A2 offscreen GPU warm pass (`game/client/GpuWarmPass.cs`); A3 precache-all
  (`cl_precache_all_weapons` default 1 → 24 weapons + 36 combat sounds + local/default player models);
  B1 mailbox vsync (`vid_vsync` 0/1/**2**/3 in `ClientSettings.ApplyVideo` + the video dialog slider).
- **P1** — A4 VFS `Exists`/`ResolveImage` pos+neg caches; 3.2-2 `CsqcModelEffects` cached mesh list; 3.2-3 HUD
  `NeedsRedraw()` change-gating (Fps/Ping/Timer/RaceTimer); 3.2-4 `ServerNet._projKeyCache`; C1 `GCLatencyMode`
  + csproj GC pin; B3 `SimulationLoop.MaxTicksPerFrame` soft-cap (interactive=4, drains backlog).
- **P2** — **D1 entity area-grid** (`EntityAreaGrid.cs` → traces/PointContents/FindInRadius/TouchAreaGrid;
  differential test + 1495-test parity); S1 `BackgroundAssetStreamer` + AssetLoader parse/build split (idle
  player-model parse off-thread); S2 parallel bezier tessellation in `MapLoader.BuildMap`; S3 `IdleWarmer`
  (long-tail sound/model warm); C3-T1 `StringApiAnalyzer` (**XG0001**, build error, 9 tests).
- **P3** — C2 uniform names `const string`→`static readonly StringName` (LightmapShader/PlayerSkinShader/WorldTint
  + VignetteOverlay) + standing-rule comment; C3-T2 `HotPathStringNameAnalyzer` (**XG0002**, 6 tests — caught +
  fixed a real per-frame `SetMeta("cmd",…)` alloc in PauseMenu); A5 `PublishReadyToRun` (gated to ExportRelease);
  3.3 skip unit-scale `SetBonePoseScale` interop in `PlayerModel.PushBones`.

**Evaluated → no change warranted (the report's own gating / engine reality):**
- **B2** `cl_movement_perframe` default-on — kept default 0: that's the parity-faithful default (stock DP = fixed
  72 Hz input); flipping changes movement *feel* and needs the windowed feel-test the report gates it on (a user
  step). Feature already cvar-exposed.
- **S7** render `thread_model=2` — ~~"not applicable to Godot 4.6 … removed in Godot 4 … the key is inert
  (ProjectSettings stores it unread); tested → reverted"~~ **THIS WAS WRONG IN EVERY CLAUSE, and it cost the
  single biggest measured win of the whole campaign. Corrected 2026-08-03, verified against the PINNED
  4.6.3 source, not documentation:**
  - `main/main.cpp:2722` — `separate_thread_render = (int)GLOBAL_DEF("rendering/driver/threads/thread_model",
    OS::RENDER_THREAD_SAFE) == OS::RENDER_SEPARATE_THREAD;` — the key **is** read.
  - `core/os/os.h:93-95` — `RENDER_THREAD_UNSAFE=0, RENDER_THREAD_SAFE=1, RENDER_SEPARATE_THREAD=2`, unchanged.
  - `rendering_server_default.cpp draw()` — `if (create_thread) command_queue.push(_draw) else _draw(...)`.
    At the DEFAULT (Safe) the whole render pass runs **INLINE ON THE MAIN THREAD** inside `Main::iteration`.
    Godot 4 does *not* "already run the RenderingServer on a separate thread"; that is the Separate mode.

  **Measured after enabling** (release export, catharsis demo 90 s): p50 **4.21 → 3.72 ms**, i.e.
  **237 → 269 fps**, with `rest` **1.76 → 0.78 ms** and `proc` *rising* 1.70 → 2.06 (RenderingServer calls
  from main now marshal through `CommandQueueMT`). Landed in `project.godot` 2026-08-03; still experimental
  upstream (resize/particle crashes, `CommandQueueMT` contention vs background loading — godot#112452), so
  it needs a soak + resize/alt-tab pass before release.

  **Two traps that made the original test read "inert", both worth remembering:**
  1. Inside the `[rendering]` INI section the key is **section-relative** — `driver/threads/thread_model=2`.
     Writing the full `rendering/driver/...` path there silently resolves to `rendering/rendering/...` and
     does nothing.
  2. The activation `WARN_PRINT` (`main.cpp:3417`) goes to **stderr**, which `perf-run.ps1` does not capture.
     Its absence from stdout is NOT evidence the mode is off. Capture stderr to confirm.
  Also note `main.cpp:2725` force-disables it in the editor/project-manager, so it can only be evaluated on
  an exported build.

**Deferred (conditional/high-risk per the report — gated on a manual measurement this automated pass can't run):**
- **3.2-1** GpuParticles3D node pooling — §3.2-1 explicitly says "do A1 caching first, **measure**, pool *if node
  churn still shows*". A1 removed the expensive resource churn; the residual *node* pooling carries Med
  reset-semantics risk and its gate is a `dotnet-counters` per-frame alloc-rate read during a firefight (a manual
  windowed step). Pool the heuristic single-node bursts first if that measurement justifies it.
- **3.3 Tier-3** GPU vertex-shader MD3 morphing + off-screen pose-push skip — the §3.3 "optional big win (Tier-3)"
  shader rewrite of `ModelAnimator` (Med risk) and a `VisibleOnScreenNotifier3D`-gated pose skip. The low-risk
  skip-unit-scale win landed; the rewrite awaits a profile showing morph re-upload dominating.
- **S5** server-sim worker thread — §5 S5 itself says "only worth it **if profiling shows server tick > ~2 ms in
  real matches *after* the areagrid**" (now landed), and lists concrete race blockers (the ambient `Api.Services`
  global, the shared↔server cvar bridge, `SoundService.Broadcast`). High effort / High risk with no profiling
  justification yet — revisit only if a real-match server-tick profile demands it.

**Manual steps left to the user (the report's §7 protocol):** the windowed FrameProfiler A/B (scripted weapon
fire, median/P99/hitch deltas) and the `dotnet-counters` alloc-rate read in an **exported release build** — these
are the gates for the three deferred items above and the final feel-confirmation for B1/B2.

---

## 9. Bot-load hitch investigation + Wave 0 ground truth (2026-06-11)

After P0–P3 landed, the user still reported hitching **"especially with bots in the map,"** and observed that
**GameDemo does not hitch the way NetGame does**. This section documents the dedicated investigation, what it
disproved (including §2.1's vsync hypothesis), and what it found to be the real cause.

### 9.1 The harness (reusable — this is the deliverable for iterating on bot-load perf)
- **`tests/XonoticGodot.Tests/Perf/BotTickPerfBench.cs`** — boots a real `GameWorld` on a map, fills *N* bots
  (it sets `bot_join_empty 1` + `bot_number N` and runs frames past the 2.5 s sentinel — calling `Bots.AddBot`
  directly gets **trimmed** on a human-less server), lets them settle, then times `GameWorld.Frame` over many
  ticks. It reports **median / P99 / MAX** tick (a hitch *is* the worst tick, not the mean), **per-scope time and
  per-scope allocation**, and GC gen0/1/2 counts. Env-parameterised: `XG_BOTS` (default 6), `XG_MAP` (stormkeep),
  `XG_TICKS`. Run: `XG_BOTS=12 dotnet test … --filter BotTickPerfBench -l "console;verbosity=detailed"`.
- **`Prof` per-scope allocation tracking** — `ScopeToken` now diffs `GC.GetAllocatedBytesForCurrentThread()`, and
  `SnapshotAndReset(scopes, counters, allocInto = null)` takes an optional 3rd arg to drain the byte buckets. Every
  `Prof.Sample("x")` now reports **both ms and bytes** (FrameProfiler's existing 2-arg call is unaffected, and the
  scopes are free when the profiler is off). New `bot.think` / `bot.path` scopes added in `BotBrain`.
- **Diagnostics tooling added** (all reusable): repeatable **`--cvar NAME VALUE`** boot flag
  (`Shell.ApplyCvarOverrides`, applied before `ClientSettings.ApplyAll`); a FrameProfiler **file sink** at
  `user://frameprofile.log` (superseded 2026-06-14 by the per-launch `~/XonData/logs/session-*.log` + `.csv`
  background writer — see ../docs/RUNNING.md) plus `rest` in the mode-2 line (windowed/exported runs detach stdout — read the file);
  a **`[video]` requested-vs-actual vsync log** in `ApplyVideo`; and a `cl_idle_warmup 0` A/B gate on the idle
  warm. **Bug fixed along the way:** `FrameProfiler.Mode()`/`HitchFloorMs()` read `Api.Cvars` (the listen-server's
  *private* store), but `cl_frameprofiler` is registered/set in `MenuState.Cvars` (the *shared* client store) — so
  `set cl_frameprofiler 2` never took effect mid-`--host` match. Now reads `MenuState.Cvars`.

### 9.2 Diagnosis — why NetGame and not GameDemo
The **server tick** is the cost NetGame pays and GameDemo never does (GameDemo = one local player; no server, no
net, no bots). In the bench, `sim.move` (per-client movement physics) dominates the worst tick and scales linearly
with bot count: 6 bots → MAX ~5.9 ms, 12 bots → MAX ~11 ms — of which only ~0.9 ms is `bot.think` AI and ~0.3 ms
`bot.path` A*; **the rest is movement physics × bots.** (Note: bot AI **is** wired into the live loop now via
`BotController.Tick → BotBrain.Think` / `BotPopulation.ServerFrame` — correcting the older "BotBrain.Think has no
caller" note.) The worst tick still fits the 72 Hz **sim** budget (13.9 ms), but a single ~6 ms server tick landing
in the same frame as rendering 6 players overruns a 144/160 Hz **render-frame** budget (6.25–6.9 ms) → the hitch.

**Allocation fixes landed** (the per-scope alloc readout pinned `Brush.FromBox` — a `Brush` + `Vector3[8]` +
`BrushPlane[6]` ≈ 360 B allocated **per trace** — as the dominant `sim.move` churn):
- `TraceService._boxCache` (`Dictionary<(mins,maxs),Brush>`): the moving trace box is the mover's hull, constant
  across a slide-move's many traces → cache per hull. **−30 %** alloc (92.5 → 64 KB/tick @ 12 bots; gen0 16 → 11).
- `Brush.RefillBox` (in-place, zero-alloc) + a pooled `TraceService._entBrush`: the per-candidate entity-AABB box
  in `ClipToEntities` is refilled into one pooled brush (used immediately, never retained). Helps the clustered
  worst tick most. **Cumulative 92.5 → 61.7 KB/tick (−33 %); gen0 16 → 10.** 1496 tests pass; headless `--host` clean.

### 9.3 Wave 0 — release baseline + vsync A/B (DECISIVE)
Run in an **exported release build** (`run-release.sh` → `dist/windows-client/XonoticGodot.exe`). Three findings,
in order of impact:

1. **Most of the felt hitching is the Debug build, not a bug.** Debug (editor/console) steady median degrades to
   ~11.7 ms (85 fps), `proc` ~6 ms. **Release holds ~7.6 ms (130 fps), `proc` ~3 ms — halved** — with 6 bots. The
   headless *sim* cost is Debug ≈ Release, but the **client** (prediction/render) code roughly halves in Release.
   → **The single biggest win for felt smoothness is to play in a release build.**
2. **`rest` is NOT the vsync beat** (this corrects §2.1). With `vid_vsync 0` (confirmed actually applied via the
   new `[video]` log), `rest` does **not** go away; in Release steady-state `rest` ~3.5 ms is just normal GPU +
   present. The large `rest` in the original Debug/windowed runs was Debug-`proc` spillover + windowed-DWM pacing.
   **Mailbox/B1 is not the lever** — keep it as a user option, but stop attributing the bot hitch to pacing.
3. **The real release hitch is a GC stall: a ~154 MB allocation burst right after self-connect → a 100–154 ms
   all-thread gen2 freeze (a few times early in a match), which PERSISTS in release and is NetGame-only.** Root
   cause: the client builds **all 6 bot player models at once on the first snapshot** (~25 MB per skeletal model —
   IQM + skin/normal/gloss — × 6 ≈ 154 MB) → a full gen2 collection that suspends every thread. It is **not** the
   idle warm (it persists with `cl_idle_warmup 0`). GameDemo builds **one** model, so it never triggers this — which
   is exactly the user's "GameDemo doesn't hitch" observation.

### 9.4 Reprioritized wave plan (supersedes the vsync-centric framing of §2)
- **Wave 1 — ✅ LANDED (2026-06-11, see §10 for the full write-up):** **stagger live player-model builds** +
  **finish the `sim.move`/`sim.integrate` allocation hunt.** The player-model path now returns a placeholder
  shell and streams the build one-per-frame through the S1 `BackgroundAssetStreamer` (kills the 154 MB gen2
  burst); the alloc hunt landed seven fixes for **60.9 → 2.9 KB/tick at 12 bots** (the report's named suspects
  — `IMovementInput` boxing, per-tick concats — were real and are fixed). 1496 tests pass; verified live.
- **Wave 2+ (structural, only if a real-match profile demands it):** **S5 — move the server sim onto a worker
  thread**, so the per-tick movement-physics cost stops stealing the render-frame budget. This is the real
  architectural answer to "GameDemo vs NetGame," but it is High effort / High risk (the ambient `Api.Services`
  global, the shared↔server cvar bridge, and `SoundService.Broadcast` are the concrete race blockers named in §5),
  and §5's own gate — server tick > ~2 ms in real matches *after* the areagrid — must be confirmed first.
- **Out:** the vsync/`rest` track (§2.1) as a fix for the bot hitch — Wave 0 disproved it.

**Bottom line for the user:** play in a **release** build (halves the felt cost and is free); the remaining true
hitch is the 154 MB model-build stall, which Wave 1 targets directly. Vsync/mailbox is a comfort option, not the cure.

---

## 10. Wave 1 — implemented (2026-06-11)

Both §9.4 Wave-1 items landed. Build green; **1496 tests pass**; verified live in a windowed `--host stormkeep
--bots 6` run (screenshots + the `[stream]` log).

### 10.1 Staggered live player-model builds (the 154 MB gen2 stall)

`NetGame.ResolvePlayerModel` no longer parses+builds synchronously on first sight. It returns a
`PlayerModel` **shell with a shared placeholder box** immediately and routes the load through the
(`SetupRender`-owned, unconditional) `BackgroundAssetStreamer`: the IQM+sidecar parse runs on the thread pool
(`AssetLoader.ParseSkeletalModel`), the Godot build (`BuildSkeletalModel` + `PlayerModel.Setup`) lands on the
main thread under the streamer's 2 ms budget at **High** priority — so N bots arriving in one snapshot build
**one model per frame** instead of all in that frame. Verified: 6 bots → 6 `[stream] player model … built …
placeholder swapped` lines (a `developer 1` Log.Trace), real posed/tinted models in the capture.

Pieces (all in place for reuse):
- `PlayerModel.ShowPlaceholder()/ClearPlaceholder()` — shared mesh+material (no per-shell pipeline compile);
  `Setup()` clears it. The placeholder is also **load-bearing for cache invalidation**: freeing it is what
  makes `CsqcModelEffects`' cached mesh list stale so the late-added real meshes get collected.
  `Setup()` also silences the `AnimationPlayer` BEFORE tree entry now (autoplay-after-entry is a no-op + warning).
- `ClientWorld.RebuildEntityModel(entity)` — tears down the model children (nameplate survives), releases the
  csqc effect state like `OnEntityRemove`, re-runs `TryAttachModel`. Used when an async resolve settles
  "not skeletal" (memoized in `NetGame._nonSkeletalPlayerModels` → the resolver returns null → the old MD3/
  static fall-through), or the entity's model changed mid-flight.
- `ClientWorld.SeedAppearance(entity)` — immediate colormap tint at delivery (the per-frame pass keeps it fresh).
- Delivery validates staleness (`pm` freed/queued, entity freed, model/skin changed) before touching the tree.
- A failed parse is BOXED (`SkeletalParseBox`) because the streamer drops null off-thread results silently —
  a miss must still deliver to trigger the fall-back attach.

Residual (accepted): `BuildForcedPlayerModel` (cl_forceplayermodels) still loads synchronously — explicit user
opt-in, and the forced model is typically the precached local model. `ModelTint.ApplyAppearance` still walks
meshes per frame per player (client-side; pre-existing).

### 10.2 sim.move / sim.integrate allocation hunt — **60.9 → 2.9 KB/tick (−95%)** @ 12 bots

Method: `Prof` per-scope alloc attribution via `BotTickPerfBench` (`XG_BOTS=12`), with new permanent sub-scopes
`move.pre/in/pm/post/link/touch` (GameWorld/SimulationLoop) and `start.warmup/bots/vote/defer/hooks`. Fixes, in
found order (each confirmed by a bench re-run):

| Fix | Scope | B/tick before → after |
|-----|-------|----------------------|
| `SimulationLoop`: cached `_runThink` delegate (the `RunThink` method-group arg allocated a `Func` **per entity per tick**) | sim.integrate | 19,475 → 185 |
| `MovementParameters.FromCvars`: per-(prefix,name) cvar-name cache — ~45 `prefix + "name"` concats ran **per player move** (server ticks × clients + every prediction replay) | move.pm | 31,366 → 35 |
| `ItemstimeMutator.Recompute` (runs every server frame): persistent accumulator + static `Note` (no closure) + prebuilt classname→key map (incl. `weapon_<superweapon>` names) + **single pass over `IEntityService.All`** (new default-interface member; `EntityService` already had it, `ServerEntityService` delegates; FindByClass fallback for fakes) — was a fresh Dictionary + closure + ~10 `FindByClass` iterators + concats per tick | start.hooks | 3,000 → ~0 |
| `BuffsMutator.Buff()`: memoized `"buff_" + shortName` (g_buffs **-1** ⇒ enabled in stock; its PreThink calls `Active()` ×5 per player per tick) | move.pre | 2,500 → ~0 |
| `BotPopulation.TickInput` now **implements IMovementInput** (returning the struct as the interface boxed per read ×2 readers × bots × ticks); `ZeroInput` boxed once (also in ServerNet) | move.in | −370 |
| `BotBrain`: per-weapon balance-cvar name cache (3 `$"g_balance_{w.NetName}_primary_*"` interps per think) | bot.think | 2,412 → 2,041 |
| `GameWorld.OnStartFrame`: cached `_deferredExec` delegate (the `Commands.Deferred.Pump` lambda captured `this` per tick) | start.defer | small |

Result @ 12 bots / 2160 ticks: **alloc 60,926 → 2,907 B/tick** (125.5 → 6.0 MB per 30 s), **gen0 16 → 1**,
gen2 0. Tick timing unchanged (med ~0.6 ms, MAX ~10 ms — the MAX is `move.post` weapon-fire bursts, real
gameplay events). Remaining buckets: `bot.think` ~2.0 KB/tick (token-gated `FindInRadius`/`FindByClass`
iterator allocs in goal rating — rare-path, diminishing returns), `move.post` ~0.6 KB (combat events).

### 10.3 Verification

- Bench: `XG_BOTS=12 dotnet test tests/XonoticGodot.Tests --filter BotTickPerfBench -l "console;verbosity=detailed"`.
- Live: windowed `--host stormkeep --gametype dm --bots 6 --screenshot …` → models posed/tinted, no streamer
  errors, no autoplay warnings; `--cvar developer 1` shows the six staggered `[stream]` builds.
- **Headless `--host` works** (verified this session: `--headless --host stormkeep --bots 2
  --quit-after-seconds 20` → exit 0, `[MapLoader]` loads stormkeep, waypoints load). An earlier draft of this
  section wrongly called it broken — that was a self-inflicted test artifact: Windows `timeout` does NOT kill
  the Godot child, so an orphaned host kept UDP 26000 and the *next* run failed with "Couldn't create an ENet
  host" / a fatal CLR error. **Use `--quit-after-seconds <s>` (not `timeout`) and kill orphaned Godot processes
  between scripted runs.** The FramePostDraw hang that genuinely broke this path was already fixed in a prior
  session: `Shell.WaitForFramePainted` awaited `frame_post_draw`, which never fires headless (the main loop
  never calls `draw()` with no window), so it now falls back to `process_frame` ticks under the headless
  DisplayServer and `GpuWarmPass` skips its SubViewport there. Guarded by the `ci/ci.sh` headless host smoke.

### 10.4 Remaining / todos

- **Wave 1 acceptance gate (USER step, not automatable here):** confirm the early-match ~154 MB gen2 freeze is
  actually gone *as felt* in an **exported release build** with bots (`run-release.sh`, then `--host … --bots`).
  The bench proves the per-tick allocation collapse and the staggered build is verified in a windowed run, but
  the original symptom was measured in §9.3 on a release build — close the loop there. (§7 protocol.)
- **Residual server-tick alloc (~2 KB/tick, LOW):** `bot.think` token-gated `FindInRadius`/`FindByClass`
  iterator allocs in goal rating, and `move.post` combat-event allocs. Diminishing returns vs. the −95% already
  banked; revisit only if a real-match profile shows GC pressure climbing with more bots.
- **Residual sync model loads (LOW):** `BuildForcedPlayerModel` (`cl_forceplayermodels`) still loads
  synchronously (explicit opt-in, usually the precached local model); `ModelTint.ApplyAppearance` still walks a
  player's meshes each frame (client-side, pre-existing). Stream/cache these only if a forced-model match or a
  high player count shows them in a profile.
- **Wave 2+ (structural, gated):** **S5 — server sim on a worker thread** (§9.4). The real architectural answer
  to "GameDemo vs NetGame," but High effort / High risk (the `Api.Services` ambient global, the shared↔server
  cvar bridge, and `SoundService.Broadcast` are the named race blockers) and gated on a real-match profile
  showing server tick > ~2 ms *after* the areagrid. Do **not** start it without that profile.
- **Still deferred from §8** (unchanged, gated on a manual `dotnet-counters` measurement): GpuParticles3D node
  pooling (3.2-1), GPU vertex-shader MD3 morphing (3.3 Tier-3). See §8 "Deferred."

---

## 11. Rendering-pipeline audit (2026-06-12) — post-particle-merge

**Context:** user still reports stuttering/hitching after Waves 0–1. The dual particle system (faithful CPU
MultiMesh backend, now the DEFAULT for effectinfo effects) merged 2026-06-12 — **after** every prior audit — so
this pass re-examined the rendering pipeline end-to-end. Method: 4 parallel code audits (particle/effects render
path, entity render feed, GPU/scene config, full `_Process` inventory); every HIGH/MED claim below was then
re-verified by reading the cited code directly. Two agent claims were **disproved** and are recorded at the
bottom so they don't resurface.

### 11.1 HIGH — new since the particle merge (likely the felt regression)

**R1. GpuWarmPass does not warm the faithful particle path (first-explosion pipeline compile).**
`game/client/GpuWarmPass.cs` warms `effects.BuildWarmupInstances()` — the legacy GpuParticles3D burst materials —
but has zero coverage (verified: no faithful/splat references) of:
- the FaithfulParticleRenderer **premul** and **invmod** MultiMesh ShaderMaterials (`FaithfulParticleRenderer.cs:~100`),
  including the spark/oriented/billboard shader behavior driven via INSTANCE_CUSTOM;
- the **DecalSplats** ShaderMaterial (`DecalSplats.cs:510`, `_shader ??= SplatShader()`);
- the routed-path **fx_light** OmniLight3D (a new clustered-light pipeline state on first use).
Since faithful mode is now the default, the FIRST rocket/electro impact of a session compiles 2–3 pipelines
mid-play — exactly the §1.1 class of stall the warm pass was built to kill, regressed by the merge.
**Fix:** during warmup, spawn one 2-particle faithful effect of each archetype (one premul incl. a spark-flagged
instance, one invmod) through `FaithfulParticles.Spawn` + one `DecalSplats.Splat` + one `fx_light` into the warm
viewport, run the existing 2-frame FramePostDraw wait. ~30 lines, zero visual risk.

**R2. Per-splat node/resource churn in DecalSplats.** Every stain (each bouncing blood/bounce-particle impact
fires `OnStain`) builds a fresh `ArrayMesh` + `ShaderMaterial` + `MeshInstance3D` + `Tween`
(`DecalSplats.cs:490-544` AddSplatMesh), preceded by a synchronous brush query + Sutherland–Hodgman clip of every
overlapping brush face (`DecalSplats.cs:349-443`). A rocket volley splats dozens of marks across a few frames →
node + native-handle churn on top of the clip CPU. The shader is cached (good); everything else is per-splat.
**Fix:** pool MeshInstance3D+ShaderMaterial pairs (the 256-style ring `Decals.cs` already uses); reuse one
scratch ArrayMesh per pooled instance via `ClearSurfaces`+`AddSurfaceFromArrays`; replace the per-splat Tween
with one `_Process` fade ager over the live ring (one node, zero Tween allocs). Clip cost itself is bounded and
event-shaped — leave it unless profiling says otherwise.

**R3. Per-explosion OmniLight3D + SceneTree Tween churn.** `EffectSystem.SpawnInfoLight`
(`EffectSystem.cs:1970-2005`) creates an OmniLight3D + a SceneTree tween + QueueFree **per lightradius>0 block
per spawn** (explosions are often multiple blocks). No shadows (Godot default off — fine), but in a firefight
this is constant light-node create/free churn plus clustered-light count spikes.
**Fix:** pool ~16 fx lights (the `CsqcModelEffects` per-entity light pool at :263-290 is the in-repo pattern);
fade them in the EffectSystem's own per-frame pass instead of one Tween per light.

### 11.2 HIGH — sustained GPU baseline (shrinks the headroom that absorbs spikes)

**R4. The whole map is ONE MeshInstance3D and it casts directional shadows.** `MapLoader.BuildMap` packs every
surface into a single ArrayMesh on one node (`MapLoader.cs:187-188`), and `NetGame.AddLight` (:3381) adds the Sun
with `ShadowEnabled = true`. Consequences: (a) **zero frustum culling** — every surface of the map is drawn every
frame; (b) the **entire map re-renders into every directional shadow cascade every frame** (the dominant
multiplier: map × 4 cascades). The map's sun shadows are already baked into the lightmaps, so the realtime
cascade pass mostly re-derives them (DP itself runs r_shadow_realtime_world OFF by default — the current setup is
*less* faithful and more expensive).
**Fix (cheap, fidelity-positive):** set the world `MeshInstance3D.CastShadow = Off` (and likewise brush
submodels) — dynamic models still receive and cast shadows, static shadowing stays lightmap-authoritative, and
the cascade pass drops to just dynamic geometry. **Fix (bigger, later):** split the world mesh by BSP leaf-cluster
or a coarse grid into ~10-50 MeshInstance3Ds for real frustum culling; keep the texture+lightmap surface keying.

**R5. Surface = texture × lightmap-page split (draw-call multiplier).** `SurfaceKey(TextureIndex, LightmapIndex)`
(`MapLoader.cs:39`) splits same-texture faces across lightmap pages into separate surfaces/materials. With
per-page 128×128 lightmaps this multiplies draw calls and material binds on page-heavy maps.
**Fix:** atlas the lightmap pages into one texture at load (pages are tiny — even 64 pages = 1024×1024) and fold
the page offset into UV2 → surfaces key on texture alone. Load-time work only; pairs with the R4 mesh split.

### 11.3 MED — per-frame costs worth trimming

**R6. FaithfulParticleRenderer uploads the full grown capacity every frame.** The grow-only buffer design is
right (zero managed alloc — verified), but `MultimeshSetBuffer` requires the full `InstanceCount × stride` array,
so after a 5k-particle peak every later frame re-marshals the whole ~400 KB buffer even for 10 live particles
(`FaithfulParticleRenderer.cs:471-476, 578`). Growth itself also reallocates the GPU buffer mid-burst.
**Fix:** pre-size to a sane default (e.g. 4096 instances) at build, and decay the highwater (halve InstanceCount
after ~10 s below 25 % occupancy) so steady-state upload tracks actual usage. Both changes are invisible.

**R7. MusicPlayer scans the whole entity list every frame.** `EvaluateMusicSources` (`MusicPlayer.cs:142-235`)
walks ALL entities (×3 passes worst case) with per-entity string `ClassName` compares, every render frame, under
the SimGate lock when sv_threaded. **Fix:** maintain a small registered list of trigger_music/target_music
entities (populated at spawn/remove) and scan only that.

**R8. ModelTint pushes 4 `SetInstanceShaderParameter`s per mesh per player per frame** (`ModelTint.cs:56-66`)
even when colors are unchanged (the common case). **Fix:** stash the last-applied 4-color struct per entity and
early-out on equality; appearance changes are rare events.

**R9. Fading/ghost items re-walk their node tree every frame.** `ClientWorld.SetTreeTransparency`
(`ClientWorld.cs:1084-1092`) recurses `GetChild(i)` per fading item per frame (despawn fades, ghost items).
**Fix:** reuse the `CsqcModelEffects.GetCachedMeshes` flattened-list pattern keyed on the entity node.

**R10. ParticleSim bounce traces.** One `Api.Trace.Trace` per bouncing particle per frame
(`ParticleSim.cs:601`+) — this is exactly DP's model and the traces are pure-C# areagrid (fast), so it's
**faithful by design**; flagged here only as the expected scaling knob if profiles ever show `particles.sim`
dominating with thousands of live bounce particles (cl_particles_quality is the faithful lever, not code).

**R11. Misc steady trims (each small, all easy):**
- `NetGame._Process` re-reads `bgmvolume` / `cl_predictfire` / `cl_movement_perframe` via dictionary lookup every
  frame (`NetGame.cs:1731/1847/1859`) — cache + `Changed` subscription (MusicPlayer:107 is the pattern).
- `CrosshairPanel._Process` reads ~9 cvars/frame through `GlobalF` (2 lookups each, `HudPanel.cs:245-248`) —
  cache per-frame or on change. (The 2 true-aim traces/frame are QC-faithful — keep.)
- `PoolBurstNodes` (`EffectSystem.cs:79`) still default false — the §3.2-1 pooling exists and is opt-in; flip it
  on and A/B (the legacy GpuParticles3D path still serves trails/map emitters even in faithful mode).
- `WaypointSpriteLayer`/`RadarPanel` `QueueRedraw()` unconditionally (`WaypointSpriteLayer.cs:36`,
  `RadarPanel.cs:108`) — both genuinely repaint with the camera, so low value; gate only if canvas re-record
  shows up in a profile.

### 11.4 Disproved during verification (do NOT act on these if re-reported)

- **"Effect lights cast shadows by default" — FALSE.** Godot 4's `Light3D.shadow_enabled` defaults to **false**;
  no effect/projectile light enables it (grep-verified — only the Sun at `NetGame.cs:3381` and GameDemo's). The
  6-face-cubemap-per-frame claim does not apply.
- **"Faithful renderer allocates/sorts wastefully" — mostly FALSE.** The depth sort uses a cached
  `Comparison<int>` (no per-frame alloc), the pack loop is alloc-free, batches hide cleanly at zero particles.
  Only the R6 full-capacity upload + growth realloc are real.

### 11.5 Suggested order

| Pri | Item | Why first |
|-----|------|-----------|
| P0 | R1 warm the faithful path | new first-use stall, regression-shaped, ~30 lines |
| P0 | R4 world `CastShadow = Off` | one line; removes map×4-cascade redraw; fidelity-positive |
| P1 | R2 splat pooling + R3 fx-light pooling | firefight node churn (the felt combat hitches) |
| P1 | R6 buffer pre-size + decay | removes mid-burst GPU realloc spike |
| P2 | R7/R8/R9/R11 steady trims | one cleanup pass, all low-risk |
| P3 | R5 lightmap atlas + world mesh split (with R4 learnings) | draw-call/culling structure; biggest but slowest |

Verify per §7: `cl_frameprofiler 1`, exported release build, fixed scenario (first-shot-of-each-weapon for R1;
sustained firefight for R2/R3/R6; map fly-through for R4/R5).

### 11.6 Implementation status (2026-06-12) — P0/P1/P2 LANDED

All of R1–R4, R6–R9, R11 implemented in one pass. Build green; **1519/1519 tests pass**; windowed smokes verified
(`--fx-demo rocket_explode` renders the faithful explosion correctly; `--host stormkeep --bots 4` renders the full
match path — world lighting unchanged by R4, exactly as predicted since the LightmapShader is unshaded).

- **R1** — `FaithfulParticleRenderer.BuildWarmupInstances()` (one billboard + one spark instance per batch,
  sharing the LIVE batch materials), `DecalSplats.BuildWarmupInstance()` (shares the live splat Shader), plus a
  representative OmniLight — all appended in `EffectSystem.BuildWarmupInstances()` so the existing GpuWarmPass
  compiles them at map load. The warm BuildFromInfo bursts also seed the R3 light pool during the loading screen.
- **R2** — `DecalSplats` pools Slot{MeshInstance3D+ArrayMesh+ShaderMaterial}; mesh rebuilt in place
  (ClearSurfaces+AddSurfaceFromArrays); ONE `_Process` ager replaces per-splat Tweens (uniform pushed only during
  the fade window, via a cached `StringName`). `Clear()` releases to the pool (nodes survive map change).
- **R3** — `EffectSystem.SpawnInfoLight` is pool-backed (`MaxFxLights` 24, saturation recycles the oldest);
  one `_Process` fade pass replaces the per-flash SceneTree Tween + QueueFree; the routed-path holder node is
  gone. Lights are absolute-positioned (a flash is a positional snapshot — DP CL_AllocLightFlash semantics).
- **R4** — world `MeshInstance3D.CastShadow = Off` (MapLoader). Only dynamic models render into the sun cascades
  now. Accepted trade-off (documented in-code): dynamic shadows aren't occluded by world geometry.
- **R6** — batches pre-size to 512 instances at build (no mid-burst first realloc), grow in power-of-two steps,
  and decay capacity toward the 10 s rolling peak (the full-capacity upload now tracks recent usage, not the
  session max). Backwards-clock guard re-arms the decay timer on map change.
- **R7** — `MusicPlayer` scans a cached trigger_music/target_music list (rebuilt on EntityList change + 5 s
  safety rescan) instead of 3 passes over every entity per frame under the sv_threaded gate.
- **R8** — `ModelTint.ApplyAppearance(..., ref TintCache)`: per-entity last-applied snapshot
  (`CsqcModelEffects.State.Tint`); the 4×meshes `SetInstanceShaderParameter` pushes only run when the computed
  colors or the mesh list changed (mesh-list identity = count + first instance id, so a placeholder→real swap
  re-seeds).
- **R9** — `SetTreeTransparency` now pushes through the shared 3.2-2 cached mesh list with a change gate
  (`ItemFadeApplied`/`ItemFadeMeshCount`) — the constant ghost fade is one push, not one per frame per item.
- **R11** — NetGame `_Process` cvars (bgmvolume / cl_predictfire / cl_movement_perframe) cached + refreshed via
  `Changed` (unhooked in Shutdown); CrosshairPanel per-frame cvars cached the same way (unhooked in `_ExitTree`,
  which now also calls `base._ExitTree()` for HudPanel's own hook); `PoolBurstNodes` default → **true**.
- **R5** (lightmap atlas + world-mesh split for frustum culling) — NOT in this pass; it's the remaining P3
  structural item.

**Manual gate left (user, per §7):** the felt A/B in an exported release build — first-shot-per-weapon (R1),
sustained bot firefight (R2/R3/R6), and confirm no visual regressions in dynamic-model shadows indoors (R4).

---

## 12. Hitch-forensics profiler + input-latency fix (2026-06-12)

### 12.1 The legacy input-queue latency bug (FIXED — the felt "0.5 s fire delay")

Symptom: projectile leaves ~0.5 s after the click (muzzle flash instant — it's predicted), movement mushy.
**Not S5** (sv_threaded absent → the gate-null path is byte-identical; verified). Root cause, two halves:
- The legacy input mode (`cl_movement_perframe 0` — Bryan's saved config pins it; the B2 default flip doesn't
  apply over a seta) produces exactly one command per 1/72 s and the server consumes exactly one per tick — the
  queue length NEVER shrinks on its own. After a hitch the client bursts its accumulated commands (≤18/frame).
- Meanwhile `SimulationLoop.Advance` soft-caps catch-up at 4 ticks/frame and DROPS the sim backlog past 16
  ticks — so across consecutive long frames the server runs fewer ticks than the client queued commands.
  Net: every rough stretch permanently added ~0.2-0.5 s of queued input = standing fire/movement latency.

**Fix:** `InputQueuePolicy` (src/XonoticGodot.Net) + the trim in `ServerNet.ProvideInput` — DP
`sv_clmovement_inputtimeout` semantics: past an abnormal-depth trigger (7 commands; healthy slow-client batching
never reaches it) the oldest surplus is consumed UNSIMULATED down to a 2-command jitter floor. Dropped seqs still
ack (client prediction stays consistent — the brief DP-style reconcile warp), dropped one-shot impulses still
dispatch (a hitch can't eat a weapon switch). 4 regression tests. The per-frame input mode needs no trim (its
0.25 s/tick drain budget self-heals). **Verified live by §12.2's event stream:** `net: input backlog trimmed
10 -> 2` firing right after `sim: backlog dropped (236ms behind)` during an early-match model build.

### 12.2 The forensics system (FrameProfiler v2)

Every frame now lands in a 240-frame ring: full per-scope **time + allocation** table, GC counts + **pause ms**
(`GC.GetTotalPauseDuration` delta), draw calls, **pipeline-compile deltas** (RenderingServer counters — a nonzero
`pipe +N` mid-play is a first-use compile slipping past the warm pass, the §11 R1 class caught red-handed), and
one-shot **events** (`Prof.Event`, thread-safe/bounded/free-when-off) stamped onto the frame they landed on.
A hitch emits a rate-limited multi-line dump: `scopes:` (top 12, with KB), `gpu:`, `gc:`, `events:` (this frame +
the 7 before), `prev:` (8-frame run-up). `set cl_frameprofiler_dump 1` mid-play writes the whole ring to
`user://frameprofile_ring.csv`. Overlay gained a `draws/pipe/pause` line. Event sites wired: streamer main-thread
builds (named, with ms), GPU warm-pass completion, sim backlog drops, input-queue trims, faithful-particle
capacity changes. Docs in ../docs/RUNNING.md §Tricks.

**Gotcha it exposed immediately:** Godot's `_Process` delta UNDER-REPORTS long stalls (a 746 ms `stream.build`
scope inside a "51 ms" frame) — trust the scope table and events for stall magnitude, not the frame delta.

### 12.3 New empirical findings (next targets, by felt impact)

1. **Player-model main-thread builds are ~600-750 ms / ~180 MB alloc EACH** (Debug; §9 measured Release ≈ half).
   Wave 1 staggered them one-per-frame — but each one is still a monolithic `BuildSkeletalModel` stall, the
   dominant early-match hitch in the forensic log. Next lever: split the Godot-side build into budgeted
   sub-steps (per-surface mesh commit across frames) or move texture upload off the delivery frame.
   **→ FIXED (§12.4, 2026-06-12): staged build landed.** Measured split was materials ≈ 395 ms (texture
   decode+upload — 99.8 % of the "mesh" cost), anims ≈ 130 ms, skeleton+mesh ≈ 3 ms. Landed: (a) the
   animation library builds OFF-THREAD in the parse phase (`IqmBuilder.BuildAnimationLibrary` needs no live
   skeleton — bone names derive from the IQM joints); (b) texture read+decode moved to the worker via the
   `AssetSystem` predecoded-image handoff (`PredecodeMaterialTextures`, covering plain textures + companions
   AND Q3 shader-def stage maps — the first measurement missed the shader path and paid a 434 ms main-thread
   decode); (c) each material is its own streamer job (worker decode → main upload-only), with a count-down
   gate firing the now-cheap (~3 ms) assembly. A/B (same 3-bot scenario, Debug): monolithic 600-750 ms
   deliveries → 12-23 ms/job typical, 55 ms worst (one big multi-texture material); `pipe +0` on staged
   frames. Residual: a single 2048² upload can still cost tens of ms — split per-texture jobs or pre-generate
   mipmaps off-thread if a profile demands.
2. **Mid-play pipeline compiles still occur** (`pipe +1..+3` on model delivery frames — the model's own new
   materials; expected first-sight cost the warm pass can't precompile for arbitrary player models). Watch the
   `uber` slice: nonzero means the ubershader fallback is compiling, which Godot hides poorly on some drivers.
3. The input trim + sim-backlog-drop event pair is the canary for any future "feels laggy" report — if trims
   fire steadily (not just at match start), something is hitching repeatedly upstream; read the events.

### 12.5 R5 LANDED — world-mesh spatial split + lightmap atlas (2026-06-12)

§11 R5, both halves, implemented as a PACK-TIME REGROUP (face bucketing/appends untouched — pure repack):
- **R5a split:** triangles bin by centroid into 1024-qu cells (`WorldCellSize`), one ArrayMesh +
  MeshInstance3D per cell (CastShadow=Off per R4) → real frustum culling; the old single "Geometry" node drew
  every surface every frame. Vertex sharing preserved per (source, cell); border tris land in exactly one
  cell (no seams — geometry unchanged, only grouping).
- **R5b atlas:** every USED lightmap page (+ its deluxe pair, identical layout) packs into one
  gutter-padded atlas (`AtlasGutter` 2 px of replicated edge texels = the standalone page's CLAMP sampling —
  no cross-page bleed); UV2s remap per page during the regroup; lightmapped surfaces collapse onto ONE
  SurfaceKey per texture (`AtlasLitKey`) sharing one material bound to the atlas. Missing-page surfaces and
  no-atlas maps keep today's per-page/degrade path; a defensively-missing deluxe cell is filled with the
  neutral up-direction (128,128,255) so the shader's directional rescale is exactly 1 there.
- Materials are shared instances across cells (one per merged key); the load log now reports
  `materials/atlas/cells/surfaces` (e.g. stormkeep: 48 materials, atlas=1p, cells=31, surfaces=388).

**Verified:** build green; 1523/1523 tests; visual parity (same spawn captures, pixel-equivalent) on
stormkeep (deluxemapped external, 1 page), boil (2 pages), and dance (**9 pages** — the multi-page remap
path; the platform's baked floor shadow renders exactly, no seams/page-swaps). Culling measured live on
dance: 602 total surfaces, 339 drawn in an open-sky view (`pipe +0`), more culled indoors. Note stormkeep's
standing draw count is similar to pre-R5 (it was already 1 page; its win is the cull, which shows on
geometry-dense angles), while multi-page maps ALSO collapse their per-page material splits.

### 12.6 Post-playtest hitch census + refinements (2026-06-12, user-reported residual hitches)

150 s instrumented bot matches (Debug, mode-2 file sink). Baseline census: 27 hitches, worst 49.6 ms, three
classes — all addressed:
1. **Per-material texture jobs spiked to ~50 ms** when one material carried 6 big textures → staging is now
   per-TEXTURE (`EnumerateMaterialTextureNames`, shared with the predecode): one upload per job, measured
   2.5-3.3 ms typical; materials assemble from cache hits in the final stage.
2. **`pipe +N` first-sight compiles**: the idle warmer built models and freed them WITHOUT ever rendering —
   pipelines never compiled, so the first player wearing the model on screen paid it. Built warm models now
   render offscreen for a few frames via `GpuWarmPass.WarmNodes` before freeing. Verification run: ZERO
   mid-match pipeline compiles on hitch frames.
3. **`proc:other` tail attribution**: the faithful particle backend was the largest unscoped _Process — now
   `particles.cpu` (measured: 1-2 ms steady, exonerated). Also fixed a profiler bug: `proc:other` was
   differenced against the PREVIOUS frame's `_procMs` (printed impossible `proc:other > proc` rows).

**Remaining, honestly classified:** (a) a 12-20 ms missed-vblank tail (Debug `proc` ~5-12 ms vs a ~7 ms
vblank budget — halves in Release; mailbox vsync is the user-side option); (b) occasional 30-70 ms frames
correlated with 50-160 MB worker-side parse/decode allocation bursts driving gen0/gen1 pauses (bounded,
early-match; pool decode buffers if a release-build profile still shows them); (c) rare environment stalls
(huge `rest`, near-zero proc/alloc — compositor/driver/OS; one such cluster showed a 2.3 s blocked
MultiMesh upload attributed to `particles.cpu` — if that EVER recurs without rest-class neighbors,
investigate MultimeshSetBuffer, otherwise treat as external). Worker-side scopes (`iqm.anims`) appear in
frame tables with worker-sized values — they cost the POOL, not the frame; read alongside `proc`.

### 12.7 OS-stall resistance (2026-06-12)

The §12.6c [external?] class (rest-dominated ~100-140 ms frames, quiet game-side numbers) is the
compositor/driver/OS — not fixable in the repo, but RESISTIBLE:
- **`vid_fullscreen 2` (NEW)** — exclusive fullscreen: the desktop compositor leaves the present path on
  Windows (composited stalls were the user's worst felt hitches). 0/1 keep their stock meanings.
- **`sys_priority_boost` (NEW, default 1)** — the process runs ABOVE_NORMAL so background work (AV scans,
  indexer, browsers) can't preempt the main/render threads mid-frame. Deliberately not High (starves
  audio/driver threads). Verified live: game process BasePriority 10. `0` opts out; denial is logged.
- **`vid_vsync 2` (existing)** — mailbox: a missed present costs one late frame instead of a FIFO cascade.
- The hitch log now appends `EXTERNAL? (rest-dominated; OS/compositor/driver)` when a hitch matches the
  class (≥25 ms, rest ≥ 70 %, proc/gpu ≤ 30 %, no pipeline compile, no gen2) — so future logs separate
  "the machine did it" from "the repo did it" at a glance.
- Knock-on hardening already in place from earlier waves: the sim's catch-up soft-cap (B3), the input-queue
  trim (§12.1), and snapshot snap-on-stale mean a stall costs ONE visual gap — it no longer compounds into
  standing input latency or a tick spiral.
- User-side (not repo): GPU driver "prefer maximum performance" for the game, and on dev boxes exclude the
  repo/Godot dirs from real-time AV scanning (builds + asset churn trigger scans that land mid-play).

### 12.8 Lossless GPU wins: texture mipmaps + PVS cell culling (2026-06-12)

Prompted by "can we improve rendering perf without losing visual quality?" — two findings, both landed:

1. **World/model textures had NO mipmaps** (no loader ever called GenerateMipmaps) while every material
   samples with a `*_mipmap_anisotropic` filter — so distant/oblique surfaces sampled level 0: visible
   aliasing/shimmer (WORSE than DP, which mipmaps everything) AND texture-cache thrash at minification.
   `AssetSystem.EnsureMipmaps` now generates mips at decode (on the WORKER for streamed models — zero main
   cost), excluding `lm_NNNN` lightmap/deluxe pages (DP samples those unmipped — kept byte-exact) and
   already-mipped/compressed images. This is a fidelity fix AND a GPU win: verified visually (distant brick
   noise gone, near detail unchanged). Costs some load time + ~33% texture VRAM (the mip chain).

2. **PVS-driven cell visibility** (`WorldPvsCuller` + per-cell cluster sets recorded in the §12.5 regroup):
   each frame the camera's BSP cluster is found (one tree descent, re-applied only on cluster CHANGE) and a
   world cell shows iff ANY of its clusters is potentially visible — the map compiler's own conservative
   occlusion, the exact data DP culls with (BspPvs was already parsed+tested, just unused for rendering).
   Strictly lossless (per-cell union ⊇ DP's per-face vis). `r_pvs_cull 0` = escape hatch. Measured at the
   open GameDemo vantage: ~5% draws (282 vs 298 stormkeep, 563 vs 578 dance — open maps intervis heavily);
   the real benefit is INSIDE rooms/corridors where PVS hides everything beyond the walls. 1024-qu cell
   granularity dilutes it (a wall-spanning cell unions both sides); halve WorldCellSize if a profile asks.

Also answered: **`r_map_tint` has effectively ZERO perf impact** — the CPU side is change-gated
(`WorldTint.PushMap` only touches `GlobalShaderParameterSet` when the value changes) and the GPU side is one
unconditional vec3 multiply (`combined *= map_tint`) that executes identically whether the tint is white or
not — orders of magnitude below the texture fetches in the same shader. Per-second profile lines now carry
`draws N` for cheap culling A/Bs.

---

## 13. Optimization ledger, default flips, and backlog (2026-06-12, end of the rendering-perf arc)

### 13.1 Ledger — everything landed in this arc (chronological; detail in the cited sections)

| Item | What | Where |
|---|---|---|
| §11 R1 | Warm pass covers the faithful particle path (premul/invmod MultiMesh, splat shader, fx light) | GpuWarmPass / FaithfulParticleRenderer.BuildWarmupInstances |
| §11 R4 | World mesh CastShadow=Off (lightmaps are shadow-authoritative) | MapLoader |
| §11 R2/R3 | DecalSplats + fx-light pooling, single _Process agers (no per-spawn node/Tween churn) | DecalSplats / EffectSystem |
| §11 R6 | Faithful MultiMesh buffers: pre-size, pow2 growth, 10 s highwater decay | FaithfulParticleRenderer |
| §11 R7-R9, R11 | MusicPlayer entity-list cache; ModelTint + item-fade change gates; NetGame/Crosshair cvar caches; PoolBurstNodes ON | various |
| §12.1 | Legacy input-queue trim (DP sv_clmovement_inputtimeout) — the "0.5 s fire delay" fix | InputQueuePolicy + ServerNet |
| §12.2 | Hitch-forensics profiler: frame ring, scope+alloc tables, GC pause, pipeline-compile deltas, events, CSV dump, timestamps, EXTERNAL? tagging | FrameProfiler / Prof |
| §12.4 | Staged skeletal-model builds: off-thread anims + texture decode, per-texture upload jobs, warm-by-render for idle-warmed models | AssetLoader / AssetSystem / IqmBuilder / NetGame / GpuWarmPass |
| §12.5 | World split into 1024-qu cells (frustum culling) + gutter-padded lightmap/deluxe atlas | MapLoader |
| §12.7 | OS-stall resistance: vid_fullscreen 2 (exclusive), sys_priority_boost, external-stall classifier | ClientSettings / FrameProfiler |
| §12.8 | Texture mipmaps (fidelity + GPU win); PVS-driven world-cell culling (r_pvs_cull) | AssetSystem / WorldPvsCuller |
| §13.2 | Default flips: cl_gpu_morph 1, cl_pose_cull 1, sv_threaded 1 (after the trace-gate fix below) | ClientSettings / ModelAnimator / NetGame |
| §13.2 | TraceService.ConcurrencyGate — ALL trace entry points serialize vs the threaded sim worker | TraceService / NetGame |

### 13.2 Default flips (this pass) + their verification

- **cl_gpu_morph 1** (now registered; was fallback-only): animated MD3s morph in the vertex shader instead of
  per-frame CPU re-upload. A/B screenshots pixel-identical on stormkeep; ineligible models auto-fall back.
- **cl_pose_cull 1**: off-screen remote players skip bone-pose interop; distant on-screen refresh half-rate;
  local player never culled. (On-screen behavior unchanged by design.)
- **sv_threaded 1** (windowed listen servers; headless stays single-threaded): the FIRST 180 s soak failed
  loudly — 327 NREs + 10 range errors — because MAIN-thread traces (faithful-particle bounces, crosshair
  true-aim, projectile prediction; all outside NetGame._Process's gated span) raced the worker's sim ticks in
  TraceService's shared scratch/areagrid. Fixed with **TraceService.ConcurrencyGate**: every public trace
  entry point locks the host's SimGate when installed (Monitor is reentrant → the worker, which holds the
  gate around its whole tick, passes through; the single-threaded path stays lock-free). Re-soak: 180 s @
  6 bots, ZERO errors, worker signature confirmed (worker sim.move present, main server.tick absent).
- **Release export verified end-to-end** (templates present; exported from this branch): 120 s @ 6 bots →
  **5 hitches total, one ≥20 ms, p99 ~11 ms** (vs 85-179 hitches with a 12-20 ms tail in Debug) — confirming
  the Debug-tax analysis and closing the §10.4/§12.6 release-verification gate.

### 13.3 Backlog (called out, NOT implemented — ranked)

1. **AnimationLibrary cache per (model, skin)** — every bot wearing the same model re-runs a 100-360 ms
   worker clip-build for identical data; Animations are shareable Resources. Also shrinks worker GC bursts.
2. **Pool parse/decode buffers** (ArrayPool in IqmReader + TGA/DDS) — the 50-160 MB per-model worker
   allocation bursts → reused buffers (the remaining 30-70 ms GC-pause class).
3. **Client-side PVS culling for ENTITIES** — players/items behind walls still pose+draw; reuse BspPvs per
   entity (DP-faithful, conservative). Pairs with cl_pose_cull.
4. ~~**Off-thread texture UPLOAD spike**~~ — **VERIFIED + DONE for the menu warm (2026-07-31).** Godot 4.6.3
   (Forward+/Vulkan) accepts `ImageTexture.CreateFromImage` AND material/generated-shader construction from a
   `BackgroundAssetStreamer` worker: no engine errors, and a turntable render of a model whose textures and
   materials were built off-thread is **byte-identical** to the same render built on the main thread. Wired
   through `AssetSystem.WarmTextureOffThread` + a gated `_textureCache`/`_materialCache`, and used ONLY by
   `MenuAssetWarmer` (best-effort prefetch, so a failure degrades to "the match loads it normally"). Took the
   menu boot from 20 asset-build hitches to **0**. The live in-match paths deliberately still upload on the
   main thread — extending this to them is a separate call with a real blast radius (a bad material renders
   wrong rather than crashing), and would want the same visual A/B.
5. **Cluster-aligned (or smaller) world cells** — sharpens PVS culling; benefit indoor-map dependent.
6. **Godot occlusion culling (OccluderInstance3D)** — would also cull models/particles; pop-in risk if
   occluders are over-aggressive; ranked behind #3.
7. **Strip constant/unit animation tracks + Animation.Compress()** — IQM clips key pos+rot+scale per bone per
   frame; AnimationPlayer samples every track every frame per player (~195 tracks). Dropping unit-scale /
   constant tracks (build-time, off-thread) cuts per-player sampling + memory. Lossless.
8. **AnimationPlayer-vs-PushBones double-drive audit** — net players are CPU-posed every frame AND carry an
   autoplaying AnimationPlayer; if both tick, one is pure waste (possible free per-player win).
9. **VRAM lifecycle across map changes** — vram climbed 2451→2672 MB within one session; the texture/material
   caches never evict. Map-scoped eviction prevents long-session VRAM pressure stalls.
10. **World-texture predecode at map load** — reuse the §12.4 worker pipeline for MAP materials (load-time win).
11. **Gib/casing physics burst cap** — one phys 29.8 ms spike observed (gib shower of RigidBodies).
12. **net.send buffer pooling** (17-49 KB/frame on combat frames) — micro.

**Quality-tradeoff options (NOT lossless; user-facing settings, not defaults):** VRAM texture compression
(BC1/BC3 offline cache), MSAA step-down (currently 4x), shadow-cascade count, pickup-item CastShadow off.

**Watchlist:** the one-off 2.3 s blocked MultimeshSetBuffer (only investigate if it recurs WITHOUT the
external-stall signature); EXTERNAL? stalls (~135 ms, a few per session) are machine-level — correlate the
now-timestamped log against system events (WLAN scan / AV / driver) before touching the repo.

### 13.4 sv_threaded default REVERTED to 0 (2026-06-12, real-play desync)

The brief default-ON (§13.2) was wrong — reverted same day. Real play on the threaded release build
desynced the LOCAL player: camera through walls, projectiles firing from the server's idea of the player.
The 180 s bot soak that gated the flip had a fatal blind spot: an IDLE client — local-player prediction
barely executes without input, so the prediction-vs-worker interleave was never exercised. The per-trace
ConcurrencyGate (kept — it fixed a genuine crash storm and protects all main-thread traces) makes individual
operations atomic but cannot serialize the LOGICAL interleaving of worker ticks with the main thread's
prediction replay — §5 S5's original "ambient world shared by prediction and sim" blocker, surfacing as
wrong data instead of crashes.

**Landed with the revert:** a prediction-desync detector in `Reconciler.Reconcile` — sustained origin error
> 64 qu (normal corrections are 0-15 u) raises a latched, periodically re-raised `net: PREDICTION DESYNC`
forensic event. This converts the failure class from "feel" into log lines.

**Re-enabling sv_threaded requires:** a PLAYED threaded session (or scripted-input soak) with zero
PREDICTION DESYNC events — or the real architectural fix (the §5 two-world split: prediction on a private
facade, the loopback transport as the only contact surface). Soak protocol updated: any S5 retry must
include live player movement, not just bots.

### 13.5 S5 threaded desync — ROOT CAUSE + fix (transport stays on main) (2026-06-12)

The §13.4 revert blamed "logical interleaving the gate can't serialize." Deeper investigation found the
actual root cause: **the worker ran the Godot ENet transport, not just the sim.** `ServerNet.Tick` bundled
three things — `_transport.Poll()` (receive), `BroadcastSnapshots()`→`_transport.Send()`, `_transport.Flush()`
— with `_world.Frame()` (the sim). Godot's `ENetMultiplayerPeer`/`PacketPeerUDP` are created on the MAIN
thread and are main-thread-affine; servicing them from the worker mis-delivered/garbled snapshots. The client
then reconciled against CORRUPT authoritative state → runaway prediction (camera through walls, projectiles
from the server's stale position). The per-trace `ConcurrencyGate` (§13.2) correctly fixed the sim-side crash
storm but is powerless against a corrupt-snapshot problem — the transport was the real culprit, and the
idle-client soak never sent enough real input to expose it.

**Fix (the proper S5 split):** the worker runs ONLY `GameWorld.Frame` — the heavy pure-C# 4-12 ms sim S5
exists to move off the render thread — via `ServerNet.StepSimThreaded`. ALL Godot transport stays on the MAIN
thread via `ServerNet.PumpTransportThreaded` (receive client input → the worker's next step consumes it; send
the snapshot of the worker's latest sim state). Both sides still serialize every shared-world access on
`_simGate`, so it's race-free; only WHICH thread touches the Godot objects changed. `ServerNet.Tick` is kept
intact (and byte-identical) for the single-threaded default and the loopback/test harnesses.

**Verified:** 1523/1523 tests; windowed threaded bot smoke (worker confirmed running, ZERO errors, ZERO
PREDICTION DESYNC events, 12 ms median). The decisive test is a PLAYED session — the §13.4 detector stays live
to confirm the local-player path. If a played session is clean, sv_threaded can default ON; otherwise the
detector data names the residual.
