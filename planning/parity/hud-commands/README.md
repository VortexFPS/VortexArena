# HUD commands on Boil

A native player entered these commands in the console of a running Boil listen match. The comparison uses Xonotic data revision `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. In the pinned `client/command/cl_cmd.qc`, `hud configure` toggles the editor, `hud save` takes the first filename argument, and `localprint` sends its first message argument to the local centerprint panel. The standard message uses the duration set by `hud_panel_centerprint_time`.

| Player action | Observed result |
|---|---|
| Run `cl_cmd hud configure` twice | The editor turned on and then off. The live cvar read 1 and then 0. [Editor frame](boil-editor.webp) |
| Set the grid width to 0.123 and run `cl_cmd hud save "parity probe"` | The exporter received both words as one argument. Its existing filename sanitizer produced `hud_luma_parityprobe.cfg`. The file contained the changed width. [Console readings and export hash](boil-console.txt) |
| Run `cl_cmd localprint "^2Map vote; starts soon" ignored` | The player saw the green message **Map vote; starts soon**, including the semicolon. The text stopped after the first message argument. [Gameplay frame](boil-localprint.webp) |
| Schedule another configure command and disconnect before it runs | A later console query still returned 0 at the menu. The freed editor could not enable configuration for the next match. [Console readings](boil-console.txt) |
| Start Boil again in the same process and invoke the command from a key binding | After the second accepted handshake, the new HUD turned configuration on. The bound sequence read 1 and toggled it back off. [Second-match readings](boil-console.txt) |

The window ran at 960×600 with Godot Mono 4.6.3, Xvfb, software rendering, Dummy audio and a fresh profile. The original command behavior was traced in source. The frames show native output. The run did not measure font metrics, exact fade duration or editor keyboard shortcuts. The editor frame comes from the first successful toggle run. The console extract and localprint frame come from the final repeat.

To repeat from the repository root, build `VortexArena.csproj`, set `GODOT` to the Godot Mono executable, then run the following command. It allocates a fresh profile and server port and prints the output directory.

```sh
xvfb-run -a -s '-screen 0 960x600x24' bash planning/parity/hud-commands/configure-save.sh
```

## Answering a server vote

The pinned client routes `vyes` and `vno` through `LocalCommand_handlevote` in `client/command/cl_cmd.qc`. An open nickname question consumes its own answer. Otherwise the client sends the server a yes or no ballot. On a dedicated host, the original sets an automatic yes ballot only when a player calls the vote. Native previously attributed an operator’s vote to the first remote player.

A separate native client on Darkzone accepted a dedicated operator’s poll with F1 and rejected the next poll with F2. During a third poll, the client opened the nickname dialog and pressed F2. The local nickname setting changed to zero and the server still reported an active poll. The client then accepted it with `cl_cmd handlevote YES`. This run used disposable profiles, disabled UID tracking and manually opened the nickname dialog. First Run and Profile also offer the preference. The approved local rankings policy retains those two controls and keeps the in-match prompt manual. The connected-client transcript below exercises that policy.

[Nickname prompt](name-prompt.webp) · [Client console after the third poll](vote-result.webp) · [Before and after transcript](vote-input.txt)

Build the Debug host, set `GODOT` to the Godot Mono executable and run the fixture from the repository root.

```bash
xvfb-run -a -s '-screen 0 1100x750x24' bash planning/parity/hud-commands/vote-input.sh
```
