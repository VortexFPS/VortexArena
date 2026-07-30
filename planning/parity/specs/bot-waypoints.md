# Bot waypoints (navigation graph, pathfinding, steering) — parity spec

**Base refs:** `server/bot/default/waypoints.qc` (+`.qh`), `server/bot/default/navigation.qc` (+`.qh`), `server/pathlib/*`, `server/steerlib.qc`, `server/bot/api.qh` (WAYPOINTFLAG_*)
**Port refs:** `src/VortexArena.Server/Bot/Waypoint.cs` (graph + load + A*), `src/VortexArena.Server/Bot/BotNavigation.cs` (goal stack + steering), `src/VortexArena.Server/Bot/BotTracewalk.cs` (reachability), `src/VortexArena.Server/GameWorld.cs:LoadWaypointNetwork`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The waypoint subsystem is the bots' map graph: a set of nodes (point "waypoints" and box "wayboxes")
joined by directed, cost-weighted links. Authored maps ship a `maps/<map>.waypoints` node file, a
`.waypoints.cache` precompiled link file, and an optional `.waypoints.hardwired` map-maker link file;
the server also auto-generates link/item/teleporter waypoints at runtime. Each strategy frame a bot
floods the graph from its position (Dijkstra: `navigation_markroutes`) and walks back-pointers to build
a goal stack toward a chosen objective (`navigation_routetogoal`); the steering layer (`havocbot_movetogoal`,
`steerlib.qc`) then converts the current goal into a wish-move with jump/crouch decisions. All of this is
**authority (server) side** — bots only exist on the server; nothing here is networked or client-side.
`pathlib/*` is a *separate* grid A* used by turrets/monsters, not the waypoint graph (noted but largely
out of scope for this unit). The waypoint **editor** (`g_waypointeditor`) is a dev tool for authoring the
`.waypoints` files in-game.

## Base algorithm (authoritative)

### Waypoint entity + flags  (`waypoints.qc:waypoint_spawn`, `api.qh`)
- A waypoint is a `spawnfunc(waypoint)` edict in the `g_waypoints` intrusive list. `m1==m2` → a point
  waypoint (collapsed to a player-hull-sized solid trigger that is `move_out_of_solid`'d, then resized to
  zero); `m1!=m2` → a waybox (`wpisbox`), keeping its volume.
- Flags (`api.qh`, exact bits): `GENERATED=BIT(23)`, `ITEM=BIT(22)`, `TELEPORT=BIT(21)`,
  `PERSONAL=BIT(19)`, `PROTECTED=BIT(18)`, `USEFUL=BIT(17)`, `DEAD_END=BIT(16)`, `LADDER=BIT(15)`,
  `JUMP=BIT(14)`, `CUSTOM_JP=BIT(13)`, `CROUCH=BIT(12)`, `SUPPORT=BIT(11)`. `NORELINK=BIT(20)` is
  deprecated. `WPFLAGMASK_NORELINK = TELEPORT|LADDER|JUMP|CUSTOM_JP|SUPPORT` — these keep hand-authored
  outgoing links and are never auto-relinked.

### Links (adjacency)  (`waypoints.qc:waypoint_addlink*`, `waypoint_removelink`, `waypoint_clearlinks`)
- Each waypoint has up to **32** outgoing links in flat fields `wp00..wp31` with parallel costs
  `wpXXmincost`. Links are **kept sorted ascending by cost**; `waypoint_addlink_customcost` inserts into
  the sorted array and drops the most expensive link when full (cap 32). Items hold *incoming* links in
  the same fields (reverse convention) so a previously-tested unwalkable link is remembered with cost 999.
- `waypoint_addlink` auto-computes cost via `waypoint_getlinkcost` unless the source is a custom-jumppad
  (then `waypoint_addlink_for_custom_jumppad` uses the jumppad push time). SUPPORT links set
  `to.SUPPORT_WP = from`.
- Up to **8** hardwired links per waypoint are mirrored in `wphw00..wphw07` so they can be identified and
  restored after a relink; hardwired links are NOT sorted.

### Link cost model  (`waypoints.qc:waypoint_getlinkcost`/`waypoint_gettravelcost`/`waypoint_getlinearcost*`)
- Box endpoints are clamped to the nearest point on each box before costing.
- `waypoint_getlinearcost(dist) = dist / (sv_maxspeed * 1.25)` if `skill >= bot_ai_bunnyhop_skilloffset`,
  else `dist / sv_maxspeed`. **Skill-dependent.**
- Underwater: `dist / (sv_maxspeed * 0.7)`. Crouched: `dist / (sv_maxspeed * 0.5)`.
- `waypoint_gettravelcost`: if both endpoints submerged → underwater cost; if both crouch → crouch cost;
  else linear cost, plus a **fall-time** term when `height = from.z - to.z > jumpheight_vec.z` and
  `sv_gravity > 0`: `sqrt(height/(gravity/2))` (a JUMP source adds `jumpheight_time + sqrt((height+jh)/(g/2))`),
  taking `max(flatcost, fallcost)`. Mixed water/crouch endpoints get a `(c + slowcost)/2` half-path average.
  **NOTE (verified gap):** the port's `TravelCost` accepts a `fromIsJump` argument but never uses it in the
  fall term — it always computes the non-jump `sqrt(height/(g/2))`, so the JUMP-source variant is missing.
- Constants: `sv_maxspeed` default **320**, `sv_gravity` default **800**, `sv_jumpvelocity` default **270**
  (→ jumpheight_vec.z ≈ 130 / jumpheight_time derived per frame in `bot_calculate_stepheightvec`).

### Auto-relink  (`waypoints.qc:waypoint_think`, `waypoint_schedulerelink*`)
- `waypoint_think` is the (slow) per-waypoint relink: for every other waypoint, if bboxes overlap → link
  both ways; else cull by **PVS** (`checkpvs`), then by XY distance (`maxdist = 1050`, **100** for
  crouch↔normal), then bidirectional **`tracewalk`** reachability (`set_tracewalk_dest_2` + `tracewalk`).
  Box / JUMP / SUPPORT waypoints forbid certain link directions. Stats tracked in
  `relink_total/walkculled/pvsculled/lengthculled`. Scheduled via `setthink`+`nextthink=time` (runs next
  server frame), so a full relink is amortized across frames.

### File load/save  (`waypoints.qc:waypoint_loadall`/`waypoint_load_links`/`waypoint_load_hardwiredlinks`/`waypoint_save*`)
- `GET_GAMETYPE_EXTENSION()` = `.race` in Race, else `""`; loaders try `maps/<map><ext>.waypoints` then
  fall back to `maps/<map>.waypoints`.
- `.waypoints`: optional `//WAYPOINT_VERSION`/`//WAYPOINT_SYMMETRY`/`//WAYPOINT_TIME` comment header
  (`WAYPOINT_VERSION = 1.04`), then triples (m1, m2, flags). `WAYPOINTFLAG_NORELINK__DEPRECATED` is masked off.
- `.waypoints.cache`: `from*to` lines (each direction explicit), version+time gated against the loaded
  `.waypoints` (mismatch → discard cache and `waypoint_schedulerelinkall`). On success sets
  `botframe_cachedwaypointlinks`. Endpoints matched within **1** unit (`findradius 1`).
- `.waypoints.hardwired`: `from*to` lines (`*`-prefixed = "special" link, e.g. jump/teleport extras),
  `#`/`//` comments skipped, matched within **5** units. Marked via `waypoint_mark_hardwiredlink`.

### Runtime auto-waypoints  (`waypoints.qc:waypoint_spawnforitem*`, `waypoint_spawnforteleporter*`, `botframe_autowaypoints`)
- `waypoint_spawnforitem` (gated by `bot_waypoints_for_items`) drops a `GENERATED|ITEM` waypoint under
  each pickup (fixed to the floor via `waypoint_fixorigin`).
- `waypoint_spawnforteleporter`/`_wz` make a `GENERATED|TELEPORT` box over the trigger + a `GENERATED`
  destination waypoint, with a single one-way `wp00` link (cost = teleport/push travel time). Oblique
  warpzones add a JUMP flag.
- `botframe_autowaypoints` (when `g_waypointeditor_auto`) auto-builds connecting waypoints toward real
  players and (`>=2`) deletes "useless" waypoints (`botframe_deleteuselesswaypoints`: keeps item/teleport/
  ladder/protected/dead-end/triangle-useful nodes).

### Pathfinding flood  (`navigation.qc:navigation_markroutes*`/`navigation_routetogoal`/`navigation_findnearestwaypoint`)
- `navigation_findnearestwaypoint(ent, walkfromwp)`: prefers a box containing the entity; else scans all
  waypoints, gating each by `navigation_waypoint_will_link` (LOS `traceline` within `bestdist=1050` +
  directional `tracewalk`), keeping the cheapest. Falls back to a jumppad's nearest waypoint.
- `navigation_markroutes(this, fixed_source)`: resets all `wpcost=1e7`; seeds the nearest reachable
  waypoints (`navigation_markroutes_nearestwaypoints`, expanding search radius: on-ground
  increment 750/max 50000, in-air 500/1500) or a fixed source; then a **Dijkstra-ish relaxation** flood
  (`wpfire` worklist, `navigation_markroutes_checkwaypoint` updates `wpcost`/`enemy` back-pointer using
  link `mincost` + per-waypoint `dmg` danger bias + teleport cost). `_inverted` floods toward the bot.
- `navigation_routetogoal(this, e, start)`: pushes the goal; if directly `tracewalk`-reachable, done;
  else routes via `e.nearestwaypoint`/`enemy` back-pointer chain, pushing each waypoint onto
  `goalcurrent`/`goalstack01..31` (depth **32**). Optimizes by skipping the nearest waypoint when a
  shortcut is walkable. Handles teleport goals (force `wp00` destination), dynamic/moving goals
  (`bot_ai_strategyinterval_movingtarget`), and item incoming-link caching.

### Steering  (`havocbot.qc:havocbot_movetogoal`, `steerlib.qc`)
- The goal stack is consumed each frame: `navigation_poptouchedgoals` pops reached goals; the current
  goal drives a wish-move. JUMP/CROUCH/LADDER/TELEPORT flags govern jump/crouch/climb/commit behavior.
  Obstacle/step detection via `tracebox` ahead at foot/step/jump heights → jump. Danger/ledge → brake.
  `havocbot_checkdanger(this, dst_ahead)` classifies the ground the bot is about to occupy: an eye→ahead
  `traceline` then a 3000qu down-trace returns 0 safe / 1 SKY-void / 2 cliff (>100qu below feet & goal) /
  3 lava-slime / 4 `trigger_hurt` in the fall column. This IS ported, faithfully and live —
  `BotDanger.CheckDanger`/`HitsTriggerHurt`, wired at `BotBrain.cs:291`.
  `havocbot_bunnyhop` keeps the bot jumping toward far goals (skill-gated, direction-cone gated,
  jump-distance-vs-remaining gated). `steerlib_*` provides pull/push/arrive/flock/traceavoid primitives.
- `havocbot_checkgoaldistance`: >0.5 s without closing on the goal (XY and Z) → unstuck (clearroute).
- `botframe` (bot.qc) drives `waypoint_loadall`+`waypoint_load_links` at map start, then per-frame
  `botframe_updatedangerousobjects`, `botframe_showwaypointlinks` (editor), `botframe_autowaypoints`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `spawnfunc(waypoint)` edict + `wpflags` | `Waypoint` class + `WaypointFlags` enum (Waypoint.cs) | Bit values match Base exactly. |
| `g_waypoints` + link bookkeeping | `WaypointNetwork` (Waypoint.cs) | 32 flat fields → one `List<WaypointLink>` adjacency. |
| `waypoint_getlinkcost`/`gettravelcost`/`getlinearcost*` | `WaypointNetwork.TravelCost`/`LinearCost` | Skill-dependent bunnyhop 1.25x IS wired (Skill seeded from server skill in ForMap). See gaps: NOT sorted; JumpHeight constant wrong (130 vs ~45.56). |
| `waypoint_addlink*` (sorted, cap 32, drop-furthest) | `WaypointNetwork.Link`/`AddLinkOnce` | Unsorted, **no 32-link cap** in load path (AutoLink caps via `maxLinksPerNode`). |
| `waypoint_think` auto-relink (PVS+dist+tracewalk, both dirs) | `WaypointNetwork.AutoLink` | Distance + `BotTracewalk` gate; **no PVS cull**; only when no `.cache` ships. |
| `waypoint_loadall` (.waypoints) | `WaypointNetwork.LoadFromText` | Faithful triple parse; symmetry header parsed but unused. |
| `waypoint_load_links` (.cache) | `WaypointNetwork.LoadLinks` | Faithful; FindAt within 1u via spatial hash. |
| `waypoint_load_hardwiredlinks` | `WaypointNetwork.LoadHardwiredLinks` | Within 5u; marks source `CustomJp` (not a dedicated hardwired flag). |
| `.race` gametype extension fallback | `GameWorld.LoadWaypointNetwork` (ext=".race") | Faithful. |
| `waypoint_spawnforitem`/`_force` | `GenerateFromEntities` (item/spawn nodes) | Only runs when **no** `.waypoints` file ships. |
| `waypoint_spawnforteleporter`/`_wz` | `GenerateFromEntities` (teleport/jumppad boxes) | Generated path only; no warpzone-angle handling; no oblique-JUMP. |
| `navigation_markroutes` (Dijkstra flood from bot) | `WaypointNetwork.FindPath` (A* between two nodes) | Different algorithm shape; no danger `dmg` bias; see gaps. |
| `navigation_findnearestwaypoint` | `WaypointNetwork.Nearest` | Box-contains then nearest-reachable tracewalk; faithful intent. |
| `navigation_routetogoal` + goalstack | `BotNavigation.SetGoal` + `_goals` stack | Faithful structure; teleport-goal/dynamic-goal shortcuts simplified. |
| `havocbot_movetogoal` steering | `BotNavigation.Steer` | jump/crouch/ladder/obstacle/brake/bunnyhop ported. |
| `havocbot_bunnyhop` | `BotNavigation.Bunnyhop` | Faithful gating incl. jump-distance formula; misses per-bot `bot_moveskill` term + danger suppression. |
| `havocbot_checkdanger` (void/cliff/lava/trigger_hurt) | `BotDanger.CheckDanger`/`HitsTriggerHurt` | **Faithful + LIVE** (BotBrain.cs:291); 5-way classify, matching 3000/−100/SKY constants. |
| `havocbot_checkgoaldistance` unstuck | `BotNavigation.CheckGoalProgress` | Faithful 0.5 s / 10qu shrink watchdog. |
| `steerlib_*` primitives | NOT IMPLEMENTED (folded into Steer) | flock/swarm/traceavoid/beamsteer absent; only pull/brake. |
| `pathlib_astar` grid A* | NOT IMPLEMENTED | Separate turret/monster pather; out of scope here. |
| waypoint editor (`waypoint_spawn_fromeditor`, symmetry, hardwire) | NOT IMPLEMENTED | Dev authoring tool; port consumes files but cannot author them. |
| `waypoint_save*` (.waypoints/.cache/.hardwired writers) | NOT IMPLEMENTED | Read-only; no save path. |
| `botframe_autowaypoints`/`deleteuselesswaypoints` | NOT IMPLEMENTED | No runtime auto-waypointing toward players. |

## Parity assessment

**Live?** Yes. `GameWorld.LoadWaypointNetwork` → `WaypointNetwork.ForMap` (loads `.waypoints`+`.cache`+
`.hardwired` with `.race` fallback, else auto-generates) is called once on the first bot frame by
`BotPopulation` (line 165); `BotBrain.Think` (StrategyTokenHeld block) calls `Nav.SetGoal` (A* over the
graph) and every frame `Nav.Steer` produces the wish-move consumed as the bot's input. The whole chain is
wired on the real match path.

**Faithful dimensions.** Node model, box/point handling, flag bits, file formats (`.waypoints`/`.cache`/
`.hardwired` parsing incl. quoted-vector handling and `.race` fallback), nearest-waypoint query, goal-stack
route building, the steering decisions (jump/crouch/ladder/obstacle-step/ledge-brake), bunnyhop gating, and
the no-progress unstuck watchdog are all close ports with matching constants (MaxSpeed 320, Gravity 800,
JumpHeight 130, strategyinterval 7, bunnyhop skilloffset 7, deviation 20, ignoregoal 3).

**Gaps (concrete):**
1. **Cost model is not skill-dependent.** Base `waypoint_getlinearcost` divides by `sv_maxspeed*1.25` for
   skill ≥ `bot_ai_bunnyhop_skilloffset` (7); the port always uses base speed. High-skill bots compute
   slightly higher path costs than Base, which can change which route ties win on long/jump-heavy maps.
1b. **JUMP-source fall-cost variant missing.** Base adds `jumpheight_time + sqrt((height+jh)/(g/2))` when
   the source waypoint has `WAYPOINTFLAG_JUMP`; the port's `TravelCost` threads a `fromIsJump` arg but
   never uses it in the fall term — it always uses the non-jump `sqrt(height/(g/2))`. (The first-pass draft
   incorrectly claimed this variant matched.)
2. **No STATIC per-waypoint danger bias in pathfinding.** Base `navigation_markroutes` adds each waypoint's
   `.dmg` (set by `botframe_updatedangerousobjects`) to its cost so routes pre-emptively detour around
   lava/rockets; the port A* uses pure link cost. **However** the port DOES handle danger at runtime via the
   live `BotDanger.CheckDanger` per-frame probe (brake/clearroute), so a bot reacts to a hazard on the path —
   it just doesn't pre-route around it. `.dmg` is purely runtime, so this gap affects live-combat detours,
   not static authored routing.
3. **No PVS cull in AutoLink.** Base `waypoint_think` culls candidate links by `checkpvs` before the
   tracewalk; the port's AutoLink does distance + tracewalk only. Behaviorally similar (tracewalk rejects
   through-wall links anyway) but slower and can keep some links Base would cull. AutoLink only runs when
   no `.cache` ships, so shipped maps are unaffected.
4. **Links not cost-sorted; no 32-link cap on the load path.** Base keeps `wp00..wp31` sorted ascending and
   caps at 32, dropping the furthest. The port's `LoadLinks`/`LoadHardwiredLinks` append unsorted with no
   cap. A* tolerates unsorted/over-32 adjacency, so route *results* match, but a waypoint with >32 cache
   links keeps them all (Base would drop the costliest).
5. **Hardwired links use the `CustomJp` flag as a marker**, not a dedicated hardwired-link concept, and
   the port has no `wphwXX` mirror — there's no save/round-trip, so this only matters for re-export (absent).
6. **Runtime auto-waypointing toward players is absent** (`botframe_autowaypoints`/`deleteuselesswaypoints`).
   Base can grow/prune the graph live in editor-auto mode; the port's graph is static after load.
7. **`steerlib` flocking/swarm/traceavoid/beamsteer primitives are absent** (folded into a simpler Steer).
   No bot flocking behavior; obstacle avoidance is a single forward probe rather than the trident/funnel sweep.
8. **Waypoint editor + file writers are absent.** The port cannot author or re-save `.waypoints` files;
   it is a read-only consumer. (Acceptable for a player-facing port; flagged as missing tooling.)
9. **Teleporter/warpzone generation is simplified** in the generated-graph path: no warpzone-angle math,
   no oblique-warpzone JUMP flag, jumppad push-time cost approximated.

**Intended divergences.** The graph keeps Dijkstra state out of the node (computed per-search) so the
network is immutable and shareable across bots — a deliberate design change from QC's per-edict
`wpcost`/`enemy` scratch, with identical routing results. The A*-between-two-nodes shape (vs Base's
single-source flood) is a deliberate equivalent: both yield an optimal least-cost path; the port doesn't
need Base's all-pairs flood because it re-plans per goal. These are design choices, not gaps — but they are
the reason the danger-bias (gap 2) and skill-cost (gap 1) terms are missing and must be re-added on top.

## Verification
- **Liveness:** traced caller chain `GameWorld.LoadWaypointNetwork` → `BotPopulation:165` → `BotBrain.Think`
  → `BotNavigation.SetGoal`/`.Steer` by reading the source (verified). No runtime in-game observation done.
- **Constants:** Base cvar defaults read from `xonotic-data.pk3dir` cfgs (strategyinterval 7,
  bunnyhop skilloffset 7, deviation 20, ignoregoal 3); physics defaults (maxspeed 320 / gravity 800 /
  jumpvelocity 270) are the known Xonotic balance defaults, matched in the port constants — verified by code read.
- **Cost-model / pathfinding gaps:** established by side-by-side read of `waypoint_gettravelcost` /
  `navigation_markroutes` vs `WaypointNetwork.TravelCost` / `FindPath`. Not behaviorally A/B-tested in game.
- No unit tests located for the bot-waypoint graph in the port test suite (none referenced in this read).

## Open questions
- **(RESOLVED)** `.dmg` is purely runtime — set only by `botframe_updatedangerousobjects` (waypoints.qc:1901),
  never loaded from the `.waypoints` file. So gap 2 affects only live-combat detours, not static routing,
  and is partially mitigated by the live `BotDanger` per-frame brake. Re-adding it would require porting
  `botframe_updatedangerousobjects` (explicitly noted unported at `BotPopulation.cs:190`).
- Does the port ever exceed 32 cache links on a real shipped `.waypoints.cache`? If shipped caches are
  always ≤32 per node, gap 4 is moot for real maps and only matters for AutoLink (already capped).
- Is the skill-dependent cost (gap 1) observable as a different *chosen route* on any common map, or does it
  only shift absolute costs without reordering routes? Needs an in-game route comparison at skill 7+.
