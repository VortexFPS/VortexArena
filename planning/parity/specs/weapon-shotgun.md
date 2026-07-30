# Shotgun — parity spec

**Base refs:** `common/weapons/weapon/shotgun.qc` (+ `shotgun.qh`); shared fire math in
`server/weapons/tracing.qc` (`fireBullet_falloff`, `W_SetupShot_Dir_ProjectileSize_Range`),
`common/weapons/calculations.qc` (`W_CalculateSpread`, `W_CalculateSpreadPattern`); balance in
`bal-wep-xonotic.cfg` (`g_balance_shotgun_*`) + `balance-xonotic.cfg` (`g_weaponspreadfactor`,
`g_hitscan_spread_style`) + `xonotic-server.cfg` (`g_ballistics_*`, `g_casings`, `g_trueaim_minrange`).
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Shotgun.cs`,
`.../Weapons/WeaponFiring.cs`, `.../Weapons/WeaponFireDriver.cs`, `.../Weapons/WeaponFireGate.cs`,
`.../Gameplay/Effects/EffectEmitter.cs`; client render in `game/client/EffectSystem.cs`.
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The Shotgun is one of the two default starting weapons. Primary fire (hitscan) sprays a fan of 12
pellets, each an independent `fireBullet_falloff` trace dealing 4 damage with optional distance
falloff, solid penetration and knockback. Secondary fire (default `g_balance_shotgun_secondary 1`)
is a short-range melee "slap" that sweeps several traces over a swing arc in front of the actor; an
alternate secondary mode (`secondary 2`) fires a triple shotgun blast instead. It runs in every
gametype that grants the weapon (standard arenas, default loadout).

## Base algorithm (authoritative)

### Primary fire — pellet fan  (`shotgun.qc:W_Shotgun_Attack`, called from `wr_think` fire&1)
- **Trigger / entry:** server `wr_think`, `fire & 1`, gated by a PRIVATE timer
  `shotgun_primarytime` (so a melee can follow a blast immediately), AND `weapon_prepareattack(...,
  false, animtime)`. On success: park `shotgun_primarytime = time + refire * W_WeaponRateFactor`,
  schedule `WFRAME_FIRE1` for `animtime`, return to `w_ready`.
- **Algorithm:**
  1. `W_DecreaseAmmo(thiswep, actor, ammo)` — consume 1 shell.
  2. `W_SetupShot(actor, weaponentity, true /*antilag*/, recoil=5, SND_SHOTGUN_FIRE, channel,
     damage*bullets, m_id)` — trueaim trace from eye, muzzle nudge, recoil punchangle −5°, fire-credit
     `damage*bullets` to accuracy.
  3. Antilag takeback ONCE for the whole volley (`antilag_takeback_all`), not per pellet.
  4. If `spread_pattern && spread_pattern_scale > 0`: lay pellets out via `W_CalculateSpreadPattern`
     (deterministic fan, `fixed_spread_factor = spread*g_weaponspreadfactor / spread_pattern_scale`,
     each pellet `fireBullet_falloff` with spread arg 0 + the pattern offset). **In xonotic balance
     `spread_pattern_scale` defaults 0, so this branch is OFF.**
  5. Else (default): for each of `bullets` pellets, `fireBullet_falloff(actor, w_shotorg, w_shotdir,
     spread, solidpenetration, damage, falloff_halflife, falloff_mindist, falloff_maxdist, 0, force,
     falloff_forcehalflife, m_id, EFFECT_BULLET_WEAK, false /*no per-pellet antilag*/)`.
  6. `antilag_restore_all`.
  7. `W_MuzzleFlash(...)` — muzzleflash model + EFFECT_SHOTGUN_MUZZLEFLASH.
  8. If `autocvar_g_casings >= 1`: `makevectors(actor.v_angle)`; `SpawnCasing(...)` — ONE ejected
     shell casing with randomized velocity.
- **`fireBullet_falloff` (tracing.qc):** `W_CalculateSpread(dir, spread, g_hitscan_spread_style, true)`,
  then trace from start to `start + dir*max_shot_distance`; sky stops it; on a damageable hit apply
  `Damage` with `damage * damage_fraction` and `force*dir*damage_fraction`, with exponential
  distance falloff for damage and force (only if a halflife is nonzero); then penetrate the solid up
  to `solidpenetration * pen_fraction` world units (`damage_fraction = pen_fraction ^
  g_ballistics_solidpenetration_exponent`), looping to hit further entities; double-hit + self-hit
  guarded; out-of-world stops.
- **Constants (primary, `g_balance_shotgun_primary_*`):**
  `ammo = 1`, `animtime = 0.2`, `bullets = 12`, `damage = 4` (per pellet), `force = 15`,
  `refire = 0.75`, `solidpenetration = 3.8`, `spread = 0.12`,
  `damagefalloff_halflife = 0`, `damagefalloff_mindist = 0`, `damagefalloff_maxdist = 0`,
  `damagefalloff_forcehalflife = 0`, `spread_pattern = 0`, `spread_pattern_scale = 0`,
  `spread_pattern_bias = 0`. Shared/global: `g_weaponspreadfactor = 1`, **`g_hitscan_spread_style = 4`
  (gauss 2D)**, `g_ballistics_mindistance = 2`, `g_ballistics_solidpenetration_exponent = 1`,
  `g_ballistics_penetrate_clips = 1`, `g_trueaim_minrange = 44`, `g_casings = 2`,
  `max_shot_distance = 32768`. Recoil punch = 5.

### Secondary melee slap  (`shotgun.qc:W_Shotgun_Attack2` + `W_Shotgun_Melee_Think`, when `secondary == 1`)
- **Trigger / entry:** `wr_think`, `secondary == 1`, routed AFTER the primary/triple block. Fires on
  `(fire & 2)` OR the out-of-ammo `(fire & 1)` auto-melee fallback, gated by
  `(!melee_blockedbyfiring || time >= shotgun_primarytime)` and
  `weapon_prepareattack(..., true, refire)`. Plays `SND_SHOTGUN_MELEE`, schedules `WFRAME_FIRE2`
  for `animtime`, spawns a `meleetemp` think entity at `time + melee_delay * W_WeaponRateFactor`.
- **Algorithm (`W_Shotgun_Melee_Think`, runs over multiple server frames spanning `melee_time`):**
  - On first run set `cnt = time`, play strength sound.
  - `meleetime = melee_time * W_WeaponRateFactor`; `swing = bound(0, (cnt+meleetime-time)/meleetime, 10)`;
    `f = (1 - swing) * melee_traces` — the number of traces to do THIS frame.
  - For `i` from `swing_prev` to `f`: `swing_factor = (1 - i/melee_traces)*2 - 1`;
    `targpos = eye + melee_path*swing_factor + v_forward*melee_range` where
    `melee_path = v_up*melee_swing_up + v_right*melee_swing_side`. Antilag traceline to targpos.
  - `Send_Effect(EFFECT_SHOTGUN_WOOSH, trace_endpos, -melee_path, 1)` per trace.
  - On a good hit (`trace_fraction<1`, takedamage, not the already-hit ent, and player OR
    `melee_nonplayerdamage`): `swing_damage = (is_player ? damage : melee_nonplayerdamage) *
    min(1, swing_factor + 1)`; `Damage(..., HITTYPE_SECONDARY, force = v_forward * force)`; accuracy add.
  - `melee_multihit`: allow several hits per swing but never the same target twice
    (`swing_alreadyhit`); else delete after first hit.
  - Ends when `time >= cnt + meleetime`; if `IS_DEAD && melee_no_doubleslap`, abort.
- **Constants (secondary, `g_balance_shotgun_secondary_*`):** `secondary = 1`, `animtime = 1.15`,
  `damage = 70`, `force = 200`, `refire = 1.25`, `alt_animtime = 0.2`, `alt_refire = 1.2`,
  `melee_blockedbyfiring = 0`, `melee_delay = 0.25`, `melee_multihit = 1`,
  `melee_nonplayerdamage = 40`, `melee_no_doubleslap = 1`, `melee_range = 120`,
  `melee_swing_side = 120`, `melee_swing_up = 30`, `melee_time = 0.15`, `melee_traces = 10`.

### Alt secondary triple-shot  (`shotgun.qc:W_Shotgun_Attack3_Frame1/2`, when `secondary == 2`)
- **Trigger:** `wr_think`, `(fire & 2) && secondary == 2`, gated by `shotgun_primarytime` +
  `weapon_prepareattack(..., false, alt_animtime)`. Parks `shotgun_primarytime = time + alt_refire *
  rate`, schedules `WFRAME_FIRE1` for `alt_animtime` to `W_Shotgun_Attack3_Frame1`.
- **Algorithm:** three staged blasts via `W_Shotgun_Attack` over Frame1 → Frame2 (each re-checks
  ammo + can W_SwitchWeapon_Force away if dry). The last shot uses the full reload sound trick.
  **Non-default** (`secondary` defaults 1).

### Ammo / reload / kill messages
- `wr_checkammo1`: shells ≥ `primary_ammo` (or clip load ≥ ammo). `wr_checkammo2`: `secondary==1` →
  always true (melee free); `secondary==2` → shells ≥ `primary_ammo`; else false.
- `wr_reload`: `W_Reload(actor, ammo, SND_RELOAD)` (reloadable; `reload_ammo` defaults 0 = no clip).
- `wr_aim` (bot): if target within `melee_range` press ATCK2 (melee), else ATCK (ranged), via
  `bot_aim(..., 1000000, 0, 0.001, ...)`.
- `wr_killmessage`: `HITTYPE_SECONDARY` → WEAPON_SHOTGUN_MURDER_SLAP, else WEAPON_SHOTGUN_MURDER.
  `wr_suicidemessage`: WEAPON_THINKING_WITH_PORTALS.

### Presentation (CSQC `wr_impacteffect`)
- On a pellet impact (client side): `pointparticles(EFFECT_SHOTGUN_IMPACT, w_org + w_backoff*2,
  w_backoff*1000, 1)`; and, throttled to once per 0.25 s (`prevric`), a 5%-chance random ricochet
  sound `SND_RIC_RANDOM()` on CH_SHOTS at the impact. Crosshair `gfx/crosshairshotgun`, size 0.65.
- Muzzleflash model `models/uziflash.md3` + EFFECT_SHOTGUN_MUZZLEFLASH; one shell casing per shot
  when `g_casings >= 1` (default 2).

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `wr_think` primary/secondary routing + private `shotgun_primarytime` | `Shotgun.WrThink` | Faithful: positive `attackTime=animtime` into `PrepareAttack` + parks `ShotgunPrimaryTime`. |
| `W_Shotgun_Attack` pellet fan | `Shotgun.Attack` | 12 pellets, ammo decrement, recoil, per-pellet `FireBullet`. |
| `fireBullet_falloff` | `WeaponFiring.FireBullet` | Spread, solid penetration loop, falloff, force, headshot, double-hit guard. |
| `W_SetupShot` | `WeaponFiring.SetupShot` | Trueaim, muzzle offset, recoil punch, fire-credit. |
| `W_CalculateSpread` | `WeaponFiring.CalculateSpread` | **Only style 0 implemented; Base default style 4.** |
| `W_CalculateSpreadPattern` | `WeaponFiring.CalculateSpreadPattern` | Pattern 1 only; pattern-fan path NOT wired into `Shotgun.Attack` (uses random per-pellet spread). |
| `ExponentialFalloff` | `WeaponFiring.ExponentialFalloff` | Faithful (halflife defaults 0 → no falloff). |
| Melee (`W_Shotgun_Attack2`/`_Think`) | `Shotgun.Melee` | Single-pass swing arc, swing_factor damage scale, multihit dedupe. |
| Alt triple-shot (`secondary==2`) | `Shotgun.WrThink` secondary==2 branch | Fires ONE blast immediately; the staged Frame1/Frame2 (3 blasts) NOT implemented. |
| `wr_checkammo1/2` | `Shotgun.CheckAmmoPrimary/CheckAmmoSecondary` | Faithful. |
| `wr_reload` | (base `WrReload`) | Reloadable flag set; clip reload timing deferred (driver T6). |
| `wr_aim` (bot melee/ranged) | NOT IMPLEMENTED | No `WrAim` override; bots don't switch to melee at close range. |
| `wr_killmessage`/`wr_suicidemessage` | `DeathMessages.SelectKillMessage`/`SelectSuicideMessage` (shotgun branch) | Selector is faithful (SLAP vs MURDER on `sec`), suicide line correct. **BUG:** `Shotgun.Melee` calls `ApplyDamage(..., RegistryId)` and `WeaponFiring.ApplyDamage` maps it via `FromWeapon(NetName)` with NO HITTYPE_SECONDARY — so the melee kill always shows WEAPON_SHOTGUN_MURDER, never MURDER_SLAP (the `sec` branch is unreachable for the shotgun). |
| Muzzleflash | `Shotgun.Attack` → `EffectEmitter.Emit("SHOTGUN_MUZZLEFLASH")` | Emitted; client renders via EffectSystem. |
| Casing eject (`g_casings`) | NOT EMITTED | `Shotgun.Attack` never spawns a casing; client `ShellCasings`/`SpawnCasing` exists but no caller. |
| `wr_impacteffect` (impact particle) | `Shotgun.Attack` per-pellet `EffectEmitter.Emit("SHOTGUN_IMPACT")` | Moved SERVER-side per pellet (Base is CSQC); ~12 emits/shot. |
| `wr_impacteffect` ricochet sound (5%/0.25 s) | NOT IMPLEMENTED | `SoundSystem.PlayRic` exists but the shotgun impact path never calls it. |
| Melee woosh (EFFECT_SHOTGUN_WOOSH per trace) | NOT EMITTED | `Shotgun.Melee` emits no woosh effect. |
| Crosshair `gfx/crosshairshotgun` size 0.65 | NOT VERIFIED | Client crosshair attribs not traced. |

**Liveness:** the fire path is LIVE — `GameWorld.WeaponThink` (server tick, `VortexArena.Server/GameWorld.cs:1182`) →
`WeaponFireDriver.Frame` → `Shotgun.WrThink` → `Attack`/`Melee`. Effect emissions go through
`EffectEmitter.Sink`, which is LIVE on both ends in a real match: `ServerNet.cs:287` installs the
networking `EffectNetSink` and `ClientWorld.cs:352` installs the rendering `RenderSink` (the headless
recording sink is only the test default). So muzzleflash/impact DO reach the client; the open item is
fidelity, not reachability.

## Parity assessment
- **logic:** Primary fan + ammo logic faithful. Gaps: (a) triple-shot (`secondary==2`) fires a single
  blast not the staged 3-blast Frame1/Frame2 sequence — AND the port's single-blast path uses
  `Secondary.Refire/Animtime` (1.25/1.15) instead of the correct `alt_refire/alt_animtime` (1.2/0.2);
  (b) no bot `wr_aim` melee/ranged switch; (c) the out-of-ammo primary auto-melee fallback is not
  wired; (d) melee `melee_delay` (0.25 s wind-up) is read into the balance struct but NOT applied, and
  `melee_no_doubleslap` is unmodeled; (e) **melee kill never tags HITTYPE_SECONDARY**, so the
  WEAPON_SHOTGUN_MURDER_SLAP obituary is unreachable. (a)–(c) are non-default-path; (d) and (e) affect
  the DEFAULT melee secondary.
- **values:** Primary/secondary balance constants all match (read from the same `g_balance_shotgun_*`
  cvars via `Bal()`). Two SHARED-cvar value gaps: (1) `g_hitscan_spread_style` default is **4**
  (gauss-2D) but the port's `CalculateSpread` only implements style 0 (uniform sphere); the pellet
  scatter distribution differs from Base. (2) `g_ballistics_mindistance` default is **2** but the
  port hardcodes `1f`, so bullets penetrate walls slightly thinner than Base allows.
- **timing:** Refire/animtime gating faithful (private `shotgun_primarytime` + shared
  ATTACK_FINISHED, `W_WeaponRateFactor` applied). Melee is collapsed to a single-frame pass instead
  of Base's traces-spread-over-`melee_time` think loop — gameplay-equivalent for total damage but the
  damage all lands on one tick rather than ramping across the 0.15 s swing (a player can't "duck out"
  mid-swing as in Base).
- **presentation:** Muzzleflash + impact puff emitted (impact moved server-side, ~12/shot). Gaps:
  no shell-casing eject (Base default `g_casings 2` ejects one), no melee woosh effect.
- **audio:** Fire sound (`shotgun_fire`) and melee sound (`shotgun_melee`) play. Gap: the impact
  RICOCHET sound (`SND_RIC_RANDOM`, 5% chance / 0.25 s throttle) is not played.

### Intended divergences
- Melee single-pass vs multi-frame think: a deliberate simplification (commented in `Shotgun.Melee`)
  — flagged here as a timing gap rather than intended_divergence because it changes observable
  swing timing; left for an owner call.
- Server-side impact effect: the port emits SHOTGUN_IMPACT server-side per pellet rather than from
  CSQC `wr_impacteffect`; visually similar but networks ~12 point-effects per shot.

## Verification
- Base constants: read directly from `bal-wep-xonotic.cfg`, `balance-xonotic.cfg`,
  `xonotic-server.cfg` (values quoted above).
- Port constants: read from `Shotgun.Configure()` — defaults match the cfg; live values come from
  the same cvar names via `Bal()`.
- Liveness: traced `GameWorld.cs:1182` → `WeaponFireDriver.Frame` → `Shotgun.WrThink` (live).
- Spread style: `WeaponFiring.CalculateSpread` source — single style-0 branch (no style switch);
  Base `W_CalculateSpread` has 8 styles, default cvar 4. UNVERIFIED in-game (static read).
- Casing/ricochet/woosh: grep confirms no caller from the shotgun path (static).

## Open questions
- RESOLVED (effect sink): `ServerNet.cs:287` (EffectNetSink) + `ClientWorld.cs:352` (RenderSink) make
  the effect path live; the headless recorder is only the test default. Remaining: is the MUZZLEFLASH
  uziflash.md3 MODEL actually attached/rendered (only the particle is confirmed emitted)?
- RESOLVED (kill message): the selector is faithful but `Shotgun.Melee` never tags HITTYPE_SECONDARY,
  so WEAPON_SHOTGUN_MURDER_SLAP is unreachable — now tracked as `weapon-shotgun.notify.killmessage`.
- Crosshair attribs (`gfx/crosshairshotgun` size 0.65) — still not audited (client-side, out of scope here).
- Is the gauss-2D spread distribution gap perceptible at `spread 0.12` over 12 pellets, or does the
  uniform-sphere style produce a close-enough cone? Needs an in-game pellet-pattern comparison.
