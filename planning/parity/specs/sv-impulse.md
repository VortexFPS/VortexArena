# sv-impulse — parity spec

**Base refs:** `server/impulse.qc` · `common/impulses/all.qh` · `server/weapons/selection.qc` · `server/weapons/throwing.qc` · `server/client.qc:PlayerUseKey`
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/WeaponImpulses.cs` · `Inventory.cs` · `WeaponThrowing.cs` · `WeaponOrder.cs` · `src/VortexArena.Server/Commands.cs:DispatchImpulse` · `game/net/ServerNet.cs:ProvideInput`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
`server/impulse.qc` is the server-side dispatcher for the one-shot `impulse N` commands a client emits when a
bind fires (the `usercmd.impulse` byte). `ImpulseCommands(this)` runs once per server frame when
`CS(this).impulse != 0`, gates the impulse against several match states, then dispatches it through the
`IMPULSES` registry (a 255-slot table keyed by impulse number, `common/impulses/all.qh`). The handlers fall into
groups: **weapon selection** (groups 1–9/14, custom-priority prev/best/next 200–229, by-id 230–253, and the
next/prev/last/best singletons 10–19), **weapon actions** (drop 17, reload 20), the **use** key (21), and the
**waypoint** sprites (30–48). Cheat impulses (99/140–148) are dispatched from the same path via `CheatImpulse`.
This unit covers the impulse routing + the weapon-selection/throwing algorithms it drives.

## Base algorithm (authoritative)

### Dispatch + gating  (`server/impulse.qc:ImpulseCommands`)
- **Trigger:** `PlayerPreThink`/spectator think (`server/client.qc:846, 2829`) calls `ImpulseCommands(this)` once
  per frame when `CS(this).impulse` is set. Server side (authority).
- **Algorithm (in order):**
  1. `if (game_stopped) return;` — no impulses during intermission/match-end.
  2. `imp = CS(this).impulse; if (!imp) return; CS(this).impulse = 0;` — read-and-clear (one-shot).
  3. `if (MinigameImpulse(this, imp)) return;` — a player inside a minigame session consumes the impulse as a
     minigame move FIRST.
  4. `if (timeout_status == TIMEOUT_ACTIVE) return;` — no impulses while the match is paused.
  5. `if (round_handler_IsActive() && !round_handler_IsRoundStarted())` → **block weapon_drop / weapon_reload /
     use** (only those three) while waiting for a round to start (CA/freezetag warmup window).
  6. `if (vehicle_impulse(this, imp)) return;` — a seated pilot's impulse switches/cycles vehicle weapons.
  7. `if (CheatImpulse(this, imp)) return;` — cheat impulses (99/140–148) when cheats allowed.
  8. `FOREACH(IMPULSES, it.impulse == imp, { f = it.impulse_handle; f(this); return; });` — registry dispatch.

### Weapon-group cycling  (`impulse.qc:weapon_group_handle`, impulses 1–9/14)
- If dead, latch `this.impulse = imp` (re-fires on respawn) and return. Else for each weapon slot call
  `W_NextWeaponOnImpulse(this, number, weaponentity)`. Only slot 0 unless `g_weaponswitch_debug == 1`.
- `W_NextWeaponOnImpulse` → `W_GetCycleWeapon(this, cl_weaponpriority, +1, imp, complain=true,
  skipmissing=(cl_weaponimpulsemode==0), …)` then `W_SwitchWeapon`.

### Custom-priority cycling  (`impulse.qc:weapon_priority_handle`, impulses 200–229)
- Vehicle gate; dead-latch. Calls `W_CycleWeapon(this, CS_CVAR(this).cvar_cl_weaponpriorities[number], dir, …)`
  — the **per-group** list `cl_weaponpriority0..9` (NOT the main `cl_weaponpriority`). `dir`: prev=-1, best=0, next=+1.

### Direct-by-id  (`impulse.qc:weapon_byid_handle`, impulses 230–253)
- Vehicle gate; dead-latch. `W_SwitchWeapon_TryOthers(this, Weapon_from_impulse(imp), weaponentity)`.
  `Weapon_from_impulse(230+idx)` maps via `m_unique_impulse`, allocated in registry (definition) order skipping
  Null/SpecialAttack/Ball-Stealer — so `weapon_byid_0` = blaster, not the alphabetically first weapon.

### next/prev/last/best singletons  (impulses 10/12, 18/19, 15/16, 11, 13)
- `weapon_next/prev_byid` (10/12) → `W_NextWeapon/W_PreviousWeapon(this, 0)` cycles `weaponorder_byid`.
- `weapon_next/prev_bygroup` (18/19) → list 1 = `weaponorder_byimpulse`.
- `weapon_next/prev_bypriority` (15/16) → list 2 = `cl_weaponpriority`.
- `weapon_last` (11) → `W_LastWeapon`: switch back to slot `.cnt` if owned+ammo, else `W_SwitchToOtherWeapon`.
- `weapon_best` (13) → `W_SwitchWeapon(this, w_getbestweapon(this, weaponentity))`.
- All gated: `if (this.vehicle) return; if (IS_DEAD(this)) ...`.

### W_GetCycleWeapon  (`selection.qc:122`)
- Walks the weapon-order token list. `weaponcur` = `selectweapon` (or `m_switchweapon.m_id` when skipmissing/none).
  Filters to impulse group (`imp>=0`) or custom group bitmask (`imp<0`). Skips unowned weapons that are hidden,
  or (not in map AND (mutatorblocked OR have_other)). `dir==0` → first valid; `dir>0` → weapon after weaponcur;
  `dir<0` → weapon before. Wraps (switchtolast / first_valid). Rotates a complaint across the group when nothing
  switchable. Returns chosen weapon id or 0.

### W_SwitchWeapon / TryOthers  (`selection.qc:265,289`)
- If target != current switchweapon: `client_hasweapon(complain)` → `W_SwitchWeapon_Force` (sets `.cnt`=outgoing,
  `m_switchweapon`, `selectweapon`); else set `selectweapon` only + complain → false. If already switching to it
  and `cl_weapon_switch_reload`, reload. `TryOthers`: on failure, if `cl_weapon_switch_fallback_to_impulse`,
  `W_NextWeaponOnImpulse(w.impulse)`.

### weapon_drop  (`impulse.qc:334` → `throwing.qc:W_ThrowWeapon`)
- Vehicle gate; dead → return (NOT latched). For each slot: `md = movedir; dv = v_right * -md.y` (only when
  dual-wielding, else `'0 0 0'`); `W_ThrowWeapon(this, weaponentity,
  W_CalculateProjectileVelocity(this, this.velocity, v_forward*750, false), dv, doreduce=true)`.
- `W_ThrowWeapon`: gates — `w != WEP_Null`, `time >= game_starttime`, `!ForbidThrowCurrentWeapon`,
  `g_weapon_throwable`, slot `state == WS_READY`, `W_IsWeaponThrowable`, owned. Then remove the weapon bit,
  `W_SwitchWeapon_Force(w_getbestweapon)`, `W_ThrowNewWeapon` (spawn loot, superweapon time-split, ammo
  transfer), and `Send_Notification(ITEM_WEAPON_DROP)`.
- `W_IsWeaponThrowable`: `!ForbidDropCurrentWeapon`, `g_pickup_items`, `!g_weaponarena`, per-weapon
  `weaponthrowable` (stock 1 except blaster/fireball/okhmg/okrpc).

### weapon_reload  (`impulse.qc:353`)
- Vehicle gate; dead → return; `weaponLocked` → return. Per slot: `w.wr_reload(w, actor, weaponentity)`.

### use  (`impulse.qc:409` → `client.qc:PlayerUseKey`)
- `PlayerUseKey`: not a player → return; in a vehicle → `vehicles_exit(VHEF_NORMAL)`; else if `g_vehicles_enter`
  → radius search (`g_vehicles_enter_radius`) for the nearest boardable vehicle → `vehicles_enter`; finally
  `MUTATOR_CALLHOOK(PlayerUseKey, this)` (button/door/objective use hooks). NOTE: `use` is *also* fired from
  `PlayerPreThink` on the `+use` button rising edge (`client.qc:2691`), independent of the impulse.

### waypoint sprites  (impulses 30–48)
- personal/here/danger waypoints at location / crosshair-trace / death-origin, the HELP-ME follow ping
  (teamplay), and clear-personal / clear-all (which also `ClientKill` in race/cts checkpoint modes).

### Constants / cvars (Base defaults)
| name | default | units | side |
|---|---|---|---|
| `g_weaponswitch_debug` | 0 | enum (0/1/2) | server |
| `cl_weaponpriority` | "vaporizer okhmg … hook" | string | client |
| `cl_weaponpriority0..9` | per-group strings | string | client |
| `cl_weaponimpulsemode` | 0 | enum | client |
| `cl_weapon_switch_reload` | 1 | bool | client |
| `cl_weapon_switch_fallback_to_impulse` | 1 | bool | client |
| drop velocity | `v_forward * 750` | qu/s | server |
| `g_weaponspeedfactor` | 1 | scalar | server |
| `g_weapon_throwable` | 1 | bool | server |
| `g_balance_superweapons_time` | 30 | s | server |
| death-drop toss | `randomvec()*125 + '0 0 200'` | qu/s | server |

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `ImpulseCommands` dispatch + gating | `Commands.cs:DispatchImpulse` (via `ServerNet.ProvideInput` → `impulse N` cmd) | live; gates intermission + timeout; **missing** minigame, round-start, cheat dispatch |
| weapon_group 1–9/14 | `WeaponImpulses.Handle` → `Inventory.NextWeaponOnImpulse` | faithful |
| weapon_priority 200–229 | `WeaponImpulses.PriorityHandle` → `Inventory.CycleWeapon(WeaponPriority)` | uses MAIN list, not per-group `cl_weaponpriorityN` |
| weapon_byid 230–253 | `WeaponImpulses.ByIdHandle` → `WeaponOrder.WeaponByIdIndex` | order faithful (pinned by test) |
| next/prev/last/best 10–19 | `WeaponImpulses.Handle` switch → `Inventory.NextWeapon/PreviousWeapon/LastWeapon/GetBestWeapon` | by-group list 1 falls back to priority (not `weaponorder_byimpulse`) |
| `W_GetCycleWeapon` | `Inventory.GetCycleWeapon` | faithful for imp=-1; group complain path simplified |
| `W_SwitchWeapon`/TryOthers | `Inventory.SwitchWeaponWithComplain`; `ByIdHandle` fallback | faithful |
| weapon_drop 17 | `WeaponImpulses.DropHandle` → `WeaponThrowing.ThrowWeapon` | dual-wield `dv` collapses to 0 (single slot) |
| weapon_reload 20 | `WeaponImpulses.ReloadHandle` → `Weapon.WrReload` | `weaponLocked` gate not modeled |
| use 21 | `VehicleBoarding.UseKey` via `+use` edge (ServerNet) | impulse 21 itself NOT routed; PlayerUseKey mutator hook not fired |
| waypoints 30–48 | NOT IMPLEMENTED | no waypoint-sprite system on the impulse path |
| cheat impulses 99/140–148 | NOT routed from impulse path | `Cheats` exists but driven by console `cheatcommand`, not impulse |

## Parity assessment
- **Weapon selection/switching (the core of this unit)** is faithfully ported and **live**: the client input
  byte (`InputCommand.Impulse`) is dispatched once-per-command in `ServerNet.ProvideInput` /
  `ProvideInputPerFrame` via `Commands.Execute("impulse N")` → `CmdImpulse` → `DispatchImpulse` →
  `WeaponImpulses.Handle` → `Inventory`. Group/by-id/next/prev/last/best/drop/reload all reach real selection
  logic. By-id order is pinned by `WeaponByIdTests`.
- **Gaps:**
  - **Custom-priority groups (200–229) use the wrong list.** QC `weapon_priority_handle` cycles the per-group
    `cl_weaponpriorityN` list (explosives / energy / hitscan etc.); the port's `PriorityHandle` cycles the single
    main `cl_weaponpriority` for ALL ten groups. The cvars are replicated (`Commands.cs` allowlist + fixup) but
    `WeaponImpulses` never reads them. Observable: `weapon_group_N_best`-style binds select from the full list,
    not the intended category.
  - **`weapon_next_bygroup` (18/19) cycles the priority list, not `weaponorder_byimpulse`.** `Inventory.NextWeapon`
    list==1 falls back to the priority list. Observable: "next by group" mouse-wheel cycling differs in order.
  - **Minigame impulses not routed.** QC dispatches `MinigameImpulse` before weapon impulses; the port
    `MinigameSessionManager.Impulse` (the method exists, `MinigameSessionManager.cs:389`) has NO caller from
    `DispatchImpulse`. A player inside a minigame pressing a weapon key drives weapon selection instead of a
    minigame move.
  - **Round-not-started gate missing.** QC blocks drop/reload/use during the pre-round warmup; the port's
    `DispatchImpulse` only gates intermission + active timeout (its own comment defers the round-start gate). A
    player can drop/reload during the CA/freezetag round-start countdown.
  - **`use` impulse (21) + PlayerUseKey mutator hook.** The +use *button edge* boards/exits vehicles
    (`VehicleBoarding.UseKey`, live), but `impulse 21` is not routed (prints "impulse 21 not handled"), and the
    `MUTATOR_CALLHOOK(PlayerUseKey)` (door/objective/button "use") is never fired — no general use-key hook.
  - **Waypoint impulses (30–48) entirely absent** — no personal/here/danger/help-me waypoint sprites driven by
    impulse.
  - **Cheat impulses (99/140–148) not on the impulse path.** `Cheats` exists but is console-driven; the QC
    `CheatImpulse` branch in `ImpulseCommands` is unported.
  - **Dead-player latch** (`this.impulse = imp` re-firing on respawn) is dropped — the port simply ignores
    switch impulses while dead (documented in `WeaponImpulses` as a networking nicety not modeled).
  - **`weaponLocked` reload gate** not modeled (a frozen Freeze-Tag player can reload). A `WeaponLocked`
    predicate covering the freeze case already exists nearby (`WeaponFireDriver.cs:328`, private) but
    `ReloadHandle` never calls it — an easy fix.
  - **`g_weaponswitch_debug` multi-slot loop** not modeled (the port always drives slot 0; dual-wield is a
    documented single-slot simplification).
- **Intended divergences:** none flagged here; the single-slot simplification + dead-latch omission are
  documented in-code as deferred parity, treated as gaps (low impact).

## Verification
- Live wiring: code-read `ServerNet.cs:1228-1235` / `:1312-1317` → `Commands.Execute("impulse N")` →
  `DispatchImpulse:1326` → `WeaponImpulses.Handle`. PASS.
- By-id order: `tests/VortexArena.Tests/WeaponByIdTests.cs` pins the QC unique-impulse order. PASS.
- Selection algorithm: code-read `Inventory.GetCycleWeapon` vs `selection.qc:W_GetCycleWeapon` — faithful for
  imp=-1 (the cycle/best path). The impulse-group `have_other`/custom-group-bitmask complain branch is
  simplified (documented in-code). Verified by read, not behaviorally.
- Drop velocity / throwable: code-read `WeaponImpulses.DropHandle` (`v_forward*750*g_weaponspeedfactor`) +
  `WeaponThrowing` vs `throwing.qc`. Faithful.
- Priority-group list mismatch / minigame / round-start / waypoint / cheat gaps: code-read (grep for callers),
  no behavioral test. PASS-as-described / FAIL-as-described per row.

## Verifier notes (2026-06-22 adversarial pass)
- The priority-group gap is **confirmed and slightly worse than the draft implied**: `Inventory.PriorityProvider`
  (wired `Commands.cs:324`) only ever returns `cl_weaponpriority`; the per-group `cl_weaponpriorityN` lists are
  replicated + fixed-up but have **no reader at all**, so even a correct `PriorityHandle(number)` would have
  nothing to read. Both halves (handler + provider) need the group index threaded through.
- `cl_weapon_switch_fallback_to_impulse` Base default is **1** (`main.qc:101 registercvar(...,"1")`). The
  by-id row's in-code comment (`WeaponImpulses.cs:142`) wrongly says it "defaults off" — but the code reads the
  live cvar (`!=0f`), so runtime behavior is faithful. Code-comment defect only; row stays faithful, noted.
- `W_NextWeapon` (dir −1) / `W_PreviousWeapon` (dir +1) name-vs-direction inversion **is** reproduced exactly by
  the port (`NextWeapon→CycleWeapon(-1)`, `PreviousWeapon→CycleWeapon(+1)`). Not a bug.
- `weapon_drop` emits `ITEM_WEAPON_DROP` (`WeaponThrowing.cs:204`) → audio dimension upgraded to faithful.

## Open questions
- Does any port bind actually emit impulses 200–229 today? The HUD weapon panel labels use `w.Impulse` (the
  by-id 230+ values); the priority-group binds (`weapon_priority_N_best`) are configured client-side and may not
  be exercised, lowering the practical impact of the wrong-list gap. Needs a live bind/runtime check.
- Is `use` (impulse 21) ever sent as an impulse by the port client, or only as the `+use` button? If only the
  button, the unrouted impulse 21 is harmless; the missing PlayerUseKey mutator hook (doors/objectives) remains.
