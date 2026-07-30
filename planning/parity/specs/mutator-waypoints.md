# Waypoint sprites (`mutator-waypoints`) — parity spec

**Base refs:** `common/mutators/mutator/waypoints/waypointsprites.qc` / `.qh` · `common/mutators/mutator/waypoints/all.inc` / `all.qh` · `server/impulse.qc` (player-deploy impulses) · `xonotic-client.cfg` / `xonotic-server.cfg` (cvar defaults)
**Port refs:** `src/VortexArena.Common/Gameplay/Waypoints/Waypoints.cs` · `game/client/WaypointSpriteLayer.cs` · `game/net/ServerNet.cs` (`SendWaypoints`) · `game/net/ClientNet.cs` (`HandleWaypoints`) · `src/VortexArena.Common/Gameplay/GameTypes/Ctf.cs` (`CollectWaypoints`) · `game/hud/RadarPanel.cs` (radar icons)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

> Note: the unit is named `mutator-waypoints` per the audit assignment, but in Base it is **not a gameplay
> mutator**. It is `REGISTER_MUTATOR(waypointsprites, true)` — a always-on shared subsystem registered through the
> mutator framework purely to get its net-entity + hook plumbing. It provides the *waypoint sprite* (objective
> marker / radar icon / player ping) system used by nearly every gametype. There is no `g_waypoints` toggle.

## Overview
A waypoint sprite is a networked marker tied to a world position (fixed, or following an owner entity). The
server spawns/updates/kills them through the `WaypointSprite_*` API; each is a `Net_LinkEntity` edict with a
per-client `SendEntity` + `Customize` (team/rule visibility filter). The client (`Draw_WaypointSprite`,
CSQC, every frame) projects each to screen and draws a floating ICON or TEXT label, a directional ARROW that
clamps to the screen edge when the objective is off-screen/behind, an optional HEALTH BAR (objectives with
health, or a build-progress bar), plus distance / edge / crosshair / lifetime fades and a blink. The same
entities feed the team radar (`teamradar_icon`). Producers: CTF flags & carriers, Domination/Onslaught control
points & generators, Key Hunt keys & carriers, Assault objectives, Keepaway/TeamKeepaway/Nexball balls,
FreezeTag frozen/reviving markers, Race start/finish/checkpoints, item-respawn (itemstime) markers, monster /
weapon / vehicle / turret / buff markers, and **player-deployed pings** (`waypoint_personal/here/danger` +
the team "HELP ME" attach).

## Base algorithm (authoritative)

### Registry of waypoint kinds  (`all.inc` / `all.qh:REGISTER_WAYPOINT`)
- A `REGISTRY(Waypoints, BITS(7))` table. Each `WP_*` def: `netname` (used as the networked sprite key), `m_name`
  (localized text shown when there is no icon), `m_icon` (HUD icon sprite name; "" → text), `m_color` (default
  tint), `m_blink` (blink multiplier, default 1). ~70 defs across all gametypes (`all.inc` lines 3-84).
- Separate `RadarIcons` registry: `RADARICON_*` → `m_radaricon` (0 = none, 1 = on-radar). The waypoint carries a
  `cnt` icon id; the client maps `teamradar_icon` → `gfx/teamradar_icon_<n>`.
- Special-case blink in `spritelookupblinkvalue`: superweapon `WP_Weapon` → 2, `WP_Item` → item's
  `m_waypointblink`, `WP_FlagReturn` → 2, else 1.

### Spawn / lifecycle  (`waypointsprites.qc:WaypointSprite_Spawn` and helpers)
- `WaypointSprite_Spawn(spr, lifetime, maxdistance, ref, ofs, showto, t, own, ownfield, hideable, icon)`: makes a
  `sprite_waypoint` edict. `fade_time = lifetime` (negative ⇒ never auto-fade); `teleport_time = time +
  |lifetime|`; follows `ref.origin + ofs` if `ref` set, else fixed at `ofs`. `enemy = showto` (personal WP),
  `team = t`, `owner = own` (auto-kill when owner gone, one per `ownfield`), `currentammo = hideable`,
  `fade_rate = maxdistance`. `think = WaypointSprite_Think` (runs every frame), `cefc =
  WaypointSprite_Customize`, `reset2 = WaypointSprite_Reset`. `Net_LinkEntity`.
- `SpawnFixed(spr, ofs, own, ownfield, icon)` = Spawn with lifetime 0, maxdistance 0, no ref, hideable **true**.
- `DeployFixed(spr, limited_range, player, ofs, icon)`: team = `player.team` if teamplay else 0; maxdistance =
  `waypointsprite_limitedrange` if limited; lifetime = `waypointsprite_deployed_lifetime`; hideable false.
- `DeployPersonal(spr, player, ofs, icon)` = Spawn, lifetime 0, owner=player, field `waypointsprite_deployed_personal`.
- `Attach(spr, player, limited_range, icon)`: follows player at `'0 0 64'`, lifetime
  `waypointsprite_deployed_lifetime`. Returns NULL if player already has an FC waypoint.
- `AttachCarrier(spr, carrier, icon)`: kills the carrier's attached WP, follows at `'0 0 64'`, lifetime 0, and if
  carrier has health sets a health bar (`UpdateMaxHealth/UpdateHealth` from `healtharmor_maxdamage(...)`).
- `Think`: if `fade_time && time >= teleport_time` ⇒ Kill. If following an owner, snap origin to
  `owner.origin + view_ofs`. Re-arms `nextthink = time` (runs every frame).
- `Disown(wp, fadetime)`: detach owner, then `FadeOutIn(fadetime)`. `FadeOutIn` sets/accelerates the fade timer.
- `Kill`: clears the owner's ownfield ref, deletes the edict. `Reset`: kill if it had a fade_time.
- Player lifecycle hooks: `PlayerDead` (disown attached + detach carrier, `deadlifetime`), `PlayerGone`,
  `ClearOwned`, `ClearPersonal`, `DetachCarrier`.

### Update API  (`waypointsprites.qc`)
- `UpdateSprites(e, m1, m2, m3)`: set the three picture netnames (`model1/2/3`) → SendFlags 2/4/8. The three are
  for the three visibility rules (see below).
- `UpdateHealth(e, f)`: quantized to `max_health/40` steps; `SendFlags |= 0x80`.
- `UpdateMaxHealth`, `UpdateBuildFinished` (build-progress bar via `pain_finished`), `UpdateOrigin` (SendFlags 64),
  `UpdateRule(team, rule)` (SendFlags 1), `UpdateTeamRadar(icon, col)` (SendFlags 32).
- `Ping(e)`: anti-spam (0.3s); sets `cnt |= BIT(7)` → radar circle pulse, SendFlags 32.
- `HelpMePing(e)`: Ping + set `waypointsprite_helpmetime = time + deployed_lifetime`.

### Visibility rules  (`SPRITERULE_*`, `waypointsprites.qh:8-10`; filter `WaypointSprite_visible_for_player`)
- `SPRITERULE_DEFAULT (0)`: personal WP (`enemy` set) only to that viewer. If `team` set: only same-team **and**
  the viewer must be `IS_PLAYER` (not spectator-as-player). Otherwise everyone.
- `SPRITERULE_TEAMPLAY (1)`: client picks netname3 (spectator), netname2 (own team), netname (enemy team).
- `SPRITERULE_SPECTATOR (2)`: only when `sv_itemstime`; to players only in warmup or `sv_itemstime == 2`.
- `Customize` runs every frame; also fires the `CustomizeWaypoint` mutator hook.

### Networking  (`WaypointSprite_SendEntity` / `Ent_WaypointSprite`)
- Header byte sendflags (low 7 bits) + `wp_extra` byte. Bit 0x80 = health/build present. Sub-fields gated by
  bits: 64 origin (exact vector), 1 team+rule, 2/4/8 the three model strings, 16
  (fade_time/teleport_time/maxdist/hideflags), 32 (radar icon + colormod RGB + helpme countdown).
- Health is sent as `health/max_health * 191` (a byte), or build timer as a 14-bit delta offset +192.

### Client draw  (`Draw_WaypointSprite`, all numbers from cvars loaded once per frame in `WaypointSprite_Load`)
- Lifetime alpha = `bound(0,(fadetime-time)/lifetime,1) ** timealphaexponent`.
- Skip if `hideflags & 2` (radar only), or `cl_hidewaypoints >= 2`, or (`hideflags & 1` && `cl_hidewaypoints`).
- Pick the sprite image per the rule + local team. Distance alpha uses `maxdistance` + `normdistance`.
- `project_3d_to_2d`; if off the inset rect (edgeoffset_*) or behind, compute an edge-clamp position + arrow angle.
- Apply distance-fade (`distancefade{alpha,scale,distance}` = mapsize × multiplier), edge-fade
  (`edgefade{alpha,scale,distance}`), crosshair-fade (`crosshairfade{alpha,scale,distance}`). Blink: in the off
  half-cycle multiply alpha by `SPRITE_HELPME_BLINK (2)` if helpme, else the def's blink value.
- Draw arrow (`drawspritearrow`, scale `SPRITE_ARROW_SCALE = 1`), then icon (if `!g_waypointsprite_text` and the
  icon exists) sized `g_waypointsprite_iconsize (32)`, else uppercase (`g_waypointsprite_uppercase`) text at
  `g_waypointsprite_fontsize (12)`, then the health bar (`SPRITE_HEALTHBAR_*` = width 104 / height 7 / margin 6 /
  border 2). `spam` debug shows "Spam" if `>= g_waypointsprite_spam` live.

### Player-deploy impulses  (`server/impulse.qc:414-499`, the live gameplay entry points)
- `waypoint_personal_here / _crosshair / _death` → `DeployPersonal(WP_Waypoint, …, RADARICON_WAYPOINT)` + Ping.
- `waypoint_here_*` → `DeployFixed(WP_Here, …, RADARICON_HERE)`. `waypoint_danger_*` → `WP_Danger`.
- `waypoint_here_follow` (teamplay only, alive) → `HelpMePing` mutator hook else `Attach(WP_Helpme, true)` + Ping.
- `waypoint_clear_personal` / `waypoint_clear` → ClearPersonal / ClearOwned. (cl aliases in `xonotic-client.cfg`.)

### Constants / cvars (Base defaults)
| cvar | default | side |
|---|---|---|
| `sv_waypointsprite_deployed_lifetime` | 10 | sv |
| `sv_waypointsprite_deadlifetime` | 1 | sv |
| `sv_waypointsprite_limitedrange` | 5120 | sv |
| `cl_hidewaypoints` | 0 | cl |
| `g_waypointsprite_alpha` | 1 | cl |
| `g_waypointsprite_scale` | 1 | cl |
| `g_waypointsprite_fontsize` | 12 | cl |
| `g_waypointsprite_iconsize` | 32 | cl |
| `g_waypointsprite_text` | 0 | cl |
| `g_waypointsprite_uppercase` | 1 | cl |
| `g_waypointsprite_iconcolor` | 0 | cl |
| `g_waypointsprite_minscale` / `_minalpha` | 0.5 / 0.4 | cl |
| `g_waypointsprite_normdistance` | 512 | cl |
| `g_waypointsprite_distancealphaexponent` / `_timealphaexponent` | 2 / 1 | cl |
| `g_waypointsprite_distancefade{alpha,scale,distancemultiplier}` | 1 / 0.7 / 0.5 | cl |
| `g_waypointsprite_edgefade{alpha,distance,scale}` | 0.5 / 50 / 1 | cl |
| `g_waypointsprite_edgeoffset_{bottom,left,right,top}` | 0.06 | cl |
| `g_waypointsprite_crosshairfade{alpha,distance,scale}` | 0.25 / 150 / 1 | cl |
| `g_waypointsprite_itemstime` | 2 | cl |
| `g_waypointsprite_spam` | 0 | cl |
| `g_waypointsprite_turrets` / `_turrets_maxdist` / `_turrets_text` / `_turrets_onlyhurt` | 1 / 5000 / 0 / 0 | cl |
| `SPRITE_HEALTHBAR_WIDTH/HEIGHT/MARGIN/BORDER` | 104 / 7 / 6 / 2 | (const) |
| `SPRITE_ARROW_SCALE` / `SPRITE_HELPME_BLINK` | 1 / 2 | (const) |

## Port mapping
- **Registry** → `WaypointRegistry` (Waypoints.cs). 38 of 63 `all.inc` defs ported (the player pings, all CTF flag
  states, Dom, KH keys+carriers, Assault, Frozen/Reviving, Monster, Vehicle). **Missing (25)**: Race
  (Checkpoint/Finish/Start/StartFinish), Keepaway/Tka (KaBall/KaBallCarrier/TkaBallCarrier{Red,Blue,Yellow,Pink}),
  NbBall/NbGoal, LmsLeader, Onslaught (OnsCP/OnsCPDefend/OnsCPAttack/OnsGen/OnsGenShielded), Buff, Item, Weapon,
  Seeker, KeyCarrierFriend, KeyCarrierFinish, VehicleIntruder. (KeyDropped + KeyCarrier{Red,Blue,Yellow,Pink} ARE
  present — note KeyCarrierNeutral does not exist in either.) Blink values flattened to 1 (no superweapon/item
  blink; OnsCPDefend=0.5 / OnsCPAttack=2 / Seeker=2 defs absent).
- **Server manager** → `WaypointSprites` static (Spawn/SpawnFixed/AttachCarrier/Update*/Ping/HelpMePing/Kill/Disown/
  DetachCarrier/Think/Reset/FadeAlpha). `GameWorld` calls `Reset()` (map load, line 496) and `Think()` (per tick,
  line 1223). **But nothing ever spawns into `_active`** — see liveness below.
- **Per-tick objective emit** → `GameType.CollectWaypoints(into)` (GameplayBases.cs virtual). Only **Ctf**
  overrides it (rebuilds one WP per flag each tick from flag status). `ServerNet.SendWaypoints` (per tick) merges
  `WaypointSprites.Active` + the gametype's `CollectWaypoints`, filters per peer (`WaypointVisible`), serializes.
- **Net** → `NetControl.Waypoints` (protocol v9). `ClientNet.HandleWaypoints` decodes to `WaypointNet` list.
- **Client draw** → `WaypointSpriteLayer` (game/client). Projects via `Camera3D.UnprojectPosition`, edge-clamp +
  arrow, icon-or-text, lifetime/maxdistance/helpme fades, health bar. Wired in `NetGame` (Source = ClientNet.Waypoints).
- **Radar** → `RadarPanel` draws a `teamradar_icon_<n>` per waypoint with RadarIcon > 0.
- **Menu** → `DialogSettingsGame` binds `cl_hidewaypoints` + `g_waypointsprite_{alpha,fontsize,edgeoffset_bottom,
  crosshairfadealpha,text}` (faithful to QC dialog), but those cvars are **inert** (the layer hardcodes constants).

## Parity assessment

### Liveness (the dominant story)
- **The entire `WaypointSprites` server manager is DEAD.** Nothing calls `Spawn / SpawnFixed / DeployFixed /
  DeployPersonal / Attach / AttachCarrier / UpdateSprites / UpdateHealth / UpdateRule / UpdateTeamRadar /
  HelpMePing / Ping / Disown / Kill / DetachCarrier`. `_active` is always empty; only `Reset()` and `Think()` are
  invoked (both no-op on an empty list). The full deploy/attach/helpme API exists but is unreachable.
- **Only CTF produces any waypoint** (via `Ctf.CollectWaypoints`). That path IS live (SendWaypoints → ClientNet →
  WaypointSpriteLayer + RadarPanel), so CTF flag markers + radar icons work. Every other gametype that spawns
  waypoints in Base has **none** in the port: Domination/Onslaught control points & generators, Key Hunt keys &
  carriers, Assault defend/destroy objectives, Keepaway/TeamKeepaway/Nexball balls & goals, FreezeTag
  frozen/reviving markers, Race start/finish/checkpoint, LMS leader, item-respawn (itemstime) markers, and the
  monster/turret/weapon/vehicle/buff markers — all missing (their `CollectWaypoints` is the base no-op).
- **Player-deployed waypoints are DEAD.** `waypoint_personal_*`, `waypoint_here_*`, `waypoint_danger_*`,
  `waypoint_here_follow` (HELP ME), `waypoint_clear*` have **no server dispatch** — the strings appear only as
  QuickMenuPanel macro text (`say_team …; waypoint_here_crosshair`), and the server never routes them to a
  handler. So the team-communication ping system (a core teamplay feature) does nothing.

### Logic / values gaps
- **Visibility rule mismatch:** port `SpriteRule.Default` is "visible to everyone" unconditionally
  (`ServerNet.WaypointVisible` `_ => true`). QC `SPRITERULE_DEFAULT` with a team set restricts to the same team
  **and** requires the viewer to be a live player (not an observer). For CTF, flag waypoints are correctly
  everyone-visible (they pass team but rely on the radar color), but a port that later spawns team-restricted
  Default waypoints would leak them to enemies/observers. `SpriteRule.Teamplay` and `Spectator` are partially
  modeled (Teamplay: team match; Spectator: observer-only — but the QC warmup/`sv_itemstime==2` allowance for
  live players is dropped).
- **Three-image rule (netname/2/3) collapsed:** the port networks a single `SpriteName`; the QC
  `SPRITERULE_TEAMPLAY` per-audience image swap (own/enemy/spectator) is not modeled.
- **Health-bar networking simplified** (intended): port pre-normalizes Health 0..1 and drops the
  `max_health/40`-step quantization and the 191-scale byte packing; build-progress reproduced. Carrier health
  bars (CTF FC `AttachCarrier` health) are absent because AttachCarrier is dead.
- **`sv_waypointsprite_*` server cvars unused:** deployed_lifetime / deadlifetime / limitedrange never read (the
  consumers — Deploy/Attach/Disown — are dead). `WaypointSprite_Init` has no port analogue.

### Presentation gaps
- **Client cvars inert:** `WaypointSpriteLayer` hardcodes `EdgeInset = 0.06`, `IconSize = 24` (QC 32), `FontSize =
  13` (QC 12). `g_waypointsprite_{alpha,scale,minscale,minalpha,normdistance,distancefade*,edgefade*,
  crosshairfade*,timealphaexponent,distancealphaexponent,text,uppercase,iconcolor,iconsize,spam,turrets*}` and
  `cl_hidewaypoints` are all read by nothing — the menu can set them but they have no effect.
- **Fades not implemented:** distance-fade, edge-fade, and crosshair-fade (alpha+scale ramps) are absent; only a
  raw max-distance cull + a single `(maxdistance-dist)…**2` ramp is done. No per-sprite scale at all (no
  `waypointsprite_scale` / minscale). No `g_waypointsprite_text` toggle (always icon-if-present). No uppercase. No
  `g_waypointsprite_iconcolor` saturation. `cl_hidewaypoints` toggle and the `hideflags & 1/2` (fixed / radar-only)
  gating are not honored client-side (Hideable is networked but never consulted in the layer).
- **Blink:** only the helpme bright/dim flash is modeled (and at 0.45 dim, vs QC's scheme of multiplying by 2 in
  the bright half); the per-def blink (superweapon/item/FlagReturn = 2) is gone.
- **Radar ping ring** (`Ping` → `cnt|BIT(7)` pulse) is a no-op; no radar circle pulse on ping/update.

### Intended divergences
- Health pre-normalization + dropped 191-byte/40-step quantization: a reasonable port simplification (the bar
  reads the same), flagged `values: partial` not a bug — but kept as a gap note since it changes the wire format.
- IconSize 24 / FontSize 13 vs QC 32 / 12: a deliberate "gently smaller" tweak per the code comment.

## Verification
- Code-read of all Base + port files above (static). **Unverified at runtime.**
- Liveness established by exhaustive caller search: `grep WaypointSprites\.(Spawn|Deploy|Attach|Update|Ping|
  HelpMePing|Disown|Kill|DetachCarrier)` across `src/` + `game/` returns **only** the definitions in Waypoints.cs
  (no callers); `override void CollectWaypoints` exists only in Ctf.cs; `waypoint_personal/here/danger` strings
  appear only in QuickMenuPanel macro text with no server handler.
- CTF flag-marker path confirmed live by the call chain Ctf.CollectWaypoints → ServerNet.SendWaypoints (per tick,
  line 471) → ClientNet.HandleWaypoints → WaypointSpriteLayer.Source + RadarPanel.

## Open questions
- Does the port intend the deploy/helpme ping system to be wired later (the API is fully built but dead), or was
  CTF-only the scope? Needs owner input.
- Are the inert `g_waypointsprite_*` menu bindings intended as forward-looking scaffolding, or should they be
  removed until the layer reads them (currently they mislead the player into thinking the knobs work)?
- Runtime check: do CTF flag markers actually render in-world + on radar in a live match (the path looks live but
  was not observed)?
