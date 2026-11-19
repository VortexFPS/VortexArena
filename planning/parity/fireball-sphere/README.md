# Fireball transient model

Directory-ordered rows 15–16 cover the stock primary Fireball BFG sphere. In the pinned Xonotic tree at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`, `W_Fireball_Explode` is the only live caller of `modeleffect_spawn`. Both CTF shockwave references are under `#if 0`. The primary blast checks the owner's combined health and armor after direct splash and checks that the projectile did not time out or get shot down before it calls the sphere at `fireball.qc:31`.

## Paired trial

Each engine ran one dedicated loopback server, a graphical shooter and a separate graphical observer. Both mounted the same Stormkeep geometry used for row 14. The staged packages share fixture BSP SHA-256 `179143560afea9b37fe725f9ccdb73c2686f9dab86e45d35ce77883e3dd719d1`. Only the entity lump was replaced. It keeps worldspawn and adds simple spawn and teleport destinations. BlueProbe joined, teleported to `1216 248 16`, faced yaw 90° and pitch 22° for the floor impact, then fired through the normal held attack binding. RedProbe used the fixed camera `1216 145 112`, yaw 90° and pitch 21°. Its native view was pinned by the existing observer-camera option. The stock observer used the server's edict console to set that pose.

Both servers used quarter-speed game time to separate the model stages on software-rendered displays. The native graphical clients explicitly used the same clock scale for this trial because the dedicated server's live `slowmo` does not replicate to their render clock. The fixture used BFG radius 96 and reduced the splash radius to 32 for the eligible impact. A second shot used a 1 Quake unit/s speed, an upward aim and a 1 ms lifetime. A third used a 250-unit splash to damage the owner. The stock `w_prop_interval` was set to 0.1 so the pinned weapon configuration cache picked up the changes between trials. [Stock inputs](stock/inputs.json) · [Native inputs](native/inputs.json)

| Trial | Stock observer and server | Native observer, shooter and server |
| --- | --- | --- |
| Impact | 28 calls at the first statement of `ModelEffect_Draw`. Shooter health and armor stayed at 100 and 0. | Owner health and armor stayed at 100 combined, with `cnt = 0`. Both clients received one sphere. |
| Timeout | Breakpoint hit at `W_Fireball_Think` line 130, which assigns `cnt = 1`. The nearby blast took health from 100 to 40.53. Zero model draws. | `cnt = 1` with health and armor unchanged. Neither client received the sphere. |
| Self-damage | The owner's already reduced health fell from 40.53 to −60.48. Zero model draws. | `cnt = 0` with combined health and armor going from 100 to −0.39. Neither client received the sphere. |

[Stock gate diagnostics](stock/observations.txt) · [Native per-client ages, scales, alpha and authority gates](native/observations.txt) · [Stock frame timestamps](stock/frames.json) · [Native frame timestamps](native/frames.json)

| Presentation sample | Stock Xonotic | Native VortexArena |
| --- | --- | --- |
| Before explosion | [Observer](stock/observer-before.webp) | [Observer](native/observer-before.webp) · [Shooter](native/shooter-before.webp) |
| Growing translucent sphere | [Observer early](stock/observer-early.webp) · [Observer growth](stock/observer-grow.webp) · [Shooter](stock/shooter-impact.webp) | [Shooter](native/shooter-grow.webp) |
| Fading | [Observer](stock/observer-fade.webp) | [Observer](native/observer-fade.webp) · [Shooter](native/shooter-fade.webp) |
| Cleared | [Observer](stock/observer-cleared.webp) | [Observer](native/observer-cleared.webp) · [Shooter](native/shooter-cleared.webp) |

Stock `ModelEffect_Draw` grows the sphere linearly over 300 ms of game time. Alpha stays at 0.20 through 50 ms, then fades over 250 ms, with deletion below 0.003. The native observer sampled scale 0 and alpha 0.20 on receipt, then scale 0.129 and alpha 0.20 at 25.8 ms. At 247.7 ms it sampled scale 1.238 and alpha 0.0418. At 298.7 ms the computed alpha was 0.00106 and the renderer removed it. The pinned visibility threshold is 0.003 at `client/main.qh:158`. On the linear fade the sphere crosses it at 296.25 ms, before alpha reaches zero at 300 ms. The observer’s 298.7 ms removal and the shooter’s later 318.9 ms removal each occurred on the first captured update below that threshold. Both arms used frame 0 of the byte-identical mounted sphere MD3. The stock TGA and native PNG each decode to a single white texel. The native clone uses the existing grid-lit model shader with entity alpha enabled for the sphere, retaining ordinary depth testing. The X11 image sample cadence and render rates differ, and the native outline is faint against the darker native scene. The frames and draw traces establish the material path and lifetime without matching pixels or claiming a frame-exact engine-to-engine duration.

The [before-fix trace](before-fix.txt) caught an observer charging 768 ms of pre-arrival frame time to the new sphere, deleting it on its first update. The fix starts age at zero on receipt's render frame and warms the same sphere shader through the existing offscreen effect pass. Software rendering still skipped intermediate images. The Modern particle backend and stock compatibility client keep their existing code paths. Both servers offer an eligible new connection the sphere while it is live on the server. The native reliable event follows acceptance and expires at 100 ms of map simulation time. [Stock sign-on and controlled native ENet boundary probe](availability.md)

## Reproduce

The scripts use only local binaries and private X displays. Supply the pinned data checkout, SDL binary and compiled stock QC directory, and Godot Mono as needed. The defaults match the local audit setup. Choose separate game ports and display ranges for contemporaneous runs. The recorder defaults to 0.25 game-time scale. Pass `--slowmo 1` for normal-speed playback.

```sh
PYTHONDONTWRITEBYTECODE=1 python3 planning/parity/fireball-sphere/record.py \
  --engine original --output /tmp/fireball-stock --port 30542 --display 247

PYTHONDONTWRITEBYTECODE=1 python3 planning/parity/fireball-sphere/record.py \
  --engine native --output /tmp/fireball-native --port 30378 --display 254
```

For native runtime samples, copy an already restored native project to a disposable directory with `Main.cs`, `Main.tscn`, `project.godot`, `.godot`, `game`, and `src`. Run `python3 planning/parity/fireball-sphere/instrument.py /path/to/copy` before building that copy and pass it to the recorder with `--native-project`. The instrumentation adds console observations only. Each run emits full frames, attack and cvar timestamps, executable and asset hashes, and renderer and dedicated-server logs. The selected images here are unmodified capture files. The manifests preserve each source frame and its timestamp.

The final production host build succeeded. All 18 focused Fireball, weapon-balance and Devastator regression cases passed with the available .NET 10 runtime using major roll-forward for the net8 test assemblies. [Verification output](verification.txt)
