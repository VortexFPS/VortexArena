# Crylink mesh and Onslaught generator destruction

The original Crylink projectile uses `models/plasmatrail.mdl`, an MD3 payload with a core quad and a long quad. The pinned `projectiles.shader` uses `autosprite` on the core, `autosprite2` on the streak, and `GL_SRC_ALPHA GL_ONE` for each. Both the ordinary and bouncing variants select that model. Native now binds the same shipped model and multiplies each surface texture alpha by the networked Crylink fade. It keeps the sprite fallback for missing model content. The projectile continues to use `TR_CRYLINKPLASMA` and the prior fix for the extra dynamic light. No asset replacement was needed.

- [Original Crylink from the matching fixture](crylink-original-matched/frames.json)
- [Native before the mesh fix](../crylink-native-fixed/flight-b.webp)
- [Native mesh in flight](crylink-native-mesh/flight.webp), [fading](crylink-native-mesh/fading.webp), and [expired](crylink-native-mesh/expired.webp)

The Onslaught camera follows the original `ons_camSetup` direction search, then uses the existing collision-aware event chase path with the stock 80-unit lift and 400-unit pullback. As in the pinned game, a requested classic chase takes precedence. Both players and spectators following players receive the camera state. The effect timeline also reaches free observers. The state clears on round reset. The pinned data ships the same `ons_ray.md3` used in this port, the shockwave via `electro_combo`, and the fire via `onslaught_generator_gib_explode`. The sequence uses forty pulses with a 50 ms minimum spacing, including two shockwaves, additive rays and the final engine explosion. The pinned effectinfo has no blocks for the two additional named small and final explosions. They are emitted when a mounted catalog supplies them.

The records use the existing two-player runner, a private Debug host, displays 163 and 164, and ports 26819 for Crylink and 26813 for Onslaught. Use `--scenario crylink --engine native` and `--scenario ons --generator-only --engine native` with fresh output directories. The earlier before and original runs remain at the [parent report](../README.md).

The isolated build succeeded. The [focused tests](focused-tests.txt) cover camera search order, pulse boundaries, round-state wire framing and clear, the two Crylink shader deforms and the existing gameplay regressions.

The Crylink retake used the same fixture BSP and external lightmap hashes in both engines. The BSP hash is `f49109966c2672979b462c7ca5eb14b754b43ec64bd5cd9897d71848013c7acf`. The stock and native-shipped copies of both projectile and ray models are byte-identical. Lighting and the distribution of visibility samples still differ across engines.

| Generator | Original | Native before | Native after |
| --- | --- | --- | --- |
| Player's pre-death view | [Red](../ons-original-generator-matched/damaged-two.webp) | [Red](../ons-native-generator/damaged-two.webp) | [Red](ons-native-death-camera/red-before.webp) · [Blue](ons-native-death-camera/blue-before.webp) |
| Camera cut | [Red](../ons-original-generator-matched/dead-camera.webp) | [Red](../ons-native-generator/destroyed.webp) · [Blue](../ons-native-generator/other-view.webp) | [Red](ons-native-death-camera/red-cut.webp) · [Blue](ons-native-death-camera/blue-cut.webp) |
| Sustained explosion | [Blue](../ons-original-generator-matched/other-view.webp) | [Blue](../ons-native-generator/other-view.webp) | [Red](ons-native-death-camera/red-sustained.webp) · [Blue](ons-native-death-camera/blue-sustained.webp) |

The native after-run server recorded RedProbe’s 110 points and 1 generator credit. The two clients converged on the same camera position and yaw in the captured HUD. The native sequence runs one pulse per eligible frame, like the original QC, so slow software render cadence stretches it. The Dummy audio backend logged an audio playback warning and this run establishes visual behavior. Spectator, manual chase and live camera clearing were outside the two-player run. [Native server](ons-native-death-camera/server.txt) · [Frame timestamps](ons-native-death-camera/frames.json)

The successful after-run captured distinct initial and sustained effects from both clients. Two longer-tail retries ended before generator destruction, one during client join and the other before the pad captured. The final pulse is covered by the focused clock test. Its exact visual frame and the bouncing Crylink variant remain unverified in live play.
