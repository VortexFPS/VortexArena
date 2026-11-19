# Fireball server availability

The pinned stock Fireball calls `Net_LinkEntity(e, false, 0.1, modeleffect_SendEntity)` in `common/effects/qc/modeleffects.qc:71`. In `lib/net.qh:183–192`, that registers the per-recipient send callback, marks every send flag, bypasses visibility culling and schedules removal at `time + dt`. The receiving CSQC creates a fresh drawable on its first linked-entity packet. It takes the lifetime and fade from that packet and anchors them to the receiving client's current `time` at `modeleffects.qc:107–143`.

## Stock sign-on

The local stock SDL executable used for the paired trial has SHA-256 `79e38b239b8dc5d5ee1ac8ada8245a51381ce27277dc165645fea0e69fed4312`. In that binary, `Host_Begin_f` sets the client's `begun` field at offset `0x10` after verifying the preceding sign-on state. `SV_SendClientDatagram` checks that field at `0x25be66` and enters the gameplay entity path at `0x25c270` only when it is set. That path reaches `EntityFrameCSQC_WriteFrame` at `0x25cd72` for a CSQC-capable client. A transport connection still signing on does not receive the entity. A CSQC-enabled client that reaches `begin` can receive it on an entity datagram while the linked edict remains live. A map teardown removes the edict along with the rest of that world.

The stock send uses the per-client entity channel, subject to datagram timing and loss. The native equivalent begins eligibility when authentication completes and sends one reliable event per connection. It sends only to authenticated peers at the first flush, and sends still-live effects to newly accepted peers immediately after their accept packet. Pending effects go through the next flush, so admission cannot duplicate them. The server discards an effect at 100 ms of map simulation time from its emission. Each received effect still starts the existing presentation timeline at age zero.

## Local ENet probe

The probe uses a disposable build, loopback-only ENet, real challenge-response authentication and the production effect sink, server dispatch and wire decoder. It holds the authentication response while controlling the real map's simulation tick. The renderer and mounted assets were already exercised by the paired trial.

| Controlled case | Received Fireball messages |
| --- | --- |
| Authenticated before emission | 1 |
| Three ENet connections holding authentication | 0 each |
| Authenticated at 98 ms of simulation after a 125 ms wall pause | 1, after the accept packet |
| Repeated authentication on that connection | Still 1 |
| Authenticated at exactly 100 ms | 0 |
| Authenticated at 125 ms | 0 |
| Six 1/32-second physics ticks before the first network flush | The first tick’s blast had expired. Only the sixth tick’s blast reached each accepted peer, once. |
| Added a fresh blast and immediately tore down the map | 1 fresh event on each accepted connection |
| Rehosted a new map on the same UDP port | 0 previous-map events |

[Probe output](availability-observations.txt) · [Probe source](AvailabilityProbe.cs)

```sh
GODOT=/path/to/Godot_v4.6.3-stable_mono_linux.x86_64 \
  bash planning/parity/fireball-sphere/reproduce-availability.sh
```

The script uses an already restored local build. Set `FIREBALL_PROBE_PORT` to override its loopback UDP port. For the stock binary branch check, run the following against the local executable above.

```sh
objdump -d --disassemble=Host_Begin_f "$STOCK"
objdump -d --disassemble=SV_SendClientDatagram.lto_priv.0 "$STOCK"
```
