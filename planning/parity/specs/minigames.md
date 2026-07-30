# Minigames — parity spec

**Base refs:** `common/minigames/{minigames,sv_minigames,cl_minigames,cl_minigames_hud}.qc/.qh` + `common/minigames/minigame/{bd,c4,nmm,pong,pp,ps,ttt}.qc/.qh` + `minigame/all.qh`
**Port refs:** `src/VortexArena.Common/Gameplay/Minigames/*` (rules) · `src/VortexArena.Server/Commands.cs`, `GameWorld.cs` (host) · `game/net/{MinigameClient,MinigameNetState,ServerNet,ClientNet,NetGame}.cs` (wire) · `game/hud/{MinigameRenderer,MinigameMenu,MinigameHelpPanel,HudManager}.cs` (view)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-07-02

## Overview

Xonotic ships seven in-server board/arcade minigames (Nine Men's Morris, Tic Tac Toe, Connect
Four, Pong, Peg Solitaire, Push-Pull, Bulldozer) playable from inside a match: a player types
`cmd minigame create <game>` (or uses the HUD minigame menu), other players join or are invited,
and the board renders as a HUD overlay while the players keep spectating/observing the match.
The server owns all rules (`sv_minigames.qc` + each game's `<id>_server_event`); state reaches
clients through linked entities (`minigame_SendEntity` / MSLE table); CSQC draws the four
minigame HUD panels and turns clicks/keys into `cmd minigame …` lines. Gate cvar:
`sv_minigames 1` (minigames.cfg). The registry rows in `registry/minigames.yaml` carry the
per-feature verdicts; this doc records the Base algorithms and the port's architecture mapping.

**Boundary:** the four HUD panel *shells* (MINIGAMEBOARD/STATUS/HELP/MENU registration,
`hud_panel_minigame*` pos/size cvars) are owned by `registry/cl-hud.yaml` row
`cl-hud.panel.minigame`. This unit owns the framework, server commands, client glue, menu/input
*behavior*, and every game's rules + view content.

## Base algorithm (authoritative)

### Framework (`minigames.qc/.qh`, `minigame/all.qh`)
- **Registry:** `REGISTER_MINIGAME(id, name)` builds the descriptor list (7 games, `_mod.inc`
  ordering); `minigame_get_descriptor(id)` (minigames.qc:5) resolves it. Each descriptor's
  `minigame_event(mg, "start"|"end"|"join"|"part"|"cmd"|"impulse"|"network_send"|…)` string
  dispatcher is the whole game API (minigames.qh:44-97).
- **Tile math** (minigames.qc:12-64): tile id = `<letter><1-based number>`, row 0 at the
  BOTTOM. `minigame_tile_letter/number/pos/name`, `minigame_relative_tile`. Teams cycle with
  `minigame_next_team(curr, n) = curr % n + 1` (:65); `minigame_prev_team` (:71) is dead in Base.
- **MSLE networking:** `msle_spawn` (:87) creates owned sub-entities (board pieces, players);
  the `MINIGAME_SIMPLELINKED_ENTITIES` table (`minigame/all.qh:100-105`) declares per-class
  field serializers; `minigame_SendEntity` (sv_minigames.qc:64) writes header + SendFlags-gated
  deltas; `minigame_CheckSend` (:118) gates to session members; `minigame_resend` (:108) forces
  `MINIG_SF_ALL`. Client: `NET_HANDLE(ENT_CLIENT_MINIGAME)` (cl_minigames.qc:179) rebuilds
  sessions/players/pieces; a `minigame_player` whose team-slot matches the local player triggers
  `activate_minigame`.

### Server session lifecycle (`sv_minigames.qc`)
- `start_minigame` (:164): IS_REAL_CLIENT gate; spawn session, netname
  `"<gameid>_<etof(owner)>"`, descriptor `"start"` event, add creator (join event assigns team;
  refusal ⇒ end+null). `join_minigame` (:200) same via `"join"`.
- `minigame_addplayer` (:127): parts any previous game first; spawns the `minigame_player`
  pointer; `sv_minigames_observer` 1/2 moves the player to observer (2 = forced); `"part"` of
  the last player ends the session. `player_clear_minigame` (:6) restores
  `MOVETYPE_WALK`/team-forcing on leave. `end_minigames` (:255) sweeps all sessions at match
  reset. All lifecycle events write `:minigame:<event>` sv_eventlog lines.
- `invite_minigame` (:263): validation chain (valid game / valid player / not self / not banned
  / not already in it) then `Send_Notification(NOTIF_ONE, player, INFO_MINIGAME_INVITE,
  session.netname, inviter.netname)` — the TARGET gets a notification with the game icon.
- `ClientCommand_minigame` (:310): `cmd minigame
  create|join|list|list-sessions|end|part|invite` + usage dump; unknown verb while in a game ⇒
  the game's `"cmd"` event (moves). `g_playban_minigames` gates banned players
  (CENTER_JOIN_PLAYBAN centerprint). `MinigameImpulse` (:296) forwards impulses to `"impulse"`
  (no shipped game implements it).

### Client glue (`cl_minigames.qc`)
- `activate_minigame` (:114) / `deactivate_minigame` (:83): set `active_minigame` +
  `minigame_self`, auto-open/close the minigame menu, run `HUD_MinigameMenu_CurrentButton`.
- `minigame_cmd_workaround` (:405): `localcmd("cmd minigame …")` — every client action is a
  server command.
- `minigame_prompt` (:416): HUD_Notify_Push of `minigames/<game>/icon_notif` + "It's your
  turn" when a turn-update for your team arrives while the menu is closed (called by
  c4/ttt/pp/nmm `network_receive`).
- Draw helpers: `minigame_hud_simpleboard` (:4, board pic in panel rect), `minigame_texture`
  (:49, `gfx/hud/<menu_skin>/minigames/…` with default-skin fallback + auto re-resolve on skin
  change), `minigame_show_allspecs` (:249, spectator name list above the board),
  `minigame_drawstring_wrapped` (:345). Board square: `minigame_hud_fitsqare` (qh:7) = centered
  min(w,h) square *of the panel rect* (so `hud_panel_minigameboard_pos/size` matter).
- `HUD_Command` hook (:428): console `hud minigame` toggles the menu (no default bind in Base);
  refuses during demo playback.

### Menu + input (`cl_minigames_hud.qc`)
- Menu tree (`HUD_MinigameMenu_Click*`): Create (per-game entries with 22px icons), Join
  (walk known sessions), Invite (every other connected player ⇒ `invite #n`), Current Game
  (Quit + per-game `HUD_MinigameMenu_CustomEntry` items), Exit Menu. Custom entries per game:
  ttt Next-Match/Single-Player, pong Start-Match/Add-AI/Remove-AI, pp Next-Match, bd
  Next-Level/Restart/Editor/Save.
- `HUD_Minigame_InputEvent` (:614): menu keyboard nav (up/down/home/end/enter/esc); board keys
  and mouse forwarded to the active game's `<id>_client_event("key_pressed"/"mouse_moved"/
  "mouse_pressed")` — every grid game supports full arrow-key/enter play and hover ghosts;
  bd is arrow-keys ONLY. Force-closes during map vote (`mv_active`).

### The games (server rules ⇢ client view, one line each; see YAML rows for verdicts)
- **ttt** (3×3): place on empty tile on your turn; win = full row/col/diagonal
  (`ttt_winning_piece` :39); draw at 9 pieces. `next` rematch is a BOTH-players handshake in
  multiplayer (`ttt_next_match` :110). `singleplayer` seats a CSQC-side AI (win > block >
  random, :468); joining an AI game is refused (:160-162). Won-match counter per player.
- **c4** (7×6): move names a column; piece falls to `c4_get_lowest_tile` (:140); win = 4-run
  (:37); draw at 42. View: `board_under` → pieces → `board_over` overlay, column hover ghost.
- **nmm** (7×7 graph, 3 concentric squares, **7** pieces/side — not the classical 9): place /
  move-adjacent / fly-at-3 phases; closing a mill (`nmm_in_mill` :188) grants a take (TAKEANY
  if all opposing pieces are milled); lose at <3 pieces or no legal move.
- **pong** (real-time): balls think at `sys_ticrate`; paddle bounce steers by hit offset
  (`pong_paddle_bounce` :103) + random jitter ±(2π/6)/2; scores to the last toucher, own-goal
  decrements (`pong_add_score` :77); 2-4 seats, AI paddles fillable/replacing leavers
  (`pong_ai_think` :211, thinkspeed 0.1s, tolerance 0.33). All 8 `sv_minigames_pong_*` cvars
  (paddle_size 0.3, paddle_speed 1, ball_wait 1, ball_speed 1, ball_radius 0.03125,
  ball_number 1, ai_thinkspeed 0.1, ai_tolerance 0.33) are live tuning knobs.
- **pp** (7×7): edge-owned pieces; place adjacent to your CURRENT piece chain; captures score;
  team-5 = disabled pieces (`pp/piece_taken` pic; current piece gets `pp/piece_current`
  marker); ends when surrounded, higher score wins; both-players rematch handshake.
- **ps** (English cross, 33 cells, centre empty): move = `<from> <to>` jump over an adjacent
  peg into the empty cell two away (`ps_move` :158); jumped peg removed. Game over when no
  jump remains — Base quirk: any pegs left ⇒ DRAW ("no more valid moves"); WIN only at zero
  pegs (unreachable — one peg always remains).
- **bd** (20×20 Sokoban-like): arrow-key dozer (`bd_move_dozer` :252) pushes ONE boulder;
  win when every target holds a boulder (`bd_check_winner` :178). Levels load from
  `minigames/bulldozer/storage_<name>.txt` (`bd_load_level` :782; start level =
  `sv_minigames_bulldozer_startlevel` = "level1"), chain via each file's nextlevel field
  (`bd_next_match` :613); in-game EDITOR (place/fill/save, `bd_canedit` gate, `bd_save_level`
  :724); per-column brick state networks via dedicated `ENT_CLIENT_BD_CONTROLLER` (:114/:151).

## Port mapping

| Base | Port |
|---|---|
| registry + descriptors | `Minigames.RegisterAll` / abstract `Minigame` (Minigame.cs:271/364) — **+ an 8th original game, Snake** |
| tile math | `MinigameTiles` (Minigame.cs:99), `MinigameSession.NextTeam` (:255) |
| MSLE entity networking | replaced: whole-session snapshot per participating peer — `MinigameNetState.Encode/EncodeEnvelope` → `ServerNet.SendMinigameState` (:921, each net frame :490, dirty-driven) → `ClientNet.HandleMinigameState` (:569) |
| sv session lifecycle | `MinigameSessionManager` (Create/JoinByName/AddPlayer/RemovePlayer/End/EndAll), built at GameWorld.cs:929, ticked :2689, swept :4773 |
| `cmd minigame` | `Commands.CmdMinigame` (Commands.cs:1917), impulse hook :2100 |
| cl activation / minigame_cmd | `MinigameClient.OnEnvelope/SendCommand` (game/net/MinigameClient.cs), wired NetGame.cs:1591-1598 |
| menu + board input | `MinigameMenu` + `MinigameRenderer._GuiInput` (mouse) + `HudManager.DrivePongKeys` (:445, Pong keys only) |
| game rules | `TicTacToe/ConnectFour/Pong/NineMensMorris/PushPull/PegSolitaire/Bulldozer.cs` (all 7 exist and are registered) |
| game views | `MinigameRenderer` generic grid draw + bespoke `DrawPong`/`DrawSnake`; **no nmm/bd draw path** |
| turn prompt, allspecs, invite notification | NOT IMPLEMENTED |

Layer split: rules in `VortexArena.Common` (server-authoritative, host = GameWorld/Commands),
wire in `game/net`, view in `game/hud`. Tests: `tests/VortexArena.Tests/MinigameSessionTests.cs`
(lifecycle, commands, ttt/c4 end-to-end, pong sim, invite contract).

## Parity assessment

Full per-row verdicts live in `registry/minigames.yaml`. The headline picture:

- **Framework/lifecycle/commands: faithful and live.** Session create/join/part/end, team
  seating, the command surface (verbs, error strings, even the Base "exising" typo), playban
  gate, IS_REAL_CLIENT gate, EndAll-at-match-reset all match and are unit-tested. Intended
  divergences: C# virtuals replace the QC string-event dispatcher; a per-peer whole-session
  snapshot replaces MSLE per-entity deltas; the port registers an original 8th game (Snake).
- **The snapshot protocol only carries grid boards + Pong.** `NmmState`/`BdState` live in
  `Session.Extra`, which `MinigameNetState.Encode` never serializes, and `MinigameRenderer`
  has no nmm/bd branch ⇒ **NMM and Bulldozer are invisible to every client** (rules run
  server-side into the void).
- **Bulldozer is additionally unplayable**: no level is ever loaded (`LoadLevel` has zero live
  callers; the shipped `storage_level*.txt` files under `Base/data/xonotic-data.pk3dir` are
  never parsed; `sv_minigames_bulldozer_startlevel` doesn't exist), and `Bulldozer.Move`
  parses every token as a direction, so the menu's next/restart/edit/save verbs step the
  (nonexistent) dozer instead.
- **Peg Solitaire is unplayable from the live client**: `Move` needs `"<from> <to>"` but the
  board click path emits one tile per click and nothing accumulates the pair.
- **Invites are half-silent**: the `INFO_MINIGAME_INVITE` notification template exists
  (NotificationsList.cs:839) but `CmdMinigame` discards the session name (`out _`) and never
  sends it; there is also no menu Invite submenu. Target players never learn they were invited.
- **No turn prompt** (`minigame_prompt`/HUD notify) and **no spectator list**; menu keyboard
  navigation and per-game board key control are missing (Pong's paddle key-drive is the one
  wired exception — verified live via HudManager.DrivePongKeys → PongMoveSink → `minigame
  move <bits>`); no hover ghosts anywhere.
- **Dead knobs**: `sv_minigames_observer` (ObserverForcer/ClearForcer seams never assigned by
  the host — leave-restore also never runs) and all 8 `sv_minigames_pong_*` cvars (PongState
  hardcodes the shipped defaults).
- **Rules verdicts**: ttt/c4/pong faithful (tested); ttt diverges on rematch (no two-player
  handshake) and AI-game join (spectator-seat instead of refusal); nmm/pp/ps logic `unknown`
  (structurally 1:1 per headers, bodies not line-verified, no tests).

## Verification

- Port refs/line numbers re-verified against working tree 2026-07-02 (this audit); Base line
  numbers re-verified with grep against the pinned tree the same day.
- Live paths traced: `GameWorld.cs:929/2689/4773`, `Commands.cs:823→1917`, `ServerNet.cs:490→921`,
  `ClientNet.cs:553→569`, `NetGame.cs:1591-1598/5038`, `HudManager.cs:313→445`.
- Unit tests: `MinigameSessionTests.cs` (create/join/part/end/endall/bot-reject/playban,
  ttt + c4 end-to-end, pong throw+tick, invite error contract, command verbs).
- Negative findings (zero-caller greps): `Bulldozer.LoadLevel`, `ObserverForcer=`/`ClearForcer=`,
  `MINIGAME_INVITE` send sites, nmm/bd branches in MinigameNetState/MinigameRenderer, two-click
  accumulator for ps.
- Not verified (confidence medium/unknown rows): nmm/pp/ps Move-handler bodies; in-game visual
  check of board art resolution per skin.

## Open questions

- Should the port adopt QC's join-refusal for singleplayer ttt (currently seats a spectator)?
- nmm/pp/ps deep rules equivalence needs either tests or a line-by-line verify pass.
- Whether the panel-rect cvars (`hud_panel_minigameboard_pos/size`) should drive the port's
  board geometry (currently a fixed centered 0.7×viewport square) — geometry consumer is this
  unit's `minigames.cl.draw_helpers` row; the cvar shells belong to cl-hud.
