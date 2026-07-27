# Teleporters & Portals — parity spec

**Base refs:** `common/mapobjects/teleporters.qc` · `common/mapobjects/trigger/teleport.qc` · `common/mapobjects/misc/teleport_dest.qc` · `server/portals.qc` · `common/weapons/weapon/porto.qc`
**Port refs:** `src/XonoticGodot.Common/Gameplay/MapObjects/Teleporters.cs` · `src/XonoticGodot.Common/Gameplay/Weapons/Porto.cs` · `src/XonoticGodot.Common/Gameplay/MapObjects/Warpzone.cs` (PlacePortoPortal) · `src/XonoticGodot.Engine/Simulation/TriggerTouch.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Two related systems:

1. **Map teleporters** — `trigger_teleport` (a touch volume), `target_teleporter` (use-activated), and the
   destination entities `info_teleport_destination` / `misc_teleporter_dest`. On contact a player is relocated
   to the destination, reoriented to the destination facing, has its speed reprojected/clamped, the teleport
   sound + a flash effect play, and anyone already standing at the exit is **telefragged** (gibbed for 10000
   damage). This is the classic Quake teleporter.

2. **Portals (the "portal banana")** — the **Port-O-Launch** weapon (`porto`) fires a bouncing projectile that
   creates a pair of linked **two-way** portals (a red in-portal and a blue out-portal — the colour is cosmetic;
   `Portal_Connect` sets `.enemy` on BOTH and both run `Portal_Think`, so a player walks through either side) on
   flat surfaces. The portal
   entities (`server/portals.qc`) are damageable model edicts with a health pool and a lifetime that fades
   them out; a player who walks/falls through is teleported via `Portal_TeleportPlayer` (a full angle/velocity
   transform), reusing the same `TeleportPlayer` core as map teleporters (telefrag included). A portal telefrag
   within 1 s of portal creation awards the "Amazing" announcer.

Both paths converge on `TeleportPlayer` (teleporters.qc:67), which is the authoritative relocate+telefrag core.

## Base algorithm (authoritative)

### TeleportPlayer — relocate + sound/effect + telefrag core  (`teleporters.qc:TeleportPlayer`)
- **Trigger / entry:** called by `Simple_TeleportPlayer` (map teleporters), `Portal_TeleportPlayer` (portals),
  and `WarpZone` (warpzones). `tflags` selects which side-effects run.
- **Algorithm (SVQC):**
  1. `telefragger = teleporter.owner ? teleporter.owner : player`.
  2. `makevectors(to_angles)`.
  3. If `player.teleportable == TELEPORT_NORMAL` (a real player) **and** `teleporter.pushltime < time`
     (one effect per teleporter per **0.2 s** debounce):
     - if `TELEPORT_FLAG_SOUND`: play `SND(TELEPORT)` (`misc/teleport`) on `CH_TRIGGER` at `VOL_BASE`/`ATTEN_NORM`
       (or a random word from `teleporter.noise` if set).
     - if `TELEPORT_FLAG_PARTICLES`: `Send_Effect(EFFECT_TELEPORT, player.origin, '0 0 0', 1)` **and**
       `Send_Effect(EFFECT_TELEPORT, to + v_forward*32, '0 0 0', 1)` — a flash at BOTH ends (entry origin and
       **32 units in front of** the exit).
     - `teleporter.pushltime = time + 0.2`.
  4. Relocate: `from=player.origin; setorigin(player,to); player.oldorigin=to; player.angles=to_angles;`
     bot aim reset; `player.fixangle=true; player.velocity=to_velocity;`
     `BITXOR_ASSIGN(player.effects, EF_TELEPORT_BIT)` (toggles the model-dissolve/flash visual on the player).
     `makevectors(player.angles); Reset_ArcBeam; UpdateCSQCProjectileAfterTeleport; UpdateItemAfterTeleport`.
  5. If `IS_PLAYER`: telefrag gate —
     `if((tflags & TELEPORT_FLAG_TDEATH) && player.takedamage && !IS_DEAD(player) && !g_race && !g_cts &&
      (autocvar_g_telefrags || (tflags & TELEPORT_FLAG_FORCE_TDEATH)) &&
      !(round_handler_IsActive() && !round_handler_IsRoundStarted()))` → `tdeath(...)`.
  6. `UNSET_ONGROUND(player); player.oldvelocity = player.velocity;`.
  7. Kill-credit window: if `teleporter.owner`: `player.pusher=owner; player.pushltime=time+g_maxpushtime;
     player.istypefrag=chat-held`. Else `pushltime=0; istypefrag=0`.
  8. `player.lastteleporttime=time; player.lastteleport_origin=from`.
- **Constants:** effect debounce `0.2 s`; `g_maxpushtime = 8.0`; sound `misc/teleport`, `CH_TRIGGER`,
  `VOL_BASE`, `ATTEN_NORM`; exit-effect offset `+v_forward*32`.
- **tflags:** `TELEPORT_FLAGS_TELEPORTER = SOUND|PARTICLES|TDEATH`; `TELEPORT_FLAGS_PORTAL =
  SOUND|PARTICLES|TDEATH|FORCE_TDEATH`; `TELEPORT_FLAGS_WARPZONE = 0` (silent, no telefrag).

### tdeath / check_tdeath — the telefrag  (`teleporters.qc:tdeath`)
- **Algorithm:** `TDEATHLOOP(player.origin)` builds a box = `player.origin + player.mins/maxs`, unioned with
  `telefragmin/telefragmax` (portals pass the destination portal's `absmin/absmax`; teleporters pass `'0 0 0'`).
  Upstream `b6e02fe3` (2026-07-06) replaced the old `findradius` scan — whose
  `deathradius = max(vlen(deathmin), vlen(deathmax))` was the box's distance from the WORLD ORIGIN (a perf-only
  bug; the per-entity `boxesoverlap` kept it correct) — with `findbox(deathmin, deathmax)`, dropping the QC-side
  box filter (the engine query is exact). The port matches: `Teleporters.CheckTdeath`/`Telefrag` use
  `IEntityService.FindInBox` (area-grid backed, precise AABB inside). For each `head != player`
  with `head.takedamage`:
  - If the teleportee is a live player (`GetResource(player,RES_HEALTH) >= 1`):
    skip if `teamplay && g_telefrags_teamplay && head.team == player.team`; else if `head` is a live player,
    `++tdeath_hit`, and `Damage(head, teleporter, telefragger, 10000, DEATH_TELEFRAG, ...)`.
  - Else (dead body / monster teleportee): `Damage(telefragger, teleporter, telefragger, 10000, DEATH_TELEFRAG)`
    — it gibs ITSELF instead of telefragging others.
- **Constants:** telefrag damage `10000`; deathtype `DEATH_TELEFRAG`; `g_telefrags = 1`,
  `g_telefrags_teamplay = 1` (**spare teammates**), `g_telefrags_avoid = 1`.
- **Note on the teamplay gate:** the condition is `!(teamplay && g_telefrags_teamplay && same-team)`. With the
  Base default `g_telefrags_teamplay = 1`, teammates standing at the exit are NOT telefragged in team modes.

### Simple_TeleportPlayer — map-teleporter destination pick + speed  (`teleporters.qc:Simple_TeleportPlayer`)
- **Algorithm (SVQC):**
  1. Destination = `teleporter.enemy` if cached (exactly one dest), else weighted-random over all
     `FOREACH_ENTITY_STRING(targetname, teleporter.target)` via `RandomSelection`: weight = `it.cnt ? it.cnt : 1`;
     if `STAT(TELEPORT_TELEFRAG_AVOID, player)` and placing the player there would telefrag (`check_tdeath`),
     priority 0 (avoid), else 1. ("sorry CSQC, random stuff ain't gonna happen" — CSQC only follows `enemy`.)
  2. `makevectors(e.mangle)`.
  3. `.speed` hard cap: if `e.speed && vlen(player.velocity) > e.speed`: rescale velocity to `e.speed`.
  4. Unless the teleporter has `KEEP_SPEED` (`trigger_teleport` BIT(1) / `target_teleporter` BIT(0)):
     clamp to `STAT(TELEPORT_MINSPEED)` (raise) and `STAT(TELEPORT_MAXSPEED)` (lower). Both default **0**
     (`g_teleport_minspeed`/`g_teleport_maxspeed` = 0, i.e. disabled in stock physics).
  5. `locout = e.origin + '0 0 1' * (1 - player.mins.z - 24)` (≈ `e.origin + '0 0 1'` since `mins.z = -24`).
  6. `TeleportPlayer(teleporter, player, locout, e.mangle, v_forward * vlen(player.velocity), '0 0 0','0 0 0',
     TELEPORT_FLAGS_TELEPORTER)` — **out-velocity is always reprojected along the destination facing**.

### Teleport_Active / Teleport_Touch — the trigger  (`trigger/teleport.qc`)
- **Active gate:** `this.active == ACTIVE_ACTIVE`; player must be `teleportable`; not on a non-teleportable
  vehicle; not a turret; not dead; team gate (`this.team` with `INVERT_TEAMS` spawnflag BIT(2)); and not
  `TELEPORT_OBSERVERS_ONLY` (BIT(0)) for `trigger_teleport`.
- **Touch:** `EXACTTRIGGER_TOUCH`; `RemoveGrapplingHooks(player)`; `e = Simple_TeleportPlayer(this,player)`;
  then `SUB_UseTargets(this,...)` with `this.target` temporarily blanked (so the dest isn't re-fired), then
  `SUB_UseTargets(e,...)`.
- **trigger_teleport_use:** in teamplay, `this.team = actor.team` (claims the teleporter for a team).
- **spawnfunc(trigger_teleport):** requires a `.target`; `teleport_findtarget` spawns bot waypoints, caches
  `enemy` if exactly one dest, sets touch, pushes to `g_teleporters` IntrusiveList.
- **target_teleporter:** disambiguates at spawn (is it a destination, a self-target teleporter, or a normal
  teleporter?) then routes through `target_teleport_use` (no touch volume; use-activated).

### info_teleport_destination  (`misc/teleport_dest.qc`)
- `mangle = angles; angles = '0 0 0'`. Requires a `.targetname` (objerror otherwise). Net-linked to CSQC
  (`teleport_dest_link`) so the client can follow single-dest teleports. `.cnt` is the random weight, `.speed`
  the per-dest cap.

### Portal weapon banana  (`server/portals.qc`, `weapon/porto.qc`)
- **Spawn:** `Portal_Spawn` — `CheckWireframeBox` clears a 96³ region; spawns a `portal` edict with
  `MDL_PORTAL`, `takedamage = DAMAGE_AIM`, `event_damage = Portal_Damage`,
  `fade_time = (g_balance_portal_lifetime >= 0 ? time + 15 : 0)`, `health = g_balance_portal_health (200)`,
  finds a safe origin (`move_out_of_solid`, 16 u in front), `setsize '-48..48'`, `Portal_MakeWaitingPortal`.
- **States/skins/effects:** in-portal `skin 0, EF_RED`; out-portal `skin 1, EF_STARDUST|EF_BLUE`;
  waiting `skin 2, EF_ADDITIVE`; broken `skin 2, no effects`. `Portal_Customize` hides the portal model from
  players who aren't allowed through it (independent-player gating).
- **Connect:** `Portal_Connect` computes `portal_transform = AnglesTransform_RightDivide(...)`, links
  `enemy` both ways, resets `fade_time`, sets `teleport_time = time`, `solid = SOLID_TRIGGER`.
- **Teleport:** `Portal_TeleportPlayer` — full position transform (`AnglesTransform_Apply`), plane-shift onto the
  exit plane (bounded ±48 in s/t), `tracebox` from `portal_safe_origin` to validate the exit is non-solid,
  angle transform via `Portal_ApplyTransformToPlayerAngle`, `right_vector` negate for chaining, then
  `TeleportPlayer(..., enemy.absmin, enemy.absmax, TELEPORT_FLAGS_PORTAL)`. On a telefrag within
  `time < teleport_time + 1` → `ANNCE_ACHIEVEMENT_AMAZING`. On success: reset fade, recharge both portals'
  health to 200 and `fade_time = time + 15`.
- **Touch/Think:** `Portal_Think` runs every frame (`PHYS_GRAVITY`-aware `Portal_WillHitPlane` predict-ahead),
  teleporting players/hooks that would cross the plane this frame; expires the portal at `fade_time`.
- **Damage:** `Portal_Damage` — telefrag deathtype ignored; subtracts damage from health; `Portal_Remove(this,1)`
  at health < 0 → plays `SND_PORTO_EXPLODE`, `EFFECT_ROCKET_EXPLODE`, deletes; natural expiry plays
  `SND_PORTO_EXPIRE` and `SUB_SetFade(portal, time, 0.5)`.
- **Constants:** `g_balance_portal_health = 200`, `g_balance_portal_lifetime = 15`, activate delay `0.1 s`,
  plane-shift bound `±48`, safe nudges `SAFENUDGE='1 1 1'` / `SAFERNUDGE='8 8 8'`, amazing window `1 s`.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `TeleportPlayer` core | `Teleporters.TeleportPlayer` | relocate + sound + telefrag + kill-credit |
| `tdeath`/`check_tdeath` | `Teleporters.Telefrag` / `Teleporters.CheckTdeath` | box telefrag |
| `Simple_TeleportPlayer` | `Teleporters.SimpleTeleportPlayer` / `PickDestination` | dest pick + speed reproject |
| `Teleport_Active`/`Teleport_Touch` | `Teleporters.TeleportActive`/`TeleportTouch` | trigger gate + relocate |
| `trigger_teleport_use` | `Teleporters.TeleportUse` | team claim |
| `target_teleporter`/`target_teleport_use` | `Teleporters.TargetTeleporterSetup`/`TargetTeleportUse` | use-activated |
| `info_teleport_destination`/`misc_teleporter_dest` | `Teleporters.TeleportDestSetup` | dest entity |
| spawnfunc registration | `MapObjectsRegistry` (lines 112-115) | all 4 classnames registered |
| live server touch dispatch | `TriggerTouch.Run` → `entity.Touch` | wired |
| client-prediction teleport | `TriggerTouch.PredictTeleportsAmbient` | single-dest only |
| EFFECT_TELEPORT flash | `EffectEmitter.Emit("TELEPORT", ...)` | both ends |
| teleport sound | `MapMover.Sound(..., "misc/teleport.wav")` | wired |
| Porto weapon projectile | `Porto` (`[Weapon]`, auto-registered) | bounce/lifetime/latch live |
| Portal edict (`Portal_Spawn`, health, lifetime, model, skins, EF_*, damage, fade, explode/expire sounds, Amazing announcer, Portal_Customize, plane transform) | **NOT IMPLEMENTED** — `Porto.PortalSpawner` → `WarpzoneManager.PlacePortoPortal` realises portals as a **seamless warpzone pair** instead | Major architectural divergence |
| `g_teleport_minspeed/maxspeed` STAT clamps | **NOT IMPLEMENTED** | out of scope; default 0 in stock |
| `EF_TELEPORT_BIT` player model flash | **NOT IMPLEMENTED** | no source reference |
| `RemoveGrapplingHooks` on teleport | **NOT IMPLEMENTED** | hook isn't dropped on teleport |

## Parity assessment

### Teleporters — largely faithful logic, several authority gaps
- **Logic:** destination lookup (single-cache + weighted-random with telefrag-avoid), relocation, speed
  reproject along facing, `.speed` cap, the exact-box telefrag with self-gib for dead/monster teleportees,
  team-claim on use, OBSERVERS_ONLY gate, and `UseTargets` firing are all ported. Live on both the server touch
  path (`TriggerTouch.Run`) and the client prediction path (single-dest only — faithful to CSQC).
- **Gaps (telefrag):**
  - `g_telefrags_teamplay` is **not registered** in `Cvars.cs`, so the port's `Cvar("g_telefrags_teamplay",0)`
    falls back to **0**. **(CORRECTED)** This does NOT telefrag teammates by default — with the cvar absent,
    `teamTelefrags=false`, so the port's spare-branch is active and **teammates ARE spared** (matching Base's
    default-1 "never telefrag teammates" behaviour). The real defect is two-fold: (a) the cvar is **inert** (a
    server override has no effect), and (b) the port's sense is **inverted** relative to the Base cvar name —
    the port treats a truthy value as "telefrag teammates" whereas Base names it "never telefrag teammates" and
    uses the same value to spare them, so any explicit `set g_telefrags_teamplay 1` would flip the port the
    WRONG way and start telefragging teammates. Net default behaviour is faithful; the wiring/sense is wrong.
  - The Base telefrag gate also requires `!g_race && !g_cts` and not-in-pre-round
    (`round_handler_IsActive() && !round_handler_IsRoundStarted()`). The port telefrag is gated **only** on
    `g_telefrags`. Result: telefrags occur in Race/CTS and during the pre-round freeze in the port; Base
    suppresses them. **Observable: spawn-overlap kills in Race/CTS and during round countdown.**
  - `TELEPORT_FLAG_FORCE_TDEATH` (the portal path forces telefrag even with `g_telefrags 0`) is not modeled.
  - `g_telefrags` / `g_telefrags_teamplay` / `g_telefrags_avoid` are not registered, so a server cvar override
    has no effect (the port uses hardcoded fallbacks; telefrag-avoid is hardcoded always-on for players).
- **Gaps (presentation/timing):**
  - The second teleport flash is emitted at `player.Origin` (= the exit origin `to`), but Base emits it at
    `to + v_forward*32` (32 u in front of the exit, facing the destination). **Observable: exit flash sits on
    the player rather than ahead of them.**
  - `EF_TELEPORT_BIT` — the player-model dissolve/flash on teleport — is not toggled. **Observable: no
    teleport "shimmer" on the model.**
  - `RemoveGrapplingHooks` is not called on teleport (a hooked player keeps the hook through a teleporter).
- **Out of scope (acknowledged):** warpzones, bot waypoint spawning, `g_teleport_min/maxspeed` STAT clamps
  (default 0), and the CSQC networking of the trigger volume.

### Portals (porto banana) — projectile faithful, portal ENTITY replaced by warpzone
- **Logic (projectile):** the Porto weapon's launch, one-portal-in-flight latch (`porto_current`),
  bounce/lifetime self-destruct, Strength speed boost, and the in/out/combined placement decision tree are
  ported and live. The single biggest fidelity caveat is in `Porto.OnTouch`: the headless touch cannot read the
  per-contact surface flags or impact plane, so slick/noimpact reflection and the true wall-normal portal
  orientation are approximated (the portal faces back along the projectile's velocity, not the real surface
  normal).
- **Major divergence — portal realisation:** Base spawns a `portal` model edict (`Portal_Spawn`) with a health
  pool (200), a 15 s fade lifetime, skins/effects (EF_RED/EF_BLUE/EF_STARDUST), `Portal_Customize` visibility
  gating, the full position/angle transform, `Portal_Damage`/`Portal_Remove`, and explode/expire sounds. The
  port instead routes each landed portal to `WarpzoneManager.PlacePortoPortal`, creating a **seamless two-way
  warpzone pair**. Consequently the following Base behaviors are **missing**:
  - **Portal health / damage** — Base portals can be shot down (`g_balance_portal_health=200`). Port warpzones
    are indestructible.
  - **Portal lifetime / fade** — Base portals expire after `g_balance_portal_lifetime=15 s` and fade out (and
    recharge to full life+health on each use). Port warpzone portals are permanent for the match.
  - **Portal model + skins + effects** — the spinning portal model, EF_RED/BLUE/STARDUST glow, additive waiting
    state. A warpzone is a (visually) transparent see-through window, not a Quake-style portal disc.
  - **`SND_PORTO_EXPLODE` / `SND_PORTO_EXPIRE`** + `EFFECT_ROCKET_EXPLODE` on destruction/expiry — never play.
  - **`ANNCE_ACHIEVEMENT_AMAZING`** on a portal telefrag within 1 s of creation — never triggered (the
    notification exists but has no caller on this path).
  - **`Portal_Customize`** — hiding the portal from players who can't use it (independent-player modes).
  - **(CORRECTED — NOT a gap)** Directionality: Base Porto portals are **two-way**, not one-way
    (`Portal_Connect` sets `.enemy` on both portals; both run `Portal_Think`). The port's two-way `LinkPair`
    is therefore **faithful** on directionality. (The earlier draft's "Base is one-way" claim was wrong.)
    The remaining portal-teleport gaps are: the **Amazing** announcer, the `portal_activatetime` 0.1 s
    owner self-skip, **independent-player** gating, the **portal-owner kill-credit**, `TELEPORT_FLAGS_PORTAL`
    (FORCE_TDEATH), and the unverified angle/velocity/plane-shift transform.
- **Liveness:** `Porto.PortalSpawner` IS wired (`GameWorld.cs:388`), so the warpzone-realised portal is live
  on a listen server. But because Porto is a superweapon rarely in default loadouts, real-match exercise is
  limited; the warpzone realisation itself is verified-by-wiring, not playtested here → `unknown`/low for the
  warpzone teleport behavioral fidelity.

## Verification
- **Code-read (high):** teleporter relocate/telefrag/dest-pick logic, spawnfunc registration, the live server
  touch dispatch (`TriggerTouch.Run` invokes `entity.Touch`), and the client prediction path were all read in
  full and traced to live callers.
- **Value diff (high):** `g_telefrags_teamplay` Base default 1 vs port fallback 0 confirmed by grep (only
  `g_maxpushtime` is registered in `Cvars.cs`); telefrag damage 10000 and deathtype match; `g_maxpushtime=8`
  matches.
- **Value diff (high):** Base second-effect offset `to + v_forward*32` vs port `player.Origin` confirmed by
  source comparison (`teleporters.qc:101` vs `Teleporters.cs:189`).
- **Missing-by-grep (high):** no `g_balance_portal_health`/`_lifetime`, `Portal_Damage`/`Portal_Remove`,
  `EF_TELEPORT_BIT`, or Amazing-on-portal in port source.
- **Unverified (low):** warpzone-realised portal teleport correctness (angle/velocity transform, seam) — needs
  a runtime check with the Porto weapon on a listen server.

## Open questions
- ~~Is the two-way Porto portal an intended divergence?~~ **RESOLVED: Base Porto portals are themselves two-way
  (`Portal_Connect` links `.enemy` both ways), so the port's two-way linking is faithful — not a divergence.**
- Should map-teleporter telefrag respect Race/CTS + pre-round suppression even though the port currently has no
  `round_handler` hook in this code path? (The Base gate is explicit.)
- Does any default Xonotic loadout / map actually give the Porto weapon, or is the portal banana effectively
  dormant in normal play (affecting how much the portal-edict gaps matter)?
