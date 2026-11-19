# Race start grid and checkpoint respawn

Rows `race.spawn.grid_respawn`, `race.qualifying.mode` and `race.qualifying.transition`. Source PIN `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`, verified with `git -C /tmp/vortex-parity-base rev-parse HEAD`.

## PIN trace in file and source order

| File under `qcsrc` | Relevant behavior |
| --- | --- |
| `common/gametypes/gametype/race/sv_race.qc` | Lines 54–69 select the score schema. Lines 199–226 reset race records and rank individual scores before converting qualifying mode 2 to Race and restoring match limits. Racers without a fastest result receive place 0. Lines 229–284 prepare joins and observers, consume the place on successful spawn, record the spawn anchor and retract on a race respawn. Lines 294–299 force respawn on death. Lines 360–439 connect qualifying score resolution, warmup suppression and limit selection to that lifecycle. |
| `lib/spawnfunc.qh` | Lines 107–153 set the entity classname to the called spawn function's name. Thus calling `spawnfunc_info_player_deathmatch` from `info_player_race` produces a canonical deathmatch spawn edict. |
| `server/cheats.qc` | Lines 533–598 generate authored entity data with place `-1` for qualifying, `0` for start-line respawn, positive places for the start grid, and target names for checkpoint links. |
| `server/client.qc` | Lines 590–609 choose before placing a player. Lines 814–821 invoke `PlayerSpawn` and suppress the spawn target in Race. Lines 871–890 invoke `PutClientInServer` before selecting the live-player or observer path. |
| `server/command/cmd.qc` | Lines 607–627 admit `ready` during qualifying mode 2, toggle the active racer flag and run the ordinary human-ready majority. |
| `server/command/vote.qc` | Lines 350–426 clear scores when a real reset is still at or before game start. The global hook ranks racers, the map objects reset, and active players spawn on the grid. Lines 432–545 start the ten-second countdown with reset at entry by default or reset at exit with `sv_ready_restart_after_countdown`. |
| `server/race.qc` | Lines 174–180 wrap the checkpoint sequence. Lines 782–847 record the last checkpoint entity and ordinal and ignore a repeat of the checkpoint just cleared. Lines 920–967 schedule the verifier's checks for each checkpoint respawn, the qualifying start and every start-grid place. Lines 1055–1083 evaluate a spot through the checkpoint it targets. Qualifying uses checkpoint 0 and the lowest place, including 0 if there is no negative place. Race filters on the last checkpoint ordinal, gives a previous spawn or matching checkpoint a priority bonus of 50, and uses the highest grid place for an unranked or out-of-grid racer who has yet to cross checkpoint 0. Once started, checkpoint 0 respawns use place 0. Lines 1086–1139 register the checkpoint from `cnt`, install touch/use and defer verification until targets are available. Lines 1220–1248 reset or retract progress, then register race spots and update the place bounds. |
| `server/scores.qc` | Lines 303–327 clear match score columns and preserve skill. The strict comparator at 415–437 uses the player ID as the last tie-break. Lines 745–800 assign grid order from that comparator. |
| `server/spawnpoints.qc` | Lines 117–196 relocate, activate and link spawn edicts. Lines 239–301 reject untargeted spawns when race spawn edicts exist and apply the target evaluator. Lines 358–443 select from the remaining spots and retain the emergency pass with target evaluation disabled. |
| `server/spawnpoints.qh` | Line 12 defines the priority bonus as 50. |
| `server/world.qc` | Lines 1781–1809 gate the automatic qualifying restart on at least one ready racer or two with recorded lap scores and route through `ReadyRestart(true)`. |

The verifier's `pl_race_checkpoint` and `pl_race_place` are locals. Its `Spawn_FilterOutBadSpots` calls pass the unchanged checkpoint edict as the player argument. The live evaluator reads the player fields and concludes that the verifier is an unstarted racer requesting the last grid slot at checkpoint 0. The qualifying branch changes `g_race_qualifying` and checks the lowest qualifying start. Native validation evaluates those two effective requests. An ordinary untargeted deathmatch spot can satisfy both when no Race spawn marker exists. A numbered checkpoint schedules validation. Race spawn markers alone do not schedule it.

## Native consumers

- `NetGame` passes the BSP and any entity overlay into `GameWorld.Boot`. The map metadata and rotation are consumed independently. The existing map-local match cvars and `Race.MapLapLimit` retain their mapinfo precedence.
- `Race.RegisterMapCheckpoint` retains the spawned map edict with its `cnt`, target name, outgoing target, flags and volume. `Race.RegisterRaceSpawn` canonicalizes the authored spawn. `GameWorld.Boot` runs verification after map spawn and deferred target linking, before a client spawn or network listener. The verifier evaluates the Race and qualifying start requests regardless of the active qualifying cvar. It neither changes that cvar nor clears any records.
- `GameWorld.ResetMap` ranks the roster through `Race.PrepareStartGrid` and respawns active racers through `ClientManager.Spawn`. Qualifying expiry uses the same reset path. A real countdown reset clears the scoreboard before the Race hook reads the qualifying score schema. `SpawnSystem.ScoreSpot` handles eligibility, weighted pick and placement through its usual path. `Race.OnPlayerSpawn` consumes the grid place and remembers the actual map spawn. `Race.OnDeath` forces the normal respawn loop and expects recrossing the checkpoint. A forced engine spawn point retains its existing priority in `SpawnSystem`. The emergency pass disables target evaluation while keeping the Race spawn target requirement, and a successful untargeted fallback resets race progress.
- Inline checkpoint bounds now supply the native Race bot route and waypoint centers. The engine's existing trigger area test uses linked bounds. Exact non-box brush overlap remains outside this spawn audit.
- ADR-0001 sets the native gameplay fidelity contract. ADR-0007 puts parsed gameplay state on typed entities and keeps shared logic Godot-free. ADR-0009 supplies the trace/entity facade used for placement. ADR-0016 treats the mounted map packs as content build output. ADR-0019 keeps the QuakeC VM on the legacy client path. The native implementation belongs in the ordinary C# server rules and map-spawn entry points. ADR-0005 and ADR-0011 retain native ENet and the native protocol. The two probe servers therefore run independently with the same BSP and settings.

## Verification

Godot host build passed with `dotnet build VortexArena.csproj --no-restore -v:q`. The shell sandbox failed before command startup with `bwrap: setting up uid map: Permission denied`; the permitted workspace read/build route ran the commands. The checkout's target is .NET 8 and this environment has only runtime 10.0.15. The test host required `DOTNET_ROLL_FORWARD=Major`.

```sh
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~RaceGridSpawnTests|FullyQualifiedName~RaceOvertimePenaltyTests|FullyQualifiedName~MapInfoGenerationTests|FullyQualifiedName~CtsSpawnTests|FullyQualifiedName~SpawnSystemAntiAbuseTests|FullyQualifiedName~RaceRecordsTests|FullyQualifiedName~CountdownAnnouncerTests'
bash planning/parity/race-grid/probe.sh /path/to/Godot_Mono 28223
```

98 focused tests passed. Eighteen Race grid and qualifying cases use native game boot, client join, qualifying expiry and ordinary forced-respawn ticks. They cover ranked placement when scores survive reset, slot consumption, unranked and late joins, observer handling, checkpoint branch selection, accepted holes and the emergency fallback, both fatal start branches, and DM excluding Race entities. The qualifying cases drive valid checkpoint touches, the two-finished gate and both restart boundaries, ready commands above and below the majority, ready-free terminal cases and timed overtime followed by sudden death. One fixture seeds a persistent map record and verifies retention alongside restoration of time, frag and lead limits. A boot fixture verifies selecting the authored team lap cap and disabling an authored qualifying clock with override zero. The tests use synthetic course entities and invoke registered checkpoint touches directly. They advance the world clock through restart and terminal conditions.

The Godot 4.6.3 Mono dedicated probe loads real BSPs and adds two bots using the server console. The script checks both actual join picks against the entity ID at the authored last-place coordinates. It discards each client's initial observer-camera pick.

| Installed map | Actual join spot for both bots | Authored last place and origin |
| --- | --- | --- |
| `leave_em_behind` | #14 | 5 at `-1280 -784 128` |
| `stormkeep` | #170 | 8 at `1184 -704 -8` |
| `courtfun` | #601 | 8 at `-256 -1088 344` |

## Paired map-boot comparison

Both hosts used the installed `leave_em_behind.pk3` (SHA-256 `31300fcbef4470afd81497665831095755808b0579d7cbd357f3677761a75ff9`). Stock used PIN `progs.dat` (`c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7`) and the 2023 DarkPlaces dedicated executable (`35b40b973d7e83d75bab026b2707bf80660840421b1e12fcfe0e9c85cb9633ea`). The compiler log at `/tmp/va-paired-run/qc.log` records the QC build and watermark. The probe generates loose entity overlays from the installed BSP lump and gives both hosts the same bytes.

```sh
bash planning/parity/race-grid/overlay-probe.sh /path/to/Godot_Mono   /path/to/xonotic-linux64-dedicated /tmp/vortex-parity-base /path/to/compiled.pk3dir 28239
```

| Scenario | PIN DarkPlaces | Native Godot |
| --- | --- | --- |
| Intact map | Reaches post-config | Opens listener |
| Remove only the spawn targeting `trigger_race_checkpoint2` (checkpoint 1) | Accepts overlay, reaches post-config | Accepts same overlay, opens listener |
| Retarget the place-5 spawn to `missing-start` | Fatal, reports checkpoint 0 and place 0 for Race respawn | Fatal before listener, same effective branch |
| Retarget the place-0 spawn to `missing-start` | Fatal, reports checkpoint 0 and place 0 for qualifying | Fatal before listener, same effective branch |
| Keep checkpoints and replace spawn markers with an ordinary untargeted deathmatch spot | Reaches post-config | Opens listener |
| Keep checkpoints and remove all player spawn markers | Fatal on Race respawn check | Fatal before listener on the same check |

## Qualifying lifecycle and headless pair

`leave_em_behind` authors a 10-minute Race and a 5-minute qualifier. The modern `gametype rc` entries for `stormkeep` and `courtfun` have no authored qualifying key. `race.qh:11–25,45` supplies the legacy 20/5/7/15/0 defaults and restores the shipped qualifier cvar default of 0 before parsing an authored key. The dedicated qualifier override selects any nonnegative value, including 0 to disable qualifying. The two-phase setup saves the effective main limits and uses zero active frag and lead limits during qualifying. The map-local record namespace remains the same.

At expiry, `world.qc:1783–1806` starts `ReadyRestart(true)` when any active racer is ready or at least two active racers have a fastest result. The ready command can also trigger an earlier restart through the ordinary human-ready majority. With neither expiry condition, the PIN proceeds to normal overtime resolution. An empty or decisive qualifying standing can end the match at expiry. Tied active standings can extend the timer and enter sudden death. A tied sudden-death expiry ends without a winner.

The real restart clears the match scoreboard before the Race hook sorts it. Consequently both timed restart options use the unranked last grid slot after that clear. The hook still sorts the available scores on a score-preserving reset. Only map-local running state is cleared. The saved main time, score and lead limits replace the qualifying limits, and the persistent record database survives. The native map boot also clears stale racers from the shared gametype instance so earlier matches cannot shift a later grid. PIN Race keeps runners on the track after a valid qualifying lap. The finish-triggered kill hook is registered by CTS.

```sh
bash planning/parity/race-grid/qualifying-probe.sh /path/to/Godot_Mono   /path/to/xonotic-linux64-dedicated /tmp/vortex-parity-base /path/to/compiled.pk3dir 28303
```

With `leave_em_behind`, a one-minute qualifier override, no players and no startup delay, both dedicated hosts reported an active timelimit of 1. PIN DarkPlaces completed the match and reloaded the map. Native reported `:gameover` and intermission, with the qualifying limit still active. Both headless processes used `slowmo 6` to accelerate the wall-clock run. After the host reboot, the DarkPlaces executable and rebuilt PIN bytecode matched the earlier SHA-256 values above. All six paired map-boot cases passed again.

The automated bot-ready attempt did not produce a standing ready racer in either dedicated server. The successful ready and two-finished paths and both countdown boundaries are verified by game-world tests. The live grid probe covers unranked joins on shipped maps. Full paired laps, remote-client presentation, the independent-player collision and damage policy, and CTS/DeFRaG placement are outside this verification. Native match configuration retains support for fractional minutes. The paired run uses a whole-minute value as supported by the PIN limit setter. No map pack or coordinated metadata diff was edited.
