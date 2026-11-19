# Native scoreboard block audit

Reference source was the restored Xonotic data checkout at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The [cl-scoreboard registry](../registry/cl-scoreboard.yaml) follows the original 21-row sequence. It records source comparisons, observed behavior, and remaining differences for each row.

## Block findings

| Block | Pinned source | Native result |
| --- | --- | --- |
| Accuracy | `scoreboard.qc:1805–1953,2298–2328`, `weapons.qc:38–79` and the Race, CTS and Nexball client hooks | The icon grid and owner accuracy feed were already live. Restore the fired-stat gate and the owned-or-in-map eligibility test, including available hidden weapons. Use the eligible registry count for double rows and the configured color stops, whose defaults are 0%, 20% and 100%. Warmup, late placement, delay, intermission and mode rules gate the grid. Invasion suppresses the item grid. Its monster target restriction does not suppress the accuracy grid. |
| Item stats | `scoreboard.qc:1954–2079,2331–2361`, `common/items/all.qh`, `common/items/inventory.qh` | Successful pickups already flow through `ServerNet.CollectItemStats`, `ScoreboardBlock`, `NetGame.UpdateScoreboard` and `ScoreboardPanel.SetItemStats`. Render the presort order in the pinned preprocessed CSQC registrations (powerups, ammo, armor, health and Instagib items). Weapon pickups use a separate stream. Mask 12 hides ammo and small/medium health and armor. The units digit filters cumulative tiers for values 1–4 only. Use the registry size for double rows and apply the Race, CTS, Nexball and Invasion suppression hooks. |
| Rankings | `scoreboard.qc:2164–2295,2726–2747,2809–2819` and the three `ShowRankings` hooks | Race and CTS already have networked ranking columns and a speed award. Fit visible rows to the 95% viewport budget so the existing arrows can page records by whole columns. Clip holder text within its column, size highlights to the fitted column, and retain stock ordinal colors and holder colors. Record and speed changes can now trigger a send with an unchanged player table. |
| Map stats | `scoreboard.qc:2081–2161`, `trigger_secret`, and Invasion's server frame hook | Monster and secret labels and values were already drawn. Carry authoritative progress through the reliable native match-state message, with round Invasion using the wave counts. The server resends on a changed count and the client redraws an open panel. The suffix is optional on read for peers that omit it. |
| Appearance export | `scoreboard.qc:22–41`, `client/hud/hud_config.qc:HUD_Panel_ExportCfg` | The pinned function exports HUD skin cvars. The existing native `hud save` command includes them through the registered scoreboard prefix. The saved native block is a superset of the 15 curated keys. |

The active skin cfg resolves `minwidth` to 0.6 in both games. The QC declaration initializes it to 0.4 before the cfg loads. The audit uses shipped effective values for layout, stat gates and color cvars. Native reads the shipped ping thresholds and their editable colors. Accuracy delay defaults to 2 seconds and item delay to 2.2 seconds. Both delay gates apply below 75% of the viewport and bypass the delay in intermission. Native also checks that each complete grid fits. `dynamichud`, `others_showscore` and `spectators_aligned` are shipped cvars with incomplete scoreboard consumers.

[ADR-0005](../../decisions/ADR-0005-custom-netcode.md) and [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) explain using native snapshot and match-state messages in place of QC entity/STAT transport. [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md) keeps the DarkPlaces/QC client path separate. These decisions describe architecture. Incomplete spectator/scoreboard features remain behavioral gaps. [Native rank lifecycle](../record-lifecycle/README.md) The follow-up below covers remote weapon availability and CTF capture rankings. The immediate native rankings page budget approximates QC's animated row budget.

## Indexed gameplay captures

Godot 4.6.3 Mono, 900×560, Xvfb with llvmpipe. A dedicated `--dedicated darkzone --gametype dm` host and a separate `--connect 127.0.0.1:28698` client used independent temporary `VORTEX_USERDIR` and XDG data directories with `--no-save-config`. The server used a temporary loose `maps/darkzone.ent` built from the shipped BSP entity lump. The fixture uses a single regular spawn at `64 -616 64`, adds rockets, small/big/mega armor, mega health and a `trigger_secret` there, and leaves the BSP and network code paths in use. Physical Join, attack and `+showscores` inputs drive the screenshots. Repeated pickup counts reflect real item respawns at the fixture spawn.

| Frame | What it shows |
| --- | --- |
| [01 Join](01-join.webp) | Native remote Join/Spectate dialog after the ENet handshake. |
| [02 Gameplay](02-gameplay.webp) | Remote player after choosing Join. Armor and health have been picked up. |
| [03 Default](03-default.webp) | The held Deathmatch scoreboard after firing. Red 0% for the fired weapon, default-filtered item grid in armor/health order, and remote `Secrets found 1/1`. |
| [04 Unfiltered](04-unfiltered.webp) | The same match with `itemstats_filter 0` and `accuracy_nocolors 1`. Rocket ammo and the small armor appear in registration order and the 0% cell turns white. |
| [05 Disabled and ALL](05-disabled-all.webp) | A separate remote match using the same fixture at port 28706, with both stat-grid enable cvars set to zero and `scoreboard_columns ALL`. Map stats remain when the grids are disabled. The follow-up removes the duplicated display columns visible in this earlier ALL capture. |
| [06 Before delay](06-before-delay.webp) | Fresh remote match at port 28708 with both `showdelay_minpos` cvars set to 0 to exercise the normal delay without a full table. The board opens with map stats and no stat grids. |
| [07 After delay](07-after-delay.webp) | The same held Tab press after the delay. Accuracy and item grids appear above map stats without a new key press. |

[Run excerpt](native-run.txt) records the accepted connection and local commands. [HUD save excerpt](hud-save-scoreboard.cfg.txt) is the scoreboard portion of the actual file produced by `hud save scoreboard-review` in the temporary client profile.

## Automated verification and limits

```sh
dotnet build VortexArena.csproj --no-restore
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ScoreboardColumnsTests|FullyQualifiedName~ScoreboardUiNavigationTests|FullyQualifiedName~RaceRecordsTests|FullyQualifiedName~ClanArenaParticipationTests|FullyQualifiedName~WelcomeBriefingTests'
python3 tools/check-parity-refs.py
```

**48 focused tests passed.** The focused suite includes the nonempty and empty item-tally wire round trips, score layout and column formatting, navigation decision tests, and Race record operations. The live run verifies remote secrets; monster counts and mode-specific suppression were traced through their source and wire paths. Race lap ranking rendering, interactive team selection and spectator actions still need dedicated in-match captures. The pinned layout and behavior comparisons in this audit are source-based.

## Follow-up on layout commands and rankings

The pin registers `LocalCommand_hud` in `client/command/cl_cmd.qc:251,488` and maps the two bare scoreboard aliases in `commands.cfg:138–139`. `Scoreboard_InitScores` invokes the no-argument path. `Cmd_Scoreboard_SetFields` reads the saved cvar for that path, applies explicit specs to the active columns and writes the cvar only for `expand_default`.

Native `ConsoleOverlay` dispatches `cl_cmd hud scoreboard_columns_set/help` with the parsed arguments and registers both bare forms. The active panel holds explicit layouts until the next score initialization. Help prints supported presets, columns and filter syntax. The [native console excerpt](column-commands.txt) is evidence for the scoreboard branches of `console-commands.client.hud_group`.

The same Deathmatch fixture ran with separate temporary server/client profiles at port 28738. The client entered with `scoreboard_columns "name | score"`. Once the player fired, the remote grid showed both the fired weapon with a percentage and dim unfired weapons from the map. The server derives the map set from successful map pickup spawns after the mode/entity filters and appends it to the existing match-state suffix. The scoreboard combines it with the native owner-inventory mask. This follows the pinned `WEAPONSINMAP` stat in `common/stats.qh:59` and `server/items/items.qc:1169`.

| Frame | What it shows |
| --- | --- |
| [08 Saved layout and map weapons](08-saved-and-map-weapons.webp) | The minimal saved columns and the remote accuracy grid. Only the fired weapon has a percentage. The other map-eligible weapon cells are dim. |
| [09 Temporary ALL](09-temporary-all.webp) | A second run at port 28752 opened the death scoreboard and submitted the nested ALL command. Identity and derived columns each appear once. The direct cvar query still reports `name | score`. |
| [10 Reloaded layout](10-reloaded-layout.webp) | The bare set command reloads the saved Name and Score columns. |
| [11 Layout console](11-layout-console.webp) | Both help paths return the same syntax. The cvar query shows the original saved spec before `expand_default` and the expanded built-in spec after it. |

The native CTF follow-up uses the separate `/ctf100record/` namespace. An enabled server ranks valid normal captures by identity at centisecond precision. The existing score block carries the results. The client draws **Capture time rankings** when its own display cvar is enabled. Pinned `common/gametypes/gametype/ctf/sv_ctf.qc:113–141` uses the same gate and delegates captures over 0.01 seconds to the race ranking path. Race, CTS and CTF ranks now survive native profile rotation and restart. [Native rank lifecycle](../record-lifecycle/README.md)

The CTF capture used a temporary copy of the Darkzone entity lump in a dedicated server profile at port 28744. The fixture adds red and blue flags and named teleport destinations at two authored spawn positions. The remote player joined red, respawned after the team switch, and used the enabled native teleport command to travel to the blue pickup and back to the red base. The normal flag touch path scored the capture. A subsequent suicide opened the native death scoreboard and left the capture ranking visible. [Native CTF command and server excerpt](ctf-native.txt)

| Frame | What it shows |
| --- | --- |
| [12 CTF carry](12-ctf-carry.webp) | The remote red player carries the blue flag at the blue base. |
| [13 CTF ranking](13-ctf-ranking.webp) | The red team has one capture and the remote scoreboard displays the player's timed first-place capture below the item grid. |

The focused follow-up passed **72 tests** including scoreboard columns and navigation, RaceRecords, CTF variants and flag passing, Clan Arena participation, and Welcome briefing. The added CTF cases drive real `FlagTouch` pickup and capture transitions and verify the enabled, disabled, one-flag, too-short, and anonymous gates.

## S1 row 4 · client/hud/panel/scoreboard.qh

The reference is Xonotic data pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` with the locally built pinned QC and DarkPlaces engine. The comparison uses two real clients on each dedicated server, Running Man in Deathmatch, standard keyboard and console input, independent profiles, and a scripted camera look on Viewer. Both original clients are graphical. Native uses a graphical Viewer and a headless Zebra driven through its ordinary config command interpreter. Zebra connects second. Viewer reconnects while Zebra stays in the match, so the original server can reuse slot 1 and the native server allocates a new stable network ID.

| Case | Pinned path and native result |
| --- | --- |
| Fully tied active players | `Scoreboard_ComparePlayerScores` compares primary, secondary and remaining sortable fields before swapping a pair whose left server entity number is lower. Both native and pin now put Zebra above Viewer at zero score, including after Viewer rejoins slot 1. The native score block sends rows in ascending reusable client-slot order. The panel uses that order for the final tiebreak and keeps the stable net ID for actions and identity. Changed score and layout blocks use the reliable snapshot path so a dropped join transition cannot leave a stale spectator row. |
| Ignore ownership | The pin sends an owner-specific clientdata update, applies it to `.ignored`, and draws `player_ignored` in the active name or spectator list. Native now sends the connected ignored IDs using a reliable replacement set to that owner. The panel redraws when that set changes regardless of score version. The ordinary `ignore` / `unignore` commands still use the authoritative chat filter. Zebra's message appears in Zebra's log and is absent from Viewer's while ignored. It reaches Viewer after unignore. |
| Radar and score focus | Pinned `HUD_Scoreboard_InputEvent` precedes `HUD_Radar_InputEvent`. Living Tab dismisses clickable radar and displays held scores. A fresh unbound key also dismisses the living picker. For a dead player, pressing the score bind temporarily hides the picker, and release brings it back. Interactive Tab+Escape navigation takes key input even when a console command attempts to open clickradar. The navigation survives console focus, captures ordinary gameplay key presses, and Escape dismisses it. A slow native frame could complete the fade before the next config update and draw once with zero alpha. The scoreboard now applies its own fade directly to the global HUD alpha at draw time. |

The pinned anonymous clients can use temporary ignores. The pin's `clear_ignores` requires a stats UID and clears stored permanent ignores, so the paired reset uses `unignore`. Native `clear_ignores` clears its match-scoped list. The native ignore indicator shares the existing filter's lifetime. The pinned permanent UID database, spectator/downloader sort ordering, and team secondary totals are separate parity gaps. Radar click coordinates, Onslaught destinations, and use-triggered opening belong to the [QuickMenu/radar audit](../quickmenu-radar/README.md).

The old scoreboard spec's descriptions of a passive panel, dead fade and absent handicap are stale. Native has live gameplay paths for each. [ADR-0005](../../decisions/ADR-0005-custom-netcode.md) and [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) explain native packet and identity boundaries. [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md) describes the separate DarkPlaces/QC path. Those choices explain how the native implementation carries state. The prior missing owner icon and gameplay bind leak were native UI gaps.

Run from the repository root with a built native project, `xdotool`, ImageMagick and Xvfb. Use an unused UDP port for each mode, plus the next port for native LAN replies. Native freezes the built DLLs in the disposable directory so concurrent worktree builds cannot change the handshake hash between clients. The script accepts `STOCK_DIR`, `XONOTIC_PIN`, and `GODOT_BIN` overrides and keeps its logs and PNGs in the supplied temporary directory.

```sh
PARITY_PORT=36255 xvfb-run -a -s '-screen 0 1500x680x24' \
  bash planning/parity/scoreboard-blocks/reproduce-row4.sh stock "$(mktemp -d)"
PARITY_PORT=36317 xvfb-run -a -s '-screen 0 900x650x24' \
  bash planning/parity/scoreboard-blocks/reproduce-row4.sh native "$(mktemp -d)"
```

The original capture set shows the [initial and reconnected ties, ignore and restore](row4-stock-rows.webp), [Zebra's view](row4-stock-other-viewer.webp), and [living, interactive and dead focus transitions](row4-stock-focus.webp). The dead focus sheet uses the rebound `F6 +showscores` press and release. [Original client and server log excerpts](row4-stock-log.txt)

The native build and 254 focused tests passed. The test set covers the reusable slot across disconnect and reconnect, score columns, scoreboard navigation, chat filtering, and bind behavior. The reproduction also checks chat delivery in both client logs, the console while navigation owns input, and a gameplay bind suppressed during navigation and restored after Escape.

The native captures show [initial and reconnected ties, viewer-only ignore, unignore and clear](row4-native-rows.webp) and [living, interactive, and dead focus transitions](row4-native-focus.webp). In frame 03, headless Zebra ignores graphical Viewer. Viewer's own row stays clear. In frames 15–20, the same dead viewer holds Tab and then a rebound F6, releases each, and finally dismisses the restored picker with Escape. [Native match log excerpts](row4-native-log.txt) · [Pre-fix fade trace from an isolated listen session](row4-native-fade-trace.txt)

```sh
dotnet build VortexArena.csproj --no-restore
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ScoreboardUiNavigationTests|FullyQualifiedName~ScoreboardColumnsTests|FullyQualifiedName~ChatEngineTests|FullyQualifiedName~Bind'
python3 tools/check-parity-refs.py
```
