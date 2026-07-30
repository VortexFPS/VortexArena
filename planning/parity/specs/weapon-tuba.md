# @!#%'n Tuba — parity spec

**Base refs:** `common/weapons/weapon/tuba.qc` · `common/weapons/weapon/tuba.qh` · `bal-wep-xonotic.cfg` (`g_balance_tuba_*`) · `common/weapons/calculations.qc` / `projectiles.qh` (shared math) · `server/damage.qc:RadiusDamage`
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Tuba.cs` · `WeaponFireDriver.cs` · `WeaponFireGate.cs` · `WeaponSplash.cs` · `Notifications/DeathMessages.cs` · `Notifications/NotificationsList.cs` · `Effects/EffectsList.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The @!#%'n Tuba is a hidden (`WEP_FLAG_HIDDEN`), splash-type, infinite-ammo melee/close-range weapon. Holding fire "plays a note": each refire tick it applies a small radius blast centered on the player (damaging + lightly knocking back nearby enemies) and emits a sustained, pitched loop sound on the client. The note's musical pitch is chosen from the movement keys held; secondary fire plays a higher pitch (+7 semitones). Pressing reload cycles the instrument through Tuba → Accordion → Klein Bottle, which changes the view model, the sound set, and the obituary text. There is also a hidden "melody recognition" facility (`W_Tuba_HasPlayed`) used by map magic-ear triggers to detect that a player played a specific tune. It is a `WEP_FLAG_NODUAL | WEP_FLAG_NOTRUEAIM` weapon (no dual-wield, no true-aim crosshair).

## Base algorithm (authoritative)

### Fire loop — note on/refresh  (`tuba.qc:wr_think`, `W_Tuba_NoteOn`)
- **Trigger / entry:** `wr_think(fire)` runs every server frame for the active weapon. `fire & 1` → primary, `fire & 2` → secondary. Each is gated by `weapon_prepareattack(..., refire)`.
- **Algorithm:**
  1. `W_Tuba_NoteOn(actor, weaponentity, hittype)`:
     - `n = W_Tuba_GetNote(actor, hittype)` (pitch, see below).
     - Build `hittype = HITTYPE_SOUND`; `if instrument & 1: |= HITTYPE_SECONDARY`; `if instrument & 2: |= HITTYPE_BOUNCE`. (So the *instrument*, not the fire button, drives the death-type bits.)
     - `W_SetupShot(...)` with damage = `WEP_CVAR(damage)`, deathtype = `hittype | WEP_TUBA.m_id`.
     - If a note entity already exists and its `cnt != n` or `instrument` changed → `W_Tuba_NoteOff` the old one.
     - If no note entity, spawn `tuba_note`: `owner=realowner=actor`, `cnt=n`, `tuba_instrument=actor.instrument`, `think=W_Tuba_NoteThink` (nextthink=time), `spawnshieldtime=time`, `Net_LinkEntity(W_Tuba_NoteSendEntity)`.
     - `note.teleport_time = time + refire * 2 * W_WeaponRateFactor(actor)` — keep-alive past the next refire so a held note doesn't gap.
     - `RadiusDamage(actor, actor, damage, edgedamage, radius, NULL, NULL, force, hittype|m_id, weaponentity, NULL)` — the blast, centered on the player.
     - Smoke ring throttled by `tuba_smoketime` (every 0.25s): `Send_Effect(EFFECT_SMOKE_RING, org + offset, v_up*100, 1)`, offset varying by instrument.
  2. After `W_Tuba_NoteOn`, `weapon_thinkf(WFRAME_IDLE, animtime, w_ready)`.
  3. If a note exists and neither fire bit is held → `W_Tuba_NoteOff`.
- **Constants:** `damage 5`, `edgedamage 0`, `force 40`, `radius 200`, `refire 0.05`, `animtime 0.05`, `attenuation 0.5`. Smoke throttle `0.25 s`.

### Pitch selection  (`tuba.qc:W_Tuba_GetNote`)
- Movement keys map to a 3×3 grid (`movestate` 1..9). Base note table: 1→-6, 2→-5, 3→-4, 4→+5, 5→0, 6→+2, 7→+3, 8→+4, 9→-1.
- `+crouch → -12`, `+jump → +12`, `+HITTYPE_SECONDARY → +7`.
- Team/player tuning: in teamplay, team 2 or 4 → `+3`; else `clientcolors & 1 → +3`. ("Eb vs C" tuba, plugs holes in the range.)
- Resulting range roughly -18..+27 (TUBA_MIN -18, TUBA_MAX 27 in CSQC).

### Note-off + melody log  (`tuba.qc:W_Tuba_NoteOff`)
- Records the just-ended note into a ring buffer `tuba_lastnotes[MAX_TUBANOTES=32]` as `vec3(on=spawnshieldtime, off=time, pitch=cnt)`, advances `tuba_lastnotes_last`, bumps `tuba_lastnotes_cnt` (capped 32).
- Runs `trigger_magicear_processmessage_forallears` — if a magic-ear matched, `bprint`s "* NAME played on the @!#%'n Tuba/Accordeon/Klein Bottle: ...".
- `delete(note)`.

### Melody recognition  (`tuba.qc:W_Tuba_HasPlayed`)
- Used by map logic (magic-ear) to test whether the last N notes form a given melody, optionally ignoring pitch/instrument and within a tempo window (`mintempo`/`maxtempo`). Pure note-buffer + rhythm-line math; clears `tuba_lastnotes_cnt` on success.

### Instrument cycle (reload)  (`tuba.qc:wr_reload`)
- Only when `state == WS_READY`. Cycles `tuba_instrument` 0→1→2→0 and sets `weaponname` to "tuba"/"akordeon"/"kleinbottle".
- Computes `hittype` from new instrument bits, `W_SetupShot(damage 0)`, `Send_Effect(EFFECT_TELEPORT, w_shotorg)`, sets `state = WS_INUSE`, `weapon_thinkf(WFRAME_RELOAD, 0.5, w_ready)`.

### Note networking + client sound  (`tuba.qc` CSQC: `W_Tuba_NoteSendEntity`, `Ent_TubaNote_*`, `tubasound`, `PRECACHE`)
- Server links a `tuba_note` entity per active note (one per player weapon), gated by `sound_allowed`. SendFlags 1 = note/instrument/attenuate, 2 = origin (re-sent from `W_Tuba_NoteThink` only when a listener's volume changes >0.5% or angle >2°).
- Client spawns a `tuba_note` sound entity (+ a second for pitch-blending), loads the per-note loop sample `TUBA_STARTNOTE(instrument, n)` = `tubaN_loopnoteM`, plays via `sound7`/`_sound` on `CH_TUBA_SINGLE`.
- Pitch stepping (`cl_tuba_pitchstep 6`, default): blends two adjacent recorded loop samples with speed = `2^(m/12)` and cos/sin crossfade volumes; falls back to plain `_sound` when pitchstep is 0 or unsupported.
- Fade-out on note-off over `cl_tuba_fadetime 0.25 s`; volume `VOL_BASE * cl_tuba_volume`, attenuation `tuba_attenuate * cl_tuba_attenuation` (`cl_tuba_attenuation 0.5`).
- Server-side `W_Tuba_NoteThink` also drives the **distance attenuation** model for the blast-less audio: `dist_mult = attenuation / snd_soundradius`, per-listener `vol = max(0, 1 - dist*dist_mult)`.

### Bot aim  (`tuba.qc:wr_aim`)
- If enemy within `radius`, randomly press primary or secondary. (Bots "can't play the tuba well yet".)

### Identity / metadata  (`tuba.qh`)
- impulse 1, `WEP_FLAG_HIDDEN | WEP_TYPE_SPLASH | WEP_FLAG_NODUAL | WEP_FLAG_NOTRUEAIM`, color `'0.909 0.816 0.345'`, infinite ammo, view model `h_tuba.iqm` (per-instrument `h_akordeon`/`h_kleinbottle`), world `v_tuba.md3`, item `g_tuba.md3`, crosshair `gfx/crosshairtuba`.
- Kill/suicide messages: BOUNCE → KLEINBOTTLE, else SECONDARY → ACCORDEON, else TUBA.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `wr_think` fire loop | `Tuba.WrThink` (driven every tick by `WeaponFireDriver`) | live, partial |
| `W_Tuba_NoteOn` blast | `Tuba.NoteOn` → `WeaponSplash.RadiusDamage` | live, values faithful, deathtype bits MISSING |
| note entity / keep-alive | `Tuba.NoteOn` spawns `tuba_note`, sets `MaxHealth=time+refire*2` | present but inert (no consumer/think) |
| `W_Tuba_GetNote` pitch | `Tuba.GetNote` (velocity-derived) | partial (cosmetic-only) |
| `W_Tuba_NoteOff` | `Tuba.NoteOff` (just deletes entity) | partial (no lastnotes buffer, no magicear) |
| `W_Tuba_HasPlayed` melody | NOT IMPLEMENTED | missing |
| `wr_reload` instrument cycle | `Tuba.Reload` (exists) — **NOT CALLED** | dead |
| smoke ring fx | EFFECT_SMOKE_RING registered; **never emitted by Tuba** | missing |
| client note sound (sound7/pitchstep/fade/attenuation) | NOT IMPLEMENTED; single `tuba_loopnote.wav` per blast | missing/stub |
| note networking (ENT_CLIENT_TUBANOTE) | NOT IMPLEMENTED | missing |
| `wr_aim` bot fire | NOT IMPLEMENTED | missing |
| identity/flags/color/models | `Tuba` ctor + `WeaponOrder.cs:209` | live, faithful |
| kill/suicide notifications | `DeathMessages.cs:94/168`, `NotificationsList.cs` (all 6) | wired but Accordion/Klein branches unreachable |

## Parity assessment

### Gaps (player-observable)
0. **Tuba damages teammates (missing `HITTYPE_SOUND`).** Base sets the blast deathtype to `HITTYPE_SOUND | m_id` so the tuba is a *sound* attack that never hurts teammates (the same-team sound rule lives in the port too — `DamageSystem.cs:94-97` returns 0 damage for a sound/hook hit on a teammate). But `Tuba.NoteOn` passes `deathType = RegistryId` (an int), and the blast path `WeaponSplash.RadiusDamage → WeaponFiring.ApplyDamage` maps that id to the bare string `weapon/tuba` with **no HITTYPE suffix at all**. So in team modes the port's tuba blast inflicts friendly fire that Base explicitly suppresses — a genuine gameplay-logic divergence the first pass missed (it had `note_blast` as `logic: faithful`).
1. **Instrument never changes.** `Tuba.Reload` is a plain `public void Reload`, not an override of `WrReload`, and nothing calls it: the reload impulse path (`WeaponImpulses.ReloadHandle`) calls `w.WrReload(...)`, which dispatches to the base generic reload. The base `WrReload` early-returns because Tuba lacks `WeaponFlags.Reloadable`. Net effect: pressing reload does nothing — you can never play the Accordion or Klein Bottle. View model, sound set, and obituary always stay "Tuba". (Even if wired, `Tuba.Reload` is itself unfaithful: no `WS_READY` guard, no `weaponname`, plays `misc/teleport.wav` instead of `Send_Effect(EFFECT_TELEPORT)`, and skips the `WS_INUSE`/`WFRAME_RELOAD 0.5s` lock.)
2. **Obituary is permanently `WEAPON_TUBA_*`** — the int-deathtype path drops **every** HITTYPE bit, not just instrument bits. `DeathMessages` selects ACCORDEON/KLEINBOTTLE from `sec`/`bounce` bits that the `weapon/tuba` tag never carries. Crucially, in Base **secondary fire** ORs `HITTYPE_SECONDARY` in `wr_think` (independent of the instrument), so a plain-Tuba secondary kill should read ACCORDEON — the port reads TUBA there too. So the first pass's framing ("only instrument bits, and secondary plain-tuba correctly yields TUBA") was wrong on the secondary-fire case; the real defect is structural (the int channel can't encode any HITTYPE bit), and it is doubly dead because the instrument cycle (reload) never runs.
3. **No tuba audio — and the chosen asset does not exist.** The sustained, pitched, fading client note (the entire point of the weapon's feel) is absent. `Tuba.NoteOn` plays `weapons/tuba_loopnote.wav` once per blast tick (~every 0.05s) — but **there is no `tuba_loopnote.wav` (and no unsuffixed `tuba_loopnote.ogg`) in the port's data**. The real samples are per-pitch `tuba_loopnote0/6/12/18/24/-6/-12/-18.ogg` (plus `tuba1_*`/`tuba2_*` per instrument). The base unsuffixed name matches nothing, so the call almost certainly plays silence — not even a working static placeholder. No pitch, no `sound7` crossfade, no fade-out, no per-listener distance attenuation; `cl_tuba_volume/attenuation/fadetime/pitchstep` are not honored; no ENT_CLIENT_TUBANOTE.
4. **No smoke ring.** Each note's `EFFECT_SMOKE_RING` puff (every 0.25s, offset by instrument) is never emitted, although the effect is registered.
5. **No melody recognition / magic-ear chat.** `W_Tuba_HasPlayed` and the `tuba_lastnotes` ring buffer + `bprint` "played on the … Tuba: …" are not ported. Map magic-ear tune triggers won't fire; no chat line on playing a recognized melody.
6. **Bots don't use the tuba** (`wr_aim` missing). Bots holding the tuba won't fire it at nearby enemies.
7. **Pitch model divergence** (intended stand-in): `GetNote` derives the movestate from velocity vs facing instead of the actual movement-key bits, and drops the crouch(-12)/jump(+12)/team-tuning(+3) shifts. Since pitch only feeds the (unported) sound, this is presently inconsequential — but it would need the real input bits to be faithful once audio lands.

### Liveness
- **Live:** `WrThink` is invoked every server tick by `WeaponFireDriver` for the active weapon; the radius blast (`RadiusDamage`) therefore fires on the real match path. Weapon is reachable (impulse 1, present in `WeaponOrder`). Damage/force/radius/refire values match Base.
- **Dead:** `Tuba.Reload` (instrument cycle) — no caller. The spawned `tuba_note` entity's keep-alive `MaxHealth`/teleport_time is set but never read (no think), so the entity is an inert marker that `NoteOff` deletes on release.
- **Missing:** audio, smoke fx, melody/magic-ear, note networking, bot aim.

### Intended divergences
- `GetNote` velocity-derived pitch is a deliberate headless stand-in (documented in code) since key bits aren't carried to the weapon; flagged so it isn't re-reported. (Currently cosmetic because audio is unported.)

## Verification
- **Reload-dead:** read `WeaponImpulses.ReloadHandle` (calls `WrReload`), `WeaponFireGate.WrReload` (base; early-returns without `Reloadable`), `Tuba` ctor SpawnFlags (no `Reloadable`), `Tuba.Reload` (non-override, no callers found via grep). Result: dead. (high)
- **Deathtype-bits-missing:** read `Tuba.NoteOn` (`deathType = RegistryId`, no HITTYPE OR), `DeathMessages.cs:40,94,168` (selects on `bounce`/`sec`). Result: Accordion/Klein unreachable. (high)
- **Values:** `Tuba.Configure()` vs `bal-wep-xonotic.cfg:572-584` — damage 5 / edgedamage 0 / force 40 / radius 200 / refire 0.05 / animtime 0.05 / attenuation 0.5 all match. (high)
- **Liveness of blast:** `WeaponFireDriver` calls `WrThink(Primary)` every tick; `NoteOn`→`RadiusDamage`. (high)
- **Audio/smoke/melody/networking/bot:** grep across `src`, `game` — no tuba sound networking, no `Send_Effect(SMOKE_RING)` from Tuba, no `W_Tuba_HasPlayed`/lastnotes, no `wr_aim`. Result: missing. (high)
- No tuba unit tests exist (grep `tests/*.cs`). (high)

### Also missing (added on verify)
8. **`wr_setup` instrument reset not ported.** Base `wr_setup` sets `tuba_instrument = 0` on every (re)equip; `Tuba` has no `WrSetup` override (the driver calls `newwep.WrSetup`, `WeaponFireDriver.cs:273`, and other weapons use it). Currently inconsequential because the instrument never leaves 0 (reload is dead), but a latent bug once cycling is wired.

## Open questions
- Does any shipped map actually rely on a tuba magic-ear melody trigger? (Affects priority of the `W_Tuba_HasPlayed` gap — it's mechanically required for those maps to behave.)
- ~~Confirm the `tuba_loopnote.wav` asset exists.~~ **Resolved (verify):** it does **not**. `find assets -iname 'tuba_loopnote.wav'` returns nothing; only per-pitch `tuba*_loopnote<N>.ogg` exist. The port references an unsuffixed `weapons/tuba_loopnote.wav` that matches no shipped sample, so the per-blast sound is almost certainly silent. (Exact host resolver fallback for a missing name was not observed at runtime — high-confidence-silent, not proven.)
