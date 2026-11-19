# Native records, rankings and ladder commands

Reference checkout `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`.

| Command | Pinned behavior | Native source |
| --- | --- | --- |
| `records [page]` | The Race and CTS hooks print each eligible map’s first ranked time and holder with clock formatting. The CTF hook prints the separate fastest-capture time to two decimal places and its saved name. It runs with the CTF leaderboard off. The ten pages index 200 catalog positions each, including maps with no record. A populated page is selected by its one-based number. Omitted, invalid, or empty pages print every populated page. | `CommandReplies.GetRecords` reads the sorted native VFS catalog, filtered for the active mode. Native rotation uses the same filter. |
| `rankings` | Reads up to 99 ranks on the current map using the world’s record type. CTS and CTF set their own types and the world defaults to Race. | `CommandReplies.GetRankings` reads the live record table. |
| `ladder` | Iterates the eligible maps, aggregates ranks 1–10 by identity, awards floor(100 / rank) and adds 10 for each map speed winner. CTS uses CTS speed. Other modes use Race speed, including when CTF provides the ranked captures. The top 30 keep catalog encounter order for equal totals. | `CommandReplies.GetLadder` reads rank and speed tables and resolves saved holder names. |

Pinned sources are `server/world.qc:961–962,1022–1038`, `server/command/getreplies.qc:35–230`, `server/command/common.qc:564–590`, and the `GetRecords` hooks in `sv_race.qc:331`, `sv_cts.qc:315`, and `sv_ctf.qc:2625`.

Native reports read the current database on request. The pin caches replies during world initialization and updates selected caches on record changes. Native keeps its existing empty-ladder message. The pin’s unconditional table header makes its empty-message branch unreachable. Native catalog selection follows the existing native MapInfo and rotation rules.

## Native verification

A fresh dedicated process for each mode used a seeded TSV in a temporary profile. Race and CTS on Leave Em Behind reported both Leave Em Behind and Courtfun and gave their respective holder 200 ladder points. CTF on Implosion reported both Implosion and Running Man CTF with `g_ctf_leaderboard 0`. Seeded fastest captures for mounted non-CTF Darkzone and the unmounted fixture were absent. `rankings` displayed the two saved Implosion ranks. The CTF ladder gave Ranked Alpha 210 points for two first-place captures and one Race speed award. A separate rendered native client joined Implosion over ENet and received the same three reports from the in-game console.

- [01 Joined CTF player console on Implosion](01-ctf-player-console.webp)
- [Dedicated Race, CTS and CTF output with the remote CTF transcript](native-run.txt)

A second native ENet run seeded oversized holder fields to force a reply over the 65,535-byte print-string limit. The client received both tails, all 68,000 repeated characters, and the Unicode character placed across the split boundary. The console displays exceptionally long single lines across multiple prints.

The focused suite passed 155 tests. The added regressions cover the 200-map boundary, the tenth page and its upper bound, empty and invalid page selection, per-mode time and column formatting, the CTF fastest-capture distinction, cross-map totals and CTS speed, and the rank/holder limits and tie order. Profile loading and restart evidence is indexed under [native local record lifecycle](../record-lifecycle/README.md).

## Hosted map catalog

| Path | Selection and freshness |
| --- | --- |
| Pinned `lsmaps` | World initialization filters the mounted MapInfo list by mode, required features and listing flags, then caches the formatted reply. A successful gametype switch refreshes it. Race and CTS mark maps with no rank-one time. CTF marks maps with no separate fastest capture. Earning a result mid-match leaves the marker cached. |
| Pinned `printmaplist` and edits | The world-initialized reply walks the explicit `g_maplist` using `MapInfo_CheckMap`. `maplist add` checks BSP existence and prepends, including duplicates. `cleanup` checks mode and features and keeps matching entries in order. Neither edit refreshes the cached reply. `Maplist_Init` and gametype reset can write the eligible catalog into the cvar. |
| Native hosted catalog | The dedicated console, listen console and remote read-only requests use the host VFS and world cvars. `lsmaps` rebuilds only its own reply, caps the display at 250 and uses the current record table. `printmaplist` rebuilds the cvar/file/catalog rotation view and retains native paging. Explicit edits use the cvar. The mounted fallback stays in memory. |
| Startup config and vote handoff | The native startup interpreter registers `maplist` before `server.cfg` and uses the requested boot mode. The listen host routes later edits to the authoritative store. A successful gametype vote selects the catalog for the forthcoming map ballot and rotation, while the running world keeps its gameplay mode until changelevel. A different mode with an empty eligible catalog leaves the current selection in place. |
| Offline selection | `lsmaps [mode]` lists mounted maps without server record markers. Its optional mode uses the same metadata and feature predicate. Create Game and Instant Action apply menu visibility flags, and Instant Action further requires a bot waypoints file. An empty Instant Action pool stops selection and logs a warning. |

Pinned sources were read from the local tree at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The relevant paths are `common/mapinfo.qc:134–209,1472–1507,1655–1678`, `common/command/generic.qc:251–330`, `server/intermission.qc:238–316`, `server/world.qc:961–962,1022–1038`, `server/command/common.qc:504–540`, `server/command/getreplies.qc:233–293`, and `server/mapvoting.qc:835–894`. The pin's `rankings_reply` additionally refreshes on Race ranking writes and removals in `server/race.qc:436,482`. Records pages and ladder use world-initialized replies. Native reads those reports on request.

The paired dedicated hosts on Implosion initially agreed on the filtered CTF rotation, rejected a missing BSP, and kept the same eligible entries after cleanup. Native loaded that rotation through `server.cfg` and prepended Implosion using the command in the cfg. Both hosts then accepted a quoted, installed duplicate. The pin's `printmaplist` still showed its boot list. Native showed the duplicate immediately. The original fixture had two eligible CTF maps and the native install had nine. The pin prints full help after rejecting a missing map. Native uses its compact usage line. [Paired console output and hashes](map-catalog.txt)

A second case started an explicitly requested Darkzone with an authored DM sidecar lacking the weapons feature. Both hosts warned and continued loading with melee-only and pickup items active. In the native offline console a separate armed/unarmed BSP pair confirmed that `lsmaps dm` applied the same feature gate. Disabling melee immediately admitted the unarmed map. A nested prefab BSP stayed out of both lists. [Feature and offline evidence](map-catalog.txt)

An actual CTF capture on Leave Em Behind confirmed the live marker difference. The pin retained the asterisk after the 4.08 s capture. The native dedicated and ENet client replies dropped it after a 5.08 s capture in the same match. [Capture excerpts](map-catalog.txt)

A native listen server with an authenticated loopback client ended a DM match on Darkzone. Its single custom gametype option selected CTF and the configured Running Man CTF rotation. The startup-defined vote hook set a marker cvar, the server rehosted on Running Man CTF, and its catalog listed CTF maps. [Listen handoff evidence](map-catalog.txt)

The pin can retain a queued map through the gametype ballot and dim unsupported modes. Native honors its explicit queued-map override before starting that ballot. Native also resolves `maplist.txt` between the explicit cvar and installed fallback, and keeps exact map names for its vote-adjacent commands. The managed gametype-switch tests cover an accepted custom alias and a rejected empty destination catalog. The offline runtime test drives the console; Instant Action's waypoint selection was source-checked.

The native Debug build and 240 focused tests passed. An earlier full suite run passed all 6,283 tests on rerun. The two migration tests that failed in the first run also passed in isolation.

### Repeat

Build the Debug Godot project and run the paired probe from the repository root. The optional offline check also requires Xvfb and xdotool. The script prints the location of the full logs.

```bash
dotnet build VortexArena.csproj --no-restore
python3 planning/parity/record-reports/map-catalog-probe.py \
  --source "$XONOTIC_SOURCE" --bytecode "$XONOTIC_BYTECODE" \
  --original "$DARKPLACES_SERVER" --godot "$GODOT" --offline
```

For the capture observation, follow the CTF earn runs with fresh profiles in the [earned-record fixture](../record-lifecycle/earned/README.md). Query pinned `sv_cmd lsmaps` or native `lsmaps` on the server once it is listening. Add `cmd lsmaps` to the client reports after the confirmed capture and before ending the match. The map should lose its asterisk immediately on native and retain it on the pin.
