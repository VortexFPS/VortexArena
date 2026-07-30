# Playtest bug backlog

Living list of bugs found during live playtesting of XonoticGodot. **Capture only — do
not start fixing until triaged.** Each entry: symptom (observed) → expected → notes /
hypothesis (unverified unless marked CONFIRMED) → suggested first look → status.

Convention reminders when picking one up:
- Compare against **Xonotic Base** (original qcsrc) before deciding the fix — most of these
  are "port drifted from Base", not "design from scratch".
- Prefix = authority (see ../docs/reference/CVARS.md); check the parity registry/specs under `planning/parity/`.
- Anything touching angles/rotation across the sim↔render boundary → read
  **planning/COORDINATE_CONVENTIONS.md** first (see #7).
- HUD fidelity work → **planning/HUD_PARITY_CONTRACT.md** + the `cl-*` specs under
  `planning/parity/specs/` (see #3, #10, #11).

Started 2026-07-03. More items pending (playtest ongoing). Screenshot evidence noted inline
(screenshots are pasted in-chat, not saved to the repo — descriptions preserve the detail).

## Fix status — 2026-07-03/04 (branch `claude/playtest-fixes`, uncommitted)

**Verified fixed in playtest** (user-confirmed on the release build): **#1** casings bounce-sound
spam, **#2** ammo texture, **#4** shield dome gone, **#7** carried-flag yaw (v2 = networked-negate),
**#8 team color** (v3 = real colormap; see #8 for the v1/v2 misfires), **#10** panel corners, **#11**
weapon autosize, **#12** item waypoints, **#13** capture announcer, **#15** flag rests on floor,
**#17** death scoreboard (v3 = centered + `MouseFilter` + respawn-stat reset), **#5** no flags in non-CTF.

_(Implemented but NOT yet re-confirmed after the map switch: player colors, **#3** HUD font, **#16** mouse-on-load,
**#18** radar scaling — the user tested #17/#5/#9a/#20 on Stormkeep DM and didn't circle back to those.)_

**Implemented, awaiting a playtest check:**
- **#21 weapon fire-effect spam** (new) — tapping/wheel-firing spammed the local muzzle flash + fire sound faster
  than the weapon's refire rate. `cl_predictfire` reset its refire clock on every fresh press (so taps bypassed it),
  and the predict-off + secondary-fire paths had NO gate at all. Fixed by giving the prediction a PERSISTENT
  per-weapon ready clock (a client mirror of Base's per-slot `ATTACK_FINISHED`) in `NetGame.UpdateLocalFireFeedback`.
  Verify: holding fire still feels instant, but mashing/wheel-firing no longer replays FX faster than real shots.
- **Player team colors** (new) — the SAME code-vs-nibble mismatch as #8 also mis-colored PLAYERS;
  fixed centrally in `ClientWorld.ResolveForcedColormap` via `NormalizeTeamColormap` (team codes
  4/13/12/9 → nibbles 1..4; forced/FFA pass through). Needs a bot team game to confirm.
- **#3 centerprint / HUD font** — was WIDTH-scaled (`viewport.X / hud_width(560)`), ~2× too big on
  wide screens; now HEIGHT-locked to Base's `vid_conheight` (600) = `screenH/600`. SYSTEMIC (every
  panel's text shrinks to Base size) — verify ALL panels, not just centerprint.
- **#18 radar scaling** — the square minimap image was stretched onto raw (non-square) map bounds;
  now drawn over the square-extended + 1/64-padded bounds (`RadarPanel.cs`). Verify on a non-square map.
- **#16 mouse capture on windowed load** — the cursor was grabbed at load start and held through the
  whole load; now free for the whole load, captured the frame the player spawns (Shell + NetGame).
- **#17 death scoreboard (FIX v2)** — v1 misdiagnosed (thought the radar bled through a centered board; the
  BOARD ITSELF is the tiny top-left box). Real cause: standalone scoreboard drew at `Cfg.SizePx` 64×64 default
  (`Configure` set Control size but not `Cfg`), and `MouseFilter=Stop` (bypassed `RegisterPanel`) made its
  large input rect eat mouse-look. v2: `Configure` sets `Cfg`; scoreboard created `MouseFilter=Ignore`.
- **#9a items droptofloor (FIX v2)** — first pass helped but the Stormkeep mega stayed low; trace switched
  from `NoMonsters` to Base-faithful `MoveFilter.Normal` (hits SOLID_BBOX platforms too).
- **#19 auto-pause local game** (feature, default-on) — `Shell.SyncAutoPause` freezes a solo listen game on
  menu/console/focus-loss; never when a remote client is connected (`NetGame.HasRemoteClients` via
  `ServerNet.ConnectedPeerCount>1`). Test: menu/console/alt-tab each freezes the bots; closing/returning resumes.
- **#20 `dom_team` model holders leak into wrong gametypes** — RUNTIME-verified: 5 `dom_team` edicts (dom_*.md3)
  were un-registered → never retired → networked + rendered (the Stormkeep-DM cluster). Now routed through the
  objective sink and retired in all modes. Eyeball the mortar room in DM: the cluster should be GONE.

**Still open:** **#6** jumppad magenta (needs a targeted jumppad screenshot — not visible from spawn,
doesn't repro headless), **#8 trail** (moving-flag `glow_trail` — a new particle system, the biggest
remaining piece), **#9 (spawn points)** (marker-vs-player check), **#9a mega** (still low — needs the
nudge-out-of-solid; see #9), **#14** flag jitter (user-deferred).

---

## Open

### 1. Casings spam their bounce sound while resting on the ground — FIXED ✓
- [x] **Status:** Fixed + user-confirmed in playtest (branch `claude/playtest-fixes`).
- **Symptom:** Shell casings play their bounce sound *continuously* while sitting on the
  ground, and keep playing until the casing despawns.
- **Expected:** The bounce sound should fire **once per actual bounce** (per ground
  impact), then go silent once the casing is at rest — matching Base.
- **CONFIRMED root cause:** the port's rest-latch is too strict, so a casing that never
  latches "on ground" keeps re-playing the sound. Base `Casing_Touch`
  (`common/effects/qc/casings.qc:113-145`) plays only when `time >= nextthink && speed > 50`,
  bumps `nextthink = time + 0.2` on EVERY touch, and once the casing `IS_ONGROUND` the
  MOVETYPE_BOUNCE physics stops generating touches → permanently silent at rest. The port's
  `CasingBody` (`game/client/ShellCasings.cs`) only steps/sounds `while (!_onGround)` (:262) —
  correct — but `_onGround` only latches on a bounce with `n.Z > 0.7 && speed < 20` (:328 world
  path) or `speed < 20` (:350 FloorZ path). On a sloped/irregular contact (`n.Z ≤ 0.7`) it
  NEVER latches, so `StepTic` runs every tic and calls `BounceCasingSound` (:320) on every
  contact; the sound throttles to `_age >= _nextSoundAt (+0.2)` (:374-381) → ~5×/s, reading as
  "continuous" until the 10s/30s lifetime ends. The default is the world-collision path
  (`TraceHook` wired, `EffectSystem.cs:189-193`), so slope/edge rests are common (Stormkeep
  etc.). Sub-divergences: (a) the port bumps `_nextSoundAt` only inside the `speed>50` branch
  (Base bumps `nextthink` on every touch); (b) no "onground ⇒ stop" for non-flat rests.
- **Fix direction:** make the casing go silent at rest like Base — extend `_onGround` to also
  latch on a low-speed contact regardless of `n.Z` (rest when post-bounce speed < the bounce-
  stop threshold), and/or bump `_nextSoundAt` on EVERY contact + add a resting flag that
  suppresses further sound. Keep the lifetime/0.2s throttle values (the gap is the missing
  rest-silence, not the window).

### 2. Shotgun-shells ammo box renders magenta — CONFIRMED root cause
- [ ] **Status:** Not started (root cause confirmed)
- **Symptom:** The shotgun ammo pickup renders with a magenta surface. Screenshot (11:53): the
  shells model itself renders fine but the **box around it** is magenta — it's the `Box01`
  surface of `a_shells.md3`.
- **Expected:** Correct material on the shells box (`textures/shellsammo.tga`).
- **CONFIRMED root cause:** the port's texture resolver is missing DarkPlaces' `textures/<name>`
  search-path fallback for **un-pathed** model-shader names. `a_shells.md3` has two surfaces:
  `Object01s02`→`shotgun2` (a real shader with a full-path `map` → resolves) and `Box01`→
  `shellsammo`. `shellsammo` has NO `.shader` entry and the model has no `.skin`, so
  `Md3Builder` resolves the bare name verbatim → `ResolveMaterial("shellsammo")` →
  `BuildPlainMaterial` → `LoadTexture("shellsammo")` → `VirtualFileSystem.ResolveImage("shellsammo")`.
  `ImageCandidates` (`game/.../VirtualFileSystem.cs:357-378`) only probes `override/<name>`,
  `<name>.tga/png/jpg`, `dds/<name>.dds`, `<name>.dds`, `<name>.tga.dds`, `<name>.pcx/wal` —
  **no `textures/<name>` variant** — but the real file is `textures/shellsammo.tga`, so every
  candidate misses → magenta fallback (`AssetSystem.cs:175-179,752-784`). DarkPlaces resolves an
  un-pathed name via `imageformats_other`, which includes `textures/%s.tga`
  (`image.c:1017-1031,1070`). Cells/Rockets do NOT hit this — each has a `.shader` naming a full
  `textures/…` path.
- **Fix direction:** add `textures/<stem>` fallback candidates (`.tga/.png/.jpg`, optionally
  `dds/textures/<stem>.dds`) to `VirtualFileSystem.ImageCandidates`, mirroring DP's
  `imageformats_other`. Apply the `textures/`-prefixed variants only when the stem has no leading
  `textures`/`gfx`/`locale` path segment (avoids a double `textures/textures/…` probe). One
  change fixes the shells box and any future bare model-shader name whose texture is under
  `textures/`.

### 3. Center notification text is too large — root cause identified (systemic HUD font basis)
- [ ] **Status:** Not started (mechanism confirmed; verify magnitude live)
- **Symptom:** Center-print / center notification text renders larger than it should.
  Screenshot (12:21) — *"You got the RED flag!"* fills a large band across mid-screen,
  visibly bigger than Base's centerprint.
- **Expected:** Match the font size Base uses for centerprint notifications.
- **Root cause (mechanism confirmed):** HUD font uses the WRONG reference basis. The port
  scales `hud_fontsize` (=11) by `viewport.X / refW` (`refW = hud_width>0 ? hud_width : 800`)
  against REAL pixel WIDTH in `HudPanel.Resolve` (`game/hud/HudPanel.cs:277-280`); centerprint
  then applies `Cfg.FontSize * BaseScale(1.3)` (`game/hud/CenterPrintPanel.cs:38,382`). Base
  pins HUD text to a HEIGHT-locked virtual canvas — `hud_fontsize` is absolute in the
  `vid_conwidth×vid_conheight` 2D canvas with `vid_conheight ~480` fixed (conwidth auto-widens
  for aspect), uniformly scaled to the screen, so text tracks screen HEIGHT not width
  (`centerprint.qc:259`, `cp_fontsize = hud_fontsize * CENTERPRINT_BASE_SIZE`). The port
  over-scales on wide/high-res displays; error grows with aspect (~6–7% at 16:9, up to ~40% at
  21:9 ultrawide).
- **Verify before fixing:** (a) MAGNITUDE — at 16:9 the basis error is only ~6–7%, which may
  under-explain "clearly too large"; check the actual display aspect/res live. (b)
  CENTERPRINT-SPECIFIC — the user singled out centerprint, so confirm the port's `BaseScale=1.3`
  equals Base's `CENTERPRINT_BASE_SIZE` constant (agent says it matches luma; verify it) — else
  there's an extra centerprint-only factor.
- **Fix direction:** reproduce Base's canvas mapping in the SHARED `HudPanel.Resolve` (render
  the HUD in a fixed virtual ~640×480 aspect-corrected canvas scaled uniformly to the viewport,
  OR at minimum scale `hud_fontsize` by `viewport.Y / ~480` instead of `viewport.X / 800`).
  SYSTEMIC — this rescales ALL HUD text, not just centerprint; verify every panel after.

### 4. Flag shield renders as a giant solid purple dome (wrong visual + maybe wrong timing)
- [ ] **Status:** Not started
- **Symptom:** A large purple/lavender translucent **hemisphere dome** appears over CTF flag
  bases when it shouldn't (or looks wrong when it should).
- **Screenshot evidence:**
  - (14:42) A clean shot: a big semi-opaque purple dome sits on the floor with a small
    **blue flag icon visible inside it** — i.e. the dome is the shield around a flag at its
    base, drawn as a solid dome roughly player-height-plus in radius.
  - (earlier batch) Multiple such domes over item/flag spawns with magenta "ITEM" waypoint
    text underneath — suggests it may draw for more than one target and/or at the wrong time.
- **Expected:** The Xonotic flag/capture shield is a subtle translucent energy shield with
  the shield SHADER (like the powerup shield), not a flat purple hemisphere — and it only
  shows under specific conditions.
- **CONFIRMED root cause:** THREE compounding defects in `Ctf.SpawnShieldEntity`
  (`Ctf.cs:1532-1554`):
  1. **No visibility gate (the main one).** Base draws the capture-shield model ONLY to a
     client who is *currently* capture-shielded and on the enemy team
     (`ctf_CaptureShield_Customize`, `sv_ctf.qc:320-326`, wired via `setcefc` at `:347`). With
     the default `g_ctf_shield_max_ratio 0` (`gametypes-server.cfg:338`) **nobody is ever
     shielded → the shield is invisible in normal play.** The port renders the model
     unconditionally (no Customize equivalent). (The port's own `CaptureShieldStatus`
     `Ctf.cs:2173-2197` also early-returns false at ratio 0, so the *push* never fires — but the
     *model* still draws.)
  2. **Wrong scale → full-size dome.** Model = `models/onslaught/generator_shield.md3` (a
     hemisphere dome). Base renders it at `scale = 0.5` (`sv_ctf.qc:352`). The port has **no
     `Entity.Scale` visual field** — only the collision bbox is scaled (`Ctf.cs:1548-1551`), so
     the dome renders at native full size.
  3. **Wrong material (opaque).** Base shader `ons_shield` (`scripts/onslaught.shader`) is a
     `tcGen environment` + additive-blend translucent energy field. The port has no shader-def
     for `ons_shield`, so `AssetSystem.ResolveMaterial` falls to `BuildPlainMaterial` → an
     **opaque** `StandardMaterial3D` (`AssetSystem.cs:138-167,175-199`), dropping the
     translucency → a solid purple surface. (`EF_ADDITIVE` blend only runs in the player/CSQC
     pass, not for world objective entities.)
  Assets are present (`ons_shield.tga`, `.skin`) — this is logic, not missing content.
- **Fix direction:** (a) gate the shield's visibility like Base (with default ratio 0 it should
  never show — simplest: only attach/network the shield model when it should actually be shown);
  (b) apply the 0.5 visual scale (needs an `Entity.Scale` networked field or a client special-
  case); (c) give `ons_shield` a proper additive/environment translucent material. Fixing (a)
  alone removes the dome in normal play.

### 5. CTF flags appear on maps in game modes where they shouldn't
- [ ] **Status:** Not started
- **Symptom:** CTF flags spawn on maps even when the current game mode isn't CTF.
- **Expected:** Flags only present in CTF (their spawn should be gated by the active
  gametype).
- **CONFIRMED root cause:** the port's flag spawnfuncs lack Base's `if (!g_ctf) delete()`
  gate, so the placeholder edict survives outside CTF. Base starts every `item_flag_*`
  spawnfunc with `if(!g_ctf){ delete(this); return; }` (`sv_ctf.qc:2731/2749/2767/2785`;
  neutral also requires one-flag `:2803-2804`). The port registers the spawnfuncs
  unconditionally (`MapObjectsRegistry.cs:270-274`) and routes them to a `Sink` that is the
  real `SpawnFlag` only when `GameType is Ctf`, else `_ => null` (`GameWorld.cs:3290-3297,3487`).
  The null sink no-ops the spawn, BUT `SpawnFuncs.TrySpawn` still returns `true`
  (`EntityClasses.cs:206-210`) so the placeholder `item_flag_team*` edict is counted and
  **never removed** (`GameWorld.cs:4472-4475`) — the placeholder deletion (`RetirePlaceholder`)
  only runs *inside* the CTF sink, which never fires here. `MapEntityFilter` doesn't hardcode
  `item_flag_*` as CTF-only (`MapEntityFilter.cs:74-95`), and a map `model` key is copied onto
  the edict (`GameWorld.cs:4546`) so it can render.
- **Fix direction:** add Base's gate at the source — in `GametypeObjectiveSpawns.FlagTeam1..4`/
  `FlagNeutral` (or in `Emit`), if the active gametype isn't CTF, `Api.Entities.Remove(e)` and
  return (neutral also requires one-flag). Don't rely on a no-op sink to drop the placeholder.

### 6. Jumppads on `space-elevator` render magenta — REAL but NOT reproduced statically (needs live repro)
- [ ] **Status:** Not started (needs a live repro to diagnose)
- **Symptom:** The jumppad FX surfaces on `space-elevator` render magenta (seen in playtest
  screenshots). The visible surfaces are bezier patches textured `effects_jumppad/jumppadfx1_a`
  /`_b` (the `trigger_push` brushes are `common/trigger` nodraw and correctly skipped).
- **Investigation result:** under the CURRENT code + shipped assets this does NOT reproduce as
  magenta. The compiled `maps/space-elevator.bsp` (in `xonotic-20230620-maps.pk3`, present in
  `assets/data`) stores the shader name WITH the `textures/` prefix
  (`textures/effects_jumppad/jumppadfx1_a`), matching the key in `scripts/effects_jumppad.shader`;
  the shader parses cleanly (single stage, `blendfunc`, `tcMod scroll`) → the animated
  `ShaderMaterial` path; and the texture (shipped only as
  `dds/textures/effects_jumppad/jumppadfx1_a.dds`, a valid DXT5) resolves via the existing
  `dds/<stem>.dds` candidate. A sweep of ALL 91 BSP shader names produced ZERO magenta. So it
  does NOT exercise the #2 `textures/` gap (its name is fully pathed) — **#2 and #6 are NOT the
  same bug.**
- **Most likely (unconfirmed) live-only causes:** (a) the map/texture loaded from a DIFFERENT
  dataset than `assets/data` — e.g. a stale `dist/<preset>` export or an older pk3 lacking the
  `dds/` tree (jumppad DDS absent + no `.tga` → magenta); (b) a runtime `CreateFromData`
  rejection of that specific DDS → null image → fallback.
- **Next step:** LIVE repro — run `--host space-elevator`, confirm the jumppad renders magenta,
  and capture which surface + the `[AssetSystem]`/`[MapLoader]` console output for that texture;
  that distinguishes (a) vs (b). (The #2 `textures/` fallback would only help #6 if the failing
  environment stores the jumppad name un-pathed and ships only `textures/*.tga` — the shipped
  pk3 does neither.)

### 7. CTF flag on a carrier rotates the WRONG way (reversed yaw) — CONFIRMED root cause
- [ ] **Status:** Not started (root cause confirmed)
- **Symptom:** When the flag is attached to a carrier, turning left makes the flag rotate
  right and vice-versa; it should sit fixed directly behind the player so it never blocks
  their view.
- **Screenshot evidence:** (17:08, 17:03) from the carrier's own view the flag is gray,
  oversized, and juts across the middle of the screen — clearly not parked behind the player
  and not oriented consistently with the carrier's facing.
- **CONFIRMED root cause:** an angle/rotation-convention inconsistency in the port (exactly
  as suspected). `EntityNode.SyncFromEntity` (`game/EntityNode.cs`) has **two** yaw→Godot
  paths that disagree by a sign:
  - full-basis path (line ~104, pitched/rolled entities): `Basis = (ToGodot(fwd),
    ToGodot(up), ToGodot(right))` ⇒ **+θ** about Godot +Y (the proven convention, same as
    `FirstPersonView`/camera).
  - yaw-only path (line ~110, players/items/**flags**): `Rotation.Y = -DegToRad(yaw)` ⇒
    **−θ** about Godot +Y. This is the "negated-yaw Euler [that] silently flips handedness"
    the camera code (`NetGame.cs:5775`, `FirstPersonView.cs:21,503`) was already fixed for.
  - The carried flag's **position** is computed in Quake space in `Ctf.cs:1888-1889`
    (`QMath.AngleVectors(carrier yaw)` → orbits with +θ), but its **orientation**
    (`cfe.Angles.Y = carrier yaw`, `Ctf.cs:1891`) renders through the −θ yaw-only path →
    position and facing counter-rotate → the visible reversal.
  - NOTE: the oversized/blocking placement in the screenshots suggests the FLAG_CARRY_OFFSET
    magnitude / attach point may also need review once the rotation is fixed (Base parks it
    small and behind).
- **Fix (implemented):** v1 (a server-side `OrientViaAngleVectors` render-hint → EntityNode
  full-basis path) DID NOT WORK — the self-connecting listen server renders flags from network
  snapshots (`ClientEntityView` copies the networked angles onto a proxy `Entity`), so a
  server-only render bool never reaches the client. v2 (shipped): pre-negate the carried flag's
  NETWORKED yaw in `Ctf.cs` (`cfe.Angles.Y = -carrierYaw`) so the client's −θ yaw-only path lands
  on +θ, matching the Quake-space carry position. The flag's angles are render-only while carried
  (nothing reads them for logic); the at-base flag keeps its true spawn angles.
- **Documented:** general gotcha in `planning/COORDINATE_CONVENTIONS.md` + agent memory. KEY
  lesson: on the self-connecting listen server, render-only entity fields set server-side do NOT
  reach the client renderer — only NETWORKED fields (origin/angles/effects/…) do.

### 8. CTF flags aren't team-colored and have no particle trail
- [~] **Status:** Team color IMPLEMENTED (v2 — real colormap, needs an eyeball); glow/dlight left off (Base default); moving-flag trail DEFERRED (new particle system).
- **Symptom:** CTF flags render **gray** instead of Red/Blue, and there's no particle trail
  following the flag as it moves.
- **Screenshot evidence:** (17:08) the carried flag is plainly gray/desaturated; the bright
  light near it reads **white/uncolored** where Base would emit a team-colored glow/dlight.
- **Expected:** Team-colored flag (red/blue skin + team-colored glow/dlight) + the
  moving-flag particle trail, matching Base's CTF effects.
- **CONFIRMED root cause:** three parts, of which two are real bugs:
  1. **Gray flag (real).** Base team color comes from `flag.glowmod = Team_ColorRGB(team)`
     (always set, `sv_ctf.qc:1387`) + `flag.colormap |= RENDER_COLORMAPPED` (`:1388-1389`) — the
     banner is a colormap-tinted "shirt" texture, like a player skin. The port sets only the
     integer `e.Skin` (`Ctf.cs:1483-1489`), never glowmod/colormap; there's a single skin file,
     so team color is NOT chosen by skin index. Worse, the client ignores `Entity.Skin` (always
     loads skin 0) AND applies colormap/glowmod tint only to **player** models
     (`ModelTint.ApplyAppearance` gated behind `st.IsPlayerModel`, `ClientWorld.cs:1289,1307`) —
     so a world flag can't be team-tinted at all.
  2. **White/uncolored glow (NOT a bug by default).** Base's team dlight is `EF_RED/EF_BLUE`
     gated by `g_ctf_dynamiclights`, which **defaults to 0/off** (`sv_ctf.qc:1444-1454`,
     `gametypes-server.cfg:354`). So no team dlight is Base-faithful; the white glow the user
     sees is the model's `glow.tga` self-illum mesh, not a dlight. The port correctly skips the
     dlight — leave it off.
  3. **No moving-flag trail (real).** Base's trail is the engine `glow_trail`: `glow_color =
     <team palette idx>`, `glow_size = 25`, `glow_trail = 1`, gated by `g_ctf_flag_glowtrails`
     which **defaults to 1/on** (`sv_ctf.qc:1428-1440`, `gametypes-server.cfg:352`). The port
     sets none of these and has no `glow_trail` field/system (`Ctf.cs:1486-1489`). The
     effectinfo `_cap`/`_pass` particles are event bursts (port fires `_cap` at `Ctf.cs:787`),
     NOT the continuous carry trail.
- **Fix (implemented — team color):** v1 (a per-team albedo modulate on the flag meshes) did
  NOTHING — the flag banner is a **ShaderMaterial**, not a StandardMaterial3D (its `banner_shirt.tga`
  companion makes `AssetSystem.TryBuildSkinMaterial` auto-compile it to `PlayerSkinShader`), and the
  albedo tint only touched `BaseMaterial3D`. v2 (shipped): the banner already has the colormap shader
  with `shirt_color` defaulting to black (→ gray); drive the team colormap onto the flag exactly like
  a player — `ModelTint.ApplyColormap(node, (int)e.Team)` in `ClientWorld.DriveCsqcModelHooks`, gated
  by `IsCtfFlagModel`. The team rides `Entity.Team` (networked; `ServerNet.cs:2257` `Colormap =
  (int)e.Team`). The banner shirt-tints red/blue/…, the pole (no `_shirt`) is untouched — matching
  Base's banner-only colormap.
  - **v3 (shipped): fixed "red flag rendered PINK."** `Entity.Team` holds the NUM_TEAM_* color CODE
    (`Teams`: Red=4/Blue=13/Yellow=12/Pink=9), but `ModelTint.TeamColor` wants the colormap low nibble
    (1=red..4=pink) — so `(int)e.Team`=4 hit `TeamColor(4)` = pink `(1,0,0.5)`. Now mapped via
    `FlagTeamNibble` (4→1 / 13→2 / 12→3 / 9→4). ⚠ **The SAME code-vs-nibble mismatch likely mis-colors
    PLAYER shirt/pants in team modes** (`ResolveForcedColormap` returns the raw `Entity.Team` code too) —
    invisible in 0-bot testing; flagged for a team-play check (possible separate bug).
- **Still deferred:** (b) dlight stays off (Base default `g_ctf_dynamiclights 0` — the white glow is
  `glow.tga` self-illum); (c) the moving-flag `glow_trail` (`g_ctf_flag_glowtrails`, default on) — a
  team-colored continuous particle trail, a genuinely new particle system.

### 9. Items (and mapper-floating spawn points) don't align to the floor
- [~] **Status:** (a) items = general items FIXED, but **Stormkeep mega STILL low** (v2 `Normal` didn't fix it);
  (b) spawn points = still open (see below).
- **FIX (items):** added `StartItem.DropItemToFloor` (small Q3 box `±15`, `mins.z..mins.z+30`, tracebox down
  `-4096`, rest at impact, keep origin if start-solid) from `SetupPermanent`. Helped most items; `NoMonsters`→
  `MoveFilter.Normal` (v2) did NOT fix the Stormkeep mega. The mega DOES go through `DropItemToFloor` (confirmed).
- **Leading hypotheses for the residual mega (needs live check on that spot):** (1) **start-solid** — Base's
  `DropToFloor_QC` does `nudgeoutofsolid` BEFORE and AFTER the tracebox; the port's `DropItemToFloor` has NO
  nudge, so a mega placed slightly embedded hits `tr.StartSolid` → early-return → keeps the embedded origin.
  Adding a nudge-out-of-solid (there's prior art in the codebase) is the likely real fix. (2) **model pivot** —
  even resting the collision box on the floor, the mega mesh may extend below `mins.z` (visual-only clip).
- **Symptom:** Items don't settle onto the floor correctly. On **Stormkeep** the mega
  health is clipped INTO the floor in XonoticGodot but sits correctly in original Xonotic —
  and the `.ent` files are identical, so it's the port's placement logic. Related: spawn
  points a mapper placed floating in the air still float in XonoticGodot, whereas Base
  drops them to the ground.
- **Expected:** Base's drop-to-floor behavior — items push up out of the floor if slightly
  clipping, and lower to rest on the floor (with the item's offset) if placed above it;
  spawn points snap to the ground too.
- **CONFIRMED root cause (items):** the port never runs an explicit drop-to-floor for world
  items — it just sets `MoveType.Toss` and lets the TOSS integrator settle the FULL bbox at
  first contact, so a wide item like the mega embeds. Base drops every `FL_ITEM` at spawn via
  `DropToFloor_QC` (`server/world.qc:2303-2425`): shrink to a small Q3 box
  (`-15,-15,mins.z`/`15,15,mins.z+30`), `nudgeoutofsolid`, `tracebox` down `-4096`, set origin,
  restore the full bbox, then `nudgeoutofsolid` AGAIN to push the full box up out of solid —
  called from `StartItem` (`server/items/items.qc:1138-1148`). Stormkeep's mega is at
  `origin "1696 -256 -32"` with no spawnflags (`stormkeep.map:19955-19957`) — it *relies* on
  droptofloor. The port's `StartItem.SetupPermanent` (`Items/StartItem.cs:280-290`) sets only
  `MoveType.Toss`; `PhysicsToss` (`Engine/Simulation/MoveTypePhysics.cs:105-257`) rests the full
  bbox and, on start-solid, zeroes velocity + sets OnGround **leaving it embedded** (:170-181).
  NOT a networking bug (the snapshot re-sends the real embedded origin). The port already has
  reusable box-trace-down drops it ignores here: `Domination.DropPointToFloor`
  (`Domination.cs:404-411`), `MapModels` ALIGN_BOTTOM (`MapModels.cs:177-181`).
- **Spawn points (#9b) — LIKELY NOT A BUG (needs a Base A/B to close):** the floating "marker" is the
  **spawn-point idle glow** — `SpawnPointParticles` (`game/client/SpawnPointParticles.cs`), a faithful port of
  CSQC `Spawn_Draw` (`client/spawnpoints.qc:20`, gated by `cl_spawn_point_particles`). It draws at each spawn
  point's RAW `e.Origin` (`:79`). Base does NOT droptofloor spawn entities (`relocate_spawnpoint` only does
  `+'0 0 1'` + `move_out_of_solid`, `spawnpoints.qc:117-148`), and Base's `Spawn_Draw` draws at the same raw
  origin — so a mapper-placed-high spawn floats its glow **in Base too**. So this is most likely EXPECTED, not a
  port bug.
- **User data (2026-07-04):** one floating marker is in the **Hagar room on Stormkeep**; "sometimes on the
  ground, mostly floating." The "sometimes on the ground" is almost certainly the SEPARATE transient
  team-colored **spawn-flash burst** emitted at the GROUNDED player origin on spawn (`SpawnSystem.cs:851`,
  `EffectEmitter.Emit("SPAWN", p.Origin)`) — a different effect from the persistent idle glow. Two effects, one
  perceived "marker."
- **To close #9b:** Base A/B — with `cl_spawn_point_particles 1`, does REAL Xonotic float the Hagar-room spawn
  glow the same way? If YES → not a bug (mapper placed it high; matches Base). If Base grounds it, check whether
  Base's spawn-point net entity sends a grounded origin the port doesn't. Only then is a fix warranted.
- **Fix direction:** items — add a real `DropToFloor_QC` and call it from
  `StartItem.SetupPermanent` (+ the keys path) for non-`noalign` items instead of leaning on
  TOSS; reuse the existing box-trace-down but faithfully reproduce Base (small Q3 box drop →
  restore full bbox → nudge up out of solid), done at spawn before the first snapshot. Spawn
  points — confirm marker-vs-player in-game first; if the marker, apply Base's load-time
  `relocate_spawnpoint` nudge; if the player hangs, ensure spawn-tick gravity. Do NOT add a
  spawnpoint droptofloor that Base lacks.

### 10. HUD panel backgrounds have square corners (missing Base's angled/beveled corners)
- [ ] **Status:** Not started
- **Symptom:** HUD panel backgrounds render with plain **square corners**. In Base the panel
  backgrounds have the signature angled/"triangular" cut corners (called out specifically
  for the **ammo panel**, but the user notes **most** port HUD panels look off the same way).
- **Expected:** Match Base's HUD-skin panel backgrounds — the beveled/angled corners that
  come from the active HUD skin's border images.
- **Hypothesis (unverified):** Base draws each panel background as a **9-slice bordered
  picture** composited from the active HUD skin's border/corner assets (`HUD_Panel_DrawBg` →
  `draw_BorderPicture`, images under `gfx/hud/<hud_skin>/border_*`); the angled corners are
  literally the skin's corner tiles. The port appears to draw a plain filled/rounded rect
  instead of compositing the skin's 9-slice border, so every panel comes out square. This is
  a systemic HUD-background renderer gap, not a per-panel tweak.
- **First look:** the port's HUD panel-background drawing code (the shared panel-bg draw in
  the HUD manager / panel base) and the `hud_skin` asset wiring; compare to Base `hud.qc`
  `HUD_Panel_DrawBg` + `draw_BorderPicture` and the skin border/corner images. See
  `planning/HUD_PARITY_CONTRACT.md`. Because it's systemic, fixing the shared bg renderer
  should correct most panels at once.

### 11. Weapon HUD panel background doesn't auto-size to contents
- [ ] **Status:** Not started
- **Symptom:** The weapon HUD panel background stays **fully expanded** (full weapon grid)
  even when the player owns only a few weapons; it should auto-size to fit just the shown
  weapons.
- **Expected:** Base sizes the weapon panel background to the weapons actually drawn (with
  `hud_panel_weapons_onlyowned` it shows only owned weapons and the background shrinks to fit
  the resulting grid).
- **Hypothesis (unverified):** The port lays out/draws the full fixed weapon grid + full-size
  background regardless of owned-weapon count, ignoring the only-owned / dynamic-size
  behavior. Relevant Base knobs: `hud_panel_weapons_onlyowned` (+ the dynamic panel sizing
  that derives rows/cols → panel dimensions from the count of icons drawn).
- **First look:** the port's weapon HUD panel under `game/hud/` (layout + background sizing);
  compare to Base `HUD_Weapons` (owned-weapon count → grid → panel size) and confirm the
  port honors `hud_panel_weapons_onlyowned` and recomputes the bg rect from the drawn set.

### 12. Item respawn-countdown waypoint sprites attached too broadly — CONFIRMED root cause
- [ ] **Status:** Not started (root cause confirmed)
- **Symptom:** Far too many items show a waypoint sprite (the generic "ITEM" marker) — e.g. a
  small health shard gets one when it shouldn't. **The magenta look is incidental**, not the
  bug: it's just the miss-color of the generic/unmapped icon on items that were never
  supposed to carry a marker.
- **Expected:** Only the items Base configures for the itemstime / respawn-countdown display
  get a marker (the "major" pickups — Mega/Big Health + Armor, powerups), and even those are
  spectator-only except in warmup / `sv_itemstime 2`. Small items (shards, small health,
  ammo) get **no** waypoint sprite.
- **CONFIRMED root cause:** the port dropped Base's itemstime gate. Base (`items.qc:329`,
  `Item_ScheduleRespawn`) only routes an item onto the countdown-waypoint path when
  `(set_itemstime || MUTATOR_CALLHOOK(Item_ScheduleRespawn, e, t)) && (t - ITEM_RESPAWN_TICKS)
  > 0`. The port's `ItemPickupRules.ScheduleRespawnIn`
  (`src/XonoticGodot.Common/Gameplay/Items/ItemPickupRules.cs:661-673`) **omits the
  `set_itemstime` term** — its own comment states it "routes EVERY long respawn (>
  ITEM_RESPAWN_TICKS) through the countdown-waypoint path (it doesn't gate on the itemstime
  set)." So every item with a long-ish respawn gets a marker. It IS on by default for
  everything.
- **Audit / fix direction (for later):** restore the `set_itemstime` gate. Enumerate which
  item defs are in Base's itemstime set (what makes `set_itemstime` true in
  `Item_ScheduleRespawn`), confirm the `SPRITERULE_SPECTATOR` visibility the port already
  models for Mega/Big (`EntityItemState.cs:99-105`), then gate `ScheduleRespawnIn` so only
  those items take the countdown-waypoint branch and everything else (shards/small items)
  falls through to the no-waypoint branch.
- **First look:** `ItemPickupRules.cs:661-673` (the missing gate) + `EntityItemState.cs:99-105`
  (WaypointAttached / SPRITERULE_SPECTATOR) vs. Base `Item_ScheduleRespawn` /
  `Item_RespawnCountdown` and the itemstime-set membership. Related: #2/#6 explain the magenta
  *look* (miss color), not the cause.

### 13. CTF capture announcer plays the wrong team's "scores" voice — FIXED
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — reported + fixed in playtest round 1.
- **Symptom:** On a CTF capture, the "red scores" vs "blue scores" announcer voice is reversed
  (a red capture plays blue's sound and vice-versa).
- **CONFIRMED root cause:** `Ctf.FlagAnnounceSound(carried.HomeTeam, "CAPTURE")` (`Ctf.cs:782`,
  `:1856`) keyed the capture VOICE on the CAPTURED (enemy) flag's team. Base `ctf_Handle_Capture`
  (`sv_ctf.qc:636`) keys it on the CAPTURING team — `_sound(player, …, flag.snd_flag_capture)`
  where `flag` is the base flag the carrier touched (the capturer's OWN team; `DIFF_TEAM(player,
  flag)` is false). You capture the enemy flag at your own base, so the captured flag's team is
  the OPPOSITE of the scoring team → the sounds swap.
- **Fix:** key the capture sound on `(int)player.Team` (the capturing/scoring team). The
  centerprint stays on the captured flag's team (`carried.HomeTeam`), matching QC's
  `APP_NUM(enemy_flag.team, CENTER_CTF_CAPTURE)` — only the SOUND was wrong. TAKEN / DROPPED /
  RETURNED correctly stay keyed on their own flag's team.

### 14. Carried flag jitters/lags behind the player at high speed (no client prediction)
- [ ] **Status:** TODO only — do NOT fix yet (per playtest round 2).
- **Symptom:** Moving fast and looking backward, the carried flag jitters behind the player.
- **Analysis (user + code):** the local player is CLIENT-PREDICTED, but the carried flag is rendered
  from the delayed/interpolated network snapshot (`ClientEntityView` proxy) at its server-updated
  position — it isn't attached to the LOCAL predicted player client-side, so it trails/jitters
  relative to the smoothly-predicted view. Same render path as #7.
- **First look:** client-side, attach/predict the carried flag to the LOCAL predicted player (when
  the local player is the carrier) instead of rendering it purely from the interpolated snapshot.
  Netcode/prediction layer (PredictionBuffer / ClientEntityView / NetGame). Deferred by request.

### 15. CTF flag spawns partway sunk into the ground — FIXED
- [x] **Status:** Fixed (branch `claude/playtest-fixes`).
- **Symptom:** On most maps the CTF flag spawns partway sunk into the ground.
- **CONFIRMED root cause:** the port's `Ctf.SpawnFlag` placed the flag at the raw map origin. Base
  `ctf_FlagSetup` (`sv_ctf.qc:1426`) lifts it by `FLAG_SPAWN_OFFSET = '0 0 1' * (PL_MAX_CONST.z − 13)`
  = a constant **32qu** (PL_MAX_CONST.z = 45), so the flag model rests ON the floor. It's a fixed
  lift, NOT a droptofloor.
- **Fix:** add `(0,0,32)` to the origin at the top of `SpawnFlag` (flows into the networked origin +
  the stored `HomeOrigin`, so initial spawn and respawn both carry it).

### 16. Windowed mode captures the mouse while loading (can't move out of the window) — FIXED
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — verify in windowed mode.
- **Symptom:** Loading into a match in WINDOWED mode captures/holds the mouse immediately, so the
  cursor can't leave the window — unlike the menu, which lets the mouse move out freely.
- **Root cause:** the pointer was grabbed at load START and held through the whole load. THREE sites
  captured during the load screen: `Shell.EnterMatchView()` (called right after `ShowLoadingScreen`,
  Shell.cs:500/536), `NetGame._Ready` at 90% (`NetGame.cs:507`), and the per-frame mouse reassert
  (`NetGame.cs:3294`, `SetWantCapture(!UiOwnsCursor)`) which runs every frame through the connect/join
  handshake while the loading screen is still up.
- **Fix:** keep the cursor FREE for the whole load and grab it the frame the local player spawns —
  matching DP (you can alt-tab / mouse out of a windowed game while it loads). `EnterMatchView` +
  `NetGame._Ready` now `SetWantCapture(false)`; the per-frame reassert is gated on `LoadingScreen is
  null`. `LoadingScreen` goes null exactly when the player spawns (`NetGame.cs:3093`) and the reassert
  runs later in that same `_Process`, so mouse-look is captured on the exact spawn frame; after the
  first spawn `LoadingScreen` stays null so steady-state behaviour is unchanged.

### 17. Death scoreboard: mis-rendered + stuck-on-after-respawn + mouse dead — FIXED v3 ✓ (user-confirmed)
- [x] **Status:** FIXED + user-confirmed in playtest (branch `claude/playtest-fixes`). v1 wrong; v2 fixed the
  RENDER; v3 fixed the STUCK/mouse (the real remaining bug, exposed once v2 made the board visible).
- **v3 root cause (CONFIRMED) — the "stays on when you respawn" + "mouse dead while dead":** the client's
  `deadNow` = `RespawnTimeStat != 0`. The server maintains that stat in `DeadPlayerThink` (`GameWorld.cs:3679`,
  runs ONLY while dead), but the alive-(re)spawn path `ClientManager.Spawn` NEVER reset it (only
  `PutObserverInServer` + DeadPlayerThink's deny/silent arms ever zero it — verified repo-wide). So after a
  respawn the stat kept its last dead value → client thought it was PERMANENTLY dead → death scoreboard stuck
  on + dead-view/mouse behaviour persisted. Was fully MASKED while the board mis-rendered as a 64×64 box (v2
  fixed the render, which is what made it visible). **Fix v3:** `ClientManager.Spawn` now clears
  `RespawnTime/RespawnTimeMax/RespawnFlags/RespawnTimeStat` after `PutPlayerInServer` (mirrors the observer
  clear). Note: mouse-look isn't gated on death in code (only `Captured && !intermission`), so it should return
  the moment the client sees itself alive — this fix is expected to resolve BOTH.
- **Symptom:** On death the CTF **death scoreboard** (auto-shown, `NetGame.UpdateScoreboard` `deathScoreboard`)
  appears CRUNCHED into a tiny top-left box (all text overlapping — "CT", "Map vorix", "kills retur caps
  score", "Red"/"Blue"), STEALS mouse-look, and doesn't go away. NOT the map-vote (that's intermission-gated
  server-side + fullscreen — ruled out in code).
- **Fix v1 was WRONG (misdiagnosis):** I assumed the scoreboard was correctly centered and the top-left RADAR
  bled through it, so I wired the scoreboard cross-fade (`ScoreboardFade`) + faded the standalone radar. The
  screenshot proved the SCOREBOARD ITSELF is the tiny top-left box. (The fade wiring is still a valid latent
  fix — kept — it just wasn't this bug.)
- **CONFIRMED root cause (v2):** the standalone `_scoreboard` (`NetGame.cs:1710`, added straight to `hudLayer`)
  is placed via `HudPanel.Configure(centeredRect)` (`LayoutScoreboard`), which sets the Control's
  `Position`/`Size` but NOT `Cfg` — and it is NEVER `LoadConfig`'d. The DRAW reads `Size2 = Cfg.SizePx`, whose
  default is **64×64 at (0,0)** (`HudPanel.cs:168`). So it DRAWS 64×64 top-left while its INPUT rect (Control
  `Size`, the real centered rect) is large. Plus: added straight to `hudLayer`, it bypasses
  `HudManager.RegisterPanel` (which sets `MouseFilter=Ignore`), so it defaults to `Stop` and its large centered
  input rect EATS the captured mouse-look before `_UnhandledInput` = "steals mouse" (and the "won't go away"
  feeling = mouse trapped).
- **Fix v2:** (1) `HudPanel.Configure` now also sets `Cfg = Cfg with { PosPx, SizePx }` so `Size2` (the draw)
  matches the configured rect → scoreboard renders centered/large. (2) `_scoreboard` created with
  `MouseFilter = Ignore` so it never eats input. (Radar left as-is: top-left when small doesn't cover the
  captured-cursor centre, and its Maximized clicks route through `_UnhandledInput`.)
- **Watch on re-test:** confirm the scoreboard is centered, mouse-look works while dead, and it clears on
  respawn. If a fresh window-resize mid-match mis-sizes it, `LayoutScoreboard` is "called once at setup" — may
  need a resize re-call (separate, minor).

### 18. Radar minimap image scaled/positioned wrong on non-square maps — FIXED
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — needs an eyeball on a non-square map.
- **Symptom:** The radar minimap doesn't scale correctly vs Base (the map image is stretched / off-center
  relative to the blips).
- **CONFIRMED root cause** (deep-dive agent): the shipped minimap image is a SQUARE (`radarmap.qc:379`,
  512×512) that LETTERBOXES the (usually non-square) map inside a square, 1/64-padded world AABB
  `[mi_picmin, mi_picmax]` (`get_mi_min_max_texcoords`, `util.qc:770-790`); the image maps `[0,1]` onto
  THAT square (`radarmap.qc:227`, `image.mins = mi_picmin`). Base draws the image over the square pic
  bounds; the port (`RadarPanel.cs:412-418`) drew the FULL `[0,1]` image over the RAW (non-square) map
  bounds → stretched on the short axis + drifting off the blips. The BLIP world→pixel scale is already
  correct (`scale2d = 1` algebraically cancels Base's division), so ONLY the image quad was wrong.
- **Fix:** draw the image quad over the square-extended + 1/64-padded bounds centered on the map center,
  UVs `[0,1]` — `RadarPanel.cs` now uses `sqHalf = max(exX,exY)*(0.5 + 1/64)` around the map center. Blip
  transform untouched.
- **Secondary (not fixed):** a map shipping a `gfx/<map>_radar` image uses Base's "clever" tracebox bounds
  (`get_mi_min_max_texcoords(1)`) — the port still feeds raw BSP bounds, a smaller residual on those maps
  only. The common `_mini` case is now correct.

### 19. Auto-pause a LOCAL game on menu / console / minimize (feature request) — FIXED v3 = slowmo (re-test)
- [x] **Status:** v3 (branch `claude/playtest-fixes`) — re-test. v1 was a no-op; v2 froze via dt=0; v3 (user's
  suggestion) uses Xonotic's OWN pause mechanism — the `slowmo` cvar. #28's cursor/pause-until-click confirmed.
- **v1 BUG (why nothing froze):** `SyncAutoPause` set `GetTree().Paused` correctly, but a tree pause only stops
  `Pausable` nodes — and NetGame is created with an EXPLICIT `ProcessMode = Always` (`Shell.cs` creation sites:
  pausing it would starve the ENet pump and time the link out). The Shell class doc claiming the match is "created
  Pausable" was stale from the GameDemo era (that node was deleted in the NetGame consolidation). So the pause
  froze nothing: NetGame._Process kept driving `_server.Tick(dt)` and the bots kept playing.
- **v3 FIX (the RIGHT mechanism — user asked "didn't Xonotic pause with slowmo?"):** yes — DP host_timescale /
  the `slowmo` cvar (`0 = paused`), which `ServerNet.ResolveSlowmo` reads into `Simulation.TimeScale` every
  StepWorld; the port's timeout pause already drives it (`GameWorld.cs:1274` `Cvars.Set("slowmo", …)`). So
  `NetGame.SyncLocalPauseSlowmo()` mirrors `GetTree().Paused` onto the server's slowmo (capture prior → set "0";
  restore on unpause; also restored in `Shutdown` so a paused teardown can't leave the next map frozen), and the
  server tick is a plain `Tick(dt)` again. Advantage over dt=0: slowmo is REPLICATED, so the client scales its
  own input cadence by it → prediction freezes IN LOCKSTEP with the server and no input queues up for an unpause
  burst — exactly how Xonotic's pause behaves. 2953/2953 tests green.
- **Known cosmetic:** purely client-side ambient FX not tied to sim time (some particles) may keep animating;
  the authoritative sim + prediction freeze. Acceptable / matches how a slowmo pause looks.
- **Not covered:** the `sv_threaded 1` path (default OFF) — its worker reads slowmo too via ResolveSlowmo, so it
  SHOULD honor the pause, but untested; verify if sv_threaded ever ships on.
- **IMPLEMENTATION:** `Shell.SyncAutoPause()` sets `GetTree().Paused = eligible && (menuOpen || ConsoleState.IsOpen
  || !windowFocused)`, where `eligible = MatchRunning && !mid-load && NetGame.IsListenServer &&
  !NetGame.HasRemoteClients && cl_autopause!=0`. `HasRemoteClients` = `ServerNet.ConnectedPeerCount > 1` (the
  local host is one peer; >1 = a remote is connected). Called from `OpenPauseMenu`/`Resume`, the focus edges in
  `_Notification`, and every frame in a new `Shell._Process` (Shell is `ProcessModeEnum.Always`, so it keeps
  running to RELEASE the pause; per-frame catches console open/close + a remote joining mid-pause → auto-unpause
  so the remote isn't frozen). The old unconditional `GetTree().Paused=true` in `OpenPauseMenu` (which would
  freeze a real MP server) is removed — the menu now only pauses a solo local game. `cl_autopause` unset = ON.
- **KNOWN CAVEAT (watch):** a VERY long focus-loss pause (alt-tab away for minutes) freezes the sim, which also
  freezes the loopback ENet service — could in theory drop the local client on return. Both peers freeze together
  so sim-time-based timeouts don't advance, but if ENet times out on WALL time this may need a loopback-timeout
  bump. Fine for typical brief pauses; revisit only if a long-alt-tab disconnect is observed.
- **FOLLOW-UP:** register `cl_autopause "1"` formally (../docs/reference/CVARS.md) — currently read with an unset=ON fallback.
- **Request:** when the host is a **local listen server with no remote players connected**, pause the game
  (freeze the sim) whenever the player opens the in-game **menu**, the **console**, or **minimizes / loses
  window focus** — like a single-player pause. Resume on return. If ANY remote client is connected, do NOT
  pause (a real multiplayer server must keep running).
- **Notes:** the pause plumbing already exists (`Shell.OpenPauseMenu`/`Resume` set `GetTree().Paused` +
  free the mouse for the in-game menu). Scope: (a) gate on "listen server AND 0 remote peers" (server knows
  its peer list — `ServerNet`/`GameWorld.Clients`); (b) also trigger on console-open and on focus-out
  (`MouseCapture.SetFocused`/`Shell._Notification` already sees `WM_WINDOW_FOCUS_OUT`), not just the menu;
  (c) probably a cvar (default ON for local) — check Base/DP's `host_framerate`/pause behaviour +
  `sv_pause`/`cl_pause`-style knobs for a faithful name. Resume must be clean (no input/step glitch).
- **First look:** `Shell.cs` (OpenPauseMenu/Resume/_Notification focus edges), `ConsoleOverlay` open/close,
  `NetGame`/`ServerNet` remote-peer count, `GetTree().Paused` gating.

### 20. `dom_team` model holders render in wrong gametypes (Stormkeep DM dom_* cluster) — FIXED (runtime-verified)
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — runtime-verified; needs a visual eyeball in DM.
- **It was `dom_team`, NOT `dom_controlpoint` (first agent was wrong).** A runtime probe (temporary `Log.Info` in
  `RetirePlaceholder` + at the lump model-copy, headless `--host stormkeep --gametype dm`) showed the 3
  `dom_controlpoint` edicts are correctly retired with EMPTY models — but FIVE **`dom_team`** edicts survive,
  each carrying a domination model: `models/domination/dom_{blue,red,unclaimed,yellow,pink}.md3`, clustered at
  `<328, -5xx, -96>` (the mortar room). That cluster is what the user saw.
- **ROOT CAUSE:** `dom_team` is a Base team-definition data holder (its `.model` is the per-team control-point
  model). The port doesn't consume `dom_team` at all (zero references), and it was **registered nowhere** as a
  spawnfunc — so it was spawned as a generic lump edict, got its map `model` key copied on (`GameWorld.cs:4551`),
  was never routed to the objective sink, never retired, and thus stayed live → networked → RENDERED in every
  mode. (The client-static-render theory was wrong: the entity is genuinely alive + networked. Base's Verified-
  by-user "no models in DM" holds because Base's `spawnfunc(dom_team)` does `if(!g_domination) delete`.)
- **FIX:** route `dom_team` through the sink like the other objectives — `GametypeObjectiveSpawns.DomTeam` =
  `Emit(e,"dom_team",team)`, registered for `dom_team` (`MapObjectsRegistry.cs`); the sink retires it in non-dom
  (default arm) AND in the Domination arm (a `dom_team` branch → `RetirePlaceholder`, never a control point —
  the port doesn't render dom_team in dom mode either). **Runtime-verified:** post-fix probe shows all 5
  `dom_team` now hit `RetirePlaceholder` in DM (were absent before). Removed → not networked → not rendered.
- **Note:** this is the general lesson from #5 extended — a mode-objective classname that the lump gives a
  `model` key but that ISN'T registered/gated will render in the wrong mode. If other maps show stray objective
  models, look for other un-registered `*_team`/objective classnames.

### 22. Scoreboard roster stale on first life (shows you as spectator + no bots until a death) — FIXED (test)
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — needs a playtest.
- **Symptom:** on your first life the scoreboard shows the PRE-JOIN state — you as a spectator, no bots — even
  after you + the bots have joined. It only refreshes once someone dies. (Found while testing #20.)
- **ROOT CAUSE:** the networked scoreboard block carries the full row set (roster + each player's spectator/team
  status), but the per-client re-send is gated on `GameScores.Version`, which only `Bump()`s on SCORE/label/team
  changes — NOT on roster/status changes. So a join (observer→player), a bot being added, a team switch, or a
  disconnect rebuilt the rows (`ServerNet.BuildScoreboard`) but never re-sent them; the client stayed on whatever
  block it first got (pre-join) until the first frag bumped the version.
- **FIX:** fold a cheap ROSTER signature (player count + each player's net id / spectator flag / team) into the
  version in `BuildScoreboard` — when it changes, `GameScores.MarkDirty()` bumps the version so the new roster
  re-sends. Ping/packet-loss deliberately excluded (they'd bump every frame). `MarkDirty()` added to `GameScores`.

### 23. Respawn countdown in the (death) scoreboard is frozen — FIXED (test)
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — needs a playtest.
- **Symptom:** the "Respawning in N…" countdown in the scoreboard / death scoreboard doesn't tick down.
- **ROOT CAUSE:** `ScoreboardPanel.IsDynamic = false` (repaints only on data change). `RespawnStat`/
  `RespawnServerTime` are fed every frame by `NetGame.UpdateScoreboard`, but they're plain auto-properties (no
  `QueueRedraw`), and the panel's `_Process` only forces a repaint while the fade is ANIMATING — so once the board
  is stably faded in, `DrawRespawn` never re-runs and the countdown freezes at its first value. (Latent; only
  visible now that #17 made the board render correctly.)
- **FIX:** `ScoreboardPanel._Process` now also `QueueRedraw()`s each frame while stably shown AND the local respawn
  countdown is live (`_active && RespawnStat != 0`), so the seconds tick. Cheap — only while dead with the board up.

### 24. Devastator plays phantom muzzle-flash/fire-sound while holding fire (guiding) — FIXED ✓ (user-confirmed)
- [x] **Status:** FIXED + user-confirmed in playtest (tap = flash, hold = silent guiding). Probe removed.
- **Symptom:** holding primary fire on the Devastator plays the muzzle flash + fire sound repeatedly even though
  no new rocket launches (correct: you're GUIDING the in-flight rocket). Reported behavior "doesn't refire" is
  actually CORRECT Base behavior; the phantom flash/sound is the bug.
- **Base behavior (`common/weapons/weapon/devastator.qc:461-472`, `wr_think`):** a rocket fires only when
  `rl_release` is set — i.e. the fire button was RELEASED since the last shot (`rl_release=1` only while primary
  is NOT held, cleared to 0 after firing). So **holding fire never refires**; it fires once, then guides the
  rocket while held (`W_Devastator_Think`). You release + re-press for the next rocket. (`guidestop 1` disables
  guiding → continuous fire; default 0.) There is NO auto-refire on explosion (the `ATTACK_FINISHED=time` in
  `W_Devastator_Explode` is only the ammo-low weapon-switch path).
- **ROOT CAUSE:** the port's client fire-prediction (#21, `cl_predictfire`, `NetGame.UpdateLocalFireFeedback`)
  uses a plain per-weapon refire clock that predicts a shot every `refire`s WHILE HELD — it doesn't model the
  Devastator's release requirement, so it played flash/sound for shots the server never fired. (The server side
  is correct — it really doesn't refire, which is why the user saw no new rocket.)
- **FIX (v3 + ammo fix; v1/v2 were confounded — see saga):** `PrimaryRefireRequiresRelease(wid)` — now a
  NETNAME-ONLY check ("devastator"), deliberately NO cvar read — gates the predict-on path to the PRESS EDGE only
  (`!_attackHeld`), so a sustained hold predicts nothing (matches Base). The `cl_predictfire`-off path already
  gated on the press edge.
- **THE SAGA (why v1/v2 "didn't work" — important for future prediction debugging):** all the arena re-tests were
  CONFOUNDED by a second, unrelated bug: `HasAmmoNow()` read only the numeric ammo count and ignored
  **IT_UNLIMITED_AMMO** — and the test arena (`g_weaponarena devastator`) grants unlimited ammo with a 0 rocket
  count, so `ammo=False` silently killed ALL prediction there, including the legitimate press-edge flash ("no
  flash at all, even tapping"). A safe in-build probe (`[dbg24s]`, since removed) proved it: every gate input
  green EXCEPT `ammo=False`; the release-gate itself (`req=True`) was armed and correct. Fixed `HasAmmoNow()` to
  pass on `p.UnlimitedAmmo || (p.Items & 1)` (bit 0 = QC IT_UNLIMITED_AMMO), mirroring Inventory's ammo arm. The
  guidestop cvar reads tried in v1 (`_sharedCvars`) / v2 (`Api.Cvars`) were unreliable from the client loop and
  are GONE (guidestop-1 servers just get a slightly under-predicted cadence — cosmetic).
- **LESSON:** when a prediction "doesn't fire," check ALL gate inputs with a probe before touching the gate —
  and don't test ammo-gated prediction in a weapon arena without the unlimited-ammo flag handled.
- **v4 addendum (2026-07-09, playtest: "devastator FX sometimes missing"):** the v3 press-edge-only gate was
  TOO strict — Base fires whenever the button is **held** with `rl_release` armed and ATTACK_FINISHED expired
  (wr_think: `rl_release || guidestop` + `weapon_prepareattack`), so a re-press during the cooldown tail +
  hold makes the server fire a rocket with NO press edge at that moment. Nothing was predicted, and both the
  networked (`OnSoundReceived` IsPredicted drop) and in-process (`SuppressOwnFireEffects`) copies of the real
  fire sound/flash are suppressed UNCONDITIONALLY for predicted weapons → a fully silent rocket. **Fix:** a
  client `rl_release` MIRROR (`_rlReleaseMirror`: armed on release + on equip like wr_setup, cleared on each
  predicted shot) replaces the press-edge condition — one predicted shot per release, fired the moment the
  ready clock lands, exactly QC's latch. Hold-to-guide still predicts nothing (#24 preserved). TWO more silencers
  fixed with it: the secondary DETONATE press was treated as a shot (Base fire&2 has no W_MuzzleFlash and no
  prepareattack) — it popped a phantom flash AND bumped the SHARED ready clock, swallowing the next primary's
  prediction (the fire→detonate→fire combo went silent) → devastator/minelayer excluded from SecondaryFiresShot;
  and prediction is now gated through the switch raise window (`_switchRaiseLeft`) like the drop, so a held
  trigger through a weapon switch doesn't flash before the server can actually fire.

### 25. Rocket ammo goes NEGATIVE under unlimited ammo (weaponarena) — Devastator FIXED; sibling sweep OPEN
- [~] **Status:** Devastator fixed (branch `claude/playtest-fixes`); the same bypass exists in OTHER weapons.
- **Symptom:** in `g_weaponarena devastator` (unlimited ammo, count 0) the ammo panel's rockets row counts
  NEGATIVE (-12, -36, …) as you fire.
- **ROOT CAUSE:** `Devastator.Attack` drained ammo with a raw `actor.TakeResource(AmmoType, …)` instead of the
  shared `DecreaseAmmo` (`WeaponFireGate.cs:384`), which is the port of QC `W_DecreaseAmmo` and early-outs under
  `IT_UNLIMITED_AMMO` (+ handles the reload clip). Base calls `W_DecreaseAmmo` there (`devastator.qc:286`).
- **FIX (Devastator):** `Attack` now calls `DecreaseAmmo(actor, slot, Cvars.Ammo)`.
- **OPEN — sibling sweep:** the same raw-TakeResource bypass exists in at least **Arc (bolt), Crylink, Electro
  (both modes), Hagar (primary + secondary tap), Hook** (grep `TakeResource(AmmoType`) — all would count negative
  under unlimited ammo (arena/NIX/give). Sweep them to `DecreaseAmmo(actor, slot, …)` (checking each has `slot`
  in scope; Hagar's loaded path already hand-checks `unlimited` for its give-back logic — leave that one).

### 26. HUD text renders too LOW in its box (health/armor numbers below center) — FIXED ✓ (user-confirmed)
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — SYSTEMIC; eyeball the whole HUD, not just health/armor.
- **Symptom:** the health + armor numbers sit visibly below vertical center of the panel (user screenshot).
- **ROOT CAUSE:** Godot `DrawString`'s Y is the BASELINE, but the shared text helpers (`HudPanel.DrawText` /
  `DrawTextCentered` / `DrawTextRight`) computed it as `pos.Y + size` — treating the ascent as the FULL font
  size. Xolonium's ascent ≈ 0.75-0.8 × size, so ALL panel text drew ~20% of the font size too low in its box;
  most visible on big tight-boxed digits (health/armor), but every panel using the helpers was affected.
- **FIX:** baseline = `pos.Y + Font.GetAscent(size)` (new `TextBaseline` helper) in all three draw helpers.
- **Watch on re-test:** any panel that was hand-tuned to compensate for the old low-bias may now sit slightly
  HIGH — glance at timer, ammo, scoreboard, centerprint while verifying.

### 27. "I'm on your team!" (teamshoot) voice plays on hits in DEATHMATCH — FIXED ✓ (user-confirmed)
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — needs a playtest; tests running.
- **Symptom:** players hit in DM play the teamshoot complaint voice ("Hey, I'm on your team!") — impossible,
  there are no teams. User asked: pain-sound mix-up, or team/damage logic thinking it's friendly fire?
- **ANSWER: the team/damage logic (both checked).** The pain-voice selection itself is fine; the teamshoot voice
  is correctly wired to the FRIENDLY-FIRE complaint path (`DamageSystem.cs` ~273 → deferred voice in
  `PlayerFrameLogic.cs:131`) — the bug is that path RUNning in DM at all.
- **ROOT CAUSE:** `Teams.SameTeam` (`Teams.cs:36`) was `a.Team != 0 && a.Team == b.Team` — MISSING Base's
  teamplay conditional. QC `SAME_TEAM(a,b)` = `teamplay ? (a.team == b.team) : (a == b)`. In FFA a player's
  `.team` still carries a pants-color-derived NON-zero value (Quake tradition), so two like-colored players
  compare EQUAL → the whole teamplay damage branch (`DamageSystem.cs:182`, teamplay_mode 4 default) ran between
  them in DM: team-damage accrual → complain threshold → the victim plays the attacker's "teamshoot" voice
  (+ mirror-damage side effects!). So DM hits between same-colored players were literally being treated as
  friendly fire — sounds AND damage-mirroring.
- **HOW players get equal teams in DM (confirmed):** the port faithfully implements DP `SV_ChangeTeam` — the
  `color` command sets `Team = pants+1` in a non-team game (`Commands.CmdColor` → `Teamplay.ChangeTeam`), and the
  client applies its configured color at connect; bots get colors too. Same pants ⇒ equal non-zero Team. (A
  headless bot-only probe never tripped the branch — it needs a pants-color match, i.e. typically the human.)
- **FIX (final shape — v1 reverted):** v1 put the teamplay conditional INSIDE `Teams.SameTeam` (the literal QC
  macro) — correct in principle, but it coupled a hot core predicate to the mutable `GameScores.Teamplay` static
  and broke 13 team-scenario unit tests via order-dependent static state (2 of 3 "weird" failures passed in
  isolation = leakage, not logic). Reverted: `Teams.SameTeam` stays the RAW compare (documented: callers on
  paths reachable outside team modes must add their own teamplay gate, as `Scores.cs` already does), and the
  **damage path is gated at the call site**: `DamageSystem.cs` friendly-fire branch now requires
  `Scoring.GameScores.Teamplay` (authoritative from GameWorld boot, `GameWorld.cs:587`). Base-equivalent: QC's
  non-teamplay `a==b` arm only admits SELF-hits, which every mode arm below excludes anyway (mode 1 is a
  teamplay-only config). **Full suite: 2953/2953 green.**

### 28. Cursor confined to the game window when LAUNCHED unfocused (background --host) — FIXED ✓ (user-confirmed)
- [x] **Status:** Fixed (branch `claude/playtest-fixes`) — verify: launch via agent while working in another app;
  the cursor must stay free until you click into the game.
- **Symptom:** when the agent launches the game while the user is focused on another application, the mouse
  becomes CONFINED to the game's window border even though Windows shows the other app as focused. (#16 fixed
  the capture-during-load; this is the never-focused-launch case.)
- **ROOT CAUSE:** `MouseCapture` gates the grab on `_focused && DisplayServer.WindowIsFocused()` — but a window
  LAUNCHED into the background can miss its focus edges entirely: Godot's internal focus flag initializes TRUE
  and no `WM_KILLFOCUS` ever arrives (real focus was never gained), so both checks pass, the spawn-frame capture
  sets the system-wide Win32 `ClipCursor` confine, and — with no focus edge ever coming — nothing releases it.
- **FIX (2 parts):** (1) `MouseCapture.WindowReallyFocused()` cross-checks the OS truth on Windows
  (`GetForegroundWindow()` P/Invoke vs the Godot window's native handle) and is now part of `Apply`'s gate;
  (2) `Shell._Process` polls it per frame (headless-guarded) and feeds transitions into `SetFocused` — so a stale
  confine releases within one frame without needing an edge, and the #19 auto-pause now also treats a
  never-focused background launch as unfocused (game waits paused until you click in — consistent defaults).

### 29. Taken/unavailable ground weapons missing the ghost-item dark tint — FIXED (needs playtest eyeball)
- [x] **Status:** Implemented (branch `feature/cpuoptimization`) — build clean, ran live twice with 0 exceptions;
  the LOOK needs a playtest confirm (walk over a weapon, check the leftover ghost is a dark translucent
  silhouette; with `g_weapon_stay 1` an owned stay-weapon should tint reddish).
- **Symptom:** when a weapon item on the ground is taken / not available (the weapons-stay "ghost"
  state), the port's render effect is wrong — no dark tint is applied where Base clearly darkens
  the ghosted item.
- **CONFIRMED root cause:** the port applied only the ghost ALPHA (`cl_ghost_items` 0.45,
  `DriveItemGhostFx`) and never the COLORMOD. Base `ItemDraw` (client/items/items.qc:182-186) sets
  `colormod = glowmod = cl_ghost_items_color` — and the shipped default `'-1 -1 -1'` is a REAL
  negative colormod (DP clamps the multiply at 0 → near-BLACK), NOT a "no tint" sentinel ('0 0 0'
  is the leave-unchanged value, per the cfg description + DP's only-copy-non-zero-colormod rule).
  So stock Base renders taken items as dark translucent silhouettes; the port rendered them as
  full-color translucent. Same gap for the weapon-stay tint (`cl_weapon_stay_color '2 0.5 0.5'`,
  alpha-only before). Both the research agent AND the parity registry rows had mis-read
  '-1 -1 -1' as "no tint" — registry corrected.
- **FIX:** `ClientWorld.SetTreeColormod` — per-SURFACE override materials
  (`SetSurfaceOverrideMaterial`) using cached tinted DUPLICATES of the shared surface materials
  (albedo+emission premultiplied by `max(colormod,0)`; emission covers QC's glowmod-same-vector),
  keyed by (material, tint) so shared/cached AssetSystem materials are never mutated; identity
  clears the overrides. Change-gated like `SetTreeTransparency` (one walk per state change).
  Wired: `DriveItemGhostFx` → `cl_ghost_items_color`, `DriveItemStayFx` → `cl_weapon_stay_color`,
  available/restore branches → identity clear. Known limits (documented in the registry):
  vehicle-hud item tint still unported; ShaderMaterial item surfaces stay untinted (alpha-only).

### 30. Client-side animations ignore pause/slowmo — FIXED (needs playtest: pause + `slowmo 0.3`)
- [x] **Status:** Implemented (branch `feature/cpuoptimization`) — build + 2953 tests green, headless smoke
  clean. Verify: pause a solo game (menu/console/alt-tab) → viewmodel sway/clips, casings, gibs, particles,
  flashes, item bob, player animations ALL freeze; `slowmo 0.3` in console → everything at 30% speed.
- **BASE truth (verified in DP source):** `CL_Frame` advances the ONE client clock by
  `clframetime *= cl.movevars_timescale` and `clframetime = 0` while paused (cl_main.c:2857/2872);
  CSQC `time` is refreshed from it every frame (csprogs.c:252) — so ALL CSQC animation (item bob,
  frame lerps, particles, viewmodel) slows/freezes in lockstep for free.
- **PORT gap:** the sim + `Api.Clock.Time` consumers already scaled (item bob/spin, damage text,
  beams, HUD), but every WALL-CLOCK animation driver ran unscaled Godot deltas: ClientWorld's
  central anim/pose/csqc-hooks drive, ViewModel (sway/switch/flash/recoil + both self-advancing
  clip players), ShellCasings + ModelGibs `_PhysicsProcess`, SpawnPointParticles pulse,
  MapParticleEmitters (Godot self-processing nodes), FaithfulParticleBackend's `_clientTime`,
  EffectSystem's FxLight fades.
- **FIX:** new `ClientRenderTime` static (game/client/ClientRenderTime.cs) — `NetGame._Process`
  publishes its per-frame `ResolveTimeScale()` (the same replicated slowmo that scales input
  cadence, #19) and every driver above multiplies its delta by it (map emitters drive
  `SpeedScale` instead — they're self-processing Godot nodes). Reset to 1 on `Shutdown` so a
  paused teardown can't freeze the menu/next match (mirrors the #19 slowmo restore). The faithful
  particle clock keeps its wall-clock SOURCE (the documented freeze/leak guard) — only its STEP
  is scaled; paused = same-time `Update()` no-op, nothing ages, nothing leaks.
- **Known residuals (documented):** modern-mode GPU particles (`cl_particles_modern` non-default,
  unverified mode) + projectile-trail Godot nodes not speed-scaled; audio deliberately unscaled
  (DP doesn't timescale sound envelopes).

### 31. Weapon-switch animation plays when the switch is impossible — FIXED (needs playtest)
- [x] **Status:** Implemented (branch `feature/cpuoptimization`) — build + 2953 tests green. Verify: with only
  one usable weapon, press next/prev/group keys → NO lower/raise animation, just the "unavailable" denial
  sound; with a second usable weapon, switching still lowers instantly on the keypress.
- **Symptom:** trying to switch weapons with no other weapon available (e.g. others have no ammo)
  still plays the viewmodel switch animation. In Base nothing switches — you just hear the
  "can't fire / impossible" denial sound.
- **CONFIRMED root cause:** the SERVER side was already faithful (audited end to end:
  `Inventory.SwitchWeaponWithComplain` leaves `SwitchWeaponId` untouched on a denied
  `ClientHasWeapon` — exactly Base `W_SwitchWeapon`/selection.qc:274 — so the weapon state machine
  never raises/drops, and the denial plays SND UNAVAILABLE like Base). The phantom animation was
  pure CLIENT-side: `NetGame.RunBoundCommand` played the keypress-predicted holster
  (`_viewModel.PlayHolster()`) on EVERY weapon-select impulse, unconditionally; the 0.45s
  grace-recover then raised the gun back — reading exactly as a switch animation. Same prediction
  family as #21/#24.
- **FIX:** `NetGame.SwitchCouldSucceedNow(imp)` gates the predicted holster — mirrors the server
  check with the same shared logic (`Inventory.ClientHasWeapon`, complain:false = silent):
  group keys (1..9, 14) test their impulse group; by-id (230+) tests the exact weapon PLUS its
  group when `cl_weapon_switch_fallback_to_impulse` is on (mirrors `ByIdHandle`); next/prev/last/
  best test "any other usable weapon" (best/last can rarely over-predict — the grace recovers).
  Pure remote client (no `LocalServerPlayer`): keeps the old predict-always behavior — the same
  graceful-degradation policy as the #24 ammo gate.

### 32. Remote players: wrong/missing animations + weapon held too high — FIXED (needs playtest)
- [x] **Status:** Implemented + probe-verified live (branch `feature/cpuoptimization`); build + 2953 tests +
  headless smoke green. Verify in playtest: remote players run/strafe/jump/crouch with real leg cycles, shoot/
  pain torso overlays play, and the held weapon sits in the RIGHT HAND through all of it.
- **Symptom:** enemy/remote players don't appear to play animations / strike the correct pose;
  the weapon they hold renders too HIGH, not where their hands are.
- **CONFIRMED root cause (live `[dbg32]` probe):** the whole animation pipeline was healthy —
  velocity/onground/ducked networked ✓, locomotion selection correct ✓, `legsTime` advancing ✓,
  weapon bone resolved (`bip01 r hand`) with a live marker ✓ — but **every clip resolved to
  framegroup 0**: stock Xonotic player IQMs define clips via NAMELESS `.framegroups` lines
  (probe: 31 groups, all `Name=''`), so `BuildClipTable`'s keyword match never hit and every
  Pick fell back to `groups[0]` = **DIE1**. Every remote player was permanently posed/playing
  the death animation regardless of movement — "no animation / wrong pose", AND the held weapon
  rode the hand bone of a DEATH pose (twisted high across the chest) — the SAME root explains
  the weapon-height complaint. Base never hits this because DP auto-names unnamed framegroups
  `groupified_<i>_anim` and `animdecide.qh`'s REGISTER_ANIMATION framenames match those — i.e.
  Base's contract is the SLOT INDEX (die1=0, die2=1, draw=2, duck=3, duckwalk=4, duckjump=5,
  duckidle=6, idle=7, jump=8, pain1/2=9/10, shoot=11, taunt=12, run=13, runbackwards=14,
  strafeleft/right=15/16, dead1/2=17/18, forwardright/left=19/20, backright/left=21/22,
  melee=23, duckwalk-dirs=24..30).
- **FIX:** `BuildClipTable` now resolves by the Base slot INDEX when the groups are unnamed
  (named sets keep the keyword path for community models with real anim names), including
  Base's `animfixfps` fallback pairs (forwardright→straferight, melee→shoot, duckwalk-dirs→
  duckwalk, pain2→pain1, die2→die1, duckjump→jump). Post-fix probe: idle@143+41, run@400+20,
  strafes@442/@463, diagonals@488/@509 — distinct real ranges; torso actions overlay (upper=2/3/4
  seen live); hand-marker positions sane. Dying/dead legs play DIE1 whose non-loop end holds the
  corpse pose (matches Base's DEAD1 hold).
- **Watch on re-test:** (a) any model whose `.framegroups` count ≠ 31 leans on the fallback
  pairs — glance at non-stock models if any; (b) the weapon should now track the hand through
  run/jump/shoot — if it still floats on SOME models, suspect that model's BoneWeapon metadata
  (sidecars), not this path; (c) chase-cam-from-inside haze seen during verification is a
  separate chase-distance quirk, not this bug.

### 33. Verify bot difficulty (skill) actually applies — VERIFIED WORKING (code audit; live A/B optional)
- [x] **Status:** Closed by a full-chain code audit (2026-07-05) — skill DOES apply in menu-started games.
- **The chain (all verified live):** menu difficulty → `MatchConfig.BotSkill` (SingleplayerScreen /
  CreateGameScreen / campaign) → `NetGame.ConfigureListenServer(_botSkill)` → writes the `skill`
  cvar when specified (NetGame.cs:734) → `BotPopulation.SpawnBot` seeds `Player.BotSkill` from
  `Cvars.Skill` + a per-frame resync loop mirrors QC bot.qc:725-736 (a live `skill` change
  re-seeds every brain) → consumed per-bot in ~10 places matching Base's formulas: think interval
  (`min(14/(skill+14),1)`), aim-error cone (`1−0.1·skill`), aim-lead blend (`skill·0.1`), fire
  deviation (`(10−skill)·0.3`), fire aggression, dodge scaling (`0.5+skill·0.1`), strafe-flip
  timing, bunnyhop gate, reload discipline (skill<2/<5 gates), skill>6 escape moves.
- **The one caveat (BY DESIGN):** a bare CLI `--host <map> --bots N` leaves `_botSkill = -1`
  (the cvar-persistence sentinel) → the `skill` cvar is NOT written → bots inherit whatever
  config.cfg / the last menu game left. Only affects automation runs, not menu play.
- **Not ported (uniform bots, not broken skill):** bots.txt per-bot skill OFFSETS
  (bot_aimskill/moveskill/… columns) default 0 — every bot shares the global skill instead of
  per-personality variance. Cosmetic-depth gap; note if bots feel "samey".
- **If it still FEELS flat:** run a menu A/B (skill 1 vs 10, same map/bots) — expect visibly
  slower reactions + wide misses at 1 vs near-instant tracking at 10. If those two feel alike
  in-game, reopen with that observation (then the suspect is a consumer formula, not the chain).

### 34. Enemy-held weapon still positioned wrong after #32 — too HIGH, "aligned to their view"
- [ ] **Status:** Not started (round 6 — #32 improved the body animations; the gun position remains wrong).
- **Symptom:** remote players' held weapon renders too high — reads as if it's aligned to their VIEW
  (eye height / view pitch) rather than sitting in their hands.
- **Leading hypothesis (from the #32 probe):** the hand marker itself is healthy (`bip01 r hand`
  resolved, live positions), so suspect the ATTACH path: `ViewEntityRenderer.EnsureAttached` may
  run while the player model is still the streaming placeholder (TagWeaponMarker null → falls back
  to entity root / RenderRoot) and never RE-ATTACH once the real skeleton lands — leaving the
  weapon at its NETWORKED wepent origin, which IS the view position ("aligned to their view").
- **Verify against Base:** `CL_ExteriorWeaponentity_Think` (weaponsystem.qc) — exterior weapon =
  `v_<weapon>.md3` attached to `tag_weapon` (gettagindex) else `"bip01 r hand"` setattachment;
  check whether stock player IQMs carry a dedicated `tag_weapon` bone the port should PREFER over
  the raw hand joint (offset/orientation authored for the gun).

### 35. Bots shoot teammates in team games + typing spare + chat bubble — FIXED (needs playtest)
- [x] **Status:** Implemented (branch `feature/cpuoptimization`) — build + 2953 tests + CTF smoke green.
  Verify: (a) CTF bots never aim at teammates; (b) start typing in front of an enemy bot — it holds
  fire; (c) a typing player shows the chat bubble sprite over their head on every client.
- **(a) CONFIRMED root cause — the `teamplay` CVAR was never written anywhere.** The port's
  `BotBrain.ShouldAttack` faithfully mirrors Base `bot_shouldattack` (team gate included) — but its
  team gate reads `Cvars.Teamplay` = the **`teamplay` cvar**, and NOTHING in the port ever set it
  (the #27 work set only the `GameScores.Teamplay` static at GameWorld boot). So the gate was
  silently non-team in every gametype → CTF bots treated same-team players as valid targets.
  Second casualty found: warmup's `teamplay_lockonrestart` gate (also reads `Cvars.Teamplay`) was
  dead. **FIX:** GameWorld boot now mirrors the gametype onto the real cvar
  (`Services.Cvars.Set("teamplay", ...)`, like Base `InitGameplayMode`); cvar registered
  (runtime, never archived). The FFA pants-team trap is unaffected: in DM `teamplay` stays 0 and
  Base's own same-team fall-through applies (bots still fight everyone).
- **(b) Typing spare:** Base `bot_shouldattack` (aim.qc:120) spares `PHYS_INPUT_BUTTON_CHAT`
  targets unless `bot_typefrag` — the port's predicate omitted it entirely. Added (reads the live
  `Entity.ButtonChat` input mirror — the same field the monster typefrag spare already reads);
  `bot_typefrag` registered (Base default 0 = spare).
- **(c) Chat bubble:** entirely unimplemented (the typing state was networked and used for
  typefrag scoring, but nothing rendered). Ported `UpdateChatBubble`/`ChatBubbleThink`
  (client.qc:1350-1397) as `GameWorld.UpdateChatBubble` in the per-client PreThink slot: while a
  live player types, a `models/misc/chatbubble.spr` entity (asset ships in the pk3dir) follows at
  `origin + '0 0 15' + maxs_z`; freed on stop/death/spectate + on disconnect (no orphans). A plain
  networked entity → visible on every client, including remote games.

### 36. Weapon textures lack color / detail textures wrong + weapon animations incorrect
- [ ] **Status:** Not started (round 6).
- **Symptom:** most weapon textures look wrong — lacking color, and the detail textures don't
  appear to be applied correctly; weapon ANIMATIONS are also definitely not playing correctly.
- **Two suspected halves:**
  (a) RENDER: Base weapon materials (dpreflectcube/reflect, _norm/_gloss/_glow stages, colormapped
  panels?) vs the port's material build for `v_*`/`h_*` models — find what stage(s) drop the color/
  detail; (b) ANIMATION: weapon md3/iqm clips — Base `weaponsystem.qc` uses FIXED frame slots
  (fire1=0, fire2=1, idle=2, reload=3 via `CL_WeaponEntity_SetModel`) — the port may be
  name-matching nameless framegroups again, the exact #32 class of bug (see
  [[player-anim-framegroups-slot-index]]).

### 37. Decals: render through walls/particles + don't wrap like DP — FIXED (needs playtest)
- [x] **Status:** Implemented (`20bed1c`) — build + 2953 tests green. Verify: shoot walls near corners
  (marks conform, no streaks, nothing floating), check marks are never visible from the far side, and
  blood splats land ON surfaces.
- **CONFIRMED root causes (the DecalSplats architecture itself was already DP-faithful —
  Sutherland–Hodgman clipping of the real render triangles, multiplicative blend, depth-tested):**
  1. **Per-miss flat-quad fallback:** `DecalSplats.Splat` emitted an UNCLIPPED flat quad whenever
     the triangle clip found nothing — a mark floating in space, poking through the corner it
     failed to conform to = the through-wall decals AND the corner streaks. Now: fallbacks only
     when a geometry SOURCE is missing entirely; a clip miss on a real map = no mark (DP).
  2. **Last live legacy caller:** blood splats still went through the Godot `Decal` PROJECTION BOX
     (`Decals.SpawnProjected`) — projection volumes paint through thin geometry inside their box
     and smear across corners. Rerouted through `Splats.SplatPoint` (CL_SpawnDecalParticleForPoint)
     with the removal-color complement + DP's staintex/blood-band atlas cells.
  3. **`cl_decals` master toggle dead** (2026-06-14 audit) — wired (unset = on; engine cvar).
- **Deferred (registry-worthy, low impact):** `cl_decals_fadetime`/`r_drawdecals_drawdistance`
  cvar wiring; decals on dynamic models (`cl_decals_models`, Base default 0 anyway).

### 38. Electro + Blaster PRIMARY projectiles look flat — FIXED (needs playtest)
- [x] **Status:** Implemented (`32ac05c`) — build + 2953 tests green. Verify: blaster/electro bolts glow
  (bright additive core, visible in dark rooms), electro explosions brighter.
- **CONFIRMED root cause (live material probe):** the bolt models load fine (magic-dispatched
  `.mdl`→MD3; surface shaders `laser/electro_projectile_core|long` resolve; `blendfunc add` +
  `autosprite` both compile) — but the compiled Add materials stayed at Godot's default
  **PerPixel SHADED** mode: scene lighting multiplies the texture BEFORE the add, so in a dim room
  the additive contribution is near-black → a flat dark blob. Q3 additive stages are UNLIT
  self-luminous adds — `ApplyBlend` + `ApplyBaseBlend` now set Unshaded for Add (and Filter, which
  multiplies the already-lit framebuffer). Also brightens every dim additive map/FX surface.
- **Residual — FIXED (branch `parity/blaster-electro-bolt-autosprite`, needs playtest):** both
  deforms are now implemented faithfully as a generated `skip_vertex_transform` vertex shader with
  per-quad center/axis baked into CUSTOM0/1 by the MD3 builder (`AutospriteQuads` +
  `AutospriteShaderGen`; DP `gl_rmain.c` Q3DEFORM_AUTOSPRITE/AUTOSPRITE2 math) — the core re-aims at
  the view plane, the streak stays on its flight axis and only rolls toward the camera, pivoting at
  its own trailing center. Also fixes HLAC (same two-quad pattern). NOTE: the same change
  **supersedes this fix's unshaded treatment for the bolts** — DP's bolt stages are LIT
  (`rgbGen lightingDiffuse`) with a fullbright `_glow` companion added on top; the new bolt material
  is lit-base + `_glow` EMISSION, additive (the general `ApplyBlend` unshaded rule for other map FX
  is untouched). Electro's `tcMod page 4 1 0.1` crackle flipbook also now animates (was a parsed
  no-op). Details: planning/blaster-electro-bolt-autosprite-parity.md.

### r8 addendum — #36 weapon appearance v2 + the fire-animation trigger (both re-reported, both FIXED)
- [x] **Appearance "too shiny / seeing through geometry" (screenshots 18:42):** caused by the r7 cubemap
  binding — the reflect-masked panels (electronew_reflect: 5% bright) mirrored the sky at FULL strength
  as an always-on EMISSION add; bright sky-colored patches read as HOLES through the gun + chrome. The
  deeper DP truth: `dpreflectcube` only evaluates inside DP's RTLIGHT shader permutations, and stock
  Xonotic ships realtime world lighting OFF — the term is near-invisible in Base. **Fix:** cubemap left
  deliberately UNBOUND (documented in `AssetSystem.DefaultReflectCubemap`); the skin shader's restrained
  no-cubemap sheen (mild metal, diffuse never killed) is the faithful default. Verified: electro on dance
  shows its real tan body, no mirror patches.
- [x] **Weapon fire animation never played (muzzle flash fine):** the networked `ViewmodelFrame` selector
  is deliberately not consumed on a LISTEN HOST ("the host path owns it") — but the host path
  (`UpdateViewModelReloadAnim`) only ever derived RELOAD, so the local player's fire clip had NO trigger
  at all. **Fix:** the host derives the per-shot fire edge from the live slot's ATTACK_FINISHED bump
  (every shot pushes it forward — Base `weapon_thinkf(WFRAME_FIRE1)` restarts per shot) → new
  `ViewModel.PlayFireClip()`, which REWINDS an already-playing fire clip (Godot `Play()` doesn't restart)
  so sustained rapid fire re-triggers visibly. Remote-client note: the int-selector path still only
  rising-edge-triggers (a machinegun burst plays fire once for a SPECTATOR) — needs a shot counter in the
  wepent block; deferred.

### r9/r9b addendum — fire anims + see-through weapons re-reported (HAGAR + DEVASTATOR screenshots); all FIXED
- [x] **Fire anims STILL dead after r8 — three deeper roots (`ea2a6f3`) + one trap (`b082c30`):**
  (1) invisible-hand IQM weapons (shotgun/uzi/nex/arc…) DISCARDED the animated h_ rig and rendered the
  static v_ model at a baked offset — structurally nothing could animate; the equip now renders the LIVE
  rig with the v_ gun on a `BoneAttachment3D` at the animated `weapon` bone (Base
  `setattachment(weaponchild, this, "weapon")` — the fire clip pumps the BONE, the gun rides it).
  (2) The DPM rigs (h_electro/h_crylink/h_rl/h_gl/h_hagar) named their nameless framegroups `anim_0..3` —
  the r7 slot fix covered IqmBuilder only; DpmBuilder now shares the fire/fire2/idle/reload slot contract.
  (3) The v_ models attached to the rig are themselves skeletal IQMs with ZERO animations and bring an
  EMPTY AnimationPlayer — `FindChild`'s first match handed ViewModel the empty one; new
  `FindAnimationPlayerWithClips` prefers the clip-carrying player. **Chain PROBE-VERIFIED** end to end via
  a scripted-fire camera-trace run (injected attack buttons): per-shot AttackFinished edge →
  `PlayFireClip -> 'fire' | anims: fire,fire2,idle,reload`.
- [x] **See-through weapon geometry (user screenshots = the HAGAR and the DEVASTATOR — both DPM rigs):**
  DPM triangles are wound OPPOSITE to the IQM/MD3 convention relative to Godot's `cull_back` — every DPM
  weapon rendered INSIDE-OUT (front faces culled, interiors visible): the devastator's hollow mid-body,
  the hagar's chrome-ish interior glints, the electro's white patches — one bug, three looks. DpmBuilder
  now emits each triangle as (0,2,1). Capture-verified: solid devastator, readable side decal. (The r9b
  commit message misattributed the hagar screenshot as "mortar" — the mortar/h_gl is the same DPM family
  and carries the same fix, but the pictured gun was the hagar.)
  **WATCH next playtest:** the flip affects ALL DPM models (monsters included) — anything that previously
  looked right was silently compensating; eyeball a monster if one shows up.
- [x] **Team tint (progress confirmed by user — "now I can see colors"):** `LocalShownamesTeam` returns the
  NUM_TEAM color CODE (blue=13) where `ModelTint.TeamColor` wants the colormap nibble (1..4) — the #8
  code-vs-nibble trap; blue guns never tinted. Normalized via the now-public
  `ClientWorld.NormalizeTeamColormap` at the viewmodel glow site.

### 39. Electro explosion FX — plumbing VERIFIED WIRED; look re-judge after #38
- [~] **Status:** Investigated (round 7) — the suspected gaps DON'T exist: effectinfo
  `lightradius/lightcolor/lightradiusfade` rows ARE parsed (EffectInfoParser) and spawned as pooled
  OmniLight3D flashes (EffectSystem.SpawnInfoLight — electro impact 250qu, combo 400qu, blue HDR
  color); the flying bolt's 90qu blue dlight rides `TrailLightFor(TR_NEXUIZPLASMA)`
  (ProjectileRenderer.BuildLight); additive particles use exact premultiplied math; scene glow is
  enabled. The dominant LOOK bug was #38's shaded-additive (the explosion's additive sprites dimmed
  the same way). **Re-judge after the round-8 playtest**; if still off, the ranked candidates are:
  OmniLight falloff curve vs DP dlight falloff, the Energy normalization (`min(8,maxc)`), and the
  scene glow HDR threshold (1.0) — each a cheap A/B, deliberately not changed blind.

### 40. Hagar fire animation never stops after firing — FIXED (needs playtest)
- [x] **Status:** Implemented (r12). Verify: hold hagar fire, release → the pump animation stops (drops to idle);
  other weapons unchanged.
- **CONFIRMED root cause:** the hagar's fire clip is authored **loop=1** (`h_hagar.iqm.framegroups`; the
  other DPM rigs' fire rows are loop=0) — and the port's only fire→idle transition was the end-of-clip
  recovery (`!IsPlaying()` → PlayIdle), which a LOOPING clip never reaches. Base never waits for clip end:
  its weapon state machine re-asserts WFRAME_IDLE explicitly when the attack window closes (w_ready →
  weapon_thinkf).
- **FIX:** the listen-host anim driver calls the new `ViewModel.StopLoopingFire()` once the slot's
  ATTACK_FINISHED expires with no new shot — no-op unless the CURRENT clip is a looping fire/fire2.

### 41. Weapons look dull — the port lacked DP's lightgrid MODEL lighting — STAGE 1 SHIPPED (viewmodel)
- [~] **Status:** Stage 1 implemented (r12): the Q3 BSP **lightgrid** (lump 15) is now parsed
  (`LightGridData`, dims from the world bounds like DP `Mod_Q3BSP_LoadLightGrid`, trilinear `Sample` like
  `Mod_Q3BSP_LightPoint`; real-asset test on stormkeep) and the VIEWMODEL is modulated by the sample at the
  camera each frame (`(ambient + 0.5·directed)/128`, floor 0.25 / cap 1.6, epsilon-gated, identity when a map
  ships no grid). Verify: the gun brightens in bright yards, dims in dark corridors, tints under colored light.
- **CONFIRMED root cause:** DP lights EVERY model from the baked lightgrid at the entity origin — the port lit
  everything with one global "Sun" DirectionalLight + flat 0.6 sky ambient, so guns (and all models) read
  gray/flat indoors regardless of the map's baked lighting.
- **Follow-ups (documented, not yet done):** (a) players/items/monsters — same sample at each entity origin
  (drive `ModelTint` colormod or a per-entity light); (b) the directed term as a real directional light
  (direction bytes are already decoded); (c) plain StandardMaterial3D weapon surfaces ignore the instance
  colormod (skin-shader surfaces only) — most weapon bodies are skin-shader, rest follow with (a)'s mechanism.

### 42. Mortar/weapons still bland vs Base — DP-formula grid shading + gamma response + sight anim — FIXED (needs playtest)
- [x] **Status:** Implemented (r15) — experiments **B, C, D + A-behind-a-cvar** from
  `planning/weapon-render-pipeline-comparison-2026-07-05.md`. Verify: the gun shows directional
  light-and-shade that CHANGES as you move through the map (bright yards pop, dark corridors dim, colored
  light tints), colored glints track the map lighting, and the mortar's sight cycles its 3 frames and
  pulses its blink. `set r_model_light_gamma 0` should visibly flatten the gun (A/B look comparison).
- **What changed (screenshot-verified: shading structure + specular glint + gamma-0 flattens; no shader errors):**
  - **B — real grid shading:** `PlayerSkinShader` gained a `grid_lit` instance-uniform branch reproducing
    DP's `MODE_LIGHTDIRECTION` per pixel: `tex×ambient + tex×diffuse×max(0,N·L) +
    gloss.rgb×diffuse×pow(max(0,N·H), 1+32·gloss.a) + glow` from the lightgrid sample (`grid_ambient` /
    `grid_diffuse` / `grid_dir` pushed by `NetGame.UpdateViewModelLightgrid` → `ViewModel.SetGridLight` →
    `ModelTint.ApplyGridLight`). EMISSION-only output (scene lights can't double-light); the 6.0-energy
    ViewFill is hidden while grid-lit; muzzle flash pops via a decaying warm ambient boost (DP's dlight
    lights the viewmodel — the grid branch can't see the OmniLight). No grid on the map → the old PBR+fill.
  - **C — colored DP specular:** the `gloss.rgb × directed × pow(N·H, 1+32·gloss.a)` term above (Blinn
    half-vector against the BAKED direction, no N·L gate — DP has none).
  - **A — gamma-faithful response (cvar `r_model_light_gamma`):** DP multiplies light onto
    GAMMA-encoded texels (vid_sRGB 0, no tonemap) → display scales LINEARLY with light. The shader
    re-encodes the texel gamma, multiplies there, clamps (DP's framebuffer saturates), and pre-decodes so
    Linear tonemap + sRGB encode round-trips it. 0 = plain linear multiply (visibly flatter — confirmed).
    Global shader param registered/polled by `WorldTint`; scale knob `r_model_light_scale` (default 1 =
    DP's absolute 1/128 — the r13 "self-calibrating average" normalization was a crutch for the linear
    response and is gone). **Default flipped to 0 in r15: Bryan preferred the linear look in the live
    A/B ("I think it looks better with r_model_light_gamma 0 — leave the feature in though").**
  - **D — sight animation:** `ShaderCompiler.NeedsAnimatedShader` now triggers on multi-frame `animMap`
    and `rgbGen wave` (the sight stage has neither tcMod nor deform — it previously compiled STATIC, frozen
    on frame 1 with no blink). The generated stage shader cycles `int(TIME*fps) % N` over per-frame
    samplers and emits the Q3 waveform (`sin/square/triangle/sawtooth/inversesawtooth`, clamped 0..1;
    `rgbGen const` too). Parser pinned by new `AnimMap_MortarSightStage_ParsesFramesFpsAndWave`.
- **Follow-ups (unchanged from #41):** players/items/monsters via the same `ApplyGridLight` mechanism;
  plain StandardMaterial3D surfaces still ignore grid light (skin-shader surfaces only).

### 43. Player PROFILE color (FFA) never applies — only team color did; weapons + bodies — FIXED (needs playtest)
- [x] **Status:** Implemented (r15). Verify (FFA): pick shirt/pants in Multiplayer ▸ Profile → your gun's
  `_shirt`-mask panels tint your shirt color and its glow takes the pants color; bots' bodies AND their
  held guns show their own colors; everything still team-colors correctly in teamplay. Verified by
  capture: `--cvar _cl_color 212` → blue shirt panels on the mortar viewmodel.
- **CONFIRMED root cause — the packed `clientcolors` never left the server.** Base applies the colormap
  palette UNCONDITIONALLY (viewmodel: `e.colormap = 256 + entcs_GetClientColors(...)`, view.qc:317;
  exterior gun: `colormap = owner.colormap`, weaponsystem.qc:180; teamplay just forces clientcolors to
  `17*teamcode` server-side). The port: (1) humans never sent `_cl_color` (the `color` client command +
  `SV_ChangeTeam` sink existed server-side with ZERO callers — only bots set colors); (2) the entity wire
  carried only the TEAM byte (`Colormap`); (3) every render sink was `hasTeam ? team : Black`.
- **FIX (full chain):**
  - Wire: new `EntityField.Colors` (bit 25, byte) = packed `16*shirt+pants`; `ServerNet` stamps it from
    `Player.ClientColors`; `ClientEntityView` → `Entity.Colors`. Codec pinned by
    `ClientColors_RoundTrip_Through_The_Delta_Codec`.
  - Client push: `NetGame.PushLocalPlayerColor` (change-gated per frame) — listen host applies via
    `Teamplay.ChangeTeam` (QC SV_ChangeTeam: FFA-only recolor), pure client sends `color <n>`.
  - Render: `ResolveForcedColormap` returns `1024 + Colors` when unforced (full palette paints — QC form);
    `ModelTint.ApplyColormap` decodes packed maps (shirt+pants nibbles + pants glow);
    `ViewEntityRenderer` tints the held gun from the OWNER's resolved colormap (change-gated, re-applied
    on rebuild); viewmodel: `UpdateViewModelColors` = pants/shirt palette + `weaponentity_glowmod`
    pants-glow fallback (vortex `wr_glow` charge override kept), watched-player aware (spectator-correct).
- **Note (user-visible, not a bug):** the "invisible names are not allowed → Player#1" warning during
  verification is the QC-faithful name check — `~/XonData/config.cfg` had `seta _cl_name "^x06b"` (a color
  tag only, no visible chars: the profile name editor's color chip inserts `^xRGB` into the name box).
  Type an actual name after picking the color.

---

## 2026-07-06 batch — remote-client / client-map-load playtest (#44–#59)

Found playtesting the **two-instance remote-client session** (`--host` + `--connect 127.0.0.1`) on branch
`feature/client-map-load` (post-main-merge `a02f92c`). Most items are pure-client parity gaps from that
effort; #52–#54 are general issues spotted during the same session.

**Fix pass 2026-07-06 (same branch, needs playtest):** #44 sv_spectate restored to 1 + host `early_join_requested`
auto-join; #45 unblocked by #44 (full ObserverOrSpectatorThink flow was already ported: +jump join, +attack cycle,
+attack2 free-fly, SpectateCopy); #46 messagemode/messagemode2 chat prompt (ChatPrompt.cs + Shell wiring);
#47 port-default `sv_maxidle_playertospectator 0` (deliberate divergence — Base ships 60);
#48 `.spr` billboard render (chatbubble was a placeholder box) + the prompt raises BUTTON_CHAT;
#49/#51/#58 root cause = `TryGetRemoteState(LocalNetId)` always MISSES (own entity lives in LocalState) →
new `ClientNet.TryGetViewedState` + 3 call-site fixes (viewmodel anim frame / colors / vortex glow);
#50 players render yaw-only in ClientEntityView (Base entcs networks yaw alone);
#52 healtharmor icons: removed the `_big/_mega` "enhancement" (Base always draws plain health/armor);
#54 ghost tint now covers ShaderMaterial surfaces (powerups are glow-shader-heavy — flat dark substitute, DP
clamped-multiply look); #55 audited — the full Base gate chain (entcs privacy + enemy LOS trace vs the real map
+ fades) is already ported and traces the post-map-load collision; needs live evidence if it still leaks;
#57/#59 audited end-to-end clean (rings ride OwnerWeaponRings, hitsound diffs LocalState.HitDamageDealtTotal —
both consumers hoisted out of the host-only block earlier) — likely fixed by the round-2 HUD mirror; RETEST.
#53 HUD editor: parity work delegated (see the item's appended notes). #56 warpzones: DEFERRED — see the item.

### 44. `sv_spectate` is forced to 0 — should be Base's default 1 **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** The listen server runs with `sv_spectate 0`; Base ships `sv_spectate 1` (spectating allowed).
- **CONFIRMED mechanism (why it's 0):** `NetGame.StartListenServer` sets it deliberately
  (`game/net/NetGame.cs` ~:655, long comment): with Base's `sv_spectate 1` the QC PlayerPreThink autojoin
  gate (`!(sv_spectate||g_campaign||forced_spectator)`) never fires for a passive client, and the
  self-hosted listen host used to sit as a permanent observer under the connect overlay — so the port
  flipped the cvar to get "host AND play". That workaround costs us spectating entirely (see #45) and
  arms the sv_spectate=0 spectator-KICK block (see #47).
- **Fix direction:** restore `sv_spectate 1` and implement the host's auto-join the way Base actually
  enters a match (join on +fire from the observer/welcome state; Create-Game UX can send the join for the
  host explicitly) instead of repurposing a server policy cvar. Re-check `ListenHostAutojoinTests`
  (they pin the current sv_spectate=0 behavior).

### 45. Spectating doesn't work generally — inspect Base's spectate flow and implement it **[client-map-load]**
- [ ] **Status:** Not started (needs a thorough Base inspection first)
- **Symptom:** Spectating is broadly non-functional in the two-instance session.
- **Expected (Base):** observers can free-fly; +attack cycles through players (chase-cam their view,
  including their viewmodel/HUD stats via SpectateCopy); +jump returns to free-fly; scoreboard marks
  spectators; `spec`/`spectate` commands work; spectators persist (not kicked) with `sv_spectate 1`.
- **Notes:** pieces EXIST in the port — `SpectateeStatus` is networked (owner block), `SpectateCopy`
  pressed-keys/stat copy is referenced server-side, T44 free-fly observer movement works, the camera has
  a spectate branch (`_client.SpectatingNetId` reads in NetGame) — but the end-to-end flow was never
  exercised: with `sv_spectate 0` (#44) a would-be spectator is auto-joined or kicked, so none of it is
  reachable in normal play. Likely intertwined: fix #44 first, then audit observer→spectate→cycle→return
  against QC `server/client.qc` (ObserverThink/SpectatorThink/PlayerUseKey) + `cl_view` spectate cam.
- **First look:** QC `sv_spectate`/`ObserverOrSpectatorThink`/`SpectateNext/Prev` vs
  `ClientManager.ObserverOrSpectatorThink` + `ServerNet.DriveObserverJoins` + NetGame's spectate camera.

### 46. Chat features aren't implemented (input side) — inspect Base's messagemode flow **[client-map-load]**
- [ ] **Status:** Not started (needs a thorough Base inspection first)
- **Symptom:** No way to chat in-game; chat features "don't seem to be implemented at all."
- **Notes (what exists vs what's missing — unverified):** the server→client half EXISTS: `Chat.Say`
  routing (public/team/private, T46), `ServerNet.BroadcastPrint` → `PrintReceived` → the ChatPanel
  scrollback + console. What appears MISSING is the client INPUT half: Base's `messagemode`/
  `messagemode2` (T / Y binds) chat prompt line — the port's binds route to the console interpreter, but
  there's no chat-prompt UI state (type → Enter sends `say`/`say_team`, Esc cancels), and no
  con_chat prompt rendering. Also related: the typing indicator (#48) should key off messagemode-open.
- **First look:** Base `keys.c`/`console.c` messagemode + `client/hud/panel/chat.qc` vs the port's
  `BindInput`/`ConsoleOverlay`/ChatPanel; wire T/Y → a chat input line → `say`/`say_team` client command.

### 47. Disable the no-idling auto-spectate/kick by default **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** Idle players get auto-moved/kicked (the "no idling" enforcement) by default.
- **Expected:** Off by default — Base ships `sv_maxidle 0` (no idle kick) and only kicks/spectates
  idlers when a server op sets it.
- **Notes:** two suspects in the port: the sv_spectate=0 parked-spectator KICK block
  (`GameWorld.PlayerFrameIdleAll` — armed by #44's forced 0) and any ported `sv_maxidle` /
  `sv_maxidle_spectatormode` logic whose default drifted from Base. Verify which one fired in the
  playtest, then align defaults with Base's cfg (likely falls out of #44 for the kick block).

### 48. Typing indicator above a player's head renders as a block glyph **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** The typing indicator over a player's head draws as a placeholder block (missing glyph),
  not the chat bubble.
- **Expected (Base):** the chat-bubble sprite (`models/misc/chatbubble.spr` / the csqc chat icon) shown
  while a player has messagemode open.
- **Notes (hypothesis):** #35's chat-bubble implementation (merged from main, `a0e3f9b`) appears to
  render a TEXT glyph the HUD font lacks → tofu box. Should draw Base's bubble art instead; also the
  "is typing" state for HUMANS needs the messagemode hook (#46) — today only bots set it.
- **First look:** the #35 chat-bubble render site (ShowNames/nameplate layer) + Base `shownames.qc` /
  chatbubble asset resolve.

### 49. Remote client's muzzle flash looks generic/strange **[client-map-load]**
- [x] **Status:** FIXED (needs playtest) — real per-weapon effectinfo flash
- **Symptom:** Weapon-fire muzzle flash on the pure client plays oddly — looks like a generic flash,
  not the weapon's own (host looks right).
- **Root cause (the original hypothesis was WRONG):** the equip path was fine — `EquipNetworkedWeapon`
  passes `MuzzleEffectFor(w)`/`MuzzleModelFor(w)` identically on host and pure client. The generic look
  was the LOCAL first-person flash itself: `ViewModel.Fire` spawned `EffectSystem.MuzzleFlashAttached`,
  a deliberate name-heuristic burst (one shape/tint classifier for every weapon) instead of the weapon's
  real effectinfo blocks — while the real per-weapon burst (the networked copy) is except'd away for the
  shooter. Related: two remote-visual regressions from the #50 fix (whole-body pitch zeroing also killed
  the aim-bone pitch) were fixed on `feature/owner-inventory` (Entity.ViewPitch), now folded back.
- **Fix:** `ViewModel.Fire` now routes through the FULL effectinfo `Spawn` path (parsed blocks + block
  dlight + faithful/modern router — the exact path a networked muzzle flash takes) at the muzzle socket's
  live world transform, `dir*1000` like the server emit. Base-faithful: QC `W_MuzzleFlash` fires
  world-space `pointparticles` at shotorg; only the flash MODEL attaches to the gun (already ported).
  The heuristic `MuzzleFlashAttached` was deleted. Watch in playtest: possible brief over-bright where
  an effect's own dlight now stacks with the `_flashLight` stand-in (kept for the grid-lit pop +
  effects without a light row).

### 50. Remote player model pitches its whole body when looking up/down **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** Watching the other (real) player: when they look down, their whole body rotates
  downward instead of bending.
- **Expected (Base):** player models never pitch whole-body — legs stay upright; aim pitch drives the
  torso/aim blend only (animdecide upper-body; cf. the lean work — render-only, never via angles).
- **Notes (strong hypothesis):** `EntityNode.SyncFromEntity` has two angle paths — full-basis for
  pitched/rolled entities, yaw-only for players/items/flags. The networked player angles now carry the
  view PITCH, which likely routes players onto the full-basis (pitched) path → whole-body tilt. Players
  should render yaw-only regardless of networked pitch (pitch belongs to the aim/torso layer).
  ⚠ angle/basis work → read `planning/COORDINATE_CONVENTIONS.md` first (#7's lesson).
- **First look:** `EntityNode.SyncFromEntity` path selection for `Kind.Player` + where ClientEntityView
  copies snapshot angles onto the proxy; Base `CSQCPlayer_ModelAppearance`/animdecide for the aim blend.

### 51. Hagar fire animation loops continuously on the remote client **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** The hagar's fire animation never stops for the pure client (the #40 symptom, which was
  FIXED on the listen host — r12 "stop looping hagar fire").
- **Notes:** #40's fix likely keyed off listen-host state (LocalServerPlayer / the server-side weapon
  frame), so the pure-client viewmodel (driven from the networked ActiveWeaponId + predicted fire) never
  gets the stop edge. Same family as #49 (pure-client viewmodel anim feeds) and #57/#58 — the
  viewmodel's per-frame state sources need a networked-stats path like the HUD mirror got.
- **First look:** the #40 fix site (ViewModel fire-anim stop condition) — what state it reads on a pure
  client; cross-ref `view-bobfall-idle-sway` notes (weapon framegroups are slot-indexed).

### 52. HUD health/armor icons are wrong — use Base's art *(general, found in this session)*
- [ ] **Status:** Not started
- **Symptom:** The health and armor icons in the HUD don't match Xonotic's.
- **Expected:** the icons Base's HUD skin ships (`gfx/hud/luma/health`/`armor` family) — resolved
  skin-aware through the mounted content, exactly like the weapon icons already are.
- **Notes:** NOT part of the networking/remote-client work (also wrong on the host). The HealthArmor
  panel is drawing a substitute glyph/icon; the TextureCache VFS resolve is already skin-aware
  (`gfx/hud/<skin>/…`), so this is likely just wrong icon names/fallback in the panel.
- **First look:** `HealthArmorPanel` icon paths vs Base `hud_healtharmor` skin assets;
  `planning/HUD_PARITY_CONTRACT.md`.

### 53. HUD editor doesn't match Base — full-functionality parity review *(general)*
- [x] **Status:** Core functionality landed 2026-07-06 (feature/client-map-load); remaining gaps listed below
- **Symptom:** The port's HUD editor is wrong vs Base's.
- **Expected:** Base's in-game HUD configure mode (`_hud_configure`): per-panel drag/resize with grid
  snap, the panel setup dialogs (the 22-panel set), border/padding controls, dock/skin selection,
  save-to-config — full functionality pairing.
- **First look:** Base `qcsrc/client/hud/hud_config.qc` + `qcsrc/menu/xonotic/dialog_hudsetup_exit.qc`
  family vs the port's `game/menu/dialogs/DialogHudPanels`/HUD editor + `HudManager`; write the gap list
  into `planning/HUD_PARITY_CONTRACT.md` before implementing.
- **2026-07-06 audit result:** the editor core (`game/hud/HudConfigEditor.cs`, the hud_config.qc port) was
  already faithful — drag/resize with grid-before-collision snap, 4-corner grips, arrow nudge
  (Alt=resize/Shift=fine/Ctrl=no-collision), Ctrl+Tab/Space/C/V/Z/S, `ftos_mindecimals` pos/size cvar
  round-trip, grid+centerline draw, `_hud_panelorder` — and so were the 22 per-panel dialogs
  (`HudPanelCommon` binds the exact Base `hud_panel_<p>_bg*` cvar set) and the exit dialog
  (panel-bg defaults / dock picker / grid controls). What was BROKEN was every seam that lets you reach
  or see it. Fixed this pass:
  - `_hud_configure 0/1` via menu buttons was INERT (MenuCommand had no bare-cvar fallback) — enter/exit
    both dead. Fixed: DP bare-cvar set/print fallback in `MenuCommand.Dispatch` default; `togglemenu` case.
  - ESC (`menu_showhudexit`) + double-click (`menu_showhudoptions <panel>`) + Ctrl+S (`hud save`) fell
    through the interpreter's unknown-command server-forward — all no-ops. Fixed: the editor registers the
    three verbs on the shared interp (`HudConfigEditor.RegisterEditorCommands`); MenuCommand cases +
    `MenuDialogRegistry` entries for the QC dialog names (`HUDExit`, `HUD<panel>` ×22, `hudconfirm`).
  - The OS pointer stayed CAPTURED while configuring (NetGame's `UiOwnsCursor` reassert predates the
    editor; net files owned by concurrent work) — mouse editing impossible. Fixed additively:
    `MouseCapture.HudEditorWantsCursor` override latch, driven per-frame by `HudConfigEditor.Update`,
    with Godot cursor shapes for move/"\\"/"/" resize (QC cursor_type).
  - Panels with bg "0" (chat/notify/racetimer/…) and data-less event panels were INVISIBLE in the editor.
    Fixed per the hud.qh configure macros: `HudPanel.Resolve` configure branch (bg "0" → border_default at
    `hud_configure_bg_minalpha`; enabled floor; disabled panels fixed 0.25 bg+fg; `teamcolorforced` red
    test tint), a `_Draw` pre-pass so bail-early panels still paint their frame, and `HudManager` forces
    StartHidden MAIN panels visible while configuring (restored on exit).
  - Hover feedback: ported `HUD_Panel_Check_Mouse_Pos` + the white 0.1-alpha grab/hover fill (QC
    hud_config.qc:999/1055).
  - `hud save <name>` backend: `HudConfigEditor.ExportCfg` (HUD_Panel_ExportCfg port) writes
    `hud_<skin>_<name>.cfg` seta dumps under `<userdir>/data/`; wired to Ctrl+S, the console verb, and the
    exit dialog's "Save current skin".
  - "Enter HUD editor" button now runs QC `HUDSetup_Check_Gamestatus` (in-match → `togglemenu 0;
    _hud_configure 1`; else the hudconfirm dialog → `map _hudsetup` — the map ships in the maps pk3).
- **Remaining gaps (out of this pass's scope):**
  1. HUD skin LIST backend (enumerate `data/hud_*.cfg` + Set skin/Refresh/Filter in the exit dialog) —
     honest stub note remains; `HudPanelCommon.SkinListProvider` is the hook.
  2. Per-panel DUMMY preview content in configure mode: Base panels draw sample data when configuring
     (ammo shows all types, vote shows a sample vote, weapons full grid, …); the port shows the forced
     frame only, except the panels that already carry config previews (centerprint, infomessages,
     pressedkeys, strafehud, engineinfo). Needs per-panel edits (files owned by other work this pass).
  3. Highlight border draws a flat-color frame, not Base's tiled `border_highlighted`/`_highlighted2` art;
     the cursor is the OS pointer with shape hints, not Base's drawn cursor art. Cosmetic.
  4. `hud_configure_menu_open` nuances: Base blends the highlighted panel's bg alpha and pins a preview
     into the open panel dialog (`HUD_Panel_UpdatePosSize_ForMenu`); the port opens dialogs over the game
     without the blend.
  5. The NetGame-standalone overlays (scoreboard/radar — see the hud-standalone-vs-manager memory) don't
     follow `hud_panel_*` cvars, so editing the radar rect moves the manager's (data-less) RadarPanel, not
     the live standalone radar.
  6. `_hud_panelorder` persists the port's discovery indices, not Base registry ids — a Base-exported cfg's
     panel order won't map onto the port (pre-existing).
  7. QC forces editor exit on `isdemo()` / `intermission == 2`; the port covers the scoreboard-up case only
     (no demo/intermission signal is wired into the editor).
  8. Event-based binds are swallowed faithfully while configuring, but POLLED input paths (e.g. the Pong
     key drive) aren't frozen. Minor.

### 54. Powerup pickups don't ghost-darken when taken *(general)*
- [ ] **Status:** Not started
- **Symptom:** Taken powerups (strength/shield) stay bright until respawn; other items correctly turn
  dark (the #29 ghost tint).
- **Expected (Base):** taken powerups ghost-darken like every other g_ghost_items pickup.
- **Notes:** #29's fix (`deabd99`, ghost/stay-weapon colormod) evidently doesn't cover the powerup
  classes — either they bypass the shared taken→ghost sink or their model tint path ignores the ghost
  colormod. Compare Base's Item_Show/ghost-items path for item_strength/item_shield vs the port's
  `ItemPickupRules` respawn-wait state + the #29 tint site.
- **First look:** the #29 implementation + `ItemPickupRules` powerup respawn path; Base
  `common/items` ghost handling (`g_ghost_items_color`, colormod `'-1 -1 -1'` family).

### 55. Remote client sees enemy names through walls / at all times **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** As the pure client, enemy nameplates show constantly — including players not visible
  on screen.
- **Expected (Base):** `shownames` gates enemy tags on actual VISIBILITY (LOS trace + on-screen +
  distance fade; teammates get the through-wall treatment, enemies never).
- **Notes (two suspects):** (a) the port's `ShowNamesLayer` draw gate lacks QC `Draw_ShowNames`'s
  LOS-trace/alpha logic (now tractable client-side: the pure client HAS the map collision since the
  client-map-load work — the trace can run locally); (b) server-side, check whether player/nameplate
  entities bypass the sv_cullentities_pvs cull (a wall-hidden enemy ideally isn't networked at all —
  though PVS ≠ LOS, so (a) is needed regardless).
- **First look:** `ShowNamesLayer` vs QC `client/shownames.qc` (the visibility/alpha function);
  `ServerNet.RelevantEntitiesFor` player treatment.
- **CONFIRMED root cause (2026-07-06, second pass):** the LOS/fade chain was fully ported and healthy —
  the first audit missed the layer's TEAMPLAY derivation: `_Draw` computed
  `bool teamplay = LocalTeam != Teams.None`. That is **the #27 FFA pants-team trap**: every FFA player
  carries a non-zero pants-color-derived team (`ScoreRowWire.Team` / `NetEntityState.Colormap` mirror
  it), so in DM `teamplay` read TRUE and any player sharing the local profile colors passed
  `sameTeamColor` → classified a TEAMMATE → `entcsPrivate/sameteam` → **the enemy LOS trace was skipped
  entirely** (shownames.qc:56's teammate arm) + unconditional fade-in + through-wall + the teammate
  status bar. Both playtest instances (and bots) ran default profile colors → every name visible all the
  time. **Fix:** the layer takes an explicit `Teamplay` flag fed from `GameScores.Teamplay` (the
  networked ScoreInfo truth — the same source #27 mandates); `LocalTeam` is documented as
  pants-poisoned-in-FFA and only compared under that gate. In FFA every non-self tag now takes the enemy
  path (LOS trace vs the real map + fades).

### 56. Warpzones broken for the remote client — render AND exit direction **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** As the pure client: the warpzone portal renderer is broken (placeholder/black), and
  traversal exits with the wrong angle/output direction.
- **Notes:** the RENDER half is a KNOWN limitation of the client-map-load round-2 work: portal
  window/disc renderers are constructed post-map-load, but the zone transforms live in the server-side
  `WarpzoneTrace.AmbientManager` and are NOT networked → dark-mirror placeholder only. The EXIT-ANGLE
  half is likely the same root: client prediction + the fixangle/view-rotation path (`WarpzoneFixView`)
  read those ambient zones, so a pure client predicts through the zone with no transform → wrong
  facing/velocity until reconcile snaps it.
- **Fix direction (promising):** the pure client now LOADS the map BSP — warpzones are static map
  entities (`trigger_warpzone` + targets), so the client can build the SAME zone set locally from its
  parsed BSP entities (no protocol change) and feed WarpzoneTrace/PortalRenderer/WarpzoneFixView.
  ⚠ read the `warpzone-angle-resolution` postmortem + memory before touching any of this.
- **First look:** where GameWorld links warpzone pairs at boot (reuse that pass client-side in
  `LoadClientMapFromServer`); `PortalRenderer.Setup` zone lookup; prediction's warp handling.
- **2026-07-06 fix-pass status: DEFERRED (scoped, not implemented).** Findings from this pass: Base
  NETWORKS zones (ENT_CLIENT_WARPZONE: source+target origin/angles + extents, sent once) and the client
  computes the transform (`WarpZone_SetUp`) + view (`WarpZone_camera_transform`) + traces
  (`WarpZone_TraceBox_ThroughZone`); on a crossing the server sends WARPZONE_TELEPORTED and the client
  rotates its VIEW ANGLES + pending predicted inputs (`CL_RotateMoves`). The port's plumbing is ambient:
  `GameWorld.Boot` → `Services.TraceImpl.SetWarpzoneManager(Warpzones)` publishes to
  `WarpzoneTrace.AmbientManager`, which prediction (TriggerTouch), PortalRenderer, and WarpzoneFixView all
  read — so the WHOLE feature lights up on a pure client the moment a locally-built `WarpzoneManager` is
  published on the client facade's TraceService. The blocker: zone construction/linking currently lives
  inside GameWorld's entity-spawn pipeline — it needs extracting into a standalone builder fed by the
  client-parsed BSP entity dicts (called from `LoadClientMapFromServer`). Do that extraction as its own
  change with the warpzone postmortem open.

### 57. Crosshair special-weapon ring (hagar load etc.) missing on the remote client **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** The around-crosshair weapon indicator (hagar secondary load, vortex charge, clip) shows
  on the host but not the pure client.
- **Notes (strong hypothesis from this session's work):** the DATA is already networked —
  `OwnerWeaponRings` rides the owner block into `ClientNet.LocalWeaponRings` ("feeds the crosshair
  rings on a PURE remote client") — but the CrosshairPanel's ring feed reads the local server Player's
  weapon-slot state, which on a pure client is now the HUD-mirror Player (`_hudMirror`, added this
  round) whose slot state is empty ⇒ no rings. Wire the ring values from `LocalWeaponRings` on the
  mirror path (stamp them onto the mirror's slot state in `UpdateHudMirror`, or feed the panel directly).
- **First look:** CrosshairPanel's ring value source + `NetGame.UpdateHudMirror`;
  `ClientNet.LocalWeaponRings` consumers (find who reads it today — possibly nobody).

### 58. Weapon coloring (profile/team tint) missing on the remote client **[client-map-load]**
- [ ] **Status:** Not started
- **Symptom:** Weapon models don't take the player color tint for the pure client (works on the host).
- **Notes:** #43 (r15, merged from main) wired the full color chain — and its own notes say the pure
  client SENDS its color (`color <n>`) and the entity wire carries packed `Colors`, including on the OWN
  entity slice (`ClientNet.LocalState.Colors`). So the remaining gap is the OWN-viewmodel tint source on
  a pure client: `UpdateViewModelColors` likely resolves colors from `LocalServerPlayer.ClientColors`
  (host-only). Read them from `LocalState.Colors` / stamp onto `_hudMirror` instead. Third-person
  weapons on OTHER players come from their entity Colors and may already work — verify which case the
  playtest saw.
- **First look:** `NetGame.UpdateViewModelColors` color source on a pure client; `_hudMirror` stamping;
  #43's fix-chain notes above.

### 59. Hit-sound confirmation doesn't fire for the remote client **[client-map-load]**
- [x] **Status:** Fixed (hitsound half; superseded by the hit/kill/typehit feedback parity rework,
  parity/hitsound-feedback, protocol v16) — damagetext-numbers half still open, see below
- **Symptom:** Landing hits as the pure client plays no hitsound (works on the host).
- **Resolution:** the whole feedback path was rebuilt to Base's stat model: the server banks per-frame
  `hitsound_damage_dealt`/`typehitsound`/`killsound` accumulators (damage.qc:611-661) and flushes them in
  EndFrame to HIT/TYPEHIT/KILL_TIME stats (+ the ceil'd cumulative total) with typehit > kill > hit
  priority (world.qc:2507). The stats ride the Feedback block (v16) and BOTH paths (host reads the live
  Player, pure client reads `ClientNet.LocalState`) feed the same faithful `HitSoundLogic` client machine
  (view.qc UpdateDamage/HitSound): accumulate-never-drop antispam beep, distinct `misc/kill` on frags,
  `misc/typehit` on team/chat hits. The old damagetext-event coupling is gone.
- **Also fixed (same branch):** the damage-number HUD over-show — the drain fed EVERY queued event to the
  layer, so a listen host drew numbers for every bot-vs-bot hit on the map (off-screen ones pinned near the
  crosshair by the 2D out-of-view fallback). Now gated per event by `DamagetextMutator.ShouldShowTo` — the
  QC `write_damagetext` per-viewer tiers (sv_damagetext 2 default = your own hits only; observers see all).
- **Still open (sibling):** the T51 damage-text layer remains fed by the server-side DamagetextMutator
  drain — floating damage numbers are still dead on a pure client (needs its own net channel).

---

## Pending
_More items to be added — playtest in progress._
