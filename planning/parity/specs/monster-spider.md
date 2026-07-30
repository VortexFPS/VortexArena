# Spider monster — parity spec

**Base refs:** `common/monsters/monster/spider.qc` · `common/monsters/monster/spider.qh` · `common/monsters/sv_monsters.qc` (shared engine) · `monsters.cfg` (balance)
**Port refs:** `src/VortexArena.Common/Gameplay/Monsters/Spider.cs` · `MonsterAI.cs` · `MonsterFramework.cs` · `MonsterSpawnFuncs.cs` · `src/VortexArena.Engine/Simulation/SimulationLoop.cs` (think driver)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Spider is a melee+ranged ground monster (`MON_FLAG_MELEE | MON_FLAG_RANGED | MON_FLAG_RIDE`). At range it
launches a bouncing "web" plasma projectile that, on impact, applies the **Webbed** status in a small radius;
Webbed halves the victim's movement speed for `web_damagetime` seconds. Once close, the spider bites for high
melee damage. Monsters appear in the Invasion gametype, on maps with hand-placed `monster_spider` entities, via
`monster_spawner` entities, and via the `editmob`/`spawnmob` console command — all gated by `g_monsters` (default
on). Almost all spider logic actually lives in the shared monster engine (`sv_monsters.qc`); the per-type `.qc`
only customises attacks, anims, setup defaults, and pain/death flavor.

## Base algorithm (authoritative)

### Identity / registration  (`spider.qh:CLASS(Spider)`, `REGISTER_MONSTER(SPIDER)`)
- Model `spider.dpm`; hitbox `m_mins '-30 -30 -25'`, `m_maxs '30 30 30'`; netname `"spider"`, name `Spider`.
- `spawnflags = MON_FLAG_MELEE | MON_FLAG_RANGED | MON_FLAG_RIDE`.
- A dedicated weapon `SpiderAttack` (subclass of `PortoLaunch`, hidden, impulse 9) carries the attack `wr_think`.
- Status effect **Webbed** registered here: `m_hidden = true`, `m_color '0.94 0.3 1'`, `m_lifetime = 10`.

### Spawn + setup  (`spider.qc:spawnfunc(monster_spider)` → `sv_monsters.qc:Monster_Spawn` → `mr_setup`)
- `spawnfunc(monster_spider){ Monster_Spawn(this, true, MON_SPIDER); }`.
- `Monster_Spawn` (shared) stamps the edict: `FL_MONSTER`, `classname "monster"`, `MOVETYPE_STEP`, `SOLID_BBOX`,
  damage/heal/touch/use handlers, applies `STATUSEFFECT_SpawnShield` for `g_monsters_spawnshieldtime` (2s),
  sets size from `m_mins/maxs * scale`, `view_ofs = '0 0 0.35' * maxs.z`, runs `mr_setup`, drops to floor, sets
  `Monster_Think` self-scheduling at `nextthink = time`.
- `mr_setup`: `RES_HEALTH = g_monster_spider_health` if unset; `speed = speed_walk`, `speed2 = speed_run`,
  `stopspeed = speed_stop`, `damageforcescale = 0.6`, `monster_loot = "health_medium"`,
  `monster_attackfunc = M_Spider_Attack`.
- Shared setup also: `RES_HEALTH *= MONSTER_SKILLMOD`, default `target_range = g_monsters_target_range (2000)`,
  `respawntime = g_monsters_respawn_delay (20)`, `attack_range = g_monsters_attack_range (120)`,
  `monster_moveflags = WANDER`, random skin 0-4, miniboss roll.
- **Constants (monsters.cfg):** `g_monster_spider_health 180`, `_speed_walk 400`, `_speed_run 500`,
  `_speed_stop 100`, `_damageforcescale 0.6`, `_loot "health_medium"`,
  `_attack_bite_damage 35`, `_attack_bite_delay 1.5`, `_attack_web_delay 3`, `_attack_web_speed 1300`,
  `_attack_web_speed_up 150`, `_attack_web_range 800`, `_attack_web_damagetime 7`.

### Attack dispatch  (`spider.qc:M_Spider_Attack` ← `sv_monsters.qc:Monster_Attack_Check`)
- Each think `Monster_Attack_Check` runs once the per-slot cooldown passes; it tries MELEE if the enemy is within
  `attack_range`, RANGED otherwise, calling `monster_attackfunc(type, ...)`.
- `M_Spider_Attack`: MELEE → `SpiderAttack.wr_think(fire 2)`; RANGED → if enemy within `web_range (800)`,
  `wr_think(fire 1)`.

### Bite (melee)  (`spider.qc:wr_think fire&2` → `sv_monsters.qc:Monster_Attack_Melee`)
- `Monster_Attack_Melee(actor, enemy, bite_damage, random(melee|shoot) anim, attack_range, bite_delay, DEATH_MONSTER_SPIDER, dostop=true)`.
- Sets `state = MONSTER_ATTACK_MELEE` (freezes movement), plays the anim, traces forward `attack_range`, and
  `Damage(trace_ent, ..., bite_damage * MONSTER_SKILLMOD, DEATH_MONSTER_SPIDER, ...)` with outward force.
- **Constants:** damage 35, delay 1.5s.

### Web (ranged projectile)  (`spider.qc:wr_think fire&1`, `M_Spider_Attack_Web`)
- `wr_think` (NPC path): sets `spider_web_delay = time + web_delay`, plays `anim_shoot`, sets attack-finished;
  `W_SetupShot_Dir(...DEATH_MONSTER_SPIDER)`, then aims `w_shotdir` at `enemy.origin + '0 0 10'`, calls
  `M_Spider_Attack_Web`.
- `M_Spider_Attack_Web`: plays `electro_fire2` (CH_SHOTS), spawns a `plasma` entity: `MOVETYPE_BOUNCE`,
  `bouncefactor 0.3`, `bouncestop 0.05`, size `'-4..4'`, `W_SetupProjVelocity_Explicit(v_forward, v_up,
  web_speed 1300, web_speed_up 150, ...)`, `nextthink = time+5`, `PROJECTILE_MAKETRIGGER`, `takedamage = NO`,
  `damageforcescale 0`, `RES_HEALTH 500`, `MIF_SPLASH | MIF_ARC`, `bot_dodge`. Networked to clients via
  `CSQCProjectile(proj, true, PROJECTILE_ELECTRO, true)` — i.e. drawn as the electro orb.
- The projectile does **no direct impact damage**; its payload is the Webbed slow.

### Web explosion  (`spider.qc:M_Spider_Attack_Web_Explode`)
- On touch or 5s timeout: `Send_Effect(EFFECT_ELECTRO_IMPACT, origin, 0, 1)`, `RadiusDamage(... damage 0,
  edge 0, radius 25, force 25, DEATH_MONSTER_SPIDER ...)`, then `FOREACH_ENTITY_RADIUS(origin, 25, alive &&
  takedamage && health>0 && monsterdef != MON_SPIDER) → StatusEffects_apply(Webbed, it, time + web_damagetime
  (7), 0)`. Then `delete(this)`. NOTE: the trailing `0` is the `eff_flags` arg of
  `StatusEffects_apply(this, actor, eff_time, eff_flags)` (status_effects.qc:31) — NOT a strength of 0. And
  `eff_time` is the absolute end time; there is NO server-side `m_lifetime` clamp (m_lifetime is only the HUD
  progressbar scale in cl_status_effects.qc, and Webbed is `m_hidden` so it has no HUD element at all).

### Webbed slow  (`spider.qc` two `spiderweb` mutator hooks)
- **PlayerPhysics_UpdateStats:** `if Webbed active on player → STAT(MOVEVARS_HIGHSPEED, player) *= 0.5`. This is
  what slows a webbed *player*.
- **MonsterMove:** `if Webbed active on monster → run speed *= 0.5; walk speed *= 0.5`. Slows a webbed *monster*.

### Pain / death  (`spider.qc:mr_pain`, `mr_death` ← `sv_monsters.qc:Monster_Damage`, `Monster_Dead`)
- `Monster_Damage` (shared): INVINCIBLE/SpawnShield gates, `healtharmor_applydamage`, calls `mr_pain` for the
  per-type reaction, applies knockback `velocity += force * damageforcescale`, gib splashes, body-impact sound,
  death at health ≤ 0 (gib at ≤ -100).
- `mr_pain`: `setanim(random(anim_pain1|anim_pain2))`, `pain_finished = animstate_endtime`, returns the take.
- `mr_death`: `setanim(random(anim_die1|anim_die2))`.
- `Monster_Dead` (shared): corpse (`SOLID_CORPSE`, `MOVETYPE_TOSS`), drop loot, death sound, `monster_lifetime
  = time+5`, scoring, `MonsterDies` hook.

### Animation map  (`spider.qc:mr_anim`, GAMEQC)
- Maps DPM frame groups: melee=0, die1=1, die2=2, shoot=3, idle=5, pain1=7, pain2=8, walk=10, run=10.

### Think loop  (`sv_monsters.qc:Monster_Think`, `mr_think`)
- `mr_think` for the spider is a no-op returning true (no per-type logic). `Monster_Think` (shared): lifetime
  check, frozen handling, enemy re-acquire every 1s (`Monster_Enemy_Check` / `Monster_FindTarget`), then
  `Monster_Move(speed2, speed, stopspeed)` and `Monster_Attack_Check`. Self-reschedules `nextthink = time`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `CLASS(Spider)` + `REGISTER_MONSTER` | `Spider.cs` `[Monster]` | Resolved by net name "spider"; size set in `Spawn`. |
| `spawnfunc(monster_spider)` | `MonsterSpawnFuncs.Spider` → `MapObjectsRegistry:199` → `MonsterAI.SpawnFromMap` | Live via map lump, Invasion, editmob. |
| `mr_setup` | `Spider.Spawn` + `MonsterAI.Setup` | All cvar values + fallbacks match monsters.cfg. |
| `CLASS(SpiderAttack)` impulse-9 weapon (player-wielded `wr_think` `IS_PLAYER` branches) | **NOT IMPLEMENTED** | No SpiderAttack weapon in the port; only reachable via MON_FLAG_RIDE riding (absent) or dev tooling. |
| `M_Spider_Attack` MELEE | `Spider.Attack` (dist ≤ AttackRange) → `MonsterAI.MeleeAttack` | DEATH_MONSTER_SPIDER, dmg 35, delay 1.5. |
| `M_Spider_Attack` RANGED + `M_Spider_Attack_Web` | `Spider.FireWeb` → `MonsterAI.SpawnProjectile` | Bounce 0.3/0.05, speed 1300, up 150, 5s, no impact dmg. |
| `M_Spider_Attack_Web_Explode` (Webbed apply) | `Spider.FireWeb` onExplode + `MonsterFramework.Webbed` | radius 25, 7s, excludes spiders. |
| spiderweb `PlayerPhysics_UpdateStats` (player slow) | **NOT IMPLEMENTED** | The signature CC has no player effect. |
| spiderweb `MonsterMove` (monster slow) | `MonsterAI.Move:757` | Inlined `speed *= 0.5` when Webbed. |
| `EFFECT_ELECTRO_IMPACT` on explode | **NOT IMPLEMENTED** | onExplode omits the particle. |
| `CSQCProjectile PROJECTILE_ELECTRO` web visual | **NOT IMPLEMENTED** | `proj.NetName="spider"` matches no `ProjectileCatalog` key. |
| `electro_fire2` fire sound | `Spider.FireWeb:121` | Played once (QC plays twice). |
| `mr_anim` frame-group map | **NOT IMPLEMENTED** | No monster client frame playback. |
| `mr_pain` | `MonsterAI.MarkPain` (generic) | Fixed 0.34s pain window, generic Pain phase. |
| `mr_death` | `MonsterAI.MarkDead` (generic) | Generic Death phase; corpse/loot/scoring faithful. |
| `Monster_Think` brain | `Spider.Think` → `MonsterAI.RunThink`; driven by `SimulationLoop.SV_RunThink` | Live every server frame. |
| respawn/fade cycle | `MonsterAI.DeadThink` / `Respawn` | +5s corpse, 20s respawn delay. |

## Parity assessment

### Gaps (player-observable)
1. **Webbed players are NOT slowed** (`monster-spider.web.player_slow`, `liveness: na`, missing). The QC
   `spiderweb` `PlayerPhysics_UpdateStats` hook (`MOVEVARS_HIGHSPEED *= 0.5`) has no port counterpart. A player
   the spider webs runs at full speed — the spider's defining crowd-control does nothing to players. The port's
   `player.SpeedMultiplier` already exists (Speed powerup, entrap nade, buffs use it), so a spiderweb
   `PlayerPhysics` hook reading the Webbed status to set `SpeedMultiplier 0.5` would close this cleanly.
2. **Web renders with the wrong visual** (`monster-spider.attack.web_projectile`, presentation partial). QC nets the
   web as the electro orb (`PROJECTILE_ELECTRO`: ebomb model + TR_NEXUIZPLASMA trail + electro_fly loop sound). The
   port DOES network it as a projectile (ServerNet.Classify sees the Bounce movetype + owner + FL_PROJECTILE marker),
   but `ProjectileCatalogKey("monster_projectile","spider")` does not match any electro/electro_orb/spiderrocket key
   in `ProjectileCatalog.Resolve`, so it falls back to the **Generic** projectile visual — wrong model, no plasma
   trail, no electro_fly loop. (Not "invisible" — it renders, just as the generic bolt.)
3. **No EFFECT_ELECTRO_IMPACT on web pop** (`monster-spider.web.electro_impact_fx`, presentation missing).
   One-line fix in the onExplode lambda (`EffectEmitter.Emit("ELECTRO_IMPACT", p.Origin)`).
4. **No monster frame animation** (`monster-spider.anim.mr_anim`, `monster-spider.pain.mr_pain`,
   `.death.mr_death`). The server tracks a logical anim phase but no client frame-group playback consumes it for
   monsters, so the spider does not visibly walk/bite/web/pain/die-animate. This is likely a *shared* monster
   presentation gap rather than spider-specific.

### Value mismatches (small)
- ~~Webbed apply strength: QC 0 vs port 1~~ — **NOT a mismatch.** The QC `0` is the `eff_flags` arg of
  `StatusEffects_apply(this, actor, eff_time, eff_flags)`, not a strength. The port's strength 1 is irrelevant
  (Webbed has no per-strength behavior). Prior audit misread the signature.
- ~~Webbed `m_lifetime = 10` cap not modeled~~ — **NOT a server cap.** `m_lifetime` is only the client HUD
  progressbar scale (`cl_status_effects.qc:19`), and `StatusEffects_apply` applies the absolute `eff_time` with no
  clamp. Webbed is `m_hidden` (no HUD element), so `m_lifetime` is entirely moot. The port's `StatusEffectDef`
  even carries a `Lifetime` field — it's just (correctly) unused for hidden effects.
- Fire sound played once vs twice (QC fires it via both `W_SetupShot_Dir` and `M_Spider_Attack_Web`; likely an
  intentional de-dup in the port).
- Pain window 0.34s fixed vs QC's anim-length-derived `pain_finished`.
- `g_monster_spider_health` is not re-read at spawn (uses the ctor `StartHealth=180` default) — matches the
  monsters.cfg default but ignores a server override of that one cvar; the speed/loot/forcescale cvars ARE read.

### Liveness
**LIVE.** The spider is a registered `[Monster]`, reachable on three real paths: the `monster_spider` BSP
spawnfunc (`MapObjectsRegistry:199`), the Invasion gametype (`Invasion.SpawnMonsterDef` → `MonsterAI.SpawnMonster`),
and the `editmob spawn` / `spawnmob` console command (`Commands.cs:CmdEditMob`). The brain runs every server frame:
`SpawnFromMap` sets `e.Think` to re-arm `NextThink = Now` and call `Spider.Think` → `MonsterAI.RunThink`, and the
engine `SimulationLoop.SV_RunThink` (port of `sv_phys` `SV_RunThink`) fires due thinks each frame — exactly the
QC `Monster_Think` self-scheduling pattern. The web projectile, melee, Webbed application, and monster-vs-monster
slow are all on this live path.

### Intended divergences
- `monster-spider.web.monster_slow`: the monster move slow is inlined into `MonsterAI.Move` instead of a separate
  `MonsterMove` mutator-hook chain. Same 0.5 multiplier, same observable effect; the port has no monster-move hook
  bus.

## Verification
- **Code-traced (high confidence):** registration, spawnfuncs, mr_setup values vs monsters.cfg, bite, web
  projectile constants, Webbed-apply filter, monster slow, think-loop liveness via SV_RunThink, death/respawn.
- **Code-traced gaps (high confidence):** player slow missing (no Webbed read in PlayerPhysics), electro-impact
  effect missing (no Emit in onExplode), web client visual missing (netname mismatch in ProjectileCatalog).
- **Medium confidence:** pain/death anim flavor, fire-sound double-cue, anim frame map (a shared monster-render
  concern), respawn presentation (corpse fade is client-side, unverified).
- **Unverified (low):** MENUQC `describe()` tooltip surfacing in `DialogMonsterTools.cs`.
- **Unit tests present:** `MonsterTurretVehicleObituaryTests.SpiderBite_RealAttack_TagsMonsterSpider_ObituaryPicksMonsterLine`
  exercises the real `spider.Attack` melee path → `DeathTypes.MonsterSpider` → monster obituary line (corrects the
  prior "no spider-specific unit tests" note). `InvasionSpawnTests` confirms "spider" resolves in the wave roster.

## Open questions
1. Is the missing webbed-player slow a deliberate omission (monsters considered out-of-scope for player physics)
   or an oversight? It is the spider's headline mechanic against players.
2. Is any monster's DPM/IQM frame animation driven client-side, or is monster animation a blanket presentation
   gap across all five monsters? (`mr_anim` maps exist in QC for each; none appear wired in the port.)
3. Does the web projectile need its own `ProjectileCatalog` entry (electro-orb-like), or should `FireWeb` set a
   projectile netname the catalog already recognises?
