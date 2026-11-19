# Timed superweapon poweroff

Directory-order audit row 93 uses Xonotic data and QuakeC at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The stock paths are `common/mutators/mutator/status_effects/status_effect/superweapons.{qh,qc}`, `common/mutators/mutator/status_effects/sv_status_effects.qc` and `server/client.qc`.

A timed Fireball pickup applies a status with an absolute deadline. The player frame sets the ordinary item bit while the weapon is owned. During its last six seconds a real client's finite timer can play `SND_POWEROFF` on `CH_INFO`. The effect survives the exact deadline tick. Once the status store has been initialized, the player frame floors a passed or cleared deadline to the current tick for the final countdown edge. Timeout clears the ordinary bit and weapon. Status retirement itself is silent. Losing the last superweapon early takes the normal removal path and plays the same asset on the stacking `CH_TRIGGER` channel. Both sources use base volume 0.7 and normal attenuation 0.5.

The unlimited bit makes the effect persistent without changing its original deadline. It suspends the countdown and preserves the weapon until the bit clears. Clearing it after expiry plays the final countdown edge and strips the weapon. If the store has never been initialized, pinned QC supplies a zero deadline. Clearing the unlimited bit then permits a single cue if `floor(-time - frametime) != floor(-time)`. With a 1/16-second tick, the fixture records one cue at time 1 and silence at time 1.125. Both paths strip the weapon and ordinary bit. Persistent normal removal, orphan cleanup, and unannounced timeout are silent. An unlimited holder without an applied effect can acquire the ordinary bit without acquiring an invented timer. InstaGib skips this ordinary-bit pass.

Player powerup cleanup also clears the three related item bits and stops the single-channel unlimited pickup sound. Death and a live player becoming an observer use the item-bit cue after the match-start guard when the starting loadout is finite. A match restart publishes its new countdown before cleanup, suppressing that cue. Stock clears the client flag before disconnect bit cleanup. Native mirrors that silence while preserving normal removal of an active non-persistent status on both restart and disconnect.

Pinned `CH_INFO` and native `SoundChannel.Auto` both have channel number 0. Pinned `CH_TRIGGER` and native `TriggerAuto` both have channel number −3. The native mixer allocates separate voices for overlapping channel-zero sounds from the same entity. The gameplay fixture checks the selected channel and the playback recordings establish audibility for the two observed recipients. [Mixer test](../../../tests/VortexArena.Tests/DpSoundSystemTests.cs)

The node-based mixer fallback releases a finished one-shot's channel key when its player is reused so a later stop still targets the correct sound.

Native uses the registered `misc/poweroff` sample and the positional sound bundle. `SoundService.Broadcast` reaches `ServerNet`, which writes the source net ID, sample, channel and mix. The listen client's loopback peer and the remote peer both reach `ClientNet.SoundReceived`, then `NetGame.OnSoundReceived` and `ClientWorld`. The current DarkPlaces mixer centers the view entity locally and spatializes the same event for nearby remote listeners. Predicted-fire suppression is specific to matching weapon-fire samples. Stock likewise uses a normal entity sound, with the same sound asset in the server and client data.

The internal native catalog key remains `superweapon`. The visible name is “Superweapons”. The owner-approved HUD continues to hide the unlimited row, including on overlap, and to show the finite timer with the skin tint. [Approved HUD evidence](superweapons-hud-verification.md)

## Focused verification

The existing Godot HUD fixture still uses the production Fireball touch handler, the native row builder and pinned CSQC. It additionally checks the running InstaGib frame, the exact countdown edge, missing-store crossing and noncrossing frames before a status tick, death, observer entry, restart, disconnect, the normal removal channel, the silent cleanup branches, the elapsed unlimited overlap, the single-channel stop on silent bit cleanup, and the fallback mixer’s stop handling when its pooled audio player is reused. The Debug build passed, along with 262 focused item touch, status timer, mixer, client lifecycle and match-flow tests. The managed tests used `DOTNET_ROLL_FORWARD=Major` on the installed .NET 10 runtime. [Fixture](../../../tests/fixtures/superweapons-hud/Probe.cs)

## Live capture

Stock used the reviewed DarkPlaces SDL binary for the dedicated game and both clients, with its SDL libraries under `sdl/usr/lib/x86_64-linux-gnu` and the supplied byte-identical QC programs. Native used Godot 4.6.3 Mono with the picker on the graphical listen host and a second client connected over ENet. Each client used a disposable profile, an Xvfb display and a separate PulseAudio sink. The monitors captured normal game audio. [Reproduction commands and cue matcher](superweapons-poweroff/REPRODUCE.md)

The Stormkeep overlay reused the existing player positions from the attached-damage probe. The native map arm placed a Fireball at `1040 240 32` with a four-second initial spawn delay. The stock arm suspended one at `1040 240 8`, added a second at `985 275 8`, and used a one-second spawn shield so pinned QC had initialized the status store before pickup. Both used the ten-second balance duration. With the shield disabled, a separate stock touch after match time 10 consumed the item without arming the timer. Pinned `StatusEffects_gettime` returns zero until the store exists, so the relative ten-second pickup value was rejected as a past absolute deadline. The native relative-duration pickup behavior is the existing UW-0123 port of the fix in `916d46a6`. [Upstream-watch ledger](../../upstream-watch/LEDGER.yaml)

- Both map-pickup arms played six recognizable countdown cues for the picker and the nearby peer. The finite HUD row appeared on pickup and cleared on expiry. [Paired frames](superweapons-poweroff/timed.webp)
- The early-loss pass removed Fireball with `give 0 fireball` before the countdown window. Stock used its second map pickup. The native repeat used a controlled timed grant followed by the same removal command. Both receivers recorded one cue in each arm.
- The overlap pass added the unlimited bit after the timed pickup or grant, held it past the original deadline, then cleared it. Both clients recorded zero poweroff cues during the hold and one at release in each engine. The HUD stayed hidden during the hold. [Paired overlap frames](superweapons-poweroff/overlap.webp)

The four listenable excerpts preserve the monitor samples at 48 kHz. They put the natural expiry at the beginning, the early removal at 8.25 seconds, and the elapsed overlap release segment at 11 seconds, with short silent joins. The event transcript uses the separate full recording clocks. Matches use normalized waveform correlation against the first 300 ms of `sound/misc/poweroff.ogg`. Software rendering and independent audio buffers limit cross-engine wall-clock comparisons. [Stock picker](superweapons-poweroff/stock-picker.flac) · [Stock peer](superweapons-poweroff/stock-peer.flac) · [Native picker](superweapons-poweroff/native-picker.flac) · [Native peer](superweapons-poweroff/native-peer.flac) · [Event transcript](superweapons-poweroff/cue-events.txt)

A fresh 480×320 run reproduced all six timed-pickup cues for both players in each game. The stock early-loss cue was also audible to both. The first native 640×448 repeat yielded three recognizable cues for the picker and two for the peer. The cause of the lower match counts remains open. [Replay checks](superweapons-poweroff/REPRODUCE.md)

| Verification input | SHA-256 |
| --- | --- |
| Xonotic SDL binary | `79e38b239b8dc5d5ee1ac8ada8245a51381ce27277dc165645fea0e69fed4312` |
| Godot Mono binary | `cabf9ac16c55f8ca662da62e1f5908d3c35f765e312be62f104097bd8f78b987` |
| Stock server QC | `c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7` |
| Stock client QC | `cbc9fd9a4e2c7fc8f02f4aab6e5883e549790eb24e71be2bc0b8a0b0fea89999` |
| Stock menu QC | `4d502424af24f90c36c8a17a034ded536571a0d353a4192d8b332e8cc8b2d771` |
| Poweroff sample | `a15d93c538d40f49c2f96ac37a550f86ad4691645dc20f4c046e6114860709d4` |
