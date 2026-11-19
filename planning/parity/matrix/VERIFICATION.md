# Native dedicated Matrix chat verification

The reference is Xonotic commit `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. Its `lib/matrix/command.qc` defines the operator commands, `lib/matrix/matrix.qc` defines the r0 requests, and `server/chat.qc` and `server/matrix.qc` connect the two chat directions.

## Dedicated console

On the native dedicated server console, use either `sv_cmd mx …` or `mx …`. The bridge is idle with an empty token, and incoming polling requires an explicit start. Set the homeserver, Matrix account ID and credential in that order, then select a room.

| Control | Effect |
| --- | --- |
| `matrix_server <url>` | Select the homeserver. Use HTTPS, or HTTP for a loopback fixture. Changing the URL clears the current token, room and poll. |
| `mx user <@user:server>` | Set the Matrix account whose own messages the bridge skips. |
| `mx token <access-token>` | Supply the bearer token in the dedicated server's stdin. |
| `mx join <!room:server>` | Select and join a room. A Matrix room alias also works. Switching rooms stops the old poll immediately. The bridge waits for any in-flight join to resolve, leaves the confirmed room, then joins and resumes polling in the latest requested room. |
| `mx sync` | Start the filtered `/sync` poll. The first response establishes the cursor, then the bridge delivers new text from the selected room. |
| `mx messages` | Switch to the room's `/events` poll. Use either polling mode at a time. |
| `mx say <text>` | Send a Matrix text message directly. |
| `mx nick <name>` | Set the Matrix account's display name. |
| `mx typing` | Send the typing state with the upstream 30-second timeout. |
| `mx leave` / `mx forget` | Clear the room selection and stop the poll. Leave the joined room, then forget it when requested. |
| `mx token` | Clear the token and stop Matrix traffic. Supplying a token again rejoins the selected room. Run `mx sync` or `mx messages` to resume polling. |

Accepted public player chat follows the game-to-Matrix path used by Xonotic. The server's team, spectator-only, private and muted chat paths have no Matrix relay. Incoming Matrix text uses server-originated chat and reaches connected clients without being relayed back. The Matrix account's own events are filtered by `mx user`.

The token stays in the current world's memory and travels only in the Matrix HTTP authorization header. Use the direct stdin command to avoid placing the secret in process arguments, shell history or the UDP rcon transport. The application redacts direct token commands from rcon audit output. It uses TLS for non-loopback homeservers and rejects redirects. Loopback homeservers use a direct connection over HTTP or HTTPS. Remote HTTPS homeservers use the system proxy settings. The pinned `matrix_server` default is `http://matrix.org`, which cannot be used with a token under this transport policy. A map rebuild or server shutdown cancels in-flight work and clears the credentials. Configure the replacement world through its own dedicated console.

## Repeat the loopback test

From the repository root, with the Darkzone map installed and a Godot .NET executable available:

```sh
dotnet build VortexArena.csproj
python3 tools/tests/verify-dedicated-matrix.py --godot "$GODOT"
```

The harness uses a native dedicated host, a connected native client, and a fake Matrix homeserver bound to `127.0.0.1`. It asserts public game chat and explicit `mx say` in the recorded HTTP requests, and incoming `/sync` and `/events` text in the client's console. It also checks disabled startup, the ordinary listen-server gate, room aliases and rapid switches before queued writes drain, during a leave and during a delayed alias join, self-echo and event-type filtering, retry with the same cursor after HTTP 503, leave, restart, token clearing, token reapplication, and active HTTP connection cancellation on map change. A local proxy recorder checks both schemes with environment proxies enabled. The HTTP fixture exchanges chat. The HTTPS fixture records a direct TLS ClientHello and closes before certificate negotiation. The token and all rooms and accounts are fixture values. No public Matrix homeserver or real credentials are needed.
