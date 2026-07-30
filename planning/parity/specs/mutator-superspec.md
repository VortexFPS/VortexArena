# Super Spectate (superspec) mutator — parity spec

**Base refs:** `common/mutators/mutator/superspec/sv_superspec.qc` (+ `sv_superspec.qh`, `_mod.inc`, `_mod.qh`) · `mutators.cfg:172` · `common/notifications/all.inc:271`
**Port refs:** NOT IMPLEMENTED (explicit deferral — see `../../TODO.md`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Super Spectate is a **server-side, admin/spectator-convenience** mutator (no gameplay effect on players).
When enabled (`g_superspectate 1`), it gives spectators and observers extra `cmd`-interface options to
automatically or manually switch which player they follow, plus pickup-announcement messages. It is
purely a presentation/UX feature layered onto the existing spectate system; it never alters combat,
movement, scoring, or item state. It runs entirely on the authority (SVQC) side and communicates with the
spectating client only through `sprint`/`centerprint` console/center messages and one notification.
The mutator is **off by default** and is only useful to real (human) clients in spectator/observer mode.

## Base algorithm (authoritative)

### Mutator registration + activation  (`_mod.inc`, `sv_superspec.qc:3-4`)
- `REGISTER_MUTATOR(superspec, expr_evaluate(autocvar_g_superspectate))`. SVQC-only.
- **Constant:** `g_superspectate` default **0** (`mutators.cfg:172`).

### Per-client option state + flag bitfields  (`sv_superspec.qc:8-25`)
Two per-client `.int` bitfields plus one filter string:
- `.autospec_flags` — auto-switch triggers:
  `ASF_STRENGTH=BIT(0)`, `ASF_SHIELD=BIT(1)`, `ASF_MEGA_AR=BIT(2)`, `ASF_MEGA_HP=BIT(3)`,
  `ASF_FLAG_GRAB=BIT(4)`, `ASF_OBSERVER_ONLY=BIT(5)`, `ASF_SHOWWHAT=BIT(6)`, `ASF_SSIM=BIT(7)`,
  `ASF_FOLLOWKILLER=BIT(8)`, `ASF_ALL=0xFFFFFF`.
- `.superspec_flags` — message verbosity:
  `SSF_SILENT=BIT(0)`, `SSF_VERBOSE=BIT(1)`, `SSF_ITEMMSG=BIT(2)`.
- `.superspec_itemfilter` — space-separated classname allowlist for item messages.

### Spectate helper  (`superspec_Spectate`, `sv_superspec.qc:27-31`)
Wraps the engine `Spectate(this, targ)` to switch the spectator's followed entity, returns true.

### Options persistence  (`superspec_save_client_conf` :33-61, load in ClientConnect :378-419)
- File format magic `"SUPERSPEC_OPTIONSFILE_V1"`.
- Filename: `superspec-local.options` for the local/listen host (`IS_LOCAL`); otherwise
  `superspec-<uri_escape(crypto_idfp)>.options`. If a remote client has no `crypto_idfp`, options are
  **not** saved/loaded (and the missing-UID notification fires, see below).
- Save writes magic, `autospec_flags`, `superspec_flags`, `superspec_itemfilter` (one per line).
- Load (on ClientConnect, only `IS_REAL_CLIENT`) reads the same back; verifies magic.
- **Defaults at connect:** `superspec_flags = SSF_VERBOSE`, `superspec_itemfilter = ""`.

### Missing-UID notification  (`superspec_hello` :370-376, ClientConnect :390-392)
- On ClientConnect, schedules a `new_pure(superspec_delayed_hello)` think 5 s later
  (`nextthink = time + 5`). The think fires `INFO_SUPERSPEC_MISSING_UID` (MSG_INFO, NOTIF_ONE_ONLY) if
  the client's `crypto_idfp == ""`, then deletes the helper entity.
- **Notification text** (`all.inc:271`): `"^F2You lack a UID, superspec options will not be saved/restored"`.

### Message helper  (`superspec_msg` :63-74)
- Always `sprint`s the console form. If `SSF_SILENT` set → no centerprint. If spamlevel>1 and not
  `SSF_VERBOSE` → no centerprint. Otherwise `centerprint`s the centered form.

### Item filter  (`superspec_filteritem` :76-89)
- Empty filter → all items pass. Otherwise tokenizes the filter and matches `item.classname`.

### ItemTouch hook  (`MUTATOR_HOOKFUNCTION(superspec, ItemTouch)` :91-139)
- Fires when any player touches an item. Loops `FOREACH_CLIENT` over spectators/observers only.
- If `SSF_ITEMMSG` and the item passes the filter: prints a pickup message (verbose form includes the
  classname). If `ASF_SSIM` set and not already following the toucher → auto-Spectate the toucher.
- Independently, for powerup/mega/flag grabs, if the matching `ASF_*` bit is set, auto-switch to the
  toucher: triggers are `item.invincible_finished` (SHIELD), `item.strength_finished` (STRENGTH),
  `item.itemdef==ITEM_ArmorMega` (MEGA_AR), `item.itemdef==ITEM_HealthMega` (MEGA_HP),
  `item.classname=="item_flag_team"` (FLAG_GRAB). `ASF_OBSERVER_ONLY` suppresses the switch unless the
  spectator is a true observer. `ASF_SHOWWHAT` prints what triggered the switch. Returns
  `MUT_ITEMTOUCH_CONTINUE` (never blocks the pickup).

### SV_ParseClientCommand hook  (:141-358)
Adds client `cmd`s (only for non-players, i.e. spectators/observers):
- `superspec_itemfilter [help|clear|show|<classnames>]` — manage the item filter.
- `superspec [help|clear|silent|verbose|item_message ...] [on|off|1|0]` — toggle message flags;
  short forms `si/ve/im`; prints current state via `OPTIONINFO`.
- `autospec [help|clear|strength|shield|mega_health|mega_armor|flag_grab|observer_only|show_what|item_msg|followkiller|all ...] [on|off]`
  — toggle auto-switch flags; short forms `st/sh/mh/ma/fg/oo/sw/im/fk/aa`.
- `followpowerup` — switch to first player with active Strength or Shield (status effects).
- `followstrength` — switch to first player with active Strength.
- `followshield` — switch to first player with active Shield.
- (`mutators.cfg` help text also mentions `followfc [red|blue]`, but no handler exists in this rev.)

### BuildMutatorsString / BuildMutatorsPrettyString  (:360-368)
- Appends `:SS` (short) and `, Super Spectators` (pretty) to the mutator list strings shown in
  scoreboard/serverinfo.

### PlayerDies hook  (:421-435)
- For each spectator with `ASF_FOLLOWKILLER` who is currently following the victim and the attacker is a
  player → auto-Spectate the attacker (with optional show-what message).

### ClientDisconnect hook  (:437-442)
- Saves the client's options file.

## Port mapping
**Nothing in this unit is implemented in the port.** There is no `SuperSpecMutator.cs` (or equivalent) in
`src/VortexArena.Common/Gameplay/Mutators/`, no `g_superspectate` cvar, no `autospec`/`superspec`/
`followpowerup`/`followstrength`/`followshield`/`superspec_itemfilter` client command, and the mutator
hook points it depends on (`ItemTouch`, `SV_ParseClientCommand`, `PlayerDies` spectator pass,
`ClientConnect`/`ClientDisconnect` options I/O, `BuildMutatorsString`) carry no superspec handler.

The single port artifact is the **registered-but-unfired** notification string
`Notifications.Info("SUPERSPEC_MISSING_UID", ...)` in
`src/VortexArena.Common/Gameplay/Notifications/NotificationsList.cs:636` — a faithful text port of the
Base notification, but it has **no caller** anywhere in the port (no `superspec_hello` equivalent), so it
can never fire.

The port's only spectate primitive is the core `"spectate"` client command
(`src/VortexArena.Server/ClientCommandRegistry.cs:57`); none of superspec's commands or auto-switch logic
build on it.

`../../TODO.md` lists superspec as an **explicit deferral** ("admin/niche P3"), confirming the omission is
intentional/known rather than an accidental miss.

## Parity assessment
- **Logic / values / timing / presentation / audio:** all **missing** — the entire mutator is absent.
  The notification text is the only faithful fragment, and it is dead.
- **Liveness:** `na` (no live code) for the mutator as a whole; the lone notification string is `dead`
  (registered, no caller).
- **Gaps (observable):**
  - `g_superspectate` cvar does not exist; setting it has no effect.
  - Spectators have none of the superspec `cmd` options (`superspec`, `autospec`, `followpowerup`,
    `followstrength`, `followshield`, `superspec_itemfilter`).
  - No auto-switch on powerup/mega/flag/item pickups or on killer; no pickup-announcement messages.
  - No per-client option persistence (`superspec-*.options`), no missing-UID notification ever fires.
  - Scoreboard/serverinfo mutator list never shows `:SS` / `Super Spectators`.
- **Intended divergence:** the *absence* is a documented deferral, but per schema rules this is recorded
  as `missing` gaps (not `intended_divergence`), because no port-specific behavior was substituted — it's
  simply not built. `intended_divergence:false`.

## Verification
- Base behavior: read in full from `sv_superspec.qc` (443 lines) + `sv_superspec.qh`, `_mod.inc`,
  `_mod.qh`; cvar default from `mutators.cfg:172`; notification from `all.inc:271`.
- Port absence: directory listing of `src/.../Gameplay/Mutators/` (no superspec file); grep for
  `superspec|autospec|g_superspectate|followpowerup|followkiller` across the whole port (only `../../TODO.md`,
  a legacy recon doc, and `NotificationsList.cs`); `ClientCommandRegistry.cs` has only core `spectate`;
  `../../TODO.md` explicit-deferral lines. High confidence.

## Open questions
- None. The unit is unambiguously not ported; the only nuance is the dead notification string, which is
  fully accounted for.
