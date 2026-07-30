# Duel — parity spec

**Base refs:** `common/gametypes/gametype/duel/{duel.qc,duel.qh,sv_duel.qc,sv_duel.qh}` · `common/gametypes/gametype/deathmatch/*` (parent behavior) · `server/client.qc:GetPlayerLimit/nJoinAllowed` · `client/announcer.qc:Announcer_Duel` · `client/hud/panel/centerprint.qc:centerprint_SetDuelTitle` · `client/csqcmodel_hooks.qc` (forced colors)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Duel.cs` · `src/VortexArena.Server/GameWorld.cs` (activate/drive) · `game/net/NetGame.cs` + `game/client/ClientWorld.cs` + `src/VortexArena.Engine/Simulation/CsqcModelAppearance.cs` (forced colors) · `game/hud/CenterPrintPanel.cs` (title)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Duel is a 1-versus-1 arena gametype: mechanically it is Deathmatch hard-limited to exactly two players,
with the powerup item class removed by default and a couple of 1v1-specific presentation touches. It
activates when `g_duel` is the selected gametype. In Base it is an extremely thin definition: a `CLASS(Duel,
Gametype)` registration plus three server mutator hooks (`GetPlayerLimit`, `Scores_CountFragsRemaining`,
`FilterItemDefinition`) and two map-support predicates. All actual fighting/scoring/respawn behavior is the
shared Deathmatch + damage/scores/client machinery. The interesting parity surface is therefore: (1) the
hard 2-player cap, (2) the default-off powerup filter, (3) the DM frag matrix it inherits, (4) the 1v1
presentation (the "P1 vs P2" centerprint title and the forced enemy-player-color path), and (5) map-support
gating.

## Base algorithm (authoritative)

### Gametype registration & identity (`duel.qh:INIT(Duel)`, `REGISTER_GAMETYPE(DUEL)`)
- **Trigger:** gametype registry init (shared). `gametype_init(this, "Duel", "duel", "g_duel",
  GAMETYPE_FLAG_USEPOINTS | GAMETYPE_FLAG_1V1, "", "timelimit=10 pointlimit=0 leadlimit=0", …)`.
- **Constants:** netname `duel`; cvar `g_duel`; flags `USEPOINTS` (frag-based scoring) + `1V1`
  (`GAMETYPE_FLAG_1V1 = BIT(6)`, sets `gametype.m_1v1 = true`). **Default match limits: `timelimit=10`
  (minutes), `pointlimit=0` (no frag limit — duels are decided by the time limit unless an admin sets a
  fraglimit), `leadlimit=0`.** Note these differ from DM (`timelimit=15 pointlimit=30 leadlimit=0`).
- **Inheritance:** `CLASS(Duel, Gametype)` includes deathmatch.qh; duel reuses the entire DM frag/obituary
  path. There is no separate duel scoring code.

### Hard player limit = 2 (`sv_duel.qc:MUTATOR_HOOKFUNCTION(duel, GetPlayerLimit)` + `server/client.qc:GetPlayerLimit`)
- **Trigger:** authority. The hook sets `M_ARGV(0,int) = 2`. Additionally `GetPlayerLimit()` short-circuits
  `if(g_duel) return 2;` BEFORE the mutator hook even runs (workaround comment: the hook can't fire before
  the gametype is loaded when switching via the vote screen).
- **Algorithm:** `nJoinAllowed(this)` computes `free_slots = max(0, player_limit - currentlyPlaying)`;
  when 2 players are already in-game a third client cannot `+jump`-join and gets
  `CENTER_JOIN_PREVENT`. The 3rd+ clients stay spectators/queued.
- **Constants:** player_limit = 2 (hardcoded). Used by join gating, bot fill (`bot.qc:654`), and the
  qcstatus free-slot advert (`client.qc:1105`).

### Powerup filter (`sv_duel.qc:MUTATOR_HOOKFUNCTION(duel, FilterItemDefinition)`)
- **Trigger:** authority, fired per item definition during map item spawn.
- **Algorithm:** `if(definition.instanceOfPowerup) return !autocvar_g_duel_with_powerups;` — i.e. when the
  item is a powerup (Strength/Shield/etc.), filter it OUT unless `g_duel_with_powerups` is set. Non-powerup
  items (mega health, mega armor, weapons, ammo) are untouched.
- **Constants:** `g_duel_with_powerups` default **0** (powerups removed by default). `g_duel_not_dm_maps`
  default **0**.

### Frags-remaining announcement (`sv_duel.qc:MUTATOR_HOOKFUNCTION(duel, Scores_CountFragsRemaining)`)
- **Trigger:** authority. Returns `true` — duel (like DM) announces "N frags remain" when a fraglimit is in
  force. Inherited semantics from DM (`sv_deathmatch.qc` returns the same).

### Map-support gating (`duel.qh:m_isAlwaysSupported`, `m_isForcedSupported`)
- `m_isAlwaysSupported(spawnpoints, diameter)` → `return (diameter < 3250)`: duel is auto-supported only on
  maps whose bounding diameter is under 3250 units (small enough for a 1v1).
- `m_isForcedSupported()` → unless `g_duel_not_dm_maps`, any map that supports DM but not explicitly duel is
  forced to also list duel (with a CSQC console warning). Map-pool/menu concern.

### Respawn timing (config: `g_duel_respawn_*`, calc: `server/client.qc:calculate_player_respawn_time`)
- duel-specific cvars are all **0**: `g_duel_respawn_delay_small/large/max 0`, `*_count 0`,
  `g_duel_respawn_waves 0`, `g_duel_weapon_stay 0`.
- **Crucial:** `GAMETYPE_DEFAULTED_SETTING(respawn_delay_small)` (client.qh:347) reads
  `g_duel_respawn_delay_small`; when it is `0` it FALLS BACK to `max(0, autocvar_g_respawn_delay_small)`.
  The generic `g_respawn_delay_small = 2` (xonotic-server.cfg:264). **So duel's EFFECTIVE respawn delay is
  2 s, not instant** (the per-gametype 0 just means "use the global default"). FFA branch with
  `sdelay_small_count==0` and not-independent → required enemy count 2; with both duelists present, the
  small (2 s) delay applies.

### Presentation: duel title (`client/announcer.qc:Announcer_Duel` → `centerprint.qc:centerprint_SetDuelTitle`)
- **Trigger:** presentation (CSQC), driven from the countdown path when `gametype.m_1v1` is true
  (announcer.qc:145,160). Runs whenever the in-game players change.
- **Algorithm:** `Announcer_Duel()` sorts players, grabs the two non-spectator duelists' names
  (`entcs_GetName`, "???" if absent), and on change calls `centerprint_SetDuelTitle(p1, p2)` which shortens
  each name to `hud_panel_scoreboard_namesize` and stores left/right title halves. The centerprint panel
  then draws "name1 vs name2" as the bold title above the centerprint area.

### Presentation: forced player colors in 1v1 (`client/csqcmodel_hooks.qc:244,309`)
- **Trigger:** presentation, per remote player model color resolution.
- **Algorithm:** when `gametype.m_1v1`: enable `forceplayercolors_enabled` for `cl_forceplayercolors`
  modes 1/2/3/5 (so the opponent can be recolored to a fixed enemy color). Also `m_1v1` SUPPRESSES
  `cl_forceuniqueplayercolors` (the unique-per-enemy coloring) since there is only one enemy.

## Port mapping

| Base feature | Port symbol | Live? |
|---|---|---|
| Gametype identity (`Duel`, `duel`, flags, `pointlimit=0`/`timelimit=10`) | `Duel.cs` ctor + `OnInit`; resolved by `GameWorld.ResolveGameType`/`GameTypes.ByName("duel")` | live (selectable gametype) |
| Hard player limit = 2 (join gate + bot fill + status advert) | `Duel.PlayerLimit = 2` (const) | **DEAD** — referenced only inside `Duel.cs`; `ClientManager.JoinAllowed` (ClientManager.cs:256) has no free-slot/maxplayers gate at all, AND `BotPopulation.TargetBotCount` reads `g_maxplayers` (BotPopulation.cs:240) with no duel→2 override, so bot fill is uncapped too |
| Powerup filter (`FilterItemDefinition`) | `Duel.FilterItem(GameItemDef)` / `Duel.WithPowerups` | **DEAD** — standalone method, no caller; the live filter chain is `MutatorHooks.FilterItemDefinition` (`StartItem.cs:90`) which Duel never registers into (Mayhem/TeamMayhem do) |
| Frags-remaining announcement | (inherited; no duel-specific port code) | n/a — generic scoreboard/announcer path |
| DM frag matrix (enemy +1, suicide/world −1) | `Duel.OnDeath` (mirrors `Deathmatch.OnDeath`) | live — `Duel.Activate()` subscribes `Combat.Death` (GameWorld.cs:1364) |
| End-of-match frag-limit latch | `Duel.MatchEnded` / `FragLimit` / `UpdateLeaderAndCheckLimit` / `RecomputeLeader` | live — `RecomputeLeader` is NOT driven for Duel in `DriveGametypeFrame` (only the incremental on-death path runs); winner read at GameWorld.cs:2055 |
| Respawn delay (effective 2 s) | `Duel.ScheduleRespawn` / `RespawnDelay` (reads generic `g_respawn_delay_small`, default 2) | live — does not read the `g_duel_respawn_delay_*` override layer, but those are 0 so the effective value matches at defaults |
| Map-support gating (diameter < 3250, DM→duel forcing) | NOT IMPLEMENTED | n/a (map-pool/menu concern; deferred) |
| Duel "P1 vs P2" centerprint title | `CenterPrintPanel.SetDuelTitle` exists | **DEAD** — no port equivalent of `Announcer_Duel`; nothing calls `SetDuelTitle` |
| Forced player colors in 1v1 | `CsqcModelAppearance.ForcePlayerColorsEnabled(...is1v1...)`; `Is1v1` set from netname `=="duel"` (NetGame.cs:3921/3932); applied in `ClientWorld` FORCECOLORS path | live — `is1v1` flows into the live color-resolution path |

## Parity assessment

- **logic** — The DM-derived frag/leader/match-end logic is faithfully reproduced (`Duel.OnDeath`
  mirrors `Deathmatch.OnDeath` exactly). The two duel-SPECIFIC rules are **logically broken at runtime**:
  the 2-player cap is not enforced (a 3rd player can join), and the powerup filter never runs. The
  `Scores_CountFragsRemaining=true` semantics are handled by the generic scoreboard, not a duel hook.
- **values** — Match-limit defaults: the port reads `fraglimit`/`g_duellimit` with a default of 0 (= no
  frag limit), which matches `pointlimit=0`. `timelimit=10` / `leadlimit=0` are gametype-init metadata
  that the port does not seed per-gametype (timelimit handling is generic). Respawn delay coincidentally
  matches (2 s effective) because the port reads the same generic cvar; the per-gametype override layer
  (`g_duel_respawn_delay_*`) is not modeled but is 0 anyway. `g_duel_with_powerups`/`g_duel_not_dm_maps`
  cvars are seeded in the port cfg but only the former is read (and that read is dead).
- **timing** — Respawn cadence matches at defaults (2 s). No frame-driven duel resolution
  (`RecomputeLeader` not called in `DriveGametypeFrame` for Duel) — relies on the on-death incremental
  path, which is correct for a 2-player FFA but means a tie/limit recheck only happens on a kill.
- **presentation** — Forced-color 1v1 path is live and faithful. The "P1 vs P2" duel title is **missing on
  the live path** (panel support exists, driver does not) — a player in a duel will not see the
  characteristic versus title during the countdown.
- **audio** — No duel-specific audio (the prepare/countdown announcer cues are generic and inherited).

### Gaps (observable)
1. **No 2-player cap (both paths):** a third (or more) client can `+jump`-join a duel match and play
   simultaneously — `ClientManager.JoinAllowed` has no free-slot gate. Bot-fill is also uncapped: Base
   `GetPlayerLimit()` short-circuits to 2 for `g_duel` and feeds `bot_fixcount`, but the port's
   `BotPopulation.TargetBotCount` reads `g_maxplayers` directly. Duel is not actually 1v1 in the port.
   (Worst gap: changes the gametype's fundamental rule.)
2. **Powerups spawn in duel:** at default `g_duel_with_powerups 0`, Strength/Shield powerups still spawn
   on the map (filter never runs), changing item-control balance that duel is built around.
3. **No duel title:** the "playerA vs playerB" centerprint title is never shown.
4. **`g_duel_respawn_delay_*` / `g_duel_weapon_stay` override layer not modeled** (cosmetic at defaults
   since all are 0 → fall back to the matching generic value; only matters if an admin sets a duel-specific
   value).
5. **Map-support gating absent** (diameter<3250, DM→duel forcing) — a map-pool/menu concern, low priority.

### Liveness
- Live callers confirmed: `Duel.Activate()` (GameWorld.cs:1364) subscribes the death handler; winner read
  (GameWorld.cs:2055); `MatchEnded` gate (GameWorld.cs:280); forced-color `is1v1` from netname.
- Dead despite existing: `Duel.PlayerLimit`, `Duel.FilterItem`/`WithPowerups`, `CenterPrintPanel.SetDuelTitle`.

### Intended divergences
None identified. The dead 2-player cap and dead powerup filter are unfinished wiring, not deliberate
changes (the code is present and clearly intends Base behavior).

## Verification
- Base values/algorithm: read `duel.qc/.qh`, `sv_duel.qc/.qh`, `deathmatch.*`, `mapinfo.qh` flags,
  `server/client.qc:GetPlayerLimit/nJoinAllowed`, `server/client.qh:GAMETYPE_DEFAULTED_SETTING`,
  `client/announcer.qc`, `client/hud/panel/centerprint.qc`, `client/csqcmodel_hooks.qc`,
  `xonotic-server.cfg` (respawn defaults), `gametypes-server.cfg` (g_duel_* defaults). All confirmed.
- Port liveness: grepped every reference to `PlayerLimit`, `FilterItem`, `WithPowerups`, `SetDuelTitle`,
  `Is1v1`; traced `GameWorld.ActivateGameType`/`DriveGametypeFrame`/`ResolveGameType` and
  `StartItem.cs:90` (the live `FilterItemDefinition.Call`). `ClientManager.JoinAllowed` confirmed to defer
  the maxplayers/free-slot gate (no 2-cap). Not run in-engine — static read only.

## Open questions
- Does any other server path (queue/balance/admin) enforce a 2-player duel cap that this audit missed? The
  `JoinAllowed` comment says the maxplayers gate is "deferred", but a runtime check on a 3-client duel
  server would confirm whether a third player actually spawns.
- Is the duel title intended to be driven from a future CSQC announcer port, or was `SetDuelTitle` added
  speculatively? (Panel support is complete; only the driver is missing.)
