# sv-cheats — parity spec

**Base refs:** `server/cheats.qc` · `server/cheats.qh` · `common/impulses/all.qh` (CHIMPULSE block)
**Port refs:** `src/VortexArena.Server/Cheats.cs` · `src/VortexArena.Server/Commands.cs` (`CmdCheat`/`DispatchImpulse`) · `src/VortexArena.Server/ClientCommandRegistry.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The server cheat subsystem decides *whether* a player may cheat (`CheatsAllowed`) and then implements the
cheats themselves across three engine entry points: per-impulse cheats (`CheatImpulse`, fired by the
`impulse N` console command via `ImpulseCommands`), per-command cheats (`CheatCommand`, fired by `cmd <name>`
in `SV_ParseClientCommand`), and a per-frame cheat (`CheatFrame`, run every player think to drive object
dragging). Every successful cheat bumps a per-player `.cheatcount` and a global `cheatcount_total`; the
single-player campaign refuses to record a level win if `cheatcount_total != 0`. Cheats are inert unless
`sv_cheats` is non-zero (default 0); the value is **snapshotted at map init** (`gamestart_sv_cheats`) so a
mid-match `sv_cheats` change has no effect until `restart`. The unit also owns the entity-dragging subsystem
(usable as a non-cheat object mover when `sv_cheats` is off) and the `info_autoscreenshot` map entity.

## Base algorithm (authoritative)

### Gating — `CheatsAllowed` (`server/cheats.qc:CheatsAllowed`)
- **Trigger / entry:** called by `IS_CHEAT(...)` inside every cheat case (sv).
- **Algorithm:**
  1. If `!ignoredead && IS_DEAD(this)` → 0 (dead players can't cheat, except dragging which passes ignoredead).
  2. If `gamestart_sv_cheats < 2 && !IS_PLAYER(this)` → 0 (observers/spectators may only cheat at `sv_cheats>=2`).
  3. If the impulse is `CLONE_MOVING`/`CLONE_STANDING` and `this.lip < autocvar_sv_clones` → 1 (clone allowance,
     independent of `sv_cheats`; `lip` counts clones already made this life).
  4. If `this.maycheat` → 1 (per-entity override; set by some game modes/tools).
  5. If `gamestart_sv_cheats && autocvar_sv_cheats` → 1 (BOTH the snapshot AND the live cvar must be truthy).
  6. Else log the attempt via `bprintf` (`tried to use cheat ...`) and return 0.
- **Constants:** `sv_cheats` default **0** (DP engine default; not set in cfg). `sv_clones` default **0**
  (xonotic-server.cfg:466). `gamestart_sv_cheats` = `autocvar_sv_cheats` at `CheatInit`.

### Cheat counting (`server/cheats.qc` BEGIN/END_CHEAT_FUNCTION macros + `CheatInit`)
- `CheatInit()` sets `cheatcount_total = world.cheatcount` (effectively resets to 0 each map; `world.cheatcount`
  starts 0). `ADD_CHEATS(e,n)` adds to both `cheatcount_total` and `e.cheatcount`. Campaign read:
  `server/campaign.qc:219` — `if (campaign_won && cheatcount_total == 0 && !_campaign_testrun)` save progress.

### Cheat impulses — `CheatImpulse` (`server/cheats.qc:CheatImpulse`, numbers in `common/impulses/all.qh`)
- **Trigger / entry:** `ImpulseCommands` dispatches `impulse N` to the IMPULSES registry; the CHIMPULSE handlers
  set `this.impulse = n` and the cheat dispatch matches `imp` (sv).
- **Impulse map:** `SPEEDRUN_INIT=30`, `GIVE_ALL=99`, `CLONE_MOVING=140`, `SPEEDRUN=141`,
  `CLONE_STANDING=142`, `TELEPORT=143`, `R00T=148`.
- **Cases:**
  - **SPEEDRUN_INIT (30):** deploy a `personal_wp` waypoint snapshotting origin/v_angle/velocity/all resources
    (RES_ROCKETS/BULLETS/CELLS/SHELLS/FUEL/HEALTH(min 1)/ARMOR)/weapons/statuseffects/items + the four
    pause*-finished timers + teleport_time. **Not counted as a cheat itself.**
  - **CLONE_MOVING (140):** `IS_CHEAT`; makevectors(v_angle); push +300 forward; `CopyBody(this,1)`; `++lip`;
    pull −300 back; `DID_CHEAT`. Spawns a moving corpse clone.
  - **CLONE_STANDING (142):** `IS_CHEAT`; `CopyBody(this,0)`; `++lip`; `DID_CHEAT`. Static corpse clone.
  - **GIVE_ALL (99):** `IS_CHEAT`; calls `CheatCommand(this, tokenize_console("give all"))` (re-uses the give path,
    already counted there).
  - **SPEEDRUN (141):** if `!g_allow_checkpoints` it's a cheat; if a personal waypoint exists, tracebox-validate
    the spot, teleport back to it, restore all resources/weapons/items + recompute the pause timers as
    `time + saved_finished − saved_teleport_time`, `StatusEffects_copy`, `MUTATOR_CALLHOOK(AbortSpeedrun)`;
    counts as a cheat only if `!g_allow_checkpoints`. Without a waypoint, prints a taunt.
  - **TELEPORT (143):** `IS_CHEAT`; if in noclip and an `info_autoscreenshot` exists, teleport there (consume it);
    else `MoveToRandomMapLocation(...)` with content masks `SOLID|CORPSE|PLAYERCLIP` forbid /
    `SLIME|LAVA|SKY|BODY|DONOTENTER` avoid, `Q3SURFACEFLAG_SKY`, attempt count `(gamestart_sv_cheats<2)?100:100000`,
    384/384 spacing; zero velocity, fixangle; `DID_CHEAT` on success.
  - **R00T (148):** `IS_CHEAT`; RandomSelection over live enemy players (else self); `Send_Effect(ROCKET_EXPLODE)`
    + `sound(CH_SHOTS, ROCKET_IMPACT)`; `RadiusDamage(spawn, this, 1000 dmg, 0 edge, 128 radius, force 500,
    DEATH_CHEAT, DMG_NOWEP)`; logs "404 Sportsmanship not found."; `DID_CHEAT`.

### Cheat commands — `CheatCommand` (`server/cheats.qc:CheatCommand`)
- **Trigger / entry:** `SV_ParseClientCommand` routes a client `cmd <name>` here first (sv). `argv(0)` = verb.
- **god:** `IS_CHEAT`; `BITXOR FL_GODMODE`; sprint ON/OFF; `DID_CHEAT` only on the ON edge.
- **notarget:** `IS_CHEAT`; `BITXOR FL_NOTARGET`; sprint ON/OFF; `DID_CHEAT` only on ON.
- **noclip:** `IS_CHEAT`; toggle `MOVETYPE_NOCLIP`↔`MOVETYPE_WALK`; `DID_CHEAT` only on entering noclip.
- **fly:** `IS_CHEAT`; toggle `MOVETYPE_FLY`↔`MOVETYPE_WALK`; `DID_CHEAT` only on entering fly.
- **give:** `IS_CHEAT`; `GiveItems(this, 1, argc)` (the full give grammar: `all`/`allweapons`/`max N res`/
  `<weapon>`/operator-prefixed); `DID_CHEAT` if anything was granted (`got>0`).
- **usetarget `<name>`:** `IS_CHEAT`; spawn temp ent with `.target=argv(1)`, `SUB_UseTargets`, delete; `DID_CHEAT`.
- **killtarget `<name>`:** `IS_CHEAT`; spawn temp ent with `.killtarget=argv(1)`, `SUB_UseTargets`, delete; `DID_CHEAT`.
- **teleporttotarget `<name>`:** `IS_CHEAT`; spawn `cheattriggerteleport`, `teleport_findtarget`,
  `Simple_TeleportPlayer`, delete; `DID_CHEAT`.
- **pointparticles `<effect> <pos0..1> <vel x y z> <countmul>`:** `IS_CHEAT`; crosshair_trace, lerp start along
  the aim line, `Send_Effect_`; `DID_CHEAT` (argc must be 5).
- **trailparticles `<effect>`:** `IS_CHEAT`; `W_SetupShot`+traceline along aim, `__trailparticles`; `DID_CHEAT`.
- **make `<model> <mode 0|1|2>`:** `IS_CHEAT`; traceline 2048, spawn a `func_breakable` (1000 health, model,
  `rocket_explode` mdl, `EF_NOMODELFLAGS`) at the hit, optionally surface-aligned (mode 1); `DID_CHEAT` if it fits.
- **penalty `<duration> <reason>`:** `IS_CHEAT`; `race_ImposePenaltyTime`; `DID_CHEAT`.
- **drag map-editor suite:** `dragbox_spawn`/`dragpoint_spawn`/`drag_remove`/`drag_setcnt`/`drag_save`/
  `drag_saveraceent`/`drag_clear` — spawn/edit/serialise race-checkpoint marker boxes/points using
  `crosshair_trace`, MDL_MARKER, attached digit models (`DragBox_Think`), and file I/O. All `IS_CHEAT`/`DID_CHEAT`.

### Per-frame dragging — `CheatFrame` / `Drag` (`server/cheats.qc:CheatFrame`,`Drag`)
- **Trigger / entry:** run each player think (sv). If cheats active → `Drag(this,true,true)` (unlimited range,
  any entity, counts as a cheat); else `Drag(this,false,false)` (limited `g_grab_range` range, only `.draggable`
  objects, **not** counted). Holding `PHYS_INPUT_BUTTON_DRAG` while looking at an entity within
  `g_grab_range` (default **200**) picks it up; impulses 10/12/14/15/16/18/19 + 1-9 adjust drag distance/speed;
  velocity is lerped toward the target each frame; `te_lightning1` draws a beam to the held object.
- `Drag_Begin` saves the draggee's movetype/gravity, sets MOVETYPE_WALK + gravity 0.00001; `Drag_Finish`
  restores and zeroes velocity for non-physics movetypes; items snap to ground when slow.

### `info_autoscreenshot` (`server/cheats.qc:spawnfunc(info_autoscreenshot)`)
- Map entity (capped by `g_max_info_autoscreenshot`, default **3**) marking an observe/screenshot point; used by
  the TELEPORT cheat as an emergency teleport target. Pushed to `g_observepoints` if not start-solid.

### NOCHEATS build
- When compiled with `NOCHEATS`, every entry returns 0 / no-op. (Not relevant to the port, which always builds the
  cheat core.)

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `CheatsAllowed` gating (snapshot, dead, observer, maycheat, live cvar) | `Cheats.Allowed` (Cheats.cs:62) | live, faithful (minus clone allowance) |
| `CheatInit` + counting | `Cheats.Init`/`AddCheats`/`CheatCountOf` (Cheats.cs:40,50,48) | live |
| campaign progress gate on cheatcount | `Campaign.PreIntermission(cheatCount==0)` (Campaign.cs:340), fed `Cheats.CheatCountTotal` (GameWorld.cs:1931) | live, faithful |
| `cmd god/notarget/noclip/fly/give` | `Cheats.Command` (Cheats.cs:75) via `Commands.CmdCheat` (Commands.cs:1453); registered Commands.cs:532-536 + allowlisted ClientCommandRegistry.cs:92 | live |
| `cmd usetarget/killtarget` | `Cheats.Command` cases exist (Cheats.cs:111-121) but **NOT registered** (no `Register("usetarget"/"killtarget")`) and **NOT in the client allowlist** | **DEAD — unreachable from a client cmd** |
| `give` grammar | `Common.Gameplay.GiveItems.Apply` (Cheats.cs:152) | live |
| GodMode honored | `DamageSystem` FL_GODMODE block (DamageSystem.cs:426) | live |
| NoTarget honored | Monster/Turret AI target filters (MonsterAI.cs:552/652, TurretAI.cs:204) | live |
| Noclip/Fly movetype | `Player.MoveType` set; consumed by movement/damage (DamageSystem.cs:586,671) | live |
| GIVE_ALL impulse (99) | `Cheats.GiveAll` (Cheats.cs:132) | **DEAD — no caller** |
| CLONE_MOVING/STANDING (140/142), CopyBody | — | **MISSING** |
| SPEEDRUN_INIT/SPEEDRUN (30/141), personal_wp | — | **MISSING** |
| TELEPORT (143), MoveToRandomMapLocation | — | **MISSING** |
| R00T (148), radius nuke | — | **MISSING** |
| `cmd teleporttotarget` | — | **MISSING** |
| `cmd pointparticles/trailparticles/make/penalty` | — | **MISSING** |
| Drag subsystem + `cmd drag*` map editor | only `g_grab_range`/`sv_clones` cvar stubs (Cvars.cs:327,329) | **MISSING** |
| `CheatFrame` per-frame dragging | — | **MISSING** |
| `info_autoscreenshot` | — | **MISSING** |
| cheat-attempt logging (`bprintf`) | — | **MISSING** |
| `sv_clones` clone allowance branch in gating | — | **MISSING** |
| `DEATH_CHEAT` deathtype (for r00t) | `DeathTypes.cs` registers DEATH_MURDER_CHEAT for turrets but no standalone CHEAT row | partial/unused |

## Parity assessment

**Logic.** The gating decision is faithful for the common path (snapshot AND live cvar, dead-block,
observer-block-under-2, maycheat override). The five *wired* commands (`god/notarget/noclip/fly/give`) match Base
ON/OFF semantics and `DID_CHEAT`-on-edge counting exactly. `usetarget`/`killtarget` have faithful *case bodies* but
are dead (see Liveness). **Gap:** `Allowed` omits the `sv_clones` clone-allowance branch (moot until CLONE impulses
exist) and the cheat-attempt `bprintf` logging. **Campaign (CORRECTED):** the earlier draft claimed the port was
stricter than Base — that is false. Base's `CampaignPreInit` (campaign.qc:63) *also* unloads + bails the level out
when `autocvar_sv_cheats` is set ("JOLLY CHEATS AHAHAHAHAHAHAH))"), exactly like the port's load-time bailout
(Campaign.cs:218). The port is **faithful** here. Separately, the intermission `cheatcount_total==0` save-gate
(campaign.qc:219) is a *second* gate that is also present and faithful (Campaign.cs:340).

**Values.** `g_grab_range` (200, matches) and `sv_clones` (0, matches) exist as cvars but only as dead stubs — no
code reads them. `g_max_info_autoscreenshot` is also a dead stub AND has the wrong default: port **60** vs Base
**3** (xonotic-server.cfg:643). `sv_cheats` default 0 matches.

**Presentation/Audio.** The implemented gameplay-state cheats have no presentation/audio of their own (`na`). The
**missing** cheats that DO have presentation/audio — R00T (rocket-explode effect + impact sound + nuke),
pointparticles/trailparticles, `make` (breakable model), and the Drag `te_lightning1` beam — are absent entirely.

**Liveness.** Only **five** commands are genuinely live: `cmd god/notarget/noclip/fly/give` → ServerNet →
`Commands.Execute` (gated by `ClientCommandRegistry` allowlist line 92, which lists exactly those five) → `CmdCheat`
→ `Cheats.Command`, and the count flows to the live `Campaign.PreIntermission`. **`usetarget`/`killtarget` are DEAD
(CORRECTED from the draft):** their `Cheats.Command` cases exist, but the verbs are neither `Register`ed in
`Commands.cs` (only the five above are, lines 532-536) nor present in the `ClientCommandRegistry` allowlist, so no
client `cmd usetarget`/`cmd killtarget` ever routes into the switch. **`Cheats.GiveAll` is dead:** `DispatchImpulse`
(Commands.cs:1312, routing at 1324-1327) routes `impulse N` only to vehicle + weapon impulses; impulse 99 (and
30/140/141/142/143/148) is never matched, so nothing calls `GiveAll`. It is unit-tested directly
(ServerInfraTests.cs:293) but unreachable in a real match — the classic present-but-dead port failure mode.

**Intended divergences.** None declared by the port. The "Godot-free core only" scoping of trace/particle/file-I/O
cheats is a deferral, not a deliberate behavioral divergence, so it is recorded as gaps rather than
`intended_divergence`.

## Verification
- Gating + god + noclip/fly toggle + give-all: unit tests `ServerInfraTests.Cheats_GatedBySvCheats`,
  `Cheats_NoclipAndFlyToggle`, `Cheats_GiveAll_GrantsWeaponsAndResources` (tests/VortexArena.Tests/ServerInfraTests.cs:262-300) — pass.
- Command liveness: traced `cmd god` → `ClientCommandRegistry` allowlist (line 92) → `Commands.CmdCheat` →
  `Cheats.Command` (read, verified).
- GIVE_ALL deadness: `grep` for `.GiveAll(` returns only the definition; `DispatchImpulse` reviewed and routes only
  to vehicle/weapon impulses (verified).
- Campaign gate: `Campaign.PreIntermission` reviewed (`Won != 0 && cheatCount == 0`) and `Campaign.cs:218`
  start-time `sv_cheats!=0` bailout reviewed; Base `campaign.qc:219` compared (value diff confirmed).
- Missing subsystems (Drag/CheatFrame/clone/teleport/r00t/speedrun/particles/make/info_autoscreenshot): `grep`
  across `src/**` returns no implementations (only cvar stubs) — verified absent.

## Open questions
- RESOLVED: the port's "refuse to start a cheats-enabled campaign" is **not** a divergence — Base does the same in
  `CampaignPreInit` (campaign.qc:63). The feature is faithful.
- Should `usetarget`/`killtarget` be wired (Register + allowlist) to match Base, or are they intentionally omitted?
  Their case bodies already exist; only the dispatch wiring is missing.
- Are any of the missing cheats (notably `make`, `pointparticles`, the drag race-editor) needed for the port's
  map-authoring / testing workflow, or are they intentionally out of scope? Owner input would let these be
  reclassified as `intended_divergence`.
