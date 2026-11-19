# KeyHunt controller and bot target order

Ordered audit row 30, `common/gametypes/gametype/keyhunt/sv_keyhunt.qh`. Stock used the Xonotic data pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` and its compiled SVQC. Both local dedicated servers mounted the same Stormkeep package and ran two non-firing bots, one per team, at 64 Hz with item pickups disabled. They used fresh profiles, a four-second start delay, and 1.25-second round and tracking settings. The warmup variant enabled indefinite warmup. The native probe logs state transitions from a disposable build. The stock probe uses read-only VM globals, edict fields, disassembly, and a breakpoint. [Selected output and input hashes](observations.txt)

## Controller

Stock `kh_Controller_SetThink` stores the ceiling of the requested delay. `kh_Controller_Think` runs once per second, decrements positive counts, and invokes the scheduled callback on a think that starts at zero. The live delayed-start probe first ran the wait callback before `game_starttime` and scheduled another wait for `game_starttime - time + 0.1`. Its round and tracker then each took three seconds from their scheduling callback. The native controller had used fractional absolute deadlines and checked roster readiness every frame. It now uses the one-second controller, shares the stock roster predicate for both readiness and owner selection, and follows the match countdown and both map-reset boundaries. Match end removes the live keys and stops the controller.

| Variant | Stock, simulation time | Native, simulation time |
|---|---|---|
| Four-second start delay | Start time 5.000. Round countdown armed at approximately 7.000, keys spawned at 10.000, tracking enabled at 13.000. | Start time 4.000. Round countdown armed at 6.000, keys spawned at 9.000, tracking enabled at 12.000. |
| Indefinite warmup | `game_starttime=0`. Round countdown armed at approximately 3.000, keys spawned at 6.000, tracking enabled at 9.000 while still in warmup. | Round countdown armed at 4.000, keys spawned at 7.000, tracking enabled at 10.000 while still in warmup. |

Stock starts its world clock near one second. The stock probe samples the countdown fields and observed the actual head and tracker changes on the next poll. The table derives stock action times from the recorded counter and next-think values. In the warmup variant, the two rosters became complete at different controller polls. The fixture compares the transitions and per-stage cadence. It sets `g_start_delay` explicitly on both servers.

## World keys and bots

Stock prepends each spawned key to `kh_worldkeylist`. The two-team run showed the blue key at the head with `count=1`, linked to the previously spawned key. Native keeps that newest-first active list alongside its per-team lookup. Bots use the active list, skip their own carriers, and keep the first target on an equal rating. Their carrier role checks whether they still own any live key.

`kh_waypointsprite_visible_for_bot` has two nested conditions. A nonzero attached waypoint returns true before the player-visibility callback and PVS branch. The pinned disassembly confirms this branch, and the live breakpoint fired from the carried-key branch while tracking was still off. Both carrier edicts had attached waypoints. The native bots chose opposing carriers before the reveal. The native tests also rate a loose key whose player marker is hidden. Regular navigation reachability still applies. Stock sampled the tracking flag at zero on both sides of the first breakpoint in each run. The breakpoint establishes entry into the carried-key branch. Final stock routes and scripted removal of an attached waypoint were outside the comparison.

## Reproduction

Use the same local stock SDL executable and compiled QC overlay as the other paired audits, a local Godot 4.6.3 Mono build, and a restored native project. The script prints the disposable run directory. It binds the native match socket to loopback in its diagnostic copy and disables reporting and publishing. Both live rosters in this probe consist of bots. The focused tests exercise human client joins, typing input, ready commands and admin endmatch through the native server.

```sh
python3 planning/parity/keyhunt-controller/compare.py --godot "$GODOT"
python3 planning/parity/keyhunt-controller/compare.py --godot "$GODOT" --variant warmup
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~KeyHunt|FullyQualifiedName~Kh_|FullyQualifiedName~RoundHandlerWiringTests|FullyQualifiedName~TargetUtilitiesTests|FullyQualifiedName~BotCtfObjectiveTests'
```

The native build and 60 focused tests passed. Tests cover the fractional, zero and disabled tracker settings, spawn order, equal bot goal ratings, typing eligibility, ready-restart with immediate and deferred map reset, and match-end key cleanup. The two-engine probe uses positive fractional settings and two teams. [Comparison script](compare.py) · [KeyHunt registry](../registry/keyhunt.yaml)
