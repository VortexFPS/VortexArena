# Items-time mutator — parity spec

**Base refs:** `common/mutators/mutator/itemstime/itemstime.qc` · `itemstime.qh` · (callers in `server/items/items.qc`, `common/stats.qh`, `client/hud/hud.qh`, `common/mutators/mutator/waypoints/waypointsprites.qc`)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/ItemstimeMutator.cs` · `game/hud/ItemsTimePanel.cs` · `game/net/NetGame.cs` (the host feed) · `game/hud/HudManager.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Items-time is a server-driven HUD aid that shows how many seconds until "timed" pickups (Mega/Big Health,
Mega/Big Armor, the powerups Strength + Shield, and an aggregate "Superweapons" slot) respawn. The server
tracks each timed item's absolute scheduled respawn time in a small array (`it_times[]`) and pushes it to
clients over a dedicated CSQC net message; the client HUD panel (#22, `HUD_ItemsTime`) draws an icon + a
remaining-seconds countdown per item, with color/blink/checkmark/expanding-flash feedback. The mutator is
always registered (`REGISTER_MUTATOR(itemstime, true)`); `sv_itemstime` (default 1) gates the server producer,
and `hud_panel_itemstime` plus `STAT(ITEMSTIME)` (= the live `sv_itemstime` value) gate who/when the client draws.

## Base algorithm (authoritative)

### Timed-item set (`itemstime.qc:Item_ItemsTime_Allow` / `Item_ItemsTime_SpectatorOnly`)
- `Item_ItemsTime_Allow(it)` = `it.instanceOfPowerup || Item_ItemsTime_SpectatorOnly(it)`.
- `Item_ItemsTime_SpectatorOnly(it)` = `ITEM_ArmorMega || (ITEM_ArmorBig && !hidebig) || ITEM_HealthMega || (ITEM_HealthBig && !hidebig)`.
- So the tracked set is: **all powerups** (Strength, Shield, and any other `instanceOfPowerup`), **Mega Health, Mega Armor**, and (unless `hud_panel_itemstime_hidebig`) **Big Health, Big Armor**. Plus one reserved aggregate slot `REGISTRY_MAX(Items)` for **superweapons** (any item whose `STAT(WEAPONS)` intersects `WEPSET_SUPERWEAPONS`).
- The `hidebig` test is CSQC-only; SVQC compiles `hud_panel_itemstime_hidebig=false`, so the **server always tracks Big Health/Armor** and the client decides whether to show them.

### Server producer (`itemstime.qc`, SVQC)
- `it_times[REGISTRY_MAX(Items) + 1]`: per-item absolute respawn time (sim seconds). `-1` = item type not present on the map.
- `STATIC_INIT`: every `Allow`ed item + the superweapons slot init to `-1`.
- `Item_ItemsTime_SetTime(e, t)`: if `!autocvar_sv_itemstime` return; weapon-pickup superweapons write the aggregate slot, all other game items write `it_times[item.m_id] = t`.
- `Item_ItemsTime_UpdateTime(e, t)` — the core "what to display" computation:
  - `isavailable = (t == 0)`.
  - Scan all live world items (`IL_EACH(g_items)`) of the **same itemdef** (or any superweapon when `e` is a superweapon).
  - If any copy has `scheduledrespawntime <= time` → `isavailable = true` (a copy is up now).
  - Else track the **minimum** `scheduledrespawntime` across copies into `t`.
  - If `isavailable`, return `-t` (NEGATIVE encoding = "another copy is available now"); else return `t`.
- `IT_Write(e, i, f)`: per real client, `WriteHeader(itemstime) WriteByte(i) WriteFloat(f)` — the **CSQC net temp message** `itemstime`.
- `SetTimesForAllPlayers()`: send to every real client gated by `warmup_stage || !IS_PLAYER(it) || autocvar_sv_itemstime == 2` — i.e. **only spectators get times in a live non-warmup round unless `sv_itemstime == 2`**.
- `ResetTimesForPlayer` / `SetTimesForPlayer`: reset (send 0/-1) or full-sync one client (used on observer/connect/spawn).
- Hooks: `reset_map_global` (reset all times then re-set from live items), `MakePlayerObserver` (full sync to the new observer), `ClientConnect` (sync if real client + warmup/`==2`), `PlayerSpawn` (reset the spawning player's view unless warmup/`==2`).
- **Live callers** (`server/items/items.qc`): `Item_Respawn` (248-250), `Item_ScheduleRespawnIn` (344-349), `StartItem` init (1182) — each computes `UpdateTime` then `SetTime` then `SetTimesForAllPlayers`. Respawn scheduling sets `scheduledrespawntime` (lines 333/340) and uses `ITEM_RESPAWN_TICKS = 10`.

### CSQC receive + state (`itemstime.qc`, CSQC)
- `ItemsTime_time[REGISTRY_MAX(Items)+1]`: client mirror; `STATIC_INIT` → all `-1`. `NET_HANDLE(itemstime)`: `i = ReadByte(); ItemsTime_time[i] = ReadFloat()`.
- `ItemsTime_availableTime[]`: per-item wall time the item last became available (drives the expanding flash).

### Client HUD draw (`itemstime.qc:HUD_ItemsTime` + `DrawItemsTimeItem`, panel #22)
- **Enable gate** (NOT just the panel master toggle): unless `_hud_configure`, the panel draws only if
  `(hud_panel_itemstime == 1 && spectatee_status != 0)` OR
  `(hud_panel_itemstime == 2 && (spectatee_status != 0 || warmup_stage || STAT(ITEMSTIME) == 2))`.
  → With stock `hud_panel_itemstime=2` + `sv_itemstime=1`, an **alive player in a normal round sees NOTHING**; only spectators (and everyone in warmup) see it. `STAT(ITEMSTIME)` = the live `autocvar_sv_itemstime` value (`stats.qh:138`).
- **Count loop** by `hidespawned` mode: 0 = all with time != -1; 1 = `time > now || -time > now` (hide spawned but keep the post-spawn blink window); 2 = `time > now` (hide spawned, no blink). If count == 0, draw nothing.
- **Layout**: `ar = max(2, hud_panel_itemstime_ratio) + 1`; `HUD_GetRowCount(count, size, ar)` picks rows; `columns = ceil(count/rows)`. `dynamicsize` shrinks the panel to the exact `ar` cell aspect; else cells are centered in their slot and the rest spaced.
- **Per item** (`DrawItemsTimeItem`):
  - `t = floor(item_time - time + 0.999)` (seconds remaining, rounded up).
  - Number color: `t<5` red `(0.7 0 0)`, `t<10` yellow `(0.7 0.7 0)`, else white.
  - Icon alpha: `hidespawned==2` → 1; else available → `blink(0.85, 0.15, 5)` (= `0.85 + 0.15*sin(time*5)`); else 0.5.
  - Icon/number split by `iconalign` (number box = `((ar-1)/ar)*width`, icon = a `mySize_y`-square).
  - Optional progress bar (`progressbar`, only while `t>0`): fraction `t/progressbar_maxtime`, baralign = `iconalign`, `reduced` spans just the number box.
  - Text on: `t>0` → the seconds number; else a **checkmark** (`gfx/hud/.../checkmark`, with a legacy fallback that just centers the icon if the pic is missing).
  - Available flash: `availableTime` bookkeeping → `f = (time - availableTime)*2`, clamped 0..1 (0 once `f>1`); when `>0` an **expanding, fading copy** of the icon (`drawpic_aspect_skin_expanding`) overlays the icon.

### Spectator waypoint sprites (`waypointsprites.qc` + `items.qc:Item_RespawnCountdown`)
- For `SpectatorOnly` items, the respawn countdown waypoint sprite is set `SPRITERULE_SPECTATOR` and its visibility is gated by `g_waypointsprite_itemstime` (client cfg default **2**: 1 = when spectating, 2 = also playing in warmup, with `STAT(ITEMSTIME)==2`).

### Constants / cvars (Base defaults)
| cvar / const | default | side | meaning |
|---|---|---|---|
| `sv_itemstime` | `1` | authority | 0 off / 1 spectators (+warmup) / 2 also alive players. Also `STAT(ITEMSTIME)`. |
| `hud_panel_itemstime` | `2` | presentation | panel enable mode (0/1/2) — see enable gate. |
| `hud_panel_itemstime_dynamicsize` | `1` | presentation | shrink to exact cell aspect. |
| `hud_panel_itemstime_ratio` | `2` | presentation | target cell width:height (used as `ar = max(2,r)+1`). |
| `hud_panel_itemstime_iconalign` | `0` | presentation | icon left(0)/right(1) of the number. |
| `hud_panel_itemstime_progressbar` | `0` | presentation | per-item countdown bar. |
| `hud_panel_itemstime_progressbar_maxtime` | `30` | presentation | bar full-scale seconds. |
| `hud_panel_itemstime_progressbar_name` | `progressbar` | presentation | bar skin pic name. |
| `hud_panel_itemstime_progressbar_reduced` | `0` | presentation | bar spans only the number box. |
| `hud_panel_itemstime_hidespawned` | `1` | presentation | 0 show all / 1 hide spawned (+blink) / 2 hide spawned. |
| `hud_panel_itemstime_hidebig` | `false` | presentation | hide Big Health/Armor (CSQC only; SVQC tracks them). |
| `hud_panel_itemstime_text` | `1` | presentation | draw the seconds number / checkmark. |
| `g_waypointsprite_itemstime` | `2` | presentation | spectator/warmup respawn waypoint sprites. |
| `ITEM_RESPAWN_TICKS` | `10` | authority | item respawn-countdown lead used by the scheduler. |

## Port mapping
- **Timed-item set** → `ItemstimeMutator.TimedItemClasses` (classname → key map) + `ClassKeyMap()` adding `weapon_<superweapon>` → "superweapons". Faithful set (mega/big health+armor, strength, shield, superweapons). Always tracks Big items (matches SVQC `hidebig=false`).
- **Server producer / `it_times`** → `ItemstimeMutator.Recompute()` building `_times` (`CurrentTimes`). It does **not** mirror QC's event-driven `SetTime`/`UpdateTime` calls at pickup/schedule; instead it **re-scans the live world items every server frame** (`OnStartFrame`) and recomputes the min-cooldown / negative-available encoding from each item's `ScheduledRespawnTime`. The resulting table matches QC's; the recompute trigger is an intentional divergence.
- **Negative "available now" encoding** → faithful (`_times[key] = avail ? -t : t`).
- **CSQC net message `itemstime`** → **NOT IMPLEMENTED.** There is no net handler anywhere in `VortexArena.Net`. The times are fed straight from the local server mutator to the local HUD in `NetGame.cs:2090-2093`, guarded by `if (_server is not null)` — **host-only**.
- **CSQC receive `ItemsTime_time[]` / `availableTime[]`** → `ItemsTimePanel._times` / `_availableTime`, populated by `SetItemTimes`/`SetItemTime` (called only from the host feed).
- **`HUD_ItemsTime` draw** → `ItemsTimePanel.DrawPanel` + `DrawItemsTimeItem` — a faithful, exhaustive port of the count loop, `HUD_GetRowCount`, dynamic/static layout, color thresholds, blink, checkmark, progress bar, and the expanding flash.
- **Enable gate** → `ItemsTimePanel` never consults `spectatee_status` / `warmup_stage` / `STAT(ITEMSTIME)` (a grep finds only a doc-comment mention); additionally `NetGame.cs:2092` force-sets `Visible = true` whenever the mutator is enabled. The QC spectatee/warmup/`STAT(ITEMSTIME)==2` gating is absent — the panel is shown to **alive players in normal rounds**, opposite to Base.
- **`STAT(ITEMSTIME)`** (`stats.qh:138`, `REGISTER_STAT(ITEMSTIME, INT, autocvar_sv_itemstime)`) → **NOT MODELED.** No client stat carries the live 0/1/2 `sv_itemstime` tier, which is the underlying reason both the enable gate (mode 2) and the spectator waypoint gate (mode 2) cannot be reproduced.
- **`_hud_configure` preview** (`Item_ItemsTime_GetTime` returns fake times in the HUD editor: ArmorMega+8, ArmorBig+0, HealthMega+0, Strength+4) → **NOT PORTED**; the panel shows nothing in the HUD editor.
- **Per-player tiers** (`SetTimesForAllPlayers` warmup/observer/`==2` gate, `ResetTimesForPlayer`, `MakePlayerObserver`/`PlayerSpawn`/`ClientConnect` hooks) → **NOT IMPLEMENTED** as such (single local view; no per-client send).
- **Spectator waypoint sprites (`g_waypointsprite_itemstime`)** → **NOT IMPLEMENTED** (no `waypointsprite_itemstime` / `SPRITERULE_SPECTATOR` in the port).
- **`hidebig` cvar** → not registered in `ItemsTimePanel.RegisterDefaults`; the catalog always includes Big items.

## Parity assessment

### Logic
- Producer table semantics (timed set, min cooldown, superweapon aggregate, negative-available encoding) are **faithful**. The recompute is event-less (per-frame scan) — same result, different trigger → **intended divergence** (documented in the mutator's own comments).
- HUD draw logic (count, layout, color, blink, checkmark, flash) is **faithful**.
- **Enable/visibility logic diverges**: Base shows the panel only to spectators (mode 1) or spectators+warmup+`sv_itemstime==2` (mode 2). The port shows it to everyone while the mutator is on. This is a real, observable behavioral gap (alive players in a normal round see a panel they would NOT see in Base, with stock cvars).
- Per-player send tiers / reset-on-spawn / observer-sync hooks are **missing** (no networked per-client state in this single-view feed).

### Values
- All HUD behavior cvars + defaults are registered faithfully (`RegisterDefaults`), and `sv_itemstime` default 1 is in `xonotic-server.cfg`. The panel enable default (`hud_panel_itemstime=2`) is in `_hud_common.cfg` and `HudLayoutDefaults`.
- Missing: `hud_panel_itemstime_hidebig` (default false) and `g_waypointsprite_itemstime` (default 2) are not registered. `ITEM_RESPAWN_TICKS` lead-in lives in the port's item scheduler (out of this unit's producer code).

### Timing
- Countdown math (`floor(item_time - time + 0.999)`, blink, the 0.5 s `*2` expanding flash) is faithful. The producer recomputes per server frame rather than on item events; the published value is identical so the displayed countdown is correct. **Faithful** in observable timing.

### Presentation
- The draw is a thorough port (grid, aspect fit, colors, blink, progress bar, expanding flash, checkmark with fallback). Icons resolve from the same `m_icon` bare skin names.
- The enable-gate divergence is the main presentation gap (panel visible to alive players). Spectator waypoint sprites for timed items are absent.

### Audio
- `na` — itemstime emits no sound of its own (the respawn-countdown beep is owned by the item respawn system, not this unit).

### Liveness
- **Server producer + HUD feed are LIVE on the host/listen path**: `GameWorld.cs:511 MutatorActivation.Apply()` activates the mutator (default `sv_itemstime=1`), `OnStartFrame` recomputes each tick, and `NetGame.cs:2090-2093` pushes `CurrentTimes` into the panel and sets it visible. Verified by `MutatorBatchT51Tests` (publish + negative encoding + disabled).
- **DEAD for a pure remote client**: the feed is inside `if (_server is not null)`, and there is no `itemstime` net message. A client connected to a separate/dedicated server never receives item times and the panel stays empty.

### Intended divergences
- Per-frame world re-scan instead of QC's event-driven `SetTime`/`UpdateTime` — documented in the mutator; same published values, cheaper to wire. `intended_divergence: true`.

## Verification
- `tests/VortexArena.Tests/MutatorBatchT51Tests.cs:386-426` — `Itemstime_PublishesRespawnTime_ForTimedItem` (absolute time on cooldown), `Itemstime_NegativeEncoding_WhenAnotherCopyAvailable` (`-30`), `Itemstime_Disabled_NoTimes`. Confirms producer logic + negative encoding + `sv_itemstime` gate.
- Source-traced live chain: `GameWorld.cs:511` → `MutatorActivation.Apply` → `ItemstimeMutator.Hook`/`OnStartFrame` → `NetGame.cs:2090` → `ItemsTimePanel.SetItemTimes`/`Visible=true`. Host-only (guarded by `_server is not null`).
- Enable-gate divergence and the missing net message established by reading `HUD_ItemsTime` (Base) vs `ItemsTimePanel.ResolveVisible` (base default) + the absence of any `itemstime` handler in `VortexArena.Net` (grep: no matches).
- Not behaviorally run in-game for this audit (visual claims about the actual rendered panel are source-level, not screenshot-verified).

## Open questions
- Is the host-only behavior acceptable given the port's single-listen-server architecture, or is a real CSQC `itemstime` message intended for true dedicated/remote play? (Determines whether the missing net message is a gap or an intended architecture simplification.)
- Should the port reproduce the Base "spectators/warmup only" enable gate (so alive players don't see the panel in a normal round with stock cvars), or is always-on a deliberate UX choice for the port?
