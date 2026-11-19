# Native map-vote verification

A Godot 4.6.3 Mono headless probe ran the native server and two independent ENet client sessions on loopback. The probe used an in-memory DM world, put one authenticated player in the active player state, and left the other observing. Both clients received the same timed map ballot with abstention enabled.

| Check | Result |
| --- | --- |
| Active player presses 1 and observer presses 2 | Both inputs reached the server's command handler and selected the expected distinct maps. |
| Active player moves right from the last map and presses Enter | Keyboard selection reached the abstain row. The server recorded abstention and cleared the previous map choice. |
| Observer moves the cursor onto the drawn “Don't care” row and clicks | Hover selected the appended row. The authenticated observer's abstention reached the server and replaced the previous map choice. |
| Server acknowledges both abstentions | Both client messages carried the appended choice index. The remote and listen panel states highlighted the own abstain choice. |
| Listen and remote countdowns | The listen deadline equaled the world timeout. The remote deadline matched the authoritative simulation deadline in the reliable ballot. With movement snapshots filtered for multiple refreshes, the ballot clock advanced, the fixed deadline stayed exact, and a 17-second extension arrived without resetting selection. Resuming snapshots did not change the deadline. |
| Previous protocol version | A third ENet peer advertised version 25 parity. The v26 server rejected it before authentication or player admission. |
| Periodic updates and ballot changes | A periodic refresh retained selection. Replacing the second candidate with the same candidate count reset the selection and kept its full name and availability. Winner reveal used the panel clock. Hiding and reopening the same candidate list reset the ballot. |

The probe drove `NetGame.HandleMapVoteInput` and `UpdateMapVotePanel` in the game assembly. The cursor check read the hitbox from an actual drawn panel. The candidate replacement and winner lifecycle cases injected panel-feed state because regular countdown packets keep the original ballot candidates. The snapshot-loss case dropped only movement packets at the observing client's transport callback and left real reliable ballot delivery intact. The previous relative anchor would have displayed expiration one second early in that run.

The final run authenticated both clients and exited successfully:

```text
[ClientNet] handshake accepted by 'VortexArena Server': netId 1, tickrate 72 Hz.
[ClientNet] handshake accepted by 'VortexArena Server': netId 2, tickrate 72 Hz.
SNAPSHOT_LOSS_PROBE_PASS dropped=94 old_error=1.000s
ABSTAIN_KEYBOARD_CURSOR_PROBE_PASS
PROTOCOL_V25_REJECT_PROBE_PASS
MAPVOTE_PROBE_PASS
```
