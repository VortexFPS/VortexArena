# Wave-14 design — CSQCMODEL foundation render contract

Status: PLAN (judged + synthesized 2026-06-27). Base ref `v0.8.6-1779-g863cd3e84`.
Registry units in scope: `cl-csqcmodel`, `net-entity-state`, `sv-world-rules`, `sv-client-lifecycle`.

---

## Chosen approach

**Design C (PRAGMATIC HYBRID) as the base, grafted with the best of A and B.**

All three designs converge on the same correct *philosophy*: extend the existing unified
`NetEntityState` delta channel + the live `LocomotionBlend`/`PlayerModel`/`PlayerSkeleton` skeletal
path — do NOT build a parallel csqcmodel/wepent/animdecide networking stack. That convergence is the
real signal: it is the only philosophy consistent with the port's architecture, and the registry's
own `net-entity-state.csqcmodel.change_mask` row documents the unified-codec divergence as *intended
and faithful*. So the disagreement is not about philosophy but about *packaging and sequencing*.

Design C wins as the base for three reasons, each verified against the code this session:

1. **It is the only design that found the genuine shipping bug.** `ClientWorld.GetAttachmentMarker`
   (ClientWorld.cs:562-572) returns a real tag `Marker3D` ONLY for the MD3 `_animators` path; for a
   skeletal player (the primary player path) it falls through to `_entityNodes[ownerIndex]` — the
   **entity root node**, not the hand bone. So a remote skeletal player's held weapon currently
   attaches to the body origin. The registry `cl-csqcmodel.tagindex.weapon_attach` row is already
   `result: fail`. Designs A and B describe the tag cascade abstractly; only C identifies that the
   highest-value-per-effort win is *fixing an attach-to-torso bug*, not adding a cascade.

2. **Its phasing is the most honest about the bimodal effort.** It cleanly separates the small,
   low-risk network plumbing + self-contained adds (Phases 1-3, 5) from the one genuinely hard core
   (Phase 4, the upper-body action overlay), and recommends landing everything else first so Wave-15
   is unblocked by the *ground layer* without waiting on the full action fan-out. Design A reaches the
   same "ground layer first" conclusion; C operationalizes it better.

3. **Its risk register is sharpest on the one subtle render trap:** the `PlayerSkeleton.FromFrames`
   4-pose split has **never been exercised with `torsoTime > 0`**. Verified at LocomotionBlend.cs:57-71:
   `Split` takes `torsoTime` but pins `Lerp3 = 0.5` (static torso == frame3) and `Lerp4 = 0` with
   `Frame4 = tb` computed-but-"reserved, not yet weighted." The 4-pose path is *half-wired*. Feeding a
   live action clip phase is where the bugs will be (fixbone re-anchor + Lerp3/Lerp4 juggle assume a
   static torso pose), and C is the design that flags this most precisely.

**Grafts taken from A and B:**

- **From B/A — the server-side animdecide DECISION port as a dedicated, Godot-free, unit-testable
  unit** (`AnimDecide.cs`). C proposes a thin `SetAction` latch; A and B correctly insist the
  *priority cascade* (DEAD > ACTIVE > IDLE) and the per-anim running-window expiry
  (`time <= start + numframes/rate`) be a faithful port with unit tests against hand-traced QC cases.
  Take B's framing: `AnimDecide` is pure decision logic (action set, priorities, per-anim framerates),
  the Godot poser does the blend. This is the already-documented intended divergence in
  `cl-csqcmodel.skeleton.upper_lower_aim`.

- **From A — fold RandomSeed and ClientInit_misc onto channels that already reach every client**
  (`SendMatchState` for the seed; the accept/welcome handshake for the constants), rather than B's new
  `NetControl.WorldRules`/`NetControl.ClientInit` reliable blocks. The match-state packet already
  broadcasts per-tick (verified used by `OverTimeManager`/`MatchOvertimes`), so the seed rides for
  free; the welcome path (`InfraClientConnect`, GameWorld.cs:1192) already delivers campaign/mutator/
  MOTD lines, so ClientInit constants extend it. Smallest diff, fewest new wire primitives.

- **From B — the `Prandom` correctness fix is a real prerequisite, NOT optional.** B is the only design
  that noticed the existing `Prandom` is (a) a process-global static that corrupts on a listen server
  (client + server share the process) and (b) the wrong algorithm vs Base's CRC16 stream. *However*,
  scope this down: do NOT block the RandomSeed wire on a bit-exact CRC16 port. Ship the seed + a
  per-context (server-instance, client-instance) deterministic RNG first; treat exact Base-stream
  parity as a follow-up, since the registry confirms **no client effect currently consumes a server
  seed at all** — networking it is additive and has no current consumer to desync. (Both A and C
  flag "no current consumer" as a reason to ship-the-seam-now; merge that with B's correctness note.)

- **From A — the fallback-frame remap (`cl-csqcmodel.fallbackframe.remap`) is satisfied-by-construction,
  not a task.** A correctly argues the skeletal path never produces the high-numbered melee/duckwalk
  frame ids the remap targets; the correct equivalent is to bake the same fallback discipline
  (melee→shoot, duckwalk-variants→duckwalk) into the action-clip `Pick()` chain in `BuildClipTable`.
  The registry row already notes the remap "rarely fires." Do not spend wave effort on a runtime
  frame-id remap.

**Rejected:** B's parallel `NetControl` reliable blocks (more wire surface than A's fold-into-existing
approach for the same observable). C's "send `AnimActionTime` as a raw float" *as the only option* —
adopt A's note that this is the same intended-divergence class as `death_time` (client-observed time,
already accepted in the `deathfade` row), so a raw float is acceptable, but document it.

---

## Quick wins vs large items

### QUICK WINS — small, self-contained, low-risk (land these first)

| # | Item | Files | Closes (registry) |
|---|------|-------|-------------------|
| QW1 | **Weapon tag-attach bone fix** — expose the resolved `BoneWeapon` as a tracked `Node3D`/`BoneAttachment3D` on the skeletal `PlayerModel`; return it from `GetAttachmentMarker`. Fixes the live attach-to-torso bug. | `PlayerModel.cs`, `ClientWorld.cs` | `cl-csqcmodel.tagindex.weapon_attach` (fail→partial/faithful) |
| QW2 | **wepent field group on the wire** — `EntityField.Wepent = 1<<19`; `NetEntityState` gains `SwitchWeapon`, `SwitchingWeapon`, `WepPhase`(2-bit ready/raise/drop), `WepAlpha`(reuse `QuantizeAlpha`), `ViewmodelSkin`, `GunAlign`. (Porto held-angle + tuba reserved, deferred.) | `NetEntity.cs`, `ServerNet.cs`, `ClientEntityView.cs`, `Entity.cs` | `net-entity-state.wepent.switch_alpha_misc` (missing→faithful, minus porto/tuba) |
| QW3 | **RandomSeed scalar** — one int on `SendMatchState`, server re-rolls every 5s (`RandomSeed_Think`); client exposes `ClientNet.RandomSeed` + a per-instance deterministic RNG helper. | `ServerNet.cs`, `ClientNet.cs`, new `RandomSeed.cs`, `GameWorld.cs` | `sv-world-rules.entity.randomseed` (missing→faithful on the wire) |
| QW4 | **ClientInit_misc constants** — bundle `g_trueaim_minrange`, hook/arc shot origins, `g_balance_damagepush_speedfactor`, armor blockpercent, fog, serverflags into the existing welcome/accept handshake (extend `InfraClientConnect`); client stores them where physics/render read. | `ClientManager.cs`/`GameWorld.cs`, `ClientNet.cs` | `sv-client-lifecycle.connect.fix_client_cvars_welcome` (partial→closer); `cl-csqcmodel` trueaim residual |
| QW5 | **Remote weapon switch anim (cosmetic)** — `ViewEntityRenderer` keys the rendered model on `SwitchingWeapon` during a raise/drop phase + a simple Y-drop tween off `WepPhase`. Applies `WepAlpha`/`ViewmodelSkin` to the built model. | `ViewEntityRenderer.cs`, `ClientEntityView.cs` | `net-entity-state.wepent.switch_alpha_misc` (raise/lower half) |

QW1 is the single highest-value-per-effort item (fixes a shipping visual bug). QW2-QW4 mirror the
Wave-5 alpha/jetpack adds almost line-for-line and are dependency-free. **Note:** `Prandom` per-context
+ CRC16 correctness (from Design B) is a quick-win-adjacent prerequisite *only if* a client effect will
actually consume the seed this wave; since none does yet, ship the per-instance split (cheap) and defer
the bit-exact CRC16 stream.

### LARGE ITEMS — the genuine anim state machine (staged, careful)

| # | Item | Files | Closes (registry) |
|---|------|-------|-------------------|
| LI1 | **Server-side animdecide DECISION + set-sites** — new Godot-free `AnimDecide.cs`: the ANIM_* framegroup table + per-anim rates, `getupperanim` priority cascade (DEAD>ACTIVE>IDLE) + running-window expiry, `SetAction` latch. Set `UpperAction` at the faithful sites: fire (SHOOT/MELEE), pain (PAIN1/PAIN2), draw (weapon raise), taunt, death. Server `Player`/`ServerPlayerState` gains `AnimUpperAction`/`AnimActionStart` (+ `GunAlign`). | new `AnimDecide.cs`, `Player.cs`/`ServerPlayerState.cs`, fire/pain/draw/taunt/death sites, `PlayerFrameLogic.cs` | `net-entity-state.csqcmodel.anim_state` (server producer) |
| LI2 | **anim-action wire** — `EntityField.AnimAction = 1<<18`; `NetEntityState.UpperAction`(byte) + `AnimActionTime`(float, the `death_time`-class divergence). Optional `LowerAction` deferred (legs are client-inferred and already faithful). | `NetEntity.cs`, `ServerNet.cs`, `ClientEntityView.cs`, `Entity.cs` | `net-entity-state.csqcmodel.anim_state` (wire) |
| LI3 | **Client torso-action overlay** — `LocomotionBlend.SelectTorsoAction(action, start, now)` returns the torso clip + phase; rework `Split` to take a real `torsoTime`/clip + non-zero `Lerp3`/`Lerp4` so frame3/frame4 animate the action (today both are pinned). `PlayerModel.BuildActionClipTable` (draw/pain/shoot/melee/taunt/die) with per-anim Fps. Bake the melee→shoot / duckwalk fallback into the Pick() chain (subsumes the fallbackframe remap). | `LocomotionBlend.cs`, `PlayerModel.cs` | `cl-csqcmodel.animdecide.state_machine` (actions); `cl-csqcmodel.skeleton.upper_lower_aim` (static-torso gap); `cl-csqcmodel.fallbackframe.remap` (by construction) |

**Explicitly out of scope for Wave-14** (defer, document as residual): LOD `_lodN` swap
(`cl-csqcmodel.lod.distance_swap` — separate render task, real but orthogonal regression); colormod /
scale / non-jetpack MF trails / v_angle networking (the rest of the csqcmodel contract); porto held-angle
+ tuba instrument (low-impact); the bit-exact `Prandom` CRC16 stream; ping column + playerstats latency.

---

## File-by-file implementation plan

### `src/VortexArena.Net/NetEntity.cs`
- `EntityField`: add `AnimAction = 1 << 18`, `Wepent = 1 << 19` (uint mask, verified free).
- `NetEntityState`: add `byte UpperAction`, `float AnimActionTime`; wepent block `int SwitchWeapon`
  (-1=none), `int SwitchingWeapon` (-1=none), `byte WepPhase` (0 ready/1 raise/2 drop),
  `byte ViewmodelSkin`, `byte WepAlpha` (same 0=opaque sentinel as body Alpha), `byte GunAlign`.
- `Diff`: set `AnimAction` when `UpperAction`/`AnimActionTime` differ; `Wepent` when any wepent field
  differs.
- `WriteDelta`/`ReadDelta`: add the two branches (AnimAction = WriteByte + WriteFloat; Wepent =
  WriteShort×2 switch/switching + WriteByte×4 phase/skin/alpha/gunalign). Reserve a comment for the
  deferred porto/tuba.
- `NetEntityFlags` (ushort, free at `1<<9`): reuse `Crouched`/`Dead` for anim DUCK/DEAD where possible;
  add `AnimDead2 = 1<<9` ONLY if die1-vs-die2 must visually differ (decide up front per Design A's risk).

### `src/VortexArena.Engine/Simulation/LocomotionBlend.cs`
- Keep `ImplicitDirection`/`SelectLegsDirectional` unchanged (faithful, live — verified 1:1 port).
- Add `SelectTorsoAction(byte action, float actionStart, float now)` → `(FrameGroup clip, float phase)`
  implementing the priority cascade + per-anim duration window (die 2s, draw 1/3s, pain 0.5s, shoot
  1/5s, taunt ~3.03s, melee). Idle returns the stable aim pose.
- Rework `Split` to accept a real torso clip + `torsoTime` and a non-zero upper split weight when an
  action is active (animate frame3/frame4 + Lerp3/Lerp4), falling back to today's `Lerp3=0.5, Lerp4=0,
  torsoTime=0` static aim pose when no action is active. **This is the half-wired 4-pose path; test the
  fixbone re-anchor interaction.**

### `src/VortexArena.Common/Gameplay/Player/AnimDecide.cs` (NEW)
- Godot-free port of the animdecide DECISION half: ANIM_* framegroup table + per-anim rates;
  `GetUpperAnim` priority (DEAD>ACTIVE>IDLE) + running-window expiry; `SetAction(Player, action)` latch
  (sets `AnimUpperAction` + `AnimActionStart = now`). Unit-testable against hand-traced QC cases.

### `game/client/PlayerModel.cs`
- `BuildActionClipTable` (analogous to `BuildClipTable`) resolving draw/pain1/pain2/shoot/melee/taunt/die
  clips with per-anim Fps, with the melee→shoot / duckwalk-variant fallback baked into the Pick() chain.
- `Pose()`: drive the torso from `SelectTorsoAction(e.UpperAction, e.AnimActionTime, now)` instead of
  `Split(..., torsoTime:0)` when an action is active; honor DUCK/DEAD via the already-decoded
  `e.IsDucked`/dead.
- **QW1:** after the skeleton is wired, create a child `Node3D` "tag_weapon" (or `BoneAttachment3D`) and
  in `PushBones()` set its global transform from the resolved `BoneWeapon` (already in `worldGodot[]`,
  conjugated Quake→Godot exactly like the body bones). Add a `TagWeaponMarker` accessor.

### `game/client/ClientWorld.cs`
- `GetAttachmentMarker`: BEFORE the `_animators` (MD3) branch, check the skeletal `PlayerModel` map and
  return its `TagWeaponMarker` when present — so a remote skeletal player's held weapon attaches to the
  HAND bone, not the body root (the QW1 bug fix). Keep the animator/entity-root fallback. Null-guard to
  the old behavior when the bone is unresolved.

### `game/client/ViewEntityRenderer.cs`
- **QW5:** key `RebuildFromWeapon` on `SwitchingWeapon` during a raise/drop phase; apply a Y-offset
  raise/lower tween off `WepPhase` + `AnimActionTime`. Apply `WepAlpha`/`ViewmodelSkin` to the built
  model. Keep the existing reparent-to-marker (now resolves to the real hand bone via QW1).

### `game/net/ServerNet.cs`
- `BuildEntitySet` (Player branch): set `UpperAction`/`AnimActionTime` from the server `AnimDecide`
  state; set the wepent block from `WeaponSlotState` (`SwitchWeaponId`/`SwitchingWeaponId`, `WepPhase`
  from `State` WS_RAISE/WS_DROP, `WepAlpha`=`QuantizeAlpha`(exterior weapon alpha), `ViewmodelSkin`,
  `GunAlign`).
- **QW3:** add a `RandomSeed` int to `SendMatchState` from a new `RandomSeed.Current` (server re-rolls
  every 5s).

### `game/net/ClientEntityView.cs` / `game/net/ClientNet.cs`
- `ClientEntityView`: decode `UpperAction`/`AnimActionTime` + wepent block onto the proxy `Entity`.
- `ClientNet`: consume `RandomSeed` (QW3) into a per-instance deterministic RNG; consume the
  ClientInit_misc constants (QW4) into client-side constant stores.

### `src/VortexArena.Common/Framework/Entity.cs`
- Add render-only mirror fields: `byte UpperAction`, `float AnimActionTime`, and the wepent display
  fields (`SwitchWeapon`, `SwitchingWeapon`, `WepPhase`, `WepAlpha` default 1, `ViewmodelSkin`,
  `GunAlign`). Server `Player` side adds `AnimUpperAction`/`AnimActionStart`/`GunAlign`.

### `src/VortexArena.Server/RandomSeed.cs` (NEW) + `GameWorld.cs`
- `RandomSeed`: `Current` int re-rolled every 5s (matching `world.qc` reroll_period), ticked from
  `OnStartFrame`; instantiated in `Boot`.

### `src/VortexArena.Server/ClientManager.cs` / `GameWorld.cs` (QW4)
- In the connect/welcome path, send the ClientInit_misc constants bundle once per client (extend the
  existing `InfraClientConnect` welcome channel — it already delivers campaign/mutator/MOTD lines).

### `src/VortexArena.Common/Math/Prandom.cs` (scoped from Design B)
- Make the RNG per-context (server-instance + client-instance) so a listen server's two RNGs don't
  corrupt each other's stream. Defer the bit-exact CRC16-vs-PCG algorithm swap unless a client effect
  consumes the seed this wave (none does today).

### Registry updates (after implementation, via `/parity-diff`)
- `cl-csqcmodel.tagindex.weapon_attach` fail→partial/faithful (QW1).
- `net-entity-state.wepent.switch_alpha_misc` missing→faithful (minus porto/tuba) (QW2/QW5).
- `sv-world-rules.entity.randomseed` missing→faithful on the wire (QW3).
- `sv-client-lifecycle.connect.fix_client_cvars_welcome` partial→closer (QW4).
- `net-entity-state.csqcmodel.anim_state` + `cl-csqcmodel.animdecide.state_machine` +
  `cl-csqcmodel.skeleton.upper_lower_aim` toward faithful (LI1-LI3).
- `cl-csqcmodel.fallbackframe.remap` satisfied-by-construction.

---

## Staged sequence

1. **Stage 0 — ground layer (QW2 wire only + Entity mirrors).** Add the `EntityField`/`NetEntityState`
   fields + codec + Entity mirrors. Dependency-free; mirrors Wave-5 alpha/jetpack. **Unblocks Wave-15.**
2. **Stage 1 — quick wins.** QW1 (tag-attach bone fix — the shipping bug), QW3 (RandomSeed + per-instance
   RNG + Prandom per-context), QW4 (ClientInit_misc). All low-risk, parallelizable.
3. **Stage 2 — wepent cosmetics.** QW2 populate (ServerNet) + QW5 (remote switch anim). Needs the
   server switch-state-machine seam exposed.
4. **Stage 3 — anim-action SHOOT-only proof.** LI1 (SetAction at the fire site only) + LI2 (wire) + LI3
   (`SelectTorsoAction` + `Split` rework). Proves the loop end-to-end AND smokes the half-wired 4-pose
   `FromFrames` path with the riskiest single action.
5. **Stage 4 — fan out remaining actions.** PAIN/DRAW/TAUNT/MELEE/DEATH set-sites once SHOOT is verified.

Land Stages 0-2 first (all buildable, mostly low-risk, close the wepent/tagindex/randomseed/clientinit
rows); tackle Stages 3-4 as their own verified sub-wave.

---

## Risks

1. **(LARGE) The 4-pose `FromFrames` split has never run with `torsoTime > 0`.** `Split` pins
   `Lerp3=0.5/Lerp4=0` today (verified LocomotionBlend.cs:57-71). Feeding a live action clip phase can
   expose bugs in the fixbone re-anchor + Lerp3/Lerp4 juggle (PlayerSkeleton.cs:124-154 assumes a static
   upper pose). The torso could tear from the legs. *Mitigation:* SHOOT-only proof (Stage 3) +
   `--skeleton-smoke` headless pose check before the visual pass.
2. **(MEDIUM) QW1 changes attachment for ALL skeletal remote players.** If `BoneWeapon` resolves to a
   bone with a different rotation convention than the MD3 `tag_weapon`, weapons attach mis-rotated.
   *Mitigation:* reuse the body's Quake→Godot conjugation (already in `PushBones`); keep the
   hide/null-guard fallback when the bone is unresolved.
3. **(MEDIUM) `Prandom` is a process-global static + wrong algorithm.** On a listen server the shared
   static means server and client RNG draws corrupt each other. *Mitigation:* per-context split is the
   prerequisite; the CRC16 bit-exact stream is deferred (no consumer yet).
4. **(MEDIUM) Server switch-state-machine seam.** Surfacing `m_switchingweapon` (the in-transition
   lowering id) without desyncing the local viewmodel is fiddly (the port has the post-lower id but the
   in-transition id needs exposing). *Mitigation:* keep QW5 cosmetic; defer if the seam fights back.
5. **(LOW) `AnimActionTime` as a raw float diverges from Base's `WriteApproxPastTime` quantization.**
   Same intended-divergence class as `death_time` (already accepted in `cl-csqcmodel.glowmod.deathfade`).
   Acceptable; document it.
6. **(LOW) Lockstep `WriteOwnerState` appends** (if any wepent owner fields ride the owner block) MUST be
   appended at the END and read in the same order or every later field desyncs — a known footgun the file
   documents.
7. **(LOW) RandomSeed has no current client consumer** — the wire is additive/dead until an effect uses
   it. Ship the seam; treat the first deterministic-effect consumer as out of scope.

---

## Test / verification strategy

**Unit (Godot-free, the established pattern — `LocomotionBlend`/`CsqcModelAppearance` already have tests):**
- `AnimDecide.GetUpperAnim`: priority cascade (DEAD>ACTIVE>IDLE) + per-anim duration boundaries (shoot
  active 0.2s, taunt ~3.03s, die 2s) against hand-traced QC cases.
- `LocomotionBlend.SelectTorsoAction`: clip + phase, non-looping clamp at last frame, fps-correct phase.
- `EntityStateCodec` round-trip for the new `AnimAction` + `Wepent` fields: `Diff` sets the right bits;
  `WriteDelta`→`ReadDelta` preserves values; an unchanged field leaves its mask bit clear (opaque/hidden
  alpha sentinels round-trip). The registry notes "no dedicated codec unit test located" — **add one.**
- `RandomSeed.Think` re-rolls at the 5s period; two per-instance RNGs from the same seed match.
- `AnimDecide.SetAction` latches action + start and clears when the duration elapses.

**In-game (the irreducible part — add rows to `NEEDS-INGAME-CHECK.md`):**
- A remote skeletal player's held weapon sits on the HAND bone, not the torso origin (QW1 — the bug fix).
- A remote player visibly lowers/raises on weapon switch (QW5).
- A remote player plays shoot/pain/taunt torso overlays WHILE the legs keep strafing (LI3) — and the
  torso does NOT tear from the legs (the 4-pose risk).
- A Cloaked player + its gun fade together per the exterior-weapon alpha rule (QW2 `WepAlpha`).
- No regression to the already-live 8-dir locomotion + alpha + jetpack.

**Harness:** `--skeleton-smoke` (cull off, pushes every frame) as the headless pose-path smoke test for
the new action overlay — catch torso-split regressions before the visual check. Build (server + game) +
full test suite green at each stage. Finish with `/parity-diff` on the four units to re-audit + update
the rows.
