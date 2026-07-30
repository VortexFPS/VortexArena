# Sound system (registry + dispatch) — parity spec

**Base refs:** `common/sounds/{sound.qh,all.qh,all.qc,all.inc}`, `common/effects/qc/globalsound.{qh,qc}`
**Port refs:** `src/VortexArena.Common/Gameplay/Sounds/{GameSound,SoundsList,SoundSystem,VoiceMessage,VoiceTypes}.cs`,
`src/VortexArena.Common/Services/Services.cs` (ISoundService / SoundChannel), `src/VortexArena.Engine/Simulation/EngineServices.cs` (SoundService / SoundEvent), `src/VortexArena.Net/SoundWire.cs`, `game/client/ClientWorld.cs` (playback), `game/client/HitSound.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
`fx-sounds` is the shared sound-effect substrate: (1) the **sound registry** (`SOUND(name, path)` table — every
weapon/gametype/item/vehicle/turret/monster/misc cue, with the `CH_*`/`VOL_*`/`ATTEN_*` constants and the
`SND_*_RANDOM()` variant pickers), (2) the low-level **emit primitives** (`sound()`/`sound7`/`soundto`/`soundat`/
`stopsound`/`play2`/`play2team`/`play2all`/`spamsound` + the `sound_allowed` / `bot_sound_monopoly` gate and the
`SVC_SOUND` wire encode), and (3) the **GlobalSound / PlayerSound / VoiceMessage** dispatch in
`globalsound.qc` — the per-model player body sounds (footstep/fall/pain/death/jump/gasp), the team-radio /
taunt voice messages, and their VOICETYPE recipient routing + directional/taunt/gentle gates. It is consumed by
nearly every other unit; this spec covers the substrate, not each consumer's individual cue (those live in the
per-weapon / per-gametype / damage specs).

## Base algorithm (authoritative)

### Sound registry + constants (`sound.qh`, `all.inc`)
- `REGISTRY(Sounds, BITS(9))`; `SOUND(name, path)` registers a `CLASS(Sound)` with a bare `sound_str` path.
  `SND_<name>` is the descriptor; `SND(id)` = `Sound_fixpath` to the resolved file.
- **Path resolution** `_Sound_fixpath`: server appends `.wav` (lets the client engine choose the codec); client
  probes `.wav`/`.ogg`/`.flac` in order, warns "Missing sound" if none. Empty path → `string_null`.
- **Channels** (`sound.qh:6-25`): `CH_INFO=0`, `CH_WEAPON_A/B=-1` (`CH_WEAPON_SINGLE=1`), `CH_VOICE=-2`,
  `CH_TRIGGER=-3` (`_SINGLE=3`), `CH_SHOTS=-4` (`_SINGLE=4`), `CH_TUBA_SINGLE=5`, `CH_PAIN=-6` (`_SINGLE=6`),
  `CH_PLAYER=-7` (`_SINGLE=7`), `CH_BGM_SINGLE=8`, `CH_AMBIENT=-9` (`_SINGLE=9`). **Negative = autochannel** (a
  fresh transient emitter per play → plays STACK); **positive "single" = one slot per (entity, channel)** (a new
  play REPLACES the prior one).
- **Attenuation** (`sound.qh:27-34`): `ATTEN_NONE=0`, `ATTEN_MIN=0.015625`, `ATTEN_LOW=0.2`, `ATTEN_NORM=0.5`,
  `ATTEN_LARGE=1`, `ATTEN_IDLE=2`, `ATTEN_STATIC=3`, `ATTEN_MAX=3.984375`.
- **Volume** (`sound.qh:36-38`): `VOL_BASE=0.7`, `VOL_BASEVOICE=1.0`, `VOL_MUFFLED=0.35`.
- **`sound(e,c,s,v,a)` macro**: SVQC wraps in `sound_allowed(MSG_BROADCAST,e)` then `sound7(...,0,0)`; everything
  routes through `sound7` (not the legacy `sound`) so channels 8-15 stay usable. CSQC calls `sound7` directly.
- **`sound8`**: like `sound7` but with an explicit origin (temporarily moves a zero-size autochannel entity).
- **Random-variant groups** (`all.inc`): `SND_GRENADE_BOUNCE_RANDOM` (1..6), `SND_NEXWHOOSH_RANDOM` (1..3),
  `SND_RIC_RANDOM` (1..3), `SND_FLACEXP_RANDOM` (hagexp 1..3), `SND_GIB_SPLAT_RANDOM` (01..04 via `prandom`).
  Pick via `REGISTRY_GET(Sounds, SND_X1.m_id + rint(random()*N))`. Several aliases via `#define`
  (`SND_ONS_GENERATOR_ALARM`=`SND_KH_ALARM`, `SND_NADE_BONUS`=`SND_KH_ALARM`, `SND_NADE_NAPALM_*`=`SND_FIREBALL_*`).
- Per-team CTF cues: `SND_CTF_{CAPTURE,DROPPED,RETURNED,TAKEN}(teamid)` switch over NUM_TEAM_1..4 → red/blue/
  yellow/pink, else neutral.

### Emit primitives + the sound gate (`all.qc`)
- **`sound_allowed(to, e)`**: walk `e` up through `body→enemy`, `realowner`, `owner`; sounds to self
  (`to==MSG_ONE && e==msg_entity`) always pass; if `bot_sound_monopoly` and `e` is a real client → **deny**;
  else pass. `bot_sound_monopoly` default **0** (`xonotic-server.cfg:489`).
- **`soundtoat`**: the `SVC_SOUND` wire encoder — quantize `atten*64`, `vol*255`, `speed=pitch*0.01*4000`; set
  `SND_VOLUME`/`SND_ATTENUATION`/`SND_LARGEENTITY` (entno≥8192 or chan outside 0..7) / `SND_LARGESOUND`
  (idx≥256) / `SND_SPEEDUSHORT4000`; write entno+chan, sample idx, and the world coord.
- **`soundto`** = `soundtoat` at the entity box center. **`soundat`** = `soundtoat` to `MSG_ALL`/`MSG_BROADCAST`
  depending on `chan&8`. **`stopsound(e,chan)`** = `SVC_STOPSOUND` (or `SND_Null` for large ents), sent both
  unreliable + reliable.
- **`play2(e,file)`**: `soundtoat(MSG_ONE, NULL, …, CH_INFO, file, VOL_BASE, ATTEN_NONE)` — a local 2D sound to
  one client. **`play2team(t,file)`** → `play2` to every real client on team t. **`play2all(samp)`** →
  `_sound(NULL, CH_INFO, samp, VOL_BASE, ATTEN_NONE)` broadcast. All three early-out under `bot_sound_monopoly`.
- **`spamsound(e,chan,samp,vol,atten)`**: like `sound()` but rate-limited by `e.spamtime` (≤ once per `time`
  step) — for touch handlers that fire many times per frame.

### GlobalSound / PlayerSound / VoiceMessage (`globalsound.qh/.qc`)
- Two registries: `PlayerSounds` (`REGISTER_PLAYERSOUND` ids = death/drown/fall/falling/gasp/jump/
  pain25/50/75/100; plus `REGISTER_VOICEMSG` ids flagged `instanceOfVoiceMessage`) and `GlobalSounds`
  (`REGISTER_GLOBALSOUND(id,"base count")` = STEP/STEP_METAL "misc/footstep0 6"/"misc/metalfootstep0 6",
  FALL/FALL_METAL "misc/hitground 4"/"misc/metalhitground 4").
- **VOICETYPE** (`globalsound.qh:64-69`): `PLAYERSOUND=10`, `TEAMRADIO=11`, `LASTATTACKER=12`,
  `LASTATTACKER_ONLY=13`, `AUTOTAUNT=14`, `TAUNT=15`.
- **`GlobalSound_sample(pair, r)`**: split `"base count"`; if count>0 → `sprintf("%s%d.wav", base, floor(r*n+1))`
  (random numbered variant); else `"%s.wav"`. `PrecacheGlobalSound` precaches every variant.
- **`GlobalSound_pitch(p)`**: a gradient mapping crossing (0,a),(c,1) asymptotic to b, with a=1.5 max, b=0.75 min,
  c=100 — used to convert a per-entity pitch scale (e.g. model `.scale`) into a playback pitch.
- **`_GlobalSound(this, gs, ps, sample, chan, vol, voicetype, fake, pitchscale)`** (`globalsound.qc:341`): the
  dispatcher. `body` classnames don't speak. Pitch from `pitchscale*this.scale` (unless q3compat hitbox-scaled).
  Routes by VOICETYPE:
  - **LASTATTACKER[_ONLY]**: play to `this.pusher` (last attacker) at the directional atten
    (`cl_voice_directional==1 ? ATTEN_MIN : ATTEN_NONE`); LASTATTACKER also plays back to the speaker at
    `VOL_BASE`/`ATTEN_NONE`. `_ONLY` skips the speaker.
  - **TEAMRADIO**: `FOREACH_CLIENT(IS_REAL_CLIENT && SAME_TEAM)` at the directional atten (`fake` → only self).
  - **AUTOTAUNT / TAUNT**: AUTOTAUNT gated on `sv_autotaunt` (default 1); TAUNT plays the taunt anim
    (`animdecide_setaction ANIMACTION_TAUNT`) and is gated on `sv_taunt` (default 1) and suppressed by
    `sv_gentle`. Broadcast to all real clients; AUTOTAUNT additionally rolls `tauntrand < cl_autotaunt`. Atten:
    if `cl_voice_directional>=1` → `bound(ATTEN_MIN, cl_voice_directional_taunt_attenuation, ATTEN_MAX)` else
    `ATTEN_NONE`.
  - **PLAYERSOUND**: `globalsound/playersound(MSG_ALL, …, ATTEN_NORM)` — heard by everyone at the emitter's
    position (`fake` → only self at MSG_ONE).
- **`globalsound`/`playersound`** (the MSG senders): unless `g_debug_globalsounds` (default **false**), emit
  directly via `soundto`/`sound7` (MSG_ONE → soundto, MSG_ALL → sound7); otherwise serialize a NET_TEMP packet
  (`REGISTER_NET_TEMP`) that CSQC replays with `sound7`/`sound8` at the entity (head-height for self).
- **Per-model sounds**: `UpdatePlayerSounds(this)` reloads when model/skin changes: `LoadPlayerSounds` from
  `sound/player/default.sounds` then the model's `<model>.sounds`; each line `key file variants`. CSQC resolves
  `this.(ps.m_playersoundfld)` for the chosen sample at play time.
- **Live callers (Base)**: footsteps/landing `PM_Footsteps`/`PM_check_hitground` (`common/physics/player.qc`,
  gated on `g_footsteps` default 1; muffled landing while ducked); pain/death/fall/drown
  (`server/player.qc:374-469`); jump (`player.qc:495`, walljump, dodging); gasp/teamshoot/autotaunt
  (`server/client.qc`); manual taunt + team radio via `cmd voicemessage` (`server/command/cmd.qc:1041-1062`).

### Cvar / constant defaults (Base)
| cvar | default | side | source |
|---|---|---|---|
| `bot_sound_monopoly` | 0 | authority | xonotic-server.cfg:489 |
| `g_footsteps` | 1 | authority | xonotic-server.cfg:306 |
| `sv_taunt` | 1 | authority | xonotic-server.cfg:8 |
| `sv_autotaunt` | 1 | authority | xonotic-server.cfg:9 |
| `g_debug_globalsounds` | 0 (false) | authority | globalsound.qh:9 |
| `cl_autotaunt` | 0 | presentation (replicated) | xonotic-client.cfg:199 |
| `cl_voice_directional` | 1 | presentation (replicated) | xonotic-client.cfg:200 |
| `cl_voice_directional_taunt_attenuation` | 0.5 | presentation (replicated) | xonotic-client.cfg:201 |
| `VOL_BASE / VOL_BASEVOICE / VOL_MUFFLED` | 0.7 / 1.0 / 0.35 | shared | sound.qh:36-38 |
| `ATTEN_*` (NONE..MAX) | 0 / 0.015625 / 0.2 / 0.5 / 1 / 2 / 3 / 3.984375 | shared | sound.qh:27-34 |

> Note: `sv_taunt`/`sv_autotaunt` ship **1** in Xonotic; the port's `VoiceCvars` doc-comments say "shipped 0",
> which is a comment error (the gate still reads the live cvar, so default behaviour follows whatever the cvar
> service returns).

## Port mapping
| Base feature | Port symbol | Liveness |
|---|---|---|
| `REGISTRY(Sounds)` + `SOUND(name,path)` | `GameSound` + `Sounds` catalog (`Registry<GameSound>`), seeded by `SoundsList.RegisterAll` via `Sounds.RegisterAll` | live (boot) |
| `CH_*` constants | `SoundChannel` enum (Services.cs) + `SoundChannelHint` (GameSound.cs); auto(neg)/single(pos) preserved by `EngineChannel`/`Single` | live |
| `ATTEN_*` / `VOL_*` | `SoundLevels` (GameSound.cs) — all eight attens + three vols, exact | live |
| `_Sound_fixpath` codec probe | `ClientWorld.LoadStream` (resolves `.ogg`/`.wav` from VFS) | live |
| `SND_*_RANDOM` pickers + counted GlobalSound | `SoundVariantGroups` (PickRegistered / ResolveGlobalSample) + `Prandom` | live (footsteps/gibs/ric/bounce) |
| per-team CTF cues | `CTF_{CAPTURE,DROPPED,RETURNED,TAKEN}_{team}` registered; Ctf.cs resolves by team | live |
| `sound(e,…)` / `sound7` / `SV_StartSound` | `ISoundService.Play` → `SoundService.Play` → `SoundEvent` Broadcast → `ServerNet` → `SoundWire`/`SoundBundle` → `ClientNet` → `ClientWorld.OnSound` | live |
| `soundtoat` `SVC_SOUND` wire encode | **replaced** by custom `SoundWire` (sample string + raw float origin/vol/atten + chan + netid + loop/stop/pitch flags) | live (intended divergence) |
| `stopsound` / `loopsound` | `ISoundService.Stop` + `Play(loop:true)` keyed by (netid,channel); client `_singleChannelPlayers` + dedicated loop players | live (Arc beam, vehicles, projectile fly-loops) |
| `play2` / `play2team` | NOT IMPLEMENTED as primitives (no per-client/per-team 2D send) | na |
| `play2all` | `SoundSystem.PlayGlobal*` (shared global emitter at ATTEN_NONE) | live |
| `spamsound` | NOT IMPLEMENTED (callers either call PlayOn directly or are named no-ops, e.g. MonsterAI.cs:1327, VehicleCommon.cs:437) | partial |
| `sound_allowed` / `bot_sound_monopoly` | NOT IMPLEMENTED (no owner-walk gate, no monopoly cvar); a separate `SoundAllowedBroadcast` exists ONLY in DamageSystem for the hit-sound gate | partial |
| `PlayerSounds` registry | `Sounds.PlayerSoundIds` + `PLAYER_*` registered (id SET correct); but `PlayerSoundSample` resolves `sound/player/default.sounds/<id>` — the manifest FILE treated as a directory, a path that does not exist → **all body sounds silent** | broken |
| `GlobalSounds` registry | `STEP/STEP_METAL/FALL/FALL_METAL` + `SoundVariantGroups.GlobalCounts` (6/6/4/4) | live |
| VOICETYPE_* | `VoiceType` enum (values 10..15, exact) | live |
| `REGISTER_VOICEMSG` table | `VoiceMessages._all` (21 entries, ids+VOICETYPE+listed, in QC order) + `VoiceTypeOf` fallback→Taunt | live |
| `_GlobalSound` dispatcher | `SoundSystem._GlobalSound` (LASTATTACKER[_ONLY]/TEAMRADIO/AUTOTAUNT/TAUNT/PLAYERSOUND recipient sets + sv_taunt/sv_autotaunt/sv_gentle gates) | live (voicemessage cmd) |
| `GlobalSound`/`PlayerSound` macros | `SoundSystem.PlayPlayerSound` / `PlayVoiceMessage` / `GlobalSound` | live wiring, but body-voice SILENT (broken sample path) — only footsteps (GlobalSound STEP/STEP_METAL, literal base path) actually sound |
| `GlobalSound_pitch` gradient | NOT IMPLEMENTED (model-scale pitch shift a=1.5/b=0.75/c=100); wire has a flat pitch byte instead | missing |
| `UpdatePlayerSounds` / `LoadPlayerSounds` (.sounds files) | NOT IMPLEMENTED: `PlayerSoundSample(modelDir,id)` does string concat, never parses the `key→file` manifest mapping; `ModelSoundDir` always returns null (no per-model/per-skin reload). This breaks the STOCK default pack too (the manifest indirection is what makes `pain50 → sound/player/espeak/player/pain50` resolvable) | missing |
| `g_debug_globalsounds` NET_TEMP path | NOT IMPLEMENTED (the direct-emit branch is the only path; correct, since the debug path is default-off and was never the live one) | na |
| per-client `cl_voice_directional` / `cl_autotaunt` reads | NOT IMPLEMENTED per-recipient (directional cases use ATTEN_MIN; AUTOTAUNT roll omitted) — documented deviation in `_GlobalSound` | partial |
| DP distance attenuation | `ClientWorld.DpDistanceGain` reproduces DP `SND_Spatialize` (radius 2400, exponent 4, optional decibel); Godot's own model disabled | live (intended divergence — faithful to DP method 1) |

## Parity assessment

### Logic — faithful for the registry + dispatch core; two real holes in the emit primitives
The registry, channel auto/single semantics, the VOICETYPE recipient routing, the random-variant pickers, and the
GlobalSound/PlayerSound/VoiceMessage call surface are a faithful re-implementation. The gaps are in the lower
`sound.qh` primitives:
- **`sound_allowed` / `bot_sound_monopoly` not implemented.** Base routes EVERY emitted sound through
  `sound_allowed`, which (a) re-homes a `body`/`realowner`/`owner` sound to its true owner and (b) silences all
  real-client sounds when `bot_sound_monopoly 1`. The port has no general gate (only DamageSystem has its own
  `SoundAllowedBroadcast` for the hit-sound). So `bot_sound_monopoly` is a dead cvar, and corpse/projectile sounds
  aren't re-homed to the owner. Both are low-impact in default play (monopoly default 0) but are real divergences.
- **`play2` / `play2team` / `spamsound` not implemented as primitives.** `play2all` has a stand-in (PlayGlobal);
  the per-client (`play2`) and per-team (`play2team`) 2D sends, and the per-entity rate-limited `spamsound`, have
  no equivalent. Consumers that wanted `spamsound` (monster body-impacts, vehicle hit) are named no-ops or call
  PlayOn directly without the spam guard, so a touch-spam source could over-emit.

### Values — faithful
All `ATTEN_*`/`VOL_*` constants and the GlobalSound variant counts (STEP/STEP_METAL=6, FALL/FALL_METAL=4) match
exactly. The sample table in SoundsList matches `all.inc` line-for-line including the `#define` aliases. The DP
distance model uses the shipped `snd_soundradius 2400` / `snd_soundsystem` exponent 4 (Xonotic's audio.cfg method
1), read from cvars with the correct fallbacks.

### Timing — faithful
Footstep cadence (`FootstepInterval=0.3`, `FootstepSpeedThreshold=0.6*maxspeed`) reproduces `PM_Footsteps`; the
landing sound fires once per genuine fall (WasFlying latch) — actually a correctness FIX over a naive OnGround edge.
`spamsound`'s `time`-step rate limit has no port equivalent (see logic gap).

### Presentation — strong, with the hit-sound sample bug
- **DP-faithful spatialization** (`ClientWorld.DpDistanceGain` + `SetSpatialVolume`): Godot's inverse-distance
  model is disabled and the gain curve reproduces DarkPlaces `SND_Spatialize` (radius/exponent/decibel), with
  per-frame re-spatialization and emitter-follow (a one-shot tracks its moving emitter by net id). This supersedes
  the older "attenuation tweak radius=2400/exp=4 is an intentional deviation" memory note — it is the *faithful* DP
  curve, not an arbitrary tweak. ATTEN_NONE sounds play centered on the listener (2D), matching DP.
- **Hit-sound sample is wrong (`game/client/HitSound.cs:117,121`)**: loads `misc/hitconfirm` (`.ogg`), but the
  registered/shipped hit sound is `SND(HIT,"misc/hit")`. The shipped file is `sound/misc/hit.wav`; `hitconfirm`
  does not exist → the local hit-confirmation beep is **silent**. (This is the long-standing bug from the
  sound-system memory note; it lives in `cl-view`/hitsound but is anchored to the `HIT` registry entry here.)
- `GlobalSound_pitch` model-scale pitch shift is not ported (no scaled-model squeak/deepen); the wire carries a
  flat pitch byte. Cosmetic.

### Audio — the correct API is called on the live paths
Pain/death/drown/fall (DamageSystem.cs:567,889), footsteps/landing (PlayerPhysics.cs:1644,1663), jump
(DodgingMutator), and the team-radio/taunt voice command (Commands.cs:1682) all reach `Api.Sound.Play` with the
right cue, channel, volume and attenuation. The hit-sound is the one wrong cue (above).

### Liveness
- **LIVE:** the registry (seeded at boot), the `Play`→wire→`OnSound` SV_StartSound path (every networked sound),
  the auto/single + loop/stop channel model (Arc beam, vehicle engines, projectile fly-loops), the
  random-variant pickers (footsteps/gibs/ric/bounce), the `_GlobalSound` VOICETYPE routing logic (reached by the
  `voicemessage` server command with the live real-client roster).
- **LIVE WIRING BUT SILENT:** the `PlayerSound`/`VoiceMessage`/`_GlobalSound` body-voice callers (pain/death/drown/
  taunt/team-radio) — the dispatch runs and the recipients are correct, but the **resolved sample path is wrong**
  (`sound/player/default.sounds/<id>`, a nonexistent path), so no audio is produced. Footsteps (STEP/STEP_METAL via
  `GlobalSound`, which uses a literal base path not the manifest) are the only body-sound family that actually
  sounds.
- **PARTIAL / DEAD:** `bot_sound_monopoly` is a dead cvar (no gate consumes it); `spamsound`'s rate guard and the
  `play2`/`play2team` sends have no live path; the per-recipient `cl_voice_directional`/`cl_autotaunt` reads are
  stubbed to defaults.
- **MISSING / DEAD:** `GlobalSound_pitch` (model-scale pitch), the `.sounds` manifest parse (`LoadPlayerSounds`) +
  `ModelSoundDir` (always null) — these being missing is the ROOT CAUSE of the silent body sounds, not a
  custom-model-only concern; `g_debug_globalsounds` NET_TEMP debug path (moot).

### Intended divergences
- **Wire format**: `SoundWire` replaces the bit-packed `SVC_SOUND` message (DP/QW protocol) with a self-describing
  record (sample string + raw floats). Rationale: the port has its own snapshot protocol; reusing DP's quantized
  `idx`/flag encoding would require the engine sound-index table. Behaviorally equivalent (volume/atten/channel/
  origin/loop/stop/pitch all carried).
- **DP distance attenuation in C#** rather than via Godot's audio node: reproduces DP's exact falloff curve;
  documented in `ClientWorld.DpDistanceGain`.

## Verification
- Code-read: full `sound.qh`/`all.qc`/`all.inc`/`globalsound.qh`/`globalsound.qc` vs the five port Sounds files +
  Services.cs + EngineServices.cs + SoundWire.cs + ClientWorld.cs sound section + HitSound.cs.
- Liveness: grepped `Api.Sound.Play` / `SoundSystem.*` / `PlayPlayerSound` / `GlobalSound` call sites — confirmed
  live callers in DamageSystem (pain/death:567,889), PlayerPhysics (footstep/fall:1644,1663), DodgingMutator
  (jump:306), Commands (voicemessage→_GlobalSound:1682), and the SoundEvent→ServerNet→SoundWire→ClientNet→OnSound
  chain.
- Values: diffed `ATTEN_*`/`VOL_*`/`CH_*` and the variant counts against `sound.qh`/`globalsound.qh`; diffed the
  `all.inc` SOUND table against SoundsList.cs.
- Cvar defaults: confirmed `g_footsteps 1`, `sv_taunt 1`, `sv_autotaunt 1`, `bot_sound_monopoly 0`,
  `cl_voice_directional 1` etc. in the Base `.cfg` files.
- Hit-sound bug: confirmed `HitSound.cs` loads `misc/hitconfirm` while the registry/file is `misc/hit`
  (`Base/data/xonotic-data.pk3dir/sound/misc/hit.wav` exists; `hitconfirm` does not).
- **Body-sound path bug (NEW, more severe than draft):** `Sounds.PlayerSoundSample(null, "pain50")` →
  `sound/player/default.sounds/pain50`. `default.sounds` is a manifest FILE (verified: not a directory), so
  `AssetLoader` probes `sound/player/default.sounds/pain50.ogg/.wav` and finds nothing. The real file is
  `sound/player/espeak/player/pain50.ogg` (verified present), reachable only by parsing the `key→file` line in
  `sound/player/default.sounds`. ⇒ all `PlayerSound`/`VoiceMessage`/`_GlobalSound` body sounds are silent. The draft
  rated these `live`/`faithful audio`; corrected to broken.
- NOT runtime-verified: actual audibility of each cue in-game, the DP falloff curve vs DP side-by-side, and the
  per-team CTF cue selection live.

## Open questions
- Should `bot_sound_monopoly` and `sound_allowed`'s owner-walk be implemented, or are they intentionally dropped
  (bot-only-noise is a niche feature; owner re-homing matters for corpse/projectile sounds attributed to a player)?
- Is the missing `spamsound` rate-limit causing any observable over-emission (monster body-impacts, repeated touch
  triggers), or are all such callers already debounced upstream?
- Is the hit-sound `misc/hitconfirm`→`misc/hit` fix in scope for this unit or owned by `cl-view`? (The wrong path
  is in HitSound.cs but the correct target is the `HIT` registry entry.)
- Are `play2`/`play2team` (per-client/per-team 2D sounds) needed by any current consumer, or is `play2all` enough?
