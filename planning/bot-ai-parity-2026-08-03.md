# Bot AI — Vortex Arena vs Base Xonotic, full parity audit

*2026-08-03. Base = `Vortex/Base` (`v0.8.6-1779-g863cd3e84`), port = `main` @ `e522afb9`.
~10,100 lines of QC (`qcsrc/server/bot/`) read against ~8,000 lines of C# (`src/VortexArena.Server/Bot/`),
plus weapons, cvars, and the shipped map data on both sides.*

**Method.** Ten subsystem comparisons, each independently re-verified by a second adversarial pass
instructed to *refute* the first (the port's dominant failure mode is code that exists but has no caller,
so every "the port has this" claim required a grep'd caller chain, and every "the port lacks this" claim
required a grep'd absence). 143 differences survived verification; 3 were refuted. The two headline
symptoms were then traced end-to-end as separate root-cause hunts.

**Two claims I made early and had to withdraw**, both worth recording so nobody re-derives them:

- `aim.qc:197` reads `int f = bound(0, 1 - 0.1*(skill + bot_offsetskill), 1)`, which looks like an
  int-truncation quirk that would zero Base's aim error at skill ≥ 1. It is not: `lib/_all.inc:20` is
  `#define int float`. There is no truncation and the port's float matches Base. **Refuted.**
- Missing `.waypoints` data. The files *are* present inside each `data/maps/*.pk3` and *are* loaded at
  runtime through the VFS reader ([NetGame.cs:876](game/net/NetGame.cs:876) →
  [GameWorld.cs:5148](src/VortexArena.Server/GameWorld.cs:5148)). **But their two companion files are
  not** — see D1/D2, which turned out to be the single largest finding in the audit.

---

## Part 1 — Why bots get stuck in corners

Ranked by contribution. The first two are data, not code, and are by far the cheapest to fix.

### D1. The shipped map pk3s contain no `.waypoints.cache` — a quarter of the graph is lost

> **Root-caused 2026-08-03 (correction).** This is not a Vortex Arena data gap and not an upstream
> difference: Xonotic ships all three files, and `Base/data/xonotic-20230620-maps.pk3` — the pack VortexMaps
> re-split — contains 32 `.waypoints`, 32 `.waypoints.cache` and 32 `.waypoints.hardwired`. They were dropped
> by **`VortexMaps build/split-pack.py`**, whose `SOURCE_EXT` classified `.cache` and `.hardwired` as
> "build input or compiler residue, never runtime content". They are neither — q3map2 emits no file with
> either extension, and every `.cache`/`.hardwired` member of the upstream pack is a waypoint companion.
> **Fixed in VortexMaps**, not here. The corrected packs reach players only after a new maps release is
> published and `data/maps.lock.json` is re-pinned.

Base ships three files per map. The port ships one.

| | `.waypoints` | `.waypoints.cache` | `.waypoints.hardwired` |
|---|---|---|---|
| Base `xonotic-maps.pk3dir/maps/` | 32 | **32** | **32** |
| Port `data/maps/*.pk3` | 32 | **0** | **0** |

`.waypoints` holds only the *nodes*. The *links* live in the `.cache` (Base
`waypoints.qc:1317-1463`). With no cache, [GameWorld.cs:5160](src/VortexArena.Server/GameWorld.cs:5160)
passes `null` and the port falls back to re-deriving every link with `AutoLink` tracewalks — which
recovers most links but not all. Measured through the port's own `ForMap`, same node file:

| map | port links / dead-end nodes | with Base's cache+hardwired |
|---|---|---|
| catharsis | 1209 / **54** | 1624 / 1 |
| fuse | 494 / **18** | 681 / 1 |
| stormkeep | 696 / **11** | 748 / 0 |

29% of catharsis's waypoints become places a bot can walk into and never out of. That is the mechanism
of "stuck in a corner" almost literally. Graph build also goes 31-38 ms → 877-1306 ms, a ~1 s freeze on
the first frame bots appear.

**Options.** (a) Ship the `.cache` files. (b) Generate a cache at build time from the BSP. (c) Improve
AutoLink to close the gap.
**Recommend (a).** These are authored artifacts that Base already ships; regenerating them is strictly
worse than copying them, and (c) cannot recover links a human placed because tracewalk *couldn't* find
them. Note the ordering trap in D3 below — restore the cache and the hardwired file together.

**Done (2026-08-03).** `SOURCE_EXT` corrected in `VortexMaps/build/split-pack.py`; verified against the
upstream pack with the real classifier — all 96 waypoint files now route to their map's pack. Local
`data/maps/*.pk3` were patched in place so the fix is testable before the release lands (a re-fetch will
revert them). `tests/VortexArena.Tests/BotWaypointDataTests.cs` is the regression net.
**Still outstanding and not mine to do: publish a maps release built with the fix, then re-pin
`data/maps.lock.json`.**

### D2. No `.waypoints.hardwired` — every JUMP/TELEPORT/SUPPORT/CUSTOM_JP node is a dead end

The hardwired file (291 link records across the 32 maps, e.g.
`Base/data/xonotic-maps.pk3dir/maps/fuse.waypoints.hardwired`) holds precisely the links that
*cannot* be auto-derived: gap jumps, drop-downs, teleport exits. Measured: fuse 11/11 flagged waypoints
have zero outgoing links; catharsis 6/6. Across the set, 70 JUMP + 13 SUPPORT + 5 TELEPORT + 3 CUSTOM_JP
nodes with no way out.

**Options.** (a) Ship them. (b) Synthesise jump links from geometry.
**Recommend (a)**, same reasoning as D1. (b) is a research project that would still under-perform hand
authoring.

### D3. No jumppad/teleporter/ladder waypoints are created when the map ships a `.waypoints` file

Base creates these from the *entities* at map load (`waypoints.qc:2010-2068`, called from
`jumppads.qc:720`, `teleporters.qc:260`, `ladder.qc:124`) — independently of the waypoint file. The port
only creates them inside `GenerateFromEntities`, which is the no-file fallback path
([Waypoint.cs:485-556](src/VortexArena.Server/Bot/Waypoint.cs:485)). Measured `teleportWps = 0` on
stormkeep (4 `trigger_push` + 1 `trigger_teleport`), catharsis, fuse. All the port's teleport-traversal
machinery ([BotNavigation.cs:287-292](src/VortexArena.Server/Bot/BotNavigation.cs:287), `:324-325`,
`:382`) is live code that never fires.

This also gates D1/D2: 53-61 cache lines per map reference generated endpoints the port's node set
doesn't contain, and `FindAt` drops them silently.

**Options.** (a) Split entity-derived waypoint spawning out of `GenerateFromEntities` and always run it.
(b) Only run it when the cache references a missing node.
**Recommend (a)** — it is what Base does, and it is a prerequisite for D1/D2 landing cleanly. Watch the
related trap at [Waypoint.cs:363](src/VortexArena.Server/Bot/Waypoint.cs:363): `LoadHardwiredLinks`
stamps `CustomJp` on every hardwired *source*, which makes that node refuse all auto-links. Restoring
hardwired without the cache would turn 4 stormkeep nodes into fresh near-dead-ends.

### D4. `navigation_unstuck` / `AI_STATUS_STUCK` / `bot_wander_enable` are entirely absent

Base's terminal recovery. `navigation_goalrating_end` (`navigation.qc:1861-1867`) sets `AI_STATUS_STUCK`
when nothing rates; `bot_think` (`bot.qc:150-151`) then runs `navigation_unstuck`
(`navigation.qc:1908-2007`) every think, which (i) walks to a *random* nearby waypoint to physically
shake loose — the comment is literally "unstuck bots from bad spots or when other bots of the same team
block all their ways" — and (ii) scans every non-generated waypoint within 1000qu one-per-frame,
tracewalking each, then routes to the farthest reachable one and clears the bit.

The port has neither half. `bot_wander_enable` is registered at
[Cvars.cs:335](src/VortexArena.Server/Cvars.cs:335) and read by nothing. The comment at
[BotBrain.cs:571](src/VortexArena.Server/Bot/BotBrain.cs:571) claims the no-progress watchdog "covers
navigation_unstuck's main value" — it does not. The watchdog clears the route and forces a re-rate; the
re-rate runs from the same spot and produces the same unreachable answer. A bot that can rate nothing
stands motionless for 2 s, re-rates, fails, forever.

**Options.** (a) Port `navigation_unstuck` faithfully, including the shared `bot_waypoint_queue`
round-robin. (b) Port only the random-waypoint shake-loose half. (c) Simpler bespoke escape (random walk
for N seconds on stuck).
**Recommend (a).** It is ~100 lines, self-contained, and it is the designed backstop for every other
failure in this list — including ones you choose not to fix. (b) is a reasonable first commit if you want
the visible half immediately.

### D5. Unreachable goals are still rated, then routed as a straight line

Base rejects a goal the graph cannot reach: `navigation_routerating` returns early on the
unreachable sentinel (`navigation.qc:1408`, sentinels `:1091`/`:1180`). The port's
[BotRoles.cs:86-90](src/VortexArena.Server/Bot/BotRoles.cs:86) falls back to straight-line cost, and
[BotNavigation.cs:227](src/VortexArena.Server/Bot/BotNavigation.cs:227) pushes the raw goal position even
when pathfinding failed. The bot then walks at a wall until the watchdog fires.

Worse, the unreachable candidate sets `rater.HasGoal`, and [BotRoles.cs:343](src/VortexArena.Server/Bot/BotRoles.cs:343)
is `if (rater.HasGoal) return;` — so it also suppresses the roam-waypoint fallback that would have given
the bot a *reachable* goal.

**Options.** (a) Return the unreachable sentinel from the rater and skip those candidates. (b) Keep the
straight-line fallback but exclude such goals from setting `HasGoal`.
**Recommend (a)** — matches Base and fixes both halves. (b) alone leaves bots picking wall-facing goals
whenever nothing else rates.

### D6. The danger response *replaces* the wish-move; Base blends a correction into it

Base sets `AI_STATUS_DANGER_AHEAD`, keeps steering (`dir = flatdir`, `havocbot.qc:1155`), and *adds*
corrections at `:1269`: `dir = normalize(dir + dodge + do_break + evadedanger)`. `evadedanger` is itself
narrow — gated on speed > 0.8·maxspeed and a waypoint goal — and is a correction back onto the
`goalcurrent_prev → destorg` centreline, i.e. "get back on the path".

The port at [BotBrain.cs:613-641](src/VortexArena.Server/Bot/BotBrain.cs:613) *discards* the navigation
move (`move = Nav.WorldToLocalMove(evadeWorld, …)` at `:633`) on any danger result. Around a railing, a
stairwell, or any room with a hole in the floor, the bot oscillates between "advance" and "peel
sideways/reverse" and never traverses. Base walks past the same geometry at full speed.

**Options.** (a) Change to an additive blend matching `havocbot.qc:1269`. (b) Keep the override but
narrow the trigger to Base's gates.
**Recommend (a).** The additive form is the whole design — the danger term is meant to bias, not steer.

### D7. `CombatMovement` — an invented strafe/retreat that overrides navigation

[BotBrain.cs:701, :907-937](src/VortexArena.Server/Bot/BotBrain.cs:701) replaces the navigation move with
a strafe whenever `Bot.Enemy` is non-null, holding one direction for up to 0.8 s with **no wall or ledge
probe on the strafe axis**. Base has no such behaviour at normal skill: the only combat movement override
is the SUPERBOT random jitter (`havocbot.qc:1280-1306`), which is `skill > 100` only.

With the 4 s sticky-enemy window plus the 2 s rescan interval, this covers most of the time a bot spends
near other players — which is exactly when players observe the corner-sticking.

**Options.** (a) Delete it; restore Base's "combat does not override navigation". (b) Keep it but gate it
on SUPERBOT like Base's jitter. (c) Keep it and add wall/ledge probes on the strafe axis.
**Recommend (a).** It is a port-only invention with a known failure mode, and Base's bots are lively
without it. If the strafe is wanted as a Vortex divergence, (c) with a probe is the minimum bar, and it
should be recorded as an intended divergence in the registry.

### D8. `Steer` uses the 3D goal direction, not Base's flat direction

Base steers on `flatdir` (`havocbot.qc:1043`, `:1155`). The port normalizes the full 3D `dir` and
projects it ([BotNavigation.cs:335, :393, :402-405](src/VortexArena.Server/Bot/BotNavigation.cs:335)), so
horizontal speed is scaled by cos(elevation): 71% for a goal 45° above, 50% at 60°, ~0% at the foot of a
ledge with the waypoint overhead. The bot creeps at the base of steps and stairs — which then trips the
0.5 s no-progress watchdog and destroys its route.

**Options.** (a) Use `flatdir` for the horizontal component as Base does. (b) Renormalize the horizontal
part to full speed after projection.
**Recommend (a).** One-line, faithful, and (b) has the same effect by a less obvious route.

### D9. No corner-cutting lookahead, no jump lockout while turning, no `bot_stop_moving_timeout`

Base's three anti-wall-grinding devices in `havocbot_movetogoal`, none of them present:

- **Lookahead / corner cut** (`havocbot.qc:979-1048`): the bot steers toward `actual_destorg` = a
  speed-scaled point *ahead* of itself (`offset = max(32, speed·cos(deviation)·0.3)·flatdir`), and when
  within that offset of the current goal it re-aims at the *next* goal to cut the corner. The port steers
  straight at the goal centre ([BotNavigation.cs:335](src/VortexArena.Server/Bot/BotNavigation.cs:335)).
- **`jumpobstacle_check` retry** (`havocbot.qc:1050-1077`): if an obstacle appears while turning, re-probe
  *without* the turn before deciding to jump — so the bot doesn't jump at a wall it only faces mid-corner.
- **`bot_stop_moving_timeout`** (`havocbot.qc:346-347, 995-1000, 1130-1134`): stop and re-orient rather
  than push forward while the yaw slews. Absent entirely. This is Base's primary anti-grind device for
  skill ≤ 3 and its jump-approach stabiliser at all skills.

**Options.** (a) Port all three. (b) Port lookahead only. (c) Port `bot_stop_moving_timeout` only.
**Recommend (a).** They interlock — the retry only makes sense with the lookahead, and the timeout is what
stops the grind the lookahead's turn creates. Do this after D1-D6; on a correct graph the symptom will
already be much smaller and you'll be able to measure what these actually buy.

### D10. Other confirmed contributors

| # | Difference | Base → port | Recommendation |
|---|---|---|---|
| D11 | Emptying the goal stack does not force a re-rate — bot idles up to 7 s | `havocbot.qc:875-879` → [BotNavigation.cs:322-330](src/VortexArena.Server/Bot/BotNavigation.cs:322) | Force `_strategyForced` when the stack empties. Cheap, high value. |
| D12 | Global 96-trace/tick tracewalk budget shared by *all* bots | port-only, [BotTracewalk.cs:43](src/VortexArena.Server/Bot/BotTracewalk.cs:43) | Raise it and make it per-bot; a 750qu walk costs the whole pool, so with 3+ bots re-rating in a tick the later ones get a fabricated "nothing reachable". Keep a budget (it exists for good perf reasons) but round-robin it like Base's strategy token. |
| D13 | Bunnyhop is never suppressed while attacking | `havocbot.qc:137, 215-221` → [BotNavigation.cs:421](src/VortexArena.Server/Bot/BotNavigation.cs:421) | Port the `AI_STATUS_ATTACKING` gate. Bots currently fight airborne at >maxspeed while `CombatMovement` steers them perpendicular. |
| D14 | Tracewalk hardcodes `MOVE_NOMONSTERS`; `bot_navigation_ignoreplayers` dead | `bot.qc:737` → [BotTracewalk.cs:258](src/VortexArena.Server/Bot/BotTracewalk.cs:258) | Wire the cvar. Bots believe the line is clear through teammates, so they jam in doorways instead of re-routing. |
| D15 | `navigation_shortenpath` absent (direct-chase cut; "closer to next goal, pop this one") | `navigation.qc:1555-1625` → absent | Port it. The second half is the direct fix for "bot got pushed past a waypoint and must physically walk back". |
| D16 | Goal stack stores static points, not entities | `navigation.qh:19-26` → [BotNavigation.cs:73-82](src/VortexArena.Server/Bot/BotNavigation.cs:73) | Larger refactor. Defer; the practical loss is chasing stale coordinates for moving goals. |
| D17 | Ledge brake fires while still standing on the ledge; no speed term | `havocbot.qc:1166-1174` → [BotNavigation.cs:381-391](src/VortexArena.Server/Bot/BotNavigation.cs:381) | Add Base's `!onGround \|\| speed > 0.3·maxspeed`. Currently any route descending >120qu self-cancels. |
| D18 | Teleport/jumppad goals pop on box overlap; no `TELEPORT_USED` gate; `LastTeleportTime` write-only | `navigation.qc:1637-1681` → [BotNavigation.cs:322-327](src/VortexArena.Server/Bot/BotNavigation.cs:322) | Port the gate and the jumppad pop delay. This is the "bot dances on a jumppad" failure. |
| D19 | JUMP-waypoint execution inverted — port jumps on *approach*, Base jumps 50-150qu *after* leaving at speed | `havocbot.qc:993-1009` → [BotNavigation.cs:345-346](src/VortexArena.Server/Bot/BotNavigation.cs:345) | Port Base's form. Pairs with D2. |
| D20 | Teleporter never restamps a bot's ViewAngles; `bot_aim_reset` never called on teleport/respawn | `teleporters.qc:113-119`, `client.qc:700-704` → [BotAim.cs:97](src/VortexArena.Server/Bot/BotAim.cs:97) (sole caller: the constructor) | Call `Reset` on spawn, teleport and warpzone. Stale higher-order filters throw the aim tens of degrees off for ~1 s after every teleport — and since `Steer` projects through `Aim.ViewAngles.Y`, the bot also *walks* wrong. |
| D21 | Obstacle jump probe: no half-jump-height fallback, no airborne variant, traces `MOVE_NOMONSTERS` | `havocbot.qc:1050-1096` → [BotNavigation.cs:359-372](src/VortexArena.Server/Bot/BotNavigation.cs:359) | Port the fallback ladder. Base jumps over *players* too. |
| D22 | Tracewalk jumpstep height hardcoded 48 vs Base's ~67 (`stepheight + 0.85·jumpheight`) | `bot.qc:615-621` → [BotTracewalk.cs:26-31](src/VortexArena.Server/Bot/BotTracewalk.cs:26) | Derive from physics cvars. The port is ~28% more pessimistic, so it rejects reachable routes. |
| D23 | Budgeted tracewalk rejects any descent >200qu | port-only, [BotTracewalk.cs:98](src/VortexArena.Server/Bot/BotTracewalk.cs:98) | Use the full column like Base (`navigation.qc:695`). Any deep drop reads as unreachable during strategy. |
| D24 | `ignoregoal` applied to the pass winner, not per-candidate during rating; keyed on the final target not `goalcurrent` | `roles.qc:140`, `havocbot.qc:1161-1162` → [BotBrain.cs:522, :636](src/VortexArena.Server/Bot/BotBrain.cs:522) | Skip ignored candidates during rating. Currently one bad goal can blank a whole pass. |
| D25 | Seed search capped (12 walks / 8 seeds / 2250qu) vs Base's ring growing to 50000 | `navigation.qc:1107-1120` → [Waypoint.cs:604-684](src/VortexArena.Server/Bot/Waypoint.cs:604) | Relax with the D12 budget change. |
| D26 | `waypoint_spawn`'s stuck-in-solid kill, `move_out_of_solid` nudge, 8qu dedupe not ported | `waypoints.qc:433-495` → absent | Port. Shipped files are clean, but user maps won't be. |
| D27 | Swimming/resurfacing branch of `movetogoal` absent | `havocbot.qc:724-736, 946-970` → absent | Port. A bot in water never presses jump, so it cannot climb out except on a ramp. |
| D28 | Ladder handling keys on the waypoint flag, not the ladder entity; no descent | `havocbot.qc:1208-1228` → [BotNavigation.cs:350](src/VortexArena.Server/Bot/BotNavigation.cs:350) | Port. Ascent currently stops halfway (when the flag pops) and loops. |
| D29 | `AutoLink` tracewalks CROUCH links with the standing hull | `waypoints.qc:1189-1206` → [Waypoint.cs:390-392](src/VortexArena.Server/Bot/Waypoint.cs:390) | Use the crouch hull. Only 2 nodes ship (erbium) but failure is total for them. |
| D30 | SUPPORT waypoint incoming-link forbid explicitly omitted | `waypoints.qc:1133-1142` → [Waypoint.cs:311-320](src/VortexArena.Server/Bot/Waypoint.cs:311) | Implement. Each SUPPORT node marks a spot a mapper found bots sticking at, and the port routes into it from the wrong side. 13 across the shipped maps. |
| D31 | `bot_ai_bunnyhop_turn_angle_*` / `_downward_pitch_max` all registered, none read; continuation branch missing | `havocbot.qc:237-255` → [BotNavigation.cs:521-554](src/VortexArena.Server/Bot/BotNavigation.cs:521) | Port the branch. Movement-quality regression + 4 dead knobs. |
| D32 | `bots.txt` `bot_moveskill` column dropped — the whole roster passes the bunnyhop gate | `bot.qc:276` → [BotPopulation.cs:186](src/VortexArena.Server/Bot/BotPopulation.cs:186) | See D46. In Base ~¼ of the roster walks these approaches instead of bunnyhopping into them. |
| D33 | Teammate-aware pickup dedup missing; `bot_ai_friends_aware_pickup_radius` dead | `roles.qc:60-104` → absent | Port. Team bots converge on one pickup and body-block in the doorway. |
| D34 | Item rating drops Base's lava and airborne-loot filters | `roles.qc:144-162` → [BotRoles.cs:242-245](src/VortexArena.Server/Bot/BotRoles.cs:242) | Port. Interacts badly with D6: bot routes to a hazard item, brakes, ignores the goal, freezes 2 s, repeats. |
| D35 | Roam anti-repeat reads the current route step, not the last two chosen goals | `roles.qc:31-36` → [BotRoles.cs:355](src/VortexArena.Server/Bot/BotRoles.cs:355) | Port the two-slot history. Prevents A→B→A oscillation in dead ends. |
| D36 | `get_closer_dest` unused — `destorg` is always the box centre | `navigation.qc:104-118` → [BotNavigation.cs:333](src/VortexArena.Server/Bot/BotNavigation.cs:333) | Use it. For a wide trigger volume the centre can be inside solid. |
| D37 | Role runs during jumppad/JUMP-waypoint flight, which Base suppresses | `havocbot.qc:60-61` → [BotBrain.cs:508](src/VortexArena.Server/Bot/BotBrain.cs:508) | Add the guard. |
| D38 | Race/CTS roles omit the `raw_touch_check` untouchable-checkpoint skip | `sv_race.qc:37-43` → [BotObjectiveRoles.cs:1499](src/VortexArena.Server/Bot/BotObjectiveRoles.cs:1499) | Port. Hard stuck when it hits. |
| D39 | `g_botclip_collisions` ships at 1 but nothing ORs `DPCONTENTS_BOTCLIP` into a bot's mask | `client.qc:624-625` → absent | Port. Mappers use botclip specifically to keep bots out of geometry traps. |

---

## Part 2 — Why bots don't fire as often as they could

### F1. Weapon selection has no ammo check (the dominant cause)

Base's `havocbot_chooseweapon` calls `client_hasweapon(this, w, weaponentity, true, false)` at
`havocbot.qc:1516, 1571, 1585, 1598` — the fourth argument is `andammo = true`, so a weapon with no ammo
is **skipped** and the loop falls through to the next entry in the priority list.

The port's `PickFromPriority` ([BotBrain.cs:1126-1137](src/VortexArena.Server/Bot/BotBrain.cs:1126))
tests `Bot.OwnedWeapons.Contains(netName)` and nothing else. There is no ammo read anywhere on the path.

Vaporizer/OkNex/Vortex head **all three** shipped priority bands
(`data/core.pk3dir/xonotic-server.cfg:155-157`), and cells are the first ammo type a bot exhausts. So:
the port re-selects the empty Vortex every 0.5 s tick → `Inventory.SwitchWeapon` latches it → the slot
runs drop/clear/raise → on the first attack `CheckAmmoWithAutoSwitch`
([WeaponFireGate.cs:184-216](src/VortexArena.Common/Gameplay/WeaponFireGate.cs:184)) yanks it back to a
weapon with ammo → 0.5 s later `ChooseWeapon` puts the dry one back. With
`switchdelay_drop/raise = 0.2/0.2` each round trip parks the slot in Drop/Raise for ~0.4 s, and
`WeaponFireGate.cs:97-98` (`st.State != Ready → return false`) rejects every shot for that whole window.

**This alone can account for most of the reported symptom.**

**Options.** (a) Add the ammo test to `PickFromPriority` and `PickOwned`. (b) Additionally port
`havocbot_chooseweapon_checkreload` (see F4).
**Recommend (a) now, (b) with it.** ~5 lines. Highest value-per-line in the whole report.

### F2. `bot_ai_weapon_combo` is inverted

Base computes `combo`, then in each priority loop does
`if ((m_weapon.m_id == w && combo) || checkreload) continue;` (`havocbot.qc:1573/1587/1600`) — when a
combo is in play it **skips the weapon it just fired** and picks the next one down the list. The intent
is: the splash weapon is on cooldown, so fire a *second* weapon during its refire. Base also blocks
switching for 1 s afterward (`lastcombotime`, `havocbot.qc:1535`).

The port ([BotBrain.cs:1040-1043](src/VortexArena.Server/Bot/BotBrain.cs:1040)) `return`s under the same
condition — it **keeps** the current weapon. Exactly backwards.

Two compounding losses. The bot sits through the slow weapon's full refire holding a dead trigger. And
`_lastAttackTime` is restamped on *every* think that emits a fire button
([BotBrain.cs:822-823](src/VortexArena.Server/Bot/BotBrain.cs:822)), so while the bot is shooting at all,
`Now - _lastAttackTime ≈ 0` and the early return fires every tick: **range-based weapon selection is
switched off for the entire engagement** whenever the held weapon is `TypeSplash` — which includes
Blaster, the universal spawn weapon, plus Crylink, Devastator, Electro, Fireball, Hagar, HLAC, Minelayer,
Mortar, Seeker, Tuba, Hook.

The trigger condition also differs: Base uses `ATTACK_FINISHED > combo_time` (the held weapon's cooldown
extends past the threshold); the port uses time-since-last-attack.

**Options.** (a) Port Base's form exactly — skip the just-fired weapon, add `lastcombotime`, use
`ATTACK_FINISHED`. (b) Keep the hold but fix the restamp so it doesn't disable range selection.
**Recommend (a).** (b) leaves the primary defect (holding a dead trigger) in place.

### F3. The bot's line-of-fire trace stops on `common/clip` brushes

`bot_aim` overrides the hit mask for the whole function —
`this.dphitcontentsmask = DPCONTENTS_SOLID|DPCONTENTS_BODY|DPCONTENTS_CORPSE` (`aim.qc:344`) — before its
LOS traceline at `aim.qc:395-402`. That mask is **transparent to `DPCONTENTS_PLAYERCLIP`**, and
`W_SetupShot` (`tracing.qc:41-50`) sets the identical mask for the real shot, so Base's LOS check and its
bullet agree: a bot shoots straight through clip brushes.

The port's [BotBrain.cs:877](src/VortexArena.Server/Bot/BotBrain.cs:877) traces with no override, so
`GenericHitMask` ([TraceService.cs:612-614](src/VortexArena.Engine/Collision/TraceService.cs:612)) returns
`Solid|Body|PlayerClip` and the trace **stops on playerclip** → `clear == false` → `return false`. On any
map with clip geometry (railings, ledge caps, doorway smoothing — most stock maps) the bot reports "line
of fire blocked" while facing a visible enemy with the fire timer armed.

The port already models this correctly elsewhere:
[LagComp.cs:52-53](src/VortexArena.Common/Gameplay/LagComp.cs:52) saves and sets exactly that mask.

**Options.** (a) Scope the same save/set/restore around the bot's LOS trace, matching `LagComp`'s pattern.
(b) Pass an explicit mask parameter to `Api.Trace.Trace`.
**Recommend (a)** — reuses the established idiom, ~4 lines, and keeps bot LOS consistent with the shot.

### F4. `shot_accurate` is inferred from hitscan-ness instead of read per weapon

`shot_accurate` is a hand-picked per-weapon literal, deliberately *not* correlated with hitscan: the
hitscan **Shotgun passes `false`** (wide cone — pellet spread makes precision pointless) while the
projectile Blaster, Crylink, Electro, Hagar, HLAC, Mortar and Porto all pass `true`. It feeds
`f = shot_accurate ? 1 : 1.6` at `aim.qc:372`.

Only Devastator overrides `BotAimAccurate()` in the port; everything else falls through
[BotBrain.cs:872](src/VortexArena.Server/Bot/BotBrain.cs:872)'s `?? (shotSpeed <= 0f)` — i.e. "is it
hitscan". At the shipped `skill 8`: Base shotgun `f = 2.2`, port `f = 1.6` → the port's fire cone is
**73% of Base's on the spawn weapon** (2.70° vs 3.71° at 500qu). And in the other direction, every
projectile weapon Base marks accurate gets a **37.5% wider** cone, so those spray shots Base withholds.

**Options.** (a) Add the literal to each weapon's descriptor (18 weapons, mechanical). (b) Keep the
inference but invert it for the shotgun family.
**Recommend (a).** (b) is a guess that will drift; the values are right there in the QC.

### F5. `havocbot_chooseweapon_checkreload` not ported

`havocbot.qc:1472-1491`, called at `:1518/:1573/:1587/:1600`. Without it the bot can select — and sit on —
a weapon that is mid-reload, holding a dead trigger for the reload duration. Bites the Rifle (4th in the
FAR list) and the entire Overkill set.
**Recommend:** port it alongside F1; same call sites.

### F6. Turrets and breakable models are never bot targets

The port's target roster is players + `FL_MONSTER` only
([BotBrain.cs:1346-1362](src/VortexArena.Server/Bot/BotBrain.cs:1346)). Base also targets turrets
(`sv_turrets.qc:1263, 1344`), breakables (`breakable.qc:138, 173`) and `door_secret`. A port bot in front
of a hostile turret or a breakable wall has no enemy at all, so no fire button is ever set — and it loses
Base's fallback of shooting a breakable to unblock its own route.
**Recommend:** widen the roster. Direct "bots don't fire" cause on any map with turrets or `func_breakable`.

### F7. `rangebias` is never converted to cost units — goal choice is distance-blind

Base converts `rangebias` to travel-time units (`navigation.qc:1225-1226`) before
`f = ratingscale · (rangebias / (rangebias + cost))` at `:1418`. The port
([BotRoles.cs:87-90, :108-110](src/VortexArena.Server/Bot/BotRoles.cs:87)) compares raw qu against a cost
in seconds: with `rangeBias 2000` and costs of 2.8-17 s, the factor spans **0.9986 to 0.9917** — a 0.7%
spread across the entire map.

So distance is effectively ignored. Every bot picks the single highest-value item on the map from
anywhere, and all of Base's per-call-site tuning (2000 items/enemies, 4000 assault, 5000 dom/race, 10000
CTF/ons, 100000 KH keys) collapses to one behaviour. Bots spend their lives on cross-map treks: **fewer
engagements per minute (less firing) and far more time walking unfamiliar geometry (more wedging)**.

This is the one difference that is a leading cause of *both* symptoms.

**Options.** (a) Convert `rangebias` to cost units at the rating site. (b) Retune the constants to the
port's units.
**Recommend (a).** (b) would have to be redone per call site and would drift from Base.

### F8. Other confirmed fire-rate contributors

| # | Difference | Recommendation |
|---|---|---|
| F9 | `ChosenWeapon` can diverge from the weapon actually held; Base always runs `wr_aim` on `m_weapon` | Read the held weapon. Seven force-switch paths (ammo auto-switch, pickups, NIX, Overkill, Nexball) move it behind the brain's back for up to 0.5 s. |
| F10 | No `MAX_WEAPONSLOTS` `wr_aim` loop — only `ChosenWeapon`'s is consulted | Known registry gap. Low priority in stock (one slot), matters for Overkill/dual-wield. |
| F11 | Enemy scan adds a `CheckPvs` pre-filter Base doesn't have ([BotBrain.cs:1335](src/VortexArena.Server/Bot/BotBrain.cs:1335)) | Remove it, or widen to DP's entity-leaf-set semantics. A single sample point outside PVS drops the candidate with no trace, so the bot never acquires and never fires. |
| F12 | `ShouldAttack` refuses Frozen targets in *every* gametype; Base only in FreezeTag | Gate on FreezeTag. Nade-frozen players are the easiest kill on the map and the port declines them, then drops the enemy for 2 s. |
| F13 | Enemy-player rating drops armor from the advantage term; no compounding `ratingscale` | Port both. A fully-armored bot reads itself as even rather than advantaged and stops pressing fights. |
| F14 | LOS trace also runs on the ballistic path, where Base skips it (`aim.qc:395` is in the `else` arm) | Skip it for lobbed weapons. A mortar bot arcing over cover has its shot vetoed by the cover. |
| F15 | `findtrajectorywithleading` not ported — closed-form gravity drop instead of the 10-trace search | Medium effort. Opposite sign (port fires lobbed weapons Base declines) but the shots land short. Defer. |
| F16 | Enemy scan missing the see-through-transparent second pass and breakable deferral | Port with F6. |
| F17 | `bot_ai_enemydetectioninterval` / `_radius` / `bot_ai_chooseweaponinterval` registered but frozen as private consts ([BotBrain.cs:30-32](src/VortexArena.Server/Bot/BotBrain.cs:30)) | Read the cvars. Values match stock so behaviour is identical — but these are the first knobs anyone reaches for when investigating "bots don't fire enough", and turning them does nothing. |
| F18 | Tuba's `wr_aim` bypasses `bot_aim` in Base; port routes it through the full gate | Low. Tuba is second-from-last in every band. |
| F19 | Minelayer mine-limit block, OkNex charge-hold, Seeker tag/missile routing unmodelled | Low / inert at shipped balance. |
| F20 | Devastator fires on a refire cadence rather than Base's press/release | Opposite sign (more firing). Record as a divergence. |

---

## Part 3 — Per-bot personality: the `bots.txt` columns

Base's `READSKILL` block (`bot.qc:264-290`) parses twelve per-bot modifier columns. The port parses two.

| Column | Base use | Port |
|---|---|---|
| `bot_aggresskill` (argv11) | fire gate, cooldown | ✅ |
| `bot_aimskill` (argv13) | fire cone | ⚠️ applied to the cone but **not** to the filter blend (`aim.qc:242`) — a half-applied modifier that tightens a good-aim bot's cone for precision it isn't given |
| `bot_moveskill` (argv7) | bunnyhop gate | ❌ D32 |
| `bot_offsetskill` (argv14) | aim error | ❌ |
| `bot_mouseskill` (argv15) | turn rate | ❌ |
| `bot_thinkskill` (argv16) | aim-think clock | ❌ |
| `bot_aiskill` (argv17) | think interval | ❌ |
| `bot_dodgeskill` (argv8) | dodge, evade standoff | ❌ |
| `bot_weaponskill` (argv10) | combo timing | ❌ |
| `bot_rangepreference` (argv12) | range bands | ❌ |
| `havocbot_keyboardskill` (argv6) | key quantisation | ❌ |
| `bot_pingskill` (argv9) | simulated ping | ❌ (cosmetic) |

Verified against the 18-row `bots.txt`: every carried column has population mean **exactly 0.000**, so
the *average* bot is faithful. What is lost is the spread. 28% of the Base roster never misses the fire
cone at all; roughly a third have fast mouse; roughly a quarter walk approaches other bots bunnyhop.

**Options.** (a) Parse all twelve and thread them through. (b) Parse the four that touch the reported
symptoms (`moveskill`, `offsetskill`, `mouseskill`, `thinkskill`). (c) Leave as-is.
**Recommend (a).** The parse is one function ([BotPopulation.cs:812-833](src/VortexArena.Server/Bot/BotPopulation.cs:812))
and the threading is mechanical. Do it *after* the symptom fixes, so you're adding variance to correct
behaviour rather than to broken behaviour. Fix the half-applied `bot_aimskill` with it.

---

## Part 4 — Dead knobs, dead code, and tooling

**Registered but never read** (a server operator can set these and nothing happens):
`bot_wander_enable`, `bot_navigation_ignoreplayers`, `bot_ai_friends_aware_pickup_radius`,
`bot_ai_enemydetectionradius`, `bot_ai_enemydetectioninterval`, `bot_ai_chooseweaponinterval`,
`bot_ai_bunnyhop_turn_angle_max/_min/_reduction`, `bot_ai_bunnyhop_downward_pitch_max`, `bot_usemodelnames`.
**Recommend:** each is fixed by the corresponding feature above; add a CI check that every registered
`bot_*` cvar has at least one read site.

**Wrong or missing registrations:** `bot_ai_custom_weapon_priority_far/_mid/_close` registered with
**empty** defaults ([Cvars.cs:372-374](src/VortexArena.Server/Cvars.cs:372)) while the file's own comment
at `:320-321` says these must not drift from stock; `g_waypoints_for_items` never registered and read with
a hardcoded fallback of 2 against Base's 0; `bot_typefrag` registered twice
([Cvars.cs:191](src/VortexArena.Server/Cvars.cs:191) and `:334`); the ten `bot_ai_aimskill_order_*` cvars
read but never registered (values correct, so no behaviour change — but invisible to `search`/apropos).
**Recommend:** fix all; mechanical, and `tools/find-cvars.py` should catch the class.

**Dead code:** [BotController.cs](src/VortexArena.Server/Bot/BotController.cs) is a second, never-constructed
bot manager whose `DefaultSkill = 5` looks like where skill is decided (the live path is
`BotPopulation.SpawnBot` reading `Cvars.Skill`); `BotNavigation.ForceJump()` has no callers;
`BotDanger`'s `committed` parameter is honoured but every one of its four call sites passes `false`;
`Nav.LastTeleportTime` is write-only; the waypoint symmetry header is parsed with the wrong key format
and read by nothing. **Recommend:** delete `BotController`, wire `committed` (it's D18's gate), fix or
delete the rest.

**Tooling, no gameplay effect** — flagged so they aren't over-weighted: the entire `bot_cmd` scripting
language (`scripting.qc`, 24 opcodes, the `sv_cmd` front end); the live-server waypoint editor
(`cmd wpeditor` listed in [ClientCommandRegistry.cs:146](src/VortexArena.Server/ClientCommandRegistry.cs:146)
but with no handler); `botframe_autowaypoints`; `navigation_markroutes_inverted`; `bot_endgame` colour
restore. **Recommend:** leave for now, except the waypoint editor — you will want it once you start
authoring waypoints for Vortex-original maps.

---

## Implementation status (2026-08-03)

All seven stages implemented; full suite green (4227 tests). Landed items are annotated in place below.
**What was NOT done, and why:**

| # | Item | Why it is still open |
|---|---|---|
| D14 | `bot_navigation_ignoreplayers` | **Attempted and reverted.** Switching tracewalk to `MOVE_NORMAL` needs an ignore entity, and none of the eight `CanWalk` call sites pass one — so every walk collided with the walking bot's own body, nothing was reachable, and bots stopped moving entirely (caught by the live-loop test). Needs the walker plumbed through `CanWalk`, plus a dedicated trace entity for `AutoLink` (which must stay player-blind: the port builds the graph lazily on the first frame with bots present, so live player positions would be baked into the link set). |
| D12, D25 | Tracewalk budget (96/tick), seed caps (12 walks / 8 seeds / 2250qu) | Both are deliberate perf bounds from the 2026-07-11 variance program, with measured tails behind them. Raising them is a perf/behaviour trade that needs `./vx perf-smoke` evidence, not a guess. Note the strategy token means only one bot rates per frame, so contention is lower than the audit assumed. |
| D15 | `navigation_shortenpath` | Real gap, self-contained, ~70 lines. Not attempted. |
| D27, D28 | Swimming/resurfacing, ladder descent | The largest remaining behavioural gaps. Each is a movetogoal branch of its own rather than a patch. |
| F10 | `wr_aim` per-slot loop | Inert at one weapon slot; matters for Overkill/dual-wield. |
| F15 | `findtrajectorywithleading` | Opposite sign (port fires lobbed weapons Base declines). |
| — | Bot scripting, waypoint editor commands | Tooling, no gameplay effect. |

Two defects were found in the fixes themselves during implementation, both by the tests, both worth recording
because they are the same shape as the originals: `_lastComboTime` defaulting to 0 froze weapon choice for the
first second of every map, and the D26 solid-nudge in `Add()` mutated authored waypoint origins (it belongs on
the derived path only).

## Part 5 — Suggested sequence

Each stage is independently shippable and independently measurable.

1. **Data** (D1, D2, D3). Ship `.waypoints.cache` + `.waypoints.hardwired`; always create entity-derived
   waypoints. Zero gameplay-code risk, largest single effect, and it also removes a ~1 s hitch.
2. **Fire rate** (F1, F2, F3, F4, F5). Ammo gate, combo un-inversion, LOS mask, per-weapon
   `shot_accurate`, reload check. Small, surgical, and F1 alone should be visible immediately.
3. **Stuck recovery** (D4, D5, D11, D24). `navigation_unstuck` + reject unreachable goals + force re-rate
   on empty stack. This is the safety net that makes everything below optional rather than urgent.
4. **Steering correctness** (D6, D7, D8, D17). Danger blend instead of override; remove or gate
   `CombatMovement`; flat direction; ledge-brake gates.
5. **Goal quality** (F7, D33, D34, F13). `rangebias` units, teammate dedup, hazard filters, armor term.
6. **Traversal** (D9, D12-D15, D18-D23, D27-D31). Lookahead, budget, jumppads, ladders, water.
7. **Personality** (Part 3) and **hygiene** (Part 4).

---

## Part 6 — Beyond parity: industry practice worth adopting

These are *additions*, not parity items. Each is compatible with the Xonotic bot design.

### Observability first — you cannot tune what you cannot see

Nothing here is measurable today. The bot test harness runs on a
**flat floor** ([BotLiveLoopTests.cs:42](tests/VortexArena.Tests/BotLiveLoopTests.cs:42)), which is why
none of the above was caught.

- **A stuck-rate metric.** Per-bot: time with |velocity| < threshold while a goal is set; goal-abandon
  rate; re-rate-without-progress count. Emit as a cvar-gated per-match summary. This turns "bots seem
  stuck" into a number you can bisect.
- **A fire-rate metric.** Shots per second of enemy-contact time, and a histogram of *why* a shot was
  declined (no enemy / outside cone / fire timer / LOS / ammo / weapon state). Given F1-F5, a decline-reason
  histogram would have found all five in an afternoon.
- **Bot AI replay.** You already have demos; record goal, route, enemy, and decline reason per think, and
  render the route + probes in the existing debug draw. This is Base's `bot_debug_goalstack` /
  `bot_debug_tracewalk`, which are also unported.
- **Soak test in CI.** Headless, 8 bots, every shipped map, 3 minutes, assert stuck-rate below a
  threshold and zero bots with a null goal for >10 s. This is the regression net the flat-floor test can
  never be.

### Navigation

- **Navmesh instead of (or beside) waypoints.** Recast/Detour-style navmesh generation from the BSP would
  eliminate the entire class of "the graph has no edge here" bugs (D1-D3, D26, D30), give real string-pulled
  paths instead of node-to-node hops, and remove the authoring burden for Vortex-original maps. Large
  project; the right framing is a *second* navigation backend behind the existing `BotNavigation` seam, so
  waypoints stay the default and stay parity-faithful. Consider it once the port is behaviourally at parity —
  not before, or you'll be debugging two systems.
- **Path smoothing / funnel.** Even on the waypoint graph, string-pulling the corridor between consecutive
  waypoints (rather than steering at each node centre) subsumes D9's corner-cut and D36's `get_closer_dest`
  and looks markedly better.
- **Local steering separated from global planning.** The recurring shape of D6, D7 and D9 is that local
  reactions *replace* the planned move instead of perturbing it. A small explicit steering layer — planned
  direction plus weighted avoidance/separation/danger terms, normalized once — makes that structurally
  impossible. Base already does this at `havocbot.qc:1269`; formalising it is cheap and would have prevented
  three of the worst findings here.
- **Agent separation.** None of Base or the port models other bots as obstacles for *steering* (only, in
  Base, for tracewalk). A separation term is the standard fix for the doorway-jam behaviour in D14/D33.
- **Stuck detection as a first-class state machine.** Rather than D4's single recovery, the usual pattern is
  escalating: re-path → jump → strafe-and-retry → teleport-to-nearest-node (dev builds only) → suicide. Ship
  the escalation with a metric on which rung fires; the histogram tells you where the real bug is.

### Combat

- **Tunable, measurable difficulty.** `skill` currently scales ~12 coupled quantities. Splitting *aim*
  (error, turn rate, reaction) from *tactics* (weapon choice, positioning, item priority) is the standard
  approach and would make skill 3 feel like a weak player rather than a broken one.
- **Reaction-time modelling.** Base fakes this with `bot_pingskill` and the aim-think clock. An explicit
  "target acquired at T, may fire at T + reaction(skill)" is clearer, easier to tune, and reads as more
  human than filter lag.
- **Deliberate miss rather than injected aim error.** Injecting error into the *aim* also suppresses
  *firing* (it widens the measured deviation), which is why F4 and the offset terms have such an outsized
  effect. Aiming truly and then perturbing the *shot* decouples the two and gives you a directly tunable
  accuracy number.
- **Weapon choice as expected-damage-per-second** over the predicted engagement, rather than an ordered
  priority list per range band. Handles ammo (F1) and reload (F5) as natural terms rather than as bolt-on
  gates, and generalises to Overkill/mutator loadouts.

### Process

- **Extend the parity registry to cover the movement path at statement granularity.**
  `planning/parity/registry/bot-ai.yaml` tracks 29 features and did not flag D6, D7, D8, D9 or F2 — because
  those rows are marked `faithful` at the function level while the bodies diverge. `havocbot_movetogoal`
  is ~470 lines in Base and ~110 in the port; a row that says "movetogoal: faithful" is not carrying
  information. Split it.
- **Treat "shipped data files" as parity surface.** D1-D3 were invisible to a source-only audit and are
  the biggest finding in the report. `tools/parity-asset-check.py` should diff the *file set* per map
  between `Base/data/xonotic-maps.pk3dir/maps/` and `data/maps/*.pk3`.
- **A caller-chain lint.** The port's dominant failure mode — feature implemented, never called; cvar
  registered, never read — recurs in 11 findings here and is mechanically detectable. A Roslyn analyser or
  even a grep-based CI step over `Bot/` would catch it at review time.

---

*Audit: 22 agents, 4.1 M tokens, 1404 tool calls. 143 differences verified, 3 refuted. Raw per-agent
output: `subagents/workflows/wf_f0cc379b-506/journal.jsonl`.*
