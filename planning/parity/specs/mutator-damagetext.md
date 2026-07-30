# Damage Text mutator — parity spec

**Base refs:** `common/mutators/mutator/damagetext/{damagetext,sv_damagetext,cl_damagetext,ui_damagetext}.{qc,qh}`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/DamagetextMutator.cs`, `src/VortexArena.Common/Gameplay/Mutators/DamageTextFormat.cs`, `game/client/DamageTextLayer.cs`, `game/client/DamageTextConfig.cs`, `game/net/NetGame.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The damagetext mutator draws floating damage numbers at the point you hit an enemy (or, in some cases,
a 2D number near screen center). It is split across three layers: a **server producer** (`sv_damagetext.qc`,
the `PlayerDamaged` mutator hook) that builds one `net_damagetext` temp entity per hit and sends it via
`Net_LinkEntity`/`write_damagetext`; a **client receiver+draw** (`cl_damagetext.qc`, the `DamageText` CSQC
class) that reads the wire entity, accumulates onto an existing number, formats the label, and animates it
each frame; and a **menu tab** (`ui_damagetext.qc`) exposing the cvars. It is always registered
(`REGISTER_MUTATOR(damagetext, true)`); behaviour is gated by `sv_damagetext` (server, default 2) and the
~30 `cl_damagetext_*` cvars (client, all `seta`/saved).

## Base algorithm (authoritative)

### Server: PlayerDamaged producer  (`sv_damagetext.qc:write_damagetext`, `MUTATOR_HOOKFUNCTION(damagetext, PlayerDamaged)`)
- **Trigger:** `MUTATOR_CALLHOOK(PlayerDamaged, attacker, this, dh, da, hitloc, deathtype, damage)` fired in
  `server/player.qc:430` after health/armor are subtracted. Args: attacker, target(=hit), health(=dh actually
  removed), armor(=da actually removed), hitloc, deathtype(int), potential_damage(=full uncapped `damage`).
- **Algorithm:**
  1. Early-out if `sv_damagetext <= 0`; if `hit == attacker`; if `potential_damage == 0`.
  2. If instagib mutator enabled AND `DEATH_WEAPONOF(deathtype) == WEP_VAPORIZER` → return (suppress one-shot text).
  3. **Same-frame accumulation:** static `net_text_prev` + `net_damagetext_prev_time`. `multiple` is true when
     prev time == `time`, prev entity alive, same `realowner` (attacker), same `enemy` (hit), same deathtype.
     If so, add this hit's health/armor/potential onto the previous temp entity (shotgun pellets coalesce).
  4. **Flags:** `DTFLAG_SAMETEAM` if `SAME_TEAM(hit, attacker)`; `DTFLAG_BIG_HEALTH/ARMOR/POTENTIAL` when the
     value `>= DAMAGETEXT_SHORT_LIMIT` (256); `DTFLAG_NO_ARMOR` when `armor == 0`; `DTFLAG_NO_POTENTIAL` when
     `almost_equals_eps(armor + health, potential_damage, 5)`.
  5. If `multiple`: rewrite prev entity's fields and return.
  6. Else, **first-hit-after-respawn:** per-victim bitset `dent_attackers[]` (11 ints × 24 bits = 255 clients,
     indexed by `etof(attacker)-1`). If this attacker's bit was unset, set it and OR in `DTFLAG_STOP_ACCUMULATION`
     (forces the client to start a fresh accumulation group).
  7. Spawn `new_pure(net_damagetext)`; set realowner/enemy/flags/deathtype/health/armor/potential;
     `setthink(SUB_Remove)`, `nextthink = (time > 10) ? time + 0.5 : 10` (so the temp entity survives ~0.5 s,
     or until t=10 early in the match while clients load in); `Net_LinkEntity(..., write_damagetext)`.
- **Wire (`write_damagetext`):** visibility filter by `sv_damagetext` tier: ALL(≥3) → every client;
  PLAYERS(≥2) → `client == attacker`; SPECTATORS(≥1) → spectator watching the attacker, or any observer.
  Then writes: `WriteByte(etof(hit))`, `WriteInt24_t(deathtype)`, `WriteByte(flags)`, then health/armor/
  potential each as a `WriteShort` or `WriteInt24_t` (per the BIG_* flags), multiplied by
  `DAMAGETEXT_PRECISION_MULTIPLIER` (128); armor omitted under NO_ARMOR, potential omitted under NO_POTENTIAL.
- **Lifecycle hooks:** `ClientDisconnect` clears the disconnecting player's bit from every victim's
  `dent_attackers`; `PlayerSpawn` zeroes the spawning player's whole `dent_attackers[]` (so the next hit from
  any attacker re-triggers STOP_ACCUMULATION).

### Client: receive + accumulate  (`cl_damagetext.qc:NET_HANDLE(damagetext)`)
- Reads the wire (mirrors `write_damagetext`), reconstitutes health/armor/potential (NO_ARMOR→0,
  NO_POTENTIAL→health+armor).
- Gate: `cl_damagetext == 0` → drop. Friendlyfire (SAMETEAM): `cl_damagetext_friendlyfire 0` drop always;
  `1` drop when health==0 && armor==0; `2` always show.
- **Placement decision:** `entcs_receiver(server_index-1)` gives the victim's networked origin. `can_use_3d` =
  entcs has origin. `too_close` = victim within `cl_damagetext_2d_close_range` (125) of view origin.
  `prefer_in_view` = `cl_damagetext_2d_out_of_view` && victim projects off-screen. `prefer_2d` = spectating
  (`spectatee_status != -1`) && `cl_damagetext_2d` && (too_close || prefer_in_view). Choose 3D world coords
  when can_use_3d && !prefer_2d, else 2D screen coords (only when `cl_damagetext_2d` && spectating), else drop.
- **Accumulation:** `IL_EACH(g_damagetext, it.m_group == server_index)` — find an existing number for this
  victim. Disown it (`m_group=0`, spawn fresh) when STOP_ACCUMULATION, or `current_alpha < accumulate_alpha_rel
  * alpha_start`, or (`accumulate_lifetime >= 0` && age > accumulate_lifetime). Otherwise add the new damage
  onto it and `goto updateDT`. 2D numbers stagger by `cl_damagetext_2d_overlap_offset * DamageText_screen_count++`.

### Client: format + size  (`cl_damagetext.qc:DamageText_update`)
- Divide stored (×128) health/armor/potential by 128, `rint` each. `total = h+a`, `potential_health = pot-armor`.
  `redundant = almost_equals_eps(h+a, pot, 5)`.
- `cl_damagetext_format` (default `-{total}`) token replace: `{armor}` (hidden if 0 && hide_redundant),
  `{potential}`/`{potential_health}` (hidden if redundant && hide_redundant), `{health}`/`{total}` (show
  `actual (potential)` when verbose && they differ, else just actual). Strip remaining unknown `{...}` tokens
  (futureproof), trim leading/trailing spaces.
- Size: `map_bound_ranges(potential, size_min_damage(25), size_max_damage(140), size_min(10), size_max(16))` —
  clamps at the src bounds, linear between.

### Client: draw  (`cl_damagetext.qc:DamageText_draw2d`)
- Per frame, `since = time - hit_time`. `size = m_size - since*shrink_rate*m_size`,
  `alpha = alpha - since*fade_rate`. Remove if alpha<=0 || size<=0 || (lifetime>=0 && since>=lifetime).
- World numbers: build `world_offset = since*velocity_world + offset_world`, rotate by **view basis**
  (forward/right/up from `view_angles`), add to victim origin, `project_3d_to_2d`, then add
  `since*velocity_screen + offset_screen`. 2D numbers: `origin + since*2d_velocity`.
- Center horizontally by `stringwidth(text, hud_fontsize*2)*0.5`. Color = friendlyfire_color when FF, else
  `cl_damagetext_color`; if `color_per_weapon`, use the weapon's `m_color`. Apply `drawfontscale =
  size/size_max`, `drawcolorcodedstring2` at `size_max` cell size.
- **Fade/shrink rates** set in CONSTRUCTOR: world → `fade_rate = 1/alpha_lifetime`, `shrink_rate = 0`;
  2D → `fade_rate = 1/2d_alpha_lifetime`, `shrink_rate = 1/2d_size_lifetime`.

### Constants / cvars (Base defaults)
| cvar | default | side | meaning |
|---|---|---|---|
| `sv_damagetext` | 2 | sv | 0 off / 1 spectators / 2 +attacker / 3 all |
| `cl_damagetext` | 1 | cl | master enable |
| `cl_damagetext_format` | `-{total}` | cl | label template |
| `cl_damagetext_format_verbose` | 0 | cl | show potential alongside actual |
| `cl_damagetext_format_hide_redundant` | 0 | cl | hide 0-armor / equal-potential tokens |
| `cl_damagetext_color` | `1 1 0` | cl | enemy color |
| `cl_damagetext_color_per_weapon` | 0 | cl | use weapon color |
| `cl_damagetext_size_min` / `_size_min_damage` | 10 / 25 | cl | small-damage font / threshold |
| `cl_damagetext_size_max` / `_size_max_damage` | 16 / 140 | cl | large-damage font / threshold |
| `cl_damagetext_alpha_start` | 1 | cl | initial alpha (3D) |
| `cl_damagetext_alpha_lifetime` | 3 | cl | fade time s (3D) |
| `cl_damagetext_lifetime` | -1 | cl | hard lifetime (−1 = ignore) |
| `cl_damagetext_velocity_world` | `0 0 20` | cl | drift (world, view-relative) |
| `cl_damagetext_offset_world` | `0 25 0` | cl | offset (world, view-relative) |
| `cl_damagetext_velocity_screen` | `0 0 0` | cl | drift (screen) |
| `cl_damagetext_offset_screen` | `0 -45 0` | cl | offset (screen) |
| `cl_damagetext_accumulate_alpha_rel` | 0.65 | cl | disown threshold (×alpha_start) |
| `cl_damagetext_accumulate_lifetime` | -1 | cl | disown by age (−1 = ignore) |
| `cl_damagetext_friendlyfire` | 1 | cl | 0 never / 1 when>0 / 2 always |
| `cl_damagetext_friendlyfire_color` | `1 0 0` | cl | FF color |
| `cl_damagetext_2d` | 1 | cl | allow 2D fallback |
| `cl_damagetext_2d_pos` | `0.47 0.53 0` | cl | 2D anchor (fraction of screen) |
| `cl_damagetext_2d_alpha_start` | 1 | cl | initial alpha (2D) |
| `cl_damagetext_2d_alpha_lifetime` | 1.3 | cl | fade time s (2D) |
| `cl_damagetext_2d_size_lifetime` | 3 | cl | shrink time s (2D) |
| `cl_damagetext_2d_velocity` | `-25 0 0` | cl | drift (2D screen) |
| `cl_damagetext_2d_overlap_offset` | `0 -15 0` | cl | per-number stagger (2D) |
| `cl_damagetext_2d_close_range` | 125 | cl | force-2D radius |
| `cl_damagetext_2d_out_of_view` | 1 | cl | force-2D when off-screen |
| `DAMAGETEXT_PRECISION_MULTIPLIER` | 128 | shared | fixed-point wire scale |
| `DAMAGETEXT_SHORT_LIMIT` | 256 | shared | int24-vs-short cutoff |

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| PlayerDamaged producer | `DamagetextMutator.OnPlayerDamaged` | live (DamageSystem.cs:461 fires the hook) |
| same-frame accumulation | `DamagetextMutator` `_prev*` fields + `multiple` branch | faithful |
| flag computation | `OnPlayerDamaged` flags block | faithful |
| dent_attackers bitset | `EntityMutatorState.DentAttackers` (`HashSet<Entity>`) | faithful (logic; representation differs) |
| PlayerSpawn / ClientDisconnect clears | `OnPlayerSpawn` (clear). **ClientDisconnect: NOT IMPLEMENTED** | partial |
| wire encode/decode + tiers | collapsed: `DrainPending()` → host-local queue (no net) | divergent (host-only) |
| format tokens + size map | `DamageTextFormat.Build` / `MapSize` | faithful |
| receive + accumulate (client) | `DamageTextLayer.Add` | partial (3D-only placement) |
| draw + fade/shrink/move | `DamageTextLayer._Draw` / `_Process` | partial |
| 2D placement heuristics | NOT IMPLEMENTED (camera-null fallback only) | missing |
| per-weapon / FF color | `DamageTextLayer._Draw` (colorKey via NetGame) | faithful |
| menu tab (ui_damagetext) | NOT IMPLEMENTED | missing |

## Parity assessment

### Faithful
- **Server producer logic + values:** the PlayerDamaged handler is a near-line-for-line port — skips,
  same-frame accumulation, all six flag bits at the right thresholds (SHORT_LIMIT 256, eps 5), the
  STOP_ACCUMULATION first-hit-after-respawn gate, instagib-vaporizer suppression. Verified by
  `MutatorBatchT51Tests` (`Damagetext_QueuesEvent_OnPlayerDamaged`, `_AccumulatesSameFrameHits`, `_Disabled`).
- **Format + size math:** `DamageTextFormat` matches token replacement, verbose/hide-redundant, unknown-token
  strip, trim, and `map_bound_ranges`. Verified by the `DamageTextFormat_*` tests. (Note `rint` is implemented
  as banker's rounding to match the engine builtin.)

### Gaps (player-observable)
- **2D fallback mode is effectively absent.** QC switches to a screen-centered 2D number for hits that are
  too close (`< cl_damagetext_2d_close_range` 125), off-screen (`cl_damagetext_2d_out_of_view`), or when
  spectating. The port only uses the 2D path when the camera is null or the point is behind the camera; it
  never applies close-range / out-of-view / spectating logic, `2d_overlap_offset` staggering, or the separate
  2D fade(1.3 s)+shrink(3 s) lifetimes. Numbers for very close or just-off-screen hits won't appear / won't be
  pinned near the crosshair as in Base.
- **World drift is reduced to a vertical world-Z rise.** QC offsets the world number along the **view basis**
  (`velocity_world`·forward/right/up + `offset_world`) before projecting; the port applies only
  `velocity_world.Z`/`offset_world.Z` as a world-up (Godot-Y) rise, scaled by a hardcoded `0.0254`
  (inch→meter) factor, and drops the X/Y (forward/right) components and `offset_screen`/`velocity_screen` are
  applied in screen space but `offset_world`'s lateral parts are lost. The number rises but doesn't track the
  view-relative offset Base uses.
- **Color codes not rendered.** QC uses `drawcolorcodedstring2`; the port uses `DrawString` with the plain
  text, so `^x`-style color codes in `cl_damagetext_format` (and the default leading `-`) render literally.
- **Font is the Godot fallback font**, not the Xonotic HUD font; `drawfontscale`-based crisp scaling
  (`size/size_max` cell) is replaced by a direct integer pixel size, so glyph metrics / centering differ.
- **ClientDisconnect dent_attackers clear missing.** Base clears a disconnecting player's bit from every
  victim's attacker set; the port has no ClientDisconnect hook. Effect: if a player disconnects and a new
  player reuses the same entity slot, the STOP_ACCUMULATION first-hit gate could mis-fire. Minor / edge.
- **Server visibility tiers collapsed to host-local.** No `Net_LinkEntity` equivalent; `DrainPending` is a
  local host queue consumed only when `_server is not null` (host path). `sv_damagetext` 1/3 tiers
  (spectators-only, all-players) and the spectator/observer filtering are not modeled — only the local
  attacker (tier 2) sees text. Acceptable for single-player/listen host; would miss text in true netplay /
  spectating. Marked intended_divergence (documented in the mutator header).
- **Menu tab missing.** `ui_damagetext.qc` (the in-game Damage Text settings tab) has no port counterpart; the
  cvars exist but aren't exposed in a settings UI.

### Liveness
- **Server producer: LIVE.** `DamageSystem.PlayerDamage` → `MutatorHooks.PlayerDamaged.Call` (DamageSystem.cs:461)
  → `DamagetextMutator.OnPlayerDamaged`. The mutator is registered via `[Mutator]` and enabled when
  `sv_damagetext > 0` (default 2).
- **Client draw: LIVE (host path).** `NetGame._Process` (NetGame.cs:2070-2089) drains the queue and calls
  `_damageText.Add(...)`; `_damageText` is created at NetGame.cs:1179. Guarded by `if (_server is not null)` —
  so it runs on a listen/host but a pure remote client (no local server) gets no damage text.

### Intended divergences
- **Host-local event queue instead of CSQC `Net_LinkEntity`.** The whole client/server split is collapsed into
  an in-process `DrainPending` queue, matching this port's general "host drives the local HUD directly" model.
  Rationale: the port's net layer doesn't replicate per-hit temp entities; the local attacker (sv_damagetext 2)
  is the common case. Documented in `DamagetextMutator` header.

## Verification
- Server producer + format helper: unit tests in `tests/VortexArena.Tests/MutatorBatchT51Tests.cs`
  (`Damagetext_QueuesEvent_OnPlayerDamaged`, `Damagetext_AccumulatesSameFrameHits`, `Damagetext_Disabled_QueuesNothing`,
  `DamageTextFormat_*`). Verified by reading the tests.
- Liveness: traced caller chain DamageSystem.cs:461 → DamagetextMutator → NetGame.cs:2074 → DamageTextLayer.Add.
- Draw fidelity (2D heuristics, view-basis drift, color codes, font): read-only code comparison vs
  `cl_damagetext.qc`; NOT verified in-game.

## Open questions
- Does the 0.0254 inch→meter scale on the world rise actually place the number at the hit point at typical
  Quake unit scale, or does it drift off the victim? Needs an in-game check.
- In real multiplayer (remote client, no local server), is there any damage text at all? The host-only guard
  suggests not — needs confirmation against the net layer's HUD feed.
- Are color-code sequences expected in `cl_damagetext_format` in practice (the default `-{total}` has none),
  i.e. is the missing `drawcolorcodedstring2` a real player-visible gap for default configs? Likely cosmetic.
