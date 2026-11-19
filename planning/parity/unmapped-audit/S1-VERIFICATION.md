# S1 verification and implementation handoff

S1 reviews 178 QC files from Xonotic data `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The [ordered manifest](S1-REVIEW.md) records the comparison, evidence quality and open dimensions per file. Runtime evidence below covers the stated trigger behavior.

## Q3 trigger_hurt

The [QC declaration](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/hurt.qh#L4) uses spawnflag 16 for the slow Q3 damage cadence. The [QC touch handler](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/hurt.qc#L26) chooses 1 second with that flag and 0.05 seconds without it. Native previously used spawnflag 1. The fix changes the native mask in [Triggers.cs](../../../src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs).

The [regression test](../../../tests/VortexArena.Tests/CompatRemapsTests.cs) calls `MapObjectsRegistry.RegisterAll` and spawns `trigger_hurt` through `SpawnFuncs.TrySpawn` before invoking the installed touch callback. It verifies default Q3 damage of 5 and that the touch at 2.1 seconds is accepted for flags 0 and 1, and throttled for flag 16. All three accept the touch at 3.1 seconds.

The full compatibility fixture passed 74 tests. The focused case filter also passed all three variants under .NET 8.0.34.

```sh
dotnet test tests/VortexArena.Tests --filter FullyQualifiedName~CompatRemapsTests --nologo
```

The checkout's SDK initially built the test assembly without a colocated .NET 8 runtime for the test host. The completed test run used the existing .NET 8-capable muxer at `/tmp/va-dotnet8/dotnet` to run the SDK's VSTest console against the built `net8.0` assembly.

The follow-up traced the non-creature eligibility field to exactly two stock producers. CTF flags and KeyHunt keys opt into trigger damage when their respective return-when-unreachable setting is enabled at spawn. The native hurt trigger and kill target now consult the same explicit field. Dropped keys use independent deadlines for returning and for letting the dropper pick them up again. The early-match regression dropped a key at one second, entered the hazard, and exercised its return at 3.1 seconds. All 123 CTF, KeyHunt, map-target and compatibility fixture tests passed.

## Local gameplay follow-up

The native view-location fixture used a disposable Stormkeep pack with an enclosing trigger and a single camera anchor at `-224 -124 -42`. A graphical listen-server client reached gameplay and displayed both configured color-coded join notice lines above the HUD. The position HUD reported the authored anchor coordinates. This run covers fixed-camera placement and notice delivery. Side-scroll movement in both directions, region exit, anchor serialization, snap behavior and launch-order Electro eviction are covered by the focused .NET 8 fixture.

[Graphical fixture](ordered-gameplay-smoke.webp) · [Ten focused tests](../../../tests/VortexArena.Tests/OrderedAuditGameplayTests.cs)

## Two-client join notices

The subsequent delayed-load fixture measured timing from receipt. A three-second notice displayed two seconds on release after a 1.5-second hold. With a four-second hold it expired at three seconds and never appeared. The pinned reader and draw loop use the same receipt and expiry boundaries. [Delayed-load traces and client lifecycle checks](client-state-notices/README.md)

Two native clients connected to a local dedicated server on the Stormkeep fixture. After Alpha joined, the server changed the notice before Beta connected. In the same observation window Alpha still displayed “Only Alpha” and Beta displayed “Only Beta.” Beta’s notice stayed at 85 seconds across captures 3.07 wall-clock seconds apart with the server paused via `slowmo 0`. The match timer stayed at 13:55. Pinned DarkPlaces passes `cl.time` to CSQC and advances it by zero while paused. The native countdown now uses the accepted snapshot clock with the same pause behavior.

The wire fixture rejects an oversized UTF-8 payload and truncates a configured notice at a Unicode scalar boundary. View-location state and the prediction carrier update only after the owner ID and the complete snapshot prefix through the owner data have been accepted. The targeted .NET 8 run passed 28 tests across the ordered-audit and client-cvar fixtures.

[Alpha during Beta’s join](ordered-gameplay-council/02-second-join-Alpha.webp) · [Beta’s notice](ordered-gameplay-council/02-second-join-Beta.webp) · [Pause start](ordered-gameplay-council/03-paused-a-Beta.webp) · [Pause repeat](ordered-gameplay-council/03-paused-b-Beta.webp) · [Inputs and timing](ordered-gameplay-council/notice-timeline.jsonl) · [Focused tests](ordered-gameplay-council/focused-tests.txt)

## Remote view-location transitions

Two headless native clients used the same local fixture with a bounded box trigger. Beta followed Alpha while Alpha occupied region 282. Beta accepted a snapshot with spectatee 1 and region 282 at server time 26.95. After Alpha left, Beta accepted region -1 while still following spectatee 1 at 27.36. After entering free observation, Beta accepted its own ID, 2, as the spectator status and region -1 at 27.78. These are client-decoded snapshot results. The earlier listen-server image records the rendered camera at the fixed anchor.

The disposable host build used read-only trace probes for accepted view state and spawn preferences, a fixture-only cheat latch to place Alpha, and temporary build shims for in-progress Onslaught and weapon-statistics code. Graphical pairs supplied the notice images. Headless peers supplied the follow and exit traces after the graphical camera attempts failed to stay synchronized with scripted input.

[Accepted Beta snapshots](ordered-gameplay-council/view-Beta.txt) · [Server trace](ordered-gameplay-council/view-server.txt) · [Commands and acknowledgments](ordered-gameplay-council/view-timeline.jsonl)

## Mode 2 preferences and replacement connection

The paired server trace exercised Spawn Near Teammate mode 2 with Alpha opted out and Beta opted in. Both reached Red. Beta changed its preference to 0, respawned, restored 1, and disconnected. Alpha retained 0. Gamma connected while Alpha remained and started at 0. The authoritative trace recorded Beta at client entity index -2 and network ID 2, then Gamma at index -3 and network ID 3. Native client entity indices and network IDs are allocated monotonically during a match. The run verified a replacement connection on the same server with fresh preference state. The focused cvar fixture separately exercises cleanup for the previous player object.

The first placement fell back to a map spawn with Alpha in the view-location corridor. A subsequent run disabled spawn shields and placed Alpha at three points with room beside the player. The post-hook trace recorded Beta’s opted-in spawn exactly 192 units to the side at each point. At the third point, Alpha was placed at `-1190 949 116` and Beta’s relocated position was `-1190 757 -7.96875`. After switching to opt-out with Alpha at the same point, Beta used the authored spawn at `-1184 1248 1`. In the separate native notice run, Beta first spawned with the server preference false at simulation time 60.08. The first opt-in update was reflected at 60.64.

[Preference and replacement server trace](ordered-gameplay-council/spawn-server.txt) · [Preference and reconnect inputs](ordered-gameplay-council/spawn-timeline.jsonl) · [Post-hook placement trace](ordered-gameplay-council/placement-server.txt) · [Placement inputs](ordered-gameplay-council/placement-timeline.jsonl) · [Earlier join ordering](ordered-gameplay-council/notice-server.txt)

### First spawn and late preference delivery

Pinned DarkPlaces clears a fresh client entity’s fields before sending server info. Its normal DP connect request carries protocol negotiation and the challenge. Optional connect key/value pairs come from explicit command arguments. Pinned Xonotic declares `cl_spawn_near_teammate` as a replicated boolean. CSQC starts sending it through `sendcvar` in `PostInit`, and SVQC updates the field through `sentcvar` on the calling player. The fresh field is false until the first update. [DarkPlaces connect request](https://gitlab.com/xonotic/darkplaces/-/blob/d93f9c4292039354a2b8d40d11bc386891e55fe5/netconn.c#L2144) · [DarkPlaces client initialization](https://gitlab.com/xonotic/darkplaces/-/blob/d93f9c4292039354a2b8d40d11bc386891e55fe5/sv_main.c#L989) · [Xonotic CSQC startup](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L461) · [Xonotic replication](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/replicate.qh#L74) · [Xonotic per-player receiver](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/cmd.qc#L804)

The pinned autojoin gate checks observer time, ping and join rules. Mode 2 reads the current replicated field when it scores spawnpoints and checks whether to spawn beside a teammate. Native keeps the same default and post-connect replication path. [Xonotic autojoin gate](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L2708) · [Xonotic mode-2 hooks](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/sv_spawn_near_teammate.qc#L22)

A pinned DarkPlaces client on Stormkeep TDM sent the stock opt-in at simulation time 6.7782, before it autojoined at 7.0126. A second run suppressed only that automatic CSQC send in the disposable fixture. The server autojoined the client with the field false at 7.0126 and reported it playing on Red. The original console handler then set the field to true at 8.4813 and back to false at 9.1220. This controlled delivery delay confirms that either order is possible. The original server probes only read the preference around autojoin and the per-client cvar update. [Pinned original runs](ordered-gameplay-council/pinned-mode2-autojoin.txt)

The current worktree rebuilt successfully. All 28 tests in `CvarReplicationTests` and `OrderedAuditGameplayTests` passed under .NET 8. The mode-2 case covers a spawn with the default false value, separate player updates and cleanup after disconnect. [Mode-2 test](../../../tests/VortexArena.Tests/CvarReplicationTests.cs)

## Parallel implementation leads

| Area | Concrete discrepancy | S1 evidence |
|---|---|---|
| View location | The server clips non-box camera regions against their brush planes. Both clients matched the original on entry and exit, including a followed player standing in an empty bounding-box corner. Free-aim presentation and combined snap flags remain open. | [Paired frames and setup](../view-location/README.md) |
| Electro | QC ages owner-scoped orbs by creation time and preserves age on replacement. The follow-up tracks native launch order independently of the detonation schedule. | [QC comparison](S1-REVIEW.md#17-commoneffectsqcrubbleqc) |
| Spawn Near Teammate | Mode 2 uses each player's replicated opt-in in QC. Native now sends the client preference on connect and change. Tests exercised two callers with opposing settings and disconnect cleanup. | [QC comparison](S1-REVIEW.md#88-commonmutatorsmutatorspawn_near_teammatespawn_near_teammateqh) |
| Guide | The native catalog now includes the previously empty topics and the pinned descriptions for the audited modes, Dodging, InstaGib and NIX. | [QC comparison](S1-REVIEW.md#19-commongametypesgametypeassaultassaultqc) |
| Join notices | Configured QC notices use delayed pipe-separated lines and a countdown overlay. The follow-up routes the join cvars to a timed HUD overlay after map load. | [QC comparison](S1-REVIEW.md#8-commonnet_noticeqc) |
| Hitplots and weaponstats | The native server now writes selected clients’ rewound hit coordinates and a separate optional local weapon matchup report. Separate ENet peers produced separate plot files. A two-map weaponstats run appended one report per map. | [Hitplots](S1-REVIEW.md#173-serverweaponshitplotqc) · [Weaponstats](S1-REVIEW.md#176-serverweaponsweaponstatsqc) |
