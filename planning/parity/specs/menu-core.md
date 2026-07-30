# Menu core — parity spec

**Base refs:** `menu/menu.qc`, `menu/draw.qc`, `menu/matrix.qc`, `menu/command/menu_cmd.qc`,
`menu/mutators/events.qc`, `menu/item.qc` + `menu/item/*.qc` + `menu/anim/*.qc`,
`menu/xonotic/*.qc` (EXCEPT `dialog_settings_*` / `dialog_multiplayer_*` → menu-dialogs unit),
`menu/xonotic/guide/*.qc`
**Port refs:** `game/menu/**` (MenuRoot/MainMenu/PauseMenu/screens + `dialogs/` + `framework/`),
`game/Shell.cs` (wiring), `src/VortexArena.Common/Menu/DataSource.cs`,
`src/VortexArena.Common/Localization/LanguagesTxt.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-07-02

## Overview

The Base menu is a QuakeC VM: an immediate-mode widget tree (`menu/item/*` retained-object classes
drawn every frame by `m_draw`) hosting the Xonotic-skinned dialogs (`menu/xonotic/*`). This unit
audits it at **behavior level**: the VM lifecycle + `menu_cmd` surface, the main-menu nexposee fan,
every user-visible screen/flow it owns (server browser, map list, keybinder, campaign screen, media
browsers, credits, language/skin pickers, player-setup widgets, HUD-panel config dialogs, cvar
editor), and `menu/xonotic/util.qc`'s real behaviors (update check, tooltips, dependency engine).
The widget toolkit itself (`menu/item/*`, `menu/anim/*`, the `menu/xonotic` skinned wrappers) is an
**intended divergence**: Godot Controls + a Theme built from the same skin assets replace it.

Boundaries: campaign file/progress logic = `sv-campaign` (this unit owns only the screen); the
in-game HUD drag/resize editor = `cl-hud` (this unit owns the menu-side per-panel option dialogs);
the in-match map-vote screen = `sv-mapvoting` (no overlap); settings/multiplayer tab CONTENTS =
menu-dialogs unit (this unit owns the two tab containers and the embedded list widgets).

## Base algorithm (authoritative)

### VM lifecycle (`menu/menu.qc:m_init 61, m_draw 753, m_toggle 981, m_display 954, m_hide 969, m_goto 1056`)
- **Entry:** engine calls `m_init` once, `m_draw(width,height)` every frame while the menu is up.
- `m_draw`: first call finishes init (fonts, skin, `MainWindow` build), fades `menuAlpha` in at 5/s
  (out 2/s on hide; intro logo `menuLogoAlpha` 2/s), exports `_menu_alpha` for engine dimming, plays
  menu music once per reload via `cd loop $menu_cdtrack` (suppressed by
  `menu_no_music_nor_welcome`), and on disconnect (debounced `MIN_DISCONNECTION_TIME` = 1 s)
  reopens the menu and runs `menu_cmd directmenu Welcome RESET`.
- `m_toggle`: ESC semantics — at main-menu root with no game running, ESC is swallowed; with a game
  (`GAME_ISSERVER|GAME_CONNECTED`), ESC closes the menu.
- `m_goto(item, hide_on_close)`: opens a named dialog directly (`directmenu` verb), optionally
  hiding the whole menu when that dialog closes (used for in-game popups).
- Sounds: `m_play_focus_sound` (hover, 0.25 s debounce, `menu_sounds >= 2`) and
  `m_play_click_sound` (`menu_sounds >= 1`) with cues MENU_SOUND_OPEN/SELECT/EXECUTE/CLEAR/WINNER.
- Tooltips: `m_tooltip`/`gettooltip` (menu.qc:529-586) — `menu_tooltips` 0 off / 1 standard /
  2 advanced (appends cvar name(s), current value, `[default]`, and "Requires X in range…"
  dependency text); skinned box, fade in 5/s out 2/s, max 16 lines.
  `util.qc:setZonedTooltip 257` falls back to the engine `cvar_description` when no explicit text.

### menu_cmd (`menu/command/menu_cmd.qc:GameCommand 42`)
Verbs: `sync` (reload all cvar widgets), `directmenu <Item> [args…]` / `directpanelhudmenu`
(HUD-prefixed), `closemenu`, `nexposee`, `servers`, `profile`, `settings`, `inputsettings`,
`videosettings`, `skinselect`, `languageselect`, `dumptree`, `isdemo`,
`update_conwidths_before_vid_restart`. `directmenu` with no argument lists openable items; extra
args are queued into a buffer consumed by `readInputArgs` dialogs (how the server pushes
`Welcome HOSTNAME <h> WELCOME <text>` / `CAMPAIGN <id>`); refused during demo playback except
`Welcome`. Falls through to the `Menu_ConsoleCommand` mutator hook (`menu/mutators/events.qc`).

### Main window nexposee (`menu/xonotic/mainwindow.qc:MainWindow_configureMainWindow 84, MainWindow_draw 59`)
Six dialogs fanned as tiles: Singleplayer 0.80/24 rows, Multiplayer 0.96/24, Media 0.96/18,
Settings 0.96/18, Credits 0.50/20 (pulled to title bar), Quit 0.50/3 (pulled). Scale search shrinks
`s *= 0.99` until tiles don't overlap (`Nexposee_calc`); closed-tile alpha from
`SKINALPHAS_MAINMENU` (~0.85). First draw auto-opens the ToS dialog when the update server reports
a new ToS version (`_Nex_ExtResponseSystem_NewToS`) or the first-run welcome when name/language
were never chosen.

### Server browser (`menu/xonotic/serverlist.qc`)
- `refreshServerList 311` (RESET/ASK/REFILTER/RESORT modes): builds host-cache masks —
  full/empty via `SLIST_FIELD_FREESLOTS>=1` / `NUMHUMANS>=1`, `menu_slist_maxping` gate (only when
  `>0` and "show laggy" off), `menu_slist_modfilter`, text filter with `gametype:` prefix syntax,
  and banned-server masks from the update-response system.
- Categories (`SLIST_CATEGORIES`): Favorites / Recommended / Normal / XPM / Instagib / Overkill /
  Defrag / Modified headings parsed from qcstatus (`CategoryForEntry`), with recommended/promoted
  feeds from the update server.
- `setSortOrder 701`: default PING ascending; clicking the same column flips direction.
- `toggleFavorite 218`: edits the `net_slist_favorites` cvar (archived), preferring the 44-char
  crypto-id fingerprint over ip:port so favorites survive IP changes. INS/MOUSE3 toggles,
  SPACE/MOUSE2 opens server info, ENTER connects (`ServerList_Connect_Click 766`).
- Rows render mod/pure, AES-encryption level, stats, IPv4/IPv6 icons; ping colored by the
  `hud_panel_scoreboard_ping_*` gradient; full/empty rows alpha-dimmed. Auto-refresh on tab focus
  throttled to 10 s.

### Map list (`menu/xonotic/maplist.qc:refilter 178, g_maplistCacheToggle 44, MapList_LoadMap 274, keyDown 310`)
Strict `MapInfo_FilterGametype` filtering (autogenerates missing .mapinfo), preview image
`maps/<bsp>` → `levelshots/<bsp>` → `nopreview_map`; clicking the preview toggles membership in
`g_maplist`; +/- keys include/exclude, type-to-search (0.5 s timeout), Ctrl+F/Ctrl+U; double-click
opens map-info; `MapList_LoadMap` runs `menu_loadmap_prepare`, optional default hostname
(`menu_use_default_hostname` → "<name>'s Xonotic Server") then `map <bsp>`.

### Keybinder (`menu/xonotic/keybinder.qc:KeyBinds_BuildList 40, keyGrabbed 316, Bind_Clear 420, Bind_Reset_All 444, replace_bind 202`)
Catalog from `KeyBinds_BuildList` (movement, attack, weapon groups 1-9 + per-weapon tree with
group-vs-individual overrider semantics, misc: jetpack, zoom, 3rd person, spectate, chat, vote
yes/no, ready, quick menu, drop key/flag, user binds 1-32 with an editor dialog, dev sandbox/
waypoint binds). Up to `MAX_KEYS_PER_FUNCTION` = 2 keys shown/stored per action; grabbing a 3rd
evicts all and rebinds; ESC cancels grab, CAPSLOCK/NUMLOCK re-arm instead of binding. "Reset all"
= `unbindall; exec binds-xonotic.cfg`. `replace_bind` migrates legacy aliases.

### Campaign screen (`menu/xonotic/campaign.qc:loadCvars 68, drawListBoxItem 194, campaignGo 96`; `dialog_singleplayer.qc`)
Reveals `min(campaignIndex+2, entries)` rows (finished + current + one "???" peek); row alphas
0.6 selectable / 1.0 current / 0.2 future (description ×0.8); campaign cycler wraps with
`mod(j+step, n)`; skill radios `g_campaign_skill` −2/0/2. Start refuses locked levels. Instant
action rolls: 30% dm(2-8 bots), 25% ctf(4-12 step 2), 15% tdm(4-8 step 2), 10% ca, 10% ft, 5%
kh(6), else lms/dom(2-8/2)/ons(6-16/2)/as(4-16/2); `timelimit 10`, bots = players−1;
`makeServerSingleplayer` locks `net_address 127.0.0.1`. Winner dialog: `util.qc:preMenuDraw 573`
watches `g_campaign<name>_won` flip 0→1 → pops `dialog_singleplayer_winner` + MENU_SOUND_WINNER.

### Media tab (`dialog_media.qc` order Guide/Demos/Screenshots/Music Player)
- **Demos** (`demolist.qc`): enumerates `demos/*.dem` incl. one subdir level, filter glob, Play /
  Timedemo with disconnect-confirm dialogs (`dialog_media_demo_{start,time}confirm.qc`),
  `cl_autodemo` checkbox.
- **Screenshots** (`screenshotlist.qc`, `screenshotimage.qc`, viewer dialog): list + filter +
  viewer with zoom −/+/reset, prev/next, slideshow; `cl_autoscreenshot`.
- **Music player** (`dialog_media_musicplayer.qc`, `soundlist.qc`, `playlist.qc`): two panes —
  all `sound/cdtracks/*.ogg` vs `music_playlist_list0` (space-separated), random order cvar,
  menu-track set/reset (`menu_cdtrack`), transport via engine `cd`/music commands, [C]/[D] markers.
- **Guide** (`guide/*.qc`): 13 fixed topics (Introduction…Mods), entries per topic with
  `describe()` text pages and keyword filter.

### util.qc behaviors
- **updateCheck (456)** + `UpdateNotification_URI_Get_Callback (316)`: queries
  `https://update.xonotic.org/checkupdate.txt` (with `cl_startcount` incremented each boot),
  `vercmp` against `g_xonoticversion` → pulsing "Update to X now!" banner in `preMenuDraw (541)`;
  also delivers banned-server masks, promoted/recommended server lists, new-ToS version, and an
  emergency hotfix pk3 (`curl --pak` + exec).
- **Helpers:** `saveAllCvars/loadAllCvars (49)` widget walk; `setDependent` family (127-255) —
  range/AND/OR/string/function/NOT dependency-driven widget disabling; `CheckSendCvars (825)`
  pushes changed cvars to a connected server (`sendcvar`); `isServerSingleplayer/
  makeServerSingleplayer (804)`; `GameType_GetID/GetCount/GetName/GetIcon (664-707)` — menu
  gametype order dm tdm ctf ca ft mayhem tmayhem ka tka kh lms dom nb ons as surv (+rr cts inv when
  `menu_create_show_all`); `dialog_hudpanel_main_checkbox/main_settings (722-802)` — the shared
  per-panel background/border/alpha/team/padding settings block.
- **Lists:** `languagelist.qc` (languages.txt rows with translation-% dimming, sets
  `prvm_language` + `menu_restart`, in-match languageWarning dialog); `skinlist.qc` /
  `hudskinlist.qc` (scan `gfx/menu/*/skinvalues.txt`; apply = `menu_skin` + `menu_restart`);
  `statslist.qc` (Profile XonStat summary from PlayerStats_PlayerDetail); `playerlist.qc`
  (score/ping/team-colored rows in server info); `cvarlist.qc` (search name+optionally
  descriptions, edit, revert, seta-promotion bookkeeping via `menu_forced_saved_cvars`).
- **Player-setup widgets:** `playermodel.qc` (models/player/*.txt metadata, hidden filter, title
  sort), `charmap.qc`, `colorpicker*.qc`, `crosshairpicker.qc` (`crosshair = 31 + cell`),
  `crosshairpreview.qc`.

## Port mapping

| Base | Port |
|---|---|
| m_init/m_draw/m_toggle/m_display/m_hide/m_goto | `MenuRoot.cs` screen stack + `Shell.cs` HandleToggleMenu/OpenMenuDialog (live) |
| menu music + click/focus sounds | NOT IMPLEMENTED (`menu_sounds` bound in Audio dialog, inert) |
| tooltips (menu_tooltips modes) | Godot TooltipText on every widget; mode cvar not consulted |
| draw.qc/matrix.qc primitives | `MenuSkin.cs` theme + Godot 2D (intended divergence) |
| menu_cmd verbs | `MenuCommand.cs:Dispatch 112` + `MenuDialogRegistry.cs` (live; no arg-buffer/dumptree/isdemo) |
| item/* + anim/* toolkit | Godot Controls + `CvarControls.cs` two-way cvar binding (intended divergence) |
| xonotic skinned wrappers + datasource.qc | `CvarControls.cs`, `MenuSkin.cs`, `Common/Menu/DataSource.cs` (intended divergence) |
| mainwindow.qc nexposee | `MainMenu.cs` (sizes/pull/alpha faithful; no ToS/first-run auto-open) |
| dialog_multiplayer/settings containers | `MultiplayerScreen.cs` / `dialogs/DialogSettings.cs` (tab order faithful; legacy `SettingsScreen.cs` is dead) |
| dialog_gamemenu + leavematchbutton | `PauseMenu.cs` (fill order faithful) |
| dialog_quit | `QuitDialog.cs` (faithful) |
| Welcome / TeamSelect / FirstRun+ToS / Uid2Name / Winner / MonsterTools / LanguageWarning | dialog classes exist under `game/menu/dialogs/` but are DEAD — no live opener (dev `--menu-screen` only) |
| serverlist.qc | `ServerBrowser.cs` + `MultiplayerScreen.cs` (real OOB master/LAN reimpl; no categories/recommended/crypto-id favorites/icons) |
| maplist.qc | `MapList.cs` + `CreateGameScreen.cs` (permissive no-mapinfo filtering; no type-to-search) |
| gametypelist/weaponslist/weaponarenacheckbox | `CreateGameScreen.cs`, `WeaponPriorityList.cs`, `DialogMutators.cs` |
| keybinder.qc | `KeyBindings.cs` + `KeyCaptureButton.cs` + `DialogSettingsInput.cs` (24 actions, 1 key each) |
| campaign.qc + dialog_singleplayer | `SingleplayerScreen.cs` (frontier/alphas/instant-action faithful; cycler clamps) |
| demolist + confirm dialogs | `DialogMediaDemo.cs` stub (no enumeration/playback) |
| screenshotlist/viewer | `DialogMediaScreenshot.cs` + `DataSource.cs:ScreenshotSource` (no viewer pane) |
| musicplayer/soundlist/playlist | `DialogMediaMusicPlayer.cs` + SoundSource (transport inert) |
| guide/*.qc | `MediaScreen.cs` + `DialogMediaGuide.cs` (topics complete, entries partial) |
| credits.qc | `CreditsScreen.cs` (names verbatim; 28 px/s vs 1 row/s) |
| languagelist.qc | `Localization.cs` + `LanguagesTxt.cs` + `DialogSettingsUser.cs` (live; no % column, warning dialog dead) |
| skinlist.qc / hudskinlist.qc | `DialogMediaSkinList.cs` + `MenuSkin.Reload` / hud-skin picker MISSING (cvar itself live) |
| statslist.qc / playerlist.qc | NOT IMPLEMENTED (Profile stats pane + server-info player rows) |
| playermodel/charmap/colorpicker/crosshairpicker | `PlayerModelSelector.cs`, `CharmapPicker.cs`, `HslColorPicker.cs`, `CrosshairPicker.cs`/`CrosshairPreview.cs`, `PickerGrid.cs` |
| dialog_hudpanel_* ×22 + hudsetup_exit | `DialogHudPanel*.cs` ×22 + `DialogHudSetupExit.cs` + `HudPanelCommon.cs` (live via menu_showhudoptions) |
| dialog_sandboxtools / dialog_monstertools | `DialogSandboxTools.cs` (live) / `DialogMonsterTools.cs` (dead) |
| util.qc updateCheck/ext-response | NOT IMPLEMENTED |
| util.qc setDependent / save-load walk | `CvarControls.cs:Dependent.Bind` + `MenuState.cs` event binding (partial) |
| cvarlist.qc | `DialogCvarList.cs` (no descriptions/only-modified) |

## Parity assessment

- **Logic:** the navigable skeleton (fan, tab containers, pause menu, quit, campaign, browsers,
  keybinder, HUD-panel dialogs) is behavior-faithful. The systematic failure mode is
  **event-triggered dialogs**: everything Base opens *at* the player (Welcome/MOTD, TeamSelect,
  Uid2Name consent, first-run, ToS push, campaign Winner, language warning, MonsterTools) exists as
  a ported class with **no live opener** — `MenuCommand` drops `directmenu` args and no server/
  client code sends the triggers.
- **Values:** spot-verified faithful where cited (nexposee sizes, campaign frontier/alphas/
  instant-action table, teamselect bits, crosshair id math, playlist cvars). Divergences:
  maxping fallback 300, credits 28 px/s, single-key binds.
- **Timing:** mostly na; menu fade/animation rates unmeasured (`timing: unknown` where relevant).
- **Presentation:** intended-divergence baseline (Godot Controls, same skin art). Real gaps:
  server-row icons/categories, screenshot viewer, translation-% column, tooltip advanced mode.
- **Audio:** globally missing — no menu music, no click/focus sounds anywhere.
- **Liveness:** dead set enumerated above; also `SettingsScreen.cs` (legacy duplicate) and
  `DialogLanguageWarning`. Everything reachable from the fan/pause menu is live.

## Verification

- YAML per-row `verification` blocks: code-path reads (file:line), grep-for-caller liveness checks
  (DialogWinner/MonsterTools/LanguageWarning/TeamSelect/Welcome/Uid2Name/FirstRun/ToS confirmed
  definition-only or Shell.cs:181-184 dev-only, re-verified 2026-07-02), and unit tests
  (`MenuDataSourceTests.cs`, `MenuPickerLogicTests.cs`, `BindsTests.cs`, `ServerInfraTests.cs`).
- Base line numbers re-checked against the pinned rev (menu.qc 61/753/954/969/981/1056/1102/1111;
  serverlist.qc 218/311/701; util.qc 49/257/316/456/664).
- No in-game behavioral checks were run for this audit (menu screens were not screenshot-diffed).

## Open questions

- Gametype list order + `menu_create_show_all` hidden rows: port registry order not diffed
  against the GAMETYPES macro (row `menu-core.create.gametypelist`, values: unknown).
- Player-model roster source vs `get_model_parameters` metadata (hidden filter, sex/skin, title
  sort) — needs a side-by-side of the selector contents.
- Leave-match button text variants across the four states (demo/campaign/singleplayer/multiplayer).
- Whether the port switches language live while connected (Base blocks + warns).
- Demo browser backend: the demo-cinematics track (.xgd) will land a player — decide whether the
  browser enumerates .xgd (intended divergence) or also legacy .dem.
