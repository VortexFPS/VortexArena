# Clan Arena participants and spectators

Source pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` at `/tmp/vortex-parity-base`.

## Source and implementation

- `qcsrc/common/ent_cs.qh:71–89` identifies a pure spectator from an absent score or spectator frags. A nonsolid participant with out-of-game frags still belongs in a team score row. The native server uses observer status plus the existing `FragsOutOfGame` sentinel for this distinction.
- `cl_clanarena.qc:14` and `qcsrc/client/hud/panel/infomessages.qc:136–142` suppress the jump-to-join hint for the local participating observer. The native client uses the local observer state and its score row. Camera and help lines keep their normal conditions and the scoreboard binding has no role in this decision.
- `sv_clanarena.qc:17–43,243–330,370–419` counts only living player entities for the alive totals. Dead round participants stay on their team and transition silently to observer after two simulation seconds unless a round reset happens first. An eligible mid-round join stays an out-of-game observer until it can spawn. Team locks, play bans and the slot cap still gate admission. Advancing the round respawns participants and eligible bots. Pure spectators and people who opted out stay out.
- `sv_clanarena.qc:551–590,612–628` keeps pure spectators free to follow either team. Participating observers use teammate-only follow at the default and can withdraw with the plain spectate command. Camera-only spectate commands preserve team participation. The native server applies the configured follow restriction on the next reset.
- The native scoreboard row and eliminated-ID list reuse the existing network format. Pending participants move back to the pure spectator group at match end. Round-start team snapshots restore participant team membership before final score reporting.

## Automated verification

The Debug Godot host built successfully. A focused run on the installed .NET host with `DOTNET_ROLL_FORWARD=Major` passed 36 tests across Clan Arena participation, gametype status, spectator rules, kill countdown, and CTS spawn handling. A temporary MSBuild target excluded the concurrent `MapInfoGenerationTests.cs` from this run because two assertions referenced the private `GameWorld.MatchHasEnded` method as a property.

The Clan Arena integration test exercised actual client connect, join and spectate commands, the observer jump path, damage, server frames and the round reset. It covered cap, team lock and play-ban refusal, queued withdrawal and disconnect cleanup, a blocked mid-round respawn, the two-second death-to-observer transition, participant and pure-spectator follow behavior, survivor counts, round winner and next-round return. It also checked that a pure spectator receives no round played award.

The codec test captured status for a player and for both observer roles. It checked stream alignment, four team alive totals, and membership of the eliminated-ID list. The other gametype status tests still passed. The winner check also verifies that zero living teams resolves to a tie, as in the pinned `Team_GetWinnerAliveTeam` helper.

## Native Darkzone run

A Godot 4.6.3 Mono Debug client in Xvfb hosted Darkzone over local ENet with three bots and a disposable `VORTEX_USERDIR`. Bot firing was disabled to hold a stable live round. The listen host had joined automatically. I withdrew with `sv_cmd spectate`, then pressed the actual Jump binding while still observing.

| State | Observed HUD and scoreboard |
|---|---|
| Pure observer | The HUD showed “Observing”, the current observer-help line and “Press jump to join”. Holding Tab listed the client under Spectators and kept the join hint. [HUD](pure.jpg) · [Tab held](pure-scores.jpg) |
| Waiting participant | Jump showed the round-already-started notification. The HUD kept “Observing” and the rotating help line, and the scoreboard moved the client to the blue team table. The join hint stayed suppressed with Tab released and held. The alive totals remained red 2, blue 1. [HUD](queued.jpg) · [Tab held](queued-scores.jpg) · [Tab released](queued-after-scores.jpg) |

The native client run covered the loopback client and the two observer states. The delayed death transition, next-round re-entry and remote-style command authorization were exercised by the server integration tests.

## Remaining limits

The stock's separate winner-confirmation delay and reset delay are outside this change. The focused changes leave stock bot-fill participation accounting and the dedicated round-win announcer cue open. The final score hook's team membership change has source-level coverage. The stock restore announcements and winning-flag effects were outside this run.
