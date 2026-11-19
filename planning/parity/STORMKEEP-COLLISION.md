# Stormkeep collision

Native compiled BSPs now use the original DarkPlaces collision profile. It uses the compiled brushes and their bevel planes, DarkPlaces brush points and trace arithmetic, snapped patch triangles with the BSP's LOD groups, and the bounding interval hierarchy. The listen server and client prediction get the same profile from the shared world builder. Set `VORTEX_PATCH_COLLISION=slabs` on both native processes to restore the previous collision. Legacy mode always selects the DarkPlaces profile.

## Six traces on rebuilt Stormkeep

The comparison used all 32 original packs in `/tmp/va-original-maps` and the 31 rebuilt packs in `data/maps`. Every pack matched its respective release lock. All 30 shared BSPs changed bytes between the releases, and the rebuilt release dropped the `_init` stub. The original packs are the upstream prebuilt Xonotic BSPs split for VortexArena. The native `maps-2026.08` packs were generated with the q3map2 pipeline. [Native map lock](../../data/maps.lock.json)

The full DarkPlaces profile returned 4,751 exact and 49 close results against the 4,800 reference traces on the original BSPs. The former slab profile returned 2,725 exact, 111 close and 1,964 differences. Close requires an endpoint distance under 0.01 game units and matching `StartSolid` and `AllSolid` flags. Rebuilt Stormkeep returned 589 exact, five close and six differences across the 600 patch-seeded traces.

The regression crosses the two builds under identical trace settings. Original brushes plus rebuilt patch triangles reproduce the reference for all 600 rows. Rebuilt brushes plus original patch triangles reproduce the same six mismatches. Single-brush traces identify the specific BSP brush records below. Brush indices are zero-based in each BSP's brush lump. Bracketed endpoint values are in Quake coordinates. [Regression](../../tests/VortexArena.Tests/DarkPlacesTraceParityTests.cs) · [Golden trace rows](../../tests/VortexArena.Tests/golden/dp-traces/patch-stormkeep.txt)

| Golden file line | Probe | Original → rebuilt endpoint | Distance | Compiled geometry responsible |
| --- | --- | --- | ---: | --- |
| 99 | Line up | `(-486.245, 612.739, 96.000)` → `(-486.245, 612.739, -48.031)` | 144.031 | The `floor-metal02` brush's underside goes from Z −64 (3737) to Z −48 (3423). The original starts all-solid. The rebuild starts clear and hits the caulk underside. |
| 127 | Line down | `(-423.845, -661.966, -47.969)` → `(-423.845, -661.966, -63.969)` | 16.000 | Original 5154 has a lava-textured top at Z −48. The rebuild lacks that solid at the probe and hits the lower `base-metal03` brush (3719, Z −64). |
| 180 | Wall player box | `(-448.031, -644.719, -186.138)` → `(-480.031, -655.807, -186.803)` | 33.873 | Rebuilt `floor-simple05` brush 4245 clips the box at its caulk face at X −464. Both builds' patch triangles give the original endpoint when traced alone. |
| 204 | Wall player box | `(-420.987, 1216.940, 78.743)` → `(-367.969, 1220.061, 78.067)` | 53.114 | Rebuilt `panel-rs01` brush 3811 clips against its `base-metal01` face at X −384. Both builds' patch triangles give the original endpoint when traced alone. |
| 228 | Wall player box | `(-224.677, -944.031, 49.966)` → `(-239.969, -926.472, 49.609)` | 23.287 | The original caulk split exposes a face at Y −928 (3511) inside the traced region. In the rebuilt brush arrangement, brush 3463 spans that split and the trace clips at the X −256 face of 3464. |
| 370 | Walking player box | `(-1006.690, 1394.788, 285.553)` → `(-1008.031, 1395.656, 285.553)` | 1.597 | Rebuilt caulk brush 3504 introduces the hit at X −992. Both builds have the nearby player-clip brush (5189 and 5223), which contributes solid flags without clipping the endpoint. |

The causal inputs are the compiled brush topology and planes. The separate patch triangle counts are 4,017 original and 3,985 rebuilt. The cross-build test changes which collision geometry the tracer receives while holding the masks, trace extension and clip arithmetic fixed. I also compared the editor map inside Xonotic's original maps pack (Git blob `e8389de88dd25a7f991f3773c0a11c63e9522723`) with the [editor source at the native release tag](https://github.com/VortexFPS/VortexMaps/blob/69e1a5c7d14c94578ebd4aca3c94839581119e3d/sources/maps/stormkeep.map) (blob `3cabc9d1418a4bd9642b9dad6c4b0d8fbf88dbab`). Five rows have corresponding editor geometry differences. The rebuilt source brushes starting at lines 18640, 8121, 13406, 18315/18307 and 17987 reproduce the source face planes of compiled brushes 3423, 4245, 3811, 3463/3464 and 3504 respectively. None has a full face-plane match in the original editor source. Compilers can add axial bevel planes.

Row 127 has a different cause. Original brush 5154 and rebuilt brush 5188 have the same ten planes. Their editor brush blocks are byte-identical (original line 1348, rebuilt line 1317). The compiled original selects `textures/common/caulk` with contents `0x00000001` (solid). The rebuild selects `textures/liquids_lava/lava0` with contents `0x20000008` (translucent lava). The solid-only probe therefore hits the floor below the lava in the rebuild. This establishes the brush-content difference at compile output. Distinguishing compiler version, shader inputs and compiler options responsible for that selection would require rebuilding under controlled variations.

## Recorded native walk

Two windowed Godot 4.6.3 native listen-server sessions ran the same zero-bot DM inputs at a fixed 64 Hz. The playtest copy of rebuilt Stormkeep replaced the player starts with one spawn at `(-348, 1535, 172)`. All 16 non-entity BSP lumps were byte-identical to the shipped map. The camera recorder waits for an active player before applying scripted input and samples the same predicted grounded state used by the HUD. After 32 idle inputs, it sent 32 forward inputs at 0.3 and yaw 90°, followed by 24 idle inputs. There was no jump input. A hull drop with and without brushes confirmed support from the `textures/exx/base-metal03` curved floor along the initial ascent.

| Measurement | Slabs | DarkPlaces |
| --- | ---: | ---: |
| Recorded frames / grounded frames | 88 / 76 | 88 / 76 |
| Settled starting Z | 164.625 | 164.589 |
| Frame 40, position on the ascent | `(-347.676, 1542.684, 168.018)` | `(-347.671, 1542.685, 167.984)` |
| Final position | `(-343.967, 1561.961, 209.027)` | `(-343.163, 1543.953, 168.002)` |
| Largest reconcile correction after forward input began | 0.0044 u | 0.0057 u |

The paths stay within 0.038 u through frame 40. Both walk uphill and regain ground after four airborne frames at the lip. The slab profile then gains about 41 u of elevation over two frames and moves onto the upper surface. DarkPlaces stays grounded below the lip and stops advancing. Final separation is 44.811 u. A repeat with the final default and the environment variable unset stayed within 0.006 u of the explicit DarkPlaces run and had the same grounded frames and final position. Original collision is the default for parity. Players who prefer the old ability to climb this lip can choose the slab profile.

The recorded runs are available for the [slab profile](stormkeep-walk/grounded-slabs.json), [explicit DarkPlaces profile](stormkeep-walk/grounded-darkplaces.json) and [new native default](stormkeep-walk/grounded-default.json). The [setup script](stormkeep-walk/setup-walk.py) reproduces the fixed spawn and [input sequence](stormkeep-walk/walk.json) from the installed map pack. The windowed runs used `--fixed-fps 64 --cvar sv_threaded 0 --camera-trace walk.json <output>` with a private Godot override setting `rendering/driver/threads/thread_model=1` for the OpenGL software renderer.

## Reproduce the trace comparison

```sh
VA_BASE_DIR=/tmp/va-original-maps dotnet test tests/VortexArena.Tests \
  --filter 'FullyQualifiedName~DarkPlacesTraceParityTests&FullyQualifiedName!~What_the_options_cost' \
  --logger 'console;verbosity=detailed'
```

The brush-swap test checks the original and rebuilt BSP hashes before asserting the six line numbers. It uses the rebuilt pack under `data/maps` (or `VA_DATA_DIR/maps`). The golden parity tests read the original maps from `VA_BASE_DIR`.
