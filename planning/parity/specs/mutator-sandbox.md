# Sandbox mutator — parity spec

**Base refs:** `common/mutators/mutator/sandbox/sv_sandbox.qc`, `common/mutators/mutator/sandbox/sv_sandbox.qh`, `menu/xonotic/dialog_sandboxtools.qc`, `server/cheats.qc` (Drag_* grab integration), `server/mutators/events.qh` (Sandbox_* hooks), `mutators.cfg` (g_sandbox* cvars)
**Port refs:** `game/menu/dialogs/DialogSandboxTools.cs` (menu UI only) · server/gameplay backend: NOT IMPLEMENTED
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Sandbox is a server-side "creative/build" mutator (`g_sandbox 1`) that lets players spawn arbitrary
models as physics-capable `object` entities, then edit, color, scale, attach, duplicate, claim, and
drag them around the map. It is enabled per-server; once active, players drive it entirely through the
`sandbox …` console command (or the in-game Sandbox Tools menu, which just emits those commands).
Objects persist to a per-map text storage file that auto-saves on a timer and auto-loads at map start.
Material-tagged objects play impact particles and sounds when they collide above a velocity threshold.
The mutator hooks the engine's generic drag/grab system (shared with `g_monsters_edit`) so objects can
be physically carried with the "drag object" key (+button8).

## Base algorithm (authoritative)

### Mutator registration + autoload  (`sv_sandbox.qc:REGISTER_MUTATOR / MUTATOR_ONADD`)
- **Trigger / entry:** `REGISTER_MUTATOR(sandbox, expr_evaluate(autocvar_g_sandbox))` — active only when the
  `g_sandbox` cvar expression is truthy. Authority (SVQC only; the whole file is `#ifdef SVQC`).
- **Algorithm:** on add, create the `g_sandbox_objects` IntrusiveList, set the first `autosave_time =
  time + g_sandbox_storage_autosave`, and if `g_sandbox_storage_autoload` call `sandbox_Database_Load()`.
- **Constants:** `g_sandbox 0` (default off), `g_sandbox_storage_autoload 1`, `g_sandbox_storage_autosave 5` (s).

### Object spawn  (`sv_sandbox.qc:sandbox_ObjectSpawn`)
- **Trigger:** `object_spawn <model>` command, `object_duplicate paste`, and storage load.
- **Algorithm:** `new(object)`, push to `g_sandbox_objects`; `takedamage = DAMAGE_AIM`,
  `damageforcescale = 1`, `solid = SOLID_BBOX`, `movetype = MOVETYPE_TOSS`, `frame=0 skin=0 material=null`;
  set touch=`sandbox_ObjectFunction_Touch`, think=`sandbox_ObjectFunction_Think`, `nextthink = time`.
  For a live (non-database) spawn: stamp owner UID (`crypto_idfp`, warn if player has none), `netname`=owner,
  `message`/`message2`=creation/edit time (`strftime "%d-%m-%Y %H:%M:%S"`); trace forward from
  `origin+view_ofs` along `v_forward * g_sandbox_editor_distance_spawn` (WarpZone_TraceLine) and `setorigin`
  to `trace_endpos`, `angles_y = v_angle.y`. `CSQCMODEL_AUTOINIT(e)`. `++object_count`.
  The spawn command first checks: flood timer (`time < player.object_flood`), `object_count >= maxobjects`,
  model arg present, and `fexists(model)`. On success `player.object_flood = time + g_sandbox_editor_flood`.
- **Constants:** `g_sandbox_editor_distance_spawn 200`, `g_sandbox_editor_flood 1` (s),
  `g_sandbox_editor_maxobjects 1000`.

### Object think — grab class + owner resync  (`sv_sandbox.qc:sandbox_ObjectFunction_Think`)
- **Trigger:** per-object think, reschedules every frame (`nextthink = time`).
- **Algorithm:** set `.grab`: `0` if readonly or `Sandbox_DragAllowed` hook returns true; else `1`
  (owner-only) when `g_sandbox_editor_free < 2 && crypto_idfp`; else `3` (anyone). Then scan all real
  clients to (re)bind `realowner` by matching `crypto_idfp` (clears owner if the owning player left;
  bots cannot own objects). `CSQCMODEL_AUTOUPDATE(this)` for client networking.
- **Constants:** `g_sandbox_editor_free 1`. `.grab` values 0/1/3 (2=team is set by drag code, not here).

### Object touch — material impact FX  (`sv_sandbox.qc:sandbox_ObjectFunction_Touch`)
- **Trigger:** physics touch callback; only if `this.material` set and `touch_timer <= time` (rate-limited
  to every 0.1 s).
- **Algorithm:** `intensity = (|this.velocity| + |toucher.velocity|) / 2`; bail if
  `< g_sandbox_object_material_velocity_min`; subtract that min, then `intensity = bound(0, intensity *
  g_sandbox_object_material_velocity_factor, 1)`. Play `_sound(CH_TRIGGER,
  "object/impact_<material>_<1..5>.wav", VOL_BASE*intensity, ATTEN_NORM)` (random of 5) and
  `Send_Effect_("impact_<material>", origin, '0 0 0', ceil(intensity*10))` (count 1..10).
- **Constants:** `g_sandbox_object_material_velocity_min 100`, `g_sandbox_object_material_velocity_factor 0.002`,
  touch_timer 0.1 s, 5 sound variants. Default materials: metal, stone, wood, flesh.

### Edit-target trace + permissions  (`sv_sandbox.qc:sandbox_ObjectEdit_Get`)
- **Trigger:** every edit/remove/attach/info/claim subcommand.
- **Algorithm:** `crosshair_trace_plusvisibletriggers`; reject if beyond `g_sandbox_editor_distance_edit`
  or `trace_ent.classname != "object"`. If `permissions` false, return target unconditionally. Otherwise
  allow if the object had no owner UID, or the player owns it, or `g_sandbox_editor_free >= 2`.
- **Constants:** `g_sandbox_editor_distance_edit 300`.

### Object edit properties  (`sv_sandbox.qc` SV_ParseClientCommand `object_edit` case)
- `skin`(stof) · `alpha`(stof) · `color_main`→`colormod`(stov) · `color_glow`→`glowmod`(stov) ·
  `frame`(stof) · `scale`→`sandbox_ObjectEdit_Scale` · `solidity` 0=SOLID_TRIGGER/1=SOLID_BBOX ·
  `physics` 0=MOVETYPE_NONE/1=MOVETYPE_TOSS/2=MOVETYPE_PHYSICS · `force`→`damageforcescale`(stof) ·
  `material`→strzone + precache 5 impact sounds (null clears). Updates `message2` edit timestamp.
  (Note Base bug: the `solidity` case has no `break;` so it falls through into `physics`.)

### Object scale  (`sv_sandbox.qc:sandbox_ObjectEdit_Scale`)
- `scale = bound(g_sandbox_object_scale_min, f, g_sandbox_object_scale_max)`, `_setmodel` to reset
  mins/maxs, then `setsize(RoundPerfectVector(mins*scale), RoundPerfectVector(maxs*scale))` (fixes #2742).
- **Constants:** `g_sandbox_object_scale_min 0.1`, `g_sandbox_object_scale_max 2`.

### Attach / detach  (`sv_sandbox.qc:sandbox_ObjectAttach_Set / _Remove`)
- Set: detach first, persist `old_solid`/`old_movetype`, force `MOVETYPE_FOLLOW`, `SOLID_NOT`,
  `DAMAGE_NO`, `setattachment(e, parent, tag)`, `owner = parent`. Remove: for each child, capture
  `gettaginfo` origin, clear attachment, restore origin/angles/solid/movetype, `DAMAGE_AIM`.

### Duplicate (copy / paste)  (`object_duplicate`)
- `copy`: `sandbox_ObjectPort_Save(e, false)` → escape quotes → `stuffcmd set <cvar> "<props>"`
  (clipboard cvar, default `cl_sandbox_clipboard`). `paste`: flood + maxobjects checks, then
  `sandbox_ObjectPort_Load(player, clipboard, false)`.

### Claim / info  (`object_claim`, `object_info object|mesh|attachments`)
- Claim: requires player UID; updates `netname`, then `crypto_idfp` to the player's. Info prints
  owner/dates, mesh tags (`FOR_EACH_TAG`), or attachment list.

### Storage save/load  (`sandbox_Database_Save / _Load`, `sandbox_ObjectPort_Save / _Load`)
- Save (gated by `Sandbox_SaveAllowed` hook): file `sandbox/storage_<name>_<map>.txt`, header comment,
  one line per non-attached object via `sandbox_ObjectPort_Save(it, true)`. Serializes (per object, with
  child attachments in slots 1..16): origin, angles (db only), model, skin, alpha, colormod, glowmod,
  frame, scale, solidity, physics, damageforcescale, material, and db-only crypto_idfp/netname/dates.
  Children also store the attach tag name. `MAX_STORAGE_ATTACHMENTS = 16`.
- Load: read each non-comment line, `sandbox_ObjectPort_Load(NULL, line, true)`, precache material sounds.
- **Constants:** `g_sandbox_storage_name default`.

### Auto-save tick  (`SV_StartFrame` hook)
- Every frame: if `g_sandbox_storage_autosave` set and `time >= autosave_time`, bump the timer and
  `sandbox_Database_Save()`.

### Command gating + read-only  (`SV_ParseClientCommand` head)
- All `g_sandbox` commands rejected (with a chat notice) when `g_sandbox_readonly` or the
  `Sandbox_EditAllowed` hook returns true. `g_sandbox` with <2 args prints the active-mode hint.
- **Constants:** `g_sandbox_readonly 0`, `g_sandbox_info 1` (logging verbosity 0/1/2).

### Drag / grab integration  (`server/cheats.qc:Drag_*`)
- The generic drag system (also used by monster-edit) reads `.grab` to decide pickup rights: 0=no,
  1=owner, 2=owner+team, 3=anyone, gated by `g_grab_range 200`. Held via `+button8` (keybind
  "drag object (sandbox)"). Drag_Begin sets `MOVETYPE_WALK`, gravity≈0; Drag_Finish restores.

### Menu (presentation)  (`menu/xonotic/dialog_sandboxtools.qc`)
- A dialog of cvar-bound inputs/sliders/colorpickers + command buttons that emit the `sandbox …`
  commands; bound to `menu_showsandboxtools` keybind. Slider ranges: skin/frame 0..99 step 1,
  alpha 0.1..1 step 0.05, scale 0.25..2 step 0.05, force 0..10 step 0.5. Clipboard cvar
  `cl_sandbox_clipboard`.

## Port mapping
- **Mutator registration / activation:** NOT IMPLEMENTED. No `SandboxMutator.cs` exists in
  `src/VortexArena.Common/Gameplay/Mutators/`; sandbox is absent from the mutator registry.
- **`sandbox` console command + all `object_*` subcommands:** NOT IMPLEMENTED. No server command
  handler matches `sandbox`/`object_spawn`/`object_edit`/`object_remove`/`object_attach`/
  `object_duplicate`/`object_claim`/`object_info` anywhere in `src/`. The `commands.cfg` alias
  (`alias sandbox "cmd g_sandbox ${* ?}"`) ships in data but reaches no backend.
- **Object entity / spawn / edit / scale / attach / touch FX / think:** NOT IMPLEMENTED. There is no
  `object` entity class and no per-object think/touch in the port.
- **Storage save/load + auto-save:** NOT IMPLEMENTED.
- **Drag/grab (`.grab`, Drag_*):** NOT IMPLEMENTED (the port has no generic object-drag system).
- **Sandbox_DragAllowed / Sandbox_SaveAllowed / Sandbox_EditAllowed hooks:** NOT IMPLEMENTED.
- **Cvars (`g_sandbox*`):** the defaults SHIP in `Base/data/.../mutators.cfg` (identical values to Base)
  but nothing reads them on the gameplay path.
- **Menu — Sandbox Tools dialog:** IMPLEMENTED as `game/menu/dialogs/DialogSandboxTools.cs`, registered
  in `game/Shell.cs` (`"sandbox" => new DialogSandboxTools()`). It is a faithful UI port (correct cvar
  bindings, slider ranges, command strings). However, its own doc-comment states every action button
  drives the server-side backend "VortexArena does not have yet, so they route through MenuCommand and
  are logged inert." So the UI is live but its outputs are no-ops.

## Parity assessment
- **Gaps:** The entire gameplay subsystem is missing. With `g_sandbox 1` set on a port server, no objects
  can be spawned/edited/dragged, nothing persists, and no impact FX play. The Sandbox Tools menu opens and
  its controls move, but pressing any button does nothing observable in-world (commands logged inert). The
  Base default is `g_sandbox 0`, so a stock match is unaffected — the divergence only surfaces when an
  operator explicitly enables sandbox.
- **Liveness:** The menu dialog is live (reachable via Shell). The server backend is `na` (no code). The
  cvars are dead (parsed into config but never read by gameplay code).
- **Intended divergences:** None declared. This is an unimplemented feature, not a deliberate change.

## Verification
- Base behavior: read in full from `sv_sandbox.qc` (834 lines), `sv_sandbox.qh`, `_mod.inc`, the
  `Sandbox_*` hook decls in `server/mutators/events.qh`, the `.grab`/Drag_* consumer in
  `server/cheats.qc`, the menu in `dialog_sandboxtools.qc`, and the cvar defaults in `mutators.cfg`.
- Port absence: grep across `src/**` and `game/**` for `sandbox`, `g_sandbox`, `object_spawn`,
  `object_duplicate`, `ObjectSpawn`, `SandboxObject` — the only hits are `DialogSandboxTools.cs`,
  its Shell registration, skinvalues color/alpha entries, and unrelated comment uses of the word
  "sandbox(ed)". The mutator directory listing confirms no `SandboxMutator.cs`.
- Menu: `DialogSandboxTools.cs` read in full; cvar names, slider ranges, and command strings match Base.

## Open questions
- None on Base behavior. The only decision for the port is whether sandbox is in scope at all (it is an
  optional, off-by-default creative mode). If pursued, it needs a server entity type, a command parser,
  the drag/grab system, and per-map text-file storage — none of which exist yet.
