# Texture compression & caching — how DarkPlaces does it, where we are, what to do next

**Status: NOTES + RECOMMENDATIONS (2026-07-31).** Written after the menu-warm work
([loading-speed-background-precache-2026-07-06.md](loading-speed-background-precache-2026-07-06.md)) turned up
~2.6 GB of menu-resident VRAM and a pile of `failed to decode DDS` errors. Everything marked **measured** below
was captured on the release export on this box (RTX 3080, stormkeep); everything else is reading upstream source
or reasoning, and is labelled as such.

DarkPlaces source references are to the reference checkout at `C:\Users\Bryan\Projects\Vortex\Base\darkplaces`
(the same tree `tools/parity-cvar-diff.py` diffs against).

---

## The questions

1. How does DarkPlaces handle texture compression and caching?
2. Can we bake and store compressed textures?
3. **Is CPU-side compression too slow / will it cause hitching?** (the reason this doc exists — the claim that
   CPU compression is fine was asserted before it was measured, and deserved the challenge)

## Short answers

1. **Compression is driver-side** (DP hands `GL_COMPRESSED_*` to `glTexImage2D` and lets the GL driver do it),
   gated by a master cvar plus twelve per-category cvars. **Caching is `r_texture_dds_save`**, which reads the
   compressed texture back off the GPU and writes `dds/<name>.dds` for next time.
2. **DP already does, and the `dds/` tree Xonotic ships IS that bake's output.** We don't need to build one.
3. **No — measured at ~70 ms for an entire map load.** This was the surprise: the objection doesn't survive
   contact with a profiler, and it kills the main argument for building a bake cache at all.

---

## 1. How DarkPlaces compresses

No CPU compressor anywhere. DP selects a compressed GL internal format and lets the driver compress at upload;
the only quality control is a hint (`gl_textures.c:1047`):

```c
if (gl_texturecompression.integer >= 2)
    qglHint(GL_TEXTURE_COMPRESSION_HINT, GL_NICEST);
else
    qglHint(GL_TEXTURE_COMPRESSION_HINT, GL_FASTEST);
```

`gl_texturecompression` is therefore a **master gate + quality selector**, not a format selector — 0 off
(its own description says this overrides the per-category cvars), 1 fast/low quality, 2 slow/high quality.
Twelve `gl_texturecompression_*` cvars then choose which categories participate (`gl_textures.c:37-48`):

| category | DP default | Xonotic override (`xonotic-client.cfg:788-794`) |
|---|---|---|
| `_color` | 1 | 1 |
| `_gloss` | 1 | 1 |
| `_glow` | 1 | 1 |
| `_lightcubemaps` | 1 | **0** |
| `_reflectmask`, `_sprites` | 1 | — |
| **`_normal`** | **0** | — |
| `_2d`, `_sky` | 0 | `_sky` → **1** |
| `_q3bsplightmaps`, `_q3bspdeluxemaps` | 0 | `_q3bsplightmaps` → 0 |
| **master `gl_texturecompression`** | 0 | **0** |

Two things worth internalising:

- **`gl_texturecompression_normal` defaults to 0.** Upstream deliberately does not block-compress normal maps.
  That independently confirms the concern that stopped us passing BC5 through to `RgtcRg` — see
  [the DdsDecoder remarks](../game/loaders/DdsDecoder.cs).
- **Xonotic ships the master at 0**, so stock DP+Xonotic effectively does *no* runtime compression. The cfg
  even carries `// FIXME the description is wrong - when this is 0, e.g. gl_texturecompression_sky still takes
  effect`, i.e. upstream isn't sure the master gates everything. Runtime compression is not the mechanism
  Xonotic relies on — the shipped `dds/` tree is.

## 2. How DarkPlaces caches — this is the "bake and store" answer

`gl_rmain.c:156-157`:

```c
r_texture_dds_load  "0"  "load compressed dds/filename.dds texture instead of filename.tga, if the file exists"
r_texture_dds_save  "0"  "save compressed dds/filename.dds texture when filename.tga is loaded,
                          so that it can be loaded instead next time"
```

`R_SaveTextureDDSFile` (`gl_textures.c:1431`) implements the bake by **reading the texture back off the GPU** —
`qglGetTexLevelParameteriv(..., GL_TEXTURE_INTERNAL_FORMAT, ...)` — mapping the GL internal format to a DDS
FourCC (DXT1/3/5, plus DXT2/DXT4 when alpha is premultiplied), walking the mip chain and writing a real `.dds`.
`r_texture_dds_save 1` saves only textures that came back compressed; `2` also saves uncompressed ones.

The call sites (`gl_rmain.c:2428-2534`) bake one file per skinframe channel: base, `_mask`, `_norm`, `_glow`,
`_gloss`, `_pants`, `_shirt`, `_reflect`.

**Those are exactly the filenames in the `dds/` tree Xonotic ships.** The conclusion is hard to avoid: the
shipped `dds/` tree is the output of someone running DP once with `r_texture_dds_save` on. The bake already
happened, upstream, and the result is in the pk3s.

Consequences of DP's design worth noting, because they are all things we are not stuck with:

- It can only bake what the driver produced, so **output quality varies by GPU vendor** and the baked artefact
  is non-deterministic across machines.
- The readback makes it **impossible on GLES2** (`R_SaveTextureDDSFile` returns -1 there outright) and it
  carries a driver-specific crash workaround (`if (!strcmp(gl_version, "2.0.5885 WinXP Release")) return -2`).
- Cache keys are **filenames**, not content hashes, so a changed source texture silently keeps a stale bake.

---

## 3. Where the port is now

| piece | state |
|---|---|
| `dds/` tree preferred over TGA | ✅ always (`VirtualFileSystem.cs:399`), not cvar-gated as in DP |
| DXT1/3/5 pass-through, compressed, with mips | ✅ pre-existing |
| BC4 / BC5 / DX10 header / BC6H / BC7 | ✅ added 2026-07-31 |
| pk3 alias stubs followed | ✅ added 2026-07-31 — **the actual bug** |
| `gl_texturecompression` (master, 0/1/2) | ✅ added 2026-07-31, default 0 |
| `gl_texturecompression_*` per-category | ✅ added 2026-08-01 — **eleven**, not twelve (see below) |
| `r_texture_dds_load` / `_save` | ❌ not implemented (load is unconditional; no bake) |

### Correction: it is eleven cvars, not twelve

`gl_textures.c:37-48` is twelve lines because **line 37 is the master itself**. The table in §1 lists eleven
categories plus the master; "twelve `gl_texturecompression_*` cvars" above was counting the master twice.
Verified against upstream source, not the table.

### ⚠ `CompressSource.Normal` inverts normal-map lighting — found 2026-08-01

`MaybeCompress` passed `Image.CompressSource.Normal` for `_norm` textures on the theory that it weights the
channels for normals. It does — by **declaring the image RG-only**. Godot's
`detect_used_channels(COMPRESS_SOURCE_NORMAL)` returns `USED_CHANNELS_RG` unconditionally (`image.cpp:3401`,
comment: *"Normal maps only use RG channels"*), which routes to `BETSY_FORMAT_BC5_UNSIGNED`
(`image_compress_betsy.cpp:761`) — a two-channel texture whose **blue samples as 0**.

That is precisely the failure [the DdsDecoder remarks](../game/loaders/DdsDecoder.cs) cite as the reason BC5 is
CPU-decoded to RGBA8 with Z reconstructed instead of passed through: `LightmapShader` and `PlayerSkinShader`
(×2) unpack `texture(normal_tex, uv).rgb * 2.0 - 1.0` and use `.z`, so B=0 gives `z = -1` and the shaded normal
points into the surface. **`bec0d4e9` therefore undid its own fix**: the decoder expanded BC5 to recover Z, and
`MaybeCompress` re-compressed it straight back to BC5.

So **the 3139 → 956 MB VRAM figure in §4 was measured with inverted normals on screen.** It was a VRAM/perf
capture; the turntable byte-compare was run for the off-thread work, not for compression. The VRAM number is
still real, but it is not the cost of a correct render.

Fixed by always passing `CompressSource.Generic`, which keeps a real blue channel (DXT1 opaque / DXT5 alpha /
BC7). A compressed `_norm` is now correct but lower quality than BC5 would be; getting BC5's quality still
needs the three shaders taught to reconstruct Z — option (E), unchanged.

### The alias-stub bug (the one that mattered)

The `failed to decode DDS` errors looked exactly like BC4/BC5 rejection. They were not. Those entries are not
DDS files at all — they are 15-20 byte ASCII stubs whose content is a sibling filename:

```
dds/textures/trak5x/base/base_pipe1a_norm.dds   →  "base_pipe1b_norm.dds"
```

They are **symlinks that lost their symlink bit when the pk3 was zipped** (verified by reading the zip: external
attributes are plain `0o600`, so nothing downstream can tell except by inspecting the bytes).
**`shared.pk3` alone carries 974, of which 903 point at a real DDS.** Every one was falling back to the
uncompressed TGA beside it — or to *nothing at all* where no TGA existed.

**Root cause is ours, not upstream's.** `data/maps.lock.json` shows `shared.pk3` comes from
`VortexFPS/VortexMaps` releases — it is our own build output. The runtime alias-following in
`AssetSystem.ResolveImageAlias` is a good safety net for packs already shipped, but the packaging pipeline
should stop producing stubs (preserve the symlink bit, or resolve duplicates at pack time). **Filed as an open
question below.** Note this also puts us *ahead* of DP on this content: DP would read the same 20 bytes, fail,
and fall back exactly as we used to.

**This changes rendering, not just logs**: normal/gloss companions that previously resolved to nothing now
load, so stormkeep VRAM went **2647 → 3139 MB**. That is the content the author aliased and the world capture
renders correctly, but it is a fidelity change and deserves a look across a few maps.

---

## 4. The CPU-vs-GPU speed question — measured

The worry: DP compresses on the GPU driver; we compress on the CPU with `Image.Compress`. Wouldn't that be slow
and hitch?

**It is not.** `AssetSystem.MaybeCompress` is scoped as `tex.compress`; a full stormkeep load with 2 bots,
release export:

| mode | `tex.compress` total, whole map load | wall-clock load (2 runs) |
|---|---|---|
| 0 — off | — | 19.5 s / 19.2 s |
| 1 — S3TC ("Fast") | **70.3 ms** | 18.2 s / 19.3 s |
| 2 — BPTC ("Good") | **64.7 ms** | 18.2 s / 18.2 s |

**~70 ms across an ~18 second load — 0.4%, below the run-to-run noise**, which is why the wall-clock column
shows no consistent ordering at all. And VRAM over the same load: **3139 MB → 956 MB, a 3.3× reduction.**

Why it is so cheap: most textures arrive already compressed from the `dds/` tree and `MaybeCompress` skips them
(`IsCompressed()` → return). Only the TGA/PNG minority compresses — but that minority is where the VRAM was.

BPTC measuring *cheaper* than S3TC is counter-intuitive (BC7 is normally much slower) and is probably routing
differences or noise at this magnitude. **Not investigated** — at 65-70 ms it does not matter, but do not quote
"BPTC is free" as a finding.

**Caveats, stated honestly.** This is one map on a 24-core box. A 4-core machine will pay more, and the load
path is already the place where a slow step is least visible (it is under the loading screen). The number that
would actually hurt is a *lazily* loaded texture compressing mid-match on the main thread — that is one texture,
so single-digit ms, but it has not been measured. The menu warm compresses on the streamer's worker lane and
cannot hitch the menu by construction.

### What this means for "better than DP"

CPU-side compression is not a speed compromise we are accepting for tidiness — measured, it costs nothing that
shows. And it is *already* better than DP on the axes that matter:

- **Deterministic.** Same output on every machine; DP's depends on the GPU vendor's compressor.
- **We choose the format.** DP can only hint fast/nice; we pick S3TC vs BPTC explicitly, and pass
  `CompressSource.Normal` for `_norm` so the compressor weights channels for normals — DP has no equivalent
  and simply defaults to not compressing normals at all.
- **No GPU readback**, so no GLES2 exclusion and no driver-string workarounds.
- **Can be off-thread.** DP compresses inline on the render thread; ours already runs on the streamer lane
  during the menu warm.

---

## 5. Options

| # | Option | Effort | Win | Risk |
|---|---|---|---|---|
| **A** | Per-category `gl_texturecompression_*` cvars with DP defaults | S | Parity; sane per-category behaviour; excludes normals like DP does | Low |
| **B** | Change our default from 0 to something on | S | ~3.3× VRAM for everyone | **Lossy** — needs a visual pass |
| **C** | Fix VortexMaps packaging so stubs stop shipping | S–M | Removes the root cause; smaller packs | Low, but outside this repo |
| **D** | Content-hashed bake cache in the user dir | M | Removes a 70 ms/load cost | **Not worth it — see below** |
| **E** | BC5 pass-through + shader Z reconstruction | M | More VRAM on `_norm` | **Medium-high** — touches world + player lighting |
| **F** | `r_texture_dds_load` / `_save` parity | M | Faithfulness | Low value; we already prefer `dds/` unconditionally |

### On (D), the bake cache — I was wrong

The previous recommendation was to build one. **The measurement retires it.** A bake cache exists to amortise a
compression cost that turns out to be ~70 ms per load, against real costs: disk space, an invalidation scheme,
and a whole new failure surface. Revisit only if (B) lands *and* profiling on a low-core machine shows
compression actually mattering there.

### On (E), BC5 pass-through

The remaining big VRAM item, and the one I would not rush. `LightmapShader` and `PlayerSkinShader` (×2) all do
`texture(normal_tex, uv).rgb * 2.0 - 1.0` and use `.z`; BC5 has no Z. Reconstruction is three shader edits, but
it changes lighting math for *every* normal map including the RGB ones, so it needs a real visual A/B across
several maps and player models. Note upstream ships `gl_texturecompression_normal 0`, i.e. DP declined this
trade too.

---

## 6. Recommendations, ranked

1. **(A) Implement the per-category cvars with DP's defaults**, and route `_norm` to the `_normal` category so
   it defaults OFF like upstream. This makes the setting behave the way a Xonotic player expects, closes 12
   cvars of parity debt, and removes the riskiest part of enabling compression. Small and contained.
2. **(C) Fix the packaging** so `shared.pk3` stops shipping symlink stubs. Keep the runtime alias-following
   regardless — it fixes packs already in the wild and makes us robust to any pack built the same way.
3. **Visual pass on the alias change** before it reaches players: normal/gloss maps that were silently missing
   now render. Verify a handful of maps look right (or better), not different-in-a-bad-way.
4. **(B) Then consider flipping the default on** — but only after (A), so normals are excluded, and only with a
   visual comparison. 3.3× VRAM is a big enough prize to be worth the QA.
   **Update 2026-08-01: recommend NOT flipping it yet.** (A) has landed and the normal-map inversion is fixed,
   but the evidence base for (B) has not improved — it has got worse. The one measurement we have was taken
   through the inverted-normal path, so the "what does compression look like" question is entirely unanswered,
   not partially answered. Flipping the default is a lossy change applied to every player, justified by a
   capture that did not render correctly. It needs a fresh VRAM number and a real visual pass first. See
   §"On flipping the master default" below.
5. **Leave (D) and (F).** (D) is solved by the shipped `dds/` tree plus a 70 ms compression cost; (F) is
   faithfulness with no user-visible benefit.
6. **(E) only if VRAM is still a problem after the above**, with a proper visual A/B.

---

## On flipping the master default (2026-08-01)

**No — keep `gl_texturecompression 0`.** Four reasons, in order of weight:

1. **The measurement that motivates it is invalid.** 3139 → 956 MB was captured with `CompressSource.Normal`
   live, i.e. with every `_norm` inverted. We do not currently know what compression costs *visually*, and the
   VRAM figure will move now that `_norm` compresses via `Generic` (BC7/DXT5 keeps three channels, so it saves
   less than the two-channel BC5 did) and defaults off entirely.
2. **Upstream declines it.** Xonotic ships the master at 0 (`xonotic-client.cfg:788`), so 0 IS the parity
   answer. Turning it on is a deliberate divergence, which needs its own justification rather than inheriting
   the parity argument.
3. **The cache makes it a one-way door in practice.** Nothing keys the texture cache on compression mode and
   nothing evicts, so with `cl_persist_asset_cache 1` the menu warm has already loaded most of the stock set
   before the player can reach the Effects slider — a player who turns it *off* after boot keeps the compressed
   textures for the process lifetime, and vice versa. Shipping it on means shipping the lossy version as the
   one most players can never fully back out of within a session.
4. **The compression still runs on the main thread.** `MaybeCompress` sits in `LoadTextureFromVpath`, which is
   the synchronous `LoadTexture` path, so a default-on setting puts CPU block-compression on the frame thread
   for every cold in-match load. §4's 70 ms figure is a whole *map load* under a loading screen; the
   mid-match lazy-load case is still unmeasured.

**What would change the answer:** re-run the §4 capture on the fixed path, byte-compare a turntable with
compression on vs off, and move `MaybeCompress` to the worker (`PredecodeTexture`) so the setting cannot hitch
the frame thread. If VRAM still falls substantially and the turntable diff is visually acceptable, flipping to
`1` becomes a reasonable proposal — with `_normal` still off, as upstream has it.

## Open questions

- **Why does VortexMaps produce stub files?** Is the pk3 built with a zip tool that drops the symlink bit, or
  are the sources themselves stubs? Determines whether (C) is a one-line packaging flag or a content fix.
- **What is the compression cost on a 4-core machine?** Everything here is a 24-core box; the 70 ms figure is
  the best case for parallel compression.
- **What does a lazily-loaded mid-match texture cost to compress?** Not measured; it is the only path where
  compression could hitch play rather than load.
- **Is the alias change a fidelity *win* on every map**, or does any surface now look worse than the TGA it was
  falling back to?

## Reproduce

```bash
# VRAM + compression cost, release export, one map load
tools\perf-run.ps1 -Label texcomp -Map stormkeep -Bots 2 -Secs 30 -Cvar "gl_texturecompression 1"
# then read the session log's "ms/frame:" line for tex.compress, and "vram NNNNmb" for the plateau
```

```bash
# count the alias stubs in a pack
python - <<'EOF'
import zipfile
z = zipfile.ZipFile('data/maps/shared.pk3')
n = sum(1 for i in z.infolist()
        if i.file_size and i.file_size < 128 and i.filename.lower().endswith('.dds'))
print(n, "stub-sized dds entries")
EOF
```
