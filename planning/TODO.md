# XonoticGodot — Active TODO (single source of truth)

**This is the living task tracker.** The two analysis docs are *point-in-time snapshots* and should not be used to
manage work:
- `../REBIRTH_FEATURE_COMPLETENESS.md` — independent audit (2026-06-05) + diff-impact revision. Reference for *why*/*where*.
- `planning/legacy/REMAINING-WORK.md` — the team's earlier gap list. Historical.
- `planning/legacy/todo/phase-*.md` — the original phased plan. Historical.

Created 2026-06-05 from `REBIRTH_FEATURE_COMPLETENESS.md` Part III (P0–P3), incorporating the post-audit diff.

> **📎 Every task is a *port*, not an invention — confirm parity from Base *before* writing code.** Each task's
> canonical Base/Darkplaces source file(s) are listed in the **§ Base/ source map** at the bottom of this file.
>
> **Mandatory per-task protocol (do this for EACH feature, in order):**
> 1. **Read the cited Base source FIRST** and pin down *exactly* how the feature behaves — inputs/outputs, edge cases,
>    spawnflags, the cvars it reads + their shipped defaults, order of operations, and which hook/call-site drives it.
>    **Do not infer behavior from the existing C#, from this file's summaries, or from memory** — the summaries here
>    (and the `…`-truncated source-map lines) are pointers, not the spec.
> 2. **Open the actual file** at `Base/data/xonotic-data.pk3dir/qcsrc/…` (or `Base/darkplaces/…` for engine C) and read
>    the full function(s) — the source-map entry only names them. Confirm parity intent before any code.
> 3. **Implement by mirroring** that behavior (same constants/defaults/branch order); deviations must be deliberate and
>    commented.
> 4. **Cite the source** in the port file's `// Port of <path>` header (the convention 124 port files already follow).
>
> Parity is judged **against Base, not against the port.** The Wave-1 / 2026-06-07 re-audits show what happens when this
> is skipped — drifted cvar defaults, conflated hooks (`PM_Physics`), and "ported" code that doesn't match the
> reference. See **§ Wave-1 fidelity audit** and **§ 🔬 Parity re-audit**.

---

## 🚧 Open blockers for a public release

**TODO-FONTS — ~~two~~ ONE bundled font family ships with no licence notice.** *(dejavu RESOLVED 2026-07-30 — see below.)* Added 2026-07-29 during the
repo restructure, when `data/` became committed content (D1/D2) and the licence texts stopped living in
an upstream checkout that travelled with nothing.

The four font packs are third-party works. `COPYING.xonotic`'s blanket "GPL v3 or later" grant does not
cover them, because Team Xonotic redistributes them without owning them and cannot relicense them.
Verified by reading what each pack actually ships:

| pack | state |
|---|---|
| `font-xolonium.pk3dir` | **OK** — `fonts/README.txt` carries the full GPLv2+ notice, © 2011–2020 Severin Meyer |
| `font-unifont.pk3dir` | **partial** — names the work + URL, no licence text. Needs the GPL font-embedding exception from unifoundry.com |
| `font-dejavu.pk3dir` | **RESOLVED 2026-07-30** — `data/licenses/LICENSE.dejavu` carries both faces' notices verbatim from the fonts' own `name` table record 13. The two differ (Bold has an Arev-glyphs clause), so both are reproduced. |
| `font-nimbussansl.pk3dir` | **missing** — URW++ Nimbus Sans L, no notice |

**What to do:** fetch the authoritative licence text from each font project and add it as
`data/licenses/FONTS.<name>`. Do **not** paraphrase or reconstruct from memory — these are legal
documents and only the real copy is any use. Details and sources: [`data/licenses/FONTS`](../data/licenses/FONTS).

Does not block development. Does block a public release that ships these fonts, since two of the four
currently carry no notice and one of those licences demands one.

---

## How this is managed (orchestrated fan-out, no per-item locks)

A **single coordinator** (an orchestrator session, or a `Workflow` run) assigns disjoint slices of work to
sub-agents each burst. There is **no claim/lock ritual** — non-overlap is guaranteed by the coordinator, not by
agents editing this file. Two fields make that safe and are the heart of this tracker:

- **`Touches`** — the files/globs a task will modify. **The conflict key.** The orchestrator may run tasks in
  parallel **only if their `Touches` sets are disjoint.** Tasks that share a hot file (see the conflict map) must
  be serialized.
- **`Blocked-by`** — prerequisite task IDs. Only dispatch a task whose blockers are ☑.

**Step 0 of every dispatched task = read Base for parity.** Before any code is written, the agent must open the task's
**§ Base/ source map** entry *and the cited Base file(s)* and confirm exactly how the feature works (see the mandatory
protocol at the top). A task brief should quote the Base behavior it is porting; "implement X" with no Base reading is
not a valid dispatch. This is the single discipline that has kept the port faithful across Waves 1–3.

**Update rule:** the orchestrator (not the worker agents) flips a task's checkbox and status when a batch lands,
and periodically re-syncs the affected rating in `REBIRTH_FEATURE_COMPLETENESS.md`. Keep IDs stable (`T#` = Part III
item #). If you add a task, give it the next free `T#` and fill in `Touches`/`Blocked-by`.

**Status legend:** ☐ not started · ◐ in progress (a batch is dispatched) · ☑ done · ⏸ deferred
**Priority:** 🔴 P0 (blocks a playable game-from-menu) · 🟠 P1 (faithful feel / important) · 🟡 P2 (breadth/fidelity) · ⚪ P3 (long tail/polish)

---

## ⚠️ Hot-file conflict map (serialize tasks that share a row)

The orchestrator must **not** run these together — they edit the same file:

| Hot file | Contended by | Note |
|---|---|---|
| `src/XonoticGodot.Server/GameWorld.cs` | **T2, T3, T6, T14, T17, T35, T36, T39, T42** | Biggest contention point. Consider splitting it, or serialize. (Re-audit added item-spawn, objective-wire, bot-tick, overtime call-sites.) |
| `src/XonoticGodot.Common/Gameplay/Weapons/*.cs` | **T2, T6, T16, T19, T51, T57** | Serialize all weapon-touching tasks (T2 first — it reshapes the fire path). T51 adds Overkill weapons; T57 the weapon tail. |
| `game/PlayerController.cs` | **T4, T15** | Camera work and keybind-consumption both edit it. |
| `src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs` | **T3, T11, T16, T18, T40, T43, T45, T57** | Coordinate / serialize. Re-audit added combat-sound/obituary, monster-damage, warpzone-radius, accuracy call-sites. |
| `src/XonoticGodot.Common/Gameplay/MapObjects/MapObjectsRegistry.cs` | **T14, T22, T35, T36, T48, T52, T59** | The single spawnfunc registry — now the hottest file: items, mode-objectives, content tail, compat remaps all register here. Serialize. |
| `src/XonoticGodot.Common/Gameplay/Registries.cs` + `GameInit.cs` | **T17, T19, T26** | New registrations + source-gen activation. |
| `src/XonoticGodot.Common/Gameplay/GameTypes/*.cs` | **T7, T8, T17, T18, T36, T42, T53** | Scoreboard wiring, team-spawn, new modes, mode-depth, objective spawns, overtime, round-stat networking. |
| `src/XonoticGodot.Common/Gameplay/Mutators/MutatorHooks.cs` | **T37, T44** | Vehicle lifecycle hooks + PM_Physics/IsFlying — both add hook chains. |
| `src/XonoticGodot.Server/Commands.cs` | **T38, T46, T47, T56, T60, T70** | Minigame/chat/generic/admin verbs + the client-command-bus gate + the admin/util cmd tail all touch the command table. |
| *(post-re-audit note)* | — | The 9 dead-code-activation tasks (T35–T43) mostly add *call-sites* into existing files (GameWorld/DamageSystem/MapObjectsRegistry) rather than new files — so they contend more than the Wave 1–3 tasks did. Prefer serializing within a hot file over worktrees here. |

---

## ✅ Recently landed (post-audit — do NOT re-dispatch)

These closed/moved after the 2026-06-05 audit (see the `↻` notes in `REBIRTH_FEATURE_COMPLETENESS.md`):
- ☑ **Menu-side HUD-editor** — 22 `DialogHudPanel*` + `DialogHudPanels` + `DialogHudSetupExit`, cvar-bound. (T27 narrowed to the *in-game* editor.)
- ☑ **First windowed visual verification** — `game/ScreenshotHook.cs` (caught + fixed a DDS bug). (T5 narrowed to *systematic* multi-map QA.)
- ☑ **Skeletal player rendering** — `game/client/PlayerModel.cs` + `LocomotionBlend.cs` + `PlayerSkeletonConfig`. (Does **not** close the view/camera gap T4.)
- ☑ **Inline-trigger bbox firing fix** — `MapObjectsCommon.InitTrigger` now `SetModel`s inline brush models.
- ☑ **`ModelInfoAndBlendTests`** — partial progress on T31.

### ✅ Wave 1 landed (2026-06-05) — T1, T2, T4, T7, T13, T20

All six Wave-1 tasks shipped (6 parallel agents, disjoint `Touches`), then an adversarial QC-faithfulness + cross-seam
review pass; the build is clean (0 errors) and **258 tests pass** (was 257 + a new spawn-equip regression guard). The
review found **1 blocker + 10 majors** that build/tests didn't catch — all fixed before marking done:
- **[blocker] Networked fire was inert** — spawned stock players never equipped a weapon (`ActiveWeaponId == -1`); the
  loadout filled only the NetName set, not the canonical `OwnedWeaponSet`. Fixed in `SpawnSystem` (QC `w_getbestweapon`
  on spawn) + a regression test (`SpawnLoadoutTests`). *The wire chain (input→buttons→driver) was already correct.*
- **[major] Hook secondary** spawned ~72 gravity bombs/s (no refire gate) → gated via `PrepareAttack` + a `RefireFor` override.
- **[major] `ServerNet.ProvideInput` double-drain** — input dequeued twice/tick (movement + weapon) over-acked under jitter
  → made idempotent per sim tick (cache keyed on `_world.Time`).
- **[major] T20 dlight tween on a detached node** → NRE on every explosion; **sizeincrease Curve clamped to 1.0** →
  growing particles never grew. Both fixed in `EffectSystem`.
- **[major] T7 `NetworkedFields` cache** keyed on `FieldCount` (never changes) → stale column layout on mode switch →
  now invalidated by a label-generation counter.
- **[major] T4 FOV** dropped the QC `*0.75` aspect-normalization (~16° too wide); **eventchase death-cam** ignored the
  `cl_eventchase_death == 2` velocity-settle gate (+ wrong default). Both fixed in `PlayerController`.

**Emergent follow-ups (new — see T34 + the T9 note):** the menu "Start"/connect path runs through the new
`game/net/NetGame.cs`, so T4's view feel lives where the user doesn't play it. ViewEffects (damage-flash + liquid-tint)
+ the `*0.75` FOV were wired into NetGame here; **zoom, death-cam, the full HUD, and the view-model are still NetGame-TODO**.

### ✅ Wave 2 landed (2026-06-06) — T3, T6, T8, T10, T12, T14, T21

All 7 shipped via a conflict-safe 2-phase fan-out (T3·T10·T12·T21 ‖ then T6·T8·T14), then an adversarial review (29
findings: 6 major / 14 minor / 9 nit, 0 blockers) → 17 fixed + 1 deferred (rifle trace) + the rest folded into the
tasks below. **Build clean, 266 tests pass; both headless play paths verified** (`--net-loopback`, `--host atelier --bots 2`).
What each delivered + the **residual wiring** a future wave must do to make it *fully observable on the menu→play path*:
- **T3** — mutator activation loop (`MutatorActivation.Apply` = `base.qh` STATIC_INIT_LATE) wired into `GameWorld.Boot`;
  all 9 dormant hooks now fire (incl. the once-dead `EditProjectile` at 21 projectile-spawn sites + `GiveFragsForKill`
  in `Scores.Obituary`). Enabling `g_dodging`/`g_instagib`/… now changes a match.
- **T6** — reload (`W_Reload`/`clip_load`), `switchdelay_raise/drop`, full `selection.qc` + impulse map landed. **Residual:**
  per-weapon ammo must route through `clip_load` to make reload *observable*, and the **net client→server impulse channel**
  is missing (humans can't switch/reload in net play yet) → folds into **T34** / a net-input task.
- **T8** — team-spawn selection (the `spawnpoints.qc` teamcheck ladder + `have_team_spawns` + `info_player_teamN`). `targetCheck=true` parity for the 2 other call sites is a **T18** (Onslaught) dependency.
- **T10** — all 7 panels ported + registered in `HudManager`. **Residual:** they only live in the `--map` `Hud`, not
  `NetGame`'s `NetHud`, and need their net data feeds → **T34**.
- **T12** — antilag rewinds monsters+nades, measured RTT, predictor reads replicated movevars. Nade rewind is dormant until **T11** spawns nades.
- **T14** — `monster_*`/`turret_*`/`vehicle_*`/`monsters_spawner` spawnfuncs registered; hand-placed NPCs spawn + think. Reset-on-round-restart deferred to the NPC subsystem tasks.
- **T21** — true-aim coloring (HITENEMY/TEAM/WORLD) + switch cross-fade. **Residual:** needs `Crosshair.AimForward` fed +
  the HUD wired into `NetGame` → **T34**; the rifle through-wall trace (**new follow-up**) needs an `Api.Trace` body/corpse-only content-mask.

### ✅ Wave 3 landed (2026-06-06) — T9, T11, T15, T16, T17, T18, T19, T22, T34

All 9 shipped via a conflict-safe **3-phase fan-out** (recon → P1: T9·T11·T16·T18 ‖ P2: T15·T19·T22 ‖ P3: T17·T34),
then a **12-agent adversarial review** (37 findings: 1 blocker / 6 major / 24 minor / 6 nit; T16 + both seam reviews
came back *faithful*). **Build clean, 548 tests pass** (was 306 at wave start; **+242**). The blocker + 4 majors +
3 high-confidence minors were fixed; **2 majors carried forward** (below). Key structural win confirmed by recon: the
audit's `DamageSystem.cs`/`Registries.cs` contention **dissolved** — the T3 hook call-sites are live and
gametypes/mutators auto-register by attribute, so most "core" work became hook handlers in *new* files.
- **T9** — ScoreInfo wire (per-mode score label/flags) so a remote client's `LayoutHash` matches the server (it was
  silently dropping the whole scoreboard block every snapshot); `LatestScoreboard` now fed into NetGame's
  `ScoreboardPanel`; configurable columns/sort/value-format. **Residual:** accuracy/rankings/map-stats render from
  settable surfaces but aren't fed on the net path yet; per-row ping/pl not networked; CTF team banner shows the
  ST_SCORE slot, not the primary team slot.
- **T11** — powerup consumers (strength/shield via `DamageCalculate`, speed via a new `WeaponRateFactor` hook, invis
  alpha) + the **full nades subsystem** (registry, throw/charge/bonus, projectile, **11 boom types**). **Residual
  (carried major):** nades are *un-throwable on the play path* — `Entity.OffhandFirePressed` is set nowhere (no
  offhand/nade `InputButtons` bit); needs a button bit + bind + server apply (shares the pre-existing
  `OffhandBlasterMutator` gap). Client render (timer model, trails, darkness overlay) out of scope (headless).
- **T15** — binds-xonotic.cfg consumed on both play paths (NetGame + PlayerController); cvar store = source of truth;
  `MenuSettings` keybinds quarantined. **Review fixes:** `+fire2` secondary alias + the `togglezoom` press-latch (the
  stock MOUSE3 zoom bind). **Residual:** runtime console `bind`/`unbind` doesn't translate engine key names (minor).
- **T16** — headshot head-AABB + multiplier (hitscan/rail) + rifle bullethail continuation. *(Review: faithful.)*
- **T17** — Mayhem + Team Mayhem (score = damage + frags), all 3 scoring methods verbatim; widened the
  `PlayerDamage_SplitHealthArmor` hook to fire unconditionally (more QC-faithful) via a back-compatible 8-arg
  `PlayerDamageArgs`.
- **T18** — Onslaught CP/generator combat; CTF pass/throw/retrieve; Domination round variant; Race penalty/overtime.
  **Review fixes:** wired `ctf.Tick()` into the frame loop (passing was inert — a missed pass lost the flag forever),
  CTF throw/pass antispam now enforced, Onslaught `ONS_TAKES` no longer over-credited on a build-capture. **Residual:**
  CTF pass-in-flight is an instant transfer (no headless projectile integrator); thrown flag uses body not view
  pitch (no `ViewAngles` field yet); Dom pre-match tick guard needs a synced `game_starttime`.
- **T19** — **10 of 12 mutators live** (rocketminsta, stale_move_negation, random_gravity, globalforces, vampirehook,
  weaponarena_random, new_toys, spawn_near_teammate, spawn_unique + the dormant 2 below). **Orchestrator wiring:** the
  `MutatorHooks.FireStartFrame` pump was added to `GameWorld.OnStartFrame` (random_gravity et al. were dormant), and
  the **blocker fix** seeds `Entity.DamageForceScale` (=`g_player_damageforcescale`, 2) on spawn so knockback +
  globalforces actually apply. **Residual:** random_items + physical_items are structural shells (need a map
  item-entity spawn pipeline / ODE); **rocketflying (carried major)** is inert until the Devastator stores its
  detonate gate on the missile entity (it writes the wrong field today; conf=low).
- **T22** — logic gates + `target_*` utilities + `func_door_secret`/conveyor/ladder. **Review fix:** door_secret now
  opens via per-entity `GtEventDamage` (the `Combat.Death` hook was unreachable at 10000 HP). **Residual:** entity
  `.reset` bodies are ported but dormant (no map_restart reset pass); `target_give`/`target_items` can't find map
  items until they're in the targetname index; magicear/target_spawn templating cut.
- **T34** — shared `FirstPersonView` (zoom/death-cam) for NetGame+GameDemo; full HUD adopted on the net path (**review
  fix:** dup crosshair/health hidden, the unfed Timer hidden); **C2S impulse channel** (`InputCommand.Impulse`,
  protocol→5) so weapon switch/reload reach the server via `WeaponImpulses.Handle` — **closes T15's deferred residual**.

**Carried-forward majors:** (1) **T11** nade-throw input wire (an offhand `InputButtons` bit → `OffhandFirePressed`,
+ bind + server apply) — **STILL CARRIED** (offhand-gated, wants a live check); (2) **T19c** rocketflying detonate-gate
— ✅ **RESOLVED in Wave A1** (new `Entity.ProjectileDetonateTime` on the Devastator/Minelayer missile, cleared by the
mutator's `EditProjectile`). **T5** (windowed visual QA) is still ☐ — deferred to a live-run pass.

### ✅ Wave A1 landed (2026-06-07) — T35, T40, T44, T38, T50, T58, T19c

All 7 forward-plan A1 tasks shipped via recon → parallel-impl → adversarial-review → fix (4 workflows; 7·7·7·6 agents).
**Build clean, 734 tests pass** (was 548 at wave start; **+186**); the real play path boots headless clean (`--host
atelier` — map loads, `ServerNet` accepts the player, exit 0, no exceptions). Review (7 reviewers, refute-by-default):
**0 blockers / 3 majors / 14 minors / 1 nit** — **T19c came back with ZERO findings**; T35/T40/T44/T38 faithful. All 3
majors + 13/14 minors + the nit fixed.
- **T35** — world-item spawn + touch (`StartItem`/`ItemSpawnFuncs`/`Item_Touch`→`ItemPickupRules`): every
  `item_*`/`weapon_*`/powerup/buff classname now spawns + is collectable; loot toss/lifetime/spawn-shield, weapon-stay
  (+ `g_weapon_stay==2` stay-ammo), powerups don't spawn at t=0, full `GiveItems` op grammar (shared w/ target_items +
  cheats). **Closes the "no pickups on any map" P0.** New `Entity.CanPickupItems` (FL_PICKUPITEMS) tagged on spawn.
- **T40** — server combat-event emission: `Scores.Obituary` now fires kill-feed/frag/typefrag/killstreak/first-blood
  via the (already-built) `NotificationSystem`; pain/death/drown + armor/body-impact sounds at the damage sites;
  warmup/round COUNTDOWN announce. New central `DeathMessages` table (the `wr_killmessage` successor — kept OUT of
  weapon files); `Warmup`/`Rounds.OnCountdownTick` → GameWorld-wired. **The biggest "feel" cluster.**
- **T44** — `PM_Physics`/`IsFlying` mutator hook call-sites (the T3 "PM_Physics wired" claim was WRONG → now REAL;
  unblocks bugrigs/T51) + spectator free-flight (`SPECTATORSPEED` seed/ladder, new `Entity.SpectatorSpeed`, wishmove
  scaled by maxspeed_mod). **Residual:** spectator-fly is inert until a `spectate`→fly-movetype transition exists
  (ClientManager/Commands — not in A1; the physics + the GameWorld move-gate are correct + dormant until then).
- **T38** — minigame activation: `MinigameSessionManager` + `cmd minigame create/join/part/end/invite` + the S2C
  session snapshot (`NetControl.MinigameState`, protocol→6, orchestrator-wired in ServerNet) + the in-game minigame
  HUD menu. Pong/TTT/C4 startable+playable. **Residual:** Bulldozer ships no on-disk levels (best-effort, out of P0).
- **T50** — menu command dispatch (directmenu/nexposee/closemenu/servers/profile/settings) + PauseMenu Join!/Spectate/
  Leave/Quick + visual pickers (crosshair/charmap/colorpicker/playermodel/reorderable `cl_weaponpriority`).
- **T58** — csqcmodel render hooks: force-model/colors, LOD `_lod1/_lod2`, deathglow/respawn-ghost, fallback-frame
  remap, `EF_*`/`MF_*` lights+trails. **Review caught a real bug:** server `EntityMutatorState.EffectFlags.FullBright`/
  `NoShadow` were mislabeled (8/8192 = EF_DIMLIGHT/EF_NODEPTHTEST) vs the engine 512/4096 the client reads → instagib/
  buffs fullbright never rendered → **fixed** (server + client now agree). New client appearance API wired via NetGame.
- **T19c** — rocketflying detonate-gate (carried major RESOLVED): `Entity.ProjectileDetonateTime` replaces the
  closure-local gate on the Devastator/Minelayer missile; `RocketFlyingMutator.EditProjectile` clears it.

**Durable lessons (for A2+):** (a) the `GameWorld.cs`/`ServerNet.cs` chokepoints held to **1 task/wave-owner** with
the other tasks **reporting exact seam snippets the orchestrator integrates** worked cleanly (no clobbers) — the model
the conflict map predicts. (b) Recon mis-pathed two partial-`Entity` files (`EntityItemState` is under Gameplay/Items,
not Framework; `PlayerPhysicsState` didn't exist → agent created it) — verify partial-Entity homes. (c) `XonoticGodot.Common.Gameplay`
contains BOTH a sub-namespace-leaf `Notifications` AND a type `Notifications`, so `using …Gameplay.Notifications;`
fails (CS0138); the notification types (`NotificationSystem`/`MsgType`) live in `…Gameplay` and come via the plain
`using XonoticGodot.Common.Gameplay;`. (d) **Two real fidelity bugs build+tests missed** — T58 EffectFlags (above) and a
T44 spectator wishmove-cap (the fly-branch capped wishspeed at base speed regardless of `SPECTATORSPEED`) — both
caught by the adversarial review + a feel-oriented test; keep the review pass. (e) **Deferred:** T58
death-frame/`death_time` networking (needs a `NetEntity` wire-widen → T54 territory); 2 stale private TEST copies
(CountdownAnnouncerTests' game-start helper; a charmap assert) for a test-quality pass.

**Playable-from-menu** advanced: floor pickups + combat feedback + working minigames now live on stock maps.

### ✅ Wave A2 landed (2026-06-07) — T36, T43, T37, T51, T56, T28, T30

All 7 A2 tasks shipped via the proven model — **recon → parallel-impl → adversarial-review → fix** (4
workflows: 7 recon · 7 impl · 8 review · 3 fix agents). The orchestrator pre-added the cross-task hook
interface (`MutatorHooks`: 4 vehicle hooks + `PlayerDamaged`) so every impl agent compiled against a stable
seam, then integrated 9 cross-task snippets serially (no two agents edited the same file in the parallel
phase — the A1 chokepoint-owner model held cleanly again). **Build clean (0 errors), 1046 tests pass** (was
738 at wave start; **+308**); the real play path boots headless clean (`--net-loopback`: 0 errors / 0
warnings, exit 0). Adversarial review (8 reviewers, refute-by-default): **0 blockers / 8 major / 14 minor /
3 nit** — every major + the high/medium-impact minors fixed; 1 review finding (F10 WallFriction) was itself
**refuted** as a false positive (the code was already correct). Carried follow-ups are in
`planning/legacy/wave-a2/deferred-findings.md` (all low/nil-impact or recon-deferred).
- **T36** — Assault/Nexball/Invasion/CTS map-objective spawnfuncs now route through `GametypeObjectiveSpawns.Sink`
  → `WireObjectiveSpawns` → each gametype's spawn API; Assault destructible→decreaser→objective links resolve
  spawn-order-independently via `Assault.ResolveObjectiveGraph` in a new `GameWorld.Boot` post-spawn hook;
  `ApplyDictFields` now plumbs `cnt`/`health`/`dmg`. **Those modes finally have a win path on a real map.**
  KeyHunt left alone (keys round-spawn — adding `item_kh_key` would double-spawn). *Carried:* `func_assault_wall`
  collision-toggle (F11), STAGE round-end volume (F12), empty-spawnmob wave (F24).
- **T43** — monster damage→pain/death/reset wired via Option A (a `GtEventDamage` shim installed on spawn —
  **zero DamageSystem edits**); `-100` live/`-50` corpse gib thresholds, faithful double-push knockback,
  `monsters_total`/`killed` counters (reset on map-load), `Monster_Reset`, default armor seed, INVINCIBLE
  lava/slime exemption, `g_monsters_score_kill`. Fixed the 2 cfg-default bugs (respawn 10→20, spawnshield
  1→2). *Carried:* per-monster `mr_death` corpse cosmetics (F16).
- **T37** — vehicles are **boardable + drivable**: new `VehicleBoarding` (use-key board radius 250 + guards +
  MULTISLOT gunner + impulse mode-switch); `GameWorld.OnClientMove` seated-gate writes `VehInput` + skips
  PM_Main + (review fix) skips the on-foot weapon driver; Bumblebee touch-board gated to `g_vehicles_enter==0`.
  *Documented partials:* touch-mode board, controller/`ACTIVE_NOT`/delayspawn, steal, client-side seated
  prediction (all recon-scoped out); gunner-hook slot1 (F25).
- **T51** — doublejump **fixed** (conditional surface-trace + velocity-clip via a real `DoublejumpMutator`;
  default `sv_doublejump 0` keeps golden traces byte-identical); the 5 **Overkill weapons** implemented
  (auto-register; `g_overkill` now grants them) incl. the okhmg superweapon gate + clip-aware reload + the
  ammo-check dispatcher; **bugrigs** (`PM_Physics` race-car drive), **hook** (offhand grapple), **campcheck**,
  **damagetext** (server feed + `DamageTextLayer` + the `PlayerDamaged` fire-site), **itemstime** backend
  (feeds the ITEMSTIME panel). *P3 stubs w/ documented substrate blockers:* breakablehook, kick_teamkiller,
  dynamic_handicap; *explicit deferral:* superspec, sandbox.
- **T56** — `defer` (sim-clock queue → a passed `restart` vote **now fires**), the **RPN VM** (crc16/bound
  fixed to DP-exact, `time` op), `maplist`/`addtolist`/`removefromlist`/`nextframe`, client verbs
  (voice/suggestmap/autoswitch/physics/clientversion), reply verbs (records/rankings/lsmaps/printmaplist/
  ladder + cvar_purechanges), `editmob`/spawnmob/killmob (+ in-vehicle gate). *Deferred (documented):* the
  RPN DB-family + qc_curl/dumpcommands/restartnotifs/runtest.
- **T28** — the i18n layer: a Godot-free **PO gettext engine** (C-escapes/octal/continuation/overlay-order),
  `Tr`/`CtxTr` seam through `Ui`/`MenuScreen`/`CvarControls`, data-driven `languages.txt` picker, `menu_restart`
  rebuild; the full **`SkinValues`** schema (skin-customizables.inc) backing `MenuSkin`. *Carried (content, not
  code):* no `common.*.po` ship in `XonoticGodot.Assets` yet, so translation is inert at runtime until they're
  added (F2). Octal multi-byte (F23) theoretical.
- **T30** — `trace_t.DpHitTextureName` is now **truthfully populated** (shader name threaded `Brush`→SAT
  sweep→`TraceResult`, mirroring the SurfaceFlags template; corpus byte-identical) — unblocks the `common/caulk`
  spawn-rejection. Golden-trace loop: corpus cross-validated against the live engine config +
  `verify-against-dp.md` capture plan (full DP-capture pipeline deferred pending a gmqcc `progs.dat` rebuild,
  per recon — honest increment, not over-claimed).

**Durable lessons (for A3+):** (a) **pre-adding the cross-task interface symbols** (hook chains) before the
parallel impl let every agent compile against a stable seam and removed `MutatorHooks.cs` from the parallel
edit set entirely — the single highest-leverage orchestration move this wave. (b) The "owner edits the file;
others return exact integration snippets the orchestrator applies serially" model held with **zero clobbers**
across 7 concurrent agents (the agents even self-reconciled transient cross-task build breaks mid-run). (c)
refute-by-default review pays for itself: it caught a real every-vehicle-use bug (seated player firing their
hand weapon) that build+1017-tests missed, AND a reviewer false-positive (F10) that refuting saved a wasted
fix. (d) Per-agent test counts are mid-run snapshots — only the orchestrator's post-integration full run is
authoritative.

**Playable-from-menu** is now reached (A1+A2): pickups, combat feedback, working bots/NPCs damage+death,
vehicles, and objective win paths on stock maps.

### ✅ Wave A3 landed (2026-06-10) — T39, T57, T48, T53, T54, T26, T31, T33

All 8 A3 tasks shipped via the proven recon → parallel-impl → adversarial-review → fix model (the impl phase was
session-limit-truncated mid-run and committed by the user as `ba83632`; the orchestration this session **finished
the integration the truncated agents had handed back as snippets**, fixed the resulting test failures, ran the
review, and fixed the findings). **Build clean (0 errors), 1446 tests pass** (was 1158 at wave start; **+288**); the
real play path boots headless clean (`--host atelier --bots 4`, exit 0, 0 hard errors). Adversarial review (6
dimensions × refute-by-default verify, 26 agents): **0 blockers / 5 major / 14 minor confirmed real / 1 refuted** —
all 5 majors + the cheap-clear minors fixed; the rest documented as residuals below.
- **T39** — HavocBot AI **live**: `BotPopulation` (bot_serverframe/bot_fixcount: fill/remove, skill resync,
  strategy-token rotation, danger cadence) + `BotDanger` (havocbot_checkdanger) wired into `GameWorld.OnStartFrame`;
  bot input rides the SAME per-tick path humans use (GameWorld branches on `p.IsBot`); `--bots N` now spawns bots
  that move/navigate/fight/respawn. **Review fix (major):** strategy-token no longer deadlocks on a dead/observer
  holder (the whole population had stopped rating goals); **+ bot_nofire** honored. *Residual:* bots fire primary
  only — no per-weapon `wr_aim` secondary (devastator detonate / mortar airburst), a documented AI simplification.
- **T57** — weapon tail: `W_ThrowWeapon` (impulse 17 + death-drop + `PinataMutator`), accuracy tracking
  (`WeaponAccuracyEvents` fired/hit/real with `accuracy_isgooddamage` gates) → `Scores.AccuracyBytes` → the **owner-only
  accuracy wire** (reconstructed this session: appended at the owner-state END, gated by AccuracyGeneration) →
  `ScoreboardPanel`/`WeaponsPanel.SetAccuracy`; Vortex chargepool + velocity-charge + forced reload. **Review fix:**
  `accuracy_byte` rounding now DP-`rint` (round-half-up, not banker's). **Weapon by-id fix (major):** `weapon_byid_N`
  now selects the QC-faithful order (blaster,shotgun,… via a weapon-local `WeaponOrder.ByIdOrder`), not the
  alphabetical RegistryId (which had `weapon_byid_0`→arc).
- **T48** — map content tail: `misc_laser` hazard (+ beam render), decoration models
  (`misc_gamemodel`/`func_static`/…), `func_pointparticles`/`func_sparks`, `func_rain`/`func_snow`, `target_speaker`,
  `target_music`/`trigger_music`. **Root-cause review fix (2 majors):** `MapObjectFieldsExtra.Apply` was called only
  on the offline `--map` path, never `GameWorld.ApplyDictFields` (`--host`), so on the live path every content-tail
  key (mdl/effect, scale, fade, music lifetime/fade, weather dir, wall solid) was silently dropped — now applied on
  both paths.
- **T53** — round/objective HUD stats for CA/FreezeTag/KeyHunt/Survival: `GametypeStatusBlock` codec (alive counts,
  KH 5-bit key pack w/ 31=self, Survival role + the **hunter-disclosure anti-cheat** — prey never receive hunter ids
  mid-round) wired into the snapshot (hash-gated per-peer) + `ModIconsPanel`/scoreboard eliminated grey-out. **Review
  fixes:** FreezeTag rules-freeze (NULL attacker) no longer deducts a victim point; Survival roles now count live
  players only + pick hunters RANDOMLY (was fixed roster order).
- **T54** — movement/prediction breadth: full MOVEVARS replication (40→46), `Physics_ClientOption` presets
  (`g_physics_clientselect`), `sentcvar`/`sendcvar` (cl_weaponpriority/autoswitch/noantilag/physics). **Review fix
  (major):** the movevar decode no longer clobbers an authoritative configured/replicated **0** back to the Xonotic
  default (CPMA/Nexuiz/Quake3 servers were silently getting Xonotic stepdown/airstrafe) — wire slots are authoritative,
  cvar fallback is now EXISTS-gated, and absent unregistered movement cvars capture the stock default so the default
  deployment doesn't rubber-band. Golden movement traces stay byte-identical.
- **T26** — source generator activated: emits Monster/Turret/Vehicle registrations + the `Bootstrap()`→`RegisterAll()`
  swap.
- **T31** — test coverage: +parser/builder/NPC/turret/vehicle/minigame suites (Md3/Dpm/Iqm/Sprite/BspLayout,
  Turret*, Monster*, Vehicle*, minigame rule engines). **Caught 2 real production bugs** the impl phase missed (a
  turret death-hook clobbered by the player-corpse path; bots force-respawned past DEAD_DEAD) — both fixed.
- **T33** — CI (`.github/workflows/ci.yml` + `ci/ci.sh`), perf benches (`tests/.../Perf/*`, measurement-first),
  packaging (`export_presets.cfg`, `tools/{package,run-dedicated}.sh`), `ADR-0014`.

**Carried residuals (documented, deferred):** bot secondary-fire (`wr_aim` ATCK2); bot custom-weapon-priority lists
ship empty; enemy-stick/moving-target strategy interval; Survival panel hides at round-end vs QC's next-countdown +
no row colormap; CA/Survival eliminated≈dead (no INGAME_JOINING state); T57 spectator accuracy-share
(`sv_accuracy_data_share`); zombie/other-monster attack damage read once at construction not live-cvar (default-correct);
the SourceGen weapon-order assertion is a round-trip (a by-id pin was added). **Also (port extension, kept):**
`sv_step_upspeed_scale`/`_max` step-up vertical-velocity limiter — see [[rebirth-stepup-velocity-limiter]].

**Playable-from-menu** now includes live bots, working accuracy/weapon-throw, map ambience/hazards/music, and
per-mode round HUD.

### ✅ Wave A4 landed (2026-06-12) — T41, T42, T46, T52, T23, T27, T66, T67 (+ T24 formally cut)

All 8 A4 tasks shipped via the proven **recon → parallel-impl → adversarial-review → fix** model (4 workflows). Batch-1
(T42·T27·T46·T52·T66·T67·T23·T24) ran in parallel on disjoint hot files **with no concurrent builds**; **T41** integrated
last (it splices into T42's `GameWorld` rewrite + T27's `NetGame` input hook). **Build clean (0 errors / 0 warnings),
1735 tests pass** (was 1456 at wave start; **+279**) — **independently re-verified**; the headless `--quit-after` smoke
and the dedicated `--host stormkeep --bots 2` smoke are both clean (map loads, bots fill, handshake accepted, 0 managed
exceptions). Adversarial review (10 reviewers, refute-by-default, every finding independently re-refuted): **16 confirmed
(3 blockers / 6 major / 5 minor / 2 nit)** — **all fixed except 2 correct no-ops**. Headline lesson (again): build +
unit-tests were green but **four host seams were dead on the live path** — computed + unit-tested, never *delivered* —
the port's recurring "ported-but-unwired" failure mode, caught only by the review.
- **T41** — client feedback: view.qc HitSound (`cl_hitsound` confirm beep off a networked damage-dealt stat), the
  clock-driven announcer **time-remaining** VOICE (`announcer.qc`; the 3-2-1 countdown is already T40, so `Tick` drives
  only time-remaining → no double-fire), and the objective crosshair rings (NADE>CAPTURE>REVIVE). **Review fixes (blocker
  ×2):** `ServerNet` never networked the new `Feedback` fields → rings/hitsound were host-only → now in the player
  snapshot; **`FreezeTag.ReviveTick` was never called** → revive ring dead → now driven per-frame via a networkable
  `Entity.ReviveProgress`.
- **T42** — `CheckRules_World` overtime/sudden-death cascade (`InitiateSuddenDeath`/`InitiateOvertime`/`GetWinningCode`):
  a tied timed DM/TDM/CTF/Dom/KH now enters overtime/sudden-death (extending the live `timelimit` cvar) instead of
  **drawing**, via a new `OverTimeManager` + a virtual `GameType.ReportsTie` on the 11 timed score modes; campaign gated.
- **T46** — full `server/chat.qc Say()`: `say_team`/`tell`/`ignore`/`unignore`/`clear_ignores`, the 4 `g_chat_*` gates,
  per-type flood, mutual-ignore routing, muted fake-accept, `formatmessage` macros. **Review fixes (blocker ×2):**
  `cmd.ChatToPlayer`/`ChatConsole` were never wired in `NetGame.StartListenServer` → all team/private/spectator delivery
  silently no-op'd → now wired (stock + threaded paths).
- **T52** — Q3/QL/CPMA/Q1/WoP/Q3DF compat *add*-side remaps (weapon/item classname tables + ammo `.count` scaling + Q3DF
  `target_*`). **Review fixes (major ×2):** `CompatRemaps.IsCtsActive` unwired (→ `target_score`/`target_fragsFilter`
  self-deleted on **every** map) and the `"frags"` map-key never promoted to `Entity.Frags` — both wired in `GameWorld`.
- **T27** — `hud_config.qc` in-HUD configure editor (drag/resize, grid-before-collision snap, Ctrl+Tab/Space/Z/S,
  normalized cvar write-back). **Review fixes:** `K_PAUSE` now passes through during edit; faithful `ftos_mindecimals`.
- **T23** — the shared menu `DataSource` abstraction + screenshots/music/skins/server-info/create-game map-info backends +
  `MapInfoBackend` (.mapinfo parser). **Review fixes:** the complete-but-orphaned `DialogMediaSkinList` and
  `DialogCreateGameMapInfo` are now wired to their live paths (skin list in Settings/User; map-info popup on map dbl-click).
- **T66** — fires the `FilterItem` item-spawn hook (6 subscribers were dead) → NIX strips map items, Duel blocks powerups,
  MeleeOnly strips small health/armor; `NetName` assigned before the hook.
- **T67** — HLAC crouch-spread modifier (ducked+grounded), mirroring Machinegun.
- **T24** — **formally cut** (0 importer LOC; see the table row).

**Carried residuals (documented):** STAT(OVERTIMES) client net (HUD "OVERTIME" banner) → T54/T23-net; round modes
(CA/FreezeTag) + objective-latched (Onslaught/Assault) keep the no-tie default; chat mutator-bus hooks + active_minigame
team branch + ServerProgsDB-permanent ignores deferred (in-memory PersistentId ignore used); chat `%x`/`%y` tokens fall
back (no server chat tracer); `Muted` not yet seeded from `g_chatban_list` at connect; T52 SG↔MG arena swap +
target_print Q3DF mode + target_init buff-drop need a live `q3compat`/.arena flag; T41 CaptureProgress ring hidden (no
Dom/Onslaught producer — faithful: Base never sets it) and rings are crosshair-anchored vs QC's 0.6×height anchor + no
per-ring labels; T23 playerstats list honest "pending" (no stats DB) + some buttons await T50 `menu_cmd`; T66
`Duel.FilterItem` still unregistered (GameItemDef signature); T27 chat-panel min-size/hover-fill/cursor-shape + a real
`hud save` file-writer deferred.

### ✅ Wave A5 landed (2026-06-12) — T45, T47, T59, T61, T68, T69, T29, T49, T5

All 9 A5 tasks shipped via **recon → parallel-impl → adversarial-review → fix** (4 workflows). Recon resolved all
contention to disjoint file ownership, so the 9 ran in **one parallel batch — no serial-integration step** (cleaner
than A4). **Build clean (0 errors / 0 warnings), 2681 tests pass + 686 VisualQA** (was 1735 at A4 close; **+946** —
T5's per-map/model/shader theories + the T47/T59/T61 suites) — **independently re-verified**; the `--quit-after` and
dedicated `--host stormkeep --bots 2` smokes are both clean. Adversarial review (11 reviewers incl. a dedicated **T47
security auditor** + a **wire-check**, refute-by-default, every finding re-refuted): **12 confirmed (2 blockers / 6
major / 3 minor / 1 nit)** — **10 fixed, 2 deferred as tracked follow-ups** (not hacked as speculative dead wires).
**Security: clean** — the T47 audit found NO client-reachable admin verb. The recurring lesson held again: build +
2677 unit tests were green, yet the wire-check surfaced several T61 subsystems ported-but-dead on the live path.
- **T45** — warpzone **combat traversal**: `WarpZone_TraceBox/TraceLine/TraceToss` recursion (16-zone guard, RefSys
  accumulator) + `WarpZone_FindRadius` recursion, routed through `WeaponFiring` + `WeaponSplash.RadiusDamage` so
  hitscan/projectiles/splash cross seamless portals (non-warpzone maps stay byte-identical). **Residual:** the client
  **portal SubViewport render** is deferred (headless-unverifiable; portals still render opaque).
- **T47** — `SV_ParseClientCommand` 3-gate security filter (UTF-8 validation, ban enforcement, per-client flood
  bucket 8/1.0s) + the **server-only vs client-callable allowlist** so a connected client can no longer invoke
  `ban`/`kick`/`map`/`set`/`endmatch`/etc. **Review fix:** flood `<=`→`<` (DP-exact burst boundary).
- **T59** — 7 rare map entities (func_stardust, dynlight, trigger_viewlocation, misc_follow, func_fourier,
  func_vectormamamam, target_voicescript). **Review fix (major):** `VoiceScript.Next` was unwired → now pumped from
  `GameWorld.OnPlayerPostThink`. **Residuals:** dynlight emits no light (no dlight render system — T4), viewloc has no
  client camera consumer.
- **T61** — notification polish: announcer queue/dedup/spacing, deathtype `.message` category registry, MSG_CHOICE
  replication, StatusEffects bitmap codec, `_GlobalSound` VOICETYPE routing. **Review fixes:** **`_GlobalSound` was
  dead (blocker)** — `CmdVoice` now routes through `SoundSystem.GlobalSound` (team-radio/taunt gates + recipients);
  deathtype-category obituary now consulted; MSG_CHOICE **client→server** replication wired (gated). **Deferred
  (tracked):** remote-player StatusEffects net channel (no client overlay renderer exists — `task_bfd50c4f`),
  per-recipient MSG_CHOICE dispatch (`task_95b4dfe3`), monster/turret kill-site categorized-deathtype routing
  (`task_deedaac2`).
- **T68** — shownames player-nametag overlay (name + teammate health/armor bar, LOS/distance/team/overlap fade ramp);
  networked the entcs Armor slice. **T69** — HUD dynamic damage-shake (keyframe jitter, factor latch; confirmed Base
  has no directional nudge).
- **T29** — verified all 7 HUD panels faithful + **fixed** `hud_panel_quickmenu_time` 0→5; **review fix:** added a real
  `EngineInfoPanel` #13 (the FPS-overlay `FpsPanel` is a separate DP `Sbar_ShowFPS`).
- **T49** — single-player campaign **verified faithful end-to-end** (no code change; 23 flow tests). See
  [[campaign-flow-verified]].
- **T5** — visual QA scoped honestly: headless `VisualQaTests` (map load + model skeleton/bind-pose + shader-compile)
  in `ci.sh`; **windowed visual correctness needs a manual eye-check** (`tools/visual-qa.sh` + ../docs/RUNNING.md) — headless
  renders blank.

**Carried residuals (documented):** T45 portal SubViewport render; T61 remote-StatusEffects net channel + per-recipient
MSG_CHOICE dispatch + monster/turret kill-site categorization (3 tracked follow-up tasks); T59 dynlight render + viewloc
client camera + path-travel; T5 windowed pixel verification (manual) + per-model boot flag (`task_9da7b4a6`).

**A6 next** (T55 server fidelity/Porto, T11c nade-throw input, T25 bot scripting+wpeditor, T32 GameTypeVote+mapvote
ballot, T60 admin/ops breadth, T70 admin/util cmd tail).

---

## 🔬 Parity re-audit (2026-06-07) — full QC↔port↔TODO sweep

An 18-subsystem fan-out (one auditor per QC area: weapons, gametypes, mutators×2, monsters, turrets, vehicles,
mapobjects, items, physics, effects/sounds/notify, minigames, client/HUD, menu, server-core, bot, commands, net/lib)
re-diffed **`Base/.../qcsrc` ↔ the port ↔ this file**, then **adversarially verified every "missing" claim**
(refute-by-default; **77 confirmed gaps + ~30 partials**, near-zero false positives). Headline:

> **The port is substantially more complete than T1–T34 implied** — all 20 gametypes, ~30 mutators, all
> monsters/turrets/vehicles, all 8 minigames, and the item/resource model are *ported*. The dominant gap is **not
> missing code but unwired runtime seams**: large, faithful subsystems that nothing on the live path calls — vehicles
> can't be boarded; bots never think; monster damage routes to `PlayerDamage`; the entire notification + sound registry
> is never *emitted*; minigame sessions are never created; world-item pickups are never *spawned*. The remainder is a
> content/breadth tail (map decoration/ambient/hazard entities, compat remaps, ~11 niche mutators) and client-feel
> features (hitsound, shownames, damagetext, announcer driver).

**New tasks added: T35–T61** (4×P0, 12×P1, 8×P2, 3×P3) — refs in the rows + the **§ Base/ source map** below. The
highest-value are the *dead-code-activation* seams: **T35** (item spawn/touch — *no pickups on any map today*), **T36**
(Assault/Nexball/Invasion/CTS objectives never spawn), **T37** (vehicles unboardable), **T38** (minigames unreachable),
**T39** (bot AI not ticked), **T40/T41** (combat feedback never emitted).

### TODO corrections (existing tasks)
- **T3** — claims it wired `PM_Physics` ("the headline dormant Call() site"); it did **not** — `PM_Physics`/`IsFlying`
  were conflated with `PlayerPhysics` and are absent (→ **T44**). The other 9 server hooks *are* wired.
- **T10** — ☑ but the **ITEMSTIME** panel has no server data feed (→ T51) and **MODICONS** feeds only tracked modes (→ T53).
- **T14** — accurate (spawnfuncs registered, NPCs idle-think), but the **monster damage→death** (→ T43) and **vehicle
  enter/input** (→ T37) seams sit between "spawned" and "playable" — NPCs/vehicles are not yet playable.
- **T19** — ☑ but **Overkill** weapons are name-only stubs (grant nothing) and `sv_doublejump` is simplified to an
  unconditional air-jump, losing the surface-gate + velocity-clip (→ T51).
- **T22** — ☑ for the logic-gate/`target_*`/door tail it claims, but its scope **omitted** the decoration/particle/
  weather/sound/laser/key/dynlight/viewloc/advanced-mover entities (→ T48/T59) and the `item_key` pickup side of the
  already-ported keylock (→ T35). Its source-map line (TODO ~646) is truncated mid-sentence.
- **T23** — should add the reorderable `cl_weaponpriority` list + a shared `DataSource` listbox; **move the language
  list (`languagelist.qc`) to T28** (it is i18n, not a data-list backend).
- **T25** — accurate; minor wording: `Bot/Waypoint.cs` already exists (parse/A*); T25 adds the editor-mutation +
  `waypoint_saveall` half, not a from-scratch file.
- **T29** — **omits the SCORE panel (#7)** (persistent on-HUD score/rank widget, on by default) — add it to the list.
- **T31** — broaden: the vehicle subsystem + the 8 minigame rule/AI engines carry comparable untested complexity but
  fall outside the current "parsers/builders/NPC" scope.

### Ported but previously untracked (the tables now reflect these are *done*, not gaps)
Faithful ports with **no T# attribution** before this audit — recorded so the tracker is honest: **all 12 untracked
gametypes** (Assault, ClanArena, FreezeTag, KeyHunt, Nexball, Invasion, Keepaway, TeamKeepaway, LMS, Survival, CTS,
Duel); the **Buffs** mutator; the **monster AI + 5 monsters**; the **vehicle** subsystem (ported, playable via T37); the
**8 minigame** rule engines + framework (ported, wired via T38); the **teamradar/RADAR** minimap; the **server
per-frame creature loop** (drown/contents/fall/regen); the **HavocBot AI body** (ported, ticked via T39); **effect +
notification net serialization**; and the **warpzone transform + teleport** core (combat traversal via T45).

---

## 🔴 P0 — Blocks a playable game from the menu

| ID | ☐ | Task → *done when* | Touches | Blocked-by |
|---|:--:|---|---|---|
| **T1** | ☑ | **[I] Wire netcode to the UI.** `Shell.OnConnect` builds a real `ClientNet`+`ClientEntityView`; add a host/listen-server launch path. → *Join a server from the menu and play (not just `--net-loopback`).* | `game/Shell.cs`, `game/net/{ClientNet,ServerNet,NetLoopback}.cs`, `Main.cs` | — |
| **T2** | ☑ | **[E] Weapon-fire driver state machine.** Port `W_WeaponFrame`/`weapon_prepareattack`/`ATTACK_FINISHED`; route primary+secondary+all slots; ammo gating. → *Weapons honor refire timing; secondary fires; dry-fire/auto-switch work.* | `src/XonoticGodot.Server/GameWorld.cs`, `src/.../Gameplay/Weapons/*.cs`, `.../Weapons/{EntityWeaponState,Inventory}.cs` | — |
| **T3** | ☑ | **[H] Mutator activation loop + fire dormant hooks.** Iterate `Registry<MutatorBase>`→`Hook()`; add the 9 zero-call `Call()` sites (esp. PlayerPhysics). → *Enabled mutators actually run in a match.* | `src/XonoticGodot.Server/{GameWorld,ClientManager,PlayerFrameLogic}.cs`, `src/.../Physics/PlayerPhysics.cs`, `.../Gameplay/Mutators/*`, `.../Damage/DamageSystem.cs` | — |
| **T4** | ☑ | **[K] View/camera subsystem (`view.qc`).** FOV+zoom, death/chase/spectator cam, damage red-flash + underwater tint. → *First-person feel: zoom works, death cam triggers, screen reacts to damage.* | `game/PlayerController.cs`, `game/client/ClientWorld.cs`, `game/GameDemo.cs` | — |
| **T5** | ☑ | **[J] Systematic visual QA.** → **A5: headless-assertable half DONE** — `VisualQaTests` (per-map load + object/brush counts, IQM/MD3/DPM skeleton+bind-pose, shader-compile) wired into `ci.sh`; `tools/visual-qa.sh` windowed driver + ../docs/RUNNING.md checklist. ⚠ **Windowed visual *correctness* (lightmaps/materials/patches/flares/rendered pose) still needs a manual eye-check** — Godot headless renders blank, so pixels can't be CI-verified. | `tests/.../VisualQaTests.cs`, `tools/visual-qa.sh`, `ci/ci.sh` | — |
| **T35** | ☑ | **[item] World-item spawn + touch pipeline.** No pickups appear on any map: `MapObjectsRegistry` registers zero `item_*`/`weapon_*` classnames and there is no `StartItem`/`Item_Touch`, so the ported `HealthItem`/`ArmorItem`/`AmmoItem` + `ItemPickupRules` engine is unreachable dead code. Port `StartItem`+`Item_Initialise`; register `item_health_{small,medium,big,mega}`/`item_armor_*`/ammo (`item_{shells,bullets,rockets,cells,fuel}`)/`weapon_*`/powerup (`item_strength`/`item_shield`/`item_invisible`/`item_speed`/`item_fuel_regen`/`item_jetpack`)/`item_buff_*`; wire world-item `Touch`→`ItemPickupRules.GiveTo` (touch gates, `ItemTouch`/`ItemTouched` hooks, loot toss/lifetime/spawn-shield, weapon-stay, autoswitch). → *Health/armor/ammo/weapon/powerup pickups exist + are collectable on stock maps.* Unblocks T19 random_items, T22 target_give/items. QC `server/items/items.qc` (`StartItem`:1007, `Item_Touch`:686), `server/items/spawning.qc`, `common/items/item/*.qh`, powerups `powerup/*.qh`, `buffs.qh`. | `src/.../Gameplay/Items/*`, `.../MapObjects/MapObjectsRegistry.cs`, `src/XonoticGodot.Server/GameWorld.cs` | — |
| **T36** | ☑ | **[G] Untracked-mode map-objective spawnfuncs.** Assault/Nexball/Invasion/CTS gametype *logic* is ported but their map entities never spawn (both `MapObjectsRegistry.RegisterAll` and `GameWorld.WireObjectiveSpawns` only handle CTF/Dom/Race/Onslaught) → those modes have **no win path on a real map**. Register + wire `target_objective`/`target_objective_decrease`/`func_assault_destructible`/`func_assault_wall`/`target_assault_roundstart`/`target_assault_roundend`/`info_player_attacker`/`info_player_defender`; `nexball_redgoal`/`bluegoal`/`fault`/`out` + the ball; `invasion_spawnpoint`/`invasion_wave`/`target_invasion_roundend`; `target_startTimer`/`target_stopTimer`; `item_kh_key`; `keepawayball`. QC `sv_assault.qc`/`sv_nexball.qc`/`sv_invasion.qc`/`sv_cts.qc`. | `src/.../Gameplay/MapObjects/MapObjectsRegistry.cs`, `src/XonoticGodot.Server/GameWorld.cs`, `.../GameTypes/{Assault,Nexball,Invasion,Cts}.cs` | — |
| **T37** | ☑ | **[veh] Vehicle runtime seam (board/input/impulse/return).** The whole Racer/Raptor/Spiderbot/Bumblebee port is dead code — nothing calls `EnterVehicle`/`GunnerEnter` and `Entity.VehInput` is never written, so a spawned vehicle can't be boarded or driven. Install a `vehicles_touch` (+`last_vehiclecheck` auto-enter + `vehicle_use` team-key) that calls `Enter`; copy the seated player's `MovementInput`/view/buttons into `VehInput` each tick; route impulses→`CycleMode`/`SetMode` (raptor bomb/flare, spiderbot volley/guided/artillery) + chase toggle; port `vehicle_use` team-gating/`ACTIVE_NOT`/`g_vehicles_delayspawn`/`vehicles_setreturn`; add `VehicleInit`/`Enter`/`Exit`/`Touch` mutator hooks. QC `common/vehicles/sv_vehicles.qc` (`vehicles_touch`:874, `vehicles_enter`:931, `vehicle_use`:522, `vehicle_impulse`:912). | `src/.../Gameplay/Vehicles/*`, `src/XonoticGodot.Engine/Simulation/*`, `game/net/{NetGame,ServerNet}.cs`, `.../Mutators/MutatorHooks.cs` | (after T14) |
| **T38** | ☑ | **[mg] Minigame activation (lifecycle + `cmd minigame` + client + menu).** All 8 minigame rule engines + a net codec + a board renderer are ported but inert — no session manager, no `cmd minigame create/join/invite/part`, and `MinigameNetState`/`MinigameRenderer.Show` have no caller. Port `sv_minigames.qc` session mgmt + the `minigame` client command; wire encode/decode + `Renderer.Show`/`OnMove`; port the in-game minigame HUD menu (`HUD_MinigameMenu*`/Help + per-game `menu_click`), renderer keyboard / two-tile / capture input (Pong/Snake/NMM/Peg unplayable otherwise), per-game `*_hud_status`, the invite notification, and Bulldozer on-disk levels + starter pack. → *Minigames are startable + playable in-game.* QC `common/minigames/{sv_minigames,cl_minigames,cl_minigames_hud,minigames}.qc` + `minigame/*.qc`. | `src/.../Gameplay/Minigames/*`, `game/hud/{MinigameRenderer,HudManager}.cs`, `game/net/MinigameNetState.cs`, `src/XonoticGodot.Server/Commands.cs` | — |

## 🟠 P1 — Faithful feel / important features

| ID | ☐ | Task → *done when* | Touches | Blocked-by |
|---|:--:|---|---|---|
| **T6** | ☑ | **[E] Reload + weapon-switch timing + selection.** `clip_load`/`clip_size`/`W_Reload`; `WS_RAISE/DROP` delays; cycle/impulse/start-weapon. | `src/.../Gameplay/Weapons/*`, `.../Weapons/{Inventory,EntityWeaponState}.cs`, `GameWorld.cs`, `src/XonoticGodot.Server/Commands.cs` | **T2** |
| **T7** | ☑ | **[G] Wire gametype score columns + sort keys.** Each mode writes its `GameScores` columns; `SetPrimary/SetSecondary`. → *Scoreboard shows caps/ticks/laps and sorts per-mode.* | `src/.../Gameplay/GameTypes/*.cs`, `.../Scoring/GameScores.cs`, `src/XonoticGodot.Server/Scores.cs` | — |
| **T8** | ☑ | **[G] Wire team-spawn selection.** Pass `teamCheck`/`p.Team`; detect `have_team_spawns`. | `src/XonoticGodot.Server/ClientManager.cs`, `src/.../Gameplay/Player/SpawnSystem.cs`, `.../GameTypes/*` | (coord T7) |
| **T9** | ☑ | **[K] Scoreboard depth.** Configurable `sbt_field` columns + per-mode cols + accuracy grid + map info + respawn + rankings. **+ Client column networking:** T7 runs the per-mode `ScoreRules` server-side only, so the client registry stays at default labels — `ScoreboardBlock.LayoutHash` won't match once the snapshot loop calls `Serialize`. Network the active label/flags set (or a mode id the client maps to the same `ScoreRulesBasics`). | `game/hud/ScoreboardPanel.cs`, `src/XonoticGodot.Net/ScoreboardBlock.cs` | **T7** |
| **T10** | ☑ | **[K] Missing match-critical HUD panels.** MAPVOTE, MODICONS, RACETIMER, CHECKPOINTS, VOTE, ITEMSTIME, PHYSICS/speedo. | `game/hud/*` (new panels), `game/hud/HudManager.cs` | — |
| **T11** | ☑ | **[H] Powerup effects + Nades.** Wire strength(×dmg)/speed/invis(alpha) consumers; build the nades subsystem (throw + 9 types). | `src/.../Gameplay/Player/StatusEffects.cs`, `.../Damage/DamageSystem.cs`, `.../Physics/PlayerPhysics.cs`, new `.../Gameplay/Nades/*` | **T3** |
| **T12** | ☑ | **[I] Antilag breadth + connect polish.** Rewind monsters+nades; measured RTT; prediction reads replicated movevars. | `game/net/ServerNet.cs`, `src/XonoticGodot.Net/AntilagBuffer.cs`, `game/net/ClientNet.cs` | (validate after T1) |
| **T13** | ☑ | **[J] Deluxemap fix + hero-material table + `_reflect`/`_shirt`/`_pants`.** | `src/XonoticGodot.Formats/Bsp/BspReader.cs`, `game/MapLoader.cs`, `game/assets/{ShaderCompiler,AssetSystem}.cs` | — |
| **T14** | ☑ | **[P] NPC map-placement spawnfuncs.** Register `monster_*`/`turret_*`/`vehicle_*`/`monsters_spawner`. → *Hand-placed NPCs appear on stock maps.* | `src/.../Gameplay/MapObjects/MapObjectsRegistry.cs`, `.../Monsters/*`, `.../Turrets/*`, `.../Vehicles/*`, `src/XonoticGodot.Server/GameWorld.cs` | — |
| **T15** | ☑ | **[L] Consume keybinds in gameplay + reconcile duplicate settings.** Wire the bind table into the controller; cvar store = single source of truth; quarantine legacy `MenuSettings`. | `game/PlayerController.cs`, `game/menu/{MenuSettings,SettingsScreen,KeyBindings}.cs` | (conflicts T4) |
| **T16** | ☑ | **[E] Headshot damage + per-weapon held-fire fidelity.** | `src/.../Gameplay/Weapons/{WeaponFiring,*}.cs`, `.../Damage/DamageSystem.cs` | **T2** |
| **T34** | ☑ | **[K] Reconcile the networked play-path presentation (T1↔T4 seam).** The menu "Start"/Create-Game/connect path runs through `game/net/NetGame.cs`, but T4's first-person feel was built into `GameDemo`/`PlayerController` (now reachable only via `--map`). Wired into NetGame so far: camera, the `*0.75` FOV, ViewEffects (damage-flash + liquid-tint), crosshair/health/radar. **Still missing on the real play path:** zoom (sample `InputButtons.Zoom` + apply to FOV), death/chase cam, the full HUD (weapon-bar/ammo/kill-feed/centerprint/announcer via `NotificationReceived`), and the first-person view-model/muzzle-flash. → *Factor a shared first-person-view component used by both `NetGame` and `GameDemo` (don't duplicate).* | `game/net/NetGame.cs`, `game/PlayerController.cs` (extract), `game/client/*`, `game/hud/*` | (after T10) |

| **T39** | ☑ | **[N] Wire HavocBot AI into the live `--host` loop.** `BotController`/`BotBrain.Think` (a complete `havocbot_ai` port) is never instantiated, so bots stand still — `--bots N` is inert. Construct a `BotController` in the server world, call `Frame()` each `StartFrame`, feed `WaypointNetwork.ForMap` on load, route the brain's input into the same movement/fire pipeline humans use; add danger detection (`havocbot_checkdanger` lava/slime/trigger_hurt/sky-edge), `navigation_unstuck`, and per-weapon `wr_aim` (bots never fire secondary today). Promotes the buried T33 prose note to a real task. QC `server/bot/default/{bot.qc,havocbot/havocbot.qc,navigation.qc}`. | `src/XonoticGodot.Server/{GameWorld,Bot/*}.cs`, `game/net/{NetGame,ServerNet}.cs` | (after T1) |
| **T40** | ☑ | **[K] Server combat-event emission (kill feed / centerprints / sounds / announcer).** The whole notification + sound registry is built but **never fired** in play — no kill feed, no frag/typefrag centerprint, no pain/death/impact sounds, no countdown/killstreak announcer. Port `Obituary()` message-selection (per-weapon `wr_killmessage`/`wr_suicidemessage` variants off the deathtype `HITTYPE` flags) + `Send_Notification(INFO/CENTER)`; emit pain/death/gasp/drown + armor/body-impact + gib player sounds at the damage/death sites; fire killstreak/multifrag/first-blood announcers + medals; emit warmup/round COUNTDOWN announcer + centerprint. QC `server/damage.qc Obituary`, `server/player.qc` (pain/impact), `common/weapons/weapon/*.qc wr_killmessage`, `notifications/all.inc`. | `src/XonoticGodot.Server/Scores.cs`, `src/.../Gameplay/Damage/DamageSystem.cs`, `.../Sounds/SoundSystem.cs`, `src/XonoticGodot.Server/WarmupController.cs`, `.../GameTypes/*` | — |
| **T41** | ☑ | **[K] Client feedback drivers (hitsound / footsteps / announcer / rings).** Client-derived feel cues are absent: `cl_hitsound` hit-confirm beep (needs a networked damage-dealt-total stat + `view.qc HitSound`), footstep/landing sounds (`PM_Footsteps`/`PM_check_hitground` in movement), the clock-driven announcer countdown VOICE (`announcer.qc` → 3-2-1 / prepare / N-min-remaining from `GAMESTARTTIME`/`ROUNDSTARTTIME`/`TIMELIMIT`), and the objective crosshair rings (`STAT(NADE_TIMER)`/`CAPTURE_PROGRESS`/`REVIVE_PROGRESS`). QC `client/view.qc HitSound`+`HUD_Draw`, `client/announcer.qc`, `common/physics/player.qc PM_Footsteps`. | `game/client/*`, `game/hud/{HudNotifications,CrosshairPanel}.cs`, `game/net/NetGame.cs`, `src/.../Physics/PlayerPhysics.cs` | (after T40) |
| **T42** | ☑ | **[G] Global overtime / sudden-death win layer (`CheckRules_World`).** Score modes latch `MatchEnded` on their frag/point limit only; a tied timed DM/TDM/CTF/Dom/KH ends in a **draw** instead of overtime/sudden-death, and `leadlimit` + the "N frags remaining" announcer are absent. Port `InitiateSuddenDeath`/`InitiateOvertime`/`checkrules_suddendeathend` + `timelimit_overtime[s]`/`timelimit_suddendeath`/`leadlimit` into a shared win-condition driver; score modes report tie/equality. QC `server/world.qc` (`CheckRules_World`:1725, `InitiateSuddenDeath`:1467, `InitiateOvertime`:1499, `WinningCondition_Scores`:1560). | `src/XonoticGodot.Server/GameWorld.cs`, `.../Gameplay/GameTypes/*.cs` | — |
| **T43** | ☑ | **[P] Monster damage→pain/death seam + reset.** `MonsterAI.MarkPain`/`MarkDead` are fully written but have **zero callers** — a monster victim falls through `DamageSystem.EventDamage` to `PlayerDamage`, so no monster pain/anim/sound, no loot drop, no gib threshold, no corpse/respawn, no `MonsterDies` hook, no `Monster_Heal` (Mage can't heal). Install a monster `event_damage` shim (armor-split → `mr_pain` → `MarkPain`/`MarkDead`); add `Monster_Reset` on round restart (deferred by T14); maintain global `monsters_total`/`monsters_killed` stats; `mr_deadthink`. QC `common/monsters/sv_monsters.qc` (`Monster_Damage`:1083, `Monster_Dead`:1036, `Monster_Reset`:999). | `src/.../Gameplay/Monsters/MonsterAI.cs`, `.../Damage/DamageSystem.cs`, `game/hud/ScoreboardPanel.cs` | (after T14) |
| **T44** | ☑ | **[H] Physics override hooks + spectator free-flight.** Two QC movement-tick mutator hooks are missing: `PM_Physics` (return-true fully replaces the move — **T3 claims this was wired but it was not**) and `IsFlying` (force the fly branch). Also observers are frozen at world origin — port the `!IS_PLAYER` branch `sys_phys_spectator_control` + `SPECTATORSPEED` impulse speed-ladder so spectators fly. `PM_Physics` unblocks bugrigs (T51). QC `ecs/systems/physics.qc:108/113`, `ecs/systems/sv_physics.qc:67`. | `src/.../Physics/PlayerPhysics.cs`, `.../Gameplay/Mutators/MutatorHooks.cs`, `game/net/ServerNet.cs` | — |
| **T45** | ☑ | **[I] Warpzone combat traversal + portal rendering.** The transform/teleport half is ported (you can walk through), but no trace recurses through zones, so on warpzone maps hitscan/projectiles/splash stop at the portal surface and the portal renders opaque. Port `WarpZone_TraceBox`/`TraceLine`/`TraceToss_ThroughZone` + `WarpZone_FindRadius` (+ RefSys/accumulator) into a `TraceService` extension and route `WeaponFiring`/`MoveTypePhysics`/`DamageSystem.RadiusDamage` through it; client portal render (Godot `SubViewport`) + `FixNearClip` + `View_Inside` + teleported view/input rotate. QC `lib/warpzone/{common,client,server}.qc`. | `src/XonoticGodot.Engine/Collision/*`, `.../MapObjects/Warpzone.cs`, `.../Weapons/WeaponFiring.cs`, `.../Damage/DamageSystem.cs`, `game/net/NetGame.cs` | — |
| **T46** | ☑ | **[L] Server chat engine (team/private/ignore/flood).** Only a raw `say` (broadcast-to-all, `teamOnly` hardcoded false) exists; `say_team`, `tell`, `ignore`/`unignore`/`clear_ignores`, per-say flood control, `g_chat_*` gates, magicear, and `formatmessage()` macros (`%d`/`%h`/`%l`) are absent — `say_team` is match-critical team feel. Port `server/chat.qc Say()` + register the cmds. QC `server/chat.qc`, `server/command/cmd.qc` (`say_team`/`tell`/`ignore`). | `src/XonoticGodot.Server/{Commands,Chat}.cs`, `game/net/ServerNet.cs` | — |
| **T47** | ☑ | **[I] Harden the client command bus (security).** `ServerNet.HandleClientCommand` runs **any** command name through the same table the server console uses — a connected client can invoke `ban`/`kick`/`gotomap`/`endmatch`/`settemp`. Add the QC `SV_ParseClientCommand` gate: per-client flood control (`sv_clientcommand_antispam_*`), UTF-8 validation, ban enforcement, and a server-only vs client-callable registry split (`reg.qh`). QC `server/command/cmd.qc SV_ParseClientCommand`, `server/command/reg.qh`. | `game/net/ServerNet.cs`, `src/XonoticGodot.Server/Commands.cs` | — |
| **T48** | ☑ | **[O] Map-entity content tail (hazard / props / ambient / music).** A swath of common map entities silently no-op: `misc_laser` (continuous-damage / detector beam — a real hazard), `misc_gamemodel`/`misc_models`/`misc_clientmodel` + dynamic `func_static`/`func_wall`/`func_illusionary`/`func_clientwall` (external decoration models — most stock maps have several, invisible today), `func_pointparticles`/`func_sparks` (ambient particles), `func_rain`/`func_snow` (weather), `target_speaker` (ambient/triggered sound), `target_music`/`trigger_music` (in-game map music). QC `common/mapobjects/{misc/laser.qc,models.qc,func/pointparticles.qc,func/rainsnow.qc,target/speaker.qc,target/music.qc}`. | `src/.../Gameplay/MapObjects/*`, `game/client/*`, `game/assets/models/*` | (after T20) |
| **T49** | ☑ | **[L] Single-player campaign wiring.** `SingleplayerScreen` ships a static 6-level array and the faithful `src/XonoticGodot.Server/Campaign.cs` core has **zero callers** — the campaign is non-functional end-to-end. Port `XonoticCampaignList` (read `maps/campaign*.txt` via `CampaignFile_Load`, track `g_campaign<name>_index`, render unlocked/current/future rows with checkmark + map preview + gametype icon, gate future levels, multi-campaign Next/Prev) and connect it to `Campaign.cs` so `CampaignSetup` boots the level. QC `menu/xonotic/campaign.qc`, `menu/xonotic/dialog_singleplayer.qc`, `common/campaign_file.qc`. | `game/menu/SingleplayerScreen.cs`, `src/XonoticGodot.Server/{Campaign,GameWorld}.cs` | — |
| **T50** | ☑ | **[L] Menu command dispatch + game menu + visual pickers.** `MenuCommand.Dispatch` ignores `directmenu`/`nexposee`/`closemenu`/`menu_show*` (bound buttons that open Servers/Profile/Settings/Guide/HUD-editor log inert); the ESC `PauseMenu` lacks Join!/Spectate (the only menu path to enter/leave the match), Leave-match, and Quick-menu; and the crosshair picker, 3D player-model preview, charmap, colorpicker, and reorderable `cl_weaponpriority` list are downgraded to text/number stand-ins. Port a by-name dialog registry + the gamemenu buttons + the picker widgets + a shared `DataSource` listbox. QC `menu/command/menu_cmd.qc`, `menu/xonotic/{dialog_gamemenu,crosshairpicker,playermodel,charmap,colorpicker,weaponslist,datasource}.qc`. | `game/menu/framework/*`, `game/menu/{PauseMenu,SingleplayerScreen}.cs`, `game/menu/dialogs/*` | — |
| **T66** | ☑ | **[item] Fire the `FilterItem` item-spawn hook.** `MutatorHooks.FilterItemDefinition` has **6 live subscribers** (Mayhem/TeamMayhem/Duel/HookMutator/MeleeOnly/NIX) but the chain is **never called** on world-item spawn (`StartItem.cs:78` proceeds unconditionally), so their item-filtering is dead code — **NIX still spawns every map item**, Duel doesn't block powerups, MeleeOnly doesn't strip health/armor, Mayhem/TeamMayhem powerup filter + Hook world-pickup suppress are inert. Fire the chain in `StartItem.SpawnInternal` *before* the have-pickup-item gate and abort+`delete` the spawn when it returns true (QC `startitem_failed`). Add a regression test (NIX strips a spawned `item_health`). *(Audit 2026-06-12.)* QC `server/items/items.qc:1031` (`if(MUTATOR_CALLHOOK(FilterItem, this))`). | `src/.../Gameplay/Items/StartItem.cs` | (after T35) |

## 🟡 P2 — Breadth & fidelity

| ID | ☐ | Task → *done when* | Touches | Blocked-by |
|---|:--:|---|---|---|
| **T17** | ☑ | **[G] Mayhem + Team Mayhem modes** (score = damage + frags), derive from DM/TDM. | new `src/.../Gameplay/GameTypes/{Mayhem,TeamMayhem}.cs`, `.../Registries.cs`, `GameWorld.cs` | — |
| **T18** | ☑ | **[G] Mode depth:** Onslaught CP/generator combat; CTF passing; Dom round variant; Race penalty/team/overtime. | `src/.../Gameplay/GameTypes/{Onslaught,Ctf,Domination,Race}.cs`, `.../Damage/DamageSystem.cs` | — |
| **T19** | ☑ | **[H] Remaining mutators** (weaponarena_random, new_toys, rocketminsta, rocketflying, vampirehook, stale_move_negation, random_items/gravity, globalforces, physical_items, spawn_*). | new `src/.../Gameplay/Mutators/*`, `.../Mutators/GameHooks.cs` | **T3** |
| **T20** | ☑ | **[K] effectinfo.txt-driven particles** + decals/casings/model-gibs (replace 13 heuristic classes). | `game/client/EffectSystem.cs`, new effectinfo parser | — |
| **T21** | ☑ | **[K] Crosshair true-aim coloring** (HITENEMY/TEAM/WORLD) + weapon-switch transition. | `game/hud/CrosshairPanel.cs` | — |
| **T22** | ☑ | **[O] Map-object tail:** logic-gate triggers (flipflop/monoflop/…), `target_*` utilities, `func_door_secret`/conveyor/ladder. | `src/.../Gameplay/MapObjects/{Triggers,MapObjectsRegistry,*}.cs` | — |
| **T23** | ☑ | **[L] Data-list backends:** screenshots/music/skins/stats lists, server-info popup, create-game map-info. *(Demos list → **T64**.)* | `game/menu/dialogs/*`, `game/menu/ServerBrowser.cs` | — |
| **T24** | ☑ | **[J] MDL importer (2026-07).** MDL is real loaded content: `casing_shell`/`casing_steel`/`gibs/chunk` are genuine Quake1 MDLs that spammed "not a known model" every boot and rendered only a placeholder. `MdlReader` (palette-decoded skin via embedded `host_quakepal` + anorms normals) + `MdlBuilder` (shared static mesh), dispatched by `AssetLoader.BuildModelFactory`. Guarded by `ModelImporterCoverageTests` + `MdlReaderTests` + the `VisualQaTests` MDL sweep. **⚠ The earlier "MDL/MD2/ZYM/PSK formally cut" decision was a mistake (not deliberately chosen) — reverted; the remaining MD2/ZYM/PSK importers are open work → see T72.** | `src/XonoticGodot.Formats/Mdl/*`, `game/loaders/{AssetLoader,models/MdlBuilder}.cs`, `tests/*` | — |
| **T72** | ☐ | **[J] MD2 / ZYM / PSK model importers (not yet implemented).** `AssetLoader.BuildModelFactory` dispatches by magic and implements IQM/DPM/MD3/MDL; MD2 (`IDP2`), ZYM (`ZYMOTICMODEL`), PSK (`ACTRHEAD`) fall through to null + a placeholder + a boot-log error (previously mislabeled a deliberate "formal cut" — reverted). **Content impact:** **ZYM** (DP `Mod_ZYMOTICMODEL_Load`, `model_alias.c`) — 2 real map props (`models/pomp/pomp.zym`, `models/train.zym` in the maps pk3) render as placeholders → port to make them show (*medium pri*); **MD2** (`Mod_IDP2_Load`) / **PSK** (`Mod_PSKMODEL_Load`) — no shipped Base content, mod/custom-map compat only (*low pri*). Mirror the MDL importer shape (`src/XonoticGodot.Formats/Mdl/` + `game/loaders/models/MdlBuilder.cs`). | `src/XonoticGodot.Formats/{Md2,Zym,Psk}/*`, `game/loaders/{AssetLoader,models/*}.cs`, `tests/*` | — |
| **T73** | ☐ | **[K] Real MDL models for legacy projectiles/props (opportunity, low-pri).** Now that MDL loads, the genuine projectile/prop MDLs (`plasma.mdl`, `rocketmissile.mdl`, `hagarmissile.mdl`, `spike.mdl`, `bullet.mdl`, `laser_dot.mdl`, `runematch/{rune,curse}.mdl`, `beam.mdl`) — currently drawn as `BodyFamily.GlowSprite`/procedural bodies in `ProjectileCatalog` — *could* use their real models. DP renders several of these as glow sprites too, so it's a per-projectile fidelity **judgement vs Base**, not a clear win; no regression today (those paths aren't loaded). | `game/client/{ProjectileCatalog,ProjectileRenderer}.cs` | — |
| **T74** | ☐ | **[J] Load-screen roster-warm alloc storm (~230MB main-thread frame).** The last deterministic single-frame allocation storm (byte-identical ~230MB in every 2026-07-03 stormkeep capture — `_scratch/perf_{sk_st0,parsepool,parsepool2,decodepool*}.json`, t≈4.4s, 2×gen2 + ~56ms GC pause; pins 0.1%low at 7fps every run). Cause: `PrecacheCombatSoundsAndModelsAsync` (`game/net/NetGame.cs` ~2201) warms the player roster via **synchronous main-thread `LoadSkeletalModel`, yielding only between models** — one frame carries a whole model's read+parse+anim-build+mesh (`iqm.mesh` scope-total 235ms > the 147ms frame span). The worker side is already fixed (IQM parse pooling + shared decode pool + bounded streamer lane, d7db8c9) — route the roster warm through that same lane (`ParseSkeletalModel` off-thread → `BuildSkeletalModel` under the streamer's main-thread budget), or slice the warm finer than per-model. ⚠ Validate via the perf report's **alloc-storm list**, not hitch trees — load-screen frames (t<~13s) write no hitch trees to the session log; and A/B with two runs (spawn-lottery fps noise, see ../docs/PERF-DEBUGGING.md). | `game/net/NetGame.cs`, `game/client/BackgroundAssetStreamer.cs`, `game/loaders/AssetLoader.cs` | — |
| **T25** | ☐ | **[N] Bot scripting (`bot_cmd`)** + waypoint-editor commands. | new `src/XonoticGodot.Server/Bot/*`, `src/XonoticGodot.Server/Commands.cs` | — |
| **T26** | ☑ | **[A] Activate the source generator** (or delete it): move analyzer ref to `XonoticGodot.Common.csproj`, cover Monster/Turret/Vehicle + catalogs, swap `Bootstrap()`→`RegisterAll()`. | `src/XonoticGodot.SourceGen/*`, `XonoticGodot.Common.csproj`, `src/.../GameInit.cs`, `.../Registries.cs` | — |
| **T51** | ☑ | **[H] Remaining mutators (wave 2) + Overkill weapons + doublejump fix.** Unported registered mutators: `hook` (`g_grappling_hook` spawn-with-offhand-grapple — a dead menu toggle today, **P1**), `damagetext` (floating damage numbers sv+cl, **P1**), `itemstime` backend (server `it_times[]` feed for the ITEMSTIME panel, which renders empty), `bugrigs` (needs T44 `PM_Physics`), `breakablehook`, `campcheck`, `kick_teamkiller`, `dynamic_handicap` (+ wire the inert basic handicap), `superspec`, `sandbox` (the `DialogSandboxTools` menu is dead). Plus: implement the 5 **Overkill** weapon classes (`okmachinegun`/`oknex`/`okshotgun`/`okhmg`/`okrpc` — referenced by name but resolve to null, so g_overkill grants nothing) and fix `sv_doublejump` (currently unconditional air-jump; QC needs the surface tracebox + velocity-clip). QC `common/mutators/mutator/{hook,damagetext,itemstime,bugrigs,breakablehook,campcheck,kick_teamkiller,dynamic_handicap,superspec,sandbox,doublejump,overkill}/*`. | new `src/.../Gameplay/Mutators/*`, `.../Weapons/*`, `game/client/*` | **T3**, T44 (bugrigs) |
| **T52** | ☑ | **[O] Compat entity remaps (Q3/QL/CPMA/Q1/WoP/Q3DF).** Only the entity-*removal* filter is ported; the *add* side is absent, so every weapon/item/armor/powerup on an imported Quake3/QuakeLive/CPMA/DeFRaG/Quake1/WoP map is an unhandled classname (geometry loads, no pickups). Port `SPAWNFUNC_Q3`/`WEAPON`/`ITEM` remaps (`weapon_railgun`→Vortex, `item_quad`→Strength, … + ammo `.count` scaling + SG↔MG arena swap) and the Q3DF `target_init`/`target_score`/`target_fragsFilter`/`target_print`. Depends on T35. QC `server/compat/{quake3,quake2,quake,wop}.qc`. | `.../MapObjects/MapObjectsRegistry.cs`, new `.../MapObjects/CompatRemaps.cs` | **T35** |
| **T53** | ☑ | **[G] Round/objective HUD stats for untracked modes.** The per-team alive counts (`REDALIVE`/`BLUEALIVE`/…), the `eliminatedPlayers` networked entity, KH `OBJECTIVE_STATUS`, and the Survival hunter-status entity exist only as comments, so MODICONS/spectator HUD won't reflect CA/FreezeTag/KeyHunt/Survival live state (T10's MODICONS feeds only tracked modes). Network these stats → `ModIconsPanel`. QC `sv_clanarena.qc CA_count_alive_players`, `sv_freezetag.qc`, `sv_keyhunt.qc kh_update_state`, `sv_survival.qc SurvivalStatuses_SendEntity`. | `.../GameTypes/{ClanArena,FreezeTag,KeyHunt,Survival}.cs`, `game/hud/ModIconsPanel.cs`, `game/net/*` | — |
| **T54** | ☑ | **[I] Movement/prediction breadth.** Only 8 of ~44 `MOVEVARS_*` stats are replicated, so a remote client whose cvars differ from the server (or any non-default air/aircontrol/warsowbunny/jumpspeedcap server) mispredicts/rubber-bands; per-client physics presets (`g_physics_clientselect` xonotic/nexuiz/cpma/xdf/…) and the `sentcvar`/`sendcvar` client-cvar replication (`cl_weaponpriority`/`cl_autoswitch`/`cl_noantilag`) are also absent (so server-side priority weapon-cycling has no client order). QC `common/stats.qh MOVEVARS_*`, `common/physics/player.qc Physics_ClientOption`, `server/command/cmd.qc sentcvar`. | `src/XonoticGodot.Net/{InputCommand,MoveVarsBlock}.cs`, `src/.../Physics/MovementParameters.cs`, `src/XonoticGodot.Server/Commands.cs` | — |
| **T55** | ☐ | **[O] Server-core fidelity gaps.** Forced teams (`g_forced_team_*` + `Player_GetForcedTeamIndex`) + the `sv_teamnagger` warmup nag are deferred (deterministic team setups + forced-spectator broken); the Porto in/out-portal placement + connected-pair teleport is unwired (`Porto.PortalSpawner`/`SpawnPortalAsWarpzone` have no caller); fall damage ignores active-grapple immunity + the `NODAMAGE` floor surface; lava doesn't ignite (`lava_burn`) and projectiles aren't damaged by lava/slime contents. QC `server/teamplay.qc`, `server/portals.qc`, `server/main.qc CreatureFrame_*`. | `src/XonoticGodot.Server/{Teamplay,PlayerFrameLogic}.cs`, `.../Weapons/Porto.cs`, `.../MapObjects/Warpzone.cs` | — |
| **T56** | ☑ | **[M] Generic + client + reply command parity.** Mostly-unported command families: `rpn` (the RPN-calculator VM stock cfgs/quickmenu rely on), `maplist add/cleanup/remove/shuffle`, `addtolist`/`removefromlist`, `qc_curl` (`generic.qc`); the client verbs `voice` (taunts), `suggestmap`, `physics`, `autoswitch`, `clientversion`; `defer <s> <cmd>`/`defer clear` (so passed `restart` votes actually fire); reply verbs `records`/`rankings`/`lsmaps`/`printmaplist`/`ladder` + `cvar_purechanges`; `editmob`/`spawnmob`/`killmob` (so the ported Monster-Tools dialog works). QC `common/command/{generic,rpn}.qc`, `server/command/{cmd,sv_cmd,common,getreplies}.qc`. | `src/XonoticGodot.Server/Commands.cs`, `src/XonoticGodot.Engine/Console/ConsoleCommands.cs` | — |
| **T57** | ☑ | **[E] Weapon tail.** `W_ThrowWeapon` (drop/throw current weapon, impulse 17 — recognised but no-op; also blocks pinata/on-death loot being collectable); per-shot/per-damage **accuracy** tracking (`accuracy_add` fired/hit/real → scoreboard accuracy% + XonStat read 0 today; `WeaponAccuracy` exists but is called only on kill-credit); per-weapon secondary depth (e.g. Vortex `chargepool` + velocity-charge `GetPressedKeys` hook + forced-reload). QC `server/weapons/{throwing,accuracy}.qc`, `server/weapons/tracing.qc:65`, `common/weapons/weapon/vortex.qc`. | `src/.../Gameplay/Weapons/*`, `.../Damage/DamageSystem.cs`, `src/XonoticGodot.Server/Scores.cs` | **T2**, T35 (loot) |
| **T58** | ☑ | **[K] csqcmodel client render hooks.** Player-model render features missing: `cl_forceplayermodels`/`forcemyplayermodel`/`forceplayercolors`/`forceuniqueplayercolors` (menu checkboxes that do nothing), model LOD distance swap (`_lod1`/`_lod2`), `cl_deathglow` + respawn-ghost corpse rendering, the `FallbackFrame` anim remap (prevents broken poses), and `CSQCModel_Effects_Apply` (`EF_FLAME`/`SHOCK`/`STARDUST`/`BRIGHTLIGHT` lights + `MF_ROCKET`/`GRENADE`/`GIB`→trail). QC `client/csqcmodel_hooks.qc`. | `game/client/{ModelAnimator,ModelTint,ClientWorld}.cs` | — |
| **T62** | ☐ | **[demo] Demo recording — `.xgd` format + recorder (always-on, client+server).** `DemoFormat` (header/frame/index; reuse `BitWriter`/`EntityStateCodec`/`SoundWire`) + `DemoRecorder : IDemoSink` (keyframe cadence + file writer) + client-side recorder tap + finalize-at-boundary; wire `DemoControl.StartRecording/StopRecording`; flip `sv_autodemo`/`cl_autodemo` defaults **on** (deliberate, comment it). *Done when* matches auto-write `.xgd` on both sides + headless round-trip test passes. Spec `specs/demo-replay-and-spectator.md` §4–5,11–13. Ref `Base/darkplaces/cl_demo.c`. | new `src/XonoticGodot.Net/DemoFormat.cs`, `src/XonoticGodot.Server/DemoRecorder.cs`, `src/XonoticGodot.Server/DemoControl.cs`, `game/net/{ServerNet,ClientNet}.cs` | — |
| **T63** | ☐ | **[demo] Replay host + time control + spectator cameras.** `GameWorld.ReplayMode` (match logic inert, spectator move kept) + `DemoPlayback : IReplayEntitySource` (playhead/`SampleAt`/seek/event-windowing/loop-sound reconstruct) + `ServerNet.ReplaySource` + `NetGame.ConfigureReplay` + `--playdemo`; two-clock pause/slow/fast/smooth-rewind/scrub + `ReplayControlBar`; `SpectatorCamera` (FreeFly/Follow/Director) + per-player view-angle record. **Perspective rules:** client demo locks to the recorded player's 1st-person; server demo = pick start perspective + switch live while watching; under capture the perspective is fixed at start. *Done when* `--playdemo` flies/follows/directs and scrub/slow-mo/rewind work. Spec §3,6–9,11–13. Ref `Base/darkplaces/cl_demo.c`, `qcsrc/client/{main,view}.qc`. | new `src/XonoticGodot.Server/DemoPlayback.cs`, `game/client/SpectatorCamera.cs`, `game/hud/ReplayControlBar.cs`; touch `game/net/{ServerNet,NetGame}.cs`, `src/XonoticGodot.Server/GameWorld.cs`, `game/client/{FirstPersonView,ClientWorld}.cs` | **T62** |
| **T64** | ☐ | **[demo] Demos menu backend** (supersedes the demo slice of **T23**): enumerate `demos/*.xgd` via VFS → filtered list; **Play** → `ConfigureReplay`; **Record to video** → FPS/resolution/format dialog → relaunch capture. *Done when* you can browse, play, and export a demo entirely from the menu. Spec §10 + `specs/video-capture.md` §3,6. | `game/menu/dialogs/DialogMediaDemo.cs` | **T63**, **T65** |
| **T65** | ☐ | **[demo] Video capture — perfect fixed-FPS render-to-file** (Godot Movie Maker / `MovieWriter`). `VideoCaptureSettings` (pure) + `VideoCaptureHook` (sibling of `ScreenshotHook`) + `Main` flags `--capture-video/-fps/-size/-format/-view` + Movie-Maker-mode-at-launch + quit-on-demo-end; **ffmpeg-optional** AVI→mp4/ogv transcode with honest fallback. *Done when* `--playdemo X --capture-video out.mp4` yields a smooth, audio-synced file. Spec `specs/video-capture.md` (Phases V0–V3). Ref `Base/darkplaces/cl_video.c` (`cl_capturevideo*`). | new `game/VideoCaptureHook.cs`, `src/XonoticGodot.Engine/Capture/VideoCaptureSettings.cs`; touch `Main.cs`, `src/XonoticGodot.Server/DemoPlayback.cs`, `../docs/RUNNING.md` | **T63** (demo→video) |
| **T67** | ☑ | **[E] HLAC crouch-spread modifier.** `Hlac.cs` seeds `SpreadCrouchmod` (primary 0.25 / secondary 0.5) but **never applies it** — spread ignores the ducked+grounded reduction (`Hlac.cs:149` flags the gap). `Machinegun.cs` already implements the identical `IS_DUCKED && IS_ONGROUND → spread *= spread_crouchmod` helper to mirror. *(Audit 2026-06-12.)* QC `common/weapons/weapon/hlac.qc:33-34` (primary), `:79-80` (secondary). | `src/.../Gameplay/Weapons/Hlac.cs` | — |
| **T68** | ☑ | **[K] `shownames` player-nametag overlay.** The `hud_shownames_*` menu cvars + quickmenu entry exist but there is **no client renderer** — the floating name + health/armor bar above players (team/LOS/distance/alpha filtering + the offscreen-arrow) is absent. Port `Draw_ShowNames`/`Draw_ShowNames_All`. *(Audit 2026-06-12.)* QC `client/shownames.qc`. | `game/client/*`, `game/hud/*` | — |

### 🎬 Demo replay & video-capture track (T62–T65)

> Self-contained feature — almost all new files plus a few netcode/menu seams. Specs:
> [`specs/demo-replay-and-spectator.md`](specs/demo-replay-and-spectator.md) +
> [`specs/video-capture.md`](specs/video-capture.md). **Decisions (2026-06-10):** always-on auto-record on
> **both** client & server (default on, one-cvar opt-out); the menu does **playback + video export**, not manual
> record; video capture rides **Godot Movie Maker (`MovieWriter`)** → AVI always, optional ffmpeg transcode to
> mp4/ogv. **Suggested order:** T62 → T63 → (T65 ∥ T64). T62/T65 can be prototyped against a live `--host` session
> before the replay host (T63) lands.

## ⚪ P3 — Long tail & polish

| ID | ☐ | Task → *done when* | Touches | Blocked-by |
|---|:--:|---|---|---|
| **T27** | ☑ | **[L] In-game HUD configure-mode editor** that *consumes* the `hud_panel_*` cvars (menu-side dialogs already done) + HUD skin-list backend. | `game/hud/*` (configure mode), `game/hud/HudManager.cs`, `game/menu/dialogs/DialogHudSetupExit.cs` | (better after T10) |
| **T28** | ☑ | **[L] Skin/theme system + localization (i18n)** — load `skinvalues.txt`/skinlist; wire gettext. | `game/menu/*`, localization infra | — |
| **T29** | ☑ | **[K] Remaining HUD panels** (CHAT, PRESSEDKEYS, ENGINEINFO, PICKUP, QUICKMENU, STRAFEHUD) + HUD layout cvars. | `game/hud/*` | — |
| **T30** | ☑ | **[C] Close the golden-trace loop** (capture from a *running* Darkplaces) + populate `trace_t.DpHitTextureName`. | `tools/movement-ref/*`, `tests/*`, `src/XonoticGodot.Engine/Collision/TraceService.cs`, `src/.../Services/Services.cs` | — |
| **T31** | ☑ | **[J/P] Test coverage** for BSP/MD3/DPM/sprite parsers, all Godot builders, the ~9k LOC of NPC code. (Model-info/locomotion-blend done.) | `tests/*` | — |
| **T32** | ☐ | **[M] Admin/debug console verbs** + GameTypeVote + map-vote network ballot UI. | `src/XonoticGodot.Server/{Commands,MapVoting,VoteController}.cs`, `game/hud/*` | — |
| **T33** | ☑ | **[project] CI pipeline** + GC/alloc perf pass + packaging/installers/dedicated-server distribution. | repo root, build config | — |
| **T59** | ☑ | **[O] Map-object long tail.** Rare/cosmetic map entities still unregistered: `func_stardust`, `dynlight` (scripted/moving lights), `trigger_viewlocation`/`target_viewlocation_*` (2.5D/over-shoulder camera regions), `misc_follow` (attach helper), `func_fourier`/`func_vectormamamam` (advanced movers), `target_voicescript`. QC `common/mapobjects/{func/stardust.qc,misc/dynlight.qc,trigger/viewloc.qc,misc/follow.qc,func/fourier.qc,func/vectormamamam.qc,target/voicescript.qc}`. | `src/.../Gameplay/MapObjects/*` | (after T48) |
| **T60** | ☐ | **[M] Admin / server-ops breadth.** Cheat impulses (clone / give-all #99 / teleport / r00t-nuke / speedrun) + the entity-drag subsystem; online ban-list federation (`g_ban_sync_uri` HTTP report/query); `sv_weaponstats` XonStat weapon-matrix upload; `sv_hitplot` aim-debug logging; vote arg restrictions (`sv_vote_command_restriction_*`) + per-command help. QC `server/cheats.qc`, `server/ipban.qc`, `server/weapons/{weaponstats,hitplot}.qc`, `server/command/vote.qc VoteCommand_checkargs`. | `src/XonoticGodot.Server/{Cheats,Bans,Commands,VoteController}.cs` | — |
| **T61** | ☑ | **[K] Feedback / notification polish.** Announcer queue + same-sample dedup + queuetime spacing (`Local_Notification_Queue_Process` — voices currently stomp); deathtypes as a registry with `.message` category (monster/turret/vehicle obituary phrasing + `DEATH_IS*`); `MSG_CHOICE` per-client cvar replication (verbose/terse frag messages); `status_effects` client networking (`StatusEffects_update` for burning/frozen overlays); voice-message/taunt routing (`_GlobalSound` VOICETYPE team-fan/taunt-anim/autotaunt/last-attacker). QC `notifications/all.qh`, `common/deathtypes/all.{qh,inc}`, `common/mutators/mutator/status_effects/*`, `common/effects/qc/globalsound.qc`. | `game/hud/HudNotifications.cs`, `src/.../Gameplay/{Damage/DeathTypes,Notifications,Sounds}/*` | (after T40) |
| **T71** | ☐ | **[K] Client-prediction / view-feel long tail** (2026-06-10 — the rest after projectile prediction + view-punch + hitmarker landed). **(a) View bob** — the whole camera bobbing on walk/run (`cl_bob*`, `qcsrc/client/view.qc`); the port bobs the *weapon model* but not the camera. **(b) Velocity zoom** — FOV widening with movement speed (`cl_velocityzoom`, `view.qc:GetCurrentFov`). **(c) Local movement-sound prediction** — footstep/jump/land played on the *predicting* client for immediacy. ⚠ Both Base and the port emit these server-authoritatively; on a listen server the authoritative tick already plays them immediately, so local prediction only helps a *pure remote client* (still a flat-floor stub) and would **double** the sound on `--host` — so this is **blocked on remote-client map replication**, not a standalone task. **(d) Gravity-bounce projectile prediction** — extend `ProjectilePredictor` with client-side gravity so grenade/mine/electro/hookbomb (`MOVETYPE_TOSS/BOUNCE`) get arc+bounce prediction too (today they're straight + snapshot-corrected `CollisionMode.None`); the gravity-free Stop/Bounce types already predict. **(e) Recoil for the remaining weapons** — overkill HMG/MG/Nex/RPC/Shotgun + vaporizer secondary still pass `recoil:0` to `SetupShot` (standard arsenal is wired). | `game/client/{FirstPersonView,ProjectileRenderer}.cs`, `src/XonoticGodot.Net/ProjectilePredictor.cs`, `src/.../Physics/PlayerPhysics.cs`, overkill weapons | (c) after remote-client collision |
| **T69** | ☑ | **[K] HUD dynamic shake on damage/event.** `hud_dynamic_shake`/`_*` are menu cvars with **no runtime** — the whole-HUD jitter on damage (and the damage-direction nudge) never plays. Port `HUD_Dynamic_Shake` (the damage-keyed offset/rotation applied to the HUD root). *(Audit 2026-06-12.)* QC `client/hud/hud.qc` (`HUD_Dynamic_Shake`). | `game/hud/HudManager.cs`, `game/hud/*` | (after T41) |
| **T70** | ☐ | **[M] Admin / debug / utility command tail.** Unported server verbs: `adminmsg`, `stuffto`, `radarmap`, `make_mapinfo`, `gettaginfo`, `printstats`, `database`, `delrec`, `effectindexdump`, `anticheat`, `bbox`, `trace` (`server/command/sv_cmd.qc`); client debug verbs `blurtest`/`boxparticles`/`create_scrshot_ent`/`debugmodel`/`localprint`/`mv_download`/`print_cptimes` (`client/command/cl_cmd.qc`). Mostly admin/debug; **partially overlaps T32/T60** — fold in or keep separate. *(Audit 2026-06-12.)* QC `server/command/sv_cmd.qc`, `client/command/cl_cmd.qc`. | `src/XonoticGodot.Server/Commands.cs`, `src/XonoticGodot.Engine/Console/ConsoleCommands.cs` | (coord T32/T60) |

---

## Suggested parallel waves (recompute from `Touches` each dispatch)

Illustrative — the orchestrator should re-derive disjoint, unblocked sets at dispatch time.

**✅ Completed:** **Wave 1** (T1·T2·T4·T7·T13·T20) · **Wave 2** (T3·T6·T8·T10·T12·T14·T21) · **Wave 3**
(T9·T11·T15·T16·T17·T18·T19·T22·T34) · **Wave A1** (T35·T40·T44·T38·T50·T58·T19c) · **Wave A2**
(T36·T43·T37·T51·T56·T28·T30) · **Wave A3** (T39·T57·T48·T53·T54·T26·T31·T33) · **Wave A4**
(T41·T42·T46·T52·T23·T27·T66·T67 + T24 cut) · **Wave A5**
(T45·T49·T47·T59·T29·T61·T5·T68·T69) — all landed + adversarially
reviewed, **build clean, 2681 tests + 686 VisualQA pass**. See the "Recently landed" blocks above + the
[§ Wave-1 fidelity audit](#-wave-1-fidelity-audit-2026-06-06--vs-base-source).

### Forward plan A1–A6 (open tasks: T5, T23–T33, T35–T61, T62–T71, carried T11c/T19c) — re-audit 2026-06-07; +T66–T71 from the 2026-06-12 audit

> **Parity-first (applies to every A-wave task):** start by reading the task's **§ Base/ source map** entry + the cited
> Base file(s) to confirm *exactly* how the feature behaves, then port it (see the mandatory protocol at the top of this
> file). Especially load-bearing here because most A-wave tasks *wire up already-ported code* — the agent must check the
> Base call-site/order to wire it faithfully, not just call the existing method.

**Model.** Within a wave no two tasks edit the same hot file's *logic*; registration tables
(`MapObjectsRegistry.RegisterAll`, `Registries`, per-mode `GameTypes/*`) are **additive-coordinated** (agents add
disjoint entries, orchestrator integrates — as Waves 1–3 did). Four chokepoints are held to **≤1 task/wave**:
`GameWorld.cs` (T35/36/39/42/49), the `DamageSystem.cs` damage pipeline (T40/43/45/57), `NetGame.cs` (T37/39/41/45),
and `Commands.cs`+`VoteController.cs` (T38/46/47/54/56/25/32/60 — the worst, 8 tasks).

> **A0 — optional decongestion (high leverage).** Split `GameWorld.cs`, `Commands.cs`, `MapObjectsRegistry.cs` into
> per-domain `partial class` files (no behavior change — the "consider splitting it" note in the conflict map). Removes
> the ≤1/wave limit on those files and lets the A6 command tail run fully parallel.

| Wave | Tasks (parallel) | Milestone | Why disjoint (chokepoint owner) |
|---|---|---|---|
| **A1 ✅** | **T35**·**T40**·**T44**·**T38**·**T50**·**T58**·**T19c** — **LANDED 2026-06-07** (build clean, 734 tests; see the "✅ Wave A1 landed" block above) | *A stock match feels alive*: floor pickups, kill-feed/sounds, spectator flight, minigames startable, menu nav | GW(T35)·DMG(T40)·PP/MH(T44)·CMD(T38)·CL(T58)·WP(T19c)·menu(T50) |
| **A2 ✅** | **T36**·**T43**·**T37**·**T51**·**T56**·**T28**·**T30** — **LANDED 2026-06-07** (build clean, 1046 tests; see the "✅ Wave A2 landed" block above) | *All modes have a win path; NPCs/vehicles work*: objective spawns, monster death, boardable vehicles, Overkill+doublejump+bugrigs+hook+damagetext+itemstime, defer/rpn/editmob, i18n+SkinValues, DpHitTextureName | GW/REG/GT(T36)·DMG(T43)·NG/SN(T37)·WP/CL(T51)·CMD(T56)·menu(T28) |
| **A3 ✅** | **T39**·**T57**·**T48**·**T53**·**T54**·**T26**·**T31**·**T33** — **LANDED 2026-06-10** (build clean, 1446 tests; see the "✅ Wave A3 landed" block above) | *Bots play; maps get props/ambience; accuracy works; per-mode round HUD; CI* | GW/NG(T39)·DMG/WP(T57)·REG/CL(T48)·GT(T53)·CMD(T54) |
| **A4 ✅** | **T41**·**T42**·**T46**·**T52**·**T23**·**T27**·**T66**·**T67** (+ **T24** cut) — **LANDED 2026-06-12** (build clean, 1735 tests; see the "✅ Wave A4 landed" block above) | *Faithful feel*: hitsound, OT, `say_team`, imported maps populated, item-filters live | NG/PP/HN(T41)·GW(T42)·CMD/SN(T46)·REG(T52)·menu(T23)·assets(T24)·Items(T66)·Weapons(T67) — T66/T67 fully disjoint, parallel-safe |
| **A5 ✅** | **T45**·**T49**·**T47**·**T59**·**T29**·**T61**·**T5**·**T68**·**T69** — **LANDED 2026-06-12** (build clean, 2681 tests + 686 VisualQA; see the "✅ Wave A5 landed" block above) | *Portals, campaign, security, polish*: combat through portals, SP works, clients can't run admin verbs, nametags + HUD shake | DMG/WZ/NG(T45)·GW(T49)·CMD/SN(T47)·REG(T59)·HN(T61)·client(T68/T69 — T69 after T41) |
| **A6** | **T55** server fidelity (Porto, needs T45) · **T11c** nade-throw input · **T25** bot scripting+wpeditor · **T32** GameTypeVote+mapvote ballot · **T60** admin/ops breadth · **T70** admin/util cmd tail | *Command/admin tail*: Porto portals, throwable nades, bot_cmd, vote ballot UI, admin verbs | T25/T32/T60/T70 share `Commands.cs`/`VoteController` → coordinate-additive, or **A0 → fully parallel** |

**Playable-from-menu** is reached after **A1+A2** (all P0 T35–T38 + the highest-impact P1 activations) — pickups,
combat feedback, working bots/NPCs, and objective win paths on stock maps. A3–A4 = faithful feel + breadth; A5–A6 =
polish, security, long tail. Dependency edges honored: T41/T61←T40, T52/T57←T35, T51-bugrigs←T44, T59←T48, T55-Porto←T45.

**Demo replay & video capture (T62–T65)** is an **independent track** that sidesteps the A-waves' chokepoint model —
its `Touches` are mostly *new* files (`DemoFormat`/`DemoRecorder`/`DemoPlayback`/`SpectatorCamera`/`VideoCaptureHook`)
with thin seams in `ServerNet`/`NetGame`/`DialogMediaDemo`. Run it as its own mini-wave in the documented order
(T62 → T63 → T65 ∥ T64); see the two demo specs and the
[§ Demo replay & video-capture track](#-demo-replay--video-capture-track-t62t65) note above.

---

## 🔎 Wave-1 fidelity audit (2026-06-06) — vs Base source

Each Wave-1 task was re-audited against its Base source above (independent verify → adversarial confirm, refute-by-
default). **All headline design claims hold and there are 0 blockers** — the architecture is faithful. But the confirm
pass validated **42 real fidelity gaps** (11 major / 24 minor / 7 nit). Majors that affect the *normal* play path are
remediation candidates; structural ones are folded into the tasks noted. Severity is the post-confirm re-rating.

> **✅ REMEDIATED (2026-06-06).** All 42 gaps were fixed via a 6-agent fan-out (35 done / 3 partial / 0 skipped on the
> first pass), then an adversarial review of the edits found 13 follow-on issues (2 major / 10 minor / 1 nit) — 11
> fixed, 1 left as a documented simplification. Build clean, **264 tests pass**, and the play path verified headless
> (`--host atelier --bots 2`, no-net `--map`, listen-server connect). **Partials carried forward (NOT fully closed):**
> (1) **A2** — frozen-fire gate covers the reachable freeze case; `player_blocked` / round-not-started / `ForbidWeaponUse`
> need server-side round_handler state (→ T3/round modes). (2) **D1** — two-slot team scoring migrated CTF/Dom/KH/TDM;
> ClanArena/FreezeTag/TeamKeepaway/Nexball/Onslaught/Assault still on private dicts (→ T9 / their mode tasks). (3) **E1** —
> deluxe angle-rescale done; the `dot(N,lightdir)` bump term needs mesh tangents (→ T13 follow-up). Left as documented
> simplification: **A4** Crylink dead-group head re-seat. Out-of-scope perf note: headless bot AI was ~0.46s/frame/2-bots
> (navigation/tracewalk) — **root-caused + fixed in T33's perf pass** (see T33 notes): the shipped
> `.waypoints`/`.cache` parsed to **zero** nodes (vtos single-quote vector literals weren't stripped) so every bot
> fell back to the ~219ms O(N²) AutoLink tracewalk graph build; fixing the parser + completing `ForMap` (load the
> precompiled link cache instead of AutoLink) + a sort/early-exit `Nearest` (29×) + pooled A* buffers drops the bot
> think to ~0.04 ms/bot/tick and the graph build to ~8 ms. (AI think itself is not yet wired into the live
> `--host` server loop — see T33.)

### T1-netcode-ui-audit — minor-gaps — 5 confirmed (1 major)
- **[major]** Clients connect straight into play — Xonotic's connect-as-Observer/Join phase is skipped
  - base `qcsrc/server/client.qc:ClientConnect` → port `XonoticGodot/src/XonoticGodot.Server/ClientManager.cs:159`
  - In Base, ClientConnect ENDS by transmuting the new client to Observer (a spectator); a human only becomes a live Player by explicitly Join-ing (ObserverOrSpectatorThink: press jump/fire → TRANSMUTE(Player)+PutClientInServer; bots auto-join via autojoin_checked). The port's ClientManager.ClientConnect unconditionally c…
- **[minor]** Debug print leftovers ship on the spawn + net play paths (stdout spam every spawn / 2x per second)
  - base `qcsrc/server/client.qc:PutPlayerInServer` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs:151`
  - Three TEMP-DEBUG blocks are unconditional (no #if DEBUG / autocvar gate) and run on the shipping net/listen-server path. SpawnSystem.SelectSpawnPoint prints every gathered spawn entity ([DBG-SPOT]) on EVERY spawn selection, and PutPlayerInServer prints [DBG-SPAWN] on every (re)spawn — both via Console.WriteLine. NetGa…
- **[minor]** Wish-move scaled by a hardcoded 360, not live sv_maxspeed — caps true top speed when sv_maxspeed > 360
  - base `qcsrc/server/client.qc:PutPlayerInServer` → port `XonoticGodot/game/net/EntityMovementStep.cs:43`
  - Both the client predictor (EntityMovementStep) and the server input converter (ServerNet.ToMovementInput) rescale the normalized ±1 wish-move by MovementParameters.Defaults.MaxSpeed (a hardcoded 360), while the actual movement reads the LIVE sv_maxspeed via MovementParameters.FromCvars() each tick and clamps wishspeed…
- **[minor]** QC ClientConnect/ClientDisconnect player-visible side-effects absent on the net path
  - base `qcsrc/server/client.qc:ClientDisconnect` → port `XonoticGodot/game/net/ServerNet.cs:350`
  - Several QC connect/disconnect effects a player would notice are not reproduced. (a) Base ClientDisconnect emits Send_Effect(EFFECT_SPAWN, this.origin, ...) — the teleport-out particle puff when a player leaves — and Send_Notification(INFO_QUIT_DISCONNECT); the port's ServerNet.OnPeerDisconnected → ClientManager.Client…
- **[nit]** Listen server spawns the human's authoritative Player only on ENet accept; the prediction carrier sits at world origin until the first snapshot
  - base `qcsrc/server/client.qc:PutClientInServer` → port `XonoticGodot/game/net/NetGame.cs:304`
  - On a listen server there are two representations of the local human: the authoritative Player (created server-side in ServerNet.HandleAuth → ClientManager.ClientConnect, only after the async ENet+auth handshake completes) and the client-side prediction carrier (NetGame.SpawnCarrier, 'client_predict', SOLID_NOT). The c…

### T2-weapon-fire-driver — major-gaps — 8 confirmed (5 major)
- **[major]** Firing does not remove the spawn shield (weapon_prepareattack_do drops STATUSEFFECT_SpawnShield)
  - base `qcsrc/server/weapons/weaponsystem.qc:weapon_prepareattack_do` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/WeaponFireGate.cs:88-91`
  - QC clears the spawn shield the instant a player fires, so you cannot shoot from behind spawn protection. The port's PrepareAttack deliberately skips this, asserting the spawn-shield system handles it — but the only remover (RemoveSpawnShield) is private to DamageSystem and is never invoked on the fire path. Net effect…
- **[major]** Frozen / player_blocked players can still fire (weaponLocked not enforced on the weapon path)
  - base `qcsrc/server/weapons/weaponsystem.qc:W_WeaponFrame (weaponLocked)` → port `XonoticGodot/src/XonoticGodot.Server/GameWorld.cs:693-705`
  - QC W_WeaponFrame calls weaponLocked() (true when StatusEffects_active(STATUSEFFECT_Frozen) or player.player_blocked, etc.) and, if locked and state!=WS_CLEAR, only calls w_ready (no fire). The port runs the entire WeaponFireDriver.Frame whenever `!gameStopped && !p.IsDead`, with no frozen/player_blocked gate. The move…
- **[major]** Electro secondary fires all orbs at once instead of one-per-frame via W_Electro_CheckAttack
  - base `qcsrc/common/weapons/weapon/electro.qc:wr_think (fire & 2) + W_Electro_CheckAttack` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/Electro.cs:143-148`
  - QC fires the first orb, sets electro_count = count, and schedules W_Electro_CheckAttack which fires the remaining (count-1) orbs on successive held ticks (each --electro_count, re-scheduled by animtime) — a stream of orbs while the button is held. The port fires all `count` orbs in a single instant from one origin/aim…
- **[major]** Crylink link-join converges on a fixed timer, not on button release (loses the weapon's core mechanic)
  - base `qcsrc/common/weapons/weapon/crylink.qc:wr_think (crylink_waitrelease release branch)` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/Crylink.cs:129-143`
  - The Crylink's defining behavior (per its own in-game description) is: hold fire → spikes spread; RELEASE → they converge (W_Crylink_LinkJoin) for burst damage at a moment the player chooses. QC triggers the join on `crylink_waitrelease==1 && !(fire & 1)` (the release edge). The port instead converges automatically onc…
- **[major]** Shotgun primary/secondary share ATTACK_FINISHED — melee can no longer follow a blast immediately
  - base `qcsrc/common/weapons/weapon/shotgun.qc:wr_think (shotgun_primarytime)` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/Shotgun.cs:101-128`
  - QC deliberately keeps the shotgun primary refire on a SEPARATE field (shotgun_primarytime) and passes only animtime as the attacktime into weapon_prepareattack, with the explicit comment 'handle refire separately so the secondary can be fired straight after a primary'. So ATTACK_FINISHED moves only by the short animti…
- **[minor]** MachineGun secondary burst fires all rounds in one instant instead of spacing them by burst_refire
  - base `qcsrc/common/weapons/weapon/machinegun.qc:W_MachineGun_Attack_Burst` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/Machinegun.cs:178-199`
  - QC fires ONE burst round, then self-reschedules weapon_thinkf(..., burst_refire, W_MachineGun_Attack_Burst) so the `burst` rounds leave the barrel ~0.06s apart (with aim drift between them), and only after misc_bulletcounter counts up to 0 does it apply burst_refire2 (0.45s) and return to ready. The port fires the ent…
- **[minor]** Per-weapon cross-mode refire interlocks dropped (electro_secondarytime+refire2; shotgun_primarytime)
  - base `qcsrc/common/weapons/weapon/electro.qc:wr_think (electro_secondarytime gates)` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/Electro.cs:130-150`
  - QC Electro gates the PRIMARY bolt on `time >= electro_secondarytime + refire2` (and secondary on `+ refire`), so you cannot instantly bolt right after lobbing orbs (and vice versa). The port has no electro_secondarytime field/logic; primary and secondary are independently refire-gated, allowing a faster bolt-after-orb…
- **[minor]** MachineGun sustained spread ignores the crouch spread modifier
  - base `qcsrc/common/weapons/weapon/machinegun.qc:W_MachineGun_Attack_Auto` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/Machinegun.cs:155-175`
  - QC multiplies the sustained-fire spread_accuracy by spread_crouchmod when the player is ducked and on the ground (tighter spread while crouched). The port's AttackAuto computes spread = spread_min ± accum and never applies the crouch modifier (SpreadCrouchmod is read into Cvars but only the legacy decay path uses the …

### T4-view-camera — major-gaps — 11 confirmed (1 major)
- **[major]** Zoom default magnitude wrong: cl_zoomfactor fallback AND seeded value 2.5 vs Base default 5
  - base `data/xonotic-client.cfg:59 (view.qc:GetCurrentFov line 501-503)` → port `XonoticGodot/game/PlayerController.cs:179,328-329`
  - GetCurrentFov reads autocvar_cl_zoomfactor with an in-code clamp default of 2.5 ONLY when out of [1,30]; Base's actual cvar default is 5. The port not only falls back to 2.5 (UpdateZoom) but actively seeds the cvar to "2.5" via Cvars.Register in _Ready — so even when the value IS set it is set wrong. A full +zoom reac…
- **[minor]** Damage flash ~5x too strong: hud_damage_factor fallback 0.13 vs Base default 0.025
  - base `data/_hud_common.cfg:303 (read by view.qc:HUD_Damage line ~1254)` → port `XonoticGodot/game/client/ViewEffects.cs:58`
  - view.qc computes myhealth_flash += dmg_take * autocvar_hud_damage_factor. Base's shipped default for hud_damage_factor is 0.025. The port hardcodes the fallback at 0.13 — ~5.2x larger — so every hit produces a vastly stronger red flash than Base whenever the cvar is unset (standalone/headless GameDemo, where no cfg po…
- **[minor]** hud_damage master multiplier fallback 1.0 vs Base default 0.55
  - base `data/_hud_common.cfg:297 (view.qc:HUD_Damage line ~1304/1307)` → port `XonoticGodot/game/client/ViewEffects.cs:62`
  - The final drawn alpha is multiplied by autocvar_hud_damage (the master flash strength). Base ships 0.55; the port's fallback is 1.0, making the flash nearly 2x more opaque on top of the already-too-strong factor. Compounds finding #1.
- **[minor]** Damage flash decays at the wrong rate: hud_damage_fade_rate fallback 0.5 vs Base 0.75
  - base `data/_hud_common.cfg:304 (view.qc:HUD_Damage line ~1252)` → port `XonoticGodot/game/client/ViewEffects.cs:60`
  - myhealth_flash fades by autocvar_hud_damage_fade_rate * frametime each frame, and the same rate drives the dead-state ramp-up. Base ships 0.75/s; the port falls back to 0.5/s, so the flash lingers ~50% longer and the death fade ramps slower than Base when the cvar is unset.
- **[minor]** hud_damage_maxalpha fallback 1.0 vs Base default 1.5
  - base `data/_hud_common.cfg:305 (view.qc:HUD_Damage line ~1254)` → port `XonoticGodot/game/client/ViewEffects.cs:59`
  - myhealth_flash is clamped to hud_damage_maxalpha. Base allows the accumulator to climb to 1.5 (so successive hits stack a deeper reserve that fades over more frames); the port caps it at 1.0, changing the saturation/persistence behavior on heavy damage.
- **[minor]** Near-death pulsating pain-threshold lowering not ported (HUD_Damage)
  - base `qcsrc/client/view.qc:HUD_Damage (lines 1256-1264)` → port `XonoticGodot/game/client/ViewEffects.cs:136-183 (UpdateDamage)`
  - When myhealth < hud_damage_pain_threshold_lower_health (Base 50), view.qc lowers pain_threshold by a sin-pulsating amount (hud_damage_pain_threshold_lower 1.25, pulsating_min 0.6, period 0.8) — driving pain_threshold negative so a permanent throbbing red overlay appears at low health. The port's UpdateDamage has no eq…
- **[minor]** cl_eventchase_viewoffset "0 0 20" lift entirely omitted from death-cam
  - base `qcsrc/client/view.qc:View_EventChase (lines 811-828) + xonotic-client.cfg:219` → port `XonoticGodot/game/PlayerController.cs:419-471 (ApplyEventChase)`
  - Before pulling the camera back, view.qc raises the origin by cl_eventchase_viewoffset (default '0 0 20'): it traces up by view_offset (+ maxs.z) and lifts current_view_origin.z to that. The port's ApplyEventChase starts the pull-back from the RAW eye with no viewoffset lift at all, so the Base death-cam sits ~20 units…
- **[minor]** Eventchase trace box is ±8 on all axes; Base mins/maxs are ±12 horizontal, ±8 vertical
  - base `data/xonotic-client.cfg:217-218 (view.qc:View_EventChase line 856)` → port `XonoticGodot/game/PlayerController.cs:459`
  - view.qc box-traces the chase camera with cl_eventchase_mins/maxs = '-12 -12 -8' / '12 12 8'. The port hardcodes mins=(-8,-8,-8), maxs=(8,8,8), so the camera keeps only 8u of horizontal clearance from walls instead of 12u — the camera will get closer to / clip side geometry differently than Base. (Vertical ±8 matches; …
- **[minor]** cl_spawnzoom spawn-zoom-in effect (Base default ON) has no port equivalent
  - base `qcsrc/client/view.qc:GetCurrentFov (lines 512-520) + xonotic-client.cfg:56-58` → port `XonoticGodot/game/PlayerController.cs:324-363 (UpdateZoom)`
  - GetCurrentFov has a spawn-zoom branch: when cl_spawnzoom (Base default 1 = ON) and zoomin_effect, it eases current_viewzoom out from 1/spawnzoomfactor to 1 on spawn (a brief zoom-in-then-settle). The port's UpdateZoom models only the held-button zoom; there is no spawn zoom state, so the characteristic Xonotic spawn z…
- **[nit]** Eye contents sampled at fixed eye, not at the rendered view_origin (diverges during chase)
  - base `qcsrc/client/view.qc:HUD_Contents (line 1176)` → port `XonoticGodot/game/PlayerController.cs:312-316 (SampleEyeContents)`
  - view.qc does pointcontents(view_origin), where view_origin is the FINAL render origin — during event-chase that is the pulled-back camera position, so the liquid tint reflects where the camera actually is. The port always samples at Player.Origin + eye height (the first-person eye), so when the death/chase camera pull…
- **[nit]** Damage tint color (0.8,0,0) vs Base hud_damage_color "1 0 0"
  - base `data/_hud_common.cfg:302` → port `XonoticGodot/game/client/ViewEffects.cs:73`
  - Base's hud_damage_color default is pure red "1 0 0"; the port hardcodes DamageColor = (0.8, 0, 0), a slightly darker red, and (unlike the contents path) never reads hud_damage_color from the cvar store, so even a loaded cfg can't correct it. Subtle color mismatch.

### T7-gametype-score-columns-sort-keys — minor-gaps — 5 confirmed (2 major)
- **[major]** AddToPlayer hard-wires `forced = true` whenever a hook is installed, so game_stopped never zeroes the delta
  - base `qcsrc/server/scores.qc:PlayerScore_Add` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Scoring/GameScores.cs:311-328`
  - QC: `mutator_returnvalue = MUTATOR_CALLHOOK(AddPlayerScore,...); if(!mutator_returnvalue && game_stopped) score = 0;` — the game_stopped clamp is bypassed ONLY when the hook actually returns true (a handler claimed the event). The port computes `forced = newDelta != delta || true;` which is unconditionally true, then …
- **[major]** Team-score layer collapsed to one flat int per team — no ST_SCORE+ST_<mode> two-slot model, no team primary/secondary or per-slot label/flags
  - base `qcsrc/common/scores.qh:149-158 / server/scores.qc:TeamScore_Compare,ScoreInfo_SetLabel_TeamScore` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Scoring/GameScores.cs:31-32,348-378,483-489`
  - QC teamscores are a 2-element array (MAX_TEAMSCORE=2): ST_SCORE plus a gametype slot (ST_CTF_CAPS / ST_DOM_TICKS / ST_DOM_CAPS / ST_KH_CAPS / ST_ONS_GENS / ST_NEXBALL_GOALS / ST_FT_ROUNDS / ST_CA_ROUNDS / ST_RACE_LAPS / ST_ASSAULT_OBJECTIVES), each with its own teamscores_label/teamscores_flags and a teamscores_primar…
- **[minor]** PlayerScore_Sort spectator exclusion (nospectators / FRAGS_SPECTATOR) not reproduced
  - base `qcsrc/server/scores.qc:PlayerScore_Sort:751-761` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Scoring/GameScores.cs:463-477 / XonoticGodot/src/XonoticGodot.Server/Scores.cs:537-543`
  - QC PlayerScore_Sort takes a `nospectators` flag and skips any client whose frags == FRAGS_SPECTATOR when building the sort chain. The port's SortPlayers/Sorted/Leader take whatever Entity/PlayerScoreRow collection they are handed and never filter spectators, so a spectator with a leftover score row can be ranked / ret…
- **[minor]** Domination always declares the DOM_TICKS column, even in the roundbased branch where QC declares only DOM_TAKES
  - base `qcsrc/common/gametypes/gametype/domination/sv_domination.qc:ScoreRules_dom:533-555` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/GameTypes/Domination.cs:127-134`
  - QC ScoreRules_dom has two arms. Roundbased: GameRules_scoring(teams, SFL_SORT_PRIO_PRIMARY, 0, { field_team(ST_DOM_CAPS,'caps',PRIMARY); field(SP_DOM_TAKES,'takes',0); }) — note NO SP_DOM_TICKS column and the team metric is ST_DOM_CAPS. Non-roundbased: declares ST_DOM_TICKS/SP_DOM_TICKS/SP_DOM_TAKES with the disable_f…
- **[nit]** RegisterAll default labels/flags for several gametype columns diverge from QC (dead but misleading; relied on only if a mode forgets to DeclareColumn)
  - base `qcsrc/common/gametypes/gametype/keepaway/sv_keepaway.qh:17-19 (and kh/ons/tka/surv per-mode field() calls)` → port `XonoticGodot/src/XonoticGodot.Common/Gameplay/Scoring/GameScores.cs:85-110`
  - GameScores.RegisterAll bakes label/flag defaults that do not match the per-mode QC field() registrations: KEEPAWAY_BCTIME is given label "bckills" + HideZero (QC: 'bctime', flags 0, no Time), KH_DESTRUCTIONS label "destroyed" (QC kh field uses 'destructions'), TKA_BCTIME given HideZero|Time (QC: 'bctime', SECONDARY on…

### T13-deluxemap-materials — minor-gaps — 3 confirmed (0 major)
- **[minor]** Deluxemap direction data decoded but never used for lighting (r_shadow.c normal-lighting unimplemented)
  - base `Base/darkplaces/r_shadow.c (deluxe normal lighting) + Base/darkplaces/gl_rsurf.c:137 (surface->deluxemaptexture used) + Base/darkplaces/model_brush.c:5807 (surface->deluxemaptexture assigned)` → port `XonoticGodot/src/XonoticGodot.Formats/Bsp/BspData.cs:96 (Deluxemaps stored), XonoticGodot/game/assets/LightmapShader.cs:49-57 (fragment never samples a deluxemap)`
  - The task's canonical source explicitly names r_shadow.c 'deluxe normal lighting' and gl_rsurf.c consumes surface->deluxemaptexture as the per-texel light direction for normal-mapped/bumped lighting. The port de-interleaves the deluxe pages into BspData.Deluxemaps but no code path ever reads them — a project-wide grep …
- **[minor]** External-lightmap (lm_NNNN) maps skip deluxemap de-interleaving entirely
  - base `Base/darkplaces/model_brush.c:5577-5611 (external lightmap load) + 5628-5677 (deluxe detect runs on the external set with bytesperpixel=4)` → port `XonoticGodot/src/XonoticGodot.Formats/Bsp/BspReader.cs:92 (split runs only on internal rawLightmapPages), XonoticGodot/game/MapLoader.cs:296-309 (external lm_NNNN loaded by raw face index)`
  - In DP, when a map has no internal lightmaps, the lm_%04d external set is loaded AND the same deluxemap detection/de-interleave runs over it (it uses count/size from the external images). The port only runs DetectAndSplitDeluxemaps over the internal lump (rawLightmapPages from lump 14); for an external-lightmap map tha…
- **[minor]** Static-only 'tcMod scale' is silently dropped (comment claims Uv1Scale handles it, but it is never set)
  - base `Base/darkplaces/model_shared.c:1800 (Q3TCMOD_SCALE) — scale multiplies the stage's texcoords` → port `XonoticGodot/game/assets/ShaderCompiler.cs:299-302 (NeedsAnimatedShader skips scale), :153-181 BuildSingleStandard / :218-249 BuildChainStage (no Uv1Scale set)`
  - A stage whose only tcMod is a static `scale sx sy` (no scroll/rotate/stretch/turb) is routed to the StandardMaterial3D path because NeedsAnimatedShader returns false for a lone Scale. The comment at ShaderCompiler.cs:300 says 'A static scale alone doesn't need a custom shader (Uv1Scale handles it)', but neither BuildS…

### T20-effectinfo-particles — major-gaps — 10 confirmed (2 major)
- **[major]** underwater / notunderwater block gating is parsed but never applied at spawn
  - base `Base/darkplaces/cl_particles.c:CL_NewParticlesFromEffectinfo:1625-1628` → port `XonoticGodot/game/client/EffectSystem.cs:567-623 (BuildFromInfo block loop)`
  - DP computes `underwater` from CL_PointSuperContents(center) and skips any block flagged PARTICLEEFFECT_UNDERWATER when not underwater and PARTICLEEFFECT_NOTUNDERWATER when underwater. The port parses these into info.Underwater/NotUnderwater (EffectInfo.cs:245-246) but BuildFromInfo's block loop never references them, …
- **[major]** Fractional `count` over-spawns: persistent particleaccumulator replaced by ceil() floored at 1
  - base `Base/darkplaces/cl_particles.c:CL_NewParticlesFromEffectinfo:1710-1754` → port `XonoticGodot/game/client/EffectSystem.cs:642-647 (BuildInfoBurst count)`
  - DP accumulates the fractional spawn count into a persistent per-info field (`info->particleaccumulator += cnt`, then `for (;particleaccumulator >= 1; particleaccumulator--)`), so a block with `count 0.5` averages ~0.5 particles per call and `count 0.025` averages ~1 per 40 calls. The port has no persistent accumulator…
- **[minor]** Missing `cnt == 0` guard: pure-dlight blocks emit a stray default particle
  - base `Base/darkplaces/cl_particles.c:CL_NewParticlesFromEffectinfo:1719-1720` → port `XonoticGodot/game/client/EffectSystem.cs:647 (BuildInfoBurst)`
  - DP skips an emitter when the computed count is zero: `if (cnt == 0) continue;` so a block with no count/countabsolute/trailspacing (countmultiplier=0, countabsolute=0) produces only its dlight and no particles. The port floors n at 1 (`Math.Clamp((int)ceil(cnt),1,1024)`) with no cnt==0 short-circuit, so such blocks al…
- **[minor]** airfriction mapped to Godot Damping with a dimensionally-wrong heuristic
  - base `Base/darkplaces/cl_particles.c:CL_MoveParticles:2972-2976` → port `XonoticGodot/game/client/EffectSystem.cs:725-728`
  - DP airfriction is a per-second FRACTION of velocity (exponential-ish decay): each frame `f = 1 - min(airfriction*frametime,1); vel *= f`. The port converts it to Godot ParticleProcessMaterial.Damping (which is an absolute units/sec linear velocity reduction) via `Damping = airfriction * baseSpeed * 0.5`. This only app…
- **[minor]** originoffset / relativeoriginoffset / relativevelocityoffset parsed but never applied to spawn
  - base `Base/darkplaces/cl_particles.c:CL_NewParticlesFromEffectinfo:1751-1773` → port `XonoticGodot/game/client/EffectSystem.cs:675-689 (BuildInfoBurst position/velocity)`
  - DP adds originoffset directly and relativeoriginoffset/relativevelocityoffset rotated into the velocity-derived forward/right/up basis: spawn origin = trailpos + originoffset + originjitter*rvec (after VectorMAMAMAM with relativeoriginoffset), spawn velocity adds the rotated relativevelocityoffset. The port only consu…
- **[minor]** bounce / liquidfriction unmodeled: no particle collision, no bounce, no bounce<0 blood-splat
  - base `Base/darkplaces/cl_particles.c:CL_MoveParticles:2982-2999 (+ CL_NewParticle pbounce doc)` → port `XonoticGodot/game/client/EffectSystem.cs:638-763 (BuildInfoBurst)`
  - DP particles collide against world geometry when bounce!=0 and cl_particles_collisions is on: they bounce, are removed on solid hit, and a `bounce -1` particle (blood) creates a splat/stain on impact. The port's GpuParticles3D have no collision at all and never read info.Bounce or info.LiquidFriction. Consequences: pa…
- **[minor]** Decal projected along emit-velocity axis instead of DP's nearest-surface search
  - base `Base/darkplaces/cl_particles.c:CL_SpawnDecalParticleForPoint:981-1008 (called from :1681)` → port `XonoticGodot/game/client/EffectSystem.cs:589-599 + Decals.cs:45-74`
  - For a pt_decal block DP calls CL_SpawnDecalParticleForPoint, which fires 32 random rays (VectorRandom) out to maxdist (= originjitter[0]) and projects the decal onto the CLOSEST hit surface using that surface's normal (it does NOT orient the decal along the velocity). The port instead passes the emit velocity / impact…
- **[nit]** Per-type default table: blood orientation is Spark, should be Billboard
  - base `Base/darkplaces/cl_particles.c:particletype[]:39 (pt_blood)` → port `XonoticGodot/game/client/EffectInfo.cs:315 (DefaultsFor)`
  - DP's particletype[] gives pt_blood {PBLEND_INVMOD, PARTICLE_BILLBOARD}. The port's DefaultsFor(Blood) returns (InvMod, Spark). Since the file sets `orientation` on only 2 of 9043 lines, all 24 `type blood` blocks rely on this default, so every blood particle is flagged Spark instead of Billboard. No functional consequ…
- **[nit]** Per-type default table: raindecal blend is InvMod, should be Add
  - base `Base/darkplaces/cl_particles.c:particletype[]:36 (pt_raindecal)` → port `XonoticGodot/game/client/EffectInfo.cs:312 (DefaultsFor)`
  - DP's particletype[] gives pt_raindecal {PBLEND_ADD, PARTICLE_ORIENTED_DOUBLESIDED}. The port's DefaultsFor(RainDecal) returns (InvMod, Oriented) — blend wrong. No current consequence because the port skips Rain/Snow/RainDecal/EntityParticle kinds entirely (BuildFromInfo treats them as weather no-ops), but the table co…
- **[nit]** DensityScale multiplies countabsolute (DP excludes it from the quality multiplier)
  - base `Base/darkplaces/cl_particles.c:CL_NewParticlesFromEffectinfo:1710-1718` → port `XonoticGodot/game/client/EffectSystem.cs:643-646 (BuildInfoBurst)`
  - DP applies cl_particles_quality only to the count-multiplier term and the trailspacing term, never to countabsolute (the baseline comment: 'absolute number of particles ... unaffected by quality and requestedcount'). The port folds cl_particles_quality into DensityScale and multiplies the whole sum, including CountAbs…

---

## 📎 Base/ source map (canonical reference per task)

**Premise:** every task is a *port of existing Xonotic/Darkplaces code*, not a new feature. Before implementing a
task, read its Base source below and mirror it; cite it in the port file's `// Port of <path>` header (the convention
124 port files already follow). Paths are relative to repo root: `qcsrc/…` = `Base/data/xonotic-data.pk3dir/qcsrc/…`,
`data/…` = `Base/data/xonotic-data.pk3dir/…`, `Base/darkplaces/…` = the engine C. *(Generated 2026-06-06 by an
evidence-based fan-out that read each cited file.)*

### T1
- `qcsrc/server/client.qc` — **ClientConnect, ClientPreConnect, PutClientInServer, PutPlayerInServer, ClientDisconnect, ClientData_Send (ENT_CLIENT_CLIENTDATA writer), Se…** — Server-side per-client connection lifecycle: accept/transmute a connecting client, set its view (SVC_SETVIEW), spawn it into the world, stream per-frame client status (spectatee/zoom/teamnagger) via …
- `qcsrc/client/main.qc` — **CSQC_Init, PostInit, Shutdown, ENT_CLIENT_INIT (NET_HANDLE), ENT_CLIENT_CLIENTDATA (NET_HANDLE), ENT_CLIENT_SCORES_INFO, net_handle_ServerW…** — Client (CSQC) connect path: initialize client state on map load, receive the server's INIT/CLIENTDATA/ServerWelcome handshake, and drive the entity-update dispatch. This is what ClientNet+ClientEntit…
- `Base/darkplaces/sv_main.c` — **SV_SpawnServer (svs.maxclients, sv.active), SV_ConnectClient, SV_SendServerinfo, host_client->begun** — Engine listen/host-server launch + connect handshake: spawning the server, allocating client slots, and the serverinfo handshake a client receives on connect. Canonical reference for the new host/lis…
- `Base/darkplaces/netconn.c` — **NetConn_OpenServerPorts, NetConn_OpenClientPorts, NetConn_Open, NetConn_ConnectionEstablished (cls.state = ca_connected), NetConn_ServerFra…** — Engine-level connection establishment and per-frame net pump for both client and server (including the LHNETADDRESSTYPE_LOOP loopback path the current --net-loopback uses). The transport semantics a …
  - → *port:* game/Shell.cs (OnConnect builds ClientNet+ClientEntityView, host launch), game/net/ClientNet.cs, game/net/ServerNet.cs, game/net/NetLoopback.cs, game/net/ClientEntityView.cs, game/GameDemo.cs (current host entry — the TODO's Main.cs does not exist)
  - *notes:* TODO lists `Main.cs`; no such file exists in XonoticGodot/game/ — the Godot host entry points are game/Shell.cs and game/GameDemo.cs (both define a root Godot node), so retarget there. Existing port files game/net/{ClientNet,ServerNet,NetLoopback}.cs and game/net/NetGame.cs already implement the play path (per MEMORY: NetGame is the real menu->play path, not PlayerController). The connection-lifecycle…

### T2
- `qcsrc/server/weapons/weaponsystem.qc` — **W_WeaponFrame, weapon_prepareattack, weapon_prepareattack_check, weapon_prepareattack_checkammo, weapon_prepareattack_do, weapon_thinkf, w_…** — The entire fire-driver state machine: per-frame button read, WS_* state transitions, W_TICSPERFRAME think loop dispatching wr_think + weapon_think, refire/ATTACK_FINISHED arming, ammo gating, dry-fir…
- `qcsrc/server/weapons/weaponsystem.qh` — **ATTACK_FINISHED, ATTACK_FINISHED_FOR, INDEPENDENT_ATTACK_FINISHED (=1), W_TICSPERFRAME (=2), attack_finished_for[], attack_finished_single[…** — Defines the refire-timer macro family (per weapon-id*slot) and the tics-per-frame constant the driver loop depends on; declares the weapon_think scheduling fields.
- `qcsrc/common/weapons/weapon.qh` — **WS_CLEAR(0), WS_RAISE(1), WS_DROP(2), WS_INUSE(3), WS_READY(4), MAX_WEAPONSLOTS(2), weaponslot(), Weapon CLASS (wr_think/wr_setup/wr_checka…** — Canonical numeric weapon-state constants the FSM switches on, the slot count, the weaponslot() index lookup, and the Weapon method vtable (wr_think etc.) the driver invokes.
- `qcsrc/common/wepent.qh` — **m_switchweapon, m_weapon, m_switchingweapon (SVQC), clip_load, clip_size** — Declares the per-weaponentity weapon-pointer fields the state machine reads/writes each tick (active vs switch-target vs in-progress switch).
  - → *port:* src/XonoticGodot.Common/Gameplay/Weapons/WeaponFireDriver.cs (==W_WeaponFrame), WeaponFireGate.cs (==weapon_prepareattack*), EntityWeaponState.cs (==per-slot weaponentity state/ATTACK_FINISHED), WeaponFiring.cs (weapon_thinkf/w_ready); already landed in Wave 1, this is the reference of record.
  - *notes:* Already shipped (Wave 1, 2026-06-05) but listed so the Source(Base) reference is recorded. The whole driver lives in ONE file: server/weapons/weaponsystem.qc. The state-machine heart is W_WeaponFrame (lines 470-659): per-tick it (a) reads PHYS_INPUT_BUTTON_ATCK/ATCK2, (b) drives the WS_CLEAR->WS_RAISE->WS_READY->WS_DROP->WS_CLEAR switch (the big `switch(this.state)` at 531), (c) loops W_TICSPERFR…

### T3
- `qcsrc/common/mutators/base.qh` — **CLASS(Mutator), REGISTER_MUTATOR/_REGISTER_MUTATOR, Mutator_Add, Mutator_Remove, STATIC_INIT_LATE(Mutators) FOREACH(Mutators,_MUTATOR_IS_EN…** — Defines the whole mutator lifecycle the activation loop must mirror: Mutator class, REGISTER_MUTATOR, Mutator_Add/Remove, STATIC_INIT_LATE that iterates Mutators and adds the enabled ones, and MUTATO…
- `qcsrc/common/mutators/events.qh` — **MUTATOR_HOOKABLE(PlayerPhysics) EV_PlayerPhysics, MUTATOR_HOOKABLE(PM_Physics), MUTATOR_HOOKABLE(PlayerJump), MUTATOR_HOOKABLE(PlayerCanCro…** — Declares the GAMEQC-shared hookable events incl. PlayerPhysics and PM_Physics (the headline dormant Call() sites). Defines MUTATOR_ARGV in/out slot machinery (M_ARGV) that the C# ref-args structs rep…
- `qcsrc/server/mutators/events.qh` — **EV_PlayerPowerups, EV_PlayerRegen, EV_Damage_Calculate, EV_PlayerDamage_SplitHealthArmor, EV_PlayerDies, EV_PlayerSpawn, EV_EditProjectile,…** — Declares the server-side hookable events that back the 9 zero-call sites: PlayerPowerups, PlayerRegen, Damage_Calculate, PlayerDamage_SplitHealthArmor, PlayerDies, PlayerSpawn, EditProjectile, Spawn_…
- `qcsrc/lib/registry.qh` — **REGISTRY, REGISTER, REGISTRY_PUSH, REGISTRY_SORT, REGISTRY_CHECK, FOREACH, REGISTRY_GET** — The REGISTRY/REGISTER/FOREACH primitives Mutators is built on; defines registration push, deterministic sort, and content-hash CL/SV agreement that the C# Registry<MutatorBase> + GameRegistries must …
- `qcsrc/common/physics/player.qc` — **SV_PlayerPhysics, sys_phys_update, MUTATOR_CALLHOOK(PlayerPhysics_UpdateStats), MUTATOR_CALLHOOK(PlayerPhysics_PostUpdateStats), PHYS_HIGHS…** — The REAL movement path (not ecs/) that fires the PlayerPhysics-family hooks during physics: PlayerPhysics_UpdateStats (where powerups/speed adjust maxspeed) and PlayerPhysics_PostUpdateStats. This is…
- `qcsrc/server/main.qc` — **MUTATOR_CALLHOOK(SV_StartFrame) at main.qc:374** — Per-server-frame loop that fires the global SV_StartFrame hook (one of the dormant Call() sites; StatusEffects_tick, random_gravity, etc. ride it). Maps to the port's GameWorld/PlayerFrameLogic frame…
- `qcsrc/server/damage.qc` — **MUTATOR_CALLHOOK(Damage_Calculate) damage.qc:601, MUTATOR_CALLHOOK(GiveFragsForKill) damage.qc:72, Damage(), Heal()** — Fires Damage_Calculate (damage/force/mirror adjust) and GiveFragsForKill — two of the dormant Call() sites that belong in DamageSystem.cs. Also defines Heal(). Shows exactly where in the damage pipel…
- `qcsrc/server/client.qc` — **player_powerups, MUTATOR_CALLHOOK(PlayerPowerups) client.qc:1634, MUTATOR_CALLHOOK(PlayerRegen) client.qc:1699** — Contains player_powerups() which fires PlayerPowerups (dormant Call() site) and the per-frame regen which fires PlayerRegen. The canonical consumer that runs the powerup/regen hook chains each player…
  - → *port:* src/XonoticGodot.Server/{GameWorld,ClientManager,PlayerFrameLogic}.cs, src/XonoticGodot.Common/Physics/PlayerPhysics.cs, src/XonoticGodot.Common/Gameplay/Mutators/* (MutatorHooks.cs, GameHooks.cs, GameplayBases.cs MutatorBase, Registries.cs GameRegistries), src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs
  - *notes:* T3 is the activation loop + wiring the 9 dormant Call() sites. The loop itself = base.qh (Mutator_Add + STATIC_INIT_LATE FOREACH-enabled) over registry.qh's Mutators registry. The 9 Call() sites are spread across these consumer files: PlayerPhysics/PM_Physics -> common/physics/player.qc (NOT ecs/); SV_StartFrame -> server/main.qc; Damage_Calculate + GiveFragsForKill -> server/damage.qc; PlayerPow…

### T4
- `qcsrc/client/view.qc` — **CSQC_UpdateView (master per-frame driver), GetCurrentFov + IsZooming + ZoomScroll + View_UpdateFov (FOV + cl_zoom/zoomfactor/zoomspeed/zoom…** — THE canonical view/camera entrypoint; every per-frame presentation step lives here. A faithful port mirrors this file end-to-end.
- `qcsrc/client/view.qh` — **current_viewzoom, current_zoomfraction, zoomin_effect, view_origin/view_angles/view_forward, camera_active, chase_active_backup, autocvar_c…** — Header for view.qc: declares the shared view globals/cvars the port needs as fields (zoom state, camera state, content/damage blur vectors).
  - → *port:* XonoticGodot/game/client/ViewEffects.cs (damage flash + content tint + postprocessing), XonoticGodot/game/GameMapView.cs and XonoticGodot/game/net/NetGame.cs SetupCameraAndHud/BuildViewState (FOV + zoom + chase/death cam)
  - *notes:* FOV/zoom/eventchase/damage-tint/content-tint are all 100% CSQC logic in view.qc — no darkplaces C needed except the engine-side r_glsl_postprocess uservec cvars it drives (those are an engine contract, not a file to port). The eventchase camera uses WarpZone_TraceBox/TraceLine (lib/warpzone) for collision; the actual trace primitive is engine-level but the camera algorithm is here.

### T5
- `Base/darkplaces/model_brush.c` — **Mod_Q3BSP_LoadLightmaps (5516), Mod_Q3BSP_LoadFaces (5806, Q3FACETYPE_FLAT/PATCH/MESH/FLARE), Mod_Q3BSP_LoadTextures (5218), Mod_Q3BSP_Load…** — Defines exactly what a faithful map render must reproduce: lightmap/deluxemap pages, the 4 face types (flat surfaces, bezier patches, meshes, flares/billboards), and texture-shader binding. QA on a r…
- `Base/darkplaces/curves.c` — **Q3PatchSubdivideFloat (64), Q3PatchTesselation (138), Q3PatchTesselationOnX/OnY (232/252), Q3PatchDimForTess (51)** — Canonical bezier-patch tessellation the port's BezierPatch.cs mirrors; patch silhouette/curvature correctness on real maps is judged against this.
- `Base/darkplaces/model_alias.c` — **Mod_IDP3_Load (1578, MD3 morph), Mod_DARKPLACESMODEL_Load (2163, DPM skeletal), Mod_INTERQUAKEMODEL_Load (3219, IQM skeletal bone poses)** — Defines the model load + bone/pose layout the on-screen bone poses must match (MD3/DPM/IQM are the shipped formats); bone-pose QA verifies the Quake↔Godot skeletal conversion against these.
- `Base/darkplaces/shader_glsl.h` — **MODE_LIGHTMAP / MODE_LIGHTDIRECTIONMAP_MODELSPACE / _TANGENTSPACE deluxemap shading (1602-1629); lightcolor *= 1/max(0.25,lightnormal.z)** — Canonical lightmap+deluxemap fragment math; verifying lightmaps/materials look right on real maps means checking the port's LightmapShader reproduces this directional modulation.
  - → *port:* XonoticGodot/game/ScreenshotHook.cs (driver); read-mostly verifies: XonoticGodot/src/XonoticGodot.Formats/Bsp/BspReader.cs, XonoticGodot/game/assets/bsp/BezierPatch.cs, XonoticGodot/game/assets/LightmapShader.cs, XonoticGodot/game/assets/{ShaderCompiler,HeroMaterials}.cs, XonoticGodot/game/assets/models/{Md3,Dpm,Iqm}Builder.cs, Rebir…
  - *notes:* T5 is a QA/verification task, not a port-new task, so there is no single feature file — instead these are the Base references whose output the windowed checklist confirms. The four sub-areas map cleanly: lightmaps/materials -> model_brush.c Mod_Q3BSP_LoadLightmaps/LoadTextures + shader_glsl.h; patches -> curves.c + Mod_Q3BSP_LoadFaces; billboards -> Q3FACETYPE_FLARE in Mod_Q3BSP_LoadFaces (+ mode…

### T6
- `qcsrc/server/weapons/weaponsystem.qc` — **W_Reload, W_ReloadedAndReady, W_DecreaseAmmo, W_WeaponFrame (WS_READY->WS_DROP via switchdelay_drop+w_clear; WS_CLEAR->WS_RAISE via switchd…** — Authoritative reload pipeline (begin reload, ammo transfer on completion, ammo decrement) AND the raise/drop switch-delay timing inside the state machine; the only place clip_load/clip_size are seede…
- `qcsrc/server/weapons/selection.qc` — **W_GetCycleWeapon, W_SwitchWeapon, W_SwitchWeapon_Force, W_SwitchToOtherWeapon, W_CycleWeapon, W_NextWeapon, W_PreviousWeapon, W_LastWeapon,…** — Complete weapon-selection/cycling logic: weaponorder traversal, ownership+ammo check, set m_switchweapon, best/last/next/prev resolution that the driver consumes.
- `qcsrc/server/weapons/selection.qh` — **w_getbestweapon macro, selectweapon/weaponcomplainindex fields, function prototypes** — Declares the w_getbestweapon(ent,wepent) macro and the selection state fields (selectweapon, weaponcomplainindex).
- `qcsrc/server/impulse.qc` — **weapon_group_handle, weapon_priority_handle, weapon_byid_handle, weapon_next/prev_byid/_bygroup/_bypriority, weapon_last, weapon_best, weap…** — Maps client impulse numbers (cycle/by-id/by-group/by-priority/last/best/reload) onto the selection.qc API -- the impulse->selection routing for T6's cycle/impulse requirement.
- `qcsrc/common/weapons/all.qh` — **X(switchdelay_drop), X(switchdelay_raise); SVQC: reload_ammo + const reloading_ammo, reload_time + const reloading_time** — Defines switchdelay_raise/drop (consumed by the raise/drop timing) and the reloading_ammo/reloading_time aliases (consumed by W_Reload) as per-weapon cvar-backed fields.
- `qcsrc/common/wepent.qh` — **clip_load, clip_size** — Declares clip_load/clip_size as the network-shared (SVQC+CSQC) magazine state the reload system maintains and the HUD reads.
- `qcsrc/server/world.qc` — **readplayerstartcvars, start_weapons / warmup_start_weapons / g_weaponarena_weapons, weapons_start/most/all** — Computes the start-weapon set (incl. weaponstart cvar + arena modes) -- canonical source for the 'start-weapon' sub-item of T6.
- `qcsrc/server/client.qc` — **STAT(WEAPONS, this) = start_weapons; GiveRandomWeapons** — Applies the computed start_weapons to the player at (re)spawn -- where the initial loadout actually lands on the entity.
  - → *port:* src/XonoticGodot.Common/Gameplay/Weapons/EntityWeaponState.cs (clip_load/clip_size + WS_RAISE/DROP delays), Inventory.cs (owned set + selection), plus new selection/reload helpers alongside WeaponFireDriver.cs; impulse routing belongs in src/XonoticGodot.Server (ClientManager/Commands) mirroring server/impul…
  - *notes:* Three logical pieces, three Base files (+ small constant/start sources). (1) RELOAD: server/weapons/weaponsystem.qc W_Reload (755) + W_ReloadedAndReady (726) + W_DecreaseAmmo (689). clip_load/old_clip_load/clip_size/weapon_load[] semantics: on switch into a RELOADABLE weapon, clip_load is loaded from weapon_load[m_id] and clip_size=reloading_ammo (W_WeaponFrame case WS_CLEAR, lines 553-559). Relo…

### T7
- `qcsrc/common/gametypes/sv_rules.qh` — **GameRules_scoring, _GameRules_scoring_begin/_field/_field_team/_end, GameRules_score_enabled, GameRules_scoring_add/_add_team** — Defines the GameRules_scoring(teams,spprio,stprio,fields) macro + field/field_team helpers each mode uses to declare its columns; the literal API a faithful port mirrors
- `qcsrc/server/scores_rules.qc` — **ScoreRules_basics, ScoreRules_basics_end, NumTeams, AVAILABLE_TEAMS** — ScoreRules_basics: registers the common columns (kills/deaths/suicides/teamkills/score/dmg/dmgtaken/skill/fps) and sets up teams; the body GameRules_scoring wraps
- `qcsrc/server/scores.qc` — **ScoreInfo_SetLabel_PlayerScore, ScoreInfo_SetLabel_TeamScore, scores_primary/secondary, PlayerScore_Compare, ScoreField_Compare, PlayerScor…** — ScoreInfo_SetLabel_PlayerScore/_TeamScore implement column label+flags assignment and capture primary/secondary sort keys; PlayerScore_Compare/ScoreField_Compare define the per-flag sort order
- `qcsrc/common/scores.qh` — **REGISTER_SP, SFL_SORT_PRIO_PRIMARY/SECONDARY, SFL_LOWER_IS_BETTER, SFL_TIME, MAX_SCORE, ST_SCORE** — Defines all SP_* score field IDs (registration order = sort priority) and the SFL_* flag bits (SORT_PRIO_PRIMARY/SECONDARY, LOWER_IS_BETTER, TIME, RANK, HIDE_ZERO, NOT_SORTABLE) that SetPrimary/SetSe…
- `qcsrc/server/world.qc` — **GameplayMode_DelayedInit** — GameplayMode_DelayedInit provides the default DM/TDM scoring registration when a mode registers no custom columns (the fallback GameRules_scoring call)
- `qcsrc/common/gametypes/gametype/ctf/sv_ctf.qc` — **ctf_ScoreRules, ctf_DelayedInit** — Canonical example of a per-mode ScoreRules block (ctf_ScoreRules) showing field_team + field with PRIMARY/SECONDARY flags and SFL_TIME/SFL_LOWER_IS_BETTER
  - → *port:* src/XonoticGodot.Common/Gameplay/GameTypes/*.cs (Deathmatch, Ctf, Domination, Onslaught, Race, etc.), src/XonoticGodot.Common/Gameplay/Scoring/GameScores.cs, src/XonoticGodot.Server/Scores.cs
  - *notes:* Each mode's *_Initialize() calls the GameRules_scoring(teams, spprio, stprio, {fields}) macro, which expands to ScoreRules_basics() + a sequence of ScoreInfo_SetLabel_PlayerScore/_TeamScore() calls; that's the C# 'each mode writes its GameScores columns' surface. SetPrimary/SetSecondary is NOT a function — it's the SFL_SORT_PRIO_PRIMARY/SFL_SORT_PRIO_SECONDARY flag passed per field; ScoreInfo_Set…

### T8
- `qcsrc/server/spawnpoints.qc` — **SelectSpawnPoint, Spawn_Score (teamcheck filter), Spawn_ScoreAll, Spawn_FilterOutBadSpots, relocate_spawnpoint, link_spawnpoint, spawnpoint…** — Full team-spawn selection: computes teamcheck and filters spots by spot.team, plus team-spawn detection at link time and team-ownership transfer on use
- `qcsrc/server/spawnpoints.qh` — **have_team_spawns, have_team_spawns_forteams, some_spawn_has_been_used, SPAWN_PRIO_NEAR_TEAMMATE_FOUND/SAMETEAM, spawnpoint_score** — Declares the have_team_spawns tri-state, have_team_spawns_forteams bitmask, some_spawn_has_been_used, and SPAWN_PRIO_* constants the selection logic reads
- `qcsrc/common/gametypes/sv_rules.qh` — **GameRules_spawning_teams, GameRules_teams** — Defines GameRules_spawning_teams(value) — the macro a mode calls to request team spawns (sets have_team_spawns); GameRules_teams(true) auto-enables it
- `qcsrc/server/teamplay.qc` — **Player_SetTeamIndex, SetPlayerTeam, TeamBalance_JoinBestTeam, TeamBalance_FindBestTeam** — Determines p.Team before spawn selection (team assignment/balance/forced teams); SelectSpawnPoint reads this.team set here
  - → *port:* src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs, src/XonoticGodot.Common/Gameplay/Player/EntitySpawnPointState.cs, src/XonoticGodot.Server/ClientManager.cs, src/XonoticGodot.Common/Gameplay/GameTypes/*.cs (mode *_Initialize calls GameRules_spawning_teams)
  - *notes:* The whole team-spawn selection lives in server/spawnpoints.qc. teamcheck is computed in SelectSpawnPoint() from have_team_spawns + have_team_spawns_forteams + this.team, then threaded through Spawn_Score()/Spawn_ScoreAll()/Spawn_FilterOutBadSpots() which reject spots where spot.team != teamcheck (teamcheck>=0). have_team_spawns is the tri-state flag (-1 requested-none-found / 0 none-requested / 1…

### T9
- `qcsrc/client/hud/panel/scoreboard.qc` — **Cmd_Scoreboard_SetFields, Scoreboard_InitScores, Scoreboard_GetField, Scoreboard_AccuracyStats_Draw, Scoreboard_MapStats_Draw, Scoreboard_R…** — The whole client scoreboard: configurable column parsing, per-mode column filtering, primary/secondary derivation, accuracy/itemstats grids, map stats, rankings, respawn status
- `qcsrc/client/hud/panel/scoreboard.qh` — **ps_primary, ps_secondary, ts_primary, ts_secondary, SCOREBOARD_DEFAULT_COLUMNS, Scoreboard_InitScores** — Scoreboard panel declarations: ps_primary/ps_secondary, ts_primary/ts_secondary, and the SCOREBOARD_DEFAULT_COLUMNS default field list
- `qcsrc/client/main.qc` — **NET_HANDLE(ENT_CLIENT_SCORES_INFO) [scores_label/flags, teamscores_label/flags], NET_HANDLE(ENT_CLIENT_SCORES), NET_HANDLE(ENT_CLIENT_TEAMS…** — Client-side receivers for the score networking — the wire format ScoreboardBlock/LayoutHash must mirror: column labels+flags (INFO), per-player scores, per-team scores
- `qcsrc/server/scores.qc` — **ScoreInfo_SendEntity, PlayerScore_SendEntity, TeamScore_SendEntity** — Server side of the same networking — ScoreInfo_SendEntity/PlayerScore_SendEntity/TeamScore_SendEntity define exactly which labels/flags/values go over the wire (the column set the client registry mus…
- `qcsrc/common/scores.qh` — **REGISTER_SP list, SFL_RANK, SFL_TIME, SFL_HIDE_ZERO, MAX_SCORE, MAX_TEAMSCORE** — Shared definition of the SP_* field set, SFL_* flag semantics, and MAX_SCORE/MAX_TEAMSCORE bounds that both columns and networking iterate over
  - → *port:* game/hud/ScoreboardPanel.cs, src/XonoticGodot.Net/ScoreboardBlock.cs (client column networking: mirror ENT_CLIENT_SCORES_INFO label/flag set so LayoutHash matches)
  - *notes:* Two halves. (A) Scoreboard rendering+columns: client/hud/panel/scoreboard.qc (2820 lines) is the entire surface — Cmd_Scoreboard_SetFields parses the configurable sbt_field column list from autocvar_scoreboard_columns incl. per-mode 'pattern/field' filters via isGametypeInFilter; Scoreboard_InitScores re-derives ps_primary/ps_secondary/ts_primary/ts_secondary from the networked SFL flags; plus Sc…

### T10
- `qcsrc/client/hud/hud.qh` — **REGISTER_HUD_PANEL(...) table lines ~250-277 (RACETIMER→HUD_RaceTimer, VOTE→HUD_Vote, MODICONS→HUD_ModIcons, MAPVOTE→MapVote_Draw, ITEMSTIM…** — Master HUD-panel registry: maps every panel name (MAPVOTE, MODICONS, RACETIMER, CHECKPOINTS, VOTE, ITEMSTIME, PHYSICS…) to its draw function and show/config flags. Defines the HUD_Panel_UpdatePosSize…
- `qcsrc/client/hud/panel/racetimer.qc` — **HUD_RaceTimer, MakeRaceString, StoreCheckpointSplits/ClearCheckpointSplits, race_checkpoint/race_laptime/race_penaltytime stats** — RACETIMER panel (#8): on-screen race split/checkpoint timer with anticipation/penalty/speed display.
- `qcsrc/client/hud/panel/checkpoints.qc` — **HUD_Checkpoints, Checkpoints_Draw, Checkpoints_drawstring, race_checkpoint_splits[]** — CHECKPOINTS panel (#27): persistent list of stored checkpoint split lines (race_checkpoint_splits) with flip/align/fontscale.
- `qcsrc/client/hud/panel/vote.qc` — **HUD_Vote, vote_active/vote_yescount/vote_nocount/vote_needed/vote_highlighted, uid2name_dialog** — VOTE panel (#9): vote-called dialog with yes/no progress bars (voteprogress_back/voted/prog) + uid2name prompt.
- `qcsrc/client/hud/panel/physics.qc` — **HUD_Physics, ACCEL2GRAV, PHYSICS_LAYOUT_*/PHYSICS_BARALIGN_*/PHYSICS_PROGRESSBAR_*/PHYSICS_TEXT_* (in physics.qh), StrafeHUD_GetStrafeplaye…** — PHYSICS/speedo panel (#15): speed + acceleration meter, top/jump speed, progress bars, unit conversion.
- `qcsrc/client/hud/panel/modicons.qc` — **HUD_ModIcons, HUD_ModIcons_SetFunc (HUD_ModIcons_GameType = gametype.m_modicons), HUD_ModIcons_Export** — MODICONS panel (#10) dispatcher: delegates drawing to the active gametype's m_modicons fn; falls back to HUD_Mod_CTF in configure mode.
- `qcsrc/common/gametypes/gametype/ctf/cl_ctf.qc` — **HUD_Mod_CTF, HUD_Mod_CTF_Reset, redflag/blueflag/neutralflag status + statuschange_time animation** — Reference per-gametype modicons implementation (flag carrier/status icons); also the configure-mode fallback the MODICONS panel draws directly. Mirror this pattern for other modes (cl_clanarena.qc, c…
- `qcsrc/common/mutators/mutator/itemstime/itemstime.qc` — **HUD_ItemsTime, Item_ItemsTime_GetTime/SetTime/Allow, ItemsTime_time[], ItemsTime_availableTime[], STAT(ITEMSTIME)** — ITEMSTIME panel (#22): item respawn-timer grid; the panel draw fn AND its data model (per-item time tracking) live in this mutator, not under hud/panel/.
- `qcsrc/client/mapvoting.qc` — **MapVote_Draw, MapVote_DrawMapItem, MapVote_DrawMapPicture, MapVote_DrawAbstain, MapVote_DrawSuggester, MapVote_ReadOption/ReadMask, mv_acti…** — MAPVOTE panel: the map/gametype voting screen (registered as MapVote_Draw). Lives at client/mapvoting.qc, NOT under hud/panel/.
  - → *port:* New XonoticGodot/game/hud/*Panel.cs per panel (RaceTimerPanel, CheckpointsPanel, VotePanel, PhysicsPanel, ModIconsPanel, ItemsTimePanel, MapVotePanel) wired through HudManager; menu dialogs already exist (DialogHudPanelRaceTimer/Vote/Physics/ModIcons/ItemsTime/Checkpoints.cs).
  - *notes:* Two panels are NOT under client/hud/panel/: ITEMSTIME lives in the itemstime mutator and MAPVOTE lives in client/mapvoting.qc. MODICONS is a thin dispatcher — the real drawing is per-gametype in common/gametypes/gametype/<mode>/cl_<mode>.qc (CTF is the canonical example and the configure-mode fallback). All panels share the hud.qh/hud.qc panel infra (HUD_Panel_LoadCvars, HUD_Panel_DrawBg, HUD_Pan…

### T11
- `qcsrc/common/mutators/mutator/powerups/sv_powerups.qc` — **powerups Damage_Calculate (g_balance_powerup_strength_damage/force/selfdamage, invincible_takedamage/takeforce), powerups PlayerPhysics_Upd…** — THE powerup-effects consumer file: the strength damage/force multiplier and shield take-damage/force multiplier (Damage_Calculate), the speed highspeed move multiplier (PlayerPhysics_UpdateStats), sp…
- `qcsrc/common/mutators/mutator/powerups/powerup/strength.qh` — **REGISTER_ITEM(Strength), STATUSEFFECT_Strength, autocvar_g_balance_powerup_strength_damage/force/selfdamage/selfforce/time** — Strength powerup ITEM registration + all autocvar_g_balance_powerup_strength_* constants (damage, force, selfdamage, selfforce, time) the consumer multiplies by.
- `qcsrc/common/mutators/mutator/powerups/powerup/speed.qh` — **REGISTER_ITEM(Speed), STATUSEFFECT_Speed, autocvar_g_balance_powerup_speed_highspeed/attack_time_multiplier/time** — Speed powerup ITEM registration + autocvar_g_balance_powerup_speed_* constants (highspeed, attack_time_multiplier, time) for the movement/attack-rate consumers.
- `qcsrc/common/mutators/mutator/powerups/powerup/invisibility.qh` — **REGISTER_ITEM(Invisibility), STATUSEFFECT_Invisibility, autocvar_g_balance_powerup_invisibility_alpha/time, InvisibilityStatusEffect m_tick…** — Invisibility powerup ITEM registration + autocvar_g_balance_powerup_invisibility_alpha (the alpha consumer) and time. invisibility.qc holds the m_tick/m_apply/m_remove that set actor.alpha = invisibi…
- `qcsrc/common/mutators/mutator/status_effects/status_effects.qc` — **StatusEffects_active, StatusEffects_tick, StatusEffects_gettime, StatusEffects_apply/remove/removeall, m_active/m_tick/m_apply/m_remove** — The effect tick/apply/remove/gettime engine the powerup status effects ride on (StatusEffects_active gates every consumer). Pairs with status_effects.qh (state fields, flags, REMOVE_* types, the SVQC…
- `qcsrc/common/mutators/mutator/nades/sv_nades.qc` — **nade_prime, toss_nade, nades_CheckThrow, NadeOffhand.offhand_think, nade_boom (dispatch to nade_*_boom), nade_touch/nade_damage/nade_beep, …** — THE nades subsystem: throw mechanics (nade_prime, toss_nade, nades_CheckThrow/charge force, NadeOffhand offhand_think), nade physics/touch/damage/beep/boom dispatch to all 9 boom types, bonus scoring…
- `qcsrc/common/mutators/mutator/nades/all.inc` — **REGISTER_NADE(NORMAL/NAPALM/ICE/TRANSLOCATE/SPAWN/HEAL/MONSTER/ENTRAP/VEIL/AMMO/DARKNESS), NADE_PROJECTILE** — Registers the 9 throwable nade types (+ NORMAL) into the Nades registry with their projectile/trail ids — the canonical type list ('9 types') the port enumerates. Pairs with nades.qh (Nade CLASS + RE…
- `qcsrc/common/mutators/mutator/nades/nade/napalm.qc` — **nade_napalm_boom, nade_napalm_burn, napalm fountain/ball spawning (and sibling files nade_ice_boom, nade_heal_boom, nade_entrap_boom, nade_…** — Representative per-type boom/effect implementation (one of the 9 nade/<type>.qc files: napalm, ice, heal, entrap, veil, ammo, darkness, monster, spawn, translocate, normal). Each nade/<type>.qc holds…
  - → *port:* src/XonoticGodot.Common/Gameplay/Player/StatusEffects.cs (i.e. Gameplay/StatusEffects.cs — strength/speed/invis consumers), src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs (strength/shield multipliers), src/XonoticGodot.Common/Physics/PlayerPhysics.cs (speed highspeed), new src/XonoticGodot.Common/Gameplay/Nad…
  - *notes:* Two halves. (a) Powerup consumers: the multipliers all live in sv_powerups.qc (strength×dmg+force, shield take-damage/force, speed highspeed+attack-rate, invisibility alpha/waypoint), gated by StatusEffects_active from status_effects.qc; the magnitudes are the autocvar_g_balance_powerup_* constants declared in each powerup/<name>.qh. The port already has StatusEffectDef/StatusEffectsCatalog (Stat…

### T12
- `qcsrc/server/antilag.qc` — **antilag_record, antilag_find, antilag_takebackorigin (lerpv), antilag_takeback, antilag_restore, antilag_clear, antilag_takeback_all, antil…** — The complete lag-compensation engine. antilag_takeback_all/antilag_restore_all already rewind FOREACH_CLIENT players PLUS IL_EACH g_monsters PLUS IL_EACH g_projectiles where classname=='nade' — this …
- `qcsrc/server/antilag.qh` — **ANTILAG_LATENCY(e) = min(0.4, CS(e).ping * 0.001), autocvar_g_antilag, autocvar_g_antilag_nudge, function prototypes** — Defines the rewind-time formula from measured ping and the 0.4s cap; the .ping field it reads is exactly the measured-RTT value T12 wants. Constants/macros for the .qc above.
- `qcsrc/server/world.qc` — **EndFrame (altime = time + frametime*(1+autocvar_g_antilag_nudge); antilag_record over FOREACH_CLIENT + IL_EACH g_monsters + IL_EACH g_proje…** — The per-server-frame recording side: every frame it pushes a timestamped origin sample for each client, monster, and nade into its antilag ring. The recording loop that must run each tick for monster…
- `Base/darkplaces/sv_user.c` — **SV_ReadClientMove (move->time, move->receivetime = sv.time), SV_ExecuteClientMoves (host_client->ping = cmd.receivetime - cmd.time), SV_App…** — Canonical MEASURED RTT: the server computes ping as (receive time - client's stamped move time) from the input stream and writes it (ms) into the .ping edict field that ANTILAG_LATENCY consumes. This…
- `qcsrc/common/stats.qh` — **REGISTER_STAT(MOVEVARS_GRAVITY/MAXSPEED/ACCELERATE/AIRACCELERATE/STOPSPEED/JUMPVELOCITY/TICRATE/TIMESCALE/ENTGRAVITY/AIRCONTROL/... FLOAT),…** — The authoritative list of replicated movement variables (the MOVEVARS_* STATs) sent server->client. 'Prediction reads replicated movevars' means the predictor must consume these stats rather than loc…
- `qcsrc/common/physics/player.qc` — **Physics_UpdateStats (STAT(MOVEVARS_MAXSPEED)=..., STAT(MOVEVARS_GRAVITY)=..., per-player movevar population), PHYS_* accessor macros** — Server writes the per-player MOVEVARS_* stats here (Physics_UpdateStats) and shared client/server physics reads them via PHYS_ macros — the producer side of the replicated movevars the client predict…
  - → *port:* src/XonoticGodot.Net/AntilagBuffer.cs (already // Port of server/antilag.qc), game/net/ServerNet.cs (per-frame antilag_record over monsters+nades; measured-RTT computation), game/net/ClientNet.cs (prediction reads replicated movevars), src/XonoticGodot.Net/MoveVarsBlock.cs (replicated MOVEVARS_* wire block),…
  - *notes:* AntilagBuffer.cs already carries a `// ...ported from server/antilag.qc` header and cites antilag_record/antilag_find/antilag_takebackorigin — so the per-entity ring is done; the T12 GAP is breadth (recording+rewinding monsters and nades, not just players) and that breadth ALREADY exists in the QC: antilag_takeback_all/antilag_restore_all (antilag.qc) and the EndFrame antilag_record loop (world.q…

### T13
- `Base/darkplaces/model_brush.c` — **Mod_Q3BSP_LoadLightmaps (5516): deluxemapping detection (5628-5677: even-count + face odd-index probe + blank-2nd-page special case), delux…** — THE deluxemap source: how DP decides a Q3BSP is deluxemapped, modelspace vs tangentspace, and de-interleaves the lightmap lump. The 'deluxemap fix' must mirror this exactly. BspReader.cs already cite…
- `Base/darkplaces/shader_glsl.h` — **MODE_LIGHTDIRECTIONMAP_MODELSPACE (1602-1623): decode Texture_Deluxemap*2-1, transform by VectorS/T/R tangent basis, lightcolor *= 1/max(0.…** — Canonical fragment math that consumes the deluxemap to add directional lighting on top of the flat lightmap — the rendering half of the deluxemap fix (LightmapShader.cs must apply this dot(normal,lig…
- `Base/darkplaces/gl_rmain.c` — **R_SkinFrame_LoadExternal (2314) / R_SkinFrame_LoadExternal_SkinFrame (2331): suffix-loading of _norm/_bump, _glow/.blend/_blend/_luma, _glo…** — THE hero-material suffix table: the exact filename suffixes, precedence order, and channel handling DP uses to assemble a multi-texture material (the _reflect/_shirt/_pants the task names live here, …
- `Base/darkplaces/model_shared.c` — **Mod_LoadQ3Shaders (1466) shader/keyword parse; Mod_LoadTextureFromQ3Shader (2270) + Mod_CreateShaderPassFromQ3ShaderLayer: maps parsed laye…** — The bridge from a parsed .shader to the actual hero textures + reflect/specular params; defines how a shader's diffuse layer selects the basename that the suffix loader then expands (the material-tab…
  - → *port:* XonoticGodot/src/XonoticGodot.Formats/Bsp/BspReader.cs (DetectAndSplitDeluxemaps — already ports Mod_Q3BSP_LoadLightmaps), XonoticGodot/game/MapLoader.cs, XonoticGodot/game/assets/ShaderCompiler.cs (suffix/hero texture assembly), XonoticGodot/game/assets/AssetSystem.cs, XonoticGodot/game/assets/HeroMaterials.cs, XonoticGodot/game/ass…
  - *notes:* Marked done in TODO but this gives the exact Base anchors for any follow-up. Two halves: (1) LOAD/SPLIT deluxemaps = model_brush.c Mod_Q3BSP_LoadLightmaps (BspReader.cs already mirrors the detection+split heuristic faithfully, including the modelspace flag and blank-2nd-page case); (2) APPLY them = shader_glsl.h MODE_LIGHTDIRECTIONMAP_* math in LightmapShader.cs — note the modelspace-vs-tangentsp…

### T14
- `qcsrc/lib/spawnfunc.qh` — **spawnfunc(id) macro, require_spawnfunc_prefix, __spawnfunc_defer, __spawnfunc_spawn, g_spawn_queue, g_map_entities, SPAWNFUNC_INTERNAL_FIEL…** — Defines the spawnfunc(id) macro and the deferred map-entity spawn pipeline (__spawnfunc_defer / __spawnfunc_spawn / g_spawn_queue / g_map_entities) that turns a map classname into a constructor call …
- `qcsrc/common/monsters/sv_monsters.qc` — **Monster_Spawn(this, check_appear, mon), Monster_Spawn_Setup, Monster_Appear, Monster_Appear_Check, Monster_Use, Monster_Reset, monsters_tot…** — The generic monster spawn driver every monster_X spawnfunc calls. Builds the monster entity from its Monster def, applies spawnflags/skill culling, sets fields/movetype/think/use, and handles the APP…
- `qcsrc/common/monsters/monster/zombie.qc` — **spawnfunc(monster_zombie), MON_ZOMBIE, mr_setup/mr_think/mr_death methods** — Representative per-type monster spawnfunc: `spawnfunc(monster_zombie){ Monster_Spawn(this,true,MON_ZOMBIE); }`. Mirror this shape for monster_golem/_mage/_spider/_wyvern (each in its sibling .qc). Sh…
- `qcsrc/common/monsters/sv_spawner.qc` — **spawnfunc(monster_spawner), spawner_use, MON_Null** — The monster_spawner map entity (a periodic spawner that emits monsters of type .spawnmob up to .count) — explicitly named in T14. Calls spawnmonster() on use.
- `qcsrc/common/monsters/sv_spawn.qc` — **spawnmonster(e, monster, monster_id, spawnedby, own, orig, respwn, removeifinvalid, moveflag), MONSTERFLAG_SPAWNED, MONSTERFLAG_NORESPAWN** — spawnmonster(): resolves a monster by netname/"random"/"anyrandom", applies owner/team/move flags, and calls Monster_Spawn. Used by monster_spawner and by code-spawned monsters (Invasion, etc.). Defi…
- `qcsrc/common/monsters/sv_monsters.qh` — **MONSTERFLAG_APPEAR, MONSTERFLAG_NORESPAWN, MONSTERFLAG_SPAWNED, MONSTERFLAG_RESPAWNED, MONSTERSKILL_NOTEASY/NOTMEDIUM/NOTHARD, MONSTER_SKIL…** — Spawnflag/skill constants the monster spawn path branches on (APPEAR, NORESPAWN, SPAWNED, RESPAWNED, NOTEASY/NOTMEDIUM/NOTHARD skill culling, MONSTER_SKILL_*). Required to faithfully port Monster_Spa…
- `qcsrc/common/monsters/all.qh` — **REGISTRY(Monsters), REGISTER_MONSTER(id, inst), MON_Null, get_monsterinfo** — Monsters registry definition: REGISTER_MONSTER macro + Monsters registry keyed by netname/monsterid that maps a monster_X classname's MON_X token to its def. The port's Registry<Monster> mirrors this.
- `qcsrc/common/turrets/sv_turrets.qc` — **turret_initialize(this, tur), turret_initparams, turret_link, turret_respawn, g_turrets, TUR_FLAG_ISTURRET** — Generic turret spawn driver every turret_X spawnfunc calls. turret_initialize() builds the turret + tur_head, applies default flag sets, model/size, think/use/damage hooks, links it for networking, r…
- `qcsrc/common/turrets/turret/machinegun.qc` — **spawnfunc(turret_machinegun), TUR_MACHINEGUN, turret_initialize, MachineGunTurret.tr_setup** — Representative per-type turret spawnfunc: `spawnfunc(turret_machinegun){ if(!turret_initialize(this,TUR_MACHINEGUN)) delete(this); }` + tr_setup flags. All 15 turret_* (ewheel/flac/fusionreactor/hell…
- `qcsrc/common/turrets/all.qh` — **REGISTRY(Turrets), REGISTER_TURRET(id, inst), TUR_Null, TUR_FIRST, TUR_LAST** — Turrets registry + REGISTER_TURRET macro mapping turret_X's TUR_X token to its def; also TUR_FIRST/TUR_LAST. Mirrors port Registry<Turret>.
- `qcsrc/common/vehicles/sv_vehicles.qc` — **vehicle_initialize(this, info, nodrop), vehicles_spawn, vehicles_touch, VHF_ISVEHICLE, vehicle_use** — Generic vehicle spawn driver every vehicle_X spawnfunc calls. vehicle_initialize() wires controller/team, model, viewport/hudmodel/tur_head sub-entities, physics plug, think=vehicles_spawn, contents …
- `qcsrc/common/vehicles/vehicle/racer.qc` — **spawnfunc(vehicle_racer), vehicle_initialize, VEH_RACER** — Representative per-type vehicle spawnfunc (spawnfunc(vehicle_racer) at line 520, calling vehicle_initialize with the Racer def). The other three (raptor.qc:587, spiderbot.qc:529, bumblebee.qc:740) fo…
- `qcsrc/common/vehicles/all.qh` — **REGISTRY(Vehicles), REGISTER_VEHICLE(id, inst), VEH_Null, VEH_FIRST, VEH_LAST** — Vehicles registry + REGISTER_VEHICLE macro mapping vehicle_X's VEH_X token to its def; VEH_FIRST/VEH_LAST. Mirrors port Registry<Vehicle>.
  - → *port:* XonoticGodot/src/XonoticGodot.Common/Gameplay/MapObjects/MapObjectsRegistry.cs (add SpawnFuncs.Register("monster_*"/"turret_*"/"vehicle_*"/"monster_spawner") entries); dispatchers in XonoticGodot/src/XonoticGodot.Common/Gameplay/Monsters/MonsterAI.cs (Monster_Spawn) + MonsterFramework.cs, XonoticGodot/src/XonoticGodot.Common/Ga…
  - *notes:* Map-placement spawnfuncs are thin: each per-type .qc defines `spawnfunc(monster_X){ Monster_Spawn(this,true,MON_X); }`, `spawnfunc(turret_X){ if(!turret_initialize(this,TUR_X)) delete(this); }`, `spawnfunc(vehicle_X){ ... vehicle_initialize(this,VEH_X,false); }`. So the canonical sources are (a) the generic spawn/init functions that do all the real work and (b) the small set of monster/turret/veh…

### T15
- `data/binds-xonotic.cfg` — **bind w +forward / bind MOUSE1 +fire / bind 1..9 weapon_group_N / bind q weapon_last / bind MWHEELUP weapnext / bind TAB +showscores / bind …** — THE canonical default bind table — the single source of action->key->command mapping a fresh profile ships with. KeyBinder_Bind_Reset_All re-execs exactly this. This is the file the port's KeyBinding…
- `Base/darkplaces/keys.c` — **keybindings[MAX_BINDMAPS][MAX_KEYS]; Key_SetBinding; Key_FindKeysForCommand; Key_Event (executes bind via Cbuf for key_game), keydown[]; ke…** — Engine-level bind storage + key->command dispatch: the bind table itself and the path from a key event to executing the bound console command string. This is the real 'consume keybinds' mechanism the…
- `Base/darkplaces/cl_input.c` — **kbutton_t in_forward/in_back/in_moveleft/in_attack/in_jump/in_use...; KeyDown/KeyUp; CL_KeyState; IN_ForwardDown/IN_AttackDown (+command ha…** — Engine consumption of +commands into gameplay movement/buttons — the half of 'bind table -> controller' that turns held keys into a per-frame movement command. The +forward/+fire/+jump etc. bound in …
- `qcsrc/server/weapons/selection.qc` — **W_NextWeapon(list)/W_PreviousWeapon(list); W_CycleWeapon; W_LastWeapon; W_NextWeaponOnImpulse (weapon_group); W_SwitchWeapon/_Force; w_getb…** — Gameplay consumption of the weapon-select binds (weapnext/weapprev/weapon_last/weapon_best/weapon_group_N/weapon_byid). These bind commands resolve here on the server to actually switch the held weap…
- `qcsrc/menu/xonotic/keybinder.qc` — **KeyBinds_BuildList (KEYBIND_DEF/HEADER macros, full action list); XonoticKeyBinder_keyGrabbed; KeyBinder_Bind_Change/Clear/Edit; KeyBinder_…** — The menu's bind EDITOR/list-builder (the reconcile half of T15): builds the canonical ordered keybind catalog with headers/icons/userbinds, grabs a key and writes it via localcmd bind, clears/edits/r…
- `qcsrc/menu/xonotic/keybinder.qh` — **MAX_KEYS_PER_FUNCTION; MAX_KEYBINDS; KEYBIND_IS_USERBIND/_SPECIAL/_OVERRIDER; CLASS(XonoticKeyBinder)** — Constants/class for keybinder.qc: MAX_KEYS_PER_FUNCTION (2 binds/action), userbind/special/overrider markers, column layout attribs, method table.
- `qcsrc/menu/xonotic/dialog_settings_input_userbind.qc` — **XonoticUserbindEditDialog_fill; loadUserBind; userbindEdit*; cvars userbindN_description/_press/_release** — The userbind editor dialog backing the 32 +userbind slots in the catalog — defines how a user-defined bind's name/press/release cvars are read and written (the X_userbind*_description/_press/_release…
  - → *port:* XonoticGodot/game/menu/KeyBindings.cs (catalog + Defaults — replace 16 invented binds with binds-xonotic.cfg's full set), XonoticGodot/game/menu/MenuSettings.cs (the duplicate settings model to QUARANTINE — its Keybinds dict + ApplyInput InputMap rebuild is the non-canonical parallel store), XonoticGodot/src/Rebi…
  - *notes:* T15 has TWO parts. (a) 'Consume keybinds in gameplay': there is no single Base file — the canonical chain is binds-xonotic.cfg (default bind strings) -> keys.c (bind table + key->command) -> cl_input.c (+command kbuttons -> cl.cmd movement/buttons) for movement, and selection.qc for weapon-select commands. The port short-circuits this with Godot InputMap actions (KeyBindings.InputActionName 'rebi…

### T16
- `qcsrc/server/weapons/tracing.qc` — **Headshot, fireBullet_falloff (headshot_multiplier application), fireBullet / fireBullet_antilag, FireRailgunBullet (headshot_notify + ANNCE…** — Canonical headshot detection (head AABB construction + trace_hits_box), the damage*headshot_multiplier scaling for hitscan, the rail headshot announce, and the shotorg/antilag setup all held-fire hit…
- `qcsrc/server/weapons/tracing.qh` — **Headshot(), fireBullet()/fireBullet_antilag()/fireBullet_falloff() prototypes, FireRailgunBullet() prototype, W_SetupShot / W_SetupShot_Dir…** — Declares the headshot_multiplier-bearing fireBullet signatures and the W_SetupShot macro surface the weapons call.
- `qcsrc/common/weapons/weapon/rifle.qc` — **W_Rifle_FireBullet, W_Rifle_BulletHail, W_Rifle_BulletHail_Continue, wr_think (burst accumulator + bullethail dispatch), rifle_accumulator** — Reference implementation of per-weapon held/continuous fire: switchweapon-masking, ATTACK_FINISHED save/restore so the last shot enforces refire, and the burstcost/bursttime accumulator -- the archet…
- `qcsrc/common/weapons/weapon/rifle.qh` — **WEP_RIFLE W_PROPS: headshot_multiplier (BOTH), bullethail, burstcost, bursttime, refire, animtime, spread, solidpenetration, damagefalloff_*** — Defines the per-weapon cvars that parameterize both the headshot multiplier and the held-fire/burst timing for the canonical rifle.
- `qcsrc/server/damage.qc` — **Damage (team/armor/force/mirror processing, event_damage dispatch), RadiusDamageForSource, damage_explosion_calcpush** — Where the headshot-scaled damage value is finally applied to the target (team nullify, armor block, knockback, hitsound, mirror) -- the consumer half of the headshot path.
  - → *port:* src/XonoticGodot.Common/Gameplay/Weapons/WeaponFiring.cs (port of tracing.qc fireBullet/FireRailgunBullet/Headshot/W_SetupShot), the per-weapon held-fire bullethail (Rifle.cs ports rifle.qc), and Damage/DamageSystem.cs (final scaled-damage application, port of damage.qc Damage()).
  - *notes:* Two coupled features. (A) HEADSHOT DAMAGE: server/weapons/tracing.qc is canonical. Headshot() (220) computes the head AABB from the target's bbox+view_ofs (the '0.6'/'1.3*view_ofs_z-0.3*maxs_z' construction is the exact head box -- must mirror precisely) and trace_hits_box. The multiplier is applied in fireBullet_falloff (441-445: `damage *= headshot_multiplier; headshot=true;`) for bullet weapon…

### T17
- `qcsrc/common/gametypes/gametype/mayhem/sv_mayhem.qc` — **MayhemCalculatePlayerScore, mayhem_Initialize, PlayerDamage_SplitHealthArmor hook, GiveFragsForKill hook, Damage_Calculate hook, reset_map_…** — Canonical FFA Mayhem: the score=damage+frags algorithm and all gameplay hooks (the file a faithful Mayhem.cs mirrors)
- `qcsrc/common/gametypes/gametype/tmayhem/sv_tmayhem.qc` — **tmayhem_Initialize, tmayhem_DelayedInit, PlayerDamage_SplitHealthArmor hook (SAME_TEAM), Damage_Calculate hook (mirrordamage=0), g_tmayhem_…** — Canonical Team Mayhem: team variant (GameRules_teams, SAME_TEAM friendly-fire handling, no mirror damage) reusing MayhemCalculatePlayerScore
- `qcsrc/common/gametypes/gametype/mayhem/sv_mayhem.qh` — **REGISTER_MUTATOR(mayhem), mayhem_Initialize decl, MayhemCalculatePlayerScore decl** — Mayhem gametype registration + MUTATOR; declares forward/cvars for MayhemCalculatePlayerScore
- `qcsrc/common/gametypes/gametype/tmayhem/sv_tmayhem.qh` — **REGISTER_MUTATOR(tmayhem), autocvar_g_tmayhem_scoring_upscaler/kill_weight/damage_weight/disable_selfdamage2score** — Team Mayhem gametype registration + g_tmayhem_scoring_* autocvar declarations consumed by the shared scorer
- `qcsrc/common/gametypes/sv_rules.qh` — **GameRules_scoring_add_team, GameRules_limit_score, GameRules_limit_lead, GameRules_teams** — GameRules_scoring_add_team / GameRules_limit_score / GameRules_teams macros the Mayhem modes invoke
  - → *port:* new src/XonoticGodot.Common/Gameplay/GameTypes/Mayhem.cs + TeamMayhem.cs, src/XonoticGodot.Common/Gameplay/Registries.cs, src/XonoticGodot.Server/GameWorld.cs, src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs (PlayerDamage_SplitHealthArmor hook)
  - *notes:* Mayhem and Team Mayhem are fully defined by sv_mayhem.qc + sv_tmayhem.qc. The score=damage+frags formula is MayhemCalculatePlayerScore() (defined in sv_mayhem.qc, shared by both): it accumulates .total_damage_dealt and converts (damage/start_hp+armor)*upscaler*damage_weight + (kills-teamkills-suicides)*upscaler*frag_weight into SCORE via GameRules_scoring_add_team. Damage accrual is the PlayerDam…

### T18
- `qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qc` — **ons_GeneratorDamage, ons_ControlPoint_Touch, ons_GeneratorSetup, Onslaught_CheckWinner, Onslaught_RoundStart, ons_ScoreRules, round_handler…** — Onslaught generator+control-point combat and round flow (the CP/generator combat half of T18)
- `qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qh` — **ST_ONS_GENS, onslaught generator/controlpoint field decls** — Onslaught constants/entity fields (generator/CP health, link state, ST_ONS_GENS) backing the combat code
- `qcsrc/common/gametypes/gametype/ctf/sv_ctf.qc` — **ctf_Handle_Throw, ctf_Handle_Retrieve, ctf_CheckPassDirection, ctf_Handle_Drop (DROP_PASS), pass_target/pass_sender handling, FlagThink pas…** — CTF flag passing/throwing/retrieving logic (the CTF passing half of T18)
- `qcsrc/common/gametypes/gametype/ctf/sv_ctf.qh` — **FLAG_PASSING, DROP_PASS, PICKUP_*, score_assist, pass_distance, pass_target, pass_sender** — CTF passing constants/state (FLAG_PASSING, DROP_PASS, pass_* fields, score_assist) the throw/retrieve code uses
- `qcsrc/common/gametypes/gametype/domination/sv_domination.qc` — **domination_roundbased branches, ScoreRules_dom, dom_Captured_Checker / point capture (TeamScore_AddToTeam ST_DOM_CAPS/ST_DOM_TICKS), round_…** — Domination round variant vs tick variant branching (the Dom round-variant half of T18)
- `qcsrc/common/gametypes/gametype/domination/sv_domination.qh` — **domination_roundbased, ST_DOM_TICKS, ST_DOM_CAPS, autocvar_g_domination_roundbased*** — Domination round-variant flag + team-score column constants
- `qcsrc/server/race.qc` — **race_penalty, race_penalty_accumulator, race_penalty_reason, race_SendTime, race_ClearTime, race_checkpoint trigger applying penalty** — Race penalty accumulation and penalized time reporting (the Race penalty half of T18)
- `qcsrc/common/gametypes/gametype/race/sv_race.qc` — **race_ScoreRules (ST_RACE_LAPS), WinningCondition_Race (always overtime), WinningCondition_QualifyingThenRace, g_race_qualifying handling** — Race team laps scoring and overtime/qualifying winning conditions (the Race team+overtime half of T18)
- `qcsrc/server/round_handler.qc` — **round_handler_Spawn, round_handler_Init, round_handler_Think, round_handler_IsRoundStarted** — Shared round driver (spawn/think/can-start/winner callbacks) used by Onslaught and round-based Domination
  - → *port:* src/XonoticGodot.Common/Gameplay/GameTypes/Onslaught.cs, Ctf.cs, Domination.cs, Race.cs; src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs (generator/flag damage hooks); RaceRecords.cs
  - *notes:* Four independent sub-features, each with a distinct canonical file. (1) Onslaught CP/generator combat: server-side sv_onslaught.qc — ons_GeneratorDamage (event_damage on generators), ons_ControlPoint_Touch/capture, Onslaught_CheckWinner, ons_GeneratorSetup, round_handler wiring; sv_onslaught.qh holds the constants/struct fields. (2) CTF passing: sv_ctf.qc — ctf_Handle_Throw (DROP_PASS), ctf_Handl…

### T19
- `qcsrc/common/mutators/mutator/weaponarena_random/sv_weaponarena_random.qc` — **weaponarena_random PlayerSpawn/GiveFragsForKill/SetStartItems, W_RandomWeapons, g_weaponarena_random, g_weaponarena_random_with_blaster** — weaponarena_random mutator: on PlayerSpawn picks a random subset of the arena weaponset (W_RandomWeapons); on GiveFragsForKill swaps the killing weapon for a fresh random one. SetStartItems reads g_w…
- `qcsrc/common/mutators/mutator/new_toys/sv_new_toys.qc` — **nt SetStartItems/SetWeaponreplace/FilterItem, nt_GetReplacement/nt_GetFullReplacement, nt_IsNewToy, NT_AUTOREPLACE_*, WEP_FLAG_MUTATORBLOCK…** — new_toys mutator: weapon-replacement engine that swaps stock guns for hagar->seeker/devastator->minelayer/machinegun->hlac/vortex->rifle via SetStartItems + SetWeaponreplace, unblocks the new-toy wea…
- `qcsrc/common/mutators/mutator/rocketminsta/sv_rocketminsta.qc` — **rm Damage_Calculate/PlayerDies, autocvar_g_rm, autocvar_g_rm_laser, DEATH_ISWEAPON(WEP_DEVASTATOR/WEP_ELECTRO)** — rocketminsta mutator (registers under instagib): Damage_Calculate zeroes devastator self-damage / electro-laser self/round damage; PlayerDies bumps damage to 1000 to always gib on a vaporizer-class d…
- `qcsrc/common/mutators/mutator/rocketflying/sv_rocketflying.qc` — **rocketflying EditProjectile/AllowRocketJumping, autocvar_g_rocket_flying, autocvar_g_rocket_flying_disabledelays** — rocketflying mutator: EditProjectile kills the rocket/mine detonate delay (sets spawnshieldtime=time); AllowRocketJumping forces rocket-jump. Trivial; depends on T3's EditProjectile chain.
- `qcsrc/common/mutators/mutator/vampirehook/sv_vampirehook.qc` — **vh GrappleHookThink, autocvar_g_vampirehook_damage/damagerate/health_steal/teamheal, Damage(), Heal(), WEP_HOOK** — vampirehook mutator: on GrappleHookThink, a hook attached to an enemy deals periodic damage and heals the owner (Damage + Heal). Depends on the GrappleHookThink chain (server/hook.qc:137) and Heal() …
- `qcsrc/common/mutators/mutator/stale_move_negation/sv_stale_move_negation.qc` — **mutator_smneg Damage_Calculate, smneg_multiplier, x_smneg_weight[REGISTRY_MAX(Weapons)], autocvar_g_smneg/_bonus/_bonus_asymptote/_cooldown…** — stale_move_negation mutator: Damage_Calculate scales damage+force by a per-weapon 'weight' that grows with use and decays for unused weapons (atan/tan multiplier curve). Per-attacker weapon-weight ar…
- `qcsrc/common/mutators/mutator/random_items/sv_random_items.qc` — **RandomItems_GetRandomItemClassName(WithProperty), RandomItems_GetRandomVanillaItemClassName, random_items_is_spawning, autocvar_g_random_lo…** — random_items mutator: replaces map item spawns with random items and drops random loot on death (Lyberta). The largest T19 mutator — item-classname randomization, map+loot probability cvars, and its …
- `qcsrc/common/mutators/mutator/random_gravity/sv_random_gravity.qc` — **random_gravity SV_StartFrame, gravity_delay, autocvar_g_random_gravity_min/max/positive/negative/negative_chance/delay, cvar_settemp(sv_gra…** — random_gravity mutator: on SV_StartFrame, randomly re-rolls sv_gravity within min/max every delay seconds (settemp-restored at match end). Depends on T3's SV_StartFrame chain.
- `qcsrc/common/mutators/mutator/globalforces/sv_globalforces.qc` — **mutator_globalforces PlayerDamage_SplitHealthArmor, damage_explosion_calcpush, autocvar_g_globalforces/_noself/_self/_range** — globalforces mutator: PlayerDamage_SplitHealthArmor applies the damage knockback force to ALL players in range (not just the target), scaled per-player. Depends on the PlayerDamage_SplitHealthArmor c…
- `qcsrc/common/mutators/mutator/physical_items/sv_physical_items.qc` — **physical_items Item_Spawn, physical_item_think/touch/damage, spawn_origin/spawn_angles, autocvar_g_physical_items/_damageforcescale/_reset,…** — physical_items mutator: on Item_Spawn, replaces each pickup with an invisible trigger + a MOVETYPE_PHYSICS ghost model so items physically tumble/bounce. Engine-dependent (DP_PHYSICS_ODE). Depends on…
- `qcsrc/common/mutators/mutator/spawn_near_teammate/sv_spawn_near_teammate.qc` — **spawn_near_teammate Spawn_Score/PlayerSpawn, msnt_lookat/msnt_timer, snt_ofs[6], SPAWN_PRIO_NEAR_TEAMMATE_FOUND/SAMETEAM, tracebox_hits_tri…** — spawn_near_teammate mutator: Spawn_Score biases spawnpoints toward living teammates (PVS + distance) and PlayerSpawn can override the spawn origin to a traced spot near a teammate, facing away. Depen…
- `qcsrc/common/mutators/mutator/spawn_unique/sv_spawn_unique.qc` — **spawn_unique Spawn_Score/PlayerSpawn, su_last_point, autocvar_g_spawn_unique** — spawn_unique mutator: Spawn_Score drops the priority of the player's last spawnpoint to ~0.1, PlayerSpawn records su_last_point — so you don't respawn on the same spot twice. Tiny; depends on Spawn_S…
  - → *port:* new src/XonoticGodot.Common/Gameplay/Mutators/{WeaponArenaRandomMutator,NewToysMutator,RocketMinstaMutator,RocketFlyingMutator,VampireHookMutator,StaleMoveNegationMutator,RandomItemsMutator,RandomGravityMutator,GlobalForcesMutator,PhysicalItemsMutator,SpawnNearTeammateMutator,SpawnUniqueMutator}.cs, src…
  - *notes:* 12 remaining mutators, all gated on T3 (the hook chains + activation loop). They map onto these hook chains: weaponarena_random -> PlayerSpawn+GiveFragsForKill+SetStartItems; new_toys -> SetStartItems+SetWeaponreplace+FilterItem; rocketminsta -> Damage_Calculate+PlayerDies; rocketflying -> EditProjectile(+AllowRocketJumping); vampirehook -> GrappleHookThink; stale_move_negation -> Damage_Calculat…

### T20
- `data/effectinfo.txt` — **effect <name> blocks with countabsolute/count/type/tex/color/size/alpha/velocity*/gravity/bounce/airfriction/originjitter/velocityjitter/st…** — THE particle effect database (9043 lines): every named effect's particle layers (type/color/size/alpha/velocity/gravity/bounce/tex/stain/orientation). The port must load and interpret this verbatim i…
- `Base/darkplaces/cl_particles.c` — **CL_ParticleEffectInfo_t struct (line ~50), CL_ParseEffectInfo (the textfile parser, ~line 315, handles type alphastatic/spark/blood/smoke/d…** — THE engine particle system that parses effectinfo.txt and spawns/simulates particles + decals. Canonical reference for the parser grammar and the particle/decal spawn+physics the port replaces its he…
- `qcsrc/common/effects/all.inc` — **EFFECT(istrail, NAME, "effectinfo_string") entries (EXPLOSION_*, *_IMPACT, *_MUZZLEFLASH, TR_BLOOD/TR_SLIGHTBLOOD, blood/bloodshower, etc.)** — The EFFECT() registry: maps each game effect name to its effectinfo.txt string and istrail flag, networked by ID. The port needs this name↔id table to resolve EFFECT_* references used throughout serv…
- `qcsrc/common/effects/all.qh` — **REGISTER_REGISTRY(Effects), #define EFFECT(...) REGISTER(Effects, EFFECT, name, m_id, Create_Effect_Entity(realname, istrail)), Create_Effe…** — Defines the EFFECT macro and the Effects registry that all.inc populates; Create_Effect_Entity binds the effectinfo name to a registered effect entity.
- `qcsrc/common/effects/qc/casings.qc` — **SpawnCasing (SVQC), NET_HANDLE(casings) + Casing_Draw + Casing_Touch + Casing_Damage (CSQC), MDL_CASING_SHELL/BULLET, ListNewChildRubble/Li…** — Shell/bullet CASINGS: server SpawnCasing networking + CSQC casing entity spawn, MOVETYPE_BOUNCE physics, touch sounds, alpha fade, count limiting. Replaces the heuristic casing class.
- `qcsrc/common/effects/qc/gibs.qc` — **Violence_GibSplash/_At/_SendEntity (SVQC), NET_HANDLE(net_gibsplash) + TossGib + Gib_Draw + Gib_Touch + Gib_setmodel + new_te_bloodshower (…** — Model-GIBS + blood: server Violence_GibSplash networking + CSQC gib tossing (TossGib), gib model selection by species, bounce physics, blood trail/pointparticles, gentle/particlegibs modes. Replaces …
- `qcsrc/common/effects/qc/damageeffects.qc` — **Damage_DamageInfo/_SendEntity (SVQC), NET_HANDLE(ENT_CLIENT_DAMAGEINFO) + DamageEffect + DamageEffect_Think (CSQC, __pointparticles of spec…** — DECALS + impact/blood damage effects: server Damage_DamageInfo networking + CSQC DAMAGEINFO handler that spawns DamageEffect particles (blood, scorch decals via effectinfo) and per-weapon/per-vehicle…
  - → *port:* XonoticGodot/game/client/EffectInfoParticle.cs (effectinfo.txt-driven particle layers replacing 13 heuristic classes), plus new casing/gib/decal entity drivers (extend XonoticGodot/game/client/DemoProjectileDriver.cs / ClientEntityView path)
  - *notes:* Four distinct layers, all canonical: (1) DATA = effectinfo.txt; (2) ENGINE parser+spawner+decals = cl_particles.c; (3) NAME→ID registry = effects/all.inc + all.qh + effect.qh; (4) the three CSQC effect entities that consume effects = casings.qc / gibs.qc / damageeffects.qc. Casings & gibs are server-spawned, networked as temp entities, then simulated client-side with Movetype_Physics_MatchTicrate…

### T21
- `qcsrc/client/hud/crosshair.qc` — **HUD_Crosshair (main), TrueAimCheck + EnemyHitCheck + TrueAim_Init (SHOTTYPE_HITTEAM/HITOBSTRUCTION/HITWORLD/HITENEMY via traceline/tracebox…** — THE entire crosshair subsystem: true-aim hit classification (enemy/team/world/obstruction), per-weapon crosshair image + size, color modes, weapon-switch fade transition, charge/reload rings, hit/pic…
- `qcsrc/client/hud/crosshair.qh` — **wcross_* declarations, trueaim/trueaim_rifle entities, SHOTTYPE_* constants, autocvar_crosshair_hittest/per_weapon/ring*/dot/hitindication/…** — Header for crosshair.qc: declares the crosshair persisted-state globals and the autocvar_crosshair_* set the port binds.
  - → *port:* XonoticGodot/game/hud/CrosshairPanel.cs (extend to true-aim coloring + weapon-switch transition), XonoticGodot/game/net/NetHud.cs (crosshair on net path)
  - *notes:* True-aim relies on engine traceline/tracebox against DPCONTENTS_BODY/CORPSE/SOLID and entcs team lookup (entcs_GetTeam) — the trace primitive is engine-level but the shot-origin reconstruction (decompressShotOrigin of STAT(SHOTORG)) and W_SetupShot parity is all here. The weapon-switch transition is the wcross_name_changestarttime/changedonetime cross-fade in HUD_Crosshair (must match W_SetupShot…

### T22
- `qcsrc/common/mapobjects/triggers.qc` — **SUB_UseTargets, SUB_UseTargets_Ex(this, actor, trigger, preventReuse, skiptargets), SUB_UseTargets_PreventReuse, SUB_UseTargets_SkipTargets…** — Defines SUB_UseTargets / SUB_UseTargets_Ex — the shared target-firing primitive that EVERY logic gate and target_* utility calls (handles .delay deferral via DelayedUse, killtarget removal, message c…
- `qcsrc/common/mapobjects/trigger/flipflop.qc` — **spawnfunc(trigger_flipflop), flipflop_use, START_ENABLED, .state** — trigger_flipflop logic gate: passes only every second trigger event through (toggles .state, fires targets on odd). START_ENABLED spawnflag. reset = self-spawnfunc.
- `qcsrc/common/mapobjects/trigger/monoflop.qc` — **spawnfunc(trigger_monoflop), monoflop_use, monoflop_fixed_use, monoflop_think, monoflop_reset, MONOFLOP_FIXED** — trigger_monoflop logic gate: turns one trigger event into an on then off (after .wait), via think. MONOFLOP_FIXED variant ignores re-triggers while active.
- `qcsrc/common/mapobjects/trigger/multivibrator.qc` — **spawnfunc(trigger_multivibrator), multivibrator_send, multivibrator_toggle, multivibrator_reset, START_ENABLED, .phase/.wait/.respawntime** — trigger_multivibrator logic gate: repeatedly emits trigger events on a wait/respawntime duty cycle with phase offset; toggleable via use. START_ENABLED.
- `qcsrc/common/mapobjects/trigger/disablerelay.qc` — **spawnfunc(trigger_disablerelay), trigger_disablerelay_use, ACTIVE_ACTIVE, ACTIVE_NOT** — trigger_disablerelay logic gate: toggles .active (ACTIVE<->NOT) of all entities matching .target — flips relays on/off.
- `qcsrc/common/mapobjects/trigger/relay_if.qc` — **spawnfunc(trigger_relay_if), trigger_relay_if_use, RELAYIF_NEGATE** — trigger_relay_if logic gate: fires targets only if cvar(.netname) == cvar(.message) (with RELAYIF_NEGATE inversion). A cvar-conditional relay.
- `qcsrc/common/mapobjects/trigger/relay_teamcheck.qc` — **spawnfunc(trigger_relay_teamcheck), trigger_relay_teamcheck_use, RELAYTEAMCHECK_INVERT, RELAYTEAMCHECK_NOTEAM, SAME_TEAM/DIFF_TEAM** — trigger_relay_teamcheck logic gate: fires targets only if activator's team matches/differs from .team (RELAYTEAMCHECK_INVERT) or has no team (RELAYTEAMCHECK_NOTEAM). Registers in g_saved_team for res…
- `qcsrc/common/mapobjects/trigger/relay_activators.qc` — **spawnfunc(relay_activate), spawnfunc(relay_deactivate), spawnfunc(relay_activatetoggle), relay_activators_use, ACTIVE_TOGGLE, .setactive** — relay_activate / relay_deactivate / relay_activatetoggle: set .active (via setactive or generic_setactive) on all entities matching .target. The activation-control relay family.
- `qcsrc/common/mapobjects/trigger/gamestart.qc` — **spawnfunc(trigger_gamestart), gamestart_use, adaptor_think2use, game_starttime, INITPRIO_FINDTARGET** — trigger_gamestart: fires its targets once at (game_starttime + .wait) via adaptor_think2use, then deletes itself. A map-load/round-start one-shot.
- `qcsrc/common/mapobjects/trigger/relay.qc` — **spawnfunc(trigger_relay), spawnfunc(target_relay), spawnfunc(target_delay), relay_use** — Defines the base relay family (trigger_relay/target_relay/target_delay) that relay_if/relay_teamcheck/disablerelay/relay_activators conceptually extend. Cited for the .active-gated relay_use pattern …
- `qcsrc/common/mapobjects/target/kill.qc` — **spawnfunc(target_kill), target_kill_use, DEATH_HURTTRIGGER, target_kill_reset** — target_kill: when used, deals 1000 DEATH_HURTTRIGGER damage to the activator (kills creatures/damagedbytriggers ents). Simple target utility.
- `qcsrc/common/mapobjects/target/give.qc` — **spawnfunc(target_give), target_give_use, ITEM_HANDLE(Pickup), GiveBuff, g_items** — target_give: gives the activator all items pointed to by .target (iterates g_items, calls Pickup/GiveBuff). Item-granting target utility.
- `qcsrc/common/mapobjects/target/items.qc` — **spawnfunc(target_items), target_items_use, GiveItems, tokenize_console, IT_UNLIMITED_AMMO, ITEM_Strength/Shield/Speed/Invisibility** — target_items: parses an item/weapon/resource list at spawn from .netname tokens (or give-string), then on use calls GiveItems to set the player's exact loadout. The most complex target utility (spawn…
- `qcsrc/common/mapobjects/target/spawn.qc` — **spawnfunc(target_spawn), target_spawn_use, target_spawn_edit_entity, target_spawn_useon, initialize_field_db, ON_MAPLOAD** — target_spawn: data-driven entity spawner/editor — creates or edits entities by writing fields parsed from .message (with $variable/$field replacement, offsets, randomization). ON_MAPLOAD fires at loa…
- `qcsrc/common/mapobjects/target/location.qc` — **spawnfunc(target_location), spawnfunc(info_location), g_locations, target_push_init** — target_location / info_location: registers a named location (in g_locations) used for location-name lookups (e.g. chat %l). Shares target_push_init.
- `qcsrc/common/mapobjects/target/spawnpoint.qc` — **spawnfunc(target_spawnpoint), target_spawnpoint_use, .spawnpoint_targ** — target_spawnpoint: when used, sets the activator's .spawnpoint_targ so their next respawn uses a chosen spawn. Map-scriptable spawn redirection.
- `qcsrc/common/mapobjects/target/changelevel.qc` — **spawnfunc(target_changelevel), target_changelevel_use, CHANGELEVEL_MULTIPLAYER, NextLevel, MapInfo_SwitchGameType, campaign_forcewin** — target_changelevel: ends/changes the level (NextLevel or changelevel(.chmap)), optionally switching gametype, with CHANGELEVEL_MULTIPLAYER fractional-player-count gating. Campaign win marker.
- `qcsrc/common/mapobjects/target/levelwarp.qc` — **spawnfunc(target_levelwarp), target_levelwarp_use, CampaignLevelWarp** — target_levelwarp: campaign-only — warps to a specific (.cnt) or next campaign level via CampaignLevelWarp. Small target utility.
- `qcsrc/common/mapobjects/target/speed.qc` — **spawnfunc(target_speed), target_speed_use, target_speed_calculatevelocity, SPEED_PERCENTAGE/ADD/LAUNCHER/POSITIVE_*/NEGATIVE_*, ENT_CLIENT_…** — target_speed: sets the activator's velocity from per-axis positive/negative/launcher/percentage/add spawnflags (full vector math). Net-linked (ENT_CLIENT_TARGET_SPEED) so it applies client-side too. …
- `qcsrc/common/mapobjects/func/door_secret.qc` — **spawnfunc(func_door_secret), fd_secret_use, fd_secret_move1..6, fd_secret_done, secret_touch, secret_blocked, secret_reset, DOOR_SECRET_1ST…** — func_door_secret: classic secret door — slides back then sideways (6-stage move chain via SUB_CalcMove), triggered by use/damage/touch, with sound stages and reset. Explicitly named in T22.
- `qcsrc/common/mapobjects/func/door_secret.qh` — **DOOR_SECRET_OPEN_ONCE, DOOR_SECRET_1ST_LEFT, DOOR_SECRET_1ST_DOWN, DOOR_SECRET_NO_SHOOT, DOOR_SECRET_YES_SHOOT** — Spawnflag constants for func_door_secret (DOOR_SECRET_*) that the move logic branches on. Needed alongside door_secret.qc.
- `qcsrc/common/mapobjects/func/conveyor.qc` — **spawnfunc(func_conveyor), spawnfunc(trigger_conveyor), conveyor_think, conveyor_init, conveyor_send, g_conveyed, .conveyor, SetMovedir, Ini…** — func_conveyor / trigger_conveyor: pushes pushable entities in the volume along .movedir at .speed each frame (clients via velocity in playerphysics, others via setorigin+move_out_of_solid). Net-linke…
- `qcsrc/common/mapobjects/func/ladder.qc` — **spawnfunc(func_ladder), spawnfunc(func_water), func_ladder_think, func_ladder_init, func_ladder_send, g_ladders, g_ladderents, .ladder_enti…** — func_ladder / func_water: marks players inside the volume with .ladder_entity so player physics applies ladder/water climbing; also spawns bot ladder waypoints. Net-linked (ENT_CLIENT_LADDER). Explic…
- `qcsrc/common/mapobjects/trigger/magicear.qc` — **spawnfunc(trigger_magicear), trigger_magicear_processmessage, trigger_magicear_processmessage_forallears, magicears, MAGICEAR_* spawnflags,…** — trigger_magicear: a chat/tuba pattern-matching 'logic' trigger — matches say/teamsay/tell text (with wildcards) or tuba note sequences and fires targets / replaces message text. Adjacent to the logic…
  - → *port:* XonoticGodot/src/XonoticGodot.Common/Gameplay/MapObjects/Triggers.cs (add FlipFlop/MonoFlop/Multivibrator/Relay-if/DisableRelay/RelayActivators/RelayTeamcheck/Gamestart/MagicEar setups + target_* utility setups) and a new XonoticGodot/src/XonoticGodot.Common/Gameplay/MapObjects/TargetUtilities.cs for target_kill/give/…
  - *notes:* T22 is the 'tail' of map objects: every logic-gate and target_* utility is a small spawnfunc whose canonical source is its own .qc under mapobjects/trigger/ or mapobjects/target/, and the shared dependency is SUB_UseTargets (the target-firing dispatcher) in triggers.qc — port that first if not already done. Registry status in the existing port (MapObjectsRegistry.cs): ALREADY registered = trigger…

### T23
- `qcsrc/menu/xonotic/datasource.qc` — **StringSource / CvarStringSource; getEntry(i, returns); reload(filter); DataSource_true/false** — The DataSource abstraction the list backends share: StringSource / CvarStringSource provide the getEntry(i, returns)/reload(filter) contract that feeds list widgets from a tokenized string or a cvar.…
- `qcsrc/menu/xonotic/datasource.qh` — **CLASS(DataSource); METHOD getEntry/reload; StringSource_str/_sep; CvarStringSource_cvar** — DataSource class/method declarations + DataSource_true/false sentinel entities for datasource.qc.
- `qcsrc/menu/xonotic/demolist.qc` — **XonoticDemoList_getDemos; getDemos_for_ext (search_begin/search_getfilename); demoName; startDemo (playdemo)/timeDemo; DemoList_Filter_Chan…** — Demos list backend: enumerates demos/*.dem (1 subdir deep) via engine search_*, strips demos/ prefix + .dem suffix, decolorizes, sorts into a buffer; filter + refresh; startDemo/timeDemo issue playde…
- `qcsrc/menu/xonotic/screenshotlist.qc` — **XonoticScreenshotList_getScreenshots; getScreenshots_for_ext; screenshotName; previewScreenshot; startSlideShow/goScreenshot; listScreensho…** — Screenshots list backend: enumerates screenshots/*.{jpg,tga,png}, same prefix/suffix-strip + sort + filter as demolist; previews selected screenshot, slideshow, opens viewer.
- `qcsrc/menu/xonotic/soundlist.qc` — **XonoticSoundList_getSounds (search_begin sound/cdtracks/*.ogg); soundName; SoundList_Add/_Add_All; SoundList_Menu_Track_Change (menu_cdtrac…** — Music (cdtracks) list backend: enumerates sound/cdtracks/*.ogg via search, marks current/default menu_cdtrack, adds tracks to the playlist; filter + set/reset menu track.
- `qcsrc/menu/xonotic/playlist.qc` — **XonoticPlayList_addToPlayList / removeFromPlayList; music_playlist_list0 cvar; XonoticPlayList_startSound (cd play); configureXonoticPlayLi…** — Music PLAYLIST backend (right pane of the music player): the ordered play queue persisted in the music_playlist_list0 cvar; add/remove/move tracks and drive cd playback. Pairs with soundlist.qc for t…
- `qcsrc/menu/xonotic/skinlist.qc` — **XonoticSkinList_getSkins (search gfx/menu/*/skinvalues.txt); skinParameter (SKINPARM_NAME/TITLE/AUTHOR/PREVIEW); loadCvars/saveCvars (menu_…** — Menu-skins list backend: globs gfx/menu/*/skinvalues.txt, parses title/author per skin, resolves skinpreview image, binds menu_skin cvar, and applies via menu_restart. (This is the LIST/picker; the v…
- `qcsrc/menu/xonotic/statslist.qc` — **XonoticStatsList_getStats (PS_D_IN_DB / db_get); convertDate; orderstr sorting; PlayerStats_PlayerDetail_CheckUpdate (showNotify)** — Player-stats list backend: formats the stats DB into ordered display rows (joined/last-seen dates, time played, matches/wins/losses/win%, kills/deaths/ratio, per-gametype ranked/unranked) and sorts t…
- `qcsrc/common/playerstats.qc` — **PS_D_IN_DB / PS_D_IN_DB / PS_D_IN_EVL; PlayerStats_PlayerDetail (HTTP); PlayerStats_PlayerDetail_CheckUpdate; db_get keys overall/* and gam…** — The data SOURCE behind statslist: builds/holds the player-stats database (PS_D_IN_DB) and fetches detail from the stats server. statslist.qc only formats what this produces.
- `qcsrc/menu/xonotic/dialog_multiplayer_join_serverinfo.qc` — **XonoticServerInfoDialog_loadServerInfo (gethostcachestring/number SLIST_FIELD_*); QCSTATUS parse P/S/F/M/T; crypto_getencryptlevel/idfp/key…** — Server-info popup backend: pulls a selected server's fields from the engine host cache and parses the QC status blob (gametype:version + P/S/F/M/T flags), encryption level, pure status, stats flags; …
- `qcsrc/menu/xonotic/serverlist.qc` — **RegisterSLCategories / SLIST_CATEGORIES; CategoryForEntry/Override; gethostcache* SLIST_FIELD_*; refreshServerList; IsFavorite/IsPromoted/I…** — Server-browser (join) list backend: the host-cache-driven server list with categories, sorting, filtering, favorites/promoted/recommended — the source feeding the serverinfo popup. Include for the fu…
- `qcsrc/menu/xonotic/dialog_multiplayer_create_mapinfo.qc` — **XonoticMapInfoDialog_loadMapInfo (MapInfo_Get_ByID); MapInfo_Map_bspname/title/author/description; MapInfo_Map_supportedGametypes; previewI…** — Create-game map-info popup backend: loads a map's metadata by id and shows bspname/title/author/description, preview image (maps/ then levelshots/ fallback), and which gametypes the map supports; Pla…
- `qcsrc/menu/xonotic/maplist.qc` — **XonoticMapList_* (g_maplist build); MapList_LoadMap; refilter via MapInfo; mapName/typeToString** — Map list backend for create-game: the selectable/orderable map list the map-info popup indexes into; builds entries from MapInfo, handles selection/add-to-playlist and LoadMap. Pairs with create_mapi…
- `qcsrc/common/mapinfo.qc` — **MapInfo_Get_ByID; MapInfo_Map_bspname/title/author/description/supportedGametypes; MapInfo_FilterGametype/_MapInfo_FilterGametype; MapInfo_…** — The data SOURCE behind both the map list and the create-game map-info popup: parses .mapinfo files, filters by gametype/features, and exposes per-map fields. maplist/create_mapinfo only present what …
  - → *port:* XonoticGodot/game/menu/dialogs/DialogMediaDemo.cs (demos), DialogMediaScreenshot.cs + XonoticGodot/game/ScreenshotHook.cs (screenshots), DialogMediaMusicPlayer.cs (music list+playlist — currently empty/inert per its header), DialogServerInfo.cs (server-info popup), DialogMultiplayerProfile.cs (stats), Rebirt…
  - *notes:* Each list is its own backend file (logic lives in the per-list .qc, not an umbrella). Two cited files are the underlying data SOURCES, not list widgets: playerstats.qc (stats DB for statslist) and mapinfo.qc (map metadata for maplist + create_mapinfo) — a faithful port must port these or they have nothing to list. demos/screenshots/skins all use the engine search_*/buffer/file APIs; music uses se…

### T24
- `Base/darkplaces/model_alias.c` — **Mod_IDP0_Load (972, MDL/IDPO Quake morph), Mod_IDP2_Load (1324, MD2/IDP2 Quake2 morph), Mod_ZYMOTICMODEL_Load (1768, ZYM skeletal: lump_bon…** — Canonical loaders for the four formats in question. Dispatch table is in model_shared.c (IDPO/IDP2/ZYMOTICMODEL/ACTRHEAD magics, 48-62). MDL/MD2 are vertex-morph (like MD3); ZYM/PSK are skeletal (lik…
- `Base/darkplaces/model_alias.h` — **mdl/md2 on-disk structs; zymtype1header_t + zymlump_t + zymbone_t; pskpnts_t/pskvtxw_t/pskface_t/pskmatt_t/pskboneinfo_t/pskrawweights_t/ps…** — On-disk struct definitions for each format the importer must read (endianness, field order); the .h the parser mirrors alongside the .c loader.
  - → *port:* new XonoticGodot/src/XonoticGodot.Formats/{Mdl,Md2,Zym,Psk}/*.cs (only those that survive the cut), XonoticGodot/game/assets/AssetLoader.cs; ZYM/PSK builders would reuse XonoticGodot/game/assets/models/IqmBuilder.cs skinning path; MDL/MD2 would reuse Md3Builder.cs morph path
  - *notes:* SHIPPED-CONTENT VERIFICATION (the task's gating clause) — counted across Base/data: MDL=19 files (real: models/bullet.mdl, casing_shell/steel, plasma, hagar/rocket/elaser missiles, gibs/chunk, items/a_bullets, runematch/{rune,curse}, beam.mdl, etc. — all in data.pk3dir, legacy projectile/casing/gib props), ZYM=2 (models/pomp/pomp.zym, models/train.zym in xonotic-maps.pk3dir), PSK=0, MD2=0. CONCLU…

### T25
- `qcsrc/server/bot/default/scripting.qc` — **bot_queuecommand, bot_dequeuecommand, bot_readcommand, bot_execute_commands, bot_cmd_eval, bot_cmd_*executors, bot_cmdhelp, bot_list_comman…** — THE bot scripting (bot_cmd) engine: per-bot command queue (ringbuffer in a stringbuf), the script parser/compiler, and the per-command executors for every BOT_CMD_* (pause/wait/turn/moveto/aim/pressk…
- `qcsrc/server/bot/default/scripting.qh` — **BOT_CMD_PAUSE/WAIT/TURN/MOVETO/AIM/PRESSKEY/SELECTWEAPON/IMPULSE/BARRIER/CONSOLE/SOUND/IF/ELSE/FI, BOT_CMD_COUNTER, BOT_CMD_PARAMETER_*, BO…** — Bot scripting constants/state: the BOT_CMD_* opcode enum (NULL..DEBUG_ASSERT_CANFIRE) + BOT_CMD_COUNTER, parameter-type tags (FLOAT/STRING/VECTOR), exec-status (IDLE/PAUSED/WAITING), per-command resu…
- `qcsrc/server/bot/default/waypoints.qc` — **waypoint_spawn, waypoint_spawn_fromeditor, waypoint_remove_fromeditor, waypoint_saveall, waypoint_load_links, waypoint_start_hardwiredlink,…** — THE waypoint editor + persistence: spawn/remove/link waypoints from the in-game editor, hardwired links, lock, unreachable-detection, symmetry axis/origin helpers, and save/load of .waypoints/.waypoi…
- `qcsrc/server/bot/default/waypoints.qh` — **WAYPOINTFLAG_GENERATED/ITEM/TELEPORT/JUMP/LADDER/CROUCH/SUPPORT/CUSTOM_JP, WPFLAGMASK_NORELINK, waypoint_* prototypes** — Waypoint constants/state shared by editor + navigation: WAYPOINTFLAG_* bitset (GENERATED/ITEM/TELEPORT/JUMP/LADDER/CROUCH/SUPPORT/PERSONAL/...) and the relink mask. The flag semantics the editor sets…
- `qcsrc/server/bot/default/bot.qc` — **find_bot_by_name, find_bot_by_number, bot_fixcount, bot_setnameandstuff, bot_think, bot_clientconnect, bot_serverframe, bot_relinkplayerlist** — Bot lifecycle/identity the scripting verbs operate on: find_bot_by_name / find_bot_by_number (target resolution for 'bot_cmd <client>'), bot_fixcount (setbots), bot connect/disconnect, the main bot t…
- `qcsrc/server/bot/default/bot.qh` — **.isbot, .bot_cmd_current, bot keys, bot_fixcount, find_bot_by_* prototypes** — Bot state fields/flags the scripting + count commands read (skill, isbot, bot keys/movement intent set by scripted presskey/moveto).
- `qcsrc/server/bot/default/navigation.qc` — **tracewalk, navigation_findnearestwaypoint, navigation_markroutes, navigation_routerating, set_tracewalk_dest** — Navigation/routing the scripted movement opcodes lean on: tracewalk reachability (BOT_CMD_MOVETO/MOVETOTARGET), nearest-waypoint lookup, route marking/rating. Needed for a faithful moveto/aimtarget; …
- `qcsrc/server/bot/api.qh` — **bot_queuecommand, bot_cmdhelp, find_bot_by_*, waypoint_spawn/remove/saveall, .wp00..wp31 + mincost fields, g_waypoints, WAYPOINTFLAG_*** — Bot subsystem public API/field surface (waypoint .wpNN link fields & costs, WAYPOINTFLAG re-export, bot_* and waypoint_* prototypes, the g_waypoints IntrusiveList) — the contract the command dispatch…
- `qcsrc/server/command/sv_cmd.qc` — **GameCommand_bot_cmd, SERVER_COMMAND(bot_cmd)** — Dispatch glue: SERVER_COMMAND(bot_cmd) -> GameCommand_bot_cmd parses subcommands (reset/setbots/load/help/<client> <cmd>) and routes to bot_queuecommand / find_bot_by_* / bot_resetqueues. The 'bot_cm…
- `qcsrc/server/command/cmd.qc` — **ClientCommand_wpeditor, CLIENT_COMMAND(wpeditor)** — Dispatch glue: CLIENT_COMMAND(wpeditor) -> ClientCommand_wpeditor maps each action (spawn/remove/hardwire/lock/unreachable/saveall/relinkall/symaxis/symorigin) to the waypoint_* editor functions. The…
  - → *port:* XonoticGodot/src/XonoticGodot.Server/Bot/Waypoint.cs (waypoint model + editor/save-load — mirror waypoints.qc/.qh); a new bot-scripting unit mirroring scripting.qc/.qh (BOT_CMD opcode VM + queue) — no XonoticGodot file yet (BotBrain.cs/BotController.cs are the live AI, not the scripted-command VM); XonoticGodot/src/Re…
  - *notes:* Two sub-features: (1) bot_cmd scripting = scripting.qc/.qh (the BOT_CMD_* opcode VM + per-bot queue) — the heart, plus bot.qc for find_bot_by_*/bot_fixcount and sv_cmd.qc GameCommand_bot_cmd for the verb. (2) waypoint-editor commands = waypoints.qc/.qh (editor ops + save/load file format) plus cmd.qc ClientCommand_wpeditor for the verb dispatch. api.qh ties the field/prototype surface together. n…

### T26
- `qcsrc/lib/registry.qh` — **REGISTRY, REGISTER, REGISTER_4/REGISTER_5, REGISTRY_PUSH, REGISTER_REGISTRY, REGISTRY_SORT, REGISTRY_CHECK, REGISTRY_HASH, REGISTRY_DEFINE_…** — THE canonical analog the source generator replaces: QC's compile-time registry metaprogramming (the [[accumulate]] machinery). Defines REGISTRY(id,max), REGISTER(...), REGISTER_REGISTRY, REGISTRY_SOR…
- `qcsrc/common/monsters/all.qh` — **REGISTRY(Monsters), REGISTER_MONSTER, REGISTRY_CHECK(Monsters), get_monsterinfo, NullMonster, MON_Null** — Monster catalog declaration the generator must cover (T26 names Monster explicitly). Declares REGISTRY(Monsters, BITS(5)) + REGISTER_REGISTRY + REGISTRY_CHECK and the REGISTER_MONSTER(id,inst) wrappe…
- `qcsrc/common/monsters/monster.qh` — **CLASS(Monster,Object), monsterid, mr_setup/mr_think/mr_death/mr_pain/mr_anim, MON_FLAG_* spawnflags** — The Monster base CLASS whose ATTRIB defaults (m_name/netname/monsterid/m_color/m_mins/m_maxs) and mr_* method slots define what each registered monster instance carries — the C# Monster base type the…
- `qcsrc/common/turrets/all.qh` — **REGISTRY(Turrets), REGISTER_TURRET, REGISTRY_CHECK(Turrets), get_turretinfo, TR_PROPS_COMMON** — Turret catalog declaration the generator must cover. REGISTRY(Turrets, BITS(5)) + REGISTER_TURRET(id,inst) wrapper. Also the TR_PROPS_COMMON X-macro property table (config dump/parse), part of the sa…
- `qcsrc/common/turrets/turret.qh` — **CLASS(Turret,Object), m_id, tr_setup/tr_think/tr_death/tr_attack/tr_config, m_weapon** — Turret base CLASS — ATTRIB defaults and tr_* method slots each registered turret carries (the C# Turret base type).
- `qcsrc/common/vehicles/all.qh` — **REGISTRY(Vehicles), REGISTER_VEHICLE, REGISTRY_CHECK(Vehicles), VEH_FIRST, VEH_LAST, NullVehicle, VEH_Null** — Vehicle catalog declaration the generator must cover. REGISTRY(Vehicles, BITS(4)) + REGISTER_VEHICLE(id,inst) wrapper + Null sentinel + VEH_FIRST/VEH_LAST iteration bounds.
- `qcsrc/common/vehicles/vehicle.qh` — **CLASS(Vehicle,Object), vehicleid, vr_setup/vr_precache/vr_think, PlayerPhysplug** — Vehicle base CLASS — ATTRIB defaults and vr_* method slots each registered vehicle carries (the C# Vehicle base type).
  - → *port:* XonoticGodot/src/XonoticGodot.SourceGen/RegistryGenerator.cs (extend s_markerAttributeNames + Common's Registry<T> catalogs to Monster/Turret/Vehicle — currently MonsterAttribute is recognised-then-skipped, Turret/Vehicle absent); XonoticGodot/src/XonoticGodot.SourceGen/GeneratorHelpers.cs; XonoticGodot/src/XonoticGodot.Common/…
  - *notes:* Pure mechanical port of the [[accumulate]] registry layer; design already fixed by ADR-0003 (XonoticGodot/planning/decisions/ADR-0003-source-generators.md). The generator EXISTS and emits Weapon/Item/Mutator/GameType today; T26 = activate (cover Monster+Turret+Vehicle catalogs) and flip the call site. registry.qh is the load-bearing spec — note especially REGISTRY_SORT (heapsort by registered_id) and …

### T27
- `qcsrc/client/hud/hud_config.qc` — **HUD_Panel_ExportCfg (writes hud_skin/hud_panel_bg*/hud_dock*/hud_progressbar_*/_hud_panelorder + per-panel _pos/_size/_bg* via HUD_Write_Pa…** — THE in-game HUD configure-mode editor: consumes/writes hud_panel_*_pos/_size cvars via mouse drag/resize + keyboard arrows, panel collision snapping, grid, undo/copy-paste, Tab cycling, and the confi…
- `qcsrc/client/hud/hud_config.qh` — **highlightedPanel/highlightedAction/resizeCorner/panel_click_*/hud_configure_* globals, HUD_Write / HUD_Write_Cvar / HUD_Write_PanelCvar mac…** — Header for hud_config.qc: configure-mode state globals + the HUD_Write/HUD_Write_Cvar/HUD_Write_PanelCvar export helper macros.
- `qcsrc/client/hud/hud.qh` — **HUD_Panel_UpdatePosSize() macro (line ~413), REGISTER_HUD_PANEL table, PANEL_CONFIG_MAIN/CANBEOFF/NO flags, panel_enabled, panel_order[]** — Defines the panel registry + HUD_Panel_UpdatePosSize macro the editor reads/writes (hud_panel_<name>_pos/_size and the per-panel _bg/_bg_color/_bg_alpha/_bg_border/_bg_padding cvars), and PANEL_CONFI…
  - → *port:* New XonoticGodot/game/hud/HudConfigEditor (configure-mode interactions) consuming HudPanel.cs/HudManager.cs; skin-list backend → extend XonoticGodot/game/menu/framework/HudPanelCommon.cs. Menu side DialogHudSetupExit.cs already exists.
  - *notes:* The 'HUD skin-list backend' = the hud_skin cvar + the gfx/hud/<skin>/ path resolution (hud_skin_path) and the .cfg export naming (hud_<skin>_<cfgname>.cfg) — all driven from HUD_Panel_ExportCfg in hud_config.qc and hud_skin_path setup in hud.qc HUD_Main. The editor reads/writes the SAME hud_panel_* cvars the live panels (T10/T29) consume via HUD_Panel_UpdatePosSize, so hud.qh is shared canon. Ski…

### T28
- `qcsrc/menu/menu.qc` — **m_init_delayed (skinvalues.txt open/fallback chain); draw_currentSkin; fgets loop -> Skin_ApplySetting; precache_pic of skin *.tga; error '…** — THE skin LOADER: on menu init, opens gfx/menu/<menu_skin>/skinvalues.txt (with default + hardcoded 'wickedx' fallback), parses each 'KEY value' line and calls Skin_ApplySetting, then precaches the sk…
- `qcsrc/menu/skin.qh` — **SKINVECTOR/SKINFLOAT/SKINSTRING macros; Skin_ApplySetting(key,_value) switch; SKINBEGIN/SKINEND** — The skin value MODEL + setter: declares every SKIN<name> variable (vector/float/string) and generates Skin_ApplySetting(key,value) via the X-macro over skin-customizables.inc. Defines exactly how a s…
- `qcsrc/menu/skin-customizables.inc` — **SKINVECTOR(COLOR_..., def); SKINFLOAT(ALPHA_..., def); SKINSTRING(...); ~hundreds of SKIN* entries (LISTBOX/KEYGRABBER/MAPLIST/TEXT colors,…** — THE canonical list of every themeable skin key with its default — SKINCOLOR_*/SKINALPHA_*/SKINFADEALPHA_*/SKINWIDTH_* etc. The exhaustive schema the port's theme must reproduce (key names + default v…
- `qcsrc/lib/i18n.qh` — **string prvm_language; CTX(s) (caret-prefix strip + CTX_cache HashMap); ZCTX; language_filename (per-language file resolution)** — The QC i18n surface the whole codebase uses: the prvm_language global, the CTX()/ZCTX() msgctxt-disambiguation helpers (strip 'CTX^' prefix), and language_filename(). The _() translatable-string mark…
- `Base/darkplaces/prvm_edict.c` — **prvm_language cvar; PRVM_PO_Load / PRVM_PO_Lookup; PRVM_PO_ParseString / PRVM_PO_UnparseString; po_t; dotranslate_/notranslate_ opt-in; PRV…** — THE gettext ENGINE: on progs load, if prvm_language is set, loads <progs>.<lang>.po + common.<lang>.po and rewrites every translatable string global through the PO table; also implements the .pot dum…
- `qcsrc/menu/xonotic/languagelist.qc` — **XonoticLanguageList_getLanguages (fopen languages.txt); languageParameter (LANGPARM_ID/NAME/NAME_LOCALIZED/PERCENTAGE); loadCvars/saveCvars…** — Language picker backend: reads languages.txt (id / English name / localized name / translation %) into the list, binds _menu_prvm_language, and applies by setting prvm_language + menu_restart. The UI…
  - → *port:* NEW: a SkinTheme loader+model (parse XonoticGodot.Assets gfx/menu/<skin>/skinvalues.txt -> SKIN* table) — the port currently has only XonoticGodot/src/XonoticGodot.Formats/Sidecars/SkinFile.cs (player-MODEL skins, NOT menu theme) and no SKINCOLOR theme; every dialog hardcodes colors. NEW: an I18n/gettext layer (l…
  - *notes:* Two distinct subsystems. SKIN/THEME: loader = menu.qc; schema+setter = skin.qh + skin-customizables.inc (the .inc is the value-bearing list — both the var declarations and the parse switch are generated from it, so it is the authoritative theme schema). The skinvalues.txt files themselves live under gfx/menu/<skin>/ (asset data, now in XonoticGodot.Assets). LOCALIZATION: _() is compile-time (GMQCC), …

### T29
- `qcsrc/client/hud/hud.qh` — **REGISTER_HUD_PANEL(CHAT/PRESSEDKEYS/ENGINEINFO/PICKUP/QUICKMENU/STRAFEHUD,...), HUD_Panel_UpdatePosSize() macro, panel_pos/panel_size/panel…** — Panel registry + HUD layout cvars: binds CHAT/PRESSEDKEYS/ENGINEINFO/PICKUP/QUICKMENU/STRAFEHUD to their draw fns and defines the per-panel _pos/_size/_bg/_bg_color/_bg_alpha/_bg_border/_bg_padding c…
- `qcsrc/client/hud/hud.qc` — **HUD_Panel_LoadCvars, HUD_Panel_DrawBg, HUD_Panel_DrawProgressBar, HUD_Scale_Enable/Disable, Hud_Dynamic_Frame (hud_dynamic_follow/shake), H…** — Shared panel layout + draw infra all T29 panels call: per-panel cvar load (pos/size/bg/dock/fg_alpha), background/progressbar drawing, dynamic-HUD shake/follow, the HUD_Main panel-iteration loop. Can…
- `qcsrc/client/hud/panel/chat.qc` — **HUD_Chat, HUD_Panel_Chat_InputEvent, con_chatrect/con_chatwidth/con_chat plumbing, chat_maximized_scroll_ofs** — CHAT panel (#12): positions the engine chat area via con_chatrect/con_chat cvars + maximized-chat scroll input.
- `qcsrc/client/hud/panel/pressedkeys.qc` — **HUD_PressedKeys, STAT(PRESSED_KEYS), KEY_FORWARD/BACKWARD/LEFT/RIGHT/JUMP/CROUCH/ATCK/ATCK2, autocvar_hud_panel_pressedkeys_aspect/attack** — PRESSEDKEYS panel (#11): draws movement/attack key icons from STAT(PRESSED_KEYS) bitfield with aspect-forcing.
- `qcsrc/client/hud/panel/engineinfo.qc` — **HUD_EngineInfo, gettime(GETTIME_FRAMESTART), frametimeavg moving average, autocvar_hud_panel_engineinfo_fps_*** — ENGINEINFO panel (#13): FPS counter with moving-average smoothing.
- `qcsrc/client/hud/panel/pickup.qc` — **HUD_Pickup, Pickup_Update, HUD_Pickup_Time, last_pickup_item/last_pickup_count, STAT(LAST_PICKUP)** — PICKUP panel (#26): last-picked-up item icon + name + count + optional respawn timer, with fade-out.
- `qcsrc/client/hud/panel/quickmenu.qc` — **HUD_QuickMenu, QuickMenu_Buffer/Page arrays, QuickMenu_Open/Close/Mouse/Page, QuickMenu_IsOpened, QuickMenu_Default** — QUICKMENU panel (#23): radial/list quick-command menu — buffer parsing, pagination, mouse/key navigation, command execution.
- `qcsrc/client/hud/panel/strafehud.qc` — **HUD_StrafeHUD, StrafeHUD_GetStrafeplayer, StrafeHUD_DetermineJumpHeld/OnGround/OnSlick, strafe physics stat reads** — STRAFEHUD panel (#21) entrypoint + state: strafe-jump angle indicator; pulls the heavy drawing from its strafehud/ subdir.
- `qcsrc/client/hud/panel/strafehud/draw.qc` — **StrafeHUD_DrawStrafeHUD, StrafeHUD_DrawGradient, StrafeHUD_DrawSoftGradient, StrafeHUD_DrawStrafeArrow, StrafeHUD_DrawTextIndicator (+ util…** — STRAFEHUD rendering core: the actual angle-wheel/gradient/arrow/text drawing the HUD_StrafeHUD entry delegates to.
  - → *port:* New XonoticGodot/game/hud/*Panel.cs (ChatPanel, PressedKeysPanel, EngineInfoPanel, PickupPanel, QuickMenuPanel, StrafeHudPanel) via HudManager; HUD layout cvars → extend XonoticGodot/game/menu/framework/HudPanelCommon.cs + HudPanel.cs. Menu dialogs already exist (DialogHudPanelChat/PressedKeys/EngineInfo/Pic…
  - *notes:* 'HUD layout cvars' = the per-panel hud_panel_<name>_pos/_size/_bg* set, read by the HUD_Panel_UpdatePosSize macro in hud.qh and applied by HUD_Panel_LoadCvars/HUD_Panel_DrawBg in hud.qc — shared by ALL panels, so hud.qh+hud.qc are the canonical layout source for T29 (and T10/T27). STRAFEHUD is split across panel/strafehud.qc + the panel/strafehud/ subdirectory (draw.qc/draw_core.qc/util.qc/extra.…

### T30
- `qcsrc/common/physics/player.qc` — **PM_Accelerate, PM_AirAccelerate, CPM_PM_Aircontrol, PlayerJump, PM_walk/PM_air/PM_swim; sv_friction/accelerate/airaccel* consumption** — The QC player-movement logic the golden corpus must reproduce; movement_ref.c is transcribed from the preprocessed form of this. The trace loop captures runtime output of exactly this code path.
- `qcsrc/ecs/systems/physics.qc` — **sys_phys_update, sys_phys_simulate (the per-tick driver that calls PM_* and the movetypes)** — The top-level physics tick that movement_ref.c mirrors; the 'running Darkplaces' capture records the state this produces each frame for parity comparison.
- `Base/darkplaces/collision.c` — **Collision_TraceLineBrushFloat / Collision_TraceBrushBrushFloat / Collision_TracePointBrushFloat: sets trace->hittexture (681/688/693/731-73…** — Where the surface a trace hits gets its texture pointer recorded; trace->hittexture->name is the SOURCE of trace_t.DpHitTextureName. The port's TraceService must surface this from the BSP collision h…
- `Base/darkplaces/model_brush.c` — **Mod_Q1BSP_TraceLineAgainstSurfaces (1509), Mod_Q1BSP_TraceBox (955); Mod_CollisionBIH_TraceLineShared/TraceBrushShared (6861+) for Q3BSP; s…** — The BSP-level trace entry that walks the tree/BIH and assigns the hit surface's texture; the world-trace half of populating DpHitTextureName (Xonotic Q3BSP maps go through the BIH path).
  - → *port:* XonoticGodot/tools/movement-ref/movement_ref.c (extend from analytic world to DP capture), XonoticGodot/tests/XonoticGodot.Tests/golden/*.json + MovementParityTests.cs, XonoticGodot/src/XonoticGodot.Engine/Collision/TraceService.cs, XonoticGodot/src/XonoticGodot.Common/Services/Services.cs (TraceResult.DpHitTextureName field, line 2…
  - *notes:* Two distinct deliverables. (1) CLOSE THE GOLDEN-TRACE LOOP: today movement_ref.c (tools/movement-ref/) is an INDEPENDENT analytic reference over a handful of hand-built convex brushes (flat/step/ramp/water) transcribed from the preprocessed QC (player.qc + ecs/systems/physics.qc + movetypes/). 'Capture from a running Darkplaces' means instead recording real engine traces — so the canonical refere…

### T31
- `Base/darkplaces/model_brush.c` — **Mod_Q3BSP_Load* family: LoadFaces (5806), LoadLightmaps (5516), LoadTextures (5218), LoadBrushes/BrushSides (5326/5270), LoadEntities (5151…** — Reference behavior the BSP-parser tests must pin: lump offsets, face types, deluxemap split, brush/entity/PVS parsing. BspReader.cs cites model_q3bsp.h + these functions; tests assert the port matche…
- `Base/darkplaces/model_alias.c` — **Mod_IDP3_Load (1578, MD3), Mod_DARKPLACESMODEL_Load (2163, DPM), Mod_INTERQUAKEMODEL_Load (3219, IQM); plus model_alias.h / model_dpmodel.h…** — Reference for MD3/DPM (and IQM) parser tests: header/lump offsets (MD3 mesh offsets relative to mesh struct; DPM all big-endian), frame/tag/bone layout. Md3Reader.cs/DpmReader.cs already cite these.
- `Base/darkplaces/model_sprite.c` — **Mod_IDSP_Load (267), Mod_IDS2_Load (378), Mod_Sprite_SharedSetup (91); spritegn.h structs (SPR/SPR32/SPRHL types, frame intervals/groups)** — Reference for the sprite-parser tests (SpriteReader.cs cites it): magic dispatch, frame/group layout, palette handling.
- `qcsrc/common/monsters/monster.qc` — **Monster_Spawn, Monster_Move, Monster_Attack_*, monster think/state machine; sub-files in monsters/monster/*.qc and turrets/, vehicles/** — Source of the ~9k LOC of NPC behavior the port mirrors (XonoticGodot/src/XonoticGodot.Common/Gameplay/{Monsters,Turrets,Vehicles}); the NPC tests pin spawn/move/attack logic against this.
  - → *port:* XonoticGodot/tests/XonoticGodot.Tests/* (new tests) covering: XonoticGodot/src/XonoticGodot.Formats/{Bsp/BspReader,Md3/Md3Reader,Dpm/DpmReader,Iqm/IqmReader,Sprites/SpriteReader}.cs; XonoticGodot/game/assets/{bsp/BezierPatch,LightmapShader,SpriteBuilder}.cs + XonoticGodot/game/assets/models/{Md3,Dpm,Iqm}Builder.cs; XonoticGodot/src/R…
  - *notes:* Pure test-coverage task — the 'canonical source' is the code under test plus the Base references those parsers were ported FROM (so tests can build golden fixtures from real shipped assets and assert against DP's documented layout). Status is already in-progress (ModelInfoAndBlendTests landed). Three buckets: (a) PARSERS — each XonoticGodot.Formats reader already has a '// faithful to Mod_*' header nam…

### T32
- `qcsrc/server/command/sv_cmd.qc` — **SERVER_COMMAND(...) table (~line 1779+), GameCommand_adminmsg/allready/moveplayer/shuffleteams/gotomap/nextmap/gametype/setbots/trace/bbox,…** — Admin/debug server console verbs (rcon/sv_cmd). The full SERVER_COMMAND table + GameCommand_* handlers: adminmsg, allready, allspec, moveplayer, shuffleteams, lockteams, gotomap, nextmap, gametype, s…
- `qcsrc/server/command/cmd.qc` — **CLIENT_COMMAND(...) table (~line 1110+), ClientCommand_mv_getpicture, ClientCommand_suggestmap, ClientCommand_ready, SV_ParseClientCommand** — Per-client command verbs (cmd ...) incl. map-vote networking: mv_getpicture (ClientCommand_mv_getpicture, server->client mapshot transfer for the ballot), suggestmap, ready, spectate, join, selecttea…
- `qcsrc/server/command/vote.qc` — **VoteCommand_call/yes/no/abstain/stop/status/master, VoteCommand_parse/checknasty/checkinlist, VoteCount, VoteThink, Nagger_SendEntity, VOTE…** — Server-side voting engine + the vote console verbs (vcall/vyes/vno/vabstain/vstop/vstatus/vmaster). VoteCommand_* dispatch, command whitelist/nasty-check, master login, VoteCount, and the Nagger enti…
- `qcsrc/server/command/vote.qh` — **VOTE_SELECT_ABSTAIN/REJECT/ACCEPT, VOTE_NULL/NORMAL/MASTER, vote_called, vote_accept_count, autocvar_sv_vote_*, VC_ASGNMNT_*** — Vote constants/state the engine and ballot share: VOTE_NULL/NORMAL/MASTER, VOTE_SELECT_*, sv_vote_* autocvars, vote_caller/called/endtime/accept/reject counts, nagger globals.
- `qcsrc/server/mapvoting.qc` — **GameTypeVote_Type_FromString, GameTypeVote_AvailabilityStatus, GameTypeVote_GetMask, MapVote_Init/Think/Tick, MapVote_SendEntity, MapVote_S…** — Server side of GameTypeVote + the map-vote network ballot: builds the candidate list, networks the ballot entity (MapVote_SendEntity) with maps/pics/votes/flags, tallies selections, handles suggestio…
- `qcsrc/server/mapvoting.qh` — **MAPVOTE_COUNT, GTV_FORBIDDEN/AVAILABLE/CUSTOM, gametypevote** — Shared map/gametype vote constants: MAPVOTE_COUNT, gametypevote flag, GTV_* availability bits — the wire/array sizing both ends agree on.
- `qcsrc/client/mapvoting.qc` — **MapVote_Draw, MapVote_DrawAbstain, MapVote_Selection, MapVote_Buildcache, Cmd_MapVote_MapDownload, ent_mapvote, mv_entries/mv_pics/mv_votes…** — THE map-vote network ballot UI (CSQC). Receives the ballot entity (Net_MapVote_Picture / ReadInt24_t list), draws the map/gametype grid with screenshots, handles mouse+keyboard selection, abstain, ti…
- `qcsrc/client/mapvoting.qh` — **MapVote_Draw, MapVote_Draw_Export** — Client ballot entry points/exports (MapVote_Draw_Export, ReadPicture hook).
- `qcsrc/server/command/reg.qh` — **SERVER_COMMAND, CLIENT_COMMAND, REGISTRY(SERVER_COMMANDS)/CLIENT_COMMANDS, m_invokecmd, SERVER_COMMANDS_aliases** — Defines the SERVER_COMMAND/CLIENT_COMMAND registry macros + alias auto-generation (qc_cmd_sv) — the dispatch framework all the admin/client verbs above plug into; the port's command-table registratio…
  - → *port:* XonoticGodot/src/XonoticGodot.Server/Commands.cs (admin/debug + client verb dispatch — mirror sv_cmd.qc/cmd.qc/reg.qh); XonoticGodot/src/XonoticGodot.Server/VoteController.cs (mirror vote.qc/vote.qh); XonoticGodot/src/XonoticGodot.Server/MapVoting.cs (mirror server/mapvoting.qc — GameTypeVote + ballot send); plus a CSQC-side ba…
  - *notes:* Three sub-features, distinct sources: (1) admin/debug verbs = sv_cmd.qc (+ cmd.qc for client verbs, reg.qh for the macro). (2) GameTypeVote = the GameTypeVote_* functions in server/mapvoting.qc (NOT vote.qc). (3) map-vote network ballot UI = the server sender in server/mapvoting.qc + the receiver/renderer in client/mapvoting.qc. The generic /vyes /vcall vote verbs themselves live in vote.qc. Note…

### T33
- `Base/Makefile` — **CLIENTBIN=xonotic-sdl, SERVERBIN=xonotic-dedicated, targets: server/client/all, update-stable/update-beta (rsync-updater)** — Reference for the upstream client+dedicated-server build/packaging topology: produces xonotic-sdl (client) and xonotic-dedicated (server) binaries, plus rsync update/release distribution. The closest…
- `Base/darkplaces/host.c` — **Host_Init, Host_Frame, Host_Main, cls.state, dedicated gating** — Reference for the dedicated-server runtime topology being reproduced: the engine host loop and the cl_available / dedicated-server gating (a headless server runs the same Host_Frame without a client)…
  - → *port:* No QuakeC port target. XonoticGodot-side artifacts: XonoticGodot/planning/decisions/ADR-0012-platform-scope.md (sanctions desktop client + headless dedicated-server scope, defers web/mobile); XonoticGodot/src/XonoticGodot.Server (the headless server host that gets the --headless / dedicated_server export); a new CI wo…
  - *notes:* Largely NOT a code-port task — it is build/CI/perf/packaging infrastructure with essentially NO canonical QuakeC source. (a) CI pipeline: pure infra, no Base source (upstream's own CI is GitLab-side, not in this checkout; Base/darkplaces/.travis.yml and Base/netradiant/.drone.yml exist but are for the C engine / radiant tooling, irrelevant to the C#/Godot port). (b) GC/alloc perf pass: a .NET pro…

- *(landed) bot-navigation perf pass* — `src/XonoticGodot.Server/Bot/Waypoint.cs`: (1) `TryParseVec` now strips the
  Quake `vtos` single-quote wrapper **before** splitting (vtos right-aligns small components → a leading space
  inside the quotes, e.g. `' 46.8 -380.6 536.0'`, which the old per-token trim mis-split) — the shipped
  `.waypoints`/`.cache`/`.hardwired` graphs previously parsed to **0 nodes**, silently forcing the ~219 ms O(N²)
  `AutoLink` tracewalk graph build on every map; (2) `ForMap` now loads the precompiled `.cache`/`.hardwired`
  link files (only `AutoLink`s when no cache ships) so the per-map graph build is ~8 ms, not ~219 ms; (3)
  `Nearest` collects→sorts→returns first reachable (caps the per-candidate `CanWalk` tracewalks to ~1: 1.17 →
  0.04 ms/call, ~29×); (4) A* `FindPath` reuses pooled buffers + heap. Net: full `BotBrain.Think` ~0.039
  ms/bot/tick on atelier (was ~0.075), allocs −35%. Verified by `tests/XonoticGodot.Tests/BotPerfBench.cs` (real
  atelier load) + a parse regression test in `BotNavTests`. **Still open:** the bot think is not yet called from
  the live server loop (`GameWorld`/`ServerNet` drive inert bots — `BotBrain.Think` has no caller outside tests),
  so `--host --bots N` doesn't yet exercise this; wiring havocbot_ai into `StartFrame` is a separate task.

### T34
- `qcsrc/client/view.qc` — **CSQC_UpdateView (single entrypoint for both NetGame & GameDemo equivalents), viewmodel_draw + viewmodel_animate (first-person weapon model:…** — Proves the reconciliation target: in Xonotic there is ONE first-person presentation path — CSQC_UpdateView — used identically for live play and demo playback (isdemo()/spectatee_status branch inside,…
- `qcsrc/client/main.qc` — **CSQC_Init, CSQC_InputEvent (shared input incl. View_InputEvent zoom scroll), CSQC_Parse_* (server messages that both live & demo consume), …** — CSQC entry/registration that wires CSQC_UpdateView + input + the demo-vs-live distinction; shows the demo path reuses the same view/HUD machinery (no parallel renderer). Reference for how a single sh…
  - → *port:* Reconcile XonoticGodot/game/net/NetGame.cs (SetupCameraAndHud / BuildViewState / ViewModel / NetHud) with XonoticGodot/game/GameDemo.cs into a shared first-person-view component; fold XonoticGodot/game/net/NetHud.cs and XonoticGodot/game/client/ViewEffects.cs + ViewModel.cs into that one component used by both.
  - *notes:* This is a port-architecture reconciliation (ADR-style), not a new Base feature. The canonical evidence is that Xonotic has NO split: NetGame and GameDemo in the port both re-implement what CSQC_UpdateView does once. So the 'source' to mirror is view.qc itself (plus main.qc for the live/demo wiring) — the lesson is that zoom/death-cam/chase/full-HUD/viewmodel/muzzleflash should be ONE component pa…

---

## 📎 Base/ source map — re-audit tasks T35–T61 (2026-06-07)

Same convention as above: `qcsrc/…` = `Base/data/xonotic-data.pk3dir/qcsrc/…`; cite the file in the port's
`// Port of <path>` header. Generated by the 18-subsystem re-audit (each ref read + adversarially verified).

### T35 — World-item spawn + touch pipeline
- `qcsrc/server/items/items.qc` — **StartItem (1007), Item_Touch (686), Item_GiveTo / Item_GiveAmmoTo (490-684, powerup *_finished timer block 598-643), Item_Show (130), Item_Respawn / Item_RespawnCountdown (236-313), the ITEM_IS_LOOT toss branch (1062-1098)** — the whole item-entity lifecycle; the port has only the give+respawn *tail* (`ItemPickupRules`).
- `qcsrc/server/items/spawning.qc` — **Item_Initialise (27), Item_IsDefinitionAllowed (17), the compat aliases item_armor1/armor25/health1/health25/health100/armor_large/health_large (99-105)**
- `qcsrc/common/items/item/{health,armor,ammo}.qh` — **SPAWNFUNC_ITEM(item_health_small/…/item_armor_mega/item_shells/…)** — the classname→def table
- `qcsrc/common/mutators/mutator/powerups/powerup/{strength,shield,invisibility,speed,fuelregen,jetpack}.qh` — **SPAWNFUNC_ITEM(item_strength/item_shield/item_invisible/item_invisibility/item_speed/item_fuel_regen/item_jetpack)** — powerup pickup items (effects already done in T11)
- `qcsrc/common/mutators/mutator/buffs/buffs.qh` — **spawnfunc(item_buff_##e) macro + buff/*.qc (13 buffs)** — buff pickup items (BuffsMutator already ports the effects/touch)
  - → *port:* `src/.../Gameplay/Items/*` (extend HealthItem/ArmorItem/AmmoItem + new powerup/buff item defs), `.../MapObjects/MapObjectsRegistry.cs` (register all item_*/weapon_* spawnfuncs), `src/XonoticGodot.Server/GameWorld.cs` (Item_Touch wire to `ItemPickupRules.GiveTo`)
  - *notes:* The single largest items gap — **nothing spawns pickups into the world**, so HealthItem/ArmorItem/AmmoItem + the whole `ItemPickupRules` engine are unreachable dead code (zero external callers). Also covers: weapon-stay (`g_weapon_stay` translucent item + ammo-from-stay path), the loot lifecycle (toss/`g_items_dropped_lifetime`/spawn-shield/NODROP-kill) so pinata/random/death-drop loot becomes collectable, and the full `GiveItems` operator grammar (`OP_MIN/MAX/PLUS/MINUS`, `all`/`allweapons`/`allammo`/`ALL` aggregates) shared with `target_items` + cheat give.

### T36 — Untracked-mode map-objective spawnfuncs
- `qcsrc/common/gametypes/gametype/assault/sv_assault.qc` — **spawnfunc(target_objective / target_objective_decrease / func_assault_destructible / func_assault_wall / target_assault_roundstart / target_assault_roundend / info_player_attacker / info_player_defender)**
- `qcsrc/common/gametypes/gametype/nexball/sv_nexball.qc` — **spawnfunc(nexball_redgoal / bluegoal / yellowgoal / pinkgoal / nexball_fault / nexball_out) + the ball spawn**
- `qcsrc/common/gametypes/gametype/invasion/sv_invasion.qc` — **spawnfunc(invasion_spawnpoint / invasion_wave / target_invasion_roundend)**
- `qcsrc/common/gametypes/gametype/cts/sv_cts.qc` + `qcsrc/server/race.qc` — **target_startTimer / target_stopTimer**
- `qcsrc/common/gametypes/gametype/keyhunt/sv_keyhunt.qc` — **item_kh_key** (KH spawns keys onto players at round start, so lower-impact)
  - → *port:* `src/.../Gameplay/MapObjects/MapObjectsRegistry.cs` (add `SpawnFuncs.Register` routing through `GametypeObjectiveSpawns.Sink`), `src/XonoticGodot.Server/GameWorld.cs` (`WireObjectiveSpawns` arms for Assault/Nexball/Invasion/Cts), `.../GameTypes/{Assault,Nexball,Invasion,Cts}.cs` (call their existing AddObjective/SpawnGoal/AddWave/SpawnStartTimer APIs)
  - *notes:* The gametype *logic* is fully ported + unit-tested via the direct API, but the **map-entity seam is absent on both sides** — the BSP entity lump silently drops these classnames, so an assault_/nexball_/invasion_/cts map has zero objectives → no win path. P0 for those modes.

### T37 — Vehicle runtime seam (board / input / impulse / return)
- `qcsrc/common/vehicles/sv_vehicles.qc` — **vehicles_touch (874), vehicles_enter (931), vehicle_use (522), vehicle_impulse (912), vehicles_setreturn/return/showwp (426-520), vehicle_initialize controller/targetname/ACTIVE_NOT/g_vehicles_delayspawn (1168-1287); MUTATOR_CALLHOOK(VehicleInit/Enter/Exit/Touch) at 1283/1072/848/876**
- `qcsrc/common/vehicles/vehicle/{racer,raptor,spiderbot,bumblebee}.qc` — **\*_frame (read PHYS_INPUT_BUTTON_*/movement/v_angle), \*_impulse (raptor/spiderbot W2MODE), bumblebee_touch/gunner_enter/gunner_exit**
  - → *port:* `src/.../Gameplay/Vehicles/*` (wire the existing Enter/CycleMode/SetMode/VehInput), `src/XonoticGodot.Engine/Simulation/*` (touch dispatch + seated-player frame), `game/net/{NetGame,ServerNet}.cs` (impulse routing + HUD stat), `.../Mutators/MutatorHooks.cs` (4 new hooks)
  - *notes:* `EnterVehicle`/`GunnerEnter`/per-vehicle `Frame`/`SetMode` all exist but are **orphaned** — no boarding path, `VehInput` never written (zero `VehInput =` writes), impulses never routed. The entire vehicle subsystem is dead code between "spawned" (T14) and "playable". P0 for vehicle maps.

### T38 — Minigame activation (lifecycle + cmd + client + menu)
- `qcsrc/common/minigames/sv_minigames.qc` — **start_minigame / join_minigame / part_minigame / end_minigame / invite_minigame / minigame_addplayer / ClientCommand_minigame; minigame_SendEntity + MSLE delta network**
- `qcsrc/common/minigames/cl_minigames.qc` + `cl_minigames_hud.qc` — **network_receive dispatch, active_minigame; HUD_MinigameMenu_Open/Close/ClickCreate/Join/Invite/CurrentGame/Quit/CustomEntry, HUD_MinigameHelp, HUD_Minigame_InputEvent/Mouse**
- `qcsrc/common/minigames/minigames.qc` + `minigame/{ttt,c4,nmm,pong,pp,ps,bd}.qc` — **minigame_SendEntity/CheckSend; each game's server_event/client_event(menu_show/menu_click/key_pressed), \*_hud_status; bd.qc bd_load_level/bd_save_level**
  - → *port:* `src/.../Gameplay/Minigames/*` (already has the rule engines + AIs), `game/hud/{MinigameRenderer,HudManager}.cs` (call `Show`/`OnMove`; add keyboard/two-tile/capture input + per-game status), `game/net/MinigameNetState.cs` (wire encode/decode), `src/XonoticGodot.Server/Commands.cs` (`minigame` verb + session mgr), new minigame HUD menu
  - *notes:* Entire subsystem **absent from the old TODO**; 8 games + framework ported but every entry point is unwired. `cmd minigame` is the ONLY way to start/join in Xonotic. Bulldozer also needs on-disk levels (ships none → no puzzles). A spawned background task (`task_b2187e72`) was also raised for this during the audit.

### T39 — Wire HavocBot AI into the live loop
- `qcsrc/server/bot/default/bot.qc` — **bot_serverframe / bot_think (called from server/main.qc StartFrame), bot_strategytoken rotation, bot_fixcount**
- `qcsrc/server/bot/default/havocbot/havocbot.qc` — **havocbot_ai, havocbot_movetogoal (jetpack/rocketjump nav), havocbot_checkdanger (lava/slime/trigger_hurt/sky), havocbot_dodge**
- `qcsrc/server/bot/default/navigation.qc` — **navigation_unstuck, botframe_updatedangerousobjects**
  - → *port:* `src/XonoticGodot.Server/GameWorld.cs` (construct `BotController`, call `Frame()` in `OnStartFrame`), `src/XonoticGodot.Server/Bot/*` (the AI body exists; add danger/unstuck), `game/net/{NetGame,ServerNet}.cs` (bot input → movement/fire pipeline; today bots get `ZeroInput`)
  - *notes:* `BotController`/`BotBrain.Think` are a complete, unit-tested `havocbot_ai` port but `new BotController` has **zero hits** repo-wide. This one wiring change makes the whole AI come alive. (T25 = scripted `bot_cmd` VM, separate.)

### T40 — Server combat-event emission
- `qcsrc/server/damage.qc` — **Obituary() (Obituary_WeaponDeath/SpecialDeath → wr_killmessage/wr_suicidemessage variant select + Send_Notification(MSG_INFO kill-feed, MSG_CENTER frag/typefrag)), the killcount/spree increment + ANNCE_KILLSTREAK_* / MULTIFRAG**
- `qcsrc/server/player.qc` — **PlayerSound(playersound_pain100/75/50/25/death/gasp/drown) (368-469), SND_ARMORIMPACT/BODYIMPACT1/2 (327-335)**
- `qcsrc/common/weapons/weapon/*.qc` — **wr_killmessage/wr_suicidemessage per weapon (e.g. devastator.qc:558 SPLASH vs DIRECT; electro BOLT/COMBO/ORBS; vortex.qc:320)**
- `qcsrc/common/notifications/all.inc` + `globalsound.qh` — **INFO_DEATH_*/CENTER_DEATH_*/ANNCE_KILLSTREAK_*/MULTIFRAG/COUNTDOWN_*, REGISTER_PLAYERSOUND, REGISTER_GLOBALSOUND(STEP/FALL/GIB)**
  - → *port:* `src/XonoticGodot.Server/Scores.cs` (Obituary → NotificationSystem), `src/.../Gameplay/Damage/DamageSystem.cs` (sounds at hit/death), `.../Sounds/SoundSystem.cs`, `src/XonoticGodot.Server/WarmupController.cs` + per-round controllers (countdown announce)
  - *notes:* The registry (`NotificationsList`, `SoundsList`) + the wire + the HUD/announcer consumers are all **built and wired** — only the gameplay *emission* is missing (`MatchController.OnFrag` is an empty hook; `Scores.Obituary` mutates score columns only). The single biggest "feel" cluster.

### T41 — Client feedback drivers
- `qcsrc/client/view.qc` — **HitSound() (894-980: STAT(HITSOUND_DAMAGE_DEALT_TOTAL)/TYPEHIT/KILL, cl_hitsound 0/1/2/3 + min/max/nom_pitch + antispam), HUD_Draw DrawCircleClippedPic rings (NADE_TIMER/CAPTURE_PROGRESS/REVIVE_PROGRESS, ~1006)**
- `qcsrc/client/announcer.qc` — **Announcer_Gamestart/Announcer_Time/Announcer_Countdown (3-2-1 + ANNCE_PREPARE + ANNCE_REMAINING_MIN_1/5 from GAMESTARTTIME/ROUNDSTARTTIME/TIMELIMIT), Announcer_Duel**
- `qcsrc/common/physics/player.qc` — **PM_Footsteps (689-708, GS_STEP/STEP_METAL cadence), PM_check_hitground (664-687, GS_FALL/FALL_METAL on landing)**
  - → *port:* `game/client/*`, `game/hud/{HudNotifications,CrosshairPanel}.cs`, `game/net/NetGame.cs` (network the damage-dealt-total stat), `src/.../Physics/PlayerPhysics.cs` (footstep/landing cadence)
  - *notes:* These are CLIENT-clock-driven (the server doesn't send them). T34 wired announce *routing*; the *driver* + hitsound + footsteps are still absent.

### T42 — Global overtime / sudden-death win layer
- `qcsrc/server/world.qc` — **CheckRules_World (1725-1861), InitiateSuddenDeath (1467), InitiateOvertime (1499), WinningCondition_Scores (1560: leadlimit + WinningConditionHelper_equality + fragsleft ANNCE_REMAINING_FRAG_*), GetWinningCode (1510); WINNING_STARTSUDDENDEATHOVERTIME**
  - → *port:* `src/XonoticGodot.Server/GameWorld.cs` (`CheckRulesAndIntermission` → add the timelimit/overtime/sudden-death branch), `.../Gameplay/GameTypes/*.cs` (report tie/equality)
  - *notes:* Today each score-mode latches `MatchEnded` on its frag/point limit only and the timelimit path just ends the match → tied timed matches **draw** instead of going to overtime/sudden-death. Changes the outcome of essentially every close timed match.

### T43 — Monster damage→pain/death seam + reset
- `qcsrc/common/monsters/sv_monsters.qc` — **Monster_Damage (1083, installed as this.event_damage in Monster_Spawn 1457), Monster_Dead (1036) / Monster_Dead_Damage (1018) / Monster_Dead_Think→mr_deadthink (965), monster_dropitem (40), Monster_Heal (1148), Monster_Reset (999, this.reset), monsters_setstatus→STAT(MONSTERS_TOTAL/KILLED) (34)**
  - → *port:* `src/.../Gameplay/Monsters/MonsterAI.cs` (install a monster damage handler that calls existing `MarkPain`/`MarkDead`/`DropItem`/`Respawn`; add `Reset`, `DeadThink`, global counters), `.../Damage/DamageSystem.cs` (EventDamage routes a monster to the shim, not PlayerDamage), `game/hud/ScoreboardPanel.cs`
  - *notes:* `MarkPain`/`MarkDead`/`DropItem`/`Respawn` are fully written but have **zero callers** — a monster victim is dispatched to `PlayerDamage` and treated like a player. The brain (`RunThink`) does run, so this is purely the damage→death seam.

### T44 — Physics override hooks + spectator free-flight
- `qcsrc/ecs/systems/physics.qc` — **MUTATOR_CALLHOOK(PM_Physics, this, maxspeed_mod, dt) (108, override branch), MUTATOR_CALLHOOK(IsFlying, this) (113), the !IS_PLAYER spectator branch (58-63: sys_phys_spectator_control + maxspeed_mod = STAT(SPECTATORSPEED))**
- `qcsrc/ecs/systems/sv_physics.qc` — **sys_phys_spectator_control (67-103, impulse 1-19/200-229 speed steps), autocvar_sv_spectator_speed_multiplier* (3-5)**
- `qcsrc/common/mutators/events.qh` — **MUTATOR_HOOKABLE(PM_Physics) (98), MUTATOR_HOOKABLE(IsFlying) (61)**
  - → *port:* `src/.../Physics/PlayerPhysics.cs` (add the two hook call-sites + a `!IsPlayer` fly branch), `.../Gameplay/Mutators/MutatorHooks.cs` (add `PM_Physics`/`IsFlying` chains), `game/net/ServerNet.cs` (stop `DriveObserverJoins` zeroing observer velocity)
  - *notes:* `PM_Physics` is a **distinct** hook from `PlayerPhysics` (which T3 wired) — T3's recon conflated them, so T3's "PM_Physics wired" claim is wrong. Observers are currently frozen at world origin (no fly path). Unblocks bugrigs (T51).

### T45 — Warpzone combat traversal + portal rendering
- `qcsrc/lib/warpzone/common.qc` — **WarpZone_TraceBox_ThroughZone (192) / TraceBox (323) / TraceLine (328) / TraceToss_ThroughZone (333) / TrailParticles (451); WarpZone_FindRadius (660) + _Recurse (592) + WarpZoneLib_BadEntity (562); WarpZone_RefSys_* (670-787) + Accumulator (11-37)**
- `qcsrc/lib/warpzone/client.qc` — **WarpZone_FixView (216), FixNearClip (174), FixPMove (203), View_Inside/Outside (152/160), NET_HANDLE(ENT_CLIENT_WARPZONE[_CAMERA/_TELEPORTED]) PreDraw + view/input rotate (133)**
- `qcsrc/lib/warpzone/server.qc` — **WarpZone_Projectile_Touch (344), WarpZone_StoreProjectileData (36)**
  - → *port:* `src/XonoticGodot.Engine/Collision/*` (TraceService extension iterating the per-crossing transform, cap 16 zones), `.../MapObjects/Warpzone.cs` (already has WarpzoneTransform/Manager/Teleport), `.../Weapons/WeaponFiring.cs` + `.../Damage/DamageSystem.cs` (route traces/RadiusDamage through it — both already flag the gap in comments), `game/net/NetGame.cs` (SubViewport portal render)
  - *notes:* Transform/teleport half is ported (you can walk through); the trace-recursion + FindRadius + client render are absent, so combat **through** portals is broken (rockets explode on the surface, beams stop, splash doesn't cross) and the portal renders opaque. Distinct from the transform math (already done).

### T46 — Server chat engine
- `qcsrc/server/chat.qc` — **Say() (teamsay/privatesay branches, g_chat_allowed/_team_allowed/_private_allowed/_spectator_allowed gates, trigger_magicear_processmessage_forallears, formatmessage, Team_ColorCode)**
- `qcsrc/server/command/cmd.qc` — **ClientCommand_say_team / tell / ignore / unignore / clear_ignores (+ ignore_add_player/ignore_playerinlist crypto-idfp db, per-say flood control)**
  - → *port:* new `src/XonoticGodot.Server/Chat.cs` + `Commands.cs` (register the verbs), `game/net/ServerNet.cs` (route client say lines)
  - *notes:* Port has only a raw `say` (broadcast-to-all, `teamOnly` hardcoded false). `say_team` is match-critical for team modes; the messagemode/messagemode2 client binds already exist but have no server handler.

### T47 — Harden the client command bus (security)
- `qcsrc/server/command/cmd.qc` — **SV_ParseClientCommand (UTF-8 round-trip reject, Ban_MaybeEnforceBanOnce, floodcheck via sv_clientcommand_antispam_count/_time, MUTATOR_CALLHOOK(SV_ParseClientCommand))**
- `qcsrc/server/command/reg.qh` — **separate REGISTRY(SERVER_COMMANDS) vs REGISTRY(CLIENT_COMMANDS)** — only CLIENT_COMMANDS is reachable from a client
  - → *port:* `game/net/ServerNet.cs` (`HandleClientCommand` gate), `src/XonoticGodot.Server/Commands.cs` (mark commands server-only vs client-callable)
  - *notes:* **Security exposure** — today a remote client can invoke admin verbs (`ban`/`kick`/`gotomap`/`endmatch`/`settemp`) because there is one flat table and no source gate, plus no flood/UTF-8/ban checks.

### T48 — Map-entity content tail (hazard / props / ambient / music)
- `qcsrc/common/mapobjects/misc/laser.qc` — **spawnfunc(misc_laser), misc_laser_think (traceline + Damage(dmg*frametime, DEATH_HURTTRIGGER) / dmg<0 instakill), DETECTOR (enemy.target → SUB_UseTargets), team/LASER_INVERT_TEAM, Draw_Laser** (a real hazard)
- `qcsrc/common/mapobjects/models.qc` — **spawnfunc(misc_gamemodel/misc_models/misc_clientmodel/func_static/func_wall/func_illusionary/func_clientwall/func_clientillusionary), g_model_init/g_clientmodel_init (external .model, scale, colormap, drop-by-spawnflags, fade_start/end, LOD, bgmscript), Ent_Wall_Draw** (decoration props — most stock maps have several)
- `qcsrc/common/mapobjects/func/pointparticles.qc` — **spawnfunc(func_pointparticles / func_sparks), Draw_PointParticles**
- `qcsrc/common/mapobjects/func/rainsnow.qc` — **spawnfunc(func_rain / func_snow), Draw_RainSnow → te_particlerain/snow**
- `qcsrc/common/mapobjects/target/speaker.qc` — **spawnfunc(target_speaker), looped/triggered/global ambient sound** (looping-sound infra already exists)
- `qcsrc/common/mapobjects/target/music.qc` — **spawnfunc(target_music / trigger_music), TargetMusic_Advance / Ent_TriggerMusic_Think (default<target<trigger priority mix)** (in-game map music, distinct from menu music T23)
  - → *port:* `src/.../Gameplay/MapObjects/*` (register + implement), `game/client/*` (particle/beam/weather render via existing EffectSystem), `game/assets/models/*` (decoration model attach via Md3/Iqm builder)
  - *notes:* All silently no-op today (bare edicts kept). `func_static`/`func_wall`/`func_illusionary` render+collide statically via BuildMap+RegisterSubmodels — only the dynamic toggle/fade/LOD behavior is missing for those.

### T49 — Single-player campaign wiring
- `qcsrc/menu/xonotic/campaign.qc` — **XonoticCampaignList_loadCvars/campaignGo/drawListBoxItem/setSelected, CampaignList_LoadMap→CampaignSetup**
- `qcsrc/menu/xonotic/dialog_singleplayer.qc` — **XonoticSingleplayerDialog (campaign list + Instant Action)**
- `qcsrc/common/campaign_file.qc` — **CampaignFile_Load (maps/campaign<name>.txt)**
  - → *port:* `game/menu/SingleplayerScreen.cs` (replace the static 6-level array; read campaign file + `g_campaign<name>_index` progress; render unlock/checkmark/preview), `src/XonoticGodot.Server/Campaign.cs` (the faithful server core — **currently has zero callers**), `GameWorld.cs`
  - *notes:* Campaign is non-functional end-to-end AND the already-ported `Campaign.cs` (CampaignFile_Load/PreInit/PreIntermission/SaveCvar/Setup) is dead code.

### T50 — Menu command dispatch + game menu + visual pickers
- `qcsrc/menu/command/menu_cmd.qc` — **GameCommand: directmenu / closemenu / directpanelhudmenu / nexposee / servers / profile / skinselect / languageselect / settings / inputsettings / videosettings / dumptree / sync**
- `qcsrc/menu/xonotic/dialog_gamemenu.qc` + `leavematchbutton.qc` — **Join!/Spectate toggle (send join/spec), Leave-match, Quick menu, Servers/Profile/Guide jumps**
- `qcsrc/menu/xonotic/{crosshairpicker,crosshairpreview,playermodel,charmap,colorpicker,colorpicker_string,weaponslist}.qc` — **the visual picker widgets + reorderable cl_weaponpriority list**
- `qcsrc/menu/xonotic/{datasource,listbox}.qc` — **StringSource/CvarStringSource getEntry/reload contract** (shared list backend)
  - → *port:* `game/menu/framework/*` (by-name dialog registry + MenuCommand dispatch + reusable picker/listbox widgets), `game/menu/{PauseMenu,SingleplayerScreen}.cs`, `game/menu/dialogs/*`
  - *notes:* `MenuCommand.Dispatch` falls to an inert default for every nav verb; `PauseMenu` lacks Join/Spectate (the only menu path to switch playing↔spectating); pickers are downgraded to text/number controls.

### T51 — Remaining mutators (wave 2) + Overkill weapons + doublejump fix
- `qcsrc/common/mutators/mutator/hook/sv_hook.qc` — **REGISTER_MUTATOR(hook, g_grappling_hook); PlayerSpawn sets offhand = OFFHAND_HOOK; SetStartItems fuel** (offhand grapple in any mode — a dead menu toggle today; P1)
- `qcsrc/common/mutators/mutator/damagetext/{sv,cl,ui}_damagetext.qc` — **floating damage numbers (DTFLAG_* wire, ~30 cl_damagetext_* cvars, 2D/world)** (P1)
- `qcsrc/common/mutators/mutator/itemstime/itemstime.qc` — **it_times[], IT_Write net feed, Item_ItemsTime_SetTime/Allow** (server backend for the T10 panel)
- `qcsrc/common/mutators/mutator/bugrigs/bugrigs.qc` — **RaceCarPhysics via PM_Physics override** (needs T44)
- `…/{breakablehook,campcheck,kick_teamkiller,dynamic_handicap,superspec,sandbox,doublejump}/sv_*.qc` — **the remaining registered mutators; doublejump.qc needs the surface tracebox + velocity-clip (port currently treats sv_doublejump as unconditional)**
- `qcsrc/common/mutators/mutator/overkill/{okmachinegun,oknex,okshotgun,okhmg,okrpc}.qc` — **REGISTER_WEAPON(OVERKILL_*) full wr_think/checkammo/reload/aim**
  - → *port:* new `src/.../Gameplay/Mutators/*`, `.../Weapons/*` (5 Overkill weapon classes), `game/client/*` (damagetext draw)
  - *notes:* `OverkillMutator` references the 5 OK weapons by name but they resolve to **null** (g_overkill grants nothing). `hook`+`damagetext` are the user-facing P1s; `sandbox`/`superspec`/`kick_teamkiller`/`campcheck`/`dynamic_handicap` are admin/niche P3.

### T52 — Compat entity remaps (Q3 / QL / CPMA / Q1 / WoP / Q3DF)
- `qcsrc/server/compat/quake3.qc` — **SPAWNFUNC_Q3(weapon_railgun→Vortex / rocketlauncher→Devastator / lightning→Electro / …) + SPAWNFUNC_ITEM(item_quad→Strength / item_armor_body→ArmorMega / …) + ammo .count scaling + SG↔MG arena swap; the Q3DF target_init/target_score/target_fragsFilter/target_print (119-295)**
- `qcsrc/server/compat/{quake2,quake,wop}.qc` — **item_armor_jacket/invulnerability; weapon_supernailgun→Hagar/supershotgun→MachineGun; WoP weapon_punchy→Arc + item_padpower/holdable_floater→Jetpack**
  - → *port:* `src/.../Gameplay/MapObjects/MapObjectsRegistry.cs` + new `.../MapObjects/CompatRemaps.cs`. Depends on T35 (item/weapon spawn pipeline).
  - *notes:* Only the entity-*removal* filter (`DoesQ3ARemoveThisEntity`) is ported; the *add* side is absent → Q3/DeFRaG maps load geometry but have no pickups/weapons. `halflife.qc` needs no work (intentional no-ops).

### T53 — Round/objective HUD stats for untracked modes
- `qcsrc/common/gametypes/gametype/clanarena/sv_clanarena.qc` — **CA_count_alive_players → STAT(REDALIVE/BLUEALIVE/YELLOWALIVE/PINKALIVE)**
- `qcsrc/common/gametypes/gametype/freezetag/sv_freezetag.qc` — **freezetag_count_alive_players, eliminatedPlayers.SendFlags**
- `qcsrc/common/gametypes/gametype/keyhunt/sv_keyhunt.qc` — **kh_update_state → STAT(OBJECTIVE_STATUS)**
- `qcsrc/common/gametypes/gametype/survival/sv_survival.qc` — **SurvivalStatuses_SendEntity**
  - → *port:* `.../GameTypes/{ClanArena,FreezeTag,KeyHunt,Survival}.cs` (network the stats — currently comments only), `game/hud/ModIconsPanel.cs`
  - *notes:* MODICONS (T10) feeds only the tracked modes; CA/FT/KH/Survival spectator HUD + alive/key/role display have no data source.

### T54 — Movement / prediction breadth
- `qcsrc/common/stats.qh` — **REGISTER_STAT(MOVEVARS_*) (~44 movement stats)** — port replicates only 8
- `qcsrc/common/physics/player.qc` — **Physics_UpdateStats (44-151), Physics_ClientOption (18-42, g_physics_<set>_<opt> resolution), REPLICATE cvar_cl_physics (7)**
- `qcsrc/server/command/cmd.qc` — **ClientCommand_sentcvar (GetCvars)** + `client/command/cl_cmd.qc` LocalCommand_sendcvar (cl_weaponpriority/autoswitch/noantilag)
  - → *port:* `src/XonoticGodot.Net/{InputCommand,MoveVarsBlock}.cs`, `src/.../Physics/MovementParameters.cs`, `src/XonoticGodot.Server/Commands.cs`
  - *notes:* Fine on a listen server (client==server cvars) but a remote client or any non-default-physics server mispredicts/rubber-bands. `sentcvar` is also what feeds the (ported) priority weapon-cycling its custom order.

### T55 — Server-core fidelity gaps
- `qcsrc/server/teamplay.qc` — **Player_GetForcedTeamIndex (349) + g_forced_team_red/blue/yellow/pink/otherwise (41-44); sv_teamnagger warmup nag (1036)**
- `qcsrc/server/portals.qc` — **Portal_Spawn/Touch/TeleportPlayer/SetIn/OutPortal/Think/ClearAllLater (the Porto in/out portal pair)**
- `qcsrc/server/main.qc` — **CreatureFrame_FallDamage (142: have_hook immunity + Q3SURFACEFLAG_NODAMAGE floor check), CreatureFrame_hotliquids (70: lava_burn Fire_AddDamage + FL_PROJECTILE contents damage)**
  - → *port:* `src/XonoticGodot.Server/{Teamplay,PlayerFrameLogic}.cs`, `.../Weapons/Porto.cs` (wire `PortalSpawner` → `Warpzone.SpawnPortalAsWarpzone`), `.../MapObjects/Warpzone.cs`
  - *notes:* `Porto.PortalSpawner`/`SpawnPortalAsWarpzone` exist but nothing connects them, so firing the Porto creates no usable portal; forced teams + nagger are explicitly deferred in `Teamplay.cs`.

### T56 — Generic + client + reply command parity
- `qcsrc/common/command/generic.qc` + `rpn.qc` — **GENERIC_COMMAND(rpn / maplist / addtolist / removefromlist / qc_curl / dumpcommands / restartnotifs / nextframe / runtest); the RPN VM**
- `qcsrc/server/command/cmd.qc` — **ClientCommand_voice / suggestmap / autoswitch / physics / clientversion**
- `qcsrc/server/command/{common,getreplies}.qc` — **CommonCommand_records/rankings/lsmaps/printmaplist/ladder/cvar_purechanges/editmob; getreplies precompute**
- engine `defer` — used by `vote.qc` (restart vote) — needs a sim-clock command queue
  - → *port:* `src/XonoticGodot.Server/Commands.cs`, `src/XonoticGodot.Engine/Console/ConsoleCommands.cs`
  - *notes:* `rpn` underpins stock alias/quickmenu cfg logic; `defer` absence makes a passed `restart` vote silently no-op; `editmob`/`spawnmob`/`killmob` make the already-ported Monster-Tools dialog functional.

### T57 — Weapon tail
- `qcsrc/server/weapons/throwing.qc` — **W_ThrowWeapon / W_ThrowNewWeapon / W_IsWeaponThrowable / SpawnThrownWeapon (impulse 17; superweapon-timer split; pinata/death loot)**
- `qcsrc/server/weapons/accuracy.qc` — **accuracy_add(fired/hit/real) + accuracy_isgooddamage; called from tracing.qc:65 / damage.qc:929 / player.qc:442; accuracy_send (ENT_CLIENT_ACCURACY)**
- `qcsrc/common/weapons/weapon/vortex.qc` — **chargepool (190-265), vortex_charge GetPressedKeys velocity-charge (87-111), forced-reload (198)** (representative per-weapon secondary depth)
  - → *port:* `src/.../Gameplay/Weapons/*`, `.../Damage/DamageSystem.cs`, `src/XonoticGodot.Server/Scores.cs`
  - *notes:* `WeaponImpulses` swallows impulse 17 (no-op); `WeaponAccuracy.RecordShotFired/RecordHit` exist but are called **only** on kill-credit, so the scoreboard accuracy% + XonStat acc-* read 0.

### T58 — csqcmodel client render hooks
- `qcsrc/client/csqcmodel_hooks.qc` — **CSQCPlayer_ModelAppearance_Apply (cl_forceplayermodels/forcemyplayermodel/forceplayercolors/forceuniqueplayercolors + cl_deathglow + RESPAWNGHOST), CSQCModel_LOD_Apply (_lod1/_lod2), CSQCPlayer_FallbackFrame_Apply (anim remap table), CSQCModel_Effects_Apply (EF_FLAME/SHOCK/STARDUST/BRIGHTLIGHT + MF_ROCKET/GRENADE/GIB→traileffect + jetpack loop)**
  - → *port:* `game/client/{ModelAnimator,ModelTint,ClientWorld}.cs`
  - *notes:* The base anim + per-entity colormod/glowmod/team tint are done; force-model/colors are menu checkboxes that do nothing, and LOD/deathglow/fallback-frame/EF_*/MF_* derivation are absent.

### T59 — Map-object long tail
- `qcsrc/common/mapobjects/func/stardust.qc` (func_stardust = placed model + EF_STARDUST), `misc/dynlight.qc` (scripted/moving/attached light), `trigger/viewloc.qc` (trigger_viewlocation + target_viewlocation_start/end, 2.5D camera), `misc/follow.qc` (misc_follow attach helper), `func/fourier.qc` + `func/vectormamamam.qc` (advanced movers), `target/voicescript.qc` (scripted voice sequence)
  - → *port:* `src/.../Gameplay/MapObjects/*` (most reuse the T48 decoration-model + MovingBrushes paths)
  - *notes:* Rare/cosmetic; bare no-op edicts today (no crash). `viewloc` only meaningful once a fixed/side-view camera mode exists (T4 territory).

### T60 — Admin / server-ops breadth
- `qcsrc/server/cheats.qc` — **CheatImpulse (137-287: CHIMPULSE_CLONE_MOVING/STANDING, GIVE_ALL #99, TELEPORT, R00T nuke, SPEEDRUN); the Drag entity-carry subsystem (705-1043)**
- `qcsrc/server/ipban.qc` — **OnlineBanList_SendBan/SendUnban/Query + URI_Get_Callback (g_ban_sync_uri)**
- `qcsrc/server/weapons/{weaponstats,hitplot}.qc` — **sv_weaponstats matrix upload; sv_hitplot/g_hitplots per-shot hit-coord log**
- `qcsrc/server/command/vote.qc` — **VoteCommand_checkargs (sv_vote_command_restriction_*), VoteCommand_macro_help (sv_vote_command_help_*)**
  - → *port:* `src/XonoticGodot.Server/{Cheats,Bans,Commands,VoteController}.cs`
  - *notes:* Text cheats (god/noclip/give) already work; the numeric cheat impulses are unreachable (`DispatchImpulse` routes only to `WeaponImpulses`). Online ban federation needs an HTTP client.

### T61 — Feedback / notification polish
- `qcsrc/common/notifications/all.qh` — **Local_Notification_Queue_Process (NOTIF_QUEUE_MAX=10, queuetime, prev_soundfile/prev_soundtime dedup); ReplicateVars notification_CHOICE_* per-client cvars (MSG_CHOICE)**
- `qcsrc/common/deathtypes/all.{qh,inc}` — **REGISTRY(Deathtypes) + .message category (monster/turret/vehicle) + DEATH_ISMONSTER/ISTURRET/ISVEHICLE + Deathtype_Name**
- `qcsrc/common/mutators/mutator/status_effects/*` — **StatusEffects_update (NET sync of statuseffect_time[]/flags[]), STATUSEFFECT_FLAG_PERSISTENT** (client burning/frozen overlays)
- `qcsrc/common/effects/qc/globalsound.qc` — **_GlobalSound VOICETYPE_TEAMRADIO/TAUNT/AUTOTAUNT/LASTATTACKER routing + directional attenuation; the voice/taunt commands**
  - → *port:* `game/hud/HudNotifications.cs`, `src/.../Gameplay/{Damage/DeathTypes,Notifications,Sounds}/*`
  - *notes:* Announcer voices currently stomp (no queue/dedup); voice-message/taunt subsystem is registry-only (`PlayVoiceMessage` ignores all VOICETYPE routing and has zero call sites). Mostly gated behind T40 obituary emission existing.

## TODO-CORE-ZIP — the `-core` split and manifest wiring are still only on a retired branch

`feature/launcher-updater` was retired locally on 2026-07-30 (the launcher itself now lives in
[VortexLauncher](https://github.com/VortexFPS/VortexLauncher)). `tools/make-manifest.py` was recovered
onto `main` at that point, because it is the GAME side of the boundary — it emits the `latest.json` the
launcher consumes.

What was **not** recovered, and is worth knowing before the first launcher-served release:

| file | on that branch | why not recovered now |
| --- | --- | --- |
| `.github/workflows/release.yml` | +84 lines: `-core` zip per target, re-upload only when the content key changes, emit `latest.json` | main's `release.yml` has since lost its whole `assets` job and been re-keyed on `data/maps.lock.json`. The branch's version assumes the old shape, so this is a rewrite, not a cherry-pick. |
| `tools/package.sh` | +83/-38: the fat/core split | same — main's `package.sh` was rewritten for the committed `data/` tree. |
| `docs/RELEASING.md` | the two-zip layout | follows whatever the above becomes. |

The branch is still on `origin/feature/launcher-updater` (`39e96e9`), so none of this is lost — read it
there when wiring the split up properly.

The design still holds and is worth keeping: a **fat** zip (binary + runtime + content) for a plain
download, and a **core** zip (binary + runtime) plus a shared content pack the launcher dedupes across
releases, so a patch release does not re-push ~900 MB to every player. `make-manifest.py` already models
both, and `--print-content-key` supplies the pack's identity from the `data/` tree SHA.
