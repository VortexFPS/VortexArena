# Attached weapon damage effects

The reference was the clean checkout at `/tmp/vortex-parity-base`, commit `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The pinned `server/weapons/tracing.qc` sends hit positions and species for direct traces. `server/damage.qc` sends one blast description when the attack permits it. `common/effects/qc/damageeffects.qc` resolves `damage_<weapon>`, picks the nearest eligible tag, and emits from that tag on each think. Normal death can keep the attachment. A missing model or a new life ends it.

The native server already resolves direct and splash victims through `DamageSystem.Apply`. It now sends the weapon ID, target ID, target-relative hit offset, and bounded damage to recipients whose snapshot contains the target. The observation of dedicated-server snapshots with a model path and index zero is recorded in the native trace. Bone resolution uses the client's posed Godot skeleton. Mode 2 also permits a single effect at an unrigged model origin. All modes use the catalog selected by the existing particle settings. Original retains the pinned definitions and atlas. Mixed and Modern retain the authored assets and routing.

The existing native event bundle supplies the record ID and length. The previous decoder consumes the record span and leaves the effect name empty for an unknown ID, so the old renderer drops it. A spawn bit in the existing 16-bit snapshot flags persists for the whole life, including across lost initial spawn snapshots. Welcome messages, the handshake version, and wire field widths retain their existing layout.

## Stock play

DarkPlaces ran the pinned compiled server and client QC in a private two-player Stormkeep match. The [entity fixture](stormkeep-probe.ent) retains the BSP geometry and replaces the entity lump with the worldspawn, player spawns, and two named teleport destinations. The stock loader received the text with a trailing NUL. Both players joined, took opposite positions, and used a Devastator with core damage 45 and force zero. The shooter viewed the victim from `1040 240 24` facing yaw 135. Both attachment lifetime bounds were set to six seconds.

- [Before the shot](stock-before.png) shows the victim in front of the shooter.
- [After the shot](stock-attached.png) shows a red particle at shoulder height after the initial explosion. The match clock advanced from 14:18 to 14:13 across these frames, including the capture and input delays.
- [Stock event excerpts](stock-events.txt) record the two live clients. [Launch and input script](stock-probe.sh) records the process arguments and held inputs used for the capture.

The stock dedicated binary SHA-256 was `35b40b973d7e83d75bab026b2707bf80660840421b1e12fcfe0e9c85cb9633ea`. The SDL client binary SHA-256 was `79e38b239b8dc5d5ee1ac8ada8245a51381ce27277dc165645fea0e69fed4312`. The three pinned QC program SHA-256 values were:

| Program | SHA-256 |
| --- | --- |
| `progs.dat` | `c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7` |
| `csprogs.dat` | `cbc9fd9a4e2c7fc8f02f4aab6e5883e549790eb24e71be2bc0b8a0b0fea89999` |
| `menu.dat` | `4d502424af24f90c36c8a17a034ded536571a0d353a4192d8b332e8cc8b2d771` |

The match used the installed Stormkeep archive with SHA-256 `8d08dc53313b34c32ce669f38e630d264a0cc0698ef703235f759d6b9d6f7dd2`. The NUL-terminated entity overlay SHA-256 was `a98cd70003a82a077bb7821060897f4cf78a47b865cd36b905003046e1f504fe`.

## Native play

A headless dedicated server and two separate Godot 4.6.3 Mono clients connected over loopback ENet. They used the same Stormkeep fixture, damage and force settings. Both clients used Original particles on OpenGL Compatibility with Mesa llvmpipe. Native captures set both attachment lifetime bounds to 12 seconds for the camera run and 20 seconds for the follow run. The extra time made it possible to change cameras while an attachment was active.

- The server sent damage 44 for the victim and 26 for the shooter's own splash to both peers at server time 49.83. Both peers resolved the rig. The victim's client selected `hand_R` for the victim and `spine3` for the shooter. The logged world positions vary as the model poses. [Before firing](native-before.png) · [Camera-run trace](native-camera-events.txt)
- The victim suppressed their own attachment particles in [first person](native-self-first-person.png), emitted from that same attachment in [chase](native-self-chase.png), and stopped at [expiry](native-expired.png). Ordinary rocket explosion particles still appear in first person.
- On the second shot the [shooter had damage effects disabled](native-disabled.png). The shooter received both records at 72.42 and created zero attachments. The victim received the same records and created both attachments. The subsequent `kill` was logged by the server. The victim's life ended before the effect duration elapsed. The victim's client recorded removal before the [100-health respawn frame](native-respawned.png).
- In the follow run the shooter fired and entered the spectator view with `cmd spectate victim`. The trace shows the target's emission suppressed in [first-person follow](native-follow-first-person.png) and resumed in [chase follow](native-follow-chase.png). At the server's live `restart`, the victim's attachment ended before its 20-second deadline. [Follow-run trace](native-observer-events.txt) · [Match reset frame](native-match-reset.png)

The [camera](native-camera-probe.sh) and [follow](native-follow-probe.sh) scripts record launch arguments and input. The runtime build included temporary print statements around event send, receipt, attachment, emission, suppression and retirement. The excerpts preserve those lines and remove terminal color codes. The production source omits that instrumentation.

## Coverage and remaining differences

The native route deliberately uses authoritative hits on entities present in the recipient's snapshot. Stock's wider client-side radius sweep can also reach nearby visual objects outside the authoritative damage set. The native route sends bounded post-modifier damage for each victim and compares poses at the client. The stock packet carries core and edge damage and lets the client estimate the radius falloff independently. Muzzle, explosion, and generic blood effects keep their existing routes.

The gameplay test fires a Devastator through the simulation loop and checks the recorded hit against actual loss of health. Other tests exercise species name selection and the object bleed exception, lifetime bounds, distributed and undistributed scheduling, a live-to-live server restart, the spawn flag's persistence after the teleport pulse, and wire framing. Both engines show the attached cue with different rendering output. The exact sprite density and cadence were not measured at the software-rendered frame rate. Mixed and Modern rendering, object-origin mode, gentle-mode gates, hidden-target delivery, and a full map load were checked in code. Their live visual cases remain unverified.

The production build and the full managed suite passed after the temporary tracing was removed. [Build and test excerpts](build-and-tests.txt)
