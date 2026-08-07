# Widening the asset streamer — what it feeds, and what that is worth (2026-08-06)

**Question asked:** "What does widening the streamer feed impact? Can we try that and see how fast it can be?"

**Short answer.** Widening the streamer's worker lane was worth **nothing** on its own, because a map load
barely used the lane: the two precache phases are synchronous main-thread loops that read, decode, compress
and upload one texture at a time. Once a **pre-pass** feeds that work to the lane, the same widening is worth
**36% of a cold load and 20–30% of a warm one**. And BC7 is the exception that had to be found by measuring:
it gets *worse* with every form of parallelism tried.

All numbers: stormkeep, dm, 0 bots, Debug project run (`--path .`), RTX 3080 dev box, 24 logical cores.
"Cold" = `~/XonData/data/dds` deleted first. Same harness as the C1/C2 captures, so the numbers are directly
comparable to those.

---

## 1. The measurement that reframed the question

`AssetSystem.CompressionTimeReport` previously reported one number — thread-time. That cannot tell "eight
threads for one second" from "one thread for eight seconds", and those have opposite fixes. It now reports the
wall span the encodes occupied and how much of the thread-time was paid on the frame thread:

```
textures.compress: 6817 ms of thread-time over 287 textures in 18646 ms wall
                   (0.37x parallel, 100% on the frame thread)
```

**100% on the frame thread.** Not "mostly", not "serialised by Betsy" — literally all of it, at `gl_texturecompression 1`,
which is a plain CPU codec that has nothing to do with Betsy.

The reason is a two-line path. `PredecodeTexture` (the worker half) decodes and mipmaps. `PrepareImage`
(reached from the main-thread `LoadTexture`) does picmip, mipmaps **and `MaybeCompress`**. So compression was
never on the lane for a map load. That also explains the C2 finding that `r_texturecompression_cpubudget`
measured as an exact no-op: **you cannot cap the concurrency of something that has none.**

## 2. Three arms that changed nothing, and why

| arm | encode off-thread | workers | pre-pass | cold load | parallel | on frame thread |
|---|---|---|---|---|---|---|
| A baseline | 0 | auto (4) | – | 19,449 ms | 0.37x | 100% |
| B widen only | 0 | 16 | – | 19,080 ms | 0.38x | 100% |
| C off-thread encode only | 1 | auto (4) | – | 19,886 ms | 0.38x | 100% |

**B** is the direct answer to "what does widening impact": 4 → 16 workers moved the load by 2%, inside noise.
**C** is the answer to "then move the encode off the frame thread": also nothing — because the map load does
not go through `PredecodeTexture` either. `PrecacheWeaponModelsAsync` and `PrecacheCombatSoundsAndModelsAsync`
are `foreach` loops calling `LoadModel` / `LoadSkeletalModel` synchronously, and those resolve materials, which
load textures, on the calling thread. Together they are ~15 s of a 19 s cold load.

**So the lane, its width, its priority band and the compression CPU budget were all tuned as if the load ran
through them. It did not. There was nothing there to widen.**

## 3. The pre-pass — feeding the lane at all

`NetGame.QueueLoadPredecode` posts one worker job per model at the start of the weapon phase, for the whole
precache set (every weapon `v_`/`h_` model plus the player-model roster — 58 models). Each job parses its model
off-thread and pre-decodes every texture its materials will probe. The synchronous loops that follow then find
those images parked in the handoff and do the GPU upload alone.

Bounded by `r_streamer_prepass` (the switch *and* the depth): a parked image is a full mip chain, and
stormkeep's texture set is ~3 GB uncompressed, so `PredecodeTexture` makes a worker wait rather than park past
the cap. Racing the main thread is harmless by construction — a texture main reaches first loads the old way
and the pre-pass's decode of it becomes a no-op.

### Cold cache, `gl_texturecompression 1` (S3TC)

| workers | park cap | cold load | encode thread-time | parallel | on frame thread |
|---|---|---|---|---|---|
| auto (4) | 64 | 16,370 ms | 16,140 ms | 1.04x | 37% |
| **8** | 64 | 12,711 ms | 37,237 ms | 3.08x | 14% |
| 12 | 64 | 12,709 ms | 52,962 ms | 4.41x | 10% |
| 16 | 64 | 13,489 ms | 66,479 ms | 5.17x | 9% |
| **8** | **192** | **12,046 ms** | 40,112 ms | 3.65x | 12% |

The knee is at **8 workers**; 12 ties it and 16 is worse. Thread-time inflating from 6.8 s to 40 s while the
wall clock halves is oversubscription, not extra work — 8 encoders plus the frame thread plus Godot's own
threads on 24 cores, each encode taking longer in wall-clock while more of them run at once.

Deeper park cap (64 → 192) is worth another ~5%: at 64 the workers spend time blocked on back-pressure.

### Head-to-head on one build

| | cold | warm |
|---|---|---|
| baseline | 18,471 ms | 9,916 / 8,244 ms |
| pre-pass, 8 workers, cap 192 | **11,777 ms** | **6,760 / 6,940 / 6,708 ms** |
| | **−36%** | **−20 to −30%** |

The **warm** column is the one that matters most — it is what every launch after the first costs, and the
pre-pass helps it even though there is almost nothing left to encode. The win there is parallel DDS read and
decode, not compression.

## 4. BC7 is the exception, and it had to be measured

| arm | cold load | textures encoded |
|---|---|---|
| baseline — encode on frame thread | 120,595 ms | 287 |
| pre-pass, 8 workers, encode off-thread | 144,351 ms | 411 |
| …plus a one-wide gate around the encoder | 180,758 ms | 415 |
| **pre-pass, 8 workers, encode kept on main** | **115,256 ms** | **287** |

> **Correction (see C10 below).** The first version of this section said Godot routes BPTC to **Betsy** — a
> compute-shader compressor owning one `RenderingDevice` — and that BC7 therefore serialised in a driver
> queue. **That is wrong, and the numbers below are what disproved it.** Godot routes BPTC to **CVTT, a CPU
> encoder** (`modules/cvtt/image_compress_cvtt.cpp`, dispatched across `WorkerThreadPool.get_thread_count()`),
> and Betsy does not implement BC7 at all. Measured: the process burns **13.4 of 24 cores for the whole encode
> and drops to ~2 the instant it ends.** The arms below are still exactly right about what to DO; the reason
> is oversubscription, not queueing.

Two things fall out, and the second one is the interesting one:

1. **Widening does not help BC7.** The encoder is already using most of the machine, so eight callers each
   fanning out internally just oversubscribe it — the 7.08x "parallel" in the 144 s run is contention.
2. **A serialising gate helps even less** (180 s). Throttling callers of an already-parallel encoder starves
   it. The intuitive fix was the worse one.

The real cost was neither. The pre-pass warms every texture a material *could* probe — companions, every shader
stage — which is **~45% more textures than the build consumes** (411–415 vs 287). Speculative work is only free
when it is free: an 8-wide etcpak absorbs it, a codec that costs 2.3 CPU-seconds per megapixel does not.

So the rule is **never hand the expensive codec the speculative work**. `AssetSystem.EncodeOffThread` is
`CompressOffThread && !UsesBetsy()` — the predicate name is a leftover from the wrong theory and now reads as
"is this the expensive path"; it is right about which textures it selects. The pre-pass still decodes in
parallel (which is where the warm-load win comes from), and the encode stays on the frame thread for exactly
the textures the build actually consumes. That arm is 115,256 ms — better than the 120,595 ms baseline and
65 s better than the naive pre-pass.

## 5. What shipped

| cvar | default | what it does |
|---|---|---|
| `r_streamer_prepass` | `0` | 0 = off; N = pre-pass on, at most N decoded images parked at once |
| `r_streamer_workers` | `0` | 0 = auto (a quarter of the machine, 2–4); N = N workers |
| `r_texturecompression_offthread` | `1` | encode on the worker lane — automatically suppressed for BC7 |

Defaults leave behaviour unchanged: with `r_streamer_prepass 0` nothing is queued, so the other two have
nothing to act on. The measured configuration is:

```
r_streamer_prepass 192; r_streamer_workers 8
```

Also landed:

- `Prof.IsMainThread`, so a cost can say which thread paid it.
- `BackgroundAssetStreamer.SetWorkerCount` — the lane grows on demand and retires workers as they go idle, so
  the cvar is honest in both directions (it was grow-only in the first draft).
- A **DDS cache write race** the extra concurrency exposed: two workers can encode the same vpath at once (the
  handoff's `ContainsKey` check is best-effort, not a lock) and both wrote `<name>.dds.tmp`, so one lost. The
  temp name now carries the thread id. Observed as `1 cache writes failed` per cold load at 8 workers.

## 6. Open

- **Should the pre-pass default on?** The load-time case is clear. The costs are real but bounded: GC pauses
  scale with the number of allocating threads (the menu warm's four-config capture measured 17.4 ms for a
  gen0+gen1 with four workers busy), and per-thread decode scratch is 4–16 MB each. Both land under a loading
  screen, where the load *is* the foreground work — but `r_streamer_workers` is process-wide, so a widened
  lane persists into the match, where `EnqueueStagedSkeletalBuild`'s per-texture fan-out would then run 8-wide
  instead of 4. Widening for the load and narrowing after is now possible (the lane shrinks); it is not wired.
- **The pre-pass's 45% over-warm** is wasted work on every codec, just invisible on a fast one. Restricting it
  to the textures a material actually binds would help BC7 most.
- **The map's own world textures are still not covered** — the pre-pass queues the weapon + player-model set
  (58 models). `render.setup` (~4.9 s cold) resolves BSP shader materials on the main thread and is untouched.

---

# P6 / P7 — making the texture cache actually a cache (2026-08-06)

**The DDS cache never converged, and nothing said so.** On stormkeep 61 of 287 textures re-encoded on *every*
launch — ~1.5 s at S3TC, ~5.0 s of an 11.0 s warm load at BC7 — while the summary line reported "cached 287,
next launch skips this" each time. Three separate bugs, found by logging every texture that reached the
encoder together with whether a cache file for it already existed on disk (`r_texture_dds_debug 1`). That one
diagnostic split the failures into two populations immediately: **22 with a cache file present, 39 without.**

## The three bugs

**1 — 22 textures: written under the resolved path, looked up under the requested one.** A bare model-shader
name (`a_shells.md3`'s "shellsammo" surface, which has no `.shader` entry) resolves through the
`textures/<stem>` fallback in `VirtualFileSystem.ImageCandidates`. `r_texture_dds_save` then banks the result
under where the bytes were *found* — `dds/textures/shellsammo.dds` — while the next launch probes the stem as
*asked for*, `dds/shellsammo.dds`, and misses. The fallback branch did emit `dds/textures/<stem>.dds`, just
**after** the raster forms, so the `.png` won every time. This is the identical ordering mistake C1 fixed at
the top of the method, missed on this one branch.

**2 — 39 textures: BC5/BC4 were decoded rather than passed through.** `DdsDecoder` deliberately expanded
RGTC to RGBA8, on a July rationale that predates the fix for it: `norm_rg` and the `_texMeta` registry landed
2026-08-02 precisely so a two-channel normal map binds correctly and the shader reconstructs Z. And the
decisive argument is not the channel question at all — **BC5 is what `MaybeCompress` itself produces** for
normal maps, so a cold load already uploads exactly this format down exactly this path. Expanding the cached
copy guaranteed decode → re-encode → same BC5, for ever. BC5 now passes through (40 files here); **BC4 still
does not** — there is no `norm_r` counterpart and the remarks name a concrete breakage (the skin shader reads
gloss from `.g`, which RGTC_R leaves at 0). That is 3 files, and the real fix is upstream in `MaybeCompress`
choosing RGTC_R for a greyscale glow map at all — see Open.

**3 — a `dds/dds/` directory.** A texture loaded from `dds/foo.dds` has that as its vpath, so re-saving it
stemmed to `dds/foo` and wrote `dds/dds/foo.dds`. 39 junk files on this machine, read by nothing.

## P7 — the cache had no idea which setting produced it

A DDS records its block format but nothing about the mode that chose it, and `MaybeCompress` skips anything
already compressed. So **`gl_texturecompression` was inert after the first load.** Verified directly: a warm
load at mode 1 against a BC7-populated cache re-encoded only the same 22 textures a mode-2 run did — it was
reading BC7 blocks throughout, and a player switching modes would have had to find and delete the cache by
hand to make the setting mean anything.

Our own cache now lives in a mode-tagged directory (`dds1/`, `dds2/`), probed ahead of the shared `dds/` tree.
That tree is untouched — it is where Xonotic's 3,207 shipped files live, and they are used whatever the
setting says, exactly as DarkPlaces uses them: re-encoding shipped DXT1 to BC7 would cost a fortune to make
the image worse.

## Result

| | before | after |
|---|---|---|
| warm load, mode 1 (S3TC) | 8,244 ms, **61** re-encodes | **5,909 ms, 3** re-encodes |
| warm load, mode 2 (BC7) | 11,014 ms, **22** re-encodes (~5.0 s) | **6,249 ms, "nothing compressed"** |
| switching modes | silently reused the other mode's blocks | re-encodes; both caches persist and stay hot |

Warm BC7 is now within 0.3 s of warm S3TC, because neither encodes anything.

Seven tests in `DdsCacheResolutionTests` pin the resolution rules — and were checked against the pre-fix code,
where the two P6 cases fail. The existing coverage only ever asked "did something resolve?", which is exactly
why this survived: every candidate resolved fine, just not the one that made the cache work.

---

# C10 — is a different BC7 encoder worth having? No. (2026-08-06)

`texcompress_bench` measures encoders on identical decoded pixels, in-process. 12 real textures, 50.33 Mpixel,
level 0 only, 24 logical cores:

| encoder | wall | CPU-s | MB out | PSNR |
|---|---|---|---|---|
| **Godot `Image.Compress` (CVTT BC7)** | **5,484 ms** | **114.5** | 48.0 | **53.71 dB** |
| BCnEncoder.NET BC7 `Fast` | 74,841 ms | 1,618 | 48.0 | 47.90 dB |
| BCnEncoder.NET BC7 `Balanced` | 121,673 ms | 2,713 | 48.0 | 48.15 dB |
| BCnEncoder.NET BC7 `BestQuality` | 326,827 ms | 7,053 | 48.0 | 48.35 dB |

The managed encoder's *fastest* mode is **13.6x slower and 5.8 dB worse**; its best is 60x slower and still
worse. The package reference was removed; the bench stayed, because the number it produced is the one worth
keeping:

**Godot's BC7 encoder is not slow — it does about 8–9 Mpixel/s and 2.3 CPU-seconds per Mpixel at 53.7 dB.**
A cold BC7 load is expensive because of the *volume* (287 textures, ~1.5 Mpixel each), not because the encoder
is bad. That kills the encoder-swap options outright, C6 included: Godot passes
`ConfigureBC7EncodingPlanFromQuality(plan, 5)` on a **1 (fastest) to 100 (best)** scale, so it is already near
the fast end and there is no quality left to give back.

## The comparison that actually matters

| encoder | wall | CPU-s | MB out | PSNR | cores busy |
|---|---|---|---|---|---|
| BC7 (CVTT) | 6,297 ms | 116.2 | 48.0 | 53.71 dB | **18.5** |
| S3TC (etcpak) | **701 ms** | **0.6** | 28.0 | 43.92 dB | **0.9** |

**BC7 costs ~190x the CPU of S3TC for +9.8 dB, and is 1.7x larger.** If encode time is the problem, the answer
is the format, not the encoder.

It also explains why the load pre-pass helps mode 1 and hurts mode 2: **etcpak uses 0.9 cores — it is
effectively single-threaded, so our worker lane can parallelise it** (measured 3.35x). CVTT already saturates
18.5, so handing it more concurrent callers only oversubscribes the machine.

## Still open

- **`MaybeCompress` produces BC4 for greyscale glow/gloss maps** (`h_mega_glow` and 2 others cache as `ATI1`).
  Godot's `detect_used_channels` returns R-only for a greyscale image, which routes to RGTC_R — and the skin
  shader reads gloss from `.g`, which RGTC_R leaves at 0. This is a **live visual bug on the cold path**, not
  a cache one; the cache merely made it visible. Fixing it also removes the last 3 per-launch re-encodes.
- Shipping a prebuilt cache (C4) and background encoding (C5) remain the only changes that remove the cost
  rather than shrink it.
