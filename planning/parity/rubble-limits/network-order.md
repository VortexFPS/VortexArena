# Casing visibility and same-frame rubble order

Directory-order audit rows 13 and 18 cover `common/effects/qc/casings.qh` and `common/effects/qc/rubble.qh`. The reference was `/tmp/vortex-parity-base` at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`, served by `/tmp/va-reviewed-stock` with its byte-identical compiled QC and SDL libraries. All runs used disposable profiles. The native probe used the pinned Godot 4.6.3 Mono binary from `/tmp/vortex-parity-godot`, with the installed .NET 10 runtime rolling the net8.0 assembly forward.

## Row 13

Two stock SDL clients fired the Shotgun and MachineGun on Stormkeep with live `g_casings` changes. The client console's `prvm_edicts client` showed the model and creation time of each received casing. With the cap set to one, a repeated creation time indicates that no new casing replaced the old one.

| Server mode and client state | Shooter receives | Second client receives |
| --- | --- | --- |
| `0`, following shooter | None | None |
| `1`, following shooter | Shotgun shell | Shotgun shell |
| `2`, following shooter | Shell and bullet casing | Shell and bullet casing |
| `2`, follower hides their viewmodel | Shell | None |
| `2`, follower sets `cl_casings 0` | Shell | None |
| `2`, shooter hides their viewmodel | None | Shell |
| `2`, independent viewer in PVS hides their own viewmodel | Shell | Shell |
| `2`, independent viewer outside PVS | Shell | None |

The old native attack path broadcast the casing effect and both peers could receive it with their personal casing or viewmodel settings disabled. Native replicates the two preferences through `sentcvar` and selects recipients at emission, as pinned `SpawnCasing` does. The PVS check uses the recipient eye and firing player's bounds. The Godot probe used two negotiated ENet sessions, the shipped Stormkeep PVS and a controlled floor. It verified the table against decoded events, including the world-relative event for the independent viewer.

## Row 18

Both phases of the paired check used a normal Shotgun attack with one unspread 300-damage pellet. The stock SDL clients loaded a server-only Stormkeep entity overlay with the shooter at `1216 248 -8` facing yaw 90 and the target at `1216 306 -8`. Beta's stock edicts linked the surviving gibs before the shell on the same creation timestamp, `31.7477894`. The native probe invoked the real Shotgun attack gate and damage path with the same weapon settings. Both native ENet clients decoded the model splash before the shell, and Beta's model tosses and shell spawn occurred in the same Godot process frame.

A second native check queued a shell, ordinary corpse splash, and bullet casing in one send frame. Both clients received that exact order. With both caps set to one, the rendered batches retained the bullet casing and one ordinary model gib. Recipient filtering now retains emission order in each client's bundle. Stock appends to the rubble list and walks head to tail when finding the oldest creation time, so ties evict the earliest insertion. Native's existing spawn sequence already gives that result across render batches, with independent casing and model-gib caps.

The existing 1,024-slot native ceiling per ordinary pool remains an intentional allocation limit. The earlier viewport, live cap and 42-assertion fixed-step results cover the debris simulation. [Cap and motion evidence](README.md)

## Emission-time follow and PVS regression

Native normally moves and fires players during simulation ticks, advances observer controls in `ServerNet.DriveObserverJoins` after those ticks, and then sends the effect queue. A follow release can therefore occur between the shot and its send. A controlled EndFrame case also changed Beta's position between three emissions in one send frame. Following Alpha yielded a view-relative shell, moving outside the PVS suppressed the second shell, and returning to the PVS yielded a world-relative bullet casing. Beta was outside the PVS at flush. The previous build delivered an empty casing sequence in this case. The final probe also used Beta's normal ENet attack2 input to release follow and verified that the last casing emitted before release stayed first-person.

The server stores each casing decision in a reusable list on the accepted peer session. The send pass merges those decisions with the effect queue in emission order. A paused-send case disconnected Beta and accepted a replacement session while the casing was pending. The replacement received the next casing after resuming play. The pending casing reached only Alpha.

Beta's real `NetGame.OnEffectReceived` callback received the decoded events. With an identity Godot camera basis and `cl_gunoffset "7 4 3"`, a shell from Quake coordinates `1216 248 30` appeared within 0.2 units of `1212 255 33` in the casing MultiMesh. With `chase_active 1`, it appeared within the same tolerance of `1216 248 30`. The probe tests the handler's chase gate with a controlled camera basis. Exact shell-tag placement, impact audio, and gentle and particle gib modes were outside this check. [Stock edicts, native probes and focused managed tests](network-order.txt)

## Repeat the native probe

From an already restored worktree, run the driver with Godot 4.6.3 Mono, a compatible .NET runtime, and Xvfb. The driver builds and runs from a disposable staging directory and prints its log location.

```sh
GODOT=/path/to/Godot_v4.6.3-stable_mono_linux.x86_64 \
  bash planning/parity/rubble-limits/reproduce-network-order.sh
```

Set `CASING_PROBE_PORT` to select a free local port if the default, 29779, is occupied. Set `DOTNET_ROLL_FORWARD=Major` when running the net8.0 build on the installed .NET 10 runtime used here. The original stock trials used the bytecode and SDL engine listed above. [Native probe source](CasingNetworkProbe.cs)
