# fx-deathtypes — parity spec

**Base refs:** `common/deathtypes/all.{qh,qc,inc}` · `server/damage.qc` (Obituary_SpecialDeath / Obituary_WeaponDeath / Obituary) · `common/weapons/weapon/*.qc` (wr_killmessage / wr_suicidemessage) · `common/notifications/all.inc` (DEATH_SELF_* / DEATH_MURDER_* notification rows)
**Port refs:** `src/VortexArena.Common/Gameplay/Damage/DeathTypes.cs` · `src/VortexArena.Common/Gameplay/Notifications/DeathMessages.cs` · `src/VortexArena.Server/Scores.cs` (EmitObituary / BroadcastObituary / SubscribeToDeaths)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
"fx-deathtypes" is the **death-attribution / obituary-message classification layer**. In Base it is the
`Deathtypes` registry (`common/deathtypes/`): a deathtype is a *packed integer* that the damage pipeline
carries from the killing event to the obituary code. The low 8 bits (`DEATH_WEAPONMASK`) are a weapon id;
bits 8–13 are HITTYPE_* flags (SECONDARY/SPLASH/BOUNCE/ARMORPIERCE/SOUND/SPAM); and "special"
(non-weapon) deaths start at `DT_FIRST = BIT(14)` and index the `Deathtypes` registry. Each registry row
(`REGISTER_DEATHTYPE`) maps a special deathtype to up to four notification entities (self / murder /
self-ent / murder-ent) and a `.message` category string (`""`, `"monster"`, `"turret"`, `"vehicle"`).
The unit's job is purely **routing**: given a deathtype, decide *which kill-feed / centerprint / personal
notification to send* and *how to classify it* (weapon vs special, monster/turret/vehicle, armor-pierce,
fire/buff). It does not compute damage; it labels deaths. It is server-authoritative (the obituary is
emitted from `server/damage.qc:Obituary`, broadcast as MSG_INFO/MSG_MULTI/MSG_CENTER notifications).

## Base algorithm (authoritative)

### Deathtype registry + integer packing  (`common/deathtypes/all.qh`)
- **Constants:** `DEATH_WEAPONMASK = BITS(8)` (0xFF); `HITTYPE_SECONDARY = BIT(8)`, `HITTYPE_SPLASH = BIT(9)`
  (auto-set by RadiusDamage), `HITTYPE_BOUNCE = BIT(10)` (set manually after a bounce),
  `HITTYPE_ARMORPIERCE = BIT(11)`, `HITTYPE_SOUND = BIT(12)` ("bleeding from ears"),
  `HITTYPE_SPAM = BIT(13)` (set after first RadiusDamage to stop effect spam); `DT_FIRST = BIT(14)`.
- `REGISTRY(Deathtypes, BITS(8))` — up to 256 special deathtypes, each `m_id += DT_FIRST`.
- `REGISTER_DEATHTYPE(id, msg_death, msg_death_by, msg_death_ent, msg_death_ent_by, extra)` sets
  `m_name = #id`, `.message = extra` (category), and the four message entities
  `death_msgself / death_msgmurder / death_msg_ent_self / death_msg_ent_murder`.
- **Classifiers (macros):** `DEATH_ISSPECIAL(t) = (t >= DT_FIRST)`; `DEATH_IS(t, dt)`; `DEATH_ENT(t)`
  (the registry entity); `DEATH_ISVEHICLE/ISTURRET/ISMONSTER(t)` = string compare on `.message`;
  `DEATH_WEAPONOF(t)` = `WEP_Null` for specials else `Weapons[t & DEATH_WEAPONMASK]`; `DEATH_ISWEAPON(t,w)`.
- `Deathtype_Name(int)` (`all.qc`): `itos(t)` for a non-special, else the registry row's `m_name`.

### Registered special deathtypes  (`common/deathtypes/all.inc`)
62 rows. By `.message` category:
- **`""` (plain env/special):** AUTOTEAMCHANGE, BUFF_INFERNO (murder-only), BUFF_VENGEANCE (murder-only),
  CAMP, CHEAT, CUSTOM, DROWN, FALL, FIRE, GENERIC, HURTTRIGGER (used as DEATH_VOID; has *_ENT variants),
  KILL, LAVA, MIRRORDAMAGE (→ DEATH_SELF_BETRAYAL), NADE, NADE_DARKNESS, NADE_HEAL, NADE_ICE, NADE_NAPALM,
  NOAMMO, ROT, SHOOTING_STAR, SLIME, SWAMP, TEAMCHANGE, TELEFRAG (murder-only), TOUCHEXPLODE, WEAPON.
- **`"monster"`:** MONSTER_MAGE, MONSTER_GOLEM_CLAW/SMASH/ZAP, MONSTER_SPIDER, MONSTER_WYVERN,
  MONSTER_ZOMBIE_JUMP/MELEE. Each has its own `DEATH_SELF_MON_*` self line; **all share
  `DEATH_MURDER_MONSTER`** for the murder line.
- **`"turret"`:** TURRET + 12 per-turret rows (EWHEEL, FLAC, HELLION, HK, MACHINEGUN, MLRS, PHASER, PLASMA,
  TESLA, WALK_GUN, WALK_MELEE, WALK_ROCKET). Each has its own `DEATH_SELF_TURRET_*`; **all share
  `DEATH_MURDER_CHEAT`** for the murder line.
- **`"vehicle"`:** 13 VH_* rows (BUMB_DEATH/GUN, CRUSH, RAPT_BOMB/CANNON/DEATH/FRAGMENT, SPID_DEATH/MINIGUN/
  ROCKET, WAKI_DEATH/GUN/ROCKET). Each carries its **own** self AND murder line; some are NULL (a vehicle
  GUN has murder-only; RAPT_FRAGMENT reuses RAPT_BOMB's lines).

### Obituary dispatch  (`server/damage.qc`)
- **`Obituary(attacker, inflictor, targ, deathtype, .weaponentity)`** — the single entry, called from the
  damage/death path. Branches:
  - **SUICIDE** (`targ == attacker`): if special, TEAMCHANGE/AUTOTEAMCHANGE → SpecialDeath with `f1=team`;
    MIRRORDAMAGE / HURTTRIGGER (with mapper `inflictor.message`) / default → `Obituary_SpecialDeath(...,
    murder=false)`. Else `Obituary_WeaponDeath(murder=false)`. `GiveFrags(-1)` (except AUTOTEAMCHANGE).
  - **MURDER** (`IS_PLAYER(attacker)`): teamkill → fixed `INFO_DEATH_TEAMKILL` (team-keyed) + CENTER
    TEAMKILL_FRAG/FRAGGED, no weapon line, killcount=0. Enemy frag → killstreak announcer (`KILL_SPREE_LIST`),
    first-blood latch (kill_count `-1`/`-2`), typefrag/`frag_centermessage_override` (DEATH_FIRE →
    CHOICE_FRAG_FIRE) / CHOICE_FRAG centerprints, then `Obituary_WeaponDeath(murder=true)` falling back to
    `Obituary_SpecialDeath(murder=true)` (with HURTTRIGGER mapper `inflictor.message2`).
  - **ACCIDENT/TRAP** (no player attacker): HURTTRIGGER (mapper `inflictor.message`), CUSTOM (mapper
    `deathmessage`), or default → `Obituary_SpecialDeath(murder=false)`. `GiveFrags(-1)`; `-5` score →
    ANNCE_ACHIEVEMENT_BOTLIKE.
- **`Obituary_SpecialDeath(...)`** picks `death_message` = (msg_from_ent ? ent-variant : normal) ×
  (murder ? death_msgmurder : death_msgself) off the registry row, then sends MSG_MULTI to the victim +
  MSG_INFO (`nent_msginfo`) to everyone else.
- **`Obituary_WeaponDeath(...)`** sets the global `w_deathtype = deathtype`, calls the weapon's
  `wr_killmessage` / `wr_suicidemessage` (which branch on the HITTYPE bits of `w_deathtype`), and sends the
  same MSG_MULTI + MSG_INFO pair. Returns false when `DEATH_WEAPONOF == WEP_Null` (→ caller falls to special).

### Per-weapon message selection  (`common/weapons/weapon/*.qc`)
Each weapon class owns `wr_killmessage`/`wr_suicidemessage`, branching on `w_deathtype & HITTYPE_*`. Examples:
devastator → `(BOUNCE|SPLASH) ? *_MURDER_SPLASH : *_MURDER_DIRECT`; electro → `SECONDARY ? *_MURDER_ORBS :
(BOUNCE ? *_MURDER_COMBO : *_MURDER_BOLT)`; rifle → 4-way `SECONDARY × BOUNCE`; tuba → `BOUNCE ? KLEINBOTTLE :
(SECONDARY ? ACCORDEON : TUBA)`; hitscan suicides → `WEAPON_THINKING_WITH_PORTALS`.

### Consumers of the classifiers (damage pipeline)
`DEATH_IS(t, DEATH_DROWN) || (t & HITTYPE_ARMORPIERCE)` → bypass armor; `t == DEATH_FIRE/BUFF_*` →
excluded from hit-sound credit; `DEATH_ISMONSTER/ISTURRET/ISVEHICLE` → obituary phrasing + scoring side.

## Port mapping
The port models the deathtype as a **plain string tag** (`DamageInfo.DeathType`), not a packed int:
- weapon kills → `"weapon/<NetName>"` (`DeathTypes.FromWeapon`); HITTYPE flags are appended as `"|flag"`
  suffix tokens (`WithHitType`/`HasHitType`); specials are bare lower-case names (`"fall"`, `"void"`, …).
- `DeathTypes.cs` reproduces the classifiers: `IsWeapon`/`IsSpecial`, `WeaponNetNameOf`, `BaseOf`,
  `HasHitType`/`WithHitType`, `BypassesArmor`, `IsFireOrBuff`, `IsAlwaysLethal`, `IsTeamChange`, and a
  `_registry` dictionary that ports the **monster/turret/vehicle** rows (name → DeathTypeDef with
  category + self/murder notif names), exposing `Lookup`/`CategoryOf`/`IsMonster`/`IsTurret`/`IsVehicle`.
- `DeathMessages.cs` houses the per-weapon `wr_killmessage`/`wr_suicidemessage` branches centrally
  (`SelectKillMessage`/`SelectSuicideMessage`, keyed on NetName + HITTYPE suffixes, copied 1:1 from each
  weapon .qc) plus `SelectSpecial` (the `Obituary_SpecialDeath` name mapping). `EnsureChoiceArgCounts`
  back-fills MSG_CHOICE/MSG_MULTI arg counts.
- `Scores.EmitObituary` (`Scores.cs`) is the port's `Obituary`: SUICIDE/ACCIDENT vs TEAMKILL vs ENEMY-FRAG
  branches, killstreak announcer, first-blood latch, typefrag/fire centerprints, then the weapon-or-special
  message send via `BroadcastObituary` (MSG_MULTI to victim + MSG_INFO to all-except). Wired live from
  `GameWorld.cs:492 Scores.SubscribeToDeaths` → hooks `Combat.Death`.

## Parity assessment

### Liveness — LIVE
`GameWorld.cs:492` calls `Scores.SubscribeToDeaths`, which hooks `Combat.Death` → `EmitObituary` →
`DeathMessages.Select*` + `DeathTypes.*`. Every kill in a normal match flows through here. `DeathTypes`
classifiers are also consumed live by the damage pipeline (armor bypass, fire/buff hit-sound exclusion),
nades (`NadesMutator.DeathIsNade`), and monster/turret/vehicle attack call sites that tag their damage.
Confirmed by `ObituaryEmissionTests` / `MonsterTurretVehicleObituaryTests` exercising the full send path.

### Faithful dimensions
- **Integer packing → string tag (INTENDED DIVERGENCE).** The port deliberately replaces the QC packed-int
  deathtype with a string tag + suffix tokens. Behaviorally equivalent for routing (weapon id, HITTYPE bits,
  special classification all preserved); only the wire encoding differs. Documented at length in DeathTypes.cs.
- **Per-weapon kill/suicide message selection.** `SelectKillMessage`/`SelectSuicideMessage` copy every
  weapon's HITTYPE branch verbatim (16 weapons incl. arc/devastator/electro/rifle/tuba multi-way). Faithful.
- **Monster/turret/vehicle classification + message routing.** The `_registry` ports all 8 monster, 13
  turret, 13 vehicle rows with the correct shared-vs-per-entity self/murder names (monster→DEATH_MURDER_MONSTER,
  turret→DEATH_MURDER_CHEAT, vehicle→per-vehicle, NULL→generic fallback). Tested.
- **Basic environment specials.** FALL, DROWN, LAVA, SLIME, SWAMP, VOID, FIRE, TELEFRAG (murder-only),
  BUFF_INFERNO/VENGEANCE, NOAMMO (self-only), KILL→generic, MIRRORDAMAGE→generic. Faithful.
- **Classifier consumers.** `BypassesArmor` (DROWN || ARMORPIERCE), `IsFireOrBuff`, `IsAlwaysLethal`,
  `IsTeamChange` match the QC predicates exactly.
- **HITTYPE flag set.** All six flags present as suffix tokens with correct semantics.

### Gaps
1. **`SelectSpecial` is missing the NADE family + several plain-`""` specials.** Its flat switch covers only
   fall/drown/lava/slime/swamp/void/fire/telefrag/buff/noammo/kill/mirrordamage. It does **not** handle
   `nade`, `nade_napalm`, `nade_ice`, `nade_heal`, `nade_darkness`, `cheat`, `camp`, `rot`, `shooting_star`,
   `touchexplode` — all of which fall through to the generic `DEATH_SELF_GENERIC` / `DEATH_MURDER_FRAG` line.
   This is **live and observable**: the port's nade booms emit `nade`/`nade_*` deathtypes
   (`NadeProjectile`/`Booms/*`), so a nade kill prints "X died" / generic frag instead of "X was blown up by
   Y's Nade". The matching notification rows DO exist in `NotificationsList.cs` (DEATH_MURDER_NADE, etc.) —
   only the selector is missing the branches. CAMP/ROT/CHEAT/SHOOTING_STAR/TOUCHEXPLODE are lower-impact
   (few/no live emitters in the port today) but the mapping is still absent.
2. **`MIRRORDAMAGE` self line uses GENERIC, not the Base `DEATH_SELF_BETRAYAL` — and this is LIVE.** Base registers
   MIRRORDAMAGE → `DEATH_SELF_BETRAYAL` ("you were betrayed"); the port maps it to `DEATH_SELF_GENERIC`. Reflected
   teamdamage IS emitted live (`DamageSystem.cs:260`), so the wrong text is observable, not latent. (The
   DEATH_SELF_BETRAYAL row exists in the port's notification list, unused.)
3. **`KILL` (/kill suicide) maps to GENERIC instead of `DEATH_SELF_SUICIDE` — also LIVE.** Base's DEATH_KILL row
   registers `DEATH_SELF_SUICIDE` ("couldn't take it anymore"); the port routes /kill to `DEATH_SELF_GENERIC`
   ("died"). The `/kill` command emits `DeathTypes.Kill` live (`Commands.cs:1135`), so this is observable on every
   manual suicide. Base also suppresses the /kill obituary entirely in CTS (`g_cts && DEATH_KILL → return`); the
   port has no such gate. (The DEATH_SELF_SUICIDE row exists in the port, unused by the selector.)
4. **`TEAMCHANGE`/`AUTOTEAMCHANGE` self obituary not selected — LIVE (new gap).** Base routes these specially in the
   SUICIDE branch (`Obituary_SpecialDeath` with `f1=targ.team`) to `DEATH_SELF_TEAMCHANGE`/`DEATH_SELF_AUTOTEAMCHANGE`
   and SKIPS `GiveFrags(-1)` for AUTOTEAMCHANGE. The port's `KillPlayerForTeamChange` force-kills through
   `DeathTypes.AutoTeamChange` (`Teamplay.cs:225`, LIVE on every team change), but `SelectSpecial` has no
   teamchange/autoteamchange case → prints `DEATH_SELF_GENERIC`, and `EmitObituary` never passes the `death_team`
   float arg those rows expect. (The frag-negation skip IS handled at `Scores.cs:103`; only the obituary self-line
   + team arg are missing.) The notif rows (DEATH_SELF_TEAMCHANGE/AUTOTEAMCHANGE, Info+Center+Multi) exist, unused.
   Note the port consolidated manual teamchange onto the AUTOTEAMCHANGE deathtype, so DEATH_TEAMCHANGE has no live emitter.
5. **HURTTRIGGER mapper-custom obituary + DEATH_CUSTOM not wired.** Base lets a `trigger_hurt`/mapper supply
   `inflictor.message`/`message2` and `DEATH_CUSTOM` supply `deathmessage`, producing the `DEATH_*_VOID_ENT`
   / `DEATH_SELF_CUSTOM` lines (via `Obituary_SpecialDeath`'s `msg_from_ent ? death_msg_ent_* : death_msg*`
   selection) with a mapper-authored string. `EmitObituary`/`SelectSpecial` have no `msg_from_ent` ENT-variant
   path — a void/hurt-trigger death always uses the fixed VOID line, ignoring any custom mapper message. There is
   no `DEATH_CUSTOM` deathtype tag in the port at all. The notification rows (DEATH_*_VOID_ENT, DEATH_SELF_CUSTOM)
   exist in the port but are never selected. Niche (mapper feature).
6. **ACCIDENT-path `ANNCE_ACHIEVEMENT_BOTLIKE` not emitted.** Base announces BOTLIKE when an accident drives the
   victim's score to -5 (`server/damage.qc:467`); `EmitObituary` has no such announce. Minor. (Covered by the
   `special.suicide_special_routing` row, which also notes the SUICIDE/ACCIDENT branch collapse.)
7. **`Deathtype_Name` (int → name) has no port equivalent.** Base exposes `Deathtype_Name` for logging/debug
   (`itos` or `m_name`). The port's `DeathType` string *is* its own name, so this is N/A for routing, but
   there's no exact analogue (GameLog uses the raw tag). Not a behavioral gap.

### Intended divergences
- **String-tag deathtype model** instead of the packed integer (weapon-mask + HITTYPE bits + DT_FIRST
  registry index). Rationale: the port carries the deathtype as `DamageInfo.DeathType` (a string) end-to-end;
  the bit math is reproduced via `HasHitType`/`BaseOf`/`IsWeapon`. Equivalent routing behavior.
- **Centralized weapon message selection** (`DeathMessages.SelectKillMessage`) instead of per-weapon-class
  `wr_killmessage` METHODs. Rationale: weapon classes are owned by another subsystem and must not carry
  message logic; the branches are copied 1:1 with per-line QC citations.

## Verification
- **Liveness:** traced `GameWorld.cs:492 → SubscribeToDeaths → Combat.Death → EmitObituary → DeathMessages.*`;
  confirmed.
- **Tests (port):** `ObituaryEmissionTests.SelectSpecial_Maps_Self_And_Murder_Families` (fall/drown/telefrag),
  `SelectSpecial_Monster_Turret_Vehicle_UseCategoryRegisteredMessages`, `Suicide_By_Fall_Emits_DeathSelfFall`,
  `SelectKillMessage_Unknown_Weapon_Falls_Back_To_Generic_Frag`; `MonsterTurretVehicleObituaryTests`. These
  cover the faithful paths; **no test exercises nade/cheat/camp/rot/shooting_star/touchexplode through
  SelectSpecial** — the gap is unguarded.
- **Value diff (Base vs port):** registry rows cross-checked against `common/deathtypes/all.inc`
  (monster/turret/vehicle self+murder names match; vehicle NULL self lines match) and notification strings
  against `common/notifications/all.inc`.
- **HITTYPE constants:** bit values not range-checked at runtime (string scheme), but the six flags and their
  semantics match `all.qh` by inspection.

## Open questions
- Are CAMP / ROT / SHOOTING_STAR / TOUCHEXPLODE deathtypes ever *emitted* on the live port path (e.g. does the
  port have camp-check kills, rot ticks, a shooting-star kill volume)? If not, those SelectSpecial branches are
  latent (still a faithfulness gap, but unobservable today). The NADE-family gap *is* observable now.
- Does any port `trigger_hurt` carry a mapper `message`/`message2` that should drive DEATH_*_VOID_ENT? (mapobject
  unit territory — flagged here because the obituary selector is the consumer.)
