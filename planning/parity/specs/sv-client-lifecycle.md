# SV client lifecycle — parity spec

**Base refs:** `server/client.qc` (ClientConnect / ClientDisconnect / PutClientInServer / PutPlayerInServer / PutObserverInServer / Join / PlayerPreThink / PlayerPostThink / PlayerThink / ObserverOrSpectatorThink / PlayerFrame / player_regen / DrownPlayer / GetPressedKeys / ShowRespawnCountdown / calculate_player_respawn_time / FixClientCvars / SendWelcomeMessage / ClientInit_*)
**Port refs:** `src/VortexArena.Server/ClientManager.cs`, `src/VortexArena.Server/PlayerFrameLogic.cs`, `src/VortexArena.Server/GameWorld.cs`, `src/VortexArena.Server/ServerPlayerState.cs`, `src/VortexArena.Common/Gameplay/Player/RespawnTiming.cs`, `src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs`, `game/net/ServerNet.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
This unit is the server-authoritative spine of a player's existence on the server: the connect
handshake (`ClientConnect`), the observer→player join gate (`Join` / `ObserverOrSpectatorThink` +
the delayed autojoin in `PlayerPreThink`), the spawn placement & loadout (`PutClientInServer` →
`PutPlayerInServer` / `PutObserverInServer`), the per-frame per-player bookkeeping that is NOT
movement (`PlayerThink`/`PlayerPostThink`/`PlayerFrame`: regen/rot, drowning, powerup countdown,
pressed-keys stat, idle-kick, version nag, chat bubble, respawn countdown), the dead-player respawn
state machine, and the disconnect teardown (`ClientDisconnect`). It runs for every real client and
bot every server frame. Movement itself (`PM_Main`) and the weapon driver (`W_WeaponFrame`) are
separate units; this spec covers what wraps them.

## Base algorithm (authoritative)

### ClientConnect  (`server/client.qc:1143`)
- **Trigger:** DP calls it once when a client's connection is accepted (server side).
- **Algorithm:** `Ban_MaybeEnforceBanOnce`; `TRANSMUTE(Client)`; broadcast `INFO_JOIN_CONNECT`
  ("<name> connected"); `bot_clientconnect`; `team = -1`; `Player_DetermineForcedTeam`;
  `TRANSMUTE(Observer)` (a fresh client is ALWAYS an observer — never spawned on connect);
  playerstats AddEvent `kills-<id>`; event-log `:join:`; set `just_joined`, `wants_join=0`;
  `stuffcmd(clientstuff)`, `cl_particles_reloadeffects`; `FixClientCvars`;
  `cmd clientversion $gameversion`; stuff `_teams_available`; set `spectatortime=time`,
  `jointime=time`; ignore-list / quickmenu / fog stuffcmds; `CSQCMODEL_AUTOINIT`;
  `model_randomizer = random()`; `sv_notice_join`; `Physics_UpdateStats`; `Handicap_Initialize`;
  playban/chatban; `MUTATOR_CALLHOOK(ClientConnect)`; `sv_hook_firstjoin` when player_count==1.
- **Constants:** `version_nagtime = time + 10 + random()*10`; `MIN_SPEC_TIME = 1` (server/client.qh:403).

### Join  (`server/client.qc:2074`)
- **Trigger:** observer presses +jump (`ObserverOrSpectatorThink`) or the delayed autojoin trips in
  `PlayerPreThink` (after MIN_SPEC_TIME, unless sv_spectate/g_campaign holds it), or a bot's first
  observer think.
- **Algorithm:** campaign bot-start gate; queue-balance / team-selection (queuedPlayer); clear
  `CPID_PREVENT_JOIN`; `TRANSMUTE(Player)`; `PutClientInServer`; then the join notifications
  (`INFO_JOIN_PLAY` / `CENTER_JOIN_PLAY_TEAM` / `ANNCE_BEGIN` for a queued player). Clears
  `team_selected`, `wants_join`.
- **joinAllowed** (`:2258`): version mismatch, `time < jointime + MIN_SPEC_TIME`, lockteams,
  forced-spectator, playban, g_maxping, queue/nJoinAllowed (g_maxplayers).

### PutClientInServer / PutPlayerInServer / PutObserverInServer  (`:865` / `:580` / `:261`)
- **PutClientInServer:** SVC_SETVIEW; `game_stopped` ⇒ force Observer; `SetSpectatee(NULL)`;
  `MUTATOR_CALLHOOK(PutClientInServer)`; branch to PutObserver or PutPlayer.
- **PutPlayerInServer:** `ForbidSpawn` hook; team assign (TeamBalance_JoinBestTeam);
  `SelectSpawnPoint` (fail ⇒ `CENTER_JOIN_NOSPAWNS`, return); `TRANSMUTE(Player)`; iscreature,
  MOVETYPE_WALK, SOLID_SLIDEBOX, dphitcontentsmask, `flags = FL_CLIENT|FL_PICKUPITEMS`,
  takedamage=DAMAGE_AIM, `effects = EF_TELEPORT_BIT|EF_RESTARTANIM_BIT`. Loadout: warmup ⇒
  `GiveWarmupResources`, else `start_ammo_*`/`start_health`/`start_armorvalue`/`start_weapons`
  (+ random-start-weapons). `SetSpectatee_status(0)`; superweapon status if WEPSET_SUPERWEAPONS;
  `items = start_items`; **spawn shield** `StatusEffects_apply(SpawnShield, time + g_spawnshieldtime)`;
  prime pause timers (`pauserotarmor/health/fuel_finished`, `pauseregen_finished` +
  `g_balance_pause_*_spawn`); **countdown extension** — `time < game_starttime` rolls shield + pauses
  forward by `(game_starttime - time)`; `damageforcescale = g_player_damageforcescale`;
  `scale = 0.8125`; angles from spot (z=0, fixangle); zero velocities/punch; AIR_FINISHED=0;
  SpawnEvent net entity; `stopsound(CH_PLAYER_SINGLE)`; `FixPlayermodel`; setsize PL_MIN/MAX;
  setorigin `spot.origin + '0 0 1'*(1-mins.z-24)`; clear conveyor/swamp/ladder/counters;
  `event_damage = PlayerDamage`; bot/monster target lists; spawn weapon entities; `alpha`/`colormod`;
  reset all weapons (`wr_resetplayer`, reload); `MUTATOR_CALLHOOK(PlayerSpawn, spot)`;
  `SUB_UseTargets(spot)`; pick best weapon; `MUTATOR_CALLHOOK(PlayerWeaponSelect)`; `ImpulseCommands`
  if queued; `W_ResetGunAlign`; `W_WeaponFrame`; `alivetime_start`; `antilag_clear`; `ReadyCount` in
  warmup.
- **PutObserverInServer:** `MakePlayerObserver` hook; despawn EFFECT_SPAWN if alive;
  recount votes/ready; SelectObservePoint/SelectSpawnPoint; SVC_SETVIEW; setmodel MDL_Null;
  setsize PL_CROUCH; RemoveGrapplingHooks; Portal_ClearAll; SetSpectatee(NULL); strip everything:
  `RES_HEALTH = FRAGS_SPECTATOR`, takedamage=DAMAGE_NO, solid=SOLID_NOT,
  MOVETYPE_FLY_WORLDONLY, `flags = FL_CLIENT|FL_NOTARGET`, RES_ARMOR = g_balance_armor_start, clear
  pause/respawn/alpha/scale, items=0, weapons cleared, `killcount = FRAGS_SPECTATOR`; `spectatortime`;
  spectator-block notification (SPECTATE_WARNING, `g_maxplayers_spectator_blocktime`); SetPlayerTeam(-1).

### ObserverOrSpectatorThink  (`:2501`)
- **Trigger:** every server frame for an observing/spectating client (from PlayerPreThink).
- **Algorithm:** minigame impulse; bot first-think autojoin; FL_JUMPRELEASED gate; +jump ⇒ set
  FL_SPAWNING (join on release if joinAllowed); +attack ⇒ SpectateNext; +attack2 ⇒ drop to free-fly /
  PutObserverInServer; spectate prev (impulse 12/16/19/220-229); SpectateUpdate each tick; free-fly
  movetype toggle on +use (cl_clippedspectating); `sv_spectate 2` blocks spectating.

### PlayerPreThink / PlayerThink / PlayerPostThink / PlayerFrame  (`:2683` / `:2336` / `:2821` / `:2852`)
- **PlayerPreThink** (every frame AND every async move; `frametime==0` on async): FixVAngle;
  `MUTATOR_CALLHOOK(PlayerPreThink)`; PlayerUseKey on +use edge; if player ⇒ PlayerThink (error if
  spawned as player within MIN_SPEC_TIME); delayed autojoin for an un-joined real client; else
  ObserverOrSpectatorThink; SetZoomState (weapon zoom); teamkill/taunt voice sounds;
  `target_voicescript_next`.
- **PlayerThink:** game_stopped/intermission early-out; timeout freezes view; `player_powerups`;
  `show_entnum`; **dead branch** = the respawn state machine (DEAD_DYING→DEAD→RESPAWNABLE→RESPAWNING,
  `ShowRespawnCountdown`, STAT(RESPAWN_TIME) management); `FixPlayermodel`; shootfromfixedorigin
  stuffcmd; dualwielding gun-align; Vortex charge; `W_WeaponFrame`; (frametime) `player_regen`,
  `player_anim`, dmg_team decay; `monsters_setstatus`.
- **PlayerPostThink:** `Player_Physics`; if player ⇒ `DrownPlayer`, `UpdateChatBubble`,
  ImpulseCommands, `GetPressedKeys`; observer clears PRESSED_KEYS; `CSQCMODEL_AUTOUPDATE`.
- **PlayerFrame** (once per server frame, frametime always set): `Physics_UpdateStats`; alivetime
  accumulation (afk-gated by `parm_idlesince`); score_frame_dmg/dmgtaken → scoring; GUNALIGN stat;
  `anticheat_prethink`; **sv_spectate disabled ⇒ kick spectators**; **nameless/invisible/too-long name
  check** (`sv_name_maxlength`); **version nag** (`version_nagtime`, vercmp); ignore-list send;
  GOD MODE info; vehicle-enter centerprints; **sv_maxidle idle-kick / move-to-spec** (the big idle
  block with `sv_maxidle`, `sv_maxidle_playertospectator`, countdown bangs); `CheatFrame`;
  game_stopped freeze; waypointsprite health.

### player_regen / DrownPlayer / calculate_player_respawn_time  (`:1688` / `:2765` / `:1399`)
- **player_regen:** `MUTATOR_CALLHOOK(PlayerRegen)` (instagib disables); RotRegen armor + health
  (CalcRegen/CalcRot snap-when-close), kill if HP<1 (DEATH_ROT), fuel regen/rot (shares
  pauseregen_finished). Constants below.
- **DrownPlayer:** dead/game_stopped/pre-game/vehicle/frozen/not-water ⇒ AIR_FINISHED=0; surfaced
  while gasping ⇒ `playersound_gasp`; submerged ⇒ AIR_FINISHED = time + drowndelay, then 2 Hz
  drown Damage(DEATH_DROWN).
- **calculate_player_respawn_time:** pcount-scaled delay between small/large, wave quantize,
  respawn_time_max = g_respawn_delay_max, countdown=10 for long waits, RESPAWN_FORCE from
  g_forced_respawn.

### FixClientCvars / SendWelcomeMessage / ClientInit  (`:999` / `:1077` / `:895`)
- **FixClientCvars:** stuffcmd of `cl_jumpspeedcap_min/max`, `cl_shootfromfixedorigin`,
  prediction/gentle settings — server pushes its physics-relevant cvars to the client.
- **SendWelcomeMessage / ClientInit_misc:** the MOTD/hostname/map/mutators welcome blob + the
  per-client init entity carrying armor-blockpercent, damagepush speedfactor, fog, serverflags,
  `g_trueaim_minrange`, hook/arc shot origins.

### Constants (Base defaults, all authority `sv_`/`g_` unless noted)
- `MIN_SPEC_TIME = 1` s — observer dwell before join.
- `g_respawn_delay_small = 2`, `g_respawn_delay_large = 2`, `g_respawn_delay_max = 5`,
  `g_respawn_delay_small_count = 0`, `g_respawn_delay_large_count = 8`, `g_respawn_waves = 0`,
  `g_forced_respawn = 0`.
- `g_spawnshieldtime = 1` s (xonotic-server.cfg); `g_player_damageforcescale = 2`.
- player scale `0.8125` (DP 1/16); `default_player_alpha`; `g_player_brightness`.
- regen/rot: `g_balance_health_regen 0.1`, `_regenstable 100`, `_regenlinear 0`, `_rot 0.1`,
  `_rotstable 100`, `_rotlinear 0`; armor `_regen 0`, `_regenstable 0`, `_rot 0.1`, `_rotstable 100`;
  fuel `_regen 0.1`, `_regenstable 50`, `_rot 0.05`, `_rotstable 0`; CalcRegen/CalcRot snap window
  `0.25`. pause-on-spawn: `pause_health_regen_spawn 0`, `pause_health_rot_spawn 5`,
  `pause_armor_rot_spawn 5`, `pause_fuel_rot_spawn 10`.
- drown: `g_balance_contents_drowndelay 10`, `_playerdamage_drowning 10`, `_damagerate 0.2`, pain gate 0.5 s.
- `sv_maxidle 0` (off), `sv_maxidle_playertospectator 0`, `sv_maxidle_minplayers 0`,
  `sv_name_maxlength 64`, `version_nagtime` random 10-20 s.

## Port mapping
- **ClientConnect** → `ClientManager.ClientConnect` + `GameWorld.InfraClientConnect` + the ServerNet
  admit path. LIVE. Models a client as a `Player` from the start (ADR-0007), marks the observer phase
  (`IsObserver`, `FragsStatus = FragsSpectator`, MOVETYPE_NONE/SOLID_NOT), fires the connect hooks
  (ban, event-log, stats, anticheat, timeout, CHAT_CONNECT). Bots autojoin at connect.
- **Join** → `ClientManager.Join`. LIVE (via ObserverOrSpectatorThink + bot connect). Reduced: clears
  observer phase + intent, spawns. Queue/team-selection/join-notifications NOT modeled.
- **joinAllowed** → `ClientManager.JoinAllowed`. Reduced: MIN_SPEC_TIME + lockteams only;
  version-mismatch / forced-spectator / playban / g_maxping / queue gates deferred.
- **PutPlayerInServer** → `ClientManager.Spawn` → `SpawnSystem.PutPlayerInServer`. LIVE. Loadout,
  hull/placement, spawn shield, pause timers, fixangle, model, PlayerSpawn hook. The
  `time < game_starttime` shield/pause **countdown extension is NOT applied** (noted as a TODO in
  SpawnSystem). `SUB_UseTargets(spot)` and `wr_resetplayer`/reload-on-spawn not confirmed here.
- **PutObserverInServer** → `ClientManager.PutObserverInServer`. Present; the live-player→observer
  `spectate` command path exists. SelectObservePoint, despawn-effect, vote/ready recount, the
  SPECTATE_WARNING block are NOT modeled.
- **ObserverOrSpectatorThink** → `ClientManager.ObserverOrSpectatorThink`, driven LIVE per human peer
  by `ServerNet.DriveObserverJoins`. +jump join, +attack SpectateNext, +attack2 free-fly,
  SpectateCopy each tick, delayed autojoin. SpectatePrev (impulse 12/16/19), minigame impulse, the
  +use clippedspectating movetype toggle, `sv_spectate 2` block NOT modeled.
- **PlayerThink dead branch** → `GameWorld.DeadPlayerThink` (death edge sets DeadFlag.Dying in
  `DamageSystem`), respawn timing via `RespawnTiming.Calculate`. LIVE. STAT(RESPAWN_TIME) maintained.
- **PlayerPostThink / PlayerFrame core** → `GameWorld.OnPlayerPostThink` + `CreatureFrameAll`:
  `PlayerFrameLogic.Regen`, `DrownPlayer`, `ContentsDamage`, `FallDamage`, `PlayerPowerups`,
  StatusEffects tick, WeaponThink. LIVE.
- **GetPressedKeys** (PRESSED_KEYS stat) → the server-authoritative stat is NOT set on the lifecycle path.
  IMPORTANT (corrected): the LOCAL player's HUD pressedkeys/strafe panel IS faithful — `PressedKeysPanel`
  computes the KEY_* bitmask client-side from the local bind state (`LocalPressedKeys`). The gap is that a
  SPECTATED player's held keys (`PressedKeysPanel.PressedKeysOverride`) are never fed by the net layer, and
  the QC observer-clears-PRESSED_KEYS rule (PlayerPostThink) is absent. So the panel works for yourself but
  not when spectating.
- **ShowRespawnCountdown** → NOT IMPLEMENTED. `RespawnCountdown` is computed by `RespawnTiming` but
  nothing consumes it to fire the 10-9-8 `CNT_RESPAWN` announcer cue.
- **DrownPlayer gasp sound** → the surfaced-gasp branch resets the timer but does NOT play
  `playersound_gasp` (commented as such).
- **sv_maxidle idle-kick / move-to-spec** → NOT IMPLEMENTED.
- **version nag** → NOT IMPLEMENTED.
- **nameless / too-long / invisible name enforcement** (`sv_name_maxlength`) → NOT IMPLEMENTED on this path.
- **UpdateChatBubble** (chat/minigame-busy bubble over a typing player) → NOT IMPLEMENTED.
- **FixClientCvars / SendWelcomeMessage / ClientInit_misc** → NOT IMPLEMENTED as a server→client
  stuffcmd/welcome channel; the port pushes a few cvars via per-client replicated-cvar tables instead.
- **alivetime afk-gating** (`parm_idlesince`-driven accumulation pause) → partial: PlayerStats
  begins alivetime on spawn, but the afk pause is not modeled (no parm_idlesince).
- **ClientDisconnect** → `ClientManager.ClientDisconnect` + `GameWorld.InfraClientDisconnect`. LIVE.
  Removes from roster/scores/sim/entity table, fires CHAT_DISCONNECT, EFFECT_SPAWN puff, stats
  finalize, event-log `:part:`. ignore-list cleanup, portal/hook teardown, killindicator/personal
  cleanup, ReadyCount/VoteCount, TeamBalance_RemoveExcessPlayers NOT all modeled.

## Parity assessment
- **Connect/join/disconnect core (logic):** faithful and LIVE for the reduced single-listen-server
  path. The big deferral is the **queue / team-selection / join-notification** machinery (queuePlayer,
  TeamBalance_JoinBestTeam ordering, the CENTER_JOIN_* centerprints, ANNCE_BEGIN) — observable as
  missing team-pick UI and join feedback in team modes. joinAllowed's version/playban/maxping/forced
  gates are absent, so a mismatched/banned/laggy client isn't blocked from joining.
- **Spawn (logic/values):** loadout, hull, placement, shield, pause timers faithful. The
  **countdown-window shield/pause extension** is missing — a player spawning during the pre-match
  countdown loses spawn protection earlier than Base. Player `scale = 0.8125` confirmed in SpawnSystem.
- **Per-frame regen/drown/contents/falldamage:** health/armor regen, drown, contents (lava/slime) and fall
  damage are faithful and LIVE (CalcRegen/CalcRot snap behavior matches; pause-timer storage unified).
  TWO real divergences found this pass: (1) **fuel regen is ungated** — Base gates fuel REGEN on the
  IT_FUEL_REGEN item flag and skips the whole fuel block under IT_UNLIMITED_AMMO, but the port regenerates
  fuel whenever the regen pause has lapsed (a player regenerates fuel with no fuel-regen pickup); (2)
  **DrownPlayer gasp sound** is missing (the surfaced-after-out-of-air branch resets the timer but never
  plays playersound_gasp).
- **Respawn state machine + timing:** faithful and LIVE (DEAD_* machine button-gated, RespawnTiming
  pcount-scaling matches). **ShowRespawnCountdown announcer (10-9-8)** is the presentation gap:
  RespawnCountdown is computed but never voiced.
- **PRESSED_KEYS stat:** missing — the HUD pressedkeys / strafe panel can't show held keys from this
  path.
- **Idle-kick / version-nag / name-enforcement / chat-bubble / welcome-MOTD / FixClientCvars:** all
  missing. These are server-administration / presentation features; their absence is observable
  (no idle kick, no MOTD, no name truncation, no chat bubble over typers) but they don't break core play.

## Liveness
- LIVE callers confirmed: `game/net/ServerNet.cs` drives `ClientConnect` (admit), `ClientDisconnect`
  (peer drop), `ObserverOrSpectatorThink` (`DriveObserverJoins`, per accepted human peer per frame).
  `GameWorld.OnClientMove`/`OnPlayerPostThink`/`CreatureFrameAll`/`OnEndFrame` drive the per-frame
  regen/drown/contents/falldamage/powerups + `DeadPlayerThink`. Bots autojoin in `ClientConnect`.
- DEAD: none in this unit — the implemented pieces are wired.

## Verification
- Code-trace only (no runtime check this pass). Connect/disconnect/observer-think liveness verified by
  grep of `game/net/ServerNet.cs` (lines 438, 512, 638, 1054). Per-frame driver verified in
  `GameWorld.cs` (CreatureFrameAll :961, OnPlayerPostThink :1117, DeadPlayerThink :1698). Constants
  read from `RespawnTiming.cs` and `SpawnSystem.cs`. Missing features verified by absence of any
  symbol (GetPressedKeys/PRESSED_KEYS set, ShowRespawnCountdown, sv_maxidle, version_nag,
  ChatBubble, FixClientCvars/SendWelcomeMessage in src/ + game/).

## Open questions
- Does any other path set the networked PRESSED_KEYS stat for the HUD (e.g. the net snapshot
  derives it from movement input)? Not found; flagged low-confidence.
- Are `SUB_UseTargets(spot)` (spawnpoint targets firing on spawn) and `wr_resetplayer` /
  reload-on-spawn modeled elsewhere in the spawn path? Not confirmed in PutPlayerInServer here.
- The `time < game_starttime` shield/pause countdown extension is explicitly TODO in SpawnSystem —
  needs a match-time handle; confirm whether the warmup/countdown path supplies one.
