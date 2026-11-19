# LMS restart timing and Survival match scoring

Reviewed on 2026-11-19 against Xonotic commit `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` in `/tmp/vortex-parity-base`.

## Last Man Standing

`qcsrc/server/world.qc:2197,2219` reads the configured `sv_ready_restart_after_countdown` into a level-local flag. The LMS hook at `qcsrc/common/gametypes/gametype/lms/sv_lms.qc:245–251` clears that flag. The mode still accepts a ready restart. The flag controls reset scheduling in `qcsrc/server/command/vote.qc:480–521` and shifts the spawn shield and health, armor and regeneration pause timers past the countdown in `qcsrc/server/client.qc:665–671`.

In the port, each boot resolves the effective rule for its world. The warmup controller schedules the reset and the normal client spawn path uses that same rule. A restart publishes the new start time before reseeding lives and spawning players. The setting stays available to the next mode through the shared cvar store. LMS has a single continuous match and still delegates mid-match respawns to its existing life-based rules.

## Remaining-frags announcer

The pin enables the hook in Deathmatch, TDM, Clan Arena, Freeze Tag, Duel, Race, Keepaway, Team Keepaway and Survival. Race permits the cue in lap racing and skips it in qualifying. Keepaway and Team Keepaway permit it with timed scoring off. The two Mayhem hooks are commented out at `sv_mayhem.qc:50–60` and `sv_tmayhem.qc:48–58`. Their high score increments could reach or skip a cue threshold in one hit.

`sv_survival.qc:54–72,236–246,431–435` banks round points into the primary score, uses hunts as the secondary key and opts in to the cue. The round handler settles individual rounds. The regular match check in `world.qc:1819–1825` compares scores and resolves the match limit. The equivalent native path now evaluates Survival's standings after round scoring and sends the shared cue. It compares the primary and secondary keys for ties, keeps scores across round resets and clears them on a match restart. Fresh matches also discard old roles and banked kills. This matters when the registry reuses the Survival object on the next map.

The forced one-frag branch in `world.qc:1593–1596` is behind the expiry exit at `world.qc:1812–1816`. That exit precedes both the default score check and the Race mode dispatch. The pinned flow can end sudden death on a decisive score or the timer expiring. Expiry itself emits no forced one-frag cue. Existing mode-owned callers retain their individual timing. For example, the DM poll still reads its raw limit during warmup, so a configured limit of 1–3 can produce a premature cue. Survival uses the warmup and countdown limit mask from the pinned score check.

## Verification

`LmsSurvivalMatchFlowTests` uses real server ticks, client join and ready commands, combat deaths and round/map resets. It checks LMS respawn with lives left, the spawn timer boundary, both paths into the restart countdown and a subsequent DM world on the same cvar store. It checks Survival’s 3/2/1 notification dispatches, score accumulation, score and combined lead limits, both restart timings, map recreation and a tied sudden-death expiry. The focused run passed 105 tests with `DOTNET_ROLL_FORWARD=Major` on the installed .NET host. The Godot Debug build succeeded. The announcer assertions record the broadcast at the server notification sink. Client audio playback was outside this run.

The Godot 4.6.3 Mono Debug dedicated host ran Darkzone in two disposable user directories with two bots, `bot_join_empty 1` and bot firing disabled. The LMS run used four starting lives and `sv_ready_restart_after_countdown 1`. A scripted bot client issued `kill`. The score dump one second after the console `restart` showed the full life count restored within the ten-second countdown. A map reload after `gametype dm` retained the configured cvar.

```text
:status:lms_darkzone:3
:labels:player:rank<!!,lives!,score,kills,deaths<,suicides<
:player:see-labels:0,4,0,0,0,0:1:-1:[BOT]Toxic
Command 'cc kill' sent to bot [BOT]Toxic
:kill:suicide:1:1:type=kill:items=4
:status:lms_darkzone:6
:player:see-labels:0,3,0,0,1,1:4:-1:[BOT]Toxic
match restarted
:status:lms_darkzone:7
:player:see-labels:0,4,0,0,0,0:5:-1:[BOT]Toxic
"sv_ready_restart_after_countdown" is "1"
gametype will change to dm on the next map
changing to darkzone
"sv_ready_restart_after_countdown" is "1"
gametype: dm  time: 2.5
```

The Survival run used `fraglimit 1`, `g_survival_warmup 1`, `g_survival_round_timelimit 2`, `g_survival_round_enddelay -1`, and `g_survival_reward_survival 1`. Both bots started at zero points. The live round timeout gave the surviving prey one point, ended the match and printed the final standings.

```text
gametype: surv  time: 3.6
players: 2 (2 bots)
:status:surv_darkzone:4
:labels:player:score!!,hunts!,survivals,kills,deaths<,suicides<
:player:see-labels:0,0,0,0,0,0:1:-1:[BOT]Toxic
:player:see-labels:0,0,0,0,0,0:1:-1:[BOT]Discovery
:scores:surv_darkzone:6
:labels:player:score!!,hunts!,survivals,kills,deaths<,suicides<
:player:see-labels:0,1,0,0,0,0:4:-1:1:[BOT]Toxic
:player:see-labels:1,0,1,0,0,0:4:-1:2:[BOT]Discovery
:gameover
** intermission **
```
