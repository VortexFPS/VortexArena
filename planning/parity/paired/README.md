# Paired Stormkeep multiplayer captures

19 November 2026. Each session ran a separate server and graphical client over loopback. Both arms loaded the same original Stormkeep BSP, SHA-256 `20b1aae86c9e1d4d08fa02cc7ebf6894578c4fd8680af586cfaaafed96127ca1`. The native client spoke native ENet. The original client and server ran unmodified Xonotic QuakeC from `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`, built with GMQCC `888ad563c7ff825ef049105adc9142e46a7cfb83` and run on the upstream DarkPlaces fork at `d93f9c4292039354a2b8d40d11bc386891e55fe5`. The native game ran the shared worktree’s Debug build at base `43e4b7d91db9267916500e5fbea85414d2d279a6` with Godot 4.6.3 .NET. Per-run manifests identify the compiled assemblies and original bytecode.

| Session | Frames | Client log | Server log | Inputs |
|---|---|---|---|---|
| Xonotic Freeze Tag | [Frames](freeze-original/frames.webp) · [Demo](freeze-original/session.dem.gz) | [Client](freeze-original/client.txt) | [Server](freeze-original/server.txt) | [Commands](freeze-original/commands.json) · [Timeline](freeze-original/timeline.json) · [Hashes](freeze-original/inputs.json) |
| Native Freeze Tag | [Frames](freeze-native/frames.webp) | [Client](freeze-native/client.txt) | [Server](freeze-native/server.txt) | [Commands](freeze-native/commands.json) · [Timeline](freeze-native/timeline.json) · [Hashes](freeze-native/inputs.json) |
| Xonotic Crylink | [Frames](crylink-original/frames.webp) · [Demo](crylink-original/session.dem.gz) | [Client](crylink-original/client.txt) | [Server](crylink-original/server.txt) | [Commands](crylink-original/commands.json) · [Timeline](crylink-original/timeline.json) · [Hashes](crylink-original/inputs.json) |
| Native Crylink | [Frames](crylink-native/frames.webp) | [Client](crylink-native/client.txt) | [Server](crylink-native/server.txt) | [Commands](crylink-native/commands.json) · [Timeline](crylink-native/timeline.json) · [Hashes](crylink-native/inputs.json) |

## Observations

| Area | Outcome |
|---|---|
| Freeze Tag self-freeze and thaw | Both clients reached live Freeze Tag with three bots. The explicit `cmd kill` generated a suicide event on each server. The native client survived as a frozen player with 1 HP, showed thaw progress, and returned to 100 HP between the 30.00 s and 32.84 s frames. The original client showed 1 HP and the revival ring before a teammate revived the player. The native freeze view was almost entirely cyan while the original retained the corridor and visible ice geometry. The recordings used different viewpoints and the native Freeze Tag arm used native lightmaps with the stock BSP. They establish the visibility difference in these runs, with its cause and original timeout behavior still open. [Original client log](freeze-original/client.txt#L182) · [Original frames](freeze-original/frames.webp) · [Native frames](freeze-native/frames.webp) |
| Freeze Tag weapon arena | With both arena cvars set to `vortex`, the native server recorded bots firing Devastator, Hagar and Mortar as well as Vortex. The original event log identifies Vortex Arena and records Vortex equipment on the compared freezes. The native item admission reads the named cvar as a number, while spawn loadout expands it as a weapon-name list. That mismatch leaves a path for map weapon pickups in a named arena. The captures establish the out-of-arena firing, with source evidence for why map pickups remain available. [Original server](freeze-original/server.txt#L66) · [Native server](freeze-native/server.txt#L97) · [Item filter](../../../src/VortexArena.Common/Gameplay/Items/StartItem.cs#L434) |
| Buffs in Freeze Tag | A separate original Freeze Tag run logged a Vampire pickup by a bot. Neither native log reports an equivalent pickup during its recorded round. Bot positions and pickup choices diverged, so this is an unpaired positive observation. The native preload warning for `models/items/buff.md3` names a different asset from the actual pickup and carrier relic, `models/relics/relic.md3`. These recordings do not establish a missing relic, a bad pickup, or a failed buff cleanup on freeze. [Original Vampire pickup](freeze-original-buff-observation/client.txt#L193) · [Native preload warning](freeze-native/client.txt#L36) · [Native relic path](../../../src/VortexArena.Common/Gameplay/Mutators/BuffsMutator.cs#L290) |
| Crylink ammo in weapon arena | Both clients fired the slow Crylink primary and took their own splash damage. In the native arena the cells display fell to −3 then −6 while firing continued. The port’s Crylink attack calls `TakeResource` directly, while the original goes through `W_DecreaseAmmo` and skips consumption with unlimited ammo. The visible negative count confirms a resource/presentation difference in the arena case. [Original frames](crylink-original/frames.webp) · [Native frames](crylink-native/frames.webp) · [Native fire log](crylink-native/server.txt#L351) · [Native attack](../../../src/VortexArena.Common/Gameplay/Weapons/Crylink.cs#L231) |
| Crylink short fade and presentation | Both servers reported a two-second primary lifetime and 1.5-second fade for inner and outer spikes, with speed reduced to 100 qu/s. The records show prominent purple impact bursts and splash feedback in Xonotic and visible native spikes, trails and subsequent splash feedback. The original and native clients framed different backgrounds after the same teleport command. Wall bounce and splash interrupt the sequences. The shots and software-rendered frames do not establish alpha curve equivalence or an isolated missing effect. [Original timeline](crylink-original/timeline.json) · [Native timeline](crylink-native/timeline.json) |

The runs stage the stock BSP with each engine’s own runtime assets and preserve the native navigation cache. The native Freeze Tag capture used native package lightmaps with the stock BSP. Both Crylink arms include the original external lightmap files and record matching hashes. The native Crylink view still appears much darker. The first original Freeze Tag session, preserved for its Vampire pickup, had an active welcome panel after the console joined the player. The main original Freeze Tag capture has the panel disabled. Separate inputs are listed with each run. Bots, spawn choices and render cadence vary across arms. This evidence can establish state transitions and visible differences, but does not support pixel comparisons or millisecond timing. Sound was disabled and player-statistics registration was outside scope.

## Reproduce on Linux

Build the native host as described in [Running Vortex Arena](../../../docs/RUNNING.md). The capture runner needs Python 3, Xvfb, xdotool, ImageMagick with WebP support, the upstream DarkPlaces dedicated and SDL executables, and the exact Xonotic data and compiled `progs.dat`, `csprogs.dat`, and `menu.dat`. Executables and libraries can be installed in a temporary directory. For this run the required SDL libraries were unpacked locally and supplied in `LD_LIBRARY_PATH`.

```sh
python3 tools/parity-paired.py \
  --output /tmp/va-paired-new \
  --godot /path/to/Godot_v4.6.3-stable_mono_linux.x86_64 \
  --xonotic-client /path/to/darkplaces-sdl \
  --xonotic-server /path/to/darkplaces-dedicated \
  --original-base /tmp/vortex-parity-base \
  --original-maps /tmp/va-original-maps \
  --original-code /path/to/compiled.pk3dir
```

Build the original bytecode with `make -C /tmp/vortex-parity-base/qcsrc QCC=/path/to/gmqcc PROGS_OUT=/path/to/compiled.pk3dir WORKDIR=/tmp/va-qc-work QCCFLAGS_WATERMARK=863cd3e qc`. Use a fresh output directory and unused ports. The runner records every sampled WebP, a timestamped input timeline, raw logs and original DarkPlaces client demos under its output directory. The four evidence directories here retain selected frames and the full logs with ANSI formatting removed. Unzip either original `.dem.gz` into the Xonotic user `data/demos` directory and use `playdemo` with the matching runtime and map data. The full frame sequences are under `/tmp/va-paired-20261119` in the capture workspace.

The official 0.8.6 engine was also tried and emitted QuakeC VM errors against the pinned 2026 gameplay data. The recorded original sessions use the upstream fork build identified above. [Xonotic upstream DarkPlaces](https://gitlab.com/xonotic/darkplaces/-/commit/d93f9c4292039354a2b8d40d11bc386891e55fe5)

## Native post-fix verification

Both scenarios ran again against the fixed Debug build with the same gameplay settings and the preserved original recordings as the reference. The Debug build completed with zero warnings and errors. The repeat used the original Stormkeep BSP and external lightmaps, which match the original hashes. The pre-fix native Freeze Tag capture used native package lightmaps.

| Check | Before | After |
|---|---|---|
| Vortex-only Freeze Tag | Devastator, Vortex, Hagar and Mortar each appeared in the native first-fire log. | Vortex was the only weapon that appeared in the post-fix first-fire log. The server recorded two Vortex frags. The client still froze at 1 HP after the scripted self-kill and recovered to 100 HP. |
| Unlimited-ammo Crylink | Cells dropped to −3 and then −6 after two primary bursts. | Cells remained at 0 through two primary bursts, with the infinity indicator still shown. Frames show projectile effects and self-splash, and the server recorded Crylink bounce splash as the cause of the self-frag. The recorder ended at 32.77 s after those observations and before its third scheduled trigger. |

[Post-fix frame sheet](post-fix/frames.webp) · [Server and client events](post-fix/events.txt) · [Freeze Tag timeline](post-fix/freeze-timeline.json) · [Crylink timeline](post-fix/crylink-timeline.json) · [Build and map hashes](post-fix/inputs.json)

The repeat confirms the arena and ammo fixes in these multiplayer scenarios. The native frozen view still showed a near-opaque cyan wash. Subsequent scripted captures verified [Vampire pickup and timer](../scripted-sandbox-buffs/README.md) and [visible Crylink fade](../scripted-objectives/README.md). The exact alpha curve remains open.
