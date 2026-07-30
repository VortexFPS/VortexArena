# mapobject-misc — parity spec

**Base refs:** `common/mapobjects/misc/{laser,corner,follow,dynlight,keys,teleport_dest}.qc` · `common/mapobjects/models.qc` · `common/mapobjects/subs.qc` (SUB_CalcMove) · `common/mapobjects/bgmscript.qc` (+ `client/bgmscript.qc`)
**Port refs:** `src/VortexArena.Common/Gameplay/MapObjects/{Laser,Follow,DynamicLight,MapModels,MapObjectsCommon,Teleporters,MovingBrushes,MapObjectsRegistry}.cs` · `game/client/LaserRenderer.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The `mapobjects/misc/*` family is the grab-bag of small, hand-placed BSP entities that are not movers,
triggers, or items: the hazard/detector laser (`misc_laser`), path waypoints (`path_corner`), the
attach/follow helper (`misc_follow`), the dynamic light (`dynlight`), pickup keys (`item_key*`), and the
teleport destination marker (`info_teleport_destination` / `misc_teleporter_dest`). The unit also covers the
two shared infrastructure files the misc entities (and every mover) lean on: `subs.qc`'s `SUB_CalcMove`
mover driver, and `bgmscript.qc`'s map-music-driven ADSR animation envelope (consumed by `models.qc`'s
`func_clientwall` / `func_pointparticles`). `models.qc` (the decoration-prop / static-wall family) is the
other piece in scope. These activate on essentially every map load (walls/props) down to a handful of stock
maps (lasers, dynlights, keys are rare).

## Base algorithm (authoritative)

### misc_laser — hazard / detector beam  (`misc/laser.qc:215 spawnfunc(misc_laser)`)
- **Trigger / entry:** SVQC `spawnfunc`; thinks every server frame (`misc_laser_think`, `nextthink = time`).
  Client renders the beam (`Draw_Laser`, CSQC) off a `Net_LinkEntity` stream.
- **Algorithm (think):** `misc_laser_aim` recomputes `.mangle` from `.enemy.origin` (a `target_position`)
  or from `.angles`. The endpoint `o` is `enemy.origin` (FINITE) else clipped to `LASER_BEAM_MAXLENGTH`
  along the aim. If `.dmg` or the enemy is a detector, `traceline(origin, o)`. Detector branch (enemy has a
  `.target`): on a creature crossing the beam, latch `.count` and `SUB_UseTargets(enemy, enemy.pusher, NULL)`
  on BOTH the enter and exit edge. Damage branch: team gate
  `((spawnflags & LASER_INVERT_TEAM)==0) == (team != hitent.team)` returns (so a non-inverted team laser
  damages only *same*-team touchers — counterintuitive but verbatim), then
  `Damage(hitent, …, dmg<0 ? 100000 : dmg*frametime, DEATH_HURTTRIGGER, …)`.
- **Constants:** `LASER_BEAM_MAXLENGTH = 32768`, `LASER_BEAM_MAXWORLDSIZE = 1048576`; spawnflags
  `LASER_FINITE = BIT(1)`, `LASER_NOTRACE = BIT(2)`, `LASER_INVERT_TEAM = BIT(3)`; `.dmg` per-second
  (-1 = instakill); default `beam_color = '1 0 0'` (red) when color and alpha both unset; default
  `scale = 1` (beam radius), `modelscale = 1` (dlight radius); obituaries "saw the light" /
  "was pushed into a laser by". End-effect `.cnt` resolves `mdl` to a particle-effect number, defaulting to
  `EFFECT_LASER_DEADLY` for a damaging laser.
- **Presentation (`Draw_Laser`, laser.qc:291):** `Draw_CylindricLine(origin, endpos, scale, "particles/laserbeam", …)`
  with sliding texcoord `time*3`; additive (alpha 0.5) when no alpha set, else normal blend; end-effect
  `__pointparticles(cnt, …)`; `adddynamiclight(endpos, modelscale, beam_color*5)`. CSQC magic `scale=2`,
  `modelscale=50` baseline multipliers.
- **Toggle:** `laser_setactive` / `laser_use` flip `.active` (ACTIVE_TOGGLE) when targetname-triggered;
  `generic_netlinked_reset` makes a targetnamed laser start active iff START_ON.

### path_corner / corner  (`misc/corner.qc:44 spawnfunc(path_corner)`)
- **SVQC:** the only server-side work is `set_platmovetype(this, this.platmovetype)` — parse the corner's
  `platmovetype` string into `platmovetype_start`/`platmovetype_end` (and a `_turn` flag) so a func_train
  can override its ease curve / turning per corner. `corner_link` is a no-op (CSQC send commented out).
- **CSQC (`corner_send`/`NET_HANDLE`):** networks origin + up to four targets + targetname + wait so the
  client can advance bgmscript-driven movers; harmless on a listen server.

### misc_follow — attach/follow at spawn  (`misc/follow.qc:66 spawnfunc(misc_follow)`)
- **Trigger / entry:** SVQC; deferred to `INITPRIO_FINDTARGET` (after the whole lump spawns). Removes
  itself afterward unless `.jointtype` is set.
- **Algorithm:** `src = find(targetname, killtarget)`, `dst = find(targetname, target)`. With `.jointtype`,
  keep the edict carrying `aiment=src`,`enemy=dst`. With `FOLLOW_ATTACH`: `setattachment` (or
  `attach_sameorigin`) parent dst→src, `dst.solid = SOLID_NOT`, delete self. Else MOVETYPE_FOLLOW ride:
  `follow_sameorigin(dst, src)` (sets aiment, punchangle, relative view_ofs/v_angle), delete self.
- **Constants:** `FOLLOW_ATTACH = BIT(0)`, `FOLLOW_LOCAL = BIT(1)`.

### dynlight — dynamic light  (`misc/dynlight.qc:112 spawnfunc(dynlight)`)
- **Trigger / entry:** SVQC; tag/follow/path lookups deferred to `INITPRIO_FINDTARGET`. Thinks at 0.1 s
  (follow/tag modes only).
- **Algorithm:** defaults `light_lev = 200`, `color = '1 1 1'`, `lefty = light_lev`. Four modes: static
  (no think), tag-attach (`dynlight_find_target` → `setattachment(dtagname)`), FOLLOW
  (`dynlight_find_aiment` → MOVETYPE_FOLLOW), path (`dynlight_find_path` → `set_movetype(NOCLIP)`,
  `setthink(train_next)`, default `speed = 100`). Toggle via `dynlight_use`/`dynlight_setactive`
  (light_lev↔lefty). `dynlight_reset` re-arms on round restart. Spins via `.avelocity` in all modes.
- **Constants:** `START_OFF = BIT(0)`, `NOSHADOW = BIT(1)` (= DNOSHADOW, commented out in Base),
  `FOLLOW = BIT(2)` (= DFOLLOW); `light_lev` default 200, `color` default '1 1 1', path `speed` default 100,
  think 0.1 s. RENDER: the dlight itself (`pflags`/`adddynamiclight`) — Base's pflags lines are commented out
  in the QUAKED entity; the realtime light comes from the engine reading `light_lev`/`color`/`style`.

### item_key / item_key1 / item_key2 / item_key_*  (`misc/keys.qc:166 spawnfunc(item_key)`)
- **Trigger / entry:** SVQC touch entity (`item_key_touch`). CSQC has only the `item_keys_usekey` helper
  (note "itemkeys isn't networked").
- **Algorithm:** spawns the key model (default `models/keys/key.md3`, `MF_ROTATE`, `EF_LOWPRECISION`),
  drops to floor unless FLOATING/`noalign`, bbox `'-16 -16 -56'..'16 16 0'` raised `+32` z. On touch by a
  player who lacks the key bits: OR the keys into `PS(player).itemkeys`, `play2(noise)`, centerprint the
  message, `SUB_UseTargets`. `item_keys_usekey(lock, player)` is the door/keylock consumer: clears the
  matching bits, returns whether any key matched (handles partial/all-keys).
- **Constants:** key IDs are single bits — GOLD `BIT(0)` colormod '1 .9 0', SILVER `BIT(1)` '.9 .9 .9',
  BRONZE `BIT(2)`, RED/BLUE/GREEN keycards `BIT(3..5)`, MASTER `0xffffff`. Default pickup sound
  `SND(ITEMPICKUP)`. `item_key1`→SILVER, `item_key2`→GOLD (legacy, swapped).

### info_teleport_destination / misc_teleporter_dest  (`misc/teleport_dest.qc:29`)
- **Trigger / entry:** SVQC; passive findable edict (no think). CSQC linked for prediction.
- **Algorithm:** store facing in `.mangle`, clear `.angles`, `setorigin`. Errors out (no link) without a
  targetname. `.cnt` / `.speed` ride the entity (teleport reorient + speed reprojection).

### SUB_CalcMove — mover driver  (`subs.qc:266`)
- The generic "move from origin to dest at speed, run a think on arrival" used by every mover. Short
  (<0.15 s) or explicitly-linear (`platmovetype 1 1`) moves use straight `delta*(1/traveltime)` velocity;
  longer eased moves spawn a controller sub-entity that re-samples a quadratic bezier each
  `PHYS_INPUT_FRAMETIME` with `cubic_speedfunc(platmovetype_start, platmovetype_end, t)` easing (and turns
  the parent when `platmovetype_turn`). `SUB_CalcAngleMove` is the angular analogue. `SUB_CalcMoveDone`
  snaps to `finaldest`, zeroes velocity, runs `think1`. Movers schedule against `.ltime`.

### bgmscript — map-music ADSR animation  (`client/bgmscript.qc` + `mapobjects/bgmscript.qc` include)
- A CSQC-only system: `func_clientwall` / `func_pointparticles` carrying a `.bgmscript` key animate their
  alpha / position / emission in sync with the map's `.bgs` music script. `BGMScript_InitEntity` loads
  `maps/<name>.bgs`, finds the named line; `doBGMScript` walks the time-coded note list each frame and
  returns an attack/decay/sustain/release amplitude (`GetAttackDecaySustainAmplitude` /
  `GetReleaseAmplitude`). `models.qc:278` applies it: `alpha = 1 ± lip*f`, `origin/angles += movedir*f`.
- **Constants:** ADSR fields `bgmscriptattack/decay/sustain/release` (sustain default 1); `movedir`, `lip`;
  gated by `autocvar_bgmvolume > 0` and `bgmtime >= 0`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `misc_laser` think/aim/damage/detector | `Laser.LaserSetup/LaserThink/LaserAim` | SVQC faithful; effect resolved by NAME not `.cnt` number |
| laser beam render (`Draw_Laser`) | `game/client/LaserRenderer.cs` | crossed-quad ribbon + OmniLight; approximations (no sliding texcoord, no interpolation) |
| `path_corner` | `MovingBrushes.PathCornerSetup` | indexed only; **`set_platmovetype` NOT ported** |
| `misc_follow` | `Follow.FollowSetup/FollowInit` | attach tag-transform bake is a render follow-up |
| `dynlight` server logic | `DynamicLight.*` | static/follow/tag/toggle faithful; **path-follow PARKS on first corner**; **no dlight render** |
| `item_key*` | **NOT IMPLEMENTED** (no spawnfunc registered) | `item_keys_usekey` consumer exists in keylock door (Triggers.cs) |
| `info_teleport_destination`/`misc_teleporter_dest` | `Teleporters.TeleportDestSetup` | faithful (mangle/origin/index) |
| `SUB_CalcMove`/`SUB_CalcAngleMove` | `MapObjectsCommon.CalcMove/CalcMoveBezier/CalcAngleMove/…` | structure faithful (bezier + cubic_speedfunc); **default easing diverges (port forces linear via PlatMoveStart/End=1; Base eases via 0/0)** |
| `func_clientwall` fade / antiwall | **NOT RENDERED** (fields parsed, no consumer) | distance-fade `Ent_Wall_PreDraw` + `g_clientmodel_use` solid-toggle |
| `bgmscript` ADSR | **NOT IMPLEMENTED** | explicitly out of scope in MapModels header |
| `models.qc` props/walls | `MapModels.*` | faithful spawn/solid/drop/colormap; colormap render is a follow-up |

## Parity assessment

### Faithful / live
- **misc_laser server logic + values** — `LaserSetup`/`LaserThink` mirror the QC line-for-line including the
  counterintuitive team gate and the detector enter/exit latch; live via `MapObjectsRegistry` →
  `SpawnFuncs["misc_laser"]`. Effect is resolved by name rather than QC's `.cnt` integer (intended port
  convention). Beam renders via `LaserRenderer` hosted in `ClientWorld` (live on listen-server/demo).
- **info_teleport_destination / misc_teleporter_dest** — faithful and live (registered, indexed, consumed by
  the teleporter find path).
- **SUB_CalcMove / SUB_CalcAngleMove** — the *structure* is faithful (linear/bezier branch split,
  `cubic_speedfunc`, controller sub-entity, `.ltime` scheduling) and live (every door/plat/train/button uses
  it), BUT the **default easing diverges**: QC `.platmovetype_start/_end` default to 0, so a no-key mover runs
  the bezier branch with `cubic_speedfunc(0,0,t) = -2t³+3t²` (smoothstep). The port hardcodes
  `PlatMoveStart=PlatMoveEnd=1`, forcing the LINEAR branch (`cubic_speedfunc(1,1,t)=t`) for every long
  (≥0.15 s) mover — see Gaps.
- **misc_follow** — server attach/follow faithful and live (registered + drained in `RunPostSpawn`).
- **MapModels (props + walls)** — faithful spawn/solid/drop-to-floor/colormap logic; live (every map).

### Gaps (observable)
- **SUB_CalcMove default easing is linear, not smoothstep.** QC fields `platmovetype_start`/`_end` default to
  0 (uninitialized), so any mover without a `platmovetype` key fails the `(start==1 && end==1)` linear test and
  runs the bezier branch eased by `cubic_speedfunc(0,0,t) = -2t³+3t²` (an ease-in-out S-curve). The port
  hardcodes `PlatMoveStart=PlatMoveEnd=1` (MapObjectsCommon.cs:109-110), so that test is always true and **every
  long (≥0.15 s) door / plat / train / button move runs at constant linear velocity** instead of the Base
  S-curve. Short (<0.15 s) moves match. The path geometry is identical (straight-line midpoint control either
  way); only the speed profile differs. Compounded by `set_platmovetype` being unported (below), so no path ever
  overrides the 1/1 default. This is the most-live divergence in the unit.
- **func_clientwall distance-fade not rendered.** `fade_start`/`fade_end`/`alpha_max`/`alpha_min`/
  `fade_vertical_offset` are parsed onto the entity (MapObjectFieldsExtra.cs) but no renderer applies
  `Ent_Wall_PreDraw`'s distance-fade alpha formula or its PVS alpha-cull — a clientwall meant to fade with
  distance stays opaque. The antiwall relay (`g_clientmodel_use`: trigger-driven solid/visible toggle) is also
  unported. (No stock map is known to depend on either.)
- **item_key* entirely missing.** No `item_key`/`item_key1`/`item_key2`/`item_key_gold/silver/master`
  spawnfunc is registered (`MapObjectsRegistry.RegisterAll` has no keys block). A map that places a key
  spawns nothing: no model, no pickup, no `itemkeys` granted — so a `trigger_keylock` / locked
  `func_door` gated on that key can never be opened. The keylock *consumer* (`item_keys_usekey`) exists, but
  the player can never acquire keys. Player sees no key prop and is permanently locked out.
- **dynlight renders no light.** `DynamicLight` runs the full server state machine (toggle, follow, tag,
  spin) but there is no DP-style realtime dynamic-light consumer, so a `dynlight` is invisible in-game (note
  Base's own `pflags` lines are commented out, but the engine still drives a realtime light from
  `light_lev`/`color` — the port has neither). Player sees no moving/pulsing light where the map authored one.
- **dynlight path-follow parks.** A path-following `dynlight` snaps onto its first `path_corner` and stops
  (TrainNext is private to MovingBrushes); it never travels the corner chain. Combined with the no-render gap
  this is currently moot but is a logic divergence.
- **path_corner `set_platmovetype` not ported.** `PathCornerSetup` only indexes the corner; it does not parse
  the corner's `platmovetype` string into per-corner `platmovetype_start/end`/turn overrides. A func_train
  whose corners specify a custom ease curve / per-corner turn string moves with the train's default linear
  easing instead. (TRAIN_TURN as a *spawnflag* IS honored; the per-corner platmovetype-string override is not.)
- **bgmscript ADSR not ported.** `func_clientwall` / `func_pointparticles` carrying a `.bgmscript` key do not
  animate alpha/position/emission to the map music. On the few maps that use it the prop is static instead of
  pulsing to the soundtrack. (Explicitly scoped out in the MapModels header.)
- **misc_laser presentation approximations.** Beam texcoord does not slide (`time*3`), and the client trace
  runs at the server tick origin (no `InterpolateOrigin`) — minor, lasers are static on stock maps.
- **misc_follow attach tag-transform** — the tag-relative origin/angle bake from `attach_sameorigin` is a
  render-attachment detail deferred; the parent link itself is issued.

### Intended divergences
- **Laser effect by NAME, not `.cnt` number** — the port resolves the end effect through the effect registry
  by name (`Entity.LaserEndEffect`) rather than QC's particle-effect integer. A raw numeric `cnt` map key is
  not honored (no shipped map uses one). Documented in `Laser.cs` header.
- **Laser detector latch uses a dedicated bool** (`Entity.LaserHitLatch`) instead of reusing `.count`, so a
  mapper-set `count` no longer pre-latches the detector (obscure). Documented.
- **No `Net_LinkEntity` for laser/clientwall** — the listen-server/demo client reads the shared entity off the
  ambient facade (the seam every client fx uses), so the dedicated ENT_CLIENT_* streams are unnecessary. NOTE:
  dropping the stream is fine for the *server* contract, but the func_clientwall distance-fade and antiwall
  toggle that the stream carried are genuinely *unimplemented* on the client (a real gap, not a divergence) —
  see Gaps.

## Verification
- **Code-read, both sides** — all six misc files + subs.qc + bgmscript.qc read in full against their port
  counterparts; `MapObjectsRegistry.RegisterAll` read to confirm which classnames are wired (keys absent;
  laser/dynlight/follow/teleport_dest/models present).
- **Liveness traced** — laser/dynlight/follow/teleport_dest spawnfuncs are registered and (for the deferred
  ones) drained in `RunPostSpawn`; `LaserRenderer` is instantiated in `ClientWorld` init.
- **item_key absence** confirmed by grep: no `item_key` registration; the only `itemkeys` references are the
  keylock consumer in Triggers.cs/Doors.cs and `EntityMapObjectStateExtra.cs`.
- **Not run in-game** — no runtime observation of laser damage, dynlight render, or key pickup this pass;
  the render/no-render and path-park claims are from code, not a live capture.

## Open questions
- Does any *stock* Xonotic map actually place an `item_key*` / locked door pair? If not, the keys gap is
  theoretical for shipped content (but still a correctness hole for custom maps).
- Will the dynlight render gap be closed by the same T4 realtime-light system referenced in `DynamicLight.cs`,
  or is a separate decision needed (Godot OmniLight per dynlight, like `LaserRenderer` already does for the
  beam endpoint)?
- Confirm whether any stock map's func_train relies on per-corner `platmovetype` strings (vs the TRAIN_TURN
  spawnflag) — that determines the severity of the `set_platmovetype` gap.
- How noticeable in-game is the SUB_CalcMove default-easing flip (linear vs smoothstep)? Mathematically real
  for every ≥0.15 s mover, but unobserved this pass — needs a side-by-side capture of a slow door/plat to judge
  cosmetic vs gameplay impact. (Fix is a one-liner: default `PlatMoveStart/End = 0` and let set_platmovetype set
  them — but that also requires porting set_platmovetype so explicit `platmovetype 1 1` keys still go linear.)
