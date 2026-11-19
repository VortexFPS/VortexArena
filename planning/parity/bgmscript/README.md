# Map music ADSR verification

## Pinned reachability and shipped content

The Xonotic data checkout used for this review is `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. In its QC, `client/main.qc:917–923` dispatches linked entity updates through `m_read`. The model and particle handlers load the lane and register their draw callbacks at `common/mapobjects/models.qc:374–411` and `common/mapobjects/func/pointparticles.qc:307–338`. The intervening `return = true` statements assign the eventual return value. GMQCC emits an assignment and continues to parse the statements that follow, with `-freturn-assignments` enabled by `qcsrc/tools/qcc.sh:86`. [GMQCC return-assignment parser](https://github.com/graphitemaster/gmqcc/blob/master/parser.cpp#L2608-L2663)

The view calls `TargetMusic_Advance` before the `g_drawables` traversal at `client/view.qc:1756,1841`. The selected sound channel or CD track supplies `bgmtime` at `common/mapobjects/target/music.qc:213`. The per-entity envelope is evaluated by `client/bgmscript.qc:173–229`. The pinned QC was traced by source and compiler semantics. The runtime runs below are native VortexArena.

A scan of the installed map packs found the script in `data/maps/courtfun.pk3`. Its BSP entity lump has 36 scripted `func_pointparticles` entities and zero scripted models. The `.mapinfo` selects track 9, and the shipped `cdtracks.cfg` maps slot 9 to `sixtyfour`. Model callback behavior is authored in the QC and exercised separately with the local fixture.

## Native verification

The logs are filtered excerpts from native listen sessions using Godot 4.6.3 Mono, GL Compatibility, and the Dummy audio device. Temporary instrumentation recorded the per-frame presentation inputs and resulting poses or emission counts, then was removed from the source.

- [Courtfun](courtfun.log) bound all 36 emitters and used the native DarkPlaces mixer. The continuous steam emitter sampled amplitude 0.949 and emitted 3 times in the measured frame. The electric sparks and smoking emitters fired 2-emission bursts on their respective note-on events. Two pre-playback rows also record the QC continuous fallback for an impulse emitter while the clock is unavailable.
- [Wall fixture with music](fixture-on.log) used the Godot audio-node backend. At 1.622 seconds the wall's render origin moved from Z=0 to Z=72 and its requested alpha was 0.350. The alias model rendered with Y yaw 45°. The recorded server origins and angles stayed at their base poses and the wall stayed solid.
- [Muted wall fixture](fixture-muted.log) had an unavailable clock, opaque render alpha, and unmodified poses. The wall fixture probes record renderer state. The scripted CLI screenshot did not produce an image in those runs.

The fixture builder copies existing Stormkeep geometry into a temporary pack and adds synthetic entity keys and lane timing. It ships no copied BSP or new music asset. From the checkout, set `GODOT` to the Godot Mono executable and run:

```bash
fixture=$(mktemp -d)
mkdir "$fixture/maps"
for item in "$PWD"/data/*.pk3dir; do ln -s "$item" "$fixture/"; done
for item in "$PWD"/data/maps/*.pk3; do ln -s "$item" "$fixture/maps/"; done
python3 planning/parity/bgmscript/make-wall-fixture.py data "$fixture/maps/bgm-wall-fixture.pk3"

xvfb-run -a "$GODOT" --path "$PWD" --rendering-method gl_compatibility \
  --render-thread safe --audio-driver Dummy --host courtfun --port 27741 \
  --gametype ctf --no-save-config --cvar bot_number 0 --cvar snd_darkplaces 1 \
  --cvar bgmvolume 1 --cvar r_pvs_cull_emitters 0 --cvar cl_precache_all_weapons 0

xvfb-run -a "$GODOT" --path "$PWD" --data "$fixture" --rendering-method gl_compatibility \
  --render-thread safe --audio-driver Dummy --host bgm-wall-fixture --port 27734 \
  --gametype dm --no-save-config --cvar bot_number 0 --cvar snd_darkplaces 0 \
  --cvar bgmvolume 1 --observe "-224 -12 12" --look-at "-224 -124 -60"
```

Set `bgmvolume 0` for the muted fixture. The runtime trace used PVS culling disabled on Courtfun to observe all three lanes from any viewpoint.

[Build and test output](verification.log) records the successful host build and 48 passing focused regressions. The test runner used the installed .NET 10 runtime via `DOTNET_ROLL_FORWARD=Major`.

## Ordinary point particles

The pinned `func_pointparticles` spawnfunc sets `this.reset = generic_netlinked_reset` and calls it once on spawn. Targeted sources start active only with `START_ENABLED`. Untargeted emitters start active. Native `PointParticles.Reset` now uses the same rule at spawn and at the post-round reset. `MusicSystemTests.LivePath_PointParticlesRearmWhenTheNextRoundBegins` boots registered `func_pointparticles` and `func_sparks` entities in Clan Arena, toggles them in a live round and advances `GameWorld.Frame` through the next-round re-arm. The test also checks the authored noise path, volume and attenuation, default attenuation and the global attenuation sentinel.

The pinned client's `Draw_PointParticles` plays the raw sample at the accepted emission point on `CH_AMBIENT (-9)` using `VOL_BASE (0.7) × volume` and the stored attenuation. In native local matches `MapParticleEmitters` passes the final point after any `movedir` trace to the same client audio path as other spatial one-shots. It uses the stacking ambient channel and no network entity ID, so the sound stays at the emitted point. The server sets and resets emitter state and does not send a sound event.

An audit of the installed BSP entity lumps found 63 point-particle/spark sources. Glow Plant's unrotated brush `*20` is the only one with `.noise`. Its sample is in `data/maps/shared.pk3`. The [Glow Plant live trace](glowplant-emitter.log) records successful mixer allocations on channel -9 for `sound/machines/spark.ogg` at several distinct brush positions, all with volume 0.7 and attenuation 0.5. The run used Godot 4.6.3 Mono with GL Compatibility and Dummy audio, the native DarkPlaces mixer, and an observer near the brush. Temporary instrumentation at the mixer submission recorded the resolved sample, world position, and returned channel slot and was removed. [Follow-up build and test output](emitter-verification.log)

```bash
xvfb-run -a "$GODOT" --path "$PWD" --rendering-method gl_compatibility \
  --render-thread safe --audio-driver Dummy --host glowplant --port 27751 \
  --gametype dm --no-save-config --cvar bot_number 0 --cvar snd_darkplaces 1 \
  --cvar bgmvolume 0 --cvar r_pvs_cull_emitters 0 --cvar cl_precache_all_weapons 0 \
  --observe "302 -370 334" --look-at "300 -315 325"
```

## Scope

This port attaches to the native listen match's map spawn and presentation pathways. The typed authored settings and envelope logic are Godot-free. Geometry extraction applies only to scripted inline submodels that pass map filtering, and the rendered offset stays off the authoritative entity. The native wire protocol and the legacy QC VM are outside this change. Standalone native remote clients and native demos lack these live entity bindings. The renderer applies signed authored `lip` and float ADSR values before native material clamping, which avoids inheriting QC's byte quantization. Distance fade, the wall's QC PVS mask, special portal integration, and model LOD belong to the broader wall renderer.

Standalone native clients already load the BSP and resolve the default CD track. Native generic snapshots skip inline models and model-less emitters. Remote parity requires a stable map-entity identity, initial emitter parameters, an authoritative active/reset and origin update path, and removal on map or relevance changes. Scripted walls also need remote pose and inactive state, and target/trigger music selection requires remote music-source state. Reading the BSP keys locally supplies the authored definitions alone.
