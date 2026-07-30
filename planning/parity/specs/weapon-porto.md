# Port-O-Launch (porto) — parity spec

**Base refs:** `common/weapons/weapon/porto.qc` · `common/weapons/weapon/porto.qh` · `server/portals.qc`/`.qh` · `bal-wep-xonotic.cfg` · `balance-xonotic.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Porto.cs` · `src/VortexArena.Common/Gameplay/MapObjects/Warpzone.cs` · `src/VortexArena.Server/GameWorld.cs` · `game/client/ProjectileCatalog.cs` · `game/client/WeaponFireSounds.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Port-O-Launch is a utility **superweapon** that fires a bouncing, no-damage, no-ammo projectile which,
on landing against a flat surface, creates a one-way teleport **portal**. A complete shot establishes an
**in-portal** (red) and an **out-portal** (blue) linked by a unique `portal_id`; players and projectiles that
enter the in-portal are teleported (with full angle/velocity transform) out the out-portal. The weapon is
rarely placed on stock maps. It activates whenever a player holds WEP_PORTO; the portal-teleport machinery
(server/portals.qc) is a distinct subsystem layered on top of the warpzone transform math.

Three placement modes exist, selected by `g_balance_porto_secondary` (default **1**):
- **secondary == 1** (default): primary fires `type 0` (in-portal only), secondary fires `type 1` (out-portal only).
- **secondary == 0**: primary fires `type -1`, a *combined* shot that lays the in-portal where it first lands,
  flips red→blue, and keeps flying to lay the out-portal at its next valid landing. Secondary, in this mode,
  *holds* the player's view angle (`porto_v_angle`) so primary can fire along a fixed aim while looking around.

## Base algorithm (authoritative)

### Fire / launch  (`porto.qc:W_Porto_Attack`, `porto.qc:wr_think`)
- **Trigger:** SVQC `wr_think`, gated by `!actor.porto_current && !actor.porto_forbidden && weapon_prepareattack(refire)`.
  Only **one** porto projectile can be live at a time (`porto_current` latch).
- **Algorithm:** `W_SetupShot(actor, ..., recoil 4, SND_PORTO_FIRE, CH_WEAPON_A, maxdamage 0)`, then **override**
  `w_shotdir = v_forward` and project `w_shotorg` onto the eye-forward ray (always shoot straight from the eye —
  WEP_FLAG_NOTRUEAIM). Spawn a `porto` entity: `cnt = type`, `effects = EF_RED`, `scale = 4`,
  `MOVETYPE_BOUNCEMISSILE`, `PROJECTILE_MAKETRIGGER`, size `'0 0 0'`. Velocity via
  `W_SetupProjVelocity_Basic(gren, speed, 0)` (no spread, no up/z). `gren.portal_id = time` (unique id);
  `gren.right_vector = v_right` (from `fixedmakevectors(fixedvectoangles(velocity))`) — the portal's roll axis.
  `nextthink = time + lifetime` → `W_Porto_Think`; touch → `W_Porto_Touch`. CSQCProjectile type PORTO_RED
  (type<=0) or PORTO_BLUE (type>0). `MUTATOR_CALLHOOK(EditProjectile)`.
- **Strength powerup:** if `STATUSEFFECT_Strength` active, launch speed is `speed * autocvar_g_balance_powerup_strength_force`.
- **Constants (bal-wep-xonotic.cfg):** `primary/secondary_speed = 1000`, `*_lifetime = 5`, `*_refire = 1.5`,
  `*_animtime = 0.3`, `secondary = 1`, `switchdelay_drop = 0.2`, `switchdelay_raise = 0.2`, `weaponthrowable = 1`,
  `weaponstart = 0`, `weaponstartoverride = -1`. **Strength force (balance-xonotic.cfg): `g_balance_powerup_strength_force = 3`.**

### Aim-hold (secondary, non-secondary mode)  (`porto.qc:wr_think` else-branch)
- While `fire & 2` is held the first time, capture `actor.(weaponentity).porto_v_angle = actor.v_angle` and set
  `porto_v_angle_held = 1`; while held, `makevectors(porto_v_angle)` overrides the aim used by the next primary
  shot. Released (`!(fire & 2)`) clears the hold. Lets you aim the combined shot then look elsewhere.

### Bounce / placement decision  (`porto.qc:W_Porto_Touch`)
- Runs on each projectile touch. `norm = trace_plane_normal`.
- **Creature hit:** trace a short ray down from the creature; if it doesn't hit world or hits a slick/clip/noimpact
  surface, **ignore** the touch (keep flying).
- **Not the owner's shot** (`realowner.playerid != playerid`): play `SND_PORTO_UNSUPPORTED`, delete (someone else's).
- **Slick (`Q3SURFACEFLAG_SLICK`) or playerclip (`DPCONTENTS_PLAYERCLIP`):** play `SND_PORTO_BOUNCE` (spamsound),
  **reflect** `right_vector` and `angles` about the plane and keep flying (no portal here).
- **No-impact (`Q3SURFACEFLAG_NOIMPACT`):** `SND_PORTO_UNSUPPORTED`, `W_Porto_Fail`, clear portals if combined.
- **type 0 → in-portal only:** `Portal_SpawnInPortalAtTrace`; on success `SND_PORTO_CREATE` + `CENTER_PORTO_CREATED_IN`
  notification + `W_Porto_Success`; on fail `SND_PORTO_UNSUPPORTED` + `W_Porto_Fail`.
- **type 1 → out-portal only:** `Portal_SpawnOutPortalAtTrace`; same success/fail pattern (`CENTER_PORTO_CREATED_OUT`).
- **combined (`EF_RED` set, type -1):** clear EF_RED / set EF_BLUE, `Portal_SpawnInPortalAtTrace`; on success
  `SND_PORTO_CREATE` + notification, reflect `right_vector`/`angles` about the plane, `CSQCProjectile(...PORTO_BLUE)`
  (change type), keep flying; on fail clear all portals + fail.
- **combined blue stage (else):** if `realowner.portal_in.portal_id == portal_id`, `Portal_SpawnOutPortalAtTrace`
  (success/fail as above); else `SND_PORTO_UNSUPPORTED` + clear all portals + fail.
- **Surface-size check** also gates a portal: `CheckWireframeBox(96×96×96)` must fit at the hit (else fail).
- `Portal_SpawnIn/OutPortalAtTrace` derives the portal angles from `fixedvectoangles2(trace_plane_normal, right_vector)`,
  runs `CheckWireframeBox` + `Portal_FindSafeOrigin`, spawns the `portal` MDL_PORTAL entity, links the in/out pair
  via `Portal_Connect` (two-way transform), 200 health, `g_balance_portal_lifetime` (15 s) refreshed on each use.

### Lifecycle / cleanup  (`porto.qc:W_Porto_Think/_Fail/_Success/_Remove`, `porto_ticker`)
- **Think (`nextthink`, lifetime 5 s):** if owner changed (`playerid` mismatch) delete; else `W_Porto_Fail(false)`.
- **Fail:** if no portal placed yet (`cnt < 0` combined), `Portal_ClearWithID`; clear `porto_current`. If a soft fail
  *and* the player still has porto and `weaponthrowable`, drop the porto as a pickup item (`W_ThrowNewWeapon`) +
  `CENTER_PORTO_FAILED` notification. Delete the projectile.
- **Success:** clear `porto_current`, delete.
- **Remove (`W_Porto_Remove`, on player death/reset via `Portal_ClearAll`):** if owner's `porto_current` is theirs,
  `W_Porto_Fail(true)` (hard).
- **`porto_ticker` mutator (SV_StartFrame):** each frame `porto_forbidden = max(0, porto_forbidden - 1)` for every
  player. `porto_forbidden` is set to **2** in `race.qc` right after a Race/CTS respawn so a player can't immediately
  re-fire a porto across the respawn. `wr_resetplayer` clears `porto_current` on respawn.

### Portal teleport  (`portals.qc:Portal_Touch/Portal_Think/Portal_TeleportPlayer`)
- A live in-portal (SOLID_TRIGGER) teleports a player/projectile that will hit its plane within a frame
  (`Portal_WillHitPlane`), applying `portal_transform` to origin, velocity, view angles and `right_vector`
  (factor −1 allows chaining). Telefrag within 1 s of creation → ANNCE_ACHIEVEMENT_AMAZING. Portal pusher credit,
  health (200, damageable), lifetime (15 s) refreshed per use, fade/expire (`SND_PORTO_EXPIRE`) / explode
  (`SND_PORTO_EXPLODE` + ROCKET_EXPLODE effect) on death. Independent-player gating (no using others' portals).

### Presentation — aim-trajectory preview  (`porto.qc:Porto_Draw`, CSQC)
- Client-only. While holding porto (and not in secondary mode, alive, not spectating/intermission), traces the
  shot path with up to 2 reflections (`reflect(dir, trace_plane_normal)`), builds a 16-point polyline, and draws it
  as `Draw_CylindricLine` segments width 4 — **red** before the first portal point, **blue** after — previewing
  where the in/out portals would land. Slick/clip surfaces extend the trace; noimpact/oversize stop it.
- Projectile render: CSQC PORTO_RED / PORTO_BLUE projectile types, model scale 4, WIZSPIKE-style trail.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `W_Porto_Attack` launch | `Porto.Attack` | Eye-shot, speed, bounce projectile, porto_current latch — present & live. |
| `wr_think` fire gating | `Porto.WrThink` (via `WeaponFireDriver.Frame`) | porto_current + PrepareAttack refire gate present. |
| `porto_forbidden` gate | `WeaponSlotState.PortoForbidden` | **READ but never SET or DECREMENTED** — no `porto_ticker` port, no race.qc setter. Inert. |
| `porto_v_angle` aim-hold | `Porto.WrThink` else-branch + `st.PortoVAngle/Held` | Present, logic simplified (see gaps). |
| `W_SetupProjVelocity_Basic` | `gren.Velocity = dir * speed` | Faithful (no spread/up/z). |
| Strength speed boost | `Porto.StrengthForce` (=`4f` hardcoded) | **DEAD: queries `ByName("buff_strength")` which is null** (powerup is `"strength"`, no buff_ prefix) → boost never applies. Also value gap (4 vs Base 3) if fixed. |
| `W_Porto_Touch` decision tree | `Porto.OnTouch` | type 0/1/-1 + red→blue chaining present; **slick/clip reflection, noimpact-fail, creature-hit, surface-size, not-owner branches MISSING.** |
| `Portal_SpawnIn/OutPortalAtTrace` | `Porto.PlacePortal` → `Porto.PortalSpawner` → `WarpzoneManager.PlacePortoPortal` | **Live** (wired in `GameWorld.Boot`). Realised as a linked warpzone pair. |
| `W_Porto_Think/_Fail/_Success` | `Porto.PortoFail`/`PortoSuccess` | Lifetime self-destruct + latch clear present; **throwable-drop + CENTER_PORTO_FAILED + Portal_ClearWithID MISSING.** |
| `W_Porto_Remove` on death | — | **NOT IMPLEMENTED.** No death/reset hook clears porto_current or fails the live projectile. |
| `wr_resetplayer` | — | **NOT IMPLEMENTED** (clears porto_current on respawn). |
| `wr_aim` (bot) | — | **NOT IMPLEMENTED.** Bots never fire porto. |
| Notifications (CREATED_IN/OUT/FAILED) | — | **NOT IMPLEMENTED.** No center-print on portal create/fail. |
| Bounce/create/unsupported sounds | `Porto.OnTouch`/`PlacePortal`/`PortoFail` | fire/create/unsupported played; **BOUNCE never played** (no slick/clip branch); expire/explode belong to portal removal. |
| `Porto_Draw` trajectory preview | — | **NOT IMPLEMENTED.** No red/blue cylindric-line portal-aim preview while holding the weapon. |
| Projectile render (PORTO_RED/BLUE) | `ProjectileCatalog` PortoRed/PortoBlue, WizSpike, scale 4 | Present. |
| Identity/attributes (porto.qh) | `Porto` ctor | Faithful (superweapon/nodual/notrueaim, color, models). |

## Parity assessment

**logic — partial.** The core single-portal launch + the in/out/combined placement tree + the porto_current latch
are present and structured like Base. But several decision branches in `W_Porto_Touch` are absent: no slick/clip
**reflection-and-continue** (the projectile is supposed to bounce off slick/clip without placing and *keep flying*),
no **noimpact fail**, no **creature-hit ignore**, no **not-owner delete**, and no **CheckWireframeBox surface-size**
gate (a porto can "succeed" on a surface too small/invalid in Base). The port's `OnTouch` treats any world-brush hit
as a valid flat surface and always places. `porto_forbidden` is effectively dead. No death/reset cleanup
(`W_Porto_Remove`/`wr_resetplayer`), no throwable-drop on soft fail, and no bot aim.

**values — partial.** Speed (1000), lifetime (5), refire (1.5), animtime (0.3), secondary (1) are cvar-seeded and
faithful. **The Strength launch boost is broken twice over:** (1) it's *dead* — `Attack` looks up
`StatusEffectsCatalog.ByName("buff_strength")`, but the Strength powerup is registered as `"strength"` (only buffs
carry the `buff_` prefix), so the lookup returns null and the `speed *= StrengthForce` line never runs; a porto
under Strength launches at base speed. (2) even if fixed, `StrengthForce` is hardcoded to 4 (nexuiz25), not the
xonotic default 3 from `g_balance_powerup_strength_force`. Portal health/lifetime (200/15 s) live in the warpzone
realiser, not audited here.

**timing — faithful (unknown for portal lifetime).** Refire/animtime gating runs through the standard
`WeaponFireDriver`/`PrepareAttack` path shared by all weapons; lifetime self-destruct via `NextThink`. Portal
15 s fade timing is delegated to the warpzone subsystem and not verified in this unit.

**presentation — partial/missing.** The flying projectile renders (PORTO_RED/BLUE, scale 4, wizspike trail). But the
signature **portal-aim trajectory preview** (`Porto_Draw`'s reflecting red/blue cylindric polyline) is entirely
absent — a player holding porto sees no predicted portal path, the most visible porto-specific HUD/view feature.
No center-print notifications (CREATED_IN/OUT, FAILED).

**audio — partial.** `porto/fire`, `porto/create`, `porto/unsupported` are played (note the port plays `create` on
both the generic touch and the explicit PlacePortal — a double-play). `porto/bounce` is **never** played (the
slick/clip reflection branch that triggers it is missing). `porto/expire` and `porto/explode` belong to portal
removal (warpzone subsystem), out of this unit's scope.

**liveness — live (core), dead (porto_forbidden, Strength boost, aim-hold).** The weapon is registered, drives
through `WeaponFireDriver`, and `PortalSpawner` is wired to the live warpzone manager in `GameWorld.Boot`, so
firing → portal placement runs on the real match path when a player holds porto. Dead code: the `PortoForbidden`
field (never written); the **Strength speed boost** (wrong status-effect name — never matches); and the
**non-secondary aim-hold** (`WrThink(Primary)` runs every tick and clears `PortoVAngleHeld` before any primary fire
can read the angle the secondary tick captured). Death cleanup, bot aim, and trajectory preview are missing rather
than dead.

### Worst gaps (player-observable)
1. No portal-aim trajectory preview (the red/blue reflecting line) while holding porto.
2. Strength speed boost is dead (wrong status-effect name `buff_strength` vs `strength`) — a porto under Strength
   launches at base speed, not Base's boosted speed; the hardcoded multiplier (4 vs Base 3) is moot until fixed.
3. No slick/clip bounce: porto places a portal on (or fails differently at) surfaces Base would bounce off — and
   the bounce sound never plays.
4. No noimpact / surface-size / creature-hit gating: porto succeeds where Base would fail.
5. No portal/center-print notifications; no death-time portal cleanup; bots can't use porto.

## Verification
- **Base extraction:** full read of `porto.qc/.qh`, `portals.qc/.qh`, `tracing.qh` (W_SetupShot/W_SetupProjVelocity),
  `calculations.qc`, and `bal-wep-xonotic.cfg` / `balance-xonotic.cfg` constant grep. Verified.
- **Port mapping:** full read of `Porto.cs`; grep-confirmed `PortalSpawner` wiring in `GameWorld.cs:388` →
  `Warpzone.PlacePortoPortal`; grep-confirmed `PortoForbidden` has no setter/decrementer; grep-confirmed no
  `Porto_Draw`/polyline/trajectory-preview rendering anywhere in `src/` or `game/`; confirmed projectile presentation
  in `ProjectileCatalog.cs` and fire sound in `WeaponFireSounds.cs`. Verified by static read.
- **Not runtime-verified:** in-game portal placement, the double `create` sound, and the warpzone teleport behavior
  (portal subsystem) were not executed; marked accordingly with medium/low confidence where unread at runtime.

## Open questions
- Does the warpzone-realised porto portal actually teleport players/projectiles with the correct angle/velocity
  transform in-game (the `LinkPair`/`WarpzoneTransform` path), and does it expire at 15 s? Needs a runtime check
  (covered more fully by the warpzone unit).
- Is the double `porto/create` play (generic `OnTouch` + `PlacePortal`) audible as a doubled cue, or de-duplicated?
- Is porto ever actually obtainable in any shipped port map/loadout (superweapon, `weaponstart 0`)? If never placed,
  the live path is reachable only via give/cheat — confirm whether any test/map grants it.
