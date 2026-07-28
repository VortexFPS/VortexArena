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
