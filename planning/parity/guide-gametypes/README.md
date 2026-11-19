# Guide gametype descriptions in Simplified Chinese

Ordered audit rows 19–23 and 25–28. The stock client loaded Xonotic data at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` and MENUQC built from that pin. Native ran the worktree's Godot 4.6.3 Mono Debug build. Both opened the framed Guide at 1100 × 700, selected the Gametypes topic and selected the same entries in audit order. The frames show the actual client windows. [Earlier Guide selection and catalog evidence](../deferred-audit/guide-evidence.md)

| Row | Entry | Observed text and layout | Paired frames |
|---|---|---|---|
| 19 | Assault | Both describe the second-round role swap, the first attackers' time as the new deadline, and the objective-health waypoints. The three paragraphs use the same line breaks. | [Stock](stock/19-assault.webp) · [Native](native/19-assault.webp) |
| 20 | CTF | Both explain the home-flag requirement, dropped-flag return and the radar and waypoint reveal at stalemate. The selected name is substituted in the first and fourth paragraphs, with matching line breaks. | [Stock](stock/20-ctf.webp) · [Native](native/20-ctf.webp) |
| 21 | CTS | Both show `CTS 竞速` and `竞速` in the comparison to Race, with the no-laps rule and solo-play paragraph. The native wrap fix keeps the `CTS` prefix beside the rest of its name at the start of the comparison, as stock does. | [Stock](stock/21-cts.webp) · [Native](native/21-cts.webp) |
| 22 | Domination | Both describe neutral starting points, capture by walking through them, and points scored periodically alongside frags. The three paragraphs use the same line breaks. | [Stock](stock/22-domination.webp) · [Native](native/22-domination.webp) |
| 23 | Duel | The Chinese introduction uses the Duel substitution twice and Deathmatch once, matching the pinned positional format. Mega Health renders red, Mega Armor renders green, and the rest of the paragraph returns to white. The final paragraph shows `XPM（Xonotic 专家模式）` in both clients. | [Stock](stock/23-duel.webp) · [Native](native/23-duel.webp) |

The description text was checked against the pinned `describe` callbacks in `qcsrc/common/gametypes/gametype/{assault,ctf,cts,domination,duel}/`, with localized inputs from `common.zh_CN.po`. This locale exercises repeated positional substitution in Duel and separate mode-name arguments in CTS. The five Chinese descriptions contain no argument placement with the second argument appearing before the first. The four non-Duel entries use white for the substituted mode names.

The initial native captures showed `CTS` isolated on a line and Duel's Chinese XPM explanation missing. Those two diagnostic frames used the wider native development entry point. The final paired frames above use the framed Guide. The native wrapper now follows the pinned `take_wrapped_line_until` CJK break opportunities, including keeping a character with following closing punctuation. The Chinese XPM expansion is scoped to Duel's Guide sentence, where stock passes its separately translated XPM argument. These changes preserve the approved remembered selection, empty-filter handling and catalog order. [Before CTS](before/21-cts.webp) · [Before Duel](before/23-duel.webp) · [Guide registry](../registry/menu-core.yaml)

## Rows 25–28

| Row | Entry | Observed text and layout | Paired frames |
|---|---|---|---|
| 25 | Freeze Tag (冰封战) | Four matching paragraphs cover round elimination, passive and teammate thawing, enemy damage accelerating thaw, the ten-second grace period, and team communication. The introduction substitutes the localized mode name. | [Stock](stock/25-freezetag.webp) · [Native](native/25-freezetag.webp) |
| 26 | Invasion (侵袭) | Two matching paragraphs describe competition for monster kills, players being unable to damage one another, and round completion when all monsters die or the timer ends. The introduction substitutes the localized mode name. | [Stock](stock/26-invasion.webp) · [Native](native/26-invasion.webp) |
| 27 | Keepaway (驱离) | Four matching paragraphs cover holding the ball, scoring for kills while carrying or killing the carrier, drops, and the unattended ball teleport. The localized mode name appears in the first and last paragraphs. | [Stock](stock/27-keepaway.webp) · [Native](native/27-keepaway.webp) |
| 28 | KeyHunt (钥匙猎取) | Three matching paragraphs describe collecting every key, random carrier selection after ten seconds, radar visibility, scoring, and passing a key. The first two paragraphs substitute KeyHunt and the third substitutes CTF (夺旗战). | [Stock](stock/28-keyhunt.webp) · [Native](native/28-keyhunt.webp) |

The four highlighted entries and their complete descriptions match the pinned gametype callbacks and Chinese catalog. Each mode-name substitution renders white and each paragraph has the same visible line breaks. These callbacks use one argument per formatted paragraph, so the four pairs exercise localized names and the CTF cross-reference. The Duel pair above covers repeated arguments and colored items. Rows 25–28 cover the Guide at the recorded locale and viewport. No menu code changes were needed in this pass.

## Reproduction

Use Xvfb, Xdotool, ImageMagick, a C compiler, the pinned checkout and compiled QC overlay, and the local Godot Mono executable. From the worktree root:

```sh
dotnet build VortexArena.csproj --no-restore
bash planning/parity/guide-gametypes/record.sh original
GODOT=/path/to/Godot_v4.6.3-stable_mono_linux.x86_64 \
  bash planning/parity/guide-gametypes/record.sh native
```

The script prints its capture directory and uses a fresh profile for each client. Stock uses its local `menu_cmd directmenu guide` command and native clicks the Main Menu Media tile. After the initial five rows, it clicks Freeze Tag and Invasion. It then focuses the empty entry filter and presses Down once per selection to bring Keepaway and KeyHunt into view. It sets `zh_CN` before starting the clients and blocks Internet sockets and hostname lookups for the client processes. By default, it reads the pinned checkout from `/tmp/vortex-parity-base` and the local stock SDL executable and pinned QC overlay from `/tmp/va-reviewed-stock`. `XONOTIC_PIN` and `STOCK_DIR` override those paths. [Stock input hashes](stock/inputs.txt) · [Native input hashes](native/inputs.txt) · [Frame hashes](SHA256SUMS) · [Capture script](record.sh)

The native build succeeded and 75 focused Guide, menu-data, PO-catalog and menu weapon-order tests passed during the initial audit with `DOTNET_ROLL_FORWARD=Major`. The follow-up runs used the identical engine, native build and catalog hashes. The layout review compared wrapping and color runs visually at the recorded viewport and locale, with the stock and native backgrounds and rendering kept as captured.
