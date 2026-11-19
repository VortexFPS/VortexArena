# Scripted multiplayer objectives

Both engines use two real loopback graphical clients on private X displays and user directories. The fixture starts from the stock Running Man CTF BSP and replaces only its entity lump. It keeps the Big Blue room control point, links it to both generators, moves the blue generator beside the point, and adds named teleport destinations for the actors. For each fixture revision, its original and native packages contain an identical BSP and stock external lightmaps. The per-run manifests fingerprint the exact fixture and assemblies. Each engine supplies its own other assets. The original data checkout was pinned to `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`.

The first successful original Onslaught point pass is in [ons-original](ons-original/frames.json). BlueProbe's first-person frames cover neutral, building, captured, damaged, repaired, and neutral again. RedProbe touches and rebuilds the pad, with capture and destruction recorded by the [original server](ons-original/server.txt). The generator shot sequence was interrupted by a host restart after the second capture. Its timestamps remain in the [action timeline](ons-original/timeline.json).

Run `python3 planning/parity/scripted-objectives/record.py --help` for fixture paths. For example, use `--scenario ons --engine original --port 26812 --display 161 --output /tmp/new-original-ons` with a fresh output directory. For the native arm supply `--engine native --port 26813 --display 163`. Use `--native-project` if the Debug build was made in a private copy. `publish.py` records the exact source frame and capture time for every selected image.

The original repeat completed another capture, point destruction, recapture, and blue-generator destruction. The frame sampler stalled during that run, so its generator result is logged without a corresponding image. [Frames](ons-original-repeat/frames.json) · [Server log](ons-original-repeat/server.txt)

The original Crylink arm parks the firing player at the start of the unobstructed lane. A second client observes from `-288 -208 112`, yaw 0°, pitch 11°. The projectile begins fading at 0.35 s and fades over 1.1 s, with the speed reduced to 100 Quake units per second. The recording contains two bursts. [Frames](crylink-original/frames.json) · [Commands](crylink-original/commands.json)

## Native fixes and checks

- Convert the stock generator team keys into native team IDs as Onslaught map entities load. Previously, the pad could start a build that never received power from a generator on the builder’s team.
- Give the pad and icon separate collision bounds, relink the icon when construction makes it solid, and give generators the solid box used by Xonotic. The former pad extended into the icon and could intercept hits aimed at it.
- Select the second pad mesh on the first powered build tick and restore the neutral pad when the icon is destroyed. Filter capture shields per viewer, including clients following a player, and use the stock additive blend for the generator shield.

The isolated Debug build completed with zero errors. All 25 focused tests passed. The tests cover map team conversion, build and reset, shield audience, generator damage, and Crylink lifetime and link behavior. [Test output](focused-tests.txt)

The [native startup capture](ons-native-initial/frames.json) records both clients after they joined and the shot into the shielded generator. That run stopped during the approach to the pad.

The native Crylink run shows a strong purple light on the floor during flight. The pinned projectile draw and the two `TR_CRYLINKPLASMA` blocks supply no dynamic light. The native Crylink catalog supplied its own moving light, which is removed by the fix. The inner and outer spike settings and both attack intervals are preserved in the input logs. The native fallback sprite also differs from the original `plasmatrail.mdl` core and streak. [Original samples](crylink-original/frames.json) · [Native before-fix samples](crylink-native/frames.json)

A shorter original run captured the generator phases that the full point pass missed. Its frames show the shield rejecting a shot, the exposed intact generator, two damaged models, and the destroyed generator with the end-of-round camera and particle sequence. The server logged the capture, generator destruction and red’s win. Use `--generator-only` to repeat this sequence. [Frames](ons-original-generator/frames.json) · [Server log](ons-original-generator/server.txt)

The fixed native repeat shows the visible Crylink spikes weakening through successive samples and clearing from the lane. The floor retains its baked lighting as the shots pass. The 65 ms observer sampler can record duplicate buffers when software rendering runs slower. The visible sequence establishes a fade and the removal of the extra light. Cross-engine alpha and timing comparisons are limited by the different projectile models and render cadence. [Fixed native frames](crylink-native-fixed/frames.json) · [Server log](crylink-native-fixed/server.txt)

The native full pass recorded construction, a completed capture, damage, automatic repair, destruction back to the neutral pad, and a second capture. The dedicated server recorded red’s score going from 0 to 10, blue’s destroy credit from 0 to 10, and red’s second capture from 10 to 20. Red’s camera also shows the generator shield disappear and the exposed-generator notice. The final volley in this pass yielded no generator destroy credit. The focused test independently sends the real rail trace through a shielded native generator, then repeats against the exposed generator and verifies the kill and round result. [Native full pass](ons-native/frames.json) · [Native server](ons-native/server.txt) · [Focused tests](focused-tests.txt)

The native generator repeat used a pad drop and an 8° downward aim at the generator. It captured the shielded, exposed, damaged and destroyed models. The live server credited RedProbe 110 points, including 100 for the generator kill, and one destroyed generator for red. The native end-of-round view kept each player's camera and showed the wreck, while the original recording cut to the generator and played a sustained particle burst. [Native generator frames](ons-native-generator/frames.json) · [Native generator log](ons-native-generator/server.txt) · [Original generator frames](ons-original-generator/frames.json)

The final original generator repeat used the same fixture BSP and 8° shot angle as the successful native repeat. Both servers credited the generator kill. The death camera and sustained explosions are visible from both original clients. [Matched original frames](ons-original-generator-matched/frames.json) · [Matched original server](ons-original-generator-matched/server.txt)

| Phase | Original Xonotic | Native VortexArena |
| --- | --- | --- |
| Crylink in flight | [Full](crylink-original/full.webp) | [Full](crylink-native-fixed/flight-b.webp) |
| Crylink final sample | [Expired](crylink-original/expired.webp) | [Expired](crylink-native-fixed/expired.webp) |
| Point build | [Building](ons-original/build.webp) | [Building](ons-native/build.webp) |
| Point damage and repair | [Damaged](ons-original/cracked.webp) · [Repaired](ons-original/repaired.webp) | [Damaged](ons-native/cracked.webp) · [Repaired](ons-native/repaired.webp) |
| Point destroyed | [Neutral pad](ons-original/reset.webp) | [Neutral pad](ons-native/reset.webp) |
| Generator shield transition | [Shielded](ons-original-generator-matched/shielded.webp) · [Exposed](ons-original-generator-matched/exposed.webp) | [Shielded](ons-native-generator/shielded.webp) · [Exposed](ons-native-generator/exposed.webp) |
| Generator damage and destruction | [Damaged](ons-original-generator-matched/damaged-two.webp) · [Destroyed](ons-original-generator-matched/dead-camera.webp) | [Damaged](ons-native-generator/damaged-two.webp) · [Destroyed](ons-native-generator/destroyed.webp) |

A follow-up adds the stock Crylink mesh and generator death-camera sequence. The before and original recordings above remain available. [Presentation comparison](presentation/README.md)
