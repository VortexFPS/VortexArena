# Blood loss mutator — parity spec

**Base refs:** `common/mutators/mutator/bloodloss/bloodloss.qc`, `bloodloss.qh`  ·  **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/BloodlossMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Blood loss is a server-side gameplay modifier: while a player's health is at or below the
`g_bloodloss` threshold (and not dead, and the match is live), the player "bleeds out" — they take
periodic 1-hp rot damage on a randomized ~0.5–1.0 s cadence, are forced into a permanent crouch, and
cannot jump. The bleeding continues until the player either dies or heals back above the threshold.
The cvar value `g_bloodloss` is simultaneously the enable switch (0 = off) AND the health threshold.
It is configured via the multiplayer-create mutators dialog (a slider over 10–50 plus an enable
checkbox). The mutator is mutually exclusive with InstaGib in the menu (greyed out when `g_instagib`).

## Base algorithm (authoritative)

### Registration / enable  (`bloodloss.qc:5`, `stats.qh:330-332`)
- **SVQC:** `REGISTER_MUTATOR(bloodloss, autocvar_g_bloodloss)` — enabled when the cvar is truthy
  (non-zero). Default `g_bloodloss 0` (`xonotic-server.cfg:304`, "amount of health below which blood
  loss occurs").
- **CSQC:** `REGISTER_MUTATOR(bloodloss, true)` — always registered client-side; the per-hook check
  reads `STAT(BLOODLOSS)`.
- `REGISTER_STAT(BLOODLOSS, FLOAT, autocvar_g_bloodloss)` networks the threshold to the client so
  CSQC prediction of crouch/jump uses the same value.
- `MENUQC`: `MutatorBloodLoss` registered with message "Blood loss".

### Health-rot tick  (`bloodloss.qc:13-29`, SVQC `MUTATOR_HOOKFUNCTION(bloodloss, PlayerPreThink)`)
- **Trigger / entry:** SVQC `PlayerPreThink`, fired once per frame for every player.
- **Algorithm:**
  ```
  if (game_stopped) return;                 // intermission → health reports strange values, don't damage
  if (IS_PLAYER(player)
      && GetResource(player, RES_HEALTH) <= autocvar_g_bloodloss
      && !IS_DEAD(player)
      && time >= player.bloodloss_timer)
  {
      if (player.vehicle)
          vehicles_exit(player.vehicle, VHEF_RELEASE);   // boots the player out of any vehicle, each tick
      if (player.event_damage)
          player.event_damage(player, player, player, 1, DEATH_ROT.m_id, DMG_NOWEP, player.origin, '0 0 0');
      player.bloodloss_timer = time + 0.5 + random() * 0.5;  // next tick in 0.5–1.0 s
  }
  ```
- **Constants:** damage `1` hp **requested** per tick — but because the hit is self-damage
  (`targ == attacker == player`), `Damage()` multiplies it by `autocvar_g_balance_selfdamagepercent`
  (0.65), so the **effective** drain is ~`0.65` hp/tick (`server/damage.qc:614-615`). The port applies
  the identical 0.65 self-damage cut (`DamageSystem.cs:225-226`), so both match. Deathtype `DEATH_ROT`
  (registered in `deathtypes/all.inc:29` as `DEATH_SELF_ROT`, self-inflicted, no weapon, no obituary
  string — passed with `DMG_NOWEP`; a "special"/non-weapon deathtype so the global weapon damage/force
  factors are skipped, but armor is NOT bypassed); next-tick interval `0.5 + random()*0.5` s (uniform
  in [0.5, 1.0]).
- **State:** per-player `.float bloodloss_timer` gates the cadence. Damage is self→self→self
  (target = inflictor = attacker = player), force `'0 0 0'`.
- **Edge cases:** skipped during `game_stopped` (intermission); skipped for dead players; the vehicle
  eject is `VHEF_RELEASE` (the QC has a TODO noting it boots the player every rot tick).

### Forced crouch  (`bloodloss.qc:31-36` SVQC, `58-62` CSQC, `MUTATOR_HOOKFUNCTION(PlayerCanCrouch)`)
- **Trigger / entry:** `common/physics/player.qc:203` `MUTATOR_CALLHOOK(PlayerCanCrouch, this, do_crouch)`,
  run during `PlayerPreThink`/crouch update. The call site has already forced `do_crouch=false` for
  vehicles / frozen / dead players BEFORE the hook (player.qc:198-201), then reads the hook's value back.
- **Algorithm (SVQC):** `if (GetResource(player, RES_HEALTH) <= autocvar_g_bloodloss) do_crouch = true;`
- **Algorithm (CSQC):** `if (STAT(HEALTH) > 0 && STAT(HEALTH) <= STAT(BLOODLOSS)) do_crouch = true;`
  — the CSQC variant additionally guards `HEALTH > 0` (so a corpse/respawning client isn't forced to crouch).
- **Effect:** the player is locked into the crouched hull/eye height while below threshold.

### Jump block  (`bloodloss.qc:38-44` SVQC, `63-67` CSQC, `MUTATOR_HOOKFUNCTION(PlayerJump)`)
- **Trigger / entry:** `common/physics/player.qc:403` `if (MUTATOR_CALLHOOK(PlayerJump, this, mjumpheight, doublejump)) return true;`
  — a `true` return from any hook handler aborts the jump entirely.
- **Algorithm (SVQC):** `if (GetResource(player, RES_HEALTH) <= autocvar_g_bloodloss) return true;`
- **Algorithm (CSQC):** `if (STAT(HEALTH) > 0 && STAT(HEALTH) <= STAT(BLOODLOSS)) return true;`
- **Effect:** the player cannot jump while below threshold.

### Active-mutator signalling  (`bloodloss.qc:46-54` SVQC, `util.qc:309`, CSQC `client.qc`)
- `BuildMutatorsString` appends `":bloodloss"`; `BuildMutatorsPrettyString` appends `", Blood loss"`
  (SVQC, serverinfo).
- `common/util.qc:309` adds the row `X("Blood loss", _("Blood loss"), MUT_BLOODLOSS, cvar("g_bloodloss") > 0)`
  to the active-mutators string built for the scoreboard.
- CSQC `client.qc` calls `mut_set_active(MUT_BLOODLOSS)` when `g_bloodloss > 0`, lighting the
  active-mutator HUD icon.
- All three are keyed on `g_bloodloss > 0` (slightly stricter than the mutator's own non-zero enable).
  The port implements **none** of them (no `mut_set_active` / `MUT_BLOODLOSS` / `BuildMutatorsString`
  equivalent anywhere in `src/` or `game/`). Cosmetic only.

### Menu  (`menu/.../dialog_multiplayer_create_mutators.qc:103-109`, MENUQC)
- Slider `makeXonoticSlider_T(10, 50, 1, "g_bloodloss", ...)` + `makeXonoticSliderCheckBox(0, 1, s, "Blood loss")`;
  `setDependent(e, "g_instagib", 0, 0)` greys it out under InstaGib. The describe paragraph
  (`bloodloss.qc:72-79`) explains stun + impaired movement + rapid health drain.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| Registration / enable | `BloodlossMutator` (`[Mutator]`), `IsEnabled => Cvars.GetFloat("g_bloodloss") != 0f` | `[Mutator]`-tagged → in `Mutators.All`; activated by `MutatorActivation.Apply()` at `GameWorld.cs:511` on server boot (LIVE). |
| Health-rot tick | `BloodlossMutator.OnPlayerPreThink` (subscribed to `MutatorHooks.PlayerPreThink`) | Fired live from `GameWorld.OnClientMove`/`PlayerPreThink.Call` at `GameWorld.cs:988`. `game_stopped` → `VehicleCommon.GameStopped`; `IS_DEAD` → `DeadState == DeadFlag.No`; vehicle eject → `VehicleCommon.ExitVehicle(..., VehicleExitFlag.Release)`; damage → `Combat.Damage(player, player, player, 1f, "rot", origin, Vector3.Zero)`; timer → `BloodlossTimer = now + 0.5f + Prandom.Float()*0.5f`. |
| Forced crouch | `BloodlossMutator.OnPlayerCanCrouch` (`MutatorHooks.PlayerCanCrouch`) | Fired live from `PlayerPhysics.UpdateCrouch` / `PlayerCanCrouch.Call` at `PlayerPhysics.cs:1012`. Sets `args.DoCrouch = true` when `Health <= Threshold`. |
| Jump block | `BloodlossMutator.OnPlayerJump` (`MutatorHooks.PlayerJump`) | Fired live from `PlayerPhysics.PlayerJump` / `PlayerJump.Call` at `PlayerPhysics.cs:820`. Returns `true` (forbid) when `Health <= Threshold`. |
| `bloodloss_timer` | `Entity.BloodlossTimer` (`EntityMutatorState.cs:38`) | Per-entity field. |
| `DEATH_ROT` deathtype | string `"rot"` passed to `Combat.Damage` | Free-form deathtype string; self-damage with no weapon, so it just removes 1 hp. |
| Active-mutator signalling | NOT IMPLEMENTED | No port equivalent of `BuildMutatorsString`/`BuildMutatorsPrettyString`, the `util.qc` `MUT_BLOODLOSS` row, or CSQC `mut_set_active(MUT_BLOODLOSS)`. |
| Menu slider/checkbox | `DialogMutators.cs:147-157` (`g_bloodloss`, slider 10–50, checkbox off=0/saved=20, dependent on `g_instagib`) | Faithful UI. |
| MENUQC describe paragraph | partial — tooltip only (`DialogMutators.cs:149-151`) | The long describe text isn't reproduced. |

## Parity assessment

**logic** — Faithful and live. All three hooks (rot tick, forced crouch, jump block) match the SVQC
control flow, including the `game_stopped` guard, the `IS_DEAD` guard, and the vehicle eject on each
rot tick. The damage call is self→self→self with amount 1 and force zero, matching Base.

**values** — Faithful. Damage 1 hp; cadence `0.5 + random()*0.5` s; threshold = the `g_bloodloss`
cvar value; menu slider range 10–50. Base default `g_bloodloss 0` (disabled). One port nuance: the
port caches `Threshold` once at `Hook()` time and only updates it when the cvar is non-zero
(falling back to a `25f` field default if the cvar read is 0). Base re-reads `autocvar_g_bloodloss`
live on every hook call. In normal play `Apply()` runs after the config is loaded so the cached
value is correct, but a mid-match `g_bloodloss` change (without re-running `Apply()`) would not be
picked up — a minor, rarely-observable divergence.

**timing** — Faithful. The rot cadence uses the same `0.5 + rand*0.5` formula via the deterministic
`Prandom.Float()`. The hooks run on the per-frame `PlayerPreThink`/physics path at the same point in
the step as Base.

**presentation** — Partial. The gameplay effect (forced crouch posture, no-jump, draining health) is
visible because the authoritative state is correct. BUT all active-mutator signalling is missing: the
serverinfo/scoreboard active-mutators string (`:bloodloss` / `, Blood loss`), the `util.qc`
`MUT_BLOODLOSS` row, and the CSQC `mut_set_active(MUT_BLOODLOSS)` HUD icon all have no port equivalent.
The MENUQC describe paragraph is also reduced to a tooltip. There is no dedicated bloodloss screen
effect in Base, so nothing else is owed.

**client prediction** — Base registers `REGISTER_MUTATOR(bloodloss, true)` on CSQC and networks
`STAT(BLOODLOSS)` so the client predicts the forced crouch / jump block (with an extra `STAT(HEALTH)>0`
guard). The port runs a single headless server sim with no CSQC prediction, so crouch/jump are
server-authoritative only — behaviourally faithful to SVQC, but not locally predicted (subject to
server-round-trip latency). Acceptable per the port's architecture; recorded as a gap, not a bug.

**audio** — `na`. Base bloodloss has no sound API calls; the rot damage uses the standard damage
pipeline (whatever pain/hurt sound that already plays). Nothing bloodloss-specific to port.

**liveness** — `live`. `BloodlossMutator` is `[Mutator]`-tagged, included in `Mutators.All`, and
`MutatorActivation.Apply()` (the QC `STATIC_INIT_LATE(Mutators)` successor) is invoked on the real
server boot path at `GameWorld.cs:511`. All three hook chains it subscribes have verified live call
sites (`GameWorld.cs:988`, `PlayerPhysics.cs:820`, `PlayerPhysics.cs:1012`). When `g_bloodloss != 0`
the mutator's `Hook()` runs and its handlers are on the live chains.

### Gaps (observable)
- All active-mutator signalling is unported: the serverinfo string (`:bloodloss`), the scoreboard
  pretty string (`, Blood loss`), the `util.qc` `MUT_BLOODLOSS` row, and the CSQC
  `mut_set_active(MUT_BLOODLOSS)` HUD icon — the port has no active-mutator mechanism at all.
- CSQC client prediction of crouch/jump (and `STAT(BLOODLOSS)`) is absent — server-authoritative only.
- Mid-match `g_bloodloss` cvar change is not re-read by the running mutator (cached threshold).
- The full menu describe paragraph is not shown (tooltip only).
- (Behavioral) No runtime/automated test confirms the rot tick, forced crouch, and jump block in a
  live match — wiring verified by static + cross-file trace only.

### Intended divergences
None identified. The threshold-caching behavior is not documented as deliberate; treated as a minor gap.

## Verification
- Base source read in full: `bloodloss.qc`, `bloodloss.qh`, `stats.qh:330-332`, `deathtypes/all.inc:29`,
  `xonotic-server.cfg:304`, `physics/player.qc:198-210` & `:403-404`,
  `menu/.../dialog_multiplayer_create_mutators.qc:103-109`.
- Port wiring traced: `BloodlossMutator.cs` (handlers) → `MutatorHooks.cs` (chains) → live callers at
  `GameWorld.cs:988` / `PlayerPhysics.cs:820` / `PlayerPhysics.cs:1012`; activation at
  `GameWorld.cs:511` via `MutatorActivation.Apply()`; `[Mutator]` registration into `Mutators.All`.
- `Combat.Damage` signature confirmed `(target, inflictor, attacker, amount, deathType, hitLoc, force)`
  in `DamageContracts.cs:54` — argument order matches the port call.
- `VehicleCommon.GameStopped` and `ExitVehicle` confirmed real in `VehicleCommon.cs:165,230`.
- No bloodloss-specific unit test exists (grep of `tests/` — only compiled DLL hits). Behavioral
  parity is therefore unverified at runtime.

## Open questions
- Should the port re-read `g_bloodloss` live (per-frame) instead of caching at `Hook()` to match Base
  exactly, or is a `MutatorActivation.Apply()` re-run on cvar change the intended convergence path?
- Is the serverinfo active-mutators string (`BuildMutatorsString`) implemented anywhere generically
  in the port, or universally absent across mutators? (Not found for bloodloss.)
