# Gameplay-earned records

The original Xonotic data checkout resolved to `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. VortexArena ran a Godot 4.6.3 Mono build of the current working tree, based on `43e4b7d91db9267916500e5fbea85414d2d279a6`. Each mode began with an empty disposable server profile. The server generated all record rows through gameplay. Separate clients connected over loopback, with cheats, public listing and stats endpoints disabled.

## Native results

| Gameplay | Result |
| --- | --- |
| Race | Walked through checkpoint 0, checkpoint 1 and the flagged finish on Leave’em Behind. The first lap ranked at **0:01.00** with a **360 qu/s** speed award. The remote ladder returned **110 points**. [Lap](01-race-lap.webp) · [After restart](02-race-after-restart.webp) |
| CTS | Completed the same ordered gates in CTS. The finish ranked at **0:01.00** with a **360 qu/s** speed award. The remote ladder returned **110 points**. [Finish](03-cts-finish.webp) · [After restart](04-cts-after-restart.webp) |
| CTF with name consent withheld | Took Blue’s flag and walked it back to Red’s base, with `cl_allow_uidtracking 1`, `cl_allow_uid2name 0` and `g_ctf_leaderboard 1`. The separate fastest-capture report showed **9.70 s** and the fixture nickname. The ranked table and UID-to-name mapping stayed empty. [Pickup](05-ctf-flag-pickup.webp) · [Reports](06-ctf-name-withheld.webp) |
| CTF with both settings enabled | A different client with both cvars set to `1` captured in **8.03 s**, improving the separate fastest capture and earning **first place** and **100 ladder points**. [Capture](07-ctf-named-capture.webp) · [After restart](08-ctf-after-restart.webp) |

Each readback used a different dedicated server PID and a new graphical client profile. The client sent `cmd records 1`, `cmd rankings` and `cmd ladder` without joining gameplay. The raw native snapshot for each mode was byte-identical across the readback restart. [Filtered server and client output](native-run.txt) · [Record rows with fixture UIDs redacted](native-records.txt)

## Original comparison

The same map geometry and checkpoint overlay produced one Race lap and a CTS finish in the pinned original. The original CTS HUD showed checkpoint 1 at **0:00.47** and finish at **0:00.95**. The local original runtime lacked `DP_CRYPTO`, so it supplied no player ID for a named ladder record. [Original Race lap](09-original-race-lap.webp) · [Original CTS split and finish](10-original-cts-split-and-finish.webp)

In the original CTF run, the client carried Blue’s flag to Red. It earned a **3.05 s** separate fastest capture with name consent set to `0` and no player ID. There was no ranked row. A new original server process returned the saved capture through `cmd records 1` and rewrote a byte-identical `server.db`. The run timings reflect each client’s input cadence and route pacing. [Original capture](11-original-ctf-capture.webp) · [Original readback](12-original-ctf-after-restart.webp) · [Original transcript and database rows](original-run.txt)

Pinned CTF writes the separate fastest capture before applying the ranked consent check. The named original ladder path was compared in source, including both consent requirements and the identity check. [Pinned CTF capture path](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/ctf/sv_ctf.qc#L113) [Pinned ranked write gate](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/race.qc#L373)

### Confirmed command differences

- **Empty ladder reply.** After a separate fastest capture with no ranked entries, native returned `No ladder on this server!`. The original returned the `Top 30 ladder rankings:` header and an empty table, including after restart. Pinned `getladder()` always constructs the table header before checking whether the output string is empty. [Pinned ladder rendering](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/getreplies.qc#L181)
- **Record reply refresh.** Native `cmd records 1` showed the earned fastest capture immediately. The original immediately displayed its cached empty startup reply, then returned the capture after restart. The in-process original database had already recorded the capture by clean shutdown. [Pinned startup reply construction](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L1010) [Pinned command dispatch](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/common.qc#L565)

## Repeat the fixture

The scripts use Linux, `ss`, Xvfb, xdotool, ImageMagick, Python 3, a C compiler and the repo’s restored Mono build. Use a Godot Mono executable matching the checkout’s project version. [fixture.py](fixture.py) preserves only the existing BSP worldspawn and adds ordinary map entities. Race and CTS use the same spawn and three checkpoint volumes. CTF places a team spawn by each end and the flags on the same walkway. The map package stays intact. [loopback.c](loopback.c) binds the game and native discovery ports to loopback and rejects non-loopback destinations at the socket calls used by the harness.

From the repository root, start with a new scratch directory.

```bash
probe=planning/parity/record-lifecycle/earned
export XONOTIC_SOURCE=/path/to/pinned/xonotic-data.pk3dir
export GODOT=/path/to/Godot_mono
export EARNED_RUN
EARNED_RUN=$(bash "$probe/prepare.sh" "$XONOTIC_SOURCE")
export LP_NUM_THREADS=2

bash "$probe/native.sh" rc Race-Lap
bash "$probe/native.sh" rc Race-Readback 0 0 read
bash "$probe/native.sh" cts CTS-Course
bash "$probe/native.sh" cts CTS-Readback 0 0 read
bash "$probe/native.sh" ctf CTF-EarnedPrivate 1 0
bash "$probe/native.sh" ctf CTF-EarnedNamed 1 1
bash "$probe/native.sh" ctf CTF-EarnedReadback 0 0 read
```

The native harness joins through the on-screen button. In CTF it selects Red and presses jump to respawn after the team change. Actual movement uses W to run forward and S to return to the Red flag. The readback phase runs only client commands. Input hold durations may need adjustment on a machine with a different render cadence. The snapshots and screenshots appear under `$EARNED_RUN/<run-label>/`. Keep raw local identity files and logs in the temporary directory.

To repeat the comparison, supply original DarkPlaces dedicated and SDL executables and the matching `progs.dat`, `csprogs.dat` and `menu.dat` directory compiled from the pinned data checkout. The original capture arm uses `g_forced_team_otherwise red` so a lone client can join Red with `cmd join`.

```bash
export DARKPLACES_SERVER=/path/to/darkplaces-dedicated
export DARKPLACES_CLIENT=/path/to/darkplaces-sdl
export XONOTIC_BYTECODE=/path/to/pinned/compiled
bash "$probe/original.sh" rc Stock-LocalRace
bash "$probe/original.sh" cts Stock-LocalCTS
bash "$probe/original.sh" ctf Stock-Capture
bash "$probe/original.sh" ctf Stock-Readback read
```
