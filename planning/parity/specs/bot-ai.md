# Bot AI (havocbot) — parity spec

**Base refs:** `server/bot/default/{bot,aim,navigation,scripting,waypoints}.qc` · `server/bot/default/havocbot/{havocbot,roles}.qc` · `server/bot/default/cvars.qh` · `server/bot/api.{qc,qh}`
**Port refs:** `src/VortexArena.Server/Bot/{BotBrain,BotAim,BotNavigation,BotDanger,BotRoles,BotObjectiveRoles,BotController,BotPopulation,BotTracewalk,Waypoint}.cs` · `src/VortexArena.Server/GameWorld.cs` (live seam) · `src/VortexArena.Server/ClientManager.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The bot AI is Xonotic's "havocbot" brain: a server-authority think loop that runs once per bot per server
frame. Each tick a bot (1) picks an enemy, (2) on a slower "strategy" clock runs a per-gametype *role* to
rate candidate goals (items/enemies/objectives/roam waypoints) and routes to the best along the waypoint
graph, (3) aims — at the enemy with projectile lead + skill-scaled error/turn-rate, else along the move
direction, (4) navigates toward the current goal producing a wish-move + jump/crouch, and (5) fires when the
aim is on target and the line of fire is clear. The `skill` cvar (0..10, `>100` = SUPERBOT) is the master
knob scaling aim error, reaction interval, turn rate and aggression. The population is managed by
`bot_serverframe`/`bot_fixcount` (fill to `bot_number`/`minplayers`/`bot_vs_human`, one add per frame),
which also rotates a *strategy token* so exactly one bot rates goals per frame, and loads the map waypoint
graph once. A separate scripting layer (`bot_cmd`/`scripting.qc`) lets maps/console drive bots verbatim
(pause/move/aim/press-key) — used for cutscenes, benchmarks and waypoint editing.

All of this is **server-side authority**. There is no client/presentation component (bots are ordinary
players to the renderer; their model/animation/sound is the normal player-presentation path).

## Base algorithm (authoritative)

### Population management (`bot.qc:bot_serverframe`, `bot_fixcount`, `bot_spawn`, `bot_removenewest`)
- **Trigger:** `bot_serverframe` once per server frame (sv).
- **fixcount target:** `bot_vs_human` (≠0, AVAILABLE_TEAMS==2) → an all-bot team sized `ceil(|ratio|×activeHumans)`;
  else if humans present OR `bot_join_empty` OR (currentbots>0 && time<5) → `max(bot_number, minplayers − activeHumans)`
  (teamplay uses `minplayers_per_team × AVAILABLE_TEAMS`), capped by `g_maxplayers` and `maxclients`; else 0.
  **One add per frame** (`multiple_per_frame=false`); excess trimmed via `bot_removenewest` (in teamplay: newest on
  the largest team). On spawn failure: 10s backoff.
- **Early sentinel:** `currentbots = -1` while `time < 2.5`, then recount existing bots (survive map change).
- **Intermission:** bots stay after match end unless all humans left → drop all.
- **skill resync:** when `autocvar_skill != skill`, set `skill = autocvar_skill` and re-cost waypoint links if the
  bunnyhop-skill threshold was crossed.
- **strategy token:** `bot_strategytoken` cycles to the next bot **without a goal** (skipping dead/frozen) each frame;
  only the holder runs its role (one goal search per frame across the population — prevents framerate spikes).
- **danger objects:** every `bot_ai_dangerdetectioninterval` (0.25s) `botframe_updatedangerousobjects(64)` flags
  `bot_dodge` entities (rockets/etc.) and accumulates per-waypoint danger costs.
- **Constants:** `bot_number 0`, `minplayers 0`, `minplayers_per_team 0`, `bot_vs_human 0`, `bot_join_empty 0`,
  `bot_prefix "[BOT]"`, `bot_suffix ""`, `skill 8` (server cfg default), `skill_auto 0`, `bot_god 0`,
  `bot_nofire 0`, `bot_ignore_bots 0`, `bot_typefrag 0`, `bot_usemodelnames 0`, `bot_config_file bots.txt`.

### Per-bot think throttle (`bot.qc:bot_think`)
- SUPERBOT thinks every 0.005s; else `bot_nextthink += max(0.01, bot_ai_thinkinterval(0.05) × min(14/(skill+14),1))`.
- Re-stamps FL_GODMODE from `bot_god`; sets a skill-based `ping` (`bound(0, 0.07 − 0.005·skill + rand·0.01, 0.65)`).
- Clears buttons each think; JUMP stays held `time < bot_jump_time + 0.2` (ramp jumps).
- Pre-game holds: campaign (`!campaign_bots_may_start`) and countdown (`time < game_starttime`) emit zero movement.
- Dead/observer: zero movement; dead presses jump to respawn (releases one frame at DEAD_DYING for the keydown edge).
- Warmup: auto-readies once.

### Aiming (`aim.qc:bot_aimdir`, `bot_aim`, `bot_shotlead`, `findtrajectorywithleading`)
- **bot_shotlead:** lead target by `shotdelay + dist/shotspeed`.
- **findtrajectorywithleading:** for gravity weapons, 10 traces raising the launch pitch 0.1 each iter until a
  `tracetoss` lands on the (faked) target — gives a true ballistic launch velocity.
- **bot_aimdir:** SUPERBOT snaps `v_angle = vectoangles(dir)` and arms fire next tick. Otherwise: a periodic
  `bot_badaimoffset = randomvec()·(1−0.1·skill)·bot_ai_aimskill_offset(1.8)` (×0.7 vertical), applied with
  enemy_factor 5 (or 2 roaming); 5 cascaded smoothing filters (`order_filter_1st..5th` = 0.2/0.2/0.1/0.2/0.25)
  mixed in by `order_mix_1st..5th` (0.01/0.075/0.01/0.0375/0.01) scaled by `skill·0.1`; a `mouseaim` think jitter
  on a `0.5 − 0.05·skill` clock; final turn rate `r = max(fixedrate=15/dist, blendrate=2)` then
  `bound(dt, r·dt·(2 + skill³·0.005 − rand), 1)`, blended by `bot_ai_aimskill_mouse(1)`.
- **fire decision:** maxfiredeviation from a distance→angle empiric formula (`1000/(dist−9) − 0.35`) widened by
  `(shot_accurate?1:1.6) + bound(0,(10−skill)·0.3,3)`, capped 90°. If `bot_ai_aimskill_firetolerance(1)` and the
  view is within the cone: arm `bot_firetimer = time + bound(0.1, 0.5 − skill·0.05, 0.5)` — but at long range only
  with probability rising with skill+aggression. With firetolerance off: a flat 0.2s timer.
- **bot_aim:** sets `dphitcontentsmask`, scales shotspeed by `W_WeaponSpeedFactor`, leads, calls bot_aimdir, and
  for non-gravity weapons traces shotorg→enemy to refuse firing through a wall/teammate. Fires only if `time > bot_firetimer`.
- **bot_shouldattack:** reject self/teammate (teamplay: also team 0 / no-team), bots if `bot_ignore_bots` (FFA),
  non-takedamage, dead, in-chat (unless `bot_typefrag`), FL_NOTARGET, alpha-invisible, and the `Bot_ForbidAttack` hook.

### Enemy selection (`havocbot.qc:havocbot_chooseenemy`)
- Keep current enemy while `bot_shouldattack`; with a `havocbot_stickenemy_time` window re-trace LOS and keep tracking
  within 1000qu. Re-scan on `bot_ai_enemydetectioninterval(2)` (SUPERBOT 0.1). Pick nearest (`bot_ai_enemydetectionradius
  10000`²) attackable target with LOS; a second pass sees through transparent surfaces (DPCONTENTS_OPAQUE) if no
  weapon/target; misc_breakablemodel are secondary targets; SUPERBOT factors target health into the rating.

### Weapon selection (`havocbot.qc:havocbot_chooseweapon`, `bot.qc:bot_custom_weapon_priority_setup`)
- Every `bot_ai_chooseweaponinterval(0.5)`. No enemy → first available mid-range weapon. With an enemy and the custom
  priority lists (`bot_ai_custom_weapon_priority_{far,mid,close}` parsed into `bot_weapons_*`, thresholds
  `bot_ai_custom_weapon_priority_distances "300 850"` shifted by `2^bot_rangepreference`): pick the first owned weapon
  from the far/mid/close list by distance. Weapon **combos** (`bot_ai_weapon_combo 1`, threshold 0.4) hold the current
  weapon for a follow-up shot; skill scales combo timing. Tuba arena forces WEP_TUBA.

### Movement / navigation (`havocbot.qc:havocbot_movetogoal`, `havocbot_bunnyhop`, `havocbot_keyboard_movement`, `havocbot_dodge`, `havocbot_checkdanger`)
- **Goal stack** drives steering toward `goalcurrent`; pops touched goals; handles waypoint flags
  (CROUCH/JUMP/TELEPORT/LADDER/box-volume). Jump over obstacles by comparing a tracebox at feet vs at +jump height.
- **Jetpack navigation** (`bot_ai_navigation_jetpack`, min dist 3500): take off, fly toward the jetpack point, brake
  and land. **trigger_hurt escape** (skill>6): jetpack up out of a hurt volume, or rocketjump (Devastator) if HP allows.
- **Water:** swim up / jump out toward the goal.
- **havocbot_checkdanger:** look-ahead + 3000qu down-trace → SKY void / >100qu cliff / LAVA·SLIME / trigger_hurt;
  brake or mark the goal unreachable (`ignoregoal` for `bot_ai_ignoregoal_timeout 3`).
- **havocbot_dodge** (SUPERBOT only): perpendicular-to-flightpath dodge of `bot_dodge` projectiles, scaled by skill.
- **havocbot_keyboard_movement** (skill<10): quantize the analog wish-move to keyboard directions on a skill-scaled
  clock (`bot_ai_keyboard_threshold 0.57`, blended within `bot_ai_keyboard_distance 250`).
- **havocbot_bunnyhop** (skill ≥ `bot_ai_bunnyhop_skilloffset 7`): jump to keep speed toward a far goal when at run
  speed, on ground, not attacking/ducked/in-water, within `dir_deviation_max 20`, `downward_pitch_max 30`,
  `turn_angle_min 4`/`turn_angle_max 80` (`turn_angle_reduction 40` per sv_maxspeed of overspeed).
- **stepheight/jumpheight:** `stepheightvec = sv_stepheight`, `jumpheight = jumpvelocity²/(2·gravity)`,
  `jumpstepheightvec = stepheight + jumpheight·0.85`.

### Roles & goal rating (`havocbot/roles.qc`, per-gametype `havocbot_role_*`)
- `havocbot_chooserole` → `HavocBot_ChooseRole` mutator hook, else `havocbot_role_generic` (DM): rate items
  (`havocbot_goalrating_items`, ×0.0001, time-items lead for skilled bots), enemy players
  (`havocbot_goalrating_enemyplayers`, `BOT_RATING_ENEMY`, health/strength/shield-aware), and roam waypoints
  (`havocbot_goalrating_waypoints`). Team gametypes (CTF/KH/Dom/Ons/KA/Nexball/Assault) install their own roles that
  rate objectives. Routing uses the waypoint-cost (Dijkstra) field via `navigation_routerating(this, e, f, rangebias)`.
- **Strategy timeout:** `navigation_goalrating_timeout_set` → `bot_strategytime = time + bot_ai_strategyinterval(7)`
  (`_movingtarget 5.5` if the goal can move); `_force/_expire` shorten it; the role only re-rates when it expires.

### Scripting (`scripting.qc`, `scripting.qh`)
- `bot_queuecommand`/`bot_execute_commands` run a per-bot command queue (PAUSE/CONTINUE/WAIT/TURN/MOVETO/AIM/PRESSKEY/
  RELEASEKEY/SELECTWEAPON/IMPULSE/IF/ELSE/FI/BARRIER/SOUND/CC/CONSOLE/…) — drives bots verbatim, bypassing the AI.

## Port mapping
| Base | Port |
|---|---|
| `bot_serverframe` / `bot_fixcount` / `bot_spawn` / `bot_removenewest` | `BotPopulation.ServerFrame` / `FixCount`+`TargetBotCount` / `SpawnBot` / `RemoveNewest` (live from `GameWorld.OnStartFrame`) |
| strategy token | `BotPopulation.RotateStrategyToken` + `BotBrain.StrategyTokenHeld` |
| `bot_setnameandstuff` (name/model slice) | `BotPopulation.PickNameAndModel` / `ParseBotFile` |
| `bot_think` throttle + holds | `BotBrain.ThinkProduce` (think throttle, god, dead/observer, MovementHold) |
| `havocbot_ai` think loop | `BotBrain.ThinkProduce` |
| `havocbot_chooseenemy` | `BotBrain.ChooseEnemy` |
| `bot_shouldattack` | `BotBrain.ShouldAttack` |
| `havocbot_chooseweapon` + custom priority lists | `BotBrain.ChooseWeapon` / `PickOwned` (simplified hitscan/splash buckets) |
| `bot_aimdir` / `bot_aim` / `bot_shotlead` / `findtrajectorywithleading` | `BotAim.AimAt` / `AimAndDecideFire` / `ShotLead` / `BallisticArc` |
| `havocbot_movetogoal` steering / goal stack | `BotNavigation.Steer` / `SetGoal` |
| `havocbot_bunnyhop` | `BotNavigation.Bunnyhop` |
| `havocbot_checkdanger` | `BotDanger.CheckDanger` |
| `havocbot_dodge` + retreat | `BotBrain.CombatMovement` (**port-specific simplification**, not the QC projectile dodge) |
| roles + goal rating | `BotRoles` / `BotObjectiveRoles` / `GoalRater` (inverse-distance, **not** the QC Dijkstra cost field) |
| waypoints | `Waypoint.cs` / `BotTracewalk.cs` (audited under the `waypoints` unit) |
| `havocbot_keyboard_movement` | **NOT IMPLEMENTED** |
| jetpack navigation / rocketjump escape | **NOT IMPLEMENTED** |
| idle-reload logic (havocbot_ai:184-212) | **NOT IMPLEMENTED** |
| per-weapon `wr_aim` secondary fire | **NOT IMPLEMENTED** (only primary attack driven) |
| `scripting.qc` bot command queue | **NOT IMPLEMENTED** |
| `autoskill` / `skill_auto` | **NOT IMPLEMENTED** |
| 12-column per-bot skill modifiers | folded into single `BotSkill` knob (**intended divergence**) |
| `botframe_updatedangerousobjects` waypoint danger costs | **NOT IMPLEMENTED** (per-bot probe only) |

## Parity assessment

**Live:** YES. `BotPopulation.ServerFrame` runs every tick from `GameWorld.OnStartFrame`; each bot's
`BotBrain.ThinkProduce` runs in its own client physics step via `BotPopulation.InputFor` (the `sys_phys_ai`
seam) and feeds the same-tick movement + weapon drivers exactly like a human usercmd. Bots connect through
`ClientManager.OnBotConnected → RegisterBot`. The fill math, strategy-token rotation, bots.txt name/model
parse, and warmup auto-ready are all wired.

**Faithful (logic+values close to Base):** the aim pipeline (`BotAim`) is a near-line-for-line port of
`bot_aimdir` including the 5 cascaded filters, mouseaim think jitter, fixedrate/blendrate turn, SUPERBOT snap,
the maxfiredeviation formula and fire-tolerance gate with all default constants matched. `bot_shouldattack`,
the think throttle, fixcount target math, bunnyhop gating constants, and the danger-probe classification all
match Base values.

**Gaps (unintended divergence):**
- **Combat movement is invented, not ported.** `BotBrain.CombatMovement` is a hand-rolled strafe-flip + HP-bias
  blend, not QC `havocbot_dodge` (which dodges *specific incoming projectiles* by their flight path) and not the
  QC SUPERBOT random-direction combat jitter. Observable: bots strafe-dodge generically regardless of incoming
  fire; they will not specifically sidestep a rocket the way a stock bot does.
- **Goal rating ignores the waypoint-cost (Dijkstra) field.** `GoalRater.Rate` uses `f·rangebias/(rangebias+dist)`
  straight-line distance instead of QC's `navigation_routerating` along the graph. Observable: bots can prefer a
  goal that is *near in a straight line but far by path* (across a gap/wall), picking worse objectives than stock.
- **No keyboard-movement emulation.** Bots emit a fully analog wish-move; QC bots below skill 10 quantize to
  keyboard directions on a skill-scaled clock. Observable: bot strafing/turning is smoother/more precise than stock
  at all skills (a known feel difference vs Base low-skill bots).
- **No jetpack navigation and no rocketjump / jetpack trigger_hurt escape.** Observable: bots can't cross
  jetpack-only gaps and will die in a trigger_hurt they could have rocket-jumped/jetpacked out of (skill>6 in Base).
- **No idle-reload logic.** QC bots reload the held weapon (skill≥2) and pre-reload inventory weapons (skill≥5) when
  not attacking. Observable: port bots never reload off-combat, so reloadable weapons run dry mid-fight more often.
- **Only primary fire is driven.** The per-weapon `wr_aim` (which decides secondary fire, charge-up, detonation,
  combos like mortar airburst / electro combo) is not invoked; `ChooseWeapon` is a coarse hitscan/splash bucket, not
  the custom far/mid/close priority lists. Observable: bots never use secondary fire, never detonate/airburst, and
  pick weapons less intelligently by range than stock.
- **Bot scripting (`bot_cmd`/`scripting.qc`) entirely missing.** Observable: map/console bot scripts
  (`bot_cmd <n> moveto/aim/presskey/…`), cutscene-driven bots, and `g_waypointeditor` bot driving do nothing.
- **`autoskill`/`skill_auto` unported.** Observable: the server can't auto-tune bot skill to match the best human.
- **Danger evade is brake-only.** The QC lateral `evadedanger` vector (steer along the safe edge) is collapsed into a
  reverse-velocity brake. Observable: bots back straight off a ledge instead of sidestepping along it.
- **No per-waypoint danger cost accumulation** (`botframe_updatedangerousobjects`). Observable: routing doesn't avoid
  chronically dangerous waypoints; only the immediate per-bot look-ahead probe protects them.

**Intended divergences:**
- **Single `BotSkill` knob instead of the 12 per-bot skill columns.** QC reads 12 randomized per-bot skill modifiers
  (move/dodge/ping/weapon/aggres/range/aim/offset/mouse/think/ai/keyboard) from bots.txt and adds them to `skill`
  throughout. The port folds everything into one `BotSkill` per bot. Rationale: documented in BotPopulation /
  BotBrain — the port doesn't read the bots.txt skill columns; one knob keeps the same skill *axis* without the
  per-facet variance. This makes a population of equal-skill bots more homogeneous than stock.

## Verification
- **Liveness:** verified by reading the caller chain — `GameWorld.OnStartFrame:914 Bots.ServerFrame()`,
  `OnClientMove:1023` + weapon `:1181` `Bots.InputFor(p,…)`, `ClientManager.OnBotConnected = Bots.RegisterBot`
  (GameWorld:485). High confidence.
- **Aim constants:** verified by direct value diff (BotAim constants vs xonotic-server.cfg defaults). High confidence.
- **fixcount math:** verified by reading `TargetBotCount` against `bot_fixcount` branch-for-branch. High confidence.
- **Behavioral fidelity (does a match of skill-N bots feel like stock?):** NOT verified at runtime — no automated
  bot-behaviour test was run for this audit; the gaps above are read from code, not measured in-game. Medium/low
  confidence on the "how different does it feel" judgments.

## Open questions
- Does `ChooseWeapon`'s hitscan/splash bucketing produce acceptable weapon picks vs the stock far/mid/close lists in
  practice, or do bots noticeably mis-pick (e.g. never preferring Vortex at range)? Needs an in-game check.
- Is the missing idle-reload actually visible (do port bots run dry more than stock), or does the simpler ammo model
  mask it? Needs a runtime observation.
- Are there any map/campaign assets that ship `bot_cmd` scripts that now silently no-op (cutscenes)? Worth a grep of
  the shipped map entities / campaign defs.
