# mapobject-target — parity spec

**Base refs:** `common/mapobjects/target/*.qc` (+ `common/mapobjects/misc/teleport_dest.qc`, the `target_position`/`info_notnull` jumppad-dest spawns in `common/mapobjects/trigger/jumppads.qc`)
**Port refs:** `src/VortexArena.Common/Gameplay/MapObjects/{TargetUtilities,TargetSpeaker,TargetMusic,VoiceScript,Teleporters}.cs`, `game/client/MusicPlayer.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The `target_*` family are the map-placed, mostly use-activated utility entities a level designer wires
behind buttons/triggers/relays: end-or-switch the level (`target_changelevel`), campaign warp
(`target_levelwarp`), play ambient/triggered audio (`target_speaker`), override background music
(`target_music`/`trigger_music`), play a scripted voice sequence (`target_voicescript`), kill the activator
(`target_kill`), set the activator's next spawn point (`target_spawnpoint`), reproject the activator's
velocity (`target_speed`), give items (`target_give`/`target_items`), label HUD regions
(`target_location`/`info_location`), data-spawn an entity (`target_spawn`), and the teleport/jumppad
destination markers (`info_teleport_destination`/`misc_teleporter_dest`, `target_position`/`info_notnull`).
All are `SVQC` (authority) except `target_music`/`trigger_music` whose PLAYBACK is `CSQC` (presentation).
They are reached on the live path by a trigger/button calling `SUB_UseTargets` → each target's `.use`.

## Base algorithm (authoritative)

### target_kill  (`target/kill.qc:target_kill_use`)
- **Trigger:** `.use` from a SUB_UseTargets fire. Authority.
- **Algorithm:** gate `active==ACTIVE_ACTIVE`; require `actor.takedamage != DAMAGE_NO` and
  (`actor.iscreature || actor.damagedbytriggers`); then `Damage(actor, this, trigger, 1000,
  DEATH_HURTTRIGGER, DMG_NOWEP, actor.origin, '0 0 0')`.
- **Constants:** damage `1000`; deathtype `DEATH_HURTTRIGGER`. Default message "was in the wrong place",
  message2 "was thrown into a world of hurt by".
- **Reset:** `reset` re-arms `active=ACTIVE_ACTIVE`.

### target_speed  (`target/speed.qc:target_speed_use` + `target_speed_calculatevelocity`)
- **Trigger:** `.use`. Authority + a CSQC-networked copy (ENT_CLIENT_TARGET_SPEED) so client prediction can
  apply the same velocity edit.
- **Algorithm:** `actor.velocity = calculatevelocity(this, this.speed, actor)`. The calc builds a velocity
  from `.speed` and the per-axis positive/negative spawnflag mask, in three modes: percentage (speed = old
  speed × speed/100), add (accumulate onto current velocity), launcher (unit per-enabled-axis, normalized ×
  |launcherspeed|). Non-launcher non-add: `normalize(maskedvel) × speed`. Unaffected axes are preserved.
- **Constants:** default `speed = 100` (only when the key is absent). Spawnflags PERCENTAGE 1, ADD 2,
  POSITIVE_X 4, NEGATIVE_X 8, POSITIVE_Y 16, NEGATIVE_Y 32, POSITIVE_Z 64, NEGATIVE_Z 128, LAUNCHER 256.
- **Edge:** a Q3COMPAT launcher branch accumulates launcherspeed *inside* the loop (a deliberately-preserved
  Q3 bug); the non-Q3 path accumulates once outside.

### target_spawnpoint  (`target/spawnpoint.qc`)
- **Trigger:** `.use`. Authority. `actor.spawnpoint_targ = this` — forces the activator's next spawn here.

### target_location / info_location  (`target/location.qc`)
- **Trigger:** spawn-time only (passive). `target_push_init` makes it SOLID_NOT no-touch; `IL_PUSH(g_locations)`.
  HUD `%l` picks the nearest location name (`.netname`; `info_location` copies `.netname`→`.message` then
  `.netname` is read as the name). Authority owns the list; the label is presentation.

### target_changelevel  (`target/changelevel.qc:target_changelevel_use`)
- **Trigger:** `.use`. Authority.
- **Algorithm:** gate `!game_stopped`, `active==ACTIVE_ACTIVE`. If CHANGELEVEL_MULTIPLAYER: only players
  trigger; stamp `actor.chlevel_targ=this`; count real (non-bot) clients and how many voted for THIS target;
  bail unless `plnum >= ceil(realplnum * min(1,count))`. If `.gametype!=""` switch next-map gametype. If
  `.chmap==""` → `NextLevel()` (and `campaign_forcewin=true` if a real client triggered in campaign), else
  `changelevel(chmap)`.
- **Constants:** `count` default `0.7` (fraction of real players). Spawnflag `CHANGELEVEL_MULTIPLAYER = BIT(1) = 2`
  (changelevel.qh). **PORT BUG:** the port hard-codes this bit as `1 << 0` (=1), so the multiplayer-fraction
  branch is gated on the wrong bit. Additionally the `.chmap`/`.gametype` entity keys are never bound from the
  BSP in the port (declared but no field binder reads them), so `.chmap` is always "" at runtime.

### target_levelwarp  (`target/levelwarp.qc`)
- **Trigger:** `.use`, campaign only (`autocvar_g_campaign`). `CampaignLevelWarp(cnt-1)` for a specific
  1-based level, else `CampaignLevelWarp(-1)` for the next level.

### target_give  (`target/give.qc:target_give_use`)
- **Trigger:** `.use`. Authority. Require a live (non-dead) player actor. For each `g_items` entity whose
  `.targetname == this.target`: hand its `itemdef` to the actor via `ITEM_HANDLE(Pickup,…)` and play the
  pickup sound (powerups on CH_TRIGGER_SINGLE, else CH_TRIGGER), or `GiveBuff` for a buff item (SND_SHIELD_RESPAWN);
  then `SUB_UseTargets(it, actor, trigger)` unless the item's target is "" / "###item###".

### target_items  (`target/items.qc:target_items_use` + spawnfunc serializer)
- **Trigger:** `.use`. Authority. Require a live player (loot toucher is deleted). `GiveItems(actor, 0,
  tokenize(netname))`; on success `centerprint(actor, message)`. The spawnfunc parses the human item tokens
  into a normalized `max/min/minus`-prefixed give string (driven by spawnflags 0/1/2/4) seeded with powerup
  durations from the balance cvars, and pre-inits the named weapons.
- **Constants:** powerup-duration defaults from `g_balance_powerup_{strength,invincible,speed,invisibility}_time`
  and `g_balance_superweapons_time`.

### target_spawn  (`target/spawn.qc`)
- **Trigger:** `.use` and/or ON_MAPLOAD (spawnflag). Authority. A reflective entity creator/editor: `.message`
  is a `key value …` list with a `$`-templating mini-language (field DB via numentityfields,
  putentityfieldstring, deferred replacement, activator/other/target/time substitutions, +offset, +random,
  helper spawnfuncs). Can spawn a new entity (`.target==""`), edit the activator (`*activator`), or edit all
  entities named by `.target`. `.count` caps the number created.

### target_speaker  (`target/speaker.qc`)
- **Trigger:** spawn (ambient) and/or `.use`. Authority drives `_sound`/`ambientsound`/`soundto`.
- **Algorithm/spawn:** precache `.noise`. Resolve atten: legacy GLOBAL (BIT2) w/ atten 0 & no loop flags → -1;
  atten 0 → ATTEN_NORM (targeted) or ATTEN_STATIC (untargeted); atten<0 → 0 (= ATTEN_NONE play-everywhere).
  Volume default 1. If targeted: ACTIVATOR(8) → `target_speaker_use_activator` (`soundto MSG_ONE` to the
  triggering real client only — random `*`-voice sample support); LOOPED_ON(1) → start now, `.use` toggles
  off/on (CH_TRIGGER_SINGLE); LOOPED_OFF(2) → start silent, `.use` toggles on/off; else one-shot per use.
  If untargeted: LOOPED_ON or fallback → `ambientsound` then `delete(this)`; LOOPED_OFF untargeted → objerror.
- **Constants:** channel CH_TRIGGER_SINGLE (single-replacement) / CH_TRIGGER. VOL_BASE × `.volume`.
- **Edge:** `*`-prefixed noise = a per-player voice-message sample with `argv(1)` random count.

### target_music / trigger_music  (`target/music.qc`)
- **Trigger:** `target_music` `.use` (per-activator override); `trigger_music` is a brush volume whose CSQC
  `Ent_TriggerMusic_Think` flags it when the local view box is inside. Authority records params; **playback is
  CSQC** (`TargetMusic_Advance`). Priority: `trigger_music` > targeted `target_music` (within `.lifetime`) >
  default `target_music` (lifetime 0 / untargeted) > cdtrack.
- **State/networking:** `target_music_sendto` (TE_CSQC_TARGET_MUSIC) writes volume×255, fade_time×16,
  fade_rate×16, lifetime byte, noise string per-client on use; `trigger_music_SendEntity` net-links the brush.
  CSQC ramps each source's volume by `frametime/fade_time` (in) and `frametime/fade_rate` (out), restarts at
  the source's `getsoundtime`, on CH_BGM_SINGLE at ATTEN_NONE.
- **Constants:** volume default 1; fade_time/fade_rate in seconds; lifetime in seconds (0 = becomes default).

### target_voicescript  (`target/voicescript.qc`)
- **Trigger:** `.use` latches the script onto the activator (`actor.voicescript=this`, index 0, nextthink =
  time+delay). Authority. The per-player tick `target_voicescript_next(pl)` is called every PlayerPreThink.
- **Algorithm:** `.message` is `<file> <dur> … * <rndfile> <rnddur> …`. The ordered prefix is `cnt` lines
  (the part before `*`); after exhaustion it loops the random pool. When the current line ended and nextthink
  arrived: pick the next token, `play2(pl, netname/file.wav)`, then schedule `voiceend = time+dur`,
  `nextthink = voiceend + wait×(0.5+random())`; a negative `dur` means no extra delay (`voiceend=time-dur`,
  `nextthink=voiceend`). When the index runs out, `pl.voicescript=NULL`.
- **Constants:** `.wait` average gap; `.delay` initial delay.

### info_teleport_destination / misc_teleporter_dest  (`misc/teleport_dest.qc`)
- **Trigger:** spawn-time. Authority sets `.mangle=.angles`, clears `.angles`, requires a targetname
  (objerror otherwise), net-links (ENT_CLIENT_TELEPORT_DEST) so CSQC prediction can teleport. The destination
  is consumed by `trigger_teleport`/`target_teleporter` via `Simple_TeleportPlayer` (origin + nudge
  `1 - mins.z - 24`, exit velocity reprojected along `mangle` forward, optional `.speed` clamp, `.cnt` weight).

### target_position / info_notnull  (`trigger/jumppads.qc:target_position`/`info_notnull`)
- **Trigger:** spawn-time, via `target_push_init` — a passive position marker pointed at by a jumppad
  (`trigger_push`) or `trigger_impulse` as its arc target. Carries origin/angles only.

## Port mapping
| Base entity | Port symbol | Layer |
|---|---|---|
| target_kill | `TargetUtilities.KillSetup/KillUse` | authority |
| target_speed | `TargetUtilities.SpeedSetup/SpeedUse/CalculateVelocity` | authority (CSQC net copy NOT ported) |
| target_spawnpoint | `TargetUtilities.SpawnPointSetup/SpawnPointUse` | authority |
| target_location / info_location | `TargetUtilities.LocationSetup/InfoLocationSetup` | authority+presentation |
| target_changelevel | `TargetUtilities.ChangeLevelSetup/ChangeLevelUse` (+ NextLevelHandler/ChangeLevelHandler/RealPlayerVoteCount seams) | authority |
| target_levelwarp | `TargetUtilities.LevelWarpSetup/LevelWarpUse` (+ CampaignLevelWarpHandler/IsCampaign seams) | authority |
| target_give | `TargetUtilities.GiveSetup/GiveUse` (+ GiveItemHandler seam) | authority |
| target_items | `TargetUtilities.ItemsSetup/ItemsUse/ApplyGiveTokens` | authority |
| target_spawn | `TargetUtilities.SpawnSetup/SpawnUse` (+ SpawnEntityHandler seam) — MINIMAL | authority |
| target_speaker | `TargetSpeaker.SpeakerSetup` + use handlers | authority(+audio) |
| target_music / trigger_music | `TargetMusic.*` (server state) + `game/client/MusicPlayer.cs` (playback) | authority+presentation |
| target_voicescript | `VoiceScript.VoiceScriptSetup/Use/Next/Clear` | authority(+audio) |
| info_teleport_destination / misc_teleporter_dest | `Teleporters.TeleportDestSetup` | authority |
| target_position / info_notnull | `Jumppads.TargetPushSetup` (out of this unit; jumppad dest) | authority |

All classnames are registered in `MapObjectsRegistry.RegisterAll()`, which is called live from
`GameInit.InstallGameplaySystems` → reached by `GameWorld.SpawnMapEntities`. `.use` is fired on the live path
by `MapObjectsCommon.UseTargets` (faithful `SUB_UseTargets`).

## Parity assessment

### Live & faithful (authority logic)
- **target_kill, target_speed, target_spawnpoint, target_location/info_location, target_give, target_items,
  target_voicescript** — logic/values match Base; reached via UseTargets; `VoiceScript.Next` is pumped every
  per-client tick in `GameWorld.PlayerPreThink`; `target_give` routes through the wired `GiveItemHandler`.
  Covered by `TargetUtilitiesTests` + `MapObjectLongTailTests` (voicescript). The Q3COMPAT launcher bug branch
  in target_speed is intentionally NOT taken (no Q3COMPAT flag) — flagged intended_divergence.

### Live with gaps
- **target_speaker** — ACTIVATOR (BIT3) plays to ALL players, not just the triggering client (no MSG_ONE);
  the `*`-prefixed per-player voice-sample randomization is not implemented; channel mapped to `Body` not a
  faithful CH_TRIGGER_SINGLE. Ambient/looped/one-shot logic is otherwise faithful.
- **target_music / trigger_music** — re-architected: the port reads server entity state directly in
  `MusicPlayer.cs` rather than QC's CSQC `TargetMusic_Advance` + TE_CSQC_TARGET_MUSIC net path. Consequences:
  (1) works only on the **listen-server** path — a remote/dedicated client gets NO target_music/trigger_music
  override (the net message is not ported); (2) `target_music_use` is GLOBAL, not the QC **per-activator +
  spectators** override; (3) trigger_music picks the FIRST fresh volume, QC uses the last-touched;
  (4) fade defaults to a 2s crossfade where QC uses fade_time/fade_rate (0 = instant). Priority stack and
  lifetime/default-slot semantics are otherwise faithful.

### Dead on the live path (logic present, no live caller / unwired seam)
- **target_changelevel** — the `NextLevelHandler` / `ChangeLevelHandler` / `RealPlayerVoteCount` seams are set
  ONLY in tests; nothing in `src/`/`game/` wires `TargetUtilities.NextLevelHandler` etc. (`Commands.ChangeLevelHandler`
  is a SEPARATE seam for the console `map` command). So a map's target_changelevel degrades to a no-op:
  hitting an end-of-level trigger does not end/switch the match. Multiplayer-fraction logic also never sees a
  real client list (falls back to single-vote). The `.gametype` next-map switch is explicitly not ported.
  **TWO MORE DEFECTS found in the adversarial pass:** (a) the `CHANGELEVEL_MULTIPLAYER` spawnflag is mis-defined
  in the port as `1 << 0` (=1) where changelevel.qh has `BIT(1)` (=2) — the multiplayer branch is gated on the
  wrong bit; (b) the `.chmap`/`.gametype` keys are never bound from the BSP entity dict (no field binder reads
  them), so `.chmap` is always "" and only the empty-chmap NextLevel branch is ever reachable even if wired.
- **target_levelwarp** — `CampaignLevelWarpHandler`/`IsCampaign` seams unwired in src/game → no-op (campaign
  level warp triggers do nothing).
- **target_spawn** — MINIMAL by design (the `$`-templating reflection engine is cut) AND its
  `SpawnEntityHandler` is unwired → both the on-use and ON_MAPLOAD paths no-op. Maps relying on target_spawn
  to create/edit entities get nothing.

### Reset infrastructure
- Every entity's QC `.reset` (re-arm `active=ACTIVE_ACTIVE` on round restart) is documented as "dormant until
  reset infra exists" in the port — only `VoiceScript` sets `.Reset`. So on a round/match restart these
  entities do not re-arm their active state the way Base does (minor; most stay ACTIVE_ACTIVE anyway).

## Verification
- `target_kill/speed/spawnpoint/location/changelevel/levelwarp/items/door_secret` — unit-tested in
  `tests/VortexArena.Tests/TargetUtilitiesTests.cs` (logic + values), but the changelevel/levelwarp tests
  inject the seams locally, so they do NOT prove live wiring.
- `target_voicescript` — `MapObjectLongTailTests.cs` (latch + advance + clear).
- Liveness of changelevel/levelwarp/spawn seams established by grep: no `TargetUtilities.NextLevelHandler |
  ChangeLevelHandler | RealPlayerVoteCount | CampaignLevelWarpHandler | IsCampaign | SpawnEntityHandler`
  assignment exists outside `tests/`.
- target_give live: `GameWorld.cs:521` wires `GiveItemHandler`.
- VoiceScript live: `GameWorld.cs:1156` pumps `VoiceScript.Next`.
- target_music/trigger_music: read by `MusicPlayer.cs` (instantiated `NetGame.cs:1059`); net-replication path
  unverified at runtime — assessed from code only.
- target_speaker, teleport dest: assessed from code; not runtime-verified for this audit.

## Open questions
- Should target_changelevel/target_levelwarp be wired to the existing `GameWorld.NextLevel`/`Campaign.LevelWarp`?
  The handlers exist and the server methods exist; only the assignment is missing. This is the single highest-value
  fix in this unit (end-of-level triggers are currently inert).
- Is the listen-server-only music override acceptable, or do remote clients need the TE_CSQC_TARGET_MUSIC path?
