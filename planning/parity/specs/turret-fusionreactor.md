# Fusion Reactor turret — parity spec

**Base refs:** `common/turrets/turret/fusionreactor.qc` · `common/turrets/turret/fusionreactor.qh` · shared framework `common/turrets/sv_turrets.qc`, `common/turrets/cl_turrets.qc`, `common/turrets/turret.qh` · balance `turrets.cfg` (`g_turrets_unit_fusreac_*`)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/FusionReactorTurret.cs` · framework `TurretAI.cs`, `TurretSpawn.cs`, `TurretSpawnFuncs.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Fusion Reactor (`turret_fusionreactor`, netname `fusreac`) is the only SUPPORT turret. It carries no weapon and never
attacks players or missiles. Each think it acts as a power supply: it loops over every *same-team* turret in range that
uses energy ammo and isn't already full, and tops up each one's ammo pool by `shot_dmg` (capped at the recipient's
`ammo_max`), spending its own ammo per recipient. This lets the offensive turrets it powers fire more often than their own
recharge allows. It is a hand-placed map entity (`spawnfunc(turret_fusionreactor)`), spawned only when `g_turrets != 0`,
and is purely server-authority gameplay plus a small client presentation layer (model, slowly spinning head whose spin rate
scales with its ammo fraction, a `te_smallflash` at each recipient, plus the generic turret damage/death/respawn FX).

## Base algorithm (authoritative)

### Identity / model / hitbox / class flags  (`fusionreactor.qh:FusionReactor`)
- **Side:** shared (GAMEQC ATTRIBs) + authority spawn.
- `spawnflags = TUR_FLAG_SUPPORT | TUR_FLAG_AMMOSOURCE` — marks it a support/ammo-source unit; `turret_initialize`
  overwrites `this.turret_flags = TUR_FLAG_ISTURRET | tur.spawnflags` and, because TUR_FLAG_SUPPORT is set, assigns the
  scoring function `turret_score_target = turret_targetscore_support` (rather than `_generic`).
- `m_mins = '-34 -34 0'`, `m_maxs = '34 34 90'` (hitbox).
- `mdl = "base.md3"` → `model = "models/turrets/base.md3"`; `head_model = "models/turrets/reactor.md3"`.
- `netname = "fusreac"`, `m_name = "Fusion Reactor"`.

### Setup  (`fusionreactor.qc:tr_setup`)  — side: shared/authority, called from `turret_initialize` and `turret_respawn`
- `ammo_flags = TFL_AMMO_ENERGY | TFL_AMMO_RECHARGE` (it has energy + regenerates).
- `target_select_flags = TFL_TARGETSELECT_TEAMCHECK | TFL_TARGETSELECT_OWNTEAM | TFL_TARGETSELECT_RANGELIMITS`
  (only same-team, range-limited; no LOS, no angle limits).
- `firecheck_flags = TFL_FIRECHECK_AMMO_OWN | TFL_FIRECHECK_AMMO_OTHER | TFL_FIRECHECK_DISTANCES | TFL_FIRECHECK_DEAD`.
- `shoot_flags = TFL_SHOOT_HITALLVALID` — the think loops over *every* valid target and "fires" at each (the support sweep).
- `aim_flags = TFL_AIM_NO`, `track_flags = TFL_TRACK_NO` — no aiming, no head tracking.
- `tur_head.scale = 0.75`; `tur_head.avelocity = '0 50 0'` — head starts spinning at 50 deg/s yaw at setup.
- `turret_firecheckfunc = turret_fusionreactor_firecheck` (overrides the generic firecheck).

### Per-think recharge sweep  (`sv_turrets.qc:turret_think`, the `TFL_SHOOT_HITALLVALID` branch)
- **Trigger:** every server frame; `turret_link` sets `setthink(turret_think)` with `nextthink = time`, re-armed each think.
- **Ammo regen (all turrets):** `if (!(spawnflags & TSF_NO_AMMO_REGEN) && ammo < ammo_max) ammo = min(ammo + ammo_recharge*frametime, ammo_max)`.
- **Inactive bail:** `if (!active) { turret_track(this); return; }` — a team-gated-off reactor only runs the head track (no-op here since TFL_TRACK_NO) and supplies no power.
- **Support sweep:** `for (e = findradius(origin, target_range); e; e = e.chain) if (e.takedamage) if (turret_validate_target(this, e, target_validate_flags)) { this.enemy = e; turret_do_updates(this); if (turret_checkfire(this)) turret_fire(this); }` then `this.enemy = NULL`.
  - NOTE: the loop guard `if (turret_validate_target(...))` is truthy for ANY non-negative-zero return; the real validity gate is enforced inside `turret_fusionreactor_firecheck` (next).
- `turret_fire` (`sv_turrets.qc`): calls `info.tr_attack(info, this)`, then `attack_finished_single[0] = time + shot_refire`, `ammo -= shot_dmg`, `--volly_counter`. shot_volly is 1 (clamped), so the volley reset path is benign.

### Firecheck  (`fusionreactor.qc:turret_fusionreactor_firecheck`)  — side: authority
Per candidate `targ = this.enemy`:
1. Mutator hook `FusionReactor_ValidTarget(this, targ)` — if it returns `MUT_FUSREAC_TARG_VALID` → accept; `MUT_FUSREAC_TARG_INVALID` → reject; otherwise fall through.
2. Reject unless ALL hold:
   - `attack_finished_single[0] <= time` (refire ready),
   - `targ` exists, `this.team == targ.team` (same team),
   - `!IS_DEAD(targ)`,
   - `this.ammo >= this.shot_dmg` (own ammo covers a top-up),
   - `targ.ammo < targ.ammo_max` (recipient not already full),
   - `vdist(targ.origin - this.origin, <=, target_range)` (in range),
   - `targ.ammo_flags & TFL_AMMO_ENERGY` (recipient uses energy ammo).

### Attack  (`fusionreactor.qc:tr_attack`)  — side: authority + presentation
- `it.enemy.ammo = min(it.enemy.ammo + it.shot_dmg, it.enemy.ammo_max)` — top the recipient up.
- `fl_org = 0.5*(enemy.absmin + enemy.absmax)` then `te_smallflash(fl_org)` — a small flash effect at the recipient's center (presentation).

### Head spin  (`fusionreactor.qc:tr_think`)  — side: authority, networked to client
- `it.tur_head.avelocity = '0 250 0' * (it.ammo / it.ammo_max)` every think: the head's yaw angular velocity scales with how
  full the reactor's own ammo pool is (full = 250 deg/s, empty = 0). `tr_think` is called at the END of `turret_think` for
  every turret. The avelocity is networked (`TNSF_AVEL`) and the client integrates head angle each render frame
  (`cl_turrets.qc:turret_draw`: `tur_head.angles += dt * tur_head.avelocity`).

### Constants (Base defaults, from `turrets.cfg` `g_turrets_unit_fusreac_*` + `turret_initparams` fallbacks)
- `health = 700`, `respawntime = 90`.
- `shot_dmg = 20` (ammo given per recipient; own ammo spent per recipient).
- `shot_refire = 0.2`.
- `shot_speed = 1` (unused — no projectile).
- `target_range = 1024`, `target_range_min = 1`.
- `ammo_max = 100`, `ammo_recharge = 100` (per second).
- `shot_radius = 0`, `shot_spread = 0`, `shot_force = 0`, `shot_volly = 0`, `shot_volly_refire = 0` (all unused / clamped).
- Global turret cvars: `g_turrets = 1` (master switch), `g_turrets_nofire 0`, `g_turrets_targetscan_mindelay 0.1`,
  `g_turrets_targetscan_maxdelay 1`, `g_turrets_aimidle_delay 5` (the scan-delay/aimidle cvars are not used by the
  HITALLVALID sweep — it runs every frame).

### State / networking
- Turret edict is `Net_LinkEntity`'d with `turret_send`. SendFlags carry SETUP (id/origin/angles), AVEL (head avelocity),
  STATUS (team + scaled health), ANG, ANIM. The reactor uses SETUP + AVEL + STATUS. Client `cl_turrets.qc` builds the model
  (`turret_construct`), spins the head from the networked avelocity, draws a waypoint sprite + healthbar (`turret_draw2d`),
  and on health→0 plays the death FX + base/head gibs (`turret_die`).

### Edge cases
- Team gating: turrets default to `team = FLOAT_MAX` if teamless/non-teamplay so SAME_TEAM works; `turret_use` re-teams +
  toggles active on trigger.
- Death/respawn: generic `turret_damage`/`turret_die`/`turret_respawn`. With respawn (default), it hides, waits
  `respawntime`, then `turret_respawn` restores full health/ammo, re-runs `tr_setup` (re-arming the 50 deg/s head spin and
  ammo flags). `TSL_NO_RESPAWN`/`TFL_DMG_DEATH_NORESPAWN` would make death permanent (not set by default).
- Friendly fire on the reactor itself follows `g_friendlyfire` (default 0 → teammates can't damage it).

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| identity/model/bbox | `FusionReactorTurret` ctor + `Spawn` (`new Vector3(-34,-34,0)/(34,34,90)`) | model `models/turrets/base.md3`, head_model NOT carried |
| `tr_setup` flags | folded into `FusionReactorTurret` consts (`Select` = TeamCheck|OwnTeam|RangeLimits) | head scale 0.75 / initial 50°/s spin NOT ported (no head entity) |
| ammo regen | `Think` (ammo += AmmoRecharge*frameTime, capped) | faithful |
| inactive bail | `Think` (`if (!st.Active) return;`) | faithful (no head track needed) |
| HITALLVALID sweep | `Think` `foreach (Api.Entities.FindInRadius(...))` | faithful loop |
| `turret_fusionreactor_firecheck` | `Think` inline checks + `IsRechargeableAlly` + `ValidTarget` | see gaps: refire-ready and EXISTENCE-OF-TFL_AMMO_ENERGY checks differ |
| `tr_attack` (top-up) | `Think` `allyState.Ammo = min(... + ShotDamage, AmmoMax); st.Ammo -= ShotDamage` | faithful |
| `tr_attack` `te_smallflash` | NOT IMPLEMENTED (comment only) | presentation gap |
| `tr_think` head spin (`'0 250 0'*ammo/ammo_max`) | NOT IMPLEMENTED | presentation gap |
| spawnfunc / master switch | `TurretSpawnFuncs.FusionReactor` → `Spawn(e,"fusreac")`; registered `MapObjectsRegistry` `turret_fusionreactor` | live |
| think driver | sim loop `RunThink` → `e.Think` (set in `TurretSpawnFuncs.Spawn`) | live |
| damage/use/death/respawn | `TurretAI.Use/Damage/Die/Respawn` via `TurretSpawn.Init` + shared death hook | live |
| `FusionReactor_ValidTarget` mutator hook | NOT IMPLEMENTED | no stock mutator uses it; cross-boundary |
| client networking + render (model/head/waypoint/healthbar/gibs) | NOT IMPLEMENTED (no `ENT_CLIENT_TURRET` net entity in port) | whole-family presentation gap |

## Parity assessment

### Logic — faithful (with two firecheck nuances)
The support sweep, same-team/range/energy/not-full gating, the per-recipient top-up, and own-ammo spend are all faithfully
ported. Two nuances vs `turret_fusionreactor_firecheck`:
1. **Refire-ready check placement.** Base checks `attack_finished_single[0] > time` *per recipient* inside the firecheck,
   then `turret_fire` resets it. The port checks `st.AttackFinished > now` *once at the top of Think* (early-returns the
   whole sweep), and sets `AttackFinished = now + ShotRefire` once if it gave to anyone. Net behavioural effect on the
   reactor is the same in practice (one refire window gates the whole sweep), but it is not a line-for-line match — the port
   recharges *all* eligible allies on each refire tick, whereas Base advances `attack_finished_single` after the first
   `turret_fire` in the loop and then `attack_finished_single[0] > time` rejects the remaining recipients that same frame.
   This makes the port supply ALL allies per 0.2s, Base supplies ONE ally per 0.2s. **This is a real logic/timing
   divergence** (the reactor powers its whole cluster ~N× faster than Base).
2. **Recipient `TFL_AMMO_ENERGY` check.** Base requires `targ.ammo_flags & TFL_AMMO_ENERGY`. The port's `IsRechargeableAlly`
   only checks "classname starts with turret_, same team, alive" — it does not verify the recipient uses energy ammo. In
   practice every offensive turret in the family uses energy, so this is currently harmless, but it is not faithful and would
   wrongly recharge a future non-energy turret.

### Values — faithful
All six live balance constants match `turrets.cfg`: shot_dmg 20, shot_refire 0.2, target_range 1024, target_range_min 1,
ammo_max 100, ammo_recharge 100, health 700. (respawntime: `FusionReactorTurret.Spawn` does not pass a respawnTime, so it
takes `TurretSpawn.Init`'s default `60f` rather than the cfg's 90 — see gaps.)

### Timing — partial
Ammo regen uses `frameTime` like Base. The per-refire sweep cadence diverges as described in Logic nuance #1 (all-allies vs
one-ally per refire).

### Presentation — missing
No turret renders on the port client at all: there is no `ENT_CLIENT_TURRET` networked entity, no turret model/head spawn,
no head spin (Base scales head avelocity by ammo fraction up to 250°/s), no `te_smallflash` at recipients (the port has the
`EffectEmitter.TeSmallflash` API available but the reactor only mentions it in a comment), no waypoint sprite/healthbar, and
no death/gib FX. A player would see nothing where the reactor stands.

### Audio — na
The reactor's own logic emits no sound (the generic turret death sound `SND_ROCKET_IMPACT` is part of the shared
death/gib FX, which is itself unported on the client). No audio cue is specific to this unit.

### Liveness — live (gameplay) / dead (presentation)
The recharge gameplay is live: `turret_fusionreactor` is registered in `MapObjectsRegistry`, the map-entity spawn loop
(`GameWorld.SpawnMapEntities` → `SpawnFuncs.TrySpawn`) dispatches it, `TurretSpawnFuncs.Spawn` sets `e.Think`/`e.NextThink`,
and the sim loop's `RunThink` (SV_RunThink port) invokes the think every frame. So on a stock map with a hand-placed reactor
(when `g_turrets 1`), it really runs and supplies power. The presentation half has no code to be live.

### Intended divergences
None recorded for this unit. The missing client render is a known, family-wide port gap (turrets are server-only so far),
not a deliberate design change — flagged as gaps, not intended_divergence.

## Verification
- **code-read** — `fusionreactor.qc`/`.qh` + `sv_turrets.qc` HITALLVALID branch vs `FusionReactorTurret.cs` + `TurretAI`/
  `TurretSpawn`/`TurretSpawnFuncs`: recharge logic + constants confirmed; firecheck nuances confirmed by reading both.
- **liveness trace** — `MapObjectsRegistry.cs:216` registers `turret_fusionreactor`; `TurretSpawnFuncs.Spawn` wires
  `e.Think`; `SimulationLoop.RunThink` invokes it. Confirmed live by reading the chain.
- **tests** — `tests/VortexArena.Tests/TurretLifecycleTests.cs` covers spawn/use/damage/die/respawn but ONLY for EWheel;
  there is NO test exercising the FusionReactor recharge sweep, its firecheck, or the head spin. Recharge correctness is
  therefore unverified at runtime.
- **presentation** — unverified by observation; absence of any turret net entity / client draw inferred from grep across
  `src/**` and `game/**` (no `ENT_CLIENT_TURRET`, no turret model spawn).

## Open questions
- Is the all-allies-per-refire vs one-ally-per-refire divergence (Logic #1) intended, or should the port advance the refire
  per recipient like Base? It materially changes how fast a reactor tops up a multi-turret cluster.
- Will the turret family ever get client networking/rendering? If not, the whole presentation block stays `missing` for every
  turret and could be marked intended once a decision is recorded.
