# Final vote input and caller verification

The reference is Xonotic data revision `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` in `/tmp/vortex-parity-base`. QuakeC was compiled from that checkout. The DarkPlaces client hosted Leave'em Behind. The native sessions used Darkzone, Godot Mono 4.6.3, and disposable loopback profiles.

## Operands and key bindings

The pinned `qcsrc/client/command/cl_cmd.qc` reads only `argv(1)`. The keywords in `qcsrc/lib/bool.qh` are case-insensitive. The fallback is `stof`, which calls C's `atof` in DarkPlaces `prvm_cmds.c`. A numeric prefix can therefore select yes even when text follows it. In normal command mode the missing argument selects no. Help is a separate command path.

| Input while an operator vote was open | Pinned Xonotic | Native |
| --- | --- | --- |
| Stock F1 → `vyes` | Accepted | Accepted |
| Stock F2 → `vno` | Rejected | Rejected |
| `cl_cmd handlevote` | Rejected | Rejected |
| `cl_cmd handlevote rubbish YES` | Rejected | Rejected |
| `cl_cmd handlevote 1tail NO` | Accepted | Accepted |
| `cl_cmd handlevote YES ignored` | Accepted | Accepted |
| `cl_cmd handlevote "" YES` | Rejected | Rejected |
| F1 after `alias vyes "cl_cmd handlevote no"` | Rejected | Rejected |
| F1 after restarting the same profile | Accepted | Accepted |
| F3 bound to `cl_cmd handlevote yes`, after the same restart | Accepted | Accepted |

Both engines displayed `cl_cmd help handlevote` usage. Their saved configs contained the F3 binding. DarkPlaces `Host_SaveConfig` writes bindings and archived cvars, and native `MenuState.SaveUserConfig` does the same. Runtime alias definitions are scoped to the process, so a fresh process rebuilds the stock `vyes` body from `commands.cfg`.

## Listen ownership and vote resolution

The pinned `qcsrc/server/command/sv_cmd.qc:GameCommand` dispatches common commands as the server. Client ballots arrive with their own player. `VoteCommand_no` rejects attempts to change an existing ballot while changes are disabled, before checking whether a caller's no stops their vote. `VoteCount` requires two yes ballots with two eligible players. On human disconnect, `qcsrc/server/client.qc:ClientDisconnect` clears `FL_CLIENT` before recounting against the remaining clients.

The native listen host joined with wire ID 1 and an independent peer joined with wire ID 2. The peer called vote 82. Its automatic yes and the listen player's no produced a 1:1 rejection. The listen operator then opened vote 83 through `sv_cmd`. The listen player's yes passed it when the peer disconnected without casting a ballot. The same peer rejoined under wire ID 3 and called vote 84. The host's yes supplied the second vote and accepted it.

`NetGame.LocalServerPlayer` now uses the net ID from the current accepted local sign-on. Before sign-on or during link loss, there is no local player. `ClientNet.Reconnect` clears the old ID before the next accept. The live run exercised the independent peer's reconnect. The in-process reconnect method has no current interactive caller, so its reset was checked in source.

## Manual nickname prompt

The native client started with `cl_allow_uid2name -1` and joined without an automatic nickname dialog. It voted with F1 and F2 before any nickname answer. During a later poll, it opened `menu_cmd directmenu Uid2Name` explicitly. F1 followed the custom `vyes` alias to answer no. The nickname setting became 0, UID tracking read back as 1, and the server poll was still pending. The F3 ballot then resolved the poll. The nickname choice read 0 after the same profile restarted.

`DialogFirstRun` provides Yes, No, and Undecided. `DialogMultiplayerProfile` exposes the nickname checkbox under Statistics with its tracking dependency. In a separate native menu probe, clicking No in First Run set `cl_allow_uid2name` to 0. Reopening the same profile and checking the nickname box set it to 1. Tracking and ranking both read 1 after each click. The pinned automatic prompt is in `qcsrc/client/hud/panel/vote.qc`.

To repeat the menu probe, start with a disposable profile and `cl_allow_uidtracking 1`. Launch with `--menu-screen firstrun`, click No, and query `cl_allow_uid2name` in the console. Then quit normally, relaunch with `--menu-screen multiplayer:Profile`, and check the nickname box. Query `cl_allow_uid2name`, `cl_allow_uidtracking`, and `cl_allow_uidranking` after the click.

## Repeat

Build `VortexArena.csproj` and run [verify-vote-input.sh](verify-vote-input.sh) from the repository root. The fixture starts Xvfb, compiles the existing loopback shim, and prints the directory containing full logs and profiles.

```sh
bash planning/parity/hud-commands/verify-vote-input.sh "$GODOT" 28965
bash planning/parity/hud-commands/verify-vote-input.sh "$GODOT" 28965 \
  "$DARKPLACES" "$STOCK_BASEDIR" "$STOCK_LIBDIR"
```

The stock basedir needs a mounted `data/zz-xonotic-data.pk3dir` at the pinned checkout, its compiled client, menu and server programs in a later mount, and the map and media packages for `leave_em_behind`. Set `VOTE_CASE=native-remote`, `VOTE_CASE=native-listen`, or `VOTE_CASE=stock` to run a single session group. The stock restart uses a separate engine session ID and a second port with the same profile and saved config.

The full xUnit suite passed 6,284 tests. The vote regressions cover the operator caller, tie, nonvoter disconnect, and locked-ballot cases.
