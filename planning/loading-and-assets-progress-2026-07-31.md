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

### 1b. Code-review fixes across the asset path — ✅ **DONE 2026-08-01**

The fourteen findings from the review of this thread's own commits. The largest was structural: pk3 link
following moved OUT of `AssetSystem` (which read every image in full, unpooled, before decoding it just to
discover it was not a 20-byte stub) and INTO `Pk3Mount`, which already indexed symlinks at mount time and
validated targets against its own archive. That one move fixed three findings — the double read, the
duplicate cache key/upload for a link and its target, and a cross-pack redirect the runtime probe allowed.
The rest: `LoadTexture` now takes the upload gate (it never did, so "one upload in flight" was false against
the main thread); compression moved out of the gate; `Request` always calls back so one throwing worker can
no longer strand the entire menu warm; the debt pacer stopped blocking High-priority work; three static
shader lazies got locked; BC4/BC5 SNORM is rejected rather than silently decoded as UNORM.

**In-match A/B, stormkeep + 2 bots, macOS release export, n=2 per arm:**

| metric | before | after | verdict |
|---|---|---|---|
| **alloc total** | **1371.5 MB** | **975.2 MB** | **−28.9%, real** (spread <0.3% within each arm) |
| p50 / p95 frame | 8.8 / 12.3 ms | 8.9 / 12.4 ms | neutral |
| p99, 1% low, slow frames | — | — | **noise** — swung ±44% and FLIPPED direction between pairs |

The allocation drop is the double-read and un-pooled LOH allocation going away, which is what the fix
predicts. The tail metrics are the lesson: on the first pair they looked like a 28% regression in 1%-low, and
a second pair reversed them. **One pair is not an A/B at this map/duration** — the tail needs more samples
than the mean does.

Two caveats worth carrying: this is an M-series Mac, so the numbers are NOT comparable to the RTX 3080
baselines in `tools/perf-baselines` (an A/B is relative and still valid); and both arms must run with a WARM
shader cache — the first capture after an export pays sync pipeline compiles that shift the whole session and
made the very first comparison useless.

Rendering verified unchanged by byte-comparing a turntable: `980252d4c4f1187bad0c30e5f2e7a18b` on both sides.
Note the capture resolution must be checked before trusting that hash — a first-run capture landed at
1280×720 before the compositor resized the window to 1650×1050, which read as a rendering difference and was
not one.

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
- **Suite must stay 4013/4013** *with the full 32-pack map set installed*. The number is content-dependent and
  that is not a footnote: with no maps it is 3731, because ~242 map-dependent cases produce no test cases at
  all rather than skipping visibly. `TestPaths.Maps` is tri-state for this reason — a PARTIAL set (one pack,
  which is what `ci/ci.sh`'s own stormkeep fetch leaves behind) lowers thresholds instead of asserting
  full-set ones. Quote the number with the content state or it means nothing.

### Running all four on macOS (2026-08-01)

Every rule above is now satisfiable on a Mac. It took four portability fixes to get there, and the specifics
below are the ones that will bite again.

```bash
./vx setup --profile dev      # engine into .godot-bin/, 32 map packs, pinned export templates
./vx engine --editor          # Godot's OWN templates (~1.1 GB) — macos-client has no custom template
./vx export --preset macos-client
./vx ci                       # the full gate, smokes included
```

**The release export.** `macos-client` is the one preset with no `custom_template/release` (a declared
exception in `engine.lock.json`), so it falls back to the editor's stock template set — which nothing
installed until `vx engine --editor` existed. Output is `dist/macos-client/VortexArena.app`, and macOS keeps
its content *inside* the bundle at `Contents/Resources/data`. `tools/perf-run.sh` places it there itself now;
the `data/` trap is the same trap, in a different directory.

**Warm the shader cache before comparing anything.** The first capture after an export pays sync pipeline
compiles that shift the whole session — the first attempt here produced a 58 s "after" against a 29 s
"before" and was worthless. Run each arm twice and use the second. Both arms share
`_scratch/perf-userdir`, so whichever runs first eats the cost.

**The mean needs n=1; the tail needs more.** At stormkeep/25 s, `p99` and `1%low` swing **±44%** run to run.
The first A/B pair here showed p99 +39% and 1%-low −28% — a convincing regression — and a second pair
reversed both. `alloc_total_mb` by contrast reproduced to under 0.3%. Report tail numbers only with at least
two pairs, or don't report them.

**These numbers are not comparable to `tools/perf-baselines/`.** Those are the Windows/RTX 3080 dev box. An
A/B is relative and stays valid, but never diff a macOS capture against a stored baseline.

**Check the turntable's resolution before trusting its hash.** A capture landed at 1280×720 before the
compositor resized the window to 1650×1050, and the differing md5 read as a rendering regression. It was not.
Compare sizes first, then bytes.

```bash
"$(sh -c '. tools/lib/find-godot.sh; find_godot "$PWD"')" --path "$PWD" \
    --resolution 1280x720 --screenshot out.png --screenshot-frames 150 --model erebus
```

**What was broken, and is not any more:** `python` (gone since macOS 12.3 — resolved via
`tools/lib/find-python.sh`), `timeout` (not in BSD userland — `tools/lib/run-timeout.sh`), the hardcoded
Windows Godot paths (`tools/lib/find-godot.sh`), and `nuget.config`'s committed local package source. A
`macos-latest` job in `ci.yml` now runs `ci/ci.sh` so this cannot rot again — which is the actual fix, since
all four survived only because nothing ever ran on a Mac.
