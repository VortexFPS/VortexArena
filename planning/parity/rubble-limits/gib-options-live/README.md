# S1 row 14 gib events

The paired fixture uses pinned Xonotic at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` with the supplied SDL engine, SDL libraries and compiled QC. The native client uses Godot 4.6.3 Mono in Forward+ and .NET 10 rolling forward the net8 build. All sessions use disposable local profiles.

Two real stock clients joined the Stormkeep fixture used for row 13. Alpha aimed from `1216 248 -8` at Beta at `1216 306 -8` and fired one unspread, 300-damage Shotgun pellet with a bound attack key. The native fixture used two negotiated ENet sessions, the same placements and the normal Shotgun fire gate. Its observing renderer used the decoded client effect handler and a controlled collision floor. Diagnostic inserts only touch the disposable project and disable the extra in-process mirror so every recorded gib comes from the recipient stream.

| Client option | Type 1 particle request and model tosses | Type 2 requests | Type 3 tosses |
| --- | --- | --- | --- |
| Default | `bloodshower` at amount 1, 12 models | Two `blood` requests of 200 | 3 |
| `cl_gentle_gibs 1` | One `morphed_damage_dissolve` at 1 | Two `morphed_damage_hit` requests of 200 | 0 |
| `cl_gentle_gibs 2` | One `damage_dissolve` at 1 | Two `damage_hit` requests of 200 | 0 |
| `cl_gentle_gibs 3` | One `happy_damage_dissolve` at 1 | Two `happy_damage_hit` requests of 200 | 0 |
| `cl_particlegibs 1` | One `particlegibs_damage_dissolve` at 1 | Two `particlegibs_damage_hit` requests of 200 | 0 |
| Client `ekg 1` | `bloodshower` at amount 5, 52 models | Two `blood` requests of 1000 | 3 |
| `cl_nogibs 1` | No requests or models | No requests | 0 |

Stock breakpoints established the executed branches and call counts in the byte-identical CSQC. The stock effect names and requested particle counts come from the arguments in the pinned source. Native names and requested counts come from the live particle router. The stock shower loop requests 50 samples at quality 1 with `amount / 50` per request, and native did the same. Live model calls agree with the stock call site inventory. The stock edict dump sampled surviving models separately from the toss call count. [Parsed observations](observations.txt)

The native client received the same six events in every trial, in the order hit, chunk, hit, chunk, chunk, full splash. The live-player callback sends the first hit at the trace impact position with the incoming force, then tests post-armor, post-hook health damage against strict thresholds of 50 and 100. The blocked amount and remaining health can move a hit below a chunk threshold. The corpse callback sends the second hit with the same impact position and force, then tests its uncapped health damage against the same thresholds. The two chunk throws use force multipliers of -0.1 and -0.2. The full splash uses corpse origin plus four vertical units and the corpse velocity. Both hit events in this fixture are capped at 200 before client scaling. The server's forced-corpse callback for server `ekg` or `sv_gentle` is independent of the local client `ekg` multiplier.

Live sound damage uses the two ear positions and a directional split for type 2. The far ear supplies the impact position and force for type 3 and the ensuing corpse callback. Copied bodies retain the source's damage dispatch and their own species. Ordinary type-2 resolves the species blood name, and type-3 uses the same species-aware model toss and existing rubble pool as the full splash. CTS suppresses server emission of all three gib types while the corpse damage state still changes. Stock code references are `qcsrc/server/player.qc:183–340,577–597`, `qcsrc/common/effects/qc/gibs.qc:9–55,210–317` and `qcsrc/common/effects/qc/damageeffects.qc:116–131`.

The pinned client defaults have `cl_gentle`, `cl_gentle_gibs`, `cl_particlegibs`, `cl_nogibs` and `ekg` at zero. Gentle mode bypasses the `cl_nogibs` reduction and suppresses type-3 chunks. The particle-only setting also suppresses type 3, with `cl_nogibs` still applying first. Client EKG multiplies the surviving amount by five. Both native particle backends received the same effect names and requested counts, with their existing catalogs and routing. ADR-0001, ADR-0005, ADR-0011 and ADR-0019 permit the native by-name packet and full-precision impact position and force. The stock packet packs position in four-unit cells and compresses the direction and speed.

The contact sheet shows stock, native Original and native Modern from left to right, in table order. The cameras and scenery differ. The native Original probe counted active and uploaded faithful instances, which can include incidental effects. Its captures showed ordinary gib models, while the dissolve and particle-only frames did not show distinct particle pixels. The Modern captures showed visible particles and may include effects lingering from an earlier trial. Those logs establish option routing and toss behavior. The follow-up below resolves the blank Original captures. Audio, fragment touchdown effects, native visual lifetime and monster callers were outside this fixture. The scoped change reuses the existing fixed-step gib pool and its allocation ceiling. The existing casing ENet fixture still passed same-frame ordering after the gib changes. [Viewport captures](viewport.webp)

## Reproduce

The stock runner defaults to `/tmp/va-reviewed-stock` with SDL libraries under `sdl/usr/lib/x86_64-linux-gnu` and bytecode under `compiled`. Set `STOCK_DIR`, `XONOTIC_PIN` or `GIB_PROBE_PORT` to override paths or ports. Statement 295739 records the ordinary type-1 call, 296024 the replacement type-1 call, 295991 the ordinary type-2 call and 296037 the replacement type-2 call. `TossGib` entry breakpoints distinguish type 1 and type 3 by source call site. The scripts print their disposable output directories.

```sh
fixture=planning/parity/rubble-limits/gib-options-live
xvfb-run -a -s '-screen 0 1600x900x24' bash "$fixture/reproduce-stock.sh"
GIB_PROBE_STOCK_TOSS=1 GIB_PROBE_PORT=29811 xvfb-run -a -s '-screen 0 1600x900x24' \
  bash "$fixture/reproduce-stock.sh"
GIB_PROBE_STOCK_HITS=1 GIB_PROBE_PORT=29826 xvfb-run -a -s '-screen 0 1600x900x24' \
  bash "$fixture/reproduce-stock.sh"
GIB_PROBE_STOCK_CAPTURE=1 GIB_PROBE_PORT=29830 xvfb-run -a -s '-screen 0 1600x900x24' \
  bash "$fixture/reproduce-stock.sh"
```

Stock used `cl_gibs_lifetime 10` for inspection and native used 2.5. Each side used ticrate 0.5 and disabled sloppy collision for observation. The pinned `csprogs.dat` SHA-256 was `cbc9fd9a4e2c7fc8f02f4aab6e5883e549790eb24e71be2bc0b8a0b0fea89999` and `csprogs.lno` was `90cf909b42adfe8c1a864d1a096f21b0ced96ab23c7ceb6a3274cdb35a710ec1`.

The native runner stages an already restored worktree. `GIB_PROBE_RENDERER=gl_compatibility` selects the software OpenGL renderer. `GIB_PROBE_PARTICLE_MODE=2` exercises the Modern backend with collisionless particles on the controlled floor.

```sh
export GODOT=/tmp/vortex-parity-godot/Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64
export DOTNET_ROLL_FORWARD=Major
bash "$fixture/reproduce-native.sh"
GIB_PROBE_PARTICLE_MODE=2 GIB_PROBE_PORT=29829 bash "$fixture/reproduce-native.sh"
python3 "$fixture/summarize.py" \
  /path/to/stock/Alpha.log /path/to/stock-toss/Alpha.log \
  /path/to/stock-hits/Alpha.log /path/to/native/probe.log /path/to/modern/probe.log
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~SharedConstantsGameplayTests|FullyQualifiedName~AttachedDamageTests|FullyQualifiedName~CasingEffectTests|FullyQualifiedName~PlayerLoopParityTests'
```

## Original viewport follow-up

The earlier matrix scene bypassed the graphical client's particle-default registration. It had a client cvar store, but omitted `cl_particles_alpha` and the per-type defaults. The stock alpha is 1. With the defaults omitted, the same perspective gentle shot submitted 645 instances in its first draw, with 639 centers in the camera frustum and the bound atlas cell at 0.918 alpha. Its packed vertex RGB and alpha were zero and hiding the batch changed zero pixels. The probe now registers the same `ParticleCvars.Defaults` as `ClientSettings.ApplyAll` in the normal `Shell` boot path.

The first visual follow-up also reproduced a separate lifetime defect. A clear after the renderer's clock had advanced reset the integration time to zero. The next update could move and fade a newly spawned particle by the full one-second catch-up step. With cvars registered and the old clock behavior, the first gentle frame had 45 instances and the next four had none. Normal and particle-only had no submitted instances in those five frames. The live effect catalog switch clears this same pool before binding a different atlas. `ParticleSim.Clear` now drops the old instances and keeps integration aligned with the current simulation time. The regression verifies both age and position after clearing and immediately spawning a short-lived particle.

The follow-up uses the same native ENet handler and packaged Stormkeep BSP. The stock fixture's entity overlay removes inline brush entities, so the native backdrop omits those models too. Stock server readback gave shooter origin within 0.032 units of `1216 248 -8`, view offset `0 0 35` and pitch/yaw `0 90` for all four trials. The native active camera sits at `1216 248 27`, points along Quake +Y and uses the normal camera's vertical FOV of 83.582° for the stock `fov 100` at 4:3. The native server still uses its controlled collision floor. The stock HUD, weapon viewmodel, lighting, sky and frame times differ.

Visual sampling forwards the received gib events through the normal decoded handler and excludes unrelated packet effects, such as the weapon flash, from the native comparison. For each of the first five draw frames after the shot it reads the renderer buffer and the bound particle atlas. It then pauses the scene and captures the same camera, world, models and existing surface marks with just the faithful particle batch hidden. The count is the number of pixels with a difference greater than four in any RGB byte. The no-gibs control checks for zero changed pixels across all five frames.

| Original option | First perspective frame instances in frustum | Maximum packed RGB / alpha | Atlas center alpha | Changed pixels, first frame | Changed pixels, orthographic first frame |
| --- | ---: | --- | ---: | ---: | ---: |
| Normal | 254 | 0.996 / 1.000 | 1.000 | 241,469 | 71,437 |
| `cl_gentle_gibs 2` | 642 | 0.971 / 0.975 | 0.918 | 88,073 | 29,035 |
| `cl_particlegibs 1` | 426 | 0.932 / 1.000 | 1.000 | 97,351 | 39,297 |
| `cl_nogibs 1` | 0 | 0 / 0 | 0 | 0 | 0 |

Both camera projections rendered all three requested Original variants once the defaults and clock were corrected. The visual sheet groups the stock reference, native Original shown, and native Original suppressed, from left to right. Rows are Normal, gentle, particle-only and no-gibs. The native images use the first sampled frame. The stock column uses the second burst sample for gentle and particle-only, and the post-obituary frame for the other two cases. The frames are independently timed. The native Normal burst covers more of its sampled viewport, and the sheet establishes visible effects and suppression without measuring pixel parity across engines. [Visual captures](visual-viewport.webp) · [Camera and readback logs](visual-observations.txt)

```sh
GIB_PROBE_STOCK_VISUAL=1 GIB_PROBE_PORT=29845 xvfb-run -a -s '-screen 0 1600x900x24' \
  bash "$fixture/reproduce-stock.sh"
GIB_PROBE_VISUAL=1 GIB_PROBE_PORT=29841 bash "$fixture/reproduce-native.sh"
GIB_PROBE_VISUAL=1 GIB_PROBE_ORTHOGRAPHIC=1 GIB_PROBE_PORT=29844 bash "$fixture/reproduce-native.sh"
GIB_PROBE_VISUAL=1 GIB_PROBE_SKIP_DEFAULTS=1 GIB_PROBE_PORT=29843 bash "$fixture/reproduce-native.sh"
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ParticleDirectSpawnTests|FullyQualifiedName~ParticleDarkPlacesTests|FullyQualifiedName~ParticleSimRunnerTests|FullyQualifiedName~SharedConstantsGameplayTests'
```
