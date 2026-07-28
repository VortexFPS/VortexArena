# Editor baked lighting vs q3map2 — gap analysis

**Date:** 2026-07-28 · **Branch:** `feature/map-editor` · **Reference map:** stormkeep

Everything below is read out of the actual toolchain on this machine, not recalled from documentation:
q3map2 source at `Base/netradiant/tools/quake3/q3map2/`, Xonotic's compile driver at
`Base/misc/tools/xonotic-map-compiler`, and stormkeep's own map/shader/options files.

---

## 1. What stormkeep was actually compiled with

`xonotic-map-compiler` line 28 — the light stage every Xonotic map gets:

```
-lightmapsize 1024 -lightmapsearchpower 4 -fastallocate -deluxe -patchshadows
-samples 4 -randomsamples -bounce 8 -fastbounce -bouncegrid -nobouncestore
-dirty -dirtdepth 64 -dirtscale 0.8 -fill
```

BSP stage (line 22): `-meta -maxarea -samplesize 8 -mv 1000000 -mi 6000000`
`stormkeep.map.options` adds `-sRGB`, which the driver expands to `-sRGBtex -sRGBcolor -sRGBlight`.
The game profile is `-game xonotic` → `game_xonotic.h`: lightmap gamma 1.0, exposure 0, compensate 1.0,
**sRGB for lightmap + texture + colour**, **patch casting enabled**, **compile deluxemaps enabled**,
half-lambert angle attenuation **disabled** (so pure N·L).

Global photon scales (`q3map2.h`): `pointScale 7500`, `areaScale 0.25`, `bounceScale 0.25`,
`formFactorValueScale 3.0`. Dirt defaults `dirtDepth 128 / dirtScale 1.0 / dirtGain 1.0`, overridden
above to depth 64, scale 0.8.

Map-side facts:

- **119 `light` entities**, all `_color 0.612675 0.859144 1.000000` (a cool blue-white), `light 20` or `40`.
- **No sky brushes at all** (`grep -c "/sky" stormkeep.map` → 0): the reference has **no sun and no
  skylight**. Every photon in that map comes from the 119 point lights and the emissive panels.
- `map_stormkeep.shader`: `q3map_nonplanar`, **`q3map_shadeAngle 150`**.
- The strips are `exx/*` shaders: `q3map_surfacelight 2500` (and 625 variants), `q3map_bounceScale 0.75`.

The exact distance law, `light.c:1009`:

```c
add = ( light->photons / ( dist * dist ) ) * angle;   /* photons = intensity * 7500 */
```

---

## 2. What we do today

Vertex-lightmap bake (`EditorLightBake`): direct N·L × `1/(1 + d²/128²)` windowed to a range of
`110·√intensity`, brush-only ray occlusion, 4-ray penumbra for area sources, 12-ray dirt at depth 64,
8-bounce emitter-level radiosity tinted by each shader's average albedo. Result is stored as
`sqrt(v/48)` in the mesh COLOR channel and expanded in `EditorWorldShader`, which puts it in EMISSION.
Luxel spacing 48 units. ~44M rays, ~9 s.

---

## 3. Gaps, ranked by how much they cost us visually

### 3.1 No deluxemap — no light *direction* anywhere `[biggest]`

q3map2 compiles deluxemaps for Xonotic by default, and stormkeep has them. A deluxemap stores, per luxel,
the **dominant incoming light direction**; the runtime does a per-pixel N·L against the *normal map*. That
is why the reference brick shows relief and per-brick shading under purely baked light.

We store scalar irradiance only, and we put it in EMISSION — a channel that by construction ignores
normals entirely. Every pixel of a face gets the same baked value, so no amount of correct intensity will
produce surface relief. **This is the main remaining source of "flat".**

Fix: accumulate a direction vector alongside intensity (Σ Lᵢ·wᵢ, normalised), store it in a second vertex
attribute (`CUSTOM0`), and in the shader modulate the baked term by `dot(normal_from_normalmap, dir)`.
Real work, but it is the difference between lit-looking and flat-looking.

### 3.2 Fabricated sun on a map that has none `[bug, cheap]`

`EditorLighting.BuildSun` falls back to a default sun (215°, 45°, warm, energy 0.6) when no sky shader is
found. stormkeep has **no sky**, so we add a warm directional wash and a shadow pattern the reference
does not have — measured at 8.89 mean on its own. It also fights the map's cool blue fixtures, which is
why our frame reads warm/brown where the reference reads blue-grey. Only build a sun when the map defines
one; keep a cvar to force one for maps under construction.

### 3.3 Wrong distance law `[cheap, changes every gradient]`

Theirs is `photons/d²` with `photons = intensity × 7500` and a `falloffTolerance` cutoff. Ours is
`1/(1 + d²/128²)` windowed to `110·√intensity`. Ours deliberately saturates near the source and dies early
— so pools are the wrong size *and* the wrong shape, independent of any scale factor. Port the real law,
including `_anglescale` (`light.c:339`), and cut off on a photon threshold instead of a radius.

### 3.4 Surface lights are points, not areas `[medium]`

q3map2 turns a `q3map_surfacelight` face into an emitter integrated over its **winding** with a form
factor (`formFactorValueScale 3.0`, `areaScale 0.25`). We collapse strips into clustered point lights at
320 units, capped at 224. A 256-unit strip becomes one point rather than a line of light, so it pools
round instead of long. Emit from the winding, subdivided; the bake has no per-cell light limit to respect.

### 3.5 No phong smoothing (`q3map_shadeAngle 150`) `[medium]`

Stormkeep's main shader smooths lightmap normals across every face pair up to 150° — nearly everything.
We bake with flat face normals, so lighting breaks at every facet the reference blends through. Smooth
bake normals across shared edges within the shade angle.

### 3.6 Patches and alpha-tested surfaces cast no shadows `[medium]`

`-patchshadows` is on and the Xonotic profile enables patch casting. `EditorShadowTrace` tests brushes
only, so every curved surface and every grate is transparent to light.

### 3.7 Resolution: 48-unit vertices vs 8-unit texels `[structural]`

`-samplesize 8` with `-samples 4 -randomsamples` (4 jittered subsamples per luxel) against our single
sample per vertex at 48 units — roughly 36× their spatial density, interpolated across large triangles
instead of across a texture. Shadow edges we simply cannot represent. Either drop the luxel size (cost is
quadratic; now tolerable since bakes are on-demand) or go to a real UV2 lightmap atlas.

### 3.8 No sRGB discipline `[cheap]`

They decode textures and `_color` from sRGB, light in linear, and store the lightmap as sRGB. We use
`_color` raw, so the map's `0.61 0.86 1.00` blue is applied with the wrong curve — hues and mid-tones
drift. Linearise light colours before use.

### 3.9 No `-fill` `[cheap]`

q3map2 fills black/unmapped luxels from neighbours. A vertex of ours that lands inside solid geometry goes
black and smears a dark wedge across its triangle. A neighbour-median pass over outliers would remove a
whole class of artifact.

### 3.10 No light grid for dynamic models `[feature]`

q3map2 builds lump 15 (a 64×64×128 grid of ambient + directional + direction) — the mechanism that lights
players, weapons and items. Our editor world lights static geometry only. Note the synergy: the retained
bake added in this change **is already a 64-unit world grid of baked light**, so it is most of a light grid
already.

### 3.11 Bounce fidelity `[minor]`

Theirs bounces per luxel with per-shader `q3map_bounceScale` (0.75 on the exx panels) and `bounceScale
0.25` globally, plus `-bouncegrid`. Ours bounces per 256-unit cell with one global albedo constant.
Honouring per-shader `q3map_bounceScale` is nearly free.

---

## 4. Recommended order

1. **3.2 sun** and **3.3 distance law** — hours, and they change every gradient in the map toward correct.
2. **3.8 sRGB** and **3.9 fill** — cheap, remove whole artifact classes.
3. **3.1 deluxemaps** — the big one for perceived flatness; needs a vertex attribute and a shader pass.
4. **3.4 area emission** and **3.5 phong** — medium, both visible on this map specifically.
5. **3.6 patch/alpha shadows** — correctness for curved geometry.
6. **3.7 resolution** — decide vertex-vs-atlas; everything above is worth more per hour than this.

---

## 5. Addendum — 2026-07-28, after implementing §3

Closed since the first pass: the photon model, `q3map_skylight`, sun deviance/samples, sRGB colours,
phong (`q3map_shadeAngle`), `-patchshadows`, `-fill`, dirtmapping, deluxemaps, and surface-light colour
(q3map2 takes it from the light image's average, colour-normalised — shaders.c:811 — not white).

**Still open, measured rather than assumed.** Our frame reads warmer than the reference: r:b of 1.97
against 1.32 at the same camera and crop. Isolation runs, each one leg of an A/B:

| leg | mean | r:b |
|---|---|---|
| q3map2 reference | 24.54 | **1.32** |
| ours, defaults | 25.78 | 1.97 |
| surface lights off | 14.29 | 1.92 |
| bounce off | 19.00 | 1.96 |
| luxels 24u instead of 48u | 21.43 | 1.94 |
| ambient 0 | 27.78 | 1.96 |
| **sun off** | 20.23 | **1.70** |

So it is not the emitters, not the bounce, not the ambient floor, and not luxel density — every one of those
leaves the cast intact. Only killing the sun moves it, and only part way. The map's 119 entity lights are
blue (`_color 0.61 0.86 1.00`); in the compiled map they clearly dominate the walls, and in ours they do
not. **The lead to chase is the fixture-to-sun ratio**, not the fixtures' colour.

Note the trap that cost two false conclusions here: `cl_editor_sun_scale` had silently stopped applying once
the sun moved into the bake (it only ever reached the real-time DirectionalLight3D), so the first "sun off"
leg was not a sun-off leg at all and read as "the sun contributes nothing". Now wired.

**Remaining structural gaps**, unchanged in kind: `-samples 4 -randomsamples` supersampling (we take one
sample per vertex), `-samplesize 8` texels against our 24-48 unit vertices, area lights integrated over their
winding with a form factor rather than as points, backsplash, alpha-tested shadow casters, and the light grid
for dynamic models.

---

## 6. Addendum — 2026-07-28, the shadow-tracer audit

Prompted by a reported regression: curved surfaces reading far too dark, appearing around the patch-geometry
work rather than around the colour calibration. That framing was correct, and the cause was not any of the
three things previously suspected.

### 6.1 The measurement

One camera on stormkeep's curved pillar (`--observe "819 878 210 46 -5"`), 1600x900, identical crop over the
brickwork band only (x 620-980, y 400-600 — excludes the strip fixtures and the side walls), every leg baked
from `--fresh-cvars` so no saved edit leaks in:

| leg | mean | p10 | p50 | p90 | r:b |
|---|---|---|---|---|---|
| **q3map2 reference (compiled BSP)** | **29.57** | 16.86 | 30.29 | 41.08 | **1.06** |
| ours, defaults | 14.63 | 4.57 | 13.06 | 26.99 | 1.65 |
| ours, `cl_editor_bake_dirt 0` | 25.88 | 14.99 | 26.62 | 35.48 | 1.47 |
| ours, `cl_editor_bake_phong 0` | 14.62 | 4.57 | 13.06 | 26.98 | 1.65 |
| ours, `cl_editor_bake_bounces 0` | 9.31 | 1.21 | 7.71 | 19.34 | 1.87 |
| **ours, `cl_editor_patch_shadows 0`** | **30.16** | 17.77 | 30.91 | 41.41 | 1.53 |

Read the last row against the first: with patch occluders removed the whole luminance DISTRIBUTION matches
the compiled map — not just the mean, but p10 through p90. So the pillar was never lit wrongly. It was
**shadowed by itself**, losing half its light, and everything else about it was already right.

Two secondary readings fall out of the same table. Phong is worth exactly 0.01 here, so it was never a
suspect. And with brightness matched, the r:b column isolates the outstanding colour question cleanly:
1.53 against 1.06, structure right and hue wrong, which is §5's open item and nothing to do with patches.

### 6.2 Why it happened when it did

`-patchshadows` landed with §3.6 and made every tessellated patch triangle an occluder. Patches then
occluded themselves, but the sample offset was 2 units at the time and hid most of it. Dropping that offset
to 0.5 — the fix for a bright band along patch seams — removed the accidental protection, and the
self-occlusion became severe. Each change was defensible alone; the pair was not, and nothing in the build
or the test suite could see it.

### 6.3 Where we diverge from q3map2's tracer

Read out of `light_trace.c`, `light_ydnar.c` and `lightmaps_ydnar.c`, not from documentation.

**a. Occluder representation.** q3map2 traces a patch as its triangles — zero thickness, Moller-Trumbore
(`TraceTriangle`, light_trace.c:1393+). We need a convex volume for the slab clip, so ours were prisms 2
units thick. A curved surface is lit mostly by rays that skim along it, and a 2-unit slab intercepts a large
share of them. This is the dominant term.

**b. Sample offset.** `DEFAULT_LIGHTMAP_SAMPLE_OFFSET` is 1.0 (q3map2.h:272), per-shader overridable as
`_lightmapSampleOffset`. Ours was 0.5. Halving the clearance while the occluder is 20x thicker than the
reference's compounds a.

**c. Self-shadow exemption — we have no equivalent.** q3map2 gives each trace surface a `surfaceNum`, gives
each luxel the list of surfaces it belongs to (`trace->surfaces`), and refuses any hit within
`SELF_SHADOW_EPSILON` (0.5) that belongs to the luxel's own surface (light_trace.c:1483-1491). It also
rejects hits closer than `trace->inhibitRadius`. Our occluders carry no surface identity at all, so a sample
cannot tell its own geometry from anything else's.

**d. Invalid samples: nudge, don't condemn.** `MapSingleLuxel` (light_ydnar.c:462+) tries the sample point;
if it lands in solid it walks a table of 8 offsets of ±0.5 luxel in the lightmap's own tangent vectors, then
falls back to the drawvert origin pushed along its normal, and only then marks the luxel `CLUSTER_OCCLUDED`.
We test once and condemn. Our own comment records the cost: the buried test took the fill pass from ~1k
candidates to ~200k.

**e. Repair is local and orientation-aware, ours is neither.** q3map2 fills an occluded luxel from its
immediate 3x3 neighbours *within the same surface's lightmap* (`FilterRawLightmap`, light_ydnar.c:2700+),
and where it does blend across surfaces (`StitchSurfaceLightmaps`) it requires `dot(n1, n2) >= 0.5` and
positions within half a sample size. `FillBlackSamples` averages any lit sample in a 3x3x3 grid of 96-unit
cells — up to 288 units away, across surfaces, with **no normal test**. A condemned patch therefore takes
its colour from whatever faces surround it, which is precisely a smooth gradient in the wrong direction.

**f. Dirtmapping.** q3map2 fires 48 vectors — 16 azimuth steps x 3 elevation rings over an 88° cone — plus
one along the normal, and each hit contributes `1 - distance/dirtDepth`, so a far hit barely counts
(`SetupDirt`/`DirtForSample`, light_ydnar.c:1433+). Ours fires 12 Fibonacci rays and counts each hit as a
full occlusion regardless of distance. Two consequences: 4x coarser, and our lowest ray sits 2.4° above the
surface where q3map2's lowest is 16.7°, so we fire grazing rays that q3map2 never fires — the rays most
likely to clip a curved surface's own neighbouring facets.

**g. `-fill` is not what we implemented.** q3map2's `-fill` fills *unused atlas pixels* to improve JPEG
compression (`FillOutLightmap`, lightmaps_ydnar.c:2372; help.c:232). The luxel repair is (d) and (e) above.
Our `FillBlackSamples` is documented as "-fill" and is not.

**h. Phong scope.** q3map2 smooths normals only within meta surfaces sharing a shader, and patches are not
meta surfaces — they keep their exact Bezier normals. `SmoothShadingNormals` blends globally across
materials, including patch-to-brush junctions. Measured at 0.01 here, so this is a latent design divergence
rather than an active defect, but it is the mechanism behind the earlier "bright line along the patch edge".

**i. Still open from §5**, unchanged: `-samples 4 -randomsamples` supersampling, 8-unit texels vs our
24-48-unit vertices, area lights integrated over their winding, alpha-tested shadow casters, the light grid.

### 6.4 What was done, and what it measured

**a + b, done.** Prism thickness 2.0 -> 0.1, sample offset 0.5 -> 1.0, both as cvars
(`cl_editor_patch_thickness`, `cl_editor_sample_offset`). Attribution, same crop and camera:

| leg | mean | p10 | p50 | p90 |
|---|---|---|---|---|
| before | 14.63 | 4.57 | 13.06 | 26.99 |
| offset alone (2.0 / 1.0) | 19.40 | 6.21 | 15.49 | 27.14 |
| **thickness alone (0.1 / 0.5)** | **29.45** | 17.20 | 30.20 | 40.62 |
| both | 29.47 | 17.21 | 30.20 | 40.62 |
| **q3map2 reference** | **29.57** | 16.86 | 30.29 | 41.08 |

Thickness was the whole of it; the offset is worth 0.02 and is kept only because it is q3map2's own value.
Final state after the perf work below: pillar **29.97** vs 29.57, window ceiling **21.83** vs 20.57.

**Two defects found while verifying, both pre-existing:**

- The luxel clipper fans each grid piece independently, so every luxel position was captured about six times
  and each copy traced against every light — to produce a value the position-keyed cache can only hold once.
  Deduping the capture: 1,849,368 -> 300,041 samples, 163.6M -> 27.1M rays, **95 s -> 14 s**.
- Which exposed the second: bounce emitters were proportional to the **sample count** in a cell rather than
  to lit **area**, so `cl_editor_bake_luxel` had silently been a brightness control, and removing duplicates
  dropped all indirect light by the same 6x (a uniform x0.70 across the frame — the signature of a gain bug,
  not a transport one). Now `sum x SampleSpacing^2 x 3.21e-6`, invariant to sample density. q3map2 scales
  radiosity by area throughout (`light->photons = value * area * areaScale`, light_bounce.c:584).

**Still to do, in order:**

1. **f** — q3map2's exact dirt distribution and distance weighting. Bounded cost, removes a whole class of
   over-darkening on curved geometry, and our grazing rays are a patch-specific hazard.
2. **d + e** — nudge before condemning, and give the fill a normal test and a smaller radius. Still ~34k
   samples repainted from up to 288 units away with no orientation test.
3. **c** — surface identity through the occluder index and the sample set. The most invasive, and only worth
   doing if 1-2 leave a residual.
4. The colour cast, which is now cleanly separated from brightness: at the window ceiling q3map2 reads
   R18.8 G21.0 B21.5 against our R26.9 G20.7 B17.8 — green matches, red is ~43% over, blue ~17% short. That
   is the fixture-to-sun ratio of section 5, not a patch or a bounce problem.
