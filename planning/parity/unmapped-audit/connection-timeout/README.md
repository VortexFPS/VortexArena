# What the connection timeout measures

Two games can read the same setting and start its clock at different times. Xonotic data at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` declares `net_connecttimeout` and configures both connection and message timeouts to 30 seconds. The engine owns the clocks. [QC declaration](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L1) · [Game defaults](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/xonotic-server.cfg#L117)

## Attempting to join a silent host

Each client tried to join a fresh UDP port on localhost. A passive socket counted datagrams and never replied. The released Xonotic 0.8.6 client sent twelve pairs about a second apart at both settings. Vortex's native ENet client waited for its configured deadline and reported “Couldn't connect to the server.”

| Client | Setting | Datagrams | Last send after first | Load-ready log to failure log |
|---|---:|---:|---:|---:|
| Xonotic 0.8.6 | 12 s | 24 | 11.02 s | Stock menu timing unmeasured |
| Xonotic 0.8.6 | 22 s | 24 | 11.06 s | Stock menu timing unmeasured |
| Native ENet | 12 s | 5 | 7.51 s | 12.12 s |
| Native ENet | 22 s | 6 | 15.51 s | 22.14 s |

The driver watched the stock socket for five more seconds after its last send. It polled the native log while receiving datagrams, with a socket timeout of 0.2 seconds. A prior native run with texture compression enabled and a 22-second setting took 35.69 wall seconds between the observed log markers. The native watchdog arms on the first eligible frame after synchronous loading and reports expiry on a subsequent frame. The final probe disabled texture compression. [Recorded sends, signons and slot lifetimes](observations.txt) · [Client watchdog](../../../../game/net/NetGame.cs) · [Probe](reproduce.py)

The released client uses a retry counter before a server accepts. The newer DarkPlaces source used by other comparisons in this project uses ten attempts. Vortex’s separate DP7 client implements that ten-attempt loop and sends the challenge request for Xonotic servers. The release's second datagram is the fallback for old NetQuake servers. The native ENet handshake follows the protocol selected by ADR-0011. [Released retry counter](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/cl_main.c#L449) · [Released send loop](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/netconn.c#L2430) · [Newer retry counter](https://github.com/xonotic/darkplaces/blob/d93f9c4292039354a2b8d40d11bc386891e55fe5/cl_main.c#L589) · [Native protocol decision](../../../decisions/ADR-0011-protocol-ecosystem-boundary.md) · [Legacy compatibility decision](../../../decisions/ADR-0019-legacy-compatibility-mode.md)

## After a server accepts

DarkPlaces opens a connection using `net_connecttimeout`. On the client, fresh in-band traffic stamps a new deadline using `net_messagetimeout`. On the server, fresh traffic uses the connect setting until the player begins, then the message setting. Each deadline retains the value sampled at that event. [Open](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/netconn.c#L1112) · [Client receive](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/netconn.c#L2328) · [Server receive](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/netconn.c#L3595)

Previously Vortex’s legacy client used a single fixed silence interval, and the legacy server could refresh its timer on duplicate traffic. The DP7 client now reads both cvars from the session's live store. Both endpoints refresh their deadline when the channel accepts fresh traffic. A session fixture checked that changing either setting leaves an already stamped deadline in place, the next fresh message uses the updated setting, and a peer stays connected through the exact deadline. Channel tests cover duplicate and reordered delivery. [Legacy session](../../../../src/VortexArena.Legacy/LegacyClientSession.cs) · [Shared channel](../../../../src/VortexArena.Legacy/Protocol/DpNetChannel.cs) · [Session tests](../../../../tests/VortexArena.Tests/Legacy/LegacyClientSessionTests.cs)

The legacy server ran the pinned game program on Stormkeep with a 12-second connect timeout and a 3-second message timeout. A client paused during signon kept its slot for 12.008 simulation seconds. A second client joined normally, sent movement and went silent. Its slot was released after 3.000 seconds. All 89 focused checks passed with the original data mounted. [Server regression](../../../../tests/VortexArena.Tests/Legacy/ServerClientsTests.cs)

The successful multiplayer recordings illustrate the separate native completion boundary. The original peer logged acceptance and a subsequent Superweapon pickup. The native peer logged handshake acceptance, loading Stormkeep for rendering and prediction, then joining the game, in that order. Loss after acceptance during a slow map load remains unmeasured. [Paired recordings and replay instructions](../../review/superweapons-poweroff-verification.md)

The Vortex launcher also joined the stock Stormkeep server through `--legacy-connect` with the pinned game program. The client's mounted configuration set the message timeout to 18 seconds. After signon stage four, the fixture paused its own server. The client reported loss after 18.06 wall seconds. The checked-in launcher reproduced the result in 18.05 seconds. An earlier trial with a three-second message timeout expired during client-program startup, before the client entered the game. Both runs read the settings through the launcher's ordinary configuration path. The Xonotic menu shares its initialized console with the session. These timing measurements exercised the standalone legacy launcher. [Launcher](../../../../game/legacy/LegacyGame.cs) · [Menu console](../../../../src/VortexArena.Legacy/Menu/LegacyConsole.cs)

The Astra review identified a four-argument channel receive that could discard a finite timeout. It now retains the last interval across fresh messages. A recorded-demo regression also verifies one message per frame during timedemo signon and playback, including the message that completes signon. Both the release and newer upstream engines return after each timedemo message. These corrections passed the expanded 90-test selection with the original data mounted. [Released timedemo loop](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/cl_demo.c#L284) · [Newer timedemo loop](https://github.com/xonotic/darkplaces/blob/d93f9c4292039354a2b8d40d11bc386891e55fe5/cl_demo.c#L288)

## Reproduce

Build the native Debug project, then run the driver with Python 3, Xvfb, the Godot Mono executable and the released SDL client. The driver checks the Xonotic data revision and creates private temporary client profiles. It prints their directory and the measurements. The released SDL executable used here has SHA-256 `79e38b239b8dc5d5ee1ac8ada8245a51381ce27277dc165645fea0e69fed4312`. The reference directory layout and compiled-QC hashes are shared with the audio capture. [Stock capture setup](../../review/superweapons-poweroff/REPRODUCE.md)

```sh
dotnet build VortexArena.csproj
python3 planning/parity/unmapped-audit/connection-timeout/reproduce.py \
  --base /tmp/vortex-parity-base --stock /tmp/va-reviewed-stock --godot "$GODOT_MONO"
python3 planning/parity/unmapped-audit/connection-timeout/reproduce.py \
  --legacy-loss --base /tmp/vortex-parity-base --stock /tmp/va-reviewed-stock --godot "$GODOT_MONO"
```

The silent-port checker requires two datagrams per stock retry group, twelve groups spaced 0.8–1.5 seconds apart and five seconds of subsequent silence. Its low-load native measurement must fall between 0.4 seconds below and one second above the setting. Disposable checks rejected a 1/3 distribution with the right total, an early 9-second result for a 12-second setting, and a 36-second result for a 22-second setting. These bounds apply to the repeatable measurement. The native ENet launch uses the worktree's game data and explicit cvar argument. The stock and legacy launches mount the original QC programs.

For the managed checks, set `VA_BASE_DIR` to a data directory containing a mounted `xonotic-data.pk3dir` at the pin, the stock Stormkeep and shared map packs, and a later-sorting `.pk3dir` containing the compiled pinned programs. The server regression uses this mount. Confirm that its output includes both measured slot lifetimes. The pure session and channel tests run without a map install.

```sh
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests --no-restore -m:1 \
  --filter 'FullyQualifiedName~A_silent_peer_uses_the_signon_deadline|FullyQualifiedName~DpClientTests|FullyQualifiedName~LegacyClientSessionTests|FullyQualifiedName~DpNetChannelTests' \
  --logger 'console;verbosity=detailed'
```
