# Loading & asset pipeline — progress and next steps

**Status: IN PROGRESS (2026-07-31).** Index and continuity record for the menu-warm / asset-pipeline thread.
Everything here is landed on `main` unless marked otherwise.

This is deliberately a **map**, not a duplicate. The detail lives in:

- [loading-speed-background-precache-2026-07-06.md](loading-speed-background-precache-2026-07-06.md) — the
  original load-path analysis plus the 2026-07-31 implementation log for the menu-warm rework.
- [texture-compression-and-caching-2026-07-31.md](texture-compression-and-caching-2026-07-31.md) — how
  DarkPlaces compresses and caches, what the port does, the measured CPU-compression cost, options and ranked
  recommendations.
- [PERFORMANCE_REPORT.md](PERFORMANCE_REPORT.md) §13.3 — the standing perf backlog (item 4 now closed).

---

## What this thread was

Started from one report: **"the menu runs very slow when you first load it on a release build; after a few
seconds it's normal again."** It turned into three connected pieces of work — the menu warm, the tooling that
measures it, and the texture pipeline it exercises.

## What landed (`e3126b16..26fe923b`)

| commit | what |
|---|---|
| `f57f0441` | Take the menu asset warm off the main thread entirely |
| `4c303023` | Serialize the warm's GPU uploads; streamer workers to `BelowNormal` |
| `ef52a26b` | Point the parity workflows' default repo path at `Projects/Vortex` |
| `7737b8ad` | Move the sound set, gib/item models, particlefont and HUD art to the menu warm |
| `bec0d4e9` | Follow pk3 image alias stubs; decode BC4/BC5/DX10; wire `gl_texturecompression` |
| `26fe923b` | Texture compression/caching notes; scope `tex.compress` |

### Headline results (release export, measured)

| | before | after |
|---|---|---|
| menu boot, first window | **7 frames**, p50 **829.8 ms** | **644 frames**, p50/p95 **6.9 ms** |
| menu asset-build hitches | every frame slow | **0** (one 13 ms frame remains) |
| warm coverage | 24 weapon + 6 player models, 36 sounds | 80 models, 63 textures, **223** sounds |
| stormkeep VRAM (`gl_texturecompression 1`) | 2647 MB | **956 MB** |
| stormkeep DDS decode failures | 18 | **0** |

In-match A/B stayed neutral across every change (`ASSET-BUILD` hitches 4 vs 4, p50 4.1 vs 4.2 ms). Suite
3973/3973 throughout.

## The four things worth remembering

1. **A per-frame budget that always runs "at least one item" is not a budget.** When items cost 300–900 ms
   against a 1.5 ms budget it silently becomes *one heavy item every frame*. Both drains now bank overshoot as
   debt and skip frames to pay it back.
2. **Godot 4.6.3 accepts texture upload AND material/shader construction from a worker thread.** Verified by
   byte-comparing a rendered turntable (md5 `080245315ffc…`), not by absence of a crash. This closed
   `PERFORMANCE_REPORT.md` §13.3 #4. Used *only* by the menu warm, where failure degrades to "the match loads
   it normally"; live in-match paths still upload on the main thread.
3. **With the main thread idle, the remaining cost arrives via the allocator and the driver.** N parallel
   decoders make an N-scaled GC pause (17.4 ms with four workers busy); N parallel uploads saturate driver
   ingest so the frame blocks in present. The warm is now strictly serial — measured better on every axis than
   any parallel configuration.
4. **Labels lie; read the breakdown.** A hitch classified `GC-PAUSE` had a 0.2 ms GC pause in a 24.9 ms frame;
   the real cost was `rest`. Chasing the label would have optimised the wrong thing.

## Tooling fixed along the way

- `run-release.ps1` / `run-release.sh` / `tools/perf-run.ps1` never placed `data/` beside the exported binary
  and relied on the CWD probe in `DataPaths.ResolveExported`. **A release build launched from anywhere else
  mounts no content and still boots, self-quits, and writes a session log full of flattering numbers** — which
  is exactly how the first capture of this investigation came back clean. All three now reproduce the packaged
  layout and launch from the install dir.
- `MenuAssetWarmer` shipped with no `Prof.Sample` scope, so its cost hid in `proc:other`; and the hitch
  detector needs a rolling median to spike above, so when *every* frame is equally slow it reports "no
  hitches". That pair is why the regression survived three weeks. The node now owns no main-thread work at all
  (no `_Process`), so `menu.warm` was removed from `TopLevelNodeScopes` rather than left permanently zero.

---

## Next steps, ranked

### 1. Per-category `gl_texturecompression_*` cvars — ✅ **DONE 2026-08-01**
**Eleven** cvars, not twelve — `gl_textures.c:37-48` is twelve lines because line 37 is the master, which
already shipped in `bec0d4e9`. Registered with DP's defaults; Xonotic's overrides arrive the normal way, from
`xonotic-client.cfg:789-794` executing over them. `_norm` routes to `_normal`, which defaults **off**.

Classification lives in `src/VortexArena.Formats/Vfs/TextureCategories.cs` (pure string logic, unit-tested —
40 cases) rather than in `AssetSystem`, because the failure mode is silent: a mis-bucketed texture does not
error, it just obeys the wrong cvar.

**This turned up a live bug it also fixes.** `CompressSource.Normal` makes Godot emit a two-channel BC5 whose
blue reads 0, and every normal-sampling shader here uses `.z` — so `gl_texturecompression 1` was inverting
lighting on every `_norm`, undoing the Z reconstruction `bec0d4e9` added three files away. Details and the
consequences for the §4 measurement are in
[texture-compression-and-caching-2026-07-31.md](texture-compression-and-caching-2026-07-31.md).

**Recommendation on the master default: keep it at 0** — see that doc's "On flipping the master default".

### 2. Fix VortexMaps packaging so alias stubs stop shipping
`shared.pk3` carries **974** symlink stubs (903 pointing at a real DDS) because the symlink bit was lost when
the pack was zipped. It is our build output (`data/maps.lock.json` → `VortexFPS/VortexMaps`), not upstream's.
The runtime alias-following stays regardless — it fixes packs already in the wild.

### 3. Visual pass on the alias change — **before this reaches players**
Normal/gloss companions that previously resolved to *nothing* now load. stormkeep renders correctly, but this
is a fidelity change across all maps and deserves eyes on a handful.

### 4. Remaining map-load → menu-warm candidates
From the earlier survey, still unmoved and all map-independent:
- **Player-model PSO warm.** The warm deliberately skips it (viewport-variant specific per
  `godot-pipeline-compile-internals`), so the per-match `GpuWarmPass` still builds + renders the roster.
  Given two "known limits" in this area turned out false this session, **re-test the claim** before accepting it.
- Map-dependent work is only reachable via the unimplemented **O3 menu-time prewarm** (BSP parse, collision
  build, map textures/lightmaps, waypoints, music) — the map is knowable at Create-Game selection, and the
  process-lifetime cache that blocked it now exists.

### 5. Smaller, known
- **GC/allocation**: `PERFORMANCE_REPORT.md` §13.3 #2 (ArrayPool in IqmReader + TGA/DDS). Sound loading needs
  one exact-size managed `byte[]` per file for `LoadFromBuffer`, much of it LOH.
- **Cache eviction**: §13.3 #9 — texture/material caches never evict; each visited map's lightmaps accumulate.
- **`docs/RUNNING.md` still lists the old project root** (`Projects/Xonotic/...`) as live operational info.
- ~20 dated postmortems still carry old paths; left alone deliberately (they are records of when they were
  written).

### Explicitly NOT recommended
- **A bake/store cache for compressed textures.** Measured: CPU compression costs ~70 ms for an entire map
  load. There is no meaningful cost left to amortise, and a cache brings disk, invalidation and a new failure
  surface. This reverses an earlier recommendation in this thread — see the notes doc for the numbers.
- **Switching audio format away from Ogg Vorbis.** The cost measured was allocation, not codec work; any
  format loaded via `LoadFromBuffer` pays it identically. Switching diverges from upstream content for no
  measured benefit.
- **BC5 pass-through** without first teaching `LightmapShader` + `PlayerSkinShader` (×2) to reconstruct Z.
  Upstream ships `gl_texturecompression_normal 0`, i.e. DP declined this trade too.

---

## Ground rules that paid off here

- **Measure on the release export, not Debug** — and check the capture actually loaded content (see the
  `data/` trap above).
- **Verify rendering changes by comparison, not by absence of errors.** The turntable byte-compare is the
  reason the off-thread work is trustworthy; re-run it after any change to the texture/material path.
- **Re-run the in-match A/B** for anything touching `BackgroundAssetStreamer` or `AssetSystem` — they are
  shared with live play, not just the menu.
- Suite must stay 3973/3973.
