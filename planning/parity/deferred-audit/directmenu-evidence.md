# Named menu arguments and close-to-game focus

Rows 18, 62–63 and 129–132 were traced at Xonotic data pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`.

This record describes the command-routing pass. The [automatic native Welcome follow-up](welcome-live-evidence.md) records the subsequent sign-on delivery and map-change fixture.

| Rows | Pinned caller and behavior | Native coverage and remaining work |
|---|---|---|
| 18, 132 | `menu/command/menu_cmd.qc:93–147` selects a named entity and copies argv 2 onward into its `readInputArgs` buffer. Welcome is the only reader at this pin. `server/scores.qc:229` calls `SendWelcomeMessage` in `server/client.qc:1077–1132`. `client/main.qc:1380–1398,1455–1487` issues `CAMPAIGN` or `HOSTNAME … WELCOME …`. It uses `closemenu Welcome` to seed data during intermission, demo playback or with `cl_welcome 0`. `menu/menu.qc:775` supplies disconnect `RESET`. | Console and menu buttons preserve token boundaries into the named route. Welcome stores the last valid payload for the session, can seed it without opening, substitutes the mounted campaign description, and clears data on reset or teardown. The native server still uses per-client chat for join text. Automatic native delivery, `cl_welcome`, intermission and old-server show-info behavior remain open. Stock demo restrictions are wired at the native route and remain untested in playback. |
| 63 | `commands.cfg:107` names GameMenu. Its Input button calls `menu_cmd directmenu inputsettings` at `dialog_gamemenu.qc:55`. `menu/menu.qc:1081–1096` walks to the owning tab root for hide-on-close. | The native GameMenu alias reaches the same pause screen as Escape. The Input button opens Settings on its Input tab. Escape closes the owning Settings screen and returns to gameplay. |
| 129 | `binds-xonotic.cfg:60` runs `team_selection_show`; `commands.cfg:143–147` selects either TeamSelect or the scoreboard selector. `server/client.qc:1194–1198` publishes `_teams_available`, read by `dialog_teamselect.qc:15–24`. | F5 opened TeamSelect with seeded bits. Native join-time bitmask production, scoreboard selection and team/spectator transitions remain unverified. |
| 62, 130 | `mainwindow.qc:59–72` checks TermsOfService before FirstRun. `dialog_firstrun.qc:11–33` gates on the default player name and runs language/save/restart. `dialog_termsofservice.qc:11–17,26–66` loads text, compares the fetched version and returns to the First Run check after acceptance. | FirstRun and TermsOfService have named factories. Automatic startup, language persistence through that startup sequence, and terms text/version/acceptance policy remain open. |
| 131 | `client/hud/panel/vote.qc:17–25` opens Uid2Name when the local answer is undecided and rankings or server player stats are enabled. | The player chose First Run, Profile and manual in-match access for native local rankings. The [connected-client recording](../hud-commands/vote-input.txt) shows the named dialog consuming a nickname answer during a server vote. ADR-0019 §8 defines the legacy identity boundary and ADR-0011 retains the separate native protocol. |

## Local runtime proof

Both runs used disposable profiles, Xvfb at 960×600, and a harness that rejected non-loopback socket destinations and disabled DNS lookups. The traced native sockets reached only `::ffff:127.0.0.1`; the stock connection used the engine's internal loopback. The host restart cleared the temporary captures, scripts and logs after inspection. The observations below were recorded from the completed runs.

- **Stock DarkPlaces and pinned MENUQC/CSQC/SVQC.** The prior local build log identified data pin `863cd3e84`; `menu.dat` SHA-256 was `4d502424af24f90c36c8a17a034ded536571a0d353a4192d8b332e8cc8b2d771`. A local Boil match with `cl_welcome 1`, a green `Local Parity Server` hostname and a yellow two-line `sv_motd` logged `Connection established to local:1` and `ParityFixture connected`. The automatic Welcome displayed that hostname, map, mode and both MOTD lines. A direct console command with the corresponding quoted arguments displayed the same title and just the supplied MOTD.
- **Native Godot 4.6.3 Debug build.** A Boil listen match self-connected at `127.0.0.1:27979` and logged a successful client handshake. The native fixture then supplied the following through the in-game console:

  ```text
  menu_cmd directmenu Welcome HOSTNAME "^2Local Parity Server" WELCOME "^3Fixture MOTD; keep this text\nSecond line: \"quoted\" value."
  menu_cmd closemenu Welcome HOSTNAME "Stored Local Host" WELCOME "Stored without opening\nSecond line."
  menu_showwelcomedialog
  menu_cmd directmenu Welcome HOSTNAME
  menu_cmd closemenu Profile
  menu_cmd directmenu Welcome CAMPAIGN 1 "Level 1: _LEVEL_DESC"
  menu_cmd directmenu Welcome CAMPAIGN nope "_LEVEL_DESC"
  menu_cmd directmenu Welcome CAMPAIGN 999 "_LEVEL_DESC"
  menu_cmd directmenu Welcome RESET
  menu_showgamemenudialog
  set _teams_available 3
  ```

- The first command rendered the green title, yellow body, literal semicolon and quoted text on the second line. Escape returned to the HUD. The preload command left gameplay visible. The Welcome alias opened the stored text. The missing hostname value logged a malformed-argument warning and a close for Profile left Welcome open. An outside click dismissed Welcome. Campaign 1 showed the mounted Xonotic Campaign title and Boil description. The nonnumeric level logged the malformed-argument warning, the out-of-range integer displayed the level error, and RESET returned to the HUD.
- The GameMenu alias opened the pause screen. Clicking **Input** highlighted the Settings Input tab, and Escape returned to the match HUD. F5 with the seeded mask enabled red and blue and disabled yellow and pink. Enter on Welcome dismissed the panel and returned to gameplay. The fixture had already joined the match, so Enter did not prove a spectator-to-player transition. The stock and native menu backdrops and typography differed.

The Debug build completed with zero errors. The focused configuration, menu, HUD editor and campaign run passed 121 tests with .NET major roll-forward. The frozen audit verifier passed 176 ordered records and 4,330 lines at the pin. The native fixture logged an audio playback virtual-method error during shutdown after the checks.
