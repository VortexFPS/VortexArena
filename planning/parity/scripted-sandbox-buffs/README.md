# Scripted Sandbox and buff multiplayer review

Each arm uses a separate loopback server and two independently launched graphical clients. The roles are ParityOwner and ParityGuest. The original uses DarkPlaces and QuakeC compiled from Xonotic data revision `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The native arms use Godot 4.6.3 Mono and ENet. [Earlier paired runtime setup](../paired/README.md)

The fixture appends named camera/player destinations and one Vampire pickup to the original Stormkeep BSP entity lump. The reproducer stages the exact same edited BSP and stock external lightmaps for both arms. The two clients use separate user directories and Xvfb displays. Inputs use in-game commands entered through the console or bound to function keys, the held V binding and relative mouse motion. The server's Sandbox autosave provides an independent model origin and movetype sample. Movement type 3 is the temporarily held WALK state and 6 is the original TOSS state.

## Observations saved before fixes

- The original guest traced the object and received an owner name, then received the edit denial. The serialized object carried a nonempty owner identity. The initial guest hold left the stored object in TOSS. With free editing enabled the guest hold changed the object to WALK. The stored object returned to TOSS on release. The original camera for the owner-only positive test aimed too high after the object settled, so that input does not establish successful owner carry.
- Original read-only mode denied a new edit command. When enabled during the guest's existing free-editor carry, the object stayed in WALK and its origin changed until the guest released V. This compares two separate behaviors, denying a new modification and handling one already in progress.
- The pre-fix native remote clients arrived at the scripted coordinates with yaw zero and faced the side wall, while the original clients faced each other. Native rendered its spawned object but both clients reported that their information traces missed. The native server assigned a zero-sized hull to Sandbox alias models because generic `SetModel` only copies bounds for inline BSP models.
- The native fix forwards the existing authoritative view snap to remote clients and assigns Sandbox hulls from model metadata when spawning, loading or scaling an object. Persisted Sandbox transforms now use the live edict pose, including position after TOSS movement.

[Original contact sheet](pre-fix/original-sandbox.jpg) · [Original inputs](pre-fix/original-inputs.json) · [Original command line](pre-fix/original-commands.json) · [Original timeline](pre-fix/original-timeline.jsonl) · [Owner log](pre-fix/original-owner.txt) · [Guest log](pre-fix/original-guest.txt) · [Server log](pre-fix/original-server.txt)

[Native contact sheet](pre-fix/native-camera.jpg) · [Native inputs](pre-fix/native-inputs.json) · [Native command line](pre-fix/native-commands.json) · [Native timeline](pre-fix/native-timeline.jsonl) · [Owner log](pre-fix/native-owner.txt) · [Guest log](pre-fix/native-guest.txt) · [Server log](pre-fix/native-server.txt)

## Reproduction

Use Xvfb, xdotool, ImageMagick with WebP support and the Linux game runtimes described in the [paired Stormkeep setup](../paired/README.md). Enable the DarkPlaces `libd0_blind_id` and `libd0_rijndael` libraries via `LD_LIBRARY_PATH` for an authenticated original ownership run. Use the public `key_0.d0pk` from the original distribution or a separate local test signing domain. The harness can generate unsigned identities or copy pre-created fixture identities into fresh user directories with `--original-key-users`. A run that fails authentication is unusable as ownership evidence. Temporary private identities are outside the repository.

```sh
python3 planning/parity/scripted-sandbox-buffs/reproduce.py \
  --output /tmp/va-sandbox-buffs-new \
  --godot /path/to/Godot_v4.6.3-stable_mono_linux.x86_64 \
  --xonotic-client /path/to/darkplaces-sdl \
  --xonotic-server /path/to/darkplaces-dedicated \
  --original-base /tmp/vortex-parity-base \
  --original-maps /tmp/va-original-maps \
  --original-code /path/to/compiled.pk3dir \
  --original-public-key /path/to/key_0.d0pk
```

Use `--engine original|native` and `--phase sandbox|buff` to run only one scenario. Original sessions use displays 151–152, native sessions use 153–154. The requested base port and native offset leave discovery ports in 26700–26719. `--native-project` points to an existing Debug runtime or an isolated project copy containing its own Debug build. Use a fresh output path for each repeat. Full logs, per-client sampled WebPs and any engine-generated demos remain in the run directory. Software rendering and asynchronous autosave limit frame-precise comparisons.

## Original repeat with fixed gameplay bindings

Both clients joined active play. The server confirmed that ParityOwner picked up the authored Vampire buff, the owner received the pickup message, and the guest received the corresponding broadcast. The same run reconfirmed the owner-only restriction and a guest carry in free-edit mode. The object stayed in WALK and changed origin when read-only was enabled during the guest's hold. It returned to TOSS when the guest released V. A new read-only edit was refused.

[Buff frames](captures/original/buff.jpg) · [Sandbox frames](captures/original/sandbox.jpg) · [Timeline](captures/original/timeline.jsonl) · [Server log](captures/original/server.log) · [Owner log](captures/original/owner.log) · [Guest log](captures/original/guest.log) · [Commands](captures/original/commands.json) · [Input hashes](captures/original/inputs.json)

## Native implementation and focused verification

The native server now sends the authoritative facing on a remote view snap, applies alias-model bounds to Sandbox props, and serializes their current server pose. New acquisitions check ownership, free-editor permissions and read-only status. An existing carry keeps updating until release or object removal when read-only is enabled, matching the recorded original behavior.

The isolated native host build succeeded. All 14 focused tests passed on .NET 8, including the drag input path and the original read-only transition. [Test log](captures/focused-tests.txt)

The first native repeat to reach active play verified both clients could trace the same ParityOwner prop. Its autosave stayed in TOSS with the same origin during the guest's owner-only hold. The owner's hold moved it from `1696 -534.077 -44.344` to `1675.658 -531.645 -44.344` and changed its movetype to WALK. The session was interrupted before the release sample and later phases. [Sandbox frames](captures/native-owner/sandbox.jpg) · [Timeline](captures/native-owner/timeline.jsonl) · [Server log](captures/native-owner/server.log) · [Owner log](captures/native-owner/owner.log) · [Guest log](captures/native-owner/guest.log) · [Commands](captures/native-owner/commands.json) · [Input hashes](captures/native-owner/inputs.json)

## Original Vampire pickup

Both graphical clients showed the map relic before the scripted touch. The owner then received “You got the Vampire buff!” and saw the timed Vampire icon. The guest received “ParityOwner got the Vampire buff!” and saw the carrier relic above the owner's model. The guest later received the buff loss on owner disconnect. This phase used the same fixture and authenticated pair as the original Sandbox observation. [Contact sheet](original-buff/contact.jpg) · [Owner events](original-buff/owner.txt) · [Guest events](original-buff/guest.txt) · [Timeline](pre-fix/original-timeline.jsonl)

A subsequent native repeat confirmed owner release. The prop switched from TOSS to WALK as the owner moved it about 20 game units, then returned to TOSS on release. Both clients again identified the prop's owner, and the guest's restricted edit was refused. [Owner carry and release](captures/native/owner-carry.jpg) · [Timeline](captures/native/timeline.jsonl) · [Input hashes](captures/native/inputs.json)

The original owner also acquired and released the prop after the script moved that player to the opposite camera. The saved entity entered WALK and returned to TOSS at a displaced origin. [Original owner frames](captures/original-owner/owner-carry.jpg) · [Timeline](captures/original-owner/timeline.jsonl)

The native free-editor sample entered WALK before the read-only toggle and remained in WALK after the toggle while the prop moved about 20 game units. Releasing V restored TOSS. The owner received the read-only command denial, and the subsequent read-only hold did not move the prop. [Native free-edit and read-only frames](captures/native/free-and-readonly.jpg) · [Timeline](captures/native/timeline.jsonl) · [Server log](captures/native/server.log) · [Owner log](captures/native/owner.log) · [Guest log](captures/native/guest.log) · [Commands](captures/native/commands.json)

## Matched native Vampire pickup

The native owner and guest reached the same fixture coordinates as the original. Both native cameras saw the active pickup before the touch. The owner's after frame shows the Vampire skull and buff meter at `1696 -320`. The guest's fixed camera sees the relic disappear from the floor and the owner gain the visible attachment. Native client logs record the accepted camera snaps. The original additionally printed explicit pickup notification text in both client logs. The sampled native frames and logs establish the pickup through the HUD and world changes.

[Native buff frames](captures/native-buff/buff.jpg) · [Timeline](captures/native-buff/timeline.jsonl) · [Owner log](captures/native-buff/owner.log) · [Guest log](captures/native-buff/guest.log) · [Server log](captures/native-buff/server.log) · [Commands](captures/native-buff/commands.json) · [Input hashes](captures/native-buff/inputs.json)

Native repeat textures were reduced in resolution for software rendering. The captures establish the state transitions and player-visible pickup feedback. Camera sampling and autosave cadence limit comparison of precise motion, effects and timing.

## Native Vampire pickup and clock correction

Both native clients reached the fixture viewpoints. The owner touched the authored pickup at `1696 -320` and the Vampire HUD timer appeared. The initial native capture showed 91 seconds on the 60-second buff. Status-effect expiry was a server timestamp and the powerups panel was subtracting a client-local clock. The fixed native client uses the latest server snapshot time. The repeat showed 58 seconds, with the original sampling 57 seconds at a slightly later point. [Before the clock fix](captures/native-buff-before-clock/buff.jpg) · [After the clock fix](captures/native-buff/buff.jpg) · [Original](captures/original/buff.jpg)

The native inactive pickup now publishes 0.3 alpha and clears fullbright, as the original does. The map relic and carrier area in the native software-rendered views still contain dark geometry. The captures establish pickup and the HUD timing correction. Relic shape, transparency and lighting parity remain unverified from these views. [Owner log](captures/native-buff/owner.log) · [Guest log](captures/native-buff/guest.log) · [Server log](captures/native-buff/server.log) · [Timeline](captures/native-buff/timeline.jsonl) · [Commands](captures/native-buff/commands.json) · [Input hashes](captures/native-buff/inputs.json) · [Isolated build](captures/native-buff/build.txt)

The final isolated run passed all 75 focused Sandbox, held-input wire, buff admission, powerup and status-timer tests. [Test log](captures/native-buff/tests.txt)
