# Nades mutator — parity spec

**Base refs:** `common/mutators/mutator/nades/` (`sv_nades.qc`, `nades.qc/.qh`, `cl_nades.qc`, `net.qc`, `nade/*.qc`) + `mutators.cfg:196-322`
**Port refs:** `src/VortexArena.Common/Gameplay/Nades/**` (core) + `Nades/Booms/*` (per-type) + `game/hud/CrosshairPanel.cs` (charge ring)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The `nades` mutator gives every player an **offhand grenade**. Holding the throw bind (`+hook` release-throw, or
double-tap `weapon_drop`) **primes** a nade — a held, charging projectile that ticks a HUD ring; releasing it throws
with a force that ramps from `minforce` to `maxforce` over the lifetime. Thrown nades are MOVETYPE_BOUNCE projectiles
that can be **shot** (launched/destroyed), **picked up** by other players, and detonate on a non-owner impact or when
their fuse expires. There are **11 real types** plus a random sentinel; each has its own `nade_<type>_boom`. A separate
**bonus-nade economy** accrues bonus nades from score/frags/objectives (and grants infinite bonus nades under Strength).
Activated by `g_nades` (default **0/off**; it is on by default in Overkill). Per-type availability is gated by
`g_nades_<type>` cvars; client type selection by `g_nades_client_select` / `g_nades_bonus_client_select`.

Layer split: almost everything is **authority** (`sv_nades.qc` + `nade/*.qc` SVQC). Presentation (`cl_nades.qc`,
`net.qc` CSQC) covers the projectile model/trail, the orb 3D model + 2D color-flash, the darkness blind overlay, and
the ammo-panel bonus-nade icon. The held-nade charge fraction (`NADE_TIMER`) is a networked stat drawn by the HUD.

## Base algorithm (authoritative)

### Mutator enable + offhand assignment (`sv_nades.qc:706 REGISTER_MUTATOR`, `:801 PlayerSpawn`, `:818`)
- `REGISTER_MUTATOR(nades, autocvar_g_nades)`; on add creates `OFFHAND_NADE`.
- PlayerSpawn: `nade_refire = spawnshield_end (if shielded) else time`; if `!g_nades_onspawn` add `nade_refire`;
  `NADE_TIMER = 0`; assign `OFFHAND_NADE` if the player has no offhand; consume a pending spawn-nade marker.

### Priming + charge-throw (`spawn_held_nade:530`, `nade_prime:576`, `nades_CheckThrow:625`, `offhand_think:675`)
- **Entry:** `ForbidThrowCurrentWeapon` hook (CBC_ORDER_LAST) → `nades_CheckThrow` for the `weapon_drop` path; or
  `PlayerPreThink:726` → `OFFHAND_NADE.offhand_think(player, player.nade_altbutton)` for the `+hook` path.
- `nade_prime`: pick type — Strength + `g_nades_bonus_onstrength` → bonus type (no consume); else a banked bonus
  (decrement); else client-select (`cl_nade_type`) or `g_nades_nade_type`. `g_nades_bonus_only` forbids priming
  without a bonus. Then `spawn_held_nade(this, this, g_nades_nade_lifetime, ntype.netname, pntype)`.
- `spawn_held_nade`: build the held `nade` + first-person `fake_nade`; `.wait = time + lifetime`; `nextthink =
  max(wait-3, time)` (beep starts 3s before boom); `nade_time_primed = time`; `setthink nade_beep`; alpha = type alpha.
- **Charge math (`:646-650` weapon_drop, `:695-699` offhand):** require ≥1s since prime; `_force = (time -
  primed)/lifetime`; `_force = minforce + _force*(maxforce-minforce)`; dir = `v_forward*W_f + v_up*W_u + v_right*W_r`
  (weapon_drop **0.75/0.2/0.05**, offhand **0.7/0.2/0.1**); `W_CalculateSpread(dir, g_nades_spread, spread_style, false)`.
- PlayerPreThink held-nade tracking: `NADE_TIMER = bound(0,(time-primed)/lifetime,1)`; keep the held nade at
  `origin + view_ofs + v_forward*8 + v_right*-8`; sync velocity; **auto-toss** at `time+0.1 >= wait` + center notification.

### Thrown-nade lifecycle (`toss_nade:321`, `nade_touch:192`, `nade_beep:239`, `nade_damage:246`, `nade_pickup:179`)
- `toss_nade`: detach held + fake; place at `w_shotorg + g_nades_throw_offset`; size **16 (small) / 32**; MOVETYPE_BOUNCE.
  If looking ~vertical (`v_angle.x∈[70,110]`) + crouch → velocity `'0 0 100'`; else newton-style: **0**=absolute
  (`W_CalculateProjectileVelocity`), **1**=`e.velocity + _velocity`, **2**=`_velocity`. `health = g_nades_nade_health (25)`;
  `takedamage = DAMAGE_AIM`; `event_damage = nade_damage`; `gravity 1`; `solid SOLID_CORPSE`; `missile_flags MIF_SPLASH|MIF_ARC`;
  `damagedbycontents = true`. `dphitcontentsmask` = SOLID|PLAYERCLIP|BOTCLIP for translocate/spawn/monster, else SOLID|BODY.
  `nade_refire = time + g_nades_nade_refire (6)`.
- `nade_touch`: owner pass-through; **pickup** if `g_nades_pickup` & past `spawnshieldtime` & toucher has no nade & nade
  at full health & `CanThrowNade(toucher)` & real client → `nade_pickup` (hand a `pickup_time (2s)` fuse, set refire);
  needkill content → delete; full-health nade just **bounces** (random bounce sound); damaged nade → `nade_boom`.
- `nade_damage`: translocate/spawn ignore damage (no map-spanning launches). Per-weapon force/damage tweaks:
  **blaster** `force*1.5, damage=0`; **vortex/vaporizer/okvortex** `force*6, damage=max_health*0.55`;
  **machinegun/okmg** `damage=max_health*0.1`; **shotgun/okshotgun** primary `damage=max_health*1.15`; **melee** `force*10,
  damage=max_health*0.1`. `velocity += force`. If `damage<=0` or (onground & player attacker) return. First hit at full
  health arms beep + extends fuse to `time+lifetime`. Subtract HP; credit attacker (except tl/spawn). HP≤0 → run
  spawn/translocate DestroyDamage then `W_PrepareExplosionByDamage(nade_boom)`.

### Detonation dispatch (`nade_boom:114`) + per-type booms
- Resolve type from `NADE_BONUS_TYPE`; if `!takedamage` (killed by lava/void) or Null → **NORMAL**. Play
  `SND_ROCKET_IMPACT`; clear `event_damage`; switch to `nade_<type>_boom`; remove hooks attached; delete the nade.
- **normal** (`normal.qc`): `RadiusDamage(damage 225, edge 90, radius 300, force 650)` + DamageInfo.
- **napalm** (`napalm.qc`): spawn `ball_count (6)` MOVETYPE_BOUNCE fireballs + a MOVETYPE_TOSS fountain (lifetime 3s).
  Each ticks every 0.1s: `napalm_damage` picks ONE target by `RandomSelection` weight `1/(1+d)` (prefer not-burning),
  `Fire_AddDamage(d*burntime, burntime)`. Fountain ejects a ball every `fountain_delay (0.5)`. Ball:
  `damage 40, radius 100, lifetime 7, spread 500, damageforcescale 4`. Fountain: `damage 50, edge 20, radius 130`.
  `burntime 0.5`, `selfdamage 1`.
- **ice** (`ice.qc`): MOVETYPE_TOSS freeze-field, lifetime `ice_freeze_time (3)`. Every 0.1s freeze live
  players/monsters in `ice_radius (300)` via `STATUSEFFECT_Frozen` for `current_freeze_time = ltime-time-0.1` (a
  declining freeze). `ice_teamcheck (2)`: 0=all, 1=skip self, 2=skip team+self. Skip recently-revived (<1.5s). Optional
  `ice_explode (0)` → final normal boom.
- **translocate** (`translocate.qc`): resize to player hull ±16, `move_out_of_solid`, tracebox down, `TeleportPlayer`
  owner to the spot carrying reprojected speed. Shot to death → `Damage(owner, attacker, translocate_destroy_damage 25)`
  + self-detonate as normal (returns true → suppress normal boom).
- **spawn** (`spawn.qc`): plant a SOLID_NOT EF_STARDUST `nade_spawn_loc` marker with `cnt = spawn_count (3)`; PlayerSpawn
  relocates the thrower there + `spawn_health_respawn (0=normal)`. Shot to death → `Damage(owner, spawn_destroy_damage 25)`,
  returns false (normal boom still runs).
- **heal** (`heal.qc`): `nades_spawn_orb(heal_time 10, heal_radius 300)`; touch heals friend/self
  (`heal_rate 15 * frametime * 0.5`, friend ×`heal_friend 1`, foe ×`heal_foe -4` → `Damage(DEATH_NADE_HEAL)`) up to
  `healthmega_max`; armor to friends (`heal_armor_rate 0` → no-op by default) up to `armormega_max`.
- **pokenade/monster** (`monster.qc`): if `g_monsters`, `spawnmonster(pokenade_type, owner, INSANE)`, noalign,
  `monster_lifetime (150)`.
- **entrap** (`entrap.qc`): `nades_spawn_orb(entrap_time 10, entrap_radius 500)`; touch damps enemy velocity
  `entrap_strength (0.01) ** dt` (ticrate-independent, dt capped 0.15) + flags real clients `nade_entrap_time`. The
  `PlayerPhysics_UpdateStats`/`MonsterMove` hooks scale speed by `entrap_speed (0.5)` (affects thrower + team too).
- **veil** (`veil.qc`): `nades_spawn_orb(veil_time 8, veil_radius 250)`; teammates' alpha saved → -1 (hidden); enemies
  tinted (render); `nade_veil_time` bumped; `nade_veil_Apply` restores alpha on lapse (PlayerPreThink).
- **ammo** (`ammo.qc`): `nades_spawn_orb(ammo_time 4, ammo_radius 300)`; touch keeps friend magazines full / drains foe
  (`ammo_clip_empty_rate 0`), gives ammo (`ammo_rate 30 * ft * 0.5`, friend ×`ammo_friend 1`, foe ×`ammo_foe -2`) up to
  pickup maxes.
- **darkness** (`darkness.qc`): MOVETYPE_TOSS dark-field, `darkness_time (4)`, `darkness_radius (300)`,
  `darkness_teamcheck (2)`; every 0.1s sets `NADE_DARKNESS_TIME` on real clients (CSQC blind overlay + `SND_BLIND`).
  Optional `darkness_explode (0)`.

### Bonus economy (`nades_GiveBonus:445`, `nades_RemoveBonus:465`, PlayerDies:836, MonsterDies:896)
- `GiveBonus`: gated by `g_nades`+`g_nades_bonus`, real live unfrozen player, `NADE_BONUS < bonus_max (3)`. Accrue
  `score/bonus_score_max (120)` while `<1`; at `≥1` bank a nade + center notif + `SND(NADE_BONUS)`, `--score`.
- PlayerPreThink per-second accrual: `time_score = bonus_score_time (-1, decays)` or `_time_flagcarrier (2)` × key count.
- PlayerDies (CBC_ORDER_LAST): toss the held nade on death (unless freezetag revive-nade mid-flight); teamkill/suicide →
  wipe attacker bonus; VIP-kill → `bonus_score_medium (30)`; spree milestone → `bonus_score_spree (40)`; else
  killcount-scaled `bound(0, bonus_score_minor (5) * killcount, bonus_score_medium)`. Always wipe victim bonus.
- MonsterDies: killing a non-spawned enemy monster → `bonus_score_minor`.

### Damage_Calculate freezetag revive-nade (`:876`)
- A frozen player hit by their OWN `DEATH_NADE` within 0.1s of toss → `freezetag_Unfreeze`, set
  `freezetag_revive_nade_health`, ice-or-glass effect, suppress damage/force, broadcast revive notifications.

### Networking / presentation (`net.qc`, `cl_nades.qc`)
- `Nade_Orb` is a NET_LINKED entity (type/origin/ltime/lifetime/radius). CSQC `orb_setup`/`orb_draw` animate scale-up +
  fade + spin; `orb_draw2d` paints a full-screen additive color flash (`hud_colorflash_alpha`) when the view is inside
  the orb. `cl_nades` sets the projectile model/trail per type, the bounce movetype, `scale 1.5`, random avelocity,
  and the per-type `dphitcontentsmask`. Darkness blind = `HUD_DarkBlinking` full-screen fill.
- `DrawAmmoNades` (cl_nades.qc:86): the ammo-panel bonus-nade count + progress bar + type icon (rainbow for random).

## Port mapping
| Base | Port |
|---|---|
| `REGISTER_MUTATOR(nades)` + hooks | `NadesMutator` (`[Mutator]`, IsEnabled = `g_nades!=0`); Hook adds PlayerSpawn/PlayerPreThink/PlayerDies(Last)/DamageCalculate. Activated via `MutatorActivation.Apply()` (`GameWorld.cs:511`). |
| `spawn_held_nade`/`nade_prime`/`CanThrowNade`/`nades_CheckThrow`/`offhand_think`/`nades_Clear` | `NadeThrow.cs` |
| `toss_nade`/`nade_touch`/`nade_beep`/`nade_damage`/`nade_pickup` | `NadeProjectile.cs` |
| `nade_boom` dispatch + `nades_spawn_orb`/`nades_orb_think` | `NadeBoom.cs` (`NadeBoomRegistry` reflection seam) |
| Nades registry + `nades_CheckTypes`/`Nades_FromString`/`nade_choose_random`/`Nades_GetType` | `NadeRegistry.cs` (+ `NadeDeathTypes`) |
| `nades_GiveBonus`/`RemoveBonus`/PlayerDies+MonsterDies bonus | `NadeBonus.cs` |
| `nade_<type>_boom` (×11) | `Booms/Nade<Type>Boom.cs` (each `INadeBoom`; spawn/translocate also `INadeDestroyDamage`) |
| `PlayerPhysics_UpdateStats` entrap-slow | `NadeEntrapSpeedMutator` (separate `[Mutator]`, in `NadeEntrapBoom.cs`) |
| `NADE_TIMER` HUD ring | `CrosshairPanel.NadeTimer` (networked via `ServerNet.cs:1663` → `NetGame.cs:2100`) |
| `cl_nades` projectile/orb/darkness render, `DrawAmmoNades` bonus icon | **NOT IMPLEMENTED** (headless-only; render omissions documented in each file) |

## Parity assessment

### Liveness — THE dominant gap
- The **NadesMutator hooks are live** (`MutatorActivation.Apply()` is called at match start; the PlayerSpawn/PreThink/
  Dies/DamageCalc chains fire each tick), and the entire logic is exercised by **28 unit tests** that call
  `NadeThrow.Prime/Toss`, `NadeBoom.Detonate`, `NadeBonus.*` directly.
- **BUT the throw input is dead.** `InputCommand.InputButtons` (`src/VortexArena.Net/InputCommand.cs:11`) has **no
  nade/offhand/hook/weapon_drop bit** — only Attack/Jump/Attack2/Zoom/Crouch/Use. `Entity.OffhandFirePressed` and
  `Entity.NadeAltButton` are **read** by `NadesMutator.OnPlayerPreThink` but **assigned nowhere** in `src/` (verified by
  grep — same dead-input failure as the offhand hook, ../../TODO.md T11). `NadeThrow.CheckThrow` (the weapon_drop path) has
  **no caller** (NadesMutator deliberately does not subscribe `ForbidThrowCurrentWeapon`). So in a real match a player
  **can never prime or throw a nade**: `OffhandThink` is called every tick with `keyPressed=false`, so it never primes.
- Consequence: every downstream behavior (charge, toss, all 11 booms, pickup, shoot-down, orbs, freeze/heal/etc.) is
  **dead on the live path** — reachable only by the death-toss (PlayerDies tosses a held nade, but a player never has
  one) and the bonus accrual (which runs but is invisible: no bonus nade can ever be primed/thrown either).
- The **bonus economy accrual + PlayerDies award** runs live (PlayerPreThink/PlayerDies fire), and the
  spawn-nade relocate in PlayerSpawn is live IF a marker exists — but no marker can be planted without a throw.
- **Omitted cleanup/lifecycle hooks (NEW):** `NadesMutator.Hook` subscribes **only** PlayerSpawn / PlayerPreThink /
  PlayerDies / Damage_Calculate. The QC `MakePlayerObserver` / `ClientDisconnect` / `reset_map_global`
  (`nades_RemovePlayer` = `nades_Clear` + `RemoveBonus` + delete `nade_spawnloc`), `PutClientInServer` (`RemoveBonus`),
  `SpectateCopy`, `VehicleEnter` (toss), and `DropSpecialItems` (toss) hooks are **not wired** — `nades_Clear`/
  `RemoveBonus` exist as methods but have no live caller on those paths (held-nade/bonus/spawn-marker leak across
  observer/disconnect/round-reset). `VehicleEnter` exists as a port HookChain but nades does not subscribe it;
  `DropSpecialItems` and `SpectateCopy` have no port HookChain at all. `MonsterDies` bonus (`NadeBonus.OnMonsterDies`)
  is written but also has **no live caller**.

### Logic / values
- The server-side logic and constants are a **faithful, thorough** port: all 11 types + random sentinel, the type
  registry with stable ids 0..11, the selection helpers, the charge-force ramp, the newton-style velocity, the
  per-weapon nade_damage tweaks, the pickup rules, the bonus economy + spree thresholds, the freeze/heal/ammo/entrap/
  veil/darkness/spawn/translocate/monster booms, and the orb helper. Constants match `mutators.cfg` defaults (verified
  inline in each file).
- **Value/logic gaps found:**
  - **napalm `damageforcescale 4`** (`g_nades_napalm_ball_damageforcescale`) is **not applied** — port models napalm as
    the Burning status effect (no knockback on the fireballs), so the ball's damage-force push is missing.
  - **Newton-style 0** is implemented as pure `_velocity` (absolute) rather than `W_CalculateProjectileVelocity(...,
    true)`. QC style-0 still routes through `W_CalculateProjectileVelocity` which can blend with player velocity per
    `g_projectiles_newton_style`; the port hardcodes absolute. Minor for the default config but a behavioral divergence.
  - **`nade_damage` melee-slap branch** (`force*10, damage=max_health*0.1` for weapons flagged
    `WEP_TYPE_MELEE_PRI`/`WEP_TYPE_MELEE_SEC`, `sv_nades.qc:279-285`) is **omitted** — the port's
    `NadeProjectile.NadeDamage` switch (lines 191-205) covers only blaster/vortex/vaporizer/mg/shotgun launches.
  - **`Nade_Damage` mutator hook** (overkill/etc. adjusting nade damage/force) is not invoked (documented deferral).
  - **`g_nades_napalm` water-damping**, the round-handler "round not started → delete fountain" guard, and the
    `revival_time < 1.5s` ice skip are partially/not modeled (ice skip is omitted; round guard omitted — relevant only
    in CA/round modes).
  - **pokenade `monster_lifetime`** has no field in the port's monster model → the timed despawn is **missing**.
  - **freezetag revive-nade**: the port suppresses damage/force but does **not** call `freezetag_Unfreeze` /
    set revive health / broadcast (documented cross-task seam) — so the revive itself doesn't happen.
  - **`g_nades_client_select` / `g_nades_bonus_client_select`**: the port always uses the server cvars
    (`g_nades_nade_type` / `g_nades_bonus_type`); there is no per-client `cl_nade_type` plumbed to the headless sim.

### Timing
- Think cadences are faithful (orb/ice/darkness/napalm 0.1s ticks; beep at wait-3; auto-toss at wait-0.1; orb particle
  gate 0.05s). They depend on the port's think scheduler running at the server tick — consistent with the rest of the
  port. Frame-time scaling in heal/ammo touch (`frametime * 0.5`) matches QC.

### Presentation / audio
- **Presentation is essentially missing** (the port is headless): no projectile model/trail, no orb 3D model + 2D
  color-flash, no darkness blind overlay, no spawn-loc stardust render, no `DrawAmmoNades` bonus-nade ammo-panel icon.
  The ONE wired piece is the **held-nade charge ring** (`CrosshairPanel.NadeTimer`, networked) — but since a nade can
  never be primed, `NadeTimer` is always 0, so even that never shows.
- **Audio:** the boom path plays `weapons/rocket_impact.wav`, the bounce plays `weapons/grenade_bounce1.wav`, the beep
  plays `overkill/grenadebip.ogg`, napalm balls play `weapons/fireball_fire.wav`. The `SND_NADE_BEEP`/`SND_NADE_BONUS`/
  `SND_BLIND`/`SND_NADE_NAPALM_FLY` cues and the `findbetterlocation` explosion-with-color particle are not faithfully
  matched, and all are unreachable because the throw path is dead.

## Verification
- Base constants: read directly from `mutators.cfg:196-322` and the per-type `.qc`/`.qh`.
- Port logic/values: read all of `Nades/**` (core + 11 booms) — constants verified inline.
- Liveness: `grep OffhandFirePressed=/NadeAltButton=` over the whole repo → reads only, zero assignments;
  `InputCommand.InputButtons` enum inspected (no nade/offhand bit); `NadeThrow.CheckThrow` has no caller;
  `MutatorActivation.Apply()` confirmed called at `GameWorld.cs:511` (so the hooks ARE added when `g_nades!=0`).
- Tests: `tests/VortexArena.Tests/PowerupsAndNadesTests.cs` — 28 facts; they prove the logic but bypass the input layer.

## Open questions
- Will the input layer gain an offhand/nade button bit (reviving the throw path for nades AND the offhand hook/blaster),
  or is the offhand family intentionally deferred? This single wire is the gate for the entire subsystem.
- Is `g_nades` ever set to 1 by any shipped ruleset/campaign in the port? (Overkill enables it in Base; confirm the
  port's Overkill config does the same — out of scope here.)
- Should newton-style 0 route through the port's full `W_CalculateProjectileVelocity` for exact velocity parity?
