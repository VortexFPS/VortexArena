# Networked entity client-state — parity spec

**Base refs:** `common/ent_cs.qc` · `common/wepent.qc` · `common/replicate.qc` · `lib/csqcmodel/{sv_model.qc,cl_model.qc,interpolate.qc,common.qh}` · `common/csqcmodel_settings.qh` · `client/csqcmodel_hooks.qc`
**Port refs:** `src/VortexArena.Net/{NetEntity.cs,SnapshotDelta.cs,SnapshotInterpolation.cs}` · `src/VortexArena.Engine/Simulation/CsqcModelAppearance.cs` · `game/net/{ServerNet.cs,ClientNet.cs,ClientEntityView.cs,ViewEntityRenderer.cs}` · `game/client/ShowNamesLayer.cs` · `game/hud/{RadarPanel.cs,CrosshairPanel.cs}`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
This unit is the wire-state layer for every networked entity Xonotic shows you that you do not yourself control: other players' positions/models/animations, their nameplates and radar blips, their carried weapon, dropped items, projectiles, gibs. In Base it is three cooperating systems plus a cvar-replication helper:

- **csqcmodel** (`lib/csqcmodel`) — the engine-adjacent generic networked-model channel (`ENT_CLIENT_MODEL`). Sends a 24-bit SendFlags change-mask and only the changed properties (origin/angles/frame/modelindex/effects/colormod/glowmod/scale/anim state/…). The client (`cl_model.qc`) reconstructs, two-snapshot-interpolates origin+angles (`interpolate.qc`), and animation-lerps frame groups. This is what makes the local player *predicted* (the server does NOT `SendEntity` the player body, so DP prediction stays engine-side) while remote players are *interpolated*.
- **ent_cs** (`common/ent_cs.qc`) — a lightweight per-player "GPS" sender entity decoupled from the model. Networks the minimal slice the HUD needs even when the player is out of PVS: origin, angles.y, health, armor, name, model, skin, colors, frags, handicap, wants-join, solid. Drives radar, nameplates, shownames teammate health/armor bars, the scoreboard team/spec inference, and player sounds. Carries a **public/private mask**: name/model/colors/frags are public; origin/health/armor/angles are private (only sent to teammates, or to everyone when `radar_showenemies`).
- **wepent** (`common/wepent.qc`) — a per-weapon-slot sender entity for the **viewmodel**. Networks the active/switching/switch weapon, the weapon alpha, and every per-weapon charge/clip HUD value: `vortex_charge`, `oknex_charge`, chargepools, `clip_load`/`clip_size`, `hagar_load`, `minelayer_mines`, `arc_heat_percent`, `tuba_instrument`, `m_gunalign`, `porto_v_angle_held`, skin. It is `setcefc(wepent_customize)`-gated to the *owner's* current view entity.
- **replicate** (`common/replicate.qc` + `lib/replicate.qh`) — the reverse direction: the client periodically `cmd sentcvar`s a small set of `cl_*` cvars to the server (autoswitch, handicap, noantilag, clippedspectating, …) every `0.8 + random()*0.4` s, plus once at session start.

The **port replaces all four channels with one unified delta-compressed entity snapshot** (`NetEntityState` + `EntityStateCodec` + `ServerSnapshotHistory`/`ClientSnapshotHistory`), plus a separate full-precision **owner block** for the local player's own authoritative state (the reconcile seed). This is a deliberate architecture change (see Intended divergences), so most "faithful" claims here are *behavioral*, not line-by-line.

## Base algorithm (authoritative)

### csqcmodel generic model channel  (`lib/csqcmodel/sv_model.qc:CSQCModel_Send`, `cl_model.qc:ENT_CLIENT_MODEL`)
- **Trigger:** `CSQCModel_LinkEntity(e)` sets `e.SendEntity = CSQCModel_Send`; `CSQCModel_CheckUpdate(e)` (run from the player think / autoupdate) diffs each property against its cached `csqcmodel_*` shadow and sets the matching SendFlags bit.
- **Algorithm:** header `ENT_CLIENT_MODEL`, then `WriteInt24_t(sf)`, a player-state byte `psf` (ISPLAYER_CLIENT/LOCAL/PLAYER bits), then for each set flag the property writer from `ALLPROPERTIES`. Client reads the same mask order, runs `InterpolateOrigin_Undo` → apply props → `InterpolateOrigin_Note`, and registers `CSQCModel_Draw` as the predraw.
- **Property table / constants** (`common.qh` + `csqcmodel_settings.qh`):
  - `CSQCMODEL_PROPERTY_FRAME=BIT(23)`, `TELEPORTED=BIT(22)`, `MODELINDEX=BIT(21)`, `ORIGIN=BIT(20)`, `YAW=BIT(19)`, `PITCHROLL=BIT(18)`, `FRAME2=BIT(17)`, `LERPFRAC=BIT(16)`, `SIZE=BIT(15)`.
  - Origin = `WriteVector` (13.3 fixed coord). Yaw/pitch/roll = `WriteAngle` (8-bit). Frame = byte. Modelindex = short. Alpha is `BIT(3)` scaled `254·a-(-1)` into a byte; colormod scaled `×16`; glowmod scaled `×254`.
  - `CSQCPLAYER_FORCE_UPDATES 4` → at least 4 origin sends/sec per player even if unchanged (keeps replay buffers full; reduces interval by `0.75·CL_MAX_USERCMDS·pm_frametime − ping`). `CL_MAX_USERCMDS 128`.
  - `EF_TELEPORT_BIT` → sets TELEPORTED (no interp); `EF_RESTARTANIM_BIT` → resends FRAME|FRAME2 (full anim restart).
- **Interpolation** (`interpolate.qc`):
  - `InterpolateOrigin_Note`: shifts `iorigin2→iorigin1`, derives velocity from origin delta when `IFLAG_AUTOVELOCITY`, derives angles from motion when `IFLAG_AUTOANGLES`. **Don't-lerp (snap) rules:** `IFLAG_TELEPORTED`, OR origin jump `> 1000`u, OR velocity jump `> 1000`, OR `dt >= 0.2`s → `itime1=itime2=time`. Otherwise `itime1=serverprevtime, itime2=time`.
  - `InterpolateOrigin_Do`: `f = bound(0, (time-itime1)/(itime2-itime1), 1 + cl_lerpexcess)`; lerps origin/velocity linearly; lerps angles via the **basis-vector blend** (`fixedvectoangles2` of blended forward/up) so it doesn't cross the yaw seam.
  - Animation lerp (`cl_model.qc`): frame-group cross-fade gated by `cl_lerpanim_maxdelta_framegroups=0.1`; `cl_nolerp=0` disables.

### ent_cs GPS channel  (`common/ent_cs.qc`)
- **Trigger:** `entcs_attach(player)` (from `common/state.qc:58`, on client connect/PlayerInit) creates `CS(player).entcs`, a sender that thinks every `0.015625`s. `entcs_detach` on disconnect.
- **Property table** (`EntCSProps` registry, `ENTCS_PROP`/`ENTCS_PROP_CODED`/`ENTCS_PROP_RESOURCE`): ENTNUM(sentinel,private), ORIGIN(private, full vector), ANGLES.y(private, coded `/ (360/64)` → byte), HEALTH(private, RES_HEALTH coded `/10` byte, bounded 0..255), ARMOR(private, same), NAME(public, string, mutable), MODEL(public), SKIN(public byte), CLIENTCOLORS(public byte), FRAGS(public short), HANDICAP_LEVEL(public byte), WANTSJOIN(public char), SOLID(public, `sv_solid` byte).
- **Privacy mask** (`_entcs_send`): always sends ENTNUM; if the owner `IS_PLAYER` and the recipient is NOT (radar_showenemies OR SAME_TEAM OR not-a-player/not-in-game), the flags are ANDed with `ENTCS_PUBLICMASK` — private fields (origin/angles/health/armor) are stripped. `m_forceupdate` (set by `entcs_force_origin`) forces an origin send for player sounds.
- **think:** diff every prop against the owner, set SendFlags; during intermission strip HEALTH; always force ORIGIN for players (so an out-of-PVS teammate's tag doesn't vanish). `entcs_update_players` marks all *other* players' private mask dirty (used on team change / clanarena round).
- **Client side** (`ent_cs.qc CSQC`): receiver array `_entcs[255]`. When the player model is in PVS the entcs origin is overridden by the real csqcmodel origin (`entcs_think`); otherwise it interpolates the networked origin. `entcs_GetSpecState/Team/Name/ClientColors/Alpha/Color/IsDead` are the HUD/scoreboard accessors. Spec inference: `frags==FRAGS_SPECTATOR` → pure spec; `FRAGS_PLAYER_OUT_OF_GAME && solid==SOLID_NOT` → in-scoreboard spec.

### wepent viewmodel channel  (`common/wepent.qc`)
- **Trigger:** `wepent_link(w_ent)` from `server/weapons/weaponsystem.qc:201` (per spawned weapon entity). Thinks every frame; diffs each `WEPENT_NETPROPS` field vs the owner weapon entity and sets the bit. `wepent_customize` gates the send to the recipient's current view entity (`WaypointSprite_getviewentity`).
- **Property table** (`WEPENT_NETPROPS`, 24-bit mask, all private): sv_entnum(sentinel), m_switchweapon, m_switchingweapon, m_weapon (all `WriteRegistered(Weapons)`), m_alpha (byte), vortex_charge (`×255` byte), oknex_charge (`×16`), m_gunalign (byte), porto_v_angle_held (byte + optional `WriteAngleVector2D`), tuba_instrument (byte), hagar_load (byte), minelayer_mines (byte), arc_heat_percent (`×255` byte), vortex_chargepool_ammo (`×16`), oknex_chargepool_ammo (`×16`), clip_load (short), clip_size (short), skin (short).
- **Client side:** `ReadWepent` reads the slot byte then applies each field to `viewmodels[slot]`. These drive the first-person viewmodel selection + the crosshair charge/clip/heat rings (`CrosshairPanel` analog).

### replicate (client→server cvars)  (`common/replicate.qc`, `lib/replicate.qh`)
- **Set** (`replicate.qh`): cvar_cl_autoswitch(bool), cl_autoscreenshot(int), cl_clippedspectating(bool), cl_autoswitch_cts(int), cl_handicap(vector), cl_handicap_damage_given/taken(float), cl_noantilag(bool), g_xonoticversion(string). Note `common/replicate.qc` includes just the header; many other `REPLICATE_INIT/REPLICATE` live alongside their feature (weaponpriority, notification choices, …).
- **Cadence** (`lib/replicate.qh`): `ReplicateVars_Start` sends all once; `ReplicateVars(CHECK)` every `0.8 + random()*0.4`s compares vs last value and pushes only changed via `cmd sentcvar <name> <value>`.

## Port mapping

| Base feature | Port symbol | Liveness |
|---|---|---|
| csqcmodel SendFlags change-mask | `EntityField` enum + `EntityStateCodec.WriteDelta/ReadDelta` (NetEntity.cs) | live (every snapshot) |
| Per-client delta baseline (DP EntityFrame) | `ServerSnapshotHistory`/`ClientSnapshotHistory` (SnapshotDelta.cs) | live |
| `InterpolateOrigin_Note/Do` + teleport/stall snap | `InterpolationBuffer.Note/Sample` (SnapshotInterpolation.cs) | live (per remote entity) |
| TELEPORTED bit | `NetEntityFlags.Teleported` (set in ServerNet on a >threshold origin jump; read in ClientNet) | live |
| Animation frame-group lerp (`cl_lerpanim_maxdelta_framegroups`) | NOT IMPLEMENTED as a 2-frame cross-fade; `Frame` is networked and applied raw (LocomotionBlend does its own client-side blend) | partial |
| Networked anim ACTION state (`anim_state`/`anim_upper_action`/`anim_lower_action` + times, `csqcmodel_settings.qh` BIT7-9 → `animdecide`) | NOT networked; `LocomotionBlend.Split` reconstructs legs(velocity)+torso(aim-pose) client-side from one networked `Frame` — remote attack/pain/jump torso overlays are inferred, not authoritative | partial |
| ent_cs origin/health/armor/team/name for radar+shownames | folded into `NetEntityState` (Origin/Health/Armor/Colormap/Model); consumed by `RadarPanel`, `ShowNamesLayer` | live |
| ent_cs public/**private** mask (`radar_showenemies`/SAME_TEAM) | NOT IMPLEMENTED — every player's full state goes to everyone (PVS-gated only) | missing |
| ent_cs out-of-PVS origin guarantee (force ORIGIN) | partial — PVS relevance cull (`RelevantEntitiesFor`) drops out-of-PVS entities entirely rather than degrading to GPS-only | partial |
| ent_cs spec-state inference (frags/solid) | NOT via entcs; `SpectateeStatus`/scoreboard rows carry it through other channels | n/a here |
| wepent m_weapon (remote held weapon) | `NetEntityState.Weapon` → `ClientEntityView` → `ViewEntityRenderer` (third-person attach) | live |
| wepent m_weapon (local viewmodel select) | owner block `ActiveWeaponId` (ServerNet.WriteOwnerState → ClientNet) | live |
| wepent charges/clip/heat (vortex_charge, clip_load, hagar_load, arc_heat, minelayer_mines, chargepools) | NOT networked; `CrosshairPanel` setters exist but are fed from LOCAL prediction only (no live feeder found in `game/`) | dead/missing |
| wepent m_switchweapon/m_switchingweapon | NOT networked (only the resolved active weapon is sent) | missing |
| wepent m_alpha / m_gunalign / porto_v_angle_held / tuba_instrument / skin | NOT networked | missing |
| csqcmodel appearance hook (force model/colors, unique colors, death-fade) | `CsqcModelAppearance` (pure, unit-tested) + `game/client/ModelTint.cs` | live (math); one documented team-list gap |
| replicate client→server cvars | `ClientNet.PumpReplicatedCvars` (`cmd sentcvar`, 0.8+rand·0.4 cadence, send-all-on-start) | live (subset) |

### Owner block (no direct Base analog — see divergences)
The local player is delta-EXCLUDED from the entity snapshot (`excludeEntNum`) exactly like Base never `SendEntity`s the player body. Instead `WriteOwnerState` sends a full-precision authoritative block: origin, velocity, onGround, health, armor, **ActiveWeaponId**, respawn-time stat, spectatee status, punch-angle, highspeed multiplier, accuracy bytes. ClientNet reads it as the reconcile seed.

## Parity assessment

**logic — faithful (behavioral).** The change-mask + per-client delta-baseline + two-snapshot interpolation reproduce the csqcmodel contract: only-changed-fields on the wire, an idle entity costs one zero mask, a lost packet keeps deltaing against the last *acked* baseline (DP's EntityFrame). The teleport/stall snap thresholds are exact: `TeleportDistance=1000`, `MaxInterval=0.2`, `lerpExcess` clamp — direct ports of `interpolate.qc`. The appearance hook (force model/colors, unique-enemy combo, palette, death-glow) is a faithful pure port with one self-documented gap (friend-color collision is checked against the local team only, not the full team list).

**values — partial.** Wire quantization differs by design (the port uses its own `NetPrecision.Low` Write/Read for origin 13i / angles 8i rather than the QC byte-coded fields), but the *interp constants* match. The csqcmodel `CSQCPLAYER_FORCE_UPDATES=4` keepalive-origin guarantee has no explicit analog (the port sends a full owner block + entity deltas every broadcast tick, which is denser, so the symptom — a stale teammate tag — does not arise; but there is no out-of-PVS GPS degrade). ent_cs's coded health (`/10`) and armor steps are not reproduced (full short is sent).

**timing — faithful where ported.** Interpolation cadence is client-render-clock driven (NetGame `_renderClock` creep), structurally matching DP `cl_nettimesync*`. The replicate cadence `0.8 + random()*0.4`s + send-all-on-start is an exact port (ClientNet.cs:724). The ent_cs 64Hz think (`0.015625`) and wepent every-frame think have no direct analog — the port runs one snapshot per server broadcast tick.

**presentation — partial.** Remote players render with model + frame + the third-person held weapon (wepent m_weapon → ViewEntityRenderer) and the appearance hook drives tint — that's live. BUT the per-weapon **charge/clip/heat HUD rings** (`CrosshairPanel.ClipLoad/ChargeFraction/ChargePool/HagarLoad/MineCount/ArcHeat`) have NO live feeder anywhere in `game/` — the setters exist but nothing assigns them, so on the live path the Vortex charge ring, Hagar/Mine-Layer load ring, Arc heat, and the reload (clip) ring never appear. Animation frame-group cross-fade (`cl_lerpanim_maxdelta_framegroups`) is replaced by the client-side LocomotionBlend rather than the networked 2-frame lerp, so remote anim blending is approximate.

**audio — na.** This unit carries state; the only sound coupling is ent_cs `entcs_force_origin` for player-sound positioning (the port positions player sounds off the live snapshot origin instead).

**liveness — partial overall.** The core snapshot/interp/radar/shownames/remote-weapon/appearance/replicate paths are LIVE and exercised every match. The dead/missing pieces: wepent charge/clip/heat fields (present-but-unfed → dead), ent_cs enemy-privacy mask (missing), switchweapon/switchingweapon/m_alpha/gunalign/porto/tuba/skin wepent fields (missing), and ~5 of the replicate cvars (cl_autoscreenshot, cl_clippedspectating, cl_autoswitch_cts, cl_handicap*, g_xonoticversion — missing).

### Gaps (player-observable)
1. **Vortex/Overkill-Nex charge ring, Hagar/Mine-Layer load ring, Arc heat ring, and reload (clip) ring never draw** — the CrosshairPanel charge setters have no live feeder (the QC wepent `vortex_charge`/`clip_load`/`hagar_load`/`arc_heat_percent`/`minelayer_mines` + chargepools are not networked and not read from local prediction). The hold-to-charge weapons give no visual charge feedback.
2. **Enemies' origin/health/armor are always visible to the netcode** — no `radar_showenemies`/SAME_TEAM private-field mask. A modified client could read every enemy's exact position/health (Base strips these for non-teammates). PVS culling is the only gate.
3. **Remote weapon switch animation is coarse** — only the resolved active weapon is networked, not `m_switchweapon`/`m_switchingweapon`, so a remote player's weapon-raise/lower transition isn't reproduced.
4. **No out-of-PVS teammate GPS** — Base keeps sending a teammate's coarse ent_cs origin even when their model leaves your PVS (so the radar blip / nameplate persists); the port's PVS relevance cull drops the entity entirely, so an out-of-PVS teammate vanishes from the radar.
5. **Several replicate cvars unported** — cl_autoscreenshot, cl_clippedspectating, cl_autoswitch_cts, cl_handicap(+damage_given/taken), g_xonoticversion are not in the client's replicated set, so the server can't honor them per-player (clipped spectating, handicap, CTS autoswitch).
6. **Animation frame-group cross-fade not networked** — `cl_lerpanim_maxdelta_framegroups`/`cl_nolerp` 2-frame lerp replaced by client-side LocomotionBlend; remote animation blending diverges from Base.
7. **Networked anim ACTION state not ported** — Base networks the discrete upper/lower-body action overlay (`animdecide`: attack/pain/jump/melee + start times) per player; the port reconstructs only legs+aim-pose from one networked `Frame` (`LocomotionBlend.Split`), so a remote player's firing/pain/jump *upper-body* animation is approximate, not authoritative. (Distinct from gap 6, which is about lerping between two frames.)

### Liveness — named callers
- Server producer: `ServerNet.BuildEntitySnapshot` (the player loop ~line 1634 + entity loop ~1715) → `RelevantEntitiesFor` → `ServerSnapshotHistory.EncodeSnapshot` (~1556), every broadcast tick.
- Owner block: `ServerNet.WriteOwnerState` (~1949) every tick.
- Client consumer: `ClientNet.HandleSnapshot` (~946) → `ClientSnapshotHistory.DecodeSnapshot` → `InterpolationBuffer.Note`; render via `SampleRemote`/`TryGetRemoteState`.
- Renderers: `ClientEntityView.Apply` (Player/Item/Gib/Projectile/Generic branches; ViewModel+Nameplate branches are dead — the server never emits those kinds), `RadarPanel`, `ShowNamesLayer`, `ViewEntityRenderer`.
- Replicate: `ClientNet.PumpReplicatedCvars` → `cmd sentcvar` → server `Commands.SentCvarAllowlist`; consumed e.g. `ServerNet` cl_noantilag (~2052).

### Intended divergences
- **Unified delta-snapshot replaces csqcmodel + ent_cs + wepent + CSQCProjectile + the generic CSQC entity stream.** One `NetEntityState` table, delta-compressed per client against the last acked baseline (a real client/server split, vs QC's per-entity `SendEntity` funcs). Rationale: a single change-masked codec is simpler, gives free idle-entity compression, and survives packet loss via acked baselines — the port has no QCVM `SendEntity`/`ReadEntity` machinery to mirror, and DP's own entity delta already works this way. Documented inline in NetEntity.cs / SnapshotDelta.cs.
- **Separate full-precision owner block** instead of relying on the engine to not-network the local body. Rationale: the port runs explicit client prediction + reconciliation in C#; it needs the authoritative owner seed every tick. Net effect matches Base (local = predicted, remotes = interpolated).
- **`NetPrecision.Low`/`Float` quantization** instead of the QC byte/coord coding. Same precision class, different bit layout.

## Verification
- Wire round-trip (Diff → WriteDelta → ReadDelta) and the ack/baseline logic: covered by the `.Net` codec design + inline invariants; no dedicated unit test located for `EntityStateCodec`/`SnapshotDelta` (mark medium confidence).
- `InterpolationBuffer` teleport/stall snap + seam-safe angle blend: structural port verified by reading; behavioral correctness (no judder/seam jump) is asserted in the camera-drift memory note (render-smoothing path), not a unit test.
- `CsqcModelAppearance`: described as unit-tested in its own header; the friend-color team-list gap is self-documented.
- Charge-ring dead feeder: verified by grep — no assignment to `CrosshairPanel.ClipLoad/ChargeFraction/ChargePool/HagarLoad/MineCount/ArcHeat` exists outside the panel itself in `game/`.
- Enemy-privacy mask absence: verified by grep — no `radar_showenemies`/`showenemies`/team-field-filter in `ServerNet`/`src`.
- Replicate cadence + set: read directly (ClientNet.cs:665, 724).

## Open questions
- Does any HUD path feed the crosshair charge rings from *local* prediction at runtime (e.g. CrosshairPanel pulling EntityWeaponState directly), that the static grep missed? Needs an in-match observation of a charging Vortex.
- Is the enemy-privacy mask omission a deliberate anti-cheat decision deferred to a later layer, or simply unported? Needs owner input — flagged as a gap pending that.
- Does the absence of an out-of-PVS GPS degrade actually drop teammate radar blips in a large map, or does the broadcast cadence + relevance bounds keep them alive in practice? Needs a runtime check on a big CTF map.
