# Multijump mutator — parity spec

**Base refs:** `common/mutators/mutator/multijump/multijump.qc` · `common/mutators/mutator/multijump/multijump.qh` · `common/stats.qh` (stats) · `mutators.cfg` (cvar defaults)
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/MultijumpMutator.cs` · `src/VortexArena.Common/Physics/PlayerPhysics.cs` (PlayerJump/PlayerPhysics hosts) · `src/VortexArena.Common/Gameplay/Mutators/EntityMutatorState.cs` (MultijumpCount/MultijumpReady fields)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Multijump lets a player jump again while in midair, up to a configured number of extra jumps (`g_multijump`,
`-1` = infinite). It is a *physics* mutator: it hooks `PlayerPhysics` (to reset the per-air-time jump counter on
landing) and `PlayerJump` (to grant the extra midair jump). It runs on both the authoritative server and the
CSQC client (shared physics) so prediction stays in sync. Activation is gated by `g_multijump != 0`. Each
player can opt in/out/cap via the replicated client cvar `cl_multijump`.

## Base algorithm (authoritative)

### Activation / registration  (`multijump.qc:13-17`)
- SVQC: `REGISTER_MUTATOR(multijump, autocvar_g_multijump)` — enabled iff `g_multijump != 0`.
- CSQC: `REGISTER_MUTATOR(multijump, true)` — always registered on the client so it can run the shared physics
  hooks against the networked `MULTIJUMP*` stats.
- The mutator's behaviour reads everything through `STAT(...)` macros (`PHYS_MULTIJUMP`, `_SPEED`, `_ADD`,
  `_MAXSPEED`, `_DODGING`, `_COUNT`, `_CLIENT`), so the server's cvars are replicated to the client as stats.

### Ground reset of the jump counter  (`multijump.qc:36-47`, hook `PlayerPhysics`)
- CSQC only: copies the networked stat into the local field — `player.multijump_count = PHYS_MULTIJUMP_COUNT(player)`.
- `if(!PHYS_MULTIJUMP(player)) return;` — inert when disabled.
- `if (IS_ONGROUND(player)) player.multijump_count = 0;` — landing clears the extra-jump tally.

### Midair re-jump grant  (`multijump.qc:49-117`, hook `PlayerJump`)
Hook signature: `M_ARGV(0)=player`, `M_ARGV(1,float)=mjumpheight` (in/out), `M_ARGV(2,bool)=doublejump`
(in/out — the "this counts as an air jump" flag the core `PlayerJump` honors to bypass its airborne early-out).
Algorithm:
1. `if(!PHYS_MULTIJUMP(player)) return;` — inert when disabled.
2. **Per-client gate:** `client_multijump = PHYS_MULTIJUMP_CLIENT(player)` — this is the per-client replicated
   field `cvar_cl_multijump` (SVQC `CS_CVAR(s).cvar_cl_multijump`; CSQC `autocvar_cl_multijump`), default `-1`.
   If `client_multijump == -1` → `client_multijump = PHYS_MULTIJUMP_CLIENTDEFAULT(player)` — this is a *different*
   value, the `g_multijump_client` server default, i.e. `STAT(MULTIJUMP_CLIENT)` (BOOL, default 1).
   If `client_multijump > 1` → `return; // nope` (disable for this client). NOTE: the two symbols are distinct —
   `cl_multijump` (replicated per-client) vs `g_multijump_client` (server default). Base also has a harmless
   type quirk: `cvar_cl_multijump` is declared `int` (`REPLICATE_INIT(int, ...)` in the `.qh`) but `REPLICATE`d
   as `bool` in the `.qc`.
3. **Ready gate (press/release debounce):**
   `if (!IS_JUMP_HELD(player) && !IS_ONGROUND(player) && client_multijump) player.multijump_ready = true; else = false;`
   i.e. the jump button must have been *released* (not held) while airborne, and the client value must be truthy.
4. **Grant condition:** `!doublejump && multijump_ready && (multijump_count < g_multijump || g_multijump == -1)
   && velocity_z > g_multijump_speed && (g_multijump_maxspeed == 0 || vdist(velocity, <=, g_multijump_maxspeed))`.
5. **On grant:**
   - If `g_multijump_add == 0` (set-mode): only if `velocity_z < jumpvelocity`, set `doublejump = true` and
     `velocity_z = 0` (the core `PlayerJump` then adds `mjumpheight`, so z becomes exactly jumpvelocity).
   - Else (add-mode): `doublejump = true` unconditionally (the core then *adds* jumpvelocity on top of current z).
   - If granted and `g_multijump_dodging` and the player is pressing a movement key
     (`movement_x != 0 || movement_y != 0`): redirect horizontal velocity toward the wish direction at the same
     speed — `curspeed = vlen(vec2(velocity))`; `makevectors(v_angle_y * '0 1 0')` (yaw only);
     `wishvel = v_forward*movement_x + v_right*movement_y`; `wishdir = normalize(wishvel)`;
     `velocity_x = wishdir_x*curspeed; velocity_y = wishdir_y*curspeed;` (z kept). (SVQC has a commented-out
     antilag-averaged topspeed branch — disabled in shipping Base; the live path uses plain `vlen(vec2(velocity))`.)
   - If `g_multijump > 0`: `++multijump_count`.
   - Always: `multijump_ready = false` (require release+press again for the next jump).

### Mutators string  (`multijump.qc:119-131`, SVQC only)
- `BuildMutatorsString` → append `:multijump`; `BuildMutatorsPrettyString` → append `, Multi jump`.

### Constants / cvars (defaults from `mutators.cfg`)
| cvar | default | units | side | meaning |
|---|---|---|---|---|
| `g_multijump` | `0` | count | sv (authority) | extra jumps allowed; `-1` = infinite; `0` = off |
| `g_multijump_client` | `1` | count | sv (authority) | client default when `cl_multijump == -1` |
| `g_multijump_add` | `0` | bool | sv (authority) | 0 = set z to jumpvel, 1 = add jumpvel |
| `g_multijump_speed` | `-999999` | qu/s | sv (authority) | min `velocity_z` to allow a re-jump |
| `g_multijump_maxspeed` | `0` | qu/s | sv (authority) | if >0, max **3D** speed (`vdist(velocity,...)`) for a re-jump (0 = no cap). Port compares 2D xy only — a divergence. |
| `g_multijump_dodging` | `1` | int (truthy; STAT INT, cvar float) | sv (authority) | redirect horizontal velocity to wish dir on re-jump |
| `cl_multijump` | `-1` | count | cl (presentation/replicated `cvar_cl_multijump`) | per-client opt-in/out/cap; `-1` = use `g_multijump_client`; `>1` = off for me |

Stat types (for reference): `MULTIJUMP` INT, `MULTIJUMP_COUNT` INT, `MULTIJUMP_ADD` INT, `MULTIJUMP_DODGING` INT,
`MULTIJUMP_SPEED` FLOAT, `MULTIJUMP_MAXSPEED` FLOAT, `MULTIJUMP_CLIENT` BOOL (= `g_multijump_client`).

### State / networking
- `.int multijump_count` and `.bool multijump_ready` are entity fields. `multijump_count` is a networked stat
  (`REGISTER_STAT(MULTIJUMP_COUNT, INT, this.multijump_count)`), so the client mirrors the server tally for
  prediction. The six tuning cvars are also stats. `cl_multijump` is replicated server-side via `REPLICATE`.

## Port mapping
- **Activation:** `MultijumpMutator.IsEnabled => Cvars.GetFloat("g_multijump") != 0f`. Subscribed on the live
  path via `MutatorActivation.Apply()` (called from `GameWorld.cs:511` at server boot). Registered through the
  `[Mutator]` attribute → `Mutators.All`. Tuning values cached in `Hook()` from the cvar store (the
  `mutators.cfg` defaults are parsed into it). **Live.**
- **Ground reset:** `OnPlayerPhysics` — `if (MaxJumps == 0) return; if (player.OnGround) player.MultijumpCount = 0;`
  Host: `PlayerPhysics.cs:188-189` calls `MutatorHooks.PlayerPhysics.Call`. **Live.**
- **Midair grant:** `OnPlayerJump` — full port of the ready-gate, count/speed/maxspeed gates, the add/set z
  branch, the dodging redirect, and the count increment. Host: `PlayerPhysics.cs:818-823` builds
  `PlayerJumpArgs(player, mjumpheight, doublejump)`, calls the chain, and writes `pj.Multijump` back into the
  core jump (`doublejump = pj.Multijump`), which at line 841 bypasses the airborne early-out exactly like Base
  `M_ARGV(2)`. **Live.**
- **`cl_multijump` per-client gate:** **NOT IMPLEMENTED.** The port reads neither `cl_multijump` nor
  `g_multijump_client`; the source comment explicitly defers it ("Per-client cl_multijump overrides are a
  client-stat concern; the server default applies here"). There is no replicated client cvar.
- **Mutators string:** NOT IMPLEMENTED in this mutator (the port's mutator-name list is handled elsewhere; not
  audited here — see `notes`).

## Parity assessment

### logic
- Ground reset, ready-gate debounce, count/speed/maxspeed gates, add-vs-set z branch, dodging redirect, count
  increment, and the `g_multijump == -1` infinite path are all **faithful** and live.
- **Gap (cl_multijump gate missing):** Base lets each client cap or opt out via `cl_multijump` and falls back to
  `g_multijump_client` when it is `-1`. Two observable defects: (a) a player who sets `cl_multijump 0` (opt out)
  or `cl_multijump 2`+ still gets multijump in the port — Base would disable it for that client (`>1 → return`);
  (b) the `client_multijump` value is also one of the truthiness factors in setting `multijump_ready`, so with
  `cl_multijump 0` Base never even arms the ready flag. The port ignores all of this and always uses the server
  setting. This is the dominant gap.

### values
- All six tuning cvars resolve from the loaded `mutators.cfg` to the exact Base defaults
  (`g_multijump_speed -999999`, `g_multijump_add 0`, `g_multijump_maxspeed 0`, `g_multijump_dodging 1`).
- `g_multijump_client 1` is parsed but **never read** (paired with the missing gate above).
- Minor: `g_multijump_dodging` is an INT stat in Base (any non-zero is truthy); the port stores it as a bool.
  Functionally equivalent for the default and all normal values.

### timing
- No mutator-owned timers. The re-jump fires on the same fixed-timestep PlayerJump tick as Base; the press/release
  debounce uses the same `JumpReleased`/`OnGround` flags. Faithful insofar as the host physics step is faithful.

### presentation / audio
- `na` — the mutator has no view/model/HUD/sound of its own. (The jump itself reuses the normal jump sound via
  the core `PlayerJump`, which is audited under physics, not here.)

### Liveness
- Live. `MultijumpMutator` is registered (`[Mutator]`), activated by `MutatorActivation.Apply()` on the server
  boot path (`GameWorld.cs:511`), and both its hooks are invoked by the live physics step
  (`PlayerPhysics.Call` at line 189, `PlayerJump.Call` at line 820). The `doublejump` out-flag is honored by the
  core jump. No dead code on the implemented features.

### Intended divergences
- None claimed by the auditor. The `cl_multijump` omission is documented in-code but is a behavioral gap, not a
  deliberate design change, so it is flagged as a gap rather than `intended_divergence`.

## Verification
- **Source read:** Base `multijump.qc/.qh`, `stats.qh`, `mutators.cfg`; port `MultijumpMutator.cs`,
  `PlayerPhysics.cs` (PlayerJump host lines 801-905, PlayerPhysics host 188-189), `MutatorActivation.cs`,
  `EntityMutatorState.cs`, `GameWorld.cs:511`.
- **Liveness traced:** `[Mutator]` → `Mutators.All` → `MutatorActivation.Apply()` (GameWorld boot) →
  `Hook()` subscribes both chains → physics step calls both chains → `PlayerJump` writes `pj.Multijump` into the
  core jump. Confirmed by reading the host.
- **Tests:** No dedicated multijump test. `MutatorBatchT51Tests.cs` exercises the sibling DoublejumpMutator via
  the same `PlayerJumpArgs` chain; `MovementParityTests.cs` notes the mutator chains run on the jump path. The
  multijump grant condition itself is **unverified by test** — behavioral check (in-game `g_multijump 2`) would
  confirm. Marked accordingly.
- **cl_multijump gap:** confirmed by grep — no `cl_multijump` or `g_multijump_client` read anywhere in the port.

## Open questions
- Does CSQC-side client prediction in the port run the same `MultijumpMutator` instance against a networked
  `MultijumpCount`, or only the server? Base syncs `multijump_count` as a stat and re-copies it in the CSQC
  PlayerPhysics hook so prediction matches. The port has a single shared physics path; whether the predicted
  client sees a correctly-synced count (vs the server-only tally) was not traced and may cause prediction
  mismatch on a re-jump. Needs a runtime/prediction check.
- Is the dodging yaw source (`ViewAngles.Y` with `Angles.Y` fallback) the exact equivalent of Base
  `v_angle_y`? Likely yes, but unverified against a live view-angle frame.
